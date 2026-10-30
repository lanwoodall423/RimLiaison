using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Toolchain;

public static class CliDeploymentManifestSchemas
{
    public const string Current = "rimliaison-cli-manifest/v1";
}

public sealed record CliDeploymentFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record CliDeploymentManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("ownerProduct")] string OwnerProduct,
    [property: JsonPropertyName("sourceCommit")] string SourceCommit,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("files")] IReadOnlyList<CliDeploymentFileEntry> Files);

internal static class CliDeploymentManifestService
{
    public const string FileName = "rimliaison-cli-manifest.json";
    private const int MaximumMismatchPaths = 8;
    private const int MaximumMismatchPathCharacters = 160;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static CliDeploymentManifest Write(
        string deploymentRoot,
        string sourceCommit,
        string targetFramework)
    {
        string root = Path.GetFullPath(deploymentRoot);
        Directory.CreateDirectory(root);
        string manifestPath = Path.Combine(root, FileName);
        CliDeploymentFileEntry[] entries = EnumerateEntries(root, manifestPath);
        if (entries.Length == 0)
            throw new InvalidDataException("The published RimLiaison CLI contains no deployment files.");

        CliDeploymentManifest manifest = new(
            CliDeploymentManifestSchemas.Current,
            ToolchainPromotionSchemas.OwnerProduct,
            sourceCommit,
            targetFramework,
            ComputePackageHash(entries),
            entries);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteOptions));
        return manifest;
    }

    public static bool Verify(
        string deploymentRoot,
        string manifestPath,
        string? expectedManifestSha256,
        string? expectedPackageSha256,
        out CliDeploymentManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            string root = Path.GetFullPath(deploymentRoot);
            string path = Path.GetFullPath(manifestPath);
            if (!Directory.Exists(root) || !File.Exists(path) || !IsWithin(root, path))
            {
                error = "The CLI deployment manifest or deployment directory is missing.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedManifestSha256) &&
                !string.Equals(ToolchainFileHash.Sha256(path), expectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment manifest hash does not match its qualified identity.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<CliDeploymentManifest>(
                File.ReadAllText(path), WriteOptions);
            if (manifest is null ||
                !string.Equals(manifest.SchemaVersion, CliDeploymentManifestSchemas.Current, StringComparison.Ordinal) ||
                !string.Equals(manifest.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(manifest.SourceCommit) ||
                string.IsNullOrWhiteSpace(manifest.TargetFramework) ||
                string.IsNullOrWhiteSpace(manifest.PackageSha256) ||
                manifest.Files is null || manifest.Files.Count == 0)
            {
                error = "The CLI deployment manifest is incomplete or unsupported.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !string.Equals(manifest.PackageSha256, expectedPackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment package hash does not match its qualified identity.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CliDeploymentFileEntry entry in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256) ||
                    !seen.Add(entry.Path) || entry.Path.Contains('\\') || Path.IsPathRooted(entry.Path) ||
                    entry.Path.Contains(':'))
                {
                    error = "The CLI deployment manifest contains an invalid or duplicate file entry.";
                    return false;
                }
                string? file = SafePath(root, entry.Path);
                if (file is null || !File.Exists(file))
                {
                    continue;
                }
                if (!string.Equals(ToolchainFileHash.Sha256(file), entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "The CLI deployment closure is missing or contains a mismatching file: " + entry.Path;
                    return false;
                }
            }

            string[] actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !SamePath(file, path))
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            string[] expected = seen.OrderBy(file => file, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                string[] unexpected = actual.Except(expected, StringComparer.Ordinal)
                    .Take(MaximumMismatchPaths)
                    .ToArray();
                string[] missing = expected.Except(actual, StringComparer.Ordinal)
                    .Take(MaximumMismatchPaths)
                    .ToArray();
                error = "CLI deployment closure mismatch: unexpected=[" +
                    FormatMismatchPaths(unexpected) +
                    "], missing=[" +
                    FormatMismatchPaths(missing) +
                    "]";
                return false;
            }
            if (!string.Equals(ComputePackageHash(manifest.Files), manifest.PackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment package hash does not match its manifest entries.";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            manifest = null;
            error = "The CLI deployment manifest could not be verified: " + exception.Message;
            return false;
        }
    }

    public static bool ContainsFile(CliDeploymentManifest manifest, string relativePath) =>
        manifest.Files.Any(entry => string.Equals(entry.Path, relativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase));

    public static string ComputePackageHash(IEnumerable<CliDeploymentFileEntry> entries) =>
        ComputePackageHash(entries.Select(entry => (entry.Path, entry.Sha256)));

    private static CliDeploymentFileEntry[] EnumerateEntries(string root, string manifestPath) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !SamePath(file, manifestPath))
            .Select(file => new CliDeploymentFileEntry(
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                ToolchainFileHash.Sha256(file)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

    private static string FormatMismatchPaths(IEnumerable<string> paths) =>
        string.Join(",", paths.Select(path =>
            path.Length <= MaximumMismatchPathCharacters
                ? path
                : path[..(MaximumMismatchPathCharacters - 3)] + "..."));

    private static string ComputePackageHash(IEnumerable<(string Path, string Sha256)> entries)
    {
        string canonical = string.Join("\n", entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => entry.Path.ToLowerInvariant() + "\0" + entry.Sha256));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    }

    private static string? SafePath(string root, string relative)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        return IsWithin(root, candidate) ? candidate : null;
    }

    private static bool IsWithin(string root, string path)
    {
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path);
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
            SamePath(root, candidate);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

// A source-pinned, candidate-only Windows publisher. The command has no
// install, PATH, game, production-promotion or ProjectNexus authority.
internal sealed record NativeRimtestPublishResult(int ExitCode, string Output);

internal static class NativeRimtestSourcePublishCommand
{
    internal const string Name = "rimtest-publish-candidate";
    private const string Schema = "rimtest-source-publish/v1";
    // The exact source SDK also builds the net8.0 developer CLI; do not pin the host to .NET 8.
    internal const string PinnedSdk = "10.0.403";

    internal sealed record Request(string SourceRoot, string SourceCommit);

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == Name;

    internal static bool TryParse(IReadOnlyList<string> args, out Request? request)
    {
        request = null;
        if (args.Count != 4 || args[0] != Name || args[3] != "--json" ||
            args[2].Length != 40 ||
            !args[2].All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(args[1]))
                return false;
            request = new Request(Path.GetFullPath(args[1]), args[2]);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Block(stdout, "RIMTEST_PUBLISH_WINDOWS_REQUIRED");

        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Block(stdout, "RIMTEST_PUBLISH_ACCOUNT_ROOT_UNAVAILABLE");

        string? host = ResolvePinnedDotnetHost(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            local,
            IsSafeDotnetHost);
        if (host is null)
            return Block(stdout, "RIMTEST_PUBLISH_PINNED_HOST_UNAVAILABLE");
        return await RunCoreAsync(
            args, stdout, host,
            Path.Combine(local, "RimLiaison", "Rimtest", "PublishScratch"),
            null, new RimLiaison.Git.SystemGitRepositoryStateProvider(),
            ExecuteFixedDotnetAsync, cancellationToken).ConfigureAwait(false);
    }

    // Host selection is deterministic and never invokes a bare executable on
    // PATH. A supplied host override is authoritative: an invalid override
    // cannot silently fall back to a different binary. Without overrides,
    // recognize only the standard machine-wide and user-local .NET locations.
    // The exact pinned SDK is still proven by --version from source cwd.
    internal static string? ResolvePinnedDotnetHost(
        string? explicitHost,
        string? explicitRoot,
        string? programFilesRoot,
        string? accountLocalRoot,
        Func<string, bool> isAvailable)
    {
        static string? Eligible(string? path, Func<string, bool> available)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                if (!Path.IsPathFullyQualified(path))
                    return null;
                string full = Path.GetFullPath(path);
                return Path.GetFileName(full).Equals(
                           "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
                       available(full)
                    ? full : null;
            }
            catch (Exception exception) when (exception is ArgumentException or
                IOException or NotSupportedException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitHost))
            return Eligible(explicitHost, isAvailable);

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            if (!Path.IsPathFullyQualified(explicitRoot))
                return null;
            return Eligible(Path.Combine(explicitRoot, "dotnet.exe"), isAvailable);
        }

        string? machine = string.IsNullOrWhiteSpace(programFilesRoot)
            ? null
            : Eligible(Path.Combine(programFilesRoot, "dotnet", "dotnet.exe"),
                isAvailable);
        if (machine is not null)
            return machine;

        return string.IsNullOrWhiteSpace(accountLocalRoot)
            ? null
            : Eligible(Path.Combine(
                accountLocalRoot, "Microsoft", "dotnet", "dotnet.exe"), isAvailable);
    }

    internal static bool IsSafeDotnetHost(string file)
    {
        try
        {
            if (!File.Exists(file) ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                return false;
            DirectoryInfo? parent = new FileInfo(file).Directory;
            while (parent is not null)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                parent = parent.Parent;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    // Pure fixture injection, never exposed through normal CLI arguments.
    internal static Task<int> RunForTestsAsync(
        string[] args,
        TextWriter stdout,
        string fixtureDotnetHost,
        string fixtureScratchParent,
        string fixtureCandidateParent,
        RimLiaison.Git.IGitRepositoryStateProvider sourceState,
        Func<string, string, IReadOnlyList<string>, CancellationToken,
            Task<NativeRimtestPublishResult>> fakePublisher,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, stdout, fixtureDotnetHost, fixtureScratchParent,
            fixtureCandidateParent, sourceState, fakePublisher, cancellationToken);

    private static async Task<int> RunCoreAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        string dotnetHost,
        string scratchParent,
        string? fixtureCandidateParent,
        RimLiaison.Git.IGitRepositoryStateProvider sourceState,
        Func<string, string, IReadOnlyList<string>, CancellationToken,
            Task<NativeRimtestPublishResult>> publisher,
        CancellationToken cancellationToken)
    {
        if (!TryParse(args, out Request? request) || request is null)
            return Block(stdout, "RIMTEST_PUBLISH_ARGUMENTS_INVALID");

        string? ownedScratch = null;
        try
        {
            if (!Path.IsPathFullyQualified(dotnetHost) ||
                !File.Exists(dotnetHost) ||
                (File.GetAttributes(dotnetHost) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_PUBLISH_PINNED_HOST_UNAVAILABLE");
            string source = request.SourceRoot;
            string project = Path.Combine(
                source, "src", "RimLiaison.Cli", "RimLiaison.Cli.csproj");
            if (!File.Exists(project))
                throw new InvalidDataException("RIMTEST_PUBLISH_PROJECT_MISSING");

            using JsonDocument pin = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(source, "global.json")));
            if (pin.RootElement.ValueKind != JsonValueKind.Object ||
                !pin.RootElement.TryGetProperty("sdk", out JsonElement sdk) ||
                sdk.ValueKind != JsonValueKind.Object ||
                !sdk.TryGetProperty("version", out JsonElement sdkVersion) ||
                sdkVersion.ValueKind != JsonValueKind.String ||
                sdkVersion.GetString() != PinnedSdk ||
                !sdk.TryGetProperty("rollForward", out JsonElement rollForward) ||
                rollForward.ValueKind != JsonValueKind.String ||
                rollForward.GetString() != "disable" ||
                !sdk.TryGetProperty("allowPrerelease", out JsonElement prerelease) ||
                prerelease.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("RIMTEST_PUBLISH_SDK_PIN_MISMATCH");

            await RequireCleanSourceAsync(
                sourceState, source, request.SourceCommit, cancellationToken)
                .ConfigureAwait(false);
            NativeRimtestPublishResult version = await publisher(
                dotnetHost, source, ["--version"], cancellationToken)
                .ConfigureAwait(false);
            if (version.ExitCode != 0 ||
                version.Output.Trim() != PinnedSdk)
                throw new InvalidDataException("RIMTEST_PUBLISH_SDK_PIN_MISMATCH");

            string parent = Path.GetFullPath(scratchParent);
            if (Overlaps(parent, source))
                throw new InvalidDataException("RIMTEST_PUBLISH_SCRATCH_SOURCE_OVERLAP");
            Directory.CreateDirectory(parent);
            ownedScratch = Path.Combine(
                parent, ".rimtest-publish-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(ownedScratch))
                throw new InvalidDataException("RIMTEST_PUBLISH_SCRATCH_COLLISION");
            Directory.CreateDirectory(ownedScratch);

            // No arbitrary compiler command, RID, flags or output directory.
            // The child cwd is the exact source, so global.json applies.
            NativeRimtestPublishResult build = await publisher(
                dotnetHost, source,
                [
                    "publish", "src/RimLiaison.Cli/RimLiaison.Cli.csproj",
                    "--configuration", "Release",
                    "--framework", "net8.0",
                    "--runtime", "win-x64",
                    "--self-contained", "false",
                    "-p:UseAppHost=true",
                    "--output", ownedScratch,
                    "--nologo"
                ], cancellationToken).ConfigureAwait(false);
            if (build.ExitCode != 0)
                throw new InvalidDataException("RIMTEST_PUBLISH_DOTNET_FAILED");

            await RequireCleanSourceAsync(
                sourceState, source, request.SourceCommit, cancellationToken)
                .ConfigureAwait(false);

            NativeRimtestPackageCandidate candidate = fixtureCandidateParent is null
                ? await NativeRimtestPackageService.StageFromCleanGitAsync(
                    source, ownedScratch, request.SourceCommit, cancellationToken)
                    .ConfigureAwait(false)
                : await NativeRimtestPackageService.StageForTestsAsync(
                    source, ownedScratch, request.SourceCommit,
                    fixtureCandidateParent, sourceState, cancellationToken)
                    .ConfigureAwait(false);

            if (!NativeRimtestPackageService.Verify(
                    candidate.PackageRoot, request.SourceCommit, out _))
                throw new InvalidDataException("RIMTEST_PUBLISH_CANDIDATE_INVALID");

            // Never report candidate success until all owned publish scratch
            // bytes have been cleaned; the account-local package is preserved.
            Directory.Delete(ownedScratch, recursive: true);
            ownedScratch = null;
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = Schema,
                status = "candidate-only",
                sourceCommit = request.SourceCommit,
                sdkVersion = PinnedSdk,
                packageRoot = candidate.PackageRoot,
                manifestSha256 = candidate.ManifestSha256,
                packageSha256 = candidate.PackageSha256,
                qualified = false,
                installed = false,
                independentlyAttestedBuild = false,
                pnCheckPassed = false
            }));
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidDataException or NotSupportedException or
            JsonException or OperationCanceledException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            string code = ex.Message.Split(':')[0];
            if (!code.StartsWith("RIMTEST_PUBLISH_", StringComparison.Ordinal) &&
                !code.StartsWith("RIMTEST_PACKAGE_", StringComparison.Ordinal))
                code = "RIMTEST_PUBLISH_FAILED";
            return Block(stdout, code);
        }
        finally
        {
            if (ownedScratch is not null && Directory.Exists(ownedScratch))
                Directory.Delete(ownedScratch, recursive: true);
        }
    }

    private static async Task RequireCleanSourceAsync(
        RimLiaison.Git.IGitRepositoryStateProvider provider,
        string source,
        string sha,
        CancellationToken cancellationToken)
    {
        RimLiaison.Git.GitRepositoryStateResult result = await provider
            .ReadWorktreeAsync(source, cancellationToken).ConfigureAwait(false);
        if (!result.Resolved || result.State is null || result.State.Dirty ||
            result.State.HeadSha != sha ||
            !string.Equals(Path.GetFullPath(result.State.RootPath), source,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RIMTEST_PUBLISH_SOURCE_UNVERIFIED");
    }

    private static bool Overlaps(string left, string right)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static int Block(TextWriter stdout, string code)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "blocked",
            code,
            qualified = false,
            installed = false,
            independentlyAttestedBuild = false,
            pnCheckPassed = false
        }));
        return RimLiaison.CliExitCodes.ConservativeSelection;
    }

    internal static async Task<NativeRimtestPublishResult> ExecuteFixedDotnetAsync(
        string executable,
        string cwd,
        IReadOnlyList<string> args,
        CancellationToken token)
    {
        using var child = new System.Diagnostics.Process();
        child.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
            child.StartInfo.ArgumentList.Add(arg);
        if (!child.Start())
            throw new InvalidDataException("RIMTEST_PUBLISH_DOTNET_START_FAILED");

        Task<string> output = DrainAsync(child.StandardOutput, token);
        Task<string> errors = DrainAsync(child.StandardError, token);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(token);
        limited.CancelAfter(args.Count == 1
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromMinutes(8));
        try
        {
            await child.WaitForExitAsync(limited.Token).ConfigureAwait(false);
            return new NativeRimtestPublishResult(
                child.ExitCode, (await output.ConfigureAwait(false) +
                    await errors.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            throw new InvalidDataException("RIMTEST_PUBLISH_DOTNET_TIMEOUT");
        }
    }

    private static async Task<string> DrainAsync(
        StreamReader input, CancellationToken token)
    {
        var bounded = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(
                    buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            int room = 8192 - bounded.Length;
            if (room > 0)
                bounded.Append(buffer, 0, Math.Min(room, count));
        }
        return bounded.ToString();
    }
}

// A candidate package is not production-qualified or installed on PATH.
internal sealed record NativeRimtestPackageCandidate(
    string PackageRoot,
    string SourceCommit,
    string ManifestSha256,
    string PackageSha256);

// Developer tooling only. This command packages already-published CLI output
// into an inert, account-local candidate. No source build, qualification,
// installation, PATH registration, or RimWorld invocation is performed.
internal static class NativeRimtestPackageCandidateCommand
{
    internal const string Name = "rimtest-package-candidate";
    private const string Schema = "rimtest-package-candidate/v1";

    internal sealed record Request(
        string SourceRoot,
        string PublishedCliRoot,
        string SourceCommit);

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    internal static bool TryParse(IReadOnlyList<string> args, out Request? request)
    {
        request = null;
        if (args.Count != 5 ||
            !string.Equals(args[0], Name, StringComparison.Ordinal) ||
            !string.Equals(args[4], "--json", StringComparison.Ordinal))
            return false;

        string commit = args[3];
        if (commit.Length != 40 ||
            !commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(args[1]) ||
                !Path.IsPathFullyQualified(args[2]))
                return false;
            string source = Path.GetFullPath(args[1]);
            string published = Path.GetFullPath(args[2]);
            request = new Request(source, published, commit);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or IOException)
        {
            return false;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        if (!TryParse(args, out Request? request) || request is null)
        {
            Write(stdout, new
            {
                schemaVersion = Schema,
                status = "blocked",
                code = "RIMTEST_CANDIDATE_ARGUMENTS_INVALID",
                usage = "rimliaison rimtest-package-candidate <absolute-source-root> <absolute-published-cli-root> <40-lowercase-hex-commit> --json",
                qualified = false,
                installed = false
            });
            return RimLiaison.CliExitCodes.ConservativeSelection;
        }

        try
        {
            NativeRimtestPackageCandidate result =
                await NativeRimtestPackageService.StageFromCleanGitAsync(
                    request.SourceRoot,
                    request.PublishedCliRoot,
                    request.SourceCommit,
                    cancellationToken).ConfigureAwait(false);
            Write(stdout, new
            {
                schemaVersion = Schema,
                status = "candidate-only",
                sourceCommit = result.SourceCommit,
                packageRoot = result.PackageRoot,
                manifestSha256 = result.ManifestSha256,
                packageSha256 = result.PackageSha256,
                qualified = false,
                installed = false,
                independentlyAttestedBuild = false,
                nextAction = "Build provenance, Windows process launch, independent trust and account-local installation must be reviewed separately."
            });
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or
            ArgumentException or UnauthorizedAccessException or NotSupportedException or
            InvalidDataException or OperationCanceledException or
            System.Security.SecurityException)
        {
            string knownCode = exception.Message.Split(':')[0];
            if (!knownCode.StartsWith("RIMTEST_PACKAGE_", StringComparison.Ordinal))
                knownCode = "RIMTEST_CANDIDATE_PREPARATION_FAILED";
            Write(stdout, new
            {
                schemaVersion = Schema,
                status = "blocked",
                code = knownCode,
                qualified = false,
                installed = false,
                nextAction = "Verify an exact clean Git source and complete independently published Windows CLI artifacts; no installation was performed."
            });
            return RimLiaison.CliExitCodes.ConservativeSelection;
        }
    }

    private static void Write(TextWriter output, object result) =>
        output.WriteLine(JsonSerializer.Serialize(result));
}

// Read-only verification of a staged development candidate. This deliberately
// does not imply that the native executable was built from the claimed source,
// that a trusted publisher signed it, or that it may be installed on PATH.
internal sealed record NativeRimtestPackageVerification(
    string PackageRoot,
    string SourceCommit,
    string ManifestSha256,
    string PackageSha256);

internal static class NativeRimtestPackageVerificationCommand
{
    internal const string Name = "rimtest-package-verify";
    private const string Schema = "rimtest-package-verification/v1";

    internal sealed record Request(string PackageRoot, string SourceCommit);

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    internal static bool TryParse(IReadOnlyList<string> args, out Request? request)
    {
        request = null;
        if (args.Count != 4 ||
            !string.Equals(args[0], Name, StringComparison.Ordinal) ||
            !string.Equals(args[3], "--json", StringComparison.Ordinal) ||
            !IsCanonicalSourceCommit(args[2]))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(args[1]))
                return false;
            request = new Request(Path.GetFullPath(args[1]), args[2]);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static int Run(string[] args, TextWriter stdout)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            WriteBlocked(stdout, "RIMTEST_PACKAGE_ACCOUNT_ROOT_UNAVAILABLE");
            return RimLiaison.CliExitCodes.ConservativeSelection;
        }

        string accountPackages = Path.Combine(
            local, "RimLiaison", "Rimtest", "CandidatePackages");
        return RunAgainstPackagesRoot(args, stdout, accountPackages);
    }

    // Internal fixture override only. CLI arguments cannot select this root.
    internal static int RunForTests(
        string[] args, TextWriter stdout, string testPackagesRoot) =>
        RunAgainstPackagesRoot(args, stdout, testPackagesRoot);

    private static int RunAgainstPackagesRoot(
        IReadOnlyList<string> args,
        TextWriter stdout,
        string accountPackages)
    {
        if (!TryParse(args, out Request? request) || request is null)
        {
            WriteBlocked(stdout, "RIMTEST_PACKAGE_VERIFY_ARGUMENTS_INVALID");
            return RimLiaison.CliExitCodes.ConservativeSelection;
        }

        if (!TryVerifyExactCandidate(
                request, accountPackages, out NativeRimtestPackageVerification? verified,
                out string code))
        {
            WriteBlocked(stdout, code);
            return RimLiaison.CliExitCodes.ConservativeSelection;
        }

        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "candidate-verified",
            sourceCommit = verified!.SourceCommit,
            packageRoot = verified.PackageRoot,
            manifestSha256 = verified.ManifestSha256,
            packageSha256 = verified.PackageSha256,
            qualified = false,
            installed = false,
            independentlyAttestedBuild = false,
            nextAction = "Verify an independently built executable, exact source-to-build provenance, trust and installation in separate authorized steps."
        }));
        return RimLiaison.CliExitCodes.Success;
    }

    private static bool TryVerifyExactCandidate(
        Request request,
        string accountPackages,
        out NativeRimtestPackageVerification? verified,
        out string code)
    {
        verified = null;
        code = "RIMTEST_PACKAGE_VERIFICATION_FAILED";
        try
        {
            string parent = Path.GetFullPath(accountPackages);
            string expected = Path.Combine(parent, "rimtest-" + request.SourceCommit);
            if (!string.Equals(request.PackageRoot, expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                code = "RIMTEST_PACKAGE_LOCATION_MISMATCH";
                return false;
            }

            // Verify() enforces no reparse/junction entries from the filesystem
            // root through the complete package closure and hashes all files.
            if (!NativeRimtestPackageService.Verify(
                    expected, request.SourceCommit, out _))
                return false;

            string manifestPath = Path.Combine(expected, CliDeploymentManifestService.FileName);
            if (!CliDeploymentManifestService.Verify(
                    expected, manifestPath, null, null,
                    out CliDeploymentManifest? manifest, out _) ||
                manifest is null ||
                !string.Equals(manifest.SourceCommit, request.SourceCommit,
                    StringComparison.Ordinal))
                return false;

            verified = new NativeRimtestPackageVerification(
                expected, request.SourceCommit,
                ToolchainFileHash.Sha256(manifestPath), manifest.PackageSha256);
            code = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsCanonicalSourceCommit(string source) =>
        source.Length == 40 &&
        source.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteBlocked(TextWriter stdout, string code) =>
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "blocked",
            code,
            qualified = false,
            installed = false,
            independentlyAttestedBuild = false
        }));
}

