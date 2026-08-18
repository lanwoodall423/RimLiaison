namespace RimTest.Catalog;

public static class CatalogRecipeIsolationPolicy
{
    public static CatalogRecipeIsolation Resolve(CatalogTest? test)
    {
        return test?.Isolation ?? new CatalogRecipeIsolation();
    }

    public static bool CanShareGeneration(CatalogRecipeIsolation isolation) =>
        isolation.Mode is CatalogRecipeIsolationMode.PureRead or
            CatalogRecipeIsolationMode.SameGenerationSafe or
            CatalogRecipeIsolationMode.FixtureResettable;

    public static bool RequiresResetBetweenRecipes(CatalogRecipeIsolation isolation) =>
        isolation.Mode == CatalogRecipeIsolationMode.FixtureResettable;

    public static bool RequiresFreshGeneration(CatalogRecipeIsolation isolation) =>
        isolation.Mode is CatalogRecipeIsolationMode.FreshGameRequired or
            CatalogRecipeIsolationMode.FreshGenerationRequired;

    public static string? ShareKey(CatalogRecipeIsolation isolation) =>
        string.IsNullOrWhiteSpace(isolation.ReuseKey) ? null : isolation.ReuseKey.Trim();

    public static bool CanJoin(
        CatalogRecipeIsolation left,
        CatalogRecipeIsolation right)
    {
        if (!CanShareGeneration(left) || !CanShareGeneration(right) ||
            !string.Equals(ShareKey(left), ShareKey(right), StringComparison.Ordinal))
        {
            return false;
        }

        bool leftResettable = RequiresResetBetweenRecipes(left);
        bool rightResettable = RequiresResetBetweenRecipes(right);
        if (leftResettable || rightResettable)
        {
            return leftResettable && rightResettable &&
                string.Equals(left.ResetRecipe, right.ResetRecipe, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(left.ResetRecipe);
        }

        return true;
    }
}
