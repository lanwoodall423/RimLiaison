using System.Text.Json.Serialization;
using RimLiaison.Execution;

namespace RimLiaison.Results;

public static class RimTestSuiteResultSchema
{
    public const string Current = "rimtest-suite-result/v1";
}

public sealed class RimTestSuiteResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimTestSuiteResultSchema.Current;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("suite")]
    public required string Suite { get; init; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("artifactFreshness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestArtifactFreshness? ArtifactFreshness { get; init; }

    [JsonPropertyName("reuse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogSuiteReuseSummary? Reuse { get; init; }

    [JsonPropertyName("failFast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogSuiteFailFastSummary? FailFast { get; init; }

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("failures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestSuiteFailure>? Failures { get; init; }

    [JsonPropertyName("skipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Skipped { get; init; }

    [JsonPropertyName("cancelled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cancelled { get; init; }

    [JsonPropertyName("selectionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionStatus { get; init; }

    [JsonPropertyName("selectionErrorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionErrorCode { get; init; }

    [JsonPropertyName("fallbackSuite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackSuite { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }
}

public sealed class RimTestSuiteFailure
{
    [JsonPropertyName("test")]
    public required string Test { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("diagnosticId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiagnosticId { get; init; }

    [JsonPropertyName("failureFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureFingerprint { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
}

public static class RimTestSuiteResultFactory
{
    public static RimTestSuiteResult FromExecution(
        CatalogSuiteExecutionResult execution,
        long durationMs,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        string? fallbackSuite = null,
        string? workflowId = null,
        RimTestArtifactFreshness? artifactFreshness = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        RimTestResult[] children = execution.Tests.ToArray();
        bool emptyExecution = children.Length == 0;
        int passed = children.Count(static child => child.Status == "pass");
        int failed = children.Count(static child =>
            child.Status is "fail" or "infrastructure" or "invalid");
        int cancelledChildren = children.Count(static child => child.Status == "cancelled");
        int cancelled = cancelledChildren > 0
            ? cancelledChildren
            : execution.Cancelled ? 1 : 0;
        bool failFastIncomplete =
            execution.FailFast is { ValidationCompleted: false } or { NotLaunched: > 0 };

        string status = execution.Cancelled || cancelled > 0
            ? "cancelled"
            : emptyExecution
                ? "conservative"
                : children.Any(static child => child.Status is "infrastructure" or "invalid")
                    ? "infrastructure"
                    : failed > 0
                        ? "fail"
                        : failFastIncomplete
                            ? "conservative"
                            : execution.Reuse?.Status == "invalidated"
                                ? "infrastructure"
                                : "pass";

        RimTestSuiteFailure[] failures = children
            .Where(static child => child.Status is "fail" or "infrastructure" or "invalid")
            .OrderBy(static child => child.Test, StringComparer.Ordinal)
            .Select(static child => new RimTestSuiteFailure
            {
                Test = child.Test,
                Status = child.Status == "fail" ? null : child.Status,
                DiagnosticId = child.Diagnostic?.Id,
                FailureFingerprint = child.FailureFingerprint,
                EvidenceId = child.EvidenceId,
                ErrorCode = child.ErrorCode
            })
            .ToArray();

        RimTestArtifactFreshness? projectedFreshness = artifactFreshness;
        if (projectedFreshness is not null)
        {
            string? runId = children
                .Select(static child => child.RunId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            string[] operationIds = children
                .SelectMany(static child => child.OperationIds ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            projectedFreshness = projectedFreshness with
            {
                RunId = runId,
                OperationIds = operationIds.Length == 0 ? null : operationIds
            };
        }

        return new RimTestSuiteResult
        {
            Status = status,
            Suite = execution.SuiteId,
            WorkflowId = workflowId ?? children
                .Select(static child => child.WorkflowId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            Passed = passed,
            Failed = failed,
            DurationMs = Math.Max(0, durationMs),
            ArtifactFreshness = projectedFreshness,
            Reuse = execution.Reuse,
            FailFast = execution.FailFast,
            Failures = failures.Length == 0 ? null : failures,
            Skipped = execution.Skipped > 0 ? execution.Skipped : null,
            Cancelled = cancelled > 0 ? cancelled : null,
            // A normal affected run has no extra selection state to report;
            // retain only conservative selection context needed to understand
            // why a broader fallback suite ran.
            SelectionStatus = emptyExecution
                ? "conservative"
                : string.Equals(
                    selectionStatus,
                    "ok",
                    StringComparison.Ordinal)
                ? null
                : selectionStatus,
            SelectionErrorCode = emptyExecution
                ? "RIMTEST_EMPTY_EXECUTION"
                : string.Equals(
                    selectionStatus,
                    "conservative",
                    StringComparison.Ordinal)
                ? selectionErrorCode
                : null,
            FallbackSuite = fallbackSuite,
            NextAction = emptyExecution
                ? "rimliaison suites"
                : children
                    .OrderBy(static child => child.Test, StringComparer.Ordinal)
                    .Select(static child => child.NextAction)
                    .FirstOrDefault(static action => !string.IsNullOrWhiteSpace(action))
        };
    }
}
