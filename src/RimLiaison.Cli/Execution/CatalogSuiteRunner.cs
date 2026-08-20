using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Profiling;
using RimLiaison.Recovery;
using RimLiaison.Results;

namespace RimLiaison.Execution;

public sealed record CatalogSuiteExecutionResult(
    string SuiteId,
    IReadOnlyList<RimTestResult> Tests,
    int Skipped,
    bool Cancelled,
    CatalogSuiteReuseSummary? Reuse = null,
    CatalogSuiteFailFastSummary? FailFast = null,
    IReadOnlyList<RimTestPrerequisiteRecovery>? PrerequisiteRecovery = null,
    RimTestCleanupSummary? Cleanup = null);

public sealed record CatalogSuiteFailFastSummary(
    [property: JsonPropertyName("firstFailure")] string? FirstFailure,
    [property: JsonPropertyName("notLaunched")] int NotLaunched,
    [property: JsonPropertyName("validationCompleted")] bool ValidationCompleted,
    [property: JsonPropertyName("historicalOrdering")]
    CatalogSuiteFailFastOrderingSummary? HistoricalOrdering = null);

public sealed record CatalogSuiteReuseMismatch(
    string TestId,
    string Reason,
    int? ExpectedGeneration,
    int? ActualGeneration,
    string? ExpectedLeaseId,
    string? ActualLeaseId,
    bool? RestartRequired,
    int? LaunchesConsumed,
    string? ErrorCode);

public sealed record CatalogSuiteReuseSummary(
    int Selected,
    int GroupsPlanned,
    int GroupsUsed,
    int GenerationsUsed,
    int FixtureResets,
    int Relaunches,
    string Status,
    string? ReuseInvalidatedAfter = null,
    string? ReuseInvalidationReason = null,
    string? FallbackReason = null,
    CatalogSuiteReuseMismatch? Mismatch = null,
    int GenerationsAvoided = 0,
    int RelaunchesAvoided = 0);

public sealed class CatalogSuiteRunner
{
    private readonly IDevBridgeRecipeAdapter recipeAdapter;
    private readonly CatalogTestExecutionService testExecutor;
    private readonly IDevBridgeLeaseAdapter? leaseAdapter;
    private readonly IDevBridgeFixtureResetAdapter? resetAdapter;
    private readonly IDevBridgeFreshGenerationAdapter? freshGenerationAdapter;

    public CatalogSuiteRunner(
        IDevBridgeRecipeAdapter recipeAdapter,
        CatalogTestExecutionService testExecutor,
        IDevBridgeLeaseAdapter? leaseAdapter = null,
        IDevBridgeFixtureResetAdapter? resetAdapter = null,
        IDevBridgeFreshGenerationAdapter? freshGenerationAdapter = null)
    {
        this.recipeAdapter = recipeAdapter ?? throw new ArgumentNullException(nameof(recipeAdapter));
        this.testExecutor = testExecutor ?? throw new ArgumentNullException(nameof(testExecutor));
        this.leaseAdapter = leaseAdapter;
        this.resetAdapter = resetAdapter;
        this.freshGenerationAdapter = freshGenerationAdapter;
    }

