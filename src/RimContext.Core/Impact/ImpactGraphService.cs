using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimContext.Core.Configuration;
using RimContext.Core.Model;
using RimContext.Core.Storage;

namespace RimContext.Core.Impact;

public sealed class ImpactGraphService
{
    public ImpactGraph Build(ImpactGraphBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        WorkspaceConfiguration configuration = WorkspaceConfiguration.Resolve(
            request.RootPath,
            request.StorePath,
            request.AssemblyRoots);
        using IndexStore store = IndexStore.OpenReadOnly(configuration);
        IReadOnlyList<IndexedFileRecord> files = store.GetFiles();
        IReadOnlyList<EntityRecord> entities = store.GetEntities();
        IReadOnlyList<RelationRecord> relations = store.GetRelations();

        var nodes = new Dictionary<string, ImpactNode>(StringComparer.Ordinal);
        foreach (IndexedFileRecord file in files)
        {
            nodes[file.Id] = new ImpactNode(
                file.Id,
                file.Kind,
                file.Path,
                Path.GetFileName(file.Path),
                file.Path,
                null,
                request.Project,
                new ImpactProvenance(
                    "rimcontext-index",
                    ImpactEvidenceClasses.Deterministic,
                    file.Id,
                    "indexed workspace file"));
        }

        foreach (EntityRecord entity in entities)
        {
            if (entity.Kind == "diagnostic")
            {
                continue;
            }

            nodes[entity.Id] = new ImpactNode(
                entity.Id,
                entity.Kind,
                entity.IdentityKey,
                DisplayName(entity),
                FilePath(entity.FileId, files),
                entity.Line,
                request.Project,
                new ImpactProvenance(
                    "rimcontext-index",
                    ImpactEvidenceClasses.Indexed,
                    entity.Id,
                    "indexed semantic entity"));
        }

        var edges = new Dictionary<string, ImpactEdge>(StringComparer.Ordinal);
        foreach (RelationRecord relation in relations)
        {
            ImpactProvenance provenance = new(
                "rimcontext-index",
                EvidenceClassFor(relation.Kind),
                relation.Id,
                relation.Kind);
            ImpactEdge edge = new(
                relation.Id,
                relation.FromId,
                relation.ToId,
                RelationshipKindFor(relation),
                ImpactClassFor(relation.Kind),
                provenance,
                ObservedTarget(relation.PayloadJson),
                FilePath(relation.FileId, files),
                relation.Line);
            edges[edge.Id] = edge;
        }

        int augmented = 0;
        foreach (ImpactGraphEvidence evidence in request.AdditionalEvidence ?? [])
        {
            string fromId = ResolveOrCreateNode(
                evidence.FromIdentity,
                evidence.FromKind ?? "component",
                evidence.FromDisplayName,
                evidence.FromFile,
                request.Project,
                nodes);
            string? toId = evidence.ToIdentity is null
                ? null
                : ResolveOrCreateNode(
                    evidence.ToIdentity,
                    evidence.ToKind ?? "component",
                    evidence.ToDisplayName,
                    evidence.ToFile,
                    request.Project,
                    nodes);
            string edgeId = StableEntityId.Create(
                "impact-edge",
                evidence.RelationshipKind,
                fromId + "\0" + (toId ?? evidence.ToIdentity ?? "unknown") + "\0" + evidence.Provenance.EvidenceId);
            if (edges.TryAdd(
                    edgeId,
                    new ImpactEdge(
                        edgeId,
                        fromId,
                        toId,
                        evidence.RelationshipKind,
                        evidence.ImpactClass,
                        evidence.Provenance,
                        evidence.ToIdentity,
                        evidence.FromFile,
                        null)))
            {
                augmented++;
            }
        }

        string sourceRevision = request.SourceRevision ?? ComputeSourceRevision(files);
        string indexGeneration = StableEntityId.DigestBase32(
            string.Join("\0", files.Select(file => file.Id + "=" + file.ContentHash)));
        var identity = new ImpactGraphIdentity(
            configuration.WorkspaceIdentity,
            WorkspaceGeneration(configuration, files),
            indexGeneration,
            request.Repository,
            sourceRevision,
            request.Project,
            request.DependencyVersions,
            request.TaskIdentity);
        long elapsed = ElapsedMilliseconds(started);
        var metrics = new ImpactGraphBuildMetrics(
            elapsed,
            IndexedLookups: 3,
            IndexCacheHit: true,
            nodes.Count,
            edges.Count,
            augmented);
        return new ImpactGraph(
            ImpactGraphSchemas.Current,
            identity,
            nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
            metrics);
    }

