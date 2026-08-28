using System.Globalization;

namespace RimLiaison.Observability;

public sealed record AgentReliabilityCoverageRow(
    string Id,
    string Label,
    string State,
    bool Required,
    string? Detail = null);

public sealed record AgentReliabilityTimingView(
    long? RimLiaisonWallTimeMilliseconds,
    long? ObservedToolingTimeMilliseconds,
    long? ObservedBuildTimeMilliseconds,
    long? ObservedDeploymentTimeMilliseconds,
    long? ObservedLifecycleRuntimeWaitMilliseconds,
    long? ObservedRecoveryTimeMilliseconds,
    long? ObservedValidationTimeMilliseconds,
    bool Complete,
    string Definition);

public sealed record AgentReliabilityWorkflowView(
    AgentReliabilityWorkflowRecord Workflow,
    string InfrastructureState,
    string ToolchainDisplay,
    string? NavigationAgentId,
    string? NavigationRunId,
    IReadOnlyList<string> IncidentSignatures,
    string EvidenceDisplay);

public sealed record AgentReliabilityObservabilityView(
    string CurrentPromotedToolchainFingerprint,
    string? CurrentPromotedToolchainVersion,
    AgentReliabilityCampaignProjection? Campaign,
    IReadOnlyList<AgentReliabilityCampaignConfiguration> HistoricalCampaigns,
    IReadOnlyList<AgentReliabilityCoverageRow> Coverage,
    AgentReliabilityTimingView Timing,
    IReadOnlyList<AgentReliabilityWorkflowView> Workflows,
    string? EmptyState,
    bool CanStartCampaign)
{
    public string CampaignState => Campaign?.State ?? AgentReliabilityStates.Incomplete;
    public int QualifyingWorkflowCount => Campaign?.ContributingWorkflowIds.Count ?? 0;
    public int QualifyingWorkflowTarget => Campaign?.Configuration.MinimumWorkflowTarget ?? 0;
    public int RecoveredIncidentCount => Campaign?.RecoveredInfrastructureIncidents ?? 0;
    public int UnrecoveredIncidentCount => Campaign?.UnrecoveredInfrastructureIncidents ?? 0;
    public int TotalIncidentCount => Campaign?.ToolingInfrastructureIncidents ?? 0;
    public string RecoveryRateDisplay => TotalIncidentCount == 0
        ? "0 / 0 = unavailable"
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0} / {1} = {2:P0}",
            RecoveredIncidentCount,
            TotalIncidentCount,
            (double)RecoveredIncidentCount / TotalIncidentCount);
}

public static class AgentReliabilityCampaignOperations
{
    public static AgentReliabilityCampaignConfiguration? FindActive(
        IAgentReliabilityCampaignStore store,
        string promotedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetReliabilityCampaigns()
            .Where(value => value.StartedAtUtc is not null && value.EndAtUtc is null)
            .Where(value => string.Equals(
                value.PromotedToolchainFingerprint,
                promotedFingerprint,
                StringComparison.Ordinal))
            .OrderByDescending(value => value.StartedAtUtc)
            .ThenBy(value => value.CampaignId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static AgentReliabilityCampaignConfiguration Start(
        IAgentReliabilityCampaignStore store,
        string promotedFingerprint,
        DateTimeOffset nowUtc,
        int minimumWorkflowTarget = 10)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(promotedFingerprint))
        {
            throw new ArgumentException("A promoted toolchain fingerprint is required.", nameof(promotedFingerprint));
        }

        AgentReliabilityCampaignConfiguration? existing = FindActive(store, promotedFingerprint);
        if (existing is not null)
        {
            return existing;
        }

        foreach (AgentReliabilityCampaignConfiguration campaign in store.GetReliabilityCampaigns()
                     .Where(value => value.StartedAtUtc is not null && value.EndAtUtc is null)
                     .Where(value => !string.Equals(
                         value.PromotedToolchainFingerprint,
                         promotedFingerprint,
                         StringComparison.Ordinal)))
        {
            store.SaveReliabilityCampaign(campaign with { EndAtUtc = nowUtc });
        }

        string suffix = promotedFingerprint.StartsWith("tc-", StringComparison.Ordinal)
            ? promotedFingerprint[3..]
            : promotedFingerprint;
        string campaignId = "reliability-" + suffix[..Math.Min(16, suffix.Length)];
        string baseId = campaignId;
        int ordinal = 2;
        while (store.GetReliabilityCampaign(campaignId) is not null)
        {
            campaignId = baseId + "-" + ordinal.ToString(CultureInfo.InvariantCulture);
            ordinal++;
        }

        AgentReliabilityCampaignConfiguration created = new(
            campaignId,
            promotedFingerprint,
            nowUtc,
            nowUtc,
            null,
            minimumWorkflowTarget,
            new AgentReliabilityCoverageRequirements());
        store.SaveReliabilityCampaign(created);
        return created;
    }

