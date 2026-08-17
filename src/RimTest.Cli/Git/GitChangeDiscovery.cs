using System.Diagnostics;
using System.Text;

namespace RimTest.Git;

public interface IGitChangeProvider
{
    Task<GitChangeDiscoveryResult> DiscoverAsync(
        string rootPath,
        string? baseReference = null,
        CancellationToken cancellationToken = default);
}

public sealed record GitChangeDiscoveryResult(
    bool Resolved,
    IReadOnlyList<string> Paths,
    string? ErrorCode = null,
    string? Error = null);

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
        if (!TryParseStatus(status.Stdout, fullRoot, paths, out string? parseError))
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
                        "--name-only",
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

            if (!TryParseNameOnly(diff.Stdout, fullRoot, paths, out parseError))
            {
                return Failure("GIT_STATUS_INVALID", parseError!);
            }
        }

        return new GitChangeDiscoveryResult(
            true,
            paths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool TryParseStatus(
        string output,
        string rootPath,
        ICollection<string> paths,
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

            if (!TryAddPath(record[3..], rootPath, paths, out error))
            {
                return false;
            }

            if (status.IndexOf('R') >= 0 || status.IndexOf('C') >= 0)
            {
                if (index + 1 >= records.Length ||
                    !TryAddPath(records[++index], rootPath, paths, out error))
                {
                    error ??= "Git returned an incomplete rename status record.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryParseNameOnly(
        string output,
        string rootPath,
        ICollection<string> paths,
        out string? error)
    {
        error = null;
        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryAddPath(record, rootPath, paths, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddPath(
        string value,
        string rootPath,
        ICollection<string> paths,
        out string? error)
    {
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

        paths.Add(path.TrimStart('/'));
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
