using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimContext.Core.Context;
using RimContext.Core.Model;
using RimLiaison.Catalog;
using RimLiaison.Benchmarking;
using RimLiaison.DevBridge;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Provenance;
using RimLiaison.RimError;
using RimLiaison.Stack;
using RimError.Core;

namespace RimLiaison.RimContext;

public sealed record RimLiaisonContextProviderOptions
{
    public required string RootPath { get; init; }
    public required string CatalogPath { get; init; }
    public string? Project { get; init; }
    public string? ObservabilityModName { get; init; }
    public string? RimContextStorePath { get; init; }
    public string? DevBridgePath { get; init; }
    public string? DevBridgeRootPath { get; init; }
    public string? DevBridgeProject { get; init; }
    public string? RimErrorPath { get; init; }
    public string? RimErrorLogPath { get; init; }
    public string? RimErrorStorePath { get; init; }
    public string? FallbackSuite { get; init; }
    public string? StackManifestPath { get; init; }
    public string? ObservabilityModId { get; init; }
    public IReadOnlyDictionary<string, string>? RelatedRepositoryRoots { get; init; }
    public IGitRepositoryStateProvider? GitProvider { get; init; }
    public IDevBridgeProcessTransport? ProcessTransport { get; init; }
    public IAgentObservabilityStore? ObservabilityStore { get; init; }
}

/// <summary>
/// The maintained RimLiaison provider. It projects owner data already exposed
/// by Git, the catalog, DevBridge2, and the observability store; it does not
/// recreate their decisions or persist a second context database.
/// </summary>
public sealed class RimLiaisonContextBundleProvider : IRimContextBundleProvider
{
    private const int MaximumProbeStdoutBytes = 512 * 1024;
    private const int MaximumProbeStderrBytes = 16 * 1024;

    private readonly RimLiaisonContextProviderOptions options;

