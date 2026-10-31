using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Validation;

/// <summary>
/// The only supported validation classifications. REQUIRED is contract-owned;
/// the other classifications are advisory and cannot block production by absence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationClassification
{
    REQUIRED,
    BEST_EFFORT,
    RECOMMENDED
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationRequirementSource
{
    TASK_REQUIREMENT,
    REPOSITORY_POLICY,
    TOOLCHAIN_CONTRACT,
    DISCOVERED
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationFindingKind
{
    MOD_DEFECT,
    TOOLING_FAILURE,
    TOOLING_IMPROVEMENT,
    OPTIONAL_VALIDATION_UNAVAILABLE,
    INFORMATIONAL_PRODUCTION_EVENT
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationCheckState
{
    PASSED,
    FAILED,
    NOT_AVAILABLE,
    NOT_EXECUTED,
    RECORDED
}

public static class ValidationPolicySchema
{
    public const string Current = "rimliaison-validation-policy/v1";
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string ValidationIncomplete = "VALIDATION_INCOMPLETE";
}

public sealed record ValidationCheckDefinition
{
    public required string Id { get; init; }
    public ValidationClassification Classification { get; init; }
    public ValidationRequirementSource Source { get; init; }
    public required string Summary { get; init; }
    public string? ComponentOwner { get; init; }

    public bool IsContractValid =>
        Classification != ValidationClassification.REQUIRED ||
        Source != ValidationRequirementSource.DISCOVERED;
}

public sealed record ValidationCheckObservation
{
    public required ValidationCheckDefinition Check { get; init; }
    public ValidationCheckState State { get; init; }
    public ValidationFindingKind? Finding { get; init; }
    public string? EvidenceReference { get; init; }
    public string? Recommendation { get; init; }
    public string? Summary { get; init; }

    public bool IsExecuted => State is ValidationCheckState.PASSED or ValidationCheckState.FAILED;
    public bool IsRequired => Check.Classification == ValidationClassification.REQUIRED;
    public bool BlocksCompletion => IsRequired && State != ValidationCheckState.PASSED;
}

public sealed record ValidationPolicyResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = ValidationPolicySchema.Current;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("requiredPassed")]
    public int RequiredPassed { get; init; }

    [JsonPropertyName("requiredFailed")]
    public int RequiredFailed { get; init; }

    [JsonPropertyName("requiredUnavailable")]
    public int RequiredUnavailable { get; init; }

    [JsonPropertyName("optionalPassed")]
    public int OptionalPassed { get; init; }

    [JsonPropertyName("optionalDefects")]
    public int OptionalDefects { get; init; }

    [JsonPropertyName("optionalUnavailable")]
    public int OptionalUnavailable { get; init; }

    [JsonPropertyName("recommendations")]
    public int Recommendations { get; init; }

    [JsonPropertyName("blockingChecks")]
    public IReadOnlyList<string> BlockingChecks { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<ValidationCheckObservation> Observations { get; init; } = [];

    public bool PermitsProduction => Status == ValidationPolicySchema.Pass;
    public bool IsValidationIncomplete => Status == ValidationPolicySchema.ValidationIncomplete;
}

public static class ValidationPolicyEvaluator
{
    public static ValidationCheckDefinition Define(
        string id,
        ValidationClassification classification,
        ValidationRequirementSource source,
        string summary,
        string? componentOwner = null)
    {
        var definition = new ValidationCheckDefinition
        {
            Id = id,
            Classification = classification,
            Source = source,
            Summary = summary,
            ComponentOwner = componentOwner
        };
        EnsureContractValid(definition);
        return definition;
    }

    public static void EnsureContractValid(ValidationCheckDefinition check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.IsContractValid)
        {
            throw new InvalidOperationException(
                $"Discovered validation '{check.Id}' cannot be promoted to REQUIRED during the current task.");
        }
    }

    public static ValidationPolicyResult Evaluate(
        IEnumerable<ValidationCheckObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ValidationCheckObservation[] values = observations.ToArray();
        foreach (ValidationCheckObservation observation in values)
        {
            EnsureContractValid(observation.Check);
        }

        int requiredPassed = values.Count(IsRequiredPassed);
        int requiredFailed = values.Count(value =>
            value.IsRequired && value.State == ValidationCheckState.FAILED);
        int requiredUnavailable = values.Count(value =>
            value.IsRequired &&
            (value.State is ValidationCheckState.NOT_AVAILABLE or
                ValidationCheckState.NOT_EXECUTED));
        int optionalPassed = values.Count(value =>
            !value.IsRequired && value.State == ValidationCheckState.PASSED);
        int optionalDefects = values.Count(value =>
            !value.IsRequired &&
            value.State == ValidationCheckState.FAILED &&
            value.Finding == ValidationFindingKind.MOD_DEFECT);
        int optionalUnavailable = values.Count(value =>
            !value.IsRequired &&
            (value.State is ValidationCheckState.NOT_AVAILABLE or
                ValidationCheckState.NOT_EXECUTED));
        int recommendations = values.Count(value =>
            value.Check.Classification == ValidationClassification.RECOMMENDED ||
            value.Finding == ValidationFindingKind.TOOLING_IMPROVEMENT);

        string status = requiredFailed > 0 || optionalDefects > 0
            ? ValidationPolicySchema.Fail
            : requiredUnavailable > 0
                ? ValidationPolicySchema.ValidationIncomplete
                : ValidationPolicySchema.Pass;

        return new ValidationPolicyResult
        {
            Status = status,
            RequiredPassed = requiredPassed,
            RequiredFailed = requiredFailed,
            RequiredUnavailable = requiredUnavailable,
            OptionalPassed = optionalPassed,
            OptionalDefects = optionalDefects,
            OptionalUnavailable = optionalUnavailable,
            Recommendations = recommendations,
            BlockingChecks = values
                .Where(value => value.BlocksCompletion ||
                    value.Finding == ValidationFindingKind.MOD_DEFECT)
                .Select(value => value.Check.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Observations = values
        };
    }

    private static bool IsRequiredPassed(ValidationCheckObservation value) =>
        value.IsRequired && value.State == ValidationCheckState.PASSED;
}

/// <summary>
/// E0 only: a pure relation evaluator for explicitly synthetic, bounded fixtures.
/// PASS here is never evidence of a native RimWorld run, a qualified runner, or gameplay.
/// Real-game provenance and orchestration require separately reviewed map-check integration.
/// </summary>
public static class CounterfactualContract
{
    public const string Current = "rimliaison-counterfactual/v1";
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Inconclusive = "INCONCLUSIVE";
    public const string InvalidEvidence = "INVALID_EVIDENCE";
}

public sealed record CounterfactualContext(
    string SchemaVersion,
    string SourceSha,
    string ModId,
    string ScenarioId,
    string MapId,
    string Nonce,
    int Seed,
    string Biome,
    string ExpectedBiome,
    string Provenance,
    bool MapReady,
    bool CurrentLogVerified,
    bool CleanupComplete,
    string? GameBuild = null,
    string? RunnerSha = null);

public sealed record CounterfactualObservation(
    string EventId,
    string SourceSha,
    string ScenarioId,
    string MapId,
    string Nonce,
    string Biome,
    string ConditionsSha256,
    string SampleId,
    string PlotId,
    string Metric,
    decimal Value,
    string Unit,
    long Tick,
    int Coverage);

public sealed record CounterfactualAssertion(
    string Name,
    string Metric,
    string Unit,
    string Relation,
    string ControlPlot,
    string TreatmentPlot,
    int MinimumPairs = 1,
    decimal Tolerance = 0m,
    decimal? LowerBound = null,
    decimal? UpperBound = null);

public sealed record CounterfactualResult(
    string SchemaVersion,
    string AssertionName,
    string Status,
    string ReasonCode,
    int SampleCount,
    decimal? ControlValue,
    decimal? TreatmentValue,
    string Unit,
    string SourceSha,
    string Nonce,
    string MapId,
    string Provenance,
    bool NativeQualified = false);

public static class CounterfactualEvaluator
{
    private const int MaximumObservations = 64;
    private const decimal MaximumMagnitude = 1_000_000_000m;
    private const decimal MaximumTolerance = 1_000_000m;

    public static CounterfactualResult Evaluate(
        CounterfactualContext context,
        CounterfactualAssertion assertion,
        IReadOnlyList<CounterfactualObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(observations);

        CounterfactualResult Result(
            string status, string reason, int count = 0,
            decimal? control = null, decimal? treatment = null) =>
            new(CounterfactualContract.Current, assertion.Name, status, reason, count,
                control, treatment, assertion.Unit, context.SourceSha, context.Nonce,
                context.MapId, "synthetic", NativeQualified: false);

        CounterfactualResult Invalid(string reason) =>
            Result(CounterfactualContract.InvalidEvidence, reason);

        if (context.SchemaVersion != CounterfactualContract.Current ||
            context.Provenance != "synthetic" ||
            context.CurrentLogVerified ||
            !string.IsNullOrEmpty(context.GameBuild) ||
            !string.IsNullOrEmpty(context.RunnerSha))
            return Invalid("SYNTHETIC_CONTRACT_REQUIRED");

        if (!IsSha(context.SourceSha) || !IsToken(context.ModId) ||
            !IsToken(context.ScenarioId) || !IsToken(context.MapId) ||
            !IsToken(context.Nonce) || !IsToken(context.Biome) ||
            !IsToken(context.ExpectedBiome) ||
            context.Biome != context.ExpectedBiome || context.Seed < 0)
            return Invalid("CONTEXT_IDENTITY_INVALID");
        if (!context.MapReady || !context.CleanupComplete)
            return Invalid("MAP_OR_CLEANUP_UNVERIFIED");

        if (!IsToken(assertion.Name) || !IsToken(assertion.Metric) ||
            !IsToken(assertion.Unit) || !IsToken(assertion.ControlPlot) ||
            !IsToken(assertion.TreatmentPlot) ||
            assertion.ControlPlot == assertion.TreatmentPlot ||
            assertion.MinimumPairs is < 1 or > 16 ||
            assertion.Tolerance is < 0 or > MaximumTolerance ||
            !new[] { "greater-than", "equal", "bounded", "no-increase" }
                .Contains(assertion.Relation, StringComparer.Ordinal) ||
            assertion.LowerBound is < -MaximumMagnitude or > MaximumMagnitude ||
            assertion.UpperBound is < -MaximumMagnitude or > MaximumMagnitude ||
            (assertion.Relation == "bounded" &&
             (assertion.LowerBound is null || assertion.UpperBound is null ||
              assertion.LowerBound > assertion.UpperBound)))
            return Invalid("ASSERTION_CONTRACT_INVALID");

        if (observations.Count > MaximumObservations)
            return Invalid("OBSERVATION_LIMIT_EXCEEDED");

        HashSet<string> seenEvents = new(StringComparer.Ordinal);
        HashSet<string> seenPlotSamples = new(StringComparer.Ordinal);
        foreach (CounterfactualObservation observation in observations)
        {
            if (observation is null || !IsToken(observation.EventId) ||
                !IsToken(observation.SampleId) || !IsToken(observation.PlotId) ||
                observation.SourceSha != context.SourceSha ||
                observation.ScenarioId != context.ScenarioId ||
                observation.MapId != context.MapId ||
                observation.Nonce != context.Nonce ||
                observation.Biome != context.Biome ||
                !IsSha256(observation.ConditionsSha256) ||
                observation.Metric != assertion.Metric ||
                observation.Unit != assertion.Unit ||
                (observation.PlotId != assertion.ControlPlot &&
                 observation.PlotId != assertion.TreatmentPlot) ||
                observation.Value is < -MaximumMagnitude or > MaximumMagnitude ||
                observation.Tick < 0 || observation.Coverage is < 0 or > 350)
                return Invalid("OBSERVATION_IDENTITY_INVALID");
            if (!seenEvents.Add(observation.EventId) ||
                !seenPlotSamples.Add(observation.SampleId + "/" + observation.PlotId))
                return Invalid("DUPLICATE_OBSERVATION");
        }

        int count = 0;
        decimal controlTotal = 0m;
        decimal treatmentTotal = 0m;
        bool incomplete = false;
        bool violated = false;

        foreach (IGrouping<string, CounterfactualObservation> group in
                 observations.GroupBy(item => item.SampleId, StringComparer.Ordinal))
        {
            CounterfactualObservation? control = group.FirstOrDefault(
                item => item.PlotId == assertion.ControlPlot);
            CounterfactualObservation? treatment = group.FirstOrDefault(
                item => item.PlotId == assertion.TreatmentPlot);
            if (control is null || treatment is null || treatment.Coverage == 0)
            {
                incomplete = true;
                continue;
            }
            if (control.ConditionsSha256 != treatment.ConditionsSha256)
                return Invalid("UNMATCHED_EXPERIMENT_CONDITIONS");
            if (control.Tick != treatment.Tick)
                return Invalid("UNMATCHED_OBSERVATION_TICK");

            count++;
            controlTotal += control.Value;
            treatmentTotal += treatment.Value;
            bool satisfied = assertion.Relation switch
            {
                "greater-than" => treatment.Value > control.Value + assertion.Tolerance,
                "equal" => Math.Abs(treatment.Value - control.Value) <= assertion.Tolerance,
                "bounded" => treatment.Value >= assertion.LowerBound &&
                             treatment.Value <= assertion.UpperBound,
                "no-increase" => treatment.Value <= control.Value + assertion.Tolerance,
                _ => false
            };
            violated |= !satisfied;
        }

        decimal? controlAverage = count == 0 ? null : controlTotal / count;
        decimal? treatmentAverage = count == 0 ? null : treatmentTotal / count;
        if (incomplete || count < assertion.MinimumPairs)
            return Result(CounterfactualContract.Inconclusive,
                "INSUFFICIENT_MATCHED_COVERAGE", count, controlAverage, treatmentAverage);
        return Result(violated ? CounterfactualContract.Fail : CounterfactualContract.Pass,
            violated ? "RELATION_VIOLATED" : "SYNTHETIC_RELATION_CONFIRMED",
            count, controlAverage, treatmentAverage);
    }

    private static bool IsSha(string? value) => IsLowerHex(value, 40);

    private static bool IsSha256(string? value) => IsLowerHex(value, 64);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 80 } && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '-' or '_' or '.');
}

