using RimLiaison;
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
            Assert(aggregate.QualificationPassed, "deterministic qualification should pass");
            Assert(!aggregate.IsPromotionReady, "qualification alone must not claim promotion readiness");
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
    public static void QualificationCliProfilesHaveAuthoritativeRunCounts()
    {
        CliRequest single = CliParser.Parse(["qualification"]);
        AssertEqual("qualification", single.Id!, "default qualification command id");
        AssertEqual(1, single.QualificationRuns, "default qualification run count");
        AssertEqual(
            QualificationProfiles.Single,
            QualificationProfiles.ResolveProfile(single.Id),
            "default qualification profile");

        CliRequest explicitRuns = CliParser.Parse(["qualification", "--runs", "3"]);
        AssertEqual(3, explicitRuns.QualificationRuns, "explicit diagnostic run count");
        Assert(explicitRuns.QualificationRunsSpecified, "explicit run count should be tracked");

        CliRequest burnIn = CliParser.Parse(["qualification", "burn-in"]);
        AssertEqual(
            QualificationProfiles.PromotionBurnIn,
            QualificationProfiles.ResolveProfile(burnIn.Id),
            "burn-in profile");
        AssertEqual(
            QualificationProfiles.PromotionBurnInRuns,
            burnIn.QualificationRuns,
            "intrinsic burn-in run count");

        CliRequest explicitBurnIn = CliParser.Parse(["qualification", "burn-in", "--runs", "25"]);
        AssertEqual(25, explicitBurnIn.QualificationRuns, "explicit burn-in compatibility count");

        foreach (string conflictingRuns in new[] { "1", "24", "26" })
        {
            try
            {
                _ = CliParser.Parse(["qualification", "burn-in", "--runs", conflictingRuns]);
                throw new InvalidOperationException(
                    $"conflicting burn-in run count {conflictingRuns} was accepted");
            }
            catch (CliParseException exception)
            {
                Assert(
                    exception.Message.Contains("qualification burn-in requires exactly 25 runs",
                        StringComparison.Ordinal),
                    "conflicting burn-in count must fail with the contract error");
            }
        }
    }

    public static void BurnInAggregateAndExecutionInvariantAreDefensive()
    {
        using var store = NewStore(out string directory);
        try
        {
            QualificationAggregate aggregate = new QualificationHarness().Run(
                QualificationProfiles.PromotionBurnInRuns,
                QualificationProfiles.PromotionBurnIn,
                store);
            AssertEqual(
                QualificationProfiles.PromotionBurnIn,
                aggregate.Profile,
                "burn-in aggregate profile");
            AssertEqual(
                QualificationProfiles.PromotionBurnInRuns,
                aggregate.TotalRuns,
                "burn-in aggregate total runs");
            AssertEqual(
                QualificationProfiles.PromotionBurnInRuns,
                aggregate.Passes,
                "burn-in aggregate passes");

            try
            {
                _ = new QualificationHarness().Run(1, QualificationProfiles.PromotionBurnIn, store);
                throw new InvalidOperationException(
                    "inconsistent burn-in execution request was accepted");
            }
            catch (InvalidOperationException exception)
            {
                Assert(
                    exception.Message.Contains("requires exactly 25 runs", StringComparison.Ordinal),
                    "inconsistent burn-in request must fail closed");
            }
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
