using System.Globalization;
using System.Text.Json;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Profiling;
using RimLiaison.Results;

namespace RimLiaison.Tests;

internal static class FailFastOrderingTests
{
    public static void HistoricallyFailureProneCheapTestsMoveEarlier()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("expensive-stable", "recipe-expensive"),
            ReusableTest("cheap-failure", "recipe-cheap"));
        CatalogSuiteReusePlan plan = CreatePlan(
            catalog,
            "expensive-stable",
            "cheap-failure");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-08-19T12:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-expensive", 4, 0, 40_000),
                new HistoryInput("recipe-cheap", 4, 3, 4_000, Retries: 2));

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(
                ["cheap-failure", "expensive-stable"],
                result.ExecutionOrder);
            Assert(result.Summary.Used, "Historical failure evidence should be applied.");
            AssertEqual("history-applied", result.Summary.Reason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void ExpensiveStableTestsMoveLater()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("stable-expensive", "recipe-expensive"),
            ReusableTest("stable-cheap", "recipe-cheap"));
        CatalogSuiteReusePlan plan = CreatePlan(
            catalog,
            "stable-expensive",
            "stable-cheap");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-expensive", 5, 0, 75_000),
                new HistoryInput("recipe-cheap", 5, 0, 2_500));

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(
                ["stable-cheap", "stable-expensive"],
                result.ExecutionOrder);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void NoHistoryFallsBackDeterministically()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "beta", "alpha");
        string directory = CreateTempDirectory();
        try
        {
            CatalogSuiteFailFastOrderingResult first =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, DateTimeOffset.UtcNow);
            CatalogSuiteFailFastOrderingResult second =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, DateTimeOffset.UtcNow);

            AssertSequence(plan.ExecutionOrder, first.ExecutionOrder);
            AssertSequence(first.ExecutionOrder, second.ExecutionOrder);
            Assert(!first.Summary.Used, "Missing history must not influence ordering.");
            AssertEqual("no-history", first.Summary.Reason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void CorruptStaleAndIncompatibleHistoryIsIgnored()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "beta", "alpha");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "rimliaison-corrupt.json"),
                "not-json");
            WriteHistory(
                directory,
                catalog,
                plan,
                now.AddDays(-CatalogSuiteFailFastOrdering.MaximumHistoryAgeDays - 1),
                [new HistoryInput("recipe-beta", 8, 8, 1_000)],
                "rimliaison-stale.json");

            CatalogSuiteFailFastOrderingResult stale =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);
            AssertSequence(plan.ExecutionOrder, stale.ExecutionOrder);
            AssertEqual("history-invalid-or-stale", stale.Summary.Reason);

            DeleteDirectory(directory);
            Directory.CreateDirectory(directory);
            CatalogDocument otherCatalog = CreateCatalog(
                ReusableTest("other-alpha", "recipe-other-alpha"),
                ReusableTest("other-beta", "recipe-other-beta"));
            CatalogSuiteReusePlan otherPlan = CreatePlan(
                otherCatalog,
                "other-alpha",
                "other-beta");
            WriteHistory(
                directory,
                otherCatalog,
                otherPlan,
                now,
                [new HistoryInput("recipe-other-alpha", 8, 8, 1_000)],
                "rimliaison-incompatible.json");

            CatalogSuiteFailFastOrderingResult incompatible =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);
            AssertSequence(plan.ExecutionOrder, incompatible.ExecutionOrder);
            AssertEqual("history-incompatible", incompatible.Summary.Reason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void InsufficientHistoryFallsBackDeterministically()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "beta", "alpha");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                [new HistoryInput("recipe-beta", 1, 1, 1_000)]);

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(plan.ExecutionOrder, result.ExecutionOrder);
            Assert(!result.Summary.Used,
                "Insufficient observations must not influence ordering.");
            AssertEqual("history-insufficient", result.Summary.Reason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void PartialHistoryDoesNotPreferOneGroupMember()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "alpha", "beta");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                [new HistoryInput("recipe-beta", 5, 5, 500)]);

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(plan.ExecutionOrder, result.ExecutionOrder);
            Assert(!result.Summary.Used,
                "Partial group history must not create a learned preference.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void SelectedTestMembershipNeverChanges()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"),
            ReusableTest("gamma", "recipe-gamma"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "gamma", "alpha", "beta");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-alpha", 5, 5, 500),
                new HistoryInput("recipe-beta", 5, 0, 500),
                new HistoryInput("recipe-gamma", 5, 0, 500));
            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSetEqual(plan.ExecutionOrder, result.ExecutionOrder);
            AssertEqual(plan.Selected, result.ExecutionOrder.Count);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void GenerationReuseSafetyDominatesHeuristicOrdering()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("group-stable", "recipe-stable"),
            ReusableTest("group-failure", "recipe-failure"),
            new CatalogTest
            {
                Id = "fresh-boundary",
                Recipe = "recipe-fresh",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FreshGenerationRequired
                }
            });
        CatalogSuiteReusePlan plan = CreatePlan(
            catalog,
            "group-stable",
            "group-failure",
            "fresh-boundary");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-stable", 5, 0, 50_000),
                new HistoryInput("recipe-failure", 5, 5, 500),
                new HistoryInput("recipe-fresh", 5, 5, 1));

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(
                ["group-failure", "group-stable", "fresh-boundary"],
                result.ExecutionOrder);
            AssertEqual(1, plan.Groups.Count);
            AssertSetEqual(
                ["group-stable", "group-failure"],
                result.ExecutionOrder.Take(2));
            AssertEqual("fresh-boundary", result.ExecutionOrder[2]);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void HistoricalOrderingKeepsMultipleReuseGroupsContiguous()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("a-stable", "recipe-a-stable", "group-a"),
            ReusableTest("a-failure", "recipe-a-failure", "group-a"),
            ReusableTest("b-stable", "recipe-b-stable", "group-b"),
            ReusableTest("b-failure", "recipe-b-failure", "group-b"));
        CatalogSuiteReusePlan plan = new(
            4,
            [
                new CatalogSuiteReuseGroup(
                    "group-a",
                    CatalogRecipeIsolationMode.PureRead,
                    ["a-stable", "a-failure"],
                    null,
                    "profile-a"),
                new CatalogSuiteReuseGroup(
                    "group-b",
                    CatalogRecipeIsolationMode.PureRead,
                    ["b-stable", "b-failure"],
                    null,
                    "profile-b")
            ])
        {
            ExecutionOrder = ["a-stable", "a-failure", "b-stable", "b-failure"]
        };
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-a-stable", 5, 0, 50_000),
                new HistoryInput("recipe-a-failure", 5, 5, 500),
                new HistoryInput("recipe-b-stable", 5, 0, 50_000),
                new HistoryInput("recipe-b-failure", 5, 5, 500));

            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(
                ["a-failure", "a-stable", "b-failure", "b-stable"],
                result.ExecutionOrder);
            AssertEqual(
                CountReusableGroupRuns(plan.ExecutionOrder, plan.Groups),
                CountReusableGroupRuns(result.ExecutionOrder, plan.Groups));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void IdenticalHistoryProducesIdenticalOrdering()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("alpha", "recipe-alpha"),
            ReusableTest("beta", "recipe-beta"),
            ReusableTest("gamma", "recipe-gamma"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "gamma", "alpha", "beta");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-alpha", 5, 1, 9_000, Retries: 1),
                new HistoryInput("recipe-beta", 5, 1, 9_000, Retries: 1),
                new HistoryInput("recipe-gamma", 5, 1, 9_000, Retries: 1));
            CatalogSuiteFailFastOrderingResult first =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);
            CatalogSuiteFailFastOrderingResult second =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            AssertSequence(first.ExecutionOrder, second.ExecutionOrder);
            AssertEqual(first.Summary.Reason, second.Summary.Reason);
            AssertEqual(first.HistoryContext, second.HistoryContext);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void SyntheticHistoryReducesExpectedFailureTimeWithoutNewTransitions()
    {
        CatalogDocument catalog = CreateCatalog(
            ReusableTest("expensive-stable", "recipe-expensive"),
            ReusableTest("cheap-failure", "recipe-cheap"));
        CatalogSuiteReusePlan plan = CreatePlan(catalog, "expensive-stable", "cheap-failure");
        string directory = CreateTempDirectory();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            WriteHistory(
                directory,
                catalog,
                plan,
                now,
                new HistoryInput("recipe-expensive", 6, 0, 60_000),
                new HistoryInput("recipe-cheap", 6, 6, 6_000));
            CatalogSuiteFailFastOrderingResult result =
                CatalogSuiteFailFastOrdering.Order(catalog, plan, directory, now);

            long before = 60_000 + 6_000;
            long after = 6_000;
            Assert(after < before, "Synthetic history should reduce expected first-failure time.");
            AssertEqual(plan.Groups.Count, 1);
            AssertSetEqual(
                plan.Groups[0].TestIds,
                result.ExecutionOrder.Take(plan.Groups[0].TestIds.Count));
            Assert(result.Summary.Used, "The synthetic history should be used.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void NonFailFastExecutionRemainsComplete()
    {
        CatalogDocument catalog = CreateCatalog(
            new CatalogTest { Id = "alpha", Recipe = "recipe-alpha" },
            new CatalogTest { Id = "beta", Recipe = "recipe-beta" },
            new CatalogTest { Id = "gamma", Recipe = "recipe-gamma" });
        var adapter = new CountingRecipeAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "complete",
                ["gamma", "alpha", "beta"],
                failFast: false)
            .GetAwaiter()
            .GetResult();

        AssertEqual(3, execution.Tests.Count);
        AssertEqual(0, execution.Skipped);
        Assert(execution.FailFast is null,
            "Non-fail-fast execution must not acquire fail-fast metadata.");
        AssertSequence(["recipe-alpha", "recipe-beta", "recipe-gamma"], adapter.RunCalls);
    }

    public static void ResultMetadataExplainsHistoricalOrderingBoundedly()
    {
        CatalogSuiteExecutionResult execution = new(
            "smoke",
            [],
            2,
            Cancelled: false,
            FailFast: new CatalogSuiteFailFastSummary(
                null,
                2,
                false,
                new CatalogSuiteFailFastOrderingSummary(
                    false,
                    "history-invalid-or-stale",
                    CatalogSuiteFailFastOrdering.PolicyVersion)));
        string json = CatalogJsonFacade.Serialize(
            RimTestSuiteResultFactory.FromExecution(execution, 1));

        Assert(
            json.Contains("historicalOrdering", StringComparison.Ordinal),
            "Result metadata must explain historical ordering.");
        Assert(
            !json.Contains("operationCounts", StringComparison.Ordinal),
            "Result metadata must not expose full history.");
        Assert(json.Length < 1024, "Ordering metadata must remain bounded.");
    }

    public static void HistoricalOrderingContextIsVersionedAndBounded()
    {
        string directory = CreateTempDirectory();
        string context = "h-" + new string('a', 64);
        try
        {
            using (EfficiencyProfiler profiler = EfficiencyProfiler.Start(directory))
            {
                profiler.SetOrderingContext(context);
                profiler.Complete(0);
                using JsonDocument profile = JsonDocument.Parse(profiler.BuildProfileJson());
                JsonElement identity = profile.RootElement.GetProperty("identity");
                AssertEqual(
                    CatalogSuiteFailFastOrdering.PolicyVersion,
                    identity.GetProperty("orderingSchema").GetString());
                AssertEqual(context, identity.GetProperty("orderingContext").GetString());
            }

            string[] persisted = Directory.GetFiles(directory, "rimliaison-*.json");
            AssertEqual(1, persisted.Length);
            Assert(
                new FileInfo(persisted[0]).Length <= EfficiencyProfiler.MaximumProfileBytes,
                "Persisted ordering context must remain within the existing profile bound.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static CatalogSuiteReusePlan CreatePlan(
        CatalogDocument catalog,
        params string[] executionOrder)
    {
        string[] reusable = executionOrder
            .Where(testId => CatalogRecipeIsolationPolicy.CanShareGeneration(
                CatalogRecipeIsolationPolicy.Resolve(
                    CatalogNavigator.FindTest(catalog, testId))))
            .ToArray();
        var groups = reusable.Length > 1
            ? new[]
            {
                new CatalogSuiteReuseGroup(
                    "shared",
                    CatalogRecipeIsolationMode.PureRead,
                    reusable,
                    null,
                    "profile-shared")
            }
            : Array.Empty<CatalogSuiteReuseGroup>();
        return new CatalogSuiteReusePlan(executionOrder.Length, groups)
        {
            ExecutionOrder = executionOrder
        };
    }

    private static CatalogDocument CreateCatalog(params CatalogTest[] tests) => new()
    {
        SchemaVersion = CatalogSchema.Current,
        Tests = tests.ToList(),
        Suites =
        [
            new CatalogSuite { Id = "suite", Tests = tests.Select(test => test.Id).ToList() }
        ]
    };

    private static CatalogTest ReusableTest(
        string id,
        string recipe,
        string reuseKey = "shared") =>
        new()
        {
            Id = id,
            Recipe = recipe,
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.PureRead,
                ReuseKey = reuseKey
            }
        };

    private static int CountReusableGroupRuns(
        IReadOnlyList<string> order,
        IReadOnlyList<CatalogSuiteReuseGroup> groups)
    {
        Dictionary<string, string> groupByTest = groups
            .Where(static group => group.TestIds.Count > 1)
            .SelectMany(group => group.TestIds.Select(testId =>
                (testId, anchor: group.TestIds[0])))
            .ToDictionary(value => value.testId, value => value.anchor, StringComparer.Ordinal);
        string? previous = null;
        int runs = 0;
        foreach (string testId in order)
        {
            if (!groupByTest.TryGetValue(testId, out string? anchor))
            {
                previous = null;
                continue;
            }

            if (!string.Equals(previous, anchor, StringComparison.Ordinal))
            {
                runs++;
                previous = anchor;
            }
        }

        return runs;
    }

    private static void WriteHistory(
        string directory,
        CatalogDocument catalog,
        CatalogSuiteReusePlan plan,
        DateTimeOffset started,
        params HistoryInput[] inputs)
    {
        WriteHistory(directory, catalog, plan, started, inputs, "rimliaison-history.json");
    }

    private static void WriteHistory(
        string directory,
        CatalogDocument catalog,
        CatalogSuiteReusePlan plan,
        DateTimeOffset started,
        IReadOnlyList<HistoryInput> inputs,
        string fileName)
    {
        var operations = inputs.Select(input => new
        {
            operation = "recipe.run",
            category = "testing",
            phase = "recipe",
            fingerprint = "synthetic",
            target = TargetHash(input.Recipe),
            runs = input.Runs,
            cumulativeMs = input.CumulativeMs,
            failures = input.Failures,
            cancelled = 0,
            retries = input.Retries,
            noOpRuns = input.NoOpRuns,
            generations = Enumerable.Range(1, Math.Max(0, input.GenerationCount))
                .Select(generation => new { generation, runs = input.Runs })
                .ToArray(),
            errorCodes = Array.Empty<object>()
        }).ToArray();
        var profile = new
        {
            schema = EfficiencyProfiler.SchemaVersion,
            identity = new
            {
                runId = "synthetic",
                command = "suite",
                startedUtc = started.ToString("O", CultureInfo.InvariantCulture),
                orderingSchema = CatalogSuiteFailFastOrdering.PolicyVersion,
                orderingContext = CatalogSuiteFailFastOrdering.BuildHistoryContext(catalog, plan)
            },
            outcome = new { status = "failure", exitCode = 1, wallTimeMs = 1 },
            operationCounts = operations
        };
        File.WriteAllText(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(profile));
    }

    private static string TargetHash(string recipe)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in recipe)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return "h-" + hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-fail-fast-ordering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(",", expected)}]; got [{string.Join(",", actual)}].");
        }
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        HashSet<string> expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        HashSet<string> actualSet = actual.ToHashSet(StringComparer.Ordinal);
        if (!expectedSet.SetEquals(actualSet))
        {
            throw new InvalidOperationException("Historical ordering changed selected membership.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }

    private sealed record HistoryInput(
        string Recipe,
        int Runs,
        int Failures,
        long CumulativeMs,
        int Retries = 0,
        int NoOpRuns = 0,
        int GenerationCount = 1);

    private sealed class CountingRecipeAdapter : IDevBridgeRecipeAdapter
    {
        internal List<string> RunCalls { get; } = [];

        public Task<DevBridgeRecipeShowResult> ShowAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(
                "{\"projects\":[\"fixture\"],\"inputs\":{\"recipe\":\"" +
                recipeId + "\"}}");
            JsonElement definition = document.RootElement.Clone();
            return Task.FromResult(new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                definition));
        }

        public Task<DevBridgeRecipePlanResult> PlanAsync(
            string recipeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevBridgeRecipePlanResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                new DevBridgeRecipePlan(recipeId, true, 0, [], null, [])));

        public Task<DevBridgeRecipeRunResult> RunAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            RunCalls.Add(recipeId);
            return Task.FromResult(new DevBridgeRecipeRunResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                true,
                "run-" + recipeId,
                1,
                null,
                null,
                null,
                null,
                null,
                false,
                0,
                [],
                null));
        }
    }
}
