using System.Diagnostics;
using RimTest.Catalog;
using RimTest.DevBridge;
using RimTest.Doctor;
using RimTest.Execution;
using RimTest.Git;
using RimTest.RimError;
using RimTest.RimContext;
using RimTest.Results;
using RimTest.Stack;

namespace RimTest;

public static class CliApplication
{
    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr)
    {
        return RunAsync(args, stdout, stderr).GetAwaiter().GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter)
    {
        return RunAsync(args, stdout, stderr, recipeAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter rimErrorAdapter)
    {
        return RunAsync(
                args,
                stdout,
                stderr,
                recipeAdapter,
                diagnosisAdapter: rimErrorAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter rimErrorAdapter,
        IRimContextImpactAdapter rimContextAdapter)
    {
        return RunAsync(
                args,
                stdout,
                stderr,
                recipeAdapter,
                diagnosisAdapter: rimErrorAdapter,
                impactAdapter: rimContextAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter? recipeAdapter = null,
        CancellationToken cancellationToken = default,
        IRimErrorDiagnosisAdapter? diagnosisAdapter = null,
        IRimContextImpactAdapter? impactAdapter = null,
        IDevBridgeProcessTransport? processTransport = null,
        IGitChangeProvider? gitChangeProvider = null)
    {
        long started = Stopwatch.GetTimestamp();
        string? workflowId = null;
        try
        {
            CliRequest request = CliParser.Parse(args);
            if (request.HelpRequested)
            {
                CliParser.WriteHelp(stdout);
                return CliExitCodes.Success;
            }

            workflowId = NeedsWorkflowCorrelation(request)
                ? WorkflowCorrelation.Create()
                : null;

            if (request.Command is CliCommand.RecipeShow or
                CliCommand.RecipePlan or
                CliCommand.RecipeRun)
            {
                return await ExecuteRecipeCommandAsync(
                    request,
                    stdout,
                    recipeAdapter,
                    cancellationToken,
                    workflowId).ConfigureAwait(false);
            }

            if (request.Command == CliCommand.Doctor)
            {
                return await ExecuteDoctorCommandAsync(
                        request,
                        stdout,
                        stderr,
                        processTransport,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.Command == CliCommand.Init)
            {
                StackInitResult result = StackInitializer.Run(request);
                WriteJson(stdout, result.Output);
                return result.ExitCode;
            }

            return await ExecuteCatalogCommandAsync(
                request,
                stdout,
                stderr,
                recipeAdapter,
                diagnosisAdapter,
                impactAdapter,
                gitChangeProvider,
                cancellationToken,
                started,
                workflowId).ConfigureAwait(false);
        }
        catch (CliParseException exception)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Invalid(
                        testId,
                        "CLI_INVALID",
                        ElapsedMilliseconds(started),
                        workflowId));
                return CliExitCodes.InvalidInput;
            }

            WriteError(
                stdout,
                "CLI_INVALID",
                [new CatalogIssue("CLI_INVALID", exception.Message)]);
            return CliExitCodes.InvalidInput;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Cancelled(
                        testId,
                        ElapsedMilliseconds(started),
                        workflowId));
                return CliExitCodes.Cancelled;
            }

            WriteJson(
                stdout,
                new
                {
                    status = "error",
                    code = "RIMTEST_CANCELLED",
                    outcome = "cancelled"
                });
            return CliExitCodes.Cancelled;
        }
        catch (Exception)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Infrastructure(
                        testId,
                        "INTERNAL_ERROR",
                        ElapsedMilliseconds(started),
                        workflowId));
                return CliExitCodes.InternalError;
            }

            stderr.WriteLine("rimtest internal error.");
            WriteError(
                stdout,
                "INTERNAL_ERROR",
                [new CatalogIssue("INTERNAL_ERROR", "An unexpected error occurred.")]);
            return CliExitCodes.InternalError;
        }
    }

    private static async Task<int> ExecuteRecipeCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeRecipeAdapter? recipeAdapter,
        CancellationToken cancellationToken,
        string? workflowId)
    {
        IDevBridgeRecipeAdapter adapter = CreateAdapter(request, recipeAdapter);
        switch (request.Command)
        {
            case CliCommand.RecipeShow:
                {
                    DevBridgeRecipeShowResult result = await adapter.ShowAsync(
                        request.Id!,
                        cancellationToken).ConfigureAwait(false);
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["recipe"] = result.RecipeId,
                        ["outcome"] = OutcomeName(result.Status.Outcome)
                    };
                    if (result.Definition.HasValue)
                    {
                        output["definition"] = result.Definition.Value;
                    }

                    AddStatusFields(output, result.Status);
                    WriteJson(stdout, output);
                    return ExitCodeFor(result.Status.Outcome);
                }
            case CliCommand.RecipePlan:
                {
                    DevBridgeRecipePlanResult result = await adapter.PlanAsync(
                        request.Id!,
                        cancellationToken).ConfigureAwait(false);
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["recipe"] = result.RecipeId,
                        ["outcome"] = OutcomeName(result.Status.Outcome)
                    };
                    if (result.Plan is not null)
                    {
                        output["alreadySatisfied"] = result.Plan.AlreadySatisfied;
                        output["estimatedRimWorldLaunches"] =
                            result.Plan.EstimatedRimWorldLaunches;
                        output["steps"] = result.Plan.Steps;
                        output["nextAction"] = result.Plan.NextAction;
                        output["blockedBy"] = result.Plan.BlockedBy;
                    }

                    AddStatusFields(output, result.Status);
                    WriteJson(stdout, output);
                    return ExitCodeFor(result.Status.Outcome);
                }
            case CliCommand.RecipeRun:
                {
                    DevBridgeRecipeRunResult result = await adapter.RunAsync(
                        request.Id!,
                        workflowId,
                        cancellationToken).ConfigureAwait(false);
                    WriteRunResult(
                        result.RecipeId,
                        result.RecipeId,
                        result,
                        stdout,
                        workflowId);
                    return ExitCodeFor(result.Status.Outcome);
                }
            default:
                throw new InvalidOperationException("Unknown recipe command.");
        }
    }

    private static async Task<int> ExecuteCatalogCommandAsync(
        CliRequest request,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IRimContextImpactAdapter? impactAdapter,
        IGitChangeProvider? gitChangeProvider,
        CancellationToken cancellationToken,
        long started,
        string? workflowId)
    {
        CatalogLoadResult loaded = CatalogLoader.Load(request.CatalogPath);
        if (loaded.Catalog is null)
        {
            if (request.Command == CliCommand.RunTest)
            {
                return WriteRimTestInvalid(
                    request.Id!,
                    FirstErrorCode(loaded.Errors, "CATALOG_INVALID"),
                    started,
                    stdout,
                    workflowId: workflowId);
            }

            WriteError(stdout, "CATALOG_INVALID", loaded.Errors);
            return CliExitCodes.InvalidInput;
        }

        IReadOnlySet<string>? recipeIds = null;
        if (request.RecipeListPath is not null)
        {
            RecipeListLoadResult recipeList = RecipeListLoader.Load(request.RecipeListPath);
            if (recipeList.RecipeIds is null)
            {
                if (request.Command == CliCommand.RunTest)
                {
                    return WriteRimTestInvalid(
                        request.Id!,
                        FirstErrorCode(recipeList.Errors, "RECIPE_LIST_INVALID"),
                        started,
                        stdout,
                        workflowId: workflowId);
                }

                WriteError(stdout, "RECIPE_LIST_INVALID", recipeList.Errors);
                return CliExitCodes.InvalidInput;
            }

            recipeIds = recipeList.RecipeIds;
        }

        CatalogValidationResult validation =
            CatalogValidator.Validate(loaded.Catalog, recipeIds);
        if (!validation.IsValid)
        {
            if (request.Command == CliCommand.RunTest)
            {
                return WriteRimTestInvalid(
                    request.Id!,
                    FirstErrorCode(validation.Errors, "CATALOG_INVALID"),
                    started,
                    stdout,
                    workflowId: workflowId);
            }

            WriteError(stdout, "CATALOG_INVALID", validation.Errors);
            return CliExitCodes.InvalidInput;
        }

        switch (request.Command)
        {
            case CliCommand.List:
                return WriteTestList(loaded.Catalog, stdout);
            case CliCommand.ShowTest:
                return WriteTest(loaded.Catalog, request.Id!, stdout);
            case CliCommand.Suites:
                return WriteSuiteList(loaded.Catalog, stdout);
            case CliCommand.ShowSuite:
                return WriteSuite(loaded.Catalog, request.Id!, stdout);
            case CliCommand.Validate:
                return WriteValidation(loaded.Catalog, validation, stdout);
            case CliCommand.SuiteRun:
                {
                    CatalogSuite? suite = CatalogNavigator.FindSuite(loaded.Catalog, request.Id!);
                    if (suite is null)
                    {
                        WriteError(
                            stdout,
                            "SUITE_NOT_FOUND",
                            [new CatalogIssue(
                            "SUITE_NOT_FOUND",
                            $"Suite was not found: {request.Id}.",
                            "id")]);
                        return CliExitCodes.NotFound;
                    }

                    return await RunSuiteAsync(
                            loaded.Catalog,
                            suite.Id,
                            CatalogNavigator.ResolvedTestIds(loaded.Catalog, suite.Id),
                            request,
                            stdout,
                            recipeAdapter,
                            diagnosisAdapter,
                            started,
                            cancellationToken,
                            workflowId: workflowId)
                        .ConfigureAwait(false);
                }
            case CliCommand.Affected:
                {
                    IReadOnlyList<string> changedPaths = request.ChangedPaths;
                    if (changedPaths.Count == 0)
                    {
                        IGitChangeProvider git = gitChangeProvider ?? new SystemGitChangeProvider();
                        GitChangeDiscoveryResult discovered;
                        try
                        {
                            discovered = await git.DiscoverAsync(
                                    AffectedGitRoot(request),
                                    request.AffectedBase,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            stderr.WriteLine("rimtest: Git change discovery failed.");
                            discovered = new GitChangeDiscoveryResult(
                                false,
                                [],
                                "GIT_DISCOVERY_FAILED",
                                exception.Message);
                        }

                        if (!discovered.Resolved)
                        {
                            var blocked = new RimTestSelectionResult
                            {
                                Status = "blocked",
                                ReasonCount = 1,
                                ErrorCode = discovered.ErrorCode ?? "GIT_DISCOVERY_FAILED",
                                NextAction = "git status --short"
                            };
                            WriteJson(stdout, blocked);
                            return SelectionExitCode(blocked);
                        }

                        changedPaths = discovered.Paths;
                        if (changedPaths.Count == 0)
                        {
                            var clean = new RimTestSelectionResult
                            {
                                Status = "ok",
                                Tests = [],
                                ReasonCount = 0
                            };
                            WriteJson(stdout, clean);
                            return CliExitCodes.Success;
                        }
                    }

                    IRimContextImpactAdapter adapter = impactAdapter ?? CreateRimContextAdapter(request);
                    var selector = new RimContextTestSelector(adapter);
                    RimTestSelectionResult selection = await selector.SelectAsync(
                            loaded.Catalog,
                            changedPaths,
                            request.FallbackSuite,
                            request.Explain,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (request.RunSelected &&
                        selection.Status == "conservative" &&
                        selection.Tests.Count == 0)
                    {
                        WriteJson(stdout, selection);
                        return SelectionExitCode(selection);
                    }

                    if (request.RunSelected &&
                        selection.Status is "ok" or "conservative")
                    {
                        return await RunSuiteAsync(
                                loaded.Catalog,
                                "affected",
                                selection.Tests,
                                request,
                                stdout,
                                recipeAdapter,
                                diagnosisAdapter,
                                started,
                                cancellationToken,
                                selection.Status,
                                selection.ErrorCode,
                                selection.FallbackSuite,
                                workflowId)
                            .ConfigureAwait(false);
                    }

                    WriteJson(stdout, selection);
                    return SelectionExitCode(selection);
                }
            case CliCommand.RunTest:
                {
                    CatalogTest? test = CatalogNavigator.FindTest(loaded.Catalog, request.Id!);
                    if (test is null)
                    {
                        return WriteRimTestInvalid(
                            request.Id!,
                            "TEST_NOT_FOUND",
                            started,
                            stdout,
                            invalidExitCode: CliExitCodes.InvalidInput,
                            workflowId: workflowId);
                    }

                    IDevBridgeRecipeAdapter adapter = CreateAdapter(request, recipeAdapter);
                    var executor = CreateTestExecutor(request, adapter, diagnosisAdapter);
                    CatalogTestExecutionResult execution = await executor.RunAsync(
                            loaded.Catalog,
                            test.Id,
                            started,
                            cancellationToken,
                            workflowId)
                        .ConfigureAwait(false);
                    WriteJson(stdout, execution.Result);
                    return RimTestExitCodeFor(execution.Run.RecipeResult.Status.Outcome);
                }
            default:
                throw new InvalidOperationException("Unknown catalog command.");
        }
    }

    private static string AffectedGitRoot(CliRequest request) =>
        request.RimContextRootPath ??
        Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT") ??
        Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT") ??
        Environment.CurrentDirectory;

    private static async Task<int> ExecuteDoctorCommandAsync(
        CliRequest request,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeProcessTransport? processTransport,
        CancellationToken cancellationToken)
    {
        var runner = new RimTestDoctorRunner(stderr);
        DoctorRunResult result = await runner.RunAsync(
                request,
                processTransport ?? new SystemDevBridgeProcessTransport(),
                cancellationToken)
            .ConfigureAwait(false);
        WriteJson(stdout, result.Output);
        return result.ExitCode;
    }

    private static int WriteTestList(CatalogDocument catalog, TextWriter stdout)
    {
        var tests = (catalog.Tests ?? [])
            .Where(static test => test is not null)
            .OrderBy(static test => test.Id, StringComparer.Ordinal)
            .Select(static test => new
            {
                id = test.Id,
                recipe = test.Recipe
            })
            .ToArray();

        WriteJson(stdout, new { tests });
        return CliExitCodes.Success;
    }

    private static int WriteTest(
        CatalogDocument catalog,
        string id,
        TextWriter stdout)
    {
        CatalogTest? test = CatalogNavigator.FindTest(catalog, id);
        if (test is null)
        {
            WriteError(
                stdout,
                "TEST_NOT_FOUND",
                [new CatalogIssue("TEST_NOT_FOUND", $"Test was not found: {id}.", "id")]);
            return CliExitCodes.NotFound;
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = test.Id,
            ["recipe"] = test.Recipe,
            ["cost"] = test.Cost.ToString().ToLowerInvariant(),
            ["suites"] = CatalogNavigator.ContainingSuiteIds(catalog, test.Id)
        };

        if (test.Description is not null)
        {
            details["description"] = test.Description;
        }

        if (test.Tags is not null)
        {
            details["tags"] = test.Tags.OrderBy(static tag => tag, StringComparer.Ordinal);
        }

        if (test.Covers is not null)
        {
            details["covers"] = test.Covers
                .OrderBy(static cover => cover.Kind, StringComparer.Ordinal)
                .ThenBy(static cover => cover.Name, StringComparer.Ordinal)
                .Select(static cover => new { kind = cover.Kind, name = cover.Name });
        }

        WriteJson(stdout, new { test = details });
        return CliExitCodes.Success;
    }

    private static int WriteSuiteList(CatalogDocument catalog, TextWriter stdout)
    {
        var suites = (catalog.Suites ?? [])
            .Where(static suite => suite is not null)
            .OrderBy(static suite => suite.Id, StringComparer.Ordinal)
            .Select(static suite => new { id = suite.Id })
            .ToArray();

        WriteJson(stdout, new { suites });
        return CliExitCodes.Success;
    }

    private static int WriteSuite(
        CatalogDocument catalog,
        string id,
        TextWriter stdout)
    {
        CatalogSuite? suite = CatalogNavigator.FindSuite(catalog, id);
        if (suite is null)
        {
            WriteError(
                stdout,
                "SUITE_NOT_FOUND",
                [new CatalogIssue("SUITE_NOT_FOUND", $"Suite was not found: {id}.", "id")]);
            return CliExitCodes.NotFound;
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = suite.Id,
            ["tests"] = (suite.Tests ?? []).OrderBy(static value => value, StringComparer.Ordinal),
            ["suites"] = (suite.Suites ?? []).OrderBy(static value => value, StringComparer.Ordinal),
            ["resolvedTests"] = CatalogNavigator
                .ResolvedTestIds(catalog, suite.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
        };

        if (suite.Description is not null)
        {
            details["description"] = suite.Description;
        }

        WriteJson(stdout, new { suite = details });
        return CliExitCodes.Success;
    }

    private static int WriteValidation(
        CatalogDocument catalog,
        CatalogValidationResult validation,
        TextWriter stdout)
    {
        WriteJson(
            stdout,
            new
            {
                valid = true,
                tests = (catalog.Tests ?? []).Count,
                suites = (catalog.Suites ?? []).Count,
                recipeVerification = validation.RecipesVerified ? "checked" : "skipped"
            });
        return CliExitCodes.Success;
    }

    private static void WriteJson(TextWriter stdout, object value)
    {
        stdout.WriteLine(CatalogJsonFacade.Serialize(value));
    }

    private static int WriteRimTestInvalid(
        string testId,
        string errorCode,
        long started,
        TextWriter stdout,
        int invalidExitCode = CliExitCodes.InvalidInput,
        string? workflowId = null)
    {
        WriteJson(
            stdout,
            RimTestResultFactory.Invalid(
                testId,
                errorCode,
                ElapsedMilliseconds(started),
                workflowId));
        return invalidExitCode;
    }

    private static string FirstErrorCode(
        IReadOnlyList<CatalogIssue> errors,
        string fallback)
    {
        return errors.FirstOrDefault()?.Code ?? fallback;
    }

    private static long ElapsedMilliseconds(long started)
    {
        return Math.Max(
            0,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static bool TryGetRunTestId(
        IReadOnlyList<string> args,
        out string testId)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (!string.Equals(args[index], "run", StringComparison.OrdinalIgnoreCase) ||
                args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            bool isRecipeRun = index > 0 &&
                string.Equals(args[index - 1], "recipe", StringComparison.OrdinalIgnoreCase);
            if (!isRecipeRun)
            {
                testId = args[index + 1];
                return true;
            }
        }

        testId = string.Empty;
        return false;
    }

    private static IDevBridgeRecipeAdapter CreateAdapter(
        CliRequest request,
        IDevBridgeRecipeAdapter? recipeAdapter)
    {
        if (recipeAdapter is not null)
        {
            return recipeAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeRecipeAdapter(
            new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IRimErrorDiagnosisAdapter CreateRimErrorAdapter(
        CliRequest request,
        IRimErrorDiagnosisAdapter? diagnosisAdapter)
    {
        if (diagnosisAdapter is not null)
        {
            return diagnosisAdapter;
        }

        RimErrorAdapterOptions options = RimErrorAdapterOptions.Discover(
            request.RimErrorPath,
            request.RimErrorLogPath,
            request.RimErrorStorePath);
        return new RimErrorDiagnosisAdapter(
            new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IRimContextImpactAdapter CreateRimContextAdapter(
        CliRequest request)
    {
        RimContextAdapterOptions options = RimContextAdapterOptions.Discover(
            request.RimContextPath,
            request.RimContextRootPath,
            request.RimContextStorePath,
            request.RimContextDepth,
            request.RimContextLimit);
        return new RimContextImpactAdapter(
            new SystemRimContextProcessTransport(),
            options);
    }

    private static CatalogTestExecutionService CreateTestExecutor(
        CliRequest request,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter)
    {
        return new CatalogTestExecutionService(
            recipeAdapter,
            () => CreateRimErrorAdapter(request, diagnosisAdapter));
    }

    private static async Task<int> RunSuiteAsync(
        CatalogDocument catalog,
        string suiteId,
        IReadOnlyList<string> testIds,
        CliRequest request,
        TextWriter stdout,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        long started,
        CancellationToken cancellationToken,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        string? fallbackSuite = null,
        string? workflowId = null)
    {
        IDevBridgeRecipeAdapter adapter = CreateAdapter(request, recipeAdapter);
        var executor = CreateTestExecutor(request, adapter, diagnosisAdapter);
        var runner = new CatalogSuiteRunner(adapter, executor);
        CatalogSuiteExecutionResult execution = await runner.RunAsync(
                catalog,
                suiteId,
                testIds,
                cancellationToken,
                workflowId)
            .ConfigureAwait(false);
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            execution,
            ElapsedMilliseconds(started),
            selectionStatus,
            selectionErrorCode,
            fallbackSuite,
            workflowId);
        WriteJson(stdout, result);
        return SuiteExitCodeFor(result.Status);
    }

    private static int SelectionExitCode(RimTestSelectionResult selection)
    {
        return selection.Status switch
        {
            "ok" => CliExitCodes.Success,
            "invalid" => CliExitCodes.InvalidInput,
            "cancelled" => CliExitCodes.Cancelled,
            "blocked" => CliExitCodes.ConservativeSelection,
            "conservative" when selection.Tests.Count > 0 => CliExitCodes.Success,
            "conservative" => CliExitCodes.ConservativeSelection,
            _ => CliExitCodes.InternalError
        };
    }

    private static int SuiteExitCodeFor(string status) => status switch
    {
        "pass" => CliExitCodes.Success,
        "fail" => CliExitCodes.TestFailure,
        "cancelled" => CliExitCodes.Cancelled,
        "conservative" => CliExitCodes.ConservativeSelection,
        "invalid" => CliExitCodes.InvalidInput,
        _ => CliExitCodes.InternalError
    };

    private static void WriteRunResult(
        string testId,
        string recipeId,
        DevBridgeRecipeRunResult result,
        TextWriter stdout,
        string? workflowId = null)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["test"] = testId,
            ["recipe"] = recipeId,
            ["outcome"] = OutcomeName(result.Status.Outcome)
        };
        string? effectiveWorkflowId = result.WorkflowId ?? workflowId;
        if (!string.IsNullOrWhiteSpace(effectiveWorkflowId))
        {
            output["workflowId"] = effectiveWorkflowId;
        }
        if (result.Passed.HasValue)
        {
            output["passed"] = result.Passed.Value;
        }

        if (result.RunId is not null)
        {
            output["runId"] = result.RunId;
        }

        if (result.Generation.HasValue)
        {
            output["generation"] = result.Generation.Value;
        }

        if (result.LeaseId is not null)
        {
            output["leaseId"] = result.LeaseId;
        }

        if (result.Evidence is not null)
        {
            output["evidence"] = result.Evidence;
        }

        if (result.EvidenceId is not null)
        {
            output["evidenceId"] = result.EvidenceId;
        }

        if (result.FailureFingerprint is not null)
        {
            output["failureFingerprint"] = result.FailureFingerprint;
        }

        if (result.FinalNextAction is not null)
        {
            output["finalNextAction"] = result.FinalNextAction;
        }

        string? nextAction = NextActionFor(result.Status.Outcome);
        if (nextAction is not null)
        {
            output["nextAction"] = nextAction;
        }

        if (result.RestartRequired.HasValue)
        {
            output["restartRequired"] = result.RestartRequired.Value;
        }

        if (result.LaunchesConsumed.HasValue)
        {
            output["launchesConsumed"] = result.LaunchesConsumed.Value;
        }

        if (result.Operations.Count > 0)
        {
            output["operations"] = result.Operations;
        }

        AddStatusFields(output, result.Status);
        WriteJson(stdout, output);
    }

    private static void AddStatusFields(
        IDictionary<string, object?> output,
        DevBridgeAdapterStatus status)
    {
        if (status.ErrorCode is not null)
        {
            output["errorCode"] = status.ErrorCode;
        }

        if (status.Error is not null)
        {
            output["error"] = status.Error;
        }

        if (status.ProcessExitCode.HasValue)
        {
            output["processExitCode"] = status.ProcessExitCode.Value;
        }

        if (!string.IsNullOrEmpty(status.Stderr))
        {
            output["stderr"] = status.Stderr;
        }

        if (status.ResponseSchema is not null)
        {
            output["responseSchema"] = status.ResponseSchema;
        }
    }

    private static string OutcomeName(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.TestFailure => "testFailure",
            DevBridgeOutcomeKind.DevBridgeRefusal => "devBridgeRefusal",
            DevBridgeOutcomeKind.InfrastructureFailure => "infrastructureFailure",
            DevBridgeOutcomeKind.Timeout => "timeout",
            DevBridgeOutcomeKind.Cancelled => "cancelled",
            DevBridgeOutcomeKind.MalformedResponse => "malformedResponse",
            DevBridgeOutcomeKind.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };
    }

    private static bool NeedsWorkflowCorrelation(CliRequest request) =>
        request.Command is CliCommand.RecipeRun or
            CliCommand.RunTest or
            CliCommand.SuiteRun ||
        request.Command == CliCommand.Affected && request.RunSelected;

    private static string? NextActionFor(DevBridgeOutcomeKind outcome) => outcome switch
    {
        DevBridgeOutcomeKind.DevBridgeRefusal or
        DevBridgeOutcomeKind.InfrastructureFailure or
        DevBridgeOutcomeKind.Timeout or
        DevBridgeOutcomeKind.MalformedResponse or
        DevBridgeOutcomeKind.IncompatibleSchema => "DevBridge.cmd doctor --json",
        _ => null
    };

    private static int ExitCodeFor(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.Success => CliExitCodes.Success,
            DevBridgeOutcomeKind.TestFailure => CliExitCodes.TestFailure,
            DevBridgeOutcomeKind.DevBridgeRefusal => CliExitCodes.NotFound,
            DevBridgeOutcomeKind.Timeout => CliExitCodes.Timeout,
            DevBridgeOutcomeKind.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };
    }

    private static int RimTestExitCodeFor(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.Success => CliExitCodes.Success,
            DevBridgeOutcomeKind.TestFailure => CliExitCodes.TestFailure,
            DevBridgeOutcomeKind.Timeout => CliExitCodes.Timeout,
            DevBridgeOutcomeKind.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };
    }

    private static void WriteError(
        TextWriter stdout,
        string code,
        IReadOnlyList<CatalogIssue> errors)
    {
        WriteJson(stdout, new { status = "error", code, errors });
    }
}
