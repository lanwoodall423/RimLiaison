using RimContext.Core;
using RimContext.Core.Contracts;
using RimContext.Core.Semantics;

namespace RimLiaison.RimContext;

/// <summary>
/// The in-process RimContext capability used by RimLiaison. The direct
/// rimctx CLI remains a separate, versioned contract for drill-down callers.
/// </summary>
public sealed class RimContextImpactAdapter : IRimContextImpactAdapter
{
    private readonly RimContextService service;
    private readonly RimContextAdapterOptions options;

    public RimContextImpactAdapter(
        RimContextAdapterOptions options,
        RimContextService? service = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.service = service ?? new RimContextService();
        options.Validate();
    }

    public Task<RimContextImpactResult> AffectedAsync(
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (changedPaths.Count == 0 || changedPaths.Any(string.IsNullOrWhiteSpace))
        {
            return Task.FromResult(Failure(
                RimContextImpactOutcome.InvalidInput,
                "RIMCONTEXT_CHANGED_PATHS_INVALID",
                "At least one non-empty changed path is required."));
        }

        try
        {
            var analysis = service.RefreshAndAffected(
                new RimContextAffectedRequest(
                    changedPaths,
                    options.RootPath,
                    options.StorePath,
                    Depth: options.Depth,
                    Limit: options.Limit),
                cancellationToken);

            if (analysis.Result is null)
            {
                return Task.FromResult(new RimContextImpactResult(
                    new RimContextAdapterStatus(
                        RimContextImpactOutcome.Unknown,
                        "RIMCONTEXT_INDEX_PARTIAL",
                        "RimContext indexing completed with diagnostics; affected selection is conservative.",
                        ResponseSchema: RimContextSchemas.Envelope),
                    [],
                    [],
                    true));
            }

            var result = analysis.Result;
            var impacts = result.Direct
                .Select(impact => ToImpact(impact, "direct"))
                .Concat(result.Dependent.Select(impact => ToImpact(impact, "dependent")))
                .Concat(result.RuntimeRisk.Select(impact => ToImpact(impact, "runtimeRisk")))
                .ToArray();
            if (result.Truncated)
            {
                return Task.FromResult(new RimContextImpactResult(
                    new RimContextAdapterStatus(
                        RimContextImpactOutcome.Unknown,
                        "RIMCONTEXT_RESULT_TRUNCATED",
                        "RimContext did not prove that the affected result was complete.",
                        ResponseSchema: RimContextSchemas.Envelope),
                    result.Changed,
                    impacts,
                    true));
            }

            return Task.FromResult(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Success,
                    ResponseSchema: RimContextSchemas.Envelope),
                result.Changed,
                impacts,
                false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure(
                RimContextImpactOutcome.Cancelled,
                "RIMTEST_CANCELLED",
                "The RimContext operation was cancelled."));
        }
        catch (RimContextException exception)
        {
            return Task.FromResult(Failure(
                MapOutcome(exception.Error.Code),
                exception.Error.Code,
                BoundError(exception.Error.Message),
                RimContextSchemas.Envelope));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Failure(
                RimContextImpactOutcome.InfrastructureFailure,
                "RIMCONTEXT_CORE_FAILED",
                BoundError(exception.Message),
                RimContextSchemas.Envelope));
        }
    }

    private static RimContextImpact ToImpact(AffectedMatch match, string tier) =>
        new(
            tier,
            match.Kind,
            match.Id,
            match.Name,
            match.File,
            match.Line,
            match.Reason,
            match.Confidence);

    private static RimContextImpactOutcome MapOutcome(string code) => code switch
    {
        "INVALID_ARGUMENT" or "LIMIT_EXCEEDED" => RimContextImpactOutcome.InvalidInput,
        "PATH_NOT_FOUND" or "INPUT_READ_FAILED" or "NOT_FOUND" => RimContextImpactOutcome.Unknown,
        "INDEX_NOT_FOUND" or "INDEX_INCOMPATIBLE" or "ROOT_MISMATCH" or
            "STORE_LOCKED" or "STORE_FAILED" => RimContextImpactOutcome.Unavailable,
        _ => RimContextImpactOutcome.InfrastructureFailure
    };

    private static RimContextImpactResult Failure(
        RimContextImpactOutcome outcome,
        string code,
        string error,
        string? responseSchema = null) =>
        new(
            new RimContextAdapterStatus(outcome, code, error, ResponseSchema: responseSchema),
            [],
            [],
            outcome is RimContextImpactOutcome.Unknown or RimContextImpactOutcome.Cancelled);

    private static string BoundError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "RimContext did not provide an error message.";
        }

        return value.Length <= 512 ? value : value[..512];
    }
}
