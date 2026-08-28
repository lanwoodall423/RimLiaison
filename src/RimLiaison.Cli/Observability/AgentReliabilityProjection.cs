using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Observability;

public static partial class AgentObservabilitySchemas
{
    public const string ReliabilityWorkflow = "rimliaison-reliability-workflow/v1";
    public const string ReliabilityCampaign = "rimliaison-reliability-campaign/v1";
    public const string ReliabilityCampaignConfiguration = "rimliaison-reliability-campaign-config/v1";
    public const string ReliabilityToolchainFingerprint = "rimliaison-toolchain-fingerprint/v1";
}

public static class AgentReliabilityStates
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Collecting = "COLLECTING";
    public const string Incomplete = "INCOMPLETE";
    public const string Complete = "complete";
    public const string Unknown = "unknown";
    public const string NotCovered = "not-covered";
    public const string Established = "established";
}

public sealed record AgentReliabilityCoverageRequirements(
    bool RequireLiveValidation = true,
    bool RequireRuntimeContentDeployment = true,
    bool RequireControlledRestart = true,
    bool RequireValidationProofReuse = true,
    bool RequireConcurrentProductionActivity = true);

public sealed record AgentReliabilityCampaignConfiguration(
    string CampaignId,
    string PromotedToolchainFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? EndAtUtc = null,
    int MinimumWorkflowTarget = 10,
    AgentReliabilityCoverageRequirements? RequiredCoverage = null)
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } =
        AgentObservabilitySchemas.ReliabilityCampaignConfiguration;

    public AgentReliabilityCoverageRequirements Coverage =>
        RequiredCoverage ?? new AgentReliabilityCoverageRequirements();
}

public sealed record AgentReliabilityProjectionOptions(
    bool? HistoryComplete = null,
    bool? HistoryDegraded = null,
    string? EfficiencyProfileDirectory = null,
    IReadOnlyDictionary<string, string>? EfficiencyProfileJsonByRunId = null);

public sealed record AgentReliabilityProjectionInput(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyList<AgentIssue> Issues,
    bool HistoryComplete = true,
    bool HistoryDegraded = false,
    IReadOnlyDictionary<string, string>? EfficiencyProfileJsonByRunId = null);

public sealed record AgentReliabilityPhaseTiming(
    long CumulativeMilliseconds,
    long Runs,
    long Failures,
    bool MayOverlapNestedActivities = true);

public sealed record AgentReliabilityWorkflowRecord(
    string SchemaVersion,
    string WorkflowId,
    string RunId,
    string AgentId,
    string? LogicalAgentId,
    string? SessionId,
    string ModId,
    string EntityType,
    string CanonicalEntityId,
    string DisplayName,
    string WorkloadKind,
    string ToolchainState,
    string? ToolchainFingerprint,
    string ToolchainBinding,
    string? SourceIdentity,
    long StartTimestamp,
    long? CompletionTimestamp,
    string TerminalOutcome,
    string EvidenceState,
    IReadOnlyList<string> MissingEvidence,
    int InfrastructureIncidentCount,
    int RecoveredInfrastructureIncidentCount,
    int UnrecoveredInfrastructureIncidentCount,
    IReadOnlyList<string> FailureSignatures,
    int SourceTestFailureCount,
    bool? BuildOccurred,
    string? BuildResult,
    bool? DeploymentOccurred,
    bool? RuntimeContentDeploymentOccurred,
    string? DeploymentResult,
    string? DeploymentFreshnessResult,
    bool? LiveValidationOccurred,
    string? LiveValidationResult,
    bool? RestartOccurred,
    string? RestartResult,
    bool ProofReuseOccurred,
    IReadOnlyList<int> RuntimeGenerations,
    int RuntimeGenerationCount,
    long? RimLiaisonWallTimeMilliseconds,
    long? ObservedToolingTimeMilliseconds,
    long? ObservedBuildTimeMilliseconds,
    long? ObservedDeploymentTimeMilliseconds,
    long? ObservedLifecycleRuntimeWaitMilliseconds,
    long? ObservedRecoveryTimeMilliseconds,
    long? ObservedValidationTimeMilliseconds,
    IReadOnlyDictionary<string, AgentReliabilityPhaseTiming> PhaseTimings,
    bool TimingEvidenceComplete,
    bool StaleDeploymentRuntimeIdentityViolation,
    string ConcurrencyEvidenceState);

