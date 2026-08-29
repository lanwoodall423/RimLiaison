using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Stack;

namespace RimLiaison.RimDev;

internal sealed record RimDevWorkspaceDiscovery(
    bool Succeeded,
    string RootPath,
    IReadOnlyList<RimDevRepository> Repositories,
    string? ErrorCode = null,
    string? Error = null,
    RimDevWorkspaceConfiguration? Configuration = null);

internal static class RimDevWorkspaceDiscoverer
{
    private const int MaximumWorkspaceBytes = 128 * 1024;

    private static readonly JsonSerializerOptions WorkspaceOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    public static RimDevWorkspaceDiscovery Discover(
        string? explicitRoot,
        string startDirectory)
    {
        string start;
        try
        {
            start = Path.GetFullPath(
                string.IsNullOrWhiteSpace(explicitRoot)
                    ? startDirectory
                    : explicitRoot!);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return Failure(
                startDirectory,
                "RIMDEV_ROOT_INVALID",
                "The rimdev workspace root is invalid.");
        }

        if (!Directory.Exists(start))
        {
            return Failure(start, "RIMDEV_ROOT_NOT_FOUND", "The rimdev workspace root does not exist.");
        }

        string root = ResolveWorkspaceRoot(start, explicitRoot);
        string workspacePath = Path.Combine(root, ".rimdev", "workspace.json");
        RimDevWorkspaceConfiguration? configuration = null;
        if (File.Exists(workspacePath))
        {
            if (!TryReadConfiguration(workspacePath, root, out configuration, out string? errorCode, out string? error))
            {
                return Failure(root, errorCode ?? "RIMDEV_WORKSPACE_INVALID", error ?? "The rimdev workspace configuration is invalid.");
            }
        }

        var configuredRepositoryList = (
            configuration?.Repositories ?? DiscoverRepositoryEntries(root)).ToList();
        StackManifestResolution currentManifest = StackManifestResolver.Discover(startDirectory);
        if (string.IsNullOrWhiteSpace(explicitRoot) &&
            currentManifest.Manifest is not null &&
            !string.Equals(currentManifest.RepositoryRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            string currentPath = Path.GetRelativePath(root, currentManifest.RepositoryRoot);
            if (!Path.IsPathRooted(currentPath) &&
                !configuredRepositoryList.Any(entry =>
                    string.Equals(
                        Path.GetFullPath(Path.Combine(root, entry.Path)),
                        currentManifest.RepositoryRoot,
                        StringComparison.OrdinalIgnoreCase)))
            {
                configuredRepositoryList.Add(new RimDevWorkspaceRepository(
                    currentPath,
                    currentManifest.Manifest.Dependencies ?? [],
                    null,
                    currentManifest.Manifest.DeploymentTarget,
                    currentManifest.Manifest.SourceProject,
                    currentManifest.Manifest.Configuration));
            }
        }

        IReadOnlyList<RimDevWorkspaceRepository> configuredRepositories =
            configuredRepositoryList;
        if (configuredRepositories.Count == 0)
        {
            return Failure(
                root,
                "RIMDEV_NO_REPOSITORIES",
                "No managed repository with a .rimdev/stack.json manifest was found.");
        }

        var repositories = new List<RimDevRepository>();
        foreach (RimDevWorkspaceRepository entry in configuredRepositories)
        {
            if (!TryResolveRepositoryPath(root, entry.Path, out string repositoryPath))
            {
                repositories.Add(new RimDevRepository(
                    entry.Path,
                    entry.Path,
                    null,
                    InvalidManifest("RIMDEV_REPOSITORY_PATH_INVALID", "The configured repository path is unsafe."),
                    entry.Dependencies,
                    entry.DeploymentRoot,
                    entry.DeploymentTarget ?? null,
                    entry.BuildProject ?? null,
                    entry.Configuration ?? "Release"));
                continue;
            }

            string manifestPath = Path.Combine(repositoryPath, ".rimdev", "stack.json");
            StackManifestResolution resolution = StackManifestResolver.Discover(repositoryPath);
            StackManifestState manifest = ToManifestState(resolution);
            string name = manifest.Project ?? Path.GetFileName(repositoryPath);
            bool ownerMetadata = manifest.IsValid &&
                string.Equals(manifest.Workload, "production", StringComparison.Ordinal);
            IReadOnlyList<string> dependencies = ownerMetadata
                ? manifest.Dependencies ?? []
                : entry.Dependencies;
            repositories.Add(new RimDevRepository(
                name,
                repositoryPath,
                File.Exists(manifestPath) ? manifestPath : resolution.ManifestPath,
                manifest,
                dependencies,
                entry.DeploymentRoot ?? configuration?.DeploymentRoot,
                ownerMetadata ? manifest.DeploymentTarget : entry.DeploymentTarget ?? manifest.DeploymentTarget,
                ownerMetadata ? manifest.SourceProject : entry.BuildProject ?? manifest.SourceProject,
                ownerMetadata
                    ? manifest.Configuration ?? "Release"
                    : entry.Configuration ?? manifest.Configuration ?? "Release"));
        }

        RimDevRepository[] ordered = repositories
            .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repository => repository.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(true, root, ordered, Configuration: configuration);
    }

    private static string ResolveWorkspaceRoot(string start, string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return start;
        }

        string? environmentRoot = Environment.GetEnvironmentVariable("RIMDEV_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            try
            {
                return Path.GetFullPath(environmentRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return start;
            }
        }

        string? configured = FindWorkspaceManifest(start);
        if (configured is not null)
        {
            return configured;
        }

        StackManifestResolution current = StackManifestResolver.Discover(start);
        string repositoryRoot = current.RepositoryRoot;
        string? parent = Directory.GetParent(repositoryRoot)?.FullName;
        if (parent is not null && HasManagedChild(parent))
        {
            return parent;
        }

        return start;
    }

    private static string? FindWorkspaceManifest(string start)
    {
        string? directory = start;
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory, ".rimdev", "workspace.json")))
            {
                return directory;
            }

            string? parent = Directory.GetParent(directory)?.FullName;
            if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = parent;
        }

        return null;
    }

    private static bool HasManagedChild(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Any(directory => File.Exists(Path.Combine(directory, ".rimdev", "stack.json")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static IReadOnlyList<RimDevWorkspaceRepository> DiscoverRepositoryEntries(string root)
    {
        var paths = new List<string>();
        if (File.Exists(Path.Combine(root, ".rimdev", "stack.json")))
        {
            paths.Add(".");
        }

        try
        {
            paths.AddRange(
                Directory.EnumerateDirectories(root)
                    .Where(directory => File.Exists(Path.Combine(directory, ".rimdev", "stack.json")))
                    .Select(directory => Path.GetRelativePath(root, directory)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new RimDevWorkspaceRepository(path, [], null, null, null, null))
            .ToArray();
    }

    private static bool TryReadConfiguration(
        string path,
        string root,
        out RimDevWorkspaceConfiguration? configuration,
        out string? errorCode,
        out string? error)
    {
        configuration = null;
        errorCode = null;
        error = null;
        try
        {
            FileInfo file = new(path);
            if (file.Length > MaximumWorkspaceBytes)
            {
                errorCode = "RIMDEV_WORKSPACE_TOO_LARGE";
                error = "The rimdev workspace configuration exceeds its size limit.";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "RIMDEV_WORKSPACE_ROOT_INVALID";
                error = "The rimdev workspace configuration must be a JSON object.";
                return false;
            }

            RimDevWorkspaceConfigurationDocument? parsed = JsonSerializer.Deserialize<RimDevWorkspaceConfigurationDocument>(
                document.RootElement.GetRawText(),
                WorkspaceOptions);
            if (parsed is null || parsed.SchemaVersion != RimDevSchemas.Workspace)
            {
                errorCode = "RIMDEV_WORKSPACE_SCHEMA_UNSUPPORTED";
                error = "The rimdev workspace configuration schema is unsupported.";
                return false;
            }

            var repositories = new List<RimDevWorkspaceRepository>();
            foreach (RimDevWorkspaceRepositoryDocument entry in parsed.Repositories ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    errorCode = "RIMDEV_REPOSITORY_PATH_INVALID";
                    error = "Every configured repository requires a relative path.";
                    return false;
                }

                repositories.Add(new RimDevWorkspaceRepository(
                    entry.Path,
                    entry.DependsOn ?? [],
                    entry.DeploymentRoot,
                    entry.DeploymentTarget,
                    entry.BuildProject,
                    entry.Configuration));
            }

            string? deploymentRoot = null;
            if (!string.IsNullOrWhiteSpace(parsed.DeploymentRoot))
            {
                deploymentRoot = ResolveOptionalPath(root, parsed.DeploymentRoot);
                if (deploymentRoot is null)
                {
                    errorCode = "RIMDEV_DEPLOYMENT_ROOT_INVALID";
                    error = "The configured deployment root is invalid.";
                    return false;
                }
            }
            string? packageRoot = string.IsNullOrWhiteSpace(parsed.PackageRoot)
                ? deploymentRoot
                : ResolveOptionalPath(root, parsed.PackageRoot);
            if (!string.IsNullOrWhiteSpace(parsed.PackageRoot) && packageRoot is null)
            {
                errorCode = "RIMDEV_PACKAGE_ROOT_INVALID";
                error = "The configured package root is invalid.";
                return false;
            }

            string? activeModsRoot = string.IsNullOrWhiteSpace(parsed.ActiveModsRoot)
                ? null
                : ResolveOptionalPath(root, parsed.ActiveModsRoot);
            if (!string.IsNullOrWhiteSpace(parsed.ActiveModsRoot) && activeModsRoot is null)
            {
                errorCode = "RIMDEV_ACTIVE_MODS_ROOT_INVALID";
                error = "The configured active Mods root is invalid.";
                return false;
            }

            configuration = new RimDevWorkspaceConfiguration(
                parsed.SchemaVersion,
                repositories,
                deploymentRoot,
                parsed.RimWorldRoot,
                parsed.RimWorldExecutable,
                parsed.DevBridgeRuntimeRoot,
                parsed.DevBridgeSourceRoot,
                parsed.DevBridgePinnedWorktreeRoot,
                packageRoot,
                activeModsRoot,
                parsed.PackageMappings);
            return true;
        }
        catch (JsonException)
        {
            errorCode = "RIMDEV_WORKSPACE_JSON_INVALID";
            error = "The rimdev workspace configuration is not valid JSON.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            errorCode = "RIMDEV_WORKSPACE_READ_FAILED";
            error = "The rimdev workspace configuration could not be read.";
            return false;
        }
    }

    private static string? ResolveOptionalPath(string root, string value)
    {
        try
        {
            string full = Path.GetFullPath(
                Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(root, value));
            return full;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryResolveRepositoryPath(
        string root,
        string value,
        out string repositoryPath)
    {
        repositoryPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is ".."))
        {
            return false;
        }

        try
        {
            repositoryPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            return Directory.Exists(repositoryPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static StackManifestState ToManifestState(StackManifestResolution resolution)
    {
        if (resolution.Manifest is null)
        {
            return InvalidManifest(
                resolution.ErrorCode ?? "STACK_MANIFEST_INVALID",
                resolution.Error ?? "The .rimdev/stack.json manifest is unavailable.");
        }

        return new(
            true,
            resolution.Manifest.Project,
            resolution.Manifest.DevBridgeProject,
            resolution.Manifest.Catalog,
            resolution.Manifest.FallbackSuite,
            resolution.Manifest.RimBridge,
            resolution.Manifest.Workload,
            resolution.Manifest.ProjectType,
            resolution.Manifest.PackageId,
            resolution.Manifest.SourceProject,
            resolution.Manifest.Configuration,
            resolution.Manifest.ExpectedAssembly,
            resolution.Manifest.DeploymentTarget,
            resolution.Manifest.TestRecipe,
            resolution.Manifest.RuntimePackage,
            resolution.Manifest.RuntimeFolder,
            resolution.Manifest.Dependencies,
            null,
            null);
    }

    private static StackManifestState InvalidManifest(string errorCode, string error) =>
        new(false, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, errorCode, error);

    private static RimDevWorkspaceDiscovery Failure(string root, string code, string error) =>
        new(false, root, [], code, error);

    private sealed class RimDevWorkspaceConfigurationDocument
    {
        [JsonPropertyName("schemaVersion")]
        public string? SchemaVersion { get; init; }

        [JsonPropertyName("repositories")]
        public List<RimDevWorkspaceRepositoryDocument>? Repositories { get; init; }

        [JsonPropertyName("devBridgePinnedWorktreeRoot")]
        public string? DevBridgePinnedWorktreeRoot { get; init; }

        [JsonPropertyName("packageRoot")]
        public string? PackageRoot { get; init; }

        [JsonPropertyName("activeModsRoot")]
        public string? ActiveModsRoot { get; init; }

        [JsonPropertyName("packageMappings")]
        public Dictionary<string, string>? PackageMappings { get; init; }

        [JsonPropertyName("deploymentRoot")]
        public string? DeploymentRoot { get; init; }
        [JsonPropertyName("rimWorldRoot")]
        public string? RimWorldRoot { get; init; }

        [JsonPropertyName("rimWorldExecutable")]
        public string? RimWorldExecutable { get; init; }

        [JsonPropertyName("devBridgeRuntimeRoot")]
        public string? DevBridgeRuntimeRoot { get; init; }

        [JsonPropertyName("devBridgeSourceRoot")]
        public string? DevBridgeSourceRoot { get; init; }

    }

    private sealed class RimDevWorkspaceRepositoryDocument
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("dependsOn")]
        public List<string>? DependsOn { get; init; }

        [JsonPropertyName("deploymentRoot")]
        public string? DeploymentRoot { get; init; }

        [JsonPropertyName("deploymentTarget")]
        public string? DeploymentTarget { get; init; }

        [JsonPropertyName("buildProject")]
        public string? BuildProject { get; init; }

        [JsonPropertyName("configuration")]
        public string? Configuration { get; init; }
    }
}
