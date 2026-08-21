using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ObservabilityIsolationTests
{
    public static void CanonicalRootIsIndependentOfWorktree()
    {
        string? previousDirectory = Environment.GetEnvironmentVariable(
            AgentObservabilityStorage.DirectoryEnvironmentVariable);
        string originalCurrentDirectory = Environment.CurrentDirectory;
        string modA = CreateTemporaryDirectory("rimliaison-mod-a-");
        string modB = CreateTemporaryDirectory("rimliaison-mod-b-");

        try
        {
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                null);
            Directory.SetCurrentDirectory(modA);
            string rootA = AgentObservabilityStorage.ResolveCanonicalRoot();
            Directory.SetCurrentDirectory(modB);
            string rootB = AgentObservabilityStorage.ResolveCanonicalRoot();

            AssertEqual(rootA, rootB);
            Assert(!IsWithin(rootA, modA), "canonical root must be outside mod A");
            Assert(!IsWithin(rootA, modB), "canonical root must be outside mod B");
            Assert(!rootA.Contains(
                    Path.Combine(".rimdev", "observability"),
                    StringComparison.OrdinalIgnoreCase),
                "canonical root must not be repository-relative observability state");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                previousDirectory);
            DeleteTemporaryDirectory(modA);
            DeleteTemporaryDirectory(modB);
        }
    }

    public static void RepresentativeWorkflowLeavesWorktreeClean()
    {
        string? previousDirectory = Environment.GetEnvironmentVariable(
            AgentObservabilityStorage.DirectoryEnvironmentVariable);
        string originalCurrentDirectory = Environment.CurrentDirectory;
        string externalRoot = CreateTemporaryDirectory("rimliaison-observability-external-");
        string worktree = CreateTemporaryDirectory("rimliaison-observability-worktree-");

        try
        {
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                externalRoot);
            Directory.SetCurrentDirectory(worktree);
            string[] before = Directory.EnumerateFileSystemEntries(
                    worktree,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            using (AgentObservabilityStore store = AgentObservabilityStore.CreateDefault())
            using (var run = new AgentObservabilityRun(
                       "run-worktree-regression",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent(
                       "mod.worktree-regression",
                       "Worktree Regression"))
            {
                agent.Start();
                agent.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestFailed,
                    "The simulated transaction observed a test failure.",
                    new { operationKey = "test:worktree-regression", exitCode = 1 });
                AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
                store.CreateDiagnosticBundle([issue.Id]);
                agent.Complete();

                AssertEqual(
                    Path.GetFullPath(externalRoot),
                    store.StorageDirectory);
            }

            string[] after = Directory.EnumerateFileSystemEntries(
                    worktree,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            Assert(before.SequenceEqual(after),
                "observability-only transaction must not mutate the mod worktree");
            Assert(!Directory.Exists(Path.Combine(worktree, ".rimdev", "observability")),
                "observability must not create a repository-local directory");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                previousDirectory);
            DeleteTemporaryDirectory(externalRoot);
            DeleteTemporaryDirectory(worktree);
        }
    }

    public static void SharedStoreHydratesAndPublishesAcrossProcesses()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-shared-");
        try
        {
            using var writer = new AgentObservabilityStore(directory);
            using var reader = new AgentObservabilityStore(directory);
            using var ui = new AgentObservabilityUi(reader);
            var updates = new List<AgentObservabilityUiUpdate>();
            using IDisposable subscription = ui.Subscribe(updates.Add);

            using var run = new AgentObservabilityRun(
                "run-cross-process",
                writer,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.cross-process",
                "Cross Process",
                "agent-cross-process");
            agent.Start();
            AgentEvent failure = agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.TestFailed,
                "Cross-process failure.",
                new { operationKey = "test:cross-process", exitCode = 1 })!;

            reader.Refresh();

            Assert(reader.GetAgents(runId: run.RunId).Any(
                value => value.AgentId == agent.AgentId),
                "reader must hydrate the runtime agent");
            Assert(reader.GetEvents(runId: run.RunId).Any(
                value => value.Id == failure.Id),
                "reader must hydrate the runtime event");
            Assert(reader.GetIssues(runId: run.RunId).Any(
                value => value.EventIds.Contains(failure.Id)),
                "reader must hydrate the runtime issue");
            Assert(updates.Any(update =>
                update.Kind == AgentObservabilityUiUpdateKind.AgentChanged),
                "desktop must receive an external agent update");
            Assert(updates.Any(update =>
                update.Kind == AgentObservabilityUiUpdateKind.EventAppended),
                "desktop must receive an external event update");
            Assert(updates.Any(update =>
                update.Kind == AgentObservabilityUiUpdateKind.IssueChanged),
                "desktop must receive an external issue update (received: " +
                string.Join(",", updates.Select(static update => update.Kind)) + ")");
            Assert(ui.Snapshot.All!.Agents.Single().AgentId == agent.AgentId,
                "desktop hydration must use the runtime process's authoritative records");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void ConcurrentStoresShareSequencesAndAgentIdentityBoundaries()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-concurrent-");
        try
        {
            using (var firstStore = new AgentObservabilityStore(directory))
            using (var secondStore = new AgentObservabilityStore(directory))
            using (var firstRun = new AgentObservabilityRun(
                       "run-concurrent-a",
                       firstStore,
                       new NoopAgentObservabilityTelemetry()))
            using (var secondRun = new AgentObservabilityRun(
                       "run-concurrent-b",
                       secondStore,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession first = firstRun.CreateAgent(
                       "mod.concurrent-a",
                       "Concurrent A",
                       "agent-concurrent-a"))
            using (AgentObservabilitySession second = secondRun.CreateAgent(
                       "mod.concurrent-b",
                       "Concurrent B",
                       "agent-concurrent-b"))
            {
                first.Start();
                second.Start();
                Task.WhenAll(
                    Task.Run(() => EmitEvents(first, "a")),
                    Task.Run(() => EmitEvents(second, "b")))
                    .GetAwaiter()
                    .GetResult();
                first.Complete();
                second.Complete();
            }

            using var reader = new AgentObservabilityStore(directory);
            AgentEvent[] events = reader.GetEvents(limit: 10_000).ToArray();
            Assert(events.Length >= 42, "both concurrent agents must remain durable");
            AssertEqual(
                events.Length,
                events.Select(static value => value.Sequence).Distinct().Count());
            Assert(events.SequenceEqual(events.OrderBy(static value => value.Sequence)),
                "shared event sequencing must remain deterministic");
            Assert(events.Where(value => value.RunId == "run-concurrent-a")
                .All(value => value.AgentId == "agent-concurrent-a"));
            Assert(events.Where(value => value.RunId == "run-concurrent-b")
                .All(value => value.AgentId == "agent-concurrent-b"));
            AssertEqual(2, reader.GetAgents().Count);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void HistoricalRunsDoNotCreateDuplicateLiveAgents()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-history-");
        try
        {
            using (var store = new AgentObservabilityStore(directory))
            using (var historicalRun = new AgentObservabilityRun(
                       "run-history",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession historical = historicalRun.CreateAgent(
                       "mod.history",
                       "Historical",
                       "shared-agent"))
            {
                historical.Start();
                historical.Complete();
            }

            using (var store = new AgentObservabilityStore(directory))
            using (var currentRun = new AgentObservabilityRun(
                       "run-current",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession current = currentRun.CreateAgent(
                       "mod.current",
                       "Current",
                       "shared-agent"))
            {
                current.Start();
                current.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.FileModified,
                    "Current activity.");
                current.Complete();
            }

            using var reader = new AgentObservabilityStore(directory);
            using var ui = new AgentObservabilityUi(reader);
            AgentObservabilityUiNavigationItem[] agents = ui.Snapshot.Navigation.Items
                .Where(static item => item.Kind == "agent")
                .ToArray();
            AssertEqual(1, agents.Length);
            AssertEqual("run-current", agents[0].RunId);
            AssertEqual("Current", agents[0].FullLabel);
            Assert(ui.Snapshot.All!.Agents.All(
                value => value.RunId == "run-current"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void UnscopedUiFollowsNewRunAfterHistoricalStartup()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-live-rollover-");
        try
        {
            using var store = new AgentObservabilityStore(directory);
            using (var historicalRun = new AgentObservabilityRun(
                       "run-live-rollover-history",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession historical = historicalRun.CreateAgent(
                       "mod.history",
                       "Historical"))
            {
                historical.Start();
                historical.Complete();
            }

            using var ui = new AgentObservabilityUi(store);
            AssertEqual("run-live-rollover-history", ui.ActiveRunId);

            Thread.Sleep(5);
            using var currentRun = new AgentObservabilityRun(
                "run-live-rollover-current",
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession current = currentRun.CreateAgent(
                "mod.current",
                "Current",
                "agent-live-rollover");
            current.Start();
            current.Record(
                DevelopmentStage.Implementation,
                AgentEventTypes.FileModified,
                "Current live activity.",
                new { filePath = "Source/Current.cs" });

            AssertEqual(currentRun.RunId, ui.ActiveRunId);
            AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
            AgentObservabilityUiNavigationItem[] agents = snapshot.Navigation.Items
                .Where(static item => item.Kind == "agent")
                .ToArray();
            AssertEqual(1, agents.Length);
            AssertEqual(current.AgentId, agents[0].AgentId);
            AssertEqual(currentRun.RunId, agents[0].RunId);
            Assert(snapshot.All!.Activity.Any(
                value => value.Event?.RunId == currentRun.RunId &&
                    value.Event.AgentId == current.AgentId));
            Assert(snapshot.All.Activity.All(
                value => value.Event is null || value.Event.RunId == currentRun.RunId));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static void EmitEvents(AgentObservabilitySession agent, string suffix)
    {
        for (int index = 0; index < 20; index++)
        {
            agent.Record(
                DevelopmentStage.Implementation,
                AgentEventTypes.FileModified,
                "Concurrent change " + suffix + index,
                new
                {
                    operationKey = "file:" + suffix + index,
                    filePath = "Source/" + suffix + index + ".cs"
                });
        }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static bool IsWithin(string candidate, string parent)
    {
        string candidatePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        string parentPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidatePath.StartsWith(parentPath, comparison);
    }

    private static void Assert(bool condition, string message = "assertion failed")
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
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
