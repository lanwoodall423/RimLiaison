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

// A candidate package is not production-qualified or installed on PATH.
internal sealed record NativeRimtestPackageCandidate(
    string PackageRoot,
    string SourceCommit,
    string ManifestSha256,
    string PackageSha256);

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
