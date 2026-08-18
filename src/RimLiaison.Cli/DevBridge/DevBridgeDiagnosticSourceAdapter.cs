using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimLiaison.DevBridge;

/// <summary>
/// Obtains only DevBridge's authoritative, generation-scoped semantic log
/// projection. RimLiaison never discovers or reads Player.log itself.
/// </summary>
public sealed class DevBridgeDiagnosticSourceAdapter : IDevBridgeDiagnosticSourceAdapter
{
    private const int MaxJsonDepth = 16;
    private const int MaxSourceBytes = 64 * 1024;

    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeDiagnosticSourceAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DevBridgeDiagnosticSourceResult> AcquireAsync(
        string testId,
        DevBridgeRecipeRunResult run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(testId))
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_REQUEST_INVALID",
                "A test id is required for scoped diagnostics.");
        }

        if (run.Generation is not int generation || generation <= 0)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISSING",
                "The failed run did not provide a positive DevBridge generation.");
        }

        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        [
                            "logs",
                            "query",
                            "--generation",
                            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "--since-launch",
                            "--severity",
                            "ERROR",
                            "--limit",
                            "64",
                            "--json"
                        ],
                        options.ShowPlanTimeout,
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Cancelled,
                "DEVBRIDGE_DIAGNOSTIC_CANCELLED",
                "The scoped DevBridge diagnostic request was cancelled.");
        }
        catch (Exception)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_SOURCE_FAILED",
                "DevBridge could not provide scoped diagnostics.");
        }

        if (process.Cancelled)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Cancelled,
                "DEVBRIDGE_DIAGNOSTIC_CANCELLED",
                "The scoped DevBridge diagnostic request was cancelled.",
                process.ExitCode,
                process.Stderr);
        }

        if (process.TimedOut)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Timeout,
                "DEVBRIDGE_DIAGNOSTIC_TIMEOUT",
                "The bounded DevBridge diagnostic request timed out.",
                process.ExitCode,
                process.Stderr);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_START_FAILED",
                "DevBridge did not start for scoped diagnostics.",
                process.ExitCode,
                process.Stderr);
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_OUTPUT_LIMIT_EXCEEDED",
                "DevBridge exceeded the bounded diagnostic response limit.",
                process.ExitCode,
                process.Stderr);
        }

        if (process.ExitCode is null or > 1)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.Unavailable,
                "DEVBRIDGE_DIAGNOSTIC_QUERY_FAILED",
                "DevBridge rejected the scoped diagnostic query.",
                process.ExitCode,
                process.Stderr);
        }

        return Parse(process, generation);
    }

    private static DevBridgeDiagnosticSourceResult Parse(
        DevBridgeProcessResult process,
        int requestedGeneration)
    {
        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.MalformedResponse,
                "DEVBRIDGE_DIAGNOSTIC_RESPONSE_MISSING",
                "DevBridge returned no scoped diagnostic response.",
                process.ExitCode,
                process.Stderr);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                process.Stdout,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxJsonDepth
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "schemaVersion", out string? schema) ||
                !string.Equals(schema, DevBridgeDiagnosticSchemas.LogsQuery, StringComparison.Ordinal) ||
                !TryString(root, "contract", out string? contract) ||
                !string.Equals(contract, DevBridgeDiagnosticSchemas.LogsQuery, StringComparison.Ordinal) ||
                !TryBool(root, "success", out bool success) ||
                !TryNonNegativeInt(root, "generation", out int generation) ||
                !TryBool(root, "available", out bool available) ||
                !TryBool(root, "truncated", out bool truncated) ||
                !root.TryGetProperty("records", out JsonElement records) ||
                records.ValueKind != JsonValueKind.Array)
            {
                return Failure(
                    DevBridgeDiagnosticSourceOutcome.IncompatibleSchema,
                    "DEVBRIDGE_DIAGNOSTIC_SCHEMA_UNSUPPORTED",
                    "DevBridge returned an unsupported scoped diagnostic contract.",
                    process.ExitCode,
                    process.Stderr);
            }

            if (generation != requestedGeneration)
            {
                return Failure(
                    DevBridgeDiagnosticSourceOutcome.Unavailable,
                    "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH",
                    "DevBridge returned diagnostics for a different generation.",
                    process.ExitCode,
                    process.Stderr);
            }

            if (!success || !available)
            {
                return Failure(
                    DevBridgeDiagnosticSourceOutcome.Unavailable,
                    ReadOptionalString(root, "errorCode") ??
                        "DEVBRIDGE_DIAGNOSTIC_SOURCE_UNAVAILABLE",
                    ReadOptionalString(root, "error") ??
                        "DevBridge did not expose a bounded diagnostic source for this generation.",
                    process.ExitCode,
                    process.Stderr);
            }

            var rendered = new StringBuilder();
            var count = 0;
            foreach (JsonElement record in records.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object ||
                    !TryString(record, "severity", out string? severity) ||
                    !TryString(record, "component", out string? component) ||
                    !TryString(record, "message", out string? message) ||
                    !TryNonNegativeInt(record, "generation", out int recordGeneration) ||
                    recordGeneration != requestedGeneration)
                {
                    return Failure(
                        DevBridgeDiagnosticSourceOutcome.IncompatibleSchema,
                        "DEVBRIDGE_DIAGNOSTIC_RECORD_INVALID",
                        "DevBridge returned an invalid or cross-generation diagnostic record.",
                        process.ExitCode,
                        process.Stderr);
                }

                string line = $"[{Bound(component, 160)}] {Bound(message, 2048)}";
                if (!TryAppendBounded(rendered, line))
                {
                    truncated = true;
                    break;
                }

                count++;
                if (record.TryGetProperty("stackFrames", out JsonElement frames) &&
                    frames.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement frame in frames.EnumerateArray())
                    {
                        if (frame.ValueKind != JsonValueKind.String)
                        {
                            return Failure(
                                DevBridgeDiagnosticSourceOutcome.IncompatibleSchema,
                                "DEVBRIDGE_DIAGNOSTIC_RECORD_INVALID",
                                "DevBridge returned an invalid stack frame.",
                                process.ExitCode,
                                process.Stderr);
                        }

                        if (!TryAppendBounded(
                                rendered,
                                "  " + Bound(frame.GetString(), 1024)))
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
            }

            string content = rendered.ToString();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            return new DevBridgeDiagnosticSourceResult(
                new DevBridgeDiagnosticSourceStatus(
                    DevBridgeDiagnosticSourceOutcome.Available,
                    ProcessExitCode: process.ExitCode,
                    Stderr: Bound(process.Stderr, 4096)),
                new DevBridgeScopedDiagnosticSource(
                    DevBridgeDiagnosticSchemas.ScopedSource,
                    requestedGeneration,
                    content,
                    bytes.Length,
                    count,
                    truncated,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        catch (JsonException)
        {
            return Failure(
                DevBridgeDiagnosticSourceOutcome.MalformedResponse,
                "DEVBRIDGE_DIAGNOSTIC_MALFORMED_JSON",
                "DevBridge returned malformed scoped diagnostic JSON.",
                process.ExitCode,
                process.Stderr);
        }
    }

    private static bool TryAppendBounded(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        string candidate = value + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(builder.ToString()) +
            Encoding.UTF8.GetByteCount(candidate) > MaxSourceBytes)
        {
            return false;
        }

        builder.Append(candidate);
        return true;
    }

    private static DevBridgeDiagnosticSourceResult Failure(
        DevBridgeDiagnosticSourceOutcome outcome,
        string code,
        string error,
        int? exitCode = null,
        string? stderr = null) =>
        new(
            new DevBridgeDiagnosticSourceStatus(
                outcome,
                code,
                Bound(error, 512),
                exitCode,
                Bound(stderr, 4096)),
            null);

    private static bool TryString(JsonElement parent, string property, out string? value)
    {
        value = null;
        return parent.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
    }

    private static string? ReadOptionalString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String
            ? Bound(element.GetString(), 512)
            : null;

    private static bool TryBool(JsonElement parent, string property, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(property, out JsonElement element) ||
            (element.ValueKind != JsonValueKind.True &&
                element.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryNonNegativeInt(JsonElement parent, string property, out int value)
    {
        value = 0;
        return parent.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value) &&
            value >= 0;
    }

    private static string? Bound(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maximum ? value : value[..maximum];
    }
}
