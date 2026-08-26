using RimContext.Core;
using RimContext.Core.Impact;

using RimContext.Core.Content;

using RimLiaison.Observability;
namespace RimLiaison.RimContext;

public sealed record ExecutionPacketGenerationResult(
    ExecutionPacket? Packet,
    PredictedImpact? Prediction,
    ActualImpact? Actual,
    string? ErrorCode = null,
    string? Error = null,
    ImpactGraph? Graph = null)
{
    public bool Succeeded => Packet is not null && ErrorCode is null;
}

/// <summary>
/// Coordinates the shared RimContext graph, pre-work packet, and post-change analysis.
/// It is deliberately static-index-only: no runtime, build, or fresh repository research occurs here.
/// </summary>
public static class ExecutionPacketCoordinator
{
    public static ExecutionPacketGenerationResult TryGenerate(
        string rootPath,
        string? storePath,
        string task,
        IReadOnlyList<string>? changedPaths = null,
        string? repository = null,
        string? project = null,
        string? sourceRevision = null,
        int maxBytes = 16 * 1024,
        int maxEntries = 16,
        IReadOnlyList<ImpactGraphEvidence>? additionalEvidence = null)
    {
        try
        {
            var service = new RimContextService();
            ImpactGraph graph = service.BuildImpactGraph(
                new ImpactGraphBuildRequest(
                    RootPath: rootPath,
                    StorePath: storePath,
                    Repository: repository,
                    Project: project,
                    SourceRevision: sourceRevision,
                    AdditionalEvidence: additionalEvidence));
            var request = new ExecutionPacketRequest(
                task,
                project,
                repository,
                sourceRevision,
                TaskIdentity: StableTaskIdentity(task, project),
                Recommendations: ContentRecommendations(rootPath, storePath, task),
                MaxBytes: maxBytes,
                MaxEntries: maxEntries);
            ExecutionPacket packet = service.CreateExecutionPacket(graph, request);
            PredictedImpact prediction = new ImpactGraphService().Predict(graph, task, maxEntries);
            ActualImpact? actual = changedPaths is { Count: > 0 }
                ? service.AnalyzeActualImpact(graph, changedPaths, rootPath, prediction)
                : null;
            AgentImpactObservabilityRecorder.RecordPacketGenerated(packet, task, project, repository);
            AgentImpactObservabilityRecorder.RecordPredictedImpact(packet, prediction);
            if (actual is not null)
            {
                AgentImpactObservabilityRecorder.RecordActualImpact(packet, actual);
            }
            return new ExecutionPacketGenerationResult(packet, prediction, actual, Graph: graph);
        }
        catch (Exception exception)
        {
            return new ExecutionPacketGenerationResult(
                null,
                null,
                null,
                "IMPACT_CONTEXT_UNAVAILABLE",
                Bound(exception.Message));
        }
    }
    private static IReadOnlyList<ImpactRecommendation> ContentRecommendations(
        string rootPath,
        string? storePath,
        string task)
    {
        string contentStorePath = ContentIntelligenceStorage.ResolveDefaultPath(rootPath);
        if (!File.Exists(contentStorePath))
        {
            return [];
        }

        try
        {
            var service = new ContentIntelligenceService(
                new ContentIntelligenceStore(contentStorePath));
            ContentQueryResult result = service.Query(
                new ContentQueryRequest(
                    Query: task,
                    Limit: 4,
                    MaxBytes: 4_096,
                    RootPath: rootPath,
                    IndexStorePath: storePath));
            var recommendations = new List<ImpactRecommendation>();
            foreach (ContentPrecedentSummary item in result.Results)
            {
                bool vanilla = string.Equals(
                    item.ReuseSource,
                    ContentReuseSources.VanillaReference,
                    StringComparison.Ordinal);
                recommendations.Add(
                    new ImpactRecommendation(
                        vanilla ? "VANILLA_REFERENCE" : "BEST_PRECEDENT",
                        item.SourceFiles?.FirstOrDefault() ?? item.BlueprintId,
                        vanilla
                            ? "content intelligence ranked a vanilla/reference comparator"
                            : "content intelligence ranked a reusable project precedent",
                        new ImpactProvenance(
                            "content-intelligence",
                            item.FinalOutcome == "PASS"
                                ? ImpactEvidenceClasses.Learned
                                : ImpactEvidenceClasses.Uncertain,
                            item.EvidenceId ?? item.BlueprintId,
                            item.GameplayRole ?? item.ContentKind),
                        item.TrustRank));
            }

            return recommendations;
        }
        catch (Exception)
        {
            return [];
        }
    }


    private static string StableTaskIdentity(string task, string? project) =>
        global::RimContext.Core.Model.StableEntityId.DigestBase32(
            (project ?? "") + "\0" + task.Trim().ToLowerInvariant());

    private static string Bound(string value) =>
        value.Length <= 256 ? value : value[..256];
}
