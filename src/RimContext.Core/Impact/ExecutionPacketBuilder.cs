using System.Diagnostics;
using System.Text;

namespace RimContext.Core.Impact;

public sealed class ExecutionPacketBuilder
{
    private readonly ImpactGraphService impactService;

    public ExecutionPacketBuilder(ImpactGraphService? impactService = null)
    {
        this.impactService = impactService ?? new ImpactGraphService();
    }

    public ExecutionPacket Build(
        ImpactGraph graph,
        ExecutionPacketRequest request)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        int maxBytes = Math.Max(512, request.MaxBytes);
        int maxEntries = Math.Clamp(request.MaxEntries, 1, 100);
        PredictedImpact prediction = impactService.Predict(graph, request.Task, maxEntries);
        ImpactNode[] relevant = graph.Nodes
            .Where(node => prediction.NodeIds.Contains(node.Id, StringComparer.Ordinal))
            .OrderByDescending(node => NodeRank(node, prediction))
            .ThenBy(node => node.File ?? node.Identity, StringComparer.Ordinal)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        ImpactPacketReference[] topFiles = relevant
            .Where(node => node.File is not null)
            .GroupBy(node => node.File!, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new ImpactPacketReference(
                group.Key,
                group.First().Id,
                index + 1,
                new ImpactProvenance(
                    "rimcontext-index",
                    ImpactEvidenceClasses.Indexed,
                    group.First().Id,
                    "ranked by task intent and graph adjacency")))
            .Take(maxEntries)
            .ToArray();

        ImpactRecommendation? bestPrecedent = request.Recommendations?
            .Where(item => item.Kind.Equals("BEST_PRECEDENT", StringComparison.Ordinal))
            .OrderBy(item => item.Rank)
            .FirstOrDefault();
        ImpactRecommendation? vanilla = request.Recommendations?
            .Where(item => item.Kind.Equals("VANILLA_REFERENCE", StringComparison.Ordinal))
            .OrderBy(item => item.Rank)
            .FirstOrDefault();
        ImpactRecommendation? capability = request.Recommendations?
            .Where(item => item.Kind.Equals("REUSABLE_CAPABILITY", StringComparison.Ordinal))
            .OrderBy(item => item.Rank)
            .FirstOrDefault();
        ImpactRecommendation? route = request.Recommendations?
            .Where(item => item.Kind.Equals("LIKELY_IMPLEMENTATION", StringComparison.Ordinal))
            .OrderBy(item => item.Rank)
            .FirstOrDefault();
        route ??= InferImplementation(relevant, prediction);

        var constraints = (request.Constraints ?? [])
            .Concat(InferConstraints(graph, prediction.NodeIds))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(maxEntries)
            .ToArray();
        var validation = (request.PredictedValidation ?? [])
            .Concat(prediction.ValidationConcerns)
            .Concat(DefaultValidation(prediction))
            .Distinct(StringComparer.Ordinal)
            .Take(maxEntries)
            .ToArray();
        var handles = relevant
            .Take(maxEntries)
            .Select((node, index) => new ImpactPacketReference(
                "rimctx://impact/" + node.Id,
                node.Id,
                index + 1,
                node.Provenance))
            .ToArray();
        var unknowns = new List<string>();
        if (relevant.Length == 0)
        {
            unknowns.Add("scope");
            unknowns.Add("precedent");
            unknowns.Add("implementation route");
        }
        if (bestPrecedent is null)
        {
            unknowns.Add("best precedent");
        }
        if (vanilla is null)
        {
            unknowns.Add("vanilla/reference comparator");
        }

        var packet = new ExecutionPacket(
            ExecutionPacketSchemas.Current,
            ExecutionPacketStatuses.Valid,
            request.Task,
            request.Project ?? graph.Identity.Project,
            request.Repository ?? graph.Identity.Repository,
            graph.Identity with
            {
                Project = request.Project ?? graph.Identity.Project,
                Repository = request.Repository ?? graph.Identity.Repository,
                SourceRevision = request.SourceRevision ?? graph.Identity.SourceRevision,
                TaskIdentity = request.TaskIdentity ?? graph.Identity.TaskIdentity
            },
            topFiles,
            bestPrecedent,
            vanilla,
            capability,
            route,
            constraints,
            validation,
            handles,
            unknowns.Distinct(StringComparer.Ordinal).Take(maxEntries).ToArray(),
            prediction.NodeIds.Take(maxEntries).ToArray(),
            new ExecutionPacketMetrics(
                0,
                0,
                graph.Metrics.IndexedLookups,
                graph.Metrics.IndexCacheHit,
                graph.Metrics.ExpensiveFreshLookupsAvoided),
            new PacketBudget(maxBytes, maxEntries, 0, false));

        long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        packet = packet with
        {
            Metrics = packet.Metrics with { GenerationElapsedMilliseconds = elapsed },
            RemediationPrecedents = request.RemediationPrecedents?
                .Take(4)
                .Select(static precedent => precedent.Normalize())
                .ToArray()
        };
        packet = FitBudget(packet, maxBytes, maxEntries);
        for (int iteration = 0; iteration < 3; iteration++)
        {
            int size = ExecutionPacketJson.Utf8Bytes(packet);
            packet = packet with
            {
                Metrics = packet.Metrics with { SizeBytes = size },
                Budget = packet.Budget with { UsedBytes = size }
            };
        }

