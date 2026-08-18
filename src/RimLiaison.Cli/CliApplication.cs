using System.Text.Json;
using System.Diagnostics;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Doctor;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.RimError;
using RimLiaison.RimContext;
using RimLiaison.Results;
using RimLiaison.Stack;

namespace RimLiaison;

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
        IGitChangeProvider? gitChangeProvider = null,
        IDevBridgeCapabilityAdapter? capabilityAdapter = null,
        IDevBridgeUiAdapter? uiAdapter = null,
        IDevBridgeModDevelopmentAdapter? developmentAdapter = null,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter = null)
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

            if (request.Command == CliCommand.Capabilities)
            {
                return await ExecuteCapabilitiesCommandAsync(
                        request,
                        stdout,
                        processTransport,
                        capabilityAdapter,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.Command is CliCommand.UiTargets or CliCommand.UiScreenshot)
            {
                return await ExecuteUiCommandAsync(
                        request,
                        stdout,
                        processTransport,
                        uiAdapter,
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
                diagnosticSourceAdapter,
                impactAdapter,
                gitChangeProvider,
                processTransport,
                cancellationToken,
                started,
                workflowId,
                developmentAdapter).ConfigureAwait(false);
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

            stderr.WriteLine("rimliaison internal error.");
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
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IRimContextImpactAdapter? impactAdapter,
        IGitChangeProvider? gitChangeProvider,
        IDevBridgeProcessTransport? processTransport,
        CancellationToken cancellationToken,
        long started,
        string? workflowId,
        IDevBridgeModDevelopmentAdapter? developmentAdapter)
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
                            diagnosticSourceAdapter,
                            processTransport,
                            started,
                            cancellationToken,
                            workflowId: workflowId)
                        .ConfigureAwait(false);
                }
            case CliCommand.Affected:
                {
                    IReadOnlyList<string> changedPaths = request.ChangedPaths;
                    IReadOnlyList<GitChangedPath>? gitChanges = null;
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
                            stderr.WriteLine("RimLiaison: Git change discovery failed.");
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
                        gitChanges = discovered.Changes;
                        if (changedPaths.Count == 0 && gitChanges.Count == 0)
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

                        if (changedPaths.Count == 0)
                        {
                            var blocked = new RimTestSelectionResult
                            {
                                Status = "blocked",
                                ReasonCount = 1,
                                ErrorCode = "GIT_CHANGED_PATHS_MISSING",
                                NextAction = "git status --short"
                            };
                            WriteJson(stdout, blocked);
                            return SelectionExitCode(blocked);
                        }
                    }

                    IRimContextImpactAdapter adapter = impactAdapter ?? CreateRimContextAdapter(request);
                    var selector = new RimContextTestSelector(adapter);
                    RimTestSelectionResult selection = await selector.SelectAsync(
                            loaded.Catalog,
                            changedPaths,
                            request.FallbackSuite,
                            request.Explain,
                            cancellationToken,
                            gitChanges)
                        .ConfigureAwait(false);

                    if (selection.Status == "ok" && selection.Tests.Count == 0)
                    {
                        selection = new RimTestSelectionResult
                        {
                            Status = "blocked",
                            ReasonCount = Math.Max(1, selection.ReasonCount),
                            ErrorCode = "AFFECTED_NO_TESTS",
                            NextAction = "rimliaison affected --run --fallback-suite <suite>",
                            Reasons = selection.Reasons
                        };
                    }

                    if (request.RunSelected && selection.Tests.Count == 0)
                    {
                        WriteJson(stdout, selection);
                        return SelectionExitCode(selection);
                    }

                    if (request.RunSelected &&
                        selection.Status is "ok" or "conservative")
                    {
                        ArtifactFreshnessTransactionRequest? freshnessRequest =
                            CreateArtifactFreshnessRequest(
                                request,
                                changedPaths,
                                workflowId);
                        return await RunSuiteAsync(
                                loaded.Catalog,
                                "affected",
                                selection.Tests,
                                request,
                                stdout,
                                recipeAdapter,
                                diagnosisAdapter,
                                diagnosticSourceAdapter,
                                processTransport,
                                started,
                                cancellationToken,
                                selection.Status,
                                selection.ErrorCode,
                                selection.FallbackSuite,
                                workflowId,
                                developmentAdapter,
                                freshnessRequest)
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
                            invalidExitCode: CliExitCodes.NotFound,
                            workflowId: workflowId);
                    }

                    IDevBridgeRecipeAdapter adapter = CreateAdapter(
                        request,
                        recipeAdapter,
                        processTransport);
                    var executor = CreateTestExecutor(
                        request,
                        adapter,
                        diagnosisAdapter,
                        diagnosticSourceAdapter,
                        processTransport);
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

    private static async Task<int> ExecuteCapabilitiesCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeCapabilityAdapter? capabilityAdapter,
        CancellationToken cancellationToken)
    {
        IDevBridgeCapabilityAdapter adapter = CreateCapabilityAdapter(
            request,
            processTransport,
            capabilityAdapter);
        var query = new DevBridgeCapabilityQuery(
            request.CapabilityQuery,
            request.CapabilityCategory,
            request.CapabilityProvider,
            request.CapabilitySource,
            request.CapabilityLimit);
        DevBridgeCapabilityDiscoveryResult result = await adapter.DiscoverAsync(
                query,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status.IsSuccess)
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = DevBridgeCapabilitySchemas.Output,
                ["status"] = "ok",
                ["source"] = "RimBridgeServer",
                ["count"] = result.Capabilities.Count,
                ["totalMatches"] = result.TotalMatches,
                ["truncated"] = result.Truncated,
                ["limit"] = query.Limit,
                ["capabilities"] = result.Capabilities
                    .Select(ToCapabilityOutput)
                    .ToArray()
            };
            AddCapabilityFilter(output, "query", query.Text);
            AddCapabilityFilter(output, "category", query.Category);
            AddCapabilityFilter(output, "providerId", query.ProviderId);
            AddCapabilityFilter(output, "source", query.Source);
            WriteJson(stdout, output);
            return CliExitCodes.Success;
        }

        var failure = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = DevBridgeCapabilitySchemas.Output,
            ["status"] = result.Status.Outcome == DevBridgeCapabilityOutcome.Unavailable
                ? "blocked"
                : "error",
            ["component"] = "rimbridge",
            ["outcome"] = CapabilityOutcomeName(result.Status.Outcome),
            ["code"] = result.Status.ErrorCode ?? "RIMBRIDGE_CAPABILITIES_FAILED",
            ["error"] = result.Status.Error ??
                "RimLiaison could not discover the RimBridgeServer capability registry."
        };
        if (result.Status.NextAction is not null)
        {
            failure["nextAction"] = result.Status.NextAction;
        }

        if (result.Status.ResponseSchema is not null)
        {
            failure["responseSchema"] = result.Status.ResponseSchema;
        }

        if (result.Status.ProcessExitCode.HasValue)
        {
            failure["processExitCode"] = result.Status.ProcessExitCode.Value;
        }

        WriteJson(stdout, failure);
        return CapabilityExitCodeFor(result.Status.Outcome);
    }

    private static async Task<int> ExecuteUiCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeUiAdapter? uiAdapter,
        CancellationToken cancellationToken)
    {
        IDevBridgeUiAdapter adapter = CreateUiAdapter(
            request,
            processTransport,
            uiAdapter);

        if (request.Command == CliCommand.UiTargets)
        {
            DevBridgeUiTargetsResult result = await adapter.GetTargetsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!result.Status.IsSuccess)
            {
                WriteUiFailure(stdout, DevBridgeUiSchemas.Targets, result.Status);
                return UiExitCodeFor(result.Status.Outcome);
            }

            var targets = result.Targets
                .Select(ToUiTargetOutput)
                .ToArray();
            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = DevBridgeUiSchemas.Targets,
                ["status"] = "ok",
                ["count"] = targets.Length,
                ["targets"] = targets
            };
            AddUiCorrelation(output, result.Status);
            WriteJson(stdout, output);
            return CliExitCodes.Success;
        }

        DevBridgeUiCellRect? cellRect = null;
        DevBridgeUiCellRect parsedCellRect = default!;
        if (request.UiCellRect is not null &&
            !DevBridgeUiAdapter.TryParseCellRect(
                request.UiCellRect,
                out parsedCellRect,
                out string cellRectError))
        {
            WriteUiFailure(
                stdout,
                DevBridgeUiSchemas.Screenshot,
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InvalidRequest,
                    "RIMTEST_UI_CELL_RECT_INVALID",
                    cellRectError,
                    NextAction: null));
            return CliExitCodes.InvalidInput;
        }
        else if (request.UiCellRect is not null)
        {
            cellRect = parsedCellRect;
        }

        var screenshotRequest = new DevBridgeUiScreenshotRequest(
            request.UiTarget,
            cellRect);
        DevBridgeUiScreenshotResult screenshot = await adapter.CaptureAsync(
                screenshotRequest,
                cancellationToken)
            .ConfigureAwait(false);
        if (!screenshot.Status.IsSuccess || screenshot.Evidence is null)
        {
            WriteUiFailure(stdout, DevBridgeUiSchemas.Screenshot, screenshot.Status);
            return UiExitCodeFor(screenshot.Status.Outcome);
        }

        var evidence = screenshot.Evidence;
        var screenshotOutput = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = DevBridgeUiSchemas.Screenshot,
            ["status"] = "ok",
            ["captureStatus"] = evidence.CaptureStatus,
            ["path"] = evidence.Path
        };
        AddUiField(screenshotOutput, "targetId", evidence.TargetId);
        AddUiField(screenshotOutput, "targetKind", evidence.TargetKind);
        AddUiField(screenshotOutput, "targetLabel", evidence.TargetLabel);
        AddUiElement(screenshotOutput, "clipRect", evidence.ClipRect);
        AddUiElement(screenshotOutput, "requestedRect", evidence.RequestedRect);
        AddUiElement(screenshotOutput, "paddedRect", evidence.PaddedRect);
        if (evidence.CameraRestored.HasValue)
        {
            screenshotOutput["cameraRestored"] = evidence.CameraRestored.Value;
        }

        AddUiField(screenshotOutput, "capturedAtUtc", evidence.CapturedAtUtc);
        AddUiField(screenshotOutput, "operationId", evidence.OperationId);
        AddUiField(screenshotOutput, "workflowId", evidence.WorkflowId);
        AddUiField(screenshotOutput, "evidenceId", evidence.EvidenceId);
        WriteJson(stdout, screenshotOutput);
        return CliExitCodes.Success;
    }

    private static Dictionary<string, object?> ToUiTargetOutput(
        DevBridgeUiTarget target)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = target.Id
        };
        AddUiField(output, "kind", target.Kind);
        AddUiField(output, "label", target.Label);
        AddUiElement(output, "rect", target.Rect);
        return output;
    }

    private static void WriteUiFailure(
        TextWriter stdout,
        string schema,
        DevBridgeUiStatus status)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = schema,
            ["status"] = status.Outcome is
                DevBridgeUiOutcome.Unavailable or
                DevBridgeUiOutcome.VisualReadinessFailure
                ? "blocked"
                : "error",
            ["component"] = "rimbridge",
            ["outcome"] = UiOutcomeName(status.Outcome),
            ["code"] = status.ErrorCode ?? "RIMTEST_UI_FAILED",
            ["error"] = status.Error ?? "RimLiaison could not complete the UI request."
        };
        AddUiField(output, "nextAction", status.NextAction);
        if (status.ProcessExitCode.HasValue)
        {
            output["processExitCode"] = status.ProcessExitCode.Value;
        }

        AddUiField(output, "operationId", status.OperationId);
        AddUiField(output, "workflowId", status.WorkflowId);
        AddUiField(output, "evidenceId", status.EvidenceId);
        WriteJson(stdout, output);
    }

    private static void AddUiCorrelation(
        IDictionary<string, object?> output,
        DevBridgeUiStatus status)
    {
        AddUiField(output, "operationId", status.OperationId);
        AddUiField(output, "workflowId", status.WorkflowId);
        AddUiField(output, "evidenceId", status.EvidenceId);
    }

    private static void AddUiField(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
    }

    private static void AddUiElement(
        IDictionary<string, object?> output,
        string name,
        JsonElement? value)
    {
        if (value.HasValue)
        {
            output[name] = value.Value;
        }
    }

    private static Dictionary<string, object?> ToCapabilityOutput(
        DevBridgeCapability capability)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = capability.Id,
            ["title"] = capability.Title,
            ["parameters"] = capability.Parameters
                .Select(ToCapabilityParameterOutput)
                .ToArray()
        };
        if (capability.Aliases.Count > 0)
        {
            output["aliases"] = capability.Aliases;
        }

        AddCapabilityField(output, "summary", capability.Summary);
        AddCapabilityField(output, "category", capability.Category);
        AddCapabilityField(output, "providerId", capability.ProviderId);
        AddCapabilityField(output, "source", capability.Source);
        if (capability.ReadOnly.HasValue)
        {
            output["readOnly"] = capability.ReadOnly.Value;
        }

        return output;
    }

    private static Dictionary<string, object?> ToCapabilityParameterOutput(
        DevBridgeCapabilityParameter parameter)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = parameter.Name
        };
        AddCapabilityField(output, "type", parameter.Type);
        AddCapabilityField(output, "description", parameter.Description);
        if (parameter.Required.HasValue)
        {
            output["required"] = parameter.Required.Value;
        }

        if (parameter.DefaultValue.HasValue)
        {
            output["default"] = parameter.DefaultValue.Value;
        }

        return output;
    }

    private static void AddCapabilityFilter(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
    }

    private static void AddCapabilityField(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
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
        var positionals = new List<string>();
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            if (OptionTakesValue(argument) && index + 1 < args.Count)
            {
                index++;
            }
        }

        if (positionals.Count == 2 &&
            string.Equals(positionals[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            testId = positionals[1];
            return true;
        }

        testId = string.Empty;
        return false;
    }

    private static bool OptionTakesValue(string argument) => argument switch
    {
        "--catalog" or
        "--recipes" or
        "--devbridge" or
        "--devbridge-root" or
        "--devbridge-project" or
        "--rimerror" or
        "--rimerror-log" or
        "--rimerror-store" or
        "--rimcontext" or
        "--rimcontext-root" or
        "--rimcontext-store" or
        "--fallback-suite" or
        "--depth" or
        "--limit" or
        "--query" or
        "--category" or
        "--provider" or
        "--provider-id" or
        "--source" or
        "--target" or
        "--cell-rect" or
        "--base" => true,
        _ => false
    };

    private static IDevBridgeCapabilityAdapter CreateCapabilityAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeCapabilityAdapter? capabilityAdapter)
    {
        if (capabilityAdapter is not null)
        {
            return capabilityAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeCapabilityAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeUiAdapter CreateUiAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeUiAdapter? uiAdapter)
    {
        if (uiAdapter is not null)
        {
            return uiAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeUiAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeRecipeAdapter CreateAdapter(
        CliRequest request,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IDevBridgeProcessTransport? processTransport = null)
    {
        if (recipeAdapter is not null)
        {
            return recipeAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeRecipeAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IRimErrorDiagnosisAdapter CreateRimErrorAdapter(
        CliRequest request,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeProcessTransport? processTransport)
    {
        if (diagnosisAdapter is not null)
        {
            return diagnosisAdapter;
        }

        RimErrorAdapterOptions options = RimErrorAdapterOptions.Discover(
            request.RimErrorPath,
            request.RimErrorLogPath,
            request.RimErrorStorePath);
        return new RimErrorDiagnosisAdapter(options);
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
        return new RimContextImpactAdapter(options);
    }

    private static CatalogTestExecutionService CreateTestExecutor(
        CliRequest request,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IDevBridgeProcessTransport? processTransport)
    {
        IDevBridgeDiagnosticSourceAdapter? selectedSource = diagnosticSourceAdapter;
        if (selectedSource is null && diagnosisAdapter is null)
        {
            RimErrorAdapterOptions rimErrorOptions = RimErrorAdapterOptions.Discover(
                request.RimErrorPath,
                request.RimErrorLogPath,
                request.RimErrorStorePath);
            if (!rimErrorOptions.IsConfigured)
            {
                DevBridgeAdapterOptions devBridgeOptions = DevBridgeAdapterOptions.Discover(
                    request.DevBridgePath,
                    request.DevBridgeRootPath);
                selectedSource = new DevBridgeDiagnosticSourceAdapter(
                    processTransport ?? new SystemDevBridgeProcessTransport(),
                    devBridgeOptions);
            }
        }

        return new CatalogTestExecutionService(
            recipeAdapter,
            () => CreateRimErrorAdapter(request, diagnosisAdapter, processTransport),
            selectedSource is null ? null : () => selectedSource);
    }

    private static async Task<int> RunSuiteAsync(
        CatalogDocument catalog,
        string suiteId,
        IReadOnlyList<string> testIds,
        CliRequest request,
        TextWriter stdout,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IDevBridgeProcessTransport? processTransport,
        long started,
        CancellationToken cancellationToken,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        string? fallbackSuite = null,
        string? workflowId = null,
        IDevBridgeModDevelopmentAdapter? developmentAdapter = null,
        ArtifactFreshnessTransactionRequest? freshnessRequest = null)
    {
        bool ownsRecipeAdapter = recipeAdapter is null;
        DevBridgeAdapterOptions? bridgeOptions = ownsRecipeAdapter
            ? DevBridgeAdapterOptions.Discover(
                request.DevBridgePath,
                request.DevBridgeRootPath)
            : null;
        IDevBridgeProcessTransport? bridgeTransport = ownsRecipeAdapter
            ? processTransport ?? new SystemDevBridgeProcessTransport()
            : null;
        IDevBridgeRecipeAdapter adapter = CreateAdapter(
            request,
            recipeAdapter,
            bridgeTransport);
        var executor = CreateTestExecutor(
            request,
            adapter,
            diagnosisAdapter,
            diagnosticSourceAdapter,
            processTransport);
        IDevBridgeLeaseAdapter? leaseAdapter = ownsRecipeAdapter &&
            bridgeOptions is not null && bridgeTransport is not null
            ? new DevBridgeLeaseAdapter(bridgeTransport, bridgeOptions)
            : null;
        IDevBridgeFixtureResetAdapter? resetAdapter = adapter as IDevBridgeFixtureResetAdapter;
        IDevBridgeFreshGenerationAdapter? freshGenerationAdapter = ownsRecipeAdapter &&
            bridgeOptions is not null && bridgeTransport is not null
            ? new DevBridgeFreshGenerationAdapter(adapter, bridgeTransport, bridgeOptions)
            : null;
        var runner = new CatalogSuiteRunner(
            adapter,
            executor,
            leaseAdapter,
            resetAdapter,
            freshGenerationAdapter);
        ArtifactFreshnessTransactionResult? freshnessTransaction = null;
        CatalogSuiteExecutionResult execution;
        if (freshnessRequest is not null)
        {
            IDevBridgeModDevelopmentAdapter owner = developmentAdapter ??
                CreateDevelopmentAdapter(request);
            freshnessTransaction = await new ArtifactFreshnessTransaction(owner)
                    .PrepareAsync(freshnessRequest, cancellationToken)
                .ConfigureAwait(false);
            execution = freshnessTransaction.Success
                ? await runner.RunAsync(
                        catalog,
                        suiteId,
                        testIds,
                        cancellationToken,
                        workflowId)
                    .ConfigureAwait(false)
                : ArtifactFailureExecution(
                    suiteId,
                    testIds,
                    freshnessTransaction.Status,
                    workflowId);
        }
        else
        {
            execution = await runner.RunAsync(
                    catalog,
                    suiteId,
                    testIds,
                    cancellationToken,
                    workflowId)
                .ConfigureAwait(false);
        }

        RimTestArtifactFreshness? artifactFreshness = freshnessTransaction?.Freshness;
        if (freshnessTransaction?.Success == true &&
            artifactFreshness is not null)
        {
            (execution, artifactFreshness) = EnforceArtifactGeneration(
                execution,
                artifactFreshness,
                workflowId);
        }

        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            execution,
            ElapsedMilliseconds(started),
            selectionStatus,
            selectionErrorCode,
            fallbackSuite,
            workflowId,
            artifactFreshness);
        WriteJson(stdout, result);
        return SuiteExitCodeFor(result.Status);
    }

    private static ArtifactFreshnessTransactionRequest? CreateArtifactFreshnessRequest(
        CliRequest request,
        IReadOnlyList<string> changedPaths,
        string? workflowId)
    {
        if (!SourceChangeClassifier.IsBuildRelevant(changedPaths))
        {
            return null;
        }

        string sourceFingerprint = string.Empty;
        WorktreeFingerprint.TryCompute(
            AffectedGitRoot(request),
            changedPaths,
            out sourceFingerprint,
            out _);
        return new ArtifactFreshnessTransactionRequest(
            request.DevBridgeProject ?? string.Empty,
            AffectedGitRoot(request),
            changedPaths,
            sourceFingerprint,
            workflowId);
    }

    private static IDevBridgeModDevelopmentAdapter CreateDevelopmentAdapter(
        CliRequest request)
    {
        DevBridgeAdapterOptions bridgeOptions = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeModDevelopmentAdapter(
            new SystemDevBridgeProcessTransport(),
            DevBridgeModDevelopmentAdapterOptions.Discover(bridgeOptions.RootPath));
    }

    private static CatalogSuiteExecutionResult ArtifactFailureExecution(
        string suiteId,
        IReadOnlyList<string> testIds,
        DevBridgeAdapterStatus status,
        string? workflowId)
    {
        string[] ordered = testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (status.Outcome == DevBridgeOutcomeKind.Cancelled)
        {
            return new CatalogSuiteExecutionResult(
                suiteId,
                ordered.Length == 0
                    ? []
                    : [RimTestResultFactory.Cancelled(ordered[0], workflowId: workflowId)],
                Math.Max(0, ordered.Length - 1),
                Cancelled: true);
        }

        string errorCode = status.ErrorCode ?? "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN";
        return new CatalogSuiteExecutionResult(
            suiteId,
            ordered
                .Select(testId => RimTestResultFactory.ArtifactFreshnessFailure(
                    testId,
                    errorCode,
                    workflowId))
                .ToArray(),
            0,
            Cancelled: false);
    }

    private static (
        CatalogSuiteExecutionResult Execution,
        RimTestArtifactFreshness Freshness) EnforceArtifactGeneration(
        CatalogSuiteExecutionResult execution,
        RimTestArtifactFreshness freshness,
        string? workflowId)
    {
        if (!freshness.Generation.HasValue)
        {
            string[] ids = execution.Tests
                .Where(static test => test.Status == "pass")
                .Select(static test => test.Test)
                .ToArray();
            return (
                ReplacePassingTestsWithFreshnessFailures(
                    execution,
                    ids,
                    "RIMTEST_ARTIFACT_GENERATION_UNKNOWN",
                    workflowId),
                freshness with
                {
                    LoadedArtifactFreshnessProven = false,
                    ErrorCode = "RIMTEST_ARTIFACT_GENERATION_UNKNOWN"
                });
        }

        string[] mismatched = execution.Tests
            .Where(test => test.Status == "pass" &&
                (!test.Generation.HasValue ||
                 test.Generation.Value != freshness.Generation.Value))
            .Select(static test => test.Test)
            .ToArray();
        if (mismatched.Length == 0)
        {
            return (execution, freshness);
        }

        return (
            ReplacePassingTestsWithFreshnessFailures(
                execution,
                mismatched,
                "RIMTEST_ARTIFACT_GENERATION_MISMATCH",
                workflowId),
            freshness with
            {
                LoadedArtifactFreshnessProven = false,
                ErrorCode = "RIMTEST_ARTIFACT_GENERATION_MISMATCH"
            });
    }

    private static CatalogSuiteExecutionResult ReplacePassingTestsWithFreshnessFailures(
        CatalogSuiteExecutionResult execution,
        IReadOnlyCollection<string> testIds,
        string errorCode,
        string? workflowId) =>
        new(
            execution.SuiteId,
            execution.Tests
                .Select(test => test.Status == "pass" && testIds.Contains(test.Test)
                    ? RimTestResultFactory.ArtifactFreshnessFailure(
                        test.Test,
                        errorCode,
                        workflowId)
                    : test)
                .ToArray(),
            execution.Skipped,
            execution.Cancelled);

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

    private static string CapabilityOutcomeName(DevBridgeCapabilityOutcome outcome) =>
        outcome switch
        {
            DevBridgeCapabilityOutcome.Unavailable => "unavailable",
            DevBridgeCapabilityOutcome.InfrastructureFailure => "infrastructureFailure",
            DevBridgeCapabilityOutcome.Timeout => "timeout",
            DevBridgeCapabilityOutcome.Cancelled => "cancelled",
            DevBridgeCapabilityOutcome.MalformedResponse => "malformedResponse",
            DevBridgeCapabilityOutcome.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };

    private static int CapabilityExitCodeFor(DevBridgeCapabilityOutcome outcome) =>
        outcome switch
        {
            DevBridgeCapabilityOutcome.Timeout => CliExitCodes.Timeout,
            DevBridgeCapabilityOutcome.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };

    private static string UiOutcomeName(DevBridgeUiOutcome outcome) =>
        outcome switch
        {
            DevBridgeUiOutcome.Unavailable => "unavailable",
            DevBridgeUiOutcome.TargetNotFound => "targetNotFound",
            DevBridgeUiOutcome.VisualReadinessFailure => "visualReadinessFailure",
            DevBridgeUiOutcome.InvalidRequest => "invalidRequest",
            DevBridgeUiOutcome.InfrastructureFailure => "infrastructureFailure",
            DevBridgeUiOutcome.Timeout => "timeout",
            DevBridgeUiOutcome.Cancelled => "cancelled",
            DevBridgeUiOutcome.MalformedResponse => "malformedResponse",
            DevBridgeUiOutcome.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };

    private static int UiExitCodeFor(DevBridgeUiOutcome outcome) =>
        outcome switch
        {
            DevBridgeUiOutcome.InvalidRequest => CliExitCodes.InvalidInput,
            DevBridgeUiOutcome.TargetNotFound => CliExitCodes.NotFound,
            DevBridgeUiOutcome.Timeout => CliExitCodes.Timeout,
            DevBridgeUiOutcome.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };

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
