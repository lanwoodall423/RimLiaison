using System.Text.Json.Serialization;

namespace RimDev.Contracts;

public sealed record ValidationRequirement
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.ValidationRequirement;

    [JsonPropertyName("requirementId")]
    public required string RequirementId { get; init; }

    [JsonPropertyName("subject")]
    public required EntityReference Subject { get; init; }

    [JsonPropertyName("assertion")]
    public required string Assertion { get; init; }

    [JsonPropertyName("preferredEvidenceLevel")]
    public string PreferredEvidenceLevel { get; init; } = "standard";

    [JsonPropertyName("staticEvidenceAllowed")]
    public bool StaticEvidenceAllowed { get; init; } = true;

    [JsonPropertyName("runtimeRequired")]
    public bool RuntimeRequired { get; init; }

    [JsonPropertyName("runtimeRequiredWhen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeRequiredWhen { get; init; }

    [JsonPropertyName("prerequisites")]
    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; init; }

    [JsonPropertyName("producer")]
    public required string Producer { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    public ValidationRequirement Normalize() => this with
    {
        SchemaVersion = SharedContractSchemas.ValidationRequirement,
        RequirementId = Bound(RequirementId, 256) ?? "unknown",
        Subject = Subject.Normalize(),
        Assertion = Bound(Assertion, 512) ?? "unknown",
        PreferredEvidenceLevel = Bound(PreferredEvidenceLevel, 64) ?? "standard",
        RuntimeRequiredWhen = Bound(RuntimeRequiredWhen, 256),
        Prerequisites = Prerequisites
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, 128)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(16)
            .ToArray(),
        Severity = Bound(Severity, 32),
        Producer = Bound(Producer, 128) ?? "unknown",
        Source = Bound(Source, 512)
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record RuntimeValidationRequest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.RuntimeValidationRequest;

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("subject")]
    public required EntityReference Subject { get; init; }

    [JsonPropertyName("assertion")]
    public required string Assertion { get; init; }

    [JsonPropertyName("prerequisites")]
    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    [JsonPropertyName("requiredEvidence")]
    public IReadOnlyList<string> RequiredEvidence { get; init; } = [];

    [JsonPropertyName("excludedWork")]
    public IReadOnlyList<string> ExcludedWork { get; init; } = [];

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    public static RuntimeValidationRequest FromRequirement(
        ValidationRequirement requirement,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        return new RuntimeValidationRequest
        {
            Reason = Bound(reason, 512) ?? "runtime evidence is required",
            Subject = requirement.Subject.Normalize(),
            Assertion = Bound(requirement.Assertion, 512) ?? "runtime assertion",
            Prerequisites = requirement.Prerequisites,
            RequiredEvidence = ["artifact-correspondence", "runtime-assertion"],
            ExcludedWork = ["unrelated-tests", "duplicate-runtime-launch"],
            Source = requirement.Source ?? requirement.Producer
        };
    }

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record ToolEventEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.ToolEvent;

    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("producer")]
    public required string Producer { get; init; }

    [JsonPropertyName("eventType")]
    public required string EventType { get; init; }

    [JsonPropertyName("identity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionIdentity? Identity { get; init; }

    [JsonPropertyName("subjects")]
    public IReadOnlyList<EntityReference> Subjects { get; init; } = [];

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundedPayload? Payload { get; init; }

    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContractProvenance? Provenance { get; init; }

    public static ToolEventEnvelope Create(
        string producer,
        string eventType,
        DateTimeOffset timestampUtc,
        ExecutionIdentity? identity = null,
        IEnumerable<EntityReference>? subjects = null,
        object? payload = null,
        ContractProvenance? provenance = null,
        string? eventId = null,
        int maximumPayloadBytes = 8_192)
    {
        string normalizedProducer = Bound(producer, 128) ?? "unknown";
        string normalizedType = Bound(eventType, 128) ?? "unknown";
        return new ToolEventEnvelope
        {
            EventId = Bound(eventId, 256) ?? "evt-" + Guid.NewGuid().ToString("N"),
            TimestampUtc = timestampUtc.ToUniversalTime(),
            Producer = normalizedProducer,
            EventType = normalizedType,
            Identity = identity?.Normalize(),
            Subjects = (subjects ?? [])
                .Take(32)
                .Select(static subject => subject.Normalize())
                .ToArray(),
            Payload = BoundedPayload.From(payload, maximumPayloadBytes),
            Provenance = provenance?.Normalize()
        };
    }

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}
