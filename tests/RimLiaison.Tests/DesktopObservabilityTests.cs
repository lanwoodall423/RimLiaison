using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RimLiaison;
using RimLiaison.Observability;
using RimLiaison.Desktop;
using System.Runtime.InteropServices;
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
        AssertEqual(2, navigationAgents.Length);
        Assert(navigationAgents.Any(item => item.FullLabel == "First"));
        Assert(navigationAgents.Any(item => item.FullLabel == "Second"));
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);
    }

    public static void RepeatedRunsForSameModShareOneTab()
    {
        using var store = new AgentObservabilityStore();
        using var historicalRun = new AgentObservabilityRun(
            "ui-wildlife-history",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession historical = historicalRun.CreateAgent(
            "Wildlife",
            "Wildlife",
            logicalAgentId: "logical-wildlife");
        historical.Start();
        historical.Complete();

        using var activeRun = new AgentObservabilityRun(
            "ui-wildlife-active",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession active = activeRun.CreateAgent(
            "Wildlife",
            "Wildlife",
            logicalAgentId: "logical-wildlife");
        active.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] wildlifeTabs = ui.Snapshot.Navigation.Items
            .Where(item => item.Kind == "agent" && item.ModId == "Wildlife")
            .ToArray();

        AssertEqual(1, wildlifeTabs.Length);
        AssertEqual(active.RunId, wildlifeTabs[0].RunId);
        AssertEqual(active.AgentId, wildlifeTabs[0].AgentId);
        AssertEqual(AgentStatus.Running, wildlifeTabs[0].Status);
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);
        AgentObservabilityAgentView activeView = ui.ShowAgent("Wildlife").Agent!;
        AssertEqual(active.RunId, activeView.CurrentSession.RunId);
        AssertEqual(1, activeView.PastSessions.Count);
        AgentObservabilitySessionSummary past = activeView.PastSessions.Single();
        AgentObservabilityAgentView pastView = ui.ShowAgent(past.AgentId, past.RunId).Agent!;
        AssertEqual(past.RunId, pastView.Agent.RunId);
        AssertEqual(active.RunId, pastView.CurrentSession.RunId);
    }
    public static void ElevenSessionsOfOneLogicalAgentCountOnce()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 11; index++)
        {
            using var run = new AgentObservabilityRun(
                "ui-eleven-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession session = run.CreateAgent(
                "mod.frontier",
                "Frontier",
                "frontier-session-" + index,
                "logical-frontier");
            session.Start();
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "RimLiaison command failed.",
                new
                {
                    operationKey = "command:frontier:" + index,
                    toolName = "RimLiaison",
                    errorCode = "RIMLIAISON_COMMAND_FAILED",
                    command = "rimliaison affected",
                    exitCode = 1
                });
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssueRow row = ui.ShowIssues().Issues!.Issues
            .First(value => value.Issue.ModId == "mod.frontier");
        AssertEqual(1, row.SharedAgentCount);
        AssertEqual(11, row.SharedTooling!.AffectedSessionCount);
    }

    public static void DistinctConcurrentLogicalAgentsRemainSeparate()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-concurrent-frontier",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent(
            "mod.frontier",
            "Frontier",
            "frontier-one-session",
            "logical-frontier-one");
        using AgentObservabilitySession second = run.CreateAgent(
            "mod.frontier",
            "Frontier",
            "frontier-two-session",
            "logical-frontier-two");
        using AgentObservabilitySession third = run.CreateAgent(
            "mod.frontier",
            "Frontier",
            "frontier-three-session",
            "logical-frontier-three");
        third.Start();
        RecordSharedFailure(third, "three");
        first.Start();
        second.Start();
        RecordSharedFailure(first, "one");
        RecordSharedFailure(second, "two");

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(item => item.Kind == "agent"));
        AgentObservabilityIssueRow firstIssue = ui.ShowIssues().Issues!.Issues
            .First(value => value.Issue.AgentId == first.AgentId);
        AssertEqual(3, firstIssue.SharedAgentCount);
    }
    public static void TopNavigationGroupsAllRimLiaisonSessions()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 4; index++)
        {
            store.RegisterAgent(IdentityAgent(
                "tool-run-" + index,
                "tool-agent-" + index,
                ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison")));
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] items = ui.Snapshot.Navigation.Items
            .Where(static item => item.Kind == "agent")
            .ToArray();
        AssertEqual(1, items.Length);
        AssertEqual("tool:rimliaison", items[0].CanonicalEntityId);
        AssertEqual("RimLiaison", items[0].FullLabel);
    }

    public static void ToolAliasesShareOneCanonicalIdentity()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "alias-run-one",
            AgentId = "alias-agent-one",
            SessionId = "alias-session-one",
            ModId = "[Tool] RimLiaison",
            ModName = "[Tool] RimLiaison",
            EntityType = ObservabilityEntityTypes.Tool
        });
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "alias-run-two",
            AgentId = "alias-agent-two",
            SessionId = "alias-session-two",
            ModId = "RimLiaison-tests-alias",
            ModName = "RimLiaison-tests-alias",
            EntityType = ObservabilityEntityTypes.Tool,
            CanonicalEntityId = "tool:RimLiaison-worktree-alias"
        });

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        Assert(store.GetAgents().All(static agent =>
            agent.EntityType == ObservabilityEntityTypes.Tool &&
            agent.CanonicalEntityId == "tool:rimliaison"));
    }

    public static void QualificationFixtureIsHiddenFromProductionNavigation()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "qualification-ui-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "rimliaison.qualification.fixture",
            "RimLiaison Qualification Fixture",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"),
            workloadKind: "qualification",
            qualificationProfile: "deterministic");
        agent.Start();

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(0, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AssertEqual(1, ui.Snapshot.All!.Agents.Count);
        AssertEqual(ObservabilityEntityTypes.Fixture, ui.Snapshot.All.Agents[0].EntityType);
    }

    public static void SyntheticFixtureModIsNotTopLevelProductionEntity()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(IdentityAgent(
            "fixture-run",
            "fixture-agent",
            ObservabilityEntityIdentity.ForFixture("com.example.fixturemod", "FixtureMod")));

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(0, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AssertEqual(ObservabilityEntityTypes.Fixture, store.GetAgents()[0].EntityType);
    }

    public static void NonProductionIdentityTaxonomyIsHidden()
    {
        using var store = new AgentObservabilityStore();
        (string Id, ObservabilityEntityIdentity Identity)[] identities =
        [
            ("agent", ObservabilityEntityIdentity.ForAgent("agent-1", "Agent")),
            ("user", ObservabilityEntityIdentity.ForUser("lan", "Lan")),
            ("process", ObservabilityEntityIdentity.ForProcess("process-1", "Process")),
            ("run", ObservabilityEntityIdentity.ForRun("run-1", "Run")),
            ("session", ObservabilityEntityIdentity.ForSession("session-1", "Session")),
            ("activity", ObservabilityEntityIdentity.ForActivity("activity-1", "Activity")),
            ("event", ObservabilityEntityIdentity.ForEvent("event-1", "Event"))
        ];
        foreach ((string id, ObservabilityEntityIdentity identity) in identities)
        {
            store.RegisterAgent(IdentityAgent(
                "taxonomy-" + id,
                "taxonomy-agent-" + id,
                identity));
        }

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(0, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AssertEqual(identities.Length, ui.Snapshot.All!.Agents.Count);
    }

    public static void RealModsAndEcosystemToolsRemainDistinct()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(IdentityAgent(
            "mod-run-one",
            "mod-agent-one",
            ObservabilityEntityIdentity.ForMod("com.example.alpha", "Alpha")));
        store.RegisterAgent(IdentityAgent(
            "mod-run-two",
            "mod-agent-two",
            ObservabilityEntityIdentity.ForMod("com.example.beta", "Beta")));
        store.RegisterAgent(IdentityAgent(
            "tool-run",
            "tool-agent",
            ObservabilityEntityIdentity.ForTool("rimtest", "RimTest")));

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] items = ui.Snapshot.Navigation.Items
            .Where(static item => item.Kind == "agent")
            .ToArray();
        AssertEqual(3, items.Length);
        Assert(items.Any(static item => item.CanonicalEntityId == "mod:com.example.alpha"));
        Assert(items.Any(static item => item.CanonicalEntityId == "mod:com.example.beta"));
        Assert(items.Any(static item => item.CanonicalEntityId == "tool:rimtest"));
    }

    public static void MalformedLegacyIdentityStaysDiagnosticOnly()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "malformed-run",
            AgentId = "malformed-agent",
            SessionId = "malformed-session",
            ModId = "Lan",
            ModName = "Lan"
        });
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "malformed-fixture-run",
            AgentId = "malformed-fixture-agent",
            SessionId = "malformed-fixture-session",
            ModId = "Fixture",
            ModName = "Fixture"
        });

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(0, ui.Snapshot.Navigation.Items.Count(static item => item.Kind == "agent"));
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);
    }


    public static void LegacyRecordsUseStableFallbackEntity()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(LegacyAgent("legacy-run-one", "legacy-agent-one"));
        store.RegisterAgent(LegacyAgent("legacy-run-two", "legacy-agent-two"));

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(item => item.Kind == "agent"));
    }

    public static void CompletedRunsRemainVisibleWithoutDismissal()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "ui-persistent-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "Aquaculture",
            "Aquaculture",
            logicalAgentId: "logical-aquaculture");
        first.Start();
        first.Complete();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem firstTab = ui.Snapshot.Navigation.Items
            .Single(item => item.Kind == "agent" && item.ModId == "Aquaculture");
        Assert(!firstTab.CanDismiss, "completed agents must not expose dismiss in monitoring navigation");

        using var secondRun = new AgentObservabilityRun(
            "ui-persistent-second",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "Aquaculture",
            "Aquaculture",
            logicalAgentId: "logical-aquaculture");
        second.Start();
        AssertEqual(AgentStatus.Running, second.Snapshot.Status);
        AgentObservabilityUiNavigationItem activeTab = ui.Snapshot.Navigation.Items
            .Single(item => item.Kind == "agent" && item.ModId == "Aquaculture");
        AssertEqual(1, ui.Snapshot.Navigation.Items.Count(item => item.Kind == "agent"));
        AssertEqual(second.RunId, activeTab.RunId);
        AssertEqual(AgentStatus.Running, activeTab.Status);
        AssertEqual(AgentObservabilityAgentNavigationStatus.Working, activeTab.NavigationStatus);
        Assert(ui.Snapshot.All!.Agents.Count(agent => agent.ModId == "Aquaculture") == 2);
        using var restartedUi = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem restartedTab = restartedUi.Snapshot.Navigation.Items
            .Single(item => item.Kind == "agent" && item.ModId == "Aquaculture");
        AssertEqual(second.RunId, restartedTab.RunId);
    }

    public static void ActiveAgentsArePrioritizedInBoundedNavigation()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 3; index++)
        {
            using var historicalRun = new AgentObservabilityRun(
                "ui-history-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession historical = historicalRun.CreateAgent(
                "mod.history." + index,
                "A Historical " + index);
            historical.Start();
            historical.Complete();
        }

        using var firstRun = new AgentObservabilityRun(
            "ui-active-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using var secondRun = new AgentObservabilityRun(
            "ui-active-second",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.active.first",
            "Z Active First");
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.active.second",
            "Z Active Second");
        first.Start();
        second.Start();

        using var ui = new AgentObservabilityUi(
            store,
            new AgentObservabilityUiOptions
            {
                MaximumNavigationAgents = 2,
                MaximumIndexedAgents = 10
            });
        AgentObservabilityUiNavigationItem[] navigationAgents = ui.Snapshot.Navigation.Items
            .Where(static item => item.Kind == "agent")
            .ToArray();

        AssertEqual(2, navigationAgents.Length);
        Assert(navigationAgents.All(item =>
            item.Status is AgentStatus.Created or AgentStatus.Running or AgentStatus.Waiting),
            "bounded navigation must retain active agents before finished history");
        Assert(navigationAgents.Any(item => item.FullLabel == "Z Active First"));
        Assert(navigationAgents.Any(item => item.FullLabel == "Z Active Second"));
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
            activity.Select(row => row.Sequence).OrderByDescending(value => value)),
            "All activity must use newest-first sequence order");
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

    public static void AgentNavigationIdentityAndHistoryAreStable()
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
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Current blocking failure.",
            new { operationKey = "test:current", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] navigationAgents = ui.Snapshot.Navigation.Items
            .Where(item => item.Kind == "agent")
            .ToArray();
        AssertEqual(2, navigationAgents.Length);
        AgentObservabilityUiNavigationItem currentTab = navigationAgents
            .Single(item => item.ModId == "mod.current");
        AssertEqual("ui-current-run", currentTab.RunId);
        Assert(currentTab.HasUnresolvedError);
        AssertEqual(
            AgentObservabilityAgentNavigationStatus.NeedsAttention,
            currentTab.NavigationStatus);
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);

        current.Complete();
        Assert(ui.Snapshot.Navigation.Items.Any(item =>
            item.ModId == "mod.current" &&
            item.RunId == current.RunId &&
            item.NavigationStatus == AgentObservabilityAgentNavigationStatus.NeedsAttention));
        Assert(ui.Snapshot.All!.Agents.Any(agent => agent.AgentId == current.AgentId),
            "completion must not remove authoritative agent history");
        Assert(store.GetEvents(runId: current.RunId, agentId: current.AgentId).Count > 0,
            "completion must preserve events");

        using var oldRunUi = new AgentObservabilityUi(store, runId: "ui-history-run");
        Assert(oldRunUi.Snapshot.Navigation.Items.Any(item =>
            item.FullLabel == "Historical Mod"));
    }
    public static void ActivityRefreshPlanIsIncremental()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-activity-reconcile",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.reconcile", "Reconcile");
        agent.Start();
        agent.Record(DevelopmentStage.Analysis, AgentEventTypes.FileInspected, "First.");
        agent.Record(DevelopmentStage.Analysis, AgentEventTypes.FileInspected, "Second.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityActivityRow[] existingRows = ui.Snapshot.All!.Activity.ToArray();
        AgentObservabilityActivityRow newest = new(
            new AgentEvent
            {
                Id = "event-newest",
                RunId = run.RunId,
                AgentId = agent.AgentId,
                ModId = agent.ModId,
                Timestamp = existingRows[^1].Event.Timestamp + 1,
                Sequence = existingRows[^1].Event.Sequence + 1,
                Stage = DevelopmentStage.Analysis,
                Type = AgentEventTypes.FileInspected,
                Summary = "Newest."
            },
            "Reconcile",
            AgentStatus.Running,
            false,
            []);
        var current = existingRows
            .Select(row => new AgentObservabilityActivityListItem(row.Event.Id, row))
            .ToArray();

        AgentObservabilityActivityReconciliationPlan unchanged =
            AgentObservabilityActivityReconciliation.Plan(current, existingRows);
        Assert(!unchanged.HasChanges, "unchanged refresh must perform no row operations");

        AgentObservabilityActivityRow[] newestFirst = [newest, .. existingRows];
        AgentObservabilityActivityReconciliationPlan inserted =
            AgentObservabilityActivityReconciliation.Plan(current, newestFirst);
        Assert(inserted.InsertedEventIds.SequenceEqual(["event-newest"]));
        AssertEqual(0, inserted.MovedEventIds.Count);
        AssertEqual(0, inserted.RemovedEventIds.Count);
        Assert(!inserted.UpdatedEventIds.Contains(existingRows[0].Event.Id));
        AgentObservabilityActivityRow[] changedRows = existingRows.ToArray();
        changedRows[0] = changedRows[0] with
        {
            Event = changedRows[0].Event with { Summary = "Changed." }
        };
        AgentObservabilityActivityReconciliationPlan changed =
            AgentObservabilityActivityReconciliation.Plan(current, changedRows);
        Assert(changed.UpdatedEventIds.SequenceEqual([existingRows[0].Event.Id]));
    }

    public static void DesktopPresentationReconciliationIsStable()
    {
        AgentObservabilityStageProgress[] stages =
        [
            new(DevelopmentStage.Analysis, "completed", false),
            new(DevelopmentStage.Research, "completed", false),
            new(DevelopmentStage.Implementation, "running", true),
            new(DevelopmentStage.Testing, "pending", false),
            new(DevelopmentStage.Packaging, "pending", false),
            new(DevelopmentStage.Complete, "pending", false)
        ];
        long stageMutationCount = 0;
        for (int iteration = 0; iteration < 100; iteration++)
        {
            AgentObservabilityStageReconciliationPlan plan =
                AgentObservabilityStageReconciliation.Plan(stages, stages);
            Assert(!plan.HasChanges, "unchanged stages must not mutate controls");
            stageMutationCount += plan.RemovedStages.Count +
                plan.MovedStages.Count +
                plan.UpdatedStages.Count +
                plan.InsertedStages.Count;
        }

        AssertEqual(0L, stageMutationCount);
        AgentObservabilityStageProgress[] changedStages = stages.ToArray();
        changedStages[2] = changedStages[2] with
        {
            State = "completed",
            IsCurrent = false
        };
        AgentObservabilityStageReconciliationPlan stageUpdate =
            AgentObservabilityStageReconciliation.Plan(stages, changedStages);
        Assert(stageUpdate.UpdatedStages.SequenceEqual(
            [DevelopmentStage.Implementation]));
        AssertEqual(0, stageUpdate.RemovedStages.Count);
        AssertEqual(0, stageUpdate.MovedStages.Count);
        AssertEqual(0, stageUpdate.InsertedStages.Count);

        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-presentation-reconcile",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.presentation-reconcile",
            "Presentation Reconcile");
        agent.Start();
        for (int index = 0; index < 3; index++)
        {
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.TestFailed,
                "Issue " + index,
                new
                {
                    operationKey = "test:presentation-" + index,
                    toolName = "dotnet test",
                    exitCode = 1
                });
        }

        using var ui = new AgentObservabilityUi(store, runId: run.RunId);
        AgentObservabilityUiSnapshot allFirst = ui.Snapshot;
        AgentObservabilityUiSnapshot allSecond = ui.Snapshot;
        Assert(ReferenceEquals(allFirst.All, allSecond.All));
        Assert(ReferenceEquals(allFirst.Navigation, allSecond.Navigation));
        AgentObservabilityIssueListItem[] current = ui.ShowIssues().Issues!.Issues
            .Select(static row => new AgentObservabilityIssueListItem(row.Issue.Id, row))
            .ToArray();
        long issueMutationCount = 0;
        for (int iteration = 0; iteration < 100; iteration++)
        {
            AgentObservabilityIssueReconciliationPlan plan =
                AgentObservabilityIssueReconciliation.Plan(current, current);
            Assert(!plan.HasChanges, "unchanged issues must not mutate rows");
            issueMutationCount += plan.RemovedIssueIds.Count +
                plan.MovedIssueIds.Count +
                plan.UpdatedIssueIds.Count +
                plan.InsertedIssueIds.Count;
        }

        AssertEqual(0L, issueMutationCount);
        AgentObservabilityIssueListItem insertedItem = new(
            "synthetic-presentation-issue",
            current[0].Row with
            {
                Issue = current[0].Row.Issue with
                {
                    Id = "synthetic-presentation-issue",
                    Summary = "Synthetic issue"
                }
            });
        AgentObservabilityIssueReconciliationPlan insertion =
            AgentObservabilityIssueReconciliation.Plan(
                current,
                [insertedItem, .. current]);
        Assert(insertion.InsertedIssueIds.SequenceEqual(
            ["synthetic-presentation-issue"]));
        AssertEqual(0, insertion.RemovedIssueIds.Count);
        AssertEqual(0, insertion.UpdatedIssueIds.Count);

        AgentObservabilityIssueListItem updatedItem = current[0] with
        {
            Row = current[0].Row with
            {
                Issue = current[0].Row.Issue with { Summary = "Updated issue" }
            }
        };
        AgentObservabilityIssueReconciliationPlan update =
            AgentObservabilityIssueReconciliation.Plan(
                current,
                [updatedItem, .. current.Skip(1)]);
        Assert(update.UpdatedIssueIds.SequenceEqual([current[0].IssueId]));
        AssertEqual(0, update.RemovedIssueIds.Count);
        AssertEqual(0, update.InsertedIssueIds.Count);
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

    public static void IssueTriageClassifiesOwnersConservatively()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-triage-owners",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession devBridge = run.CreateAgent("mod.devbridge", "DevBridge Mod");
        using AgentObservabilitySession compiler = run.CreateAgent("mod.compiler", "Compiler Mod");
        using AgentObservabilitySession ambiguous = run.CreateAgent("mod.ambiguous", "Ambiguous Mod");
        devBridge.Start();
        compiler.Start();
        ambiguous.Start();
        devBridge.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Readiness identity mismatch occurred.",
            new
            {
                operationKey = "readiness:lease",
                toolName = "DevBridge2",
                command = "readiness check",
                errorCode = "READINESS_IDENTITY_MISMATCH",
                exitCode = 1
            });
        compiler.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Compiler reported CS0246.",
            new
            {
                operationKey = "build:compiler",
                toolName = "compiler",
                command = "dotnet build Source/Compiler.csproj",
                filePath = "Source/Compiler.cs",
                errorCode = "CS0246",
                diagnosticOutput = "error CS0246: missing type",
                causalDiagnostic = "error CS0246: missing type",
                exitCode = 1
            });
        ambiguous.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Operation failed.",
            new { operationKey = "command:ambiguous", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentIssue[] issues = store.GetIssues().ToArray();
        AgentObservabilityIssueTriage devBridgeTriage = ui.ShowIssue(
            issues.Single(issue => issue.AgentId == devBridge.AgentId).Id).Issue!.Triage!;
        AgentObservabilityIssueTriage compilerTriage = ui.ShowIssue(
            issues.Single(issue => issue.AgentId == compiler.AgentId).Id).Issue!.Triage!;
        AgentObservabilityIssueTriage ambiguousTriage = ui.ShowIssue(
            issues.Single(issue => issue.AgentId == ambiguous.AgentId).Id).Issue!.Triage!;
        AssertEqual("DevBridge2", devBridgeTriage.ProbableOwner.Owner);
        AssertEqual("high", devBridgeTriage.ProbableOwner.Confidence);
        AssertEqual("Mod / project", compilerTriage.ProbableOwner.Owner);
        AssertEqual("high", compilerTriage.ProbableOwner.Confidence);
        AssertEqual("Unknown", ambiguousTriage.ProbableOwner.Owner);
        AssertEqual("low", ambiguousTriage.ProbableOwner.Confidence);
    }

    public static void SharedToolingHintsAvoidUnrelatedFailures()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-shared-tooling",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent(
            "mod.first",
            "First",
            logicalAgentId: "logical-first");
        using AgentObservabilitySession second = run.CreateAgent(
            "mod.second",
            "Second",
            logicalAgentId: "logical-second");
        using AgentObservabilitySession unrelated = run.CreateAgent(
            "mod.unrelated",
            "Unrelated",
            logicalAgentId: "logical-unrelated");
        first.Start();
        second.Start();
        unrelated.Start();
        RecordReadinessFailure(first, "shared-1");
        RecordReadinessFailure(second, "shared-2");
        unrelated.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Different infrastructure failure.",
            new
            {
                operationKey = "readiness:different",
                toolName = "DevBridge2",
                errorCode = "READINESS_TIMEOUT",
                exitCode = 1
            });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssuesView issues = ui.ShowIssues().Issues!;
        AgentObservabilityIssueRow shared = issues.Issues.Single(row => row.ModName == "First");
        AssertEqual(2, shared.SharedAgentCount);
        AssertEqual("READINESS_IDENTITY_MISMATCH", shared.SharedTooling!.FailureCode);
        Assert(issues.Issues.All(row => row.Issue.AgentId != unrelated.AgentId ||
            row.SharedAgentCount == 0));
        long signatureComputationsBeforeRecovery = ui.IssueSignatureComputations;

        first.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RecoveryCompleted,
            "Readiness recovered.",
            new { operationKey = "readiness:shared-1", recovered = true });
        Assert(
            ui.IssueSignatureComputations > signatureComputationsBeforeRecovery,
            "relevant issue evidence must invalidate its cached signature");
        AgentObservabilityIssuesView ordered = ui.ShowIssues().Issues!;
        Assert(!ordered.Issues[0].Issue.Recovered,
            "unresolved issue must outrank recovered shared-tooling history");
    }
    public static void GenericWrapperCodesDoNotCreateSharedToolingCounts()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "run-generic-wrapper-1",
            store,
            new NoopAgentObservabilityTelemetry());
        using var secondRun = new AgentObservabilityRun(
            "run-generic-wrapper-2",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.generic.first",
            "First");
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.generic.second",
            "Second");
        first.Start();
        second.Start();
        first.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "RimLiaison command failed.",
            new { errorCode = "RIMLIAISON_COMMAND_FAILED", exitCode = 1 });
        second.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "RimLiaison command failed.",
            new { errorCode = "RIMLIAISON_COMMAND_FAILED", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssuesView issues = ui.ShowIssues().Issues!;
        Assert(issues.Issues.All(row =>
            row.SharedAgentCount == 0 &&
            (row.SharedTooling is null || row.SharedTooling.AffectedAgentCount == 0)),
            "generic wrapper codes alone must not claim shared tooling impact");
    }


    public static void ChatPacketContainsBoundedTriageAndMissingEvidence()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-chat-packet",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.chat", "Chat Mod");
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Build failed for Chat Mod.",
            new
            {
                operationKey = "build:chat",
                toolName = "compiler",
                command = "dotnet build Source/Chat.csproj",
                errorCode = "CS0246",
                exitCode = 1,
                transactionId = "tx-chat",
                workflowId = "wf-chat",
                project = "Chat.csproj",
                branch = "feature/chat",
                commitSha = "commit-chat"
            });
        using var unrelatedRun = new AgentObservabilityRun(
            "ui-chat-unrelated",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession unrelated = unrelatedRun.CreateAgent(
            "mod.unrelated-chat",
            "Unrelated Chat");
        unrelated.Start();
        unrelated.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Unrelated history must not be copied.",
            new { operationKey = "command:unrelated", errorCode = "OTHER_FAILURE", exitCode = 1 });

        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
        using var ui = new AgentObservabilityUi(store, runId: run.RunId);
        string packet = ui.CreateChatPacket(issue.Id);
        Assert(packet.Contains("Agent/mod: Chat Mod", StringComparison.Ordinal));
        Assert(packet.Contains("run=ui-chat-packet", StringComparison.Ordinal));
        Assert(packet.Contains("Issue: " + issue.Id, StringComparison.Ordinal));
        Assert(packet.Contains("Failure event:", StringComparison.Ordinal));
        Assert(packet.Contains("Command: dotnet build Source/Chat.csproj", StringComparison.Ordinal));
        Assert(packet.Contains("tx-chat", StringComparison.Ordinal));
        Assert(packet.Contains("wf-chat", StringComparison.Ordinal));
        Assert(packet.Contains("Evidence: Incomplete", StringComparison.Ordinal));
        Assert(packet.Contains("Missing evidence:", StringComparison.Ordinal));
        Assert(!packet.Contains("Unrelated history must not be copied", StringComparison.Ordinal));
        Assert(packet.Length <= 8_000);
    }

    public static void ChatGPTActionSupportsCheckedIssuesAndPreservesCausalEvidence()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-chat-selection",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent(
            "mod.chat.first",
            "Chat First",
            logicalAgentId: "logical-chat-first");
        using AgentObservabilitySession second = run.CreateAgent(
            "mod.chat.second",
            "Chat Second",
            logicalAgentId: "logical-chat-second");
        first.Start();
        second.Start();
        first.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "DevBridge validation failed.",
            new
            {
                operationKey = "affected:doctor:first",
                toolName = "DevBridge2",
                command = "rimliaison doctor --token supersecret --json",
                errorCode = "RIMLIAISON_COMMAND_FAILED",
                underlyingErrorCode = "OUTPUT_TOO_LARGE",
                outerErrorCode = "RIMLIAISON_COMMAND_FAILED",
                error = "The DevBridge response exceeded the output limit.",
                exitCode = 2,
                workingDirectory = "C:/RimDev/Repos/RimTest",
                processEvidence = new
                {
                    resolvedExecutablePath = "C:/RimDev/Tools/rimliaison.cmd",
                    resolvedToolRoot = "C:/RimDev/Tools"
                },
                stdout = "doctor output",
                stderr = "bounded stderr"
            });
        second.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Compiler validation failed.",
            new
            {
                operationKey = "build:second",
                toolName = "compiler",
                command = "dotnet build Chat.csproj",
                errorCode = "CS0246",
                exitCode = 1,
                project = "Chat.csproj"
            });

        AgentIssue firstIssue = store.GetIssues(agentId: first.AgentId).Single();
        AgentIssue secondIssue = store.GetIssues(agentId: second.AgentId).Single();
        using var ui = new AgentObservabilityUi(store, runId: run.RunId);
        IReadOnlyList<string> before = ui.Snapshot.SelectedIssueIds;
        AgentObservabilityChatPacket packet = ui.CreateChatPacket(
            [firstIssue.Id, secondIssue.Id]);

        AssertEqual(before, ui.Snapshot.SelectedIssueIds);
        Assert(packet.Text.Length <= AgentObservabilityIssueTriageBuilder.MaximumChatPacketCharacters);
        Assert(packet.Text.Contains("Selected issues: 2", StringComparison.Ordinal));
        Assert(packet.Text.Contains(firstIssue.Id, StringComparison.Ordinal));
        Assert(packet.Text.Contains(secondIssue.Id, StringComparison.Ordinal));
        Assert(packet.Text.Contains("Primary/root failure: code=OUTPUT_TOO_LARGE", StringComparison.Ordinal));
        Assert(packet.Text.Contains("Propagation: outerCode=RIMLIAISON_COMMAND_FAILED", StringComparison.Ordinal));
        Assert(packet.Text.Contains("Top-level workflow:", StringComparison.Ordinal));
        Assert(packet.Text.Contains("resolvedExecutablePath", StringComparison.Ordinal));
        Assert(packet.Text.Contains("exitCode=2", StringComparison.Ordinal));
        Assert(packet.Text.Contains("Affected occurrences:", StringComparison.Ordinal));
        Assert(packet.Text.Contains("Distinct durable logical agents:", StringComparison.Ordinal));
        Assert(packet.Text.Contains("CS0246", StringComparison.Ordinal));
        Assert(packet.Text.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert(!packet.Text.Contains("supersecret", StringComparison.Ordinal));

        using var form = new ObservabilityMainForm(store);
        form.Show();
        Application.DoEvents();
        SelectNavigation(form, "issues", "Issues");
        ListView issueList = GetPrivateField<ListView>(form, "issueList");
        issueList.Items[0].Selected = true;
        Application.DoEvents();
        Button chatButton = Descendants(form)
            .OfType<Button>()
            .Single(button => button.Text == "Copy to ChatGPT");
        Assert(issueList.CheckBoxes, "Issues must expose checkbox selection.");
        Assert(issueList.MultiSelect, "Issues must support multi-selection.");
        Assert(chatButton.Enabled, "Copy to ChatGPT must be enabled for a current issue.");
        using var failingForm = new ObservabilityMainForm(
            store,
            clipboardWriter: _ => throw new ExternalException("clipboard unavailable"));
        failingForm.Show();
        Application.DoEvents();
        SelectNavigation(failingForm, "issues", "Issues");
        ListView failingIssueList = GetPrivateField<ListView>(failingForm, "issueList");
        failingIssueList.Items[0].Selected = true;
        Application.DoEvents();
        AgentObservabilityUi formUi = GetPrivateField<AgentObservabilityUi>(
            failingForm,
            "observabilityUi");
        IReadOnlyList<string> selectionBeforeClipboardFailure = formUi.Snapshot.SelectedIssueIds;
        Button failingChatButton = Descendants(failingForm)
            .OfType<Button>()
            .Single(button => button.Text == "Copy to ChatGPT");
        failingChatButton.PerformClick();
        Label streamStatus = GetPrivateField<Label>(failingForm, "streamStatus");
        Assert(
            selectionBeforeClipboardFailure.SequenceEqual(formUi.Snapshot.SelectedIssueIds),
            "clipboard failure must not change issue selection");
        Assert(
            streamStatus.Text.Contains("could not be copied", StringComparison.Ordinal),
            "clipboard failure must be reported without throwing");
    }

    public static void IssuesProjectionIsIndexedCachedAndLazy()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-indexed-issues",
            store,
            new NoopAgentObservabilityTelemetry());
        var sessions = new List<AgentObservabilitySession>();
        try
        {
            for (int index = 0; index < 150; index++)
            {
                AgentObservabilitySession session = run.CreateAgent(
                    "mod.issue-" + index,
                    "Issue " + index);
                session.Start();
                RecordReadinessFailure(session, "indexed-" + index);
                sessions.Add(session);
            }

            Assert(store.GetIssues().Count >= 100, "fixture should contain a large issue history");
            using var ui = new AgentObservabilityUi(
                store,
                new AgentObservabilityUiOptions
                {
                    MaximumIssueRows = 25,
                    MaximumIndexedIssues = 500
                });
            AgentObservabilityUiSnapshot first = ui.ShowIssues();
            long signatureComputations = ui.IssueSignatureComputations;
            AssertEqual(0L, store.DiagnosticBundleCreationCount);
            AgentObservabilityUiSnapshot second = ui.Snapshot;
            Assert(ReferenceEquals(first.Issues, second.Issues),
                "unchanged Issues projections should be reused");
            AssertEqual(signatureComputations, ui.IssueSignatureComputations);
            Assert(first.Issues!.Issues.Count <= 25);
            Assert(first.Issues.HasMoreIssues);

            AgentObservabilityUiSnapshot expanded = ui.LoadMoreIssues();
            Assert(expanded.Issues!.Issues.Count <= 50);
            Assert(expanded.Issues.Issues.Count > first.Issues.Issues.Count);
        }
        finally
        {
            foreach (AgentObservabilitySession session in sessions)
            {
                session.Dispose();
            }
        }
    }

    public static void ProductionOverviewGroupsSessionsAndShowsCurrentState()
    {
        using var store = new AgentObservabilityStore();
        using var historicalRun = new AgentObservabilityRun(
            "ui-production-history",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession historical = historicalRun.CreateAgent(
            "mod.production",
            "Production Mod",
            logicalAgentId: "logical-production");
        historical.Start();
        historical.Complete();

        using var activeRun = new AgentObservabilityRun(
            "ui-production-active",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession active = activeRun.CreateAgent(
            "mod.production",
            "Production Mod",
            logicalAgentId: "logical-production");
        active.Start("running Quicktest");
        active.SetProductionState(
            DevelopmentStage.Testing,
            "quicktest",
            "none");
        active.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ProductionStateChanged,
            "Quicktest is ready.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityProductionEntry entry = ui.Snapshot.All!.Production.Single();
        AssertEqual(active.RunId, entry.RunId);
        AssertEqual(DevelopmentStage.Testing, entry.CurrentStage);
        AssertEqual("quicktest", entry.CurrentOperation);
        AssertEqual("Quicktest is ready.", entry.LatestEvent);
        Assert(!entry.IsHistorical);
        AssertEqual(2, ui.Snapshot.All!.Agents.Count);
    }

    public static void RecommendationsHaveSeparateNonBlockingSurface()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "ui-recommendations",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.recommendation",
            "Recommendation Mod");
        agent.Start();
        agent.RecordToolingRecommendation(
            "quicktest",
            "Quicktest readiness is unavailable.",
            "Expose a bounded readiness probe.",
            "DevBridge2",
            "evidence://quicktest",
            affectedCurrentTask: false,
            priority: "normal");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.ShowRecommendations();
        AssertEqual(AgentObservabilityUiView.Recommendations, snapshot.View);
        Assert(snapshot.Recommendations is not null);
        AgentObservabilityRecommendationRow recommendation =
            snapshot.Recommendations!.Recommendations.Single();
        AssertEqual("DevBridge2", recommendation.Owner);
        AssertEqual("new", recommendation.Status);
        Assert(!recommendation.ProductionAffected);
        AgentObservabilityUiSnapshot filtered = ui.SetFilter(
            new AgentObservabilityUiFilter(Query: "quicktest"));
        AssertEqual(1, filtered.Recommendations!.Recommendations.Count);
        AgentSnapshot persisted = store.GetAgents().Single();
        AssertEqual(AgentStatus.Running, persisted.Status);
        Assert(!persisted.FailureState);
    }

    public static void IssueCategoriesAreOperatorReadable()
    {
        static AgentIssue Issue(AgentIssueCategory category, bool blocking = false, bool recovered = false) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = "run",
                AgentId = "agent",
                ModId = "mod",
                Summary = "summary",
                Category = category,
                Severity = blocking ? AgentIssueSeverity.Error : AgentIssueSeverity.Warning,
                Blocking = blocking,
                Recovered = recovered
            };

        AssertEqual(
            "Mod defect",
            new AgentObservabilityIssueRow(Issue(AgentIssueCategory.ModDefect), "Mod", null, false).CategoryLabel);
        AssertEqual(
            "Required-validation blocker",
            new AgentObservabilityIssueRow(Issue(AgentIssueCategory.CapabilityGap, blocking: true), "Mod", null, false).CategoryLabel);
        AssertEqual(
            "Tooling/infrastructure incident",
            new AgentObservabilityIssueRow(Issue(AgentIssueCategory.ToolingFailure), "Mod", null, false).CategoryLabel);
        AssertEqual(
            "Recovered infrastructure incident",
            new AgentObservabilityIssueRow(Issue(AgentIssueCategory.ToolingFailure, recovered: true), "Mod", null, false).CategoryLabel);
        AssertEqual(
            "Optional validation unavailable",
            new AgentObservabilityIssueRow(Issue(AgentIssueCategory.OptionalValidationUnavailable), "Mod", null, false).CategoryLabel);
    }

    public static void ToolingEntitiesAggregateAcrossRunsAndPersist()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 100; index++)
        {
            using var run = new AgentObservabilityRun(
                "tool-run-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession session = run.CreateAgent(
                "RimLiaison",
                "RimLiaison",
                agentId: "process-" + index,
                logicalAgentId: "worker-" + index,
                sessionId: "session-" + index,
                entityIdentity: ObservabilityEntityIdentity.ForTool(
                    "rimliaison",
                    "RimLiaison"));
            session.Start();
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.InformationalProductionEvent,
                "Activity from process " + index);
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] tooling = ui.Snapshot.Navigation.Items
            .Where(item => item.EntityType == ObservabilityEntityTypes.Tool)
            .ToArray();
        AssertEqual(1, tooling.Length);
        AssertEqual("tool:rimliaison", tooling[0].CanonicalEntityId);
        AssertEqual(100, ui.Snapshot.All!.Agents.Count);

        AgentEvent firstActivity = store.GetEvents().First();
        AgentObservabilityAgentView detail = ui.ShowAgent("RimLiaison").Agent!;
        AssertEqual(99, detail.PastSessions.Count);
        Assert(detail.RecentActivity.Any(row => row.Event.Id == firstActivity.Id) == false,
            "bounded recent activity may omit old events");
        AgentObservabilityAgentView selected = ui.SelectEvent(firstActivity.Id).Agent!;
        AssertEqual(firstActivity.Id, selected.SelectedEventId);
        Assert(selected.SelectedEvent is not null, "aggregated activity must remain drillable");
    }

    public static void EntityRoutingKeepsModsAndToolsDistinct()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "entity-routing",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession rimliaison = run.CreateAgent(
            "RimLiaison",
            "RimLiaison",
            agentId: "tool-agent",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"));
        using AgentObservabilitySession rimcontext = run.CreateAgent(
            "RimContext",
            "RimContext",
            agentId: "context-agent",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimcontext", "RimContext"));
        using AgentObservabilitySession firstMod = run.CreateAgent(
            "mod.alpha",
            "Alpha",
            agentId: "alpha-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod("mod.alpha", "Alpha"));
        using AgentObservabilitySession secondMod = run.CreateAgent(
            "mod.beta",
            "Beta",
            agentId: "beta-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod("mod.beta", "Beta"));
        using AgentObservabilitySession unknown = run.CreateAgent(
            "opaque-source",
            "Opaque Source",
            agentId: "unknown-agent",
            entityIdentity: ObservabilityEntityIdentity.ForUnknown(
                "opaque-source",
                "Opaque Source"));
        rimliaison.Start();
        rimcontext.Start();
        firstMod.Start();
        secondMod.Start();
        unknown.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] entities = ui
            .Snapshot.Navigation.Items
            .Where(item => item.Kind == "agent")
            .ToArray();
        AssertEqual(2, entities.Count(item => item.EntityType == ObservabilityEntityTypes.Tool));
        AssertEqual(0, entities.Count(item => item.EntityType == ObservabilityEntityTypes.Unknown));
        AssertEqual(2, entities.Count(item => item.EntityType == ObservabilityEntityTypes.Mod));
        Assert(entities.Any(item => item.CanonicalEntityId == "mod:mod.alpha"));
        Assert(entities.Any(item => item.CanonicalEntityId == "mod:mod.beta"));
    }

    public static void PersistedToolingIdentityDoesNotDuplicateOnReload()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-entity-reload-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new AgentObservabilityStore(directory))
            {
                for (int index = 0; index < 3; index++)
                {
                    using var run = new AgentObservabilityRun(
                        "persisted-run-" + index,
                        writer,
                        new NoopAgentObservabilityTelemetry());
                    using AgentObservabilitySession session = run.CreateAgent(
                        "RimLiaison",
                        "RimLiaison",
                        agentId: "persisted-process-" + index,
                        sessionId: "persisted-session-" + index,
                        entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"));
                    session.Start();
                    session.Record(
                        DevelopmentStage.Testing,
                        AgentEventTypes.InformationalProductionEvent,
                        "Persisted tooling activity " + index);
                }
            }

            using var reader = new AgentObservabilityStore(directory);
            using var ui = new AgentObservabilityUi(reader);
            AgentObservabilityUiNavigationItem[] tooling = ui.Snapshot.Navigation.Items
                .Where(item => item.EntityType == ObservabilityEntityTypes.Tool)
                .ToArray();
            AssertEqual(1, tooling.Length);
            AgentObservabilityAgentView detail = ui.ShowAgent("RimLiaison").Agent!;
            AssertEqual(2, detail.PastSessions.Count);
            Assert(detail.RecentActivity.Count > 0);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static void ConcurrentToolingActivitiesRemainOneEntity()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "concurrent-tooling",
            store,
            new NoopAgentObservabilityTelemetry());

        Parallel.For(0, 2_000, index =>
        {
            using AgentObservabilitySession session = run.CreateAgent(
                "RimLiaison",
                "RimLiaison",
                agentId: "concurrent-process-" + index,
                logicalAgentId: "concurrent-worker-" + index,
                sessionId: "concurrent-session-" + index,
                entityIdentity: ObservabilityEntityIdentity.ForTool(
                    "rimliaison",
                    "RimLiaison"));
            session.Start();
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.InformationalProductionEvent,
                "Concurrent RimLiaison activity " + index);
        });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] tooling = ui.Snapshot.Navigation.Items
            .Where(item => item.EntityType == ObservabilityEntityTypes.Tool)
            .ToArray();
        AssertEqual(1, tooling.Length);
        AssertEqual("tool:rimliaison", tooling[0].CanonicalEntityId);
        AssertEqual(2_000, store.GetAgents(limit: 3_000).Count);
        Assert(store.GetEvents(limit: 10_000).Count >= 2_000);
        Assert(ui.ShowAgent("RimLiaison").Agent!.RecentActivity.Count > 0);
    }

    public static void WindowsPathIdentityNormalizationIsStable()
    {
        ObservabilityEntityIdentity backslash = ObservabilityEntityIdentity.ForMod(
            @"C:\RimDev\Repos\ExampleMod\",
            "Example Mod");
        ObservabilityEntityIdentity slash = ObservabilityEntityIdentity.ForMod(
            "c:/rimdev/repos/examplemod",
            "Example Mod");

        AssertEqual(backslash.CanonicalEntityId, slash.CanonicalEntityId);
        AssertEqual("mod:c:/rimdev/repos/examplemod", backslash.CanonicalEntityId);

        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "path-variant-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent(
            @"C:\RimDev\Repos\ExampleMod\",
            "Example Mod");
        using AgentObservabilitySession second = run.CreateAgent(
            "c:/rimdev/repos/examplemod",
            "Example Mod");
        Assert(ReferenceEquals(first, second), "path variants must reuse the canonical session");
    }

    public static void KnownToolFallbackUsesToolIdentity()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "tool-fallback",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent(
            "RimTest",
            "RimTest",
            agentId: "fallback-agent");
        session.Start();

        AssertEqual(ObservabilityEntityTypes.Tool, session.EntityType);
        AssertEqual("tool:rimtest", session.CanonicalEntityId);
        using var ui = new AgentObservabilityUi(store);
        AssertEqual(
            1,
            ui.Snapshot.Navigation.Items.Count(item =>
                item.EntityType == ObservabilityEntityTypes.Tool));
    }

    public static void MultipleToolIdentitiesAggregateAcrossRuns()
    {
        string[] toolIds =
        [
            "rimliaison",
            "rimtest",
            "rimcontext",
            "rimcontent",
            "rimerror",
            "rimbench",
            "devbridge2"
        ];
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 140; index++)
        {
            string toolId = toolIds[index % toolIds.Length];
            using var run = new AgentObservabilityRun(
                "multi-tool-run-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession session = run.CreateAgent(
                toolId,
                toolId,
                agentId: "multi-tool-agent-" + index,
                entityIdentity: ObservabilityEntityIdentity.ForTool(toolId, toolId));
            session.Start();
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.InformationalProductionEvent,
                "Tool activity " + index);
        }

        using var ui = new AgentObservabilityUi(store);
        var counts = ui.Snapshot.Navigation.Items
            .Where(item => item.EntityType == ObservabilityEntityTypes.Tool)
            .GroupBy(
                item => item.CanonicalEntityId ?? string.Empty,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        AssertEqual(toolIds.Length, counts.Count);
        Assert(counts.Values.All(count => count == 1));
    }

    public static void ProductionAndActivitySortByRealTimestamp()
    {
        using var store = new AgentObservabilityStore();
        AgentSnapshot missing = TestAgent("timestamp-missing", "agent-missing", "Missing", 0);
        AgentSnapshot older = TestAgent("timestamp-old", "agent-old", "Older", 1_704_067_200_000);
        AgentSnapshot newer = TestAgent("timestamp-new", "agent-new", "Newer", 1_704_153_600_000);
        store.RegisterAgent(missing);
        store.RegisterAgent(older);
        store.RegisterAgent(newer);
        store.AppendEvent(new AgentEventRequest(
            missing.RunId,
            missing.AgentId,
            missing.ModId,
            DevelopmentStage.Analysis,
            AgentEventTypes.InformationalProductionEvent,
            "Missing activity.",
            Timestamp: 0));
        store.AppendEvent(new AgentEventRequest(
            older.RunId,
            older.AgentId,
            older.ModId,
            DevelopmentStage.Analysis,
            AgentEventTypes.InformationalProductionEvent,
            "Older activity.",
            Timestamp: 1_704_067_201_000));
        store.AppendEvent(new AgentEventRequest(
            newer.RunId,
            newer.AgentId,
            newer.ModId,
            DevelopmentStage.Analysis,
            AgentEventTypes.InformationalProductionEvent,
            "Newer activity.",
            Timestamp: 1_704_153_601_000));

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAllView all = ui.Snapshot.All!;
        AssertEqual("Newer", all.Production[0].ModName);
        AssertEqual(1_704_153_601_000, all.Production[0].LatestTimestamp);
        Assert(all.Activity.Select(row => row.Event.Timestamp).SequenceEqual(
            all.Activity.Select(row => row.Event.Timestamp).OrderByDescending(value => value)),
            "activity must sort by the underlying timestamp");
        AssertEqual("Missing", all.Production[^1].ModName);
        Assert(!all.Production[^1].LatestTimestamp.HasValue);
        AssertEqual("—", AgentObservabilityTime.FormatLocal(0));
        Assert(AgentObservabilityTime.SortValue(null) < AgentObservabilityTime.SortValue(1),
            "missing timestamps must sort below current activity");
    }

    public static void RecommendationDuplicatesNestByStableOperation()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 3; index++)
        {
            using var run = new AgentObservabilityRun(
                "recommendation-run-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession session = run.CreateAgent(
                "mod.recommendation",
                "Recommendation",
                logicalAgentId: "recommendation-agent-" + index);
            session.Start();
            session.RecordToolingRecommendation(
                index == 2 ? " Validation:cleanup " : "validation:cleanup",
                "Improve cleanup validation.",
                "Run cleanup validation before packaging.",
                "RimLiaison",
                null,
                affectedCurrentTask: false);
        }

        using var distinctRun = new AgentObservabilityRun(
            "recommendation-distinct",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession distinct = distinctRun.CreateAgent(
            "mod.recommendation",
            "Recommendation",
            logicalAgentId: "recommendation-distinct-agent");
        distinct.Start();
        distinct.RecordToolingRecommendation(
            "validation:cleanup-other",
            "Improve cleanup validation.",
            "Run cleanup validation before testing.",
            "RimLiaison",
            null,
            affectedCurrentTask: false);

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityRecommendationsView view = ui.ShowRecommendations().Recommendations!;
        AssertEqual(2, view.Recommendations.Count);
        AgentObservabilityRecommendationRow grouped = view.Recommendations
            .Single(row => row.OccurrenceCount == 3);
        AssertEqual(
            grouped.Occurrences.Max(value => value.Issue.Timestamp),
            grouped.Issue.Timestamp);
        AssertEqual(3, grouped.Occurrences.Count);
        Assert(grouped.Occurrences.Select(value => value.Issue.Timestamp).SequenceEqual(
            grouped.Occurrences.Select(value => value.Issue.Timestamp).OrderByDescending(value => value)),
            "nested recommendations must be newest first");
    }

    public static void IssueDuplicatesNestWithoutMergingDistinctFailures()
    {
        using var store = new AgentObservabilityStore();
        for (int index = 0; index < 2; index++)
        {
            using var run = new AgentObservabilityRun(
                "issue-run-" + index,
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession session = run.CreateAgent(
                "mod.issue",
                "Issue",
                logicalAgentId: "issue-agent-" + index);
            session.Start();
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "The command failed.",
                new { operationKey = "command:shared", errorCode = "SHARED_FAILURE", exitCode = 1 });
        }

        using var rootCauseRun = new AgentObservabilityRun(
            "issue-root-cause",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession rootCause = rootCauseRun.CreateAgent(
            "mod.issue",
            "Issue",
            logicalAgentId: "issue-root-cause-agent");
        rootCause.Start();
        rootCause.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "The command failed.",
            new { operationKey = "command:shared", errorCode = "DIFFERENT_FAILURE", exitCode = 1 });

        using var distinctRun = new AgentObservabilityRun(
            "issue-distinct",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession distinct = distinctRun.CreateAgent(
            "mod.issue",
            "Issue",
            logicalAgentId: "issue-distinct-agent");
        distinct.Start();
        distinct.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "The command failed.",
            new { operationKey = "command:different-root-cause", errorCode = "SHARED_FAILURE", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssueRow[] rows = ui.ShowIssues().Issues!.Issues.ToArray();
        AgentObservabilityIssueRow[] sharedRows = rows
            .Where(row => row.Issue.OperationKey == "command:shared")
            .ToArray();
        AssertEqual(2, sharedRows.Length);
        AssertEqual(2, sharedRows.Max(row => row.Occurrences.Count));
        AssertEqual(1, sharedRows.Min(row => row.Occurrences.Count));
        AgentObservabilityIssueRow grouped = sharedRows.Single(row => row.Occurrences.Count == 2);
        AssertEqual(
            grouped.Occurrences.Max(value => value.Issue.Timestamp),
            grouped.Issue.Timestamp);
        Assert(rows.Any(row => row.Issue.OperationKey == "command:different-root-cause"),
            "a distinct operation must remain a distinct issue");
        Assert(rows.All(row => row.Issue.Timestamp > 0), "issue timestamps must be real data");
    }
    public static void OneActiveAgentAndHistoricalSessionsExposeOneWorkingAgent()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var store = new AgentObservabilityStore(
            options: new AgentObservabilityOptions
            {
                WorkingStalenessThreshold = TimeSpan.FromSeconds(1)
            },
            nowMilliseconds: () => now);
        using var run = new AgentObservabilityRun(
            "lifecycle-active",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession active = run.CreateAgent(
            "mod.fixture",
            "Fixture",
            logicalAgentId: "logical-fixture");
        active.Start();

        for (int index = 0; index < 20; index++)
        {
            store.RegisterAgent(new AgentSnapshot
            {
                RunId = "lifecycle-history-" + index,
                AgentId = "history-agent-" + index,
                SessionId = "history-session-" + index,
                LogicalAgentId = "logical-fixture",
                ModId = "mod.fixture",
                ModName = "Fixture",
                Status = AgentStatus.Running,
                StartTime = now - 10_000
            });
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.Snapshot;
        AgentObservabilityAllView all = snapshot.All!;
        AssertEqual(1, all.Agents.Count(agent => agent.Status == AgentStatus.Running));
        AssertEqual(1, all.Production.Count(entry => entry.Status == AgentStatus.Running));
        AssertEqual(
            1,
            snapshot.Navigation.Items.Count(item =>
                item.NavigationStatus == AgentObservabilityAgentNavigationStatus.Working));
    }

    public static void LiveActivityInsertionInvalidatesAllProjection()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "live-activity",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.live-activity",
            "Live Activity");
        agent.Start();
        using var ui = new AgentObservabilityUi(store);
        int initialCount = ui.Snapshot.All!.Activity.Count;

        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Live activity one.");
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Live activity two.");

        AgentObservabilityAllView all = ui.Snapshot.All!;
        AssertEqual(initialCount + 2, all.Activity.Count);
        AssertEqual("Live activity two.", all.Activity[0].Activity);
        AssertEqual("Live activity one.", all.Activity[1].Activity);
    }

    public static void FrontierAgentTabSelectionResolvesAgentEntity()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "frontier-agent-selection",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "Frontier",
            "Frontier",
            agentId: "frontier-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod("frontier", "Frontier"));
        agent.Start();
        agent.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Frontier research activity.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.Kind == "agent" && item.Label == "Frontier");
        AgentObservabilityUiSnapshot selected = ui.ShowAgent(tab.AgentId!, tab.RunId);

        AssertEqual(AgentObservabilityUiView.Agent, selected.View);
        Assert(selected.Agent is not null, "Frontier tab must resolve an agent view");
        AssertEqual(agent.AgentId, selected.Agent!.Agent.AgentId);
        AssertEqual(ObservabilityEntityTypes.Mod, tab.EntityType);
    }

    public static void FrontierAgentTabDoesNotUseModDisplayTextAsKey()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "frontier-agent-key",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "Frontier",
            "Frontier",
            agentId: "frontier-agent-keyed");
        agent.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.FullLabel == "Frontier");
        AgentObservabilityUiSnapshot selected = ui.ShowAgent(tab.AgentId!, tab.RunId);

        AssertEqual("frontier-agent-keyed", selected.Selection.View.AgentId);
        AssertEqual("frontier-agent-keyed", selected.Agent!.Agent.AgentId);
        Assert(selected.Selection.View.AgentId != selected.Agent.Agent.ModId);
    }

    public static void ClickingFrontierAgentTabRendersAgentDetailData()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "frontier-agent-render",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("Frontier", "Frontier");
        agent.Start("Inspecting Frontier");
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Frontier tests started.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.FullLabel == "Frontier");
        AgentObservabilityAgentView detail = ui.ShowAgent(tab.AgentId!, tab.RunId).Agent!;

        AssertEqual(AgentStatus.Running, detail.Agent.Status);
        AssertEqual("Frontier", detail.Agent.DisplayName);
        Assert(detail.RecentActivity.Any(row => row.Activity == "Frontier tests started."));
        Assert(string.IsNullOrWhiteSpace(detail.EmptyState));
    }

    public static void GenericSecondAgentRendersThroughTheSameRoute()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "generic-agent-render",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent("Frontier", "Frontier");
        using AgentObservabilitySession second = run.CreateAgent("Harbor", "Harbor");
        first.Start();
        second.Start();
        second.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Harbor changed a file.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.FullLabel == "Harbor");
        AgentObservabilityAgentView detail = ui.ShowAgent(tab.AgentId!, tab.RunId).Agent!;

        AssertEqual(second.AgentId, detail.Agent.AgentId);
        Assert(detail.RecentActivity.Any(row => row.Activity == "Harbor changed a file."));
    }

    public static void SameDisplayNameDoesNotMergeAgentTabs()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "same-display-name",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = run.CreateAgent(
            "mod.frontier.one",
            "Frontier",
            agentId: "frontier-one");
        using AgentObservabilitySession second = run.CreateAgent(
            "mod.frontier.two",
            "Frontier",
            agentId: "frontier-two");
        first.Start();
        second.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] tabs = ui.Snapshot.Navigation.Items
            .Where(item => item.Kind == "agent" && item.FullLabel == "Frontier")
            .ToArray();

        AssertEqual(2, tabs.Length);
        Assert(tabs.Select(tab => tab.AgentId).Distinct(StringComparer.Ordinal).Count() == 2);
        Assert(ui.ShowAgent("frontier-one", run.RunId).Agent!.Agent.AgentId == "frontier-one");
        Assert(ui.ShowAgent("frontier-two", run.RunId).Agent!.Agent.AgentId == "frontier-two");
    }

    public static void CanonicalAgentRouteDoesNotDependOnDisplayText()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "canonical-agent-route",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "package-alias",
            "Changed display",
            agentId: "canonical-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.frontier",
                "Changed display"));
        agent.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView detail =
            ui.ShowAgent("mod:com.example.frontier", run.RunId).Agent!;

        AssertEqual("canonical-agent", detail.Agent.AgentId);
        AssertEqual("mod:com.example.frontier", detail.Agent.CanonicalEntityId);
    }

    public static void AgentWithActivityShowsRecentActivity()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "agent-activity-detail",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.activity", "Activity");
        agent.Start();
        agent.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Inspected activity source.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView detail = ui.ShowAgent(agent.AgentId, run.RunId).Agent!;
        Assert(detail.RecentActivity.Count > 0);
        Assert(detail.RecentActivity.Any(row => row.Event.AgentId == agent.AgentId));
        Assert(detail.Agent.LastActivityAt is not null);
    }

    public static void AgentWithoutDetailHistoryShowsExplicitEmptyState()
    {
        using var store = new AgentObservabilityStore();
        const string runId = "agent-empty-detail";
        const string agentId = "empty-agent";
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = runId,
            AgentId = agentId,
            SessionId = "empty-session",
            ModId = "mod.empty",
            ModName = "Empty Agent",
            DisplayName = "Empty Agent",
            EntityType = ObservabilityEntityTypes.Mod,
            CanonicalEntityId = "mod:empty",
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.ShowAgent(agentId, runId);

        Assert(snapshot.Agent is not null);
        AssertEqual(
            "No activity has been reported for this agent yet.",
            snapshot.Agent!.EmptyState);
        Assert(!string.IsNullOrWhiteSpace(snapshot.EmptyState));
    }

    public static void NewActivityAppearsForSelectedAgentWithoutRefresh()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "selected-agent-live-activity",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.live-agent", "Live Agent");
        agent.Start();
        using var ui = new AgentObservabilityUi(store);
        ui.ShowAgent(agent.AgentId, run.RunId);

        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "New live activity.");
        AgentObservabilityAgentView detail = ui.Snapshot.Agent!;

        Assert(detail.RecentActivity.Any(row => row.Activity == "New live activity."));
    }

    public static void SelectedAgentStatusUpdatesLive()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "selected-agent-live-status",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.live-status", "Live Status");
        agent.Start();
        using var ui = new AgentObservabilityUi(store);
        ui.ShowAgent(agent.AgentId, run.RunId);

        agent.Complete();
        AgentObservabilityAgentView detail = ui.Snapshot.Agent!;

        AssertEqual(AgentStatus.Completed, detail.Agent.Status);
        AssertEqual(AgentCompletionState.Succeeded, detail.Agent.CompletionState);
    }

    public static void MalformedAgentRouteShowsDiagnosticState()
    {
        using var store = new AgentObservabilityStore();
        using var ui = new AgentObservabilityUi(store);

        AgentObservabilityUiSnapshot snapshot = ui.ShowAgent("missing-agent", "missing-run");

        AssertEqual(AgentObservabilityUiView.Agent, snapshot.View);
        Assert(snapshot.Agent is null);
        Assert(!string.IsNullOrWhiteSpace(snapshot.EmptyState));
        Assert(snapshot.EmptyState!.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    public static void AgentSelectionSurvivesStoreReloadByCanonicalIdentity()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-agent-selection-reload-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string runId = "agent-selection-reload";
            const string agentId = "reload-agent";
            using (var writer = new AgentObservabilityStore(directory))
            {
                writer.RegisterAgent(new AgentSnapshot
                {
                    RunId = runId,
                    AgentId = agentId,
                    SessionId = "reload-session",
                    ModId = "frontier-alias",
                    ModName = "Frontier",
                    DisplayName = "Frontier",
                    EntityType = ObservabilityEntityTypes.Mod,
                    CanonicalEntityId = "mod:com.example.frontier",
                    StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }

            using var reader = new AgentObservabilityStore(directory);
            using var ui = new AgentObservabilityUi(reader);
            AgentObservabilityUiSnapshot snapshot =
                ui.ShowAgent("mod:com.example.frontier", runId);

            Assert(snapshot.Agent is not null);
            AssertEqual(agentId, snapshot.Selection.View.AgentId);
            AssertEqual("mod:com.example.frontier", snapshot.Agent!.Agent.CanonicalEntityId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static void DesktopFrontierClickShowsAgentDetailPanel()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "desktop-frontier-click",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("Frontier", "Frontier");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Frontier validation failed.",
            new { operationKey = "validation:frontier", exitCode = 1 });

        using var form = new ObservabilityMainForm(store);
        form.Show();
        Application.DoEvents();
        Button button = form.Controls
            .OfType<FlowLayoutPanel>()
            .Single()
            .Controls
            .OfType<Panel>()
            .SelectMany(panel => panel.Controls.OfType<Button>())
            .Single(value => value.Text == "! Frontier");
        button.PerformClick();

        Control agentPanel = (Control)typeof(ObservabilityMainForm)
            .GetField("agentPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;
        TabControl tabs = Descendants(agentPanel).OfType<TabControl>().Single();
        ListView activity = Descendants(agentPanel)
            .OfType<ListView>()
            .Single(value => value.Columns.Count == 4 &&
                value.Columns[2].Text == "Activity");
        TextBox details = tabs.TabPages[0].Controls.OfType<TextBox>().Single();

        Assert(agentPanel.Parent is not null, "agent detail panel must be attached");
        Assert(agentPanel.Visible, "agent detail panel must be visible");
        Assert(activity.Items.Count > 0, "Frontier detail must render activity rows");
        Assert(!string.IsNullOrWhiteSpace(details.Text), "Frontier detail must render content");
    }

    public static void DesktopContentHostMountsEveryPrimaryView()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "desktop-content-host",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession frontier = run.CreateAgent("Frontier", "Frontier");
        using AgentObservabilitySession tool = run.CreateAgent(
            "rimliaison-tool",
            "RimLiaison",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"));
        frontier.Start();
        frontier.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Frontier validation failed.",
            new { operationKey = "validation:frontier", exitCode = 1 });
        frontier.RecordToolingRecommendation(
            "quicktest",
            "Quicktest readiness is unavailable.",
            "Expose a bounded readiness probe.",
            "DevBridge2",
            "evidence://quicktest",
            affectedCurrentTask: false,
            priority: "normal");
        frontier.Record(
            DevelopmentStage.Implementation,
            ContentObservabilityEventTypes.BlueprintCreated,
            "Content lifecycle created.",
            new ContentObservabilityEventData(
                ContentObservabilitySchemas.EventData,
                "created",
                ProjectId: "frontier",
                BlueprintId: "blueprint-frontier",
                ContentKind: "ThingDef",
                GameplayRole: "early-game ranged weapon",
                Reason: "visual-tree regression fixture"));
        tool.Start();
        tool.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "RimLiaison tooling validation started.");

        using var form = new ObservabilityMainForm(store);
        Panel contentPanel = GetPrivateField<Panel>(form, "contentPanel");
        Panel allPanel = GetPrivateField<Panel>(form, "allPanel");
        Panel issuesPanel = GetPrivateField<Panel>(form, "issuesPanel");
        Panel contentIntelligencePanel =
            GetPrivateField<Panel>(form, "contentIntelligencePanel");
        Panel agentPanel = GetPrivateField<Panel>(form, "agentPanel");
        ListView allActivity = GetPrivateField<ListView>(form, "allActivity");
        ListView issueList = GetPrivateField<ListView>(form, "issueList");
        ListView contentList = GetPrivateField<ListView>(form, "contentList");
        ListView agentActivity = GetPrivateField<ListView>(form, "agentActivity");

        Assert(ReferenceEquals(contentPanel.Parent, form),
            "content host must be attached to the form");
        Assert(form.Controls.Contains(contentPanel),
            "form controls must contain the content host");
        AssertEqual(DockStyle.Fill, contentPanel.Dock);
        Assert(Descendants(contentPanel).Contains(allPanel),
            "All panel must descend from the mounted content host");
        Assert(Descendants(contentPanel).Contains(issuesPanel),
            "Issues panel must descend from the mounted content host");
        Assert(Descendants(contentPanel).Contains(contentIntelligencePanel),
            "Content Intelligence panel must descend from the mounted content host");
        Assert(Descendants(contentPanel).Contains(agentPanel),
            "agent panel must descend from the mounted content host");

        form.Show();
        Application.DoEvents();

        SelectNavigation(form, "all", "All");
        Assert(allPanel.Visible);
        Assert(allActivity.Items.Count > 0, "All must render activity rows");
        Assert(HasDisplayRectangle(allActivity), "All activity must have a display rectangle");

        SelectNavigation(form, "issues", "Issues");
        Assert(issuesPanel.Visible);
        Assert(issueList.Items.Count > 0, "Issues must render issue rows");
        Assert(HasDisplayRectangle(issueList), "Issues must have a display rectangle");

        SelectNavigation(form, "recommendations", "Recommendations");
        Assert(issuesPanel.Visible, "Recommendations must use the Issues host");
        Assert(issueList.Items.Count > 0, "Recommendations must render recommendation rows");
        Assert(HasDisplayRectangle(issueList),
            "Recommendations must have a display rectangle through the Issues host");

        SelectNavigation(form, "content", "Content Intelligence");
        Assert(contentIntelligencePanel.Visible);
        Assert(contentList.Items.Count > 0,
            "Content Intelligence must render lifecycle rows");
        Assert(HasDisplayRectangle(contentList),
            "Content Intelligence must have a display rectangle");

        SelectNavigation(form, "agent", "Frontier");
        Assert(agentPanel.Visible, "Frontier must expose the agent panel");
        Assert(agentActivity.Items.Count > 0, "Frontier must render activity rows");
        Assert(HasDisplayRectangle(agentActivity),
            "Frontier detail must have a display rectangle");

        SelectNavigation(form, "agent", "RimLiaison", ObservabilityEntityTypes.Tool);
        Assert(agentPanel.Visible, "RimLiaison must expose the agent panel");
        Assert(agentActivity.Items.Count > 0, "RimLiaison must render activity rows");
        Assert(HasDisplayRectangle(agentActivity),
            "RimLiaison detail must have a display rectangle");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
    private static T GetPrivateField<T>(ObservabilityMainForm form, string name)
    {
        return (T)(typeof(ObservabilityMainForm)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!);
    }

    private static void SelectNavigation(
        ObservabilityMainForm form,
        string kind,
        string fullLabel,
        string? entityType = null)
    {
        FlowLayoutPanel navigation = form.Controls
            .OfType<FlowLayoutPanel>()
            .Single();
        Button button = navigation.Controls
            .OfType<Panel>()
            .SelectMany(static panel => panel.Controls.OfType<Button>())
            .Single(value => value.Tag is AgentObservabilityUiNavigationItem item &&
                item.Kind == kind &&
                item.FullLabel == fullLabel &&
                (entityType is null || item.EntityType == entityType));
        button.PerformClick();
        Application.DoEvents();
        Assert(button.Tag is AgentObservabilityUiNavigationItem { Selected: true },
            $"{fullLabel} navigation item must be selected");
    }

    private static bool HasDisplayRectangle(Control control) =>
        control.DisplayRectangle.Width > 0 &&
        control.DisplayRectangle.Height > 0;


    public static void ToolDetailWithDataUsesCanonicalToolIdentity()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "tool-detail-data",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession tool = run.CreateAgent(
            "tool-alias",
            "RimLiaison",
            agentId: "tool-detail-agent",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"));
        tool.Start();
        tool.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestStarted,
            "Tool validation started.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem tab = ui.Snapshot.Navigation.Items
            .Single(item => item.EntityType == ObservabilityEntityTypes.Tool);
        AgentObservabilityAgentView detail =
            ui.ShowAgent(tab.CanonicalEntityId!, tab.RunId).Agent!;

        AssertEqual(ObservabilityEntityTypes.Tool, detail.Agent.EntityType);
        AssertEqual("tool:rimliaison", detail.Agent.CanonicalEntityId);
        Assert(detail.RecentActivity.Any(row => row.Activity == "Tool validation started."));
    }

    public static void ToolDetailWithoutDataShowsExplicitEmptyState()
    {
        using var store = new AgentObservabilityStore();
        const string runId = "tool-detail-empty";
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = runId,
            AgentId = "tool-empty-agent",
            SessionId = "tool-empty-session",
            ModId = "RimContext",
            ModName = "RimContext",
            DisplayName = "RimContext",
            EntityType = ObservabilityEntityTypes.Tool,
            CanonicalEntityId = "tool:rimcontext",
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot =
            ui.ShowAgent("tool:rimcontext", runId);

        Assert(snapshot.Agent is not null);
        AssertEqual(
            "No activity has been reported for this agent yet.",
            snapshot.Agent!.EmptyState);
        Assert(!string.IsNullOrWhiteSpace(snapshot.EmptyState));
    }

    public static void DuplicateDisplayNamesAcrossEntityTypesStaySeparate()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "cross-namespace-display-name",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession mod = run.CreateAgent(
            "mod.rimliaison",
            "RimLiaison",
            agentId: "mod-name-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "mod.rimliaison",
                "RimLiaison"));
        using AgentObservabilitySession tool = run.CreateAgent(
            "tool-alias",
            "RimLiaison",
            agentId: "tool-name-agent",
            entityIdentity: ObservabilityEntityIdentity.ForTool(
                "rimliaison",
                "RimLiaison"));
        mod.Start();
        tool.Start();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] tabs = ui.Snapshot.Navigation.Items
            .Where(item => item.FullLabel == "RimLiaison")
            .ToArray();

        AssertEqual(2, tabs.Length);
        Assert(tabs.Any(item => item.EntityType == ObservabilityEntityTypes.Mod));
        Assert(tabs.Any(item => item.EntityType == ObservabilityEntityTypes.Tool));
        foreach (AgentObservabilityUiNavigationItem tab in tabs)
        {
            AgentObservabilityAgentView detail =
                ui.ShowAgent(tab.CanonicalEntityId!, tab.RunId).Agent!;
            AssertEqual(tab.EntityType, detail.Agent.EntityType);
            AssertEqual(tab.CanonicalEntityId, detail.Agent.CanonicalEntityId);
        }
    }

    public static void RepeatedEntitySwitchingDoesNotLeakDetailState()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "repeated-entity-switch",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        using AgentObservabilitySession tool = run.CreateAgent(
            "tool-alias",
            "RimLiaison",
            entityIdentity: ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison"));
        alpha.Start();
        beta.Start();
        tool.Start();
        alpha.Record(DevelopmentStage.Research, AgentEventTypes.FileInspected, "Alpha only.");
        beta.Record(DevelopmentStage.Testing, AgentEventTypes.TestStarted, "Beta only.");
        tool.Record(DevelopmentStage.Testing, AgentEventTypes.CommandStarted, "Tool only.");

        using var ui = new AgentObservabilityUi(store);
        for (int index = 0; index < 4; index++)
        {
            AgentObservabilityAgentView alphaView =
                ui.ShowAgent(alpha.AgentId, run.RunId).Agent!;
            Assert(alphaView.RecentActivity.All(row => row.Event.AgentId == alpha.AgentId));
            AgentObservabilityAgentView toolView =
                ui.ShowAgent(tool.CanonicalEntityId, run.RunId).Agent!;
            Assert(toolView.RecentActivity.All(row => row.Event.AgentId == tool.AgentId));
            AgentObservabilityAgentView betaView =
                ui.ShowAgent(beta.AgentId, run.RunId).Agent!;
            Assert(betaView.RecentActivity.All(row => row.Event.AgentId == beta.AgentId));
        }

        beta.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Beta live update.");
        AgentObservabilityAgentView finalBeta = ui.Snapshot.Agent!;
        Assert(finalBeta.RecentActivity.Any(row => row.Activity == "Beta live update."));
        Assert(finalBeta.RecentActivity.All(row => row.Event.AgentId == beta.AgentId));
    }

    public static void DesktopMalformedSelectionShowsDiagnosticInsteadOfBlank()
    {
        using var store = new AgentObservabilityStore();
        using var form = new ObservabilityMainForm(store);
        form.Show();
        Application.DoEvents();

        AgentObservabilityUi ui = (AgentObservabilityUi)typeof(ObservabilityMainForm)
            .GetField("observabilityUi", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;
        AgentObservabilityUiSnapshot snapshot = ui.ShowAgent("missing-agent", "missing-run");
        typeof(ObservabilityMainForm)
            .GetMethod("RefreshFromSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, [snapshot]);

        Control agentPanel = (Control)typeof(ObservabilityMainForm)
            .GetField("agentPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;
        TabControl tabs = Descendants(agentPanel).OfType<TabControl>().Single();
        TextBox details = tabs.TabPages[0].Controls.OfType<TextBox>().Single();

        Assert(agentPanel.Visible);
        Assert(details.Text.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    public static void CanonicalModTabLoadsAliasActivityAndRealEmptyState()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "canonical-mod-tab",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession activity = run.CreateAgent(
            "package-alias",
            "Example Mod",
            agentId: "example-agent",
            entityIdentity: ObservabilityEntityIdentity.ForMod(
                "com.example.mod",
                "Example Mod"));
        activity.Start();
        activity.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Activity stored under a raw package alias.");

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView detail =
            ui.ShowAgent("mod:com.example.mod", run.RunId).Agent!;
        AssertEqual("mod:com.example.mod", detail.Agent.CanonicalEntityId);
        Assert(detail.RecentActivity.Any(row =>
            row.Activity == "Activity stored under a raw package alias."));

        AgentSnapshot empty = new()
        {
            RunId = "canonical-empty",
            AgentId = "canonical-empty-agent",
            SessionId = "canonical-empty-session",
            ModId = "empty-alias",
            ModName = "Empty Mod",
            EntityType = ObservabilityEntityTypes.Mod,
            CanonicalEntityId = "mod:com.example.empty",
            DisplayName = "Empty Mod",
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        store.RegisterAgent(empty);
        AgentObservabilityUiSnapshot emptySnapshot =
            ui.ShowAgent("mod:com.example.empty", empty.RunId);
        AssertEqual(
            "No activity has been reported for this agent yet.",
            emptySnapshot.Agent!.EmptyState);
    }

    public static void PersistedWorkingStateIsReconciledOnRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-lifecycle-restart-" + Guid.NewGuid().ToString("N"));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            using (var writer = new AgentObservabilityStore(directory))
            {
                writer.RegisterAgent(new AgentSnapshot
                {
                    RunId = "restart-stale",
                    AgentId = "restart-stale-agent",
                    SessionId = "restart-stale-session",
                    ModId = "mod.restart",
                    ModName = "Restart Mod",
                    Status = AgentStatus.Running,
                    StartTime = now - 60_000
                });
            }

            using var reader = new AgentObservabilityStore(
                directory,
                new AgentObservabilityOptions
                {
                    WorkingStalenessThreshold = TimeSpan.FromSeconds(1)
                },
                nowMilliseconds: () => now);
            AgentSnapshot reconciled = reader.GetAgents().Single();
            Assert(reconciled.Status is AgentStatus.Failed or AgentStatus.Completed);
            AssertEqual(AgentCompletionState.Cancelled, reconciled.CompletionState);
            AssertEqual("STALE", reconciled.CompletionResult);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static AgentSnapshot IdentityAgent(
        string runId,
        string agentId,
        ObservabilityEntityIdentity identity,
        string workloadKind = "production",
        string? qualificationProfile = null) =>
        new()
        {
            RunId = runId,
            AgentId = agentId,
            SessionId = "session-" + agentId,
            ModId = identity.DisplayName,
            ModName = identity.DisplayName,
            EntityType = identity.EntityType,
            CanonicalEntityId = identity.CanonicalEntityId,
            DisplayName = identity.DisplayName,
            WorkloadKind = workloadKind,
            QualificationProfile = qualificationProfile,
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };


    public static void UnknownAgentIdentityUsesOccurrenceFallback()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "unknown-agent-run",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.unknown", "Unknown");
        session.Start();
        for (int index = 0; index < 96; index++)
        {
            session.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "Repeated command failure.",
                new { operationKey = "command:repeated", errorCode = "REPEATED_FAILURE", exitCode = 1 });
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityIssueRow row = ui.ShowIssues().Issues!.Issues
            .Single(value => value.Issue.Category == AgentIssueCategory.Error);
        AssertEqual(0, row.SharedAgentCount);
        Assert(row.OccurrenceCount >= 96, "repeated events must remain occurrences, not agents");
    }

    public static void RecommendationDuplicateArrivesLive()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "recommendation-live-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.live-recommendation",
            "Live Recommendation",
            logicalAgentId: "logical-live-first");
        first.Start();
        first.RecordToolingRecommendation(
            "validation:live",
            "Improve live validation.",
            "Run the live validation.",
            "RimLiaison",
            null,
            affectedCurrentTask: false);

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(1, ui.ShowRecommendations().Recommendations!.Recommendations.Count);

        using var secondRun = new AgentObservabilityRun(
            "recommendation-live-second",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.live-recommendation",
            "Live Recommendation",
            logicalAgentId: "logical-live-second");
        second.Start();
        second.RecordToolingRecommendation(
            " VALIDATION:LIVE ",
            "Improve live validation.",
            "Run the live validation.",
            "RimLiaison",
            null,
            affectedCurrentTask: false);

        AgentObservabilityRecommendationRow row =
            ui.Snapshot.Recommendations!.Recommendations.Single();
        AssertEqual(2, row.Occurrences.Count);
    }

    public static void IssueDuplicateArrivesLive()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "issue-live-first",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.live-issue",
            "Live Issue",
            logicalAgentId: "logical-live-first");
        first.Start();
        first.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Live command failed.",
            new { operationKey = "command:live", errorCode = "LIVE_FAILURE", exitCode = 1 });

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(1, ui.ShowIssues().Issues!.Issues.Count);

        using var secondRun = new AgentObservabilityRun(
            "issue-live-second",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.live-issue",
            "Live Issue",
            logicalAgentId: "logical-live-second");
        second.Start();
        second.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Live command failed.",
            new { operationKey = "command:live", errorCode = "LIVE_FAILURE", exitCode = 1 });

        AgentObservabilityIssueRow row = ui.Snapshot.Issues!.Issues.Single();
        AssertEqual(2, row.Occurrences.Count);
    }

    private static void RecordSharedFailure(
        AgentObservabilitySession agent,
        string suffix)
    {
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "RimLiaison command failed.",
            new
            {
                operationKey = "command:shared:" + suffix,
                toolName = "RimLiaison",
                command = "rimliaison affected",
                errorCode = "RIMLIAISON_COMMAND_FAILED",
                exitCode = 1
            });
    }

    private static AgentSnapshot TestAgent(
        string runId,
        string agentId,
        string modName,
        long startTime,
        string? logicalAgentId = null) =>
        new()
        {
            RunId = runId,
            AgentId = agentId,
            LogicalAgentId = logicalAgentId,
            SessionId = "session-" + agentId,
            ModId = "mod." + modName.ToLowerInvariant(),
            ModName = modName,
            StartTime = startTime
        };

    private static AgentSnapshot LegacyAgent(string runId, string agentId) =>
        new()
        {
            RunId = runId,
            AgentId = agentId,
            ModId = "mod.legacy",
            ModName = "Legacy",
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

    private static void RecordReadinessFailure(
        AgentObservabilitySession agent,
        string operationKey)
    {
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Readiness identity mismatch occurred.",
            new
            {
                operationKey = "readiness:" + operationKey,
                toolName = "DevBridge2",
                command = "readiness check",
                errorCode = "READINESS_IDENTITY_MISMATCH",
                exitCode = 1
            });
    }

    public static void ExistingCliUiRemainsAvailable()
    {
        using var store = new AgentObservabilityStore();
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = CliApplication.RunAsync(
                ["list", "--catalog", Path.Combine(Directory.GetCurrentDirectory(), "TestCatalog", "rimtest.catalog.json")],
                output,
                error,
                observabilityStore: store,
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
