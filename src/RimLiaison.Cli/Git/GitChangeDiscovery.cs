using System.Diagnostics;
using System.Text;

namespace RimLiaison.Git;

public interface IGitChangeProvider
{
    Task<GitChangeDiscoveryResult> DiscoverAsync(
        string rootPath,
        string? baseReference = null,
        CancellationToken cancellationToken = default);
}

public sealed record GitChangedPath(
    string Path,
    string Status,
    string? OriginalPath = null)
{
    public bool IsDeleted => Status.IndexOf('D') >= 0;

    public bool IsRenamed =>
        Status.IndexOf('R') >= 0 ||
        OriginalPath is not null;
}

public sealed record GitChangeDiscoveryResult(
    bool Resolved,
    IReadOnlyList<string> Paths,
    string? ErrorCode = null,
    string? Error = null)
{
    public IReadOnlyList<GitChangedPath> Changes { get; init; } = [];
}

public sealed class SystemGitChangeProvider : IGitChangeProvider
{
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> GeneratedDirectories = new(
        [
            ".git",
            ".rimctx",
            ".vs",
            "artifacts",
            "bin",
            "coverage",
            "obj",
            "testresults"
        ],
        StringComparer.OrdinalIgnoreCase);

    public async Task<GitChangeDiscoveryResult> DiscoverAsync(
        string rootPath,
        string? baseReference = null,
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
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return Failure("GIT_ROOT_INVALID", "The Git workspace root is invalid.");
        }

        if (!Directory.Exists(fullRoot))
        {
            return Failure("GIT_ROOT_NOT_FOUND", "The Git workspace root does not exist.");
        }

