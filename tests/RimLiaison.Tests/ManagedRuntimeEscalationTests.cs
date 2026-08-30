using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Recovery;

namespace RimLiaison.Tests;

internal static class ManagedRuntimeEscalationTests
{
    public static void ReconnectRestoresServiceWithoutReset()
    {
        ScriptedTransport transport = new(
            _ => Process("{\"status\":\"ready\",\"healthy\":true,\"generation\":4}"));
        DevBridgeCapabilityRecoveryResult result = Recover(transport, "RIMBRIDGE_NOT_READY");

        Assert(result.Succeeded, "reconnect should recover a transient readiness failure");
        AssertEqual("RECONCILE", result.HighestLevel);
        AssertEqual(false, result.RimWorldRestarted);
        AssertEqual(1, transport.Requests.Count);
    }

    public static void CoordinatorRecycleRestoresService()
    {
        int calls = 0;
        ScriptedTransport transport = new(request =>
        {
            calls++;
            return calls switch
            {
                1 => Process("", exitCode: 2),
                2 => Process("{\"success\":true,\"state\":\"Responsive\"}"),
                3 => Process("{\"healthy\":true,\"state\":\"READY\",\"generation\":8,\"coordinatorCount\":1,\"activeLeases\":0}"),
                _ => Process("{\"status\":\"ready\",\"healthy\":true,\"generation\":8,\"coordinatorCount\":1,\"activeLeases\":0}")
            };
        });
        DevBridgeCapabilityRecoveryResult result = Recover(transport);

        Assert(result.Succeeded, "coordinator recycle should recover a no-response failure");
        AssertEqual("COORDINATOR_RECYCLE", result.HighestLevel);
        AssertEqual(false, result.RimWorldRestarted);
        AssertEqual(4, transport.Requests.Count);
        Assert(transport.Requests.Count(request => request.Arguments.Contains("restart")) == 0,
            "coordinator recovery must not launch a second runtime");
    }

    public static void FullResetRestoresServiceAfterCoordinatorFailure()
    {
        int calls = 0;
        ScriptedTransport transport = new(request =>
        {
            calls++;
            return calls switch
            {
                1 => Process("", exitCode: 2),
                2 => Process("{\"success\":false,\"errorCode\":\"DEVBRIDGE_COORDINATOR_RECOVERY_FAILED\"}", 2),
                3 => Process("{\"success\":false,\"errorCode\":\"DEVBRIDGE_COORDINATOR_UNRESPONSIVE\"}", 2),
                4 => Process("{\"success\":true,\"state\":\"Responsive\"}"),
                5 => Process("{\"success\":true,\"state\":\"READY\",\"generation\":9}"),
                6 => Process("{\"status\":\"READY\",\"healthy\":true,\"generation\":9,\"coordinatorCount\":1,\"activeLeases\":0}"),
                _ => Process("{\"status\":\"READY\",\"healthy\":true,\"generation\":9,\"coordinatorCount\":1,\"activeLeases\":0}")
            };
        });
        DevBridgeCapabilityRecoveryResult result = Recover(transport);

        Assert(result.Succeeded, "full managed reset should recover after coordinator reset failure");
        AssertEqual("FULL_RUNTIME_RESET", result.HighestLevel);
        AssertEqual(true, result.RimWorldRestarted);
        AssertEqual(8, transport.Requests.Count);
        AssertEqual(1, transport.Requests.Count(request => request.Arguments.Contains("restart")));
        AssertEqual("READY", result.FinalState);
        Assert(result.Actions!.Any(action => action.Action == "shutdown-managed-runtime"),
            "full reset must preserve the managed shutdown action");
    }

    public static void UnsafeCheckpointDoesNotEscalate()
    {
        ScriptedTransport transport = new(
            _ => Process("", exitCode: 2));
        DevBridgeCapabilityRecoveryResult result = Recover(
            transport,
            checkpoint: ProductionCheckpoint.AssertionsStarted);

        Assert(!result.Succeeded, "unsafe stateful checkpoint must remain failed");
        AssertEqual("RECONCILE", result.HighestLevel);
        AssertEqual(1, transport.Requests.Count);
    }

    public static void AmbiguousPromotedIdentityFailsClosed()
    {
        int calls = 0;
        ScriptedTransport transport = new(_ =>
        {
            calls++;
            return calls == 1
                ? Process("", exitCode: 2)
                : Process("{\"success\":false,\"errorCode\":\"PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH\",\"error\":\"ambiguous\"}", 2);
        });
        DevBridgeCapabilityRecoveryResult result = Recover(transport);

        Assert(!result.Succeeded, "ambiguous promoted identity must fail closed");
        AssertEqual("COORDINATOR_RECYCLE", result.HighestLevel);
        AssertEqual("PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH", result.ErrorCode);
        AssertEqual(2, transport.Requests.Count);
    }

