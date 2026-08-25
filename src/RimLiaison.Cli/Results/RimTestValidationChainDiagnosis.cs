using System.Text.Json.Serialization;
using RimLiaison.DevBridge;
using RimLiaison.Execution;

namespace RimLiaison.Results;

public static class RimTestValidationChainSchema
{
    public const string Current = "rimtest-validation-chain/v1";
}

/// <summary>
/// Additive, bounded diagnosis of the canonical source-to-evidence validation chain.
/// It classifies ownership separately from the legacy suite status so a blocked
/// prerequisite cannot be reported as a project assertion failure.
public sealed class RimTestValidationChainDiagnosis
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimTestValidationChainSchema.Current;

    [JsonPropertyName("result")]
    public required string OverallResult { get; init; }

    [JsonPropertyName("firstFailedBoundary")]
    public required string FirstFailedBoundary { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonPropertyName("probableOwner")]
    public required string ProbableOwner { get; init; }

    [JsonPropertyName("ownershipConfidence")]
    public required string OwnershipConfidence { get; init; }

    [JsonPropertyName("ownershipReason")]
    public required string OwnershipReason { get; init; }

    [JsonPropertyName("projectRuntimeExecuted")]
    public bool ProjectRuntimeExecuted { get; init; }

    [JsonPropertyName("runtimeValidationExecuted")]
    public bool RuntimeValidationExecuted { get; init; }

    [JsonPropertyName("projectFailureObserved")]
    public bool ProjectFailureObserved { get; init; }

    [JsonPropertyName("artifactFreshness")]
    public required string ArtifactFreshness { get; init; }

    [JsonPropertyName("readiness")]
    public required string Readiness { get; init; }

    [JsonPropertyName("lease")]
    public required string Lease { get; init; }

    [JsonPropertyName("evidenceIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EvidenceIds { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }
}

public static class RimTestValidationChainDiagnoser
{
    public static RimTestValidationChainDiagnosis Diagnose(
        CatalogSuiteExecutionResult execution,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        RimTestArtifactFreshness? freshness = null,
        DevBridgeAdapterStatus? freshnessStatus = null,
        bool freshnessRequested = false,
        string? workflowId = null)
    {
        RimTestResult[] tests = execution.Tests.ToArray();
        bool projectFailure = tests.Any(static test => test.Status == "fail");
        bool runtimeEvidence = tests.Any(HasRuntimeEvidence);
        bool projectRuntimeExecuted = tests.Any(static test => test.Status is "pass" or "fail");
        string? code = selectionErrorCode ??
            freshnessStatus?.ErrorCode ?? freshness?.ErrorCode ??
            tests.Select(static test => test.ErrorCode)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        bool runtimeInfrastructureFailure = tests.Any(test =>
            test.Status == "infrastructure" &&
            (HasRuntimeEvidence(test) || IsRuntimeInfrastructureCode(test.ErrorCode)));
        bool runtimeAttempted = runtimeEvidence || runtimeInfrastructureFailure;
        bool incomplete = execution.Cancelled ||
            execution.FailFast is { ValidationCompleted: false } or { NotLaunched: > 0 } ||
            tests.Any(static test => test.Status is "cancelled" or "invalid");
        string artifact = ArtifactState(freshnessRequested, freshness);
        bool freshnessFailed = freshnessRequested &&
            (artifact is "failed" or "stale");
        bool trustedProjectFailure = projectFailure &&
            (!freshnessRequested || artifact == "fresh");
        bool selectionFailure = IsSelectionFailure(selectionStatus);
        bool readinessFailure = IsReadinessCode(code);
        bool leaseFailure = IsLeaseCode(code);
        bool buildFailure = IsBuildCode(code);
        bool deployFailure = IsDeployCode(code);

        string result;
        string boundary;
        string owner;
        string confidence;
        string reason;
        string readiness;
        string lease;

        if (trustedProjectFailure)
        {
            result = "PROJECT_VALIDATION_FAILED";
            boundary = "runtime";
            owner = "target project";
            confidence = "high";
            reason = "A project test returned a failure after runtime evidence was produced.";
            readiness = "ready";
            lease = "acquired";
        }
        else if (projectFailure)
        {
            result = "NOT_PROVEN";
            boundary = freshnessRequested ? "artifact-freshness" : "runtime";
            owner = "unknown";
            confidence = "low";
            reason = "A project assertion was observed, but current-artifact correspondence was not proven.";
            readiness = "unproven";
            lease = "unproven";
        }
        else if (runtimeInfrastructureFailure)
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "runtime";
            owner = "RimLiaison/DevBridge2 runtime orchestration";
            confidence = "high";
            reason = "Runtime evidence exists, but the runtime operation ended with an infrastructure outcome and no project assertion.";
            readiness = "ready";
            lease = "acquired";
        }
        else if (buildFailure)
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "build";
            owner = "RimLiaison/DevBridge2/build orchestration";
            confidence = "high";
            reason = "The development build failed before lease or runtime evidence; no project assertion was observed.";
            readiness = "not_reached";
            lease = "not_acquired";
        }
        else if (deployFailure)
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "deploy";
            owner = "DevBridge2 deployment orchestration";
            confidence = "high";
            reason = "Deployment failed before current-artifact runtime evidence; no project assertion was observed.";
            readiness = "not_reached";
            lease = "not_acquired";
        }
        else if (readinessFailure)
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "readiness";
            owner = "DevBridge2 readiness/identity boundary";
            confidence = "high";
            reason = "Readiness or process identity failed before a valid runtime lease was usable.";
            readiness = "failed";
            lease = "not_acquired";
        }
        else if (leaseFailure || tests.Any(static test => test.Status == "blocked"))
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "lease";
            owner = "DevBridge2 lease orchestration";
            confidence = "high";
            reason = "The runtime lease was unavailable or capability execution was blocked before project runtime validation.";
            readiness = "ready";
            lease = "failed";
        }
        else if (freshnessFailed || IsArtifactCode(code))
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "artifact-freshness";
            owner = "RimLiaison/DevBridge2 artifact freshness";
            confidence = "high";
            reason = "Current-artifact correspondence was not proven, so runtime validation cannot be trusted.";
            readiness = "not_reached";
            lease = "not_acquired";
        }
        else if (selectionFailure)
        {
            result = "INFRASTRUCTURE_BLOCKED";
            boundary = "source";
            owner = "RimLiaison/RimContext selection";
            confidence = "high";
            reason = "Source impact or test selection stopped before build and runtime evidence.";
            readiness = "not_reached";
            lease = "not_acquired";
        }
        else if (incomplete)
        {
            result = "NOT_PROVEN";
            boundary = "runtime";
            owner = "unknown";
            confidence = "low";
            reason = "The selected runtime set did not complete, so the validation chain cannot claim pass or project failure.";
            readiness = runtimeAttempted ? "ready" : "unproven";
            lease = runtimeAttempted ? "acquired" : "unproven";
        }
        else if (freshnessRequested && artifact != "fresh" && !runtimeAttempted)
        {
            result = "NOT_PROVEN";
            boundary = "artifact-freshness";
            owner = "unknown";
            confidence = "low";
            reason = "The canonical chain stopped without enough evidence to assign the failure to tooling or project code.";
            readiness = "unproven";
            lease = "unproven";
        }
        else if (!runtimeAttempted)
        {
            result = "NOT_PROVEN";
            boundary = "runtime";
            owner = "unknown";
            confidence = "low";
            reason = "No project runtime or assertion evidence was produced.";
            readiness = freshnessRequested && artifact == "fresh" ? "ready" : "unproven";
            lease = "unproven";
        }
        else
        {
            result = "PASS";
            boundary = "none";
            owner = "none";
            confidence = "high";
            reason = "The source, build, deployment, freshness, readiness, lease, runtime, and evidence chain completed.";
            readiness = "ready";
            lease = "acquired";
        }

        if (result == "PASS" && freshnessRequested && artifact != "fresh")
        {
            result = "NOT_PROVEN";
            boundary = "artifact-freshness";
            owner = "unknown";
            confidence = "low";
            reason = "Runtime-shaped results exist, but current-artifact freshness was not proven.";
            readiness = "unproven";
            lease = "unproven";
        }

        return new RimTestValidationChainDiagnosis
        {
            OverallResult = result,
            FirstFailedBoundary = boundary,
            Code = code ?? (result == "PASS" ? null : "RIMTEST_VALIDATION_CHAIN_INCOMPLETE"),
            ProbableOwner = owner,
            OwnershipConfidence = confidence,
            OwnershipReason = reason,
            ProjectRuntimeExecuted = projectRuntimeExecuted,
            RuntimeValidationExecuted = runtimeAttempted,
            ProjectFailureObserved = projectFailure,
            ArtifactFreshness = artifact,
            Readiness = readiness,
            Lease = lease,
            EvidenceIds = EvidenceIds(freshness, tests, workflowId),
            NextAction = NextActionFor(result, boundary)
        };
    }

    private static bool HasRuntimeEvidence(RimTestResult test) =>
        test.Status is "pass" or "fail" ||
        !string.IsNullOrWhiteSpace(test.RunId) ||
        test.OperationIds is { Count: > 0 } ||
        test.Generation.HasValue;
    private static bool IsRuntimeInfrastructureCode(string? code) =>
        code is not null && (code.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase));

    private static bool IsSelectionFailure(string? status) =>
        status is not null &&
        status is not "ok" and not "conservative";

    private static string ArtifactState(bool requested, RimTestArtifactFreshness? freshness)
    {
        if (!requested) return "not_requested";
        return freshness?.EvaluationStatus?.ToUpperInvariant() switch
        {
            "FRESH" => "fresh",
            "STALE" => "stale",
            "FAILED" => "failed",
            _ => "unproven"
        };
    }

    private static bool IsBuildCode(string? code) =>
        code is not null && (code.StartsWith("DEVELOPMENT_BUILD", StringComparison.Ordinal) ||
            code.StartsWith("BUILD_", StringComparison.Ordinal) ||
            code.StartsWith("MSBUILD_", StringComparison.Ordinal));

    private static bool IsDeployCode(string? code) =>
        code is not null && (code.Contains("DEPLOY", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("DEPLOYMENT", StringComparison.OrdinalIgnoreCase));

    private static bool IsArtifactCode(string? code) =>
        code is not null && (code.Contains("FRESHNESS", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("RIMTEST_ARTIFACT", StringComparison.Ordinal));

    private static bool IsReadinessCode(string? code) =>
        code is not null && (code.StartsWith("READINESS_", StringComparison.Ordinal) ||
            code.StartsWith("GENERATION_", StringComparison.Ordinal) ||
            code.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("PROCESS_", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("ENDPOINT", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("COMPANION", StringComparison.OrdinalIgnoreCase));

    private static bool IsLeaseCode(string? code) =>
        code is not null && code.Contains("LEASE", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string>? EvidenceIds(
        RimTestArtifactFreshness? freshness,
        IReadOnlyList<RimTestResult> tests,
        string? workflowId)
    {
        var ids = new List<string>(capacity: 8);
        Add(workflowId);
        Add(freshness?.TransactionId);
        Add(freshness?.LeaseId);
        Add(freshness?.RunId);
        foreach (RimTestResult test in tests)
        {
            if (test.Status != "pass")
            {
                Add(test.EvidenceId);
                Add(test.RunId);
            }
        }

        return ids.Count == 0 ? null : ids;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || ids.Contains(value, StringComparer.Ordinal) || ids.Count >= 8) return;
            ids.Add(value);
        }
    }

    private static string? NextActionFor(string result, string boundary) => result switch
    {
        "PASS" => null,
        "PROJECT_VALIDATION_FAILED" => "inspect the project assertion evidence and fix the target project",
        "INFRASTRUCTURE_BLOCKED" when boundary == "build" => "inspect DevBridge2 build diagnostics, then rerun rimliaison affected --run --fail-fast --json",
        "INFRASTRUCTURE_BLOCKED" => "follow the owning component nextAction, then rerun rimliaison affected --run --fail-fast --json",
        _ => "do not claim validation; inspect the structured evidence and rerun the canonical validation"
    };
}
