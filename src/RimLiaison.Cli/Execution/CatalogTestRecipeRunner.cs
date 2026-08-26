using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Observability;
using RimLiaison.Profiling;
using RimLiaison.Validation;

namespace RimLiaison.Execution;

public sealed record CatalogTestRunResult(
    string TestId,
    string RecipeId,
    DevBridgeRecipeRunResult RecipeResult,
    ValidationCapabilityPreflightResult? CapabilityPreflight = null);

public interface ICatalogTestRecipeRunner
{
    Task<CatalogTestRunResult> RunAsync(
        CatalogDocument catalog,
        string testId,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        DevBridgeRecipeExecutionContext? executionContext = null);
}

public sealed class CatalogTestRecipeRunner : ICatalogTestRecipeRunner
{
    private readonly IDevBridgeRecipeAdapter adapter;
    private readonly ValidationCapabilityNegotiator? capabilityNegotiator;

    public CatalogTestRecipeRunner(
        IDevBridgeRecipeAdapter adapter,
        IDevBridgeCapabilityAdapter? capabilityAdapter = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        capabilityNegotiator = capabilityAdapter is null
            ? null
            : new ValidationCapabilityNegotiator(capabilityAdapter);
    }

    public async Task<CatalogTestRunResult> RunAsync(
        CatalogDocument catalog,
        string testId,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        DevBridgeRecipeExecutionContext? executionContext = null)
    {
        CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
        if (test is null)
        {
            throw new KeyNotFoundException($"Test was not found: {testId}.");
        }
        ValidationPolicyEvaluator.Define(
            test.Id,
            test.ValidationClassification,
            test.ValidationSource ?? ValidationRequirementSource.TOOLCHAIN_CONTRACT,
            test.Description ?? "Catalog validation " + test.Id);


        ValidationCapabilityPreflightResult preflight =
            test.RequiredCapabilities is not { Count: > 0 }
                ? new(ValidationCapabilityPreflightOutcome.Available, [])
                : capabilityNegotiator is null
                    ? new(
                        ValidationCapabilityPreflightOutcome.InfrastructureFailure,
                        [],
                        ValidationCapabilitySchema.DiscoveryFailedCode)
                    : await capabilityNegotiator.NegotiateAsync(
                            test,
                            workflowId,
                            executionContext?.LeaseId,
                            cancellationToken)
                        .ConfigureAwait(false);
        if (!preflight.IsAvailable)
        {
            string errorCode = preflight.ErrorCode ??
                preflight.Evidence.FirstOrDefault()?.ErrorCode ??
                ValidationCapabilitySchema.DiscoveryFailedCode;
            if (preflight.IsBlocked || preflight.IsUnavailableOptional)
            {
                ValidationCapabilityEvidence[] gaps = preflight.Evidence.ToArray();
                ValidationCapabilityEvidence first = gaps.FirstOrDefault() ??
                    new ValidationCapabilityEvidence
                    {
                        ErrorCode = errorCode,
                        ValidationId = test.Id,
                        RequiredCapabilityId = "unknown",
                        Reason = "Capability discovery did not produce evidence.",
                        ProbableOwner = "RimLiaison",
                        RecommendedRemediation = "Inspect capability discovery.",
                        Fingerprint = "capability|unknown|provider|any",
                        Classification = test.ValidationClassification,
                        Blocking = test.ValidationClassification ==
                            ValidationClassification.REQUIRED
                    };
                bool blocking = preflight.IsBlocked;
                string outcome = blocking ? "blocked" : "not_available";
                string finding = blocking
                    ? "TOOLING_FAILURE"
                    : "OPTIONAL_VALIDATION_UNAVAILABLE";
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.ValidationCapabilityBlocked,
                    blocking
                        ? $"Validation blocked: required capability {first.RequiredCapabilityId} is unavailable. No product failure was observed. Probable owner: {first.ProbableOwner}."
                        : $"Optional validation not available: capability {first.RequiredCapabilityId} could not be used. No product failure was observed.",
                    new
                    {
                        operationKey = "test:" + test.Id,
                        validationId = test.Id,
                        validationClassification = test.ValidationClassification.ToString(),
                        state = first.State,
                        outcome,
                        issueKind = finding,
                        blocking,
                        errorCode = first.ErrorCode,
                        capabilityId = first.RequiredCapabilityId,
                        requiredCapabilityId = first.RequiredCapabilityId,
                        expectedProvider = first.ExpectedProvider,
                        discoveredProvider = first.DiscoveredProvider,
                        reason = first.Reason,
                        probableOwner = first.ProbableOwner,
                        componentOwner = first.ProbableOwner,
                        recommendedRemediation = first.RecommendedRemediation,
                        recommendation = first.RecommendedRemediation,
                        evidenceReference = first.EvidenceLink,
                        operationAttempted = false,
                        workflowId,
                        agentId = first.AgentId,
                        fingerprint = first.Fingerprint
                    });
            }

            return new CatalogTestRunResult(
                test.Id,
                test.Recipe,
                NotAttemptedResult(test.Recipe, errorCode, workflowId),
                preflight);
        }

