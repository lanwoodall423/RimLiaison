using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Observability;

public static partial class AgentObservabilitySchemas
{
    public const string Event = "rimliaison-agent-event/v2";
    public const string Issue = "rimliaison-agent-issue/v2";
    public const string Agent = "rimliaison-agent-snapshot/v2";
    public const string LegacyEvent = "rimliaison-agent-event/v1";
    public const string LegacyIssue = "rimliaison-agent-issue/v1";
    public const string LegacyAgent = "rimliaison-agent-snapshot/v1";
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
    ValidationIncomplete,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentIssueCategory
{
    Error,
    Retry,
    Rework,
    ToolLimitation,
    CapabilityGap,
    OptionalValidationUnavailable,
    ToolingImprovement,
    ModDefect,
    ToolingFailure,
    InformationalProductionEvent,
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
    public const string ValidationCapabilityBlocked = "validation.capability.blocked";
    public const string ValidationRecommendationRecorded = "validation.recommendation.recorded";
    public const string ProductionStateChanged = "production.state.changed";
    public const string InformationalProductionEvent = "production.information";
    public const string SuiteCompleted = "test.suite.completed";
    public const string ValidationEvidenceRecorded = "test.evidence.recorded";
    public const string ValidationEvidenceDecision = "test.evidence.decision";
    public const string ExecutionPacketGenerated = "execution.packet.generated";
    public const string ExecutionPacketBypassed = "execution.packet.bypassed";
    public const string ExecutionPacketExpanded = "execution.packet.expanded";
    public const string ExecutionPacketPartiallyInvalidated = "execution.packet.partially.invalidated";
    public const string ExecutionPacketInvalidated = "execution.packet.invalidated";
    public const string PredictedImpactCreated = "impact.predicted";
    public const string ActualImpactCalculated = "impact.actual";
    public const string ValidationPlanGenerated = "validation.plan.generated";
    public const string ValidationPlanBroadened = "validation.plan.broadened";
    public const string AgentValidationAdded = "validation.agent.added";
    public const string ValidationReductionRejected = "validation.reduction.rejected";
    public const string ValidationStarted = "validation.started";
    public const string ValidationCompleted = "validation.completed";
    public const string StaleEvidenceRejected = "validation.evidence.stale";
    public const string ImpactRelationshipLearned = "impact.relationship.learned";
    public const string ImpactRelationshipPromoted = "impact.relationship.promoted";
    public const string ImpactProjectOverrideApplied = "impact.project.override";
    public const string ImpactRelationshipInvalidated = "impact.relationship.invalidated";
    public const string EvidenceReused = "validation.evidence.reused";
    public const string RuntimeEscalationRequested = "validation.runtime.escalation.requested";
    public const string RuntimeEvidenceCompleted = "validation.runtime.evidence.completed";
    public const string IdentityMismatchRejected = "validation.identity.mismatch";
    public const string FailureDetected = "failure.detected";
    public const string DiagnosisProduced = "failure.diagnosis.produced";
    public const string RemediationValidated = "remediation.validated";
    public const string RemediationPrecedentStored = "remediation.precedent.stored";
    public const string RemediationPrecedentReused = "remediation.precedent.reused";
    public const string RemediationPrecedentEligibilityChanged = "remediation.precedent.eligibility.changed";
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
    public const string ContentBlueprintCreated = ContentObservabilityEventTypes.BlueprintCreated;
    public const string ContentBlueprintUpdated = ContentObservabilityEventTypes.BlueprintUpdated;
    public const string ContentBlueprintValidated = ContentObservabilityEventTypes.BlueprintValidated;
    public const string ContentPrecedentDetected = ContentObservabilityEventTypes.PrecedentDetected;
    public const string ContentPrecedentQualified = ContentObservabilityEventTypes.PrecedentQualified;
    public const string ContentReuseSelected = ContentObservabilityEventTypes.ReuseSelected;
    public const string ContentPromotionStarted = ContentObservabilityEventTypes.PromotionStarted;
    public const string ContentPromotionCompleted = ContentObservabilityEventTypes.PromotionCompleted;
    public const string ContentPromotionRejected = ContentObservabilityEventTypes.PromotionRejected;
    public const string ContentArchetypeUsed = ContentObservabilityEventTypes.ArchetypeUsed;
    public const string ContentRegressionDetected = ContentObservabilityEventTypes.RegressionDetected;
    public const string ContentArchetypeQuarantined = ContentObservabilityEventTypes.ArchetypeQuarantined;
    public const string ContentRollbackCompleted = ContentObservabilityEventTypes.RollbackCompleted;
    public const string ContentProjectExclusionApplied = ContentObservabilityEventTypes.ProjectExclusionApplied;
    public const string ContentSourceIneligible = ContentObservabilityEventTypes.SourceIneligible;
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
    string? SpanId = null,
    string? LogicalAgentId = null,
    string? SessionId = null);

public static class ObservabilityEntityTypes
{
    public const string Mod = "mod";
    public const string Tool = "tool";
    public const string Infrastructure = "infrastructure";
    public const string Fixture = "fixture";
    public const string Test = "test";
    public const string Agent = "agent";
    public const string User = "user";
    public const string Operator = "operator";
    public const string Process = "process";
    public const string Session = "session";
    public const string Run = "run";
    public const string Activity = "activity";
    public const string Event = "event";
    public const string Runtime = "runtime";
    public const string Unknown = "unknown";
}

/// Canonical entity identity shared by agent snapshots, events, issues, and
/// navigation. Runtime/session/process/run identifiers remain separate
/// correlation namespaces and must never become entity keys.
/// The workflow owns the subject identity; tool/component ownership is
/// attribution metadata and must not replace that subject.
public sealed record ObservabilityEntityIdentity(
    string EntityType,
    string CanonicalEntityId,
    string DisplayName)
{
    public bool CanAppearInTopLevelNavigation =>
        EntityType is ObservabilityEntityTypes.Mod or ObservabilityEntityTypes.Tool &&
        CanonicalEntityId.StartsWith(EntityType + ":", StringComparison.Ordinal);
    public static ObservabilityEntityIdentity ForMod(string canonicalModId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Mod, canonicalModId, displayName ?? canonicalModId);

    public static ObservabilityEntityIdentity ForTool(string canonicalToolId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Tool, canonicalToolId, displayName ?? canonicalToolId);

    public static ObservabilityEntityIdentity ForRuntime(string canonicalRuntimeId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Runtime, canonicalRuntimeId, displayName ?? canonicalRuntimeId);
    public static ObservabilityEntityIdentity ForInfrastructure(
        string canonicalInfrastructureId,
        string? displayName = null) =>
        Create(
            ObservabilityEntityTypes.Infrastructure,
            canonicalInfrastructureId,
            displayName ?? canonicalInfrastructureId);

    public static ObservabilityEntityIdentity ForFixture(string canonicalFixtureId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Fixture, canonicalFixtureId, displayName ?? canonicalFixtureId);

    public static ObservabilityEntityIdentity ForTest(string canonicalTestId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Test, canonicalTestId, displayName ?? canonicalTestId);

    public static ObservabilityEntityIdentity ForAgent(string canonicalAgentId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Agent, canonicalAgentId, displayName ?? canonicalAgentId);

    public static ObservabilityEntityIdentity ForUser(string canonicalUserId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.User, canonicalUserId, displayName ?? canonicalUserId);

    public static ObservabilityEntityIdentity ForProcess(string canonicalProcessId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Process, canonicalProcessId, displayName ?? canonicalProcessId);

    public static ObservabilityEntityIdentity ForSession(string canonicalSessionId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Session, canonicalSessionId, displayName ?? canonicalSessionId);

    public static ObservabilityEntityIdentity ForRun(string canonicalRunId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Run, canonicalRunId, displayName ?? canonicalRunId);

    public static ObservabilityEntityIdentity ForActivity(string canonicalActivityId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Activity, canonicalActivityId, displayName ?? canonicalActivityId);

    public static ObservabilityEntityIdentity ForEvent(string canonicalEventId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Event, canonicalEventId, displayName ?? canonicalEventId);

    public static ObservabilityEntityIdentity ForUnknown(string suppliedId, string? displayName = null) =>
        Create(ObservabilityEntityTypes.Unknown, suppliedId, displayName ?? suppliedId);

    public static ObservabilityEntityIdentity Create(
        string entityType,
        string canonicalEntityId,
        string displayName)
    {
        string type = string.IsNullOrWhiteSpace(entityType)
            ? ObservabilityEntityTypes.Unknown
            : entityType.Trim().ToLowerInvariant();
        string id = NormalizeCanonicalEntityId(canonicalEntityId);
        string name = string.IsNullOrWhiteSpace(displayName)
            ? id
            : displayName.Trim();
        string canonicalId = id.StartsWith(type + ":", StringComparison.Ordinal)
            ? id
            : type + ":" + id;
        return new(type, canonicalId, name);
    }

    private static string NormalizeCanonicalEntityId(string value) =>
        value.Trim()
            .Replace('\\', '/')
            .Trim('/')
            .ToLowerInvariant();
}

public static class AgentObservabilityEntityIdentity
{
    public static string GroupKey(AgentSnapshot agent) =>
        GroupKey(agent.EntityType, agent.CanonicalEntityId, agent.ModId);

