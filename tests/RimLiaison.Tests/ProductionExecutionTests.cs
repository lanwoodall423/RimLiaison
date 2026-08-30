using RimLiaison.Execution;
using RimLiaison.Recovery;
using RimLiaison.Results;

namespace RimLiaison.Tests;

internal static class ProductionExecutionTests
{
    public static void FailureOwnershipTableIsBounded()
    {
        AssertEqual(
            ProductionFailureClassification.SelfHealable,
            ProductionExecutionPolicy.Classify("DEVBRIDGE_NO_STRUCTURED_RESPONSE").Classification);
        AssertEqual(
            ProductionFailureClassification.SelfHealable,
            ProductionExecutionPolicy.Classify("RIMBRIDGE_LEASE_REQUIRED").Classification);
        AssertEqual(
            ProductionFailureClassification.ProjectConfigurationFailure,
            ProductionExecutionPolicy.Classify("PROJECT_RECIPE_NOT_FOUND").Classification);
        AssertEqual(
            ProductionFailureClassification.ProjectConfigurationFailure,
            ProductionExecutionPolicy.Classify("DEVELOPMENT_BUILD_FAILED").Classification);
        AssertEqual(
            ProductionFailureClassification.TrulyFatal,
            ProductionExecutionPolicy.Classify("PROJECT_METADATA_OWNER_MISMATCH").Classification);
        AssertEqual(
            ProductionFailureClassification.TrulyFatal,
            ProductionExecutionPolicy.Classify("RIMTEST_ARTIFACT_FINGERPRINT_MISMATCH").Classification);
        AssertEqual(
            ProductionFailureClassification.ObsoleteAfterConsolidation,
            ProductionExecutionPolicy.Classify("DEVBRIDGE_SLOT_REQUIRED").Classification);
    }

    public static void ProjectFailureNormalizesToModFailure()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult(
                "affected",
                [new RimTestResult
                {
                    Test = "fixture-build",
                    Status = RimTestValidationStates.Infrastructure,
                    ErrorCode = "DEVELOPMENT_BUILD_FAILED"
                }],
                0,
                Cancelled: false),
            0);

        AssertEqual("MOD_FAILURE", result.Orchestration!.AgentOutcome);
        AssertEqual("ProjectConfigurationFailure", result.Orchestration.Failure!.Classification);
    }

    public static void UnrecoveredInfrastructureNormalizesToToolchainFatal()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult(
                "affected",
                [new RimTestResult
                {
                    Test = "fixture-runtime",
                    Status = RimTestValidationStates.Infrastructure,
                    ErrorCode = "DEVBRIDGE_NO_STRUCTURED_RESPONSE"
                }],
                0,
                Cancelled: false),
            0);

        AssertEqual("TOOLCHAIN_FATAL", result.Orchestration!.AgentOutcome);
        AssertEqual("SelfHealable", result.Orchestration.Failure!.Classification);
        AssertEqual("DEVBRIDGE_NO_STRUCTURED_RESPONSE", result.Orchestration.Failure.ErrorCode);
    }

    public static void RecoveryEvidenceIsCountedByCycleType()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult(
                "affected",
                [new RimTestResult
                {
                    Test = "fixture-runtime",
                    Status = RimTestValidationStates.Pass
                }],
                0,
                Cancelled: false,
                PrerequisiteRecovery:
                [
                    new RimTestPrerequisiteRecovery(
                        "coordinator",
                        "recovering",
                        1,
                        "DEVBRIDGE_NO_STRUCTURED_RESPONSE",
                        "restart",
                        Checkpoint: "PRE_MUTATION"),
                    new RimTestPrerequisiteRecovery(
                        "coordinator",
                        "recovered",
                        1,
                        Checkpoint: "PRE_MUTATION")
                ]),
            0);

        AssertEqual(1, result.ToolchainRecoveryCount);
        Assert(result.ToolchainRecoveryTypes!.SequenceEqual(["coordinator"]),
            "recovery types must be distinct and deterministic");
        AssertEqual(1, result.Orchestration!.ToolchainRecoveryCount);
        AssertEqual("ASSERTIONS_STARTED", result.Orchestration.LastSafeCheckpoint);
    }

    public static void AgentOutcomeModelHasOnlyThreeValues()
    {
        string[] outcomes =
        [
            ProductionExecutionPolicy.AgentOutcomeFor(
                ProductionExecutionPolicy.Classify("DEVELOPMENT_BUILD_FAILED")),
            ProductionExecutionPolicy.AgentOutcomeFor(
                ProductionExecutionPolicy.Classify("DEVBRIDGE_NO_STRUCTURED_RESPONSE")),
            ProductionExecutionPolicy.AgentOutcomeFor(
                ProductionExecutionPolicy.Classify("DEVBRIDGE_NO_STRUCTURED_RESPONSE"),
                workflowPassed: true)
        ];

        Assert(outcomes.SequenceEqual(["MOD_FAILURE", "TOOLCHAIN_FATAL", "PASS"]),
            "ordinary agent outcomes must remain a three-value contract");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
