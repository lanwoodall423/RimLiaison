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
    RecoveryFailed
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
        _ => "unavailable"
    };

    public static bool IsTerminalFailure(this PrerequisiteRecoveryState state) =>
        state is PrerequisiteRecoveryState.RecoveryRequired or
            PrerequisiteRecoveryState.Contended or
            PrerequisiteRecoveryState.Unavailable or
            PrerequisiteRecoveryState.RecoveryFailed;
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
    [property: JsonPropertyName("action")] string? Action = null);

public static class PrerequisiteRecoveryProjection
{
    public static RimTestPrerequisiteRecovery FromStatus(
        string component,
        DevBridgeAdapterStatus status) =>
        new(
            component,
            status.RecoveryState.ToWireName(),
            Math.Max(0, status.RecoveryAttempts),
            status.ErrorCode,
            status.RecoveryAction);
}
