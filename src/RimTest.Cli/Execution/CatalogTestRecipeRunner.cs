using RimTest.Catalog;
using RimTest.DevBridge;

namespace RimTest.Execution;

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

        DevBridgeRecipeRunResult result = await adapter.RunAsync(
            test.Recipe,
            workflowId,
            executionContext,
            cancellationToken).ConfigureAwait(false);
        return new CatalogTestRunResult(test.Id, test.Recipe, result);
    }
}
