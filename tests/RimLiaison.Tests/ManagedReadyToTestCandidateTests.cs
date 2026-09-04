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

    public static void ConfiguredTargetWithoutLiveRuntimeEvidenceCannotPass()
    {
        using Fixture fixture = new() { GenericTargetLoaded = false };
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.TargetConfigured == true &&
            result.TargetLoaded == false && result.DefsValid == false &&
            result.ErrorCode == "CANDIDATE_TARGET_NOT_PROVEN",
            $"configured target was accepted without live load evidence: success={result.Success}, error={result.ErrorCode}");
    }

    public static void CandidateDoesNotSynthesizeCustomSuite()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.RecipeEvidence is null &&
            !transport.Requests.Any(request =>
                request.Arguments.Any(argument => argument.Contains("run_suite", StringComparison.OrdinalIgnoreCase))),
            "managed candidate load validation synthesized or executed a project-specific suite");
    }

    public static void AttributableTargetLoadErrorPreventsPass()
    {
        using Fixture fixture = new() { GenericTargetLoadError = true };
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.TargetLoaded == true && result.DefsValid == false &&
            result.AttributableTargetErrors.Any(error => error.Contains("Def load failed", StringComparison.Ordinal)),
            "an attributable target load error was not retained as a managed candidate failure");
    }

    public static void GenericAssemblyEvidenceIsRetained()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        GenericCandidateLoadEvidence evidence = (GenericCandidateLoadEvidence)result.ManagedLoadEvidence!;
        Assert(result.Success && evidence.AssemblyLoadState == "loaded-by-rimbridge-list-mods" &&
            result.TargetAssemblyLoadState == evidence.AssemblyLoadState,
            "generic assembly-load evidence was not retained");
    }

    public static void CandidateGenericEvidenceUsesExplicitActiveOnlyRequest()
    {
        using Fixture fixture = new();
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        DevBridgeProcessRequest request = transport.Requests.Single(request =>
            request.OperationKey == "candidate-generic-load-evidence");
        Assert(result.Success &&
            request.Arguments.Contains("{\"includeInactive\":false}") &&
            !request.Arguments.Contains("{}"),
            "candidate generic load evidence did not use the explicit active-only request");
    }

    public static void OutputTooLargeFallbackCannotProveTargetLoad()
    {
        using Fixture fixture = new() { GenericFallback = true };
        string manifestPath = fixture.CreateCandidate();
        ManagedReadyToTestCandidateResult result = fixture.Consumer(
                new RecordingTransport(fixture.RuntimeHandler)).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.TargetLoaded == false &&
            result.ErrorCode == "OUTPUT_TOO_LARGE",
            "a summarized coordinator fallback established generic target load");
    }

    public static void GenericEvidenceRequiresLoadedSessionIdentity()
    {
        static ManagedReadyToTestCandidateResult Run(bool loadedInSession, bool matchesLoadedSession)
        {
            using Fixture fixture = new()
            {
                GenericLoadedInSession = loadedInSession,
                GenericMatchesLoadedSession = matchesLoadedSession
            };
            return fixture.Consumer(new RecordingTransport(fixture.RuntimeHandler))
                .RunAsync(fixture.CreateCandidate()).GetAwaiter().GetResult();
        }

        ManagedReadyToTestCandidateResult notLoaded = Run(false, true);
        ManagedReadyToTestCandidateResult mismatched = Run(true, false);
        ManagedReadyToTestCandidateResult loaded = Run(true, true);
        Assert(!notLoaded.Success && notLoaded.TargetLoaded == false &&
            !mismatched.Success && mismatched.TargetLoaded == false &&
            loaded.Success && loaded.TargetLoaded == true &&
            loaded.ManagedLoadEvidence?.MatchedMod?.Enabled == true &&
            loaded.ManagedLoadEvidence.MatchedMod.LoadedInSession == true &&
            loaded.ManagedLoadEvidence.MatchedMod.MatchesLoadedSession == true,
            "generic load evidence did not require the complete loaded-session identity");
    }
    public static void ExplicitRecipeMissingToolRemainsFailure()
    {
        using Fixture fixture = new();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        DevBridgeRecipeAdapter adapter = new(transport, new DevBridgeAdapterOptions
        {
            CommandPath = Path.Combine(fixture.RepositoryRoot, "DevBridge.cmd"),
            RootPath = fixture.RepositoryRoot,
            SourceRootPath = fixture.RepositoryRoot
        });
        DevBridgeRecipeRunResult result = adapter.RunAsync("fixture").GetAwaiter().GetResult();
        Assert(!result.Status.IsSuccess && result.Status.ErrorCode == "RIMBRIDGE_TOOL_NOT_FOUND",
            "an explicitly requested missing custom suite was not preserved as a tool-not-found failure");
    }



    public static void ManagedCandidateCleanupRunsAfterSuccess()
    {
        using Fixture fixture = new() { GenericBridgeExitCode = true };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.Cleanup.LeaseReleased && result.Cleanup.RuntimeRestored &&
            transport.Requests.Count(request => request.Arguments.Contains("end")) >= 2 &&
            transport.Requests.Any(request => request.Arguments.Contains("rimworld/list_mods") &&
                request.Arguments.Contains("--lease")) &&
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

    public static void StoppedCandidateStartsRequiredManagedRuntime()
    {
        using Fixture fixture = new() { InitialRuntimeStopped = true, InitialRimBridgeMode = "off" };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.RimBridgeMode == "required" &&
            fixture.CoordinatorShutdownCalls == 1 &&
            transport.Requests.Any(request => request.Arguments.Contains("begin")) &&
            transport.Requests.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue("DEVBRIDGE_RIMBRIDGE_MODE", out string? mode) &&
                mode == "required"),
            "a stopped managed runtime was rejected instead of launching with required RimBridge mode");
    }

    public static void StaleOwnedPidReconcilesBeforeCandidate()
    {
        using Fixture fixture = new()
        {
            InitialRuntimeStopped = true,
            InitialRimBridgeMode = "off",
            StaleOwnedPid = true
        };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(result.Success && result.ErrorCode is null &&
            fixture.CoordinatorShutdownCalls == 1 &&
            transport.Requests.Any(request => request.Arguments.Contains("begin")),
            "stale owned-process state was not reconciled to the stopped launch boundary");
    }


    public static void UnmanagedRimWorldStillFailsClosed()
    {
        using Fixture fixture = new() { InitialRuntimeStopped = true, UnmanagedRimWorld = true };
        string manifestPath = fixture.CreateCandidate();
        var transport = new RecordingTransport(fixture.RuntimeHandler);
        ManagedReadyToTestCandidateResult result = fixture.Consumer(transport).RunAsync(manifestPath).GetAwaiter().GetResult();
        Assert(!result.Success && result.ErrorCode == "PROCESS_OWNERSHIP_AMBIGUOUS" &&
            !transport.Requests.Any(request => request.Arguments.Contains("begin")),
            "an unmanaged RimWorld process was adopted or allowed through candidate preflight");
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
        public bool GenericTargetLoaded { get; init; } = true;
        public bool GenericTargetLoadError { get; init; }
        public bool GenericBridgeExitCode { get; init; }
        public bool GenericFallback { get; init; }
        public bool GenericEnabled { get; init; } = true;
        public bool GenericLoadedInSession { get; init; } = true;
        public bool GenericMatchesLoadedSession { get; init; } = true;
        public bool GenericAssemblyEvidence { get; init; } = true;
        public string InitialRimBridgeMode { get; init; } = "required";
        public bool InitialRuntimeStopped { get; init; }
        public bool StaleOwnedPid { get; init; }
        public bool UnmanagedRimWorld { get; init; }
        public string? ActiveLeaseId { get; init; }
        public int CoordinatorShutdownCalls { get; private set; }
        private int statusCalls;
        private int candidateStatusCalls;
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
            if (request.Arguments.Contains("bridge") && request.Arguments.Contains("call"))
            {
                if (GenericFallback)
                    return Result("{\"success\":false,\"errorCode\":\"OUTPUT_TOO_LARGE\",\"error\":\"The coordinator result exceeded the maximum payload length.\",\"payloadMetadata\":{\"summarized\":true,\"truncated\":true,\"fallback\":true}}");
                string packageId = GenericTargetLoaded ? "lan.fixture" : "lan.other";
                string assembly = GenericAssemblyEvidence
                    ? ",\"assemblies\":[{\"name\":\"Fixture.dll\",\"loaded\":true}]"
                    : string.Empty;
                string errors = GenericTargetLoadError
                    ? ",\"errors\":[{\"packageId\":\"lan.fixture\",\"message\":\"Def load failed\"}]"
                    : string.Empty;
                string record = "{\"packageId\":\"" + packageId +
                    "\",\"modId\":\"mod-config:" + packageId + ":fixture\"," +
                    "\"enabled\":" + GenericEnabled.ToString().ToLowerInvariant() +
                    ",\"loadedInSession\":" + GenericLoadedInSession.ToString().ToLowerInvariant() +
                    ",\"matchesLoadedSession\":" + GenericMatchesLoadedSession.ToString().ToLowerInvariant() +
                    ",\"activeLoadOrder\":0,\"loadedSessionOrder\":0" + assembly + errors + "}";
                return Result(
                    "{\"success\":true,\"rimBridgeRoute\":{\"success\":true,\"result\":{\"success\":true,\"mods\":[" +
                    record + "],\"operation\":{\"name\":\"list_mods\"}},\"provenance\":{\"generation\":2}}}",
                    GenericBridgeExitCode ? 4 : 0);
            }
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("show"))
                return Result("{\"schemaVersion\":\"devbridge-test-recipe-show/v1\",\"recipe\":{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"projects\":[\"fixture\"]},\"errorCode\":null,\"error\":null}");
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("plan"))
                return Result("{\"schemaVersion\":\"devbridge-test-recipe-plan/v1\",\"recipe\":\"fixture\",\"alreadySatisfied\":false,\"estimatedRimWorldLaunches\":1,\"nextAction\":\"run\",\"blockedBy\":[],\"steps\":[]}");
            if (request.Arguments.Contains("recipe") && request.Arguments.Contains("run"))
                return Result("{\"schemaVersion\":\"devbridge-test-recipe-run/v1\",\"recipe\":\"fixture\",\"success\":false,\"errorCode\":\"RIMBRIDGE_TOOL_NOT_FOUND\",\"error\":\"RimBridge did not find the requested tool.\",\"generation\":2,\"runId\":\"run-1\",\"leaseId\":null,\"evidence\":null,\"evidenceId\":null,\"failureFingerprint\":null,\"finalNextAction\":null,\"restartRequired\":false,\"launchesConsumed\":0,\"operations\":[],\"workflowId\":\"candidate-test\"}");
            if (request.Arguments.Contains("status"))
            {
                string root = RepositoryRoot.Replace("\\", "\\\\");
                string mode = CoordinatorShutdownCalls > 0 ? "required" : InitialRimBridgeMode;
                string lease = ActiveLeaseId is null ? "null" : $"\"{ActiveLeaseId}\"";
                if (request.OperationKey == "candidate-runtime-status")
                {
                    candidateStatusCalls++;
                    if (InitialRuntimeStopped && candidateStatusCalls == 1)
                    {
                        string stale = StaleOwnedPid
                            ? "\"errorCode\":\"PROCESS_EXITED\",\"error\":\"The coordinator-owned RimWorld process is no longer running.\""
                            : "\"errorCode\":null,\"error\":null";
                        return Result($"{{\"success\":true,\"state\":\"STOPPED\",{stale},\"generation\":2,\"profileFingerprint\":null,\"resolvedMods\":[],\"leaseId\":{lease},\"rimBridge\":{{\"ConfiguredMode\":\"{mode}\",\"PackageId\":\"brrainz.rimbridgeserver\",\"Version\":\"2.1.0\",\"LifecycleState\":\"STALE\"}}}}");
                    }
                    return Result($"{{\"success\":true,\"state\":\"READY\",\"generation\":2,\"profileFingerprint\":\"profile-fixture\",\"resolvedMods\":[\"lan.fixture\",\"Core\"],\"leaseId\":{lease},\"rimBridge\":{{\"ConfiguredMode\":\"{mode}\",\"PackageId\":\"brrainz.rimbridgeserver\",\"Version\":\"2.1.0\",\"LifecycleState\":\"READY\"}}}}");
                }
                statusCalls++;
                if (UnmanagedRimWorld && statusCalls == 1)
                    return Result("{\"success\":false,\"errorCode\":\"PROCESS_OWNERSHIP_AMBIGUOUS\",\"error\":\"An unmanaged RimWorld process is running.\"}");
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

        private static DevBridgeProcessResult Result(string stdout, int exitCode = 0) => new(exitCode, stdout, string.Empty);
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
