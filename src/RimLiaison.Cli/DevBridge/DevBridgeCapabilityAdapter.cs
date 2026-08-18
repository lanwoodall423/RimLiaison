using System.Text.Json;

namespace RimLiaison.DevBridge;

public static class DevBridgeCapabilitySchemas
{
    public const string Output = "rimtest-capabilities/v1";
}

public enum DevBridgeCapabilityOutcome
{
    Success,
    Unavailable,
    InfrastructureFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public sealed record DevBridgeCapabilityStatus(
    DevBridgeCapabilityOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? ResponseSchema = null,
    string? NextAction = "DevBridge.cmd doctor --json")
{
    public bool IsSuccess => Outcome == DevBridgeCapabilityOutcome.Success;
}

public sealed record DevBridgeCapabilityQuery(
    string? Text = null,
    string? Category = null,
    string? ProviderId = null,
    string? Source = null,
    int Limit = 20);

public sealed record DevBridgeCapabilityParameter(
    string Name,
    string? Type = null,
    string? Description = null,
    bool? Required = null,
    JsonElement? DefaultValue = null);

public sealed record DevBridgeCapability(
    string Id,
    IReadOnlyList<string> Aliases,
    string? Title,
    string? Summary,
    string? Category,
    string? ProviderId,
    string? Source,
    IReadOnlyList<DevBridgeCapabilityParameter> Parameters,
    bool? ReadOnly);

public sealed record DevBridgeCapabilityDiscoveryResult(
    DevBridgeCapabilityStatus Status,
    IReadOnlyList<DevBridgeCapability> Capabilities,
    int TotalMatches,
    bool Truncated);

public interface IDevBridgeCapabilityAdapter
{
    Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
        DevBridgeCapabilityQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only RimLiaison adapter for the DevBridge-routed RimBridge tools registry.
/// This adapter deliberately has no tool-call or lifecycle API.
/// </summary>
public sealed class DevBridgeCapabilityAdapter : IDevBridgeCapabilityAdapter
{
    private const int MaxMessageLength = 512;
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeCapabilityAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
        DevBridgeCapabilityQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Limit is < 1 or > 100)
        {
            return Failure(
                DevBridgeCapabilityOutcome.IncompatibleSchema,
                "RIMTEST_CAPABILITIES_LIMIT_INVALID",
                "Capability result limit must be from 1 through 100.");
        }

        DevBridgeProcessResult process = await InvokeAsync(cancellationToken)
            .ConfigureAwait(false);
        return Parse(process, query);
    }

    private async Task<DevBridgeProcessResult> InvokeAsync(
        CancellationToken cancellationToken)
    {
        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.RootPath,
            ["--root", options.RootPath, "bridge", "tools", "--json"],
            options.ShowPlanTimeout,
            options.MaxStdoutBytes,
            options.MaxStderrBytes);

