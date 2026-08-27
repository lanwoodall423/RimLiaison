using System.Text.Json;
using RimLiaison.Observability;
using System.Text.Json.Nodes;

namespace RimLiaison.Tests;

internal static class ObservabilityHydrationTests
{
    public static void RecentHydrationIsBoundedAndDeferred()
    {
        string directory = CreateDirectory();
        try
        {
            AgentSnapshot agent = Agent("run-large", "agent-large");
            WriteRecords(directory, "agents", "agent", [agent]);
            WriteRecords(
                directory,
                "events",
                "event",
                Enumerable.Range(1, 10_000)
                    .Select(index => Event(agent, index))
                    .ToArray());

            using var store = new AgentObservabilityStore(
                directory,
                loadPersistedRecords: false);
            Assert(store.InitialHydrationPending, "deferred stores must advertise pending hydration");
            AssertEqual(0, store.GetEvents(limit: 10).Count);

            AgentObservabilityHydrationResult result = store.HydrateRecentAsync(
                    maximumEvents: 25,
                    maximumIssues: 10,
                    maximumAgents: 5)
                .GetAwaiter()
                .GetResult();

            Assert(result.Completed);
            AssertEqual(25, store.GetEvents(limit: 100).Count);
            AssertEqual("evt-10000", store.GetEvents(limit: 1).Single().Id);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void MalformedAndTruncatedRecordsAreDegradedButUsable()
    {
        string directory = CreateDirectory();
        try
        {
            AgentSnapshot agent = Agent("run-corrupt", "agent-corrupt");
            WriteRecords(directory, "agents", "agent", [agent]);
            string valid = Persisted("event", Event(agent, 1));
            File.WriteAllText(
                Path.Combine(directory, "events.jsonl"),
                valid + Environment.NewLine +
                "{ this is not json" + Environment.NewLine +
                new string('x', 2_000) + Environment.NewLine +
                "{\"kind\":\"event\",\"value\":{\"id\":");

            using var store = new AgentObservabilityStore(
                directory,
                new AgentObservabilityOptions { MaximumPersistedBytes = 1_024 },
                loadPersistedRecords: false);
            AgentObservabilityHydrationResult result = store.HydrateRecentAsync()
                .GetAwaiter()
                .GetResult();

            Assert(result.Completed);
            Assert(result.Degraded, "bad persisted lines must be visible as degraded state");
            AssertEqual("evt-1", store.GetEvents(limit: 10).Single().Id);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void LegacyIdentityMigrationIsDurableAndIdempotent()
    {
        string directory = CreateDirectory();
        try
        {
            AgentSnapshot initial = Agent("legacy-tool-run", "legacy-tool-agent") with
            {
                ModId = "RimLiaison",
                ModName = "RimLiaison"
            };
            AgentSnapshot update = initial with
            {
                Status = AgentStatus.Completed,
                CompletionState = AgentCompletionState.Succeeded
            };
            AgentEvent eventRecord = Event(initial, 1) with
            {
                ModId = "RimLiaison"
            };
            File.WriteAllLines(
                Path.Combine(directory, "agents.jsonl"),
                [
                    LegacyPersisted("agent", initial),
                    LegacyPersisted("agent", update)
                ]);
            File.WriteAllLines(
                Path.Combine(directory, "events.jsonl"),
                [LegacyPersisted("event", eventRecord)]);
            File.AppendAllText(
                Path.Combine(directory, "events.jsonl"),
                "{ malformed legacy event" + Environment.NewLine);

            using (var store = new AgentObservabilityStore(directory))
            {
                AgentSnapshot migratedAgent = store.GetAgents().Single();
                AgentEvent migratedEvent = store.GetEvents(limit: 10).Single();
                AssertEqual(ObservabilityEntityTypes.Tool, migratedAgent.EntityType);
                AssertEqual("tool:rimliaison", migratedAgent.CanonicalEntityId);
                AssertEqual(ObservabilityEntityTypes.Tool, migratedEvent.EntityType);
                AssertEqual("tool:rimliaison", migratedEvent.CanonicalEntityId);
                AssertEqual(1, File.ReadAllLines(Path.Combine(directory, "agents.jsonl")).Length);
                AssertEqual(1, File.ReadAllLines(Path.Combine(directory, "events.jsonl")).Length);
            }

            string migratedAgents = File.ReadAllText(Path.Combine(directory, "agents.jsonl"));
            string migratedEvents = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
            using (var reopened = new AgentObservabilityStore(directory))
            {
                AssertEqual(1, reopened.GetAgents().Count);
                AssertEqual(1, reopened.GetEvents(limit: 10).Count);
            }

            AssertEqual(
                migratedAgents,
                File.ReadAllText(Path.Combine(directory, "agents.jsonl")));
            AssertEqual(
                migratedEvents,
                File.ReadAllText(Path.Combine(directory, "events.jsonl")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }
    public static void LegacyToolTargetWithStructuredProjectEvidenceIsReassociated()
    {
        string directory = CreateDirectory();
        try
        {
            AgentSnapshot knownMod = Agent("known-frontier-run", "known-frontier-agent") with
            {
                ModId = "Frontier",
                ModName = "Frontier",
                EntityType = ObservabilityEntityTypes.Mod,
                CanonicalEntityId = "mod:frontier",
                DisplayName = "Frontier"
            };
            AgentSnapshot legacyTool = Agent("legacy-frontier-run", "legacy-frontier-agent") with
            {
                ModId = "RimLiaison",
                ModName = "RimLiaison",
                EntityType = ObservabilityEntityTypes.Tool,
                CanonicalEntityId = "tool:rimliaison",
                DisplayName = "RimLiaison"
            };
            AgentEvent legacyEvent = Event(legacyTool, 1) with
            {
                ModId = "RimLiaison",
                EntityType = ObservabilityEntityTypes.Tool,
                CanonicalEntityId = "tool:rimliaison",
                DisplayName = "RimLiaison",
                Data = JsonSerializer.SerializeToElement(
                    new { project = "Frontier", repository = "Frontier" })
            };
            WriteRecords(directory, "agents", "agent", [knownMod, legacyTool]);
            WriteRecords(directory, "events", "event", [legacyEvent]);

            string firstAgents;
            string firstEvents;
            using (var store = new AgentObservabilityStore(directory))
            {
                AgentSnapshot migratedAgent = store.GetAgents()
                    .Single(value => value.RunId == legacyTool.RunId);
                AgentEvent migratedEvent = store.GetEvents().Single();
                AssertEqual("mod:frontier", migratedAgent.CanonicalEntityId);
                AssertEqual("mod:frontier", migratedEvent.CanonicalEntityId);
                firstAgents = File.ReadAllText(Path.Combine(directory, "agents.jsonl"));
                firstEvents = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
            }

            using (var reopened = new AgentObservabilityStore(directory))
            {
                AssertEqual("mod:frontier", reopened.GetAgents()
                    .Single(value => value.RunId == legacyTool.RunId)
                    .CanonicalEntityId);
                AssertEqual("mod:frontier", reopened.GetEvents().Single().CanonicalEntityId);
            }

            AssertEqual(
                firstAgents,
                File.ReadAllText(Path.Combine(directory, "agents.jsonl")));
            AssertEqual(
                firstEvents,
                File.ReadAllText(Path.Combine(directory, "events.jsonl")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }


    public static void TemporaryPersistenceContentionIsBounded()
    {
        string directory = CreateDirectory();
        try
        {
            string eventsPath = Path.Combine(directory, "events.jsonl");
            File.WriteAllText(eventsPath, string.Empty);
            string lockPath = eventsPath + ".lock";
            using var heldLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var store = new AgentObservabilityStore(
                directory,
                loadPersistedRecords: false);

            Task<AgentObservabilityHydrationResult> hydration = store.HydrateRecentAsync();
            AgentObservabilityHydrationResult result = hydration
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();

            Assert(result.Completed);
            Assert(result.Degraded);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void HistoricalHydrationRemainsAvailableOnDemand()
    {
        string directory = CreateDirectory();
        try
        {
            AgentSnapshot agent = Agent("run-history", "agent-history");
            WriteRecords(directory, "agents", "agent", [agent]);
            WriteRecords(
                directory,
                "events",
                "event",
                Enumerable.Range(1, 100)
                    .Select(index => Event(agent, index))
                    .ToArray());

            using var store = new AgentObservabilityStore(
                directory,
                loadPersistedRecords: false);
            store.HydrateRecentAsync(maximumEvents: 5, maximumIssues: 5, maximumAgents: 5)
                .GetAwaiter()
                .GetResult();
            AssertEqual(5, store.GetEvents(limit: 100).Count);

            AgentObservabilityHydrationResult result = store.HydrateHistoryAsync()
                .GetAwaiter()
                .GetResult();
            Assert(result.Completed);
            AssertEqual(100, store.GetEvents(limit: 200).Count);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void RecordsArrivingAfterHydrationBecomeVisible()
    {
        string directory = CreateDirectory();
        try
        {
            using var reader = new AgentObservabilityStore(
                directory,
                loadPersistedRecords: false);
            reader.HydrateRecentAsync()
                .GetAwaiter()
                .GetResult();

            using var writer = new AgentObservabilityStore(directory);
            AgentSnapshot agent = Agent("run-live", "agent-live");
            writer.RegisterAgent(agent);
            writer.AppendEvent(new AgentEventRequest(
                agent.RunId,
                agent.AgentId,
                agent.ModId,
                DevelopmentStage.Testing,
                "live.event",
                "Arrived after hydration."));

            reader.Refresh();
            AssertEqual(1, reader.GetEvents(limit: 10).Count);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void HydrationCancellationIsSafe()
    {
        string directory = CreateDirectory();
        try
        {
            using var store = new AgentObservabilityStore(
                directory,
                loadPersistedRecords: false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            AssertThrows<OperationCanceledException>(() => store.HydrateRecentAsync(
                    cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void CompactionRetainsRecentRecordsWithinThreshold()
    {
        string directory = CreateDirectory();
        try
        {
            var options = new AgentObservabilityOptions
            {
                MaximumPersistedBytes = 4 * 1024,
                MaximumEventDataBytes = 256,
                MaximumEvents = 100,
                MaximumIssues = 100,
                MaximumAgents = 10
            };
            using (var store = new AgentObservabilityStore(directory, options))
            using (var run = new AgentObservabilityRun(
                       "run-compaction",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent("mod.compaction", "Compaction"))
            {
                agent.Start();
                for (int index = 0; index < 100; index++)
                {
                    agent.Record(
                        DevelopmentStage.Implementation,
                        AgentEventTypes.FileModified,
                        "Modified " + index,
                        new
                        {
                            filePath = "Source/File" + index + ".cs",
                            operationKey = "file:" + index
                        });
                }

                agent.Complete();
            }

            Assert(
                new FileInfo(Path.Combine(directory, "events.jsonl")).Length <=
                    options.MaximumPersistedBytes,
                "compacted events must remain within the configured byte threshold");
            Assert(
                !Directory.EnumerateFiles(directory, "*.tmp-*", SearchOption.AllDirectories).Any(),
                "atomic compaction must clean temporary files");

            using var reader = new AgentObservabilityStore(directory, options);
            Assert(reader.GetEvents(limit: 1).Count == 1);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void UnresolvedIssueEvidenceSurvivesEvidenceRetention()
    {
        string directory = CreateDirectory();
        try
        {
            var options = new AgentObservabilityOptions
            {
                MaximumEvidenceEntries = 1,
                MaximumPersistedBytes = 4 * 1024
            };
            AgentDiagnosticEvidenceReference keep;
            AgentDiagnosticEvidenceReference drop;
            using (var store = new AgentObservabilityStore(directory, options))
            using (var run = new AgentObservabilityRun(
                       "run-evidence",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent("mod.evidence", "Evidence"))
            {
                agent.Start();
                keep = store.PersistEvidence("diagnostic", "keep", false)!;
                agent.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.CommandFailed,
                    "Command failed.",
                    new
                    {
                        operationKey = "command:evidence",
                        command = "dotnet test",
                        exitCode = 1,
                        errorCode = "TEST_FAILED",
                        evidenceReference = keep.Id
                    });
                Assert(store.GetIssues().Any(issue =>
                    !issue.Recovered &&
                    issue.EvidenceReference == keep.Id));
                drop = store.PersistEvidence("diagnostic", "drop", false)!;
                agent.Complete();
            }

            string evidenceDirectory = Path.Combine(directory, "evidence");
            Assert(
                File.Exists(Path.Combine(evidenceDirectory, keep.Id + ".json")),
                "protected evidence was deleted");
            Assert(
                !File.Exists(Path.Combine(evidenceDirectory, drop.Id + ".json")),
                "unprotected evidence was retained");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void StartupDiagnosticIsBoundedAndExternal()
    {
        Exception exception = new InvalidOperationException(new string('x', 100_000));
        string path = AgentObservabilityStorage.WriteStartupDiagnostic(exception);
        try
        {
            Assert(path.StartsWith(
                AgentObservabilityStorage.ResolveDiagnosticRoot(),
                StringComparison.OrdinalIgnoreCase));
            Assert(new FileInfo(path).Length <= 12_000);
            Assert(File.ReadAllText(path).Contains(
                "RimLiaison Observability UI startup failure",
                StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static AgentSnapshot Agent(string runId, string agentId) => new()
    {
        RunId = runId,
        AgentId = agentId,
        ModId = "mod.hydration",
        ModName = "Hydration",
        StartTime = 1,
        SessionId = "session-" + runId
    };

    private static AgentEvent Event(AgentSnapshot agent, long sequence) => new()
    {
        Id = "evt-" + sequence,
        RunId = agent.RunId,
        AgentId = agent.AgentId,
        ModId = agent.ModId,
        Timestamp = sequence,
        Sequence = sequence,
        Stage = DevelopmentStage.Testing,
        Type = "test.event",
        Summary = "Historical event " + sequence
    };

    private static void WriteRecords<T>(
        string directory,
        string fileName,
        string kind,
        IEnumerable<T> values) =>
        File.WriteAllLines(
            Path.Combine(directory, fileName + ".jsonl"),
            values.Select(value => Persisted(kind, value)));

    private static string LegacyPersisted<T>(string kind, T value)
    {
        JsonObject root = JsonNode.Parse(Persisted(kind, value))!.AsObject();
        JsonObject persistedValue = root["value"]!.AsObject();
        persistedValue.Remove("entityType");
        persistedValue.Remove("canonicalEntityId");
        persistedValue.Remove("displayName");
        return root.ToJsonString(AgentObservabilityJson.Options);
    }

    private static string Persisted<T>(string kind, T value) =>
        JsonSerializer.Serialize(
            new { kind, value },
            AgentObservabilityJson.Options);

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-hydration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
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

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Expected " + typeof(TException).Name + ".");
    }
}
