using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Stack;

public static class RimDevStackSchema
{
    public const string Current = "rimdev-stack/v1";
}

public sealed class RimDevStackManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimDevStackSchema.Current;

    [JsonPropertyName("project")]
    public string Project { get; init; } = string.Empty;

    [JsonPropertyName("devBridgeProject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DevBridgeProject { get; init; }

    [JsonPropertyName("catalog")]
    public string Catalog { get; init; } = string.Empty;

    [JsonPropertyName("fallbackSuite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackSuite { get; init; }

    [JsonPropertyName("rimBridge")]
    public string RimBridge { get; init; } = string.Empty;

    [JsonPropertyName("workload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Workload { get; init; }

    [JsonPropertyName("projectType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectType { get; init; }

    [JsonPropertyName("packageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; init; }

    [JsonPropertyName("sourceProject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceProject { get; init; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; init; }

    [JsonPropertyName("expectedAssembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedAssembly { get; init; }

    [JsonPropertyName("deploymentTarget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentTarget { get; init; }

    [JsonPropertyName("testRecipe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestRecipe { get; init; }

    [JsonPropertyName("runtimePackage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RuntimePackage { get; init; }

    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Dependencies { get; init; }
}

internal sealed record StackManifestResolution(
    bool Found,
    string RepositoryRoot,
    string? ManifestPath,
    RimDevStackManifest? Manifest,
    string? ErrorCode,
    string? Error);

internal static class StackManifestResolver
{
    private const int MaximumManifestBytes = 32 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static StackManifestResolution Discover(string? startDirectory = null)
    {
        string start;
        try
        {
            start = Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return Failure(
                Path.GetFullPath(Environment.CurrentDirectory),
                "STACK_MANIFEST_DISCOVERY_FAILED",
                "The stack manifest search root is invalid.");
        }

        string repositoryRoot = FindRepositoryRoot(start);
        string? directory = start;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string manifestPath = Path.Combine(directory, ".rimdev", "stack.json");
            if (File.Exists(manifestPath))
            {
                return Load(directory, manifestPath);
            }

            if (string.Equals(directory, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            string? parent = Directory.GetParent(directory)?.FullName;
            if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = parent;
        }

        return new(
            false,
            repositoryRoot,
            null,
            null,
            "STACK_MANIFEST_MISSING",
            "No .rimdev/stack.json was found.");
    }

    public static string Serialize(RimDevStackManifest manifest) =>
        JsonSerializer.Serialize(manifest, WriteOptions);

    public static string CatalogPath(StackManifestResolution resolution)
    {
        if (resolution.Manifest is null)
        {
            return Path.Combine(
                Environment.CurrentDirectory,
                "TestCatalog",
                "rimtest.catalog.json");
        }

        return Path.Combine(resolution.RepositoryRoot, resolution.Manifest.Catalog);
    }

    private static StackManifestResolution Load(string repositoryRoot, string manifestPath)
    {
        try
        {
            FileInfo file = new(manifestPath);
            if (file.Length > MaximumManifestBytes)
            {
                return Failure(
                    repositoryRoot,
                    "STACK_MANIFEST_TOO_LARGE",
                    "The stack manifest exceeds its size limit.",
                    manifestPath);
            }

            string json = File.ReadAllText(manifestPath);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    repositoryRoot,
                    "STACK_MANIFEST_ROOT_INVALID",
                    "The stack manifest root must be a JSON object.",
                    manifestPath);
            }

            if (!TryGetString(document.RootElement, "schemaVersion", out string? schemaVersion))
            {
                return Failure(
                    repositoryRoot,
                    "STACK_MANIFEST_SCHEMA_MISSING",
                    "The stack manifest schemaVersion is required.",
                    manifestPath);
            }

            if (!string.Equals(schemaVersion, RimDevStackSchema.Current, StringComparison.Ordinal))
            {
                return Failure(
                    repositoryRoot,
                    "STACK_MANIFEST_SCHEMA_UNSUPPORTED",
                    "The stack manifest schemaVersion is not supported.",
                    manifestPath);
            }

            RimDevStackManifest? manifest = JsonSerializer.Deserialize<RimDevStackManifest>(
                json,
                ReadOptions);
            if (manifest is null)
            {
                return Failure(
                    repositoryRoot,
                    "STACK_MANIFEST_ROOT_INVALID",
                    "The stack manifest produced no document.",
                    manifestPath);
            }

            string? validationCode = Validate(manifest, repositoryRoot) ??
                ProjectMetadataValidator.Validate(manifest, repositoryRoot);
            return validationCode is null
                ? new(true, repositoryRoot, manifestPath, manifest, null, null)
                : Failure(
                    repositoryRoot,
                    validationCode,
                    "The stack manifest contains an invalid project configuration.",
                    manifestPath);
        }
        catch (JsonException)
        {
            return Failure(
                repositoryRoot,
                "STACK_MANIFEST_JSON_INVALID",
                "The stack manifest is not valid JSON.",
                manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            return Failure(
                repositoryRoot,
                "STACK_MANIFEST_READ_FAILED",
                "The stack manifest could not be read.",
                manifestPath);
        }
    }

    private static string? Validate(RimDevStackManifest manifest, string repositoryRoot)
    {
        if (!IsProjectName(manifest.Project) ||
            !IsRelativeCatalog(manifest.Catalog, repositoryRoot) ||
            !IsToken(manifest.RimBridge) ||
            (manifest.DevBridgeProject is not null && !IsToken(manifest.DevBridgeProject)) ||
            (manifest.FallbackSuite is not null && !IsToken(manifest.FallbackSuite)))
        {
            return "STACK_MANIFEST_FIELD_INVALID";
        }

        if (manifest.RimBridge is not ("via-devbridge" or "disabled"))
        {
            return "STACK_MANIFEST_RIMBRIDGE_MODE_INVALID";
        }

        return null;
    }

    private static bool IsProjectName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => !char.IsControl(character) && character is not '/' and not '\\');

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsRelativeCatalog(string? value, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 260 ||
            value.Any(char.IsControl) ||
            Path.IsPathRooted(value))
        {
            return false;
        }

        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, normalized));
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) +
                Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = null;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
    }

    private static string FindRepositoryRoot(string start)
    {
        string? directory = start;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) ||
                File.Exists(Path.Combine(directory, ".git")))
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

        return start;
    }

    private static StackManifestResolution Failure(
        string repositoryRoot,
        string code,
        string error,
        string? manifestPath = null) =>
        new(true, repositoryRoot, manifestPath, null, code, error);
}
