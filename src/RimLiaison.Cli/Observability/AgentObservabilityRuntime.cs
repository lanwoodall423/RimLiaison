using System.Collections;
using System.Diagnostics;
using System.Text.Json;

using RimLiaison.Validation;

namespace RimLiaison.Observability;

public static class AgentObservabilityRuntime
{
    private static readonly AsyncLocal<AgentObservabilitySession?> CurrentSlot = new();

    public static AgentObservabilitySession? Current => CurrentSlot.Value;

    public static AgentOperationScope? BeginOperation(
        string operationType,
        string activityName,
        DevelopmentStage stage,
        string? operationKey = null,
        object? data = null) =>
        Current?.BeginOperation(operationType, activityName, stage, operationKey, data);

    public static AgentEvent? Record(
        DevelopmentStage stage,
        string type,
        string summary,
        object? data = null) =>
        Current?.Record(stage, type, summary, data);

    public static AgentDiagnosticEvidenceReference? PersistEvidence(
        string kind,
        string? content,
        bool truncated = false) =>
        Current?.PersistEvidence(kind, content, truncated);

    internal static IDisposable Activate(AgentObservabilitySession session)
    {
        AgentObservabilitySession? previous = CurrentSlot.Value;
        CurrentSlot.Value = session;
        return new Activation(previous);
    }

    private sealed class Activation(AgentObservabilitySession? previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                CurrentSlot.Value = previous;
            }
        }
    }
}

public sealed class AgentObservabilityRun : IDisposable
{
    private readonly IAgentObservabilityStore store;
    private readonly IAgentObservabilityTelemetry telemetry;
    private readonly bool ownsTelemetry;
    private readonly Activity? runActivity;
    private readonly IDisposable issueSubscription;
    private readonly List<AgentObservabilitySession> sessions = [];
    private readonly long startedTimestamp;
    private int disposed;

