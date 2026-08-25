using System.Text.Json;
using RimLiaison;
using RimLiaison.Observability;

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
