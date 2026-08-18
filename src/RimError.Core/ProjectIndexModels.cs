using System.Text.Json.Serialization;

namespace RimError.Core;

public sealed record ProjectIndex
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("v")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("root")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("files")]
    public ProjectIndexFile[] Files { get; init; } = [];

    [JsonPropertyName("projects")]
    public ProjectIndexProject[] Projects { get; init; } = [];

    [JsonPropertyName("symbols")]
    public ProjectIndexSymbol[] Symbols { get; init; } = [];

    [JsonPropertyName("defs")]
    public ProjectIndexDefinition[] Definitions { get; init; } = [];

    [JsonPropertyName("refs")]
    public ProjectIndexReference[] References { get; init; } = [];
}

public sealed record ProjectIndexFile
{
    [JsonPropertyName("path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Length { get; init; }

    [JsonPropertyName("ticks")]
    public long LastWriteUtcTicks { get; init; }

    [JsonPropertyName("hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentHash { get; init; }
}

public sealed record ProjectIndexProject
{
    [JsonPropertyName("path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; init; }

    [JsonPropertyName("root")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootNamespace { get; init; }

    [JsonPropertyName("package")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; init; }
}

public sealed record ProjectIndexSymbol
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodName { get; init; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ParameterCount { get; init; }

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("project")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }

    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; init; }
}

public sealed record ProjectIndexDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("project")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }

    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; init; }
}

public sealed record ProjectIndexReference
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("project")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }

    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; init; }
}

public sealed record ProjectIndexOptions
{
    public const long DefaultMaxIndexedFileBytes = 4 * 1024 * 1024;

    public long MaxIndexedFileBytes { get; init; } = DefaultMaxIndexedFileBytes;

    public int MaxAttributionCandidates { get; init; } = 8;

    public void Validate()
    {
        if (MaxIndexedFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIndexedFileBytes));
        }

        if (MaxAttributionCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttributionCandidates));
        }
    }
}