public sealed record AgentReliabilityCampaignProjection(
    string SchemaVersion,
    AgentReliabilityCampaignConfiguration Configuration,
    string State,
    string ProjectionEvidenceState,
    IReadOnlyList<AgentReliabilityWorkflowRecord> Workflows,
    IReadOnlyList<string> ContributingWorkflowIds,
    IReadOnlyDictionary<string, string> ExcludedWorkflowIds,
    int ProductionWorkflowsObserved,
    int CompletedSuccessfully,
    int SourceTestFailureWorkflows,
    int ToolingInfrastructureIncidents,
    int RecoveredInfrastructureIncidents,
    int UnrecoveredInfrastructureIncidents,
    IReadOnlyList<string> FailureSignatures,
    int ExactPromotedToolchainWorkflows,
    int ExperimentalToolchainWorkflows,
    int UnknownToolchainWorkflows,
    int CompleteEvidenceWorkflows,
    bool LiveValidationCovered,
    bool RuntimeContentDeploymentCovered,
    bool ControlledRestartCovered,
    bool ProofReuseCovered,
    string ConcurrentProductionActivityState,
    int DistinctSimultaneousLogicalAgents,
    int RuntimeGenerationCount,
    long? ObservedRimLiaisonWallTimeMilliseconds,
    long? ObservedToolingTimeMilliseconds,
    IReadOnlyList<string> MissingRequiredEvidenceOrCoverage,
    bool StaleDeploymentRuntimeIdentityViolation,
    bool HistoryComplete,
    bool HistoryDegraded);

public sealed record AgentToolchainFingerprintEvidence(
    [property: JsonPropertyName("schemaVersion")] string ManifestSchemaVersion,
    string Version,
    string State,
    string Profile,
    IReadOnlyDictionary<string, string> Components,
    IReadOnlyList<string> PromotionCriteria,
    string? QualificationArtifact)
{
    [JsonIgnore]
    public string Fingerprint { get; init; } = string.Empty;
}

public static class PromotedToolchainIdentity
{
    public static string ComputeFingerprint(AgentToolchainFingerprintEvidence manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var builder = new StringBuilder();
        Append(builder, AgentObservabilitySchemas.ReliabilityToolchainFingerprint);
        Append(builder, manifest.ManifestSchemaVersion);
        Append(builder, manifest.Version);
        Append(builder, manifest.State);
        Append(builder, manifest.Profile);
        foreach ((string key, string value) in manifest.Components.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, key);
            Append(builder, value);
        }
        Append(builder, "<components>");
        foreach (string criterion in manifest.PromotionCriteria.OrderBy(static value => value, StringComparer.Ordinal))
        {
            Append(builder, criterion);
        }
        Append(builder, "<criteria>");
        Append(builder, manifest.QualificationArtifact);
        return "tc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static bool TryLoadFingerprint(string? repositoryRoot, out string? fingerprint)
    {
        foreach (string root in CandidateRoots(repositoryRoot))
        {
            string path = Path.Combine(root, "qualification", "toolchain-known-good.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                FileInfo file = new(path);
                if (file.Length <= 64 * 1024)
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    AgentToolchainFingerprintEvidence? manifest = JsonSerializer.Deserialize<AgentToolchainFingerprintEvidence>(
                        bytes,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (manifest is not null &&
                        string.Equals(manifest.State, "promoted", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(manifest.Version))
                    {
                        fingerprint = ComputeFingerprint(manifest);
                        return true;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        fingerprint = null;
        return false;
    }

    private static IEnumerable<string> CandidateRoots(string? repositoryRoot)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in new[]
        {
            repositoryRoot,
            Environment.GetEnvironmentVariable("RIMTEST_ROOT"),
            AppContext.BaseDirectory
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string? current;
            try
            {
                current = Path.GetFullPath(value);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            for (int depth = 0; current is not null && depth < 6; depth++)
            {
                if (seen.Add(current))
                {
                    yield return current;
                }
                current = Directory.GetParent(current)?.FullName;
            }
        }
    }

    private static void Append(StringBuilder builder, string? value) =>
        builder.Append(value ?? string.Empty).Append('\0');
}

public static class AgentReliabilityProjection
{
    private const int MaximumEvents = 50_000;
    private const int MaximumIssues = 10_000;
    private const int MaximumProfiles = 32;
    private const int MaximumProfileBytes = 16 * 1024;

    public static AgentReliabilityCampaignProjection Build(
        IAgentObservabilityStore store,
        AgentReliabilityCampaignConfiguration configuration,
        AgentReliabilityProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new AgentReliabilityProjectionOptions();
        bool historyComplete = options.HistoryComplete ??
            (store is IAgentObservabilityHistoryStatus historyStatus ? historyStatus.HistoryComplete : false);
        bool historyDegraded = options.HistoryDegraded ??
            (store is IAgentObservabilityHistoryStatus degradedStatus && degradedStatus.HistoryDegraded);
        IReadOnlyDictionary<string, string>? profiles = options.EfficiencyProfileJsonByRunId;
        if (profiles is null && !string.IsNullOrWhiteSpace(options.EfficiencyProfileDirectory))
        {
            profiles = ReadProfiles(options.EfficiencyProfileDirectory!);
        }

        return Build(
            new AgentReliabilityProjectionInput(
                store.GetAgents(limit: 10_000),
                store.GetEvents(limit: MaximumEvents),
                store.GetIssues(includeRecovered: true, limit: MaximumIssues),
                historyComplete,
                historyDegraded,
                profiles),
            configuration);
    }

    public static AgentReliabilityCampaignProjection Build(
        AgentReliabilityProjectionInput input,
        AgentReliabilityCampaignConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration);

        AgentSnapshot[] agents = input.Agents
            .GroupBy(static agent => (agent.RunId, agent.AgentId))
            .Select(static group => group.OrderByDescending(static agent => agent.LastActivityAt ?? agent.StartTime).First())
            .OrderBy(static agent => agent.RunId, StringComparer.Ordinal)
            .ThenBy(static agent => agent.AgentId, StringComparer.Ordinal)
            .ToArray();
        Dictionary<(string RunId, string AgentId), AgentEvent[]> events = input.Events
            .GroupBy(static value => (value.RunId, value.AgentId))
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static value => value.Sequence).ThenBy(static value => value.Id, StringComparer.Ordinal).ToArray());
        Dictionary<(string RunId, string AgentId), AgentIssue[]> issues = input.Issues
            .GroupBy(static value => (value.RunId, value.AgentId))
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static value => value.Timestamp).ThenBy(static value => value.Id, StringComparer.Ordinal).ToArray());

        var workflows = new List<AgentReliabilityWorkflowRecord>(agents.Length);
        foreach (AgentSnapshot agent in agents)
        {
            events.TryGetValue((agent.RunId, agent.AgentId), out AgentEvent[]? workflowEvents);
            issues.TryGetValue((agent.RunId, agent.AgentId), out AgentIssue[]? workflowIssues);
            workflowEvents ??= [];
            workflowIssues ??= [];
            string? profile = input.EfficiencyProfileJsonByRunId is not null &&
                input.EfficiencyProfileJsonByRunId.TryGetValue(agent.RunId, out string? suppliedProfile)
                    ? suppliedProfile
                    : null;
            workflows.Add(BuildWorkflow(agent, workflowEvents, workflowIssues, profile, input));
        }

        return BuildCampaign(workflows, configuration, input.HistoryComplete, input.HistoryDegraded);
    }

