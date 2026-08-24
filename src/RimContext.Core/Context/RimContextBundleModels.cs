using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimContext.Core.Context;

public static class RimContextBundleSchema
{
    public const string Current = "rimctx-bundle/v1";
}

public static class RimContextBundleStatuses
{
    public const string Available = "available";
    public const string Unknown = "unknown";
    public const string Unavailable = "unavailable";
    public const string Stale = "stale";
}

public static class RimContextDecisionActions
{
    public const string Run = "RUN";
    public const string Reuse = "REUSE";
    public const string Skip = "SKIP";
    public const string Invalidate = "INVALIDATE";
    public const string Retry = "RETRY";
    public const string Block = "BLOCK";
}

public sealed record RimContextBundleRequest(
    string? RootPath = null,
    string? StorePath = null,
    IReadOnlyList<string>? AssemblyRoots = null,
    bool Verbose = false,
    DateTimeOffset? NowUtc = null,
    int MaxDecisions = 32,
    int MaxRecentExecutions = 16,
    int MaxFailures = 16,
    int MaxExtensions = 32);

public sealed record RimContextProviderRequest(
    string RootPath,
    string? StorePath,
    IReadOnlyList<string> AssemblyRoots,
    bool Verbose,
    DateTimeOffset NowUtc,
    int MaxDecisions,
    int MaxRecentExecutions,
    int MaxFailures,
    int MaxExtensions);

public interface IRimContextBundleProvider
{
    string Id { get; }

