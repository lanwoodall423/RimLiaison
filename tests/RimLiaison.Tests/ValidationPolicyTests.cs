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

    public static void MapEventsMatchedAreNeverNativeProof()
    {
        MapEventAssessment result = MapEventEvaluator.Evaluate(MapFixture(), MapEvents());
        AssertEqual(MapEventContract.ReadyForNativeReview, result.Status);
        AssertEqual("MATCHED_UNTRUSTED_MAP_EVENTS", result.ReasonCode);
        AssertEqual(17, result.MapUniqueId);
        AssertEqual(24, result.ObservedCount);
        AssertEqual("untrusted-log-text", result.Provenance);
        Assert(!result.NativeQualified, "Source event text cannot qualify RimWorld.");
        Assert(result.Status != "PASS", "An untrusted event must not be reported as PASS.");
        MapWitnessSourceOnlyEvidenceIsNeverNative();
    }

    public static void MapEventsMissingAndChanceMissStayInconclusive()
    {
        MapEventExpectation fixture = MapFixture();
        AssertEqual(MapEventContract.Inconclusive,
            MapEventEvaluator.Evaluate(fixture, []).Status);
        AssertEqual("NO_MAP_EVENTS", MapEventEvaluator.Evaluate(fixture, []).ReasonCode);
        AssertEqual("MISSING_MATCHED_EVENT",
            MapEventEvaluator.Evaluate(fixture, [MapEvents()[0]]).ReasonCode);
        AssertEqual("MISSING_MATCHED_EVENT",
            MapEventEvaluator.Evaluate(fixture, [MapEvents()[1]]).ReasonCode);
        string[] chanceMiss = MapEvents();
        chanceMiss[0] = chanceMiss[0].Replace(
            "\"outcome\":\"completed\"", "\"outcome\":\"chance_miss\"", StringComparison.Ordinal);
        chanceMiss[1] = chanceMiss[1].Replace(
            "\"brambleCount\":24", "\"brambleCount\":0", StringComparison.Ordinal)
            .Replace("\"featureObserved\":true", "\"featureObserved\":false",
                StringComparison.Ordinal);
        MapEventAssessment missingFeature = MapEventEvaluator.Evaluate(fixture, chanceMiss);
        AssertEqual(MapEventContract.Inconclusive, missingFeature.Status);
        AssertEqual("NO_FEATURE_OBSERVED", missingFeature.ReasonCode);
        AssertEqual(0, missingFeature.ObservedCount);
        Assert(!missingFeature.NativeQualified, "No-feature fixture cannot be native.");
        string[] otherOutcome = MapEvents();
        otherOutcome[0] = otherOutcome[0].Replace(
            "\"outcome\":\"completed\"", "\"outcome\":\"no_eligible_seeds\"",
            StringComparison.Ordinal);
        AssertEqual("TERMINAL_OUTCOME_NOT_POSITIVE",
            MapEventEvaluator.Evaluate(fixture, otherOutcome).ReasonCode);
    }

    public static void MapEventsForeignMalformedAndDuplicateAreInvalid()
    {
        MapEventExpectation expected = MapFixture();
        string[] valid = MapEvents();
        void Reject(MapEventExpectation contract, IReadOnlyList<string> lines, string label)
        {
            MapEventAssessment assessment = MapEventEvaluator.Evaluate(contract, lines);
            AssertEqual(MapEventContract.InvalidEvidence, assessment.Status);
            Assert(!assessment.NativeQualified, label + " was called native evidence.");
        }
        void Mutate(int index, string from, string to, string label)
        {
            string[] lines = MapEvents();
            lines[index] = lines[index].Replace(from, to, StringComparison.Ordinal);
            Assert(lines[index] != valid[index], "Mutation not applied: " + label);
            Reject(expected, lines, label);
        }

        Mutate(0, "\"nonce\":\"map-abc12345\"", "\"nonce\":\"other-run-1234\"",
            "cross-run");
        Mutate(1, "\"mapUniqueId\":17", "\"mapUniqueId\":18", "cross-map");
        Mutate(1, "\"mapWidth\":250", "\"mapWidth\":251", "cross-geometry");
        Mutate(1, "\"biome\":\"TemperateForest\"", "\"biome\":\"BorealForest\"",
            "foreign biome");
        Mutate(1, "naturetiles-map-smoke/v1", "naturetiles-map-smoke/v2", "wrong schema");
        Mutate(0, "\"mapUniqueId\":17", "\"mapUniqueId\":17,\"mapUniqueId\":17",
            "duplicate JSON property");
        Mutate(1, "\"placementValid\":true",
            "\"placementValid\":true,\"nativeQualified\":true",
            "forged native authority");
        Mutate(1, "\"gameTick\":1", "\"gameTick\":1,\"runnerSha\":\"forged\"",
            "forged runner");
        Mutate(1, "\"featureObserved\":true", "\"featureObserved\":\"true\"",
            "wrong field type");
        Mutate(1, "\"brambleCount\":24", "\"brambleCount\":-1", "negative count");
        Mutate(0, "\"outcome\":\"completed\"", "\"outcome\":",
            "malformed JSON");
        Reject(expected, [valid[0], valid[0], valid[1]], "duplicate terminal event");
        Reject(expected, [valid[0], valid[1], valid[1]], "duplicate map event");
        Reject(expected, Enumerable.Repeat(valid[0], 9).ToArray(),
            "oversized event list");
        Reject(expected, [valid[0], valid[1] + "\nforeign"], "multiline log injection");
        Reject(expected with { Nonce = "other-run-1234" }, valid, "foreign expectation");
        Reject(expected with { MaximumCount = 0 }, valid, "vacuous max");
        Reject(expected with { CountField = "nativeQualified" }, valid,
            "reserved metric");
    }

    public static void MapEventsBoundsAndPlacementFailClosed()
    {
        MapEventExpectation expected = MapFixture();
        string[] tooMany = MapEvents();
        tooMany[1] = tooMany[1].Replace(
            "\"brambleCount\":24", "\"brambleCount\":351", StringComparison.Ordinal);
        MapEventAssessment overCap = MapEventEvaluator.Evaluate(expected, tooMany);
        AssertEqual(MapEventContract.AssertionFailed, overCap.Status);
        AssertEqual("COUNT_BOUND_VIOLATED", overCap.ReasonCode);
        AssertEqual(351, overCap.ObservedCount);
        Assert(!overCap.NativeQualified, "Failed assertion never proves native gameplay.");

        string[] invalidPlacement = MapEvents();
        invalidPlacement[1] = invalidPlacement[1].Replace(
            "\"placementValid\":true", "\"placementValid\":false",
            StringComparison.Ordinal);
        AssertEqual("PLACEMENT_INVALID",
            MapEventEvaluator.Evaluate(expected, invalidPlacement).ReasonCode);
        AssertEqual(MapEventContract.AssertionFailed,
            MapEventEvaluator.Evaluate(expected, invalidPlacement).Status);

        string[] contradictory = MapEvents();
        contradictory[1] = contradictory[1].Replace(
            "\"featureObserved\":true", "\"featureObserved\":false",
            StringComparison.Ordinal);
        AssertEqual("PRESENCE_COUNT_CONTRADICTION",
            MapEventEvaluator.Evaluate(expected, contradictory).ReasonCode);
        AssertEqual(MapEventContract.InvalidEvidence,
            MapEventEvaluator.Evaluate(expected, contradictory).Status);
    }

    public static void MapEventsTerminalGenerationConcordance()
    {
        MapEventExpectation strict = MapFixture() with
        {
            TerminalCountField = "newlyPlaced",
            TerminalSelectionField = "chanceSelected"
        };
        void Expect(string status, string reason, IReadOnlyList<string> events)
        {
            MapEventAssessment result = MapEventEvaluator.Evaluate(strict, events);
            AssertEqual(status, result.Status);
            AssertEqual(reason, result.ReasonCode);
            AssertEqual("untrusted-log-text", result.Provenance);
            Assert(!result.NativeQualified, "Terminal evidence is still untrusted text.");
        }
        string[] ChangeTerminal(string source, string replacement)
        {
            string[] lines = MapEvents();
            lines[0] = lines[0].Replace(source, replacement, StringComparison.Ordinal);
            Assert(lines[0] != MapEvents()[0], "Terminal fixture mutation must apply.");
            return lines;
        }

        Expect(MapEventContract.ReadyForNativeReview, "MATCHED_UNTRUSTED_MAP_EVENTS",
            MapEvents());
        string[] legitimateMiss = ChangeTerminal(
            "\"outcome\":\"completed\",\"chanceSelected\":true,\"newlyPlaced\":24",
            "\"outcome\":\"chance_miss\",\"chanceSelected\":false,\"newlyPlaced\":0");
        Expect(MapEventContract.Inconclusive, "NO_NEW_FEATURE_PLACED",
            legitimateMiss); // Existing plants may still be present on the map.

        Expect(MapEventContract.InvalidEvidence, "TERMINAL_COUNT_OBSERVATION_MISMATCH",
            ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":25"));
        Expect(MapEventContract.AssertionFailed, "TERMINAL_COUNT_BOUND_VIOLATED",
            ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":351"));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_SELECTION_CONTRADICTION",
            ChangeTerminal("\"chanceSelected\":true", "\"chanceSelected\":false"));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_ZERO_POSITIVE_CONTRADICTION",
            ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":0"));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_COUNT_INVALID",
            ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":\"24\""));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_COUNT_INVALID",
            ChangeTerminal(",\"newlyPlaced\":24", ""));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_COUNT_INVALID",
            ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":-1"));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_SELECTION_INVALID",
            ChangeTerminal("\"chanceSelected\":true", "\"chanceSelected\":\"true\""));
        Expect(MapEventContract.InvalidEvidence, "TERMINAL_SELECTION_INVALID",
            ChangeTerminal(",\"chanceSelected\":true", ""));

        AssertEqual("EXPECTATION_CONTRACT_INVALID", MapEventEvaluator.Evaluate(
            strict with { TerminalCountField = "brambleCount" }, MapEvents()).ReasonCode);
        AssertEqual("EXPECTATION_CONTRACT_INVALID", MapEventEvaluator.Evaluate(
            strict with { TerminalSelectionField = "nativeQualified" },
            MapEvents()).ReasonCode);

        MapEventExpectation other = strict with
        {
            EventPrefix = "CrystalQA",
            OutcomeSchema = "crystal-genstep-outcome/v1",
            ObservationSchema = "crystal-map-smoke/v1",
            Nonce = "crystal-run-1234",
            CountField = "crystalCount",
            PresenceField = "crystalObserved",
            IntegrityField = "placementClear",
            TerminalCountField = "crystalsCreated",
            TerminalSelectionField = "crystalSelected",
            PositiveOutcome = "crystals_generated"
        };
        string[] independentMod = MapEvents().Select(line => line
            .Replace("NatureTilesQA", "CrystalQA", StringComparison.Ordinal)
            .Replace("naturetiles-genstep-outcome/v1", "crystal-genstep-outcome/v1",
                StringComparison.Ordinal)
            .Replace("naturetiles-map-smoke/v1", "crystal-map-smoke/v1",
                StringComparison.Ordinal)
            .Replace("map-abc12345", "crystal-run-1234", StringComparison.Ordinal)
            .Replace("brambleCount", "crystalCount", StringComparison.Ordinal)
            .Replace("featureObserved", "crystalObserved", StringComparison.Ordinal)
            .Replace("placementValid", "placementClear", StringComparison.Ordinal)
            .Replace("newlyPlaced", "crystalsCreated", StringComparison.Ordinal)
            .Replace("chanceSelected", "crystalSelected", StringComparison.Ordinal)
            .Replace("\"outcome\":\"completed\"", "\"outcome\":\"crystals_generated\"",
                StringComparison.Ordinal)).ToArray();
        MapEventAssessment unrelated = MapEventEvaluator.Evaluate(other, independentMod);
        AssertEqual(MapEventContract.ReadyForNativeReview, unrelated.Status);
        Assert(!unrelated.NativeQualified, "Other mods cannot claim native proof.");

        // Existing integrations may opt out of terminal-specific evidence,
        // without any new required field or change in their old outcome.
        AssertEqual(MapEventContract.ReadyForNativeReview,
            MapEventEvaluator.Evaluate(MapFixture(),
                ChangeTerminal("\"newlyPlaced\":24", "\"newlyPlaced\":0")).Status);
    }

    public static void MapEventsAreReusableAcrossModSchemas()
    {
        MapEventExpectation other = MapFixture() with
        {
            EventPrefix = "OtherModQA",
            OutcomeSchema = "othermod-genstep-outcome/v1",
            ObservationSchema = "othermod-map-smoke/v1",
            Nonce = "other-run-1234",
            ExpectedBiome = "BorealForest",
            CountField = "crystalCount",
            PresenceField = "featureSeen",
            IntegrityField = "layoutValid",
            PositiveOutcome = "placed"
        };
        string[] source = MapEvents();
        string[] converted = source.Select(line => line
            .Replace("NatureTilesQA", "OtherModQA", StringComparison.Ordinal)
            .Replace("naturetiles-genstep-outcome/v1", "othermod-genstep-outcome/v1",
                StringComparison.Ordinal)
            .Replace("naturetiles-map-smoke/v1", "othermod-map-smoke/v1",
                StringComparison.Ordinal)
            .Replace("map-abc12345", "other-run-1234", StringComparison.Ordinal)
            .Replace("TemperateForest", "BorealForest", StringComparison.Ordinal)
            .Replace("brambleCount", "crystalCount", StringComparison.Ordinal)
            .Replace("featureObserved", "featureSeen", StringComparison.Ordinal)
            .Replace("placementValid", "layoutValid", StringComparison.Ordinal)
            .Replace("\"outcome\":\"completed\"", "\"outcome\":\"placed\"",
                StringComparison.Ordinal)).ToArray();
        MapEventAssessment result = MapEventEvaluator.Evaluate(other, converted);
        AssertEqual(MapEventContract.ReadyForNativeReview, result.Status);
        Assert(!result.NativeQualified, "Generic source events remain untrusted.");
        AssertEqual(MapEventContract.InvalidEvidence,
            MapEventEvaluator.Evaluate(MapFixture(), converted).Status);
    }


    public static void MapWitnessSourceOnlyEvidenceIsNeverNative()
    {
        string source = new('a', 40);
        string runner = new('b', 40);
        string run = "a4f77b80-b536-4515-9d44-2e06ff087c34";
        string seed = "bramble seed 42";
        const int tile = 451;
        DateTimeOffset started = new(2026, 11, 10, 21, 30, 0, TimeSpan.Zero);
        MapEventExpectation expectation = MapFixture();
        MapEventAssessment events = MapEventEvaluator.Evaluate(expectation, MapEvents());
        var witness = new MapWitnessReceipt(
            MapWitnessContract.Current, source, runner, run, run,
            expectation.Nonce, "1.7.4990", expectation.ExpectedBiome, seed,
            tile, 17, 24, new string('c', 64), 2048,
            started, started.AddMinutes(2), started.AddMinutes(4),
            CurrentLogVerified: true, CleanupComplete: true);
        MapWitnessAssessment Evaluate(MapWitnessReceipt? receipt) =>
            MapWitnessEvaluator.Evaluate(expectation, events, receipt,
                source, runner, run, seed, tile);
        void Reject(MapWitnessReceipt receipt, string reason)
        {
            MapWitnessAssessment result = Evaluate(receipt);
            AssertEqual(reason, result.ReasonCode);
            Assert(!result.NativeQualified && !result.SeedVerified &&
                !result.ScreenshotVerified,
                "Untrusted witness fields may never qualify native gameplay.");
        }

        MapWitnessAssessment complete = Evaluate(witness);
        AssertEqual(MapWitnessContract.ReadyForAttestation, complete.Status);
        Assert(!complete.NativeQualified && !complete.SeedVerified &&
            !complete.ScreenshotVerified,
            "Plausible synthetic source fixtures are not native proof.");
        AssertEqual("NATIVE_WITNESS_MISSING", Evaluate(null).ReasonCode);
        Reject(witness with { SourceHead = new string('d', 40) },
            "SOURCE_RUNNER_MISMATCH");
        Reject(witness with { RunnerHead = new string('d', 40) },
            "SOURCE_RUNNER_MISMATCH");
        Reject(witness with { RunId = "bd851990-0eb4-42f1-8d2f-01a30389bdce" },
            "RUN_NONCE_MISMATCH");
        Reject(witness with { ScreenshotRunId = "bd851990-0eb4-42f1-8d2f-01a30389bdce" },
            "RUN_NONCE_MISMATCH");
        Reject(witness with { Nonce = "foreign-run-1234" },
            "RUN_NONCE_MISMATCH");
        Reject(witness with { GameVersion = "1.6.5000" },
            "GAME_VERSION_MISMATCH");
        Reject(witness with { WorldSeed = "foreign seed" },
            "SEED_TILE_MISMATCH");
        Reject(witness with { WorldTile = tile + 1 },
            "SEED_TILE_MISMATCH");
        Reject(witness with { MapUniqueId = 18 },
            "MAP_IDENTITY_MISMATCH");
        Reject(witness with { ObservedCount = 0 },
            "MAP_IDENTITY_MISMATCH");
        Reject(witness with { Biome = "BorealForest" },
            "MAP_IDENTITY_MISMATCH");
        Reject(witness with { ScreenshotSha256 = "" },
            "SCREENSHOT_EVIDENCE_MISSING");
        Reject(witness with { ScreenshotByteCount = 0 },
            "SCREENSHOT_EVIDENCE_MISSING");
        Reject(witness with { ScreenshotCapturedUtc = started.AddDays(-1) },
            "SCREENSHOT_RUN_TIME_MISMATCH");
        Reject(witness with { CurrentLogVerified = false },
            "CURRENT_LOG_UNVERIFIED");
        Reject(witness with { CleanupComplete = false },
            "OWNED_CLEANUP_UNVERIFIED");
        Reject(witness with { SchemaVersion = "rimliaison-map-witness/v0" },
            "WITNESS_ENVELOPE_INVALID");

        MapWitnessAssessment noMap = MapWitnessEvaluator.Evaluate(expectation,
            events with { Status = MapEventContract.Inconclusive },
            witness, source, runner, run, seed, tile);
        AssertEqual("SOURCE_EVENT_NOT_READY", noMap.ReasonCode);
        Assert(!noMap.NativeQualified, "Missing map readiness is not native proof.");
        AssertEqual("EXPECTED_CUSTODY_INVALID",
            MapWitnessEvaluator.Evaluate(expectation, events, witness,
                source, runner, run, seed, -1).ReasonCode);

        // Source-only bytes-to-receipt binding. The image is a generated 1x1
        // synthetic PNG fixture, not a captured RimWorld game screenshot.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlE" +
            "QVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==");
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(png))
            .ToLowerInvariant();
        MapWitnessReceipt bound = witness with
        {
            ScreenshotSha256 = hash,
            ScreenshotByteCount = png.Length
        };
        MapWitnessScreenshotBinding Bind(
            MapWitnessReceipt? receipt, ReadOnlyMemory<byte> bytes) =>
            MapWitnessScreenshotBinder.Bind(expectation, events, receipt,
                bytes, source, runner, run, seed, tile);
        void RejectBytes(MapWitnessReceipt? receipt, ReadOnlyMemory<byte> bytes,
            string reason)
        {
            MapWitnessScreenshotBinding result = Bind(receipt, bytes);
            AssertEqual(reason, result.ReasonCode);
            Assert(result.Receipt is null,
                "Failed byte custody cannot return any witness receipt.");
            Assert(!result.NativeQualified && !result.SeedVerified &&
                !result.ScreenshotVerified,
                "Byte custody has no guest provenance or native authority.");
        }

        // Build synthetic chunks with correct CRCs so the parser must
        // reject invalid PNG structure, not just damaged checksums.
        static byte[] PngChunk(string type, byte[] payload)
        {
            byte[] chunk = new byte[payload.Length + 12];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                chunk.AsSpan(0, 4), (uint)payload.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            payload.CopyTo(chunk, 8);
            uint crc = 0xffffffffu;
            foreach (byte value in chunk.AsSpan(4, payload.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 :
                        (crc >> 1) ^ 0xedb88320u;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                chunk.AsSpan(payload.Length + 8, 4), ~crc);
            return chunk;
        }

        MapWitnessReceipt ClaimForPng(byte[] bytes) => bound with
        {
            ScreenshotSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant(),
            ScreenshotByteCount = bytes.Length
        };

        void RejectInvalidContainer(byte[] bytes) =>
            RejectBytes(ClaimForPng(bytes), bytes, "SCREENSHOT_PNG_INVALID");

        void AcceptDecodedPng(byte[] bytes)
        {
            MapWitnessScreenshotBinding result = Bind(ClaimForPng(bytes), bytes);
            AssertEqual(MapWitnessContract.ReadyForAttestation, result.Status);
            AssertEqual("SCREENSHOT_BYTES_BOUND_NATIVE_AUTHORITY_UNPROVEN",
                result.ReasonCode);
            Assert(result.Receipt is not null && !result.NativeQualified &&
                !result.SeedVerified && !result.ScreenshotVerified,
                "Decodable PNG does not prove authentic host/game capture.");
        }

        static byte[] PngZlib(byte[] raw)
        {
            using MemoryStream output = new();
            using (var zlib = new System.IO.Compression.ZLibStream(
                output, System.IO.Compression.CompressionLevel.Optimal,
                leaveOpen: true))
                zlib.Write(raw);
            return output.ToArray();
        }

        // Fixture has a standard 13-byte IHDR and one IDAT, followed by IEND.
        byte[] PngWithData(byte[] imageData) =>
            [.. png[..33], .. PngChunk("IDAT", imageData), .. png[^12..]];

        MapWitnessScreenshotBinding bytesBound = Bind(bound, png);
        AssertEqual(MapWitnessContract.ReadyForAttestation, bytesBound.Status);
        AssertEqual("SCREENSHOT_BYTES_BOUND_NATIVE_AUTHORITY_UNPROVEN",
            bytesBound.ReasonCode);
        AssertEqual(hash, bytesBound.Receipt?.ScreenshotSha256);
        AssertEqual((long)png.Length,
            bytesBound.Receipt?.ScreenshotByteCount ?? -1L);
        Assert(!bytesBound.NativeQualified && !bytesBound.SeedVerified &&
            !bytesBound.ScreenshotVerified,
            "Even exact real bytes only qualify for independent attestation.");

        RejectBytes(null, png, "NATIVE_WITNESS_MISSING");
        RejectBytes(bound, ReadOnlyMemory<byte>.Empty,
            "SCREENSHOT_BYTES_MISSING_OR_OUT_OF_BOUNDS");
        RejectBytes(witness, png, "SCREENSHOT_BYTES_DIGEST_MISMATCH");
        RejectBytes(bound with { ScreenshotByteCount = png.Length - 1 }, png,
            "SCREENSHOT_BYTES_DIGEST_MISMATCH");
        byte[] corrupt = (byte[])png.Clone();
        corrupt[45] ^= 0x01;
        RejectBytes(bound, corrupt, "SCREENSHOT_PNG_INVALID");
        byte[] appended = [.. png, 0x00];
        RejectBytes(bound, appended, "SCREENSHOT_PNG_INVALID");
        // An ancillary chunk after image data is permitted, but neither an
        // invalid reserved chunk name nor resumed IDAT data is valid PNG.
        byte[] textChunk = PngChunk("tEXt", [(byte)'k', 0, (byte)'v']);
        byte[] validAncillary =
            [.. png[..^12], .. textChunk, .. png[^12..]];
        MapWitnessScreenshotBinding ancillaryBound = Bind(bound with
        {
            ScreenshotSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(validAncillary))
                .ToLowerInvariant(),
            ScreenshotByteCount = validAncillary.Length
        }, validAncillary);
        AssertEqual(MapWitnessContract.ReadyForAttestation,
            ancillaryBound.Status);
        byte[] invalidReserved =
            [.. png[..^12], .. PngChunk("texT", []), .. png[^12..]];
        RejectInvalidContainer(invalidReserved);
        byte[] invalidLetters =
            [.. png[..^12], .. PngChunk("t3Xt", []), .. png[^12..]];
        RejectInvalidContainer(invalidLetters);
        byte[] resumedImageData =
            [.. png[..^12], .. textChunk,
             .. PngChunk("IDAT", [0]), .. png[^12..]];
        RejectInvalidContainer(resumedImageData);
        byte[] latePalette =
            [.. png[..^12], .. PngChunk("PLTE", [0, 0, 0]),
             .. png[^12..]];
        RejectInvalidContainer(latePalette);

        // A correct CRC on every PNG chunk is not evidence that IDAT
        // decompresses into actual valid image scanlines.
        byte[] rgbaPixels = [0, 255, 32, 64, 255];
        byte[] encoded = PngZlib(rgbaPixels);
        AcceptDecodedPng(PngWithData(encoded));
        int midpoint = Math.Max(1, encoded.Length / 2);
        byte[] splitData =
            [.. png[..33],
             .. PngChunk("IDAT", encoded[..midpoint]),
             .. PngChunk("IDAT", encoded[midpoint..]),
             .. png[^12..]];
        AcceptDecodedPng(splitData);

        // RGB and RGBA are supported, but only non-interlaced 8-bit.
        byte[] header = png.AsSpan(16, 13).ToArray();
        header[9] = 2;
        byte[] rgb =
            [.. png[..8], .. PngChunk("IHDR", header),
             .. PngChunk("IDAT", PngZlib([0, 1, 2, 3])),
             .. png[^12..]];
        AcceptDecodedPng(rgb);

        RejectInvalidContainer(PngWithData([0]));
        RejectInvalidContainer(PngWithData(PngZlib([0, 1])));
        RejectInvalidContainer(PngWithData(PngZlib([5, 0, 0, 0, 0])));
        RejectInvalidContainer(PngWithData(
            PngZlib([0, 0, 0, 0, 0, 255])));
        byte[] corruptZlib = (byte[])encoded.Clone();
        corruptZlib[^1] ^= 1;
        RejectInvalidContainer(PngWithData(corruptZlib));

        byte[] hugeHeader = png.AsSpan(16, 13).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            hugeHeader.AsSpan(0, 4), 8192);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            hugeHeader.AsSpan(4, 4), 8192);
        RejectInvalidContainer(
            [.. png[..8], .. PngChunk("IHDR", hugeHeader), .. png[33..]]);
        byte[] interlacedHeader = png.AsSpan(16, 13).ToArray();
        interlacedHeader[12] = 1;
        RejectInvalidContainer(
            [.. png[..8], .. PngChunk("IHDR", interlacedHeader), .. png[33..]]);

        const string foreignRunId = "bd851990-0eb4-42f1-8d2f-01a30389bdce";
        RejectBytes(bound with { RunId = foreignRunId }, png,
            "RUN_NONCE_MISMATCH");
        RejectBytes(bound with { ScreenshotRunId = foreignRunId }, png,
            "RUN_NONCE_MISMATCH");
        RejectBytes(bound with { Nonce = "another-run" }, png,
            "RUN_NONCE_MISMATCH");
        RejectBytes(bound with { SourceHead = new string('d', 40) }, png,
            "SOURCE_RUNNER_MISMATCH");
        RejectBytes(bound with { WorldTile = tile + 1 }, png,
            "SEED_TILE_MISMATCH");
        RejectBytes(bound with { GameVersion = "1.6.1" }, png,
            "GAME_VERSION_MISMATCH");
        RejectBytes(bound with { MapUniqueId = 18 }, png,
            "MAP_IDENTITY_MISMATCH");
        RejectBytes(bound with { ScreenshotCapturedUtc = started.AddHours(3) },
            png, "SCREENSHOT_RUN_TIME_MISMATCH");
        RejectBytes(bound with { CurrentLogVerified = false }, png,
            "CURRENT_LOG_UNVERIFIED");
        RejectBytes(bound with { CleanupComplete = false }, png,
            "OWNED_CLEANUP_UNVERIFIED");
    }

    private static MapEventExpectation MapFixture() => new(
        "NatureTilesQA", "naturetiles-genstep-outcome/v1",
        "naturetiles-map-smoke/v1", "map-abc12345", "TemperateForest",
        "brambleCount", "featureObserved", "placementValid", "completed", 350);

    // Exact field names and schema match the opt-in 16-file NatureTiles 1.7
    // source candidate; these strings are inert synthetic fixtures, NOT a game log.
    private static string[] MapEvents() =>
    [
        "[NatureTilesQA] {\"schemaVersion\":\"naturetiles-genstep-outcome/v1\",\"nonce\":\"map-abc12345\",\"mapUniqueId\":17,\"biome\":\"TemperateForest\",\"mapWidth\":250,\"mapHeight\":250,\"outcome\":\"completed\",\"chanceSelected\":true,\"newlyPlaced\":24}",
        "[NatureTilesQA] {\"schemaVersion\":\"naturetiles-map-smoke/v1\",\"nonce\":\"map-abc12345\",\"mapUniqueId\":17,\"biome\":\"TemperateForest\",\"mapWidth\":250,\"mapHeight\":250,\"gameTick\":1,\"brambleCount\":24,\"featureObserved\":true,\"placementValid\":true}"
    ];

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
