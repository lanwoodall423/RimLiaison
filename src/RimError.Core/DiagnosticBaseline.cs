using System.Text.Json.Serialization;

namespace RimError.Core;

public sealed record DiagnosticBaseline
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("v")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StoreSchemaVersion { get; init; }

    [JsonPropertyName("fp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FingerprintSchemaVersion { get; init; }

    [JsonPropertyName("rw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimWorldVersion { get; init; }

    [JsonPropertyName("mods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModProfile { get; init; }

    [JsonPropertyName("items")]
    public DiagnosticRecord[] Items { get; init; } = [];

    public static DiagnosticBaseline FromSnapshot(
        string name,
        DiagnosticStoreSnapshot snapshot,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DiagnosticBaselineNames.Validate(name);

        return new DiagnosticBaseline
        {
            Name = name,
            CreatedAt = createdAt,
            StoreSchemaVersion = snapshot.SchemaVersion,
            FingerprintSchemaVersion = snapshot.FingerprintSchemaVersion,
            RimWorldVersion = snapshot.RimWorldVersion,
            ModProfile = snapshot.ModProfile,
            Items = snapshot.Items
        };
    }
}

public static class DiagnosticBaselineNames
{
    public const string Default = "default";

    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            throw new ArgumentException(
                "Baseline names must contain 1 to 64 safe characters.",
                nameof(name));
        }

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            {
                continue;
            }

            throw new ArgumentException(
                "Baseline names may contain only letters, digits, '.', '-' and '_'.",
                nameof(name));
        }
    }
}
