using RimContext.Core.Content;

using RimContext.Core.Configuration;
using RimContext.Core.Context;
using RimContext.Core.Contracts;
using RimContext.Core.Logging;
using RimContext.Core.Model;
using RimContext.Core.Semantics;
using RimContext.Core.Storage;

using RimContext.Core.Impact;

namespace RimContext.Core;

/// <summary>
/// The typed RimContext entrypoint used by hosts in the same repository.
/// Versioned JSON remains owned by RimContext.Cli; callers in-process receive
/// the same bounded domain results without a serialization round trip.
/// </summary>
public sealed class RimContextService
{
    private readonly ILogger logger;

    public RimContextService(ILogger? logger = null)
    {
        this.logger = logger ?? new NullLogger();
    }

    public IndexBuildResult Index(
        RimContextIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = WorkspaceConfiguration.Resolve(
            request.RootPath,
            request.StorePath,
            request.AssemblyRoots);
        var result = new WorkspaceIndexer().Build(
            configuration,
            logger,
            force: request.Force);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public AffectedResult Affected(
        RimContextAffectedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = WorkspaceConfiguration.Resolve(
            request.RootPath,
            request.StorePath,
            request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var result = new SemanticQueryEngine(store).FindAffected(
            request.ChangedPaths,
            configuration.RootPath,
            request.Depth,
            request.Limit);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public RimContextAffectedAnalysis RefreshAndAffected(
        RimContextAffectedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var index = Index(
            new RimContextIndexRequest(
                request.RootPath,
                request.StorePath,
                request.AssemblyRoots,
                request.Force),
            cancellationToken);
        if (index.Diagnostics is { Count: > 0 })
        {
            return new RimContextAffectedAnalysis(index, null);
        }

        return new RimContextAffectedAnalysis(index, Affected(request, cancellationToken));
    }

    public RimContextSummary Summary(
        RimContextSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = WorkspaceConfiguration.Resolve(
            request.RootPath,
            request.StorePath,
            request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var counts = store.GetCounts();
        var files = store.GetFiles();
        var entities = store.GetEntities();
        var diagnosticCounts = CountDiagnostics(files, entities);
        cancellationToken.ThrowIfCancellationRequested();
        return new RimContextSummary(
            store.Metadata.SchemaVersion,
            store.Metadata.ToolVersion,
            store.Metadata.WorkspaceIdentity,
            store.Metadata.IndexedAtUtc,
            configuration.StoreDisplayPath(),
            counts.FileCount,
            counts.EntityCount,
            counts.RelationCount,
            entities.LongCount(item => item.Kind == "mod"),
            entities.LongCount(item => item.Kind == "project"),
            files.LongCount(item => item.Kind == "source_file"),
            files.LongCount(item => item.Kind == "xml_file"),
            entities.LongCount(item => item.Kind == "def"),
            entities.LongCount(item => item.Kind == "harmony_patch"),
            diagnosticCounts.Error,
            diagnosticCounts.Warning);
    }

    public Task<RimContextBundle> ContextBundleAsync(
        RimContextBundleRequest? request = null,
        IEnumerable<IRimContextBundleProvider>? providers = null,
        CancellationToken cancellationToken = default)
    {
        RimContextBundleRequest selected = request ?? new RimContextBundleRequest();
        IEnumerable<IRimContextBundleProvider> selectedProviders = providers ??
            [new RimContextWorkspaceProvider()];
        return RimContextBundleBuilder.BuildAsync(
            selected,
            selectedProviders,
            cancellationToken);
    }
    public ImpactGraph BuildImpactGraph(
        ImpactGraphBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ImpactGraph result = new ImpactGraphService().Build(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ExecutionPacket CreateExecutionPacket(
        ImpactGraph graph,
        ExecutionPacketRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionPacket result = new ExecutionPacketBuilder().Build(graph, request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ActualImpact AnalyzeActualImpact(
        ImpactGraph graph,
        IReadOnlyList<string> changedPaths,
        string? rootPath = null,
        PredictedImpact? prediction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(changedPaths);
        cancellationToken.ThrowIfCancellationRequested();
        ActualImpact result = new ImpactGraphService().AnalyzeDiff(
            graph,
            changedPaths,
            rootPath,
            prediction);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ImpactStatusResult EvaluateExecutionPacket(
        ExecutionPacket packet,
        ImpactGraph currentGraph,
        IReadOnlyList<string>? changedPaths = null,
        string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(currentGraph);
        cancellationToken.ThrowIfCancellationRequested();
        ImpactStatusResult result = new ImpactGraphService().EvaluatePacket(
            packet,
            currentGraph,
            changedPaths,
            rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }


    public ContentBlueprint CaptureContentBlueprint(
        ContentBlueprintCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ContentBlueprint result = (request.StorePath is null
                ? new ContentIntelligenceService()
                : new ContentIntelligenceService(new ContentIntelligenceStore(request.StorePath)))
            .CaptureBlueprint(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ContentEvidence CaptureContentEvidence(
        ContentEvidenceCaptureRequest request,
        string? storePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ContentEvidence result = (storePath is null
                ? new ContentIntelligenceService()
                : new ContentIntelligenceService(new ContentIntelligenceStore(storePath)))
            .CaptureEvidence(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ContentQueryResult QueryContent(
        ContentQueryRequest request,
        string? storePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ContentQueryResult result = (storePath is null
                ? new ContentIntelligenceService()
                : new ContentIntelligenceService(new ContentIntelligenceStore(storePath)))
            .Query(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ContentAnalysisResult AnalyzeContent(
        ContentAnalysisRequest request,
        string? storePath = null,

        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ContentAnalysisResult result = (storePath is null
                ? new ContentIntelligenceService()
                : new ContentIntelligenceService(new ContentIntelligenceStore(storePath)))
            .Analyze(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public ValidationPlan BuildValidationPlan(
        ValidationPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidationPlan result = new MinimumSafeValidationPlanner().Build(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ImpactLearningResult LearnImpact(
        ImpactLearningObservation observation,
        string? learningStorePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        ImpactLearningResult result = new ImpactLearningService(
            new ImpactLearningStore(learningStorePath)).Observe(observation);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent> ReadRemediationPrecedents(
        string failureFamily,
        global::RimDev.Contracts.EntityReference? subject = null,
        string? learningStorePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent> result =
            new ImpactLearningStore(learningStorePath)
                .ReadRemediationPrecedents(failureFamily, subject);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public bool RecordValidatedRemediation(
        global::RimDev.Contracts.RemediationPrecedent precedent,
        string? learningStorePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(precedent);
        cancellationToken.ThrowIfCancellationRequested();
        bool recorded = new ImpactLearningStore(learningStorePath)
            .RecordValidatedRemediation(precedent);
        cancellationToken.ThrowIfCancellationRequested();
        return recorded;
    }

    public ContentReuseDecision SelectContentReuse(
        ContentReuseRequest request,
        string? storePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ContentReuseDecision result = (storePath is null
                ? new ContentIntelligenceService()
                : new ContentIntelligenceService(new ContentIntelligenceStore(storePath)))
            .SelectReuse(request);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static (long Error, long Warning) CountDiagnostics(
        IReadOnlyList<IndexedFileRecord> files,
        IReadOnlyList<EntityRecord> entities)
    {
        var diagnostics = entities
            .Where(item => item.Kind == "diagnostic")
            .Select(item => ParseDiagnostic(item.PayloadJson))
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
        if (diagnostics.Length == 0)
        {
            return (files.LongCount(item => item.ParseStatus == "error"), 0);
        }

        return (
            diagnostics.LongCount(item => item == "error"),
            diagnostics.LongCount(item => item == "warning"));
    }

    private static string? ParseDiagnostic(string payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("severity", out var value)
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public sealed record RimContextIndexRequest(
    string? RootPath = null,
    string? StorePath = null,
    IReadOnlyList<string>? AssemblyRoots = null,
    bool Force = false);

public sealed record RimContextAffectedRequest(
    IReadOnlyList<string> ChangedPaths,
    string? RootPath = null,
    string? StorePath = null,
    IReadOnlyList<string>? AssemblyRoots = null,
    int Depth = IndexConstants.DefaultAffectedDepth,
    int Limit = IndexConstants.DefaultLimit,
    bool Force = false);

public sealed record RimContextAffectedAnalysis(
    IndexBuildResult Index,
    AffectedResult? Result);

public sealed record RimContextSummaryRequest(
    string? RootPath = null,
    string? StorePath = null,
    IReadOnlyList<string>? AssemblyRoots = null);

public sealed record RimContextSummary(
    int SchemaVersion,
    string ToolVersion,
    string WorkspaceId,
    string IndexedAtUtc,
    string Store,
    long FileCount,
    long EntityCount,
    long RelationCount,
    long Mods,
    long Projects,
    long SourceFiles,
    long XmlFiles,
    long Defs,
    long HarmonyPatches,
    long DiagnosticErrors,
    long DiagnosticWarnings);
