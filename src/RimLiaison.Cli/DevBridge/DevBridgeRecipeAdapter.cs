using System.Diagnostics;
using System.Text.Json;
using RimLiaison.Observability;
using RimLiaison.Profiling;

namespace RimLiaison.DevBridge;

public interface IDevBridgeRecipeAdapter
{
    Task<DevBridgeRecipeShowResult> ShowAsync(
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeRecipePlanResult> PlanAsync(
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        RunAsync(recipeId, cancellationToken);

    Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        string? workflowId,
        DevBridgeRecipeExecutionContext? executionContext,
        CancellationToken cancellationToken = default) =>
        RunAsync(recipeId, workflowId, cancellationToken);
}

public sealed class DevBridgeRecipeAdapter :
    IDevBridgeRecipeAdapter,
    IDevBridgeFixtureResetAdapter
{
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeAdapterOptions options;

    public DevBridgeRecipeAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public async Task<DevBridgeRecipeShowResult> ShowAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        DevBridgeProcessResult process = await InvokeAsync(
            "show",
            recipeId,
            options.ShowPlanTimeout,
            cancellationToken).ConfigureAwait(false);
        using ParsedEnvelope? envelope = ParseEnvelope(
            process,
            DevBridgeRecipeSchemas.Show,
            recipeId,
            out DevBridgeAdapterStatus? failure);
        if (envelope is null)
        {
            return new DevBridgeRecipeShowResult(recipeId, failure!, null);
        }

        JsonElement root = envelope.Root;
        if (!TryGetString(root, "errorCode", out string? errorCode, allowNull: true) ||
            !TryGetString(root, "error", out string? error, allowNull: true))
        {
            return MalformedShow(recipeId, process, "show response has invalid error fields.");
        }

        if (IsRefusal(envelope, process, errorCode))
        {
            return new DevBridgeRecipeShowResult(
                recipeId,
                RefusalStatus(envelope, process, errorCode, error),
                null);
        }

        if (!root.TryGetProperty("recipe", out JsonElement recipe) ||
            recipe.ValueKind != JsonValueKind.Object ||
            !TryGetString(recipe, "id", out string? reportedId) ||
            !string.Equals(reportedId, recipeId, StringComparison.Ordinal))
        {
            return MalformedShow(
                recipeId,
                process,
                "show response did not contain the requested recipe definition.");
        }

        if (!TryValidateRecipeSchema(recipe, out string? recipeSchemaError))
        {
            return MalformedShow(recipeId, process, recipeSchemaError!);
        }

        return new DevBridgeRecipeShowResult(
            recipeId,
            SuccessStatus(envelope, process),
            recipe.Clone());
    }

    public async Task<DevBridgeRecipePlanResult> PlanAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        DevBridgeProcessResult process = await InvokeAsync(
            "plan",
            recipeId,
            options.ShowPlanTimeout,
            cancellationToken).ConfigureAwait(false);
        using ParsedEnvelope? envelope = ParseEnvelope(
            process,
            DevBridgeRecipeSchemas.Plan,
            recipeId,
            out DevBridgeAdapterStatus? failure);
        if (envelope is null)
        {
            return new DevBridgeRecipePlanResult(recipeId, failure!, null);
        }

