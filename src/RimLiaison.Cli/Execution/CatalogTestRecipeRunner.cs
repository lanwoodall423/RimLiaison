using RimLiaison.Catalog;
using RimLiaison.DevBridge;
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
        return new CatalogTestRunResult(test.Id, test.Recipe, result);
    }
}