// Purely observational prerequisite for the trusted NatureTiles
// "rimtest dev-check --json" check. This inspects the current PROCESS's
// Windows PATH, not a future PN executor's PATH or process spawn result.
internal static class NativeRimtestPathPreflightCommand
{
    internal const string Name = "rimtest-path-preflight";
    private const string Schema = "rimtest-path-preflight/v1";
    private const string Executable = "rimtest.exe";

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    internal static bool TryParse(IReadOnlyList<string> args, out string? commit)
    {
        commit = null;
        if (args.Count != 3 ||
            !string.Equals(args[0], Name, StringComparison.Ordinal) ||
            !string.Equals(args[2], "--json", StringComparison.Ordinal) ||
            args[1].Length != 40 ||
            !args[1].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;

        commit = args[1];
        return true;
    }

    internal static int Run(string[] args, TextWriter stdout)
    {
        if (!OperatingSystem.IsWindows())
            return Block(stdout, "RIMTEST_PATH_WINDOWS_REQUIRED");

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Block(stdout, "RIMTEST_PATH_ACCOUNT_ROOT_UNAVAILABLE");

        return RunAgainstLocalEnvironment(
            args, stdout,
            Path.Combine(local, "RimLiaison", "Rimtest", "Installed"),
            Environment.GetEnvironmentVariable("PATH"),
            Directory.GetCurrentDirectory());
    }

    // Only tests may supply synthetic PATH, cwd or installed-parent identity.
    // The production CLI has no arbitrary path, command or environment inputs.
    internal static int RunForTests(
        string[] args,
        TextWriter stdout,
        string fixtureInstalledParent,
        string? fixturePath,
        string fixtureCwd) =>
        RunAgainstLocalEnvironment(
            args, stdout, fixtureInstalledParent, fixturePath, fixtureCwd);

    private static int RunAgainstLocalEnvironment(
        IReadOnlyList<string> args,
        TextWriter stdout,
        string installedParent,
        string? processPath,
        string processCwd)
    {
        if (!TryParse(args, out string? commit) || commit is null)
            return Block(stdout, "RIMTEST_PATH_ARGUMENTS_INVALID");

        try
        {
            string expectedDir = Path.GetFullPath(
                Path.Combine(installedParent, "rimtest-" + commit));
            string expectedExe = Path.Combine(expectedDir, Executable);
            string cwd = Path.GetFullPath(processCwd);

            // On Windows a process working directory can shadow an intended
            // PATH executable. A different rimtest.exe here is never a PASS.
            if (!SameDirectory(cwd, expectedDir) &&
                File.Exists(Path.Combine(cwd, Executable)))
                return Block(stdout, "RIMTEST_PATH_CWD_SHADOW");

            if (string.IsNullOrWhiteSpace(processPath))
                return Block(stdout, "RIMTEST_PATH_NOT_CONFIGURED");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var discovered = new List<string>();
            bool foundExpectedDir = false;
            foreach (string raw in processPath.Split(Path.PathSeparator))
            {
                string segment = raw.Trim();
                if (segment.Length == 0 ||
                    segment.Contains('"') ||
                    !Path.IsPathFullyQualified(segment))
                    return Block(stdout, "RIMTEST_PATH_ENTRY_INVALID");

                string directory = Path.GetFullPath(segment);
                if (!visited.Add(directory))
                    continue;

                if (SameDirectory(directory, expectedDir))
                    foundExpectedDir = true;

                string executable = Path.Combine(directory, Executable);
                if (File.Exists(executable))
                    discovered.Add(executable);
            }

            if (!foundExpectedDir)
                return Block(stdout, "RIMTEST_PATH_NOT_CONFIGURED");
            if (!File.Exists(expectedExe))
                return Block(stdout, "RIMTEST_PATH_EXECUTABLE_MISSING");
            int expectedIndex = discovered.FindIndex(
                executable => SameFilePath(executable, expectedExe));
            if (expectedIndex < 0)
                return Block(stdout, "RIMTEST_PATH_NOT_RESOLVED");

            for (int i = 0; i < discovered.Count; i++)
            {
                if (!SameFilePath(discovered[i], expectedExe))
                    return Block(stdout, i < expectedIndex
                        ? "RIMTEST_PATH_SHADOWED"
                        : "RIMTEST_PATH_AMBIGUOUS");
            }

            // A valid PATH entry is not enough: require exactly the matching
            // source-pinned native apphost and all dependency bytes.
            if (!NativeRimtestPackageService.Verify(expectedDir, commit, out _))
                return Block(stdout, "RIMTEST_PATH_PACKAGE_INVALID");

            string manifestPath = Path.Combine(
                expectedDir, CliDeploymentManifestService.FileName);
            if (!CliDeploymentManifestService.Verify(
                    expectedDir, manifestPath, null, null,
                    out CliDeploymentManifest? manifest, out _) ||
                manifest is null ||
                !string.Equals(manifest.SourceCommit, commit, StringComparison.Ordinal))
                return Block(stdout, "RIMTEST_PATH_PACKAGE_INVALID");

            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = Schema,
                status = "local-path-preflight-only",
                sourceCommit = commit,
                executablePath = expectedExe,
                manifestSha256 = ToolchainFileHash.Sha256(manifestPath),
                packageSha256 = manifest.PackageSha256,
                qualified = false,
                approvedInstallation = false,
                pnCheckPassed = false,
                nextAction = "Run the separately authorized exact NatureTiles ProjectNexus workspace.check from its actual Windows executor."
            }));
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return Block(stdout, "RIMTEST_PATH_INSPECTION_FAILED");
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool SameFilePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static int Block(TextWriter stdout, string code)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "blocked",
            code,
            qualified = false,
            approvedInstallation = false,
            pnCheckPassed = false
        }));
        return RimLiaison.CliExitCodes.ConservativeSelection;
    }
}

