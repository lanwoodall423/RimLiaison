namespace RimError.Core;

/// <summary>
/// Typed RimError orchestration shared by RimError.Cli and in-process hosts.
/// The CLI is responsible for argument parsing and JSON presentation; this
/// service owns bounded ingestion, correlation, root-cause analysis, and
/// optional persistence.
/// </summary>
public sealed class RimErrorService
{
    public async ValueTask<RimErrorIngestResult> IngestAsync(
        RimErrorIngestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sources);
        cancellationToken.ThrowIfCancellationRequested();

        var ingestion = await new DiagnosticIngestor()
            .IngestAsync(request.Sources, request.Options, cancellationToken)
            .ConfigureAwait(false);
        return await CompleteAsync(request, ingestion, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RimErrorIngestResult> IngestFilesAsync(
        IEnumerable<string> paths,
        DiagnosticIngestionMetadata? metadata = null,
        DiagnosticIngestionOptions? options = null,
        string? projectPath = null,
        string? indexCachePath = null,
        IDiagnosticStore? store = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var ingestion = await new DiagnosticIngestor()
            .IngestFilesAsync(paths, metadata, options, cancellationToken)
            .ConfigureAwait(false);
        return await CompleteAsync(
                new RimErrorIngestRequest(
                    [],
                    options,
                    projectPath,
                    indexCachePath,
                    store),
                ingestion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<DiagnosticLatestReport> LatestAsync(
        IDiagnosticStore store,
        string? runId = null,
        bool includeAll = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            snapshot = DiagnosticLatestReportBuilder.FilterByRun(snapshot, runId);
        }

        return DiagnosticLatestReportBuilder.Build(snapshot, includeAll);
    }

    public async ValueTask<DiagnosticStoreSnapshot?> ReadAsync(
        IDiagnosticStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return await store.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RimErrorIngestResult> CompleteAsync(
        RimErrorIngestRequest request,
        DiagnosticIngestionResult ingestion,
        CancellationToken cancellationToken)
    {
        var snapshot = ingestion.ToSnapshot();
        if (request.ProjectPath is not null)
        {
            if (request.IndexCachePath is not null)
            {
                var index = await new ProjectIndexer()
                    .BuildOrLoadAsync(request.ProjectPath, request.IndexCachePath)
                    .ConfigureAwait(false);
                snapshot = DiagnosticSourceAttributor.Enrich(snapshot, index);
            }
            else
            {
                var index = await new ProjectIndexer()
                    .BuildOrLoadAsync(request.ProjectPath)
                    .ConfigureAwait(false);
                snapshot = DiagnosticSourceAttributor.Enrich(snapshot, index);
            }
        }
        else if (request.IndexCachePath is not null)
        {
            throw new ArgumentException("An index cache requires a project path.");
        }

        snapshot = DiagnosticIntegrationCorrelator.Apply(snapshot);
        snapshot = DiagnosticRootCauseEngine.Apply(snapshot);
        if (request.Store is not null)
        {
            await request.Store.WriteAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        return new RimErrorIngestResult(
            ingestion,
            snapshot,
            DiagnosticLatestReportBuilder.Build(
                snapshot,
                includeDiagnostics: false));
    }
}

public sealed record RimErrorIngestRequest(
    IReadOnlyList<DiagnosticSourceInput> Sources,
    DiagnosticIngestionOptions? Options = null,
    string? ProjectPath = null,
    string? IndexCachePath = null,
    IDiagnosticStore? Store = null);

public sealed record RimErrorIngestResult(
    DiagnosticIngestionResult Ingestion,
    DiagnosticStoreSnapshot Snapshot,
    DiagnosticLatestReport Report);
