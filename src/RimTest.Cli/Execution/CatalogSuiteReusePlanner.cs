using RimTest.Catalog;

namespace RimTest.Execution;

public sealed record CatalogSuiteReuseGroup(
    string ReuseKey,
    CatalogRecipeIsolationMode Mode,
    IReadOnlyList<string> TestIds,
    string? ResetRecipe);

public sealed record CatalogSuiteReusePlan(
    int Selected,
    IReadOnlyList<CatalogSuiteReuseGroup> Groups)
{
    public bool HasReusableGroups => Groups.Count > 0;
}

public static class CatalogSuiteReusePlanner
{
    public static CatalogSuiteReusePlan Plan(
        CatalogDocument catalog,
        IReadOnlyList<string> orderedTestIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(orderedTestIds);

        var groups = new List<CatalogSuiteReuseGroup>();
        CatalogSuiteReuseGroupBuilder? current = null;
        foreach (string testId in orderedTestIds)
        {
            CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
            if (test is null)
            {
                Flush(current, groups);
                current = null;
                continue;
            }

            CatalogRecipeIsolation isolation = CatalogRecipeIsolationPolicy.Resolve(test);
            string? reuseKey = CatalogRecipeIsolationPolicy.ShareKey(isolation);
            if (reuseKey is null || !CatalogRecipeIsolationPolicy.CanShareGeneration(isolation))
            {
                Flush(current, groups);
                current = null;
                continue;
            }

            if (current is null || !CatalogRecipeIsolationPolicy.CanJoin(current.Isolation, isolation))
            {
                Flush(current, groups);
                current = new CatalogSuiteReuseGroupBuilder(test.Id, isolation, reuseKey);
            }
            else
            {
                current.TestIds.Add(test.Id);
            }
        }

        Flush(current, groups);
        return new CatalogSuiteReusePlan(
            orderedTestIds.Count,
            groups
                .Where(static group => group.TestIds.Count > 1)
                .Select(static group => group)
                .ToArray());
    }

    private static void Flush(
        CatalogSuiteReuseGroupBuilder? group,
        ICollection<CatalogSuiteReuseGroup> groups)
    {
        if (group is not null)
        {
            groups.Add(group.ToRecord());
        }
    }

    private sealed class CatalogSuiteReuseGroupBuilder
    {
        internal CatalogSuiteReuseGroupBuilder(
            string testId,
            CatalogRecipeIsolation isolation,
            string reuseKey)
        {
            Isolation = isolation;
            ReuseKey = reuseKey;
            TestIds = [testId];
        }

        internal CatalogRecipeIsolation Isolation { get; }
        internal string ReuseKey { get; }
        internal List<string> TestIds { get; }

        internal CatalogSuiteReuseGroup ToRecord() => new(
            ReuseKey,
            Isolation.Mode,
            TestIds.ToArray(),
            Isolation.ResetRecipe);
    }
}