    public AgentObservabilityRun(
        string runId,
        IAgentObservabilityStore store,
        IAgentObservabilityTelemetry? telemetry = null)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("A run id is required.", nameof(runId));
        }

        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.telemetry = telemetry ?? CreateTelemetry();
        ownsTelemetry = telemetry is null;
        RunId = runId.Trim();
        startedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            runActivity = this.telemetry.StartActivity(
                "rimliaison.run",
                tags: new Dictionary<string, object?>
                {
                    [AgentObservabilityTags.RunId] = RunId
                });
        }
        catch
        {
            runActivity = null;
        }
        issueSubscription = store.Subscribe(notification =>
        {
            if (notification.Issue is { } issue &&
                string.Equals(issue.RunId, RunId, StringComparison.Ordinal))
            {
                this.telemetry.RecordIssue(issue.Category, issue.Recovered);
            }
        });
    }

    public string RunId { get; }

    public string SessionId => "session-" + RunId;

    public IAgentObservabilityStore Store => store;

    public AgentObservabilitySession CreateAgent(
        string modId,
        string modName,
        string? agentId = null,
        string? logicalAgentId = null,
        string? sessionId = null,
        string workloadKind = "production",
        string toolchainState = "promoted",
        string? qualificationProfile = null,
        ObservabilityEntityIdentity? entityIdentity = null)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentObservabilityRun));
        }

        ObservabilityEntityIdentity resolvedEntityIdentity =
            IsFixtureWorkload(workloadKind, qualificationProfile)
                ? ObservabilityEntityIdentity.ForFixture(
                    "qualification",
                    "Qualification fixture")
                : entityIdentity ??
                    ObservabilityEntityIdentityResolver.ForProducer(modId, modName);
        lock (sessions)
        {
            if (agentId is null && logicalAgentId is null && sessionId is null)
            {
                AgentObservabilitySession? existing = sessions.FirstOrDefault(session =>
                    string.Equals(session.EntityType, resolvedEntityIdentity.EntityType, StringComparison.Ordinal) &&
                    string.Equals(session.CanonicalEntityId, resolvedEntityIdentity.CanonicalEntityId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return existing;
                }
            }

            string resolvedAgentId = agentId ?? "agent-" + Guid.NewGuid().ToString("N");
            string? resolvedLogicalAgentId = string.IsNullOrWhiteSpace(logicalAgentId)
                ? null
                : logicalAgentId.Trim();
            string resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
                ? SessionId
                : sessionId.Trim();
            AgentObservabilitySession? logicalExisting = sessions.FirstOrDefault(session =>
                string.Equals(session.EntityType, resolvedEntityIdentity.EntityType, StringComparison.Ordinal) &&
                string.Equals(session.CanonicalEntityId, resolvedEntityIdentity.CanonicalEntityId, StringComparison.Ordinal) &&
                string.Equals(session.LogicalAgentId, resolvedLogicalAgentId, StringComparison.Ordinal) &&
                string.Equals(session.SessionId, resolvedSessionId, StringComparison.Ordinal) &&
                (resolvedLogicalAgentId is not null ||
                    string.Equals(session.AgentId, resolvedAgentId, StringComparison.Ordinal)));
            if (logicalExisting is not null)
            {
                return logicalExisting;
            }

            string resolvedModId = SessionModId(resolvedEntityIdentity, modId);
            string resolvedModName = resolvedEntityIdentity.DisplayName;
            AgentObservabilitySession session = new(
                this,
                store,
                telemetry,
                resolvedModId,
                resolvedModName,
                resolvedEntityIdentity,
                resolvedAgentId,
                resolvedLogicalAgentId,
                resolvedSessionId,
                workloadKind,
                toolchainState,
                qualificationProfile,
                runActivity);
            sessions.Add(session);
            return session;
        }
    }

    private static bool IsFixtureWorkload(
        string workloadKind,
        string? qualificationProfile) =>
        !string.IsNullOrWhiteSpace(qualificationProfile) ||
        workloadKind.Trim().ToLowerInvariant() is
            "qualification" or "fixture" or "test";
    private static string SessionModId(
        ObservabilityEntityIdentity identity,
        string fallback)
    {
        if (identity.EntityType == ObservabilityEntityTypes.Tool)
        {
            return identity.DisplayName;
        }

        if (identity.EntityType is ObservabilityEntityTypes.Fixture or
            ObservabilityEntityTypes.Test)
        {
            int separator = identity.CanonicalEntityId.IndexOf(':');
            return separator >= 0
                ? identity.CanonicalEntityId[(separator + 1)..]
                : identity.CanonicalEntityId;
        }

        return fallback;
    }

    internal long ElapsedMilliseconds =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    internal void RecordAgentDuration(
        DevelopmentStage stage,
        string outcome,
        long durationMilliseconds) =>
        telemetry.RecordAgentDuration(stage, outcome, durationMilliseconds);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        AgentObservabilitySession[] current;
        lock (sessions)
        {
            current = sessions.ToArray();
        }
        foreach (AgentObservabilitySession session in current)
        {
            session.Dispose();
        }

        try
        {
            runActivity?.Stop();
        }
        catch
        {
        }
        issueSubscription.Dispose();
        if (ownsTelemetry)
        {
            telemetry.Dispose();
        }
    }

    private static IAgentObservabilityTelemetry CreateTelemetry()
    {
        try
        {
            return new OpenTelemetryAgentTelemetry();
        }
        catch
        {
            return new NoopAgentObservabilityTelemetry();
        }
    }
}

