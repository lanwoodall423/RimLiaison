using RimContext.Core.Impact;
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
    Recommendations,
    Agent,
    Issue,
    Content
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
    bool HasUnresolvedError = false,
    string? EntityType = null,
    string? CanonicalEntityId = null);

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
public sealed record AgentObservabilityProductionEntry(
    string Key,
    string ModId,
    string ModName,
    string AgentId,
    string? LogicalAgentId,
    string RunId,
    string SessionId,
    string WorkloadKind,
    string ToolchainState,
    string? QualificationProfile,
    DevelopmentStage CurrentStage,
    string? CurrentOperation,
    AgentStatus Status,
    string BlockingState,
    long ElapsedMilliseconds,
    long? LatestTimestamp,
    string? LatestEvent,
    string? CompletionResult,
    bool IsHistorical);

public sealed record AgentObservabilityIssueOccurrence(
    AgentIssue Issue,
    string ModName,
    AgentStatus? AgentStatus);


public sealed record AgentObservabilityAllView(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AgentObservabilityActivityRow> Activity,
    bool HasMoreActivity,
    long? LatestSequence,
    string? EmptyState = null)
{
    public IReadOnlyList<AgentObservabilityProductionEntry> Production { get; init; } = [];
}

public sealed record AgentObservabilityIssueRow(
    AgentIssue Issue,
    string ModName,
    AgentStatus? AgentStatus,
    bool Selected)
{
    public string State => Issue.Recovered ? "recovered" : "unresolved";

    public string StateLabel => Issue.Recovered ? "Recovered" : "Unresolved";

    public IReadOnlyList<AgentObservabilityIssueOccurrence> Occurrences { get; init; } = [];

    public int OccurrenceCount =>
        Occurrences.Count == 0
            ? Math.Max(1, Issue.Occurrences)
            : Math.Max(
                Issue.Occurrences,
                Occurrences.Sum(static occurrence => Math.Max(1, occurrence.Issue.Occurrences)));

    public int SharedAgentCount { get; init; }

    public AgentObservabilitySharedToolingHint? SharedTooling { get; init; }

    public string CategoryLabel =>
        Issue.Category switch
        {
            AgentIssueCategory.ModDefect => "Mod defect",
            AgentIssueCategory.ToolingFailure => Issue.Recovered
                ? "Recovered infrastructure incident"
                : "Tooling/infrastructure incident",
            AgentIssueCategory.OptionalValidationUnavailable => "Optional validation unavailable",
            AgentIssueCategory.CapabilityGap => "Required-validation blocker",
            _ when Issue.Blocking => "Required-validation blocker",
            _ => Issue.Category.ToString()
        };
}

public sealed record AgentObservabilityRecommendationRow(
    AgentIssue Issue,
    string ModName,
    string? Owner,
    string? Recommendation,
    string Status,
    bool ProductionAffected)
{
    public IReadOnlyList<AgentObservabilityIssueOccurrence> Occurrences { get; init; } = [];

    public int OccurrenceCount =>
        Occurrences.Count == 0
            ? Math.Max(1, Issue.Occurrences)
            : Math.Max(
                Issue.Occurrences,
                Occurrences.Sum(static occurrence => Math.Max(1, occurrence.Issue.Occurrences)));

    public int SharedAgentCount { get; init; }
}

public sealed record AgentObservabilityRecommendationsView(
    IReadOnlyList<AgentObservabilityRecommendationRow> Recommendations,
    int NewCount,
    int ResolvedCount,
    bool HasMore,
    string? EmptyState = null);
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

public sealed record AgentObservabilityValidationItem(
    string Value,
    bool AgentAdded = false);

public sealed record AgentObservabilityLearningItem(
    string RelationshipId,
    string FromIdentity,
    string ToIdentity,
    string Scope,
    string? Project,
    string? Evidence,
    bool PromotedGlobal,
    bool Invalidated);

public sealed record AgentObservabilityEfficiencyMetrics(
    long? PacketGenerationMilliseconds,
    int? PacketBytes,
    long? ValidationMilliseconds,
    int ValidationRecipes,
    int RuntimeValidations,
    int BroadFallbacks,
    int ValidationFailures,
    int StaleEvidenceRejections,
    int ValidationReplans,
    int DeduplicatedRequirements,
    int DeepExpansions,
    bool? PacketUsable);

