using System.Security.Cryptography;
using System.Text;
using RimLiaison.DevBridge;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Recovery;
using RimLiaison.Results;

namespace RimLiaison.Execution;

public sealed record ArtifactFreshnessTransactionRequest(
    string Project,
    string RepositoryRoot,
    IReadOnlyList<string> ChangedPaths,
    string SourceFingerprint,
    string? WorkflowId,
    string? TestRecipe = null,
    string? LeaseId = null);

public sealed record ArtifactFreshnessTransactionResult(
    bool Success,
    DevBridgeAdapterStatus Status,
    RimTestArtifactFreshness Freshness,
    IReadOnlyList<RimTestPrerequisiteRecovery>? RecoveryEvents = null,
    RimTestCleanupSummary? Cleanup = null);

public sealed class ArtifactFreshnessTransaction
{
    private readonly IDevBridgeModDevelopmentAdapter developmentAdapter;
    private readonly IDevBridgeLeaseAdapter? leaseAdapter;
    private readonly IDevBridgeFreshGenerationAdapter? readinessAdapter;
    private readonly IGitRepositoryStateProvider? repositoryStateProvider;

    public ArtifactFreshnessTransaction(
        IDevBridgeModDevelopmentAdapter developmentAdapter,
        IDevBridgeLeaseAdapter? leaseAdapter = null,
        IDevBridgeFreshGenerationAdapter? readinessAdapter = null,
        IGitRepositoryStateProvider? repositoryStateProvider = null)
    {
        this.developmentAdapter = developmentAdapter ??
            throw new ArgumentNullException(nameof(developmentAdapter));
        this.leaseAdapter = leaseAdapter;
        this.readinessAdapter = readinessAdapter;
        this.repositoryStateProvider = repositoryStateProvider;
    }