// Opt-in local copy of a fully verified candidate into an immutable,
// version-specific account-local location. This is not installation on PATH,
// source-to-build attestation, game validation, or a ProjectNexus trust grant.
internal static class NativeRimtestCandidateInstallCommand
{
    internal const string Name = "rimtest-install-candidate";
    private const string Schema = "rimtest-candidate-install/v1";
    private const int MaximumFiles = 2048;
    private const int MaximumDepth = 24;
    private const long MaximumBytes = 512L * 1024 * 1024;

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);

    internal static bool TryParse(IReadOnlyList<string> args, out string? sourceCommit)
    {
        sourceCommit = null;
        if (args.Count != 3 ||
            !string.Equals(args[0], Name, StringComparison.Ordinal) ||
            !string.Equals(args[2], "--json", StringComparison.Ordinal) ||
            args[1].Length != 40 ||
            !args[1].All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        sourceCommit = args[1];
        return true;
    }

    internal static int Run(string[] args, TextWriter stdout)
    {
        if (!OperatingSystem.IsWindows())
            return Block(stdout, "RIMTEST_INSTALL_WINDOWS_REQUIRED");
        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Block(stdout, "RIMTEST_INSTALL_ACCOUNT_ROOT_UNAVAILABLE");
        return RunCore(args, stdout, Path.Combine(local, "RimLiaison", "Rimtest"));
    }

    // The account-root injection exists only in deterministic isolated tests.
    // The normal CLI does not accept any caller-provided source or destination.
    internal static int RunForTests(
        string[] args, TextWriter stdout, string fixtureRimtestRoot) =>
        RunCore(args, stdout, fixtureRimtestRoot);

    private static int RunCore(
        IReadOnlyList<string> args, TextWriter stdout, string rimtestRoot)
    {
        if (!TryParse(args, out string? sourceCommit) || sourceCommit is null)
            return Block(stdout, "RIMTEST_INSTALL_ARGUMENTS_INVALID");

        string? ownedStage = null;
        try
        {
            string root = Path.GetFullPath(rimtestRoot);
            string source = Path.Combine(
                root, "CandidatePackages", "rimtest-" + sourceCommit);
            string targetParent = Path.Combine(root, "Installed");
            string target = Path.Combine(targetParent, "rimtest-" + sourceCommit);
            if (!Directory.Exists(source))
                throw new InvalidDataException("RIMTEST_INSTALL_CANDIDATE_MISSING");
            if (!NativeRimtestPackageService.Verify(source, sourceCommit, out _))
                throw new InvalidDataException("RIMTEST_INSTALL_CANDIDATE_INVALID");

            string sourceManifest = Path.Combine(
                source, CliDeploymentManifestService.FileName);
            string sourceManifestHash = ToolchainFileHash.Sha256(sourceManifest);
            if (Path.Exists(target))
                throw new IOException("RIMTEST_INSTALL_VERSION_ALREADY_EXISTS");
            RequirePlainExistingAncestors(targetParent);
            Directory.CreateDirectory(targetParent);
            RequirePlainExistingAncestors(targetParent);

            ownedStage = Path.Combine(
                targetParent, ".rimtest-install-stage-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(ownedStage))
                throw new IOException("RIMTEST_INSTALL_STAGE_COLLISION");
            Directory.CreateDirectory(ownedStage);
            int count = 0;
            long bytes = 0;
            CopyPlainTree(source, ownedStage, 0, ref count, ref bytes);

            if (!NativeRimtestPackageService.Verify(ownedStage, sourceCommit, out _) ||
                !string.Equals(
                    ToolchainFileHash.Sha256(Path.Combine(
                        ownedStage, CliDeploymentManifestService.FileName)),
                    sourceManifestHash, StringComparison.Ordinal) ||
                !NativeRimtestPackageService.Verify(source, sourceCommit, out _) ||
                !string.Equals(
                    ToolchainFileHash.Sha256(sourceManifest),
                    sourceManifestHash, StringComparison.Ordinal))
                throw new InvalidDataException("RIMTEST_INSTALL_CLOSURE_CHANGED");

            if (Path.Exists(target))
                throw new IOException("RIMTEST_INSTALL_VERSION_ALREADY_EXISTS");

            // Same-volume and non-replacing. A separately reviewed process
            // must handle PATH registration and a real Windows apphost launch.
            Directory.Move(ownedStage, target);
            ownedStage = null;
            if (!NativeRimtestPackageService.Verify(target, sourceCommit, out _) ||
                !string.Equals(
                    ToolchainFileHash.Sha256(Path.Combine(
                        target, CliDeploymentManifestService.FileName)),
                    sourceManifestHash, StringComparison.Ordinal))
                throw new InvalidDataException("RIMTEST_INSTALL_POSTMOVE_INVALID");

            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = Schema,
                status = "installed-unbound-candidate",
                sourceCommit,
                installedPath = target,
                executablePath = Path.Combine(
                    target, NativeRimtestPackageService.RunnerExecutableFileName),
                manifestSha256 = sourceManifestHash,
                qualified = false,
                pathConfigured = false,
                pnCheckPassed = false,
                independentlyAttestedBuild = false,
                nextAction = "Review real Windows build provenance; separately approve PATH binding and run NatureTiles' exact ProjectNexus validation.rimtest."
            }));
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidDataException or
            System.Security.SecurityException)
        {
            string code = exception.Message.Split(':')[0];
            if (!code.StartsWith("RIMTEST_INSTALL_", StringComparison.Ordinal))
                code = "RIMTEST_INSTALL_FAILED";
            return Block(stdout, code);
        }
        finally
        {
            // Never remove an existing version or caller-controlled path.
            // A successfully moved target is intentionally left untouched.
            if (ownedStage is not null && Directory.Exists(ownedStage))
                Directory.Delete(ownedStage, recursive: true);
        }
    }

    private static void RequirePlainExistingAncestors(string path)
    {
        DirectoryInfo? cursor = new(Path.GetFullPath(path));
        while (cursor is not null)
        {
            if (cursor.Exists &&
                (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_INSTALL_REPARSE_PARENT");
            cursor = cursor.Parent;
        }
    }

    private static void CopyPlainTree(
        string source, string target, int depth, ref int count, ref long bytes)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("RIMTEST_INSTALL_DEPTH_LIMIT");
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_INSTALL_REPARSE_ENTRY");
            string destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(destination);
                CopyPlainTree(entry.FullName, destination, depth + 1, ref count, ref bytes);
            }
            else if (entry is FileInfo file)
            {
                count++;
                bytes += file.Length;
                if (count > MaximumFiles || bytes > MaximumBytes)
                    throw new InvalidDataException("RIMTEST_INSTALL_PACKAGE_LIMIT");
                File.Copy(file.FullName, destination, overwrite: false);
            }
            else
                throw new InvalidDataException("RIMTEST_INSTALL_FILE_TYPE_INVALID");
        }
    }

    private static int Block(TextWriter stdout, string code)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "blocked",
            code,
            qualified = false,
            pathConfigured = false,
            pnCheckPassed = false,
            independentlyAttestedBuild = false
        }));
        return RimLiaison.CliExitCodes.ConservativeSelection;
    }
}