        try
        {
            return await transport.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                Cancelled: true);
        }
        catch (Exception exception)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: exception.Message);
        }
    }

    private static DevBridgeCapabilityDiscoveryResult Parse(
        DevBridgeProcessResult process,
        DevBridgeCapabilityQuery query)
    {
        if (process.Cancelled)
        {
            return Failure(
                DevBridgeCapabilityOutcome.Cancelled,
                "RIMTEST_CANCELLED",
                "The DevBridge capability request was cancelled.",
                process.ExitCode);
        }

        if (process.TimedOut)
        {
            return Failure(
                DevBridgeCapabilityOutcome.Timeout,
                "DEVBRIDGE_CAPABILITIES_TIMEOUT",
                "DevBridge did not return the RimBridge capability registry in time.",
                process.ExitCode);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return Failure(
                DevBridgeCapabilityOutcome.InfrastructureFailure,
                "DEVBRIDGE_START_FAILED",
                "RimLiaison could not start DevBridge.",
                process.ExitCode);
        }

        if (process.StdoutTruncated)
        {
            return Failure(
                DevBridgeCapabilityOutcome.InfrastructureFailure,
                "DEVBRIDGE_CAPABILITIES_OUTPUT_TRUNCATED",
                "DevBridge returned more capability data than RimLiaison can safely inspect.",
                process.ExitCode);
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failure(
                DevBridgeCapabilityOutcome.InfrastructureFailure,
                "DEVBRIDGE_CAPABILITIES_NO_RESPONSE",
                "DevBridge returned no structured capability response.",
                process.ExitCode);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                process.Stdout,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    DevBridgeCapabilityOutcome.MalformedResponse,
                    "RIMBRIDGE_CAPABILITIES_RESPONSE_INVALID",
                    "DevBridge capability response must be a JSON object.",
                    process.ExitCode);
            }

            if (TryGetOptionalString(root, out string? schema, "schemaVersion") &&
                schema is not null &&
                !IsSupportedSchema(schema))
            {
                return Failure(
                    DevBridgeCapabilityOutcome.IncompatibleSchema,
                    "RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED",
                    "DevBridge returned an unsupported capability response schema.",
                    process.ExitCode,
                    schema);
            }

            if (!TryGetOptionalBoolean(root, out bool? rootSuccess, "success"))
            {
                return Failure(
                    DevBridgeCapabilityOutcome.MalformedResponse,
                    "RIMBRIDGE_CAPABILITIES_RESPONSE_INVALID",
                    "DevBridge capability response has an invalid success field.",
                    process.ExitCode,
                    schema);
            }

            if (rootSuccess == false)
            {
                return RouteFailure(root, process, schema);
            }

            if (!TryGetCapabilityResult(
                    root,
                    process,
                    schema,
                    out JsonElement result,
                    out DevBridgeCapabilityDiscoveryResult? failure))
            {
                return failure!;
            }

            if (!TryGetCapabilityArray(result, out JsonElement capabilities, out string? listError))
            {
                return Failure(
                    DevBridgeCapabilityOutcome.IncompatibleSchema,
                    "RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED",
                    listError!,
                    process.ExitCode,
                    schema);
            }

            var parsed = new List<DevBridgeCapability>();
            foreach (JsonElement descriptor in capabilities.EnumerateArray())
            {
                if (!TryParseCapability(descriptor, out DevBridgeCapability? capability, out string? descriptorError))
                {
                    return Failure(
                        DevBridgeCapabilityOutcome.MalformedResponse,
                        "RIMBRIDGE_CAPABILITY_DESCRIPTOR_INVALID",
                        descriptorError!,
                        process.ExitCode,
                        schema);
                }

                parsed.Add(capability!);
            }

            IReadOnlyList<DevBridgeCapability> matches = parsed
                .Where(capability => Matches(capability, query))
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray();
            int totalMatches = matches.Count;
            bool truncated = totalMatches > query.Limit;
            IReadOnlyList<DevBridgeCapability> bounded = matches
                .Take(query.Limit)
                .ToArray();

            if (process.ExitCode is > 0)
            {
                return Failure(
                    DevBridgeCapabilityOutcome.InfrastructureFailure,
                    "DEVBRIDGE_RESULT_CONFLICT",
                    "DevBridge returned capability data with a non-success process result.",
                    process.ExitCode,
                    schema);
            }

            return new DevBridgeCapabilityDiscoveryResult(
                new DevBridgeCapabilityStatus(
                    DevBridgeCapabilityOutcome.Success,
                    ProcessExitCode: process.ExitCode,
                    ResponseSchema: schema,
                    NextAction: null),
                bounded,
                totalMatches,
                truncated);
        }
        catch (JsonException exception)
        {
            return Failure(
                DevBridgeCapabilityOutcome.MalformedResponse,
                "RIMBRIDGE_CAPABILITIES_JSON_INVALID",
                "DevBridge returned malformed capability JSON: " + exception.Message,
                process.ExitCode);
        }
    }

    private static DevBridgeCapabilityDiscoveryResult RouteFailure(
        JsonElement root,
        DevBridgeProcessResult process,
        string? schema)
    {
        string errorCode = GetStringOrDefault(root, "errorCode", "RIMBRIDGE_CAPABILITIES_UNAVAILABLE");
        string error = GetStringOrDefault(
            root,
            "error",
            "DevBridge could not provide the live-game capability registry.");
        return Failure(
            IsUnavailableCode(errorCode)
                ? DevBridgeCapabilityOutcome.Unavailable
                : DevBridgeCapabilityOutcome.InfrastructureFailure,
            errorCode,
            error,
            process.ExitCode,
            schema);
    }

    private static bool TryGetCapabilityResult(
        JsonElement root,
        DevBridgeProcessResult process,
        string? schema,
        out JsonElement result,
        out DevBridgeCapabilityDiscoveryResult? failure)
    {
        failure = null;
        if (TryGetProperty(root, out JsonElement route, "rimBridgeRoute"))
        {
            if (route.ValueKind != JsonValueKind.Object)
            {
                failure = Failure(
                    DevBridgeCapabilityOutcome.MalformedResponse,
                    "RIMBRIDGE_CAPABILITIES_RESPONSE_INVALID",
                    "DevBridge returned an invalid RimBridge route envelope.",
                    process.ExitCode,
                    schema);
                result = default;
                return false;
            }

            if (!TryGetOptionalBoolean(route, out bool? routeSuccess, "success"))
            {
                failure = Failure(
                    DevBridgeCapabilityOutcome.MalformedResponse,
                    "RIMBRIDGE_CAPABILITIES_RESPONSE_INVALID",
                    "DevBridge returned an invalid RimBridge route status.",
                    process.ExitCode,
                    schema);
                result = default;
                return false;
            }

            if (routeSuccess == false)
            {
                failure = RouteFailure(route, process, schema);
                result = default;
                return false;
            }

            if (!TryGetProperty(route, out result, "result") ||
                result.ValueKind != JsonValueKind.Object)
            {
                failure = Failure(
                    DevBridgeCapabilityOutcome.IncompatibleSchema,
                    "RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED",
                    "DevBridge RimBridge route did not contain a capability result.",
                    process.ExitCode,
                    schema);
                result = default;
                return false;
            }

            return true;
        }

        if (TryGetProperty(root, out result, "result") &&
            result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        // This fallback keeps the adapter compatible with a direct tools/list
        // projection used by older DevBridge test fixtures. It is still a
        // discovery-only response and never exposes a generic call surface.
        if (TryGetProperty(root, out JsonElement directTools, "tools", "capabilities") &&
            directTools.ValueKind == JsonValueKind.Array)
        {
            result = root;
            return true;
        }

        failure = Failure(
            DevBridgeCapabilityOutcome.IncompatibleSchema,
            "RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED",
            "DevBridge response did not contain a RimBridge capability result.",
            process.ExitCode,
            schema);
        result = default;
        return false;
    }

    private static bool TryGetCapabilityArray(
        JsonElement result,
        out JsonElement capabilities,
        out string? error)
    {
        error = null;
        if (!TryGetProperty(result, out capabilities, "tools", "capabilities"))
        {
            error = "RimBridge capability result did not contain tools.";
            return false;
        }

        if (capabilities.ValueKind != JsonValueKind.Array)
        {
            error = "RimBridge capability result tools field must be an array.";
            return false;
        }

        return true;
    }

    private static bool TryParseCapability(
        JsonElement value,
        out DevBridgeCapability? capability,
        out string? error)
    {
        capability = null;
        error = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "Each RimBridge capability must be a JSON object.";
            return false;
        }

        if (TryGetProperty(value, out JsonElement nested, "descriptor") &&
            nested.ValueKind == JsonValueKind.Object)
        {
            value = nested;
        }

        if (!TryGetOptionalString(value, out string? id, "id", "capabilityId", "name") ||
            string.IsNullOrWhiteSpace(id))
        {
            error = "Each RimBridge capability must have a non-empty id or name.";
            return false;
        }

        if (!TryGetOptionalString(value, out string? title, "title") ||
            !TryGetOptionalString(value, out string? summary, "summary", "description") ||
            !TryGetOptionalString(value, out string? category, "category") ||
            !TryGetOptionalString(value, out string? providerId, "providerId", "provider") ||
            !TryGetOptionalString(value, out string? source, "source"))
        {
            error = $"Capability {id} has an invalid authoring metadata field.";
            return false;
        }

        if (!TryGetAliases(value, out List<string> aliases, out error) ||
            !TryGetParameters(value, out List<DevBridgeCapabilityParameter> parameters, out error) ||
            !TryGetReadOnly(value, out bool? readOnly, out error))
        {
            return false;
        }

        capability = new DevBridgeCapability(
            id!,
            aliases,
            string.IsNullOrWhiteSpace(title) ? id : title,
            summary,
            category,
            providerId,
            source,
            parameters,
            readOnly);
        return true;
    }

    private static bool TryGetAliases(
        JsonElement value,
        out List<string> aliases,
        out string? error)
    {
        aliases = [];
        error = null;
        if (!TryGetProperty(value, out JsonElement aliasValue, "aliases", "alias"))
        {
            return true;
        }

        if (aliasValue.ValueKind == JsonValueKind.String)
        {
            if (!string.IsNullOrWhiteSpace(aliasValue.GetString()))
            {
                aliases.Add(aliasValue.GetString()!);
            }

            return true;
        }

        if (aliasValue.ValueKind != JsonValueKind.Array)
        {
            error = "Capability aliases must be a string or array of strings.";
            return false;
        }

        foreach (JsonElement item in aliasValue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                error = "Capability aliases must contain only non-empty strings.";
                return false;
            }

            aliases.Add(item.GetString()!);
        }

        return true;
    }

    private static bool TryGetParameters(
        JsonElement value,
        out List<DevBridgeCapabilityParameter> parameters,
        out string? error)
    {
        parameters = [];
        error = null;
        if (TryGetProperty(value, out JsonElement parameterValue, "parameters"))
        {
            if (parameterValue.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (parameterValue.ValueKind != JsonValueKind.Array)
            {
                error = "Capability parameters must be an array.";
                return false;
            }

            foreach (JsonElement item in parameterValue.EnumerateArray())
            {
                if (!TryParseParameter(item, out DevBridgeCapabilityParameter? parameter, out error))
                {
                    return false;
                }

                parameters.Add(parameter!);
            }

            return true;
        }

        if (!TryGetProperty(value, out JsonElement inputSchema, "inputSchema"))
        {
            return true;
        }

        return TryParseInputSchema(inputSchema, out parameters, out error);
    }

    private static bool TryParseParameter(
        JsonElement value,
        out DevBridgeCapabilityParameter? parameter,
        out string? error)
    {
        parameter = null;
        error = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryGetOptionalString(value, out string? name, "name") ||
            string.IsNullOrWhiteSpace(name))
        {
            error = "Each capability parameter must have a non-empty name.";
            return false;
        }

        if (!TryGetOptionalString(value, out string? type, "parameterType", "type") ||
            !TryGetOptionalString(value, out string? description, "description") ||
            !TryGetOptionalBoolean(value, out bool? required, "required"))
        {
            error = $"Capability parameter {name} has an invalid field.";
            return false;
        }

        JsonElement? defaultValue = null;
        if (TryGetProperty(value, out JsonElement rawDefault, "defaultValue", "default"))
        {
            defaultValue = rawDefault.Clone();
        }

        parameter = new DevBridgeCapabilityParameter(
            name!,
            type,
            description,
            required,
            defaultValue);
        return true;
    }

    private static bool TryParseInputSchema(
        JsonElement value,
        out List<DevBridgeCapabilityParameter> parameters,
        out string? error)
    {
        parameters = [];
        error = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "Capability inputSchema must be an object.";
            return false;
        }

        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetProperty(value, out JsonElement requiredValue, "required"))
        {
            if (requiredValue.ValueKind != JsonValueKind.Array)
            {
                error = "Capability inputSchema required must be an array.";
                return false;
            }

            foreach (JsonElement item in requiredValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(item.GetString()))
                {
                    error = "Capability inputSchema required must contain strings.";
                    return false;
                }

                requiredNames.Add(item.GetString()!);
            }
        }

        if (!TryGetProperty(value, out JsonElement properties, "properties"))
        {
            return true;
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            error = "Capability inputSchema properties must be an object.";
            return false;
        }

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            JsonElement definition = property.Value;
            if (definition.ValueKind != JsonValueKind.Object)
            {
                error = $"Capability inputSchema parameter {property.Name} must be an object.";
                return false;
            }

            if (!TryGetOptionalString(definition, out string? type, "parameterType", "type") ||
                !TryGetOptionalString(definition, out string? description, "description"))
            {
                error = $"Capability inputSchema parameter {property.Name} has an invalid field.";
                return false;
            }

            JsonElement? defaultValue = null;
            if (TryGetProperty(definition, out JsonElement rawDefault, "defaultValue", "default"))
            {
                defaultValue = rawDefault.Clone();
            }

            parameters.Add(new DevBridgeCapabilityParameter(
                property.Name,
                type,
                description,
                requiredNames.Contains(property.Name),
                defaultValue));
        }

        return true;
    }

    private static bool TryGetReadOnly(
        JsonElement value,
        out bool? readOnly,
        out string? error)
    {
        readOnly = null;
        error = null;
        if (TryGetProperty(value, out JsonElement readOnlyValue, "readOnly", "isReadOnly"))
        {
            if (readOnlyValue.ValueKind != JsonValueKind.True &&
                readOnlyValue.ValueKind != JsonValueKind.False)
            {
                error = "Capability readOnly must be a boolean.";
                return false;
            }

            readOnly = readOnlyValue.GetBoolean();
            return true;
        }

        if (TryGetProperty(value, out JsonElement mutatingValue, "mutating", "isMutating"))
        {
            if (mutatingValue.ValueKind != JsonValueKind.True &&
                mutatingValue.ValueKind != JsonValueKind.False)
            {
                error = "Capability mutating must be a boolean.";
                return false;
            }

            readOnly = !mutatingValue.GetBoolean();
        }

        return true;
    }

    private static bool Matches(
        DevBridgeCapability capability,
        DevBridgeCapabilityQuery query)
    {
        if (query.Category is not null &&
            !string.Equals(capability.Category, query.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.ProviderId is not null &&
            !string.Equals(capability.ProviderId, query.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Source is not null &&
            !string.Equals(capability.Source, query.Source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return true;
        }

        string text = query.Text.Trim();
        if (Contains(capability.Id, text) ||
            capability.Aliases.Any(alias => Contains(alias, text)) ||
            Contains(capability.Title, text) ||
            Contains(capability.Summary, text) ||
            Contains(capability.Category, text) ||
            Contains(capability.ProviderId, text) ||
            Contains(capability.Source, text))
        {
            return true;
        }

        return capability.Parameters.Any(parameter =>
            Contains(parameter.Name, text) ||
            Contains(parameter.Type, text) ||
            Contains(parameter.Description, text));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSupportedSchema(string schema) =>
        schema.Equals("v1", StringComparison.OrdinalIgnoreCase) ||
        schema.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
        schema.EndsWith("-v1", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnavailableCode(string code) =>
        code.Contains("NOT_READY", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("LEASE_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("NO_ACTIVE_GENERATION", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("PROCESS_IDENTITY", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("ENDPOINT", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("AUTH", StringComparison.OrdinalIgnoreCase);

    private static DevBridgeCapabilityDiscoveryResult Failure(
        DevBridgeCapabilityOutcome outcome,
        string errorCode,
        string error,
        int? processExitCode = null,
        string? responseSchema = null)
    {
        return new DevBridgeCapabilityDiscoveryResult(
            new DevBridgeCapabilityStatus(
                outcome,
                errorCode,
                BoundMessage(error),
                processExitCode,
                responseSchema,
                outcome == DevBridgeCapabilityOutcome.Cancelled
                    ? null
                    : "DevBridge.cmd doctor --json"),
            [],
            0,
            false);
    }

    private static string GetStringOrDefault(
        JsonElement value,
        string propertyName,
        string fallback)
    {
        return TryGetOptionalString(value, out string? result, propertyName) &&
            !string.IsNullOrWhiteSpace(result)
            ? result!
            : fallback;
    }

    private static string BoundMessage(string value) =>
        value.Length <= MaxMessageLength
            ? value
            : value[..MaxMessageLength];

    private static bool TryGetProperty(
        JsonElement value,
        out JsonElement result,
        params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in names)
            {
                if (value.TryGetProperty(name, out result))
                {
                    return true;
                }
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (names.Any(name =>
                        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    result = property.Value;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static bool TryGetOptionalString(
        JsonElement value,
        out string? result,
        params string[] names)
    {
        result = null;
        if (!TryGetProperty(value, out JsonElement property, names))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return true;
    }

    private static bool TryGetOptionalBoolean(
        JsonElement value,
        out bool? result,
        params string[] names)
    {
        result = null;
        if (!TryGetProperty(value, out JsonElement property, names))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.True &&
            property.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        result = property.GetBoolean();
        return true;
    }
}
