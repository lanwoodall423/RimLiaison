using System.Text.Json.Serialization;
using RimTest.DevBridge;
using RimTest.RimError;

namespace RimTest.Results;

public static class RimTestResultSchema
{
    public const string Current = "rimtest-result/v1";
}

/// <summary>
/// The bounded diagnostic projection used by the default agent-facing result.
/// The complete RimError report remains owned and retrievable by RimError.
/// </summary>
public sealed class RimTestDiagnosticSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    public static RimTestDiagnosticSummary FromRimError(
        RimErrorDiagnosticSummary diagnosis) => new()
        {
            Id = diagnosis.Id,
            Category = diagnosis.Category,
            Method = diagnosis.Method,
            Source = diagnosis.Source,
            Line = diagnosis.Line
        };
}

public sealed class RimTestResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimTestResultSchema.Current;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("test")]
    public required string Test { get; init; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("operationIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OperationIds { get; init; }

    [JsonPropertyName("failureFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureFingerprint { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("diagnosticStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiagnosticStatus { get; init; }

    [JsonPropertyName("diagnostic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestDiagnosticSummary? Diagnostic { get; init; }

    [JsonPropertyName("diagnosticErrorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiagnosticErrorCode { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }
}

public static class RimTestResultFactory
{
    public static RimTestResult FromRun(
        string testId,
        DevBridgeRecipeRunResult run,
        long durationMs,
        string? workflowId = null)
    {
        string status = run.Status.Outcome switch
        {
            DevBridgeOutcomeKind.Success => "pass",
            DevBridgeOutcomeKind.TestFailure => "fail",
            DevBridgeOutcomeKind.Cancelled => "cancelled",
            _ => "infrastructure"
        };
        bool includeFailureDetails = status != "pass";

        return new RimTestResult
        {
            Status = status,
            Test = testId,
            WorkflowId = workflowId ?? run.WorkflowId,
            DurationMs = BoundDuration(durationMs),
            RunId = run.RunId,
            Generation = run.Generation,
            OperationIds = run.Operations
                .Select(static operation => operation.OperationId)
                .Where(static operationId => !string.IsNullOrWhiteSpace(operationId))
                .Select(static operationId => operationId!)
                .Take(8)
                .ToArray() is { Length: > 0 } operationIds
                ? operationIds
                : null,
            FailureFingerprint = includeFailureDetails
                ? run.FailureFingerprint
                : null,
            EvidenceId = includeFailureDetails
                ? run.EvidenceId ?? run.Evidence
                : null,
            ErrorCode = includeFailureDetails
                ? run.Status.ErrorCode
                : null,
            NextAction = NextActionFor(run.Status.Outcome)
        };
    }

    public static RimTestResult Invalid(
        string testId,
        string errorCode,
        long durationMs = 0,
        string? workflowId = null)
    {
        return new RimTestResult
        {
            Status = "invalid",
            Test = testId,
            WorkflowId = workflowId,
            DurationMs = BoundDuration(durationMs),
            ErrorCode = errorCode
        };
    }

    public static RimTestResult Infrastructure(
        string testId,
        string errorCode,
        long durationMs = 0,
        string? workflowId = null)
    {
        return new RimTestResult
        {
            Status = "infrastructure",
            Test = testId,
            WorkflowId = workflowId,
            DurationMs = BoundDuration(durationMs),
            ErrorCode = errorCode,
            NextAction = NextActionForError(errorCode)
        };
    }

    public static RimTestResult Cancelled(
        string testId,
        long durationMs = 0,
        string? workflowId = null)
    {
        return new RimTestResult
        {
            Status = "cancelled",
            Test = testId,
            WorkflowId = workflowId,
            DurationMs = BoundDuration(durationMs),
            ErrorCode = "RIMTEST_CANCELLED"
        };
    }

    public static RimTestResult ArtifactFreshnessFailure(
        string testId,
        string errorCode,
        string? workflowId = null) =>
        new()
        {
            Status = "infrastructure",
            Test = testId,
            WorkflowId = workflowId,
            ErrorCode = errorCode,
            NextAction = "inspect-artifact-freshness"
        };

    public static RimTestResult AttachDiagnosis(
        RimTestResult result,
        RimErrorDiagnosisResult diagnosis)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(diagnosis);
        if (!string.Equals(result.Status, "fail", StringComparison.Ordinal))
        {
            return result;
        }

        return new RimTestResult
        {
            SchemaVersion = result.SchemaVersion,
            Status = result.Status,
            Test = result.Test,
            WorkflowId = result.WorkflowId,
            DurationMs = result.DurationMs,
            RunId = result.RunId,
            Generation = result.Generation,
            OperationIds = result.OperationIds,
            FailureFingerprint = result.FailureFingerprint,
            EvidenceId = result.EvidenceId,
            ErrorCode = result.ErrorCode,
            DiagnosticStatus = diagnosis.IsAvailable && diagnosis.Diagnosis is not null
                ? null
                : diagnosis.Outcome switch
                {
                    RimErrorDiagnosisOutcome.Empty => "empty",
                    _ => "unavailable"
                },
            Diagnostic = diagnosis.IsAvailable && diagnosis.Diagnosis is not null
                ? RimTestDiagnosticSummary.FromRimError(diagnosis.Diagnosis)
                : null,
            DiagnosticErrorCode = diagnosis.IsAvailable && diagnosis.Diagnosis is not null
                ? null
                : diagnosis.Status.ErrorCode,
            NextAction = diagnosis.IsAvailable && diagnosis.Diagnosis is not null
                ? $"rimerror show {diagnosis.Diagnosis.Id}"
                : result.NextAction
        };
    }

    private static string? NextActionFor(DevBridgeOutcomeKind outcome) => outcome switch
    {
        DevBridgeOutcomeKind.DevBridgeRefusal or
        DevBridgeOutcomeKind.InfrastructureFailure or
        DevBridgeOutcomeKind.Timeout or
        DevBridgeOutcomeKind.MalformedResponse or
        DevBridgeOutcomeKind.IncompatibleSchema => "DevBridge.cmd doctor --json",
        _ => null
    };

    private static string? NextActionForError(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return null;
        }

        return errorCode.StartsWith("DEVBRIDGE_", StringComparison.Ordinal) ||
            errorCode.StartsWith("RIMBRIDGE_", StringComparison.Ordinal) ||
            errorCode.StartsWith("READINESS_", StringComparison.Ordinal) ||
            errorCode.StartsWith("TEST_RECIPE_", StringComparison.Ordinal) ||
            errorCode.StartsWith("RIMTEST_ARTIFACT_", StringComparison.Ordinal) ||
            errorCode.StartsWith("DEVELOPMENT_", StringComparison.Ordinal)
            ? "DevBridge.cmd doctor --json"
            : null;
    }

    private static long BoundDuration(long durationMs)
    {
        return Math.Max(0, durationMs);
    }
}