    public static string GroupKey(AgentEvent eventRecord) =>
        GroupKey(eventRecord.EntityType, eventRecord.CanonicalEntityId, eventRecord.ModId);

    public static string GroupKey(AgentIssue issue) =>
        GroupKey(issue.EntityType, issue.CanonicalEntityId, issue.ModId);

    public static string GroupKey(
        string? entityType,
        string? canonicalEntityId,
        string fallbackId)
    {
        string type = string.IsNullOrWhiteSpace(entityType)
            ? ObservabilityEntityTypes.Unknown
            : entityType.Trim().ToLowerInvariant();
        ObservabilityEntityIdentity identity = ObservabilityEntityIdentity.Create(
            type,
            canonicalEntityId ?? fallbackId,
            fallbackId);
        return type + "\u001f" + identity.CanonicalEntityId;
    }
}


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

    [JsonPropertyName("logicalAgentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalAgentId { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = ObservabilityEntityTypes.Unknown;

    [JsonPropertyName("canonicalEntityId")]
    public string CanonicalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

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

    [JsonPropertyName("logicalAgentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalAgentId { get; init; }
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = ObservabilityEntityTypes.Unknown;

    [JsonPropertyName("canonicalEntityId")]
    public string CanonicalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("modName")]
    public required string ModName { get; init; }

    [JsonPropertyName("workloadKind")]
    public string WorkloadKind { get; init; } = "production";

    [JsonPropertyName("toolchainState")]
    public string ToolchainState { get; init; } = "promoted";

    [JsonPropertyName("toolchainFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolchainFingerprint { get; init; }
    [JsonPropertyName("toolchainBindingProven")]
    public bool ToolchainBindingProven { get; init; }
    [JsonPropertyName("toolchainMode")]
    public string ToolchainMode { get; init; } = "unknown";
    [JsonPropertyName("rimLiaisonExecutablePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimLiaisonExecutablePath { get; init; }
    [JsonPropertyName("rimLiaisonExecutableSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimLiaisonExecutableSha256 { get; init; }
    [JsonPropertyName("rimLiaisonAssemblyPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimLiaisonAssemblyPath { get; init; }
    [JsonPropertyName("rimLiaisonAssemblySha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimLiaisonAssemblySha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeRoot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DevBridgeRuntimeRoot { get; init; }
    [JsonPropertyName("devBridgePackageSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DevBridgePackageSha256 { get; init; }
    [JsonPropertyName("transactionConsumerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionConsumerPath { get; init; }
    [JsonPropertyName("transactionConsumerSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionConsumerSha256 { get; init; }
    [JsonPropertyName("toolchainCompatibilityContract")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolchainCompatibilityContract { get; init; }

    [JsonPropertyName("qualificationProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QualificationProfile { get; init; }

    [JsonPropertyName("status")]
    public AgentStatus Status { get; init; } = AgentStatus.Created;

    [JsonPropertyName("currentStage")]
    public DevelopmentStage CurrentStage { get; init; } = DevelopmentStage.Analysis;

    [JsonPropertyName("currentOperation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentOperation { get; init; }

    [JsonPropertyName("blockingState")]
    public string BlockingState { get; init; } = "none";

    [JsonPropertyName("currentActivity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentActivity { get; init; }

    [JsonPropertyName("startTime")]
    public long StartTime { get; init; }

    [JsonPropertyName("lastActivityAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LastActivityAt { get; init; }

    [JsonPropertyName("completedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompletedAt { get; init; }

    [JsonPropertyName("completionState")]
    public AgentCompletionState CompletionState { get; init; } = AgentCompletionState.None;

    [JsonPropertyName("completionResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionResult { get; init; }

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

    [JsonPropertyName("logicalAgentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalAgentId { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }
    [JsonPropertyName("modId")]
    public required string ModId { get; init; }

    [JsonPropertyName("reportingTool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportingTool { get; init; }

    [JsonPropertyName("reportingModId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportingModId { get; init; }

    [JsonPropertyName("causalComponent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausalComponent { get; init; }

    [JsonPropertyName("affectedProject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AffectedProject { get; init; }

    [JsonPropertyName("affectedValidations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedValidations { get; init; }

    [JsonPropertyName("causalIssueKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausalIssueKey { get; init; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; init; } = ObservabilityEntityTypes.Unknown;

    [JsonPropertyName("canonicalEntityId")]
    public string CanonicalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

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

    [JsonPropertyName("classification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Classification { get; init; }

    [JsonPropertyName("validationClassification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidationClassification { get; init; }

    [JsonPropertyName("blocking")]
    public bool Blocking { get; init; }

    [JsonPropertyName("currentState")]
    public string CurrentState { get; init; } = "open";

    [JsonPropertyName("resolutionState")]
    public string ResolutionState { get; init; } = "unresolved";

    [JsonPropertyName("componentOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentOwner { get; init; }

    [JsonPropertyName("evidenceReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceReference { get; init; }

    [JsonPropertyName("affectedValidation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AffectedValidation { get; init; }

    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; init; }

    [JsonPropertyName("capabilityId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapabilityId { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("probableOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProbableOwner { get; init; }

    [JsonPropertyName("affectedAgentIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedAgentIds { get; init; }

    [JsonPropertyName("affectedRunIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedRunIds { get; init; }

    [JsonPropertyName("affectedModIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedModIds { get; init; }
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; init; }

    [JsonPropertyName("occurrences")]
    public int Occurrences { get; init; } = 1;
}
/// Logical identity for one agent across sessions and runs. The legacy
/// fallback intentionally remains session-scoped because records without a
/// supplied logical identity cannot be safely merged. This namespace is for
/// lifecycle and diagnostics only; navigation always uses entity identity.
public static class AgentObservabilityLogicalIdentity
{
    public static string For(AgentSnapshot agent) =>
        For(agent.LogicalAgentId, agent.RunId, agent.AgentId);

    public static string For(AgentEvent eventRecord) =>
        For(eventRecord.LogicalAgentId, eventRecord.RunId, eventRecord.AgentId);

    public static string For(AgentIssue issue) =>
        For(issue.LogicalAgentId, issue.RunId, issue.AgentId);

    public static string For(
        string? logicalAgentId,
        string runId,
        string agentId)
    {
        return string.IsNullOrWhiteSpace(logicalAgentId)
            ? "legacy:" + runId + "\u001f" + agentId
            : logicalAgentId.Trim();
    }

    public static string GroupKey(AgentSnapshot agent) =>
        GroupKey(For(agent), AgentObservabilityEntityIdentity.GroupKey(agent));

    public static string GroupKey(AgentIssue issue) =>
        GroupKey(For(issue), AgentObservabilityEntityIdentity.GroupKey(issue));

    public static string GroupKey(string logicalAgentId, string canonicalEntityGroupKey) =>
        logicalAgentId + "\u001f" + canonicalEntityGroupKey;
}

public sealed record AgentObservabilityView(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyList<AgentIssue> Issues);

public sealed record AgentDiagnosticMod(
    string ModId,
    string ModName,
    string AgentId,
    string RunId,
    string? LogicalAgentId = null);

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
    string? FailureMessage,
    string? CausalDiagnostic = null,
    bool CausalDiagnosticTruncated = false,
    string? DiagnosticSignature = null,
    string? Orchestrator = null,
    string? FailureSurface = null,
    string? CausalOwner = null,
    string? OwnershipConfidence = null,
    string? OwnershipBasis = null,
    string? RawStdoutEvidenceId = null,
    string? RawStderrEvidenceId = null,
    JsonElement? Discrimination = null);

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
    public TimeSpan WorkingStalenessThreshold { get; init; } = TimeSpan.FromMinutes(5);
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
            StallThreshold <= TimeSpan.Zero ||
            WorkingStalenessThreshold <= TimeSpan.Zero)
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
