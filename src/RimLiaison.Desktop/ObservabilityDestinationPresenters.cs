using RimLiaison.Observability;

namespace RimLiaison.Desktop;

public sealed record ObservabilityOverviewRow(
    string ProjectId,
    string Project,
    string State,
    string Action,
    string Activity,
    string Result,
    string Time,
    string Problem,
    string Tooling);

public sealed record ObservabilityProjectHeader(
    string Project,
    string State,
    string Action,
    string Activity,
    string Problem,
    string LastAttempt,
    string LastSuccessfulValidation,
    string Tooling);

public sealed record ObservabilityProblemRow(
    string IssueId,
    string State,
    string Severity,
    string Project,
    string Owner,
    string Problem,
    string Occurred,
    string NextAction);

public sealed record ObservabilitySystemSummary(
    string Health,
    string Storage,
    string History,
    string Counts,
    string Tooling);

/// <summary>
/// Destination presenters contain owner-facing labels only. They consume the
/// canonical projection and do not read the store or calculate project state.
/// </summary>
public static class ObservabilityDestinationPresenters
{
    public static IReadOnlyList<ObservabilityOverviewRow> Overview(
        IEnumerable<ProjectObservabilityState> projects,
        bool filterApplied = false) =>
        projects.Select(project => new ObservabilityOverviewRow(
                project.Identity.CanonicalEntityId,
                project.DisplayName,
                State(project.State),
                project.ActionRequired ? "Yes" : "No",
                project.LastMeaningfulOperation ?? "No meaningful activity",
                Result(project.LatestAttempt?.Result),
                AgentObservabilityTime.FormatLocal(project.LastMeaningfulActivityAt),
                project.CurrentUnresolvedProblem?.Summary ?? "—",
                project.HasToolingFindings
                    ? $"{project.ToolingFindings.Count} finding(s)"
                    : "—"))
            .ToArray();

    public static ObservabilityProjectHeader Project(ProjectObservabilityState project) =>
        new(
            project.DisplayName,
            State(project.State),
            project.ActionRequired ? "Yes" : "No",
            project.LastMeaningfulOperation ?? "No meaningful activity",
            project.CurrentUnresolvedProblem?.Summary ?? "—",
            Attempt(project.LatestAttempt),
            Attempt(project.LastSuccessfulValidation),
            project.HasToolingFindings
                ? $"{project.ToolingFindings.Count} finding(s)"
                : "None");

    public static IReadOnlyList<ObservabilityProblemRow> Problems(
        IEnumerable<AgentObservabilityIssueRow> rows) =>
        rows.Select(row => new ObservabilityProblemRow(
                row.Issue.Id,
                row.StateLabel,
                row.Issue.Severity.ToString(),
                row.ModName,
                Owner(row.Issue),
                row.Issue.Summary,
                AgentObservabilityTime.FormatLocal(row.Issue.Timestamp),
                row.Issue.Recommendation ?? "Inspect supporting evidence"))
            .ToArray();

    public static ObservabilitySystemSummary System(AgentObservabilitySystemView view) =>
        new(
            view.HistoryDegraded ? "Degraded" : "Available",
            view.StorageLocation,
            view.Completeness.ToString() +
                (view.HistoryComplete ? string.Empty : " · incomplete"),
            $"{view.AgentCount} agents · {view.EventCount} events · {view.IssueCount} problems",
            $"{view.ToolingFindingCount} tooling finding group(s)");

    private static string State(ProjectObservabilityStateKind state) => state switch
    {
        ProjectObservabilityStateKind.Healthy => "Healthy",
        ProjectObservabilityStateKind.Working => "Working",
        ProjectObservabilityStateKind.NeedsAttention => "Needs attention",
        ProjectObservabilityStateKind.Blocked => "Blocked",
        _ => "Unknown"
    };

    private static string Result(string? result) => string.IsNullOrWhiteSpace(result)
        ? "—"
        : result.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
          result.Equals("pass", StringComparison.OrdinalIgnoreCase)
            ? "PASS"
            : result.ToUpperInvariant();

    private static string Attempt(ProjectObservabilityAttempt? attempt) => attempt is null
        ? "—"
        : Result(attempt.Result) + " · " + attempt.Operation +
          " · " + AgentObservabilityTime.FormatLocal(attempt.Timestamp);

    private static string Owner(AgentIssue issue) =>
        issue.ComponentOwner is not null
            ? issue.ComponentOwner
            : issue.Category switch
            {
                AgentIssueCategory.ModDefect => "Project",
                AgentIssueCategory.ToolingFailure or
                    AgentIssueCategory.ToolLimitation or
                    AgentIssueCategory.CapabilityGap => "RimLiaison/tooling",
                _ => "Unresolved"
            };
}
