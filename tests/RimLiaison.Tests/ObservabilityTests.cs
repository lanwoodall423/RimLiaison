using System.Diagnostics;
using System.Text.Json;
using RimLiaison;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ObservabilityTests
{
    public static void AssociationsAndViewsStayScoped()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-observability-1",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.alpha", "Alpha");
        agent.Start();
        agent.SetStage(DevelopmentStage.Implementation, "editing");
        AgentEvent? eventRecord = agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.FileModified,
            "Source file modified.",
            new { filePath = "Source/Alpha.cs", operationKey = "file:Source/Alpha.cs" });
        agent.Complete();

        Assert(eventRecord is not null, "the event should be accepted");
        AssertEqual("run-observability-1", eventRecord!.RunId);
        AssertEqual(agent.AgentId, eventRecord.AgentId);
        AssertEqual("mod.alpha", eventRecord.ModId);
        AssertEqual(DevelopmentStage.Implementation, eventRecord.Stage);
        Assert(store.GetEvents(agentId: agent.AgentId).All(
            value => value.RunId == eventRecord.RunId && value.ModId == eventRecord.ModId),
            "agent events must retain run and mod identity");
        AgentObservabilityView view = store.Query(agentId: agent.AgentId);
        AssertEqual(1, view.Agents.Count);
        Assert(view.Events.Count >= 4, "lifecycle events should be queryable");
        Assert(view.Events.SequenceEqual(
            view.Events.OrderBy(value => value.Sequence)),
            "event order must be deterministic");
    }

    public static void ConcurrentInterleavedAgentsRemainIsolated()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-concurrent",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();

        Task.WhenAll(
            Task.Run(async () =>
            {
                alpha.Record(
                    DevelopmentStage.Analysis,
                    AgentEventTypes.FileInspected,
                    "Mod A event.",
                    new { filePath = "Source/A.cs", operationKey = "file:a" });
                await Task.Yield();
                alpha.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.CommandFailed,
                    "Mod A error.",
                    new { operationKey = "command:a", command = "dotnet test A", exitCode = 1 });
                await Task.Yield();
                alpha.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.RecoveryCompleted,
                    "Mod A recovery.",
                    new { operationKey = "command:a", recovered = true });
            }),
            Task.Run(async () =>
            {
                beta.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildStarted,
                    "Mod B build.",
                    new { operationKey = "build:b" });
                await Task.Yield();
                beta.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildFailed,
                    "Mod B build failed.",
                    new { operationKey = "build:b", errorCode = "BUILD_FAILED" });
            })).GetAwaiter().GetResult();

        AgentEvent[] alphaEvents = store.GetEvents(agentId: alpha.AgentId).ToArray();
        AgentEvent[] betaEvents = store.GetEvents(agentId: beta.AgentId).ToArray();
        Assert(alphaEvents.Length > 0 && betaEvents.Length > 0, "both streams should contain events");
        Assert(alphaEvents.All(value => value.AgentId == alpha.AgentId && value.ModId == "mod.alpha"),
            "Mod A events must not contain Mod B identity");
        Assert(betaEvents.All(value => value.AgentId == beta.AgentId && value.ModId == "mod.beta"),
            "Mod B events must not contain Mod A identity");
        AgentIssue[] betaIssues = store.GetIssues(agentId: beta.AgentId).ToArray();
        Assert(betaIssues.Any(value => value.Category == AgentIssueCategory.Error),
            "Mod B build failure should create an issue");
        Assert(store.GetIssues(agentId: alpha.AgentId).All(value => value.ModId == "mod.alpha"),
            "issue queries must remain agent-scoped");
    }

    public static void LifecycleCompletionAndFailureAreStructured()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-lifecycle",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession completed = run.CreateAgent("mod.complete", "Complete");
        completed.Start();
        completed.SetStage(DevelopmentStage.Packaging, "packaging");
        completed.Complete();
        AssertEqual(AgentStatus.Completed, completed.Snapshot.Status);
        AssertEqual(DevelopmentStage.Complete, completed.Snapshot.CurrentStage);
        AssertEqual(AgentCompletionState.Succeeded, completed.Snapshot.CompletionState);

        using AgentObservabilitySession failed = run.CreateAgent("mod.failed", "Failed");
        failed.Start();
        failed.SetStage(DevelopmentStage.Testing, "testing");
        failed.Fail("Test failed.", "TEST_FAILED");
        AssertEqual(AgentStatus.Failed, failed.Snapshot.Status);
        Assert(failed.Snapshot.FailureState, "failure state must be exposed to the UI");
        AssertEqual("Test failed.", failed.Snapshot.FailureSummary);
        Assert(store.GetEvents(agentId: completed.AgentId).Any(
            value => value.Type == AgentEventTypes.AgentCompleted));
        Assert(store.GetEvents(agentId: failed.AgentId).Any(
            value => value.Type == AgentEventTypes.AgentFailed));
    }

    public static void FailureIssueRecoveryAndReferencesWork()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-issues",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.issues", "Issues");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandStarted,
            "Command started.",
            new { operationKey = "command:test", command = "dotnet test" });
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Command failed.",
            new
            {
                operationKey = "command:test",
                command = "dotnet test",
                exitCode = 1,
                errorCode = "TEST_FAILED"
            });
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RecoveryCompleted,
            "Command recovered.",
            new { operationKey = "command:test", recovered = true });

        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single(
            value => value.Category == AgentIssueCategory.Error);
        Assert(issue.Recovered, "recovered issues must remain visible as recovered");
        Assert(issue.EventIds.Count >= 2, "the issue must reference problem and recovery events");
        Assert(issue.ResolutionEventId is not null, "recovery must have a stable resolution reference");
        Assert(issue.RelatedCommands?.Contains("dotnet test") == true,
            "commands should be stored structurally on the issue");
        Assert(store.GetEvents(agentId: agent.AgentId)
            .Where(value => issue.EventIds.Contains(value.Id))
            .Select(value => value.Id)
            .SequenceEqual(issue.EventIds),
            "issue event references must resolve to supporting events in order");
    }

    public static void RetryHeuristicsAndStallsAreConservative()
    {
        using var store = new AgentObservabilityStore(options: new AgentObservabilityOptions
        {
            StallThreshold = TimeSpan.FromSeconds(5)
        });
        using var run = new AgentObservabilityRun(
            "run-heuristics",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.heuristics", "Heuristics");
        agent.Start();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "Repeated command failure.",
                new { operationKey = "command:retry", command = "tool --safe", exitCode = 2 });
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.RetryStarted,
                "Retry started.",
                new { operationKey = "command:retry" });
        }
        agent.Waiting("waiting for dependency", TimeSpan.FromSeconds(8));
        for (int index = 0; index < 3; index++)
        {
            agent.Record(
                DevelopmentStage.Research,
                AgentEventTypes.FileInspected,
                "Inspected source file.",
                new { operationKey = "file:repeat.cs", filePath = "repeat.cs" });
        }

        AgentIssue[] issues = store.GetIssues(agentId: agent.AgentId).ToArray();
        Assert(issues.Any(value => value.Category == AgentIssueCategory.Retry),
            "repeated failures should produce a retry issue");
        Assert(issues.Any(value => value.Category == AgentIssueCategory.Stall),
            "long explicit waits should produce a stall issue");
        Assert(issues.Any(value => value.Category == AgentIssueCategory.RedundantWork &&
            value.Severity == AgentIssueSeverity.Info),
            "repeated work should be reported conservatively as informational potential work");
    }

    public static void DiagnosticBundlesExcludeUnrelatedHistory()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-bundle",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession alpha = run.CreateAgent("mod.alpha", "Alpha");
        using AgentObservabilitySession beta = run.CreateAgent("mod.beta", "Beta");
        alpha.Start();
        beta.Start();
        alpha.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.ToolFailed,
            "Tool failed.",
            new
            {
                operationKey = "tool:alpha",
                toolName = "compiler",
                command = "compiler Alpha.cs",
                filePath = "Source/Alpha.cs",
                errorCode = "COMPILER_FAILED"
            });
        beta.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.ToolFailed,
            "Unrelated tool failed.",
            new { operationKey = "tool:beta", toolName = "compiler", filePath = "Source/Beta.cs" });
        AgentIssue alphaIssue = store.GetIssues(agentId: alpha.AgentId).Single();
        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([alphaIssue.Id]);

        AssertEqual(1, bundle.Issues.Count);
        AssertEqual(alphaIssue.Id, bundle.IssueIds.Single());
        Assert(bundle.Mods.Any(value => value.ModId == "mod.alpha"),
            "bundle should include the selected mod identity");
        Assert(bundle.SupportingEvents.All(value => value.AgentId == alpha.AgentId),
            "bundle must exclude unrelated agent history");
        Assert(bundle.Files.Contains("Source/Alpha.cs"),
            "bundle should include related files");
        Assert(bundle.Commands.Contains("compiler Alpha.cs"),
            "bundle should include related commands");
    }

    public static void DiagnosticBundleV2ContainsStructuredBuildEvidence()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-diagnostic-v2",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession buildAgent = run.CreateAgent(
            "mod.build",
            "Build Mod");
        using AgentObservabilitySession unrelatedAgent = run.CreateAgent(
            "mod.unrelated",
            "Unrelated Mod");
        buildAgent.Start();
        unrelatedAgent.Start();

        buildAgent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildStarted,
            "Build started.",
            new
            {
                operationKey = "build:fixture",
                project = "fixture",
                sourceProject = "Source/Fixture.csproj",
                configuration = "Debug",
                command = "dotnet build Source/Fixture.csproj --configuration Debug",
                workingDirectory = "C:/repo",
                transactionId = "tx-build-1",
                workflowId = "wf-build-1",
                sourceFingerprint = "source-fingerprint-1"
            });
        buildAgent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Build and deployment command failed.",
            new
            {
                operationKey = "build:fixture",
                project = "fixture",
                sourceProject = "Source/Fixture.csproj",
                configuration = "Debug",
                command = "dotnet build Source/Fixture.csproj --configuration Debug",
                workingDirectory = "C:/repo",
                exitCode = 1,
                stderr = "Source/Fixture.cs(12,7): error CS0246: The type or namespace name 'MissingType' could not be found.",
                diagnosticOutput = "error CS0246: The type or namespace name 'MissingType' could not be found.",
                transactionId = "tx-build-1",
                workflowId = "wf-build-1",
                builtSha256 = new string('b', 64),
                token = "secret-value"
            });
        unrelatedAgent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Unrelated build failed.",
            new
            {
                operationKey = "build:unrelated",
                command = "dotnet build Unrelated.csproj",
                exitCode = 1,
                diagnosticOutput = "CS9999 unrelated failure",
                transactionId = "tx-build-1",
                workflowId = "wf-build-1"
            });
        buildAgent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.IntegrationFailed,
            "Build wrapper failed after the compiler failure.",
            new
            {
                operationKey = "build:fixture",
                transactionId = "tx-build-1",
                workflowId = "wf-build-1"
            });

        AgentIssue selected = store.GetIssues(agentId: buildAgent.AgentId)
            .First(issue => issue.Category == AgentIssueCategory.Error);
        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([selected.Id]);

        AssertEqual(AgentObservabilitySchemas.Bundle, bundle.SchemaVersion);
        Assert(bundle.SelectedIssueIds.SequenceEqual([selected.Id]));
        Assert(bundle.SelectedIssues.Any(issue => issue.Id == selected.Id));
        Assert(bundle.CorrelatedIssues.Any(issue => issue.Category == AgentIssueCategory.IntegrationIssue),
            "same-operation wrapper failures should be separated as correlated issues");
        Assert(bundle.SupportingEvents.All(eventRecord =>
            eventRecord.AgentId == buildAgent.AgentId),
            "causal closure must exclude the concurrent agent");
        AgentDiagnosticBuildEvidence build = bundle.BuildEvidence.First(value =>
            value.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true);
        AssertEqual("Source/Fixture.csproj", build.SourceProject);
        AssertEqual("Debug", build.Configuration);
        AssertEqual(1, build.ExitCode);
        Assert(build.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true);
        Assert(build.TransactionId == "tx-build-1" && build.WorkflowId == "wf-build-1");
        Assert(bundle.CommandEvidence.Any(command =>
            command.Command?.Contains("dotnet build Source/Fixture.csproj", StringComparison.Ordinal) == true &&
            command.ExitCode == 1 &&
            command.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true));
        Assert(bundle.Correlations.Any(value =>
            value.Kind == "transaction" && value.Value == "tx-build-1"));
        Assert(bundle.Completeness.IsComplete,
            "a compiler diagnostic plus command/build metadata should be complete");

        string json = JsonSerializer.Serialize(bundle, AgentObservabilityJson.Options);
        Assert(!json.Contains("secret-value", StringComparison.Ordinal),
            "new structured evidence must retain credential redaction");
        Assert(!json.Contains("CS9999 unrelated failure", StringComparison.Ordinal),
            "unrelated agent diagnostics must not enter the bundle");
    }

    public static void DiagnosticBundleMissingBuildDiagnosticsIsExplicitlyIncomplete()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-diagnostic-incomplete",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.incomplete",
            "Incomplete Build");
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "Build failed without diagnostics.",
            new
            {
                operationKey = "build:incomplete",
                project = "incomplete",
                command = "dotnet build Incomplete.csproj",
                exitCode = 1
            });

        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([issue.Id]);

        AssertEqual(AgentDiagnosticCompletenessStatuses.Incomplete, bundle.Completeness.Status);
        Assert(bundle.Completeness.MissingEvidence.Contains("build.diagnostics"));
        Assert(bundle.BuildEvidence.Count > 0);
        Assert(bundle.BuildEvidence.All(value =>
            string.IsNullOrWhiteSpace(value.Output) &&
            string.IsNullOrWhiteSpace(value.DiagnosticOutput) &&
            string.IsNullOrWhiteSpace(value.ErrorOutput)));
    }

    public static void DiagnosticEvidenceSurvivesStoreReloadOutsideWorktree()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-diagnostic-evidence-" + Guid.NewGuid().ToString("N"));
        string? issueId = null;
        try
        {
            using (var store = new AgentObservabilityStore(directory))
            using (var run = new AgentObservabilityRun(
                       "run-diagnostic-evidence",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent(
                       "mod.evidence",
                       "Evidence Mod"))
            {
                agent.Start();
                AgentDiagnosticEvidenceReference? evidence = store.PersistEvidence(
                    "compiler.diagnostics",
                    new string('x', 5_000) +
                    "\nerror CS0246: MissingType was not found. apiKey=secret-value");
                Assert(evidence is not null, "the bounded evidence store should accept compiler output");
                agent.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildFailed,
                    "Build failed with persisted diagnostics.",
                    new
                    {
                        operationKey = "build:evidence",
                        command = "dotnet build Evidence.csproj",
                        exitCode = 1,
                        diagnosticEvidenceId = evidence!.Id
                    });
                issueId = store.GetIssues(agentId: agent.AgentId).Single().Id;
                AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([issueId]);
                Assert(bundle.BuildEvidence.Any(value =>
                    value.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true));
                Assert(!JsonSerializer.Serialize(bundle, AgentObservabilityJson.Options)
                    .Contains("secret-value", StringComparison.Ordinal));
            }

            using var reloaded = new AgentObservabilityStore(directory);
            AgentDiagnosticBundle reloadedBundle = reloaded.CreateDiagnosticBundle([issueId!]);
            Assert(reloadedBundle.BuildEvidence.Any(value =>
                value.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true),
                "long-form evidence must be available after a store reload");
            Assert(reloadedBundle.Completeness.IsComplete);
            Assert(Directory.Exists(Path.Combine(directory, "evidence")),
                "evidence must live under the canonical observability root");
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

    public static void DiagnosticEvidenceHonorsConfiguredBounds()
    {
        using var store = new AgentObservabilityStore(options: new AgentObservabilityOptions
        {
            MaximumEvidenceBytes = 128,
            MaximumEvidenceEntries = 2
        });
        AgentDiagnosticEvidenceReference? reference = store.PersistEvidence(
            "compiler.diagnostics",
            new string('x', 512) + " apiKey=secret-value");
        Assert(reference is not null && reference.Truncated,
            "evidence larger than the configured bound must be marked truncated");
        AgentDiagnosticEvidence? evidence = store.GetEvidence(reference!.Id);
        Assert(evidence is not null && evidence.Content.Length <= 128,
            "persisted evidence must remain within its configured character bound");
        Assert(!evidence!.Content.Contains("secret-value", StringComparison.Ordinal),
            "bounded evidence must retain redaction while truncating");
    }

    public static void DurableStoreReloadsStructuredState()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-observability-" + Guid.NewGuid().ToString("N"));
        try
        {
            string issueId;
            using (var store = new AgentObservabilityStore(directory))
            using (var run = new AgentObservabilityRun(
                       "run-durable",
                       store,
                       new NoopAgentObservabilityTelemetry()))
            using (AgentObservabilitySession agent = run.CreateAgent("mod.durable", "Durable"))
            {
                agent.Start();
                agent.Record(
                    DevelopmentStage.Testing,
                    AgentEventTypes.TestFailed,
                    "Durable failure.",
                    new { operationKey = "test:durable", errorCode = "TEST_FAILED" });
                issueId = store.GetIssues(agentId: agent.AgentId).Single().Id;
            }

            using var reloaded = new AgentObservabilityStore(directory);
            Assert(reloaded.GetAgents(runId: "run-durable").Count == 1,
                "agent snapshots must survive reload");
            Assert(reloaded.GetEvents(runId: "run-durable").Count > 0,
                "events must survive reload");
            Assert(reloaded.GetIssues(runId: "run-durable").Any(value => value.Id == issueId),
                "issues must survive reload");
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

    public static void OTelCorrelationIsOptionalAndHierarchical()
    {
        using var store = new AgentObservabilityStore();
        using var telemetry = new OpenTelemetryAgentTelemetry(
            new AgentObservabilityTelemetryOptions { Enabled = true });
        using var run = new AgentObservabilityRun("run-otel", store, telemetry);
        using AgentObservabilitySession agent = run.CreateAgent("mod.otel", "OTel");
        agent.Start();
        using (AgentOperationScope operation = agent.BeginOperation(
                   "tool",
                   "tool.execute",
                   DevelopmentStage.Implementation,
                   "tool:otel",
                   new { toolName = "fake-tool" })!)
        {
            operation.Complete("Tool completed.");
        }

        AgentEvent[] events = store.GetEvents(runId: "run-otel").ToArray();
        AgentEvent agentEvent = events.Single(value => value.Type == AgentEventTypes.AgentCreated);
        AgentEvent operationEvent = events.Single(value => value.Type == AgentEventTypes.ToolCompleted);
        Assert(!string.IsNullOrWhiteSpace(agentEvent.TraceId),
            "enabled OTel should attach a trace id to product events");
        Assert(!string.IsNullOrWhiteSpace(operationEvent.SpanId),
            "meaningful operations should attach a span id");
        AssertEqual(agentEvent.TraceId, operationEvent.TraceId);
    }

    public static void OTelDisabledStillStoresProductState()
    {
        using var store = new AgentObservabilityStore();
        using var telemetry = new OpenTelemetryAgentTelemetry(
            new AgentObservabilityTelemetryOptions { Enabled = false });
        using var run = new AgentObservabilityRun("run-otel-off", store, telemetry);
        using AgentObservabilitySession agent = run.CreateAgent("mod.off", "OTel Off");
        agent.Start();
        agent.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.SearchCompleted,
            "Search completed.",
            new { operationKey = "search:off" });

        AgentEvent eventRecord = store.GetEvents(agentId: agent.AgentId).Single(
            value => value.Type == AgentEventTypes.SearchCompleted);
        Assert(eventRecord.TraceId is null && eventRecord.SpanId is null,
            "disabled OTel must not be required for product state");
        Assert(store.GetAgents(agentId: agent.AgentId).Count == 1,
            "agent state must remain available with telemetry disabled");
    }

    public static void TelemetryFailureCannotBreakExecution()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-otel-failure",
            store,
            new ThrowingTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.failure", "Telemetry Failure");
        agent.Start();
        agent.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.TestPassed,
            "Test still passed.",
            new { operationKey = "test:failure" });
        agent.Complete();
        Assert(store.GetEvents(agentId: agent.AgentId).Any(
            value => value.Type == AgentEventTypes.TestPassed),
            "telemetry exporter/provider errors must not stop agent execution");
    }

    public static void RuntimeOperationsDoNotNeedModelCalls()
    {
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-runtime",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent("mod.runtime", "Runtime");
        using IDisposable activation = agent.Activate();
        agent.Start();
        using (AgentOperationScope operation = AgentObservabilityRuntime.BeginOperation(
                   "command",
                   "shell command",
                   DevelopmentStage.Implementation,
                   "command:runtime",
                   new { command = "echo safe" })!)
        {
            operation.Complete("Command completed.", new { exitCode = 0 });
        }

        Assert(store.GetEvents(agentId: agent.AgentId).Any(
            value => value.Type == AgentEventTypes.CommandStarted),
            "runtime instrumentation should emit from execution primitives");
        Assert(store.GetEvents(agentId: agent.AgentId).Any(
            value => value.Type == AgentEventTypes.CommandCompleted),
            "runtime instrumentation should emit completion without narration");
    }

    public static void CliWiresTheAuthoritativeStore()
    {
        using var store = new AgentObservabilityStore();
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = CliApplication.RunAsync(
                [
                    "list",
                    "--catalog",
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "TestCatalog",
                        "rimtest.catalog.json")
                ],
                output,
                error,
                observabilityStore: store,
                observabilityTelemetry: new NoopAgentObservabilityTelemetry())
            .GetAwaiter()
            .GetResult();

        AssertEqual(0, exitCode);
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.CommandStarted),
            "the CLI should wire command start events into the product store");
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.CommandCompleted),
            "the CLI should wire command completion events into the product store");
        Assert(store.GetAgents().Any(value => value.ModName == "RimLiaison"),
            "the CLI should expose the mod display name as the agent identity");
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
            throw new InvalidOperationException(
                $"Expected {expected}, got {actual}.");
        }
    }

    private sealed class ThrowingTelemetry : IAgentObservabilityTelemetry
    {
        public bool Enabled => true;

        public Activity? StartActivity(
            string name,
            ActivityContext? parentContext = null,
            IReadOnlyDictionary<string, object?>? tags = null) =>
            throw new InvalidOperationException("simulated exporter failure");

        public void RecordEvent(string type) =>
            throw new InvalidOperationException("simulated exporter failure");

        public void RecordOperation(
            string operationType,
            DevelopmentStage stage,
            string outcome,
            double durationMilliseconds) =>
            throw new InvalidOperationException("simulated exporter failure");

        public void RecordAgentDuration(
            DevelopmentStage stage,
            string outcome,
            double durationMilliseconds) =>
            throw new InvalidOperationException("simulated exporter failure");

        public void RecordIssue(AgentIssueCategory category, bool recovered) =>
            throw new InvalidOperationException("simulated exporter failure");

        public void Dispose()
        {
        }
    }
}
