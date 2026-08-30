using System.Text.Json;
using System.Security.Cryptography;
using RimLiaison.DevBridge;
using RimLiaison.Stack;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class ProjectMetadataOwnershipTests
{
    public static void ProductionManifestValidatesOwnerMetadata()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root);
            StackManifestResolution result = StackManifestResolver.Discover(root);
            Assert(result.Found, $"valid production manifest must resolve: {result.ErrorCode}");
            Assert(result.Manifest?.PackageId == "lan.frontier", "package identity must come from the owner manifest");
            Assert(result.Manifest?.ExpectedAssembly == "Frontier.dll", "assembly identity must come from the owner manifest");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void MissingProductionMetadataFailsClosed()
    {
        string root = CreateRepository();
        try
        {
            WriteManifest(root, "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Frontier\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\",\"workload\":\"production\"}");
            StackManifestResolution result = StackManifestResolver.Discover(root);
            Assert(result.Manifest is null, "incomplete production metadata must not produce a manifest");
            Assert(result.ErrorCode == "PROJECT_METADATA_MISSING", $"""expected PROJECT_METADATA_MISSING, got {result.ErrorCode}""");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void ContradictoryProductionMetadataFailsClosed()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root, expectedAssembly: "Other.dll");
            StackManifestResolution result = StackManifestResolver.Discover(root);
            Assert(result.Manifest is null, "contradictory production metadata must not produce a manifest");
            Assert(result.ErrorCode == "PROJECT_METADATA_IDENTITY_CONTRADICTION",
                $"expected identity contradiction, got {result.ErrorCode}");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void MaterializerUsesOwningManifestNotToolingCatalog()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root);
            ProjectOwnedDescriptorMaterialization? materialization =
                ProjectOwnedDescriptorMaterializer.Materialize(
                    "Frontier",
                    root,
                    Path.Combine(Path.GetTempPath(), "FrontierRuntime"),
                    out string? errorCode,
                    out string? error);
            Assert(materialization is not null, error ?? errorCode ?? "materialization failed");
            Assert(materialization!.DescriptorPath.Contains("rimliaison-project-contract-", StringComparison.OrdinalIgnoreCase),
                "execution descriptor must be temporary derived state");
            Assert(!materialization.DescriptorPath.Contains("DevelopmentProjects", StringComparison.OrdinalIgnoreCase),
                "production execution must not resolve a tooling catalog descriptor");
            Assert(
                materialization.Descriptor.ExpectedAssembly == "Frontier.dll",
                "materialized execution contract must preserve owner assembly identity");
            using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(materialization.DescriptorPath));
            Assert(
                !contract.RootElement.TryGetProperty("sourceRoot", out _),
                "sourceRoot must remain an adapter argument, not a DevBridge descriptor field");
            Assert(
                !contract.RootElement.TryGetProperty("runtimeRoot", out _),
                "runtimeRoot must remain an adapter argument, not a DevBridge descriptor field");
            ProjectOwnedDescriptorMaterializer.Delete(materialization);
        }
        finally
        {
            Delete(root);
        }
    }

    public static void DrfProjectOwnedMetadataPasses()
    {
        AssertProjectOwnerContract("DeferredRealityFramework", "deferred-reality");
    }

    public static void FrontierProjectOwnedMetadataPasses()
    {
        AssertProjectOwnerContract("Frontier", "frontier");
    }

    public static void InsightCanvasProjectOwnedMetadataPasses()
    {
        AssertProjectOwnerContract("InsightCanvas", "insight-canvas");
    }

    public static void WrongMetadataOwnerFailsClosed()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root);
            ProjectOwnedDescriptorMaterialization? materialization =
                ProjectOwnedDescriptorMaterializer.Materialize(
                    "not-frontier",
                    root,
                    Path.Combine(Path.GetTempPath(), "FrontierRuntime"),
                    out string? errorCode,
                    out string? error);
            Assert(materialization is null, "a forged project owner must fail closed");
            Assert(errorCode == "PROJECT_METADATA_OWNER_MISMATCH",
                $"expected PROJECT_METADATA_OWNER_MISMATCH, got {errorCode}: {error}");
        }
        finally
        {
            Delete(root);
        }
    }

    private static void AssertProjectOwnerContract(string project, string devBridgeProject)
    {
        string root = CreateRepository();
        try
        {
            string manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = "rimdev-stack/v1",
                project,
                devBridgeProject,
                catalog = "catalog.json",
                rimBridge = "disabled",
                workload = "production",
                projectType = "rimworld-content-mod",
                packageId = "lan.frontier",
                sourceProject = "Source/Frontier.csproj",
                configuration = "Release",
                expectedAssembly = "Frontier.dll",
                deploymentTarget = "1.6/Assemblies/Frontier.dll",
                testRecipe = "mod-development-smoke",
                runtimePackage = new
                {
                    sourceRoot = ".",
                    include = new[] { "About/**", "1.*/**" },
                    exclude = new[] { ".rimdev/**", "Source/**", "bin/**", "obj/**" }
                }
            });
            WriteManifest(root, manifest);
            string runtimeRoot = Path.Combine(Path.GetTempPath(), project + "-Runtime");
            ProjectOwnedDescriptorMaterialization? materialization =
                ProjectOwnedDescriptorMaterializer.Materialize(
                    devBridgeProject,
                    root,
                    runtimeRoot,
                    out string? errorCode,
                    out string? error);
            try
            {
                Assert(materialization is not null, error ?? errorCode ?? "materialization failed");
                using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(materialization!.DescriptorPath));
                Assert(contract.RootElement.GetProperty("project").GetString() == devBridgeProject,
                    "execution project identity must remain the DevBridge routing identity");
                Assert(contract.RootElement.GetProperty("metadataOwner").GetString() == project,
                    "metadata owner must remain the repository project identity");
                Assert(contract.RootElement.GetProperty("metadataSource").GetString() ==
                    Path.Combine(root, ".rimdev", "stack.json"),
                    "metadata source must remain the project-owned manifest");
                Assert(contract.RootElement.GetProperty("contractProducer").GetString() == "RimLiaison",
                    "contract producer must remain RimLiaison");
                Assert(contract.RootElement.GetProperty("materializedContractPath").GetString() ==
                    materialization.DescriptorPath,
                    "contract location must be explicit and distinct from ownership");
            }
            finally
            {
                if (materialization is not null)
                {
                    ProjectOwnedDescriptorMaterializer.Delete(materialization);
                }
            }
        }
        finally
        {
            Delete(root);
        }
    }

    public static void MissingRuntimeRootFailsClosed()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root);
            ProjectOwnedDescriptorMaterialization? materialization =
                ProjectOwnedDescriptorMaterializer.Materialize(
                    "Frontier",
                    root,
                    null,
                    out string? errorCode,
                    out string? error);
            Assert(materialization is null, "missing runtime root must not materialize an execution contract");
            Assert(errorCode == "PROJECT_RUNTIME_ROOT_MISSING",
                $"expected PROJECT_RUNTIME_ROOT_MISSING, got {errorCode}: {error}");
        }
        finally
        {
            Delete(root);
        }
    }

    public static void ProductionBindingRequiresExactInstalledIdentity()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-toolchain-binding-" + Guid.NewGuid().ToString("N"));
        string cliPath = Path.Combine(root, "promoted", "rimliaison.exe");
        string sourceCliPath = Path.Combine(root, "source", "rimliaison.exe");
        string assemblyPath = Path.Combine(root, "rimliaison.dll");
        string runtimeRoot = Path.Combine(root, "runtime");
        string consumerPath = Path.Combine(root, "promoted", "transaction-components", "mod-test.ps1");
        string unifiedManifestPath = Path.Combine(root, "promoted", "unified-package.json");
        string coordinatorPath = Path.Combine(runtimeRoot, "Coordinator", "DevBridge.Coordinator.exe");
        string manifestPath = Path.Combine(root, "production-toolchain.json");
        string? priorManifest = Environment.GetEnvironmentVariable(
            "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
        string? priorSource = Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceCliPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coordinatorPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(consumerPath)!);
            File.WriteAllText(cliPath, "promoted");
            File.WriteAllText(assemblyPath, "assembly");
            File.WriteAllText(sourceCliPath, "source");
            File.WriteAllText(Path.Combine(runtimeRoot, "DevBridge.cmd"), "runtime");
            File.WriteAllText(coordinatorPath, "coordinator");
            File.WriteAllText(consumerPath, "consumer");
            File.WriteAllText(
                unifiedManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-unified-production-package/v2",
                    productFingerprint = "tc-promoted",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimBridgeServer = new
                    {
                        boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary
                    }
                }));
            File.WriteAllText(
                Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    packageSha256 = "package-hash",
                    files = new[]
                    {
                        new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = Sha256(coordinatorPath) }
                    }
                }));
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = "tc-promoted",
                    rimLiaisonAssemblyPath = assemblyPath,
                    rimLiaisonAssemblySha256 = Sha256(assemblyPath),
                    rimLiaisonExecutablePath = cliPath,
                    rimLiaisonExecutableSha256 = Sha256(cliPath),
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    devBridgeRuntimeRoot = runtimeRoot,
                    devBridgePackageSha256 = "package-hash",
                    devBridgeCoordinatorSha256 = Sha256(coordinatorPath),
                    transactionConsumerPath = consumerPath,
                    transactionConsumerSha256 = Sha256(consumerPath),
                    unifiedManifestPath,
                    unifiedManifestSha256 = Sha256(unifiedManifestPath),
                    runtimeProtocolContract = "devbridge-mod-development/v1"
                }));
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                manifestPath);
            Environment.SetEnvironmentVariable(
                "DEVBRIDGE_SOURCE_ROOT",
                Path.Combine(root, "source"));

            ProductionToolchainBindingResolution rejected =
                ProductionToolchainBindingResolver.Resolve(
                    root,
                    currentExecutablePath: sourceCliPath);
            Assert(
                rejected.Failure?.ErrorCode == "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                "source executable must be rejected before production execution");

            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", runtimeRoot);
            ProductionToolchainBindingResolution accepted =
                ProductionToolchainBindingResolver.Resolve(
                    root,
                    currentExecutablePath: cliPath);
            Assert(accepted.Succeeded, accepted.Failure?.Error ?? "promoted identity was rejected");
            Assert(accepted.Binding!.OwnerProduct == ToolchainPromotionSchemas.OwnerProduct &&
                   accepted.Binding.RuntimeSubsystem == ToolchainPromotionSchemas.RuntimeSubsystem,
                "production binding must identify RimLiaison.Runtime as an internal component");
            string evidence = JsonSerializer.Serialize(accepted.Binding.ToEvidence());
            Assert(evidence.Contains("\"runtime\"", StringComparison.Ordinal) &&
                   evidence.Contains("\"rimBridgeServer\"", StringComparison.Ordinal),
                "production evidence must retain the runtime and RimBridgeServer boundaries");
            Assert(
                accepted.Binding!.Fingerprint.StartsWith("tc-", StringComparison.Ordinal),
                "production fingerprint must be the single unified product identity");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                priorManifest);
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", priorSource);
            Delete(root);
        }
    }


    public static void SourceRuntimeRootFailsClosed()
    {
        string root = CreateRepository();
        try
        {
            WriteProductionManifest(root);
            ProjectOwnedDescriptorMaterialization? materialization =
                ProjectOwnedDescriptorMaterializer.Materialize(
                    "Frontier",
                    root,
                    Path.Combine(root, "Runtime"),
                    out string? errorCode,
                    out string? error);
            Assert(materialization is null, "source-relative runtime root must not materialize an execution contract");
            Assert(errorCode == "PROJECT_RUNTIME_ROOT_INVALID",
                $"expected PROJECT_RUNTIME_ROOT_INVALID, got {errorCode}: {error}");
        }
        finally
        {
            Delete(root);
        }
    }

    private static string CreateRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-project-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, ".rimdev"));
        Directory.CreateDirectory(Path.Combine(root, "Source"));
        Directory.CreateDirectory(Path.Combine(root, "About"));
        File.WriteAllText(
            Path.Combine(root, "Source", "Frontier.csproj"),
            "<Project><PropertyGroup><AssemblyName>Frontier</AssemblyName></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "About", "About.xml"),
            "<ModMetaData><packageId>lan.frontier</packageId></ModMetaData>");
        return root;
    }

    private static void WriteProductionManifest(string root, string expectedAssembly = "Frontier.dll") =>
        WriteManifest(root, JsonSerializer.Serialize(new
        {
            schemaVersion = "rimdev-stack/v1",
            project = "Frontier",
            catalog = "catalog.json",
            rimBridge = "disabled",
            workload = "production",
            projectType = "rimworld-content-mod",
            packageId = "lan.frontier",
            sourceProject = "Source/Frontier.csproj",
            configuration = "Release",
            expectedAssembly,
            deploymentTarget = "1.6/Assemblies/Frontier.dll",
            testRecipe = "mod-development-smoke",
            runtimePackage = new
            {
                sourceRoot = ".",
                include = new[] { "About/**", "1.*/**" },
                exclude = new[] { ".rimdev/**", "Source/**", "bin/**", "obj/**" }
            }
        }));

    private static void WriteManifest(string root, string json) =>
        File.WriteAllText(Path.Combine(root, ".rimdev", "stack.json"), json);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Delete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not obscure the assertion.
        }
    }
}
