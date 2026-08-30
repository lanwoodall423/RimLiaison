using System.Security.Cryptography;
using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class ValidationRecipeResolverTests
{
    public static void ProjectOwnedRecipeResolves()
    {
        using TempRepo repo = TempRepo.Create("project");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", repo.Root, ".rimdev/recipes/demo-smoke.json", null);
        Assert(result.IsSuccess && result.Recipe is { Source: "PROJECT_OWNED" }, "project recipe must resolve from metadata");
    }

    public static void BuiltinRecipeResolves()
    {
        using TempRepo repo = TempRepo.Create("builtin");
        string central = Path.Combine(repo.Root, "toolchain");
        WriteRecipe(central, "quicktest-smoke", "quicktest-smoke", "tooling");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "quicktest-smoke", repo.Root, null, central);
        Assert(result.IsSuccess && result.Recipe!.Source == "TOOLCHAIN_BUILTIN", "generic builtin must remain toolchain-owned");
    }

    public static void MissingRecipeReturnsStructuredFailure()
    {
        using TempRepo repo = TempRepo.Create("missing");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "missing-smoke", repo.Root, ".rimdev/recipes/missing-smoke.json", null);
        Assert(result.ErrorCode == "PROJECT_RECIPE_NOT_FOUND", "missing project recipe must return a structured code");
    }

    public static void ProjectRecipeDoesNotRequireCentralRuntime()
    {
        using TempRepo repo = TempRepo.Create("no-central");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", repo.Root, ".rimdev/recipes/demo-smoke.json", null);
        Assert(result.IsSuccess, "project-owned recipe must resolve without a central catalog");
    }

    public static void RelativeRecipePathSurvivesRepositoryMove()
    {
        using TempRepo first = TempRepo.Create("move-a");
        string moved = Path.Combine(Path.GetTempPath(), "recipe-move-" + Guid.NewGuid().ToString("N"));
        Directory.Move(first.Root, moved);
        using TempRepo second = new(moved);
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", second.Root, ".rimdev/recipes/demo-smoke.json", null);
        Assert(result.IsSuccess && result.Recipe!.Path.StartsWith(moved, StringComparison.OrdinalIgnoreCase), "relative recipe metadata must survive a repository move");
    }

    public static void AbsoluteRecipePathIsRejected()
    {
        using TempRepo repo = TempRepo.Create("absolute");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", repo.Root, Path.Combine(repo.Root, ".rimdev/recipes/demo-smoke.json"), null);
        Assert(result.ErrorCode == "PROJECT_RECIPE_PATH_INVALID", "absolute project recipe metadata must be rejected");
    }

    public static void ForeignProjectRecipeIsRejected()
    {
        using TempRepo repo = TempRepo.Create("foreign", owner: "other");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", repo.Root, ".rimdev/recipes/demo-smoke.json", null);
        Assert(result.ErrorCode == "DEVELOPMENT_RECIPE_PROJECT_MISMATCH", "another project's recipe must be rejected");
    }

    public static void AmbiguousLegacyOwnershipFailsClosed()
    {
        using TempRepo repo = TempRepo.Create("ambiguous", owner: "demo", owners: ["demo", "other"]);
        string central = Path.Combine(repo.Root, "toolchain");
        WriteRecipe(central, "legacy-smoke", "legacy-smoke", "other", "demo");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "legacy-smoke", repo.Root, null, central);
        Assert(result.ErrorCode == "PROJECT_RECIPE_OWNERSHIP_AMBIGUOUS", "ambiguous legacy ownership must fail closed");
    }

    public static void LegacyProjectRecipeMigratesDeterministically()
    {
        using TempRepo repo = TempRepo.Create("legacy", owner: "demo");
        string central = Path.Combine(repo.Root, "toolchain");
        WriteRecipe(central, "legacy-smoke", "legacy-smoke", "demo");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "legacy-smoke", repo.Root, null, central);
        Assert(result.IsSuccess && result.Recipe!.Source == "LEGACY_CENTRAL_PROJECT_RECIPE", "exact single-owner legacy recipe must resolve in bounded compatibility mode");
    }

    public static void RecipeHashAndSchemaAreRecorded()
    {
        using TempRepo repo = TempRepo.Create("hash");
        string path = Path.Combine(repo.Root, ".rimdev", "recipes", "demo-smoke.json");
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(
            "demo", "demo-smoke", repo.Root, ".rimdev/recipes/demo-smoke.json", null);
        Assert(result.IsSuccess && result.Recipe!.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) &&
               result.Recipe.SchemaVersion == "devbridge-test-recipe/v1", "recipe hash and schema must be recorded");
    }

    public static void EcosystemRecipesResolve()
    {
        AssertResolve("C:\\RimDev\\Repos\\DeferredRealityFramework", "deferred-reality", "deferred-reality-development-smoke", ".rimdev/recipes/deferred-reality-development-smoke.json");
        AssertResolve("C:\\RimDev\\Repos\\Frontier", "frontier", "mod-development-smoke", ".rimdev/recipes/mod-development-smoke.json");
        AssertResolve("C:\\RimDev\\Repos\\InsightCanvas", "insight-canvas", "insightcanvas-in-game-suite", ".rimdev/recipes/insightcanvas-in-game-suite.json");
    }

    private static void AssertResolve(string root, string project, string id, string path)
    {
        ValidationRecipeResolutionResult result = ValidationRecipeResolver.Resolve(project, id, root, path, null);
        Assert(result.IsSuccess, $"{id} must resolve: {result.ErrorCode} {result.Error}");
    }

    private static void WriteRecipe(string root, string id, string recipeId, params string[] owners)
    {
        string path = Path.Combine(root, "TestRecipes", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = "devbridge-test-recipe/v1",
            id = recipeId,
            description = "test",
            projects = owners,
            inputs = new { quicktest = true },
            requiresReady = true,
            success = new { quicktestReady = true }
        }));

    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TempRepo : IDisposable
    {
        public string Root { get; }
        internal TempRepo(string root) => Root = root;
        public static TempRepo Create(string name, string owner = "demo", string[]? owners = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "recipe-resolver-" + name + "-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, ".rimdev", "recipes", "demo-smoke.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = "devbridge-test-recipe/v1",
                id = "demo-smoke",
                description = "test",
                projects = owners ?? [owner],
                inputs = new { quicktest = true },
                requiresReady = true,
                success = new { quicktestReady = true }
            }));
            return new TempRepo(root);
        }
        public void Dispose() => TryDelete(Root);
        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