    public async Task<ArtifactFreshnessTransactionResult> PrepareAsync(
        ArtifactFreshnessTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Project))
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_DEVBRIDGE_PROJECT_MISSING",
                    "A build-relevant affected run requires the manifest DevBridge project alias."));
        }

        if (string.IsNullOrWhiteSpace(request.SourceFingerprint))
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_SOURCE_FINGERPRINT_UNAVAILABLE",
                    "The current worktree fingerprint could not be established."));
        }

        if (!WorktreeFingerprint.TryCompute(
                request.RepositoryRoot,
                request.ChangedPaths,
                out string initialSourceFingerprint,
                out string? initialFingerprintError) ||
            !string.Equals(
                initialSourceFingerprint,
                request.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                    initialFingerprintError ??
                        "The selected source inputs changed before the artifact transaction could start."));
        }

        WorktreeIntegritySnapshotResult initialSnapshot =
            await WorktreeIntegritySnapshot.CaptureAsync(
                    request.RepositoryRoot,
                    request.ChangedPaths,
                    repositoryStateProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!initialSnapshot.Success || initialSnapshot.Snapshot is null)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                    initialSnapshot.Error ??
                        "The initial worktree transaction snapshot could not be established."));
        }

        RimTestCleanupSummary? cleanup = null;
        DevBridgeModDevelopmentResult result;
        try
        {
            result = await developmentAdapter.RunAsync(
                    request.Project,
                    request.RepositoryRoot,
                    request.SourceFingerprint,
                    request.WorkflowId,
                    string.IsNullOrWhiteSpace(request.LeaseId)
                        ? null
                        : new DevBridgeModDevelopmentExecutionContext(request.LeaseId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"));
        }
        catch (Exception exception)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                Bound(exception.Message)));
        }

        List<RimTestPrerequisiteRecovery>? recoveryEvents = null;
        if (DevBridgeIdentityMismatchPolicy.IsIdentityMismatch(result.Status))
        {
            IdentityRecoveryResult identityRecovery =
                await RecoverIdentityMismatchAsync(
                        request,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
            recoveryEvents = identityRecovery.Events;
            result = identityRecovery.Result;
            if (!identityRecovery.CanContinue)
            {
                return Failure(
                    request,
                    result.Status,
                    freshness: RimTestArtifactFreshness.From(result, request.WorkflowId),
                    recoveryEvents: recoveryEvents);
            }
        }
        else if (RuntimeTransitionRecoveryClassifier.IsRecoverable(result.Status))
        {
            recoveryEvents = [];
            int? previousGeneration = result.Generation ?? result.Freshness?.Generation;
            recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                RuntimeTransitionRecoveryClassifier.Component,
                "recovering",
                1,
                result.Status.ErrorCode,
                RuntimeTransitionRecoveryClassifier.RecoverAction,
                request.WorkflowId,
                previousGeneration));
            RecordTransitionRecovery(
                AgentEventTypes.RetryStarted,
                "Waiting for DevBridge to settle a shared runtime transition.",
                request,
                result.Status,
                previousGeneration,
                RuntimeTransitionRecoveryClassifier.RecoverAction);

            if (readinessAdapter is null || string.IsNullOrWhiteSpace(request.TestRecipe))
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.RecoveryRequired.ToWireName(),
                    1,
                    result.Status.ErrorCode,
                    "establish-fresh-generation",
                    request.WorkflowId,
                    previousGeneration));
                return Failure(
                    request,
                    result.Status with
                    {
                        RecoveryState = PrerequisiteRecoveryState.RecoveryRequired,
                        RecoveryAttempts = 1,
                        RecoveryAction = "establish-fresh-generation"
                    },
                    recoveryEvents: recoveryEvents);
            }

            DevBridgeFreshGenerationResult recovery;
            try
            {
                recovery = await readinessAdapter.EnsureFreshGenerationAsync(
                        request.TestRecipe!,
                        previousGeneration,
                        request.WorkflowId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.Cancelled,
                        "RIMTEST_CANCELLED",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "cancel-shared-runtime-transition-recovery"),
                    recoveryEvents: recoveryEvents);
            }
            catch (Exception exception)
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.RecoveryFailed.ToWireName(),
                    1,
                    "DEVBRIDGE_READINESS_RECOVERY_FAILED",
                    "establish-fresh-generation",
                    request.WorkflowId,
                    previousGeneration));
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_READINESS_RECOVERY_FAILED",
                        Bound(exception.Message),
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "establish-fresh-generation"),
                    recoveryEvents: recoveryEvents);
            }

            if (!recovery.IsUsable)
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    recovery.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                        ? PrerequisiteRecoveryState.RecoveryFailed.ToWireName()
                        : PrerequisiteRecoveryState.Unavailable.ToWireName(),
                    1,
                    recovery.Status.ErrorCode ?? result.Status.ErrorCode,
                    "establish-fresh-generation",
                    request.WorkflowId,
                    recovery.Generation ?? previousGeneration));
                return Failure(
                    request,
                    result.Status with
                    {
                        ErrorCode = recovery.Status.ErrorCode ?? result.Status.ErrorCode,
                        Error = recovery.Status.Error ?? result.Status.Error,
                        RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts = 1,
                        RecoveryAction = "establish-fresh-generation"
                    },
                    recoveryEvents: recoveryEvents);
            }

            if (!WorktreeFingerprint.TryCompute(
                    request.RepositoryRoot,
                    request.ChangedPaths,
                    out string recoveredSourceFingerprint,
                    out string? recoveredFingerprintError) ||
                !string.Equals(
                    recoveredSourceFingerprint,
                    request.SourceFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.RecoveryFailed.ToWireName(),
                    1,
                    "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                    "revalidate-source-fingerprint",
                    request.WorkflowId,
                    recovery.Generation));
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                        recoveredFingerprintError ??
                            "The selected source inputs changed while the shared runtime was recovering.",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "revalidate-source-fingerprint"),
                    recoveryEvents: recoveryEvents);
            }

            try
            {
                result = await developmentAdapter.RunAsync(
                        request.Project,
                        request.RepositoryRoot,
                        request.SourceFingerprint,
                        request.WorkflowId,
                        executionContext: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.Cancelled,
                        "RIMTEST_CANCELLED",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "cancel-shared-runtime-transition-recovery"),
                    recoveryEvents: recoveryEvents);
            }
            catch (Exception exception)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                        Bound(exception.Message),
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: RuntimeTransitionRecoveryClassifier.RetryAction),
                    recoveryEvents: recoveryEvents);
            }

            int? rerunGeneration = result.Generation ?? result.Freshness?.Generation;
            if (result.Status.IsSuccess &&
                result.Success == true &&
                recovery.Generation is int recoveredGeneration &&
                (rerunGeneration is null || rerunGeneration.Value < recoveredGeneration))
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.RecoveryFailed.ToWireName(),
                    1,
                    "RIMTEST_ARTIFACT_GENERATION_MISMATCH",
                    "reject-stale-generation-evidence",
                    request.WorkflowId,
                    rerunGeneration));
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "RIMTEST_ARTIFACT_GENERATION_MISMATCH",
                        "The retried transaction did not prove evidence from the recovered generation.",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "reject-stale-generation-evidence"),
                    recoveryEvents: recoveryEvents);
            }

            bool transitionRecovered = !RuntimeTransitionRecoveryClassifier.IsRecoverable(result.Status);
            if (!transitionRecovered)
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.TransitionRecoveryExhausted.ToWireName(),
                    1,
                    result.Status.ErrorCode,
                    RuntimeTransitionRecoveryClassifier.ExhaustedAction,
                    request.WorkflowId,
                    result.Generation ?? result.Freshness?.Generation));
                RecordTransitionRecovery(
                    AgentEventTypes.RecoveryCompleted,
                    "Shared runtime transition recovery was exhausted; preserving the DevBridge error.",
                    request,
                    result.Status,
                    result.Generation ?? result.Freshness?.Generation,
                    RuntimeTransitionRecoveryClassifier.ExhaustedAction);
            }
            else
            {
                recoveryEvents.Add(new RimTestPrerequisiteRecovery(
                    RuntimeTransitionRecoveryClassifier.Component,
                    PrerequisiteRecoveryState.Recovered.ToWireName(),
                    1,
                    null,
                    RuntimeTransitionRecoveryClassifier.RetryAction,
                    request.WorkflowId,
                    recovery.Generation));
                RecordTransitionRecovery(
                    AgentEventTypes.RecoveryCompleted,
                    "Recovered shared runtime transition on a fresh DevBridge generation.",
                    request,
                    result.Status,
                    result.Generation ?? recovery.Generation,
                    RuntimeTransitionRecoveryClassifier.RetryAction,
                    recovered: true);
            }

            result = result with
            {
                Status = result.Status with
                {
                    RecoveryState = transitionRecovered && result.Status.IsSuccess
                        ? PrerequisiteRecoveryState.Recovered
                        : transitionRecovered
                            ? PrerequisiteRecoveryState.Recovered
                            : PrerequisiteRecoveryState.TransitionRecoveryExhausted,
                    RecoveryAttempts = 1,
                    RecoveryAction = transitionRecovered
                        ? RuntimeTransitionRecoveryClassifier.RetryAction
                        : RuntimeTransitionRecoveryClassifier.ExhaustedAction
                }
            };
        }

        int recoveryAttempts = recoveryEvents is { Count: > 0 }
            ? Math.Max(1, result.Status.RecoveryAttempts)
            : result.Status.RecoveryAttempts;
        if (IsLeaseRequired(result.Status))
        {
            if (leaseAdapter is null)
            {
                return Failure(
                    request,
                    result.Status with
                    {
                        RecoveryState = PrerequisiteRecoveryState.RecoveryRequired,
                        RecoveryAttempts = 0,
                        RecoveryAction = "acquire-compatible-lease"
                    },
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }

            recoveryAttempts = Math.Max(1, recoveryAttempts + 1);
            DevBridgeLeaseResult lease;
            try
            {
                lease = await leaseAdapter.BeginLeaseAsync(
                        request.WorkflowId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.Cancelled,
                        "RIMTEST_CANCELLED",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: recoveryAttempts,
                        RecoveryAction: "acquire-compatible-lease"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }
            catch (Exception exception)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_LEASE_ACQUIRE_FAILED",
                        Bound(exception.Message),
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: recoveryAttempts,
                        RecoveryAction: "acquire-compatible-lease"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }

            if (!lease.IsUsable)
            {
                PrerequisiteRecoveryState state = LeaseRecoveryState(lease.Status);
                return Failure(
                    request,
                    result.Status with
                    {
                        ErrorCode = lease.Status.ErrorCode ?? result.Status.ErrorCode,
                        Error = lease.Status.Error ?? result.Status.Error,
                        RecoveryState = state,
                        RecoveryAttempts = recoveryAttempts,
                        RecoveryAction = "acquire-compatible-lease"
                    },
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }

            bool released = false;
            string? releaseErrorCode = null;
            try
            {
                result = await developmentAdapter.RunAsync(
                        request.Project,
                        request.RepositoryRoot,
                        request.SourceFingerprint,
                        request.WorkflowId,
                        new DevBridgeModDevelopmentExecutionContext(lease.LeaseId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = result with
                {
                    Status = new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.Cancelled,
                        "RIMTEST_CANCELLED",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: recoveryAttempts,
                        RecoveryAction: "retry-after-lease-acquisition")
                };
            }
            catch (Exception exception)
            {
                result = result with
                {
                    Status = new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVELOPMENT_TRANSACTION_FAILED",
                        Bound(exception.Message),
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: recoveryAttempts,
                        RecoveryAction: "retry-after-lease-acquisition")
                };
            }
            finally
            {
                try
                {
                    DevBridgeLeaseResult end = await leaseAdapter.EndLeaseAsync(
                            lease.LeaseId!,
                            request.WorkflowId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    released = end.Status.IsSuccess;
                    releaseErrorCode = end.Status.ErrorCode;
                }
                catch (Exception)
                {
                    released = false;
                    releaseErrorCode = "DEVBRIDGE_LEASE_RELEASE_FAILED";
                }

                cleanup = new RimTestCleanupSummary
                {
                    Status = released ? "RESTORED" : "FAILED",
                    LeaseReleased = released,
                    TemporaryStateCleared = released,
                    ErrorCode = released
                        ? null
                        : releaseErrorCode ?? "DEVBRIDGE_LEASE_RELEASE_FAILED"
                };
            }

            if (!released)
            {
                result = result with
                {
                    Status = new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_LEASE_RELEASE_FAILED",
                        "The bounded lease recovery completed without authoritative release evidence.",
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: recoveryAttempts,
                        RecoveryAction: "release-recovered-lease")
                };
            }
            else
            {
                result = result with
                {
                    Status = result.Status with
                    {
                        RecoveryState = result.Status.IsSuccess
                            ? PrerequisiteRecoveryState.Recovered
                            : PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts = recoveryAttempts,
                        RecoveryAction = "retry-after-lease-acquisition"
                    }
                };
            }
        }

        RimTestArtifactFreshness freshness = RimTestArtifactFreshness.From(
            result,
            request.WorkflowId);

        if (result.Status.Outcome == DevBridgeOutcomeKind.Cancelled)
        {
            return Failure(
                request,
                result.Status,
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        if (!result.Status.IsSuccess || result.Success != true)
        {
            string code = result.Status.ErrorCode ??
                "DEVELOPMENT_TRANSACTION_FAILED";
            return Failure(
                request,
                result.Status with
                {
                    ErrorCode = code,
                    Error = result.Status.Error ??
                        "DevBridge2 did not complete the mod-development transaction."
                },
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        if (result.Freshness is null ||
            !string.Equals(
                freshness.SourceFingerprint,
                request.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_SOURCE_FINGERPRINT_MISMATCH",
                    "DevBridge2 did not bind the transaction to the selected worktree fingerprint."),
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        string? metadataError = ValidateFreshnessMetadata(freshness);
        if (metadataError is not null)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    metadataError,
                    "DevBridge2 returned incomplete or contradictory artifact-freshness evidence."),
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        if (!freshness.LoadedArtifactFreshnessProven)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    freshness.ErrorCode ?? "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN",
                    "DevBridge2 did not conservatively prove that the tested generation corresponds to the built and deployed artifact."),
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        WorktreeIntegritySnapshotResult finalSnapshot =
            await WorktreeIntegritySnapshot.CaptureAsync(
                    request.RepositoryRoot,
                    request.ChangedPaths,
                    repositoryStateProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        WorktreeMutationClassification classification = finalSnapshot.Success &&
            finalSnapshot.Snapshot is not null
            ? WorktreeMutationClassifier.Classify(
                initialSnapshot.Snapshot,
                finalSnapshot.Snapshot,
                result.BuildOutputs,
                result.TransactionId,
                freshness.DeploymentDecision)
            : WorktreeMutationClassification.Rejected(
                finalSnapshot.Error ??
                    "The final worktree transaction snapshot could not be established.");
        if (!classification.Accepted)
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                    classification.Error ??
                        "The source worktree changed while the artifact transaction was running."),
                freshness,
                cleanup: cleanup,
                recoveryEvents: recoveryEvents);
        }

        return new(
            true,
            result.Status,
            freshness with
            {
                ErrorCode = null,
                SourceInputsStable = classification.BuildOwnedOutputChanges.Count == 0
                    ? null
                    : true,
                BuildOwnedOutputChanges = classification.BuildOwnedOutputChanges.Count == 0
                    ? null
                    : classification.BuildOwnedOutputChanges
            },
            RecoveryEvents: recoveryEvents,
            Cleanup: cleanup);
    }

    private async Task<IdentityRecoveryResult> RecoverIdentityMismatchAsync(
        ArtifactFreshnessTransactionRequest request,
        DevBridgeModDevelopmentResult initial,
        CancellationToken cancellationToken)
    {
        List<RimTestPrerequisiteRecovery> events = [];
        DevBridgeAdapterStatus status = initial.Status;
        DevBridgeModDevelopmentResult result = initial;
        DevBridgeIdentityMismatch? mismatch = status.IdentityMismatch;

        if (!DevBridgeIdentityMismatchPolicy.ShouldRecover(status))
        {
            return new(
                result with
                {
                    Status = DevBridgeIdentityMismatchPolicy.Refuse(status)
                },
                events,
                false);
        }

        if (readinessAdapter is null || string.IsNullOrWhiteSpace(request.TestRecipe))
        {
            return new(
                result with
                {
                    Status = status with
                    {
                        RecoveryState = PrerequisiteRecoveryState.RecoveryRequired,
                        RecoveryAttempts = 0,
                        RecoveryAction = "establish-fresh-generation"
                    }
                },
                events,
                false);
        }

        int? previousGeneration = result.Generation ?? result.Freshness?.Generation;
        for (int attempt = 1; attempt <= DevBridgeIdentityMismatchPolicy.MaximumAttempts; attempt++)
        {
            events.Add(new RimTestPrerequisiteRecovery(
                RuntimeTransitionRecoveryClassifier.Component,
                "recovering",
                attempt,
                status.ErrorCode,
                "refresh-identity-and-wait-ready",
                request.WorkflowId,
                previousGeneration,
                mismatch));
            RecordTransitionRecovery(
                AgentEventTypes.RetryStarted,
                "Refreshing DevBridge identity before retrying the affected transaction.",
                request,
                status,
                previousGeneration,
                "refresh-identity-and-wait-ready");

            DevBridgeFreshGenerationResult recovery;
            try
            {
                recovery = await readinessAdapter.EnsureFreshGenerationAsync(
                        request.TestRecipe!,
                        previousGeneration,
                        request.WorkflowId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(
                    result with
                    {
                        Status = status with
                        {
                            Outcome = DevBridgeOutcomeKind.Cancelled,
                            ErrorCode = "RIMTEST_CANCELLED",
                            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts = attempt,
                            RecoveryAction = "cancel-identity-recovery"
                        }
                    },
                    events,
                    false);
            }
            catch (Exception exception)
            {
                return new(
                    result with
                    {
                        Status = status with
                        {
                            ErrorCode = "DEVBRIDGE_READINESS_RECOVERY_FAILED",
                            Error = Bound(exception.Message),
                            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts = attempt,
                            RecoveryAction = "refresh-identity-and-wait-ready"
                        }
                    },
                    events,
                    false);
            }

            if (!recovery.IsUsable)
            {
                if (DevBridgeIdentityMismatchPolicy.IsIdentityMismatch(recovery.Status) &&
                    !DevBridgeIdentityMismatchPolicy.ShouldRecover(recovery.Status))
                {
                    DevBridgeIdentityMismatch? refusedMismatch =
                        recovery.Status.IdentityMismatch ?? mismatch;
                    events.Add(new RimTestPrerequisiteRecovery(
                        RuntimeTransitionRecoveryClassifier.Component,
                        PrerequisiteRecoveryState.RecoveryFailed.ToWireName(),
                        attempt,
                        recovery.Status.ErrorCode,
                        "refuse-unsafe-identity-recovery",
                        request.WorkflowId,
                        recovery.Generation ?? previousGeneration,
                        refusedMismatch));
                    return new(
                        result with
                        {
                            Status = DevBridgeIdentityMismatchPolicy.Refuse(
                                recovery.Status,
                                "refuse-unsafe-identity-recovery") with
                            {
                                RecoveryAttempts = attempt
                            }
                        },
                        events,
                        false);
                }
                status = status with
                {
                    ErrorCode = recovery.Status.ErrorCode ?? status.ErrorCode,
                    Error = recovery.Status.Error ?? status.Error,
                    RecoveryAttempts = attempt,
                    RecoveryAction = "refresh-identity-and-wait-ready"
                };
                if (attempt == DevBridgeIdentityMismatchPolicy.MaximumAttempts)
                {
                    return new(
                        result with
                        {
                            Status = status with
                            {
                                RecoveryState = PrerequisiteRecoveryState.TransitionRecoveryExhausted
                            }
                        },
                        events,
                        false);
                }

                continue;
            }

            if (!WorktreeFingerprint.TryCompute(
                    request.RepositoryRoot,
                    request.ChangedPaths,
                    out string recoveredSourceFingerprint,
                    out string? recoveredFingerprintError) ||
                !string.Equals(
                    recoveredSourceFingerprint,
                    request.SourceFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    result with
                    {
                        Status = status with
                        {
                            ErrorCode = "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                            Error = recoveredFingerprintError ??
                                "The selected source inputs changed while identity recovery was running.",
                            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts = attempt,
                            RecoveryAction = "revalidate-source-fingerprint"
                        }
                    },
                    events,
                    false);
            }

            try
            {
                result = await developmentAdapter.RunAsync(
                        request.Project,
                        request.RepositoryRoot,
                        request.SourceFingerprint,
                        request.WorkflowId,
                        executionContext: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(
                    result with
                    {
                        Status = status with
                        {
                            Outcome = DevBridgeOutcomeKind.Cancelled,
                            ErrorCode = "RIMTEST_CANCELLED",
                            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts = attempt,
                            RecoveryAction = "cancel-identity-recovery"
                        }
                    },
                    events,
                    false);
            }
            catch (Exception exception)
            {
                return new(
                    result with
                    {
                        Status = status with
                        {
                            ErrorCode = "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                            Error = Bound(exception.Message),
                            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts = attempt,
                            RecoveryAction = RuntimeTransitionRecoveryClassifier.RetryAction
                        }
                    },
                    events,
                    false);
            }

            int? rerunGeneration = result.Generation ?? result.Freshness?.Generation;
            if (result.Status.IsSuccess &&
                result.Success == true &&
                recovery.Generation is int recoveredGeneration &&
                (rerunGeneration is null || rerunGeneration.Value < recoveredGeneration))
            {
                return new(
                    result with
                    {
                        Status = new DevBridgeAdapterStatus(
                            DevBridgeOutcomeKind.InfrastructureFailure,
                            "RIMTEST_ARTIFACT_GENERATION_MISMATCH",
                            "The recovered transaction did not prove evidence from the final READY generation.",
                            RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                            RecoveryAttempts: attempt,
                            RecoveryAction: "reject-stale-generation-evidence",
                            IdentityMismatch: mismatch)
                    },
                    events,
                    false);
            }

            if (DevBridgeIdentityMismatchPolicy.IsIdentityMismatch(result.Status))
            {
                mismatch = result.Status.IdentityMismatch ?? mismatch;
                status = result.Status;
                previousGeneration = rerunGeneration ?? recovery.Generation;
                if (!DevBridgeIdentityMismatchPolicy.ShouldRecover(status) ||
                    attempt == DevBridgeIdentityMismatchPolicy.MaximumAttempts)
                {
                    PrerequisiteRecoveryState terminalState =
                        DevBridgeIdentityMismatchPolicy.ShouldRecover(status)
                            ? PrerequisiteRecoveryState.TransitionRecoveryExhausted
                            : PrerequisiteRecoveryState.RecoveryFailed;
                    return new(
                        result with
                        {
                            Status = status with
                            {
                                RecoveryState = terminalState,
                                RecoveryAttempts = attempt,
                                RecoveryAction = terminalState ==
                                    PrerequisiteRecoveryState.RecoveryFailed
                                        ? "refuse-unsafe-identity-recovery"
                                        : RuntimeTransitionRecoveryClassifier.ExhaustedAction
                            }
                        },
                        events,
                        false);
                }

                continue;
            }

            events.Add(new RimTestPrerequisiteRecovery(
                RuntimeTransitionRecoveryClassifier.Component,
                PrerequisiteRecoveryState.Recovered.ToWireName(),
                attempt,
                null,
                "retry-development-freshness-transaction",
                request.WorkflowId,
                rerunGeneration ?? recovery.Generation,
                mismatch));
            return new(
                result with
                {
                    Status = result.Status with
                    {
                        RecoveryState = PrerequisiteRecoveryState.Recovered,
                        RecoveryAttempts = attempt,
                        RecoveryAction = "retry-development-freshness-transaction"
                    }
                },
                events,
                true);
        }

        return new(
            result with
            {
                Status = status with
                {
                    RecoveryState = PrerequisiteRecoveryState.TransitionRecoveryExhausted,
                    RecoveryAttempts = DevBridgeIdentityMismatchPolicy.MaximumAttempts,
                    RecoveryAction = RuntimeTransitionRecoveryClassifier.ExhaustedAction
                }
            },
            events,
            false);
    }

    private sealed record IdentityRecoveryResult(
        DevBridgeModDevelopmentResult Result,
        List<RimTestPrerequisiteRecovery> Events,
        bool CanContinue);

    private static bool IsLeaseRequired(DevBridgeAdapterStatus status) =>
        string.Equals(
            status.ErrorCode,
            "RIMBRIDGE_LEASE_REQUIRED",
            StringComparison.Ordinal);

    private static PrerequisiteRecoveryState LeaseRecoveryState(
        DevBridgeAdapterStatus status)
    {
        if (status.ErrorCode?.Contains("CONTEND", StringComparison.OrdinalIgnoreCase) == true ||
            status.ErrorCode?.Contains("HELD", StringComparison.OrdinalIgnoreCase) == true ||
            status.ErrorCode?.Contains("OWNER", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PrerequisiteRecoveryState.Contended;
        }

        return status.Outcome is DevBridgeOutcomeKind.InfrastructureFailure or
            DevBridgeOutcomeKind.Timeout or
            DevBridgeOutcomeKind.MalformedResponse
            ? PrerequisiteRecoveryState.Unavailable
            : PrerequisiteRecoveryState.RecoveryFailed;
    }

    private static string? ValidateFreshnessMetadata(
        RimTestArtifactFreshness freshness)
    {
        if (!IsSha256(freshness.BuiltArtifactSha256) ||
            !IsSha256(freshness.DeployedArtifactSha256) ||
            !string.Equals(
                freshness.BuiltArtifactSha256,
                freshness.DeployedArtifactSha256,
                StringComparison.OrdinalIgnoreCase) ||
            freshness.GenerationBefore is null ||
            freshness.GenerationAfter is null ||
            freshness.Generation is null ||
            freshness.GenerationAfter.Value != freshness.Generation.Value ||
            string.IsNullOrWhiteSpace(freshness.DeploymentDecision) ||
            freshness.DeploymentDecision is not ("deployed" or "unchanged") ||
            string.IsNullOrWhiteSpace(freshness.TransactionId) ||
            string.IsNullOrWhiteSpace(freshness.LeaseId) ||
            string.IsNullOrWhiteSpace(freshness.Proof))
        {
            return "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN";
        }

        if (freshness.DeploymentDecision == "deployed" &&
            freshness.GenerationAfter.Value <= freshness.GenerationBefore.Value)
        {
            return "RIMTEST_ARTIFACT_GENERATION_MISMATCH";
        }

        return null;
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static ArtifactFreshnessTransactionResult Failure(
        ArtifactFreshnessTransactionRequest request,
        DevBridgeAdapterStatus status,
        RimTestArtifactFreshness? freshness = null,
        RimTestCleanupSummary? cleanup = null,
        IReadOnlyList<RimTestPrerequisiteRecovery>? recoveryEvents = null)
    {
        RimTestArtifactFreshness projected = (freshness ?? new RimTestArtifactFreshness
        {
            SourceFingerprint = request.SourceFingerprint,
            WorkflowId = request.WorkflowId
        }) with
        {
            SourceFingerprint = freshness?.SourceFingerprint ?? request.SourceFingerprint,
            WorkflowId = freshness?.WorkflowId ?? request.WorkflowId,
            EvaluationStatus = freshness is null ||
                string.Equals(
                    freshness.EvaluationStatus,
                    "NOT_EVALUATED",
                    StringComparison.Ordinal)
                ? "NOT_EVALUATED"
                : status.ErrorCode?.Contains(
                    "STALE",
                    StringComparison.OrdinalIgnoreCase) == true
                    ? "STALE"
                    : "FAILED",
            LoadedArtifactFreshnessProven = false,
            ErrorCode = status.ErrorCode ?? "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN"
        };
        return new(false, status, projected, recoveryEvents, cleanup);
    }

    private static void RecordTransitionRecovery(
        string eventType,
        string summary,
        ArtifactFreshnessTransactionRequest request,
        DevBridgeAdapterStatus status,
        int? generation,
        string action,
        bool recovered = false)
    {
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Implementation,
            eventType,
            summary,
            new
            {
                operationKey = "artifact-freshness:" + (request.WorkflowId ?? request.Project),
                component = RuntimeTransitionRecoveryClassifier.Component,
                state = recovered
                    ? PrerequisiteRecoveryState.Recovered.ToWireName()
                    : eventType == AgentEventTypes.RetryStarted
                        ? "recovering"
                        : status.RecoveryState.ToWireName(),
                attempts = 1,
                action,
                errorCode = status.ErrorCode,
                workflowId = request.WorkflowId,
                generation,
                recovered
            });
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 4096 ? trimmed : trimmed[..4096];
    }
}

internal sealed record WorktreePathState(
    string Status,
    bool Exists,
    string? Sha256);

internal sealed record WorktreeIntegritySnapshot(
    string RootPath,
    string? HeadSha,
    IReadOnlyDictionary<string, WorktreePathState> Paths)
{
    public static async Task<WorktreeIntegritySnapshotResult> CaptureAsync(
        string rootPath,
        IReadOnlyList<string> protectedPaths,
        IGitRepositoryStateProvider? repositoryStateProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            string fullRoot = Path.GetFullPath(rootPath);
            GitRepositoryStateSnapshot? repository = null;
            if (repositoryStateProvider is not null)
            {
                GitRepositoryStateResult result = await repositoryStateProvider
                    .ReadWorktreeAsync(fullRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Resolved || result.State is null)
                {
                    return WorktreeIntegritySnapshotResult.Failed(
                        result.Error ?? result.ErrorCode ??
                            "Git worktree state could not be resolved.");
                }

                repository = result.State;
                if (!PathsEqual(fullRoot, repository.RootPath))
                {
                    return WorktreeIntegritySnapshotResult.Failed(
                        "Git resolved a different repository root during the transaction.");
                }
            }

            var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (repository is not null)
            {
                foreach (GitRepositoryChange change in repository.Changes)
                {
                    AddStatus(statuses, change.Path, change.Status);
                    if (!string.IsNullOrWhiteSpace(change.OriginalPath))
                    {
                        AddStatus(statuses, change.OriginalPath!, change.Status);
                    }
                }
            }

            string[] paths = protectedPaths
                .Concat(statuses.Keys)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(path => NormalizePath(fullRoot, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var states = new Dictionary<string, WorktreePathState>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (!WorktreeFingerprint.TryComputeFileSha256(
                        fullRoot,
                        path,
                        out bool exists,
                        out string? sha256,
                        out string? error))
                {
                    return WorktreeIntegritySnapshotResult.Failed(error ??
                        "A worktree path could not be read for transaction integrity.");
                }

                statuses.TryGetValue(path, out string? status);
                states[path] = new WorktreePathState(status ?? string.Empty, exists, sha256);
            }

            return new WorktreeIntegritySnapshotResult(
                true,
                new WorktreeIntegritySnapshot(
                    fullRoot,
                    repository?.HeadSha,
                    states));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return WorktreeIntegritySnapshotResult.Failed(exception.Message);
        }
    }

    private static void AddStatus(
        IDictionary<string, string> statuses,
        string path,
        string status)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (statuses.TryGetValue(normalized, out string? existing) &&
            !string.Equals(existing, status, StringComparison.Ordinal))
        {
            statuses[normalized] = existing + ";" + status;
            return;
        }

        statuses[normalized] = status;
    }

    private static string NormalizePath(string root, string path)
    {
        string candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(
                root,
                path.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathsEqual(candidate, root) &&
            !candidate.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A worktree transaction path is outside the repository root.");
        }

        return Path.GetRelativePath(root, candidate).Replace('\\', '/');
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record WorktreeIntegritySnapshotResult(
    bool Success,
    WorktreeIntegritySnapshot? Snapshot = null,
    string? Error = null)
{
    public static WorktreeIntegritySnapshotResult Failed(string error) =>
        new(false, Error: error);
}

internal sealed record WorktreeMutationClassification(
    bool Accepted,
    IReadOnlyList<RimTestBuildOwnedOutputChange> BuildOwnedOutputChanges,
    string? Error = null)
{
    public static WorktreeMutationClassification Rejected(string error) =>
        new(false, [], error);
}

internal static class WorktreeMutationClassifier
{
    public static WorktreeMutationClassification Classify(
        WorktreeIntegritySnapshot before,
        WorktreeIntegritySnapshot after,
        IReadOnlyList<DevBridgeBuildOutputEvidence>? buildOutputs,
        string? transactionId,
        string? deploymentDecision)
    {
        if (!string.Equals(before.RootPath, after.RootPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.HeadSha, after.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return WorktreeMutationClassification.Rejected(
                "The repository identity or HEAD changed during the artifact transaction.");
        }

        var expected = new Dictionary<string, DevBridgeBuildOutputEvidence>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DevBridgeBuildOutputEvidence output in buildOutputs ?? [])
        {
            string path = output.RepositoryPath.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path) ||
                !IsSha256(output.Sha256) ||
                string.IsNullOrWhiteSpace(transactionId) ||
                !string.Equals(output.TransactionId, transactionId, StringComparison.Ordinal) ||
                !expected.TryAdd(path, output))
            {
                return WorktreeMutationClassification.Rejected(
                    "Build-output provenance was incomplete or contradictory.");
            }
        }

        string[] mutatedPaths = before.Paths.Keys
            .Concat(after.Paths.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !SameState(before.Paths, after.Paths, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accepted = new List<RimTestBuildOwnedOutputChange>();
        foreach (string path in mutatedPaths)
        {
            before.Paths.TryGetValue(path, out WorktreePathState? previous);
            after.Paths.TryGetValue(path, out WorktreePathState? current);
            if (!string.Equals(deploymentDecision, "deployed", StringComparison.Ordinal) ||
                !expected.TryGetValue(path, out DevBridgeBuildOutputEvidence? output) ||
                current is null ||
                !current.Exists ||
                !string.Equals(current.Sha256, output.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !IsOwnerWritableStatusChange(previous, current))
            {
                return WorktreeMutationClassification.Rejected(
                    $"The worktree path '{path}' changed without matching build-output provenance.");
            }

            accepted.Add(new RimTestBuildOwnedOutputChange
            {
                Path = path,
                Sha256 = output.Sha256.ToLowerInvariant()
            });
        }

        return new WorktreeMutationClassification(true, accepted);
    }

    private static bool SameState(
        IReadOnlyDictionary<string, WorktreePathState> before,
        IReadOnlyDictionary<string, WorktreePathState> after,
        string path) =>
        before.TryGetValue(path, out WorktreePathState? left) ==
            after.TryGetValue(path, out WorktreePathState? right) &&
        Equals(left, right);

    private static bool IsOwnerWritableStatusChange(
        WorktreePathState? before,
        WorktreePathState after)
    {
        if (before is not null &&
            string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (after.Status == "??")
        {
            return before is null;
        }

        if (after.Status.Length != 2 || after.Status[1] != 'M')
        {
            return false;
        }

        char previousIndexStatus = before?.Status.Length >= 1
            ? before.Status[0]
            : ' ';
        return after.Status[0] == previousIndexStatus;
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');
}

internal static class SourceChangeClassifier
{
    public static bool IsBuildRelevant(IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        return changedPaths.Any(IsBuildRelevant);
    }

    public static bool IsBuildRelevant(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/').TrimStart('.', '/');
        string lower = normalized.ToLowerInvariant();
        if (lower.StartsWith(".git/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimctx/", StringComparison.Ordinal) ||
            lower.Contains("/bin/", StringComparison.Ordinal) ||
            lower.Contains("/obj/", StringComparison.Ordinal) ||
            lower.StartsWith("bin/", StringComparison.Ordinal) ||
            lower.StartsWith("obj/", StringComparison.Ordinal))
        {
            return false;
        }

        if (lower.StartsWith("source/", StringComparison.Ordinal) ||
            lower.StartsWith("src/", StringComparison.Ordinal))
        {
            return true;
        }

        string extension = Path.GetExtension(lower);
        return extension is ".cs" or ".csproj" or ".fs" or ".fsproj" or
            ".vb" or ".vbproj" or ".props" or ".targets" or ".sln" or
            ".slnx" or ".lock" ||
            lower.EndsWith("global.json", StringComparison.Ordinal) ||
            lower.EndsWith("directory.build.props", StringComparison.Ordinal) ||
            lower.EndsWith("directory.build.targets", StringComparison.Ordinal) ||
            lower.EndsWith("directory.packages.props", StringComparison.Ordinal);
    }
}

internal static class WorktreeFingerprint
{
    public static bool TryComputeFileSha256(
        string root,
        string path,
        out bool exists,
        out string? sha256,
        out string? error)
    {
        exists = false;
        sha256 = null;
        error = null;
        try
        {
            string fullRoot = Path.GetFullPath(root);
            string candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(
                    fullRoot,
                    path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(candidate, fullRoot))
            {
                throw new InvalidOperationException(
                    "A changed path is outside the current worktree.");
            }

            if (!File.Exists(candidate))
            {
                return true;
            }

            exists = true;
            using FileStream stream = File.OpenRead(candidate);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            error = Bound(exception.Message);
            return false;
        }
    }

    public static bool TryCompute(
        string root,
        IReadOnlyList<string> changedPaths,
        out string fingerprint,
        out string? error)
    {
        fingerprint = string.Empty;
        error = null;
        try
        {
            string fullRoot = Path.GetFullPath(root);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string path in changedPaths
                         .Where(static value => !string.IsNullOrWhiteSpace(value))
                         .Select(static value => value.Replace('\\', '/'))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
            {
                string rawPath = path;
                string relative = rawPath.TrimStart('/');
                if (Path.IsPathRooted(rawPath))
                {
                    string candidate = Path.GetFullPath(rawPath);
                    if (!IsWithin(candidate, fullRoot))
                    {
                        throw new InvalidOperationException(
                            "A changed path is outside the current worktree.");
                    }

                    relative = Path.GetRelativePath(fullRoot, candidate).Replace('\\', '/');
                }

                string filePath = Path.GetFullPath(
                    Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(filePath, fullRoot))
                {
                    throw new InvalidOperationException(
                        "A changed path escapes the current worktree.");
                }

                AppendText(hash, relative);
                if (!File.Exists(filePath))
                {
                    AppendText(hash, "\0missing\0");
                    continue;
                }

                AppendText(hash, "\0file\0");
                using FileStream stream = File.OpenRead(filePath);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                AppendText(hash, "\0end\0");
            }

            fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            error = Bound(exception.Message);
            return false;
        }
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static bool IsWithin(string candidate, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 1024 ? trimmed : trimmed[..1024];
    }
}
