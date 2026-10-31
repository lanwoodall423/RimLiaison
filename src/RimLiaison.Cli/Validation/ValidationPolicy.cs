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
