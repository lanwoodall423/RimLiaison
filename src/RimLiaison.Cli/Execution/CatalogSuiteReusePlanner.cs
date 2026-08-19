using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimLiaison.Catalog;

namespace RimLiaison.Execution;

public sealed record CatalogSuiteReuseGroup(
    string ReuseKey,
    CatalogRecipeIsolationMode Mode,
    IReadOnlyList<string> TestIds,
    string? ResetRecipe,
    string? ProfileSignature = null);

public sealed record CatalogSuiteRecipeProfile(
    string Signature,
    IReadOnlyList<string> Projects,
    IReadOnlyList<KeyValuePair<string, string>> Inputs);

public sealed record CatalogSuiteReusePlan(
    int Selected,
    IReadOnlyList<CatalogSuiteReuseGroup> Groups,
    string? FallbackReason = null)
{
    public bool HasReusableGroups => Groups.Count > 0;
}

public static class CatalogSuiteReusePlanner
{
    public static CatalogSuiteReusePlan Plan(
        CatalogDocument catalog,
        IReadOnlyList<string> orderedTestIds)
    {
        return Plan(catalog, orderedTestIds, recipeProfiles: null);
    }

    public static CatalogSuiteReusePlan Plan(
        CatalogDocument catalog,
        IReadOnlyList<string> orderedTestIds,
        IReadOnlyDictionary<string, CatalogSuiteRecipeProfile?>? recipeProfiles)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(orderedTestIds);

        var groups = new List<CatalogSuiteReuseGroup>();
        CatalogSuiteReuseGroupBuilder? current = null;
        string? fallbackReason = null;
        foreach (string testId in orderedTestIds)
        {
            CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
            if (test is null)
            {
                Flush(current, groups);
                current = null;
                continue;
            }

            CatalogRecipeIsolation isolation = CatalogRecipeIsolationPolicy.Resolve(test);
            string? reuseKey = CatalogRecipeIsolationPolicy.ShareKey(isolation);
            CatalogSuiteRecipeProfile? profile = recipeProfiles is not null &&
                recipeProfiles.TryGetValue(test.Recipe, out CatalogSuiteRecipeProfile? knownProfile)
                ? knownProfile
                : null;
            if (reuseKey is null || !CatalogRecipeIsolationPolicy.CanShareGeneration(isolation))
            {
                Flush(current, groups);
                current = null;
                continue;
            }

            bool canJoin = current is not null &&
                CatalogRecipeIsolationPolicy.CanJoin(current.Isolation, isolation) &&
                (recipeProfiles is null ||
                    current.ProfileSignature is not null &&
                    profile?.Signature is not null &&
                    string.Equals(current.ProfileSignature, profile.Signature,
                        StringComparison.Ordinal));
            if (!canJoin)
            {
                if (current is not null &&
                    string.Equals(current.ReuseKey, reuseKey, StringComparison.Ordinal) &&
                    CatalogRecipeIsolationPolicy.CanJoin(current.Isolation, isolation) &&
                    recipeProfiles is not null)
                {
                    fallbackReason ??= current.ProfileSignature is null || profile is null
                        ? "RIMTEST_REUSE_PROFILE_UNAVAILABLE"
                        : "RIMTEST_REUSE_PROFILE_INCOMPATIBLE";
                }
                Flush(current, groups);
                current = new CatalogSuiteReuseGroupBuilder(
                    test.Id,
                    isolation,
                    reuseKey,
                    profile?.Signature);
            }
            else
            {
                current!.TestIds.Add(test.Id);
            }
        }

        Flush(current, groups);
        return new CatalogSuiteReusePlan(
            orderedTestIds.Count,
            groups
                .Where(static group => group.TestIds.Count > 1)
                .Select(static group => group)
                .ToArray(),
            fallbackReason);
    }

    public static bool TryCreateRecipeProfile(
        JsonElement definition,
        out CatalogSuiteRecipeProfile? profile)
    {
        profile = null;
        if (definition.ValueKind != JsonValueKind.Object ||
            !TryGetStringArray(definition, "projects", out List<string> projects) ||
            !TryGetInputs(definition, out List<KeyValuePair<string, string>> inputs))
        {
            return false;
        }

        string material = string.Join('\u001f', projects) + '\u001e' +
            string.Join('\u001f', inputs.Select(static input =>
                input.Key + "=" + input.Value));
        string signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        profile = new CatalogSuiteRecipeProfile(signature, projects, inputs);
        return true;
    }

    private static void Flush(
        CatalogSuiteReuseGroupBuilder? group,
        ICollection<CatalogSuiteReuseGroup> groups)
    {
        if (group is not null)
        {
            groups.Add(group.ToRecord());
        }
    }

    private sealed class CatalogSuiteReuseGroupBuilder
    {
        internal CatalogSuiteReuseGroupBuilder(
            string testId,
            CatalogRecipeIsolation isolation,
            string reuseKey,
            string? profileSignature)
        {
            Isolation = isolation;
            ReuseKey = reuseKey;
            ProfileSignature = profileSignature;
            TestIds = [testId];
        }

        internal CatalogRecipeIsolation Isolation { get; }
        internal string ReuseKey { get; }
        internal string? ProfileSignature { get; }
        internal List<string> TestIds { get; }

        internal CatalogSuiteReuseGroup ToRecord() => new(
            ReuseKey,
            Isolation.Mode,
            TestIds.ToArray(),
            Isolation.ResetRecipe,
            ProfileSignature);
    }

    private static bool TryGetStringArray(
        JsonElement parent,
        string propertyName,
        out List<string> values)
    {
        values = [];
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                values = [];
                return false;
            }

            values.Add(item.GetString()!.Trim());
        }

        return true;
    }

    private static bool TryGetInputs(
        JsonElement parent,
        out List<KeyValuePair<string, string>> inputs)
    {
        inputs = [];
        if (!parent.TryGetProperty("inputs", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            string? inputValue = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.Null => "null",
                _ => null
            };
            if (inputValue is null)
            {
                inputs = [];
                return false;
            }

            inputs.Add(new KeyValuePair<string, string>(
                property.Name,
                inputValue));
        }

        inputs = inputs
            .OrderBy(static input => input.Key, StringComparer.Ordinal)
            .ToList();
        return true;
    }
}
