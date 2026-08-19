using System.Text.Json;

namespace RimLiaison.DevBridge;

public static class DevBridgeUiSchemas
{
    public const string Targets = "rimtest-ui-targets/v1";
    public const string Screenshot = "rimtest-ui-screenshot/v1";
}

public enum DevBridgeUiOutcome
{
    Success,
    Unavailable,
    TargetNotFound,
    VisualReadinessFailure,
    InvalidRequest,
    InfrastructureFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public sealed record DevBridgeUiStatus(
    DevBridgeUiOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? OperationId = null,
    string? WorkflowId = null,
    string? EvidenceId = null,
    string? NextAction = "DevBridge.cmd doctor --json")
{
    public bool IsSuccess => Outcome == DevBridgeUiOutcome.Success;
}

public sealed record DevBridgeUiTarget(
    string Id,
    string? Kind,
    string? Label,
    JsonElement? Rect);

public sealed record DevBridgeUiTargetsResult(
    DevBridgeUiStatus Status,
    IReadOnlyList<DevBridgeUiTarget> Targets);

public sealed record DevBridgeUiCellRect(int X, int Z, int Width, int Height);

public sealed record DevBridgeUiScreenshotRequest(
    string? TargetId,
    DevBridgeUiCellRect? CellRect);

public sealed record DevBridgeUiScreenshotEvidence(
    string Path,
    string? TargetId,
    string? TargetKind,
    string? TargetLabel,
    JsonElement? ClipRect,
    string CaptureStatus,
    JsonElement? RequestedRect,
    JsonElement? PaddedRect,
    bool? CameraRestored,
    string? CapturedAtUtc,
    string? OperationId,
    string? WorkflowId,
    string? EvidenceId);

public sealed record DevBridgeUiScreenshotResult(
    DevBridgeUiStatus Status,
    DevBridgeUiScreenshotEvidence? Evidence);

public sealed record DevBridgeUiInputCheckResult(
    DevBridgeUiStatus Status,
    JsonElement? Evidence);

public interface IDevBridgeUiInspectionAdapter
{
    Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        CancellationToken cancellationToken = default);

    Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        CheckInputAsync(cancellationToken);

    Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default) =>
        CheckInputAsync(workflowId, cancellationToken);
}

public interface IDevBridgeUiAdapter
{
    Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        CancellationToken cancellationToken = default);

    Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        GetTargetsAsync(cancellationToken);

    Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default) =>
        GetTargetsAsync(workflowId, cancellationToken);

    Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        CancellationToken cancellationToken = default);

    Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(request, cancellationToken);

    Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(request, workflowId, cancellationToken);
}

/// <summary>
/// RimLiaison-owned routing for the small visual-validation surface. It discovers
/// tool ids and parameter names from RimBridgeServer before making typed calls.
/// It intentionally has no lifecycle, lease, identity, or generic execution API.
/// </summary>
public sealed class DevBridgeUiAdapter : IDevBridgeUiAdapter, IDevBridgeUiInspectionAdapter
{
    private const int CapabilityLimit = 100;
    private const int MaxMessageLength = 512;
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;
    private readonly IDevBridgeCapabilityAdapter capabilityAdapter;

