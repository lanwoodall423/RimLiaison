using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using RimError.Core;
using RimContext.Core;
using RimContext.Core.Semantics;
using RimContext.Core.Storage;
using RimLiaison;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.RimError;
using RimLiaison.RimContext;
using RimLiaison.Recovery;
using RimLiaison.Results;

namespace RimLiaison.Tests;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("valid catalog", ValidCatalogLoads),
        ("isolation metadata validates safe defaults", IsolationMetadataValidatesSafeDefaults),
        ("duplicate ids fail", DuplicateIdsFail),
        ("missing references fail", MissingReferencesFail),
        ("suite cycles fail", SuiteCyclesFail),
        ("missing recipes fail", MissingRecipesFail),
        ("recipe list loads", RecipeListLoads),
        ("list is minimal and sorted", ListIsMinimalAndSorted),
        ("environment fallback leaves list usable", EnvironmentFallbackLeavesListUsable),
        ("environment fallback leaves run usable", EnvironmentFallbackLeavesRunUsable),
        ("environment fallback leaves doctor usable", EnvironmentFallbackLeavesDoctorUsable),
        ("explicit fallback is rejected on unrelated command", ExplicitFallbackIsRejectedOnUnrelatedCommand),
        ("show exposes metadata", ShowExposesMetadata),
        ("missing run uses not-found contract", MissingRunUsesNotFoundContract),
        ("missing show and suite commands use not-found exit code", MissingShowAndSuiteCommandsUseNotFoundExitCode),
        ("suite run parse errors are not single-test results", SuiteRunParseErrorsAreNotSingleTestResults),
        ("suite and validation commands work", SuiteAndValidationCommandsWork),
        ("invalid catalog fails before command output", InvalidCatalogFailsBeforeCommand),
        ("catalog test delegates recipe id", CatalogTestDelegatesRecipeId),
        ("workflow correlation reaches DevBridge", WorkflowCorrelationReachesDevBridge),
        ("old DevBridge responses remain compatible", OldDevBridgeResponsesRemainCompatible),
        ("old DevBridge request parsers remain compatible", OldDevBridgeRequestParsersRemainCompatible),
        ("mismatched workflow ids fail closed", MismatchedWorkflowIdsFailClosed),
        ("catalog run CLI delegates execution", CatalogRunCliDelegatesExecution),
        ("run result categories are compact", RunResultCategoriesAreCompact),
        ("compact final output includes workflow id", CompactFinalOutputIncludesWorkflowId),
        ("agent output contracts are golden and bounded", AgentOutputContractsAreGoldenAndBounded),
        ("RimError diagnosis reads the Core store", RimErrorDiagnosisIsNormalized),
        ("DevBridge diagnostic source is bounded and generation scoped", DevBridgeDiagnosticSourceIsBoundedAndGenerationScoped),
        ("automatic diagnostics carry scoped identities", AutomaticDiagnosticsCarryScopedIdentities),
        ("normal CLI failure acquires diagnostics automatically", NormalCliFailureAcquiresDiagnosticsAutomatically),
        ("successful test skips diagnostic acquisition", SuccessfulTestSkipsDiagnosticAcquisition),
        ("stale diagnostic source cannot produce trustworthy result", StaleDiagnosticSourceCannotProduceTrustworthyResult),
        ("scoped RimError diagnosis filters nearby runs", ScopedRimErrorDiagnosisFiltersNearbyRuns),
        ("RimError scoped source uses Core in memory", RimErrorScopedSourceUsesCore),
        ("diagnostic failure preserves test failure", DiagnosticFailurePreservesTestFailure),
        ("recipe CLI delegates structured plan", RecipeCliDelegatesStructuredPlan),
        ("plan preserves no-launch result", PlanPreservesNoLaunchResult),
        ("successful recipe run is normalized", SuccessfulRecipeRunIsNormalized),
        ("recipe assertion failure is classified", RecipeAssertionFailureIsClassified),
        ("DevBridge refusal is classified", DevBridgeRefusalIsClassified),
        ("infrastructure failure is classified", InfrastructureFailureIsClassified),
        ("timeout is classified", TimeoutIsClassified),
        ("cancellation is classified", CancellationIsClassified),
        ("malformed response is classified", MalformedResponseIsClassified),
        ("incompatible schema is classified", IncompatibleSchemaIsClassified),
        ("RimContext direct coverage selects a test", RimContextDirectCoverageSelectsTest),
        ("RimContext transitive coverage selects a test", RimContextTransitiveCoverageSelectsTest),
        ("RimContext shared coverage is deduplicated", RimContextSharedCoverageIsDeduplicated),
        ("RimContext no impact is conservative", RimContextNoImpactSelectsNoTests),
        ("RimContext unknown impact uses fallback", RimContextUnknownImpactUsesFallback),
        ("RimContext unavailable is conservative", RimContextUnavailableIsConservative),
        ("RimContext selection ordering is deterministic", RimContextSelectionOrderingIsDeterministic),
        ("RimContext adapter uses Core", RimContextAdapterUsesCore),
        ("RimContext complete index stays ready", RimContextCompleteIndexStaysReady),
        ("RimContext partial index is recovered once", RimContextPartialIndexIsRecoveredOnce),
        ("RimContext failed recovery preserves diagnostics", RimContextFailedRecoveryPreservesDiagnostics),
        ("RimContext cancellation is bounded", RimContextCancellationIsBounded),
        ("affected CLI emits compact selection", AffectedCliEmitsCompactSelection),
        ("affected run pass is compact", AffectedRunPassIsCompact),
        ("affected zero impact uses fallback", AffectedZeroImpactUsesFallback),
        ("affected changed path without fallback blocks", AffectedChangedPathWithoutFallbackBlocks),
        ("suite all-pass aggregation is compact", SuiteAllPassAggregationIsCompact),
        ("empty suite execution is conservative", EmptySuiteExecutionIsConservative),
        ("suite one failure is summarized", SuiteOneFailureIsSummarized),
        ("suite multiple failures are deterministic", SuiteMultipleFailuresAreDeterministic),
        ("suite fail-fast stops after first failure", SuiteFailFastStopsAfterFirstFailure),
        ("suite fail-fast pass is complete", SuiteFailFastPassExecutesEverySelectedTest),
        ("fail-fast ordering surfaces cheap historical failures", FailFastOrderingTests.HistoricallyFailureProneCheapTestsMoveEarlier),
        ("fail-fast ordering moves expensive stable tests later", FailFastOrderingTests.ExpensiveStableTestsMoveLater),
        ("fail-fast ordering falls back without history", FailFastOrderingTests.NoHistoryFallsBackDeterministically),
        ("fail-fast ordering ignores corrupt stale and incompatible history", FailFastOrderingTests.CorruptStaleAndIncompatibleHistoryIsIgnored),
        ("fail-fast ordering falls back with insufficient history", FailFastOrderingTests.InsufficientHistoryFallsBackDeterministically),
        ("fail-fast ordering ignores partial group history", FailFastOrderingTests.PartialHistoryDoesNotPreferOneGroupMember),
        ("fail-fast ordering preserves selected membership", FailFastOrderingTests.SelectedTestMembershipNeverChanges),
        ("generation reuse safety dominates fail-fast ordering", FailFastOrderingTests.GenerationReuseSafetyDominatesHeuristicOrdering),
        ("fail-fast ordering keeps reuse groups contiguous", FailFastOrderingTests.HistoricalOrderingKeepsMultipleReuseGroupsContiguous),
        ("fail-fast ordering is deterministic for identical history", FailFastOrderingTests.IdenticalHistoryProducesIdenticalOrdering),
        ("synthetic history reduces expected first failure time", FailFastOrderingTests.SyntheticHistoryReducesExpectedFailureTimeWithoutNewTransitions),
        ("non-fail-fast execution remains complete", FailFastOrderingTests.NonFailFastExecutionRemainsComplete),
        ("fail-fast ordering result metadata is bounded", FailFastOrderingTests.ResultMetadataExplainsHistoricalOrderingBoundedly),
        ("fail-fast ordering context is versioned and bounded", FailFastOrderingTests.HistoricalOrderingContextIsVersionedAndBounded),
        ("suite cancellation stops new children", SuiteCancellationStopsNewChildren),
        ("suite duplicate tests execute once", SuiteDuplicateTestsExecuteOnce),
        ("suite plan refusal blocks execution", SuitePlanRefusalBlocksExecution),
        ("suite child infrastructure failure is summarized", SuiteChildInfrastructureFailureIsSummarized),
        ("unannotated recipes use the safe path", UnannotatedRecipesUseSafePath),
        ("unsafe recipes never share state", UnsafeRecipesNeverShareState),
        ("mutation recipes never share state", MutationRecipesNeverShareState),
        ("incompatible reuse profiles fall back safely", IncompatibleReuseProfilesFallBackSafely),
        ("reuse planner groups compatible tests deterministically", ReusePlannerGroupsCompatibleTestsDeterministically),
        ("reuse planner preserves hard boundaries", ReusePlannerPreservesHardBoundaries),
        ("grouped suite execution avoids lifecycle transitions", GroupedSuiteExecutionAvoidsLifecycleTransitions),
        ("reuse cancellation cannot contaminate later tests", ReuseCancellationCannotContaminateLaterTests),
        ("DevBridge reuse refusal preserves its cause", DevBridgeReuseRefusalPreservesCause),
        ("compatible recipes reuse one generation", CompatibleRecipesReuseOneGeneration),
        ("fail-fast preserves compatible reuse", FailFastPreservesCompatibleReuse),
        ("resettable recipes require successful reset", ResettableRecipesRequireSuccessfulReset),
        ("failed reset invalidates reuse", FailedResetInvalidatesReuse),
        ("test failure cannot contaminate later recipes", TestFailureCannotContaminateLaterRecipes),
        ("generation and lease changes invalidate reuse", GenerationAndLeaseChangesInvalidateReuse),
        ("reuse result remains bounded and identities stay distinct", ReuseResultIsBoundedAndIdentitiesStayDistinct),
        ("lease adapter preserves owner and generation identity", LeaseAdapterPreservesOwnerAndGenerationIdentity),
        ("existing compatible lease retries a blocked recipe", ExistingCompatibleLeaseRetriesBlockedRecipe),
        ("fresh generation adapter proves readiness conservatively", FreshGenerationAdapterProvesReadinessConservatively),
        ("affected run uses conservative fallback", AffectedRunUsesConservativeFallback),
        ("suite run CLI is deterministic", SuiteRunCliIsDeterministic),
        ("capabilities discover the registered surface", CapabilitiesDiscoverRegisteredSurface),
        ("capabilities query filters the registry", CapabilitiesQueryFiltersRegistry),
        ("capabilities bound output", CapabilitiesBoundOutput),
        ("capabilities preserve parameter metadata", CapabilitiesPreserveParameterMetadata),
        ("capabilities report unavailable bridge", CapabilitiesReportUnavailableBridge),
        ("capabilities reject malformed response", CapabilitiesRejectMalformedResponse),
        ("capabilities reject incompatible response", CapabilitiesRejectIncompatibleResponse),
        ("capability discovery does not mutate lifecycle", CapabilityDiscoveryDoesNotMutateLifecycle),
        ("ui target enumeration", UiTargetEnumeration),
        ("ui target object schema is supported", UiTargetObjectSchemaIsSupported),
        ("ui target discovery recovers a required lease", UiTargetDiscoveryRecoversRequiredLease),
        ("ui bridge calls carry workflow identity", UiBridgeCallsCarryWorkflowIdentity),
        ("ui targeted screenshot uses clipping", UiTargetedScreenshotUsesClipping),
        ("ui missing target fails before capture", UiMissingTargetFailsBeforeCapture),
        ("ui reports unavailable bridge", UiReportsUnavailableBridge),
        ("ui reports visual readiness failure", UiReportsVisualReadinessFailure),
        ("ui cell capture preserves camera", UiCellCapturePreservesCamera),
        ("ui requests do not mutate lifecycle", UiRequestsDoNotMutateLifecycle),
        ("transactional ui viewport captures and restores", TransactionalUiViewportCapturesAndRestores),
        ("transactional ui viewport restores after ui failure", TransactionalUiViewportRestoresAfterUiFailure),
        ("transactional ui viewport surfaces restoration failure", TransactionalUiViewportSurfacesRestorationFailure),
        ("transactional ui viewport validates explicit dimensions", TransactionalUiViewportValidatesExplicitDimensions),
        ("ui output is compact", UiOutputIsCompact),
        ("canonical UI guidance is generated", CanonicalUiGuidanceIsGenerated),
        ("doctor healthy output is compact", DoctorHealthyOutputIsCompact),
        ("doctor reads DevBridge RimBridge status shape", DoctorReadsDevBridgeRimBridgeStatusShape),
        ("doctor reports blocked component", DoctorReportsBlockedComponent),
        ("stack manifest defaults are used", StackManifestDefaultsAreUsed),
        ("explicit CLI overrides beat manifest", ExplicitCliOverridesBeatManifest),
        ("malformed stack schema is blocked", MalformedStackSchemaIsBlocked),
        ("unknown stack schema is blocked", UnknownStackSchemaIsBlocked),
        ("missing stack manifest is blocked", MissingStackManifestIsBlocked),
        ("local configuration does not leak", LocalConfigurationDoesNotLeak),
        ("init creates an empty repository handoff", InitCreatesEmptyRepositoryHandoff),
        ("init fills a missing manifest field safely", InitFillsMissingManifestFieldSafely),
        ("init merges explicit configuration safely", InitMergesExplicitConfigurationSafely),
        ("init preserves existing AGENTS", InitPreservesExistingAgents),
        ("init preserves existing manifest", InitPreservesExistingManifest),
        ("init is idempotent", InitIsIdempotent),
        ("init force behavior is intentional", InitForceBehaviorIsIntentional),
        ("manifest-only repair preserves AGENTS", ManifestOnlyRepairPreservesAgents),
        ("doctor missing project provides a handoff", DoctorMissingProjectProvidesHandoff),
        ("doctor missing fallback provides a handoff", DoctorMissingFallbackProvidesHandoff),
        ("doctor missing catalog provides a handoff", DoctorMissingCatalogProvidesHandoff),
        ("doctor invalid catalog provides a handoff", DoctorInvalidCatalogProvidesHandoff),
        ("affected discovers Git changes without paths", AffectedDiscoversGitChangesWithoutPaths),
        ("clean affected run is explicit and does not launch", CleanAffectedRunIsExplicitAndDoesNotLaunch),
        ("affected source run performs freshness transaction", AffectedSourceRunPerformsFreshnessTransaction),
        ("fail-fast affected run still proves freshness", FailFastAffectedRunStillProvesFreshness),
        ("affected companion recipe may use a later generation", AffectedCompanionRecipeMayUseLaterGeneration),
        ("affected identical artifact uses no-deploy proof", AffectedIdenticalArtifactUsesNoDeployProof),
        ("affected build failure blocks pass", AffectedBuildFailureBlocksPass),
        ("affected deployment failure blocks pass", AffectedDeploymentFailureBlocksPass),
        ("affected readiness failure blocks pass", AffectedReadinessFailureBlocksPass),
        ("affected run recovers readiness once", AffectedRunRecoversReadinessOnce),
        ("affected generation mismatch blocks pass", AffectedGenerationMismatchBlocksPass),
        ("affected unknown freshness blocks pass", AffectedUnknownFreshnessBlocksPass),
        ("affected incomplete freshness metadata blocks pass", AffectedIncompleteFreshnessMetadataBlocksPass),
        ("affected propagates transaction identities", AffectedPropagatesTransactionIdentities),
        ("mod-development adapter parses bounded freshness response", ModDevelopmentAdapterParsesBoundedFreshnessResponse),
        ("valid development descriptor is preserved", ValidDevelopmentDescriptorIsPreserved),
        ("missing development descriptor is derived", MissingDevelopmentDescriptorIsDerived),
        ("malformed development descriptor is repaired", MalformedDevelopmentDescriptorIsRepaired),
        ("stale development descriptor is reconciled safely", StaleDevelopmentDescriptorIsReconciledSafely),
        ("ambiguous development descriptor is blocked", AmbiguousDevelopmentDescriptorIsBlocked),
        ("lease recovery retries the owner transaction once", LeaseRecoveryRetriesOwnerTransactionOnce),
        ("lease contention remains explicit", LeaseContentionRemainsExplicit),
        ("lease recovery has no loop", LeaseRecoveryHasNoLoop),
        ("freshness cleanup failure remains visible", FreshnessCleanupFailureRemainsVisible),
        ("cleanup failure remains independent in orchestration", CleanupFailureRemainsIndependentInOrchestration),
        ("Git discovery includes staged and untracked files", GitDiscoveryIncludesStagedAndUntrackedFiles),
        ("Git discovery preserves deleted and renamed paths", GitDiscoveryPreservesDeletedAndRenamedPaths),
        ("explicit affected paths take precedence", ExplicitAffectedPathsTakePrecedence),
        ("environment fallback drives affected fallback", EnvironmentFallbackDrivesAffectedFallback),
        ("affected deleted path uses conservative fallback", AffectedDeletedPathUsesConservativeFallback),
        ("affected rename without fallback blocks", AffectedRenameWithoutFallbackBlocks),
        ("Git discovery failure is conservative", GitDiscoveryFailureIsConservative),
        ("RimError diagnosis provides drill-down next action", RimErrorDiagnosisProvidesNextAction),
        ("DevBridge failure provides doctor next action", DevBridgeFailureProvidesNextAction),
        ("RimContext stale result provides recovery next action", RimContextStaleProvidesNextAction),
        ("efficiency profiler aggregates compact schema", ProfilerTests.AggregatesCompactSchema),
        ("efficiency profiler groups repeated operations", ProfilerTests.GroupsRepeatedOperations),
        ("efficiency profiler groups failures and retries safely", ProfilerTests.GroupsFailuresAndRetries),
        ("efficiency profiler records unchanged generations", ProfilerTests.RecordsUnchangedGenerations),
        ("efficiency profiler redacts raw values", ProfilerTests.RedactsRawValues),
        ("efficiency profiler fingerprints are deterministic", ProfilerTests.FingerprintsAreDeterministic),
        ("efficiency profiler prioritizes specialized evidence", ProfilerTests.PrioritizesSpecializedEvidence),
        ("efficiency profiler bounds sections and total output", ProfilerTests.BoundsSectionsAndOutput),
        ("efficiency profiler preserves overflow totals", ProfilerTests.PreservesOverflowTotals),
        ("efficiency profiler failures do not alter command results", ProfilerTests.FailuresDoNotAlterCommandResults),
        ("efficiency profiler preserves CLI output contracts", ProfilerTests.PreservesCliOutputContracts),
        ("efficiency profiler emits success and failure profiles", ProfilerTests.EmitsSuccessAndFailureProfiles)
    ];

    public static int Main()
    {
        int failures = 0;
        foreach ((string name, Action test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ValidCatalogLoads()
    {
        CatalogValidationResult result = CatalogValidator.Validate(CreateCatalog());

        Assert(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Code)));
        AssertSequence(
            ["assembler-smoke", "settings-smoke"],
            CatalogNavigator.ResolvedTestIds(CreateCatalog(), "smoke"));
        AssertSequence(
            ["smoke"],
            CatalogNavigator.ContainingSuiteIds(CreateCatalog(), "assembler-smoke"));
    }

    private static void IsolationMetadataValidatesSafeDefaults()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest
            {
                Id = "missing-key",
                Recipe = "recipe-a",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.SameGenerationSafe
                }
            },
            new CatalogTest
            {
                Id = "missing-reset",
                Recipe = "recipe-b",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FixtureResettable,
                    ReuseKey = "fixture"
                }
            },
            new CatalogTest
            {
                Id = "unexpected-reset",
                Recipe = "recipe-c",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.PureRead,
                    ReuseKey = "fixture",
                    ResetRecipe = "reset"
                }
            });

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "ISOLATION_REUSE_KEY_REQUIRED");
        AssertHasCode(result.Errors, "ISOLATION_RESET_RECIPE_REQUIRED");
        AssertHasCode(result.Errors, "ISOLATION_RESET_RECIPE_UNEXPECTED");
    }

    private static void DuplicateIdsFail()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "other-fixture"
        });

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "TEST_ID_DUPLICATE");
    }

    private static void MissingReferencesFail()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Suites[0].Tests.Add("does-not-exist");
        catalog.Suites[0].Suites.Add("missing-suite");

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "UNKNOWN_TEST_REFERENCE");
        AssertHasCode(result.Errors, "UNKNOWN_SUITE_REFERENCE");
    }

    private static void SuiteCyclesFail()
    {
        var catalog = new CatalogDocument
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest { Id = "one", Recipe = "fixture" }
            ],
            Suites =
            [
                new CatalogSuite { Id = "a", Suites = ["b"] },
                new CatalogSuite { Id = "b", Suites = ["a"] }
            ]
        };

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "SUITE_CYCLE");
    }

    private static void MissingRecipesFail()
    {
        CatalogValidationResult result = CatalogValidator.Validate(
            CreateCatalog(),
            new HashSet<string>(["settings-fixture"], StringComparer.Ordinal));

        AssertHasCode(result.Errors, "MISSING_RECIPE_REFERENCE");
        Assert(!result.IsValid, "Unknown recipes must invalidate a checked catalog.");
    }

    private static void RecipeListLoads()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "recipes.json");
            File.WriteAllText(
                path,
                """
                {
                  "schemaVersion": "devbridge-test-recipe-list/v1",
                  "recipes": [
                    {"id": "assembler-fixture"},
                    {"id": "settings-fixture"}
                  ]
                }
                """);

            RecipeListLoadResult result = RecipeListLoader.Load(path);

            Assert(result.Errors.Count == 0, "Recipe list should load.");
            Assert(result.RecipeIds is not null, "Recipe ids should be returned.");
            Assert(result.RecipeIds!.Contains("assembler-fixture"), "Recipe id is missing.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ListIsMinimalAndSorted()
    {
        CliResult result = RunCli(CreateCatalog(), "list");

        AssertEqual(
            """{"tests":[{"id":"assembler-smoke","recipe":"assembler-fixture"},{"id":"settings-smoke","recipe":"settings-fixture"}]}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(string.IsNullOrEmpty(result.Stderr), "List should not write diagnostics.");
    }

    private static void EnvironmentFallbackLeavesListUsable()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            "smoke",
            () => RunCli(CreateCatalog(), "list"));

        AssertEqual(
            """{"tests":[{"id":"assembler-smoke","recipe":"assembler-fixture"},{"id":"settings-smoke","recipe":"settings-fixture"}]}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add list diagnostics.");
    }

    private static void EnvironmentFallbackLeavesRunUsable()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        CliResult result = WithFallbackSuiteEnvironment(
            "settings",
            () => RunCatalogCliWithAdapter(
                CreateCatalog(),
                adapter,
                "run",
                "assembler-smoke"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual("assembler-smoke", root.GetProperty("test").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add run diagnostics.");
    }

    private static void EnvironmentFallbackLeavesDoctorUsable()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            "settings",
            () => RunDoctorFixture(contextAvailable: true));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ready", document.RootElement.GetProperty("status").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add doctor diagnostics.");
    }

    private static void ExplicitFallbackIsRejectedOnUnrelatedCommand()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            null,
            () => RunCli(CreateCatalog(), "list", "--fallback-suite", "smoke"));

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("error", document.RootElement.GetProperty("status").GetString());
        AssertEqual("CLI_INVALID", document.RootElement.GetProperty("code").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An invalid fallback option must remain machine-readable.");
    }

    private static void MissingRunUsesNotFoundContract()
    {
        CliResult result = RunCli(CreateCatalog(), "run", "does-not-exist");

        AssertEqual(CliExitCodes.NotFound, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("rimtest-result/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("invalid", root.GetProperty("status").GetString());
        AssertEqual("does-not-exist", root.GetProperty("test").GetString());
        AssertEqual("TEST_NOT_FOUND", root.GetProperty("errorCode").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "A missing test must not write a human stderr transcript.");
    }

    private static void MissingShowAndSuiteCommandsUseNotFoundExitCode()
    {
        CliResult show = RunCli(CreateCatalog(), "show", "does-not-exist");
        CliResult suiteShow = RunCli(CreateCatalog(), "suite", "show", "does-not-exist");
        CliResult suiteRun = RunCli(CreateCatalog(), "suite", "run", "does-not-exist");

        AssertEqual(CliExitCodes.NotFound, show.ExitCode);
        AssertEqual(CliExitCodes.NotFound, suiteShow.ExitCode);
        AssertEqual(CliExitCodes.NotFound, suiteRun.ExitCode);
        AssertEqual(
            "TEST_NOT_FOUND",
            JsonDocument.Parse(show.Stdout).RootElement.GetProperty("code").GetString());
        AssertEqual(
            "SUITE_NOT_FOUND",
            JsonDocument.Parse(suiteShow.Stdout).RootElement.GetProperty("code").GetString());
        AssertEqual(
            "SUITE_NOT_FOUND",
            JsonDocument.Parse(suiteRun.Stdout).RootElement.GetProperty("code").GetString());
        Assert(string.IsNullOrEmpty(show.Stderr) &&
            string.IsNullOrEmpty(suiteShow.Stderr) &&
            string.IsNullOrEmpty(suiteRun.Stderr),
            "Not-found commands must not write human stderr transcripts.");
    }

    private static void SuiteRunParseErrorsAreNotSingleTestResults()
    {
        CliResult result = RunCli(
            CreateCatalog(),
            "suite",
            "run",
            "smoke",
            "--fallback-suite",
            "settings");

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("CLI_INVALID", root.GetProperty("code").GetString());
        Assert(!root.TryGetProperty("schemaVersion", out _),
            "A suite parse error must not use the single-test result schema.");
        Assert(!result.Stdout.Contains("rimtest-result/v1", StringComparison.Ordinal),
            "A suite parse error must not pretend the suite name is a test id.");

        CliResult incompleteFailFast = RunCli(
            CreateCatalog(),
            "affected",
            "--fail-fast");
        AssertEqual(CliExitCodes.InvalidInput, incompleteFailFast.ExitCode);
        AssertEqual(
            "CLI_INVALID",
            JsonDocument.Parse(incompleteFailFast.Stdout)
                .RootElement
                .GetProperty("code")
                .GetString());
    }

    private static void ShowExposesMetadata()
    {
        CliResult result = RunCli(CreateCatalog(), "show", "assembler-smoke");

        AssertEqual(
            """{"test":{"id":"assembler-smoke","recipe":"assembler-fixture","cost":"low","suites":["smoke"],"description":"Checks assembler registration.","tags":["assembler","crafting"],"covers":[{"kind":"csharp_type","name":"CompAssembler"},{"kind":"def","name":"CCM_Assembler"}]}}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
    }

    private static void InvalidCatalogFailsBeforeCommand()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "duplicate"
        });

        CliResult result = RunCli(catalog, "list");

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        Assert(result.Stdout.Contains(
            "\"code\":\"CATALOG_INVALID\"",
            StringComparison.Ordinal), "Invalid catalog error was not returned.");
        Assert(!result.Stdout.Contains(
            "\"tests\":[",
            StringComparison.Ordinal), "Invalid catalog must not produce a list.");
    }

    private static void SuiteAndValidationCommandsWork()
    {
        CliResult suites = RunCli(CreateCatalog(), "suites");
        AssertEqual(
            """{"suites":[{"id":"settings"},{"id":"smoke"}]}""",
            suites.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, suites.ExitCode);

        CliResult suite = RunCli(CreateCatalog(), "suite", "show", "smoke");
        AssertEqual(
            """{"suite":{"id":"smoke","tests":["assembler-smoke"],"suites":["settings"],"resolvedTests":["assembler-smoke","settings-smoke"]}}""",
            suite.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, suite.ExitCode);

        CliResult validation = RunCli(CreateCatalog(), "validate");
        AssertEqual(
            """{"valid":true,"tests":2,"suites":2,"recipeVerification":"skipped"}""",
            validation.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, validation.ExitCode);
    }

    private static void CatalogTestDelegatesRecipeId()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": true,
                  "generation": 3,
                  "finalNextAction": "status"
                }
                """));
        var runner = new CatalogTestRecipeRunner(CreateAdapter(transport));

        CatalogTestRunResult result = runner.RunAsync(
                CreateCatalog(),
                "assembler-smoke")
            .GetAwaiter()
            .GetResult();

        AssertEqual("assembler-smoke", result.TestId);
        AssertEqual("assembler-fixture", result.RecipeId);
        AssertEqual(DevBridgeOutcomeKind.Success, result.RecipeResult.Status.Outcome);
        AssertEqual("assembler-fixture", transport.Requests.Single().Arguments[5]);
    }

    private static void WorkflowCorrelationReachesDevBridge()
    {
        const string workflowId = "rw-correlation-1";
        var transport = new FakeTransport(
            (request, _) =>
            {
                int index = request.Arguments.ToList().IndexOf("--workflow-id");
                Assert(index >= 0 && request.Arguments[index + 1] == workflowId,
                    "RimLiaison did not pass workflowId to DevBridge.");
                return ProcessResult(
                    $$"""
                    {
                      "schemaVersion":"devbridge-test-recipe-run/v1",
                      "recipe":"fixture",
                      "success":true,
                      "workflowId":"{{workflowId}}",
                      "generation":2,
                      "operations":[
                        {"tool":"rimworld/fixture","success":true,"operationId":"op-1","workflowId":"{{workflowId}}","generation":2,"launchId":"launch-2"}
                      ]
                    }
                    """);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", workflowId)
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual(workflowId, result.WorkflowId);
        AssertEqual("op-1", result.Operations.Single().OperationId);
        AssertEqual(workflowId, result.Operations.Single().WorkflowId);
    }

    private static void OldDevBridgeResponsesRemainCompatible()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion":"devbridge-test-recipe-run/v1",
                  "recipe":"fixture",
                  "success":true,
                  "generation":2
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-old-response")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual("rw-old-response", result.WorkflowId);
    }

    private static void OldDevBridgeRequestParsersRemainCompatible()
    {
        int calls = 0;
        var transport = new FakeTransport(
            (request, _) =>
            {
                calls++;
                bool hasWorkflowOption = request.Arguments.Contains(
                    "--workflow-id", StringComparer.Ordinal);
                if (calls == 1)
                {
                    Assert(hasWorkflowOption, "The first request must carry workflowId.");
                    return ProcessResult(
                        """
                        {
                          "schemaVersion":"devbridge-test-recipe-run/v1",
                          "recipe":"fixture",
                          "success":false,
                          "errorCode":"TEST_RECIPE_USAGE",
                          "error":"unknown recipe run option."
                        }
                        """,
                        exitCode: 2);
                }

                Assert(!hasWorkflowOption, "Compatibility retry must omit workflowId.");
                return ProcessResult(
                    """
                    {
                      "schemaVersion":"devbridge-test-recipe-run/v1",
                      "recipe":"fixture",
                      "success":true,
                      "generation":2
                    }
                    """);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-old-parser")
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, calls);
        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual("rw-old-parser", result.WorkflowId);
    }

    private static void MismatchedWorkflowIdsFailClosed()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion":"devbridge-test-recipe-run/v1",
                  "recipe":"fixture",
                  "success":true,
                  "workflowId":"rw-other"
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-requested")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.MalformedResponse, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_WORKFLOW_ID_MISMATCH", result.Status.ErrorCode);
        AssertEqual("rw-requested", result.WorkflowId);
    }

    private static void CompactFinalOutputIncludesWorkflowId()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");

        CliResult result = RunCatalogCliWithAdapter(
            CreateCatalog(),
            adapter,
            "run",
            "assembler-smoke",
            "--json");
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;

        Assert(root.TryGetProperty("workflowId", out JsonElement workflow) &&
               workflow.GetString()!.StartsWith("rw-", StringComparison.Ordinal),
            "RimLiaison final output did not expose the workflow id.");
        Assert(!root.TryGetProperty("operations", out _),
            "RimLiaison final output embedded operation telemetry.");
        Assert(!result.Stdout.Contains("Player.log", StringComparison.Ordinal),
            "RimLiaison final output embedded log content.");
    }

    private static void PlanPreservesNoLaunchResult()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "fixture",
                  "alreadySatisfied": true,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "none",
                  "blockedBy": []
                }
                """));

        DevBridgeRecipePlanResult result = CreateAdapter(transport)
            .PlanAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        Assert(result.Plan is not null, "Plan was not returned.");
        Assert(result.Plan!.AlreadySatisfied, "Plan should be already satisfied.");
        AssertEqual(0, result.Plan.EstimatedRimWorldLaunches);
        AssertEqual("plan", transport.Requests.Single().Arguments[4]);
    }

    private static void RecipeCliDelegatesStructuredPlan()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "fixture",
                  "alreadySatisfied": true,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "none",
                  "blockedBy": []
                }
                """));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exitCode = CliApplication.Run(
            ["recipe", "plan", "fixture"],
            stdout,
            stderr,
            CreateAdapter(transport));

        AssertEqual(CliExitCodes.Success, exitCode);
        Assert(stdout.ToString().Contains(
            "\"alreadySatisfied\":true",
            StringComparison.Ordinal), "CLI did not return the structured plan.");
        AssertEqual("plan", transport.Requests.Single().Arguments[4]);
    }

    private static void CatalogRunCliDelegatesExecution()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": true,
                  "generation": 8,
                  "finalNextAction": "status"
                }
                """));
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.Run(
                ["run", "assembler-smoke", "--json", "--catalog", catalogPath],
                stdout,
                stderr,
                CreateAdapter(transport));

            AssertEqual(CliExitCodes.Success, exitCode);
            Assert(stdout.ToString().Contains(
                "\"test\":\"assembler-smoke\"",
                StringComparison.Ordinal), "Catalog test id was not reported.");
            Assert(stdout.ToString().Contains(
                "\"schemaVersion\":\"rimtest-result/v1\"",
                StringComparison.Ordinal), "Catalog run result schema was not reported.");
            Assert(!stdout.ToString().Contains(
                "\"recipe\"",
                StringComparison.Ordinal), "Catalog run should not copy recipe payload data.");
            AssertEqual("run", transport.Requests.Single().Arguments[4]);
            AssertEqual("assembler-fixture", transport.Requests.Single().Arguments[5]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunResultCategoriesAreCompact()
    {
        CliResult pass = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": true,
                          "runId": "run-8",
                          "generation": 8,
                          "finalNextAction": "status"
                        }
                        """))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Success, pass.ExitCode);
        Assert(pass.Stdout.Contains(
            "\"schemaVersion\":\"rimtest-result/v1\"",
            StringComparison.Ordinal), "Pass schema version is missing.");
        Assert(pass.Stdout.Contains(
            "\"status\":\"pass\"",
            StringComparison.Ordinal), "Pass status is missing.");
        Assert(pass.Stdout.Contains(
            "\"runId\":\"run-8\"",
            StringComparison.Ordinal), "Pass run id was lost.");
        Assert(!pass.Stdout.Contains(
            "\"operations\"",
            StringComparison.Ordinal), "Pass output contains operation telemetry.");
        Assert(!pass.Stdout.Contains(
            "\"recipe\"",
            StringComparison.Ordinal), "Pass output contains redundant recipe data.");

        CliResult failure = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": false,
                          "generation": 8,
                          "failureFingerprint": "fp-8",
                          "evidenceId": "evidence-8",
                          "errorCode": "RECIPE_ASSERTION_FAILED",
                          "operations": [
                            {
                              "tool": "rimworld/fixture",
                              "success": false,
                              "errorCode": "RECIPE_ASSERTION_FAILED",
                              "assertions": [
                                {"pointer": "/value", "success": false}
                              ]
                            }
                          ]
                        }
                        """,
                        exitCode: 4))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.TestFailure, failure.ExitCode);
        Assert(failure.Stdout.Contains(
            "\"status\":\"fail\"",
            StringComparison.Ordinal), "Failure status is missing.");
        Assert(failure.Stdout.Contains(
            "\"failureFingerprint\":\"fp-8\"",
            StringComparison.Ordinal), "Failure fingerprint was lost.");
        Assert(failure.Stdout.Contains(
            "\"evidenceId\":\"evidence-8\"",
            StringComparison.Ordinal), "Evidence id was lost.");
        Assert(!failure.Stdout.Contains(
            "\"operations\"",
            StringComparison.Ordinal), "Failure output contains operation telemetry.");

        CliResult infrastructure = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        "hidden diagnostic",
                        StartError: "DevBridge did not start."))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InternalError, infrastructure.ExitCode);
        Assert(infrastructure.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "Infrastructure status is missing.");
        Assert(!infrastructure.Stdout.Contains(
            "hidden diagnostic",
            StringComparison.Ordinal), "Raw stderr leaked into the result.");

        CliResult refusal = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": false,
                          "errorCode": "TEST_RECIPE_NOT_FOUND",
                          "error": "recipe was refused"
                        }
                        """,
                        exitCode: 4))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InternalError, refusal.ExitCode);
        Assert(refusal.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "DevBridge refusal should be infrastructure output.");

        CliResult cancelled = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        string.Empty,
                        Cancelled: true))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Cancelled, cancelled.ExitCode);
        Assert(cancelled.Stdout.Contains(
            "\"status\":\"cancelled\"",
            StringComparison.Ordinal), "Cancellation status is missing.");

        CatalogDocument invalidCatalog = CreateCatalog();
        invalidCatalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "duplicate"
        });
        CliResult invalid = RunCatalogCliWithAdapter(
            invalidCatalog,
            CreateAdapter(
                new FakeTransport(
                    (_, _) => throw new InvalidOperationException(
                        "execution must not be reached"))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InvalidInput, invalid.ExitCode);
        Assert(invalid.Stdout.Contains(
            "\"status\":\"invalid\"",
            StringComparison.Ordinal), "Invalid status is missing.");

        CliResult timeout = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        string.Empty,
                        TimedOut: true))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Timeout, timeout.ExitCode);
        Assert(timeout.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "Timeout should be compact infrastructure output.");
    }

    private static void AgentOutputContractsAreGoldenAndBounded()
    {
        string pass = CatalogJsonFacade.Serialize(new RimTestResult
        {
            Status = "pass",
            Test = "assembler-smoke",
            DurationMs = 4821,
            RunId = "run-123"
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-result/v1\",\"status\":\"pass\",\"test\":\"assembler-smoke\",\"durationMs\":4821,\"runId\":\"run-123\"}",
            pass);
        Assert(RimTestOutputBudgets.Utf8Bytes(pass) <=
            RimTestOutputBudgets.SingleTestPassMaxBytes,
            "Single-test pass exceeded its normal output budget.");

        string failure = CatalogJsonFacade.Serialize(new RimTestResult
        {
            Status = "fail",
            Test = "assembler-smoke",
            DurationMs = 4821,
            RunId = "run-123",
            Generation = 7,
            FailureFingerprint = "fp-123",
            EvidenceId = "evidence-123",
            ErrorCode = "RECIPE_ASSERTION_FAILED",
            Diagnostic = new RimTestDiagnosticSummary
            {
                Id = "RE-81F72",
                Category = "runtime",
                Method = "CCM.CompAssembler.Tick",
                Source = "Source/Comps/CompAssembler.cs",
                Line = 131
            }
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-result/v1\",\"status\":\"fail\",\"test\":\"assembler-smoke\",\"durationMs\":4821,\"runId\":\"run-123\",\"generation\":7,\"failureFingerprint\":\"fp-123\",\"evidenceId\":\"evidence-123\",\"errorCode\":\"RECIPE_ASSERTION_FAILED\",\"diagnostic\":{\"id\":\"RE-81F72\",\"category\":\"runtime\",\"method\":\"CCM.CompAssembler.Tick\",\"source\":\"Source/Comps/CompAssembler.cs\",\"line\":131}}",
            failure);
        Assert(RimTestOutputBudgets.Utf8Bytes(failure) <=
            RimTestOutputBudgets.SingleTestFailureMaxBytes,
            "Single-test failure exceeded its normal output budget.");

        string suite = CatalogJsonFacade.Serialize(new RimTestSuiteResult
        {
            Status = "pass",
            Suite = "smoke",
            Passed = 7,
            Failed = 0,
            DurationMs = 18432
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-suite-result/v1\",\"status\":\"pass\",\"suite\":\"smoke\",\"passed\":7,\"failed\":0,\"durationMs\":18432}",
            suite);
        Assert(RimTestOutputBudgets.Utf8Bytes(suite) <=
            RimTestOutputBudgets.SuitePassMaxBytes,
            "Suite pass exceeded its normal output budget.");

        string selection = CatalogJsonFacade.Serialize(new RimTestSelectionResult
        {
            Status = "ok",
            Tests = ["assembler-smoke", "recipe-smoke"],
            ReasonCount = 3
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-selection/v1\",\"status\":\"ok\",\"tests\":[\"assembler-smoke\",\"recipe-smoke\"],\"reasonCount\":3}",
            selection);
        Assert(RimTestOutputBudgets.Utf8Bytes(selection) <=
            RimTestOutputBudgets.AffectedSelectionMaxBytes,
            "Affected selection exceeded its normal output budget.");

        string affectedSuite = CatalogJsonFacade.Serialize(new RimTestSuiteResult
        {
            Status = "pass",
            Suite = "affected",
            Passed = 2,
            Failed = 0,
            DurationMs = 4821
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-suite-result/v1\",\"status\":\"pass\",\"suite\":\"affected\",\"passed\":2,\"failed\":0,\"durationMs\":4821}",
            affectedSuite);
        Assert(RimTestOutputBudgets.Utf8Bytes(affectedSuite) <=
            RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Affected suite pass exceeded its normal output budget.");
    }

    private static void RimErrorDiagnosisIsNormalized()
    {
        string directory = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(directory, "latest.json");
            var store = new JsonFileDiagnosticStore(storePath);
            store.WriteAsync(new DiagnosticStoreSnapshot
            {
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "RE-81F72",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "controlled failure",
                        OriginatingType = "CCM.CompAssembler",
                        OriginatingMethod = "Tick",
                        SourceFile = "Source/Comps/CompAssembler.cs",
                        SourceLine = 131,
                        RunId = "run-7",
                        TestId = "assembler-smoke",
                        OccurrenceCount = 20
                    }
                ]
            }).GetAwaiter().GetResult();

            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = storePath
                });
            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "assembler-smoke",
                        "run-7",
                        7,
                        "evidence-7",
                        "fp-7",
                        "RECIPE_ASSERTION_FAILED"))
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            AssertEqual("RE-81F72", result.Diagnosis!.Id);
            AssertEqual("runtime", result.Diagnosis.Category);
            AssertEqual("CCM.CompAssembler.Tick", result.Diagnosis.Method);
            AssertEqual("Source/Comps/CompAssembler.cs", result.Diagnosis.Source);
            AssertEqual(131, result.Diagnosis.Line);

            var diagnosisAdapter = new FakeRimErrorDiagnosisAdapter(result);
            CliResult cli = RunCatalogCliWithAdapters(
                CreateCatalog(),
                CreateAdapter(
                    new FakeTransport(
                        (_, _) => ProcessResult(
                            """
                            {
                              "schemaVersion": "devbridge-test-recipe-run/v1",
                              "recipe": "assembler-fixture",
                              "success": false,
                              "failureFingerprint": "fp-7",
                              "evidence": "evidence-7",
                              "errorCode": "RECIPE_ASSERTION_FAILED",
                              "operations": [
                                {
                                  "tool": "rimworld/fixture",
                                  "success": false,
                                  "assertions": [
                                    {"pointer": "/value", "success": false}
                                  ]
                                }
                              ]
                            }
                            """,
                            exitCode: 4))),
                diagnosisAdapter,
                "run",
                "assembler-smoke");
            AssertEqual(CliExitCodes.TestFailure, cli.ExitCode);
            Assert(!cli.Stdout.Contains(
                "\"diagnosticStatus\":\"available\"",
                StringComparison.Ordinal), "Available diagnosis should not repeat a redundant status.");
            Assert(cli.Stdout.Contains(
                "\"id\":\"RE-81F72\"",
                StringComparison.Ordinal), "Diagnostic id is missing.");
            Assert(cli.Stdout.Contains(
                "\"source\":\"Source/Comps/CompAssembler.cs\"",
                StringComparison.Ordinal), "Diagnostic source is missing.");
            Assert(!cli.Stdout.Contains(
                "\"symbol\"",
                StringComparison.Ordinal), "Default output should not copy the full RimError summary.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimErrorScopedSourceUsesCore()
    {
        string directory = CreateTempDirectory();
        const string sourceContent = "System.NullReferenceException: controlled failure\n   at Fixture.Test()\n";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(sourceContent);
            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = Path.Combine(directory, "latest.json")
                });
            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "fixture-test",
                        "run-scoped",
                        3,
                        "evidence-scoped",
                        "fingerprint",
                        "RECIPE_ASSERTION_FAILED",
                        ScopedSource: new RimErrorScopedDiagnosticSource(
                            RimErrorSchemas.ScopedDiagnosticSource,
                            3,
                            sourceContent,
                            bytes.Length,
                            1,
                            false,
                            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())))
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            Assert(result.Diagnosis is not null, "The in-memory scoped source was not diagnosed.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DevBridgeDiagnosticSourceIsBoundedAndGenerationScoped()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 1,
                  "sinceLaunch": true,
                  "available": true,
                  "rawBytes": 4096,
                  "semanticBytes": 128,
                  "truncated": false,
                  "records": [
                    {
                      "sequence": 1,
                      "generation": 1,
                      "sinceLaunch": true,
                      "severity": "ERROR",
                      "component": "RimWorld",
                      "message": "controlled fixture failure",
                      "stackFrames": ["at Fixture.Test()"]
                    }
                  ]
                }
                """));
        var adapter = new DevBridgeDiagnosticSourceAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeDiagnosticSourceResult result = adapter.AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        Assert(result.Status.IsAvailable, "The bounded source should be available.");
        Assert(result.Source is not null, "The source payload is missing.");
        AssertEqual(1, result.Source!.Generation);
        Assert(result.Source.Content.Contains("controlled fixture failure", StringComparison.Ordinal),
            "The semantic log message was not projected.");
        Assert(result.Source.Content.Contains("at Fixture.Test()", StringComparison.Ordinal),
            "The semantic stack frame was not projected.");
        Assert(result.Source.SourceBytes <= 64 * 1024, "The source exceeded its bound.");
        AssertEqual(64, result.Source.Sha256.Length);
        Assert(transport.Requests[0].Arguments.Contains("--since-launch"),
            "The query was not launch scoped.");
        Assert(transport.Requests[0].Arguments.Contains("--generation") &&
            transport.Requests[0].Arguments.Contains("1"),
            "The query was not generation scoped.");

        var staleTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 2,
                  "sinceLaunch": true,
                  "available": true,
                  "truncated": false,
                  "records": []
                }
                """));
        DevBridgeDiagnosticSourceResult stale = new DevBridgeDiagnosticSourceAdapter(
                staleTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        AssertEqual(
            "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH",
            stale.Status.ErrorCode);
        Assert(stale.Source is null, "A stale source must not be handed to RimError.");

        var missingTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 1,
                  "sinceLaunch": true,
                  "available": false,
                  "truncated": false,
                  "records": [],
                  "errorCode": "PLAYER_LOG_UNAVAILABLE"
                }
                """));
        DevBridgeDiagnosticSourceResult missing = new DevBridgeDiagnosticSourceAdapter(
                missingTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        AssertEqual("PLAYER_LOG_UNAVAILABLE", missing.Status.ErrorCode);

        string[] records = Enumerable.Range(1, 64)
            .Select(index =>
                $$"""{"sequence":{{index}},"generation":1,"sinceLaunch":true,"severity":"ERROR","component":"RimWorld","message":"{{new string('x', 2048)}}"}""")
            .ToArray();
        string oversizedJson =
            $$"""{"schemaVersion":"devbridge-logs-query/v1","contract":"devbridge-logs-query/v1","success":true,"generation":1,"sinceLaunch":true,"available":true,"truncated":false,"records":[{{string.Join(',', records)}}]}""";
        var oversizedTransport = new FakeTransport((_, _) => ProcessResult(oversizedJson));
        DevBridgeDiagnosticSourceResult oversized = new DevBridgeDiagnosticSourceAdapter(
                oversizedTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        Assert(oversized.Source is not null, "The bounded source should remain usable.");
        Assert(oversized.Source!.Truncated, "Oversized semantic evidence was not marked truncated.");
        Assert(oversized.Source.SourceBytes <= 64 * 1024,
            "Oversized semantic evidence exceeded the source bound.");
    }

    private static void AutomaticDiagnosticsCarryScopedIdentities()
    {
        const string workflowId = "workflow-diagnostic-1";
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-scoped",
            "RECIPE_ASSERTION_FAILED",
            generation: 7,
            workflowId: workflowId,
            operations: [new DevBridgeOperationSummary(
                "rimworld/fixture",
                false,
                "RECIPE_ASSERTION_FAILED",
                ["/value"],
                "operation-diagnostic-1",
                workflowId,
                7,
                "launch-diagnostic-1")]);
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("RE-scoped"));
        var source = new FakeDiagnosticSourceAdapter(AvailableSource(7));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp(),
                workflowId: workflowId)
            .GetAwaiter()
            .GetResult();

        AssertEqual("fail", execution.Result.Status);
        AssertEqual("RE-scoped", execution.Result.Diagnostic!.Id);
        AssertEqual(1, source.Calls);
        AssertEqual(1, diagnosis.Calls);
        RimErrorDiagnosisRequest request = diagnosis.Request!;
        AssertEqual("workflow-diagnostic-1", request.WorkflowId);
        AssertEqual("run-assembler-fixture", request.RunId);
        AssertEqual(7, request.Generation);
        AssertEqual("operation-diagnostic-1", request.Operations![0].OperationId);
        AssertEqual("rimtest-devbridge-diagnostic-source/v1", request.ScopedSource!.SchemaVersion);
        AssertEqual(7, request.ScopedSource.Generation);
    }

    private static void NormalCliFailureAcquiresDiagnosticsAutomatically()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var recipe = new FakeRecipeAdapter();
            recipe.Runs["assembler-fixture"] = FailedRun(
                "assembler-fixture",
                "fp-cli-auto",
                "RECIPE_ASSERTION_FAILED");
            var transport = new FakeTransport(
                (request, _) => request.Arguments[0] switch
                {
                    "logs" => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-logs-query/v1",
                          "contract": "devbridge-logs-query/v1",
                          "success": true,
                          "generation": 1,
                          "sinceLaunch": true,
                          "available": true,
                          "rawBytes": 64,
                          "semanticBytes": 64,
                          "truncated": false,
                          "records": [
                            {
                              "sequence": 1,
                              "generation": 1,
                              "sinceLaunch": true,
                              "severity": "ERROR",
                              "component": "RimWorld",
                              "message": "System.NullReferenceException: controlled CLI failure",
                              "stackFrames": ["at Fixture.Test()"]
                            }
                          ]
                        }
                        """),
                    _ => throw new InvalidOperationException(
                        "Unexpected process in automatic diagnostic test: " +
                        request.Arguments[0])
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        [
                            "run",
                            "assembler-smoke",
                            "--json",
                            "--catalog",
                            catalogPath
                        ],
                        stdout,
                        stderr,
                        recipe,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement result = document.RootElement;
            AssertEqual(CliExitCodes.TestFailure, exitCode);
            AssertEqual("fail", result.GetProperty("status").GetString());
            Assert(result.GetProperty("diagnostic").GetProperty("id").GetString() is { Length: > 0 },
                "The direct RimError.Core diagnosis did not produce a bounded diagnostic id.");
            Assert(transport.Requests.Any(request => request.Arguments[0] == "logs"),
                "The normal CLI did not acquire a DevBridge diagnostic source.");
            Assert(!transport.Requests.Any(request => request.Arguments[0] is "ingest" or "latest"),
                "The normal CLI crossed the obsolete RimError CLI boundary.");
            Assert(!stdout.ToString().Contains("Player.log", StringComparison.Ordinal),
                "The normal failure result exposed a Player.log path.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuccessfulTestSkipsDiagnosticAcquisition()
    {
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("unused"));
        var source = new FakeDiagnosticSourceAdapter(AvailableSource(7));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp())
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", execution.Result.Status);
        AssertEqual(0, source.Calls);
        AssertEqual(0, diagnosis.Calls);
    }

    private static void StaleDiagnosticSourceCannotProduceTrustworthyResult()
    {
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-stale",
            "RECIPE_ASSERTION_FAILED");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("must-not-use"));
        var source = new FakeDiagnosticSourceAdapter(
            new DevBridgeDiagnosticSourceResult(
                new DevBridgeDiagnosticSourceStatus(
                    DevBridgeDiagnosticSourceOutcome.Unavailable,
                    "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH"),
                null));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp())
            .GetAwaiter()
            .GetResult();

        AssertEqual("fail", execution.Result.Status);
        AssertEqual("unavailable", execution.Result.DiagnosticStatus);
        AssertEqual(
            "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH",
            execution.Result.DiagnosticErrorCode);
        AssertEqual(0, diagnosis.Calls);
    }

    private static void ScopedRimErrorDiagnosisFiltersNearbyRuns()
    {
        string directory = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(directory, "latest.json");
            new JsonFileDiagnosticStore(storePath).WriteAsync(new DiagnosticStoreSnapshot
            {
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "RE-other",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "other run",
                        RunId = "run-other"
                    },
                    new DiagnosticRecord
                    {
                        Id = "RE-current",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "current run",
                        RunId = "run-current"
                    }
                ]
            }).GetAwaiter().GetResult();
            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = storePath
                });

            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "assembler-smoke",
                        "run-current",
                        7,
                        "evidence-current",
                        "fp-current",
                        "RECIPE_ASSERTION_FAILED"))
                .GetAwaiter()
                .GetResult();
            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            AssertEqual("RE-current", result.Diagnosis!.Id);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DiagnosticFailurePreservesTestFailure()
    {
        var recipeTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": false,
                  "failureFingerprint": "fp-preserved",
                  "evidence": "evidence-preserved",
                  "errorCode": "RECIPE_ASSERTION_FAILED",
                  "operations": [
                    {
                      "tool": "rimworld/fixture",
                      "success": false,
                      "assertions": [
                        {"pointer": "/value", "success": false}
                      ]
                    }
                  ]
                }
                """,
                exitCode: 4));
        var diagnosis = new FakeRimErrorDiagnosisAdapter(
            new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Timeout,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Timeout,
                    "RIMERROR_TIMEOUT",
                    "timeout"),
                null,
                null));

        CliResult result = RunCatalogCliWithAdapters(
            CreateCatalog(),
            CreateAdapter(recipeTransport),
            diagnosis,
            "run",
            "assembler-smoke");

        AssertEqual(CliExitCodes.TestFailure, result.ExitCode);
        Assert(result.Stdout.Contains(
            "\"status\":\"fail\"",
            StringComparison.Ordinal), "Test failure status changed.");
        Assert(result.Stdout.Contains(
            "\"failureFingerprint\":\"fp-preserved\"",
            StringComparison.Ordinal), "Test failure fingerprint was lost.");
        Assert(result.Stdout.Contains(
            "\"diagnosticStatus\":\"unavailable\"",
            StringComparison.Ordinal), "Degraded diagnostic status is missing.");
        AssertEqual(1, diagnosis.Calls);
        AssertEqual("fp-preserved", diagnosis.Request!.FailureFingerprint);

        var passDiagnosis = new FakeRimErrorDiagnosisAdapter(diagnosis.Result);
        CliResult pass = RunCatalogCliWithAdapters(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": true
                        }
                        """))),
            passDiagnosis,
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Success, pass.ExitCode);
        Assert(pass.Stdout.Contains(
            "\"status\":\"pass\"",
            StringComparison.Ordinal), "RimError must not alter a passing test.");
        AssertEqual(0, passDiagnosis.Calls);
    }

    private static void SuccessfulRecipeRunIsNormalized()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "fixture",
                  "success": true,
                  "generation": 7,
                  "runId": "run-7",
                  "evidence": "Runtime/readiness.json",
                  "evidenceId": "evidence-7",
                  "failureFingerprint": null,
                  "finalNextAction": "status",
                  "restartRequired": false,
                  "launchesConsumed": 0,
                  "operations": null
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        Assert(result.Passed == true, "Run should pass.");
        AssertEqual("run-7", result.RunId);
        AssertEqual(7, result.Generation);
        AssertEqual("evidence-7", result.EvidenceId);
        AssertEqual("Runtime/readiness.json", result.Evidence);
    }

    private static void RecipeAssertionFailureIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "fixture",
                  "success": false,
                  "generation": 7,
                  "failureFingerprint": "fp-7",
                  "finalNextAction": "inspect-evidence",
                  "errorCode": "RECIPE_ASSERTION_FAILED",
                  "error": "assertion failed",
                  "operations": [
                    {
                      "tool": "rimworld/fixture",
                      "success": false,
                      "errorCode": "RECIPE_ASSERTION_FAILED",
                      "assertions": [
                        {"pointer": "/value", "success": false}
                      ]
                    }
                  ]
                }
                """,
                exitCode: 4));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.TestFailure, result.Status.Outcome);
        Assert(result.Passed == false, "Failed recipe should not pass.");
        AssertEqual("fp-7", result.FailureFingerprint);
        AssertEqual("/value", result.Operations.Single().FailedAssertionPointers.Single());
    }

    private static void DevBridgeRefusalIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "missing",
                  "alreadySatisfied": false,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "inspect-evidence",
                  "blockedBy": ["TEST_RECIPE_NOT_FOUND"],
                  "errorCode": "TEST_RECIPE_NOT_FOUND",
                  "error": "not found"
                }
                """,
                exitCode: 4));

        DevBridgeRecipePlanResult result = CreateAdapter(transport)
            .PlanAsync("missing")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.DevBridgeRefusal, result.Status.Outcome);
        AssertEqual("TEST_RECIPE_NOT_FOUND", result.Status.ErrorCode);
        Assert(result.Plan is null, "Refused plan must not be treated as executable.");
    }

    private static void InfrastructureFailureIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                "cannot start",
                StartError: "DevBridge command was not found."));

        DevBridgeRecipeShowResult result = CreateAdapter(transport)
            .ShowAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.InfrastructureFailure, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_START_FAILED", result.Status.ErrorCode);
    }

    private static void TimeoutIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                TimedOut: true));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Timeout, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_CLIENT_TIMEOUT", result.Status.ErrorCode);
    }

    private static void CancellationIsClassified()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeTransport(
            (_, token) =>
            {
                Assert(token.IsCancellationRequested, "Cancellation was not forwarded.");
                return new DevBridgeProcessResult(
                    null,
                    string.Empty,
                    string.Empty,
                    Cancelled: true);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", cancellation.Token)
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Cancelled, result.Status.Outcome);
    }

    private static void MalformedResponseIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult("{"));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.MalformedResponse, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_MALFORMED_JSON", result.Status.ErrorCode);
    }

    private static void IncompatibleSchemaIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {"schemaVersion":"devbridge-test-recipe-run/v2"}
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.IncompatibleSchema, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_SCHEMA_UNSUPPORTED", result.Status.ErrorCode);
    }

    private static void RimContextDirectCoverageSelectsTest()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    "Defs/Assembler.xml",
                    12,
                    "changed_file",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Defs/Assembler.xml"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("ok", result.Status);
        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextTransitiveCoverageSelectsTest()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "dependent",
                    "csharp_type",
                    "type-assembler",
                    "CompAssembler",
                    "Source/CompAssembler.cs",
                    44,
                    "csharp_type_usage",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Base.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("ok", result.Status);
        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextSharedCoverageIsDeduplicated()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null),
                new RimContextImpact(
                    "dependent",
                    "csharp_type",
                    "type-assembler",
                    "CompAssembler",
                    null,
                    null,
                    null,
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Assembler.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(2, result.ReasonCount);
    }

    private static void RimContextNoImpactSelectsNoTests()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact()));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Isolated.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("conservative", result.Status);
        AssertEqual(0, result.Tests.Count);
        AssertEqual("RIMCONTEXT_NO_TESTS", result.ErrorCode);
        AssertEqual("rimliaison affected --run --fallback-suite <suite>", result.NextAction);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextUnknownImpactUsesFallback()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unknown,
                    "RIMCONTEXT_RESULT_TRUNCATED"),
                [],
                [],
                true)));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Unknown.cs"],
                "smoke",
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("conservative", result.Status);
        AssertEqual("smoke", result.FallbackSuite);
        AssertSequence(["assembler-smoke", "settings-smoke"], result.Tests);
    }

    private static void RimContextUnavailableIsConservative()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unavailable,
                    "INDEX_NOT_FOUND"),
                [],
                [],
                false)));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Unknown.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("blocked", result.Status);
        AssertEqual("CONTEXT_STALE", result.ErrorCode);
        AssertEqual("rimliaison affected --run --json", result.NextAction);
        AssertEqual(0, result.Tests.Count);
        Assert(result.ReasonCount > 0, "Conservative selection needs a reason.");
    }

    private static void RimContextSelectionOrderingIsDeterministic()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "dependent",
                    "feature",
                    "feature-settings",
                    "settings",
                    null,
                    null,
                    "feature",
                    null),
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    "changed_file",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Mixed.cs"],
                null,
                true)
            .GetAwaiter()
            .GetResult();

        AssertSequence(["assembler-smoke", "settings-smoke"], result.Tests);
        Assert(result.Reasons is not null, "Explain should return reasons.");
        AssertEqual("direct", result.Reasons![0].Tier);
        AssertEqual("dependent", result.Reasons[1].Tier);
    }

    private static void RimContextAdapterUsesCore()
    {
        string directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "Source.cs"),
                "namespace Fixture; public class Changed { public int Value; }\n");
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    StorePath = Path.Combine(directory, ".rimctx", "index.sqlite"),
                    Depth = 8,
                    Limit = 100
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual("Source.cs", result.Changed.Single());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextCompleteIndexStaysReady()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (request, _) =>
                {
                    calls++;
                    Assert(!request.Force, "A complete index must not trigger a forced rebuild.");
                    return CompleteContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual(PrerequisiteRecoveryState.Ready, result.Status.RecoveryState);
            AssertEqual(0, result.Status.RecoveryAttempts);
            AssertEqual(1, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextPartialIndexIsRecoveredOnce()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (request, _) =>
                {
                    calls++;
                    return request.Force
                        ? CompleteContextAnalysis(directory)
                        : PartialContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            AssertEqual(2, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextFailedRecoveryPreservesDiagnostics()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (_, _) =>
                {
                    calls++;
                    return PartialContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Unknown, result.Status.Outcome);
            AssertEqual("RIMCONTEXT_INDEX_RECOVERY_FAILED", result.Status.ErrorCode);
            AssertEqual(PrerequisiteRecoveryState.RecoveryFailed, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            Assert(result.Status.Error?.Contains("BROKEN_SOURCE", StringComparison.Ordinal) == true,
                "A failed rebuild must retain bounded index diagnostics.");
            AssertEqual(2, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static RimContextAffectedAnalysis CompleteContextAnalysis(string root) =>
        new(
            new IndexBuildResult(
                new StoreMetadata(1, "test", "fixture", root, "fingerprint", "now"),
                new IndexCounts(1, 0, 0),
                1,
                new IndexStatistics(1, 1, 0, 0, 0),
                1,
                []),
            new AffectedResult([], [], [], [], false));

    private static RimContextAffectedAnalysis PartialContextAnalysis(string root) =>
        new(
            new IndexBuildResult(
                new StoreMetadata(1, "test", "fixture", root, "fingerprint", "now"),
                new IndexCounts(1, 0, 0),
                1,
                new IndexStatistics(1, 1, 0, 0, 0),
                1,
                [new IndexDiagnostic("Source.cs", "source parse failed", "BROKEN_SOURCE")]),
            null);

    private static void RimContextCancellationIsBounded()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new RimContextImpactAdapter(
            new RimContextAdapterOptions
            {
                RootPath = "Workspace",
                Depth = 8,
                Limit = 100
            });
        RimContextImpactResult result = adapter.AffectedAsync(
                ["Source/Base.cs"],
                cancellation.Token)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RimContextImpactOutcome.Cancelled, result.Status.Outcome);
    }

    private static void AffectedCliEmitsCompactSelection()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Defs/Assembler.xml",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(
                "{\"schemaVersion\":\"rimtest-selection/v1\",\"status\":\"ok\",\"tests\":[\"assembler-smoke\"],\"reasonCount\":1}",
                stdout.ToString().Trim());
            Assert(!stdout.ToString().Contains(
                "Defs/Assembler.xml",
                StringComparison.Ordinal), "Normal selection output should omit impact details.");
            Assert(string.IsNullOrEmpty(stderr.ToString()), "Selection should not write diagnostics.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RimContextImpactResult SuccessfulImpact(
        params RimContextImpact[] impacts) =>
        new(
            new RimContextAdapterStatus(RimContextImpactOutcome.Success),
            ["changed.cs"],
            impacts,
            false);

    private static void AffectedRunPassIsCompact()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Defs/Assembler.xml",
                        "--run",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual(1, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            JsonElement orchestration = root.GetProperty("orchestration");
            AssertEqual("rimtest-orchestration/v1",
                orchestration.GetProperty("schemaVersion").GetString());
            AssertEqual("PASS", orchestration.GetProperty("overall").GetString());
            AssertEqual("NOT_RUN", orchestration.GetProperty("sourceBuild").GetString());
            AssertEqual("PASS", orchestration.GetProperty("staticTests").GetString());
            AssertEqual("NOT_EVALUATED", orchestration.GetProperty("deployment").GetString());
            AssertEqual("PASS", orchestration.GetProperty("runtimeValidation").GetString());
            AssertEqual("READY", orchestration.GetProperty("infrastructure").GetString());
            Assert(!root.TryGetProperty("selectionStatus", out _),
                "Known-safe affected runs should omit redundant selection status.");
            Assert(!stdout.ToString().Contains("operations", StringComparison.Ordinal),
                "Affected pass should not emit child telemetry.");
            Assert(RimTestOutputBudgets.Utf8Bytes(stdout.ToString()) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
                "Affected pass exceeded its normal output budget.");
            Assert(string.IsNullOrEmpty(stderr.ToString()),
                "Affected pass should not write a human transcript.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedZeroImpactUsesFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Isolated.cs"),
                "class Isolated {}\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Isolated.cs",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("RIMCONTEXT_NO_TESTS", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void AffectedChangedPathWithoutFallbackBlocks()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Isolated.cs"),
                "class Isolated {}\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());

            CliResult result = WithFallbackSuiteEnvironment(
                null,
                () => WithCurrentDirectory(
                    directory,
                    () =>
                    {
                        var stdout = new StringWriter();
                        var stderr = new StringWriter();
                        int exitCode = CliApplication.RunAsync(
                                [
                                    "affected",
                                    "Source/Isolated.cs",
                                    "--run",
                                    "--json",
                                    "--catalog",
                                    catalogPath
                                ],
                                stdout,
                                stderr,
                                adapter,
                                impactAdapter: impactAdapter)
                            .GetAwaiter()
                            .GetResult();
                        return new CliResult(
                            exitCode,
                            stdout.ToString(),
                            stderr.ToString());
                    }));

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
            AssertEqual("conservative", root.GetProperty("status").GetString());
            AssertEqual("RIMCONTEXT_NO_TESTS", root.GetProperty("errorCode").GetString());
            AssertEqual(
                "rimliaison affected --run --fallback-suite <suite>",
                root.GetProperty("nextAction").GetString());
            Assert(!root.TryGetProperty("suite", out _),
                "An affected run without fallback must not execute an empty suite.");
            AssertEqual(0, adapter.RunCalls.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuiteAllPassAggregationIsCompact()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var executor = new CatalogTestExecutionService(adapter);
        var runner = new CatalogSuiteRunner(adapter, executor);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["settings-smoke", "assembler-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 18432);
        string output = CatalogJsonFacade.Serialize(result);

        AssertEqual("pass", result.Status);
        AssertEqual(2, result.Passed);
        AssertEqual(0, result.Failed);
        Assert(!output.Contains("operations", StringComparison.Ordinal),
            "Suite output must not contain child transcripts.");
        Assert(!output.Contains("assembler-fixture", StringComparison.Ordinal),
            "Suite output must not contain child recipe payloads.");
        Assert(!output.Contains("failures", StringComparison.Ordinal),
            "Successful suite must omit failures.");
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.PlanCalls);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
    }

    private static void EmptySuiteExecutionIsConservative()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult("empty", [], 0, false),
            100,
            selectionStatus: "ok");

        AssertEqual("conservative", result.Status);
        AssertEqual(0, result.Passed);
        AssertEqual(0, result.Failed);
        AssertEqual("conservative", result.SelectionStatus);
        AssertEqual("RIMTEST_EMPTY_EXECUTION", result.SelectionErrorCode);
        AssertEqual("rimliaison suites", result.NextAction);
        Assert(!string.Equals(result.Status, "pass", StringComparison.Ordinal),
            "An empty suite execution must never be a normal pass.");
    }

    private static void SuiteOneFailureIsSummarized()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(
            AvailableDiagnosis("RE-81F72"));
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter, () => diagnosis));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("fail", result.Status);
        AssertEqual(1, result.Passed);
        AssertEqual(1, result.Failed);
        AssertEqual(1, result.Failures!.Count);
        AssertEqual("assembler-smoke", result.Failures[0].Test);
        AssertEqual("RE-81F72", result.Failures[0].DiagnosticId);
        AssertEqual("fp-assembler", result.Failures[0].FailureFingerprint);
    }

    private static void SuiteMultipleFailuresAreDeterministic()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = FailedRun(
            "settings-fixture",
            "fp-settings",
            "RECIPE_ASSERTION_FAILED");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["settings-smoke", "assembler-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("fail", result.Status);
        AssertEqual(0, result.Passed);
        AssertEqual(2, result.Failed);
        AssertSequence(
            ["assembler-smoke", "settings-smoke"],
            result.Failures!.Select(static failure => failure.Test).ToArray());
        AssertSequence(
            ["assembler-fixture", "settings-fixture"],
            adapter.RunCalls);
        Assert(execution.FailFast is null,
            "The default suite mode must not add fail-fast execution metadata.");
    }

    private static void SuiteFailFastStopsAfterFirstFailure()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "smoke",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);
        string json = CatalogJsonFacade.Serialize(result);

        AssertEqual("fail", result.Status);
        AssertEqual(1, result.Failed);
        AssertEqual(2, result.Skipped);
        AssertSequence(["assembler-fixture"], adapter.RunCalls);
        AssertEqual("assembler-smoke", result.FailFast!.FirstFailure);
        AssertEqual(2, result.FailFast.NotLaunched);
        Assert(!result.FailFast.ValidationCompleted,
            "A stopped failure path must report incomplete validation.");
        Assert(!json.Contains("third-fixture", StringComparison.Ordinal),
            "Fail-fast output must not include unlaunched recipe payloads.");
        Assert(!json.Contains("operations", StringComparison.Ordinal),
            "Fail-fast output must not include child transcripts.");
        Assert(RimTestOutputBudgets.Utf8Bytes(json) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Fail-fast output exceeded the bounded suite budget.");
    }

    private static void SuiteFailFastPassExecutesEverySelectedTest()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "smoke",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);
        string json = CatalogJsonFacade.Serialize(result);

        AssertEqual("pass", result.Status);
        AssertEqual(3, result.Passed);
        AssertSequence(
            ["assembler-fixture", "settings-fixture", "third-fixture"],
            adapter.RunCalls);
        Assert(result.FailFast!.FirstFailure is null,
            "A passing fail-fast run must not invent a failure reference.");
        AssertEqual(0, result.FailFast.NotLaunched);
        Assert(result.FailFast.ValidationCompleted,
            "A fail-fast PASS must prove complete selected-test execution.");
        RimTestSuiteResult partial = RimTestSuiteResultFactory.FromExecution(
            execution with
            {
                FailFast = new CatalogSuiteFailFastSummary(null, 1, false)
            },
            100);
        AssertEqual("conservative", partial.Status);
        Assert(RimTestOutputBudgets.Utf8Bytes(json) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Passing fail-fast output exceeded the bounded suite budget.");
    }

    private static void SuiteCancellationStopsNewChildren()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = CancelledRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "affected",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("cancelled", result.Status);
        AssertEqual(1, result.Cancelled!.Value);
        AssertEqual(2, result.Skipped!.Value);
        AssertSequence(["assembler-fixture"], adapter.RunCalls);
        Assert(result.FailFast is not null &&
                result.FailFast.FirstFailure is null &&
                !result.FailFast.ValidationCompleted,
            "Cancellation must remain conservative in fail-fast mode.");
    }

    private static void SuiteDuplicateTestsExecuteOnce()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "affected",
                ["settings-smoke", "assembler-smoke", "assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, execution.Tests.Count);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
    }

    private static void SuiteChildInfrastructureFailureIsSummarized()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = InfrastructureRun(
            "assembler-fixture",
            "DEVBRIDGE_CLIENT_TIMEOUT");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("infrastructure", result.Status);
        AssertEqual(1, result.Passed);
        AssertEqual(1, result.Failed);
        AssertEqual("infrastructure", result.Failures![0].Status);
        AssertEqual("DEVBRIDGE_CLIENT_TIMEOUT", result.Failures[0].ErrorCode);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
        Assert(result.FailFast is not null &&
                result.FailFast.FirstFailure is null &&
                result.FailFast.ValidationCompleted,
            "Infrastructure failures must not be treated as ordinary fail-fast failures.");
    }

    private static void SuitePlanRefusalBlocksExecution()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Plans["assembler-fixture"] = new DevBridgeRecipePlanResult(
            "assembler-fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.DevBridgeRefusal,
                "TEST_RECIPE_NOT_FOUND"),
            null);
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("infrastructure", result.Status);
        AssertEqual(1, result.Failed);
        AssertEqual(1, result.Skipped);
        AssertEqual(0, adapter.RunCalls.Count);
        AssertEqual("TEST_RECIPE_NOT_FOUND", result.Failures![0].ErrorCode);
    }

    private static void UnannotatedRecipesUseSafePath()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest { Id = "unknown-a", Recipe = "recipe-a" },
            new CatalogTest { Id = "unknown-b", Recipe = "recipe-b" });
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, 7) with { WorkflowId = workflow }
        };
        var lease = new FakeLeaseAdapter();
        var fresh = new FakeFreshGenerationAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            resetAdapter: null,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "unknown",
                ["unknown-a", "unknown-b"],
                workflowId: "workflow-unknown")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        AssertEqual(0, fresh.Calls.Count);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Unannotated recipes must not receive a reusable lease.");
        Assert(execution.Reuse is null,
            "An unannotated suite should not claim a reuse transaction.");
    }

    private static void UnsafeRecipesNeverShareState()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest
            {
                Id = "fresh-a",
                Recipe = "recipe-a",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FreshGenerationRequired
                }
            },
            new CatalogTest
            {
                Id = "fresh-b",
                Recipe = "recipe-b",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FreshGameRequired
                }
            });
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, index + 10) with { WorkflowId = workflow }
        };
        var lease = new FakeLeaseAdapter();
        var fresh = new FakeFreshGenerationAdapter(11, 12);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            resetAdapter: null,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "unsafe",
                ["fresh-a", "fresh-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, fresh.Calls.Count);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Fresh-state recipes must never receive a shared lease.");
        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void MutationRecipesNeverShareState()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("mutation-a", "recipe-a"),
            ReusableTest("mutation-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, 7) with { WorkflowId = workflow }
        };
        using (JsonDocument document = JsonDocument.Parse(
                   "{\"projects\":[],\"inputs\":{},\"allowInGameMutation\":true}"))
        {
            adapter.ShowDefinitions["recipe-a"] = document.RootElement.Clone();
        }

        var lease = new FakeLeaseAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "mutation",
                ["mutation-a", "mutation-b"],
                workflowId: "workflow-mutation")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "A recipe that explicitly allows in-game mutation must not receive a shared lease.");
        AssertEqual("RIMTEST_RECIPE_MUTATION_NOT_SHAREABLE", execution.Reuse!.FallbackReason);
    }

    private static void IncompatibleReuseProfilesFallBackSafely()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("profile-project", "recipe-project"),
            ReusableTest("profile-baseline", "recipe-baseline"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-profile-" + index, "operation-profile-" + index)
        };
        SetRecipeProfile(adapter, "recipe-project", ["frontier"]);
        SetRecipeProfile(adapter, "recipe-baseline", []);
        adapter.Plans["recipe-project"] = SatisfiedPlan("recipe-project");
        adapter.Plans["recipe-baseline"] = SatisfiedPlan("recipe-baseline");
        var lease = new FakeLeaseAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "profile-incompatible",
                ["profile-baseline", "profile-project"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Recipes with incompatible profiles must not share a supplied lease.");
        Assert(execution.Reuse is not null,
            "A rejected reuse group must remain visible in the bounded summary.");
        AssertEqual(0, execution.Reuse!.GroupsPlanned);
        AssertEqual("RIMTEST_REUSE_PROFILE_INCOMPATIBLE", execution.Reuse.FallbackReason);
    }

    private static void ReusePlannerGroupsCompatibleTestsDeterministically()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared-1", "recipe-a", "shared"),
            ReusableTestWithKey("b-other-1", "recipe-b", "other"),
            ReusableTestWithKey("c-shared-2", "recipe-c", "shared"),
            ReusableTestWithKey("d-other-2", "recipe-d", "other"));
        var profiles = new Dictionary<string, CatalogSuiteRecipeProfile?>(
            StringComparer.Ordinal)
        {
            ["recipe-a"] = RecipeProfile("profile-shared"),
            ["recipe-b"] = RecipeProfile("profile-other"),
            ["recipe-c"] = RecipeProfile("profile-shared"),
            ["recipe-d"] = RecipeProfile("profile-other")
        };

        CatalogSuiteReusePlan first = CatalogSuiteReusePlanner.Plan(
            catalog,
            ["d-other-2", "c-shared-2", "b-other-1", "a-shared-1"],
            profiles);
        CatalogSuiteReusePlan second = CatalogSuiteReusePlanner.Plan(
            catalog,
            ["a-shared-1", "b-other-1", "c-shared-2", "d-other-2"],
            profiles);

        string[] expectedOrder =
            ["a-shared-1", "c-shared-2", "b-other-1", "d-other-2"];
        AssertSequence(expectedOrder, first.ExecutionOrder.ToArray());
        AssertSequence(expectedOrder, second.ExecutionOrder.ToArray());
        AssertEqual(2, first.Groups.Count);
        AssertSequence(["a-shared-1", "c-shared-2"], first.Groups[0].TestIds.ToArray());
        AssertSequence(["b-other-1", "d-other-2"], first.Groups[1].TestIds.ToArray());
        AssertEqual(first.Groups[0].ReuseKey, second.Groups[0].ReuseKey);
        AssertEqual(first.Groups[0].Mode, second.Groups[0].Mode);
        AssertEqual(first.Groups[0].ProfileSignature, second.Groups[0].ProfileSignature);
        AssertEqual(first.Groups[1].ReuseKey, second.Groups[1].ReuseKey);
        AssertEqual(first.Groups[1].Mode, second.Groups[1].Mode);
        AssertEqual(first.Groups[1].ProfileSignature, second.Groups[1].ProfileSignature);
        AssertSequence(first.Groups[0].TestIds.ToArray(), second.Groups[0].TestIds.ToArray());
        AssertSequence(first.Groups[1].TestIds.ToArray(), second.Groups[1].TestIds.ToArray());
        AssertEqual(null, first.FallbackReason);
    }

    private static void ReusePlannerPreservesHardBoundaries()
    {
        CatalogTest fresh = new()
        {
            Id = "b-fresh",
            Recipe = "recipe-fresh",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FreshGenerationRequired
            }
        };
        CatalogDocument freshCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared", "recipe-a", "shared"),
            fresh,
            ReusableTestWithKey("c-shared", "recipe-c", "shared"));
        var compatibleProfiles = new Dictionary<string, CatalogSuiteRecipeProfile?>(
            StringComparer.Ordinal)
        {
            ["recipe-a"] = RecipeProfile("same"),
            ["recipe-c"] = RecipeProfile("same")
        };
        CatalogSuiteReusePlan freshPlan = CatalogSuiteReusePlanner.Plan(
            freshCatalog,
            ["a-shared", "b-fresh", "c-shared"],
            compatibleProfiles);
        AssertSequence(
            ["a-shared", "b-fresh", "c-shared"],
            freshPlan.ExecutionOrder.ToArray());
        AssertEqual(0, freshPlan.Groups.Count);

        CatalogDocument unavailableCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-unavailable", "recipe-a", "shared"),
            ReusableTestWithKey("b-unavailable", "recipe-b", "shared"));
        CatalogSuiteReusePlan unavailablePlan = CatalogSuiteReusePlanner.Plan(
            unavailableCatalog,
            ["a-unavailable", "b-unavailable"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same")
            });
        AssertEqual(0, unavailablePlan.Groups.Count);
        AssertEqual("RIMTEST_REUSE_PROFILE_UNAVAILABLE", unavailablePlan.FallbackReason);

        CatalogSuiteReusePlan incompatiblePlan = CatalogSuiteReusePlanner.Plan(
            unavailableCatalog,
            ["a-unavailable", "b-unavailable"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("projects-a"),
                ["recipe-b"] = RecipeProfile("projects-b")
            });
        AssertEqual(0, incompatiblePlan.Groups.Count);
        AssertEqual("RIMTEST_REUSE_PROFILE_INCOMPATIBLE", incompatiblePlan.FallbackReason);

        CatalogTest wrongMode = ReusableTestWithKey(
            "b-mode", "recipe-b", "shared", CatalogRecipeIsolationMode.SameGenerationSafe);
        CatalogDocument modeCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-mode", "recipe-a", "shared"),
            wrongMode);
        CatalogSuiteReusePlan modePlan = CatalogSuiteReusePlanner.Plan(
            modeCatalog,
            ["a-mode", "b-mode"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same"),
                ["recipe-b"] = RecipeProfile("same")
            });
        AssertEqual(0, modePlan.Groups.Count);

        CatalogTest invalidResetA = new()
        {
            Id = "a-invalid-reset",
            Recipe = "recipe-a",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "shared"
            }
        };
        CatalogTest invalidResetB = new()
        {
            Id = "b-invalid-reset",
            Recipe = "recipe-b",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "shared",
                ResetRecipe = ""
            }
        };
        CatalogSuiteReusePlan invalidResetPlan = CatalogSuiteReusePlanner.Plan(
            CreateIsolationCatalog(invalidResetA, invalidResetB),
            ["a-invalid-reset", "b-invalid-reset"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same"),
                ["recipe-b"] = RecipeProfile("same")
            });
        AssertEqual(0, invalidResetPlan.Groups.Count);
    }

    private static void GroupedSuiteExecutionAvoidsLifecycleTransitions()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared-1", "recipe-a", "shared"),
            ReusableTestWithKey("b-other-1", "recipe-b", "other"),
            ReusableTestWithKey("c-shared-2", "recipe-c", "shared"),
            ReusableTestWithKey("d-other-2", "recipe-d", "other"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(
                    recipe,
                    context?.LeaseId == "lease-other" ? 8 : 7,
                    context?.LeaseId,
                    workflow,
                    "run-grouped-" + index,
                    "operation-grouped-" + index)
        };
        foreach (string recipe in new[] { "recipe-a", "recipe-b", "recipe-c", "recipe-d" })
        {
            adapter.Plans[recipe] = SatisfiedPlan(recipe);
        }
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-shared", 7));
        lease.BeginResults.Enqueue(SuccessLease("lease-other", 8));
        var fresh = new FakeFreshGenerationAdapter(8);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "grouped",
                ["d-other-2", "c-shared-2", "b-other-1", "a-shared-1"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertSequence(
            ["recipe-a", "recipe-c", "recipe-b", "recipe-d"],
            adapter.RunCalls);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, execution.Reuse!.GroupsPlanned);
        AssertEqual(2, execution.Reuse.GroupsUsed);
        AssertEqual(2, execution.Reuse.GenerationsAvoided);
        AssertEqual(2, execution.Reuse.RelaunchesAvoided);
        AssertEqual(1, execution.Reuse.Relaunches);
    }

    private static void ReuseCancellationCannotContaminateLaterTests()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("cancel-a", "recipe-a"),
            ReusableTest("cancel-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["recipe-a"] = CancelledRun("recipe-a");
        adapter.Runs["recipe-b"] = PassRunWithLease(
            "recipe-b", 8, "lease-recovered", null,
            "cancel-recovered", "cancel-recovered-operation");
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-cancel", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "cancel-reuse",
                ["cancel-a", "cancel-b"])
            .GetAwaiter()
            .GetResult();

        AssertSequence(["recipe-a"], adapter.RunCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
    }

    private static void DevBridgeReuseRefusalPreservesCause()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("refusal-a", "recipe-a"),
            ReusableTest("refusal-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-refusal-pass", "operation-refusal-pass")
                : new DevBridgeRecipeRunResult(
                    recipe,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.DevBridgeRefusal,
                        "RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
                        "A supplied lease cannot authorize an autonomous restart."),
                    false,
                    "run-refusal-fail",
                    7,
                    context?.LeaseId,
                    null,
                    null,
                    null,
                    "ensure-ready",
                    true,
                    0,
                    [],
                    workflow)
        };
        SetRecipeProfile(adapter, "recipe-a", []);
        SetRecipeProfile(adapter, "recipe-b", []);
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-refusal", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "refusal",
                ["refusal-a", "refusal-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Reuse!.ReuseInvalidationReason);
        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Tests[1].ErrorCode);
        Assert(execution.Reuse.Mismatch is not null,
            "A supplied-lease refusal must expose its bounded mismatch details.");
        AssertEqual(true, execution.Reuse.Mismatch!.RestartRequired);
        AssertEqual(0, execution.Reuse.Mismatch.LaunchesConsumed);
        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Reuse.Mismatch.ErrorCode);
        AssertEqual("infrastructure",
            RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void CompatibleRecipesReuseOneGeneration()
    {
        AssertCompatibleRecipesReuse(failFast: false);
    }

    private static void FailFastPreservesCompatibleReuse()
    {
        AssertCompatibleRecipesReuse(failFast: true);
    }

    private static void AssertCompatibleRecipesReuse(bool failFast)
    {
        const string workflowId = "workflow-reuse";
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("read-a", "recipe-a"),
            ReusableTest("read-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(
                    recipe,
                    7,
                    context?.LeaseId,
                    workflow,
                    "run-reuse-" + index,
                    "operation-reuse-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-reuse", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reuse",
                ["read-b", "read-a"],
                workflowId: workflowId,
                failFast: failFast)
            .GetAwaiter()
            .GetResult();
        CatalogSuiteReuseSummary reuse = execution.Reuse!;

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(1, lease.BeginCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual(1, reuse.GroupsUsed);
        AssertEqual(1, reuse.GenerationsUsed);
        AssertEqual(0, reuse.Relaunches);
        AssertEqual(1, reuse.GenerationsAvoided);
        AssertEqual(1, reuse.RelaunchesAvoided);
        AssertEqual("used", reuse.Status);
        AssertEqual(2, adapter.ExecutionContexts.Count);
        Assert(adapter.ExecutionContexts.All(
                context => context?.LeaseId == "lease-reuse"),
            "Compatible recipes must execute under the same lease.");
        AssertSequence(
            ["run-reuse-0", "run-reuse-1"],
            adapter.RunResults.Select(static result => result.RunId!).ToArray());
        AssertSequence(
            ["operation-reuse-0", "operation-reuse-1"],
            adapter.RunResults.SelectMany(static result => result.Operations)
                .Select(static operation => operation.OperationId!)
                .ToArray());
        Assert(adapter.RunResults.All(result => result.WorkflowId == workflowId),
            "Workflow identity must propagate to every shared-generation recipe.");
        if (failFast)
        {
            Assert(execution.FailFast is not null &&
                    execution.FailFast.NotLaunched == 0 &&
                    execution.FailFast.ValidationCompleted,
                "Fail-fast PASS must preserve complete reuse execution.");
        }
    }

    private static void ResettableRecipesRequireSuccessfulReset()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ResettableTest("reset-a", "recipe-a"),
            ResettableTest("reset-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 8, context?.LeaseId, workflow,
                    "run-reset-" + index, "operation-reset-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var reset = new FakeResetAdapter
        {
            Result = SuccessfulReset("lease-reset", 8)
        };
        var lease = new FakeLeaseAdapter { BeginResult = SuccessLease("lease-reset", 8) };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            reset);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reset",
                ["reset-a", "reset-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, reset.Calls.Count);
        AssertEqual("fixture-reset", reset.Calls[0].RecipeId);
        AssertEqual(1, execution.Reuse!.FixtureResets);
        AssertEqual(1, execution.Reuse.GenerationsAvoided);
        AssertEqual(1, execution.Reuse.RelaunchesAvoided);
        AssertEqual("used", execution.Reuse.Status);
        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void FailedResetInvalidatesReuse()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ResettableTest("reset-fail-a", "recipe-a"),
            ResettableTest("reset-fail-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
            {
                int generation = context?.LeaseId == "lease-reset-2" ? 9 : 8;
                return PassRunWithLease(recipe, generation, context?.LeaseId, workflow,
                    "run-reset-fail-" + index, "operation-reset-fail-" + index);
            }
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var reset = new FakeResetAdapter
        {
            Result = new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RESET_NOT_VERIFIED"),
                8,
                "lease-reset-1")
        };
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-reset-1", 8));
        lease.BeginResults.Enqueue(SuccessLease("lease-reset-2", 9));
        lease.RenewResults.Enqueue(SuccessLease("lease-reset-1", 8));
        var fresh = new FakeFreshGenerationAdapter(9);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            reset,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reset-fail",
                ["reset-fail-a", "reset-fail-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, reset.Calls.Count);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual("reset-fail-b", execution.Reuse.ReuseInvalidatedAfter);
        AssertEqual("RESET_NOT_VERIFIED", execution.Reuse.ReuseInvalidationReason);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
        AssertEqual("infrastructure", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual("lease-reset-2", adapter.ExecutionContexts[1]!.LeaseId);
    }

    private static void TestFailureCannotContaminateLaterRecipes()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("fail-a", "recipe-a"),
            ReusableTest("fail-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? FailedRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "failure-run", "failure-operation")
                : PassRunWithLease(recipe, 8, context?.LeaseId, workflow,
                    "recovered-run", "recovered-operation")
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-failure-1", 7));
        lease.BeginResults.Enqueue(SuccessLease("lease-failure-2", 8));
        var fresh = new FakeFreshGenerationAdapter(8);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "failure-recovery",
                ["fail-a", "fail-b"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 10);

        AssertEqual("fail", result.Status);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual(0, execution.Reuse?.GenerationsAvoided ?? 0);
        AssertEqual("lease-failure-2", adapter.ExecutionContexts[1]!.LeaseId);
        AssertEqual("failure-run", adapter.RunResults[0].RunId);
    }

    private static void GenerationAndLeaseChangesInvalidateReuse()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("mismatch-a", "recipe-a"),
            ReusableTest("mismatch-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-mismatch-" + index, "operation-mismatch-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-mismatch", 7)
        };
        lease.RenewResults.Enqueue(SuccessLease("lease-mismatch", 9));
        var fresh = new FakeFreshGenerationAdapter(10);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "mismatch",
                ["mismatch-a", "mismatch-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual("RIMTEST_REUSE_LEASE_INVALID", execution.Reuse.ReuseInvalidationReason);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
        AssertEqual("infrastructure", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void ReuseResultIsBoundedAndIdentitiesStayDistinct()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("bounded-a", "recipe-a"),
            ReusableTest("bounded-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "bounded-run-" + index, "bounded-operation-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            new FakeLeaseAdapter { BeginResult = SuccessLease("lease-bounded", 7) });

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "bounded",
                ["bounded-a", "bounded-b"],
                workflowId: "workflow-bounded")
            .GetAwaiter()
            .GetResult();
        string json = CatalogJsonFacade.Serialize(
            RimTestSuiteResultFactory.FromExecution(execution, 10));

        Assert(RimTestOutputBudgets.Utf8Bytes(json) <= RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Reuse summary exceeded the bounded suite output budget.");
        Assert(json.Contains("\"reuse\"", StringComparison.Ordinal),
            "The bounded suite result must expose reuse planning information.");
        Assert(!json.Contains("bounded-operation", StringComparison.Ordinal),
            "Suite output must not expose child operation transcripts.");
        AssertEqual(2, adapter.RunResults.Select(static result => result.RunId)
            .Distinct(StringComparer.Ordinal).Count());
        AssertEqual(2, adapter.RunResults.SelectMany(static result => result.Operations)
            .Select(static operation => operation.OperationId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    private static void FreshGenerationAdapterProvesReadinessConservatively()
    {
        var recipe = new FakeRecipeAdapter();
        using JsonDocument definition = JsonDocument.Parse(
            "{\"id\":\"recipe-a\",\"projects\":[\"fixture\"],\"inputs\":{\"quicktest\":true}}");
        recipe.ShowDefinitions["recipe-a"] = definition.RootElement.Clone();
        int calls = 0;
        var transport = new FakeTransport((request, _) =>
        {
            calls++;
            Assert(request.Arguments.Contains("restart"),
                "Fresh-generation preparation must use DevBridge restart.");
            Assert(request.Arguments.Contains("--projects"),
                "Recipe project intent must be supplied to DevBridge.");
            Assert(request.Arguments.Contains("quicktest=true"),
                "Recipe test inputs must be supplied to DevBridge.");
            Assert(request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.ContainsKey("DEVBRIDGE_AGENT"),
                "Lifecycle requests must carry a stable owner identity.");
            return ProcessResult(calls == 1
                ? "{\"success\":true,\"exitCode\":0,\"state\":\"READY\",\"generation\":8,\"restartPending\":false}"
                : "{\"success\":true,\"exitCode\":0,\"state\":\"LOADING\",\"generation\":8,\"restartPending\":true}");
        });
        var adapter = new DevBridgeFreshGenerationAdapter(
            recipe,
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                RunTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeFreshGenerationResult ready = adapter.EnsureFreshGenerationAsync(
                "recipe-a",
                7,
                "workflow-fresh")
            .GetAwaiter()
            .GetResult();
        DevBridgeFreshGenerationResult unready = adapter.EnsureFreshGenerationAsync(
                "recipe-a",
                8,
                "workflow-fresh")
            .GetAwaiter()
            .GetResult();

        Assert(ready.IsUsable, "A typed newer READY generation should be usable.");
        AssertEqual(8, ready.Generation);
        AssertEqual(1, ready.LaunchesConsumed);
        AssertEqual("DEVBRIDGE_FRESH_GENERATION_NOT_READY", unready.Status.ErrorCode);
    }

    private static void LeaseAdapterPreservesOwnerAndGenerationIdentity()
    {
        var transport = new FakeTransport((request, _) =>
        {
            string operation = request.Arguments.Count > 1
                ? request.Arguments[1]
                : string.Empty;
            string leaseId = operation == "begin"
                ? "lease-adapter"
                : request.Arguments.FirstOrDefault(value =>
                    value.StartsWith("lease-", StringComparison.Ordinal)) ?? "lease-adapter";
            return ProcessResult(
                $"progress\n{{\"success\":true,\"exitCode\":0,\"generation\":12,\"leaseId\":\"{leaseId}\"}}");
        });
        var adapter = new DevBridgeLeaseAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeLeaseResult begin = adapter.BeginLeaseAsync("workflow-lease")
            .GetAwaiter()
            .GetResult();
        DevBridgeLeaseResult renew = adapter.RenewLeaseAsync(
                "lease-adapter",
                "workflow-lease")
            .GetAwaiter()
            .GetResult();
        DevBridgeLeaseResult end = adapter.EndLeaseAsync(
                "lease-adapter",
                "workflow-lease")
            .GetAwaiter()
            .GetResult();

        Assert(begin.IsUsable && renew.IsUsable && end.Status.IsSuccess,
            "Lifecycle JSON responses should remain usable across lease operations.");
        AssertEqual("lease-adapter", begin.LeaseId);
        AssertEqual(12, begin.Generation);
        AssertEqual(3, transport.Requests.Count);
        string? owner = transport.Requests[0].EnvironmentVariables!["DEVBRIDGE_AGENT"];
        Assert(transport.Requests.All(request =>
                request.EnvironmentVariables!["DEVBRIDGE_AGENT"] == owner),
            "All workflow lease operations must use one stable DevBridge owner identity.");
    }

    private static void ExistingCompatibleLeaseRetriesBlockedRecipe()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("lease-a", "recipe-a"),
            ReusableTest("lease-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? LeaseRequiredRun(recipe, workflow, context?.LeaseId)
                : PassRunWithLease(
                    recipe,
                    7,
                    context?.LeaseId,
                    workflow,
                    "run-compatible-" + index,
                    "operation-compatible-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-existing", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "lease-recovery",
                ["lease-a", "lease-b"],
                workflowId: "wf-compatible-lease")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(1, lease.BeginCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual(3, adapter.RunCalls.Count);
        Assert(execution.PrerequisiteRecovery?.Single().State == "recovered",
            "A blocked operation resumed under the already-valid compatible lease.");
        Assert(adapter.ExecutionContexts.All(
                context => context?.LeaseId == "lease-existing"),
            "The retry must reuse the supported transaction lease rather than acquire another one.");
    }

    private static void EnvironmentFallbackDrivesAffectedFallback()
    {
        IReadOnlyList<string> runCalls = [];
        CliResult result = WithFallbackSuiteEnvironment(
            "smoke",
            () =>
            {
                string directory = CreateTempDirectory();
                try
                {
                    string catalogPath = Path.Combine(directory, "catalog.json");
                    File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
                    var adapter = new FakeRecipeAdapter();
                    adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
                    adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
                    var developmentAdapter = new FakeModDevelopmentAdapter();
                    var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                        new RimContextAdapterStatus(
                            RimContextImpactOutcome.Unknown,
                            "RIMCONTEXT_RESULT_TRUNCATED"),
                        [],
                        [],
                        true));
                    var stdout = new StringWriter();
                    var stderr = new StringWriter();
                    int exitCode = CliApplication.RunAsync(
                            [
                                "affected",
                                "Source/Unknown.cs",
                                "--run",
                                "--json",
                                "--catalog",
                                catalogPath
                            ],
                            stdout,
                            stderr,
                            adapter,
                            cancellationToken: default,
                            impactAdapter: impactAdapter,
                            developmentAdapter: developmentAdapter)
                        .GetAwaiter()
                        .GetResult();

                    runCalls = adapter.RunCalls.ToArray();
                    return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            });

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
        AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
        AssertSequence(
            ["assembler-fixture", "settings-fixture"],
            runCalls);
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add affected diagnostics.");
    }

    private static void AffectedRunUsesConservativeFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unknown,
                    "RIMCONTEXT_RESULT_TRUNCATED"),
                [],
                [],
                true));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Unknown.cs",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    cancellationToken: default,
                    impactAdapter: impactAdapter,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedDeletedPathUsesConservativeFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var git = new FakeGitChangeProvider(
                new GitChangeDiscoveryResult(true, ["Source/Deleted.cs"])
                {
                    Changes =
                    [
                        new GitChangedPath("Source/Deleted.cs", "D ")
                    ]
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("RIMCONTEXT_CHANGE_UNPROVEN", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void AffectedRenameWithoutFallbackBlocks()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var git = new FakeGitChangeProvider(
                new GitChangeDiscoveryResult(
                    true,
                    ["Source/Old.cs", "Source/New.cs"])
                {
                    Changes =
                    [
                        new GitChangedPath(
                            "Source/New.cs",
                            "R100",
                            "Source/Old.cs")
                    ]
                });

            CliResult result = WithFallbackSuiteEnvironment(
                null,
                () => WithCurrentDirectory(
                    directory,
                    () =>
                    {
                        var stdout = new StringWriter();
                        var stderr = new StringWriter();
                        int exitCode = CliApplication.RunAsync(
                                [
                                    "affected",
                                    "--run",
                                    "--json",
                                    "--catalog",
                                    catalogPath
                                ],
                                stdout,
                                stderr,
                                adapter,
                                impactAdapter: impactAdapter,
                                gitChangeProvider: git)
                            .GetAwaiter()
                            .GetResult();
                        return new CliResult(
                            exitCode,
                            stdout.ToString(),
                            stderr.ToString());
                    }));

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
            AssertEqual("conservative", root.GetProperty("status").GetString());
            AssertEqual("RIMCONTEXT_CHANGE_UNPROVEN", root.GetProperty("errorCode").GetString());
            AssertEqual(
                "rimliaison affected --run --fallback-suite <suite>",
                root.GetProperty("nextAction").GetString());
            Assert(!root.TryGetProperty("suite", out _),
                "A rename without fallback must never become an empty-suite pass.");
            AssertEqual(0, adapter.RunCalls.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuiteRunCliIsDeterministic()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "suite", "run", "smoke", "--fail-fast", "--json", "--catalog", catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("smoke", root.GetProperty("suite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            Assert(root.GetProperty("failFast").GetProperty("validationCompleted").GetBoolean(),
                "The suite CLI must expose complete fail-fast validation on PASS.");
            AssertEqual(0, root.GetProperty("failFast").GetProperty("notLaunched").GetInt32());
            Assert(!stdout.ToString().Contains("operations", StringComparison.Ordinal),
                "Suite CLI must not emit child operations.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CapabilitiesDiscoverRegisteredSurface()
    {
        (CliResult result, FakeTransport transport) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    { "id": "rimworld/get_screenshot", "aliases": ["screenshot"], "title": "Screenshot", "summary": "Capture a screenshot", "category": "screenshots", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_screen_targets", "title": "Screen targets", "summary": "Inspect visible UI targets", "category": "ui", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/set_camera", "title": "Camera view", "summary": "Control the camera view", "category": "camera", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_game_state", "title": "Runtime state", "summary": "Inspect live game state", "category": "inspection", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/click", "title": "Live interaction", "summary": "Interact with a live-game target", "category": "interaction", "providerId": "rimworld", "source": "Core", "mutating": true },
                    { "id": "rimtest/invoke_companion", "title": "Companion test", "summary": "Invoke a registered companion test", "category": "companion", "providerId": "rimtest", "source": "Optional" },
                    { "id": "rimworld/run_lua", "title": "Lua script", "summary": "Run a Lua inspection script", "category": "scripts", "providerId": "rimworld", "source": "Optional" }
                  ]
                }
              }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-capabilities/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("RimBridgeServer", root.GetProperty("source").GetString());
        AssertEqual(7, root.GetProperty("count").GetInt32());
        Assert(!root.GetProperty("truncated").GetBoolean(),
            "A small capability registry should not be truncated.");
        string[] ids = root.GetProperty("capabilities")
            .EnumerateArray()
            .Select(capability => capability.GetProperty("id").GetString()!)
            .ToArray();
        Assert(ids.Contains("rimworld/get_screenshot"), "Screenshot capability was not discoverable.");
        Assert(ids.Contains("rimworld/get_screen_targets"), "UI target capability was not discoverable.");
        Assert(ids.Contains("rimworld/set_camera"), "Camera capability was not discoverable.");
        Assert(ids.Contains("rimworld/get_game_state"), "Runtime state capability was not discoverable.");
        Assert(ids.Contains("rimworld/click"), "Live interaction capability was not discoverable.");
        Assert(ids.Contains("rimtest/invoke_companion"), "Companion capability was not discoverable.");
        Assert(ids.Contains("rimworld/run_lua"), "Lua capability was not discoverable.");
        AssertEqual(1, transport.Requests.Count);
    }

    private static void CapabilitiesQueryFiltersRegistry()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    { "id": "rimworld/get_screenshot", "title": "Screenshot", "summary": "Capture the game screen", "category": "screenshots", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/set_camera", "title": "Camera view", "summary": "Control the camera", "category": "view", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_game_state", "title": "Runtime state", "summary": "Inspect state", "category": "inspection", "providerId": "rimworld", "source": "Core" }
                  ]
                }
              }
            }
            """,
            "--query",
            "camera",
            "--category",
            "view",
            "--provider",
            "rimworld",
            "--source",
            "Core");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("camera", root.GetProperty("query").GetString());
        AssertEqual("view", root.GetProperty("category").GetString());
        AssertEqual("rimworld", root.GetProperty("providerId").GetString());
        AssertEqual("Core", root.GetProperty("source").GetString());
        AssertEqual(1, root.GetProperty("totalMatches").GetInt32());
        AssertEqual(
            "rimworld/set_camera",
            root.GetProperty("capabilities")[0].GetProperty("id").GetString());
    }

    private static void CapabilitiesBoundOutput()
    {
        var tools = Enumerable.Range(1, 25)
            .Select(index => new
            {
                id = $"rimworld/tool_{index:00}",
                title = $"Tool {index}",
                summary = "Registered capability",
                category = "inspection",
                providerId = "rimworld",
                source = "Core"
            })
            .ToArray();
        string response = JsonSerializer.Serialize(new
        {
            success = true,
            rimBridgeRoute = new
            {
                success = true,
                result = new { tools }
            }
        });

        (CliResult result, _) = RunCapabilitiesFixture(response, "--limit", "3");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(3, root.GetProperty("count").GetInt32());
        AssertEqual(25, root.GetProperty("totalMatches").GetInt32());
        Assert(root.GetProperty("truncated").GetBoolean(),
            "Capability discovery must report bounded output.");
        AssertEqual(3, root.GetProperty("capabilities").GetArrayLength());
    }

    private static void CapabilitiesPreserveParameterMetadata()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    {
                      "id": "rimworld/get_game_state",
                      "title": "Runtime state",
                      "summary": "Inspect live game state",
                      "parameters": [
                        { "name": "includeColonists", "parameterType": "boolean", "description": "Include colonists", "required": true, "defaultValue": false },
                        { "name": "mapId", "parameterType": "string", "description": "Map identifier", "required": false }
                      ]
                    },
                    {
                      "name": "legacy/get_state",
                      "description": "Legacy state inspection",
                      "inputSchema": {
                        "type": "object",
                        "properties": { "target": { "type": "string", "description": "State target" } },
                        "required": ["target"]
                      }
                    }
                  ]
                }
              }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement state = root.GetProperty("capabilities")
            .EnumerateArray()
            .Single(capability => capability.GetProperty("id").GetString() == "rimworld/get_game_state");
        JsonElement includeColonists = state.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "includeColonists");
        AssertEqual("boolean", includeColonists.GetProperty("type").GetString());
        AssertEqual("Include colonists", includeColonists.GetProperty("description").GetString());
        Assert(includeColonists.GetProperty("required").GetBoolean(),
            "Required capability parameters must be marked required.");
        Assert(!includeColonists.GetProperty("default").GetBoolean(),
            "Capability parameter defaults must be preserved.");

        JsonElement legacy = root.GetProperty("capabilities")
            .EnumerateArray()
            .Single(capability => capability.GetProperty("id").GetString() == "legacy/get_state");
        Assert(legacy.GetProperty("parameters")[0].GetProperty("required").GetBoolean(),
            "Legacy inputSchema required parameters must remain authorable.");
    }

    private static void CapabilitiesReportUnavailableBridge()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            { "success": false, "errorCode": "RIMBRIDGE_NOT_READY", "error": "No ready live-game route" }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("rimbridge", root.GetProperty("component").GetString());
        AssertEqual("RIMBRIDGE_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("bridge tools", StringComparison.Ordinal),
            "Unavailable discovery must not hand agents a manual bridge probe.");
    }

    private static void CapabilitiesRejectMalformedResponse()
    {
        (CliResult result, _) = RunCapabilitiesFixture("{\"success\":true");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("RIMBRIDGE_CAPABILITIES_JSON_INVALID", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void CapabilitiesRejectIncompatibleResponse()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "schemaVersion": "rimbridge-tools/v2",
              "success": true,
              "rimBridgeRoute": { "success": true, "result": { "tools": [] } }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("incompatibleSchema", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void CapabilityDiscoveryDoesNotMutateLifecycle()
    {
        (CliResult result, FakeTransport transport) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": { "success": true, "result": { "tools": [] } }
            }
            """);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(1, transport.Requests.Count);
        DevBridgeProcessRequest request = transport.Requests[0];
        AssertSequence(
            ["--root", request.Arguments[1], "bridge", "tools", "--json"],
            request.Arguments);
        Assert(!request.Arguments.Contains("call", StringComparer.OrdinalIgnoreCase),
            "Capability discovery must not expose a generic bridge call.");
        Assert(!request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase),
            "Capability discovery must not begin a lifecycle session.");
    }

    private static void UiTargetEnumeration()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture("targets");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-ui-targets/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual(2, root.GetProperty("count").GetInt32());
        JsonElement target = root.GetProperty("targets")
            .EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == "window:main");
        AssertEqual("window", target.GetProperty("kind").GetString());
        AssertEqual("Main window", target.GetProperty("label").GetString());
        AssertEqual(2, target.GetProperty("rect").GetProperty("width").GetInt32());
        AssertEqual(2, transport.Requests.Count);
        Assert(transport.Requests[0].Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase),
            "Target enumeration must discover registered tools.");
        Assert(transport.Requests[1].Arguments.Any(argument =>
                argument.Contains("get_screen_targets", StringComparison.OrdinalIgnoreCase)),
            "Target enumeration must call the registered screen-target capability.");
    }

    private static void UiTargetObjectSchemaIsSupported()
    {
        (CliResult result, _) = RunUiFixture(
            "targets",
            targetResponse: RouteResponse(
                """
                {
                  "success": true,
                  "targets": {
                    "windows": [
                      {
                        "windowTargetId": "window:dialog",
                        "kind": "window",
                        "title": "Dialog",
                        "rect": { "x": 1, "y": 2, "width": 3, "height": 4 }
                      }
                    ]
                  }
                }
                """,
                "op-targets-object"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(1, root.GetProperty("count").GetInt32());
        JsonElement target = root.GetProperty("targets").EnumerateArray().Single();
        AssertEqual("window:dialog", target.GetProperty("id").GetString());
        AssertEqual("Dialog", target.GetProperty("label").GetString());
    }

    private static void UiTargetDiscoveryRecoversRequiredLease()
    {
        string directory = CreateTempDirectory();
        try
        {
            int toolsCalls = 0;
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            "{\"success\":true,\"exitCode\":0,\"leaseId\":\"lease-targets\",\"generation\":1}");
                    }

                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult("{\"success\":true,\"exitCode\":0}");
                    }

                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        toolsCalls++;
                        return toolsCalls == 1
                            ? ProcessResult(
                                "{\"success\":false,\"errorCode\":\"RIMBRIDGE_LEASE_REQUIRED\",\"error\":\"lease required\"}")
                            : ProcessResult(UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiTargetsCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected target-discovery request: " +
                        string.Join(" ", request.Arguments));
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        [
                            "ui",
                            "targets",
                            "--json",
                            "--devbridge",
                            "DevBridge.cmd",
                            "--devbridge-root",
                            directory
                        ],
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("ok", root.GetProperty("status").GetString());
            AssertEqual(2, root.GetProperty("count").GetInt32());
            AssertEqual(2, toolsCalls);
            Assert(transport.Requests.Any(request =>
                    request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase)),
                "Lease-required target discovery must acquire a lease.");
            Assert(transport.Requests.Any(request =>
                    request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase)),
                "Lease-required target discovery must release its lease.");
            Assert(transport.Requests
                .Where(request => request.Arguments.Contains(
                    "rimworld/get_screen_targets",
                    StringComparer.OrdinalIgnoreCase))
                .All(request => request.Arguments.Contains("lease-targets", StringComparer.Ordinal)),
                "The retried target call must carry the acquired lease.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void UiBridgeCallsCarryWorkflowIdentity()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "current"]);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        DevBridgeProcessRequest[] bridgeCalls = transport.Requests
            .Where(request => request.Arguments.Contains(
                "call",
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        DevBridgeProcessRequest[] routedUiRequests = transport.Requests
            .Where(request => request.Arguments.Contains(
                    "tools",
                    StringComparer.OrdinalIgnoreCase) ||
                request.Arguments.Contains("call", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert(bridgeCalls.Length > 0 &&
            bridgeCalls.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue(
                    "DEVBRIDGE_AGENT",
                out string? agent) &&
                !string.IsNullOrWhiteSpace(agent)),
            "All UI bridge calls must carry the canonical workflow owner identity.");
        Assert(bridgeCalls.All(request =>
                request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("lease-1", StringComparer.Ordinal)),
            "Transactional UI bridge calls must carry the acquired lease identity.");
        Assert(routedUiRequests.All(request =>
                request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("lease-1", StringComparer.Ordinal)),
            "Transactional UI capability discovery and calls must share the acquired lease identity.");
    }

    private static void UiTargetedScreenshotUsesClipping()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-ui-screenshot/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("captured", root.GetProperty("captureStatus").GetString());
        AssertEqual("window:main", root.GetProperty("targetId").GetString());
        AssertEqual("window", root.GetProperty("targetKind").GetString());
        AssertEqual("Main window", root.GetProperty("targetLabel").GetString());
        AssertEqual("/evidence/main.png", root.GetProperty("path").GetString());
        AssertEqual("op-target-shot", root.GetProperty("operationId").GetString());
        AssertEqual(4, transport.Requests.Count);
        DevBridgeProcessRequest screenshotRequest = transport.Requests
            .Single(request => request.Arguments.Contains(
                "rimworld/take_screenshot",
                StringComparer.OrdinalIgnoreCase));
        using JsonDocument arguments = JsonDocument.Parse(screenshotRequest.Arguments[5]);
        JsonElement screenshotArguments = arguments.RootElement;
        AssertEqual("window:main", screenshotArguments.GetProperty("targetId").GetString());
        Assert(screenshotArguments.GetProperty("waitForVisualReady").GetBoolean(),
            "Targeted captures must wait for visual readiness.");
        Assert(!screenshotArguments.GetProperty("doNotResetCamera").GetBoolean(),
            "Targeted captures must preserve camera restoration policy.");
        Assert(screenshotArguments.GetProperty("includeScreenTargets").GetBoolean(),
            "Targeted captures must use RimBridge target clipping.");
        Assert(!screenshotRequest.Arguments.Contains(
            "rimworld/get_screenshot",
            StringComparer.OrdinalIgnoreCase),
            "RimLiaison must not substitute an unrestricted full-screen capture.");
    }

    private static void UiMissingTargetFailsBeforeCapture()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:missing"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.NotFound, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("targetNotFound", root.GetProperty("outcome").GetString());
        AssertEqual("RIMTEST_UI_TARGET_NOT_FOUND", root.GetProperty("code").GetString());
        Assert(!transport.Requests.Any(request => request.Arguments.Contains(
            "rimworld/take_screenshot",
            StringComparer.OrdinalIgnoreCase)),
            "A missing target must fail before the screenshot operation.");
    }

    private static void UiReportsUnavailableBridge()
    {
        (CliResult result, _) = RunUiFixture(
            "targets",
            toolsResponse: """
                { "success": false, "errorCode": "RIMBRIDGE_NOT_READY", "error": "No live route" }
                """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("unavailable", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("bridge tools", StringComparison.OrdinalIgnoreCase),
            "Unavailable UI discovery must return the RimLiaison owner handoff.");
    }

    private static void UiReportsVisualReadinessFailure()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"],
            targetScreenshotResponse: RouteResponse(
                """
                { "success": false, "errorCode": "RIMBRIDGE_VISUAL_NOT_READY", "error": "Renderer is not ready" }
                """,
                "op-not-ready"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("visualReadinessFailure", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_VISUAL_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("op-not-ready", root.GetProperty("operationId").GetString());
    }

    private static void UiCellCapturePreservesCamera()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--cell-rect", "10,20,3,4"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("/evidence/cell.png", root.GetProperty("path").GetString());
        Assert(root.GetProperty("cameraRestored").GetBoolean(),
            "Cell capture must preserve RimBridgeServer camera restoration.");
        AssertEqual(10, root.GetProperty("requestedRect").GetProperty("x").GetInt32());
        AssertEqual(4, root.GetProperty("requestedRect").GetProperty("height").GetInt32());
        DevBridgeProcessRequest cellRequest = transport.Requests
            .Single(request => request.Arguments.Contains(
                "rimworld/screenshot_cell_rect",
                StringComparer.OrdinalIgnoreCase));
        using JsonDocument arguments = JsonDocument.Parse(cellRequest.Arguments[5]);
        JsonElement captureArguments = arguments.RootElement;
        AssertEqual(10, captureArguments.GetProperty("x").GetInt32());
        AssertEqual(20, captureArguments.GetProperty("z").GetInt32());
        AssertEqual(3, captureArguments.GetProperty("width").GetInt32());
        AssertEqual(4, captureArguments.GetProperty("height").GetInt32());
        Assert(!captureArguments.GetProperty("doNotResetCamera").GetBoolean(),
            "Cell capture must request camera restoration.");
    }

    private static void UiRequestsDoNotMutateLifecycle()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        string[] lifecycleTerms =
        [
            "begin",
            "start",
            "restart",
            "kill",
            "lease",
            "lifecycle",
            "generation"
        ];
        Assert(!transport.Requests
            .SelectMany(request => request.Arguments)
            .Any(argument => lifecycleTerms.Any(term =>
                argument.Contains(term, StringComparison.OrdinalIgnoreCase))),
            "UI discovery/capture must not acquire leases or mutate lifecycle state.");
    }

    private static void TransactionalUiViewportCapturesAndRestores()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ok", root.GetProperty("status").GetString());
        JsonElement viewport = root.GetProperty("viewport");
        JsonElement preparation = viewport.GetProperty("preparation");
        JsonElement restoration = viewport.GetProperty("restoration");
        AssertEqual("prepared", preparation.GetProperty("status").GetString());
        AssertEqual("restored", restoration.GetProperty("status").GetString());
        AssertEqual(1024, preparation.GetProperty("effectiveViewport")
            .GetProperty("clientWidth").GetInt32());
        AssertEqual(768, preparation.GetProperty("effectiveViewport")
            .GetProperty("clientHeight").GetInt32());
        Assert(!preparation.GetProperty("persistentPreferenceMutation").GetBoolean(),
            "temporary viewport evidence must prove no persistent preference mutation");
        Assert(restoration.GetProperty("restorationVerified").GetBoolean(),
            "temporary viewport evidence must prove restoration");

        int beginIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase));
        int restoreIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase));
        int endIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase));
        Assert(beginIndex >= 0 && restoreIndex > beginIndex && endIndex > restoreIndex,
            "transactional UI validation must restore before releasing its temporary lease");
    }

    private static void TransactionalUiViewportSurfacesRestorationFailure()
    {
        (CliResult result, _) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"],
            restoreResponse: """
                {"success":false,"exitCode":4,"viewport":{"schemaVersion":"devbridge-viewport-environment/v1","success":false,"status":"cleanupFailed","errorCode":"VIEWPORT_RESTORE_FAILED","error":"The original window state could not be verified.","transactionId":"viewport-1","leaseId":"lease-1","persistentPreferenceMutation":false,"restorationVerified":false,"cleanupStatus":"restore-required"}}
                """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("RIMTEST_UI_VIEWPORT_RESTORE_FAILED", root.GetProperty("code").GetString());
        AssertEqual("/evidence/main.png", root.GetProperty("screenshotEvidence")
            .GetProperty("path").GetString());
        AssertEqual("restorationFailure", root.GetProperty("viewportRestoration")
            .GetProperty("outcome").GetString());
        Assert(!root.GetProperty("viewportRestoration")
            .GetProperty("restorationVerified").GetBoolean(),
            "restoration failure must remain explicit in machine-readable evidence");
    }

    private static void TransactionalUiViewportRestoresAfterUiFailure()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"],
            screenshotResponse: RouteResponse(
                "{\"success\":false,\"errorCode\":\"RIMBRIDGE_UI_ASSERTION_FAILED\",\"error\":\"layout assertion failed\"}",
                "op-ui-failure"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("RIMBRIDGE_UI_ASSERTION_FAILED", root.GetProperty("code").GetString());
        AssertEqual("restored", root.GetProperty("viewportRestoration")
            .GetProperty("status").GetString());
        Assert(transport.Requests.Any(request =>
                request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase)),
            "viewport cleanup must run after a UI operation failure");
    }

    private static void TransactionalUiViewportValidatesExplicitDimensions()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main", "--viewport", "explicit",
                "--viewport-width", "319", "--viewport-height", "720"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        AssertEqual("CLI_INVALID", root.GetProperty("code").GetString());
        Assert(root.GetProperty("errors").EnumerateArray().Single()
            .GetProperty("message").GetString()!.Contains("viewport-width", StringComparison.OrdinalIgnoreCase),
            "explicit viewport bounds must be validated before any DevBridge call");
    }

    private static void UiOutputIsCompact()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        Assert(result.Stdout.Length < 1200,
            "UI screenshot output should remain compact evidence metadata.");
        Assert(!result.Stdout.Contains("cameraBefore", StringComparison.OrdinalIgnoreCase),
            "RimLiaison must not dump camera diagnostics into compact output.");
        Assert(!result.Stdout.Contains("cameraDuringCapture", StringComparison.OrdinalIgnoreCase),
            "RimLiaison must not dump the full bridge payload.");
        Assert(!result.Stdout.Contains("sourcePath", StringComparison.OrdinalIgnoreCase),
            "UI output should expose the selected screenshot path once.");
    }

    private static void CanonicalUiGuidanceIsGenerated()
    {
        string directory = CreateTempDirectory();
        try
        {
            CliResult result = RunInitFixture(directory);
            AssertEqual(CliExitCodes.Success, result.ExitCode);
            string agents = File.ReadAllText(Path.Combine(directory, "AGENTS.md"));
            Assert(agents.Contains(
                "functional tests alone are insufficient",
                StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must require visual inspection for UI work.");
            Assert(agents.Contains("rimliaison ui targets", StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must point agents to target discovery.");
            Assert(agents.Contains("rimliaison ui screenshot", StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must point agents to selective screenshots.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DoctorHealthyOutputIsCompact()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("rimtest-doctor/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ready", root.GetProperty("status").GetString());
        AssertEqual("ok", root.GetProperty("catalog").GetString());
        AssertEqual("ok", root.GetProperty("rimctx").GetString());
        AssertEqual("ok", root.GetProperty("devbridge").GetString());
        AssertEqual("ok", root.GetProperty("rimerror").GetString());
        AssertEqual("configured", root.GetProperty("rimbridge").GetString());
        AssertEqual("rimliaison affected --run --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("findings", StringComparison.Ordinal),
            "Doctor must not copy the DevBridge doctor transcript.");
        Assert(string.IsNullOrEmpty(result.Stderr),
            "Healthy doctor output should not write diagnostics.");
    }

    private static void DoctorReportsBlockedComponent()
    {
        CliResult result = RunDoctorFixture(contextAvailable: false);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-doctor/v1\",\"status\":\"blocked\",\"component\":\"rimctx\",\"code\":\"INDEX_MISSING\",\"nextAction\":\"rimliaison affected --run --json\"}",
            result.Stdout.Trim());
        Assert(result.Stderr.Contains("rimctx INDEX_MISSING", StringComparison.Ordinal),
            "Blocked doctor diagnostics should identify the component and code.");
    }

    private static void DoctorReadsDevBridgeRimBridgeStatusShape()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            usePascalRimBridgeFields: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("configured", document.RootElement.GetProperty("rimbridge").GetString());
    }

    private static void StackManifestDefaultsAreUsed()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("FixtureMod", document.RootElement.GetProperty("project").GetString());
    }

    private static void ExplicitCliOverridesBeatManifest()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            useExplicitOverrides: true);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(!result.Stdout.Contains("override", StringComparison.Ordinal),
            "Local alias configuration must not leak into doctor output.");
    }

    private static void MalformedStackSchemaIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor("{\"schemaVersion\":");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-doctor/v1\",\"status\":\"blocked\",\"component\":\"manifest\",\"code\":\"STACK_MANIFEST_JSON_INVALID\",\"nextAction\":\"rimliaison init --json --manifest-only --force\"}",
            result.Stdout.Trim());
    }

    private static void UnknownStackSchemaIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor(
            "{\"schemaVersion\":\"rimdev-stack/v99\",\"project\":\"Fixture\",\"catalog\":\"catalog.json\",\"rimBridge\":\"via-devbridge\"}");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        AssertEqual(
            "STACK_MANIFEST_SCHEMA_UNSUPPORTED",
            JsonDocument.Parse(result.Stdout).RootElement.GetProperty("code").GetString());
    }

    private static void MissingStackManifestIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor(manifest: null);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("STACK_MANIFEST_MISSING", document.RootElement.GetProperty("code").GetString());
        AssertEqual("rimliaison init --json", document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void LocalConfigurationDoesNotLeak()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        Assert(!result.Stdout.Contains("\"fixture\"", StringComparison.Ordinal),
            "Doctor output must not expose the DevBridge alias.");
        Assert(!result.Stdout.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "Doctor output must not expose machine-local paths.");
    }

    private static void InitCreatesEmptyRepositoryHandoff()
    {
        string directory = CreateTempDirectory();
        try
        {
            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            Assert(File.Exists(Path.Combine(directory, ".rimdev", "stack.json")),
                $"init must create the stack manifest. stdout={result.Stdout} stderr={result.Stderr}");
            Assert(File.Exists(Path.Combine(directory, "AGENTS.md")),
                "init must create the canonical AGENTS template.");
            using JsonDocument manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("rimdev-stack/v1", manifest.RootElement.GetProperty("schemaVersion").GetString());
            AssertEqual("TestCatalog/rimtest.catalog.json", manifest.RootElement.GetProperty("catalog").GetString());
            AssertEqual("via-devbridge", manifest.RootElement.GetProperty("rimBridge").GetString());
            Assert(!manifest.RootElement.TryGetProperty("devBridgeProject", out _),
                "init must not guess a DevBridge alias.");
            Assert(!result.Stdout.Contains(directory, StringComparison.OrdinalIgnoreCase),
                "init output must use repository-relative paths.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitFillsMissingManifestFieldSafely()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(
                Path.Combine(directory, "catalog.json"),
                Serialize(CreateCatalog()));
            string manifestPath = Path.Combine(directory, ".rimdev", "stack.json");
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"custom\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            AssertEqual("smoke", document.RootElement.GetProperty("fallbackSuite").GetString());
            AssertEqual("Custom", document.RootElement.GetProperty("project").GetString());
            AssertEqual("custom", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("catalog.json", document.RootElement.GetProperty("catalog").GetString());
            AssertEqual("disabled", document.RootElement.GetProperty("rimBridge").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitMergesExplicitConfigurationSafely()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(
                Path.Combine(directory, "catalog.json"),
                Serialize(CreateCatalog()));
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "target-specific instructions\n");
            string manifestPath = Path.Combine(directory, ".rimdev", "stack.json");
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(
                directory,
                "--devbridge-project",
                "custom-project",
                "--fallback-suite",
                "smoke");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("target-specific instructions\n", File.ReadAllText(agentsPath));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            AssertEqual("custom-project", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("smoke", document.RootElement.GetProperty("fallbackSuite").GetString());
            AssertEqual("Custom", document.RootElement.GetProperty("project").GetString());
            AssertEqual("catalog.json", document.RootElement.GetProperty("catalog").GetString());
            AssertEqual("disabled", document.RootElement.GetProperty("rimBridge").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitPreservesExistingAgents()
    {
        string directory = CreateTempDirectory();
        try
        {
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "custom instructions\n");
            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("custom instructions\n", File.ReadAllText(agentsPath));
            Assert(result.Stdout.Contains("AGENTS.md", StringComparison.Ordinal),
                "init should report the existing AGENTS file.");
            Assert(result.Stdout.Contains("existing", StringComparison.Ordinal),
                "init should not overwrite an existing AGENTS file.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitPreservesExistingManifest()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rimDevDirectory = Path.Combine(directory, ".rimdev");
            Directory.CreateDirectory(rimDevDirectory);
            string manifestPath = Path.Combine(rimDevDirectory, "stack.json");
            string existing = "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"custom\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"disabled\"}\n";
            File.WriteAllText(manifestPath, existing);

            CliResult result = RunInitFixture(
                directory,
                "--devbridge-project",
                "new-project",
                "--fallback-suite",
                "new-suite");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual(existing, File.ReadAllText(manifestPath));
            Assert(result.Stdout.Contains("stack.json", StringComparison.Ordinal),
                "init should report the existing manifest.");
            Assert(result.Stdout.Contains("existing", StringComparison.Ordinal),
                "init should not overwrite an existing manifest.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitIsIdempotent()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "TestCatalog"));
            File.WriteAllText(
                Path.Combine(directory, "TestCatalog", "rimtest.catalog.json"),
                Serialize(CreateCatalog()));

            CliResult first = RunInitFixture(directory);
            string manifest = File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json"));
            string agents = File.ReadAllText(Path.Combine(directory, "AGENTS.md"));
            CliResult second = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, first.ExitCode);
            AssertEqual(CliExitCodes.Success, second.ExitCode);
            AssertEqual(manifest, File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual(agents, File.ReadAllText(Path.Combine(directory, "AGENTS.md")));
            Assert(second.Stdout.Contains("\"status\":\"existing\"", StringComparison.Ordinal),
                "Repeated init should report existing files without rewriting them.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitForceBehaviorIsIntentional()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "replace me\n");
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "stack.json"),
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"old-project\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(
                directory,
                "--force",
                "--devbridge-project",
                "new-project",
                "--fallback-suite",
                "settings");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            Assert(!string.Equals(
                    "replace me\n",
                    File.ReadAllText(Path.Combine(directory, "AGENTS.md")),
                    StringComparison.Ordinal),
                "--force must retain its intentional AGENTS overwrite behavior.");
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("new-project", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("settings", document.RootElement.GetProperty("fallbackSuite").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ManifestOnlyRepairPreservesAgents()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "keep this handoff\n");
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "stack.json"),
                "{\"schemaVersion\":");

            CliResult result = RunInitFixture(
                directory,
                "--manifest-only",
                "--force");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("keep this handoff\n", File.ReadAllText(agentsPath));
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("rimdev-stack/v1", document.RootElement.GetProperty("schemaVersion").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DoctorMissingProjectProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}",
            Serialize(CreateCatalog()));

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(
            "STACK_MANIFEST_DEVBRIDGE_PROJECT_MISSING",
            document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --devbridge-project <project>",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorMissingFallbackProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"rimBridge\":\"via-devbridge\"}",
            Serialize(CreateCatalog()));

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(
            "STACK_MANIFEST_FALLBACK_SUITE_MISSING",
            document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --fallback-suite smoke",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorMissingCatalogProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctor(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("CATALOG_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --manifest-only --force --catalog catalog.json",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorInvalidCatalogProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}",
            "{\"schemaVersion\":");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("CATALOG_JSON_INVALID", document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison validate --json --catalog catalog.json",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void AffectedDiscoversGitChangesWithoutPaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                true,
                ["Source/Staged.cs", "Source/New.cs"]));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--base", "origin/main", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(1, git.Calls.Count);
            AssertEqual("origin/main", git.Calls[0].Base);
            AssertSequence(
                ["Source/Staged.cs", "Source/New.cs"],
                impactAdapter.ChangedPaths);
            Assert(stdout.ToString().Contains(
                "\"status\":\"ok\"",
                StringComparison.Ordinal),
                "Automatic Git changes should feed RimContext selection.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void GitDiscoveryIncludesStagedAndUntrackedFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            RunGit(directory, "init", "--quiet");
            RunGit(directory, "config", "user.email", "RimLiaison@example.invalid");
            RunGit(directory, "config", "user.name", "RimLiaison");
            string tracked = Path.Combine(directory, "Source", "Tracked.cs");
            File.WriteAllText(tracked, "class Tracked {}\n");
            RunGit(directory, "add", "Source/Tracked.cs");
            RunGit(directory, "commit", "--quiet", "-m", "initial");
            File.WriteAllText(tracked, "class Tracked { int Value; }\n");
            File.WriteAllText(Path.Combine(directory, "Source", "Staged.cs"), "class Staged {}\n");
            RunGit(directory, "add", "Source/Staged.cs");
            File.WriteAllText(Path.Combine(directory, "Source", "Untracked.cs"), "class Untracked {}\n");
            File.WriteAllText(Path.Combine(directory, "bin", "Generated.cs"), "generated\n");

            GitChangeDiscoveryResult result = new SystemGitChangeProvider()
                .DiscoverAsync(directory)
                .GetAwaiter()
                .GetResult();

            Assert(result.Resolved, result.Error ?? "Git discovery should resolve.");
            Assert(result.Paths.Contains("Source/Tracked.cs"), "Tracked modification was not discovered.");
            Assert(result.Paths.Contains("Source/Staged.cs"), "Staged file was not discovered.");
            Assert(result.Paths.Contains("Source/Untracked.cs"), "Untracked file was not discovered.");
            Assert(!result.Paths.Any(path => path.StartsWith("bin/", StringComparison.Ordinal)),
                "Generated build directories must be excluded.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void GitDiscoveryPreservesDeletedAndRenamedPaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            RunGit(directory, "init", "--quiet");
            RunGit(directory, "config", "user.email", "RimLiaison@example.invalid");
            RunGit(directory, "config", "user.name", "RimLiaison");
            File.WriteAllText(Path.Combine(directory, "Source", "Old.cs"), "old\n");
            File.WriteAllText(Path.Combine(directory, "Source", "Deleted.cs"), "deleted\n");
            RunGit(directory, "add", "Source/Old.cs", "Source/Deleted.cs");
            RunGit(directory, "commit", "--quiet", "-m", "initial");
            RunGit(directory, "mv", "Source/Old.cs", "Source/New.cs");
            RunGit(directory, "rm", "Source/Deleted.cs");

            GitChangeDiscoveryResult result = new SystemGitChangeProvider()
                .DiscoverAsync(directory)
                .GetAwaiter()
                .GetResult();

            Assert(result.Resolved, result.Error ?? "Git discovery should resolve.");
            Assert(result.Paths.Contains("Source/Old.cs"),
                "The rename source must remain in the changed path set.");
            Assert(result.Paths.Contains("Source/New.cs"),
                "The rename destination must remain in the changed path set.");
            Assert(result.Paths.Contains("Source/Deleted.cs"),
                "Deleted paths must remain in the changed path set.");
            GitChangedPath rename = result.Changes.Single(change => change.IsRenamed);
            AssertEqual("Source/New.cs", rename.Path);
            AssertEqual("Source/Old.cs", rename.OriginalPath);
            Assert(result.Changes.Any(change =>
                    change.IsDeleted && change.Path == "Source/Deleted.cs"),
                "Git discovery must retain deletion status.");

            GitChangeDiscoveryResult baseResult = new SystemGitChangeProvider()
                .DiscoverAsync(directory, "HEAD")
                .GetAwaiter()
                .GetResult();
            Assert(baseResult.Resolved, baseResult.Error ?? "Git base discovery should resolve.");
            Assert(baseResult.Paths.Contains("Source/Old.cs"),
                "Base diff discovery must retain the rename source.");
            Assert(baseResult.Paths.Contains("Source/New.cs"),
                "Base diff discovery must retain the rename destination.");
            Assert(baseResult.Paths.Contains("Source/Deleted.cs"),
                "Base diff discovery must retain deleted paths.");
            Assert(baseResult.Changes.Any(change =>
                    change.IsRenamed &&
                    change.Path == "Source/New.cs" &&
                    change.OriginalPath == "Source/Old.cs"),
                "Base diff discovery must preserve rename source/destination.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void CleanAffectedRunIsExplicitAndDoesNotLaunch()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(true, []));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--run", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    adapter,
                    gitChangeProvider: git,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("ok", root.GetProperty("status").GetString());
            AssertEqual(0, root.GetProperty("tests").GetArrayLength());
            AssertEqual(0, adapter.RunCalls.Count);
            AssertEqual(0, developmentAdapter.Calls.Count);
            Assert(string.IsNullOrEmpty(stderr.ToString()),
                "A clean affected run should not write diagnostics.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedSourceRunPerformsFreshnessTransaction()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual(1, root.GetProperty("passed").GetInt32());
        AssertEqual("deployed", freshness.GetProperty("deploymentDecision").GetString());
        AssertEqual("FRESH", freshness.GetProperty("evaluationStatus").GetString());
        AssertEqual(
            new string('b', 64),
            freshness.GetProperty("builtArtifactSha256").GetString());
        AssertEqual(
            new string('b', 64),
            freshness.GetProperty("deployedArtifactSha256").GetString());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A deployed source-change pass must carry a freshness proof.");
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        AssertEqual(1, result.DevelopmentCalls.Count);
        AssertEqual(1, result.RecipeCalls.Count);
        Assert(RimTestOutputBudgets.Utf8Bytes(result.Stdout) <=
            RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Freshness success output must remain bounded.");
        Assert(!string.IsNullOrWhiteSpace(root.GetProperty("workflowId").GetString()),
            "Affected runs must create a workflow correlation id.");
        AssertEqual(
            root.GetProperty("workflowId").GetString(),
            result.DevelopmentCalls[0].WorkflowId);
    }

    private static void FailFastAffectedRunStillProvesFreshness()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            failFast: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        Assert(root.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(),
            "Fail-fast must not bypass the artifact freshness transaction.");
        JsonElement failFast = root.GetProperty("failFast");
        AssertEqual(0, failFast.GetProperty("notLaunched").GetInt32());
        Assert(failFast.GetProperty("validationCompleted").GetBoolean(),
            "A passing affected fail-fast run must prove complete validation.");
        AssertEqual(1, result.DevelopmentCalls.Count);
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void AffectedCompanionRecipeMayUseLaterGeneration()
    {
        CatalogDocument catalog = new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "artifact-smoke",
                    Recipe = "artifact-fixture",
                    ArtifactFreshnessAnchor = true,
                    Covers =
                    [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                },
                new CatalogTest
                {
                    Id = "companion-smoke",
                    Recipe = "companion-fixture",
                    Covers =
                    [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                }
            ],
            Suites = []
        };
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed",
                generation: 7),
            recipeRunFactory: (recipeId, _) => PassRunWithGeneration(
                recipeId,
                recipeId == "artifact-fixture" ? 7 : 8),
            scenarioCatalog: catalog);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual(2, root.GetProperty("passed").GetInt32());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A later-generation companion recipe must not invalidate the artifact anchor proof.");
        AssertEqual("artifact-smoke", freshness.GetProperty("artifactTestId").GetString());
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        AssertEqual(2, result.RecipeCalls.Count);
    }

    private static void AffectedIdenticalArtifactUsesNoDeployProof()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "unchanged"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement freshness = document.RootElement.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", document.RootElement.GetProperty("status").GetString());
        AssertEqual("unchanged", freshness.GetProperty("deploymentDecision").GetString());
        AssertEqual(
            "identical-deployment-hash-plus-owned-generation-state",
            freshness.GetProperty("proof").GetString());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "An identical artifact fast path still needs owned generation evidence.");
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void AffectedBuildFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "DEVELOPMENT_BUILD_FAILED"));

        AssertArtifactTransactionFailure(result, "DEVELOPMENT_BUILD_FAILED");
    }

    private static void AffectedDeploymentFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "DEVELOPMENT_DEPLOYMENT_FAILED"));

        AssertArtifactTransactionFailure(result, "DEVELOPMENT_DEPLOYMENT_FAILED");
    }

    private static void AffectedReadinessFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "READINESS_TIMEOUT"));

        AssertArtifactTransactionFailure(result, "READINESS_TIMEOUT");
    }

    private static void AffectedRunRecoversReadinessOnce()
    {
        string directory = CreateTempDirectory();
        try
        {
            string fingerprint = Convert.ToHexString(
                SHA256.HashData([])).ToLowerInvariant();
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) => calls++ == 0
                    ? FailedDevelopmentResult(workflowId, "PROCESS_EXITED")
                    : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged")
            };
            var readiness = new FakeFreshGenerationAdapter(8);
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, readinessAdapter: readiness)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-readiness-recovery",
                            TestRecipe: "recipe-a"))
                    .GetAwaiter()
                    .GetResult();

            Assert(result.Success, "A stopped runtime should recover before the owner transaction is retried.");
            AssertEqual(2, development.Calls.Count);
            AssertEqual(1, readiness.Calls.Count);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            AssertEqual("FRESH", result.Freshness.EvaluationStatus);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void AffectedGenerationMismatchBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            PassRunWithGeneration("assembler-fixture", 8));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual("RIMTEST_ARTIFACT_GENERATION_MISMATCH",
            root.GetProperty("failures")[0].GetProperty("errorCode").GetString());
        AssertEqual("RIMTEST_ARTIFACT_GENERATION_MISMATCH", freshness.GetProperty("errorCode").GetString());
        Assert(!freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A mismatched generation must invalidate the freshness proof.");
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void AffectedUnknownFreshnessBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed",
                loadedArtifactFreshnessProven: false,
                errorCode: "DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN"));

        AssertArtifactTransactionFailure(
            result,
            "DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN");
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert(!document.RootElement.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(),
            "Unknown owner freshness must remain an explicit failure.");
    }

    private static void AffectedIncompleteFreshnessMetadataBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) =>
            {
                DevBridgeModDevelopmentResult complete = SuccessfulDevelopmentResult(
                    sourceFingerprint,
                    workflowId,
                    "deployed");
                return complete with
                {
                    Freshness = complete.Freshness! with
                    {
                        BuiltArtifactSha256 = null
                    }
                };
            });

        AssertArtifactTransactionFailure(result, "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN");
    }

    private static void AffectedPropagatesTransactionIdentities()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            PassRunWithIdentity("assembler-fixture", 7, "op-assembler-1"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(
            root.GetProperty("workflowId").GetString(),
            freshness.GetProperty("workflowId").GetString());
        AssertEqual("run-assembler-fixture", freshness.GetProperty("runId").GetString());
        AssertEqual(
            "op-assembler-1",
            freshness.GetProperty("operationIds")[0].GetString());
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        Assert(!string.IsNullOrWhiteSpace(freshness.GetProperty("transactionId").GetString()),
            "The DevBridge transaction identity must reach the result.");
        Assert(!string.IsNullOrWhiteSpace(freshness.GetProperty("leaseId").GetString()),
            "The DevBridge lease identity must reach the result.");
    }

    private static void ModDevelopmentAdapterParsesBoundedFreshnessResponse()
    {
        const string workflowId = "wf-mod-1";
        const string sourceFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string response = JsonSerializer.Serialize(new
        {
            schemaVersion = DevBridgeModDevelopmentSchemas.Current,
            project = "fixture",
            success = true,
            transactionId = "tx-1",
            workflowId,
            generation = 7,
            leaseId = "lease-00000000000000000000000000000001",
            artifactFreshness = new
            {
                sourceFingerprint,
                builtArtifactSha256 = new string('b', 64),
                deployedArtifactSha256 = new string('b', 64),
                deploymentDecision = "deployed",
                generationBefore = 6,
                generationAfter = 7,
                generation = 7,
                transactionId = "tx-1",
                workflowId,
                leaseId = "lease-00000000000000000000000000000001",
                loadedArtifactFreshnessProven = true,
                proof = "deployment-hash-plus-new-owned-generation"
            }
        });
        var transport = new FakeTransport((_, _) => ProcessResult(response));
        var adapter = new DevBridgeModDevelopmentAdapter(
            transport,
            new DevBridgeModDevelopmentAdapterOptions
            {
                RootPath = "DevBridgeRoot",
                DescriptorPath = "DevBridgeRoot/fixture.json",
                DeploymentRoot = "DeploymentRoot",
                PowerShellPath = "pwsh",
                Timeout = TimeSpan.FromSeconds(1),
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 1024
            });

        DevBridgeModDevelopmentResult result = adapter.RunAsync(
                "fixture",
                "RepositoryRoot",
                sourceFingerprint,
                workflowId)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status.IsSuccess, "A valid owner response should parse as success.");
        AssertEqual(7, result.Freshness!.Generation);
        Assert(result.Freshness.LoadedArtifactFreshnessProven,
            "The adapter must preserve the owner freshness proof.");
        AssertEqual(1, transport.Requests.Count);
        Assert(transport.Requests[0].Arguments.Contains("-SkipRecipe"),
            "RimLiaison must ask the owner for the transaction without running its broad recipe.");
        Assert(transport.Requests[0].Arguments.Contains("-SourceFingerprint") &&
            transport.Requests[0].Arguments.Contains(sourceFingerprint),
            "The source identity must cross the owner boundary.");
        Assert(transport.Requests[0].Arguments.Contains("-WorkflowId") &&
            transport.Requests[0].Arguments.Contains(workflowId),
            "The workflow identity must cross the owner boundary.");
        Assert(
            transport.Requests[0].Arguments.Count(argument =>
                string.Equals(argument, "-DevelopmentRoot", StringComparison.Ordinal)) == 1,
            "The owner must receive exactly one primary development root.");
        Assert(
            transport.Requests[0].Arguments.Count(argument =>
                string.Equals(argument, "-AdditionalDevelopmentRoot", StringComparison.Ordinal)) == 1,
            "The owner must receive the coordinator root as a distinct additional development root.");
        string[] requestArguments = transport.Requests[0].Arguments.ToArray();
        int developmentRootIndex = Array.IndexOf(requestArguments, "-DevelopmentRoot");
        int additionalRootIndex = Array.IndexOf(requestArguments, "-AdditionalDevelopmentRoot");
        AssertEqual(
            Path.GetFullPath("RepositoryRoot"),
            transport.Requests[0].Arguments[developmentRootIndex + 1]);
        AssertEqual("DevBridgeRoot", transport.Requests[0].Arguments[additionalRootIndex + 1]);
        AssertEqual(4096, transport.Requests[0].MaxStdoutBytes);
    }

    private static void ValidDevelopmentDescriptorIsPreserved()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            string original = """
                {
                  "schemaVersion": "devbridge-mod-development/v1",
                  "project": "fixture",
                  "sourceProject": "Source/Fixture.csproj",
                  "configuration": "Debug",
                  "expectedAssembly": "Fixture.dll",
                  "deploymentTarget": "custom/Assemblies/Fixture.dll",
                  "testRecipe": "custom-development"
                }
                """;
            File.WriteAllText(descriptorPath, original);

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "A valid descriptor should remain usable.");
            AssertEqual(PrerequisiteRecoveryState.Ready, result.State);
            AssertEqual(original, File.ReadAllText(descriptorPath));
            AssertEqual(0, result.Attempts);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void MissingDevelopmentDescriptorIsDerived()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "Canonical project metadata should derive the missing descriptor.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            AssertEqual(1, result.Attempts);
            Assert(File.Exists(descriptorPath), "The recovered descriptor should be atomically materialized.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("Source/Fixture.csproj", document.RootElement.GetProperty("sourceProject").GetString());
            AssertEqual("Fixture.dll", document.RootElement.GetProperty("expectedAssembly").GetString());
            AssertEqual("1.6/Assemblies/Fixture.dll", document.RootElement.GetProperty("deploymentTarget").GetString());
            AssertEqual("fixture-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void MalformedDevelopmentDescriptorIsRepaired()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            File.WriteAllText(descriptorPath, "{ malformed descriptor");

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "Malformed descriptor JSON should be safely reconstructed.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            Assert(!string.IsNullOrWhiteSpace(result.BackupPath) &&
                File.Exists(result.BackupPath),
                "The malformed input should remain recoverable through its backup.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("fixture", document.RootElement.GetProperty("project").GetString());
            AssertEqual("fixture-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void StaleDevelopmentDescriptorIsReconciledSafely()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            File.WriteAllText(
                descriptorPath,
                """
                {
                  "schemaVersion": "devbridge-mod-development/v1",
                  "project": "fixture",
                  "sourceProject": "Source/Deleted.csproj",
                  "configuration": "Debug",
                  "expectedAssembly": "Legacy.dll",
                  "deploymentTarget": "custom/Assemblies/Legacy.dll",
                  "testRecipe": "custom-development"
                }
                """);

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator) with
                    {
                        TestRecipe = null,
                        DeploymentTarget = null
                    });

            Assert(result.CanProceed, "A stale descriptor with canonical replacement metadata should recover.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            Assert(!string.IsNullOrWhiteSpace(result.BackupPath) &&
                File.Exists(result.BackupPath),
                "Reconciliation should preserve the stale descriptor as a bounded backup.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("Source/Fixture.csproj", document.RootElement.GetProperty("sourceProject").GetString());
            AssertEqual("Fixture.dll", document.RootElement.GetProperty("expectedAssembly").GetString());
            AssertEqual("Debug", document.RootElement.GetProperty("configuration").GetString());
            AssertEqual("custom/Assemblies/Legacy.dll", document.RootElement.GetProperty("deploymentTarget").GetString());
            AssertEqual("custom-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void AmbiguousDevelopmentDescriptorIsBlocked()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            File.WriteAllText(
                Path.Combine(repository, "Source", "Other.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "unknown.json");
            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "unknown",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator) with
                    {
                        ChangedPaths = null,
                        TestRecipe = "unknown-development",
                        DeploymentTarget = "1.6/Assemblies/Unknown.dll"
                    });

            Assert(!result.CanProceed, "Ambiguous canonical project metadata must fail closed.");
            AssertEqual(PrerequisiteRecoveryState.RecoveryRequired, result.State);
            AssertEqual("DEVBRIDGE_DESCRIPTOR_SOURCE_AMBIGUOUS", result.ErrorCode);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static DevBridgeModDevelopmentAdapterOptions DescriptorOptions(
        string coordinator) =>
        new()
        {
            RootPath = coordinator,
            ChangedPaths = ["Source/Changed.cs"],
            TestRecipe = "fixture-development"
        };

    private static (string Repository, string Coordinator) CreateDescriptorFixture()
    {
        string repository = CreateTempDirectory();
        string coordinator = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repository, "Source"));
        Directory.CreateDirectory(Path.Combine(repository, "1.6", "Assemblies"));
        Directory.CreateDirectory(Path.Combine(coordinator, "DevelopmentProjects"));
        File.WriteAllText(
            Path.Combine(repository, "Source", "Changed.cs"),
            "namespace Fixture; public class Changed {}\n");
        File.WriteAllText(
            Path.Combine(repository, "Source", "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
        return (repository, coordinator);
    }

    private static void LeaseRecoveryRetriesOwnerTransactionOnce()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) =>
                    calls++ == 0
                        ? FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED")
                        : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged")
            };
            var lease = new FakeLeaseAdapter();
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-lease-recovery"))
                    .GetAwaiter()
                    .GetResult();

            Assert(result.Success, "A compatible lease should allow one bounded owner retry.");
            AssertEqual(2, development.Calls.Count);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(1, lease.EndCalls);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void LeaseContentionRemainsExplicit()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            var development = new FakeModDevelopmentAdapter
            {
                Result = FailedDevelopmentResult(null, "RIMBRIDGE_LEASE_REQUIRED")
            };
            var lease = new FakeLeaseAdapter();
            lease.BeginResults.Enqueue(new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.DevBridgeRefusal,
                    "DEVBRIDGE_LEASE_CONTENDED"),
                null,
                null));
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            null))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "An actively owned lease must remain a blocker.");
            AssertEqual(PrerequisiteRecoveryState.Contended, result.Status.RecoveryState);
            AssertEqual(1, development.Calls.Count);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(0, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void LeaseRecoveryHasNoLoop()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (_, workflowId) =>
                {
                    calls++;
                    return FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED");
                }
            };
            var lease = new FakeLeaseAdapter();
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            null))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A second lease-required response must remain a bounded failure.");
            AssertEqual(2, calls);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(1, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void FreshnessCleanupFailureRemainsVisible()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) => calls++ == 0
                    ? FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED")
                    : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged")
            };
            var lease = new FakeLeaseAdapter
            {
                EndResult = new DevBridgeLeaseResult(
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_LEASE_RELEASE_FAILED"),
                    null,
                    null)
            };
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-cleanup-failure"))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A failed lease release must block the freshness proof.");
            AssertEqual("DEVBRIDGE_LEASE_RELEASE_FAILED", result.Status.ErrorCode);
            Assert(result.Cleanup is not null, "Lease cleanup evidence must be present on failure.");
            AssertEqual("FAILED", result.Cleanup!.Status);
            AssertEqual(false, result.Cleanup.LeaseReleased);
            AssertEqual(false, result.Cleanup.TemporaryStateCleared);
            AssertEqual(1, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void CleanupFailureRemainsIndependentInOrchestration()
    {
        CatalogSuiteExecutionResult execution = new(
            "affected",
            [new RimTestResult { Status = "fail", Test = "fixture" }],
            0,
            Cancelled: false,
            Cleanup: new RimTestCleanupSummary
            {
                Status = "FAILED",
                LeaseReleased = false,
                TemporaryStateCleared = false,
                ErrorCode = "DEVBRIDGE_LEASE_RELEASE_FAILED"
            });

        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 1);

        AssertEqual("TEST_FAILURE", result.Orchestration!.Overall);
        AssertEqual("FAILED", result.Orchestration.Cleanup!.Status);
        AssertEqual("RIMTEST_TEST_FAILURE", result.Orchestration.Failure!.ErrorCode);
    }

    private static void AssertArtifactTransactionFailure(
        AffectedScenarioResult result,
        string errorCode)
    {
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual(0, root.GetProperty("passed").GetInt32());
        AssertEqual(1, root.GetProperty("failed").GetInt32());
        AssertEqual(
            errorCode,
            root.GetProperty("failures")[0].GetProperty("errorCode").GetString());
        Assert(!root.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(), "A failed transaction cannot prove freshness.");
        JsonElement orchestration = root.GetProperty("orchestration");
        bool sourceBuildFailed = errorCode.StartsWith(
            "DEVELOPMENT_BUILD",
            StringComparison.Ordinal) || errorCode.StartsWith("BUILD_", StringComparison.Ordinal);
        AssertEqual(
            sourceBuildFailed ? "SOURCE_BUILD_FAILURE" : "INFRASTRUCTURE_FAILURE",
            orchestration.GetProperty("overall").GetString());
        AssertEqual(
            sourceBuildFailed ? "FAIL" : "NOT_RUN",
            orchestration.GetProperty("sourceBuild").GetString());
        string expectedDeployment = root.GetProperty("artifactFreshness")
            .GetProperty("evaluationStatus").GetString() == "FAILED"
            ? "FAILED"
            : "NOT_EVALUATED";
        AssertEqual(expectedDeployment, orchestration.GetProperty("deployment").GetString());
        AssertEqual("BLOCKED", orchestration.GetProperty("runtimeValidation").GetString());
        AssertEqual(
            errorCode.StartsWith("RIMTEST_", StringComparison.Ordinal)
                ? "RimLiaison"
                : "DevBridge2",
            orchestration.GetProperty("failure")
            .GetProperty("owner").GetString());
        AssertEqual(errorCode, orchestration.GetProperty("failure")
            .GetProperty("errorCode").GetString());
        AssertEqual(0, result.RecipeCalls.Count);
    }

    private static void ExplicitAffectedPathsTakePrecedence()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                false,
                [],
                "GIT_DISCOVERY_FAILED"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Foo.cs",
                        "Defs/Foo.xml",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(0, git.Calls.Count);
            AssertSequence(["Source/Foo.cs", "Defs/Foo.xml"], impactAdapter.ChangedPaths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void GitDiscoveryFailureIsConservative()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                false,
                [],
                "GIT_DISCOVERY_FAILED"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--run", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.InternalError, exitCode);
            AssertEqual("infrastructure", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual("blocked", root.GetProperty("selectionStatus").GetString());
            AssertEqual("GIT_DISCOVERY_FAILED", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("git status --short", root.GetProperty("nextAction").GetString());
            JsonElement orchestration = root.GetProperty("orchestration");
            AssertEqual("INFRASTRUCTURE_FAILURE", orchestration.GetProperty("overall").GetString());
            AssertEqual("UNAVAILABLE", orchestration.GetProperty("infrastructure").GetString());
            AssertEqual("NOT_EVALUATED", orchestration.GetProperty("deployment").GetString());
            AssertEqual("RimLiaison", orchestration.GetProperty("failure")
                .GetProperty("owner").GetString());
            AssertEqual("selection", orchestration.GetProperty("failure")
                .GetProperty("stage").GetString());
            AssertEqual("GIT_DISCOVERY_FAILED", orchestration.GetProperty("failure")
                .GetProperty("errorCode").GetString());
            Assert(!root.TryGetProperty("artifactFreshness", out _),
                "A selection failure must not emit a false freshness failure.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RimErrorDiagnosisProvidesNextAction()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-diagnosis",
            "RECIPE_ASSERTION_FAILED");
        CliResult result = RunCatalogCliWithAdapters(
            CreateCatalog(),
            adapter,
            new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("d-81f72")),
            "run",
            "assembler-smoke");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("fail", root.GetProperty("status").GetString());
        AssertEqual("d-81f72", root.GetProperty("diagnostic").GetProperty("id").GetString());
        AssertEqual("rimerror show d-81f72", root.GetProperty("nextAction").GetString());
    }

    private static void DevBridgeFailureProvidesNextAction()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = InfrastructureRun(
            "assembler-fixture",
            "DEVBRIDGE_REFUSAL");
        CliResult result = RunCatalogCliWithAdapter(
            CreateCatalog(),
            adapter,
            "run",
            "assembler-smoke");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual("DEVBRIDGE_REFUSAL", root.GetProperty("errorCode").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void RimContextStaleProvidesNextAction()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unavailable,
                    "INDEX_NOT_FOUND"),
                [],
                [],
                false));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Foo.cs",
                        "--json",
                        "--fallback-suite",
                        "missing",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, exitCode);
            AssertEqual("blocked", root.GetProperty("status").GetString());
            AssertEqual("CONTEXT_STALE", root.GetProperty("errorCode").GetString());
            AssertEqual("rimliaison affected --run --json", root.GetProperty("nextAction").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunCapabilitiesFixture(
        string response,
        params string[] options)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport((_, _) => ProcessResult(response));
            var arguments = new List<string>
            {
                "capabilities",
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            arguments.AddRange(options);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunUiFixture(
        string operation,
        IReadOnlyList<string>? options = null,
        string? toolsResponse = null,
        string? targetResponse = null,
        string? targetScreenshotResponse = null,
        string? cellScreenshotResponse = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(toolsResponse ?? UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(targetResponse ?? UiTargetsCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/take_screenshot",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            targetScreenshotResponse ?? UiTargetScreenshotCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/screenshot_cell_rect",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            cellScreenshotResponse ?? UiCellScreenshotCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected DevBridge UI request: " +
                        string.Join(" ", request.Arguments));
                });
            var arguments = new List<string>
            {
                "ui",
                operation,
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            if (options is not null)
            {
                arguments.AddRange(options);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunTransactionalUiFixture(
        IReadOnlyList<string> options,
        string? restoreResponse = null,
        string? screenshotResponse = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            "{\"success\":true,\"exitCode\":0,\"leaseId\":\"lease-1\",\"generation\":1}");
                    }

                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult("{\"success\":true,\"exitCode\":0}");
                    }

                    if (request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(ViewportBeginResponse());
                    }

                    if (request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(restoreResponse ?? ViewportRestoreResponse());
                    }

                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiTargetsCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/take_screenshot",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(screenshotResponse ?? UiTargetScreenshotCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected transactional UI request: " +
                        string.Join(" ", request.Arguments));
                });
            var arguments = new List<string>
            {
                "ui",
                "screenshot",
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            arguments.AddRange(options);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static string UiToolsResponse() =>
        """
        {
          "success": true,
          "rimBridgeRoute": {
            "success": true,
            "result": {
              "tools": [
                {
                  "id": "rimworld/get_screen_targets",
                  "title": "Visible screen targets",
                  "summary": "Inspect visible screen and UI targets",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "waitForVisualReady", "type": "boolean" }
                  ]
                },
                {
                  "id": "rimworld/take_screenshot",
                  "title": "Targeted screenshot",
                  "summary": "Capture a screenshot clipped to a visible UI target",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "targetId", "type": "string", "required": true },
                    { "name": "clipPadding", "type": "integer" },
                    { "name": "includeScreenTargets", "type": "boolean" },
                    { "name": "suppressMessage", "type": "boolean" },
                    { "name": "waitForVisualReady", "type": "boolean" },
                    { "name": "doNotResetCamera", "type": "boolean" }
                  ]
                },
                {
                  "id": "rimworld/screenshot_cell_rect",
                  "title": "Cell-region screenshot",
                  "summary": "Capture a screenshot of a map cell rectangle",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "x", "type": "integer", "required": true },
                    { "name": "z", "type": "integer", "required": true },
                    { "name": "width", "type": "integer", "required": true },
                    { "name": "height", "type": "integer", "required": true },
                    { "name": "paddingCells", "type": "integer" },
                    { "name": "waitForVisualReady", "type": "boolean" },
                    { "name": "doNotResetCamera", "type": "boolean" }
                  ]
                }
              ]
            }
          }
        }
        """;

    private static string UiTargetsCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "targets": [
                {
                  "id": "window:main",
                  "kind": "window",
                  "label": "Main window",
                  "rect": { "x": 10, "y": 20, "width": 2, "height": 2 }
                },
                {
                  "id": "menu:context",
                  "kind": "context-menu",
                  "label": "Context menu",
                  "rect": { "x": 30, "y": 40, "width": 3, "height": 3 }
                }
              ]
            }
            """,
            "op-targets");

    private static string UiTargetScreenshotCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "path": "/evidence/main.png",
              "clipTargetId": "window:main",
              "clipTargetKind": "window",
              "clipTargetLabel": "Main window",
              "clipRect": { "x": 10, "y": 20, "width": 2, "height": 2 },
              "cameraRestored": true,
              "capturedAtUtc": "2026-08-17T00:00:00Z"
            }
            """,
            "op-target-shot",
            "evidence-target-shot");

    private static string UiCellScreenshotCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "path": "/evidence/cell.png",
              "requestedRect": { "x": 10, "z": 20, "width": 3, "height": 4 },
              "paddedRect": { "x": 9, "z": 19, "width": 5, "height": 6 },
              "cameraRestored": true,
              "capturedAtUtc": "2026-08-17T00:00:00Z"
            }
            """,
            "op-cell-shot",
            "evidence-cell-shot");

    private static string ViewportBeginResponse() =>
        "{" +
        "\"success\":true,\"exitCode\":0,\"viewport\":" +
        "{\"schemaVersion\":\"devbridge-viewport-environment/v1\",\"success\":true," +
        "\"status\":\"prepared\",\"transactionId\":\"viewport-1\",\"leaseId\":\"lease-1\"," +
        "\"generation\":1,\"requested\":{\"kind\":\"narrow\",\"width\":1024,\"height\":768}," +
        "\"capturedState\":{\"clientWidth\":1920,\"clientHeight\":1080,\"windowHandle\":7001}," +
        "\"effectiveViewport\":{\"clientWidth\":1024,\"clientHeight\":768,\"windowHandle\":7001}," +
        "\"persistentPreferenceMutation\":false,\"restorationVerified\":false}" +
        "}";

    private static string ViewportRestoreResponse() =>
        "{" +
        "\"success\":true,\"exitCode\":0,\"viewport\":" +
        "{\"schemaVersion\":\"devbridge-viewport-environment/v1\",\"success\":true," +
        "\"status\":\"restored\",\"transactionId\":\"viewport-1\",\"leaseId\":\"lease-1\"," +
        "\"generation\":1,\"restoredViewport\":{\"clientWidth\":1920,\"clientHeight\":1080,\"windowHandle\":7001}," +
        "\"persistentPreferenceMutation\":false,\"restorationVerified\":true,\"cleanupStatus\":\"restored\"}" +
        "}";

    private static string RouteResponse(
        string result,
        string? operationId = null,
        string? evidenceId = null)
    {
        string operation = operationId is null
            ? string.Empty
            : $"\"operationId\":{JsonSerializer.Serialize(operationId)},";
        string evidence = evidenceId is null
            ? string.Empty
            : $"\"evidenceId\":{JsonSerializer.Serialize(evidenceId)},";
        return $"{{\"success\":true,\"rimBridgeRoute\":{{\"success\":true,{operation}{evidence}\"result\":{result}}}}}";
    }

    private static CliResult RunDoctorFixture(
        bool contextAvailable,
        bool useExplicitOverrides = false,
        bool usePascalRimBridgeFields = false)
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            string manifestCatalog = useExplicitOverrides ? "missing.json" : "catalog.json";
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "stack.json"),
                $"{{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"FixtureMod\",\"devBridgeProject\":\"fixture\",\"catalog\":\"{manifestCatalog}\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}}");
            string overrideCatalogPath = Path.Combine(directory, "override.json");
            if (useExplicitOverrides)
            {
                File.WriteAllText(overrideCatalogPath, Serialize(CreateCatalog()));
            }
            if (contextAvailable)
            {
                new RimContextService().Index(
                    new RimContextIndexRequest(directory));
            }
            string rimContextPath = Path.Combine(directory, "rimctx.cmd");
            string devBridgePath = Path.Combine(directory, "DevBridge.cmd");
            string rimErrorPath = Path.Combine(directory, "rimerror.cmd");
            string rimBridge = usePascalRimBridgeFields
                ? "{\"ConfiguredMode\":\"required\",\"LifecycleState\":\"READY\"}"
                : "{\"configuredMode\":\"optional\",\"lifecycleState\":\"READY\"}";
            File.WriteAllText(rimContextPath, "fixture");
            File.WriteAllText(devBridgePath, "fixture");
            File.WriteAllText(rimErrorPath, "fixture");
            var transport = new FakeTransport(
                (request, _) => request.Arguments.Contains("summary")
                    ? ProcessResult(contextAvailable
                        ? "{\"schemaVersion\":\"rimctx/v1\",\"status\":\"ok\",\"command\":\"summary\",\"data\":{}}"
                        : "{\"schemaVersion\":\"rimctx/v1\",\"status\":\"error\",\"command\":\"summary\",\"code\":\"INDEX_NOT_FOUND\",\"message\":\"missing\"}")
                    : request.Arguments.Contains("project")
                        ? ProcessResult($"{{\"success\":true,\"projectResolution\":{{\"canonicalProjects\":[\"{request.Arguments[4]}\"]}}}}")
                    : ProcessResult(
                        $"{{\"success\":true,\"healthy\":true,\"rimBridge\":{rimBridge}}}"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var arguments = new List<string>
            {
                "doctor",
                "--json",
                "--rimcontext",
                rimContextPath,
                "--rimcontext-root",
                directory,
                "--devbridge",
                devBridgePath,
                "--devbridge-root",
                directory,
                "--rimerror",
                rimErrorPath
            };
            if (useExplicitOverrides)
            {
                arguments.Add("--catalog");
                arguments.Add(overrideCatalogPath);
                arguments.Add("--devbridge-project");
                arguments.Add("override");
            }

            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunManifestOnlyDoctor(string? manifest)
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            if (manifest is not null)
            {
                Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
                File.WriteAllText(Path.Combine(directory, ".rimdev", "stack.json"), manifest);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        ["doctor", "--json"],
                        stdout,
                        stderr)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunManifestOnlyDoctorWithCatalog(
        string manifest,
        string catalog)
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(Path.Combine(directory, ".rimdev", "stack.json"), manifest);
            File.WriteAllText(Path.Combine(directory, "catalog.json"), catalog);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        ["doctor", "--json"],
                        stdout,
                        stderr)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunInitFixture(
        string directory,
        params string[] options)
    {
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] arguments = ["init", "--json", .. options];
        int exitCode = WithCurrentDirectory(
            directory,
            () => CliApplication.RunAsync(
                    arguments,
                    stdout,
                    stderr)
                .GetAwaiter()
                .GetResult());
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static T WithFallbackSuiteEnvironment<T>(
        string? value,
        Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable("RIMTEST_FALLBACK_SUITE");
        try
        {
            Environment.SetEnvironmentVariable("RIMTEST_FALLBACK_SUITE", value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIMTEST_FALLBACK_SUITE", previous);
        }
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        process.WaitForExit();
        AssertEqual(0, process.ExitCode);
    }

    private static T WithCurrentDirectory<T>(string directory, Func<T> action)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static void DeleteDirectoryIncludingReadOnlyFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    private static RimErrorDiagnosisResult AvailableDiagnosis(string id) =>
        new(
            RimErrorDiagnosisOutcome.Available,
            new RimErrorAdapterStatus(RimErrorDiagnosisOutcome.Available),
            new RimErrorDiagnosticSummary
            {
                Id = id,
                Category = "runtime"
            },
            "fail");

    private static DevBridgeDiagnosticSourceResult AvailableSource(int generation) =>
        new(
            new DevBridgeDiagnosticSourceStatus(
                DevBridgeDiagnosticSourceOutcome.Available),
            new DevBridgeScopedDiagnosticSource(
                DevBridgeDiagnosticSchemas.ScopedSource,
                generation,
                "[RimWorld] controlled failure\n",
                System.Text.Encoding.UTF8.GetByteCount("[RimWorld] controlled failure\n"),
                1,
                false,
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("[RimWorld] controlled failure\n")))
                    .ToLowerInvariant()));

    private static DevBridgeRecipeRunResult PassRun(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            7,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult PassRunWithGeneration(
        string recipeId,
        int generation) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            generation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult PassRunWithIdentity(
        string recipeId,
        int generation,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            generation,
            "lease-00000000000000000000000000000001",
            null,
            null,
            null,
            null,
            null,
            null,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                true,
                null,
                [],
                operationId,
                null,
                generation,
                "launch-1")]);

    private static DevBridgeModDevelopmentResult SuccessfulDevelopmentResult(
        string sourceFingerprint,
        string? workflowId,
        string deploymentDecision,
        int generation = 7,
        bool loadedArtifactFreshnessProven = true,
        string? errorCode = null)
    {
        int generationBefore = deploymentDecision == "unchanged"
            ? generation
            : Math.Max(0, generation - 1);
        return new DevBridgeModDevelopmentResult(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Success,
                errorCode),
            true,
            "tx-1",
            workflowId,
            generation,
            "lease-00000000000000000000000000000001",
            new DevBridgeArtifactFreshness(
                sourceFingerprint,
                new string('b', 64),
                new string('b', 64),
                deploymentDecision,
                generationBefore,
                generation,
                generation,
                loadedArtifactFreshnessProven,
                deploymentDecision == "unchanged"
                    ? "identical-deployment-hash-plus-owned-generation-state"
                    : "deployment-hash-plus-new-owned-generation",
                "tx-1",
                workflowId,
                "lease-00000000000000000000000000000001",
                errorCode));
    }

    private static DevBridgeModDevelopmentResult FailedDevelopmentResult(
        string? workflowId,
        string errorCode) =>
        new(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode,
                "simulated owner failure"),
            false,
            "tx-failed",
            workflowId,
            null,
            null,
            null);

    private static AffectedScenarioResult RunAffectedSourceScenario(
        Func<string, string?, DevBridgeModDevelopmentResult>? resultFactory = null,
        DevBridgeRecipeRunResult? recipeRun = null,
        bool failFast = false,
        CatalogDocument? scenarioCatalog = null,
        Func<string, int, DevBridgeRecipeRunResult>? recipeRunFactory = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Changed.cs"),
                "class Changed { int Value = 1; }\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(scenarioCatalog ?? CreateCatalog()));

            var recipeAdapter = new FakeRecipeAdapter
            {
                RunFactory = recipeRunFactory is null
                    ? null
                    : (recipeId, _, _, index) => recipeRunFactory(recipeId, index)
            };
            recipeAdapter.Runs["assembler-fixture"] =
                recipeRun ?? PassRun("assembler-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter
            {
                Factory = resultFactory
            };
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var arguments = new List<string>
            {
                "affected",
                "Source/Changed.cs",
                "--run"
            };
            if (failFast)
            {
                arguments.Add("--fail-fast");
            }

            arguments.AddRange(
                [
                    "--json",
                    "--devbridge-project",
                    "fixture",
                    "--catalog",
                    catalogPath
                ]);

            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        recipeAdapter,
                        impactAdapter: impactAdapter,
                        developmentAdapter: developmentAdapter)
                    .GetAwaiter()
                    .GetResult());

            return new AffectedScenarioResult(
                exitCode,
                stdout.ToString(),
                stderr.ToString(),
                developmentAdapter.Calls.ToArray(),
                recipeAdapter.RunCalls.ToArray());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static DevBridgeRecipeRunResult FailedRun(
        string recipeId,
        string fingerprint,
        string errorCode,
        int generation = 1,
        string? workflowId = null,
        IReadOnlyList<DevBridgeOperationSummary>? operations = null) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.TestFailure,
                errorCode),
            false,
            "run-" + recipeId,
            generation,
            null,
            null,
            "evidence-" + recipeId,
            fingerprint,
            null,
            null,
            null,
            operations ?? [],
            workflowId);

    private static DevBridgeRecipeRunResult InfrastructureRun(
        string recipeId,
        string errorCode) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult CancelledRun(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Cancelled,
                "RIMTEST_CANCELLED"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipePlanResult SuccessfulPlan(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            new DevBridgeRecipePlan(
                recipeId,
                false,
                0,
                [],
                null,
                []));

    private static DevBridgeRecipePlanResult SatisfiedPlan(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            new DevBridgeRecipePlan(
                recipeId,
                true,
                0,
                [],
                "none",
                []));

    private static CatalogTest ReusableTest(string id, string recipeId) =>
        ReusableTestWithKey(id, recipeId, "fixture-ready");

    private static CatalogTest ReusableTestWithKey(
        string id,
        string recipeId,
        string reuseKey,
        CatalogRecipeIsolationMode mode = CatalogRecipeIsolationMode.PureRead,
        string? resetRecipe = null) =>
        new()
        {
            Id = id,
            Recipe = recipeId,
            Isolation = new CatalogRecipeIsolation
            {
                Mode = mode,
                ReuseKey = reuseKey,
                ResetRecipe = resetRecipe
            }
        };

    private static CatalogSuiteRecipeProfile RecipeProfile(string signature) =>
        new(signature, [], []);

    private static void SetRecipeProfile(
        FakeRecipeAdapter adapter,
        string recipeId,
        IReadOnlyList<string> projects)
    {
        string projectJson = string.Join(",", projects.Select(project =>
            "\"" + project + "\""));
        using JsonDocument definition = JsonDocument.Parse(
            "{\"projects\":[" + projectJson + "],\"inputs\":{\"quicktest\":true}}");
        adapter.ShowDefinitions[recipeId] = definition.RootElement.Clone();
    }

    private static CatalogTest ResettableTest(string id, string recipeId) =>
        new()
        {
            Id = id,
            Recipe = recipeId,
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "fixture-resettable",
                ResetRecipe = "fixture-reset"
            }
        };

    private static CatalogDocument CreateIsolationCatalog(params CatalogTest[] tests) =>
        new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests = tests.ToList(),
            Suites = []
        };

    private static DevBridgeLeaseResult SuccessLease(string leaseId, int generation) =>
        new(
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            leaseId,
            generation);

    private static DevBridgeResetResult SuccessfulReset(string leaseId, int generation) =>
        new(
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            generation,
            leaseId);

    private static DevBridgeRecipeRunResult PassRunWithLease(
        string recipeId,
        int generation,
        string? leaseId,
        string? workflowId,
        string runId,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            runId,
            generation,
            leaseId,
            null,
            null,
            null,
            null,
            false,
            0,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                true,
                null,
                [],
                operationId,
                workflowId,
                generation,
                "launch-" + runId)],
            workflowId);

    private static DevBridgeRecipeRunResult LeaseRequiredRun(
        string recipeId,
        string? workflowId,
        string? leaseId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "RIMBRIDGE_LEASE_REQUIRED"),
            false,
            "run-lease-required",
            7,
            leaseId,
            null,
            null,
            null,
            null,
            null,
            0,
            [],
            workflowId);

    private static DevBridgeRecipeRunResult FailedRunWithLease(
        string recipeId,
        int generation,
        string? leaseId,
        string? workflowId,
        string runId,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.TestFailure,
                "RECIPE_ASSERTION_FAILED"),
            false,
            runId,
            generation,
            leaseId,
            null,
            "evidence-" + runId,
            "failure-" + runId,
            null,
            false,
            0,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                false,
                "RECIPE_ASSERTION_FAILED",
                [],
                operationId,
                workflowId,
                generation,
                "launch-" + runId)],
            workflowId);

    private static CatalogDocument CreateCatalog()
    {
        return new CatalogDocument
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "settings-smoke",
                    Recipe = "settings-fixture",
                    Cost = CatalogCost.Medium,
                    Tags = ["settings"],
                    Covers = [new CatalogCoverage { Kind = "feature", Name = "settings" }]
                },
                new CatalogTest
                {
                    Id = "assembler-smoke",
                    Recipe = "assembler-fixture",
                    Cost = CatalogCost.Low,
                    Description = "Checks assembler registration.",
                    Tags = ["crafting", "assembler"],
                    Covers =
                    [
                        new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" },
                        new CatalogCoverage { Kind = "csharp_type", Name = "CompAssembler" }
                    ]
                }
            ],
            Suites =
            [
                new CatalogSuite
                {
                    Id = "smoke",
                    Tests = ["assembler-smoke"],
                    Suites = ["settings"]
                },
                new CatalogSuite
                {
                    Id = "settings",
                    Tests = ["settings-smoke"]
                }
            ]
        };
    }

    private static CliResult RunCli(
        CatalogDocument catalog,
        params string[] command)
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(catalog));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] args = command
                .Concat(["--catalog", catalogPath])
                .ToArray();
            int exitCode = CliApplication.Run(args, stdout, stderr);
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CliResult RunCatalogCliWithAdapter(
        CatalogDocument catalog,
        IDevBridgeRecipeAdapter recipeAdapter,
        params string[] command)
    {
        return RunCatalogCliWithAdapters(
            catalog,
            recipeAdapter,
            null,
            command);
    }

    private static CliResult RunCatalogCliWithAdapters(
        CatalogDocument catalog,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        params string[] command)
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(catalog));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] args = command
                .Concat(["--catalog", catalogPath])
                .ToArray();
            int exitCode = diagnosisAdapter is null
                ? CliApplication.Run(args, stdout, stderr, recipeAdapter)
                : CliApplication.Run(
                    args,
                    stdout,
                    stderr,
                    recipeAdapter,
                    diagnosisAdapter);
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
                "rimliaison-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Serialize(CatalogDocument catalog)
    {
        return JsonSerializer.Serialize(
            catalog,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters =
                {
                    new JsonStringEnumConverter(
                        JsonNamingPolicy.CamelCase,
                        allowIntegerValues: false)
                }
            });
    }

    private static DevBridgeRecipeAdapter CreateAdapter(FakeTransport transport)
    {
        return new DevBridgeRecipeAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1),
                RunTimeout = TimeSpan.FromSeconds(1)
            });
    }

    private static DevBridgeProcessResult ProcessResult(
        string stdout,
        int exitCode = 0)
    {
        return new DevBridgeProcessResult(exitCode, stdout, string.Empty);
    }

    private static void AssertHasCode(
        IEnumerable<CatalogIssue> errors,
        string code)
    {
        Assert(errors.Any(error => error.Code == code), $"Expected error code {code}.");
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        AssertEqual(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertEqual(expected[index], actual[index]);
        }
    }

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
            throw new InvalidOperationException(
                $"Expected {expected}; got {actual}.");
        }
    }

    private sealed class FakeTransport : IDevBridgeProcessTransport
    {
        private readonly Func<
            DevBridgeProcessRequest,
            CancellationToken,
            DevBridgeProcessResult> handler;

        public FakeTransport(
            Func<
                DevBridgeProcessRequest,
                CancellationToken,
                DevBridgeProcessResult> handler)
        {
            this.handler = handler;
        }

        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request, cancellationToken));
        }
    }

    private sealed class FakeRecipeAdapter : IDevBridgeRecipeAdapter
    {
        public Dictionary<string, DevBridgeRecipeRunResult> Runs { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, DevBridgeRecipePlanResult> Plans { get; } =
            new(StringComparer.Ordinal);

        public List<string> PlanCalls { get; } = [];

        public List<string> RunCalls { get; } = [];

        public List<DevBridgeRecipeExecutionContext?> ExecutionContexts { get; } = [];

        public List<DevBridgeRecipeRunResult> RunResults { get; } = [];

        public Dictionary<string, JsonElement> ShowDefinitions { get; } =
            new(StringComparer.Ordinal);

        public Func<
            string,
            string?,
            DevBridgeRecipeExecutionContext?,
            int,
            DevBridgeRecipeRunResult>? RunFactory
        { get; init; }

        public Task<DevBridgeRecipeShowResult> ShowAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            JsonElement definition;
            if (ShowDefinitions.TryGetValue(recipeId, out JsonElement configured))
            {
                definition = configured.Clone();
            }
            else
            {
                using JsonDocument document = JsonDocument.Parse(
                    "{\"projects\":[],\"inputs\":{}}");
                definition = document.RootElement.Clone();
            }

            return Task.FromResult(new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                definition));
        }

        public Task<DevBridgeRecipePlanResult> PlanAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            PlanCalls.Add(recipeId);
            return Task.FromResult(
                Plans.TryGetValue(recipeId, out DevBridgeRecipePlanResult? plan)
                    ? plan
                    : SuccessfulPlan(recipeId));
        }

        public Task<DevBridgeRecipeRunResult> RunAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(recipeId, null, null, cancellationToken);
        }

        public Task<DevBridgeRecipeRunResult> RunAsync(
            string recipeId,
            string? workflowId,
            DevBridgeRecipeExecutionContext? executionContext,
            CancellationToken cancellationToken = default)
        {
            RunCalls.Add(recipeId);
            ExecutionContexts.Add(executionContext);
            int index = RunResults.Count;
            DevBridgeRecipeRunResult result = RunFactory is not null
                ? RunFactory(recipeId, workflowId, executionContext, index)
                : Runs.TryGetValue(recipeId, out DevBridgeRecipeRunResult? configured)
                    ? configured
                    : InfrastructureRun(recipeId, "FAKE_RECIPE_NOT_CONFIGURED");
            RunResults.Add(result);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLeaseAdapter : IDevBridgeLeaseAdapter
    {
        public Queue<DevBridgeLeaseResult> BeginResults { get; } = [];

        public Queue<DevBridgeLeaseResult> RenewResults { get; } = [];

        public DevBridgeLeaseResult BeginResult { get; init; } = SuccessLease("lease-default", 7);

        public DevBridgeLeaseResult? EndResult { get; set; }

        public int BeginCalls { get; private set; }

        public int RenewCalls { get; private set; }

        public int EndCalls { get; private set; }

        private int CurrentGeneration { get; set; } = 7;

        public Task<DevBridgeLeaseResult> BeginLeaseAsync(
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            DevBridgeLeaseResult result = BeginResults.Count > 0
                ? BeginResults.Dequeue()
                : BeginResult;
            if (result.Generation is > 0)
            {
                CurrentGeneration = result.Generation.Value;
            }

            return Task.FromResult(result);
        }

        public Task<DevBridgeLeaseResult> RenewLeaseAsync(
            string leaseId,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            return Task.FromResult(
                RenewResults.Count > 0
                    ? RenewResults.Dequeue()
                    : SuccessLease(leaseId, CurrentGeneration));
        }

        public Task<DevBridgeLeaseResult> EndLeaseAsync(
            string leaseId,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            EndCalls++;
            return Task.FromResult(EndResult ?? SuccessLease(leaseId, CurrentGeneration));
        }
    }

    private sealed record ResetCall(
        string RecipeId,
        string LeaseId,
        int Generation,
        string? WorkflowId);

    private sealed class FakeResetAdapter : IDevBridgeFixtureResetAdapter
    {
        public List<ResetCall> Calls { get; } = [];

        public DevBridgeResetResult Result { get; init; } =
            SuccessfulReset("lease-default", 7);

        public Task<DevBridgeResetResult> ResetAsync(
            string resetRecipeId,
            string leaseId,
            int expectedGeneration,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ResetCall(resetRecipeId, leaseId, expectedGeneration, workflowId));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeFreshGenerationAdapter : IDevBridgeFreshGenerationAdapter
    {
        private readonly Queue<int> generations;

        public FakeFreshGenerationAdapter(params int[] generations)
        {
            this.generations = new Queue<int>(generations);
        }

        public List<(string RecipeId, int? PreviousGeneration, string? WorkflowId)> Calls { get; } = [];

        public Task<DevBridgeFreshGenerationResult> EnsureFreshGenerationAsync(
            string recipeId,
            int? previousGeneration,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((recipeId, previousGeneration, workflowId));
            if (generations.Count == 0)
            {
                return Task.FromResult(new DevBridgeFreshGenerationResult(
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "FAKE_FRESH_GENERATION_EXHAUSTED"),
                    null));
            }

            return Task.FromResult(new DevBridgeFreshGenerationResult(
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                generations.Dequeue(),
                1));
        }
    }

    private sealed class FakeModDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
    {
        public List<(string Project, string SourceFingerprint, string? WorkflowId)> Calls { get; } = [];

        public DevBridgeModDevelopmentResult? Result { get; set; }

        public Func<string, string?, DevBridgeModDevelopmentResult>? Factory { get; set; }

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((project, sourceFingerprint, workflowId));
            if (Factory is not null)
            {
                return Task.FromResult(Factory(sourceFingerprint, workflowId));
            }

            if (Result is not null)
            {
                return Task.FromResult(Result);
            }

            return Task.FromResult(
                new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                    true,
                    "tx-1",
                    workflowId,
                    7,
                    "lease-00000000000000000000000000000001",
                    new DevBridgeArtifactFreshness(
                        sourceFingerprint,
                        new string('a', 64),
                        new string('a', 64),
                        "unchanged",
                        7,
                        7,
                        7,
                        true,
                        "test-proof",
                        "tx-1",
                        workflowId,
                        "lease-00000000000000000000000000000001")));
        }
    }

    private sealed record AffectedScenarioResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        IReadOnlyList<(string Project, string SourceFingerprint, string? WorkflowId)> DevelopmentCalls,
        IReadOnlyList<string> RecipeCalls);

    private sealed class FakeImpactAdapter : IRimContextImpactAdapter
    {
        private readonly RimContextImpactResult result;

        public FakeImpactAdapter(RimContextImpactResult result)
        {
            this.result = result;
        }

        public IReadOnlyList<string> ChangedPaths { get; private set; } = [];

        public Task<RimContextImpactResult> AffectedAsync(
            IReadOnlyList<string> changedPaths,
            CancellationToken cancellationToken = default) =>
            RecordAndReturn(changedPaths);

        private Task<RimContextImpactResult> RecordAndReturn(
            IReadOnlyList<string> changedPaths)
        {
            ChangedPaths = changedPaths.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeGitChangeProvider : IGitChangeProvider
    {
        private readonly GitChangeDiscoveryResult result;

        public FakeGitChangeProvider(GitChangeDiscoveryResult result)
        {
            this.result = result;
        }

        public List<(string Root, string? Base)> Calls { get; } = [];

        public Task<GitChangeDiscoveryResult> DiscoverAsync(
            string rootPath,
            string? baseReference = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((rootPath, baseReference));
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRimErrorDiagnosisAdapter : IRimErrorDiagnosisAdapter
    {
        private readonly RimErrorDiagnosisResult result;

        public FakeRimErrorDiagnosisAdapter(RimErrorDiagnosisResult result)
        {
            this.result = result;
        }

        public int Calls { get; private set; }

        public RimErrorDiagnosisRequest? Request { get; private set; }

        public RimErrorDiagnosisResult Result => result;

        public Task<RimErrorDiagnosisResult> DiagnoseAsync(
            RimErrorDiagnosisRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeDiagnosticSourceAdapter : IDevBridgeDiagnosticSourceAdapter
    {
        private readonly DevBridgeDiagnosticSourceResult result;

        public FakeDiagnosticSourceAdapter(DevBridgeDiagnosticSourceResult result)
        {
            this.result = result;
        }

        public int Calls { get; private set; }

        public string? TestId { get; private set; }

        public string? RunId { get; private set; }

        public Task<DevBridgeDiagnosticSourceResult> AcquireAsync(
            string testId,
            DevBridgeRecipeRunResult run,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            TestId = testId;
            RunId = run.RunId;
            return Task.FromResult(result);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
