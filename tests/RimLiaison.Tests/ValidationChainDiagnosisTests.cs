using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Results;

namespace RimLiaison.Tests;

internal static class ValidationChainDiagnosisTests
{
    public static void DevelopmentBuildFailureIsInfrastructure()
    {
        RimTestSuiteResult result = Result(
            [],
            Freshness("FAILED", "DEVELOPMENT_BUILD_FAILED", "tx-build"),
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.InfrastructureFailure, "DEVELOPMENT_BUILD_FAILED"));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("INFRASTRUCTURE_BLOCKED", diagnosis.OverallResult);
        Equal("build", diagnosis.FirstFailedBoundary);
        Equal("DEVELOPMENT_BUILD_FAILED", diagnosis.Code);
        Equal("not_acquired", diagnosis.Lease);
        Equal(false, diagnosis.ProjectRuntimeExecuted);
    }

    public static void ArtifactFreshnessFailureIsInfrastructure()
    {
        RimTestSuiteResult result = Result(
            [],
            Freshness("FAILED", "RIMTEST_ARTIFACT_FRESHNESS_FAILED", "tx-freshness"),
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "RIMTEST_ARTIFACT_FRESHNESS_FAILED"));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("INFRASTRUCTURE_BLOCKED", diagnosis.OverallResult);
        Equal("artifact-freshness", diagnosis.FirstFailedBoundary);
        Equal("failed", diagnosis.ArtifactFreshness);
    }

    public static void ReadinessIdentityFailureIsInfrastructure()
    {
        RimTestSuiteResult result = Result(
            [],
            Freshness("FAILED", "GENERATION_IDENTITY_MISMATCH", "tx-readiness"),
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "GENERATION_IDENTITY_MISMATCH"));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("INFRASTRUCTURE_BLOCKED", diagnosis.OverallResult);
        Equal("readiness", diagnosis.FirstFailedBoundary);
        Equal("failed", diagnosis.Readiness);
    }

    public static void LeaseFailureIsInfrastructure()
    {
        RimTestSuiteResult result = Result(
            [],
            Freshness("FRESH", null, "tx-lease"),
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "RIMBRIDGE_LEASE_REQUIRED"));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("INFRASTRUCTURE_BLOCKED", diagnosis.OverallResult);
        Equal("lease", diagnosis.FirstFailedBoundary);
        Equal("failed", diagnosis.Lease);
    }

    public static void RuntimeTimeoutIsInfrastructure()
    {
        RimTestSuiteResult result = Result(
            [Test("infrastructure", "DEVBRIDGE_TIMEOUT", "run-timeout", "op-timeout")],
            Freshness("FRESH", null, "tx-runtime"),
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success));
        RimTestValidationChainDiagnosis diagnosis = Require(result);
        Equal("INFRASTRUCTURE_BLOCKED", diagnosis.OverallResult);
        Equal("runtime", diagnosis.FirstFailedBoundary);
        Equal(false, diagnosis.ProjectRuntimeExecuted);
        Equal(false, diagnosis.ProjectFailureObserved);
        Equal(true, diagnosis.RuntimeValidationExecuted);
    }

    public static void ProjectAssertionFailureIsProjectFailure()
    {
        RimTestSuiteResult result = Result(
            [Test("fail", "ASSERTION_FRONTIER", "run-assertion", "op-assertion")],
            Freshness("FRESH", null, "tx-project"),
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("PROJECT_VALIDATION_FAILED", diagnosis.OverallResult);
        Equal("runtime", diagnosis.FirstFailedBoundary);
        Equal(true, diagnosis.ProjectRuntimeExecuted);
        Equal(true, diagnosis.ProjectFailureObserved);
    }

    public static void CompleteChainIsPass()
    {
        RimTestSuiteResult result = Result(
            [Test("pass", null, "run-pass", "op-pass")],
            Freshness("FRESH", null, "tx-pass"),
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success));
        RimTestValidationChainDiagnosis diagnosis = Require(result);

        Equal("PASS", diagnosis.OverallResult);
        Equal("none", diagnosis.FirstFailedBoundary);
        Equal("fresh", diagnosis.ArtifactFreshness);
        Equal("ready", diagnosis.Readiness);
        Equal("acquired", diagnosis.Lease);
    }

    private static RimTestSuiteResult Result(
        IReadOnlyList<RimTestResult> tests,
        RimTestArtifactFreshness freshness,
        DevBridgeAdapterStatus status) =>
        RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult("affected", tests, 0, false),
            1,
            artifactFreshness: freshness,
            freshnessStatus: status,
            freshnessRequested: true,
            workflowId: "wf-chain");

    private static RimTestArtifactFreshness Freshness(
        string evaluationStatus,
        string? errorCode,
        string transactionId) => new()
        {
            EvaluationStatus = evaluationStatus,
            ErrorCode = errorCode,
            TransactionId = transactionId,
            LoadedArtifactFreshnessProven = evaluationStatus == "FRESH"
        };

    private static RimTestResult Test(
        string status,
        string? errorCode,
        string runId,
        string operationId) => new()
        {
            Status = status,
            Test = "frontier",
            ErrorCode = errorCode,
            RunId = runId,
            OperationIds = [operationId],
            Generation = 475
        };

    private static RimTestValidationChainDiagnosis Require(RimTestSuiteResult result) =>
        result.ValidationDiagnosis ?? throw new InvalidOperationException("Validation diagnosis was not projected.");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