        return packet;
    }

    private static ExecutionPacket FitBudget(
        ExecutionPacket packet,
        int maxBytes,
        int maxEntries)
    {
        bool truncated = packet.Budget.Truncated;
        ExecutionPacket current = packet;
        while (ExecutionPacketJson.Utf8Bytes(current) > maxBytes)
        {
            if (current.ExpandHandles.Count > 1)
            {
                current = current with { ExpandHandles = current.ExpandHandles.Take(current.ExpandHandles.Count - 1).ToArray() };
            }
            else if (current.TopFiles.Count > 1)
            {
                current = current with { TopFiles = current.TopFiles.Take(current.TopFiles.Count - 1).ToArray() };
            }
            else if (current.PredictedValidation.Count > 1)
            {
                current = current with { PredictedValidation = current.PredictedValidation.Take(current.PredictedValidation.Count - 1).ToArray() };
            }
            else if (current.KnownConstraints.Count > 0)
            {
                current = current with { KnownConstraints = current.KnownConstraints.Take(current.KnownConstraints.Count - 1).ToArray() };
            }
            else if (current.Unknowns.Count > 1)
            {
                current = current with { Unknowns = current.Unknowns.Take(current.Unknowns.Count - 1).ToArray() };
            }
            else if (current.RemediationPrecedents is { Count: > 0 })
            {
                current = current with
                {
                    RemediationPrecedents = current.RemediationPrecedents
                        .Take(current.RemediationPrecedents.Count - 1)
                        .ToArray()
                };
            }
            else if (current.BestPrecedent is not null)
            {
                current = current with { BestPrecedent = null };
            }
            else if (current.VanillaReference is not null)
            {
                current = current with { VanillaReference = null };
            }
            else if (current.ReusableCapability is not null)
            {
                current = current with { ReusableCapability = null };
            }
            else if (current.LikelyImplementation is not null)
            {
                current = current with { LikelyImplementation = null };
            }
            else
            {
                current = current with
                {
                    Task = current.Task.Length > 64 ? current.Task[..64] : current.Task,
                    Project = null,
                    Repository = null,
                    Identity = current.Identity with
                    {
                        Repository = null,
                        SourceRevision = null,
                        Project = null,
                        DependencyVersions = null,
                        TaskIdentity = null
                    },
                    TopFiles = [],
                    BestPrecedent = null,
                    VanillaReference = null,
                    ReusableCapability = null,
                    RemediationPrecedents = null,
                    KnownConstraints = [],
                    PredictedValidation = [],
                    ExpandHandles = [],
                    Unknowns = [],
                    RelevantNodeIds = [],
                    Metrics = new ExecutionPacketMetrics(0, 0, 0, false),
                    Budget = current.Budget with { Truncated = true }
                };
                truncated = true;
                if (ExecutionPacketJson.Utf8Bytes(current) >= maxBytes)
                {
                    break;
                }
            }

            truncated = true;
        }

        int size = ExecutionPacketJson.Utf8Bytes(current);
        return current with
        {
            Budget = current.Budget with
            {
                MaxBytes = maxBytes,
                MaxEntries = maxEntries,
                UsedBytes = size,
                Truncated = truncated
            }
        };
    }

    private static ImpactRecommendation? InferImplementation(
        IReadOnlyList<ImpactNode> relevant,
        PredictedImpact prediction)
    {
        string[] files = relevant
            .Select(node => node.File)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        bool onlyXml = files.All(file => file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        bool hasHarmony = prediction.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal);
        string route = onlyXml ? "xml_defs" : hasHarmony ? "csharp_harmony" : "csharp_or_mixed";
        return new ImpactRecommendation(
            "LIKELY_IMPLEMENTATION",
            route,
            onlyXml
                ? "indexed comparator and relevant files are XML-only"
                : hasHarmony
                    ? "indexed Harmony relationship requires runtime-sensitive C#"
                    : "indexed C# or mixed source component is relevant",
            new ImpactProvenance(
                "rimcontext-index",
                ImpactEvidenceClasses.Inferred,
                null,
                "derived from ranked indexed file kinds"));
    }

    private static string[] InferConstraints(ImpactGraph graph, IReadOnlyList<string> nodeIds)
    {
        var ids = nodeIds.ToHashSet(StringComparer.Ordinal);
        var constraints = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImpactEdge edge in graph.Edges.Where(edge => ids.Contains(edge.FromId) || edge.ToId is not null && ids.Contains(edge.ToId)))
        {
            if (edge.RelationshipKind == ImpactRelationshipKinds.HarmonyTarget)
            {
                constraints.Add("Harmony target is dynamic-risk; preserve target identity");
            }
            if (edge.RelationshipKind == ImpactRelationshipKinds.SerializationConcern)
            {
                constraints.Add("Preserve save/load compatibility");
            }
        }

        return constraints.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string[] DefaultValidation(PredictedImpact prediction)
    {
        var result = new List<string>();
        if (prediction.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal))
        {
            result.Add("Harmony/runtime scenario");
        }
        if (prediction.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal))
        {
            result.Add("Conservative broader validation");
        }

        return result.ToArray();
    }

    private static int NodeRank(ImpactNode node, PredictedImpact prediction)
    {
        int index = prediction.NodeIds.IndexOf(node.Id);
        return index < 0 ? 0 : prediction.NodeIds.Count - index;
    }
}

internal static class ExecutionPacketListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