    public static void RecoveryEvidenceIsStructuredAndBounded()
    {
        ScriptedTransport transport = new(
            _ => Process("{\"status\":\"ready\",\"healthy\":true,\"generation\":4}"));
        DevBridgeCapabilityRecoveryResult result = Recover(transport, "DEVBRIDGE_NO_STRUCTURED_RESPONSE");
        string json = JsonSerializer.Serialize(result);

        Assert(json.Length < 16 * 1024, "recovery evidence must remain bounded");
        AssertEqual("DEVBRIDGE_NO_STRUCTURED_RESPONSE", result.Trigger);
        Assert(result.ElapsedRecoveryMilliseconds >= 0, "recovery timing must be recorded");
    }
    public static void ArtifactTransactionUsesSharedRecoveryOnce()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-escalation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string changedPath = "Changed.cs";
        File.WriteAllText(Path.Combine(directory, changedPath), "class Changed {}");
        string sourceFingerprint = Fingerprint(directory, changedPath);
        int developmentCalls = 0;
        int transportCall = 0;
        ScriptedTransport transport = new(request =>
        {
            int call = transportCall++;
            return call switch
            {
                0 => Process("", 2),
                1 => Process("{\"success\":true,\"state\":\"Responsive\"}"),
                2 => Process("{\"status\":\"READY\",\"healthy\":true,\"generation\":8,\"coordinatorCount\":1,\"activeLeases\":0}"),
                _ => Process("{\"status\":\"READY\",\"healthy\":true,\"generation\":8,\"coordinatorCount\":1,\"activeLeases\":0}")
            };
        });
        var development = new ScriptedDevelopmentAdapter(() =>
        {
            developmentCalls++;
            return developmentCalls == 1
                ? new(
                    "fixture",
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_NO_STRUCTURED_RESPONSE"),
                    false,
                    null,
                    "wf-affected",
                    7,
                    null,
                    null)
                : new(
                    "fixture",
                    new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                    true,
                    "tx-affected",
                    "wf-affected",
                    8,
                    null,
                    new DevBridgeArtifactFreshness(
                        sourceFingerprint,
                        new string('a', 64),
                        new string('a', 64),
                        "unchanged",
                        8,
                        8,
                        8,
                        true,
                        "proof",
                        "tx-affected",
                        "wf-affected",
                        "lease-affected"));
        });

        try
        {
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(
                        development,
                        recoveryTransport: transport,
                        recoveryOptions: new DevBridgeAdapterOptions
                        {
                            CommandPath = "DevBridge.cmd",
                            RootPath = directory
                        })
                    .PrepareAsync(new(
                        "fixture",
                        directory,
                        [changedPath],
                        sourceFingerprint,
                        "wf-affected",
                        TestRecipe: "recipe"))
                    .GetAwaiter()
                    .GetResult();

            Assert(
                result.Success,
                $"affected artifact transaction should retry after managed recovery: {result.Status.ErrorCode} {result.Status.Error}");
            AssertEqual(2, developmentCalls);
            AssertEqual(4, transport.Requests.Count);
            AssertEqual("managed-runtime-reset", result.RecoveryEvents!.Single().Component);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    private static string Fingerprint(string directory, string path)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(path));
        hash.AppendData(Encoding.UTF8.GetBytes("\0file\0"));
        hash.AppendData(File.ReadAllBytes(Path.Combine(directory, path)));
        hash.AppendData(Encoding.UTF8.GetBytes("\0end\0"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static DevBridgeCapabilityRecoveryResult Recover(
        ScriptedTransport transport,
        string trigger = "DEVBRIDGE_NO_STRUCTURED_RESPONSE",
        ProductionCheckpoint checkpoint = ProductionCheckpoint.PreMutation) =>
        DevBridgeCapabilityRecovery.RecoverAsync(
                transport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                },
                "wf-escalation",
                triggerCode: trigger,
                checkpoint: checkpoint)
            .GetAwaiter()
            .GetResult();

    private static DevBridgeProcessResult Process(string stdout, int exitCode = 0) =>
        new(exitCode, stdout, string.Empty);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
    {
        private readonly Func<DevBridgeModDevelopmentResult> factory;

        public ScriptedDevelopmentAdapter(Func<DevBridgeModDevelopmentResult> factory) =>
            this.factory = factory;

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory());
    }

    private sealed class ScriptedTransport : IDevBridgeProcessTransport
    {
        private readonly Func<DevBridgeProcessRequest, DevBridgeProcessResult> handler;
        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public ScriptedTransport(Func<DevBridgeProcessRequest, DevBridgeProcessResult> handler) =>
            this.handler = handler;

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }
}