    public async Task<CatalogSuiteExecutionResult> RunAsync(
        CatalogDocument catalog,
        string suiteId,
        IReadOnlyList<string> testIds,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        bool failFast = false,
        string? sourceFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(testIds);

        string[] orderedTestIds = testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var results = new List<RimTestResult>();
        IReadOnlyDictionary<string, DevBridgeRecipeShowResult> recipeShows =
            orderedTestIds.Length > 1
                ? await LoadRecipeShowsAsync(catalog, orderedTestIds, cancellationToken)
                    .ConfigureAwait(false)
                : new Dictionary<string, DevBridgeRecipeShowResult>(StringComparer.Ordinal);
        Dictionary<string, CatalogSuiteRecipeProfile?> recipeProfiles =
            CreateRecipeProfiles(recipeShows);
        CatalogSuiteReusePlan reusePlan = CatalogSuiteReusePlanner.Plan(
            catalog,
            orderedTestIds,
            recipeProfiles);
        CatalogSuiteFailFastOrderingResult? historicalOrdering = failFast
            ? CatalogSuiteFailFastOrdering.Order(catalog, reusePlan)
            : null;
        EfficiencyProfiler.Active?.SetOrderingContext(
            historicalOrdering?.HistoryContext ??
            CatalogSuiteFailFastOrdering.BuildHistoryContext(catalog, reusePlan));
        string[] executionTestIds = (historicalOrdering?.ExecutionOrder ??
                reusePlan.ExecutionOrder)
            .ToArray();
        CatalogSuiteFailFastOrderingSummary? historicalOrderingSummary =
            historicalOrdering?.Summary;
        var reuse = new ReuseAccumulator(
            executionTestIds.Length,
            reusePlan.Groups.Count,
            reusePlan.FallbackReason);
        var prerequisiteRecovery = new List<RimTestPrerequisiteRecovery>();
        var groupsByTest = reusePlan.Groups
            .SelectMany(group => group.TestIds.Select(testId => (testId, group)))
            .ToDictionary(value => value.testId, value => value.group, StringComparer.Ordinal);
        CatalogSuiteReuseGroup? activeGroup = null;
        ReuseSession? session = null;
        bool needsFreshState = false;
        bool hasExecuted = false;
        int? lastGeneration = null;

        // A multi-test run is preflighted through DevBridge's existing plan
        // operation. The plan remains a lifecycle-owner preflight; explicit
        // catalog isolation metadata is the only permission RimLiaison uses to
        // hold a shared lease.
        if (executionTestIds.Length > 1)
        {
            foreach (string testId in executionTestIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Aggregate(
                        suiteId,
                        results,
                        executionTestIds.Length - results.Count,
                        cancelled: true,
                        failFast: CreateFailFastSummary(
                            failFast,
                            firstFailure: null,
                            notLaunched: executionTestIds.Length,
                            validationCompleted: false,
                            historicalOrdering: historicalOrderingSummary));
                }

                CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
                if (test is null)
                {
                    results.Add(RimTestResultFactory.Invalid(testId, "TEST_NOT_FOUND"));
                    continue;
                }

                DevBridgeRecipePlanResult plan = await TryPlanAsync(
                        test.Recipe,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (plan.Status.Outcome == DevBridgeOutcomeKind.Cancelled)
                {
                    results.Add(RimTestResultFactory.Cancelled(testId));
                    return Aggregate(
                        suiteId,
                        results,
                        executionTestIds.Length - results.Count,
                        cancelled: true,
                        failFast: CreateFailFastSummary(
                            failFast,
                            firstFailure: null,
                            notLaunched: executionTestIds.Length,
                            validationCompleted: false,
                            historicalOrdering: historicalOrderingSummary));
                }

                if (plan.Status.Outcome != DevBridgeOutcomeKind.Success ||
                    plan.Plan is null)
                {
                    results.Add(RimTestResultFactory.Infrastructure(
                        testId,
                        plan.Status.ErrorCode ?? "DEVBRIDGE_PLAN_FAILED"));
                }
            }

            if (results.Count > 0)
            {
                return Aggregate(
                    suiteId,
                    results,
                    executionTestIds.Length - results.Count,
                    cancelled: false,
                    failFast: CreateFailFastSummary(
                        failFast,
                        firstFailure: null,
                        notLaunched: executionTestIds.Length,
                        validationCompleted: false,
                        historicalOrdering: historicalOrderingSummary));
            }
        }

        bool cancelled = false;
        string? firstFailure = null;
        int notLaunched = 0;
        bool validationCompleted = true;
        try
        {
            for (int index = 0; index < executionTestIds.Length; index++)
            {
                string testId = executionTestIds[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    notLaunched = executionTestIds.Length - index;
                    validationCompleted = false;
                    break;
                }

                CatalogSuiteReuseGroup? group = groupsByTest.TryGetValue(testId, out CatalogSuiteReuseGroup? plannedGroup)
                    ? plannedGroup
                    : null;
                if (!ReferenceEquals(activeGroup, group))
                {
                    if (hasExecuted && group is not null)
                    {
                        // A different explicit reuse group has not proved that
                        // it can inherit the prior recipe's state.  Require a
                        // fresh generation before entering it.
                        needsFreshState = true;
                    }
                    await CloseSessionAsync(
                            session,
                            workflowId,
                            reuse,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    session = null;
                    activeGroup = group;
                }

                CatalogTest? test = CatalogNavigator.FindTest(catalog, testId);
                CatalogRecipeIsolation isolation = CatalogRecipeIsolationPolicy.Resolve(test);
                bool requiresFresh =
                    needsFreshState || CatalogRecipeIsolationPolicy.RequiresFreshGeneration(isolation);
                if (requiresFresh)
                {
                    await CloseSessionAsync(
                            session,
                            workflowId,
                            reuse,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    session = null;
                    activeGroup = null;
                    DevBridgeFreshGenerationResult fresh = await EnsureFreshGenerationAsync(
                            test?.Recipe ?? testId,
                            lastGeneration,
                            workflowId,
                            reuse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!fresh.IsUsable)
                    {
                        results.Add(RimTestResultFactory.Infrastructure(
                            testId,
                            fresh.Status.ErrorCode ?? "RIMTEST_FRESH_GENERATION_UNAVAILABLE",
                            workflowId: workflowId));
                        needsFreshState = true;
                        continue;
                    }

                    lastGeneration = fresh.Generation;
                    needsFreshState = false;
                    if (group is not null)
                    {
                        activeGroup = group;
                    }
                }

                if (group is not null && session is null)
                {
                    session = await OpenSessionAsync(
                            group,
                            test?.Recipe ?? testId,
                            workflowId,
                            reuse,
                            lastGeneration,
                            recipeShows,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (session is not null)
                    {
                        lastGeneration = session.Generation;
                    }
                }

                bool sessionInvalidatedBeforeExecution = false;
                if (session is not null)
                {
                    DevBridgeLeaseResult renewed = await RenewSessionAsync(
                            session,
                            workflowId,
                            reuse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!renewed.IsUsable || renewed.Generation != session.Generation)
                    {
                        InvalidateSession(
                            session,
                            testId,
                            renewed.Status.ErrorCode ?? "RIMTEST_REUSE_LEASE_INVALID",
                            reuse);
                        await CloseSessionAsync(
                                session,
                                workflowId,
                                reuse,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        session = null;
                        activeGroup = group;
                        needsFreshState = true;
                        sessionInvalidatedBeforeExecution = true;
                    }
                    else if (session.TestsRun > 0 &&
                        CatalogRecipeIsolationPolicy.RequiresResetBetweenRecipes(isolation))
                    {
                        DevBridgeResetResult reset = await ResetBetweenRecipesAsync(
                                group!,
                                session,
                                workflowId,
                                reuse,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!reset.IsUsable || reset.Generation != session.Generation ||
                            !string.Equals(reset.LeaseId, session.LeaseId, StringComparison.Ordinal))
                        {
                            InvalidateSession(
                                session,
                                testId,
                                reset.Status.ErrorCode ?? "RIMTEST_RESET_NOT_VERIFIED",
                                reuse);
                            await CloseSessionAsync(
                                    session,
                                    workflowId,
                                    reuse,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            session = null;
                            activeGroup = group;
                            needsFreshState = true;
                            sessionInvalidatedBeforeExecution = true;
                        }
                        else
                        {
                            session.FixtureResets++;
                            reuse.FixtureResets++;
                        }
                    }
                }

                if (sessionInvalidatedBeforeExecution)
                {
                    DevBridgeFreshGenerationResult fresh = await EnsureFreshGenerationAsync(
                            test?.Recipe ?? testId,
                            lastGeneration,
                            workflowId,
                            reuse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!fresh.IsUsable)
                    {
                        results.Add(RimTestResultFactory.Infrastructure(
                            testId,
                            fresh.Status.ErrorCode ?? "RIMTEST_REUSE_RECOVERY_FAILED",
                            workflowId: workflowId));
                        continue;
                    }

                    lastGeneration = fresh.Generation;
                    needsFreshState = false;
                    if (group is not null)
                    {
                        session = await OpenSessionAsync(
                                group,
                                test?.Recipe ?? testId,
                                workflowId,
                                reuse,
                                lastGeneration,
                                recipeShows,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (session is not null)
                        {
                            lastGeneration = session.Generation;
                        }
                    }
                }

                bool wasSharingExistingGeneration = session is not null &&
                    session.TestsRun > 0;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    DevBridgeRecipeExecutionContext? executionContext =
                        session is null && string.IsNullOrWhiteSpace(sourceFingerprint)
                            ? null
                            : new DevBridgeRecipeExecutionContext(
                                session?.LeaseId,
                                sourceFingerprint);
                    ExecutionRecoveryResult executed = await RunWithLeaseRecoveryAsync(
                            catalog,
                            testId,
                            started,
                            cancellationToken,
                            workflowId,
                            executionContext,
                            session,
                            reuse)
                        .ConfigureAwait(false);
                    CatalogTestExecutionResult execution = executed.Execution;
                    if (executed.Recovery is not null)
                    {
                        prerequisiteRecovery.Add(executed.Recovery);
                    }
                    reuse.Observe(execution.Run.RecipeResult);
                    lastGeneration = execution.Run.RecipeResult.Generation ?? lastGeneration;

                    string? sessionMismatch = session is null
                        ? null
                        : SessionMismatchReason(execution.Run.RecipeResult, session);
                    if (session is not null && sessionMismatch is not null)
                    {
                        CatalogSuiteReuseMismatch mismatch = new(
                            testId,
                            sessionMismatch,
                            session.Generation,
                            execution.Run.RecipeResult.Generation,
                            session.LeaseId,
                            execution.Run.RecipeResult.LeaseId,
                            execution.Run.RecipeResult.RestartRequired,
                            execution.Run.RecipeResult.LaunchesConsumed,
                            execution.Run.RecipeResult.Status.ErrorCode);
                        InvalidateSession(
                            session,
                            testId,
                            sessionMismatch,
                            reuse,
                            mismatch);
                        execution = execution with
                        {
                            Result = ResultForSessionMismatch(
                                execution,
                                sessionMismatch,
                                workflowId)
                        };
                        needsFreshState = true;
                    }

                    if (wasSharingExistingGeneration &&
                        sessionMismatch is null &&
                        string.Equals(execution.Result.Status, "pass", StringComparison.Ordinal))
                    {
                        reuse.ObserveSuccessfulReuse();
                    }

                    results.Add(execution.Result);
                    hasExecuted = true;
                    if (session is not null)
                    {
                        session.TestsRun++;
                        if (!string.Equals(execution.Result.Status, "pass", StringComparison.Ordinal))
                        {
                            InvalidateSession(
                                session,
                                testId,
                                "RIMTEST_REUSE_INVALIDATED_AFTER_FAILURE",
                                reuse);
                            needsFreshState = true;
                        }
                    }

                    if (string.Equals(execution.Result.Status, "cancelled", StringComparison.Ordinal))
                    {
                        notLaunched = executionTestIds.Length - index - 1;
                        validationCompleted = false;
                        cancelled = true;
                        break;
                    }

                    // A test failure is the only ordinary outcome that may
                    // activate fail-fast. Infrastructure, invalid, and
                    // ownership/cancellation outcomes retain the normal
                    // conservative aggregation path. The operation above is
                    // already complete; fail-fast never cancels it.
                    if (failFast &&
                        string.Equals(execution.Result.Status, "fail", StringComparison.Ordinal) &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        firstFailure = testId;
                        notLaunched = executionTestIds.Length - index - 1;
                        validationCompleted = notLaunched == 0;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    notLaunched = executionTestIds.Length - index;
                    validationCompleted = false;
                    cancelled = true;
                    break;
                }
                catch (Exception)
                {
                    results.Add(RimTestResultFactory.Infrastructure(
                        testId,
                        "RIMTEST_CHILD_EXECUTION_FAILED",
                        workflowId: workflowId));
                    if (session is not null)
                    {
                        InvalidateSession(session, testId, "RIMTEST_REUSE_INVALIDATED_AFTER_FAILURE", reuse);
                        needsFreshState = true;
                    }
                }
            }
        }
        finally
        {
            await CloseSessionAsync(
                    session,
                    workflowId,
                    reuse,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return Aggregate(
            suiteId,
            results,
            executionTestIds.Length - results.Count,
            cancelled,
            reuse.ToSummary(),
            CreateFailFastSummary(
                failFast,
                firstFailure,
                notLaunched,
                validationCompleted,
                historicalOrderingSummary),
            prerequisiteRecovery.Count == 0 ? null : prerequisiteRecovery,
            reuse.ToCleanup());
    }

    private async Task<ExecutionRecoveryResult> RunWithLeaseRecoveryAsync(
        CatalogDocument catalog,
        string testId,
        long started,
        CancellationToken cancellationToken,
        string? workflowId,
        DevBridgeRecipeExecutionContext? executionContext,
        ReuseSession? session,
        ReuseAccumulator reuse)
    {
        CatalogTestExecutionResult first = await testExecutor.RunAsync(
                catalog,
                testId,
                started,
                cancellationToken,
                workflowId,
                executionContext)
            .ConfigureAwait(false);
        if (!IsLeaseRequired(first.Run.RecipeResult.Status))
        {
            return new ExecutionRecoveryResult(first, null);
        }

        if (session is not null && executionContext is not null)
        {
            CatalogTestExecutionResult retry = await testExecutor.RunAsync(
                    catalog,
                    testId,
                    started,
                    cancellationToken,
                    workflowId,
                    executionContext)
                .ConfigureAwait(false);
            return new ExecutionRecoveryResult(
                retry,
                RecoveryEvent(
                    "rimbridge-lease",
                    retry.Run.RecipeResult.Status.Outcome is
                        DevBridgeOutcomeKind.Success or DevBridgeOutcomeKind.TestFailure
                        ? PrerequisiteRecoveryState.Recovered
                        : PrerequisiteRecoveryState.RecoveryFailed,
                    1,
                    retry.Run.RecipeResult.Status.ErrorCode,
                    "retry-with-existing-compatible-lease"));
        }

        if (leaseAdapter is null)
        {
            reuse.FallbackReason ??= "DEVBRIDGE_LEASE_ADAPTER_UNAVAILABLE";
            return new ExecutionRecoveryResult(
                first,
                RecoveryEvent(
                    "rimbridge-lease",
                    PrerequisiteRecoveryState.RecoveryRequired,
                    0,
                    first.Run.RecipeResult.Status.ErrorCode,
                    "acquire-compatible-lease"));
        }

        DevBridgeLeaseResult lease;
        try
        {
            lease = await leaseAdapter.BeginLeaseAsync(
                    workflowId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ExecutionRecoveryResult(
                first,
                RecoveryEvent(
                    "rimbridge-lease",
                    PrerequisiteRecoveryState.Unavailable,
                    1,
                    "DEVBRIDGE_LEASE_ACQUIRE_FAILED",
                    "acquire-compatible-lease"));
        }

        if (!lease.IsUsable)
        {
            reuse.FallbackReason ??= lease.Status.ErrorCode ??
                "DEVBRIDGE_LEASE_ACQUIRE_FAILED";
            return new ExecutionRecoveryResult(
                first,
                RecoveryEvent(
                    "rimbridge-lease",
                    LeaseRecoveryState(lease.Status),
                    1,
                    lease.Status.ErrorCode ?? first.Run.RecipeResult.Status.ErrorCode,
                    "acquire-compatible-lease"));
        }

        CatalogTestExecutionResult? retryResult = null;
        bool released = false;
        try
        {
            retryResult = await testExecutor.RunAsync(
                    catalog,
                    testId,
                    started,
                    cancellationToken,
                    workflowId,
                    new DevBridgeRecipeExecutionContext(
                        lease.LeaseId,
                        executionContext?.SourceFingerprint))
                .ConfigureAwait(false);
        }
        finally
        {
            reuse.MarkCleanupAttempted();
            try
            {
                DevBridgeLeaseResult end = await leaseAdapter.EndLeaseAsync(
                        lease.LeaseId!,
                        workflowId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                released = end.Status.IsSuccess;
                reuse.MarkLeaseReleased(released, end.Status.ErrorCode);
            }
            catch (Exception)
            {
                released = false;
                reuse.MarkLeaseReleased(false, "DEVBRIDGE_LEASE_RELEASE_FAILED");
            }
        }

        if (!released)
        {
            retryResult = retryResult! with
            {
                Result = RimTestResultFactory.Infrastructure(
                    testId,
                    "DEVBRIDGE_LEASE_RELEASE_FAILED",
                    retryResult!.Result.DurationMs,
                    workflowId)
            };
        }

        PrerequisiteRecoveryState state = released &&
            retryResult!.Run.RecipeResult.Status.Outcome is
                DevBridgeOutcomeKind.Success or DevBridgeOutcomeKind.TestFailure
            ? PrerequisiteRecoveryState.Recovered
            : PrerequisiteRecoveryState.RecoveryFailed;
        return new ExecutionRecoveryResult(
            retryResult!,
            RecoveryEvent(
                "rimbridge-lease",
                state,
                1,
                released
                    ? retryResult!.Run.RecipeResult.Status.ErrorCode
                    : "DEVBRIDGE_LEASE_RELEASE_FAILED",
                released
                    ? "retry-after-lease-acquisition"
                    : "release-recovered-lease"));
    }

    private static bool IsLeaseRequired(DevBridgeAdapterStatus status) =>
        string.Equals(
            status.ErrorCode,
            "RIMBRIDGE_LEASE_REQUIRED",
            StringComparison.Ordinal);

    private static PrerequisiteRecoveryState LeaseRecoveryState(
        DevBridgeAdapterStatus status)
    {
        if (status.ErrorCode?.Contains("CONTEND", StringComparison.OrdinalIgnoreCase) == true ||
            status.ErrorCode?.Contains("HELD", StringComparison.OrdinalIgnoreCase) == true ||
            status.ErrorCode?.Contains("OWNER", StringComparison.OrdinalIgnoreCase) == true)
        {
            return PrerequisiteRecoveryState.Contended;
        }

        return status.Outcome is DevBridgeOutcomeKind.InfrastructureFailure or
            DevBridgeOutcomeKind.Timeout or
            DevBridgeOutcomeKind.MalformedResponse
            ? PrerequisiteRecoveryState.Unavailable
            : PrerequisiteRecoveryState.RecoveryFailed;
    }

    private static RimTestPrerequisiteRecovery RecoveryEvent(
        string component,
        PrerequisiteRecoveryState state,
        int attempts,
        string? errorCode,
        string action) =>
        new(
            component,
            state.ToWireName(),
            attempts,
            errorCode,
            action);

    private sealed record ExecutionRecoveryResult(
        CatalogTestExecutionResult Execution,
        RimTestPrerequisiteRecovery? Recovery);

    private async Task<DevBridgeRecipePlanResult> TryPlanAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProfilerActivity.ObserveAsync(
                    "recipe.plan",
                    "devbridge",
                    () => recipeAdapter.PlanAsync(recipeId, cancellationToken),
                    (activity, result) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            result.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : result.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            result.Status.ErrorCode);
                        ProfilerActivity.SetCounts(activity, items: result.Plan?.Steps.Count);
                    },
                    phase: "plan",
                    target: recipeId,
                    scope: "recipe")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"),
                null);
        }
        catch (Exception)
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_PLAN_FAILED"),
                null);
        }
    }

    private async Task<IReadOnlyDictionary<string, DevBridgeRecipeShowResult>>
        LoadRecipeShowsAsync(
            CatalogDocument catalog,
            IReadOnlyList<string> orderedTestIds,
            CancellationToken cancellationToken)
    {
        var shows = new Dictionary<string, DevBridgeRecipeShowResult>(
            StringComparer.Ordinal);
        foreach (string recipeId in orderedTestIds
                     .Select(testId => CatalogNavigator.FindTest(catalog, testId)?.Recipe)
                     .Where(static recipe => !string.IsNullOrWhiteSpace(recipe))
                     .Select(static recipe => recipe!)
                     .Distinct(StringComparer.Ordinal))
        {
            shows[recipeId] = await TryShowAsync(recipeId, cancellationToken)
                .ConfigureAwait(false);
        }

        return shows;
    }

    private async Task<DevBridgeRecipeShowResult> TryShowAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProfilerActivity.ObserveAsync(
                    "recipe.show",
                    "devbridge",
                    () => recipeAdapter.ShowAsync(recipeId, cancellationToken),
                    (activity, result) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            result.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : result.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            result.Status.ErrorCode);
                        ProfilerActivity.SetCounts(
                            activity,
                            items: result.Definition?.ValueKind == JsonValueKind.Object ? 1 : 0);
                    },
                    phase: "plan",
                    target: recipeId,
                    scope: "recipe")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"),
                null);
        }
        catch (Exception)
        {
            return new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_REUSE_SHOW_FAILED"),
                null);
        }
    }

    private static Dictionary<string, CatalogSuiteRecipeProfile?> CreateRecipeProfiles(
        IReadOnlyDictionary<string, DevBridgeRecipeShowResult> shows)
    {
        var profiles = new Dictionary<string, CatalogSuiteRecipeProfile?>(
            StringComparer.Ordinal);
        foreach ((string recipeId, DevBridgeRecipeShowResult show) in shows)
        {
            profiles[recipeId] = show.Status.IsSuccess &&
                show.Definition is JsonElement definition &&
                CatalogSuiteReusePlanner.TryCreateRecipeProfile(
                    definition,
                    out CatalogSuiteRecipeProfile? profile)
                ? profile
                : null;
        }

        return profiles;
    }

    private static CatalogSuiteExecutionResult Aggregate(
        string suiteId,
        IReadOnlyList<RimTestResult> results,
        int skipped,
        bool cancelled,
        CatalogSuiteReuseSummary? reuse = null,
        CatalogSuiteFailFastSummary? failFast = null,
        IReadOnlyList<RimTestPrerequisiteRecovery>? prerequisiteRecovery = null,
        RimTestCleanupSummary? cleanup = null) =>
        new(
            suiteId,
            results,
            Math.Max(0, skipped),
            cancelled,
            reuse,
            failFast,
            prerequisiteRecovery,
            cleanup);

    private static CatalogSuiteFailFastSummary? CreateFailFastSummary(
        bool enabled,
        string? firstFailure,
        int notLaunched,
        bool validationCompleted,
        CatalogSuiteFailFastOrderingSummary? historicalOrdering = null) =>
        enabled
            ? new(
                firstFailure,
                Math.Max(0, notLaunched),
                validationCompleted,
                historicalOrdering)
            : null;

    private async Task<ReuseSession?> OpenSessionAsync(
        CatalogSuiteReuseGroup group,
        string recipeId,
        string? workflowId,
        ReuseAccumulator reuse,
        int? previousGeneration,
        IReadOnlyDictionary<string, DevBridgeRecipeShowResult> recipeShows,
        CancellationToken cancellationToken)
    {
        if (leaseAdapter is null)
        {
            reuse.FallbackReason ??= "DEVBRIDGE_LEASE_ADAPTER_UNAVAILABLE";
            return null;
        }

        // The suite was preflighted already, but this bounded plan is the
        // authoritative check immediately before taking a reusable lease.
        // A supplied lease cannot authorize the recipe's own restart path.
        // Prepare the requested generation first when the plan says one is
        // needed, then acquire the lease on the resulting READY generation.
        // Unknown or malformed planning remains a safe per-recipe fallback.
        DevBridgeRecipePlanResult plan;
        try
        {
            plan = await recipeAdapter.PlanAsync(recipeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            reuse.FallbackReason ??= "DEVBRIDGE_REUSE_PLAN_FAILED";
            return null;
        }

        if (plan.Status.Outcome != DevBridgeOutcomeKind.Success || plan.Plan is null)
        {
            reuse.FallbackReason ??= plan.Status.ErrorCode ?? "DEVBRIDGE_REUSE_PLAN_FAILED";
            return null;
        }

        DevBridgeRecipeShowResult show = recipeShows.TryGetValue(
                recipeId,
                out DevBridgeRecipeShowResult? cachedShow)
            ? cachedShow
            : await TryShowAsync(recipeId, cancellationToken).ConfigureAwait(false);

        if (!show.Status.IsSuccess)
        {
            reuse.FallbackReason ??= show.Status.ErrorCode ?? "DEVBRIDGE_REUSE_SHOW_FAILED";
            return null;
        }

        if (show.Definition is JsonElement definition &&
            definition.ValueKind == JsonValueKind.Object &&
            RecipeAllowsInGameMutation(definition) &&
            group.Mode != CatalogRecipeIsolationMode.FixtureResettable)
        {
            reuse.FallbackReason ??= "RIMTEST_RECIPE_MUTATION_NOT_SHAREABLE";
            return null;
        }

        int? preparedGeneration = null;
        if (!plan.Plan.AlreadySatisfied)
        {
            if (freshGenerationAdapter is null)
            {
                reuse.FallbackReason ??= "DEVBRIDGE_FRESH_GENERATION_ADAPTER_UNAVAILABLE";
                return null;
            }

            DevBridgeFreshGenerationResult prepared;
            try
            {
                prepared = await ProfilerActivity.ObserveAsync(
                        "generation.prepare",
                        "lifecycle",
                        () => freshGenerationAdapter
                            .EnsureFreshGenerationAsync(
                                recipeId,
                                previousGeneration,
                                workflowId,
                                cancellationToken),
                        (activity, result) =>
                        {
                            ProfilerActivity.SetOutcome(
                                activity,
                                result.Status.Outcome == DevBridgeOutcomeKind.Success
                                    ? "success"
                                    : result.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                        ? "cancelled"
                                        : "failure",
                                result.Status.ErrorCode);
                            ProfilerActivity.SetGeneration(activity, result.Generation);
                            ProfilerActivity.SetStateChanged(activity, true);
                        },
                        phase: "generation",
                        target: recipeId,
                        scope: "fresh-generation")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                reuse.FallbackReason ??= "DEVBRIDGE_REUSE_GENERATION_PREPARE_FAILED";
                return null;
            }
            if (!prepared.IsUsable ||
                previousGeneration.HasValue && prepared.Generation <= previousGeneration.Value)
            {
                reuse.FallbackReason ??= prepared.Status.ErrorCode ??
                    "DEVBRIDGE_REUSE_GENERATION_PREPARE_FAILED";
                return null;
            }

            reuse.ObserveGeneration(prepared.Generation);
            reuse.Relaunches += Math.Max(0, prepared.LaunchesConsumed);
            preparedGeneration = prepared.Generation;
        }

        DevBridgeLeaseResult lease;
        try
        {
            lease = await ProfilerActivity.ObserveAsync(
                    "lease.begin",
                    "lifecycle",
                    () => leaseAdapter.BeginLeaseAsync(
                        workflowId,
                        cancellationToken),
                    (activity, result) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            result.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : result.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            result.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, result.Generation);
                        ProfilerActivity.SetStateChanged(activity, true);
                    },
                    phase: "lease",
                    scope: "begin")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            reuse.FallbackReason ??= "DEVBRIDGE_LEASE_ACQUIRE_FAILED";
            return null;
        }
        if (!lease.IsUsable)
        {
            reuse.FallbackReason ??= lease.Status.ErrorCode ?? "DEVBRIDGE_LEASE_ACQUIRE_FAILED";
            return null;
        }

        if (preparedGeneration.HasValue && lease.Generation != preparedGeneration)
        {
            reuse.FallbackReason ??= "RIMTEST_REUSE_GENERATION_CHANGED_BEFORE_LEASE";
            try
            {
                await leaseAdapter.EndLeaseAsync(
                        lease.LeaseId!,
                        workflowId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original generation mismatch is already fail-closed;
                // cleanup is best effort but remains owner-scoped.
            }
            return null;
        }

        reuse.GroupsUsed++;
        reuse.ObserveGeneration(lease.Generation);
        return new ReuseSession(group, lease.LeaseId!, lease.Generation!.Value);
    }

    private static bool RecipeAllowsInGameMutation(JsonElement definition)
    {
        // DevBridge2's recipe/show contract uses allowInGameMutation. Accept
        // the older plural spelling only as an additional conservative signal;
        // either explicit true value makes a non-resettable reuse group unsafe.
        return IsTrue(definition, "allowInGameMutation") ||
            IsTrue(definition, "allowsInGameMutation");
    }

    private static bool IsTrue(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True &&
        value.GetBoolean();

    private async Task<DevBridgeLeaseResult> RenewSessionAsync(
        ReuseSession session,
        string? workflowId,
        ReuseAccumulator reuse,
        CancellationToken cancellationToken)
    {
        if (session.TestsRun == 0)
        {
            return new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                session.LeaseId,
                session.Generation);
        }

        if (leaseAdapter is null)
        {
            return new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_LEASE_ADAPTER_UNAVAILABLE"),
                session.LeaseId,
                null);
        }

        DevBridgeLeaseResult result;
        try
        {
            result = await ProfilerActivity.ObserveAsync(
                    "lease.renew",
                    "lifecycle",
                    () => leaseAdapter.RenewLeaseAsync(
                        session.LeaseId,
                        workflowId,
                        cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : value.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Generation);
                        ProfilerActivity.SetStateChanged(activity, false);
                    },
                    phase: "lease",
                    scope: "renew")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_LEASE_RENEW_FAILED"),
                session.LeaseId,
                null);
        }
        reuse.ObserveGeneration(result.Generation);
        return result;
    }

    private async Task<DevBridgeResetResult> ResetBetweenRecipesAsync(
        CatalogSuiteReuseGroup group,
        ReuseSession session,
        string? workflowId,
        ReuseAccumulator reuse,
        CancellationToken cancellationToken)
    {
        if (resetAdapter is null || string.IsNullOrWhiteSpace(group.ResetRecipe))
        {
            reuse.FallbackReason ??= "RIMTEST_RESET_ADAPTER_UNAVAILABLE";
            return new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_RESET_ADAPTER_UNAVAILABLE"),
                null,
                session.LeaseId);
        }

        DevBridgeResetResult result;
        try
        {
            result = await ProfilerActivity.ObserveAsync(
                    "fixture.reset",
                    "lifecycle",
                    () => resetAdapter.ResetAsync(
                        group.ResetRecipe,
                        session.LeaseId,
                        session.Generation,
                        workflowId,
                        cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : value.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Generation);
                        ProfilerActivity.SetStateChanged(activity, true);
                    },
                    phase: "fixture-reset",
                    target: group.ResetRecipe,
                    scope: "fixture")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_RESET_NOT_VERIFIED"),
                null,
                session.LeaseId);
        }
        reuse.ObserveGeneration(result.Generation);
        return result;
    }

    private async Task<DevBridgeFreshGenerationResult> EnsureFreshGenerationAsync(
        string testId,
        int? previousGeneration,
        string? workflowId,
        ReuseAccumulator reuse,
        CancellationToken cancellationToken)
    {
        if (freshGenerationAdapter is null)
        {
            reuse.FallbackReason ??= "RIMTEST_FRESH_GENERATION_ADAPTER_UNAVAILABLE";
            return new DevBridgeFreshGenerationResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_FRESH_GENERATION_ADAPTER_UNAVAILABLE"),
                null);
        }

        DevBridgeFreshGenerationResult result;
        try
        {
            result = await ProfilerActivity.ObserveAsync(
                    "generation.ensure-fresh",
                    "lifecycle",
                    () => freshGenerationAdapter
                        .EnsureFreshGenerationAsync(
                            testId,
                            previousGeneration,
                            workflowId,
                            cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : value.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Generation);
                        ProfilerActivity.SetStateChanged(activity, true);
                    },
                    phase: "generation",
                    target: testId,
                    scope: "fresh-generation")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new DevBridgeFreshGenerationResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_FRESH_GENERATION_UNAVAILABLE"),
                null);
        }
        if (result.IsUsable && previousGeneration.HasValue &&
            result.Generation <= previousGeneration.Value)
        {
            return result with
            {
                Status = new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_FRESH_GENERATION_NOT_NEW",
                    "DevBridge did not establish a generation newer than the prior state.")
            };
        }

        reuse.ObserveGeneration(result.Generation);
        reuse.Relaunches += Math.Max(0, result.LaunchesConsumed);
        return result;
    }

    private static string? SessionMismatchReason(
        DevBridgeRecipeRunResult result,
        ReuseSession session)
    {
        bool expectedOutcome = result.Status.Outcome is
            DevBridgeOutcomeKind.Success or DevBridgeOutcomeKind.TestFailure;
        if (!expectedOutcome)
        {
            return result.Status.ErrorCode ??
                (result.RestartRequired == true
                    ? "RIMTEST_REUSE_RESTART_REQUIRED"
                    : (result.LaunchesConsumed ?? 0) != 0
                        ? "RIMTEST_REUSE_LAUNCHES_CONSUMED"
                        : "RIMTEST_REUSE_DEVBRIDGE_REFUSAL");
        }

        if (result.Generation != session.Generation)
        {
            return "RIMTEST_REUSE_GENERATION_MISMATCH";
        }

        if (!string.Equals(result.LeaseId, session.LeaseId, StringComparison.Ordinal))
        {
            return "RIMTEST_REUSE_LEASE_MISMATCH";
        }

        if (result.RestartRequired == true)
        {
            return "RIMTEST_REUSE_RESTART_REQUIRED";
        }

        if ((result.LaunchesConsumed ?? 0) != 0)
        {
            return "RIMTEST_REUSE_LAUNCHES_CONSUMED";
        }

        return null;
    }

    private static RimTestResult ResultForSessionMismatch(
        CatalogTestExecutionResult execution,
        string reason,
        string? workflowId)
    {
        DevBridgeRecipeRunResult run = execution.Run.RecipeResult;
        if (run.Status.Outcome is not
                (DevBridgeOutcomeKind.Success or DevBridgeOutcomeKind.TestFailure) &&
            !string.IsNullOrWhiteSpace(run.Status.ErrorCode))
        {
            return RimTestResultFactory.FromRun(
                execution.Run.TestId,
                run,
                execution.Result.DurationMs,
                workflowId);
        }

        return RimTestResultFactory.Infrastructure(
            execution.Run.TestId,
            reason,
            execution.Result.DurationMs,
            workflowId);
    }

    private async Task CloseSessionAsync(
        ReuseSession? session,
        string? workflowId,
        ReuseAccumulator reuse,
        CancellationToken cancellationToken)
    {
        if (session is null || leaseAdapter is null || session.Closed)
        {
            return;
        }

        session.Closed = true;
        reuse.MarkCleanupAttempted();
        DevBridgeLeaseResult result;
        try
        {
            result = await ProfilerActivity.ObserveAsync(
                    "lease.end",
                    "lifecycle",
                    () => leaseAdapter.EndLeaseAsync(
                        session.LeaseId,
                        workflowId,
                        cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome == DevBridgeOutcomeKind.Success
                                ? "success"
                                : value.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                    ? "cancelled"
                                    : "failure",
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Generation);
                        ProfilerActivity.SetStateChanged(activity, true);
                    },
                    phase: "lease",
                    scope: "end")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            reuse.MarkLeaseReleased(false, "DEVBRIDGE_LEASE_RELEASE_FAILED");
            InvalidateSession(
                session,
                session.TestsRun == 0 ? null : session.Group.TestIds[Math.Min(
                    session.TestsRun - 1,
                    session.Group.TestIds.Count - 1)],
                "DEVBRIDGE_LEASE_RELEASE_FAILED",
                reuse);
            return;
        }
        if (!result.Status.IsSuccess)
        {
            reuse.MarkLeaseReleased(false, result.Status.ErrorCode ?? "DEVBRIDGE_LEASE_RELEASE_FAILED");
            InvalidateSession(
                session,
                session.TestsRun == 0 ? null : session.Group.TestIds[Math.Min(
                    session.TestsRun - 1,
                    session.Group.TestIds.Count - 1)],
                result.Status.ErrorCode ?? "DEVBRIDGE_LEASE_RELEASE_FAILED",
                reuse);
        }
        else
        {
            reuse.MarkLeaseReleased(true, null);
        }
    }

    private static void InvalidateSession(
        ReuseSession session,
        string? testId,
        string reason,
        ReuseAccumulator reuse,
        CatalogSuiteReuseMismatch? mismatch = null)
    {
        reuse.Invalidate(testId, reason, mismatch);
        session.Invalidated = true;
    }

    private sealed class ReuseSession
    {
        internal ReuseSession(CatalogSuiteReuseGroup group, string leaseId, int generation)
        {
            Group = group;
            LeaseId = leaseId;
            Generation = generation;
        }

        internal CatalogSuiteReuseGroup Group { get; }
        internal string LeaseId { get; }
        internal int Generation { get; }
        internal int TestsRun { get; set; }
        internal int FixtureResets { get; set; }
        internal bool Invalidated { get; set; }
        internal bool Closed { get; set; }
    }

    private sealed class ReuseAccumulator
    {
        private readonly HashSet<int> generations = [];

        internal ReuseAccumulator(
            int selected,
            int groupsPlanned,
            string? fallbackReason = null)
        {
            Selected = selected;
            GroupsPlanned = groupsPlanned;
            FallbackReason = fallbackReason;
        }

        internal int Selected { get; }
        internal int GroupsPlanned { get; }
        internal int GroupsUsed { get; set; }
        internal int FixtureResets { get; set; }
        internal int Relaunches { get; set; }
        internal int GenerationsAvoided { get; private set; }
        internal int RelaunchesAvoided { get; private set; }
        internal string? ReuseInvalidatedAfter { get; private set; }
        internal string? ReuseInvalidationReason { get; private set; }
        internal string? FallbackReason { get; set; }
        internal CatalogSuiteReuseMismatch? Mismatch { get; private set; }
        internal bool CleanupAttempted { get; private set; }
        internal bool LeaseReleased { get; private set; }
        internal string? CleanupErrorCode { get; private set; }

        internal void Observe(DevBridgeRecipeRunResult result)
        {
            ObserveGeneration(result.Generation);
            Relaunches += Math.Max(0, result.LaunchesConsumed ?? 0);
        }

        internal void ObserveGeneration(int? generation)
        {
            if (generation is > 0)
            {
                generations.Add(generation.Value);
            }
        }

        internal void ObserveSuccessfulReuse()
        {
            GenerationsAvoided++;
            RelaunchesAvoided++;
        }

        internal void Invalidate(
            string? testId,
            string reason,
            CatalogSuiteReuseMismatch? mismatch = null)
        {
            ReuseInvalidatedAfter ??= testId;
            ReuseInvalidationReason ??= reason;
            Mismatch ??= mismatch;
        }

        internal void MarkCleanupAttempted() => CleanupAttempted = true;

        internal void MarkLeaseReleased(bool released, string? errorCode)
        {
            CleanupAttempted = true;
            if (released)
            {
                LeaseReleased = true;
                return;
            }

            CleanupErrorCode ??= errorCode ?? "DEVBRIDGE_LEASE_RELEASE_FAILED";
        }

        internal RimTestCleanupSummary? ToCleanup()
        {
            if (!CleanupAttempted)
            {
                return null;
            }

            bool failed = !string.IsNullOrWhiteSpace(CleanupErrorCode);
            return new RimTestCleanupSummary
            {
                Status = failed ? "FAILED" : "RESTORED",
                LeaseReleased = LeaseReleased,
                TemporaryStateCleared = !failed,
                ErrorCode = CleanupErrorCode
            };
        }

        internal CatalogSuiteReuseSummary? ToSummary()
        {
            if (GroupsPlanned == 0 && string.IsNullOrWhiteSpace(ReuseInvalidatedAfter) &&
                string.IsNullOrWhiteSpace(FallbackReason))
            {
                return null;
            }

            string status = !string.IsNullOrWhiteSpace(ReuseInvalidatedAfter)
                ? "invalidated"
                : GroupsUsed > 0
                    ? "used"
                    : "notUsed";
            return new CatalogSuiteReuseSummary(
                Selected,
                GroupsPlanned,
                GroupsUsed,
                generations.Count,
                FixtureResets,
                Relaunches,
                status,
                ReuseInvalidatedAfter,
                ReuseInvalidationReason,
                FallbackReason,
                Mismatch,
                GenerationsAvoided,
                RelaunchesAvoided);
        }
    }
}