    public DevBridgeUiAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        capabilityAdapter = new DevBridgeCapabilityAdapter(transport, options);
    }

    public async Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        CancellationToken cancellationToken = default) =>
        await GetTargetsAsync(null, null, cancellationToken).ConfigureAwait(false);

    public async Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        return await GetTargetsAsync(workflowId, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DevBridgeUiTargetsResult> GetTargetsAsync(
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default)
    {
        (DevBridgeCapability? capability, DevBridgeUiStatus? discoveryFailure) =
            await FindCapabilityAsync(
                    "screen targets",
                    IsScreenTargetsCapability,
                    workflowId,
                    leaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (discoveryFailure is not null)
        {
            return new DevBridgeUiTargetsResult(discoveryFailure, []);
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfSupported(capability!, arguments, "waitForVisualReady", true);
        ToolCallResult call = await InvokeToolAsync(
                capability!.Id,
                arguments,
                workflowId,
                leaseId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!call.Status.IsSuccess)
        {
            return new DevBridgeUiTargetsResult(call.Status, []);
        }

        if (!TryGetOptionalBoolean(call.Result, out bool? success, "success") ||
            success == false)
        {
            return new DevBridgeUiTargetsResult(
                Failure(
                    MapToolFailureOutcome(call.Result),
                    GetString(call.Result, "errorCode", "code") ??
                        "RIMTEST_UI_TARGETS_FAILED",
                    GetString(call.Result, "error", "message") ??
                        "RimBridgeServer could not enumerate visible UI targets.",
                    call.Status),
                []);
        }

        if (!TryGetTargetArray(call.Result, out JsonElement targetArray))
        {
            return new DevBridgeUiTargetsResult(
                Failure(
                    DevBridgeUiOutcome.IncompatibleSchema,
                    "RIMTEST_UI_TARGETS_SCHEMA_UNSUPPORTED",
                    "RimBridgeServer did not return a supported targets collection.",
                    call.Status),
                []);
        }

        var targets = new List<DevBridgeUiTarget>();
        foreach (JsonElement value in targetArray.EnumerateArray())
        {
            if (!TryGetString(value, out string? id, "windowTargetId", "targetId", "id") ||
                string.IsNullOrWhiteSpace(id))
            {
                return new DevBridgeUiTargetsResult(
                    Failure(
                        DevBridgeUiOutcome.MalformedResponse,
                        "RIMTEST_UI_TARGET_DESCRIPTOR_INVALID",
                        "Each visible UI target must have a non-empty id.",
                        call.Status),
                    []);
            }

            targets.Add(
                new DevBridgeUiTarget(
                    id!,
                    GetString(value, "kind", "targetKind", "type"),
                    GetString(value, "label", "title", "name"),
                    GetElement(value, "rect", "screenRect", "bounds")));
        }

        return new DevBridgeUiTargetsResult(
            Success(call.Status),
            targets);
    }

    public async Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        CancellationToken cancellationToken = default) =>
        await CheckInputAsync(null, null, cancellationToken).ConfigureAwait(false);

    public async Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        return await CheckInputAsync(workflowId, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DevBridgeUiInputCheckResult> CheckInputAsync(
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default)
    {
        (DevBridgeCapability? capability, DevBridgeUiStatus? discoveryFailure) =
            await FindCapabilityAsync(
                    "UI input state",
                    IsUiStateCapability,
                    workflowId,
                    leaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (discoveryFailure is not null)
        {
            return new DevBridgeUiInputCheckResult(discoveryFailure, null);
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddIfSupported(capability!, arguments, "waitForVisualReady", true);
        ToolCallResult call = await InvokeToolAsync(
                capability!.Id,
                arguments,
                workflowId,
                leaseId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!call.Status.IsSuccess)
        {
            return new DevBridgeUiInputCheckResult(call.Status, null);
        }

        if (!TryGetOptionalBoolean(call.Result, out bool? success, "success") ||
            success == false)
        {
            return new DevBridgeUiInputCheckResult(
                Failure(
                    MapToolFailureOutcome(call.Result),
                    GetString(call.Result, "errorCode", "code") ?? "RIMTEST_UI_INPUT_CHECK_FAILED",
                    GetString(call.Result, "error", "message") ??
                    "RimBridgeServer could not inspect live UI input state.",
                    call.Status),
                null);
        }

        bool? focused = GetOptionalBoolean(call.Result,
            "focused", "windowFocused", "inputReady");
        if (focused == false)
        {
            return new DevBridgeUiInputCheckResult(
                Failure(
                    DevBridgeUiOutcome.VisualReadinessFailure,
                    "RIMTEST_UI_INPUT_NOT_READY",
                    "The live RimWorld UI did not report a ready/focused input surface.",
                    call.Status),
                call.Result);
        }

        return new DevBridgeUiInputCheckResult(Success(call.Status), call.Result);
    }

    public async Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        CancellationToken cancellationToken = default) =>
        await CaptureAsync(request, null, null, cancellationToken).ConfigureAwait(false);

    public async Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        return await CaptureAsync(request, workflowId, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DevBridgeUiScreenshotResult> CaptureAsync(
        DevBridgeUiScreenshotRequest request,
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken = default)
    {
        if ((request.TargetId is null) == (request.CellRect is null))
        {
            return FailureResult(
                DevBridgeUiOutcome.InvalidRequest,
                "RIMTEST_UI_CAPTURE_REQUEST_INVALID",
                "Specify exactly one target id or cell rectangle.");
        }

        if (request.CellRect is not null &&
            (request.CellRect.Width < 1 ||
             request.CellRect.Height < 1 ||
             (long)request.CellRect.Width * request.CellRect.Height > 1024))
        {
            return FailureResult(
                DevBridgeUiOutcome.InvalidRequest,
                "RIMTEST_UI_CELL_RECT_INVALID",
                "Cell rectangle width and height must be positive and cover at most 1024 cells.");
        }

        DevBridgeUiTarget? target = null;
        if (request.TargetId is not null)
        {
            DevBridgeUiTargetsResult targets = await GetTargetsAsync(
                    workflowId,
                    leaseId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!targets.Status.IsSuccess)
            {
                return new DevBridgeUiScreenshotResult(targets.Status, null);
            }

            target = targets.Targets.FirstOrDefault(
                value => string.Equals(value.Id, request.TargetId, StringComparison.Ordinal));
            if (target is null)
            {
                return FailureResult(
                    DevBridgeUiOutcome.TargetNotFound,
                    "RIMTEST_UI_TARGET_NOT_FOUND",
                    $"Visible UI target was not found: {request.TargetId}.");
            }
        }

        string query = request.TargetId is not null ? "screenshot" : "cell rect";
        (DevBridgeCapability? capability, DevBridgeUiStatus? discoveryFailure) =
            await FindCapabilityAsync(
                    query,
                    request.TargetId is not null
                        ? IsTargetScreenshotCapability
                        : IsCellScreenshotCapability,
                    workflowId,
                    leaseId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (discoveryFailure is not null)
        {
            return new DevBridgeUiScreenshotResult(discoveryFailure, null);
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.TargetId is not null)
        {
            if (!AddRequiredArgument(capability!, arguments, "targetId", request.TargetId) &&
                !AddRequiredArgument(capability!, arguments, "clipTargetId", request.TargetId))
            {
                return FailureResult(
                    DevBridgeUiOutcome.IncompatibleSchema,
                    "RIMTEST_UI_CAPABILITY_SCHEMA_UNSUPPORTED",
                    "The registered screenshot capability does not accept targetId or clipTargetId.");
            }

            AddIfSupported(capability!, arguments, "clipPadding", 0);
            AddIfSupported(capability!, arguments, "includeScreenTargets", true);
            AddIfSupported(capability!, arguments, "includeTargets", true);
            AddIfSupported(capability!, arguments, "suppressMessage", true);
        }
        else
        {
            DevBridgeUiCellRect cellRect = request.CellRect!;
            if (!AddRequiredArgument(capability!, arguments, "x", cellRect.X) ||
                !AddRequiredArgument(capability!, arguments, "z", cellRect.Z) ||
                !AddRequiredArgument(capability!, arguments, "width", cellRect.Width) ||
                !AddRequiredArgument(capability!, arguments, "height", cellRect.Height))
            {
                return FailureResult(
                    DevBridgeUiOutcome.IncompatibleSchema,
                    "RIMTEST_UI_CAPABILITY_SCHEMA_UNSUPPORTED",
                    "The registered cell screenshot capability does not accept x, z, width, and height.");
            }

            AddIfSupported(capability!, arguments, "paddingCells", 0);
        }

        // Readiness is delegated to the registered live-game operation. This
        // avoids taking a screenshot of a loading or non-rendered state.
        AddIfSupported(capability!, arguments, "waitForVisualReady", true);
        AddIfSupported(capability!, arguments, "doNotResetCamera", false);
        ToolCallResult call = await InvokeToolAsync(
                capability!.Id,
                arguments,
                workflowId,
                leaseId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!call.Status.IsSuccess)
        {
            return new DevBridgeUiScreenshotResult(call.Status, null);
        }

        if (!TryGetOptionalBoolean(call.Result, out bool? success, "success") ||
            success == false)
        {
            return FailureResult(
                MapToolFailureOutcome(call.Result),
                GetString(call.Result, "errorCode", "code") ?? "RIMTEST_UI_CAPTURE_FAILED",
                GetString(call.Result, "error", "message") ??
                    "RimBridgeServer could not capture the requested visual evidence.",
                call.Status);
        }

        string? path = GetString(call.Result, "path", "screenshotPath", "sourcePath");
        if (string.IsNullOrWhiteSpace(path))
        {
            return FailureResult(
                DevBridgeUiOutcome.MalformedResponse,
                "RIMTEST_UI_CAPTURE_RESPONSE_INVALID",
                "RimBridgeServer did not return a screenshot path.",
                call.Status);
        }

        bool? cameraRestored = GetOptionalBoolean(call.Result, "cameraRestored");
        if (request.CellRect is not null && cameraRestored == false)
        {
            return FailureResult(
                DevBridgeUiOutcome.InfrastructureFailure,
                "RIMTEST_UI_CAMERA_RESTORE_FAILED",
                "Cell-region capture did not confirm camera restoration.",
                call.Status);
        }

        var evidence = new DevBridgeUiScreenshotEvidence(
            path!,
            GetString(call.Result, "clipTargetId", "targetId") ?? request.TargetId,
            GetString(call.Result, "clipTargetKind", "targetKind") ?? target?.Kind,
            GetString(call.Result, "clipTargetLabel", "targetLabel") ?? target?.Label,
            GetElement(call.Result, "clipRect", "screenRect", "clipBounds") ?? target?.Rect,
            "captured",
            GetElement(call.Result, "requestedRect", "requestedCellRect"),
            GetElement(call.Result, "paddedRect", "paddedCellRect"),
            cameraRestored,
            GetString(call.Result, "capturedAtUtc", "capturedAt"),
            call.Status.OperationId,
            call.Status.WorkflowId,
            call.Status.EvidenceId);
        return new DevBridgeUiScreenshotResult(Success(call.Status), evidence);
    }

    public static bool TryParseCellRect(
        string? value,
        out DevBridgeUiCellRect rect,
        out string error)
    {
        rect = default!;
        error = "Cell rectangle must use x,z,width,height.";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int z) ||
            !int.TryParse(parts[2], out int width) ||
            !int.TryParse(parts[3], out int height))
        {
            return false;
        }

        if (width < 1 || height < 1 || (long)width * height > 1024)
        {
            error = "Cell rectangle width and height must be positive and cover at most 1024 cells.";
            return false;
        }

        rect = new DevBridgeUiCellRect(x, z, width, height);
        return true;
    }

    private async Task<(DevBridgeCapability? Capability, DevBridgeUiStatus? Failure)> FindCapabilityAsync(
        string query,
        Func<DevBridgeCapability, bool> predicate,
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken)
    {
        DevBridgeCapabilityDiscoveryResult discovery = await capabilityAdapter.DiscoverAsync(
                new DevBridgeCapabilityQuery(query, Limit: CapabilityLimit),
                workflowId,
                leaseId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!discovery.Status.IsSuccess)
        {
            return (null, FromCapabilityStatus(discovery.Status));
        }

        DevBridgeCapability? capability = discovery.Capabilities.FirstOrDefault(predicate);
        return capability is null
            ? (null, new DevBridgeUiStatus(
                DevBridgeUiOutcome.IncompatibleSchema,
                "RIMTEST_UI_CAPABILITY_MISSING",
                "RimBridgeServer did not register the requested visual-validation capability."))
            : (capability, null);
    }

    private async Task<ToolCallResult> InvokeToolAsync(
        string toolId,
        IReadOnlyDictionary<string, object?> arguments,
        string? workflowId,
        string? leaseId,
        CancellationToken cancellationToken)
    {
        string serializedArguments = JsonSerializer.Serialize(arguments);
        var bridgeArguments = new List<string>
        {
            "--root",
            options.RootPath,
            "bridge",
            "call",
            toolId,
            serializedArguments
        };
        if (!string.IsNullOrWhiteSpace(leaseId))
        {
            bridgeArguments.Add("--lease");
            bridgeArguments.Add(leaseId);
        }

        bridgeArguments.Add("--json");
        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.RootPath,
            bridgeArguments,
            options.ShowPlanTimeout,
            options.MaxStdoutBytes,
            options.MaxStderrBytes,
            DevBridgeProcessEnvironment.ForWorkflow(workflowId));

        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.Cancelled,
                    "RIMTEST_CANCELLED",
                    "The RimLiaison UI capture request was cancelled.",
                    NextAction: null),
                null);
        }
        catch (Exception)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "DEVBRIDGE_START_FAILED",
                    "RimLiaison could not start DevBridge."),
                null);
        }

        if (process.Cancelled)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.Cancelled,
                    "RIMTEST_CANCELLED",
                    "The RimLiaison UI request was cancelled.",
                    process.ExitCode,
                    NextAction: null),
                null);
        }

        if (process.TimedOut)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.Timeout,
                    "DEVBRIDGE_UI_TIMEOUT",
                    "DevBridge did not return the live-game UI response in time.",
                    process.ExitCode),
                null);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "DEVBRIDGE_START_FAILED",
                    "RimLiaison could not start DevBridge.",
                    process.ExitCode),
                null);
        }

        if (process.StdoutTruncated)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "DEVBRIDGE_UI_OUTPUT_TRUNCATED",
                    "DevBridge returned more UI evidence than RimLiaison can safely inspect.",
                    process.ExitCode),
                null);
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "DEVBRIDGE_UI_NO_RESPONSE",
                    "DevBridge returned no structured live-game UI response.",
                    process.ExitCode),
                null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidToolResponse(process.ExitCode);
            }

            JsonElement envelope = root;
            if (TryGetProperty(root, out JsonElement route, "rimBridgeRoute"))
            {
                if (route.ValueKind != JsonValueKind.Object)
                {
                    return InvalidToolResponse(process.ExitCode);
                }

                envelope = route;
            }

            if (!TryGetOptionalBoolean(envelope, out bool? routeSuccess, "success"))
            {
                return InvalidToolResponse(process.ExitCode);
            }

            string? operationId = GetString(envelope, "operationId");
            string? routedWorkflowId = GetString(envelope, "workflowId");
            string? evidenceId = GetString(envelope, "evidenceId");
            if (routeSuccess == false)
            {
                string errorCode = GetString(envelope, "errorCode", "code") ??
                    "RIMBRIDGE_UI_OPERATION_FAILED";
                string error = GetString(envelope, "error", "message") ??
                    "DevBridge could not route the RimBridgeServer UI operation.";
                DevBridgeUiOutcome outcome = MapFailureCode(errorCode, error);
                return new ToolCallResult(
                    new DevBridgeUiStatus(
                        outcome,
                        errorCode,
                        Limit(error),
                        process.ExitCode,
                        operationId,
                        routedWorkflowId,
                        evidenceId),
                    null);
            }

            if (!TryGetProperty(envelope, out JsonElement result, "result") ||
                result.ValueKind != JsonValueKind.Object)
            {
                return InvalidToolResponse(process.ExitCode);
            }

            if (process.ExitCode is > 0)
            {
                return new ToolCallResult(
                    new DevBridgeUiStatus(
                        DevBridgeUiOutcome.InfrastructureFailure,
                        "DEVBRIDGE_RESULT_CONFLICT",
                        "DevBridge returned UI data with a non-success process result.",
                        process.ExitCode,
                        operationId,
                        routedWorkflowId,
                        evidenceId),
                    null);
            }

            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.Success,
                    ProcessExitCode: process.ExitCode,
                    OperationId: operationId,
                    WorkflowId: routedWorkflowId,
                    EvidenceId: evidenceId,
                    NextAction: null),
                result.Clone());
        }
        catch (JsonException exception)
        {
            return new ToolCallResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.MalformedResponse,
                    "RIMBRIDGE_UI_JSON_INVALID",
                    Limit("DevBridge returned malformed UI JSON: " + exception.Message),
                    process.ExitCode),
                null);
        }
    }

    private static ToolCallResult InvalidToolResponse(int? processExitCode)
    {
        return new ToolCallResult(
            new DevBridgeUiStatus(
                DevBridgeUiOutcome.MalformedResponse,
                "RIMBRIDGE_UI_RESPONSE_INVALID",
                "DevBridge returned an invalid RimBridgeServer UI response.",
                processExitCode),
            null);
    }

    private static bool IsScreenTargetsCapability(DevBridgeCapability capability) =>
        HasId(capability, "/get_screen_targets") ||
        HasId(capability, "get_screen_targets");

    private static bool IsUiStateCapability(DevBridgeCapability capability) =>
        HasId(capability, "/get_ui_state") ||
        HasId(capability, "get_ui_state");

    private static bool IsTargetScreenshotCapability(DevBridgeCapability capability) =>
        HasId(capability, "/take_screenshot") ||
        HasId(capability, "take_screenshot");

    private static bool IsCellScreenshotCapability(DevBridgeCapability capability) =>
        HasId(capability, "/screenshot_cell_rect") ||
        HasId(capability, "screenshot_cell_rect");

    private static bool HasId(DevBridgeCapability capability, string suffix) =>
        capability.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
        capability.Aliases.Any(alias =>
            alias.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alias, suffix.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    private static bool AddRequiredArgument(
        DevBridgeCapability capability,
        IDictionary<string, object?> arguments,
        string name,
        object value)
    {
        DevBridgeCapabilityParameter? parameter = FindParameter(capability, name);
        if (parameter is null)
        {
            return false;
        }

        arguments[parameter.Name] = value;
        return true;
    }

    private static void AddIfSupported(
        DevBridgeCapability capability,
        IDictionary<string, object?> arguments,
        string name,
        object value)
    {
        DevBridgeCapabilityParameter? parameter = FindParameter(capability, name);
        if (parameter is not null)
        {
            arguments[parameter.Name] = value;
        }
    }

    private static DevBridgeCapabilityParameter? FindParameter(
        DevBridgeCapability capability,
        string name) => capability.Parameters.FirstOrDefault(
        parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));

    private static DevBridgeUiStatus FromCapabilityStatus(
        DevBridgeCapabilityStatus status) => new(
        status.Outcome switch
        {
            DevBridgeCapabilityOutcome.Unavailable => DevBridgeUiOutcome.Unavailable,
            DevBridgeCapabilityOutcome.Timeout => DevBridgeUiOutcome.Timeout,
            DevBridgeCapabilityOutcome.Cancelled => DevBridgeUiOutcome.Cancelled,
            DevBridgeCapabilityOutcome.MalformedResponse => DevBridgeUiOutcome.MalformedResponse,
            DevBridgeCapabilityOutcome.IncompatibleSchema => DevBridgeUiOutcome.IncompatibleSchema,
            _ => DevBridgeUiOutcome.InfrastructureFailure
        },
        status.ErrorCode,
        Limit(status.Error),
        status.ProcessExitCode,
        NextAction: status.Outcome == DevBridgeCapabilityOutcome.Cancelled ? null : status.NextAction);

    private static DevBridgeUiStatus Success(DevBridgeUiStatus status) => new(
        DevBridgeUiOutcome.Success,
        ProcessExitCode: status.ProcessExitCode,
        OperationId: status.OperationId,
        WorkflowId: status.WorkflowId,
        EvidenceId: status.EvidenceId,
        NextAction: null);

    private static DevBridgeUiStatus Failure(
        DevBridgeUiOutcome outcome,
        string code,
        string error,
        DevBridgeUiStatus? correlation = null) => new(
        outcome,
        code,
        Limit(error),
        correlation?.ProcessExitCode,
        correlation?.OperationId,
        correlation?.WorkflowId,
        correlation?.EvidenceId);

    private static DevBridgeUiScreenshotResult FailureResult(
        DevBridgeUiOutcome outcome,
        string code,
        string error,
        DevBridgeUiStatus? correlation = null) => new(
        Failure(outcome, code, error, correlation),
        null);

    private static DevBridgeUiOutcome MapToolFailureOutcome(JsonElement? result) => result.HasValue
        ? MapFailureCode(
            GetString(result.Value, "errorCode", "code") ?? string.Empty,
            GetString(result.Value, "error", "message") ?? string.Empty)
        : DevBridgeUiOutcome.InfrastructureFailure;

    private static DevBridgeUiOutcome MapFailureCode(string code, string error)
    {
        string value = code + " " + error;
        if (ContainsAny(value, "VISUAL", "READINESS", "LOADING"))
        {
            return DevBridgeUiOutcome.VisualReadinessFailure;
        }

        if (ContainsAny(
            value,
            "LEASE",
            "UNAVAILABLE",
            "NOT_READY",
            "NOT_CONFIGURED",
            "ENDPOINT",
            "AUTH"))
        {
            return DevBridgeUiOutcome.Unavailable;
        }

        if (ContainsAny(value, "TARGET_NOT_FOUND", "NOT_FOUND", "CLIP"))
        {
            return DevBridgeUiOutcome.TargetNotFound;
        }

        if (ContainsAny(value, "TIMEOUT", "TIMED_OUT"))
        {
            return DevBridgeUiOutcome.Timeout;
        }

        return DevBridgeUiOutcome.InfrastructureFailure;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement? value, params string[] names) =>
        TryGetString(value, out string? result, names) ? result : null;

    private static bool TryGetString(
        JsonElement? value,
        out string? result,
        params string[] names)
    {
        result = null;
        if (!value.HasValue ||
            !TryGetProperty(value.Value, out JsonElement property, names))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return true;
    }

    private static bool? GetOptionalBoolean(JsonElement? value, params string[] names) =>
        TryGetOptionalBoolean(value, out bool? result, names) ? result : null;

    private static bool TryGetOptionalBoolean(
        JsonElement? value,
        out bool? result,
        params string[] names)
    {
        result = null;
        if (!value.HasValue ||
            !TryGetProperty(value.Value, out JsonElement property, names))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.True &&
            property.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        result = property.GetBoolean();
        return true;
    }

    private static bool TryGetArray(
        JsonElement? value,
        out JsonElement result,
        params string[] names)
    {
        result = default;
        return value.HasValue &&
            TryGetProperty(value.Value, out result, names) &&
            result.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetTargetArray(
        JsonElement? value,
        out JsonElement result)
    {
        if (TryGetArray(value, out result, "targets"))
        {
            return true;
        }

        if (!value.HasValue ||
            !TryGetProperty(value.Value, out JsonElement targets, "targets") ||
            targets.ValueKind != JsonValueKind.Object)
        {
            result = default;
            return false;
        }

        return TryGetArray(targets, out result, "windows");
    }

    private static JsonElement? GetElement(JsonElement? value, params string[] names) =>
        value.HasValue &&
        TryGetProperty(value.Value, out JsonElement result, names)
            ? result.Clone()
            : null;

    private static bool TryGetProperty(
        JsonElement value,
        out JsonElement result,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(name, out result))
            {
                return true;
            }
        }

        result = default;
        return false;
    }

    private static string Limit(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= MaxMessageLength
                ? value
                : value[..MaxMessageLength];

    private sealed record ToolCallResult(
        DevBridgeUiStatus Status,
        JsonElement? Result);
}
