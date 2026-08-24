using System.Security.Cryptography;
using System.Text;
using RimLiaison.Git;

namespace RimLiaison.RimDev;

public sealed class SystemRimDevProcessRunner : IRimDevProcessRunner
{
    private const int MaximumOutputChars = 512 * 1024;

    public async Task<RimDevProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Directory.Exists(workingDirectory) ||
            string.IsNullOrWhiteSpace(fileName))
        {
            return new(null, string.Empty, string.Empty, StartError: "The command working directory or executable is invalid.");
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
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

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(null, string.Empty, string.Empty, StartError: "The command could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(null, string.Empty, string.Empty, StartError: Bound(exception.Message));
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            return new(
                null,
                await SafeResultAsync(stdoutTask).ConfigureAwait(false),
                await SafeResultAsync(stderrTask).ConfigureAwait(false),
                TimedOut: !cancellationToken.IsCancellationRequested,
                Cancelled: cancellationToken.IsCancellationRequested,
                StartError: cancellationToken.IsCancellationRequested ? "The command was cancelled." : "The command timed out.");
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(null, string.Empty, string.Empty, Cancelled: cancellationToken.IsCancellationRequested);
        }
        catch (InvalidOperationException exception)
        {
            return new(process.ExitCode, string.Empty, string.Empty, StartError: Bound(exception.Message));
        }

        return new(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var output = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length + read > MaximumOutputChars)
            {
                throw new InvalidOperationException("Command output exceeded the bounded limit.");
            }

            output.Append(buffer, 0, read);
        }
    }

    private static async Task<string> SafeResultAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void TryTerminate(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 4096 ? trimmed : trimmed[..4096];
    }
}

public sealed class SystemRimDevGitClient : IRimDevGitClient
{
    private readonly IRimDevProcessRunner processRunner;

    public SystemRimDevGitClient(IRimDevProcessRunner? processRunner = null)
    {
        this.processRunner = processRunner ?? new SystemRimDevProcessRunner();
    }

    public async Task<RimDevGitResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        RimDevProcessResult result = await processRunner.RunAsync(
                repositoryPath,
                "git",
                arguments,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.Succeeded,
            result.ExitCode ?? -1,
            result.Stdout,
            result.Stderr,
            result.StartError is not null
                ? "GIT_START_FAILED"
                : result.TimedOut
                    ? "GIT_TIMEOUT"
                    : result.Cancelled
                        ? "GIT_CANCELLED"
                        : result.Succeeded
                            ? null
                            : "GIT_COMMAND_FAILED");
    }
}

public sealed class RimDevGitReader
{
    private readonly IRimDevGitClient git;
    private readonly IGitRepositoryStateProvider stateProvider;

    public RimDevGitReader(
        IRimDevGitClient git,
        IGitRepositoryStateProvider? stateProvider = null)
    {
        this.git = git;
        this.stateProvider = stateProvider ?? new SystemGitRepositoryStateProvider();
    }

