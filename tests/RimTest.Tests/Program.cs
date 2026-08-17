using System.Text.Json;
using System.Text.Json.Serialization;
using RimTest;
using RimTest.Catalog;
using RimTest.DevBridge;
using RimTest.Execution;
using RimTest.Git;
using RimTest.RimError;
using RimTest.RimContext;
using RimTest.Results;

namespace RimTest.Tests;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("valid catalog", ValidCatalogLoads),
        ("duplicate ids fail", DuplicateIdsFail),
        ("missing references fail", MissingReferencesFail),
        ("suite cycles fail", SuiteCyclesFail),
        ("missing recipes fail", MissingRecipesFail),
        ("recipe list loads", RecipeListLoads),
        ("list is minimal and sorted", ListIsMinimalAndSorted),
        ("show exposes metadata", ShowExposesMetadata),
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
        ("RimError diagnosis is normalized", RimErrorDiagnosisIsNormalized),
        ("RimError unavailable degrades", RimErrorUnavailableDegrades),
        ("RimError timeout degrades", RimErrorTimeoutDegrades),
        ("RimError malformed response degrades", RimErrorMalformedResponseDegrades),
        ("RimError incompatible response degrades", RimErrorIncompatibleResponseDegrades),
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
        ("RimContext no impact selects no tests", RimContextNoImpactSelectsNoTests),
        ("RimContext unknown impact uses fallback", RimContextUnknownImpactUsesFallback),
        ("RimContext unavailable is conservative", RimContextUnavailableIsConservative),
        ("RimContext selection ordering is deterministic", RimContextSelectionOrderingIsDeterministic),
        ("RimContext adapter parses affected v1", RimContextAdapterParsesAffectedV1),
        ("RimContext refreshes before affected", RimContextAdapterRefreshesBeforeAffected),
        ("RimContext partial refresh is conservative", RimContextPartialRefreshIsConservative),
        ("affected CLI emits compact selection", AffectedCliEmitsCompactSelection),
        ("affected run pass is compact", AffectedRunPassIsCompact),
        ("suite all-pass aggregation is compact", SuiteAllPassAggregationIsCompact),
        ("suite one failure is summarized", SuiteOneFailureIsSummarized),
        ("suite multiple failures are deterministic", SuiteMultipleFailuresAreDeterministic),
        ("suite cancellation stops new children", SuiteCancellationStopsNewChildren),
        ("suite duplicate tests execute once", SuiteDuplicateTestsExecuteOnce),
        ("suite plan refusal blocks execution", SuitePlanRefusalBlocksExecution),
        ("suite child infrastructure failure is summarized", SuiteChildInfrastructureFailureIsSummarized),
        ("affected run uses conservative fallback", AffectedRunUsesConservativeFallback),
        ("suite run CLI is deterministic", SuiteRunCliIsDeterministic),
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
        ("init preserves existing AGENTS", InitPreservesExistingAgents),
        ("init preserves existing manifest", InitPreservesExistingManifest),
        ("affected discovers Git changes without paths", AffectedDiscoversGitChangesWithoutPaths),
        ("clean affected run is explicit and does not launch", CleanAffectedRunIsExplicitAndDoesNotLaunch),
        ("Git discovery includes staged and untracked files", GitDiscoveryIncludesStagedAndUntrackedFiles),
        ("explicit affected paths take precedence", ExplicitAffectedPathsTakePrecedence),
        ("Git discovery failure is conservative", GitDiscoveryFailureIsConservative),
        ("RimError diagnosis provides drill-down next action", RimErrorDiagnosisProvidesNextAction),
        ("DevBridge failure provides doctor next action", DevBridgeFailureProvidesNextAction),
        ("RimContext stale result provides recovery next action", RimContextStaleProvidesNextAction)
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
                    "RimTest did not pass workflowId to DevBridge.");
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
            "RimTest final output did not expose the workflow id.");
        Assert(!root.TryGetProperty("operations", out _),
            "RimTest final output embedded operation telemetry.");
        Assert(!result.Stdout.Contains("Player.log", StringComparison.Ordinal),
            "RimTest final output embedded log content.");
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
        bool integrationVerified = false;
        var transport = new FakeTransport(
            (request, _) =>
            {
                if (request.Arguments[0] == "ingest")
                {
                    int integrationIndex = request.Arguments
                        .ToList()
                        .IndexOf("--integration");
                    string integrationPath = request.Arguments[integrationIndex + 1];
                    string integration = File.ReadAllText(integrationPath);
                    Assert(integration.Contains(
                        "\"runId\":\"run-7\"",
                        StringComparison.Ordinal), "Run id was not passed to RimError.");
                    Assert(integration.Contains(
                        "\"generation\":7",
                        StringComparison.Ordinal), "Generation was not passed to RimError.");
                    Assert(integration.Contains(
                        "\"evidence\":\"evidence-7\"",
                        StringComparison.Ordinal), "Evidence was not passed to RimError.");
                    Assert(integration.Contains(
                        "\"workflowId\":\"rw-diagnosis-7\"",
                        StringComparison.Ordinal), "Workflow id was not passed to RimError.");
                    Assert(integration.Contains(
                        "\"operationId\":\"op-7\"",
                        StringComparison.Ordinal), "RimBridge operation metadata was not passed to RimError.");
                    integrationVerified = true;
                    return ProcessResult(
                        "{\"status\":\"fail\",\"errors\":1,\"warnings\":0}",
                        exitCode: 1);
                }

                return ProcessResult(
                    """
                    {
                      "status": "fail",
                      "errors": 1,
                      "warnings": 0,
                      "rootCauses": [
                        {
                          "id": "RE-81F72",
                          "category": "runtime",
                          "method": "CCM.CompAssembler.Tick",
                          "source": "Source/Comps/CompAssembler.cs",
                          "line": 131,
                          "confidence": "high",
                          "count": 20
                        }
                      ]
                    }
                    """);
            });

        RimErrorDiagnosisResult result = CreateRimErrorAdapter(transport)
            .DiagnoseAsync(
                new RimErrorDiagnosisRequest(
                    "assembler-smoke",
                    "run-7",
                    7,
                    "evidence-7",
                    "fp-7",
                    "RECIPE_ASSERTION_FAILED",
                    "rw-diagnosis-7",
                    [new RimErrorOperationCorrelation(
                        "op-7",
                        "rimworld/fixture",
                        false,
                        "RECIPE_ASSERTION_FAILED",
                        "rw-diagnosis-7",
                        7,
                        "launch-7")]))
            .GetAwaiter()
            .GetResult();

        Assert(integrationVerified, "RimError integration metadata was not inspected.");
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

    private static void RimErrorUnavailableDegrades()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: "RimError was not found."));

        RimErrorDiagnosisResult result = CreateRimErrorAdapter(transport)
            .DiagnoseAsync(new RimErrorDiagnosisRequest(
                "assembler-smoke",
                "run-1",
                1,
                "evidence-1",
                "fp-1",
                "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimErrorDiagnosisOutcome.Unavailable, result.Outcome);
        AssertEqual("RIMERROR_START_FAILED", result.Status.ErrorCode);
    }

    private static void RimErrorTimeoutDegrades()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                TimedOut: true));

        RimErrorDiagnosisResult result = CreateRimErrorAdapter(transport)
            .DiagnoseAsync(new RimErrorDiagnosisRequest(
                "assembler-smoke",
                "run-1",
                1,
                "evidence-1",
                "fp-1",
                "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimErrorDiagnosisOutcome.Timeout, result.Outcome);
        AssertEqual("RIMERROR_TIMEOUT", result.Status.ErrorCode);
    }

    private static void RimErrorMalformedResponseDegrades()
    {
        int calls = 0;
        var transport = new FakeTransport(
            (_, _) =>
            {
                calls++;
                return calls == 1
                    ? ProcessResult(
                        "{\"status\":\"fail\",\"errors\":1,\"warnings\":0}",
                        exitCode: 1)
                    : ProcessResult("{");
            });

        RimErrorDiagnosisResult result = CreateRimErrorAdapter(transport)
            .DiagnoseAsync(new RimErrorDiagnosisRequest(
                "assembler-smoke",
                "run-1",
                1,
                "evidence-1",
                "fp-1",
                "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimErrorDiagnosisOutcome.MalformedResponse, result.Outcome);
        AssertEqual("RIMERROR_MALFORMED_JSON", result.Status.ErrorCode);
    }

    private static void RimErrorIncompatibleResponseDegrades()
    {
        int calls = 0;
        var transport = new FakeTransport(
            (_, _) =>
            {
                calls++;
                return calls == 1
                    ? ProcessResult(
                        "{\"status\":\"fail\",\"errors\":1,\"warnings\":0}",
                        exitCode: 1)
                    : ProcessResult(
                        "{\"schemaVersion\":\"rimerror-latest/v2\",\"status\":\"fail\",\"errors\":1,\"warnings\":0}");
            });

        RimErrorDiagnosisResult result = CreateRimErrorAdapter(transport)
            .DiagnoseAsync(new RimErrorDiagnosisRequest(
                "assembler-smoke",
                "run-1",
                1,
                "evidence-1",
                "fp-1",
                "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimErrorDiagnosisOutcome.IncompatibleSchema, result.Outcome);
        AssertEqual("RIMERROR_SCHEMA_UNSUPPORTED", result.Status.ErrorCode);
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

        AssertEqual("ok", result.Status);
        AssertEqual(0, result.Tests.Count);
        AssertEqual(0, result.ReasonCount);
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
        AssertEqual("rimctx index --json", result.NextAction);
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

    private static void RimContextAdapterParsesAffectedV1()
    {
        var transport = new FakeRimContextProcessTransport(
            new RimContextProcessResult(
                0,
                """
                {
                  "schemaVersion":"rimctx/v1",
                  "status":"ok",
                  "command":"affected",
                  "data":{
                    "changed":["Source/Base.cs"],
                    "direct":[{"kind":"csharp_type","id":"type-base","name":"Game.Base"}],
                    "dependent":[{"kind":"csharp_type","id":"type-use","name":"Game.Use","reason":"csharp_type_usage"}],
                    "runtime_risk":[{"kind":"harmony_patch","id":"patch-base","name":"BasePatch.Postfix","reason":"harmony_target"}],
                    "truncated":false
                  },
                  "meta":{"count":3,"truncated":false}
                }
                """,
                string.Empty));
        var adapter = new RimContextImpactAdapter(
            transport,
            new RimContextAdapterOptions
            {
                CommandPath = "rimctx.cmd",
                RootPath = "Workspace",
                Depth = 8,
                Limit = 100,
                Timeout = TimeSpan.FromSeconds(1)
            });

        RimContextImpactResult result = adapter.AffectedAsync(["Source/Base.cs"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
        AssertEqual(3, result.Impacts.Count);
        AssertEqual("runtimeRisk", result.Impacts[2].Tier);
        AssertEqual(2, transport.Requests.Count);
        AssertEqual("index", transport.Requests[0].Arguments[0]);
        AssertEqual("affected", transport.Requests[1].Arguments[0]);
        Assert(transport.Requests[1].Arguments.Contains("--depth"), "Depth was not forwarded.");
    }

    private static void RimContextAdapterRefreshesBeforeAffected()
    {
        var transport = new FakeRimContextProcessTransport(
            new RimContextProcessResult(
                0,
                """
                {
                  "schemaVersion":"rimctx/v1",
                  "status":"ok",
                  "command":"affected",
                  "data":{"changed":["Source/Base.cs"],"direct":[],"dependent":[],"runtime_risk":[],"truncated":false},
                  "meta":{"count":0,"truncated":false}
                }
                """,
                string.Empty));
        var adapter = new RimContextImpactAdapter(
            transport,
            new RimContextAdapterOptions
            {
                CommandPath = "rimctx.cmd",
                RootPath = "Workspace",
                Depth = 8,
                Limit = 100,
                Timeout = TimeSpan.FromSeconds(1)
            });

        RimContextImpactResult result = adapter.AffectedAsync(["Source/Base.cs"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
        AssertEqual(2, transport.Requests.Count);
        AssertEqual("index", transport.Requests[0].Arguments[0]);
        AssertEqual("affected", transport.Requests[1].Arguments[0]);
    }

    private static void RimContextPartialRefreshIsConservative()
    {
        var transport = new FakeRimContextProcessTransport(
            new RimContextProcessResult(
                0,
                "{}",
                string.Empty),
            new RimContextProcessResult(
                0,
                """
                {
                  "schemaVersion":"rimctx/v1",
                  "status":"partial",
                  "command":"index",
                  "data":{"files":{"scanned":1}}
                }
                """,
                string.Empty));
        var adapter = new RimContextImpactAdapter(
            transport,
            new RimContextAdapterOptions
            {
                CommandPath = "rimctx.cmd",
                RootPath = "Workspace",
                Depth = 8,
                Limit = 100,
                Timeout = TimeSpan.FromSeconds(1)
            });

        RimContextImpactResult result = adapter.AffectedAsync(["Source/Base.cs"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(RimContextImpactOutcome.Unknown, result.Status.Outcome);
        AssertEqual("RIMCONTEXT_INDEX_PARTIAL", result.Status.ErrorCode);
        AssertEqual(1, transport.Requests.Count);
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
                ["third-smoke", "assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("cancelled", result.Status);
        AssertEqual(1, result.Cancelled!.Value);
        AssertEqual(2, result.Skipped!.Value);
        AssertSequence(["assembler-fixture"], adapter.RunCalls);
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
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("infrastructure", result.Status);
        AssertEqual(1, result.Passed);
        AssertEqual(1, result.Failed);
        AssertEqual("infrastructure", result.Failures![0].Status);
        AssertEqual("DEVBRIDGE_CLIENT_TIMEOUT", result.Failures[0].ErrorCode);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
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
                    impactAdapter: impactAdapter)
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
                    ["suite", "run", "smoke", "--json", "--catalog", catalogPath],
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
            Assert(!stdout.ToString().Contains("operations", StringComparison.Ordinal),
                "Suite CLI must not emit child operations.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
        AssertEqual("rimtest affected --run --json", root.GetProperty("nextAction").GetString());
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
            "{\"schemaVersion\":\"rimtest-doctor/v1\",\"status\":\"blocked\",\"component\":\"rimctx\",\"code\":\"INDEX_MISSING\",\"nextAction\":\"rimctx index --json\"}",
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
            "{\"schemaVersion\":\"rimtest-doctor/v1\",\"status\":\"blocked\",\"component\":\"manifest\",\"code\":\"STACK_MANIFEST_JSON_INVALID\"}",
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
        AssertEqual("rimtest init --json", document.RootElement.GetProperty("nextAction").GetString());
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

            CliResult result = RunInitFixture(directory);

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
            RunGit(directory, "config", "user.email", "rimtest@example.invalid");
            RunGit(directory, "config", "user.name", "RimTest");
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

    private static void CleanAffectedRunIsExplicitAndDoesNotLaunch()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(true, []));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--run", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    adapter,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("ok", root.GetProperty("status").GetString());
            AssertEqual(0, root.GetProperty("tests").GetArrayLength());
            AssertEqual(0, adapter.RunCalls.Count);
            Assert(string.IsNullOrEmpty(stderr.ToString()),
                "A clean affected run should not write diagnostics.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            AssertEqual(CliExitCodes.ConservativeSelection, exitCode);
            AssertEqual("blocked", root.GetProperty("status").GetString());
            AssertEqual("GIT_DISCOVERY_FAILED", root.GetProperty("errorCode").GetString());
            AssertEqual("git status --short", root.GetProperty("nextAction").GetString());
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
            AssertEqual("rimctx index --json", root.GetProperty("nextAction").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static CliResult RunInitFixture(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode = WithCurrentDirectory(
            directory,
            () => CliApplication.RunAsync(
                    ["init", "--json"],
                    stdout,
                    stderr)
                .GetAwaiter()
                .GetResult());
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
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

    private static DevBridgeRecipeRunResult PassRun(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
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

    private static DevBridgeRecipeRunResult FailedRun(
        string recipeId,
        string fingerprint,
        string errorCode) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.TestFailure,
                errorCode),
            false,
            "run-" + recipeId,
            1,
            null,
            null,
            "evidence-" + recipeId,
            fingerprint,
            null,
            null,
            null,
            []);

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
            "rimtest-tests-" + Guid.NewGuid().ToString("N"));
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

    private static RimErrorDiagnosisAdapter CreateRimErrorAdapter(
        FakeTransport transport)
    {
        return new RimErrorDiagnosisAdapter(
            transport,
            new RimErrorAdapterOptions
            {
                CommandPath = "rimerror.exe",
                WorkingDirectory = "RimErrorRoot",
                LogPath = "Player.log",
                StorePath = "RimErrorRoot/latest.json",
                IngestTimeout = TimeSpan.FromSeconds(1),
                LatestTimeout = TimeSpan.FromSeconds(1)
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

        public Task<DevBridgeRecipeShowResult> ShowAsync(
            string recipeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                null));

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
            RunCalls.Add(recipeId);
            return Task.FromResult(
                Runs.TryGetValue(recipeId, out DevBridgeRecipeRunResult? run)
                    ? run
                    : InfrastructureRun(recipeId, "FAKE_RECIPE_NOT_CONFIGURED"));
        }
    }

    private sealed class FakeRimContextProcessTransport : IRimContextProcessTransport
    {
        private readonly RimContextProcessResult result;
        private readonly RimContextProcessResult? indexResult;

        public FakeRimContextProcessTransport(
            RimContextProcessResult result,
            RimContextProcessResult? indexResult = null)
        {
            this.result = result;
            this.indexResult = indexResult;
        }

        public List<RimContextProcessRequest> Requests { get; } = [];

        public Task<RimContextProcessResult> ExecuteAsync(
            RimContextProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.Count > 0 &&
                string.Equals(request.Arguments[0], "index", StringComparison.Ordinal))
            {
                return Task.FromResult(indexResult ?? new RimContextProcessResult(
                    0,
                    "{\"schemaVersion\":\"rimctx/v1\",\"status\":\"ok\",\"command\":\"index\",\"data\":{}}",
                    string.Empty));
            }

            return Task.FromResult(result);
        }
    }

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

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