    public static AgentReliabilityCampaignConfiguration Archive(
        IAgentReliabilityCampaignStore store,
        string campaignId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        AgentReliabilityCampaignConfiguration campaign = store.GetReliabilityCampaign(campaignId) ??
            throw new InvalidOperationException("Reliability campaign was not found: " + campaignId);
        AgentReliabilityCampaignConfiguration archived = campaign with { EndAtUtc = nowUtc };
        store.SaveReliabilityCampaign(archived);
        return archived;
    }
}

public static class AgentReliabilityObservabilityProjection
{
    public static AgentReliabilityObservabilityView Build(
        IAgentObservabilityStore store,
        string promotedFingerprint,
        string? promotedVersion = null,
        AgentReliabilityProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(promotedFingerprint);
        AgentReliabilityCampaignConfiguration? active = AgentReliabilityCampaignOperations.FindActive(
            (IAgentReliabilityCampaignStore)store,
            promotedFingerprint);
        AgentReliabilityCampaignProjection? campaign = active is null
            ? null
            : AgentReliabilityProjection.Build(store, active, options);
        IReadOnlyList<AgentReliabilityCampaignConfiguration> history =
            ((IAgentReliabilityCampaignStore)store).GetReliabilityCampaigns()
                .Where(value => active is null || value.CampaignId != active.CampaignId)
                .OrderByDescending(value => value.StartedAtUtc ?? value.CreatedAtUtc)
                .ThenBy(value => value.CampaignId, StringComparer.Ordinal)
                .Take(64)
                .ToArray();
        return Build(campaign, history, promotedFingerprint, promotedVersion);
    }

