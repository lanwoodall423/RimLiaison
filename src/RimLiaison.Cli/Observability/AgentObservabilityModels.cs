using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Observability;

public static partial class AgentObservabilitySchemas
{
    public const string Event = "rimliaison-agent-event/v1";
    public const string Issue = "rimliaison-agent-issue/v1";
    public const string Agent = "rimliaison-agent-snapshot/v1";
    public const string Bundle = "rimliaison-agent-diagnostic-bundle/v2";
    public const string LegacyBundle = "rimliaison-agent-diagnostic-bundle/v1";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DevelopmentStage
{
    Analysis,
    Research,
    Implementation,
    Testing,
    Packaging,
    Complete
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentStatus
{
    Created,
    Running,
    Waiting,
    Completed,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentCompletionState
{
    None,
    Succeeded,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentIssueCategory
{
    Error,
    Retry,
    Rework,
    ToolLimitation,
    Stall,
    RedundantWork,
    ContextIssue,
    IntegrationIssue,
    Workaround
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentIssueSeverity
{
    Info,
    Warning,
    Error
}

public static class AgentEventTypes
{
    public const string AgentCreated = "agent.created";
    public const string AgentStarted = "agent.started";
    public const string StageChanged = "stage.changed";
    public const string FileInspected = "file.inspected";
    public const string FileModified = "file.modified";
    public const string FileCreated = "file.created";
    public const string FileDeleted = "file.deleted";
    public const string SearchStarted = "search.started";
    public const string SearchCompleted = "search.completed";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string ToolFailed = "tool.failed";
    public const string ToolException = "tool.exception";
    public const string CommandStarted = "command.started";
    public const string CommandCompleted = "command.completed";
    public const string CommandFailed = "command.failed";
    public const string CommandTimeout = "command.timeout";
    public const string BuildStarted = "build.started";
    public const string BuildSucceeded = "build.succeeded";
    public const string BuildFailed = "build.failed";
    public const string TestStarted = "test.started";
    public const string TestPassed = "test.passed";
    public const string TestFailed = "test.failed";
    public const string SuiteCompleted = "test.suite.completed";
    public const string ValidationEvidenceRecorded = "test.evidence.recorded";
    public const string ValidationEvidenceDecision = "test.evidence.decision";
    public const string PublicationChecked = "git.publication.checked";
    public const string PackagingStarted = "packaging.started";
    public const string PackagingCompleted = "packaging.completed";
    public const string AgentWaiting = "agent.waiting";
    public const string AgentResumed = "agent.resumed";
    public const string AgentCompleted = "agent.completed";
    public const string AgentFailed = "agent.failed";
    public const string RetryStarted = "retry.started";
    public const string RetryCompleted = "retry.completed";
    public const string WorkaroundApplied = "workaround.applied";
    public const string ToolLimitation = "tool.limitation";
    public const string ContextIssue = "context.issue";
    public const string IntegrationFailed = "integration.failed";
    public const string RecoveryCompleted = "recovery.completed";
    public const string BuildDiagnostics = "diagnostic.build";
}

public sealed record AgentEventRequest(
    string RunId,
    string AgentId,
    string ModId,
    DevelopmentStage Stage,
    string Type,
    string Summary,
    object? Data = null,
    long? Timestamp = null,
    string? TraceId = null,
    string? SpanId = null);

public sealed record AgentEvent
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = AgentObservabilitySchemas.Event;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("stage")]
    public DevelopmentStage Stage { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("traceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }

    [JsonPropertyName("spanId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpanId { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}

public sealed record AgentSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = AgentObservabilitySchemas.Agent;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("modName")]
    public required string ModName { get; init; }

    [JsonPropertyName("status")]
    public AgentStatus Status { get; init; } = AgentStatus.Created;

    [JsonPropertyName("currentStage")]
    public DevelopmentStage CurrentStage { get; init; } = DevelopmentStage.Analysis;

    [JsonPropertyName("currentActivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentActivity { get; init; }

    [JsonPropertyName("startTime")]
    public long StartTime { get; init; }

    [JsonPropertyName("completedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompletedAt { get; init; }

    [JsonPropertyName("completionState")]
    public AgentCompletionState CompletionState { get; init; } = AgentCompletionState.None;

    [JsonPropertyName("failureState")]
    public bool FailureState { get; init; }

    [JsonPropertyName("failureSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureSummary { get; init; }
}

public sealed record AgentIssue
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = AgentObservabilitySchemas.Issue;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("category")]
    public AgentIssueCategory Category { get; init; }

    [JsonPropertyName("severity")]
    public AgentIssueSeverity Severity { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("eventIds")]
    public IReadOnlyList<string> EventIds { get; init; } = [];

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DevelopmentStage? Stage { get; init; }

    [JsonPropertyName("relatedFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RelatedFiles { get; init; }

    [JsonPropertyName("relatedToolCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RelatedToolCalls { get; init; }

    [JsonPropertyName("relatedCommands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RelatedCommands { get; init; }

    [JsonPropertyName("recovered")]
    public bool Recovered { get; init; }

    [JsonPropertyName("resolutionEventId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolutionEventId { get; init; }

    [JsonPropertyName("traceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }

    [JsonPropertyName("spanIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SpanIds { get; init; }

    [JsonPropertyName("operationKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationKey { get; init; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; init; }

    [JsonPropertyName("occurrences")]
    public int Occurrences { get; init; } = 1;
}

public sealed record AgentObservabilityView(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyList<AgentIssue> Issues);

public sealed record AgentDiagnosticMod(
    string ModId,
    string ModName,
    string AgentId,
    string RunId);

public sealed record AgentTraceReference(
    string? TraceId,
    IReadOnlyList<string> SpanIds);

public sealed record AgentRecoveryStep(
    string EventId,
    long Timestamp,
    string Type,
    string Summary,
    bool Successful);

public static class AgentDiagnosticCompletenessStatuses
{
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";
}

public sealed record AgentDiagnosticCompleteness(
    string Status,
    IReadOnlyList<string> MissingEvidence)
{
    [JsonIgnore]
    public bool IsComplete => string.Equals(
        Status,
        AgentDiagnosticCompletenessStatuses.Complete,
        StringComparison.Ordinal);
}

public sealed record AgentDiagnosticEvidenceReference(
    string Id,
    string Kind,
    int CharacterCount,
    bool Truncated);

public sealed record AgentDiagnosticEvidence(
    string Id,
    string Kind,
    string Content,
    bool Truncated);

public sealed record AgentDiagnosticCommandEvidence(
    string EventId,
    string? Command,
    string? Tool,
    string? WorkingDirectory,
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    string? Stdout,
    string? Stderr,
    string? DiagnosticOutput,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool DiagnosticOutputTruncated,
    string? OperationKey = null,
    string? TransactionId = null,
    string? WorkflowId = null);

public sealed record AgentDiagnosticBuildEvidence(
    string EventId,
    string? Project,
    string? SourceProject,
    string? Configuration,
    string? Command,
    string? WorkingDirectory,
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    string? Output,
    string? ErrorOutput,
    string? DiagnosticOutput,
    bool OutputTruncated,
    bool ErrorOutputTruncated,
    bool DiagnosticOutputTruncated,
    string? TransactionId,
    string? WorkflowId,
    string? SourceFingerprint,
    string? BuiltArtifactSha256,
    string? DeployedArtifactSha256,
    string? DeploymentDecision,
    string? StagingPath,
    bool? LoadedArtifactFreshnessProven,
    string? FreshnessState,
    string? ErrorCode,
    string? FailureMessage);

public sealed record AgentDiagnosticToolOperationEvidence(
    string EventId,
    string? Tool,
    string? OperationKey,
    string? OperationType,
    string? Outcome,
    string? ErrorCode,
    string Summary,
    string? TransactionId = null,
    string? WorkflowId = null);

public sealed record AgentDiagnosticCorrelation(
    string Kind,
    string Value,
    IReadOnlyList<string> EventIds);

public sealed record AgentDiagnosticRepositoryState(
    string? RepositoryRoot,
    string? Project,
    string? SourceProject,
    string? Configuration,
    string? SourceFingerprint,
    string? Branch,
    string? CommitSha,
    IReadOnlyList<string> ChangedFiles);

public sealed record AgentDiagnosticEnvironmentState(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> ToolVersions);

public sealed record AgentDiagnosticBundle
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = AgentObservabilitySchemas.Bundle;

    [JsonPropertyName("issueIds")]
    public IReadOnlyList<string> IssueIds { get; init; } = [];

    [JsonPropertyName("selectedIssueIds")]
    public IReadOnlyList<string> SelectedIssueIds { get; init; } = [];

    [JsonPropertyName("selectedIssues")]
    public IReadOnlyList<AgentIssue> SelectedIssues { get; init; } = [];

    [JsonPropertyName("correlatedIssueIds")]
    public IReadOnlyList<string> CorrelatedIssueIds { get; init; } = [];

    [JsonPropertyName("correlatedIssues")]
    public IReadOnlyList<AgentIssue> CorrelatedIssues { get; init; } = [];

    [JsonPropertyName("mods")]
    public IReadOnlyList<AgentDiagnosticMod> Mods { get; init; } = [];

    [JsonPropertyName("issues")]
    public IReadOnlyList<AgentIssue> Issues { get; init; } = [];

    [JsonPropertyName("supportingEvents")]
    public IReadOnlyList<AgentEvent> SupportingEvents { get; init; } = [];

    [JsonPropertyName("toolCalls")]
    public IReadOnlyList<string> ToolCalls { get; init; } = [];

    [JsonPropertyName("commands")]
    public IReadOnlyList<string> Commands { get; init; } = [];

    [JsonPropertyName("commandEvidence")]
    public IReadOnlyList<AgentDiagnosticCommandEvidence> CommandEvidence { get; init; } = [];

    [JsonPropertyName("buildEvidence")]
    public IReadOnlyList<AgentDiagnosticBuildEvidence> BuildEvidence { get; init; } = [];

    [JsonPropertyName("toolOperations")]
    public IReadOnlyList<AgentDiagnosticToolOperationEvidence> ToolOperations { get; init; } = [];

    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];

    [JsonPropertyName("relatedFiles")]
    public IReadOnlyList<string> RelatedFiles { get; init; } = [];

    [JsonPropertyName("recoveryPath")]
    public IReadOnlyList<AgentRecoveryStep> RecoveryPath { get; init; } = [];

    [JsonPropertyName("traces")]
    public IReadOnlyList<AgentTraceReference> Traces { get; init; } = [];

    [JsonPropertyName("correlations")]
    public IReadOnlyList<AgentDiagnosticCorrelation> Correlations { get; init; } = [];

    [JsonPropertyName("repository")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentDiagnosticRepositoryState? Repository { get; init; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentDiagnosticEnvironmentState? Environment { get; init; }

    [JsonPropertyName("completeness")]
    public AgentDiagnosticCompleteness Completeness { get; init; } =
        new(
            AgentDiagnosticCompletenessStatuses.Incomplete,
            ["completeness.not-recorded"]);
}

public enum AgentObservabilityNotificationKind
{
    EventAppended,
    IssueChanged,
    AgentChanged
}

public sealed record AgentObservabilityNotification(
    AgentObservabilityNotificationKind Kind,
    AgentEvent? Event = null,
    AgentIssue? Issue = null,
    AgentSnapshot? Agent = null);

public sealed class AgentObservabilityOptions
{
    public int MaximumEvents { get; init; } = 20_000;
    public int MaximumIssues { get; init; } = 5_000;
    public int MaximumAgents { get; init; } = 2_000;
    public int MaximumIssueEventReferences { get; init; } = 512;
    public int MaximumBundleEvidenceValues { get; init; } = 2_048;
    public int MaximumBundleSupportingEvents { get; init; } = 4_096;
    public int MaximumBundleCorrelatedIssues { get; init; } = 2_048;
    public int MaximumBundleOutputCharacters { get; init; } = 32 * 1024;
    public int MaximumPersistedBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumEventDataBytes { get; init; } = 8 * 1024;
    public int MaximumEvidenceBytes { get; init; } = 512 * 1024;
    public int MaximumEvidenceEntries { get; init; } = 2_048;
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromSeconds(30);
    public bool EnableIssueDetection { get; init; } = true;

    internal void Validate()
    {
        if (MaximumEvents <= 0 || MaximumIssues <= 0 || MaximumAgents <= 0 ||
            MaximumIssueEventReferences <= 0 || MaximumIssueEventReferences > 10_000 ||
            MaximumBundleEvidenceValues <= 0 || MaximumBundleEvidenceValues > 20_000 ||
            MaximumBundleSupportingEvents <= 0 || MaximumBundleSupportingEvents > 50_000 ||
            MaximumBundleCorrelatedIssues <= 0 || MaximumBundleCorrelatedIssues > 20_000 ||
            MaximumBundleOutputCharacters <= 0 || MaximumBundleOutputCharacters > 1_000_000 ||
            MaximumPersistedBytes <= 0 || MaximumEventDataBytes <= 0 ||
            MaximumEvidenceBytes <= 0 || MaximumEvidenceBytes > 16 * 1024 * 1024 ||
            MaximumEvidenceEntries <= 0 || MaximumEvidenceEntries > 100_000 ||
            StallThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentObservabilityOptions));
        }
    }
}

public static class AgentObservabilityJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