        JsonElement root = envelope.Root;
        if (!string.Equals(envelope.SchemaVersion, DevBridgeRecipeSchemas.Plan, StringComparison.Ordinal) &&
            IsRefusal(envelope, process, envelope.Response?.ErrorCode))
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                RefusalStatus(
                    envelope,
                    process,
                    envelope.Response?.ErrorCode,
                    envelope.Response?.Error),
                null);
        }

        if (!TryGetString(root, "recipe", out string? reportedId) ||
            !string.Equals(reportedId, recipeId, StringComparison.Ordinal))
        {
            return MalformedPlan(recipeId, process, "plan response recipe id did not match the request.");
        }

        if (!TryGetString(root, "errorCode", out string? errorCode, allowNull: true) ||
            !TryGetString(root, "error", out string? error, allowNull: true))
        {
            return MalformedPlan(recipeId, process, "plan response has invalid error fields.");
        }

        if (IsRefusal(envelope, process, errorCode))
        {
            return new DevBridgeRecipePlanResult(
                recipeId,
                RefusalStatus(envelope, process, errorCode, error),
                null);
        }

        if (!TryGetBoolean(root, "alreadySatisfied", out bool alreadySatisfied) ||
            !TryGetInt(root, "estimatedRimWorldLaunches", out int estimatedLaunches) ||
            !TryGetString(root, "nextAction", out string? nextAction, allowNull: true) ||
            !TryGetStringArray(root, "blockedBy", out List<string> blockedBy) ||
            !TryGetPlanSteps(root, out List<DevBridgeRecipePlanStep> steps))
        {
            return MalformedPlan(recipeId, process, "plan response is missing typed plan fields.");
        }

        return new DevBridgeRecipePlanResult(
            recipeId,
            SuccessStatus(envelope, process),
            new DevBridgeRecipePlan(
                recipeId,
                alreadySatisfied,
                estimatedLaunches,
                steps,
                nextAction,
                blockedBy));
    }

    public Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(recipeId, workflowId: null, cancellationToken);
    }

    public async Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
                recipeId,
                workflowId,
                executionContext: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DevBridgeRecipeRunResult> RunAsync(
        string recipeId,
        string? workflowId,
        DevBridgeRecipeExecutionContext? executionContext,
        CancellationToken cancellationToken = default)
    {
        DevBridgeProcessResult process = await InvokeRecipeRunAsync(
            recipeId,
            workflowId,
            executionContext,
            cancellationToken).ConfigureAwait(false);
        using ParsedEnvelope? envelope = ParseEnvelope(
            process,
            DevBridgeRecipeSchemas.Run,
            recipeId,
            out DevBridgeAdapterStatus? failure);
        if (envelope is null)
        {
            return FailedRun(recipeId, failure!, null, workflowId);
        }

        JsonElement root = envelope.Root;
        if (!string.Equals(envelope.SchemaVersion, DevBridgeRecipeSchemas.Run, StringComparison.Ordinal) &&
            IsRefusal(envelope, process, envelope.Response?.ErrorCode))
        {
            return FailedRun(
                recipeId,
                RefusalStatus(
                    envelope,
                    process,
                    envelope.Response?.ErrorCode,
                    envelope.Response?.Error),
                null,
                workflowId);
        }

        if (envelope.Response?.ErrorCode is string responseErrorCode &&
            !responseErrorCode.StartsWith("RECIPE_", StringComparison.Ordinal))
        {
            return FailedRun(
                recipeId,
                RefusalStatus(
                    envelope,
                    process,
                    responseErrorCode,
                    envelope.Response.Error),
                null,
                workflowId);
        }

        if (!TryGetString(root, "recipe", out string? reportedId) ||
            !string.Equals(reportedId, recipeId, StringComparison.Ordinal))
        {
            return FailedRun(
                recipeId,
                MalformedStatus(envelope, process, "run response recipe id did not match the request."),
                null,
                workflowId);
        }

        if (!TryGetBoolean(root, "success", out bool passed) ||
            !TryGetString(root, "errorCode", out string? errorCode, allowNull: true) ||
            !TryGetString(root, "error", out string? error, allowNull: true) ||
            !TryGetNullableInt(root, "generation", out int? generation) ||
            !TryGetNullableString(root, "runId", out string? runId) ||
            !TryGetNullableString(root, "leaseId", out string? leaseId) ||
            !TryGetNullableString(root, "evidence", out string? evidence) ||
            !TryGetNullableString(root, "evidenceId", out string? evidenceId) ||
            !TryGetNullableString(root, "failureFingerprint", out string? fingerprint) ||
            !TryGetNullableString(root, "finalNextAction", out string? nextAction) ||
            !TryGetNullableBoolean(root, "restartRequired", out bool? restartRequired) ||
            !TryGetNullableInt(root, "launchesConsumed", out int? launchesConsumed) ||
            !TryGetNullableString(root, "workflowId", out string? responseWorkflowId) ||
            !TryGetOperations(root, out List<DevBridgeOperationSummary> operations))
        {
            return FailedRun(
                recipeId,
                MalformedStatus(envelope, process, "run response is missing typed result fields."),
                null,
                workflowId);
        }

        if (!string.IsNullOrWhiteSpace(workflowId) &&
            !string.IsNullOrWhiteSpace(responseWorkflowId) &&
            !string.Equals(workflowId, responseWorkflowId, StringComparison.Ordinal))
        {
            return FailedRun(
                recipeId,
                MalformedStatus(
                    envelope,
                    process,
                    "DevBridge returned a workflow id that did not match the request."),
                null,
                workflowId,
                errorCode: "DEVBRIDGE_WORKFLOW_ID_MISMATCH");
        }

        if (passed &&
            (envelope.PayloadExitCode is > 0 ||
             process.ExitCode is > 0 ||
             !string.IsNullOrWhiteSpace(errorCode)))
        {
            return FailedRun(
                recipeId,
                InfrastructureStatus(
                    process,
                    "DEVBRIDGE_RESULT_CONFLICT",
                    "DevBridge returned success with a non-success process result."),
                passed,
                workflowId);
        }

        DevBridgeOutcomeKind outcome = passed
            ? DevBridgeOutcomeKind.Success
            : IsTestFailure(errorCode, operations)
                ? DevBridgeOutcomeKind.TestFailure
                : DevBridgeOutcomeKind.DevBridgeRefusal;
        var status = new DevBridgeAdapterStatus(
            outcome,
            errorCode,
            error,
            process.ExitCode,
            BoundStderr(process.Stderr),
            DevBridgeRecipeSchemas.Run,
            Response: envelope.Response,
            ProcessEvidence: process.Evidence);

        return new DevBridgeRecipeRunResult(
            recipeId,
            status,
            passed,
            runId,
            generation,
            leaseId,
            evidence,
            evidenceId,
            fingerprint,
            nextAction,
            restartRequired,
            launchesConsumed,
            operations,
            responseWorkflowId ?? workflowId);
    }

    private async Task<DevBridgeProcessResult> InvokeAsync(
        string operation,
        string recipeId,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workflowId = null,
        string? leaseId = null,
        string? environmentWorkflowId = null,
        string? sourceFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: "Recipe id is required.");
        }

        var arguments = new List<string>
        {
            "--root",
            options.RootPath,
            "test",
            "recipe",
            operation,
            recipeId,
            "--json"
        };
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            arguments.Add("--workflow-id");
            arguments.Add(workflowId);
        }
        if (!string.IsNullOrWhiteSpace(leaseId))
        {
            arguments.Add("--lease");
            arguments.Add(leaseId);
        }
        if (!string.IsNullOrWhiteSpace(sourceFingerprint))
        {
            arguments.Add("--source-fingerprint");
            arguments.Add(sourceFingerprint);
        }

        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.RootPath,
            arguments,
            timeout,
            options.MaxStdoutBytes,
            options.MaxStderrBytes,
            DevBridgeProcessEnvironment.ForWorkflow(environmentWorkflowId ?? workflowId));

        try
        {
            return await transport.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                Cancelled: true);
        }
        catch (Exception exception)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: exception.Message);
        }
    }

    private async Task<DevBridgeProcessResult> InvokeRecipeRunAsync(
        string recipeId,
        string? workflowId,
        DevBridgeRecipeExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process = await InvokeAsync(
            "run",
            recipeId,
            options.RunTimeout,
            cancellationToken,
            workflowId,
            executionContext?.LeaseId,
            sourceFingerprint: executionContext?.SourceFingerprint).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(workflowId) ||
            !IsWorkflowOptionRejected(process))
        {
            return process;
        }

        // An older coordinator rejects the additive request option before it can
        // mutate lifecycle state. Retry once without the optional context and
        // retain the caller's workflow locally.
        Activity? retryActivity = ProfilerActivity.Start(
            "devbridge.recipe.retry",
            "retry",
            phase: "devbridge",
            target: recipeId,
            scope: "workflow-option");
        AgentOperationScope? observation = AgentObservabilityRuntime.BeginOperation(
            "retry",
            "devbridge.recipe.retry",
            DevelopmentStage.Testing,
            "recipe:" + recipeId,
            new
            {
                toolName = "DevBridge",
                operationType = "recipe",
                recipe = recipeId,
                reason = "workflow-option"
            });
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.RetryStarted,
            "Retrying recipe without the optional workflow argument.",
            new
            {
                operationKey = "recipe:" + recipeId,
                recipe = recipeId,
                reason = "workflow-option"
            });
        bool retryStopped = false;
        try
        {
            DevBridgeProcessResult retry = await InvokeAsync(
                    "run",
                    recipeId,
                    options.RunTimeout,
                    cancellationToken,
                    leaseId: executionContext?.LeaseId,
                    environmentWorkflowId: workflowId,
                    sourceFingerprint: executionContext?.SourceFingerprint)
                .ConfigureAwait(false);
            ProfilerActivity.SetRetry(retryActivity, 1);
            ProfilerActivity.SetOutcome(
                retryActivity,
                retry.Cancelled
                    ? "cancelled"
                    : retry.TimedOut || retry.ExitCode is not 0
                        ? "failure"
                        : "success");
            ProfilerActivity.Stop(retryActivity);
            observation?.Complete(
                "DevBridge recipe retry completed.",
                new
                {
                    recipe = recipeId,
                    exitCode = retry.ExitCode,
                    outcome = retry.Cancelled
                        ? "cancelled"
                        : retry.TimedOut || retry.ExitCode is not 0
                            ? "failure"
                            : "success"
                });
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.RetryCompleted,
                "DevBridge recipe retry completed.",
                new
                {
                    operationKey = "recipe:" + recipeId,
                    recipe = recipeId,
                    outcome = retry.Cancelled
                        ? "cancelled"
                        : retry.TimedOut || retry.ExitCode is not 0
                            ? "failure"
                            : "success"
                });
            retryStopped = true;
            return retry;
        }
        catch (OperationCanceledException)
        {
            ProfilerActivity.Stop(retryActivity, "cancelled", "RIMTEST_CANCELLED");
            observation?.Fail("DevBridge recipe retry was cancelled.", "RIMTEST_CANCELLED");
            retryStopped = true;
            throw;
        }
        catch
        {
            ProfilerActivity.Stop(retryActivity, "failure", "DEVBRIDGE_RETRY_FAILED");
            observation?.Fail("DevBridge recipe retry failed.", "DEVBRIDGE_RETRY_FAILED");
            retryStopped = true;
            throw;
        }
        finally
        {
            if (!retryStopped)
            {
                ProfilerActivity.Stop(retryActivity, "failure");
            }
            observation?.Dispose();
        }
    }

    public async Task<DevBridgeResetResult> ResetAsync(
        string resetRecipeId,
        string leaseId,
        int expectedGeneration,
        string? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resetRecipeId) ||
            string.IsNullOrWhiteSpace(leaseId) || expectedGeneration <= 0)
        {
            return new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RIMTEST_RESET_REQUEST_INVALID"),
                null,
                leaseId);
        }

        DevBridgeRecipeRunResult result = await RunAsync(
                resetRecipeId,
                workflowId,
                new DevBridgeRecipeExecutionContext(leaseId),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status.Outcome != DevBridgeOutcomeKind.Success ||
            result.Passed != true ||
            result.Generation != expectedGeneration ||
            !string.Equals(result.LeaseId, leaseId, StringComparison.Ordinal) ||
            result.RestartRequired == true ||
            (result.LaunchesConsumed ?? 0) != 0)
        {
            return new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    result.Status.ErrorCode ?? "DEVBRIDGE_RESET_NOT_VERIFIED",
                    "The deterministic reset recipe did not prove same-generation reset success.",
                    result.Status.ProcessExitCode,
                    result.Status.Stderr,
                    result.Status.ResponseSchema),
                result.Generation,
                result.LeaseId ?? leaseId);
        }

        return new DevBridgeResetResult(
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Success,
                null,
                null,
                result.Status.ProcessExitCode,
                result.Status.Stderr,
                result.Status.ResponseSchema),
            result.Generation,
            result.LeaseId);
    }

    private static bool IsWorkflowOptionRejected(DevBridgeProcessResult process)
    {
        if (process.Cancelled || process.TimedOut ||
            process.StdoutTruncated || string.IsNullOrWhiteSpace(process.Stdout))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                process.Stdout,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                TryGetString(root, "schemaVersion", out string? schema) &&
                string.Equals(schema, DevBridgeRecipeSchemas.Run, StringComparison.Ordinal) &&
                TryGetString(root, "errorCode", out string? errorCode, allowNull: true) &&
                string.Equals(errorCode, "TEST_RECIPE_USAGE", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParsedEnvelope? ParseEnvelope(
        DevBridgeProcessResult process,
        string expectedSchema,
        string recipeId,
        out DevBridgeAdapterStatus? failure)
    {
        _ = recipeId;
        failure = null;
        string stderr = BoundStderr(process.Stderr);
        if (process.Cancelled)
        {
            failure = new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Cancelled,
                "RIMTEST_CANCELLED",
                "The DevBridge client process was cancelled.",
                process.ExitCode,
                stderr,
                expectedSchema,
                ProcessEvidence: process.Evidence);
            return null;
        }

        if (process.TimedOut)
        {
            failure = new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Timeout,
                "DEVBRIDGE_CLIENT_TIMEOUT",
                "The bounded DevBridge client process timed out.",
                process.ExitCode,
                stderr,
                expectedSchema,
                ProcessEvidence: process.Evidence);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            failure = InfrastructureStatus(
                process,
                "DEVBRIDGE_START_FAILED",
                process.StartError!);
            return null;
        }

        if (process.StdoutTruncated || process.StderrTruncated)
        {
            failure = InfrastructureStatus(
                process,
                "DEVBRIDGE_OUTPUT_LIMIT_EXCEEDED",
                "The DevBridge client exceeded the adapter output bound.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            failure = InfrastructureStatus(
                process,
                "DEVBRIDGE_NO_STRUCTURED_RESPONSE",
                "DevBridge produced no structured JSON response.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                process.Stdout,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException)
        {
            failure = InfrastructureStatus(
                process,
                "DEVBRIDGE_RESPONSE_INVALID",
                "DevBridge returned malformed structured JSON.") with
            {
                Outcome = DevBridgeOutcomeKind.MalformedResponse
            };
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            failure = MalformedStatus(
                null,
                process,
                "DevBridge JSON response root must be an object.");
            return null;
        }
        DevBridgeProcessResponseParser.TryParse(
            process.Stdout,
            out DevBridgeProcessResponse? response);
        string? schemaVersion = response?.SchemaVersion;

        if (response?.RepresentsFailure(process.ExitCode) == true &&
            !string.Equals(schemaVersion, expectedSchema, StringComparison.Ordinal))
        {
            return new ParsedEnvelope(
                document,
                schemaVersion,
                response.ExitCode,
                response);
        }

        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            document.Dispose();
            failure = MalformedStatus(
                null,
                process,
                "DevBridge JSON response did not contain schemaVersion.");
            return null;
        }

        if (!string.Equals(schemaVersion, expectedSchema, StringComparison.Ordinal))
        {
            document.Dispose();
            failure = new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.IncompatibleSchema,
                "DEVBRIDGE_SCHEMA_UNSUPPORTED",
                $"Expected {expectedSchema}; received {schemaVersion}.",
                process.ExitCode,
                stderr,
                schemaVersion,
                Response: response,
                ProcessEvidence: process.Evidence);
            return null;
        }

        return new ParsedEnvelope(
            document,
            schemaVersion,
            response?.ExitCode,
            response);
    }

    private static bool TryValidateRecipeSchema(
        JsonElement recipe,
        out string? error)
    {
        error = null;
        if (!TryGetString(recipe, "schemaVersion", out string? schemaVersion))
        {
            error = "recipe definition did not contain schemaVersion.";
            return false;
        }

        if (!string.Equals(schemaVersion, DevBridgeRecipeSchemas.RecipeV1,
                StringComparison.Ordinal) &&
            !string.Equals(schemaVersion, DevBridgeRecipeSchemas.RecipeV2,
                StringComparison.Ordinal))
        {
            error = $"recipe definition schema {schemaVersion} is unsupported.";
            return false;
        }

        return true;
    }

    private static bool TryGetPlanSteps(
        JsonElement root,
        out List<DevBridgeRecipePlanStep> steps)
    {
        steps = [];
        if (!root.TryGetProperty("steps", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement step in value.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object ||
                !TryGetString(step, "action", out string? action) ||
                !TryGetString(step, "reasonCode", out string? reasonCode, allowNull: true) ||
                !TryGetString(step, "condition", out string? condition, allowNull: true) ||
                !TryGetString(step, "recipe", out string? recipe, allowNull: true))
            {
                return false;
            }

            steps.Add(new DevBridgeRecipePlanStep(action!, reasonCode, condition, recipe));
        }

        return true;
    }

    private static bool TryGetOperations(
        JsonElement root,
        out List<DevBridgeOperationSummary> operations)
    {
        operations = [];
        if (!root.TryGetProperty("operations", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement operation in value.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object ||
                !TryGetString(operation, "tool", out string? tool) ||
                !TryGetBoolean(operation, "success", out bool success) ||
                !TryGetString(operation, "errorCode", out string? errorCode, allowNull: true) ||
                !TryGetFailedAssertionPointers(
                    operation,
                    out List<string> failedAssertions) ||
                !TryGetNullableString(operation, "operationId", out string? operationId) ||
                !TryGetNullableString(operation, "workflowId", out string? workflowId) ||
                !TryGetNullableInt(operation, "generation", out int? generation) ||
                !TryGetNullableString(operation, "launchId", out string? launchId))
            {
                return false;
            }

            operations.Add(new DevBridgeOperationSummary(
                tool!,
                success,
                errorCode,
                failedAssertions,
                operationId,
                workflowId,
                generation,
                launchId));
        }

        return true;
    }

    private static bool TryGetFailedAssertionPointers(
        JsonElement operation,
        out List<string> pointers)
    {
        pointers = [];
        if (!operation.TryGetProperty("assertions", out JsonElement assertions) ||
            assertions.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (assertions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement assertion in assertions.EnumerateArray())
        {
            if (assertion.ValueKind != JsonValueKind.Object ||
                !TryGetString(assertion, "pointer", out string? pointer) ||
                !TryGetBoolean(assertion, "success", out bool success))
            {
                return false;
            }

            if (!success)
            {
                pointers.Add(pointer!);
            }
        }

        return true;
    }

    private static bool IsTestFailure(
        string? errorCode,
        IReadOnlyList<DevBridgeOperationSummary> operations)
    {
        if (operations.Any(operation => operation.FailedAssertionPointers.Count > 0))
        {
            return true;
        }

        return errorCode is "RECIPE_ASSERTION_FAILED" or
            "RECIPE_EXPECTED_FAILURE_NOT_RETURNED" or
            "RECIPE_EVIDENCE_MISMATCH";
    }

    private static bool IsRefusal(
        ParsedEnvelope envelope,
        DevBridgeProcessResult process,
        string? errorCode)
    {
        return !string.IsNullOrWhiteSpace(errorCode) ||
            envelope.PayloadExitCode is > 0 ||
            process.ExitCode is > 0;
    }

    private static DevBridgeRecipeShowResult MalformedShow(
        string recipeId,
        DevBridgeProcessResult process,
        string error)
    {
        return new DevBridgeRecipeShowResult(
            recipeId,
            MalformedStatus(null, process, error),
            null);
    }

    private static DevBridgeRecipePlanResult MalformedPlan(
        string recipeId,
        DevBridgeProcessResult process,
        string error)
    {
        return new DevBridgeRecipePlanResult(
            recipeId,
            MalformedStatus(null, process, error),
            null);
    }

    private static DevBridgeRecipeRunResult FailedRun(
        string recipeId,
        DevBridgeAdapterStatus status,
        bool? passed,
        string? workflowId = null,
        string? errorCode = null)
    {
        if (errorCode is not null)
        {
            status = status with { ErrorCode = errorCode };
        }

        return new DevBridgeRecipeRunResult(
            recipeId,
            status,
            passed,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            workflowId);
    }

    private static DevBridgeAdapterStatus SuccessStatus(
        ParsedEnvelope envelope,
        DevBridgeProcessResult process)
    {
        return new DevBridgeAdapterStatus(
            DevBridgeOutcomeKind.Success,
            null,
            null,
            process.ExitCode,
            BoundStderr(process.Stderr),
            envelope.SchemaVersion,
            Response: envelope.Response,
            ProcessEvidence: process.Evidence);
    }

    private static DevBridgeAdapterStatus RefusalStatus(
        ParsedEnvelope envelope,
        DevBridgeProcessResult process,
        string? errorCode,
        string? error)
    {
        return new DevBridgeAdapterStatus(
            DevBridgeOutcomeKind.DevBridgeRefusal,
            errorCode ?? "DEVBRIDGE_REFUSED",
            error,
            process.ExitCode,
            BoundStderr(process.Stderr),
            envelope.SchemaVersion,
            RecoveryAction: envelope.Response?.NextAction,
            Response: envelope.Response,
            ProcessEvidence: process.Evidence);
    }

    private static DevBridgeAdapterStatus MalformedStatus(
        ParsedEnvelope? envelope,
        DevBridgeProcessResult process,
        string error)
    {
        return new DevBridgeAdapterStatus(
            DevBridgeOutcomeKind.MalformedResponse,
            "DEVBRIDGE_RESPONSE_INVALID",
            error,
            process.ExitCode,
            BoundStderr(process.Stderr),
            envelope?.SchemaVersion,
            Response: envelope?.Response ?? process.Response,
            ProcessEvidence: process.Evidence);
    }

    private static DevBridgeAdapterStatus InfrastructureStatus(
        DevBridgeProcessResult process,
        string code,
        string error)
    {
        return new DevBridgeAdapterStatus(
            DevBridgeOutcomeKind.InfrastructureFailure,
            code,
            error,
            process.ExitCode,
            BoundStderr(process.Stderr),
            Response: process.Response,
            ProcessEvidence: process.Evidence);
    }

    private static bool TryGetString(
        JsonElement parent,
        string name,
        out string? value,
        bool allowNull = false)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            return allowNull;
        }

        if (element.ValueKind == JsonValueKind.Null && allowNull)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return allowNull || !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetNullableString(
        JsonElement parent,
        string name,
        out string? value)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            value = null;
            return true;
        }

        return TryGetString(parent, name, out value, allowNull: true);
    }

    private static bool TryGetBoolean(
        JsonElement parent,
        string name,
        out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetNullableBoolean(
        JsonElement parent,
        string name,
        out bool? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetInt(
        JsonElement parent,
        string name,
        out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value);
    }

    private static bool TryGetNullableInt(
        JsonElement parent,
        string name,
        out int? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetStringArray(
        JsonElement parent,
        string name,
        out List<string> values)
    {
        values = [];
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                return false;
            }

            values.Add(item.GetString()!);
        }

        return true;
    }

    private static string BoundStderr(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        return stderr.Length <= 4096 ? stderr : stderr[..4096];
    }

    private static void ValidateOptions(DevBridgeAdapterOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.CommandPath) ||
            string.IsNullOrWhiteSpace(value.RootPath))
        {
            throw new ArgumentException("DevBridge command path and root path are required.");
        }

        if (value.ShowPlanTimeout <= TimeSpan.Zero ||
            value.RunTimeout <= TimeSpan.Zero ||
            value.MaxStdoutBytes <= 0 ||
            value.MaxStderrBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private sealed class ParsedEnvelope(
        JsonDocument document,
        string? schemaVersion,
        int? payloadExitCode,
        DevBridgeProcessResponse? response) : IDisposable
    {
        public JsonElement Root => document.RootElement;
        public string? SchemaVersion { get; } = schemaVersion;
        public int? PayloadExitCode { get; } = payloadExitCode;
        public DevBridgeProcessResponse? Response { get; } = response;

        public void Dispose()
        {
            document.Dispose();
        }
    }
}