/// <summary>
/// Pure, game-free cross-event consistency check. A caller-supplied log is
/// never a trusted game process: even a perfect match is only eligible for a
/// separately qualified native-run provenance and cleanup attestation.
/// </summary>
public static class MapEventContract
{
    public const string Current = "rimliaison-map-events/v1";
    public const string ReadyForNativeReview = "READY_FOR_NATIVE_REVIEW";
    public const string Inconclusive = "INCONCLUSIVE";
    public const string InvalidEvidence = "INVALID_EVIDENCE";
    public const string AssertionFailed = "ASSERTION_FAILED";
}

public sealed record MapEventExpectation(
    string EventPrefix,
    string OutcomeSchema,
    string ObservationSchema,
    string Nonce,
    string ExpectedBiome,
    string CountField,
    string PresenceField,
    string IntegrityField,
    string PositiveOutcome,
    int MaximumCount,
    string? TerminalCountField = null,
    string? TerminalSelectionField = null);

public sealed record MapEventAssessment(
    string SchemaVersion,
    string Status,
    string ReasonCode,
    int? MapUniqueId,
    int? ObservedCount,
    string Provenance = "untrusted-log-text",
    bool NativeQualified = false);

public static class MapEventEvaluator
{
    private const int MaximumEvents = 8;
    private const int MaximumEventCharacters = 4_096;
    private const int MaximumDimension = 1_000;
    private const int MaximumObservedCount = 1_000_000;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = 4,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private sealed record MapEvent(
        string Schema, int MapId, string Biome, int Width, int Height,
        string? Outcome, int? Count, bool? Present, bool? Integrity,
        int? TerminalCount = null, bool? TerminalSelected = null);

