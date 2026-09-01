namespace RimLiaison.Git;

public enum RepositoryChangeClassificationKind
{
    Unknown,
    MeaningfulSourceOrConfiguration,
    GeneratedTransient,
    BuildOwnedArtifact,
    TrackedProductionArtifact
}

public sealed record RepositoryChangeClassification(
    string Path,
    RepositoryChangeClassificationKind Kind)
{
    public bool IsGenerated => Kind == RepositoryChangeClassificationKind.GeneratedTransient;

    public bool IsMeaningful => !IsGenerated;
}

/// <summary>
/// Explicit ownership facts supplied by a repository descriptor or an
/// artifact transaction. Path heuristics never promote an arbitrary artifact
/// to build-owned status.
/// </summary>
public sealed class RepositoryChangeClassificationContext
{
    private readonly HashSet<string> buildOwnedPaths;
    private readonly HashSet<string> trackedProductionPaths;
    private readonly HashSet<string> generatedPaths;

    public RepositoryChangeClassificationContext(
        IEnumerable<string>? buildOwnedPaths = null,
        IEnumerable<string>? trackedProductionPaths = null,
        IEnumerable<string>? generatedPaths = null)
    {
        this.buildOwnedPaths = NormalizeSet(buildOwnedPaths);
        this.trackedProductionPaths = NormalizeSet(trackedProductionPaths);
        this.generatedPaths = NormalizeSet(generatedPaths);
    }

    internal bool IsBuildOwned(string path) => buildOwnedPaths.Contains(path);

    internal bool IsTrackedProduction(string path) => trackedProductionPaths.Contains(path);

    internal bool IsConfiguredGenerated(string path) =>
        generatedPaths.Contains(path) ||
        generatedPaths.Any(prefix =>
            path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> NormalizeSet(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(RepositoryChangeClassificationPolicy.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public static class RepositoryChangeClassificationPolicy
{
    private static readonly HashSet<string> GeneratedDirectoryNames = new(
        [
            ".git",
            ".rimctx",
            ".rimerror",
            ".vs",
            "artifacts",
            "bin",
            "coverage",
            "obj",
            "testresults"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static RepositoryChangeClassification Classify(
        string path,
        RepositoryChangeClassificationContext? context = null)
    {
        string normalized = NormalizePath(path);
        if (context is not null)
        {
            if (context.IsBuildOwned(normalized))
            {
                return new(normalized, RepositoryChangeClassificationKind.BuildOwnedArtifact);
            }

            if (context.IsTrackedProduction(normalized))
            {
                return new(normalized, RepositoryChangeClassificationKind.TrackedProductionArtifact);
            }

            if (context.IsConfiguredGenerated(normalized))
            {
                return new(normalized, RepositoryChangeClassificationKind.GeneratedTransient);
            }
        }

        if (IsKnownGeneratedPath(normalized))
        {
            return new(normalized, RepositoryChangeClassificationKind.GeneratedTransient);
        }

        return new(
            normalized,
            IsSourceOrConfigurationPath(normalized)
                ? RepositoryChangeClassificationKind.MeaningfulSourceOrConfiguration
                : RepositoryChangeClassificationKind.Unknown);
    }

    public static RepositoryChangeClassification Classify(
        GitRepositoryChange change,
        RepositoryChangeClassificationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Classification != RepositoryChangeClassificationKind.Unknown)
        {
            return new(NormalizePath(change.Path), change.Classification);
        }

        if (change.Generated)
        {
            return new(NormalizePath(change.Path), RepositoryChangeClassificationKind.GeneratedTransient);
        }

        return Classify(change.Path, context);
    }

    public static bool HasMeaningfulChanges(IEnumerable<GitRepositoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.Any(static change => Classify(change).IsMeaningful);
    }

    public static IReadOnlyList<string> MeaningfulPaths(
        IEnumerable<GitRepositoryChange> changes,
        int maximum = 8)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (maximum <= 0)
        {
            return [];
        }

        return changes
            .Where(static change => Classify(change).IsMeaningful)
            .Select(static change => NormalizePath(change.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToArray();
    }

    public static bool IsGeneratedPath(string path) => Classify(path).IsGenerated;

    public static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static bool IsKnownGeneratedPath(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(GeneratedDirectoryNames.Contains))
        {
            return true;
        }

        if (segments.Length >= 2 &&
            segments[0].Equals(".rimdev", StringComparison.OrdinalIgnoreCase) &&
            segments[1] is "failure-handoffs" or "observability" or "profiles" or "qualification" or "validation-proofs")
        {
            return true;
        }

        if (segments.Length == 2 &&
            segments[0].Equals(".rimdev", StringComparison.OrdinalIgnoreCase) &&
            segments[1].EndsWith(".local.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = segments[^1];
        int developmentProjectsIndex = Array.FindIndex(
            segments,
            static segment => segment.Equals("DevelopmentProjects", StringComparison.OrdinalIgnoreCase));
        return developmentProjectsIndex >= 0 && IsLegacyDescriptorRecoveryFile(fileName);
    }

    private static bool IsSourceOrConfigurationPath(string normalized)
    {
        if (normalized.Equals(".rimdev/stack.json", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("source/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("defs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("about/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(normalized);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".toml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyDescriptorRecoveryFile(string fileName)
    {
        const string backupMarker = ".recovery-backup-";
        int backupIndex = fileName.LastIndexOf(backupMarker, StringComparison.OrdinalIgnoreCase);
        if (backupIndex >= 0)
        {
            string identity = fileName[(backupIndex + backupMarker.Length)..];
            return identity.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                IsGuidN(identity[..^5]);
        }

        const string temporaryMarker = ".recovery-";
        int temporaryIndex = fileName.LastIndexOf(temporaryMarker, StringComparison.OrdinalIgnoreCase);
        if (temporaryIndex < 0)
        {
            return false;
        }

        string temporaryIdentity = fileName[(temporaryIndex + temporaryMarker.Length)..];
        return temporaryIdentity.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
            IsGuidN(temporaryIdentity[..^4]);
    }

    private static bool IsGuidN(string value) =>
        value.Length == 32 && Guid.TryParseExact(value, "N", out _);
}
