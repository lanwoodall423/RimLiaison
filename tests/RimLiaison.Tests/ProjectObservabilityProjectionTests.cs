using System.Text.Json;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ProjectObservabilityProjectionTests
{
    public static void NewerFailedCommandDoesNotReplaceSuccessfulValidation()
    {
        AgentSnapshot agent = Agent("alpha", "run-1", AgentStatus.Completed, AgentCompletionState.ValidationIncomplete);
        AgentEvent pass = Event(agent, "validation.completed", "Validation passed", 100, 1, new { outcome = "pass" });
        AgentEvent failure = Event(agent, AgentEventTypes.CommandFailed, "Command failed", 200, 2, new { command = "rimliaison affected", errorCode = "CMD_FAIL" });
        ProjectObservabilityState state = Build(agent, [pass, failure]).Projects.Single();

        Equal(ProjectObservabilityStateKind.NeedsAttention, state.State);
        Equal(failure.Id, state.LatestAttempt!.EventId);
        Equal(pass.Id, state.LastSuccessfulValidation!.EventId);
        Equal(failure.Id, state.Timeline.Single(entry => entry.Timestamp == 200).EventId);
    }

    public static void RecoveredInfrastructureFailureIsAHealthyProjectWithFinding()
    {
        AgentSnapshot agent = Agent("alpha", "run-2", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent failed = Event(agent, AgentEventTypes.CommandFailed, "DevBridge failed", 100, 1, new { errorCode = "DB_RECONNECT", componentOwner = "DevBridge" });
        AgentEvent recovered = Event(agent, AgentEventTypes.RecoveryCompleted, "Recovery completed", 150, 2, new { recovered = true });
        AgentEvent pass = Event(agent, AgentEventTypes.ValidationCompleted, "Validation passed", 200, 3, new { outcome = "pass" });
        AgentIssue issue = Issue(agent, AgentIssueCategory.ToolingFailure, failed, recovered, recovered: true, fingerprint: "DB_RECONNECT");
        ProjectObservabilityProjectionResult result = Build(agent, [failed, recovered, pass], [issue]);

        Equal(ProjectObservabilityStateKind.Healthy, result.Projects.Single().State);
        ToolingFinding finding = result.ToolingFindings.Single();
        Equal(ToolingFindingKind.RecoveredFailure, finding.Kind);
        False(finding.ProductionWorkFailed);
        True(finding.RecoverySucceeded);
    }

    public static void ActiveProjectIsWorking()
    {
        AgentSnapshot agent = Agent("alpha", "run-3", AgentStatus.Running, AgentCompletionState.None, start: 100, lastActivity: 180);
        ProjectObservabilityState state = Build(agent, [Event(agent, AgentEventTypes.ToolStarted, "Inspecting", 180, 1)]).Projects.Single();
        Equal(ProjectObservabilityStateKind.Working, state.State);
        Equal(1, state.ActiveSessions.Count);
    }

    public static void InactiveSuccessfulProjectIsHealthy()
    {
        AgentSnapshot agent = Agent("alpha", "run-4", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent pass = Event(agent, AgentEventTypes.TestPassed, "Tests passed", 100, 1);
        ProjectObservabilityState state = Build(agent, [pass]).Projects.Single();
        Equal(ProjectObservabilityStateKind.Healthy, state.State);
        False(state.ActionRequired);
    }

    public static void StaleSessionNeedsAttention()
    {
        AgentSnapshot agent = Agent("alpha", "run-5", AgentStatus.Running, AgentCompletionState.None, start: 100, lastActivity: 100);
        ProjectObservabilityState state = Build(agent, [], now: 200, thresholdMilliseconds: 50).Projects.Single();
        Equal(ProjectObservabilityStateKind.NeedsAttention, state.State);
        True(state.StaleSessionDetected);
        Equal(0, state.ActiveSessions.Count);
    }
    public static void AbandonedSessionNeedsAttention()
    {
        AgentSnapshot agent = Agent("alpha", "run-abandoned", AgentStatus.Running, AgentCompletionState.None)
            with
        { CompletionResult = "ABANDONED" };
        ProjectObservabilityState state = Build(agent, []).Projects.Single();
        Equal(ProjectObservabilityStateKind.NeedsAttention, state.State);
        True(state.StaleSessionDetected);
    }


    public static void IncompleteHistoryIsNotInferredHealthy()
    {
        AgentSnapshot agent = Agent("alpha", "run-6", AgentStatus.Completed, AgentCompletionState.Succeeded);
        ProjectObservabilityProjectionResult result = Build(
            agent,
            [Event(agent, AgentEventTypes.TestPassed, "Tests passed", 100, 1)],
            historyComplete: false);
        Equal(ProjectObservabilityStateKind.Unknown, result.Projects.Single().State);
        Equal(ProjectObservabilityCompleteness.Partial, result.Projects.Single().Completeness);
    }

    public static void TimelineRetainsLatestMeaningfulActivityWhenCapped()
    {
        AgentSnapshot agent = Agent("alpha", "run-capped-timeline", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent pass = Event(agent, AgentEventTypes.TestPassed, "Passed", 100, 1);
        AgentEvent lifecycle = Event(agent, AgentEventTypes.StageChanged, "Stage changed", 200, 2);
        ProjectObservabilityState state = ProjectObservabilityProjection.Build(
            new AgentObservabilityView([agent], [pass, lifecycle], []),
            new ProjectObservabilityProjectionOptions(1_000, TimeSpan.FromMinutes(5), MaximumTimelineEntries: 1))
            .Projects.Single();

        Equal(100L, state.LastMeaningfulActivityAt!.Value);
        True(state.Timeline.Single().EventId == pass.Id);
    }

    public static void QualificationActivityDoesNotCreateProductionProject()
    {
        AgentSnapshot production = Agent("production", "run-7a", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentSnapshot qualification = Agent("qualification", "run-7b", AgentStatus.Completed, AgentCompletionState.Succeeded, workload: "qualification", qualification: "burn-in");
        ProjectObservabilityProjectionResult result = Build(
            [production, qualification],
            [Event(production, AgentEventTypes.TestPassed, "Production passed", 100, 1), Event(qualification, AgentEventTypes.TestPassed, "Qualification passed", 200, 2)]);
        Equal(1, result.Projects.Count);
        Equal("mod:production", result.Projects.Single().Identity.CanonicalEntityId);
    }

    public static void MultipleSessionsAggregateUnderOneProject()
    {
        AgentSnapshot first = Agent("alpha", "run-8a", AgentStatus.Completed, AgentCompletionState.Succeeded, logicalAgent: "worker-a");
        AgentSnapshot second = Agent("alpha", "run-8b", AgentStatus.Running, AgentCompletionState.None, start: 200, lastActivity: 210, logicalAgent: "worker-b");
        ProjectObservabilityState state = Build(
            [first, second],
            [Event(first, AgentEventTypes.TestPassed, "First passed", 100, 1), Event(second, AgentEventTypes.ToolStarted, "Second working", 210, 2)]).Projects.Single();
        Equal(2, state.Timeline.Count);
        Equal(1, state.ActiveSessions.Count);
        Equal("worker-b", state.ActiveSessions.Single().LogicalAgentId);
    }

    public static void ConcurrentLogicalWorkersRemainDistinct()
    {
        AgentSnapshot first = Agent("alpha", "run-9a", AgentStatus.Running, AgentCompletionState.None, logicalAgent: "worker-a", start: 100, lastActivity: 150);
        AgentSnapshot second = Agent("alpha", "run-9b", AgentStatus.Running, AgentCompletionState.None, logicalAgent: "worker-b", start: 110, lastActivity: 150);
        ProjectObservabilityState state = Build(
            [first, second],
            [Event(first, AgentEventTypes.ToolStarted, "A", 150, 1), Event(second, AgentEventTypes.ToolStarted, "B", 150, 2)]).Projects.Single();
        Equal(2, state.ActiveSessions.Count);
        Equal(2, state.ActiveSessions.Select(session => session.LogicalAgentId).Distinct().Count());
    }

    public static void OverviewAndProjectTimelineShareT2()
    {
        AgentSnapshot agent = Agent("alpha", "run-10", AgentStatus.Completed, AgentCompletionState.ValidationIncomplete);
        AgentEvent t1 = Event(agent, AgentEventTypes.ValidationCompleted, "Validation passed", 100, 1, new { outcome = "pass" });
        AgentEvent t2 = Event(agent, AgentEventTypes.CommandFailed, "Newer command failed", 200, 2);
        ProjectObservabilityState state = Build(agent, [t1, t2]).Projects.Single();
        Equal(200L, state.LastMeaningfulActivityAt!.Value);
        True(state.Timeline.Any(entry => entry.EventId == t2.Id));
        Equal(t2.Id, state.LatestAttempt!.EventId);
    }

    public static void SuccessfulTaskWithRecoveredToolingFailureRemainsHealthy()
    {
        AgentSnapshot agent = Agent("alpha", "run-11", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent failed = Event(agent, AgentEventTypes.ToolFailed, "Tool transiently failed", 100, 1, new { errorCode = "TRANSIENT", componentOwner = "DevBridge" });
        AgentEvent recovered = Event(agent, AgentEventTypes.RecoveryCompleted, "Recovered", 120, 2, new { recovered = true });
        AgentEvent pass = Event(agent, AgentEventTypes.ValidationCompleted, "Passed", 140, 3, new { outcome = "pass" });
        AgentIssue issue = Issue(agent, AgentIssueCategory.ToolingFailure, failed, recovered, recovered: true, fingerprint: "TRANSIENT");
        ProjectObservabilityState state = Build(agent, [failed, recovered, pass], [issue]).Projects.Single();
        Equal(ProjectObservabilityStateKind.Healthy, state.State);
        True(state.HasToolingFindings);
    }
    public static void FindingWithRecoveryEventsRemainsOneOccurrence()
    {
        AgentSnapshot agent = Agent("alpha", "run-recovered-occurrence", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent failed = Event(agent, AgentEventTypes.ToolFailed, "Tool failed", 100, 1, new { errorCode = "TRANSIENT" });
        AgentEvent recovered = Event(agent, AgentEventTypes.RecoveryCompleted, "Tool recovered", 120, 2, new { recovered = true });
        AgentIssue issue = Issue(agent, AgentIssueCategory.ToolingFailure, failed, recovered, recovered: true, fingerprint: "TRANSIENT");

        ToolingFinding finding = Build(agent, [failed, recovered], [issue]).ToolingFindings.Single();

        Equal(1, finding.OccurrenceCount);
        Equal(2, finding.Occurrences.Single().SupportingEventIds.Count);
    }

    public static void OrdinaryRetryDoesNotCreateToolingFinding()
    {
        AgentSnapshot agent = Agent("alpha", "run-ordinary-retry", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent retry = Event(
            agent,
            AgentEventTypes.RetryStarted,
            "Retry started",
            100,
            1,
            new { operationKey = "validation:ordinary", retryCount = 1 });

        Equal(0, Build(agent, [retry]).ToolingFindings.Count);
    }

    public static void RepeatedRetryCreatesOneFindingGroup()
    {
        AgentSnapshot agent = Agent("alpha", "run-repeated-retry", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent first = Event(
            agent,
            AgentEventTypes.RetryStarted,
            "Retry started",
            100,
            1,
            new { operationKey = "validation:repeated", retryCount = 2, fingerprint = "RETRY_X" });
        AgentEvent second = Event(
            agent,
            AgentEventTypes.RetryCompleted,
            "Retry completed",
            120,
            2,
            new { operationKey = "validation:repeated", retryCount = 2, fingerprint = "RETRY_X" });

        ToolingFinding finding = Build(agent, [first, second]).ToolingFindings.Single();

        Equal(2, finding.OccurrenceCount);
    }

    public static void SuccessfulWorkaroundCreatesToolingFinding()
    {
        AgentSnapshot agent = Agent("alpha", "run-12", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent workaround = Event(agent, AgentEventTypes.WorkaroundApplied, "Used fallback command", 100, 1, new { workaround = "fallback command" });
        AgentEvent pass = Event(agent, AgentEventTypes.ValidationCompleted, "Passed", 120, 2, new { outcome = "pass" });
        ProjectObservabilityProjectionResult result = Build(agent, [workaround, pass]);
        Equal(ProjectObservabilityStateKind.Healthy, result.Projects.Single().State);
        Equal(ToolingFindingKind.SuccessfulWorkaround, result.ToolingFindings.Single().Kind);
    }

    public static void RepeatedToolingShortcomingAggregatesAcrossProjectsAndKeepsOccurrences()
    {
        AgentSnapshot first = Agent("alpha", "run-13a", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentSnapshot second = Agent("beta", "run-13b", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent firstEvent = Event(first, AgentEventTypes.ToolLimitation, "Unsupported operation", 100, 1, new { fingerprint = "CAP_X" });
        AgentEvent secondEvent = Event(second, AgentEventTypes.ToolLimitation, "Unsupported operation", 200, 2, new { fingerprint = "CAP_X" });
        ProjectObservabilityProjectionResult result = Build([first, second], [firstEvent, secondEvent]);
        ToolingFinding finding = result.ToolingFindings.Single();
        Equal(2, finding.OccurrenceCount);
        Equal(2, finding.AffectedProjects.Count);
    }
    public static void FindingCountSurvivesBoundedOccurrenceRetention()
    {
        AgentSnapshot agent = Agent("alpha", "run-13-bounded", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent first = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation", 100, 1, new { fingerprint = "CAP_BOUNDED" });
        AgentEvent second = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation", 200, 2, new { fingerprint = "CAP_BOUNDED" });
        ProjectObservabilityProjectionResult result = ProjectObservabilityProjection.Build(
            new AgentObservabilityView([agent], [first, second], []),
            new ProjectObservabilityProjectionOptions(
                300,
                TimeSpan.FromMinutes(5),
                MaximumFindingOccurrences: 1));
        ToolingFinding finding = result.ToolingFindings.Single();
        Equal(2, finding.OccurrenceCount);
        Equal(1, finding.Occurrences.Count);
    }


    public static void ProjectFailureWithoutToolingFindingIsSeparate()
    {
        AgentSnapshot agent = Agent("alpha", "run-project-only", AgentStatus.Failed, AgentCompletionState.Failed);
        AgentEvent projectFailure = Event(agent, AgentEventTypes.TestFailed, "Project assertion failed", 100, 1);
        AgentIssue projectIssue = Issue(agent, AgentIssueCategory.ModDefect, projectFailure, classification: "MOD_FAILURE");
        ProjectObservabilityProjectionResult result = Build(agent, [projectFailure], [projectIssue]);
        Equal(ProjectObservabilityStateKind.Blocked, result.Projects.Single().State);
        Equal(0, result.ToolingFindings.Count);
    }

    public static void ToolingFailureWithoutProjectProblemRemainsIndependent()
    {
        AgentSnapshot agent = Agent("alpha", "run-tooling-only", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent tooling = Event(agent, AgentEventTypes.ToolFailed, "Tool unavailable", 100, 1, new { errorCode = "TOOL_DOWN" });
        AgentIssue toolingIssue = Issue(agent, AgentIssueCategory.ToolingFailure, tooling, fingerprint: "TOOL_DOWN");
        ProjectObservabilityProjectionResult result = Build(agent, [tooling], [toolingIssue]);
        Equal(ProjectObservabilityStateKind.NeedsAttention, result.Projects.Single().State);
        True(result.ToolingFindings.Single().ProductionWorkFailed);
    }

    public static void ProjectFailureAndToolingFindingStaySeparate()
    {
        AgentSnapshot agent = Agent("alpha", "run-14", AgentStatus.Failed, AgentCompletionState.Failed);
        AgentEvent projectFailure = Event(agent, AgentEventTypes.TestFailed, "Project assertion failed", 100, 1);
        AgentEvent tooling = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported diagnostic", 110, 2, new { fingerprint = "DIAG_X" });
        AgentIssue projectIssue = Issue(agent, AgentIssueCategory.ModDefect, projectFailure, classification: "MOD_FAILURE");
        ProjectObservabilityProjectionResult result = Build(agent, [projectFailure, tooling], [projectIssue]);
        Equal(ProjectObservabilityStateKind.Blocked, result.Projects.Single().State);
        Equal(1, result.ToolingFindings.Count);
        Equal(ToolingFindingKind.UnsupportedOperation, result.ToolingFindings.Single().Kind);
    }

    public static void MissingEvidenceProducesUnknown()
    {
        AgentSnapshot agent = Agent("alpha", "run-15", AgentStatus.Created, AgentCompletionState.None);
        ProjectObservabilityState state = Build(agent, []).Projects.Single();
        Equal(ProjectObservabilityStateKind.Unknown, state.State);
        Equal(ProjectObservabilityCompleteness.Unknown, state.Completeness);
        False(state.ActionRequired);
    }
    public static void TerminalSnapshotWithoutMeaningfulEvidenceIsUnknown()
    {
        AgentSnapshot agent = Agent("alpha", "run-terminal-without-history", AgentStatus.Completed, AgentCompletionState.Succeeded);

        ProjectObservabilityState state = Build(agent, []).Projects.Single();

        Equal(ProjectObservabilityStateKind.Unknown, state.State);
        Equal(ProjectObservabilityCompleteness.Unknown, state.Completeness);
    }

    public static void AmbiguousSessionIdentityDoesNotCrossContaminateProjects()
    {
        AgentSnapshot first = Agent("alpha", "run-ambiguous", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentSnapshot second = first with
        {
            ModId = "beta",
            ModName = "beta",
            CanonicalEntityId = "mod:beta",
            DisplayName = "beta"
        };
        AgentEvent firstEvent = Event(first, AgentEventTypes.TestPassed, "Alpha passed", 100, 1);
        AgentEvent secondEvent = Event(second, AgentEventTypes.TestPassed, "Beta passed", 200, 2);

        ProjectObservabilityProjectionResult result = Build([first, second], [firstEvent, secondEvent]);

        Equal(2, result.Projects.Count);
        True(result.Projects.All(project => project.Completeness == ProjectObservabilityCompleteness.Unknown));
    }


    public static void SimilarDistinctFindingsDoNotCollapse()
    {
        AgentSnapshot agent = Agent("alpha", "run-16", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent first = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation A", 100, 1, new { fingerprint = "CAP_A" });
        AgentEvent second = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation B", 110, 2, new { fingerprint = "CAP_B" });
        ProjectObservabilityProjectionResult result = Build(agent, [first, second]);
        Equal(2, result.ToolingFindings.Count);
    }

    public static void UiOverviewAndProjectDetailUseSameCanonicalProjection()
    {
        AgentSnapshot agent = Agent("alpha", "run-ui", AgentStatus.Completed, AgentCompletionState.Succeeded);
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(agent);
        store.AppendEvent(new AgentEventRequest(
            agent.RunId,
            agent.AgentId,
            agent.ModId,
            DevelopmentStage.Testing,
            AgentEventTypes.CommandFailed,
            "Newest command failed",
            Timestamp: 200));
        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityAllView all = ui.Snapshot.All!;
        AgentObservabilityAgentView detail = ui.ShowAgent(agent.ModId).Agent!;

        Equal(1, all.Projects.Count);
        Equal(all.Projects.Single().State, detail.Project!.State);
        Equal(
            all.Projects.Single().LastMeaningfulActivityAt,
            detail.Project.LastMeaningfulActivityAt);
        True(detail.CanonicalTimeline.Any(entry => entry.EventId == all.Projects.Single().Timeline.Last().EventId));
    }

    public static void FailedTerminalSnapshotNeedsAttentionWithoutAttemptEvent()
    {
        AgentSnapshot agent = Agent("alpha", "run-terminal-failure", AgentStatus.Failed, AgentCompletionState.Failed);
        ProjectObservabilityState state = Build(agent, []).Projects.Single();
        Equal(ProjectObservabilityStateKind.NeedsAttention, state.State);
        True(state.ActionRequired);
    }

    public static void ToolingAssessmentPreservesObservedEvidenceAndDerivedOwner()
    {
        AgentSnapshot agent = Agent("alpha", "run-assessment", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent workaround = Event(
            agent,
            AgentEventTypes.WorkaroundApplied,
            "Fallback used",
            100,
            1,
            new
            {
                workaround = "fallback command",
                command = "tool --fallback",
                arguments = "--safe",
                stdoutExcerpt = "captured stdout",
                stderrExcerpt = "captured stderr",
                causalComponent = "DevBridge",
                evidenceId = "evidence-1"
            });
        ToolingAssessment assessment = ProjectObservabilityProjection.BuildAssessment(
            Build(agent, [workaround]).ToolingFindings,
            ProjectObservabilityCompleteness.Complete);
        ToolingAssessmentFinding finding = assessment.Findings.Single();

        Equal(AgentObservabilitySchemas.ToolingAssessment, assessment.SchemaVersion);
        True(finding.Arguments!.Contains("--safe", StringComparer.Ordinal));
        True(finding.StandardOutput!.Contains("captured stdout", StringComparer.Ordinal));
        True(finding.StandardError!.Contains("captured stderr", StringComparer.Ordinal));
        True(finding.EvidenceIds!.Contains("evidence-1", StringComparer.Ordinal));
        True(finding.LikelyComponentOwners!.Contains("derived:DevBridge", StringComparer.Ordinal));
        True(finding.ObservedFacts.Any(fact => fact.Contains("derived component owner", StringComparison.Ordinal)));
        True(finding.SupportingEventIds!.Contains(workaround.Id, StringComparer.Ordinal));
    }

    public static void RecoveredProductionToolchainAssessmentContainsRepairEvidence()
    {
        AgentSnapshot agent = Agent("alpha", "run-production-recovery", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent recoveredTooling = Event(
            agent,
            AgentEventTypes.ToolFailed,
            "Recovered production-toolchain integrity issue",
            100,
            1,
            new
            {
                issueKind = "TOOLING_FAILURE",
                componentOwner = "RimLiaison",
                errorCode = "PRODUCTION_TOOLCHAIN_INTEGRITY_FAULT",
                originalFault = "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
                expectedPromotedFingerprint = "promoted-fingerprint",
                affectedArtifacts = new[] { "C:/staging/rimliaison.exe" },
                repairAttempted = true,
                repairResult = "repaired",
                verificationResult = "promoted-identity-and-readiness-verified",
                retryCount = 1,
                retryResult = "normal-operation-continued",
                recovered = true,
                elapsedRecoveryMs = 37,
                productionImpact = "toolchain-repaired-before-project-operation",
                recoveryAction = "reinstall-qualified-promoted-package",
                recoveryState = "recovered"
            });
        AgentIssue issue = Issue(
            agent,
            AgentIssueCategory.ToolingFailure,
            recoveredTooling,
            recovered: true,
            fingerprint: "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING");

        ToolingAssessment assessment = ProjectObservabilityProjection.BuildAssessment(
            Build(agent, [recoveredTooling], [issue]).ToolingFindings,
            ProjectObservabilityCompleteness.Complete);
        ToolingAssessmentFinding finding = assessment.Findings.Single();
        string facts = string.Join("\n", finding.ObservedFacts);

        True(finding.RecoverySucceeded);
        False(finding.ProductionWorkFailed);
        Equal(1, finding.RetryCount);
        Equal(37L, finding.AddedDelayMilliseconds);
        True(facts.Contains("originalFault=PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING", StringComparison.Ordinal));
        True(facts.Contains("affectedArtifact=C:/staging/rimliaison.exe", StringComparison.Ordinal));
        True(facts.Contains("expectedPromotedFingerprint=promoted-fingerprint", StringComparison.Ordinal));
        True(facts.Contains("repairResult=repaired", StringComparison.Ordinal));
        True(facts.Contains("verificationResult=promoted-identity-and-readiness-verified", StringComparison.Ordinal));
        True(facts.Contains("retryResult=normal-operation-continued", StringComparison.Ordinal));
        True(facts.Contains("recoveryDurationMs=37", StringComparison.Ordinal));
        True(facts.Contains("workflowOutcome=recovered", StringComparison.Ordinal));
        Equal(1, assessment.Recurrence.Single().OccurrenceCount);
        Equal("complete", finding.EvidenceCompleteness);
    }

    public static void UnkeyedSimilarFindingsRemainSeparate()
    {
        AgentSnapshot agent = Agent("alpha", "run-unkeyed", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent first = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation", 100, 1);
        AgentEvent second = Event(agent, AgentEventTypes.ToolLimitation, "Unsupported operation", 110, 2);
        ProjectObservabilityProjectionResult result = Build(agent, [first, second]);
        Equal(2, result.ToolingFindings.Count);
    }
    public static void OptionalToolingGapDoesNotFailSuccessfulProject()
    {
        AgentSnapshot agent = Agent("alpha", "run-optional-gap", AgentStatus.Completed, AgentCompletionState.Succeeded);
        AgentEvent gap = Event(
            agent,
            AgentEventTypes.ValidationCapabilityBlocked,
            "Optional validation unavailable",
            100,
            1,
            new { required = false, capabilityId = "optional-check" });
        AgentEvent pass = Event(agent, AgentEventTypes.ValidationCompleted, "Passed", 120, 2, new { outcome = "pass" });
        ProjectObservabilityProjectionResult result = Build(agent, [gap, pass]);

        Equal(ProjectObservabilityStateKind.Healthy, result.Projects.Single().State);
        Equal(ToolingFindingKind.MissingCapability, result.ToolingFindings.Single().Kind);
    }


    public static void UiProjectionReevaluatesStalenessAsTimeAdvances()
    {
        const long lastActivity = 100;
        const long staleThresholdMilliseconds = 5 * 60 * 1000;
        AgentSnapshot agent = Agent(
            "alpha",
            "run-clock",
            AgentStatus.Running,
            AgentCompletionState.None,
            start: lastActivity,
            lastActivity: lastActivity);
        AgentEvent started = Event(
            agent,
            AgentEventTypes.AgentStarted,
            "started",
            lastActivity,
            1);

        ProjectObservabilityState before = Build(
            agent,
            [started],
            now: lastActivity + staleThresholdMilliseconds - 1,
            thresholdMilliseconds: staleThresholdMilliseconds).Projects.Single();
        ProjectObservabilityState after = Build(
            agent,
            [started],
            now: lastActivity + staleThresholdMilliseconds + 1,
            thresholdMilliseconds: staleThresholdMilliseconds).Projects.Single();

        Equal(ProjectObservabilityStateKind.Working, before.State);
        Equal(ProjectObservabilityStateKind.NeedsAttention, after.State);
    }

    private static ProjectObservabilityProjectionResult Build(
        AgentSnapshot agent,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue>? issues = null,
        long now = 1_000,
        long thresholdMilliseconds = 5_000,
        bool historyComplete = true) =>
        Build([agent], events, issues, now, thresholdMilliseconds, historyComplete);

    private static ProjectObservabilityProjectionResult Build(
        IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue>? issues = null,
        long now = 1_000,
        long thresholdMilliseconds = 5_000,
        bool historyComplete = true) =>
        ProjectObservabilityProjection.Build(
            new AgentObservabilityView(agents, events, issues ?? []),
            new ProjectObservabilityProjectionOptions(
                now,
                TimeSpan.FromMilliseconds(thresholdMilliseconds),
                HistoryComplete: historyComplete));

    private static AgentSnapshot Agent(
        string modId,
        string runId,
        AgentStatus status,
        AgentCompletionState completion,
        long start = 10,
        long? lastActivity = null,
        string? logicalAgent = null,
        string workload = "production",
        string? qualification = null) =>
        new AgentSnapshot
        {
            AgentId = "agent-" + runId,
            RunId = runId,
            SessionId = "session-" + runId,
            ModId = modId,
            ModName = modId,
            EntityType = ObservabilityEntityTypes.Mod,
            CanonicalEntityId = "mod:" + modId,
            DisplayName = modId,
            WorkloadKind = workload,
            QualificationProfile = qualification,
            Status = status,
            CompletionState = completion,
            CompletionResult = completion == AgentCompletionState.Succeeded ? "PASS" : null,
            FailureState = completion == AgentCompletionState.Failed,
            StartTime = start,
            LastActivityAt = lastActivity,
            CompletedAt = completion is AgentCompletionState.Succeeded or AgentCompletionState.Failed ? start + 10 : null
        } with
        { LogicalAgentId = logicalAgent };

    private static AgentEvent Event(
        AgentSnapshot agent,
        string type,
        string summary,
        long timestamp,
        long sequence,
        object? data = null) =>
        new()
        {
            Id = $"evt-{agent.RunId}-{sequence}",
            RunId = agent.RunId,
            AgentId = agent.AgentId,
            LogicalAgentId = agent.LogicalAgentId,
            SessionId = agent.SessionId,
            ModId = agent.ModId,
            EntityType = agent.EntityType,
            CanonicalEntityId = agent.CanonicalEntityId,
            DisplayName = agent.DisplayName,
            Timestamp = timestamp,
            Sequence = sequence,
            Stage = DevelopmentStage.Testing,
            Type = type,
            Summary = summary,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data)
        };


    private static AgentIssue Issue(
        AgentSnapshot agent,
        AgentIssueCategory category,
        AgentEvent first,
        AgentEvent? second = null,
        bool recovered = false,
        string? fingerprint = null,
        string? classification = null) =>
        new()
        {
            Id = "issue-" + first.Id,
            RunId = agent.RunId,
            AgentId = agent.AgentId,
            LogicalAgentId = agent.LogicalAgentId,
            SessionId = agent.SessionId,
            ModId = agent.ModId,
            EntityType = agent.EntityType,
            CanonicalEntityId = agent.CanonicalEntityId,
            DisplayName = agent.DisplayName,
            Timestamp = first.Timestamp,
            Category = category,
            Severity = category == AgentIssueCategory.ModDefect ? AgentIssueSeverity.Error : AgentIssueSeverity.Warning,
            Summary = first.Summary,
            EventIds = second is null ? [first.Id] : [first.Id, second.Id],
            Recovered = recovered,
            ResolutionEventId = recovered ? second?.Id : null,
            Fingerprint = fingerprint,
            Classification = classification,
            Blocking = category == AgentIssueCategory.ModDefect,
            CurrentState = recovered ? "resolved" : "open",
            ResolutionState = recovered ? "resolved" : "unresolved"
        };

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
