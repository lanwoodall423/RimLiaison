using RimContext.Core.Impact;
using RimLiaison.Catalog;
using RimLiaison.RimContext;
using RimLiaison.Results;
using RimLiaison.Observability;

namespace RimLiaison.Validation;

public sealed record ValidationPlanGenerationResult(
    ValidationPlan? Plan,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool Succeeded => Plan is not null && ErrorCode is null;
}

/// <summary>
/// Projects the canonical TestCatalog into the shared RimContext planner. It does not execute
/// recipes or replace DevBridge ownership.
/// </summary>
public static class MinimumSafeValidationCoordinator
{
    public static ValidationPlanGenerationResult TryBuild(
        ExecutionPacketGenerationResult impact,
        CatalogDocument catalog,
        string rootPath,
        string? repository,
        string? project,
        string? fallbackSuite = null,
        IReadOnlyList<string>? agentAdditionalTestIds = null,
        IReadOnlyList<ValidationOutcomeEvidence>? priorEvidence = null,
        IReadOnlyList<global::RimDev.Contracts.ValidationRequirement>? generatedRequirements = null)
    {
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rootPath);
        if (impact.Graph is null || impact.Actual is null)
        {
            return new ValidationPlanGenerationResult(
                null,
                "IMPACT_ANALYSIS_UNAVAILABLE",
                "Actual indexed impact is unavailable; canonical selection remains authoritative.");
        }

        try
        {
            ImpactGraph graph = impact.Graph;
            var identity = new ValidationSourceIdentity(
                graph.Identity.WorkspaceIdentity,
                graph.Identity.SourceRevision ?? graph.Identity.IndexGeneration,
                graph.Identity.IndexGeneration,
                project ?? graph.Identity.Project,
                repository ?? graph.Identity.Repository);
            ValidationCatalogEntry[] entries = catalog.Tests
                .Select(test => new ValidationCatalogEntry(
                    test.Id,
                    test.Recipe,
                    (test.Covers ?? [])
                        .Select(coverage => new ValidationCoverage(coverage.Kind, coverage.Name))
                        .ToArray(),
                    test.Tags,
                    test.ValidationClassification.ToString(),
                    Project: project,
                    CostRank: test.Cost switch
                    {
                        CatalogCost.Low => 1,
                        CatalogCost.Medium => 2,
                        CatalogCost.High => 3,
                        _ => 4
                    }))
                .ToArray();
            string[] fallbackTests = ResolveFallbackTests(catalog, fallbackSuite);
            var learning = new ImpactLearningService(
                new ImpactLearningStore(ImpactLearningStore.ResolveDefaultPath(rootPath)))
                .Applicable(identity);
            ValidationPlan plan = new MinimumSafeValidationPlanner().Build(
                new ValidationPlanRequest(
                    graph,
                    impact.Actual,
                    entries,
                    identity,
                    fallbackSuite,
                    fallbackTests,
                    learning,
                    priorEvidence,
                    AgentAdditionalTestIds: agentAdditionalTestIds,
                    GeneratedRequirements: generatedRequirements));
            return new ValidationPlanGenerationResult(plan);
        }
        catch (Exception exception)
        {
            return new ValidationPlanGenerationResult(
                null,
                "VALIDATION_PLAN_UNAVAILABLE",
                Bound(exception.Message));
        }
    }

    public static int LearnFromOutcome(
        ValidationPlan? plan,
        ImpactGraph? graph,
        RimTestSuiteResult result,
        string rootPath)
    {
        if (plan is null || graph is null || result.Failures is not { Count: > 0 })
        {
            return 0;
        }

        var changedIds = plan.ActualChangedNodeIds.ToHashSet(StringComparer.Ordinal);
        ImpactNode[] changedNodes = graph.Nodes
            .Where(node => changedIds.Contains(node.Id))
            .ToArray();
        var service = new ImpactLearningService(
            new ImpactLearningStore(ImpactLearningStore.ResolveDefaultPath(rootPath)));
        int learned = 0;
        foreach (RimTestSuiteFailure failure in result.Failures)
        {
            if (string.IsNullOrWhiteSpace(failure.DiagnosticId) &&
                string.IsNullOrWhiteSpace(failure.EvidenceId))
            {
                continue;
            }

            foreach (ImpactNode node in changedNodes)
            {
                string evidenceId = failure.EvidenceId ?? failure.DiagnosticId!;
                ImpactLearningResult learning = service.Observe(
                    new ImpactLearningObservation(
                        node.Identity,
                        failure.Test,
                        "validation-failure",
                        node.Kind == "harmony_patch"
                            ? ImpactClasses.DynamicPotential
                            : ImpactClasses.Direct,
                        new ImpactProvenance(
                            "rimerror",
                            ImpactEvidenceClasses.Learned,
                            evidenceId,
                            failure.DiagnosticId),
                        plan.SourceIdentity,
                        evidenceId,
                        CausalAttribution: !string.IsNullOrWhiteSpace(failure.DiagnosticId),
                        RimErrorAttribution: !string.IsNullOrWhiteSpace(failure.DiagnosticId),
                        Project: plan.SourceIdentity.Project,
                        GlobalCandidate: false));
                AgentImpactObservabilityRecorder.RecordLearning(
                    learning,
                    plan.SourceIdentity);
                if (learning.Learned)
                {
                    learned++;
                }
            }
        }

        return learned;
    }

    public static ValidationPlan ApplyAgentRequest(
        ValidationPlan plan,
        AgentValidationRequest request,
        CatalogDocument catalog)
    {
        ValidationPlan updated = new MinimumSafeValidationPlanner().ApplyAgentRequest(
            plan,
            request,
            catalog.Tests
                .Select(test => new ValidationCatalogEntry(
                    test.Id,
                    test.Recipe,
                    (test.Covers ?? [])
                        .Select(coverage => new ValidationCoverage(coverage.Kind, coverage.Name))
                        .ToArray(),
                    test.Tags,
                    test.ValidationClassification.ToString()))
                .ToArray());
        AgentImpactObservabilityRecorder.RecordAgentValidationChange(plan, updated, request);
        return updated;
    }

    private static string[] ResolveFallbackTests(CatalogDocument catalog, string? suiteId)
    {
        CatalogSuite? suite = suiteId is null
            ? null
            : catalog.Suites.FirstOrDefault(item => item.Id == suiteId);
        return suite?.Tests
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static string Bound(string value) =>
        value.Length <= 256 ? value : value[..256];
}
