using System.Text.Json.Serialization;

namespace RimDev.Contracts;

public static class FailureContractSchemas
{
    public const string Packet = "rimdev-failure-packet/v1";
    public const string Diagnosis = "rimdev-failure-diagnosis/v1";
    public const string RemediationPrecedent = "rimdev-remediation-precedent/v1";
}

public sealed record FailureEvidencePacket
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = FailureContractSchemas.Packet;

    [JsonPropertyName("identity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionIdentity? Identity { get; init; }

    [JsonPropertyName("failedValidation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EntityReference? FailedValidation { get; init; }

    [JsonPropertyName("classification")]
    public required string Classification { get; init; }

    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("stackOrLog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceReference? StackOrLog { get; init; }

    [JsonPropertyName("changedSourceFiles")]
    public IReadOnlyList<string> ChangedSourceFiles { get; init; } = [];

    [JsonPropertyName("affectedEntities")]
    public IReadOnlyList<EntityReference> AffectedEntities { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<EntityReference> Dependencies { get; init; } = [];

    [JsonPropertyName("frameworks")]
    public IReadOnlyList<EntityReference> Frameworks { get; init; } = [];

    [JsonPropertyName("precedingEvidence")]
    public IReadOnlyList<EvidenceReference> PrecedingEvidence { get; init; } = [];

    [JsonPropertyName("references")]
    public IReadOnlyList<EvidenceReference> References { get; init; } = [];

    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContractProvenance? Provenance { get; init; }

    public FailureEvidencePacket Normalize() => this with
    {
        SchemaVersion = FailureContractSchemas.Packet,
        Identity = Identity?.Normalize(),
        FailedValidation = FailedValidation?.Normalize(),
        Classification = Bound(Classification, 128) ?? "unknown",
        Error = Bound(Error, 1024) ?? "unknown failure",
        ChangedSourceFiles = ChangedSourceFiles
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, 1024)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray(),
        AffectedEntities = NormalizeEntities(AffectedEntities),
        Dependencies = NormalizeEntities(Dependencies),
        Frameworks = NormalizeEntities(Frameworks),
        PrecedingEvidence = NormalizeEvidence(PrecedingEvidence),
        References = NormalizeEvidence(References),
        StackOrLog = StackOrLog?.Normalize(),
        Provenance = Provenance?.Normalize()
    };

    private static EntityReference[] NormalizeEntities(IEnumerable<EntityReference> values) => values
        .Take(32)
        .Select(static value => value.Normalize())
        .GroupBy(value => value.Kind + "\0" + value.Id, StringComparer.Ordinal)
        .Select(group => group.First())
        .ToArray();

    private static EvidenceReference[] NormalizeEvidence(IEnumerable<EvidenceReference> values) => values
        .Take(16)
        .Select(static value => value.Normalize())
        .GroupBy(value => value.Uri, StringComparer.Ordinal)
        .Select(group => group.First())
        .ToArray();

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record FailureDiagnosis
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = FailureContractSchemas.Diagnosis;

    [JsonPropertyName("packet")]
    public required FailureEvidencePacket Packet { get; init; }

    [JsonPropertyName("likelyRootCause")]
    public required string LikelyRootCause { get; init; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "low";

    [JsonPropertyName("relevantEvidence")]
    public IReadOnlyList<EvidenceReference> RelevantEvidence { get; init; } = [];

    [JsonPropertyName("additionalRequirements")]
    public IReadOnlyList<ValidationRequirement> AdditionalRequirements { get; init; } = [];

    [JsonPropertyName("reproductionContext")]
    public IReadOnlyList<EntityReference> ReproductionContext { get; init; } = [];

    [JsonPropertyName("reductionCandidates")]
    public IReadOnlyList<EntityReference> ReductionCandidates { get; init; } = [];

    [JsonPropertyName("remediation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remediation { get; init; }

    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContractProvenance? Provenance { get; init; }
}
public sealed record RemediationPrecedent
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = FailureContractSchemas.RemediationPrecedent;

    [JsonPropertyName("precedentId")]
    public required string PrecedentId { get; init; }

    [JsonPropertyName("failureFamily")]
    public required string FailureFamily { get; init; }

    [JsonPropertyName("subject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EntityReference? Subject { get; init; }

    [JsonPropertyName("applicability")]
    public IReadOnlyList<string> Applicability { get; init; } = [];

    [JsonPropertyName("rootCause")]
    public required string RootCause { get; init; }

    [JsonPropertyName("validatedRemediation")]
    public required string ValidatedRemediation { get; init; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];

    [JsonPropertyName("successfulValidationIdentity")]
    public required ExecutionIdentity SuccessfulValidationIdentity { get; init; }

    [JsonPropertyName("provenance")]
    public required ContractProvenance Provenance { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "proven";

    [JsonPropertyName("supportCount")]
    public int SupportCount { get; init; } = 1;

    public RemediationPrecedent Normalize() => this with
    {
        SchemaVersion = FailureContractSchemas.RemediationPrecedent,
        PrecedentId = Bound(PrecedentId, 256) ?? "unknown",
        FailureFamily = Bound(FailureFamily, 256) ?? "unknown",
        Subject = Subject?.Normalize(),
        Applicability = Applicability
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, 256)!)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray(),
        RootCause = Bound(RootCause, 1024) ?? "unknown",
        ValidatedRemediation = Bound(ValidatedRemediation, 1024) ?? "unknown",
        Evidence = Evidence.Take(16).Select(static value => value.Normalize()).ToArray(),
        SuccessfulValidationIdentity = SuccessfulValidationIdentity.Normalize(),
        Provenance = Provenance.Normalize(),
        Status = Bound(Status, 32)?.ToLowerInvariant() ?? "tentative",
        SupportCount = Math.Clamp(SupportCount, 1, 10_000)
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}
