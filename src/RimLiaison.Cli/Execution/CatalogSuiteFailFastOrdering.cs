using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Catalog;
using RimLiaison.Profiling;

namespace RimLiaison.Execution;

public sealed record CatalogSuiteFailFastOrderingSummary(
    [property: JsonPropertyName("used")] bool Used,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("policy")] string Policy);

public sealed record CatalogSuiteFailFastOrderingResult(
    IReadOnlyList<string> ExecutionOrder,
    CatalogSuiteFailFastOrderingSummary Summary,
    string HistoryContext);

/// <summary>
/// Applies a small, deterministic historical ordering hint to fail-fast runs.
/// The reuse planner remains authoritative: this class can only reorder the
/// members of an already-proven compatible reuse group.
/// </summary>
public static class CatalogSuiteFailFastOrdering
{
    public const string PolicyVersion = EfficiencyProfiler.HistoricalOrderingSchemaVersion;
    public const int MaximumHistoryAgeDays = 14;
    public const int MaximumProfilesToInspect = 32;
    public const int MinimumObservedRuns = 2;

    private const int MaximumOperationEntriesPerProfile = 64;
    private const int MaximumRecipeEvidence = 128;
    private const long MaximumRunsPerEntry = 100_000;
    private const long MaximumDurationMsPerEntry = 7L * 24 * 60 * 60 * 1000;
    private const int MaximumGenerationEntriesPerOperation = 8;
    private const int MaximumFutureSkewMinutes = 5;
    private const string NoHistoryReason = "no-history";
    private const string InvalidHistoryReason = "history-invalid-or-stale";
    private const string IncompatibleHistoryReason = "history-incompatible";
    private const string InsufficientHistoryReason = "history-insufficient";
    private const string NoCompatibleGroupsReason = "no-compatible-groups";
    private const string NoOrderChangeReason = "history-no-order-change";
    private const string AppliedReason = "history-applied";

    public static CatalogSuiteFailFastOrderingResult Order(
        CatalogDocument catalog,
        CatalogSuiteReusePlan reusePlan,
        string? profileDirectory = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reusePlan);

