using System.Text.Json;

namespace RimTest.RimContext;

public sealed class RimContextImpactAdapter : IRimContextImpactAdapter
{
    private readonly IRimContextProcessTransport transport;
    private readonly RimContextAdapterOptions options;

    public RimContextImpactAdapter(
        IRimContextProcessTransport transport,
        RimContextAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public async Task<RimContextImpactResult> AffectedAsync(
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (changedPaths.Count == 0 || changedPaths.Any(string.IsNullOrWhiteSpace))
        {
            return Failure(
                RimContextImpactOutcome.InvalidInput,
                "RIMCONTEXT_CHANGED_PATHS_INVALID",
                "At least one non-empty changed path is required.");
        }

        RimContextProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    CreateRequest(changedPaths),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                RimContextImpactOutcome.Cancelled,
                "RIMTEST_CANCELLED",
                "The RimContext client process was cancelled.");
        }
        catch (Exception exception)
        {
            return Failure(
                RimContextImpactOutcome.InfrastructureFailure,
                "RIMCONTEXT_TRANSPORT_FAILED",
                BoundError(exception.Message));
        }

        if (process.Cancelled)
        {
            return Failure(
                RimContextImpactOutcome.Cancelled,
                "RIMTEST_CANCELLED",
                "The RimContext client process was cancelled.",
                process.ExitCode);
        }

