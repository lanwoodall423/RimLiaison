using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimContext.Core.Impact;

public static class ValidationPlanSchemas
{
    public const string Current = "rimtest-validation-plan/v1";
    public const string LearningCurrent = "rimimpact-learning/v1";
}

public static class ValidationPlanTiers
{
    public const string NarrowTargeted = "narrow_targeted";
    public const string AffectedComponent = "affected_component";
    public const string AffectedProject = "affected_project";
    public const string AffectedFramework = "affected_framework_dependents";
    public const string BroaderCanonical = "broader_canonical";
}

public static class ValidationRequirementKinds
{
    public const string Build = "compile/build";
    public const string StaticReference = "xml/schema/reference";
    public const string TargetedTest = "targeted_test";
    public const string Integration = "integration_test";
    public const string FrameworkContract = "framework_contract";
    public const string Runtime = "runtime_quicktest";
    public const string Serialization = "save_load";
    public const string Ui = "ui_validation";
    public const string Compatibility = "compatibility";
    public const string BroaderFallback = "broader_fallback";
}

public sealed record ValidationCoverage(string Kind, string Name);

public sealed record ValidationCatalogEntry(
    string TestId,
    string RecipeId,
    IReadOnlyList<ValidationCoverage> Coverage,
    IReadOnlyList<string>? Tags = null,
    string Classification = "REQUIRED",
    string? Project = null,
    string? FrameworkVersion = null,
    string? RimWorldVersion = null,
    int CostRank = 0)
{
    public bool HasTag(string tag) =>
        (Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase);
}

public sealed record ValidationSourceIdentity(
    string WorkspaceIdentity,
    string SourceRevision,
    string? IndexGeneration = null,
    string? Project = null,
    string? Repository = null,
    string? FrameworkVersion = null,
    string? RimWorldVersion = null);

public sealed record ValidationOutcomeEvidence(
    string EvidenceId,
    ValidationSourceIdentity Identity,
    string PlanFingerprint,
    string Status,
    IReadOnlyList<string> TestIds,
    string? RuntimeGeneration = null,
    string? FailureAttribution = null);

public sealed record ValidationRequirement(
    string RequirementId,
    string Kind,
    string Tier,
    string Reason,
    string Classification,
    string? TestId,
    string? RecipeId,
    IReadOnlyList<string> ImpactClasses,
    IReadOnlyList<string> EvidenceIds,
    bool Available = true,
    bool AgentRequested = false,
    string? Source = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ReusedEvidenceIds { get; init; }
}

public sealed record ValidationPlanOverride(
    string RequirementId,
    string Action,
    string EvidenceId,
    ValidationSourceIdentity SourceIdentity,
    string Reason,
    bool Accepted);

public sealed record ValidationPlanMetrics(
    long PlanningElapsedMilliseconds,
    int GraphNodesVisited,
    int GraphEdgesVisited,
    int CatalogEntriesConsidered,
    int LearnedRelationshipsConsidered,
    int DeduplicatedRequirements,
    int? ExpensiveFreshLookupsAvoided = null);

