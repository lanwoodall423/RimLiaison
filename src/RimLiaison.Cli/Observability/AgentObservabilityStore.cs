using RimDev.Contracts;
using RimLiaison.Validation;
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

    AgentDiagnosticEvidenceReference? PersistEvidence(
        string kind,
        string? content,
        bool truncated = false);

    AgentDiagnosticEvidence? GetEvidence(string evidenceId);

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
public interface IAgentObservabilityHydrationStore
{
    bool InitialHydrationPending { get; }
    bool UseWatcherForLiveRefresh { get; }

    Task<AgentObservabilityHydrationResult> HydrateRecentAsync(
        int maximumEvents = 2_000,
        int maximumIssues = 500,
        int maximumAgents = 250,
        CancellationToken cancellationToken = default);

    Task<AgentObservabilityHydrationResult> HydrateHistoryAsync(
        CancellationToken cancellationToken = default);
}

public sealed record AgentObservabilityHydrationResult(
    bool Completed,
    bool Degraded,
    int EventsLoaded,
    int IssuesLoaded,
    int AgentsLoaded,
    string? Message = null);


/// <summary>
/// The authoritative RimLiaison activity and issue store. With a storage
/// directory it uses bounded append-only JSONL records; without one it is a
/// deterministic in-memory store suitable for tests and embedded callers.
/// </summary>
public sealed class AgentObservabilityStore :
    IAgentObservabilityStore,
    IAgentObservabilityLiveStore,
    IAgentObservabilityHydrationStore,
    IAgentObservabilityHistoryStatus,
    IAgentReliabilityCampaignStore,
    IDisposable
{
    private const string EventsFileName = "events.jsonl";
    private const string IssuesFileName = "issues.jsonl";
    private const string AgentsFileName = "agents.jsonl";
    private const string ReliabilityCampaignsFileName = "reliability-campaigns.jsonl";
    private const string SequenceFileName = "metadata.sequence";
    private const string EvidenceDirectoryName = "evidence";
    private const string EventRecordKind = "event";
    private const string IssueRecordKind = "issue";
    private const string AgentRecordKind = "agent";
    private const string ReliabilityCampaignRecordKind = "reliability-campaign";
    private const int EvidenceMaintenanceSlack = 128;

    private readonly object gate = new();
    private int initialHydrationPending;
    private readonly bool useWatcherForLiveRefresh;
    private readonly object refreshGate = new();
    private readonly AgentObservabilityOptions options;
    private readonly Func<long> nowMilliseconds;
    private readonly string? storageDirectory;
    private readonly string? eventsPath;
    private readonly string? issuesPath;
    private readonly string? agentsPath;
    private readonly string? reliabilityCampaignsPath;
    private readonly string? sequencePath;
    private readonly string? evidenceDirectory;
    private readonly FileSystemWatcher? storageWatcher;
    private readonly List<AgentEvent> events = [];
    private readonly Dictionary<string, AgentIssue> issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentDiagnosticEvidence> evidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentReliabilityCampaignConfiguration> reliabilityCampaigns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents = [];
    private readonly List<Action<AgentObservabilityNotification>> subscribers = [];
    private readonly AgentIssueDetector issueDetector;
    private long nextSequence;
    private long lastTimestamp;
    private long diagnosticBundleCreationCount;
    private int refreshRequested;
    private int refreshQueued;
    private bool historyComplete = true;
    private bool historyDegraded;
    private int disposed;
    private bool identityMigrationPending;

    public AgentObservabilityStore(
        string? storageDirectory = null,
        AgentObservabilityOptions? options = null,
        bool loadPersistedRecords = true,
        Func<long>? nowMilliseconds = null)
    {
        this.options = options ?? new AgentObservabilityOptions();
        this.options.Validate();
        this.nowMilliseconds = nowMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!string.IsNullOrWhiteSpace(storageDirectory))
        {
            this.storageDirectory = Path.GetFullPath(storageDirectory);
            Directory.CreateDirectory(this.storageDirectory);
            eventsPath = Path.Combine(this.storageDirectory, EventsFileName);
            issuesPath = Path.Combine(this.storageDirectory, IssuesFileName);
            agentsPath = Path.Combine(this.storageDirectory, AgentsFileName);
            reliabilityCampaignsPath = Path.Combine(this.storageDirectory, ReliabilityCampaignsFileName);
            sequencePath = Path.Combine(this.storageDirectory, SequenceFileName);
            evidenceDirectory = Path.Combine(this.storageDirectory, EvidenceDirectoryName);
            Directory.CreateDirectory(evidenceDirectory);
        }

        issueDetector = new AgentIssueDetector(this.options);
        useWatcherForLiveRefresh = this.storageDirectory is not null && !loadPersistedRecords;
        initialHydrationPending = useWatcherForLiveRefresh ? 1 : 0;
        if (loadPersistedRecords)
        {
            LoadPersistedRecords();
        }
        storageWatcher = CreateStorageWatcher();
    }

    public static AgentObservabilityStore CreateDefault(
        AgentObservabilityOptions? options = null,
        bool loadPersistedRecords = true)
    {
        string directory = AgentObservabilityStorage.ResolveCanonicalRoot();
        try
        {
            return new AgentObservabilityStore(
                directory,
                options,
                loadPersistedRecords);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // Observability must never prevent the owning command from running.
            return new AgentObservabilityStore(
                options: options,
                loadPersistedRecords: loadPersistedRecords);
        }
    }
    public bool HistoryComplete
    {
        get
        {
            lock (gate)
            {
                return historyComplete;
            }
        }
    }

    public bool HistoryDegraded
    {
        get
        {
            lock (gate)
            {
                return historyDegraded;
            }
        }
    }

    public AgentReliabilityCampaignConfiguration? GetReliabilityCampaign(string campaignId)
    {
        if (string.IsNullOrWhiteSpace(campaignId))
        {
            return null;
        }

        lock (gate)
        {
            return reliabilityCampaigns.TryGetValue(campaignId, out AgentReliabilityCampaignConfiguration? configuration)
                ? configuration
                : null;
        }
    }
    public IReadOnlyList<AgentReliabilityCampaignConfiguration> GetReliabilityCampaigns()
    {
        lock (gate)
        {
            return reliabilityCampaigns.Values
                .OrderByDescending(value => value.StartedAtUtc ?? value.CreatedAtUtc)
                .ThenBy(value => value.CampaignId, StringComparer.Ordinal)
                .ToArray();
        }
    }


    public void SaveReliabilityCampaign(AgentReliabilityCampaignConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.CampaignId))
        {
            throw new ArgumentException("A campaign id is required.", nameof(configuration));
        }

        lock (gate)
        {
            reliabilityCampaigns[configuration.CampaignId] = configuration;
        }
        PersistRecord(reliabilityCampaignsPath, ReliabilityCampaignRecordKind, configuration);
    }

    public string? StorageDirectory => storageDirectory;

    public bool InitialHydrationPending =>
        Volatile.Read(ref initialHydrationPending) != 0;
    public bool UseWatcherForLiveRefresh => useWatcherForLiveRefresh;
    public long DiagnosticBundleCreationCount
    {
        get
        {
            lock (gate)
            {
                return diagnosticBundleCreationCount;
            }
        }
    }

    public static string ResolveDefaultStorageDirectory() =>
        AgentObservabilityStorage.ResolveCanonicalRoot();

    public AgentSnapshot RegisterAgent(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = NormalizePersistedAgent(snapshot);
        ValidateIdentity(snapshot.RunId, snapshot.AgentId, snapshot.ModId);
        ValidateOptionalLogicalAgentId(snapshot.LogicalAgentId);
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
        snapshot = NormalizePersistedAgent(snapshot);
        long now = Math.Max(0, nowMilliseconds());
        if (snapshot.Status is (AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting) &&
            snapshot.StartTime > 0 &&
            now - snapshot.StartTime <=
                (long)options.WorkingStalenessThreshold.TotalMilliseconds)
        {
            snapshot = snapshot with
            {
                LastActivityAt = Math.Max(
                    snapshot.LastActivityAt.GetValueOrDefault(),
                    Math.Max(0, nowMilliseconds()))
            };
        }
        ValidateIdentity(snapshot.RunId, snapshot.AgentId, snapshot.ModId);
        ValidateOptionalLogicalAgentId(snapshot.LogicalAgentId);
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
    private void ReconcileLifecycleStateLocked()
    {
        if (agents.Count == 0)
        {
            return;
        }

        var latestActivity = new Dictionary<AgentObservabilityAgentIdentity, long>();
        var terminalEvents = new Dictionary<AgentObservabilityAgentIdentity, AgentEvent>();
        foreach (AgentEvent eventRecord in events)
        {
            AgentObservabilityAgentIdentity identity =
                new(eventRecord.RunId, eventRecord.AgentId);
            if (!latestActivity.TryGetValue(identity, out long latest) ||
                eventRecord.Timestamp > latest)
            {
                latestActivity[identity] = eventRecord.Timestamp;
            }

            if (eventRecord.Type is AgentEventTypes.AgentCompleted or AgentEventTypes.AgentFailed &&
                (!terminalEvents.TryGetValue(identity, out AgentEvent? previous) ||
                 eventRecord.Sequence > previous.Sequence))
            {
                terminalEvents[identity] = eventRecord;
            }
        }

        long now = Math.Max(0, nowMilliseconds());
        long stalenessMilliseconds = Math.Max(
            1,
            (long)options.WorkingStalenessThreshold.TotalMilliseconds);
        foreach ((AgentObservabilityAgentIdentity identity, AgentSnapshot agent) in agents.ToArray())
        {
            if (agent.Status is not (AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting))
            {
                continue;
            }

            AgentSnapshot? reconciled = null;
            if (terminalEvents.TryGetValue(identity, out AgentEvent? terminal))
            {
                bool failed = terminal.Type == AgentEventTypes.AgentFailed;
                bool cancelled = failed &&
                    string.Equals(
                        AgentObservabilityData.GetString(terminal.Data, "outcome"),
                        "cancelled",
                        StringComparison.OrdinalIgnoreCase);
                reconciled = agent with
                {
                    Status = failed ? AgentStatus.Failed : AgentStatus.Completed,
                    CurrentStage = failed ? agent.CurrentStage : DevelopmentStage.Complete,
                    CurrentOperation = failed ? "failed" : "complete",
                    CurrentActivity = failed ? "failed" : "complete",
                    CompletedAt = terminal.Timestamp,
                    CompletionState = failed
                        ? cancelled
                            ? AgentCompletionState.Cancelled
                            : AgentCompletionState.Failed
                        : AgentCompletionState.Succeeded,
                    CompletionResult = failed
                        ? cancelled ? "CANCELLED" : ValidationPolicySchema.Fail
                        : ValidationPolicySchema.Pass,
                    FailureState = failed,
                    FailureSummary = failed
                        ? terminal.Summary
                        : null,
                    LastActivityAt = Math.Max(
                        agent.LastActivityAt.GetValueOrDefault(),
                        terminal.Timestamp)
                };
            }
            else
            {
                long lastActivity = Math.Max(
                    agent.StartTime,
                    Math.Max(
                        agent.LastActivityAt.GetValueOrDefault(),
                        latestActivity.GetValueOrDefault(identity)));
                if (agent.Status is (AgentStatus.Running or AgentStatus.Waiting) &&
                    lastActivity > 0 &&
                    now - lastActivity > stalenessMilliseconds)
                {
                    reconciled = agent with
                    {
                        Status = AgentStatus.Failed,
                        CurrentOperation = "stale",
                        CurrentActivity = "stale",
                        CompletedAt = now,
                        CompletionState = AgentCompletionState.Cancelled,
                        CompletionResult = "STALE",
                        FailureState = true,
                        FailureSummary =
                            "Observability session became stale without a terminal lifecycle event.",
                        LastActivityAt = lastActivity
                    };
                }
                else if (agent.LastActivityAt.GetValueOrDefault() < lastActivity)
                {
                    reconciled = agent with { LastActivityAt = lastActivity };
                }
            }

            if (reconciled is not null && !RecordsEqual(agent, reconciled))
            {
                agents[identity] = reconciled;
                if (storageDirectory is not null)
                {
                    PersistRecord(agentsPath, AgentRecordKind, reconciled);
                }
                identityMigrationPending = true;
            }
        }
    }


    public AgentEvent AppendEvent(AgentEventRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.RunId, request.AgentId, request.ModId);
        ValidateOptionalLogicalAgentId(request.LogicalAgentId);
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

            string logicalAgentId = request.LogicalAgentId ?? agent.LogicalAgentId ?? string.Empty;
            if (!string.Equals(agent.RunId, request.RunId, StringComparison.Ordinal) ||
                !string.Equals(agent.ModId, request.ModId, StringComparison.Ordinal) ||
                !string.Equals(
                    AgentObservabilityLogicalIdentity.For(
                        agent.LogicalAgentId,
                        agent.RunId,
                        agent.AgentId),
                    AgentObservabilityLogicalIdentity.For(
                        logicalAgentId,
                        request.RunId,
                        request.AgentId),
                    StringComparison.Ordinal) ||
                (request.SessionId is not null &&
                 !string.Equals(request.SessionId, agent.SessionId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "An event cannot cross run, agent, session, logical-agent, or mod boundaries.");
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
            string eventId = "evt-" + Guid.NewGuid().ToString("N");
            JsonElement? boundedData = AgentObservabilityData.ToElement(
                request.Data,
                options.MaximumEventDataBytes);
            ToolEventEnvelope contractEvent = ToolEventEnvelope.Create(
                producer: "RimLiaison.Observability",
                eventType: request.Type,
                timestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                identity: new ExecutionIdentity
                {
                    ExecutionId = request.RunId
                },
                subjects:
                [
                    new EntityReference
                    {
                        Kind = agent.EntityType switch
                        {
                            ObservabilityEntityTypes.Tool => EntityReferenceKinds.Tool,
                            ObservabilityEntityTypes.Runtime => EntityReferenceKinds.RuntimeSubject,
                            ObservabilityEntityTypes.Mod => EntityReferenceKinds.Mod,
                            _ => "unknown"
                        },
                        Id = agent.CanonicalEntityId
                    }
                ],
                payload: boundedData,
                provenance: new ContractProvenance
                {
                    Source = AgentObservabilitySchemas.Event
                },
                eventId: eventId,
                maximumPayloadBytes: options.MaximumEventDataBytes);
            eventRecord = new AgentEvent
            {
                Id = eventId,
                RunId = request.RunId,
                AgentId = request.AgentId,
                LogicalAgentId = string.IsNullOrWhiteSpace(logicalAgentId)
                    ? null
                    : logicalAgentId,
                SessionId = request.SessionId ?? agent.SessionId,
                ModId = request.ModId,
                EntityType = agent.EntityType,
                CanonicalEntityId = agent.CanonicalEntityId,
                DisplayName = agent.DisplayName,
                Timestamp = timestamp,
                Sequence = sequence,
                Stage = request.Stage,
                Type = request.Type.Trim(),
                Summary = AgentObservabilityData.BoundText(request.Summary, 1024),
                TraceId = AgentObservabilityData.BoundIdentifier(request.TraceId, 128),
                SpanId = AgentObservabilityData.BoundIdentifier(request.SpanId, 128),
                Data = contractEvent.Payload?.Data
            };
            events.Add(eventRecord);
            ReconcileLifecycleStateLocked();
            if (agents.TryGetValue(
                    new AgentObservabilityAgentIdentity(request.RunId, request.AgentId),
                    out AgentSnapshot? updatedAgent) &&
                !RecordsEqual(agent, updatedAgent))
            {
                notifications.Add(new AgentObservabilityNotification(
                    AgentObservabilityNotificationKind.AgentChanged,
                    Agent: updatedAgent));
            }
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
            ReconcileLifecycleStateLocked();
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
                    (Matches(issue.ModId, modId) ||
                        issue.ReportingModId is not null &&
                        Matches(issue.ReportingModId, modId)) &&
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

    public AgentDiagnosticEvidenceReference? PersistEvidence(
        string kind,
        string? content,
        bool truncated = false)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string normalizedKind = AgentObservabilityData.BoundIdentifier(kind, 128) ??
            "diagnostic";
        string trimmed = content.Trim();
        string bounded = AgentObservabilityData.BoundText(
            trimmed,
            options.MaximumEvidenceBytes);
        bool boundedByLimit = Encoding.UTF8.GetByteCount(trimmed) >
            options.MaximumEvidenceBytes;
        var value = new AgentDiagnosticEvidence(
            "evd-" + Guid.NewGuid().ToString("N"),
            normalizedKind,
            bounded,
            truncated || boundedByLimit);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return null;
            }
            evidence[value.Id] = value;
            PersistEvidenceFile(value);
            if (evidence.Count > options.MaximumEvidenceEntries + EvidenceMaintenanceSlack)
            {
                TrimEvidence();
            }
        }

        return new AgentDiagnosticEvidenceReference(
            value.Id,
            value.Kind,
            value.Content.Length,
            value.Truncated);
    }

    public AgentDiagnosticEvidence? GetEvidence(string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId))
        {
            return null;
        }

        lock (gate)
        {
            if (evidence.TryGetValue(evidenceId, out AgentDiagnosticEvidence? value))
            {
                return value;
            }

            if (evidenceDirectory is null)
            {
                return null;
            }

            AgentDiagnosticEvidence? persisted = ReadEvidenceFile(evidenceId);
            if (persisted is not null)
            {
                evidence[persisted.Id] = persisted;
            }
            return persisted;
        }
    }

    public AgentDiagnosticBundle CreateDiagnosticBundle(
        IEnumerable<string> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        lock (gate)
        {
            diagnosticBundleCreationCount++;
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
            HashSet<string> selectedIssueIdSet = selectedIssues
                .Select(static issue => issue.Id)
                .ToHashSet(StringComparer.Ordinal);
            Dictionary<string, AgentEvent> eventsById = events
                .ToDictionary(static eventRecord => eventRecord.Id, StringComparer.Ordinal);
            HashSet<(string RunId, string AgentId, string ModId)> identities = selectedIssues
                .Select(issue => (issue.RunId, issue.AgentId, issue.ModId))
                .ToHashSet();
            foreach (AgentIssue issue in selectedIssues)
            {
                foreach (string eventId in issue.EventIds)
                {
                    if (eventsById.TryGetValue(eventId, out AgentEvent? eventRecord))
                    {
                        identities.Add((eventRecord.RunId, eventRecord.AgentId, eventRecord.ModId));
                    }
                }
            }
            string[] selectedEventReferences = selectedIssues
                .SelectMany(issue => issue.EventIds.Concat(
                    issue.ResolutionEventId is string resolution
                        ? [resolution]
                        : []))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HashSet<string> includedEventIds = selectedIssues
                .SelectMany(static issue => issue.EventIds)
                .Where(eventsById.ContainsKey)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> missingEventIds = selectedEventReferences
                .Where(eventId => !eventsById.ContainsKey(eventId))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> relatedEventIds = new(includedEventIds, StringComparer.Ordinal);
            HashSet<string> relatedIssueIds = new(selectedIssueIdSet, StringComparer.Ordinal);
            HashSet<string> operationKeys = selectedIssues
                .Select(static issue => issue.OperationKey)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> traceIds = selectedIssues
                .Select(static issue => issue.TraceId)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> spanIds = selectedIssues
                .SelectMany(static issue => issue.SpanIds ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> transactionIds = [];
            HashSet<string> workflowIds = [];

            foreach (AgentIssue issue in selectedIssues)
            {
                foreach (string eventId in issue.EventIds)
                {
                    relatedEventIds.Add(eventId);
                }

                if (issue.ResolutionEventId is not null)
                {
                    relatedEventIds.Add(issue.ResolutionEventId);
                }
            }

            foreach (AgentEvent eventRecord in events
                         .Where(eventRecord => includedEventIds.Contains(eventRecord.Id)))
            {
                AddCorrelationValues(
                    eventRecord,
                    operationKeys,
                    traceIds,
                    spanIds,
                    transactionIds,
                    workflowIds,
                    relatedEventIds,
                    relatedIssueIds);
            }

            // Expand only through stable causal identifiers. Identity is a
            // hard boundary: a concurrent agent in the same run cannot enter
            // the bundle merely because it used the same tool or workflow.
            var correlatedIssues = new Dictionary<string, AgentIssue>(StringComparer.Ordinal);
            bool expanded;
            do
            {
                expanded = false;
                foreach (AgentEvent eventRecord in events
                             .OrderBy(static value => value.Sequence)
                             .ThenBy(static value => value.Id, StringComparer.Ordinal))
                {
                    if (includedEventIds.Count >= options.MaximumBundleSupportingEvents ||
                        !identities.Contains((eventRecord.RunId, eventRecord.AgentId, eventRecord.ModId)) ||
                        includedEventIds.Contains(eventRecord.Id) ||
                        !MatchesDiagnosticCorrelation(
                            eventRecord,
                            includedEventIds,
                            relatedEventIds,
                            operationKeys,
                            traceIds,
                            spanIds,
                            transactionIds,
                            workflowIds,
                            relatedIssueIds))
                    {
                        continue;
                    }

                    includedEventIds.Add(eventRecord.Id);
                    AddCorrelationValues(
                        eventRecord,
                        operationKeys,
                        traceIds,
                        spanIds,
                        transactionIds,
                        workflowIds,
                        relatedEventIds,
                        relatedIssueIds);
                    expanded = true;
                }

                foreach (AgentIssue issue in issues.Values
                             .OrderBy(static value => value.Timestamp)
                             .ThenBy(static value => value.Id, StringComparer.Ordinal))
                {
                    if (selectedIssueIdSet.Contains(issue.Id) ||
                        correlatedIssues.ContainsKey(issue.Id) ||
                        correlatedIssues.Count >= options.MaximumBundleCorrelatedIssues ||
                        !identities.Contains((issue.RunId, issue.AgentId, issue.ModId)) ||
                        !MatchesDiagnosticIssue(
                            issue,
                            includedEventIds,
                            relatedIssueIds,
                            operationKeys,
                            traceIds,
                            spanIds))
                    {
                        continue;
                    }

                    correlatedIssues[issue.Id] = issue;
                    relatedIssueIds.Add(issue.Id);
                    foreach (string eventId in issue.EventIds)
                    {
                        relatedEventIds.Add(eventId);
                        if (!eventsById.ContainsKey(eventId))
                        {
                            missingEventIds.Add(eventId);
                        }
                    }
                    if (issue.ResolutionEventId is not null)
                    {
                        relatedEventIds.Add(issue.ResolutionEventId);
                        if (!eventsById.ContainsKey(issue.ResolutionEventId))
                        {
                            missingEventIds.Add(issue.ResolutionEventId);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(issue.OperationKey))
                    {
                        operationKeys.Add(issue.OperationKey);
                    }
                    if (!string.IsNullOrWhiteSpace(issue.TraceId))
                    {
                        traceIds.Add(issue.TraceId);
                    }
                    foreach (string spanId in issue.SpanIds ?? [])
                    {
                        spanIds.Add(spanId);
                    }
                    expanded = true;
                }
            }
            while (expanded);

            AgentEvent[] supportingEvents = events
                .Where(eventRecord => includedEventIds.Contains(eventRecord.Id))
                .OrderBy(static eventRecord => eventRecord.Sequence)
                .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
                .Take(options.MaximumBundleSupportingEvents)
                .ToArray();
            AgentDiagnosticMod[] mods = agents.Values
                .Where(agent => identities.Contains((agent.RunId, agent.AgentId, agent.ModId)))
                .Select(agent => new AgentDiagnosticMod(
                    agent.ModId,
                    agent.ModName,
                    agent.AgentId,
                    agent.RunId,
                    agent.LogicalAgentId))
                .OrderBy(static value => value.ModId, StringComparer.Ordinal)
                .ThenBy(static value => value.AgentId, StringComparer.Ordinal)
                .ToArray();

            var toolCalls = new HashSet<string>(StringComparer.Ordinal);
            var commands = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            var commandEvidence = new List<AgentDiagnosticCommandEvidence>();
            var buildEvidence = new List<AgentDiagnosticBuildEvidence>();
            var toolOperations = new List<AgentDiagnosticToolOperationEvidence>();
            foreach (AgentEvent eventRecord in supportingEvents)
            {
                AddDataString(eventRecord.Data, toolCalls, "toolCallId", "toolName");
                AddDataString(eventRecord.Data, commands, "command");
                AddDataString(eventRecord.Data, commands, "commandText");
                AddDataString(eventRecord.Data, files, "filePath", "path");
                AddDataStrings(eventRecord.Data, toolCalls, "relatedToolCalls");
                AddDataStrings(eventRecord.Data, commands, "relatedCommands");
                AddDataStrings(eventRecord.Data, files, "relatedFiles");

                AgentDiagnosticCommandEvidence? command =
                    ToCommandEvidence(eventRecord);
                if (command is not null)
                {
                    commandEvidence.Add(command);
                    if (!string.IsNullOrWhiteSpace(command.Command))
                    {
                        commands.Add(command.Command);
                    }
                }

                AgentDiagnosticBuildEvidence? build =
                    ToBuildEvidence(eventRecord);
                if (build is not null)
                {
                    buildEvidence.Add(build);
                }

                AgentDiagnosticToolOperationEvidence? tool =
                    ToToolOperationEvidence(eventRecord);
                if (tool is not null)
                {
                    toolOperations.Add(tool);
                }
            }

            commandEvidence = commandEvidence
                .GroupBy(static value => value.EventId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(options.MaximumBundleEvidenceValues)
                .ToList();
            buildEvidence = buildEvidence
                .GroupBy(static value => value.EventId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(options.MaximumBundleEvidenceValues)
                .ToList();
            toolOperations = toolOperations
                .GroupBy(static value => value.EventId, StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(options.MaximumBundleEvidenceValues)
                .ToList();

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

            AgentDiagnosticCorrelation[] correlations =
                BuildCorrelations(supportingEvents)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray();
            AgentDiagnosticRepositoryState? repository =
                BuildRepositoryState(supportingEvents, buildEvidence);
            AgentDiagnosticEnvironmentState? environment =
                BuildEnvironmentState(supportingEvents);
            AgentDiagnosticCompleteness completeness = BuildCompleteness(
                requestedIds,
                selectedIssues,
                correlatedIssues.Values,
                missingEventIds,
                supportingEvents,
                commandEvidence,
                buildEvidence);
            AgentIssue[] correlated = correlatedIssues.Values
                .OrderBy(static issue => issue.Timestamp)
                .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                .ToArray();

            return new AgentDiagnosticBundle
            {
                IssueIds = selectedIssues.Select(static issue => issue.Id).ToArray(),
                SelectedIssueIds = requestedIds,
                SelectedIssues = selectedIssues,
                CorrelatedIssueIds = correlated.Select(static issue => issue.Id).ToArray(),
                CorrelatedIssues = correlated,
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
                CommandEvidence = commandEvidence,
                BuildEvidence = buildEvidence,
                ToolOperations = toolOperations,
                Files = files
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray(),
                RelatedFiles = files
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .Take(options.MaximumBundleEvidenceValues)
                    .ToArray(),
                RecoveryPath = recoveryPath,
                Traces = traces.Take(options.MaximumBundleEvidenceValues).ToArray(),
                Correlations = correlations,
                Repository = repository,
                Environment = environment,
                Completeness = completeness
            };
        }
    }

    private static void AddCorrelationValues(
        AgentEvent eventRecord,
        ISet<string> operationKeys,
        ISet<string> traceIds,
        ISet<string> spanIds,
        ISet<string> transactionIds,
        ISet<string> workflowIds,
        ISet<string> relatedEventIds,
        ISet<string> relatedIssueIds)
    {
        string? operationKey = EventOperationKey(eventRecord);
        if (!string.IsNullOrWhiteSpace(operationKey))
        {
            operationKeys.Add(operationKey);
        }

        string? traceId = eventRecord.TraceId ??
            AgentObservabilityData.GetString(eventRecord.Data, "traceId");
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            traceIds.Add(traceId);
        }

        if (!string.IsNullOrWhiteSpace(eventRecord.SpanId))
        {
            spanIds.Add(eventRecord.SpanId);
        }
        AddDataString(eventRecord.Data, spanIds, "spanId", "parentSpanId");
        AddDataString(eventRecord.Data, transactionIds, "transactionId", "transaction");
        AddDataString(eventRecord.Data, workflowIds, "workflowId", "workflow");
        AddDataStrings(
            eventRecord.Data,
            relatedEventIds,
            "relatedEventIds");
        AddDataString(
            eventRecord.Data,
            relatedEventIds,
            "parentEventId",
            "causeEventId",
            "causedByEventId");
        AddDataStrings(
            eventRecord.Data,
            relatedIssueIds,
            "relatedIssueIds");
    }

    private static bool MatchesDiagnosticCorrelation(
        AgentEvent eventRecord,
        IReadOnlySet<string> includedEventIds,
        IReadOnlySet<string> relatedEventIds,
        IReadOnlySet<string> operationKeys,
        IReadOnlySet<string> traceIds,
        IReadOnlySet<string> spanIds,
        IReadOnlySet<string> transactionIds,
        IReadOnlySet<string> workflowIds,
        IReadOnlySet<string> relatedIssueIds)
    {
        if (relatedEventIds.Contains(eventRecord.Id))
        {
            return true;
        }

        string? operationKey = EventOperationKey(eventRecord);
        if (!string.IsNullOrWhiteSpace(operationKey) && operationKeys.Contains(operationKey))
        {
            return true;
        }

        string? traceId = eventRecord.TraceId ??
            AgentObservabilityData.GetString(eventRecord.Data, "traceId");
        if (!string.IsNullOrWhiteSpace(traceId) && traceIds.Contains(traceId))
        {
            return true;
        }

        if ((!string.IsNullOrWhiteSpace(eventRecord.SpanId) &&
             spanIds.Contains(eventRecord.SpanId)) ||
            AgentObservabilityData.GetStrings(eventRecord.Data, "spanIds")
                .Any(spanIds.Contains))
        {
            return true;
        }

        string? transactionId = FirstString(
            eventRecord.Data,
            "transactionId",
            "transaction");
        if (!string.IsNullOrWhiteSpace(transactionId) && transactionIds.Contains(transactionId))
        {
            return true;
        }

        string? workflowId = FirstString(
            eventRecord.Data,
            "workflowId",
            "workflow");
        if (!string.IsNullOrWhiteSpace(workflowId) && workflowIds.Contains(workflowId))
        {
            return true;
        }

        if (AgentObservabilityData.GetStrings(eventRecord.Data, "relatedEventIds")
                .Any(includedEventIds.Contains) ||
            AgentObservabilityData.GetStrings(eventRecord.Data, "relatedIssueIds")
                .Any(relatedIssueIds.Contains) ||
            new[] { "parentEventId", "causeEventId", "causedByEventId" }
                .Select(name => AgentObservabilityData.GetString(eventRecord.Data, name))
                .Any(value => value is not null && includedEventIds.Contains(value)))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesDiagnosticIssue(
        AgentIssue issue,
        IReadOnlySet<string> includedEventIds,
        IReadOnlySet<string> relatedIssueIds,
        IReadOnlySet<string> operationKeys,
        IReadOnlySet<string> traceIds,
        IReadOnlySet<string> spanIds) =>
        relatedIssueIds.Contains(issue.Id) ||
        issue.EventIds.Any(includedEventIds.Contains) ||
        (!string.IsNullOrWhiteSpace(issue.OperationKey) &&
         operationKeys.Contains(issue.OperationKey)) ||
        (!string.IsNullOrWhiteSpace(issue.TraceId) &&
         traceIds.Contains(issue.TraceId)) ||
        (issue.SpanIds ?? []).Any(spanIds.Contains);

    private static string? EventOperationKey(AgentEvent eventRecord) =>
        AgentObservabilityData.GetString(eventRecord.Data, "operationKey") ??
        AgentObservabilityData.GetString(eventRecord.Data, "operation");

    private AgentDiagnosticCommandEvidence? ToCommandEvidence(AgentEvent eventRecord)
    {
        JsonElement? data = eventRecord.Data;
        JsonElement? build = GetObject(data, "build");
        JsonElement? failure = GetObject(data, "failure");
        EvidenceValue stdout = ReadEvidenceValue(
            data,
            build,
            failure,
            ["stdoutEvidenceId"],
            ["stdout", "stdoutExcerpt"]);
        EvidenceValue stderr = ReadEvidenceValue(
            data,
            build,
            failure,
            ["stderrEvidenceId"],
            ["stderr", "stderrExcerpt", "errorOutput"]);
        EvidenceValue diagnostic = ReadEvidenceValue(
            data,
            build,
            failure,
            ["diagnosticEvidenceId", "outputEvidenceId", "buildOutputEvidenceId"],
            ["diagnosticOutput", "output", "error"]);
        string? command = FirstString(
            data,
            "command",
            "commandText") ?? FirstString(build, "command") ?? FirstString(failure, "command");
        string? tool = FirstString(data, "toolName", "tool") ?? FirstString(build, "tool");
        int? exitCode = ToInt32(FirstInt64(data, "exitCode")) ??
            ToInt32(FirstInt64(build, "exitCode")) ??
            ToInt32(FirstInt64(failure, "exitCode"));
        string? operationKey = EventOperationKey(eventRecord);
        bool isCommandEvent = eventRecord.Type.Contains("command", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.StartsWith("build", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.StartsWith("test", StringComparison.OrdinalIgnoreCase);
        if (!isCommandEvent && command is null && tool is null && exitCode is null &&
            stdout.Text is null && stderr.Text is null && diagnostic.Text is null)
        {
            return null;
        }

        return new AgentDiagnosticCommandEvidence(
            eventRecord.Id,
            string.IsNullOrWhiteSpace(command)
                ? null
                : AgentObservabilityData.SanitizeCommand(command, options.MaximumBundleOutputCharacters),
            AgentObservabilityData.BoundIdentifier(tool, 256),
            FirstString(
                data,
                "workingDirectory",
                "workingContext",
                "cwd") ?? FirstString(build, "workingDirectory", "workingContext", "cwd"),
            exitCode,
            FirstBoolean(data, build, failure, "timedOut") ??
                eventRecord.Type.EndsWith("timeout", StringComparison.OrdinalIgnoreCase),
            FirstBoolean(data, build, failure, "cancelled") ??
                string.Equals(
                    AgentObservabilityData.GetString(data, "outcome"),
                    "cancelled",
                    StringComparison.OrdinalIgnoreCase),
            stdout.Text,
            stderr.Text,
            diagnostic.Text,
            stdout.Truncated || AgentObservabilityData.GetBoolean(data, "stdoutTruncated"),
            stderr.Truncated || AgentObservabilityData.GetBoolean(data, "stderrTruncated"),
            diagnostic.Truncated || AgentObservabilityData.GetBoolean(data, "diagnosticOutputTruncated"),
            string.IsNullOrWhiteSpace(operationKey)
                ? null
                : AgentObservabilityData.SanitizeCommand(
                    operationKey,
                    options.MaximumBundleOutputCharacters),
            FirstString(data, "transactionId", "transaction") ??
                FirstString(build, "transactionId", "transaction"),
            FirstString(data, "workflowId", "workflow") ??
                FirstString(build, "workflowId", "workflow"));
    }

    private AgentDiagnosticBuildEvidence? ToBuildEvidence(AgentEvent eventRecord)
    {
        JsonElement? data = eventRecord.Data;
        JsonElement? build = GetObject(data, "build");
        JsonElement? failure = GetObject(data, "failure");
        bool isBuildEvent = eventRecord.Type.StartsWith("build", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type == AgentEventTypes.BuildDiagnostics ||
            build is not null ||
            FirstString(data, "sourceProject", "builtSha256", "stagingPath") is not null;
        if (!isBuildEvent)
        {
            return null;
        }

        EvidenceValue output = ReadEvidenceValue(
            build,
            data,
            failure,
            ["outputEvidenceId", "buildOutputEvidenceId", "stdoutEvidenceId"],
            ["output", "stdout", "stdoutExcerpt"]);
        EvidenceValue errorOutput = ReadEvidenceValue(
            build,
            data,
            failure,
            ["errorOutputEvidenceId", "stderrEvidenceId"],
            ["errorOutput", "stderr", "stderrExcerpt"]);
        EvidenceValue diagnostic = ReadEvidenceValue(
            build,
            data,
            failure,
            ["diagnosticEvidenceId", "outputEvidenceId"],
            ["causalDiagnostic", "diagnosticOutput", "output", "error"]);
        EvidenceValue causalDiagnostic = ReadEvidenceValue(
            build,
            data,
            failure,
            ["causalDiagnosticEvidenceId"],
            ["causalDiagnostic"]);
        bool? freshnessProven = FirstBoolean(data, build, failure, "loadedArtifactFreshnessProven");
        string? deploymentDecision = FirstString(
            data,
            "deploymentDecision") ?? FirstString(build, "deploymentDecision");
        string? command = FirstString(data, "command", "commandText") ??
            FirstString(build, "command") ??
            FirstString(failure, "command");
        string? orchestrator = FirstString(data, "orchestrator") ??
            FirstString(build, "orchestrator") ??
            FirstString(failure, "orchestrator");
        string? failureSurface = FirstString(data, "failureSurface") ??
            FirstString(build, "failureSurface") ??
            FirstString(failure, "failureSurface");
        string? causalOwner = FirstString(data, "likelyOwner", "causalOwner") ??
            FirstString(build, "likelyOwner", "causalOwner") ??
            FirstString(failure, "likelyOwner", "causalOwner");
        string? ownershipConfidence = FirstString(data, "ownershipConfidence") ??
            FirstString(build, "ownershipConfidence") ??
            FirstString(failure, "ownershipConfidence");
        string? ownershipBasis = FirstString(data, "ownershipBasis") ??
            FirstString(build, "ownershipBasis") ??
            FirstString(failure, "ownershipBasis");
        return new AgentDiagnosticBuildEvidence(
            eventRecord.Id,
            FirstString(data, "project") ?? FirstString(build, "project"),
            FirstString(data, "sourceProject") ?? FirstString(build, "sourceProject"),
            FirstString(data, "configuration") ?? FirstString(build, "configuration"),
            string.IsNullOrWhiteSpace(command)
                ? null
                : AgentObservabilityData.SanitizeCommand(
                    command,
                    options.MaximumBundleOutputCharacters),
            FirstString(data, "workingDirectory", "workingContext", "cwd") ??
                FirstString(build, "workingDirectory", "workingContext", "cwd"),
            FirstInt32(data, build, failure, "exitCode"),
            FirstBoolean(data, build, failure, "timedOut") ??
                eventRecord.Type.EndsWith("timeout", StringComparison.OrdinalIgnoreCase),
            FirstBoolean(data, build, failure, "cancelled") ?? false,
            output.Text,
            errorOutput.Text,
            diagnostic.Text,
            output.Truncated || AgentObservabilityData.GetBoolean(data, "outputTruncated"),
            errorOutput.Truncated || AgentObservabilityData.GetBoolean(data, "stderrTruncated"),
            diagnostic.Truncated || AgentObservabilityData.GetBoolean(data, "diagnosticOutputTruncated"),
            FirstString(data, "transactionId", "transaction") ??
                FirstString(build, "transactionId", "transaction") ??
                FirstString(failure, "transactionId", "transaction"),
            FirstString(data, "workflowId", "workflow") ??
                FirstString(build, "workflowId", "workflow") ??
                FirstString(failure, "workflowId", "workflow"),
            FirstString(data, "sourceFingerprint") ?? FirstString(build, "sourceFingerprint"),
            FirstString(data, "builtSha256", "builtArtifactSha256") ??
                FirstString(build, "builtSha256", "builtArtifactSha256"),
            FirstString(data, "deployedSha256", "deployedArtifactSha256") ??
                FirstString(build, "deployedSha256", "deployedArtifactSha256"),
            deploymentDecision,
            FirstString(data, "stagingPath") ?? FirstString(build, "stagingPath"),
            freshnessProven,
            FirstString(data, "freshnessState", "deploymentState") ??
                deploymentDecision,
            FirstString(data, "errorCode") ?? FirstString(failure, "errorCode"),
            FirstString(data, "failureMessage", "message") ??
                FirstString(failure, "message", "error"),
            causalDiagnostic.Text,
            causalDiagnostic.Truncated ||
                AgentObservabilityData.GetBoolean(data, "causalDiagnosticTruncated"),
            FirstString(data, "diagnosticSignature") ??
                FirstString(build, "diagnosticSignature"),
            orchestrator,
            failureSurface,
            causalOwner,
            ownershipConfidence,
            ownershipBasis,
            AgentObservabilityData.GetString(data, "rawStdoutEvidenceId"),
            AgentObservabilityData.GetString(data, "rawStderrEvidenceId"),
            GetObject(data, "buildDiscrimination"));
    }

    private static AgentDiagnosticToolOperationEvidence? ToToolOperationEvidence(
        AgentEvent eventRecord)
    {
        JsonElement? data = eventRecord.Data;
        string? tool = AgentObservabilityData.GetString(data, "toolName") ??
            AgentObservabilityData.GetString(data, "tool");
        bool isToolEvent = eventRecord.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
            tool is not null;
        if (!isToolEvent)
        {
            return null;
        }

        return new AgentDiagnosticToolOperationEvidence(
            eventRecord.Id,
            AgentObservabilityData.BoundIdentifier(tool, 256),
            string.IsNullOrWhiteSpace(EventOperationKey(eventRecord))
                ? null
                : AgentObservabilityData.SanitizeCommand(
                    EventOperationKey(eventRecord),
                    2_048),
            AgentObservabilityData.GetString(data, "operationType"),
            AgentObservabilityData.GetString(data, "outcome"),
            AgentObservabilityData.GetString(data, "errorCode"),
            eventRecord.Summary,
            AgentObservabilityData.GetString(data, "transactionId"),
            AgentObservabilityData.GetString(data, "workflowId"));
    }

    private EvidenceValue ReadEvidenceValue(
        JsonElement? first,
        JsonElement? second,
        JsonElement? third,
        IReadOnlyList<string> evidenceNames,
        IReadOnlyList<string> valueNames)
    {
        foreach (JsonElement? source in new[] { first, second, third })
        {
            foreach (string evidenceName in evidenceNames)
            {
                string? evidenceId = AgentObservabilityData.GetString(source, evidenceName);
                if (string.IsNullOrWhiteSpace(evidenceId))
                {
                    continue;
                }

                AgentDiagnosticEvidence? stored = GetEvidence(evidenceId);
                return stored is null
                    ? new EvidenceValue(null, true)
                    : new EvidenceValue(
                        AgentObservabilityData.BoundText(
                            stored.Content,
                            options.MaximumBundleOutputCharacters),
                        stored.Truncated || stored.Content.Length > options.MaximumBundleOutputCharacters);
            }

            foreach (string valueName in valueNames)
            {
                string? value = AgentObservabilityData.GetString(source, valueName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return new EvidenceValue(
                        AgentObservabilityData.BoundText(
                            value,
                            options.MaximumBundleOutputCharacters),
                        value.Length > options.MaximumBundleOutputCharacters);
                }
            }
        }

        return new EvidenceValue(null, false);
    }

    private static AgentDiagnosticCorrelation[] BuildCorrelations(
        IReadOnlyList<AgentEvent> supportingEvents)
    {
        var values = new Dictionary<(string Kind, string Value), HashSet<string>>();
        void Add(string kind, string? value, string eventId)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string safeValue = kind == "operation"
                ? AgentObservabilityData.SanitizeCommand(value, 256)
                : AgentObservabilityData.BoundIdentifier(value, 256)!;
            (string Kind, string Value) key = (kind, safeValue);
            if (!values.TryGetValue(key, out HashSet<string>? ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                values[key] = ids;
            }
            ids.Add(eventId);
        }

        foreach (AgentEvent eventRecord in supportingEvents)
        {
            Add("run", eventRecord.RunId, eventRecord.Id);
            Add("agent", eventRecord.AgentId, eventRecord.Id);
            Add("mod", eventRecord.ModId, eventRecord.Id);
            Add("trace", eventRecord.TraceId, eventRecord.Id);
            Add("span", eventRecord.SpanId, eventRecord.Id);
            Add("operation", EventOperationKey(eventRecord), eventRecord.Id);
            Add("transaction", FirstString(eventRecord.Data, "transactionId", "transaction"), eventRecord.Id);
            Add("workflow", FirstString(eventRecord.Data, "workflowId", "workflow"), eventRecord.Id);
        }

        return values
            .OrderBy(static pair => pair.Key.Kind, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => new AgentDiagnosticCorrelation(
                pair.Key.Kind,
                pair.Key.Value,
                pair.Value.OrderBy(static value => value, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static AgentDiagnosticRepositoryState? BuildRepositoryState(
        IReadOnlyList<AgentEvent> supportingEvents,
        IReadOnlyList<AgentDiagnosticBuildEvidence> builds)
    {
        string? repositoryRoot = FirstEventString(supportingEvents, "repositoryRoot", "repoRoot");
        string? project = FirstEventString(supportingEvents, "project") ??
            builds.Select(static value => value.Project).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        string? sourceProject = FirstEventString(supportingEvents, "sourceProject") ??
            builds.Select(static value => value.SourceProject).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        string? configuration = FirstEventString(supportingEvents, "configuration") ??
            builds.Select(static value => value.Configuration).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        string? sourceFingerprint = FirstEventString(supportingEvents, "sourceFingerprint") ??
            builds.Select(static value => value.SourceFingerprint).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        string? branch = FirstEventString(supportingEvents, "branch", "gitBranch");
        string? commitSha = FirstEventString(supportingEvents, "commitSha", "commit", "headSha");
        var changedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentEvent eventRecord in supportingEvents)
        {
            AddDataStrings(eventRecord.Data, changedFiles, "changedFiles");
            AddDataStrings(eventRecord.Data, changedFiles, "changedPaths");
        }

        if (repositoryRoot is null && project is null && sourceProject is null &&
            configuration is null && sourceFingerprint is null && branch is null &&
            commitSha is null && changedFiles.Count == 0)
        {
            return null;
        }

        return new AgentDiagnosticRepositoryState(
            AgentObservabilityData.BoundText(repositoryRoot, 1024),
            AgentObservabilityData.BoundText(project, 512),
            AgentObservabilityData.BoundText(sourceProject, 1024),
            AgentObservabilityData.BoundText(configuration, 128),
            AgentObservabilityData.BoundIdentifier(sourceFingerprint, 256),
            AgentObservabilityData.BoundText(branch, 256),
            AgentObservabilityData.BoundIdentifier(commitSha, 256),
            changedFiles
                .Select(value => AgentObservabilityData.BoundText(value, 1024))
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Take(512)
                .ToArray());
    }

    private static AgentDiagnosticEnvironmentState? BuildEnvironmentState(
        IReadOnlyList<AgentEvent> supportingEvents)
    {
        string[] valueNames =
        [
            "osDescription",
            "runtimeVersion",
            "dotnetVersion",
            "rimWorldVersion",
            "powerShellVersion",
            "devBridgeVersion"
        ];
        string[] toolNames = ["toolVersion", "devBridgeVersion", "rimliaisonVersion"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AgentEvent eventRecord in supportingEvents)
        {
            foreach (string name in valueNames)
            {
                string? value = AgentObservabilityData.GetString(eventRecord.Data, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values[name] = AgentObservabilityData.BoundText(value, 512);
                }
            }

            foreach (string name in toolNames)
            {
                string? value = AgentObservabilityData.GetString(eventRecord.Data, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    toolVersions[name] = AgentObservabilityData.BoundText(value, 512);
                }
            }

            JsonElement? environment = GetObject(eventRecord.Data, "environment");
            if (environment is { ValueKind: JsonValueKind.Object } environmentObject)
            {
                foreach (JsonProperty property in environmentObject.EnumerateObject().Take(32))
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        !property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("key", StringComparison.OrdinalIgnoreCase))
                    {
                        values[property.Name] = AgentObservabilityData.BoundText(
                            property.Value.GetString(),
                            512);
                    }
                }
            }
        }

        return values.Count == 0 && toolVersions.Count == 0
            ? null
            : new AgentDiagnosticEnvironmentState(values, toolVersions);
    }

    private AgentDiagnosticCompleteness BuildCompleteness(
        IReadOnlyList<string> requestedIds,
        IReadOnlyList<AgentIssue> selectedIssues,
        IEnumerable<AgentIssue> correlatedIssues,
        IReadOnlySet<string> missingEventIds,
        IReadOnlyList<AgentEvent> supportingEvents,
        IReadOnlyList<AgentDiagnosticCommandEvidence> commandEvidence,
        IReadOnlyList<AgentDiagnosticBuildEvidence> buildEvidence)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (selectedIssues.Count == 0 || requestedIds.Any(id =>
                selectedIssues.All(issue => !string.Equals(issue.Id, id, StringComparison.Ordinal))))
        {
            missing.Add("selectedIssues");
        }

        if (missingEventIds.Count > 0)
        {
            missing.Add("supportingEvents");
        }

        bool buildFailure = supportingEvents.Any(eventRecord =>
                eventRecord.Type == AgentEventTypes.BuildFailed ||
                (eventRecord.Type == AgentEventTypes.BuildDiagnostics &&
                    int.TryParse(
                        AgentObservabilityData.GetString(eventRecord.Data, "exitCode"),
                        out int exitCode) &&
                    exitCode != 0)) ||
            buildEvidence.Any(value => value.ExitCode is not null and not 0);
        bool hasCommand = commandEvidence.Any(value => !string.IsNullOrWhiteSpace(value.Command)) ||
            buildEvidence.Any(value => !string.IsNullOrWhiteSpace(value.Command));
        bool hasMeaningfulBuildDiagnostics = buildEvidence.Any(value =>
            !string.IsNullOrWhiteSpace(value.Output) ||
            !string.IsNullOrWhiteSpace(value.ErrorOutput) ||
            !string.IsNullOrWhiteSpace(value.DiagnosticOutput) ||
            !string.IsNullOrWhiteSpace(value.CausalDiagnostic));
        bool hasCausalBuildDiagnostic = buildEvidence.Any(value =>
            !string.IsNullOrWhiteSpace(value.CausalDiagnostic) ||
            !string.IsNullOrWhiteSpace(value.DiagnosticSignature));
        bool causalBuildDiagnosticIsTruncated = buildEvidence.Any(value =>
            value.CausalDiagnosticTruncated ||
            (value.DiagnosticOutputTruncated &&
                string.IsNullOrWhiteSpace(value.CausalDiagnostic)));
        bool hasDurableRawBuildOutput = buildEvidence.Any(value =>
            !string.IsNullOrWhiteSpace(value.RawStdoutEvidenceId) ||
            !string.IsNullOrWhiteSpace(value.RawStderrEvidenceId));
        if (buildFailure)
        {
            if (!hasCommand)
            {
                missing.Add("commands");
            }
            if (buildEvidence.Count == 0)
            {
                missing.Add("build");
            }
            if (!hasMeaningfulBuildDiagnostics)
            {
                missing.Add("build.diagnostics");
            }
            if (!hasCausalBuildDiagnostic)
            {
                missing.Add("build.causalDiagnostic");
            }
            if (causalBuildDiagnosticIsTruncated)
            {
                missing.Add("build.causalDiagnostic");
            }
            if (buildEvidence.Any(value => value.OutputTruncated || value.ErrorOutputTruncated) &&
                !hasDurableRawBuildOutput)
            {
                missing.Add("build.rawOutput");
            }
        }

        bool commandFailure = commandEvidence.Any(value =>
            value.ExitCode is not null and not 0 || value.TimedOut || value.Cancelled);
        if (commandFailure && !hasCommand)
        {
            missing.Add("commands");
        }
        if (commandFailure && commandEvidence.All(value =>
                string.IsNullOrWhiteSpace(value.Stdout) &&
                string.IsNullOrWhiteSpace(value.Stderr) &&
                string.IsNullOrWhiteSpace(value.DiagnosticOutput)))
        {
            missing.Add("command.output");
        }

        if (correlatedIssues.Any() && supportingEvents.Count == 0)
        {
            missing.Add("correlatedEvidence");
        }

        string[] missingValues = missing
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        return new AgentDiagnosticCompleteness(
            missingValues.Length == 0
                ? AgentDiagnosticCompletenessStatuses.Complete
                : AgentDiagnosticCompletenessStatuses.Incomplete,
            missingValues);
    }

    private static string? FirstEventString(
        IReadOnlyList<AgentEvent> events,
        params string[] names) =>
        events.Select(eventRecord => FirstString(eventRecord.Data, names))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstString(JsonElement? data, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = AgentObservabilityData.GetString(data, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static long? FirstInt64(JsonElement? data, params string[] names)
    {
        foreach (string name in names)
        {
            long? value = AgentObservabilityData.GetInt64(data, name);
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    private static int? FirstInt32(
        JsonElement? first,
        JsonElement? second,
        JsonElement? third,
        string name)
    {
        long? value = FirstInt64(first, name) ??
            FirstInt64(second, name) ??
            FirstInt64(third, name);
        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value.Value
            : null;
    }

    private static int? ToInt32(long? value) =>
        value is >= int.MinValue and <= int.MaxValue
            ? (int)value.Value
            : null;

    private static bool? FirstBoolean(
        JsonElement? first,
        JsonElement? second,
        JsonElement? third,
        string name)
    {
        foreach (JsonElement? source in new[] { first, second, third })
        {
            bool? value = AgentObservabilityData.GetNullableBoolean(source, name);
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    private static JsonElement? GetObject(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value;
    }

    private readonly record struct EvidenceValue(string? Text, bool Truncated);

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
    public Task<AgentObservabilityHydrationResult> HydrateRecentAsync(
        int maximumEvents = 2_000,
        int maximumIssues = 500,
        int maximumAgents = 250,
        CancellationToken cancellationToken = default) =>
        HydratePersistedAsync(
            recentOnly: true,
            maximumEvents,
            maximumIssues,
            maximumAgents,
            cancellationToken);

    public Task<AgentObservabilityHydrationResult> HydrateHistoryAsync(
        CancellationToken cancellationToken = default) =>
        HydratePersistedAsync(
            recentOnly: false,
            maximumEvents: options.MaximumEvents,
            maximumIssues: options.MaximumIssues,
            maximumAgents: options.MaximumAgents,
            cancellationToken);

    private async Task<AgentObservabilityHydrationResult> HydratePersistedAsync(
        bool recentOnly,
        int maximumEvents,
        int maximumIssues,
        int maximumAgents,
        CancellationToken cancellationToken)
    {
        if (maximumEvents <= 0 || maximumIssues <= 0 || maximumAgents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        try
        {
            return await Task.Run(
                    () => HydratePersistedRecords(
                        recentOnly,
                        maximumEvents,
                        maximumIssues,
                        maximumAgents,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (recentOnly)
            {
                Interlocked.Exchange(ref initialHydrationPending, 0);
            }
        }
    }

    private AgentObservabilityHydrationResult HydratePersistedRecords(
        bool recentOnly,
        int maximumEvents,
        int maximumIssues,
        int maximumAgents,
        CancellationToken cancellationToken)
    {
        if (storageDirectory is null)
        {
            return new AgentObservabilityHydrationResult(true, false, 0, 0, 0);
        }

        lock (refreshGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistedReadResult persistedEvents = ReadPersistedRecords(
                eventsPath,
                EventRecordKind,
                recentOnly ? maximumEvents : null,
                cancellationToken);
            PersistedReadResult persistedIssues = ReadPersistedRecords(
                issuesPath,
                IssueRecordKind,
                recentOnly ? maximumIssues : null,
                cancellationToken);
            PersistedReadResult persistedAgents = ReadPersistedRecords(
                agentsPath,
                AgentRecordKind,
                recentOnly ? maximumAgents : null,
                cancellationToken);

            List<AgentObservabilityNotification> notifications = [];
            bool loadDegraded = false;
            lock (gate)
            {
                ThrowIfDisposed();
                Dictionary<string, AgentEvent> previousEvents = events
                    .ToDictionary(static value => value.Id, StringComparer.Ordinal);
                Dictionary<string, AgentIssue> previousIssues = new(issues, StringComparer.Ordinal);
                Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot> previousAgents =
                    new(agents);

                if (!recentOnly)
                {
                    if (CanReplaceHydratedState(persistedEvents))
                    {
                        events.Clear();
                    }

                    if (CanReplaceHydratedState(persistedIssues))
                    {
                        issues.Clear();
                    }

                    if (CanReplaceHydratedState(persistedAgents))
                    {
                        agents.Clear();
                    }
                }

                LoadAgents(persistedAgents.Records);
                loadDegraded |= LoadEvents(persistedEvents.Records);
                loadDegraded |= LoadIssues(persistedIssues.Records);
                ReconcileLifecycleStateLocked();
                nextSequence = events.Count == 0
                    ? 0
                    : events.Max(static value => value.Sequence);
                lastTimestamp = events.Count == 0
                    ? 0
                    : events.Max(static value => value.Timestamp);
                TrimEvents();
                TrimIssues();
                TrimAgents();
                TrimEvidence();
                AddChangedNotificationsLocked(
                    previousEvents,
                    previousIssues,
                    previousAgents,
                    notifications);
            }

            foreach (AgentObservabilityNotification notification in notifications)
            {
                Notify(notification);
            }

            bool degraded = loadDegraded ||
                persistedEvents.Degraded ||
                persistedIssues.Degraded ||
                persistedAgents.Degraded;
            lock (gate)
            {
                historyDegraded |= degraded;
                if (recentOnly)
                {
                    historyComplete = false;
                }
            }
            string? message = degraded
                ? "Some historical observability records were skipped or unavailable."
                : null;
            return new AgentObservabilityHydrationResult(
                true,
                degraded,
                persistedEvents.Records.Count,
                persistedIssues.Records.Count,
                persistedAgents.Records.Count,
                message);
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
            TrimEvidence();
        }

        storageWatcher?.Dispose();
        CompactPersistedFiles();
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
            if (ReloadPersistedRecordsLocked())
            {
                TrimEvidence();
                MigratePersistedIdentityStateLocked();
            }
            else
            {
                historyDegraded = true;
            }
        }
    }

    private void MigratePersistedIdentityStateLocked()
    {
        if (!identityMigrationPending || storageDirectory is null)
        {
            return;
        }

        try
        {
            RewritePersistedStateFile(
                eventsPath,
                EventRecordKind,
                events.OrderBy(static value => value.Sequence)
                    .ThenBy(static value => value.Id, StringComparer.Ordinal));
            RewritePersistedStateFile(
                issuesPath,
                IssueRecordKind,
                issues.Values.OrderBy(static value => value.Timestamp)
                    .ThenBy(static value => value.Id, StringComparer.Ordinal));
            RewritePersistedStateFile(
                agentsPath,
                AgentRecordKind,
                agents.Values.OrderBy(static value => value.StartTime)
                    .ThenBy(static value => value.AgentId, StringComparer.Ordinal));
            identityMigrationPending = false;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // A locked or read-only legacy store remains usable in memory.
            // The next process start retries the idempotent migration.
        }
    }

    private void RewritePersistedStateFile<T>(
        string? path,
        string kind,
        IEnumerable<T> values)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        using FileStream lockStream = AcquireFileLock(path + ".lock");
        Rewrite(path, kind, values, enforceMaximumBytes: false);
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
        IReadOnlyList<JsonElement>? persistedCampaigns = ReadRecords(
            reliabilityCampaignsPath,
            ReliabilityCampaignRecordKind);
        if (persistedEvents is null || persistedIssues is null || persistedAgents is null ||
            persistedCampaigns is null)
        {
            return false;
        }

        events.Clear();
        issues.Clear();
        agents.Clear();
        reliabilityCampaigns.Clear();
        LoadAgents(persistedAgents);
        LoadEvents(persistedEvents);
        LoadIssues(persistedIssues);
        LoadReliabilityCampaigns(persistedCampaigns);
        ReassociatePersistedToolingTargetsLocked();
        ReconcileLifecycleStateLocked();
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
    private PersistedReadResult ReadPersistedRecords(
        string? path,
        string expectedKind,
        int? maximumRecords,
        CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return new PersistedReadResult([], false, true);
        }

        FileStream? lockStream = null;
        try
        {
            lockStream = TryAcquireFileLock(path + ".lock");
            if (lockStream is null)
            {
                return new PersistedReadResult([], true, false);
            }

            return ReadPersistedRecordsUnlocked(
                path,
                expectedKind,
                maximumRecords,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return new PersistedReadResult([], true, false);
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    private PersistedReadResult ReadPersistedRecordsUnlocked(
        string path,
        string expectedKind,
        int? maximumRecords,
        CancellationToken cancellationToken)
    {
        const long maximumInitialReadBytes = 4L * 1024 * 1024;
        var records = new List<JsonElement>();
        bool degraded = false;

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = maximumRecords is null
            ? 0
            : Math.Max(0, stream.Length - maximumInitialReadBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, false),
            detectEncodingFromByteOrderMarks: true);
        if (start > 0)
        {
            _ = ReadBoundedLine(
                reader,
                options.MaximumPersistedBytes,
                cancellationToken,
                out _);
        }
        string? line;
        while ((line = ReadBoundedLine(
                   reader,
                   options.MaximumPersistedBytes,
                   cancellationToken,
                   out bool oversized)) is not null)
        {
            if (oversized)
            {
                degraded = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
                    degraded = true;
                    continue;
                }

                records.Add(value.Clone());
                if (maximumRecords is int limit && records.Count > limit)
                {
                    records.RemoveAt(0);
                }
            }
            catch (JsonException)
            {
                degraded = true;
            }
        }

        return new PersistedReadResult(records, degraded, true);
    }

    private static string? ReadBoundedLine(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken,
        out bool oversized)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4_096));
        oversized = false;
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                return builder.Length == 0 && !oversized
                    ? null
                    : builder.ToString();
            }

            cancellationToken.ThrowIfCancellationRequested();
            char value = (char)next;
            if (value is '\r' or '\n')
            {
                return oversized ? string.Empty : builder.ToString();
            }

            if (builder.Length < maximumCharacters)
            {
                builder.Append(value);
            }
            else
            {
                oversized = true;
            }
        }
    }
    private void AddChangedNotificationsLocked(
        IReadOnlyDictionary<string, AgentEvent> previousEvents,
        IReadOnlyDictionary<string, AgentIssue> previousIssues,
        IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> previousAgents,
        ICollection<AgentObservabilityNotification> notifications)
    {
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
    private static bool CanReplaceHydratedState(PersistedReadResult result) =>
        result.Available && result.Records.Count > 0;


    private bool LoadEvents(IReadOnlyList<JsonElement> records)
    {
        bool degraded = false;
        HashSet<string> existingIds = events
            .Select(static value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement value in records)
        {
            try
            {
                AgentEvent? valueRecord = value.Deserialize<AgentEvent>(
                    AgentObservabilityJson.Options);
                if (valueRecord is null ||
                    string.IsNullOrWhiteSpace(valueRecord.Id) ||
                    valueRecord.Sequence <= 0)
                {
                    degraded = true;
                    continue;
                }

                AgentEvent normalized = NormalizePersistedEvent(valueRecord);
                if (!RecordsEqual(valueRecord, normalized))
                {
                    identityMigrationPending = true;
                }

                if (existingIds.Add(valueRecord.Id))
                {
                    events.Add(normalized);
                }
                else
                {
                    identityMigrationPending = true;
                }
            }
            catch (JsonException)
            {
                degraded = true;
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
        return degraded;
    }

    private bool LoadIssues(IReadOnlyList<JsonElement> records)
    {
        bool degraded = false;
        foreach (JsonElement value in records)
        {
            try
            {
                AgentIssue? issue = value.Deserialize<AgentIssue>(AgentObservabilityJson.Options);
                if (issue is null || string.IsNullOrWhiteSpace(issue.Id))
                {
                    degraded = true;
                    continue;
                }

                AgentIssue normalized = NormalizePersistedIssue(issue);
                if (!RecordsEqual(issue, normalized) ||
                    issues.ContainsKey(issue.Id))
                {
                    identityMigrationPending = true;
                }

                issues[issue.Id] = normalized;
            }
            catch (JsonException)
            {
                degraded = true;
            }
        }
        return degraded;
    }

    private bool LoadAgents(IReadOnlyList<JsonElement> records)
    {
        bool degraded = false;
        foreach (JsonElement value in records)
        {
            try
            {
                AgentSnapshot? agent = value.Deserialize<AgentSnapshot>(
                    AgentObservabilityJson.Options);
                if (agent is null || string.IsNullOrWhiteSpace(agent.AgentId))
                {
                    degraded = true;
                    continue;
                }

                AgentSnapshot normalized = NormalizePersistedAgent(agent);
                AgentObservabilityAgentIdentity identity =
                    new(agent.RunId, agent.AgentId);
                if (!RecordsEqual(agent, normalized) || agents.ContainsKey(identity))
                {
                    identityMigrationPending = true;
                }

                agents[identity] = normalized;
            }
            catch (JsonException)
            {
                degraded = true;
            }
        }
        return degraded;
    }


    private void LoadReliabilityCampaigns(IReadOnlyList<JsonElement> records)
    {
        foreach (JsonElement value in records)
        {
            try
            {
                AgentReliabilityCampaignConfiguration? configuration =
                    value.Deserialize<AgentReliabilityCampaignConfiguration>(AgentObservabilityJson.Options);
                if (configuration is null || string.IsNullOrWhiteSpace(configuration.CampaignId))
                {
                    historyDegraded = true;
                    continue;
                }

                reliabilityCampaigns[configuration.CampaignId] = configuration;
            }
            catch (JsonException)
            {
                historyDegraded = true;
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
        return ReadPersistedRecordsUnlocked(
                path,
                expectedKind,
                maximumRecords: null,
                CancellationToken.None)
            .Records;
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

    private void PersistEvidenceFile(AgentDiagnosticEvidence value)
    {
        if (evidenceDirectory is null)
        {
            return;
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            string path = Path.Combine(evidenceDirectory, value.Id + ".json");
            temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string json = JsonSerializer.Serialize(value, AgentObservabilityJson.Options);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or JsonException)
        {
            // Evidence is an enhancement to observability. A read-only or
            // unavailable evidence root must never fail the command that was
            // being observed.
        }
        finally
        {
            if (temporaryPath is not null)
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
    }

    private AgentDiagnosticEvidence? ReadEvidenceFile(string evidenceId)
    {
        if (evidenceDirectory is null ||
            !evidenceId.StartsWith("evd-", StringComparison.Ordinal) ||
            evidenceId.Any(char.IsControl) ||
            evidenceId.Contains(Path.DirectorySeparatorChar) ||
            evidenceId.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(evidenceDirectory, evidenceId + ".json");
            if (!File.Exists(path) ||
                new FileInfo(path).Length > options.MaximumEvidenceBytes * 2L)
            {
                return null;
            }

            AgentDiagnosticEvidence? value = JsonSerializer.Deserialize<AgentDiagnosticEvidence>(
                File.ReadAllText(path),
                AgentObservabilityJson.Options);
            if (value is null ||
                !string.Equals(value.Id, evidenceId, StringComparison.Ordinal))
            {
                return null;
            }

            string content = AgentObservabilityData.BoundText(
                value.Content,
                options.MaximumEvidenceBytes);
            return value with
            {
                Kind = AgentObservabilityData.BoundIdentifier(value.Kind, 128) ?? "diagnostic",
                Content = content,
                Truncated = value.Truncated ||
                    Encoding.UTF8.GetByteCount(value.Content ?? string.Empty) >
                        options.MaximumEvidenceBytes
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or JsonException)
        {
            return null;
        }
    }

    private void TrimEvidence()
    {
        HashSet<string> protectedIds = issues.Values
            .Where(static issue => !issue.Recovered)
            .Select(static issue => issue.EvidenceReference)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToHashSet(StringComparer.Ordinal);

        if (evidence.Count > options.MaximumEvidenceEntries)
        {
            int removableCount = Math.Max(
                0,
                evidence.Count - options.MaximumEvidenceEntries);
            foreach (string id in evidence.Keys
                         .Where(id => !protectedIds.Contains(id))
                         .Take(removableCount)
                         .ToArray())
            {
                evidence.Remove(id);
            }
        }

        if (evidenceDirectory is null)
        {
            return;
        }

        try
        {
            FileInfo[] files = new DirectoryInfo(evidenceDirectory)
                .GetFiles("evd-*.json")
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
                .ToArray();
            HashSet<string> protectedFileNames = protectedIds
                .Select(static id => id + ".json")
                .ToHashSet(StringComparer.Ordinal);
            int unprotectedSlots = Math.Max(
                0,
                options.MaximumEvidenceEntries -
                    files.Count(file => protectedFileNames.Contains(file.Name)));
            foreach (FileInfo file in files)
            {

                if (protectedFileNames.Contains(file.Name))
                {
                    continue;
                }

                if (unprotectedSlots > 0)
                {
                    unprotectedSlots--;
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
        }
    }
    private void CompactPersistedFiles()
    {
        if (storageDirectory is null)
        {
            return;
        }

        CompactPersistedFile(eventsPath, EventRecordKind);
        CompactPersistedFile(issuesPath, IssueRecordKind);
        CompactPersistedFile(agentsPath, AgentRecordKind);
        CompactPersistedFile(reliabilityCampaignsPath, ReliabilityCampaignRecordKind);
    }

    private void CompactPersistedFile(string? path, string kind)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            using FileStream lockStream = AcquireFileLock(path + ".lock");
            CompactIfNeeded(path, kind, force: true);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private void CompactIfNeeded(
        string path,
        string kind,
        bool force = false)
    {
        try
        {
            long triggerBytes = force
                ? options.MaximumPersistedBytes
                : Math.Max(
                    options.MaximumPersistedBytes + 1L,
                    Math.Min(long.MaxValue / 2, options.MaximumPersistedBytes * 2L));
            if (new FileInfo(path).Length <= triggerBytes)
            {
                return;
            }

            switch (kind)
            {
                case EventRecordKind:
                    Rewrite(
                        path,
                        kind,
                        SelectEventsForCompaction(path));
                    break;
                case IssueRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedIssuesForCompaction(path)
                            .Values
                            .OrderByDescending(static issue => !issue.Recovered)
                            .ThenByDescending(static issue => issue.Timestamp)
                            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                            .Take(options.MaximumIssues)
                            .OrderBy(static issue => issue.Timestamp)
                            .ThenBy(static issue => issue.Id, StringComparer.Ordinal));
                    break;
                case AgentRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedAgentsForCompaction(path)
                            .Values
                            .OrderByDescending(static agent => agent.Status is not
                                (AgentStatus.Completed or AgentStatus.Failed))
                            .ThenByDescending(static agent => agent.StartTime)
                            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
                            .Take(options.MaximumAgents)
                            .OrderBy(static agent => agent.StartTime)
                            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal));
                    break;
                case ReliabilityCampaignRecordKind:
                    Rewrite(
                        path,
                        kind,
                        ReadPersistedCampaignsForCompaction(path)
                            .Values
                            .OrderBy(static campaign => campaign.CreatedAtUtc)
                            .Take(64));
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private IEnumerable<AgentEvent> SelectEventsForCompaction(string path)
    {
        Dictionary<string, AgentEvent> allEvents = ReadPersistedEventsForCompaction(path);
        Dictionary<string, AgentIssue> allIssues =
            issuesPath is string issuePath && File.Exists(issuePath)
                ? ReadPersistedIssuesForCompaction(issuePath)
                : new(issues, StringComparer.Ordinal);
        HashSet<string> requiredIds = allIssues.Values
            .Where(static issue => !issue.Recovered)
            .OrderByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .SelectMany(static issue => issue.EventIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .Take(options.MaximumEvents)
            .ToHashSet(StringComparer.Ordinal);

        AgentEvent[] required = allEvents.Values
            .Where(eventRecord => requiredIds.Contains(eventRecord.Id))
            .OrderBy(static eventRecord => eventRecord.Sequence)
            .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .ToArray();
        int remaining = Math.Max(0, options.MaximumEvents - required.Length);
        AgentEvent[] recent = allEvents.Values
            .Where(eventRecord => !requiredIds.Contains(eventRecord.Id))
            .OrderBy(static eventRecord => eventRecord.Sequence)
            .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .TakeLast(remaining)
            .ToArray();
        return required.Concat(recent);
    }

    private Dictionary<string, AgentReliabilityCampaignConfiguration>
        ReadPersistedCampaignsForCompaction(string path)
    {
        var result = new Dictionary<string, AgentReliabilityCampaignConfiguration>(
            StringComparer.Ordinal);
        foreach (JsonElement value in ReadRecordsUnlocked(path, ReliabilityCampaignRecordKind))
        {
            try
            {
                AgentReliabilityCampaignConfiguration? campaign =
                    value.Deserialize<AgentReliabilityCampaignConfiguration>(AgentObservabilityJson.Options);
                if (campaign is not null && !string.IsNullOrWhiteSpace(campaign.CampaignId))
                {
                    result[campaign.CampaignId] = campaign;
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach ((string id, AgentReliabilityCampaignConfiguration campaign) in reliabilityCampaigns)
        {
            result[id] = campaign;
        }

        return result;
    }

    private void Rewrite<T>(
        string path,
        string kind,
        IEnumerable<T> values,
        bool enforceMaximumBytes = true)
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
                if (enforceMaximumBytes)
                {
                    while (totalBytes > options.MaximumPersistedBytes && lines.Count > 1)
                    {
                        totalBytes -= lines.Dequeue().Bytes;
                    }
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
            File.Replace(
                temporaryPath,
                path,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
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
            historyComplete = false;
            events.RemoveRange(0, events.Count - options.MaximumEvents);
        }
    }

    private void TrimIssues()
    {
        if (issues.Count <= options.MaximumIssues)
        {
            return;
        }

        historyComplete = false;
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

        historyComplete = false;
        foreach (AgentObservabilityAgentIdentity identity in agents.Values
                     .OrderByDescending(static agent => agent.Status is AgentStatus.Completed or AgentStatus.Failed
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

    private static AgentSnapshot NormalizePersistedAgent(AgentSnapshot agent)
    {
        ObservabilityEntityIdentity identity =
            ObservabilityEntityIdentityResolver.ForPersisted(
                agent.EntityType,
                agent.CanonicalEntityId,
                agent.ModId,
                agent.ModName,
                agent.WorkloadKind,
                agent.QualificationProfile);
        return agent with
        {
            SchemaVersion = AgentObservabilitySchemas.Agent,
            ModId = NormalizeEntityModId(identity, agent.ModId),
            ModName = NormalizeEntityDisplayName(identity, agent.ModName),
            EntityType = identity.EntityType,
            CanonicalEntityId = identity.CanonicalEntityId,
            DisplayName = identity.DisplayName
        };
    }

    private AgentEvent NormalizePersistedEvent(AgentEvent eventRecord)
    {
        agents.TryGetValue(
            new AgentObservabilityAgentIdentity(eventRecord.RunId, eventRecord.AgentId),
            out AgentSnapshot? owner);
        ObservabilityEntityIdentity identity =
            ObservabilityEntityIdentityResolver.ForPersisted(
                owner?.EntityType ?? eventRecord.EntityType,
                owner?.CanonicalEntityId ?? eventRecord.CanonicalEntityId,
                eventRecord.ModId,
                eventRecord.DisplayName,
                owner?.WorkloadKind,
                owner?.QualificationProfile);
        return eventRecord with
        {
            SchemaVersion = AgentObservabilitySchemas.Event,
            ModId = NormalizeEntityModId(identity, eventRecord.ModId),
            EntityType = identity.EntityType,
            CanonicalEntityId = identity.CanonicalEntityId,
            DisplayName = identity.DisplayName,
            Summary = AgentObservabilityData.BoundText(eventRecord.Summary, 1024),
            Data = AgentObservabilityData.ToElement(
                eventRecord.Data,
                options.MaximumEventDataBytes)
        };
    }

    private AgentIssue NormalizePersistedIssue(AgentIssue issue)
    {
        agents.TryGetValue(
            new AgentObservabilityAgentIdentity(issue.RunId, issue.AgentId),
            out AgentSnapshot? owner);
        bool subjectAttributed =
            !string.IsNullOrWhiteSpace(issue.AffectedProject) ||
            !string.IsNullOrWhiteSpace(issue.ReportingModId) &&
                !string.Equals(issue.ReportingModId, issue.ModId, StringComparison.Ordinal);
        ObservabilityEntityIdentity identity =
            subjectAttributed
                ? ObservabilityEntityIdentityResolver.ForPersisted(
                    issue.EntityType,
                    issue.CanonicalEntityId,
                    issue.ModId,
                    issue.DisplayName)
                : ObservabilityEntityIdentityResolver.ForPersisted(
                    owner?.EntityType ?? issue.EntityType,
                    owner?.CanonicalEntityId ?? issue.CanonicalEntityId,
                    issue.ModId,
                    issue.DisplayName,
                    owner?.WorkloadKind,
                    owner?.QualificationProfile);
        return issue with
        {
            SchemaVersion = AgentObservabilitySchemas.Issue,
            ModId = NormalizeEntityModId(identity, issue.ModId),
            EntityType = identity.EntityType,
            CanonicalEntityId = identity.CanonicalEntityId,
            DisplayName = identity.DisplayName,
            Summary = AgentObservabilityData.BoundText(issue.Summary, 512),
            EventIds = issue.EventIds
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(options.MaximumIssueEventReferences)
                .ToArray(),
            RelatedFiles = NormalizeValues(issue.RelatedFiles),
            ReportingTool = AgentObservabilityData.BoundIdentifier(issue.ReportingTool, 128),
            ReportingModId = AgentObservabilityData.BoundIdentifier(issue.ReportingModId, 256),
            CausalComponent = AgentObservabilityData.BoundIdentifier(issue.CausalComponent, 128),
            AffectedProject = AgentObservabilityData.BoundIdentifier(issue.AffectedProject, 256),
            AffectedValidations = NormalizeValues(issue.AffectedValidations, 256),
            CausalIssueKey = AgentObservabilityData.BoundIdentifier(issue.CausalIssueKey, 256),
            RelatedToolCalls = NormalizeValues(issue.RelatedToolCalls),
            RelatedCommands = NormalizeCommands(issue.RelatedCommands),
            TraceId = AgentObservabilityData.BoundIdentifier(issue.TraceId, 128),
            SpanIds = NormalizeValues(issue.SpanIds, 128),
            OperationKey = AgentObservabilityData.BoundIdentifier(issue.OperationKey, 256),
            RetryCount = Math.Max(0, issue.RetryCount),
            Occurrences = Math.Max(1, issue.Occurrences)
        };
    }
    private void ReassociatePersistedToolingTargetsLocked()
    {
        // Historical tool records are migrated only when structured target
        // evidence agrees with an existing canonical mod entity.
        Dictionary<string, AgentSnapshot> knownMods = agents.Values
            .Where(static agent =>
                agent.EntityType == ObservabilityEntityTypes.Mod &&
                agent.CanonicalEntityId.StartsWith(
                    "mod:",
                    StringComparison.Ordinal))
            .GroupBy(static agent => agent.CanonicalEntityId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        if (knownMods.Count == 0)
        {
            return;
        }

        foreach ((AgentObservabilityAgentIdentity key, AgentSnapshot agent) in agents.ToArray())
        {
            if (agent.EntityType != ObservabilityEntityTypes.Tool ||
                agent.CanonicalEntityId != "tool:rimliaison" ||
                !TryGetPersistedProjectTarget(key, out string target))
            {
                continue;
            }

            ObservabilityEntityIdentity candidate =
                ObservabilityEntityIdentity.ForMod(target);
            if (!knownMods.TryGetValue(candidate.CanonicalEntityId, out AgentSnapshot? knownMod))
            {
                continue;
            }

            ObservabilityEntityIdentity identity = new(
                knownMod.EntityType,
                knownMod.CanonicalEntityId,
                knownMod.DisplayName);
            agents[key] = agent with
            {
                ModId = knownMod.ModId,
                ModName = knownMod.ModName,
                EntityType = identity.EntityType,
                CanonicalEntityId = identity.CanonicalEntityId,
                DisplayName = identity.DisplayName
            };
            for (int index = 0; index < events.Count; index++)
            {
                AgentEvent eventRecord = events[index];
                if (eventRecord.RunId == key.RunId && eventRecord.AgentId == key.AgentId)
                {
                    events[index] = eventRecord with
                    {
                        ModId = knownMod.ModId,
                        EntityType = identity.EntityType,
                        CanonicalEntityId = identity.CanonicalEntityId,
                        DisplayName = identity.DisplayName
                    };
                }
            }

            foreach ((string issueId, AgentIssue issue) in issues.ToArray())
            {
                if (issue.RunId == key.RunId && issue.AgentId == key.AgentId)
                {
                    issues[issueId] = issue with
                    {
                        ModId = knownMod.ModId,
                        EntityType = identity.EntityType,
                        CanonicalEntityId = identity.CanonicalEntityId,
                        DisplayName = identity.DisplayName
                    };
                }
            }

            identityMigrationPending = true;
        }
    }

    private bool TryGetPersistedProjectTarget(
        AgentObservabilityAgentIdentity key,
        out string target)
    {
        target = string.Empty;
        string? projectTarget = null;
        string? repositoryTarget = null;
        foreach (AgentEvent eventRecord in events)
        {
            if (eventRecord.RunId != key.RunId || eventRecord.AgentId != key.AgentId)
            {
                continue;
            }

            string? project = AgentObservabilityData.GetString(eventRecord.Data, "project");
            string? repository = AgentObservabilityData.GetString(
                eventRecord.Data,
                "repository");
            if (!string.IsNullOrWhiteSpace(project))
            {
                project = project.Trim();
                if (projectTarget is not null &&
                    !string.Equals(projectTarget, project, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                projectTarget = project;
            }

            if (!string.IsNullOrWhiteSpace(repository))
            {
                repository = repository.Trim();
                if (repositoryTarget is not null &&
                    !string.Equals(
                        repositoryTarget,
                        repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                repositoryTarget = repository;
            }
        }

        if (projectTarget is not null &&
            repositoryTarget is not null &&
            !string.Equals(projectTarget, repositoryTarget, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        target = projectTarget ?? repositoryTarget ?? string.Empty;
        return target.Length > 0;
    }

    private static string NormalizeEntityModId(
        ObservabilityEntityIdentity identity,
        string fallback)
    {
        if (identity.EntityType == ObservabilityEntityTypes.Tool)
        {
            return identity.DisplayName;
        }

        if (identity.EntityType is ObservabilityEntityTypes.Fixture or
            ObservabilityEntityTypes.Test)
        {
            int separator = identity.CanonicalEntityId.IndexOf(':');
            return separator >= 0
                ? identity.CanonicalEntityId[(separator + 1)..]
                : identity.CanonicalEntityId;
        }

        return NormalizeLegacyModId(fallback);
    }

    private static string NormalizeEntityDisplayName(
        ObservabilityEntityIdentity identity,
        string fallback) =>
        identity.EntityType is ObservabilityEntityTypes.Tool or
            ObservabilityEntityTypes.Fixture or
            ObservabilityEntityTypes.Test
            ? identity.DisplayName
            : fallback;

    private static string NormalizeLegacyModId(string modId) =>
        ObservabilityProjectIdentityResolver.TryNormalizeKnownTemporaryIdentity(
            modId,
            out string canonicalModId)
            ? canonicalModId
            : modId;

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

    private sealed record PersistedReadResult(
        IReadOnlyList<JsonElement> Records,
        bool Degraded,
        bool Available);

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
    private static void ValidateOptionalLogicalAgentId(string? logicalAgentId)
    {
        if (!string.IsNullOrWhiteSpace(logicalAgentId))
        {
            ValidateText(logicalAgentId, nameof(logicalAgentId), 256);
        }
    }

    private static bool SameIdentity(AgentSnapshot left, AgentSnapshot right) =>
        string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal) &&
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.ModId, right.ModId, StringComparison.Ordinal) &&
        string.Equals(left.EntityType, right.EntityType, StringComparison.Ordinal) &&
        string.Equals(left.CanonicalEntityId, right.CanonicalEntityId, StringComparison.Ordinal) &&
        string.Equals(
            AgentObservabilityLogicalIdentity.For(left),
            AgentObservabilityLogicalIdentity.For(right),
            StringComparison.Ordinal);
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
        if (maximum <= 0)
        {
            return string.Empty;
        }

        string text = RedactText(value?.Trim() ?? string.Empty);
        if (text.Length <= maximum)
        {
            return text;
        }

        const string suffix = "...[truncated]";
        return maximum <= suffix.Length
            ? suffix[..maximum]
            : text[..(maximum - suffix.Length)] + suffix;
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

    public static bool? GetNullableBoolean(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return value.GetBoolean();
    }

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
