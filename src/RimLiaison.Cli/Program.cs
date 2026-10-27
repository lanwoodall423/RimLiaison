using System.Security.Cryptography;
using System.Text.Json;

namespace RimLiaison;

internal static class Program
{
    public static int Main(string[] args)
    {
        // An explicitly packaged native rimtest.exe is a development-only
        // entrypoint, not an alias for the promoted production CLI.
        NativeRimtestRunner.Dispatch? native = NativeRimtestRunner.Prepare(
            Environment.ProcessPath, args, Directory.GetCurrentDirectory());
        if (native is not null)
        {
            if (!native.Allowed)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimtest-dev-runner/v1",
                    status = "blocked",
                    code = native.ErrorCode,
                    error = native.Error,
                    nextAction = "Use the reviewed native development-runner package and verify its installed source identity externally."
                }));
                return CliExitCodes.ConservativeSelection;
            }

            args = native.Args;
            Console.Error.WriteLine(
                "RIMTEST_DEV_RUNNER sourceCommit=" + native.SourceCommit +
                " runnerExeSha256=" + native.RunnerExeSha256 +
                " cliAssemblySha256=" + native.CliAssemblySha256);
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return CliApplication.RunAsync(
                    args,
                    Console.Out,
                    Console.Error,
                    cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}

// This gate applies ONLY to the native apphost intentionally named rimtest.exe.
// rimliaison.exe, the legacy rimtest.cmd alias and dotnet-hosted CLI retain
// their existing behavior. The separately reviewed packaging step must place
// the exact .exe, .dll and small metadata sidecar together in one stable folder.
internal static class NativeRimtestRunner
{
    private const string ManifestFileName = "rimtest-dev-runner.json";
    private const int MaxManifestBytes = 4_096;
    private const long MaxCliAssemblyBytes = 64L * 1024 * 1024;
    private const long MaxRunnerExeBytes = 32L * 1024 * 1024;

    internal sealed record Dispatch(
        bool Allowed,
        string[] Args,
        string? ErrorCode = null,
        string? Error = null,
        string? SourceCommit = null,
        string? RunnerExeSha256 = null,
        string? CliAssemblySha256 = null);

    internal static Dispatch? Prepare(
        string? executablePath,
        IReadOnlyList<string> arguments,
        string invocationDirectory)
    {
        if (!string.Equals(
                Path.GetFileName(executablePath),
                "rimtest.exe",
                StringComparison.OrdinalIgnoreCase))
            return null;

        static Dispatch Block(string code, string message) =>
            new(false, [], code, message);

        // No native development alias may call promotion, qualification,
        // rimdev, direct game commands, or any hidden experimental switches.
        if (arguments.Count < 2 ||
            !string.Equals(arguments[0], "dev-check", StringComparison.Ordinal) ||
            arguments.Count(argument => argument == "--json") != 1)
            return Block("DEV_RUNNER_COMMAND_NOT_ALLOWED",
                "Native rimtest.exe accepts only dev-check [workspace] --json.");

        string[] other = arguments
            .Where(argument => argument != "--json")
            .ToArray();
        if (other.Length is < 1 or > 2 ||
            (other.Length == 2 &&
             (string.IsNullOrWhiteSpace(other[1]) ||
              other[1].StartsWith("-", StringComparison.Ordinal))))
            return Block("DEV_RUNNER_COMMAND_NOT_ALLOWED",
                "Native rimtest.exe refuses additional arguments and switches.");

        string workspace;
        try
        {
            // This binds the ordinary ProjectNexus invocation to its actual
            // candidate cwd, never the runner installation or source repo.
            workspace = Path.GetFullPath(
                other.Length == 2 ? other[1] : invocationDirectory,
                invocationDirectory);
            if (!Directory.Exists(workspace) ||
                !File.Exists(Path.Combine(workspace, "About", "About.xml")))
                return Block("DEV_RUNNER_WORKSPACE_INVALID",
                    "The selected mod workspace must exist and contain About/About.xml.");
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Block("DEV_RUNNER_WORKSPACE_INVALID",
                "The selected mod workspace is not a valid local directory.");
        }

        if (!TryVerifyLocalPackage(
                executablePath!, out string? sourceCommit,
                out string? exeSha256, out string? assemblySha256))
            return Block("DEV_RUNNER_PROVENANCE_INVALID",
                "Native runner metadata or complete dependency package cannot be verified.");

        return new Dispatch(
            true, ["dev-check", workspace, "--json"],
            SourceCommit: sourceCommit,
            RunnerExeSha256: exeSha256,
            CliAssemblySha256: assemblySha256);
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsRegularLocalFile(string path, long maximumBytes)
    {
        var info = new FileInfo(path);
        return info.Exists &&
               (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
               info.Length > 0 && info.Length <= maximumBytes;
    }

    private static string Sha256File(string path)
    {
        using FileStream input = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static bool TryVerifyLocalPackage(
        string executablePath,
        out string? sourceCommit,
        out string? runnerExeSha256,
        out string? cliAssemblySha256)
    {
        sourceCommit = null;
        runnerExeSha256 = null;
        cliAssemblySha256 = null;
        try
        {
            string runner = Path.GetFullPath(executablePath);
            string root = Path.GetDirectoryName(runner)!;
            string manifestPath = Path.Combine(root, ManifestFileName);
            string assemblyPath = Path.Combine(root, "rimliaison.dll");
            if (!IsRegularLocalFile(runner, MaxRunnerExeBytes) ||
                !IsRegularLocalFile(assemblyPath, MaxCliAssemblyBytes) ||
                !IsRegularLocalFile(manifestPath, MaxManifestBytes))
                return false;

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(manifestPath));
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object)
                return false;
            JsonProperty[] properties = item.EnumerateObject().ToArray();
            if (properties.Length != 4 ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 4)
                return false;
            if (!item.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String ||
                schema.GetString() != "rimtest-dev-runner/v1" ||
                !item.TryGetProperty("sourceCommit", out JsonElement commit) ||
                commit.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("runnerExeSha256", out JsonElement runnerHash) ||
                runnerHash.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("cliAssemblySha256", out JsonElement assemblyHash) ||
                assemblyHash.ValueKind != JsonValueKind.String)
                return false;

            string? candidateCommit = commit.GetString();
            string? expectedExe = runnerHash.GetString();
            string? expectedAssembly = assemblyHash.GetString();
            if (!IsLowerHex(candidateCommit, 40) ||
                !IsLowerHex(expectedExe, 64) ||
                !IsLowerHex(expectedAssembly, 64) ||
                !string.Equals(expectedExe, Sha256File(runner), StringComparison.Ordinal) ||
                !string.Equals(expectedAssembly, Sha256File(assemblyPath), StringComparison.Ordinal))
                return false;

            // A sidecar alone covers only the apphost and CLI assembly. Before
            // launch also verify every published dependency and the immutable
            // source-bound closure manifest. A mutated DLL, deps.json or a
            // missing/added file must block the native development runner.
            if (!RimLiaison.Toolchain.NativeRimtestPackageService.Verify(
                    root, candidateCommit, out _))
                return false;

            sourceCommit = candidateCommit;
            runnerExeSha256 = expectedExe;
            cliAssemblySha256 = expectedAssembly;
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException or JsonException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
