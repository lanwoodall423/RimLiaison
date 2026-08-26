using RimContext.Core.Content;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Results;

namespace RimLiaison;

internal sealed class ContentIntelligenceCapture
{
    private readonly ContentIntelligenceService service;
    private readonly ContentBlueprint blueprint;

    private ContentIntelligenceCapture(
        ContentIntelligenceService service,
        ContentBlueprint blueprint)
    {
        this.service = service;
        this.blueprint = blueprint;
    }

    public static async Task<ContentIntelligenceCapture?> TryCreateAsync(
        CliRequest request,
        IReadOnlyList<string> changedPaths,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ContentKind) &&
            string.IsNullOrWhiteSpace(request.ContentRole))
        {
            return null;
        }

        string root = request.RimContextRootPath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT") ??
            Environment.CurrentDirectory;
        string? commit = null;
        try
        {
            GitRepositoryStateResult git = await new SystemGitRepositoryStateProvider()
                .ReadAsync(root, cancellationToken)
                .ConfigureAwait(false);
            commit = git.State?.HeadSha;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Git identity remains explicitly unknown when the owner cannot resolve it.
        }
        string storePath = ContentIntelligenceStorage.ResolveDefaultPath(root);
        var service = new ContentIntelligenceService(new ContentIntelligenceStore(storePath));
        AgentObservabilitySession? session = AgentObservabilityRuntime.Current;
        ContentBlueprint blueprint = service.CaptureBlueprint(
            new ContentBlueprintCaptureRequest(
                root,
                request.RimContextStorePath,
                new ContentBlueprintIntent(
                    request.ContentKind,
                    request.ContentRole,
                    ReuseSource: request.ContentReuseSource),
                changedPaths,
                Repository: null,
                Project: request.DevBridgeProject ?? request.StackManifest.Manifest?.Project,
                AgentId: session?.AgentId,
                SessionId: session?.SessionId,
                RunId: session?.RunId ?? workflowId,
                Commit: commit,
                LogicalAgentId: session?.LogicalAgentId));
        Emit(
            DevelopmentStage.Analysis,
            ContentObservabilityEventTypes.BlueprintCreated,
            "Content blueprint created.",
            Data(blueprint, state: "created", reason: "semantic intent captured before validation"));
        ContentReuseDecision? reuse = blueprint.ReuseDecision;
        if (reuse is not null)
        {
            Emit(
                DevelopmentStage.Research,
                ContentObservabilityEventTypes.ReuseSelected,
                "Content reuse source selected.",
                Data(
                    blueprint,
                    state: "selected",
                    reuseSource: reuse.Source,
                    precedentId: reuse.Source == ContentReuseSources.Precedent
                        ? reuse.ReferenceIds?.FirstOrDefault()
                        : null,
                    archetypeId: reuse.Source == ContentReuseSources.RimContent
                        ? reuse.ReferenceIds?.FirstOrDefault()
                        : null,
                    reason: reuse.Reason,
                    referenceIds: reuse.ReferenceIds));
            if (reuse.Source == ContentReuseSources.Precedent)
            {
                Emit(
                    DevelopmentStage.Research,
                    ContentObservabilityEventTypes.PrecedentDetected,
                    "A proven precedent was selected for content reuse.",
                    Data(
                        blueprint,
                        state: "detected",
                        precedentId: reuse.ReferenceIds?.FirstOrDefault(),
                        reuseSource: reuse.Source,
                        reason: reuse.Reason,
                        referenceIds: reuse.ReferenceIds));
            }
        }
        return new ContentIntelligenceCapture(service, blueprint);
    }

    public void RecordEvidence(RimTestSuiteResult result)
    {
        ContentSourceIdentity source = blueprint.Metadata.SourceIdentity ?? new ContentSourceIdentity();
        var outcome = new ContentEvidenceOutcome(
            result.Orchestration?.StaticTests,
            result.Orchestration?.SourceBuild,
            result.Status,
            result.Orchestration?.RuntimeValidation,
            null,
            result.Status == "pass" ? "PASS" : result.Status.ToUpperInvariant());
        string[] errors = result.Failures?
            .Select(failure => failure.ErrorCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray() ?? [];
        string[] references = result.Failures?
            .Select(failure => failure.EvidenceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray() ?? [];
        ContentRepairAttempt[] repairs = result.PrerequisiteRecovery?
            .Select(recovery => new ContentRepairAttempt(
                recovery.Component,
                recovery.ErrorCode,
                recovery.Action,
                string.Equals(recovery.State, "recovered", StringComparison.OrdinalIgnoreCase)))
            .ToArray() ?? [];
        ContentObservabilityMetrics metrics = new(
            result.DurationMs,
            ValidationAttempts: 1,
            RepairCount: repairs.Length,
            RetryCount: result.PrerequisiteRecovery?.Count ?? 0,
            Succeeded: string.Equals(outcome.Final, "PASS", StringComparison.OrdinalIgnoreCase),
            Available: true);
        ContentEvidenceLifecycleResult lifecycle = service.CaptureEvidenceLifecycle(
            new ContentEvidenceCaptureRequest(
                blueprint.BlueprintId,
                source,
                outcome,
                Errors: errors.Length == 0 ? null : errors,
                Warnings: null,
                Repairs: repairs.Length == 0 ? null : repairs,
                CapturedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                Metrics: new ContentMetricSnapshot(result.DurationMs),
                EvidenceReferences: references.Length == 0 ? null : references));
        ContentBlueprint eventBlueprint = service.Store.GetBlueprint(blueprint.BlueprintId) ?? blueprint;
        bool evidenceAttached = eventBlueprint.Metadata.ValidationEvidence?.Contains(
            lifecycle.Evidence.EvidenceId,
            StringComparer.Ordinal) == true;
        if (evidenceAttached)
        {
            Emit(
                DevelopmentStage.Testing,
                ContentObservabilityEventTypes.BlueprintUpdated,
                "Content blueprint updated with validation evidence.",
                Data(
                    eventBlueprint,
                    state: "updated",
                    evidenceId: lifecycle.Evidence.EvidenceId,
                    referenceIds: references));
        }
        Emit(
            DevelopmentStage.Testing,
            ContentObservabilityEventTypes.BlueprintValidated,
            "Content blueprint evidence recorded.",
            Data(
                eventBlueprint,
                state: "validated",
                evidenceId: lifecycle.Evidence.EvidenceId,
                validationResult: outcome.Final,
                reason: lifecycle.Evidence.Errors?.FirstOrDefault(),
                metrics: metrics,
                referenceIds: references));

        foreach (ContentPrecedentCandidate candidate in lifecycle.Candidates)
        {
            ContentObservabilityEventData candidateData = Data(
                blueprint,
                state: candidate.Qualification.Qualified ? "proven" : "observed",
                precedentId: candidate.CandidateId,
                reason: candidate.Qualification.Qualified
                    ? "qualification criteria satisfied"
                    : string.Join(",", candidate.Qualification.Reasons),
                qualified: candidate.Qualification.Qualified,
                replayPassed: null,
                evidenceId: lifecycle.Evidence.EvidenceId,
                supportingBlueprintIds: candidate.Qualification.SupportingBlueprintIds,
                metrics: new ContentObservabilityMetrics(
                    ValidationAttempts: candidate.Qualification.ValidationAttempts,
                    RepairCount: candidate.Qualification.RepairCount,
                    Available: true));
            Emit(
                DevelopmentStage.Research,
                ContentObservabilityEventTypes.PrecedentDetected,
                "Structural precedent candidate analyzed.",
                candidateData);
            if (candidate.Qualification.Qualified)
            {
                Emit(
                    DevelopmentStage.Research,
                    ContentObservabilityEventTypes.PrecedentQualified,
                    "Precedent qualified from independent evidence.",
                    candidateData with { Metrics = null });
            }
        }

        foreach (ContentPromotionResult promotion in lifecycle.Promotions)
        {
            ContentPrecedentCandidate? candidate = lifecycle.Candidates
                .FirstOrDefault(value => value.CandidateId == promotion.CandidateId);
            string? precedentId = candidate?.CandidateId ?? promotion.CandidateId;
            Emit(
                DevelopmentStage.Implementation,
                ContentObservabilityEventTypes.PromotionStarted,
                "RimContent promotion evaluated.",
                Data(
                    blueprint,
                    state: "started",
                    precedentId: precedentId,
                    archetypeId: promotion.ArchetypeId,
                    archetypeVersion: promotion.Version,
                    evidenceId: lifecycle.Evidence.EvidenceId,
                    reason: "qualified precedent reached automatic promotion",
                    replayPassed: promotion.Replay.Passed,
                    supportingBlueprintIds: promotion.Replay.ReplayedBlueprintIds));
            Emit(
                DevelopmentStage.Implementation,
                promotion.Promoted
                    ? ContentObservabilityEventTypes.PromotionCompleted
                    : ContentObservabilityEventTypes.PromotionRejected,
                promotion.Promoted
                    ? "RimContent archetype promoted."
                    : "RimContent promotion rejected.",
                Data(
                    blueprint,
                    state: promotion.Promoted ? "promoted" : "rejected",
                    precedentId: precedentId,
                    archetypeId: promotion.ArchetypeId,
                    archetypeVersion: promotion.Version,
                    evidenceId: lifecycle.Evidence.EvidenceId,
                    reason: promotion.Reasons.FirstOrDefault(),
                    replayPassed: promotion.Replay.Passed,
                    supportingBlueprintIds: promotion.Replay.ReplayedBlueprintIds));
        }

        if (lifecycle.ArchetypeUsage is { } usage)
        {
            bool succeeded = usage.Succeeded == true;
            Emit(
                DevelopmentStage.Implementation,
                ContentObservabilityEventTypes.ArchetypeUsed,
                "RimContent archetype used by content task.",
                Data(
                    blueprint,
                    state: succeeded ? "succeeded" : "failed",
                    archetypeId: usage.ArchetypeId,
                    archetypeVersion: usage.ArchetypeVersion,
                    evidenceId: usage.EvidenceId,
                    reuseSource: ContentReuseSources.RimContent,
                    reason: succeeded ? "validation passed" : lifecycle.QuarantineReason,
                    metrics: metrics with { Succeeded = succeeded }));
            if (!succeeded)
            {
                Emit(
                    DevelopmentStage.Testing,
                    ContentObservabilityEventTypes.RegressionDetected,
                    "RimContent reuse regression detected.",
                    Data(
                        blueprint,
                        state: "regression",
                        archetypeId: usage.ArchetypeId,
                        archetypeVersion: usage.ArchetypeVersion,
                        evidenceId: usage.EvidenceId,
                        reason: lifecycle.QuarantineReason,
                        metrics: metrics with { Succeeded = false }));
            }
        }

        if (lifecycle.ArchetypeQuarantined && lifecycle.ArchetypeUsage is { } quarantinedUsage)
        {
            Emit(
                DevelopmentStage.Testing,
                ContentObservabilityEventTypes.ArchetypeQuarantined,
                "RimContent archetype quarantined after attributable regression.",
                Data(
                    blueprint,
                    state: "quarantined",
                    archetypeId: quarantinedUsage.ArchetypeId,
                    archetypeVersion: quarantinedUsage.ArchetypeVersion,
                    evidenceId: quarantinedUsage.EvidenceId,
                    reason: lifecycle.QuarantineReason));
            if (lifecycle.RolledBackToVersion is { } rollbackVersion)
            {
                Emit(
                    DevelopmentStage.Implementation,
                    ContentObservabilityEventTypes.RollbackCompleted,
                    "RimContent reuse rolled back to the prior stable version.",
                    Data(
                        blueprint,
                        state: "rolled-back",
                        archetypeId: quarantinedUsage.ArchetypeId,
                        archetypeVersion: rollbackVersion,
                        previousArchetypeVersion: quarantinedUsage.ArchetypeVersion,
                        evidenceId: quarantinedUsage.EvidenceId,
                        reason: "prior active version remained healthy"));
            }
        }
    }
    private static void Emit(
        DevelopmentStage stage,
        string type,
        string summary,
        ContentObservabilityEventData data) =>
        AgentObservabilityRuntime.Record(stage, type, summary, data);

    private static ContentObservabilityEventData Data(
        ContentBlueprint blueprint,
        string state,
        string? reason = null,
        string? precedentId = null,
        string? archetypeId = null,
        int? archetypeVersion = null,
        int? previousArchetypeVersion = null,
        string? evidenceId = null,
        string? reuseSource = null,
        string? validationResult = null,
        bool? qualified = null,
        bool? replayPassed = null,
        IReadOnlyList<string>? referenceIds = null,
        IReadOnlyList<string>? supportingBlueprintIds = null,
        ContentObservabilityMetrics? metrics = null)
    {
        string? resolvedReuseSource = reuseSource ?? blueprint.Intent.ReuseSource;
        return new ContentObservabilityEventData(
            ContentObservabilitySchemas.EventData,
            state,
            blueprint.Metadata.Project,
            blueprint.BlueprintId,
            precedentId,
            precedentId,
            archetypeId,
            archetypeVersion,
            previousArchetypeVersion,
            evidenceId,
            blueprint.Metadata.SourceIdentity?.SourceFingerprint,
            blueprint.Intent.ContentKind,
            blueprint.Intent.GameplayRole,
            resolvedReuseSource,
            reason,
            validationResult,
            qualified,
            replayPassed,
            state,
            referenceIds,
            supportingBlueprintIds,
            metrics,
            blueprint.Intent.DesignParameters,
            blueprint.Intent.VanillaComparables,
            blueprint.Intent.FrameworkRequirements,
            blueprint.Metadata.FrameworkDependencies,
            blueprint.Intent.ValidationExpectations,
            resolvedReuseSource switch
            {
                ContentReuseSources.RimContent or
                    ContentReuseSources.Precedent or
                    ContentReuseSources.VanillaReference => "reused",
                ContentReuseSources.Novel => "novel",
                _ => null
            });
    }
}
