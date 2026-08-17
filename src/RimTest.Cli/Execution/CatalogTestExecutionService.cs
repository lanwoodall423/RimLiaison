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

    public CatalogTestExecutionService(
        IDevBridgeRecipeAdapter recipeAdapter,
        Func<IRimErrorDiagnosisAdapter>? diagnosisFactory = null)
        : this(new CatalogTestRecipeRunner(recipeAdapter), diagnosisFactory)
    {
    }

    public CatalogTestExecutionService(
        ICatalogTestRecipeRunner recipeRunner,
        Func<IRimErrorDiagnosisAdapter>? diagnosisFactory = null)
    {
        this.recipeRunner = recipeRunner ?? throw new ArgumentNullException(nameof(recipeRunner));
        this.diagnosisFactory = diagnosisFactory;
    }

    public async Task<CatalogTestExecutionResult> RunAsync(
        CatalogDocument catalog,
        string testId,
        long started,
        CancellationToken cancellationToken = default)
    {
        CatalogTestRunResult run = await recipeRunner.RunAsync(
                catalog,
                testId,
                cancellationToken)
            .ConfigureAwait(false);
        RimTestResult normalized = RimTestResultFactory.FromRun(
            run.TestId,
            run.RecipeResult,
            ElapsedMilliseconds(started));

        if (run.RecipeResult.Status.Outcome == DevBridgeOutcomeKind.TestFailure)
        {
            RimErrorDiagnosisResult diagnosis;
            try
            {
                IRimErrorDiagnosisAdapter? adapter = diagnosisFactory?.Invoke();
                diagnosis = adapter is null
                    ? UnavailableDiagnosis("RIMERROR_NOT_CONFIGURED")
                    : await TryDiagnoseAsync(adapter, run, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception)
            {
                diagnosis = UnavailableDiagnosis("RIMERROR_ADAPTER_FAILED");
            }

            normalized = RimTestResultFactory.AttachDiagnosis(normalized, diagnosis);
        }

        return new CatalogTestExecutionResult(run, normalized);
    }

    private static async Task<RimErrorDiagnosisResult> TryDiagnoseAsync(
        IRimErrorDiagnosisAdapter adapter,
        CatalogTestRunResult result,
        CancellationToken cancellationToken)
    {
        DevBridgeRecipeRunResult run = result.RecipeResult;
        var request = new RimErrorDiagnosisRequest(
            result.TestId,
            run.RunId,
            run.Generation,
            run.EvidenceId ?? run.Evidence,
            run.FailureFingerprint,
            run.Status.ErrorCode);
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

    private static RimErrorDiagnosisResult UnavailableDiagnosis(string code) =>
        new(
            RimErrorDiagnosisOutcome.Unavailable,
            new RimErrorAdapterStatus(
                RimErrorDiagnosisOutcome.Unavailable,
                code,
                "The RimError request could not be completed."),
            null,
            null);

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
