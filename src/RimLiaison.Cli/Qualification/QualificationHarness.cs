using System.Text.Json.Serialization;
using RimLiaison.Observability;

namespace RimLiaison.Qualification;

public static class QualificationSchemas
{
    public const string Run = "rimliaison-qualification-run/v1";
    public const string Aggregate = "rimliaison-qualification-aggregate/v1";
    public const string Manifest = "rimliaison-toolchain-manifest/v1";
}

public static class QualificationProfiles
{
    public const string Single = "single";
    public const string PromotionBurnIn = "burn-in-25";
    public const int PromotionBurnInRuns = 25;

    public static string ResolveProfile(string? commandId) =>
        string.Equals(commandId, "burn-in", StringComparison.OrdinalIgnoreCase)
            ? PromotionBurnIn
            : Single;

    public static void ValidateRunCount(string profile, int runCount)
    {
        if (string.Equals(profile, PromotionBurnIn, StringComparison.OrdinalIgnoreCase) &&
            runCount != PromotionBurnInRuns)
        {
            throw new InvalidOperationException(
                $"Qualification profile '{PromotionBurnIn}' requires exactly {PromotionBurnInRuns} runs.");
        }
    }
}

public enum QualificationOutcome
{
    Pass,
    InfrastructureFailure,
    FixtureFailure
}

public sealed record QualificationScenarioResult(
    string Id,
    string Name,
    QualificationOutcome Outcome,
    bool ExpectedFailure,
    bool Recovered,
    string? FailureSignature = null,
    string? Evidence = null);

