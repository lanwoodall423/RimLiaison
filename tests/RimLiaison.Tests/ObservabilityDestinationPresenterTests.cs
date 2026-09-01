using RimLiaison.Desktop;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ObservabilityDestinationPresenterTests
{
    public static void OverviewAndProjectUseCanonicalProjectState()
    {
        ProjectObservabilityTimelineEntry timeline = new(
            "event-1",
            100,
            1,
            "validation.completed",
            DevelopmentStage.Testing,
            "Validation passed",
            "pass",
            true);
        ProjectObservabilityAttempt attempt = new("event-1", 100, "validate", "pass", "Validation passed");
        ProjectObservabilityState project = new(
            new ObservabilityEntityIdentity("mod", "mod:alpha", "Alpha"),
            "Alpha",
            ProjectObservabilityStateKind.Healthy,
            false,
            null,
            "Validation",
            100,
            attempt,
            attempt,
            null,
            null,
            null,
            null,
            [],
            [timeline],
            [],
            ProjectObservabilityCompleteness.Complete);

        ObservabilityOverviewRow overview = ObservabilityDestinationPresenters.Overview([project]).Single();
        ObservabilityProjectHeader header = ObservabilityDestinationPresenters.Project(project);

        Equal("Alpha", overview.Project);
        Equal(header.State, overview.State);
        Equal(header.Activity, overview.Activity);
        Equal(header.LastSuccessfulValidation, header.LastAttempt);
        Equal("PASS", overview.Result);
    }

    public static void ProblemsExposeOwnerFocusedAction()
    {
        AgentIssue issue = new()
        {
            Id = "issue-1",
            RunId = "run-1",
            AgentId = "agent-1",
            ModId = "mod:alpha",
            DisplayName = "Alpha",
            Category = AgentIssueCategory.ToolingFailure,
            Severity = AgentIssueSeverity.Error,
            Summary = "Tool failed",
            Timestamp = 100,
            ComponentOwner = "DevBridge",
            Recommendation = "Restart the tool",
            Blocking = false,
            CurrentState = "open",
            ResolutionState = "unresolved"
        };

        ObservabilityProblemRow row = ObservabilityDestinationPresenters.Problems(
            [new AgentObservabilityIssueRow(issue, "Alpha", null, false)]).Single();
        Equal("DevBridge", row.Owner);
        Equal("Restart the tool", row.NextAction);
        Equal("Tool failed", row.Problem);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
