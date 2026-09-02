using System.Text.Json;
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
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(result.Passed, result.Error ?? "owned runtime candidate was rejected");
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

    public static void CandidateHealthReportsStructuralOnly()
    {
        using CandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify();
        using JsonDocument summary = JsonDocument.Parse(result.Summary);
        Assert(summary.RootElement.GetProperty("devBridgeDoctor").GetString() == "not-run-structural" &&
            summary.RootElement.GetProperty("capabilities").GetString() == "not-run-structural",
            "candidate health must not run live DevBridge checks before activation");
    }

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
        private readonly string runtimeManifestHash;

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
                RimLiaisonAssemblySha256 = Hash(assembly)
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
