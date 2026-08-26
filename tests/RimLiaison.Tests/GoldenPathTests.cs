using RimLiaison.GoldenPath;
using RimLiaison.Observability;
using RimLiaison.Validation;

namespace RimLiaison.Tests;

internal static class GoldenPathTests
{
    public static void SuccessfulGoldenPathCompletion()
    {
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-success", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.golden", "Golden Mod", "agent.golden", sessionId: "session.golden");
        GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor("golden"),
            Operation("build", GoldenPathStage.Build, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed()),
            Operation("runtime", GoldenPathStage.RuntimeValidation, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed(), ["build"]));

        Assert(result.Status == "PASS", "all required Golden Path steps should pass");
        Assert(session.Snapshot.SessionId == "session.golden", "session identity should remain stable");
        Assert(session.Snapshot.CompletionResult == "PASS", "completion result should be published");
    }

    public static void ProductionStateAndEventsAreStructured()
    {
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-events", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.events", "Events Mod", "agent.events", sessionId: "session.events");
        _ = Run(session, GoldenPathPreflightResult.ReadyFor(),
            Operation("requirements", GoldenPathStage.Requirements, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed()));

        AgentEvent[] events = store.GetEvents(run.RunId, session.AgentId, session.ModId, 200).ToArray();
        Assert(events.Any(value => value.Type == AgentEventTypes.ProductionStateChanged), "state transitions must be events");
        Assert(events.All(value => value.RunId == run.RunId && value.AgentId == session.AgentId && value.ModId == session.ModId && value.SessionId == session.SessionId), "events must carry all production identities");
    }

    public static void OptionalRuntimeUnavailableStillPasses()
    {
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-optional", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.optional", "Optional Mod", "agent.optional");
        GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor(),
            Operation("build", GoldenPathStage.Build, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed()),
            Operation("runtime", GoldenPathStage.RuntimeValidation, ValidationClassification.BEST_EFFORT, _ => GoldenPathStepResult.OptionalUnavailable("Quicktest unavailable", "DevBridge2", "RUNTIME_UNAVAILABLE", "Add Quicktest readiness support.", "runtime"), ["build"]));

        Assert(result.Status == "PASS", "optional runtime absence must not block production");
        Assert(result.RecommendationIds.Count > 0, "optional runtime absence should produce a recommendation");
    }

    public static void RequiredRuntimeUnavailableBlocksOnlyDependentClaim()
    {
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-required", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.required", "Required Mod", "agent.required");
        GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor(),
            Operation("build", GoldenPathStage.Build, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed()),
            Operation("runtime", GoldenPathStage.RuntimeValidation, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.ToolingFailure("Quicktest unavailable", "RUNTIME_UNAVAILABLE", "DevBridge2", false), ["build"]),
            Operation("publish", GoldenPathStage.Publish, ValidationClassification.REQUIRED, _ => GoldenPathStepResult.Passed(), ["runtime"]));

        Assert(result.Status == "VALIDATION_INCOMPLETE", "required runtime absence must be incomplete");
        Assert(result.Validation.RequiredPassed == 1, $"successful build evidence should remain credited (passed={result.Validation.RequiredPassed}, failed={result.Validation.RequiredFailed}, unavailable={result.Validation.RequiredUnavailable}, status={result.Status})");
        Assert(result.Steps["publish"].State == GoldenPathStepState.NotExecuted, "only dependent publish claim should be skipped");
    }

    public static void InfrastructureRetrySucceeds()
    {
        int attempts = 0;
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-retry", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.retry", "Retry Mod", "agent.retry");
        GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor(),
            Operation("build", GoldenPathStage.Build, ValidationClassification.REQUIRED,
                _ => ++attempts == 1
                    ? GoldenPathStepResult.ToolingFailure("temporary build owner failure", "BUILD_TRANSIENT", "DevBridge2")
                    : GoldenPathStepResult.Passed()));

        Assert(result.Status == "PASS" && attempts == 2, "a bounded infrastructure retry should recover");
        Assert(store.GetEvents(run.RunId, session.AgentId, session.ModId, 200).Any(value => value.Type == AgentEventTypes.RetryCompleted), "retry completion should be visible");
    }

    public static void FailedRetryCreatesIncidentWithoutToolDevelopment()
    {
        using AgentObservabilityStore store = new();
        using AgentObservabilityRun run = new("run-golden-incident", store, new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession session = run.CreateAgent("mod.incident", "Incident Mod", "agent.incident");
        GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor(),
            Operation("runtime", GoldenPathStage.RuntimeValidation, ValidationClassification.BEST_EFFORT,
                _ => GoldenPathStepResult.ToolingFailure("runtime owner failed", "RUNTIME_OWNER_FAILED", "DevBridge2"),
                retry: _ => GoldenPathStepResult.ToolingFailure("runtime owner still failed", "RUNTIME_RETRY_FAILED", "DevBridge2", false)));

        AgentIssue incident = store.GetIssues(run.RunId, session.AgentId, session.ModId).Single(value => value.Category == AgentIssueCategory.ToolingFailure);
        Assert(result.Status == "PASS", "optional infrastructure failure must not stop the mod");
        Assert(incident.ComponentOwner == "DevBridge2" && incident.CurrentState == "open", "persistent owner failure must be an incident");
        AgentEvent retry = store.GetEvents(run.RunId, session.AgentId, session.ModId, 200).Single(value => value.Type == AgentEventTypes.RetryCompleted);
        Assert(AgentObservabilityData.GetString(retry.Data, "automaticToolRepair") != "true", "retry must not start tooling development");
    }

    public static void RecommendationPersistsAfterCompletion()
    {
        using AgentObservabilityStore store = new();
        string directory = Path.Combine(Path.GetTempPath(), "rimliaison-golden-" + Guid.NewGuid().ToString("N"));
        using (AgentObservabilityStore persisted = new(directory))
        using (AgentObservabilityRun run = new("run-golden-persist", persisted, new NoopAgentObservabilityTelemetry()))
        using (AgentObservabilitySession session = run.CreateAgent("mod.persist", "Persist Mod", "agent.persist"))
        {
            GoldenPathRunResult result = Run(session, GoldenPathPreflightResult.ReadyFor(),
                Operation("runtime", GoldenPathStage.RuntimeValidation, ValidationClassification.BEST_EFFORT,
                    _ => GoldenPathStepResult.OptionalUnavailable("runtime unavailable", "DevBridge2", recommendation: "Expose a bounded Quicktest readiness probe.")));
            Assert(result.Status == "PASS", "recommendation scenario should complete");
        }

        using AgentObservabilityStore reopened = new(directory);
        Assert(reopened.GetIssues(runId: "run-golden-persist", includeRecovered: true).Any(value => value.Category == AgentIssueCategory.ToolingImprovement), "recommendations must survive session end");
        Directory.Delete(directory, recursive: true);
    }

    private static GoldenPathRunResult Run(
        AgentObservabilitySession session,
        GoldenPathPreflightResult preflight,
        params GoldenPathOperation[] operations) =>
        new GoldenPathOrchestrator().RunAsync(
            new GoldenPathRunRequest
            {
                Identity = new GoldenPathIdentity
                {
                    ModId = session.ModId,
                    AgentId = session.AgentId,
                    RunId = session.RunId,
                    SessionId = session.SessionId
                },
                Preflight = preflight,
                Operations = operations
            },
            session).GetAwaiter().GetResult();

    private static GoldenPathOperation Operation(
        string id,
        GoldenPathStage stage,
        ValidationClassification classification,
        Func<GoldenPathOperationContext, GoldenPathStepResult> execute,
        IReadOnlyList<string>? dependsOn = null,
        Func<GoldenPathOperationContext, GoldenPathStepResult>? retry = null) => new()
        {
            Id = id,
            Stage = stage,
            Check = ValidationPolicyEvaluator.Define(
                id,
                classification,
                classification == ValidationClassification.REQUIRED
                    ? ValidationRequirementSource.TASK_REQUIREMENT
                    : ValidationRequirementSource.DISCOVERED,
                id,
                classification == ValidationClassification.REQUIRED ? "RimTest" : "DevBridge2"),
            DependsOn = dependsOn ?? [],
            ExecuteAsync = (context, _) => Task.FromResult(execute(context)),
            RetryAsync = retry is null ? null : (context, _) => Task.FromResult(retry(context))
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
