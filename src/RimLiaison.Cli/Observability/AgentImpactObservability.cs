using RimContext.Core.Impact;
using RimContext.Core.Model;
using RimLiaison.Results;

namespace RimLiaison.Observability;

public static class AgentImpactObservabilitySchemas
{
    public const string Current = "rimliaison-agent-impact/v1";
}

public static class AgentImpactObservabilityIdentity
{
    public static string PacketId(ExecutionPacket packet) =>
        StableEntityId.DigestBase32(string.Join(
            "\0",
            packet.SchemaVersion,
            packet.Task,
            packet.Identity.WorkspaceIdentity,
            packet.Identity.SourceRevision,
            packet.Identity.IndexGeneration,
            packet.Metrics.SizeBytes));

    public static string PlanId(ValidationPlan plan) =>
        string.IsNullOrWhiteSpace(plan.PlanFingerprint)
            ? StableEntityId.DigestBase32(string.Join(
                "\0",
                plan.SchemaVersion,
                plan.SourceIdentity.WorkspaceIdentity,
                plan.SourceIdentity.SourceRevision,
                plan.Tier))
            : plan.PlanFingerprint;

    public static string RelationshipId(LearnedImpactRelationship relationship) =>
        StableEntityId.DigestBase32(string.Join(
            "\0",
            relationship.FromIdentity,
            relationship.ToIdentity,
            relationship.RelationshipKind,
            relationship.Project,
            relationship.FrameworkVersion,
            relationship.RimWorldVersion));
}

