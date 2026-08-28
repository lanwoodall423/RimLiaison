using System.Text.Json;
using RimLiaison.Observability;
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
        DevBridgeDevelopmentDescriptor? developmentDescriptor = null;
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
            developmentDescriptor = reconciliation.Descriptor;
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

        AgentOperationScope? buildObservation = AgentObservabilityRuntime.BeginOperation(
            "tool",
            "build.deploy",
            DevelopmentStage.Implementation,
            "build:" + project,
            new
            {
                toolName = "DevBridge",
                operationType = "build",
                project,
                sourceFingerprint
            });
        string commandText = AgentObservabilityData.SanitizeCommand(
            options.PowerShellPath + " " + string.Join(" ", arguments),
            4_096);
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildStarted,
            "Build and deployment transaction started.",
            new
            {
                operationKey = "build:" + project,
                project,
                command = commandText,
                workingDirectory = options.RootPath,
                sourceFingerprint,
                workflowId
            });
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
                        DevBridgeProcessEnvironment.ForWorkflow(workflowId),
                        OperationKey: "build:" + project),
                    cancellationToken)
                .ConfigureAwait(false);
            AgentDiagnosticEvidenceReference? stdoutEvidence =
                AgentObservabilityRuntime.PersistEvidence(
                    "devbridge.process.stdout",
                    process.Stdout,
                    process.StdoutTruncated);
            AgentDiagnosticEvidenceReference? stderrEvidence =
                AgentObservabilityRuntime.PersistEvidence(
                    "devbridge.process.stderr",
                    process.Stderr,
                    process.StderrTruncated);
            var processDetails = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["project"] = project,
                ["command"] = commandText,
                ["workingDirectory"] = options.RootPath,
                ["sourceFingerprint"] = sourceFingerprint,
                ["workflowId"] = workflowId,
                ["exitCode"] = process.ExitCode,
                ["stderrExcerpt"] = AgentObservabilityData.BoundText(process.Stderr, 2048),
                ["stdoutExcerpt"] = AgentObservabilityData.BoundText(process.Stdout, 2048),
                ["stdoutTruncated"] = process.StdoutTruncated,
                ["stderrTruncated"] = process.StderrTruncated,
                ["timedOut"] = process.TimedOut,
                ["cancelled"] = process.Cancelled
            };
            if (stdoutEvidence is not null)
            {
                processDetails["stdoutEvidenceId"] = stdoutEvidence.Id;
            }
            if (stderrEvidence is not null)
            {
                processDetails["stderrEvidenceId"] = stderrEvidence.Id;
            }
            if (process.ExitCode is 0 && process.StartError is null &&
                !process.TimedOut && !process.Cancelled)
            {
                buildObservation?.Complete("Build and deployment command completed.", processDetails);
                processDetails["operationKey"] = "build:" + project;
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildSucceeded,
                    "Build and deployment command succeeded.",
                    processDetails);
            }
            else
            {
                buildObservation?.Fail(
                    process.TimedOut
                        ? "Build and deployment command timed out."
                        : "Build and deployment command failed.",
                    process.TimedOut
                        ? "DEVBRIDGE_BUILD_TIMEOUT"
                        : "DEVBRIDGE_BUILD_FAILED",
                    processDetails,
                    timeout: process.TimedOut);
                processDetails["operationKey"] = "build:" + project;
                processDetails["errorCode"] = process.TimedOut
                    ? "DEVBRIDGE_BUILD_TIMEOUT"
                    : "DEVBRIDGE_BUILD_FAILED";
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Implementation,
                    AgentEventTypes.BuildFailed,
                    process.TimedOut
                        ? "Build and deployment command timed out."
                        : "Build and deployment command failed.",
                    processDetails);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            buildObservation?.Fail("Build and deployment was cancelled.", "RIMTEST_CANCELLED");
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Implementation,
                AgentEventTypes.BuildFailed,
                "Build and deployment was cancelled.",
                new { operationKey = "build:" + project, project, errorCode = "RIMTEST_CANCELLED" });
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.Cancelled,
                    "RIMTEST_CANCELLED"),
                workflowId);
        }
        catch (Exception exception)
        {
            buildObservation?.Fail(
                "Build and deployment raised an exception.",
                "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                new { project, error = AgentObservabilityData.BoundText(exception.Message, 1024) });
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Implementation,
                AgentEventTypes.BuildFailed,
                "Build and deployment raised an exception.",
                new { operationKey = "build:" + project, project, errorCode = "DEVBRIDGE_MOD_TRANSACTION_FAILED" });
            return Failed(
                project,
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "DEVBRIDGE_MOD_TRANSACTION_FAILED",
                Bound(exception.Message)),
                workflowId);
        }
        finally
        {
            buildObservation?.Dispose();
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
            DevBridgeBuildDiagnostics? build = ParseBuild(root);
            IReadOnlyList<DevBridgeBuildOutputEvidence> buildOutputs =
                ResolveBuildOutputs(
                    fullRepositoryRoot,
                    deploymentRoot,
                    developmentDescriptor,
                    freshness,
                    transactionId);
            RecordBuildDiagnostics(
                project,
                sourceFingerprint,
                transactionId,
                responseWorkflowId ?? workflowId,
                process,
                build,
                freshness);

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
                    freshness,
                    build,
                    buildOutputs);
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
                    freshness,
                    build,
                    buildOutputs);
            }

            if (!success)
            {
                string errorCode = ReadFailureCode(root) ??
                    "DEVELOPMENT_TRANSACTION_FAILED";
                DevBridgeIdentityMismatch? identityMismatch =
                    DevBridgeIdentityMismatchParser.Parse(
                        root,
                        options.RootPath,
                        errorCode);
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
                        descriptorRecoveryAction,
                        identityMismatch),
                    false,
                    transactionId,
                    responseWorkflowId ?? workflowId,
                    generation,
                    leaseId,
                    freshness,
                    build,
                    buildOutputs);
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
                freshness,
                build,
                buildOutputs);
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
        TryGetNullableString(value, "builtPackageSha256", out string? builtPackageHash);
        TryGetNullableString(value, "deployedPackageSha256", out string? deployedPackageHash);
        TryGetNullableString(value, "deploymentManifestPath", out string? manifestPath);
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
            errorCode,
            builtPackageHash,
            deployedPackageHash,
            manifestPath);
    }

    private static IReadOnlyList<DevBridgeBuildOutputEvidence> ResolveBuildOutputs(
        string repositoryRoot,
        string deploymentRoot,
        DevBridgeDevelopmentDescriptor? descriptor,
        DevBridgeArtifactFreshness? freshness,
        string? transactionId)
    {
        if (descriptor is null ||
            string.IsNullOrWhiteSpace(freshness?.BuiltArtifactSha256) ||
            !string.Equals(
                freshness.BuiltArtifactSha256,
                freshness.DeployedArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            string root = Path.GetFullPath(repositoryRoot);
            string target = Path.GetFullPath(Path.Combine(
                deploymentRoot,
                descriptor.DeploymentTarget.Replace('/', Path.DirectorySeparatorChar)));
            string relative = Path.GetRelativePath(root, target).Replace('\\', '/');
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith("../", StringComparison.Ordinal))
            {
                return [];
            }

            return
            [
                new DevBridgeBuildOutputEvidence(
                    relative,
                    freshness.BuiltArtifactSha256!,
                    transactionId ?? freshness.TransactionId)
            ];
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException)
        {
            return [];
        }
    }

    private static DevBridgeBuildDiagnostics? ParseBuild(JsonElement root)
    {
        JsonElement? build = GetObject(root, "build");
        JsonElement? failure = GetObject(root, "failure");
        if (build is null && failure is null)
        {
            return null;
        }

        string? command = FirstString(build, failure, "command", "commandText");
        int? exitCode = FirstInt(build, failure, "exitCode");
        string? output = FirstString(build, null, "output", "stdout");
        string? diagnosticOutput = FirstString(failure, build, "diagnosticOutput", "output", "error");
        string? causalDiagnostic = FirstString(failure, build, "causalDiagnostic");
        string? errorOutput = FirstString(build, failure, "errorOutput", "stderr");
        string? sourceProject = FirstString(build, null, "sourceProject");
        string? stagingPath = FirstString(build, null, "stagingPath");
        bool? timedOut = FirstBoolean(build, failure, "timedOut");
        bool? cancelled = FirstBoolean(build, failure, "cancelled");
        string? builtSha256 = FirstString(build, null, "builtSha256", "builtArtifactSha256");
        string? configuration = FirstString(build, null, "configuration");
        string? workingDirectory = FirstString(build, failure, "workingDirectory", "workingContext", "cwd");
        string? sourceFingerprint = FirstString(build, null, "sourceFingerprint");
        string? failureMessage = FirstString(failure, build, "message", "error", "failureMessage");
        string? transactionId = FirstString(build, failure, "transactionId", "transaction");
        string? workflowId = FirstString(build, failure, "workflowId", "workflow");
        string? errorCode = FirstString(failure, build, "errorCode", "code");
        bool? outputTruncated = FirstBoolean(build, failure, "outputTruncated");
        bool? causalDiagnosticTruncated =
            FirstBoolean(failure, build, "causalDiagnosticTruncated") ??
            FirstBoolean(failure, build, "diagnosticOutputTruncated");
        string? diagnosticSignature = FirstString(failure, build, "diagnosticSignature");
        string? rawStdoutPath = FirstString(build, failure, "rawStdoutPath");
        string? rawStderrPath = FirstString(build, failure, "rawStderrPath");
        string? rawNativeStdoutPath = FirstString(build, failure, "rawNativeStdoutPath");
        string? rawNativeStderrPath = FirstString(build, failure, "rawNativeStderrPath");
        string? orchestrator = FirstString(build, failure, "orchestrator");
        string? failureSurface = FirstString(build, failure, "failureSurface");
        string? likelyOwner = FirstString(build, failure, "likelyOwner");
        string? ownershipConfidence = FirstString(build, failure, "ownershipConfidence");
        string? ownershipBasis = FirstString(build, failure, "ownershipBasis");
        JsonElement? ownership = build is { ValueKind: JsonValueKind.Object } buildObject
            ? GetObject(buildObject, "ownership")
            : failure is { ValueKind: JsonValueKind.Object } failureObject
                ? GetObject(failureObject, "ownership")
                : null;
        JsonElement? discrimination = root.TryGetProperty("buildDiscrimination", out JsonElement discriminator) &&
            discriminator.ValueKind == JsonValueKind.Object
            ? discriminator
            : null;
        // Ownership may be nested in the build/failure object.
        if (ownership is { ValueKind: JsonValueKind.Object } ownershipObject)
        {
            likelyOwner ??= FirstString(ownershipObject, null, "likelyOwner");
            ownershipConfidence ??= FirstString(ownershipObject, null, "confidence", "ownershipConfidence");
            ownershipBasis ??= FirstString(ownershipObject, null, "basis", "ownershipBasis");
            orchestrator ??= FirstString(ownershipObject, null, "orchestrator");
            failureSurface ??= FirstString(ownershipObject, null, "failureSurface");
        }
        if (command is null && exitCode is null && output is null &&
            diagnosticOutput is null && causalDiagnostic is null && errorOutput is null && sourceProject is null &&
            stagingPath is null && timedOut is null && cancelled is null &&
            builtSha256 is null && configuration is null && workingDirectory is null &&
            sourceFingerprint is null && failureMessage is null && transactionId is null &&
            workflowId is null && errorCode is null && outputTruncated is null &&
            diagnosticSignature is null && rawStdoutPath is null && rawStderrPath is null)
        {
            return null;
        }

        return new DevBridgeBuildDiagnostics(
            command,
            exitCode,
            output,
            sourceProject,
            stagingPath,
            timedOut,
            builtSha256,
            diagnosticOutput,
            errorOutput,
            configuration,
            cancelled,
            workingDirectory,
            sourceFingerprint,
            failureMessage,
            transactionId,
            workflowId,
            errorCode,
            outputTruncated,
            causalDiagnostic,
            causalDiagnosticTruncated,
            diagnosticSignature,
            rawStdoutPath,
            rawStderrPath,
            rawNativeStdoutPath,
            rawNativeStderrPath,
            orchestrator,
            failureSurface,
            likelyOwner,
            ownershipConfidence,
            ownershipBasis,
            discrimination);
    }

    private static void RecordBuildDiagnostics(
        string project,
        string sourceFingerprint,
        string? transactionId,
        string? workflowId,
        DevBridgeProcessResult process,
        DevBridgeBuildDiagnostics? build,
        DevBridgeArtifactFreshness? freshness)
    {
        if (build is null)
        {
            return;
        }

        AgentDiagnosticEvidenceReference? outputEvidence =
            AgentObservabilityRuntime.PersistEvidence(
                "devbridge.build.output",
                build.Output,
                build.OutputTruncated ?? false);
        AgentDiagnosticEvidenceReference? diagnosticEvidence =
            AgentObservabilityRuntime.PersistEvidence(
                "devbridge.build.diagnostics",
                build.CausalDiagnostic ?? build.DiagnosticOutput,
                build.CausalDiagnosticTruncated ?? build.OutputTruncated ?? false);
        AgentDiagnosticEvidenceReference? errorEvidence =
            AgentObservabilityRuntime.PersistEvidence(
                "devbridge.build.error",
                build.ErrorOutput,
                false);
        AgentDiagnosticEvidenceReference? rawStdoutEvidence =
            PersistRawEvidence("devbridge.build.raw-stdout", build.RawStdoutPath);
        AgentDiagnosticEvidenceReference? rawStderrEvidence =
            PersistRawEvidence("devbridge.build.raw-stderr", build.RawStderrPath);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationKey"] = "build:" + project,
            ["project"] = project,
            ["orchestrator"] = build.Orchestrator ?? "DevBridge2",
            ["failureSurface"] = build.FailureSurface ?? "project-build",
            ["sourceFingerprint"] = build.SourceFingerprint ?? sourceFingerprint,
            ["transactionId"] = transactionId ?? build.TransactionId,
            ["workflowId"] = workflowId ?? build.WorkflowId,
            ["command"] = build.Command,
            ["workingDirectory"] = build.WorkingDirectory,
            ["sourceProject"] = build.SourceProject,
            ["stagingPath"] = build.StagingPath,
            ["configuration"] = build.Configuration,
            ["exitCode"] = build.ExitCode ?? process.ExitCode,
            ["timedOut"] = build.TimedOut ?? process.TimedOut,
            ["cancelled"] = build.Cancelled ?? process.Cancelled,
            ["output"] = AgentObservabilityData.BoundText(build.Output, 1_024),
            ["causalDiagnostic"] = AgentObservabilityData.BoundText(build.CausalDiagnostic, 4_096),
            ["diagnosticOutput"] = AgentObservabilityData.BoundText(build.DiagnosticOutput, 1_024),
            ["errorOutput"] = AgentObservabilityData.BoundText(build.ErrorOutput, 1_024),
            ["outputTruncated"] = build.OutputTruncated ?? outputEvidence?.Truncated ?? false,
            ["causalDiagnosticTruncated"] = build.CausalDiagnosticTruncated ?? false,
            ["diagnosticOutputTruncated"] = diagnosticEvidence?.Truncated ?? false,
            ["errorOutputTruncated"] = errorEvidence?.Truncated ?? false,
            ["diagnosticSignature"] = build.DiagnosticSignature,
            ["likelyOwner"] = build.LikelyOwner,
            ["ownershipConfidence"] = build.OwnershipConfidence,
            ["ownershipBasis"] = build.OwnershipBasis,
            ["buildDiscrimination"] = build.Discrimination,
            ["builtSha256"] = build.BuiltSha256,
            ["deployedArtifactSha256"] = freshness?.DeployedArtifactSha256,
            ["deploymentDecision"] = freshness?.DeploymentDecision,
            ["generationBefore"] = freshness?.GenerationBefore,
            ["generationAfter"] = freshness?.GenerationAfter,
            ["generation"] = freshness?.Generation,
            ["loadedArtifactFreshnessProven"] = freshness?.LoadedArtifactFreshnessProven,
            ["freshnessState"] = freshness?.Proof ?? freshness?.DeploymentDecision,
            ["failureMessage"] = build.FailureMessage,
            ["errorCode"] = build.ErrorCode
        };
        if (outputEvidence is not null)
        {
            data["outputEvidenceId"] = outputEvidence.Id;
        }
        if (diagnosticEvidence is not null)
        {
            data["diagnosticEvidenceId"] = diagnosticEvidence.Id;
        }
        if (diagnosticEvidence is not null &&
            !string.IsNullOrWhiteSpace(build.CausalDiagnostic))
        {
            data["causalDiagnosticEvidenceId"] = diagnosticEvidence.Id;
        }
        if (errorEvidence is not null)
        {
            data["errorOutputEvidenceId"] = errorEvidence.Id;
        }
        if (rawStdoutEvidence is not null)
        {
            data["rawStdoutEvidenceId"] = rawStdoutEvidence.Id;
        }
        if (rawStderrEvidence is not null)
        {
            data["rawStderrEvidenceId"] = rawStderrEvidence.Id;
        }

        AgentObservabilityRuntime.Record(
            DevelopmentStage.Implementation,
            AgentEventTypes.BuildDiagnostics,
            "DevBridge returned structured build diagnostics.",
            data);
    }

    private static AgentDiagnosticEvidenceReference? PersistRawEvidence(
        string kind,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path)
                ? AgentObservabilityRuntime.PersistEvidence(
                    kind,
                    File.ReadAllText(path),
                    false)
                : null;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static JsonElement? GetObject(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value;
    }

    private static string? FirstString(
        JsonElement? first,
        JsonElement? second,
        params string[] names)
    {
        foreach (JsonElement? source in new[] { first, second })
        {
            if (source is not { ValueKind: JsonValueKind.Object } element)
            {
                continue;
            }

            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return BoundDiagnostic(value.GetString());
                }
            }
        }

        return null;
    }

    private static int? FirstInt(
        JsonElement? first,
        JsonElement? second,
        string name)
    {
        foreach (JsonElement? source in new[] { first, second })
        {
            if (source is not { ValueKind: JsonValueKind.Object } element ||
                !element.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int parsed))
            {
                continue;
            }

            return parsed;
        }

        return null;
    }

    private static bool? FirstBoolean(
        JsonElement? first,
        JsonElement? second,
        string name)
    {
        foreach (JsonElement? source in new[] { first, second })
        {
            if (source is not { ValueKind: JsonValueKind.Object } element ||
                !element.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                continue;
            }

            return value.GetBoolean();
        }

        return null;
    }

    private static string? BoundDiagnostic(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : AgentObservabilityData.BoundText(value, 16 * 1024);

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
