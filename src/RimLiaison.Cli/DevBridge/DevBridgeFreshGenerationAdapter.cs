using System.Text.Json;
using RimLiaison.Recovery;

namespace RimLiaison.DevBridge;

/// <summary>
/// Requests a new READY generation through DevBridge's lifecycle command.  A
/// recipe lease is deliberately not used to authorize this operation: the
/// resulting lease must belong to the generation whose profile was requested.
/// </summary>
public sealed class DevBridgeFreshGenerationAdapter : IDevBridgeFreshGenerationAdapter
{
    private const int MaxProjects = 64;
    private const int MaxInputs = 64;
    private const int MaxArgumentLength = 512;

    private readonly IDevBridgeRecipeAdapter recipeAdapter;
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeFreshGenerationAdapter(
        IDevBridgeRecipeAdapter recipeAdapter,
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.recipeAdapter = recipeAdapter ?? throw new ArgumentNullException(nameof(recipeAdapter));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DevBridgeFreshGenerationResult> EnsureFreshGenerationAsync(
        string recipeId,
        int? previousGeneration,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return Failure("RIMTEST_FRESH_GENERATION_REQUEST_INVALID", "A recipe id is required.");
        }

        DevBridgeRecipeShowResult show;
        try
        {
            show = await recipeAdapter.ShowAsync(recipeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("RIMTEST_CANCELLED", "The fresh-generation request was cancelled.",
                DevBridgeOutcomeKind.Cancelled);
        }
        catch (Exception exception)
        {
            return Failure("DEVBRIDGE_RECIPE_SHOW_FAILED", Bound(exception.Message));
        }

        if (!show.Status.IsSuccess || show.Definition is null)
        {
            return new DevBridgeFreshGenerationResult(
                show.Status,
                null);
        }

        if (!TryBuildRestartArguments(show.Definition.Value, out List<string> arguments,
                out string? argumentError))
        {
            return Failure("DEVBRIDGE_RECIPE_PROFILE_INVALID", argumentError);
        }

        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.CommandPath,
                        options.RootPath,
                        ["--root", options.RootPath, "restart", .. arguments, "--json"],
                        options.RunTimeout,
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes,
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("RIMTEST_CANCELLED", "The fresh-generation request was cancelled.",
                DevBridgeOutcomeKind.Cancelled);
        }
        catch (Exception exception)
        {
            return Failure("DEVBRIDGE_RESTART_START_FAILED", Bound(exception.Message));
        }

        if (process.Cancelled)
        {
            return Failure("RIMTEST_CANCELLED", "The fresh-generation request was cancelled.",
                DevBridgeOutcomeKind.Cancelled, process);
        }

        if (process.TimedOut)
        {
            return Failure("DEVBRIDGE_RESTART_TIMEOUT",
                "The bounded DevBridge restart request timed out.",
                DevBridgeOutcomeKind.Timeout, process);
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            return Failure("DEVBRIDGE_RESTART_OUTPUT_LIMIT_EXCEEDED",
                "The DevBridge restart response exceeded its output bound.",
                DevBridgeOutcomeKind.MalformedResponse, process);
        }

        if (!TryParseLastObject(process.Stdout, out JsonDocument? document))
        {
            return Failure("DEVBRIDGE_RESTART_RESPONSE_INVALID",
                Bound(process.StartError ?? process.Stderr),
                string.IsNullOrWhiteSpace(process.StartError)
                    ? DevBridgeOutcomeKind.MalformedResponse
                    : DevBridgeOutcomeKind.InfrastructureFailure,
                process);
        }

        using (document!)
        {
            JsonElement root = document!.RootElement;
            bool success = TryGetBoolean(root, "success", out bool reportedSuccess) &&
                reportedSuccess &&
                TryGetInt(root, "exitCode", out int exitCode) && exitCode == 0 &&
                process.ExitCode is null or 0;
            string? errorCode = TryGetNullableString(root, "errorCode", out string? parsedCode)
                ? parsedCode
                : null;
            string? error = TryGetNullableString(root, "error", out string? parsedError)
                ? parsedError
                : null;
            if (!success)
            {
                string effectiveErrorCode = errorCode ?? "DEVBRIDGE_RESTART_FAILED";
                DevBridgeIdentityMismatch? identityMismatch =
                    DevBridgeIdentityMismatchParser.Parse(
                        root,
                        options.RootPath,
                        effectiveErrorCode);
                return Failure(
                    effectiveErrorCode,
                    error ?? Bound(process.Stderr),
                    process.ExitCode is > 0
                        ? DevBridgeOutcomeKind.DevBridgeRefusal
                        : DevBridgeOutcomeKind.InfrastructureFailure,
                    process,
                    identityMismatch: identityMismatch);
            }

            if (!TryGetString(root, "state", out string? state) ||
                !TryGetInt(root, "generation", out int generation) ||
                !TryGetBoolean(root, "restartPending", out bool restartPending))
            {
                return Failure("DEVBRIDGE_RESTART_RESPONSE_INVALID",
                    "The restart response did not include typed readiness fields.",
                    DevBridgeOutcomeKind.MalformedResponse,
                    process);
            }

            if (!string.Equals(state, "READY", StringComparison.OrdinalIgnoreCase) ||
                restartPending || generation <= 0)
            {
                return Failure("DEVBRIDGE_FRESH_GENERATION_NOT_READY",
                    "DevBridge did not prove a new READY generation.",
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    process,
                    generation > 0 ? generation : null);
            }

            if (previousGeneration.HasValue && generation <= previousGeneration.Value)
            {
                return Failure("DEVBRIDGE_FRESH_GENERATION_NOT_NEW",
                    "DevBridge returned a generation that is not newer than the prior state.",
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    process,
                    generation);
            }

            return new DevBridgeFreshGenerationResult(
                SuccessStatus(process),
                generation,
                LaunchesConsumed: 1);
        }
    }