    ValueTask<RimContextProviderSnapshot> CollectAsync(
        RimContextProviderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RimContextProviderSnapshot(
    string ProviderId,
    DateTimeOffset ObservedAtUtc,
    RimContextSection<RimContextTopology>? Topology = null,
    RimContextSection<RimContextRepositoryState>? Repository = null,
    RimContextSection<RimContextEnvironmentState>? Environment = null,
    RimContextSection<RimContextDeploymentState>? Deployment = null,
    RimContextSection<RimContextRuntimeState>? Runtime = null,
    RimContextSection<RimContextTestingState>? Testing = null,
    IReadOnlyList<RimContextExecution>? RecentExecutions = null,
    IReadOnlyList<RimContextFailure>? Failures = null,
    RimContextSection<RimContextEfficiencyMetrics>? Efficiency = null,
    IReadOnlyList<RimContextDecision>? Decisions = null,
    IReadOnlyList<RimContextExtension>? Extensions = null,
    IReadOnlyList<RimContextRepositoryState>? RelatedRepositories = null);

public sealed record RimContextSection<T>
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public T? Value { get; init; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonPropertyName("observedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ObservedAtUtc { get; init; }

    [JsonPropertyName("ageSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AgeSeconds { get; init; }

    [JsonPropertyName("stale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stale { get; init; }

    [JsonPropertyName("staleAfterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StaleAfterSeconds { get; init; }

    [JsonPropertyName("reasonCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }
}

public sealed record RimContextBundle
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimContextBundleSchema.Current;

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("snapshotStatus")]
    public string SnapshotStatus { get; init; } = "partial";

    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    [JsonPropertyName("staleReasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StaleReasons { get; init; }

    [JsonPropertyName("agentSummary")]
    public required RimContextAgentSummary AgentSummary { get; init; }

    [JsonPropertyName("ownership")]
    public IReadOnlyList<RimContextOwnership> Ownership { get; init; } = [];

    [JsonPropertyName("topology")]
    public required RimContextSection<RimContextTopology> Topology { get; init; }

    [JsonPropertyName("repository")]
    public required RimContextSection<RimContextRepositoryState> Repository { get; init; }

    [JsonPropertyName("relatedRepositories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimContextRepositoryState>? RelatedRepositories { get; init; }

    [JsonPropertyName("environment")]
    public required RimContextSection<RimContextEnvironmentState> Environment { get; init; }

    [JsonPropertyName("deployment")]
    public required RimContextSection<RimContextDeploymentState> Deployment { get; init; }

    [JsonPropertyName("runtime")]
    public required RimContextSection<RimContextRuntimeState> Runtime { get; init; }

    [JsonPropertyName("testing")]
    public required RimContextSection<RimContextTestingState> Testing { get; init; }

    [JsonPropertyName("recentExecutions")]
    public IReadOnlyList<RimContextExecution> RecentExecutions { get; init; } = [];

    [JsonPropertyName("failures")]
    public IReadOnlyList<RimContextFailure> Failures { get; init; } = [];

    [JsonPropertyName("efficiency")]
    public required RimContextSection<RimContextEfficiencyMetrics> Efficiency { get; init; }

    [JsonPropertyName("decisions")]
    public IReadOnlyList<RimContextDecision> Decisions { get; init; } = [];

    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimContextExtension>? Extensions { get; init; }
}

public sealed record RimContextAgentSummary
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("actionRequired")]
    public IReadOnlyList<string> ActionRequired { get; init; } = [];

    [JsonPropertyName("blockers")]
    public IReadOnlyList<string> Blockers { get; init; } = [];

    [JsonPropertyName("reusableEvidence")]
    public IReadOnlyList<string> ReusableEvidence { get; init; } = [];

    [JsonPropertyName("ownership")]
    public IReadOnlyList<string> Ownership { get; init; } = [];

    [JsonPropertyName("meaningfulChanges")]
    public IReadOnlyList<string> MeaningfulChanges { get; init; } = [];

    [JsonPropertyName("recentFailures")]
    public IReadOnlyList<string> RecentFailures { get; init; } = [];

    [JsonPropertyName("deploymentCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentCorrespondence { get; init; }
}

public sealed record RimContextTopology
{
    [JsonPropertyName("components")]
    public IReadOnlyList<RimContextComponent> Components { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<RimContextDependency> Dependencies { get; init; } = [];
}

public sealed record RimContextComponent
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("repository")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repository { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonPropertyName("commit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; init; }

    [JsonPropertyName("localPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalPath { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record RimContextDependency
{
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public sealed record RimContextRepositoryState
{
    [JsonPropertyName("component")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Component { get; init; }

    [JsonPropertyName("identity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identity { get; init; }

    [JsonPropertyName("localPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalPath { get; init; }

    [JsonPropertyName("branch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; init; }

    [JsonPropertyName("headSha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeadSha { get; init; }

    [JsonPropertyName("upstreamSha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamSha { get; init; }

    [JsonPropertyName("upstreamBranch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamBranch { get; init; }

    [JsonPropertyName("ahead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ahead { get; init; }

    [JsonPropertyName("behind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Behind { get; init; }

    [JsonPropertyName("dirty")]
    public bool? Dirty { get; init; }

    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("changedFiles")]
    public IReadOnlyList<RimContextChangedFile> ChangedFiles { get; init; } = [];

    [JsonPropertyName("generatedFiles")]
    public IReadOnlyList<RimContextChangedFile> GeneratedFiles { get; init; } = [];
}

public sealed record RimContextChangedFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("untracked")]
    public bool Untracked { get; init; }

    [JsonPropertyName("originalPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalPath { get; init; }
}

public sealed record RimContextEnvironmentState
{
    [JsonPropertyName("os")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Os { get; init; }

    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; init; }

    [JsonPropertyName("compiler")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Compiler { get; init; }

    [JsonPropertyName("rimWorldVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimWorldVersion { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<RimContextToolVersion> Tools { get; init; } = [];

    [JsonPropertyName("configuration")]
    public IReadOnlyList<RimContextSetting> Configuration { get; init; } = [];

    [JsonPropertyName("secretsExcluded")]
    public bool SecretsExcluded { get; init; } = true;
}

public sealed record RimContextToolVersion
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public sealed record RimContextSetting
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed record RimContextDeploymentState
{
    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("buildArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildArtifactFingerprint { get; init; }

    [JsonPropertyName("deployedArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeployedArtifactFingerprint { get; init; }

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    [JsonPropertyName("correspondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Correspondence { get; init; }

    [JsonPropertyName("deploymentDecision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentDecision { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("runtimeArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeArtifactFingerprint { get; init; }

    [JsonPropertyName("runtimeArtifactStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeArtifactStatus { get; init; }

    [JsonPropertyName("runtimeGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RuntimeGeneration { get; init; }

    [JsonPropertyName("sourceCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceCorrespondence { get; init; }

    [JsonPropertyName("buildDeploymentCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildDeploymentCorrespondence { get; init; }

    [JsonPropertyName("deploymentRuntimeCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentRuntimeCorrespondence { get; init; }

    [JsonPropertyName("runtimeLaunchId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeLaunchId { get; init; }
}

public sealed record RimContextRuntimeState
{
    [JsonPropertyName("rimWorldRunning")]
    public bool? RimWorldRunning { get; init; }

    [JsonPropertyName("processId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("bridgeStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BridgeStatus { get; init; }

    [JsonPropertyName("mapState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MapState { get; init; }

    [JsonPropertyName("gameState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameState { get; init; }

    [JsonPropertyName("quicktestState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuicktestState { get; init; }

    [JsonPropertyName("leaseOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseOwner { get; init; }

    [JsonPropertyName("leaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseId { get; init; }

    [JsonPropertyName("launchId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LaunchId { get; init; }

    [JsonPropertyName("restartPending")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RestartPending { get; init; }

    [JsonPropertyName("targetGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetGeneration { get; init; }

    [JsonPropertyName("leaseState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseState { get; init; }

    [JsonPropertyName("activeLeaseCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ActiveLeaseCount { get; init; }

    [JsonPropertyName("maintenanceReady")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaintenanceReady { get; init; }

    [JsonPropertyName("currentGenerationTrust")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentGenerationTrust { get; init; }

    [JsonPropertyName("runtimeArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeArtifactFingerprint { get; init; }

    [JsonPropertyName("runtimeArtifactStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeArtifactStatus { get; init; }

    [JsonPropertyName("failureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureCode { get; init; }
}

public sealed record RimContextTestingState
{
    [JsonPropertyName("availableSuites")]
    public IReadOnlyList<string> AvailableSuites { get; init; } = [];

    [JsonPropertyName("availableTests")]
    public IReadOnlyList<string> AvailableTests { get; init; } = [];

    [JsonPropertyName("selectedSuites")]
    public IReadOnlyList<string> SelectedSuites { get; init; } = [];

    [JsonPropertyName("executedSuites")]
    public IReadOnlyList<string> ExecutedSuites { get; init; } = [];

    [JsonPropertyName("reusedSuites")]
    public IReadOnlyList<string> ReusedSuites { get; init; } = [];

    [JsonPropertyName("skippedSuites")]
    public IReadOnlyList<string> SkippedSuites { get; init; } = [];

    [JsonPropertyName("selectedTests")]
    public IReadOnlyList<string> SelectedTests { get; init; } = [];

    [JsonPropertyName("executedTests")]
    public IReadOnlyList<string> ExecutedTests { get; init; } = [];

    [JsonPropertyName("reusedTests")]
    public IReadOnlyList<string> ReusedTests { get; init; } = [];

    [JsonPropertyName("skippedTests")]
    public IReadOnlyList<string> SkippedTests { get; init; } = [];

    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Policy { get; init; }

    [JsonPropertyName("latestEvidence")]
    public IReadOnlyList<RimContextEvidenceReference> LatestEvidence { get; init; } = [];

    [JsonPropertyName("invalidatedEvidence")]
    public IReadOnlyList<RimContextEvidenceReference> InvalidatedEvidence { get; init; } = [];

    [JsonPropertyName("benchmarkSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimContextBenchmarkSummary? BenchmarkSummary { get; init; }

    [JsonPropertyName("cacheStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CacheStatus { get; init; }

    [JsonPropertyName("invalidationReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvalidationReason { get; init; }

    [JsonPropertyName("additionalValidationRequired")]
    public bool? AdditionalValidationRequired { get; init; }

    [JsonPropertyName("latestResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestResult { get; init; }

    [JsonPropertyName("latestSourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestSourceFingerprint { get; init; }

    [JsonPropertyName("latestBuildArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestBuildArtifactFingerprint { get; init; }

    [JsonPropertyName("latestDeploymentArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestDeploymentArtifactFingerprint { get; init; }

    [JsonPropertyName("latestTransactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestTransactionId { get; init; }

    [JsonPropertyName("latestGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LatestGeneration { get; init; }

    [JsonPropertyName("latestDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LatestDurationMs { get; init; }

    [JsonPropertyName("infrastructureFailure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InfrastructureFailure { get; init; }

    [JsonPropertyName("retryable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }
}

public sealed record RimContextExecution
{
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("startedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAtUtc { get; init; }

    [JsonPropertyName("endedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndedAtUtc { get; init; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    [JsonPropertyName("inputFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFingerprint { get; init; }

    [JsonPropertyName("executionMode")]
    public required string ExecutionMode { get; init; }

    [JsonPropertyName("reasonCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; init; }

    [JsonPropertyName("failureKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureKind { get; init; }

    [JsonPropertyName("infrastructure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Infrastructure { get; init; }

    [JsonPropertyName("retryable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }
}

public sealed record RimContextFailure
{
    [JsonPropertyName("signatureCode")]
    public required string SignatureCode { get; init; }

    [JsonPropertyName("originatingComponent")]
    public required string OriginatingComponent { get; init; }

    [JsonPropertyName("classification")]
    public required string Classification { get; init; }

    [JsonPropertyName("rootCause")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootCause { get; init; }

    [JsonPropertyName("recommendedAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedAction { get; init; }

    [JsonPropertyName("retryAppropriate")]
    public bool? RetryAppropriate { get; init; }

    [JsonPropertyName("observedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ObservedAtUtc { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("retryAfterStateChange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RetryAfterStateChange { get; init; }

    [JsonPropertyName("requiresSourceModification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresSourceModification { get; init; }

    [JsonPropertyName("infrastructureOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InfrastructureOnly { get; init; }

    [JsonPropertyName("evidenceInvalidationEffect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceInvalidationEffect { get; init; }

    [JsonPropertyName("knowledge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimContextFailureKnowledge? Knowledge { get; init; }
}

public sealed record RimContextFailureKnowledge
{
    [JsonPropertyName("signatureCode")]
    public required string SignatureCode { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("knownCause")]
    public required string KnownCause { get; init; }

    [JsonPropertyName("recommendedAction")]
    public required string RecommendedAction { get; init; }

    [JsonPropertyName("inappropriateActions")]
    public IReadOnlyList<string> InappropriateActions { get; init; } = [];

    [JsonPropertyName("evidenceImpact")]
    public required string EvidenceImpact { get; init; }

    [JsonPropertyName("resolutionProvenance")]
    public required string ResolutionProvenance { get; init; }

    [JsonPropertyName("matchReason")]
    public required string MatchReason { get; init; }
}

public sealed record RimContextEfficiencyMetrics
{
    [JsonPropertyName("buildMs")]
    public long? BuildMs { get; init; }

    [JsonPropertyName("deploymentMs")]
    public long? DeploymentMs { get; init; }

    [JsonPropertyName("staticTestMs")]
    public long? StaticTestMs { get; init; }

    [JsonPropertyName("runtimeTestMs")]
    public long? RuntimeTestMs { get; init; }

    [JsonPropertyName("totalWorkflowMs")]
    public long? TotalWorkflowMs { get; init; }

    [JsonPropertyName("cacheHits")]
    public int? CacheHits { get; init; }

    [JsonPropertyName("cacheMisses")]
    public int? CacheMisses { get; init; }

    [JsonPropertyName("rimWorldLaunches")]
    public int? RimWorldLaunches { get; init; }

    [JsonPropertyName("rimWorldRestarts")]
    public int? RimWorldRestarts { get; init; }

    [JsonPropertyName("retries")]
    public int? Retries { get; init; }

    [JsonPropertyName("buildCount")]
    public int? BuildCount { get; init; }

    [JsonPropertyName("deploymentCount")]
    public int? DeploymentCount { get; init; }

    [JsonPropertyName("testCount")]
    public int? TestCount { get; init; }

    [JsonPropertyName("executedTestCount")]
    public int? ExecutedTestCount { get; init; }

    [JsonPropertyName("reusedEvidenceCount")]
    public int? ReusedEvidenceCount { get; init; }

    [JsonPropertyName("invalidatedEvidenceCount")]
    public int? InvalidatedEvidenceCount { get; init; }

    [JsonPropertyName("expensiveOperationCount")]
    public int? ExpensiveOperationCount { get; init; }

    [JsonPropertyName("observedPerformance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimContextObservedPerformanceSummary? ObservedPerformance { get; init; }

    [JsonPropertyName("benchmarkSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimContextBenchmarkSummary? BenchmarkSummary { get; init; }
}
public sealed record RimContextObservedPerformanceSummary
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; init; }

    [JsonPropertyName("medianWorkflowDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MedianWorkflowDurationMs { get; init; }

    [JsonPropertyName("p90WorkflowDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? P90WorkflowDurationMs { get; init; }

    [JsonPropertyName("validationReuseRate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ValidationReuseRate { get; init; }

    [JsonPropertyName("averageExpensiveOperations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AverageExpensiveOperations { get; init; }

    [JsonPropertyName("runtimeLaunchesPerRuntimeWorkflow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RuntimeLaunchesPerRuntimeWorkflow { get; init; }

    [JsonPropertyName("infrastructureRetryRate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InfrastructureRetryRate { get; init; }

    [JsonPropertyName("topFailureClassification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TopFailureClassification { get; init; }
}


public sealed record RimContextBenchmarkSummary
{
    [JsonPropertyName("baselineVersion")]
    public required string BaselineVersion { get; init; }

    [JsonPropertyName("scenarioCount")]
    public int ScenarioCount { get; init; }

    [JsonPropertyName("passedScenarioCount")]
    public int PassedScenarioCount { get; init; }

    [JsonPropertyName("regressionCount")]
    public int RegressionCount { get; init; }

    [JsonPropertyName("totalExpensiveOperations")]
    public int TotalExpensiveOperations { get; init; }

    [JsonPropertyName("reusableEvidenceCount")]
    public int ReusableEvidenceCount { get; init; }

    [JsonPropertyName("invalidatedEvidenceCount")]
    public int InvalidatedEvidenceCount { get; init; }
}

public sealed record RimContextEvidenceReference
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("validationKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidationKind { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; init; }

    [JsonPropertyName("reusable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Reusable { get; init; }

    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("buildArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildArtifactFingerprint { get; init; }

    [JsonPropertyName("deploymentArtifactFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentArtifactFingerprint { get; init; }

    [JsonPropertyName("suiteId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SuiteId { get; init; }

    [JsonPropertyName("testIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TestIds { get; init; }

    [JsonPropertyName("runtimeGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RuntimeGeneration { get; init; }

    [JsonPropertyName("requiresRuntimeGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresRuntimeGeneration { get; init; }

    [JsonPropertyName("deploymentCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentCorrespondence { get; init; }

    [JsonPropertyName("recordedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RecordedAtUtc { get; init; }
}

public sealed record RimContextDecision
{
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("reasonCode")]
    public required string ReasonCode { get; init; }

    [JsonPropertyName("explanation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Explanation { get; init; }

    [JsonPropertyName("relevantChangedInputs")]
    public IReadOnlyList<string> RelevantChangedInputs { get; init; } = [];

    [JsonPropertyName("previousEvidence")]
    public IReadOnlyList<RimContextEvidenceReference> PreviousEvidence { get; init; } = [];

    [JsonPropertyName("evidenceReused")]
    public IReadOnlyList<RimContextEvidenceReference> EvidenceReused { get; init; } = [];

    [JsonPropertyName("evidenceInvalidated")]
    public IReadOnlyList<RimContextEvidenceReference> EvidenceInvalidated { get; init; } = [];

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cost { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("observedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public sealed record RimContextOwnership
{
    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("responsibility")]
    public required string Responsibility { get; init; }
}

public sealed record RimContextExtension
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public required JsonElement Value { get; init; }
}

public static class RimContextOwnershipCatalog
{
    public static IReadOnlyList<RimContextOwnership> Default { get; } =
    [
        new()
        {
            Fact = "aggregation",
            Owner = "RimContext",
            Responsibility = "Aggregate bounded provider snapshots and expose the versioned bundle."
        },
        new()
        {
            Fact = "repository",
            Owner = "Git",
            Responsibility = "Authoritative repository history, branch, and working-tree state."
        },
        new()
        {
            Fact = "static-index",
            Owner = "RimContext",
            Responsibility = "Source, Defs, Harmony, project, dependency, and affected-impact facts."
        },
        new()
        {
            Fact = "test-selection",
            Owner = "RimTest/RimLiaison",
            Responsibility = "Affected-test policy, evidence reuse, invalidation, and selection decisions."
        },
        new()
        {
            Fact = "deployment",
            Owner = "DevBridge2",
            Responsibility = "Build transaction, artifact identity, deployment, generations, and freshness proof."
        },
        new()
        {
            Fact = "runtime",
            Owner = "DevBridge2/RimBridgeServer",
            Responsibility = "RimWorld process, generation, bridge, map, game, and lease state."
        },
        new()
        {
            Fact = "failures",
            Owner = "RimError/RimLiaison",
            Responsibility = "Failure classification, scoped diagnostics, and bounded recovery references."
        },
        new()
        {
            Fact = "orchestration",
            Owner = "RimLiaison",
            Responsibility = "Coordinate owner operations and project compact execution evidence."
        }
    ];
}

public static class RimContextBundleJson
{
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions VerboseOptions = CreateOptions(true);

    public static string Serialize(
        RimContextBundle bundle,
        bool verbose = false,
        int? maxBytes = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The context output limit must be positive.");
        }

        JsonSerializerOptions options = verbose ? VerboseOptions : CompactOptions;
        JsonNode node = JsonSerializer.SerializeToNode(bundle, options) ?? new JsonObject();
        if (!verbose)
        {
            RemoveEmptyArrays(node);
        }

        string json = node.ToJsonString(options);
        if (maxBytes is null || System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes.Value)
        {
            return json;
        }

        if (node is JsonObject root)
        {
            root["truncated"] = true;
        }

        while (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes.Value)
        {
            (JsonArray Array, string Path, int Priority) candidate = EnumerateArrays(node)
                .Where(static item => item.Array.Count > 0)
                .OrderBy(static item => item.Priority)
                .ThenBy(static item => item.Path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate.Array is null)
            {
                break;
            }

            candidate.Array.RemoveAt(candidate.Array.Count - 1);
            if (!verbose)
            {
                RemoveEmptyArrays(node);
            }

            json = node.ToJsonString(options);
        }

        if (System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes.Value)
        {
            return json;
        }

        return new JsonObject
        {
            ["schemaVersion"] = bundle.SchemaVersion,
            ["snapshotStatus"] = "partial",
            ["stale"] = bundle.Stale,
            ["truncated"] = true,
            ["message"] = "Response exceeded --max-bytes; context arrays were omitted."
        }.ToJsonString(CompactOptions);
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };

    private static void RemoveEmptyArrays(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (string propertyName in jsonObject.Select(property => property.Key).ToArray())
            {
                if (!jsonObject.TryGetPropertyValue(propertyName, out JsonNode? value) || value is null)
                {
                    jsonObject.Remove(propertyName);
                    continue;
                }

                RemoveEmptyArrays(value);
                if (value is JsonArray array && array.Count == 0)
                {
                    jsonObject.Remove(propertyName);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? child in jsonArray.Where(static child => child is not null).ToArray())
            {
                RemoveEmptyArrays(child!);
            }
        }
    }

    private static IEnumerable<(JsonArray Array, string Path, int Priority)> EnumerateArrays(
        JsonNode node,
        string path = "")
    {
        if (node is JsonObject jsonObject)
        {
            foreach (KeyValuePair<string, JsonNode?> property in jsonObject)
            {
                if (property.Value is JsonArray array)
                {
                    string arrayPath = path.Length == 0
                        ? property.Key
                        : path + "." + property.Key;
                    yield return (array, arrayPath, ArrayPriority(property.Key));
                    foreach (var nested in EnumerateArrays(array, arrayPath))
                    {
                        yield return nested;
                    }
                }
                else if (property.Value is not null)
                {
                    foreach (var nested in EnumerateArrays(property.Value, path + "." + property.Key))
                    {
                        yield return nested;
                    }
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? child in jsonArray.Where(static child => child is not null))
            {
                foreach (var nested in EnumerateArrays(child!, path))
                {
                    yield return nested;
                }
            }
        }
    }

    private static int ArrayPriority(string propertyName) => propertyName switch
    {
        "extensions" => 0,
        "recentExecutions" => 1,
        "failures" => 2,
        "decisions" => 3,
        "generatedFiles" => 4,
        "changedFiles" => 5,
        "latestEvidence" => 6,
        "availableTests" or "availableSuites" => 7,
        "components" or "dependencies" => 20,
        "ownership" => 100,
        _ => 10
    };
}