public sealed record ValidationPlan(
    string SchemaVersion,
    string Status,
    string Tier,
    ValidationSourceIdentity SourceIdentity,
    IReadOnlyList<ValidationRequirement> Required,
    IReadOnlyList<ValidationRequirement> Additional,
    IReadOnlyList<ValidationPlanOverride> Overrides,
    IReadOnlyList<string> ActualChangedFiles,
    IReadOnlyList<string> ActualChangedNodeIds,
    IReadOnlyList<string> ImpactClasses,
    IReadOnlyList<string> ValidationConcerns,
    IReadOnlyList<string> ExpansionReasons,
    bool ScopeExpanded,
    string? PredictionTier,
    string PlanFingerprint,
    ValidationPlanMetrics Metrics)
{
    [JsonIgnore]
    public IReadOnlyList<string> RequiredTestIds => Required
        .Where(requirement => requirement.TestId is not null)
        .Select(requirement => requirement.TestId!)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    [JsonIgnore]
    public IReadOnlyList<string> AllTestIds => Required
        .Concat(Additional)
        .Where(requirement => requirement.TestId is not null)
        .Select(requirement => requirement.TestId!)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    [JsonIgnore]
    public IReadOnlyList<string> TestsNeedingExecution => Required
        .Where(requirement => requirement.TestId is not null)
        .GroupBy(requirement => requirement.TestId!, StringComparer.Ordinal)
        .Where(group => group.Any(requirement => requirement.ReusedEvidenceIds is not { Count: > 0 }))
        .Select(group => group.Key)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    [JsonPropertyName("generatedRequirements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimDev.Contracts.ValidationRequirement>? GeneratedRequirements { get; init; }

    [JsonPropertyName("runtimeRequests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimDev.Contracts.RuntimeValidationRequest>? RuntimeRequests { get; init; }
}

public sealed record ValidationPlanRequest(
    ImpactGraph Graph,
    ActualImpact Actual,
    IReadOnlyList<ValidationCatalogEntry> Catalog,
    ValidationSourceIdentity SourceIdentity,
    string? FallbackSuite = null,
    IReadOnlyList<string>? FallbackTestIds = null,
    IReadOnlyList<LearnedImpactRelationship>? LearnedRelationships = null,
    IReadOnlyList<ValidationOutcomeEvidence>? PriorEvidence = null,
    IReadOnlyList<string>? AgentAdditionalTestIds = null,
    IReadOnlyList<RimDev.Contracts.ValidationRequirement>? GeneratedRequirements = null);

public sealed record AgentValidationRequest(
    IReadOnlyList<string>? AdditionalTestIds = null,
    IReadOnlyList<string>? RemoveRequirementIds = null,
    IReadOnlyList<ValidationPlanOverride>? Overrides = null);

public sealed record LearnedImpactRelationship(
    string SchemaVersion,
    string FromIdentity,
    string ToIdentity,
    string RelationshipKind,
    string ImpactClass,
    ImpactProvenance Provenance,
    string Scope,
    string? Project = null,
    string? FrameworkVersion = null,
    string? RimWorldVersion = null,
    string? SourceRevision = null,
    int SupportCount = 1,
    int IndependentObservations = 1,
    string Status = "tentative",
    IReadOnlyList<string>? EvidenceIds = null);

public sealed record ImpactLearningObservation(
    string FromIdentity,
    string ToIdentity,
    string RelationshipKind,
    string ImpactClass,
    ImpactProvenance Provenance,
    ValidationSourceIdentity SourceIdentity,
    string EvidenceId,
    bool CausalAttribution,
    bool RevertedChange = false,
    bool TargetedReproduction = false,
    bool DeterministicRelationship = false,
    bool RimErrorAttribution = false,
    int IndependentObservations = 1,
    string? Project = null,
    bool GlobalCandidate = false);

public sealed record ImpactLearningResult(
    bool Learned,
    bool PromotedGlobal,
    LearnedImpactRelationship? Relationship,
    string? RejectionReason = null);

public sealed record ImpactLearningOverride(
    string FromIdentity,
    string ToIdentity,
    string RelationshipKind,
    string? Project,
    bool Excluded,
    string Reason,
    ValidationSourceIdentity SourceIdentity,
    string EvidenceId);

public static class ValidationEvidenceGate
{
    public static bool IsCurrent(
        ValidationOutcomeEvidence evidence,
        ValidationSourceIdentity current,
        string planFingerprint)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(current);
        RimDev.Contracts.ExecutionIdentity evidenceIdentity = ToSharedIdentity(evidence.Identity);
        RimDev.Contracts.ExecutionIdentity currentIdentity = ToSharedIdentity(current);
        RimDev.Contracts.IdentityComparisonResult comparison =
            RimDev.Contracts.ExecutionIdentityComparer.Compare(
                evidenceIdentity,
                currentIdentity,
                RimDev.Contracts.IdentityComparisonRequirements.Static);
        return evidence.Status == "PASS" &&
            comparison.IsExact &&
            string.Equals(evidence.PlanFingerprint, planFingerprint, StringComparison.Ordinal) &&
            string.Equals(evidence.Identity.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal) &&
            string.Equals(evidence.Identity.IndexGeneration, current.IndexGeneration, StringComparison.Ordinal);

    }
    private static RimDev.Contracts.ExecutionIdentity ToSharedIdentity(ValidationSourceIdentity identity) =>
        new()
        {
            RepositoryId = identity.Repository ?? identity.WorkspaceIdentity,
            ProjectId = identity.Project,
            SourceRevision = identity.SourceRevision,
            BuildIdentity = identity.IndexGeneration
        };

}

public static class ValidationPlanJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(ValidationPlan plan) => JsonSerializer.Serialize(plan, Options);

    public static ValidationPlan Deserialize(string json)
    {
        ValidationPlan plan = JsonSerializer.Deserialize<ValidationPlan>(json, Options) ??
            throw new InvalidOperationException("Validation plan JSON was empty.");
        if (!string.Equals(plan.SchemaVersion, ValidationPlanSchemas.Current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported validation plan schema: {plan.SchemaVersion}.");
        }

        return plan;
    }

    public static string Fingerprint(ValidationPlan plan)
    {
        using SHA256 hash = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(
            string.Join("\0", new[]
            {
                plan.SchemaVersion,
                plan.SourceIdentity.WorkspaceIdentity,
                plan.SourceIdentity.SourceRevision,
                plan.SourceIdentity.IndexGeneration,
                plan.Tier,
                string.Join(",", plan.Required.Select(requirement =>
                    requirement.RequirementId + ":" + requirement.Kind)),
                string.Join(",", plan.ActualChangedNodeIds),
                string.Join(",", plan.GeneratedRequirements?.Select(requirement =>
                    requirement.RequirementId + ":" + requirement.Assertion) ?? []),
                string.Join(",", plan.RuntimeRequests?.Select(request =>
                    request.Subject.Id + ":" + request.Assertion) ?? [])
            }));
        return Convert.ToHexString(hash.ComputeHash(bytes)).ToLowerInvariant();
    }
}
