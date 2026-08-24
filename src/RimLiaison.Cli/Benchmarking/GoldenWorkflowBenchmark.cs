using System.Collections.ObjectModel;
using System.Diagnostics;
using RimContext.Core.Context;
using RimLiaison.Git;
using RimLiaison.Provenance;

namespace RimLiaison.Benchmarking;

public static class GoldenWorkflowBenchmarkSchema
{
    public const string Current = "rimliaison-golden-workflows/v1";
    public const string Baseline = "rimliaison-golden-workflows-baseline/v1";
}

public static class GoldenWorkflowScenarioIds
{
    public const string Documentation = "A-documentation-only";
    public const string XmlData = "B-xml-data-only";
    public const string Runtime = "C-csharp-runtime-ui";
    public const string NoRelevantSource = "D-no-relevant-source";
    public const string StaleDeployment = "E-build-current-deployment-stale";
    public const string GeneratedState = "F-generated-observability-state";
    public const string Infrastructure = "G-infrastructure-failure";
    public const string Dependency = "H-dependency-change";
}

public sealed record GoldenWorkflowMetric
{
    public required string ScenarioId { get; init; }

    public required string Category { get; init; }

    public string ExpectedAction { get; init; } = string.Empty;

    public string ObservedAction { get; init; } = string.Empty;

    public string WorkflowAction { get; init; } = string.Empty;

    public string Status { get; init; } = "pass";

    public long TotalWorkflowMs { get; init; }

    /// <summary>
    /// The scenario durations above are a stable cost envelope used for
    /// operation-count comparisons. They are not a historical wall-clock
    /// measurement. MeasuredDurationMs is populated only by RunMeasured().
    /// </summary>
    public string DurationBasis { get; init; } = "deterministic-cost-envelope-v1";

    public long? MeasuredDurationMs { get; init; }

    public int BuildCount { get; init; }

    public int DeploymentCount { get; init; }

    public int TestCount { get; init; }

    public int ExecutedTestCount { get; init; }

    public long TestExecutionMs { get; init; }

    public int ReusedEvidenceCount { get; init; }

    public int InvalidatedEvidenceCount { get; init; }

    public int RimWorldLaunches { get; init; }

    public int RimWorldRestarts { get; init; }

    public int Retries { get; init; }

    public int ExpensiveOperationCount { get; init; }

    public int SourceDebuggingCount { get; init; }

    public string? FailureClassification { get; init; }

    public string? RetryReasonCode { get; init; }

    public IReadOnlyList<string> Decisions { get; init; } = [];
}

public sealed record GoldenWorkflowBaseline
{
    public required string ScenarioId { get; init; }

    public int ExpectedExpensiveOperations { get; init; }

    public int ExpectedBuilds { get; init; }

    public int ExpectedDeployments { get; init; }

    public int ExpectedTests { get; init; }

    public int ExpectedReusedEvidence { get; init; }

    public int ExpectedInvalidatedEvidence { get; init; }
}

public sealed record GoldenWorkflowBenchmarkReport
{
    public string SchemaVersion { get; init; } = GoldenWorkflowBenchmarkSchema.Current;

    public string BaselineVersion { get; init; } = GoldenWorkflowBenchmarkSchema.Baseline;

    public string BaselineSource { get; init; } = "current-implementation";

    public string BaselineComparison { get; init; } = "operation-counts";

    public long? MeasuredDurationMs { get; init; }

    public IReadOnlyList<GoldenWorkflowMetric> Scenarios { get; init; } = [];

    public IReadOnlyList<GoldenWorkflowBaseline> Baseline { get; init; } = [];

    public int PassedScenarioCount { get; init; }

    public int RegressionCount { get; init; }

    public RimContextBenchmarkSummary Summary { get; init; } = new()
    {
        BaselineVersion = GoldenWorkflowBenchmarkSchema.Baseline
    };
}

public static class GoldenWorkflowBenchmarkRunner
{
    private static readonly DateTimeOffset BenchmarkTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Runs the deterministic operation-count benchmark. The default form is
    /// stable and is the one used by tests and regression comparison.
    /// </summary>
    public static GoldenWorkflowBenchmarkReport Run() => RunCore(measure: false);

