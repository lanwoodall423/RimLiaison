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

    public IReadOnlyList<string> ExecutionOrder { get; init; } = [];
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

        string[] selectedTestIds = orderedTestIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var groups = new List<CatalogSuiteReuseGroup>();
        var executionOrder = new List<string>(selectedTestIds.Length);
        var candidateSegment = new List<ReusableCandidate>();
        var profilesByCompatibility = new Dictionary<ReuseCompatibilityBase, string>();
        string? fallbackReason = null;
        foreach (string testId in selectedTestIds)
        {
            CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
            if (test is null)
            {
                FlushSegment(candidateSegment, executionOrder, groups);
                executionOrder.Add(testId);
                continue;
            }

            CatalogRecipeIsolation isolation = CatalogRecipeIsolationPolicy.Resolve(test);
            string? reuseKey = CatalogRecipeIsolationPolicy.ShareKey(isolation);
            bool hasReusableIsolation = reuseKey is not null &&
                CatalogRecipeIsolationPolicy.CanShareGeneration(isolation) &&
                CatalogRecipeIsolationPolicy.CanJoin(isolation, isolation);
            if (!hasReusableIsolation)
            {
                FlushSegment(candidateSegment, executionOrder, groups);
                executionOrder.Add(testId);
                continue;
            }

            var compatibilityBase = new ReuseCompatibilityBase(
                reuseKey!,
                isolation.Mode,
                isolation.ResetRecipe);
            if (recipeProfiles is null ||
                !recipeProfiles.TryGetValue(test.Recipe, out CatalogSuiteRecipeProfile? profile) ||
                profile is null ||
                string.IsNullOrWhiteSpace(profile.Signature))
            {
                fallbackReason ??= "RIMTEST_REUSE_PROFILE_UNAVAILABLE";
                FlushSegment(candidateSegment, executionOrder, groups);
                executionOrder.Add(testId);
                continue;
            }

            if (profilesByCompatibility.TryGetValue(
                    compatibilityBase,
                    out string? knownSignature) &&
                !string.Equals(knownSignature, profile.Signature,
                    StringComparison.Ordinal))
            {
                fallbackReason ??= "RIMTEST_REUSE_PROFILE_INCOMPATIBLE";
            }
            else
            {
                profilesByCompatibility[compatibilityBase] = profile.Signature;
            }

            candidateSegment.Add(new ReusableCandidate(
                test.Id,
                isolation,
                reuseKey!,
                profile.Signature));
        }

        FlushSegment(candidateSegment, executionOrder, groups);
        return new CatalogSuiteReusePlan(
            selectedTestIds.Length,
            groups.ToArray(),
            fallbackReason)
        {
            ExecutionOrder = executionOrder.ToArray()
        };
    }

    private static void FlushSegment(
        ICollection<ReusableCandidate> segment,
        ICollection<string> executionOrder,
        ICollection<CatalogSuiteReuseGroup> groups)
    {
        if (segment.Count == 0)
        {
            return;
        }

        var buckets = new Dictionary<ReuseCompatibilityKey, CandidateBucket>();
        int index = 0;
        foreach (ReusableCandidate candidate in segment)
        {
            var key = new ReuseCompatibilityKey(
                candidate.ReuseKey,
                candidate.Isolation.Mode,
                candidate.Isolation.ResetRecipe,
                candidate.ProfileSignature);
            if (!buckets.TryGetValue(key, out CandidateBucket? bucket))
            {
                bucket = new CandidateBucket(index, candidate);
                buckets.Add(key, bucket);
            }
            else
            {
                bucket.Candidates.Add(candidate);
            }

            index++;
        }

        foreach (CandidateBucket bucket in buckets.Values.OrderBy(
                     static bucket => bucket.FirstIndex))
        {
            foreach (ReusableCandidate candidate in bucket.Candidates)
            {
                executionOrder.Add(candidate.TestId);
            }
            if (bucket.Candidates.Count > 1)
            {
                ReusableCandidate first = bucket.Candidates[0];
                groups.Add(new CatalogSuiteReuseGroup(
                    first.ReuseKey,
                    first.Isolation.Mode,
                    bucket.Candidates.Select(static candidate => candidate.TestId).ToArray(),
                    first.Isolation.ResetRecipe,
                    first.ProfileSignature));
            }
        }

        segment.Clear();
    }

    private sealed record ReusableCandidate(
        string TestId,
        CatalogRecipeIsolation Isolation,
        string ReuseKey,
        string ProfileSignature);

    private sealed class CandidateBucket
    {
        internal CandidateBucket(int firstIndex, ReusableCandidate first)
        {
            FirstIndex = firstIndex;
            Candidates = [first];
        }

        internal int FirstIndex { get; }
        internal List<ReusableCandidate> Candidates { get; }
    }

    private readonly record struct ReuseCompatibilityBase(
        string ReuseKey,
        CatalogRecipeIsolationMode Mode,
        string? ResetRecipe);

    private readonly record struct ReuseCompatibilityKey(
        string ReuseKey,
        CatalogRecipeIsolationMode Mode,
        string? ResetRecipe,
        string ProfileSignature);

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
