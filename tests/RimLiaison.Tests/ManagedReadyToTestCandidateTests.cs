using System.Text.Json;
using RimLiaison.DevBridge;
using RimLiaison.Stack;

namespace RimLiaison.Tests;

internal static class ManagedReadyToTestCandidateTests
{
    public static void MutatedCandidateRejectedBeforeLease()
    {
        using Fixture fixture = new();
        string manifest = fixture.CreateCandidate();
        File.AppendAllText(Path.Combine(fixture.RuntimeRoot, "About", "About.xml"), "mutated");
        var transport = new RecordingTransport((_, _) => throw new InvalidOperationException("runtime was touched"));
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifest).GetAwaiter().GetResult();
        Assert(result.Outcome == "CANDIDATE_INPUT_FAILURE" && result.ErrorCode == "RIMTEST_CANDIDATE_PACKAGE_MUTATED" && transport.Requests.Count == 0,
            "mutated candidate was not rejected before runtime mutation");
    }

    public static void CandidateManagedExecutionNeverBuildsSource()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        StackManifestResolution stack = StackManifestResolver.Discover(fixture.RepositoryRoot);
        Assert(stack.Found, $"fixture stack manifest not found: {stack.Error}");
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        DevBridgeModDevelopmentResult result = fixture.Transaction(transport).RunAsync(
                "fixture", fixture.RepositoryRoot, fixture.SourceFingerprint, "candidate-test",
                new DevBridgeModDevelopmentExecutionContext(
                    Candidate: fixture.Manifest!, CandidateManifestPath: manifestPath))
            .GetAwaiter().GetResult();
        Assert(result.Success == true && transport.Requests.All(request => request.FileName != "dotnet"),
            $"managed candidate execution rebuilt source: {result.Status.ErrorCode} {result.Status.Error}; requests={string.Join("|", transport.Requests.Select(request => string.Join(",", request.Arguments)))}");
    }


    public static void CandidatePackageHashPreservedThroughDeploymentEvidence()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        DevBridgeModDevelopmentResult result = fixture.Transaction(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(
                "fixture", fixture.RepositoryRoot, fixture.SourceFingerprint, "candidate-test",
                new DevBridgeModDevelopmentExecutionContext(
                    Candidate: fixture.Manifest!, CandidateManifestPath: manifestPath))
            .GetAwaiter().GetResult();
        Assert(result.Freshness?.BuiltPackageSha256 == fixture.Manifest!.PackageSha256 &&
            result.Freshness.DeployedPackageSha256 == fixture.Manifest.PackageSha256,
            "candidate package identity was not preserved in deployment evidence");
    }

    public static void ForbiddenCandidateContentRejected()
    {
        using Fixture fixture = new();
        string manifest = fixture.CreateCandidate();
        Directory.CreateDirectory(Path.Combine(fixture.RuntimeRoot, "bin"));
        File.WriteAllText(Path.Combine(fixture.RuntimeRoot, "bin", "bad.dll"), "bad");
        var transport = new RecordingTransport((_, _) => throw new InvalidOperationException("runtime was touched"));
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifest).GetAwaiter().GetResult();
        Assert(result.ErrorCode == "RIMTEST_CANDIDATE_FORBIDDEN_PAYLOAD" && transport.Requests.Count == 0,
            "forbidden staged content entered the managed path");
    }

    public static void ExistingDependencyResolverPathIsReused()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        fixture.Transaction(transport).RunAsync(
                "fixture", fixture.RepositoryRoot, fixture.SourceFingerprint, "candidate-test",
                new DevBridgeModDevelopmentExecutionContext(
                    Candidate: fixture.Manifest!, CandidateManifestPath: manifestPath))
            .GetAwaiter().GetResult();
        Assert(transport.Requests.Any(request => request.Arguments.Contains("show") && request.Arguments.Contains("recipe")) &&
            transport.Requests.Any(request => request.Arguments.Contains("plan") && request.Arguments.Contains("recipe")) &&
            transport.Requests.Any(request => request.Arguments.Contains("register") && request.Arguments.Contains("project")),
            "managed candidate bypassed the existing recipe/dependency resolver path");
    }

    public static void ModsConfigEvidenceRequiresTargetDependencyProfile()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.TargetConfigured == true && result.ResolvedDependencies.Contains("Core") &&
            result.ModsConfigProfileFingerprint == "profile-fixture",
            "candidate evidence did not prove the intended target/dependency profile");
    }

    public static void ReadyWithoutTargetRecipeCannotPass()
    {
        using Fixture fixture = new() { TargetRecipePasses = false };
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.Outcome == "MOD_FAILURE" && result.TargetLoaded == false,
            $"generic READY state incorrectly counted as target success: success={result.Success}, outcome={result.Outcome}, targetLoaded={result.TargetLoaded}, error={result.ErrorCode} {result.Error}");
    }

    public static void ManagedCandidateCleanupRunsAfterSuccess()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.Cleanup.LeaseReleased && result.Cleanup.RuntimeRestored &&
            transport.Requests.Any(request => request.Arguments.Contains("end")) &&
            transport.Requests.Any(request => request.Arguments.Contains("release") && request.Arguments.Contains("project")),
            "managed candidate cleanup was not proven after success");
    }

    public static void CandidateUsesNoExternalDevBridgeAuthority()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(transport.Requests.All(request => Path.GetFileName(request.FileName) == "DevBridge.cmd") &&
            transport.Requests.All(request => !request.WorkingDirectory.Contains("DevBridge2", StringComparison.OrdinalIgnoreCase)),
            "managed candidate execution used an external DevBridge2 authority: " +
            string.Join(" | ", transport.Requests.Select(request => request.FileName + "@" + request.WorkingDirectory)));
    }
    public static void CandidateRequestsRequiredRimBridgeMode()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.RimBridgeMode == "required" &&
            transport.Requests.Count > 0 &&
            transport.Requests.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue("DEVBRIDGE_RIMBRIDGE_MODE", out string? mode) &&
                mode == "required"),
            "candidate execution did not carry the required RimBridge mode in every runtime request");
    }

    public static void AlreadyRequiredRimBridgeCoordinatorIsReused()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && fixture.CoordinatorShutdownCalls == 0 &&
            !transport.Requests.Any(request => request.Arguments.Contains("coordinator") &&
                request.Arguments.Contains("shutdown")),
            "an already-required coordinator was unnecessarily recycled");
    }

    public static void OffRimBridgeCoordinatorTransitionsWithoutStoppingRimWorld()
    {
        using Fixture fixture = new() { InitialRimBridgeMode = "off" };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        int shutdown = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("coordinator") && request.Arguments.Contains("shutdown"));
        int begin = transport.Requests.FindIndex(request => request.Arguments.Contains("begin"));
        Assert(result.Success && fixture.CoordinatorShutdownCalls == 1 && shutdown >= 0 && begin > shutdown &&
            !transport.Requests.Take(begin).Any(request => request.Arguments.Contains("restart")),
            "off-mode transition did not use one safe coordinator shutdown before the lease");
    }

    public static void ActiveLeasePreventsUnsafeRimBridgeTransition()
    {
        using Fixture fixture = new()
        {
            InitialRimBridgeMode = "off",
            ActiveLeaseId = "active-lease"
        };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.ErrorCode == "RIMBRIDGE_MODE_TRANSITION_BUSY" &&
            fixture.CoordinatorShutdownCalls == 0 &&
            !transport.Requests.Any(request => request.Arguments.Contains("begin")),
            "an active lease was force-recycled while changing RimBridge mode");
    }

    public static void ConcurrentRequiredCandidatesShareOneCoordinatorTransition()
    {
        using Fixture fixture = new() { InitialRimBridgeMode = "off" };
        string firstManifest = fixture.CreateCandidate();
        string secondManifest = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateConsumer consumer = fixture.Consumer(transport);
        Task.WhenAll(
                consumer.RunAsync(firstManifest),
                consumer.RunAsync(secondManifest))
            .GetAwaiter().GetResult();
        Assert(fixture.CoordinatorShutdownCalls == 1 &&
            transport.Requests.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue("DEVBRIDGE_RIMBRIDGE_MODE", out string? mode) &&
                mode == "required"),
            "concurrent candidates created competing coordinator mode transitions");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string RepositoryRoot { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-managed-candidate-" + Guid.NewGuid().ToString("N"));
        public string CandidateBase { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-managed-output-" + Guid.NewGuid().ToString("N"));
        public string RuntimeRoot { get; private set; } = string.Empty;
        public string ManagedModsRoot { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-managed-mods-" + Guid.NewGuid().ToString("N"));
        public string SourceFingerprint { get; } = new('a', 64);
        public ReadyToTestCandidateManifest? Manifest { get; private set; }
        public bool TargetRecipePasses { get; init; } = true;
        public string InitialRimBridgeMode { get; init; } = "required";
        public string? ActiveLeaseId { get; init; }
        public int CoordinatorShutdownCalls { get; private set; }
        private int statusCalls;
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".rimdev", "recipes"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "Source"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "About"));
            File.WriteAllText(Path.Combine(RepositoryRoot, "About", "About.xml"), "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(RepositoryRoot, "Source", "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "recipes", "fixture.json"), "{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"description\":\"fixture\",\"projects\":[\"fixture\"]}");
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "stack.json"), """
                {
                  "schemaVersion":"rimdev-stack/v1","project":"Fixture","devBridgeProject":"fixture",
                  "catalog":"catalog.json","rimBridge":"disabled",
                  "workload":"production","projectType":"rimworld-content-mod","packageId":"lan.fixture",
                  "sourceProject":"Source/Fixture.csproj","configuration":"Release","expectedAssembly":"Fixture.dll",
                  "deploymentTarget":"1.6/Assemblies/Fixture.dll","testRecipe":"fixture",
                  "runtimePackage":{"sourceRoot":".","include":["About/**","1.*/**"],"exclude":[".rimdev/**","Source/**","bin/**","obj/**"]},"dependencies":["Core"]
                }
                """);
            File.WriteAllText(Path.Combine(RepositoryRoot, "catalog.json"), "{}");
            File.WriteAllText(Path.Combine(RepositoryRoot, "DevBridge.cmd"), string.Empty);
        }

        public string CreateCandidate()
        {
            string candidate = Path.Combine(CandidateBase, "fixture", "candidate-" + Guid.NewGuid().ToString("N"));
            RuntimeRoot = Path.Combine(candidate, "runtime");
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "About"));
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "1.6", "Assemblies"));
            File.WriteAllText(Path.Combine(RuntimeRoot, "About", "About.xml"), "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(RuntimeRoot, "1.6", "Assemblies", "Fixture.dll"), "candidate-assembly");
            var entries = Directory.EnumerateFiles(RuntimeRoot, "*", SearchOption.AllDirectories)
                .Select(file => new ReadyToTestCandidatePackageEntry(
                    file,
                    Path.GetRelativePath(RuntimeRoot, file).Replace('\\', '/'),
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
                    new FileInfo(file).Length))
                .ToArray();
            Manifest = new ReadyToTestCandidateManifest
            {
                CandidateId = Path.GetFileName(candidate), Project = "fixture", CanonicalProjectId = "fixture",
                PackageId = "lan.fixture", SourceRepositoryRoot = RepositoryRoot, SourceFingerprint = SourceFingerprint,
                StagedRuntimeRoot = RuntimeRoot, PackageSha256 = ReadyToTestCandidateVerifier.ComputePackageHash(entries),
                RequiredRuntimeDependencies = ["Core"], BuildEvidenceIdentity = "candidate-build:test",
                BuildStatus = "PASS", StaticValidationIdentity = "runtime-package:test", StaticValidationStatus = "PASS",
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            string manifestPath = Path.Combine(candidate, "candidate.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true }));
            return manifestPath;
        }

        public ManagedReadyToTestCandidateConsumer Consumer(RecordingTransport transport) =>
            new(transport, new ManagedReadyToTestCandidateOptions
            {
                BridgeOptions = new DevBridgeAdapterOptions
                {
                    CommandPath = Path.Combine(RepositoryRoot, "DevBridge.cmd"), RootPath = RepositoryRoot,
                    SourceRootPath = RepositoryRoot
                },
                DeploymentRoot = ManagedModsRoot, WorkflowId = "candidate-test",
                Timeout = TimeSpan.FromSeconds(5), MaxStdoutBytes = 65536, MaxStderrBytes = 4096
            });
        public InternalDevelopmentTransactionService Transaction(RecordingTransport transport) =>
            new(transport, new DevBridgeModDevelopmentAdapterOptions
            {
                RootPath = RepositoryRoot, DeploymentRoot = ManagedModsRoot,
                Timeout = TimeSpan.FromSeconds(5), MaxStdoutBytes = 65536, MaxStderrBytes = 4096
            });

        public DevBridgeProcessResult RuntimeHandler(DevBridgeProcessRequest request, CancellationToken _)
        {
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("show"))
                return Result("{\"schemaVersion\":\"devbridge-test-recipe-show/v1\",\"recipe\":{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"projects\":[\"fixture\"]},\"errorCode\":null,\"error\":null}");
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("plan"))
                return Result("{\"schemaVersion\":\"devbridge-test-recipe-plan/v1\",\"recipe\":\"fixture\",\"alreadySatisfied\":false,\"estimatedRimWorldLaunches\":1,\"nextAction\":\"run\",\"blockedBy\":[],\"steps\":[]}");
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("run"))
            {
                string passed = TargetRecipePasses ? "true" : "false";
                string error = TargetRecipePasses ? "null" : "\"MOD_DEF_ERROR\"";
                return Result($"{{\"schemaVersion\":\"devbridge-test-recipe-run/v1\",\"recipe\":\"fixture\",\"success\":{passed},\"errorCode\":{error},\"error\":{error},\"generation\":2,\"runId\":\"run-1\",\"leaseId\":null,\"evidence\":\"target-load\",\"evidenceId\":\"evidence-1\",\"failureFingerprint\":null,\"finalNextAction\":null,\"restartRequired\":false,\"launchesConsumed\":0,\"operations\":[],\"workflowId\":\"candidate-test\"}}");
            }
            if (request.Arguments.Contains("status"))
            {
                string root = RepositoryRoot.Replace("\\", "\\\\");
                string mode = CoordinatorShutdownCalls > 0 ? "required" : InitialRimBridgeMode;
                string lease = ActiveLeaseId is null ? "null" : $"\"{ActiveLeaseId}\"";
                if (request.OperationKey == "candidate-runtime-status")
                    return Result($"{{\"success\":true,\"generation\":2,\"profileFingerprint\":\"profile-fixture\",\"resolvedMods\":[\"lan.fixture\",\"Core\"],\"leaseId\":{lease},\"rimBridge\":{{\"ConfiguredMode\":\"{mode}\",\"PackageId\":\"brrainz.rimbridgeserver\",\"Version\":\"2.1.0\",\"LifecycleState\":\"READY\"}}}}");
                statusCalls++;
                return statusCalls == 1
                    ? Result($"{{\"success\":true,\"generation\":1,\"rimworldRoot\":\"{root}\",\"rimBridge\":{{\"ConfiguredMode\":\"{mode}\",\"PackageId\":\"brrainz.rimbridgeserver\",\"Version\":\"2.1.0\",\"LifecycleState\":\"READY\"}}}}")
                    : Result($"{{\"success\":true,\"generation\":2,\"profileFingerprint\":\"profile-fixture\",\"resolvedMods\":[\"lan.fixture\",\"Core\"],\"rimBridge\":{{\"ConfiguredMode\":\"{mode}\",\"PackageId\":\"brrainz.rimbridgeserver\",\"Version\":\"2.1.0\",\"LifecycleState\":\"READY\"}}}}");
            }
            if (request.Arguments.Contains("coordinator") && request.Arguments.Contains("shutdown"))
            {
                CoordinatorShutdownCalls++;
                return Result("{\"success\":true,\"exitCode\":0,\"state\":\"STOPPED\"}");
            }
            if (request.Arguments.Contains("wait-ready")) return Result("{\"success\":true,\"state\":\"READY\",\"generation\":2}");
            if (request.Arguments.Contains("stop")) return Result("{\"success\":true,\"gameState\":\"STOPPED\"}");
            if (request.Arguments.Contains("begin")) return Result("{\"success\":true,\"exitCode\":0,\"generation\":1,\"leaseId\":\"lease-00000000000000000000000000000001\"}");
            if (request.Arguments.Contains("end")) return Result("{\"success\":true,\"exitCode\":0,\"generation\":2,\"leaseId\":\"lease-00000000000000000000000000000001\"}");
            return Result("{\"success\":true}");
        }

        public void Dispose()
        {
            Delete(RepositoryRoot); Delete(CandidateBase); Delete(ManagedModsRoot);
        }

        private static DevBridgeProcessResult Result(string stdout) => new(0, stdout, string.Empty);
        private static void Delete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class RecordingTransport : IDevBridgeProcessTransport
    {
        private readonly Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler;
        private readonly object gate = new();
        public List<DevBridgeProcessRequest> Requests { get; } = [];
        public RecordingTransport(Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler) => this.handler = handler;
        public Task<DevBridgeProcessResult> ExecuteAsync(DevBridgeProcessRequest request, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Requests.Add(request);
                return Task.FromResult(handler(request, cancellationToken));
            }
        }
    }
}