    public static MapEventAssessment Evaluate(
        MapEventExpectation expected, IReadOnlyList<string> logEvents)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(logEvents);

        MapEventAssessment Result(
            string status, string reason, int? mapId = null, int? count = null) =>
            new(MapEventContract.Current, status, reason, mapId, count);

        MapEventAssessment Invalid(string reason) =>
            Result(MapEventContract.InvalidEvidence, reason);

        string[] declaredFields =
        [
            expected.CountField, expected.PresenceField, expected.IntegrityField,
            .. new[] { expected.TerminalCountField, expected.TerminalSelectionField }
                .OfType<string>()
        ];
        if (!Token(expected.EventPrefix, 48) ||
            !SchemaToken(expected.OutcomeSchema) ||
            !SchemaToken(expected.ObservationSchema) ||
            expected.OutcomeSchema == expected.ObservationSchema ||
            !Token(expected.Nonce, 40, minimum: 8) ||
            !Token(expected.ExpectedBiome, 80) ||
            !Token(expected.PositiveOutcome, 48) ||
            declaredFields.Any(field => !Token(field, 48) ||
                field is "nonce" or "schemaVersion" or "mapUniqueId" or
                    "mapWidth" or "mapHeight" or "biome" or "outcome" or
                    "nativeQualified" or "provenance" or "currentLogVerified" or
                    "cleanupComplete" or "runnerSha" or "sourceSha") ||
            declaredFields.Distinct(StringComparer.Ordinal).Count() != declaredFields.Length ||
            expected.MaximumCount is < 1 or > MaximumObservedCount)
            return Invalid("EXPECTATION_CONTRACT_INVALID");

