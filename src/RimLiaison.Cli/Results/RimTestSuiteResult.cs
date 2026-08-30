using RimDev.Contracts;
using System.Text.Json.Serialization;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Recovery;
using RimLiaison.Validation;


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

    [JsonIgnore]
    public ExecutionIdentity? ExecutionIdentity { get; init; }
    [JsonPropertyName("artifactFreshness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestArtifactFreshness? ArtifactFreshness { get; init; }

    [JsonPropertyName("orchestration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestOrchestrationSummary? Orchestration { get; init; }
    [JsonPropertyName("validationDiagnosis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestValidationChainDiagnosis? ValidationDiagnosis { get; init; }

    [JsonPropertyName("prerequisiteRecovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestPrerequisiteRecovery>? PrerequisiteRecovery { get; init; }

    [JsonPropertyName("toolchainRecovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestToolchainRecovery? ToolchainRecovery { get; init; }

    [JsonPropertyName("toolchainRecoveryCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ToolchainRecoveryCount { get; init; }

    [JsonPropertyName("toolchainRecoveryTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ToolchainRecoveryTypes { get; init; }

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

    [JsonPropertyName("selectedTests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SelectedTests { get; init; }

    [JsonPropertyName("executedTests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ExecutedTests { get; init; }

    [JsonPropertyName("blockedTests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestSuiteBlockedTest>? BlockedTests { get; init; }

    [JsonPropertyName("selectedTestCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SelectedTestCount { get; init; }

    [JsonPropertyName("executedTestCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExecutedTestCount { get; init; }

    [JsonPropertyName("blockedTestCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockedTestCount { get; init; }

    [JsonPropertyName("failedTestCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FailedTestCount { get; init; }

    [JsonPropertyName("infrastructureFailureCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InfrastructureFailureCount { get; init; }


    [JsonPropertyName("blocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Blocked { get; init; }

    [JsonPropertyName("unavailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Unavailable { get; init; }

    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValidationPolicyResult? Validation { get; init; }

    [JsonPropertyName("validationPlan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::RimContext.Core.Impact.ValidationPlan? ValidationPlan { get; init; }


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


public sealed record RimTestToolchainRecovery(
    [property: JsonPropertyName("attempted")] bool Attempted,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("trigger")] string? Trigger,
    [property: JsonPropertyName("highestLevel")] string HighestLevel,
    [property: JsonPropertyName("rimWorldRestarted")] bool RimWorldRestarted,
    [property: JsonPropertyName("finalState")] string FinalState,
    [property: JsonPropertyName("elapsedRecoveryMs")] long ElapsedRecoveryMilliseconds);

public sealed class RimTestSuiteBlockedTest
{
    [JsonPropertyName("test")]
    public required string Test { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("blockedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockedBy { get; init; }

    [JsonPropertyName("prerequisite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prerequisite { get; init; }

    [JsonPropertyName("causalFailureId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausalFailureId { get; init; }
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
    public static RimTestSuiteResult FromSelectionFailure(
        string suiteId,
        string selectionStatus,
        string errorCode,
        string? error,
        string? nextAction,
        long durationMs,
        string? workflowId = null,
        IReadOnlyList<RimTestPrerequisiteRecovery>? prerequisiteRecovery = null)
    {
        var execution = new CatalogSuiteExecutionResult(
            suiteId,
            [],
            0,
            Cancelled: false,
            PrerequisiteRecovery: prerequisiteRecovery);
        return new RimTestSuiteResult
        {
            Status = "infrastructure",
            Suite = suiteId,
            WorkflowId = workflowId,
            Passed = 0,
            Failed = 0,
            DurationMs = Math.Max(0, durationMs),
            PrerequisiteRecovery = prerequisiteRecovery,
            SelectionStatus = selectionStatus,
            SelectionErrorCode = errorCode,
            NextAction = nextAction,
            ValidationDiagnosis = RimTestValidationChainDiagnoser.Diagnose(
                execution,
                selectionStatus,
                errorCode,
                freshnessRequested: false,
                workflowId: workflowId),
            Orchestration = RimTestOrchestrationProjector.Project(
                execution,
                suiteId,
                selectionStatus,
                errorCode,
                nextAction,
                freshnessRequested: false,
                freshness: null,
                freshnessStatus: null,
                workflowId: workflowId)
        };
    }

    public static RimTestSuiteResult FromExecution(
        CatalogSuiteExecutionResult execution,
        long durationMs,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        string? fallbackSuite = null,
        string? workflowId = null,
        RimTestArtifactFreshness? artifactFreshness = null,
        DevBridgeAdapterStatus? freshnessStatus = null,
        bool freshnessRequested = false,
        global::RimContext.Core.Impact.ValidationPlan? validationPlan = null,
        IReadOnlyList<string>? selectedTestIds = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        RimTestResult[] children = execution.Tests.ToArray();
        bool emptyExecution = children.Length == 0;
        int passed = children.Count(static child =>
            child.Status == RimTestValidationStates.Pass);
        int blocked = children.Count(static child =>
            child.Status == RimTestValidationStates.Blocked);
        int unavailable = children.Count(static child =>
            child.Status is RimTestValidationStates.NotAvailable or "not_executed");
        int failed = children.Count(static child =>
            child.Status == RimTestValidationStates.Fail);
        int infrastructureFailures = children.Count(static child =>
            child.Status is RimTestValidationStates.Infrastructure or "invalid");
        string[] selected = (selectedTestIds ?? children
                .Select(static child => child.Test)
                .ToArray())
            .Where(static test => !string.IsNullOrWhiteSpace(test))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] executed = children
            .Where(static child =>
                child.Status is RimTestValidationStates.Pass or RimTestValidationStates.Fail)
            .Select(static child => child.Test)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RimTestSuiteBlockedTest[] blockedTests = children
            .Where(static child => child.Status == RimTestValidationStates.Blocked)
            .OrderBy(static child => child.Test, StringComparer.Ordinal)
            .Select(static child => new RimTestSuiteBlockedTest
            {
                Test = child.Test,
                ErrorCode = child.ErrorCode,
                BlockedBy = child.BlockedBy,
                Prerequisite = child.Prerequisite,
                CausalFailureId = child.CausalFailureId
            })
            .ToArray();
        bool prerequisiteBlocked = children.Any(static child =>
            child.ValidationOutcome == "PREREQUISITE_BLOCKED");
        bool includeAccounting = blocked > 0 || infrastructureFailures > 0;
        int cancelledChildren = children.Count(static child => child.Status == "cancelled");
        RimTestToolchainRecovery? toolchainRecovery =
            ProjectToolchainRecovery(execution.PrerequisiteRecovery);
        int cancelled = cancelledChildren > 0
            ? cancelledChildren
            : execution.Cancelled ? 1 : 0;
        ValidationPolicyResult validation = EvaluateValidation(children);
        bool failFastIncomplete =
            execution.FailFast is { ValidationCompleted: false } or { NotLaunched: > 0 };

        string[] recoveryTypes = (execution.PrerequisiteRecovery ?? [])
            .Where(static recovery => !string.IsNullOrWhiteSpace(recovery.Component))
            .Select(static recovery => recovery.Component)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static component => component, StringComparer.Ordinal)
            .Take(16)
            .ToArray();

        string status = execution.Cancelled || cancelled > 0
            ? "cancelled"
            : emptyExecution
                ? "conservative"
                : children.Any(static child =>
                    child.Status is RimTestValidationStates.Infrastructure or "invalid")
                    ? "infrastructure"
                    : failed > 0
                        ? "fail"
                        : prerequisiteBlocked
                            ? "infrastructure"
                            : blocked > 0
                                ? "blocked"
                                : failFastIncomplete
                                    ? "conservative"
                                    : execution.Reuse?.Status == "invalidated"
                                        ? "infrastructure"
                                        : "pass";

        RimTestSuiteFailure[] failures = children
            .Where(static child =>
                child.Status is RimTestValidationStates.Fail or
                    RimTestValidationStates.Infrastructure or "invalid")
            .OrderBy(static child => child.Test, StringComparer.Ordinal)
            .Select(static child => new RimTestSuiteFailure
            {
                Test = child.Test,
                Status = child.Status == RimTestValidationStates.Fail ? null : child.Status,
                DiagnosticId = child.Diagnostic?.Id,
                FailureFingerprint = child.FailureFingerprint,
                EvidenceId = child.EvidenceId,
                ErrorCode = child.ErrorCode
            })
            .ToArray();

        RimTestArtifactFreshness? projectedFreshness = artifactFreshness;
        if (projectedFreshness is not null)
        {
            IEnumerable<RimTestResult> artifactChildren =
                string.IsNullOrWhiteSpace(projectedFreshness.ArtifactTestId)
                    ? children
                    : children.Where(child => string.Equals(
                        child.Test,
                        projectedFreshness.ArtifactTestId,
                        StringComparison.Ordinal));
            string? runId = artifactChildren
                .Select(static child => child.RunId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            string[] operationIds = artifactChildren
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
            SelectedTests = includeAccounting ? selected : null,
            ExecutedTests = includeAccounting ? executed : null,
            BlockedTests = blockedTests.Length == 0 ? null : blockedTests,
            SelectedTestCount = includeAccounting ? selected.Length : null,
            ExecutedTestCount = includeAccounting ? executed.Length : null,
            BlockedTestCount = includeAccounting ? blockedTests.Length : null,
            FailedTestCount = includeAccounting ? failed : null,
            InfrastructureFailureCount = includeAccounting ? infrastructureFailures : null,
            Blocked = blocked > 0 ? blocked : null,
            Unavailable = unavailable > 0 ? unavailable : null,
            Validation = validation,
            ValidationPlan = validationPlan,
            DurationMs = Math.Max(0, durationMs),
            ArtifactFreshness = projectedFreshness,
            ExecutionIdentity = projectedFreshness?.ToExecutionIdentity(),
            ValidationDiagnosis = string.Equals(execution.SuiteId, "affected", StringComparison.Ordinal)
                ? RimTestValidationChainDiagnoser.Diagnose(
                    execution,
                    selectionStatus,
                    selectionErrorCode,
                    projectedFreshness,
                    freshnessStatus,
                    freshnessRequested,
                    workflowId ?? children
                        .Select(static child => child.WorkflowId)
                        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)))
                : null,
            Orchestration = string.Equals(execution.SuiteId, "affected", StringComparison.Ordinal)
                ? RimTestOrchestrationProjector.Project(
                    execution,
                    execution.SuiteId,
                    selectionStatus,
                    selectionErrorCode,
                    execution.Tests
                        .Select(static child => child.NextAction)
                        .FirstOrDefault(static action => !string.IsNullOrWhiteSpace(action)),
                    freshnessRequested,
                    projectedFreshness,
                    freshnessStatus,
                    workflowId)
                : null,
            PrerequisiteRecovery = execution.PrerequisiteRecovery,
            ToolchainRecovery = toolchainRecovery,
            ToolchainRecoveryCount = recoveryTypes.Length == 0 ? null : recoveryTypes.Length,
            ToolchainRecoveryTypes = recoveryTypes.Length == 0 ? null : recoveryTypes,
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

    internal static RimTestToolchainRecovery? ProjectToolchainRecovery(
        IReadOnlyList<RimTestPrerequisiteRecovery>? recoveries)
    {
        RimTestPrerequisiteRecovery[] items = (recoveries ?? [])
            .Where(static recovery => recovery.Attempts > 0)
            .Take(16)
            .ToArray();
        if (items.Length == 0)
        {
            return null;
        }

        RimTestPrerequisiteRecovery? managedReset = items.FirstOrDefault(
            static recovery => recovery.Component == "managed-runtime-reset");
        string highestLevel = managedReset?.Action ??
            items.Select(static recovery => recovery.Action)
                .FirstOrDefault(static action => !string.IsNullOrWhiteSpace(action)) ??
            "RECONCILE";
        return new(
            Attempted: true,
            Count: items.Select(static recovery => recovery.Component)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            Trigger: managedReset?.ErrorCode ??
                items.Select(static recovery => recovery.ErrorCode)
                    .FirstOrDefault(static code => !string.IsNullOrWhiteSpace(code)),
            HighestLevel: highestLevel,
            RimWorldRestarted: string.Equals(
                highestLevel,
                "FULL_RUNTIME_RESET",
                StringComparison.Ordinal),
            FinalState: items.Any(static recovery => recovery.State == "recovered")
                ? "READY"
                : "UNAVAILABLE",
            ElapsedRecoveryMilliseconds: items
                .Select(static recovery => recovery.ElapsedRecoveryMilliseconds ?? 0)
                .DefaultIfEmpty()
                .Max());
    }
    private static ValidationPolicyResult EvaluateValidation(
        IReadOnlyList<RimTestResult> children)
    {
        var observations = children.Select(child =>
        {
            ValidationClassification classification =
                Enum.TryParse(
                    child.ValidationClassification,
                    ignoreCase: false,
                    out ValidationClassification parsed)
                    ? parsed
                    : ValidationClassification.REQUIRED;
            ValidationRequirementSource source =
                classification == ValidationClassification.REQUIRED
                    ? ValidationRequirementSource.TOOLCHAIN_CONTRACT
                    : ValidationRequirementSource.DISCOVERED;
            ValidationCheckState state = child.Status switch
            {
                "pass" => ValidationCheckState.PASSED,
                "fail" or "infrastructure" or "invalid" =>
                    ValidationCheckState.FAILED,
                "not_available" or "not_executed" or "blocked" =>
                    ValidationCheckState.NOT_AVAILABLE,
                _ => ValidationCheckState.NOT_EXECUTED
            };
            ValidationFindingKind? finding = child.Status switch
            {
                "fail" when classification != ValidationClassification.REQUIRED =>
                    ValidationFindingKind.MOD_DEFECT,
                "infrastructure" or "invalid" =>
                    ValidationFindingKind.TOOLING_FAILURE,
                "not_available" or "not_executed" =>
                    ValidationFindingKind.OPTIONAL_VALIDATION_UNAVAILABLE,
                _ => null
            };
            return new ValidationCheckObservation
            {
                Check = ValidationPolicyEvaluator.Define(
                    child.Test,
                    classification,
                    source,
                    "Catalog validation " + child.Test),
                State = state,
                Finding = finding,
                EvidenceReference = child.EvidenceId,
                Recommendation = child.NextAction,
                Summary = child.ErrorCode
            };
        });
        return ValidationPolicyEvaluator.Evaluate(observations);
    }

}
