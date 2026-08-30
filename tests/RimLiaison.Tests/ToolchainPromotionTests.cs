using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimLiaison.DevBridge;
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
                devBridgeRuntimeRoot = "unused",
                devBridgePackageSha256 = "unused",
                devBridgeCoordinatorSha256 = "unused",
                transactionConsumerPath = "unused",
                transactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                transactionConsumerSha256 = "unused",
                unifiedManifestRelativePath = "unified-package.json",
                compatibilityContract = "devbridge-mod-development/v1"
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