public sealed class AgentObservabilitySession : IDisposable
{
    private readonly AgentObservabilityRun run;
    private readonly IAgentObservabilityStore store;
    private readonly IAgentObservabilityTelemetry telemetry;
    private readonly Activity? agentActivity;
    private readonly long startedTimestamp;
    private AgentSnapshot snapshot;
    private int disposed;
    private int started;
    internal AgentObservabilitySession(
        AgentObservabilityRun run,
        IAgentObservabilityStore store,
        IAgentObservabilityTelemetry telemetry,
        string modId,
        string modName,
        ObservabilityEntityIdentity entityIdentity,
        string agentId,
        string? logicalAgentId,
        string sessionId,
        string workloadKind,
        string toolchainState,
        string? qualificationProfile,
        Activity? runActivity)
    {
        this.run = run;
        this.store = store;
        this.telemetry = telemetry;
        ValidateIdentity(modId, modName, agentId, logicalAgentId);
        AgentId = agentId;
        LogicalAgentId = logicalAgentId;
        SessionId = sessionId;
        ModId = modId;
        ModName = modName;
        EntityType = entityIdentity.EntityType;
        CanonicalEntityId = entityIdentity.CanonicalEntityId;
        DisplayName = entityIdentity.DisplayName;
        long startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        startedTimestamp = Stopwatch.GetTimestamp();
        snapshot = new AgentSnapshot
        {
            AgentId = AgentId,
            LogicalAgentId = LogicalAgentId,
            SessionId = SessionId,
            RunId = run.RunId,
            ModId = ModId,
            ModName = ModName,
            EntityType = EntityType,
            CanonicalEntityId = CanonicalEntityId,
            DisplayName = DisplayName,
            WorkloadKind = workloadKind,
            ToolchainState = toolchainState,
            QualificationProfile = qualificationProfile,
            Status = AgentStatus.Created,
            CurrentStage = DevelopmentStage.Analysis,
            CurrentOperation = "created",
            CurrentActivity = "created",
            BlockingState = "none",
            StartTime = startTime
        };
        snapshot = store.RegisterAgent(snapshot);
        try
        {
            agentActivity = telemetry.StartActivity(
                "rimliaison.mod-agent",
                runActivity is null ? null : runActivity.Context,
                new Dictionary<string, object?>
                {
                    [AgentObservabilityTags.RunId] = run.RunId,
                    [AgentObservabilityTags.AgentId] = AgentId,
                    [AgentObservabilityTags.LogicalAgentId] = LogicalAgentId,
                    [AgentObservabilityTags.ModId] = ModId,
                    [AgentObservabilityTags.ModName] = ModName,
                    [AgentObservabilityTags.EntityType] = EntityType,
                    [AgentObservabilityTags.CanonicalEntityId] = CanonicalEntityId,
                    [AgentObservabilityTags.Stage] = "analysis"
                });
        }
        catch
        {
            agentActivity = null;
        }
        Record(
            DevelopmentStage.Analysis,
            AgentEventTypes.AgentCreated,
            "Mod agent created.",
            new { activity = "created", sessionId = SessionId });
    }

    public string AgentId { get; }

    public string? LogicalAgentId { get; }

    public string RunId => run.RunId;

    public string SessionId { get; }


    public string EntityType { get; }

    public string CanonicalEntityId { get; }

    public string DisplayName { get; }
    public string ModId { get; }

    public string ModName { get; }

    public AgentSnapshot Snapshot => snapshot;

    public IAgentObservabilityStore Store => store;

    public IDisposable Activate() => AgentObservabilityRuntime.Activate(this);

