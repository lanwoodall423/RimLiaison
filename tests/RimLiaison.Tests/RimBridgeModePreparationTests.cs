using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class RimBridgeModePreparationTests
{
    public static void OffCoordinatorTransitionsToRequiredMode()
    {
        var transport = new FakeTransport();
        DevBridgeAdapterOptions options = new()
        {
            CommandPath = "DevBridge.cmd",
            RootPath = "C:\\DevBridge",
            ShowPlanTimeout = TimeSpan.FromSeconds(1),
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 4096
        };

        RimBridgeModePreparationResult result =
            RimBridgeModePreparation.EnsureRequiredAsync(options, transport)
                .GetAwaiter()
                .GetResult();

        Assert(result.Succeeded, result.Error ?? "required RimBridge preparation failed");
        Assert(transport.Requests.Count == 2, "an off ready coordinator must be shut down exactly once");
        Assert(transport.Requests.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue("DEVBRIDGE_RIMBRIDGE_MODE", out string? mode) &&
                mode == "required"),
            "every mode-transition request must carry required RimBridge mode");
        Assert(transport.Requests[1].Arguments.Contains("shutdown", StringComparer.Ordinal),
            "an off coordinator must transition through the coordinator shutdown boundary");
    }

    private sealed class FakeTransport : IDevBridgeProcessTransport
    {
        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string output = request.Arguments.Contains("status", StringComparer.Ordinal)
                ? "{\"state\":\"READY\",\"rimBridge\":{\"configuredMode\":\"off\"},\"leases\":[]}"
                : "{\"success\":true}";
            return Task.FromResult(new DevBridgeProcessResult(0, output, string.Empty));
        }
    }

    private static void Assert(bool condition, string error)
    {
        if (!condition)
        {
            throw new InvalidOperationException(error);
        }
    }
}
