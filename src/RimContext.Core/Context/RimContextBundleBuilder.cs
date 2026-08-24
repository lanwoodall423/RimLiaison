namespace RimContext.Core.Context;

public static class RimContextBundleBuilder
{
    private const int DefaultStaleAfterSeconds = 300;

    public static async Task<RimContextBundle> BuildAsync(
        RimContextBundleRequest? request,
        IEnumerable<IRimContextBundleProvider> providers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        RimContextBundleRequest normalized = NormalizeRequest(request);
        DateTimeOffset now = (normalized.NowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        string root = Path.GetFullPath(normalized.RootPath ?? Directory.GetCurrentDirectory());
        string? store = string.IsNullOrWhiteSpace(normalized.StorePath)
            ? null
            : Path.GetFullPath(normalized.StorePath!);
        string[] assemblyRoots = (normalized.AssemblyRoots ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var providerRequest = new RimContextProviderRequest(
            root,
            store,
            assemblyRoots,
            normalized.Verbose,
            now,
            normalized.MaxDecisions,
            normalized.MaxRecentExecutions,
            normalized.MaxFailures,
            normalized.MaxExtensions);

        var snapshots = new List<RimContextProviderSnapshot>();
        foreach (IRimContextBundleProvider provider in providers
                     .Where(static provider => provider is not null)
                     .OrderBy(static provider => provider.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RimContextProviderSnapshot snapshot;
            try
            {
                snapshot = await provider
                    .CollectAsync(providerRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                snapshot = new RimContextProviderSnapshot(
                    provider.Id,
                    now,
                    Failures:
                    [
                        new RimContextFailure
                        {
                            SignatureCode = "CONTEXT_PROVIDER_FAILED",
                            OriginatingComponent = provider.Id,
                            Classification = "provider",
                            RootCause = exception.GetType().Name,
                            RecommendedAction = "retry-context-snapshot",
                            RetryAppropriate = true,
                            ObservedAtUtc = now
                        }
                    ]);
            }

            snapshots.Add(snapshot);
        }

        return Build(normalized, now, snapshots);
    }

    public static RimContextBundle Build(
        RimContextBundleRequest? request,
        DateTimeOffset nowUtc,
        IEnumerable<RimContextProviderSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        RimContextBundleRequest normalized = NormalizeRequest(request);
        DateTimeOffset now = nowUtc.ToUniversalTime();
        RimContextProviderSnapshot[] ordered = snapshots
            .Where(static snapshot => snapshot is not null)
            .OrderBy(static snapshot => snapshot.ProviderId, StringComparer.Ordinal)
            .ThenBy(static snapshot => snapshot.ObservedAtUtc)
            .ToArray();

        RimContextSection<RimContextTopology> topology = SelectSection(
            ordered,
            static snapshot => snapshot.Topology,
            now,
            DefaultStaleAfterSeconds,
            "topology");
        RimContextSection<RimContextRepositoryState> repository = SelectSection(
            ordered,
            static snapshot => snapshot.Repository,
            now,
            DefaultStaleAfterSeconds,
            "repository");
        RimContextSection<RimContextEnvironmentState> environment = SelectSection(
            ordered,
            static snapshot => snapshot.Environment,
            now,
            DefaultStaleAfterSeconds,
            "environment");
        RimContextSection<RimContextDeploymentState> deployment = SelectSection(
            ordered,
            static snapshot => snapshot.Deployment,
            now,
            DefaultStaleAfterSeconds,
            "deployment");
        RimContextSection<RimContextRuntimeState> runtime = SelectSection(
            ordered,
            static snapshot => snapshot.Runtime,
            now,
            DefaultStaleAfterSeconds,
            "runtime");
        RimContextSection<RimContextTestingState> testing = SelectSection(
            ordered,
            static snapshot => snapshot.Testing,
            now,
            DefaultStaleAfterSeconds,
            "testing");
        RimContextSection<RimContextEfficiencyMetrics> efficiency = SelectSection(
            ordered,
            static snapshot => snapshot.Efficiency,
            now,
            DefaultStaleAfterSeconds,
            "efficiency");

        RimContextDecision[] decisions = ordered
            .SelectMany(static snapshot => snapshot.Decisions ?? [])
            .Where(static decision => decision is not null)
            .OrderByDescending(static decision => decision.ObservedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static decision => decision.Decision, StringComparer.Ordinal)
            .ThenBy(static decision => decision.Action, StringComparer.Ordinal)
            .ThenBy(static decision => decision.ReasonCode, StringComparer.Ordinal)
            .Take(normalized.MaxDecisions)
            .ToArray();
        RimContextExecution[] recentExecutions = ordered
            .SelectMany(static snapshot => snapshot.RecentExecutions ?? [])
            .Where(static execution => execution is not null)
            .OrderByDescending(static execution => execution.EndedAtUtc ?? execution.StartedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static execution => execution.Operation, StringComparer.Ordinal)
            .ThenBy(static execution => execution.Result, StringComparer.Ordinal)
            .Take(normalized.MaxRecentExecutions)
            .ToArray();
        RimContextFailure[] failures = ordered
            .SelectMany(static snapshot => snapshot.Failures ?? [])
            .Where(static failure => failure is not null)
            .OrderByDescending(static failure => failure.ObservedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static failure => failure.SignatureCode, StringComparer.Ordinal)
            .ThenBy(static failure => failure.OriginatingComponent, StringComparer.Ordinal)
            .Take(normalized.MaxFailures)
            .ToArray();
        RimContextExtension[] extensions = ordered
            .SelectMany(static snapshot => snapshot.Extensions ?? [])
            .Where(static extension => extension is not null)
            .OrderBy(static extension => extension.Provider, StringComparer.Ordinal)
            .ThenBy(static extension => extension.Key, StringComparer.Ordinal)
            .GroupBy(
                static extension => extension.Provider + "\u001f" + extension.Key,
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(normalized.MaxExtensions)
            .ToArray();
        RimContextRepositoryState[] relatedRepositories = ordered
            .SelectMany(static snapshot => snapshot.RelatedRepositories ?? [])
            .Where(static repository => repository is not null)
            .OrderBy(static repository => repository.Component ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static repository => repository.LocalPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static repository => repository.Identity ?? string.Empty, StringComparer.Ordinal)
            .GroupBy(
                static repository => repository.LocalPath ?? repository.Identity ?? repository.Component ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(16)
            .ToArray();

        var sections = new (string Name, string Status, bool? Stale)[]
        {
            ("topology", topology.Status, topology.Stale),
            ("repository", repository.Status, repository.Stale),
            ("environment", environment.Status, environment.Stale),
            ("deployment", deployment.Status, deployment.Stale),
            ("runtime", runtime.Status, runtime.Stale),
            ("testing", testing.Status, testing.Stale),
            ("efficiency", efficiency.Status, efficiency.Stale)
        };
        string[] staleReasons = sections
            .Where(static section => section.Stale == true ||
                string.Equals(section.Status, RimContextBundleStatuses.Stale, StringComparison.Ordinal))
            .Select(static section => section.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        bool stale = staleReasons.Length > 0;
        bool complete = sections.All(static section =>
            string.Equals(section.Status, RimContextBundleStatuses.Available, StringComparison.Ordinal));
        RimContextAgentSummary agentSummary = BuildAgentSummary(
            sections,
            repository,
            deployment,
            runtime,
            testing,
            decisions,
            failures,
            complete,
            stale);

        return new RimContextBundle
        {
            GeneratedAtUtc = now,
            SnapshotStatus = complete ? "complete" : "partial",
            Stale = stale,
            StaleReasons = staleReasons.Length == 0 ? null : staleReasons,
            AgentSummary = agentSummary,
            Ownership = RimContextOwnershipCatalog.Default,
            Topology = topology,
            Repository = repository,
            RelatedRepositories = relatedRepositories.Length == 0 ? null : relatedRepositories,
            Environment = environment,
            Deployment = deployment,
            Runtime = runtime,
            Testing = testing,
            RecentExecutions = recentExecutions,
            Failures = failures,
            Efficiency = efficiency,
            Decisions = decisions,
            Extensions = extensions.Length == 0 ? null : extensions
        };
    }

    private static RimContextAgentSummary BuildAgentSummary(
        IReadOnlyList<(string Name, string Status, bool? Stale)> sections,
        RimContextSection<RimContextRepositoryState> repository,
        RimContextSection<RimContextDeploymentState> deployment,
        RimContextSection<RimContextRuntimeState> runtime,
        RimContextSection<RimContextTestingState> testing,
        IReadOnlyList<RimContextDecision> decisions,
        IReadOnlyList<RimContextFailure> failures,
        bool complete,
        bool stale)
    {
        string[] blockers = sections
            .Where(static section => section.Status is RimContextBundleStatuses.Unknown or RimContextBundleStatuses.Unavailable)
            .Select(section => section.Name + ":" + section.Status)
            .Concat(decisions
                .Where(static decision => decision.Action == RimContextDecisionActions.Block)
                .Select(static decision => decision.ReasonCode))
            .Concat(failures
                .Where(static failure => failure.InfrastructureOnly == true ||
                    failure.Classification is "infrastructure" or "provider" or "integration")
                .Select(static failure => failure.SignatureCode))
            .Concat(testing.Value?.InfrastructureFailure == true
                ? new[] { "testing:infrastructure" }
                : Array.Empty<string>())
            .Concat(!string.IsNullOrWhiteSpace(runtime.Value?.FailureCode)
                ? new[] { runtime.Value!.FailureCode! }
                : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        string[] actions = sections
            .Where(static section => section.Stale == true)
            .Select(static section => "refresh-" + section.Name)
            .Concat(decisions
                .Where(static decision => decision.Action is RimContextDecisionActions.Invalidate or
                    RimContextDecisionActions.Retry or RimContextDecisionActions.Block)
                .Select(static decision => decision.ReasonCode))
            .Concat(failures
                .Select(static failure => failure.RecommendedAction)
                .Where(static action => !string.IsNullOrWhiteSpace(action))
                .Select(static action => action!))
            .Concat(string.Equals(deployment.Value?.Correspondence, "mismatch", StringComparison.Ordinal) ||
                string.Equals(deployment.Value?.Correspondence, "source-changed", StringComparison.Ordinal) ||
                string.Equals(deployment.Value?.Correspondence, "runtime-stale", StringComparison.Ordinal) ||
                string.Equals(deployment.Value?.Correspondence, "unknown", StringComparison.Ordinal)
                ? new[] { "verify-deployment-correspondence" }
                : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        string[] reusableEvidence = (testing.Value?.LatestEvidence ?? [])
            .Where(static evidence => evidence.Status is null or "available" or "reused" or "valid")
            .Select(static evidence => evidence.Id)
            .Concat(decisions.SelectMany(static decision => decision.EvidenceReused).Select(static evidence => evidence.Id))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        string[] ownership = new[]
        {
            testing.Status is RimContextBundleStatuses.Available or RimContextBundleStatuses.Stale
                ? "RimTest/RimLiaison"
                : string.Empty,
            runtime.Status is RimContextBundleStatuses.Available or RimContextBundleStatuses.Stale
                ? "DevBridge2/RimBridgeServer"
                : string.Empty,
            deployment.Status is RimContextBundleStatuses.Available or RimContextBundleStatuses.Stale
                ? "DevBridge2"
                : string.Empty
        }
        .Where(static owner => owner.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

        string[] meaningfulChanges = (repository.Value?.ChangedFiles ?? [])
            .Select(static change => change.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        string[] recentFailures = failures
            .Select(static failure => string.IsNullOrWhiteSpace(failure.RootCause)
                ? failure.SignatureCode
                : failure.SignatureCode + ":" + Bound(failure.RootCause!, 160))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        string? deploymentCorrespondence = deployment.Value?.Correspondence;

        string status = blockers.Length > 0
            ? "blocked"
            : stale || actions.Length > 0 || recentFailures.Length > 0 || meaningfulChanges.Length > 0 ||
                  string.Equals(deploymentCorrespondence, "mismatch", StringComparison.Ordinal) ||
                  string.Equals(deploymentCorrespondence, "runtime-stale", StringComparison.Ordinal) ||
                  string.Equals(deploymentCorrespondence, "unknown", StringComparison.Ordinal)
                    ? "action-required"
                    : !complete
                        ? "unknown"
                        : "healthy";
        return new RimContextAgentSummary
        {
            Status = status,
            ActionRequired = actions,
            Blockers = blockers,
            ReusableEvidence = reusableEvidence,
            Ownership = ownership,
            MeaningfulChanges = meaningfulChanges,
            RecentFailures = recentFailures,
            DeploymentCorrespondence = deploymentCorrespondence
        };
    }

    private static string Bound(string value, int maximum)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    private static RimContextSection<T> SelectSection<T>(
        IReadOnlyList<RimContextProviderSnapshot> snapshots,
        Func<RimContextProviderSnapshot, RimContextSection<T>?> selector,
        DateTimeOffset now,
        int defaultStaleAfterSeconds,
        string sectionName)
    {
        RimContextProviderSnapshot? selectedSnapshot = null;
        RimContextSection<T>? selected = null;
        foreach (RimContextProviderSnapshot snapshot in snapshots)
        {
            RimContextSection<T>? candidate = selector(snapshot);
            if (candidate is null)
            {
                continue;
            }

            if (selected is null || StatusRank(candidate.Status) < StatusRank(selected.Status))
            {
                selectedSnapshot = snapshot;
                selected = candidate;
            }
        }

        if (selected is null)
        {
            return Unknown<T>(
                "CONTEXT_PROVIDER_UNAVAILABLE",
                "No provider supplied the requested context section.",
                sectionName);
        }

        DateTimeOffset observed = (selected.ObservedAtUtc ?? selectedSnapshot!.ObservedAtUtc).ToUniversalTime();
        int staleAfter = selected.StaleAfterSeconds ?? defaultStaleAfterSeconds;
        long ageSeconds = Math.Max(0, (long)(now - observed).TotalSeconds);
        bool stale = selected.Stale ?? ageSeconds > staleAfter;
        string status = stale && string.Equals(
                selected.Status,
                RimContextBundleStatuses.Available,
                StringComparison.Ordinal)
            ? RimContextBundleStatuses.Stale
            : selected.Status;
        return selected with
        {
            Status = status,
            Provider = selected.Provider ?? selectedSnapshot!.ProviderId,
            ObservedAtUtc = observed,
            AgeSeconds = ageSeconds,
            Stale = stale,
            StaleAfterSeconds = staleAfter
        };
    }

    private static int StatusRank(string status) => status switch
    {
        RimContextBundleStatuses.Available => 0,
        RimContextBundleStatuses.Stale => 1,
        RimContextBundleStatuses.Unavailable => 2,
        RimContextBundleStatuses.Unknown => 3,
        _ => 4
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

    private static RimContextBundleRequest NormalizeRequest(RimContextBundleRequest? request)
    {
        RimContextBundleRequest value = request ?? new RimContextBundleRequest();
        if (value.MaxDecisions <= 0 ||
            value.MaxRecentExecutions <= 0 ||
            value.MaxFailures <= 0 ||
            value.MaxExtensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Context bundle limits must be positive.");
        }

        return value;
    }
}
