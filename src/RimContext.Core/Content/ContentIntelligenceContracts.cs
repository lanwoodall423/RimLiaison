using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimContext.Core.Content;

public static class ContentIntelligenceSchemas
{
    public const string Blueprint = "content-blueprint/v1";
    public const string Evidence = "content-evidence/v1";
    public const string Query = "content-query/v1";
    public const string Store = "content-intelligence-store/v1";
}

public static class ContentReuseSources
{
    public const string RimContent = "RimContent";
    public const string Precedent = "precedent";
    public const string VanillaReference = "vanilla/reference";
    public const string Novel = "novel";

    public static int TrustRank(string? source) => source switch
    {
        RimContent => 0,
        Precedent => 1,
        VanillaReference => 2,
        Novel => 3,
        _ => 4
    };
    public static bool IsKnown(string? source) =>
        source is null or RimContent or Precedent or VanillaReference or Novel;
}

public sealed record ContentBlueprintIntent(
    string? ContentKind = null,
    string? GameplayRole = null,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? VanillaComparables = null,
    IReadOnlyList<string>? FrameworkRequirements = null,
    bool? DeliberateNoFramework = null,
    IReadOnlyList<string>? ProjectConstraints = null,
    IReadOnlyList<string>? ValidationExpectations = null,
    string? ReuseSource = null);

public sealed record ContentSourceIdentity(
    string? Repository = null,
    string? Project = null,
    string? Commit = null,
    string? SourceFingerprint = null,
    string? WorkspaceIdentity = null,
    string? ToolVersion = null,
    string? RimWorldVersion = null);

public sealed record ContentRepairAttempt(
    string? Stage = null,
    string? ErrorCode = null,
    string? Summary = null,
    bool? Succeeded = null,
    string? TimestampUtc = null);

public sealed record ContentMetricSnapshot(
    long? ElapsedMilliseconds = null,
    long? InputTokens = null,
    long? OutputTokens = null);

public sealed record ContentBlueprintMetadata(
    string? Repository = null,
    string? Project = null,
    string? AgentId = null,
    string? SessionId = null,
    string? RunId = null,
    IReadOnlyList<string>? SourceFiles = null,
    IReadOnlyList<string>? EntityIdentifiers = null,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyList<string>? FrameworkDependencies = null,
    ContentSourceIdentity? SourceIdentity = null,
    string? CreatedAtUtc = null,
    string? UpdatedAtUtc = null,
    IReadOnlyList<string>? ValidationEvidence = null,
    IReadOnlyList<ContentRepairAttempt>? RepairHistory = null,
    ContentMetricSnapshot? Metrics = null,
    string? LogicalAgentId = null);

public sealed record ContentBlueprint(
    string SchemaVersion,
    string BlueprintId,
    ContentBlueprintIntent Intent,
    ContentBlueprintMetadata Metadata,
    string? ProjectOverride = null,
    bool? ExcludedFromGlobalReuse = null,
    ContentReuseDecision? ReuseDecision = null,
    IReadOnlyList<RimDev.Contracts.ValidationRequirement>? ValidationRequirements = null);

public sealed record ContentEvidenceOutcome(
    string? StaticReferenceValidation = null,
    string? Build = null,
    string? AffectedTests = null,
    string? Runtime = null,
    string? Serialization = null,
    string? Final = null);

public sealed record ContentEvidence(
    string SchemaVersion,
    string EvidenceId,
    string BlueprintId,
    ContentSourceIdentity SourceIdentity,
    ContentEvidenceOutcome Outcome,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<ContentRepairAttempt>? Repairs = null,
    string? CapturedAtUtc = null,
    ContentMetricSnapshot? Metrics = null,
    IReadOnlyList<string>? EvidenceReferences = null);

public sealed record ContentPrecedentPolicy(
    string PrecedentId,
    string? Project = null,
    bool Excluded = false,
    IReadOnlyList<string>? Constraints = null,
    string? UpdatedAtUtc = null);

public sealed record ContentBlueprintCaptureRequest(
    string RootPath,
    string? StorePath,
    ContentBlueprintIntent Intent,
    IReadOnlyList<string>? ChangedPaths = null,
    string? Repository = null,
    string? Project = null,
    string? AgentId = null,
    string? SessionId = null,
    string? RunId = null,
    string? Commit = null,
    string? RimWorldVersion = null,
    string? CapturedAtUtc = null,
    IReadOnlyList<string>? ValidationEvidence = null,
    IReadOnlyList<ContentRepairAttempt>? RepairHistory = null,
    ContentMetricSnapshot? Metrics = null,
    string? LogicalAgentId = null,
    IReadOnlyList<RimDev.Contracts.ValidationRequirement>? ValidationRequirements = null);

public sealed record ContentEvidenceCaptureRequest(
    string BlueprintId,
    ContentSourceIdentity SourceIdentity,
    ContentEvidenceOutcome Outcome,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<ContentRepairAttempt>? Repairs = null,
    string? CapturedAtUtc = null,
    ContentMetricSnapshot? Metrics = null,
    IReadOnlyList<string>? EvidenceReferences = null);
public sealed record ContentQueryRequest(
    string? Query = null,
    string? ContentKind = null,
    string? GameplayRole = null,
    string? Project = null,
    bool IncludeFailures = false,
    int Limit = 20,
    int MaxBytes = 65_536,
    string? RootPath = null,
    string? IndexStorePath = null);

public sealed record ContentPrecedentSummary(
    string BlueprintId,
    string? ContentKind,
    string? GameplayRole,
    string? ReuseSource,
    string? Project,
    string? SourceFingerprint,
    string? Commit,
    IReadOnlyList<string>? SourceFiles,
    IReadOnlyList<string>? EntityIdentifiers,
    IReadOnlyList<string>? Dependencies,
    IReadOnlyList<string>? Constraints,
    string? FinalOutcome,
    string? EvidenceId,
    int TrustRank,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? VanillaComparables = null,
    IReadOnlyList<string>? FrameworkRequirements = null,
    IReadOnlyList<string>? ValidationExpectations = null,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? Warnings = null,
    string? Repository = null,
    string? ProjectOverride = null,
    bool? ExcludedFromGlobalReuse = null,
    IReadOnlyList<string>? FrameworkDependencies = null);

public sealed record ContentQueryResult(
    string SchemaVersion,
    IReadOnlyList<ContentPrecedentSummary> Results,
    bool Truncated,
    int ResultLimit,
    int MaxBytes);

public static class ContentIntelligenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ??
        throw new JsonException("The content intelligence record was empty.");

    public static void ValidateBlueprint(ContentBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        if (blueprint.Intent is null || blueprint.Metadata is null)
        {
            throw new JsonException("A content blueprint requires intent and metadata objects.");
        }

        if (!string.Equals(blueprint.SchemaVersion, ContentIntelligenceSchemas.Blueprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported content blueprint schema: {blueprint.SchemaVersion}.");
        }
        if (!ContentReuseSources.IsKnown(blueprint.Intent.ReuseSource))
        {
            throw new InvalidOperationException(
                $"Unsupported content reuse source: {blueprint.Intent.ReuseSource}.");
        }
    }
    public static void ValidateEvidence(ContentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.SourceIdentity is null || evidence.Outcome is null)
        {
            throw new JsonException("Content evidence requires source identity and outcome objects.");
        }

        if (!string.Equals(evidence.SchemaVersion, ContentIntelligenceSchemas.Evidence, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported content evidence schema: {evidence.SchemaVersion}.");
        }
    }
}
