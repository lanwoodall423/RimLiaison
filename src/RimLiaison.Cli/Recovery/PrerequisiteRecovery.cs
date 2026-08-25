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
    DevBridgeIdentityMismatch? IdentityMismatch = null);

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

public sealed record DevBridgeCapabilityRecoveryResult(
    PrerequisiteRecoveryState State,
    int Attempts,
    string? ErrorCode = null,
    string? Error = null,
    DevBridgeProcessResult? Process = null)
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
        CancellationToken cancellationToken = default)
    {
        string key = Path.GetFullPath(options.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        Lazy<Task<DevBridgeCapabilityRecoveryResult>> lazy = InFlight.GetOrAdd(
            key,
            _ => new(
                () => RecoverCoreAsync(transport, options, workflowId),
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
        string? workflowId)
    {
        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        ["--root", options.RootPath, "doctor", "--json"],
                        options.ShowPlanTimeout,
                        Math.Min(options.MaxStdoutBytes, 512 * 1024),
                        Math.Min(options.MaxStderrBytes, 16 * 1024),
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new(
                PrerequisiteRecoveryState.RecoveryFailed,
                1,
                "DEVBRIDGE_DOCTOR_FAILED",
                Bound(exception.Message));
        }

        if (process.ExitCode is 0 &&
            !process.TimedOut &&
            !process.Cancelled &&
            TryDoctorReady(process.Stdout))
        {
            return new(PrerequisiteRecoveryState.Recovered, 1, Process: process);
        }

        return new(
            PrerequisiteRecoveryState.RecoveryFailed,
            1,
            ReadErrorCode(process.Stdout) ?? "DEVBRIDGE_DOCTOR_FAILED",
            ReadError(process.Stdout) ?? Bound(process.Stderr),
            process);
    }

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
