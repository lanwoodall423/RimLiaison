namespace RimTest.Catalog;

public static class CatalogValidator
{
    private const int MaxIdLength = 64;
    private const int MaxRecipeLength = 128;
    private const int MaxDescriptionLength = 512;
    private const int MaxTagLength = 64;
    private const int MaxCoverageKindLength = 64;
    private const int MaxCoverageNameLength = 256;
    private const int MaxReuseKeyLength = 128;
    private const int MaxResetRecipeLength = 128;

    public static CatalogValidationResult Validate(
        CatalogDocument catalog,
        IReadOnlySet<string>? knownRecipeIds = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var errors = new List<CatalogIssue>();
        List<CatalogTest> tests = catalog.Tests ?? [];
        List<CatalogSuite> suites = catalog.Suites ?? [];

        if (catalog.Tests is null)
        {
            errors.Add(new CatalogIssue(
                "TEST_COLLECTION_INVALID",
                "Catalog tests must be an array.",
                "tests"));
        }

        if (catalog.Suites is null)
        {
            errors.Add(new CatalogIssue(
                "SUITE_COLLECTION_INVALID",
                "Catalog suites must be an array.",
                "suites"));
        }

        if (!string.Equals(
                catalog.SchemaVersion,
                CatalogSchema.Current,
                StringComparison.Ordinal))
        {
            errors.Add(new CatalogIssue(
                "SCHEMA_VERSION_UNSUPPORTED",
                $"Catalog must use schema {CatalogSchema.Current}.",
                "schemaVersion"));
        }

        var testIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < tests.Count; index++)
        {
            CatalogTest? test = tests[index];
            string path = $"tests[{index}]";
            if (test is null)
            {
                errors.Add(new CatalogIssue("TEST_INVALID", "Test entry must be an object.", path));
                continue;
            }

            string id = test.Id ?? string.Empty;
            ValidateIdentifier(id, $"{path}.id", "TEST_ID_INVALID", errors);
            AddDuplicateIssue(testIds, id, index, "TEST_ID_DUPLICATE", $"{path}.id", errors);

            string recipe = test.Recipe ?? string.Empty;
            if (string.IsNullOrWhiteSpace(recipe))
            {
                errors.Add(new CatalogIssue(
                    "TEST_RECIPE_INVALID",
                    "Test recipe must be non-empty.",
                    $"{path}.recipe"));
            }
            else if (recipe.Length > MaxRecipeLength)
            {
                errors.Add(new CatalogIssue(
                    "TEST_RECIPE_INVALID",
                    $"Test recipe must be at most {MaxRecipeLength} characters.",
                    $"{path}.recipe"));
            }

            ValidateDescription(test.Description, $"{path}.description", errors);
            ValidateTags(test.Tags, $"{path}.tags", errors);
            ValidateCoverage(test.Covers, $"{path}.covers", errors);
            ValidateIsolation(test.Isolation, $"{path}.isolation", errors);

            if (knownRecipeIds is not null &&
                !string.IsNullOrWhiteSpace(recipe) &&
                !knownRecipeIds.Contains(recipe))
            {
                errors.Add(new CatalogIssue(
                    "MISSING_RECIPE_REFERENCE",
                    $"No DevBridge recipe with id {recipe} was found in the supplied recipe list.",
                    $"{path}.recipe"));
            }
        }

