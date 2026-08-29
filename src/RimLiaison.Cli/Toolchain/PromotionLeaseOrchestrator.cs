using RimLiaison.DevBridge;

namespace RimLiaison.Toolchain;

public sealed record PromotionLiveVerificationResult(
    bool Passed,
    string? ErrorCode,
    string? Error,
    string? LeaseId,
    int? Generation,
    bool LeaseReleased,
    int Attempts,
    string Stage);

/// <summary>
/// Owns only the bounded lease scope around the live capability verification
/// required by production promotion. Lease creation and release remain the
/// canonical DevBridge adapter operations.
/// </summary>
public interface IPromotionLeaseOrchestrator
{
    Task<PromotionLiveVerificationResult> VerifyCapabilitiesAsync(
        string workflowId,
        int? expectedGeneration,
        CancellationToken cancellationToken = default);
}

public sealed class PromotionLeaseOrchestrator : IPromotionLeaseOrchestrator
{
    private const int MaximumAttempts = 2;
    private readonly IDevBridgeLeaseAdapter leaseAdapter;
    private readonly IDevBridgeCapabilityAdapter capabilityAdapter;

    public PromotionLeaseOrchestrator(
        IDevBridgeLeaseAdapter leaseAdapter,
        IDevBridgeCapabilityAdapter capabilityAdapter)
    {
        this.leaseAdapter = leaseAdapter ?? throw new ArgumentNullException(nameof(leaseAdapter));
        this.capabilityAdapter = capabilityAdapter ?? throw new ArgumentNullException(nameof(capabilityAdapter));
    }

    public async Task<PromotionLiveVerificationResult> VerifyCapabilitiesAsync(
        string workflowId,
        int? expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return Failure(
                "PROMOTION_WORKFLOW_ID_MISSING",
                "A promotion workflow identity is required.",
                null,
                expectedGeneration,
                0,
                "lease-not-started");
        }

        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            DevBridgeLeaseResult lease;
            try
            {
                lease = await leaseAdapter.BeginLeaseAsync(workflowId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    "RIMTEST_CANCELLED",
                    "Promotion lease acquisition was cancelled.",
                    null,
                    expectedGeneration,
                    attempt,
                    "lease-acquisition");
            }
            catch (Exception exception)
            {
                return Failure(
                    "DEVBRIDGE_LEASE_ACQUIRE_FAILED",
                    exception.Message,
                    null,
                    expectedGeneration,
                    attempt,
                    "lease-acquisition");
            }

            if (!lease.IsUsable)
            {
                return Failure(
                    lease.Status.ErrorCode ?? "DEVBRIDGE_LEASE_ACQUIRE_FAILED",
                    lease.Status.Error ?? "DevBridge did not grant a usable promotion lease.",
                    lease.LeaseId,
                    lease.Generation ?? expectedGeneration,
                    attempt,
                    "lease-acquisition");
            }

            PromotionLiveVerificationResult verification = Failure(
                "PROMOTION_LIVE_VERIFICATION_FAILED",
                "Promotion live verification did not produce a result.",
                lease.LeaseId,
                lease.Generation,
                attempt,
                "capabilities-check");
            bool retryForGeneration = false;
            bool released = false;
            string? releaseError = null;
            try
            {
                if (expectedGeneration is > 0 && lease.Generation != expectedGeneration)
                {
                    retryForGeneration = attempt < MaximumAttempts;
                    verification = Failure(
                        "PROMOTION_GENERATION_MISMATCH",
                        "The promotion lease belongs to a different DevBridge generation.",
                        lease.LeaseId,
                        lease.Generation,
                        attempt,
                        "generation-validation");
                }
                else
                {
                    DevBridgeCapabilityDiscoveryResult capabilities;
                    try
                    {
                        capabilities = await capabilityAdapter.DiscoverAsync(
                                new DevBridgeCapabilityQuery(Limit: 1),
                                workflowId,
                                lease.LeaseId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        verification = Failure(
                            "RIMTEST_CANCELLED",
                            "Promotion live verification was cancelled.",
                            lease.LeaseId,
                            lease.Generation,
                            attempt,
                            "capabilities-check");
                        capabilities = null!;
                    }
                    catch (Exception exception)
                    {
                        verification = Failure(
                            "PROMOTION_LIVE_VERIFICATION_FAILED",
                            exception.Message,
                            lease.LeaseId,
                            lease.Generation,
                            attempt,
                            "capabilities-check");
                        capabilities = null!;
                    }

                    if (capabilities is not null)
                    {
                        retryForGeneration =
                            GenerationChanged(capabilities, lease.Generation, expectedGeneration) &&
                            attempt < MaximumAttempts;
                        verification = capabilities.Status.IsSuccess
                            ? new PromotionLiveVerificationResult(
                                true,
                                null,
                                null,
                                lease.LeaseId,
                                capabilities.Status.Evidence?.Generation ?? lease.Generation,
                                false,
                                attempt,
                                "capabilities-check")
                            : Failure(
                                capabilities.Status.ErrorCode ?? "PROMOTION_LIVE_VERIFICATION_FAILED",
                                capabilities.Status.Error ?? "The live capability verification failed.",
                                lease.LeaseId,
                                capabilities.Status.Evidence?.Generation ?? lease.Generation,
                                attempt,
                                "capabilities-check");
                    }
                }
            }
            finally
            {
                try
                {
                    DevBridgeLeaseResult ended = await leaseAdapter.EndLeaseAsync(
                            lease.LeaseId!,
                            workflowId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    released = ended.Status.IsSuccess;
                    if (!released)
                    {
                        releaseError = ended.Status.Error ??
                            "DevBridge rejected promotion lease release.";
                    }
                }
                catch (Exception exception)
                {
                    releaseError = exception.Message;
                }
            }

            if (!released)
            {
                return Failure(
                    "PROMOTION_LEASE_RELEASE_FAILED",
                    releaseError ?? "DevBridge did not release the promotion lease.",
                    lease.LeaseId,
                    lease.Generation,
                    attempt,
                    "lease-release");
            }

            if (retryForGeneration)
            {
                continue;
            }

            return verification with { LeaseReleased = true };
        }

        return Failure(
            "PROMOTION_GENERATION_MISMATCH",
            "Promotion live verification exhausted its bounded generation reacquisition budget.",
            null,
            expectedGeneration,
            MaximumAttempts,
            "generation-validation");
    }

    private static bool GenerationChanged(
        DevBridgeCapabilityDiscoveryResult result,
        int? leaseGeneration,
        int? expectedGeneration) =>
        result.Status.Evidence?.Generation is int reported &&
        ((leaseGeneration is > 0 && reported != leaseGeneration) ||
         (expectedGeneration is > 0 && reported != expectedGeneration));

    private static PromotionLiveVerificationResult Failure(
        string code,
        string error,
        string? leaseId,
        int? generation,
        int attempts,
        string stage) =>
        new(false, code, error, leaseId, generation, false, attempts, stage);
}
