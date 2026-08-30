using System.Security.Cryptography;
using System.Text.Json;

namespace RimLiaison.DevBridge;

public sealed record ResolvedValidationRecipe(
    string Id,
    string OwnerProject,
    string Source,
    string Path,
    string Sha256,
    string SchemaVersion);

public sealed record ValidationRecipeResolutionResult(
    ResolvedValidationRecipe? Recipe,
    string? ErrorCode,
    string? Error)
{
    public bool IsSuccess => Recipe is not null && ErrorCode is null;
}

/// <summary>
/// Resolves validation recipes from the requesting project first. A project
/// recipe uses its explicit project-relative path when declared, otherwise the
/// canonical .rimdev/recipes/{id}.json convention. Only after that bounded
/// project lookup may a legacy central compatibility path be considered.
/// Source/worktree searching is intentionally forbidden.
/// </summary>
public static class ValidationRecipeResolver
{
    private static readonly HashSet<string> BuiltinIds = new(StringComparer.Ordinal)
    {
        "quicktest-smoke"
    };

    public static ValidationRecipeResolutionResult Resolve(
        string ownerProject,
        string recipeId,
        string repositoryRoot,
        string? projectRecipePath,
        string? toolchainRoot)
    {
        if (string.IsNullOrWhiteSpace(ownerProject) || string.IsNullOrWhiteSpace(recipeId))
            return Failure("PROJECT_RECIPE_REQUEST_INVALID", "A project and recipe id are required.");

        if (!IsSafeToken(recipeId))
            return Failure("PROJECT_RECIPE_ID_INVALID", "Recipe ids must be portable single-file tokens.");

        string sourceRoot;
        try
        {
            sourceRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Failure("PROJECT_REPOSITORY_ROOT_INVALID", "The project repository root is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(projectRecipePath))
        {
            if (!IsSafeRelativePath(projectRecipePath))
                return Failure("PROJECT_RECIPE_PATH_INVALID", "Project recipe paths must be relative JSON paths without parent traversal.");

            string path = Path.GetFullPath(Path.Combine(sourceRoot,
                projectRecipePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(path, sourceRoot))
                return Failure("PROJECT_RECIPE_PATH_INVALID", "The project recipe path must remain inside the owning repository.");
            return ReadAndValidate(path, ownerProject, recipeId, "PROJECT_OWNED");
        }

        if (BuiltinIds.Contains(recipeId))
        {
            string? centralBuiltinPath = ExactCentralPath(toolchainRoot, recipeId);
            return centralBuiltinPath is null
                ? Failure("TEST_RECIPE_NOT_FOUND", "The declared toolchain builtin recipe is unavailable.")
                : ReadAndValidate(centralBuiltinPath, ownerProject, recipeId, "TOOLCHAIN_BUILTIN");
        }

        string projectConventionPath = Path.Combine(
            sourceRoot,
            ".rimdev",
            "recipes",
            recipeId + ".json");
        if (IsWithin(projectConventionPath, sourceRoot) && File.Exists(projectConventionPath))
            return ReadAndValidate(projectConventionPath, ownerProject, recipeId, "PROJECT_OWNED");

        // Compatibility is bounded to one exact legacy file and one
        // unambiguous project owner. It is never a search across paths.
        string? centralPath = ExactCentralPath(toolchainRoot, recipeId);
        if (centralPath is null)
            return Failure("PROJECT_RECIPE_NOT_FOUND",
                $"The project-owned recipe '{recipeId}' was not found at the canonical project path.");
        return ReadAndValidate(centralPath, ownerProject, recipeId, "LEGACY_CENTRAL_PROJECT_RECIPE");
    }


    private static bool IsSafeToken(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            character is (>= 'A' and <= 'Z') or
            (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or '.' or '_' or '-');

    private static ValidationRecipeResolutionResult ReadAndValidate(
        string path,
        string ownerProject,
        string recipeId,
        string source)
    {
        if (!File.Exists(path))
            return Failure("PROJECT_RECIPE_NOT_FOUND", $"Validation recipe '{recipeId}' was not found at the resolved path.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            string? schema = GetString(root, "schemaVersion");
            if (schema is not ("devbridge-test-recipe/v1" or "devbridge-test-recipe/v2"))
                return Failure("PROJECT_RECIPE_SCHEMA_UNSUPPORTED", "The resolved recipe schema is unsupported.");
            if (!string.Equals(GetString(root, "id"), recipeId, StringComparison.Ordinal))
                return Failure("PROJECT_RECIPE_ID_MISMATCH", "The resolved recipe id does not match project metadata.");
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), recipeId, StringComparison.OrdinalIgnoreCase))
                return Failure("PROJECT_RECIPE_ID_FILENAME_MISMATCH", "Recipe file names must match their declared id.");
            if (!root.TryGetProperty("projects", out JsonElement projects) || projects.ValueKind != JsonValueKind.Array)
                return Failure("PROJECT_RECIPE_OWNER_MISSING", "The resolved recipe does not declare an owning project.");

            string[] owners = projects.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (source != "TOOLCHAIN_BUILTIN")
            {
                if (!owners.Contains(ownerProject, StringComparer.Ordinal))
                    return Failure("DEVELOPMENT_RECIPE_PROJECT_MISMATCH", $"Recipe '{recipeId}' does not request project '{ownerProject}'.");
                if (owners.Length != 1)
                    return Failure("PROJECT_RECIPE_OWNERSHIP_AMBIGUOUS", "A project-specific recipe must declare exactly one owner.");
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return new(new ResolvedValidationRecipe(recipeId, ownerProject, source,
                Path.GetFullPath(path), sha256, schema!), null, null);
        }
        catch (JsonException)
        {
            return Failure("PROJECT_RECIPE_INVALID_JSON", "The resolved validation recipe is not valid JSON.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure("PROJECT_RECIPE_READ_FAILED", "The resolved validation recipe could not be read.");
        }
    }

    private static string? ExactCentralPath(string? toolchainRoot, string recipeId)
    {
        if (string.IsNullOrWhiteSpace(toolchainRoot))
            return null;
        try
        {
            string root = Path.GetFullPath(toolchainRoot);
            return Path.Combine(root, "TestRecipes", recipeId + ".json");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        string normalized = path.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not ".." &&
            segment.All(character => !char.IsControl(character)));
    }

    private static bool IsWithin(string candidate, string root)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ValidationRecipeResolutionResult Failure(string code, string error) =>
        new(null, code, error);
}
