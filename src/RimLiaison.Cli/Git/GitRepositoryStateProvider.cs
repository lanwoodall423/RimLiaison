using System.Diagnostics;
using System.Text;
using RimLiaison.Execution;

namespace RimLiaison.Git;

public interface IGitRepositoryStateProvider
{
    Task<GitRepositoryStateResult> ReadAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task<GitRepositoryStateResult> ReadWorktreeAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        ReadAsync(rootPath, cancellationToken);
}

public sealed record GitRepositoryStateResult(
    bool Resolved,
    GitRepositoryStateSnapshot? State = null,
    string? ErrorCode = null,
    string? Error = null);

public sealed record GitRepositoryStateSnapshot(
    string RootPath,
    string Identity,
    string? Branch,
    string? HeadSha,
    string? UpstreamSha,
    int? Ahead,
    int? Behind,
    bool Dirty,
    IReadOnlyList<GitRepositoryChange> Changes,
    string? SourceFingerprint = null,
    string? UpstreamName = null);

public sealed record GitRepositoryChange(
    string Path,
    string Status,
    bool Untracked,
    bool Generated,
    string? OriginalPath = null,
    RepositoryChangeClassificationKind Classification = RepositoryChangeClassificationKind.Unknown);

/// <summary>
/// Read-only Git metadata for context snapshots. It is separate from the
/// affected-selection provider because generated files must be visible here,
/// while affected selection intentionally filters them.
/// </summary>
public sealed class SystemGitRepositoryStateProvider : IGitRepositoryStateProvider
{
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public async Task<GitRepositoryStateResult> ReadAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Failure("GIT_ROOT_INVALID", "A Git workspace root is required.");
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Failure("GIT_ROOT_INVALID", "The Git workspace root is invalid.");
        }

        if (!Directory.Exists(fullRoot))
        {
            return Failure("GIT_ROOT_NOT_FOUND", "The Git workspace root does not exist.");
        }

        GitCommandResult topLevel = await RunGitAsync(
                fullRoot,
                ["rev-parse", "--show-toplevel"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!topLevel.Succeeded)
        {
            return Failure(topLevel.ErrorCode ?? "GIT_DISCOVERY_FAILED", "Git repository state could not be resolved.");
        }

        string repositoryRoot = topLevel.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return Failure("GIT_REPOSITORY_ROOT_INVALID", "Git returned an empty repository root.");
        }

        GitCommandResult branch = await RunGitAsync(
                fullRoot,
                ["branch", "--show-current"],
                cancellationToken)
            .ConfigureAwait(false);
        GitCommandResult head = await RunGitAsync(
                fullRoot,
                ["rev-parse", "HEAD"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!branch.Succeeded || !head.Succeeded)
        {
            return Failure("GIT_METADATA_FAILED", "Git branch or HEAD state could not be resolved.");
        }

        GitCommandResult upstream = await RunGitAsync(
                fullRoot,
                ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
                cancellationToken)
            .ConfigureAwait(false);
        string? upstreamName = upstream.Succeeded ? TrimOrNull(upstream.Stdout) : null;
        string? upstreamSha = null;
        int? ahead = null;
        int? behind = null;
        if (upstreamName is not null)
        {
            GitCommandResult upstreamRevision = await RunGitAsync(
                    fullRoot,
                    ["rev-parse", "@{upstream}"],
                    cancellationToken)
                .ConfigureAwait(false);
            GitCommandResult divergence = await RunGitAsync(
                    fullRoot,
                    ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"],
                    cancellationToken)
                .ConfigureAwait(false);
            upstreamSha = upstreamRevision.Succeeded ? TrimOrNull(upstreamRevision.Stdout) : null;
            if (divergence.Succeeded)
            {
                string[] counts = divergence.Stdout
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (counts.Length == 2 &&
                    int.TryParse(counts[0], out int parsedAhead) &&
                    int.TryParse(counts[1], out int parsedBehind) &&
                    parsedAhead >= 0 &&
                    parsedBehind >= 0)
                {
                    ahead = parsedAhead;
                    behind = parsedBehind;
                }
            }
        }

        GitCommandResult status = await RunGitAsync(
                fullRoot,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!status.Succeeded || !TryParseStatus(status.Stdout, out GitRepositoryChange[] changes))
        {
            return Failure("GIT_STATUS_INVALID", "Git returned an invalid working-tree status.");
        }

        string canonicalRoot = Path.GetFullPath(repositoryRoot);
        GitRepositoryChange[] orderedChanges = changes
            .OrderBy(static change => change.Generated)
            .ThenBy(static change => change.Path, StringComparer.Ordinal)
            .ThenBy(static change => change.OriginalPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static change => change.Status, StringComparer.Ordinal)
            .ToArray();
        string? sourceFingerprint = null;
        GitRepositoryChange[] meaningfulChanges = orderedChanges
            .Where(static change => !change.Generated)
            .ToArray();
        if (meaningfulChanges.Length > 0 &&
            WorktreeFingerprint.TryCompute(
                canonicalRoot,
                meaningfulChanges
                    .SelectMany(static change => new[] { change.Path, change.OriginalPath })
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Select(static path => path!)
                    .ToArray(),
                out string computedFingerprint,
                out _))
        {
            sourceFingerprint = computedFingerprint;
        }

        var state = new GitRepositoryStateSnapshot(
            canonicalRoot,
            "git:" + NormalizeIdentity(canonicalRoot),
            TrimOrNull(branch.Stdout),
            TrimOrNull(head.Stdout),
            upstreamSha,
            ahead,
            behind,
            orderedChanges.Length > 0,
            orderedChanges,
            sourceFingerprint,
            upstreamName);
        return new GitRepositoryStateResult(true, state);
    }

    public async Task<GitRepositoryStateResult> ReadWorktreeAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Failure("GIT_ROOT_INVALID", "A valid Git workspace root is required.");
        }

        GitCommandResult topLevel = await RunGitAsync(
                rootPath,
                ["rev-parse", "--show-toplevel"],
                cancellationToken)
            .ConfigureAwait(false);
        GitCommandResult head = await RunGitAsync(
                rootPath,
                ["rev-parse", "HEAD"],
                cancellationToken)
            .ConfigureAwait(false);
        GitCommandResult status = await RunGitAsync(
                rootPath,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!topLevel.Succeeded || !head.Succeeded || !status.Succeeded ||
            !TryParseStatus(status.Stdout, out GitRepositoryChange[] changes))
        {
            return Failure("GIT_STATUS_INVALID", "Git returned an invalid working-tree snapshot.");
        }

        string canonicalRoot = Path.GetFullPath(topLevel.Stdout.Trim());
        return new GitRepositoryStateResult(
            true,
            new GitRepositoryStateSnapshot(
                canonicalRoot,
                "git:" + NormalizeIdentity(canonicalRoot),
                null,
                TrimOrNull(head.Stdout),
                null,
                null,
                null,
                changes.Length > 0,
                changes
                    .OrderBy(static change => change.Path, StringComparer.Ordinal)
                    .ToArray()));
    }

    private static bool TryParseStatus(string output, out GitRepositoryChange[] changes)
    {
        var parsed = new List<GitRepositoryChange>();
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < records.Length; index++)
        {
            string record = records[index];
            if (record.Length < 3)
            {
                changes = [];
                return false;
            }

            string status = record[..2];
            string path = record[3..].Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path))
            {
                changes = [];
                return false;
            }

            bool untracked = status.IndexOf('?') >= 0;
            string? originalPath = null;
            if (status.IndexOf('R') >= 0 || status.IndexOf('C') >= 0)
            {
                if (index + 1 >= records.Length)
                {
                    changes = [];
                    return false;
                }

                originalPath = records[++index].Replace('\\', '/');
            }

            RepositoryChangeClassification classification =
                RepositoryChangeClassificationPolicy.Classify(path);
            parsed.Add(new GitRepositoryChange(
                path,
                status,
                untracked,
                classification.IsGenerated,
                originalPath,
                classification.Kind));
        }

        changes = parsed.ToArray();
        return true;
    }


    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return FailureResult("GIT_START_FAILED");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return FailureResult("GIT_START_FAILED");
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            return FailureResult(
                cancellationToken.IsCancellationRequested ? "GIT_CANCELLED" : "GIT_TIMEOUT");
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return FailureResult("GIT_OUTPUT_LIMIT_EXCEEDED");
        }
        catch (OperationCanceledException)
        {
            return FailureResult("GIT_CANCELLED");
        }

        if (process.ExitCode != 0)
        {
            return FailureResult("GIT_COMMAND_FAILED");
        }

        return new GitCommandResult(true, stdoutTask.Result, stderrTask.Result, null);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var output = new StringBuilder();
        int byteCount = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToString();
            }

            byteCount += Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (byteCount > MaximumOutputBytes)
            {
                throw new InvalidOperationException("Git output exceeded the bounded limit.");
            }

            output.Append(buffer, 0, count);
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
        }
    }

    private static string? TrimOrNull(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string NormalizeIdentity(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant();

    private static GitRepositoryStateResult Failure(string code, string error) =>
        new(false, null, code, error);

    private static GitCommandResult FailureResult(string code) =>
        new(false, string.Empty, string.Empty, code);

    private sealed record GitCommandResult(
        bool Succeeded,
        string Stdout,
        string Stderr,
        string? ErrorCode);
}
