using System.Text.Json.Serialization;

namespace RimLiaison.RimDev;

public static class RimDevSchemas
{
    public const string Workspace = "rimdev-workspace/v1";
    public const string Result = "rimdev-result/v1";
    public const string BuildEvidence = "rimdev-build-evidence/v1";
}

public enum RimDevOperation
{
    Menu,
    Help,
    Status,
    Sync,
    Build,
    Test,
    Deploy,
    Push,
    Merge,
    All
}

public sealed record RimDevRunOptions(
    RimDevOperation Operation,
    string? RootPath,
    bool Confirm,
    bool Json,
    string? StateDirectory = null,
    TextReader? Input = null);

public sealed record RimDevWorkspaceConfiguration(
    string SchemaVersion,
    IReadOnlyList<RimDevWorkspaceRepository> Repositories,
    string? DeploymentRoot);

public sealed record RimDevWorkspaceRepository(
    string Path,
    IReadOnlyList<string> Dependencies,
    string? DeploymentRoot,
    string? DeploymentTarget,
    string? BuildProject,
    string? Configuration);

public sealed record RimDevRepository(
    string Name,
    string Path,
    string? ManifestPath,
    StackManifestState Manifest,
    IReadOnlyList<string> Dependencies,
    string? DeploymentRoot,
    string? DeploymentTarget,
    string? BuildProject,
    string Configuration);

public sealed record StackManifestState(
    bool IsValid,
    string? Project,
    string? DevBridgeProject,
    string? Catalog,
    string? FallbackSuite,
    string? RimBridge,
    string? ErrorCode,
    string? Error);

public sealed record RimDevRepositoryResult(
    string Name,
    string Path,
    string Status,
    string Summary,
    string? Branch = null,
    bool? Dirty = null,
    int? Ahead = null,
    int? Behind = null,
    string? Upstream = null,
    string? Build = null,
    string? Deployment = null,
    string? Merge = null,
    string? ErrorCode = null,
    string? NextAction = null,
    IReadOnlyList<string>? Deployed = null);

public sealed record RimDevResult(
    string Command,
    string Status,
    IReadOnlyList<RimDevRepositoryResult> Repositories,
    IReadOnlyList<string> NextActions,
    bool MergePerformed = false,
    string? ErrorCode = null,
    string SchemaVersion = RimDevSchemas.Result);

public sealed record RimDevProcessResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut = false,
    bool Cancelled = false,
    string? StartError = null)
{
    public bool Succeeded => ExitCode == 0 &&
        !TimedOut &&
        !Cancelled &&
        string.IsNullOrWhiteSpace(StartError);
}

public interface IRimDevProcessRunner
{
    Task<RimDevProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record RimDevGitResult(
    bool Succeeded,
    int ExitCode,
    string Stdout,
    string Stderr,
    string? ErrorCode = null)
{
    public static RimDevGitResult Failure(string code, string? error = null) =>
        new(false, -1, string.Empty, error ?? string.Empty, code);
}

public interface IRimDevGitClient
{
    Task<RimDevGitResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed record RimDevRepositoryObservation(
    RimDevRepository Repository,
    Git.GitRepositoryStateSnapshot? State,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> MeaningfulPaths,
    IReadOnlyList<string> BuildPaths,
    IReadOnlyList<string> TestPaths,
    IReadOnlyList<string> Remotes,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool IsAvailable => State is not null && ErrorCode is null;
}

public sealed record RimDevBuildEvidence(
    string RepositoryPath,
    string ProjectPath,
    string Configuration,
    string? HeadSha,
    string? ChangedPathsFingerprint,
    IReadOnlyList<string> IdentityPaths,
    string OutputPath,
    string OutputSha256,
    DateTimeOffset BuiltAtUtc,
    string SchemaVersion = RimDevSchemas.BuildEvidence,
    string? DependencyFingerprint = null);

public sealed record RimDevBuildTarget(
    string Path,
    string AssemblyName,
    string Configuration);

public sealed record RimDevPullRequest(
    int Number,
    string Title,
    string HeadBranch,
    string BaseBranch,
    string? HeadSha,
    string? BaseSha,
    bool IsDraft,
    string? Mergeable,
    IReadOnlyList<string> CheckStates,
    string? Url);

public sealed record RimDevPullRequestQueryResult(
    bool Available,
    IReadOnlyList<RimDevPullRequest> PullRequests,
    string? ErrorCode = null,
    string? Error = null);

public interface IRimDevPullRequestProvider
{
    Task<RimDevPullRequestQueryResult> FindAsync(
        string repositoryPath,
        string branch,
        CancellationToken cancellationToken = default);

    Task<RimDevProcessResult> MergeAsync(
        string repositoryPath,
        RimDevPullRequest pullRequest,
        CancellationToken cancellationToken = default);
}

public sealed record RimDevBuildExecutionResult(
    bool Succeeded,
    RimDevBuildTarget? Target,
    RimDevBuildEvidence? Evidence,
    string Summary,
    string? ErrorCode = null,
    string? Output = null);

public sealed record RimDevTestExecutionResult(
    bool Succeeded,
    string Status,
    string Summary,
    bool ArtifactFreshnessProven,
    IReadOnlyList<string> Deployed = null!,
    string? ErrorCode = null,
    string? Output = null);

public sealed record RimDevTestEvidence(
    string RepositoryPath,
    string? HeadSha,
    string SourceIdentity,
    IReadOnlyList<string> Deployed,
    DateTimeOffset RecordedAtUtc,
    string SchemaVersion = "rimdev-test-evidence/v1",
    string? DependencyFingerprint = null);
