using System.Text.Json;
using System.Text.Json.Nodes;
using RimLiaison.Git;
using RimLiaison.Qualification;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class UnifiedRuntimeOwnershipTests
{
    public static void DirtySourcePathsAreMeaningful()
    {
        IReadOnlyList<string> paths = RepositoryChangeClassificationPolicy.MeaningfulPaths(
            [new GitRepositoryChange("src/DevBridgeRuntime/Transaction/mod-test.ps1", "M", false, false)]);
        Assert(paths.SequenceEqual(["src/DevBridgeRuntime/Transaction/mod-test.ps1"]),
            "owned runtime source changes must remain qualification-significant");
    }

    public static void CandidateContractHasNoExternalCheckoutIdentity()
    {
        string[] names = typeof(ToolchainCandidate).GetProperties().Select(property => property.Name).ToArray();
        Assert(names.All(name => !name.Contains("DevBridgeSource", StringComparison.Ordinal) &&
            !name.Contains("DevBridgeRelease", StringComparison.Ordinal)),
            "candidate identity must not expose an external DevBridge checkout or release manifest");
    }

    public static void PromotedFingerprintIncludesRuntimeHashes()
    {
        string first = ToolchainPromotionService.ComputePromotedFingerprint(
            "source", "cli", "assembly", "coordinator", "package", "consumer", "protocol",
            "RimLiaison", "RimLiaison.Runtime", "mod-a", "manifest-a");
        string second = ToolchainPromotionService.ComputePromotedFingerprint(
            "source", "cli", "assembly", "coordinator", "package", "consumer", "protocol",
            "RimLiaison", "RimLiaison.Runtime", "mod-b", "manifest-a");
        Assert(first != second, "mod identity must participate in the promoted product fingerprint");
    }

    public static void QualifiedIdentityIncludesRuntimeHashes()
    {
        string first = ToolchainPromotionService.ComputeQualifiedPayloadIdentity(
            "qualification", "source", "cli", "assembly", "package", "coordinator", "consumer",
            "protocol", "mod-a", "manifest-a");
        string second = ToolchainPromotionService.ComputeQualifiedPayloadIdentity(
            "qualification", "source", "cli", "assembly", "package", "coordinator", "consumer",
            "protocol", "mod-b", "manifest-a");
        Assert(first != second, "qualified payload identity must include owned runtime hashes");
    }

    public static void OwnedRuntimeManifestBindsSourceCommit()
    {
        using CandidateFixture fixture = new();
        using JsonDocument manifest = JsonDocument.Parse(fixture.RuntimeManifestText);
        Assert(manifest.RootElement.GetProperty("sourceCommit").GetString() == fixture.SourceCommit &&
            ToolchainFileHash.Sha256(fixture.RuntimeManifestPath) == fixture.RuntimeManifestHash,
            "new unified runtime manifest must bind its exact source commit and serialized-file hash");
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(result.Passed, result.Error ?? "owned runtime candidate was rejected");
    }

    public static void RuntimeManifestSourceCommitMismatchBlocks()
    {
        using CandidateFixture fixture = new();
        fixture.SetManifestSourceCommit("other-source");
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(!result.Passed, "a runtime manifest source commit mismatch was accepted");
    }

    public static void RuntimeManifestMissingSourceCommitBlocks()
    {
        using CandidateFixture fixture = new();
        fixture.SetManifestSourceCommit(null);
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(!result.Passed, "a new unified runtime manifest without sourceCommit was accepted");
    }

    public static void RuntimeManifestGenerationIsDeterministic()
    {
        using CandidateFixture first = new();
        using CandidateFixture second = new();
        Assert(first.RuntimeManifestText == second.RuntimeManifestText &&
            first.RuntimeManifestHash == second.RuntimeManifestHash,
            "identical runtime inputs must generate identical manifest contents and hashes");
    }

    public static void RuntimeManifestHashMutationBlocks()
    {
        using CandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify(runtimeManifestHash: "wrong");
        Assert(!result.Passed, "runtime manifest hash substitution was accepted");
    }

    public static void RuntimeModHashMutationBlocks()
    {
        using CandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify(modHash: "wrong");
        Assert(!result.Passed, "runtime mod hash substitution was accepted");
    }

    public static void CandidateHealthRunsPublishedCliSelfCheck()
    {
        using CandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify();
        using JsonDocument summary = JsonDocument.Parse(result.Summary);
        Assert(result.Passed &&
            summary.RootElement.GetProperty("healthStage").GetString() == "candidate-pre-commit" &&
            summary.RootElement.GetProperty("candidateCliProbe").GetString() == "passed",
            result.Error ?? "candidate health did not run the published CLI self-check");
    }

    public static void IsolatedRuntimeBuildAvoidsExecutingCliOutput()
    {
        string sourceRoot = FindSourceRoot();
        string componentRoot = Path.Combine(sourceRoot, "src", "DevBridgeRuntime");
        string sourcePackageRoot = Path.Combine(componentRoot, "Package");
        RimWorldManagedAssemblyResolution managed = RimWorldManagedAssemblyResolver.Resolve(
            sourceRoot,
            componentRoot);
        Assert(managed.Succeeded && managed.ManagedDirectory is not null,
            managed.Error ?? "RimWorld managed assemblies could not be resolved for isolated build coverage");
        string packageSnapshot = Snapshot(sourcePackageRoot);
        string cliSnapshot = Snapshot(Path.Combine(sourceRoot, "src", "RimLiaison.Cli", "bin", "Release", "net8.0"));
        string root = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-isolated-runtime-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "candidate-a", "runtime-build", "package");
        string second = Path.Combine(root, "candidate-b", "runtime-build", "package");
        Directory.CreateDirectory(root);
        try
        {
            string executingCliAssembly = Path.Combine(
                sourceRoot,
                "src",
                "RimLiaison.Cli",
                "bin",
                "Release",
                "net8.0",
                "rimliaison.dll");
            using FileStream heldCliAssembly = File.Open(
                executingCliAssembly,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            ToolchainCandidateMaterializer.RuntimeBuildResult[] builds = Task.WhenAll(
                    ToolchainCandidateMaterializer.BuildOwnedRuntimeAsync(
                        sourceRoot,
                        sourcePackageRoot,
                        first,
                        managed.ManagedDirectory!,
                        CancellationToken.None),
                    ToolchainCandidateMaterializer.BuildOwnedRuntimeAsync(
                        sourceRoot,
                        sourcePackageRoot,
                        second,
                        managed.ManagedDirectory!,
                        CancellationToken.None))
                .GetAwaiter()
                .GetResult();
            foreach ((ToolchainCandidateMaterializer.RuntimeBuildResult build, string packageRoot) in builds.Zip(
                         [first, second],
                         (build, packageRoot) => (build, packageRoot)))
            {
                Assert(build.Succeeded,
                    (build.Error ?? "isolated runtime build failed") +
                    "\nstdout=" + build.Stdout +
                    "\nstderr=" + build.Stderr);
                Assert(build.ProjectPath.EndsWith("runtime-build.proj", StringComparison.OrdinalIgnoreCase) &&
                    !build.ProjectPath.EndsWith("RimLiaison.sln", StringComparison.OrdinalIgnoreCase),
                    "candidate runtime build must use the isolated runtime project graph");
                Assert(Path.GetFullPath(build.IsolatedPackageRoot) == Path.GetFullPath(packageRoot),
                    "runtime build evidence must identify its isolated package root");
                Assert(ToolchainCandidateMaterializer.ValidateRuntimePackage(
                        packageRoot,
                        out string? packageError),
                    packageError ?? ("isolated runtime package was incomplete; files=" +
                        string.Join(",", Directory.EnumerateFiles(
                            packageRoot,
                            "*",
                            SearchOption.AllDirectories)
                            .Select(path => Path.GetRelativePath(packageRoot, path))) +
                        "\nstdout=" + build.Stdout +
                        "\nstderr=" + build.Stderr));
                Assert(File.Exists(Path.Combine(packageRoot, "About", "About.xml")) &&
                    File.Exists(Path.Combine(packageRoot, "CHANGELOG.md")) &&
                    File.Exists(Path.Combine(packageRoot, "Coordinator", "DevBridge.Coordinator.exe")) &&
                    File.Exists(Path.Combine(packageRoot, "BridgeTools", "DevBridge2.BridgeTools.dll")) &&
                    File.Exists(Path.Combine(packageRoot, "1.6", "Assemblies", "DevBridge2.dll")),
                    "isolated runtime package omitted static or generated product content");
                Assert(packageRoot.StartsWith(Path.Combine(root, "candidate-"),
                        StringComparison.OrdinalIgnoreCase),
                    "runtime output escaped the candidate-owned build root");
                Assert(!Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
                        .Any(path => Path.GetFileName(path).Contains(
                            "Microsoft.NETFramework.ReferenceAssemblies",
                            StringComparison.OrdinalIgnoreCase)),
                    "compile-only reference assemblies leaked into runtime package output");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Assert(packageSnapshot == Snapshot(sourcePackageRoot),
            "candidate runtime materialization mutated the source runtime package");
        Assert(cliSnapshot == Snapshot(Path.Combine(
                sourceRoot,
                "src",
                "RimLiaison.Cli",
                "bin",
                "Release",
                "net8.0")),
            "candidate runtime materialization mutated the executing CLI deployment");
    }

    private static string FindSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RimLiaison.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("RimLiaison source root could not be found");
    }

    private static string Snapshot(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + "\0" +
                    ToolchainFileHash.Sha256(path))
                .OrderBy(value => value, StringComparer.Ordinal));

    public static void CandidateHealthIgnoresRimWorldExecutable()
    {
        using CandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify(rimWorldExecutable: Path.Combine(
            fixture.Root, "nonexistent-rimworld.exe"));
        Assert(result.Passed, result.Error ?? "structural candidate health consulted RimWorld");
    }

    public static void MachinePreflightMissingRootBlocks()
    {
        using MachineFixture fixture = MachineFixture.Create();
        using EnvironmentScope scope = fixture.Environment();
        Directory.Delete(fixture.RimWorldRoot, recursive: true);
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier().Verify(
            fixture.SourceRoot, fixture.RuntimeRoot);
        Assert(result.ErrorCode == "RIMWORLD_ROOT_NOT_FOUND", "missing RimWorld root returned the wrong preflight code");
    }

    public static void MachinePreflightMissingManagedAssemblyBlocks()
    {
        using MachineFixture fixture = MachineFixture.Create(withManagedAssemblies: false);
        using EnvironmentScope scope = fixture.Environment();
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier().Verify(
            fixture.SourceRoot, fixture.RuntimeRoot);
        Assert(result.ErrorCode == "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
            "missing managed assemblies returned the wrong preflight code");
    }

    public static void MachinePreflightMissingProfileBlocks()
    {
        using MachineFixture fixture = MachineFixture.Create(withManagedAssemblies: true, withProfile: false);
        using EnvironmentScope scope = fixture.Environment();
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier().Verify(
            fixture.SourceRoot, fixture.RuntimeRoot);
        Assert(result.ErrorCode == "RIMWORLD_PROFILE_NOT_INITIALIZED",
            "missing ModsConfig returned the wrong preflight code");
    }

    public static void MachinePreflightReadyIsReadOnly()
    {
        using MachineFixture fixture = MachineFixture.Create(withManagedAssemblies: true, withProfile: true);
        using EnvironmentScope scope = fixture.Environment();
        string[] before = Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier().Verify(
            fixture.SourceRoot, fixture.RuntimeRoot);
        string[] after = Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert(result.Passed && before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase),
            "ready machine preflight must not mutate the machine or profile");
    }

    public static void MachinePreflightRejectsOutsideModsDestination()
    {
        using MachineFixture fixture = MachineFixture.Create(withManagedAssemblies: true, withProfile: true);
        using EnvironmentScope scope = fixture.Environment();
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier().Verify(
            fixture.SourceRoot, Path.Combine(fixture.Root, "outside", "DevBridge2"));
        Assert(result.ErrorCode == "RIMLIAISON_RUNTIME_DESTINATION_INVALID",
            "outside Mods destination was accepted by machine preflight");
    }

    public static void PreflightErrorsAreStructured()
    {
        ProductionMachinePreflightResult result = ProductionMachinePreflightResult.Blocked(
            "RIMWORLD_PROFILE_NOT_INITIALIZED", "profile missing");
        string json = JsonSerializer.Serialize(result);
        Assert(json.Contains("rimliaison-production-machine-preflight/v1", StringComparison.Ordinal) &&
            json.Contains("RIMWORLD_PROFILE_NOT_INITIALIZED", StringComparison.Ordinal),
            "machine preflight failure omitted structured identity");
    }
    public static void MachinePreflightRunningProcessBlocks()
    {
        using MachineFixture fixture = MachineFixture.Create(withManagedAssemblies: true, withProfile: true);
        using EnvironmentScope scope = fixture.Environment();
        ProductionMachinePreflightResult result = new ProductionMachinePreflightVerifier(() => true).Verify(
            fixture.SourceRoot, fixture.RuntimeRoot);
        Assert(result.ErrorCode == "RIMWORLD_PROCESS_RUNNING",
            "running RimWorld process did not block canonical activation preflight");
    }

    public static void LegacyRuntimeIdentityDoesNotSatisfyNewCandidate()
    {
        using CandidateFixture fixture = new(legacyMetadata: true);
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(!result.Passed, "legacy unowned runtime metadata was accepted as a new candidate");
    }

    public static void Net472ReferenceAssembliesDependencyIsPinnedAndPrivate()
    {
        string sourceRoot = FindSourceRoot();
        string project = File.ReadAllText(Path.Combine(
            sourceRoot,
            "src",
            "DevBridgeRuntime",
            "Mod",
            "DevBridge2.csproj"));
        Assert(project.Contains(
                "<PackageReference Include=\"Microsoft.NETFramework.ReferenceAssemblies.net472\"",
                StringComparison.Ordinal) &&
            project.Contains("Version=\"1.0.3\"", StringComparison.Ordinal) &&
            project.Contains("PrivateAssets=\"All\"", StringComparison.Ordinal),
            "net472 reference assemblies package must be pinned and private");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class CandidateFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-owned-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly string sourceCommit = "owned-source";
        private readonly string executable;
        private readonly string assembly;
        private readonly string runtime;
        private readonly string consumer;
        private readonly string coordinator;
        private readonly string mod;
        private readonly string runtimeManifest;
        private readonly string modHash;
        private string runtimeManifestHash;

        public string SourceCommit => sourceCommit;
        public string RuntimeManifestPath => runtimeManifest;
        public string RuntimeManifestText => File.ReadAllText(runtimeManifest);

        public string RuntimeManifestHash => runtimeManifestHash;

        public CandidateFixture(bool legacyMetadata = false)
        {
            executable = Path.Combine(Root, "candidate", "rimliaison.exe");
            assembly = Path.Combine(Root, "candidate", "rimliaison.dll");
            consumer = Path.Combine(Root, "candidate", "transaction-components", "mod-test.ps1");
            runtime = Path.Combine(Root, "runtime");
            coordinator = Path.Combine(runtime, "Coordinator", "DevBridge.Coordinator.exe");
            mod = Path.Combine(runtime, "1.6", "Assemblies", "DevBridge2.dll");
            runtimeManifest = Path.Combine(runtime, ".devbridge-runtime-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(consumer)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coordinator)!);
            Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
            File.WriteAllText(executable, "candidate-cli");
            File.WriteAllText(assembly, "candidate-assembly");
            File.WriteAllText(consumer, "candidate-consumer");
            foreach (string publishedFile in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(publishedFile, Path.Combine(Path.GetDirectoryName(executable)!, Path.GetFileName(publishedFile)), overwrite: true);
            CliDeploymentManifest cliManifest = CliDeploymentManifestService.Write(
                Path.GetDirectoryName(executable)!,
                sourceCommit,
                "net8.0");
            File.WriteAllText(Path.Combine(runtime, "DevBridge.cmd"), "owned-runtime");
            File.WriteAllText(coordinator, "owned-coordinator");
            File.WriteAllText(mod, "owned-mod");
            modHash = Hash(mod);
            File.WriteAllText(runtimeManifest, JsonSerializer.Serialize(new
            {
                schemaVersion = "devbridge-runtime-manifest/v1",
                ownerProduct = legacyMetadata ? "DevBridge2" : ToolchainPromotionSchemas.OwnerProduct,
                componentRole = "DevBridge runtime",
                project = ToolchainPromotionSchemas.OwnerProduct,
                packageId = "lan.devbridge2",
                productionEligible = false,
                sourceCommit,
                runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                packageSha256 = "owned-package",
                files = new[]
                {
                    new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = Hash(coordinator) },
                    new { path = "1.6/Assemblies/DevBridge2.dll", sha256 = modHash }
                }
            }));
            runtimeManifestHash = Hash(runtimeManifest);
        }

        public void SetManifestSourceCommit(string? value)
        {
            JsonObject manifest = JsonNode.Parse(RuntimeManifestText)!.AsObject();
            if (value is null)
                manifest.Remove("sourceCommit");
            else
                manifest["sourceCommit"] = value;
            File.WriteAllText(runtimeManifest, manifest.ToJsonString());
            runtimeManifestHash = Hash(runtimeManifest);
        }

        public PromotionCandidateHealthResult Verify(
            string? modHash = null,
            string? runtimeManifestHash = null,
            string? rimWorldExecutable = null)
        {
            var binding = new PromotionCandidateHealthBinding(
                executable,
                runtime,
                "candidate-fingerprint",
                sourceCommit,
                "owned-package",
                Hash(coordinator),
                Hash(consumer),
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                rimWorldExecutable ?? Path.Combine(Root, "rimworld.exe"))
            {
                DevBridgeModSha256 = modHash ?? this.modHash,
                DevBridgeRuntimeManifestSha256 = runtimeManifestHash ?? this.runtimeManifestHash,
                RimLiaisonExecutableSha256 = Hash(executable),
                RimLiaisonAssemblySha256 = Hash(assembly),
                RimLiaisonCliDeploymentManifestSha256 = Hash(Path.Combine(
                    Path.GetDirectoryName(executable)!,
                    CliDeploymentManifestService.FileName)),
                RimLiaisonCliDeploymentPackageSha256 = JsonSerializer.Deserialize<CliDeploymentManifest>(
                    File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!,
                        CliDeploymentManifestService.FileName)))!.PackageSha256
            };
            return ToolchainPromotionService.RunCandidateHealthAsync(
                binding, "owned-runtime-test", CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static string Hash(string path) => ToolchainFileHash.Sha256(path);
    }

    private sealed class MachineFixture : IDisposable
    {
        public string Root { get; }
        public string SourceRoot { get; }
        public string RimWorldRoot { get; }
        public string RuntimeRoot { get; }
        private readonly bool withManagedAssemblies;
        private readonly bool withProfile;

        private MachineFixture(bool withManagedAssemblies, bool withProfile)
        {
            Root = Path.Combine(Path.GetTempPath(), "rimliaison-machine-preflight-" + Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            RimWorldRoot = Path.Combine(Root, "RimWorld");
            RuntimeRoot = Path.Combine(RimWorldRoot, "Mods", "DevBridge2");
            this.withManagedAssemblies = withManagedAssemblies;
            this.withProfile = withProfile;
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(Path.Combine(RimWorldRoot, "Mods"));
            if (withManagedAssemblies)
            {
                string managed = Path.Combine(RimWorldRoot, "RimWorldWin64_Data", "Managed");
                Directory.CreateDirectory(managed);
                File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "fixture");
                File.WriteAllText(Path.Combine(managed, "UnityEngine.CoreModule.dll"), "fixture");
            }
            File.WriteAllText(Path.Combine(RimWorldRoot, "RimWorldWin64.exe"), "fixture");
            if (withProfile)
            {
                string profile = Path.Combine(Root, "ModsConfig.xml");
                File.WriteAllText(profile, "<ModsConfigData />");
            }
        }

        public static MachineFixture Create(bool withManagedAssemblies = false, bool withProfile = false) =>
            new(withManagedAssemblies, withProfile);

        public EnvironmentScope Environment() => new(
            ("RIMWORLD_ROOT", RimWorldRoot),
            ("RIMWORLD_EXECUTABLE", Path.Combine(RimWorldRoot, "RimWorldWin64.exe")),
            ("RIMLIAISON_MODS_CONFIG", withProfile ? Path.Combine(Root, "ModsConfig.xml") : Path.Combine(Root, "missing-ModsConfig.xml")),
            ("RIMDEV_ROOT", SourceRoot));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> values = new();

        public EnvironmentScope(params (string Name, string? Value)[] entries)
        {
            foreach ((string name, string? value) in entries)
            {
                values[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in values)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
