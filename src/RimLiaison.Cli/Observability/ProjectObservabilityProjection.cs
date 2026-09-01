using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Observability;

public static partial class AgentObservabilitySchemas
{
    public const string ToolingAssessment = "rimliaison-tooling-assessment/v1";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectObservabilityStateKind
{
    Healthy,
    Working,
    NeedsAttention,
    Blocked,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectObservabilityCompleteness
{
    Complete,
    Partial,
    Degraded,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolingFindingKind
{
    BlockingFailure,
    RecoveredFailure,
    MissingCapability,
    UnsupportedOperation,
    SuccessfulWorkaround,
    DiagnosticDeficiency,
    ExcessiveRetry,
    ExcessiveWaiting,
    RepeatedInspection,
    RedundantWork,
    InefficientWorkflow,
    Other
}

public sealed record ProjectObservabilityTimelineEntry(
    string EventId,
    long Timestamp,
    long Sequence,
    string Type,
    DevelopmentStage Stage,
    string Summary,
    string Result,
    bool IsMeaningful);

public sealed record ProjectObservabilityAttempt(
    string EventId,
    long Timestamp,
    string Operation,
    string Result,
    string Summary);

public sealed record ProjectObservabilityProblemObservation(
    long Timestamp,
    string Summary,
    string? EventId = null,
    string? IssueId = null,
    bool IsProjectProblem = true);

public sealed record ProjectObservabilitySession(
    string RunId,
    string AgentId,
    string? LogicalAgentId,
    string? SessionId,
    long StartTime,
    AgentStatus Status,
    AgentCompletionState CompletionState,
    bool IsStale);

public sealed record ProjectObservabilityState(
    ObservabilityEntityIdentity Identity,
    string DisplayName,
    ProjectObservabilityStateKind State,
    bool ActionRequired,
    string? CurrentOperation,
    string? LastMeaningfulOperation,
    long? LastMeaningfulActivityAt,
    ProjectObservabilityAttempt? LatestAttempt,
    ProjectObservabilityAttempt? LastSuccessfulValidation,
    ProjectObservabilityProblemObservation? LastFailureOrProblem,
    ProjectObservabilityProblemObservation? CurrentUnresolvedProblem,
    string? ProblemOwner,
    string? ProblemClassification,
    IReadOnlyList<ProjectObservabilitySession> ActiveSessions,
    IReadOnlyList<ProjectObservabilityTimelineEntry> Timeline,
    IReadOnlyList<ToolingFinding> ToolingFindings,
    ProjectObservabilityCompleteness Completeness,
    bool StaleSessionDetected = false)
{
    public bool HasToolingFindings => ToolingFindings.Count > 0;

    public bool HasPartialEvidence => Completeness is
        ProjectObservabilityCompleteness.Partial or
        ProjectObservabilityCompleteness.Degraded or
        ProjectObservabilityCompleteness.Unknown;
}

public sealed record ProjectObservabilityProjectionResult(
    IReadOnlyList<ProjectObservabilityState> Projects,
    IReadOnlyList<ToolingFinding> ToolingFindings,
    ProjectObservabilityCompleteness Completeness,
    IReadOnlyList<string> MissingEvidence);
public sealed record ToolingFindingOccurrence(
    string OccurrenceId,
    string FindingIdentity,
    ToolingFindingKind Kind,
    string Summary,
    long Timestamp,
    string ProjectId,
    string? ProjectDisplayName,
    string? RunId,
    string? AgentId,
    string? LogicalAgentId,
    string? SessionId,
    string? Operation,
    string? Workaround,
    string? ComponentOwner,
    string Confidence,
    IReadOnlyList<string> Provenance,
    IReadOnlyList<string> SupportingEventIds,
    string? ErrorCode = null,
    string? Command = null,
    string? Arguments = null,
    string? Stdout = null,
    string? Stderr = null,
    string? DiagnosticOutput = null,
    IReadOnlyList<string>? RecoveryAttempts = null,
    int RetryCount = 0,
    long? AddedDelayMilliseconds = null,
    int RuntimeLaunches = 0,
    int RepeatedWorkCount = 0,
    int? TokenCount = null,
    string? ValidationImpact = null,
    string? BuildImpact = null,
    string? RuntimeImpact = null,
    IReadOnlyDictionary<string, string>? Versions = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool ProductionWorkFailed = false,
    bool RecoverySucceeded = false,
    bool EvidenceComplete = true,
    IReadOnlyList<string>? MissingEvidence = null,
    bool ComponentOwnerDerived = false,
    IReadOnlyList<string>? EvidenceIds = null,
    IReadOnlyList<string>? ObservedEvidence = null);

public sealed record ToolingFinding(
    string FindingIdentity,
    ToolingFindingKind Kind,
    string Summary,
    string Confidence,
    IReadOnlyList<ToolingFindingOccurrence> Occurrences,
    bool ProductionWorkFailed,
    bool RecoverySucceeded,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<string> AffectedLogicalAgents,
    long FirstObservedAt,
    long LastObservedAt,
    IReadOnlyList<string> MissingEvidence,
    int? TotalOccurrenceCount = null)
{
    public int OccurrenceCount => TotalOccurrenceCount ?? Occurrences.Count;
}

public sealed record ToolingAssessmentFinding(
    string FindingIdentity,
    string Kind,
    string Summary,
    string Confidence,
    IReadOnlyList<string> ObservedFacts,
    string? DerivedInterpretation,
    IReadOnlyList<string> SuggestedInvestigationAreas,
    bool ProductionWorkFailed,
    bool RecoverySucceeded,
    string? Workaround,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> LikelyComponentOwners,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> SupportingEvidenceIds,
    IReadOnlyList<string> RecoveryAttempts,
    int? RetryCount,
    long? AddedDelayMilliseconds,
    int? RuntimeLaunches,
    int? RepeatedWorkCount,
    IReadOnlyList<int> MeasuredTokenCounts,
    IReadOnlyList<string> ValidationImpact,
    IReadOnlyList<string> BuildImpact,
    IReadOnlyList<string> RuntimeImpact,
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyDictionary<string, string> Environment,
    string EvidenceCompleteness,
    IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyList<string>? StandardOutput = null,
    IReadOnlyList<string>? StandardError = null,
    IReadOnlyList<string>? Diagnostics = null,
    IReadOnlyList<string>? EvidenceIds = null,
    IReadOnlyList<string>? SupportingEventIds = null);

public sealed record ToolingAssessmentAggregate(
    string FindingIdentity,
    int OccurrenceCount,
    int ProjectCount,
    int LogicalAgentCount,
    long FirstObservedAt,
    long LastObservedAt);

public sealed record ToolingAssessment(
    string SchemaVersion,
    string ExecutiveSummary,
    IReadOnlyList<ToolingAssessmentFinding> Findings,
    IReadOnlyList<ToolingAssessmentAggregate> Recurrence,
    bool ProductionWorkFailed,
    bool RecoverySucceeded,
    string EvidenceCompleteness,
    IReadOnlyList<string> MissingEvidence);

public sealed record ProjectObservabilityProjectionOptions(
    long NowMilliseconds,
    TimeSpan WorkingStalenessThreshold,
    int MaximumTimelineEntries = 5_000,
    int MaximumFindingOccurrences = 5_000,
    bool HistoryComplete = true,
    bool HistoryDegraded = false)
{
    public static ProjectObservabilityProjectionOptions Default(long nowMilliseconds) =>
        new(nowMilliseconds, TimeSpan.FromMinutes(5));
}

/// <summary>
/// The deterministic owner-facing projection. It is the only place that decides
/// project state, activity semantics, and tooling-finding aggregation.
/// </summary>
public static class ProjectObservabilityProjection
{
    private static readonly HashSet<string> AttemptTypes = new(StringComparer.Ordinal)
    {
        AgentEventTypes.CommandCompleted,
        AgentEventTypes.CommandFailed,
        AgentEventTypes.CommandTimeout,
        AgentEventTypes.BuildSucceeded,
        AgentEventTypes.BuildFailed,
        AgentEventTypes.TestPassed,
        AgentEventTypes.TestFailed,
        AgentEventTypes.SuiteCompleted,
        AgentEventTypes.ValidationCompleted,
        AgentEventTypes.PackagingCompleted,
        AgentEventTypes.AgentCompleted,
        AgentEventTypes.AgentFailed,
        AgentEventTypes.ToolCompleted,
        AgentEventTypes.ToolFailed,
        AgentEventTypes.ToolException
    };

    private static readonly HashSet<string> ValidationTypes = new(StringComparer.Ordinal)
    {
        AgentEventTypes.TestPassed,
        AgentEventTypes.SuiteCompleted,
        AgentEventTypes.ValidationCompleted
    };

    public static ProjectObservabilityProjectionResult Build(
        AgentObservabilityView source,
        ProjectObservabilityProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumTimelineEntries <= 0 || options.MaximumFindingOccurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        AgentSnapshot[] eligibleAgents = source.Agents
            .Where(IsProductionProject)
            .ToArray();
        Dictionary<string, List<AgentSnapshot>> agentsByProject = eligibleAgents
            .GroupBy(ProjectKey)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        Dictionary<string, string> sessionProjects = eligibleAgents
            .GroupBy(SessionKey, StringComparer.Ordinal)
            .Where(group => group.Select(ProjectKey).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Select(ProjectKey).First(),
                StringComparer.Ordinal);
        Dictionary<string, List<AgentEvent>> eventsByProject = agentsByProject.Keys
            .ToDictionary(key => key, _ => new List<AgentEvent>(), StringComparer.Ordinal);
        foreach (AgentEvent eventRecord in source.Events)
        {
            if (sessionProjects.TryGetValue(SessionKey(eventRecord), out string? projectKey) &&
                eventsByProject.TryGetValue(projectKey, out List<AgentEvent>? projectEvents))
            {
                projectEvents.Add(eventRecord);
            }
        }

        Dictionary<string, List<AgentIssue>> issuesByProject = agentsByProject.Keys
            .ToDictionary(key => key, _ => new List<AgentIssue>(), StringComparer.Ordinal);
        foreach (AgentIssue issue in source.Issues)
        {
            if (sessionProjects.TryGetValue(SessionKey(issue), out string? projectKey) &&
                issuesByProject.TryGetValue(projectKey, out List<AgentIssue>? projectIssues))
            {
                projectIssues.Add(issue);
            }
        }

        Dictionary<string, ToolingFinding> aggregateFindings = BuildToolingFindings(
            source,
            sessionProjects,
            agentsByProject,
            options.MaximumFindingOccurrences);
        var projects = new List<ProjectObservabilityState>(agentsByProject.Count);
        foreach ((string projectKey, List<AgentSnapshot> projectAgents) in agentsByProject)
        {
            List<AgentEvent> projectEvents = eventsByProject[projectKey];
            List<AgentIssue> projectIssues = issuesByProject[projectKey];
            ToolingFinding[] tooling = aggregateFindings.Values
                .Where(finding => finding.Occurrences.Any(occurrence => occurrence.ProjectId == projectKey))
                .OrderByDescending(finding => finding.LastObservedAt)
                .ThenBy(finding => finding.FindingIdentity, StringComparer.Ordinal)
                .ToArray();
            projects.Add(BuildProject(
                projectAgents,
                projectEvents,
                projectIssues,
                tooling,
                options));
        }

        ProjectObservabilityCompleteness completeness = options.HistoryDegraded
            ? ProjectObservabilityCompleteness.Degraded
            : options.HistoryComplete
                ? ProjectObservabilityCompleteness.Complete
                : ProjectObservabilityCompleteness.Partial;
        string[] missingEvidence = options.HistoryDegraded
            ? ["Persisted observability history is degraded; older records may be unavailable."]
            : options.HistoryComplete
                ? []
                : ["Persisted observability history is incomplete; state is not evidence of absent activity."];
        return new ProjectObservabilityProjectionResult(
            projects.OrderByDescending(project => AgentObservabilityTime.SortValue(project.LastMeaningfulActivityAt))
                .ThenBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.Identity.CanonicalEntityId, StringComparer.Ordinal)
                .ToArray(),
            aggregateFindings.Values
                .OrderByDescending(finding => finding.LastObservedAt)
                .ThenBy(finding => finding.FindingIdentity, StringComparer.Ordinal)
                .ToArray(),
            completeness,
            missingEvidence);
    }

    public static ToolingAssessment BuildAssessment(
        ToolingFindingOccurrence occurrence,
        ProjectObservabilityCompleteness completeness,
        IEnumerable<string>? missingEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ToolingFinding finding = new(
            occurrence.FindingIdentity,
            occurrence.Kind,
            occurrence.Summary,
            occurrence.Confidence,
            [occurrence],
            occurrence.ProductionWorkFailed,
            occurrence.RecoverySucceeded,
            [occurrence.ProjectId],
            string.IsNullOrWhiteSpace(occurrence.LogicalAgentId)
                ? []
                : [occurrence.LogicalAgentId],
            occurrence.Timestamp,
            occurrence.Timestamp,
            occurrence.MissingEvidence ?? [],
            1);
        return BuildAssessment([finding], completeness, missingEvidence);
    }

    public static ToolingAssessment BuildAssessment(
        IEnumerable<ToolingFinding> findings,
        ProjectObservabilityCompleteness completeness,
        IEnumerable<string>? missingEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ToolingFinding[] values = findings
            .Where(static finding => finding is not null)
            .OrderByDescending(finding => finding.LastObservedAt)
            .ThenBy(finding => finding.FindingIdentity, StringComparer.Ordinal)
            .ToArray();
        string[] missing = (missingEvidence ?? [])
            .Concat(values.SelectMany(static finding => finding.MissingEvidence))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return new ToolingAssessment(
            AgentObservabilitySchemas.ToolingAssessment,
            values.Length == 0
                ? "No deterministic tooling findings were observed."
                : $"{values.Length} tooling finding group(s) require engineering assessment; production outcomes remain separate.",
            values.Select(ToAssessmentFinding).ToArray(),
            values.Select(finding => new ToolingAssessmentAggregate(
                    finding.FindingIdentity,
                    finding.OccurrenceCount,
                    finding.AffectedProjects.Count,
                    finding.AffectedLogicalAgents.Count,
                    finding.FirstObservedAt,
                    finding.LastObservedAt))
                .ToArray(),
            values.Any(static finding => finding.ProductionWorkFailed),
            values.Any(static finding => finding.RecoverySucceeded),
            completeness == ProjectObservabilityCompleteness.Complete && missing.Length == 0
                ? ProjectObservabilityCompleteness.Complete.ToString()
                : completeness == ProjectObservabilityCompleteness.Degraded
                    ? ProjectObservabilityCompleteness.Degraded.ToString()
                    : ProjectObservabilityCompleteness.Partial.ToString(),
            missing);
    }

    private static ProjectObservabilityState BuildProject(
        IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue> issues,
        IReadOnlyList<ToolingFinding> tooling,
        ProjectObservabilityProjectionOptions options)
    {
        AgentSnapshot representative = PreferredAgent(agents);
        AgentEvent[] orderedEvents = events
            .OrderBy(EventOrder)
            .ThenBy(eventRecord => eventRecord.Sequence)
            .ThenBy(eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .ToArray();
        AgentEvent[] meaningfulEvents = orderedEvents
            .Where(IsMeaningful)
            .ToArray();
        AgentEvent? latestMeaningfulEvent = meaningfulEvents.LastOrDefault();
        AgentEvent[] timelineEvents = orderedEvents
            .TakeLast(options.MaximumTimelineEntries)
            .ToArray();
        if (latestMeaningfulEvent is not null &&
            !timelineEvents.Any(eventRecord => eventRecord.Id == latestMeaningfulEvent.Id))
        {
            timelineEvents = (timelineEvents.Length >= options.MaximumTimelineEntries
                    ? timelineEvents.Skip(1)
                    : timelineEvents)
                .Append(latestMeaningfulEvent)
                .OrderBy(EventOrder)
                .ThenBy(eventRecord => eventRecord.Sequence)
                .ThenBy(eventRecord => eventRecord.Id, StringComparer.Ordinal)
                .ToArray();
        }
        ProjectObservabilityTimelineEntry[] timeline = timelineEvents
            .Select(ToTimelineEntry)
            .ToArray();
        ProjectObservabilityAttempt[] attempts = meaningfulEvents
            .Where(eventRecord => AttemptTypes.Contains(eventRecord.Type))
            .Select(ToAttempt)
            .ToArray();
        ProjectObservabilityAttempt? latestAttempt = attempts.LastOrDefault();
        ProjectObservabilityAttempt? successfulValidation = meaningfulEvents
            .Where(eventRecord => ValidationTypes.Contains(eventRecord.Type) &&
                EventResult(eventRecord) == "succeeded")
            .Select(ToAttempt)
            .LastOrDefault();
        AgentIssue? unresolvedProjectProblem = issues
            .Where(issue => !issue.Recovered && IsProjectProblem(issue))
            .OrderByDescending(issue => AgentObservabilityTime.SortValue(issue.Timestamp))
            .ThenByDescending(issue => issue.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        AgentIssue? unresolvedToolingBlock = issues
            .Where(issue => !issue.Recovered && IsToolingIssue(issue) && issue.Blocking)
            .OrderByDescending(issue => AgentObservabilityTime.SortValue(issue.Timestamp))
            .ThenByDescending(issue => issue.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        AgentEvent? unresolvedDirectToolingBlock = events
            .Where(IsBlockingCapabilityEvent)
            .OrderByDescending(EventOrder)
            .ThenByDescending(eventRecord => eventRecord.Sequence)
            .ThenByDescending(eventRecord => eventRecord.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        ProjectObservabilitySession[] activeSessions = agents
            .Where(agent => IsActive(agent, options))
            .OrderByDescending(agent => agent.StartTime)
            .ThenBy(agent => agent.AgentId, StringComparer.Ordinal)
            .Select(agent => new ProjectObservabilitySession(
                agent.RunId,
                agent.AgentId,
                agent.LogicalAgentId,
                agent.SessionId,
                agent.StartTime,
                agent.Status,
                agent.CompletionState,
                false))
            .ToArray();
        bool stale = activeSessions.Length == 0 &&
            agents.Any(agent => IsStale(agent, options));
        bool failedTerminalState = activeSessions.Length == 0 &&
            agents.Any(agent =>
                agent.FailureState ||
                agent.CompletionState is AgentCompletionState.Failed or AgentCompletionState.Cancelled);
        AgentSnapshot? activeAgent = agents
            .Where(agent => IsActive(agent, options))
            .OrderByDescending(agent => agent.StartTime)
            .ThenBy(agent => agent.AgentId, StringComparer.Ordinal)
            .FirstOrDefault();
        bool latestFailureRecoveredAsTooling = latestAttempt?.Result == "failed" &&
            latestAttempt is not null &&
            issues.Any(issue => issue.Recovered &&
                IsToolingIssue(issue) &&
                issue.EventIds.Contains(latestAttempt.EventId, StringComparer.Ordinal));
        ProjectObservabilityProblemObservation? failure = meaningfulEvents
            .Where(eventRecord => EventResult(eventRecord) == "failed")
            .Select(eventRecord => new ProjectObservabilityProblemObservation(
                eventRecord.Timestamp,
                eventRecord.Summary,
                eventRecord.Id,
                null,
                IsProjectFailureEvent(eventRecord)))
            .Concat(unresolvedProjectProblem is null
                ? []
                : [new ProjectObservabilityProblemObservation(
                    unresolvedProjectProblem.Timestamp,
                    unresolvedProjectProblem.Summary,
                    unresolvedProjectProblem.EventIds.FirstOrDefault(),
                    unresolvedProjectProblem.Id,
                    true)])
            .OrderByDescending(observation => AgentObservabilityTime.SortValue(observation.Timestamp))
            .ThenByDescending(observation => observation.EventId ?? observation.IssueId, StringComparer.Ordinal)
            .FirstOrDefault();

        ProjectObservabilityCompleteness completeness = options.HistoryDegraded
            ? ProjectObservabilityCompleteness.Degraded
            : meaningfulEvents.Length == 0
                ? ProjectObservabilityCompleteness.Unknown
                : options.HistoryComplete ? ProjectObservabilityCompleteness.Complete : ProjectObservabilityCompleteness.Partial;
        ProjectObservabilityStateKind state;
        bool actionRequired;
        if ((unresolvedProjectProblem is not null && unresolvedProjectProblem.Blocking) ||
            unresolvedToolingBlock is not null ||
            unresolvedDirectToolingBlock is not null)
        {
            state = ProjectObservabilityStateKind.Blocked;
            actionRequired = true;
        }
        else if (unresolvedProjectProblem is not null || stale ||
            failedTerminalState ||
            latestAttempt?.Result == "failed" && !latestFailureRecoveredAsTooling)
        {
            state = ProjectObservabilityStateKind.NeedsAttention;
            actionRequired = true;
        }
        else if (activeSessions.Any(agent => agent.Status is AgentStatus.Running or AgentStatus.Waiting))
        {
            state = ProjectObservabilityStateKind.Working;
            actionRequired = false;
        }
        else if (completeness != ProjectObservabilityCompleteness.Complete)
        {
            state = ProjectObservabilityStateKind.Unknown;
            actionRequired = false;
        }
        else if (latestAttempt is null && representative.CompletionState != AgentCompletionState.Succeeded)
        {
            state = ProjectObservabilityStateKind.Unknown;
            actionRequired = false;
        }
        else
        {
            state = ProjectObservabilityStateKind.Healthy;
            actionRequired = false;
        }

        ProjectObservabilityTimelineEntry? lastMeaningful = timeline
            .Where(entry => entry.IsMeaningful)
            .OrderByDescending(entry => AgentObservabilityTime.SortValue(entry.Timestamp))
            .ThenByDescending(entry => entry.Sequence)
            .ThenByDescending(entry => entry.EventId, StringComparer.Ordinal)
            .FirstOrDefault();
        AgentEvent? lastMeaningfulEvent = lastMeaningful is null
            ? null
            : events.FirstOrDefault(eventRecord =>
                string.Equals(eventRecord.Id, lastMeaningful.EventId, StringComparison.Ordinal));
        return new ProjectObservabilityState(
            ObservabilityEntityIdentity.Create(
                representative.EntityType,
                representative.CanonicalEntityId,
                representative.DisplayName),
            representative.DisplayName,
            state,
            actionRequired,
            activeAgent?.CurrentOperation ?? activeAgent?.CurrentActivity,
            lastMeaningfulEvent is null ? null : EventOperation(lastMeaningfulEvent),
            ValidTimestamp(lastMeaningful?.Timestamp),
            latestAttempt,
            successfulValidation,
            failure,
            CurrentProblemObservation(unresolvedProjectProblem, unresolvedToolingBlock, unresolvedDirectToolingBlock),
            (unresolvedProjectProblem ?? unresolvedToolingBlock)?.ComponentOwner ??
                (unresolvedProjectProblem ?? unresolvedToolingBlock)?.ProbableOwner,
            (unresolvedProjectProblem ?? unresolvedToolingBlock)?.Classification ??
                (unresolvedProjectProblem ?? unresolvedToolingBlock)?.Category.ToString(),
            activeSessions,
            timeline,
            tooling,
            completeness,
            stale);
    }
    private static ProjectObservabilityProblemObservation? CurrentProblemObservation(
        AgentIssue? projectProblem,
        AgentIssue? toolingProblem,
        AgentEvent? directToolingProblem)
    {
        if (projectProblem is not null)
            return new(
                projectProblem.Timestamp,
                projectProblem.Summary,
                projectProblem.EventIds.FirstOrDefault(),
                projectProblem.Id,
                true);
        if (toolingProblem is not null)
            return new(
                toolingProblem.Timestamp,
                toolingProblem.Summary,
                toolingProblem.EventIds.FirstOrDefault(),
                toolingProblem.Id,
                false);
        return directToolingProblem is null
            ? null
            : new(
                directToolingProblem.Timestamp,
                directToolingProblem.Summary,
                directToolingProblem.Id,
                null,
                false);
    }


    private static Dictionary<string, ToolingFinding> BuildToolingFindings(
        AgentObservabilityView source,
        IReadOnlyDictionary<string, string> sessionProjects,
        IReadOnlyDictionary<string, List<AgentSnapshot>> agentsByProject,
        int maximumOccurrences)
    {
        var occurrences = new Dictionary<string, List<ToolingFindingOccurrence>>(StringComparer.Ordinal);
        foreach (AgentIssue issue in source.Issues.Where(IsToolingIssue))
        {
            if (!sessionProjects.TryGetValue(SessionKey(issue), out string? projectId) ||
                !agentsByProject.ContainsKey(projectId))
            {
                continue;
            }
            AgentEvent[] supporting = issue.EventIds
                .Select(id => source.Events.FirstOrDefault(eventRecord => eventRecord.Id == id))
                .Where(static eventRecord => eventRecord is not null)
                .Select(static eventRecord => eventRecord!)
                .OrderBy(EventOrder)
                .ThenBy(eventRecord => eventRecord.Sequence)
                .ThenBy(eventRecord => eventRecord.Id, StringComparer.Ordinal)
                .ToArray();
            AddOccurrence(
                occurrences,
                CreateOccurrence(issue, supporting.FirstOrDefault(), projectId, supporting));
        }

        foreach (AgentEvent eventRecord in source.Events.Where(IsDirectToolingEvent))
        {
            if (!sessionProjects.TryGetValue(SessionKey(eventRecord), out string? projectId) ||
                !agentsByProject.ContainsKey(projectId))
            {
                continue;
            }
            AddOccurrence(occurrences, CreateOccurrence(null, eventRecord, projectId));
        }

        return occurrences.ToDictionary(
            pair => pair.Key,
            pair => AggregateFinding(pair.Value, maximumOccurrences),
            StringComparer.Ordinal);

        void AddOccurrence(Dictionary<string, List<ToolingFindingOccurrence>> target, ToolingFindingOccurrence occurrence)
        {
            if (!target.TryGetValue(occurrence.FindingIdentity, out List<ToolingFindingOccurrence>? values))
            {
                values = [];
                target[occurrence.FindingIdentity] = values;
            }
            if (!values.Any(value => value.OccurrenceId == occurrence.OccurrenceId))
            {
                values.Add(occurrence);
            }
        }
    }

    private static ToolingFinding AggregateFinding(
        IReadOnlyList<ToolingFindingOccurrence> values,
        int maximumOccurrences)
    {
        ToolingFindingOccurrence[] ordered = values
            .OrderBy(occurrence => occurrence.Timestamp)
            .ThenBy(occurrence => occurrence.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        ToolingFindingOccurrence representative = ordered[^1];
        ToolingFindingOccurrence[] retained = ordered.TakeLast(maximumOccurrences).ToArray();
        return new ToolingFinding(
            representative.FindingIdentity,
            representative.Kind,
            representative.Summary,
            representative.Confidence,
            retained,
            values.Any(static occurrence => occurrence.ProductionWorkFailed),
            values.Any(static occurrence => occurrence.RecoverySucceeded),
            values.Select(static occurrence => occurrence.ProjectId).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            values.Select(static occurrence => occurrence.LogicalAgentId).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            ordered[0].Timestamp,
            ordered[^1].Timestamp,
            values.SelectMany(static occurrence => occurrence.MissingEvidence ?? []).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            values.Count);
    }

    private static ToolingFindingOccurrence CreateOccurrence(
        AgentIssue? issue,
        AgentEvent? eventRecord,
        string projectId,
        IReadOnlyList<AgentEvent>? supportingEvents = null)
    {
        JsonElement? data = eventRecord?.Data;
        ToolingFindingKind kind = issue is not null
            ? FindingKind(issue)
            : FindingKind(eventRecord!);
        string identity = FindingIdentity(issue, eventRecord, kind);
        string? errorCode = First(
            AgentObservabilityData.GetString(data, "errorCode"),
            AgentObservabilityData.GetString(data, "underlyingErrorCode"),
            AgentObservabilityData.GetString(data, "failureCode"));
        string summary = issue?.Summary ?? eventRecord?.Summary ?? "Tooling finding";
        string? explicitComponent = issue?.ComponentOwner ??
            AgentObservabilityData.GetString(data, "componentOwner");
        string? component = explicitComponent ?? issue?.CausalComponent ?? issue?.ProbableOwner ??
            AgentObservabilityData.GetString(data, "causalComponent") ??
            AgentObservabilityData.GetString(data, "probableOwner");
        bool componentDerived = explicitComponent is null && component is not null;
        string? operation = issue?.OperationKey ?? AgentObservabilityData.GetString(data, "operationKey") ??
            AgentObservabilityData.GetString(data, "operation");
        bool recovered = issue?.Recovered == true ||
            AgentObservabilityData.GetBoolean(data, "recovered");
        string? projectName = issue?.AffectedProject ?? eventRecord?.DisplayName;
        string occurrenceId = eventRecord?.Id ?? issue?.Id ?? "unknown";
        string[] missing = [];
        if (eventRecord is null)
        {
            missing = ["critical: Supporting event was not retained in the current history window."];
        }
        IEnumerable<string> evidenceValues = issue?.EvidenceReference is string issueEvidence
            ? [issueEvidence]
            : [];
        string[] evidenceIds = evidenceValues
            .Concat(AgentObservabilityData.GetString(data, "evidenceId") is string evidenceId
                ? [evidenceId]
                : [])
            .Concat(AgentObservabilityData.GetStrings(data, "evidenceIds"))
            .Concat(AgentObservabilityData.GetStrings(data, "evidenceReferences"))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return new ToolingFindingOccurrence(
            occurrenceId,
            identity,
            kind,
            summary,
            eventRecord?.Timestamp ?? issue?.Timestamp ?? 0,
            projectId,
            projectName,
            eventRecord?.RunId ?? issue?.RunId,
            eventRecord?.AgentId ?? issue?.AgentId,
            eventRecord?.LogicalAgentId ?? issue?.LogicalAgentId,
            eventRecord?.SessionId ?? issue?.SessionId,
            operation,
            AgentObservabilityData.GetString(data, "workaround") ??
                (kind == ToolingFindingKind.SuccessfulWorkaround ? summary : null),
            component,
            issue is not null ? "observed" : "observed-event",
            ProvenanceValues(issue, eventRecord),
            supportingEvents is { Count: > 0 }
                ? supportingEvents.Select(static value => value.Id).Distinct(StringComparer.Ordinal).ToArray()
                : eventRecord is null ? [] : [eventRecord.Id],
            errorCode,
            AgentObservabilityData.GetString(data, "command"),
            AgentObservabilityData.GetString(data, "arguments"),
            AgentObservabilityData.GetString(data, "stdout") ??
                AgentObservabilityData.GetString(data, "stdoutExcerpt"),
            AgentObservabilityData.GetString(data, "stderr") ??
                AgentObservabilityData.GetString(data, "stderrExcerpt"),
            AgentObservabilityData.GetString(data, "diagnosticOutput") ??
                AgentObservabilityData.GetString(data, "causalDiagnostic"),
            AgentObservabilityData.GetStrings(data, "recoveryAttempts"),
            (int)(AgentObservabilityData.GetInt64(data, "retryCount") ?? issue?.RetryCount ?? 0),
            AgentObservabilityData.GetInt64(data, "addedDelayMilliseconds") ??
                AgentObservabilityData.GetInt64(data, "durationMs") ??
                AgentObservabilityData.GetInt64(data, "elapsedRecoveryMs"),
            (int)(AgentObservabilityData.GetInt64(data, "runtimeLaunches") ?? 0),
            (int)(AgentObservabilityData.GetInt64(data, "repeatedWorkCount") ??
                (kind is ToolingFindingKind.RepeatedInspection or ToolingFindingKind.RedundantWork ? 1 : 0)),
            (int?)AgentObservabilityData.GetInt64(data, "tokenCount"),
            AgentObservabilityData.GetString(data, "validationImpact"),
            AgentObservabilityData.GetString(data, "buildImpact"),
            AgentObservabilityData.GetString(data, "runtimeImpact"),
            GetStringMap(data, "toolVersions"),
            GetStringMap(data, "environment"),
            kind == ToolingFindingKind.BlockingFailure && !recovered,
            recovered,
            eventRecord is not null,
            missing,
            componentDerived,
            evidenceIds,
            ObservedToolingEvidence(data));
    }
    private static IReadOnlyList<string> ObservedToolingEvidence(JsonElement? data)
    {
        var values = new List<string>(14);
        AddString("originalFault", "originalFault");
        AddString("expectedPromotedFingerprint", "expectedPromotedFingerprint");
        AddString("promotedSourceCommit", "promotedSourceCommit");
        AddString("recoveryPayloadPath", "recoveryPayloadPath");
        AddString("repairResult", "repairResult");
        AddString("verificationResult", "verificationResult");
        AddString("retryResult", "retryResult");
        AddString("productionImpact", "productionImpact");
        AddString("recoveryAction", "recoveryAction");
        AddString("recoveryState", "recoveryState");
        foreach (string artifact in AgentObservabilityData.GetStrings(data, "affectedArtifacts").Take(32))
            values.Add("affectedArtifact=" + artifact);
        if (AgentObservabilityData.GetNullableBoolean(data, "repairAttempted") is bool repairAttempted)
            values.Add("repairAttempted=" + repairAttempted);
        if (AgentObservabilityData.GetNullableBoolean(data, "currentSourceDiverged") is bool currentSourceDiverged)
            values.Add("currentSourceDiverged=" + currentSourceDiverged);
        if (AgentObservabilityData.GetNullableBoolean(data, "recovered") is bool recovered)
            values.Add("workflowOutcome=" + (recovered ? "recovered" : "blocked"));
        if (AgentObservabilityData.GetInt64(data, "recoveryAttempts") is long recoveryAttempts)
            values.Add("recoveryAttempts=" + recoveryAttempts);
        if (AgentObservabilityData.GetInt64(data, "elapsedRecoveryMs") is long elapsedRecoveryMs)
            values.Add("recoveryDurationMs=" + elapsedRecoveryMs);
        return values;

        void AddString(string propertyName, string label)
        {
            if (AgentObservabilityData.GetString(data, propertyName) is string value)
                values.Add(label + "=" + value);
        }
    }

    private static IReadOnlyList<string> ProvenanceValues(
        AgentIssue? issue,
        AgentEvent? eventRecord)
    {
        var values = new List<string>(2);
        if (issue is not null) values.Add("issue:" + issue.Id);
        if (eventRecord is not null) values.Add("event:" + eventRecord.Id);
        return values;
    }

    private static IReadOnlyDictionary<string, string> GetStringMap(JsonElement? data, string propertyName)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty item in property.EnumerateObject().Take(64))
        {
            if (item.Value.ValueKind == JsonValueKind.String)
                result[item.Name] = AgentObservabilityData.BoundText(item.Value.GetString(), 512);
        }

        return result;
    }

    private static int? OptionalIntSum(IEnumerable<int> values)
    {
        int sum = 0;
        bool observed = false;
        foreach (int value in values)
        {
            if (value > 0) observed = true;
            sum += value;
        }

        return observed ? sum : null;
    }

    private static long? OptionalLongSum(IEnumerable<long?> values)
    {
        long sum = 0;
        bool observed = false;
        foreach (long? value in values)
        {
            if (value is null) continue;
            observed = true;
            sum += value.Value;
        }

        return observed ? sum : null;
    }

    private static ToolingAssessmentFinding ToAssessmentFinding(ToolingFinding finding)
    {
        ToolingFindingOccurrence[] occurrences = finding.Occurrences.ToArray();
        return new ToolingAssessmentFinding(
            finding.FindingIdentity,
            finding.Kind.ToString(),
            finding.Summary,
            finding.Confidence,
            occurrences.SelectMany(ObservedFacts).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            DerivedInterpretation(finding),
            SuggestedInvestigationAreas(finding),
            finding.ProductionWorkFailed,
            finding.RecoverySucceeded,
            occurrences.Select(static occurrence => occurrence.Workaround).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            occurrences.Select(static occurrence => occurrence.Operation).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            finding.AffectedProjects,
            occurrences
                .Select(static occurrence => occurrence.ComponentOwner is null
                    ? null
                    : (occurrence.ComponentOwnerDerived ? "derived:" : "observed:") + occurrence.ComponentOwner)
                .Where(static value => value is not null)
                .Select(static value => value!)
                .Distinct(StringComparer.Ordinal)
                .Take(32)
                .ToArray(),
            occurrences.Select(static occurrence => occurrence.Command).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.Select(static occurrence => occurrence.ErrorCode).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.SelectMany(static occurrence => occurrence.Provenance).Distinct(StringComparer.Ordinal).Take(128).ToArray(),
            occurrences.SelectMany(static occurrence => occurrence.RecoveryAttempts ?? []).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            OptionalIntSum(occurrences.Select(static occurrence => occurrence.RetryCount)),
            OptionalLongSum(occurrences.Select(static occurrence => occurrence.AddedDelayMilliseconds)),
            OptionalIntSum(occurrences.Select(static occurrence => occurrence.RuntimeLaunches)),
            OptionalIntSum(occurrences.Select(static occurrence => occurrence.RepeatedWorkCount)),
            occurrences.Select(static occurrence => occurrence.TokenCount).Where(static value => value is not null).Select(static value => value!.Value).ToArray(),
            occurrences.Select(static occurrence => occurrence.ValidationImpact).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).ToArray(),
            occurrences.Select(static occurrence => occurrence.BuildImpact).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).ToArray(),
            occurrences.Select(static occurrence => occurrence.RuntimeImpact).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).ToArray(),
            occurrences.SelectMany(static occurrence => occurrence.Versions ?? new Dictionary<string, string>()).GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
            occurrences.SelectMany(static occurrence => occurrence.Environment ?? new Dictionary<string, string>()).GroupBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
            finding.MissingEvidence.Count == 0 ? "complete" : "partial",
            finding.MissingEvidence,
            occurrences.Select(static occurrence => occurrence.Arguments).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.Select(static occurrence => occurrence.Stdout).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.Select(static occurrence => occurrence.Stderr).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.Select(static occurrence => occurrence.DiagnosticOutput).Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            occurrences.SelectMany(static occurrence => occurrence.EvidenceIds ?? []).Distinct(StringComparer.Ordinal).Take(64).ToArray(),
            occurrences.SelectMany(static occurrence => occurrence.SupportingEventIds).Distinct(StringComparer.Ordinal).Take(128).ToArray());

        static IEnumerable<string> ObservedFacts(ToolingFindingOccurrence occurrence)
        {
            yield return $"{occurrence.Timestamp}: {occurrence.Summary}";
            if (occurrence.ErrorCode is not null) yield return "errorCode=" + occurrence.ErrorCode;
            if (occurrence.Command is not null) yield return "command=" + occurrence.Command;
            if (occurrence.Arguments is not null) yield return "arguments were captured";
            if (occurrence.Stdout is not null) yield return "bounded stdout was captured";
            if (occurrence.Stderr is not null) yield return "bounded stderr was captured";
            if (occurrence.DiagnosticOutput is not null) yield return "bounded diagnostics were captured";
            if (occurrence.ComponentOwner is not null)
                yield return (occurrence.ComponentOwnerDerived ? "derived component owner=" : "observed component owner=") + occurrence.ComponentOwner;
            foreach (string evidence in occurrence.ObservedEvidence ?? [])
                yield return evidence;
        }
    }

    private static string? DerivedInterpretation(ToolingFinding finding) => finding.Kind switch
    {
        ToolingFindingKind.RecoveredFailure => "A deterministic tooling failure was followed by recovery; the production outcome is reported separately.",
        ToolingFindingKind.SuccessfulWorkaround => "The operation succeeded with an explicit workaround; assess whether the workaround should become native tooling.",
        ToolingFindingKind.MissingCapability => "The workflow recorded a missing capability rather than an inferred project defect.",
        _ => null
    };
    private static IReadOnlyList<string> SuggestedInvestigationAreas(ToolingFinding finding) =>
        finding.Kind switch
        {
            ToolingFindingKind.RecoveredFailure =>
                ["Inspect recovery trigger, retry budget, and the original component failure."],
            ToolingFindingKind.SuccessfulWorkaround =>
                ["Determine whether the workaround can become a supported operation."],
            ToolingFindingKind.MissingCapability =>
                ["Confirm capability ownership and whether the validation contract should be extended."],
            ToolingFindingKind.ExcessiveRetry =>
                ["Inspect retry policy and whether the first failure was actionable."],
            _ => []
        };

    private static ProjectObservabilityTimelineEntry ToTimelineEntry(AgentEvent eventRecord) =>
        new(eventRecord.Id, eventRecord.Timestamp, eventRecord.Sequence, eventRecord.Type,
            eventRecord.Stage, eventRecord.Summary, EventResult(eventRecord), IsMeaningful(eventRecord));

    private static ProjectObservabilityAttempt ToAttempt(AgentEvent eventRecord) =>
        new(eventRecord.Id, eventRecord.Timestamp, eventRecord.Type, EventResult(eventRecord), eventRecord.Summary);

    private static bool IsProductionProject(AgentSnapshot agent) =>
        (agent.EntityType is ObservabilityEntityTypes.Mod or ObservabilityEntityTypes.Tool) &&
        agent.CanonicalEntityId.StartsWith(agent.EntityType + ":", StringComparison.Ordinal) &&
        string.Equals(agent.WorkloadKind, "production", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(agent.QualificationProfile);


    private static bool IsProjectProblem(AgentIssue issue) =>
        issue.Category == AgentIssueCategory.ModDefect ||
        issue.Classification is "MOD_FAILURE" or "PROJECT_FAILURE" ||
        issue.EntityType == ObservabilityEntityTypes.Mod && !IsToolingIssue(issue);

    private static bool IsToolingIssue(AgentIssue issue) => issue.Category is
        AgentIssueCategory.ToolingFailure or
        AgentIssueCategory.ToolLimitation or
        AgentIssueCategory.CapabilityGap or
        AgentIssueCategory.ToolingImprovement or
        AgentIssueCategory.OptionalValidationUnavailable or
        AgentIssueCategory.Stall or
        AgentIssueCategory.RedundantWork or
        AgentIssueCategory.ContextIssue or
        AgentIssueCategory.Workaround ||
        issue.Category == AgentIssueCategory.Retry && issue.RetryCount > 1;

    private static bool IsDirectToolingEvent(AgentEvent eventRecord) =>
        eventRecord.Type is
            AgentEventTypes.ValidationCapabilityBlocked or
            AgentEventTypes.WorkaroundApplied or
            AgentEventTypes.ToolLimitation ||
        (eventRecord.Type == AgentEventTypes.RetryStarted ||
            eventRecord.Type == AgentEventTypes.RetryCompleted) &&
        IsExcessiveRetry(eventRecord);

    private static bool IsExcessiveRetry(AgentEvent eventRecord) =>
        (AgentObservabilityData.GetInt64(eventRecord.Data, "retryCount") ?? 0) > 1 ||
        (AgentObservabilityData.GetInt64(eventRecord.Data, "attempts") ?? 0) > 1 ||
        (AgentObservabilityData.GetInt64(eventRecord.Data, "repeatedWorkCount") ?? 0) > 0 ||
        AgentObservabilityData.GetBoolean(eventRecord.Data, "excessive");

    private static ToolingFindingKind FindingKind(AgentIssue issue) => issue.Category switch
    {
        AgentIssueCategory.ToolingFailure => issue.Recovered ? ToolingFindingKind.RecoveredFailure : ToolingFindingKind.BlockingFailure,
        AgentIssueCategory.ToolLimitation => ToolingFindingKind.UnsupportedOperation,
        AgentIssueCategory.CapabilityGap or AgentIssueCategory.OptionalValidationUnavailable => ToolingFindingKind.MissingCapability,
        AgentIssueCategory.Workaround => ToolingFindingKind.SuccessfulWorkaround,
        AgentIssueCategory.Retry => ToolingFindingKind.ExcessiveRetry,
        AgentIssueCategory.Stall => ToolingFindingKind.ExcessiveWaiting,
        AgentIssueCategory.RedundantWork => ToolingFindingKind.RedundantWork,
        AgentIssueCategory.ContextIssue => ToolingFindingKind.DiagnosticDeficiency,
        _ => ToolingFindingKind.Other
    };

    private static ToolingFindingKind FindingKind(AgentEvent eventRecord) => eventRecord.Type switch
    {
        AgentEventTypes.WorkaroundApplied => ToolingFindingKind.SuccessfulWorkaround,
        AgentEventTypes.ValidationCapabilityBlocked => ToolingFindingKind.MissingCapability,
        AgentEventTypes.ToolLimitation => ToolingFindingKind.UnsupportedOperation,
        AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted => ToolingFindingKind.ExcessiveRetry,
        _ => ToolingFindingKind.Other
    };

    private static string FindingIdentity(AgentIssue? issue, AgentEvent? eventRecord, ToolingFindingKind kind)
    {
        string? operation = issue?.OperationKey ??
            AgentObservabilityData.GetString(eventRecord?.Data, "operationKey");
        string? stable = First(
            issue?.Fingerprint,
            issue?.CapabilityId,
            AgentObservabilityData.GetString(eventRecord?.Data, "fingerprint"),
            issue?.CausalIssueKey);
        if (stable is null)
        {
            // A summary or error code is evidence for the occurrence, not a
            // trustworthy cross-run identity. Keep unkeyed records separate.
            stable = issue is not null
                ? "issue:" + issue.Id
                : "event:" + (eventRecord?.Id ?? "unknown");
        }
        if (operation is not null) stable += "|operation|" + operation;
        return "tooling|" + kind + "|" + Normalize(stable);
    }

    private static string ProjectKey(AgentSnapshot agent) =>
        ProjectKey(agent.EntityType, agent.CanonicalEntityId, agent.DisplayName);

    private static string ProjectKey(string entityType, string canonicalEntityId, string displayName) =>
        entityType + "\u001f" + ObservabilityEntityIdentity.Create(entityType, canonicalEntityId, displayName).CanonicalEntityId;

    private static string ProjectKey(AgentIssue issue) => ProjectKey(issue.EntityType, issue.CanonicalEntityId, issue.DisplayName);

    private static string SessionKey(AgentSnapshot agent) => agent.RunId + "\u001f" + agent.AgentId;
    private static string SessionKey(AgentEvent eventRecord) => eventRecord.RunId + "\u001f" + eventRecord.AgentId;
    private static string SessionKey(AgentIssue issue) => issue.RunId + "\u001f" + issue.AgentId;

    private static AgentSnapshot PreferredAgent(IEnumerable<AgentSnapshot> values) => values
        .OrderByDescending(agent => IsActive(agent, ProjectObservabilityProjectionOptions.Default(agent.LastActivityAt ?? agent.StartTime)) && !agent.FailureState)
        .ThenByDescending(agent => agent.StartTime)
        .ThenByDescending(agent => agent.CompletedAt ?? 0)
        .ThenBy(agent => agent.AgentId, StringComparer.Ordinal)

        .First();
    private static bool IsActive(AgentSnapshot agent, ProjectObservabilityProjectionOptions options) =>
        agent.Status is AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting &&
        !IsStale(agent, options);
    private static bool IsStale(AgentSnapshot agent, ProjectObservabilityProjectionOptions options)
    {
        if (agent.CompletionResult is "STALE" or "ABANDONED") return true;
        if (agent.Status is not (AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting)) return false;
        long activity = Math.Max(agent.StartTime, agent.LastActivityAt ?? 0);
        return options.NowMilliseconds > activity &&
            options.NowMilliseconds - activity > (long)options.WorkingStalenessThreshold.TotalMilliseconds;
    }

    private static bool IsMeaningful(AgentEvent eventRecord) =>
        !AgentObservabilityData.GetBoolean(eventRecord.Data, "lifecycleOnly") &&
        eventRecord.Type is not (AgentEventTypes.AgentCreated or AgentEventTypes.AgentStarted or AgentEventTypes.StageChanged);

    private static string EventOperation(AgentEvent eventRecord) =>
        First(
            AgentObservabilityData.GetString(eventRecord.Data, "operationKey"),
            AgentObservabilityData.GetString(eventRecord.Data, "operation"),
            eventRecord.Type) ?? "observability event";

    private static bool IsProjectFailureEvent(AgentEvent eventRecord) =>
        (eventRecord.Type is AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed ||
            eventRecord.Type == AgentEventTypes.ValidationCompleted &&
            EventResult(eventRecord) == "failed");

    private static bool IsBlockingCapabilityEvent(AgentEvent eventRecord) =>
        eventRecord.Type == AgentEventTypes.ValidationCapabilityBlocked &&
        !AgentObservabilityData.GetBoolean(eventRecord.Data, "recovered") &&
        (AgentObservabilityData.GetBoolean(eventRecord.Data, "required") ||
            AgentObservabilityData.GetBoolean(eventRecord.Data, "blocksProduction") ||
            string.Equals(
                AgentObservabilityData.GetString(eventRecord.Data, "validationClassification"),
                "REQUIRED",
                StringComparison.OrdinalIgnoreCase));
    private static string EventResult(AgentEvent eventRecord)
    {
        if (eventRecord.Type is AgentEventTypes.CommandFailed or AgentEventTypes.CommandTimeout or
            AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed or AgentEventTypes.ToolFailed or
            AgentEventTypes.ToolException or AgentEventTypes.AgentFailed ||
            eventRecord.Type.EndsWith("failed", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("timeout", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }
        string? outcome = AgentObservabilityData.GetString(eventRecord.Data, "outcome") ??
            AgentObservabilityData.GetString(eventRecord.Data, "status");
        if (outcome is not null && outcome.Contains("fail", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (eventRecord.Type.EndsWith("started", StringComparison.OrdinalIgnoreCase)) return "running";
        if (eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outcome, "pass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outcome, "succeeded", StringComparison.OrdinalIgnoreCase)) return "succeeded";
        return "observed";
    }
    private static long? ValidTimestamp(long? timestamp) =>
        AgentObservabilityTime.IsValid(timestamp) ? timestamp : null;

    private static long EventOrder(AgentEvent eventRecord) => AgentObservabilityTime.SortValue(eventRecord.Timestamp);

    private static string? First(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Normalize(string value) => string.Join(
        " ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
