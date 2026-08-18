using System.Text.Json;
using System.Security;

namespace RimLiaison.Catalog;

public static class CatalogLoader
{
    public const int MaxCatalogBytes = 512 * 1024;

    public static CatalogLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("CATALOG_PATH_INVALID", "A catalog path is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or SecurityException)
        {
            return Failure("CATALOG_PATH_INVALID", TrimMessage(exception.Message));
        }

        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return Failure("CATALOG_NOT_FOUND", $"Catalog file was not found: {fullPath}.");
            }

            if (file.Length > MaxCatalogBytes)
            {
                return Failure(
                    "CATALOG_TOO_LARGE",
                    $"Catalog exceeds the {MaxCatalogBytes}-byte limit.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            return Failure("CATALOG_READ_FAILED", TrimMessage(exception.Message));
        }

        try
        {
            string json = File.ReadAllText(fullPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Failure("CATALOG_EMPTY", "Catalog file is empty.");
            }

            CatalogDocument? catalog = CatalogJson.Deserialize<CatalogDocument>(json);
            return catalog is null
                ? Failure("CATALOG_EMPTY", "Catalog JSON produced no document.")
                : new CatalogLoadResult(catalog, []);
        }
        catch (JsonException exception)
        {
            return Failure("CATALOG_JSON_INVALID", TrimMessage(exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            return Failure("CATALOG_READ_FAILED", TrimMessage(exception.Message));
        }
    }

    private static CatalogLoadResult Failure(string code, string message)
    {
        return new CatalogLoadResult(null, [new CatalogIssue(code, message)]);
    }

    private static string TrimMessage(string message)
    {
        return message.Length <= 240 ? message : message[..240];
    }
}