    public RimLiaisonContextBundleProvider(RimLiaisonContextProviderOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => "rimliaison";

    public async ValueTask<RimContextProviderSnapshot> CollectAsync(
        RimContextProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = request.NowUtc;

        IGitRepositoryStateProvider gitProvider = options.GitProvider ?? new SystemGitRepositoryStateProvider();
        GitRepositoryStateResult git = await gitProvider
            .ReadAsync(options.RootPath, cancellationToken)
            .ConfigureAwait(false);
        AgentObservabilitySnapshot observability = ReadObservability(
            request,
            options.ObservabilityStore);
        RimErrorProjection rimError = await ReadRimErrorAsync(
                request.MaxFailures,
                cancellationToken)
            .ConfigureAwait(false);
        CatalogSnapshot catalog = ReadCatalog();
        RuntimeProbe runtime = await ProbeRuntimeAsync(now, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RimContextRepositoryState> relatedRepositories =
            await ReadRelatedRepositoriesAsync(
                    gitProvider,
                    request.Verbose,
                    runtime.RootPath,
                    cancellationToken)
                .ConfigureAwait(false);
        RimContextRepositoryState? repository = git.State is null
            ? null
            : ToRepositoryState(git.State, "RimLiaison", request.Verbose);
        RimContextDeploymentState? deployment = observability.Deployment is null
            ? null
            : MergeDeployment(observability.Deployment, runtime.State, git.State);

        var snapshot = new RimContextProviderSnapshot(
            Id,
            now,
            Topology: Available(
                CreateTopology(git.State, relatedRepositories, runtime, request.Verbose),
                Id,
                now),
            Repository: repository is null
                ? Unavailable<RimContextRepositoryState>(
                    git.ErrorCode ?? "GIT_STATE_UNAVAILABLE",
                    "Git did not provide a trustworthy repository snapshot.",
                    Id,
                    "git status --short")
                : Available(repository, "git", now),
            Environment: Available(CreateEnvironment(request, runtime), Id, now),
            Deployment: deployment is null
                ? Unknown<RimContextDeploymentState>(
                    "DEPLOYMENT_EVIDENCE_UNAVAILABLE",
                    "No recent DevBridge build/deployment evidence was recorded.",
                    "devbridge2")
                : Available(deployment, "devbridge2", observability.DeploymentObservedAtUtc ?? now),
            Runtime: runtime.State is null
                ? new RimContextSection<RimContextRuntimeState>
                {
                    Status = runtime.Status,
                    Value = runtime.RuntimeIdentity is null
                        ? null
                        : new RimContextRuntimeState { RuntimeIdentity = runtime.RuntimeIdentity },
                    Provider = "devbridge2/rimbridgeserver",
                    ReasonCode = runtime.ErrorCode,
                    Message = runtime.Message,
                    NextAction = runtime.NextAction
                }
                : new RimContextSection<RimContextRuntimeState>
                {
                    Status = runtime.Status,
                    Value = runtime.State,
                    Provider = "devbridge2/rimbridgeserver",
                    ObservedAtUtc = runtime.ObservedAtUtc ?? now,
                    Stale = runtime.Status == RimContextBundleStatuses.Stale,
                    ReasonCode = runtime.State.FailureCode,
                    NextAction = runtime.Status == RimContextBundleStatuses.Stale
                        ? "refresh-runtime"
                        : null
                },
            Testing: catalog.State is null
                ? Unavailable<RimContextTestingState>(
                    catalog.ErrorCode ?? "CATALOG_UNAVAILABLE",
                    catalog.Message ?? "The test catalog was not available.",
                    "rimliaison",
                    "rimliaison validate --json")
                : Available(
            CreateTesting(catalog.State, observability, git.State),
                    "rimtest/rimliaison",
                    observability.ObservedAtUtc ?? now),
            RecentExecutions: observability.Executions,
            Failures: observability.Failures
                .Concat(rimError.Failures)
                .OrderByDescending(static failure => failure.ObservedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(static failure => failure.SignatureCode, StringComparer.Ordinal)
                .ThenBy(static failure => failure.EvidenceId ?? string.Empty, StringComparer.Ordinal)
                .GroupBy(
                    static failure => failure.SignatureCode + "\u001f" +
                        (failure.EvidenceId ?? failure.OriginatingComponent),
                    StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(request.MaxFailures)
                .ToArray(),
            Efficiency: observability.Efficiency is null
                ? Unknown<RimContextEfficiencyMetrics>(
                    "EFFICIENCY_EVIDENCE_UNAVAILABLE",
                    "No recent execution metrics were recorded.",
                    "rimliaison")
                : Available(observability.Efficiency, "rimliaison", observability.ObservedAtUtc ?? now),
            Decisions: observability.Decisions,
            Extensions: new RimContextExtension[]
            {
                Extension(
                    Id,
                    "configuration",
                    new
                    {
                        catalog = request.Verbose ? DisplayPath(options.CatalogPath) : "configured",
                        project = options.Project,
                        modId = options.ObservabilityModId,
                        modName = options.ObservabilityModName,
                        fallbackSuite = options.FallbackSuite,
                        devBridgeProject = options.DevBridgeProject,
                        rimContextStore = string.IsNullOrWhiteSpace(options.RimContextStorePath)
                            ? null
                            : request.Verbose ? DisplayPath(options.RimContextStorePath!) : "configured",
                        stackManifest = options.StackManifestPath is null
                            ? null
                            : request.Verbose ? DisplayPath(options.StackManifestPath) : "configured"
                    }),
                Extension(
                    Id,
                    "providerStatus",
                    new
                    {
                        git = git.Resolved ? "available" : git.ErrorCode ?? "unavailable",
                        catalog = catalog.State is null ? catalog.ErrorCode ?? "unavailable" : "available",
                        runtime = runtime.State is null ? runtime.ErrorCode ?? "unavailable" : "available",
                        rimError = rimError.Status,
                        relatedRepositories = relatedRepositories.Count
                    }),
                Extension(
                    Id,
                    "stateHygiene",
                    CreateStateHygieneAudit(request.Verbose, runtime.RootPath))
            },
            RelatedRepositories: relatedRepositories
                .Select(repositoryState => repositoryState with
                {
                    Component = repositoryState.Component ?? "related"
                })
                .ToArray()
            );
        return snapshot;
    }

    private RimContextTopology CreateTopology(
        GitRepositoryStateSnapshot? git,
        IReadOnlyList<RimContextRepositoryState> relatedRepositories,
        RuntimeProbe runtime,
        bool verbose)
    {
        string? commit = git?.HeadSha;
        var components = new List<RimContextComponent>
        {
            new()
            {
                Name = "RimLiaison",
                Role = "Canonical agent-facing orchestration, selection, recovery, and bounded result projection.",
                Repository = git?.Identity,
                Version = AssemblyVersion(typeof(RimLiaisonContextBundleProvider).Assembly),
                Commit = commit,
                LocalPath = verbose ? options.RootPath : null,
                Capabilities = ["context-bundle", "affected-selection", "orchestration", "doctor"]
            },
            new()
            {
                Name = "RimContext",
                Role = "Static source, Defs, Harmony, project, and dependency context.",
                Repository = git?.Identity,
                Version = IndexConstants.ToolVersion,
                Commit = commit,
                LocalPath = verbose
                    ? ExistingPath(Path.Combine(options.RootPath, "src", "RimContext.Core"))
                    : null,
                Capabilities = ["static-index", "affected-impact", "definitions", "references", "harmony"]
            },
            new()
            {
                Name = "RimTest",
                Role = "Affected-test selection, suite execution, evidence reuse, and validation result authority.",
                Repository = git?.Identity,
                Version = AssemblyVersion(typeof(RimLiaisonContextBundleProvider).Assembly),
                Commit = commit,
                LocalPath = verbose
                    ? ExistingPath(Path.Combine(options.RootPath, "src", "RimLiaison.Cli"))
                    : null,
                Capabilities = ["affected-selection", "suite-execution", "evidence-reuse", "validation"]
            }
        };
        string? devBridgeRoot = options.DevBridgeRootPath ?? runtime.RootPath;
        if (!string.IsNullOrWhiteSpace(devBridgeRoot) &&
            Directory.Exists(devBridgeRoot))
        {
            RimContextRepositoryState? devBridgeRepository = relatedRepositories
                .FirstOrDefault(static repository =>
                    string.Equals(repository.Component, "DevBridge2", StringComparison.Ordinal));
            components.Add(new RimContextComponent
            {
                Name = "DevBridge2",
                Role = "Build, deployment, lifecycle, generation, lease, and artifact-freshness authority.",
                Repository = devBridgeRepository?.Identity,
                Version = runtime.ComponentVersions.TryGetValue("coordinatorVersion", out string? coordinatorVersion)
                    ? coordinatorVersion
                    : runtime.ComponentVersions.TryGetValue("bridgeToolsVersion", out string? bridgeToolsVersion)
                        ? bridgeToolsVersion
                        : runtime.ComponentVersions.TryGetValue("modVersion", out string? modVersion)
                            ? modVersion
                            : null,
                Commit = runtime.ComponentVersions.TryGetValue("coordinatorRevision", out string? coordinatorRevision)
                    ? coordinatorRevision
                    : devBridgeRepository?.HeadSha,
                LocalPath = verbose ? Path.GetFullPath(devBridgeRoot!) : null,
                Capabilities = ["build", "deploy", "runtime", "generation", "lease"]
            });
        }

        string rimErrorPath = Path.Combine(options.RootPath, "src", "RimError.Core");
        if (Directory.Exists(rimErrorPath))
        {
            components.Add(new RimContextComponent
            {
                Name = "RimError",
                Role = "Bounded diagnostic ingestion, classification, and root-cause projection.",
                Repository = git?.Identity,
                Version = AssemblyVersionByName("RimError.Core"),
                LocalPath = verbose ? rimErrorPath : null,
                Capabilities = ["failure-classification", "diagnostics"]
            });
        }

        var dependencies = new List<RimContextDependency>
        {
            new() { From = "RimLiaison", To = "RimContext", Reason = "static impact and context provider" },
            new() { From = "RimLiaison", To = "RimTest", Reason = "affected selection, execution, and evidence projection" }
        };
        if (components.Any(static component => component.Name == "DevBridge2"))
        {
            components.Add(new RimContextComponent
            {
                Name = "RimBridgeServer",
                Role = "Live in-game bridge and semantic RimWorld operation authority.",
                Repository = "pardeike/RimBridgeServer",
                Version = runtime.ComponentVersions.TryGetValue(
                    "rimBridgeVersion",
                    out string? rimBridgeVersion)
                    ? rimBridgeVersion
                    : null,
                Capabilities = ["live-game-operations", "map-state", "game-state"]
            });
            dependencies.Add(new RimContextDependency
            {
                From = "RimLiaison",
                To = "DevBridge2",
                Reason = "owner-routed build, deployment, and runtime operations"
            });
            dependencies.Add(new RimContextDependency
            {
                From = "DevBridge2",
                To = "RimBridgeServer",
                Reason = "live in-game bridge ownership"
            });
        }
        if (components.Any(static component => component.Name == "RimError"))
        {
            dependencies.Add(new RimContextDependency
            {
                From = "RimLiaison",
                To = "RimError",
                Reason = "bounded failure diagnosis"
            });
        }

        return new RimContextTopology
        {
            Components = components
                .OrderBy(static component => component.Name, StringComparer.Ordinal)
                .ToArray(),
            Dependencies = dependencies
                .OrderBy(static dependency => dependency.From, StringComparer.Ordinal)
                .ThenBy(static dependency => dependency.To, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private RimContextEnvironmentState CreateEnvironment(
        RimContextProviderRequest request,
        RuntimeProbe runtime)
    {
        var tools = new List<RimContextToolVersion>
        {
            new() { Name = "rimctx", Version = IndexConstants.ToolVersion },
            new() { Name = "rimliaison", Version = AssemblyVersion(typeof(RimLiaisonContextBundleProvider).Assembly) },
            new() { Name = ".NET", Version = Environment.Version.ToString() }
        };
        return new RimContextEnvironmentState
        {
            Os = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Compiler = IndexConstants.SemanticIndexerVersion,
            RimWorldVersion = runtime.ComponentVersions.TryGetValue(
                "rimWorldVersion",
                out string? rimWorldVersion)
                ? rimWorldVersion
                : RimContextBundleStatuses.Unknown,
            Tools = tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray(),
            Configuration = new RimContextSetting[]
            {
                new RimContextSetting
                {
                    Name = "catalog",
                    Value = request.Verbose ? DisplayPath(options.CatalogPath) : "configured"
                },
                new RimContextSetting { Name = "devBridgeProject", Value = options.DevBridgeProject ?? "unknown" },
                new RimContextSetting { Name = "fallbackSuite", Value = options.FallbackSuite ?? "unknown" },
                new RimContextSetting
                {
                    Name = "root",
                    Value = request.Verbose ? DisplayPath(request.RootPath) : "repository-root"
                },
                new RimContextSetting
                {
                    Name = "rimContextStore",
                    Value = string.IsNullOrWhiteSpace(options.RimContextStorePath)
                        ? "default"
                        : request.Verbose ? DisplayPath(options.RimContextStorePath!) : "configured"
                }
            }
            .OrderBy(static setting => setting.Name, StringComparer.Ordinal)
            .ToArray(),
            SecretsExcluded = true
        };
    }

    private static RimContextTestingState CreateTesting(
        CatalogDocument catalog,
        AgentObservabilitySnapshot observability,
        GitRepositoryStateSnapshot? git)
    {
        string? invalidation = observability.Decisions
            .FirstOrDefault(static decision =>
                string.Equals(decision.Action, RimContextDecisionActions.Invalidate, StringComparison.Ordinal))
            ?.ReasonCode;
        string? cacheStatus = observability.Decisions.Any(static decision =>
                string.Equals(decision.Action, RimContextDecisionActions.Reuse, StringComparison.Ordinal))
            ? "hit"
            : observability.Decisions.Any(static decision =>
                string.Equals(decision.Action, RimContextDecisionActions.Run, StringComparison.Ordinal))
                ? "miss"
                : null;
        return new RimContextTestingState
        {
            AvailableSuites = (catalog.Suites ?? [])
                .Where(static suite => suite is not null)
                .Select(static suite => suite.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray(),
            AvailableTests = (catalog.Tests ?? [])
                .Where(static test => test is not null)
                .Select(static test => test.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray(),
            SelectedSuites = observability.SelectedSuites,
            ExecutedSuites = observability.ExecutedSuites,
            ReusedSuites = observability.ReusedSuites,
            SkippedSuites = observability.SkippedSuites,
            SelectedTests = observability.SelectedTests,
            ExecutedTests = observability.ExecutedTests,
            ReusedTests = observability.ReusedTests,
            SkippedTests = observability.SkippedTests,
            Policy = "RimContext affected impact plus catalog coverage; unknown or stale input uses the configured fallback suite.",
            LatestEvidence = observability.Evidence,
            InvalidatedEvidence = observability.InvalidatedEvidence,
            BenchmarkSummary = observability.BenchmarkSummary,
            CacheStatus = observability.CacheStatus ?? cacheStatus,
            InvalidationReason = observability.InvalidationReason ?? invalidation,
            AdditionalValidationRequired = AdditionalValidationRequired(observability, git),
            LatestResult = observability.LatestResult,
            LatestSourceFingerprint = observability.LatestSourceFingerprint,
            LatestBuildArtifactFingerprint = observability.LatestBuildArtifactFingerprint,
            LatestDeploymentArtifactFingerprint = observability.LatestDeploymentArtifactFingerprint,
            LatestTransactionId = observability.LatestTransactionId,
            LatestGeneration = observability.LatestGeneration,
            LatestDurationMs = observability.LatestDurationMs,
            InfrastructureFailure = observability.InfrastructureFailure,
            Retryable = observability.Retryable
        };
    }

    private static bool? AdditionalValidationRequired(
        AgentObservabilitySnapshot observability,
        GitRepositoryStateSnapshot? git)
    {
        string? currentSourceFingerprint = git?.SourceFingerprint;
        if (observability.InfrastructureFailure == true ||
            observability.InvalidatedEvidence.Count > 0 ||
            observability.Decisions.Any(static decision =>
                decision.Action is RimContextDecisionActions.Invalidate or
                    RimContextDecisionActions.Retry or
                    RimContextDecisionActions.Block) ||
            observability.LatestResult is "fail" or "failure" or "infrastructure" or "cancelled")
        {
            return true;
        }

        RimContextDecision? latestOwnerDecision = observability.Decisions
            .FirstOrDefault(static decision =>
                string.Equals(decision.Owner, "RimTest/RimLiaison", StringComparison.Ordinal));
        if (latestOwnerDecision?.Action is RimContextDecisionActions.Reuse or
            RimContextDecisionActions.Skip)
        {
            return false;
        }

        if ((observability.LatestResult is "pass" or "passed" or "success") &&
            !string.IsNullOrWhiteSpace(observability.LatestSourceFingerprint) &&
            !string.IsNullOrWhiteSpace(currentSourceFingerprint) &&
            string.Equals(
                observability.LatestSourceFingerprint,
                currentSourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Git supplies change facts, not test policy. Without an owner
        // decision or matching owner evidence the correct answer is unknown.
        return null;
    }

    private async Task<RuntimeProbe> ProbeRuntimeAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        DevBridgeAdapterOptions bridge;
        try
        {
            bridge = DiscoverConfiguredBridge();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            InvalidOperationException or NotSupportedException)
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_CONFIGURATION_INVALID", "DevBridge configuration could not be resolved.");
        }

        IDevBridgeProcessTransport transport = options.ProcessTransport ?? new SystemDevBridgeProcessTransport();
        DevBridgeProcessResult process;
        try
        {
            process = await ExecuteProbeAsync(
                    transport,
                    bridge,
                    ["--root", bridge.RootPath, "doctor", "--json"],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_CANCELLED", "The runtime context probe was cancelled.");
        }
        catch (Exception)
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_PROBE_FAILED", "DevBridge did not return runtime state.");
        }

        if (process.Cancelled)
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_CANCELLED", "The runtime context probe was cancelled.");
        }

        if (process.TimedOut)
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_CLIENT_TIMEOUT", "DevBridge runtime state probe timed out.", "DevBridge.cmd doctor --json");
        }

        if (!string.IsNullOrWhiteSpace(process.StartError) ||
            process.StdoutTruncated ||
            string.IsNullOrWhiteSpace(process.Stdout))
        {
            return RuntimeProbe.Unavailable("DEVBRIDGE_RESPONSE_INVALID", "DevBridge runtime state was unavailable.", "DevBridge.cmd doctor --json");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            RuntimeProbe? parsed = ParseRuntimeProbe(document.RootElement, observedAtUtc);
            if (parsed is null)
            {
                return RuntimeProbe.Unavailable(
                    "DEVBRIDGE_RESPONSE_INVALID",
                    "DevBridge response contained no runtime identity.",
                    "DevBridge.cmd doctor --json");
            }

            parsed = parsed with { RootPath = bridge.RootPath };
            if (parsed.State is null)
            {
                return parsed;
            }

            // The agent snapshot is a synchronized, read-only DevBridge2
            // projection for lease, quicktest, bridge-endpoint, and loaded
            // component identity. Failure of this optional probe must not
            // erase a trustworthy doctor result.
            try
            {
                DevBridgeProcessResult? agentSnapshot = await ExecuteProbeAsync(
                        transport,
                        bridge,
                        ["--root", bridge.RootPath, "agent", "snapshot", "--json"],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (IsProbeResponseUsable(agentSnapshot))
                {
                    using JsonDocument snapshotDocument = JsonDocument.Parse(agentSnapshot!.Stdout!);
                    RimContextRuntimeState mergedState = MergeAgentSnapshot(
                        parsed.State!,
                        snapshotDocument.RootElement);
                    parsed = parsed with
                    {
                        State = mergedState,
                        Status = IsRuntimeStale(mergedState)
                            ? RimContextBundleStatuses.Stale
                            : parsed.Status,
                        ComponentVersions = MergeComponentVersions(
                            parsed.ComponentVersions,
                            snapshotDocument.RootElement)
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Optional owner state is unknown; the doctor projection is
                // still the authoritative result for this snapshot.
            }

            return parsed;
        }
        catch (JsonException)
        {
            return RuntimeProbe.Unavailable(
                "DEVBRIDGE_RESPONSE_INVALID",
                "DevBridge returned malformed runtime JSON.",
                "DevBridge.cmd doctor --json");
        }
    }

    private static async Task<DevBridgeProcessResult> ExecuteProbeAsync(
        IDevBridgeProcessTransport transport,
        DevBridgeAdapterOptions bridge,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await transport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    bridge.CommandPath,
                    bridge.RootPath,
                    arguments,
                    TimeSpan.FromSeconds(20),
                    MaximumProbeStdoutBytes,
                    MaximumProbeStderrBytes),
                cancellationToken)
            .ConfigureAwait(false);

    private DevBridgeAdapterOptions DiscoverConfiguredBridge()
        // Context must use the same canonical installation/configuration
        // discovery as doctor and every other DevBridge adapter. Keeping a
        // second resolver here caused a ready stack to appear unavailable.
        => DevBridgeAdapterOptions.Discover(options.DevBridgePath, options.DevBridgeRootPath);

    private static bool IsProbeResponseUsable(DevBridgeProcessResult? process) =>
        process is not null &&
        !process.Cancelled &&
        !process.TimedOut &&
        string.IsNullOrWhiteSpace(process.StartError) &&
        !process.StdoutTruncated &&
        !string.IsNullOrWhiteSpace(process.Stdout);

    private static RuntimeProbe? ParseRuntimeProbe(
        JsonElement root,
        DateTimeOffset observedAtUtc)
    {
        JsonElement operational = GetObject(root, "operationalState");
        JsonElement runtime = GetObject(root, "runtime");
        JsonElement rimBridge = GetObject(root, "rimBridge");
        JsonElement quicktest = GetObject(root, "quicktest");
        JsonElement runtimeIdentity = GetObject(root, "runtimeIdentity");
        bool? healthy = FirstBoolean(root, "healthy") ??
            FirstBoolean(operational, "healthy");
        int? generation = FirstInt(root, "generation") ??
            FirstInt(operational, "generation") ??
            FirstInt(runtime, "generation");
        int? processId = FirstInt(root, "processId", "rimWorldPid") ??
            FirstInt(operational, "processId") ??
            FirstInt(runtime, "processId");
        bool? processRunning = FirstBoolean(root, "processRunning", "rimWorldRunning") ??
            FirstBoolean(operational, "processRunning") ??
            FirstBoolean(runtime, "processRunning");
        string? phase = FirstString(root, "lifecycleState", "operationalState", "state") ??
            FirstString(operational, "phase", "readinessState", "state") ??
            FirstString(runtime, "lifecycleState", "state") ??
            FirstString(GetObject(root, "gameState"), "lifecycleState", "state");
        string? gameState = GetString(root, "gameState") ??
            FirstString(GetObject(root, "gameState"), "phase", "state") ??
            GetString(runtime, "gameState") ??
            FirstString(GetObject(runtime, "gameState"), "phase", "state");
        string? mapState = GetString(root, "mapState") ??
            GetString(GetObject(root, "gameState"), "mapState");
        string? bridgeLifecycle = FirstString(rimBridge, "lifecycleState", "state");
        string? launchId = FirstString(root, "launchId") ??
            FirstString(operational, "launchId") ??
            FirstString(rimBridge, "launchId");
        string? leaseState = GetString(root, "leaseState") ??
            FirstString(GetObject(root, "leaseState"), "state", "status") ??
            FirstString(operational, "leaseState");
        string? leaseId = FirstString(root, "leaseId") ??
            FirstString(GetObject(root, "leaseState"), "leaseId");
        string? currentTrust = FirstString(root, "currentGenerationTrust") ??
            FirstString(operational, "currentGenerationTrust");
        int? activeLeases = FirstInt(root, "activeLeaseCount") ??
            FirstInt(operational, "activeLeaseCount");
        bool? running = processRunning ??
            (processId.HasValue ? processId > 0 : IsLifecycleRunning(phase));
        RimContextRuntimeIdentity? parsedIdentity = ParseRuntimeIdentity(runtimeIdentity);
        if (healthy is null && generation is null && processId is null &&
            phase is null && bridgeLifecycle is null && running is null &&
            parsedIdentity is null)
        {
            return null;
        }
        if (healthy is null && generation is null && processId is null &&
            phase is null && bridgeLifecycle is null && running is null)
        {
            return new RuntimeProbe(
                null,
                RimContextBundleStatuses.Unavailable,
                FirstString(root, "errorCode", "failureCode"),
                FirstString(root, "error"),
                FirstString(root, "nextAction"),
                observedAtUtc,
                ReadComponentVersions(root),
                null,
                parsedIdentity);
        }

        string? bridgeStatus = bridgeLifecycle ?? (healthy switch
        {
            true => "available",
            false => "unhealthy",
            _ => phase
        });
        var componentVersions = ReadComponentVersions(root);
        bool? staleFlag = FirstBoolean(root, "stale", "isStale") ??
            FirstBoolean(operational, "stale", "isStale") ??
            FirstBoolean(runtime, "stale", "isStale");
        string? freshnessStatus = FirstString(
            root,
            "freshnessStatus",
            "runtimeStatus",
            "stateFreshness") ??
            FirstString(operational, "freshnessStatus", "runtimeStatus") ??
            FirstString(runtime, "freshnessStatus", "runtimeStatus");
        bool stale = staleFlag == true ||
            freshnessStatus?.Equals("STALE", StringComparison.OrdinalIgnoreCase) == true ||
            bridgeLifecycle?.Equals("STALE", StringComparison.OrdinalIgnoreCase) == true ||
            currentTrust?.Contains("stale", StringComparison.OrdinalIgnoreCase) == true;
        return new RuntimeProbe(
            new RimContextRuntimeState
            {
                RimWorldRunning = running,
                ProcessId = processId,
                Generation = generation,
                BridgeStatus = bridgeStatus,
                MapState = mapState,
                GameState = gameState,
                QuicktestState = FirstString(root, "quicktestState") ??
                    FirstString(quicktest, "state", "status"),
                LeaseOwner = FirstString(root, "launchOwner") ??
                    FirstString(GetObject(root, "leaseState"), "owner"),
                LeaseId = leaseId,
                LaunchId = launchId,
                RestartPending = FirstBoolean(root, "restartPending") ??
                    FirstBoolean(operational, "restartQueued"),
                TargetGeneration = FirstInt(root, "targetGeneration") ??
                    FirstInt(operational, "targetGeneration"),
                LeaseState = leaseState,
                ActiveLeaseCount = activeLeases,
                MaintenanceReady = FirstBoolean(root, "maintenanceReady") ??
                    FirstBoolean(operational, "maintenanceReady"),
                CurrentGenerationTrust = currentTrust,
                FailureCode = FirstString(root, "errorCode", "failureCode") ??
                    FirstString(operational, "terminalFailureCode") ??
                    FirstString(rimBridge, "errorCode"),
                RuntimeIdentity = parsedIdentity
            },
            stale ? RimContextBundleStatuses.Stale : RimContextBundleStatuses.Available,
            null,
            null,
            null,
            observedAtUtc,
            componentVersions,
            null,
            parsedIdentity);
    }

    private static RimContextRuntimeState MergeAgentSnapshot(
        RimContextRuntimeState state,
        JsonElement snapshot)
    {
        JsonElement quicktest = GetObject(snapshot, "quicktest");
        JsonElement endpoint = GetObject(snapshot, "rimBridgeEndpoint");
        JsonElement lease = GetObject(snapshot, "requestingAgentLease");
        JsonElement maintenance = GetObject(snapshot, "maintenance");
        JsonElement failure = GetObject(snapshot, "failure");
        JsonElement componentBuilds = GetObject(snapshot, "componentBuilds");
        JsonElement modBuild = GetObject(componentBuilds, "mod");
        foreach (string name in new[] { "modBuild", "RimTest", "RimLiaison", "RimLiaison.Cli" })
        {
            if (modBuild.ValueKind != JsonValueKind.Object)
            {
                modBuild = GetObject(componentBuilds, name);
            }
        }

        string? endpointState = FirstString(endpoint, "state", "mode");
        string? quicktestState = FirstString(quicktest, "state", "status");
        bool snapshotStale = FirstBoolean(snapshot, "stale", "isStale") == true ||
            endpointState?.Equals("STALE", StringComparison.OrdinalIgnoreCase) == true;
        return state with
        {
            Generation = FirstInt(snapshot, "generation") ?? state.Generation,
            TargetGeneration = FirstInt(snapshot, "targetGeneration") ?? state.TargetGeneration,
            GameState = FirstString(snapshot, "gameState") ??
                FirstString(GetObject(snapshot, "gameState"), "phase", "state") ?? state.GameState,
            QuicktestState = quicktestState ?? state.QuicktestState,
            BridgeStatus = endpointState ?? state.BridgeStatus,
            LeaseState = FirstString(lease, "state", "status") ?? state.LeaseState,
            LeaseId = FirstString(lease, "leaseId", "id") ?? state.LeaseId,
            LeaseOwner = FirstString(lease, "owner", "agentId") ?? state.LeaseOwner,
            MaintenanceReady = FirstBoolean(maintenance, "ready") ?? state.MaintenanceReady,
            CurrentGenerationTrust = snapshotStale
                ? "stale"
                : FirstString(snapshot, "currentGenerationTrust") ?? state.CurrentGenerationTrust,
            RuntimeArtifactFingerprint = FirstString(modBuild, "artifactSha256", "artifactFingerprint") ?? state.RuntimeArtifactFingerprint,
            RuntimeArtifactStatus = FirstString(modBuild, "loadedStatus", "status") ?? state.RuntimeArtifactStatus,
            FailureCode = FirstString(failure, "code", "errorCode") ??
                FirstString(snapshot, "errorCode") ?? state.FailureCode,
            RestartPending = state.RestartPending ??
                FirstBoolean(snapshot, "restartPending")
        };
    }

    private static IReadOnlyDictionary<string, string> ReadComponentVersions(JsonElement root)
    {
        JsonElement components = GetObject(root, "components");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in new[] { "coordinatorVersion", "modVersion", "bridgeToolsVersion" })
        {
            string? value = GetString(components, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        JsonElement coordinatorBuild = GetObject(components, "coordinatorBuild");
        values.TryAdd(
            "coordinatorVersion",
            FirstString(coordinatorBuild, "informationalVersion", "productVersion") ?? "");
        if (values.TryGetValue("coordinatorVersion", out string? emptyVersion) &&
            string.IsNullOrWhiteSpace(emptyVersion))
        {
            values.Remove("coordinatorVersion");
        }
        string? revision = FirstString(coordinatorBuild, "sourceRevision", "revision");
        if (!string.IsNullOrWhiteSpace(revision))
        {
            values["coordinatorRevision"] = revision;
        }

        string? rimBridgeVersion = FirstString(GetObject(root, "rimBridge"), "version");
        if (!string.IsNullOrWhiteSpace(rimBridgeVersion))
        {
            values["rimBridgeVersion"] = rimBridgeVersion;
        }

        string? rimWorldVersion = FirstString(root, "rimWorldVersion", "gameVersion") ??
            FirstString(GetObject(root, "runtime"), "rimWorldVersion", "gameVersion");

        if (!string.IsNullOrWhiteSpace(rimWorldVersion))
        {
            values["rimWorldVersion"] = rimWorldVersion;
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> MergeComponentVersions(
        IReadOnlyDictionary<string, string> existing,
        JsonElement snapshot)
    {
        var values = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        JsonElement builds = GetObject(snapshot, "componentBuilds");
        foreach ((string source, string versionKey, string revisionKey) in new[]
        {
            ("coordinator", "coordinatorVersion", "coordinatorRevision"),
            ("coordinatorBuild", "coordinatorVersion", "coordinatorRevision"),
            ("mod", "modVersion", "modRevision"),
            ("modBuild", "modVersion", "modRevision"),
            ("bridgeTools", "bridgeToolsVersion", "bridgeToolsRevision"),
            ("rimBridge", "rimBridgeVersion", "rimBridgeRevision"),
            ("rimBridgeServer", "rimBridgeVersion", "rimBridgeRevision")
        })
        {
            JsonElement build = GetObject(builds, source);
            string? version = FirstString(build, "version", "informationalVersion", "productVersion");
            if (!string.IsNullOrWhiteSpace(version))
            {
                values[versionKey] = version;
            }

            string? revision = FirstString(build, "sourceRevision", "revision", "commit");
            if (!string.IsNullOrWhiteSpace(revision))
            {
                values[revisionKey] = revision;
            }
        }

        return values;
    }

    private static bool IsLifecycleRunning(string? value) =>
        value is not null &&
        (value.Equals("READY", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("RUNNING", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("LOADING", StringComparison.OrdinalIgnoreCase));

    private static bool IsRuntimeStale(RimContextRuntimeState state) =>
        state.BridgeStatus?.Equals("STALE", StringComparison.OrdinalIgnoreCase) == true ||
        state.CurrentGenerationTrust?.Contains("stale", StringComparison.OrdinalIgnoreCase) == true ||
        state.RuntimeArtifactStatus?.Equals("stale", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<IReadOnlyList<RimContextRepositoryState>> ReadRelatedRepositoriesAsync(
        IGitRepositoryStateProvider gitProvider,
        bool verbose,
        string? discoveredDevBridgeRoot,
        CancellationToken cancellationToken)
    {
        var configured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.RelatedRepositoryRoots is not null)
        {
            foreach ((string name, string path) in options.RelatedRepositoryRoots
                         .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                             !string.IsNullOrWhiteSpace(pair.Value))
                         .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                configured[name] = path;
            }
        }

        string? devBridgeRoot = options.DevBridgeRootPath ?? discoveredDevBridgeRoot;
        if (!string.IsNullOrWhiteSpace(devBridgeRoot))
        {
            configured.TryAdd("DevBridge2", devBridgeRoot!);
        }

        string root = Path.GetFullPath(options.RootPath);
        var results = new List<RimContextRepositoryState>();
        foreach ((string component, string path) in configured)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(fullPath))
            {
                continue;
            }

            GitRepositoryStateResult state;
            try
            {
                state = await gitProvider.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (state.State is not null &&
                !string.Equals(state.State.RootPath, root, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(ToRepositoryState(state.State, component, verbose));
            }
        }

        return results
            .OrderBy(static repository => repository.Component ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static repository => repository.LocalPath ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private object CreateStateHygieneAudit(bool verbose, string? discoveredDevBridgeRoot)
    {
        string root = Path.GetFullPath(options.RootPath);
        string? devBridgeRoot = options.DevBridgeRootPath ?? discoveredDevBridgeRoot;
        string canonicalObservability;
        try
        {
            canonicalObservability = AgentObservabilityStorage.ResolveCanonicalRoot();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            canonicalObservability = "unknown";
        }

        string[] generatedPaths = [
            ".rimctx",
            ".rimdev/observability",
            ".rimdev/profiles",
            ".rimdev/validation-proofs",
            ".rimerror",
            ".vs",
            "bin",
            "obj",
            "TestResults"
        ];
        var generated = generatedPaths
            .Select(path => new
            {
                path,
                present = Directory.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))) ||
                    File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))),
                ignoredByConvention = true,
                owner = path.StartsWith(".rimctx", StringComparison.Ordinal)
                    ? "RimContext"
                    : path.StartsWith(".rimdev/observability", StringComparison.Ordinal)
                        ? "legacy-observability"
                    : path.StartsWith(".rimdev", StringComparison.Ordinal)
                        ? "RimLiaison"
                        : path.StartsWith(".rimerror", StringComparison.Ordinal)
                            ? "RimError"
                        : "build-tooling"
            })
            .ToArray();
        var meaningfulConfiguration = new[]
        {
            new
            {
                path = ".rimdev/stack.json",
                present = File.Exists(Path.Combine(root, ".rimdev", "stack.json")),
                ignoredByConvention = false,
                owner = "RimLiaison"
            }
        };
        var unclassified = new[]
        {
            ".rimdev/transactions",
            ".rimdev/reports",
            ".rimdev/cache",
            ".rimdev/tmp",
            "artifacts",
            "coverage"
        }
        .Select(path => new
        {
            path,
            present = Directory.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))) ||
                File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))),
            ignoredByConvention = false,
            requiresOwnerReview = true,
            owner = path.StartsWith(".rimerror", StringComparison.Ordinal)
                ? "RimError"
                : "DevBridge2"
        })
        .ToArray();

        if (!verbose)
        {
            // The compact bundle keeps only present state and stable owners.
            // Full roots, absent candidates, and the complete convention list
            // remain available through --verbose without consuming startup
            // context tokens on healthy defaults.
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalOwner"] = "AgentObservabilityStore",
                ["repositoryLocalGenerated"] = generated
                    .Where(static item => item.present)
                    .Select(item => new
                    {
                        item.path,
                        item.owner,
                        item.ignoredByConvention
                    })
                    .ToArray(),
                ["meaningfulConfiguration"] = meaningfulConfiguration
                    .Where(static item => item.present)
                    .Select(item => new { item.path, item.owner })
                    .ToArray(),
                ["unclassifiedCandidates"] = unclassified
                    .Where(static item => item.present)
                    .Select(item => new
                    {
                        item.path,
                        item.owner,
                        item.requiresOwnerReview
                    })
                    .ToArray(),
                ["externalOwnerState"] = new object[]
                {
                    new
                    {
                        owner = "DevBridge2",
                        configured = !string.IsNullOrWhiteSpace(devBridgeRoot),
                        readOnlyProbe = true
                    },
                    new
                    {
                        owner = "AgentObservabilityStore",
                        configured = !string.Equals(canonicalObservability, "unknown", StringComparison.Ordinal),
                        readOnlyProbe = true
                    }
                }
            };
        }

        return new
        {
            canonicalObservability = canonicalObservability.Replace('\\', '/'),
            repositoryLocalGenerated = generated,
            meaningfulConfiguration,
            unclassifiedCandidates = unclassified,
            externalOwnerState = new object[]
            {
                new
                {
                    owner = "DevBridge2",
                    root = string.IsNullOrWhiteSpace(devBridgeRoot)
                        ? "unknown"
                        : DisplayPath(devBridgeRoot!),
                    readOnlyProbe = true
                },
                new
                {
                    owner = "AgentObservabilityStore",
                    root = canonicalObservability.Replace('\\', '/'),
                    readOnlyProbe = true
                }
            }
        };
    }

    private static RimContextDeploymentState MergeDeployment(
        RimContextDeploymentState deployment,
        RimContextRuntimeState? runtime,
        GitRepositoryStateSnapshot? repository)
    {
        string? sourceCorrespondence = CompareFingerprint(
            deployment.SourceFingerprint,
            repository?.SourceFingerprint);
        if (sourceCorrespondence is null &&
            deployment.SourceFingerprint is not null &&
            repository?.Dirty == true &&
            repository.Changes.Any(static change => !change.Generated))
        {
            sourceCorrespondence = "source-changed";
        }

        string? buildDeploymentCorrespondence = CompareFingerprint(
            deployment.BuildArtifactFingerprint,
            deployment.DeployedArtifactFingerprint);
        string? deploymentRuntimeCorrespondence = CompareFingerprint(
            deployment.DeployedArtifactFingerprint,
            runtime?.RuntimeArtifactFingerprint);
        if (deploymentRuntimeCorrespondence is null &&
            runtime?.Generation is not null &&
            deployment.Generation is not null)
        {
            deploymentRuntimeCorrespondence = runtime.Generation < deployment.Generation
                ? "runtime-stale"
                : runtime.Generation == deployment.Generation
                    ? "generation-matches"
                    : "runtime-generation-newer";
        }

        string? overall = sourceCorrespondence is "mismatch" or "source-changed"
            ? "source-changed"
            : buildDeploymentCorrespondence == "mismatch"
                ? "mismatch"
                : deploymentRuntimeCorrespondence is "mismatch"
                    ? "runtime-mismatch"
                    : deploymentRuntimeCorrespondence == "runtime-stale" ||
                        string.Equals(runtime?.CurrentGenerationTrust, "stale", StringComparison.OrdinalIgnoreCase)
                        ? "runtime-stale"
                        : sourceCorrespondence == "corresponds" &&
                          buildDeploymentCorrespondence == "corresponds" &&
                          deploymentRuntimeCorrespondence is "corresponds" or "generation-matches"
                            ? "synchronized"
                            : runtime is null || deploymentRuntimeCorrespondence is null
                                ? "unknown"
                                : deployment.Correspondence ?? "unknown";
        return deployment with
        {
            Correspondence = overall,
            RuntimeArtifactFingerprint = runtime?.RuntimeArtifactFingerprint,
            RuntimeArtifactStatus = runtime?.RuntimeArtifactStatus,
            RuntimeGeneration = runtime?.Generation,
            RuntimeLaunchId = runtime?.LaunchId,
            SourceCorrespondence = sourceCorrespondence,
            BuildDeploymentCorrespondence = buildDeploymentCorrespondence,
            DeploymentRuntimeCorrespondence = deploymentRuntimeCorrespondence
        };
    }

    private static string? CompareFingerprint(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return null;
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? "corresponds"
            : "mismatch";
    }

    private async Task<RimErrorProjection> ReadRimErrorAsync(
        int maximumFailures,
        CancellationToken cancellationToken)
    {
        RimErrorAdapterOptions adapter;
        try
        {
            adapter = RimErrorAdapterOptions.Discover(
                options.RimErrorPath,
                options.RimErrorLogPath,
                options.RimErrorStorePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return new RimErrorProjection([], "configuration-invalid");
        }

        if (string.IsNullOrWhiteSpace(adapter.StorePath))
        {
            return new RimErrorProjection([], "unconfigured");
        }

        try
        {
            var store = new JsonFileDiagnosticStore(adapter.StorePath);
            DiagnosticStoreSnapshot? snapshot = await store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return new RimErrorProjection([], "empty");
            }

            DiagnosticLatestReport report = DiagnosticLatestReportBuilder.Build(
                snapshot,
                includeAll: false,
                includeDiagnostics: true);
            IReadOnlyDictionary<string, DiagnosticRecord> records = snapshot.Items
                .GroupBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            RimContextFailure[] failures = (report.RootCauses ?? [])
                .Take(maximumFailures)
                .Select(summary => ProjectRimErrorFailure(
                    summary,
                    records.TryGetValue(summary.Id, out DiagnosticRecord? record) ? record : null,
                    snapshot.CapturedAt))
                .ToArray();
            return new RimErrorProjection(failures, "available");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            return new RimErrorProjection([], "store-unavailable");
        }
    }

    private static RimContextFailure ProjectRimErrorFailure(
        DiagnosticRootCauseSummary summary,
        DiagnosticRecord? record,
        DateTimeOffset? capturedAt)
    {
        string classification = summary.Category ?? record?.Category ?? "diagnostic";
        string signature = summary.Code ?? record?.BuildCode ?? summary.Id;
        string? establishedCause = JoinNonEmpty(
            summary.Type,
            summary.Method,
            summary.Def,
            summary.Member,
            summary.Asset,
            summary.Code);
        FailureKnowledgeMatch? knowledge = FailureKnowledgeCatalog.Match(
            signature,
            establishedCause,
            classification,
            summary.Source is null ? null : [summary.Source]);
        return new RimContextFailure
        {
            SignatureCode = signature,
            OriginatingComponent = record?.OriginatingAssembly ?? "RimError",
            Classification = classification,
            RootCause = establishedCause ?? knowledge?.Entry.KnownCause,
            RecommendedAction = knowledge?.Entry.RecommendedAction ?? "inspect-rimerror-diagnostic",
            RetryAppropriate = knowledge?.Entry.Retryable,
            ObservedAtUtc = record?.LastOccurrence ?? record?.FirstOccurrence ?? capturedAt,
            EvidenceId = summary.Id,
            Knowledge = knowledge is null
                ? null
                : new RimContextFailureKnowledge
                {
                    SignatureCode = knowledge.Entry.SignatureCode,
                    Status = knowledge.Entry.Status,
                    Confidence = knowledge.Entry.Confidence,
                    KnownCause = knowledge.Entry.KnownCause,
                    RecommendedAction = knowledge.Entry.RecommendedAction,
                    InappropriateActions = knowledge.Entry.InappropriateActions,
                    EvidenceImpact = knowledge.Entry.EvidenceImpact,
                    ResolutionProvenance = knowledge.Entry.ResolutionProvenance,
                    MatchReason = knowledge.MatchReason
                }
        };
    }

    private static string? JoinNonEmpty(params string?[] values)
    {
        string[] present = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return present.Length == 0 ? null : string.Join(" | ", present);
    }

    private AgentObservabilitySnapshot ReadObservability(
        RimContextProviderRequest request,
        IAgentObservabilityStore? suppliedStore)
    {
        AgentObservabilityStore? ownedStore = null;
        IAgentObservabilityStore? store = suppliedStore;
        try
        {
            if (store is null)
            {
                ownedStore = AgentObservabilityStore.CreateDefault();
                store = ownedStore;
            }

            AgentEvent[] events = store.GetEvents(
                    modId: options.ObservabilityModId,
                    limit: request.Verbose ? 512 : 128)
                .ToArray();
            AgentIssue[] issues = store.GetIssues(
                    modId: options.ObservabilityModId,
                    includeRecovered: false,
                    limit: request.MaxFailures)
                .ToArray();
            return ProjectObservability(events, issues, request);
        }
        catch (Exception)
        {
            return AgentObservabilitySnapshot.Empty;
        }
        finally
        {
            ownedStore?.Dispose();
        }
    }

    private static AgentObservabilitySnapshot ProjectObservability(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue> issues,
        RimContextProviderRequest request)
    {
        DateTimeOffset? observed = events
            .Select(EventTime)
            .Where(static value => value.HasValue)
            .Max();
        var executions = events
            .Where(IsExecutionEvent)
            .OrderByDescending(static record => record.Timestamp)
            .ThenByDescending(static record => record.Sequence)
            .Take(request.MaxRecentExecutions)
            .Select(ProjectExecution)
            .ToArray();
        var decisions = events
            .Where(record => record.Type == "test.selection.decision" ||
                record.Type == AgentEventTypes.TestStarted ||
                record.Type == AgentEventTypes.ValidationEvidenceDecision ||
                record.Type == AgentEventTypes.PublicationChecked)
            .OrderByDescending(static record => record.Timestamp)
            .ThenByDescending(static record => record.Sequence)
            .Take(request.MaxDecisions)
            .Select(ProjectDecision)
            .ToArray();
        var evidence = events
            .SelectMany(ProjectEvidence)
            .GroupBy(static value => value.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static value => value.Id, StringComparer.Ordinal)
            .Take(request.MaxExtensions)
            .ToArray();
        RimContextDeploymentState? deployment = ProjectDeployment(events, out DateTimeOffset? deploymentObserved);
        RimContextEfficiencyMetrics? efficiency = ProjectEfficiency(events, decisions);
        var failures = issues
            .OrderByDescending(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .Take(request.MaxFailures)
            .Select(issue => ProjectFailure(issue, events))
            .ToArray();
        TestingProjection testing = ProjectTesting(events, decisions, evidence);
        return new AgentObservabilitySnapshot(
            observed,
            executions,
            decisions,
            evidence,
            failures,
            deployment,
            deploymentObserved,
            efficiency,
            testing);
    }

    private CatalogSnapshot ReadCatalog()
    {
        CatalogLoadResult loaded = CatalogLoader.Load(options.CatalogPath);
        if (loaded.Catalog is null)
        {
            CatalogIssue? issue = loaded.Errors.FirstOrDefault();
            return new CatalogSnapshot(null, issue?.Code ?? "CATALOG_INVALID", issue?.Message);
        }

        CatalogValidationResult validation = CatalogValidator.Validate(loaded.Catalog);
        if (!validation.IsValid)
        {
            CatalogIssue issue = validation.Errors[0];
            return new CatalogSnapshot(null, issue.Code, issue.Message);
        }

        return new CatalogSnapshot(loaded.Catalog, null, null);
    }

    private static bool IsExecutionEvent(AgentEvent record) => record.Type is
        AgentEventTypes.BuildSucceeded or
        AgentEventTypes.BuildFailed or
        AgentEventTypes.BuildDiagnostics or
        AgentEventTypes.TestPassed or
        AgentEventTypes.TestFailed or
        AgentEventTypes.SuiteCompleted or
        AgentEventTypes.ValidationEvidenceRecorded or
        AgentEventTypes.ValidationEvidenceDecision or
        AgentEventTypes.PublicationChecked or
        AgentEventTypes.CommandCompleted or
        AgentEventTypes.CommandFailed or
        AgentEventTypes.RetryCompleted or
        AgentEventTypes.RecoveryCompleted;

    private static RimContextExecution ProjectExecution(AgentEvent record)
    {
        JsonElement data = record.Data.GetValueOrDefault();
        string result = FirstString(data, "result", "status") ??
            (record.Type is AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed or AgentEventTypes.CommandFailed
                ? "failure"
                : "success");
        string mode = GetString(data, "executionMode") ??
            (record.Type is AgentEventTypes.RetryCompleted ? "retried" : "performed");
        return new RimContextExecution
        {
            Operation = GetString(data, "operationKey") ?? record.Type,
            Result = result,
            StartedAtUtc = GetLong(data, "startedAtUtc") is long started
                ? DateTimeOffset.FromUnixTimeMilliseconds(started)
                : null,
            EndedAtUtc = EventTime(record),
            DurationMs = GetLong(data, "durationMs"),
            InputFingerprint = FirstString(data, "sourceFingerprint", "inputFingerprint"),
            ExecutionMode = mode,
            ReasonCode = GetString(data, "errorCode") ?? GetString(data, "reasonCode"),
            TransactionId = GetString(data, "transactionId"),
            Generation = GetInt(data, "generation") ?? GetInt(data, "generationAfter"),
            EvidenceId = FirstString(data, "evidenceId", "outputEvidenceId", "diagnosticEvidenceId"),
            Phase = FirstString(data, "phase", "stage"),
            FailureKind = FirstString(data, "failureKind", "classification"),
            Infrastructure = GetBoolean(data, "infrastructureFailure") ??
                (string.Equals(result, "infrastructure", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : null),
            Retryable = GetBoolean(data, "retryable")
        };
    }

    private static RimContextDecision ProjectDecision(AgentEvent record)
    {
        JsonElement data = record.Data.GetValueOrDefault();
        string action = NormalizeAction(GetString(data, "action") ??
            GetString(data, "publicationAction"));
        string reasonCode = GetString(data, "reasonCode") ??
            GetString(data, "errorCode") ??
            (record.Type == AgentEventTypes.TestStarted ? "TEST_SELECTION_RUN" : "TEST_SELECTION_DECISION");
        string decision = GetString(data, "decision") ??
            (record.Type == AgentEventTypes.PublicationChecked
                ? "publication-evidence"
                : "test-selection");
        string[] changedInputs = GetStringArray(data, "changedInputs", "changedPaths");
        return new RimContextDecision
        {
            Decision = decision,
            Action = action,
            ReasonCode = reasonCode,
            Explanation = GetString(data, "explanation") ?? record.Summary,
            RelevantChangedInputs = changedInputs,
            PreviousEvidence = EvidenceReferences(data, "previousEvidence"),
            EvidenceReused = EvidenceReferences(data, "evidenceReused"),
            EvidenceInvalidated = EvidenceReferences(data, "evidenceInvalidated"),
            DurationMs = GetLong(data, "durationMs"),
            Cost = GetString(data, "cost"),
            Owner = GetString(data, "owner") ?? "RimTest/RimLiaison",
            ObservedAtUtc = EventTime(record)
        };
    }

    private static RimContextFailure ProjectFailure(
        AgentIssue issue,
        IReadOnlyList<AgentEvent> events)
    {
        string category = issue.Category.ToString().ToLowerInvariant();
        string code = string.IsNullOrWhiteSpace(issue.OperationKey)
            ? "RIMLIAISON_" + category.ToUpperInvariant()
            : issue.OperationKey!;
        AgentEvent? supporting = events
            .Where(record => issue.EventIds.Contains(record.Id, StringComparer.Ordinal))
            .OrderByDescending(static record => record.Timestamp)
            .FirstOrDefault();
        JsonElement data = supporting?.Data.GetValueOrDefault() ?? default;
        string? eventClassification = FirstString(data, "classification", "failureKind");
        FailureKnowledgeMatch? knowledge = FailureKnowledgeCatalog.Match(
            code,
            issue.Summary,
            eventClassification ?? category,
            issue.RelatedFiles);
        return new RimContextFailure
        {
            SignatureCode = code,
            OriginatingComponent = FirstString(data, "owner", "component", "tool") ?? "RimLiaison",
            Classification = eventClassification ?? category,
            RootCause = FirstString(data, "rootCause", "knownRootCause") ??
                knowledge?.Entry.KnownCause,
            RecommendedAction = FirstString(data, "nextAction", "recommendedAction") ?? knowledge?.Entry.RecommendedAction ?? issue.RelatedCommands?
                .FirstOrDefault(static command => !string.IsNullOrWhiteSpace(command)) ??
                "inspect-rimliaison-evidence",
            RetryAppropriate = GetBoolean(data, "retryable") ?? knowledge?.Entry.Retryable,
            ObservedAtUtc = issue.Timestamp <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(issue.Timestamp),
            EvidenceId = FirstString(data, "evidenceId", "outputEvidenceId", "diagnosticEvidenceId"),
            RetryAfterStateChange = GetBoolean(data, "retryAfterStateChange"),
            RequiresSourceModification = GetBoolean(data, "requiresSourceModification"),
            InfrastructureOnly = GetBoolean(data, "infrastructureOnly") ??
                GetBoolean(data, "infrastructureFailure"),
            EvidenceInvalidationEffect = FirstString(data, "evidenceInvalidationEffect", "reuseInvalidationReason"),
            Knowledge = knowledge is null
                ? null
                : new RimContextFailureKnowledge
                {
                    SignatureCode = knowledge.Entry.SignatureCode,
                    Status = knowledge.Entry.Status,
                    Confidence = knowledge.Entry.Confidence,
                    KnownCause = knowledge.Entry.KnownCause,
                    RecommendedAction = knowledge.Entry.RecommendedAction,
                    InappropriateActions = knowledge.Entry.InappropriateActions,
                    EvidenceImpact = knowledge.Entry.EvidenceImpact,
                    ResolutionProvenance = knowledge.Entry.ResolutionProvenance,
                    MatchReason = knowledge.MatchReason
                }
        };
    }

    private static RimContextDeploymentState? ProjectDeployment(
        IReadOnlyList<AgentEvent> events,
        out DateTimeOffset? observedAtUtc)
    {
        foreach (AgentEvent record in events
                     .Where(record => record.Type == AgentEventTypes.SuiteCompleted ||
                         record.Type == AgentEventTypes.BuildDiagnostics ||
                         record.Type == AgentEventTypes.BuildSucceeded ||
                         record.Type == AgentEventTypes.BuildFailed)
                     .OrderByDescending(static record => record.Timestamp)
                     .ThenByDescending(static record => record.Sequence))
        {
            JsonElement data = record.Data.GetValueOrDefault();
            JsonElement freshness = GetObject(data, "artifactFreshness");
            string? source = FirstString(data, "sourceFingerprint") ?? GetString(freshness, "sourceFingerprint");
            string? built = FirstString(data, "builtArtifactSha256", "builtSha256") ??
                FirstString(freshness, "builtArtifactSha256", "builtSha256");
            string? deployed = FirstString(data, "deployedArtifactSha256", "deployedSha256") ??
                FirstString(freshness, "deployedArtifactSha256", "deployedSha256");
            string? decision = FirstString(data, "deploymentDecision") ??
                FirstString(freshness, "deploymentDecision", "evaluationStatus");
            if (source is null && built is null && deployed is null && decision is null)
            {
                continue;
            }

            observedAtUtc = EventTime(record);
            string? correspondence = built is null || deployed is null
                ? null
                : string.Equals(built, deployed, StringComparison.OrdinalIgnoreCase)
                    ? "corresponds"
                    : "mismatch";
            return new RimContextDeploymentState
            {
                SourceFingerprint = source,
                BuildArtifactFingerprint = built,
                DeployedArtifactFingerprint = deployed,
                Target = FirstString(data, "stagingPath", "deploymentTarget", "target"),
                Correspondence = correspondence,
                DeploymentDecision = decision ?? GetString(data, "freshnessState"),
                TransactionId = FirstString(data, "transactionId") ?? GetString(freshness, "transactionId"),
                Generation = GetInt(data, "generation") ?? GetInt(data, "generationAfter") ??
                    GetInt(freshness, "generation") ?? GetInt(freshness, "generationAfter"),
                EvidenceId = FirstString(data, "evidenceId", "outputEvidenceId", "diagnosticEvidenceId") ??
                    FirstString(freshness, "evidenceId", "proof")
            };
        }

        observedAtUtc = null;
        return null;
    }

    private static TestingProjection ProjectTesting(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<RimContextDecision> decisions,
        IReadOnlyList<RimContextEvidenceReference> evidence)
    {
        AgentEvent? suite = events
            .Where(static record => record.Type == AgentEventTypes.SuiteCompleted)
            .OrderByDescending(static record => record.Timestamp)
            .ThenByDescending(static record => record.Sequence)
            .FirstOrDefault();
        AgentEvent? selection = events
            .Where(static record => record.Type == "test.selection.decision" ||
                record.Type == AgentEventTypes.TestStarted)
            .OrderByDescending(static record => record.Timestamp)
            .ThenByDescending(static record => record.Sequence)
            .FirstOrDefault();
        JsonElement suiteData = suite?.Data.GetValueOrDefault() ?? default;
        JsonElement selectionData = selection?.Data.GetValueOrDefault() ?? default;
        string[] selectedSuites = GetStringArray(suiteData, "selectedSuites");
        if (selectedSuites.Length == 0)
        {
            selectedSuites = GetStringArray(selectionData, "selectedSuites");
        }

        string? suiteId = FirstString(suiteData, "suiteId");
        if (selectedSuites.Length == 0 && suiteId is not null)
        {
            selectedSuites = [suiteId];
        }

        string[] executedSuites = GetStringArray(suiteData, "executedSuites");
        string[] reusedSuites = GetStringArray(suiteData, "reusedSuites");
        string[] skippedSuites = GetStringArray(suiteData, "skippedSuites");
        string[] selectedTests = GetStringArray(suiteData, "selectedTests");
        if (selectedTests.Length == 0)
        {
            selectedTests = GetStringArray(selectionData, "tests");
        }

        string[] executedTests = GetStringArray(suiteData, "executedTests");
        string[] reusedTests = GetStringArray(suiteData, "reusedTests");
        string[] skippedTests = GetStringArray(suiteData, "skippedTests");
        string? reuseStatus = FirstString(suiteData, "reuseStatus");
        string? cacheStatus = reuseStatus?.ToLowerInvariant() switch
        {
            "used" or "hit" or "reused" => "hit",
            "invalidated" or "mismatch" => "invalidated",
            _ when suite is not null => "miss",
            _ => null
        };
        cacheStatus ??= decisions.Any(static decision => decision.Action == RimContextDecisionActions.Reuse)
            ? "hit"
            : decisions.Any(static decision => decision.Action == RimContextDecisionActions.Run)
                ? "miss"
                : null;
        JsonElement freshness = GetObject(suiteData, "artifactFreshness");
        string? result = FirstString(suiteData, "result", "status");
        bool? infrastructure = GetBoolean(suiteData, "infrastructureFailure") ??
            (string.Equals(result, "infrastructure", StringComparison.OrdinalIgnoreCase) ? true : null);
        RimContextEvidenceReference[] invalidatedEvidence = evidence
            .Where(static reference => string.Equals(reference.Status, "invalidated", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reference.Result, "fail", StringComparison.OrdinalIgnoreCase) ||
                reference.Reusable == false)
            .OrderBy(static reference => reference.Id, StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        return new TestingProjection(
            selectedSuites,
            executedSuites,
            reusedSuites,
            skippedSuites,
            selectedTests,
            executedTests,
            reusedTests,
            skippedTests,
            cacheStatus,
            FirstString(suiteData, "reuseInvalidationReason", "invalidationReason") ??
                decisions.FirstOrDefault(static decision => decision.Action == RimContextDecisionActions.Invalidate)?.ReasonCode,
            result,
            FirstString(suiteData, "sourceFingerprint") ?? GetString(freshness, "sourceFingerprint"),
            FirstString(suiteData, "builtArtifactSha256", "builtSha256") ??
                FirstString(freshness, "builtArtifactSha256", "builtSha256"),
            FirstString(suiteData, "deployedArtifactSha256", "deployedSha256") ??
                FirstString(freshness, "deployedArtifactSha256", "deployedSha256"),
            FirstString(suiteData, "transactionId") ?? GetString(freshness, "transactionId"),
            GetInt(suiteData, "generation") ?? GetInt(freshness, "generation"),
            GetLong(suiteData, "durationMs"),
            infrastructure,
            GetBoolean(suiteData, "retryable"),
            invalidatedEvidence,
            GoldenWorkflowBenchmarkRunner.Summary());
    }

    private static RimContextEfficiencyMetrics? ProjectEfficiency(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<RimContextDecision> decisions)
    {
        if (events.Count == 0)
        {
            return null;
        }

        long? buildMs = DurationFor(events, AgentEventTypes.BuildStarted, AgentEventTypes.BuildSucceeded, AgentEventTypes.BuildFailed);
        long? runtimeMs = DurationFor(events, AgentEventTypes.TestStarted, AgentEventTypes.TestPassed, AgentEventTypes.TestFailed);
        int retries = events.Count(record => record.Type is AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted);
        int cacheHits = decisions.Count(static decision => decision.Action == RimContextDecisionActions.Reuse);
        int cacheMisses = decisions.Count(static decision => decision.Action == RimContextDecisionActions.Run);
        int buildCount = events.Count(record => record.Type is AgentEventTypes.BuildSucceeded or AgentEventTypes.BuildFailed);
        int deploymentCount = events.Count(record =>
            record.Type == AgentEventTypes.SuiteCompleted &&
            GetObject(record.Data.GetValueOrDefault(), "artifactFreshness").ValueKind == JsonValueKind.Object);
        int testCount = events.Count(record => record.Type == AgentEventTypes.TestStarted);
        int executedTestCount = events
            .Where(static record => record.Type == AgentEventTypes.SuiteCompleted)
            .Sum(record => GetStringArray(record.Data.GetValueOrDefault(), "executedTests").Length);
        int invalidatedEvidenceCount = decisions.Count(static decision =>
            decision.Action == RimContextDecisionActions.Invalidate);
        int expensiveOperationCount = buildCount + deploymentCount + testCount;
        return new RimContextEfficiencyMetrics
        {
            BuildMs = buildMs,
            BuildCount = buildCount,
            DeploymentCount = deploymentCount,
            TestCount = testCount,
            ExecutedTestCount = executedTestCount,
            RuntimeTestMs = runtimeMs,
            TotalWorkflowMs = buildMs.HasValue || runtimeMs.HasValue
                ? (buildMs ?? 0) + (runtimeMs ?? 0)
                : null,
            CacheHits = cacheHits,
            CacheMisses = cacheMisses,
            ReusedEvidenceCount = cacheHits,
            InvalidatedEvidenceCount = invalidatedEvidenceCount,
            ObservedPerformance = ProjectObservedPerformance(events),
            BenchmarkSummary = GoldenWorkflowBenchmarkRunner.Summary(),
            RimWorldLaunches = events
                .Select(record => GetInt(record.Data.GetValueOrDefault(), "launchesConsumed"))
                .Where(static count => count.HasValue)
                .Sum(static count => count!.Value),
            RimWorldRestarts = events.Count(record => GetBoolean(record.Data.GetValueOrDefault(), "restartRequired") == true),
            Retries = retries
        };
    }

    public static RimContextObservedPerformanceSummary ProjectObservedPerformance(
        IReadOnlyList<AgentEvent> events)
    {
        ObservedWorkflow[] workflows = events
            .GroupBy(static record => record.RunId, StringComparer.Ordinal)
            .Select(ProjectObservedWorkflow)
            .Where(static workflow => workflow is not null)
            .Select(static workflow => workflow!)
            .OrderByDescending(static workflow => workflow.EndedAt)
            .Take(32)
            .ToArray();
        if (workflows.Length == 0)
        {
            return new RimContextObservedPerformanceSummary
            {
                Status = "insufficient-data",
                SampleCount = 0
            };
        }

        long[] durations = workflows
            .Select(static workflow => workflow.DurationMs)
            .OrderBy(static duration => duration)
            .ToArray();
        int runtimeCount = workflows.Count(static workflow => workflow.RuntimeRequired);
        int reuseCount = workflows.Count(static workflow => workflow.ReusedValidation);
        int retryCount = workflows.Count(static workflow => workflow.InfrastructureRetry);
        string? topFailure = workflows
            .Select(static workflow => workflow.FailureClassification)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value!, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group.Key)
            .FirstOrDefault();
        return new RimContextObservedPerformanceSummary
        {
            Status = workflows.Length >= 2 ? "available" : "insufficient-data",
            SampleCount = workflows.Length,
            MedianWorkflowDurationMs = workflows.Length >= 2 ? Percentile(durations, 0.50) : null,
            P90WorkflowDurationMs = workflows.Length >= 2 ? Percentile(durations, 0.90) : null,
            ValidationReuseRate = workflows.Length >= 2 ? (double)reuseCount / workflows.Length : null,
            AverageExpensiveOperations = workflows.Length >= 2
                ? workflows.Average(static workflow => workflow.ExpensiveOperations)
                : null,
            RuntimeLaunchesPerRuntimeWorkflow = runtimeCount >= 2
                ? (double)workflows.Where(static workflow => workflow.RuntimeRequired)
                    .Sum(static workflow => workflow.RuntimeLaunches) / runtimeCount
                : null,
            InfrastructureRetryRate = workflows.Length >= 2
                ? (double)retryCount / workflows.Length
                : null,
            TopFailureClassification = topFailure
        };
    }

    private static ObservedWorkflow? ProjectObservedWorkflow(
        IEnumerable<AgentEvent> grouped)
    {
        AgentEvent[] records = grouped
            .OrderBy(static record => record.Timestamp)
            .ThenBy(static record => record.Sequence)
            .ToArray();
        AgentEvent? started = records.FirstOrDefault(record =>
            record.Type == AgentEventTypes.CommandStarted);
        AgentEvent? completed = records.LastOrDefault(record =>
            record.Type == AgentEventTypes.CommandCompleted);
        string? command = FirstString(
            started?.Data.GetValueOrDefault() ?? default,
            "command") ??
            FirstString(
                completed?.Data.GetValueOrDefault() ?? default,
                "command");
        if (started is null || completed is null ||
            command is not ("affected" or "rimdev" or "publish" or "build" or "deploy" or "test"))
        {
            return null;
        }

        long duration = GetLong(
                completed.Data.GetValueOrDefault(),
                "durationMs") ??
            Math.Max(0, completed.Timestamp - started.Timestamp);
        bool reused = records.Any(record =>
            record.Type == AgentEventTypes.ValidationEvidenceDecision &&
            GetString(record.Data.GetValueOrDefault(), "action") == RimContextDecisionActions.Reuse) ||
            records.Any(record =>
                record.Type == AgentEventTypes.PublicationChecked &&
                GetString(record.Data.GetValueOrDefault(), "publicationAction") == "reuse");
        bool runtime = records.Any(record =>
            record.Type == AgentEventTypes.SuiteCompleted &&
            (GetObject(record.Data.GetValueOrDefault(), "artifactFreshness").ValueKind == JsonValueKind.Object ||
             GetBoolean(record.Data.GetValueOrDefault(), "requiresRuntime") == true));
        int expensive = records.Count(record =>
            record.Type is AgentEventTypes.BuildSucceeded or
                AgentEventTypes.BuildFailed or
                AgentEventTypes.SuiteCompleted or
                AgentEventTypes.TestStarted);
        int launches = records
            .Select(record => GetInt(record.Data.GetValueOrDefault(), "launchesConsumed"))
            .Where(static value => value.HasValue)
            .Sum(static value => value!.Value);
        string? failure = records
            .Where(record => record.Type is AgentEventTypes.CommandFailed or
                AgentEventTypes.BuildFailed or
                AgentEventTypes.TestFailed or
                AgentEventTypes.IntegrationFailed)
            .Select(record => FirstString(record.Data.GetValueOrDefault(), "errorCode", "code") ?? record.Type)
            .FirstOrDefault();
        return new ObservedWorkflow(
            completed.Timestamp,
            duration,
            runtime,
            reused,
            records.Any(record => record.Type == AgentEventTypes.RetryStarted),
            expensive,
            launches,
            failure);
    }

    private static long Percentile(IReadOnlyList<long> values, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(values.Count * percentile) - 1,
            0,
            values.Count - 1);
        return values[index];
    }

    private sealed record ObservedWorkflow(
        long EndedAt,
        long DurationMs,
        bool RuntimeRequired,
        bool ReusedValidation,
        bool InfrastructureRetry,
        int ExpensiveOperations,
        int RuntimeLaunches,
        string? FailureClassification);

    private static long? DurationFor(
        IReadOnlyList<AgentEvent> events,
        string startType,
        params string[] endTypes)
    {
        AgentEvent? start = events
            .Where(record => record.Type == startType)
            .OrderByDescending(static record => record.Timestamp)
            .FirstOrDefault();
        AgentEvent? end = events
            .Where(record => endTypes.Contains(record.Type, StringComparer.Ordinal) &&
                start is not null && record.Timestamp >= start.Timestamp)
            .OrderBy(static record => record.Timestamp)
            .FirstOrDefault();
        if (start is null || end is null || end.Timestamp < start.Timestamp)
        {
            return null;
        }

        return end.Timestamp - start.Timestamp;
    }

    private static IEnumerable<RimContextEvidenceReference> ProjectEvidence(AgentEvent record)
    {
        JsonElement data = record.Data.GetValueOrDefault();
        if (ValidationEvidenceParser.TryParse(record, out ValidationEvidenceRecord? validationEvidence) &&
            validationEvidence is not null)
        {
            yield return new RimContextEvidenceReference
            {
                Id = validationEvidence.EvidenceId,
                Kind = validationEvidence.Identity.ValidationKind,
                Fingerprint = validationEvidence.Identity.ComputeFingerprint(),
                Status = validationEvidence.Result == "pass" && validationEvidence.Reusable
                    ? "available"
                    : validationEvidence.Result == "fail"
                        ? "failure"
                        : "invalidated",
                ValidationKind = validationEvidence.Identity.ValidationKind,
                Result = validationEvidence.Result,
                Reusable = validationEvidence.Reusable,
                SourceFingerprint = validationEvidence.Identity.ContentFingerprint,
                BuildArtifactFingerprint = validationEvidence.Identity.BuildArtifactSha256,
                DeploymentArtifactFingerprint = validationEvidence.Identity.DeploymentArtifactSha256,
                SuiteId = validationEvidence.Identity.SuiteId,
                TestIds = validationEvidence.Identity.TestIds,
                RuntimeGeneration = validationEvidence.Identity.RuntimeGeneration,
                RequiresRuntimeGeneration = validationEvidence.Identity.RequiresRuntimeGeneration,
                DeploymentCorrespondence = validationEvidence.Identity.DeploymentCorrespondence,
                RecordedAtUtc = validationEvidence.RecordedAtUtc
            };
        }

        foreach (RimContextEvidenceReference reference in EvidenceReferences(data, "evidenceReused"))
        {
            yield return reference with { Status = "available" };
        }

        foreach (RimContextEvidenceReference reference in EvidenceReferences(data, "evidenceInvalidated"))
        {
            yield return reference with { Status = "invalidated" };
        }

        JsonElement freshness = GetObject(data, "artifactFreshness");
        string? evaluation = FirstString(freshness, "evaluationStatus");
        string? reuseStatus = FirstString(data, "reuseStatus");
        string evidenceStatus = (string.Equals(evaluation, "STALE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evaluation, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reuseStatus, "invalidated", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reuseStatus, "mismatch", StringComparison.OrdinalIgnoreCase))
            ? "invalidated"
            : record.Type is AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed
                ? "failure"
                : "available";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((JsonElement source, string name) in new[]
        {
            (data, "evidenceId"),
            (data, "outputEvidenceId"),
            (data, "diagnosticEvidenceId"),
            (data, "errorOutputEvidenceId"),
            (freshness, "evidenceId"),
            (freshness, "proof")
        })
        {
            string? value = GetString(source, name);
            if (value is not null && seen.Add(value))
            {
                yield return new RimContextEvidenceReference
                {
                    Id = value,
                    Kind = name,
                    Status = evidenceStatus
                };
            }
        }
    }

    private static IReadOnlyList<RimContextEvidenceReference> EvidenceReferences(
        JsonElement data,
        string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? new RimContextEvidenceReference
                {
                    Id = item.GetString() ?? "unknown",
                    Kind = propertyName,
                    Status = propertyName == "evidenceInvalidated" ? "invalidated" : "available"
                }
                : item.ValueKind == JsonValueKind.Object
                    ? new RimContextEvidenceReference
                    {
                        Id = GetString(item, "id") ?? "unknown",
                        Kind = GetString(item, "kind"),
                        Fingerprint = GetString(item, "fingerprint"),
                        Status = GetString(item, "status")
                    }
                    : new RimContextEvidenceReference { Id = "unknown" })
            .Where(static reference => reference.Id != "unknown")
            .OrderBy(static reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] GetStringArray(JsonElement data, params string[] names)
    {
        foreach (string name in names)
        {
            if (data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString()!)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        return [];
    }

    private static string NormalizeAction(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        RimContextDecisionActions.Run => RimContextDecisionActions.Run,
        RimContextDecisionActions.Reuse => RimContextDecisionActions.Reuse,
        RimContextDecisionActions.Skip => RimContextDecisionActions.Skip,
        RimContextDecisionActions.Invalidate => RimContextDecisionActions.Invalidate,
        RimContextDecisionActions.Retry => RimContextDecisionActions.Retry,
        RimContextDecisionActions.Block => RimContextDecisionActions.Block,
        _ => RimContextDecisionActions.Run
    };

    private static RimContextRuntimeIdentity? ParseRuntimeIdentity(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RimContextRuntimeIdentity
        {
            DevBridgeSourceRoot = GetString(data, "devBridgeSourceRoot"),
            DevBridgeRuntimeRoot = GetString(data, "devBridgeRuntimeRoot"),
            DevBridgePinnedWorktreeRoot = GetString(data, "devBridgePinnedWorktreeRoot"),
            RimWorldRoot = GetString(data, "rimWorldRoot"),
            RimWorldExecutable = GetString(data, "rimWorldExecutable"),
            ResolutionSource = GetString(data, "resolutionSource"),
            RimWorldRootExists = GetBoolean(data, "rimWorldRootExists"),
            RimWorldExecutableExists = GetBoolean(data, "rimWorldExecutableExists"),
            DevBridgeRuntimeRootExists = GetBoolean(data, "devBridgeRuntimeRootExists"),
            InstalledRuntimeLayoutValid = GetBoolean(data, "installedRuntimeLayoutValid"),
            RuntimeBelongsToRimWorld = GetBoolean(data, "runtimeBelongsToRimWorld"),
            ErrorCode = GetString(data, "errorCode"),
            NextAction = GetString(data, "nextAction")
        };
    }

    private static string? GetString(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static long? GetLong(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long result)
            ? result
            : null;

    private static bool? GetBoolean(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static JsonElement GetObject(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? FirstString(JsonElement data, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = GetString(data, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? FirstInt(JsonElement data, params string[] names)
    {
        foreach (string name in names)
        {
            int? value = GetInt(data, name);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool? FirstBoolean(JsonElement data, params string[] names)
    {
        foreach (string name in names)
        {
            bool? value = GetBoolean(data, name);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetNestedString(JsonElement data, string parent, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(parent, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? GetString(value, name)
            : null;

    private static int? GetNestedInt(JsonElement data, string parent, string name) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(parent, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? GetInt(value, name)
            : null;

    private static DateTimeOffset? EventTime(AgentEvent record) =>
        record.Timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(record.Timestamp) : null;

    private static RimContextRepositoryState ToRepositoryState(
        GitRepositoryStateSnapshot state,
        string? component = null,
        bool verbose = true)
    {
        IEnumerable<GitRepositoryChange> meaningfulChanges = state.Changes
            .Where(static change => !change.Generated)
            .OrderBy(static change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.Status, StringComparer.Ordinal);
        IEnumerable<GitRepositoryChange> generatedChanges = state.Changes
            .Where(static change => change.Generated)
            .OrderBy(static change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static change => change.Status, StringComparer.Ordinal);
        if (!verbose)
        {
            meaningfulChanges = meaningfulChanges.Take(8);
            generatedChanges = generatedChanges.Take(8);
        }

        RimContextChangedFile[] changes = meaningfulChanges.Select(ToChangedFile).ToArray();
        RimContextChangedFile[] generated = generatedChanges.Select(ToChangedFile).ToArray();
        return new RimContextRepositoryState
        {
            Component = component,
            Identity = state.Identity,
            LocalPath = verbose ? state.RootPath : null,
            Branch = state.Branch,
            HeadSha = state.HeadSha,
            UpstreamSha = state.UpstreamSha,
            UpstreamBranch = state.UpstreamName,
            Ahead = state.Ahead,
            Behind = state.Behind,
            Dirty = state.Dirty,
            SourceFingerprint = state.SourceFingerprint,
            ChangedFiles = changes,
            GeneratedFiles = generated
        };
    }

    private static RimContextChangedFile ToChangedFile(GitRepositoryChange change) => new()
    {
        Path = change.Path,
        Status = change.Status,
        Category = change.Generated ? "generated" : CategoryFor(change.Path),
        Untracked = change.Untracked,
        OriginalPath = change.OriginalPath
    };

    private static string CategoryFor(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return "documentation";
        }

        if (normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("contracts/", StringComparison.OrdinalIgnoreCase))
        {
            return "tooling";
        }

        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            return "source";
        }

        if (normalized.StartsWith(".rimdev/", StringComparison.OrdinalIgnoreCase))
        {
            return "configuration";
        }

        return "other";
    }

    private static RimContextSection<T> Available<T>(T value, string provider, DateTimeOffset observedAtUtc) => new()
    {
        Status = RimContextBundleStatuses.Available,
        Value = value,
        Provider = provider,
        ObservedAtUtc = observedAtUtc
    };

    private static RimContextSection<T> Unknown<T>(
        string reasonCode,
        string message,
        string provider) => new()
        {
            Status = RimContextBundleStatuses.Unknown,
            Provider = provider,
            ReasonCode = reasonCode,
            Message = message
        };

    private static RimContextSection<T> Unavailable<T>(
        string reasonCode,
        string message,
        string provider,
        string? nextAction = null) => new()
        {
            Status = RimContextBundleStatuses.Unavailable,
            Provider = provider,
            ReasonCode = reasonCode,
            Message = message,
            NextAction = nextAction
        };

    private static RimContextExtension Extension(string provider, string key, object value) => new()
    {
        Provider = provider,
        Key = key,
        Value = JsonSerializer.SerializeToElement(value)
    };

    private static string? ExistingPath(string path) => Directory.Exists(path) ? Path.GetFullPath(path) : null;

    private string DisplayPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(root, full).Replace('\\', '/');
            }

            return full.Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return path.Replace('\\', '/');
        }
    }

    private static string AssemblyVersion(Assembly assembly) =>
        assembly.GetName().Version?.ToString() ?? "unknown";

    private static string? AssemblyVersionByName(string name)
    {
        try
        {
            return Assembly.Load(name).GetName().Version?.ToString();
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or
            BadImageFormatException)
        {
            return null;
        }
    }

    private sealed record CatalogSnapshot(CatalogDocument? State, string? ErrorCode, string? Message);

    private sealed record RimErrorProjection(
        IReadOnlyList<RimContextFailure> Failures,
        string Status);

    private sealed record AgentObservabilitySnapshot(
        DateTimeOffset? ObservedAtUtc,
        IReadOnlyList<RimContextExecution> Executions,
        IReadOnlyList<RimContextDecision> Decisions,
        IReadOnlyList<RimContextEvidenceReference> Evidence,
        IReadOnlyList<RimContextFailure> Failures,
        RimContextDeploymentState? Deployment,
        DateTimeOffset? DeploymentObservedAtUtc,
        RimContextEfficiencyMetrics? Efficiency,
        TestingProjection Testing)
    {
        public IReadOnlyList<string> SelectedSuites => Testing.SelectedSuites;
        public IReadOnlyList<string> ExecutedSuites => Testing.ExecutedSuites;
        public IReadOnlyList<string> ReusedSuites => Testing.ReusedSuites;
        public IReadOnlyList<string> SkippedSuites => Testing.SkippedSuites;
        public IReadOnlyList<string> SelectedTests => Testing.SelectedTests;
        public IReadOnlyList<string> ExecutedTests => Testing.ExecutedTests;
        public IReadOnlyList<string> ReusedTests => Testing.ReusedTests;
        public IReadOnlyList<string> SkippedTests => Testing.SkippedTests;
        public IReadOnlyList<RimContextEvidenceReference> InvalidatedEvidence => Testing.InvalidatedEvidence;
        public RimContextBenchmarkSummary BenchmarkSummary => Testing.BenchmarkSummary;
        public string? CacheStatus => Testing.CacheStatus;
        public string? InvalidationReason => Testing.InvalidationReason;
        public string? LatestResult => Testing.LatestResult;
        public string? LatestSourceFingerprint => Testing.LatestSourceFingerprint;
        public string? LatestBuildArtifactFingerprint => Testing.LatestBuildArtifactFingerprint;
        public string? LatestDeploymentArtifactFingerprint => Testing.LatestDeploymentArtifactFingerprint;
        public string? LatestTransactionId => Testing.LatestTransactionId;
        public int? LatestGeneration => Testing.LatestGeneration;
        public long? LatestDurationMs => Testing.LatestDurationMs;
        public bool? InfrastructureFailure => Testing.InfrastructureFailure;
        public bool? Retryable => Testing.Retryable;

        public static AgentObservabilitySnapshot Empty { get; } = new(
            null,
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            TestingProjection.Empty);
    }

    private sealed record TestingProjection(
        IReadOnlyList<string> SelectedSuites,
        IReadOnlyList<string> ExecutedSuites,
        IReadOnlyList<string> ReusedSuites,
        IReadOnlyList<string> SkippedSuites,
        IReadOnlyList<string> SelectedTests,
        IReadOnlyList<string> ExecutedTests,
        IReadOnlyList<string> ReusedTests,
        IReadOnlyList<string> SkippedTests,
        string? CacheStatus,
        string? InvalidationReason,
        string? LatestResult,
        string? LatestSourceFingerprint,
        string? LatestBuildArtifactFingerprint,
        string? LatestDeploymentArtifactFingerprint,
        string? LatestTransactionId,
        int? LatestGeneration,
        long? LatestDurationMs,
        bool? InfrastructureFailure,
        bool? Retryable,
        IReadOnlyList<RimContextEvidenceReference> InvalidatedEvidence,
        RimContextBenchmarkSummary BenchmarkSummary)
    {
        public static TestingProjection Empty { get; } = new(
            SelectedSuites: [],
            ExecutedSuites: [],
            ReusedSuites: [],
            SkippedSuites: [],
            SelectedTests: [],
            ExecutedTests: [],
            ReusedTests: [],
            SkippedTests: [],
            CacheStatus: null,
            InvalidationReason: null,
            LatestResult: null,
            LatestSourceFingerprint: null,
            LatestBuildArtifactFingerprint: null,
            LatestDeploymentArtifactFingerprint: null,
            LatestTransactionId: null,
            LatestGeneration: null,
            LatestDurationMs: null,
            InfrastructureFailure: null,
            Retryable: null,
            InvalidatedEvidence: [],
            BenchmarkSummary: GoldenWorkflowBenchmarkRunner.Summary());
    }

    private sealed record RuntimeProbe(
        RimContextRuntimeState? State,
        string Status,
        string? ErrorCode,
        string? Message,
        string? NextAction,
        DateTimeOffset? ObservedAtUtc,
        IReadOnlyDictionary<string, string> ComponentVersions,
        string? RootPath = null,
        RimContextRuntimeIdentity? RuntimeIdentity = null)
    {
        public static RuntimeProbe Unavailable(
            string code,
            string message,
            string? nextAction = null) => new(
                null,
                RimContextBundleStatuses.Unavailable,
                code,
                message,
                nextAction,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                null);
    }
}
