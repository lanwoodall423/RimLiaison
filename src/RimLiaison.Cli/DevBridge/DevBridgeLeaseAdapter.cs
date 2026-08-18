using System.Globalization;
using System.Text.Json;

namespace RimLiaison.DevBridge;

public sealed class DevBridgeLeaseAdapter : IDevBridgeLeaseAdapter
{
    private const string LeaseIdProperty = "leaseId";
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeLeaseAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<DevBridgeLeaseResult> BeginLeaseAsync(
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(["test", "begin", "--json"], workflowId, null, cancellationToken);

    public Task<DevBridgeLeaseResult> RenewLeaseAsync(
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(["test", "renew", leaseId, "--json"], workflowId, leaseId, cancellationToken);

    public Task<DevBridgeLeaseResult> EndLeaseAsync(
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(["test", "end", leaseId, "--json"], workflowId, leaseId, cancellationToken);

    private async Task<DevBridgeLeaseResult> InvokeAsync(
        IReadOnlyList<string> command,
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        ["--root", options.RootPath, .. command],
                        options.ShowPlanTimeout,
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes,
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                DevBridgeOutcomeKind.Cancelled,
                "RIMTEST_CANCELLED",
                "The DevBridge lease request was cancelled.",
                leaseId);
        }
        catch (Exception exception)
        {
            return Failure(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "DEVBRIDGE_LEASE_START_FAILED",
                Bound(exception.Message),
                leaseId);
        }

        if (process.Cancelled)
        {
            return Failure(
                DevBridgeOutcomeKind.Cancelled,
                "RIMTEST_CANCELLED",
                "The DevBridge lease request was cancelled.",
                leaseId,
                process);
        }

        if (process.TimedOut)
        {
            return Failure(
                DevBridgeOutcomeKind.Timeout,
                "DEVBRIDGE_LEASE_TIMEOUT",
                "The bounded DevBridge lease request timed out.",
                leaseId,
                process);
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            return Failure(
                DevBridgeOutcomeKind.MalformedResponse,
                "DEVBRIDGE_LEASE_OUTPUT_LIMIT_EXCEEDED",
                "The DevBridge lease response exceeded its output bound.",
                leaseId,
                process);
        }

        if (!TryParseLastObject(process.Stdout, out JsonDocument? document))
        {
            return Failure(
                string.IsNullOrWhiteSpace(process.StartError)
                    ? DevBridgeOutcomeKind.MalformedResponse
                    : DevBridgeOutcomeKind.InfrastructureFailure,
                string.IsNullOrWhiteSpace(process.StartError)
                    ? "DEVBRIDGE_LEASE_RESPONSE_INVALID"
                    : "DEVBRIDGE_LEASE_START_FAILED",
                Bound(process.StartError ?? process.Stderr),
                leaseId,
                process);
        }

        using (document!)
        {
            JsonElement root = document!.RootElement;
            bool success = TryGetBoolean(root, "success", out bool reportedSuccess) && reportedSuccess &&
                TryGetInt(root, "exitCode", out int exitCode) && exitCode == 0 &&
                process.ExitCode is null or 0;
            string? reportedLease = TryGetNullableString(root, LeaseIdProperty, out string? parsedLease)
                ? parsedLease
                : leaseId;
            int? generation = TryGetInt(root, "generation", out int parsedGeneration) &&
                parsedGeneration > 0 ? parsedGeneration : null;
            string? errorCode = TryGetNullableString(root, "errorCode", out string? parsedCode)
                ? parsedCode
                : null;
            string? error = TryGetNullableString(root, "error", out string? parsedError)
                ? parsedError
                : null;

            if (!success)
            {
                return Failure(
                    process.ExitCode is > 0
                        ? DevBridgeOutcomeKind.DevBridgeRefusal
                        : DevBridgeOutcomeKind.InfrastructureFailure,
                    errorCode ?? "DEVBRIDGE_LEASE_FAILED",
                    error ?? Bound(process.Stderr),
                    reportedLease,
                    process,
                    generation);
            }

            return new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Success,
                    null,
                    null,
                    process.ExitCode,
                    Bound(process.Stderr),
                    "devbridge-command/v1"),
                reportedLease,
                generation);
        }
    }

    private static DevBridgeLeaseResult Failure(
        DevBridgeOutcomeKind outcome,
        string code,
        string? error,
        string? leaseId,
        DevBridgeProcessResult? process = null,
        int? generation = null) =>
        new(
            new DevBridgeAdapterStatus(
                outcome,
                code,
                error,
                process?.ExitCode,
                Bound(process?.Stderr),
                "devbridge-command/v1"),
            leaseId,
            generation);

    private static bool TryParseLastObject(
        string? output,
        out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            try
            {
                JsonDocument candidate = JsonDocument.Parse(
                    lines[index].Trim(),
                    new JsonDocumentOptions { MaxDepth = 32 });
                if (candidate.RootElement.ValueKind == JsonValueKind.Object)
                {
                    document = candidate;
                    return true;
                }

                candidate.Dispose();
            }
            catch (JsonException)
            {
                // Lifecycle commands emit bounded progress lines before the final JSON object.
            }
        }

        return false;
    }

    private static bool TryGetBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryGetNullableString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.String &&
            (value = element.GetString()) is not null;
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 4096 ? trimmed : trimmed[..4096];
    }
}