        AgentOperationScope? observation = AgentObservabilityRuntime.BeginOperation(
            "tool",
            "test.recipe.run",
            DevelopmentStage.Testing,
            "test:" + test.Id,
            new
            {
                toolName = "DevBridge",
                operationType = "test",
                testId = test.Id,
                recipe = test.Recipe
            });
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Test started.",
            new
            {
                operationKey = "test:" + test.Id,
                testId = test.Id,
                recipe = test.Recipe
            });
        try
        {
            DevBridgeRecipeRunResult result = await ProfilerActivity.ObserveAsync(
                    "recipe.run",
                    "testing",
                    () => adapter.RunAsync(
                        test.Recipe,
                        workflowId,
                        executionContext,
                        cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome switch
                            {
                                DevBridgeOutcomeKind.Success => "success",
                                DevBridgeOutcomeKind.Cancelled => "cancelled",
                                DevBridgeOutcomeKind.TestFailure => "test-failure",
                                _ => "failure"
                            },
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Generation);
                        ProfilerActivity.SetCounts(activity, items: value.Operations.Count);
                    },
                    phase: "recipe",
                    target: test.Recipe,
                    scope: "recipe")
                .ConfigureAwait(false);
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operationKey"] = "test:" + test.Id,
                ["testId"] = test.Id,
                ["recipe"] = test.Recipe,
                ["generation"] = result.Generation,
                ["exitCode"] = result.Status.ProcessExitCode,
                ["errorCode"] = result.Status.ErrorCode,
                ["operationCount"] = result.Operations.Count
            };
            if (result.Status.Outcome == DevBridgeOutcomeKind.Success)
            {
                observation?.Complete("Test tool execution completed.", details);
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestPassed,
                    "Test passed.",
                    details);
            }
            else if (result.Status.Outcome is not DevBridgeOutcomeKind.TestFailure and
                not DevBridgeOutcomeKind.Cancelled &&
                test.ValidationClassification != ValidationClassification.REQUIRED)
            {
                details["validationClassification"] = test.ValidationClassification.ToString();
                details["validationId"] = test.Id;
                details["issueKind"] = "OPTIONAL_VALIDATION_UNAVAILABLE";
                details["blocking"] = false;
                details["recommendation"] = "Repair the optional validation owner or recipe, then rerun when relevant.";
                observation?.Complete("Optional validation was unavailable.", details);
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.ValidationCapabilityBlocked,
                    "Optional validation was unavailable; no product failure was observed.",
                    details);
            }
            else
            {
                observation?.Fail(
                    "Test tool execution failed.",
                    result.Status.ErrorCode ?? "TEST_EXECUTION_FAILED",
                    details,
                    timeout: result.Status.Outcome == DevBridgeOutcomeKind.Timeout);
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestFailed,
                    "Test failed.",
                    details);
            }
            return new CatalogTestRunResult(test.Id, test.Recipe, result, preflight);
        }
        catch (OperationCanceledException)
        {
            observation?.Fail("Test was cancelled.", "RIMTEST_CANCELLED");
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.TestFailed,
                "Test was cancelled.",
                new { testId = test.Id, operationKey = "test:" + test.Id, outcome = "cancelled" });
            throw;
        }
        catch (Exception exception)
        {
            observation?.Fail(
                "Test execution raised an exception.",
                "TEST_EXECUTION_EXCEPTION",
                new { testId = test.Id, error = AgentObservabilityData.BoundText(exception.Message, 1024) });
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.TestFailed,
                "Test execution raised an exception.",
                new
                {
                    testId = test.Id,
                    operationKey = "test:" + test.Id,
                    errorCode = "TEST_EXECUTION_EXCEPTION"
                });
            throw;
        }
        finally
        {
            observation?.Dispose();
        }
    }

    private static DevBridgeRecipeRunResult NotAttemptedResult(
        string recipeId,
        string errorCode,
        string? workflowId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode,
                "Validation preflight did not permit recipe execution."),
            Passed: null,
            RunId: null,
            Generation: null,
            LeaseId: null,
            Evidence: null,
            EvidenceId: null,
            FailureFingerprint: null,
            FinalNextAction: "inspect-validation-capability",
            RestartRequired: null,
            LaunchesConsumed: 0,
            Operations: [],
            WorkflowId: workflowId);
}

