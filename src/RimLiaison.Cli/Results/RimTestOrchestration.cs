using System.Text.Json.Serialization;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Recovery;

namespace RimLiaison.Results;

public static class RimTestOrchestrationSchema
{
    public const string Current = "rimtest-orchestration/v1";
}

/// <summary>
/// Additive, affected-run-only projection of the end-to-end validation state.
/// The existing suite/result fields remain the compact compatibility surface;
/// this envelope makes infrastructure outcomes machine-actionable.
/// </summary>
public sealed class RimTestOrchestrationSummary
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimTestOrchestrationSchema.Current;

    [JsonPropertyName("overall")]
    public required string Overall { get; init; }

    [JsonPropertyName("sourceBuild")]
    public required string SourceBuild { get; init; }

    [JsonPropertyName("staticTests")]
    public required string StaticTests { get; init; }

    [JsonPropertyName("deployment")]
    public required string Deployment { get; init; }

    [JsonPropertyName("runtimeValidation")]
    public required string RuntimeValidation { get; init; }

    [JsonPropertyName("infrastructure")]
    public required string Infrastructure { get; init; }

    [JsonPropertyName("failure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestOrchestrationFailure? Failure { get; init; }

    [JsonPropertyName("cleanup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RimTestCleanupSummary? Cleanup { get; init; }
}

public sealed class RimTestOrchestrationFailure
{
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("errorCode")]
    public required string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("recoveryAttempted")]
    public bool RecoveryAttempted { get; init; }

    [JsonPropertyName("recoveryResult")]
    public required string RecoveryResult { get; init; }

    [JsonPropertyName("retrySafe")]
    public bool RetrySafe { get; init; }

    [JsonPropertyName("manualInterventionRequired")]
    public bool ManualInterventionRequired { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }

    [JsonPropertyName("leaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("identityMismatch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DevBridgeIdentityMismatch? IdentityMismatch { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }
}

public sealed class RimTestCleanupSummary
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("leaseReleased")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LeaseReleased { get; init; }

    [JsonPropertyName("temporaryStateCleared")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TemporaryStateCleared { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }
}

internal static class RimTestOrchestrationProjector
{
    public static RimTestOrchestrationSummary Project(
        CatalogSuiteExecutionResult execution,
        string suiteId,
        string? selectionStatus,
        string? selectionErrorCode,
        string? selectionNextAction,
        bool freshnessRequested,
        RimTestArtifactFreshness? freshness,
        DevBridgeAdapterStatus? freshnessStatus,
        string? workflowId)
    {
        bool hasTestFailure = execution.Tests.Any(static test => test.Status == "fail");
        bool hasInfrastructureFailure = execution.Tests.Any(static test =>
            test.Status is "infrastructure" or "invalid");
        bool hasCancelled = execution.Cancelled || execution.Tests.Any(static test =>
            test.Status == "cancelled") ||
            string.Equals(selectionStatus, "cancelled", StringComparison.Ordinal);
        bool cleanupFailed = string.Equals(
            execution.Cleanup?.Status,
            "FAILED",
            StringComparison.Ordinal);
        bool hasSelectionFailure = selectionStatus is "blocked" or "invalid";
        bool freshnessSucceeded = freshness?.EvaluationStatus == "FRESH";
        bool freshnessFailed = freshnessRequested &&
            freshness?.EvaluationStatus == "FAILED";
        bool freshnessStale = freshnessRequested &&
            freshness?.EvaluationStatus == "STALE";
        string? freshnessErrorCode = freshnessStatus?.ErrorCode ?? freshness?.ErrorCode;
        bool recoveryFailed = HasRecoveryState(
            execution.PrerequisiteRecovery,
            "recoveryFailed",
            "recoveryRequired",
            "unavailable",
            "contended",
            "transitionRecoveryExhausted") ||
            freshnessStatus?.RecoveryState is
                PrerequisiteRecoveryState.RecoveryFailed or
                PrerequisiteRecoveryState.RecoveryRequired or
                PrerequisiteRecoveryState.Unavailable or
                PrerequisiteRecoveryState.Contended or
                PrerequisiteRecoveryState.TransitionRecoveryExhausted;
        bool recovered = HasRecoveryState(
            execution.PrerequisiteRecovery,
            "recovered") ||
            freshnessStatus?.RecoveryState == PrerequisiteRecoveryState.Recovered;
        bool transitionRecoveryExhausted = HasRecoveryState(
            execution.PrerequisiteRecovery,
            "transitionRecoveryExhausted") ||
            freshnessStatus?.RecoveryState == PrerequisiteRecoveryState.TransitionRecoveryExhausted;
        bool contended = HasRecoveryState(execution.PrerequisiteRecovery, "contended") ||
            freshnessStatus?.RecoveryState == PrerequisiteRecoveryState.Contended;
        bool sourceBuildFailure = IsSourceBuildFailure(freshnessErrorCode);
        bool unresolvedInfrastructure = hasSelectionFailure ||
            hasInfrastructureFailure ||
            cleanupFailed ||
            ((freshnessFailed || freshnessStale) && !sourceBuildFailure);

        string staticTests = execution.Tests.Count == 0
            ? "NOT_RUN"
            : hasTestFailure
                ? "FAIL"
                : hasInfrastructureFailure || hasCancelled
                    ? "NOT_RUN"
                    : "PASS";
        string sourceBuild = !freshnessRequested
            ? "NOT_RUN"
            : freshnessSucceeded
                ? "PASS"
                : sourceBuildFailure
                    ? "FAIL"
                    : "NOT_RUN";
        string deployment = !freshnessRequested
            ? "NOT_EVALUATED"
            : freshness?.EvaluationStatus ?? "NOT_EVALUATED";
        string runtime = hasTestFailure
            ? "FAIL"
            : freshnessFailed || freshnessStale || hasSelectionFailure ||
              hasInfrastructureFailure || hasCancelled
                ? "BLOCKED"
                : execution.Tests.Count == 0
                    ? "NOT_RUN"
                    : "PASS";

        string infrastructure = contended
            ? "CONTENDED"
            : transitionRecoveryExhausted
                ? "TRANSITION_RECOVERY_EXHAUSTED"
            : recoveryFailed || cleanupFailed
                ? "RECOVERY_FAILED"
                : unresolvedInfrastructure
                    ? "UNAVAILABLE"
            : recovered
                ? "RECOVERED"
                : "READY";

        string overall = hasCancelled
            ? "CANCELLED"
            : hasTestFailure
                ? "TEST_FAILURE"
                : sourceBuildFailure
                    ? "SOURCE_BUILD_FAILURE"
                    : hasSelectionFailure || freshnessFailed || freshnessStale ||
                      hasInfrastructureFailure || cleanupFailed
                    ? "INFRASTRUCTURE_FAILURE"
                    : "PASS";

        RimTestOrchestrationFailure? failure = overall == "PASS"
            ? null
            : BuildFailure(
                execution,
                selectionStatus,
                selectionErrorCode,
                selectionNextAction,
                freshnessStatus,
                freshness,
                workflowId,
                hasTestFailure,
                hasSelectionFailure,
                sourceBuildFailure,
                cleanupFailed);

        return new RimTestOrchestrationSummary
        {
            Overall = overall,
            SourceBuild = sourceBuild,
            StaticTests = staticTests,
            Deployment = deployment,
            RuntimeValidation = runtime,
            Infrastructure = infrastructure,
            Failure = failure,
            Cleanup = execution.Cleanup
        };
    }

    private static RimTestOrchestrationFailure BuildFailure(
        CatalogSuiteExecutionResult execution,
        string? selectionStatus,
        string? selectionErrorCode,
        string? selectionNextAction,
        DevBridgeAdapterStatus? freshnessStatus,
        RimTestArtifactFreshness? freshness,
        string? workflowId,
        bool hasTestFailure,
        bool hasSelectionFailure,
        bool sourceBuildFailure,
        bool cleanupFailed)
    {
        string? freshnessErrorCode = freshnessStatus?.ErrorCode ?? freshness?.ErrorCode;
        string? testErrorCode = execution.Tests
            .Select(static test => test.ErrorCode)
            .FirstOrDefault(static code => !string.IsNullOrWhiteSpace(code));
        DevBridgeIdentityMismatch? identityMismatch =
            freshnessStatus?.IdentityMismatch ??
            execution.PrerequisiteRecovery?
                .Select(static recovery => recovery.IdentityMismatch)
                .FirstOrDefault(static value => value is not null);
        string? errorCode = hasSelectionFailure
            ? selectionErrorCode
            : hasTestFailure
                ? testErrorCode
                : sourceBuildFailure
                    ? freshnessErrorCode
                    : cleanupFailed
                        ? execution.Cleanup?.ErrorCode
                        : freshnessErrorCode ?? testErrorCode;
        errorCode ??= hasTestFailure
            ? "RIMTEST_TEST_FAILURE"
            : "RIMTEST_ORCHESTRATION_FAILED";

        string owner = OwnerFor(errorCode);
        string stage = StageFor(errorCode, hasSelectionFailure, freshnessStatus is not null);
        bool recoveryAttempted =
            freshnessStatus?.RecoveryAttempts > 0 ||
            execution.PrerequisiteRecovery?.Any(static recovery => recovery.Attempts > 0) == true;
        string recoveryResult = RecoveryResultFor(
            freshnessStatus,
            execution.PrerequisiteRecovery,
            recoveryAttempted);
        bool retrySafe = RetrySafeFor(errorCode, recoveryResult);

        RimTestResult? child = execution.Tests.FirstOrDefault(test =>
            string.Equals(test.ErrorCode, errorCode, StringComparison.Ordinal));
        return new RimTestOrchestrationFailure
        {
            Owner = owner,
            Stage = stage,
            ErrorCode = errorCode,
            RecoveryAttempted = recoveryAttempted,
            RecoveryResult = recoveryResult,
            RetrySafe = retrySafe,
            ManualInterventionRequired = ManualInterventionFor(errorCode, recoveryResult),
            NextAction = selectionNextAction ?? child?.NextAction ??
                freshnessStatus?.RecoveryAction ??
                (owner == "DevBridge2" ? "DevBridge.cmd doctor --json" : null),
            Error = freshnessStatus?.Error ??
                (cleanupFailed
                    ? "The canonical affected-run cleanup did not produce authoritative restoration evidence."
                    : null),
            WorkflowId = workflowId ?? freshness?.WorkflowId ?? child?.WorkflowId,
            TransactionId = freshness?.TransactionId,
            LeaseId = freshness?.LeaseId,
            RunId = freshness?.RunId ?? child?.RunId,
            Generation = freshness?.Generation ?? child?.Generation,
            IdentityMismatch = identityMismatch,
            EvidenceId = child?.EvidenceId
        };
    }

    private static bool HasRecoveryState(
        IReadOnlyList<RimTestPrerequisiteRecovery>? recoveries,
        params string[] states) =>
        recoveries?.Any(recovery => states.Contains(
            recovery.State,
            StringComparer.Ordinal)) == true;

    private static bool IsSourceBuildFailure(string? errorCode) =>
        errorCode is not null &&
        (errorCode.StartsWith("DEVELOPMENT_BUILD", StringComparison.Ordinal) ||
         errorCode.StartsWith("BUILD_", StringComparison.Ordinal) ||
         errorCode.StartsWith("MSBUILD_", StringComparison.Ordinal));

    private static string OwnerFor(string errorCode) =>
        errorCode.StartsWith("RIMCONTEXT_", StringComparison.Ordinal)
            ? "RimContext"
            : errorCode.StartsWith("RIMERROR_", StringComparison.Ordinal)
                ? "RimError"
                : errorCode.StartsWith("RIMTEST_", StringComparison.Ordinal)
                    ? "RimLiaison"
                    : errorCode.StartsWith("DEVBRIDGE_", StringComparison.Ordinal) ||
                      errorCode.StartsWith("DEVELOPMENT_", StringComparison.Ordinal) ||
                      errorCode.StartsWith("RIMBRIDGE_", StringComparison.Ordinal) ||
                      errorCode.StartsWith("READINESS_", StringComparison.Ordinal)
                        ? "DevBridge2"
                        : "RimLiaison";

    private static string StageFor(
        string errorCode,
        bool selectionFailed,
        bool freshnessAttempted)
    {
        if (selectionFailed)
        {
            return errorCode.StartsWith("RIMCONTEXT_", StringComparison.Ordinal)
                ? "rimcontext"
                : "selection";
        }

        if (errorCode.StartsWith("DEVELOPMENT_BUILD", StringComparison.Ordinal) ||
            errorCode.StartsWith("BUILD_", StringComparison.Ordinal) ||
            errorCode.StartsWith("MSBUILD_", StringComparison.Ordinal))
        {
            return "build";
        }

        if (errorCode.Contains("DEPLOY", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("FRESHNESS", StringComparison.OrdinalIgnoreCase))
        {
            return "deploy";
        }

        if (errorCode.Contains("LEASE", StringComparison.OrdinalIgnoreCase))
        {
            return "lease";
        }

        if (errorCode.Contains("READINESS", StringComparison.OrdinalIgnoreCase) ||
            errorCode is "PROCESS_EXITED" or "PROCESS_STOPPED" ||
            errorCode.StartsWith("GENERATION_", StringComparison.Ordinal) ||
            errorCode.Contains("ENDPOINT", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("PROCESS_", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("COMPANION", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("PROTOCOL", StringComparison.OrdinalIgnoreCase) ||
            errorCode is "DEVBRIDGE_NO_STRUCTURED_RESPONSE" or
                "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING" or
                "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID")
        {
            return "readiness";
        }

        return freshnessAttempted ? "artifact-freshness" : "runtime";
    }

    private static string RecoveryResultFor(
        DevBridgeAdapterStatus? freshnessStatus,
        IReadOnlyList<RimTestPrerequisiteRecovery>? recoveries,
        bool attempted)
    {
        if (!attempted)
        {
            return "notAttempted";
        }

        if (freshnessStatus?.RecoveryState == PrerequisiteRecoveryState.Contended ||
            HasRecoveryState(recoveries, "contended"))
        {
            return "contended";
        }

        if (freshnessStatus?.RecoveryState is
                PrerequisiteRecoveryState.RecoveryFailed or
                PrerequisiteRecoveryState.RecoveryRequired or
                PrerequisiteRecoveryState.Unavailable ||
            HasRecoveryState(recoveries, "recoveryFailed", "recoveryRequired", "unavailable"))
        {
            return "failed";
        }

        if (freshnessStatus?.RecoveryState ==
                PrerequisiteRecoveryState.TransitionRecoveryExhausted ||
            HasRecoveryState(recoveries, "transitionRecoveryExhausted"))
        {
            return "exhausted";
        }

        return "recovered";
    }

    private static bool RetrySafeFor(string errorCode, string recoveryResult) =>
        recoveryResult is "notAttempted" or "recovered" &&
        !errorCode.Contains("ASSERTION", StringComparison.OrdinalIgnoreCase) &&
        !IsSourceBuildFailure(errorCode) &&
        !errorCode.Contains("AMBIGUOUS", StringComparison.OrdinalIgnoreCase);

    private static bool ManualInterventionFor(string errorCode, string recoveryResult) =>
        (recoveryResult is "failed" or "exhausted" or "contended") &&
        (errorCode.Contains("AMBIGUOUS", StringComparison.OrdinalIgnoreCase) ||
         errorCode.Contains("MANUAL", StringComparison.OrdinalIgnoreCase) ||
         errorCode.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase));
}