    private static AgentReliabilityWorkflowRecord BuildWorkflow(
        AgentSnapshot agent,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue> issues,
        string? profileJson,
        AgentReliabilityProjectionInput input)
    {
        AgentEvent? first = events.OrderBy(static value => value.Timestamp).ThenBy(static value => value.Sequence).FirstOrDefault();
        AgentEvent? terminal = events
            .Where(static value => value.Type is AgentEventTypes.AgentCompleted or AgentEventTypes.AgentFailed)
            .OrderByDescending(static value => value.Sequence)
            .FirstOrDefault();
        string workflowId = events.Select(GetWorkflowId).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? agent.RunId;
        string? sourceIdentity = FirstValue(events, "sourceFingerprint") ??
            events.Select(value => GetNestedString(value.Data, "artifactFreshness", "sourceFingerprint"))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        string? toolchainFingerprint = Normalize(agent.ToolchainFingerprint);
        string binding = Normalize(agent.WorkloadKind) != "production"
            ? "not-applicable"
            : Normalize(agent.ToolchainState) == "experimental"
                ? "experimental"
                : string.IsNullOrWhiteSpace(toolchainFingerprint)
                    ? "unknown"
                    : "promoted";
        string terminalOutcome = TerminalOutcome(agent, terminal);
        List<string> missing = [];
        if (input.HistoryDegraded || !input.HistoryComplete)
        {
            missing.Add("observability.history");
        }
        if (agent.StartTime <= 0)
        {
            missing.Add("workflow.startTime");
        }
        if (terminal is null && agent.CompletedAt is null)
        {
            missing.Add("workflow.terminalOutcome");
        }
        if (Normalize(agent.WorkloadKind) == "production" && string.IsNullOrWhiteSpace(toolchainFingerprint))
        {
            missing.Add("toolchain.fingerprint");
        }
        if (Normalize(agent.WorkloadKind) == "production" && string.IsNullOrWhiteSpace(sourceIdentity))
        {
            missing.Add("source.identity");
        }

        List<Incident> incidents = InfrastructureIncidents(issues, events);
        string[] signatures = incidents
            .Select(static incident => incident.Signature)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray()!;
        int sourceTestFailures = events.Count(IsSourceTestFailure);
        bool? buildOccurred = BuildOccurred(events);
        string? buildResult = BuildResult(events);
        bool? deploymentOccurred = DeploymentOccurred(events);
        bool? runtimeContentDeployment = RuntimeContentDeploymentOccurred(events);
        string? deploymentResult = DeploymentResult(events);
        string? freshness = DeploymentFreshness(events);
        bool? liveValidation = LiveValidationOccurred(events);
        string? liveResult = LiveValidationResult(events);
        bool? restart = RestartOccurred(events);
        string? restartResult = RestartResult(events);
        bool proofReuse = ProofReuseOccurred(events);
        int[] generations = RuntimeGenerations(events).OrderBy(static value => value).ToArray();
        bool staleViolation = StaleRuntimeViolation(events);
        if (staleViolation)
        {
            missing.Remove("deployment.freshness");
        }

        IReadOnlyDictionary<string, AgentReliabilityPhaseTiming> phaseTimings = ParsePhaseTimings(profileJson);
        long? profileWall = ProfileNumber(profileJson, "outcome", "wallTimeMs");
        long? wall = ExplicitWallTime(events) ?? profileWall;
        long? tooling = profileWall ?? wall;
        bool timingComplete = profileJson is not null && phaseTimings.Count > 0;
        string evidenceState = missing.Count == 0
            ? AgentReliabilityStates.Complete
            : AgentReliabilityStates.Incomplete;
        string concurrency = agent.LogicalAgentId is null || agent.CompletedAt is null
            ? AgentReliabilityStates.Unknown
            : AgentReliabilityStates.NotCovered;

        return new AgentReliabilityWorkflowRecord(
            AgentObservabilitySchemas.ReliabilityWorkflow,
            workflowId,
            agent.RunId,
            agent.AgentId,
            agent.LogicalAgentId,
            agent.SessionId,
            agent.ModId,
            agent.EntityType,
            agent.CanonicalEntityId,
            agent.DisplayName,
            Normalize(agent.WorkloadKind) ?? "unknown",
            Normalize(agent.ToolchainState) ?? "unknown",
            toolchainFingerprint,
            binding,
            Normalize(sourceIdentity),
            Math.Max(0, agent.StartTime),
            agent.CompletedAt,
            terminalOutcome,
            evidenceState,
            missing.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            incidents.Count,
            incidents.Count(static incident => incident.Recovered),
            incidents.Count(static incident => !incident.Recovered),
            signatures,
            sourceTestFailures,
            buildOccurred,
            buildResult,
            deploymentOccurred,
            runtimeContentDeployment,
            deploymentResult,
            freshness,
            liveValidation,
            liveResult,
            restart,
            restartResult,
            proofReuse,
            generations,
            generations.Length,
            wall,
            tooling,
            PhaseValue(phaseTimings, "build"),
            PhaseValue(phaseTimings, "deploy") ?? PhaseValue(phaseTimings, "deployment"),
            PhaseValue(phaseTimings, "lifecycle") ?? PhaseValue(phaseTimings, "runtime"),
            PhaseValue(phaseTimings, "recovery"),
            PhaseValue(phaseTimings, "validation"),
            phaseTimings,
            timingComplete,
            staleViolation,
            concurrency);
    }

