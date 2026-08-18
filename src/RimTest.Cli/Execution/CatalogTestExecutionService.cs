using System.Diagnostics;
using RimTest.Catalog;
using RimTest.DevBridge;
using RimTest.RimError;
using RimTest.Results;

namespace RimTest.Execution;

public sealed record CatalogTestExecutionResult(
    CatalogTestRunResult Run,
    RimTestResult Result);

public sealed class CatalogTestExecutionService
{
    private readonly ICatalogTestRecipeRunner recipeRunner;
    private readonly Func<IRimErrorDiagnosisAdapter>? diagnosisFactory;
    private readonly Func<IDevBridgeDiagnosticSourceAdapter>? diagnosticSourceFactory;

    public CatalogTestExecutionService(
        IDevBridgeRecipeAdapter recipeAdapter,
        Func<IRimErrorDiagnosisAdapter>? diagnosisFactory = null,
        Func<IDevBridgeDiagnosticSourceAdapter>? diagnosticSourceFactory = null)
        : this(
            new CatalogTestRecipeRunner(recipeAdapter),
            diagnosisFactory,
            diagnosticSourceFactory)
    {
    }

    public CatalogTestExecutionService(
        ICatalogTestRecipeRunner recipeRunner,
        Func<IRimErrorDiagnosisAdapter>? diagnosisFactory = null,
        Func<IDevBridgeDiagnosticSourceAdapter>? diagnosticSourceFactory = null)
    {
        this.recipeRunner = recipeRunner ?? throw new ArgumentNullException(nameof(recipeRunner));
        this.diagnosisFactory = diagnosisFactory;
        this.diagnosticSourceFactory = diagnosticSourceFactory;
    }

    public async Task<CatalogTestExecutionResult> RunAsync(
        CatalogDocument catalog,
        string testId,
        long started,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        DevBridgeRecipeExecutionContext? executionContext = null)
    {
        CatalogTestRunResult run = await recipeRunner.RunAsync(
                catalog,
                testId,
                cancellationToken,
                workflowId,
                executionContext)
            .ConfigureAwait(false);
        RimTestResult normalized = RimTestResultFactory.FromRun(
            run.TestId,
            run.RecipeResult,
            ElapsedMilliseconds(started),
            workflowId);

        if (run.RecipeResult.Status.Outcome == DevBridgeOutcomeKind.TestFailure)
        {
            RimErrorDiagnosisResult diagnosis;
            try
            {
                IDevBridgeDiagnosticSourceAdapter? sourceAdapter =
                    diagnosticSourceFactory?.Invoke();
                if (sourceAdapter is null)
                {
                    diagnosis = await DiagnoseWithoutAutomaticSourceAsync(
                            run,
                            workflowId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    DevBridgeDiagnosticSourceResult source =
                        await sourceAdapter.AcquireAsync(
                                run.TestId,
                                run.RecipeResult,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!source.Status.IsAvailable || source.Source is null)
                    {
                        diagnosis = SourceFailureDiagnosis(source.Status);
                    }
                    else if (run.RecipeResult.Generation != source.Source.Generation)
                    {
                        diagnosis = UnavailableDiagnosis(
                            "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH");
                    }
                    else
                    {
                        IRimErrorDiagnosisAdapter? adapter = diagnosisFactory?.Invoke();
                        diagnosis = adapter is null
                            ? UnavailableDiagnosis("RIMERROR_NOT_CONFIGURED")
                            : await TryDiagnoseAsync(
                                    adapter,
                                    run,
                                    workflowId,
                                    ToRimErrorSource(source.Source),
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                diagnosis = UnavailableDiagnosis("DEVBRIDGE_DIAGNOSTIC_SOURCE_FAILED");
            }

            normalized = RimTestResultFactory.AttachDiagnosis(normalized, diagnosis);
        }

        return new CatalogTestExecutionResult(run, normalized);
    }

    private async Task<RimErrorDiagnosisResult> DiagnoseWithoutAutomaticSourceAsync(
        CatalogTestRunResult result,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        IRimErrorDiagnosisAdapter? adapter = diagnosisFactory?.Invoke();
        return adapter is null
            ? UnavailableDiagnosis("RIMERROR_NOT_CONFIGURED")
            : await TryDiagnoseAsync(
                    adapter,
                    result,
                    workflowId,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<RimErrorDiagnosisResult> TryDiagnoseAsync(
        IRimErrorDiagnosisAdapter adapter,
        CatalogTestRunResult result,
        string? workflowId,
        RimErrorScopedDiagnosticSource? scopedSource,
        CancellationToken cancellationToken)
    {
        DevBridgeRecipeRunResult run = result.RecipeResult;
        var request = new RimErrorDiagnosisRequest(
            result.TestId,
            run.RunId,
            run.Generation,
            run.EvidenceId ?? run.Evidence,
            run.FailureFingerprint,
            run.Status.ErrorCode,
            workflowId ?? run.WorkflowId,
            run.Operations
                .Select(static operation => new RimErrorOperationCorrelation(
                    operation.OperationId,
                    operation.Tool,
                    operation.Success,
                    operation.ErrorCode,
                    operation.WorkflowId,
                    operation.Generation,
                    operation.LaunchId))
                .ToArray(),
            scopedSource);
        try
        {
            RimErrorDiagnosisResult? diagnosis = await adapter.DiagnoseAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            return diagnosis ?? UnavailableDiagnosis("RIMERROR_EMPTY_RESPONSE");
        }
        catch (OperationCanceledException)
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
        catch (Exception)
        {
            return UnavailableDiagnosis("RIMERROR_ADAPTER_FAILED");
        }
    }

    private static RimErrorDiagnosisResult SourceFailureDiagnosis(
        DevBridgeDiagnosticSourceStatus status) =>
        status.Outcome switch
        {
            DevBridgeDiagnosticSourceOutcome.Cancelled => new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Cancelled,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Cancelled,
                    status.ErrorCode ?? "DEVBRIDGE_DIAGNOSTIC_CANCELLED",
                    status.Error),
                null,
                null),
            DevBridgeDiagnosticSourceOutcome.Timeout => new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Timeout,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Timeout,
                    status.ErrorCode ?? "DEVBRIDGE_DIAGNOSTIC_TIMEOUT",
                    status.Error),
                null,
                null),
            _ => UnavailableDiagnosis(
                status.ErrorCode ?? "DEVBRIDGE_DIAGNOSTIC_SOURCE_UNAVAILABLE")
        };

    private static RimErrorScopedDiagnosticSource ToRimErrorSource(
        DevBridgeScopedDiagnosticSource source) =>
        new(
            source.SchemaVersion,
            source.Generation,
            source.Content,
            source.SourceBytes,
            source.RecordCount,
            source.Truncated,
            source.Sha256,
            source.LaunchId);

    private static RimErrorDiagnosisResult UnavailableDiagnosis(
        string code,
        string? error = null) =>
        new(
            RimErrorDiagnosisOutcome.Unavailable,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.Unavailable,
                code,
                error ?? "The RimError request could not be completed."),
            null,
            null);

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
