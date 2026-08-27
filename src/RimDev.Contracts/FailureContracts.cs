using System.Text.Json.Serialization;

namespace RimDev.Contracts;

public static class FailureContractSchemas
{
    public const string Packet = "rimdev-failure-packet/v1";
    public const string Diagnosis = "rimdev-failure-diagnosis/v1";
    public const string RemediationPrecedent = "rimdev-remediation-precedent/v1";
}

public sealed record FailureCausalReference(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("entity")] string Entity);

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

    [JsonPropertyName("reportingTool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportingTool { get; init; }

    [JsonPropertyName("causalComponent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausalComponent { get; init; }

    [JsonPropertyName("affectedProject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AffectedProject { get; init; }

    [JsonPropertyName("affectedModIds")]
    public IReadOnlyList<string> AffectedModIds { get; init; } = [];

    [JsonPropertyName("failureSurface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureSurface { get; init; }

    [JsonPropertyName("orchestrator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Orchestrator { get; init; }

    [JsonPropertyName("underlyingErrorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnderlyingErrorCode { get; init; }

    [JsonPropertyName("causalIssueKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausalIssueKey { get; init; }

    [JsonPropertyName("failureSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureSummary { get; init; }

    [JsonPropertyName("causalChain")]
    public IReadOnlyList<FailureCausalReference> CausalChain { get; init; } = [];

    [JsonPropertyName("stackOrLog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceReference? StackOrLog { get; init; }

    [JsonPropertyName("changedSourceFiles")]
    public IReadOnlyList<string> ChangedSourceFiles { get; init; } = [];

    [JsonPropertyName("affectedEntities")]
    public IReadOnlyList<EntityReference> AffectedEntities { get; init; } = [];

    [JsonPropertyName("blockedValidations")]
    public IReadOnlyList<EntityReference> BlockedValidations { get; init; } = [];


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
        AffectedModIds = AffectedModIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, 256)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray(),
        ReportingTool = Bound(ReportingTool, 128),
        CausalComponent = Bound(CausalComponent, 128),
        AffectedProject = Bound(AffectedProject, 256),
        FailureSurface = Bound(FailureSurface, 128),
        Orchestrator = Bound(Orchestrator, 128),
        UnderlyingErrorCode = Bound(UnderlyingErrorCode, 128),
        CausalIssueKey = Bound(CausalIssueKey, 256),
        CausalChain = CausalChain
            .Take(16)
            .Select(value => new FailureCausalReference(
                Bound(value.Role, 64) ?? "unknown",
                Bound(value.Component, 128) ?? "unknown",
                Bound(value.Entity, 256) ?? "unknown"))
            .ToArray(),
        FailureSummary = Bound(FailureSummary, 1024),
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
        BlockedValidations = NormalizeEntities(BlockedValidations),
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