    private static AgentReliabilityCampaignProjection BuildCampaign(
        IReadOnlyList<AgentReliabilityWorkflowRecord> workflows,
        AgentReliabilityCampaignConfiguration configuration,
        bool historyComplete,
        bool historyDegraded)
    {
        AgentReliabilityWorkflowRecord[] production = workflows
            .Where(static workflow => workflow.WorkloadKind == "production")
            .OrderBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .ThenBy(static workflow => workflow.RunId, StringComparer.Ordinal)
            .ToArray();
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        var contributing = new List<AgentReliabilityWorkflowRecord>();
        int experimental = 0;
        int unknown = 0;
        foreach (AgentReliabilityWorkflowRecord workflow in workflows
                     .Where(static workflow => workflow.WorkloadKind != "production")
                     .OrderBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal))
        {
            excluded[workflow.WorkflowId] = "qualification-workload";
        }

        foreach (AgentReliabilityWorkflowRecord workflow in production)
        {
            string exclusion = workflow.ToolchainBinding switch
            {
                "experimental" => "experimental-toolchain",
                "unknown" => "unknown-toolchain-identity",
                "promoted" when !string.Equals(
                    workflow.ToolchainFingerprint,
                    configuration.PromotedToolchainFingerprint,
                    StringComparison.Ordinal) => "promoted-fingerprint-mismatch",
                _ => string.Empty
            };
            if (exclusion.Length > 0)
            {
                excluded[workflow.WorkflowId] = exclusion;
                if (exclusion == "experimental-toolchain")
                {
                    experimental++;
                }
                else if (exclusion == "unknown-toolchain-identity")
                {
                    unknown++;
                }
                continue;
            }
            contributing.Add(workflow);
        }

