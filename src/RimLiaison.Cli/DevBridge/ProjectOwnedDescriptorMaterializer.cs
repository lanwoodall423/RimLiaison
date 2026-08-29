using System.Text.Json;
using RimLiaison.Stack;

namespace RimLiaison.DevBridge;

internal sealed record ProjectOwnedDescriptorMaterialization(
    string DescriptorPath,
    DevBridgeDevelopmentDescriptor Descriptor,
    string TemporaryRoot);

internal static class ProjectOwnedDescriptorMaterializer
{
    public static ProjectOwnedDescriptorMaterialization? Materialize(
        string project,
        string repositoryRoot,
        string? runtimeRoot,
        out string? errorCode,
        out string? error)
    {
        errorCode = null;
        error = null;
        StackManifestResolution resolution = StackManifestResolver.Discover(repositoryRoot);
        if (!resolution.Found || resolution.Manifest is null)
        {
            errorCode = "PROJECT_METADATA_MISSING";
            error = "The production repository has no readable .rimdev/stack.json project manifest. " +
                "Create or repair it inside the owning repository; do not add a DevBridge descriptor.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            errorCode = "PROJECT_RUNTIME_ROOT_MISSING";
            error = "The project-owned runtime deployment root is not configured. " +
                "Configure the active RimWorld Mods root and package mapping; do not deploy into the source repository.";
            return null;
        }

        string sourceRoot = Path.GetFullPath(resolution.RepositoryRoot);
        string resolvedRuntimeRoot;
        try
        {
            resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            errorCode = "PROJECT_RUNTIME_ROOT_INVALID";
            error = "The project-owned runtime deployment root is invalid.";
            return null;
        }

        if (string.Equals(sourceRoot, resolvedRuntimeRoot, StringComparison.OrdinalIgnoreCase) ||
            resolvedRuntimeRoot.StartsWith(
                Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "PROJECT_RUNTIME_ROOT_INVALID";
            error = "The project-owned runtime deployment root cannot be inside the source repository.";
            return null;
        }

        RimDevStackManifest manifest = resolution.Manifest;
        if (!string.Equals(manifest.Project, project, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(manifest.DevBridgeProject, project, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "PROJECT_METADATA_OWNER_MISMATCH";
            error = "The project manifest identity does not match the resolved project.";
            return null;
        }

        errorCode = ProjectMetadataValidator.Validate(manifest, resolution.RepositoryRoot);
        if (errorCode is not null)
        {
            error = errorCode switch
            {
                "PROJECT_METADATA_MISSING" => "The production repository project manifest is incomplete.",
                "PROJECT_METADATA_IDENTITY_CONTRADICTION" => "The project manifest contradicts its package or build identity.",
                _ => "The production repository project manifest is invalid."
            };
            return null;
        }

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-project-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = DevBridgeModDevelopmentSchemas.Current,
                ["project"] = project,
                ["sourceProject"] = manifest.SourceProject,
                ["configuration"] = manifest.Configuration,
                ["expectedAssembly"] = manifest.ExpectedAssembly,
                ["deploymentTarget"] = manifest.DeploymentTarget,
                ["testRecipe"] = manifest.TestRecipe,
                ["runtimePackage"] = manifest.RuntimePackage!.Value
            };
            string descriptorPath = Path.Combine(temporaryRoot, "devbridge-execution-contract.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }));
            var descriptor = new DevBridgeDevelopmentDescriptor(
                DevBridgeModDevelopmentSchemas.Current,
                project,
                manifest.SourceProject!,
                manifest.Configuration!,
                manifest.ExpectedAssembly!,
                manifest.DeploymentTarget!,
                manifest.TestRecipe!,
                manifest.RuntimePackage);
            return new ProjectOwnedDescriptorMaterialization(descriptorPath, descriptor, temporaryRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            TryDelete(temporaryRoot);
            errorCode = "PROJECT_METADATA_EXECUTION_CONTRACT_FAILED";
            error = "The project-owned execution contract could not be materialized.";
            return null;
        }
    }

    public static void Delete(ProjectOwnedDescriptorMaterialization materialization)
    {
        TryDelete(materialization.TemporaryRoot);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception) when (path.Length > 0)
        {
            // A temporary execution contract is not an authoritative project artifact.
        }
    }
}
