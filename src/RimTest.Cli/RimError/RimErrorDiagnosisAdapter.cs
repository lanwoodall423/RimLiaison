using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Security.Cryptography;
using RimTest.DevBridge;

namespace RimTest.RimError;

public interface IRimErrorDiagnosisAdapter
{
    Task<RimErrorDiagnosisResult> DiagnoseAsync(
        RimErrorDiagnosisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses RimError's existing ingest/latest CLI boundary. RimTest passes only
/// bounded integration metadata and projects the bounded latest report.
/// </summary>
public sealed class RimErrorDiagnosisAdapter : IRimErrorDiagnosisAdapter
{
    private const string DevBridgeRunSchema = "devbridge-test-recipe-run/v1";
    private const int MaxJsonDepth = 16;

    private static readonly JsonSerializerOptions IntegrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly IDevBridgeProcessTransport transport;
    private readonly RimErrorAdapterOptions options;

    public RimErrorDiagnosisAdapter(
        IDevBridgeProcessTransport transport,
        RimErrorAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public async Task<RimErrorDiagnosisResult> DiagnoseAsync(
        RimErrorDiagnosisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.IsConfigured && request.ScopedSource is null)
        {
            return Unavailable(
                "RIMERROR_NOT_CONFIGURED",
                "RimError diagnostic configuration was not supplied and no scoped DevBridge source was available.");
        }

        if (string.IsNullOrWhiteSpace(request.TestId))
        {
            return Unavailable(
                "RIMERROR_REQUEST_INVALID",
                "A test id is required for RimError correlation.");
        }

        string? integrationPath = null;
        string? scopedSourcePath = null;
        try
        {
            string? storePath = options.StorePath;
            if (request.ScopedSource is not null)
            {
                if (!IsValidScopedSource(request.ScopedSource))
                {
                    return Unavailable(
                        "RIMERROR_SCOPED_SOURCE_INVALID",
                        "The bounded DevBridge diagnostic source did not match its contract.");
                }

                storePath ??= DefaultAutomaticStorePath();
                scopedSourcePath = Path.Combine(
                    Path.GetTempPath(),
                    "rimtest-devbridge-diagnostic-" + Guid.NewGuid().ToString("N") + ".log");
                await File.WriteAllTextAsync(
                    scopedSourcePath,
                    request.ScopedSource.Content,
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
            }

            string? ingestPath = request.ScopedSource is not null
                ? scopedSourcePath
                : options.LogPath;
            if (ingestPath is not null)
            {
                if (storePath is null)
                {
                    return Unavailable(
                        "RIMERROR_STORE_NOT_CONFIGURED",
                        "A RimError store is required when ingesting a log.");
                }

                integrationPath = Path.Combine(
                    Path.GetTempPath(),
                    "rimtest-rimerror-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(
                    integrationPath,
                    BuildIntegrationJson(request),
                    cancellationToken).ConfigureAwait(false);

                DevBridgeProcessResult ingest = await ExecuteAsync(
                    [
                        "ingest",
                        ingestPath,
                        "--store",
                        storePath,
                        "--integration",
                        integrationPath,
                        "--test",
                        request.TestId,
                        .. RunOption(request.RunId)
                    ],
                    options.IngestTimeout,
                    cancellationToken).ConfigureAwait(false);
                RimErrorDiagnosisResult? ingestFailure = ProcessFailure(
                    ingest,
                    "ingest");
                if (ingestFailure is not null)
                {
                    return ingestFailure;
                }

                RimErrorDiagnosisResult ingestReport = ParseLatest(ingest);
                if (ingestReport.Outcome is
                    RimErrorDiagnosisOutcome.MalformedResponse or
                    RimErrorDiagnosisOutcome.IncompatibleSchema)
                {
                    return ingestReport;
                }
            }

            if (storePath is null)
            {
                return Unavailable(
                    "RIMERROR_STORE_NOT_CONFIGURED",
                    "A RimError store is required to request latest diagnostics.");
            }

            DevBridgeProcessResult latest = await ExecuteAsync(
                [
                    "latest",
                    "--json",
                    "--store",
                    storePath,
                    .. RunOption(request.RunId)
                ],
                options.LatestTimeout,
                cancellationToken).ConfigureAwait(false);
            return ParseLatest(latest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            return Unavailable(
                "RIMERROR_ADAPTER_FAILED",
                "The RimError request could not be completed.");
        }
        finally
        {
            if (integrationPath is not null)
            {
                TryDelete(integrationPath);
            }

            if (scopedSourcePath is not null)
            {
                TryDelete(scopedSourcePath);
            }
        }
    }

    private async Task<DevBridgeProcessResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.WorkingDirectory,
            arguments,
            timeout,
            options.MaxStdoutBytes,
            options.MaxStderrBytes);
        return await transport.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private static RimErrorDiagnosisResult? ProcessFailure(
        DevBridgeProcessResult process,
        string operation)
    {
        if (process.Cancelled)
        {
            return Cancelled();
        }

        if (process.TimedOut)
        {
            return new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Timeout,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Timeout,
                    "RIMERROR_TIMEOUT",
                    "The bounded RimError request timed out.",
                    process.ExitCode),
                null,
                null);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return Unavailable(
                "RIMERROR_START_FAILED",
                "RimError did not start.",
                process.ExitCode);
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            return Unavailable(
                "RIMERROR_OUTPUT_LIMIT_EXCEEDED",
                "RimError exceeded the adapter output bound.",
                process.ExitCode);
        }

        if (process.ExitCode is null or > 1)
        {
            return Unavailable(
                "RIMERROR_" + operation.ToUpperInvariant() + "_FAILED",
                "RimError returned an operational failure.",
                process.ExitCode);
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Unavailable(
                "RIMERROR_NO_STRUCTURED_RESPONSE",
                "RimError produced no structured response.",
                process.ExitCode);
        }

        return null;
    }

    private static RimErrorDiagnosisResult ParseLatest(
        DevBridgeProcessResult process)
    {
        RimErrorDiagnosisResult? processFailure = ProcessFailure(process, "latest");
        if (processFailure is not null)
        {
            return processFailure;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                process.Stdout!,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxJsonDepth
                });
        }
        catch (JsonException)
        {
            return new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.MalformedResponse,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.MalformedResponse,
                    "RIMERROR_MALFORMED_JSON",
                    "RimError returned malformed JSON.",
                    process.ExitCode),
                null,
                null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("schemaVersion", out _) ||
                root.TryGetProperty("v", out _) ||
                !TryGetString(root, "status", out string? reportStatus) ||
                !TryGetNonNegativeInt(root, "errors", out _) ||
                !TryGetNonNegativeInt(root, "warnings", out _) ||
                reportStatus is not ("clean" or "warn" or "fail"))
            {
                return Incompatible(process.ExitCode);
            }

            if (!TryGetDiagnostic(
                    root,
                    "rootCauses",
                    out RimErrorDiagnosticSummary? diagnosis))
            {
                return Incompatible(process.ExitCode);
            }

            if (diagnosis is null &&
                !TryGetDiagnostic(root, "diagnostics", out diagnosis))
            {
                return Incompatible(process.ExitCode);
            }

            if (diagnosis is null)
            {
                return new RimErrorDiagnosisResult(
                    RimErrorDiagnosisOutcome.Empty,
                    new RimErrorAdapterStatus(
                        RimErrorDiagnosisOutcome.Empty,
                        null,
                        null,
                        process.ExitCode),
                    null,
                    reportStatus);
            }

            return new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Available,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Available,
                    null,
                    null,
                    process.ExitCode),
                diagnosis,
                reportStatus);
        }
    }

    private static bool TryGetDiagnostic(
        JsonElement root,
        string propertyName,
        out RimErrorDiagnosticSummary? diagnosis)
    {
        diagnosis = null;
        if (!root.TryGetProperty(propertyName, out JsonElement collection))
        {
            return true;
        }

        if (collection.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (collection.GetArrayLength() == 0)
        {
            return true;
        }

        JsonElement first = collection[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !TryGetString(first, "id", out string? id))
        {
            return false;
        }

        if (!TryGetOptionalString(first, "category", out string? category) ||
            !TryGetOptionalString(first, "type", out string? type) ||
            !TryGetOptionalString(first, "method", out string? method) ||
            !TryGetOptionalString(first, "symbol", out string? symbol) ||
            !TryGetOptionalString(first, "def", out string? def) ||
            !TryGetOptionalString(first, "code", out string? code) ||
            !TryGetOptionalString(first, "source", out string? source) ||
            !TryGetOptionalInt(first, "line", out int? line) ||
            !TryGetOptionalString(first, "confidence", out string? confidence))
        {
            return false;
        }

        diagnosis = new RimErrorDiagnosticSummary
        {
            Id = Bound(id, 128)!,
            Category = Bound(category, 96),
            Type = Bound(type, 160),
            Method = Bound(method, 240),
            Symbol = Bound(symbol, 240),
            Def = Bound(def, 240),
            Code = Bound(code, 128),
            Source = Bound(source, 512),
            Line = line,
            Confidence = Bound(confidence, 32)
        };
        return true;
    }

    private static string BuildIntegrationJson(RimErrorDiagnosisRequest request)
    {
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = RimErrorSchemas.Integration,
                devBridge = new
                {
                    schemaVersion = DevBridgeRunSchema,
                    workflowId = request.WorkflowId,
                    runId = request.RunId,
                    testId = request.TestId,
                    generation = request.Generation,
                    failureCode = request.FailureCode,
                    evidence = request.EvidenceId
                },
                rimBridge = request.Operations is { Count: > 0 }
                    ? new
                    {
                        workflowId = request.WorkflowId,
                        operations = request.Operations.Select(static operation => new
                        {
                            operationId = operation.OperationId,
                            operationName = operation.OperationName,
                            success = operation.Success,
                            errorCode = operation.ErrorCode,
                            workflowId = operation.WorkflowId,
                            generation = operation.Generation,
                            launchId = operation.LaunchId
                        })
                    }
                    : null
            },
            IntegrationJsonOptions);
    }

    private static bool IsValidScopedSource(RimErrorScopedDiagnosticSource source)
    {
        if (!string.Equals(
                source.SchemaVersion,
                RimErrorSchemas.ScopedDiagnosticSource,
                StringComparison.Ordinal) ||
            source.Generation <= 0 ||
            source.SourceBytes < 0 ||
            source.SourceBytes > 64 * 1024 ||
            source.RecordCount < 0 ||
            source.Content is null ||
            Encoding.UTF8.GetByteCount(source.Content) != source.SourceBytes ||
            string.IsNullOrWhiteSpace(source.Sha256) ||
            source.Sha256.Length != 64 ||
            !source.Sha256.All(static value =>
                value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            return false;
        }

        byte[] expected = Convert.FromHexString(source.Sha256);
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(source.Content));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string DefaultAutomaticStorePath()
    {
        string? configured = Environment.GetEnvironmentVariable("RIMERROR_STATE_PATH");
        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.CurrentDirectory, ".rimerror", "latest.json")
                : configured);
    }

    private static IEnumerable<string> RunOption(string? runId)
    {
        return string.IsNullOrWhiteSpace(runId)
            ? []
            : ["--run", runId];
    }

    private static bool TryGetString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetOptionalString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
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

    private static bool TryGetNonNegativeInt(
        JsonElement parent,
        string name,
        out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value) &&
            value >= 0;
    }

    private static bool TryGetOptionalInt(
        JsonElement parent,
        string name,
        out int? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
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

    private static RimErrorDiagnosisResult Incompatible(int? processExitCode)
    {
        return new RimErrorDiagnosisResult(
            RimErrorDiagnosisOutcome.IncompatibleSchema,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.IncompatibleSchema,
                "RIMERROR_SCHEMA_UNSUPPORTED",
                "RimError latest response did not match its current report contract.",
                processExitCode),
            null,
            null);
    }

    private static RimErrorDiagnosisResult Unavailable(
        string code,
        string error,
        int? processExitCode = null)
    {
        return new RimErrorDiagnosisResult(
            RimErrorDiagnosisOutcome.Unavailable,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.Unavailable,
                code,
                error,
                processExitCode),
            null,
            null);
    }

    private static RimErrorDiagnosisResult Cancelled()
    {
        return new RimErrorDiagnosisResult(
            RimErrorDiagnosisOutcome.Cancelled,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.Cancelled,
                "RIMERROR_CANCELLED",
                "The RimError request was cancelled."),
            null,
            null);
    }

    private static string? Bound(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maximum ? value : value[..maximum];
    }

    private static void ValidateOptions(RimErrorAdapterOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.CommandPath) ||
            string.IsNullOrWhiteSpace(value.WorkingDirectory))
        {
            throw new ArgumentException(
                "RimError command path and working directory are required.");
        }

        if (value.IngestTimeout <= TimeSpan.Zero ||
            value.LatestTimeout <= TimeSpan.Zero ||
            value.MaxStdoutBytes <= 0 ||
            value.MaxStderrBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