        (string concurrencyState, int simultaneousAgents) = ConcurrencyCoverage(contributing);
        bool live = contributing.Any(static workflow => workflow.LiveValidationOccurred == true);
        bool runtimeDeployment = contributing.Any(static workflow => workflow.RuntimeContentDeploymentOccurred == true);
        bool restart = contributing.Any(static workflow => workflow.RestartOccurred == true);
        bool proofReuse = contributing.Any(static workflow => workflow.ProofReuseOccurred);
        int incidents = contributing.Sum(static workflow => workflow.InfrastructureIncidentCount);
        int recovered = contributing.Sum(static workflow => workflow.RecoveredInfrastructureIncidentCount);
        int unrecovered = contributing.Sum(static workflow => workflow.UnrecoveredInfrastructureIncidentCount);
        bool staleViolation = contributing.Any(static workflow => workflow.StaleDeploymentRuntimeIdentityViolation);
        string[] signatures = contributing
            .SelectMany(static workflow => workflow.FailureSignatures)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        if (!historyComplete)
        {
            missing.Add("observability.history");
        }
        if (historyDegraded)
        {
            missing.Add("observability.degraded");
        }
        foreach (AgentReliabilityWorkflowRecord workflow in contributing)
        {
            foreach (string value in workflow.MissingEvidence)
            {
                missing.Add(workflow.WorkflowId + ":" + value);
            }
        }
        foreach (AgentReliabilityWorkflowRecord workflow in production
                     .Where(workflow => workflow.ToolchainBinding == "unknown"))
        {
            missing.Add(workflow.WorkflowId + ":toolchain.fingerprint");
        }
        if (configuration.Coverage.RequireLiveValidation && !live)
        {
            missing.Add("coverage.live-validation");
        }
        if (configuration.Coverage.RequireRuntimeContentDeployment && !runtimeDeployment)
        {
            missing.Add("coverage.runtime-content-deployment");
        }
        if (configuration.Coverage.RequireControlledRestart && !restart)
        {
            missing.Add("coverage.controlled-restart");
        }
        if (configuration.Coverage.RequireValidationProofReuse && !proofReuse)
        {
            missing.Add("coverage.validation-proof-reuse");
        }
        if (configuration.Coverage.RequireConcurrentProductionActivity &&
            concurrencyState != AgentReliabilityStates.Established)
        {
            missing.Add("coverage.concurrent-production-activity:" + concurrencyState);
        }
        if (staleViolation)
        {
            missing.Add("violation.stale-deployment-runtime-identity");
        }
        if (unrecovered > 0)
        {
            missing.Add("violation.unrecovered-tooling-incident");
        }

        int complete = contributing.Count(static workflow => workflow.EvidenceState == AgentReliabilityStates.Complete);
        string state;
        if (staleViolation || unrecovered > 0)
        {
            state = AgentReliabilityStates.Fail;
        }
        else if (!historyComplete || historyDegraded || unknown > 0 || contributing.Any(static workflow => workflow.EvidenceState != AgentReliabilityStates.Complete))
        {
            state = AgentReliabilityStates.Incomplete;
        }
        else if (contributing.Count < configuration.MinimumWorkflowTarget || missing.Any())
        {
            state = AgentReliabilityStates.Collecting;
        }
        else
        {
            state = AgentReliabilityStates.Pass;
        }