    private static bool TryBuildRestartArguments(
        JsonElement definition,
        out List<string> arguments,
        out string? error)
    {
        arguments = [];
        error = null;
        if (definition.ValueKind != JsonValueKind.Object ||
            !definition.TryGetProperty("projects", out JsonElement projects) ||
            projects.ValueKind != JsonValueKind.Array ||
            projects.GetArrayLength() > MaxProjects ||
            !definition.TryGetProperty("inputs", out JsonElement inputs) ||
            inputs.ValueKind != JsonValueKind.Object ||
            inputs.EnumerateObject().Count() > MaxInputs)
        {
            error = "The recipe definition did not contain bounded projects and inputs.";
            return false;
        }

        var projectNames = new List<string>();
        foreach (JsonElement project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.String ||
                !TryBoundArgument(project.GetString(), out string? value))
            {
                error = "The recipe definition contains an invalid project alias.";
                return false;
            }

            projectNames.Add(value!);
        }

        arguments.Add("--projects");
        arguments.Add(projectNames.Count == 0 ? "none" : string.Join(",", projectNames));

        foreach (JsonProperty input in inputs.EnumerateObject().OrderBy(
                     static value => value.Name,
                     StringComparer.Ordinal))
        {
            if (!TryJsonScalar(input.Value, out string? value) ||
                !TryBoundArgument(input.Name, out string? name) ||
                !TryBoundArgument(value, out string? boundedValue))
            {
                error = "The recipe definition contains an invalid test input.";
                return false;
            }

            arguments.Add("--input");
            arguments.Add(name + "=" + boundedValue);
        }

        return true;
    }

    private static bool TryJsonScalar(JsonElement element, out string? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null
        };
        return value is not null;
    }

    private static bool TryBoundArgument(string? value, out string? bounded)
    {
        bounded = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return bounded is not null && bounded.Length <= MaxArgumentLength &&
            !bounded.Contains('\0');
    }

    private static bool TryParseLastObject(string? output, out JsonDocument? document)
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
                // Progress output precedes the bounded final object.
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

    private static bool TryGetString(JsonElement parent, string name, out string? value)
    {
        value = null;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            (value = element.GetString()) is not null;
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

    private static DevBridgeFreshGenerationResult Failure(
        string code,
        string? error,
        DevBridgeOutcomeKind outcome = DevBridgeOutcomeKind.InfrastructureFailure,
        DevBridgeProcessResult? process = null,
        int? generation = null,
        DevBridgeIdentityMismatch? identityMismatch = null) =>
        new(
            new DevBridgeAdapterStatus(
                outcome,
                code,
                Bound(error),
                process?.ExitCode,
                Bound(process?.Stderr),
                "devbridge-command/v1",
                IdentityMismatch: identityMismatch),
            generation);

    private static DevBridgeAdapterStatus SuccessStatus(DevBridgeProcessResult process) =>
        new(
            DevBridgeOutcomeKind.Success,
            null,
            null,
            process.ExitCode,
            Bound(process.Stderr),
            "devbridge-command/v1");

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