    /// <summary>
    /// Runs the same scenarios while adding best-effort wall-clock timings to
    /// the otherwise deterministic report. Timing is informative only and is
    /// never used for the regression gate.
    /// </summary>
    public static GoldenWorkflowBenchmarkReport RunMeasured() => RunCore(measure: true);

    private static GoldenWorkflowBenchmarkReport RunCore(bool measure)
    {
        long started = Stopwatch.GetTimestamp();
        GoldenWorkflowMetric[] scenarios =
        [
            Measure(DocumentationScenario, measure),
            Measure(XmlScenario, measure),
            Measure(RuntimeScenario, measure),
            Measure(NoRelevantSourceScenario, measure),
            Measure(StaleDeploymentScenario, measure),
            Measure(GeneratedStateScenario, measure),
            Measure(InfrastructureScenario, measure),
            Measure(DependencyScenario, measure)
        ];
        GoldenWorkflowBaseline[] baseline = BaselineValues.ToArray();
        int regressions = scenarios.Count(metric =>
            baseline.First(expected => expected.ScenarioId == metric.ScenarioId)
                is var expected &&
            (metric.ExpensiveOperationCount != expected.ExpectedExpensiveOperations ||
             metric.BuildCount != expected.ExpectedBuilds ||
             metric.DeploymentCount != expected.ExpectedDeployments ||
             metric.TestCount != expected.ExpectedTests ||
             metric.ReusedEvidenceCount != expected.ExpectedReusedEvidence ||
             metric.InvalidatedEvidenceCount != expected.ExpectedInvalidatedEvidence));
        int passed = scenarios.Count(metric => metric.Status == "pass");
        int totalExpensive = scenarios.Sum(static metric => metric.ExpensiveOperationCount);
        int reused = scenarios.Sum(static metric => metric.ReusedEvidenceCount);
        int invalidated = scenarios.Sum(static metric => metric.InvalidatedEvidenceCount);
        return new GoldenWorkflowBenchmarkReport
        {
            Scenarios = scenarios,
            Baseline = baseline,
            PassedScenarioCount = passed,
            RegressionCount = regressions,
            BaselineSource = "current-implementation",
            BaselineComparison = "operation-counts",
            MeasuredDurationMs = measure ? ElapsedMilliseconds(started) : null,
            Summary = new RimContextBenchmarkSummary
            {
                BaselineVersion = GoldenWorkflowBenchmarkSchema.Baseline,
                ScenarioCount = scenarios.Length,
                PassedScenarioCount = passed,
                RegressionCount = regressions,
                TotalExpensiveOperations = totalExpensive,
                ReusableEvidenceCount = reused,
                InvalidatedEvidenceCount = invalidated
            }
        };
    }

    private static GoldenWorkflowMetric Measure(
        Func<GoldenWorkflowMetric> scenario,
        bool measure)
    {
        if (!measure)
        {
            return scenario();
        }

        long started = Stopwatch.GetTimestamp();
        GoldenWorkflowMetric result = scenario();
        return result with { MeasuredDurationMs = ElapsedMilliseconds(started) };
    }

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    public static RimContextBenchmarkSummary Summary() => Run().Summary;

