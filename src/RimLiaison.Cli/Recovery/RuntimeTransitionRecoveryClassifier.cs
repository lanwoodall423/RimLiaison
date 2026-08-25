using RimLiaison.DevBridge;

namespace RimLiaison.Recovery;

/// <summary>
/// Identifies the narrow set of DevBridge failures that can be caused by a
/// shared runtime transition.  This is deliberately an allow-list: a build,
/// recipe, policy, lease, or assertion failure must not become a lifecycle
/// retry merely because it was returned by DevBridge.
/// </summary>
public static class RuntimeTransitionRecoveryClassifier
{
    public const string Component = "shared-runtime-transition";
    public const string RecoverAction = "wait-for-fresh-generation";
    public const string RetryAction = "rerun-development-freshness-transaction";
    public const string ExhaustedAction = "shared-runtime-transition-recovery-exhausted";

    private static readonly HashSet<string> RecoverableCodes = new(StringComparer.Ordinal)
    {
        "RIMBRIDGE_ENDPOINT_STALE",
        "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH",
        "RIMBRIDGE_PROCESS_MISMATCH",
        "RIMBRIDGE_PROTOCOL_ERROR",
        "RIMBRIDGE_COMPANION_UNAVAILABLE",
        "ENDPOINT_UNAVAILABLE",
        "DEVBRIDGE_NO_STRUCTURED_RESPONSE",

        // Coordinator/readiness/scope failures can be transient for a
        // read-only capability probe.
        "DEVBRIDGE_COORDINATOR_UNAVAILABLE",
        "DEVBRIDGE_COORDINATOR_NOT_READY",
        "DEVBRIDGE_COORDINATOR_SCOPE_MISMATCH",
        "DEVBRIDGE_SCOPE_MISMATCH",
        "READINESS_NOT_READY",
        "READINESS_TIMEOUT",
        "SCOPE_MISMATCH",

        // Existing DevBridge/RimLiaison readiness spellings.
        "PROCESS_EXITED",
        "PROCESS_STOPPED",
        "READINESS_IDENTITY_MISMATCH",
        "RIMBRIDGE_NOT_READY",
        "RIMBRIDGE_STALE",
        "GENERATION_INPUT_MISMATCH",
        "RIMBRIDGE_ENDPOINT_UNAVAILABLE",
        // The mod-development adapter's own response-envelope failures.  Do
        // not include output-limit, schema, or start failures: those are
        // client/configuration problems, not evidence of a shared transition.
        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING",
        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID"
    };

    public static bool IsRecoverable(DevBridgeAdapterStatus status)
    {
        if (DevBridgeIdentityMismatchPolicy.IsIdentityMismatch(status))
        {
            return DevBridgeIdentityMismatchPolicy.ShouldRecover(status);
        }

        return (status.Outcome is DevBridgeOutcomeKind.InfrastructureFailure or
                DevBridgeOutcomeKind.MalformedResponse) &&
            status.ErrorCode is not null &&
            RecoverableCodes.Contains(status.ErrorCode);
    }

    public static bool IsRecoverableCapability(
        DevBridgeCapabilityStatus status)
    {
        string? code = status.Evidence?.UnderlyingErrorCode ?? status.ErrorCode;
        return (status.Outcome is DevBridgeCapabilityOutcome.InfrastructureFailure or
                DevBridgeCapabilityOutcome.Unavailable or
                DevBridgeCapabilityOutcome.Timeout) &&
            IsRecoverableCode(code);
    }

    private static bool IsRecoverableCode(string? code) =>
        code is not null && RecoverableCodes.Contains(code);

}
