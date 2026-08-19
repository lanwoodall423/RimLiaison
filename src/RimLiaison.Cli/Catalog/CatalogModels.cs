using System.Text.Json.Serialization;

namespace RimLiaison.Catalog;

public static class CatalogSchema
{
    public const string Current = "rimtest-catalog/v1";
    public const string DevBridgeRecipeList = "devbridge-test-recipe-list/v1";
}

public enum CatalogCost
{
    Unknown,
    Low,
    Medium,
    High
}

public enum CatalogRecipeIsolationMode
{
    Unknown,
    PureRead,
    SameGenerationSafe,
    FixtureResettable,
    FreshGameRequired,
    FreshGenerationRequired
}

public sealed class CatalogRecipeIsolation
{
    [JsonPropertyName("mode")]
    public CatalogRecipeIsolationMode Mode { get; init; } = CatalogRecipeIsolationMode.Unknown;

    [JsonPropertyName("reuseKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReuseKey { get; init; }

    [JsonPropertyName("resetRecipe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetRecipe { get; init; }
}

public sealed class CatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("tests")]
    public required List<CatalogTest> Tests { get; init; }

    [JsonPropertyName("suites")]
    public required List<CatalogSuite> Suites { get; init; }
}

public sealed class CatalogTest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("recipe")]
    public required string Recipe { get; init; }

    [JsonPropertyName("artifactFreshnessAnchor")]
    public bool ArtifactFreshnessAnchor { get; init; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("covers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CatalogCoverage>? Covers { get; init; }

    [JsonPropertyName("cost")]
    public CatalogCost Cost { get; init; } = CatalogCost.Unknown;

    [JsonPropertyName("isolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogRecipeIsolation? Isolation { get; init; }
}

public sealed class CatalogCoverage
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class CatalogSuite
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("tests")]
    public List<string> Tests { get; init; } = [];

    [JsonPropertyName("suites")]
    public List<string> Suites { get; init; } = [];
}

public sealed record CatalogIssue(string Code, string Message, string? Path = null)
{
    public string Severity => "error";
}

public sealed class CatalogValidationResult
{
    public CatalogValidationResult(
        CatalogDocument catalog,
        IReadOnlyList<CatalogIssue> errors,
        bool recipesVerified)
    {
        Catalog = catalog;
        Errors = errors;
        RecipesVerified = recipesVerified;
    }

    public CatalogDocument Catalog { get; }
    public IReadOnlyList<CatalogIssue> Errors { get; }
    public bool IsValid => Errors.Count == 0;
    public bool RecipesVerified { get; }
}

public sealed record CatalogLoadResult(
    CatalogDocument? Catalog,
    IReadOnlyList<CatalogIssue> Errors);

public sealed record RecipeListLoadResult(
    IReadOnlySet<string>? RecipeIds,
    IReadOnlyList<CatalogIssue> Errors);
