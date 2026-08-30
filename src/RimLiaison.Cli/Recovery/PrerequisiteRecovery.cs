using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.DevBridge;
namespace RimLiaison.Recovery;

/// <summary>
/// Bounded state for a Tooling-owned runtime prerequisite.  The state is
/// intentionally separate from the application/test outcome: a recovered
/// prerequisite must not be mistaken for a test failure, and contention must
/// not be mistaken for a source defect.
/// </summary>
public enum PrerequisiteRecoveryState
{
    Ready,
    Recovered,
    RecoveryRequired,
    Contended,
    Unavailable,
    RecoveryFailed,
    TransitionRecoveryExhausted
}

public static class PrerequisiteRecoveryStateNames
{
    public static string ToWireName(this PrerequisiteRecoveryState state) => state switch
    {
        PrerequisiteRecoveryState.Ready => "ready",
        PrerequisiteRecoveryState.Recovered => "recovered",
        PrerequisiteRecoveryState.RecoveryRequired => "recoveryRequired",
        PrerequisiteRecoveryState.Contended => "contended",
        PrerequisiteRecoveryState.Unavailable => "unavailable",
        PrerequisiteRecoveryState.RecoveryFailed => "recoveryFailed",
        PrerequisiteRecoveryState.TransitionRecoveryExhausted => "transitionRecoveryExhausted",
        _ => "unavailable"
    };

    public static bool IsTerminalFailure(this PrerequisiteRecoveryState state) =>
        state is PrerequisiteRecoveryState.RecoveryRequired or
            PrerequisiteRecoveryState.Contended or
            PrerequisiteRecoveryState.Unavailable or
            PrerequisiteRecoveryState.RecoveryFailed or
            PrerequisiteRecoveryState.TransitionRecoveryExhausted;
}

/// <summary>
/// Compact, optional agent-facing evidence of prerequisite recovery.
/// Normal successful runs omit this field, preserving the existing output.
/// </summary>
public sealed record RimTestPrerequisiteRecovery(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("workflowId")] string? WorkflowId = null,
    [property: JsonPropertyName("generation")] int? Generation = null,
    [property: JsonPropertyName("identityMismatch")]
    DevBridgeIdentityMismatch? IdentityMismatch = null,
    [property: JsonPropertyName("checkpoint")]
    string? Checkpoint = null,
    [property: JsonPropertyName("elapsedRecoveryMs")]
    long? ElapsedRecoveryMilliseconds = null);

public static class PrerequisiteRecoveryProjection
{
    public static RimTestPrerequisiteRecovery FromStatus(
        string component,
        DevBridgeAdapterStatus status,
        string? workflowId = null,
        int? generation = null) =>
        new(
            component,
            status.RecoveryState.ToWireName(),
            Math.Max(0, status.RecoveryAttempts),
            status.ErrorCode,
            status.RecoveryAction,
            workflowId,
            generation);
}

public sealed record DevBridgeRecoveryAction(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("elapsedRecoveryMs")] long ElapsedRecoveryMilliseconds,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null);

public sealed record DevBridgeCapabilityRecoveryResult(
    PrerequisiteRecoveryState State,
    int Attempts,
    string? ErrorCode = null,
    string? Error = null,
    DevBridgeProcessResult? Process = null,
    string Trigger = "unknown",
    string HighestLevel = "RECONCILE",
    bool RimWorldRestarted = false,
    string FinalState = "UNAVAILABLE",
    IReadOnlyList<DevBridgeRecoveryAction>? Actions = null,
    long ElapsedRecoveryMilliseconds = 0)
{
    public bool Succeeded => State == PrerequisiteRecoveryState.Recovered;
}

public static class DevBridgeCapabilityRecovery
{
    private static readonly ConcurrentDictionary<
        string,
        Lazy<Task<DevBridgeCapabilityRecoveryResult>>> InFlight = [];

