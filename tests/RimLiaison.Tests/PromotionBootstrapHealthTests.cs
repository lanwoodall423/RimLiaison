using System.Security.Cryptography;
using System.Text.Json;
using RimLiaison.Git;
using RimLiaison.Qualification;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class PromotionBootstrapHealthTests
{
    public static void HealthyProductionHealthyCandidateSuccess() => AssertSuccess("healthy");
    public static void MissingLegacyRuntimeReplacement() => AssertSuccess("missing");
    public static void CorruptLegacyRuntimeReplacement() => AssertSuccess("corrupt");
    public static void UnrecoverableLegacyReplacement() => AssertSuccess("unrecoverable");

    public static void MissingLegacyUnhealthyCandidateRollback()
    {
        using Fixture fixture = new("missing");
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.ErrorCode == "PROMOTION_CANDIDATE_HEALTH_FAILED", "candidate failure was not classified");
        Assert(File.ReadAllText(fixture.ManifestPath) == before, "candidate failure changed the active manifest");
        Assert(!Directory.Exists(fixture.ProductionRuntimeRoot), "candidate failure activated a missing legacy runtime");
    }

    public static void CandidateRootHealthBinding()
    {
        using Fixture fixture = new("missing");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", "candidate-root promotion failed");
        Assert(fixture.CandidateVerifier.Binding is not null &&
               fixture.CandidateVerifier.Binding.CandidateRuntimeRoot.Contains(
                   "runtime-promoted-source",
                   StringComparison.OrdinalIgnoreCase),
            "candidate health did not receive the staged runtime root");
        Assert(fixture.CandidateVerifier.Binding?.CandidateRuntimeRoot != fixture.ProductionRuntimeRoot,
            "candidate health used the active runtime root");
    }

    public static void UnrelatedHealthyInstallationCannotSatisfyIdentity()
    {
        using Fixture fixture = new("missing");
        fixture.CandidateVerifier.ExpectedRuntimeRoot = Path.Combine(fixture.Root, "unrelated-runtime");
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.ErrorCode == "PROMOTION_CANDIDATE_HEALTH_FAILED", "unrelated runtime was accepted");
        Assert(fixture.CandidateVerifier.Binding?.CandidateRuntimeRoot != fixture.CandidateVerifier.ExpectedRuntimeRoot,
            "candidate binding was silently substituted with an unrelated runtime");
    }

    public static void MissingCandidateDllFailsAccurately()
    {
        using Fixture fixture = new("missing");
        File.Delete(Path.Combine(fixture.CandidateRuntimeRoot, "1.6", "Assemblies", "DevBridge2.dll"));
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RECOVERY_PAYLOAD_INVALID", "missing candidate DLL was not rejected");
        Assert(File.ReadAllText(fixture.ManifestPath) == fixture.ManifestBefore, "missing DLL changed active identity");
    }

    public static void CorruptCandidateCoordinatorFailsAccurately()
    {
        using Fixture fixture = new("missing");
        File.WriteAllText(Path.Combine(fixture.CandidateRuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"), "corrupt");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RECOVERY_PAYLOAD_INVALID", "corrupt coordinator was not rejected");
        Assert(File.ReadAllText(fixture.ManifestPath) == fixture.ManifestBefore, "corrupt coordinator changed active identity");
    }

    public static void CandidateFingerprintMismatchFails()
    {
        using Fixture fixture = new("missing");
        fixture.MutateQualificationAssemblyHash();
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_QUALIFIED_ARTIFACT_MISMATCH", "candidate fingerprint mismatch was accepted");
    }

    public static void PostCommitDoctorResolvesNewFingerprint()
    {
        using Fixture fixture = new("missing");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted" && result.PromotedFingerprint == fixture.ExpectedFingerprint,
            "post-commit health did not resolve the new fingerprint");
        Assert(fixture.CanonicalVerifier.InstalledExecutable is not null, "post-commit doctor was not invoked");
        Assert(fixture.CanonicalVerifier.ResolvedFingerprint == fixture.ExpectedFingerprint,
            "post-commit doctor did not resolve the active new fingerprint");
        Assert(result.CandidateHealth?.ActiveManifestChanged == true, "promotion evidence omitted manifest activation");
    }

    public static void PostCommitDoctorFailureRollsBack()
    {
        using Fixture fixture = new("missing") { CanonicalPass = false };
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_POST_COMMIT_HEALTH_FAILED", "post-commit failure was not classified");
        using JsonDocument beforeManifest = JsonDocument.Parse(before);
        using JsonDocument afterManifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(
            afterManifest.RootElement.GetProperty("promotedFingerprint").GetString() ==
            beforeManifest.RootElement.GetProperty("promotedFingerprint").GetString(),
            "post-commit failure left a new manifest active");
        Assert(!Directory.Exists(Path.Combine(fixture.ProductionRuntimeRoot, "Coordinator")),
            "post-commit rollback left candidate runtime contents active");
    }

    public static void FailedPromotionNeverChangesActiveIdentity()
    {
        using Fixture fixture = new("healthy");
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.Status == "blocked", "failed promotion did not block");
        Assert(File.ReadAllText(fixture.ManifestPath) == before, "failed promotion changed active identity");
    }

    public static void ConcurrentPromotionsAreLockSafe()
    {
        using Fixture fixture = new("missing") { CandidateDelay = TimeSpan.FromMilliseconds(250) };
        Task<ToolchainPromotionResult> first = Task.Run(() => fixture.Promote());
        Task<ToolchainPromotionResult> second = Task.Run(() => fixture.Promote());
        Task.WaitAll(first, second);
        Assert(
            new[] { first.Result.Status, second.Result.Status }.Count(status => status == "promoted") == 1 &&
            new[] { first.Result.ErrorCode, second.Result.ErrorCode }.Any(code =>
                code is "PROMOTION_LOCKED" or "PROMOTION_DESTINATION_EXISTS"),
            $"concurrent promotions did not preserve single-writer transaction safety: {first.Result.Status}/{first.Result.ErrorCode}/{first.Result.Error}, {second.Result.Status}/{second.Result.ErrorCode}/{second.Result.Error}");
    }

    public static void CancellationLeavesDeterministicRollbackState()
    {
        using Fixture fixture = new("missing") { CancelCandidate = true };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_TRANSACTION_FAILED", "cancellation was not contained by the transaction");
        using JsonDocument beforeManifest = JsonDocument.Parse(fixture.ManifestBefore);
        using JsonDocument afterManifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(
            afterManifest.RootElement.GetProperty("promotedFingerprint").GetString() ==
            beforeManifest.RootElement.GetProperty("promotedFingerprint").GetString(),
            "cancellation changed the active manifest");
        Assert(!Directory.Exists(fixture.ProductionRuntimeRoot), "cancellation activated candidate runtime");
    }

    private static void AssertSuccess(string legacyState)
    {
        using Fixture fixture = new(legacyState);
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", $"healthy candidate could not replace {legacyState} production: {result.ErrorCode} {result.Error}");
        Assert(File.Exists(Path.Combine(fixture.ProductionRuntimeRoot, "DevBridge.cmd")), "candidate runtime was not committed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private const string SourceCommit = "source-commit";
        private readonly string? previousManifestEnvironment;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-bootstrap-test-" + Guid.NewGuid().ToString("N"));
        public string ManifestPath { get; }
        public string PackagePath { get; }
        public string CandidateRuntimeRoot { get; }
        public string ProductionRuntimeRoot { get; }
        public string ExpectedCandidateRuntimeRoot { get; }
        public string ExpectedFingerprint { get; }
        public string ManifestBefore { get; }
        public FakeCandidateVerifier CandidateVerifier { get; } = new();
        public FakeCanonicalVerifier CanonicalVerifier { get; } = new();
        public bool CanonicalPass { get; init; } = true;
        public bool CancelCandidate { get; init; }
        public TimeSpan CandidateDelay { get; init; }

        public Fixture(string legacyState)
        {
            Directory.CreateDirectory(Root);
            string artifactRoot = Path.Combine(Root, "qualified-artifacts");
            string candidatePayloadRoot = Path.Combine(Root, "qualified-runtime");
            string transactionRoot = Path.Combine(artifactRoot, "transaction-components");
            CandidateRuntimeRoot = candidatePayloadRoot;
            ExpectedCandidateRuntimeRoot = candidatePayloadRoot;
            ProductionRuntimeRoot = Path.Combine(Root, "production", "DevBridge2");
            ManifestPath = Path.Combine(Root, "production-toolchain.json");
            PackagePath = Path.Combine(Root, "qualified-package.json");
            Directory.CreateDirectory(artifactRoot);
            Directory.CreateDirectory(Path.Combine(candidatePayloadRoot, "Coordinator"));
            Directory.CreateDirectory(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies"));
            Directory.CreateDirectory(transactionRoot);
            File.WriteAllText(Path.Combine(artifactRoot, "rimliaison.exe"), "candidate-executable");
            File.WriteAllText(Path.Combine(artifactRoot, "rimliaison.dll"), "candidate-assembly");
            File.WriteAllText(Path.Combine(transactionRoot, "mod-test.ps1"), "candidate-consumer");
            File.WriteAllText(Path.Combine(artifactRoot, "unified-package.json"), "{}");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "DevBridge.cmd"), "candidate-runtime");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "Coordinator", "DevBridge.Coordinator.exe"), "candidate-coordinator");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies", "DevBridge2.dll"), "candidate-mod");
            string coordinatorHash = Hash(Path.Combine(candidatePayloadRoot, "Coordinator", "DevBridge.Coordinator.exe"));
            string modHash = Hash(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies", "DevBridge2.dll"));
            File.WriteAllText(
                Path.Combine(candidatePayloadRoot, ".devbridge-runtime-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    packageSha256 = "candidate-package",
                    files = new[]
                    {
                        new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = coordinatorHash },
                        new { path = "1.6/Assemblies/DevBridge2.dll", sha256 = modHash }
                    }
                }));

            string executableHash = Hash(Path.Combine(artifactRoot, "rimliaison.exe"));
            string assemblyHash = Hash(Path.Combine(artifactRoot, "rimliaison.dll"));
            string consumerHash = Hash(Path.Combine(transactionRoot, "mod-test.ps1"));
            ExpectedFingerprint = ToolchainPromotionService.ComputePromotedFingerprint(
                SourceCommit,
                executableHash,
                assemblyHash,
                coordinatorHash,
                "candidate-package",
                consumerHash,
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                ToolchainPromotionSchemas.OwnerProduct,
                ToolchainPromotionSchemas.RuntimeSubsystem);
            File.WriteAllText(
                Path.Combine(artifactRoot, "unified-package.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-unified-production-package/v2",
                    productFingerprint = ExpectedFingerprint,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimBridgeServer = new { boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary }
                }));

            string qualificationPath = Path.Combine(Root, "qualification.json");
            File.WriteAllText(
                qualificationPath,
                JsonSerializer.Serialize(new
                {
                    sourceCommit = SourceCommit,
                    passes = 1,
                    totalRuns = 1,
                    infrastructureFailures = 0,
                    fixtureFailures = 0,
                    qualifiedArtifactHashes = new
                    {
                        rimLiaisonExecutableSha256 = executableHash,
                        rimLiaisonAssemblySha256 = assemblyHash,
                        devBridgePackageSha256 = "candidate-package",
                        devBridgeCoordinatorSha256 = coordinatorHash,
                        transactionConsumerSha256 = consumerHash
                    }
                }));

            string oldCliDirectory = Path.Combine(Root, "production", "old-cli");
            Directory.CreateDirectory(oldCliDirectory);
            string oldCli = Path.Combine(oldCliDirectory, "rimliaison.exe");
            string oldAssembly = Path.Combine(oldCliDirectory, "rimliaison.dll");
            string oldConsumer = Path.Combine(oldCliDirectory, "transaction-components", "mod-test.ps1");
            string oldUnified = Path.Combine(oldCliDirectory, "unified-package.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldConsumer)!);
            File.WriteAllText(oldCli, "old-executable");
            File.WriteAllText(oldAssembly, "old-assembly");
            File.WriteAllText(oldConsumer, "candidate-consumer");
            File.WriteAllText(oldUnified, "{}");
            if (legacyState is "healthy" or "corrupt")
            {
                Directory.CreateDirectory(ProductionRuntimeRoot);
                File.WriteAllText(Path.Combine(ProductionRuntimeRoot, "DevBridge.cmd"), legacyState);
            }
            string oldCliHash = Hash(oldCli);
            if (legacyState == "unrecoverable")
                File.Delete(oldCli);
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = "previous-fingerprint",
                    fingerprint = "previous-fingerprint",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimLiaisonExecutablePath = oldCli,
                    rimLiaisonExecutableSha256 = oldCliHash,
                    rimLiaisonAssemblyPath = oldAssembly,
                    rimLiaisonAssemblySha256 = Hash(oldAssembly),
                    devBridgeRuntimeRoot = ProductionRuntimeRoot,
                    devBridgePackageSha256 = "old-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    transactionConsumerPath = oldConsumer,
                    transactionConsumerSha256 = Hash(oldConsumer),
                    unifiedManifestPath = oldUnified,
                    unifiedManifestSha256 = Hash(oldUnified),
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                    qualificationArtifactSha256 = "old-proof"
                }));
            ManifestBefore = File.ReadAllText(ManifestPath);
            string qualificationHash = Hash(qualificationPath);
            File.WriteAllText(
                PackagePath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ToolchainPromotionSchemas.Package,
                    sourceCommit = SourceCommit,
                    qualificationArtifactPath = qualificationPath,
                    qualificationArtifactSha256 = qualificationHash,
                    artifactRoot,
                    rimLiaisonExecutableRelativePath = "rimliaison.exe",
                    rimLiaisonAssemblyRelativePath = "rimliaison.dll",
                    rimLiaisonExecutableSha256 = executableHash,
                    rimLiaisonAssemblySha256 = assemblyHash,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    devBridgeRuntimeRoot = ProductionRuntimeRoot,
                    devBridgeRuntimeArtifactRoot = candidatePayloadRoot,
                    devBridgePackageSha256 = "candidate-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    transactionConsumerPath = Path.Combine(transactionRoot, "mod-test.ps1"),
                    transactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                    transactionConsumerSha256 = consumerHash,
                    unifiedManifestRelativePath = "unified-package.json",
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract
                }));
            previousManifestEnvironment = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", ManifestPath);
            CandidateVerifier.ExpectedRuntimeRoot = null;
        }

        public ToolchainPromotionResult Promote(bool candidatePass = true)
        {
            CandidateVerifier.Pass = candidatePass;
            CandidateVerifier.Delay = CandidateDelay;
            CandidateVerifier.Cancel = CancelCandidate;
            CanonicalVerifier.Pass = CanonicalPass;
            return ToolchainPromotionService.PromoteAsync(
                    Root,
                    PackagePath,
                    null,
                    promotionHealthVerifier: CandidateVerifier,
                    canonicalHealthVerifier: CanonicalVerifier,
                    gitRepositoryStateProvider: new FakeGitProvider(SourceCommit))
                .GetAwaiter()
                .GetResult();
        }

        public void MutateQualificationAssemblyHash()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "qualification.json")));
            var node = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            node["qualifiedArtifactHashes"]!["rimLiaisonAssemblySha256"] = "mismatch";
            string path = Path.Combine(Root, "qualification.json");
            File.WriteAllText(path, node.ToJsonString());
            var package = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PackagePath))!.AsObject();
            package["qualificationArtifactSha256"] = Hash(path);
            File.WriteAllText(PackagePath, package.ToJsonString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", previousManifestEnvironment);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    private sealed class FakeGitProvider(string commit) : IGitRepositoryStateProvider
    {
        public Task<GitRepositoryStateResult> ReadAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryStateResult(true, new GitRepositoryStateSnapshot(
                rootPath, "fixture", null, commit, null, 0, 0, false, [])));
    }

    private sealed class FakeCandidateVerifier : IPromotionCandidateHealthVerifier
    {
        public bool Pass { get; set; }
        public bool Cancel { get; set; }
        public TimeSpan Delay { get; set; }
        public string? ExpectedRuntimeRoot { get; set; }
        public PromotionCandidateHealthBinding? Binding { get; private set; }

        public async Task<PromotionCandidateHealthResult> VerifyAsync(
            PromotionCandidateHealthBinding binding,
            string workflowId,
            CancellationToken cancellationToken)
        {
            Binding = binding;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            bool bound = ExpectedRuntimeRoot is null ||
                string.Equals(
                    Path.GetFullPath(binding.CandidateRuntimeRoot),
                    Path.GetFullPath(ExpectedRuntimeRoot),
                    StringComparison.OrdinalIgnoreCase);
            bool pass = Pass && bound;
            return new(
                pass,
                "fixture-candidate-health",
                pass ? null : "fixture candidate health failed",
                new PromotionCandidateHealthEvidence(
                    "candidate-pre-commit",
                    pass ? "passed" : "failed",
                    binding.CandidateFingerprint,
                    binding.CandidateRuntimeRoot,
                    binding.CandidateExecutable,
                    binding.CandidateSourceCommit,
                    binding.DevBridgePackageSha256,
                    binding.DevBridgeCoordinatorSha256,
                    binding.TransactionConsumerSha256,
                    binding.RuntimeProtocolContract,
                    pass ? null : "fixture candidate health failed"));
        }
    }

    private sealed class FakeCanonicalVerifier : IPromotionCanonicalHealthVerifier
    {
        public bool Pass { get; set; }
        public string? InstalledExecutable { get; private set; }
        public string? ResolvedFingerprint { get; private set; }

        public Task<PromotionCanonicalHealthResult> VerifyAsync(
            string sourceRoot,
            string installedExecutable,
            string expectedFingerprint,
            CancellationToken cancellationToken)
        {
            InstalledExecutable = installedExecutable;
            string manifestPath = Environment.GetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST")!;
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            ResolvedFingerprint = manifest.RootElement.GetProperty("promotedFingerprint").GetString();
            return Task.FromResult(new PromotionCanonicalHealthResult(
                Pass,
                "fixture-canonical-health",
                Pass ? null : "fixture post-commit doctor failed"));
        }
    }
}