        if (process.TimedOut)
        {
            return Failure(
                RimContextImpactOutcome.Timeout,
                "RIMCONTEXT_CLIENT_TIMEOUT",
                "The bounded RimContext client process timed out.",
                process.ExitCode);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return Failure(
                RimContextImpactOutcome.Unavailable,
                "RIMCONTEXT_START_FAILED",
                BoundError(process.StartError),
                process.ExitCode);
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            return Failure(
                RimContextImpactOutcome.InfrastructureFailure,
                "RIMCONTEXT_OUTPUT_LIMIT_EXCEEDED",
                "The RimContext client exceeded the adapter output bound.",
                process.ExitCode);
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failure(
                RimContextImpactOutcome.Unavailable,
                "RIMCONTEXT_NO_STRUCTURED_RESPONSE",
                "RimContext produced no structured JSON response.",
                process.ExitCode);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                process.Stdout,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException)
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_MALFORMED_JSON",
                "RimContext returned malformed structured JSON.",
                process.ExitCode);
        }

        using (document)
        {
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_RESPONSE_INVALID",
                "RimContext JSON response root must be an object.",
                process.ExitCode);
        }

        if (!TryGetString(root, "schemaVersion", out string? schemaVersion))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_SCHEMA_MISSING",
                "RimContext response did not contain schemaVersion.",
                process.ExitCode);
        }

        if (!string.Equals(schemaVersion, RimContextSchemas.Envelope, StringComparison.Ordinal))
        {
            return Failure(
                RimContextImpactOutcome.IncompatibleSchema,
                "RIMCONTEXT_SCHEMA_UNSUPPORTED",
                $"Expected {RimContextSchemas.Envelope}; received {schemaVersion}.",
                process.ExitCode,
                schemaVersion);
        }

        if (!TryGetString(root, "command", out string? command) ||
            !string.Equals(command, "affected", StringComparison.Ordinal))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_COMMAND_INVALID",
                "RimContext response was not an affected command envelope.",
                process.ExitCode,
                schemaVersion);
        }

        if (!TryGetString(root, "status", out string? responseStatus))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_STATUS_MISSING",
                "RimContext response did not contain status.",
                process.ExitCode,
                schemaVersion);
        }

        if (string.Equals(responseStatus, "error", StringComparison.Ordinal))
        {
            return ParseError(root, process, schemaVersion!);
        }

        if (responseStatus is not ("ok" or "partial"))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_STATUS_INVALID",
                "RimContext response status was not ok, partial, or error.",
                process.ExitCode,
                schemaVersion);
        }

        if (!root.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return Failure(
                responseStatus == "partial"
                    ? RimContextImpactOutcome.Unknown
                    : RimContextImpactOutcome.MalformedResponse,
                responseStatus == "partial"
                    ? "RIMCONTEXT_RESULT_TRUNCATED"
                    : "RIMCONTEXT_DATA_MISSING",
                responseStatus == "partial"
                    ? "RimContext returned a partial affected result."
                    : "RimContext response did not contain affected data.",
                process.ExitCode,
                schemaVersion);
        }

        if (!TryGetStringArray(data, "changed", out List<string> changed) ||
            changed.Count == 0 ||
            !TryGetBoolean(data, "truncated", out bool dataTruncated))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_DATA_INVALID",
                "RimContext affected data did not contain typed changed/truncated fields.",
                process.ExitCode,
                schemaVersion);
        }

        if (!TryGetMatches(data, "direct", "direct", out List<RimContextImpact> direct) ||
            !TryGetMatches(data, "dependent", "dependent", out List<RimContextImpact> dependent) ||
            !TryGetMatches(data, "runtime_risk", "runtimeRisk", out List<RimContextImpact> runtimeRisk))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_MATCHES_INVALID",
                "RimContext affected data contained an invalid impact entry.",
                process.ExitCode,
                schemaVersion);
        }

        if (!TryGetMetaTruncated(root, out bool envelopeTruncated))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_META_INVALID",
                "RimContext response meta.truncated was not a boolean.",
                process.ExitCode,
                schemaVersion);
        }
        var impacts = direct
            .Concat(dependent)
            .Concat(runtimeRisk)
            .ToArray();
        bool truncated = dataTruncated || envelopeTruncated || responseStatus == "partial";
        if (truncated)
        {
            return new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unknown,
                    "RIMCONTEXT_RESULT_TRUNCATED",
                    "RimContext did not prove that the affected result was complete.",
                    process.ExitCode,
                    schemaVersion),
                changed,
                impacts,
                true);
        }

        if (process.ExitCode is > 0)
        {
            return Failure(
                RimContextImpactOutcome.InfrastructureFailure,
                "RIMCONTEXT_RESULT_CONFLICT",
                "RimContext returned a successful envelope with a failing process exit code.",
                process.ExitCode,
                schemaVersion);
        }

        return new RimContextImpactResult(
            new RimContextAdapterStatus(
                RimContextImpactOutcome.Success,
                null,
                null,
                process.ExitCode,
                ResponseSchema: schemaVersion),
            changed,
            impacts,
            false);
        }
    }

    private RimContextProcessRequest CreateRequest(IReadOnlyList<string> changedPaths)
    {
        var arguments = new List<string>
        {
            "affected"
        };
        arguments.AddRange(changedPaths);
        arguments.Add("--root");
        arguments.Add(options.RootPath);
        if (!string.IsNullOrWhiteSpace(options.StorePath))
        {
            arguments.Add("--store");
            arguments.Add(options.StorePath!);
        }

        arguments.Add("--depth");
        arguments.Add(options.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--limit");
        arguments.Add(options.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--json");
        arguments.Add("--compact");

        return new RimContextProcessRequest(
            options.CommandPath,
            options.RootPath,
            arguments,
            options.Timeout,
            options.MaxStdoutBytes,
            options.MaxStderrBytes);
    }

    private static RimContextImpactResult ParseError(
        JsonElement root,
        RimContextProcessResult process,
        string schemaVersion)
    {
        if (!TryGetString(root, "code", out string? code))
        {
            return Failure(
                RimContextImpactOutcome.MalformedResponse,
                "RIMCONTEXT_ERROR_INVALID",
                "RimContext error response did not contain code.",
                process.ExitCode,
                schemaVersion);
        }

        TryGetString(root, "message", out string? message, allowNull: true);
        RimContextImpactOutcome outcome = code switch
        {
            "INVALID_ARGUMENT" or "LIMIT_EXCEEDED" => RimContextImpactOutcome.InvalidInput,
            "PATH_NOT_FOUND" or "INPUT_READ_FAILED" or "NOT_FOUND" => RimContextImpactOutcome.Unknown,
            "INDEX_NOT_FOUND" or "INDEX_INCOMPATIBLE" or "ROOT_MISMATCH" or
                "STORE_LOCKED" or "STORE_FAILED" => RimContextImpactOutcome.Unavailable,
            _ => RimContextImpactOutcome.InfrastructureFailure
        };
        return Failure(
            outcome,
                code!,
            BoundError(message),
            process.ExitCode,
            schemaVersion);
    }

    private static bool TryGetMatches(
        JsonElement data,
        string propertyName,
        string tier,
        out List<RimContextImpact> matches)
    {
        matches = [];
        if (!data.TryGetProperty(propertyName, out JsonElement value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "kind", out string? kind) ||
                !TryGetString(item, "id", out string? id) ||
                !TryGetNullableString(item, "name", out string? name) ||
                !TryGetNullableString(item, "file", out string? file) ||
                !TryGetNullableInt(item, "line", out int? line) ||
                !TryGetNullableString(item, "reason", out string? reason) ||
                !TryGetNullableString(item, "confidence", out string? confidence))
            {
                return false;
            }

            matches.Add(new RimContextImpact(
                tier,
                kind!,
                id!,
                NullIfWhiteSpace(name),
                NullIfWhiteSpace(file),
                line,
                NullIfWhiteSpace(reason),
                NullIfWhiteSpace(confidence)));
        }

        return true;
    }

    private static bool TryGetMetaTruncated(
        JsonElement root,
        out bool truncated)
    {
        truncated = false;
        if (!root.TryGetProperty("meta", out JsonElement meta) ||
            meta.ValueKind != JsonValueKind.Object)
        {
            return !root.TryGetProperty("meta", out _);
        }

        if (!meta.TryGetProperty("truncated", out JsonElement value))
        {
            return true;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        truncated = value.GetBoolean();
        return true;
    }

    private static bool TryGetStringArray(
        JsonElement parent,
        string propertyName,
        out List<string> values)
    {
        values = [];
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                return false;
            }

            values.Add(item.GetString()!);
        }

        return true;
    }

    private static bool TryGetString(
        JsonElement parent,
        string propertyName,
        out string? value,
        bool allowNull = false)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null && allowNull)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return allowNull || !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetNullableString(
        JsonElement parent,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryGetBoolean(
        JsonElement parent,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetNullableInt(
        JsonElement parent,
        string propertyName,
        out int? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static RimContextImpactResult Failure(
        RimContextImpactOutcome outcome,
        string errorCode,
        string? error,
        int? processExitCode = null,
        string? responseSchema = null) =>
        new(
            new RimContextAdapterStatus(
                outcome,
                errorCode,
                BoundError(error),
                processExitCode,
                responseSchema),
            [],
            [],
            false);

    private static string? BoundError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return error.Length <= 512 ? error : error[..512];
    }
}