    public PredictedImpact Predict(
        ImpactGraph graph,
        string task,
        int maxEntries = 16)
    {
        ArgumentNullException.ThrowIfNull(graph);
        string[] terms = Tokens(task);
        var ranked = graph.Nodes
            .Select(node => (Node: node, Score: Score(node, terms)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Node.File ?? item.Node.Identity, StringComparer.Ordinal)
            .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxEntries))
            .Select(item => item.Node)
            .ToArray();
        if (ranked.Length == 0)
        {
            return new PredictedImpact([], [], [ImpactClasses.Unknown], [], "task intent did not match indexed identities");
        }

        var nodeIds = new HashSet<string>(ranked.Select(node => node.Id), StringComparer.Ordinal);
        foreach (ImpactEdge edge in graph.Edges)
        {
            if (nodeIds.Contains(edge.FromId) || edge.ToId is not null && nodeIds.Contains(edge.ToId))
            {
                nodeIds.Add(edge.FromId);
                if (edge.ToId is not null)
                {
                    nodeIds.Add(edge.ToId);
                }
            }
        }

        ImpactNode[] selected = graph.Nodes
            .Where(node => nodeIds.Contains(node.Id))
            .OrderBy(node => node.File ?? node.Identity, StringComparer.Ordinal)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxEntries))
            .ToArray();
        string[] concerns = Concerns(graph, selected.Select(node => node.Id).ToHashSet(StringComparer.Ordinal));
        string[] classes = graph.Edges
            .Where(edge => selected.Any(node => node.Id == edge.FromId || node.Id == edge.ToId))
            .Select(edge => edge.ImpactClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new PredictedImpact(
            selected.Select(node => node.File).Where(value => value is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray(),
            selected.Select(node => node.Id).ToArray(),
            classes.Length == 0 ? [ImpactClasses.Unknown] : classes,
            concerns,
            "task intent matched indexed entities and their related edges",
            selected.Length < nodeIds.Count);
    }

    public ActualImpact AnalyzeDiff(
        ImpactGraph graph,
        IReadOnlyList<string> changedPaths,
        string? rootPath = null,
        PredictedImpact? prediction = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(changedPaths);
        HashSet<string> normalizedPaths = changedPaths
            .Select(path => NormalizePath(path, rootPath))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImpactNode[] changedNodes = graph.Nodes
            .Where(node => node.File is not null && normalizedPaths.Contains(NormalizePath(node.File, rootPath)))
            .ToArray();
        bool unknownChangedFiles = normalizedPaths.Count > 0 && changedNodes.Length == 0;
        var changedIds = changedNodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var dependentIds = new HashSet<string>(StringComparer.Ordinal);
        var impactClasses = new HashSet<string>(StringComparer.Ordinal);
        bool dynamic = false;
        bool serialization = false;
        foreach (ImpactEdge edge in graph.Edges)
        {
            if (!changedIds.Contains(edge.FromId) &&
                (edge.ToId is null || !changedIds.Contains(edge.ToId)))
            {
                continue;
            }

            impactClasses.Add(edge.ImpactClass);
            if (changedIds.Contains(edge.FromId) && edge.ToId is not null)
            {
                dependentIds.Add(edge.ToId);
            }
            if (edge.ToId is not null && changedIds.Contains(edge.ToId))
            {
                dependentIds.Add(edge.FromId);
            }
            dynamic |= edge.ImpactClass == ImpactClasses.DynamicPotential ||
                edge.RelationshipKind == ImpactRelationshipKinds.HarmonyTarget;
            serialization |= edge.RelationshipKind == ImpactRelationshipKinds.SerializationConcern ||
                edge.RelationshipKind.Contains("serial", StringComparison.OrdinalIgnoreCase);
        }

        dynamic |= changedNodes.Any(node => node.Kind.Contains("harmony", StringComparison.OrdinalIgnoreCase));
        serialization |= changedNodes.Any(node =>
            node.Identity.Contains("serialize", StringComparison.OrdinalIgnoreCase) ||
            node.Identity.Contains("save", StringComparison.OrdinalIgnoreCase) ||
            node.Identity.Contains("load", StringComparison.OrdinalIgnoreCase));
        if (dynamic)
        {
            impactClasses.Add(ImpactClasses.DynamicPotential);
        }
        if (serialization)
        {
            impactClasses.Add(ImpactClasses.Unknown);
        }
        if (unknownChangedFiles)
        {
            impactClasses.Add(ImpactClasses.Unknown);
        }

        var concerns = Concerns(graph, changedIds.Concat(dependentIds).ToHashSet(StringComparer.Ordinal)).ToList();
        if (unknownChangedFiles)
        {
            concerns.Add("Unknown changed component");
        }

        if (dynamic && !concerns.Contains("Harmony/dynamic dispatch", StringComparer.Ordinal))
        {
            concerns.Add("Harmony/dynamic dispatch");
        }
        if (serialization && !concerns.Contains("Serialization/save-load", StringComparer.Ordinal))
        {
            concerns.Add("Serialization/save-load");
        }
        string[] actualFiles = changedPaths
            .Select(path => NormalizePath(path, rootPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        bool expanded = prediction is not null &&
            (actualFiles.Length > prediction.Files.Count ||
             !prediction.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal) && dynamic ||
             !prediction.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal) &&
                (serialization || unknownChangedFiles));
        var reasons = new List<string>();
        if (prediction is not null && actualFiles.Length > prediction.Files.Count)
        {
            reasons.Add($"actual files {actualFiles.Length} exceeded predicted files {prediction.Files.Count}");
        }
        if (dynamic && prediction is not null && !prediction.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal))
        {
            reasons.Add("actual diff introduced Harmony or dynamic-risk relationships");
        }
        if (serialization && prediction is not null && !prediction.ValidationConcerns.Contains("Serialization/save-load", StringComparer.Ordinal))
        {
            reasons.Add("actual diff introduced serialization/save-load concern");
        }
        if (unknownChangedFiles && prediction is not null &&
            !prediction.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal))
        {
            reasons.Add("actual diff contains files absent from the indexed graph");
        }
        return new ActualImpact(
            actualFiles,
            changedNodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).ToArray(),
            dependentIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            impactClasses.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            concerns,
            dynamic,
            serialization,
            expanded,
            reasons,
            prediction);
    }

    public ImpactStatusResult EvaluatePacket(
        ExecutionPacket packet,
        ImpactGraph currentGraph,
        IReadOnlyList<string>? changedPaths = null,
        string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(currentGraph);
        if (!string.Equals(packet.Identity.WorkspaceIdentity, currentGraph.Identity.WorkspaceIdentity, StringComparison.Ordinal) ||
            !string.Equals(packet.Project, currentGraph.Identity.Project, StringComparison.Ordinal))
        {
            return new ImpactStatusResult(
                ExecutionPacketStatuses.Invalid,
                ["identity"],
                ["workspace or project identity changed"]);
        }
        if (string.Equals(packet.Identity.IndexGeneration, currentGraph.Identity.IndexGeneration, StringComparison.Ordinal) &&
            (changedPaths is null || changedPaths.Count == 0))
        {
            return new ImpactStatusResult(ExecutionPacketStatuses.Valid, [], []);
        }

        HashSet<string> relevantFiles = packet.TopFiles
            .Select(reference => reference.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] changed = (changedPaths ?? [])
            .Select(path => NormalizePath(path, rootPath))
            .ToArray();
        bool relevantChange = changed.Length == 0 || changed.Any(relevantFiles.Contains);
        bool boundaryChange = changed.Any(path =>
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Directory.Build.", StringComparison.OrdinalIgnoreCase));
        if (boundaryChange)
        {
            return new ImpactStatusResult(
                ExecutionPacketStatuses.Invalid,
                ["identity", "validation"],
                ["project or dependency boundary changed"]);
        }
        if (!relevantChange)
        {
            return new ImpactStatusResult(
                ExecutionPacketStatuses.Valid,
                [],
                ["only unrelated files changed; relevant packet sections remain reusable"]);
        }

        var stale = new List<string> { "scope", "topFiles", "validation" };
        if (packet.BestPrecedent is not null)
        {
            stale.Add("precedent");
        }
        return new ImpactStatusResult(
            ExecutionPacketStatuses.PartiallyStale,
            stale.Distinct(StringComparer.Ordinal).ToArray(),
            ["a relevant indexed file changed"]);
    }

    private static string[] Concerns(ImpactGraph graph, HashSet<string> ids)
    {
        var concerns = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImpactEdge edge in graph.Edges)
        {
            if (!ids.Contains(edge.FromId) && (edge.ToId is null || !ids.Contains(edge.ToId)))
            {
                continue;
            }

            if (edge.RelationshipKind == ImpactRelationshipKinds.DefReference ||
                edge.RelationshipKind == ImpactRelationshipKinds.ComponentDef)
            {
                concerns.Add("Def/reference");
            }
            if (edge.RelationshipKind.StartsWith("recipe_", StringComparison.Ordinal))
            {
                concerns.Add("Recipe inputs/products/workbench");
            }
            if (edge.RelationshipKind.StartsWith("research_", StringComparison.Ordinal))
            {
                concerns.Add("Research unlock/prerequisite");
            }
            if (edge.RelationshipKind == ImpactRelationshipKinds.TestCoverage)
            {
                concerns.Add("Declared test coverage");
            }
            if (edge.RelationshipKind == ImpactRelationshipKinds.RuntimeObservation)
            {
                concerns.Add("Runtime scenario");
            }
            if (edge.RelationshipKind == ImpactRelationshipKinds.SerializationConcern)
            {
                concerns.Add("Serialization/save-load");
            }
        }

        return concerns.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static int Score(ImpactNode node, IReadOnlyList<string> terms)
    {
        string value = string.Join(' ', node.Identity, node.DisplayName, node.File).ToLowerInvariant();
        return terms.Sum(term => value.Contains(term, StringComparison.Ordinal) ? 1 : 0);
    }

    private static string[] Tokens(string value) =>
        value.Split([' ', '\t', '\r', '\n', '/', '\\', ':', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.ToLowerInvariant())
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string ResolveOrCreateNode(
        string identity,
        string kind,
        string? displayName,
        string? file,
        string? project,
        IDictionary<string, ImpactNode> nodes)
    {
        ImpactNode? existing = nodes.Values.FirstOrDefault(node =>
            string.Equals(node.Identity, identity, StringComparison.Ordinal) ||
            string.Equals(node.DisplayName, identity, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        string id = StableEntityId.Create("impact-node", kind, identity);
        nodes.TryAdd(
            id,
            new ImpactNode(
                id,
                kind,
                identity,
                displayName ?? identity,
                file,
                null,
                project,
                new ImpactProvenance("augmentation", ImpactEvidenceClasses.Explicit, identity, "declared or observed evidence")));
        return id;
    }

    private static string RelationshipKindFor(RelationRecord relation)
    {
        if (relation.Kind.Contains("harmony", StringComparison.OrdinalIgnoreCase))
        {
            return ImpactRelationshipKinds.HarmonyTarget;
        }
        if (relation.Kind.Contains("def_reference", StringComparison.OrdinalIgnoreCase))
        {
            return ImpactRelationshipKinds.DefReference;
        }
        if (relation.Kind.Contains("project", StringComparison.OrdinalIgnoreCase) ||
            relation.Kind.Contains("assembly", StringComparison.OrdinalIgnoreCase))
        {
            return ImpactRelationshipKinds.ProjectDependency;
        }
        if (relation.Kind.Contains("usage", StringComparison.OrdinalIgnoreCase) ||
            relation.Kind == "owns")
        {
            return ImpactRelationshipKinds.SourceComponent;
        }
        return relation.Kind;
    }

    private static string ImpactClassFor(string relationKind) =>
        relationKind.Contains("harmony", StringComparison.OrdinalIgnoreCase)
            ? ImpactClasses.DynamicPotential
            : ImpactClasses.Direct;

    private static string EvidenceClassFor(string relationKind) =>
        relationKind.Contains("harmony", StringComparison.OrdinalIgnoreCase)
            ? ImpactEvidenceClasses.Uncertain
            : ImpactEvidenceClasses.Deterministic;

    private static string? FilePath(string? fileId, IReadOnlyList<IndexedFileRecord> files) =>
        fileId is null ? null : files.FirstOrDefault(file => file.Id == fileId)?.Path;

    private static string? DisplayName(EntityRecord entity)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(entity.PayloadJson);
            foreach (string property in new[] { "defName", "name", "qualifiedName", "target" })
            {
                if (document.RootElement.TryGetProperty(property, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // The indexed identity remains usable when a payload is malformed.
        }

        return entity.IdentityKey.Split('\0').LastOrDefault();
    }

    private static string? ObservedTarget(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            foreach (string property in new[] { "target", "targetText", "observedTarget", "name" })
            {
                if (document.RootElement.TryGetProperty(property, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string ComputeSourceRevision(IReadOnlyList<IndexedFileRecord> files) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\0", files.Select(file => file.Path + "=" + file.ContentHash)))))
            .ToLowerInvariant();

    private static string WorkspaceGeneration(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> files) =>
        StableEntityId.DigestBase32(
            configuration.WorkspaceIdentity + "\0" +
            string.Join("\0", files.Select(file => file.Id + "=" + file.ContentHash)));

    private static string NormalizePath(string path, string? rootPath)
    {
        string value = path.Replace('\\', '/').TrimStart('.', '/');
        if (rootPath is not null && Path.IsPathFullyQualified(path))
        {
            try
            {
                value = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                // Keep the slash-normalized path for a foreign root.
            }
        }

        return value.TrimStart('.', '/');
    }

    private static long ElapsedMilliseconds(long started) =>
        (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
