using System.Security.Cryptography;
using System.Text;
using RimError.Core;

namespace RimLiaison.RimError;

public interface IRimErrorDiagnosisAdapter
{
    Task<RimErrorDiagnosisResult> DiagnoseAsync(
        RimErrorDiagnosisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses RimError.Core directly for the normal RimLiaison diagnostic path.
/// Log/store paths remain supported for compatibility, while generation-scoped
/// DevBridge evidence is ingested from memory without temporary handoff files.
/// </summary>
public sealed class RimErrorDiagnosisAdapter : IRimErrorDiagnosisAdapter
{
    private const string DevBridgeRunSchema = "devbridge-test-recipe-run/v1";

    private readonly RimErrorService service;
    private readonly RimErrorAdapterOptions options;

    public RimErrorDiagnosisAdapter(
        RimErrorAdapterOptions options,
        RimErrorService? service = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.service = service ?? new RimErrorService();
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

        try
        {
            if (request.ScopedSource is not null &&
                !IsValidScopedSource(request.ScopedSource))
            {
                return Unavailable(
                    "RIMERROR_SCOPED_SOURCE_INVALID",
                    "The bounded DevBridge diagnostic source did not match its contract.");
            }

            string? storePath = options.StorePath;
            if (request.ScopedSource is not null)
            {
                storePath ??= DefaultAutomaticStorePath();
            }

            if (storePath is null)
            {
                return Unavailable(
                    options.LogPath is null
                        ? "RIMERROR_STORE_NOT_CONFIGURED"
                        : "RIMERROR_STORE_NOT_CONFIGURED",
                    "A RimError store is required when ingesting a log.");
            }

            var store = new JsonFileDiagnosticStore(storePath);
            RimErrorIngestResult? ingestion = null;
            if (request.ScopedSource is not null)
            {
                var metadata = CreateMetadata(request);
                ingestion = await service.IngestAsync(
                        new RimErrorIngestRequest(
                            [new DiagnosticSourceInput
                            {
                                Source = "devbridge-generation",
                                Reader = new StringReader(request.ScopedSource.Content),
                                InputBytes = request.ScopedSource.SourceBytes,
                                Metadata = metadata
                            }],
                            Store: store),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (options.LogPath is not null)
            {
                ingestion = await service.IngestFilesAsync(
                        [options.LogPath],
                        CreateMetadata(request),
                        store: store,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            DiagnosticStoreSnapshot? snapshot = ingestion?.Snapshot ??
                await service.ReadAsync(store, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.RunId))
            {
                snapshot = DiagnosticLatestReportBuilder.FilterByRun(snapshot, request.RunId);
            }

            var report = DiagnosticLatestReportBuilder.Build(snapshot);
            return ToDiagnosisResult(report);
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
    }

    private static DiagnosticIngestionMetadata CreateMetadata(
        RimErrorDiagnosisRequest request) =>
        new()
        {
            WorkflowId = request.WorkflowId,
            RunId = request.RunId,
            TestId = request.TestId,
            OperationId = request.Operations?.FirstOrDefault()?.OperationId,
            OperationName = request.Operations?.FirstOrDefault()?.OperationName,
            Integration = CreateIntegration(request)
        };

    private static DiagnosticIntegrationState CreateIntegration(
        RimErrorDiagnosisRequest request)
    {
        var operations = request.Operations?
            .Select(operation => new DiagnosticBridgeOperation
            {
                WorkflowId = operation.WorkflowId ?? request.WorkflowId,
                OperationId = operation.OperationId,
                OperationName = operation.OperationName,
                Success = operation.Success,
                ErrorCode = operation.ErrorCode,
                RunId = request.RunId,
                Generation = operation.Generation ?? request.Generation,
                LaunchId = operation.LaunchId
            })
            .ToArray();

        return new DiagnosticIntegrationState
        {
            SourceSchemas = [DevBridgeRunSchema],
            DevBridge = new DiagnosticDevBridgeContext
            {
                WorkflowId = request.WorkflowId,
                SourceSchema = DevBridgeRunSchema,
                RunId = request.RunId,
                TestId = request.TestId,
                Generation = request.Generation,
                LaunchId = request.ScopedSource?.LaunchId,
                FailureCode = request.FailureCode,
                Evidence = request.EvidenceId
            },
            RimBridge = request.Operations is { Count: > 0 }
                ? new DiagnosticRimBridgeContext
                {
                    WorkflowId = request.WorkflowId,
                    RunId = request.RunId,
                    Generation = request.Generation,
                    LaunchId = request.ScopedSource?.LaunchId
                }
                : null,
            Operations = operations is { Length: > 0 } ? operations : null
        };
    }

    private static RimErrorDiagnosisResult ToDiagnosisResult(
        DiagnosticLatestReport report)
    {
        var diagnosis = report.RootCauses?.FirstOrDefault() is { } rootCause
            ? new RimErrorDiagnosticSummary
            {
                Id = rootCause.Id,
                Category = rootCause.Category,
                Type = rootCause.Type,
                Method = rootCause.Method,
                Symbol = rootCause.Symbol,
                Def = rootCause.Def,
                Code = rootCause.Code,
                Source = rootCause.Source,
                Line = rootCause.Line,
                Confidence = rootCause.Confidence
            }
            : report.Diagnostics?.FirstOrDefault() is { } diagnostic
                ? new RimErrorDiagnosticSummary
                {
                    Id = diagnostic.Id,
                    Category = diagnostic.Category,
                    Type = diagnostic.Type,
                    Method = diagnostic.Method,
                    Symbol = diagnostic.Symbol,
                    Def = diagnostic.Def,
                    Code = diagnostic.Code,
                    Source = diagnostic.Source,
                    Line = diagnostic.Line,
                    Confidence = diagnostic.Confidence
                }
                : null;

        if (diagnosis is null)
        {
            return new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Empty,
                new RimErrorAdapterStatus(RimErrorDiagnosisOutcome.Empty),
                null,
                report.Status);
        }

        return new RimErrorDiagnosisResult(
            RimErrorDiagnosisOutcome.Available,
            new RimErrorAdapterStatus(RimErrorDiagnosisOutcome.Available),
            diagnosis,
            report.Status);
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

    private static RimErrorDiagnosisResult Unavailable(string code, string error) =>
        new(
            RimErrorDiagnosisOutcome.Unavailable,
            new RimErrorAdapterStatus(RimErrorDiagnosisOutcome.Unavailable, code, error),
            null,
            null);

    private static RimErrorDiagnosisResult Cancelled() =>
        new(
            RimErrorDiagnosisOutcome.Cancelled,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.Cancelled,
                "RIMERROR_CANCELLED",
                "The RimError request was cancelled."),
            null,
            null);

    private static void ValidateOptions(RimErrorAdapterOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.WorkingDirectory))
        {
            throw new ArgumentException(
                "RimError working directory is required.");
        }

        if (value.IngestTimeout <= TimeSpan.Zero ||
            value.LatestTimeout <= TimeSpan.Zero ||
            value.MaxStdoutBytes <= 0 ||
            value.MaxStderrBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
