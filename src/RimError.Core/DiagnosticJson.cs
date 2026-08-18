using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimError.Core;

public static class DiagnosticJson
{
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(DiagnosticRecord diagnostic, bool includeStack = false)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return JsonSerializer.Serialize(
            includeStack ? diagnostic : WithoutStack(diagnostic),
            Options);
    }

    public static string Serialize(DiagnosticStoreSnapshot snapshot, bool includeStack = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (includeStack)
        {
            return JsonSerializer.Serialize(snapshot, Options);
        }

        var compactSnapshot = snapshot with
        {
            Items = snapshot.Items.Select(WithoutStack).ToArray()
        };

        return JsonSerializer.Serialize(compactSnapshot, Options);
    }

    public static string Serialize(DiagnosticLatestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static string Serialize(DiagnosticComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static string Serialize(DiagnosticCausalAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return JsonSerializer.Serialize(analysis, Options);
    }

    public static string Serialize(DiagnosticBaseline baseline, bool includeStack = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var value = includeStack
            ? baseline
            : baseline with
            {
                Items = baseline.Items.Select(WithoutStack).ToArray()
            };
        return JsonSerializer.Serialize(value, Options);
    }

    public static T? Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    private static DiagnosticRecord WithoutStack(DiagnosticRecord diagnostic) =>
        diagnostic with { StackFrames = null };

    private static JsonSerializerOptions CreateOptions() => new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}
