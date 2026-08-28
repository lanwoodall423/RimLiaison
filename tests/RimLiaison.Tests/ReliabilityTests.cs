using System.Text.Json;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ReliabilityTests
{
    public static void ProductionCampaignProjection()
    {
        SuccessfulWorkflowHasNoInfrastructureIncident();
        SourceFailureIsNotInfrastructureFailure();
        RecoveredIncidentIsCounted();
        UnrecoveredIncidentIsCounted();
        CausalDuplicatesCollapse();
        QualificationIsExcluded();
        ExperimentalToolchainIsExcluded();
        UnknownToolchainIsIncomplete();
        FingerprintMismatchIsExcluded();
        CoverageIsDetected();
        ConcurrencyIsProven();
        MissingConcurrencyIsUnknown();
        StaleRuntimeIdentityFails();
        MissingProfileDoesNotCreateTiming();
        NestedProfileTimingIsNotSummed();
        DefaultCampaignCollects();
        CompleteCampaignPasses();
        UnrecoveredCampaignFails();
        HistoryLossIsIncomplete();
        PromotedFingerprintIsDeterministic();
        QualificationFixtureFailureDoesNotCount();
        LegitimateModTestFailureDoesNotFailCampaign();
        ToolchainFingerprintBindsCampaign();
        CampaignConfigurationPersists();
    }

    private static void SuccessfulWorkflowHasNoInfrastructureIncident()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-success", "logical-success");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AgentReliabilityWorkflowRecord workflow = projection.Workflows.Single();
        AssertEqual(0, workflow.InfrastructureIncidentCount, "successful workflow has no infrastructure incident");
        AssertEqual(1, projection.CompletedSuccessfully, "successful workflow completes successfully");
    }

    private static void SourceFailureIsNotInfrastructureFailure()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-source-failure", "logical-source");
        events.Insert(events.Count - 1, Event(snapshot, 8, AgentEventTypes.TestFailed, new
        {
            issueKind = "MOD_DEFECT",
            failureKind = "test",
            errorCode = "ASSERTION_FAILED"
        }));
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events).Workflows.Single();
        AssertEqual(1, workflow.SourceTestFailureCount, "source/test failure is observed");
        AssertEqual(0, workflow.InfrastructureIncidentCount, "source/test failure does not become infrastructure failure");
    }

    private static void RecoveredIncidentIsCounted()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-recovered", "logical-recovered");
        AgentIssue issue = Issue(snapshot, "issue-recovered", "cause-recovered", recovered: true);
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events, [issue]).Workflows.Single();
        AssertEqual(1, workflow.RecoveredInfrastructureIncidentCount, "recovered incident is counted");
        AssertEqual(0, workflow.UnrecoveredInfrastructureIncidentCount, "recovered incident is not unrecovered");
    }

    private static void UnrecoveredIncidentIsCounted()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-blocked", "logical-blocked");
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events, [Issue(snapshot, "issue-blocked", "cause-blocked")]).Workflows.Single();
        AssertEqual(1, workflow.UnrecoveredInfrastructureIncidentCount, "unrecovered incident is counted");
    }

    private static void CausalDuplicatesCollapse()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-duplicate", "logical-duplicate");
        AgentIssue first = Issue(snapshot, "issue-1", "same-cause", recovered: true);
        AgentIssue second = Issue(snapshot, "issue-2", "same-cause", recovered: true);
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events, [first, second]).Workflows.Single();
        AssertEqual(1, workflow.InfrastructureIncidentCount, "propagated causal duplicates collapse");
    }

    private static void QualificationIsExcluded()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-qualification", "logical-qualification", workloadKind: "qualification");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual(0, projection.ProductionWorkflowsObserved, "qualification is absent from production observations");
        Assert(projection.ExcludedWorkflowIds.Values.Contains("qualification-workload"), "qualification exclusion is explicit");
    }

    private static void ExperimentalToolchainIsExcluded()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-experimental", "logical-experimental", toolchainState: "experimental", toolchainFingerprint: "tc-experimental");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual(0, projection.ExactPromotedToolchainWorkflows, "experimental workflow cannot contribute");
        AssertEqual("experimental-toolchain", projection.ExcludedWorkflowIds["run-experimental"], "experimental exclusion reason");
    }

    private static void UnknownToolchainIsIncomplete()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-unknown-toolchain", "logical-unknown", toolchainFingerprint: null);
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual(AgentReliabilityStates.Incomplete, projection.State, "unknown toolchain is incomplete");
        AssertEqual(1, projection.UnknownToolchainWorkflows, "unknown toolchain is counted separately");
    }

    private static void FingerprintMismatchIsExcluded()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-other-toolchain", "logical-other", toolchainFingerprint: "tc-other");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual("promoted-fingerprint-mismatch", projection.ExcludedWorkflowIds["run-other-toolchain"], "cross-fingerprint workflow is excluded");
        AssertEqual(0, projection.ExactPromotedToolchainWorkflows, "cross-fingerprint workflow cannot pass campaign");
    }

    private static void CoverageIsDetected()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-coverage", "logical-coverage");
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events).Workflows.Single();
        Assert(workflow.LiveValidationOccurred == true, "live validation coverage");
        Assert(workflow.RuntimeContentDeploymentOccurred == true, "runtime deployment coverage");
        Assert(workflow.RestartOccurred == true, "restart coverage");
        Assert(workflow.ProofReuseOccurred, "proof reuse coverage");
    }

    private static void ConcurrencyIsProven()
    {
        (AgentSnapshot first, List<AgentEvent> firstEvents) = SuccessfulWorkflow("run-concurrent-a", "logical-a", start: 100, completion: 300);
        (AgentSnapshot second, List<AgentEvent> secondEvents) = SuccessfulWorkflow("run-concurrent-b", "logical-b", start: 150, completion: 350);
        AgentReliabilityCampaignProjection projection = Project(
            [first, second],
            [.. firstEvents, .. secondEvents]);
        AssertEqual(AgentReliabilityStates.Established, projection.ConcurrentProductionActivityState, "shared generation and overlapping logical agents prove concurrency");
        AssertEqual(2, projection.DistinctSimultaneousLogicalAgents, "both logical agents are covered");
    }

    private static void MissingConcurrencyIsUnknown()
    {
        (AgentSnapshot first, List<AgentEvent> firstEvents) = SuccessfulWorkflow("run-no-concurrency-a", "logical-no-a");
        (AgentSnapshot second, List<AgentEvent> secondEvents) = SuccessfulWorkflow("run-no-concurrency-b", "logical-no-b");
        AgentReliabilityProjectionInput input = new(
            [first, second],
            [.. firstEvents.Select(RemoveGeneration), .. secondEvents.Select(RemoveGeneration)],
            [],
            true);
        AgentReliabilityCampaignProjection projection = AgentReliabilityProjection.Build(input, Campaign(minimum: 2));
        AssertEqual(AgentReliabilityStates.Unknown, projection.ConcurrentProductionActivityState, "missing runtime concurrency evidence remains unknown");
    }

    private static void StaleRuntimeIdentityFails()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-stale", "logical-stale");
        events[2] = Event(snapshot, 3, AgentEventTypes.SuiteCompleted, new
        {
            status = "pass",
            artifactFreshness = new
            {
                sourceFingerprint = "source-run-stale",
                deploymentDecision = "deployed",
                evaluationStatus = "MISMATCH",
                loadedArtifactFreshnessProven = false,
                generation = 4
            }
        });
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual(AgentReliabilityStates.Fail, projection.State, "stale runtime identity is a campaign failure");
    }

    private static void MissingProfileDoesNotCreateTiming()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-no-profile", "logical-no-profile");
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events).Workflows.Single();
        Assert(workflow.ObservedBuildTimeMilliseconds is null, "missing profile does not fabricate build duration");
        Assert(!workflow.TimingEvidenceComplete, "missing profile is explicit timing incompleteness");
    }

    private static void NestedProfileTimingIsNotSummed()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-profile", "logical-profile");
        string profile = "{\"outcome\":{\"wallTimeMs\":50},\"phaseTiming\":[{\"phase\":\"build\",\"runs\":1,\"cumulativeMs\":10},{\"phase\":\"deploy\",\"runs\":1,\"cumulativeMs\":20}]}";
        AgentReliabilityWorkflowRecord workflow = Project(snapshot, events, profile: profile).Workflows.Single();
        AssertEqual(50L, workflow.RimLiaisonWallTimeMilliseconds, "profile wall time is authoritative");
        AssertEqual(10L, workflow.ObservedBuildTimeMilliseconds, "phase timing remains separate");
        AssertEqual(50L, workflow.ObservedToolingTimeMilliseconds, "nested phase values are not summed into wall time");
    }

    private static void DefaultCampaignCollects()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-collecting", "logical-collecting");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events, configuration: new AgentReliabilityCampaignConfiguration("campaign-default", "tc-promoted", DateTimeOffset.UnixEpoch));
        AssertEqual(AgentReliabilityStates.Collecting, projection.State, "default target and missing concurrency remain collecting");
    }

    private static void CompleteCampaignPasses()
    {
        (AgentSnapshot first, List<AgentEvent> firstEvents) = SuccessfulWorkflow("run-pass-a", "logical-pass-a", start: 100, completion: 300);
        (AgentSnapshot second, List<AgentEvent> secondEvents) = SuccessfulWorkflow("run-pass-b", "logical-pass-b", start: 150, completion: 350);
        AgentReliabilityCampaignProjection projection = AgentReliabilityProjection.Build(
            new AgentReliabilityProjectionInput([first, second], [.. firstEvents, .. secondEvents], [], true),
            Campaign(minimum: 2));
        AssertEqual(AgentReliabilityStates.Pass, projection.State, "all conservative acceptance criteria pass");
    }

    private static void UnrecoveredCampaignFails()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-fail-campaign", "logical-fail-campaign");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events, [Issue(snapshot, "issue-fail-campaign", "cause-fail-campaign")], configuration: Campaign(minimum: 1, requireConcurrency: false));
        AssertEqual(AgentReliabilityStates.Fail, projection.State, "unrecovered infrastructure blocker fails campaign");
    }


    private static void HistoryLossIsIncomplete()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-history-loss", "logical-history-loss");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events, historyComplete: false, configuration: Campaign(minimum: 1, requireConcurrency: false));
        AssertEqual(AgentReliabilityStates.Incomplete, projection.State, "bounded history cannot pass silently");
    }

    private static void PromotedFingerprintIsDeterministic()
    {
        AgentToolchainFingerprintEvidence manifest = new(
            ManifestSchemaVersion: AgentObservabilitySchemas.ReliabilityToolchainFingerprint,
            Version: "1",
            State: "promoted",
            Profile: "burn-in",
            Components: new Dictionary<string, string> { ["rimliaison"] = "a", ["devbridge2"] = "b" },
            PromotionCriteria: ["criterion-b", "criterion-a"],
            QualificationArtifact: "artifact.json");
        string first = PromotedToolchainIdentity.ComputeFingerprint(manifest);
        string second = PromotedToolchainIdentity.ComputeFingerprint(manifest with { Components = new Dictionary<string, string> { ["devbridge2"] = "b", ["rimliaison"] = "a" }, PromotionCriteria = ["criterion-a", "criterion-b"] });
        AssertEqual(first, second, "toolchain fingerprint canonicalization is deterministic");
    }

    private static void QualificationFixtureFailureDoesNotCount()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-fixture-failure", "logical-fixture-failure", workloadKind: "qualification");
        events.Insert(events.Count - 1, Event(snapshot, 8, AgentEventTypes.TestFailed, new { qualificationExpected = true, failureSignature = "fixture.runtime.failure" }));
        AgentReliabilityCampaignProjection projection = Project(snapshot, events);
        AssertEqual(0, projection.SourceTestFailureWorkflows, "qualification fixture failure is excluded");
    }

    private static void LegitimateModTestFailureDoesNotFailCampaign()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-legitimate-test", "logical-legitimate-test");
        events.Insert(events.Count - 1, Event(snapshot, 8, AgentEventTypes.TestFailed, new { issueKind = "MOD_DEFECT", failureKind = "test" }));
        AgentReliabilityCampaignProjection projection = Project(snapshot, events, configuration: Campaign(minimum: 1, requireConcurrency: false));
        Assert(projection.State != AgentReliabilityStates.Fail, "legitimate mod test failure does not fail infrastructure reliability");
    }

    private static void ToolchainFingerprintBindsCampaign()
    {
        (AgentSnapshot snapshot, List<AgentEvent> events) = SuccessfulWorkflow("run-bound", "logical-bound", toolchainFingerprint: "tc-b");
        AgentReliabilityCampaignProjection projection = Project(snapshot, events, configuration: Campaign(fingerprint: "tc-a", minimum: 1, requireConcurrency: false));
        Assert(!projection.ContributingWorkflowIds.Contains("run-bound"), "campaign cannot reuse another promoted fingerprint");
    }
    private static void CampaignConfigurationPersists()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-reliability-" + Guid.NewGuid().ToString("N"));
        AgentReliabilityCampaignConfiguration expected = Campaign(minimum: 3);
        try
        {
            using (AgentObservabilityStore first = new(directory))
            {
                first.SaveReliabilityCampaign(expected);
            }

            using AgentObservabilityStore second = new(directory);
            AgentReliabilityCampaignConfiguration? actual =
                second.GetReliabilityCampaign(expected.CampaignId);
            Assert(actual is not null, "campaign configuration is reloaded from canonical observability storage");
            AssertEqual(expected.PromotedToolchainFingerprint, actual!.PromotedToolchainFingerprint, "persisted campaign fingerprint");
            AssertEqual(expected.MinimumWorkflowTarget, actual.MinimumWorkflowTarget, "persisted campaign target");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }


    private static AgentReliabilityCampaignProjection Project(
        IReadOnlyList<AgentSnapshot> snapshots,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue>? issues = null,
        AgentReliabilityCampaignConfiguration? configuration = null) =>
        AgentReliabilityProjection.Build(
            new AgentReliabilityProjectionInput(snapshots, events, issues ?? [], true),
            configuration ?? Campaign(minimum: snapshots.Count, requireConcurrency: true));

    private static AgentReliabilityCampaignProjection Project(
        AgentSnapshot snapshot,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue>? issues = null,
        string? profile = null,
        bool historyComplete = true,
        AgentReliabilityCampaignConfiguration? configuration = null) =>
        AgentReliabilityProjection.Build(
            new AgentReliabilityProjectionInput(
                [snapshot],
                events,
                issues ?? [],
                historyComplete,
                false,
                profile is null ? null : new Dictionary<string, string> { [snapshot.RunId] = profile }),
            configuration ?? Campaign(minimum: 1, requireConcurrency: false));

    private static AgentReliabilityCampaignConfiguration Campaign(
        string fingerprint = "tc-promoted",
        int minimum = 10,
        bool requireConcurrency = true) =>
        new(
            "campaign-test",
            fingerprint,
            DateTimeOffset.UnixEpoch,
            MinimumWorkflowTarget: minimum,
            RequiredCoverage: new AgentReliabilityCoverageRequirements(
                RequireConcurrentProductionActivity: requireConcurrency));

    private static (AgentSnapshot Snapshot, List<AgentEvent> Events) SuccessfulWorkflow(
        string runId,
        string logicalAgentId,
        string workloadKind = "production",
        string toolchainState = "promoted",
        string? toolchainFingerprint = "tc-promoted",
        long start = 100,
        long completion = 200)
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "agent-" + runId,
            LogicalAgentId = logicalAgentId,
            SessionId = "session-" + runId,
            RunId = runId,
            ModId = "mod." + runId,
            EntityType = ObservabilityEntityTypes.Mod,
            CanonicalEntityId = "mod:" + runId,
            DisplayName = runId,
            ModName = runId,
            WorkloadKind = workloadKind,
            ToolchainState = toolchainState,
            ToolchainFingerprint = toolchainFingerprint,
            Status = AgentStatus.Completed,
            CurrentStage = DevelopmentStage.Complete,
            StartTime = start,
            CompletedAt = completion,
            CompletionState = AgentCompletionState.Succeeded,
            CompletionResult = "PASS"
        };
        List<AgentEvent> events =
        [
            Event(snapshot, 1, AgentEventTypes.CommandStarted, new { workflowId = runId, sourceFingerprint = "source-" + runId }),
            Event(snapshot, 2, AgentEventTypes.BuildSucceeded, new { durationMs = 10, sourceFingerprint = "source-" + runId }),
            Event(snapshot, 3, AgentEventTypes.SuiteCompleted, new
            {
                status = "pass",
                artifactFreshness = new
                {
                    sourceFingerprint = "source-" + runId,
                    deploymentDecision = "deployed",
                    evaluationStatus = "FRESH",
                    loadedArtifactFreshnessProven = true,
                    generation = 4
                }
            }),
            Event(snapshot, 4, AgentEventTypes.RuntimeEvidenceCompleted, new { result = "pass", generation = 4 }),
            Event(snapshot, 5, AgentEventTypes.ToolCompleted, new { operationKey = "devbridge.lifecycle.restart", outcome = "success", generation = 4 }),
            Event(snapshot, 6, AgentEventTypes.ValidationEvidenceDecision, new { action = "reuse", evidenceId = "ve-" + runId }),
            Event(snapshot, 7, AgentEventTypes.AgentCompleted, new { outcome = "success" })
        ];
        return (snapshot, events);
    }

    private static AgentEvent RemoveGeneration(AgentEvent value)
    {
        object data = value.Type switch
        {
            AgentEventTypes.SuiteCompleted => new { status = "pass", artifactFreshness = new { sourceFingerprint = "source", deploymentDecision = "deployed", evaluationStatus = "FRESH", loadedArtifactFreshnessProven = true } },
            AgentEventTypes.RuntimeEvidenceCompleted => new { result = "pass" },
            AgentEventTypes.ToolCompleted => new { operationKey = "devbridge.lifecycle.restart", outcome = "success" },
            _ => new { }
        };
        return value with { Data = JsonSerializer.SerializeToElement(data) };
    }

    private static AgentEvent Event(AgentSnapshot snapshot, long sequence, string type, object data) =>
        new()
        {
            Id = "evt-" + snapshot.RunId + "-" + sequence.ToString(),
            RunId = snapshot.RunId,
            AgentId = snapshot.AgentId,
            LogicalAgentId = snapshot.LogicalAgentId,
            SessionId = snapshot.SessionId,
            ModId = snapshot.ModId,
            EntityType = snapshot.EntityType,
            CanonicalEntityId = snapshot.CanonicalEntityId,
            DisplayName = snapshot.DisplayName,
            Timestamp = snapshot.StartTime + sequence,
            Sequence = sequence,
            Stage = DevelopmentStage.Testing,
            Type = type,
            Summary = type,
            Data = JsonSerializer.SerializeToElement(data)
        };

    private static AgentIssue Issue(AgentSnapshot snapshot, string id, string cause, bool recovered = false) =>
        new()
        {
            Id = id,
            RunId = snapshot.RunId,
            AgentId = snapshot.AgentId,
            LogicalAgentId = snapshot.LogicalAgentId,
            SessionId = snapshot.SessionId,
            ModId = snapshot.ModId,
            EntityType = snapshot.EntityType,
            CanonicalEntityId = snapshot.CanonicalEntityId,
            DisplayName = snapshot.DisplayName,
            Timestamp = snapshot.StartTime + 8,
            Category = AgentIssueCategory.ToolingFailure,
            Severity = AgentIssueSeverity.Error,
            Summary = cause,
            CausalIssueKey = cause,
            Fingerprint = "signature." + cause,
            Recovered = recovered,
            Blocking = true
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message + $" (expected {expected}, actual {actual})");
        }
    }
}

