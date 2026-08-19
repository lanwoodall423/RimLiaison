using System.Text.Json;
using RimLiaison.Recovery;

namespace RimLiaison.DevBridge;

public sealed class DevBridgeModDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
{
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeModDevelopmentAdapterOptions options;

    public DevBridgeModDevelopmentAdapter(
        IDevBridgeProcessTransport transport,
        DevBridgeModDevelopmentAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            project,
            repositoryRoot,
            sourceFingerprint,
            workflowId,
            executionContext: null,
            cancellationToken);

    public async Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        DevBridgeModDevelopmentExecutionContext? executionContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryRoot) ||
            string.IsNullOrWhiteSpace(sourceFingerprint))
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_MOD_TRANSACTION_INPUT_INVALID",
                    "The mod-development transaction requires a project, repository root, and source fingerprint."),
                workflowId);
        }

        string fullRepositoryRoot;
        try
        {
            fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_MOD_TRANSACTION_INPUT_INVALID",
                    "The repository root is invalid."),
                workflowId);
        }

        string descriptorPath = ResolveDescriptorPath(project);
        PrerequisiteRecoveryState descriptorRecoveryState =
            PrerequisiteRecoveryState.Ready;
        int descriptorRecoveryAttempts = 0;
        string? descriptorRecoveryAction = null;
        if (options.EnableDescriptorRecovery)
        {
            DevBridgeDescriptorReconciliationResult reconciliation =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    project,
                    fullRepositoryRoot,
                    descriptorPath,
                    options);
            if (!reconciliation.CanProceed &&
                reconciliation.State != PrerequisiteRecoveryState.Ready)
            {
                return Failed(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        reconciliation.ErrorCode ?? "DEVBRIDGE_DESCRIPTOR_RECONCILIATION_FAILED",
                        reconciliation.Error ?? "The DevBridge development descriptor could not be reconciled.",
                        RecoveryState: reconciliation.State,
                        RecoveryAttempts: reconciliation.Attempts,
                        RecoveryAction: reconciliation.Action),
                    workflowId);
            }

            // A recovered descriptor is now the input to the authoritative
            // DevBridge2 transaction.  ResolveDeploymentRoot reads the
            // reconciled file, preserving the existing deployment ownership.
            descriptorPath = reconciliation.DescriptorPath;
            descriptorRecoveryState = reconciliation.State;
            descriptorRecoveryAttempts = reconciliation.Attempts;
            descriptorRecoveryAction = reconciliation.Action;
        }

        string deploymentRoot = ResolveDeploymentRoot(
            descriptorPath,
            fullRepositoryRoot);
        string scriptPath = Path.Combine(options.RootPath, "scripts", "mod-test.ps1");
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-Project",
            project,
            "-DescriptorPath",
            descriptorPath,
            "-CoordinatorRoot",
            options.RootPath,
            "-DevelopmentRoot",
            fullRepositoryRoot,
            "-AdditionalDevelopmentRoot",
            options.RootPath,
            "-DeploymentRoot",
            deploymentRoot,
            "-SourceFingerprint",
            sourceFingerprint,
            "-SkipRecipe",
            "-Json"
        };
        if (!string.IsNullOrWhiteSpace(executionContext?.LeaseId))
        {
            arguments.Insert(arguments.Count - 2, "-LeaseId");
            arguments.Insert(arguments.Count - 2, executionContext.LeaseId!);
        }
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            arguments.Insert(arguments.Count - 2, "-WorkflowId");
            arguments.Insert(arguments.Count - 2, workflowId);
        }

        DevBridgeProcessResult process;
        try
        {
            process = await transport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.PowerShellPath,
                        options.RootPath,
                        arguments,
                        options.Timeout,
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes,
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"),
                workflowId);
        }
        catch (Exception exception)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                    Bound(exception.Message)),
                workflowId);
        }

        if (process.Cancelled)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED",
                    null,
                    process.ExitCode,
                    Bound(process.Stderr)),
                workflowId);
        }

        if (process.TimedOut)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Timeout,
                    "DEVBRIDGE_MOD_TRANSACTION_TIMEOUT",
                    "The bounded mod-development transaction timed out.",
                    process.ExitCode,
                    Bound(process.Stderr)),
                workflowId);
        }

        if (process.StdoutTruncated)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.MalformedResponse,
                    "DEVBRIDGE_MOD_TRANSACTION_OUTPUT_TRUNCATED",
                    "The mod-development transaction result exceeded the bounded output limit.",
                    process.ExitCode,
                    Bound(process.Stderr),
                    DevBridgeModDevelopmentSchemas.Current),
                workflowId);
        }

        if (string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    process.StartError is null
                        ? DevBridgeOutcomeKind.MalformedResponse
                        : DevBridgeOutcomeKind.InfrastructureFailure,
                    process.StartError is null
                        ? "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING"
                        : "DEVBRIDGE_MOD_TRANSACTION_START_FAILED",
                    Bound(process.StartError ?? process.Stderr),
                    process.ExitCode,
                    Bound(process.Stderr)),
                workflowId);
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
        catch (JsonException exception)
        {
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.MalformedResponse,
                    "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
                    Bound(exception.Message),
                    process.ExitCode,
                    Bound(process.Stderr),
                    DevBridgeModDevelopmentSchemas.Current),
                workflowId);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? schema = null;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "schemaVersion", out schema) ||
                !string.Equals(
                    schema,
                    DevBridgeModDevelopmentSchemas.Current,
                    StringComparison.Ordinal))
            {
                return Failed(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.IncompatibleSchema,
                        "DEVBRIDGE_MOD_TRANSACTION_SCHEMA_UNSUPPORTED",
                        "The mod-development transaction returned an unsupported schema.",
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema),
                    workflowId);
            }

            if (!TryGetBoolean(root, "success", out bool success))
            {
                return Failed(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.MalformedResponse,
                        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
                        "The mod-development transaction did not return a boolean success field.",
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema),
                    workflowId);
            }

            TryGetNullableString(root, "project", out string? reportedProject);
            if (!string.IsNullOrWhiteSpace(reportedProject) &&
                !string.Equals(project, reportedProject, StringComparison.Ordinal))
            {
                return Failed(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.MalformedResponse,
                        "DEVBRIDGE_MOD_TRANSACTION_PROJECT_MISMATCH",
                        "The mod-development transaction returned a different project.",
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema),
                    workflowId);
            }

            TryGetNullableString(root, "transactionId", out string? transactionId);
            TryGetNullableString(root, "workflowId", out string? responseWorkflowId);
            TryGetNullableString(root, "leaseId", out string? leaseId);
            TryGetNullableInt(root, "generation", out int? generation);
            DevBridgeArtifactFreshness? freshness = ParseFreshness(root);

            if (!string.IsNullOrWhiteSpace(workflowId) &&
                !string.IsNullOrWhiteSpace(responseWorkflowId) &&
                !string.Equals(workflowId, responseWorkflowId, StringComparison.Ordinal))
            {
                return new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.MalformedResponse,
                        "DEVBRIDGE_WORKFLOW_ID_MISMATCH",
                        "The mod-development transaction returned a different workflow id.",
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema),
                    false,
                    transactionId,
                    workflowId,
                    generation,
                    leaseId,
                    freshness);
            }

            if (success &&
                (process.ExitCode is > 0 ||
                 !string.IsNullOrWhiteSpace(process.StartError)))
            {
                return new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_MOD_TRANSACTION_RESULT_CONFLICT",
                        "The transaction returned success with a non-success process result.",
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema),
                    false,
                    transactionId,
                    responseWorkflowId ?? workflowId,
                    generation,
                    leaseId,
                    freshness);
            }

            if (!success)
            {
                string errorCode = ReadFailureCode(root) ??
                    "DEVELOPMENT_TRANSACTION_FAILED";
                return new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        errorCode,
                        ReadFailureMessage(root),
                        process.ExitCode,
                        Bound(process.Stderr),
                        schema,
                        descriptorRecoveryState,
                        descriptorRecoveryAttempts,
                        descriptorRecoveryAction),
                    false,
                    transactionId,
                    responseWorkflowId ?? workflowId,
                    generation,
                    leaseId,
                    freshness);
            }

            return new DevBridgeModDevelopmentResult(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Success,
                    null,
                    null,
                    process.ExitCode,
                    Bound(process.Stderr),
                    schema,
                    descriptorRecoveryState,
                    descriptorRecoveryAttempts,
                    descriptorRecoveryAction),
                true,
                transactionId,
                responseWorkflowId ?? workflowId,
                generation,
                leaseId,
                freshness);
        }
    }

    private string ResolveDescriptorPath(string project)
    {
        if (!string.IsNullOrWhiteSpace(options.DescriptorPath))
        {
            return options.DescriptorPath!;
        }

        return Path.Combine(options.RootPath, "DevelopmentProjects", project + ".json");
    }

    private string ResolveDeploymentRoot(
        string descriptorPath,
        string repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(options.DeploymentRoot))
        {
            return options.DeploymentRoot!;
        }

        try
        {
            if (File.Exists(descriptorPath) &&
                new FileInfo(descriptorPath).Length <= 128 * 1024)
            {
                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(descriptorPath),
                    new JsonDocumentOptions { MaxDepth = 16 });
                if (TryGetString(
                        document.RootElement,
                        "deploymentTarget",
                        out string? relativeTarget) &&
                    !Path.IsPathRooted(relativeTarget))
                {
                    string candidate = Path.GetFullPath(
                        Path.Combine(repositoryRoot, relativeTarget!.Replace('/', Path.DirectorySeparatorChar)));
                    string? parent = Directory.GetParent(candidate)?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent) &&
                        Directory.Exists(parent))
                    {
                        return repositoryRoot;
                    }
                }
            }
        }
        catch (Exception)
        {
            // DevBridge2 performs the authoritative descriptor/path validation.
        }

        return options.RootPath;
    }

    private static DevBridgeArtifactFreshness? ParseFreshness(JsonElement root)
    {
        if (!root.TryGetProperty("artifactFreshness", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        TryGetNullableString(value, "sourceFingerprint", out string? sourceFingerprint);
        TryGetNullableString(value, "builtArtifactSha256", out string? builtHash);
        TryGetNullableString(value, "deployedArtifactSha256", out string? deployedHash);
        TryGetNullableString(value, "deploymentDecision", out string? deploymentDecision);
        TryGetNullableInt(value, "generationBefore", out int? generationBefore);
        TryGetNullableInt(value, "generationAfter", out int? generationAfter);
        TryGetNullableInt(value, "generation", out int? generation);
        TryGetBoolean(value, "loadedArtifactFreshnessProven", out bool loadedProven);
        TryGetNullableString(value, "proof", out string? proof);
        TryGetNullableString(value, "transactionId", out string? transactionId);
        TryGetNullableString(value, "workflowId", out string? workflowId);
        TryGetNullableString(value, "leaseId", out string? leaseId);
        TryGetNullableString(value, "errorCode", out string? errorCode);
        return new DevBridgeArtifactFreshness(
            sourceFingerprint,
            builtHash,
            deployedHash,
            deploymentDecision,
            generationBefore,
            generationAfter,
            generation,
            loadedProven,
            proof,
            transactionId,
            workflowId,
            leaseId,
            errorCode);
    }

    private static string? ReadFailureCode(JsonElement root)
    {
        if (root.TryGetProperty("failure", out JsonElement failure) &&
            failure.ValueKind == JsonValueKind.Object &&
            TryGetNullableString(failure, "errorCode", out string? nestedCode) &&
            !string.IsNullOrWhiteSpace(nestedCode))
        {
            return nestedCode;
        }

        return TryGetNullableString(root, "errorCode", out string? code)
            ? code
            : null;
    }

    private static string? ReadFailureMessage(JsonElement root)
    {
        if (root.TryGetProperty("failure", out JsonElement failure) &&
            failure.ValueKind == JsonValueKind.Object &&
            TryGetNullableString(failure, "message", out string? nestedMessage) &&
            !string.IsNullOrWhiteSpace(nestedMessage))
        {
            return Bound(nestedMessage);
        }

        return TryGetNullableString(root, "error", out string? message)
            ? Bound(message)
            : "The mod-development transaction failed.";
    }

    private static DevBridgeModDevelopmentResult Failed(
        string project,
        DevBridgeAdapterStatus status,
        string? workflowId) =>
        new(project, status, false, null, workflowId, null, null, null);

    private static bool TryGetString(
        JsonElement parent,
        string name,
        out string? value)
    {
        value = null;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
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

    private static bool TryGetBoolean(
        JsonElement parent,
        string name,
        out bool value)
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

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 4096 ? trimmed : trimmed[..4096];
    }

    private static void ValidateOptions(DevBridgeModDevelopmentAdapterOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.RootPath) ||
            value.Timeout <= TimeSpan.Zero ||
            value.MaxStdoutBytes <= 0 ||
            value.MaxStderrBytes <= 0)
        {
            throw new ArgumentException("DevBridge mod-development adapter options are invalid.");
        }
    }
}