        var suiteIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < suites.Count; index++)
        {
            CatalogSuite? suite = suites[index];
            string path = $"suites[{index}]";
            if (suite is null)
            {
                continue;
            }

            string id = suite.Id ?? string.Empty;
            ValidateIdentifier(id, $"{path}.id", "SUITE_ID_INVALID", errors);
            AddDuplicateIssue(suiteIds, id, index, "SUITE_ID_DUPLICATE", $"{path}.id", errors);
        }

        for (int index = 0; index < suites.Count; index++)
        {
            CatalogSuite? suite = suites[index];
            string path = $"suites[{index}]";
            if (suite is null)
            {
                errors.Add(new CatalogIssue("SUITE_INVALID", "Suite entry must be an object.", path));
                continue;
            }

            ValidateDescription(suite.Description, $"{path}.description", errors);

            List<string> directTests = suite.Tests ?? [];
            List<string> nestedSuites = suite.Suites ?? [];
            if (suite.Tests is null)
            {
                errors.Add(new CatalogIssue(
                    "SUITE_TESTS_INVALID",
                    "Suite tests must be an array.",
                    $"{path}.tests"));
            }

            if (suite.Suites is null)
            {
                errors.Add(new CatalogIssue(
                    "SUITE_SUITES_INVALID",
                    "Suite suites must be an array.",
                    $"{path}.suites"));
            }

            if (directTests.Count == 0 && nestedSuites.Count == 0)
            {
                errors.Add(new CatalogIssue(
                    "SUITE_EMPTY",
                    "Suite must contain at least one test or nested suite.",
                    path));
            }

            ValidateReferences(
                directTests,
                testIds,
                $"{path}.tests",
                "TEST_REFERENCE_INVALID",
                "UNKNOWN_TEST_REFERENCE",
                errors);
            ValidateReferences(
                nestedSuites,
                suiteIds,
                $"{path}.suites",
                "SUITE_REFERENCE_INVALID",
                "UNKNOWN_SUITE_REFERENCE",
                errors);
        }

        foreach (KeyValuePair<string, int> pair in testIds)
        {
            if (suiteIds.ContainsKey(pair.Key) && pair.Key.Length > 0)
            {
                errors.Add(new CatalogIssue(
                    "ID_COLLISION",
                    $"Test and suite share the id {pair.Key}.",
                    $"tests[{pair.Value}].id"));
            }
        }

        DetectSuiteCycles(suites, errors);

        errors.Sort(static (left, right) =>
        {
            int path = string.CompareOrdinal(left.Path ?? string.Empty, right.Path ?? string.Empty);
            if (path != 0)
            {
                return path;
            }

            int code = string.CompareOrdinal(left.Code, right.Code);
            return code != 0
                ? code
                : string.CompareOrdinal(left.Message, right.Message);
        });

        return new CatalogValidationResult(catalog, errors, knownRecipeIds is not null);
    }

    private static void ValidateIdentifier(
        string value,
        string path,
        string code,
        ICollection<CatalogIssue> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new CatalogIssue(code, "Identifier must be non-empty.", path));
            return;
        }

        if (value.Length > MaxIdLength)
        {
            errors.Add(new CatalogIssue(
                code,
                $"Identifier must be at most {MaxIdLength} characters.",
                path));
        }

        if (value.Any(char.IsWhiteSpace))
        {
            errors.Add(new CatalogIssue(
                code,
                "Identifier must not contain whitespace.",
                path));
        }
    }

    private static void AddDuplicateIssue(
        IDictionary<string, int> ids,
        string id,
        int index,
        string code,
        string path,
        ICollection<CatalogIssue> errors)
    {
        if (ids.TryGetValue(id, out int firstIndex))
        {
            errors.Add(new CatalogIssue(
                code,
                $"Identifier {id} is duplicated; first declared at index {firstIndex}.",
                path));
            return;
        }

        ids[id] = index;
    }

    private static void ValidateDescription(
        string? description,
        string path,
        ICollection<CatalogIssue> errors)
    {
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            errors.Add(new CatalogIssue(
                "DESCRIPTION_TOO_LONG",
                $"Description must be at most {MaxDescriptionLength} characters.",
                path));
        }
    }

    private static void ValidateTags(
        List<string>? tags,
        string path,
        ICollection<CatalogIssue> errors)
    {
        if (tags is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < tags.Count; index++)
        {
            string tag = tags[index] ?? string.Empty;
            string tagPath = $"{path}[{index}]";
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength)
            {
                errors.Add(new CatalogIssue(
                    "TAG_INVALID",
                    $"Tag must be non-empty and at most {MaxTagLength} characters.",
                    tagPath));
            }

            if (!seen.Add(tag))
            {
                errors.Add(new CatalogIssue(
                    "TAG_DUPLICATE",
                    $"Tag {tag} is duplicated.",
                    tagPath));
            }
        }
    }

    private static void ValidateCoverage(
        List<CatalogCoverage>? covers,
        string path,
        ICollection<CatalogIssue> errors)
    {
        if (covers is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < covers.Count; index++)
        {
            CatalogCoverage? cover = covers[index];
            string coverPath = $"{path}[{index}]";
            if (cover is null)
            {
                errors.Add(new CatalogIssue(
                    "COVERAGE_INVALID",
                    "Coverage entry must be an object.",
                    coverPath));
                continue;
            }

            string kind = cover.Kind ?? string.Empty;
            string name = cover.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(kind) || kind.Length > MaxCoverageKindLength)
            {
                errors.Add(new CatalogIssue(
                    "COVERAGE_KIND_INVALID",
                    $"Coverage kind must be non-empty and at most {MaxCoverageKindLength} characters.",
                    $"{coverPath}.kind"));
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxCoverageNameLength)
            {
                errors.Add(new CatalogIssue(
                    "COVERAGE_NAME_INVALID",
                    $"Coverage name must be non-empty and at most {MaxCoverageNameLength} characters.",
                    $"{coverPath}.name"));
            }

            string key = kind + "\u001f" + name;
            if (!seen.Add(key))
            {
                errors.Add(new CatalogIssue(
                    "COVERAGE_DUPLICATE",
                    $"Coverage {kind}:{name} is duplicated.",
                    coverPath));
            }
        }
    }

    private static void ValidateIsolation(
        CatalogRecipeIsolation? isolation,
        string path,
        ICollection<CatalogIssue> errors)
    {
        if (isolation is null)
        {
            return;
        }

        string? reuseKey = isolation.ReuseKey;
        if (reuseKey is not null &&
            (string.IsNullOrWhiteSpace(reuseKey) || reuseKey.Length > MaxReuseKeyLength))
        {
            errors.Add(new CatalogIssue(
                "ISOLATION_REUSE_KEY_INVALID",
                $"Isolation reuseKey must be non-empty and at most {MaxReuseKeyLength} characters.",
                $"{path}.reuseKey"));
        }

        string? resetRecipe = isolation.ResetRecipe;
        if (resetRecipe is not null &&
            (string.IsNullOrWhiteSpace(resetRecipe) || resetRecipe.Length > MaxResetRecipeLength))
        {
            errors.Add(new CatalogIssue(
                "ISOLATION_RESET_RECIPE_INVALID",
                $"Isolation resetRecipe must be non-empty and at most {MaxResetRecipeLength} characters.",
                $"{path}.resetRecipe"));
        }

        if (isolation.Mode == CatalogRecipeIsolationMode.Unknown)
        {
            return;
        }

        if (CatalogRecipeIsolationPolicy.CanShareGeneration(isolation) &&
            string.IsNullOrWhiteSpace(reuseKey))
        {
            errors.Add(new CatalogIssue(
                "ISOLATION_REUSE_KEY_REQUIRED",
                "Reusable recipes must declare a non-empty isolation reuseKey.",
                $"{path}.reuseKey"));
        }

        if (CatalogRecipeIsolationPolicy.RequiresResetBetweenRecipes(isolation) &&
            string.IsNullOrWhiteSpace(resetRecipe))
        {
            errors.Add(new CatalogIssue(
                "ISOLATION_RESET_RECIPE_REQUIRED",
                "fixture-resettable recipes must declare a deterministic resetRecipe.",
                $"{path}.resetRecipe"));
        }

        if (!CatalogRecipeIsolationPolicy.RequiresResetBetweenRecipes(isolation) &&
            !string.IsNullOrWhiteSpace(resetRecipe))
        {
            errors.Add(new CatalogIssue(
                "ISOLATION_RESET_RECIPE_UNEXPECTED",
                "resetRecipe is only valid for fixture-resettable recipes.",
                $"{path}.resetRecipe"));
        }
    }

    private static void ValidateReferences(
        List<string> references,
        IReadOnlyDictionary<string, int> knownIds,
        string path,
        string invalidCode,
        string unknownCode,
        ICollection<CatalogIssue> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < references.Count; index++)
        {
            string reference = references[index] ?? string.Empty;
            string referencePath = $"{path}[{index}]";
            if (string.IsNullOrWhiteSpace(reference))
            {
                errors.Add(new CatalogIssue(
                    invalidCode,
                    "Reference must be non-empty.",
                    referencePath));
                continue;
            }

            if (!knownIds.ContainsKey(reference))
            {
                errors.Add(new CatalogIssue(
                    unknownCode,
                    $"Referenced id {reference} does not exist.",
                    referencePath));
            }

            if (!seen.Add(reference))
            {
                errors.Add(new CatalogIssue(
                    "DUPLICATE_SUITE_MEMBER",
                    $"Reference {reference} is duplicated.",
                    referencePath));
            }
        }
    }

    private static void DetectSuiteCycles(
        List<CatalogSuite> suites,
        ICollection<CatalogIssue> errors)
    {
        var byId = new Dictionary<string, CatalogSuite>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < suites.Count; index++)
        {
            CatalogSuite? suite = suites[index];
            string id = suite?.Id ?? string.Empty;
            if (suite is not null && !string.IsNullOrWhiteSpace(id) && byId.TryAdd(id, suite))
            {
                paths[id] = $"suites[{index}].suites";
            }
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        void Visit(string id)
        {
            if (state.TryGetValue(id, out int currentState))
            {
                if (currentState == 2)
                {
                    return;
                }

                int start = stack.IndexOf(id);
                IEnumerable<string> cycle = start >= 0
                    ? stack.Skip(start).Append(id)
                    : new[] { id, id };
                errors.Add(new CatalogIssue(
                    "SUITE_CYCLE",
                    $"Suite cycle detected: {string.Join(" -> ", cycle)}.",
                    paths.TryGetValue(id, out string? path) ? path : "suites"));
                return;
            }

            state[id] = 1;
            stack.Add(id);
            foreach (string? nested in byId[id].Suites ?? [])
            {
                if (!string.IsNullOrWhiteSpace(nested) && byId.ContainsKey(nested))
                {
                    Visit(nested);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        foreach (string id in byId.Keys.OrderBy(static value => value, StringComparer.Ordinal))
        {
            Visit(id);
        }
    }
}
