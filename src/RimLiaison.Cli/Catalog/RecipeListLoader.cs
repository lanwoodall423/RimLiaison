using System.Text.Json;
using System.Security;

namespace RimLiaison.Catalog;

public static class RecipeListLoader
{
    public static RecipeListLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("RECIPE_LIST_PATH_INVALID", "A recipe-list path is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or SecurityException)
        {
            return Failure("RECIPE_LIST_PATH_INVALID", TrimMessage(exception.Message));
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return Failure(
                    "RECIPE_LIST_NOT_FOUND",
                    $"Recipe-list file was not found: {fullPath}.");
            }

            using FileStream stream = File.OpenRead(fullPath);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    schema.GetString(),
                    CatalogSchema.DevBridgeRecipeList,
                    StringComparison.Ordinal))
            {
                return Failure(
                    "RECIPE_LIST_INVALID",
                    $"Recipe list must use schema {CatalogSchema.DevBridgeRecipeList}.");
            }

            if (!root.TryGetProperty("recipes", out JsonElement recipes) ||
                recipes.ValueKind != JsonValueKind.Array)
            {
                return Failure(
                    "RECIPE_LIST_INVALID",
                    "Recipe list must contain a recipes array.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement recipe in recipes.EnumerateArray())
            {
                string pathForId = $"recipes[{index}].id";
                if (recipe.ValueKind != JsonValueKind.Object ||
                    !recipe.TryGetProperty("id", out JsonElement idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    return Failure(
                        "RECIPE_LIST_INVALID",
                        "Each recipe must contain a non-empty id.",
                        pathForId);
                }

                string id = idElement.GetString()!;
                if (!ids.Add(id))
                {
                    return Failure(
                        "RECIPE_LIST_DUPLICATE_ID",
                        $"Recipe id is duplicated: {id}.",
                        pathForId);
                }

                index++;
            }

            return new RecipeListLoadResult(ids, []);
        }
        catch (JsonException exception)
        {
            return Failure("RECIPE_LIST_JSON_INVALID", TrimMessage(exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            return Failure("RECIPE_LIST_READ_FAILED", TrimMessage(exception.Message));
        }
    }

    private static RecipeListLoadResult Failure(
        string code,
        string message,
        string? path = null)
    {
        return new RecipeListLoadResult(
            null,
            [new CatalogIssue(code, message, path)]);
    }

    private static string TrimMessage(string message)
    {
        return message.Length <= 240 ? message : message[..240];
    }
}