        long? wall = SumNullable(contributing.Select(static workflow => workflow.RimLiaisonWallTimeMilliseconds));
        long? tooling = SumNullable(contributing.Select(static workflow => workflow.ObservedToolingTimeMilliseconds));
        return new(
            AgentObservabilitySchemas.ReliabilityCampaign,
            configuration,
            state,
            state == AgentReliabilityStates.Incomplete || !historyComplete || historyDegraded
                ? AgentReliabilityStates.Incomplete
                : AgentReliabilityStates.Complete,
            workflows,
            contributing.Select(static workflow => workflow.WorkflowId).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            excluded,
            production.Length,
            contributing.Count(static workflow => workflow.TerminalOutcome == "succeeded"),
            contributing.Count(static workflow => workflow.SourceTestFailureCount > 0),
            incidents,
            recovered,
            unrecovered,
            signatures,
            contributing.Count,
            experimental,
            unknown,
            complete,
            live,
            runtimeDeployment,
            restart,
            proofReuse,
            concurrencyState,
            simultaneousAgents,
            contributing.Sum(static workflow => workflow.RuntimeGenerationCount),
            wall,
            tooling,
            missing.ToArray(),
            staleViolation,
            historyComplete,
            historyDegraded);
    }

    private static (string State, int DistinctAgents) ConcurrencyCoverage(
        IReadOnlyList<AgentReliabilityWorkflowRecord> workflows)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentReliabilityWorkflowRecord left in workflows)
        {
            if (string.IsNullOrWhiteSpace(left.LogicalAgentId) || left.CompletionTimestamp is null)
            {
                continue;
            }
            HashSet<int> leftGenerations = Generations(left);
            foreach (AgentReliabilityWorkflowRecord right in workflows)
            {
                if (string.Equals(left.WorkflowId, right.WorkflowId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(right.LogicalAgentId) || right.CompletionTimestamp is null ||
                    string.Equals(left.LogicalAgentId, right.LogicalAgentId, StringComparison.Ordinal))
                {
                    continue;
                }
                long leftEnd = left.CompletionTimestamp.Value;
                long rightEnd = right.CompletionTimestamp.Value;
                if (Math.Min(leftEnd, rightEnd) <= Math.Max(left.StartTimestamp, right.StartTimestamp) ||
                    !leftGenerations.Overlaps(Generations(right)))
                {
                    continue;
                }
                observed.Add(left.LogicalAgentId!);
                observed.Add(right.LogicalAgentId!);
            }
        }
        if (observed.Count >= 2)
        {
            return (AgentReliabilityStates.Established, observed.Count);
        }
        if (workflows.Count < 2)
        {
            return (AgentReliabilityStates.NotCovered, observed.Count);
        }
        return (AgentReliabilityStates.Unknown, observed.Count);
    }
    private static HashSet<int> Generations(AgentReliabilityWorkflowRecord workflow) =>
        workflow.RuntimeGenerations.ToHashSet();
    private static string TerminalOutcome(AgentSnapshot agent, AgentEvent? terminal)
    {
        if (agent.CompletionState == AgentCompletionState.Succeeded || terminal?.Type == AgentEventTypes.AgentCompleted)
        {
            return "succeeded";
        }
        if (agent.CompletionState == AgentCompletionState.Cancelled)
        {
            return "cancelled";
        }
        if (agent.CompletionState == AgentCompletionState.ValidationIncomplete)
        {
            return "validation-incomplete";
        }
        if (agent.CompletionState == AgentCompletionState.Failed || terminal?.Type == AgentEventTypes.AgentFailed)
        {
            return "failed";
        }
        return "unknown";
    }

    private static List<Incident> InfrastructureIncidents(
        IReadOnlyList<AgentIssue> issues,
        IReadOnlyList<AgentEvent> events)
    {
        var incidents = new Dictionary<string, Incident>(StringComparer.Ordinal);
        foreach (AgentIssue issue in issues.Where(IsInfrastructureIssue))
        {
            string key = issue.CausalIssueKey ?? issue.Fingerprint ?? "issue:" + issue.Id;
            string signature = issue.Fingerprint ?? issue.CausalIssueKey ??
                FirstIssueCode(issue, events) ?? string.Empty;
            bool recovered = issue.Recovered;
            if (incidents.TryGetValue(key, out Incident? existing))
            {
                incidents[key] = existing with
                {
                    Recovered = existing.Recovered && recovered,
                    Signature = string.IsNullOrWhiteSpace(existing.Signature) ? signature : existing.Signature
                };
            }
            else
            {
                incidents[key] = new Incident(recovered, signature);
            }
        }
        if (incidents.Count > 0)
        {
            return incidents.Values.ToList();
        }

        foreach (AgentEvent value in events.Where(IsExplicitInfrastructureFailure))
        {
            string key = FailureKey(value);
            string signature = FirstValue(value, "fingerprint", "causalIssueKey", "underlyingErrorCode", "errorCode") ?? string.Empty;
            bool recovered = string.Equals(FirstValue(value, "recoveryState"), "recovered", StringComparison.OrdinalIgnoreCase) ||
                events.Any(candidate => candidate.Type == AgentEventTypes.RecoveryCompleted &&
                    string.Equals(FailureKey(candidate), key, StringComparison.Ordinal));
            incidents[key] = new Incident(recovered, signature);
        }
        return incidents.Values.ToList();
    }

    private static bool IsInfrastructureIssue(AgentIssue issue) =>
        issue.Category is AgentIssueCategory.ToolingFailure or AgentIssueCategory.CapabilityGap or AgentIssueCategory.IntegrationIssue ||
        string.Equals(issue.Classification, "TOOLING_FAILURE", StringComparison.Ordinal) ||
        string.Equals(issue.Classification, "INFRASTRUCTURE_FAILURE", StringComparison.Ordinal);

    private static string? FirstIssueCode(AgentIssue issue, IReadOnlyList<AgentEvent> events) =>
        issue.EventIds.Select(id => events.FirstOrDefault(value => value.Id == id))
            .Where(static value => value is not null)
            .Select(value => FirstValue(value!, "underlyingErrorCode", "errorCode"))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsExplicitInfrastructureFailure(AgentEvent value)
    {
        if (AgentObservabilityData.GetBoolean(value.Data, "lifecycleOnly"))
        {
            return false;
        }
        return string.Equals(FirstValue(value, "issueKind", "classification", "failureKind"), "TOOLING_FAILURE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FirstValue(value, "failureKind"), "infrastructure", StringComparison.OrdinalIgnoreCase) ||
            AgentObservabilityData.GetBoolean(value.Data, "infrastructureFailure");
    }

    private static string FailureKey(AgentEvent value) =>
        FirstValue(value, "causalIssueKey", "causalFailureId", "fingerprint", "underlyingErrorCode", "errorCode") ??
        "event:" + value.Id;

    private static bool IsSourceTestFailure(AgentEvent value)
    {
        if (AgentObservabilityData.GetBoolean(value.Data, "qualificationExpected") ||
            IsExplicitInfrastructureFailure(value))
        {
            return false;
        }
        if (string.Equals(FirstValue(value, "issueKind"), "MOD_DEFECT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Type == AgentEventTypes.TestFailed)
        {
            return true;
        }
        if (value.Type == AgentEventTypes.SuiteCompleted &&
            string.Equals(FirstValue(value, "status", "result"), "fail", StringComparison.OrdinalIgnoreCase))
        {
            return !AgentObservabilityData.GetBoolean(value.Data, "infrastructureFailure") &&
                FirstValue(value, "failureKind") is not "infrastructure";
        }
        return false;
    }

    private static bool? BuildOccurred(IReadOnlyList<AgentEvent> events)
    {
        if (!events.Any(static value => value.Type is AgentEventTypes.BuildStarted or AgentEventTypes.BuildSucceeded or AgentEventTypes.BuildFailed))
        {
            return null;
        }
        return true;
    }

    private static string? BuildResult(IReadOnlyList<AgentEvent> events) =>
        events.LastOrDefault(static value => value.Type is AgentEventTypes.BuildSucceeded or AgentEventTypes.BuildFailed) is AgentEvent value
            ? value.Type == AgentEventTypes.BuildSucceeded ? "succeeded" : "failed"
            : null;

    private static bool? DeploymentOccurred(IReadOnlyList<AgentEvent> events) =>
        events.Any(value => NestedObject(value.Data, "artifactFreshness") || NestedObject(value.Data, "deployment")) ? true : null;

    private static bool? RuntimeContentDeploymentOccurred(IReadOnlyList<AgentEvent> events) =>
        events.Any(value => string.Equals(
            FirstValue(value, "deploymentDecision") ?? GetNestedString(value.Data, "artifactFreshness", "deploymentDecision"),
            "deployed",
            StringComparison.OrdinalIgnoreCase)) ? true : null;

    private static string? DeploymentResult(IReadOnlyList<AgentEvent> events) =>
        events.Select(value => FirstValue(value, "deploymentResult", "deploymentDecision") ??
                GetNestedString(value.Data, "artifactFreshness", "deploymentDecision"))
            .LastOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? DeploymentFreshness(IReadOnlyList<AgentEvent> events)
    {
        foreach (AgentEvent value in events.Reverse())
        {
            bool? proven = AgentObservabilityData.GetNullableBoolean(
                NestedElement(value.Data, "artifactFreshness"),
                "loadedArtifactFreshnessProven") ??
                AgentObservabilityData.GetNullableBoolean(value.Data, "loadedArtifactFreshnessProven");
            string? status = FirstValue(value, "freshnessState", "evaluationStatus") ??
                GetNestedString(value.Data, "artifactFreshness", "evaluationStatus");
            if (proven == true || string.Equals(status, "FRESH", StringComparison.OrdinalIgnoreCase))
            {
                return "fresh";
            }
            if (proven == false || status is "STALE" or "FAILED" or "MISMATCH")
            {
                return "stale-or-mismatched";
            }
        }
        return null;
    }

    private static bool? LiveValidationOccurred(IReadOnlyList<AgentEvent> events) =>
        events.Any(value => value.Type == AgentEventTypes.RuntimeEvidenceCompleted ||
            NestedObject(value.Data, "runtimeValidation") ||
            string.Equals(FirstValue(value, "validationKind"), "runtime", StringComparison.OrdinalIgnoreCase)) ? true : null;

    private static string? LiveValidationResult(IReadOnlyList<AgentEvent> events) =>
        events.Where(value => value.Type == AgentEventTypes.RuntimeEvidenceCompleted ||
                NestedObject(value.Data, "runtimeValidation") ||
                string.Equals(FirstValue(value, "validationKind"), "runtime", StringComparison.OrdinalIgnoreCase))
            .Select(value => FirstValue(value, "result", "status", "outcome") ??
                GetNestedString(value.Data, "runtimeValidation", "status"))
            .LastOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static bool? RestartOccurred(IReadOnlyList<AgentEvent> events)
    {
        AgentEvent[] candidates = events.Where(value =>
                value.Type == AgentEventTypes.RecoveryCompleted &&
                    (AgentObservabilityData.GetBoolean(value.Data, "restart") ||
                     AgentObservabilityData.GetBoolean(value.Data, "restartOccurred")) ||
                value.Type is AgentEventTypes.ToolCompleted or AgentEventTypes.ToolFailed or AgentEventTypes.ToolException &&
                    (FirstValue(value, "operationKey")?.Contains("restart", StringComparison.OrdinalIgnoreCase) == true))
            .ToArray();
        return candidates.Length == 0 ? null : true;
    }

    private static string? RestartResult(IReadOnlyList<AgentEvent> events) =>
        events.Where(value => RestartOccurred([value]) == true)
            .Select(value => FirstValue(value, "outcome", "status") ??
                (value.Type == AgentEventTypes.RecoveryCompleted ? "succeeded" : "failed"))
            .LastOrDefault();

    private static bool ProofReuseOccurred(IReadOnlyList<AgentEvent> events) =>
        events.Any(value => value.Type == AgentEventTypes.EvidenceReused ||
            value.Type == AgentEventTypes.ValidationEvidenceDecision &&
                string.Equals(FirstValue(value, "action"), "reuse", StringComparison.OrdinalIgnoreCase) ||
            AgentObservabilityData.GetStrings(value.Data, "reusedTests").Count > 0 ||
            AgentObservabilityData.GetStrings(value.Data, "reusedSuites").Count > 0);

    private static IEnumerable<int> RuntimeGenerations(IReadOnlyList<AgentEvent> events)
    {
        foreach (AgentEvent value in events)
        {
            long? generation = AgentObservabilityData.GetInt64(value.Data, "generation") ??
                AgentObservabilityData.GetInt64(NestedElement(value.Data, "artifactFreshness"), "generation");
            if (generation is >= 1 and <= int.MaxValue)
            {
                yield return (int)generation.Value;
            }
        }
    }

    private static bool StaleRuntimeViolation(IReadOnlyList<AgentEvent> events)
    {
        bool stale = events.Any(value =>
            DeploymentFreshness([value]) == "stale-or-mismatched" ||
            string.Equals(FirstValue(value, "errorCode", "underlyingErrorCode"), "RIMTEST_ARTIFACT_GENERATION_MISMATCH", StringComparison.OrdinalIgnoreCase));
        bool runtimePass = events.Any(value =>
            (value.Type == AgentEventTypes.RuntimeEvidenceCompleted || NestedObject(value.Data, "runtimeValidation") ||
             string.Equals(FirstValue(value, "validationKind"), "runtime", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(FirstValue(value, "result", "status", "outcome") ??
                GetNestedString(value.Data, "runtimeValidation", "status"), "pass", StringComparison.OrdinalIgnoreCase));
        return stale && runtimePass;
    }

    private static long? ExplicitWallTime(IReadOnlyList<AgentEvent> events) =>
        events.Where(static value => value.Type is AgentEventTypes.CommandCompleted or AgentEventTypes.CommandFailed)
            .Select(static value => AgentObservabilityData.GetInt64(value.Data, "durationMs"))
            .LastOrDefault(static value => value.HasValue);

    private static IReadOnlyDictionary<string, AgentReliabilityPhaseTiming> ParsePhaseTimings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, AgentReliabilityPhaseTiming>(StringComparer.Ordinal);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("phaseTiming", out JsonElement phases) ||
                phases.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, AgentReliabilityPhaseTiming>(StringComparer.Ordinal);
            }
            var result = new Dictionary<string, AgentReliabilityPhaseTiming>(StringComparer.Ordinal);
            foreach (JsonElement phase in phases.EnumerateArray())
            {
                string? name = StringProperty(phase, "phase");
                long? duration = LongProperty(phase, "cumulativeMs");
                if (string.IsNullOrWhiteSpace(name) || !duration.HasValue)
                {
                    continue;
                }
                result[name] = new(
                    Math.Max(0, duration.Value),
                    Math.Max(0, LongProperty(phase, "runs") ?? 0),
                    Math.Max(0, LongProperty(phase, "failures") ?? 0));
            }
            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, AgentReliabilityPhaseTiming>(StringComparer.Ordinal);
        }
    }

    private static long? PhaseValue(IReadOnlyDictionary<string, AgentReliabilityPhaseTiming> values, string name) =>
        values.TryGetValue(name, out AgentReliabilityPhaseTiming? value) ? value.CumulativeMilliseconds : null;

    private static long? ProfileNumber(string? json, string section, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(section, out JsonElement value)
                ? LongProperty(value, property)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadProfiles(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "rimliaison-*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(static value => value, StringComparer.Ordinal)
                         .Take(MaximumProfiles))
            {
                FileInfo file = new(path);
                if (file.Length > MaximumProfileBytes)
                {
                    continue;
                }
                string json = File.ReadAllText(path);
                using JsonDocument document = JsonDocument.Parse(json);
                string? runId = document.RootElement.TryGetProperty("identity", out JsonElement identity)
                    ? StringProperty(identity, "runId")
                    : null;
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    result[runId] = json;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return result;
    }

    private static string? GetWorkflowId(AgentEvent value) =>
        FirstValue(value, "workflowId") ?? GetNestedString(value.Data, "artifactFreshness", "workflowId");

    private static string? FirstValue(IEnumerable<AgentEvent> events, params string[] names) =>
        events.Select(value => FirstValue(value, names))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstValue(AgentEvent value, params string[] names) =>
        FirstValue(value.Data, names);

    private static string? FirstValue(JsonElement? data, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = AgentObservabilityData.GetString(data, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
    private static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? LongProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out long result)
            ? result
            : null;


    private static string? GetNestedString(JsonElement? data, string parent, string child) =>
        FirstValue(NestedElement(data, parent), child);

    private static bool NestedObject(JsonElement? data, string name) =>
        NestedElement(data, name).HasValue;

    private static JsonElement? NestedElement(JsonElement? data, string name)
    {
        if (data is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? SumNullable(IEnumerable<long?> values)
    {
        long total = 0;
        bool found = false;
        foreach (long? value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }
            total = checked(total + Math.Max(0, value.Value));
            found = true;
        }
        return found ? total : null;
    }

    private static void ValidateConfiguration(AgentReliabilityCampaignConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.CampaignId) ||
            string.IsNullOrWhiteSpace(configuration.PromotedToolchainFingerprint) ||
            configuration.MinimumWorkflowTarget < 1)
        {
            throw new ArgumentException("A bounded campaign identity and positive workflow target are required.", nameof(configuration));
        }
    }

    private sealed record Incident(bool Recovered, string Signature);
}

public interface IAgentObservabilityHistoryStatus
{
    bool HistoryComplete { get; }
    bool HistoryDegraded { get; }
}

public interface IAgentReliabilityCampaignStore
{
    AgentReliabilityCampaignConfiguration? GetReliabilityCampaign(string campaignId);
    void SaveReliabilityCampaign(AgentReliabilityCampaignConfiguration configuration);
}
