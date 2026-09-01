using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimLiaison.Qualification;
using RimLiaison.Toolchain;
namespace RimLiaison.Tests;

internal static class ToolchainPromotionTests
{
    public static void PromotionRequiresPackage()
    {
        ToolchainPromotionResult result = ToolchainPromotionService.PromoteAsync(
                AppContext.BaseDirectory,
                null,
                null)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_PACKAGE_MISSING",
            "promotion without a package did not fail closed");
    }
    public static void StaticPromotionPathDoesNotAcquireLease()
    {
        var orchestrator = new CountingPromotionLeaseOrchestrator();
        ToolchainPromotionResult result = ToolchainPromotionService.PromoteAsync(
                AppContext.BaseDirectory,
                null,
                null,
                promotionLeaseOrchestrator: orchestrator)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == "blocked" &&
               result.ErrorCode == "PROMOTION_PACKAGE_MISSING" &&
               orchestrator.Calls == 0,
            "static promotion preflight unexpectedly acquired a live lease");
    }


    public static void MalformedPromotionPackageFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            string packagePath = Path.Combine(root, "package.json");
            File.WriteAllText(packagePath, "{}");
            ToolchainPromotionResult result = WithManifest(root, () => ToolchainPromotionService.PromoteAsync(
                    root,
                    packagePath,
                    null)
                .GetAwaiter()
                .GetResult());
            Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_PACKAGE_INVALID",
                "malformed promotion package was accepted");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void QualificationHashMismatchFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            string qualificationPath = Path.Combine(root, "qualification.json");
            File.WriteAllText(qualificationPath, "{\"SourceCommit\":\"source\",\"Passes\":1,\"TotalRuns\":1,\"InfrastructureFailures\":0,\"FixtureFailures\":0}");
            string packagePath = WritePackage(root, qualificationPath, "source", "bad");
            ToolchainPromotionResult result = WithManifest(root, () => ToolchainPromotionService.PromoteAsync(
                    root,
                    packagePath,
                    null)
                .GetAwaiter()
                .GetResult());
            Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_QUALIFICATION_HASH_MISMATCH",
                "qualification hash mismatch was accepted");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void IncompleteQualificationFailsClosed()
    {
        string root = CreateRoot();
        try
        {
            string qualificationPath = Path.Combine(root, "qualification.json");
            File.WriteAllText(qualificationPath, "{\"SourceCommit\":\"source\",\"Passes\":0,\"TotalRuns\":1,\"InfrastructureFailures\":0,\"FixtureFailures\":0}");
            string hash = Hash(qualificationPath);
            string packagePath = WritePackage(root, qualificationPath, "source", hash);
            ToolchainPromotionResult result = WithManifest(root, () => ToolchainPromotionService.PromoteAsync(
                    root,
                    packagePath,
                    null)
                .GetAwaiter()
                .GetResult());
            Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_QUALIFICATION_NOT_PROVEN",
                "incomplete qualification was accepted");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void CandidatePackageIsImmutableExactWithoutInstalledRuntime()
    {
        string root = CreateRoot();
        string? previousManifest = Environment.GetEnvironmentVariable(
            "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
        try
        {
            string artifactRoot = Path.Combine(root, "artifacts");
            string runtimeRoot = Path.Combine(root, "runtime");
            string activeRuntimeRoot = Path.Combine(root, "active-runtime-not-installed");
            string packageRoot = Path.Combine(root, "unified");
            Directory.CreateDirectory(Path.Combine(artifactRoot));
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "Coordinator"));
            Directory.CreateDirectory(packageRoot);
            string executablePath = Path.Combine(artifactRoot, "rimliaison.exe");
            string assemblyPath = Path.Combine(artifactRoot, "rimliaison.dll");
            string coordinatorPath = Path.Combine(runtimeRoot, "Coordinator", "DevBridge.Coordinator.exe");
            string consumerPath = Path.Combine(packageRoot, "transaction-components", "mod-test.ps1");
            string unifiedManifestPath = Path.Combine(packageRoot, "unified-package.json");
            File.WriteAllText(executablePath, "qualified-executable");
            File.WriteAllText(assemblyPath, "qualified-assembly");
            File.WriteAllText(coordinatorPath, "qualified-coordinator");
            File.WriteAllText(Path.Combine(runtimeRoot, "DevBridge.cmd"), "qualified-runtime-command");
            Directory.CreateDirectory(Path.GetDirectoryName(consumerPath)!);
            File.WriteAllText(consumerPath, "qualified-consumer");
            File.WriteAllText(unifiedManifestPath, "{}");
            string coordinatorHash = Hash(coordinatorPath);
            string consumerHash = Hash(consumerPath);
            File.WriteAllText(
                Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    packageSha256 = "runtime-package",
                    files = new[]
                    {
                        new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = coordinatorHash }
                    }
                }));
            string manifestPath = Path.Combine(root, "production-toolchain.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = "previous",
                    fingerprint = "previous",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimLiaisonExecutablePath = executablePath,
                    rimLiaisonExecutableSha256 = Hash(executablePath),
                    rimLiaisonAssemblyPath = assemblyPath,
                    rimLiaisonAssemblySha256 = Hash(assemblyPath),
                    devBridgeRuntimeRoot = activeRuntimeRoot,
                    devBridgePackageSha256 = "runtime-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    transactionConsumerPath = consumerPath,
                    transactionConsumerSha256 = consumerHash,
                    unifiedManifestPath,
                    unifiedManifestSha256 = Hash(unifiedManifestPath),
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                    qualificationArtifactPath = "previous-qualification.json",
                    qualificationArtifactSha256 = "previous-qualification-hash"
                }));
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                manifestPath);

            string qualificationPath = Path.Combine(root, "qualification.json");
            File.WriteAllText(qualificationPath, "{\"proof\":\"pass\"}");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            QualificationRunResult run = new(
                QualificationSchemas.Run,
                1,
                "run-1",
                "burn-in-25",
                "qualification",
                "experimental",
                QualificationOutcome.Pass,
                [],
                0,
                0,
                1,
                0,
                [],
                now,
                now);
            string executableHash = Hash(executablePath);
            string assemblyHash = Hash(assemblyPath);
            QualificationAggregate qualification = new(
                QualificationSchemas.Aggregate,
                "burn-in-25",
                "qualification",
                "experimental",
                1,
                1,
                0,
                0,
                1,
                0,
                new Dictionary<string, int>(),
                [run],
                now,
                now,
                "source-commit",
                new Dictionary<string, string>
                {
                    ["rimLiaisonExecutableSha256"] = executableHash,
                    ["rimLiaisonAssemblySha256"] = assemblyHash
                })
            {
                CandidateComplete = true,
                PromotionPackageEmitted = true
            };
            string packagePath = Path.Combine(root, "package.json");
            string packagePath2 = Path.Combine(root, "package-copy.json");
            ToolchainCandidate candidate = new(
                "source-commit",
                artifactRoot,
                executablePath,
                assemblyPath,
                activeRuntimeRoot,
                runtimeRoot,
                "runtime-package",
                coordinatorHash,
                consumerPath,
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                root,
                "component-commit",
                Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json"),
                Hash(Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json")));
            string generated = ToolchainPromotionService.WriteQualifiedPromotionPackage(
                qualification,
                qualificationPath,
                packagePath,
                candidate);
            string generatedCopy = ToolchainPromotionService.WriteQualifiedPromotionPackage(
                qualification,
                qualificationPath,
                packagePath2,
                candidate);
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(generated));
            JsonElement packageRootJson = package.RootElement;
            Assert(generated == Path.GetFullPath(packagePath), "package path must be absolute");
            Assert(packageRootJson.GetProperty("sourceCommit").GetString() == "source-commit",
                "package must bind the qualified source");
            Assert(packageRootJson.GetProperty("qualificationArtifactSha256").GetString() == Hash(qualificationPath),
                "package must hash the exact qualification artifact");
            Assert(packageRootJson.GetProperty("rimLiaisonExecutableSha256").GetString() == executableHash,
                "package must capture the qualified executable hash");
            Assert(packageRootJson.GetProperty("rimLiaisonAssemblySha256").GetString() == assemblyHash,
                "package must capture the qualified assembly hash");
            Assert(packageRootJson.GetProperty("devBridgeCoordinatorSha256").GetString() == coordinatorHash,
                "package must capture the installed coordinator hash");
            Assert(packageRootJson.GetProperty("transactionConsumerSha256").GetString() == consumerHash,
                "package must capture the transaction consumer hash");
            Assert(File.ReadAllText(generated) == File.ReadAllText(generatedCopy),
                "package serialization must be deterministic");
            string immutablePayloadRoot = packageRootJson.GetProperty("artifactRoot").GetString()!;
            Assert(immutablePayloadRoot != Path.GetFullPath(artifactRoot),
                "recovery payload must not point at the mutable local Release directory");
            Assert(File.ReadAllText(Path.Combine(immutablePayloadRoot, "rimliaison.exe")) == "qualified-executable",
                "recovery payload must preserve the qualified executable");
            bool immutable = false;
            try
            {
                ToolchainPromotionService.WriteQualifiedPromotionPackage(
                    qualification,
                    qualificationPath,
                    packagePath,
                    candidate);
            }
            catch (IOException)
            {
                immutable = true;
            }
            Assert(immutable, "qualified package must not be overwritten");
            File.WriteAllText(executablePath, "substituted-executable");
            bool substitutionRejected = false;
            try
            {
                ToolchainPromotionService.WriteQualifiedPromotionPackage(
                    qualification,
                    qualificationPath,
                    Path.Combine(root, "substituted.json"),
                    candidate);
            }
            catch (InvalidDataException)
            {
                substitutionRejected = true;
            }
            Assert(substitutionRejected, "artifact substitution must fail closed");
            File.WriteAllText(executablePath, "qualified-executable");
            File.WriteAllText(coordinatorPath, "substituted-coordinator");
            bool runtimeSubstitutionRejected = false;
            try
            {
                ToolchainPromotionService.WriteQualifiedPromotionPackage(
                    qualification,
                    qualificationPath,
                    Path.Combine(root, "substituted-runtime.json"),
                    candidate);
            }
            catch (InvalidDataException)
            {
                runtimeSubstitutionRejected = true;
            }
            Assert(runtimeSubstitutionRejected, "candidate runtime substitution must fail closed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                previousManifest);
            Delete(root);
        }
    }

    private static string WritePackage(string root, string qualificationPath, string sourceCommit, string qualificationHash)
    {
        string packagePath = Path.Combine(root, "package.json");
        File.WriteAllText(
            packagePath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = ToolchainPromotionSchemas.Package,
                sourceCommit,
                qualificationArtifactPath = qualificationPath,
                qualificationArtifactSha256 = qualificationHash,
                artifactRoot = root,
                rimLiaisonExecutableRelativePath = "rimliaison.exe",
                rimLiaisonAssemblyRelativePath = "rimliaison.dll",
                rimLiaisonExecutableSha256 = "unused",
                rimLiaisonAssemblySha256 = "unused",
                ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                devBridgeRuntimeRoot = "unused",
                devBridgePackageSha256 = "unused",
                devBridgeCoordinatorSha256 = "unused",
                transactionConsumerPath = "unused",
                transactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                transactionConsumerSha256 = "unused",
                unifiedManifestRelativePath = "unified-package.json",
                runtimeProtocolContract = "devbridge-mod-development/v1"
            }));
        return packagePath;
    }

    private static T WithManifest<T>(string root, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
        string path = Path.Combine(root, "production-toolchain.json");
        Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", path);
        try
        {
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", previous);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-promotion-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingPromotionLeaseOrchestrator : IPromotionLeaseOrchestrator
    {
        public int Calls { get; private set; }

        public Task<PromotionLiveVerificationResult> VerifyCapabilitiesAsync(
            string workflowId,
            int? expectedGeneration,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("static promotion path acquired a live lease");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
