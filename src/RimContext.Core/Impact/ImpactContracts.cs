using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimContext.Core.Impact;

public static class ImpactGraphSchemas
{
    public const string Current = "rimimpact-graph/v1";
}

public static class ExecutionPacketSchemas
{
    public const string Current = "rimexecution-packet/v1";
}

public static class ImpactRelationshipKinds
{
    public const string SourceComponent = "source_component";
    public const string ComponentDef = "component_def";
    public const string DefReference = "def_reference";
    public const string RecipeInput = "recipe_input";
    public const string RecipeProduct = "recipe_product";
    public const string RecipeWorkbench = "recipe_workbench";
    public const string ResearchUnlock = "research_unlock";
    public const string ResearchPrerequisite = "research_prerequisite";
    public const string FrameworkConsumer = "framework_consumer";
    public const string HarmonyTarget = "harmony_target";
    public const string SerializationConcern = "serialization_concern";
    public const string TestCoverage = "test_coverage";
    public const string RuntimeObservation = "runtime_observation";
    public const string ProjectDependency = "project_dependency";
    public const string PriorFailure = "prior_failure";
    public const string ContentPrecedent = "content_precedent";
}

public static class ImpactEvidenceClasses
{
    public const string Deterministic = "deterministic";
    public const string Explicit = "explicit";
    public const string Indexed = "indexed";
    public const string ObservedRuntime = "observed_runtime";
    public const string FrameworkKnown = "framework_known";
    public const string Learned = "learned";
    public const string Inferred = "inferred";
    public const string Uncertain = "uncertain";
}

public static class ImpactClasses
{
    public const string Direct = "DIRECT";
    public const string Declared = "DECLARED";
    public const string RuntimeObserved = "RUNTIME_OBSERVED";
    public const string Framework = "FRAMEWORK";
    public const string DynamicPotential = "DYNAMIC/POTENTIAL";
    public const string Learned = "LEARNED";
    public const string Unknown = "UNKNOWN";
}

public static class ExecutionPacketStatuses
{
    public const string Valid = "valid";
    public const string PartiallyStale = "partially_stale";
    public const string Invalid = "invalid";
    public const string Unavailable = "unavailable";
}

public sealed record ImpactGraphIdentity(
    string WorkspaceIdentity,
    string WorkspaceGeneration,
    string IndexGeneration,
    string? Repository = null,
    string? SourceRevision = null,
    string? Project = null,
    IReadOnlyDictionary<string, string>? DependencyVersions = null,
    string? TaskIdentity = null);

public sealed record ImpactProvenance(
    string Source,
    string EvidenceClass,
    string? EvidenceId = null,
    string? Reason = null,
    string? ObservedAtUtc = null);

public sealed record ImpactNode(
    string Id,
    string Kind,
    string Identity,
    string? DisplayName = null,
    string? File = null,
    int? Line = null,
    string? Project = null,
    ImpactProvenance? Provenance = null);

public sealed record ImpactEdge(
    string Id,
    string FromId,
    string? ToId,
    string RelationshipKind,
    string ImpactClass,
    ImpactProvenance Provenance,
    string? ObservedTarget = null,
    string? File = null,
    int? Line = null);

public sealed record ImpactGraph(
    string SchemaVersion,
    ImpactGraphIdentity Identity,
    IReadOnlyList<ImpactNode> Nodes,
    IReadOnlyList<ImpactEdge> Edges,
    ImpactGraphBuildMetrics Metrics);

public sealed record ImpactGraphBuildMetrics(
    long ElapsedMilliseconds,
    int IndexedLookups,
    bool IndexCacheHit,
    int NodeCount,
    int EdgeCount,
    int AugmentedEdgeCount = 0,
    int? ExpensiveFreshLookupsAvoided = null);

public sealed record ImpactGraphEvidence(
    string FromIdentity,
    string? ToIdentity,
    string RelationshipKind,
    string ImpactClass,
    ImpactProvenance Provenance,
    string? FromKind = null,
    string? ToKind = null,
    string? FromDisplayName = null,
    string? ToDisplayName = null,
    string? FromFile = null,
    string? ToFile = null);

