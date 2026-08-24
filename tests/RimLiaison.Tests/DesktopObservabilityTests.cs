using System.Diagnostics;
using RimLiaison;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class DesktopObservabilityTests
{
    public static void AllIsDefault()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-default", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.hospitality", "Hospitality");
        agent.Start("Inspecting source");
        agent.Record(DevelopmentStage.Analysis, AgentEventTypes.FileInspected, "Inspected source.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
        AssertEqual(AgentObservabilityUiView.All, snapshot.View);
        Assert(snapshot.All is not null, "All must be the default populated view");
        Assert(snapshot.Navigation.Items[0].Selected, "All navigation item must be selected by default");
    }

    public static void MultipleConcurrentAgentsAppear()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-agents", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();
        alpha.Record(DevelopmentStage.Implementation, AgentEventTypes.FileModified, "Alpha changed a file.");
        beta.Record(DevelopmentStage.Testing, AgentEventTypes.TestStarted, "Beta started tests.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
        AssertEqual(2, snapshot.All!.Agents.Count);
        Assert(snapshot.Navigation.Items.Count(item => item.Kind == "agent") == 2,
            "each mod must have one navigation item");
    }

    public static void MultipleConcurrentRunsRemainVisible()
    {
        using var store = new AgentObservabilityStore();
        using var ui = new AgentObservabilityUi(store);
        using var firstRun = new AgentObservabilityRun(
            "ui-run-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using var secondRun = new AgentObservabilityRun(
            "ui-run-second",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.first",
            "First");
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.second",
            "Second");

        first.Start();
        second.Start();

        AgentObservabilityUiNavigationItem[] navigationAgents = ui.Snapshot.Navigation.Items
            .Where(static item => item.Kind == "agent")
            .ToArray();
        AssertEqual(2, navigationAgents.Length,
            "concurrent runs must keep both agent tabs visible");
        Assert(navigationAgents.Any(item => item.FullLabel == "First"));
        Assert(navigationAgents.Any(item => item.FullLabel == "Second"));
        AssertEqual(2, ui.Snapshot.All!.Agents.Count,
            "All activity must retain agents from concurrent runs");
    }

    public static void InterleavedEventsRemainChronological()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-order", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();
        alpha.Record(DevelopmentStage.Analysis, AgentEventTypes.FileInspected, "A1");
        beta.Record(DevelopmentStage.Analysis, AgentEventTypes.FileInspected, "B1");
        alpha.Record(DevelopmentStage.Implementation, AgentEventTypes.FileModified, "A2");

        using var ui = new AgentObservabilityUi(store);
        IReadOnlyList<AgentObservabilityActivityRow> activity = ui.Snapshot.All!.Activity;
        Assert(activity.Select(row => row.Sequence).SequenceEqual(
            activity.Select(row => row.Sequence).OrderBy(value => value)),
            "All activity must use stable sequence order");
        Assert(activity.Any(row => row.ModName == "Alpha") && activity.Any(row => row.ModName == "Beta"),
            "interleaved rows must retain their originating mod");
    }

    public static void IndividualAgentViewFiltersCorrectly()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-filter", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();
        alpha.Record(DevelopmentStage.Implementation, AgentEventTypes.FileModified, "Alpha event.");
        beta.Record(DevelopmentStage.Implementation, AgentEventTypes.FileModified, "Beta event.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView view = ui.ShowAgent("mod.alpha").Agent!;
        AssertEqual("Alpha", view.Agent.ModName);
        Assert(view.RecentActivity.All(row => row.Event.AgentId == alpha.AgentId),
            "individual view must exclude other agents");
        Assert(view.RecentActivity.Any(row => row.Activity == "Alpha event."));
    }

    public static void IssuesViewContainsOnlyStructuredIssues()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-issues", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.issues", "Issues");
        agent.Start();
        agent.Record(DevelopmentStage.Research, AgentEventTypes.FileInspected, "Normal activity.");
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Tests failed.",
            new { operationKey = "test:issues", errorCode = "TEST_FAILED", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssuesView issues = ui.ShowIssues().Issues!;
        Assert(issues.Issues.Count > 0, "failure should appear in Issues");
        Assert(issues.Issues.All(row => row.Issue.Id.StartsWith("issue-", StringComparison.Ordinal)),
            "Issues must contain structured issue records only");
    }

    public static void RecoveredAndUnresolvedStatesAreDistinct()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-recovery", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.recovery", "Recovery");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Recovered command failed.",
            new { operationKey = "command:recovered", command = "tool recovered", exitCode = 1 });
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RecoveryCompleted,
            "Fallback succeeded.",
            new { operationKey = "command:recovered", recovered = true });
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Unresolved command failed.",
            new { operationKey = "command:unresolved", command = "tool unresolved", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssuesView issues = ui.ShowIssues().Issues!;
        Assert(issues.Issues.Any(row => row.State == "recovered"));
        Assert(issues.Issues.Any(row => row.State == "unresolved"));
    }

    public static void IssueDetailResolvesSupportingEvents()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-detail", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.detail", "Detail");
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.ToolFailed,
            "Compiler failed.",
            new
            {
                operationKey = "tool:compiler",
                toolName = "compiler",
                command = "compiler Source/Detail.cs",
                filePath = "Source/Detail.cs",
                stderr = "CS0246 missing type",
                exitCode = 1
            });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssueDetail detail = ui.ShowIssue(issue.Id).Issue!;
        Assert(detail.UnresolvedEventIds.Count == 0, "all issue event references must resolve");
        Assert(detail.SupportingEvents.Any(eventRecord => eventRecord.Id == issue.EventIds[0]));
        Assert(detail.RelatedFiles.Contains("Source/Detail.cs"));
        Assert(detail.RelatedCommands.Contains("compiler Source/Detail.cs"));
        Assert(detail.Output.Any(output => output.Text.Contains("CS0246", StringComparison.Ordinal)));
    }

    public static void ViewActivityNavigatesToAgentContext()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-navigation", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.navigation", "Navigation");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Navigation failure.",
            new { operationKey = "test:navigation", exitCode = 1 });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();

        using var ui = new AgentObservabilityUi(store);
        ui.ShowIssue(issue.Id);
        AgentObservabilityAgentView agentView = ui.ShowAgent(agent.AgentId).Agent!;
        AssertEqual(AgentObservabilityUiView.Agent, ui.CurrentView);
        Assert(agentView.RecentActivity.Any(row => issue.EventIds.Contains(row.Event.Id)),
            "issue activity navigation must land on supporting agent events");
    }

    public static void MultipleIssueSelectionBuildsCorrectBundle()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-bundle", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.bundle", "Bundle");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "First failure.",
            new { operationKey = "test:first", filePath = "Source/First.cs", exitCode = 1 });
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Second failure.",
            new { operationKey = "build:second", filePath = "Source/Second.cs", exitCode = 1 });
        AgentIssue[] issues = store.GetIssues(agentId: agent.AgentId).ToArray();

        using var ui = new AgentObservabilityUi(store);
        AgentDiagnosticBundle bundle = ui.PrepareAssessment(issues.Select(issue => issue.Id));
        AssertEqual(issues.Length, bundle.Issues.Count);
        Assert(bundle.Files.Contains("Source/First.cs"));
        Assert(bundle.Files.Contains("Source/Second.cs"));
    }

    public static void NewAgentsAppearLive()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-live-agents", store, new NoopAgentObservabilityTelemetry());
        using var ui = new AgentObservabilityUi(store);
        var updates = new List<AgentObservabilityUiUpdate>();
        using IDisposable subscription = ui.Subscribe(updates.Add);
        using AgentObservabilitySession agent = run.CreateAgent("mod.live", "Live");
        agent.Start();

        Assert(updates.Any(update => update.Kind == AgentObservabilityUiUpdateKind.AgentChanged));
        Assert(ui.Snapshot.Navigation.Items.Any(item => item.FullLabel == "Live"));
    }

    public static void NewIssuesAppearLive()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-live-issues", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.live-issue", "Live Issue");
        agent.Start();
        using var ui = new AgentObservabilityUi(store);
        var updates = new List<AgentObservabilityUiUpdate>();
        using IDisposable subscription = ui.Subscribe(updates.Add);
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Live issue.",
            new { operationKey = "test:live", exitCode = 1 });

        Assert(updates.Any(update => update.Kind == AgentObservabilityUiUpdateKind.IssueChanged));
        Assert(ui.ShowIssues().Issues!.Issues.Any(row => row.Issue.Summary.Contains("Live issue", StringComparison.Ordinal)));
    }

    public static void OneAgentCanFailWhileAnotherContinues()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-fail-continue", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession failed = run.CreateAgent("mod.failed", "Failed");
        using AgentObservabilitySession running = run.CreateAgent("mod.running", "Running");
        failed.Start();
        running.Start();
        failed.Fail("Agent failed.", "FAILED");
        running.Record(DevelopmentStage.Implementation, AgentEventTypes.FileModified, "Still working.");

        using var ui = new AgentObservabilityUi(store);
        IReadOnlyList<AgentSnapshot> agents = ui.Snapshot.All!.Agents;
        AssertEqual(AgentStatus.Failed, agents.Single(agent => agent.ModId == "mod.failed").Status);
        AssertEqual(AgentStatus.Running, agents.Single(agent => agent.ModId == "mod.running").Status);
    }

    public static void OneAgentCanCompleteWhileAnotherContinues()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-complete-continue", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession complete = run.CreateAgent("mod.complete", "Complete");
        using AgentObservabilitySession running = run.CreateAgent("mod.running", "Running");
        complete.Start();
        running.Start();
        complete.Complete();
        running.Record(DevelopmentStage.Testing, AgentEventTypes.TestStarted, "Still testing.");

        using var ui = new AgentObservabilityUi(store);
        IReadOnlyList<AgentSnapshot> agents = ui.Snapshot.All!.Agents;
        AssertEqual(AgentStatus.Completed, agents.Single(agent => agent.ModId == "mod.complete").Status);
        AssertEqual(AgentStatus.Running, agents.Single(agent => agent.ModId == "mod.running").Status);
    }

    public static void ViewSwitchingIsLocalAndDoesNotCallModels()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-local", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.local", "Local");
        agent.Start();
        using var ui = new AgentObservabilityUi(store);
        ui.ShowIssues();
        ui.ShowAgent(agent.AgentId);
        ui.ShowAll();
        AssertEqual(AgentObservabilityUiView.All, ui.CurrentView);
        Assert(store.GetEvents().All(eventRecord => !eventRecord.Type.Contains("llm", StringComparison.OrdinalIgnoreCase)),
            "view switching must not create model calls");
    }

    public static void BundlePreparationIsLocalAndDoesNotCallModels()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-local-bundle", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.local-bundle", "Local Bundle");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Local bundle failure.",
            new { operationKey = "test:local-bundle", exitCode = 1 });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
        using var ui = new AgentObservabilityUi(store);
        AgentDiagnosticBundle bundle = ui.PrepareAssessment([issue.Id]);
        AssertEqual(1, bundle.Issues.Count);
        Assert(store.GetEvents().All(eventRecord => !eventRecord.Type.Contains("llm", StringComparison.OrdinalIgnoreCase)),
            "bundle preparation must not create model calls");
    }

    public static void OTelDisabledDoesNotAffectDesktopViews()
    {
        using var store = new AgentObservabilityStore();
        using var telemetry = new OpenTelemetryAgentTelemetry(
            new AgentObservabilityTelemetryOptions { Enabled = false });
        using var run = new AgentObservabilityRun("ui-otel-off", store, telemetry);
        using AgentObservabilitySession agent = run.CreateAgent("mod.otel-off", "OTel Off");
        agent.Start();
        agent.Record(DevelopmentStage.Analysis, AgentEventTypes.SearchCompleted, "Search completed.");

        using var ui = new AgentObservabilityUi(store);
        Assert(ui.Snapshot.All is not null);
        Assert(ui.ShowAgent(agent.AgentId).Agent is not null);
        Assert(ui.ShowIssues().Issues is not null);
    }

    public static void LargeVolumeViewsRemainBounded()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun("ui-volume", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.volume", "Volume");
        agent.Start();
        for (int index = 0; index < 3_000; index++)
        {
            agent.Record(
                DevelopmentStage.Research,
                AgentEventTypes.FileInspected,
                "Inspected file " + index,
                new { filePath = "Source/File" + index + ".cs", operationKey = "file:" + index });
        }

        using var ui = new AgentObservabilityUi(
            store,
            new AgentObservabilityUiOptions
            {
                MaximumActivityRows = 100,
                MaximumIndexedEvents = 500,
                MaximumRecentActivityRows = 25,
                MaximumIssueRows = 50,
                MaximumIndexedIssues = 100
            });
        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
        stopwatch.Stop();
        Assert(snapshot.All!.Activity.Count <= 100);
        Assert(snapshot.All.HasMoreActivity);
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "bounded volume view should remain responsive");
    }

    public static void AgentNavigationIdentityAndDismissalAreStable()
    {
        using var store = new AgentObservabilityStore();
        using (var historicalRun = new AgentObservabilityRun(
                   "ui-history-run",
                   store,
                   new NoopAgentObservabilityTelemetry()))
        using (AgentObservabilitySession historical = historicalRun.CreateAgent(
                   "mod.history",
                   "Historical Mod",
                   "shared-agent"))
        {
            historical.Start();
            historical.Complete();
        }

        using var currentRun = new AgentObservabilityRun(
            "ui-current-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession current = currentRun.CreateAgent(
            "mod.current",
            "Current Mod",
            "shared-agent");
        current.Start();
        current.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Current activity.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] navigationAgents = ui.Snapshot.Navigation.Items
            .Where(item => item.Kind == "agent")
            .ToArray();
        AssertEqual(2, navigationAgents.Length);
        Assert(navigationAgents.Any(item => item.FullLabel == "Current Mod" &&
            item.Key.Contains("ui-current-run", StringComparison.Ordinal)));
        Assert(navigationAgents.Any(item => item.FullLabel == "Historical Mod" &&
            item.Key.Contains("ui-history-run", StringComparison.Ordinal)));
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);

        Assert(!ui.DismissAgent(current.AgentId, current.RunId),
            "active agents must not be dismissed");
        current.Complete();
        Assert(ui.Snapshot.Navigation.Items.Any(item =>
            item.AgentId == current.AgentId &&
            item.RunId == current.RunId &&
            item.CanDismiss));

        Assert(ui.DismissAgent(current.AgentId, current.RunId));
        Assert(!ui.Snapshot.Navigation.Items.Any(item =>
            item.AgentId == current.AgentId &&
            item.RunId == current.RunId));
        Assert(ui.Snapshot.Navigation.Items.Any(item =>
            item.FullLabel == "Historical Mod"));
        Assert(ui.Snapshot.All!.Agents.Any(agent => agent.AgentId == current.AgentId),
            "dismissal must not remove authoritative agent history");
        Assert(store.GetEvents(runId: current.RunId, agentId: current.AgentId).Count > 0,
            "dismissal must preserve events");

        AgentSnapshot lateUpdate = current.Snapshot with { CurrentActivity = "late refresh" };
        store.UpdateAgent(lateUpdate);
        Assert(!ui.Snapshot.Navigation.Items.Any(item =>
            item.AgentId == current.AgentId &&
            item.RunId == current.RunId),
            "a late store refresh must not recreate a dismissed tab");

        AgentObservabilityUi oldRunUi = new(store, runId: "ui-history-run");
        using (oldRunUi)
        {
            Assert(oldRunUi.Snapshot.Navigation.Items.Any(item =>
                item.FullLabel == "Historical Mod"));
        }
    }

    public static void IssueSelectionAndAssessmentSurviveLiveUpdates()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-selection-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.selection",
            "Selection Mod");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "First issue.",
            new { operationKey = "test:first", exitCode = 1 });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();

        using var ui = new AgentObservabilityUi(store, runId: run.RunId);
        ui.ShowIssues();
        ui.SelectIssue(issue.Id);
        AssertEqual(AgentObservabilityUiView.Issues, ui.CurrentView);
        AssertEqual(issue.Id, ui.Snapshot.SelectedIssueId);
        Assert(ui.Snapshot.Issue is not null, "row selection must expose issue detail");

        AgentDiagnosticBundle bundle = ui.PrepareAssessment([issue.Id]);
        AgentObservabilityUiSnapshot assessment = ui.Snapshot;
        AssertEqual(AgentObservabilityIssueMode.Assessment, assessment.IssueMode);
        Assert(assessment.Assessment is not null);
        Assert(assessment.Assessment!.IssueIds.SequenceEqual(bundle.IssueIds));

        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Second issue.",
            new { operationKey = "build:second", filePath = "Source/Second.cs", exitCode = 1 });
        AgentObservabilityUiSnapshot afterLiveIssue = ui.Snapshot;
        Assert(
            afterLiveIssue.IssueMode == AgentObservabilityIssueMode.Assessment,
            "new issue arrival must not reset an assessment");
        AssertEqual(issue.Id, afterLiveIssue.SelectedIssueId);
        Assert(afterLiveIssue.Assessment!.IssueIds.SequenceEqual(bundle.IssueIds));

        ui.ShowIssue(issue.Id);
        AssertEqual(AgentObservabilityIssueMode.Details, ui.Snapshot.IssueMode);
        Assert(ui.Snapshot.Issue is not null);
        Assert(ui.Snapshot.Assessment is null);
    }

    public static void ActivitySelectionResolvesRelatedDetailsAndSurvivesLiveEvents()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-event-selection-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.event-selection",
            "Event Selection Mod");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Test started.",
            new
            {
                operationKey = "test:detail",
                toolName = "dotnet test",
                command = "dotnet test Source/EventSelection.csproj",
                filePath = "Source/EventSelection.cs"
            });
        AgentEvent failed = agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Test failed.",
            new
            {
                operationKey = "test:detail",
                toolName = "dotnet test",
                command = "dotnet test Source/EventSelection.csproj",
                filePath = "Source/EventSelection.cs",
                stderr = "assertion failed",
                durationMs = 42,
                exitCode = 1
            })!;
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
        AgentEvent other = agent.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.FileInspected,
            "Inspected another file.",
            new { filePath = "Source/Other.cs" })!;

        using var ui = new AgentObservabilityUi(store, runId: run.RunId);
        AgentObservabilityUiSnapshot agentView = ui.ShowAgent(
            agent.AgentId,
            run.RunId,
            failed.Id);
        AgentObservabilityEventDetail detail = agentView.Agent!.SelectedEvent!;
        AssertEqual(failed.Id, agentView.SelectedEventId);
        Assert(detail.Files.Contains("Source/EventSelection.cs"));
        Assert(detail.Tools.Contains("dotnet test"));
        Assert(detail.Commands.Contains("dotnet test Source/EventSelection.csproj"));
        Assert(detail.TestResults.Any(result => result.Status == "failed"));
        Assert(detail.RelatedIssueIds.Contains(issue.Id));
        Assert(detail.Output.Any(output => output.Text.Contains("assertion failed", StringComparison.Ordinal)));
        AssertEqual(42L, detail.DurationMilliseconds);

        AgentObservabilityUiSnapshot otherView = ui.SelectEvent(other.Id);
        AssertEqual(other.Id, otherView.Agent!.SelectedEventId);
        AssertEqual(other.Id, otherView.Agent.SelectedEvent!.Event.Id);
        Assert(otherView.Agent.SelectedEvent.Files.Contains("Source/Other.cs"));
        Assert(otherView.Agent.SelectedEvent.TestResults.Count == 0);
        Assert(otherView.Agent.SelectedEvent.RelatedIssueIds.Count == 0);

        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "A live update.",
            new { filePath = "Source/Live.cs" });
        AgentObservabilityUiSnapshot afterLiveEvent = ui.Snapshot;
        Assert(
            other.Id == afterLiveEvent.SelectedEventId,
            "incoming events must not overwrite an explicit historical selection");
        AssertEqual(other.Id, afterLiveEvent.Agent!.SelectedEvent!.Event.Id);
    }

    public static void ExistingCliUiRemainsAvailable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = CliApplication.RunAsync(
                ["list", "--catalog", Path.Combine(Directory.GetCurrentDirectory(), "TestCatalog", "rimtest.catalog.json")],
                output,
                error,
                observabilityTelemetry: new NoopAgentObservabilityTelemetry())
            .GetAwaiter()
            .GetResult();
        AssertEqual(CliExitCodes.Success, exitCode);
        Assert(string.IsNullOrWhiteSpace(error.ToString()), "existing CLI UI surface should remain usable");
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

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message);
        }
    }
}