// Explicit candidate-only import from a ProjectNexus-owned, hash-verified
// native artifact operation. This is never a PATH installation, promotion,
// qualification result, or authority to run RimWorld. No user path input.
internal static class NativeRimtestManagedArtifactImportCommand
{
    internal const string Name = "rimtest-import-managed-artifact";
    private const string Schema = "rimtest-managed-artifact-import/v1";
    private const string OperationName = "native.rimtest-artifact-build";
    private const string ZipName = "artifacts/rimtest-native-candidate.zip";
    private const string ReceiptName = "artifacts/rimtest-native-build.json";
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 2048;
    private const int MaxDepth = 24;

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == Name;

    internal static bool TryParse(
        IReadOnlyList<string> args, out string? operationId,
        out string? commit, out string? archiveHash)
    {
        operationId = commit = archiveHash = null;
        if (args.Count != 5 || args[0] != Name || args[4] != "--json" ||
            !Guid.TryParseExact(args[1], "D", out Guid parsed) ||
            parsed.ToString("D") != args[1] || !LowerHex(args[2], 40) ||
            !LowerHex(args[3], 64))
            return false;
        operationId = args[1];
        commit = args[2];
        archiveHash = args[3];
        return true;
    }

    internal static int Run(string[] args, TextWriter stdout)
    {
        if (!OperatingSystem.IsWindows())
            return Block(stdout, "RIMTEST_IMPORT_WINDOWS_REQUIRED");
        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return Block(stdout, "RIMTEST_IMPORT_ACCOUNT_ROOT_UNAVAILABLE");
        return RunForTests(args, stdout,
            Path.Combine(local, "ProjectNexus", "mcp-probe", "operations"),
            Path.Combine(local, "RimLiaison", "Rimtest", "CandidatePackages"));
    }

