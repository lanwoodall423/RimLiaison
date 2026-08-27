using System.Text.Json;
using RimLiaison;
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
            using var ui = new AgentObservabilityUi(reader, runId: "run-current");
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

    public static void UnscopedUiRetainsConcurrentRuns()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-live-rollover-");
        const string historicalRunId = "run-live-rollover-history";
        const string currentRunId = "run-live-rollover-current";
        try
        {
            using var store = new AgentObservabilityStore(directory);
            using (var historicalRun = new AgentObservabilityRun(
                       historicalRunId,
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
            AssertEqual(null, ui.ActiveRunId);

            Thread.Sleep(5);
            using var currentRun = new AgentObservabilityRun(
                currentRunId,
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

            AssertEqual(null, ui.ActiveRunId);
            AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
            AgentObservabilityUiNavigationItem[] agents = snapshot.Navigation.Items
                .Where(static item => item.Kind == "agent")
                .ToArray();
            AssertEqual(2, agents.Length);
            Assert(agents.Any(value => value.RunId == historicalRunId));
            Assert(agents.Any(value => value.RunId == currentRunId));
            Assert(snapshot.All!.Activity.Any(
                value => value.Event?.RunId == currentRunId &&
                    value.Event.AgentId == current.AgentId));
            Assert(snapshot.All.Activity.Any(
                value => value.Event?.RunId == historicalRunId));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void TemporaryWorktreesShareProvenRepositoryIdentity()
    {
        string main = CreateTemporaryDirectory("RimLiaison-");
        string tests = CreateTemporaryDirectory("RimLiaison-tests-3f2a");
        string unrelated = CreateTemporaryDirectory("OtherProject-");
        try
        {
            WriteGitOrigin(main, "git@github.com:example/RimLiaison.git");
            WriteGitOrigin(tests, "https://github.com/example/RimLiaison.git");
            WriteGitOrigin(unrelated, "git@github.com:other/OtherProject.git");

            ObservabilityProjectIdentity mainIdentity =
                ObservabilityProjectIdentityResolver.Resolve(main);
            ObservabilityProjectIdentity testIdentity =
                ObservabilityProjectIdentityResolver.Resolve(tests);
            ObservabilityProjectIdentity unrelatedIdentity =
                ObservabilityProjectIdentityResolver.Resolve(unrelated);
            AssertEqual(mainIdentity.ModId, testIdentity.ModId);
            Assert(!string.Equals(
                    mainIdentity.ModId,
                    unrelatedIdentity.ModId,
                    StringComparison.OrdinalIgnoreCase),
                "different Git repositories must remain separate");
            AssertEqual("git-remote", testIdentity.Source);
        }
        finally
        {
            DeleteTemporaryDirectory(main);
            DeleteTemporaryDirectory(tests);
            DeleteTemporaryDirectory(unrelated);
        }
    }

    public static void KnownTemporaryIdentityMigrationPreservesBoundaries()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-migration-");
        try
        {
            AgentSnapshot legacyAgent = new()
            {
                AgentId = "agent-polluted",
                RunId = "run-polluted",
                ModId = "[Tool] RimLiaison",
                ModName = "[Tool] RimLiaison",
                StartTime = 1
            };
            AgentEvent legacyEvent = new()
            {
                Id = "event-polluted",
                AgentId = legacyAgent.AgentId,
                RunId = legacyAgent.RunId,
                ModId = "RimLiaison-tests-3f2a",
                Type = AgentEventTypes.FileInspected,
                Summary = "Legacy activity.",
                Timestamp = 1,
                Sequence = 1,
                Stage = DevelopmentStage.Analysis
            };
            File.WriteAllText(
                Path.Combine(directory, "agents.jsonl"),
                JsonSerializer.Serialize(
                    new { kind = "agent", value = legacyAgent },
                    AgentObservabilityJson.Options) + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(directory, "events.jsonl"),
                JsonSerializer.Serialize(
                    new { kind = "event", value = legacyEvent },
                    AgentObservabilityJson.Options) + Environment.NewLine);

            string firstAgents;
            string firstEvents;
            using (var reader = new AgentObservabilityStore(directory))
            {
                AgentSnapshot migrated = reader.GetAgents().Single();
                AgentEvent migratedEvent = reader.GetEvents().Single();
                AssertEqual("RimLiaison", migrated.ModId);
                AssertEqual("tool:rimliaison", migrated.CanonicalEntityId);
                AssertEqual("tool:rimliaison", migratedEvent.CanonicalEntityId);
                firstAgents = File.ReadAllText(Path.Combine(directory, "agents.jsonl"));
                firstEvents = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
            }

            using (var reader = new AgentObservabilityStore(directory))
            {
                AssertEqual(firstAgents, File.ReadAllText(Path.Combine(directory, "agents.jsonl")));
                AssertEqual(firstEvents, File.ReadAllText(Path.Combine(directory, "events.jsonl")));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void QualificationRecordsPersistAsFixtureClassification()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-qualification-");
        try
        {
            using (var writer = new AgentObservabilityStore(directory))
            using (var run = new AgentObservabilityRun(
                       "qualification-persisted-run",
                       writer,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent(
                       "rimliaison.qualification.fixture",
                       "RimLiaison Qualification Fixture",
                       entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"),
                       workloadKind: "qualification",
                       qualificationProfile: "deterministic"))
            {
                agent.Start();
                agent.Complete();
            }

            string agents = File.ReadAllText(Path.Combine(directory, "agents.jsonl"));
            Assert(agents.Contains("\"entityType\":\"fixture\"", StringComparison.Ordinal));
            Assert(!agents.Contains("\"entityType\":\"tool\"", StringComparison.Ordinal));
            using var reader = new AgentObservabilityStore(directory);
            using var ui = new AgentObservabilityUi(reader);
            AssertEqual(0, ui.Snapshot.All!.Production.Count);
            AssertEqual(0, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void ConcurrentToolAliasesRemainOneCanonicalEntity()
    {
        using var store = new AgentObservabilityStore();
        Parallel.For(0, 16, index =>
        {
            store.RegisterAgent(new AgentSnapshot
            {
                AgentId = "concurrent-agent-" + index,
                RunId = "concurrent-run-" + index,
                SessionId = "concurrent-session-" + index,
                ModId = index % 2 == 0
                    ? "[Tool] RimLiaison"
                    : "RimLiaison-worktree-" + index,
                ModName = "RimLiaison",
                EntityType = ObservabilityEntityTypes.Tool
            });
        });

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(1, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        Assert(store.GetAgents().All(static agent =>
            agent.CanonicalEntityId == "tool:rimliaison"));
    }

    public static void CliFixtureStoreCannotWriteCanonicalRoot()
    {
        string canonical = CreateTemporaryDirectory("rimliaison-observability-canonical-");
        string? previous = Environment.GetEnvironmentVariable(
            AgentObservabilityStorage.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                canonical);
            using var store = new AgentObservabilityStore();
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "list",
                        "--catalog",
                        Path.Combine(
                            Directory.GetCurrentDirectory(),
                            "TestCatalog",
                            "rimtest.catalog.json")
                    ],
                    output,
                    error,
                    observabilityStore: store,
                    observabilityTelemetry: new NoopAgentObservabilityTelemetry())
                .GetAwaiter()
                .GetResult();
            AssertEqual(CliExitCodes.Success, exitCode);
            Assert(!Directory.EnumerateFileSystemEntries(canonical).Any(),
                "in-memory CLI fixtures must not create canonical store records");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                previous);
            DeleteTemporaryDirectory(canonical);
        }
    }

    public static void IntegrityValidatorAcceptsCoherentStore()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "integrity-valid-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "integrity.alias",
            "Integrity",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.integrity",
                "Integrity"));
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Integrity activity.");
        agent.Complete();

        AgentObservabilityIntegrityReport report =
            AgentObservabilityIntegrityValidator.Validate(store);
        Assert(report.IsValid, string.Join(
            "; ",
            report.Findings.Select(static finding => finding.Code)));
    }

    public static void IntegrityValidatorReportsUnresolvedActivity()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-integrity-");
        try
        {
            AgentEvent orphan = new()
            {
                Id = "orphan-event",
                RunId = "orphan-run",
                AgentId = "orphan-agent",
                ModId = "orphan-mod",
                EntityType = ObservabilityEntityTypes.Mod,
                CanonicalEntityId = "mod:orphan",
                DisplayName = "Orphan",
                Timestamp = 1,
                Sequence = 1,
                Stage = DevelopmentStage.Analysis,
                Type = AgentEventTypes.FileInspected,
                Summary = "Orphan activity."
            };
            File.WriteAllText(
                Path.Combine(directory, "events.jsonl"),
                JsonSerializer.Serialize(
                    new { kind = "event", value = orphan },
                    AgentObservabilityJson.Options));

            using var store = new AgentObservabilityStore(directory);
            AgentObservabilityIntegrityReport report =
                AgentObservabilityIntegrityValidator.Validate(store);
            Assert(report.Findings.Any(finding =>
                finding.Code == "event.owner.unresolved"),
                "unresolved activity must become an integrity diagnostic");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    public static void MultiModLogicalAgentRetainsAttribution()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "multi-mod-integrity",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent(
            "alpha-alias",
            "Alpha",
            logicalAgentId: "worker-multi-mod",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.alpha",
                "Alpha"));
        using AgentObservabilitySession beta = run.CreateAgent(
            "beta-alias",
            "Beta",
            logicalAgentId: "worker-multi-mod",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.beta",
                "Beta"));
        alpha.Start();
        beta.Start();
        alpha.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Alpha-only activity.");
        beta.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Beta-only activity.");

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            2,
            ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AgentObservabilityAgentView alphaView =
            ui.ShowAgent("mod:com.example.alpha", run.RunId).Agent!;
        AgentObservabilityAgentView betaView =
            ui.ShowAgent("mod:com.example.beta", run.RunId).Agent!;
        Assert(alphaView.RecentActivity.Any(row => row.Activity == "Alpha-only activity."));
        Assert(!alphaView.RecentActivity.Any(row => row.Activity == "Beta-only activity."));
        Assert(betaView.RecentActivity.Any(row => row.Activity == "Beta-only activity."));
        Assert(!betaView.RecentActivity.Any(row => row.Activity == "Alpha-only activity."));

        alpha.Complete();
        beta.Complete();
        AssertEqual(
            0,
            ui.Snapshot.Navigation.Items.Count(item =>
                item.NavigationStatus == AgentObservabilityAgentNavigationStatus.Working));
    }

    public static void ConcurrentProjectSubjectsRetainToolOwnership()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "concurrent-project-subjects",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession frontier = run.CreateAgent(
            "com.frontier",
            "Frontier",
            entityIdentity: ObservabilityEntityIdentity.ForMod("com.frontier", "Frontier"));
        using AgentObservabilitySession wildlife = run.CreateAgent(
            "com.wildlife",
            "Wildlife",
            entityIdentity: ObservabilityEntityIdentity.ForMod("com.wildlife", "Wildlife"));
        frontier.Start();
        wildlife.Start();

        Task.WhenAll(
            Task.Run(() => RecordToolFailure(frontier, "RimTest")),
            Task.Run(() => RecordToolFailure(wildlife, "DevBridge2")))
            .GetAwaiter()
            .GetResult();

        AgentEvent[] events = store.GetEvents(runId: run.RunId).ToArray();
        Assert(events.Where(value => value.AgentId == frontier.AgentId).All(
                value => value.CanonicalEntityId == "mod:com.frontier"),
            "Frontier tool events must retain Frontier");
        Assert(events.Where(value => value.AgentId == wildlife.AgentId).All(
                value => value.CanonicalEntityId == "mod:com.wildlife"),
            "Wildlife tool events must retain Wildlife");
        Assert(store.GetIssues(runId: run.RunId).All(issue =>
                issue.CanonicalEntityId is "mod:com.frontier" or "mod:com.wildlife"),
            "shared tooling issues must remain subject-scoped");
        Assert(store.GetIssues(agentId: frontier.AgentId).All(
                issue => issue.ComponentOwner == "RimTest"));
        Assert(store.GetIssues(agentId: wildlife.AgentId).All(
                issue => issue.ComponentOwner == "DevBridge2"));
        Assert(!events.Any(value => value.CanonicalEntityId == "tool:rimliaison"),
            "shared RimLiaison orchestration must not become the active subject");

        frontier.Complete();
        wildlife.Complete();
    }

    public static void ConcurrentCanonicalRegistrationDoesNotDuplicate()
    {
        using var store = new AgentObservabilityStore();
        AgentSnapshot snapshot = new()
        {
            RunId = "concurrent-canonical",
            AgentId = "concurrent-agent",
            SessionId = "concurrent-session",
            ModId = "alias",
            ModName = "Concurrent",
            EntityType = ObservabilityEntityTypes.Mod,
            CanonicalEntityId = "mod:com.example.concurrent",
            DisplayName = "Concurrent",
            Status = AgentStatus.Running,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        Parallel.For(0, 32, _ => store.RegisterAgent(snapshot));
        Parallel.For(0, 32, index => store.AppendEvent(new AgentEventRequest(
            snapshot.RunId,
            snapshot.AgentId,
            snapshot.ModId,
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Concurrent event " + index,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId: snapshot.SessionId)));

        AssertEqual(1, store.GetAgents().Count);
        AssertEqual(32, store.GetEvents(limit: 100).Count);
        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AssertEqual(32, ui.Snapshot.All!.Activity.Count);
    }


    public static void LifecycleReconnectKeepsOneCanonicalAgent()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "lifecycle-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "lifecycle-alias",
            "Lifecycle",
            logicalAgentId: "lifecycle-worker",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.lifecycle",
                "Lifecycle"));
        first.Start();
        using (AgentOperationScope operation = first.BeginOperation(
                   "implementation",
                   "implementation",
                   DevelopmentStage.Implementation)!)
        {
            first.Record(
                DevelopmentStage.Implementation,
                AgentEventTypes.FileModified,
                "First operation activity.");
            operation.Complete("First operation complete.");
        }
        using (AgentOperationScope operation = first.BeginOperation(
                   "testing",
                   "testing",
                   DevelopmentStage.Testing)!)
        {
            operation.Complete("Second operation complete.");
        }
        AssertEqual(AgentStatus.Running, first.Snapshot.Status);
        first.Complete();
        AssertEqual(AgentStatus.Completed, first.Snapshot.Status);

        using var secondRun = new AgentObservabilityRun(
            "lifecycle-reconnect",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession reconnected = secondRun.CreateAgent(
            "lifecycle-alias",
            "Lifecycle",
            logicalAgentId: "lifecycle-worker",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.lifecycle",
                "Lifecycle"));
        reconnected.Start();
        reconnected.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Reconnected activity.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.Kind == "agent");
        AssertEqual(secondRun.RunId, tab.RunId);
        AssertEqual(AgentStatus.Running, tab.Status);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(item =>
                item.NavigationStatus == AgentObservabilityAgentNavigationStatus.Working));
        AgentObservabilityAgentView detail =
            ui.ShowAgent("mod:com.example.lifecycle", secondRun.RunId).Agent!;
        Assert(detail.RecentActivity.Any(row => row.Activity == "Reconnected activity."));
        Assert(detail.RecentActivity.Any(row => row.Activity == "First operation activity."));

        reconnected.Complete();
        AssertEqual(
            0,
            ui.Snapshot.Navigation.Items.Count(item =>
                item.NavigationStatus == AgentObservabilityAgentNavigationStatus.Working));
    }

    public static void IntegrityValidatorDetectsToolSubjectInversion()
    {
        string directory = CreateTemporaryDirectory("rimliaison-observability-integrity-inversion-");
        try
        {
            AgentSnapshot toolAgent = new()
            {
                AgentId = "inversion-agent",
                RunId = "inversion-run",
                SessionId = "inversion-session",
                ModId = "RimLiaison",
                ModName = "RimLiaison",
                EntityType = ObservabilityEntityTypes.Tool,
                CanonicalEntityId = "tool:rimliaison",
                DisplayName = "RimLiaison",
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            AgentEvent inversion = new()
            {
                Id = "inversion-event",
                AgentId = toolAgent.AgentId,
                RunId = toolAgent.RunId,
                SessionId = toolAgent.SessionId,
                ModId = toolAgent.ModId,
                EntityType = toolAgent.EntityType,
                CanonicalEntityId = toolAgent.CanonicalEntityId,
                DisplayName = toolAgent.DisplayName,
                Type = AgentEventTypes.FileInspected,
                Summary = "Tool activity with a project target.",
                Timestamp = toolAgent.StartTime,
                Sequence = 1,
                Stage = DevelopmentStage.Analysis,
                Data = JsonSerializer.SerializeToElement(
                    new { project = "Frontier", toolName = "RimLiaison" })
            };
            File.WriteAllText(
                Path.Combine(directory, "agents.jsonl"),
                JsonSerializer.Serialize(
                    new { kind = "agent", value = toolAgent },
                    AgentObservabilityJson.Options) + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(directory, "events.jsonl"),
                JsonSerializer.Serialize(
                    new { kind = "event", value = inversion },
                    AgentObservabilityJson.Options) + Environment.NewLine);

            using var store = new AgentObservabilityStore(directory);
            AgentObservabilityIntegrityReport report =
                AgentObservabilityIntegrityValidator.Validate(store);
            Assert(report.Findings.Any(finding =>
                    finding.Code == "subject.tool-inversion.suspected" &&
                    finding.EventId == inversion.Id),
                "tool subjects with project targets must be diagnosable");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static void WriteGitOrigin(string root, string remote)
    {
        string git = Path.Combine(root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(
            Path.Combine(git, "config"),
            "[remote \"origin\"]\n\turl = " + remote + "\n");
    }

    private static void RecordToolFailure(
        AgentObservabilitySession agent,
        string componentOwner)
    {
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ToolFailed,
            componentOwner + " failed.",
            new
            {
                operationKey = "tool:" + componentOwner,
                toolName = componentOwner,
                componentOwner,
                errorCode = "TOOL_FAILURE"
            });
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
