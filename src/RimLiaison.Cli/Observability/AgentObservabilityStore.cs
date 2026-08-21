using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimLiaison.Observability;

public interface IAgentObservabilityStore
{
    AgentSnapshot RegisterAgent(AgentSnapshot snapshot);

    AgentSnapshot UpdateAgent(AgentSnapshot snapshot);

    AgentEvent AppendEvent(AgentEventRequest request);

    IReadOnlyList<AgentSnapshot> GetAgents(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        int limit = 200);

    IReadOnlyList<AgentEvent> GetEvents(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        int limit = 1000);

    IReadOnlyList<AgentIssue> GetIssues(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        bool includeRecovered = true,
        int limit = 500);

    AgentObservabilityView Query(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        bool issuesOnly = false,
        int limit = 500);

    AgentDiagnosticBundle CreateDiagnosticBundle(
        IEnumerable<string> issueIds);

    IDisposable Subscribe(Action<AgentObservabilityNotification> handler);
}

/// <summary>
/// Optional live-store capability used by the desktop consumer when the
/// authoritative store is shared across processes. In-memory test stores do
/// not need to implement this capability.
/// </summary>
public interface IAgentObservabilityLiveStore
{
    string? StorageDirectory { get; }

    void Refresh();
}

/// <summary>
/// The authoritative RimLiaison activity and issue store. With a storage
/// directory it uses bounded append-only JSONL records; without one it is a
/// deterministic in-memory store suitable for tests and embedded callers.
/// </summary>
public sealed class AgentObservabilityStore :
    IAgentObservabilityStore,
    IAgentObservabilityLiveStore,
    IDisposable
{
    private const string EventsFileName = "events.jsonl";
    private const string IssuesFileName = "issues.jsonl";
    private const string AgentsFileName = "agents.jsonl";
    private const string SequenceFileName = "metadata.sequence";
    private const string EventRecordKind = "event";
    private const string IssueRecordKind = "issue";
    private const string AgentRecordKind = "agent";

    private readonly object gate = new();
    private readonly object refreshGate = new();
    private readonly AgentObservabilityOptions options;
    private readonly string? storageDirectory;
    private readonly string? eventsPath;
    private readonly string? issuesPath;
    private readonly string? agentsPath;
    private readonly string? sequencePath;
    private readonly FileSystemWatcher? storageWatcher;
    private readonly List<AgentEvent> events = [];
    private readonly Dictionary<string, AgentIssue> issues = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents = [];
    private readonly List<Action<AgentObservabilityNotification>> subscribers = [];
    private readonly AgentIssueDetector issueDetector;
    private long nextSequence;
    private long lastTimestamp;
    private int refreshRequested;
    private int refreshQueued;
    private int disposed;

    public AgentObservabilityStore(
        string? storageDirectory = null,
        AgentObservabilityOptions? options = null)
    {
        this.options = options ?? new AgentObservabilityOptions();
        this.options.Validate();
        if (!string.IsNullOrWhiteSpace(storageDirectory))
        {
            this.storageDirectory = Path.GetFullPath(storageDirectory);
            Directory.CreateDirectory(this.storageDirectory);
            eventsPath = Path.Combine(this.storageDirectory, EventsFileName);
            issuesPath = Path.Combine(this.storageDirectory, IssuesFileName);
            agentsPath = Path.Combine(this.storageDirectory, AgentsFileName);
            sequencePath = Path.Combine(this.storageDirectory, SequenceFileName);
        }

        issueDetector = new AgentIssueDetector(this.options);
        LoadPersistedRecords();
        storageWatcher = CreateStorageWatcher();
    }

    public static AgentObservabilityStore CreateDefault(
        AgentObservabilityOptions? options = null)
    {
        string directory = AgentObservabilityStorage.ResolveCanonicalRoot();
        try
        {
            return new AgentObservabilityStore(directory, options);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // Observability must never prevent the owning command from running.
            return new AgentObservabilityStore(options: options);
        }
    }

    public string? StorageDirectory => storageDirectory;

    public static string ResolveDefaultStorageDirectory() =>
        AgentObservabilityStorage.ResolveCanonicalRoot();

    public AgentSnapshot RegisterAgent(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.RunId, snapshot.AgentId, snapshot.ModId);
        ValidateText(snapshot.ModName, nameof(snapshot.ModName), 256);
        AgentObservabilityNotification? notification = null;
        lock (gate)
        {
            ThrowIfDisposed();
            AgentObservabilityAgentIdentity identity =
                new(snapshot.RunId, snapshot.AgentId);
            if (agents.TryGetValue(identity, out AgentSnapshot? existing))
            {
                if (!SameIdentity(existing, snapshot))
                {
                    throw new InvalidOperationException(
                        "An agent identity cannot be reused for a different mod.");
                }

                return existing;
            }

            agents[identity] = snapshot;
            TrimAgents();
            PersistRecord(agentsPath, AgentRecordKind, snapshot);
            notification = new AgentObservabilityNotification(
                AgentObservabilityNotificationKind.AgentChanged,
                Agent: snapshot);
        }

        Notify(notification);
        return snapshot;
    }

    public AgentSnapshot UpdateAgent(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.RunId, snapshot.AgentId, snapshot.ModId);
        ValidateText(snapshot.ModName, nameof(snapshot.ModName), 256);
        AgentObservabilityNotification? notification = null;
        lock (gate)
        {
            ThrowIfDisposed();
            AgentObservabilityAgentIdentity identity =
                new(snapshot.RunId, snapshot.AgentId);
            if (!agents.TryGetValue(identity, out AgentSnapshot? existing))
            {
                throw new InvalidOperationException(
                    "An agent must be registered before it can be updated.");
            }

            if (!SameIdentity(existing, snapshot))
            {
                throw new InvalidOperationException(
                    "An agent update cannot change its run or mod identity.");
            }

            agents[identity] = snapshot;
            PersistRecord(agentsPath, AgentRecordKind, snapshot);
            notification = new AgentObservabilityNotification(
                AgentObservabilityNotificationKind.AgentChanged,
                Agent: snapshot);
        }

        Notify(notification);
        return snapshot;
    }

    public AgentEvent AppendEvent(AgentEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.RunId, request.AgentId, request.ModId);
        ValidateText(request.Type, nameof(request.Type), 128);
        ValidateText(request.Summary, nameof(request.Summary), 1024);

        AgentEvent eventRecord;
        List<AgentObservabilityNotification> notifications = [];
        lock (gate)
        {
            ThrowIfDisposed();
            if (!agents.TryGetValue(
                    new AgentObservabilityAgentIdentity(request.RunId, request.AgentId),
                    out AgentSnapshot? agent))
            {
                throw new InvalidOperationException(
                    "An agent must be registered before it can emit events.");
            }

            if (!string.Equals(agent.RunId, request.RunId, StringComparison.Ordinal) ||
                !string.Equals(agent.ModId, request.ModId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "An event cannot cross run, agent, or mod boundaries.");
            }

            long requestedTimestamp = request.Timestamp.GetValueOrDefault(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (requestedTimestamp < 0)
            {
                requestedTimestamp = 0;
            }

            // Timestamp ties are expected under concurrency. Sequence is the
            // deterministic total-order tiebreaker. File-backed stores obtain
            // it from a small application-level counter so separate RimLiaison
            // processes cannot reuse the same sequence number.
            long timestamp = Math.Max(requestedTimestamp, lastTimestamp);
            lastTimestamp = timestamp;
            long sequence = AllocateSequence();
            eventRecord = new AgentEvent
            {
                Id = "evt-" + Guid.NewGuid().ToString("N"),
                RunId = request.RunId,
                AgentId = request.AgentId,
                ModId = request.ModId,
                Timestamp = timestamp,
                Sequence = sequence,
                Stage = request.Stage,
                Type = request.Type.Trim(),
                Summary = AgentObservabilityData.BoundText(request.Summary, 1024),
                TraceId = AgentObservabilityData.BoundIdentifier(request.TraceId, 128),
                SpanId = AgentObservabilityData.BoundIdentifier(request.SpanId, 128),
                Data = AgentObservabilityData.ToElement(
                    request.Data,
                    options.MaximumEventDataBytes)
            };
            events.Add(eventRecord);
            TrimEvents();
            PersistRecord(eventsPath, EventRecordKind, eventRecord);
            notifications.Add(new AgentObservabilityNotification(
                AgentObservabilityNotificationKind.EventAppended,
                Event: eventRecord));

            if (options.EnableIssueDetection)
            {
                foreach (AgentIssue issue in issueDetector.Observe(
                             eventRecord,
                             issues.Values))
                {
                    issues[issue.Id] = issue;
                    PersistRecord(issuesPath, IssueRecordKind, issue);
                    notifications.Add(new AgentObservabilityNotification(
                        AgentObservabilityNotificationKind.IssueChanged,
                        Event: eventRecord,
                        Issue: issue));
                }
                TrimIssues();
            }
        }

        foreach (AgentObservabilityNotification notification in notifications)
        {
            Notify(notification);
        }
        return eventRecord;
    }

    public IReadOnlyList<AgentSnapshot> GetAgents(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        int limit = 200)
    {
        ValidateLimit(limit, 10_000);
        lock (gate)
        {
            return agents.Values
                .Where(agent => Matches(agent.RunId, runId) &&
                    Matches(agent.AgentId, agentId) &&
                    Matches(agent.ModId, modId))
                .OrderByDescending(static agent => agent.StartTime)
                .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }
    }

    public IReadOnlyList<AgentEvent> GetEvents(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        int limit = 1000)
    {
        ValidateLimit(limit, 50_000);
        lock (gate)
        {
            return events
                .Where(eventRecord => Matches(eventRecord.RunId, runId) &&
                    Matches(eventRecord.AgentId, agentId) &&
                    Matches(eventRecord.ModId, modId))
                .OrderBy(static eventRecord => eventRecord.Sequence)
                .ThenBy(static eventRecord => eventRecord.Timestamp)
                .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
                .TakeLast(limit)
                .ToArray();
        }
    }

    public IReadOnlyList<AgentIssue> GetIssues(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        bool includeRecovered = true,
        int limit = 500)
    {
        ValidateLimit(limit, 10_000);
        lock (gate)
        {
            return issues.Values
                .Where(issue => Matches(issue.RunId, runId) &&
                    Matches(issue.AgentId, agentId) &&
                    Matches(issue.ModId, modId) &&
                    (includeRecovered || !issue.Recovered))
                .OrderByDescending(static issue => issue.Timestamp)
                .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }
    }

    public AgentObservabilityView Query(
        string? runId = null,
        string? agentId = null,
        string? modId = null,
        bool issuesOnly = false,
        int limit = 500)
    {
        // Issues have a deliberately lower storage/query bound than events.
        // Keep the aggregate query contract valid for callers that request the
        // larger event bound (for example, the desktop UI's initial cache).
        int issueLimit = Math.Min(limit, 10_000);
        IReadOnlyList<AgentIssue> selectedIssues = GetIssues(
            runId,
            agentId,
            modId,
            includeRecovered: true,
            issueLimit);
        return new AgentObservabilityView(
            GetAgents(runId, agentId, modId, Math.Min(limit, 10_000)),
            issuesOnly
                ? []
                : GetEvents(runId, agentId, modId, Math.Min(limit, 50_000)),
            selectedIssues);
    }

    public AgentDiagnosticBundle CreateDiagnosticBundle(
        IEnumerable<string> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        lock (gate)
        {
            string[] requestedIds = issueIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(options.MaximumIssues)
                .ToArray();
            AgentIssue[] selectedIssues = requestedIds
                .Select(id => issues.TryGetValue(id, out AgentIssue? issue) ? issue : null)
                .Where(static issue => issue is not null)
                .Select(static issue => issue!)
                .OrderBy(static issue => issue.Timestamp)
                .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                .ToArray();
            HashSet<string> selectedEventIds = selectedIssues
                .SelectMany(static issue => issue.EventIds)
                .ToHashSet(StringComparer.Ordinal);
            AgentEvent[] supportingEvents = events
                .Where(eventRecord => selectedEventIds.Contains(eventRecord.Id))
                .OrderBy(static eventRecord => eventRecord.Sequence)
                .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
                .ToArray();
            HashSet<(string RunId, string AgentId, string ModId)> identities = selectedIssues
                .Select(issue => (issue.RunId, issue.AgentId, issue.ModId))
                .ToHashSet();
            AgentDiagnosticMod[] mods = agents.Values
                .Where(agent => identities.Contains((agent.RunId, agent.AgentId, agent.ModId)))
                .Select(agent => new AgentDiagnosticMod(
                    agent.ModId,
                    agent.ModName,
                    agent.AgentId,
                    agent.RunId))
                .OrderBy(static value => value.ModId, StringComparer.Ordinal)
                .ThenBy(static value => value.AgentId, StringComparer.Ordinal)
                .ToArray();

            var toolCalls = new HashSet<string>(StringComparer.Ordinal);
            var commands = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (AgentEvent eventRecord in supportingEvents)
            {
                AddDataString(eventRecord.Data, toolCalls, "toolCallId", "toolName");
                AddDataString(eventRecord.Data, commands, "command");
                AddDataString(eventRecord.Data, files, "filePath", "path");
                AddDataStrings(eventRecord.Data, toolCalls, "relatedToolCalls");
                AddDataStrings(eventRecord.Data, commands, "relatedCommands");
                AddDataStrings(eventRecord.Data, files, "relatedFiles");
            }

            AgentRecoveryStep[] recoveryPath = supportingEvents
                .Where(static eventRecord => IsRecoveryEvent(eventRecord.Type) ||
                    eventRecord.Type is AgentEventTypes.RetryStarted or
                        AgentEventTypes.RetryCompleted)
                .Select(eventRecord => new AgentRecoveryStep(
                    eventRecord.Id,
                    eventRecord.Timestamp,
                    eventRecord.Type,
                    eventRecord.Summary,
                    IsSuccessfulEvent(eventRecord)))
                .ToArray();
            AgentTraceReference[] traces = supportingEvents
                .Where(static eventRecord => !string.IsNullOrWhiteSpace(eventRecord.TraceId))
                .GroupBy(static eventRecord => eventRecord.TraceId!, StringComparer.Ordinal)
                .Select(group => new AgentTraceReference(
                    group.Key,
                    group.Select(static eventRecord => eventRecord.SpanId)
                        .Where(static spanId => !string.IsNullOrWhiteSpace(spanId))
                        .Select(static spanId => spanId!)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static spanId => spanId, StringComparer.Ordinal)
                        .ToArray()))
                .OrderBy(static trace => trace.TraceId, StringComparer.Ordinal)
                .ToArray();

            return new AgentDiagnosticBundle
            {
                IssueIds = selectedIssues.Select(static issue => issue.Id).ToArray(),
                Mods = mods,
                Issues = selectedIssues,
                SupportingEvents = supportingEvents,
                ToolCalls = toolCalls
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray(),
                Commands = commands
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray(),
                Files = files
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray(),
                RecoveryPath = recoveryPath,
                Traces = traces.Take(options.MaximumBundleEvidenceValues).ToArray()
            };
        }
    }

    public IDisposable Subscribe(Action<AgentObservabilityNotification> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            ThrowIfDisposed();
            subscribers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    /// <summary>
    /// Reloads externally persisted records and publishes only records or
    /// updates not already observed by this process. File-backed stores also
    /// call this automatically from their file watcher; the public method is
    /// useful for deterministic hosts and integration tests.
    /// </summary>
    public void Refresh()
    {
        if (storageDirectory is null)
        {
            return;
        }

        lock (refreshGate)
        {
            List<AgentObservabilityNotification> notifications = [];
            lock (gate)
            {
                ThrowIfDisposed();
                Dictionary<string, AgentEvent> previousEvents = events
                    .ToDictionary(static eventRecord => eventRecord.Id, StringComparer.Ordinal);
                Dictionary<string, AgentIssue> previousIssues = new(issues, StringComparer.Ordinal);
                Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot> previousAgents =
                    new(agents);

                if (!ReloadPersistedRecordsLocked())
                {
                    return;
                }

                foreach (AgentSnapshot agent in agents.Values)
                {
                    AgentObservabilityAgentIdentity identity =
                        new(agent.RunId, agent.AgentId);
                    if (!previousAgents.TryGetValue(identity, out AgentSnapshot? previous) ||
                        !RecordsEqual(previous, agent))
                    {
                        notifications.Add(new AgentObservabilityNotification(
                            AgentObservabilityNotificationKind.AgentChanged,
                            Agent: agent));
                    }
                }

                foreach (AgentEvent eventRecord in events)
                {
                    if (!previousEvents.ContainsKey(eventRecord.Id))
                    {
                        notifications.Add(new AgentObservabilityNotification(
                            AgentObservabilityNotificationKind.EventAppended,
                            Event: eventRecord));
                    }
                }

                foreach (AgentIssue issue in issues.Values)
                {
                    if (!previousIssues.TryGetValue(issue.Id, out AgentIssue? previous) ||
                        !RecordsEqual(previous, issue))
                    {
                        notifications.Add(new AgentObservabilityNotification(
                            AgentObservabilityNotificationKind.IssueChanged,
                            Issue: issue));
                    }
                }
            }

            // Keep notification delivery in the same refresh critical section.
            // Otherwise a second watcher/manual refresh could observe the new
            // state before the first refresh delivered its callbacks and
            // suppress the only live update for a desktop subscriber.
            foreach (AgentObservabilityNotification notification in notifications)
            {
                Notify(notification);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            subscribers.Clear();
        }

        storageWatcher?.Dispose();
    }

    private FileSystemWatcher? CreateStorageWatcher()
    {
        if (storageDirectory is null)
        {
            return null;
        }

        try
        {
            var watcher = new FileSystemWatcher(storageDirectory, "*.jsonl")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnStorageChanged;
            watcher.Created += OnStorageChanged;
            watcher.Renamed += OnStorageRenamed;
            watcher.Error += OnStorageWatcherError;
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void OnStorageChanged(object sender, FileSystemEventArgs args) =>
        ScheduleRefresh();

    private void OnStorageRenamed(object sender, RenamedEventArgs args) =>
        ScheduleRefresh();

    private void OnStorageWatcherError(object sender, ErrorEventArgs args) =>
        ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref refreshRequested, 1);
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    Interlocked.Exchange(ref refreshRequested, 0);
                    try
                    {
                        Refresh();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
                while (Volatile.Read(ref refreshRequested) != 0);
            }
            catch
            {
                // Live updates are a presentation enhancement. The next file
                // notification can schedule another bounded refresh.
            }
            finally
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (Volatile.Read(ref refreshRequested) != 0)
                {
                    ScheduleRefresh();
                }
            }
        });
    }

    private void LoadPersistedRecords()
    {
        if (storageDirectory is null)
        {
            return;
        }

        lock (gate)
        {
            ReloadPersistedRecordsLocked();
        }
    }

    private bool ReloadPersistedRecordsLocked()
    {
        IReadOnlyList<JsonElement>? persistedEvents = ReadRecords(
            eventsPath,
            EventRecordKind);
        IReadOnlyList<JsonElement>? persistedIssues = ReadRecords(
            issuesPath,
            IssueRecordKind);
        IReadOnlyList<JsonElement>? persistedAgents = ReadRecords(
            agentsPath,
            AgentRecordKind);
        if (persistedEvents is null || persistedIssues is null || persistedAgents is null)
        {
            return false;
        }

        events.Clear();
        issues.Clear();
        agents.Clear();
        LoadEvents(persistedEvents);
        LoadIssues(persistedIssues);
        LoadAgents(persistedAgents);
        nextSequence = events.Count == 0
            ? 0
            : events.Max(static eventRecord => eventRecord.Sequence);
        lastTimestamp = events.Count == 0
            ? 0
            : events.Max(static eventRecord => eventRecord.Timestamp);
        TrimEvents();
        TrimIssues();
        TrimAgents();
        return true;
    }

    private void LoadEvents(IReadOnlyList<JsonElement> records)
    {
        foreach (JsonElement value in records)
        {
            try
            {
                AgentEvent? valueRecord = value.Deserialize<AgentEvent>(
                    AgentObservabilityJson.Options);
                if (valueRecord is not null &&
                    !string.IsNullOrWhiteSpace(valueRecord.Id) &&
                    valueRecord.Sequence > 0)
                {
                    events.Add(valueRecord with
                    {
                        Summary = AgentObservabilityData.BoundText(valueRecord.Summary, 1024),
                        Data = AgentObservabilityData.ToElement(
                            valueRecord.Data,
                            options.MaximumEventDataBytes)
                    });
                }
            }
            catch (JsonException)
            {
                // A corrupt historical line is ignored; current runtime state
                // remains available and new lines are still appendable.
            }
        }
        events.Sort(static (left, right) =>
        {
            int sequence = left.Sequence.CompareTo(right.Sequence);
            return sequence != 0
                ? sequence
                : string.CompareOrdinal(left.Id, right.Id);
        });
    }

    private void LoadIssues(IReadOnlyList<JsonElement> records)
    {
        foreach (JsonElement value in records)
        {
            try
            {
                AgentIssue? issue = value.Deserialize<AgentIssue>(AgentObservabilityJson.Options);
                if (issue is not null && !string.IsNullOrWhiteSpace(issue.Id))
                {
                    issues[issue.Id] = NormalizePersistedIssue(issue);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private void LoadAgents(IReadOnlyList<JsonElement> records)
    {
        foreach (JsonElement value in records)
        {
            try
            {
                AgentSnapshot? agent = value.Deserialize<AgentSnapshot>(
                    AgentObservabilityJson.Options);
                if (agent is not null && !string.IsNullOrWhiteSpace(agent.AgentId))
                {
                    agents[new AgentObservabilityAgentIdentity(agent.RunId, agent.AgentId)] = agent;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private IReadOnlyList<JsonElement>? ReadRecords(string? path, string expectedKind)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        FileStream? lockStream = null;
        try
        {
            lockStream = TryAcquireFileLock(path + ".lock");
            if (lockStream is null)
            {
                return null;
            }

            return ReadRecordsUnlocked(path, expectedKind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    private IReadOnlyList<JsonElement> ReadRecordsUnlocked(
        string path,
        string expectedKind)
    {
        var records = new List<JsonElement>();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > options.MaximumPersistedBytes)
            {
                continue;
            }

            JsonElement? parsedValue = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    line,
                    new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("kind", out JsonElement kind) ||
                    !string.Equals(kind.GetString(), expectedKind, StringComparison.Ordinal) ||
                    !root.TryGetProperty("value", out JsonElement value))
                {
                    continue;
                }
                parsedValue = value.Clone();
            }
            catch (JsonException)
            {
            }
            if (parsedValue.HasValue)
            {
                records.Add(parsedValue.Value);
            }
        }

        return records;
    }

    private long AllocateSequence()
    {
        if (sequencePath is null)
        {
            return ++nextSequence;
        }

        try
        {
            using FileStream lockStream = AcquireFileLock(sequencePath + ".lock");
            long persisted = ReadSequence(sequencePath);
            long next = Math.Max(persisted, nextSequence) + 1;
            WriteSequence(sequencePath, next);
            nextSequence = next;
            return next;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // Product state remains useful even when the optional durable
            // sequence counter is unavailable. The event id still provides a
            // unique identity, and the local counter preserves ordering.
            return ++nextSequence;
        }
    }

    private static long ReadSequence(string path)
    {
        try
        {
            return long.TryParse(
                    File.ReadAllText(path).Trim(),
                    out long value) && value > 0
                ? value
                : 0;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    private static void WriteSequence(string path, long value)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporaryPath,
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool RecordsEqual<T>(T left, T right)
    {
        try
        {
            return string.Equals(
                JsonSerializer.Serialize(left, AgentObservabilityJson.Options),
                JsonSerializer.Serialize(right, AgentObservabilityJson.Options),
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return EqualityComparer<T>.Default.Equals(left, right);
        }
    }

    private void PersistRecord<T>(string? path, string kind, T value)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(
                new PersistedRecord<T>(kind, value),
                AgentObservabilityJson.Options);
            using FileStream lockStream = AcquireFileLock(path + ".lock");
            AppendLineUnlocked(path, json);
            CompactIfNeeded(path, kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or JsonException)
        {
            // Persistence is deliberately best effort. The in-memory product
            // state remains authoritative for the active run.
        }
    }

    private void CompactIfNeeded(string path, string kind)
    {
        try
        {
            if (new FileInfo(path).Length <= options.MaximumPersistedBytes)
            {
                return;
            }

            switch (kind)
            {
                case EventRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedEventsForCompaction(path)
                            .Values
                            .OrderBy(static eventRecord => eventRecord.Sequence)
                            .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
                            .TakeLast(options.MaximumEvents));
                    break;
                case IssueRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedIssuesForCompaction(path)
                            .Values
                            .OrderBy(static issue => issue.Timestamp)
                            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                            .TakeLast(options.MaximumIssues));
                    break;
                case AgentRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedAgentsForCompaction(path)
                            .Values
                            .OrderBy(static agent => agent.StartTime)
                            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
                            .TakeLast(options.MaximumAgents));
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
        }
    }

    private void Rewrite<T>(string path, string kind, IEnumerable<T> values)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var lines = new Queue<(string Value, int Bytes)>();
            long totalBytes = 0;
            foreach (T value in values)
            {
                string json = JsonSerializer.Serialize(
                    new PersistedRecord<T>(kind, value),
                    AgentObservabilityJson.Options);
                int bytes = Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
                lines.Enqueue((json, bytes));
                totalBytes += bytes;
                while (totalBytes > options.MaximumPersistedBytes && lines.Count > 1)
                {
                    totalBytes -= lines.Dequeue().Bytes;
                }
            }

            using (var writer = new StreamWriter(
                       new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                       new UTF8Encoding(false)))
            {
                foreach ((string value, _) in lines)
                {
                    writer.WriteLine(value);
                }
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private Dictionary<string, AgentEvent> ReadPersistedEventsForCompaction(string path)
    {
        var result = new Dictionary<string, AgentEvent>(StringComparer.Ordinal);
        foreach (JsonElement value in ReadRecordsUnlocked(path, EventRecordKind))
        {
            try
            {
                AgentEvent? eventRecord = value.Deserialize<AgentEvent>(
                    AgentObservabilityJson.Options);
                if (eventRecord is not null && !string.IsNullOrWhiteSpace(eventRecord.Id))
                {
                    result[eventRecord.Id] = eventRecord;
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (AgentEvent eventRecord in events)
        {
            result[eventRecord.Id] = eventRecord;
        }
        return result;
    }

    private Dictionary<string, AgentIssue> ReadPersistedIssuesForCompaction(string path)
    {
        var result = new Dictionary<string, AgentIssue>(StringComparer.Ordinal);
        foreach (JsonElement value in ReadRecordsUnlocked(path, IssueRecordKind))
        {
            try
            {
                AgentIssue? issue = value.Deserialize<AgentIssue>(
                    AgentObservabilityJson.Options);
                if (issue is not null && !string.IsNullOrWhiteSpace(issue.Id))
                {
                    result[issue.Id] = issue;
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (AgentIssue issue in issues.Values)
        {
            result[issue.Id] = issue;
        }
        return result;
    }

    private Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot>
        ReadPersistedAgentsForCompaction(string path)
    {
        var result = new Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot>();
        foreach (JsonElement value in ReadRecordsUnlocked(path, AgentRecordKind))
        {
            try
            {
                AgentSnapshot? agent = value.Deserialize<AgentSnapshot>(
                    AgentObservabilityJson.Options);
                if (agent is not null && !string.IsNullOrWhiteSpace(agent.AgentId))
                {
                    result[new AgentObservabilityAgentIdentity(agent.RunId, agent.AgentId)] = agent;
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach ((AgentObservabilityAgentIdentity identity, AgentSnapshot agent) in agents)
        {
            result[identity] = agent;
        }
        return result;
    }

    private static void AppendLineUnlocked(string path, string value)
    {
        using FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            bufferSize: 1024,
            leaveOpen: true);
        writer.WriteLine(value);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static FileStream AcquireFileLock(string path)
    {
        FileStream? stream = TryAcquireFileLock(path);
        return stream ?? throw new IOException(
            "Could not acquire the observability persistence lock: " + path);
    }

    private static FileStream? TryAcquireFileLock(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(5 * Math.Min(attempt + 1, 10));
            }
        }

        return null;
    }

    private void TrimEvents()
    {
        if (events.Count > options.MaximumEvents)
        {
            events.RemoveRange(0, events.Count - options.MaximumEvents);
        }
    }

    private void TrimIssues()
    {
        if (issues.Count <= options.MaximumIssues)
        {
            return;
        }

        foreach (string id in issues.Values
                     .OrderByDescending(static issue => issue.Recovered)
                     .ThenBy(static issue => issue.Timestamp)
                     .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                     .Take(issues.Count - options.MaximumIssues)
                     .Select(static issue => issue.Id)
                     .ToArray())
        {
            issues.Remove(id);
        }
    }

    private void TrimAgents()
    {
        if (agents.Count <= options.MaximumAgents)
        {
            return;
        }

        foreach (AgentObservabilityAgentIdentity identity in agents.Values
                     .OrderBy(static agent => agent.Status is AgentStatus.Completed or AgentStatus.Failed
                         ? 0
                         : 1)
                     .ThenBy(static agent => agent.StartTime)
                     .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
                     .Take(agents.Count - options.MaximumAgents)
                     .Select(static agent => new AgentObservabilityAgentIdentity(
                         agent.RunId,
                         agent.AgentId))
                     .ToArray())
        {
            agents.Remove(identity);
        }
    }

    private AgentIssue NormalizePersistedIssue(AgentIssue issue) => issue with
    {
        Summary = AgentObservabilityData.BoundText(issue.Summary, 512),
        EventIds = issue.EventIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(options.MaximumIssueEventReferences)
            .ToArray(),
        RelatedFiles = NormalizeValues(issue.RelatedFiles),
        RelatedToolCalls = NormalizeValues(issue.RelatedToolCalls),
        RelatedCommands = NormalizeCommands(issue.RelatedCommands),
        TraceId = AgentObservabilityData.BoundIdentifier(issue.TraceId, 128),
        SpanIds = NormalizeValues(issue.SpanIds, 128),
        OperationKey = AgentObservabilityData.BoundIdentifier(issue.OperationKey, 256),
        RetryCount = Math.Max(0, issue.RetryCount),
        Occurrences = Math.Max(1, issue.Occurrences)
    };

    private static IReadOnlyList<string>? NormalizeValues(
        IReadOnlyList<string>? values,
        int maximum = 128)
    {
        string[] normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => AgentObservabilityData.BoundText(value, 2048))
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyList<string>? NormalizeCommands(IReadOnlyList<string>? values)
    {
        string[] normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(AgentObservabilityData.SanitizeCommand)
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private void Notify(AgentObservabilityNotification? notification)
    {
        if (notification is null)
        {
            return;
        }

        Action<AgentObservabilityNotification>[] handlers;
        lock (gate)
        {
            handlers = subscribers.ToArray();
        }
        foreach (Action<AgentObservabilityNotification> handler in handlers)
        {
            try
            {
                handler(notification);
            }
            catch
            {
                // A subscriber is a presentation concern and cannot break the
                // authoritative store or the running agent.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentObservabilityStore));
        }
    }

    private static bool SameIdentity(AgentSnapshot left, AgentSnapshot right) =>
        string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal) &&
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.ModId, right.ModId, StringComparison.Ordinal);

    private static bool Matches(string value, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(value, expected, StringComparison.Ordinal);

    private static void ValidateIdentity(string runId, string agentId, string modId)
    {
        ValidateText(runId, nameof(runId), 256);
        ValidateText(agentId, nameof(agentId), 256);
        ValidateText(modId, nameof(modId), 256);
    }

    private static void ValidateText(string value, string parameterName, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, bounded, and free of control characters.",
                parameterName);
        }
    }

    private static void ValidateLimit(int limit, int maximum)
    {
        if (limit <= 0 || limit > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    private static bool IsRecoveryEvent(string type) =>
        type.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith("passed", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessfulEvent(AgentEvent eventRecord) =>
        eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
        eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
        eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase) ||
        AgentObservabilityData.GetString(eventRecord.Data, "outcome") is "success" or "passed";

    private static void AddDataString(
        JsonElement? data,
        ISet<string> target,
        params string[] names)
    {
        foreach (string name in names)
        {
            string? value = AgentObservabilityData.GetString(data, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }
    }

    private static void AddDataStrings(
        JsonElement? data,
        ISet<string> target,
        string name)
    {
        foreach (string value in AgentObservabilityData.GetStrings(data, name))
        {
            target.Add(value);
        }
    }

    private sealed record PersistedRecord<T>(string Kind, T Value);

    private sealed class Subscription(
        AgentObservabilityStore owner,
        Action<AgentObservabilityNotification> handler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (owner.gate)
            {
                owner.subscribers.Remove(handler);
            }
        }
    }
}

public static class AgentObservabilityData
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?<name>\b(?:password|passwd|secret|token|api[-_]?key|authorization|credential|access[-_]?token|refresh[-_]?token|client[-_]?secret)\b)(?<separator>\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationValue = new(
        @"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KnownCredentialFormat = new(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{16,}|xox[baprs]-[A-Za-z0-9-]{16,}|AIza[0-9A-Za-z_-]{20,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] SensitiveNames =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apiKey",
        "apikey",
        "authorization",
        "credential"
    ];

    public static JsonElement? ToElement(object? value, int maximumBytes)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            JsonElement element = value is JsonElement json
                ? json.Clone()
                : JsonSerializer.SerializeToElement(value, AgentObservabilityJson.Options);
            JsonElement sanitized = Sanitize(element, depth: 0);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                sanitized,
                AgentObservabilityJson.Options);
            if (bytes.Length > maximumBytes)
            {
                return JsonSerializer.SerializeToElement(
                    new { truncated = true, bytes = bytes.Length },
                    AgentObservabilityJson.Options);
            }

            return sanitized;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return JsonSerializer.SerializeToElement(
                new { unavailable = true },
                AgentObservabilityJson.Options);
        }
    }

    public static string BoundText(string? value, int maximum)
    {
        string text = RedactText(value?.Trim() ?? string.Empty);
        if (text.Length <= maximum)
        {
            return text;
        }

        return text[..Math.Max(0, maximum - 13)] + "...[truncated]";
    }

    private static string RedactText(string value)
    {
        string sanitized = AuthorizationValue.Replace(value, "[REDACTED]");
        sanitized = KnownCredentialFormat.Replace(sanitized, "[REDACTED]");
        return SensitiveAssignment.Replace(
            sanitized,
            match => match.Groups["name"].Value +
                match.Groups["separator"].Value +
                "[REDACTED]");
    }

    public static string? BoundIdentifier(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : BoundText(value, maximum);

    public static string? GetString(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    public static long? GetInt64(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
        {
            return null;
        }

        return result;
    }

    public static bool GetBoolean(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } element &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True &&
        value.GetBoolean();

    public static IReadOnlyList<string> GetStrings(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Take(128)
            .ToArray();
    }

    public static string SanitizeCommand(string? command, int maximum = 2048)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        string[] tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            string lower = token.ToLowerInvariant();
            if (SensitiveNames.Any(name => lower.Contains(name.ToLowerInvariant(), StringComparison.Ordinal)) ||
                lower is "-p" or "--password" or "--token" or "--api-key" or "-token")
            {
                tokens[index] = "[REDACTED]";
                if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    tokens[index + 1] = "[REDACTED]";
                }
            }
        }

        return BoundText(string.Join(' ', tokens), maximum);
    }

    private static JsonElement Sanitize(JsonElement value, int depth)
    {
        if (depth > 8)
        {
            return JsonSerializer.SerializeToElement(
                new { truncated = true },
                AgentObservabilityJson.Options);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var objectValue = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject().Take(128))
                {
                    if (SensitiveNames.Any(name =>
                            string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase) ||
                            property.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        objectValue[property.Name] = "[REDACTED]";
                    }
                    else if (string.Equals(property.Name, "command", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        objectValue[property.Name] = SanitizeCommand(property.Value.GetString(), 4096);
                    }
                    else
                    {
                        objectValue[property.Name] = Sanitize(property.Value, depth + 1);
                    }
                }
                return JsonSerializer.SerializeToElement(objectValue, AgentObservabilityJson.Options);
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(
                    value.EnumerateArray().Take(128).Select(item => Sanitize(item, depth + 1)),
                    AgentObservabilityJson.Options);
            case JsonValueKind.String:
                return JsonSerializer.SerializeToElement(
                    BoundText(value.GetString(), 4096),
                    AgentObservabilityJson.Options);
            default:
                return value.Clone();
        }
    }
}
