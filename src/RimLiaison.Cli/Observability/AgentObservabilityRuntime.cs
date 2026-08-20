using System.Collections;
using System.Diagnostics;
using System.Text.Json;

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

    public IAgentObservabilityStore Store => store;

    public AgentObservabilitySession CreateAgent(
        string modId,
        string modName,
        string? agentId = null)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentObservabilityRun));
        }

        lock (sessions)
        {
            AgentObservabilitySession? existing = sessions.FirstOrDefault(session =>
                string.Equals(session.ModId, modId, StringComparison.Ordinal));
            if (existing is not null)
            {
                // The product model is one user-facing agent per mod. Stages
                // and operations belong to this session rather than creating
                // role-specific agents for the same mod.
                return existing;
            }

            AgentObservabilitySession session = new(
                this,
                store,
                telemetry,
                modId,
                modName,
                agentId ?? "agent-" + Guid.NewGuid().ToString("N"),
                runActivity);
            sessions.Add(session);
            return session;
        }
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
        string agentId,
        Activity? runActivity)
    {
        this.run = run;
        this.store = store;
        this.telemetry = telemetry;
        ValidateIdentity(modId, modName, agentId);
        AgentId = agentId;
        ModId = modId;
        ModName = modName;
        startedTimestamp = Stopwatch.GetTimestamp();
        long startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshot = new AgentSnapshot
        {
            AgentId = AgentId,
            RunId = run.RunId,
            ModId = ModId,
            ModName = ModName,
            Status = AgentStatus.Created,
            CurrentStage = DevelopmentStage.Analysis,
            CurrentActivity = "created",
            StartTime = startTime
        };
        store.RegisterAgent(snapshot);
        try
        {
            agentActivity = telemetry.StartActivity(
                "rimliaison.mod-agent",
                runActivity is null ? null : runActivity.Context,
                new Dictionary<string, object?>
                {
                    [AgentObservabilityTags.RunId] = run.RunId,
                    [AgentObservabilityTags.AgentId] = AgentId,
                    [AgentObservabilityTags.ModId] = ModId,
                    [AgentObservabilityTags.ModName] = ModName,
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
            new { activity = "created" });
    }

    public string AgentId { get; }

    public string RunId => run.RunId;

    public string ModId { get; }

    public string ModName { get; }

    public AgentSnapshot Snapshot => snapshot;

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
            CurrentActivity = "complete",
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CompletionState = AgentCompletionState.Succeeded,
            FailureState = false,
            FailureSummary = null
        });
        AgentEvent? result = Record(
            DevelopmentStage.Complete,
            AgentEventTypes.AgentCompleted,
            summary ?? "Mod agent completed.",
            new { outcome = "success" });
        StopActivity("success");
        return result;
    }

    public AgentEvent? Fail(
        string summary,
        string? errorCode = null,
        AgentCompletionState completionState = AgentCompletionState.Failed)
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
            CurrentActivity = "failed",
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CompletionState = completionState,
            FailureState = true,
            FailureSummary = boundedSummary
        });
        AgentEvent? result = Record(
            snapshot.CurrentStage,
            AgentEventTypes.AgentFailed,
            boundedSummary,
            new
            {
                outcome = completionState == AgentCompletionState.Cancelled
                    ? "cancelled"
                    : "failure",
                errorCode
            });
        StopActivity(completionState == AgentCompletionState.Cancelled ? "cancelled" : "failure");
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
                SpanId: correlation?.SpanId.ToString()));
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
        snapshot = value;
        try
        {
            store.UpdateAgent(snapshot);
        }
        catch
        {
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

    private static void ValidateIdentity(string modId, string modName, string agentId)
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
                    [AgentObservabilityTags.ModId] = session.ModId,
                    [AgentObservabilityTags.ModName] = session.ModName,
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
