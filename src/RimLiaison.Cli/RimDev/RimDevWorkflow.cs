using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using RimContext.Core;
using RimContext.Core.Contracts;
using RimContext.Core.Semantics;
using RimLiaison.DevBridge;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Provenance;


namespace RimLiaison.RimDev;
public sealed class RimDevWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IRimDevGitClient git;
    private readonly IRimDevProcessRunner processRunner;
    private readonly IRimDevPullRequestProvider pullRequests;
    private readonly RimDevGitReader reader;
    private readonly RimDevBuildEvidenceStore evidenceStore;
    private readonly IAgentObservabilityStore observabilityStore;
    private readonly Dictionary<string, RimDevRepositoryObservation> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> fetched = new(StringComparer.OrdinalIgnoreCase);

    public RimDevWorkflow(
        IRimDevGitClient? git = null,
        IRimDevProcessRunner? processRunner = null,
        IRimDevPullRequestProvider? pullRequests = null,
        IGitRepositoryStateProvider? stateProvider = null,
        string? stateDirectory = null,
        IAgentObservabilityStore? observabilityStore = null)
    {
        this.processRunner = processRunner ?? new SystemRimDevProcessRunner();
        this.git = git ?? new SystemRimDevGitClient(this.processRunner);
        this.pullRequests = pullRequests ?? new SystemRimDevPullRequestProvider(this.processRunner);
        reader = new RimDevGitReader(this.git, stateProvider);
        evidenceStore = new RimDevBuildEvidenceStore(stateDirectory);
        this.observabilityStore = observabilityStore ?? AgentObservabilityStore.CreateDefault();
    }

    public async Task<int> RunAsync(
        RimDevRunOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (options.Operation == RimDevOperation.Menu)
        {
            RimDevHumanUi.WriteMenu(stdout);
            return CliExitCodes.Success;
        }

        if (options.Operation == RimDevOperation.Help)
        {
            RimDevHumanUi.WriteHelp(stdout);
            return CliExitCodes.Success;
        }

        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(
            options.RootPath,
            Environment.CurrentDirectory);
        if (!workspace.Succeeded)
        {
            RimDevResult failure = new(
                options.Operation.ToString().ToLowerInvariant(),
                "blocked",
                [],
                [workspace.Error ?? "The rimdev workspace could not be resolved."],
                ErrorCode: workspace.ErrorCode ?? "RIMDEV_WORKSPACE_INVALID");
            Write(failure, options.Json, stdout);
            return CliExitCodes.ConservativeSelection;
        }

        RimDevResult result;
        try
        {
            result = options.Operation switch
            {
                RimDevOperation.Status => await StatusAsync(workspace, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Sync => await SyncAsync(workspace, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Build => await BuildAsync(workspace, null, false, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Test => await TestAsync(workspace, null, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Deploy => await DeployAsync(workspace, null, false, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Push => await PushAsync(workspace, null, cancellationToken).ConfigureAwait(false),
                RimDevOperation.Merge => await MergeAsync(
                        workspace,
                        options.Confirm,
                        options.Json ? null : stdout,
                        options.Json ? null : options.Input ?? Console.In,
                        cancellationToken)
                    .ConfigureAwait(false),
                RimDevOperation.All => await AllAsync(workspace, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported rimdev operation.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(
                options.Operation.ToString().ToLowerInvariant(),
                "blocked",
                [],
                ["The rimdev operation was cancelled."],
                ErrorCode: "RIMDEV_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException)
        {
            result = new(
                options.Operation.ToString().ToLowerInvariant(),
                "blocked",
                [],
                [Bound(exception.Message) ?? "The rimdev operation failed."],
                ErrorCode: "RIMDEV_OPERATION_FAILED");
        }

        Write(result, options.Json, stdout);
        return result.Status switch
        {
            "ok" => CliExitCodes.Success,
            "failed" => CliExitCodes.TestFailure,
            _ => CliExitCodes.ConservativeSelection
        };
    }

    private async Task<RimDevResult> StatusAsync(
        RimDevWorkspaceDiscovery workspace,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            RimDevRepositoryObservation observation = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State));
                continue;
            }

            GitRepositoryStateSnapshot state = observation.State;
            bool attention = state.Dirty || state.UpstreamName is null ||
                state.Ahead is > 0 || state.Behind is > 0 || !repository.Manifest.IsValid;
            string merge = await MergeReadinessAsync(repository, state, cancellationToken).ConfigureAwait(false);
            results.Add(new(
                repository.Name,
                repository.Path,
                attention ? "attention" : "ready",
                attention ? "attention required" : "ready",
                state.Branch,
                state.Dirty,
                state.Ahead,
                state.Behind,
                state.UpstreamName,
                CachedBuildStatus(repository, state),
                CheapDeploymentStatus(repository, workspace.RootPath),
                merge,
                NextAction: attention
                    ? StatusNextAction(repository, state)
                    : string.Equals(merge, "ready", StringComparison.OrdinalIgnoreCase)
                        ? "Approved work is ready to merge; review it with rimdev merge."
                        : null));
        }

        return Aggregate("status", results);
    }

    private async Task<RimDevResult> SyncAsync(
        RimDevWorkspaceDiscovery workspace,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            RimDevRepositoryObservation observation = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State));
                continue;
            }

            RimDevRepositoryResult? fetchFailure = await EnsureFetchedAsync(observation, cancellationToken).ConfigureAwait(false);
            if (fetchFailure is not null)
            {
                results.Add(fetchFailure);
                continue;
            }

            observation = await RefreshAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State));
                continue;
            }

            RimDevPolicyDecision decision = RimDevGitPolicy.DecideSync(observation.State);
            if (!decision.Allowed)
            {
                results.Add(Blocked(repository, decision.ErrorCode!, decision.Explanation, observation.State));
            }
            else if (decision.Action == "fast-forward")
            {
                RimDevGitResult merge = await git.RunAsync(repository.Path, ["merge", "--ff-only", "@{upstream}"], cancellationToken).ConfigureAwait(false);
                if (!merge.Succeeded)
                {
                    results.Add(Blocked(repository, "GIT_FAST_FORWARD_FAILED", "The remote changed while synchronizing; inspect the branch and retry.", observation.State));
                }
                else
                {
                    observation = await RefreshAsync(repository, cancellationToken).ConfigureAwait(false);
                    if (observation.State is null || observation.ErrorCode is not null)
                    {
                        results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State));
                    }
                    else
                    {
                        results.Add(Success(repository, "synchronized", "fast-forwarded safely", observation.State));
                    }
                }
            }
            else
            {
                results.Add(Success(
                    repository,
                    "current",
                    observation.State.Dirty
                        ? "already synchronized; uncommitted files were left untouched"
                        : "already synchronized",
                    observation.State));
            }
        }

        return Aggregate("sync", results);
    }

    private async Task<RimDevResult> BuildAsync(
        RimDevWorkspaceDiscovery workspace,
        RimDevInvocationContext? context,
        bool reuseValidation,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations =
            await ObserveAllAsync(workspace, cancellationToken).ConfigureAwait(false);
        HashSet<string> dependencyAffected = ComputeDependencyAffected(workspace.Repositories, observations);
        IReadOnlyList<RimDevRepository> ordered = OrderRepositories(workspace.Repositories, out HashSet<string> cycles);
        var completed = new Dictionary<string, RimDevRepositoryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (RimDevRepository repository in ordered)
        {
            if (cycles.Contains(repository.Name))
            {
                RimDevRepositoryResult result = Blocked(repository, "RIMDEV_DEPENDENCY_CYCLE", "Repository build dependencies contain a cycle.");
                results.Add(result);
                completed[repository.Name] = result;
                continue;
            }

            if (repository.Dependencies.Any(dependency =>
                    completed.TryGetValue(dependency, out RimDevRepositoryResult? dependencyResult) &&
                    dependencyResult.Status is "blocked" or "fail"))
            {
                RimDevRepositoryResult result = Blocked(repository, "RIMDEV_DEPENDENCY_BLOCKED", "A required repository did not complete; no build was attempted.");
                results.Add(result);
                completed[repository.Name] = result;
                continue;
            }

            RimDevRepositoryObservation observation = observations[repository.Name];
            if (observation.State is null || observation.ErrorCode is not null)
            {
                RimDevRepositoryResult result = Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State);
                results.Add(result);
                completed[repository.Name] = result;
                continue;
            }

            if (reuseValidation && context?.TestResults.TryGetValue(repository.Path, out RimDevTestExecutionResult? test) == true &&
                test.Succeeded && test.ArtifactFreshnessProven)
            {
                RimDevRepositoryResult result = Success(repository, "reused", "reused the validated RimTest build/artifact transaction", observation.State);
                results.Add(result);
                completed[repository.Name] = result;
                continue;
            }

            bool dependencyChanged = dependencyAffected.Contains(repository.Name);
            if (observation.BuildPaths.Count == 0 && !dependencyChanged)
            {
                RimDevRepositoryResult result = Success(
                    repository,
                    "skip",
                    observation.MeaningfulPaths.Count == 0 ? "no source changes" : "no build inputs changed",
                    observation.State);
                results.Add(result);
                completed[repository.Name] = result;
                continue;
            }

            string? dependencyFingerprint = ComputeDependencyFingerprint(repository, observations);
            RimDevBuildExecutionResult build = await BuildRepositoryAsync(repository, observation, dependencyFingerprint, cancellationToken).ConfigureAwait(false);
            RimDevRepositoryResult buildResult = build.Succeeded
                ? Success(repository, "pass", dependencyChanged && observation.BuildPaths.Count == 0
                    ? build.Summary + "; selected because a dependency changed"
                    : build.Summary, observation.State, build: "PASS")
                : Failed(repository, build.ErrorCode ?? "RIMDEV_BUILD_FAILED", build.Summary, observation.State, build: "FAIL");
            results.Add(buildResult);
            completed[repository.Name] = buildResult;
        }

        return Aggregate("build", results);
    }

    private async Task<RimDevResult> TestAsync(
        RimDevWorkspaceDiscovery workspace,
        RimDevInvocationContext? context,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations =
            await ObserveAllAsync(workspace, cancellationToken).ConfigureAwait(false);
        HashSet<string> dependencyAffected = ComputeDependencyAffected(workspace.Repositories, observations);
        IReadOnlyList<RimDevRepository> ordered = OrderRepositories(workspace.Repositories, out HashSet<string> cycles);
        var completed = new Dictionary<string, RimDevRepositoryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (RimDevRepository repository in ordered)
        {
            if (cycles.Contains(repository.Name))
            {
                RimDevRepositoryResult cycle = Blocked(repository, "RIMDEV_DEPENDENCY_CYCLE", "Repository test dependencies contain a cycle.");
                results.Add(cycle);
                completed[repository.Name] = cycle;
                if (context is not null)
                {
                    context.TestResults[repository.Path] = new(false, "blocked", cycle.Summary, false, [], cycle.ErrorCode);
                }

                continue;
            }

            if (repository.Dependencies.Any(dependency =>
                    completed.TryGetValue(dependency, out RimDevRepositoryResult? dependencyResult) &&
                    dependencyResult.Status is "blocked" or "fail"))
            {
                RimDevRepositoryResult blocked = Blocked(repository, "RIMDEV_DEPENDENCY_BLOCKED", "A required repository did not complete; no tests were attempted.");
                results.Add(blocked);
                completed[repository.Name] = blocked;
                if (context is not null)
                {
                    context.TestResults[repository.Path] = new(false, "blocked", blocked.Summary, false, [], blocked.ErrorCode);
                }

                continue;
            }

            RimDevRepositoryObservation observation = observations[repository.Name];
            if (observation.State is null || observation.ErrorCode is not null)
            {
                RimDevRepositoryResult blocked = Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State);
                results.Add(blocked);
                completed[repository.Name] = blocked;
                if (context is not null)
                {
                    context.TestResults[repository.Path] = new(false, "blocked", blocked.Summary, false, [], blocked.ErrorCode);
                }

                continue;
            }

            bool dependencyChanged = dependencyAffected.Contains(repository.Name);
            if (observation.TestPaths.Count == 0 && !dependencyChanged)
            {
                RimDevTestExecutionResult skipped = new(
                    true,
                    "skip",
                    observation.MeaningfulPaths.Count == 0 ? "no source changes" : "no test-relevant changes",
                    false,
                    []);
                if (context is not null)
                {
                    context.TestResults[repository.Path] = skipped;
                }

                RimDevRepositoryResult skippedResult = Success(repository, "skip", skipped.Summary, observation.State);
                results.Add(skippedResult);
                completed[repository.Name] = skippedResult;
                continue;
            }

            IReadOnlyDictionary<string, string>? dependencyFingerprints =
                ComputeDependencyFingerprints(repository, observations);
            ValidationPublicationCheck canonical = ValidationPublicationChecker.Evaluate(
                observation.State,
                ToPublicationChanges(observation.ChangedPaths),
                observabilityStore,
                repository.Manifest.Project ?? repository.Name,
                PublicationConfiguration(repository),
                dependencyFingerprints: dependencyFingerprints);
            if (canonical.Result.SafeToPublish && canonical.Result.ReusedEvidenceCount > 0)
            {
                bool runtime = canonical.Analysis.RequiresRuntime;
                RimDevTestExecutionResult canonicalReuse = new(
                    true,
                    runtime ? "reused" : "skip",
                    "reused canonical RimTest publication evidence",
                    runtime,
                    runtime ? ["canonical validation evidence"] : []);
                if (context is not null)
                {
                    context.TestResults[repository.Path] = canonicalReuse;
                }

                RimDevRepositoryResult canonicalResult = Success(
                    repository,
                    canonicalReuse.Status,
                    canonicalReuse.Summary,
                    observation.State);
                results.Add(canonicalResult);
                completed[repository.Name] = canonicalResult;
                continue;
            }

            string command = ResolveRimLiaisonCommand(out IReadOnlyList<string> prefix);
            var validationArguments = new List<string>(prefix.Count + 3 + (dependencyFingerprints?.Count ?? 0) * 2);
            validationArguments.AddRange(prefix);
            validationArguments.Add("affected");
            validationArguments.Add("--run");
            validationArguments.Add("--json");
            if (dependencyFingerprints is not null)
            {
                foreach (KeyValuePair<string, string> pair in dependencyFingerprints.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
                {
                    validationArguments.Add("--dependency-fingerprint");
                    validationArguments.Add(pair.Key + "=" + pair.Value);
                }
            }
            RimDevProcessResult process = await processRunner.RunAsync(
                    repository.Path,
                    command,
                    validationArguments,
                    TimeSpan.FromMinutes(30),
                    cancellationToken)
                .ConfigureAwait(false);
            if (observabilityStore is IAgentObservabilityLiveStore liveStore)
            {
                liveStore.Refresh();
            }

            RimDevTestExecutionResult execution = ParseTestResult(process);
            if (execution.Succeeded && observation.State is not null)
            {
                ValidationPublicationCheck completedCanonical = ValidationPublicationChecker.Evaluate(
                    observation.State,
                    ToPublicationChanges(observation.ChangedPaths),
                    observabilityStore,
                    repository.Manifest.Project ?? repository.Name,
                    PublicationConfiguration(repository),
                    dependencyFingerprints: dependencyFingerprints);
                if (!completedCanonical.Result.SafeToPublish)
                {
                    execution = new(
                        false,
                        "blocked",
                        "RimTest completed without reusable canonical validation evidence.",
                        false,
                        [],
                        "RIMDEV_CANONICAL_TEST_EVIDENCE_MISSING");
                }
            }
            if (context is not null)
            {
                context.TestResults[repository.Path] = execution;
            }


            RimDevRepositoryResult executionResult = execution.Succeeded
                ? Success(repository, "pass", execution.Summary, observation.State)
                : execution.Status is "blocked" or "infrastructure"
                    ? Blocked(repository, execution.ErrorCode ?? "RIMDEV_TEST_INFRASTRUCTURE", execution.Summary, observation.State)
                    : Failed(repository, execution.ErrorCode ?? "RIMDEV_TEST_FAILED", execution.Summary, observation.State);
            results.Add(executionResult);
            completed[repository.Name] = executionResult;
        }

        return Aggregate("test", results);
    }

    private async Task<RimDevResult> DeployAsync(
        RimDevWorkspaceDiscovery workspace,
        RimDevInvocationContext? context,
        bool requireTestSuccess,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations =
            await ObserveAllAsync(workspace, cancellationToken).ConfigureAwait(false);
        foreach (RimDevRepository repository in workspace.Repositories.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            RimDevRepositoryObservation observation = observations[repository.Name];
            if (observation.State is null || observation.ErrorCode is not null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State));
                continue;
            }

            if (context?.TestResults.TryGetValue(repository.Path, out RimDevTestExecutionResult? test) == true)
            {
                if (requireTestSuccess && !test.Succeeded)
                {
                    bool infrastructure = test.Status is "blocked" or "infrastructure";
                    results.Add(Blocked(
                        repository,
                        infrastructure ? "RIMDEV_DEPLOY_TEST_BLOCKED" : "RIMDEV_DEPLOY_TEST_FAILED",
                        infrastructure
                            ? "Deployment was skipped because infrastructure did not prove affected validation."
                            : "Deployment was skipped because affected tests did not pass.",
                        observation.State));
                    continue;
                }

                if (test.Succeeded && test.Status.Equals("skip", StringComparison.OrdinalIgnoreCase) &&
                    observation.BuildPaths.Count == 0)
                {
                    results.Add(Success(repository, "skip", "no affected build inputs; deployment was not needed", observation.State));
                    continue;
                }

                if (test.Succeeded && test.ArtifactFreshnessProven)
                {
                    results.Add(Success(repository, "reused", "deployment was proven by RimTest artifact freshness", observation.State, deployment: "REUSED", deployed: test.Deployed));
                    continue;
                }
            }

            RimDevBuildEvidence? evidence = evidenceStore.Read(repository.Path);
            if (evidence is null)
            {
                results.Add(Blocked(repository, "RIMDEV_BUILD_EVIDENCE_MISSING", "Run rimdev build and keep the source unchanged before deploying.", observation.State));
                continue;
            }

            string? dependencyFingerprint = ComputeDependencyFingerprint(repository, observations);
            if (!IsEvidenceCurrent(repository, observation, evidence, dependencyFingerprint, out string? evidenceError))
            {
                results.Add(Blocked(repository, "RIMDEV_BUILD_EVIDENCE_STALE", evidenceError, observation.State));
                continue;
            }

            RimDevDeploymentSpec? deployment = ResolveDeployment(repository, workspace.RootPath, evidence, out string? deploymentError);
            if (deployment is null)
            {
                results.Add(Blocked(repository, "RIMDEV_DEPLOYMENT_CONFIGURATION_MISSING", deploymentError, observation.State));
                continue;
            }

            string? errorCode = DeployArtifact(evidence, deployment, out string summary);
            results.Add(errorCode is null
                ? Success(repository, "pass", summary, observation.State, deployment: "PASS", deployed: [deployment.TargetPath])
                : Failed(repository, errorCode, summary, observation.State, deployment: "FAIL"));
        }

        return Aggregate("deploy", results);

    }
    private async Task<RimDevResult> PushAsync(
        RimDevWorkspaceDiscovery workspace,
        RimDevInvocationContext? context,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        var observations = new Dictionary<string, RimDevRepositoryObservation>(
            await ObserveAllAsync(workspace, cancellationToken).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RimDevRepository> ordered = OrderRepositories(workspace.Repositories, out HashSet<string> cycles);
        var completed = new Dictionary<string, RimDevRepositoryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (RimDevRepository repository in ordered)
        {
            if (cycles.Contains(repository.Name))
            {
                RimDevRepositoryResult cycle = Blocked(repository, "RIMDEV_DEPENDENCY_CYCLE", "Repository publication dependencies contain a cycle.");
                results.Add(cycle);
                completed[repository.Name] = cycle;
                continue;
            }

            if (repository.Dependencies.Any(dependency =>
                    completed.TryGetValue(dependency, out RimDevRepositoryResult? dependencyResult) &&
                    dependencyResult.Status is "blocked" or "fail"))
            {
                RimDevRepositoryResult dependencyBlocked = Blocked(
                    repository,
                    "RIMDEV_DEPENDENCY_BLOCKED",
                    "A required repository did not pass its publication gate; push was not attempted.");
                results.Add(dependencyBlocked);
                completed[repository.Name] = dependencyBlocked;
                continue;
            }

            RimDevRepositoryObservation observation = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null)
            {
                RimDevRepositoryResult unavailable = Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State);
                results.Add(unavailable);
                completed[repository.Name] = unavailable;
                continue;
            }

            if (context?.PhaseResults.TryGetValue(repository.Path, out List<RimDevRepositoryResult>? phases) == true &&
                phases.Any(value => value.Status is "blocked" or "fail"))
            {
                RimDevRepositoryResult phaseBlocked = Blocked(
                    repository,
                    PublicationPhaseErrorCode(phases),
                    PublicationPhaseSummary(phases),
                    observation.State);
                results.Add(phaseBlocked);
                completed[repository.Name] = phaseBlocked;
                continue;
            }

            bool alreadyFetched = fetched.Contains(observation.Repository.Path);
            RimDevRepositoryResult? fetchFailure = await EnsureFetchedAsync(observation, cancellationToken).ConfigureAwait(false);
            if (fetchFailure is not null)
            {
                results.Add(fetchFailure);
                completed[repository.Name] = fetchFailure;
                continue;
            }

            if (!alreadyFetched)
            {
                observation = await RefreshAsync(repository, cancellationToken).ConfigureAwait(false);
                observations[repository.Name] = observation;
                if (observation.State is null || observation.ErrorCode is not null)
                {
                    RimDevRepositoryResult refreshFailure = Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error, observation.State);
                    results.Add(refreshFailure);
                    completed[repository.Name] = refreshFailure;
                    continue;
                }
            }

            RimDevPolicyDecision decision = RimDevGitPolicy.DecidePush(observation.State);
            if (!decision.Allowed)
            {
                RimDevRepositoryResult gitBlocked = Blocked(repository, decision.ErrorCode!, decision.Explanation, observation.State);
                results.Add(gitBlocked);
                completed[repository.Name] = gitBlocked;
                continue;
            }

            ValidationPublicationCheck publication = ValidationPublicationChecker.Evaluate(
                observation.State,
                ToPublicationChanges(observation.ChangedPaths),
                observabilityStore,
                repository.Manifest.Project ?? repository.Name,
                PublicationConfiguration(repository),
                dependencyFingerprints: ComputeDependencyFingerprints(repository, observations));
            if (!publication.Result.SafeToPublish)
            {
                string code = PublicationErrorCode(publication.Result);
                RimDevRepositoryResult publicationBlocked = Blocked(
                    repository,
                    code,
                    "Publication evidence blocked push: " + code + ": " + PublicationExplanation(publication.Result),
                    observation.State,
                    publication.Result.NextAction);
                results.Add(publicationBlocked);
                completed[repository.Name] = publicationBlocked;
                continue;
            }

            if (decision.Action == "current")
            {
                RimDevRepositoryResult current = Success(repository, "skip", observation.State.Dirty
                    ? "nothing to push; uncommitted files were left untouched"
                    : "nothing to push", observation.State);
                results.Add(current);
                completed[repository.Name] = current;
                continue;
            }

            RimDevGitResult push = await git.RunAsync(repository.Path, ["push"], cancellationToken).ConfigureAwait(false);
            RimDevRepositoryResult result = push.Succeeded
                ? Success(repository, "pushed", observation.State.Dirty
                    ? "pushed committed work; uncommitted files were left untouched"
                    : "pushed committed work", observation.State)
                : Blocked(repository, "GIT_PUSH_REJECTED", "The push was rejected without force-push or local file changes.", observation.State);
            results.Add(result);
            completed[repository.Name] = result;
        }

        return Aggregate("push", results);
    }

    private async Task<RimDevResult> MergeAsync(
        RimDevWorkspaceDiscovery workspace,
        bool confirmed,
        TextWriter? planWriter,
        TextReader? confirmationReader,
        CancellationToken cancellationToken)
    {
        var results = new List<RimDevRepositoryResult>();
        bool performed = false;
        foreach (RimDevRepository repository in workspace.Repositories.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            RimDevRepositoryObservation observation = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null || observation.State.Branch is null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_DETACHED_HEAD", observation.Error ?? "Merge requires an explicitly named source branch.", observation.State));
                continue;
            }

            RimDevRepositoryResult? fetchFailure = await EnsureFetchedAsync(observation, cancellationToken).ConfigureAwait(false);
            if (fetchFailure is not null)
            {
                results.Add(fetchFailure);
                continue;
            }

            observation = await RefreshAsync(repository, cancellationToken).ConfigureAwait(false);
            if (observation.State is null || observation.ErrorCode is not null || observation.State.Branch is null)
            {
                results.Add(Blocked(repository, observation.ErrorCode ?? "GIT_STATE_UNAVAILABLE", observation.Error ?? "The branch state could not be refreshed.", observation.State));
                continue;
            }

            RimDevPullRequestQueryResult query = await pullRequests.FindAsync(repository.Path, observation.State.Branch, cancellationToken).ConfigureAwait(false);
            if (!query.Available)
            {
                results.Add(Blocked(repository, query.ErrorCode ?? "GITHUB_PR_QUERY_UNAVAILABLE", query.Error));
                continue;
            }

            RimDevPullRequest[] candidates = query.PullRequests.Where(pr =>
                string.Equals(pr.HeadBranch, observation.State.Branch, StringComparison.Ordinal)).ToArray();
            if (candidates.Length == 0)
            {
                results.Add(Blocked(repository, "MERGE_PR_NOT_FOUND", "No single open pull request was found for the current branch."));
                continue;
            }

            RimDevPullRequest? pr = candidates.Length == 1
                ? candidates[0]
                : !confirmed && planWriter is not null && confirmationReader is not null
                    ? RimDevHumanUi.SelectMergeCandidate(planWriter, confirmationReader, candidates)
                    : null;
            if (pr is null)
            {
                results.Add(Blocked(
                    repository,
                    "MERGE_PR_AMBIGUOUS",
                    candidates.Length == 1
                        ? "No merge was selected."
                        : "More than one merge candidate was returned; no merge was selected."));
                continue;
            }

            if (pr.IsDraft)
            {
                results.Add(Blocked(repository, "MERGE_PR_DRAFT", "The pull request is still a draft."));
                continue;
            }

            if (!string.Equals(pr.Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Blocked(repository, "MERGE_PR_NOT_MERGEABLE", "GitHub does not report this pull request as mergeable."));
                continue;
            }

            if (!ChecksPass(pr.CheckStates, out string checksReason))
            {
                results.Add(Blocked(repository, "MERGE_CHECKS_NOT_PASSING", checksReason));
                continue;
            }

            if (string.IsNullOrWhiteSpace(pr.HeadSha) || string.IsNullOrWhiteSpace(observation.State.HeadSha) ||
                !string.Equals(pr.HeadSha, observation.State.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Blocked(repository, "MERGE_SOURCE_STALE", "The local source branch does not match the pull request head."));
                continue;
            }

            RimDevGitResult target = await git.RunAsync(repository.Path, ["rev-parse", "refs/remotes/origin/" + pr.BaseBranch], cancellationToken).ConfigureAwait(false);
            if (!target.Succeeded || string.IsNullOrWhiteSpace(pr.BaseSha) ||
                !string.Equals(target.Stdout.Trim(), pr.BaseSha, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Blocked(repository, "MERGE_TARGET_STALE", "The local target branch is stale or cannot be identified safely."));
                continue;
            }

            string plan = $"{repository.Path}: PR #{pr.Number} {pr.HeadBranch} -> {pr.BaseBranch} (merge action: merge PR)";
            bool approved = confirmed;
            if (confirmed)
            {
                if (planWriter is not null)
                {
                    RimDevHumanUi.WriteMergePlan(planWriter, repository, pr);
                }
            }
            else if (planWriter is not null && confirmationReader is not null)
            {
                approved = RimDevHumanUi.ConfirmMerge(planWriter, confirmationReader, repository, pr);
            }

            if (!approved)
            {
                results.Add(Blocked(
                    repository,
                    "MERGE_CONFIRMATION_REQUIRED",
                    planWriter is null
                        ? $"Ready to merge {plan}; re-run rimdev merge --yes to confirm this exact action."
                        : "No merge was performed; the default is No."));
                continue;
            }

            RimDevProcessResult merged = await pullRequests.MergeAsync(repository.Path, pr, cancellationToken).ConfigureAwait(false);
            if (!merged.Succeeded)
            {
                results.Add(Failed(repository, "MERGE_FAILED", "The merge command failed for " + plan + ".", observation.State));
                continue;
            }

            performed = true;
            string summary = "merged " + plan;
            if (!observation.State.Dirty)
            {
                bool onTarget = string.Equals(observation.State.Branch, pr.BaseBranch, StringComparison.Ordinal);
                if (!onTarget)
                {
                    RimDevGitResult switchResult = await git.RunAsync(
                            repository.Path,
                            ["switch", "--quiet", "--no-guess", pr.BaseBranch],
                            cancellationToken)
                        .ConfigureAwait(false);
                    onTarget = switchResult.Succeeded;
                    if (!onTarget)
                    {
                        summary += "; local target branch was left unchanged";
                    }
                }

                if (onTarget)
                {
                    RimDevGitResult fetch = await git.RunAsync(repository.Path, ["fetch", "--prune", "--no-tags", "origin"], cancellationToken).ConfigureAwait(false);
                    if (fetch.Succeeded)
                    {
                        RimDevGitResult ff = await git.RunAsync(repository.Path, ["merge", "--ff-only", "refs/remotes/origin/" + pr.BaseBranch], cancellationToken).ConfigureAwait(false);
                        summary += ff.Succeeded ? "; local target synchronized" : "; local target sync was not safe to complete";
                    }
                    else
                    {
                        summary += "; local target sync was not available";
                    }
                }
            }
            else
            {
                summary += "; local branch/worktree left unchanged";
            }

            results.Add(Success(repository, "merged", summary, observation.State, merge: "MERGED"));
        }

        RimDevResult aggregate = Aggregate("merge", results);
        return aggregate with { MergePerformed = performed };
    }

    private async Task<RimDevResult> AllAsync(
        RimDevWorkspaceDiscovery workspace,
        CancellationToken cancellationToken)
    {
        var context = new RimDevInvocationContext();
        RimDevResult sync = await SyncAsync(workspace, cancellationToken).ConfigureAwait(false);
        RecordPhase(context, sync);
        RimDevResult test = await TestAsync(workspace, context, cancellationToken).ConfigureAwait(false);
        RecordPhase(context, test);
        RimDevResult build = await BuildAsync(workspace, context, true, cancellationToken).ConfigureAwait(false);
        RecordPhase(context, build);
        RimDevResult deploy = await DeployAsync(workspace, context, true, cancellationToken).ConfigureAwait(false);
        RecordPhase(context, deploy);
        RimDevResult push = await PushAsync(workspace, context, cancellationToken).ConfigureAwait(false);
        var results = new List<RimDevRepositoryResult>();
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            RimDevRepositoryResult? a = Find(sync, repository.Name);
            RimDevRepositoryResult? b = Find(test, repository.Name);
            RimDevRepositoryResult? c = Find(build, repository.Name);
            RimDevRepositoryResult? d = Find(deploy, repository.Name);
            RimDevRepositoryResult? e = Find(push, repository.Name);
            string[] statuses = [a?.Status ?? "blocked", b?.Status ?? "blocked", c?.Status ?? "blocked", d?.Status ?? "blocked", e?.Status ?? "blocked"];
            string status = statuses.Any(v => v == "fail") ? "fail" : statuses.Any(v => v == "blocked") ? "blocked" : "ok";
            RimDevRepositoryObservation observation = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
            string merge = observation.State is null || observation.ErrorCode is not null
                ? "unavailable"
                : await MergeReadinessAsync(repository, observation.State, cancellationToken).ConfigureAwait(false);
            results.Add(new(
                repository.Name,
                repository.Path,
                status,
                string.Join("; ", "sync: " + (a?.Summary ?? "not run"), "test: " + (b?.Summary ?? "not run"), "build: " + (c?.Summary ?? "not run"), "deploy: " + (d?.Summary ?? "not run"), "push: " + (e?.Summary ?? "not run")),
                b?.Branch ?? c?.Branch ?? a?.Branch,
                b?.Dirty ?? c?.Dirty ?? a?.Dirty,
                b?.Ahead ?? c?.Ahead ?? a?.Ahead,
                b?.Behind ?? c?.Behind ?? a?.Behind,
                b?.Upstream ?? c?.Upstream ?? a?.Upstream,
                c?.Build,
                d?.Deployment,
                merge,
                statuses.Any(v => v is "blocked" or "fail") ? "RIMDEV_ALL_PARTIAL" : null,
                null,
                d?.Deployed));
        }

        var nextActions = new List<string>();
        foreach (RimDevRepositoryResult? value in results
                     .SelectMany(_ => new[] { Find(sync, _.Name), Find(test, _.Name), Find(build, _.Name), Find(deploy, _.Name), Find(push, _.Name) })
                     .Where(value => value?.NextAction is not null))
        {
            nextActions.Add(value!.NextAction!);
        }

        if (results.Any(value => string.Equals(value.Merge, "ready", StringComparison.OrdinalIgnoreCase)))
        {
            nextActions.Add("Approved work is ready to merge; review it with rimdev merge.");
        }

        return new(
            "all",
            results.Any(r => r.Status == "fail") ? "failed" : results.Any(r => r.Status == "blocked") ? "partial" : "ok",
            results,
            nextActions.Distinct(StringComparer.Ordinal).ToArray(),
            MergePerformed: false);
    }

    private static void RecordPhase(RimDevInvocationContext context, RimDevResult phase)
    {
        foreach (RimDevRepositoryResult result in phase.Repositories)
        {
            if (!context.PhaseResults.TryGetValue(result.Path, out List<RimDevRepositoryResult>? phases))
            {
                phases = [];
                context.PhaseResults[result.Path] = phases;
            }

            phases.Add(result);
        }
    }

    private static IReadOnlyList<GitRepositoryChange> ToPublicationChanges(
        IReadOnlyList<string> paths) =>
        paths.Select(path => new GitRepositoryChange(
                path,
                "M",
                false,
                RimDevGitReader.IsGeneratedPath(path)))
            .ToArray();

    private static IReadOnlyDictionary<string, string> PublicationConfiguration(
        RimDevRepository repository) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = ConfigurationPath(repository.Path, repository.Manifest.Catalog),
            ["devBridgeProject"] = repository.Manifest.DevBridgeProject ?? "unknown",
            ["fallbackSuite"] = repository.Manifest.FallbackSuite ?? "unknown"
        };

    private static string ConfigurationPath(string repositoryPath, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "unknown";
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(repositoryPath, configured));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return "invalid";
        }
    }

    private static string PublicationErrorCode(ValidationPublicationResult result) =>
        result.Decisions
            .Where(decision => decision.ValidationKind is not null &&
                decision.Action is global::RimContext.Core.Context.RimContextDecisionActions.Block or
                    global::RimContext.Core.Context.RimContextDecisionActions.Invalidate)
            .Select(static decision => decision.ReasonCode)
            .FirstOrDefault() ?? ValidationDecisionReasonCodes.PublicationValidationRequired;

    private static string PublicationExplanation(ValidationPublicationResult result) =>
        string.Join(
            "; ",
            result.Decisions
                .Where(decision => decision.Action is global::RimContext.Core.Context.RimContextDecisionActions.Block or
                    global::RimContext.Core.Context.RimContextDecisionActions.Invalidate)
                .Select(static decision => decision.ReasonCode + ": " + decision.Explanation));

    private static string PublicationPhaseErrorCode(IReadOnlyList<RimDevRepositoryResult> phases) =>
        phases.First(value => value.Status is "blocked" or "fail").ErrorCode ??
        ValidationDecisionReasonCodes.PublicationValidationRequired;

    private static string PublicationPhaseSummary(IReadOnlyList<RimDevRepositoryResult> phases) =>
        "Publication was blocked because " +
        phases.First(value => value.Status is "blocked" or "fail").Summary;

    private async Task<RimDevRepositoryObservation> ObserveAsync(RimDevRepository repository, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(repository.Path, out RimDevRepositoryObservation? value))
        {
            return value;
        }

        value = await reader.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        cache[repository.Path] = value;
        return value;
    }

    private async Task<IReadOnlyDictionary<string, RimDevRepositoryObservation>> ObserveAllAsync(
        RimDevWorkspaceDiscovery workspace,
        CancellationToken cancellationToken)
    {
        var observations = new Dictionary<string, RimDevRepositoryObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            observations[repository.Name] = await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
        }

        return observations;
    }

    private static HashSet<string> ComputeDependencyAffected(
        IReadOnlyList<RimDevRepository> repositories,
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations)
    {
        var affected = new HashSet<string>(
            repositories
                .Where(repository => observations.TryGetValue(repository.Name, out RimDevRepositoryObservation? observation) &&
                    observation.BuildPaths.Count > 0)
                .Select(repository => repository.Name),
            StringComparer.OrdinalIgnoreCase);
        var downstream = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (RimDevRepository repository in repositories)
            {
                if (downstream.Contains(repository.Name) ||
                    !repository.Dependencies.Any(dependency => affected.Contains(dependency)))
                {
                    continue;
                }

                downstream.Add(repository.Name);
                affected.Add(repository.Name);
                changed = true;
            }
        }
        while (changed);

        return downstream;
    }

    private static IReadOnlyDictionary<string, string>? ComputeDependencyFingerprints(
        RimDevRepository repository,
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations)
    {
        var dependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool unavailable = false;

        void Visit(RimDevRepository current)
        {
            foreach (string dependency in current.Dependencies)
            {
                if (!dependencyNames.Add(dependency))
                {
                    continue;
                }

                if (!observations.TryGetValue(dependency, out RimDevRepositoryObservation? observation) ||
                    observation.State is null ||
                    RimDevSourceIdentity.Compute(observation.Repository.Path, observation.State, observation.MeaningfulPaths) is null)
                {
                    unavailable = true;
                    continue;
                }

                Visit(observation.Repository);
            }
        }

        Visit(repository);
        if (dependencyNames.Count == 0 || unavailable)
        {
            return dependencyNames.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : null;
        }

        var fingerprints = new Dictionary<string, string>(
            dependencyNames.Count,
            StringComparer.Ordinal);
        foreach (string dependency in dependencyNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            RimDevRepositoryObservation observation = observations[dependency];
            string? identity = RimDevSourceIdentity.Compute(
                observation.Repository.Path,
                observation.State!,
                observation.MeaningfulPaths);
            if (identity is null)
            {
                return null;
            }

            fingerprints[dependency] = identity;
        }

        return fingerprints;
    }

    private static string? ComputeDependencyFingerprint(
        RimDevRepository repository,
        IReadOnlyDictionary<string, RimDevRepositoryObservation> observations)
    {
        IReadOnlyDictionary<string, string>? fingerprints = ComputeDependencyFingerprints(repository, observations);
        if (fingerprints is null || fingerprints.Count == 0)
        {
            return null;
        }

        string[] identities = fingerprints
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "\0" + pair.Value)
            .ToArray();
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", identities))))
            .ToLowerInvariant();
    }

    private async Task<RimDevRepositoryObservation> RefreshAsync(RimDevRepository repository, CancellationToken cancellationToken)
    {
        cache.Remove(repository.Path);
        return await ObserveAsync(repository, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RimDevRepositoryResult?> EnsureFetchedAsync(RimDevRepositoryObservation observation, CancellationToken cancellationToken)
    {
        if (fetched.Contains(observation.Repository.Path))
        {
            return null;
        }

        if (observation.Remotes.Count == 0)
        {
            return Blocked(observation.Repository, "GIT_REMOTE_MISSING", "No Git remote is configured.", observation.State);
        }

        foreach (string remote in observation.Remotes)
        {
            RimDevGitResult result = await git.RunAsync(observation.Repository.Path, ["fetch", "--prune", "--no-tags", "--", remote], cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return Blocked(observation.Repository, "GIT_FETCH_FAILED", "Fetching " + remote + " failed; no local branch or file was changed.", observation.State);
            }
        }

        fetched.Add(observation.Repository.Path);
        return null;
    }

    private async Task<RimDevBuildExecutionResult> BuildRepositoryAsync(
        RimDevRepository repository,
        RimDevRepositoryObservation observation,
        string? dependencyFingerprint,
        CancellationToken cancellationToken)
    {
        RimDevBuildTarget? target = ResolveBuildTarget(repository, observation, out string? error);
        if (target is null)
        {
            return new(false, null, null, error ?? "No unambiguous build project was found.", "RIMDEV_BUILD_PROJECT_AMBIGUOUS");
        }

        RimDevProcessResult process = await processRunner.RunAsync(repository.Path, "dotnet", ["build", target.Path, "--configuration", target.Configuration, "--nologo"], TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        if (!process.Succeeded)
        {
            return new(false, target, null, process.TimedOut ? "build timed out" : "build failed", process.TimedOut ? "RIMDEV_BUILD_TIMEOUT" : "RIMDEV_BUILD_FAILED", Bound(process.Stdout + "\n" + process.Stderr));
        }

        string? output = FindBuildOutput(target);
        if (output is null)
        {
            return new(false, target, null, "build passed but its expected assembly was not found", "RIMDEV_BUILD_OUTPUT_MISSING");
        }

        RimDevRepositoryObservation after = await RefreshAsync(repository, cancellationToken).ConfigureAwait(false);
        if (after.State is null ||
            !string.Equals(after.State.HeadSha, observation.State?.HeadSha, StringComparison.OrdinalIgnoreCase) ||
            !after.ChangedPaths.SequenceEqual(observation.ChangedPaths, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, target, null, "source changed during build; output was not recorded", "RIMDEV_SOURCE_CHANGED_DURING_BUILD");
        }

        string[] identityPaths = after.ChangedPaths.Append(Path.GetRelativePath(repository.Path, target.Path)).ToArray();
        string? identity = RimDevSourceIdentity.Compute(repository.Path, after.State, identityPaths);
        if (identity is null)
        {
            return new(false, target, null, "source identity could not be proven", "RIMDEV_SOURCE_IDENTITY_UNKNOWN");
        }

        string hash = HashFile(output);
        var evidence = new RimDevBuildEvidence(
            repository.Path,
            target.Path,
            target.Configuration,
            after.State.HeadSha,
            identity,
            identityPaths,
            output,
            hash,
            DateTimeOffset.UtcNow,
            DependencyFingerprint: dependencyFingerprint);
        return evidenceStore.Write(evidence)
            ? new(true, target, evidence, "built " + Path.GetFileName(target.Path), Output: Bound(process.Stdout))
            : new(false, target, null, "build passed but evidence could not be stored", "RIMDEV_BUILD_EVIDENCE_WRITE_FAILED");
    }

    private static RimDevBuildTarget? ResolveBuildTarget(
        RimDevRepository repository,
        RimDevRepositoryObservation observation,
        out string? error)
    {
        error = null;
        string[] candidates;
        if (!string.IsNullOrWhiteSpace(repository.BuildProject))
        {
            string? configured = SafeRepositoryPath(repository.Path, repository.BuildProject!);
            if (configured is null || !File.Exists(configured))
            {
                error = "The configured buildProject does not exist inside the repository.";
                return null;
            }

            candidates = [configured];
        }
        else
        {
            string? contextProject = TryFindContextProject(repository, observation);
            if (contextProject is not null)
            {
                candidates = [contextProject];
            }
            else
            {
                try
                {
                    candidates = Directory.EnumerateFiles(repository.Path, "*.csproj", SearchOption.AllDirectories)
                        .Where(path => !IsGeneratedFile(path) && !IsTestOrToolPath(path))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    error = "The repository project tree could not be inspected.";
                    return null;
                }
            }
        }

        var scored = candidates.Select(path => (Path: path, Score: ScoreProject(path, repository.Name)))
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scored.Length == 0)
        {
            error = "No non-test .csproj build project was found.";
            return null;
        }

        int bestScore = scored[0].Score;
        string[] best = scored.Where(value => value.Score == bestScore).Select(value => value.Path).ToArray();
        if (best.Length > 1 && bestScore <= 0)
        {
            error = "More than one build project is possible; add buildProject to .rimdev/workspace.json.";
            return null;
        }

        string selected = best[0];
        return new(selected, ReadAssemblyName(selected) ?? Path.GetFileNameWithoutExtension(selected), repository.Configuration);
    }

    private static string? TryFindContextProject(
        RimDevRepository repository,
        RimDevRepositoryObservation observation)
    {
        if (observation.BuildPaths.Count == 0)
        {
            return null;
        }

        string storePath = Path.Combine(repository.Path, ".rimctx", "index.sqlite");
        if (!File.Exists(storePath))
        {
            return null;
        }

        try
        {
            AffectedResult affected = new RimContextService().Affected(
                new RimContextAffectedRequest(
                    observation.BuildPaths,
                    repository.Path,
                    storePath,
                    Depth: 8,
                    Limit: 100));
            if (affected.Truncated)
            {
                return null;
            }

            string[] candidates = affected.Direct
                .Concat(affected.Dependent)
                .Concat(affected.RuntimeRisk)
                .Where(match => string.Equals(match.Kind, "project", StringComparison.OrdinalIgnoreCase))
                .Select(match => match.File)
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Select(file => SafeRepositoryPath(repository.Path, file!))
                .Where(file => file is not null &&
                    file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(file) &&
                    !IsGeneratedFile(file) &&
                    !IsTestOrToolPath(file))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                int bestScore = candidates.Max(path => ScoreProject(path, repository.Name));
                string[] best = candidates
                    .Where(path => ScoreProject(path, repository.Name) == bestScore)
                    .ToArray();
                return best.Length == 1 && bestScore > 0 ? best[0] : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or RimContextException)
        {
            // An existing RimContext index is an optimization, not a second
            // source of truth. The deterministic project fallback below remains
            // available when the index is absent, stale, or unreadable.
        }

        return null;
    }

    private static string? FindBuildOutput(RimDevBuildTarget target)
    {
        try
        {
            string root = Path.Combine(Path.GetDirectoryName(target.Path) ?? string.Empty, "bin", target.Configuration);
            if (!Directory.Exists(root))
            {
                return null;
            }

            string[] outputs = Directory.EnumerateFiles(root, target.AssemblyName + ".dll", SearchOption.AllDirectories)
                .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            return outputs.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsEvidenceCurrent(
        RimDevRepository repository,
        RimDevRepositoryObservation observation,
        RimDevBuildEvidence evidence,
        string? dependencyFingerprint,
        out string? error)
    {
        error = null;
        if (observation.State is null || !string.Equals(repository.Path, evidence.RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.State.HeadSha, evidence.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            error = "The repository revision changed after the build.";
            return false;
        }

        HashSet<string> evidencedInputs = evidence.IdentityPaths
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (observation.MeaningfulPaths.Any(path => !evidencedInputs.Contains(path.Replace('\\', '/'))))
        {
            error = "The repository has meaningful inputs that were not present when the build was recorded.";
            return false;
        }

        string? current = RimDevSourceIdentity.Compute(repository.Path, observation.State, evidence.IdentityPaths);
        if (!string.Equals(current, evidence.ChangedPathsFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            error = "The source or project inputs changed after the build.";
            return false;
        }

        if (repository.Dependencies.Count > 0 &&
            (dependencyFingerprint is null || evidence.DependencyFingerprint is null ||
                !string.Equals(dependencyFingerprint, evidence.DependencyFingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            error = "A dependency changed or its source identity could not be proven after the build.";
            return false;
        }

        try
        {
            if (!File.Exists(evidence.OutputPath) ||
                !string.Equals(HashFile(evidence.OutputPath), evidence.OutputSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "The validated build output is missing or was changed.";
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "The validated build output could not be read.";
            return false;
        }

        return true;
    }

    private static RimDevDeploymentSpec? ResolveDeployment(
        RimDevRepository repository,
        string workspaceRoot,
        RimDevBuildEvidence evidence,
        out string? error)
    {
        error = null;
        if (!repository.Manifest.IsValid ||
            repository.Manifest.Workload != "production" ||
            string.IsNullOrWhiteSpace(repository.Manifest.SourceProject) ||
            string.IsNullOrWhiteSpace(repository.Manifest.ExpectedAssembly) ||
            string.IsNullOrWhiteSpace(repository.Manifest.DeploymentTarget))
        {
            error = "PROJECT_METADATA_MISSING: the production repository has no complete project-owned metadata.";
            return null;
        }

        string? deploymentRoot = ResolveConfiguredPath(workspaceRoot, repository.DeploymentRoot);
        string target = repository.Manifest.DeploymentTarget;
        string expectedAssembly = repository.Manifest.ExpectedAssembly;
        if (deploymentRoot is null)
        {
            error = "WORKSPACE_DEPLOYMENT_ROOT_MISSING: the machine workspace has no deployment root.";
            return null;
        }

        if (!string.Equals(expectedAssembly, Path.GetFileName(evidence.OutputPath), StringComparison.OrdinalIgnoreCase))
        {
            error = "PROJECT_METADATA_IDENTITY_CONTRADICTION: expectedAssembly does not match the validated output.";
            return null;
        }

        string? targetPath = SafeChildPath(deploymentRoot, target);
        string? parent = targetPath is null ? null : Directory.GetParent(targetPath)?.FullName;
        if (targetPath is null || parent is null || !Directory.Exists(parent) || IsReparsePoint(parent) ||
            (File.Exists(targetPath) && IsReparsePoint(targetPath)))
        {
            error = "WORKSPACE_DEPLOYMENT_TARGET_INVALID: the machine deployment target is outside the configured root or its parent is unsafe.";
            return null;
        }

        return new(targetPath, expectedAssembly);
    }

    private static string? DeployArtifact(RimDevBuildEvidence evidence, RimDevDeploymentSpec deployment, out string summary)
    {
        string temporary = deployment.TargetPath + ".rimdev-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(evidence.OutputPath, temporary, overwrite: false);
            if (!string.Equals(HashFile(temporary), evidence.OutputSha256, StringComparison.OrdinalIgnoreCase))
            {
                summary = "staged deployment output hash did not match the validated build output";
                return "RIMDEV_DEPLOYMENT_HASH_MISMATCH";
            }

            File.Move(temporary, deployment.TargetPath, overwrite: true);
            if (!string.Equals(HashFile(deployment.TargetPath), evidence.OutputSha256, StringComparison.OrdinalIgnoreCase))
            {
                summary = "deployed artifact hash did not match the validated build output";
                return "RIMDEV_DEPLOYMENT_HASH_MISMATCH";
            }

            summary = "deployed " + deployment.ExpectedAssembly + " to " + deployment.TargetPath;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            summary = Bound(exception.Message) ?? "the deployment target could not be replaced";
            return "RIMDEV_DEPLOYMENT_FAILED";
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
        }
    }

    private static IReadOnlyList<RimDevRepository> OrderRepositories(IReadOnlyList<RimDevRepository> repositories, out HashSet<string> cycles)
    {
        var byName = repositories.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var output = new List<RimDevRepository>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RimDevRepository repository in repositories.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            Visit(repository);
        }

        cycles = foundCycles;
        return output;

        void Visit(RimDevRepository repository)
        {
            if (visited.Contains(repository.Name)) return;
            if (!visiting.Add(repository.Name))
            {
                foundCycles.Add(repository.Name);
                return;
            }

            foreach (string dependency in repository.Dependencies.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (byName.TryGetValue(dependency, out RimDevRepository? dependencyRepository))
                {
                    Visit(dependencyRepository);
                    if (foundCycles.Contains(dependencyRepository.Name)) foundCycles.Add(repository.Name);
                }
            }

            visiting.Remove(repository.Name);
            visited.Add(repository.Name);
            output.Add(repository);
        }
    }

    private static RimDevTestExecutionResult ParseTestResult(RimDevProcessResult process)
    {
        JsonElement? json = LastJson(process.Stdout);
        if (json is null)
        {
            return new(false, "infrastructure", process.TimedOut ? "affected test timed out" : "affected test returned no structured result", false, [], process.TimedOut ? "RIMDEV_TEST_TIMEOUT" : "RIMDEV_TEST_RESULT_MISSING");
        }

        JsonElement value = json.Value;
        string status = GetString(value, "status");
        bool passed = process.Succeeded && status is "pass" or "ok";
        bool freshness = value.TryGetProperty("artifactFreshness", out JsonElement artifact) &&
            artifact.ValueKind == JsonValueKind.Object &&
            artifact.TryGetProperty("loadedArtifactFreshnessProven", out JsonElement proven) &&
            proven.ValueKind == JsonValueKind.True;
        string? errorCode = passed
            ? null
            : status == "fail"
                ? "RIMDEV_TEST_FAILED"
                : status == "blocked"
                    ? "RIMDEV_TEST_BLOCKED"
                    : process.TimedOut
                        ? "RIMDEV_TEST_TIMEOUT"
                        : "RIMDEV_TEST_INFRASTRUCTURE";
        string summary = passed
            ? "affected tests passed"
            : status == "fail"
                ? "affected tests failed"
                : "affected test validation was blocked by infrastructure";
        return new(passed, passed ? status : status == "fail" ? "fail" : "infrastructure", summary, freshness, freshness ? ["validated RimTest artifact"] : [], errorCode, Bound(process.Stdout));
    }

    private static JsonElement? LastJson(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].TrimStart().StartsWith("{", StringComparison.Ordinal)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(lines[i]);
                return document.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        return null;
    }

    private static string ResolveRimLiaisonCommand(out IReadOnlyList<string> prefix)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "rimliaison.exe");
        if (File.Exists(exe)) { prefix = []; return exe; }
        string dll = Path.Combine(AppContext.BaseDirectory, "rimliaison.dll");
        if (File.Exists(dll)) { prefix = [dll]; return "dotnet"; }
        prefix = [];
        return "rimliaison";
    }

    private string CachedBuildStatus(RimDevRepository repository, GitRepositoryStateSnapshot state)
    {
        RimDevBuildEvidence? evidence = evidenceStore.Read(repository.Path);
        return evidence is not null && string.Equals(evidence.HeadSha, state.HeadSha, StringComparison.OrdinalIgnoreCase) && File.Exists(evidence.OutputPath)
            ? "PASS (cached)"
            : "unknown";
    }


    private async Task<string> MergeReadinessAsync(
        RimDevRepository repository,
        GitRepositoryStateSnapshot state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Branch))
        {
            return "unknown";
        }

        RimDevPullRequestQueryResult query = await pullRequests
            .FindAsync(repository.Path, state.Branch, cancellationToken)
            .ConfigureAwait(false);
        if (!query.Available)
        {
            return "unavailable";
        }

        RimDevPullRequest[] candidates = query.PullRequests
            .Where(value => string.Equals(value.HeadBranch, state.Branch, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            return "no open PR";
        }

        if (candidates.Length != 1)
        {
            return "ambiguous";
        }

        RimDevPullRequest candidate = candidates[0];
        if (candidate.IsDraft)
        {
            return "draft";
        }

        if (!string.Equals(candidate.Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase))
        {
            return "not mergeable";
        }

        return ChecksPass(candidate.CheckStates, out _) &&
            !string.IsNullOrWhiteSpace(candidate.HeadSha) &&
            !string.IsNullOrWhiteSpace(state.HeadSha) &&
            string.Equals(candidate.HeadSha, state.HeadSha, StringComparison.OrdinalIgnoreCase)
            ? "ready"
            : "checks or source stale";
    }

    private string CheapDeploymentStatus(RimDevRepository repository, string workspaceRoot)
    {
        RimDevBuildEvidence? evidence = evidenceStore.Read(repository.Path);
        if (evidence is null) return "unknown";
        return ResolveDeployment(repository, workspaceRoot, evidence, out _) is { } deployment && File.Exists(deployment.TargetPath)
            ? "present"
            : "unknown";
    }

    private static RimDevRepositoryResult? Find(RimDevResult result, string name) =>
        result.Repositories.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));

    private static RimDevResult Aggregate(string command, IReadOnlyList<RimDevRepositoryResult> results)
    {
        string status = results.Any(value => value.Status == "fail")
            ? "failed"
            : results.Any(value => value.Status is "blocked" or "attention")
                ? "partial"
                : "ok";
        return new(command, status, results, results.Where(value => value.NextAction is not null).Select(value => value.NextAction!).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static RimDevRepositoryResult Success(RimDevRepository repository, string status, string summary, GitRepositoryStateSnapshot? state, string? build = null, string? deployment = null, string? merge = null, IReadOnlyList<string>? deployed = null) =>
        new(repository.Name, repository.Path, status, summary, state?.Branch, state?.Dirty, state?.Ahead, state?.Behind, state?.UpstreamName, build, deployment, merge, Deployed: deployed);

    private static RimDevRepositoryResult Failed(RimDevRepository repository, string code, string summary, GitRepositoryStateSnapshot? state, string? build = null, string? deployment = null) =>
        new(repository.Name, repository.Path, "fail", summary, state?.Branch, state?.Dirty, state?.Ahead, state?.Behind, state?.UpstreamName, build, deployment, ErrorCode: code, NextAction: FriendlyNextAction(repository, code));

    private static RimDevRepositoryResult Blocked(
        RimDevRepository repository,
        string code,
        string? summary,
        GitRepositoryStateSnapshot? state = null,
        string? nextAction = null) =>
        new(repository.Name, repository.Path, "blocked", summary ?? "blocked safely", state?.Branch, state?.Dirty, state?.Ahead, state?.Behind, state?.UpstreamName, ErrorCode: code, NextAction: nextAction ?? FriendlyNextAction(repository, code));

    private static string StatusNextAction(RimDevRepository repository, GitRepositoryStateSnapshot state)
    {
        if (!repository.Manifest.IsValid)
        {
            return repository.Name + " has project configuration that needs attention. Ask your development agent to repair the RimDev project setup.";
        }

        if (state.UpstreamName is null)
        {
            return repository.Name + " is not connected to a GitHub branch yet. Ask your development agent to finish the branch setup.";
        }

        if (state.Dirty && state.Behind is > 0)
        {
            return repository.Name + " has local changes that are not committed. RimDev left them untouched. Ask your development agent to finish or commit that work before running sync.";
        }

        if (state.Dirty)
        {
            return repository.Name + " has local changes. RimDev left them untouched. Ask your development agent to finish or commit that work before running all.";
        }

        if (state.Behind is > 0)
        {
            return repository.Name + " has newer work available. rimdev all can update it safely when there are no local changes.";
        }

        if (state.Ahead is > 0)
        {
            return repository.Name + " has committed work waiting to be pushed. rimdev all can validate and push it safely.";
        }

        return "Review the item above, then ask your development agent if you are unsure what to do next.";
    }

    private static string FriendlyNextAction(RimDevRepository repository, string code) => code switch
    {
        "GIT_DIRTY_BEHIND" => repository.Name + " has local changes that are not committed. RimDev left them untouched. Ask your development agent to finish or commit that work before running sync.",
        "GIT_DIVERGED" => repository.Name + " and GitHub have different changes. RimDev did not guess how to combine them. Ask your development agent to review the branch before trying again.",
        "GIT_UPSTREAM_MISSING" => repository.Name + " is not connected to an upstream branch. Ask your development agent to finish the branch setup before running sync or push.",
        "GIT_DETACHED_HEAD" => repository.Name + " is not currently on a named branch. Ask your development agent to choose the correct project branch before continuing.",
        "GIT_REMOTE_MISSING" => repository.Name + " has no GitHub connection configured. Ask your development agent to finish the project setup.",
        "GIT_FETCH_FAILED" => "RimDev could not check GitHub for newer work. Check the connection or ask your development agent, then try again.",
        "GIT_FAST_FORWARD_FAILED" => "The remote changed while RimDev was updating. No conflict was guessed; ask your development agent to review the branch and retry.",
        "GIT_NON_FAST_FORWARD" or "GIT_PUSH_REJECTED" => repository.Name + " could not be pushed safely. RimDev did not force-push; ask your development agent to review the branch.",
        "MERGE_CONFIRMATION_REQUIRED" => "Review the merge plan above. Type y to merge, or press Enter to keep the safe default No.",
        "MERGE_PR_NOT_FOUND" => "No approved pull request was found for " + repository.Name + ". Ask your development agent to finish or open the correct pull request.",
        "MERGE_PR_AMBIGUOUS" => "More than one possible pull request was found for " + repository.Name + ". RimDev did not guess; ask your development agent to choose one.",
        "MERGE_PR_DRAFT" => "The pull request is still a draft. Ask your development agent to mark the correct work ready when it is complete.",
        "MERGE_PR_NOT_MERGEABLE" => "The pull request cannot be merged safely yet. Ask your development agent to resolve the reported issue before retrying.",
        "MERGE_CHECKS_NOT_PASSING" => "Wait for every required check to pass, then run rimdev merge again.",
        "MERGE_SOURCE_STALE" or "MERGE_TARGET_STALE" => "The branch changed while the merge was being checked. Ask your development agent to refresh the work and retry.",
        "GITHUB_PR_QUERY_UNAVAILABLE" => "RimDev could not check GitHub right now. Check the connection or ask your development agent, then retry.",
        "RIMDEV_BUILD_FAILED" or "RIMDEV_BUILD_TIMEOUT" => "A build failed. Review the compiler output, fix the source, and run rimdev build again.",
        "RIMDEV_BUILD_OUTPUT_MISSING" or "RIMDEV_BUILD_PROJECT_AMBIGUOUS" => "RimDev could not establish a trustworthy build output. Review the project configuration, then run rimdev build again.",
        "RIMDEV_BUILD_EVIDENCE_MISSING" => "Run rimdev build first and keep the source unchanged before deploying.",
        "RIMDEV_DEPLOY_TEST_FAILED" => "Fix the test or readiness issue, then run rimdev all again.",
        "RIMDEV_DEPLOY_TEST_BLOCKED" => "Validation infrastructure did not prove the deployment safe. Follow the reported recovery action, then run rimdev all again.",
        "RIMDEV_TEST_FAILED" => "A test failed. Ask your development agent to review the test result, fix the source, and run rimdev test again.",
        "RIMDEV_CANONICAL_TEST_EVIDENCE_MISSING" => "RimTest did not leave reusable canonical validation evidence. Follow the reported recovery action, then run rimdev test again.",
        "RIMDEV_TEST_BLOCKED" or "RIMDEV_TEST_INFRASTRUCTURE" or "RIMDEV_TEST_TIMEOUT" or "RIMDEV_TEST_RESULT_MISSING" => "Validation infrastructure did not prove the test safe. Follow the reported recovery action, then run rimdev test again.",
        "RIMDEV_DEPLOYMENT_FAILED" => "Deployment did not complete. Ask your development agent to review the target and run rimdev deploy again.",
        "RIMDEV_DEPENDENCY_BLOCKED" => "A required project did not finish. Fix that project first, then run rimdev all again.",
        "RIMDEV_DEPENDENCY_CYCLE" => "Project dependencies need attention. Ask your development agent to repair the project setup.",
        _ => "Ask your development agent to review " + repository.Name + "; RimDev left local work untouched where it was blocked."
    };

    private static void Write(RimDevResult result, bool json, TextWriter stdout)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        stdout.WriteLine($"rimdev {result.Command}: {HumanStatus(result.Status)}");
        if (result.Command == "status")
        {
            stdout.WriteLine("Repository | Local path | Branch | Worktree | Ahead/Behind | Upstream | Build | Deploy | PR");
            foreach (RimDevRepositoryResult value in result.Repositories)
            {
                stdout.WriteLine(string.Join(" | ", value.Name, value.Path, value.Branch ?? "detached", value.Dirty == true ? "DIRTY" : "clean", $"{value.Ahead?.ToString() ?? "?"}/{value.Behind?.ToString() ?? "?"}", value.Upstream ?? "none", value.Build ?? "unknown", value.Deployment ?? "unknown", value.Merge ?? "not checked"));
                if (value.Status != "ready") stdout.WriteLine("  " + HumanStatus(value.Status) + ": " + value.Summary);
            }
        }
        else
        {
            foreach (RimDevRepositoryResult value in result.Repositories)
            {
                stdout.WriteLine($"- {value.Name}: {HumanStatus(value.Status)} — {value.Summary}");
                if (value.Deployed is { Count: > 0 }) stdout.WriteLine("  deployed: " + string.Join(", ", value.Deployed));
            }
        }

        foreach (string action in result.NextActions) stdout.WriteLine("Next: " + action);
        WriteHumanSummary(result, stdout);
    }

    private static void WriteHumanSummary(RimDevResult result, TextWriter stdout)
    {
        if (result.Command == "status")
        {
            if (result.Repositories.Count == 0)
            {
                stdout.WriteLine("Summary: RimDev could not open a workspace. Nothing was changed; follow the Next instruction above or ask your development agent.");
                return;
            }

            int attention = result.Repositories.Count(value => value.Status != "ready");
            int mergeReady = result.Repositories.Count(value => string.Equals(value.Merge, "ready", StringComparison.OrdinalIgnoreCase));
            if (attention == 0 && mergeReady > 0)
            {
                stdout.WriteLine("Summary: Everything is ready. " + CountWords(mergeReady, "pull request", "pull requests") + " ready to merge. Run: rimdev merge");
            }
            else
            {
                stdout.WriteLine(attention == 0
                    ? "Summary: Everything is ready. You can run: rimdev all"
                    : $"Summary: {attention} {Pluralize(attention, "item")} need attention. Nothing was changed.");
            }
            return;
        }

        if (result.Command == "all" && result.Status == "ok")
        {
            int deployed = result.Repositories.Sum(value => value.Deployed?.Count ?? 0);
            int pushed = result.Repositories.Count(value => value.Summary.Contains("push: pushed", StringComparison.OrdinalIgnoreCase));
            int mergeReady = result.Repositories.Count(value => string.Equals(value.Merge, "ready", StringComparison.OrdinalIgnoreCase));
            string deploymentSummary = deployed == 0
                ? "nothing needed deployment"
                : CountWords(deployed, "target", "targets") + " deployed";
            string pushSummary = pushed == 0
                ? "nothing needed pushing"
                : CountWords(pushed, "branch", "branches") + " pushed";
            stdout.WriteLine("Summary: Finished successfully. " + deploymentSummary + " and " + pushSummary + ".");
            stdout.WriteLine(mergeReady == 0
                ? "No pull requests are currently ready to merge."
                : CountWords(mergeReady, "pull request", "pull requests") + " ready to merge. Run: rimdev merge");
            return;
        }

        if (result.Status == "ok")
        {
            stdout.WriteLine("Summary: Finished successfully. Nothing else is required.");
            return;
        }

        int failed = result.Repositories.Count(value => value.Status == "fail");
        int blocked = result.Repositories.Count(value => value.Status == "blocked");
        if (failed > 0)
        {
            stdout.WriteLine("Summary: " + CountWords(failed, "item", "items") + " failed. Review the lines above and ask your development agent to fix them.");
        }
        else if (blocked > 0)
        {
            stdout.WriteLine("Summary: RimDev stopped safely. " + CountWords(blocked, "item", "items") + " need attention; local work was left untouched where it was blocked.");
        }
        else
        {
            stdout.WriteLine("Summary: RimDev needs attention. Review the lines above before trying again.");
        }
    }

    private static string CountWords(int count, string singular, string plural) =>
        count + " " + (count == 1 ? singular : plural);

    private static string HumanStatus(string status) => status.ToLowerInvariant() switch
    {
        "ok" or "ready" or "current" or "synchronized" or "pass" or "pushed" or "reused" or "merged" => "PASS",
        "partial" or "attention" or "blocked" => "BLOCKED",
        "skip" => "SKIPPED",
        "failed" or "fail" => "FAIL",
        _ => status.ToUpperInvariant()
    };

    private static string Pluralize(int count, string singular) => count == 1 ? singular : singular + "s";

    private static string? SafeRepositoryPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            return full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static string? ResolveConfiguredPath(string workspaceRoot, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workspaceRoot, value)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static string? SafeChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return true; }
    }

    private static bool IsGeneratedFile(string path) => RimDevGitReader.IsGeneratedPath(path.Replace(Path.DirectorySeparatorChar, '/'));

    private static bool IsTestOrToolPath(string path)
    {
        string lower = path.Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/tests/", StringComparison.Ordinal) || lower.Contains("/test/", StringComparison.Ordinal) ||
            lower.Contains("/devtools/", StringComparison.Ordinal) || lower.Contains("/examples/", StringComparison.Ordinal) ||
            lower.Contains("/fixtures/", StringComparison.Ordinal);
    }

    private static int ScoreProject(string path, string repositoryName)
    {
        string normalized = NormalizeToken(repositoryName);
        int score = NormalizeToken(Path.GetFileNameWithoutExtension(path)) == normalized ? 100 : 0;
        string? assembly = ReadAssemblyName(path);
        if (!string.IsNullOrWhiteSpace(assembly) && NormalizeToken(assembly) == normalized) score += 200;
        if (path.Replace('\\', '/').Contains("/source/", StringComparison.OrdinalIgnoreCase)) score += 20;
        return score;
    }

    private static string? ReadAssemblyName(string projectPath)
    {
        try
        {
            return XDocument.Load(projectPath).Descendants().FirstOrDefault(value =>
                value.Name.LocalName.Equals("AssemblyName", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or NotSupportedException) { return null; }
    }

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string GetString(JsonElement value, string property) => value.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string? Bound(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= 4096 ? value.Trim() : value.Trim()[..4096];

    private static bool ChecksPass(IReadOnlyList<string> checks, out string reason)
    {
        string[] failing = checks.Where(value => value is not ("SUCCESS" or "PASSED" or "NEUTRAL" or "SKIPPED")).ToArray();
        if (checks.Count == 0 || failing.Length > 0)
        {
            reason = checks.Count == 0 ? "No required check result was returned." : "Required checks are not all passing: " + string.Join(", ", failing) + ".";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private sealed record RimDevDeploymentSpec(string TargetPath, string ExpectedAssembly);
    private sealed class RimDevInvocationContext
    {
        public Dictionary<string, RimDevTestExecutionResult> TestResults { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<RimDevRepositoryResult>> PhaseResults { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class RimDevBuildEvidenceStore
{
    private readonly string directory;

    public RimDevBuildEvidenceStore(string? directory = null)
    {
        directory = directory ?? Environment.GetEnvironmentVariable("RIMDEV_STATE_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimLiaison", "rimdev-state");
        this.directory = Path.GetFullPath(directory);
    }

    public RimDevBuildEvidence? Read(string repositoryPath)
    {
        try
        {
            string path = FilePath(repositoryPath);
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return null;
            return JsonSerializer.Deserialize<RimDevBuildEvidence>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public bool Write(RimDevBuildEvidence evidence)
    {
        string path = FilePath(evidence.RepositoryPath);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(evidence));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { } }
    }

    private string FilePath(string repositoryPath)
    {
        string identity = Path.GetFullPath(repositoryPath).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(directory, hash + ".json");
    }
}