        if (logEvents.Count > MaximumEvents)
            return Invalid("EVENT_LIMIT_EXCEEDED");
        if (logEvents.Count == 0)
            return Result(MapEventContract.Inconclusive, "NO_MAP_EVENTS");

        string prefix = "[" + expected.EventPrefix + "] ";
        MapEvent? terminal = null;
        MapEvent? observed = null;

        foreach (string raw in logEvents)
        {
            if (raw is null || raw.Length > MaximumEventCharacters ||
                raw.Any(char.IsControl) ||
                !raw.StartsWith(prefix, StringComparison.Ordinal))
                return Invalid("EVENT_ENVELOPE_INVALID");

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    raw.AsMemory(prefix.Length), JsonOptions);
                JsonElement item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object)
                    return Invalid("EVENT_JSON_INVALID");

                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in item.EnumerateObject())
                {
                    if (!keys.Add(property.Name))
                        return Invalid("DUPLICATE_JSON_PROPERTY");
                }
                // Event text may never claim native trust; only the approved
                // external host check can establish such provenance.
                if (keys.Overlaps(new[]
                    {
                        "nativeQualified", "currentLogVerified",
                        "cleanupComplete", "runnerSha", "sourceSha", "provenance"
                    }))
                    return Invalid("FORGED_AUTHORITY_PROPERTY");

                if (!TryString(item, "schemaVersion", out string? schema) ||
                    (schema != expected.OutcomeSchema &&
                     schema != expected.ObservationSchema) ||
                    !TryString(item, "nonce", out string? nonce) ||
                    nonce != expected.Nonce ||
                    !TryString(item, "biome", out string? biome) ||
                    biome != expected.ExpectedBiome ||
                    !TryInteger(item, "mapUniqueId", out int mapId) || mapId < 0 ||
                    !TryInteger(item, "mapWidth", out int width) ||
                    !TryInteger(item, "mapHeight", out int height) ||
                    width is < 50 or > MaximumDimension ||
                    height is < 50 or > MaximumDimension)
                    return Invalid("EVENT_IDENTITY_INVALID");

                MapEvent record;
                if (schema == expected.OutcomeSchema)
                {
                    if (!TryString(item, "outcome", out string? outcome) ||
                        !Token(outcome, 48))
                        return Invalid("OUTCOME_FIELD_INVALID");
                    int? terminalCount = null;
                    bool? terminalSelected = null;
                    if (expected.TerminalCountField is not null)
                    {
                        if (!TryInteger(item, expected.TerminalCountField, out int value) ||
                            value is < 0 or > MaximumObservedCount)
                            return Invalid("TERMINAL_COUNT_INVALID");
                        terminalCount = value;
                    }
                    if (expected.TerminalSelectionField is not null)
                    {
                        if (!TryBool(item, expected.TerminalSelectionField, out bool selected))
                            return Invalid("TERMINAL_SELECTION_INVALID");
                        terminalSelected = selected;
                    }
                    record = new(schema, mapId, biome!, width, height,
                        outcome, null, null, null, terminalCount, terminalSelected);
                    if (terminal is not null)
                        return Invalid("DUPLICATE_TERMINAL_EVENT");
                    terminal = record;
                }
                else
                {
                    if (!TryInteger(item, expected.CountField, out int count) ||
                        count is < 0 or > MaximumObservedCount ||
                        !TryBool(item, expected.PresenceField, out bool present) ||
                        !TryBool(item, expected.IntegrityField, out bool integrity))
                        return Invalid("OBSERVATION_FIELD_INVALID");
                    record = new(schema!, mapId, biome!, width, height,
                        null, count, present, integrity);
                    if (observed is not null)
                        return Invalid("DUPLICATE_OBSERVATION_EVENT");
                    observed = record;
                }
            }
            catch (JsonException)
            {
                return Invalid("EVENT_JSON_INVALID");
            }
        }

        if (terminal is null || observed is null)
            return Result(MapEventContract.Inconclusive, "MISSING_MATCHED_EVENT");
        if (terminal.MapId != observed.MapId ||
            terminal.Biome != observed.Biome ||
            terminal.Width != observed.Width ||
            terminal.Height != observed.Height)
            return Invalid("CROSS_MAP_EVENT_MISMATCH");

        int metric = observed.Count!.Value;
        if (observed.Present != (metric > 0))
            return Invalid("PRESENCE_COUNT_CONTRADICTION");
        if (metric > expected.MaximumCount)
            return Result(MapEventContract.AssertionFailed, "COUNT_BOUND_VIOLATED",
                observed.MapId, metric);
        if (observed.Integrity != true)
            return Result(MapEventContract.AssertionFailed, "PLACEMENT_INVALID",
                observed.MapId, metric);
        if (terminal.TerminalSelected is false &&
            (terminal.TerminalCount is > 0 ||
             terminal.Outcome == expected.PositiveOutcome))
            return Invalid("TERMINAL_SELECTION_CONTRADICTION");
        if (terminal.TerminalCount is int placed)
        {
            if (placed > expected.MaximumCount)
                return Result(MapEventContract.AssertionFailed,
                    "TERMINAL_COUNT_BOUND_VIOLATED", observed.MapId, metric);
            if (placed > metric)
                return Invalid("TERMINAL_COUNT_OBSERVATION_MISMATCH");
            if (placed == 0)
                return terminal.Outcome == expected.PositiveOutcome
                    ? Invalid("TERMINAL_ZERO_POSITIVE_CONTRADICTION")
                    : Result(MapEventContract.Inconclusive,
                        "NO_NEW_FEATURE_PLACED", observed.MapId, metric);
        }
        if (metric == 0)
            return Result(MapEventContract.Inconclusive, "NO_FEATURE_OBSERVED",
                observed.MapId, metric);
        if (terminal.Outcome != expected.PositiveOutcome)
            return Result(MapEventContract.Inconclusive, "TERMINAL_OUTCOME_NOT_POSITIVE",
                observed.MapId, metric);
        return Result(MapEventContract.ReadyForNativeReview,
            "MATCHED_UNTRUSTED_MAP_EVENTS", observed.MapId, metric);
    }

    private static bool TryString(JsonElement item, string key, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(key, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return value is not null;
    }

    private static bool TryInteger(JsonElement item, string key, out int value)
    {
        value = 0;
        return item.TryGetProperty(key, out JsonElement property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryBool(JsonElement item, string key, out bool value)
    {
        value = false;
        if (!item.TryGetProperty(key, out JsonElement property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool SchemaToken(string? value) =>
        value is { Length: > 0 and <= 80 } &&
        value.Count(character => character == '/') == 1 &&
        value.All(character => character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '/');

    private static bool Token(string? value, int maximum, int minimum = 1) =>
        value is not null && value.Length >= minimum && value.Length <= maximum &&
        value.All(character => character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');
}
