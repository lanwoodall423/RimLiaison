using System.Text.Json;
using RimContext.Core.Content;
using RimContext.Core.Impact;
using RimLiaison;
using RimLiaison.Observability;
using RimLiaison.Results;

namespace RimLiaison.Tests;

internal static class Prompt3AuditTests
{
    public static void MultiAgentLifecycleAndBundleAudit()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-audit",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent(
            "mod.alpha",
            "Alpha",
            "agent-alpha");
        using AgentObservabilitySession beta = run.CreateAgent(
            "mod.beta",
            "Beta",
            "agent-beta");

        Assert(ReferenceEquals(alpha, run.CreateAgent("mod.alpha", "Alpha")),
            "default same-mod creation must reuse the active logical session");
        alpha.Start("analysis");
        beta.Start("analysis");

        Task.WhenAll(
            Task.Run(async () =>
            {
                alpha.SetStage(DevelopmentStage.Research, "research");
                alpha.Record(
                    DevelopmentStage.Research,
                    AgentEventTypes.SearchCompleted,
                    "Alpha research completed.",
                    new { operationKey = "search:alpha" });
                await Task.Yield();
                alpha.SetStage(DevelopmentStage.Implementation, "implementing");
                alpha.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.ToolFailed,
                    "Alpha compiler failed.",
                    new
                    {
                        operationKey = "build:alpha",
                        toolName = "compiler",
                        command = "compiler Source/Alpha.cs",
                        filePath = "Source/Alpha.cs",
                        exitCode = 1,
                        errorCode = "COMPILER_FAILED"
                    });
                alpha.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.RecoveryCompleted,
                    "Alpha fallback succeeded.",
                    new { operationKey = "build:alpha", recovered = true });
                alpha.SetStage(DevelopmentStage.Testing, "testing");
                alpha.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestPassed,
                    "Alpha tests passed.",
                    new { operationKey = "test:alpha", exitCode = 0 });
                alpha.SetStage(DevelopmentStage.Packaging, "packaging");
                alpha.Record(
                    DevelopmentStage.Packaging,
                    AgentEventTypes.PackagingCompleted,
                    "Alpha package ready.",
                    new { operationKey = "package:alpha" });
                alpha.Complete("Alpha completed.");
            }),
            Task.Run(async () =>
            {
                beta.SetStage(DevelopmentStage.Research, "research");
                beta.Record(
                    DevelopmentStage.Research,
                    AgentEventTypes.SearchCompleted,
                    "Beta research completed.",
                    new { operationKey = "search:beta" });
                await Task.Yield();
                beta.SetStage(DevelopmentStage.Implementation, "implementing");
                beta.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildFailed,
                    "Beta build failed.",
                    new
                    {
                        operationKey = "build:beta",
                        filePath = "Source/Beta.cs",
                        exitCode = 1,
                        errorCode = "BUILD_FAILED"
                    });
                beta.SetStage(DevelopmentStage.Testing, "testing");
                beta.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestFailed,
                    "Beta tests remain blocked.",
                    new { operationKey = "test:beta", exitCode = 1 });
                beta.Fail("Beta could not complete.", "BUILD_FAILED");
            })).GetAwaiter().GetResult();

        AgentSnapshot[] agents = store.GetAgents(runId: run.RunId).ToArray();
        AssertEqual(2, agents.Length);
        AssertEqual(AgentCompletionState.Succeeded,
            agents.Single(agent => agent.ModId == "mod.alpha").CompletionState);
        AssertEqual(AgentCompletionState.Failed,
            agents.Single(agent => agent.ModId == "mod.beta").CompletionState);

        AgentEvent[] allEvents = store.GetEvents(runId: run.RunId).ToArray();
        Assert(allEvents.SequenceEqual(allEvents.OrderBy(eventRecord => eventRecord.Sequence)),
            "All events must have a deterministic interleaved order");
        Assert(allEvents.Where(eventRecord => eventRecord.ModId == "mod.alpha")
            .All(eventRecord => eventRecord.AgentId == "agent-alpha"));
        Assert(allEvents.Where(eventRecord => eventRecord.ModId == "mod.beta")
            .All(eventRecord => eventRecord.AgentId == "agent-beta"));

        AgentIssue alphaIssue = store.GetIssues(
                runId: run.RunId,
                agentId: alpha.AgentId)
            .Single(issue => issue.Category == AgentIssueCategory.Error);
        AgentIssue betaIssue = store.GetIssues(
                runId: run.RunId,
                agentId: beta.AgentId)
            .First(issue => issue.Category == AgentIssueCategory.Error);
        Assert(alphaIssue.Recovered, "Alpha's fallback issue must be recovered");
        Assert(!betaIssue.Recovered, "Beta's build issue must remain unresolved");

        using var ui = new AgentObservabilityUi(store);
        AssertEqual(AgentObservabilityUiView.All, ui.CurrentView);
        AgentObservabilityUiSnapshot all = ui.Snapshot;
        AssertEqual(2, all.All!.Agents.Count);
        Assert(all.All.Activity.Any(row => row.Event.AgentId == alpha.AgentId));
        Assert(all.All.Activity.Any(row => row.Event.AgentId == beta.AgentId));

        AgentObservabilityAgentView alphaView = ui.ShowAgent(alpha.AgentId).Agent!;
        Assert(alphaView.RecentActivity.All(row => row.Event.AgentId == alpha.AgentId));
        Assert(alphaView.Agent.Status == AgentStatus.Completed);

        AgentObservabilityIssuesView issues = ui.ShowIssues().Issues!;
        Assert(issues.Issues.Any(row => row.Issue.Id == alphaIssue.Id && row.Issue.Recovered));
        Assert(issues.Issues.Any(row => row.Issue.Id == betaIssue.Id && !row.Issue.Recovered));

        AgentObservabilityIssueDetail detail = ui.ShowIssue(alphaIssue.Id).Issue!;
        AssertEqual(0, detail.UnresolvedEventIds.Count);
        Assert(detail.SupportingEvents.All(eventRecord =>
            eventRecord.AgentId == alpha.AgentId && eventRecord.ModId == alpha.ModId));

        AgentDiagnosticBundle bundle = ui.PrepareAssessment([alphaIssue.Id, betaIssue.Id]);
        AssertEqual(2, bundle.Issues.Count);
        Assert(bundle.Mods.Select(mod => mod.ModId).OrderBy(value => value)
            .SequenceEqual(["mod.alpha", "mod.beta"]));
        Assert(bundle.SupportingEvents.All(eventRecord =>
            eventRecord.ModId is "mod.alpha" or "mod.beta"));
        Assert(bundle.SupportingEvents.All(eventRecord =>
            eventRecord.ModId == "mod.alpha"
                ? eventRecord.AgentId == alpha.AgentId
                : eventRecord.AgentId == beta.AgentId));
    }

    public static void RedactionAndIssueBoundsAudit()
    {
        using var store = new AgentObservabilityStore(options: new AgentObservabilityOptions
        {
            MaximumIssueEventReferences = 3,
            MaximumPersistedBytes = 32 * 1024
        });
        using var run = new AgentObservabilityRun(
            "run-prompt3-safety",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.safety", "Safety");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Command failed: password=hunter2 Authorization: Bearer abc-secret-value.",
            new
            {
                operationKey = "command:safety",
                command = "tool --token hunter2",
                stderr = "apiKey=api-secret-value access_token=access-secret-value",
                stdout = "Bearer stdout-secret-value",
                exitCode = 1
            });

        for (int index = 0; index < 8; index++)
        {
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "Repeated failure " + index,
                new
                {
                    operationKey = "command:safety",
                    command = "tool --token hunter" + index,
                    exitCode = 1
                });
        }

        AgentIssue issue = store.GetIssues(agentId: agent.AgentId)
            .First(value => value.Category == AgentIssueCategory.Error);
        AgentEvent failed = store.GetEvents(agentId: agent.AgentId)
            .First(value => value.Type == AgentEventTypes.CommandFailed);
        string eventJson = failed.Data?.GetRawText() ?? string.Empty;
        Assert(!eventJson.Contains("hunter2", StringComparison.Ordinal));
        Assert(!eventJson.Contains("api-secret-value", StringComparison.Ordinal));
        Assert(!eventJson.Contains("access-secret-value", StringComparison.Ordinal));
        Assert(!eventJson.Contains("stdout-secret-value", StringComparison.Ordinal));
        Assert(!failed.Summary.Contains("hunter2", StringComparison.Ordinal));
        Assert(issue.EventIds.Count <= 3, "issue support references must be bounded");

        AgentEvent? resolution = agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RecoveryCompleted,
            "Fallback completed.",
            new { operationKey = "command:safety", recovered = true });
        AgentIssue recovered = store.GetIssues(agentId: agent.AgentId)
            .First(value => value.Id == issue.Id);
        Assert(recovered.Recovered);
        Assert(resolution is not null && recovered.ResolutionEventId == resolution.Id);
        Assert(recovered.EventIds.Count <= 3);

        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([recovered.Id]);
        string bundleJson = JsonSerializer.Serialize(bundle, AgentObservabilityJson.Options);
        Assert(!bundleJson.Contains("hunter2", StringComparison.Ordinal));
        Assert(!bundleJson.Contains("api-secret-value", StringComparison.Ordinal));
        Assert(bundle.SupportingEvents.Any(eventRecord =>
            eventRecord.Id == recovered.ResolutionEventId));
    }

    public static void AbandonedAgentBecomesTerminal()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-abandoned",
            store,
            new NoopAgentObservabilityTelemetry());
        AgentObservabilitySession agent = run.CreateAgent("mod.abandoned", "Abandoned");
        agent.Start();
        agent.Dispose();

        AgentSnapshot snapshot = store.GetAgents(agentId: agent.AgentId).Single();
        AssertEqual(AgentStatus.Failed, snapshot.Status);
        AssertEqual(AgentCompletionState.Cancelled, snapshot.CompletionState);
        Assert(snapshot.FailureState);
        Assert(store.GetEvents(agentId: agent.AgentId)
            .Any(eventRecord => eventRecord.Type == AgentEventTypes.AgentFailed));
    }

    public static void ZeroExitSuccessResolvesFailure()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-success-resolution",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.success", "Success");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Test failed first.",
            new { operationKey = "test:success", exitCode = 1 });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
        AgentEvent? success = agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestPassed,
            "Test passed on retry.",
            new { operationKey = "test:success", exitCode = 0 });

        AgentIssue resolved = store.GetIssues(agentId: agent.AgentId)
            .Single(value => value.Id == issue.Id);
        Assert(resolved.Recovered, "zero-exit passed events must resolve the failure");
        Assert(success is not null && resolved.ResolutionEventId == success.Id);
    }

    public static void RepeatedWorkHistoryIsAgentScoped()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-repeated-work",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();
        alpha.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Alpha inspected shared file.",
            new { operationKey = "file:shared", filePath = "Source/Shared.cs" });
        alpha.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Alpha inspected shared file again.",
            new { operationKey = "file:shared", filePath = "Source/Shared.cs" });
        beta.Record(
            DevelopmentStage.Research,
            AgentEventTypes.FileInspected,
            "Beta inspected shared file.",
            new { operationKey = "file:shared", filePath = "Source/Shared.cs" });

        Assert(!store.GetIssues(agentId: beta.AgentId)
            .Any(issue => issue.Category == AgentIssueCategory.RedundantWork),
            "repeated-work evidence must not cross agent boundaries");
    }

    public static void UncertainIssueSignalsUseQualifiedWording()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-qualified-issues",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.qualified", "Qualified");
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.ToolLimitation,
            "The compiler cannot expose the required diagnostic.",
            new { operationKey = "tool:compiler" });
        agent.Record(
            DevelopmentStage.Research,
            AgentEventTypes.ContextIssue,
            "The source index was incomplete.",
            new { operationKey = "context:index" });
        agent.Record(
            DevelopmentStage.Implementation,
            "rework.detected",
            "The same edit was revisited.",
            new { operationKey = "file:shared" });

        AgentIssue[] issues = store.GetIssues(agentId: agent.AgentId).ToArray();
        Assert(issues.Any(issue => issue.Category == AgentIssueCategory.ToolLimitation &&
            issue.Summary.StartsWith("Possible tooling limitation:", StringComparison.Ordinal)));
        Assert(issues.Any(issue => issue.Category == AgentIssueCategory.ContextIssue &&
            issue.Summary.StartsWith("Possible context issue:", StringComparison.Ordinal)));
        Assert(issues.Any(issue => issue.Category == AgentIssueCategory.Rework &&
            issue.Summary.StartsWith("Potential rework:", StringComparison.Ordinal)));
    }

    public static void PersistedRecordsRemainBounded()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-prompt3-bounded-" + Guid.NewGuid().ToString("N"));
        var options = new AgentObservabilityOptions
        {
            MaximumPersistedBytes = 4 * 1024,
            MaximumEventDataBytes = 256,
            MaximumEvents = 100,
            MaximumIssues = 100,
            MaximumAgents = 10
        };

        try
        {
            using (var store = new AgentObservabilityStore(directory, options))
            using (var run = new AgentObservabilityRun(
                       "run-prompt3-persisted-bounds",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent("mod.bounded", "Bounded"))
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

            foreach (string fileName in new[] { "events.jsonl", "issues.jsonl", "agents.jsonl" })
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    Assert(new FileInfo(path).Length <= options.MaximumPersistedBytes,
                        fileName + " must stay within its configured persistence bound");
                }
            }
        }
        finally
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
    }
    public static void ContentIntelligenceLifecycleProjectionIsIncremental()
    {
        using var store = new AgentObservabilityStore();
        using var firstRun = new AgentObservabilityRun(
            "run-prompt3-content-1",
            store,
            new NoopAgentObservabilityTelemetry());
        using var secondRun = new AgentObservabilityRun(
            "run-prompt3-content-2",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession first = firstRun.CreateAgent(
            "mod.content",
            "Content",
            "agent-content-1",
            logicalAgentId: "persistent-content-agent",
            sessionId: "content-session-1");
        using AgentObservabilitySession second = secondRun.CreateAgent(
            "mod.content",
            "Content",
            "agent-content-2",
            logicalAgentId: "persistent-content-agent",
            sessionId: "content-session-2");
        first.Start();
        second.Start();

        RecordContent(
            first,
            ContentObservabilityEventTypes.BlueprintCreated,
            "created",
            "bp-1",
            "project.alpha",
            designParameters: new Dictionary<string, string> { ["count"] = "2" },
            vanillaComparables: ["BaseWeapon"],
            frameworkRequirements: ["Core"],
            frameworkDependencies: ["Core:1"],
            validationExpectations: ["static", "build"],
            implementationNovelty: "reused");
        RecordContent(first, ContentObservabilityEventTypes.PrecedentDetected, "observed", "bp-1", "project.alpha", "precedent-1");
        RecordContent(first, ContentObservabilityEventTypes.PrecedentQualified, "proven", "bp-1", "project.alpha", "precedent-1", qualified: true);
        RecordContent(first, ContentObservabilityEventTypes.ReuseSelected, "selected", "bp-1", "project.alpha", "precedent-1", reuseSource: ContentReuseSources.Precedent);
        RecordContent(
            first,
            ContentObservabilityEventTypes.BlueprintValidated,
            "validated",
            "bp-1",
            "project.alpha",
            "precedent-1",
            validationResult: "PASS",
            evidenceId: "evidence-1",
            referenceIds: ["evidence-1"],
            metrics: new ContentObservabilityMetrics(
                ElapsedMilliseconds: 100,
                ValidationAttempts: 1,
                Succeeded: true,
                Available: true));
        RecordContent(first, ContentObservabilityEventTypes.PromotionCompleted, "promoted", "bp-1", "project.alpha", "precedent-1", archetypeId: "shape-1", archetypeVersion: 1, replayPassed: true);
        RecordContent(first, ContentObservabilityEventTypes.ArchetypeUsed, "succeeded", "bp-1", "project.alpha", archetypeId: "shape-1", archetypeVersion: 1, reuseSource: ContentReuseSources.RimContent, metrics: new ContentObservabilityMetrics(Succeeded: true, Available: true));

        RecordContent(second, ContentObservabilityEventTypes.BlueprintCreated, "created", "bp-2", "project.beta");
        RecordContent(second, ContentObservabilityEventTypes.ReuseSelected, "selected", "bp-2", "project.beta", archetypeId: "shape-1", archetypeVersion: 1, reuseSource: ContentReuseSources.RimContent);
        RecordContent(
            second,
            ContentObservabilityEventTypes.BlueprintValidated,
            "validated",
            "bp-2",
            "project.beta",
            validationResult: "FAIL",
            metrics: new ContentObservabilityMetrics(
                ElapsedMilliseconds: 200,
                ValidationAttempts: 1,
                RepairCount: 1,
                Succeeded: false,
                Available: true));
        RecordContent(second, ContentObservabilityEventTypes.ArchetypeUsed, "failed", "bp-2", "project.beta", archetypeId: "shape-1", archetypeVersion: 1, reuseSource: ContentReuseSources.RimContent, metrics: new ContentObservabilityMetrics(Succeeded: false, Available: true));
        RecordContent(second, ContentObservabilityEventTypes.RegressionDetected, "regression", "bp-2", "project.beta", archetypeId: "shape-1", archetypeVersion: 1, reason: "ATTRIBUTABLE_REUSE_FAILURE");
        RecordContent(second, ContentObservabilityEventTypes.ArchetypeQuarantined, "quarantined", "bp-2", "project.beta", archetypeId: "shape-1", archetypeVersion: 1, reason: "ATTRIBUTABLE_REUSE_FAILURE");
        RecordContent(second, ContentObservabilityEventTypes.RollbackCompleted, "rolled-back", "bp-2", "project.beta", archetypeId: "shape-1", archetypeVersion: 1, previousArchetypeVersion: 2);
        first.Complete();

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiSnapshot snapshot = ui.ShowContent();
        ContentIntelligenceObservabilityView content = snapshot.Content!;
        AssertEqual(2, content.Blueprints.Count);
        AssertEqual(1, content.ProvenPrecedents.Count);
        ContentBlueprintRow blueprintRow = content.Blueprints.Single(row => row.BlueprintId == "bp-1");
        AssertEqual("2", blueprintRow.DesignParameters!["count"]);
        AssertEqual("BaseWeapon", blueprintRow.VanillaComparables![0]);
        AssertEqual("evidence-1", blueprintRow.EvidenceId);
        AssertEqual("evidence-1", blueprintRow.ReferenceIds.Single());
        AssertEqual("Core", blueprintRow.FrameworkRequirements![0]);
        AssertEqual("reused", blueprintRow.ImplementationNovelty);
        ContentPrecedentRow precedentRow = content.ProvenPrecedents.Single();
        AssertEqual("proven", precedentRow.State);
        AssertEqual(1, precedentRow.SuccessfulUses);
        AssertEqual(1d, content.Efficiency.PrecedentReuseSuccessRate);
        ContentArchetypeRow archetypeRow = content.Archetypes.Single();
        AssertEqual(1, archetypeRow.PriorStableVersion);
        AssertEqual("persistent-content-agent", content.Blueprints[0].LogicalAgentId);
        AssertEqual(1, content.Efficiency.ReuseDistribution.RimContent);
        AssertEqual(1, content.Efficiency.ReuseDistribution.ProvenPrecedent);
        AssertEqual("unavailable", content.Efficiency.TokenAvailability);
        AssertEqual("available", content.Efficiency.TimeAvailability);
        AssertEqual(1, content.Efficiency.RegressionCount);
        AssertEqual(1, content.Efficiency.CompletedFeatures);
        AssertEqual(100d, content.Efficiency.MedianElapsedMilliseconds);
        Assert(content.LiveActivity.Count <= 100);

        ContentIntelligenceObservabilityView sameContent = ui.ShowContent().Content!;
        Assert(ReferenceEquals(content, sameContent), "unchanged content projection must be cached");
        RecordContent(second, ContentObservabilityEventTypes.SourceIneligible, "ineligible", "bp-2", "project.beta", reason: "admin exclusion");
        second.Complete();
        ContentIntelligenceObservabilityView updatedContent = ui.Snapshot.Content!;
        Assert(!ReferenceEquals(content, updatedContent), "new content event must invalidate only the content projection");
        Assert(updatedContent.LiveActivity.Any(row => row.State == "ineligible"));
    }
    public static void ExecutionImpactLifecycleAndProjection()
    {
        var identity = new ImpactGraphIdentity(
            "workspace-prompt3",
            "workspace-generation-1",
            "index-generation-1",
            Repository: "repo-prompt3",
            SourceRevision: "source-1",
            Project: "project.prompt3",
            TaskIdentity: "task-prompt3");
        var packet = new ExecutionPacket(
            "rimexecution-packet/v1",
            ExecutionPacketStatuses.Valid,
            "implement prompt 3",
            "project.prompt3",
            "repo-prompt3",
            identity,
            [new ImpactPacketReference("src/Changed.cs", "node.changed", 1)],
            null,
            null,
            null,
            null,
            ["direct dependency changed"],
            ["tests.prompt3"],
            [new ImpactPacketReference("expand:runtime", "node.runtime", 2)],
            ["serialization coverage unavailable"],
            ["node.changed", "node.runtime"],
            new ExecutionPacketMetrics(7, 2048, 3, true, ExpensiveFreshLookupsAvoided: 2),
            new PacketBudget(4096, 16, 2048, false));
        var prediction = new PredictedImpact(
            ["src/Changed.cs"],
            ["node.changed"],
            ["direct"],
            ["runtime"],
            "indexed impact graph");
        var actual = new ActualImpact(
            ["src/Changed.cs", "src/Dependent.cs"],
            ["node.changed", "node.dependent"],
            ["node.dependent"],
            ["direct", "runtime"],
            ["runtime"],
            HarmonyOrDynamicRisk: true,
            SerializationRisk: false,
            ScopeExpanded: true,
            ["actual dependent changed"],
            prediction);
        var source = new ValidationSourceIdentity(
            identity.WorkspaceIdentity,
            identity.SourceRevision!,
            identity.IndexGeneration,
            identity.Project,
            identity.Repository);
        var requirement = new ValidationRequirement(
            "test.prompt3",
            ValidationRequirementKinds.TargetedTest,
            ValidationPlanTiers.NarrowTargeted,
            "changed direct dependency requires targeted validation",
            "REQUIRED",
            "tests.prompt3",
            "recipe.prompt3",
            ["direct"],
            ["evidence.prompt3"]);
        var plan = new ValidationPlan(
            ValidationPlanSchemas.Current,
            "valid",
            ValidationPlanTiers.NarrowTargeted,
            source,
            [requirement],
            [],
            [],
            actual.ChangedFiles,
            actual.ChangedNodeIds,
            actual.ImpactClasses,
            actual.ValidationConcerns,
            actual.ExpansionReasons,
            actual.ScopeExpanded,
            ValidationPlanTiers.NarrowTargeted,
            "plan-prompt3",
            new ValidationPlanMetrics(4, 2, 1, 1, 1, 0, 2));
        var catalog = new[]
        {
            new ValidationCatalogEntry(
                "tests.prompt3",
                "recipe.prompt3",
                [new ValidationCoverage("impactClass", "direct")]),
            new ValidationCatalogEntry(
                "tests.agent",
                "recipe.agent",
                [new ValidationCoverage("impactClass", "direct")])
        };
        var agentPlan = new MinimumSafeValidationPlanner().ApplyAgentRequest(
            plan,
            new AgentValidationRequest(AdditionalTestIds: ["tests.agent"]),
            catalog);
        Assert(agentPlan.Additional.Any(requirement => requirement.TestId == "tests.agent"));
        var reducedPlan = new MinimumSafeValidationPlanner().ApplyAgentRequest(
            agentPlan,
            new AgentValidationRequest(RemoveRequirementIds: ["test.prompt3"]),
            catalog);
        AssertEqual(agentPlan.Required.Count, reducedPlan.Required.Count);
        var relationship = new LearnedImpactRelationship(
            ValidationPlanSchemas.LearningCurrent,
            "node.changed",
            "node.dependent",
            "direct_dependents",
            "runtime",
            new ImpactProvenance("rimerror", "causal", "evidence.prompt3", "failure attribution"),
            "project",
            Project: "project.prompt3",
            SourceRevision: source.SourceRevision,
            SupportCount: 2,
            IndependentObservations: 2,
            Status: "proven",
            EvidenceIds: ["evidence.prompt3"]);
        var result = new ImpactLearningResult(true, false, relationship);
        var suiteResult = new RimTestSuiteResult
        {
            Status = "PASS",
            Suite = "affected",
            Passed = 1,
            Failed = 0,
            DurationMs = 13
        };

        using var store = new AgentObservabilityStore();
        using (var firstRun = new AgentObservabilityRun(
                   "run-prompt3-impact-1",
                   store,
                   new NoopAgentObservabilityTelemetry()))
        using (AgentObservabilitySession first = firstRun.CreateAgent(
                   "mod.prompt3",
                   "Prompt 3",
                   "agent-prompt3-1",
                   logicalAgentId: "persistent-prompt3-agent",
                   sessionId: "prompt3-session-1"))
        {
            first.Start();
            using var activation = first.Activate();
            AgentImpactObservabilityRecorder.RecordPacketGenerated(
                packet,
                packet.Task,
                packet.Project,
                packet.Repository);
            AgentImpactObservabilityRecorder.RecordPredictedImpact(packet, prediction);
            AgentImpactObservabilityRecorder.RecordActualImpact(packet, actual);
            AgentImpactObservabilityRecorder.RecordPacketExpanded(
                AgentImpactObservabilityIdentity.PacketId(packet),
                "expand:runtime",
                identity.SourceRevision,
                identity.IndexGeneration);
            AgentImpactObservabilityRecorder.RecordValidationPlan(plan);
            AgentImpactObservabilityRecorder.RecordValidationStarted(
                plan,
                ["tests.prompt3"],
                "validation-prompt3",
                ["recipe.prompt3"]);
            AgentImpactObservabilityRecorder.RecordValidationCompleted(
                plan,
                suiteResult,
                ["tests.prompt3"],
                ["recipe.prompt3"]);
            AgentImpactObservabilityRecorder.RecordAgentValidationChange(
                plan,
                agentPlan,
                new AgentValidationRequest(AdditionalTestIds: ["tests.agent"]));
            AgentImpactObservabilityRecorder.RecordAgentValidationChange(
                agentPlan,
                reducedPlan,
                new AgentValidationRequest(RemoveRequirementIds: ["test.prompt3"]));
            AgentImpactObservabilityRecorder.RecordLearning(result, source);
            first.Complete();
        }

        using (var secondRun = new AgentObservabilityRun(
                   "run-prompt3-impact-2",
                   store,
                   new NoopAgentObservabilityTelemetry()))
        using (AgentObservabilitySession second = secondRun.CreateAgent(
                   "mod.prompt3",
                   "Prompt 3",
                   "agent-prompt3-2",
                   logicalAgentId: "persistent-prompt3-agent",
                   sessionId: "prompt3-session-2"))
        {
            second.Start();
            using var activation = second.Activate();
            AgentImpactObservabilityRecorder.RecordPacketStatus(
                AgentImpactObservabilityIdentity.PacketId(packet),
                ExecutionPacketStatuses.PartiallyStale,
                "source changed after packet generation",
                "source-2",
                identity.IndexGeneration);
            AgentImpactObservabilityRecorder.RecordStaleEvidenceRejected(
                source,
                source with { SourceRevision = "source-2" },
                "validation evidence source revision is stale");
            second.Complete();
        }

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView view = ui.ShowAgent("agent-prompt3-2").Agent!;
        AgentObservabilityExecutionImpact impact = view.ExecutionImpact!;
        AssertEqual(AgentImpactObservabilityIdentity.PacketId(packet), impact.PacketId);
        AssertEqual(2048, impact.Metrics.PacketBytes);
        Assert(impact.PredictedFiles.Contains("src/Changed.cs"));
        Assert(impact.ActualFiles.Contains("src/Dependent.cs"));
        Assert(impact.RequiredValidation.Any(item => item.Value.Contains("changed direct dependency", StringComparison.Ordinal)));
        Assert(impact.Learning.Any(item => item.PromotedGlobal == false && item.FromIdentity == "node.changed"));
        AssertEqual(1, impact.Metrics.DeepExpansions);
        AssertEqual(1, impact.Metrics.StaleEvidenceRejections);
        AssertEqual("partially_stale", impact.PacketStatus);
        Assert(view.PastSessions.Count >= 1, "persistent logical agent history must remain visible");
        Assert(packet.Budget.UsedBytes <= packet.Budget.MaxBytes);
        Assert(impact.AgentValidation.Any(item => item.Value == "tests.agent"));
        AssertEqual(1, impact.Metrics.ValidationRecipes);
        AssertEqual(13L, impact.Metrics.ValidationMilliseconds);
        AssertEqual(false, impact.Metrics.PacketUsable);
        Assert(store.GetEvents().Any(
            eventRecord => eventRecord.Type == AgentEventTypes.ValidationReductionRejected));

        AgentEvent[] lifecycleEvents = store.GetEvents().Where(
            eventRecord => eventRecord.Type.StartsWith("execution.", StringComparison.Ordinal) ||
                eventRecord.Type.StartsWith("impact.", StringComparison.Ordinal) ||
                eventRecord.Type.StartsWith("validation.", StringComparison.Ordinal)).ToArray();
        Assert(lifecycleEvents.Length >= 9);
        Assert(lifecycleEvents.All(eventRecord =>
            !string.IsNullOrWhiteSpace(eventRecord.RunId) &&
            !string.IsNullOrWhiteSpace(eventRecord.AgentId) &&
            !string.IsNullOrWhiteSpace(eventRecord.LogicalAgentId) &&
            !string.IsNullOrWhiteSpace(eventRecord.SessionId)));
    }

    public static void ExecutionImpactAdministrationIsAudited()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "rimtest-prompt3-learning-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var source = new ValidationSourceIdentity("workspace-admin", "source-admin");
            var relationship = new LearnedImpactRelationship(
                ValidationPlanSchemas.LearningCurrent,
                "from.admin",
                "to.admin",
                "direct_dependents",
                "runtime",
                new ImpactProvenance("rimerror", "causal", "evidence-admin"),
                "project",
                Project: "project.admin");
            var learningStore = new ImpactLearningStore(path);
            learningStore.Append(relationship);
            using var store = new AgentObservabilityStore();
            using var run = new AgentObservabilityRun(
                "run-prompt3-admin",
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.admin",
                "Admin",
                "agent-admin",
                logicalAgentId: "persistent-admin-agent");
            agent.Start();
            using var activation = agent.Activate();
            var administration = new AgentImpactObservabilityAdministration(learningStore);
            administration.ExcludeForProject(
                relationship.FromIdentity,
                relationship.ToIdentity,
                relationship.RelationshipKind,
                "project.admin",
                source,
                "evidence-admin",
                "operator confirmed relationship is not causal");

            Assert(!administration.Inspect("project.admin").Any());
            AgentEvent audit = store.GetEvents(agentId: agent.AgentId).Single(
                eventRecord => eventRecord.Type == AgentEventTypes.ImpactRelationshipInvalidated);
            Assert(audit.Summary.Contains("invalidated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + ".overrides");
        }
    }

    public static void FailureAndRemediationObservability()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-prompt3-failure",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.failure",
            "Failure",
            "agent-failure");
        agent.Start();
        using var activation = agent.Activate();

        var identity = new global::RimDev.Contracts.ExecutionIdentity
        {
            RepositoryId = "repo-failure",
            ProjectId = "project.failure",
            SourceRevision = "source-failure",
            SourceFingerprint = "fingerprint-failure",
            ExecutionId = "execution-failure"
        };
        var packet = new global::RimDev.Contracts.FailureEvidencePacket
        {
            Identity = identity,
            FailedValidation = new global::RimDev.Contracts.EntityReference
            {
                Kind = global::RimDev.Contracts.EntityReferenceKinds.Test,
                Id = "tests.failure"
            },
            Classification = "validation-failed",
            Error = "assertion failed",
            ChangedSourceFiles = ["Source/Failure.cs"],
            Dependencies =
            [
                new global::RimDev.Contracts.EntityReference
                {
                    Kind = "dependency",
                    Id = "dependency.failure"
                }
            ],
            PrecedingEvidence =
            [
                new global::RimDev.Contracts.EvidenceReference
                {
                    Kind = "test",
                    Uri = "evidence.failure"
                }
            ]
        };
        var diagnosis = new global::RimDev.Contracts.FailureDiagnosis
        {
            Packet = packet,
            LikelyRootCause = "changed dependency affected validation",
            Confidence = "high",
            RelevantEvidence =
            [
                new global::RimDev.Contracts.EvidenceReference
                {
                    Kind = "test",
                    Uri = "evidence.failure"
                }
            ],
            ReproductionContext = [packet.FailedValidation!],
            ReductionCandidates =
            [
                new global::RimDev.Contracts.EntityReference
                {
                    Kind = "dependency",
                    Id = "dependency.failure"
                }
            ]
        };
        var precedent = new global::RimDev.Contracts.RemediationPrecedent
        {
            PrecedentId = "precedent-failure",
            FailureFamily = "validation-failed",
            Subject = packet.FailedValidation,
            Applicability = ["dependency.failure"],
            RootCause = diagnosis.LikelyRootCause,
            ValidatedRemediation = "run the targeted test after rebuilding",
            Evidence =
            [
                new global::RimDev.Contracts.EvidenceReference
                {
                    Kind = "test",
                    Uri = "evidence.failure"
                }
            ],
            SuccessfulValidationIdentity = identity,
            Provenance = new global::RimDev.Contracts.ContractProvenance
            {
                Source = "prompt3-test",
                References = [new global::RimDev.Contracts.EvidenceReference { Kind = "test", Uri = "evidence.failure" }]
            }
        };
        string learningPath = Path.Combine(
            Path.GetTempPath(),
            "rimtest-prompt3-remediation-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var learningStore = new ImpactLearningStore(learningPath);
            Assert(learningStore.RecordValidatedRemediation(precedent), "proven precedent is stored");
            var administration = new AgentImpactObservabilityAdministration(learningStore);
            administration.SetRemediationEligibility(
                precedent,
                eligible: false,
                "operator found the remediation too broad");
            global::RimDev.Contracts.RemediationPrecedent[] remaining = administration
                .InspectRemediation(precedent.FailureFamily, precedent.Subject)
                .ToArray();
            Assert(remaining.Length == 0, "ineligible precedent is excluded");
            Assert(store.GetEvents().Any(eventRecord =>
                eventRecord.Type == AgentEventTypes.RemediationPrecedentEligibilityChanged));
        }
        finally
        {
            TryDelete(learningPath);
        }


        AgentImpactObservabilityRecorder.RecordFailurePacket(packet);
        AgentImpactObservabilityRecorder.RecordDiagnosis(diagnosis);
        AgentImpactObservabilityRecorder.RecordRemediationPrecedent(precedent, reused: false);
        AgentImpactObservabilityRecorder.RecordRemediationPrecedent(precedent, reused: true);

        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAgentView view = ui.ShowAgent(agent.AgentId).Agent!;
        AssertEqual(2, view.FailureEvents.Count);
        AssertEqual(3, view.RemediationEvents.Count);
        Assert(view.RemediationEvents.Any(eventRecord =>
            eventRecord.Type == AgentEventTypes.RemediationPrecedentReused));
        string snapshot = JsonSerializer.Serialize(view, AgentObservabilityJson.Options);
        Assert(!snapshot.Contains("FailureEvents", StringComparison.Ordinal));
        Assert(!snapshot.Contains("RemediationEvents", StringComparison.Ordinal));
    }


    private static void RecordContent(
        AgentObservabilitySession session,
        string type,
        string state,
        string blueprintId,
        string projectId,
        string? precedentId = null,
        string? reuseSource = null,
        string? archetypeId = null,
        int? archetypeVersion = null,
        int? previousArchetypeVersion = null,
        string? validationResult = null,
        bool? qualified = null,
        bool? replayPassed = null,
        string? reason = null,
        ContentObservabilityMetrics? metrics = null,
        IReadOnlyDictionary<string, string>? designParameters = null,
        IReadOnlyList<string>? vanillaComparables = null,
        IReadOnlyList<string>? frameworkRequirements = null,
        IReadOnlyList<string>? frameworkDependencies = null,
        IReadOnlyList<string>? validationExpectations = null,
        string? evidenceId = null,
        IReadOnlyList<string>? referenceIds = null,
        IReadOnlyList<string>? supportingBlueprintIds = null,
        string? implementationNovelty = null)
    {
        session.Record(
            DevelopmentStage.Implementation,
            type,
            "Content lifecycle " + state + ".",
            new ContentObservabilityEventData(
                ContentObservabilitySchemas.EventData,
                state,
                ProjectId: projectId,
                BlueprintId: blueprintId,
                PrecedentId: precedentId,
                PatternId: precedentId,
                ArchetypeId: archetypeId,
                ArchetypeVersion: archetypeVersion,
                PreviousArchetypeVersion: previousArchetypeVersion,
                ReuseSource: reuseSource,
                Reason: reason,
                ValidationResult: validationResult,
                Qualified: qualified,
                ReplayPassed: replayPassed,
                EvidenceId: evidenceId,
                ReferenceIds: referenceIds,
                SupportingBlueprintIds: supportingBlueprintIds,
                Metrics: metrics,
                DesignParameters: designParameters,
                VanillaComparables: vanillaComparables,
                FrameworkRequirements: frameworkRequirements,
                FrameworkDependencies: frameworkDependencies,
                ValidationExpectations: validationExpectations,
                ImplementationNovelty: implementationNovelty));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
}
