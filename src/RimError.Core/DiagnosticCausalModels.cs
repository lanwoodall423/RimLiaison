using System.Text.Json.Serialization;

namespace RimError.Core;

public sealed record DiagnosticCausalAnalysis
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("v")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("groups")]
    public DiagnosticRootCauseGroup[] Groups { get; init; } = [];

    [JsonPropertyName("links")]
    public DiagnosticCausalLink[] Links { get; init; } = [];
}

public sealed record DiagnosticRootCauseGroup
{
    [JsonPropertyName("root")]
    public required string RootId { get; init; }

    [JsonPropertyName("children")]
    public string[] ChildIds { get; init; } = [];

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("signals")]
    public string[] Signals { get; init; } = [];
}

public sealed record DiagnosticCausalLink
{
    [JsonPropertyName("parent")]
    public required string ParentId { get; init; }

    [JsonPropertyName("child")]
    public required string ChildId { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("signals")]
    public string[] Signals { get; init; } = [];
}

public sealed record DiagnosticRootCauseSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }

    [JsonPropertyName("def")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Def { get; init; }

    [JsonPropertyName("member")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Member { get; init; }

    [JsonPropertyName("asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    [JsonPropertyName("test")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Test { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }
}
