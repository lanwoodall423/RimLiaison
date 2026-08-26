using System.Text.Json.Serialization;
using RimLiaison.Observability;
using RimLiaison.Validation;

namespace RimLiaison.GoldenPath;

public static class GoldenPathSchemas
{
    public const string Current = "rimliaison-golden-path/v1";
    public const string Preflight = "rimliaison-golden-path-preflight/v1";
    public const string ProductionStateEvent = "production.state.changed";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoldenPathStage
{
    Preflight,
    Requirements,
    Selection,
    Build,
    Deploy,
    RuntimeStartup,
    RuntimeValidation,
    Evidence,
    Classification,
    Publish,
    Completion,
    Tooling
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoldenPathStepState
{
    Passed,
    Failed,
    Unavailable,
    NotExecuted
}

public sealed record GoldenPathIdentity
{
    public required string ModId { get; init; }
    public required string AgentId { get; init; }
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
}

public sealed record GoldenPathPreflightResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = GoldenPathSchemas.Preflight;
    public required string Status { get; init; }
    public bool Ready { get; init; }
    public string? Project { get; init; }
    public string? NextAction { get; init; }
    public string? BlockingState { get; init; }
    public string? ComponentOwner { get; init; }
    public string? ErrorCode { get; init; }

    public static GoldenPathPreflightResult ReadyFor(string? project = null) => new()
    {
        Status = "ready",
        Ready = true,
        Project = project,
        BlockingState = "none"
    };

    public static GoldenPathPreflightResult Blocked(
        string errorCode,
        string? nextAction = null,
        string? componentOwner = null) => new()
        {
            Status = "blocked",
            Ready = false,
            ErrorCode = errorCode,
            NextAction = nextAction,
            ComponentOwner = componentOwner,
            BlockingState = "required"
        };
}

public sealed record GoldenPathStepResult
{
    public GoldenPathStepState State { get; init; }
    public ValidationFindingKind? Finding { get; init; }
    public string? Summary { get; init; }
    public string? ErrorCode { get; init; }
    public string? EvidenceReference { get; init; }
    public string? Recommendation { get; init; }
    public string? ComponentOwner { get; init; }
    public string? AffectedValidation { get; init; }
    public bool OperationAttempted { get; init; }
    public bool Retryable { get; init; }
    public object? Evidence { get; init; }

    public static GoldenPathStepResult Passed(
        string? summary = null,
        string? evidenceReference = null,
        object? evidence = null) => new()
        {
            State = GoldenPathStepState.Passed,
            Summary = summary,
            EvidenceReference = evidenceReference,
            Evidence = evidence,
            OperationAttempted = true
        };

    public static GoldenPathStepResult ModFailure(
        string summary,
        string? errorCode = null,
        string? evidenceReference = null) => new()
        {
            State = GoldenPathStepState.Failed,
            Finding = ValidationFindingKind.MOD_DEFECT,
            Summary = summary,
            ErrorCode = errorCode,
            EvidenceReference = evidenceReference,
            OperationAttempted = true
        };

    public static GoldenPathStepResult ToolingFailure(
        string summary,
        string? errorCode,
        string componentOwner,
        bool retryable = true,
        string? affectedValidation = null,
        object? evidence = null) => new()
        {
            State = GoldenPathStepState.Unavailable,
            Finding = ValidationFindingKind.TOOLING_FAILURE,
            Summary = summary,
            ErrorCode = errorCode,
            ComponentOwner = componentOwner,
            Retryable = retryable,
            AffectedValidation = affectedValidation,
            Evidence = evidence,
            OperationAttempted = false
        };

    public static GoldenPathStepResult OptionalUnavailable(
        string summary,
        string componentOwner,
        string? errorCode = null,
        string? recommendation = null,
        string? affectedValidation = null,
        object? evidence = null) => new()
        {
            State = GoldenPathStepState.Unavailable,
            Finding = ValidationFindingKind.OPTIONAL_VALIDATION_UNAVAILABLE,
            Summary = summary,
            ErrorCode = errorCode,
            ComponentOwner = componentOwner,
            Recommendation = recommendation,
            AffectedValidation = affectedValidation,
            Evidence = evidence,
            OperationAttempted = false
        };

    public static GoldenPathStepResult NotExecuted(string summary) => new()
    {
        State = GoldenPathStepState.NotExecuted,
        Summary = summary,
        OperationAttempted = false
    };
}

public sealed record GoldenPathOperation
{
    public required string Id { get; init; }
    public required GoldenPathStage Stage { get; init; }
    public required ValidationCheckDefinition Check { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public required Func<GoldenPathOperationContext, CancellationToken, Task<GoldenPathStepResult>> ExecuteAsync { get; init; }
    public Func<GoldenPathOperationContext, CancellationToken, Task<GoldenPathStepResult>>? RetryAsync { get; init; }
}

public sealed record GoldenPathOperationContext(
    GoldenPathIdentity Identity,
    AgentObservabilitySession Session,
    IReadOnlyDictionary<string, GoldenPathStepResult> CompletedSteps,
    GoldenPathPreflightResult Preflight);

public sealed record GoldenPathRunRequest
{
    public required GoldenPathIdentity Identity { get; init; }
    public required GoldenPathPreflightResult Preflight { get; init; }
    public required IReadOnlyList<GoldenPathOperation> Operations { get; init; }
    public string? ModName { get; init; }
}

public sealed record GoldenPathRunResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = GoldenPathSchemas.Current;
    public required GoldenPathIdentity Identity { get; init; }
    public required string Status { get; init; }
    public required string CompletionResult { get; init; }
    public required GoldenPathPreflightResult Preflight { get; init; }
    public required ValidationPolicyResult Validation { get; init; }
    public IReadOnlyDictionary<string, GoldenPathStepResult> Steps { get; init; } =
        new Dictionary<string, GoldenPathStepResult>(StringComparer.Ordinal);
    public IReadOnlyList<string> ToolingIncidentIds { get; init; } = [];
    public IReadOnlyList<string> RecommendationIds { get; init; } = [];

    public bool Passed => Status == ValidationPolicySchema.Pass;
}
