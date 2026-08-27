using System.Text.Json;

namespace RimLiaison.DevBridge;

internal static class DevBridgeProcessResponseParser
{
    public static bool TryParse(
        string? stdout,
        out DevBridgeProcessResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                stdout,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement root = document.RootElement;
            response = new DevBridgeProcessResponse(
                ReadBoolean(root, "success"),
                ReadBoolean(root, "healthy"),
                ReadInt(root, "exitCode"),
                ReadString(root, "errorCode"),
                ReadString(root, "error"),
                ReadString(root, "nextAction") ?? ReadString(root, "finalNextAction"),
                ReadString(root, "state") ?? ReadString(root, "status"),
                ReadString(root, "schemaVersion"),
                ReadString(root, "protocolVersion") ?? ReadString(root, "protocol"),
                ReadString(root, "buildIdentity") ??
                    ReadString(root, "buildVersion") ??
                    ReadString(root, "version"),
                root.TryGetProperty("findings", out JsonElement findings)
                    ? findings.Clone()
                    : null,
                root.TryGetProperty("runtimeIdentity", out JsonElement runtimeIdentity)
                    ? runtimeIdentity.Clone()
                    : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool? ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int result)
                ? result
                : null;
}