    public static async Task<DevBridgeCapabilityRecoveryResult> RecoverAsync(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options,
        string? workflowId,
        CancellationToken cancellationToken = default,
        string? triggerCode = null,
        ProductionCheckpoint checkpoint = ProductionCheckpoint.PreMutation)
    {
        string normalizedTrigger = string.IsNullOrWhiteSpace(triggerCode)
            ? "unknown"
            : triggerCode.Trim().ToUpperInvariant();
        string key = Path.GetFullPath(options.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant() + "|" + normalizedTrigger + "|" + checkpoint;
        Lazy<Task<DevBridgeCapabilityRecoveryResult>> lazy = InFlight.GetOrAdd(
            key,
            _ => new(
                () => RecoverCoreAsync(
                    transport,
                    options,
                    workflowId,
                    normalizedTrigger,
                    checkpoint),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<DevBridgeCapabilityRecoveryResult> task = lazy.Value;
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                InFlight.TryRemove(new KeyValuePair<
                    string,
                    Lazy<Task<DevBridgeCapabilityRecoveryResult>>>(key, lazy));
            }
        }
    }

    private static async Task<DevBridgeCapabilityRecoveryResult> RecoverCoreAsync(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options,
        string? workflowId,
        string triggerCode,
        ProductionCheckpoint checkpoint)
    {
        long started = Stopwatch.GetTimestamp();
        var actions = new List<DevBridgeRecoveryAction>();
        DevBridgeProcessResult? process = null;

        (process, string? doctorErrorCode, string? doctorError) =
            await ExecuteAsync(
                    transport,
                    options,
                    ["doctor", "--json"],
                    workflowId)
                .ConfigureAwait(false);
        AddAction(
            actions,
            "RECONCILE",
            "reconnect-and-reprobe",
            IsReadyProcess(process) ? "recovered" : "failed",
            doctorErrorCode,
            started);
        if (IsReadyProcess(process))
        {
            return Recovered(
                triggerCode,
                "RECONCILE",
                false,
                process,
                actions,
                started);
        }

        if (checkpoint != ProductionCheckpoint.PreMutation ||
            !NeedsEscalation(triggerCode))
        {
            return Failed(
                triggerCode,
                doctorErrorCode ?? "DEVBRIDGE_DOCTOR_FAILED",
                doctorError ?? Bound(process?.Stderr),
                process,
                "RECONCILE",
                actions,
                started);
        }

        process = await ExecuteCommandAsync(
                transport,
                options,
                ["coordinator", "recover", "--json"],
                workflowId)
            .ConfigureAwait(false);
        AddAction(
            actions,
            "COORDINATOR_RECYCLE",
            "recycle-exact-coordinator",
            IsSuccessfulProcess(process) ? "accepted" : "failed",
            ReadErrorCode(process?.Stdout),
            started);
        string? coordinatorErrorCode = ReadErrorCode(process?.Stdout);
        if (IsAmbiguousIdentity(coordinatorErrorCode))
        {
            return Failed(
                triggerCode,
                coordinatorErrorCode!,
                ReadError(process?.Stdout) ??
                    "Promoted and installed runtime identity is ambiguous.",
                process,
                "COORDINATOR_RECYCLE",
                actions,
                started);
        }
        (bool ready, DevBridgeProcessResult? readyProcess, string? readyCode, string? readyError) =
            await VerifyReadyAsync(transport, options, workflowId).ConfigureAwait(false);
        if (ready)
        {
            return Recovered(
                triggerCode,
                "COORDINATOR_RECYCLE",
                false,
                readyProcess ?? process!,
                actions,
                started);
        }

        process = await ExecuteCommandAsync(
                transport,
                options,
                ["coordinator", "shutdown", "--json"],
                workflowId)
            .ConfigureAwait(false);
        AddAction(
            actions,
            "FULL_RUNTIME_RESET",
            "shutdown-managed-runtime",
            IsSuccessfulProcess(process) ? "accepted" : "failed",
            ReadErrorCode(process?.Stdout),
            started);

        process = await ExecuteCommandAsync(
                transport,
                options,
                ["restart", "--json"],
                workflowId)
            .ConfigureAwait(false);
        bool rimWorldRestarted = IsSuccessfulProcess(process);
        AddAction(
            actions,
            "FULL_RUNTIME_RESET",
            "restart-managed-rimworld",
            rimWorldRestarted ? "accepted" : "failed",
            ReadErrorCode(process?.Stdout),
            started);

        process = await ExecuteCommandAsync(
                transport,
                options,
                ["wait-ready", "--json"],
                workflowId)
            .ConfigureAwait(false);
        AddAction(
            actions,
            "FULL_RUNTIME_RESET",
            "wait-for-ready-generation",
            IsReadyProcess(process) ? "recovered" : "failed",
            ReadErrorCode(process?.Stdout),
            started);
        (ready, readyProcess, readyCode, readyError) =
            await VerifyReadyAsync(transport, options, workflowId).ConfigureAwait(false);
        if (ready)
        {
            return Recovered(
                triggerCode,
                "FULL_RUNTIME_RESET",
                rimWorldRestarted,
                readyProcess ?? process!,
                actions,
                started);
        }

        return Failed(
            triggerCode,
            readyCode ?? ReadErrorCode(process?.Stdout) ?? "DEVBRIDGE_RUNTIME_NOT_READY",
            readyError ?? ReadError(process?.Stdout) ?? Bound(process?.Stderr),
            readyProcess ?? process,
            "FULL_RUNTIME_RESET",
            actions,
            started,
            rimWorldRestarted);
    }

    private static async Task<(DevBridgeProcessResult Process, string? ErrorCode, string? Error)>
        ExecuteAsync(
            IDevBridgeProcessTransport transport,
            DevBridgeAdapterOptions options,
            IReadOnlyList<string> command,
            string? workflowId)
    {
        DevBridgeProcessResult process = await ExecuteCommandAsync(
                transport,
                options,
                command,
                workflowId)
            .ConfigureAwait(false);
        return (process, ReadErrorCode(process.Stdout), ReadError(process.Stdout));
    }

    private static async Task<DevBridgeProcessResult> ExecuteCommandAsync(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options,
        IReadOnlyList<string> command,
        string? workflowId)
    {
        try
        {
            return await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        ["--root", options.RootPath, .. command],
                        options.ShowPlanTimeout,
                        Math.Min(options.MaxStdoutBytes, 512 * 1024),
                        Math.Min(options.MaxStderrBytes, 16 * 1024),
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new DevBridgeProcessResult(
                null,
                null,
                Bound(exception.Message),
                StartError: "DEVBRIDGE_RECOVERY_COMMAND_FAILED");
        }
    }

    private static async Task<(bool Ready, DevBridgeProcessResult? Process, string? ErrorCode, string? Error)>
        VerifyReadyAsync(
            IDevBridgeProcessTransport transport,
            DevBridgeAdapterOptions options,
            string? workflowId)
    {
        DevBridgeProcessResult status = await ExecuteCommandAsync(
                transport,
                options,
                ["status", "--json"],
                workflowId)
            .ConfigureAwait(false);
        if (!IsReadyProcess(status))
        {
            return (false, status, ReadErrorCode(status.Stdout), ReadError(status.Stdout) ?? Bound(status.Stderr));
        }

        DevBridgeProcessResult doctor = await ExecuteCommandAsync(
                transport,
                options,
                ["doctor", "--json"],
                workflowId)
            .ConfigureAwait(false);
        return (
            IsReadyProcess(doctor),
            doctor,
            ReadErrorCode(doctor.Stdout),
            ReadError(doctor.Stdout) ?? Bound(doctor.Stderr));
    }

    private static bool NeedsEscalation(string triggerCode) =>
        ProductionExecutionPolicy.RequiresPreMutationEscalation(triggerCode);

    private static bool IsAmbiguousIdentity(string? errorCode) =>
        errorCode is not null &&
        (errorCode.Contains("FINGERPRINT_MISMATCH", StringComparison.OrdinalIgnoreCase) ||
         errorCode.Contains("IDENTITY_AMBIGUOUS", StringComparison.OrdinalIgnoreCase) ||
         errorCode.Contains("OWNERSHIP_AMBIGUOUS", StringComparison.OrdinalIgnoreCase) ||
         errorCode.Contains("PROMOTED", StringComparison.OrdinalIgnoreCase) &&
             errorCode.Contains("MISMATCH", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccessfulProcess(DevBridgeProcessResult? process) =>
        process is not null &&
        !process.TimedOut &&
        !process.Cancelled &&
        process.StartError is null &&
        process.ExitCode is null or 0 &&
        TryParseLastObject(process.Stdout, out JsonDocument? document) &&
        UsesSuccessEnvelope(document!);

    private static bool IsReadyProcess(DevBridgeProcessResult? process) =>
        process is not null &&
        !process.TimedOut &&
        !process.Cancelled &&
        process.StartError is null &&
        process.ExitCode is null or 0 &&
        TryParseLastObject(process.Stdout, out JsonDocument? document) &&
        UsesReadyEnvelope(document!);

    private static bool UsesSuccessEnvelope(JsonDocument document)
    {
        using (document)
        {
            JsonElement root = document.RootElement;
            return root.TryGetProperty("success", out JsonElement success) &&
                success.ValueKind == JsonValueKind.True;
        }
    }

    private static bool UsesReadyEnvelope(JsonDocument document)
    {
        using (document)
        {
            JsonElement root = document.RootElement;
            bool healthy = root.TryGetProperty("healthy", out JsonElement healthyValue) &&
                healthyValue.ValueKind == JsonValueKind.True;
            string? state = GetString(root, "status", "state", "lifecycleState");
            bool readyState = healthy ||
                string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "responsive", StringComparison.OrdinalIgnoreCase) ||
                (root.TryGetProperty("success", out JsonElement success) &&
                 success.ValueKind == JsonValueKind.True &&
                 !string.Equals(state, "error", StringComparison.OrdinalIgnoreCase));
            return readyState &&
                OptionalIntIs(root, "coordinatorCount", 1) &&
                OptionalIntIs(root, "activeLeases", 0) &&
                OptionalIntIs(root, "activeTests", 0) &&
                OptionalPositiveInt(root, "generation");
        }
    }

    private static bool OptionalIntIs(JsonElement root, string name, int expected)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return true;
        }

        return value.TryGetInt32(out int actual) && actual == expected;
    }