public sealed record AgentObservabilityExecutionImpact(
    string? PacketId,
    string? PacketStatus,
    string? SourceRevision,
    string? WorkspaceIdentity,
    string? IndexGeneration,
    IReadOnlyList<string> PredictedFiles,
    IReadOnlyList<string> ActualFiles,
    IReadOnlyList<string> DirectImpacts,
    IReadOnlyList<string> DeclaredImpacts,
    IReadOnlyList<string> RuntimeImpacts,
    IReadOnlyList<string> FrameworkImpacts,
    IReadOnlyList<string> DynamicImpacts,
    IReadOnlyList<string> LearnedImpacts,
    IReadOnlyList<AgentObservabilityValidationItem> RequiredValidation,
    IReadOnlyList<AgentObservabilityValidationItem> AgentValidation,
    string? ValidationTier,
    IReadOnlyList<AgentObservabilityLearningItem> Learning,
    AgentObservabilityEfficiencyMetrics Metrics,
    string? EmptyState = null);

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
    AgentObservabilityEventDetail? SelectedEvent = null,
    AgentObservabilityExecutionImpact? ExecutionImpact = null)
{
    [JsonIgnore]
    public IReadOnlyList<AgentEvent> FailureEvents => RecentActivity
        .Select(row => row.Event)
        .Where(eventRecord => eventRecord.Type is
            AgentEventTypes.FailureDetected or AgentEventTypes.DiagnosisProduced)
        .ToArray();

    [JsonIgnore]
    public IReadOnlyList<AgentEvent> RemediationEvents => RecentActivity
        .Select(row => row.Event)
        .Where(eventRecord => eventRecord.Type is
            AgentEventTypes.RemediationValidated or
            AgentEventTypes.RemediationPrecedentStored or
            AgentEventTypes.RemediationPrecedentReused or
            AgentEventTypes.RemediationPrecedentEligibilityChanged)
        .ToArray();

    [JsonIgnore]
    public IReadOnlyList<AgentEvent> EvidenceReuseEvents => RecentActivity
        .Select(row => row.Event)
        .Where(eventRecord => eventRecord.Type == AgentEventTypes.EvidenceReused)
        .ToArray();
}

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
    string ResolutionState,
    string? TraceId,
    IReadOnlyList<string> SpanIds,
    string? FocusEventId)
{
    public AgentObservabilityIssueTriage? Triage { get; init; }
    public IReadOnlyList<AgentObservabilityIssueOccurrence> Occurrences { get; init; } = [];
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

    [JsonPropertyName("recommendations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentObservabilityRecommendationsView? Recommendations { get; init; }
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

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentIntelligenceObservabilityView? Content { get; init; }

    [JsonPropertyName("emptyState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmptyState { get; init; }
}
public sealed record AgentObservabilityUiFilter(
    string? Query = null,
    AgentIssueCategory? IssueCategory = null,
    bool? Blocking = null);

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
    private AgentObservabilityIssuesView? cachedIssuesView;
    private AgentObservabilityRecommendationsView? cachedRecommendationsView;
    private AgentObservabilityUiNavigationModel? cachedNavigation;
    private long cachedNavigationRevision = -1;
    private AgentObservabilityAllView? cachedAllView;
    private long cachedAllRevision = -1;
    private long cachedRecommendationProjectionRevision = -1;
    private int visibleIssueLimit;
    private AgentObservabilityUiRoute route = new(AgentObservabilityUiView.All);
    private HashSet<string> selectedIssueIds = new(StringComparer.Ordinal);
    private string? selectedIssueId;
    private string? selectedEventId;
    private AgentObservabilityIssueMode issueMode = AgentObservabilityIssueMode.Details;
    private AgentObservabilityAgentDetailTab agentDetailTab = AgentObservabilityAgentDetailTab.Event;
    private AgentDiagnosticBundle? assessment;
    private bool includeRecovered = true;
    private AgentObservabilityUiFilter filter = new();
    private bool delayed;
    private string? streamMessage;
    private long revision;
    private long issueProjectionRevision;
    private long issueSelectionRevision;
    private long issueSignatureComputations;
    private long cachedIssueProjectionRevision = -1;
    private long cachedIssueSelectionRevision = -1;
    private long contentProjectionRevision;
    private long cachedContentProjectionRevision = -1;
    private ContentIntelligenceObservabilityView? cachedContentView;
    private string? selectedContentBlueprintId;
    private bool cachedIncludeRecovered;
    private int cachedVisibleIssueLimit;
    private int disposed;
    private int liveRefreshQueued;
    private long liveRefreshLastStarted = long.MinValue;
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

    public AgentObservabilityUiSnapshot ShowRecommendations()
    {
        lock (gate)
        {
            selectedEventId = null;
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        return Navigate(new AgentObservabilityUiRoute(AgentObservabilityUiView.Recommendations));
    }

    public AgentObservabilityUiSnapshot ShowContent()
    {
        lock (gate)
        {
            selectedContentBlueprintId = null;
            selectedEventId = null;
            assessment = null;
            issueMode = AgentObservabilityIssueMode.Details;
        }

        return Navigate(new AgentObservabilityUiRoute(AgentObservabilityUiView.Content));
    }

    public AgentObservabilityUiSnapshot SelectContentBlueprint(string? blueprintId)
    {
        lock (gate)
        {
            ThrowIfDisposedLocked();
            selectedContentBlueprintId = string.IsNullOrWhiteSpace(blueprintId)
                ? null
                : blueprintId.Trim();
            revision++;
        }

        return Snapshot;
    }
    public AgentObservabilityUiSnapshot SetFilter(AgentObservabilityUiFilter nextFilter)
    {
        ArgumentNullException.ThrowIfNull(nextFilter);
        lock (gate)
        {
            ThrowIfDisposedLocked();
            filter = nextFilter with
            {
                Query = string.IsNullOrWhiteSpace(nextFilter.Query)
                    ? null
                    : nextFilter.Query.Trim()
            };
            revision++;
            issueProjectionRevision++;
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

            AgentSnapshot? visibleAgent = route.View == AgentObservabilityUiView.Agent
                ? FindAgentLocked(route.AgentId ?? string.Empty, route.RunId)
                : null;
            if (visibleAgent is not null &&
                !string.Equals(
                    EntityGroupKey(visibleAgent),
                    EntityGroupKey(eventRecord),
                    StringComparison.OrdinalIgnoreCase))
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
            cachedRecommendationsView = null;
            cachedNavigation = null;
            cachedAllView = null;
            selectedEventId = null;
            assessment = null;
        }
    }

    private void RefreshLiveStore()
    {
        if (Volatile.Read(ref disposed) != 0 ||
            store is not IAgentObservabilityLiveStore liveStore ||
            store is IAgentObservabilityHydrationStore
            {
                UseWatcherForLiveRefresh: true,
                InitialHydrationPending: true
            })
        {
            return;
        }
        long now = nowMilliseconds();
        long previous = Volatile.Read(ref liveRefreshLastStarted);
        if ((previous != long.MinValue && now - previous < 1_000) ||
            Interlocked.CompareExchange(
                ref liveRefreshLastStarted,
                now,
                previous) != previous)
        {
            return;
        }

        if (Interlocked.Exchange(ref liveRefreshQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
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
            finally
            {
                Interlocked.Exchange(ref liveRefreshQueued, 0);
            }
        });
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
            case AgentObservabilityUiView.Recommendations:
                snapshot = snapshot with
                {
                    Recommendations = BuildRecommendationsViewLocked()
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
            case AgentObservabilityUiView.Content:
                snapshot = snapshot with
                {
                    Content = BuildContentViewLocked()
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
                snapshot.Recommendations?.EmptyState ??
                snapshot.Agent?.EmptyState ??
                snapshot.Content?.EmptyState ??
                (snapshot.Issue is null && route.View == AgentObservabilityUiView.Issue
                    ? "Issue not found."
                    : null) ??
                (snapshot.Agent is null && route.View == AgentObservabilityUiView.Agent
                    ? "Mod or agent not found."
                    : null)
        };
    }

    private ContentIntelligenceObservabilityView BuildContentViewLocked()
    {
        if (cachedContentView is not null &&
            cachedContentProjectionRevision == contentProjectionRevision &&
            string.Equals(cachedContentView.SelectedBlueprintId, selectedContentBlueprintId, StringComparison.Ordinal))
        {
            return cachedContentView;
        }

        cachedContentView = ContentIntelligenceObservabilityProjection.Build(
            events,
            selectedContentBlueprintId,
            options.MaximumRecentActivityRows,
            options.MaximumIndexedAgents);
        cachedContentProjectionRevision = contentProjectionRevision;
        return cachedContentView;
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
                route.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue),
            new(
                "recommendations",
                "recommendations",
                "Recommendations",
                "Recommendations",
                route.View == AgentObservabilityUiView.Recommendations),
            new(
                "content",
                "content",
                "Content Intelligence",
                "Content Intelligence",
                route.View == AgentObservabilityUiView.Content)
        };
        AgentSnapshot? selectedAgent = route.View == AgentObservabilityUiView.Agent &&
            route.AgentId is not null
                ? FindAgentLocked(route.AgentId, route.RunId)
                : null;
        string? selectedGroupKey = selectedAgent is null
            ? null
            : EntityGroupKey(selectedAgent);
        IEnumerable<NavigationAgentGroup> modAgents = agents.Values
            .Where(agent =>
                MatchesRun(agent.RunId) &&
                MatchesFilterLocked(agent) &&
                CanAppearInTopLevelNavigation(agent))
            .GroupBy(EntityGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                AgentSnapshot[] groupAgents = group.ToArray();
                bool hasUnresolvedError = issues.Values.Any(issue =>
                    !issue.Recovered &&
                    issue.Severity == AgentIssueSeverity.Error &&
                    string.Equals(
                        EntityGroupKey(issue),
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
                "entity-group:" + group.Key,
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
                group.HasUnresolvedError,
                agent.EntityType,
                agent.CanonicalEntityId));
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
            .Where(agent => MatchesRun(agent.RunId) && MatchesFilterLocked(agent))
            .OrderByDescending(static agent => agent.StartTime)
            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
            .ToArray();
        AgentEvent[] visibleEvents = events
            .Where(eventRecord => MatchesRun(eventRecord.RunId) && MatchesFilterLocked(eventRecord))
            .ToArray();
        bool hasMore = visibleEvents.Length > options.MaximumActivityRows;
        AgentEvent[] boundedEvents = visibleEvents
            .OrderByDescending(static eventRecord => AgentObservabilityTime.SortValue(eventRecord.Timestamp))
            .ThenByDescending(static eventRecord => eventRecord.Sequence)
            .ThenBy(static eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .Take(options.MaximumActivityRows)
            .ToArray();
        HashSet<string> visibleEventIds = boundedEvents
            .Select(static eventRecord => eventRecord.Id)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<string>> issueIdsByEvent =
            BuildIssueEventIndexLocked(visibleEventIds);
        AgentObservabilityActivityRow[] rows = boundedEvents
            .Select(eventRecord => ToActivityRowLocked(eventRecord, issueIdsByEvent))
            .ToArray();
        cachedAllView = new AgentObservabilityAllView(
            visibleAgents,
            rows,
            hasMore,
            rows.Length == 0 ? null : rows.Max(static row => row.Sequence),
            visibleAgents.Length == 0
                ? "No mod agents have reported activity yet."
                : rows.Length == 0
                    ? "Agents are registered; activity has not arrived yet."
                    : null)
        {
            Production = BuildProductionEntriesLocked(visibleAgents, visibleEvents)
        };
        cachedAllRevision = revision;
        return cachedAllView;

    }
    private IReadOnlyList<AgentObservabilityProductionEntry> BuildProductionEntriesLocked(
        IReadOnlyList<AgentSnapshot> visibleAgents,
        IReadOnlyList<AgentEvent> visibleEvents)
    {
        AgentSnapshot[] productionAgents = visibleAgents
            .Where(CanAppearInTopLevelNavigation)
            .ToArray();
        HashSet<string> productionGroups = productionAgents
            .Select(EntityGroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AgentEvent[] productionEvents = visibleEvents
            .Where(eventRecord => productionGroups.Contains(EntityGroupKey(eventRecord)))
            .ToArray();
        Dictionary<string, AgentEvent> latestByGroup = productionEvents
            .GroupBy(eventRecord => EntityGroupKey(eventRecord), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(static value => AgentObservabilityTime.SortValue(value.Timestamp))
                    .ThenByDescending(static value => value.Sequence)
                    .ThenBy(static value => value.Id, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        return productionAgents
            .GroupBy(EntityGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                AgentSnapshot representative = PreferredAgent(group.ToArray());
                latestByGroup.TryGetValue(group.Key, out AgentEvent? latest);
                long? latestTimestamp = MaxTimestamp(
                    latest?.Timestamp,
                    representative.StartTime,
                    representative.CompletedAt);
                foreach (AgentSnapshot candidate in group)
                {
                    latestTimestamp = MaxTimestamp(latestTimestamp, candidate.StartTime, candidate.CompletedAt);
                }

                return new AgentObservabilityProductionEntry(
                    group.Key,
                    representative.ModId,
                    representative.ModName,
                    representative.AgentId,
                    representative.LogicalAgentId,
                    representative.RunId,
                    representative.SessionId,
                    representative.WorkloadKind,
                    representative.ToolchainState,
                    representative.QualificationProfile,
                    representative.CurrentStage,
                    representative.CurrentOperation ?? representative.CurrentActivity,
                    representative.Status,
                    representative.BlockingState,
                    ElapsedMilliseconds(representative),
                    latestTimestamp,
                    latest?.Summary,
                    representative.CompletionResult,
                    representative.Status == AgentStatus.Completed);
            })
            .OrderByDescending(static entry => AgentObservabilityTime.SortValue(entry.LatestTimestamp))
            .ThenBy(static entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
                MatchesFilterLocked(issue) &&
                (includeRecovered || !issue.Recovered))
            .ToArray();
        AgentIssue[][] groups = candidates
            .GroupBy(IssueGroupKeyLocked, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(static issue => AgentObservabilityTime.SortValue(issue.Timestamp))
                .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                .ToArray())
            .OrderByDescending(static group => AgentObservabilityTime.SortValue(group[0].Timestamp))
            .ThenBy(static group => group[0].Id, StringComparer.Ordinal)
            .ToArray();
        int recoveredCount = candidates.Count(static issue => issue.Recovered);
        int unresolvedCount = candidates.Length - recoveredCount;
        bool hasMore = groups.Length > visibleIssueLimit;
        AgentIssueRowBuilder rowBuilder = new(agents);
        AgentObservabilityIssueRow[] rows = groups
            .Take(visibleIssueLimit)
            .Select(group =>
            {
                AgentIssue parent = group[0];
                AgentObservabilityIssueRow row = rowBuilder.Build(
                    parent,
                    selectedIssueIds.Contains(parent.Id));
                AgentObservabilitySharedToolingHint? shared =
                    GetSharedToolingHintLocked(parent);
                return row with
                {
                    Occurrences = group.Select(ToIssueOccurrenceLocked).ToArray(),
                    SharedAgentCount = shared is not null
                        ? shared.AffectedAgentCount
                        : CountKnownAgentIdentitiesLocked(group) > 1
                            ? CountKnownAgentIdentitiesLocked(group)
                            : 0,
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
                    ? $"Showing the first {rows.Length} issue groups. Load more to inspect older history."
                    : null);
        cachedIssueProjectionRevision = issueProjectionRevision;
        cachedIssueSelectionRevision = issueSelectionRevision;
        cachedIncludeRecovered = includeRecovered;
        cachedVisibleIssueLimit = visibleIssueLimit;
        return cachedIssuesView;
    }
    private AgentObservabilityRecommendationsView BuildRecommendationsViewLocked()
    {
        if (cachedRecommendationsView is not null &&
            cachedRecommendationProjectionRevision == issueProjectionRevision)
        {
            return cachedRecommendationsView;
        }
        AgentIssue[] candidates = issues.Values
            .Where(issue => MatchesRun(issue.RunId) && MatchesFilterLocked(issue) &&
                (issue.Recommendation is not null ||
                 issue.Category is AgentIssueCategory.ToolingImprovement or
                    AgentIssueCategory.OptionalValidationUnavailable or
                    AgentIssueCategory.ToolLimitation))
            .ToArray();
        AgentIssue[][] allGroups = candidates
            .GroupBy(AgentObservabilityRecordIdentity.ForRecommendation, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(static issue => AgentObservabilityTime.SortValue(issue.Timestamp))
                .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
                .ToArray())
            .OrderByDescending(static group => AgentObservabilityTime.SortValue(group[0].Timestamp))
            .ThenBy(static group => group[0].Id, StringComparer.Ordinal)
            .ToArray();
        AgentIssue[][] groups = allGroups
            .Take(options.MaximumIssueRows)
            .ToArray();
        AgentObservabilityRecommendationRow[] rows = groups
            .Select(group =>
            {
                AgentIssue issue = group[0];
                AgentSnapshot? agent = FindAgentLocked(issue.AgentId, issue.RunId);
                bool affected = issue.Blocking ||
                    issue.Category == AgentIssueCategory.OptionalValidationUnavailable &&
                    issue.ValidationClassification is "REQUIRED";
                return new AgentObservabilityRecommendationRow(
                    issue,
                    agent?.ModName ?? issue.ModId,
                    issue.ComponentOwner ?? issue.ProbableOwner,
                    issue.Recommendation ?? issue.Summary,
                    issue.Recovered ? "resolved" : "new",
                    affected)
                {
                    Occurrences = group.Select(ToIssueOccurrenceLocked).ToArray(),
                    SharedAgentCount = CountKnownAgentIdentitiesLocked(group)
                };
            })
            .ToArray();
        cachedRecommendationsView = new AgentObservabilityRecommendationsView(
            rows,
            rows.Count(row => row.Status == "new"),
            rows.Count(row => row.Status == "resolved"),
            allGroups.Length > rows.Length,
            candidates.Length == 0 ? "No recommendations have been recorded." : null);
        cachedRecommendationProjectionRevision = issueProjectionRevision;
        return cachedRecommendationsView;
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
            .Where(eventRecord =>
                MatchesRun(eventRecord.RunId) &&
                string.Equals(
                    EntityGroupKey(eventRecord),
                    EntityGroupKey(agent),
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AgentEvent[] recentEvents = agentEvents
            .TakeLast(options.MaximumRecentActivityRows)
            .ToArray();
        AgentEvent? selectedForAgent = selectedEventId is null
            ? null
            : FindEventLocked(selectedEventId);
        if (selectedEventId is not null &&
            (selectedForAgent is null ||
             !string.Equals(
                 EntityGroupKey(selectedForAgent),
                 EntityGroupKey(agent),
                 StringComparison.OrdinalIgnoreCase)))
        {
            selectedEventId = null;
            selectedForAgent = null;
        }
        if (selectedForAgent is not null &&
            string.Equals(
                EntityGroupKey(selectedForAgent),
                EntityGroupKey(agent),
                StringComparison.OrdinalIgnoreCase) &&
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
        AgentEvent[] impactEvents = events
            .Where(eventRecord =>
                agents.TryGetValue(AgentIdentity(eventRecord), out AgentSnapshot? eventAgent) &&
                string.Equals(
                    EntityGroupKey(eventAgent),
                    EntityGroupKey(agent),
                    StringComparison.OrdinalIgnoreCase))
            .TakeLast(Math.Min(options.MaximumSupportingEvents, 2_000))
            .ToArray();
        AgentObservabilityExecutionImpact? executionImpact =
            BuildExecutionImpactLocked(impactEvents);
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
            .Where(issue =>
                MatchesRun(issue.RunId) &&
                string.Equals(
                    EntityGroupKey(issue),
                    EntityGroupKey(agent),
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .ToArray();
        AgentObservabilityEventDetail? selectedEvent = selectedForAgent is not null &&
                string.Equals(
                    EntityGroupKey(selectedForAgent),
                    EntityGroupKey(agent),
                    StringComparison.OrdinalIgnoreCase)
            ? BuildEventDetailLocked(selectedForAgent)
            : null;
        AgentSnapshot[] sessionAgents = agents.Values
            .Where(value =>
                MatchesRun(value.RunId) &&
                string.Equals(EntityGroupKey(value), EntityGroupKey(agent), StringComparison.OrdinalIgnoreCase))
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
            selectedEvent,
            executionImpact);
    }

    private AgentObservabilityExecutionImpact? BuildExecutionImpactLocked(
        IReadOnlyList<AgentEvent> impactEvents)
    {
        AgentEvent? packet = LatestImpactEvent(impactEvents, AgentEventTypes.ExecutionPacketGenerated);
        AgentEvent? packetStatus = impactEvents
            .LastOrDefault(eventRecord => eventRecord.Type is
                AgentEventTypes.ExecutionPacketPartiallyInvalidated or
                AgentEventTypes.ExecutionPacketInvalidated);
        AgentEvent? prediction = LatestImpactEvent(impactEvents, AgentEventTypes.PredictedImpactCreated);
        AgentEvent? actual = LatestImpactEvent(impactEvents, AgentEventTypes.ActualImpactCalculated);
        AgentEvent[] plans = impactEvents
            .Where(eventRecord => eventRecord.Type is
                AgentEventTypes.ValidationPlanGenerated or AgentEventTypes.ValidationPlanBroadened)
            .ToArray();
        AgentEvent? plan = plans.LastOrDefault();
        AgentEvent? completed = LatestImpactEvent(impactEvents, AgentEventTypes.ValidationCompleted);
        if (packet is null && packetStatus is null && prediction is null && actual is null &&
            plan is null && completed is null)
        {
            return null;
        }

        IReadOnlyList<string> actualClasses = AgentObservabilityData.GetStrings(
            actual?.Data,
            "actualImpactClasses");
        IReadOnlyList<string> learned = impactEvents
            .Where(eventRecord => eventRecord.Type is
                AgentEventTypes.ImpactRelationshipLearned or
                AgentEventTypes.ImpactRelationshipPromoted)
            .Select(eventRecord =>
            {
                string from = AgentObservabilityData.GetString(eventRecord.Data, "fromIdentity") ?? "unknown";
                string to = AgentObservabilityData.GetString(eventRecord.Data, "toIdentity") ?? "unknown";
                return from + " -> " + to;
            })
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        var learning = impactEvents
            .Where(eventRecord => eventRecord.Type is
                AgentEventTypes.ImpactRelationshipLearned or
                AgentEventTypes.ImpactRelationshipPromoted or
                AgentEventTypes.ImpactRelationshipInvalidated or
                AgentEventTypes.ImpactProjectOverrideApplied)
            .Select(eventRecord => new AgentObservabilityLearningItem(
                AgentObservabilityData.GetString(eventRecord.Data, "relationshipId") ?? eventRecord.Id,
                AgentObservabilityData.GetString(eventRecord.Data, "fromIdentity") ?? "unknown",
                AgentObservabilityData.GetString(eventRecord.Data, "toIdentity") ?? "unknown",
                AgentObservabilityData.GetString(eventRecord.Data, "scope") ??
                    (eventRecord.Type == AgentEventTypes.ImpactRelationshipInvalidated ? "override" : "project"),
                AgentObservabilityData.GetString(eventRecord.Data, "project"),
                AgentObservabilityData.GetString(eventRecord.Data, "evidenceId") ??
                    string.Join(",", AgentObservabilityData.GetStrings(eventRecord.Data, "evidenceIds")),
                eventRecord.Type == AgentEventTypes.ImpactRelationshipPromoted,
                eventRecord.Type == AgentEventTypes.ImpactRelationshipInvalidated))
            .Take(64)
            .ToArray();
        AgentObservabilityValidationItem[] required = plan is null
            ? []
            : AgentObservabilityData.GetStrings(plan.Data, "requiredItems")
                .Select(value => new AgentObservabilityValidationItem(value))
                .ToArray();
        AgentObservabilityValidationItem[] agentValidation =
            impactEvents
                .Where(eventRecord => eventRecord.Type == AgentEventTypes.AgentValidationAdded)
                .SelectMany(eventRecord => AgentObservabilityData.GetStrings(eventRecord.Data, "addedTestIds"))
                .Distinct(StringComparer.Ordinal)
                .Select(value => new AgentObservabilityValidationItem(value, true))
                .Take(64)
                .ToArray();
        int deduplicated = plan is null
            ? 0
            : (int)(AgentObservabilityData.GetInt64(plan.Data, "deduplicatedRequirements") ?? 0);
        int validationRecipes = completed is null
            ? 0
            : AgentObservabilityData.GetStrings(completed.Data, "validationRecipeIds").Count;
        IReadOnlyList<string> actualConcerns = AgentObservabilityData.GetStrings(
            actual?.Data,
            "actualConcerns");
        IReadOnlyList<string> runtime = actualConcerns
            .Where(value => value.Contains("runtime", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("harmony", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IReadOnlyList<string> framework = actualClasses
            .Where(value => value.Contains("framework", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IReadOnlyList<string> dynamic = actualClasses
            .Where(value => value.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("potential", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (dynamic.Count == 0 &&
            AgentObservabilityData.GetBoolean(actual?.Data, "harmonyOrDynamicRisk"))
        {
            dynamic = ["harmony/dynamic risk"];
        }
        int broadFallbacks = plans.Count(eventRecord =>
            string.Equals(
                AgentObservabilityData.GetString(eventRecord.Data, "validationPlanTier"),
                ValidationPlanTiers.BroaderCanonical,
                StringComparison.Ordinal));
        string? packetId = AgentObservabilityData.GetString(packetStatus?.Data, "packetId") ??
            AgentObservabilityData.GetString(packet?.Data, "packetId");
        string? packetStatusValue = AgentObservabilityData.GetString(packetStatus?.Data, "packetStatus") ??
            AgentObservabilityData.GetString(packet?.Data, "packetStatus");
        var metrics = new AgentObservabilityEfficiencyMetrics(
            packet is null
                ? null
                : AgentObservabilityData.GetInt64(packet.Data, "packetGenerationMilliseconds"),
            packet is null
                ? null
                : (int?)AgentObservabilityData.GetInt64(packet.Data, "packetBytes"),
            completed is null
                ? null
                : AgentObservabilityData.GetInt64(completed.Data, "validationElapsedMilliseconds"),
            validationRecipes,
            runtime.Count,
            broadFallbacks,
            completed is null
                ? 0
                : (int)(AgentObservabilityData.GetInt64(completed.Data, "failed") ?? 0),
            impactEvents.Count(eventRecord => eventRecord.Type == AgentEventTypes.StaleEvidenceRejected),
            Math.Max(0, plans.Length - 1),
            deduplicated,
            impactEvents.Count(eventRecord => eventRecord.Type == AgentEventTypes.ExecutionPacketExpanded),
            packetStatusValue is null
                ? null
                : string.Equals(
                    packetStatusValue,
                    ExecutionPacketStatuses.Valid,
                    StringComparison.Ordinal));
        return new AgentObservabilityExecutionImpact(
            packetId,
            packetStatusValue,
            packet is null
                ? null
                : AgentObservabilityData.GetString(packet.Data, "sourceRevision"),
            packet is null
                ? null
                : AgentObservabilityData.GetString(packet.Data, "workspaceIdentity"),
            packet is null
                ? null
                : AgentObservabilityData.GetString(packet.Data, "indexGeneration"),
            AgentObservabilityData.GetStrings(prediction?.Data, "predictedFiles"),
            AgentObservabilityData.GetStrings(actual?.Data, "actualFiles"),
            AgentObservabilityData.GetStrings(actual?.Data, "directDependents"),
            actualClasses,
            runtime,
            framework,
            dynamic,
            learned,
            required,
            agentValidation,
            plan is null
                ? null
                : AgentObservabilityData.GetString(plan.Data, "validationPlanTier"),
            learning,
            metrics,
            packet is null ? "No Execution Packet has been recorded." : null);
    }

    private static AgentEvent? LatestImpactEvent(
        IReadOnlyList<AgentEvent> events,
        string type) =>
        events.LastOrDefault(eventRecord => eventRecord.Type == type);

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
            Triage = triage,
            Occurrences = issues.Values
                .Where(candidate =>
                    MatchesRun(candidate.RunId) &&
                    string.Equals(
                        IssueGroupKeyLocked(candidate),
                        IssueGroupKeyLocked(issue),
                        StringComparison.Ordinal))
                .OrderByDescending(static candidate => AgentObservabilityTime.SortValue(candidate.Timestamp))
                .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
                .Select(ToIssueOccurrenceLocked)
                .ToArray()
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
            var affected = new Dictionary<string, AgentIssue>(StringComparer.Ordinal);
            foreach (string issueId in issueIds)
            {
                if (!issues.TryGetValue(issueId, out AgentIssue? candidate) ||
                    TrustworthyAgentIdentityLocked(candidate) is not string agentIdentity)
                {
                    continue;
                }

                affected.TryAdd(agentIdentity, candidate);
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
                    issueIds
                        .Select(id => issues.TryGetValue(id, out AgentIssue? value) ? value : null)
                        .Where(static value => value is not null)
                        .Select(static value => value!.ModId)
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
                string.Equals(EntityGroupKey(value), EntityGroupKey(agent), StringComparison.OrdinalIgnoreCase))
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
    private string IssueGroupKeyLocked(AgentIssue issue)
    {
        AgentObservabilityIssueSignature signature = GetIssueSignatureLocked(issue);
        string? structuredFingerprint = signature.ErrorCode is null
            ? null
            : signature.Fingerprint + "|operation|" + issue.OperationKey;
        return AgentObservabilityRecordIdentity.ForIssue(issue, structuredFingerprint);
    }

    private AgentObservabilityIssueOccurrence ToIssueOccurrenceLocked(AgentIssue issue)
    {
        AgentSnapshot? agent = FindAgentLocked(issue.AgentId, issue.RunId);
        return new AgentObservabilityIssueOccurrence(
            issue,
            agent?.ModName ?? issue.ModId,
            agent?.Status);
    }

    private int CountKnownAgentIdentitiesLocked(IEnumerable<AgentIssue> issueRecords) =>
        issueRecords
            .Select(TrustworthyAgentIdentityLocked)
            .Where(static value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private string? TrustworthyAgentIdentityLocked(AgentIssue issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.LogicalAgentId))
        {
            return issue.LogicalAgentId.Trim();
        }

        return agents.TryGetValue(
                new AgentObservabilityAgentIdentity(issue.RunId, issue.AgentId),
                out AgentSnapshot? agent) &&
            !string.IsNullOrWhiteSpace(agent.LogicalAgentId)
                ? agent.LogicalAgentId.Trim()
                : null;
    }

    private static long? MaxTimestamp(params long?[] timestamps)
    {
        long? result = null;
        foreach (long? timestamp in timestamps)
        {
            if (!AgentObservabilityTime.IsValid(timestamp) ||
                (result is not null && timestamp <= result))
            {
                continue;
            }

            result = timestamp;
        }

        return result;
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
                ObservabilityEntityIdentityResolver.Matches(value, agentIdOrModId))
            .ToArray();
        if (modIdMatches.Length > 0)
        {
            return PreferredAgent(modIdMatches);
        }

        AgentSnapshot[] storeMatches = store.GetAgents(
                runId: scopeRunId,
                limit: options.MaximumIndexedAgents)
            .Where(value => ObservabilityEntityIdentityResolver.Matches(value, agentIdOrModId))
            .OrderByDescending(static value => value.StartTime)
            .ToArray();
        if (storeMatches.Length > 0)
        {
            foreach (AgentSnapshot value in storeMatches)
            {
                UpsertAgentLocked(value);
            }

            return PreferredAgent(storeMatches);
        }

        return null;
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
            AgentObservabilityUiView.Recommendations => new(AgentObservabilityUiView.Recommendations),
            AgentObservabilityUiView.Content => new(AgentObservabilityUiView.Content),
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
        if (eventRecord.Type.StartsWith("content.", StringComparison.Ordinal))
        {
            contentProjectionRevision++;
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
    private bool MatchesFilterLocked(AgentSnapshot agent)
    {
        if (!MatchesFilterText(
                filter.Query,
                agent.ModId,
                agent.ModName,
                agent.AgentId,
                agent.LogicalAgentId,
                agent.RunId,
                agent.CurrentStage.ToString(),
                agent.CurrentOperation,
                agent.BlockingState,
                agent.Status.ToString()))
        {
            return false;
        }

        return filter.Blocking is null ||
            (agent.BlockingState == "required") == filter.Blocking.Value;
    }

    private bool MatchesFilterLocked(AgentEvent eventRecord) =>
        MatchesFilterText(
            filter.Query,
            eventRecord.ModId,
            eventRecord.AgentId,
            eventRecord.LogicalAgentId,
            eventRecord.RunId,
            eventRecord.Stage.ToString(),
            eventRecord.Type,
            eventRecord.Summary);

    private bool MatchesFilterLocked(AgentIssue issue)
    {
        if (filter.IssueCategory is not null && issue.Category != filter.IssueCategory)
        {
            return false;
        }

        if (filter.Blocking is not null && issue.Blocking != filter.Blocking.Value)
        {
            return false;
        }

        return MatchesFilterText(
            filter.Query,
            issue.ModId,
            issue.AgentId,
            issue.LogicalAgentId,
            issue.RunId,
            issue.Category.ToString(),
            issue.Summary,
            issue.ComponentOwner,
            issue.Recommendation);
    }

    private static bool MatchesFilterText(string? query, params string?[] values) =>
        string.IsNullOrWhiteSpace(query) ||
        values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private static AgentObservabilityAgentIdentity AgentIdentity(AgentSnapshot agent) =>
        new(agent.RunId, agent.AgentId);

    private static AgentObservabilityAgentIdentity AgentIdentity(AgentEvent eventRecord) =>
        new(eventRecord.RunId, eventRecord.AgentId);

    private static string EntityGroupKey(AgentSnapshot agent) =>
        AgentObservabilityEntityIdentity.GroupKey(agent);

    private static string EntityGroupKey(AgentIssue issue) =>
        AgentObservabilityEntityIdentity.GroupKey(issue);

    private static string EntityGroupKey(AgentEvent eventRecord) =>
        AgentObservabilityEntityIdentity.GroupKey(eventRecord);
    private static bool CanAppearInTopLevelNavigation(AgentSnapshot agent)
    {
        bool eligibleType =
            (string.Equals(agent.EntityType, ObservabilityEntityTypes.Mod, StringComparison.Ordinal) ||
             string.Equals(agent.EntityType, ObservabilityEntityTypes.Tool, StringComparison.Ordinal)) &&
            agent.CanonicalEntityId.StartsWith(
                agent.EntityType + ":",
                StringComparison.Ordinal);
        bool productionWorkload =
            string.Equals(agent.WorkloadKind, "production", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(agent.QualificationProfile);
        return eligibleType && productionWorkload;
    }

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
            .ThenBy(static agent => agent.RunId, StringComparer.Ordinal)
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
