using RimLiaison.DevBridge;

namespace RimLiaison.Recovery;

public enum ProductionFailureClassification
{
    SelfHealable,
    ProjectConfigurationFailure,
    TrulyFatal,
    ObsoleteAfterConsolidation
}

public enum ProductionCheckpoint
{
    PreMutation,
    BuildComplete,
    PackageComplete,
    DeploymentCommitted,
    RuntimeStarted,
    AssertionsStarted
}

public sealed record ProductionFailureAssessment(
    ProductionFailureClassification Classification,
    string Code,
    string Owner,
    bool SafeToReplay,
    string? Reason = null)
{
    public bool IsProjectFailure =>
        Classification == ProductionFailureClassification.ProjectConfigurationFailure;
}

/// <summary>
/// The single allow-list for the ordinary production execution boundary.
/// Recovery implementations remain in their owning adapters; this policy only
/// decides whether a result may be recovered, belongs to the project, or must
/// stop with a trustworthy toolchain handoff.
/// </summary>
public static class ProductionExecutionPolicy
{
    private static readonly HashSet<string> SelfHealableCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEVBRIDGE_COORDINATOR_UNAVAILABLE",
        "DEVBRIDGE_COORDINATOR_NOT_READY",
        "DEVBRIDGE_COORDINATOR_UNRESPONSIVE",
        "DEVBRIDGE_COORDINATOR_SCOPE_MISMATCH",
        "DEVBRIDGE_SCOPE_MISMATCH",
        "SCOPE_MISMATCH",
        "DEVBRIDGE_NO_STRUCTURED_RESPONSE",
        "DEVBRIDGE_CLIENT_TIMEOUT",
        "DEVBRIDGE_MOD_TRANSACTION_TIMEOUT",
        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING",
        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
        "RIMBRIDGE_ENDPOINT_STALE",
        "RIMBRIDGE_ENDPOINT_UNAVAILABLE",
        "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH",
        "RIMBRIDGE_PROCESS_MISMATCH",
        "RIMBRIDGE_PROTOCOL_ERROR",
        "RIMBRIDGE_COMPANION_UNAVAILABLE",
        "RIMBRIDGE_NOT_READY",
        "RIMBRIDGE_STALE",
        "ENDPOINT_UNAVAILABLE",
        "READINESS_NOT_READY",
        "READINESS_TIMEOUT",
        "READINESS_IDENTITY_MISMATCH",
        "PROCESS_EXITED",
        "PROCESS_STOPPED",
        "GENERATION_INPUT_MISMATCH",
        "GENERATION_MISMATCH",
        "RIMBRIDGE_LEASE_REQUIRED",
        "LEASE_REQUIRED",
        "STALE_LEASE",
        "EXPIRED_LEASE",
        "PROJECT_RUNTIME_ROOT_MISSING",
        "PROJECT_WORKSPACE_ENROLLMENT_REQUIRED",
        "PROJECT_WORKSPACE_STALE",
        "DEPLOYMENT_RECONCILIATION_REQUIRED",
        "RUNTIME_PACKAGE_STALE",
        "RUNTIME_ASSET_MISSING",
        "RIMWORLD_RESTART_REQUIRED",
        "DEVBRIDGE_RESTART_REQUIRED"
    };

    private static readonly HashSet<string> ProjectCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT_RECIPE_NOT_FOUND",
        "PROJECT_RECIPE_INVALID_JSON",
        "PROJECT_RECIPE_PATH_INVALID",
        "PROJECT_RECIPE_SCHEMA_UNSUPPORTED",
        "PROJECT_RECIPE_ID_MISMATCH",
        "PROJECT_RECIPE_ID_FILENAME_MISMATCH",
        "PROJECT_RECIPE_OWNER_MISSING",
        "PROJECT_METADATA_MISSING",
        "PROJECT_METADATA_FIELD_INVALID",
        "PROJECT_METADATA_SOURCE_MISSING",
        "PROJECT_METADATA_IDENTITY_CONTRADICTION",
        "PROJECT_METADATA_RUNTIME_PACKAGE_INVALID",
        "PROJECT_METADATA_WORKLOAD_INVALID",
        "PROJECT_RECIPE_READ_FAILED",
        "DEVELOPMENT_RECIPE_PROJECT_MISMATCH",
        "DEVELOPMENT_BUILD_FAILED",
        "DEVELOPMENT_ASSERTION_FAILED",
        "RECIPE_ASSERTION_FAILED",
        "TEST_ASSERTION_FAILED",
        "RIMTEST_TEST_FAILURE",
        "DEVELOPMENT_RUNTIME_ASSERTION_FAILED",
        "RUNTIME_ASSERTION_FAILED",
        "TEST_RECIPE_INVALID",
        "TEST_RECIPE_NOT_FOUND"
    };

    private static readonly HashSet<string> ObsoleteCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEVBRIDGE_RECOVER_COMMAND_REQUIRED",
        "DEVBRIDGE_SLOT_REQUIRED",
        "DEVBRIDGE_MANUAL_RESTART_REQUIRED",
        "DEVBRIDGE_LEGACY_COORDINATOR_REQUIRED"
    };

    public static ProductionFailureAssessment Classify(
        string? errorCode,
        string? error = null,
        bool safeReplay = false,
        string? buildOwnerType = null)
    {
        string code = string.IsNullOrWhiteSpace(errorCode)
            ? "RIMLIAISON_UNCLASSIFIED_FAILURE"
            : errorCode.Trim();
        string ownerType = buildOwnerType?.Trim().ToUpperInvariant() ?? string.Empty;

        if (IsToolchainBuildOwner(ownerType) && IsBuildFailureCode(code))
        {
            return new(
                ProductionFailureClassification.SelfHealable,
                code,
                "RimLiaison",
                safeReplay,
                error ?? "RimLiaison-owned build state may be reconciled once.");
        }

        if (ObsoleteCodes.Contains(code))
        {
            return new(
                ProductionFailureClassification.ObsoleteAfterConsolidation,
                code,
                "RimLiaison",
                false,
                "The legacy recovery surface is not part of the consolidated production product.");
        }

        if (code.Equals("PROJECT_METADATA_OWNER_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("PROJECT_IDENTITY_CONFLICT", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("OWNERSHIP_AMBIGUOUS", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("IDENTITY_AMBIGUITY", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("HASH_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("FINGERPRINT_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("OUTPUT_LIMIT", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("SCHEMA_UNSUPPORTED", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("RESPONSE_INVALID", StringComparison.OrdinalIgnoreCase) &&
                !SelfHealableCodes.Contains(code))
        {
            return new(
                ProductionFailureClassification.TrulyFatal,
                code,
                "RimLiaison",
                false,
                error ?? "Identity, freshness, or protocol evidence is not trustworthy.");
        }

        if (ownerType == "PROJECT_BUILD" ||
            ProjectCodes.Contains(code) ||
            code.StartsWith("MSBUILD_", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("COMPILER_", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("BUILD_", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProductionFailureClassification.ProjectConfigurationFailure,
                code,
                "project",
                false,
                error ?? "The project configuration or validation found a project-owned failure.");
        }

        if (SelfHealableCodes.Contains(code) ||
            code.StartsWith("DEPLOYMENT_RECONCILIATION_", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("RUNTIME_PACKAGE_", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("RUNTIME_ASSET_", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProductionFailureClassification.SelfHealable,
                code,
                "RimLiaison",
                safeReplay,
                error ?? "RimLiaison may reconcile this owned runtime condition once.");
        }

        return new(
            ProductionFailureClassification.TrulyFatal,
            code,
            "RimLiaison",
            false,
            error ?? "The failure was not proven safe to repair or replay.");
    }

    private static bool IsToolchainBuildOwner(string ownerType) =>
        ownerType is "TOOLCHAIN_BUILD" or "RUNTIME_MATERIALIZATION" or "TEST_HARNESS_BUILD";

    private static bool IsBuildFailureCode(string code) =>
        code.StartsWith("DEVELOPMENT_BUILD", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("BUILD_", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("MSBUILD_", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("COMPILER_", StringComparison.OrdinalIgnoreCase);

    public static ProductionFailureAssessment Classify(
        string? errorCode,
        string? error,
        string? buildOwnerType) =>
        Classify(errorCode, error, false, buildOwnerType);

    public static string AgentOutcomeFor(
        ProductionFailureAssessment assessment,
        bool workflowPassed = false)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (workflowPassed)
        {
            return "PASS";
        }

        return assessment.IsProjectFailure
            ? "MOD_FAILURE"
            : "TOOLCHAIN_FATAL";
    }

    public static bool IsProjectOwned(string? errorCode) =>
        Classify(errorCode).IsProjectFailure;

    public static bool IsProjectOwned(string? errorCode, string? buildOwnerType) =>
        Classify(errorCode, null, buildOwnerType).IsProjectFailure;

    public static bool IsRecoverable(string? errorCode) =>
        Classify(errorCode).Classification == ProductionFailureClassification.SelfHealable;

    public static bool RequiresPreMutationEscalation(
        string? errorCode,
        string? buildOwnerType = null)
    {
        string code = errorCode?.Trim() ?? string.Empty;
        string ownerType = buildOwnerType?.Trim().ToUpperInvariant() ?? string.Empty;
        return (IsToolchainBuildOwner(ownerType) && IsBuildFailureCode(code)) ||
            code is "DEVBRIDGE_NO_STRUCTURED_RESPONSE" or
                "DEVBRIDGE_COORDINATOR_UNAVAILABLE" or
                "DEVBRIDGE_COORDINATOR_NOT_READY" or
                "DEVBRIDGE_COORDINATOR_UNRESPONSIVE" or
                "DEVBRIDGE_CLIENT_TIMEOUT" or
                "DEVBRIDGE_MOD_TRANSACTION_TIMEOUT";
    }

    public static string CheckpointName(ProductionCheckpoint checkpoint) =>
        checkpoint switch
        {
            ProductionCheckpoint.PreMutation => "PRE_MUTATION",
            ProductionCheckpoint.BuildComplete => "BUILD_COMPLETE",
            ProductionCheckpoint.PackageComplete => "PACKAGE_COMPLETE",
            ProductionCheckpoint.DeploymentCommitted => "DEPLOYMENT_COMMITTED",
            ProductionCheckpoint.RuntimeStarted => "RUNTIME_STARTED",
            ProductionCheckpoint.AssertionsStarted => "ASSERTIONS_STARTED",
            _ => checkpoint.ToString().ToUpperInvariant()
        };
}