/// <summary>
/// Writes packet, impact, validation, and learning lifecycle events into the existing
/// observability store. Event data is bounded and all writes are non-fatal.
/// </summary>
public static class AgentImpactObservabilityRecorder
{
    public static AgentEvent? RecordPacketGenerated(
        ExecutionPacket packet,
        string task,
        string? project = null,
        string? repository = null) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.ExecutionPacketGenerated,
            "Execution Packet generated.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                packetId = AgentImpactObservabilityIdentity.PacketId(packet),
                task = AgentObservabilityData.BoundText(task, 256),
                taskIdentity = StableEntityId.DigestBase32(task.Trim().ToLowerInvariant()),
                project,
                repository,
                packetStatus = packet.Status,
                packetBytes = packet.Metrics.SizeBytes,
                packetGenerationMilliseconds = packet.Metrics.GenerationElapsedMilliseconds,
                indexedLookups = packet.Metrics.IndexedLookups,
                indexCacheHit = packet.Metrics.IndexCacheHit,
                sourceRevision = packet.Identity.SourceRevision,
                workspaceIdentity = packet.Identity.WorkspaceIdentity,
                indexGeneration = packet.Identity.IndexGeneration,
                topReferences = packet.TopFiles
                    .Take(16)
                    .Select(reference => reference.Value)
                    .ToArray(),
                likelyScope = packet.RelevantNodeIds.Take(32).ToArray(),
                knownConstraints = packet.KnownConstraints.Take(32).ToArray(),
                predictedValidation = packet.PredictedValidation.Take(32).ToArray(),
                expansionCount = packet.ExpandHandles.Count,
                agentInputTokens = packet.Metrics.AgentInputTokens
            });

    public static AgentEvent? RecordPacketBypassed(
        string task,
        string reason,
        string? project = null,
        string? repository = null) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.ExecutionPacketBypassed,
            "Execution Packet bypassed for a bounded task.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                task = AgentObservabilityData.BoundText(task, 256),
                taskIdentity = StableEntityId.DigestBase32(task.Trim().ToLowerInvariant()),
                reason = AgentObservabilityData.BoundIdentifier(reason, 128),
                project,
                repository
            });

    public static AgentEvent? RecordPacketExpanded(
        string packetId,
        string handle,
        string? sourceRevision,
        string? indexGeneration) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Research,
            AgentEventTypes.ExecutionPacketExpanded,
            "Execution Packet section expanded.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                packetId,
                handle = AgentObservabilityData.BoundIdentifier(handle, 256),
                sourceRevision,
                indexGeneration
            });

    public static AgentEvent? RecordPacketStatus(
        string packetId,
        string status,
        string reason,
        string? sourceRevision,
        string? indexGeneration,
        string? task = null,
        string? project = null) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            status == ExecutionPacketStatuses.PartiallyStale
                ? AgentEventTypes.ExecutionPacketPartiallyInvalidated
                : AgentEventTypes.ExecutionPacketInvalidated,
            "Execution Packet validity changed.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                packetId,
                task,
                taskIdentity = task is null
                    ? null
                    : StableEntityId.DigestBase32(task.Trim().ToLowerInvariant()),
                project,
                packetStatus = status,
                reason = AgentObservabilityData.BoundText(reason, 256),
                sourceRevision,
                indexGeneration
            });

    public static AgentEvent? RecordPredictedImpact(
        ExecutionPacket packet,
        PredictedImpact prediction) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.PredictedImpactCreated,
            "Predicted change impact created.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                packetId = AgentImpactObservabilityIdentity.PacketId(packet),
                task = packet.Task,
                taskIdentity = StableEntityId.DigestBase32(packet.Task.Trim().ToLowerInvariant()),
                project = packet.Project,
                sourceRevision = packet.Identity.SourceRevision,
                workspaceIdentity = packet.Identity.WorkspaceIdentity,
                indexGeneration = packet.Identity.IndexGeneration,
                predictedFiles = prediction.Files.Take(64).ToArray(),
                predictedNodeIds = prediction.NodeIds.Take(64).ToArray(),
                predictedImpactClasses = prediction.ImpactClasses.Take(32).ToArray(),
                predictedConcerns = prediction.ValidationConcerns.Take(32).ToArray(),
                basis = AgentObservabilityData.BoundText(prediction.Basis, 256),
                truncated = prediction.Truncated
            });

    public static AgentEvent? RecordActualImpact(
        ExecutionPacket packet,
        ActualImpact actual) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.ActualImpactCalculated,
            "Actual diff impact calculated.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                packetId = AgentImpactObservabilityIdentity.PacketId(packet),
                task = packet.Task,
                taskIdentity = StableEntityId.DigestBase32(packet.Task.Trim().ToLowerInvariant()),
                project = packet.Project,
                sourceRevision = packet.Identity.SourceRevision,
                workspaceIdentity = packet.Identity.WorkspaceIdentity,
                indexGeneration = packet.Identity.IndexGeneration,
                actualFiles = actual.ChangedFiles.Take(128).ToArray(),
                actualNodeIds = actual.ChangedNodeIds.Take(128).ToArray(),
                directDependents = actual.DirectDependents.Take(128).ToArray(),
                actualImpactClasses = actual.ImpactClasses.Take(32).ToArray(),
                actualConcerns = actual.ValidationConcerns.Take(32).ToArray(),
                harmonyOrDynamicRisk = actual.HarmonyOrDynamicRisk,
                serializationRisk = actual.SerializationRisk,
                scopeExpanded = actual.ScopeExpanded,
                expansionReasons = actual.ExpansionReasons.Take(32).ToArray()
            });

    public static AgentEvent? RecordValidationPlan(
        ValidationPlan plan,
        bool broadened = false) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            broadened
                ? AgentEventTypes.ValidationPlanBroadened
                : AgentEventTypes.ValidationPlanGenerated,
            broadened
                ? "Validation plan broadened from actual impact."
                : "Minimum-safe validation plan generated.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                planStatus = plan.Status,
                validationPlanTier = plan.Tier,
                workspaceIdentity = plan.SourceIdentity.WorkspaceIdentity,
                sourceRevision = plan.SourceIdentity.SourceRevision,
                indexGeneration = plan.SourceIdentity.IndexGeneration,
                project = plan.SourceIdentity.Project,
                repository = plan.SourceIdentity.Repository,
                actualFiles = plan.ActualChangedFiles.Take(128).ToArray(),
                actualNodeIds = plan.ActualChangedNodeIds.Take(128).ToArray(),
                impactClasses = plan.ImpactClasses.Take(32).ToArray(),
                validationConcerns = plan.ValidationConcerns.Take(32).ToArray(),
                expansionReasons = plan.ExpansionReasons.Take(32).ToArray(),
                requiredTestIds = plan.RequiredTestIds.Take(64).ToArray(),
                requiredRecipeIds = plan.Required
                    .Where(requirement => requirement.RecipeId is not null)
                    .Select(requirement => requirement.RecipeId!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToArray(),
                requiredItems = plan.Required
                    .Take(64)
                    .Select(FormatRequirement)
                    .ToArray(),
                additionalTestIds = plan.Additional
                    .Where(requirement => requirement.TestId is not null)
                    .Select(requirement => requirement.TestId!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToArray(),
                additionalRecipeIds = plan.Additional
                    .Where(requirement => requirement.RecipeId is not null)
                    .Select(requirement => requirement.RecipeId!)
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToArray(),
                additionalItems = plan.Additional
                    .Take(64)
                    .Select(FormatRequirement)
                    .ToArray(),
                deduplicatedRequirements = plan.Metrics.DeduplicatedRequirements,
                planningElapsedMilliseconds = plan.Metrics.PlanningElapsedMilliseconds,
                learnedRelationshipsConsidered = plan.Metrics.LearnedRelationshipsConsidered,
                predictionTier = plan.PredictionTier,
                scopeExpanded = plan.ScopeExpanded
            });

    public static AgentEvent? RecordValidationStarted(
        ValidationPlan plan,
        IReadOnlyList<string> testIds,
        string? validationRunId = null,
        IReadOnlyList<string>? recipeIds = null) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationStarted,
            "Canonical validation started.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                validationRunId,
                validationTestIds = testIds.Take(64).ToArray(),
                validationRecipeIds = (recipeIds ?? []).Take(64).ToArray(),
                validationPlanTier = plan.Tier,
                project = plan.SourceIdentity.Project,
                sourceRevision = plan.SourceIdentity.SourceRevision,
                indexGeneration = plan.SourceIdentity.IndexGeneration
            });

    public static AgentEvent? RecordValidationCompleted(
        ValidationPlan plan,
        RimTestSuiteResult result,
        IReadOnlyList<string> testIds,
        IReadOnlyList<string>? recipeIds = null) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationCompleted,
            "Canonical validation completed.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                validationTestIds = testIds.Take(64).ToArray(),
                validationRecipeIds = (recipeIds ?? []).Take(64).ToArray(),
                project = plan.SourceIdentity.Project,
                sourceRevision = plan.SourceIdentity.SourceRevision,
                indexGeneration = plan.SourceIdentity.IndexGeneration,
                validationStatus = result.Status,
                validationElapsedMilliseconds = result.DurationMs,
                passed = result.Passed,
                failed = result.Failed,
                blocked = result.Blocked,
                unavailable = result.Unavailable,
                failureTests = (result.Failures ?? [])
                    .Take(64)
                    .Select(failure => failure.Test)
                    .ToArray(),
                evidenceIds = (result.Failures ?? [])
                    .Select(failure => failure.EvidenceId ?? failure.DiagnosticId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(64)
                    .ToArray()
            });

    public static AgentEvent? RecordStaleEvidenceRejected(
        ValidationSourceIdentity expected,
        ValidationSourceIdentity actual,
        string reason) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.StaleEvidenceRejected,
            "Stale validation evidence rejected.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                expectedSourceRevision = expected.SourceRevision,
                expectedIndexGeneration = expected.IndexGeneration,
                actualSourceRevision = actual.SourceRevision,
                actualIndexGeneration = actual.IndexGeneration,
                project = expected.Project,
                reason = AgentObservabilityData.BoundText(reason, 256)
            });

    public static AgentEvent? RecordAgentValidationChange(
        ValidationPlan before,
        ValidationPlan after,
        AgentValidationRequest request)
    {
        string[] removedIds = (request.RemoveRequirementIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool removalRequested = removedIds.Length > 0;
        bool removalRejected = removedIds.Any(
            requirementId => after.Required.Any(
                requirement => string.Equals(requirement.RequirementId, requirementId, StringComparison.Ordinal)));
        bool removalAccepted = removalRequested && !removalRejected;
        string eventType = removalRejected
            ? AgentEventTypes.ValidationReductionRejected
            : AgentEventTypes.AgentValidationAdded;

        return AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            eventType,
            removalRejected
                ? "Planner rejected a prohibited validation reduction."
                : removalAccepted
                    ? "Agent validation reduction accepted under planner policy."
                    : "Agent-added validation recorded.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(after),
                addedTestIds = (request.AdditionalTestIds ?? []).Take(64).ToArray(),
                removedRequirementIds = removedIds.Take(64).ToArray(),
                acceptedOverrides = after.Overrides
                    .Where(value => value.Accepted)
                    .Select(value => value.RequirementId)
                    .Take(64)
                    .ToArray(),
                requiredCountBefore = before.Required.Count,
                requiredCountAfter = after.Required.Count,
                removalAccepted
            });
    }

    public static AgentEvent? RecordLearning(
        ImpactLearningResult result,
        ValidationSourceIdentity sourceIdentity) =>
        result.Relationship is null
            ? null
            : AgentObservabilityRuntime.Record(
                DevelopmentStage.Testing,
                result.PromotedGlobal
                    ? AgentEventTypes.ImpactRelationshipPromoted
                    : AgentEventTypes.ImpactRelationshipLearned,
                result.PromotedGlobal
                    ? "Impact relationship promoted globally."
                    : "Impact relationship learned for the project.",
                new
                {
                    schemaVersion = AgentImpactObservabilitySchemas.Current,
                    relationshipId = AgentImpactObservabilityIdentity.RelationshipId(result.Relationship),
                    fromIdentity = result.Relationship.FromIdentity,
                    toIdentity = result.Relationship.ToIdentity,
                    relationshipKind = result.Relationship.RelationshipKind,
                    impactClass = result.Relationship.ImpactClass,
                    scope = result.Relationship.Scope,
                    project = result.Relationship.Project,
                    sourceRevision = sourceIdentity.SourceRevision,
                    indexGeneration = sourceIdentity.IndexGeneration,
                    evidenceIds = result.Relationship.EvidenceIds?.Take(64).ToArray() ?? [],
                    independentObservations = result.Relationship.IndependentObservations,
                    supportCount = result.Relationship.SupportCount,
                    promotedGlobal = result.PromotedGlobal
                });

    public static AgentEvent? RecordProjectOverride(
        ImpactLearningOverride learningOverride) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            learningOverride.Excluded
                ? AgentEventTypes.ImpactRelationshipInvalidated
                : AgentEventTypes.ImpactProjectOverrideApplied,
            learningOverride.Excluded
                ? "Impact relationship invalidated by project override."
                : "Impact relationship project override applied.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                relationshipId = StableEntityId.DigestBase32(string.Join(
                    "\0",
                    learningOverride.FromIdentity,
                    learningOverride.ToIdentity,
                    learningOverride.RelationshipKind,
                    learningOverride.Project)),
                fromIdentity = learningOverride.FromIdentity,
                toIdentity = learningOverride.ToIdentity,
                relationshipKind = learningOverride.RelationshipKind,
                project = learningOverride.Project,
                evidenceId = learningOverride.EvidenceId,
                reason = AgentObservabilityData.BoundText(learningOverride.Reason, 256),
                excluded = learningOverride.Excluded
            });

    public static AgentEvent? RecordEvidenceReused(
        ValidationPlan plan,
        IReadOnlyList<string> testIds,
        IReadOnlyList<string> evidenceIds) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.EvidenceReused,
            "Current validation evidence was reused.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                testIds = testIds.Take(64).ToArray(),
                evidenceIds = evidenceIds.Take(64).ToArray(),
                sourceRevision = plan.SourceIdentity.SourceRevision,
                indexGeneration = plan.SourceIdentity.IndexGeneration
            });

    public static AgentEvent? RecordRuntimeEscalation(
        ValidationPlan plan,
        IReadOnlyList<global::RimDev.Contracts.RuntimeValidationRequest> requests) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RuntimeEscalationRequested,
            "Runtime evidence was required by the validation plan.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                requestCount = requests.Count,
                requests = requests.Take(16).Select(request => new
                {
                    request.Reason,
                    subject = request.Subject.Id,
                    request.Assertion,
                    request.RequiredEvidence,
                    request.ExcludedWork
                }).ToArray(),
                sourceRevision = plan.SourceIdentity.SourceRevision,
                indexGeneration = plan.SourceIdentity.IndexGeneration
            });

    public static AgentEvent? RecordFailurePacket(
        global::RimDev.Contracts.FailureEvidencePacket packet)
    {
        global::RimDev.Contracts.FailureEvidencePacket normalized = packet.Normalize();
        return AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.FailureDetected,
            "Structured validation failure packet recorded.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                classification = normalized.Classification,
                error = AgentObservabilityData.BoundText(normalized.Error, 512),
                failedValidation = normalized.FailedValidation?.Id,
                changedSourceFiles = normalized.ChangedSourceFiles.Take(32).ToArray(),
                affectedEntities = normalized.AffectedEntities.Take(32).Select(entity => entity.Id).ToArray(),
                evidenceReferences = normalized.PrecedingEvidence
                    .Concat(normalized.References)
                    .Take(16)
                    .Select(reference => reference.Uri)
                    .ToArray()
            });
    }

    public static AgentEvent? RecordRuntimeEvidenceCompleted(
        ValidationPlan plan,
        RimTestSuiteResult result) =>
        plan.RuntimeRequests is not { Count: > 0 } requests
            ? null
            : AgentObservabilityRuntime.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.RuntimeEvidenceCompleted,
                "Runtime evidence collection completed.",
                new
                {
                    schemaVersion = AgentImpactObservabilitySchemas.Current,
                    validationPlanId = AgentImpactObservabilityIdentity.PlanId(plan),
                    requestCount = requests.Count,
                    validationStatus = result.Status,
                    evidenceIds = (result.Failures ?? [])
                        .Select(failure => failure.EvidenceId ?? failure.DiagnosticId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Take(64)
                        .ToArray()
                });

    public static AgentEvent? RecordDiagnosis(
        global::RimDev.Contracts.FailureDiagnosis diagnosis) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.DiagnosisProduced,
            "RimError produced a structured diagnosis.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                classification = diagnosis.Packet.Classification,
                likelyRootCause = AgentObservabilityData.BoundText(diagnosis.LikelyRootCause, 512),
                confidence = diagnosis.Confidence,
                evidenceReferences = diagnosis.RelevantEvidence.Take(16).Select(reference => reference.Uri).ToArray(),
                additionalRequirements = diagnosis.AdditionalRequirements.Take(16).Select(requirement => requirement.RequirementId).ToArray()
            });

    public static AgentEvent? RecordRemediationPrecedent(
        global::RimDev.Contracts.RemediationPrecedent precedent,
        bool reused) =>
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            reused ? AgentEventTypes.RemediationPrecedentReused : AgentEventTypes.RemediationPrecedentStored,
            reused
                ? "A validated remediation precedent was reused."
                : "A validated remediation precedent was stored.",
            new
            {
                schemaVersion = AgentImpactObservabilitySchemas.Current,
                precedentId = precedent.PrecedentId,
                failureFamily = precedent.FailureFamily,
                subject = precedent.Subject?.Id,
                status = precedent.Status,
                supportCount = precedent.SupportCount,
                evidenceReferences = precedent.Evidence.Take(16).Select(reference => reference.Uri).ToArray(),
                sourceRevision = precedent.SuccessfulValidationIdentity.SourceRevision,
                deploymentIdentity = precedent.SuccessfulValidationIdentity.DeploymentIdentity,
                processGeneration = precedent.SuccessfulValidationIdentity.ProcessGeneration
            });

    private static string FormatRequirement(ValidationRequirement requirement) =>
        string.Join(
            " · ",
            requirement.TestId ?? "unavailable-test",
            requirement.Kind,
            requirement.Tier,
            AgentObservabilityData.BoundText(requirement.Reason, 180),
            requirement.EvidenceIds.Count == 0
                ? "declared"
                : "evidence=" + string.Join(",", requirement.EvidenceIds.Take(4)));
}
