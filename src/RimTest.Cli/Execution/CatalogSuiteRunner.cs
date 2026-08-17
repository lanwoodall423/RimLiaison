using System.Diagnostics;
using RimTest.Catalog;
using RimTest.DevBridge;
using RimTest.Results;

namespace RimTest.Execution;

public sealed record CatalogSuiteExecutionResult(
    string SuiteId,
    IReadOnlyList<RimTestResult> Tests,
    int Skipped,
    bool Cancelled);

public sealed class CatalogSuiteRunner
{
    private readonly IDevBridgeRecipeAdapter recipeAdapter;
    private readonly CatalogTestExecutionService testExecutor;

    public CatalogSuiteRunner(
        IDevBridgeRecipeAdapter recipeAdapter,
        CatalogTestExecutionService testExecutor)
    {
        this.recipeAdapter = recipeAdapter ?? throw new ArgumentNullException(nameof(recipeAdapter));
        this.testExecutor = testExecutor ?? throw new ArgumentNullException(nameof(testExecutor));
    }

    public async Task<CatalogSuiteExecutionResult> RunAsync(
        CatalogDocument catalog,
        string suiteId,
        IReadOnlyList<string> testIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(testIds);

        string[] orderedTestIds = testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var results = new List<RimTestResult>();

        // A multi-test run is preflighted through DevBridge's existing plan
        // operation. Plan output is never used to share lifecycle state or
        // skip a recipe; DevBridge remains the execution authority.
        if (orderedTestIds.Length > 1)
        {
            foreach (string testId in orderedTestIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Aggregate(
                        suiteId,
                        results,
                        orderedTestIds.Length - results.Count,
                        cancelled: true);
                }

                CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
                if (test is null)
                {
                    results.Add(RimTestResultFactory.Invalid(testId, "TEST_NOT_FOUND"));
                    continue;
                }

                DevBridgeRecipePlanResult plan = await TryPlanAsync(
                        test.Recipe,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (plan.Status.Outcome == DevBridgeOutcomeKind.Cancelled)
                {
                    results.Add(RimTestResultFactory.Cancelled(testId));
                    return Aggregate(
                        suiteId,
                        results,
                        orderedTestIds.Length - results.Count,
                        cancelled: true);
                }

                if (plan.Status.Outcome != DevBridgeOutcomeKind.Success ||
                    plan.Plan is null)
                {
                    results.Add(RimTestResultFactory.Infrastructure(
                        testId,
                        plan.Status.ErrorCode ?? "DEVBRIDGE_PLAN_FAILED"));
                }
            }

            if (results.Count > 0)
            {
                return Aggregate(
                    suiteId,
                    results,
                    orderedTestIds.Length - results.Count,
                    cancelled: false);
            }
        }

        for (int index = 0; index < orderedTestIds.Length; index++)
        {
            string testId = orderedTestIds[index];
            if (cancellationToken.IsCancellationRequested)
            {
                return Aggregate(
                    suiteId,
                    results,
                    orderedTestIds.Length - results.Count,
                    cancelled: true);
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                CatalogTestExecutionResult execution = await testExecutor.RunAsync(
                        catalog,
                        testId,
                        started,
                        cancellationToken)
                    .ConfigureAwait(false);
                results.Add(execution.Result);
                if (string.Equals(execution.Result.Status, "cancelled", StringComparison.Ordinal))
                {
                    return Aggregate(
                        suiteId,
                        results,
                        orderedTestIds.Length - results.Count,
                        cancelled: true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Aggregate(
                    suiteId,
                    results,
                    orderedTestIds.Length - results.Count,
                    cancelled: true);
            }
            catch (Exception)
            {
                results.Add(RimTestResultFactory.Infrastructure(testId, "RIMTEST_CHILD_EXECUTION_FAILED"));
            }
        }

        return Aggregate(suiteId, results, 0, cancelled: false);
    }

    private async Task<DevBridgeRecipePlanResult> TryPlanAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await recipeAdapter.PlanAsync(recipeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"),
                null);
        }
        catch (Exception)
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_PLAN_FAILED"),
                null);
        }
    }

    private static CatalogSuiteExecutionResult Aggregate(
        string suiteId,
        IReadOnlyList<RimTestResult> results,
        int skipped,
        bool cancelled) =>
        new(
            suiteId,
            results,
            Math.Max(0, skipped),
            cancelled);
}