    public AgentEvent? Start(string? activity = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)
        {
            return null;
        }

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return null;
        }

        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Running,
            CurrentActivity = AgentObservabilityData.BoundText(activity ?? "starting", 256)
        });
        return Record(
            snapshot.CurrentStage,
            AgentEventTypes.AgentStarted,
            "Mod agent started.",
            new { activity = snapshot.CurrentActivity });
    }

    public AgentEvent? SetStage(
        DevelopmentStage stage,
        string? activity = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)

        {
            return null;
        }

        if (snapshot.CurrentStage == stage && activity is null)
        {
            return null;
        }

        DevelopmentStage previous = snapshot.CurrentStage;
        UpdateSnapshot(snapshot with
        {
            CurrentStage = stage,
            CurrentActivity = AgentObservabilityData.BoundText(
                activity ?? stage.ToString(),
                256),
            Status = snapshot.Status == AgentStatus.Created
                ? AgentStatus.Running
                : snapshot.Status
        });
        return Record(
            stage,
            AgentEventTypes.StageChanged,
            "Lifecycle stage changed to " + stage.ToString().ToLowerInvariant() + ".",
            new
            {
                previousStage = previous.ToString().ToLowerInvariant(),
                stage = stage.ToString().ToLowerInvariant(),
                activity = snapshot.CurrentActivity
            });
    }

    public void SetActivity(string activity)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal || string.IsNullOrWhiteSpace(activity))
        {
            return;
        }

        UpdateSnapshot(snapshot with
        {
            CurrentActivity = AgentObservabilityData.BoundText(activity, 256)
        });
    }

    public AgentEvent? SetProductionState(
        DevelopmentStage stage,
        string operation,
        string blockingState = "none",
        string? completionResult = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)
        {
            return null;
        }

        string boundedOperation = AgentObservabilityData.BoundText(operation, 256);
        string boundedBlockingState = AgentObservabilityData.BoundText(
            string.IsNullOrWhiteSpace(blockingState) ? "none" : blockingState,
            64);
        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Running,
            CurrentStage = stage,
            CurrentOperation = boundedOperation,
            CurrentActivity = boundedOperation,
            BlockingState = boundedBlockingState,
            CompletionResult = completionResult
        });
        return Record(
            stage,
            AgentEventTypes.ProductionStateChanged,
            "Production state changed.",
            new
            {
                sessionId = SessionId,
                stage = stage.ToString().ToLowerInvariant(),
                operation = boundedOperation,
                blockingState = boundedBlockingState,
                completionResult
            });
    }

    public AgentEvent? Waiting(
        string? activity = null,
        TimeSpan? duration = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)
        {
            return null;
        }

        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Waiting,
            CurrentActivity = AgentObservabilityData.BoundText(activity ?? "waiting", 256)
        });
        return Record(
            snapshot.CurrentStage,
            AgentEventTypes.AgentWaiting,
            "Mod agent is waiting.",
            new
            {
                activity = snapshot.CurrentActivity,
                durationMs = duration.HasValue
                    ? Math.Max(0, (long)duration.Value.TotalMilliseconds)
                    : (long?)null
            });
    }

    public AgentEvent? Resumed(string? activity = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)
        {
            return null;
        }

        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Running,
            CurrentActivity = AgentObservabilityData.BoundText(activity ?? "resumed", 256)
        });
        return Record(
            snapshot.CurrentStage,
            AgentEventTypes.AgentResumed,
            "Mod agent resumed.",
            new { activity = snapshot.CurrentActivity });
    }

    public AgentEvent? Complete(string? summary = null)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            snapshot.Status is AgentStatus.Completed or AgentStatus.Failed)
        {
            return null;
        }

        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Completed,
            CurrentStage = DevelopmentStage.Complete,
            CurrentOperation = "complete",
            CurrentActivity = "complete",
            BlockingState = "none",
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CompletionState = AgentCompletionState.Succeeded,
            CompletionResult = ValidationPolicySchema.Pass,
            FailureState = false,
            FailureSummary = null
        });
        AgentEvent? result = Record(
            DevelopmentStage.Complete,
            AgentEventTypes.AgentCompleted,
            summary ?? "Mod agent completed.",
            new { outcome = "success", validationStatus = ValidationPolicySchema.Pass });
        StopActivity("success");
        return result;
    }

    public AgentEvent? Complete(
        ValidationPolicyResult validation,
        string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(validation);
        return validation.Status switch
        {
            ValidationPolicySchema.Pass => Complete(summary),
            ValidationPolicySchema.ValidationIncomplete => Fail(
                summary ?? "Production is valid, but required validation is incomplete.",
                "VALIDATION_INCOMPLETE",
                AgentCompletionState.ValidationIncomplete),
            _ => Fail(
                summary ?? "Required validation failed; production is blocked.",
                "VALIDATION_REQUIRED_FAILED",
                AgentCompletionState.Failed)
        };
    }

    public AgentEvent? Fail(
        string summary,
        string? errorCode = null,
        AgentCompletionState completionState = AgentCompletionState.Failed,
        object? data = null)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            snapshot.Status is AgentStatus.Completed or AgentStatus.Failed)
        {
            return null;
        }

        string boundedSummary = AgentObservabilityData.BoundText(summary, 512);
        UpdateSnapshot(snapshot with
        {
            Status = AgentStatus.Failed,
            CurrentOperation = "failed",
            CurrentActivity = "failed",
            BlockingState = completionState == AgentCompletionState.ValidationIncomplete
                ? "required"
                : "none",
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CompletionState = completionState,
            CompletionResult = completionState == AgentCompletionState.ValidationIncomplete
                ? ValidationPolicySchema.ValidationIncomplete
                : ValidationPolicySchema.Fail,
            FailureState = true,
            FailureSummary = boundedSummary
        });
        AgentEvent? result = Record(
            snapshot.CurrentStage,
            AgentEventTypes.AgentFailed,
            boundedSummary,
            MergeData(
                data ?? new { },
                null,
                0,
                completionState switch
                {
                    AgentCompletionState.Cancelled => "cancelled",
                    AgentCompletionState.ValidationIncomplete => "validation-incomplete",
                    _ => "failure"
                },
                errorCode));
        StopActivity(completionState switch
        {
            AgentCompletionState.Cancelled => "cancelled",
            AgentCompletionState.ValidationIncomplete => "validation-incomplete",
            _ => "failure"
        });
        return result;
    }

    public AgentEvent? Record(
        DevelopmentStage stage,
        string type,
        string summary,
        object? data = null,
        Activity? activity = null)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            (IsTerminal && type is not AgentEventTypes.AgentCompleted and not AgentEventTypes.AgentFailed))
        {
            return null;
        }

        try
        {
            Activity? correlation = activity ?? agentActivity;
            AgentEvent eventRecord = store.AppendEvent(new AgentEventRequest(
                RunId,
                AgentId,
                ModId,
                stage,
                type,
                summary,
                data,
                TraceId: correlation?.TraceId.ToString(),
                SpanId: correlation?.SpanId.ToString(),
                LogicalAgentId: LogicalAgentId,
                SessionId: SessionId));
            try
            {
                telemetry.RecordEvent(type);
            }
            catch
            {
            }
            return eventRecord;
        }
        catch
        {
            // Instrumentation is intentionally non-fatal. The agent's owning
            // operation must continue even if the local store is unavailable.
            return null;
        }
    }

    public AgentDiagnosticEvidenceReference? PersistEvidence(
        string kind,
        string? content,
        bool truncated = false)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return store.PersistEvidence(kind, content, truncated);
        }
        catch
        {
            // Long-form evidence is best effort and must never change the
            // outcome of the command being observed.
            return null;
        }
    }

    public AgentOperationScope? BeginOperation(
        string operationType,
        string activityName,
        DevelopmentStage stage,
        string? operationKey = null,
        object? data = null)
    {
        if (Volatile.Read(ref disposed) != 0 || IsTerminal)
        {
            return null;
        }

        return new AgentOperationScope(
            this,
            telemetry,
            operationType,
            activityName,
            stage,
            operationKey,

            data,
            agentActivity);
    }

    public AgentEvent? RecordValidationRecommendation(
        string validationId,
        string summary,
        string recommendation,
        string? componentOwner = null,
        string? evidenceReference = null)
    {
        return Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationRecommendationRecorded,
            summary,
            new
            {
                operationKey = "validation:" + validationId,
                validationId,
                validationClassification = ValidationClassification.RECOMMENDED.ToString(),
                issueKind = "TOOLING_IMPROVEMENT",
                blocking = false,
                componentOwner,
                evidenceReference,
                recommendation
            });
    }

    public AgentEvent? RecordToolingRecommendation(
        string operationKey,
        string summary,
        string recommendation,
        string? componentOwner,
        string? evidenceReference,
        bool affectedCurrentTask,
        string priority = "normal",
        object? evidence = null)
    {
        return Record(
            snapshot.CurrentStage,
            AgentEventTypes.ValidationRecommendationRecorded,
            summary,
            new
            {
                operationKey,
                issueKind = "TOOLING_IMPROVEMENT",
                validationClassification = ValidationClassification.RECOMMENDED.ToString(),
                blocking = false,
                componentOwner,
                evidenceReference,
                affectedCurrentTask,
                priority,
                recommendation,
                evidence
            });
    }

    public AgentEvent? RecordToolingIncident(
        string operationKey,
        string summary,
        string? errorCode,
        string componentOwner,
        ValidationClassification validationClassification,
        string affectedValidation,
        string? evidenceReference,
        string recoveryState)
    {
        return Record(
            snapshot.CurrentStage,
            AgentEventTypes.ToolFailed,
            summary,
            new
            {
                operationKey,
                issueKind = "TOOLING_FAILURE",
                classification = "TOOLING_FAILURE",
                validationClassification = validationClassification.ToString(),
                blocking = validationClassification == ValidationClassification.REQUIRED,
                componentOwner,
                affectedValidation,
                evidenceReference,
                errorCode,
                recoveryState,
                automaticToolRepair = false,
                recommendation = "Triage the supporting-tool capability separately; do not modify the mod to compensate."
            });
    }

    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (snapshot.Status is not (AgentStatus.Completed or AgentStatus.Failed))
        {
            // An owned session must not disappear as a permanently running
            // agent when its caller abandons it without an explicit outcome.
            // Preserve that terminal fact in the same structured lifecycle
            // path used for ordinary failures and cancellation.
            Fail(
                "Mod agent ended without a completion result.",
                "RIMLIAISON_AGENT_ABANDONED",
                AgentCompletionState.Cancelled);
        }

        Interlocked.Exchange(ref disposed, 1);
    }

    internal AgentEvent? CompleteOperation(
        AgentOperationScope operation,
        string summary,
        object? data,
        Activity? activity,
        long durationMilliseconds)
    {
        var payload = MergeData(
            data,
            operation.OperationKey,
            durationMilliseconds,
            "success",
            null);
        AgentEvent? result = Record(
            operation.Stage,
            operation.OperationType + ".completed",
            summary,
            payload,
            activity);
        try
        {
            telemetry.RecordOperation(
                operation.OperationType,
                operation.Stage,
                "success",
                durationMilliseconds);
        }
        catch
        {
        }
        StopOperationActivity(activity, "success", null);
        return result;
    }

    internal AgentEvent? FailOperation(
        AgentOperationScope operation,
        string summary,
        string? errorCode,
        bool timeout,
        object? data,
        Activity? activity,
        long durationMilliseconds)
    {
        string outcome = timeout ? "timeout" : "failure";
        var payload = MergeData(
            data,
            operation.OperationKey,
            durationMilliseconds,
            outcome,
            errorCode);
        AgentEvent? result = Record(
            operation.Stage,
            operation.OperationType + (timeout ? ".timeout" : ".failed"),
            summary,
            payload,
            activity);
        try
        {
            telemetry.RecordOperation(
                operation.OperationType,
                operation.Stage,
                outcome,
                durationMilliseconds);
        }
        catch
        {
        }
        StopOperationActivity(activity, outcome, errorCode);
        return result;
    }

    internal static Dictionary<string, object?> MergeData(
        object? data,
        string? operationKey,
        long durationMilliseconds,
        string outcome,
        string? errorCode)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["durationMs"] = Math.Max(0, durationMilliseconds),
            ["outcome"] = outcome
        };
        if (!string.IsNullOrWhiteSpace(operationKey))
        {
            result["operationKey"] = operationKey;
        }
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            result["errorCode"] = AgentObservabilityData.BoundText(errorCode, 128);
        }

        if (data is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key && !string.IsNullOrWhiteSpace(key))
                {
                    result[key] = entry.Value;
                }
            }
        }
        else if (data is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        else if (data is not null)
        {
            result["value"] = data;
        }
        return result;
    }

    private void UpdateSnapshot(AgentSnapshot value)
    {
        try
        {
            snapshot = store.UpdateAgent(value);
        }
        catch
        {
            snapshot = value;
        }
    }

    private void StopActivity(string outcome)
    {
        long duration = Math.Max(
            0,
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        try
        {
            run.RecordAgentDuration(snapshot.CurrentStage, outcome, duration);
        }
        catch
        {
        }
        try
        {
            if (outcome is "success")
            {
                agentActivity?.SetStatus(ActivityStatusCode.Ok);
            }
            else if (outcome is "failure" or "timeout")
            {
                agentActivity?.SetStatus(ActivityStatusCode.Error, snapshot.FailureSummary);
            }
            agentActivity?.Stop();
        }
        catch
        {
        }
    }

    private static void StopOperationActivity(
        Activity? activity,
        string outcome,
        string? errorCode)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            activity.SetTag(AgentObservabilityTags.Outcome, outcome);
            if (!string.IsNullOrWhiteSpace(errorCode))
            {
                activity.SetTag("rimliaison.error.code", errorCode);
            }
            activity.SetStatus(
                outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            activity.Stop();
        }
        catch
        {
        }
    }

    private static void ValidateIdentity(
        string modId,
        string modName,
        string agentId,
        string? logicalAgentId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.Length > 256 || modId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded mod id is required.", nameof(modId));
        }
        if (string.IsNullOrWhiteSpace(modName) || modName.Length > 256 || modName.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded mod display name is required.", nameof(modName));
        }
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 256 || agentId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded agent id is required.", nameof(agentId));
        }
        if (logicalAgentId is not null &&
            (logicalAgentId.Length > 256 || logicalAgentId.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "A bounded logical agent id is required.",
                nameof(logicalAgentId));
        }
    }

    private bool IsTerminal =>
        snapshot.Status is AgentStatus.Completed or AgentStatus.Failed;
}

