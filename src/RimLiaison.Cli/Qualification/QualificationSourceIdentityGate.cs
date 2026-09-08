using RimLiaison.Execution;
using RimLiaison.Git;

namespace RimLiaison.Qualification;

internal sealed record QualificationSourceCheckpoint(
    string SourceRoot,
    string SourceCommit,
    string SourceFingerprint,
    IReadOnlyList<string> MeaningfulPaths);

internal sealed record QualificationSourceGateResult(
    bool Succeeded,
    string? ErrorCode,
    string? Error,
    QualificationSourceCheckpoint? Checkpoint);

internal static class QualificationSourceIdentityGate
{
    public static async Task<QualificationSourceGateResult> CaptureAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
            .ReadAsync(sourceRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!source.Resolved || source.State is null || string.IsNullOrWhiteSpace(source.State.HeadSha))
        {
            return Failure(
                "QUALIFICATION_SOURCE_UNRESOLVED",
                "Qualification could not resolve an immutable source commit.");
        }

        IReadOnlyList<string> meaningfulPaths = RepositoryChangeClassificationPolicy
            .MeaningfulPaths(source.State.Changes);
        if (meaningfulPaths.Count > 0)
        {
            return Failure(
                "QUALIFICATION_SOURCE_DIRTY",
                "Qualification requires a clean immutable source checkout before burn-in.",
                meaningfulPaths);
        }

        string[] sourcePaths = CandidateSourcePaths(sourceRoot);
        if (!WorktreeFingerprint.TryCompute(
                sourceRoot,
                sourcePaths,
                out string fingerprint,
                out string? fingerprintError))
        {
            return Failure(
                "QUALIFICATION_SOURCE_FINGERPRINT_UNAVAILABLE",
                fingerprintError ?? "Qualification could not compute the source fingerprint.");
        }

        return new QualificationSourceGateResult(
            true,
            null,
            null,
            new QualificationSourceCheckpoint(
                Path.GetFullPath(sourceRoot),
                source.State.HeadSha,
                fingerprint,
                []));
    }

    public static async Task<QualificationSourceGateResult> RecheckAsync(
        QualificationSourceCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
            .ReadAsync(checkpoint.SourceRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!source.Resolved || source.State is null || string.IsNullOrWhiteSpace(source.State.HeadSha))
        {
            return Failure(
                "QUALIFICATION_SOURCE_CHANGED_DURING_RUN",
                "Qualification lost the immutable source identity during burn-in.");
        }

        IReadOnlyList<string> meaningfulPaths = RepositoryChangeClassificationPolicy
            .MeaningfulPaths(source.State.Changes);
        string[] sourcePaths = CandidateSourcePaths(checkpoint.SourceRoot);
        if (!WorktreeFingerprint.TryCompute(
                checkpoint.SourceRoot,
                sourcePaths,
                out string fingerprint,
                out _))
        {
            return Failure(
                "QUALIFICATION_SOURCE_CHANGED_DURING_RUN",
                "Qualification could not re-establish the source identity after burn-in.");
        }

        if (!string.Equals(source.State.HeadSha, checkpoint.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            meaningfulPaths.Count > 0 ||
            !string.Equals(fingerprint, checkpoint.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "QUALIFICATION_SOURCE_CHANGED_DURING_RUN",
                "Qualification source identity changed during burn-in.",
                meaningfulPaths);
        }

        return new QualificationSourceGateResult(true, null, null, checkpoint);
    }

    private static QualificationSourceGateResult Failure(
        string code,
        string error,
        IReadOnlyList<string>? meaningfulPaths = null) =>
        new(
            false,
            code,
            meaningfulPaths is { Count: > 0 }
                ? error + " Changed paths: " + string.Join(", ", meaningfulPaths)
                : error,
            null);

    private static string[] CandidateSourcePaths(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        return Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fullRoot, path).Replace('\\', '/'))
            .Where(path => !RepositoryChangeClassificationPolicy.Classify(path).IsGenerated)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
