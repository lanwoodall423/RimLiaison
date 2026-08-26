using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimContext.Core.Content;

public static class ContentPhase2Schemas
{
    public const string Qualification = "content-qualification/v1";
    public const string Analysis = "content-analysis/v1";
    public const string Archetype = "content-archetype/v1";
    public const string Promotion = "content-promotion/v1";
    public const string Replay = "content-replay/v1";
    public const string ReuseDecision = "content-reuse-decision/v1";
}

public sealed record ContentQualificationCriteria(
    string CriteriaVersion = "content-qualification-criteria/v1",
    int MinimumSuccessfulImplementations = 2,
    int MinimumDistinctProjects = 2,
    int MinimumDistinctRuns = 2,
    double MaximumRepairRate = 0.5,
    bool RequireIndependentRuns = true,
    bool RequireAllApplicableValidation = true,
    bool RequireFreshEvidence = true,
    int RegressionFailureThreshold = 1);

public sealed record ContentStructuralFingerprint(
    string FingerprintVersion,
    string Fingerprint,
    string CanonicalShape,
    string? ContentKind,
    string? GameplayRole);

public sealed record ContentQualificationResult(
    string SchemaVersion,
    bool Qualified,
    ContentQualificationCriteria Criteria,
    int SuccessfulImplementations,
    int DistinctProjects,
    int DistinctRuns,
    int ValidationAttempts,
    int RepairCount,
    int FailureCount,
    int StaleEvidenceCount,
    double RepairRate,
    IReadOnlyList<string> SupportingBlueprintIds,
    IReadOnlyList<string> Reasons);

public sealed record ContentPrecedentCandidate(
    string CandidateId,
    ContentStructuralFingerprint StructuralFingerprint,
    IReadOnlyList<string> BlueprintIds,
    IReadOnlyList<string> Projects,
    ContentQualificationResult Qualification,
    string? RepresentativeBlueprintId = null);

public sealed record ContentAnalysisRequest(
    string? ContentKind = null,
    string? GameplayRole = null,
    int Limit = 20,
    int MaxBytes = 65_536,
    ContentQualificationCriteria? Criteria = null,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? FrameworkRequirements = null);

public sealed record ContentAnalysisResult(
    string SchemaVersion,
    IReadOnlyList<ContentPrecedentCandidate> Candidates,
    bool Truncated,
    int ResultLimit,
    int MaxBytes);

public sealed record ContentArchetype(
    string SchemaVersion,
    string ArchetypeId,
    int Version,
    string Status,
    ContentStructuralFingerprint StructuralFingerprint,
    string? ContentKind,
    string? GameplayRole,
    IReadOnlyDictionary<string, string>? Templates = null,
    IReadOnlyDictionary<string, string>? Defaults = null,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? ValidationExpectations = null,
    IReadOnlyList<string>? SupportingBlueprintIds = null,
    string? PromotedAtUtc = null,
    string? QuarantinedAtUtc = null,
    string? QuarantineReason = null,
    IReadOnlyList<string>? Examples = null,
    IReadOnlyList<string>? FrameworkRequirements = null);

public sealed record ContentReplayResult(
    string SchemaVersion,
    bool Passed,
    string ArchetypeId,
    IReadOnlyList<string> ReplayedBlueprintIds,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Checks);

public sealed record ContentPromotionResult(
    string SchemaVersion,
    bool Promoted,
    string? ArchetypeId,
    int? Version,
    ContentReplayResult Replay,
    IReadOnlyList<string> Reasons,
    string? CandidateId = null);

public sealed record ContentReuseRequest(
    string? ContentKind = null,
    string? GameplayRole = null,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? FrameworkRequirements = null,
    string? Project = null,
    string? RootPath = null,
    string? IndexStorePath = null);

public sealed record ContentReuseDecision(
    string SchemaVersion,
    string Source,
    string Reason,
    IReadOnlyList<string>? ReferenceIds = null,
    string? SelectedAtUtc = null);

public sealed record ContentArchetypeUsage(
    string SchemaVersion,
    string UsageId,
    string ArchetypeId,
    int ArchetypeVersion,
    string BlueprintId,
    string? EvidenceId = null,
    bool? Succeeded = null,
    string? CapturedAtUtc = null,
    string? SourceFingerprint = null);

public sealed record ContentIntelligenceSnapshot(
    IReadOnlyList<ContentBlueprint> Blueprints,
    IReadOnlyList<ContentEvidence> Evidences,
    IReadOnlyList<ContentPrecedentPolicy> Policies,
    IReadOnlyList<ContentArchetype> Archetypes,
    IReadOnlyList<ContentArchetypeUsage> Usages);

public sealed record ContentEvidenceLifecycleResult(
    ContentEvidence Evidence,
    IReadOnlyList<ContentPrecedentCandidate> Candidates,
    IReadOnlyList<ContentPromotionResult> Promotions,
    ContentArchetypeUsage? ArchetypeUsage = null,
    bool ArchetypeQuarantined = false,
    string? QuarantineReason = null,
    int? RolledBackToVersion = null);

public static class ContentPhase2Json
{
    public static void ValidateQualification(ContentQualificationResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.SchemaVersion, ContentPhase2Schemas.Qualification, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported content qualification schema: {value.SchemaVersion}.");
        }
    }

    public static void ValidateArchetype(ContentArchetype value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.SchemaVersion, ContentPhase2Schemas.Archetype, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported content archetype schema: {value.SchemaVersion}.");
        }
    }

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ContentIntelligenceJson.Options);
}
