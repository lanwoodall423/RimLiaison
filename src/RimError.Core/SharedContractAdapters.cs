using RimDev.Contracts;

namespace RimError.Core;

public static class SharedContractAdapters
{
    public static ExecutionIdentity ToExecutionIdentity(this DiagnosticDevBridgeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ExecutionIdentity
        {
            DeploymentIdentity = context.LeaseId ?? context.ProfileFingerprint,
            ProcessGeneration = context.Generation,
            RuntimeInstanceId = context.RuntimeSlotId ?? context.LaunchId,
            ExecutionId = context.RunId,
            TestIds = context.TestId is null ? null : [context.TestId],
            ToolVersion = context.SourceSchema
        };
    }
}