    // Injected roots are internal-only deterministic test fixtures.
    internal static int RunForTests(
        string[] args, TextWriter stdout, string operationsRoot,
        string candidatesRoot)
    {
        if (!TryParse(args, out string? operationId, out string? commit,
                out string? archiveHash))
            return Block(stdout, "RIMTEST_IMPORT_ARGUMENTS_INVALID");

        string? ownedStage = null;
        try
        {
            string operations = Path.GetFullPath(operationsRoot);
            string candidates = Path.GetFullPath(candidatesRoot);
            string operationFile = Path.Combine(operations, operationId! + ".json");
            string captured = Path.Combine(operations, operationId + ".outputs",
                "outputs", "artifacts");
            string archive = Path.Combine(captured, "rimtest-native-candidate.zip");
            string receiptFile = Path.Combine(captured, "rimtest-native-build.json");

            RequirePlainFile(operationFile, 65536);
            RequirePlainFile(archive, MaxArchiveBytes);
            RequirePlainFile(receiptFile, 16384);
            string archiveActualHash = ToolchainFileHash.Sha256(archive);
            if (archiveActualHash != archiveHash)
                throw new InvalidDataException("RIMTEST_IMPORT_ARCHIVE_HASH_MISMATCH");

            using JsonDocument operation = JsonDocument.Parse(
                File.ReadAllText(operationFile));
            JsonElement meta = operation.RootElement;
            if (Int(meta, "schemaVersion") != 2 ||
                Text(meta, "operationId") != operationId ||
                Text(meta, "projectId") != "rimliaison" ||
                Text(meta, "kind") != "managed-local-operation" ||
                Text(meta, "capabilityId") != "local_operation.run" ||
                Text(meta, "localOperationId") != OperationName ||
                Text(meta, "localOperationSideEffectClass") != "artifact-generation" ||
                Text(meta, "status") != "passed" || Int(meta, "exitCode") != 0 ||
                !meta.GetProperty("sourceUnchanged").GetBoolean() ||
                Text(meta, "sourceHead") != commit ||
                !Text(meta, "localOperationDefinitionId").StartsWith(
                    "sha256:", StringComparison.Ordinal) ||
                !LowerHex(Text(meta, "localOperationDefinitionId")[7..], 64))
                throw new InvalidDataException("RIMTEST_IMPORT_OPERATION_UNVERIFIED");

            JsonElement outputs = meta.GetProperty("outputEvidence");
            if (outputs.ValueKind != JsonValueKind.Array ||
                outputs.GetArrayLength() != 2)
                throw new InvalidDataException("RIMTEST_IMPORT_OUTPUTS_UNVERIFIED");
            VerifyOutput(outputs[0], ZipName,
                "outputs/artifacts/rimtest-native-candidate.zip",
                new FileInfo(archive).Length, archiveActualHash);
            VerifyOutput(outputs[1], ReceiptName,
                "outputs/artifacts/rimtest-native-build.json",
                new FileInfo(receiptFile).Length,
                ToolchainFileHash.Sha256(receiptFile));

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(receiptFile));
            JsonElement receipt = document.RootElement;
            if (Text(receipt, "schemaVersion") != "rimtest-pn-artifact/v1" ||
                Text(receipt, "status") != "source-build-only" ||
                Text(receipt, "sourceCommit") != commit ||
                Text(receipt, "archive") != ZipName ||
                Text(receipt, "archiveSha256") != archiveHash ||
                receipt.GetProperty("archiveBytes").GetInt64() !=
                    new FileInfo(archive).Length ||
                !receipt.GetProperty("archiveSmokePassed").GetBoolean() ||
                Text(receipt, "archiveSmokeMethod") !=
                    NativeRimtestArchiveSmoke.Method ||
                Int(receipt, "archiveSmokeProbeCount") !=
                    NativeRimtestArchiveSmoke.ExpectedProbeCount ||
                Text(receipt, "runtimeIdentifier") != "win-x64" ||
                Text(receipt, "targetFramework") != "net8.0" ||
                Text(receipt, "sdkVersion") !=
                    NativeRimtestSourcePublishCommand.PinnedSdk ||
                !LowerHex(Text(receipt, "packageSha256"), 64) ||
                !LowerHex(Text(receipt, "manifestSha256"), 64) ||
                !LowerHex(Text(receipt, "runnerExeSha256"), 64) ||
                !LowerHex(Text(receipt, "cliAssemblySha256"), 64) ||
                receipt.GetProperty("installed").GetBoolean() ||
                receipt.GetProperty("qualified").GetBoolean() ||
                receipt.GetProperty("pathConfigured").GetBoolean() ||
                receipt.GetProperty("pnCheckPassed").GetBoolean())
                throw new InvalidDataException("RIMTEST_IMPORT_RECEIPT_UNVERIFIED");

            // Reject dangerous archive forms before creating account-local
            // staging. Extraction is bounded and is never run in the game.
            using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
            {
                long expanded = 0;
                int files = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                {
                    string path = entry.FullName;
                    bool directory = path.EndsWith('/');
                    string[] parts = path.TrimEnd('/').Split('/');
                    if (parts.Length == 0 || parts.Length > MaxDepth ||
                        parts.Any(part => part.Length is 0 or > 160 ||
                            part == "." || part == ".." || part.EndsWith('.') ||
                            part.EndsWith(' ') || part.Contains(':') ||
                            part.Contains('\\') || part.Any(char.IsControl)) ||
                        path.StartsWith('/') ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                        !names.Add(path.TrimEnd('/')))
                        throw new InvalidDataException("RIMTEST_IMPORT_ZIP_UNSAFE");
                    if (directory)
                        continue;
                    if (++files > MaxEntries || entry.Length > MaxExtractedBytes - expanded)
                        throw new InvalidDataException("RIMTEST_IMPORT_ZIP_LIMIT");
                    expanded += entry.Length;
                }
                if (files == 0)
                    throw new InvalidDataException("RIMTEST_IMPORT_ZIP_EMPTY");
            }

            string final = Path.Combine(candidates, "rimtest-" + commit);
            if (Path.Exists(final))
                throw new InvalidDataException("RIMTEST_IMPORT_VERSION_ALREADY_EXISTS");
            RequirePlainParents(candidates);
            Directory.CreateDirectory(candidates);
            RequirePlainParents(candidates);
            ownedStage = Path.Combine(candidates, ".rimtest-import-" +
                Guid.NewGuid().ToString("N"));
            if (Path.Exists(ownedStage))
                throw new InvalidDataException("RIMTEST_IMPORT_STAGE_COLLISION");
            Directory.CreateDirectory(ownedStage);
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, ownedStage);

            if (!NativeRimtestPackageService.Verify(ownedStage, commit, out _) ||
                !CliDeploymentManifestService.Verify(ownedStage,
                    Path.Combine(ownedStage, CliDeploymentManifestService.FileName),
                    Text(receipt, "manifestSha256"),
                    Text(receipt, "packageSha256"), out _, out _) ||
                ToolchainFileHash.Sha256(Path.Combine(ownedStage, "rimtest.exe")) !=
                    Text(receipt, "runnerExeSha256") ||
                ToolchainFileHash.Sha256(Path.Combine(ownedStage, "rimliaison.dll")) !=
                    Text(receipt, "cliAssemblySha256"))
                throw new InvalidDataException("RIMTEST_IMPORT_PACKAGE_UNVERIFIED");

            // Reverify both immutable input files immediately before creating
            // the versioned candidate; no existing version may be replaced.
            if (ToolchainFileHash.Sha256(archive) != archiveHash ||
                ToolchainFileHash.Sha256(receiptFile) !=
                    Text(outputs[1], "sha256") || Path.Exists(final))
                throw new InvalidDataException("RIMTEST_IMPORT_EVIDENCE_CHANGED");
            Directory.Move(ownedStage, final);
            ownedStage = null;

            if (!NativeRimtestPackageService.Verify(final, commit, out _))
                throw new InvalidDataException("RIMTEST_IMPORT_POSTMOVE_INVALID");
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = Schema,
                status = "candidate-imported",
                sourceCommit = commit,
                operationId,
                archiveSha256 = archiveHash,
                candidateRoot = final,
                packageSha256 = Text(receipt, "packageSha256"),
                installed = false,
                pathConfigured = false,
                qualified = false,
                pnCheckPassed = false,
                independentlyAttestedBuild = false
            }));
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or JsonException or
            KeyNotFoundException or InvalidOperationException or
            System.Security.SecurityException)
        {
            string code = ex.Message.Split(':')[0];
            if (!code.StartsWith("RIMTEST_IMPORT_", StringComparison.Ordinal))
                code = "RIMTEST_IMPORT_EVIDENCE_INVALID";
            return Block(stdout, code);
        }
        finally
        {
            if (ownedStage is not null && Directory.Exists(ownedStage))
                Directory.Delete(ownedStage, recursive: true);
        }
    }

    private static void VerifyOutput(JsonElement output, string path,
        string evidencePath, long actualBytes, string actualSha256)
    {
        if (Text(output, "path") != path ||
            Text(output, "evidencePath") != evidencePath ||
            output.GetProperty("size").GetInt64() != actualBytes ||
            Text(output, "sha256") != actualSha256)
            throw new InvalidDataException("RIMTEST_IMPORT_OUTPUTS_UNVERIFIED");
    }

    private static string Text(JsonElement root, string property) =>
        root.GetProperty(property).GetString() ??
        throw new InvalidDataException("RIMTEST_IMPORT_EVIDENCE_INVALID");
    private static int Int(JsonElement root, string property) =>
        root.GetProperty(property).GetInt32();
    private static bool LowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequirePlainFile(string file, long maxBytes)
    {
        RequirePlainParents(Path.GetDirectoryName(file)!);
        var info = new FileInfo(file);
        if (!info.Exists || info.Length is <= 0 || info.Length > maxBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("RIMTEST_IMPORT_CAPTURE_UNAVAILABLE");
    }

    private static void RequirePlainParents(string directory)
    {
        DirectoryInfo? cursor = new(Path.GetFullPath(directory));
        while (cursor is not null)
        {
            if (cursor.Exists &&
                (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_IMPORT_REPARSE_PARENT");
            cursor = cursor.Parent;
        }
    }

    private static int Block(TextWriter output, string code)
    {
        output.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = Schema,
            status = "blocked",
            code,
            installed = false,
            pathConfigured = false,
            qualified = false,
            pnCheckPassed = false,
            independentlyAttestedBuild = false
        }));
        return RimLiaison.CliExitCodes.ConservativeSelection;
    }
}

internal static class NativeRimtestPackageService
{
    internal const string RunnerSidecarFileName = "rimtest-dev-runner.json";
    internal const string RunnerExecutableFileName = "rimtest.exe";
    private const string CliExecutableFileName = "rimliaison.exe";
    private const string CliAssemblyFileName = "rimliaison.dll";
    private const string CliDepsFileName = "rimliaison.deps.json";
    private const string CliRuntimeConfigFileName = "rimliaison.runtimeconfig.json";
    private const string SidecarSchema = "rimtest-dev-runner/v1";
    private const int MaximumFileCount = 2048;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumDepth = 24;
    private const int MaximumSidecarBytes = 4096;

    internal static Task<NativeRimtestPackageCandidate> StageFromCleanGitAsync(
        string sourceRoot,
        string publishedCliRoot,
        string expectedSourceCommit,
        CancellationToken cancellationToken = default)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidDataException("RIMTEST_PACKAGE_ACCOUNT_ROOT_UNAVAILABLE");

