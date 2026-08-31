using RimLiaison.DevBridge;
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
    public static void ProjectBuildFailureNormalizesToModFailure()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult("affected", [], 0, Cancelled: false),
            0,
            artifactFreshness: new RimTestArtifactFreshness
            {
                EvaluationStatus = "FAILED",
                ErrorCode = "DEVELOPMENT_BUILD_FAILED"
            },
            freshnessStatus: new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "DEVELOPMENT_BUILD_FAILED"),
            freshnessRequested: true);

        AssertEqual("MOD_FAILURE", result.Orchestration!.AgentOutcome);
        AssertEqual("ProjectConfigurationFailure", result.Orchestration.Failure!.Classification);
    }
    public static void ExplicitBuildOwnerControlsClassification()
    {
        AssertEqual(
            ProductionFailureClassification.ProjectConfigurationFailure,
            ProductionExecutionPolicy.Classify(
                "DEVELOPMENT_BUILD_FAILED",
                "project compiler failure",
                "PROJECT_BUILD").Classification);
        AssertEqual(
            ProductionFailureClassification.SelfHealable,
            ProductionExecutionPolicy.Classify(
                "DEVELOPMENT_BUILD_FAILED",
                "toolchain compiler failure",
                "TOOLCHAIN_BUILD").Classification);
        Assert(
            ProductionExecutionPolicy.RequiresPreMutationEscalation(
                "DEVELOPMENT_BUILD_FAILED",
                "TOOLCHAIN_BUILD"),
            "toolchain-owned build failures must enter bounded recovery");

        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult("affected", [], 0, Cancelled: false),
            0,
            artifactFreshness: new RimTestArtifactFreshness
            {
                EvaluationStatus = "FAILED",
                ErrorCode = "DEVELOPMENT_BUILD_FAILED",
                BuildOwnerType = "PROJECT_BUILD",
                BuildOwnerProject = "Deferred Reality Framework",
                BuildTarget = "Source/DeferredRealityFramework.csproj",
                BuildCommandIdentity = "dotnet build",
                BuildEvidenceId = "build:DeferredRealityFramework"
            },
            freshnessStatus: new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "DEVELOPMENT_BUILD_FAILED"),
            freshnessRequested: true);

        AssertEqual("MOD_FAILURE", result.Orchestration!.AgentOutcome);
        AssertEqual(
            "Deferred Reality Framework",
            result.Orchestration.Failure!.BuildOwnerProject);
        AssertEqual(
            "Source/DeferredRealityFramework.csproj",
            result.Orchestration.Failure.BuildTarget);
        AssertEqual(
            "build:DeferredRealityFramework",
            result.Orchestration.Failure.BuildEvidenceId);
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
