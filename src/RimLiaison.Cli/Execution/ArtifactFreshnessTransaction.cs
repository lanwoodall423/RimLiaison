using System.Security.Cryptography;
using System.Text;
using RimLiaison.DevBridge;
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

    public ArtifactFreshnessTransaction(
        IDevBridgeModDevelopmentAdapter developmentAdapter,
        IDevBridgeLeaseAdapter? leaseAdapter = null,
        IDevBridgeFreshGenerationAdapter? readinessAdapter = null)
    {
        this.developmentAdapter = developmentAdapter ??
            throw new ArgumentNullException(nameof(developmentAdapter));
        this.leaseAdapter = leaseAdapter;
        this.readinessAdapter = readinessAdapter;
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

        // PROCESS_EXITED and the related identity/readiness codes are owned
        // runtime conditions, not source failures.  Give the lifecycle owner
        // one bounded chance to establish a fresh READY generation before the
        // authoritative build/deploy transaction is attempted again.
        if (IsReadinessRecoverable(result.Status) &&
            readinessAdapter is not null &&
            !string.IsNullOrWhiteSpace(request.TestRecipe))
        {
            DevBridgeFreshGenerationResult recovery;
            try
            {
                recovery = await readinessAdapter.EnsureFreshGenerationAsync(
                        request.TestRecipe!,
                        result.Generation ?? result.Freshness?.Generation,
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
                        RecoveryAction: "restart-and-retry-development-transaction"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }
            catch (Exception exception)
            {
                return Failure(
                    request,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_READINESS_RECOVERY_FAILED",
                        Bound(exception.Message),
                        RecoveryState: PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts: 1,
                        RecoveryAction: "restart-and-retry-development-transaction"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }

            if (!recovery.IsUsable)
            {
                return Failure(
                    request,
                    result.Status with
                    {
                        ErrorCode = recovery.Status.ErrorCode ?? result.Status.ErrorCode,
                        Error = recovery.Status.Error ?? result.Status.Error,
                        RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
                        RecoveryAttempts = 1,
                        RecoveryAction = "restart-and-retry-development-transaction"
                    },
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
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
                        RecoveryAction: "restart-and-retry-development-transaction"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
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
                        RecoveryAction: "restart-and-retry-development-transaction"),
                    RimTestArtifactFreshness.From(result, request.WorkflowId));
            }

            result = result with
            {
                Status = result.Status with
                {
                    RecoveryState = result.Status.IsSuccess
                        ? PrerequisiteRecoveryState.Recovered
                        : PrerequisiteRecoveryState.RecoveryFailed,
                    RecoveryAttempts = 1,
                    RecoveryAction = "restart-and-retry-development-transaction"
                }
            };
        }

        int recoveryAttempts = 0;
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

            recoveryAttempts = 1;
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
            return Failure(request, result.Status, freshness, cleanup: cleanup);
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
                cleanup: cleanup);
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
                cleanup: cleanup);
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
                cleanup: cleanup);
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
                cleanup: cleanup);
        }

        if (!WorktreeFingerprint.TryCompute(
                request.RepositoryRoot,
                request.ChangedPaths,
                out string afterFingerprint,
                out string? fingerprintError) ||
            !string.Equals(
                afterFingerprint,
                request.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                request,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION",
                    fingerprintError ??
                        "The source worktree changed while the artifact transaction was running."),
                freshness,
                cleanup: cleanup);
        }

        return new(
            true,
            result.Status,
            freshness with { ErrorCode = null },
            Cleanup: cleanup);
    }

    private static bool IsLeaseRequired(DevBridgeAdapterStatus status) =>
        string.Equals(
            status.ErrorCode,
            "RIMBRIDGE_LEASE_REQUIRED",
            StringComparison.Ordinal);

    private static bool IsReadinessRecoverable(DevBridgeAdapterStatus status) =>
        status.ErrorCode is "PROCESS_EXITED" or
            "PROCESS_STOPPED" or
            "READINESS_IDENTITY_MISMATCH" or
            "RIMBRIDGE_NOT_READY" or
            "RIMBRIDGE_STALE" or
            "GENERATION_INPUT_MISMATCH";

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
        RimTestCleanupSummary? cleanup = null)
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
        return new(false, status, projected, Cleanup: cleanup);
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
