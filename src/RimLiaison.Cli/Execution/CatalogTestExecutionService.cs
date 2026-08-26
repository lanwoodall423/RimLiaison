using System.Diagnostics;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Profiling;
using RimLiaison.RimError;
using RimLiaison.Results;
using RimLiaison.Validation;

namespace RimLiaison.Execution;

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
        Func<IDevBridgeDiagnosticSourceAdapter>? diagnosticSourceFactory = null,
        IDevBridgeCapabilityAdapter? capabilityAdapter = null)
        : this(
            new CatalogTestRecipeRunner(recipeAdapter, capabilityAdapter),
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
        CatalogTestRunResult run = await ProfilerActivity.ObserveAsync(
                "test-execution",
                "testing",
                () => recipeRunner.RunAsync(
                    catalog,
                    testId,
                    cancellationToken,
                    workflowId,
                    executionContext),
                (activity, value) =>
                {
                    DevBridgeRecipeRunResult result = value.RecipeResult;
                    ProfilerActivity.SetOutcome(
                        activity,
                        result.Status.Outcome switch
                        {
                            DevBridgeOutcomeKind.Success => "success",
                            DevBridgeOutcomeKind.Cancelled => "cancelled",
                            DevBridgeOutcomeKind.TestFailure => "test-failure",
                            _ => "failure"
                        },
                        result.Status.ErrorCode);
                    ProfilerActivity.SetLogicalTarget(activity, value.TestId);
                    ProfilerActivity.SetGeneration(activity, result.Generation);
                    ProfilerActivity.SetCounts(activity, items: 1);
                },
                phase: "test",
                target: testId,
                scope: "test")
            .ConfigureAwait(false);
        RimTestResult normalized = run.CapabilityPreflight switch
        {
            { IsBlocked: true } preflight => RimTestResultFactory.CapabilityBlocked(
                run.TestId,
                preflight.Evidence,
                ElapsedMilliseconds(started),
                workflowId),
            { IsUnavailableOptional: true } preflight => RimTestResultFactory.CapabilityUnavailable(
                run.TestId,
                preflight.Evidence,
                ElapsedMilliseconds(started),
                workflowId),
            _ => RimTestResultFactory.FromRun(
                run.TestId,
                run.RecipeResult,
                ElapsedMilliseconds(started),
                workflowId,
                run.CapabilityPreflight is null
                    ? null
                    : CatalogNavigator.FindTest(catalog, run.TestId)?.ValidationClassification
                        is ValidationClassification.REQUIRED
                        ? null
                        : CatalogNavigator.FindTest(catalog, run.TestId)?.ValidationClassification)
        };
        CatalogTest? definition = CatalogNavigator.FindTest(catalog, run.TestId);
        if (definition is not null &&
            definition.ValidationClassification != ValidationClassification.REQUIRED &&
            normalized.Status == "infrastructure")
        {
            normalized = RimTestResultFactory.OptionalUnavailable(
                normalized,
                definition.ValidationClassification);
        }

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
                        await ProfilerActivity.ObserveAsync(
                                "diagnostic-source",
                                "diagnosis",
                                () => sourceAdapter.AcquireAsync(
                                    run.TestId,
                                    run.RecipeResult,
                                    cancellationToken),
                                (activity, value) =>
                                {
                                    ProfilerActivity.SetOutcome(
                                        activity,
                                        value.Status.Outcome switch
                                        {
                                            DevBridgeDiagnosticSourceOutcome.Available => "success",
                                            DevBridgeDiagnosticSourceOutcome.Cancelled => "cancelled",
                                            _ => "failure"
                                        },
                                        value.Status.ErrorCode);
                                    ProfilerActivity.SetGeneration(
                                        activity,
                                        value.Source?.Generation ?? run.RecipeResult.Generation);
                                    ProfilerActivity.SetCounts(
                                        activity,
                                        items: value.Source?.RecordCount);
                                },
                                phase: "diagnostic-source",
                                target: run.TestId,
                                scope: "devbridge")
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
            RimErrorDiagnosisResult? diagnosis = await ProfilerActivity.ObserveAsync(
                    "rimerror.diagnosis",
                    "diagnosis",
                    () => adapter.DiagnoseAsync(
                        request,
                        cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Outcome switch
                            {
                                RimErrorDiagnosisOutcome.Available or
                                RimErrorDiagnosisOutcome.Empty => "success",
                                RimErrorDiagnosisOutcome.Cancelled => "cancelled",
                                _ => "failure"
                            },
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, run.Generation);
                        ProfilerActivity.SetLogicalTarget(activity, result.TestId);
                        ProfilerActivity.SetCounts(activity, items: run.Operations.Count);
                    },
                    phase: "diagnosis",
                    target: result.TestId,
                    scope: "rimerror")
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
