using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Observability;

public static partial class AgentObservabilitySchemas
{
    public const string Ui = "rimliaison-agent-observability-ui/v1";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentObservabilityUiView
{
    All,
    Issues,
    Agent,
    Issue
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentObservabilityIssueMode
{
    Details,
    Assessment
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentObservabilityAgentDetailTab
{
    Event,
    Artifacts,
    BuildTestIssues
}

/// <summary>
/// Stable identity for one persisted observability session. RunId remains part
/// of the session key; logical-agent grouping is handled separately so
/// multiple sessions can share one top-level agent view.
/// </summary>
public readonly record struct AgentObservabilityAgentIdentity(
    string RunId,
    string AgentId)
{
    public string Key => RunId + "\u001f" + AgentId;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentObservabilityAgentNavigationStatus
{
    Working,
    NeedsAttention,
    Completed,
    Failed
}


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentObservabilityUiUpdateKind
{
    NavigationChanged,
    SelectionChanged,
    EventAppended,
    IssueChanged,
    AgentChanged,
    StreamStateChanged
}

public sealed record AgentObservabilityUiRoute(
    AgentObservabilityUiView View,
    string? AgentId = null,
    string? IssueId = null,
    string? FocusEventId = null,
    string? RunId = null);

public sealed record AgentObservabilityUiNavigationItem(
    string Key,
    string Kind,
    string Label,
    string FullLabel,
    bool Selected,
    string? AgentId = null,
    string? ModId = null,
    AgentStatus? Status = null,
    string? RunId = null,
    bool CanDismiss = false,
    AgentObservabilityAgentNavigationStatus NavigationStatus =
        AgentObservabilityAgentNavigationStatus.Completed,
    bool HasUnresolvedError = false);

public sealed record AgentObservabilityUiNavigationModel(
    AgentObservabilityUiView ActiveView,
    IReadOnlyList<AgentObservabilityUiNavigationItem> Items);

public sealed record AgentObservabilityActivityRow(
    AgentEvent Event,
    string ModName,
    AgentStatus? AgentStatus,
    bool HasIssue,
    IReadOnlyList<string> IssueIds)
{
    [JsonIgnore]
    public long Sequence => Event.Sequence;

    [JsonIgnore]
    public string Activity => Event.Summary;

    [JsonIgnore]
    public string EventId => Event.Id;
}

public sealed record AgentObservabilityActivityListItem(
    string EventId,
    AgentObservabilityActivityRow Row);

public sealed record AgentObservabilityActivityReconciliationPlan(
    IReadOnlyList<string> RemovedEventIds,
    IReadOnlyList<string> MovedEventIds,
    IReadOnlyList<string> UpdatedEventIds,
    IReadOnlyList<string> InsertedEventIds)
{
    public bool HasChanges =>
        RemovedEventIds.Count > 0 ||
        MovedEventIds.Count > 0 ||
        UpdatedEventIds.Count > 0 ||
        InsertedEventIds.Count > 0;
}

public sealed record AgentObservabilityAllView(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AgentObservabilityActivityRow> Activity,
    bool HasMoreActivity,
    long? LatestSequence,
    string? EmptyState = null);

public sealed record AgentObservabilityIssueRow(
    AgentIssue Issue,
    string ModName,
    AgentStatus? AgentStatus,
    bool Selected)
{
    public string State => Issue.Recovered ? "recovered" : "unresolved";

    public string StateLabel => Issue.Recovered ? "Recovered" : "Unresolved";

    public int SharedAgentCount { get; init; }

    public AgentObservabilitySharedToolingHint? SharedTooling { get; init; }
}

public sealed record AgentObservabilityIssuesView(
    IReadOnlyList<AgentObservabilityIssueRow> Issues,
    IReadOnlyList<string> SelectedIssueIds,
    int RecoveredCount,
    int UnresolvedCount,
    bool HasMoreIssues,
    string? EmptyState = null);

public sealed record AgentObservabilityStageProgress(
    DevelopmentStage Stage,
    string State,
    bool IsCurrent);

public sealed record AgentObservabilityBuildTestResult(
    string Kind,
    string Status,
    string EventId,
    long Timestamp,
    string Summary);

public sealed record AgentObservabilityOutputExcerpt(
    string EventId,
    string Kind,
    string Text);

public sealed record AgentObservabilitySessionSummary(
    string RunId,
    string AgentId,
    string ModId,
    string ModName,
    AgentStatus Status,
    AgentCompletionState CompletionState,
    long StartTime,
    long? CompletedAt,
    long? DurationMilliseconds,
    bool FailureState,
    string? FailureSummary,
    string? LogicalAgentId = null);


public sealed record AgentObservabilityEventDetail(
    AgentEvent Event,
    AgentSnapshot? Agent,
    IReadOnlyList<AgentEvent> RelatedEvents,
    string? OperationKey,
    string? Status,
    long? DurationMilliseconds,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Commands,
    IReadOnlyList<AgentObservabilityBuildTestResult> BuildResults,
    IReadOnlyList<AgentObservabilityBuildTestResult> TestResults,
    IReadOnlyList<AgentIssue> Issues,
    IReadOnlyList<string> RelatedIssueIds,
    IReadOnlyList<AgentObservabilityOutputExcerpt> Output);

public sealed record AgentObservabilityAgentView(
    AgentSnapshot Agent,
    long ElapsedMilliseconds,
    IReadOnlyList<AgentObservabilityStageProgress> StageProgress,
    IReadOnlyList<AgentObservabilityActivityRow> RecentActivity,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Commands,
    IReadOnlyList<AgentObservabilityBuildTestResult> BuildResults,
    IReadOnlyList<AgentObservabilityBuildTestResult> TestResults,
    IReadOnlyList<AgentIssue> Warnings,
    IReadOnlyList<AgentIssue> Errors,
    IReadOnlyList<AgentIssue> Issues,
    AgentObservabilitySessionSummary CurrentSession,
    IReadOnlyList<AgentObservabilitySessionSummary> PastSessions,
    string? EmptyState = null,
    string? SelectedEventId = null,
    AgentObservabilityEventDetail? SelectedEvent = null);

public sealed record AgentObservabilityIssueDetail(
    AgentIssue Issue,
    AgentSnapshot? Agent,
    IReadOnlyList<AgentEvent> SupportingEvents,
    IReadOnlyList<string> UnresolvedEventIds,
    IReadOnlyList<string> RelatedFiles,
    IReadOnlyList<string> RelatedTools,
    IReadOnlyList<string> RelatedCommands,
    IReadOnlyList<AgentObservabilityOutputExcerpt> Output,
    AgentEvent? ResolutionEvent,
    IReadOnlyList<AgentRecoveryStep> RecoveryPath,
    string RecoveryState,
    string? TraceId,
    IReadOnlyList<string> SpanIds,
    string? FocusEventId)
{
    public AgentObservabilityIssueTriage? Triage { get; init; }
}

public sealed record AgentObservabilityUiStreamStatus(
    bool Live,
    bool Delayed,
    long Revision,
    long? LatestSequence,
    string? Message = null);

public sealed record AgentObservabilityUiSelection(
    AgentObservabilityUiRoute View,
    string? SelectedIssueId,
    string? SelectedEventId,
    AgentObservabilityIssueMode IssueMode,
    AgentObservabilityAgentDetailTab AgentDetailTab,
    IReadOnlyList<string> SelectedIssueIds,
    AgentDiagnosticBundle? Assessment);

public sealed record AgentObservabilityUiSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = AgentObservabilitySchemas.Ui;

    [JsonPropertyName("defaultView")]
    public AgentObservabilityUiView DefaultView { get; init; } = AgentObservabilityUiView.All;

    [JsonPropertyName("view")]
    public AgentObservabilityUiView View { get; init; }

    [JsonPropertyName("navigation")]
    public required AgentObservabilityUiNavigationModel Navigation { get; init; }

    [JsonPropertyName("selectedIssueIds")]
    public IReadOnlyList<string> SelectedIssueIds { get; init; } = [];

    [JsonPropertyName("selectedIssueId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedIssueId { get; init; }

    [JsonPropertyName("selectedEventId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedEventId { get; init; }

    [JsonPropertyName("issueMode")]
    public AgentObservabilityIssueMode IssueMode { get; init; } = AgentObservabilityIssueMode.Details;

    [JsonPropertyName("agentDetailTab")]
    public AgentObservabilityAgentDetailTab AgentDetailTab { get; init; } = AgentObservabilityAgentDetailTab.Event;

    [JsonPropertyName("assessment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentDiagnosticBundle? Assessment { get; init; }

    [JsonPropertyName("selection")]
    public AgentObservabilityUiSelection Selection { get; init; } =
        new(
            new AgentObservabilityUiRoute(AgentObservabilityUiView.All),
            null,
            null,
            AgentObservabilityIssueMode.Details,
            AgentObservabilityAgentDetailTab.Event,
            [],
            null);

    [JsonPropertyName("stream")]
    public required AgentObservabilityUiStreamStatus Stream { get; init; }

    [JsonPropertyName("all")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentObservabilityAllView? All { get; init; }

    [JsonPropertyName("issues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentObservabilityIssuesView? Issues { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentObservabilityAgentView? Agent { get; init; }

    [JsonPropertyName("issue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentObservabilityIssueDetail? Issue { get; init; }

    [JsonPropertyName("emptyState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmptyState { get; init; }
}

public sealed record AgentObservabilityUiUpdate(
    long Revision,
    AgentObservabilityUiUpdateKind Kind,
    AgentObservabilityUiView View,
    AgentEvent? Event = null,
    AgentIssue? Issue = null,
    AgentSnapshot? Agent = null,
    bool PreserveActivityPosition = true,
    bool RequestScroll = false);

public sealed class AgentObservabilityUiOptions
{
    public int MaximumActivityRows { get; init; } = 1_000;
    public int MaximumIssueRows { get; init; } = 100;
    public int MaximumRecentActivityRows { get; init; } = 50;
    public int MaximumSupportingEvents { get; init; } = 1_000;
    public int MaximumIndexedEvents { get; init; } = 20_000;
    public int MaximumIndexedIssues { get; init; } = 5_000;
    public int MaximumIndexedAgents { get; init; } = 2_000;
    public int MaximumNavigationAgents { get; init; } = 100;

    internal void Validate()
    {
        if (MaximumActivityRows <= 0 || MaximumActivityRows > 50_000 ||
            MaximumIssueRows <= 0 || MaximumIssueRows > 10_000 ||
            MaximumRecentActivityRows <= 0 || MaximumRecentActivityRows > 5_000 ||
            MaximumSupportingEvents <= 0 || MaximumSupportingEvents > 50_000 ||
            MaximumIndexedEvents < MaximumActivityRows || MaximumIndexedEvents > 50_000 ||
            MaximumIndexedIssues < MaximumIssueRows || MaximumIndexedIssues > 10_000 ||
            MaximumIndexedAgents < MaximumNavigationAgents || MaximumIndexedAgents > 10_000 ||
            MaximumNavigationAgents <= 0 || MaximumNavigationAgents > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentObservabilityUiOptions));
        }
    }
}

/// <summary>
/// A local, bounded view/controller over the authoritative observability store.
/// It is intentionally UI-toolkit independent so the CLI, desktop surface, and
/// tests all consume the same All/Issues/agent/detail contract.
/// </summary>
public sealed class AgentObservabilityUi : IDisposable
{
    private sealed record NavigationAgentGroup(
        string Key,
        AgentSnapshot Representative,
        AgentObservabilityAgentNavigationStatus NavigationStatus,
        bool HasUnresolvedError);

    private static readonly DevelopmentStage[] LifecycleStages =
    [
        DevelopmentStage.Analysis,
        DevelopmentStage.Research,
        DevelopmentStage.Implementation,
        DevelopmentStage.Testing,
        DevelopmentStage.Packaging,
        DevelopmentStage.Complete
    ];

    private static readonly string[] OutputNames =
    [
        "stderr",
        "stdout",
        "errorOutput",
        "output",
        "error"
    ];

    private static readonly string[] ToolNames =
    [
        "toolName",
        "toolCallId"
    ];

    private readonly object gate = new();
    private readonly IAgentObservabilityStore store;
    private readonly AgentObservabilityUiOptions options;
    private readonly string? explicitRunId;
    private readonly Func<long> nowMilliseconds;
    private readonly Dictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents = [];
    private readonly List<AgentEvent> events = [];
    private readonly Dictionary<string, AgentEvent> eventsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> eventsByOperation = new(StringComparer.Ordinal);
    private readonly HashSet<string> hydratedOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentIssue> issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> issueIdsByEvent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentObservabilityIssueSignature> issueSignatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> issueIdsByFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentObservabilitySharedToolingHint?> sharedToolingHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentDiagnosticBundle> diagnosticBundles = new(StringComparer.Ordinal);
    private readonly List<Action<AgentObservabilityUiUpdate>> subscribers = [];
    private readonly IDisposable storeSubscription;
    private string? activeRunId;
    private AgentObservabilityUiRoute route = new(AgentObservabilityUiView.All);
    private HashSet<string> selectedIssueIds = new(StringComparer.Ordinal);
    private string? selectedIssueId;
    private string? selectedEventId;
    private AgentObservabilityIssueMode issueMode = AgentObservabilityIssueMode.Details;
    private AgentObservabilityAgentDetailTab agentDetailTab = AgentObservabilityAgentDetailTab.Event;
    private AgentDiagnosticBundle? assessment;
    private bool includeRecovered = true;
    private bool delayed;
    private string? streamMessage;
    private long revision;
    private long issueProjectionRevision;
    private long issueSelectionRevision;
    private long issueSignatureComputations;
    private long cachedIssueProjectionRevision = -1;
    private long cachedIssueSelectionRevision = -1;
    private bool cachedIncludeRecovered;
    private int cachedVisibleIssueLimit;
    private AgentObservabilityIssuesView? cachedIssuesView;
    private AgentObservabilityUiNavigationModel? cachedNavigation;
    private long cachedNavigationRevision = -1;
    private AgentObservabilityAllView? cachedAllView;
    private long cachedAllRevision = -1;
    private int visibleIssueLimit;
    private int disposed;
    public AgentObservabilityUi(
        IAgentObservabilityStore store,
        AgentObservabilityUiOptions? options = null,
        string? runId = null,
        Func<long>? nowMilliseconds = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? new AgentObservabilityUiOptions();
        this.options.Validate();
        explicitRunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();
        activeRunId = explicitRunId;
        this.nowMilliseconds = nowMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        visibleIssueLimit = this.options.MaximumIssueRows;

        AgentObservabilityView initial = store.Query(
            runId: explicitRunId,
            issuesOnly: false,
            limit: this.options.MaximumIndexedEvents);
        lock (gate)
        {
            foreach (AgentSnapshot agent in initial.Agents)
            {
                UpsertAgentLocked(agent);
            }

            foreach (AgentEvent eventRecord in initial.Events)
            {
                UpsertEventLocked(eventRecord);
            }

            foreach (AgentIssue issue in initial.Issues)
            {
                UpsertIssueLocked(issue);
            }
        }

        storeSubscription = store.Subscribe(OnStoreNotification);
    }

    public AgentObservabilityUiView CurrentView
    {
        get
        {
            lock (gate)
            {
                return route.View;
            }
        }
    }
    public long IssueSignatureComputations
    {
        get
        {
            lock (gate)
            {
                return issueSignatureComputations;
            }
        }
    }

    public string? ActiveRunId
    {
        get
        {
            lock (gate)
            {
                return activeRunId;
            }
        }
    }

    public AgentObservabilityUiSnapshot Snapshot
    {
        get
        {
            RefreshLiveStore();
            lock (gate)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public AgentObservabilityUiSnapshot ShowAll()
    {
        lock (gate)
        {
            selectedEventId = null;
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        return Navigate(new AgentObservabilityUiRoute(AgentObservabilityUiView.All));
    }

    public AgentObservabilityUiSnapshot ShowIssues(bool includeRecoveredIssues = true)
    {
        lock (gate)
        {
            includeRecovered = includeRecoveredIssues;
            if (route.View is not (AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue))
            {
                selectedEventId = null;
                assessment = null;
                issueMode = AgentObservabilityIssueMode.Details;
            }
        }

        return Navigate(new AgentObservabilityUiRoute(AgentObservabilityUiView.Issues));
    }
    public AgentObservabilityUiSnapshot LoadMoreIssues()
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
            if (route.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue)
            {
                visibleIssueLimit = Math.Min(
                    options.MaximumIndexedIssues,
                    visibleIssueLimit + options.MaximumIssueRows);
            }
        }

        return Snapshot;
    }

    public AgentObservabilityUiSnapshot ShowAgent(
        string agentIdOrModId,
        string? requestedRunId = null,
        string? focusEventId = null)
    {
        if (string.IsNullOrWhiteSpace(agentIdOrModId))
        {
            throw new ArgumentException("An agent or mod id is required.", nameof(agentIdOrModId));
        }

        AgentSnapshot? agent;
        lock (gate)
        {
            agent = FindAgentLocked(agentIdOrModId.Trim(), requestedRunId);

            bool changedAgent = route.View != AgentObservabilityUiView.Agent ||
                !string.Equals(route.AgentId, agent?.AgentId ?? agentIdOrModId.Trim(), StringComparison.Ordinal) ||
                !string.Equals(route.RunId, agent?.RunId ?? requestedRunId ?? activeRunId, StringComparison.Ordinal);
            if (changedAgent)
            {
                selectedEventId = null;
            }

            selectedEventId = ResolveFocusEventLocked(
                focusEventId,
                agent?.RunId,
                agent?.AgentId);
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        return Navigate(new AgentObservabilityUiRoute(
            AgentObservabilityUiView.Agent,
            AgentId: agent?.AgentId ?? agentIdOrModId.Trim(),
            RunId: agent?.RunId ?? requestedRunId ?? activeRunId));
    }

    public AgentObservabilityUiSnapshot ShowIssue(string issueId)
    {
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("An issue id is required.", nameof(issueId));
        }

        AgentIssue? issue;
        lock (gate)
        {
            issue = FindIssueLocked(issueId.Trim());
            selectedIssueId = issue?.Id ?? issueId.Trim();
            selectedEventId = issue?.EventIds.FirstOrDefault();
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        return Navigate(new AgentObservabilityUiRoute(
            AgentObservabilityUiView.Issue,
            AgentId: issue?.AgentId,
            IssueId: issue?.Id ?? issueId.Trim(),
            FocusEventId: issue?.EventIds.FirstOrDefault(),
            RunId: issue?.RunId ?? activeRunId));
    }

    /// <summary>
    /// Selects an issue without changing the primary view. Row selection and
    /// checkbox selection are intentionally independent: the former drives
    /// inspection, while the latter drives a multi-issue assessment bundle.
    /// </summary>
    public AgentObservabilityUiSnapshot SelectIssue(string issueId)
    {
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("An issue id is required.", nameof(issueId));
        }

        lock (gate)
        {
            ThrowIfDisposedLocked();
            AgentIssue? issue = FindIssueLocked(issueId.Trim());
            string? nextIssueId = issue?.Id ?? issueId.Trim();
            bool changed = !string.Equals(selectedIssueId, nextIssueId, StringComparison.Ordinal);
            selectedIssueId = nextIssueId;
            selectedEventId = issue?.EventIds.FirstOrDefault();
            if (changed)
            {
                assessment = null;
                issueMode = AgentObservabilityIssueMode.Details;
            }
        }

        return PublishSelectionChange();
    }

    public AgentObservabilityUiSnapshot ShowIssueDetails(string issueId) => ShowIssue(issueId);

    public AgentObservabilityUiSnapshot SelectEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("An event id is required.", nameof(eventId));
        }

        lock (gate)
        {
            ThrowIfDisposedLocked();
            AgentEvent? eventRecord = FindEventLocked(eventId.Trim());
            if (eventRecord is null)
            {
                throw new KeyNotFoundException("Unknown observability event id: " + eventId);
            }

            if (route.View == AgentObservabilityUiView.Agent &&
                (!string.Equals(route.AgentId, eventRecord.AgentId, StringComparison.Ordinal) ||
                 !string.Equals(route.RunId, eventRecord.RunId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The selected event does not belong to the visible agent.");
            }

            selectedEventId = eventRecord.Id;
        }

        return PublishSelectionChange();
    }

    public AgentObservabilityUiSnapshot SetAgentDetailTab(
        AgentObservabilityAgentDetailTab tab)
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
            agentDetailTab = tab;
        }

        return PublishSelectionChange();
    }


    public AgentObservabilityUiSnapshot Navigate(AgentObservabilityUiRoute requestedRoute)
    {
        ArgumentNullException.ThrowIfNull(requestedRoute);
        Action<AgentObservabilityUiUpdate>[] handlers;
        AgentObservabilityUiUpdate update;
        AgentObservabilityUiSnapshot snapshot;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            AgentObservabilityUiRoute previous = route;
            route = NormalizeRouteLocked(requestedRoute);
            ApplyRouteStateLocked(previous, route);
            revision++;
            snapshot = BuildSnapshotLocked();
            update = new AgentObservabilityUiUpdate(
                revision,
                AgentObservabilityUiUpdateKind.NavigationChanged,
                route.View,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
        return snapshot;
    }

    public IReadOnlyList<string> SelectIssues(IEnumerable<string> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        Action<AgentObservabilityUiUpdate>[] handlers;
        IReadOnlyList<string> selected;
        AgentObservabilityUiUpdate update;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? issueId in issueIds)
            {
                if (string.IsNullOrWhiteSpace(issueId))
                {
                    continue;
                }

                AgentIssue? issue = FindIssueLocked(issueId.Trim());
                if (issue is not null)
                {
                    accepted.Add(issue.Id);
                }
            }

            bool changed = !selectedIssueIds.SetEquals(accepted);
            selectedIssueIds = accepted;
            if (changed)
            {
                assessment = null;
                issueMode = AgentObservabilityIssueMode.Details;
                issueSelectionRevision++;
            }
            selected = selectedIssueIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            revision++;
            update = new AgentObservabilityUiUpdate(
                revision,
                AgentObservabilityUiUpdateKind.SelectionChanged,
                route.View,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
        return selected;
    }

    public IReadOnlyList<string> ClearIssueSelection() => SelectIssues([]);

    public AgentDiagnosticBundle PrepareAssessment()
    {
        string[] ids;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            ids = selectedIssueIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
        }

        if (ids.Length == 0)
        {
            throw new InvalidOperationException("Select at least one issue before preparing an assessment.");
        }

        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle(ids);
        lock (gate)
        {
            ThrowIfDisposedLocked();
            assessment = bundle;
            issueMode = AgentObservabilityIssueMode.Assessment;
        }
        PublishSelectionChange();
        return bundle;
    }

    public AgentDiagnosticBundle PrepareAssessment(IEnumerable<string> issueIds)
    {
        ArgumentNullException.ThrowIfNull(issueIds);
        string[] requested = issueIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> selected = SelectIssues(requested);
        if (selected.Count != requested.Length)
        {
            string[] missing = requested
                .Except(selected, StringComparer.Ordinal)
                .ToArray();
            throw new KeyNotFoundException(
                "Unknown observability issue id(s): " + string.Join(", ", missing));
        }

        return PrepareAssessment();
    }
    public string CreateChatPacket(string issueId)
    {
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("An issue id is required.", nameof(issueId));
        }

        lock (gate)
        {
            ThrowIfDisposedLocked();
            AgentObservabilityIssueDetail? detail = BuildIssueDetailLocked(issueId.Trim());
            if (detail?.Triage is null)
            {
                throw new KeyNotFoundException("Unknown observability issue id: " + issueId);
            }

            AgentDiagnosticBundle bundle = GetDiagnosticBundleLocked(detail.Issue.Id);
            return AgentObservabilityIssueTriageBuilder.FormatChatPacket(
                detail.Triage,
                detail.Issue,
                bundle);
        }
    }


    public void SetStreamDelayed(bool value, string? message = null)
    {
        Action<AgentObservabilityUiUpdate>[] handlers;
        AgentObservabilityUiUpdate update;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            delayed = value;
            streamMessage = AgentObservabilityData.BoundIdentifier(message, 256);
            revision++;
            update = new AgentObservabilityUiUpdate(
                revision,
                AgentObservabilityUiUpdateKind.StreamStateChanged,
                route.View,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
    }

    public IDisposable Subscribe(Action<AgentObservabilityUiUpdate> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            subscribers.Add(handler);
        }

        return new UiSubscription(this, handler);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        storeSubscription.Dispose();
        lock (gate)
        {
            subscribers.Clear();
            agents.Clear();
            events.Clear();
            eventsById.Clear();
            eventsByOperation.Clear();
            hydratedOperations.Clear();
            issues.Clear();
            issueIdsByEvent.Clear();
            issueSignatures.Clear();
            issueIdsByFingerprint.Clear();
            sharedToolingHints.Clear();
            diagnosticBundles.Clear();
            selectedIssueIds.Clear();
            cachedIssuesView = null;
            cachedNavigation = null;
            cachedAllView = null;
            selectedEventId = null;
            assessment = null;
            cachedIssuesView = null;
        }
    }

    private void RefreshLiveStore()
    {
        if (Volatile.Read(ref disposed) != 0 ||
            store is not IAgentObservabilityLiveStore liveStore)
        {
            return;
        }

        try
        {
            liveStore.Refresh();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            SetStreamDelayed(true, "The shared observability store could not be refreshed.");
        }
        catch (UnauthorizedAccessException)
        {
            SetStreamDelayed(true, "The shared observability store could not be refreshed.");
        }
    }

    private void OnStoreNotification(AgentObservabilityNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Action<AgentObservabilityUiUpdate>[] handlers;
        AgentObservabilityUiUpdate update;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            string? notificationRunId = notification.Agent?.RunId ??
                notification.Event?.RunId ??
                notification.Issue?.RunId;
            if (notificationRunId is not null && !MatchesRun(notificationRunId))
            {
                return;
            }

            if (notification.Agent is not null)
            {
                UpsertAgentLocked(notification.Agent);
            }

            if (notification.Event is not null)
            {
                UpsertEventLocked(notification.Event);
            }

            if (notification.Issue is not null)
            {
                UpsertIssueLocked(notification.Issue);
            }

            revision++;
            AgentObservabilityUiUpdateKind kind = notification.Kind switch
            {
                AgentObservabilityNotificationKind.EventAppended =>
                    AgentObservabilityUiUpdateKind.EventAppended,
                AgentObservabilityNotificationKind.IssueChanged =>
                    AgentObservabilityUiUpdateKind.IssueChanged,
                AgentObservabilityNotificationKind.AgentChanged =>
                    AgentObservabilityUiUpdateKind.AgentChanged,
                _ => AgentObservabilityUiUpdateKind.EventAppended
            };
            update = new AgentObservabilityUiUpdate(
                revision,
                kind,
                route.View,
                notification.Event,
                notification.Issue,
                notification.Agent,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
    }

    private AgentObservabilityUiSnapshot BuildSnapshotLocked()
    {
        AgentObservabilityUiSnapshot snapshot = new()
        {
            View = route.View,
            Navigation = BuildNavigationLocked(),
            SelectedIssueIds = selectedIssueIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            SelectedIssueId = selectedIssueId,
            SelectedEventId = selectedEventId,
            IssueMode = issueMode,
            AgentDetailTab = agentDetailTab,
            Assessment = assessment,
            Stream = new AgentObservabilityUiStreamStatus(
                Live: true,
                Delayed: delayed,
                Revision: revision,
                LatestSequence: events.Count == 0 ? null : events[^1].Sequence,
                Message: streamMessage),
            Selection = new AgentObservabilityUiSelection(
                route,
                selectedIssueId,
                selectedEventId,
                issueMode,
                agentDetailTab,
                selectedIssueIds
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray(),
                assessment)
        };

        switch (route.View)
        {
            case AgentObservabilityUiView.All:
                snapshot = snapshot with { All = BuildAllViewLocked() };
                break;
            case AgentObservabilityUiView.Issues:
                snapshot = snapshot with
                {
                    Issues = BuildIssuesViewLocked(),
                    Issue = BuildIssueDetailLocked(selectedIssueId)
                };
                break;
            case AgentObservabilityUiView.Agent:
                snapshot = snapshot with
                {
                    Agent = BuildAgentViewLocked(route.RunId, route.AgentId)
                };
                break;
            case AgentObservabilityUiView.Issue:
                snapshot = snapshot with
                {
                    Issues = BuildIssuesViewLocked(),
                    Issue = BuildIssueDetailLocked(route.IssueId)
                };
                break;
        }

        return snapshot with
        {
            SelectedIssueId = selectedIssueId,
            SelectedEventId = selectedEventId,
            IssueMode = issueMode,
            AgentDetailTab = agentDetailTab,
            Assessment = assessment,
            Selection = new AgentObservabilityUiSelection(
                route,
                selectedIssueId,
                selectedEventId,
                issueMode,
                agentDetailTab,
                selectedIssueIds
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray(),
                assessment),
            EmptyState = snapshot.All?.EmptyState ??
                snapshot.Issues?.EmptyState ??
                snapshot.Agent?.EmptyState ??
                (snapshot.Issue is null && route.View == AgentObservabilityUiView.Issue
                    ? "Issue not found."
                    : null)
        };
    }

    private AgentObservabilityUiNavigationModel BuildNavigationLocked()
    {
        if (cachedNavigation is not null &&
            cachedNavigationRevision == revision)
        {
            return cachedNavigation;
        }

        var items = new List<AgentObservabilityUiNavigationItem>
        {
            new(
                "all",
                "all",
                "All",
                "All",
                route.View == AgentObservabilityUiView.All),
            new(
                "issues",
                "issues",
                "Issues",
                "Issues",
                route.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue)
        };

        AgentSnapshot? selectedAgent = route.View == AgentObservabilityUiView.Agent &&
            route.AgentId is not null
                ? FindAgentLocked(route.AgentId, route.RunId)
                : null;
        string? selectedGroupKey = selectedAgent is null
            ? null
            : AgentLogicalGroupKey(selectedAgent);
        IEnumerable<NavigationAgentGroup> modAgents = agents.Values
            .Where(agent => MatchesRun(agent.RunId))
            .GroupBy(AgentLogicalGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                AgentSnapshot[] groupAgents = group.ToArray();
                bool hasUnresolvedError = issues.Values.Any(issue =>
                    !issue.Recovered &&
                    issue.Severity == AgentIssueSeverity.Error &&
                    string.Equals(
                        AgentLogicalGroupKey(issue),
                        group.Key,
                        StringComparison.OrdinalIgnoreCase));
                AgentSnapshot representative = PreferredAgent(groupAgents);
                AgentObservabilityAgentNavigationStatus navigationStatus =
                    representative.Status == AgentStatus.Failed
                        ? AgentObservabilityAgentNavigationStatus.Failed
                        : hasUnresolvedError
                            ? AgentObservabilityAgentNavigationStatus.NeedsAttention
                            : representative.Status is AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting
                                ? AgentObservabilityAgentNavigationStatus.Working
                                : AgentObservabilityAgentNavigationStatus.Completed;
                return new NavigationAgentGroup(
                    group.Key,
                    representative,
                    navigationStatus,
                    hasUnresolvedError);
            })
            .OrderBy(static group => group.NavigationStatus switch
            {
                AgentObservabilityAgentNavigationStatus.NeedsAttention => 0,
                AgentObservabilityAgentNavigationStatus.Failed => 1,
                AgentObservabilityAgentNavigationStatus.Working => 2,
                _ => 3
            })
            .ThenByDescending(static group => group.Representative.StartTime)
            .ThenBy(static group => AgentDisplayName(group.Representative), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(options.MaximumNavigationAgents);
        foreach (NavigationAgentGroup group in modAgents)
        {
            AgentSnapshot agent = group.Representative;
            string fullLabel = AgentDisplayName(agent);
            string label = ShortLabel(fullLabel);
            items.Add(new AgentObservabilityUiNavigationItem(
                "agent-group:" + group.Key,
                "agent",
                label,
                fullLabel,
                route.View == AgentObservabilityUiView.Agent &&
                    string.Equals(selectedGroupKey, group.Key, StringComparison.OrdinalIgnoreCase),
                agent.AgentId,
                agent.ModId,
                agent.Status,
                agent.RunId,
                CanDismiss: false,
                group.NavigationStatus,
                group.HasUnresolvedError));
        }

        cachedNavigation = new AgentObservabilityUiNavigationModel(route.View, items);
        cachedNavigationRevision = revision;
        return cachedNavigation;
    }

    private AgentObservabilityAllView BuildAllViewLocked()
    {
        if (cachedAllView is not null &&
            cachedAllRevision == revision)
        {
            return cachedAllView;
        }

        AgentSnapshot[] visibleAgents = agents.Values
            .Where(agent => MatchesRun(agent.RunId))
            .OrderByDescending(static agent => agent.StartTime)
            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
            .ToArray();
        AgentEvent[] visibleEvents = events
            .Where(eventRecord => MatchesRun(eventRecord.RunId))
            .ToArray();
        bool hasMore = visibleEvents.Length > options.MaximumActivityRows;
        AgentEvent[] boundedEvents = visibleEvents
            .TakeLast(options.MaximumActivityRows)
            .ToArray();
        HashSet<string> visibleEventIds = boundedEvents
            .Select(static eventRecord => eventRecord.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<string>> issueIdsByEvent =
            BuildIssueEventIndexLocked(visibleEventIds);
        var rows = boundedEvents
            .Select(eventRecord => ToActivityRowLocked(eventRecord, issueIdsByEvent))
            .ToArray();
        cachedAllView = new AgentObservabilityAllView(
            visibleAgents,
            rows,
            hasMore,
            rows.Length == 0 ? null : rows[^1].Sequence,
            visibleAgents.Length == 0
                ? "No mod agents have reported activity yet."
                : rows.Length == 0
                    ? "Agents are registered; activity has not arrived yet."
                    : null);
        cachedAllRevision = revision;
        return cachedAllView;

    }

    private AgentObservabilityIssuesView BuildIssuesViewLocked()
    {
        if (cachedIssuesView is not null &&
            cachedIssueProjectionRevision == issueProjectionRevision &&
            cachedIssueSelectionRevision == issueSelectionRevision &&
            cachedIncludeRecovered == includeRecovered &&
            cachedVisibleIssueLimit == visibleIssueLimit)
        {
            return cachedIssuesView;
        }

        AgentIssue[] candidates = issues.Values
            .Where(issue => MatchesRun(issue.RunId) &&
                (includeRecovered || !issue.Recovered))
            .ToArray();
        AgentIssue[] visibleIssues = candidates
            .OrderBy(static issue => issue.Recovered ? 1 : 0)
            .ThenBy(static issue => issue.Severity switch
            {
                AgentIssueSeverity.Error => 0,
                AgentIssueSeverity.Warning => 1,
                _ => 2
            })
            .ThenByDescending(issue => GetSharedToolingHintLocked(issue)?.AffectedAgentCount ?? 0)
            .ThenByDescending(IsActiveAgentLocked)
            .ThenByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .ToArray();
        int recoveredCount = candidates.Count(static issue => issue.Recovered);
        int unresolvedCount = candidates.Length - recoveredCount;
        bool hasMore = visibleIssues.Length > visibleIssueLimit;
        AgentIssueRowBuilder rowBuilder = new(agents);
        AgentObservabilityIssueRow[] rows = visibleIssues
            .Take(visibleIssueLimit)
            .Select(issue =>
            {
                AgentObservabilityIssueRow row =
                    rowBuilder.Build(issue, selectedIssueIds.Contains(issue.Id));
                AgentObservabilitySharedToolingHint? shared =
                    GetSharedToolingHintLocked(issue);
                return row with
                {
                    SharedAgentCount = shared?.AffectedAgentCount ?? 0,
                    SharedTooling = shared
                };
            })
            .ToArray();
        cachedIssuesView = new AgentObservabilityIssuesView(
            rows,
            selectedIssueIds
                .Where(id => rows.Any(row => string.Equals(row.Issue.Id, id, StringComparison.Ordinal)))
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            recoveredCount,
            unresolvedCount,
            hasMore,
            candidates.Length == 0
                ? "No structured issues have been reported."
                : hasMore
                    ? $"Showing the first {rows.Length} issues. Load more to inspect older history."
                    : null);
        cachedIssueProjectionRevision = issueProjectionRevision;
        cachedIssueSelectionRevision = issueSelectionRevision;
        cachedIncludeRecovered = includeRecovered;
        cachedVisibleIssueLimit = visibleIssueLimit;
        return cachedIssuesView;
    }

    private AgentObservabilityAgentView? BuildAgentViewLocked(
        string? requestedRunId,
        string? agentId)
    {
        AgentSnapshot? agent = agentId is null
            ? null
            : FindAgentLocked(agentId, requestedRunId);
        if (agent is null)
        {
            return null;
        }

        AgentEvent[] agentEvents = events
            .Where(eventRecord => MatchesRun(eventRecord.RunId) &&
                string.Equals(eventRecord.RunId, agent.RunId, StringComparison.Ordinal) &&
                string.Equals(eventRecord.AgentId, agent.AgentId, StringComparison.Ordinal))
            .ToArray();
        AgentEvent[] recentEvents = agentEvents
            .TakeLast(options.MaximumRecentActivityRows)
            .ToArray();
        AgentEvent? selectedForAgent = selectedEventId is null
            ? null
            : FindEventLocked(selectedEventId);
        if (selectedEventId is not null &&
            (selectedForAgent is null ||
             !string.Equals(selectedForAgent.RunId, agent.RunId, StringComparison.Ordinal) ||
             !string.Equals(selectedForAgent.AgentId, agent.AgentId, StringComparison.Ordinal)))
        {
            selectedEventId = null;
            selectedForAgent = null;
        }
        if (selectedForAgent is not null &&
            string.Equals(selectedForAgent.RunId, agent.RunId, StringComparison.Ordinal) &&
            string.Equals(selectedForAgent.AgentId, agent.AgentId, StringComparison.Ordinal) &&
            !recentEvents.Any(value => string.Equals(value.Id, selectedForAgent.Id, StringComparison.Ordinal)))
        {
            recentEvents = recentEvents
                .Skip(recentEvents.Length >= options.MaximumRecentActivityRows ? 1 : 0)
                .Append(selectedForAgent)
                .OrderBy(static value => value.Sequence)
                .ThenBy(static value => value.Id, StringComparer.Ordinal)
                .ToArray();
        }
        HashSet<string> recentEventIds = recentEvents
            .Select(static eventRecord => eventRecord.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<string>> issueIdsByEvent =
            BuildIssueEventIndexLocked(recentEventIds);
        AgentObservabilityActivityRow[] recent = recentEvents
            .Select(eventRecord => ToActivityRowLocked(eventRecord, issueIdsByEvent))
            .ToArray();
        var files = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var buildResults = new List<AgentObservabilityBuildTestResult>();
        var testResults = new List<AgentObservabilityBuildTestResult>();
        foreach (AgentEvent eventRecord in agentEvents)
        {
            AddEventValues(eventRecord, files, tools, commands);
            if (eventRecord.Type.StartsWith("build.", StringComparison.Ordinal))
            {
                buildResults.Add(ToBuildTestResult("build", eventRecord));
            }
            else if (eventRecord.Type.StartsWith("test.", StringComparison.Ordinal))
            {
                testResults.Add(ToBuildTestResult("test", eventRecord));
            }
        }

        AgentIssue[] agentIssues = issues.Values
            .Where(issue => MatchesRun(issue.RunId) &&
                string.Equals(issue.RunId, agent.RunId, StringComparison.Ordinal) &&
                string.Equals(issue.AgentId, agent.AgentId, StringComparison.Ordinal))
            .OrderByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .ToArray();
        AgentObservabilityEventDetail? selectedEvent = selectedForAgent is not null &&
                string.Equals(selectedForAgent.RunId, agent.RunId, StringComparison.Ordinal) &&
                string.Equals(selectedForAgent.AgentId, agent.AgentId, StringComparison.Ordinal)
            ? BuildEventDetailLocked(selectedForAgent)
            : null;
        AgentSnapshot[] sessionAgents = agents.Values
            .Where(value =>
                MatchesRun(value.RunId) &&
                string.Equals(AgentLogicalGroupKey(value), AgentLogicalGroupKey(agent), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AgentSnapshot currentSessionAgent = PreferredAgent(sessionAgents);
        AgentObservabilitySessionSummary currentSession = ToSessionSummary(currentSessionAgent);
        AgentObservabilitySessionSummary[] pastSessions = sessionAgents
            .Where(value => !string.Equals(
                AgentIdentity(value).Key,
                AgentIdentity(currentSessionAgent).Key,
                StringComparison.Ordinal))
            .OrderByDescending(static value => value.StartTime)
            .ThenBy(static value => value.RunId, StringComparer.Ordinal)
            .Select(ToSessionSummary)
            .ToArray();
        return new AgentObservabilityAgentView(
            agent,
            ElapsedMilliseconds(agent),
            BuildStageProgress(agent),
            recent,
            SortedValues(files).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(tools).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(commands).Take(options.MaximumSupportingEvents).ToArray(),
            buildResults.TakeLast(options.MaximumRecentActivityRows).ToArray(),
            testResults.TakeLast(options.MaximumRecentActivityRows).ToArray(),
            agentIssues.Where(static issue => issue.Severity == AgentIssueSeverity.Warning).ToArray(),
            agentIssues.Where(static issue => issue.Severity == AgentIssueSeverity.Error).ToArray(),
            agentIssues,
            currentSession,
            pastSessions,
            agentEvents.Length == 0 ? "No activity has been reported for this agent yet." : null,
            selectedEvent?.Event.Id,
            selectedEvent);
    }

    public AgentObservabilityEventDetail? GetEventDetail(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        lock (gate)
        {
            ThrowIfDisposedLocked();
            AgentEvent? eventRecord = FindEventLocked(eventId.Trim());
            return eventRecord is null ? null : BuildEventDetailLocked(eventRecord);
        }
    }

    private AgentObservabilityEventDetail BuildEventDetailLocked(AgentEvent eventRecord)
    {
        string? operationKey = OperationKey(eventRecord);
        var related = new Dictionary<string, AgentEvent>(StringComparer.Ordinal)
        {
            [eventRecord.Id] = eventRecord
        };
        string? operationIndexKey = string.IsNullOrWhiteSpace(operationKey)
            ? null
            : OperationIndexKey(eventRecord.RunId, eventRecord.AgentId, operationKey);
        if (operationIndexKey is not null && hydratedOperations.Add(operationIndexKey))
        {
            foreach (AgentEvent historicalEvent in store.GetEvents(
                         runId: eventRecord.RunId,
                         agentId: eventRecord.AgentId,
                         modId: eventRecord.ModId,
                         limit: Math.Min(
                             50_000,
                             Math.Max(options.MaximumIndexedEvents, options.MaximumSupportingEvents))))
            {
                if (string.Equals(OperationKey(historicalEvent), operationKey, StringComparison.Ordinal))
                {
                    UpsertEventLocked(historicalEvent);
                }
            }
        }

        if (operationIndexKey is not null &&
            eventsByOperation.TryGetValue(operationIndexKey, out List<string>? relatedIds))
        {
            foreach (string relatedId in relatedIds)
            {
                if (eventsById.TryGetValue(relatedId, out AgentEvent? relatedEvent) &&
                    string.Equals(relatedEvent.RunId, eventRecord.RunId, StringComparison.Ordinal) &&
                    string.Equals(relatedEvent.AgentId, eventRecord.AgentId, StringComparison.Ordinal))
                {
                    related[relatedEvent.Id] = relatedEvent;
                }
            }
        }

        AgentEvent[] relatedEvents = related.Values
            .OrderBy(static value => value.Sequence)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .Take(options.MaximumSupportingEvents)
            .ToArray();
        var files = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var buildResults = new List<AgentObservabilityBuildTestResult>();
        var testResults = new List<AgentObservabilityBuildTestResult>();
        var output = new List<AgentObservabilityOutputExcerpt>();
        var relatedIssueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentEvent relatedEvent in relatedEvents)
        {
            AddEventValues(relatedEvent, files, tools, commands);
            if (relatedEvent.Type.StartsWith("build.", StringComparison.Ordinal))
            {
                buildResults.Add(ToBuildTestResult("build", relatedEvent));
            }
            else if (relatedEvent.Type.StartsWith("test.", StringComparison.Ordinal))
            {
                testResults.Add(ToBuildTestResult("test", relatedEvent));
            }

            if (issueIdsByEvent.TryGetValue(relatedEvent.Id, out HashSet<string>? issueIds))
            {
                relatedIssueIds.UnionWith(issueIds);
            }

            AddOutputExcerpts(relatedEvent, output);
        }

        AgentIssue[] relatedIssues = relatedIssueIds
            .Where(issues.ContainsKey)
            .Select(id => issues[id])
            .OrderByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .ToArray();
        string? status = AgentObservabilityData.GetString(eventRecord.Data, "outcome") ??
            EventStatus(eventRecord);
        long? duration = AgentObservabilityData.GetInt64(eventRecord.Data, "durationMs") ??
            AgentObservabilityData.GetInt64(eventRecord.Data, "durationMilliseconds");
        return new AgentObservabilityEventDetail(
            eventRecord,
            agents.TryGetValue(AgentIdentity(eventRecord), out AgentSnapshot? agent)
                ? agent
                : null,
            relatedEvents,
            operationKey,
            status,
            duration,
            SortedValues(files).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(tools).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(commands).Take(options.MaximumSupportingEvents).ToArray(),
            buildResults.TakeLast(options.MaximumRecentActivityRows).ToArray(),
            testResults.TakeLast(options.MaximumRecentActivityRows).ToArray(),
            relatedIssues,
            relatedIssueIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            output.Take(options.MaximumSupportingEvents).ToArray());
    }

    private AgentObservabilityIssueDetail? BuildIssueDetailLocked(string? issueId)
    {
        if (issueId is null)
        {
            return null;
        }

        AgentIssue? issue = FindIssueLocked(issueId);
        if (issue is null)
        {
            return null;
        }

        AgentSnapshot? agent = FindAgentLocked(issue.AgentId, issue.RunId);
        var eventIndex = new Dictionary<string, AgentEvent>(StringComparer.Ordinal);
        foreach (string eventId in issue.EventIds)
        {
            if (eventsById.TryGetValue(eventId, out AgentEvent? eventRecord) &&
                string.Equals(eventRecord.RunId, issue.RunId, StringComparison.Ordinal) &&
                string.Equals(eventRecord.AgentId, issue.AgentId, StringComparison.Ordinal))
            {
                eventIndex[eventId] = eventRecord;
            }
        }

        if (issue.EventIds.Any(eventId => !eventIndex.ContainsKey(eventId)) ||
            (issue.ResolutionEventId is not null &&
             !eventIndex.ContainsKey(issue.ResolutionEventId)))
        {
            foreach (AgentEvent eventRecord in store.GetEvents(
                         runId: issue.RunId,
                         agentId: issue.AgentId,
                         modId: issue.ModId,
                         limit: Math.Min(
                             50_000,
                             Math.Max(options.MaximumIndexedEvents, options.MaximumSupportingEvents))))
            {
                eventIndex[eventRecord.Id] = eventRecord;
            }
        }

        AgentEvent[] supporting = IssueEventsLocked(issue)
            .OrderBy(static eventRecord => eventRecord.Sequence)
            .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .Take(options.MaximumSupportingEvents)
            .ToArray();
        HashSet<string> resolvedIds = supporting
            .Select(static eventRecord => eventRecord.Id)
            .ToHashSet(StringComparer.Ordinal);
        string[] unresolved = issue.EventIds
            .Where(eventId => !resolvedIds.Contains(eventId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AgentEvent? resolution = issue.ResolutionEventId is not null &&
            eventIndex.TryGetValue(issue.ResolutionEventId, out AgentEvent? resolutionEvent)
            ? resolutionEvent
            : null;
        var files = new HashSet<string>(issue.RelatedFiles ?? [], StringComparer.Ordinal);
        var tools = new HashSet<string>(issue.RelatedToolCalls ?? [], StringComparer.Ordinal);
        var commands = new HashSet<string>(issue.RelatedCommands ?? [], StringComparer.Ordinal);
        var output = new List<AgentObservabilityOutputExcerpt>();
        foreach (AgentEvent eventRecord in supporting)
        {
            AddEventValues(eventRecord, files, tools, commands);
            AddOutputExcerpts(eventRecord, output);
        }

        AgentRecoveryStep[] recoveryPath = supporting
            .Where(static eventRecord => eventRecord.Type == AgentEventTypes.RecoveryCompleted ||
                eventRecord.Type == AgentEventTypes.RetryStarted ||
                eventRecord.Type == AgentEventTypes.RetryCompleted)
            .Select(eventRecord => new AgentRecoveryStep(
                eventRecord.Id,
                eventRecord.Timestamp,
                eventRecord.Type,
                eventRecord.Summary,
                IsSuccessfulEvent(eventRecord)))
            .ToArray();
        AgentDiagnosticBundle bundle = GetDiagnosticBundleLocked(issue.Id);
        AgentObservabilityIssueSignature signature = GetIssueSignatureLocked(issue);
        AgentObservabilitySharedToolingHint? sharedTooling =
            GetSharedToolingHintLocked(issue);
        AgentObservabilityIssueTriage triage =
            AgentObservabilityIssueTriageBuilder.Build(
                issue,
                agent,
                supporting,
                bundle,
                agent is not null && IsCurrentSessionLocked(agent),
                sharedTooling,
                signature);
        return new AgentObservabilityIssueDetail(
            issue,
            agent,
            supporting,
            unresolved,
            SortedValues(files).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(tools).Take(options.MaximumSupportingEvents).ToArray(),
            SortedValues(commands).Take(options.MaximumSupportingEvents).ToArray(),
            output,
            resolution,
            recoveryPath,
            issue.Recovered ? "recovered" : "unresolved",
            issue.TraceId,
            issue.SpanIds ?? [],
            route.FocusEventId ?? selectedEventId ?? issue.EventIds.FirstOrDefault())
        {
            Triage = triage
        };

    }

    private AgentDiagnosticBundle GetDiagnosticBundleLocked(string issueId)
    {
        if (!diagnosticBundles.TryGetValue(issueId, out AgentDiagnosticBundle? bundle))
        {
            bundle = store.CreateDiagnosticBundle([issueId]);
            diagnosticBundles[issueId] = bundle;
        }

        return bundle;
    }

    private AgentObservabilityIssueSignature GetIssueSignatureLocked(AgentIssue issue)
    {
        if (!issueSignatures.TryGetValue(issue.Id, out AgentObservabilityIssueSignature? signature))
        {
            issueSignatureComputations++;
            signature = AgentObservabilityIssueTriageBuilder.Describe(
                issue,
                IssueEventsLocked(issue));
            issueSignatures[issue.Id] = signature;
            if (signature.IsStrong && !string.IsNullOrWhiteSpace(signature.Fingerprint))
            {
                if (!issueIdsByFingerprint.TryGetValue(
                        signature.Fingerprint,
                        out HashSet<string>? issueIds))
                {
                    issueIds = new HashSet<string>(StringComparer.Ordinal);
                    issueIdsByFingerprint[signature.Fingerprint] = issueIds;
                }

                issueIds.Add(issue.Id);
                InvalidateSharedToolingHintsLocked(issueIds);
            }
        }

        return signature;
    }

    private AgentObservabilitySharedToolingHint? GetSharedToolingHintLocked(
        AgentIssue issue)
    {
        if (sharedToolingHints.TryGetValue(issue.Id, out AgentObservabilitySharedToolingHint? cached))
        {
            return cached;
        }

        AgentObservabilityIssueSignature signature = GetIssueSignatureLocked(issue);
        AgentObservabilitySharedToolingHint? result = null;
        if (signature.IsStrong &&
            signature.ErrorCode is not null &&
            signature.Component is not null &&
            AgentObservabilityIssueTriageBuilder.IsToolingComponent(signature.Component) &&
            issueIdsByFingerprint.TryGetValue(
                signature.Fingerprint,
                out HashSet<string>? issueIds))
        {
            var affected = new Dictionary<string, AgentIssue>(StringComparer.OrdinalIgnoreCase);
            foreach (string issueId in issueIds)
            {
                if (!issues.TryGetValue(issueId, out AgentIssue? candidate))
                {
                    continue;
                }

                string logicalKey = AgentLogicalGroupKey(candidate);
                affected.TryAdd(logicalKey, candidate);
            }

            int affectedSessionCount = issueIds
                .Select(id => issues.TryGetValue(id, out AgentIssue? value)
                    ? value.RunId + "\u001f" + value.AgentId
                    : null)
                .Where(static value => value is not null)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (affected.Count >= 2 || affectedSessionCount >= 2)
            {
                result = new AgentObservabilitySharedToolingHint(
                    signature.ErrorCode,
                    signature.Component,
                    affected.Count,
                    affected.Values
                        .Select(static value => value.ModId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                        .Take(options.MaximumNavigationAgents)
                        .ToArray(),
                    affectedSessionCount);
            }
        }

        sharedToolingHints[issue.Id] = result;
        return result;
    }

    private IReadOnlyList<AgentEvent> IssueEventsLocked(AgentIssue issue)
    {
        IEnumerable<AgentEvent> direct = issue.EventIds
            .Where(eventsById.ContainsKey)
            .Select(eventId => eventsById[eventId]);
        IEnumerable<AgentEvent> causal = eventsById.Values
            .Where(value =>
                string.Equals(value.RunId, issue.RunId, StringComparison.Ordinal) &&
                string.Equals(value.AgentId, issue.AgentId, StringComparison.Ordinal) &&
                string.Equals(value.ModId, issue.ModId, StringComparison.Ordinal) &&
                IsCausalDevBridgeEvent(value));
        return direct
            .Concat(causal)
            .DistinctBy(static value => value.Id, StringComparer.Ordinal)
            .OrderBy(static value => value.Sequence)
            .ThenBy(static value => value.Id, StringComparer.Ordinal)
            .Take(options.MaximumSupportingEvents)
            .ToArray();
    }

    private static bool IsCausalDevBridgeEvent(AgentEvent value)
    {
        string? tool = AgentObservabilityData.GetString(value.Data, "toolName");
        return string.Equals(tool, "DevBridge", StringComparison.OrdinalIgnoreCase) &&
            (AgentObservabilityData.GetString(value.Data, "underlyingErrorCode") is not null ||
             AgentObservabilityData.GetString(value.Data, "errorCode") is not null ||
             AgentObservabilityData.GetString(value.Data, "outerErrorCode") is not null);
    }

    private bool IsActiveAgentLocked(AgentIssue issue) =>
        agents.TryGetValue(new AgentObservabilityAgentIdentity(issue.RunId, issue.AgentId), out AgentSnapshot? agent) &&
        agent.Status is AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting;

    private bool IsCurrentSessionLocked(AgentSnapshot agent)
    {
        AgentSnapshot[] candidates = agents.Values
            .Where(value =>
                MatchesRun(value.RunId) &&
                string.Equals(AgentLogicalGroupKey(value), AgentLogicalGroupKey(agent), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length > 0 &&
            string.Equals(
                AgentIdentity(PreferredAgent(candidates)).Key,
                AgentIdentity(agent).Key,
                StringComparison.Ordinal);
    }

    private Dictionary<string, List<string>> BuildIssueEventIndexLocked(
        IReadOnlySet<string>? eventFilter = null)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string eventId, HashSet<string> issueIds) in issueIdsByEvent)
        {
            if (eventFilter is not null && !eventFilter.Contains(eventId))
            {
                continue;
            }

            result[eventId] = issueIds
                .Where(id => issues.TryGetValue(id, out AgentIssue? issue) && MatchesRun(issue.RunId))
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToList();
        }

        return result;
    }

    private AgentObservabilityActivityRow ToActivityRowLocked(
        AgentEvent eventRecord,
        IReadOnlyDictionary<string, List<string>> issueIdsByEvent)
    {
        AgentSnapshot? agent = agents.TryGetValue(AgentIdentity(eventRecord), out AgentSnapshot? value)
            ? value
            : null;
        IReadOnlyList<string> issueIds = issueIdsByEvent.TryGetValue(eventRecord.Id, out List<string>? ids)
            ? ids.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToArray()
            : [];
        return new AgentObservabilityActivityRow(
            eventRecord,
            agent is null ? eventRecord.ModId : AgentDisplayName(agent),
            agent?.Status,
            issueIds.Count > 0,
            issueIds);
    }

    private AgentSnapshot? FindAgentLocked(
        string agentIdOrModId,
        string? requestedRunId = null)
    {
        string? scopeRunId = requestedRunId ?? activeRunId;
        AgentSnapshot[] agentIdMatches = agents.Values
            .Where(value =>
                MatchesRun(value.RunId, scopeRunId) &&
                string.Equals(value.AgentId, agentIdOrModId, StringComparison.Ordinal))
            .ToArray();
        if (agentIdMatches.Length > 0)
        {
            return requestedRunId is null
                ? PreferredAgent(agentIdMatches)
                : agentIdMatches[0];
        }

        AgentSnapshot[] modIdMatches = agents.Values
            .Where(value =>
                MatchesRun(value.RunId, scopeRunId) &&
                string.Equals(value.ModId, agentIdOrModId, StringComparison.Ordinal))
            .ToArray();
        if (modIdMatches.Length > 0)
        {
            return PreferredAgent(modIdMatches);
        }

        AgentSnapshot? agent = store.GetAgents(
                runId: scopeRunId,
                agentId: agentIdOrModId,
                limit: options.MaximumIndexedAgents)
            .OrderByDescending(static value => value.StartTime)
            .FirstOrDefault();
        if (agent is null)
        {
            agent = store.GetAgents(
                    runId: scopeRunId,
                    modId: agentIdOrModId,
                    limit: options.MaximumIndexedAgents)
                .OrderByDescending(static value => value.StartTime)
                .FirstOrDefault();
        }

        if (agent is not null)
        {
            UpsertAgentLocked(agent);
        }

        return agent;
    }

    private AgentIssue? FindIssueLocked(string issueId)
    {
        if (issues.TryGetValue(issueId, out AgentIssue? issue) && MatchesRun(issue.RunId))
        {
            return issue;
        }

        issue = store.GetIssues(runId: activeRunId, limit: 10_000)
            .FirstOrDefault(value => string.Equals(value.Id, issueId, StringComparison.Ordinal));
        if (issue is not null)
        {
            UpsertIssueLocked(issue);
        }

        return issue;
    }

    private AgentObservabilityUiRoute NormalizeRouteLocked(AgentObservabilityUiRoute requested)
    {
        return requested.View switch
        {
            AgentObservabilityUiView.All => new(AgentObservabilityUiView.All),
            AgentObservabilityUiView.Issues => new(AgentObservabilityUiView.Issues),
            AgentObservabilityUiView.Agent => new(
                AgentObservabilityUiView.Agent,
                requested.AgentId,
                RunId: requested.RunId ?? activeRunId),
            AgentObservabilityUiView.Issue => new(
                AgentObservabilityUiView.Issue,
                requested.AgentId,
                requested.IssueId,
                requested.FocusEventId,
                requested.RunId ?? activeRunId),
            _ => new(AgentObservabilityUiView.All)
        };
    }

    private void UpsertAgentLocked(AgentSnapshot agent)
    {
        if (!MatchesRun(agent.RunId))
        {
            return;
        }

        AgentObservabilityAgentIdentity identity = AgentIdentity(agent);
        bool orderingChanged = !agents.TryGetValue(identity, out AgentSnapshot? existing) ||
            IsActiveStatus(existing.Status) != IsActiveStatus(agent.Status) ||
            !string.Equals(existing.ModName, agent.ModName, StringComparison.Ordinal) ||
            !string.Equals(existing.ModId, agent.ModId, StringComparison.Ordinal);
        agents[identity] = agent;
        if (orderingChanged)
        {
            issueProjectionRevision++;
        }

        if (agents.Count > options.MaximumIndexedAgents)
        {
            AgentSnapshot? remove = agents.Values
                .OrderBy(static value => value.StartTime)
                .ThenBy(static value => value.AgentId, StringComparer.Ordinal)
                .FirstOrDefault(value =>
                    !string.Equals(value.AgentId, route.AgentId, StringComparison.Ordinal) ||
                    !string.Equals(value.RunId, route.RunId, StringComparison.Ordinal));
            if (remove is not null)
            {
                agents.Remove(AgentIdentity(remove));
                issueProjectionRevision++;
            }
        }
    }

    private void UpsertEventLocked(AgentEvent eventRecord)
    {
        if (!MatchesRun(eventRecord.RunId))
        {
            return;
        }

        bool evidenceChanged = !eventsById.TryGetValue(
                eventRecord.Id,
                out AgentEvent? existing) ||
            !SameEventEvidence(existing, eventRecord);
        if (evidenceChanged)
        {
            InvalidateIssueSignaturesForEventLocked(eventRecord.Id);
        }
        if (existing is not null)
        {
            RemoveEventFromOperationIndexLocked(existing);
            eventsById[eventRecord.Id] = eventRecord;
            AddEventToOperationIndexLocked(eventRecord);
            int index = events.FindIndex(value => string.Equals(value.Id, eventRecord.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                events[index] = eventRecord;
            }

            diagnosticBundles.Clear();
            return;
        }

        eventsById[eventRecord.Id] = eventRecord;
        diagnosticBundles.Clear();
        AddEventToOperationIndexLocked(eventRecord);
        if (events.Count == 0 || eventRecord.Sequence >= events[^1].Sequence)
        {
            events.Add(eventRecord);
        }
        else
        {
            int index = events.BinarySearch(eventRecord, AgentEventSequenceComparer.Instance);
            events.Insert(index < 0 ? ~index : index, eventRecord);
        }

        while (events.Count > options.MaximumIndexedEvents)
        {
            AgentEvent removed = events[0];
            events.RemoveAt(0);
            eventsById.Remove(removed.Id);
            RemoveEventFromOperationIndexLocked(removed);
        }
    }

    private void UpsertIssueLocked(AgentIssue issue)
    {
        if (!MatchesRun(issue.RunId))
        {
            return;
        }

        bool added = !issues.TryGetValue(issue.Id, out AgentIssue? existing);
        bool evidenceChanged = added || !SameIssueEvidence(existing!, issue);
        bool projectionChanged = added || !SameIssueProjection(existing!, issue);
        if (!added)
        {
            RemoveIssueFromEventIndexLocked(existing!);
            if (evidenceChanged)
            {
                RemoveIssueSignatureLocked(existing!.Id);
            }
        }

        issues[issue.Id] = issue;
        AddIssueToEventIndexLocked(issue);
        if (evidenceChanged)
        {
            GetIssueSignatureLocked(issue);
        }

        if (projectionChanged || evidenceChanged)
        {
            issueProjectionRevision++;
            diagnosticBundles.Clear();
        }

        if (issues.Count > options.MaximumIndexedIssues)
        {
            string? remove = issues.Values
                .OrderBy(static value => value.Timestamp)
                .ThenBy(static value => value.Id, StringComparer.Ordinal)
                .Select(static value => value.Id)
                .FirstOrDefault(value => !selectedIssueIds.Contains(value));
            if (remove is not null &&
                issues.Remove(remove, out AgentIssue? removed))
            {
                RemoveIssueFromEventIndexLocked(removed);
                RemoveIssueSignatureLocked(removed.Id);
                issueProjectionRevision++;
            }
        }
    }
    private AgentObservabilitySessionSummary ToSessionSummary(AgentSnapshot agent)
    {
        long? duration = agent.CompletedAt is long completedAt
            ? Math.Max(0, completedAt - agent.StartTime)
            : null;
        return new AgentObservabilitySessionSummary(
            agent.RunId,
            agent.AgentId,
            agent.ModId,
            AgentDisplayName(agent),
            agent.Status,
            agent.CompletionState,
            agent.StartTime,
            agent.CompletedAt,
            duration,
            agent.FailureState,
            agent.FailureSummary,
            agent.LogicalAgentId);
    }

    private bool MatchesRun(string value) =>
        MatchesRun(value, activeRunId);

    private static bool MatchesRun(string value, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(expected, value, StringComparison.Ordinal);

    private static AgentObservabilityAgentIdentity AgentIdentity(AgentSnapshot agent) =>
        new(agent.RunId, agent.AgentId);

    private static AgentObservabilityAgentIdentity AgentIdentity(AgentEvent eventRecord) =>
        new(eventRecord.RunId, eventRecord.AgentId);

    private static string AgentLogicalGroupKey(AgentSnapshot agent) =>
        AgentObservabilityLogicalIdentity.GroupKey(agent);

    private static string AgentLogicalGroupKey(AgentIssue issue) =>
        AgentObservabilityLogicalIdentity.GroupKey(issue);

    private static AgentSnapshot PreferredAgent(IEnumerable<AgentSnapshot> candidates) =>
        candidates
            .OrderBy(static agent => agent.Status is AgentStatus.Completed or AgentStatus.Failed
                ? 1
                : 0)
            .ThenByDescending(static agent => agent.StartTime)
            .ThenByDescending(static agent => agent.CompletedAt ?? 0)
            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
            .First();


    private static string AgentDisplayName(AgentSnapshot agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ModName))
        {
            return agent.ModName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(agent.ModId))
        {
            return agent.ModId;
        }

        return agent.AgentId;
    }

    private static string? OperationKey(AgentEvent eventRecord) =>
        AgentObservabilityData.GetString(eventRecord.Data, "operationKey") ??
        AgentObservabilityData.GetString(eventRecord.Data, "operation");

    private static string OperationIndexKey(
        string runId,
        string agentId,
        string operationKey) =>
        new AgentObservabilityAgentIdentity(runId, agentId).Key + "\u001f" + operationKey;

    private static string EventStatus(AgentEvent eventRecord)
    {
        if (eventRecord.Type.EndsWith("failed", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("timeout", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (eventRecord.Type.EndsWith("started", StringComparison.OrdinalIgnoreCase))
        {
            return "running";
        }

        if (eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        return "observed";
    }

    private static void AddOutputExcerpts(
        AgentEvent eventRecord,
        ICollection<AgentObservabilityOutputExcerpt> output)
    {
        foreach (string outputName in OutputNames)
        {
            string? value = AgentObservabilityData.GetString(eventRecord.Data, outputName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                output.Add(new AgentObservabilityOutputExcerpt(
                    eventRecord.Id,
                    outputName,
                    AgentObservabilityData.BoundText(value, 4_096)));
            }
        }
    }

    private void AddEventToOperationIndexLocked(AgentEvent eventRecord)
    {
        string? operationKey = OperationKey(eventRecord);
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return;
        }

        string indexKey = OperationIndexKey(
            eventRecord.RunId,
            eventRecord.AgentId,
            operationKey);
        if (!eventsByOperation.TryGetValue(indexKey, out List<string>? ids))
        {
            ids = [];
            eventsByOperation[indexKey] = ids;
        }

        if (!ids.Contains(eventRecord.Id, StringComparer.Ordinal))
        {
            ids.Add(eventRecord.Id);
        }
    }

    private void RemoveEventFromOperationIndexLocked(AgentEvent eventRecord)
    {
        string? operationKey = OperationKey(eventRecord);
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return;
        }

        string indexKey = OperationIndexKey(
            eventRecord.RunId,
            eventRecord.AgentId,
            operationKey);
        if (!eventsByOperation.TryGetValue(indexKey, out List<string>? ids))
        {
            return;
        }

        ids.RemoveAll(id => string.Equals(id, eventRecord.Id, StringComparison.Ordinal));
        if (ids.Count == 0)
        {
            eventsByOperation.Remove(indexKey);
        }
    }

    private void AddIssueToEventIndexLocked(AgentIssue issue)
    {
        foreach (string eventId in issue.EventIds)
        {
            if (!issueIdsByEvent.TryGetValue(eventId, out HashSet<string>? ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                issueIdsByEvent[eventId] = ids;
            }

            ids.Add(issue.Id);
        }
    }

    private void RemoveIssueFromEventIndexLocked(AgentIssue issue)
    {
        foreach (string eventId in issue.EventIds)
        {
            if (!issueIdsByEvent.TryGetValue(eventId, out HashSet<string>? ids))
            {
                continue;
            }

            ids.Remove(issue.Id);
            if (ids.Count == 0)
            {
                issueIdsByEvent.Remove(eventId);
            }
        }
    }
    private void RemoveIssueSignatureLocked(string issueId)
    {
        sharedToolingHints.Remove(issueId);
        if (!issueSignatures.Remove(issueId, out AgentObservabilityIssueSignature? signature) ||
            string.IsNullOrWhiteSpace(signature.Fingerprint) ||
            !issueIdsByFingerprint.TryGetValue(signature.Fingerprint, out HashSet<string>? issueIds))
        {
            return;
        }

        issueIds.Remove(issueId);
        InvalidateSharedToolingHintsLocked(issueIds);
        if (issueIds.Count == 0)
        {
            issueIdsByFingerprint.Remove(signature.Fingerprint);
        }
    }

    private void InvalidateIssueSignaturesForEventLocked(string eventId)
    {
        if (!issueIdsByEvent.TryGetValue(eventId, out HashSet<string>? issueIds))
        {
            return;
        }

        string[] affected = issueIds.ToArray();
        foreach (string issueId in affected)
        {
            RemoveIssueSignatureLocked(issueId);
        }

        if (affected.Length > 0)
        {
            issueProjectionRevision++;
        }
    }

    private void InvalidateSharedToolingHintsLocked(IEnumerable<string> issueIds)
    {
        foreach (string issueId in issueIds)
        {
            sharedToolingHints.Remove(issueId);
        }
    }

    private static bool SameIssueEvidence(AgentIssue left, AgentIssue right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal) &&
        string.Equals(left.ModId, right.ModId, StringComparison.Ordinal) &&
        left.EventIds.SequenceEqual(right.EventIds, StringComparer.Ordinal);

    private static bool SameIssueProjection(AgentIssue left, AgentIssue right) =>
        SameIssueEvidence(left, right) &&
        left.Timestamp == right.Timestamp &&
        left.Category == right.Category &&
        left.Severity == right.Severity &&
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
        left.Recovered == right.Recovered;

    private static bool SameEventEvidence(AgentEvent left, AgentEvent right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal) &&
        string.Equals(left.ModId, right.ModId, StringComparison.Ordinal) &&
        string.Equals(left.Type, right.Type, StringComparison.Ordinal) &&
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
        string.Equals(
            left.Data?.GetRawText(),
            right.Data?.GetRawText(),
            StringComparison.Ordinal);

    private static bool IsActiveStatus(AgentStatus status) =>
        status is AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting;

    private string? ResolveFocusEventLocked(
        string? requestedEventId,
        string? requestedRunId,
        string? requestedAgentId)
    {
        string? candidate = requestedEventId ?? selectedEventId;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        AgentEvent? eventRecord = FindEventLocked(candidate);
        return eventRecord is not null &&
            (requestedRunId is null || string.Equals(eventRecord.RunId, requestedRunId, StringComparison.Ordinal)) &&
            (requestedAgentId is null || string.Equals(eventRecord.AgentId, requestedAgentId, StringComparison.Ordinal))
            ? eventRecord.Id
            : null;
    }

    private AgentEvent? FindEventLocked(string eventId)
    {
        if (eventsById.TryGetValue(eventId, out AgentEvent? eventRecord) &&
            MatchesRun(eventRecord.RunId))
        {
            return eventRecord;
        }

        eventRecord = store.GetEvents(
                runId: activeRunId,
                limit: Math.Min(
                    50_000,
                    Math.Max(options.MaximumIndexedEvents, options.MaximumSupportingEvents)))
            .FirstOrDefault(value => string.Equals(value.Id, eventId, StringComparison.Ordinal));
        if (eventRecord is not null)
        {
            UpsertEventLocked(eventRecord);
        }

        return eventRecord;
    }

    private void ApplyRouteStateLocked(
        AgentObservabilityUiRoute previous,
        AgentObservabilityUiRoute next)
    {
        bool previousIssueView = previous.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue;
        bool nextIssueView = next.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue;
        if (previousIssueView && !nextIssueView)
        {
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        bool changedAgent = previous.View != AgentObservabilityUiView.Agent ||
            next.View != AgentObservabilityUiView.Agent ||
            !string.Equals(previous.AgentId, next.AgentId, StringComparison.Ordinal) ||
            !string.Equals(previous.RunId, next.RunId, StringComparison.Ordinal);
        if (changedAgent && next.View == AgentObservabilityUiView.Agent)
        {
            selectedEventId = ResolveFocusEventLocked(
                next.FocusEventId,
                next.RunId,
                next.AgentId);
            agentDetailTab = AgentObservabilityAgentDetailTab.Event;
        }

        if (next.View == AgentObservabilityUiView.Issue && next.IssueId is not null)
        {
            AgentIssue? issue = FindIssueLocked(next.IssueId);
            if (issue is not null)
            {
                selectedIssueId = issue.Id;
                selectedEventId = ResolveFocusEventLocked(
                    next.FocusEventId ?? issue.EventIds.FirstOrDefault(),
                    issue.RunId,
                    issue.AgentId);
            }
        }
    }

    private AgentObservabilityUiSnapshot PublishSelectionChange()
    {
        Action<AgentObservabilityUiUpdate>[] handlers;
        AgentObservabilityUiUpdate update;
        AgentObservabilityUiSnapshot snapshot;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            revision++;
            snapshot = BuildSnapshotLocked();
            update = new AgentObservabilityUiUpdate(
                revision,
                AgentObservabilityUiUpdateKind.SelectionChanged,
                route.View,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
        return snapshot;
    }

    private AgentObservabilityUiSnapshot PublishNavigationChange()
    {
        Action<AgentObservabilityUiUpdate>[] handlers;
        AgentObservabilityUiUpdate update;
        AgentObservabilityUiSnapshot snapshot;
        lock (gate)
        {
            ThrowIfDisposedLocked();
            revision++;
            snapshot = BuildSnapshotLocked();
            update = new AgentObservabilityUiUpdate(
                revision,
                AgentObservabilityUiUpdateKind.NavigationChanged,
                route.View,
                PreserveActivityPosition: true,
                RequestScroll: false);
            handlers = subscribers.ToArray();
        }

        Notify(handlers, update);
        return snapshot;
    }

    private long ElapsedMilliseconds(AgentSnapshot agent)
    {
        if (agent.StartTime <= 0)
        {
            return 0;
        }

        long end = agent.CompletedAt ?? nowMilliseconds();
        return Math.Max(0, end - agent.StartTime);
    }

    private static IReadOnlyList<AgentObservabilityStageProgress> BuildStageProgress(AgentSnapshot agent)
    {
        int currentIndex = Array.IndexOf(LifecycleStages, agent.CurrentStage);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        return LifecycleStages
            .Select((stage, index) =>
            {
                string state = agent.Status switch
                {
                    AgentStatus.Completed => "completed",
                    AgentStatus.Failed when index < currentIndex => "completed",
                    AgentStatus.Failed when index == currentIndex => "failed",
                    AgentStatus.Waiting when index == currentIndex => "waiting",
                    AgentStatus.Created when index == currentIndex => "created",
                    _ when index < currentIndex => "completed",
                    _ when index == currentIndex => "current",
                    _ => "pending"
                };
                return new AgentObservabilityStageProgress(
                    stage,
                    state,
                    index == currentIndex && agent.Status is not AgentStatus.Completed);
            })
            .ToArray();
    }

    private static AgentObservabilityBuildTestResult ToBuildTestResult(
        string kind,
        AgentEvent eventRecord)
    {
        string status = eventRecord.Type switch
        {
            AgentEventTypes.BuildStarted or AgentEventTypes.TestStarted => "running",
            AgentEventTypes.BuildSucceeded or AgentEventTypes.TestPassed => "passed",
            AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed => "failed",
            _ => eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase)
                ? "completed"
                : "observed"
        };
        return new AgentObservabilityBuildTestResult(
            kind,
            status,
            eventRecord.Id,
            eventRecord.Timestamp,
            eventRecord.Summary);
    }

    private static void AddEventValues(
        AgentEvent eventRecord,
        ISet<string> files,
        ISet<string> tools,
        ISet<string> commands)
    {
        AddValue(files, AgentObservabilityData.GetString(eventRecord.Data, "filePath"));
        AddValue(files, AgentObservabilityData.GetString(eventRecord.Data, "path"));
        AddValues(files, AgentObservabilityData.GetStrings(eventRecord.Data, "relatedFiles"));
        foreach (string name in ToolNames)
        {
            AddValue(tools, AgentObservabilityData.GetString(eventRecord.Data, name));
        }

        AddValues(tools, AgentObservabilityData.GetStrings(eventRecord.Data, "relatedToolCalls"));
        AddValue(commands, AgentObservabilityData.GetString(eventRecord.Data, "command"));
        AddValues(commands, AgentObservabilityData.GetStrings(eventRecord.Data, "relatedCommands"));
    }

    private static void AddValue(ISet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void AddValues(ISet<string> values, IEnumerable<string> source)
    {
        foreach (string value in source)
        {
            AddValue(values, value);
        }
    }

    private static string[] SortedValues(IEnumerable<string> values) =>
        values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool IsSuccessfulEvent(AgentEvent eventRecord) =>
        eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
        eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
        eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase) ||
        AgentObservabilityData.GetBoolean(eventRecord.Data, "recovered");

    private static string ShortLabel(string value)
    {
        const int maximum = 48;
        return value.Length <= maximum
            ? value
            : AgentObservabilityData.BoundText(value, maximum);
    }

    private static void Notify(
        IEnumerable<Action<AgentObservabilityUiUpdate>> handlers,
        AgentObservabilityUiUpdate update)
    {
        foreach (Action<AgentObservabilityUiUpdate> handler in handlers)
        {
            try
            {
                handler(update);
            }
            catch
            {
                // A view subscriber cannot interrupt runtime event capture.
            }
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentObservabilityUi));
        }
    }

    private sealed class AgentIssueRowBuilder
    {
        private readonly IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents;

        public AgentIssueRowBuilder(
            IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents)
        {
            this.agents = agents;
        }

        public AgentObservabilityIssueRow Build(AgentIssue issue, bool selected)
        {
            return new(
                issue,
                agents.TryGetValue(
                        new AgentObservabilityAgentIdentity(issue.RunId, issue.AgentId),
                        out AgentSnapshot? agent)
                    ? AgentDisplayName(agent)
                    : issue.ModId,
                agent?.Status,
                selected);
        }
    }

    private sealed class AgentEventSequenceComparer : IComparer<AgentEvent>
    {
        public static readonly AgentEventSequenceComparer Instance = new();

        public int Compare(AgentEvent? left, AgentEvent? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int result = left.Sequence.CompareTo(right.Sequence);
            return result != 0
                ? result
                : string.CompareOrdinal(left.Id, right.Id);
        }
    }

    private sealed class UiSubscription : IDisposable
    {
        private readonly AgentObservabilityUi owner;
        private readonly Action<AgentObservabilityUiUpdate> handler;
        private int disposed;

        public UiSubscription(
            AgentObservabilityUi owner,
            Action<AgentObservabilityUiUpdate> handler)
        {
            this.owner = owner;
            this.handler = handler;
        }

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
