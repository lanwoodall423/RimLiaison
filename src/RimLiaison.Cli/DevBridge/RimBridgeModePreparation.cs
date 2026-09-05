using System.Text.Json;

namespace RimLiaison.DevBridge;

public sealed record RimBridgeModePreparationResult(
    bool Succeeded,
    IDevBridgeProcessTransport Transport,
    string? ErrorCode = null,
    string? Error = null)
{
    public static RimBridgeModePreparationResult Success(IDevBridgeProcessTransport transport) =>
        new(true, transport);

    public static RimBridgeModePreparationResult Failure(
        IDevBridgeProcessTransport transport,
        string errorCode,
        string error) =>
        new(false, transport, errorCode, error);
}

public static class RimBridgeModePreparation
{
    public static async Task<RimBridgeModePreparationResult> EnsureRequiredAsync(
        DevBridgeAdapterOptions options,
        IDevBridgeProcessTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        RequiredRimBridgeProcessTransport required =
            new(transport);
        DevBridgeProcessResult status = await required.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.CommandPath,
                    options.RootPath,
                    ["--root", options.RootPath, "status", "--json"],
                    options.ShowPlanTimeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    OperationKey: "rimbridge-mode-status"),
                cancellationToken)
            .ConfigureAwait(false);
        if (status.ExitCode is not 0 || string.IsNullOrWhiteSpace(status.Stdout))
        {
            return RimBridgeModePreparationResult.Failure(
                required,
                "RIMBRIDGE_MODE_STATUS_FAILED",
                status.StartError ?? status.Stderr ??
                "The DevBridge coordinator status could not be read.");
        }

        using JsonDocument document = JsonDocument.Parse(status.Stdout);
        JsonElement root = document.RootElement;
        string? mode = StringValue(root, "rimBridge", "configuredMode");
        string? state = StringValue(root, "state");
        string? error = StringValue(root, "error") ?? StringValue(root, "errorMessage");
        if (HasActiveLease(root))
        {
            return RimBridgeModePreparationResult.Failure(
                required,
                "RIMBRIDGE_MODE_TRANSITION_BUSY",
                "The active DevBridge coordinator has a lease; required RimBridge mode cannot be forced safely.");
        }

        if (string.Equals(mode, "required", StringComparison.OrdinalIgnoreCase))
        {
            return RimBridgeModePreparationResult.Success(required);
        }

        if (!string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(error))
        {
            return RimBridgeModePreparationResult.Failure(
                required,
                "RIMBRIDGE_MODE_STATUS_FAILED",
                error);
        }

        // STOPPED is a lazy-launch boundary, not proof that the coordinator
        // process has exited. An off coordinator can still accept the next
        // request, so it must cross the shutdown boundary before required
        // mode is used.
        DevBridgeProcessResult shutdown = await required.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.CommandPath,
                    options.RootPath,
                    ["--root", options.RootPath, "coordinator", "shutdown", "--json"],
                    options.ShowPlanTimeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    OperationKey: "rimbridge-mode-transition"),
                cancellationToken)
            .ConfigureAwait(false);
        if (shutdown.ExitCode is not 0 || shutdown.TimedOut || shutdown.Cancelled ||
            !string.IsNullOrWhiteSpace(shutdown.StartError))
        {
            return RimBridgeModePreparationResult.Failure(
                required,
                "RIMBRIDGE_MODE_TRANSITION_FAILED",
                shutdown.StartError ?? shutdown.Stderr ??
                "The DevBridge coordinator did not accept the required RimBridge mode transition.");
        }

        return RimBridgeModePreparationResult.Success(required);
    }

    private static bool HasActiveLease(JsonElement root)
    {
        JsonElement leaseId = Property(root, "leaseId");
        if (leaseId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(leaseId.GetString()))
        {
            return true;
        }

        JsonElement leases = Property(root, "leases");
        return leases.ValueKind == JsonValueKind.Array && leases.EnumerateArray().Any(lease =>
            string.Equals(StringValue(lease, "status"), "ACTIVE", StringComparison.OrdinalIgnoreCase));
    }

    private static string? StringValue(JsonElement root, string name) =>
        StringValue(Property(root, name));

    private static string? StringValue(JsonElement root, string parent, string name) =>
        StringValue(Property(Property(root, parent), name));

    private static string? StringValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private sealed class RequiredRimBridgeProcessTransport : IDevBridgeProcessTransport
    {
        private readonly IDevBridgeProcessTransport inner;

        public RequiredRimBridgeProcessTransport(IDevBridgeProcessTransport inner) =>
            this.inner = inner;

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> environment = request.EnvironmentVariables is null
                ? new(StringComparer.Ordinal)
                : new(request.EnvironmentVariables, StringComparer.Ordinal);
            environment["DEVBRIDGE_RIMBRIDGE_MODE"] = "required";
            return inner.ExecuteAsync(
                request with { EnvironmentVariables = environment },
                cancellationToken);
        }
    }
}