public sealed class AgentOperationScope : IDisposable
{
    private readonly AgentObservabilitySession session;
    private readonly IAgentObservabilityTelemetry telemetry;
    private readonly Activity? activity;
    private readonly long startedTimestamp;
    private int completed;
    internal AgentOperationScope(
        AgentObservabilitySession session,
        IAgentObservabilityTelemetry telemetry,
        string operationType,
        string activityName,
        DevelopmentStage stage,
        string? operationKey,
        object? data,
        Activity? parentActivity)
    {
        this.session = session;
        this.telemetry = telemetry;
        OperationType = NormalizeOperationType(operationType);
        ActivityName = AgentObservabilityData.BoundText(activityName, 256);
        Stage = stage;
        OperationKey = AgentObservabilityData.BoundIdentifier(operationKey, 256);
        startedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            activity = telemetry.StartActivity(
                "rimliaison." + NormalizeActivityName(ActivityName, OperationType),
                parentActivity is null ? null : parentActivity.Context,
                new Dictionary<string, object?>
                {
                    [AgentObservabilityTags.RunId] = session.RunId,
                    [AgentObservabilityTags.AgentId] = session.AgentId,
                    [AgentObservabilityTags.LogicalAgentId] = session.LogicalAgentId,
                    [AgentObservabilityTags.ModId] = session.ModId,
                    [AgentObservabilityTags.ModName] = session.ModName,
                    [AgentObservabilityTags.EntityType] = session.EntityType,
                    [AgentObservabilityTags.CanonicalEntityId] = session.CanonicalEntityId,
                    [AgentObservabilityTags.ToolName] = "RimLiaison",
                    [AgentObservabilityTags.Stage] = stage.ToString().ToLowerInvariant(),
                    [AgentObservabilityTags.OperationType] = OperationType
                });
        }
        catch
        {
            activity = null;
        }
        session.Record(
            Stage,
            OperationType + ".started",
            ActivityName + " started.",
            AgentObservabilitySession.MergeData(
                data,
                OperationKey,
                0,
                "started",
                null),
            activity);
    }

    public string OperationType { get; }

    public string ActivityName { get; }

    public DevelopmentStage Stage { get; }

    public string? OperationKey { get; }

    public AgentEvent? Complete(
        string? summary = null,
        object? data = null)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return null;
        }

        return session.CompleteOperation(
            this,
            summary ?? ActivityName + " completed.",
            data,
            activity,
            ElapsedMilliseconds());
    }

    public AgentEvent? Fail(
        string? summary = null,
        string? errorCode = null,
        object? data = null,
        bool timeout = false)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return null;
        }

        return session.FailOperation(
            this,
            summary ?? ActivityName + " failed.",
            errorCode,
            timeout,
            data,
            activity,
            ElapsedMilliseconds());
    }

    public void Dispose()
    {
        if (Volatile.Read(ref completed) == 0)
        {
            Fail(
                ActivityName + " ended without a completion result.",
                "RIMLIAISON_OPERATION_ABORTED");
        }
    }

    private long ElapsedMilliseconds() =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    private static string NormalizeOperationType(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 64 ||
            normalized.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
        {
            return "operation";
        }
        return normalized;
    }

    private static string NormalizeActivityName(string value, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new System.Text.StringBuilder(Math.Min(normalized.Length, 96));
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
            if (builder.Length >= 96)
            {
                break;
            }
        }
        return builder.Length == 0 ? fallback : builder.ToString();
    }
}
