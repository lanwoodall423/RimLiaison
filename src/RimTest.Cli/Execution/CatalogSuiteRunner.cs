using System.Diagnostics;
using System.Text.Json;
using RimTest.Catalog;
using RimTest.DevBridge;
using RimTest.Results;

namespace RimTest.Execution;

public sealed record CatalogSuiteExecutionResult(
    string SuiteId,
    IReadOnlyList<RimTestResult> Tests,
    int Skipped,
    bool Cancelled,
    CatalogSuiteReuseSummary? Reuse = null);

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
    string? FallbackReason = null);

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
        string? workflowId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(testIds);

        string[] orderedTestIds = testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var results = new List<RimTestResult>();
        CatalogSuiteReusePlan reusePlan = CatalogSuiteReusePlanner.Plan(catalog, orderedTestIds);
        var reuse = new ReuseAccumulator(orderedTestIds.Length, reusePlan.Groups.Count);
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
        // catalog isolation metadata is the only permission RimTest uses to
        // hold a shared lease.
        if (orderedTestIds.Length > 1)
        {
            foreach (string testId in orderedTestIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Aggregate(
                        suiteId,
                        results,
                        orderedTestIds.Length - results.Count,
                        cancelled: true);
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
                        orderedTestIds.Length - results.Count,
                        cancelled: true);
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
                    orderedTestIds.Length - results.Count,
                    cancelled: false);
            }
        }

        bool cancelled = false;
        try
        {
            for (int index = 0; index < orderedTestIds.Length; index++)
            {
                string testId = orderedTestIds[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
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
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (session is not null)
                        {
                            lastGeneration = session.Generation;
                        }
                    }
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    DevBridgeRecipeExecutionContext? executionContext = session is null
                        ? null
                        : new DevBridgeRecipeExecutionContext(session.LeaseId);
                    CatalogTestExecutionResult execution = await testExecutor.RunAsync(
                            catalog,
                            testId,
                            started,
                            cancellationToken,
                            workflowId,
                            executionContext)
                        .ConfigureAwait(false);
                    reuse.Observe(execution.Run.RecipeResult);
                    lastGeneration = execution.Run.RecipeResult.Generation ?? lastGeneration;

                    if (session is not null &&
                        !MatchesSession(execution.Run.RecipeResult, session))
                    {
                        InvalidateSession(
                            session,
                            testId,
                            "RIMTEST_REUSE_GENERATION_MISMATCH",
                            reuse);
                        execution = execution with
                        {
                            Result = RimTestResultFactory.Infrastructure(
                                testId,
                                "RIMTEST_REUSE_GENERATION_MISMATCH",
                                workflowId: workflowId)
                        };
                        needsFreshState = true;
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
                        cancelled = true;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
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
            orderedTestIds.Length - results.Count,
            cancelled,
            reuse.ToSummary());
    }

    private async Task<DevBridgeRecipePlanResult> TryPlanAsync(
        string recipeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await recipeAdapter.PlanAsync(recipeId, cancellationToken)
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

    private static CatalogSuiteExecutionResult Aggregate(
        string suiteId,
        IReadOnlyList<RimTestResult> results,
        int skipped,
        bool cancelled,
        CatalogSuiteReuseSummary? reuse = null) =>
        new(
            suiteId,
            results,
            Math.Max(0, skipped),
            cancelled,
            reuse);

    private async Task<ReuseSession?> OpenSessionAsync(
        CatalogSuiteReuseGroup group,
        string recipeId,
        string? workflowId,
        ReuseAccumulator reuse,
        int? previousGeneration,
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

        DevBridgeRecipeShowResult show;
        try
        {
            show = await recipeAdapter.ShowAsync(recipeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            reuse.FallbackReason ??= "DEVBRIDGE_REUSE_SHOW_FAILED";
            return null;
        }

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
                prepared = await freshGenerationAdapter
                    .EnsureFreshGenerationAsync(
                        recipeId,
                        previousGeneration,
                        workflowId,
                        cancellationToken)
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
            lease = await leaseAdapter.BeginLeaseAsync(
                    workflowId,
                    cancellationToken)
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
            result = await leaseAdapter.RenewLeaseAsync(
                    session.LeaseId,
                    workflowId,
                    cancellationToken)
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
            result = await resetAdapter.ResetAsync(
                    group.ResetRecipe,
                    session.LeaseId,
                    session.Generation,
                    workflowId,
                    cancellationToken)
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
            result = await freshGenerationAdapter
                .EnsureFreshGenerationAsync(
                    testId,
                    previousGeneration,
                    workflowId,
                    cancellationToken)
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

    private static bool MatchesSession(
        DevBridgeRecipeRunResult result,
        ReuseSession session) =>
        result.Generation == session.Generation &&
        string.Equals(result.LeaseId, session.LeaseId, StringComparison.Ordinal) &&
        result.RestartRequired != true &&
        (result.LaunchesConsumed ?? 0) == 0 &&
        result.Status.Outcome is DevBridgeOutcomeKind.Success or DevBridgeOutcomeKind.TestFailure;

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
        DevBridgeLeaseResult result;
        try
        {
            result = await leaseAdapter.EndLeaseAsync(
                    session.LeaseId,
                    workflowId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
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
            InvalidateSession(
                session,
                session.TestsRun == 0 ? null : session.Group.TestIds[Math.Min(
                    session.TestsRun - 1,
                    session.Group.TestIds.Count - 1)],
                result.Status.ErrorCode ?? "DEVBRIDGE_LEASE_RELEASE_FAILED",
                reuse);
        }
    }

    private static void InvalidateSession(
        ReuseSession session,
        string? testId,
        string reason,
        ReuseAccumulator reuse)
    {
        reuse.Invalidate(testId, reason);
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

        internal ReuseAccumulator(int selected, int groupsPlanned)
        {
            Selected = selected;
            GroupsPlanned = groupsPlanned;
        }

        internal int Selected { get; }
        internal int GroupsPlanned { get; }
        internal int GroupsUsed { get; set; }
        internal int FixtureResets { get; set; }
        internal int Relaunches { get; set; }
        internal string? ReuseInvalidatedAfter { get; private set; }
        internal string? ReuseInvalidationReason { get; private set; }
        internal string? FallbackReason { get; set; }

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

        internal void Invalidate(string? testId, string reason)
        {
            ReuseInvalidatedAfter ??= testId;
            ReuseInvalidationReason ??= reason;
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
                FallbackReason);
        }
    }
}