public sealed record ImpactGraphBuildRequest(
    string? RootPath = null,
    string? StorePath = null,
    IReadOnlyList<string>? AssemblyRoots = null,
    string? Repository = null,
    string? Project = null,
    string? SourceRevision = null,
    string? TaskIdentity = null,
    IReadOnlyDictionary<string, string>? DependencyVersions = null,
    IReadOnlyList<ImpactGraphEvidence>? AdditionalEvidence = null);

public sealed record ImpactPacketReference(
    string Value,
    string? NodeId = null,
    int Rank = 0,
    ImpactProvenance? Provenance = null);

public sealed record ImpactRecommendation(
    string Kind,
    string Value,
    string Reason,
    ImpactProvenance Provenance,
    int Rank = 0);

public sealed record ExecutionPacketMetrics(
    long GenerationElapsedMilliseconds,
    int SizeBytes,
    int IndexedLookups,
    bool IndexCacheHit,
    int? ExpensiveFreshLookupsAvoided = null,
    int? AgentInputTokens = null);

public sealed record ExecutionPacket(
    string SchemaVersion,
    string Status,
    string Task,
    string? Project,
    string? Repository,
    ImpactGraphIdentity Identity,
    IReadOnlyList<ImpactPacketReference> TopFiles,
    ImpactRecommendation? BestPrecedent,
    ImpactRecommendation? VanillaReference,
    ImpactRecommendation? ReusableCapability,
    ImpactRecommendation? LikelyImplementation,
    IReadOnlyList<string> KnownConstraints,
    IReadOnlyList<string> PredictedValidation,
    IReadOnlyList<ImpactPacketReference> ExpandHandles,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RelevantNodeIds,
    ExecutionPacketMetrics Metrics,
    PacketBudget Budget,
    IReadOnlyList<string>? StaleSections = null)
{
    [JsonPropertyName("remediationPrecedents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent>? RemediationPrecedents { get; init; }
}

public sealed record PacketBudget(
    int MaxBytes,
    int MaxEntries,
    int UsedBytes,
    bool Truncated);

public sealed record ExecutionPacketRequest(
    string Task,
    string? Project = null,
    string? Repository = null,
    string? SourceRevision = null,
    string? TaskIdentity = null,
    int MaxBytes = 16 * 1024,
    int MaxEntries = 16,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<ImpactRecommendation>? Recommendations = null,
    IReadOnlyList<string>? PredictedValidation = null,
    IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent>? RemediationPrecedents = null);

public sealed record PredictedImpact(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> ImpactClasses,
    IReadOnlyList<string> ValidationConcerns,
    string Basis,
    bool Truncated = false);

public sealed record ActualImpact(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> ChangedNodeIds,
    IReadOnlyList<string> DirectDependents,
    IReadOnlyList<string> ImpactClasses,
    IReadOnlyList<string> ValidationConcerns,
    bool HarmonyOrDynamicRisk,
    bool SerializationRisk,
    bool ScopeExpanded,
    IReadOnlyList<string> ExpansionReasons,
    PredictedImpact? Prediction = null);

public sealed record ImpactStatusResult(
    string Status,
    IReadOnlyList<string> StaleSections,
    IReadOnlyList<string> Reasons);

public static class ImpactGraphJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(ImpactGraph graph) =>
        JsonSerializer.Serialize(graph, Options);

    public static ImpactGraph Deserialize(string json)
    {
        ImpactGraph graph = JsonSerializer.Deserialize<ImpactGraph>(json, Options) ??
            throw new InvalidOperationException("Impact graph JSON was empty.");
        if (!string.Equals(graph.SchemaVersion, ImpactGraphSchemas.Current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported impact graph schema: {graph.SchemaVersion}.");
        }

        return graph;
    }
}

public static class ExecutionPacketJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(ExecutionPacket packet) =>
        JsonSerializer.Serialize(packet, Options);

    public static ExecutionPacket Deserialize(string json)
    {
        ExecutionPacket packet = JsonSerializer.Deserialize<ExecutionPacket>(json, Options) ??
            throw new InvalidOperationException("Execution packet JSON was empty.");
        if (!string.Equals(packet.SchemaVersion, ExecutionPacketSchemas.Current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported execution packet schema: {packet.SchemaVersion}.");
        }

        return packet;
    }

    public static int Utf8Bytes(ExecutionPacket packet) =>
        Encoding.UTF8.GetByteCount(Serialize(packet));
}
