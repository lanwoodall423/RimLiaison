using System.Text.Json;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using RimContext.Core.Content;

using RimContext.Core.Context;
using RimContext.Core.Impact;

using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.Doctor;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.RimError;
using RimLiaison.RimContext;
using RimLiaison.Recovery;
using RimLiaison.Results;
using RimLiaison.Stack;
using RimLiaison.Profiling;
using RimLiaison.Observability;
using RimLiaison.Provenance;
using RimLiaison.Benchmarking;
using RimLiaison.RimDev;
using RimLiaison.Qualification;
using RimLiaison.Validation;
using RimLiaison.Toolchain;

namespace RimLiaison;

public static class CliApplication
{
    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr)
    {
        return RunAsync(args, stdout, stderr).GetAwaiter().GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter)
    {
        return RunAsync(args, stdout, stderr, recipeAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter rimErrorAdapter)
    {
        return RunAsync(
                args,
                stdout,
                stderr,
                recipeAdapter,
                diagnosisAdapter: rimErrorAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter rimErrorAdapter,
        IRimContextImpactAdapter rimContextAdapter)
    {
        return RunAsync(
                args,
                stdout,
                stderr,
                recipeAdapter,
                diagnosisAdapter: rimErrorAdapter,
                impactAdapter: rimContextAdapter)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter? recipeAdapter = null,
        CancellationToken cancellationToken = default,
        IRimErrorDiagnosisAdapter? diagnosisAdapter = null,
        IRimContextImpactAdapter? impactAdapter = null,
        IDevBridgeProcessTransport? processTransport = null,
        IGitChangeProvider? gitChangeProvider = null,
        IDevBridgeCapabilityAdapter? capabilityAdapter = null,
        IDevBridgeUiAdapter? uiAdapter = null,
        IDevBridgeModDevelopmentAdapter? developmentAdapter = null,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter = null,
        IDevBridgeViewportAdapter? viewportAdapter = null,
        IDevBridgeLeaseAdapter? leaseAdapter = null,
        IAgentObservabilityStore? observabilityStore = null,
        IAgentObservabilityTelemetry? observabilityTelemetry = null,
        IDevBridgeFreshGenerationAdapter? freshGenerationRecoveryAdapter = null)
    {
        EfficiencyProfiler profiler = EfficiencyProfiler.Start();
        long started = Stopwatch.GetTimestamp();
        string? commandName = null;
        string? workflowId = null;
        int exitCode = CliExitCodes.InternalError;
        AgentObservabilityRun? observabilityRun = null;
        AgentObservabilitySession? observabilityAgent = null;
        IAgentObservabilityStore? eventStore = null;
        IDisposable? observabilityActivation = null;
        try
        {
            CliRequest request = CliParser.Parse(args);
            if (request.HelpRequested)
            {
                profiler.SetCommand("help");
                CliParser.WriteHelp(stdout);
                exitCode = CliExitCodes.Success;
                return exitCode;
            }
            commandName = request.Command.ToString().ToLowerInvariant();
            profiler.SetCommand(request.Command.ToString());
            eventStore = observabilityStore ??
                AgentObservabilityStore.CreateDefault();
            observabilityRun = new AgentObservabilityRun(
                profiler.RunId,
                eventStore,
                observabilityTelemetry);
            ObservabilityEntityIdentity observabilityEntity =
                ResolveObservabilityEntity(request);
            bool experimentalToolchain = request.ExperimentalToolchain ||
                request.Command is CliCommand.Qualification or CliCommand.ToolchainPromotion;
            string workloadKind = request.Command == CliCommand.Qualification
                ? "qualification"
                : "production";
            ProductionToolchainBinding? productionBinding = null;
            PromotedToolchainRecoveryResult? productionToolchainRecovery = null;
            ProductionToolchainBindingFailure? productionToolchainFailure = null;
            LegacyPromotionMigrationResult? legacyPromotionMigration = null;
            ProjectRuntimeBindingResult? projectBinding = null;
            if (!experimentalToolchain &&
                RequiresProjectBinding(request))
            {
                projectBinding = ResolveProjectBinding(request);
                if (!projectBinding.Succeeded)
                {
                    observabilityAgent = observabilityRun.CreateAgent(
                        LegacyModId(observabilityEntity),
                        observabilityEntity.DisplayName,
                        logicalAgentId: ResolveLogicalAgentId(),
                        entityIdentity: observabilityEntity,
                        workloadKind: workloadKind,
                        toolchainState: "unbound");
                    observabilityAgent.Start("command:" + request.Command.ToString().ToLowerInvariant());
                    observabilityActivation = observabilityAgent.Activate();
                    observabilityAgent.Record(
                        DevelopmentStage.Analysis,
                        "project.binding.failed",
                        projectBinding.Error ?? "Project binding failed.",
                        projectBinding.ToEvidence());
                    WriteJson(stdout, projectBinding.ToEvidence());
                    exitCode = CliExitCodes.ConservativeSelection;
                    return exitCode;
                }

            }

            if (!experimentalToolchain &&
                string.Equals(
                    request.StackManifest.Manifest?.Workload,
                    "production",
                    StringComparison.OrdinalIgnoreCase) &&
                RequiresProductionToolchainBinding(request))
            {
                legacyPromotionMigration = LegacyPromotionMigrationService.Ensure(
                    request.StackManifest.RepositoryRoot,
                    cancellationToken);
                ProductionToolchainBindingResolution resolution =
                    legacyPromotionMigration.State == LegacyPromotionMigrationState.Blocked
                        ? new(
                            null,
                            new ProductionToolchainBindingFailure(
                                legacyPromotionMigration.ErrorCode ??
                                    "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_UNAVAILABLE",
                                legacyPromotionMigration.Error ??
                                    "The active legacy promotion could not be made self-restorable.",
                                legacyPromotionMigration.NextAction ??
                                    "Create and intentionally promote a new qualified RimLiaison production package.",
                                [],
                                legacyPromotionMigration.PromotedFingerprint,
                                ManifestPath: Environment.GetEnvironmentVariable(
                                    "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST")))
                        : ProductionToolchainBindingResolver.Resolve(
                            request.StackManifest.RepositoryRoot,
                            requestedDevBridgePath: request.DevBridgePath,
                            requestedDevBridgeRoot: request.DevBridgeRootPath);
                if (!resolution.Succeeded &&
                    ProductionExecutionPolicy.IsPromotedToolchainIntegrityCode(
                        resolution.Failure?.ErrorCode))
                {
                    ProductionToolchainBindingFailure failure = resolution.Failure!;
                    productionToolchainFailure = failure;
                    DevBridgeAdapterOptions? recoveryOptions =
                        string.IsNullOrWhiteSpace(failure.DevBridgeRuntimeRoot)
                            ? null
                            : DevBridgeAdapterOptions.Discover(
                                rootPath: failure.DevBridgeRuntimeRoot);
                    productionToolchainRecovery =
                        await DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                                failure,
                                request.StackManifest.RepositoryRoot,
                                processTransport ?? new SystemDevBridgeProcessTransport(),
                                recoveryOptions,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    resolution = ProductionToolchainBindingResolver.Resolve(
                        request.StackManifest.RepositoryRoot,
                        requestedDevBridgePath: request.DevBridgePath,
                        requestedDevBridgeRoot: request.DevBridgeRootPath);
                }

                if (!resolution.Succeeded)
                {
                    ProductionToolchainBindingFailure failure = resolution.Failure!;
                    observabilityAgent = observabilityRun.CreateAgent(
                        LegacyModId(observabilityEntity),
                        observabilityEntity.DisplayName,
                        logicalAgentId: ResolveLogicalAgentId(),
                        entityIdentity: observabilityEntity,
                        workloadKind: workloadKind,
                        toolchainState: "unbound",
                        toolchainFingerprint: failure.ExpectedFingerprint);
                    observabilityAgent.Start("command:" + request.Command.ToString().ToLowerInvariant());
                    observabilityActivation = observabilityAgent.Activate();
                    observabilityAgent.Record(
                        DevelopmentStage.Analysis,
                        "toolchain.binding.failed",
                        failure.Error,
                        failure.ToEvidence());
                    if (productionToolchainRecovery is not null)
                    {
                        observabilityAgent.Record(
                            DevelopmentStage.Analysis,
                            AgentEventTypes.ToolFailed,
                            "Promoted production-toolchain recovery could not restore the required identity.",
                            new
                            {
                                operationKey = "cli:" + request.Command.ToString().ToLowerInvariant(),
                                issueKind = "TOOLING_FAILURE",
                                blocking = true,
                                projectImplicated = false,
                                recovered = false,
                                componentOwner = "RimLiaison",
                                errorCode = productionToolchainRecovery.ErrorCode,
                                underlyingErrorCode = failure.ErrorCode,
                                originalFault = failure.ErrorCode,
                                expectedPromotedFingerprint = failure.ExpectedFingerprint,
                                affectedArtifacts = failure.MismatchingArtifacts ?? failure.ExpectedArtifacts,
                                repairAttempted = true,
                                repairResult = "failed",
                                recoveryAction = productionToolchainRecovery.Action,
                                recoveryState = productionToolchainRecovery.State.ToWireName(),
                                recoveryVerification = productionToolchainRecovery.Verification,
                                verificationResult = productionToolchainRecovery.Verification,
                                elapsedRecoveryMs = productionToolchainRecovery.ElapsedRecoveryMilliseconds,
                                retryCount = 0,
                                retryResult = "not-attempted",
                                nextAction = failure.NextAction
                            });
                    }
                    WriteJson(
                        stdout,
                        new
                        {
                            schemaVersion = "rimliaison-toolchain-binding/v1",
                            status = "blocked",
                            owner = "RimLiaison",
                            code = productionToolchainRecovery?.ErrorCode ?? failure.ErrorCode,
                            error = productionToolchainRecovery?.Error ?? failure.Error,
                            nextAction = failure.NextAction,
                            projectImplicated = false,
                            expectedFingerprint = failure.ExpectedFingerprint,
                            currentExecutablePath = failure.CurrentExecutablePath,
                            devBridgeRuntimeRoot = failure.DevBridgeRuntimeRoot,
                            manifestPath = failure.ManifestPath,
                            expectedArtifacts = failure.ExpectedArtifacts,
                            mismatchingArtifacts = failure.MismatchingArtifacts,
                            recoveryAttempted = productionToolchainRecovery is not null,
                            recovery = productionToolchainRecovery
                        });
                    exitCode = CliExitCodes.ConservativeSelection;
                    return exitCode;
                }

                productionBinding = resolution.Binding!;
                request = request with
                {
                    DevBridgePath = productionBinding.DevBridgeCommandPath,
                    DevBridgeRootPath = productionBinding.DevBridgeRuntimeRoot
                };
            }

            string toolchainState = experimentalToolchain
                ? "experimental"
                : productionBinding is null
                    ? "unbound"
                    : "promoted";
            string? toolchainFingerprint = productionBinding?.Fingerprint;
            string modId = LegacyModId(observabilityEntity);
            observabilityAgent ??= observabilityRun.CreateAgent(
                modId,
                observabilityEntity.DisplayName,
                logicalAgentId: ResolveLogicalAgentId(),
                entityIdentity: observabilityEntity,
                workloadKind: workloadKind,
                toolchainState: toolchainState,
                qualificationProfile: request.Command == CliCommand.Qualification
                    ? request.Id
                    : null,
                toolchainFingerprint: toolchainFingerprint,
                toolchainBindingProven: productionBinding is not null,
                productionBinding: productionBinding);
            observabilityAgent.Start("command:" + request.Command.ToString().ToLowerInvariant());
            observabilityActivation = observabilityAgent.Activate();
            DevelopmentStage commandStage = ObservabilityStageFor(request.Command);
            observabilityAgent.SetStage(
                commandStage,
                "command:" + request.Command.ToString().ToLowerInvariant());
            observabilityAgent.Record(
                commandStage,
                AgentEventTypes.CommandStarted,
                "RimLiaison command started.",
                new
                {
                    operationKey = "cli:" + request.Command.ToString().ToLowerInvariant(),
                    workloadKind,
                    toolchainState,
                    toolchainFingerprint,
                    toolchainMode = experimentalToolchain ? "experimental" : "production",
                    productionToolchainBinding = productionBinding?.ToEvidence(),
                    command = request.Command.ToString().ToLowerInvariant(),
                    target = request.Id,
                    toolName = "RimLiaison",
                    componentOwner = "RimLiaison"
                });
            if (request.Command == CliCommand.SelfCheck)
            {
                WriteJson(stdout, new
                {
                    schemaVersion = "rimliaison-self-check/v1",
                    status = "ready",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    assembly = typeof(CliApplication).Assembly.GetName().Name,
                    assemblyVersion = typeof(CliApplication).Assembly.GetName().Version?.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
                });
                exitCode = CliExitCodes.Success;
                return exitCode;
            }

            if (legacyPromotionMigration?.Migrated == true)
            {
                observabilityAgent.Record(
                    commandStage,
                    AgentEventTypes.ToolFailed,
                    "Recovered tooling issue: the active legacy promotion was migrated to durable recovery material.",
                    new
                    {
                        operationKey = "cli:" + request.Command.ToString().ToLowerInvariant(),
                        issueKind = "TOOLING_FAILURE",
                        blocking = false,
                        projectImplicated = false,
                        recovered = true,
                        componentOwner = "RimLiaison",
                        errorCode = "PRODUCTION_TOOLCHAIN_LEGACY_MIGRATED",
                        promotedSourceCommit = legacyPromotionMigration.PromotedSourceCommit,
                        promotedFingerprint = legacyPromotionMigration.PromotedFingerprint,
                        recoveryPackagePath = legacyPromotionMigration.RecoveryPackagePath,
                        migrationElapsedMs = legacyPromotionMigration.ElapsedMilliseconds,
                        repairAttempted = true,
                        repairResult = "migrated",
                        nextAction = "continue with the normal production workflow"
                    });
            }
            if (productionToolchainRecovery is not null)
            {
                observabilityAgent.Record(
                    commandStage,
                    AgentEventTypes.ToolFailed,
                    "Recovered tooling issue: the promoted production package was repaired.",
                    new
                    {
                        operationKey = "cli:" + request.Command.ToString().ToLowerInvariant(),
                        issueKind = "TOOLING_FAILURE",
                        blocking = false,
                        projectImplicated = false,
                        recovered = true,
                        componentOwner = "RimLiaison",
                        errorCode = "PRODUCTION_TOOLCHAIN_INTEGRITY_FAULT",
                        underlyingErrorCode = productionToolchainRecovery.ErrorCode,
                        originalFault = productionToolchainFailure?.ErrorCode,
                        expectedPromotedFingerprint = productionToolchainFailure?.ExpectedFingerprint,
                        affectedArtifacts = productionToolchainFailure?.MismatchingArtifacts ??
                            productionToolchainFailure?.ExpectedArtifacts,
                        repairAttempted = true,
                        repairResult = "repaired",
                        originalFailure = productionToolchainFailure?.ToEvidence(),
                        recoveryAction = productionToolchainRecovery.Action,
                        recoveryState = productionToolchainRecovery.State.ToWireName(),
                        recoveryVerification = productionToolchainRecovery.Verification,
                        alreadyRepaired = productionToolchainRecovery.AlreadyRepaired,
                        recoveryAttempts = productionToolchainRecovery.Attempts,
                        promotedSourceCommit = productionToolchainRecovery.PromotedSourceCommit,
                        recoveryPayloadPath = productionToolchainRecovery.RecoveryPackagePath,
                        retryCount = 1,
                        retryResult = "normal-operation-continued",
                        elapsedRecoveryMs = productionToolchainRecovery.ElapsedRecoveryMilliseconds,
                        productionImpact = "toolchain-repaired-before-project-operation"
                    });
                observabilityAgent.Record(
                    commandStage,
                    AgentEventTypes.RecoveryCompleted,
                    "Promoted production-toolchain recovery completed.",
                    new
                    {
                        operationKey = "cli:" + request.Command.ToString().ToLowerInvariant(),
                        recovered = true,
                        componentOwner = "RimLiaison",
                        errorCode = productionToolchainRecovery.ErrorCode,
                        originalFailure = productionToolchainFailure?.ToEvidence(),
                        recoveryVerification = productionToolchainRecovery.Verification,
                        retryCount = 1,
                        retryResult = "normal-operation-continued",
                        durationMs = productionToolchainRecovery.ElapsedRecoveryMilliseconds
                    });
            }
            if (projectBinding is not null)
            {
                observabilityAgent.Record(
                    commandStage,
                    "project.binding.resolved",
                    "Project runtime binding resolved.",
                    projectBinding.ToEvidence());
            }
            workflowId = request.Command == CliCommand.SelfCheck
                ? null
                : WorkflowCorrelation.Create();
            profiler.SetWorkflow(workflowId);
            if (request.Command == CliCommand.ToolchainPromotion)
            {
                ToolchainPromotionResult promotion = await ToolchainPromotionService.PromoteAsync(
                        request.StackManifest.RepositoryRoot,
                        request.PromotionPackagePath,
                        request.QualificationOutputPath,
                        cancellationToken,
                        workflowId)
                    .ConfigureAwait(false);
                if (promotion.Status == "promoted" &&
                    !string.IsNullOrWhiteSpace(promotion.PromotedFingerprint) &&
                    eventStore is IAgentReliabilityCampaignStore campaignStore)
                {
                    AgentReliabilityCampaignConfiguration campaign =
                        AgentReliabilityCampaignOperations.Start(
                            campaignStore,
                            promotion.PromotedFingerprint,
                            DateTimeOffset.UtcNow);
                    AgentReliabilityObservabilityView reliability =
                        AgentReliabilityObservabilityProjection.Build(
                            eventStore,
                            promotion.PromotedFingerprint);
                    promotion = promotion with
                    {
                        ReliabilityCampaignId = campaign.CampaignId,
                        ReliabilityCampaignState = reliability.CampaignState
                    };
                    observabilityAgent.Record(
                        commandStage,
                        "toolchain.promotion.completed",
                        "Production toolchain promotion completed.",
                        new
                        {
                            transactionId = promotion.PromotionTransactionId,
                            workflowId,
                            promotedFingerprint = promotion.PromotedFingerprint,
                            previousFingerprint = promotion.PreviousFingerprint,
                            reliabilityCampaignId = campaign.CampaignId,
                            reliabilityCampaignState = reliability.CampaignState
                        });
                }
                WriteJson(stdout, promotion);
                exitCode = promotion.Status == "promoted"
                    ? CliExitCodes.Success
                    : CliExitCodes.ConservativeSelection;
                return exitCode;
            }
            if (request.Command == CliCommand.Qualification)
            {
                string profile = QualificationProfiles.ResolveProfile(request.Id);
                string outputPath = request.QualificationOutputPath ??
                    Path.Combine(".rimdev", "qualification", "latest.json");
                string qualificationDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(outputPath))!;
                Directory.CreateDirectory(qualificationDirectory);
                GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
                    .ReadAsync(request.StackManifest.RepositoryRoot, cancellationToken)
                    .ConfigureAwait(false);
                string sourceCommit = source.State?.HeadSha ??
                    throw new InvalidDataException(
                        "Qualification did not resolve the current source commit.");
                string qualificationId = DateTimeOffset.UtcNow.ToString(
                    "yyyyMMddTHHmmssfffZ");
                string qualificationArtifactPath = Path.Combine(
                    qualificationDirectory,
                    "qualification-" + sourceCommit + "-" + qualificationId + ".json");
                string packagePath = Path.Combine(
                    qualificationDirectory,
                    "qualified-toolchain-package-" + sourceCommit + "-" + qualificationId + ".json");
                string candidateRoot = Path.Combine(
                    qualificationDirectory,
                    "candidate-" + sourceCommit + "-" + qualificationId);
                ToolchainCandidateMaterializationResult candidateResult;
                string manifestPath = Environment.GetEnvironmentVariable(
                        "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST") ??
                    "C:/RimDev/.rimdev/production-toolchain.json";
                if (!ToolchainPromotionService.TryReadPromotionDestination(
                        manifestPath,
                        out string? runtimeRoot,
                        out string runtimeProtocolContract,
                        out string? destinationError))
                {
                    candidateResult = ToolchainCandidateMaterializationResult.Failure(
                        "PROMOTION_PRODUCTION_MANIFEST_INVALID",
                        destinationError ?? "The production runtime destination is unavailable.",
                        "Repair the project-owned production manifest, then retry qualification.");
                }
                else
                {
                    candidateResult = await ToolchainCandidateMaterializer.MaterializeAsync(
                            request.StackManifest.RepositoryRoot,
                            candidateRoot,
                            Path.GetDirectoryName(typeof(CliApplication).Assembly.Location)!,
                            runtimeRoot!,
                            runtimeProtocolContract,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                QualificationProfiles.ValidateRunCount(profile, request.QualificationRuns);
                QualificationAggregate aggregate = new QualificationHarness().Run(
                    request.QualificationRuns,
                    profile,
                    eventStore,
                    toolchainState: "experimental");
                aggregate = await AttachQualificationProvenanceAsync(
                        aggregate,
                        request.StackManifest.RepositoryRoot,
                        candidateResult.Candidate,
                        cancellationToken)
                    .ConfigureAwait(false);
                aggregate = aggregate with
                {
                    CandidateComplete = candidateResult.Succeeded,
                    CandidateFailureCode = candidateResult.ErrorCode,
                    CandidateFailure = candidateResult.Error,
                    CandidateBuildEvidence = candidateResult.RimWorldManagedAssemblies?.ToEvidence(),
                    QualificationArtifactPath = Path.GetFullPath(qualificationArtifactPath),
                    QualifiedPromotionPackagePath = Path.GetFullPath(packagePath)
                };
                string qualificationJson = JsonSerializer.Serialize(
                    aggregate,
                    new JsonSerializerOptions { WriteIndented = true });
                using (StreamWriter writer = new(
                           new FileStream(
                               qualificationArtifactPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.Read)))
                {
                    writer.Write(qualificationJson);
                }

                if (candidateResult.Candidate is not null && aggregate.QualificationPassed)
                {
                    try
                    {
                        ToolchainPromotionService.WriteQualifiedPromotionPackage(
                            aggregate,
                            qualificationArtifactPath,
                            packagePath,
                            candidateResult.Candidate);
                        aggregate = aggregate with { PromotionPackageEmitted = true };
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        aggregate = aggregate with
                        {
                            CandidateFailureCode = "PROMOTION_PACKAGE_EMISSION_FAILED",
                            CandidateFailure = exception.Message
                        };
                    }
                }
                string json = JsonSerializer.Serialize(
                    aggregate,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(outputPath, json);
                string backlogPath = Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? ".",
                    "tooling-improvement-backlog.json");
                string backlogJson = JsonSerializer.Serialize(
                    QualificationHarness.BuildBacklog(aggregate, eventStore),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(backlogPath, backlogJson);
                WriteJson(stdout, aggregate);
                exitCode = aggregate.PromotionReady
                    ? CliExitCodes.Success
                    : CliExitCodes.TestFailure;
                return exitCode;
            }

            if (request.Command is CliCommand.RecipeShow or
                CliCommand.RecipePlan or
                CliCommand.RecipeRun)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.recipe",
                        "command",
                        () => ExecuteRecipeCommandAsync(
                            request,
                            stdout,
                            recipeAdapter,
                            cancellationToken,
                            workflowId),
                        AnnotateExit,
                        phase: "command",
                        scope: request.Command.ToString())
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.Doctor)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.doctor",
                        "command",
                        () => ExecuteDoctorCommandAsync(
                            request,
                            stdout,
                            stderr,
                            processTransport,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: "doctor")
                    .ConfigureAwait(false);
                return exitCode;
            }
            if (request.Command == CliCommand.Preflight)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.preflight",
                        "preflight",
                        () => ExecutePreflightCommandAsync(
                            request,
                            stdout,
                            stderr,
                            processTransport,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: "preflight")
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.Context)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.context",
                        "command",
                        () => ExecuteContextCommandAsync(
                            request,
                            stdout,
                            processTransport,
                            eventStore,
                            observabilityEntity,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: "context")
                    .ConfigureAwait(false);
                return exitCode;
            }
            if (request.Command == CliCommand.Content)
            {
                ContentQueryResult result = new ContentIntelligenceService(
                        new ContentIntelligenceStore(
                            ContentIntelligenceStorage.ResolveDefaultPath(
                                request.RimContextRootPath)))
                    .Query(new ContentQueryRequest(
                        request.Id,
                        request.ContentKind,
                        request.ContentRole,
                        Limit: Math.Min(request.RimContextLimit, 100),
                        MaxBytes: request.ContentMaxBytes,
                        RootPath: request.RimContextRootPath,
                        IndexStorePath: request.RimContextStorePath));
                WriteJson(stdout, result);
                exitCode = CliExitCodes.Success;
                return exitCode;
            }

            if (request.Command == CliCommand.PublishCheck)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.publish-check",
                        "publication",
                        () => ExecutePublishCheckCommandAsync(
                            request,
                            stdout,
                            eventStore,
                            gitChangeProvider,
                            observabilityEntity,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: "publish-check")
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.Benchmarks)
            {
                GoldenWorkflowBenchmarkReport report = GoldenWorkflowBenchmarkRunner.RunMeasured();
                WriteJson(stdout, report);
                exitCode = report.RegressionCount == 0 &&
                    report.PassedScenarioCount == report.Scenarios.Count
                    ? CliExitCodes.Success
                    : CliExitCodes.TestFailure;
                return exitCode;
            }

            if (request.Command == CliCommand.RimDev)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.rimdev",
                        "rimdev",
                        () => new RimDevWorkflow(observabilityStore: eventStore).RunAsync(
                            new RimDevRunOptions(
                                request.RimDevOperation ?? throw new InvalidOperationException("rimdev operation is missing"),
                                request.RimDevRootPath,
                                request.RimDevConfirm,
                                request.RimDevJson,
                                Input: Console.In),
                            stdout,
                            stderr,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: request.RimDevOperation?.ToString() ?? "unknown")
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.Capabilities)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.capabilities",
                        "command",
                        () => ExecuteCapabilitiesCommandAsync(
                            request,
                            stdout,
                            processTransport,
                            capabilityAdapter,
                            workflowId,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: "capabilities")
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command is CliCommand.UiTargets or CliCommand.UiScreenshot)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.ui",
                        "command",
                        () => ExecuteUiCommandAsync(
                            request,
                            stdout,
                            processTransport,
                            uiAdapter,
                            viewportAdapter,
                            leaseAdapter,
                            workflowId,
                            cancellationToken),
                        AnnotateExit,
                        phase: "command",
                        scope: request.Command.ToString())
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.Init)
            {
                exitCode = await ProfilerActivity.ObserveAsync(
                        "command.init",
                        "command",
                        () =>
                        {
                            StackInitResult result = StackInitializer.Run(request);
                            WriteJson(stdout, result.Output);
                            return Task.FromResult(result.ExitCode);
                        },
                        AnnotateExit,
                        phase: "command",
                        scope: "init")
                    .ConfigureAwait(false);
                return exitCode;
            }

            if (request.Command == CliCommand.GoldenPath)
            {
                observabilityAgent.SetProductionState(
                    DevelopmentStage.Analysis,
                    "preflight",
                    "required");
                DoctorRunResult preflight = await new RimTestDoctorRunner(stderr)
                    .RunAsync(
                        request,
                        processTransport ?? new SystemDevBridgeProcessTransport(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (preflight.ExitCode != CliExitCodes.Success)
                {
                    WriteJson(
                        stdout,
                        new
                        {
                            schemaVersion = "rimliaison-golden-path/v1",
                            status = "VALIDATION_INCOMPLETE",
                            completionResult = "VALIDATION_INCOMPLETE",
                            preflight = new
                            {
                                status = "blocked",
                                ready = false,
                                owner = "RimLiaison",
                                details = preflight.Output
                            }
                        });
                    observabilityAgent.Record(
                        DevelopmentStage.Analysis,
                        AgentEventTypes.ToolFailed,
                        "Golden Path preflight is blocked.",
                        new
                        {
                            operationKey = "preflight",
                            issueKind = "TOOLING_FAILURE",
                            blocking = true,
                            componentOwner = "RimLiaison",
                            details = preflight.Output
                        });
                    exitCode = preflight.ExitCode;
                    return exitCode;
                }

                observabilityAgent.Record(
                    DevelopmentStage.Analysis,
                    AgentEventTypes.InformationalProductionEvent,
                    "Golden Path preflight is ready.",
                    new
                    {
                        operationKey = "preflight",
                        owner = "RimLiaison",
                        status = "ready"
                    });
                request = request with
                {
                    Command = CliCommand.Affected,
                    RunSelected = true
                };
            }
            exitCode = await ProfilerActivity.ObserveAsync(
                    "command.catalog",
                    "command",
                    () => ExecuteCatalogCommandAsync(
                        request,
                        stdout,
                        stderr,
                        recipeAdapter,
                        diagnosisAdapter,
                        diagnosticSourceAdapter,
                        impactAdapter,
                        gitChangeProvider,
                        processTransport,
                        cancellationToken,
                        started,
                        workflowId,
                        developmentAdapter,
                        freshGenerationRecoveryAdapter,
                        capabilityAdapter,
                        productionBinding),
                    AnnotateExit,
                    phase: "command",
                    scope: request.Command.ToString())
                .ConfigureAwait(false);
            return exitCode;
        }
        catch (CliParseException exception)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Invalid(
                        testId,
                        "CLI_INVALID",
                        ElapsedMilliseconds(started),
                        workflowId));
                exitCode = CliExitCodes.InvalidInput;
                return exitCode;
            }

            WriteError(
                stdout,
                "CLI_INVALID",
                [new CatalogIssue("CLI_INVALID", exception.Message)]);
            exitCode = CliExitCodes.InvalidInput;
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Cancelled(
                        testId,
                        ElapsedMilliseconds(started),
                        workflowId));
                exitCode = CliExitCodes.Cancelled;
                return exitCode;
            }

            WriteJson(
                stdout,
                new
                {
                    status = "error",
                    code = "RIMTEST_CANCELLED",
                    outcome = "cancelled"
                });
            exitCode = CliExitCodes.Cancelled;
            return exitCode;
        }
        catch (Exception)
        {
            if (TryGetRunTestId(args, out string? testId))
            {
                WriteJson(
                    stdout,
                    RimTestResultFactory.Infrastructure(
                        testId,
                        "INTERNAL_ERROR",
                        ElapsedMilliseconds(started),
                        workflowId));
                exitCode = CliExitCodes.InternalError;
                return exitCode;
            }

            stderr.WriteLine("rimliaison internal error.");
            WriteError(
                stdout,
                "INTERNAL_ERROR",
                [new CatalogIssue("INTERNAL_ERROR", "An unexpected error occurred.")]);
            exitCode = CliExitCodes.InternalError;
            return exitCode;
        }
        finally
        {
            if (observabilityAgent is not null)
            {
                try
                {
                    AgentWorkflowTelemetrySummary telemetry =
                        AgentWorkflowTelemetrySummary.FromEvents(
                            eventStore?.GetEvents(runId: profiler.RunId, limit: 4096) ?? []);
                    string operationKey = "cli:" + (commandName ?? "unknown");
                    if (exitCode == CliExitCodes.Success)
                    {
                        observabilityAgent.Record(
                            observabilityAgent.Snapshot.CurrentStage,
                            AgentEventTypes.CommandCompleted,
                            "RimLiaison command completed.",
                            new
                            {
                                operationKey,
                                command = commandName,
                                workflowId,
                                exitCode,
                                durationMs = ElapsedMilliseconds(started),
                                outcome = "success",
                                telemetry
                            });
                        observabilityAgent.Complete("RimLiaison command completed.");
                    }
                    else
                    {
                        AgentEvent[] priorFailures = eventStore?
                            .GetEvents(runId: profiler.RunId, limit: 4096)
                            .Where(IsFailureLifecycleEvent)
                            .ToArray() ?? [];
                        AgentEvent? cause = priorFailures.LastOrDefault();
                        string failureCode = exitCode == CliExitCodes.Cancelled
                            ? "RIMTEST_CANCELLED"
                            : "RIMLIAISON_COMMAND_FAILED";
                        string? underlyingErrorCode = priorFailures
                            .AsEnumerable()
                            .Reverse()
                            .Select(eventRecord =>
                                AgentObservabilityData.GetString(
                                    eventRecord.Data,
                                    "underlyingErrorCode") ??
                                AgentObservabilityData.GetString(
                                    eventRecord.Data,
                                    "errorCode"))
                            .FirstOrDefault(code =>
                                !string.IsNullOrWhiteSpace(code) &&
                                code is not "RIMLIAISON_COMMAND_FAILED" and
                                    not "DEVBRIDGE_COMMAND_FAILED");
                        string? componentOwner = cause is null
                            ? null
                            : AgentObservabilityData.GetString(
                                cause.Data,
                                "componentOwner");
                        var failureData = new Dictionary<string, object?>(
                            StringComparer.Ordinal)
                        {
                            ["operationKey"] = operationKey,
                            ["command"] = commandName,
                            ["workflowId"] = workflowId,
                            ["exitCode"] = exitCode,
                            ["errorCode"] = failureCode,
                            ["outerErrorCode"] = failureCode,
                            ["underlyingErrorCode"] = underlyingErrorCode,
                            ["componentOwner"] = componentOwner,
                            ["causeEventId"] = cause?.Id,
                            ["relatedEventIds"] = priorFailures
                                .Select(eventRecord => eventRecord.Id)
                                .Take(32)
                                .ToArray(),
                            ["lifecycleOnly"] = priorFailures.Length > 0,
                            ["outcome"] = "failure"
                        };
                        observabilityAgent.Record(
                            observabilityAgent.Snapshot.CurrentStage,
                            AgentEventTypes.CommandFailed,
                            exitCode == CliExitCodes.Cancelled
                                ? "RimLiaison command was cancelled."
                                : "RimLiaison command failed.",
                            failureData);
                        observabilityAgent.Fail(
                            exitCode == CliExitCodes.Cancelled
                                ? "RimLiaison command was cancelled."
                                : "RimLiaison command failed.",
                            failureCode,
                            completionState: exitCode == CliExitCodes.Cancelled
                                ? AgentCompletionState.Cancelled
                                : AgentCompletionState.Failed,
                            data: failureData);
                    }
                }
                catch
                {
                }
            }
            observabilityActivation?.Dispose();
            observabilityRun?.Dispose();
            profiler.Complete(exitCode, cancellationToken.IsCancellationRequested);
            profiler.Dispose();
        }
    }

    private static async Task<int> ExecuteRecipeCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeRecipeAdapter? recipeAdapter,
        CancellationToken cancellationToken,
        string? workflowId)
    {
        IDevBridgeRecipeAdapter adapter = CreateAdapter(request, recipeAdapter);
        switch (request.Command)
        {
            case CliCommand.RecipeShow:
                {
                    DevBridgeRecipeShowResult result = await adapter.ShowAsync(
                        request.Id!,
                        cancellationToken).ConfigureAwait(false);
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["recipe"] = result.RecipeId,
                        ["outcome"] = OutcomeName(result.Status.Outcome)
                    };
                    if (result.Definition.HasValue)
                    {
                        output["definition"] = result.Definition.Value;
                    }

                    AddStatusFields(output, result.Status);
                    WriteJson(stdout, output);
                    return ExitCodeFor(result.Status.Outcome);
                }
            case CliCommand.RecipePlan:
                {
                    DevBridgeRecipePlanResult result = await adapter.PlanAsync(
                        request.Id!,
                        cancellationToken).ConfigureAwait(false);
                    var output = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["recipe"] = result.RecipeId,
                        ["outcome"] = OutcomeName(result.Status.Outcome)
                    };
                    if (result.Plan is not null)
                    {
                        output["alreadySatisfied"] = result.Plan.AlreadySatisfied;
                        output["estimatedRimWorldLaunches"] =
                            result.Plan.EstimatedRimWorldLaunches;
                        output["steps"] = result.Plan.Steps;
                        output["nextAction"] = result.Plan.NextAction;
                        output["blockedBy"] = result.Plan.BlockedBy;
                    }

                    AddStatusFields(output, result.Status);
                    WriteJson(stdout, output);
                    return ExitCodeFor(result.Status.Outcome);
                }
            case CliCommand.RecipeRun:
                {
                    DevBridgeRecipeRunResult result = await adapter.RunAsync(
                        request.Id!,
                        workflowId,
                        cancellationToken).ConfigureAwait(false);
                    WriteRunResult(
                        result.RecipeId,
                        result.RecipeId,
                        result,
                        stdout,
                        workflowId);
                    return ExitCodeFor(result.Status.Outcome);
                }
            default:
                throw new InvalidOperationException("Unknown recipe command.");
        }
    }
    private static async Task<int> ExecuteCatalogCommandAsync(
        CliRequest request,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IRimContextImpactAdapter? impactAdapter,
        IGitChangeProvider? gitChangeProvider,
        IDevBridgeProcessTransport? processTransport,
        CancellationToken cancellationToken,
        long started,
        string? workflowId,
        IDevBridgeModDevelopmentAdapter? developmentAdapter,
        IDevBridgeFreshGenerationAdapter? freshGenerationRecoveryAdapter,
        IDevBridgeCapabilityAdapter? capabilityAdapter,
        ProductionToolchainBinding? productionBinding)
    {
        CatalogLoadResult loaded = CatalogLoader.Load(request.CatalogPath);
        if (loaded.Catalog is null)
        {
            if (request.Command == CliCommand.RunTest)
            {
                return WriteRimTestInvalid(
                    request.Id!,
                    FirstErrorCode(loaded.Errors, "CATALOG_INVALID"),
                    started,
                    stdout,
                    workflowId: workflowId);
            }

            WriteError(stdout, "CATALOG_INVALID", loaded.Errors);
            return CliExitCodes.InvalidInput;
        }

        IReadOnlySet<string>? recipeIds = null;
        if (request.RecipeListPath is not null)
        {
            RecipeListLoadResult recipeList = RecipeListLoader.Load(request.RecipeListPath);
            if (recipeList.RecipeIds is null)
            {
                if (request.Command == CliCommand.RunTest)
                {
                    return WriteRimTestInvalid(
                        request.Id!,
                        FirstErrorCode(recipeList.Errors, "RECIPE_LIST_INVALID"),
                        started,
                        stdout,
                        workflowId: workflowId);
                }

                WriteError(stdout, "RECIPE_LIST_INVALID", recipeList.Errors);
                return CliExitCodes.InvalidInput;
            }

            recipeIds = recipeList.RecipeIds;
        }

        CatalogValidationResult validation =
            CatalogValidator.Validate(loaded.Catalog, recipeIds);
        if (!validation.IsValid)
        {
            if (request.Command == CliCommand.RunTest)
            {
                return WriteRimTestInvalid(
                    request.Id!,
                    FirstErrorCode(validation.Errors, "CATALOG_INVALID"),
                    started,
                    stdout,
                    workflowId: workflowId);
            }

            WriteError(stdout, "CATALOG_INVALID", validation.Errors);
            return CliExitCodes.InvalidInput;
        }

        switch (request.Command)
        {
            case CliCommand.List:
                return WriteTestList(loaded.Catalog, stdout);
            case CliCommand.ShowTest:
                return WriteTest(loaded.Catalog, request.Id!, stdout);
            case CliCommand.Suites:
                return WriteSuiteList(loaded.Catalog, stdout);
            case CliCommand.ShowSuite:
                return WriteSuite(loaded.Catalog, request.Id!, stdout);
            case CliCommand.Validate:
                return WriteValidation(loaded.Catalog, validation, stdout);
            case CliCommand.SuiteRun:
                {
                    CatalogSuite? suite = CatalogNavigator.FindSuite(loaded.Catalog, request.Id!);
                    if (suite is null)
                    {
                        WriteError(
                            stdout,
                            "SUITE_NOT_FOUND",
                            [new CatalogIssue(
                            "SUITE_NOT_FOUND",
                            $"Suite was not found: {request.Id}.",
                            "id")]);
                        return CliExitCodes.NotFound;
                    }

                    return await RunSuiteAsync(
                            loaded.Catalog,
                            suite.Id,
                            CatalogNavigator.ResolvedTestIds(loaded.Catalog, suite.Id),
                            request,
                            stdout,
                            recipeAdapter,
                            diagnosisAdapter,
                            diagnosticSourceAdapter,
                            processTransport,
                            started,
                            cancellationToken,
                            workflowId: workflowId,
                            providedCapabilityAdapter: capabilityAdapter,
                            transactionConsumerPath: productionBinding?.TransactionConsumerPath)
                        .ConfigureAwait(false);
                }
            case CliCommand.Affected:
                {
                    IReadOnlyList<string> changedPaths = request.ChangedPaths;
                    IReadOnlyList<GitChangedPath>? gitChanges = null;
                    if (changedPaths.Count == 0)
                    {
                        IGitChangeProvider git = gitChangeProvider ?? new SystemGitChangeProvider();
                        GitChangeDiscoveryResult discovered;
                        try
                        {
                            discovered = await ProfilerActivity.ObserveAsync(
                                    "git.change-discovery",
                                    "git",
                                    () => git.DiscoverAsync(
                                        AffectedGitRoot(request),
                                        request.AffectedBase,
                                        cancellationToken),
                                    (activity, result) =>
                                    {
                                        ProfilerActivity.SetOutcome(
                                            activity,
                                            result.Resolved ? "success" : "failure",
                                            result.ErrorCode);
                                        ProfilerActivity.SetCounts(
                                            activity,
                                            items: result.Paths.Count);
                                    },
                                    phase: "discovery",
                                    scope: "affected")
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            stderr.WriteLine("RimLiaison: Git change discovery failed.");
                            discovered = new GitChangeDiscoveryResult(
                                false,
                                [],
                                "GIT_DISCOVERY_FAILED",
                                exception.Message);
                        }

                        if (!discovered.Resolved)
                        {
                            var blocked = new RimTestSelectionResult
                            {
                                Status = "blocked",
                                ReasonCount = 1,
                                ErrorCode = discovered.ErrorCode ?? "GIT_DISCOVERY_FAILED",
                                NextAction = "git status --short"
                            };
                            if (request.RunSelected)
                            {
                                return WriteAffectedSelectionFailure(
                                    blocked,
                                    started,
                                    stdout,
                                    workflowId);
                            }

                            WriteJson(stdout, blocked);
                            return SelectionExitCode(blocked);
                        }

                        changedPaths = discovered.Paths;
                        gitChanges = discovered.Changes;
                        if (changedPaths.Count == 0 && gitChanges.Count == 0)
                        {
                            var clean = new RimTestSelectionResult
                            {
                                Status = "ok",
                                Tests = [],
                                ReasonCount = 0
                            };
                            WriteJson(stdout, clean);
                            return CliExitCodes.Success;
                        }

                        if (changedPaths.Count == 0)
                        {
                            var blocked = new RimTestSelectionResult
                            {
                                Status = "blocked",
                                ReasonCount = 1,
                                ErrorCode = "GIT_CHANGED_PATHS_MISSING",
                                NextAction = "git status --short"
                            };
                            if (request.RunSelected)
                            {
                                return WriteAffectedSelectionFailure(
                                    blocked,
                                    started,
                                    stdout,
                                    workflowId);
                            }

                            WriteJson(stdout, blocked);
                            return SelectionExitCode(blocked);
                        }
                    }

                    ContentIntelligenceCapture? contentCapture =
                        await ContentIntelligenceCapture.TryCreateAsync(
                                request,
                                changedPaths,
                                workflowId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    string packetTask = Environment.GetEnvironmentVariable("RIMTEST_TASK") ??
                        string.Join(" ", changedPaths);
                    ExecutionPacketGenerationResult packetGeneration =
                        ExecutionPacketCoordinator.TryGenerate(
                            AffectedGitRoot(request),
                            request.RimContextStorePath,
                            packetTask,
                            changedPaths,
                            repository: request.StackManifest.Manifest?.Project,
                            project: request.StackManifest.Manifest?.Project,
                            additionalEvidence: CatalogImpactEvidence(request.CatalogPath));

                    ValidationPlanGenerationResult planGeneration =
                        MinimumSafeValidationCoordinator.TryBuild(
                            packetGeneration,
                            loaded.Catalog,
                            AffectedGitRoot(request),
                            repository: request.StackManifest.Manifest?.Project,
                            project: request.StackManifest.Manifest?.Project,
                            fallbackSuite: request.FallbackSuite);
                    ValidationPlan? validationPlan = planGeneration.Plan;
                    if (validationPlan is not null)
                    {
                        AgentImpactObservabilityRecorder.RecordValidationPlan(
                            validationPlan,
                            broadened: validationPlan.ScopeExpanded ||
                                !string.Equals(
                                    validationPlan.PredictionTier,
                                    validationPlan.Tier,
                                    StringComparison.Ordinal));
                        string[] reusedEvidenceIds = validationPlan.Required
                            .SelectMany(requirement => requirement.ReusedEvidenceIds ?? [])
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        if (reusedEvidenceIds.Length > 0)
                        {
                            AgentImpactObservabilityRecorder.RecordEvidenceReused(
                                validationPlan,
                                validationPlan.Required
                                    .Where(requirement => requirement.ReusedEvidenceIds is { Count: > 0 })
                                    .Select(requirement => requirement.TestId)
                                    .Where(testId => testId is not null)
                                    .Select(testId => testId!)
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray(),
                                reusedEvidenceIds);
                        }
                        if (validationPlan.RuntimeRequests is { Count: > 0 } runtimeRequests)
                        {
                            AgentImpactObservabilityRecorder.RecordRuntimeEscalation(
                                validationPlan,
                                runtimeRequests);
                        }


                    }

                    IRimContextImpactAdapter adapter = impactAdapter ?? CreateRimContextAdapter(request);
                    var selector = new RimContextTestSelector(adapter);
                    RimTestSelectionResult selection = await ProfilerActivity.ObserveAsync(
                            "affected-selection",
                            "selection",
                            () => selector.SelectAsync(
                                loaded.Catalog,
                                changedPaths,
                                request.FallbackSuite,
                                request.Explain,
                                cancellationToken,
                                gitChanges),
                            (activity, result) =>
                            {
                                ProfilerActivity.SetOutcome(
                                    activity,
                                    result.Status is "ok" or "conservative"
                                        ? "success"
                                        : result.Status == "cancelled"
                                            ? "cancelled"
                                            : "failure",
                                    result.ErrorCode);
                                ProfilerActivity.SetCounts(
                                    activity,
                                    items: result.Tests.Count);
                            },
                            phase: "affected",
                            scope: "changed-paths")
                        .ConfigureAwait(false);

                    if (validationPlan is not null &&
                        selection.Status is "ok" or "conservative")
                    {
                        HashSet<string> reused = validationPlan.Required
                            .Where(requirement => requirement.TestId is not null)
                            .GroupBy(requirement => requirement.TestId!, StringComparer.Ordinal)
                            .Where(group => group.All(requirement => requirement.ReusedEvidenceIds is { Count: > 0 }))
                            .Select(group => group.Key)
                            .ToHashSet(StringComparer.Ordinal);
                        string[] required = validationPlan.TestsNeedingExecution
                            .Where(testId => loaded.Catalog.Tests.Any(test => test.Id == testId))
                            .ToArray();
                        string[] merged = selection.Tests
                            .Where(testId => !reused.Contains(testId))
                            .Concat(required)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(testId => testId, StringComparer.Ordinal)
                            .ToArray();
                        selection = selection.WithTests(
                            merged,
                            validationPlan.Status == "ready" ? selection.Status : "conservative");
                    }

                    if (selection.Status == "ok" && selection.Tests.Count == 0)
                    {
                        selection = new RimTestSelectionResult
                        {
                            Status = "blocked",
                            ReasonCount = Math.Max(1, selection.ReasonCount),
                            ErrorCode = "AFFECTED_NO_TESTS",
                            NextAction = "rimliaison affected --run --fallback-suite <suite>",
                            Reasons = selection.Reasons,
                            RecoveryState = selection.RecoveryState,
                            RecoveryAttempts = selection.RecoveryAttempts,
                            RecoveryAction = selection.RecoveryAction
                        };
                    }
                    if (request.Explain)
                    {
                        selection = selection.WithImpact(
                            packetGeneration.Packet,
                            packetGeneration.Prediction,
                            packetGeneration.Actual,
                            request.Explain ? validationPlan : null);
                    }


                    AgentObservabilityRuntime.Record(
                        DevelopmentStage.Analysis,
                        "test.selection.decision",
                        "Affected-test selection decision recorded.",
                        new
                        {
                            decision = "affected-test-selection",
                            action = selection.Status is "ok" or "conservative"
                                ? RimContextDecisionActions.Run
                                : selection.Status == "blocked"
                                    ? RimContextDecisionActions.Block
                                    : RimContextDecisionActions.Skip,
                            reasonCode = selection.ErrorCode ?? "AFFECTED_SELECTION_PROVEN",
                            explanation = selection.Status == "conservative"
                                ? "RimContext selected a conservative validation set."
                                : selection.Status == "blocked"
                                    ? "RimLiaison blocked execution because affected-test selection was not trustworthy."
                                    : "RimContext selected the affected validation set.",
                            changedInputs = changedPaths,
                            tests = selection.Tests,
                            selectedSuites = new[] { request.FallbackSuite ?? "affected" },
                            fallbackSuite = request.FallbackSuite,
                            selectionStatus = selection.Status,
                            executionPacketStatus = packetGeneration.Packet?.Status ?? "unavailable",
                            packetBytes = packetGeneration.Packet?.Metrics.SizeBytes,
                            packetGenerationMilliseconds = packetGeneration.Packet?.Metrics.GenerationElapsedMilliseconds,
                            impactScopeExpanded = packetGeneration.Actual?.ScopeExpanded,
                            validationPlanStatus = validationPlan?.Status,
                            validationPlanTier = validationPlan?.Tier,
                            validationRequiredCount = validationPlan?.RequiredTestIds.Count,
                            validationAdditionalCount = validationPlan?.RequiredTestIds.Count,
                            owner = "RimTest/RimLiaison"
                        });

                    if (request.RunSelected && selection.Tests.Count == 0)
                    {
                        if (selection.Status == "blocked")
                        {
                            return WriteAffectedSelectionFailure(
                                selection,
                                started,
                                stdout,
                                workflowId);
                        }

                        WriteJson(stdout, selection);
                        return SelectionExitCode(selection);
                    }

                    if (request.RunSelected &&
                        selection.Status is "ok" or "conservative")
                    {
                        ArtifactFreshnessTransactionRequest? freshnessRequest =
                            CreateArtifactFreshnessRequest(
                                request,
                                loaded.Catalog,
                                selection.Tests,
                                changedPaths,
                                workflowId);
                        return await RunSuiteAsync(
                                loaded.Catalog,
                                "affected",
                                selection.Tests,
                                request,
                                stdout,
                                recipeAdapter,
                                diagnosisAdapter,
                                diagnosticSourceAdapter,
                                processTransport,
                                started,
                                cancellationToken,
                                selection.Status,
                                selection.ErrorCode,
                                selection.FallbackSuite,
                                workflowId,
                                developmentAdapter,
                                freshnessRequest,
                                SelectionRecovery(selection),
                                validationChangedPaths: changedPaths,
                                freshGenerationRecoveryAdapter: freshGenerationRecoveryAdapter,
                                providedCapabilityAdapter: capabilityAdapter,
                                contentCapture: contentCapture,
                                validationPlan: validationPlan,
                                protectRepositoryWorktree: true,
                                impactGraph: packetGeneration.Graph,
                                transactionConsumerPath: productionBinding?.TransactionConsumerPath)
                            .ConfigureAwait(false);
                    }

                    if (request.RunSelected)
                    {
                        return WriteAffectedSelectionFailure(
                            selection,
                            started,
                            stdout,
                            workflowId);
                    }

                    WriteJson(stdout, selection);
                    return SelectionExitCode(selection);
                }
            case CliCommand.RunTest:
                {
                    CatalogTest? test = CatalogNavigator.FindTest(loaded.Catalog, request.Id!);
                    if (test is null)
                    {
                        return WriteRimTestInvalid(
                            request.Id!,
                            "TEST_NOT_FOUND",
                            started,
                            stdout,
                            invalidExitCode: CliExitCodes.NotFound,
                            workflowId: workflowId);
                    }

                    return await RunSuiteAsync(
                            loaded.Catalog,
                            test.Id,
                            [test.Id],
                            request,
                            stdout,
                            recipeAdapter,
                            diagnosisAdapter,
                            diagnosticSourceAdapter,
                            processTransport,
                            started,
                            cancellationToken,
                            workflowId: workflowId,
                            freshGenerationRecoveryAdapter: freshGenerationRecoveryAdapter,
                            providedCapabilityAdapter: capabilityAdapter,
                            transactionConsumerPath: productionBinding?.TransactionConsumerPath,
                            singleTestOutput: true)
                        .ConfigureAwait(false);
                }
            default:
                throw new InvalidOperationException("Unknown catalog command.");
        }
    }

    private static string AffectedGitRoot(CliRequest request) =>
        request.RimContextRootPath ??
        Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT") ??
        Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT") ??
        Environment.CurrentDirectory;
    private static IReadOnlyList<ImpactGraphEvidence> CatalogImpactEvidence(string catalogPath)
    {
        try
        {
            CatalogLoadResult loaded = CatalogLoader.Load(catalogPath);
            if (loaded.Catalog is null)
            {
                return [];
            }

            var evidence = new List<ImpactGraphEvidence>();
            foreach (CatalogTest test in loaded.Catalog.Tests)
            {
                string testIdentity = "test/" + test.Id;
                foreach (CatalogCoverage coverage in test.Covers ?? [])
                {
                    evidence.Add(new ImpactGraphEvidence(
                        testIdentity,
                        coverage.Name,
                        ImpactRelationshipKinds.TestCoverage,
                        ImpactClasses.Declared,
                        new ImpactProvenance(
                            "test-catalog",
                            ImpactEvidenceClasses.Explicit,
                            test.Id,
                            "catalog test coverage"),
                        "test",
                        coverage.Kind,
                        test.Id,
                        coverage.Name));
                }

                foreach (CatalogCapabilityRequirement capability in test.RequiredCapabilities ?? [])
                {
                    evidence.Add(new ImpactGraphEvidence(
                        "framework/" + capability.CapabilityId,
                        testIdentity,
                        ImpactRelationshipKinds.FrameworkConsumer,
                        ImpactClasses.Framework,
                        new ImpactProvenance(
                            "test-catalog",
                            ImpactEvidenceClasses.FrameworkKnown,
                            capability.CapabilityId,
                            capability.Purpose),
                        "framework_capability",
                        "test",
                        capability.CapabilityId,
                        test.Id));
                }
            }

            return evidence;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }


    private static IReadOnlyDictionary<string, string>? DiscoverContextRelatedRepositoryRoots(
        string rootPath)
    {
        try
        {
            RimDevWorkspaceDiscovery discovery = RimDevWorkspaceDiscoverer.Discover(
                explicitRoot: null,
                startDirectory: rootPath);
            if (!discovery.Succeeded)
            {
                return null;
            }

            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (RimDevRepository repository in discovery.Repositories
                         .OrderBy(static value => value.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static value => value.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(repository.Path))
                {
                    continue;
                }

                string name = repository.Name;
                if (!roots.TryAdd(name, repository.Path))
                {
                    roots.TryAdd(name + ":" + repository.Path, repository.Path);
                }
            }

            return roots.Count == 0 ? null : roots;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<int> ExecuteDoctorCommandAsync(
        CliRequest request,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeProcessTransport? processTransport,
        CancellationToken cancellationToken)
    {
        var runner = new RimTestDoctorRunner(stderr);
        DoctorRunResult result = await runner.RunAsync(
                request,
                processTransport ?? new SystemDevBridgeProcessTransport(),
                cancellationToken)
            .ConfigureAwait(false);
        WriteJson(stdout, result.Output);
        return result.ExitCode;
    }

    private static async Task<int> ExecutePreflightCommandAsync(
        CliRequest request,
        TextWriter stdout,
        TextWriter stderr,
        IDevBridgeProcessTransport? processTransport,
        CancellationToken cancellationToken)
    {
        ProjectRuntimeBindingResult? projectBinding =
            RequiresProjectBinding(request)
                ? ResolveProjectBinding(request)
                : null;
        if (projectBinding is not null && !projectBinding.Succeeded)
        {
            WriteJson(stdout, projectBinding.ToEvidence());
            return CliExitCodes.ConservativeSelection;
        }

        DoctorRunResult result = await new RimTestDoctorRunner(stderr)
            .RunAsync(
                request,
                processTransport ?? new SystemDevBridgeProcessTransport(),
                cancellationToken)
            .ConfigureAwait(false);
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "rimliaison-golden-path-preflight/v1",
            ["status"] = result.ExitCode == CliExitCodes.Success ? "ready" : "blocked",
            ["ready"] = result.ExitCode == CliExitCodes.Success,
            ["owner"] = "RimLiaison",
            ["nextAction"] = result.Output.TryGetValue("nextAction", out object? nextAction)
                ? nextAction
                : null
        };
        if (projectBinding is not null)
        {
            output["projectBinding"] = projectBinding.ToEvidence();
        }
        ExecutionPacketGenerationResult packet = ExecutionPacketCoordinator.TryGenerate(
            AffectedGitRoot(request),
            request.RimContextStorePath,
            Environment.GetEnvironmentVariable("RIMTEST_TASK") ??
                "substantive development task",
            repository: request.StackManifest.Manifest?.Project,
            project: request.StackManifest.Manifest?.Project,
            additionalEvidence: CatalogImpactEvidence(request.CatalogPath));
        output["executionPacketStatus"] = packet.Succeeded
            ? ExecutionPacketStatuses.Valid
            : ExecutionPacketStatuses.Unavailable;
        if (packet.Packet is not null)
        {
            output["executionPacket"] = packet.Packet;
            output["impactPrediction"] = packet.Prediction;
        }
        else
        {
            output["executionPacketError"] = new
            {
                code = packet.ErrorCode,
                message = packet.Error
            };
        }
        if (result.Output.TryGetValue("project", out object? project))
        {
            output["project"] = project;
        }
        if (result.ExitCode != CliExitCodes.Success)
        {
            output["code"] = result.Output.TryGetValue("code", out object? code)
                ? code
                : result.Output.TryGetValue("errorCode", out object? errorCode)
                    ? errorCode
                    : "PREFLIGHT_BLOCKED";
            output["blockingState"] = "required";
        }
        WriteJson(stdout, output);
        return result.ExitCode;
    }

    private static async Task<int> ExecuteContextCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeProcessTransport? processTransport,
        IAgentObservabilityStore observabilityStore,
        ObservabilityEntityIdentity observabilityEntity,
        CancellationToken cancellationToken)
    {
        string rootPath = AffectedGitRoot(request);
        IReadOnlyDictionary<string, string>? relatedRepositoryRoots =
            DiscoverContextRelatedRepositoryRoots(rootPath);
        (string modId, string modName) = ResolveObservabilityMod(observabilityEntity);
        var provider = new RimLiaisonContextBundleProvider(
            new RimLiaisonContextProviderOptions
            {
                RootPath = rootPath,
                CatalogPath = request.CatalogPath,
                Project = request.StackManifest.Manifest?.Project,
                ObservabilityModName = modName,
                RimContextStorePath = request.RimContextStorePath,
                DevBridgePath = request.DevBridgePath,
                DevBridgeRootPath = request.DevBridgeRootPath,
                DevBridgeProject = request.DevBridgeProject,
                RimErrorPath = request.RimErrorPath,
                RimErrorLogPath = request.RimErrorLogPath,
                RimErrorStorePath = request.RimErrorStorePath,
                FallbackSuite = request.FallbackSuite,
                StackManifestPath = request.StackManifest.ManifestPath,
                ObservabilityModId = modId,
                RelatedRepositoryRoots = relatedRepositoryRoots,
                ProcessTransport = processTransport,
                ObservabilityStore = observabilityStore
            });
        RimContextBundle bundle = await RimContextBundleBuilder.BuildAsync(
                new RimContextBundleRequest(
                    RootPath: rootPath,
                    StorePath: request.RimContextStorePath,
                    Verbose: request.ContextVerbose,
                    MaxDecisions: request.ContextVerbose ? 32 : 8,
                    MaxRecentExecutions: request.ContextVerbose ? 32 : 8,
                    MaxFailures: request.ContextVerbose ? 32 : 8,
                    MaxExtensions: request.ContextVerbose ? 32 : 12),
                [provider],
                cancellationToken)
            .ConfigureAwait(false);
        stdout.WriteLine(RimContextBundleJson.Serialize(bundle, request.ContextVerbose));
        return CliExitCodes.Success;
    }

    private static async Task<int> ExecutePublishCheckCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IAgentObservabilityStore observabilityStore,
        IGitChangeProvider? suppliedChangeProvider,
        ObservabilityEntityIdentity observabilityEntity,
        CancellationToken cancellationToken)
    {
        string rootPath = AffectedGitRoot(request);
        GitRepositoryStateResult repositoryResult = await new SystemGitRepositoryStateProvider()
            .ReadAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        if (!repositoryResult.Resolved || repositoryResult.State is null)
        {
            var blocked = new
            {
                schemaVersion = "rimliaison-publication-check/v1",
                status = "blocked",
                safeToPublish = false,
                publicationAction = "block",
                reasonCode = repositoryResult.ErrorCode ?? "GIT_STATE_UNAVAILABLE",
                nextAction = "git status --short"
            };
            RecordPublicationCheck(blocked);
            WriteJson(stdout, blocked);
            return CliExitCodes.ConservativeSelection;
        }

        GitRepositoryStateSnapshot repository = repositoryResult.State;
        IReadOnlyList<GitRepositoryChange> changes;
        if (request.AffectedBase is null)
        {
            changes = repository.Changes;
        }
        else
        {
            IGitChangeProvider changeProvider = suppliedChangeProvider ?? new SystemGitChangeProvider();
            GitChangeDiscoveryResult discovered = await changeProvider
                .DiscoverAsync(rootPath, request.AffectedBase, cancellationToken)
                .ConfigureAwait(false);
            if (!discovered.Resolved)
            {
                var blocked = new
                {
                    schemaVersion = "rimliaison-publication-check/v1",
                    status = "blocked",
                    safeToPublish = false,
                    publicationAction = "block",
                    reasonCode = discovered.ErrorCode ?? "GIT_DISCOVERY_FAILED",
                    nextAction = "git status --short"
                };
                RecordPublicationCheck(blocked);
                WriteJson(stdout, blocked);
                return CliExitCodes.ConservativeSelection;
            }

            changes = discovered.Changes
                .Select(change => new GitRepositoryChange(
                    change.Path,
                    change.Status,
                    change.Status.Contains("?", StringComparison.Ordinal),
                    ValidationChangeAnalyzer.IsGeneratedPath(change.Path),
                    change.OriginalPath))
                .ToArray();
        }

        ValidationPublicationCheck publication = ValidationPublicationChecker.Evaluate(
            repository,
            changes,
            observabilityStore,
            ResolveObservabilityMod(observabilityEntity).ModId,
            ValidationConfiguration(request),
            dependencyFingerprints: request.DependencyFingerprints);
        ValidationChangeAnalysis analysis = publication.Analysis;
        ValidationPublicationResult result = publication.Result;
        var output = new
        {
            schemaVersion = "rimliaison-publication-check/v1",
            status = result.Status,
            safeToPublish = result.SafeToPublish,
            publicationAction = result.PublicationAction,
            action = result.SafeToPublish
                ? RimContextDecisionActions.Reuse
                : RimContextDecisionActions.Block,
            decision = "publication-evidence",
            reasonCode = analysis.ReasonCode,
            owner = "RimTest/RimLiaison",
            changeCategory = analysis.Category,
            meaningfulChangedInputs = analysis.MeaningfulPaths,
            generatedChangedInputs = analysis.GeneratedPaths,
            requiredValidation = result.RequiredValidation,
            reusedEvidence = result.ReusedEvidence,
            invalidatedEvidence = result.InvalidatedEvidence,
            reusedEvidenceCount = result.ReusedEvidenceCount,
            invalidatedEvidenceCount = result.InvalidatedEvidenceCount,
            newValidationCount = result.NewValidationCount,
            decisions = result.Decisions,
            nextAction = result.NextAction,
            evidenceCount = publication.EvidenceCount
        };
        RecordPublicationCheck(output);
        WriteJson(stdout, output);
        return result.SafeToPublish
            ? CliExitCodes.Success
            : CliExitCodes.ConservativeSelection;

        void RecordPublicationCheck(object value)
        {
            AgentObservabilityRuntime.Record(
                DevelopmentStage.Analysis,
                AgentEventTypes.PublicationChecked,
                "Git publication evidence check recorded.",
                value);
        }
    }

    private static IReadOnlyDictionary<string, string> ValidationConfiguration(
        CliRequest request) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = Path.GetFullPath(request.CatalogPath),
            ["devBridgeProject"] = request.DevBridgeProject ?? "unknown",
            ["fallbackSuite"] = request.FallbackSuite ?? "unknown"
        };

    private static void RecordValidationEvidence(
        RimTestSuiteResult result,
        IReadOnlyList<string> selectedTestIds,
        CliRequest request,
        ArtifactFreshnessTransactionRequest? freshnessRequest,
        IReadOnlyList<string>? validationChangedPaths)
    {
        IReadOnlyList<string> sourceInputs = validationChangedPaths ??
            freshnessRequest?.ChangedPaths ??
            [];
        string? contentFingerprint = result.ArtifactFreshness?.SourceFingerprint;
        if (contentFingerprint is null && sourceInputs.Count > 0 &&
            WorktreeFingerprint.TryCompute(
                AffectedGitRoot(request),
                sourceInputs,
                out string computedFingerprint,
                out _))
        {
            contentFingerprint = computedFingerprint;
        }

        ValidationEvidenceRecord evidence = ValidationEvidenceFactory.FromSuiteResult(
            ValidationEvidenceFactory.RepositoryIdentity(AffectedGitRoot(request)),
            commitSha: null,
            contentFingerprint,
            sourceInputs,
            result.Suite,
            selectedTestIds,
            result,
            DateTimeOffset.UtcNow,
            dependencyFingerprints: request.DependencyFingerprints,
            toolVersions: ValidationEvidenceFactory.DefaultToolVersions(),
            configuration: ValidationConfiguration(request),
            environmentFingerprint: ValidationEvidenceFactory.DefaultEnvironmentFingerprint());
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationEvidenceRecorded,
            "Immutable validation evidence recorded.",
            new
            {
                evidenceId = evidence.EvidenceId,
                validationEvidence = evidence,
                validationKind = evidence.Identity.ValidationKind,
                result = evidence.Result,
                reusable = evidence.Reusable,
                sourceFingerprint = evidence.Identity.ContentFingerprint,
                suiteId = evidence.Identity.SuiteId,
                testIds = evidence.Identity.TestIds,
                owner = "RimTest/RimLiaison"
            });
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationEvidenceDecision,
            "Validation evidence decision recorded after suite execution.",
            new
            {
                decision = "validation-evidence",
                action = evidence.Reusable
                    ? RimContextDecisionActions.Run
                    : RimContextDecisionActions.Block,
                reasonCode = evidence.Reusable
                    ? ValidationDecisionReasonCodes.EvidenceRecorded
                    : ValidationDecisionReasonCodes.EvidenceResultNotPass,
                explanation = evidence.Reusable
                    ? "The selected validation ran and produced reusable evidence."
                    : "The validation result is not safe for publication reuse.",
                evidenceReused = Array.Empty<object>(),
                evidenceInvalidated = Array.Empty<object>(),
                owner = "RimTest/RimLiaison",
                evidenceId = evidence.EvidenceId,
                durationMs = result.DurationMs
            });
    }


    private static async Task<int> ExecuteCapabilitiesCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeCapabilityAdapter? capabilityAdapter,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        IDevBridgeCapabilityAdapter adapter = CreateCapabilityAdapter(
            request,
            processTransport,
            capabilityAdapter);
        var query = new DevBridgeCapabilityQuery(
            request.CapabilityQuery,
            request.CapabilityCategory,
            request.CapabilityProvider,
            request.CapabilitySource,
            request.CapabilityLimit);
        DevBridgeCapabilityDiscoveryResult result = await adapter.DiscoverAsync(
                query,
                workflowId,
                request.UiLeaseId,
                cancellationToken)
            .ConfigureAwait(false);
        DevBridgeCapabilityRecoveryResult? recovery = null;

        if (!result.Status.IsSuccess)
        {
            RecordCapabilityFailure(result.Status);
            if (RuntimeTransitionRecoveryClassifier.IsRecoverableCapability(result.Status) &&
                TryGetCapabilityRecovery(adapter, request, processTransport, out var recoveryContext))
            {
                RecordCapabilityEvent(
                    AgentEventTypes.RetryStarted,
                    "Retrying DevBridge capability discovery after bounded recovery.",
                    result.Status,
                    new { retryCount = 1, recoveryAction = "doctor" });
                recovery = await DevBridgeCapabilityRecovery.RecoverAsync(
                        recoveryContext.Transport,
                        recoveryContext.Options,
                        workflowId,
                        cancellationToken,
                        triggerCode: result.Status.ErrorCode)
                    .ConfigureAwait(false);
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Analysis,
                    AgentEventTypes.RecoveryCompleted,
                    recovery.Succeeded
                        ? "DevBridge capability recovery completed."
                        : "DevBridge capability recovery failed.",
                    new
                    {
                        state = recovery.State.ToString(),
                        attempts = recovery.Attempts,
                        errorCode = recovery.ErrorCode,
                        error = recovery.Error
                    });

                if (recovery.Succeeded)
                {
                    DevBridgeCapabilityDiscoveryResult retry = await adapter.DiscoverAsync(
                            query,
                            workflowId,
                            request.UiLeaseId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    result = retry;
                    AgentObservabilityRuntime.Record(
                        DevelopmentStage.Analysis,
                        AgentEventTypes.RetryCompleted,
                        retry.Status.IsSuccess
                            ? "DevBridge capability retry succeeded."
                            : "DevBridge capability retry remained failed.",
                        new
                        {
                            retryCount = 1,
                            recovered = retry.Status.IsSuccess,
                            errorCode = retry.Status.ErrorCode
                        });
                }
                else
                {
                    AgentObservabilityRuntime.Record(
                        DevelopmentStage.Analysis,
                        AgentEventTypes.RetryCompleted,
                        "DevBridge capability retry was not attempted after recovery failure.",
                        new
                        {
                            retryCount = 1,
                            recovered = false,
                            errorCode = recovery.ErrorCode
                        });
                }
            }
        }

        if (result.Status.IsSuccess)
        {
            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = DevBridgeCapabilitySchemas.Output,
                ["status"] = "ok",
                ["source"] = "RimBridgeServer",
                ["count"] = result.Capabilities.Count,
                ["totalMatches"] = result.TotalMatches,
                ["truncated"] = result.Truncated,
                ["limit"] = query.Limit,
                ["capabilities"] = result.Capabilities
                    .Select(ToCapabilityOutput)
                    .ToArray()
            };
            AddCapabilityFilter(output, "query", query.Text);
            AddCapabilityFilter(output, "category", query.Category);
            AddCapabilityFilter(output, "providerId", query.ProviderId);
            AddCapabilityFilter(output, "source", query.Source);
            WriteJson(stdout, output);
            return CliExitCodes.Success;
        }

        var failure = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = DevBridgeCapabilitySchemas.Output,
            ["status"] = result.Status.Outcome == DevBridgeCapabilityOutcome.Unavailable
                ? "blocked"
                : "error",
            ["component"] = "rimbridge",
            ["outcome"] = CapabilityOutcomeName(result.Status.Outcome),
            ["code"] = result.Status.ErrorCode ?? "RIMBRIDGE_CAPABILITIES_FAILED",
            ["error"] = result.Status.Error ??
                "RimLiaison could not discover the RimBridgeServer capability registry."
        };
        if (result.Status.NextAction is not null)
        {
            failure["nextAction"] = result.Status.NextAction;
        }
        if (result.Status.ResponseSchema is not null)
        {
            failure["responseSchema"] = result.Status.ResponseSchema;
        }
        if (result.Status.ProcessExitCode.HasValue)
        {
            failure["processExitCode"] = result.Status.ProcessExitCode.Value;
        }
        AddCapabilityFailureEvidence(failure, result.Status.Evidence);
        if (recovery is not null)
        {
            failure["recovery"] = new
            {
                state = recovery.State.ToString(),
                attempts = recovery.Attempts,
                errorCode = recovery.ErrorCode,
                trigger = recovery.Trigger,
                highestLevel = recovery.HighestLevel,
                rimWorldRestarted = recovery.RimWorldRestarted,
                finalState = recovery.FinalState,
                elapsedRecoveryMs = recovery.ElapsedRecoveryMilliseconds,
                actions = recovery.Actions,
                error = recovery.Error
            };
        }

        WriteJson(stdout, failure);
        return CapabilityExitCodeFor(result.Status.Outcome);
    }


    private static bool TryGetCapabilityRecovery(
        IDevBridgeCapabilityAdapter adapter,
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        out CapabilityRecoveryContext context)
    {
        if (adapter is DevBridgeCapabilityAdapter concrete)
        {
            context = new(
                processTransport ?? new SystemDevBridgeProcessTransport(),
                concrete.Options);
            return true;
        }

        if (processTransport is not null)
        {
            context = new(
                processTransport,
                DevBridgeAdapterOptions.Discover(
                    request.DevBridgePath,
                    request.DevBridgeRootPath));
            return true;
        }
        context = null!;
        return false;
    }

    private static void RecordCapabilityFailure(DevBridgeCapabilityStatus status) =>
        RecordCapabilityEvent(
            AgentEventTypes.ToolFailed,
            "DevBridge capability discovery failed.",
            status,
            new { toolName = "DevBridge", command = "bridge tools" });

    private static void RecordCapabilityEvent(
        string type,
        string summary,
        DevBridgeCapabilityStatus status,
        object? additionalData = null)
    {
        DevBridgeFailureEvidence? evidence = status.Evidence;
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["toolName"] = "DevBridge",
            ["command"] = evidence?.Command ?? "bridge tools",
            ["errorCode"] = evidence?.UnderlyingErrorCode ?? status.ErrorCode,
            ["underlyingErrorCode"] = evidence?.UnderlyingErrorCode,
            ["outerErrorCode"] = evidence?.OuterErrorCode,
            ["error"] = evidence?.UnderlyingError ?? status.Error,
            ["outerError"] = status.Error,
            ["exitCode"] = evidence?.ExitCode ?? status.ProcessExitCode,
            ["leaseId"] = evidence?.LeaseId,
            ["scopeIdentity"] = evidence?.ScopeIdentity,
            ["generation"] = evidence?.Generation,
            ["route"] = evidence?.Route,
            ["readinessIdentity"] = evidence?.ReadinessIdentity,
            ["stdoutTail"] = evidence?.StdoutTail,
            ["stderrTail"] = evidence?.StderrTail,
            ["diagnosticTail"] = evidence?.DiagnosticTail
        };
        if (additionalData is not null)
        {
            foreach (var property in additionalData.GetType().GetProperties())
            {
                data[property.Name] = property.GetValue(additionalData);
            }
        }

        AgentObservabilityRuntime.Record(
            DevelopmentStage.Analysis,
            type,
            summary,
            data);
    }

    private static void AddCapabilityFailureEvidence(
        IDictionary<string, object?> output,
        DevBridgeFailureEvidence? evidence)
    {
        if (evidence is null)
        {
            return;
        }

        AddIfPresent(output, "outerErrorCode", evidence.OuterErrorCode);
        AddIfPresent(output, "underlyingErrorCode", evidence.UnderlyingErrorCode);
        AddIfPresent(output, "underlyingError", evidence.UnderlyingError);
        AddIfPresent(output, "coordinatorRoot", evidence.CoordinatorRoot);
        AddIfPresent(output, "leaseId", evidence.LeaseId);
        AddIfPresent(output, "scopeIdentity", evidence.ScopeIdentity);
        if (evidence.Generation.HasValue)
        {
            output["generation"] = evidence.Generation.Value;
        }
        AddIfPresent(output, "route", evidence.Route);
        AddIfPresent(output, "readinessIdentity", evidence.ReadinessIdentity);
        AddIfPresent(output, "stdoutTail", evidence.StdoutTail);
        AddIfPresent(output, "stderrTail", evidence.StderrTail);
        AddIfPresent(output, "diagnosticTail", evidence.DiagnosticTail);
    }

    private static void AddIfPresent(
        IDictionary<string, object?> output,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[key] = value;
        }
    }

    private sealed record CapabilityRecoveryContext(
        IDevBridgeProcessTransport Transport,
        DevBridgeAdapterOptions Options);
    private static async Task<int> ExecuteUiCommandAsync(
        CliRequest request,
        TextWriter stdout,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeUiAdapter? uiAdapter,
        IDevBridgeViewportAdapter? viewportAdapter,
        IDevBridgeLeaseAdapter? leaseAdapter,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        IDevBridgeUiAdapter adapter = CreateUiAdapter(
            request,
            processTransport,
            uiAdapter);

        if (request.Command == CliCommand.UiTargets)
        {
            DevBridgeUiTargetsResult result = await adapter.GetTargetsAsync(
                    workflowId,
                    request.UiLeaseId,
                    cancellationToken)
                .ConfigureAwait(false);
            DevBridgeLeaseResult? targetLeaseAcquisition = null;
            DevBridgeLeaseResult? targetLeaseRelease = null;
            IDevBridgeLeaseAdapter? targetLeaseAdapter = null;
            if (!result.Status.IsSuccess &&
                IsLeaseRequired(result.Status))
            {
                targetLeaseAdapter = CreateLeaseAdapter(
                    request,
                    processTransport,
                    leaseAdapter);
                targetLeaseAcquisition = await targetLeaseAdapter.BeginLeaseAsync(
                        workflowId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!targetLeaseAcquisition.IsUsable)
                {
                    WriteUiFailure(
                        stdout,
                        DevBridgeUiSchemas.Targets,
                        new DevBridgeUiStatus(
                            targetLeaseAcquisition.Status.Outcome switch
                            {
                                DevBridgeOutcomeKind.DevBridgeRefusal => DevBridgeUiOutcome.Unavailable,
                                DevBridgeOutcomeKind.Timeout => DevBridgeUiOutcome.Timeout,
                                DevBridgeOutcomeKind.Cancelled => DevBridgeUiOutcome.Cancelled,
                                _ => DevBridgeUiOutcome.InfrastructureFailure
                            },
                            targetLeaseAcquisition.Status.ErrorCode,
                            targetLeaseAcquisition.Status.Error,
                            targetLeaseAcquisition.Status.ProcessExitCode,
                            NextAction: NextActionFor(targetLeaseAcquisition.Status.Outcome)));
                    return LeaseExitCodeFor(targetLeaseAcquisition.Status.Outcome);
                }

                try
                {
                    result = await adapter.GetTargetsAsync(
                            workflowId,
                            targetLeaseAcquisition.LeaseId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    targetLeaseRelease = await targetLeaseAdapter.EndLeaseAsync(
                            targetLeaseAcquisition.LeaseId!,
                            workflowId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (targetLeaseRelease is not null && !targetLeaseRelease.Status.IsSuccess)
                {
                    result = new DevBridgeUiTargetsResult(
                        new DevBridgeUiStatus(
                            DevBridgeUiOutcome.InfrastructureFailure,
                            "RIMTEST_UI_TARGETS_LEASE_RELEASE_FAILED",
                            "Target discovery completed, but the temporary lease was not released safely.",
                            targetLeaseRelease.Status.ProcessExitCode,
                            NextAction: NextActionFor(targetLeaseRelease.Status.Outcome)),
                        []);
                }
            }

            if (!result.Status.IsSuccess)
            {
                WriteUiFailure(stdout, DevBridgeUiSchemas.Targets, result.Status);
                return UiExitCodeFor(result.Status.Outcome);
            }

            var targets = result.Targets
                .Select(ToUiTargetOutput)
                .ToArray();
            var output = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = DevBridgeUiSchemas.Targets,
                ["status"] = "ok",
                ["count"] = targets.Length,
                ["targets"] = targets
            };
            AddUiCorrelation(output, result.Status);
            WriteJson(stdout, output);
            return CliExitCodes.Success;
        }

        DevBridgeUiCellRect? cellRect = null;
        DevBridgeUiCellRect parsedCellRect = default!;
        if (request.UiCellRect is not null &&
            !DevBridgeUiAdapter.TryParseCellRect(
                request.UiCellRect,
                out parsedCellRect,
                out string cellRectError))
        {
            WriteUiFailure(
                stdout,
                DevBridgeUiSchemas.Screenshot,
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InvalidRequest,
                    "RIMTEST_UI_CELL_RECT_INVALID",
                    cellRectError,
                    NextAction: null));
            return CliExitCodes.InvalidInput;
        }
        else if (request.UiCellRect is not null)
        {
            cellRect = parsedCellRect;
        }

        DevBridgeViewportResult? viewportPreparation = null;
        DevBridgeViewportResult? viewportRestoration = null;
        DevBridgeLeaseResult? leaseAcquisition = null;
        DevBridgeLeaseResult? leaseRelease = null;
        string? viewportLeaseId = request.UiLeaseId;
        bool ownsViewportLease = false;
        IDevBridgeViewportAdapter? environmentAdapter = null;
        IDevBridgeLeaseAdapter? lifecycleLeaseAdapter = null;
        DevBridgeViewportRequest? preparedViewportRequest = null;
        DevBridgeUiInputCheckResult? inputCheck = null;
        if (request.UiViewport is not null)
        {
            if (!DevBridgeViewportRequest.TryCreate(
                    request.UiViewport,
                    request.UiViewportWidth,
                    request.UiViewportHeight,
                    out DevBridgeViewportRequest viewportRequest,
                    out string viewportError))
            {
                WriteUiViewportFailure(
                    stdout,
                    null,
                    new DevBridgeViewportResult(
                        new DevBridgeViewportStatus(
                            DevBridgeViewportOutcome.InvalidRequest,
                            "RIMTEST_VIEWPORT_REQUEST_INVALID",
                            viewportError,
                            NextAction: null),
                        null),
                    null,
                    null,
                    null);
                return CliExitCodes.InvalidInput;
            }

            preparedViewportRequest = viewportRequest;
            environmentAdapter = CreateViewportAdapter(
                request,
                processTransport,
                viewportAdapter);
            if (viewportLeaseId is null)
            {
                lifecycleLeaseAdapter = CreateLeaseAdapter(
                    request,
                    processTransport,
                    leaseAdapter);
                leaseAcquisition = await lifecycleLeaseAdapter.BeginLeaseAsync(
                        workflowId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!leaseAcquisition.IsUsable)
                {
                    WriteUiViewportFailure(
                        stdout,
                        null,
                        null,
                        null,
                        leaseAcquisition,
                        null);
                    return LeaseExitCodeFor(leaseAcquisition.Status.Outcome);
                }

                viewportLeaseId = leaseAcquisition.LeaseId;
                ownsViewportLease = true;
            }

            viewportPreparation = await environmentAdapter.BeginAsync(
                    viewportRequest,
                    viewportLeaseId!,
                    workflowId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!viewportPreparation.Status.IsSuccess ||
                string.IsNullOrWhiteSpace(viewportPreparation.Evidence?.TransactionId))
            {
                if (ownsViewportLease && lifecycleLeaseAdapter is not null)
                {
                    leaseRelease = await lifecycleLeaseAdapter.EndLeaseAsync(
                            viewportLeaseId!,
                            workflowId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                WriteUiViewportFailure(
                    stdout,
                    null,
                    viewportPreparation,
                    null,
                    leaseRelease,
                    null);
                return ViewportExitCodeFor(viewportPreparation.Status.Outcome);
            }
        }

        var screenshotRequest = new DevBridgeUiScreenshotRequest(
            request.UiTarget,
            cellRect);
        DevBridgeUiScreenshotResult screenshot;
        try
        {
            if (request.UiInputCheck)
            {
                inputCheck = adapter is IDevBridgeUiInspectionAdapter inspectionAdapter
                    ? await inspectionAdapter.CheckInputAsync(
                            workflowId,
                            viewportLeaseId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : new DevBridgeUiInputCheckResult(
                        new DevBridgeUiStatus(
                            DevBridgeUiOutcome.IncompatibleSchema,
                            "RIMTEST_UI_INPUT_CHECK_UNSUPPORTED",
                            "The configured UI adapter does not expose semantic input-state inspection.",
                            NextAction: null),
                        null);
            }

            if (inputCheck is not null &&
                !inputCheck.Status.IsSuccess &&
                !string.Equals(inputCheck.Status.ErrorCode,
                    "RIMTEST_UI_CAPABILITY_MISSING", StringComparison.Ordinal))
            {
                screenshot = new DevBridgeUiScreenshotResult(inputCheck.Status, null);
            }
            else
            {
                screenshot = await adapter.CaptureAsync(
                        screenshotRequest,
                        workflowId,
                        viewportLeaseId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            screenshot = new DevBridgeUiScreenshotResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.Cancelled,
                    "RIMTEST_CANCELLED",
                    "The RimLiaison UI screenshot request was cancelled.",
                    NextAction: null),
                null);
        }
        catch (Exception exception)
        {
            screenshot = new DevBridgeUiScreenshotResult(
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "RIMTEST_UI_CAPTURE_EXCEPTION",
                    BoundUiMessage(exception.Message)),
                null);
        }
        finally
        {
            if (viewportPreparation?.Evidence?.TransactionId is not null &&
                environmentAdapter is not null)
            {
                viewportRestoration = await environmentAdapter.RestoreAsync(
                        viewportPreparation.Evidence.TransactionId,
                        viewportLeaseId!,
                        workflowId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (ownsViewportLease && lifecycleLeaseAdapter is not null)
            {
                leaseRelease = await lifecycleLeaseAdapter.EndLeaseAsync(
                        viewportLeaseId!,
                        workflowId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        if (!screenshot.Status.IsSuccess || screenshot.Evidence is null)
        {
            WriteUiViewportFailure(
                stdout,
                screenshot.Status,
                viewportPreparation,
                viewportRestoration,
                leaseRelease,
                screenshot,
                inputCheck);
            return UiExitCodeFor(screenshot.Status.Outcome);
        }

        if (preparedViewportRequest is not null &&
            !TryValidateViewportLayout(
                preparedViewportRequest,
                viewportPreparation!.Evidence!,
                screenshot.Evidence,
                out string layoutError))
        {
            WriteUiViewportFailure(
                stdout,
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "RIMTEST_UI_LAYOUT_ASSERTION_FAILED",
                    layoutError,
                    NextAction: viewportRestoration?.Status.NextAction),
                viewportPreparation,
                viewportRestoration,
                leaseRelease,
                screenshot,
                inputCheck);
            return CliExitCodes.InternalError;
        }

        if (viewportRestoration is not null && !viewportRestoration.Status.IsSuccess)
        {
            WriteUiViewportFailure(
                stdout,
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "RIMTEST_UI_VIEWPORT_RESTORE_FAILED",
                    viewportRestoration.Status.Error ??
                    "The screenshot was captured, but the user's prior viewport was not verified as restored.",
                    viewportRestoration.Status.ProcessExitCode,
                    NextAction: viewportRestoration.Status.NextAction),
                viewportPreparation,
                viewportRestoration,
                leaseRelease,
                screenshot,
                inputCheck);
            return CliExitCodes.InternalError;
        }

        if (leaseRelease is not null && !leaseRelease.Status.IsSuccess)
        {
            WriteUiViewportFailure(
                stdout,
                new DevBridgeUiStatus(
                    DevBridgeUiOutcome.InfrastructureFailure,
                    "RIMTEST_UI_VIEWPORT_LEASE_RELEASE_FAILED",
                    "The screenshot completed, but the temporary viewport lease was not released safely.",
                    leaseRelease.Status.ProcessExitCode),
                viewportPreparation,
                viewportRestoration,
                leaseRelease,
                screenshot);
            return CliExitCodes.InternalError;
        }

        var evidence = screenshot.Evidence;
        var screenshotOutput = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = DevBridgeUiSchemas.Screenshot,
            ["status"] = "ok",
            ["captureStatus"] = evidence.CaptureStatus,
            ["path"] = evidence.Path
        };
        AddUiField(screenshotOutput, "targetId", evidence.TargetId);
        AddUiField(screenshotOutput, "targetKind", evidence.TargetKind);
        AddUiField(screenshotOutput, "targetLabel", evidence.TargetLabel);
        AddUiElement(screenshotOutput, "clipRect", evidence.ClipRect);
        AddUiElement(screenshotOutput, "requestedRect", evidence.RequestedRect);
        AddUiElement(screenshotOutput, "paddedRect", evidence.PaddedRect);
        if (evidence.CameraRestored.HasValue)
        {
            screenshotOutput["cameraRestored"] = evidence.CameraRestored.Value;
        }

        AddUiField(screenshotOutput, "capturedAtUtc", evidence.CapturedAtUtc);
        AddUiField(screenshotOutput, "operationId", evidence.OperationId);
        AddUiField(screenshotOutput, "workflowId", evidence.WorkflowId);
        AddUiField(screenshotOutput, "evidenceId", evidence.EvidenceId);
        if (viewportPreparation is not null)
        {
            screenshotOutput["viewport"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["preparation"] = ToViewportOutput(viewportPreparation),
                ["restoration"] = ToViewportOutput(viewportRestoration)
            };
        }
        if (inputCheck is not null)
        {
            screenshotOutput["inputCheck"] = ToInputCheckOutput(inputCheck);
        }
        WriteJson(stdout, screenshotOutput);
        return CliExitCodes.Success;
    }

    private static bool TryValidateViewportLayout(
        DevBridgeViewportRequest request,
        DevBridgeViewportEvidence viewport,
        DevBridgeUiScreenshotEvidence screenshot,
        out string error)
    {
        error = "The live viewport response did not include verified client dimensions.";
        if (viewport.EffectiveViewport is not JsonElement effective ||
            !effective.TryGetProperty("clientWidth", out JsonElement widthElement) ||
            !effective.TryGetProperty("clientHeight", out JsonElement heightElement) ||
            !widthElement.TryGetInt32(out int width) ||
            !heightElement.TryGetInt32(out int height) || width < 1 || height < 1)
        {
            return false;
        }

        if (request.Width.HasValue && request.Height.HasValue &&
            (width != request.Width.Value || height != request.Height.Value))
        {
            error = $"The effective client viewport was {width}x{height}, not the requested {request.Width}x{request.Height}.";
            return false;
        }

        if (screenshot.ClipRect is not JsonElement clip ||
            clip.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (clip.TryGetProperty("width", out JsonElement clipWidthElement) &&
            clip.TryGetProperty("height", out JsonElement clipHeightElement) &&
            clipWidthElement.TryGetInt32(out int clipWidth) &&
            clipHeightElement.TryGetInt32(out int clipHeight) &&
            (clipWidth < 0 || clipHeight < 0 || clipWidth > width || clipHeight > height))
        {
            error = $"The captured UI region {clipWidth}x{clipHeight} exceeds the verified client viewport {width}x{height}.";
            return false;
        }

        return true;
    }

    private static Dictionary<string, object?> ToUiTargetOutput(
        DevBridgeUiTarget target)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = target.Id
        };
        AddUiField(output, "kind", target.Kind);
        AddUiField(output, "label", target.Label);
        AddUiElement(output, "rect", target.Rect);
        return output;
    }

    private static void WriteUiFailure(
        TextWriter stdout,
        string schema,
        DevBridgeUiStatus status)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = schema,
            ["status"] = status.Outcome is
                DevBridgeUiOutcome.Unavailable or
                DevBridgeUiOutcome.VisualReadinessFailure
                ? "blocked"
                : "error",
            ["component"] = "rimbridge",
            ["outcome"] = UiOutcomeName(status.Outcome),
            ["code"] = status.ErrorCode ?? "RIMTEST_UI_FAILED",
            ["error"] = status.Error ?? "RimLiaison could not complete the UI request."
        };
        AddUiField(output, "nextAction", status.NextAction);
        if (status.ProcessExitCode.HasValue)
        {
            output["processExitCode"] = status.ProcessExitCode.Value;
        }

        AddUiField(output, "operationId", status.OperationId);
        AddUiField(output, "workflowId", status.WorkflowId);
        AddUiField(output, "evidenceId", status.EvidenceId);
        WriteJson(stdout, output);
    }

    private static void WriteUiViewportFailure(
        TextWriter stdout,
        DevBridgeUiStatus? uiStatus,
        DevBridgeViewportResult? preparation,
        DevBridgeViewportResult? restoration,
        DevBridgeLeaseResult? lease,
        DevBridgeUiScreenshotResult? screenshot,
        DevBridgeUiInputCheckResult? inputCheck = null)
    {
        DevBridgeViewportResult? failedViewport =
            restoration is not null && !restoration.Status.IsSuccess
                ? restoration
                : preparation is not null && !preparation.Status.IsSuccess
                    ? preparation
                    : null;
        DevBridgeViewportStatus? viewportStatus = failedViewport?.Status;
        DevBridgeAdapterStatus? leaseStatus = lease?.Status;
        string? code = uiStatus?.ErrorCode ?? viewportStatus?.ErrorCode ??
            leaseStatus?.ErrorCode;
        string? error = uiStatus?.Error ?? viewportStatus?.Error ?? leaseStatus?.Error;
        string? nextAction = uiStatus?.NextAction ?? viewportStatus?.NextAction;
        if (nextAction is null && leaseStatus is not null)
        {
            nextAction = "DevBridge.cmd doctor --json";
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = DevBridgeUiSchemas.Screenshot,
            ["status"] = uiStatus?.Outcome is
                DevBridgeUiOutcome.Unavailable or
                DevBridgeUiOutcome.VisualReadinessFailure
                || viewportStatus?.Outcome is
                    DevBridgeViewportOutcome.Unavailable or
                    DevBridgeViewportOutcome.Busy or
                    DevBridgeViewportOutcome.Unsupported
                ? "blocked"
                : "error",
            ["component"] = preparation is not null || restoration is not null || lease is not null
                ? "devbridge"
                : "rimbridge",
            ["outcome"] = uiStatus is not null
                ? UiOutcomeName(uiStatus.Outcome)
                : viewportStatus is not null
                    ? ViewportOutcomeName(viewportStatus.Outcome)
                    : leaseStatus is not null
                        ? OutcomeName(leaseStatus.Outcome)
                        : "infrastructureFailure",
            ["code"] = code ?? "RIMTEST_UI_VIEWPORT_FAILED",
            ["error"] = error ?? "RimLiaison could not complete the transactional UI request."
        };
        AddUiField(output, "nextAction", nextAction);
        if (uiStatus?.ProcessExitCode is int uiExit)
        {
            output["processExitCode"] = uiExit;
        }
        else if (viewportStatus?.ProcessExitCode is int viewportExit)
        {
            output["processExitCode"] = viewportExit;
        }
        else if (leaseStatus?.ProcessExitCode is int leaseExit)
        {
            output["processExitCode"] = leaseExit;
        }

        AddUiField(output, "operationId", uiStatus?.OperationId);
        AddUiField(output, "workflowId", uiStatus?.WorkflowId);
        AddUiField(output, "evidenceId", uiStatus?.EvidenceId);

        if (preparation is not null)
        {
            output["viewportPreparation"] = ToViewportOutput(preparation);
        }
        if (restoration is not null)
        {
            output["viewportRestoration"] = ToViewportOutput(restoration);
        }
        if (screenshot?.Evidence is not null)
        {
            output["screenshotEvidence"] = ToUiScreenshotEvidenceOutput(screenshot.Evidence);
        }
        if (inputCheck is not null)
        {
            output["inputCheck"] = ToInputCheckOutput(inputCheck);
        }

        WriteJson(stdout, output);
    }

    private static Dictionary<string, object?> ToInputCheckOutput(
        DevBridgeUiInputCheckResult result)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = result.Status.ErrorCode == "RIMTEST_UI_CAPABILITY_MISSING"
                ? "notApplicable"
                : result.Status.IsSuccess ? "ready" : "blocked",
            ["outcome"] = UiOutcomeName(result.Status.Outcome)
        };
        AddUiField(output, "code", result.Status.ErrorCode);
        AddUiField(output, "error", result.Status.Error);
        AddUiField(output, "nextAction", result.Status.NextAction);
        AddUiElement(output, "evidence", result.Evidence);
        return output;
    }

    private static Dictionary<string, object?> ToViewportOutput(
        DevBridgeViewportResult? result)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (result is null)
        {
            output["status"] = "notRequested";
            return output;
        }

        output["schemaVersion"] = DevBridgeViewportSchemas.Environment;
        output["status"] = result.Evidence?.Status ??
            (result.Status.IsSuccess ? "prepared" : "error");
        output["outcome"] = ViewportOutcomeName(result.Status.Outcome);
        output["success"] = result.Status.IsSuccess;
        AddUiField(output, "code", result.Status.ErrorCode);
        AddUiField(output, "error", result.Status.Error);
        AddUiField(output, "nextAction", result.Status.NextAction);
        if (result.Status.ProcessExitCode.HasValue)
        {
            output["processExitCode"] = result.Status.ProcessExitCode.Value;
        }

        DevBridgeViewportEvidence? evidence = result.Evidence;
        if (evidence is null)
        {
            return output;
        }

        AddUiField(output, "transactionId", evidence.TransactionId);
        AddUiField(output, "leaseId", evidence.LeaseId);
        if (evidence.Generation.HasValue)
        {
            output["generation"] = evidence.Generation.Value;
        }
        AddUiElement(output, "requested", evidence.Requested);
        AddUiElement(output, "capturedState", evidence.CapturedState);
        AddUiElement(output, "effectiveViewport", evidence.EffectiveViewport);
        AddUiElement(output, "restoredViewport", evidence.RestoredViewport);
        output["persistentPreferenceMutation"] = evidence.PersistentPreferenceMutation;
        output["restorationVerified"] = evidence.RestorationVerified;
        AddUiField(output, "cleanupStatus", evidence.CleanupStatus);
        return output;
    }

    private static Dictionary<string, object?> ToUiScreenshotEvidenceOutput(
        DevBridgeUiScreenshotEvidence evidence)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = evidence.Path,
            ["captureStatus"] = evidence.CaptureStatus
        };
        AddUiField(output, "targetId", evidence.TargetId);
        AddUiField(output, "targetKind", evidence.TargetKind);
        AddUiField(output, "targetLabel", evidence.TargetLabel);
        AddUiElement(output, "clipRect", evidence.ClipRect);
        AddUiElement(output, "requestedRect", evidence.RequestedRect);
        AddUiElement(output, "paddedRect", evidence.PaddedRect);
        if (evidence.CameraRestored.HasValue)
        {
            output["cameraRestored"] = evidence.CameraRestored.Value;
        }
        AddUiField(output, "capturedAtUtc", evidence.CapturedAtUtc);
        AddUiField(output, "operationId", evidence.OperationId);
        AddUiField(output, "workflowId", evidence.WorkflowId);
        AddUiField(output, "evidenceId", evidence.EvidenceId);
        return output;
    }

    private static string BoundUiMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "RimLiaison could not complete the UI request.";
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private static void AddUiCorrelation(
        IDictionary<string, object?> output,
        DevBridgeUiStatus status)
    {
        AddUiField(output, "operationId", status.OperationId);
        AddUiField(output, "workflowId", status.WorkflowId);
        AddUiField(output, "evidenceId", status.EvidenceId);
    }

    private static void AddUiField(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
    }

    private static void AddUiElement(
        IDictionary<string, object?> output,
        string name,
        JsonElement? value)
    {
        if (value.HasValue)
        {
            output[name] = value.Value;
        }
    }

    private static Dictionary<string, object?> ToCapabilityOutput(
        DevBridgeCapability capability)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = capability.Id,
            ["title"] = capability.Title,
            ["parameters"] = capability.Parameters
                .Select(ToCapabilityParameterOutput)
                .ToArray()
        };
        if (capability.Aliases.Count > 0)
        {
            output["aliases"] = capability.Aliases;
        }

        AddCapabilityField(output, "summary", capability.Summary);
        AddCapabilityField(output, "category", capability.Category);
        AddCapabilityField(output, "providerId", capability.ProviderId);
        AddCapabilityField(output, "source", capability.Source);
        if (capability.ReadOnly.HasValue)
        {
            output["readOnly"] = capability.ReadOnly.Value;
        }

        return output;
    }

    private static Dictionary<string, object?> ToCapabilityParameterOutput(
        DevBridgeCapabilityParameter parameter)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = parameter.Name
        };
        AddCapabilityField(output, "type", parameter.Type);
        AddCapabilityField(output, "description", parameter.Description);
        if (parameter.Required.HasValue)
        {
            output["required"] = parameter.Required.Value;
        }

        if (parameter.DefaultValue.HasValue)
        {
            output["default"] = parameter.DefaultValue.Value;
        }

        return output;
    }

    private static void AddCapabilityFilter(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
    }

    private static void AddCapabilityField(
        IDictionary<string, object?> output,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output[name] = value;
        }
    }

    private static int WriteTestList(CatalogDocument catalog, TextWriter stdout)
    {
        var tests = (catalog.Tests ?? [])
            .Where(static test => test is not null)
            .OrderBy(static test => test.Id, StringComparer.Ordinal)
            .Select(static test => new
            {
                id = test.Id,
                recipe = test.Recipe
            })
            .ToArray();

        WriteJson(stdout, new { tests });
        return CliExitCodes.Success;
    }

    private static int WriteTest(
        CatalogDocument catalog,
        string id,
        TextWriter stdout)
    {
        CatalogTest? test = CatalogNavigator.FindTest(catalog, id);
        if (test is null)
        {
            WriteError(
                stdout,
                "TEST_NOT_FOUND",
                [new CatalogIssue("TEST_NOT_FOUND", $"Test was not found: {id}.", "id")]);
            return CliExitCodes.NotFound;
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = test.Id,
            ["recipe"] = test.Recipe,
            ["cost"] = test.Cost.ToString().ToLowerInvariant(),
            ["suites"] = CatalogNavigator.ContainingSuiteIds(catalog, test.Id)
        };

        if (test.Description is not null)
        {
            details["description"] = test.Description;
        }

        if (test.Tags is not null)
        {
            details["tags"] = test.Tags.OrderBy(static tag => tag, StringComparer.Ordinal);
        }

        if (test.Covers is not null)
        {
            details["covers"] = test.Covers
                .OrderBy(static cover => cover.Kind, StringComparer.Ordinal)
                .ThenBy(static cover => cover.Name, StringComparer.Ordinal)
                .Select(static cover => new { kind = cover.Kind, name = cover.Name });
        }

        WriteJson(stdout, new { test = details });
        return CliExitCodes.Success;
    }

    private static int WriteSuiteList(CatalogDocument catalog, TextWriter stdout)
    {
        var suites = (catalog.Suites ?? [])
            .Where(static suite => suite is not null)
            .OrderBy(static suite => suite.Id, StringComparer.Ordinal)
            .Select(static suite => new { id = suite.Id })
            .ToArray();

        WriteJson(stdout, new { suites });
        return CliExitCodes.Success;
    }

    private static int WriteSuite(
        CatalogDocument catalog,
        string id,
        TextWriter stdout)
    {
        CatalogSuite? suite = CatalogNavigator.FindSuite(catalog, id);
        if (suite is null)
        {
            WriteError(
                stdout,
                "SUITE_NOT_FOUND",
                [new CatalogIssue("SUITE_NOT_FOUND", $"Suite was not found: {id}.", "id")]);
            return CliExitCodes.NotFound;
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = suite.Id,
            ["tests"] = (suite.Tests ?? []).OrderBy(static value => value, StringComparer.Ordinal),
            ["suites"] = (suite.Suites ?? []).OrderBy(static value => value, StringComparer.Ordinal),
            ["resolvedTests"] = CatalogNavigator
                .ResolvedTestIds(catalog, suite.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
        };

        if (suite.Description is not null)
        {
            details["description"] = suite.Description;
        }

        WriteJson(stdout, new { suite = details });
        return CliExitCodes.Success;
    }

    private static int WriteValidation(
        CatalogDocument catalog,
        CatalogValidationResult validation,
        TextWriter stdout)
    {
        WriteJson(
            stdout,
            new
            {
                valid = true,
                tests = (catalog.Tests ?? []).Count,
                suites = (catalog.Suites ?? []).Count,
                recipeVerification = validation.RecipesVerified ? "checked" : "skipped"
            });
        return CliExitCodes.Success;
    }

    private static void WriteJson(TextWriter stdout, object value)
    {
        stdout.WriteLine(CatalogJsonFacade.Serialize(value));
    }

    private static int WriteRimTestInvalid(
        string testId,
        string errorCode,
        long started,
        TextWriter stdout,
        int invalidExitCode = CliExitCodes.InvalidInput,
        string? workflowId = null)
    {
        WriteJson(
            stdout,
            RimTestResultFactory.Invalid(
                testId,
                errorCode,
                ElapsedMilliseconds(started),
                workflowId));
        return invalidExitCode;
    }

    private static string FirstErrorCode(
        IReadOnlyList<CatalogIssue> errors,
        string fallback)
    {
        return errors.FirstOrDefault()?.Code ?? fallback;
    }

    private static long ElapsedMilliseconds(long started)
    {
        return Math.Max(
            0,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void AnnotateExit(Activity? activity, int exitCode)
    {
        ProfilerActivity.SetOutcome(
            activity,
            exitCode == CliExitCodes.Cancelled
                ? "cancelled"
                : exitCode == CliExitCodes.Success
                    ? "success"
                    : "failure",
            exitCode is 0 or CliExitCodes.Cancelled
                ? null
                : "CLI_EXIT_" + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AnnotateSuiteExecution(
        Activity? activity,
        CatalogSuiteExecutionResult execution)
    {
        ProfilerActivity.SetOutcome(
            activity,
            execution.Cancelled
                ? "cancelled"
                : execution.Tests.Any(static test =>
                    test.Status is "fail" or "infrastructure" or "invalid")
                    ? "failure"
                    : "success");
        ProfilerActivity.SetCounts(activity, items: execution.Tests.Count);
        foreach (RimTestResult test in execution.Tests)
        {
            ProfilerActivity.SetGeneration(activity, test.Generation);
            if (test.Generation.HasValue)
            {
                break;
            }
        }
    }

    private static bool TryGetRunTestId(
        IReadOnlyList<string> args,
        out string testId)
    {
        var positionals = new List<string>();
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("-", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            if (OptionTakesValue(argument) && index + 1 < args.Count)
            {
                index++;
            }
        }

        if (positionals.Count == 2 &&
            string.Equals(positionals[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            testId = positionals[1];
            return true;
        }

        testId = string.Empty;
        return false;
    }

    private static bool OptionTakesValue(string argument) => argument switch
    {
        "--catalog" or
        "--recipes" or
        "--devbridge" or
        "--devbridge-root" or
        "--devbridge-project" or
        "--rimerror" or
        "--rimerror-log" or
        "--rimerror-store" or
        "--rimcontext" or
        "--rimcontext-root" or
        "--rimcontext-store" or
        "--fallback-suite" or
        "--depth" or
        "--limit" or
        "--query" or
        "--category" or
        "--provider" or
        "--provider-id" or
        "--source" or
        "--target" or
        "--cell-rect" or
        "--viewport" or
        "--viewport-width" or
        "--viewport-height" or
        "--lease" or
        "--base" => true,
        _ => false
    };

    private static IDevBridgeCapabilityAdapter CreateCapabilityAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeCapabilityAdapter? capabilityAdapter)
    {
        if (capabilityAdapter is not null)
        {
            return capabilityAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeCapabilityAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeUiAdapter CreateUiAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeUiAdapter? uiAdapter)
    {
        if (uiAdapter is not null)
        {
            return uiAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeUiAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeViewportAdapter CreateViewportAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeViewportAdapter? viewportAdapter)
    {
        if (viewportAdapter is not null)
        {
            return viewportAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeViewportAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeLeaseAdapter CreateLeaseAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeLeaseAdapter? leaseAdapter)
    {
        if (leaseAdapter is not null)
        {
            return leaseAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        return new DevBridgeLeaseAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IDevBridgeRecipeAdapter CreateAdapter(
        CliRequest request,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IDevBridgeProcessTransport? processTransport = null,
        string? recipeFilePath = null)
    {
        if (recipeAdapter is not null)
        {
            return recipeAdapter;
        }

        DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath) with
        {
            RecipeFilePath = recipeFilePath
        };
        return new DevBridgeRecipeAdapter(
            processTransport ?? new SystemDevBridgeProcessTransport(),
            options);
    }

    private static IRimErrorDiagnosisAdapter CreateRimErrorAdapter(
        CliRequest request,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeProcessTransport? processTransport)
    {
        if (diagnosisAdapter is not null)
        {
            return diagnosisAdapter;
        }

        RimErrorAdapterOptions options = RimErrorAdapterOptions.Discover(
            request.RimErrorPath,
            request.RimErrorLogPath,
            request.RimErrorStorePath);
        return new RimErrorDiagnosisAdapter(options);
    }

    private static IRimContextImpactAdapter CreateRimContextAdapter(
        CliRequest request)
    {
        RimContextAdapterOptions options = RimContextAdapterOptions.Discover(
            request.RimContextPath,
            request.RimContextRootPath,
            request.RimContextStorePath,
            request.RimContextDepth,
            request.RimContextLimit);
        return new RimContextImpactAdapter(options);
    }


    private static CatalogTestExecutionService CreateTestExecutor(
        CliRequest request,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IDevBridgeProcessTransport? processTransport,
        IDevBridgeCapabilityAdapter? providedCapabilityAdapter = null)
    {
        IDevBridgeDiagnosticSourceAdapter? selectedSource = diagnosticSourceAdapter;
        if (selectedSource is null && diagnosisAdapter is null)
        {
            RimErrorAdapterOptions rimErrorOptions = RimErrorAdapterOptions.Discover(
                request.RimErrorPath,
                request.RimErrorLogPath,
                request.RimErrorStorePath);
            if (!rimErrorOptions.IsConfigured)
            {
                DevBridgeAdapterOptions devBridgeOptions = DevBridgeAdapterOptions.Discover(
                    request.DevBridgePath,
                    request.DevBridgeRootPath);
                selectedSource = new DevBridgeDiagnosticSourceAdapter(
                    processTransport ?? new SystemDevBridgeProcessTransport(),
                    devBridgeOptions);
            }
        }

        IDevBridgeCapabilityAdapter? capabilityAdapter = providedCapabilityAdapter;
        if (capabilityAdapter is null &&
            (recipeAdapter is not null || processTransport is not null || diagnosisAdapter is null))
        {
            DevBridgeAdapterOptions capabilityOptions = DevBridgeAdapterOptions.Discover(
                request.DevBridgePath,
                request.DevBridgeRootPath);
            capabilityAdapter = new DevBridgeCapabilityAdapter(
                processTransport ?? new SystemDevBridgeProcessTransport(),
                capabilityOptions);
        }

        ArgumentNullException.ThrowIfNull(recipeAdapter);
        return new CatalogTestExecutionService(
            recipeAdapter,
            () => CreateRimErrorAdapter(request, diagnosisAdapter, processTransport),
            selectedSource is null ? null : () => selectedSource,
            capabilityAdapter);
    }

    private static async Task<int> RunSuiteAsync(
        CatalogDocument catalog,
        string suiteId,
        IReadOnlyList<string> testIds,
        CliRequest request,
        TextWriter stdout,
        IDevBridgeRecipeAdapter? recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        IDevBridgeDiagnosticSourceAdapter? diagnosticSourceAdapter,
        IDevBridgeProcessTransport? processTransport,
        long started,
        CancellationToken cancellationToken,
        string? selectionStatus = null,
        string? selectionErrorCode = null,
        string? fallbackSuite = null,
        string? workflowId = null,
        IDevBridgeModDevelopmentAdapter? developmentAdapter = null,
        ArtifactFreshnessTransactionRequest? freshnessRequest = null,
        RimTestPrerequisiteRecovery? selectionRecovery = null,
        IReadOnlyList<string>? validationChangedPaths = null,
        bool protectRepositoryWorktree = false,
        IDevBridgeFreshGenerationAdapter? freshGenerationRecoveryAdapter = null,
        IDevBridgeCapabilityAdapter? providedCapabilityAdapter = null,
        ContentIntelligenceCapture? contentCapture = null,
        ValidationPlan? validationPlan = null,
        ImpactGraph? impactGraph = null,
        bool singleTestOutput = false,
        string? transactionConsumerPath = null)
    {
        string[] validationRecipeIds = testIds
            .Select(testId => catalog.Tests.FirstOrDefault(test => test.Id == testId)?.Recipe)
            .Where(recipe => !string.IsNullOrWhiteSpace(recipe))
            .Select(recipe => recipe!)
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        if (validationPlan is not null)
        {
            AgentImpactObservabilityRecorder.RecordValidationStarted(
                validationPlan,
                testIds,
                workflowId,
                validationRecipeIds);
        }
        bool ownsRecipeAdapter = recipeAdapter is null;
        bool needsBridgeTransport = ownsRecipeAdapter || freshnessRequest is not null;
        DevBridgeAdapterOptions? bridgeOptions = needsBridgeTransport
            ? DevBridgeAdapterOptions.Discover(
                request.DevBridgePath,
                request.DevBridgeRootPath)
            : null;
        IDevBridgeProcessTransport? bridgeTransport = needsBridgeTransport
            ? processTransport ?? new SystemDevBridgeProcessTransport()
            : null;
        IDevBridgeRecipeAdapter adapter = CreateAdapter(
            request,
            recipeAdapter,
            bridgeTransport,
            ProjectRecipeFilePath(request, validationRecipeIds));
        var executor = CreateTestExecutor(
            request,
            adapter,
            diagnosisAdapter,
            diagnosticSourceAdapter,
            processTransport,
            providedCapabilityAdapter);
        IDevBridgeLeaseAdapter? leaseAdapter = needsBridgeTransport &&
            bridgeOptions is not null && bridgeTransport is not null
            ? new DevBridgeLeaseAdapter(bridgeTransport, bridgeOptions)
            : null;
        IDevBridgeFixtureResetAdapter? resetAdapter = adapter as IDevBridgeFixtureResetAdapter;
        IDevBridgeFreshGenerationAdapter? freshGenerationAdapter =
            freshGenerationRecoveryAdapter ??
            ((ownsRecipeAdapter || freshnessRequest is not null) &&
             bridgeOptions is not null && bridgeTransport is not null
                ? new DevBridgeFreshGenerationAdapter(adapter, bridgeTransport, bridgeOptions)
                : null);
        var runner = new CatalogSuiteRunner(
            adapter,
            executor,
            leaseAdapter,
            resetAdapter,
            freshGenerationAdapter);
        ArtifactFreshnessTransactionResult? freshnessTransaction = null;
        CatalogSuiteExecutionResult execution;
        if (freshnessRequest is not null)
        {
            IDevBridgeModDevelopmentAdapter owner = developmentAdapter ??
                CreateDevelopmentAdapter(request, bridgeTransport, freshnessRequest, transactionConsumerPath);
            freshnessTransaction = await ProfilerActivity.ObserveAsync(
                    "artifact-freshness.transaction",
                    "build-deploy",
                    () => new ArtifactFreshnessTransaction(
                            owner,
                            leaseAdapter,
                            freshGenerationAdapter,
                            protectRepositoryWorktree
                                ? new SystemGitRepositoryStateProvider()
                                : null,
                            recoveryTransport: bridgeTransport,
                            recoveryOptions: bridgeOptions)
                        .PrepareAsync(freshnessRequest, cancellationToken),
                    (activity, value) =>
                    {
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Status.Outcome == DevBridgeOutcomeKind.Cancelled
                                ? "cancelled"
                                : value.Success
                                    ? "success"
                                    : "failure",
                            value.Status.ErrorCode);
                        ProfilerActivity.SetGeneration(activity, value.Freshness.Generation);
                        ProfilerActivity.SetStateChanged(
                            activity,
                            value.Freshness.DeploymentDecision switch
                            {
                                "deployed" => true,
                                "unchanged" => false,
                                _ => null
                            });
                        ProfilerActivity.SetCounts(
                            activity,
                            items: freshnessRequest.ChangedPaths.Count);
                    },
                    phase: "freshness",
                    scope: freshnessRequest.Project)
                .ConfigureAwait(false);
            RimTestPrerequisiteRecovery? promotedRecovery =
                freshnessTransaction.RecoveryEvents?.LastOrDefault(eventRecord =>
                    eventRecord.Component == "promoted-production-toolchain");
            if (promotedRecovery is not null)
            {
                bool recovered = promotedRecovery.State == "recovered";
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Analysis,
                    AgentEventTypes.ToolFailed,
                    recovered
                        ? "Recovered tooling issue: promoted production package repaired."
                        : "Promoted production-toolchain recovery failed.",
                    new
                    {
                        operationKey = "suite:" + suiteId,
                        issueKind = "TOOLING_FAILURE",
                        blocking = !recovered,
                        projectImplicated = false,
                        recovered,
                        componentOwner = "RimLiaison",
                        errorCode = promotedRecovery.OriginalFault ?? promotedRecovery.ErrorCode,
                        originalFault = promotedRecovery.OriginalFault,
                        affectedArtifacts = promotedRecovery.AffectedArtifacts,
                        repairAttempted = true,
                        repairResult = promotedRecovery.RepairResult,
                        verificationResult = promotedRecovery.VerificationResult,
                        recoveryAction = promotedRecovery.Action,
                        recoveryAttempts = promotedRecovery.Attempts,
                        promotedSourceCommit = promotedRecovery.PromotedSourceCommit,
                        currentSourceDiverged = promotedRecovery.CurrentSourceDiverged,
                        recoveryPayloadPath = promotedRecovery.RecoveryPayloadPath,
                        recoveryDurationMs = promotedRecovery.ElapsedRecoveryMilliseconds,
                        retryResult = promotedRecovery.RetryResult,
                        productionImpact = "production-toolchain-integrity"
                    });
                AgentObservabilityRuntime.Record(
                    DevelopmentStage.Analysis,
                    AgentEventTypes.RecoveryCompleted,
                    recovered
                        ? "Promoted production-toolchain recovery completed."
                        : "Promoted production-toolchain recovery exhausted.",
                    new
                    {
                        operationKey = "suite:" + suiteId,
                        recovered,
                        projectImplicated = false,
                        componentOwner = "RimLiaison",
                        errorCode = promotedRecovery.OriginalFault ?? promotedRecovery.ErrorCode,
                        originalFault = promotedRecovery.OriginalFault,
                        expectedPromotedFingerprint = promotedRecovery.ExpectedPromotedFingerprint,
                        affectedArtifacts = promotedRecovery.AffectedArtifacts,
                        promotedSourceCommit = promotedRecovery.PromotedSourceCommit,
                        currentSourceDiverged = promotedRecovery.CurrentSourceDiverged,
                        recoveryPayloadPath = promotedRecovery.RecoveryPayloadPath,
                        repairResult = promotedRecovery.RepairResult,
                        verificationResult = promotedRecovery.VerificationResult,
                        retryCount = promotedRecovery.RetryResult == "not-attempted" ? 0 : 1,
                        retryResult = promotedRecovery.RetryResult,
                        durationMs = promotedRecovery.ElapsedRecoveryMilliseconds
                    });
            }
            execution = freshnessTransaction.Success
                ? await ProfilerActivity.ObserveAsync(
                        "test-suite",
                        "testing",
                        () => runner.RunAsync(
                            catalog,
                            suiteId,
                            testIds,
                            cancellationToken,
                            workflowId,
                            failFast: request.FailFast,
                            sourceFingerprint: freshnessTransaction.Freshness.SourceFingerprint),
                        AnnotateSuiteExecution,
                        phase: "suite",
                        target: suiteId,
                        scope: "suite")
                    .ConfigureAwait(false)
                : ArtifactFailureExecution(
                    suiteId,
                    testIds,
                    freshnessTransaction.Status,
                    workflowId,
                    request.FailFast,
                    freshnessTransaction.Cleanup);
        }
        else
        {
            execution = await ProfilerActivity.ObserveAsync(
                    "test-suite",
                    "testing",
                    () => runner.RunAsync(
                        catalog,
                        suiteId,
                        testIds,
                        cancellationToken,
                        workflowId,
                        failFast: request.FailFast),
                    AnnotateSuiteExecution,
                    phase: "suite",
                    target: suiteId,
                    scope: "suite")
                .ConfigureAwait(false);
        }

        if (freshnessTransaction?.RecoveryEvents is { Count: > 0 })
        {
            execution = execution with
            {
                PrerequisiteRecovery = (execution.PrerequisiteRecovery ?? [])
                    .Concat(freshnessTransaction.RecoveryEvents)
                    .ToArray()
            };
        }
        else if (freshnessTransaction is not null &&
            (freshnessTransaction.Status.RecoveryState != PrerequisiteRecoveryState.Ready ||
             freshnessTransaction.Status.RecoveryAttempts > 0))
        {
            RimTestPrerequisiteRecovery recovery =
                PrerequisiteRecoveryProjection.FromStatus(
                    "artifact-freshness",
                    freshnessTransaction.Status,
                    freshnessTransaction.Freshness.WorkflowId,
                    freshnessTransaction.Freshness.Generation);
            execution = execution with
            {
                PrerequisiteRecovery = (execution.PrerequisiteRecovery ?? [])
                    .Append(recovery)
                    .ToArray()
            };
        }

        if (selectionRecovery is not null)
        {
            execution = execution with
            {
                PrerequisiteRecovery = (execution.PrerequisiteRecovery ?? [])
                    .Append(selectionRecovery)
                    .ToArray()
            };
        }

        if (freshnessTransaction?.Cleanup is not null)
        {
            execution = execution with
            {
                Cleanup = MergeCleanup(execution.Cleanup, freshnessTransaction.Cleanup)
            };
        }

        RimTestArtifactFreshness? artifactFreshness = freshnessTransaction?.Freshness;
        if (freshnessTransaction?.Success == true &&
            artifactFreshness is not null)
        {
            (execution, artifactFreshness) = EnforceArtifactGeneration(
                execution,
                artifactFreshness,
                workflowId,
                catalog,
                testIds);
        }

        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            execution,
            ElapsedMilliseconds(started),
            selectionStatus,
            selectionErrorCode,
            fallbackSuite,
            workflowId,
            artifactFreshness,
            freshnessTransaction?.Status,
            freshnessRequest is not null,
            request.Explain ? validationPlan : null,
            testIds);
        RecordSuiteCompletion(
            execution,
            result,
            testIds,
            selectionStatus,
            selectionErrorCode,
            fallbackSuite,
            workflowId);
        RecordValidationEvidence(
            result,
            testIds,
            request,
            freshnessRequest,
            validationChangedPaths);
        if (validationPlan is not null)
        {
            AgentImpactObservabilityRecorder.RecordValidationCompleted(
                validationPlan,
                result,
                testIds,
                validationRecipeIds);
            AgentImpactObservabilityRecorder.RecordRuntimeEvidenceCompleted(
                validationPlan,
                result);
            RimTestOrchestrationFailure? orchestrationFailure = result.Orchestration?.Failure;
            if (result.BlockedTestCount > 0 && orchestrationFailure is not null)
            {
                string project = orchestrationFailure.AffectedProject ??
                    validationPlan.SourceIdentity.Project ??
                    "Frontier";
                global::RimDev.Contracts.EntityReference[] blockedValidations = result.BlockedTests!
                    .Select(static blocked => new global::RimDev.Contracts.EntityReference
                    {
                        Kind = global::RimDev.Contracts.EntityReferenceKinds.Test,
                        Id = blocked.Test
                    })
                    .ToArray();
                AgentImpactObservabilityRecorder.RecordFailurePacket(
                    new global::RimDev.Contracts.FailureEvidencePacket
                    {
                        Identity = new global::RimDev.Contracts.ExecutionIdentity
                        {
                            RepositoryId = validationPlan.SourceIdentity.Repository,
                            ProjectId = validationPlan.SourceIdentity.Project,
                            SourceRevision = validationPlan.SourceIdentity.SourceRevision,
                            BuildIdentity = validationPlan.SourceIdentity.IndexGeneration,
                            ExecutionId = result.WorkflowId
                        },
                        FailedValidation = new global::RimDev.Contracts.EntityReference
                        {
                            Kind = global::RimDev.Contracts.EntityReferenceKinds.BuildArtifact,
                            Id = "build:" + project
                        },
                        Classification = orchestrationFailure.ErrorCode,
                        Error = orchestrationFailure.Summary ??
                            orchestrationFailure.Error ??
                            orchestrationFailure.ErrorCode,
                        FailureSummary = orchestrationFailure.Summary,
                        ReportingTool = orchestrationFailure.ReportingTool,
                        CausalComponent = orchestrationFailure.CausalComponent,
                        AffectedProject = orchestrationFailure.AffectedProject ?? project,
                        AffectedModIds = orchestrationFailure.AffectedModIds ??
                            [project],
                        FailureSurface = orchestrationFailure.FailureSurface,
                        Orchestrator = orchestrationFailure.Orchestrator,
                        UnderlyingErrorCode = orchestrationFailure.UnderlyingErrorCode,
                        CausalIssueKey = "project:" + project + "|cause:" +
                            (orchestrationFailure.UnderlyingErrorCode ??
                                orchestrationFailure.ErrorCode),
                        CausalChain = (orchestrationFailure.CausalChain ?? [])
                            .Select(link => new global::RimDev.Contracts.FailureCausalReference(
                                link.Role,
                                link.Component,
                                link.Entity))
                            .ToArray(),
                        ChangedSourceFiles = validationPlan.ActualChangedFiles,
                        AffectedEntities = string.IsNullOrWhiteSpace(project)
                            ? []
                            : [new global::RimDev.Contracts.EntityReference
                            {
                                Kind = global::RimDev.Contracts.EntityReferenceKinds.Mod,
                                Id = project
                            }],
                        BlockedValidations = blockedValidations,
                        PrecedingEvidence = string.IsNullOrWhiteSpace(
                                orchestrationFailure.EvidenceId)
                            ? []
                            :
                            [
                                new global::RimDev.Contracts.EvidenceReference
                                {
                                    Kind = "validation",
                                    Uri = orchestrationFailure.EvidenceId
                                }
                            ]
                    });
            }

            foreach (RimTestSuiteFailure failure in (result.Failures ?? [])
                         .Where(static failure => failure.Status is null)
                         .Take(16))
            {
                AgentImpactObservabilityRecorder.RecordFailurePacket(
                    new global::RimDev.Contracts.FailureEvidencePacket
                    {
                        Identity = new global::RimDev.Contracts.ExecutionIdentity
                        {
                            RepositoryId = validationPlan.SourceIdentity.Repository,
                            ProjectId = validationPlan.SourceIdentity.Project,
                            SourceRevision = validationPlan.SourceIdentity.SourceRevision,
                            BuildIdentity = validationPlan.SourceIdentity.IndexGeneration,
                            ExecutionId = result.WorkflowId
                        },
                        FailedValidation = new global::RimDev.Contracts.EntityReference
                        {
                            Kind = global::RimDev.Contracts.EntityReferenceKinds.Test,
                            Id = failure.Test
                        },
                        Classification = failure.ErrorCode ?? "validation-failed",
                        Error = failure.ErrorCode ?? "validation failed",
                        ChangedSourceFiles = validationPlan.ActualChangedFiles,
                        PrecedingEvidence = string.IsNullOrWhiteSpace(failure.EvidenceId)
                            ? []
                            :
                            [
                                new global::RimDev.Contracts.EvidenceReference
                                {
                                    Kind = "validation",
                                    Uri = failure.EvidenceId
                                }
                            ]
                    });
            }
        }
        contentCapture?.RecordEvidence(result);
        MinimumSafeValidationCoordinator.LearnFromOutcome(
            validationPlan,
            impactGraph,
            result,
            AffectedGitRoot(request));
        if (singleTestOutput)
        {
            RimTestResult single = execution.Tests.FirstOrDefault() ??
                RimTestResultFactory.Invalid(
                    testIds.FirstOrDefault() ?? suiteId,
                    "RIMTEST_SINGLE_RESULT_MISSING",
                    workflowId: workflowId);
            WriteJson(stdout, single);
            return SingleTestExitCodeFor(single.Status, single.ErrorCode);
        }
        WriteJson(stdout, result);
        return SuiteExitCodeFor(result.Status);
    }

    private static string? ProjectRecipeFilePath(
        CliRequest request,
        IReadOnlyList<string> recipeIds)
    {
        RimDevStackManifest? manifest = request.StackManifest.Manifest;
        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.TestRecipePath) ||
            string.IsNullOrWhiteSpace(manifest.TestRecipe) ||
            recipeIds.Count != 1 ||
            !string.Equals(recipeIds[0], manifest.TestRecipe, StringComparison.Ordinal))
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(
            request.StackManifest.RepositoryRoot,
            manifest.TestRecipePath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(path) ? path : null;
    }

    private static void RecordSuiteCompletion(
        CatalogSuiteExecutionResult execution,
        RimTestSuiteResult result,
        IReadOnlyList<string> selectedTestIds,
        string? selectionStatus,
        string? selectionErrorCode,
        string? fallbackSuite,
        string? workflowId)
    {
        string[] executedTests = execution.Tests
            .Where(static test => test.Status is "pass" or "fail")
            .Select(static test => test.Test)
            .Where(static test => !string.IsNullOrWhiteSpace(test))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static test => test, StringComparer.Ordinal)
            .ToArray();
        string[] selectedTests = selectedTestIds
            .Where(static test => !string.IsNullOrWhiteSpace(test))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static test => test, StringComparer.Ordinal)
            .ToArray();
        string[] blockedTests = execution.Tests
            .Where(static test => test.Status == "blocked")
            .Select(static test => test.Test)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static test => test, StringComparer.Ordinal)
            .ToArray();
        string[] skippedTests = selectedTests
            .Except(executedTests, StringComparer.Ordinal)
            .Except(blockedTests, StringComparer.Ordinal)
            .ToArray();
        RimTestArtifactFreshness? freshness = result.ArtifactFreshness;
        RimTestOrchestrationFailure? failure = result.Orchestration?.Failure;
        AgentObservabilityRuntime.Record(
            DevelopmentStage.Testing,
            AgentEventTypes.SuiteCompleted,
            "RimTest suite result recorded.",
            new
            {
                operationKey = "suite:" + execution.SuiteId,
                suiteId = execution.SuiteId,
                selectedSuites = new[] { execution.SuiteId },
                selectedTestCount = selectedTests.Length,
                executedTestCount = executedTests.Length,
                blockedTests,
                blockedTestCount = blockedTests.Length,
                failedTestCount = result.FailedTestCount,
                infrastructureFailureCount = result.InfrastructureFailureCount,
                executedSuites = executedTests.Length > 0 ? new[] { execution.SuiteId } : Array.Empty<string>(),
                reusedSuites = execution.Reuse?.GroupsUsed > 0 ? new[] { execution.SuiteId } : Array.Empty<string>(),
                skippedSuites = execution.Skipped > 0 ? new[] { execution.SuiteId } : Array.Empty<string>(),
                selectedTests,
                executedTests,
                reusedTests = Array.Empty<string>(),
                skippedTests,
                skippedTestCount = execution.Skipped,
                result = result.Status,
                status = result.Status,
                durationMs = result.DurationMs,
                passed = result.Passed,
                failed = result.Failed,
                cancelled = result.Cancelled,
                selectionStatus,
                selectionErrorCode,
                fallbackSuite,
                workflowId,
                reuseStatus = execution.Reuse?.Status,
                reuseInvalidationReason = execution.Reuse?.ReuseInvalidationReason,
                reuseFallbackReason = execution.Reuse?.FallbackReason,
                reuseGroupsUsed = execution.Reuse?.GroupsUsed,
                reuseGenerationsUsed = execution.Reuse?.GenerationsUsed,
                artifactFreshness = freshness is null
                    ? null
                    : new
                    {
                        sourceFingerprint = freshness.SourceFingerprint,
                        builtArtifactSha256 = freshness.BuiltArtifactSha256,
                        deployedArtifactSha256 = freshness.DeployedArtifactSha256,
                        deploymentDecision = freshness.DeploymentDecision,
                        evaluationStatus = freshness.EvaluationStatus,
                        generation = freshness.Generation,
                        generationBefore = freshness.GenerationBefore,
                        generationAfter = freshness.GenerationAfter,
                        transactionId = freshness.TransactionId,
                        workflowId = freshness.WorkflowId,
                        leaseId = freshness.LeaseId,
                        evidenceId = freshness.Proof,
                        errorCode = freshness.ErrorCode,
                        underlyingErrorCode = freshness.UnderlyingErrorCode,
                        project = freshness.Project,
                        orchestrator = freshness.Orchestrator,
                        failureSurface = freshness.FailureSurface,
                        likelyOwner = freshness.LikelyOwner,
                        ownershipConfidence = freshness.OwnershipConfidence,
                        ownershipBasis = freshness.OwnershipBasis,
                        causalDiagnostic = freshness.CausalDiagnostic,
                        failureMessage = freshness.FailureMessage,
                        loadedArtifactFreshnessProven = freshness.LoadedArtifactFreshnessProven
                    },
                overall = result.Orchestration?.Overall,
                agentOutcome = result.Orchestration?.AgentOutcome,
                toolchainRecoveryCount = result.Orchestration?.ToolchainRecoveryCount,
                toolchainRecoveryTypes = result.Orchestration?.ToolchainRecoveryTypes,
                lastSafeCheckpoint = result.Orchestration?.LastSafeCheckpoint,
                sourceBuild = result.Orchestration?.SourceBuild,
                staticTests = result.Orchestration?.StaticTests,
                deployment = result.Orchestration?.Deployment,
                runtimeValidation = result.Orchestration?.RuntimeValidation,
                infrastructure = result.Orchestration?.Infrastructure,
                failureKind = failure?.Stage ?? (result.Status switch
                {
                    "fail" => "test",
                    "infrastructure" => "infrastructure",
                    "cancelled" => "cancelled",
                    _ => null
                }),
                owner = failure?.Owner,
                errorCode = failure?.ErrorCode,
                nextAction = failure?.NextAction,
                retryable = failure?.RetrySafe,
                classification = failure?.Classification,
                recoveryAttempted = failure?.RecoveryAttempted,
                recoveryResult = failure?.RecoveryResult,
                retrySafe = failure?.RetrySafe,
                failureSummary = failure?.Summary,
                reportingTool = failure?.ReportingTool,
                causalComponent = failure?.CausalComponent,
                affectedProject = failure?.AffectedProject,
                affectedModIds = failure?.AffectedModIds,
                failureSurface = failure?.FailureSurface,
                orchestrator = failure?.Orchestrator,
                underlyingErrorCode = failure?.UnderlyingErrorCode,
                infrastructureFailure = result.Status == "infrastructure" ||
                    result.Orchestration?.Overall is "SOURCE_BUILD_FAILURE" or "INFRASTRUCTURE_FAILURE"
            });
    }

    private static ArtifactFreshnessTransactionRequest? CreateArtifactFreshnessRequest(
        CliRequest request,
        CatalogDocument catalog,
        IReadOnlyList<string> selectedTestIds,
        IReadOnlyList<string> changedPaths,
        string? workflowId)
    {
        if (!SourceChangeClassifier.IsBuildRelevant(changedPaths) ||
            IsRimLiaisonRepository(request))
        {
            return null;
        }

        string sourceFingerprint = string.Empty;
        WorktreeFingerprint.TryCompute(
            AffectedGitRoot(request),
            changedPaths,
            out sourceFingerprint,
            out _);
        return new ArtifactFreshnessTransactionRequest(
            request.DevBridgeProject ?? string.Empty,
            AffectedGitRoot(request),
            changedPaths,
            sourceFingerprint,
            workflowId,
            TestRecipe: SelectDevelopmentRecipe(catalog, selectedTestIds));
    }

    private static bool IsRimLiaisonRepository(CliRequest request)
    {
        if (!string.Equals(
                request.StackManifest.Manifest?.Project,
                "RimLiaison",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string affectedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AffectedGitRoot(request)));
            string manifestRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(request.StackManifest.RepositoryRoot));
            return string.Equals(
                affectedRoot,
                manifestRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException)
        {
            return true;
        }
    }


    private static IDevBridgeModDevelopmentAdapter CreateDevelopmentAdapter(
        CliRequest request,
        IDevBridgeProcessTransport? processTransport,
        ArtifactFreshnessTransactionRequest? freshnessRequest,
        string? transactionConsumerPath = null)
    {
        DevBridgeAdapterOptions bridgeOptions = DevBridgeAdapterOptions.Discover(
            request.DevBridgePath,
            request.DevBridgeRootPath);
        string repositoryRoot = AffectedGitRoot(request);
        DevBridgeModDevelopmentAdapterOptions modOptions =
            DevBridgeModDevelopmentAdapterOptions.Discover(bridgeOptions.RootPath) with
            {
                ScriptRootPath = bridgeOptions.SourceRootPath,
                TransactionConsumerPath = transactionConsumerPath,
                DeploymentRoot = ResolveProjectRuntimeRoot(request, repositoryRoot),
                ChangedPaths = freshnessRequest?.ChangedPaths,
                TestRecipe = freshnessRequest?.TestRecipe,
                UseInternalTransaction = transactionConsumerPath is not null
            };
        IDevBridgeProcessTransport ownerTransport =
            processTransport ?? new SystemDevBridgeProcessTransport();
        return modOptions.UseInternalTransaction
            ? new InternalDevelopmentTransactionService(ownerTransport, modOptions)
            : new DevBridgeModDevelopmentAdapter(ownerTransport, modOptions);
    }

    private static string? ResolveProjectRuntimeRoot(
        CliRequest request,
        string repositoryRoot)
    {
        RimDevStackManifest? manifest = request.StackManifest.Manifest;
        if (manifest is null)
        {
            return null;
        }

        if (string.Equals(manifest.Workload, "production", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectRuntimeBindingResolver.Resolve(repositoryRoot, manifest).RuntimeRoot;
        }

        return ResolveNonProductionRuntimeRoot(request, repositoryRoot);
    }

    private static string? ResolveNonProductionRuntimeRoot(
        CliRequest request,
        string repositoryRoot)
    {
        RimDevWorkspaceDiscovery workspace =
            RimDevWorkspaceDiscoverer.Discover(null, repositoryRoot);
        RimDevWorkspaceConfiguration? configuration = workspace.Configuration;
        string? project = request.DevBridgeProject ??
            request.StackManifest.Manifest?.DevBridgeProject;
        if (!workspace.Succeeded ||
            configuration?.ActiveModsRoot is null ||
            string.IsNullOrWhiteSpace(project) ||
            configuration.PackageMappings is null ||
            !configuration.PackageMappings.TryGetValue(project, out string? packageName) ||
            string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        try
        {
            string runtimeRoot = Path.GetFullPath(Path.Combine(
                configuration.ActiveModsRoot,
                packageName));
            string activeModsRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(configuration.ActiveModsRoot));
            if (string.Equals(runtimeRoot, activeModsRoot, StringComparison.OrdinalIgnoreCase) ||
                !runtimeRoot.StartsWith(
                    activeModsRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return runtimeRoot;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static string? SelectDevelopmentRecipe(
        CatalogDocument catalog,
        IReadOnlyList<string> selectedTestIds)
    {
        string[] selectedRecipes = selectedTestIds
            .Select(testId => CatalogNavigator.FindTest(catalog, testId))
            .Where(static test => test?.ArtifactFreshnessAnchor == true)
            .Select(static test => test!.Recipe)
            .Where(static recipe => !string.IsNullOrWhiteSpace(recipe))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedRecipes.Length == 1)
        {
            return selectedRecipes[0];
        }

        // The catalog's single freshness anchor is canonical even when the
        // impact map selected a companion test instead of the anchor itself.
        // This keeps descriptor recovery targeted without inventing a second
        // project/recipe registry.
        string[] catalogRecipes = catalog.Tests
            .Where(static test => test.ArtifactFreshnessAnchor)
            .Select(static test => test.Recipe)
            .Where(static recipe => !string.IsNullOrWhiteSpace(recipe))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return catalogRecipes.Length == 1 ? catalogRecipes[0] : null;
    }

    private static RimTestPrerequisiteRecovery? SelectionRecovery(
        RimTestSelectionResult selection)
    {
        if (selection.RecoveryState is null ||
            selection.RecoveryAttempts is null)
        {
            return null;
        }

        return new RimTestPrerequisiteRecovery(
            "rimcontext-index",
            selection.RecoveryState,
            selection.RecoveryAttempts.Value,
            selection.ErrorCode,
            selection.RecoveryAction);
    }

    private static CatalogSuiteExecutionResult ArtifactFailureExecution(
        string suiteId,
        IReadOnlyList<string> testIds,
        DevBridgeAdapterStatus status,
        string? workflowId,
        bool failFast,
        RimTestCleanupSummary? cleanup)
    {
        string[] ordered = testIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (status.Outcome == DevBridgeOutcomeKind.Cancelled)
        {
            return new CatalogSuiteExecutionResult(
                suiteId,
                ordered.Length == 0
                    ? []
                    : [RimTestResultFactory.Cancelled(ordered[0], workflowId: workflowId)],
                Math.Max(0, ordered.Length - 1),
                Cancelled: true,
                FailFast: failFast
                    ? new CatalogSuiteFailFastSummary(
                        null,
                        ordered.Length,
                        false,
                        new CatalogSuiteFailFastOrderingSummary(
                            false,
                            "not-attempted",
                            CatalogSuiteFailFastOrdering.PolicyVersion))
                    : null,
                Cleanup: cleanup);
        }

        string errorCode = status.ErrorCode ?? "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN";
        return new CatalogSuiteExecutionResult(
            suiteId,
            ordered
                .Select(testId => RimTestResultFactory.ArtifactFreshnessFailure(
                    testId,
                    errorCode,
                    workflowId))
                .ToArray(),
            0,
            Cancelled: false,
            FailFast: failFast
                ? new CatalogSuiteFailFastSummary(
                    null,
                    ordered.Length,
                    false,
                    new CatalogSuiteFailFastOrderingSummary(
                        false,
                        "not-attempted",
                        CatalogSuiteFailFastOrdering.PolicyVersion))
                : null,
            Cleanup: cleanup);
    }

    private static RimTestCleanupSummary MergeCleanup(
        RimTestCleanupSummary? first,
        RimTestCleanupSummary second)
    {
        if (first is null)
        {
            return second;
        }

        bool failed = string.Equals(first.Status, "FAILED", StringComparison.Ordinal) ||
            string.Equals(second.Status, "FAILED", StringComparison.Ordinal);
        return new RimTestCleanupSummary
        {
            Status = failed ? "FAILED" : "RESTORED",
            LeaseReleased = first.LeaseReleased == false || second.LeaseReleased == false
                ? false
                : first.LeaseReleased ?? second.LeaseReleased,
            TemporaryStateCleared = first.TemporaryStateCleared == false ||
                    second.TemporaryStateCleared == false
                ? false
                : first.TemporaryStateCleared ?? second.TemporaryStateCleared,
            ErrorCode = first.ErrorCode ?? second.ErrorCode
        };
    }

    private static (
        CatalogSuiteExecutionResult Execution,
        RimTestArtifactFreshness Freshness) EnforceArtifactGeneration(
        CatalogSuiteExecutionResult execution,
        RimTestArtifactFreshness freshness,
        string? workflowId,
        CatalogDocument catalog,
        IReadOnlyList<string> selectedTestIds)
    {
        string[] artifactTestIds = ResolveArtifactFreshnessTestIds(
            catalog,
            selectedTestIds);
        string? artifactTestId = artifactTestIds.Length == 1
            ? artifactTestIds[0]
            : null;
        freshness = freshness with { ArtifactTestId = artifactTestId };

        if (!freshness.Generation.HasValue)
        {
            string[] ids = execution.Tests
                .Where(test => test.Status == "pass" &&
                    artifactTestIds.Contains(test.Test, StringComparer.Ordinal))
                .Select(static test => test.Test)
                .ToArray();
            return (
                ReplacePassingTestsWithFreshnessFailures(
                    execution,
                    ids,
                    "RIMTEST_ARTIFACT_GENERATION_UNKNOWN",
                    workflowId),
                freshness with
                {
                    LoadedArtifactFreshnessProven = false,
                    ErrorCode = "RIMTEST_ARTIFACT_GENERATION_UNKNOWN"
                });
        }

        string[] mismatched = execution.Tests
            .Where(test => test.Status == "pass" &&
                artifactTestIds.Contains(test.Test, StringComparer.Ordinal) &&
                (!test.Generation.HasValue ||
                 test.Generation.Value != freshness.Generation.Value))
            .Select(static test => test.Test)
            .ToArray();
        if (mismatched.Length == 0)
        {
            return (execution, freshness);
        }

        return (
            ReplacePassingTestsWithFreshnessFailures(
                execution,
                mismatched,
                "RIMTEST_ARTIFACT_GENERATION_MISMATCH",
                workflowId),
            freshness with
            {
                LoadedArtifactFreshnessProven = false,
                ErrorCode = "RIMTEST_ARTIFACT_GENERATION_MISMATCH"
            });
    }

    private static string[] ResolveArtifactFreshnessTestIds(
        CatalogDocument catalog,
        IReadOnlyList<string> selectedTestIds)
    {
        string[] explicitAnchors = selectedTestIds
            .Where(testId => CatalogNavigator.FindTest(catalog, testId)
                ?.ArtifactFreshnessAnchor == true)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return explicitAnchors.Length > 0
            ? explicitAnchors
            : selectedTestIds
                .Where(static testId => !string.IsNullOrWhiteSpace(testId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static CatalogSuiteExecutionResult ReplacePassingTestsWithFreshnessFailures(
        CatalogSuiteExecutionResult execution,
        IReadOnlyCollection<string> testIds,
        string errorCode,
        string? workflowId) =>
        execution with
        {
            Tests = execution.Tests
                .Select(test => test.Status == "pass" && testIds.Contains(test.Test)
                    ? RimTestResultFactory.ArtifactFreshnessFailure(
                        test.Test,
                        errorCode,
                        workflowId)
                    : test)
                .ToArray(),
            // Preserve the historical default projection while retaining the
            // reuse summary for an explicit fail-fast run.
            Reuse = execution.FailFast is null ? null : execution.Reuse
        };

    private static int SelectionExitCode(RimTestSelectionResult selection)
    {
        return selection.Status switch
        {
            "ok" => CliExitCodes.Success,
            "invalid" => CliExitCodes.InvalidInput,
            "cancelled" => CliExitCodes.Cancelled,
            "blocked" => CliExitCodes.ConservativeSelection,
            "conservative" when selection.Tests.Count > 0 => CliExitCodes.Success,
            "conservative" => CliExitCodes.ConservativeSelection,
            _ => CliExitCodes.InternalError
        };
    }

    private static int WriteAffectedSelectionFailure(
        RimTestSelectionResult selection,
        long started,
        TextWriter stdout,
        string? workflowId)
    {
        string errorCode = selection.ErrorCode ?? "RIMTEST_AFFECTED_SELECTION_FAILED";
        RimTestPrerequisiteRecovery? recovery = SelectionRecovery(selection);
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromSelectionFailure(
            "affected",
            selection.Status,
            errorCode,
            error: null,
            nextAction: selection.NextAction,
            durationMs: ElapsedMilliseconds(started),
            workflowId: workflowId,
            prerequisiteRecovery: recovery is null ? null : [recovery]);
        WriteJson(stdout, result);
        return SuiteExitCodeFor(result.Status);
    }

    private static int SuiteExitCodeFor(string status) => status switch
    {
        "pass" => CliExitCodes.Success,
        "fail" => CliExitCodes.TestFailure,
        "cancelled" => CliExitCodes.Cancelled,
        "conservative" => CliExitCodes.ConservativeSelection,
        "invalid" => CliExitCodes.InvalidInput,
        _ => CliExitCodes.InternalError
    };

    private static int SingleTestExitCodeFor(string status, string? errorCode) =>
        string.Equals(errorCode, "DEVBRIDGE_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(errorCode, "DEVBRIDGE_CLIENT_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(errorCode, "RIMTEST_TIMEOUT", StringComparison.OrdinalIgnoreCase)
            ? CliExitCodes.Timeout
            : status switch
            {
                "pass" => CliExitCodes.Success,
                "fail" => CliExitCodes.TestFailure,
                "cancelled" => CliExitCodes.Cancelled,
                "blocked" or "conservative" => CliExitCodes.ConservativeSelection,
                "invalid" => CliExitCodes.InvalidInput,
                _ => CliExitCodes.InternalError
            };

    private static void WriteRunResult(
        string testId,
        string recipeId,
        DevBridgeRecipeRunResult result,
        TextWriter stdout,
        string? workflowId = null)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["test"] = testId,
            ["recipe"] = recipeId,
            ["outcome"] = OutcomeName(result.Status.Outcome)
        };
        string? effectiveWorkflowId = result.WorkflowId ?? workflowId;
        if (!string.IsNullOrWhiteSpace(effectiveWorkflowId))
        {
            output["workflowId"] = effectiveWorkflowId;
        }
        if (result.Passed.HasValue)
        {
            output["passed"] = result.Passed.Value;
        }

        if (result.RunId is not null)
        {
            output["runId"] = result.RunId;
        }

        if (result.Generation.HasValue)
        {
            output["generation"] = result.Generation.Value;
        }

        if (result.LeaseId is not null)
        {
            output["leaseId"] = result.LeaseId;
        }

        if (result.Evidence is not null)
        {
            output["evidence"] = result.Evidence;
        }

        if (result.EvidenceId is not null)
        {
            output["evidenceId"] = result.EvidenceId;
        }

        if (result.FailureFingerprint is not null)
        {
            output["failureFingerprint"] = result.FailureFingerprint;
        }

        if (result.FinalNextAction is not null)
        {
            output["finalNextAction"] = result.FinalNextAction;
        }

        string? nextAction = NextActionFor(result.Status.Outcome);
        if (nextAction is not null)
        {
            output["nextAction"] = nextAction;
        }

        if (result.RestartRequired.HasValue)
        {
            output["restartRequired"] = result.RestartRequired.Value;
        }

        if (result.LaunchesConsumed.HasValue)
        {
            output["launchesConsumed"] = result.LaunchesConsumed.Value;
        }

        if (result.Operations.Count > 0)
        {
            output["operations"] = result.Operations;
        }

        AddStatusFields(output, result.Status);
        WriteJson(stdout, output);
    }

    private static void AddStatusFields(
        IDictionary<string, object?> output,
        DevBridgeAdapterStatus status)
    {
        if (status.ErrorCode is not null)
        {
            output["errorCode"] = status.ErrorCode;
        }

        if (status.Error is not null)
        {
            output["error"] = status.Error;
        }

        if (status.ProcessExitCode.HasValue)
        {
            output["processExitCode"] = status.ProcessExitCode.Value;
        }

        if (!string.IsNullOrEmpty(status.Stderr))
        {
            output["stderr"] = status.Stderr;
        }

        if (status.ResponseSchema is not null)
        {
            output["responseSchema"] = status.ResponseSchema;
        }

        if (status.RecoveryState != PrerequisiteRecoveryState.Ready ||
            status.RecoveryAttempts > 0)
        {
            output["recoveryState"] = status.RecoveryState.ToWireName();
            output["recoveryAttempts"] = Math.Max(0, status.RecoveryAttempts);
            if (status.RecoveryAction is not null)
            {
                output["recoveryAction"] = status.RecoveryAction;
            }
        }

        if (status.IdentityMismatch is not null)
        {
            output["identityMismatch"] = status.IdentityMismatch;
        }
        if (status.Response is not null)
        {
            output["state"] = status.Response.State;
            output["nextAction"] = status.Response.NextAction;
            output["protocolVersion"] = status.Response.ProtocolVersion;
            output["buildIdentity"] = status.Response.BuildIdentity;
            output["findings"] = status.Response.Findings;
        }

        if (status.ProcessEvidence is not null)
        {
            output["processEvidence"] = status.ProcessEvidence;
        }
    }

    private static string OutcomeName(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.TestFailure => "testFailure",
            DevBridgeOutcomeKind.DevBridgeRefusal => "devBridgeRefusal",
            DevBridgeOutcomeKind.InfrastructureFailure => "infrastructureFailure",
            DevBridgeOutcomeKind.Timeout => "timeout",
            DevBridgeOutcomeKind.Cancelled => "cancelled",
            DevBridgeOutcomeKind.MalformedResponse => "malformedResponse",
            DevBridgeOutcomeKind.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };
    }

    private static bool IsFailureLifecycleEvent(AgentEvent eventRecord) =>
        eventRecord.Type is AgentEventTypes.ToolFailed or AgentEventTypes.ToolException or
            AgentEventTypes.CommandFailed or AgentEventTypes.CommandTimeout or
            AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed or
            AgentEventTypes.AgentFailed or AgentEventTypes.IntegrationFailed ||
        AgentObservabilityData.GetString(eventRecord.Data, "outcome") is
            "failure" or "timeout" or "cancelled";

    private static bool RequiresProductionToolchainBinding(CliRequest request) =>
        request.Command is CliCommand.RecipeRun or
            CliCommand.RunTest or
            CliCommand.SuiteRun or
            CliCommand.GoldenPath or
            CliCommand.Preflight ||
        (request.Command == CliCommand.Doctor &&
         string.IsNullOrWhiteSpace(request.DevBridgeRootPath)) ||
        request.Command == CliCommand.Affected && request.RunSelected;
    private static bool RequiresProjectBinding(CliRequest request) =>
        string.Equals(
            request.StackManifest.Manifest?.Workload,
            "production",
            StringComparison.OrdinalIgnoreCase) &&
        (RequiresProductionToolchainBinding(request) ||
         request.Command == CliCommand.Preflight);

    private static ProjectRuntimeBindingResult ResolveProjectBinding(CliRequest request)
    {
        if (request.StackManifest.Manifest is null)
        {
            return new(
                false,
                request.DevBridgeProject ?? "unknown",
                request.StackManifest.RepositoryRoot,
                null,
                null,
                null,
                "unknown",
                false,
                false,
                null,
                request.StackManifest.ErrorCode ?? "PROJECT_METADATA_MISSING",
                "A valid production project manifest is required before runtime binding.",
                "repair the project-owned .rimdev/stack.json");
        }

        return ProjectRuntimeBindingResolver.Resolve(
            request.StackManifest.RepositoryRoot,
            request.StackManifest.Manifest);
    }

    private static async Task<QualificationAggregate> AttachQualificationProvenanceAsync(
        QualificationAggregate aggregate,
        string sourceRoot,
        ToolchainCandidate? candidate,
        CancellationToken cancellationToken)
    {
        GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
            .ReadAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (candidate is not null)
        {
            hashes["rimLiaisonExecutableSha256"] = candidate.RimLiaisonExecutableSha256;
            hashes["rimLiaisonAssemblySha256"] = candidate.RimLiaisonAssemblySha256;
            hashes["rimLiaisonCliDeploymentManifestSha256"] =
                candidate.RimLiaisonCliDeploymentManifestSha256 ?? string.Empty;
            hashes["rimLiaisonCliDeploymentPackageSha256"] =
                candidate.RimLiaisonCliDeploymentPackageSha256 ?? string.Empty;
            hashes["devBridgePackageSha256"] = candidate.DevBridgePackageSha256;
            hashes["devBridgeCoordinatorSha256"] = candidate.DevBridgeCoordinatorSha256;
            hashes["devBridgeModSha256"] = candidate.DevBridgeModSha256 ?? string.Empty;
            hashes["devBridgeRuntimeManifestSha256"] = candidate.DevBridgeRuntimeManifestSha256 ?? string.Empty;
        }
        return aggregate with
        {
            SourceCommit = source.State?.HeadSha,
            QualifiedArtifactHashes = hashes
        };
    }

    private static string? ResolveLogicalAgentId()
    {
        string? value = Environment.GetEnvironmentVariable("RIMLIAISON_LOGICAL_AGENT_ID") ??
            Environment.GetEnvironmentVariable("RIMLIAISON_WORKER_ID");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ObservabilityEntityIdentity ResolveObservabilityEntity(
        CliRequest request)
    {
        // Resolve the subject before any nested executor starts. Nested
        // RimContext, RimTest, and DevBridge2 events inherit this session.
        string? explicitRoot = request.Command == CliCommand.RimDev
            ? request.RimDevRootPath
            : request.RimContextRootPath;
        StackManifestResolution target = string.IsNullOrWhiteSpace(explicitRoot)
            ? request.StackManifest
            : StackManifestResolver.Discover(explicitRoot);
        string root = target.RepositoryRoot;

        if (ObservabilityProjectIdentityResolver.IsRimLiaisonRepository(root))
        {
            return ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison");
        }

        bool hasProjectTarget = target.Manifest is not null ||
            File.Exists(Path.Combine(root, "About", "About.xml"));
        if (!hasProjectTarget)
        {
            return ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison");
        }

        ObservabilityProjectIdentity project =
            ObservabilityProjectIdentityResolver.Resolve(
                root,
                target.Manifest?.Project);
        return ObservabilityEntityIdentityResolver.ForMod(project);
    }

    private static (string ModId, string ModName) ResolveObservabilityMod(
        ObservabilityEntityIdentity identity) =>
        (LegacyModId(identity), identity.DisplayName);

    private static string LegacyModId(ObservabilityEntityIdentity identity)
    {
        if (identity.EntityType != ObservabilityEntityTypes.Mod)
        {
            return "RimLiaison";
        }

        const string prefix = "mod:";
        return identity.CanonicalEntityId.StartsWith(prefix, StringComparison.Ordinal)
            ? identity.CanonicalEntityId[prefix.Length..]
            : identity.CanonicalEntityId;
    }

    private static string? TryReadModDisplayName(string repositoryRoot)
    {
        string aboutPath = Path.Combine(repositoryRoot, "About", "About.xml");
        try
        {
            if (!File.Exists(aboutPath) || new FileInfo(aboutPath).Length > 256_000)
            {
                return null;
            }

            XDocument document = XDocument.Load(aboutPath, LoadOptions.None);
            string? name = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))?
                .Value
                .Trim();
            return string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)
                ? null
                : name;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or XmlException)
        {
            return null;
        }
    }

    private static DevelopmentStage ObservabilityStageFor(CliCommand command) =>
        command switch
        {
            CliCommand.RecipeRun or CliCommand.RunTest or CliCommand.SuiteRun or
            CliCommand.Affected or CliCommand.GoldenPath => DevelopmentStage.Testing,
            CliCommand.Capabilities or CliCommand.UiTargets => DevelopmentStage.Research,
            CliCommand.UiScreenshot => DevelopmentStage.Testing,
            CliCommand.Init or CliCommand.Doctor or CliCommand.Preflight or
            CliCommand.Context or CliCommand.PublishCheck or CliCommand.Benchmarks =>
                DevelopmentStage.Analysis,
            _ => DevelopmentStage.Analysis
        };

    private static string? NextActionFor(DevBridgeOutcomeKind outcome) => outcome switch
    {
        DevBridgeOutcomeKind.DevBridgeRefusal or
        DevBridgeOutcomeKind.InfrastructureFailure or
        DevBridgeOutcomeKind.Timeout or
        DevBridgeOutcomeKind.MalformedResponse or
        DevBridgeOutcomeKind.IncompatibleSchema => "DevBridge.cmd doctor --json",
        _ => null
    };

    private static int ExitCodeFor(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.Success => CliExitCodes.Success,
            DevBridgeOutcomeKind.TestFailure => CliExitCodes.TestFailure,
            DevBridgeOutcomeKind.DevBridgeRefusal => CliExitCodes.NotFound,
            DevBridgeOutcomeKind.Timeout => CliExitCodes.Timeout,
            DevBridgeOutcomeKind.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };
    }

    private static string CapabilityOutcomeName(DevBridgeCapabilityOutcome outcome) =>
        outcome switch
        {
            DevBridgeCapabilityOutcome.Unavailable => "unavailable",
            DevBridgeCapabilityOutcome.InfrastructureFailure => "infrastructureFailure",
            DevBridgeCapabilityOutcome.Timeout => "timeout",
            DevBridgeCapabilityOutcome.Cancelled => "cancelled",
            DevBridgeCapabilityOutcome.MalformedResponse => "malformedResponse",
            DevBridgeCapabilityOutcome.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };

    private static int CapabilityExitCodeFor(DevBridgeCapabilityOutcome outcome) =>
        outcome switch
        {
            DevBridgeCapabilityOutcome.Timeout => CliExitCodes.Timeout,
            DevBridgeCapabilityOutcome.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };

    private static string UiOutcomeName(DevBridgeUiOutcome outcome) =>
        outcome switch
        {
            DevBridgeUiOutcome.Unavailable => "unavailable",
            DevBridgeUiOutcome.TargetNotFound => "targetNotFound",
            DevBridgeUiOutcome.VisualReadinessFailure => "visualReadinessFailure",
            DevBridgeUiOutcome.InvalidRequest => "invalidRequest",
            DevBridgeUiOutcome.InfrastructureFailure => "infrastructureFailure",
            DevBridgeUiOutcome.Timeout => "timeout",
            DevBridgeUiOutcome.Cancelled => "cancelled",
            DevBridgeUiOutcome.MalformedResponse => "malformedResponse",
            DevBridgeUiOutcome.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };

    private static int UiExitCodeFor(DevBridgeUiOutcome outcome) =>
        outcome switch
        {
            DevBridgeUiOutcome.InvalidRequest => CliExitCodes.InvalidInput,
            DevBridgeUiOutcome.TargetNotFound => CliExitCodes.NotFound,
            DevBridgeUiOutcome.Timeout => CliExitCodes.Timeout,
            DevBridgeUiOutcome.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };

    private static string ViewportOutcomeName(DevBridgeViewportOutcome outcome) =>
        outcome switch
        {
            DevBridgeViewportOutcome.AlreadyRestored => "alreadyRestored",
            DevBridgeViewportOutcome.Unavailable => "unavailable",
            DevBridgeViewportOutcome.Busy => "busy",
            DevBridgeViewportOutcome.InvalidRequest => "invalidRequest",
            DevBridgeViewportOutcome.Unsupported => "unsupported",
            DevBridgeViewportOutcome.VerificationFailure => "verificationFailure",
            DevBridgeViewportOutcome.RestorationFailure => "restorationFailure",
            DevBridgeViewportOutcome.InfrastructureFailure => "infrastructureFailure",
            DevBridgeViewportOutcome.Timeout => "timeout",
            DevBridgeViewportOutcome.Cancelled => "cancelled",
            DevBridgeViewportOutcome.MalformedResponse => "malformedResponse",
            DevBridgeViewportOutcome.IncompatibleSchema => "incompatibleSchema",
            _ => "success"
        };

    private static int ViewportExitCodeFor(DevBridgeViewportOutcome outcome) =>
        outcome switch
        {
            DevBridgeViewportOutcome.InvalidRequest => CliExitCodes.InvalidInput,
            DevBridgeViewportOutcome.Timeout => CliExitCodes.Timeout,
            DevBridgeViewportOutcome.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };

    private static int LeaseExitCodeFor(DevBridgeOutcomeKind outcome) =>
        outcome switch
        {
            DevBridgeOutcomeKind.Timeout => CliExitCodes.Timeout,
            DevBridgeOutcomeKind.Cancelled => CliExitCodes.Cancelled,
            DevBridgeOutcomeKind.DevBridgeRefusal => CliExitCodes.NotFound,
            _ => CliExitCodes.InternalError
        };

    private static bool IsLeaseRequired(DevBridgeUiStatus status) =>
        string.Equals(status.ErrorCode, "RIMBRIDGE_LEASE_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
        status.Error?.Contains("lease", StringComparison.OrdinalIgnoreCase) == true;

    private static int RimTestExitCodeFor(DevBridgeOutcomeKind outcome)
    {
        return outcome switch
        {
            DevBridgeOutcomeKind.Success => CliExitCodes.Success,
            DevBridgeOutcomeKind.TestFailure => CliExitCodes.TestFailure,
            DevBridgeOutcomeKind.Timeout => CliExitCodes.Timeout,
            DevBridgeOutcomeKind.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.InternalError
        };
    }

    private static void WriteError(
        TextWriter stdout,
        string code,
        IReadOnlyList<CatalogIssue> errors)
    {
        WriteJson(stdout, new { status = "error", code, errors });
    }
}
