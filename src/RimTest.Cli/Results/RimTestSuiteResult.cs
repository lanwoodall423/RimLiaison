using System.Text.Json.Serialization;
using RimTest.Execution;

namespace RimTest.Results;

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
        string? fallbackSuite = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        RimTestResult[] children = execution.Tests.ToArray();
        int passed = children.Count(static child => child.Status == "pass");
        int failed = children.Count(static child =>
            child.Status is "fail" or "infrastructure" or "invalid");
        int cancelledChildren = children.Count(static child => child.Status == "cancelled");
        int cancelled = cancelledChildren > 0
            ? cancelledChildren
            : execution.Cancelled ? 1 : 0;

        string status = execution.Cancelled || cancelled > 0
            ? "cancelled"
            : children.Any(static child => child.Status is "infrastructure" or "invalid")
                ? "infrastructure"
                : failed > 0
                    ? "fail"
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

        return new RimTestSuiteResult
        {
            Status = status,
            Suite = execution.SuiteId,
            Passed = passed,
            Failed = failed,
            DurationMs = Math.Max(0, durationMs),
            Failures = failures.Length == 0 ? null : failures,
            Skipped = execution.Skipped > 0 ? execution.Skipped : null,
            Cancelled = cancelled > 0 ? cancelled : null,
            // A normal affected run has no extra selection state to report;
            // retain only conservative selection context needed to understand
            // why a broader fallback suite ran.
            SelectionStatus = string.Equals(
                    selectionStatus,
                    "ok",
                    StringComparison.Ordinal)
                ? null
                : selectionStatus,
            SelectionErrorCode = string.Equals(
                    selectionStatus,
                    "conservative",
                    StringComparison.Ordinal)
                ? selectionErrorCode
                : null,
            FallbackSuite = fallbackSuite
        };
    }
}