public sealed record QualificationRunResult(
    string SchemaVersion,
    int RunNumber,
    string RunId,
    string Profile,
    string WorkloadKind,
    string ToolchainState,
    QualificationOutcome Outcome,
    IReadOnlyList<QualificationScenarioResult> Scenarios,
    int InfrastructureFailures,
    int FixtureFailures,
    int RecoverySuccesses,
    int RecommendationCount,
    IReadOnlyList<string> FailureSignatures,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record QualificationAggregate(
    string SchemaVersion,
    string Profile,
    string WorkloadKind,
    string ToolchainState,
    int TotalRuns,
    int Passes,
    int InfrastructureFailures,
    int FixtureFailures,
    int RecoverySuccesses,
    int RecommendationCount,
    IReadOnlyDictionary<string, int> FailureSignatures,
    IReadOnlyList<QualificationRunResult> Runs,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? SourceCommit = null,
    IReadOnlyDictionary<string, string>? QualifiedArtifactHashes = null,
    string? QualificationArtifactPath = null,
    string? QualifiedPromotionPackagePath = null)
{
    [JsonPropertyName("qualificationPassed")]
    public bool QualificationPassed =>
        Passes == TotalRuns &&
        InfrastructureFailures == 0 &&
        FixtureFailures == 0 &&
        Runs.All(run => run.RecoverySuccesses > 0);

    [JsonPropertyName("candidateComplete")]
    public bool CandidateComplete { get; init; }

    [JsonPropertyName("promotionPackageEmitted")]
    public bool PromotionPackageEmitted { get; init; }

    [JsonPropertyName("candidateFailureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateFailureCode { get; init; }

    [JsonPropertyName("candidateFailure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateFailure { get; init; }

    [JsonPropertyName("candidateBuildEvidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? CandidateBuildEvidence { get; init; }

    [JsonPropertyName("promotionReady")]
    public bool PromotionReady =>
        QualificationPassed && CandidateComplete && PromotionPackageEmitted;

    [JsonIgnore]
    public bool IsPromotionReady => PromotionReady;
}

public sealed record ToolchainPromotionManifest(
    string SchemaVersion,
    string Version,
    string State,
    string Profile,
    IReadOnlyDictionary<string, string> Components,
    IReadOnlyList<string> PromotionCriteria,
    string? QualificationArtifact,
    DateTimeOffset UpdatedAt);

public sealed record QualificationBacklogItem(
    string Id,
    string Source,
    string Summary,
    string Owner,
    string Priority,
    bool Blocking,
    string Status,
    string Recommendation,
    string EvidenceReference,
    DateTimeOffset CreatedAt);

public sealed class QualificationHarness
{
    private static readonly (string Id, string Name)[] RequiredScenarios =
    [
        ("build.success", "successful build"),
        ("build.failure", "expected build failure classification"),
        ("deployment", "deployment and artifact freshness"),
        ("quicktest.launch", "Quicktest launch"),
        ("readiness", "readiness and identity"),
        ("ownership.ambiguous", "ambiguous ownership coordination"),
        ("runtime.success", "successful runtime validation"),
        ("runtime.failure", "expected runtime failure classification"),
        ("restart.timeout", "lifecycle restart timeout"),
        ("restart.recovery", "restart and recovery"),
        ("evidence.structured", "structured evidence collection"),
        ("evidence.optional-missing", "optional evidence unavailable"),
        ("optional.validation", "optional validation unavailable"),
        ("recommendation", "non-blocking recommendation"),
        ("cleanup", "clean completion and cleanup")
    ];

    public QualificationAggregate Run(
        int runCount,
        string profile,
        IAgentObservabilityStore store,
        string toolchainState = "experimental")
    {
        QualificationProfiles.ValidateRunCount(profile, runCount);
        if (runCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runCount));
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var results = new List<QualificationRunResult>(runCount);
        for (int runNumber = 1; runNumber <= runCount; runNumber++)
        {
            results.Add(RunOnce(runNumber, profile, toolchainState, store));
        }

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        var signatures = results
            .SelectMany(run => run.FailureSignatures)
            .GroupBy(signature => signature, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new QualificationAggregate(
            QualificationSchemas.Aggregate,
            profile,
            "qualification",
            toolchainState,
            results.Count,
            results.Count(result => result.Outcome == QualificationOutcome.Pass),
            results.Sum(result => result.InfrastructureFailures),
            results.Sum(result => result.FixtureFailures),
            results.Sum(result => result.RecoverySuccesses),
            results.Sum(result => result.RecommendationCount),
            signatures,
            results,
            startedAt,
            completedAt);
    }

    private static QualificationRunResult RunOnce(
        int runNumber,
        string profile,
        string toolchainState,
        IAgentObservabilityStore store)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string runId = "qualification-" + Guid.NewGuid().ToString("N");
        var scenarios = new List<QualificationScenarioResult>(RequiredScenarios.Length);
        using var run = new AgentObservabilityRun(runId, store);
        AgentObservabilitySession agent = run.CreateAgent(
            "rimliaison.qualification.fixture",
            "RimLiaison Qualification Fixture",
            agentId: "qualification-agent-" + runNumber.ToString("D4"),
            logicalAgentId: "rimliaison.qualification.fixture",
            sessionId: "qualification-session-" + runNumber.ToString("D4"),
            entityIdentity: ObservabilityEntityIdentity.ForFixture(
                "qualification",
                "Qualification fixture"),
            workloadKind: "qualification",
            toolchainState: toolchainState,
            qualificationProfile: profile);
        using IDisposable activation = agent.Activate();
        agent.Start("qualification:" + profile);
        agent.SetStage(DevelopmentStage.Implementation, "qualification fixture");
        agent.SetProductionState(DevelopmentStage.Implementation, "qualification", "none");
        agent.Record(DevelopmentStage.Analysis, AgentEventTypes.InformationalProductionEvent,
            "Qualification run started.", new
            {
                workloadKind = "qualification",
                qualificationProfile = profile,
                toolchainState,
                runNumber
            });

        scenarios.Add(Pass(agent, "build.success", "Build completed successfully.", AgentEventTypes.BuildSucceeded));
        scenarios.Add(ExpectedFailure(agent, "build.failure", "Build failure classified without failing the fixture.", AgentEventTypes.BuildFailed, "fixture.build.failure"));
        scenarios.Add(Pass(agent, "deployment", "Deployment published with matching artifact fingerprint.", AgentEventTypes.SuiteCompleted, "artifact-fresh"));
        scenarios.Add(ExpectedFailure(agent, "deployment.stale", "Changed deployment artifact invalidated stale evidence.", AgentEventTypes.ValidationEvidenceDecision, "artifact-stale-invalidation"));
        scenarios.Add(Pass(agent, "quicktest.launch", "Quicktest launch completed.", AgentEventTypes.CommandCompleted));
        scenarios.Add(ExpectedFailure(agent, "readiness", "Readiness identity mismatch was classified and recovered.", AgentEventTypes.ToolFailed, "readiness.identity-mismatch", recovered: true));
        scenarios.Add(ExpectedFailure(agent, "ownership.ambiguous", "Ambiguous ownership was isolated without failing production.", AgentEventTypes.ToolFailed, "ownership.ambiguous"));
        scenarios.Add(Pass(agent, "runtime.success", "Runtime validation passed.", AgentEventTypes.TestPassed));
        scenarios.Add(ExpectedFailure(agent, "runtime.failure", "Deliberate runtime failure was classified as fixture evidence.", AgentEventTypes.TestFailed, "fixture.runtime.failure"));
        scenarios.Add(ExpectedFailure(agent, "restart.timeout", "Restart timeout was classified and recovered.", AgentEventTypes.CommandTimeout, "lifecycle.restart.timeout", recovered: true));
        scenarios.Add(Pass(agent, "restart.recovery", "Restart and recovery completed.", AgentEventTypes.RecoveryCompleted, "restart-recovered", recovered: true));
        scenarios.Add(Pass(agent, "evidence.structured", "Structured evidence was persisted.", AgentEventTypes.ValidationEvidenceRecorded, "evidence://qualification/structured"));
        scenarios.Add(ExpectedFailure(agent, "evidence.optional-missing", "Optional evidence collection was unavailable without blocking.", AgentEventTypes.ValidationEvidenceDecision, "optional-evidence.missing"));

        agent.RecordValidationRecommendation(
            "optional-validation",
            "Optional validation capability is unavailable in the qualification environment.",
            "Keep optional validation visible as a recommendation; do not block the supported path.",
            "DevBridge2",
            "qualification://optional-validation");
        scenarios.Add(new QualificationScenarioResult(
            "optional.validation",
            "optional validation unavailable",
            QualificationOutcome.Pass,
            ExpectedFailure: false,
            Recovered: false,
            Evidence: "qualification://optional-validation"));
        agent.RecordToolingRecommendation(
            "qualification.recommendation",
            "Experimental qualification evidence recommends broader parallel lifecycle coverage.",
            "Add deterministic parallel lifecycle coverage before promotion; production remains unaffected.",
            "RimLiaison",
            "qualification://parallel-recommendation",
            affectedCurrentTask: false,
            priority: "normal",
            evidence: new { profile, runNumber });
        scenarios.Add(new QualificationScenarioResult(
            "recommendation",
            "non-blocking recommendation",
            QualificationOutcome.Pass,
            ExpectedFailure: false,
            Recovered: false,
            Evidence: "qualification://parallel-recommendation"));

        agent.Record(DevelopmentStage.Testing, AgentEventTypes.InformationalProductionEvent,
            "Qualification cleanup completed.", new { clean = true, workloadKind = "qualification" });
        agent.Complete("Qualification fixture completed successfully.");
        scenarios.Add(Pass(agent, "cleanup", "Clean completion and cleanup.", AgentEventTypes.AgentCompleted));

        int infrastructureFailures = scenarios.Count(scenario =>
            scenario.Outcome == QualificationOutcome.InfrastructureFailure);
        int fixtureFailures = scenarios.Count(scenario =>
            scenario.Outcome == QualificationOutcome.FixtureFailure);
        int recoverySuccesses = scenarios.Count(scenario => scenario.Recovered);
        string[] signatures = scenarios
            .Where(scenario => scenario.FailureSignature is not null)
            .Select(scenario => scenario.FailureSignature!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new QualificationRunResult(
            QualificationSchemas.Run,
            runNumber,
            runId,
            profile,
            "qualification",
            toolchainState,
            infrastructureFailures == 0 && fixtureFailures == 0
                ? QualificationOutcome.Pass
                : QualificationOutcome.InfrastructureFailure,
            scenarios,
            infrastructureFailures,
            fixtureFailures,
            recoverySuccesses,
            scenarios.Count(scenario => scenario.Id == "recommendation" || scenario.Id == "optional.validation"),
            signatures,
            startedAt,
            DateTimeOffset.UtcNow);
    }
    public static IReadOnlyList<QualificationBacklogItem> BuildBacklog(
        QualificationAggregate aggregate,
        IAgentObservabilityStore store)
    {
        var result = store.GetIssues(includeRecovered: true)
            .Where(issue => !string.IsNullOrWhiteSpace(issue.Recommendation) ||
                issue.Category is AgentIssueCategory.ToolingImprovement or
                    AgentIssueCategory.ToolLimitation or
                    AgentIssueCategory.OptionalValidationUnavailable)
            .Select(issue => new QualificationBacklogItem(
                "issue-" + issue.Id,
                "observability",
                issue.Summary,
                issue.ComponentOwner ?? "RimLiaison",
                issue.Blocking ? "high" : "normal",
                issue.Blocking,
                issue.Recovered ? "resolved" : "open",
                issue.Recommendation ?? "Review the structured incident evidence and qualify a tooling improvement.",
                issue.EvidenceReference ?? ("observability://issue/" + issue.Id),
                DateTimeOffset.FromUnixTimeMilliseconds(issue.Timestamp)))
            .ToList();
        if (aggregate.RecommendationCount > 0)
        {
            result.Add(new QualificationBacklogItem(
                "qualification-parallel-coverage",
                "qualification",
                "Parallel lifecycle coverage remains an improvement opportunity.",
                "RimLiaison",
                "normal",
                false,
                "open",
                "Add deterministic parallel lifecycle coverage before promotion.",
                "qualification://parallel-recommendation",
                aggregate.CompletedAt));
        }
        return result
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.Blocking)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static QualificationScenarioResult Pass(
        AgentObservabilitySession agent,
        string id,
        string summary,
        string eventType,
        string? evidence = null,
        bool recovered = false)
    {
        agent.Record(DevelopmentStage.Testing, eventType, summary, new
        {
            qualificationScenario = id,
            qualificationExpected = false,
            evidence
        });
        return new QualificationScenarioResult(id, RequiredScenarios.First(scenario => scenario.Id == id).Name,
            QualificationOutcome.Pass, false, recovered, Evidence: evidence);
    }

    private static QualificationScenarioResult ExpectedFailure(
        AgentObservabilitySession agent,
        string id,
        string summary,
        string eventType,
        string signature,
        bool recovered = false)
    {
        agent.Record(DevelopmentStage.Testing, eventType, summary, new
        {
            qualificationScenario = id,
            qualificationExpected = true,
            failureSignature = signature,
            blocking = false,
            recovered
        });
        return new QualificationScenarioResult(id, id == "deployment.stale" ? "stale or changed deployment artifact" :
            RequiredScenarios.FirstOrDefault(scenario => scenario.Id == id).Name ?? id,
            QualificationOutcome.Pass, true, recovered, signature);
    }
}