        // Production callers cannot supply a target in a mod, game folder or
        // production toolchain. The package is inert until separately approved.
        string accountPackages = Path.Combine(local, "RimLiaison", "Rimtest", "CandidatePackages");
        return StageCoreAsync(
            sourceRoot, publishedCliRoot, expectedSourceCommit, accountPackages,
            new RimLiaison.Git.SystemGitRepositoryStateProvider(),
            cancellationToken);
    }

    // This explicitly injected path is internal for deterministic isolated
    // fixtures; it is not a user-facing command or a trust grant.
    internal static Task<NativeRimtestPackageCandidate> StageForTestsAsync(
        string sourceRoot,
        string publishedCliRoot,
        string expectedSourceCommit,
        string packageParent,
        RimLiaison.Git.IGitRepositoryStateProvider sourceState,
        CancellationToken cancellationToken = default) =>
        StageCoreAsync(
            sourceRoot, publishedCliRoot, expectedSourceCommit, packageParent,
            sourceState, cancellationToken);

    private static async Task<NativeRimtestPackageCandidate> StageCoreAsync(
        string sourceRoot,
        string publishedCliRoot,
        string expectedSourceCommit,
        string packageParent,
        RimLiaison.Git.IGitRepositoryStateProvider sourceState,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native rimtest apphost staging requires Windows.");
        if (!LowerHex(expectedSourceCommit, 40))
            throw new InvalidDataException("RIMTEST_PACKAGE_SOURCE_SHA_INVALID");
        cancellationToken.ThrowIfCancellationRequested();

        string source = Path.GetFullPath(sourceRoot);
        string published = Path.GetFullPath(publishedCliRoot);
        string parent = Path.GetFullPath(packageParent);
        if (Within(source, parent) || Within(published, parent) ||
            Within(parent, source) || Within(parent, published))
            throw new InvalidDataException("RIMTEST_PACKAGE_OUTPUT_OVERLAPS_INPUT");

        RequirePlainDirectory(source);
        RequirePlainDirectory(published);
        if (!File.Exists(Path.Combine(published, CliExecutableFileName)) ||
            !File.Exists(Path.Combine(published, CliAssemblyFileName)) ||
            !File.Exists(Path.Combine(published, CliDepsFileName)) ||
            !File.Exists(Path.Combine(published, CliRuntimeConfigFileName)))
            throw new InvalidDataException("RIMTEST_PACKAGE_PUBLISH_INCOMPLETE");
        if (File.Exists(Path.Combine(published, RunnerExecutableFileName)) ||
            File.Exists(Path.Combine(published, RunnerSidecarFileName)))
            throw new InvalidDataException("RIMTEST_PACKAGE_PUBLISH_AMBIGUOUS");

        await RequireExactCleanSourceAsync(sourceState, source, expectedSourceCommit,
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(parent);
        RequirePlainDirectory(parent);

        string final = Path.Combine(parent, "rimtest-" + expectedSourceCommit);
        if (Path.Exists(final))
            throw new IOException("RIMTEST_PACKAGE_VERSION_ALREADY_EXISTS");

        string stage = Path.Combine(parent, ".rimtest-stage-" + Guid.NewGuid().ToString("N"));
        if (Path.Exists(stage))
            throw new IOException("RIMTEST_PACKAGE_STAGE_COLLISION");

        bool stageOwned = false;
        try
        {
            Directory.CreateDirectory(stage);
            stageOwned = true;
            int count = 0;
            long bytes = 0;
            CopyPublishedTree(published, stage, 0, ref count, ref bytes);
            File.Copy(Path.Combine(published, CliExecutableFileName),
                Path.Combine(stage, RunnerExecutableFileName), overwrite: false);

            string sourceExe = Path.Combine(published, CliExecutableFileName);
            if (!string.Equals(
                    ToolchainFileHash.Sha256(sourceExe),
                    ToolchainFileHash.Sha256(Path.Combine(stage, RunnerExecutableFileName)),
                    StringComparison.Ordinal))
                throw new InvalidDataException("RIMTEST_PACKAGE_APPHOST_COPY_MISMATCH");

            string sidecar = Path.Combine(stage, RunnerSidecarFileName);
            string sidecarJson = JsonSerializer.Serialize(new
            {
                schemaVersion = SidecarSchema,
                sourceCommit = expectedSourceCommit,
                runnerExeSha256 = ToolchainFileHash.Sha256(
                    Path.Combine(stage, RunnerExecutableFileName)),
                cliAssemblySha256 = ToolchainFileHash.Sha256(
                    Path.Combine(stage, CliAssemblyFileName))
            });
            File.WriteAllText(sidecar, sidecarJson, new UTF8Encoding(false));

            CliDeploymentManifest manifest = CliDeploymentManifestService.Write(
                stage, expectedSourceCommit, "net8.0");
            if (!Verify(stage, expectedSourceCommit, out string? verificationError))
                throw new InvalidDataException(
                    "RIMTEST_PACKAGE_POSTSTAGE_VERIFICATION_FAILED:" + verificationError);

            // Reject changes between source read and publication, including a
            // moved HEAD or dirty worktree. No source snapshot grants runtime trust.
            await RequireExactCleanSourceAsync(sourceState, source, expectedSourceCommit,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.Exists(final))
                throw new IOException("RIMTEST_PACKAGE_VERSION_ALREADY_EXISTS");

            Directory.Move(stage, final); // Non-replacing, same-volume publication.
            stageOwned = false;
            return new NativeRimtestPackageCandidate(
                final,
                expectedSourceCommit,
                ToolchainFileHash.Sha256(Path.Combine(final,
                    CliDeploymentManifestService.FileName)),
                manifest.PackageSha256);
        }
        finally
        {
            if (stageOwned && Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    internal static bool Verify(
        string packageRoot,
        string? expectedSourceCommit,
        out string? error)
    {
        error = null;
        try
        {
            string root = Path.GetFullPath(packageRoot);
            RequirePlainDirectory(root);
            int count = 0;
            long bytes = 0;
            VerifyPlainTree(root, 0, ref count, ref bytes);
            if (File.Exists(Path.Combine(root, CliExecutableFileName)))
                throw new InvalidDataException("RIMTEST_PACKAGE_UNRESTRICTED_APPHOST_PRESENT");

            string[] required =
            [
                RunnerExecutableFileName,
                CliAssemblyFileName,
                CliDepsFileName,
                CliRuntimeConfigFileName,
                RunnerSidecarFileName,
                CliDeploymentManifestService.FileName
            ];
            if (required.Any(file => !File.Exists(Path.Combine(root, file))))
                throw new InvalidDataException("RIMTEST_PACKAGE_REQUIRED_FILE_MISSING");

            string sidecarPath = Path.Combine(root, RunnerSidecarFileName);
            if (new FileInfo(sidecarPath).Length > MaximumSidecarBytes)
                throw new InvalidDataException("RIMTEST_PACKAGE_SIDECAR_OVERSIZE");
            using JsonDocument parsed = JsonDocument.Parse(
                File.ReadAllText(sidecarPath));
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("RIMTEST_PACKAGE_SIDECAR_INVALID");
            JsonProperty[] properties = parsed.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 4 ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidDataException("RIMTEST_PACKAGE_SIDECAR_INVALID");

            JsonElement json = parsed.RootElement;
            if (!json.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String ||
                schema.GetString() != SidecarSchema ||
                !json.TryGetProperty("sourceCommit", out JsonElement commit) ||
                commit.ValueKind != JsonValueKind.String ||
                !LowerHex(commit.GetString(), 40) ||
                !json.TryGetProperty("runnerExeSha256", out JsonElement exeHash) ||
                exeHash.ValueKind != JsonValueKind.String ||
                !LowerHex(exeHash.GetString(), 64) ||
                !json.TryGetProperty("cliAssemblySha256", out JsonElement dllHash) ||
                dllHash.ValueKind != JsonValueKind.String ||
                !LowerHex(dllHash.GetString(), 64))
                throw new InvalidDataException("RIMTEST_PACKAGE_SIDECAR_INVALID");

            string pinnedCommit = commit.GetString()!;
            if (expectedSourceCommit is not null &&
                !string.Equals(expectedSourceCommit, pinnedCommit, StringComparison.Ordinal))
                throw new InvalidDataException("RIMTEST_PACKAGE_SOURCE_COMMIT_MISMATCH");
            if (!string.Equals(exeHash.GetString(),
                    ToolchainFileHash.Sha256(Path.Combine(root, RunnerExecutableFileName)),
                    StringComparison.Ordinal) ||
                !string.Equals(dllHash.GetString(),
                    ToolchainFileHash.Sha256(Path.Combine(root, CliAssemblyFileName)),
                    StringComparison.Ordinal))
                throw new InvalidDataException("RIMTEST_PACKAGE_SIDECAR_HASH_MISMATCH");

            string manifestPath = Path.Combine(root, CliDeploymentManifestService.FileName);
            if (!CliDeploymentManifestService.Verify(root, manifestPath, null, null,
                    out CliDeploymentManifest? manifest, out string? closureError) ||
                manifest is null ||
                manifest.TargetFramework != "net8.0" ||
                manifest.SourceCommit != pinnedCommit ||
                required.Any(file => !CliDeploymentManifestService.ContainsFile(manifest, file) &&
                    file != CliDeploymentManifestService.FileName))
                throw new InvalidDataException(
                    "RIMTEST_PACKAGE_CLOSURE_INVALID:" + closureError);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException or
            InvalidDataException or JsonException or System.Security.SecurityException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static async Task RequireExactCleanSourceAsync(
        RimLiaison.Git.IGitRepositoryStateProvider sourceState,
        string sourceRoot,
        string expectedSourceCommit,
        CancellationToken cancellationToken)
    {
        RimLiaison.Git.GitRepositoryStateResult result = await
            sourceState.ReadWorktreeAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        RimLiaison.Git.GitRepositoryStateSnapshot? state = result.State;
        if (!result.Resolved || state is null || state.Dirty ||
            !string.Equals(Path.GetFullPath(state.RootPath), sourceRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.HeadSha, expectedSourceCommit, StringComparison.Ordinal))
            throw new InvalidDataException("RIMTEST_PACKAGE_GIT_SOURCE_UNVERIFIED");
    }

    private static void CopyPublishedTree(
        string source,
        string target,
        int depth,
        ref int count,
        ref long bytes)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("RIMTEST_PACKAGE_TREE_TOO_DEEP");
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_PACKAGE_REPARSE_ENTRY");
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(Path.Combine(target, entry.Name));
                CopyPublishedTree(entry.FullName, Path.Combine(target, entry.Name),
                    depth + 1, ref count, ref bytes);
            }
            else if (entry is FileInfo file)
            {
                if (depth == 0 &&
                    (file.Name.Equals(CliExecutableFileName, StringComparison.OrdinalIgnoreCase) ||
                     file.Name.Equals(CliDeploymentManifestService.FileName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (depth == 0 &&
                    (file.Name.Equals(RunnerExecutableFileName, StringComparison.OrdinalIgnoreCase) ||
                     file.Name.Equals(RunnerSidecarFileName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("RIMTEST_PACKAGE_PUBLISH_AMBIGUOUS");
                count++;
                bytes += file.Length;
                if (count > MaximumFileCount || bytes > MaximumPackageBytes)
                    throw new InvalidDataException("RIMTEST_PACKAGE_TOO_LARGE");
                File.Copy(file.FullName, Path.Combine(target, file.Name), overwrite: false);
            }
            else
            {
                throw new InvalidDataException("RIMTEST_PACKAGE_UNKNOWN_FILE_TYPE");
            }
        }
    }

    private static void VerifyPlainTree(
        string root,
        int depth,
        ref int count,
        ref long bytes)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("RIMTEST_PACKAGE_TREE_TOO_DEEP");
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_PACKAGE_REPARSE_ENTRY");
            if (entry is DirectoryInfo)
                VerifyPlainTree(entry.FullName, depth + 1, ref count, ref bytes);
            else if (entry is FileInfo file)
            {
                count++;
                bytes += file.Length;
                if (count > MaximumFileCount || bytes > MaximumPackageBytes)
                    throw new InvalidDataException("RIMTEST_PACKAGE_TOO_LARGE");
            }
            else
                throw new InvalidDataException("RIMTEST_PACKAGE_UNKNOWN_FILE_TYPE");
        }
    }

    private static void RequirePlainDirectory(string path)
    {
        DirectoryInfo? current = new(path);
        if (!current.Exists)
            throw new DirectoryNotFoundException("RIMTEST_PACKAGE_DIRECTORY_MISSING");
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_PACKAGE_REPARSE_DIRECTORY");
            current = current.Parent;
        }
    }

    private static bool LowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Within(string parent, string candidate)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string child = Path.GetFullPath(candidate);
        return string.Equals(root, child, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

// Candidate-only Windows artifact emission for a separately trusted PN local
// operation. No user-supplied path, install step or system PATH change exists.
internal static class ManagedNativeRimtestArtifactBuildCommand
{
    internal const string Name = "rimtest-artifact-build";
    internal const string ArchivePath = "artifacts/rimtest-native-candidate.zip";
    internal const string ReceiptPath = "artifacts/rimtest-native-build.json";
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;

    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == Name;

    internal static bool TryParse(IReadOnlyList<string> args) =>
        args.Count == 2 && args[0] == Name && args[1] == "--json";

    internal static string[] PublishArguments(string publishedRoot) =>
    [
        "publish", "src/RimLiaison.Cli/RimLiaison.Cli.csproj",
        "--configuration", "Release",
        "--framework", "net8.0",
        "--runtime", "win-x64",
        "--self-contained", "false",
        "-p:UseAppHost=true",
        "--output", publishedRoot,
        "--nologo"
    ];

    internal static async Task<int> RunAsync(
        string[] args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (!TryParse(args))
            return Block(stdout, "RIMTEST_ARTIFACT_ARGUMENTS_INVALID");
        if (!OperatingSystem.IsWindows())
            return Block(stdout, "RIMTEST_ARTIFACT_WINDOWS_REQUIRED");

        string? scratch = null;
        string? archive = null;
        string? receipt = null;
        bool completed = false;
        try
        {
            string root = Path.GetFullPath(Environment.CurrentDirectory);
            if (!File.Exists(Path.Combine(root, "RimLiaison.sln")) ||
                !File.Exists(Path.Combine(root, ".projectnexus", "project.json")) ||
                !File.Exists(Path.Combine(
                    root, "src", "RimLiaison.Cli", "RimLiaison.Cli.csproj")))
                throw new InvalidDataException("RIMTEST_ARTIFACT_SOURCE_ROOT_INVALID");

            using JsonDocument pin = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "global.json")));
            JsonElement sdk = pin.RootElement.GetProperty("sdk");
            if (sdk.GetProperty("version").GetString() !=
                    NativeRimtestSourcePublishCommand.PinnedSdk ||
                sdk.GetProperty("rollForward").GetString() != "disable" ||
                sdk.GetProperty("allowPrerelease").ValueKind != JsonValueKind.False)
                throw new InvalidDataException("RIMTEST_ARTIFACT_SDK_PIN_INVALID");

            var git = new RimLiaison.Git.SystemGitRepositoryStateProvider();
            RimLiaison.Git.GitRepositoryStateResult original =
                await git.ReadWorktreeAsync(root, cancellationToken).ConfigureAwait(false);
            RimLiaison.Git.GitRepositoryStateSnapshot? state = original.State;
            string? pinnedSource = state?.HeadSha;
            if (!original.Resolved || state is null || state.Dirty ||
                pinnedSource is not { Length: 40 } ||
                !string.Equals(Path.GetFullPath(state.RootPath), root,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RIMTEST_ARTIFACT_SOURCE_UNVERIFIED");
            string sourceCommit = pinnedSource;

            string? host = NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                NativeRimtestSourcePublishCommand.IsSafeDotnetHost);
            if (host is null)
                throw new InvalidDataException("RIMTEST_ARTIFACT_DOTNET_HOST_MISSING");

            NativeRimtestPublishResult version =
                await NativeRimtestSourcePublishCommand.ExecuteFixedDotnetAsync(
                    host, root, ["--version"], cancellationToken).ConfigureAwait(false);
            if (version.ExitCode != 0 ||
                version.Output.Trim() != NativeRimtestSourcePublishCommand.PinnedSdk)
                throw new InvalidDataException("RIMTEST_ARTIFACT_SDK_MISMATCH");

            string artifactRoot = Path.Combine(root, "artifacts");
            archive = Path.Combine(root,
                ArchivePath.Replace('/', Path.DirectorySeparatorChar));
            receipt = Path.Combine(root,
                ReceiptPath.Replace('/', Path.DirectorySeparatorChar));
            if (Path.Exists(archive) || Path.Exists(receipt))
                throw new InvalidDataException("RIMTEST_ARTIFACT_OUTPUT_ALREADY_EXISTS");
            if (Directory.Exists(artifactRoot) &&
                (new DirectoryInfo(artifactRoot).Attributes &
                    FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("RIMTEST_ARTIFACT_OUTPUT_REPARSE");

            scratch = Path.Combine(root, "src", "RimLiaison.Cli", "obj",
                ".rimtest-artifact-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(scratch))
                throw new InvalidDataException("RIMTEST_ARTIFACT_SCRATCH_COLLISION");
            Directory.CreateDirectory(scratch);
            string published = Path.Combine(scratch, "published");
            NativeRimtestPublishResult build =
                await NativeRimtestSourcePublishCommand.ExecuteFixedDotnetAsync(
                    host, root, PublishArguments(published), cancellationToken)
                    .ConfigureAwait(false);
            if (build.ExitCode != 0)
                throw new InvalidDataException("RIMTEST_ARTIFACT_PUBLISH_FAILED");

            string package = Path.Combine(scratch, "package");
            Directory.Move(published, package);
            foreach (string required in new[]
                     {
                         "rimliaison.exe", "rimliaison.dll",
                         "rimliaison.deps.json", "rimliaison.runtimeconfig.json"
                     })
                if (!File.Exists(Path.Combine(package, required)))
                    throw new InvalidDataException("RIMTEST_ARTIFACT_PUBLISH_INCOMPLETE");

            string apphost = Path.Combine(package, "rimliaison.exe");
            string runner = Path.Combine(package, "rimtest.exe");
            string assembly = Path.Combine(package, "rimliaison.dll");
            File.Copy(apphost, runner, overwrite: false);
            File.Delete(apphost); // Never package the unrestricted CLI apphost.
            File.WriteAllText(
                Path.Combine(package, NativeRimtestPackageService.RunnerSidecarFileName),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimtest-dev-runner/v1",
                    sourceCommit,
                    runnerExeSha256 = ToolchainFileHash.Sha256(runner),
                    cliAssemblySha256 = ToolchainFileHash.Sha256(assembly)
                }), new UTF8Encoding(false));

            CliDeploymentManifest manifest =
                CliDeploymentManifestService.Write(package, sourceCommit, "net8.0");
            if (!NativeRimtestPackageService.Verify(
                    package, sourceCommit, out _))
                throw new InvalidDataException("RIMTEST_ARTIFACT_PACKAGE_INVALID");

            RimLiaison.Git.GitRepositoryStateResult after =
                await git.ReadWorktreeAsync(root, cancellationToken).ConfigureAwait(false);
            if (!after.Resolved || after.State is null || after.State.Dirty ||
                after.State.HeadSha != sourceCommit)
                throw new InvalidDataException("RIMTEST_ARTIFACT_SOURCE_MOVED");

            // Keep the as-yet unpublished ZIP under the ignored obj/ scratch
            // tree. Creating artifacts/ before the final Git recheck would
            // make our own untracked output look like a source mutation.
            string stagedArchive = Path.Combine(scratch, "rimtest-native-candidate.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(
                package, stagedArchive, System.IO.Compression.CompressionLevel.Optimal,
                includeBaseDirectory: false);
            long archiveBytes = new FileInfo(stagedArchive).Length;
            if (archiveBytes <= 0 || archiveBytes > MaximumArchiveBytes)
                throw new InvalidDataException("RIMTEST_ARTIFACT_ARCHIVE_OVERSIZE");

            string archiveSha256 = ToolchainFileHash.Sha256(stagedArchive);
            int archiveSmokeProbeCount = await NativeRimtestArchiveSmoke.VerifyAsync(
                stagedArchive, archiveSha256, sourceCommit, manifest.PackageSha256,
                Path.Combine(scratch, "archive-smoke"), cancellationToken)
                .ConfigureAwait(false);

            // Publication requires proof that the real repository remained
            // clean after running the ZIP's fail-closed native probes. Only
            // then is the exact verified ZIP moved to ProjectNexus's fixed
            // declared output path.
            await NativeRimtestArchivePublication.PublishAfterSourceCheckAsync(
                stagedArchive, archive, archiveBytes, archiveSha256,
                async token =>
                {
                    RimLiaison.Git.GitRepositoryStateResult postSmoke =
                        await git.ReadWorktreeAsync(root, token).ConfigureAwait(false);
                    return postSmoke.Resolved && postSmoke.State is not null &&
                        !postSmoke.State.Dirty && postSmoke.State.HeadSha == sourceCommit;
                }, cancellationToken).ConfigureAwait(false);

            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                schemaVersion = "rimtest-pn-artifact/v1",
                status = "source-build-only",
                sourceCommit,
                sdkVersion = NativeRimtestSourcePublishCommand.PinnedSdk,
                targetFramework = "net8.0",
                runtimeIdentifier = "win-x64",
                archive = ArchivePath,
                archiveSha256,
                archiveBytes,
                archiveSmokePassed = true,
                archiveSmokeMethod = NativeRimtestArchiveSmoke.Method,
                archiveSmokeProbeCount,
                packageSha256 = manifest.PackageSha256,
                runnerExeSha256 = ToolchainFileHash.Sha256(runner),
                cliAssemblySha256 = ToolchainFileHash.Sha256(assembly),
                manifestSha256 = ToolchainFileHash.Sha256(Path.Combine(
                    package, CliDeploymentManifestService.FileName)),
                qualified = false,
                installed = false,
                pathConfigured = false,
                pnCheckPassed = false
            }), new UTF8Encoding(false));
            completed = true;
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "rimtest-pn-artifact/v1",
                status = "source-build-only",
                sourceCommit,
                archiveSha256,
                archiveSmokePassed = true,
                archiveSmokeMethod = NativeRimtestArchiveSmoke.Method,
                archiveSmokeProbeCount,
                installed = false,
                pathConfigured = false,
                qualified = false,
                pnCheckPassed = false
            }));
            return RimLiaison.CliExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or JsonException or
            NotSupportedException or OperationCanceledException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception
            or KeyNotFoundException)
        {
            string code = ex.Message.Split(':')[0];
            if (!code.StartsWith("RIMTEST_ARTIFACT_", StringComparison.Ordinal))
                code = "RIMTEST_ARTIFACT_BUILD_FAILED";
            return Block(stdout, code);
        }
        finally
        {
            if (!completed)
            {
                if (archive is not null && File.Exists(archive))
                    File.Delete(archive);
                if (receipt is not null && File.Exists(receipt))
                    File.Delete(receipt);
            }
            if (scratch is not null && Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private static int Block(TextWriter stdout, string code)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = "rimtest-pn-artifact/v1",
            status = "blocked",
            code,
            installed = false,
            pathConfigured = false,
            qualified = false,
            pnCheckPassed = false
        }));
        return RimLiaison.CliExitCodes.ConservativeSelection;
    }
}


