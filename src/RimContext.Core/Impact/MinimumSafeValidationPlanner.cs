using System.Diagnostics;

namespace RimContext.Core.Impact;

public sealed class MinimumSafeValidationPlanner
{
    public ValidationPlan Build(ValidationPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        ImpactGraph graph = request.Graph;
        ActualImpact actual = request.Actual;
        ValidationCatalogEntry[] catalog = request.Catalog
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TestId) && !string.IsNullOrWhiteSpace(entry.RecipeId))
            .OrderBy(entry => entry.TestId, StringComparer.Ordinal)
            .ToArray();
        var actualIds = actual.ChangedNodeIds.ToHashSet(StringComparer.Ordinal);
        var dependentIds = actual.DirectDependents.ToHashSet(StringComparer.Ordinal);
        ImpactNode[] changedNodes = graph.Nodes
            .Where(node => actualIds.Contains(node.Id))
            .ToArray();
        ImpactNode[] dependentNodes = graph.Nodes
            .Where(node => dependentIds.Contains(node.Id))
            .ToArray();
        string[] changedNames = changedNodes
            .SelectMany(node => new[] { node.Identity, node.DisplayName })
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        var learned = (request.LearnedRelationships ?? [])
            .Where(relationship => AppliesTo(relationship, request.SourceIdentity))
            .ToArray();
        var required = new List<ValidationRequirement>();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (ValidationCatalogEntry entry in catalog)
        {
            bool directCoverage = HasCoverage(entry, changedNodes);
            bool dependentCoverage = !directCoverage && HasCoverage(entry, dependentNodes);
            bool learnedCoverage = !directCoverage && !dependentCoverage && learned.Any(relationship =>
                string.Equals(relationship.ToIdentity, entry.TestId, StringComparison.Ordinal) &&
                changedNames.Contains(relationship.FromIdentity, StringComparer.Ordinal));
            if (directCoverage || dependentCoverage || learnedCoverage)
            {
                AddRequirement(
                    required,
                    addedKeys,
                    entry,
                    RequirementKindFor(entry, actual),
                    TierFor(actual, directCoverage, dependentCoverage, learnedCoverage),
                    directCoverage
                        ? "catalog coverage matches an actual changed component"
                        : dependentCoverage
                            ? "catalog coverage matches an actual direct dependent"
                            : "learned validation relationship is applicable",
                    directCoverage || dependentCoverage
                        ? ImpactEvidenceClasses.Explicit
                        : ImpactEvidenceClasses.Learned,
                    learned.Where(item => string.Equals(item.ToIdentity, entry.TestId, StringComparison.Ordinal))
                        .SelectMany(item => item.EvidenceIds ?? [])
                        .ToArray());
            }
        }

        if (actual.HarmonyOrDynamicRisk)
        {
            AddTagged(
                required,
                addedKeys,
                catalog,
                entry => entry.HasTag("runtime") || entry.HasTag("quicktest") || entry.HasTag("ui") ||
                    entry.RecipeId.Contains("quicktest", StringComparison.OrdinalIgnoreCase),
                ValidationRequirementKinds.Runtime,
                "Harmony or dynamic relationship requires runtime-sensitive coverage",
                ValidationPlanTiers.AffectedProject,
                ImpactClasses.DynamicPotential);
        }

        if (actual.SerializationRisk)
        {
            AddTagged(
                required,
                addedKeys,
                catalog,
                entry => entry.HasTag("save-load") || entry.HasTag("serialization") ||
                    entry.HasTag("runtime") || entry.RecipeId.Contains("save", StringComparison.OrdinalIgnoreCase),
                ValidationRequirementKinds.Serialization,
                "serialization/save-load concern requires compatibility coverage",
                ValidationPlanTiers.AffectedProject,
                ImpactClasses.Unknown);
        }

        bool frameworkRisk = actual.ImpactClasses.Contains(ImpactClasses.Framework, StringComparer.Ordinal) ||
            actual.ValidationConcerns.Any(value => value.Contains("framework", StringComparison.OrdinalIgnoreCase));
        if (frameworkRisk)
        {
            AddTagged(
                required,
                addedKeys,
                catalog,
                entry => entry.HasTag("framework") || entry.HasTag("consumer") || entry.HasTag("integration"),
                ValidationRequirementKinds.FrameworkContract,
                "framework-shared impact requires consumer-aware validation",
                ValidationPlanTiers.AffectedFramework,
                ImpactClasses.Framework);
        }
        RimDev.Contracts.ValidationRequirement[] generatedRequirements =
            (request.GeneratedRequirements ?? [])
                .Where(static requirement => requirement is not null)
                .Select(static requirement => requirement.Normalize())
                .Take(16)
                .ToArray();
        foreach (RimDev.Contracts.ValidationRequirement generated in generatedRequirements)
        {
            ValidationCatalogEntry? candidate = catalog
                .Where(entry => generated.RuntimeRequired
                    ? entry.HasTag("runtime") ||
                        entry.HasTag("quicktest") ||
                        entry.RecipeId.Contains("quicktest", StringComparison.OrdinalIgnoreCase)
                    : entry.Coverage.Any(coverage =>
                        string.Equals(coverage.Name, generated.Subject.Id, StringComparison.Ordinal)))
                .OrderBy(entry => entry.CostRank == 0 ? int.MaxValue : entry.CostRank)
                .ThenBy(entry => entry.TestId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is null)
            {
                required.Add(new ValidationRequirement(
                    generated.RequirementId,
                    generated.RuntimeRequired
                        ? ValidationRequirementKinds.Runtime
                        : ValidationRequirementKinds.StaticReference,
                    ValidationPlanTiers.BroaderCanonical,
                    generated.Assertion,
                    generated.Severity ?? "REQUIRED",
                    null,
                    null,
                    ["generated-requirement"],
                    [],
                    Available: false,
                    Source: generated.Producer));
                continue;
            }

            AddRequirement(
                required,
                addedKeys,
                candidate,
                generated.RuntimeRequired
                    ? ValidationRequirementKinds.Runtime
                    : RequirementKindFor(candidate, actual),
                generated.RuntimeRequired
                    ? ValidationPlanTiers.AffectedProject
                    : ValidationPlanTiers.NarrowTargeted,
                generated.Assertion,
                ImpactEvidenceClasses.Explicit,
                []);
        }

        bool unknownRisk = actual.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal) ||
            actual.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal) &&
            required.Count == 0;
        if (unknownRisk)
        {
            string[] fallback = (request.FallbackTestIds ?? catalog.Select(entry => entry.TestId).ToArray())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (string testId in fallback)
            {
                ValidationCatalogEntry? entry = catalog.FirstOrDefault(item =>
                    string.Equals(item.TestId, testId, StringComparison.Ordinal));
                if (entry is null)
                {
                    continue;
                }

                AddRequirement(
                    required,
                    addedKeys,
                    entry,
                    ValidationRequirementKinds.BroaderFallback,
                    ValidationPlanTiers.BroaderCanonical,
                    "unknown or dynamic graph coverage requires conservative fallback",
                    ImpactEvidenceClasses.Uncertain,
                    []);
            }
        }

        if (required.Count == 0)
        {
            IReadOnlyList<string> fallback = request.FallbackTestIds ?? [];
            foreach (string testId in fallback)
            {
                ValidationCatalogEntry? entry = catalog.FirstOrDefault(item =>
                    string.Equals(item.TestId, testId, StringComparison.Ordinal));
                if (entry is not null)
                {
                    AddRequirement(
                        required,
                        addedKeys,
                        entry,
                        ValidationRequirementKinds.BroaderFallback,
                        ValidationPlanTiers.AffectedComponent,
                        "no exact catalog mapping; fallback coverage is required",
                        ImpactEvidenceClasses.Uncertain,
                        []);
                }
            }
        }

        var additional = new List<ValidationRequirement>();
        foreach (string testId in request.AgentAdditionalTestIds ?? [])
        {
            ValidationCatalogEntry? entry = catalog.FirstOrDefault(item =>
                string.Equals(item.TestId, testId, StringComparison.Ordinal));
            if (entry is null || addedKeys.Contains(TestKey(entry.TestId, entry.RecipeId)))
            {
                continue;
            }

            additional.Add(CreateRequirement(
                entry,
                RequirementKindFor(entry, actual),
                ValidationPlanTiers.NarrowTargeted,
                "agent-requested additional validation",
                ImpactEvidenceClasses.Explicit,
                [],
                agentRequested: true));
        }

        string tier = TierForPlan(actual, required);
        if (required.Count == 0 && actual.ChangedFiles.Count > 0)
        {
            required.Add(new ValidationRequirement(
                "unmapped-impact",
                ValidationRequirementKinds.BroaderFallback,
                ValidationPlanTiers.BroaderCanonical,
                "actual impact has no safe catalog mapping",
                "REQUIRED",
                null,
                null,
                [ImpactClasses.Unknown],
                [],
                Available: false,
                Source: "impact-planner"));
            tier = ValidationPlanTiers.BroaderCanonical;
        }

        string[] expansionReasons = actual.ExpansionReasons
            .Concat(actual.ScopeExpanded ? ["actual impact exceeded predicted scope"] : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var plan = new ValidationPlan(
            ValidationPlanSchemas.Current,
            required.Any(requirement => !requirement.Available) ? "incomplete" : "ready",
            tier,
            request.SourceIdentity,
            required
                .OrderBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
                .ToArray(),
            additional
                .OrderBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
                .ToArray(),
            [],
            actual.ChangedFiles,
            actual.ChangedNodeIds,
            actual.ImpactClasses,
            actual.ValidationConcerns,
            expansionReasons,
            actual.ScopeExpanded,
            actual.Prediction is null ? null : TierForPrediction(actual.Prediction),
            "pending",
            new ValidationPlanMetrics(
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                changedNodes.Length,
                graph.Edges.Count,
                catalog.Length,
                learned.Length,
                Math.Max(0, required.Count + additional.Count - addedKeys.Count)));
        plan = plan with
        {
            GeneratedRequirements = generatedRequirements.Length == 0 ? null : generatedRequirements,
            RuntimeRequests = BuildRuntimeRequests(generatedRequirements, plan.Required)
        };
        string planFingerprint = ValidationPlanJson.Fingerprint(plan);
        IReadOnlyList<ValidationRequirement> reusedRequired = ReuseCurrentEvidence(
            plan.Required,
            request.PriorEvidence,
            request.SourceIdentity,
            planFingerprint);
        return plan with
        {
            Required = reusedRequired,
            PlanFingerprint = planFingerprint
        };
    }

    public ValidationPlan ApplyAgentRequest(
        ValidationPlan plan,
        AgentValidationRequest request,
        IReadOnlyList<ValidationCatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        var overrides = new List<ValidationPlanOverride>(plan.Overrides);
        foreach (ValidationPlanOverride overrideRequest in request.Overrides ?? [])
        {
            bool accepted = overrideRequest.Accepted &&
                string.Equals(overrideRequest.SourceIdentity.WorkspaceIdentity, plan.SourceIdentity.WorkspaceIdentity, StringComparison.Ordinal) &&
                string.Equals(overrideRequest.SourceIdentity.SourceRevision, plan.SourceIdentity.SourceRevision, StringComparison.Ordinal) &&
                plan.Required.Any(requirement => requirement.RequirementId == overrideRequest.RequirementId);
            overrides.Add(overrideRequest with { Accepted = accepted });
        }

        var required = plan.Required.ToList();
        foreach (string requirementId in request.RemoveRequirementIds ?? [])
        {
            ValidationPlanOverride? proof = overrides.LastOrDefault(item =>

                item.RequirementId == requirementId && item.Accepted && item.Action == "remove");
            if (proof is not null)
            {
                required.RemoveAll(requirement => requirement.RequirementId == requirementId);
            }
            else
            {
                overrides.Add(new ValidationPlanOverride(
                    requirementId,
                    "remove",
                    "agent-request-without-authoritative-proof",
                    plan.SourceIdentity,
                    "required validation cannot be removed by ordinary agent choice",
                    false));
            }
        }

        var additional = plan.Additional.ToList();
        var knownRecipes = required.Concat(additional)
            .Select(requirement => requirement.RecipeId)
            .Where(value => value is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (string testId in request.AdditionalTestIds ?? [])
        {
            ValidationCatalogEntry? entry = catalog.FirstOrDefault(item => item.TestId == testId);
            if (entry is null || !knownRecipes.Add(entry.RecipeId))
            {
                continue;
            }

            additional.Add(CreateRequirement(
                entry,
                RequirementKindFor(entry, null),
                ValidationPlanTiers.NarrowTargeted,
                "agent-requested additional validation",
                ImpactEvidenceClasses.Explicit,
                [],
                true));
        }
        ValidationPlan updated = plan with
        {
            Required = required,
            Additional = additional,
            Overrides = overrides,
            RuntimeRequests = BuildRuntimeRequests(plan.GeneratedRequirements ?? [], required)
        };
        return updated with { PlanFingerprint = ValidationPlanJson.Fingerprint(updated) };
    }
    private static IReadOnlyList<RimDev.Contracts.RuntimeValidationRequest> BuildRuntimeRequests(
        IReadOnlyList<RimDev.Contracts.ValidationRequirement> generated,
        IReadOnlyList<ValidationRequirement> selected)
    {
        var requests = new List<RimDev.Contracts.RuntimeValidationRequest>();
        foreach (RimDev.Contracts.ValidationRequirement requirement in generated
                     .Where(static requirement => requirement.RuntimeRequired))
        {
            requests.Add(RimDev.Contracts.RuntimeValidationRequest.FromRequirement(
                requirement,
                "generated validation requirement requires runtime evidence"));
        }
        foreach (ValidationRequirement requirement in selected.Where(static requirement =>
                     requirement.Kind == ValidationRequirementKinds.Runtime))
        {
            requests.Add(RimDev.Contracts.RuntimeValidationRequest.FromRequirement(
                new RimDev.Contracts.ValidationRequirement
                {
                    RequirementId = requirement.RequirementId,
                    Subject = new RimDev.Contracts.EntityReference
                    {
                        Kind = RimDev.Contracts.EntityReferenceKinds.Test,
                        Id = requirement.TestId ?? requirement.RequirementId
                    },
                    Assertion = requirement.Reason,
                    RuntimeRequired = true,
                    Producer = "RimLiaison",
                    Source = requirement.Source
                },
                "selected validation coverage requires runtime evidence"));
        }
        return requests
            .GroupBy(request => request.Subject.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.Subject.Id, StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }
    private static IReadOnlyList<ValidationRequirement> ReuseCurrentEvidence(
        IReadOnlyList<ValidationRequirement> requirements,
        IReadOnlyList<ValidationOutcomeEvidence>? priorEvidence,
        ValidationSourceIdentity sourceIdentity,
        string planFingerprint)
    {
        if (priorEvidence is null || priorEvidence.Count == 0)
        {
            return requirements;
        }

        return requirements
            .Select(requirement =>
            {
                string? testId = requirement.TestId;
                string[] reusable = testId is null
                    ? []
                    : priorEvidence
                        .Where(evidence => ValidationEvidenceGate.IsCurrent(
                            evidence,
                            sourceIdentity,
                            planFingerprint))
                        .Where(evidence => evidence.TestIds.Contains(testId, StringComparer.Ordinal))
                        .Select(evidence => evidence.EvidenceId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .Take(8)
                        .ToArray();
                return reusable.Length == 0
                    ? requirement
                    : requirement with
                    {
                        EvidenceIds = requirement.EvidenceIds
                            .Concat(reusable)
                            .Distinct(StringComparer.Ordinal)
                            .Take(8)
                            .ToArray(),
                        ReusedEvidenceIds = reusable
                    };
            })
            .ToArray();
    }

    private static void AddTagged(
        ICollection<ValidationRequirement> requirements,
        ISet<string> keys,
        IReadOnlyList<ValidationCatalogEntry> catalog,
        Func<ValidationCatalogEntry, bool> predicate,
        string kind,
        string reason,
        string tier,
        string evidenceClass)
    {
        foreach (ValidationCatalogEntry entry in catalog.Where(predicate))
        {
            AddRequirement(
                requirements,
                keys,
                entry,
                kind,
                tier,
                reason,
                evidenceClass,
                []);
        }
    }

    private static void AddRequirement(
        ICollection<ValidationRequirement> requirements,
        ISet<string> keys,
        ValidationCatalogEntry entry,
        string kind,
        string tier,
        string reason,
        string evidenceClass,
        IReadOnlyList<string> evidenceIds)
    {
        string key = TestKey(entry.TestId, entry.RecipeId);
        if (!keys.Add(key))
        {
            return;
        }

        requirements.Add(CreateRequirement(
            entry,
            kind,
            tier,
            reason,
            evidenceClass,
            evidenceIds,
            false));
    }

    private static ValidationRequirement CreateRequirement(
        ValidationCatalogEntry entry,
        string kind,
        string tier,
        string reason,
        string evidenceClass,
        IReadOnlyList<string> evidenceIds,
        bool agentRequested)
    {
        return new ValidationRequirement(
            entry.TestId + "@" + entry.RecipeId,
            kind,
            tier,
            reason,
            entry.Classification,
            entry.TestId,
            entry.RecipeId,
            [evidenceClass],
            evidenceIds,
            true,
            agentRequested,
            "validation-catalog");
    }

    private static string RequirementKindFor(ValidationCatalogEntry entry, ActualImpact? actual)
    {
        if (entry.HasTag("save-load") || entry.HasTag("serialization"))
        {
            return ValidationRequirementKinds.Serialization;
        }
        if (entry.HasTag("runtime") || entry.HasTag("quicktest") || entry.RecipeId.Contains("quicktest", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationRequirementKinds.Runtime;
        }
        if (entry.HasTag("framework") || entry.HasTag("consumer"))
        {
            return ValidationRequirementKinds.FrameworkContract;
        }
        if (entry.HasTag("integration"))
        {
            return ValidationRequirementKinds.Integration;
        }
        if (actual is not null && actual.ChangedFiles.Any(file => file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationRequirementKinds.StaticReference;
        }
        return ValidationRequirementKinds.TargetedTest;
    }

    private static bool HasCoverage(
        ValidationCatalogEntry entry,
        IReadOnlyList<ImpactNode> nodes) =>
        entry.Coverage.Any(coverage =>
            nodes.Any(node =>
                string.Equals(coverage.Kind, node.Kind, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(coverage.Name, node.Identity, StringComparison.Ordinal) ||
                    string.Equals(coverage.Name, node.DisplayName, StringComparison.Ordinal))));

    private static string TierFor(
        ActualImpact actual,
        bool direct,
        bool dependent,
        bool learned) =>
        actual.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal)
            ? ValidationPlanTiers.BroaderCanonical
            : actual.HarmonyOrDynamicRisk || actual.SerializationRisk
                ? ValidationPlanTiers.AffectedProject
                : direct
                    ? ValidationPlanTiers.NarrowTargeted
                    : dependent || learned
                        ? ValidationPlanTiers.AffectedComponent
                        : ValidationPlanTiers.AffectedComponent;

    private static string TierForPlan(ActualImpact actual, IReadOnlyList<ValidationRequirement> requirements) =>
        actual.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal)
            ? ValidationPlanTiers.BroaderCanonical
            : actual.ImpactClasses.Contains(ImpactClasses.Framework, StringComparer.Ordinal)
                ? ValidationPlanTiers.AffectedFramework
                : actual.HarmonyOrDynamicRisk || actual.SerializationRisk
                    ? ValidationPlanTiers.AffectedProject
                    : requirements.Count > 0
                        ? requirements.Min(requirement => TierRank(requirement.Tier)) switch
                        {
                            0 => ValidationPlanTiers.NarrowTargeted,
                            _ => ValidationPlanTiers.AffectedComponent
                        }
                        : ValidationPlanTiers.BroaderCanonical;

    private static string TierForPrediction(PredictedImpact prediction) =>
        prediction.ImpactClasses.Contains(ImpactClasses.Unknown, StringComparer.Ordinal)
            ? ValidationPlanTiers.BroaderCanonical
            : prediction.ImpactClasses.Contains(ImpactClasses.DynamicPotential, StringComparer.Ordinal)
                ? ValidationPlanTiers.AffectedProject
                : ValidationPlanTiers.NarrowTargeted;

    private static int TierRank(string tier) => tier switch
    {
        ValidationPlanTiers.NarrowTargeted => 0,
        ValidationPlanTiers.AffectedComponent => 1,
        ValidationPlanTiers.AffectedProject => 2,
        ValidationPlanTiers.AffectedFramework => 3,
        _ => 4
    };

    private static bool AppliesTo(
        LearnedImpactRelationship relationship,
        ValidationSourceIdentity identity)
    {
        if (relationship.Scope == "project" &&
            !string.Equals(relationship.Project, identity.Project, StringComparison.Ordinal))
        {
            return false;
        }
        if (relationship.FrameworkVersion is not null &&
            relationship.FrameworkVersion != identity.FrameworkVersion)
        {
            return false;
        }
        if (relationship.RimWorldVersion is not null &&
            relationship.RimWorldVersion != identity.RimWorldVersion)
        {
            return false;
        }

        return relationship.Status is "tentative" or "proven";
    }

    private static string TestKey(string testId, string recipeId) => recipeId;
}