    public async Task<RimDevRepositoryObservation> ReadAsync(
        RimDevRepository repository,
        CancellationToken cancellationToken = default)
    {
        if (!repository.Manifest.IsValid)
        {
            return new(
                repository,
                null,
                [],
                [],
                [],
                [],
                [],
                repository.Manifest.ErrorCode ?? "STACK_MANIFEST_INVALID",
                repository.Manifest.Error);
        }

        GitRepositoryStateResult stateResult = await stateProvider
            .ReadAsync(repository.Path, cancellationToken)
            .ConfigureAwait(false);
        if (!stateResult.Resolved || stateResult.State is null)
        {
            return new(
                repository,
                null,
                [],
                [],
                [],
                [],
                [],
                stateResult.ErrorCode ?? "GIT_STATE_UNAVAILABLE",
                stateResult.Error);
        }

        GitRepositoryStateSnapshot state = stateResult.State;
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.UpstreamName is not null)
        {
            await AddPathsAsync(
                    repository.Path,
                    ["diff", "--name-only", "-z", "@{upstream}...HEAD"],
                    changedPaths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await AddPathsAsync(
                    repository.Path,
                    ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "-z", "HEAD"],
                    changedPaths,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await AddPathsAsync(
                repository.Path,
                ["diff", "--name-only", "-z", "HEAD"],
                changedPaths,
                cancellationToken)
            .ConfigureAwait(false);
        await AddPathsAsync(
                repository.Path,
                ["diff", "--cached", "--name-only", "-z"],
                changedPaths,
                cancellationToken)
            .ConfigureAwait(false);
        await AddPathsAsync(
                repository.Path,
                ["ls-files", "--others", "--exclude-standard", "-z"],
                changedPaths,
                cancellationToken)
            .ConfigureAwait(false);

        string[] meaningful = changedPaths
            .Where(path => !IsGeneratedPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] buildPaths = meaningful
            .Where(IsBuildRelevant)
            .ToArray();
        string[] testPaths = meaningful
            .Where(IsTestRelevant)
            .ToArray();

        if (state.Dirty &&
            changedPaths.Count > 0 &&
            meaningful.Length == 0 &&
            state.Changes.Count > 0 &&
            state.Changes.All(change => change.Generated))
        {
            state = state with { Dirty = false };
        }

        RimDevGitResult remotesResult = await git.RunAsync(
                repository.Path,
                ["remote"],
                cancellationToken)
            .ConfigureAwait(false);
        string[] remotes = remotesResult.Succeeded
            ? Lines(remotesResult.Stdout)
            : [];
        return new(
            repository,
            state,
            changedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            meaningful,
            buildPaths,
            testPaths,
            remotes,
            remotesResult.Succeeded ? null : "GIT_REMOTES_UNAVAILABLE",
            remotesResult.Succeeded ? null : Bound(remotesResult.Stderr));
    }

    private async Task AddPathsAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        ISet<string> paths,
        CancellationToken cancellationToken)
    {
        RimDevGitResult result = await git.RunAsync(
                repositoryPath,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return;
        }

        foreach (string path in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Replace('\\', '/'));
            }
        }
    }

    public static bool IsGeneratedPath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        string lower = normalized.ToLowerInvariant();
        return lower.StartsWith(".git/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimctx/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimdev/observability/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimdev/profiles/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimdev/validation-proofs/", StringComparison.Ordinal) ||
            lower.StartsWith(".rimerror/", StringComparison.Ordinal) ||
            lower.StartsWith(".vs/", StringComparison.Ordinal) ||
            lower.StartsWith("coverage/", StringComparison.Ordinal) ||
            lower.StartsWith("testresults/", StringComparison.Ordinal) ||
            lower.Contains("/bin/", StringComparison.Ordinal) ||
            lower.Contains("/obj/", StringComparison.Ordinal) ||
            lower.StartsWith("bin/", StringComparison.Ordinal) ||
            lower.StartsWith("obj/", StringComparison.Ordinal) ||
            lower.StartsWith("artifacts/", StringComparison.Ordinal) ||
            lower.EndsWith(".dll", StringComparison.Ordinal) ||
            lower.EndsWith(".pdb", StringComparison.Ordinal) ||
            lower.EndsWith(".g.cs", StringComparison.Ordinal) ||
            lower.EndsWith(".generated.cs", StringComparison.Ordinal) ||
            lower.EndsWith(".designer.cs", StringComparison.Ordinal) ||
            lower.EndsWith(".deps.json", StringComparison.Ordinal) ||
            lower.EndsWith(".runtimeconfig.json", StringComparison.Ordinal);
    }

    private static bool IsTestRelevant(string path)
    {
        if (IsGeneratedPath(path))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/').TrimStart('.', '/');
        string lower = normalized.ToLowerInvariant();
        if (lower.EndsWith(".md", StringComparison.Ordinal) ||
            lower.EndsWith(".txt", StringComparison.Ordinal) ||
            lower.StartsWith("docs/", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsBuildRelevant(string path)
    {
        string normalized = path.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        string extension = Path.GetExtension(normalized);
        return normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Lines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 2048 ? trimmed : trimmed[..2048];
    }
}

public static class RimDevGitPolicy
{
    public static RimDevPolicyDecision DecideSync(GitRepositoryStateSnapshot state) =>
        state.Branch is null
            ? Block("GIT_DETACHED_HEAD", "Check out the intended branch explicitly, then run rimdev sync.")
            : state.UpstreamName is null
                ? Block("GIT_UPSTREAM_MISSING", "Set an upstream branch explicitly, then run rimdev sync.")
                : state.Dirty && state.Behind is > 0
                    ? Block("GIT_DIRTY_BEHIND", "Commit or inspect local changes before synchronizing; rimdev will not stash or discard them.")
                    : state.Ahead is > 0 && state.Behind is > 0
                        ? Block("GIT_DIVERGED", "The branch has both local and remote commits. Resolve the branch deliberately; rimdev will not merge or reset it.")
                        : state.Behind is > 0
                            ? new RimDevPolicyDecision(true, "fast-forward", null, null)
                            : new RimDevPolicyDecision(true, "current", null, null);

    public static RimDevPolicyDecision DecidePush(GitRepositoryStateSnapshot state) =>
        state.Branch is null
            ? Block("GIT_DETACHED_HEAD", "Push from the intended branch explicitly; rimdev will not switch branches.")
            : state.UpstreamName is null
                ? Block("GIT_UPSTREAM_MISSING", "Set an upstream branch explicitly; rimdev will not guess a push destination.")
                : state.Ahead is > 0 && state.Behind is > 0
                    ? Block("GIT_DIVERGED", "The branch is diverged. Reconcile it deliberately before pushing.")
                    : state.Behind is > 0
                        ? Block("GIT_NON_FAST_FORWARD", "The remote is ahead. Synchronize safely before pushing.")
                        : new RimDevPolicyDecision(true, state.Ahead is > 0 ? "push" : "current", null, null);

    private static RimDevPolicyDecision Block(string code, string explanation) =>
        new(false, "blocked", code, explanation);
}

public sealed record RimDevPolicyDecision(
    bool Allowed,
    string Action,
    string? ErrorCode,
    string? Explanation);

internal static class RimDevSourceIdentity
{
    public static string? Compute(
        string root,
        GitRepositoryStateSnapshot state,
        IReadOnlyList<string> identityPaths)
    {
        var paths = identityPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0 && state.HeadSha is null)
        {
            return null;
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, state.HeadSha ?? "");
        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }

        foreach (string path in paths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }

            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Append(hash, "\0" + path + "\0");
            if (!File.Exists(fullPath))
            {
                Append(hash, "missing");
                continue;
            }

            using FileStream stream = File.OpenRead(fullPath);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
