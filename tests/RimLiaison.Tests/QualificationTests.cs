using RimLiaison.Observability;
using RimLiaison.Qualification;
using RimLiaison.Validation;

namespace RimLiaison.Tests;

internal static class QualificationTests
{
    public static void HarnessCoversDeterministicContract()
    {
        using var store = NewStore(out string directory);
        try
        {
            QualificationAggregate aggregate = new QualificationHarness().Run(1, "single", store);
            string[] required =
            [
                "build.success", "build.failure", "deployment", "deployment.stale",
                "quicktest.launch", "readiness", "ownership.ambiguous",
                "runtime.success", "runtime.failure", "restart.timeout",
                "restart.recovery", "evidence.structured", "evidence.optional-missing",
                "optional.validation", "recommendation", "cleanup"
            ];
            Assert(aggregate.IsPromotionReady, "deterministic qualification should pass");
            AssertEqual(1, aggregate.TotalRuns, "qualification run count");
            AssertEqual(1, aggregate.Passes, "qualification passes");
            AssertEqual(0, aggregate.InfrastructureFailures, "qualification infrastructure failures");
            Assert(aggregate.RecoverySuccesses >= 2, "recovery coverage should be observed");
            AssertEqual(2, aggregate.RecommendationCount, "recommendation count");
            foreach (string id in required)
            {
                Assert(aggregate.Runs[0].Scenarios.Any(scenario => scenario.Id == id),
                    "missing qualification scenario: " + id);
            }
            AgentSnapshot snapshot = store.GetAgents().Single();
            AssertEqual("qualification", snapshot.WorkloadKind, "qualification workload identity");
            AssertEqual("experimental", snapshot.ToolchainState, "experimental toolchain identity");
            AssertEqual(AgentStatus.Completed, snapshot.Status, "qualification completion");
            Assert(store.GetEvents().Any(eventRecord =>
                eventRecord.Type == AgentEventTypes.ValidationRecommendationRecorded),
                "recommendation event publication");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void ProductionFirstRecommendationDoesNotFailModWorkflow()
    {
        using var store = NewStore(out string directory);
        try
        {
            using var run = new AgentObservabilityRun("production-recommendation", store);
            AgentObservabilitySession agent = run.CreateAgent(
                "fixture.production",
                "Production Fixture",
                agentId: "production-agent",
                logicalAgentId: "fixture.production");
            using IDisposable activation = agent.Activate();
            agent.Start("production");
            agent.RecordToolingRecommendation(
                "unsupported-check",
                "Unsupported validation is not available.",
                "Keep this optional check visible without blocking production.",
                "RimLiaison",
                "production://recommendation",
                affectedCurrentTask: false);
            agent.Complete("Production workflow completed.");
            AgentSnapshot snapshot = store.GetAgents().Single();
            AssertEqual(AgentStatus.Completed, snapshot.Status, "production should complete");
            Assert(!snapshot.FailureState, "recommendation must not fail production");
            AgentIssue issue = store.GetIssues().Single(issue =>
                issue.Category == AgentIssueCategory.ToolingImprovement);
            Assert(!issue.Blocking, "recommendation must be non-blocking");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void RequiredCapabilityCanFailGoldenPath()
    {
        using var store = NewStore(out string directory);
        try
        {
            using var run = new AgentObservabilityRun("required-blocker", store);
            AgentObservabilitySession agent = run.CreateAgent(
                "fixture.required",
                "Required Fixture",
                agentId: "required-agent",
                logicalAgentId: "fixture.required");
            using IDisposable activation = agent.Activate();
            agent.Start("production");
            agent.RecordToolingIncident(
                "required-readiness",
                "Required readiness capability failed.",
                "READINESS_TIMEOUT",
                "DevBridge2",
                ValidationClassification.REQUIRED,
                "readiness",
                "production://required-readiness",
                "not-recovered");
            agent.Fail("Required Golden Path capability failed.", "READINESS_TIMEOUT");
            AgentSnapshot snapshot = store.GetAgents().Single();
            AssertEqual(AgentStatus.Failed, snapshot.Status, "required capability should fail path");
            AgentIssue issue = store.GetIssues().Single(issue =>
                issue.OperationKey == "required-readiness");
            Assert(issue.Blocking, "required capability issue must block");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    public static void QualificationBacklogProjectsStructuredRecommendations()
    {
        using var store = NewStore(out string directory);
        try
        {
            QualificationAggregate aggregate = new QualificationHarness().Run(1, "single", store);
            IReadOnlyList<QualificationBacklogItem> backlog =
                QualificationHarness.BuildBacklog(aggregate, store);
            Assert(backlog.Any(item => item.Id == "qualification-parallel-coverage"),
                "qualification recommendation should produce backlog item");
            Assert(backlog.All(item => item is { Blocking: false, Status: "open" } || item.Blocking),
                "backlog must preserve blocking status");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static AgentObservabilityStore NewStore(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "rimliaison-qualification-" + Guid.NewGuid().ToString("N"));
        return new AgentObservabilityStore(directory);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }
}
