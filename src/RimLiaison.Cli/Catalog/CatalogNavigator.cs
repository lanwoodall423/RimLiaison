namespace RimLiaison.Catalog;

public static class CatalogNavigator
{
    public static CatalogTest? FindTest(CatalogDocument catalog, string id)
    {
        return (catalog.Tests ?? [])
            .FirstOrDefault(test => test is not null &&
                string.Equals(test.Id, id, StringComparison.Ordinal));
    }

    public static CatalogSuite? FindSuite(CatalogDocument catalog, string id)
    {
        return (catalog.Suites ?? [])
            .FirstOrDefault(suite => suite is not null &&
                string.Equals(suite.Id, id, StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> ResolvedTestIds(
        CatalogDocument catalog,
        string suiteId)
    {
        Dictionary<string, CatalogSuite> suites = BuildSuiteMap(catalog);
        if (!suites.TryGetValue(suiteId, out _))
        {
            return [];
        }

        var result = new List<string>();
        var seenTests = new HashSet<string>(StringComparer.Ordinal);
        var visitingSuites = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            if (!suites.TryGetValue(id, out CatalogSuite? suite) ||
                !visitingSuites.Add(id))
            {
                return;
            }

            foreach (string testId in suite.Tests ?? [])
            {
                if (!string.IsNullOrWhiteSpace(testId) && seenTests.Add(testId))
                {
                    result.Add(testId);
                }
            }

            foreach (string? nestedId in suite.Suites ?? [])
            {
                if (!string.IsNullOrWhiteSpace(nestedId))
                {
                    Visit(nestedId);
                }
            }

            visitingSuites.Remove(id);
        }

        Visit(suiteId);
        return result;
    }

    public static IReadOnlyList<string> ContainingSuiteIds(
        CatalogDocument catalog,
        string testId)
    {
        Dictionary<string, CatalogSuite> suites = BuildSuiteMap(catalog);
        var containing = new List<string>();

        foreach (CatalogSuite suite in suites.Values)
        {
            if (ContainsTest(suite.Id, testId, suites, new HashSet<string>(StringComparer.Ordinal)))
            {
                containing.Add(suite.Id);
            }
        }

        containing.Sort(StringComparer.Ordinal);
        return containing;
    }

    private static bool ContainsTest(
        string suiteId,
        string testId,
        IReadOnlyDictionary<string, CatalogSuite> suites,
        ISet<string> visiting)
    {
        if (string.IsNullOrWhiteSpace(suiteId))
        {
            return false;
        }

        if (!suites.TryGetValue(suiteId, out CatalogSuite? suite) ||
            !visiting.Add(suiteId))
        {
            return false;
        }

        if ((suite.Tests ?? []).Any(id =>
                string.Equals(id, testId, StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (string? nestedId in suite.Suites ?? [])
        {
            if (!string.IsNullOrWhiteSpace(nestedId) &&
                ContainsTest(nestedId, testId, suites, visiting))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, CatalogSuite> BuildSuiteMap(CatalogDocument catalog)
    {
        var suites = new Dictionary<string, CatalogSuite>(StringComparer.Ordinal);
        foreach (CatalogSuite? suite in catalog.Suites ?? [])
        {
            if (suite is not null &&
                !string.IsNullOrWhiteSpace(suite.Id) &&
                !suites.ContainsKey(suite.Id))
            {
                suites.Add(suite.Id, suite);
            }
        }

        return suites;
    }
}