        string historyContext = BuildHistoryContext(catalog, reusePlan);
        return Order(
            catalog,
            reusePlan,
            historyContext,
            profileDirectory,
            now ?? DateTimeOffset.UtcNow);
    }

    public static string BuildHistoryContext(
        CatalogDocument catalog,
        CatalogSuiteReusePlan reusePlan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reusePlan);

        Dictionary<string, CatalogSuiteReuseGroup> groupsByTest = reusePlan.Groups
            .SelectMany(group => group.TestIds.Select(testId => (testId, group)))
            .GroupBy(value => value.testId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().group,
                StringComparer.Ordinal);

        var material = new StringBuilder(256);
        AppendContextPart(material, CatalogSchema.Current);
        AppendContextPart(material, PolicyVersion);
        foreach (string testId in reusePlan.ExecutionOrder
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static value => value, StringComparer.Ordinal))
        {
            CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
            CatalogRecipeIsolation isolation = CatalogRecipeIsolationPolicy.Resolve(test);
            AppendContextPart(material, testId);
            AppendContextPart(material, test?.Recipe ?? string.Empty);
            AppendContextPart(material, isolation.Mode.ToString());
            AppendContextPart(material, CatalogRecipeIsolationPolicy.ShareKey(isolation) ?? string.Empty);
            AppendContextPart(material, isolation.ResetRecipe ?? string.Empty);
            AppendContextPart(
                material,
                groupsByTest.TryGetValue(testId, out CatalogSuiteReuseGroup? group)
                    ? group.ProfileSignature ?? string.Empty
                    : string.Empty);
        }

        return "h-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private static CatalogSuiteFailFastOrderingResult Order(
        CatalogDocument catalog,
        CatalogSuiteReusePlan reusePlan,
        string historyContext,
        string? profileDirectory,
        DateTimeOffset now)
    {
        IReadOnlyList<string> deterministicOrder = reusePlan.ExecutionOrder.ToArray();
        HistoryReadResult history = ReadHistory(
            profileDirectory,
            historyContext,
            now);

        if (history.FilesFound == 0)
        {
            return Result(deterministicOrder, NoHistoryReason, historyContext);
        }

        if (history.ValidProfiles == 0)
        {
            return Result(deterministicOrder, InvalidHistoryReason, historyContext);
        }

        if (history.ContextMatches == 0)
        {
            return Result(deterministicOrder, IncompatibleHistoryReason, historyContext);
        }

        Dictionary<string, OrderingScore> scores = history.Evidence
            .Select(pair => (pair.Key, Score: CreateScore(pair.Value)))
            .Where(value => value.Score is not null)
            .ToDictionary(
                value => value.Key,
                value => value.Score!.Value,
                StringComparer.Ordinal);
        if (scores.Count == 0)
        {
            return Result(deterministicOrder, InsufficientHistoryReason, historyContext);
        }

        if (reusePlan.Groups.Count == 0)
        {
            return Result(deterministicOrder, NoCompatibleGroupsReason, historyContext);
        }

        string[] ordered = ApplyWithinGroups(
            catalog,
            reusePlan,
            deterministicOrder,
            scores);
        string reason = ordered.SequenceEqual(deterministicOrder, StringComparer.Ordinal)
            ? NoOrderChangeReason
            : AppliedReason;
        return Result(
            ordered,
            reason,
            historyContext,
            used: !string.Equals(reason, NoOrderChangeReason, StringComparison.Ordinal));
    }

    private static CatalogSuiteFailFastOrderingResult Result(
        IReadOnlyList<string> order,
        string reason,
        string historyContext,
        bool used = false) =>
        new(
            order,
            new CatalogSuiteFailFastOrderingSummary(used, reason, PolicyVersion),
            historyContext);

    private static string[] ApplyWithinGroups(
        CatalogDocument catalog,
        CatalogSuiteReusePlan reusePlan,
        IReadOnlyList<string> deterministicOrder,
        IReadOnlyDictionary<string, OrderingScore> scores)
    {
        HashSet<string> selected = deterministicOrder.ToHashSet(StringComparer.Ordinal);
        if (selected.Count != deterministicOrder.Count)
        {
            // The planner normally supplies a distinct order. If a malformed
            // plan ever violates that invariant, preserve it exactly rather
            // than allowing the hint layer to change membership or counts.
            return deterministicOrder.ToArray();
        }

        Dictionary<string, int> membershipCounts = reusePlan.Groups
            .Where(static group => group.TestIds.Count > 1)
            .SelectMany(group => group.TestIds)
            .GroupBy(static testId => testId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        CatalogSuiteReuseGroup[] safeGroups = reusePlan.Groups
            .Where(static group => group.TestIds.Count > 1)
            .Where(group => group.TestIds.Count == group.TestIds.Distinct(StringComparer.Ordinal).Count())
            .Where(group => group.TestIds.All(
                testId =>
                    selected.Contains(testId) &&
                    membershipCounts.TryGetValue(testId, out int count) &&
                    count == 1))
            // A partially observed group is deliberately left in planner
            // order. A known score for only one member must not turn missing
            // history into a learned preference for that member.
            .Where(group => group.TestIds.All(testId =>
                ScoreFor(catalog, testId, scores).HasEvidence))
            .ToArray();
        Dictionary<string, CatalogSuiteReuseGroup> groupsByTest = safeGroups
            .Where(static group => group.TestIds.Count > 1)
            .SelectMany(group => group.TestIds.Select(testId => (testId, group)))
            .GroupBy(value => value.testId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().group,
                StringComparer.Ordinal);
        var emittedGroups = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(deterministicOrder.Count);

        foreach (string testId in deterministicOrder)
        {
            if (!groupsByTest.TryGetValue(testId, out CatalogSuiteReuseGroup? group))
            {
                ordered.Add(testId);
                continue;
            }

            string groupAnchor = group.TestIds[0];
            if (!emittedGroups.Add(groupAnchor))
            {
                continue;
            }

            foreach (string groupTestId in group.TestIds
                         .Select(value => (TestId: value, Score: ScoreFor(
                             catalog,
                             value,
                             scores)))
                         .OrderByDescending(value => value.Score.HasEvidence)
                         .ThenByDescending(value => value.Score.Priority)
                         .ThenByDescending(value => value.Score.FailureRate)
                         .ThenBy(value => value.Score.AverageDurationMs)
                         .ThenByDescending(value => value.Score.RetryRate)
                         .ThenBy(value => value.Score.GenerationCount)
                         .ThenBy(value => value.Score.NoOpRate)
                         .ThenByDescending(value => value.Score.Runs)
                         .ThenBy(value => value.TestId, StringComparer.Ordinal)
                         .Select(static value => value.TestId))
            {
                ordered.Add(groupTestId);
            }
        }

        return ordered.ToArray();
    }

    private static OrderingScore ScoreFor(
        CatalogDocument catalog,
        string testId,
        IReadOnlyDictionary<string, OrderingScore> scores)
    {
        return scores.TryGetValue(RecipeTarget(catalog, testId), out OrderingScore score)
            ? score
            : OrderingScore.Unknown;
    }

    private static string RecipeTarget(CatalogDocument catalog, string testId)
    {
        CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
        string hash = ProfilerValue.Hash(test?.Recipe);
        return hash.Length == 0 ? string.Empty : "h-" + hash;
    }

    private static OrderingScore? CreateScore(HistoryEvidence evidence)
    {
        if (evidence.Runs < MinimumObservedRuns)
        {
            return null;
        }

        int failureRate = Percentage(evidence.Failures, evidence.Runs);
        int retryRate = Percentage(evidence.Retries, evidence.Runs);
        int noOpRate = Percentage(evidence.NoOpRuns, evidence.Runs);
        long averageDurationMs = evidence.DurationMs / Math.Max(1, evidence.Runs);
        int cheapness = 100 - (int)Math.Min(100, averageDurationMs / 250);
        int generationPenalty = Math.Min(
            20,
            Math.Max(0, evidence.GenerationCount - 1) * 4);
        int noOpPenalty = Math.Min(20, noOpRate / 5);
        long priority = failureRate * 10_000L +
            retryRate * 1_000L +
            cheapness * 10L -
            generationPenalty * 100L -
            noOpPenalty * 10L;

        return new OrderingScore(
            priority,
            failureRate,
            averageDurationMs,
            retryRate,
            evidence.GenerationCount,
            noOpRate,
            evidence.Runs,
            HasEvidence: true);
    }

    private static int Percentage(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }

        return (int)Math.Min(100, Math.Max(0, numerator * 100 / denominator));
    }

    private static HistoryReadResult ReadHistory(
        string? requestedDirectory,
        string historyContext,
        DateTimeOffset now)
    {
        string directory = string.IsNullOrWhiteSpace(requestedDirectory)
            ? Path.Combine(Environment.CurrentDirectory, ".rimdev", "profiles")
            : requestedDirectory;
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return HistoryReadResult.Empty;
            }

            files = Directory.EnumerateFiles(
                    directory,
                    "rimliaison-*.json",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Take(MaximumProfilesToInspect)
                .ToArray();
        }
        catch
        {
            return HistoryReadResult.Empty;
        }

        var evidence = new Dictionary<string, HistoryEvidence>(StringComparer.Ordinal);
        int validProfiles = 0;
        int contextMatches = 0;
        foreach (string file in files)
        {
            if (!TryReadProfile(
                    file,
                    historyContext,
                    now,
                    out bool valid,
                    out bool contextMatchesFile,
                    out IReadOnlyList<ProfileEvidence> entries))
            {
                continue;
            }

            if (valid)
            {
                validProfiles++;
            }

            if (!contextMatchesFile)
            {
                continue;
            }

            contextMatches++;
            foreach (ProfileEvidence entry in entries)
            {
                if (!evidence.TryGetValue(entry.Target, out HistoryEvidence? current))
                {
                    if (evidence.Count >= MaximumRecipeEvidence)
                    {
                        continue;
                    }

                    current = new HistoryEvidence();
                    evidence.Add(entry.Target, current);
                }

                current.Add(entry);
            }
        }

        return new HistoryReadResult(
            files.Length,
            validProfiles,
            contextMatches,
            evidence);
    }

    private static bool TryReadProfile(
        string path,
        string historyContext,
        DateTimeOffset now,
        out bool valid,
        out bool contextMatches,
        out IReadOnlyList<ProfileEvidence> entries)
    {
        valid = false;
        contextMatches = false;
        entries = [];
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > EfficiencyProfiler.MaximumProfileBytes)
            {
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > EfficiencyProfiler.MaximumProfileBytes)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    MaxDepth = 16,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            JsonElement root = document.RootElement;
            if (!TryGetString(root, "schema", out string? schema) ||
                !string.Equals(schema, EfficiencyProfiler.SchemaVersion, StringComparison.Ordinal) ||
                !root.TryGetProperty("identity", out JsonElement identity) ||
                !root.TryGetProperty("outcome", out JsonElement outcome) ||
                !TryGetString(identity, "orderingSchema", out string? orderingSchema) ||
                !string.Equals(orderingSchema, PolicyVersion, StringComparison.Ordinal) ||
                !TryGetString(identity, "orderingContext", out string? storedContext) ||
                !TryGetString(identity, "startedUtc", out string? startedText) ||
                !DateTimeOffset.TryParse(
                    startedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset started) ||
                started > now.AddMinutes(MaximumFutureSkewMinutes) ||
                now - started > TimeSpan.FromDays(MaximumHistoryAgeDays) ||
                !TryGetString(outcome, "status", out string? outcomeStatus) ||
                outcomeStatus is not ("success" or "failure"))
            {
                return false;
            }

            valid = true;
            contextMatches = string.Equals(
                storedContext,
                historyContext,
                StringComparison.Ordinal);
            if (!contextMatches ||
                !root.TryGetProperty("operationCounts", out JsonElement operationCounts) ||
                operationCounts.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            var parsed = new List<ProfileEvidence>();
            int operationCount = 0;
            foreach (JsonElement operation in operationCounts.EnumerateArray())
            {
                if (++operationCount > MaximumOperationEntriesPerProfile)
                {
                    break;
                }

                if (TryReadOperation(operation, out ProfileEvidence? evidence))
                {
                    parsed.Add(evidence!);
                }
            }

            entries = parsed;
            return true;
        }
        catch
        {
            valid = false;
            contextMatches = false;
            entries = [];
            return false;
        }
    }

    private static bool TryReadOperation(
        JsonElement operation,
        out ProfileEvidence? evidence)
    {
        evidence = null;
        if (operation.ValueKind != JsonValueKind.Object ||
            !TryGetString(operation, "operation", out string? operationName) ||
            !TryGetString(operation, "category", out string? category) ||
            !TryGetString(operation, "phase", out string? phase) ||
            !TryGetString(operation, "target", out string? target) ||
            !string.Equals(operationName, "recipe.run", StringComparison.Ordinal) ||
            !string.Equals(category, "testing", StringComparison.Ordinal) ||
            !string.Equals(phase, "recipe", StringComparison.Ordinal) ||
            !IsTargetHash(target) ||
            !TryGetLong(operation, "runs", 1, MaximumRunsPerEntry, out long runs) ||
            !TryGetLong(operation, "cumulativeMs", 0, MaximumDurationMsPerEntry, out long durationMs) ||
            !TryGetLong(operation, "failures", 0, MaximumRunsPerEntry, out long failures) ||
            !TryGetLong(operation, "retries", 0, MaximumRunsPerEntry * 32, out long retries) ||
            !TryGetLong(operation, "noOpRuns", 0, MaximumRunsPerEntry, out long noOpRuns) ||
            failures > runs ||
            noOpRuns > runs)
        {
            return false;
        }

        var generationValues = new List<int>();
        if (operation.TryGetProperty("generations", out JsonElement generations))
        {
            if (generations.ValueKind != JsonValueKind.Array ||
                generations.GetArrayLength() > MaximumGenerationEntriesPerOperation)
            {
                return false;
            }

            foreach (JsonElement generation in generations.EnumerateArray())
            {
                if (!TryGetLong(generation, "generation", 1, int.MaxValue, out long value))
                {
                    return false;
                }

                if (!generationValues.Contains((int)value))
                {
                    generationValues.Add((int)value);
                }
            }
        }

        evidence = new ProfileEvidence(
            target!,
            runs,
            durationMs,
            failures,
            retries,
            noOpRuns,
            generationValues);
        return true;
    }

    private static bool IsTargetHash(string? value)
    {
        if (value is null || value.Length != 18 || !value.StartsWith("h-", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Skip(2).All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');
    }

    private static bool TryGetString(
        JsonElement parent,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetLong(
        JsonElement parent,
        string propertyName,
        long minimum,
        long maximum,
        out long value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out value))
        {
            return false;
        }

        return value >= minimum && value <= maximum;
    }

    private static void AppendContextPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private readonly record struct OrderingScore(
        long Priority,
        int FailureRate,
        long AverageDurationMs,
        int RetryRate,
        int GenerationCount,
        int NoOpRate,
        long Runs,
        bool HasEvidence)
    {
        internal static OrderingScore Unknown => new(
            long.MinValue,
            0,
            long.MaxValue,
            0,
            int.MaxValue,
            100,
            0,
            false);
    }

    private sealed class HistoryEvidence
    {
        private readonly HashSet<int> generations = [];

        internal long Runs { get; private set; }
        internal long DurationMs { get; private set; }
        internal long Failures { get; private set; }
        internal long Retries { get; private set; }
        internal long NoOpRuns { get; private set; }
        internal int GenerationCount => generations.Count;

        internal void Add(ProfileEvidence value)
        {
            Runs = Math.Min(MaximumRunsPerEntry * MaximumProfilesToInspect, Runs + value.Runs);
            DurationMs = Math.Min(
                MaximumDurationMsPerEntry * MaximumProfilesToInspect,
                DurationMs + value.DurationMs);
            Failures = Math.Min(Runs, Failures + value.Failures);
            Retries = Math.Min(Runs * 32, Retries + value.Retries);
            NoOpRuns = Math.Min(Runs, NoOpRuns + value.NoOpRuns);
            foreach (int generation in value.Generations)
            {
                if (generations.Count >= MaximumGenerationEntriesPerOperation * 2)
                {
                    break;
                }

                generations.Add(generation);
            }
        }
    }

    private sealed record ProfileEvidence(
        string Target,
        long Runs,
        long DurationMs,
        long Failures,
        long Retries,
        long NoOpRuns,
        IReadOnlyList<int> Generations)
    {
        internal int GenerationCount => Generations.Count;
    }

    private sealed record HistoryReadResult(
        int FilesFound,
        int ValidProfiles,
        int ContextMatches,
        IReadOnlyDictionary<string, HistoryEvidence> Evidence)
    {
        internal static HistoryReadResult Empty { get; } = new(
            0,
            0,
            0,
            new Dictionary<string, HistoryEvidence>(StringComparer.Ordinal));
    }
}
