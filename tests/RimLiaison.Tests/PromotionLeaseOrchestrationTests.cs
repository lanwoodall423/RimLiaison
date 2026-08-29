using RimLiaison.DevBridge;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class PromotionLeaseOrchestrationTests
{
    public static void AcquiresCanonicalLease()
    {
        var lease = new FakeLeaseAdapter("lease-a", 12);
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(result.Passed && lease.BeginCalls == 1, "live promotion verification did not acquire one canonical lease");
    }

    public static void ForwardsLeaseIdToLiveVerification()
    {
        var lease = new FakeLeaseAdapter("lease-forwarded", 12);
        var capabilities = new FakeCapabilityAdapter(Success());
        _ = Run(lease, capabilities, 12);
        Assert(capabilities.LeaseIds.SequenceEqual(["lease-forwarded"]), "the canonical lease ID did not reach capabilities");
    }

    public static void ForwardsWorkflowIdentityToLeaseAndVerification()
    {
        var lease = new FakeLeaseAdapter("lease-workflow", 12);
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12, "promotion-workflow");
        Assert(result.Passed && lease.Workflows.All(value => value == "promotion-workflow") &&
               capabilities.Workflows.SequenceEqual(["promotion-workflow"]),
            "promotion lease and live verification did not share the workflow owner");
    }

    public static void SuccessfulVerificationReleasesLease()
    {
        var lease = new FakeLeaseAdapter("lease-success", 12);
        PromotionLiveVerificationResult result = Run(lease, new FakeCapabilityAdapter(Success()), 12);
        Assert(result.Passed && result.LeaseReleased && lease.EndCalls == 1,
            "successful live verification did not release its lease");
    }

    public static void FailedVerificationReleasesLease()
    {
        var lease = new FakeLeaseAdapter("lease-failure", 12);
        var capabilities = new FakeCapabilityAdapter(Failure("RIMBRIDGE_RUNTIME_FAILED"));
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(!result.Passed && result.ErrorCode == "RIMBRIDGE_RUNTIME_FAILED" &&
               result.LeaseReleased && lease.EndCalls == 1,
            "live verification failure did not preserve its error or release the lease");
    }

    public static void ExceptionReleasesLease()
    {
        var lease = new FakeLeaseAdapter("lease-exception", 12);
        var capabilities = new FakeCapabilityAdapter { Throw = new InvalidOperationException("live probe failed") };
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(!result.Passed && result.ErrorCode == "PROMOTION_LIVE_VERIFICATION_FAILED" &&
               result.LeaseReleased && lease.EndCalls == 1,
            "live verification exception did not release its lease");
    }

    public static void CancellationReleasesLease()
    {
        var lease = new FakeLeaseAdapter("lease-cancel", 12);
        var capabilities = new FakeCapabilityAdapter { Throw = new OperationCanceledException() };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        PromotionLiveVerificationResult result = new PromotionLeaseOrchestrator(lease, capabilities)
            .VerifyCapabilitiesAsync("promotion-test", 12, cancellation.Token)
            .GetAwaiter()
            .GetResult();
        Assert(!result.Passed && result.ErrorCode == "RIMTEST_CANCELLED" &&
               result.LeaseReleased && lease.EndCalls == 1,
            "cancelled live verification did not release its lease");
    }

    public static void GenerationMismatchReacquiresOnce()
    {
        var lease = new FakeLeaseAdapter("lease-new-generation", 12);
        lease.BeginResults.Enqueue(Lease("lease-old-generation", 13));
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(result.Passed && result.Attempts == 2 && lease.BeginCalls == 2 && lease.EndCalls == 2,
            "safe generation mismatch did not perform one bounded reacquisition");
    }

    public static void GenerationMismatchIsBounded()
    {
        var lease = new FakeLeaseAdapter("unused", 12);
        lease.BeginResults.Enqueue(Lease("lease-wrong-1", 13));
        lease.BeginResults.Enqueue(Lease("lease-wrong-2", 14));
        PromotionLiveVerificationResult result = Run(lease, new FakeCapabilityAdapter(Success()), 12);
        Assert(!result.Passed && result.ErrorCode == "PROMOTION_GENERATION_MISMATCH" &&
               result.Attempts == 2 && lease.BeginCalls == 2 && lease.EndCalls == 2,
            "generation reacquisition was not bounded");
    }

    public static void CapabilityGenerationChangeReacquires()
    {
        var lease = new FakeLeaseAdapter("lease-capability", 12);
        var capabilities = new FakeCapabilityAdapter(
            Failure("RIMBRIDGE_GENERATION_MISMATCH", generation: 13),
            Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(result.Passed && result.Attempts == 2 && lease.BeginCalls == 2 && lease.EndCalls == 2,
            "capability generation change did not trigger bounded reacquisition");
    }

    public static void LeaseReleaseFailureBlocksPromotion()
    {
        var lease = new FakeLeaseAdapter("lease-release-failure", 12)
        {
            EndResult = FailureLease("DEVBRIDGE_LEASE_END_FAILED")
        };
        PromotionLiveVerificationResult result = Run(lease, new FakeCapabilityAdapter(Success()), 12);
        Assert(!result.Passed && result.ErrorCode == "PROMOTION_LEASE_RELEASE_FAILED" &&
               lease.EndCalls == 1,
            "lease release failure did not block live verification");
    }

    public static void AcquireFailureDoesNotCallLiveVerification()
    {
        var lease = new FakeLeaseAdapter
        {
            BeginResult = FailureLease("DEVBRIDGE_LEASE_CONTENDED")
        };
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12);
        Assert(!result.Passed && result.ErrorCode == "DEVBRIDGE_LEASE_CONTENDED" &&
               lease.EndCalls == 0 && capabilities.Calls == 0,
            "lease acquisition failure incorrectly entered live verification");
    }

    public static void MissingWorkflowIdentityFailsBeforeLease()
    {
        var lease = new FakeLeaseAdapter("lease-never", 12);
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = new PromotionLeaseOrchestrator(lease, capabilities)
            .VerifyCapabilitiesAsync(" ", 12)
            .GetAwaiter()
            .GetResult();
        Assert(!result.Passed && result.ErrorCode == "PROMOTION_WORKFLOW_ID_MISSING" &&
               lease.BeginCalls == 0 && capabilities.Calls == 0,
            "missing promotion workflow identity was not rejected before lease acquisition");
    }

    public static void OtherWorkflowCannotReuseLease()
    {
        var lease = new FakeLeaseAdapter("lease-owner", 12);
        var capabilities = new FakeCapabilityAdapter(Success());
        PromotionLiveVerificationResult result = Run(lease, capabilities, 12, "workflow-owner");
        Assert(result.Passed && lease.Workflows.All(value => value == "workflow-owner") &&
               capabilities.Workflows.All(value => value == "workflow-owner"),
            "promotion attempted to reuse a lease across workflow owners");
    }

    public static void ReportsLeaseGenerationAndStage()
    {
        var lease = new FakeLeaseAdapter("lease-evidence", 12);
        PromotionLiveVerificationResult result = Run(lease, new FakeCapabilityAdapter(Success()), 12);
        Assert(result.Passed && result.LeaseId == "lease-evidence" && result.Generation == 12 &&
               result.Stage == "capabilities-check",
            "promotion live evidence omitted lease generation or operation stage");
    }

    private static PromotionLiveVerificationResult Run(
        FakeLeaseAdapter lease,
        FakeCapabilityAdapter capabilities,
        int generation,
        string workflow = "promotion-test") =>
        new PromotionLeaseOrchestrator(lease, capabilities)
            .VerifyCapabilitiesAsync(workflow, generation)
            .GetAwaiter()
            .GetResult();

    private static DevBridgeCapabilityDiscoveryResult Success() =>
        new(new DevBridgeCapabilityStatus(DevBridgeCapabilityOutcome.Success), [], 0, false);

    private static DevBridgeCapabilityDiscoveryResult Failure(string code, int? generation = null) =>
        new(
            new DevBridgeCapabilityStatus(
                DevBridgeCapabilityOutcome.Unavailable,
                code,
                "live verification failed",
                Evidence: generation is null ? null : new DevBridgeFailureEvidence(Generation: generation)),
            [],
            0,
            false);

    private static DevBridgeLeaseResult Lease(string id, int generation) =>
        new(new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success), id, generation);

    private static DevBridgeLeaseResult FailureLease(string code) =>
        new(new DevBridgeAdapterStatus(DevBridgeOutcomeKind.DevBridgeRefusal, code, code), null, null);

    private sealed class FakeLeaseAdapter : IDevBridgeLeaseAdapter
    {
        public FakeLeaseAdapter(string id = "lease-default", int generation = 12)
        {
            BeginResult = Lease(id, generation);
        }

        public Queue<DevBridgeLeaseResult> BeginResults { get; } = [];
        public DevBridgeLeaseResult BeginResult { get; init; }
        public DevBridgeLeaseResult? EndResult { get; init; }
        public int BeginCalls { get; private set; }
        public int EndCalls { get; private set; }
        public List<string?> Workflows { get; } = [];

        public Task<DevBridgeLeaseResult> BeginLeaseAsync(string? workflowId, CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            Workflows.Add(workflowId);
            return Task.FromResult(BeginResults.Count == 0 ? BeginResult : BeginResults.Dequeue());
        }

        public Task<DevBridgeLeaseResult> RenewLeaseAsync(string leaseId, string? workflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lease(leaseId, 12));

        public Task<DevBridgeLeaseResult> EndLeaseAsync(string leaseId, string? workflowId, CancellationToken cancellationToken = default)
        {
            EndCalls++;
            Workflows.Add(workflowId);
            return Task.FromResult(EndResult ?? Lease(leaseId, 12));
        }
    }

    private sealed class FakeCapabilityAdapter : IDevBridgeCapabilityAdapter
    {
        private readonly Queue<DevBridgeCapabilityDiscoveryResult> results = [];
        public FakeCapabilityAdapter(params DevBridgeCapabilityDiscoveryResult[] results)
        {
            foreach (DevBridgeCapabilityDiscoveryResult result in results)
            {
                this.results.Enqueue(result);
            }
        }

        public Exception? Throw { get; init; }
        public int Calls { get; private set; }
        public List<string?> LeaseIds { get; } = [];
        public List<string?> Workflows { get; } = [];

        public Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
            DevBridgeCapabilityQuery query,
            CancellationToken cancellationToken = default) =>
            DiscoverAsync(query, null, null, cancellationToken);

        public Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
            DevBridgeCapabilityQuery query,
            string? workflowId,
            string? leaseId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LeaseIds.Add(leaseId);
            Workflows.Add(workflowId);
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult(results.Count == 0 ? Success() : results.Dequeue());
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
