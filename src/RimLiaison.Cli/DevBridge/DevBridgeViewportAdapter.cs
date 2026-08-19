using System.Globalization;
using System.Text.Json;

namespace RimLiaison.DevBridge;

public static class DevBridgeViewportSchemas
{
    public const string Environment = "devbridge-viewport-environment/v1";
    public const int WideWidth = 1600;
    public const int WideHeight = 900;
    public const int NarrowWidth = 1024;
    public const int NarrowHeight = 768;
    public const int MinimumWidth = 320;
    public const int MinimumHeight = 240;
    public const int MaximumWidth = 7680;
    public const int MaximumHeight = 4320;
}

public enum DevBridgeViewportOutcome
{
    Success,
    AlreadyRestored,
    Unavailable,
    Busy,
    InvalidRequest,
    Unsupported,
    VerificationFailure,
    RestorationFailure,
    InfrastructureFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public sealed record DevBridgeViewportStatus(
    DevBridgeViewportOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? NextAction = "DevBridge.cmd doctor --json")
{
    public bool IsSuccess => Outcome is DevBridgeViewportOutcome.Success or
        DevBridgeViewportOutcome.AlreadyRestored;
}

public sealed record DevBridgeViewportRequest(string Kind, int? Width = null, int? Height = null)
{
    public static bool TryCreate(
        string? kind,
        int? width,
        int? height,
        out DevBridgeViewportRequest request,
        out string error)
    {
        request = null!;
        error = "Viewport kind must be current, wide, narrow, or explicit.";
        string normalized = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (normalized)
        {
            case "current" when width is null && height is null:
                request = new DevBridgeViewportRequest(normalized);
                return true;
            case "wide" when width is null && height is null:
                request = new DevBridgeViewportRequest(
                    normalized, DevBridgeViewportSchemas.WideWidth,
                    DevBridgeViewportSchemas.WideHeight);
                return true;
            case "narrow" when width is null && height is null:
                request = new DevBridgeViewportRequest(
                    normalized, DevBridgeViewportSchemas.NarrowWidth,
                    DevBridgeViewportSchemas.NarrowHeight);
                return true;
            case "explicit" when width.HasValue && height.HasValue:
                if (width.Value < DevBridgeViewportSchemas.MinimumWidth ||
                    width.Value > DevBridgeViewportSchemas.MaximumWidth ||
                    height.Value < DevBridgeViewportSchemas.MinimumHeight ||
                    height.Value > DevBridgeViewportSchemas.MaximumHeight)
                {
                    error = "Viewport dimensions are outside the supported safety bounds.";
                    return false;
                }

                request = new DevBridgeViewportRequest(normalized, width, height);
                return true;
            case "current" or "wide" or "narrow":
                error = "Current, wide, and narrow viewport requests do not accept custom dimensions.";
                return false;
            case "explicit":
                error = "An explicit viewport request requires both width and height.";
                return false;
            default:
                return false;
        }
    }
}

public sealed record DevBridgeViewportEvidence(
    string SchemaVersion,
    string Status,
    string? TransactionId,
    string? LeaseId,
    int? Generation,
    JsonElement? Requested,
    JsonElement? CapturedState,
    JsonElement? EffectiveViewport,
    JsonElement? RestoredViewport,
    bool PersistentPreferenceMutation,
    bool RestorationVerified,
    string? CleanupStatus);

public sealed record DevBridgeViewportResult(
    DevBridgeViewportStatus Status,
    DevBridgeViewportEvidence? Evidence);

public interface IDevBridgeViewportAdapter
{
    Task<DevBridgeViewportResult> BeginAsync(
        DevBridgeViewportRequest request,
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeViewportResult> RestoreAsync(
        string transactionId,
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// RimLiaison's typed client for the lease-bound DevBridge2 runtime-only
/// viewport transaction. It never edits RimWorld preferences or ModsConfig.
/// </summary>
public sealed class DevBridgeViewportAdapter : IDevBridgeViewportAdapter
{
    private const int MaxMessageLength = 4096;
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeViewportAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<DevBridgeViewportResult> BeginAsync(
        DevBridgeViewportRequest request,
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(leaseId))
        {
            return Task.FromResult(Failure(
                DevBridgeViewportOutcome.InvalidRequest,
                "RIMTEST_VIEWPORT_REQUEST_INVALID",
                "A valid viewport request and canonical lease id are required."));
        }

        var arguments = new List<string>
        {
            "--root",
            options.RootPath,
            "environment",
            "viewport",
            "begin",
            leaseId,
            request.Kind
        };
        if (string.Equals(request.Kind, "explicit", StringComparison.Ordinal))
        {
            arguments.Add(request.Width!.Value.ToString(CultureInfo.InvariantCulture));
            arguments.Add(request.Height!.Value.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--json");
        return InvokeAsync(arguments, workflowId, cancellationToken);
    }

    public Task<DevBridgeViewportResult> RestoreAsync(
        string transactionId,
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || string.IsNullOrWhiteSpace(leaseId))
        {
            return Task.FromResult(Failure(
                DevBridgeViewportOutcome.InvalidRequest,
                "RIMTEST_VIEWPORT_RESTORE_REQUEST_INVALID",
                "A viewport transaction id and canonical lease id are required."));
        }

        return InvokeAsync(
            ["--root", options.RootPath, "environment", "viewport", "restore",
                leaseId, transactionId, "--json"],
            workflowId,
            cancellationToken);
    }

    private async Task<DevBridgeViewportResult> InvokeAsync(
        IReadOnlyList<string> arguments,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        arguments,
                        options.ShowPlanTimeout,
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes,
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(DevBridgeViewportOutcome.Cancelled, "RIMTEST_CANCELLED",
                "The RimLiaison viewport request was cancelled.");
        }
        catch (Exception exception)
        {
            return Failure(DevBridgeViewportOutcome.InfrastructureFailure,
                "DEVBRIDGE_VIEWPORT_START_FAILED", Bound(exception.Message));
        }

        if (process.Cancelled)
            return Failure(DevBridgeViewportOutcome.Cancelled, "RIMTEST_CANCELLED",
                "The RimLiaison viewport request was cancelled.", process.ExitCode);
        if (process.TimedOut)
            return Failure(DevBridgeViewportOutcome.Timeout, "DEVBRIDGE_VIEWPORT_TIMEOUT",
                "DevBridge did not complete the bounded viewport transaction.", process.ExitCode);
        if (!string.IsNullOrWhiteSpace(process.StartError))
            return Failure(DevBridgeViewportOutcome.InfrastructureFailure,
                "DEVBRIDGE_VIEWPORT_START_FAILED", Bound(process.StartError), process.ExitCode);
        if (process.StdoutTruncated || process.StderrTruncated)
            return Failure(DevBridgeViewportOutcome.MalformedResponse,
                "DEVBRIDGE_VIEWPORT_OUTPUT_LIMIT_EXCEEDED",
                "DevBridge returned more viewport state than RimLiaison can safely inspect.",
                process.ExitCode);

        if (!TryParseLastObject(process.Stdout, out JsonDocument? document))
            return Failure(DevBridgeViewportOutcome.MalformedResponse,
                "DEVBRIDGE_VIEWPORT_RESPONSE_INVALID",
                "DevBridge returned no structured viewport response.", process.ExitCode);

        using (document!)
        {
            JsonElement root = document!.RootElement;
            if (!TryGetProperty(root, out JsonElement viewport, "viewport") ||
                viewport.ValueKind != JsonValueKind.Object)
                return Failure(DevBridgeViewportOutcome.IncompatibleSchema,
                    "DEVBRIDGE_VIEWPORT_SCHEMA_UNSUPPORTED",
                    "DevBridge did not return the versioned viewport transaction object.",
                    process.ExitCode);

            string? schema = GetString(viewport, "schemaVersion");
            if (!string.Equals(schema, DevBridgeViewportSchemas.Environment,
                    StringComparison.Ordinal))
                return Failure(DevBridgeViewportOutcome.IncompatibleSchema,
                    "DEVBRIDGE_VIEWPORT_SCHEMA_UNSUPPORTED",
                    "DevBridge returned an unsupported viewport transaction schema.",
                    process.ExitCode);

            bool success = GetBoolean(viewport, "success") == true &&
                GetBoolean(root, "success") == true &&
                (process.ExitCode is null or 0);
            DevBridgeViewportOutcome outcome = success
                ? string.Equals(GetString(viewport, "status"), "alreadyRestored",
                    StringComparison.OrdinalIgnoreCase)
                    ? DevBridgeViewportOutcome.AlreadyRestored
                    : DevBridgeViewportOutcome.Success
                : MapFailureCode(GetString(viewport, "errorCode") ??
                    GetString(root, "errorCode") ?? string.Empty);
            string? code = GetString(viewport, "errorCode") ?? GetString(root, "errorCode");
            string? error = GetString(viewport, "error") ?? GetString(root, "error");
            DevBridgeViewportStatus status = new(
                outcome,
                code,
                Bound(error),
                process.ExitCode,
                GetString(viewport, "nextAction") ?? GetString(root, "nextAction"));

            return new DevBridgeViewportResult(
                status,
                new DevBridgeViewportEvidence(
                    schema!,
                    GetString(viewport, "status") ?? (success ? "prepared" : "blocked"),
                    GetString(viewport, "transactionId"),
                    GetString(viewport, "leaseId"),
                    GetInt(viewport, "generation"),
                    GetElement(viewport, "requested"),
                    GetElement(viewport, "capturedState"),
                    GetElement(viewport, "effectiveViewport"),
                    GetElement(viewport, "restoredViewport"),
                    GetBoolean(viewport, "persistentPreferenceMutation") == true,
                    GetBoolean(viewport, "restorationVerified") == true,
                    GetString(viewport, "cleanupStatus")));
        }
    }

    private static DevBridgeViewportResult Failure(
        DevBridgeViewportOutcome outcome,
        string code,
        string? error,
        int? processExitCode = null) => new(
            new DevBridgeViewportStatus(outcome, code, Bound(error), processExitCode,
                outcome is DevBridgeViewportOutcome.Cancelled ? null : "DevBridge.cmd doctor --json"),
            null);

    private static DevBridgeViewportOutcome MapFailureCode(string code) =>
        code.Contains("BUSY", StringComparison.OrdinalIgnoreCase)
            ? DevBridgeViewportOutcome.Busy
            : code.Contains("UNSUPPORTED", StringComparison.OrdinalIgnoreCase) ||
              code.Contains("WINDOW_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                ? DevBridgeViewportOutcome.Unsupported
                : code.Contains("RESTORE", StringComparison.OrdinalIgnoreCase)
                    ? DevBridgeViewportOutcome.RestorationFailure
                    : code.Contains("VERIFY", StringComparison.OrdinalIgnoreCase) ||
                      code.Contains("DIMENSIONS", StringComparison.OrdinalIgnoreCase)
                        ? DevBridgeViewportOutcome.VerificationFailure
                        : code.Contains("LEASE", StringComparison.OrdinalIgnoreCase) ||
                          code.Contains("NOT_READY", StringComparison.OrdinalIgnoreCase)
                            ? DevBridgeViewportOutcome.Unavailable
                            : code.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
                                ? DevBridgeViewportOutcome.Timeout
                                : DevBridgeViewportOutcome.InfrastructureFailure;

    private static bool? GetBoolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static int? GetInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int result)
            ? result
            : null;

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static JsonElement? GetElement(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) ? property.Clone() : null;

    private static bool TryGetProperty(JsonElement value, out JsonElement property,
        string name) => value.TryGetProperty(name, out property);

    private static bool TryParseLastObject(string? output, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            try
            {
                JsonDocument candidate = JsonDocument.Parse(
                    lines[index].Trim(), new JsonDocumentOptions { MaxDepth = 64 });
                if (candidate.RootElement.ValueKind == JsonValueKind.Object)
                {
                    document = candidate;
                    return true;
                }

                candidate.Dispose();
            }
            catch (JsonException)
            {
                // Progress lines are expected before the final bounded object.
            }
        }

        return false;
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string trimmed = value.Trim();
        return trimmed.Length <= MaxMessageLength ? trimmed : trimmed[..MaxMessageLength];
    }
}
