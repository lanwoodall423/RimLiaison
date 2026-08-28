using System.Text.Json;
using RimLiaison.DevBridge;
using RimLiaison.Stack;

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
                string.Equals(
                    contract.RootElement.GetProperty("sourceRoot").GetString(),
                    Path.GetFullPath(root),
                    StringComparison.OrdinalIgnoreCase),
                "the execution contract must preserve the owning source root");
            Assert(
                string.Equals(
                    contract.RootElement.GetProperty("runtimeRoot").GetString(),
                    Path.Combine(Path.GetTempPath(), "FrontierRuntime"),
                    StringComparison.OrdinalIgnoreCase),
                "the execution contract must preserve the resolved runtime root");
            ProjectOwnedDescriptorMaterializer.Delete(materialization);
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