    public static AgentReliabilityObservabilityView Build(
        AgentReliabilityCampaignProjection? campaign,
        IReadOnlyList<AgentReliabilityCampaignConfiguration> history,
        string promotedFingerprint,
        string? promotedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        AgentReliabilityCoverageRequirements required = campaign?.Configuration.Coverage ??
            new AgentReliabilityCoverageRequirements();
        var coverage = new List<AgentReliabilityCoverageRow>
        {
            new("production-workflows", "Production workflow count", campaign is null
                ? AgentReliabilityStates.Incomplete
                : campaign.ProductionWorkflowsObserved >= campaign.Configuration.MinimumWorkflowTarget
                    ? AgentReliabilityStates.Pass
                    : AgentReliabilityStates.Incomplete, true,
                campaign is null ? "campaign not started" : $"{campaign.ProductionWorkflowsObserved} / {campaign.Configuration.MinimumWorkflowTarget}"),
            new("live-validation", "Real live validation", Coverage(campaign?.LiveValidationCovered, required.RequireLiveValidation), required.RequireLiveValidation),
            new("runtime-deployment", "Runtime deployment", Coverage(campaign?.RuntimeContentDeploymentCovered, required.RequireRuntimeContentDeployment), required.RequireRuntimeContentDeployment),
            new("controlled-restart", "Controlled restart", Coverage(campaign?.ControlledRestartCovered, required.RequireControlledRestart), required.RequireControlledRestart),
            new("proof-reuse", "Proof/cache reuse", Coverage(campaign?.ProofReuseCovered, required.RequireValidationProofReuse), required.RequireValidationProofReuse),
            new("concurrent-agents", "Concurrent-agent operation", campaign is null
                ? AgentReliabilityStates.Incomplete
                : campaign.ConcurrentProductionActivityState == AgentReliabilityStates.Established
                    ? AgentReliabilityStates.Pass
                    : campaign.ConcurrentProductionActivityState == AgentReliabilityStates.NotCovered
                        ? AgentReliabilityStates.Missing
                        : AgentReliabilityStates.Incomplete, required.RequireConcurrentProductionActivity,
                campaign is null ? null : campaign.ConcurrentProductionActivityState),
            new("promoted-toolchain", "Promoted toolchain usage", campaign is null
                ? AgentReliabilityStates.Incomplete
                : campaign.ProductionWorkflowsObserved > 0 &&
                    campaign.UnknownToolchainWorkflows == 0 &&
                    campaign.ExactPromotedToolchainWorkflows == campaign.ProductionWorkflowsObserved
                    ? AgentReliabilityStates.Pass
                    : AgentReliabilityStates.Incomplete, true),
            new("fresh-deployment", "No stale/mismatched deployment", campaign is null
                ? AgentReliabilityStates.Incomplete
                : campaign.ProductionWorkflowsObserved > 0 &&
                    !campaign.StaleDeploymentRuntimeIdentityViolation
                    ? AgentReliabilityStates.Pass
                    : AgentReliabilityStates.Incomplete, true),
            new("clean-start", "Clean start", AgentReliabilityStates.Missing, false, "authoritative evidence unavailable; non-blocking recommendation")
        };
        AgentReliabilityTimingView timing = Timing(campaign);
        IReadOnlyList<AgentReliabilityWorkflowView> workflows = campaign?.Workflows
            .Where(value => value.WorkloadKind == "production")
            .OrderByDescending(value => value.StartTimestamp)
            .ThenBy(value => value.WorkflowId, StringComparer.Ordinal)
            .Take(100)
            .Select(value => new AgentReliabilityWorkflowView(
                value,
                value.UnrecoveredInfrastructureIncidentCount > 0
                    ? "BLOCKED"
                    : value.InfrastructureIncidentCount > 0
                        ? "RECOVERED"
                        : value.EvidenceState == AgentReliabilityStates.Complete ? "CLEAN" : "INCOMPLETE",
                value.ToolchainBinding + (value.ToolchainFingerprint is null ? string.Empty : " · " + value.ToolchainFingerprint),
                value.AgentId,
                value.RunId,
                value.FailureSignatures,
                value.EvidenceState))
            .ToArray() ?? [];
        string? emptyState = campaign is null
            ? "No active campaign for the currently promoted toolchain. Start one to collect production evidence."
            : null;
        return new(
            promotedFingerprint,
            promotedVersion,
            campaign,
            history,
            coverage,
            timing,
            workflows,
            emptyState,
            campaign is null);
    }

    private static string Coverage(bool? covered, bool required) =>
        !required ? AgentReliabilityStates.NotCovered : covered == true
            ? AgentReliabilityStates.Pass
            : covered == false ? AgentReliabilityStates.Missing : AgentReliabilityStates.Incomplete;

    private static AgentReliabilityTimingView Timing(AgentReliabilityCampaignProjection? campaign)
    {
        IReadOnlyList<AgentReliabilityWorkflowRecord> workflows = campaign?.Workflows
            .Where(value => value.WorkloadKind == "production" && campaign.ContributingWorkflowIds.Contains(value.WorkflowId, StringComparer.Ordinal))
            .ToArray() ?? [];
        long? Sum(Func<AgentReliabilityWorkflowRecord, long?> selector)
        {
            long total = 0;
            bool found = false;
            foreach (AgentReliabilityWorkflowRecord workflow in workflows)
            {
                long? value = selector(workflow);
                if (value is null) continue;
                total += Math.Max(0, value.Value);
                found = true;
            }
            return found ? total : null;
        }
        bool complete = workflows.Count > 0 && workflows.All(value => value.TimingEvidenceComplete);
        return new(
            Sum(value => value.RimLiaisonWallTimeMilliseconds),
            Sum(value => value.ObservedToolingTimeMilliseconds),
            Sum(value => value.ObservedBuildTimeMilliseconds),
            Sum(value => value.ObservedDeploymentTimeMilliseconds),
            Sum(value => value.ObservedLifecycleRuntimeWaitMilliseconds),
            Sum(value => value.ObservedRecoveryTimeMilliseconds),
            Sum(value => value.ObservedValidationTimeMilliseconds),
            complete,
            "Wall time is command duration; tooling time is the efficiency profile outcome. Phase values are cumulative per phase and are not summed into tooling time.");
    }
}
