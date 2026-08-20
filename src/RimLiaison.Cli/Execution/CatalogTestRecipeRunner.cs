using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Observability;
using RimLiaison.Profiling;

namespace RimLiaison.Execution;

public sealed record CatalogTestRunResult(
    string TestId,
    string RecipeId,
    DevBridgeRecipeRunResult RecipeResult);

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

    public CatalogTestRecipeRunner(IDevBridgeRecipeAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
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
            return new CatalogTestRunResult(test.Id, test.Recipe, result);
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
}
