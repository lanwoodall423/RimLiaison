using System.Text.Json;
using RimLiaison.Observability;
using RimLiaison.Validation;

namespace RimLiaison.Tests;

internal static class ValidationPolicyTests
{
    public static void RequiredFailureBlocksCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TASK_REQUIREMENT,
                ValidationCheckState.FAILED,
                ValidationFindingKind.MOD_DEFECT)
        ]);

        AssertEqual(ValidationPolicySchema.Fail, result.Status);
        Assert(!result.PermitsProduction, "A required failure must block production.");
    }

    public static void RequiredSuccessPermitsCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.REPOSITORY_POLICY,
                ValidationCheckState.PASSED)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        Assert(result.PermitsProduction, "A passing required check must permit production.");
    }

    public static void UnavailableBestEffortDoesNotBlockCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("runtime-deep-check", ValidationClassification.BEST_EFFORT,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.NOT_AVAILABLE,
                ValidationFindingKind.OPTIONAL_VALIDATION_UNAVAILABLE)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        AssertEqual(1, result.OptionalUnavailable);
        Assert(result.PermitsProduction, "Unavailable best-effort validation must not block.");
    }

    public static void DiscoveredRecommendationDoesNotBlockCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("deeper-runtime-proof", ValidationClassification.RECOMMENDED,
                ValidationRequirementSource.DISCOVERED,
                ValidationCheckState.RECORDED,
                ValidationFindingKind.TOOLING_IMPROVEMENT)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        AssertEqual(1, result.Recommendations);
        Assert(result.PermitsProduction, "A recommendation must not block production.");
    }

    public static void ExecutedOptionalModDefectIsStillSurfaced()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("optional-runtime", ValidationClassification.BEST_EFFORT,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.FAILED,
                ValidationFindingKind.MOD_DEFECT)
        ]);

        AssertEqual(ValidationPolicySchema.Fail, result.Status);
        AssertEqual(1, result.OptionalDefects);
        Assert(!result.PermitsProduction, "An executed optional mod defect must remain blocking.");
    }

    public static void DiscoveredValidationCannotEscalateToRequired()
    {
        bool rejected = false;
        try
        {
            ValidationPolicyEvaluator.Define(
                "new-idea",
                ValidationClassification.REQUIRED,
                ValidationRequirementSource.DISCOVERED,
                "A newly noticed validation idea");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "A discovered validation must not silently become REQUIRED.");
    }

    public static void StructuredOutputSeparatesDefectAndRecommendation()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "run",
            AgentId = "agent",
            ModId = "mod",
            ModName = "Example Mod",
            StartTime = 1
        });
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Optional runtime assertion failed.",
            new
            {
                operationKey = "test:optional-runtime",
                validationId = "optional-runtime",
                validationClassification = "BEST_EFFORT",
                issueKind = "MOD_DEFECT",
                blocking = true,
                errorCode = "MOD_ASSERTION_FAILED",
                evidenceReference = "evidence/mod-failure"
            }));
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationRecommendationRecorded,
            "Deep runtime test is unavailable.",
            new
            {
                operationKey = "validation:deep-runtime",
                validationId = "deep-runtime",
                validationClassification = "RECOMMENDED",
                issueKind = "TOOLING_IMPROVEMENT",
                blocking = false,
                componentOwner = "RimBridgeServer",
                recommendation = "Add a bounded deep runtime recipe."
            }));

        AgentIssue[] issues = store.GetIssues().ToArray();
        AgentIssue defect = issues.Single(issue => issue.Classification == "MOD_DEFECT");
        AgentIssue recommendation = issues.Single(issue =>
            issue.Classification == "TOOLING_IMPROVEMENT");
        AssertEqual(AgentIssueCategory.ModDefect, defect.Category);
        Assert(defect.Blocking, "The mod defect must be marked blocking.");
        AssertEqual("optional-runtime", defect.AffectedValidation);
        AssertEqual(AgentIssueCategory.ToolingImprovement, recommendation.Category);
        Assert(!recommendation.Blocking, "The tooling recommendation must be non-blocking.");
        AssertEqual("RimBridgeServer", recommendation.ComponentOwner);
        Assert(recommendation.Recommendation is not null,
            "The recommendation must remain queryable in structured output.");
    }

    public static void NonBlockingRecommendationPersistsAfterCompletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rimliaison-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new AgentObservabilityStore(directory))
            {
                store.RegisterAgent(new AgentSnapshot
                {
                    RunId = "run",
                    AgentId = "agent",
                    ModId = "mod",
                    ModName = "Example Mod",
                    StartTime = 1
                });
                store.AppendEvent(new AgentEventRequest(
                    "run",
                    "agent",
                    "mod",
                    DevelopmentStage.Testing,
                    AgentEventTypes.ValidationRecommendationRecorded,
                    "Optional runtime test is unavailable.",
                    new
                    {
                        operationKey = "validation:obscure-runtime",
                        validationId = "obscure-runtime",
                        validationClassification = "RECOMMENDED",
                        issueKind = "TOOLING_IMPROVEMENT",
                        blocking = false,
                        recommendation = "Add the obscure runtime test."
                    }));
            }

            using var reloaded = new AgentObservabilityStore(directory);
            AgentIssue issue = reloaded.GetIssues().Single();
            AssertEqual("obscure-runtime", issue.AffectedValidation);
            Assert(!issue.Blocking, "Persisted recommendations must remain non-blocking.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static void CounterfactualHealthyAndMutantAreDistinguished()
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            CounterfactualContext context = SyntheticContext(seed);
            CounterfactualAssertion assertion = BrambleRelation();
            CounterfactualObservation[] healthy = PathSamples(context);
            CounterfactualResult pass = CounterfactualEvaluator.Evaluate(context, assertion, healthy);
            AssertEqual(CounterfactualContract.Pass, pass.Status);
            AssertEqual("SYNTHETIC_RELATION_CONFIRMED", pass.ReasonCode);
            AssertEqual(1, pass.SampleCount);
            AssertEqual(30m, pass.ControlValue);
            AssertEqual(45m, pass.TreatmentValue);
            AssertEqual("synthetic", pass.Provenance);
            Assert(!pass.NativeQualified, "A fixture must never qualify a native game run.");

            healthy[1] = healthy[1] with { Value = healthy[0].Value };
            CounterfactualResult mutation = CounterfactualEvaluator.Evaluate(context, assertion, healthy);
            AssertEqual(CounterfactualContract.Fail, mutation.Status);
            AssertEqual("RELATION_VIOLATED", mutation.ReasonCode);
            Assert(!mutation.NativeQualified, "Even a detected mutant is not native evidence.");
        }
    }

    public static void CounterfactualInsufficientCoverageIsInconclusive()
    {
        CounterfactualContext context = SyntheticContext(4);
        CounterfactualObservation[] observations = PathSamples(context);
        observations[1] = observations[1] with { Coverage = 0 };
        AssertEqual(CounterfactualContract.Inconclusive,
            CounterfactualEvaluator.Evaluate(context, BrambleRelation(), observations).Status);

        observations = PathSamples(context);
        AssertEqual(CounterfactualContract.Inconclusive,
            CounterfactualEvaluator.Evaluate(context, BrambleRelation(),
                [observations[1]]).Status);
        AssertEqual(CounterfactualContract.Inconclusive,
            CounterfactualEvaluator.Evaluate(context,
                BrambleRelation() with { MinimumPairs = 2 }, observations).Status);
        AssertEqual(0, CounterfactualEvaluator.Evaluate(
            context, BrambleRelation(), Array.Empty<CounterfactualObservation>()).SampleCount);
    }

    public static void CounterfactualForeignAndMalformedEvidenceIsRejected()
    {
        CounterfactualContext context = SyntheticContext(5);
        CounterfactualObservation[] normal = PathSamples(context);
        CounterfactualAssertion assertion = BrambleRelation();

        void Reject(CounterfactualContext candidateContext,
            CounterfactualAssertion candidateAssertion,
            IReadOnlyList<CounterfactualObservation> candidateEvents,
            string label)
        {
            CounterfactualResult result = CounterfactualEvaluator.Evaluate(
                candidateContext, candidateAssertion, candidateEvents);
            AssertEqual(CounterfactualContract.InvalidEvidence, result.Status);
            Assert(!result.NativeQualified, label + " incorrectly qualified the game.");
        }

        Reject(context with { Provenance = "live" }, assertion, normal, "fake native evidence");
        Reject(context with { CurrentLogVerified = true }, assertion, normal, "fake log");
        Reject(context with { RunnerSha = new string('f', 64) }, assertion, normal, "fake runner");
        Reject(context with { SourceSha = new string('b', 40) }, assertion, normal, "foreign source");
        Reject(context with { ExpectedBiome = "BorealForest" }, assertion, normal, "wrong biome");
        Reject(context with { MapReady = false }, assertion, normal, "missing map-ready");
        Reject(context with { CleanupComplete = false }, assertion, normal, "missing cleanup");
        Reject(context with { SchemaVersion = "other" }, assertion, normal, "wrong version");

        Reject(context, assertion, [normal[0], normal[1] with { Nonce = "foreign" }],
            "foreign nonce");
        Reject(context, assertion, [normal[0], normal[1] with { SourceSha = new string('c', 40) }],
            "foreign source event");
        Reject(context, assertion, [normal[0], normal[1] with { Biome = "BorealForest" }],
            "wrong event biome");
        Reject(context, assertion, [normal[0], normal[1] with { Unit = "cells" }],
            "wrong unit");
        Reject(context, assertion, [normal[0], normal[1] with { Tick = 101 }],
            "mismatched ticks");
        Reject(context, assertion,
            [normal[0], normal[1] with { ConditionsSha256 = new string('e', 64) }],
            "mismatched experimental conditions");
        Reject(context, assertion,
            [normal[0], normal[1] with { ConditionsSha256 = "not-a-sha256" }],
            "invalid conditions fingerprint");
        Reject(context, assertion, [normal[0], normal[1] with { EventId = normal[0].EventId }],
            "duplicate event");
        Reject(context, assertion, [normal[0], normal[0] with { EventId = "another-event" }],
            "duplicate plot/sample");
        Reject(context, assertion, [normal[0], normal[1] with { EventId = new string('x', 81) }],
            "oversized token");
        Reject(context, assertion, [normal[0], normal[1] with { Coverage = -1 }],
            "negative coverage");
        Reject(context, assertion,
            Enumerable.Range(0, 65).Select(index =>
                normal[0] with { EventId = "e" + index, SampleId = "s" + index }).ToArray(),
            "oversized observation list");
        Reject(context, assertion with { Relation = "custom-expression" }, normal,
            "unknown relation");
        Reject(context, assertion with { Tolerance = -1m }, normal,
            "negative tolerance");
        Reject(context, assertion with { MinimumPairs = 0 }, normal,
            "vacuous required count");
    }

    public static void CounterfactualRelationAllowlistHasExplicitBoundaries()
    {
        CounterfactualContext context = SyntheticContext(6);
        CounterfactualObservation[] observations = PathSamples(context);
        CounterfactualAssertion path = BrambleRelation();
        CounterfactualResult Evaluate(CounterfactualAssertion assertion,
            IReadOnlyList<CounterfactualObservation> values) =>
            CounterfactualEvaluator.Evaluate(context, assertion, values);

        AssertEqual(CounterfactualContract.Pass,
            Evaluate(path with { Tolerance = 14m }, observations).Status);
        AssertEqual(CounterfactualContract.Fail,
            Evaluate(path with { Tolerance = 15m }, observations).Status);
        AssertEqual(CounterfactualContract.Pass,
            Evaluate(path with { Relation = "equal", Tolerance = 15m }, observations).Status);
        AssertEqual(CounterfactualContract.Fail,
            Evaluate(path with { Relation = "equal", Tolerance = 14m }, observations).Status);
        AssertEqual(CounterfactualContract.Pass,
            Evaluate(path with { Relation = "bounded", LowerBound = 40m, UpperBound = 50m },
                observations).Status);
        AssertEqual(CounterfactualContract.Fail,
            Evaluate(path with { Relation = "bounded", LowerBound = 10m, UpperBound = 20m },
                observations).Status);
        AssertEqual(CounterfactualContract.InvalidEvidence,
            Evaluate(path with { Relation = "bounded", LowerBound = 50m, UpperBound = 40m },
                observations).Status);
        AssertEqual(CounterfactualContract.Fail,
            Evaluate(path with { Relation = "no-increase" }, observations).Status);
        observations[1] = observations[1] with { Value = 30m };
        AssertEqual(CounterfactualContract.Pass,
            Evaluate(path with { Relation = "no-increase" }, observations).Status);

        CounterfactualObservation[] twoPairs =
        [
            .. PathSamples(context),
            PathSamples(context)[0] with { EventId = "control2", SampleId = "pair2", Tick = 200 },
            PathSamples(context)[1] with { EventId = "bramble2", SampleId = "pair2", Tick = 200 }
        ];
        CounterfactualResult two = Evaluate(path with { MinimumPairs = 2 }, twoPairs);
        AssertEqual(CounterfactualContract.Pass, two.Status);
        AssertEqual(2, two.SampleCount);
    }

    private static CounterfactualContext SyntheticContext(int seed) =>
        new(CounterfactualContract.Current, new string('a', 40),
            "naturetiles", "bramble-route", "fixture-map-" + seed, "nonce-" + seed,
            seed, "TemperateForest", "TemperateForest", "synthetic",
            MapReady: true, CurrentLogVerified: false, CleanupComplete: true);

    private static CounterfactualAssertion BrambleRelation() =>
        new("bramble-path-cost", "path-cost", "ticks", "greater-than",
            "control", "brambles");

    private static CounterfactualObservation[] PathSamples(CounterfactualContext context) =>
    [
        new("control-event", context.SourceSha, context.ScenarioId, context.MapId,
            context.Nonce, context.Biome, new string('d', 64),
            "pair1", "control", "path-cost",
            30m, "ticks", 100, 0),
        new("bramble-event", context.SourceSha, context.ScenarioId, context.MapId,
            context.Nonce, context.Biome, new string('d', 64),
            "pair1", "brambles", "path-cost",
            45m, "ticks", 100, 4)
    ];

    private static ValidationCheckObservation Observation(
        string id,
        ValidationClassification classification,
        ValidationRequirementSource source,
        ValidationCheckState state,
        ValidationFindingKind? finding = null) =>
        new()
        {
            Check = ValidationPolicyEvaluator.Define(
                id,
                classification,
                source,
                "Validation " + id),
            State = state,
            Finding = finding,
            Recommendation = finding == ValidationFindingKind.TOOLING_IMPROVEMENT
                ? "Record separately."
                : null
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
