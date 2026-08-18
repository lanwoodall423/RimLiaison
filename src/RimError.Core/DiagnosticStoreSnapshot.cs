using System.Text.Json.Serialization;

namespace RimError.Core;

public sealed record DiagnosticStoreSnapshot
{
    [JsonPropertyName("v")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CapturedAt { get; init; }

    [JsonPropertyName("fp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FingerprintSchemaVersion { get; init; }

    [JsonPropertyName("rw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimWorldVersion { get; init; }

    [JsonPropertyName("mods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModProfile { get; init; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long InputBytes { get; init; }

    [JsonPropertyName("raw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long RawOccurrenceCount { get; init; }

    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long LinesRead { get; init; }

    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SourceCount { get; init; }

    [JsonPropertyName("malformed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long MalformedLineCount { get; init; }

    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TruncatedLineCount { get; init; }

    [JsonPropertyName("dropped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long DroppedDiagnosticCount { get; init; }

    [JsonPropertyName("items")]
    public DiagnosticRecord[] Items { get; init; } = [];

    [JsonPropertyName("causal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticCausalAnalysis? CausalAnalysis { get; init; }

    [JsonPropertyName("integration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticIntegrationState? Integration { get; init; }
}
