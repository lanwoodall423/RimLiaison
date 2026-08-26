using RimContext.Core.Impact;

namespace RimLiaison.Observability;

/// <summary>
/// Exceptional, auditable learning controls. Normal packet generation, planning, and execution
/// never call this API.
/// </summary>
public sealed class AgentImpactObservabilityAdministration
{
    private readonly ImpactLearningStore store;

    public AgentImpactObservabilityAdministration(ImpactLearningStore? store = null)
    {
        this.store = store ?? new ImpactLearningStore();
    }

    public IReadOnlyList<LearnedImpactRelationship> Inspect(
        string? project = null,
        string? frameworkVersion = null,
        string? rimWorldVersion = null) =>
        store.Read(project, frameworkVersion, rimWorldVersion);

    public void InvalidateRelationship(
        LearnedImpactRelationship relationship,
        ValidationSourceIdentity sourceIdentity,
        string evidenceId,
        string reason) =>
        ApplyOverride(
            new ImpactLearningOverride(
                relationship.FromIdentity,
                relationship.ToIdentity,
                relationship.RelationshipKind,
                relationship.Scope == "project" ? relationship.Project : null,
                Excluded: true,
                reason,
                sourceIdentity,
                evidenceId));

    public void ExcludeForProject(
        string fromIdentity,
        string toIdentity,
        string relationshipKind,
        string project,
        ValidationSourceIdentity sourceIdentity,
        string evidenceId,
        string reason) =>
        ApplyOverride(
            new ImpactLearningOverride(
                fromIdentity,
                toIdentity,
                relationshipKind,
                project,
                Excluded: true,
                reason,
                sourceIdentity,
                evidenceId));

    private void ApplyOverride(ImpactLearningOverride learningOverride)
    {
        store.AppendOverride(learningOverride);
        AgentImpactObservabilityRecorder.RecordProjectOverride(learningOverride);
    }

    public IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent> InspectRemediation(
        string failureFamily,
        global::RimDev.Contracts.EntityReference? subject = null) =>
        store.ReadRemediationPrecedents(failureFamily, subject);

    public void SetRemediationEligibility(
        global::RimDev.Contracts.RemediationPrecedent precedent,
        bool eligible,
        string reason)
    {
        store.SetRemediationEligibility(precedent, eligible, reason);
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RemediationPrecedentEligibilityChanged,
            eligible
                ? "Remediation precedent eligibility restored."
                : "Remediation precedent marked ineligible.",
            new
            {
                precedentId = precedent.PrecedentId,
                eligible,
                reason = AgentObservabilityData.BoundText(reason, 256)
            });
    }
}
