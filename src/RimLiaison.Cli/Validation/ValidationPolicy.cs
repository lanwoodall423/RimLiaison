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