// Publishes a verified native ZIP only after a fresh source cleanliness
// check. Both the staged ZIP and every smoke-test scratch file remain under
// the ignored obj/ tree until the check completes.
internal static class NativeRimtestArchivePublication
{
    internal static async Task PublishAfterSourceCheckAsync(
        string stagedArchive,
        string outputArchive,
        long expectedBytes,
        string expectedSha256,
        Func<CancellationToken, Task<bool>> sourceClean,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static bool Matches(string path, long bytes, string sha256) =>
            File.Exists(path) && new FileInfo(path).Length == bytes &&
            string.Equals(ToolchainFileHash.Sha256(path), sha256,
                StringComparison.Ordinal);

        if (expectedBytes <= 0 || expectedSha256.Length != 64 ||
            Path.Exists(outputArchive) ||
            !Matches(stagedArchive, expectedBytes, expectedSha256))
            throw new InvalidDataException("RIMTEST_ARTIFACT_ARCHIVE_MISMATCH");

        if (!await sourceClean(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("RIMTEST_ARTIFACT_SOURCE_MOVED");

        cancellationToken.ThrowIfCancellationRequested();
        if (Path.Exists(outputArchive) ||
            !Matches(stagedArchive, expectedBytes, expectedSha256))
            throw new InvalidDataException("RIMTEST_ARTIFACT_ARCHIVE_MISMATCH");

        string destination = Path.GetFullPath(outputArchive);
        string parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("RIMTEST_ARTIFACT_OUTPUT_INVALID");
        if (Directory.Exists(parent) &&
            (new DirectoryInfo(parent).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("RIMTEST_ARTIFACT_OUTPUT_REPARSE");

        Directory.CreateDirectory(parent);
        File.Move(stagedArchive, destination); // No overwrite of an existing output.
        if (!Matches(destination, expectedBytes, expectedSha256))
            throw new InvalidDataException("RIMTEST_ARTIFACT_ARCHIVE_MISMATCH");
    }
}

// This source-build-only verifier proves the actual ZIP just emitted by
// the fixed native artifact operation. It never runs an allowed dev-check,
// installs a runner, changes PATH, or asserts real-game qualification.
internal static class NativeRimtestArchiveSmoke
{
    internal const string Method = "zip-extracted-blocked-apphost/v1";
    internal const int ExpectedProbeCount = 2;
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumFileCount = 2048;

    internal static async Task<int> VerifyAsync(
        string archivePath,
        string expectedArchiveSha256,
        string expectedSourceCommit,
        string expectedPackageSha256,
        string scratchRoot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_WINDOWS_REQUIRED");
        string archive = Path.GetFullPath(archivePath);
        string ownedRoot = Path.GetFullPath(scratchRoot);
        if (Path.Exists(ownedRoot))
            throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_SCRATCH_EXISTS");
        if (!File.Exists(archive) ||
            new FileInfo(archive).Length is <= 0 or > MaximumArchiveBytes ||
            !string.Equals(ToolchainFileHash.Sha256(archive),
                expectedArchiveSha256, StringComparison.Ordinal))
            throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_ARCHIVE_MISMATCH");

        // Bound decompression before materializing anything, including archives
        // that could have a small compressed size but enormous contents.
        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            long expandedBytes = 0;
            int count = 0;
            foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Name.Length == 0)
                    continue;
                if (++count > MaximumFileCount ||
                    entry.Length > MaximumExpandedBytes - expandedBytes)
                    throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_ZIP_LIMIT");
                expandedBytes += entry.Length;
            }
            if (count == 0)
                throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_ZIP_EMPTY");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ownedRoot);
        try
        {
            string extracted = Path.Combine(ownedRoot, "extracted");
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, extracted);
            string manifestPath = Path.Combine(
                extracted, CliDeploymentManifestService.FileName);
            if (!NativeRimtestPackageService.Verify(
                    extracted, expectedSourceCommit, out _) ||
                !CliDeploymentManifestService.Verify(
                    extracted, manifestPath, null, expectedPackageSha256,
                    out CliDeploymentManifest? manifest, out _) ||
                manifest is null || manifest.SourceCommit != expectedSourceCommit)
                throw new InvalidDataException("RIMTEST_ARTIFACT_SMOKE_CLOSURE_MISMATCH");

            string emptyMod = Path.Combine(ownedRoot, "not-a-mod");
            Directory.CreateDirectory(emptyMod);
            string runner = Path.Combine(extracted, "rimtest.exe");
            var probes = new (string[] Args, string ErrorCode)[]
            {
                (["qualification", "promote", "--json"],
                    "DEV_RUNNER_COMMAND_NOT_ALLOWED"),
                (["dev-check", "--json"], "DEV_RUNNER_WORKSPACE_INVALID")
            };
            foreach ((string[] arguments, string code) in probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RimLiaison.NativeRimtestRunner.Dispatch? admission =
                    RimLiaison.NativeRimtestRunner.Prepare(
                        runner, arguments, emptyMod);
                if (admission is null || admission.Allowed ||
                    admission.ErrorCode != code)
                    throw new InvalidDataException(
                        "RIMTEST_ARTIFACT_SMOKE_PROBE_ADMITTED");

                PromotionChildProcessResult child =
                    await ToolchainPromotionService.RunJsonCommandAsync(
                        runner, arguments, cancellationToken,
                        workingDirectory: emptyMod,
                        processTimeout: TimeSpan.FromSeconds(12))
                        .ConfigureAwait(false);
                if (child.ExitCode != RimLiaison.CliExitCodes.ConservativeSelection ||
                    child.TimedOut || child.Cancelled ||
                    child.StartError is not null ||
                    !string.IsNullOrWhiteSpace(child.Stderr) ||
                    child.Stdout.Length is <= 0 or > 4096)
                    throw new InvalidDataException(
                        "RIMTEST_ARTIFACT_SMOKE_PROCESS_FAILED");
                using JsonDocument result = JsonDocument.Parse(child.Stdout);
                JsonElement output = result.RootElement;
                if (output.GetProperty("schemaVersion").GetString() !=
                        "rimtest-dev-runner/v1" ||
                    output.GetProperty("status").GetString() != "blocked" ||
                    output.GetProperty("code").GetString() != code)
                    throw new InvalidDataException(
                        "RIMTEST_ARTIFACT_SMOKE_IDENTITY_MISMATCH");
            }

            if (Directory.EnumerateFileSystemEntries(emptyMod).Any() ||
                !string.Equals(ToolchainFileHash.Sha256(archive),
                    expectedArchiveSha256, StringComparison.Ordinal) ||
                !NativeRimtestPackageService.Verify(
                    extracted, expectedSourceCommit, out _) ||
                !CliDeploymentManifestService.Verify(
                    extracted, manifestPath, null, expectedPackageSha256,
                    out _, out _))
                throw new InvalidDataException(
                    "RIMTEST_ARTIFACT_SMOKE_POSTRUN_MISMATCH");
            return probes.Length;
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
        }
    }
}