        GitProcessResult status = await RunGitAsync(
                fullRoot,
                [
                    "--no-optional-locks",
                    "status",
                    "--porcelain=v1",
                    "-z",
                    "--untracked-files=all"
                ],
                cancellationToken)
            .ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return Failure(
                status.ErrorCode ?? "GIT_DISCOVERY_FAILED",
                status.Error ?? "Git working-tree state could not be resolved.");
        }

        var paths = new List<string>();
        var changes = new List<GitChangedPath>();
        if (!TryParseStatus(
                status.Stdout,
                fullRoot,
                paths,
                changes,
                out string? parseError))
        {
            return Failure("GIT_STATUS_INVALID", parseError!);
        }

        if (!string.IsNullOrWhiteSpace(baseReference))
        {
            GitProcessResult diff = await RunGitAsync(
                fullRoot,
                [
                    "--no-optional-locks",
                    "diff",
                    "--name-status",
                    "-z",
                    "--diff-filter=ACDMRTUXB",
                    baseReference!,
                    "--"
                ],
                cancellationToken)
                .ConfigureAwait(false);
            if (!diff.Succeeded)
            {
                return Failure(
                    diff.ErrorCode ?? "GIT_BASE_INVALID",
                    diff.Error ?? "The requested Git base could not be resolved.");
            }

            if (!TryParseNameStatus(
                    diff.Stdout,
                    fullRoot,
                    paths,
                    changes,
                    out parseError))
            {
                return Failure("GIT_STATUS_INVALID", parseError!);
            }
        }

        return new GitChangeDiscoveryResult(
            true,
            paths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray())
        {
            Changes = changes
                .Distinct()
                .OrderBy(static change => change.Path, StringComparer.Ordinal)
                .ThenBy(static change => change.OriginalPath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static change => change.Status, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static bool TryParseStatus(
        string output,
        string rootPath,
        ICollection<string> paths,
        ICollection<GitChangedPath> changes,
        out string? error)
    {
        error = null;
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < records.Length; index++)
        {
            string record = records[index];
            if (record.Length < 3)
            {
                error = "Git returned a malformed porcelain status record.";
                return false;
            }

            string status = record[..2];
            if (status is "!!")
            {
                continue;
            }

            if (!TryNormalizePath(
                    record[3..],
                    rootPath,
                    out string? path,
                    out error))
            {
                return false;
            }

            string? originalPath = null;
            if (status.IndexOf('R') >= 0 || status.IndexOf('C') >= 0)
            {
                if (index + 1 >= records.Length ||
                    !TryNormalizePath(
                        records[++index],
                        rootPath,
                        out originalPath,
                        out error))
                {
                    error ??= "Git returned an incomplete rename status record.";
                    return false;
                }
            }

            AddChange(path, originalPath, status, paths, changes, originalPath);
        }

        return true;
    }

    private static bool TryParseNameStatus(
        string output,
        string rootPath,
        ICollection<string> paths,
        ICollection<GitChangedPath> changes,
        out string? error)
    {
        error = null;
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < records.Length; index++)
        {
            string record = records[index];
            if (record.Length == 0)
            {
                error = "Git returned a malformed name-status record.";
                return false;
            }

            int separator = record.IndexOf('\t');
            string status = separator >= 0
                ? record[..separator]
                : record;
            string? firstValue = separator >= 0
                ? record[(separator + 1)..]
                : null;
            bool renameOrCopy = status.IndexOf('R') >= 0 ||
                status.IndexOf('C') >= 0;

            if (string.IsNullOrEmpty(firstValue))
            {
                if (index + 1 >= records.Length)
                {
                    error = "Git returned an incomplete name-status record.";
                    return false;
                }

                firstValue = records[++index];
            }

            if (!TryNormalizePath(
                    firstValue,
                    rootPath,
                    out string? firstPath,
                    out error))
            {
                return false;
            }

            string? secondPath = null;
            if (renameOrCopy)
            {
                if (index + 1 >= records.Length ||
                    !TryNormalizePath(
                        records[++index],
                        rootPath,
                        out secondPath,
                        out error))
                {
                    error ??= "Git returned an incomplete rename name-status record.";
                    return false;
                }
            }

            // `git diff --name-status` presents rename/copy paths as
            // original followed by destination. Keep both in the path set
            // and retain their relationship for conservative selection.
            AddChange(
                renameOrCopy ? secondPath ?? firstPath : firstPath,
                renameOrCopy ? firstPath : null,
                status,
                paths,
                changes,
                secondPath);
        }

        return true;
    }

    private static void AddChange(
        string? path,
        string? originalPath,
        string status,
        ICollection<string> paths,
        ICollection<GitChangedPath> changes,
        string? additionalPath = null)
    {
        if (path is not null)
        {
            paths.Add(path);
        }

        if (additionalPath is not null &&
            !string.Equals(path, additionalPath, StringComparison.Ordinal))
        {
            paths.Add(additionalPath);
        }

        if (path is not null || originalPath is not null)
        {
            changes.Add(new GitChangedPath(
                path ?? originalPath!,
                status,
                originalPath));
        }
    }

    private static bool TryNormalizePath(
        string value,
        string rootPath,
        out string? normalizedPath,
        out string? error)
    {
        normalizedPath = null;
        error = null;
        string path = value.TrimEnd('\r', '\n');
        if (path.Length == 0)
        {
            error = "Git returned an empty changed path.";
            return false;
        }

        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            error = "Git returned an absolute changed path.";
            return false;
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => GeneratedDirectories.Contains(segment)))
        {
            return true;
        }

        string absolute;
        try
        {
            absolute = Path.GetFullPath(Path.Combine(rootPath, path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            error = "Git returned an invalid changed path.";
            return false;
        }

        if (!IsWithin(rootPath, absolute))
        {
            error = "Git returned a changed path outside the workspace root.";
            return false;
        }

        normalizedPath = path.TrimStart('/');
        return true;
    }

    private static bool IsWithin(string rootPath, string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)),
                path,
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GitProcessResult> RunGitAsync(
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
                return FailureResult("GIT_START_FAILED", "Git did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return FailureResult("GIT_START_FAILED", "Git could not be started.");
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
                cancellationToken.IsCancellationRequested ? "GIT_CANCELLED" : "GIT_TIMEOUT",
                cancellationToken.IsCancellationRequested
                    ? "Git change discovery was cancelled."
                    : "Git change discovery timed out.");
        }

        string stdout;
        string stderr;
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
        }
        catch (InvalidOperationException)
        {
            return FailureResult(
                "GIT_OUTPUT_LIMIT_EXCEEDED",
                "Git change discovery exceeded its bounded output limit.");
        }
        catch (OperationCanceledException)
        {
            return FailureResult(
                "GIT_CANCELLED",
                "Git change discovery was cancelled.");
        }
        if (process.ExitCode != 0)
        {
            return FailureResult(
                "GIT_DISCOVERY_FAILED",
                "Git could not resolve the working-tree state.");
        }

        return new GitProcessResult(true, stdout, null, null);
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
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
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

    private static GitChangeDiscoveryResult Failure(string code, string error) =>
        new(false, [], code, error);

    private static GitProcessResult FailureResult(string code, string error) =>
        new(false, string.Empty, code, error);

    private sealed record GitProcessResult(
        bool Succeeded,
        string Stdout,
        string? ErrorCode,
        string? Error);
}
