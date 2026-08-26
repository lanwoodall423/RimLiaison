using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimDev.Contracts;

public static class EntityReferenceKinds
{
    public const string RepositoryFile = "repository-file";
    public const string Def = "def";
    public const string Mod = "mod";
    public const string Tool = "tool";
    public const string BuildArtifact = "build-artifact";
    public const string Test = "test";
    public const string RuntimeSubject = "runtime-subject";
    public const string ContentBlueprint = "content-blueprint";
    public const string RimBenchAnalysis = "rimbench-analysis";
}

public sealed record EntityReference
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.EntityReference;

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    public EntityReference Normalize() => this with
    {
        SchemaVersion = SharedContractSchemas.EntityReference,
        Kind = Bound(Kind, 64) ?? "unknown",
        Id = Bound(Id, 512) ?? "unknown",
        Path = Bound(Path, 1024),
        Name = Bound(Name, 256),
        Version = Bound(Version, 128)
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record EvidenceReference
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    public EvidenceReference Normalize() => this with
    {
        Kind = Bound(Kind, 64) ?? "artifact",
        Uri = Bound(Uri, 1024) ?? "unknown",
        Sha256 = Bound(Sha256, 128),
        SizeBytes = SizeBytes is >= 0 and <= 1_073_741_824 ? SizeBytes : null
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record BoundedPayload
{
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }

    public static BoundedPayload? From(object? value, int maximumBytes)
    {
        if (value is null)
        {
            return null;
        }

        int limit = Math.Clamp(maximumBytes, 256, 65_536);
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        }
        catch (JsonException)
        {
            return new BoundedPayload { Truncated = true };
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length > limit)
        {
            return new BoundedPayload
            {
                Data = JsonSerializer.SerializeToElement(new
                {
                    omitted = true,
                    reason = "payload-exceeds-bound"
                }),
                Truncated = true,
                Sha256 = hash
            };
        }

        return new BoundedPayload
        {
            Data = JsonDocument.Parse(bytes).RootElement.Clone(),
            Truncated = false,
            Sha256 = hash
        };
    }
}

public sealed record ContractProvenance
{
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("producerVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProducerVersion { get; init; }

    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EvidenceReference>? References { get; init; }

    public ContractProvenance Normalize() => this with
    {
        Source = Bound(Source, 512),
        ProducerVersion = Bound(ProducerVersion, 128),
        References = References is null
            ? null
            : References
                .Take(8)
                .Select(static reference => reference.Normalize())
                .ToArray()
    };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= maximum
                ? trimmed
                : trimmed[..maximum];
}

public sealed record EvidenceRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.Evidence;

    [JsonPropertyName("evidenceId")]
    public required string EvidenceId { get; init; }

    [JsonPropertyName("producer")]
    public required string Producer { get; init; }

    [JsonPropertyName("evidenceType")]
    public required string EvidenceType { get; init; }

    [JsonPropertyName("identity")]
    public required ExecutionIdentity Identity { get; init; }

    [JsonPropertyName("recordedAtUtc")]
    public DateTimeOffset RecordedAtUtc { get; init; }

    [JsonPropertyName("subjects")]
    public IReadOnlyList<EntityReference> Subjects { get; init; } = [];

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundedPayload? Payload { get; init; }

    [JsonPropertyName("reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceReference? Reference { get; init; }

    [JsonPropertyName("provenance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContractProvenance? Provenance { get; init; }

    public bool IsPassing => Status.Equals("pass", StringComparison.OrdinalIgnoreCase);

    public static EvidenceRecord Create(
        string producer,
        string evidenceType,
        ExecutionIdentity identity,
        string status,
        DateTimeOffset recordedAtUtc,
        IEnumerable<EntityReference>? subjects = null,
        object? payload = null,
        EvidenceReference? reference = null,
        ContractProvenance? provenance = null,
        int maximumPayloadBytes = 8_192)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ExecutionIdentity normalizedIdentity = identity.Normalize();
        string normalizedProducer = Bound(producer, 128) ?? "unknown";
        string normalizedType = Bound(evidenceType, 128) ?? "unknown";
        string normalizedStatus = Bound(status, 64)?.ToLowerInvariant() ?? "unknown";
        EntityReference[] normalizedSubjects = (subjects ?? [])
            .Take(32)
            .Select(static subject => subject.Normalize())
            .ToArray();
        string basis = string.Join(
            "\0",
            SharedContractSchemas.Evidence,
            normalizedProducer,
            normalizedType,
            normalizedIdentity.ComputeFingerprint(),
            normalizedStatus,
            recordedAtUtc.ToUniversalTime().ToString("O"));
        string evidenceId = "ev-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(basis))).ToLowerInvariant();
        return new EvidenceRecord
        {
            EvidenceId = evidenceId,
            Producer = normalizedProducer,
            EvidenceType = normalizedType,
            Identity = normalizedIdentity,
            RecordedAtUtc = recordedAtUtc.ToUniversalTime(),
            Subjects = normalizedSubjects,
            Status = normalizedStatus,
            Payload = BoundedPayload.From(payload, maximumPayloadBytes),
            Reference = reference?.Normalize(),
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

public sealed record EvidenceApplicabilityResult(
    bool Applicable,
    string Reason,
    IdentityComparisonResult IdentityComparison);

public static class EvidenceApplicability
{
    public static EvidenceApplicabilityResult Evaluate(
        EvidenceRecord? evidence,
        ExecutionIdentity? current,
        bool runtimeRequired = false)
    {
        if (evidence is null)
        {
            return new(false, "evidence-missing", new(
                IdentityMatchKind.Insufficient, ["evidence"], [], []));
        }
        if (!evidence.IsPassing)
        {
            return new(false, "evidence-result-not-pass", new(
                IdentityMatchKind.Insufficient, [], [], []));
        }

        IdentityComparisonResult comparison = ExecutionIdentityComparer.Compare(
            evidence.Identity,
            current,
            runtimeRequired
                ? IdentityComparisonRequirements.Runtime
                : IdentityComparisonRequirements.Static);
        string reason = comparison.IsMismatch
            ? "identity-mismatch"
            : comparison.IsApplicable(runtimeRequired
                ? IdentityComparisonRequirements.Runtime
                : IdentityComparisonRequirements.Static)
                ? "applicable"
                : "identity-insufficient";
        return new(
            comparison.IsApplicable(runtimeRequired
                ? IdentityComparisonRequirements.Runtime
                : IdentityComparisonRequirements.Static),
            reason,
            comparison);
    }
}