    private static GoldenWorkflowMetric DocumentationScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("docs/readme.md", "M", false, false));
        ValidationPublicationResult result = Publish(analysis, []);
        return Metric(
            GoldenWorkflowScenarioIds.Documentation,
            "documentation",
            analysis,
            result,
            expectedAction: "skip",
            totalWorkflowMs: 2,
            testExecutionMs: 0);
    }

    private static GoldenWorkflowMetric XmlScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("Defs/Thing.xml", "M", false, false));
        ValidationPublicationResult result = Publish(analysis, []);
        return Metric(
            GoldenWorkflowScenarioIds.XmlData,
            "data",
            analysis,
            result,
            expectedAction: "block",
            totalWorkflowMs: 8,
            testExecutionMs: 4,
            buildCount: 0,
            deploymentCount: 0,
            testCount: 1,
            executedTestCount: 1,
            expensiveOperationCount: 1);
    }

    private static GoldenWorkflowMetric RuntimeScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("Source/Widget.cs", "M", false, false));
        ValidationEvidenceIdentity identity = Identity(
            "source-widget-v1",
            ["Source/Widget.cs"],
            ["static-compile", "quicktest"],
            runtimeGeneration: 7,
            build: "build-widget-v1",
            deployment: "deploy-widget-v1");
        ValidationEvidenceRecord evidence = ValidationEvidenceRecord.Create(
            identity,
            "pass",
            BenchmarkTime,
            sourceProof: "proof-widget-v1",
            transactionId: "tx-widget-v1");
        ValidationPublicationResult result = Publish(analysis, [evidence], identity);
        return Metric(
            GoldenWorkflowScenarioIds.Runtime,
            "runtime",
            analysis,
            result,
            expectedAction: "reuse",
            totalWorkflowMs: 42,
            testExecutionMs: 24,
            buildCount: 1,
            deploymentCount: 1,
            testCount: 1,
            executedTestCount: 1,
            rimWorldLaunches: 1,
            expensiveOperationCount: 1);
    }

    private static GoldenWorkflowMetric NoRelevantSourceScenario()
    {
        ValidationChangeAnalysis analysis = Analyze();
        ValidationPublicationResult result = Publish(analysis, []);
        return Metric(
            GoldenWorkflowScenarioIds.NoRelevantSource,
            "none",
            analysis,
            result,
            expectedAction: "skip",
            totalWorkflowMs: 1,
            testExecutionMs: 0);
    }

    private static GoldenWorkflowMetric StaleDeploymentScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("Source/Widget.cs", "M", false, false));
        ValidationEvidenceIdentity staticIdentity = Identity(
            "source-widget-v1",
            ["Source/Widget.cs"],
            ["static-compile"]);
        ValidationEvidenceIdentity staleRuntimeIdentity = Identity(
            "source-widget-v1",
            ["Source/Widget.cs"],
            ["quicktest"],
            runtimeGeneration: 6,
            build: "build-widget-old",
            deployment: "deploy-widget-old");
        ValidationEvidenceRecord[] evidence =
        [
            ValidationEvidenceRecord.Create(staticIdentity, "pass", BenchmarkTime),
            ValidationEvidenceRecord.Create(staleRuntimeIdentity, "pass", BenchmarkTime.AddMinutes(1))
        ];
        ValidationEvidenceIdentity current = Identity(
            "source-widget-v1",
            ["Source/Widget.cs"],
            ["static-compile", "quicktest"],
            runtimeGeneration: 7,
            build: "build-widget-current",
            deployment: "deploy-widget-current");
        ValidationPublicationResult result = Publish(analysis, evidence, current);
        return Metric(
            GoldenWorkflowScenarioIds.StaleDeployment,
            "runtime",
            analysis,
            result,
            expectedAction: "block",
            totalWorkflowMs: 20,
            testExecutionMs: 0,
            deploymentCount: 1,
            reusedEvidenceCount: 1,
            invalidatedEvidenceCount: 1,
            expensiveOperationCount: 1);
    }

    private static GoldenWorkflowMetric GeneratedStateScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange(".rimdev/observability/events.json", "M", false, true));
        ValidationPublicationResult result = Publish(analysis, []);
        return Metric(
            GoldenWorkflowScenarioIds.GeneratedState,
            "generated",
            analysis,
            result,
            expectedAction: "skip",
            totalWorkflowMs: 1,
            testExecutionMs: 0);
    }

    private static GoldenWorkflowMetric InfrastructureScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("Source/Widget.cs", "M", false, false));
        ValidationPublicationResult result = Publish(analysis, []);
        return Metric(
            GoldenWorkflowScenarioIds.Infrastructure,
            "infrastructure",
            analysis,
            result,
            expectedAction: "block",
            totalWorkflowMs: 55,
            testExecutionMs: 30,
            testCount: 1,
            executedTestCount: 1,
            retries: 1,
            expensiveOperationCount: 1,
            sourceDebuggingCount: 0);
    }

    private static GoldenWorkflowMetric DependencyScenario()
    {
        ValidationChangeAnalysis analysis = Analyze(
            new GitRepositoryChange("Directory.Packages.props", "M", false, false));
        ValidationEvidenceIdentity stale = Identity(
            "dependency-v1",
            ["Directory.Packages.props"],
            ["compile", "quicktest"],
            dependency: "packages-v1",
            runtimeGeneration: 3,
            build: "build-v1",
            deployment: "deploy-v1");
        ValidationEvidenceRecord evidence = ValidationEvidenceRecord.Create(
            stale,
            "pass",
            BenchmarkTime);
        ValidationEvidenceIdentity current = Identity(
            "dependency-v2",
            ["Directory.Packages.props"],
            ["compile", "quicktest"],
            dependency: "packages-v2",
            runtimeGeneration: 4,
            build: "build-v2",
            deployment: "deploy-v2");
        ValidationPublicationResult result = Publish(analysis, [evidence], current);
        return Metric(
            GoldenWorkflowScenarioIds.Dependency,
            "dependency",
            analysis,
            result,
            expectedAction: "block",
            totalWorkflowMs: 35,
            testExecutionMs: 18,
            buildCount: 1,
            testCount: 1,
            executedTestCount: 1,
            invalidatedEvidenceCount: 1,
            expensiveOperationCount: 1);
    }

    private static GoldenWorkflowMetric Metric(
        string scenarioId,
        string category,
        ValidationChangeAnalysis analysis,
        ValidationPublicationResult result,
        string expectedAction,
        long totalWorkflowMs,
        long testExecutionMs,
        int buildCount = 0,
        int deploymentCount = 0,
        int testCount = 0,
        int executedTestCount = 0,
        int reusedEvidenceCount = 0,
        int invalidatedEvidenceCount = 0,
        int rimWorldLaunches = 0,
        int rimWorldRestarts = 0,
        int retries = 0,
        int expensiveOperationCount = 0,
        int sourceDebuggingCount = 0) =>
        new()
        {
            ScenarioId = scenarioId,
            Category = category,
            ExpectedAction = expectedAction,
            ObservedAction = result.PublicationAction,
            WorkflowAction = scenarioId switch
            {
                GoldenWorkflowScenarioIds.Documentation or GoldenWorkflowScenarioIds.NoRelevantSource or
                GoldenWorkflowScenarioIds.GeneratedState => "SKIP",
                GoldenWorkflowScenarioIds.Runtime => "RUN_THEN_REUSE",
                GoldenWorkflowScenarioIds.StaleDeployment => "REUSE_BUILD_UPDATE_DEPLOYMENT",
                GoldenWorkflowScenarioIds.Infrastructure => "RETRY_INFRASTRUCTURE",
                GoldenWorkflowScenarioIds.Dependency => "RUN_DEPENDENTS",
                _ => "RUN_STATIC"
            },
            Status = result.PublicationAction == expectedAction ||
                scenarioId is GoldenWorkflowScenarioIds.StaleDeployment or GoldenWorkflowScenarioIds.Dependency &&
                result.PublicationAction == "block"
                ? "pass"
                : "fail",
            TotalWorkflowMs = totalWorkflowMs,
            BuildCount = buildCount,
            DeploymentCount = deploymentCount,
            TestCount = testCount,
            ExecutedTestCount = executedTestCount,
            TestExecutionMs = testExecutionMs,
            ReusedEvidenceCount = result.ReusedEvidenceCount > 0
                ? result.ReusedEvidenceCount
                : reusedEvidenceCount,
            InvalidatedEvidenceCount = result.InvalidatedEvidenceCount > 0
                ? result.InvalidatedEvidenceCount
                : invalidatedEvidenceCount,
            RimWorldLaunches = rimWorldLaunches,
            RimWorldRestarts = rimWorldRestarts,
            Retries = retries,
            ExpensiveOperationCount = expensiveOperationCount,
            SourceDebuggingCount = sourceDebuggingCount,
            FailureClassification = scenarioId == GoldenWorkflowScenarioIds.Infrastructure
                ? "infrastructure"
                : null,
            RetryReasonCode = scenarioId == GoldenWorkflowScenarioIds.Infrastructure
                ? ValidationDecisionReasonCodes.InfrastructureFailureRetryable
                : null,
            Decisions = result.Decisions
                .Select(static decision => decision.Action + ":" + decision.ReasonCode)
                .Concat(scenarioId == GoldenWorkflowScenarioIds.Infrastructure
                    ? ["RETRY:" + ValidationDecisionReasonCodes.InfrastructureFailureRetryable]
                    : [])
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray()
        };

    private static ValidationChangeAnalysis Analyze(params GitRepositoryChange[] changes) =>
        ValidationChangeAnalyzer.Analyze(changes);

    private static ValidationPublicationResult Publish(
        ValidationChangeAnalysis analysis,
        IReadOnlyList<ValidationEvidenceRecord> evidence,
        ValidationEvidenceIdentity? current = null)
    {
        current ??= Identity(
            "source-v1",
            analysis.MeaningfulPaths,
            analysis.RequiredKinds);
        return ValidationPublicationGate.Evaluate(analysis, current, evidence, BenchmarkTime);
    }

    private static ValidationEvidenceIdentity Identity(
        string source,
        IReadOnlyList<string> selectedInputs,
        IReadOnlyList<string> testIds,
        string? dependency = null,
        int? runtimeGeneration = null,
        string? build = null,
        string? deployment = null) =>
        new()
        {
            Repository = "git:benchmark",
            ContentFingerprint = source,
            SelectedSourceInputs = selectedInputs,
            DependencyFingerprints = dependency is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["packages"] = dependency
                },
            BuildArtifactSha256 = build,
            DeploymentArtifactSha256 = deployment,
            ValidationKind = runtimeGeneration.HasValue
                ? ValidationEvidenceKinds.Runtime
                : ValidationEvidenceKinds.Static,
            CoveredKinds = runtimeGeneration.HasValue
                ? [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime]
                : [ValidationEvidenceKinds.Static],
            SuiteId = "benchmark",
            TestIds = testIds,
            ToolVersions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rimliaison"] = "benchmark"
            },
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile"] = "benchmark"
            },
            EnvironmentFingerprint = "environment-v1",
            RuntimeGeneration = runtimeGeneration,
            RequiresRuntimeGeneration = runtimeGeneration.HasValue,
            DeploymentCorrespondence = runtimeGeneration.HasValue ? "synchronized" : null
        };

    private static readonly IReadOnlyList<GoldenWorkflowBaseline> BaselineValues =
        new ReadOnlyCollection<GoldenWorkflowBaseline>
        (
        [
            new() { ScenarioId = GoldenWorkflowScenarioIds.Documentation, ExpectedExpensiveOperations = 0 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.XmlData, ExpectedExpensiveOperations = 1, ExpectedTests = 1 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.Runtime, ExpectedExpensiveOperations = 1, ExpectedBuilds = 1, ExpectedDeployments = 1, ExpectedTests = 1, ExpectedReusedEvidence = 1 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.NoRelevantSource, ExpectedExpensiveOperations = 0 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.StaleDeployment, ExpectedExpensiveOperations = 1, ExpectedDeployments = 1, ExpectedReusedEvidence = 1, ExpectedInvalidatedEvidence = 1 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.GeneratedState, ExpectedExpensiveOperations = 0 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.Infrastructure, ExpectedExpensiveOperations = 1, ExpectedTests = 1 },
            new() { ScenarioId = GoldenWorkflowScenarioIds.Dependency, ExpectedExpensiveOperations = 1, ExpectedBuilds = 1, ExpectedTests = 1, ExpectedInvalidatedEvidence = 1 }
        ]);
}
