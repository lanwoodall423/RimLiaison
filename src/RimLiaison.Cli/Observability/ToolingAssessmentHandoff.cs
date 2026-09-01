using System.Runtime.InteropServices;
using System.Text.Json;

namespace RimLiaison.Observability;

public static class ToolingAssessmentHandoffSchemas
{
    public const string Current = "rimliaison-tooling-assessment-handoff/v1";
}

public enum ToolingAssessmentDeliveryTransport
{
    Clipboard,
    Export,
    Cancelled,
    Failed
}

public sealed record ToolingAssessmentHandoffPacket(
    string SchemaVersion,
    ToolingAssessment Assessment,
    string AssessmentJson,
    string ClipboardText,
    string ExportText,
    bool ClipboardSufficient,
    IReadOnlyList<string> OmittedEvidence);

public sealed record ToolingAssessmentDeliveryResult(
    ToolingAssessmentDeliveryTransport Transport,
    bool Succeeded,
    bool UsedFallback,
    string? FailureReason);

/// <summary>
/// Builds and delivers the one canonical engineering handoff. The assessment
/// remains the same semantic object regardless of transport.
/// </summary>
public static class ToolingAssessmentHandoff
{
    public const int MaximumClipboardCharacters = 64_000;
    public const string AgentInstruction =
        "Assess this RimLiaison tooling finding. Determine root cause and classify it as a project defect, tooling defect, infrastructure issue, missing capability, inefficient workflow, or expected limitation. Determine whether RimLiaison or another tool should be fixed or extended. Recommend the smallest high-value remediation and, where appropriate, formulate implementation work.";

    public static ToolingAssessmentHandoffPacket Prepare(
        ToolingAssessment assessment,
        int maximumClipboardCharacters = MaximumClipboardCharacters)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (maximumClipboardCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClipboardCharacters));
        }

        string assessmentJson = SerializeAssessment(assessment);
        string clipboardText = AgentInstruction + "\n\nAssessment evidence:\n" + assessmentJson;
        string[] omittedEvidence = assessment.MissingEvidence
            .Concat(assessment.Findings.SelectMany(static finding => finding.MissingEvidence))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        bool clipboardSufficient = clipboardText.Length <= maximumClipboardCharacters &&
            !ContainsExplicitCriticalOmission(omittedEvidence);
        if (!clipboardSufficient && clipboardText.Length > maximumClipboardCharacters)
        {
            omittedEvidence = omittedEvidence
                .Concat(["Assessment exceeds the safe clipboard limit; export preserves the complete packet."])
                .Distinct(StringComparer.Ordinal)
                .Take(64)
                .ToArray();
        }

        return new ToolingAssessmentHandoffPacket(
            ToolingAssessmentHandoffSchemas.Current,
            assessment,
            assessmentJson,
            clipboardText,
            assessmentJson,
            clipboardSufficient,
            omittedEvidence);
    }

    public static ToolingAssessmentDeliveryResult Deliver(
        ToolingAssessmentHandoffPacket packet,
        Action<string> clipboardWriter,
        Func<string, bool> exportWriter)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(clipboardWriter);
        ArgumentNullException.ThrowIfNull(exportWriter);

        bool clipboardAttempted = packet.ClipboardSufficient;
        if (clipboardAttempted)
        {
            try
            {
                clipboardWriter(packet.ClipboardText);
                return new(ToolingAssessmentDeliveryTransport.Clipboard, true, false, null);
            }
            catch (Exception exception) when (
                exception is ExternalException or InvalidOperationException or IOException)
            {
                if (TryExport(packet, exportWriter, out ToolingAssessmentDeliveryResult fallback))
                {
                    return fallback with { UsedFallback = true, FailureReason = exception.Message };
                }

                return new(
                    ToolingAssessmentDeliveryTransport.Failed,
                    false,
                    true,
                    "Clipboard delivery failed and export delivery failed.");
            }
        }

        if (TryExport(packet, exportWriter, out ToolingAssessmentDeliveryResult result))
        {
            return result with { UsedFallback = true };
        }

        return new(
            ToolingAssessmentDeliveryTransport.Cancelled,
            false,
            true,
            packet.OmittedEvidence.Count == 0
                ? "Clipboard delivery was not diagnostically sufficient and export was cancelled."
                : string.Join(" ", packet.OmittedEvidence));
    }
    public static string SerializeAssessment(ToolingAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        string rawJson = JsonSerializer.Serialize(assessment, AgentObservabilityJson.Options);
        using JsonDocument document = JsonDocument.Parse(rawJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRedacted(writer, document.RootElement);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                    {
                        writer.WriteStringValue("[REDACTED]");
                    }
                    else if (string.Equals(property.Name, "command", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(AgentObservabilityData.SanitizeCommand(
                            property.Value.GetString(),
                            int.MaxValue));
                    }
                    else
                    {
                        WriteRedacted(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactString(value.GetString()));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static string RedactString(string? value)
    {
        string text = value ?? string.Empty;
        bool containsSensitiveValue = text.Contains("--token", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("--password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("token:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("secret:", StringComparison.OrdinalIgnoreCase);
        return containsSensitiveValue
            ? AgentObservabilityData.SanitizeCommand(text, int.MaxValue)
            : AgentObservabilityData.BoundText(text, int.MaxValue);
    }

    private static bool TryExport(
        ToolingAssessmentHandoffPacket packet,
        Func<string, bool> exportWriter,
        out ToolingAssessmentDeliveryResult result)
    {
        try
        {
            if (exportWriter(packet.ExportText))
            {
                result = new(ToolingAssessmentDeliveryTransport.Export, true, true, null);
                return true;
            }

            result = new(ToolingAssessmentDeliveryTransport.Cancelled, false, true, "Export was cancelled.");
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = new(ToolingAssessmentDeliveryTransport.Failed, false, true, exception.Message);
            return false;
        }
    }

    private static bool ContainsExplicitCriticalOmission(IEnumerable<string> values) =>
        values.Any(value => value.StartsWith("critical:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("required:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("degraded", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("not retained", StringComparison.OrdinalIgnoreCase));
}