    private static bool OptionalPositiveInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return true;
        }

        return value.TryGetInt32(out int actual) && actual > 0;
    }

    private static DevBridgeCapabilityRecoveryResult Recovered(
        string triggerCode,
        string highestLevel,
        bool rimWorldRestarted,
        DevBridgeProcessResult process,
        IReadOnlyList<DevBridgeRecoveryAction> actions,
        long started) =>
        new(
            PrerequisiteRecoveryState.Recovered,
            1,
            Trigger: triggerCode,
            HighestLevel: highestLevel,
            RimWorldRestarted: rimWorldRestarted,
            FinalState: "READY",
            Actions: actions,
            ElapsedRecoveryMilliseconds: ElapsedMilliseconds(started),
            Process: process);

    private static DevBridgeCapabilityRecoveryResult Failed(
        string triggerCode,
        string errorCode,
        string? error,
        DevBridgeProcessResult? process,
        string highestLevel,
        IReadOnlyList<DevBridgeRecoveryAction> actions,
        long started,
        bool rimWorldRestarted = false) =>
        new(
            PrerequisiteRecoveryState.RecoveryFailed,
            1,
            errorCode,
            error,
            process,
            triggerCode,
            highestLevel,
            rimWorldRestarted,
            "UNAVAILABLE",
            actions,
            ElapsedMilliseconds(started));

    private static void AddAction(
        ICollection<DevBridgeRecoveryAction> actions,
        string level,
        string action,
        string state,
        string? errorCode,
        long started) =>
        actions.Add(new(
            level,
            action,
            state,
            1,
            ElapsedMilliseconds(started),
            errorCode));

    private static bool TryParseLastObject(
        string? output,
        out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(output);
            return true;
        }
        catch (JsonException)
        {
            foreach (string line in output.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries).Reverse())
            {
                try
                {
                    document = JsonDocument.Parse(line);
                    return true;
                }
                catch (JsonException)
                {
                }
            }
        }

        return false;
    }

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static bool TryDoctorReady(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            string? status = GetString(root, "status");
            return string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                (root.TryGetProperty("success", out JsonElement success) &&
                 success.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadErrorCode(string? stdout) =>
        TryParse(stdout, out JsonElement root)
            ? GetString(root, "errorCode", "code")
            : null;

    private static string? ReadError(string? stdout) =>
        TryParse(stdout, out JsonElement root)
            ? GetString(root, "error", "message")
            : null;

    private static bool TryParse(string? value, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string? Bound(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Length <= 1024
                ? value.Trim()
                : value.Trim()[..1024];
}
