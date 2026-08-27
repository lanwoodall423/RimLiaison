using System.Text.Json;
using RimLiaison.DevBridge;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class FailureHandlingTests
{
    public static void ProjectCompilerFailureIsOwnedByProject()
    {
        (AgentIssue issue, AgentDiagnosticBundle bundle) = CreateBuildFailure(
            new
            {
                operationKey = "build:failure",
                causalDiagnostic = "Failing.cs(12,7): error CS0246: The type or namespace name 'MissingType' could not be found.",
                diagnosticSignature = "CS0246",
                orchestrator = "DevBridge2",
                failureSurface = "project-build",
                likelyOwner = "project",
                ownershipConfidence = "high",
                ownershipBasis = "native build failed with the same causal diagnostic",
                exitCode = 1
            });

        AgentObservabilityIssueTriage triage = BuildTriage(issue, bundle);
        Equal("Mod / project", triage.ProbableOwner.Owner);
        Equal("high", triage.ProbableOwner.Confidence);
        Equal("DevBridge2", triage.Orchestrator);
        Equal("project-build", triage.FailureSurface);
        True(triage.ProbableOwner.Reason.Contains("native build", StringComparison.OrdinalIgnoreCase),
            "project ownership must retain its causal basis");
    }

    public static void DevBridgeInjectedFailureIsOwnedByDevBridge()
    {
        (AgentIssue issue, AgentDiagnosticBundle bundle) = CreateBuildFailure(
            new
            {
                operationKey = "build:failure",
                causalDiagnostic = "error MSB4057: The target 'DevBridgeInjectedTarget' does not exist in the project.",
                diagnosticSignature = "MSB4057",
                orchestrator = "DevBridge2",
                failureSurface = "project-build",
                likelyOwner = "DevBridge2",
                ownershipConfidence = "high",
                ownershipBasis = "native build passed while the DevBridge-controlled build failed",
                exitCode = 1
            });

        AgentObservabilityIssueTriage triage = BuildTriage(issue, bundle);
        Equal("DevBridge2", triage.ProbableOwner.Owner);
        Equal("high", triage.ProbableOwner.Confidence);
        True(triage.ProbableOwner.Reason.Contains("native build passed", StringComparison.OrdinalIgnoreCase),
            "tool ownership must retain the discriminator basis");
    }

    public static void MissingBuildCauseRemainsUnproven()
    {
        (AgentIssue issue, AgentDiagnosticBundle bundle) = CreateBuildFailure(
            new
            {
                operationKey = "build:failure",
                errorCode = "DEVELOPMENT_BUILD_FAILED",
                orchestrator = "DevBridge2",
                failureSurface = "project-build",
                exitCode = 1
            });

        AgentObservabilityIssueTriage triage = BuildTriage(issue, bundle);
        Equal("Unknown", triage.ProbableOwner.Owner);
        Equal("unproven", triage.ProbableOwner.Confidence);
        True(!triage.EvidenceComplete, "missing causal output must make evidence incomplete");
        True(triage.MissingEvidence.Contains("build.causalDiagnostic"),
            "missing causal output must be named");
    }

    public static void CausalDiagnosticSurvivesBoundedHandoffAndRawEvidenceRetrieval()
    {
        string fullLog = new string('x', 800) + "\nerror CS0165: Use of unassigned local variable 'value'.\n" + new string('y', 800);
        using var store = new AgentObservabilityStore();
        AgentDiagnosticEvidenceReference raw = store.PersistEvidence(
            "devbridge.build.raw-stdout",
            fullLog)!;
        (AgentIssue issue, AgentDiagnosticBundle bundle) = CreateBuildFailure(
            new
            {
                operationKey = "build:failure",
                causalDiagnostic = "error CS0165: Use of unassigned local variable 'value'.",
                diagnosticSignature = "CS0165",
                output = new string('p', 9000),
                outputTruncated = true,
                causalDiagnosticTruncated = false,
                rawStdoutEvidenceId = raw.Id,
                likelyOwner = "project",
                ownershipConfidence = "high",
                ownershipBasis = "native build failed with the same causal diagnostic",
                buildDiscrimination = new
                {
                    likelyOwner = "project",
                    ownershipConfidence = "high",
                    ownershipBasis = "native build failed with the same causal diagnostic"
                },
                exitCode = 1
            },
            store);

        AgentObservabilityIssueTriage triage = BuildTriage(issue, bundle);
        AgentObservabilityChatPacket handoff = AgentObservabilityIssueTriageBuilder.FormatChatPacket(
            [(issue, triage, bundle)]);
        True(handoff.Text.Contains("CS0165", StringComparison.Ordinal),
            "causal compiler diagnostics must precede bounded ancillary output");
        JsonElement discrimination = bundle.BuildEvidence.First(value => value.Discrimination is not null).Discrimination!.Value;
        Equal("project", discrimination.GetProperty("likelyOwner").GetString());
        True(handoff.Text.Contains(raw.Id, StringComparison.Ordinal),
            "handoff must reference durable raw build evidence");
        True(handoff.Text.Length <= AgentObservabilityIssueTriageBuilder.MaximumChatPacketCharacters,
            "diagnostic handoff must remain bounded");
        True(handoff.Completeness.IsComplete, "complete causal evidence must remain complete");
        True(store.GetEvidence(raw.Id)?.Content == fullLog, "raw build evidence must remain retrievable");
    }

    public static void TruncatedCauseCannotBeComplete()
    {
        (AgentIssue issue, AgentDiagnosticBundle bundle) = CreateBuildFailure(
            new
            {
                operationKey = "build:failure",
                output = "build output prefix",
                outputTruncated = true,
                diagnosticOutputTruncated = true,
                exitCode = 1
            });
        AgentObservabilityIssueTriage triage = BuildTriage(issue, bundle);
        True(!triage.EvidenceComplete, "truncated causal output cannot be complete");
        True(triage.MissingEvidence.Contains("build.causalDiagnostic"),
            "truncated cause must identify the missing causal diagnostic");
    }

    public static void MissingManifestIsSafelyRepairedAndDoctorRetries()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-manifest-repair-" + Guid.NewGuid().ToString("N"));
        string catalog = Path.Combine(root, "TestCatalog", "rimtest.catalog.json");
        string repositoryCatalog = Path.Combine(
            Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.Parent!.Parent!.FullName,
            "TestCatalog",
            "rimtest.catalog.json");
        string oldDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.Copy(repositoryCatalog, catalog);
            Environment.CurrentDirectory = root;
            using var output = new StringWriter();
            using var errors = new StringWriter();
            string devBridgeRoot = Path.GetFullPath(Path.Combine(oldDirectory, "..", "DevBridge2"));
            var transport = new DoctorTransport();
            int exitCode = CliApplication.RunAsync(
                    [
                        "doctor",
                        "--json",
                        "--catalog", catalog,
                        "--devbridge", Path.Combine(devBridgeRoot, "DevBridge.cmd"),
                        "--fallback-suite", "smoke",
                        "--devbridge-root", devBridgeRoot,
                        "--devbridge-project", "frontier",
                        "--rimcontext-root", oldDirectory,
                        "--rimcontext-store", Path.Combine(oldDirectory, ".rimctx", "index.sqlite"),
                        "--rimerror-log", Path.Combine(root, "rimerror.log"),
                        "--rimerror-store", Path.Combine(root, "rimerror-store.json"),
                    ],
                    output,
                    errors,
                    processTransport: transport)
                .GetAwaiter()
                .GetResult();
            Equal(0, exitCode);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement result = document.RootElement;
            Equal("ready", result.GetProperty("status").GetString());
            True(result.TryGetProperty("manifestRecovery", out JsonElement recovery) &&
                recovery.GetProperty("repaired").GetBoolean(),
                "doctor must report that safe manifest repair occurred");
            True(File.Exists(Path.Combine(root, ".rimdev", "stack.json")),
                "safe doctor repair must create the missing manifest");
            True(transport.DoctorCalls == 1 && transport.ProjectCalls == 1,
                "doctor must retry its blocked operation after repair");
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public static void UnsafeManifestRepairDoesNotMutateState()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-manifest-unsafe-" + Guid.NewGuid().ToString("N"));
        string oldDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".rimdev"), "deliberate file");
            Environment.CurrentDirectory = root;
            using var output = new StringWriter();
            using var errors = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    ["doctor", "--json"],
                    output,
                    errors,
                    processTransport: new DoctorTransport())
                .GetAwaiter()
                .GetResult();
            Equal(3, exitCode);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            JsonElement result = document.RootElement;
            Equal("STACK_MANIFEST_AUTO_REPAIR_UNSAFE", result.GetProperty("code").GetString());
            Equal("required", result.GetProperty("blockingState").GetString());
            True(File.ReadAllText(Path.Combine(root, ".rimdev")) == "deliberate file",
                "unsafe reconstruction must not overwrite deliberate user state");
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class DoctorTransport : IDevBridgeProcessTransport
    {
        public int DoctorCalls { get; private set; }
        public int ProjectCalls { get; private set; }

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("doctor", StringComparer.Ordinal))
            {
                DoctorCalls++;
                return Task.FromResult(new DevBridgeProcessResult(
                    0,
                    "{\"success\":true,\"healthy\":true,\"rimBridge\":{\"lifecycleState\":\"ACTIVE\"}}",
                    null));
            }

            ProjectCalls++;
            return Task.FromResult(new DevBridgeProcessResult(
                0,
                "{\"success\":true,\"projectResolution\":{\"canonicalProjects\":[\"frontier\"]}}",
                null));
        }
    }
    private static (AgentIssue Issue, AgentDiagnosticBundle Bundle) CreateBuildFailure(
        object diagnostics,
        AgentObservabilityStore? existingStore = null)
    {
        AgentObservabilityStore store = existingStore ?? new AgentObservabilityStore();
        AgentObservabilityRun run = new("run-failure-handling", store, new NoopAgentObservabilityTelemetry());
        AgentObservabilitySession agent = run.CreateAgent("mod.failure", "Failure");
        agent.Start();
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildStarted,
            "DevBridge-controlled build started.",
            new { operationKey = "build:failure", command = "dotnet build" });
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildDiagnostics,
            "DevBridge returned build diagnostics.",
            diagnostics);
        agent.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildFailed,
            "DevBridge-controlled build failed.",
            new
            {
                operationKey = "build:failure",
                command = "dotnet build",
                errorCode = "DEVELOPMENT_BUILD_FAILED",
                exitCode = 1
            });
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single(
            value => value.Category == AgentIssueCategory.Error);
        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([issue.Id]);
        agent.Dispose();
        run.Dispose();
        if (existingStore is null)
        {
            store.Dispose();
        }
        return (issue, bundle);
    }

    private static AgentObservabilityIssueTriage BuildTriage(
        AgentIssue issue,
        AgentDiagnosticBundle bundle) =>
        AgentObservabilityIssueTriageBuilder.Build(
            issue,
            null,
            bundle.SupportingEvents,
            bundle,
            true,
            null);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
