using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Git;

namespace RimLiaison.Toolchain;

public sealed record ToolchainCandidate(
    string SourceCommit,
    string CandidateRoot,
    string RimLiaisonExecutablePath,
    string RimLiaisonAssemblyPath,
    string DevBridgeRuntimeRoot,
    string DevBridgeRuntimeArtifactRoot,
    string DevBridgePackageSha256,
    string DevBridgeCoordinatorSha256,
    string TransactionConsumerPath,
    string RuntimeProtocolContract,
    string DevBridgeSourceRoot,
    string DevBridgeSourceCommit,
    string DevBridgeReleaseManifestPath,
    string DevBridgeReleaseManifestSha256)
{
    public string RimLiaisonExecutableSha256 => ToolchainFileHash.Sha256(RimLiaisonExecutablePath);
    public string RimLiaisonAssemblySha256 => ToolchainFileHash.Sha256(RimLiaisonAssemblyPath);
    public string TransactionConsumerSha256 => ToolchainFileHash.Sha256(TransactionConsumerPath);
}

internal sealed record ToolchainCandidateMaterializationResult(
    ToolchainCandidate? Candidate,
    string? ErrorCode,
    string? Error,
    string? NextAction)
{
    public bool Succeeded => Candidate is not null;

    public static ToolchainCandidateMaterializationResult Failure(
        string code,
        string error,
        string nextAction) => new(null, code, error, nextAction);
}

internal static class ToolchainCandidateMaterializer
{
    private const string CandidatePackageEnvironment = "RIMLIAISON_DEVBRIDGE_CANDIDATE_PACKAGE";
    private const string PinnedRootEnvironment = "DEVBRIDGE_PINNED_WORKTREE_ROOT";
    private const string PinnedRootCompatibilityEnvironment = "RIMTEST_DEVBRIDGE_PINNED_ROOT";
    private const string SourceRootEnvironment = "DEVBRIDGE_SOURCE_ROOT";
    private const string RuntimeRootEnvironment = "RIMTEST_DEVBRIDGE_ROOT";

    public static async Task<ToolchainCandidateMaterializationResult> MaterializeAsync(
        string sourceRoot,
        string candidateRoot,
        string artifactRoot,
        string runtimeRoot,
        string runtimeProtocolContract,
        CancellationToken cancellationToken)
    {
        try
        {
            GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
                .ReadAsync(sourceRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!source.Resolved || string.IsNullOrWhiteSpace(source.State?.HeadSha))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "CANDIDATE_SOURCE_UNRESOLVED",
                    "The RimLiaison source identity could not be resolved before candidate creation.",
                    "Resolve the source checkout, then retry qualification.");
            }
            if (source.State.Dirty)
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "CANDIDATE_SOURCE_DIRTY",
                    "Candidate creation refuses a dirty RimLiaison source checkout.",
                    "Commit or restore the source checkout, then retry qualification.");
            }

            string fullCandidateRoot = Path.GetFullPath(candidateRoot);
            if (Directory.Exists(fullCandidateRoot))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "CANDIDATE_DESTINATION_EXISTS",
                    "The immutable candidate destination already exists; refusing to reuse it.",
                    "Remove only the abandoned candidate through the owning workflow, then retry qualification.");
            }
            Directory.CreateDirectory(fullCandidateRoot);

            string fullArtifactRoot = Path.GetFullPath(artifactRoot);
            string sourceExecutable = Path.Combine(fullArtifactRoot, "rimliaison.exe");
            string sourceAssembly = Path.Combine(fullArtifactRoot, "rimliaison.dll");
            if (!File.Exists(sourceExecutable) || !File.Exists(sourceAssembly))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "CANDIDATE_RIMLIAISON_ARTIFACT_MISSING",
                    "The current Release RimLiaison executable or assembly is missing.",
                    "Build the RimLiaison Release artifacts, then retry qualification.");
            }

            string devBridgeRoot = ResolveDevBridgeSourceRoot();
            if (!TryReadPinnedDevBridgeRevision(sourceRoot, out string? pinnedDevBridgeRevision))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "CROSS_STACK_PIN_INVALID",
                    "The RimLiaison cross-stack contract has no valid DevBridge2 pin.",
                    "Repair contracts/cross-stack-compatibility.json, then retry qualification.");
            }
            GitRepositoryStateResult devBridgeSource = await new SystemGitRepositoryStateProvider()
                .ReadAsync(devBridgeRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!devBridgeSource.Resolved || devBridgeSource.State?.HeadSha is null)
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_SOURCE_UNRESOLVED",
                    "The pinned DevBridge2 source checkout could not be resolved.",
                    "Materialize the pinned DevBridge2 worktree and retry qualification.");
            }
            if (devBridgeSource.State.Dirty)
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_SOURCE_DIRTY",
                    "The DevBridge2 candidate source checkout is dirty.",
                    "Use the clean pinned DevBridge2 worktree and retry qualification.");
            }
            if (!string.Equals(devBridgeSource.State.HeadSha, pinnedDevBridgeRevision, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_PIN_MISMATCH",
                    "The DevBridge2 candidate source does not match the pinned compatibility revision.",
                    "Materialize the exact pinned DevBridge2 worktree and retry qualification.");
            }
            string? runtimePackageOverride = Environment.GetEnvironmentVariable(CandidatePackageEnvironment);
            string runtimeBuildRoot = Path.Combine(fullCandidateRoot, "devbridge-build");
            string runtimeSource;
            if (!string.IsNullOrWhiteSpace(runtimePackageOverride))
            {
                runtimeSource = Path.GetFullPath(runtimePackageOverride);
                if (!Directory.Exists(runtimeSource))
                {
                    return ToolchainCandidateMaterializationResult.Failure(
                        "DEVBRIDGE_CANDIDATE_PACKAGE_MISSING",
                        "The configured DevBridge candidate package directory is missing.",
                        "Produce the candidate with the pinned DevBridge2 release workflow, then retry qualification.");
                }
            }
            else
            {
                string releaseScript = Path.Combine(devBridgeRoot, "scripts", "release.ps1");
                if (!File.Exists(releaseScript))
                {
                    return ToolchainCandidateMaterializationResult.Failure(
                        "DEVBRIDGE_CANDIDATE_SOURCE_MISSING",
                        "The pinned DevBridge2 release workflow is unavailable.",
                        "Materialize the pinned DevBridge2 worktree and retry qualification.");
                }
                Directory.CreateDirectory(runtimeBuildRoot);
                (int exitCode, string output, string error, bool timedOut) = await RunReleaseAsync(
                        releaseScript,
                        devBridgeRoot,
                        runtimeBuildRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (timedOut || exitCode != 0)
                {
                    string diagnostics = string.Join(
                        " ",
                        new[] { BoundDiagnostic(error), BoundDiagnostic(output) }
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                    return ToolchainCandidateMaterializationResult.Failure(
                        "DEVBRIDGE_CANDIDATE_BUILD_FAILED",
                        "The DevBridge2 owner workflow failed while building the isolated runtime candidate." +
                        (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : " " + diagnostics),
                        "Inspect the bounded DevBridge2 release diagnostics and repair that component before retrying.");
                }
                runtimeSource = ResolveReleasePackage(runtimeBuildRoot);
            }

            string releaseManifestPath = Path.Combine(runtimeSource, "release-manifest.json");
            if (!File.Exists(releaseManifestPath))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_MANIFEST_MISSING",
                    "The DevBridge2 owner workflow produced no release manifest.",
                    "Repair the DevBridge2 release workflow, then retry qualification.");
            }
            using JsonDocument releaseManifest = JsonDocument.Parse(
                File.ReadAllText(releaseManifestPath));
            if (!TryReadReleaseIdentity(releaseManifest.RootElement, out string? componentCommit, out string? identityError))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_IDENTITY_INVALID",
                    identityError ?? "The DevBridge2 release manifest has no exact component identity.",
                    "Produce a clean pinned DevBridge2 release package, then retry qualification.");
            }
            if (!string.Equals(componentCommit, devBridgeSource.State.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_IDENTITY_MISMATCH",
                    "The DevBridge2 release manifest does not match its source checkout.",
                    "Rebuild the candidate from the exact pinned DevBridge2 checkout.");
            }

            string runtimeCandidateRoot = Path.Combine(fullCandidateRoot, "runtime");
            CopyDirectory(runtimeSource, runtimeCandidateRoot);
            string runtimeManifestPath = WriteRuntimeManifest(
                runtimeCandidateRoot,
                releaseManifest.RootElement,
                componentCommit!);
            string coordinatorPath = Path.Combine(
                runtimeCandidateRoot,
                "Coordinator",
                "DevBridge.Coordinator.exe");
            if (!File.Exists(coordinatorPath) || !File.Exists(Path.Combine(runtimeCandidateRoot, "DevBridge.cmd")))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_ARTIFACT_MISSING",
                    "The isolated DevBridge2 candidate is missing its command or coordinator.",
                    "Produce a complete DevBridge2 runtime release, then retry qualification.");
            }

            string candidateExecutable = Path.Combine(fullCandidateRoot, "rimliaison.exe");
            string candidateAssembly = Path.Combine(fullCandidateRoot, "rimliaison.dll");
            File.Copy(sourceExecutable, candidateExecutable, overwrite: false);
            File.Copy(sourceAssembly, candidateAssembly, overwrite: false);

            string sourceConsumer = Path.Combine(devBridgeRoot, "scripts", "mod-test.ps1");
            if (!File.Exists(sourceConsumer))
            {
                return ToolchainCandidateMaterializationResult.Failure(
                    "DEVBRIDGE_CANDIDATE_CONSUMER_MISSING",
                    "The pinned DevBridge2 transaction consumer is missing.",
                    "Repair the pinned DevBridge2 checkout, then retry qualification.");
            }
            string candidateConsumer = Path.Combine(
                fullCandidateRoot,
                "transaction-components",
                "mod-test.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(candidateConsumer)!);
            File.Copy(sourceConsumer, candidateConsumer, overwrite: false);

            string candidateReleaseManifestPath = Path.Combine(
                runtimeCandidateRoot,
                "release-manifest.json");
            string releaseManifestHash = ToolchainFileHash.Sha256(candidateReleaseManifestPath);
            string coordinatorHash = ToolchainFileHash.Sha256(coordinatorPath);
            string packageHash = ReadRuntimePackageHash(runtimeManifestPath);
            var candidate = new ToolchainCandidate(
                source.State.HeadSha!,
                fullCandidateRoot,
                candidateExecutable,
                candidateAssembly,
                Path.GetFullPath(runtimeRoot),
                runtimeCandidateRoot,
                packageHash,
                coordinatorHash,
                candidateConsumer,
                runtimeProtocolContract,
                Path.GetFullPath(devBridgeRoot),
                componentCommit!,
                candidateReleaseManifestPath,
                releaseManifestHash);
            WriteCandidateDescriptor(candidate);
            return new(candidate, null, null, null);
        }
        catch (OperationCanceledException)
        {
            return ToolchainCandidateMaterializationResult.Failure(
                "CANDIDATE_BUILD_CANCELLED",
                "Candidate materialization was cancelled.",
                "Retry qualification when the owning build workflow is available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return ToolchainCandidateMaterializationResult.Failure(
                exception is InvalidDataException
                    ? "DEVBRIDGE_CANDIDATE_IDENTITY_INVALID"
                    : "DEVBRIDGE_CANDIDATE_BUILD_FAILED",
                exception.Message,
                "Inspect the bounded DevBridge2 release diagnostics and repair that component before retrying.");
        }
    }
    private static string BoundDiagnostic(string? value)
    {
        const int maxLength = 4096;
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + " [truncated]";
    }

    private static async Task<(int ExitCode, string Output, string Error, bool TimedOut)> RunReleaseAsync(
        string releaseScript,
        string workingDirectory,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(releaseScript);
        process.StartInfo.ArgumentList.Add("-OutputRoot");
        process.StartInfo.ArgumentList.Add(outputRoot);
        if (!process.Start())
        {
            return (-1, string.Empty, "pwsh could not be started.", false);
        }
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput);
        Task<string> stderr = ReadBoundedAsync(process.StandardError);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (-1, stdout.Result, "DevBridge2 release workflow timed out.", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (process.ExitCode, stdout.Result, stderr.Result, false);
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int maxLength = 8192;
        char[] buffer = new char[1024];
        var output = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length < maxLength)
                output.Append(buffer, 0, Math.Min(read, maxLength - output.Length));
        }
        return output.Length == maxLength ? output + " [truncated]" : output.ToString();
    }

    private static string ResolveReleasePackage(string outputRoot)
    {
        string[] packages = Directory.EnumerateDirectories(outputRoot, "DevBridge2-*")
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packages.Length != 1)
        {
            throw new InvalidDataException("The DevBridge2 release workflow did not produce exactly one candidate package.");
        }
        return packages[0];
    }

    private static string ResolveDevBridgeSourceRoot()
    {
        foreach (string name in new[]
                 {
                     PinnedRootEnvironment,
                     PinnedRootCompatibilityEnvironment,
                     SourceRootEnvironment,
                     RuntimeRootEnvironment
                 })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(value);
        }
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        string? parent = Directory.GetParent(current)?.FullName;
        while (!string.IsNullOrWhiteSpace(parent))
        {
            string sibling = Path.Combine(parent, "DevBridge2");
            if (Directory.Exists(sibling)) return sibling;
            parent = Directory.GetParent(parent)?.FullName;
        }
        return Path.Combine(Environment.CurrentDirectory, "..", "DevBridge2");
    }

    private static bool TryReadReleaseIdentity(
        JsonElement root,
        out string? sourceCommit,
        out string? error)
    {
        sourceCommit = null;
        error = null;
        if (!TryString(root, "contract", out string? contract) ||
            !string.Equals(contract, "devbridge-release/v1", StringComparison.Ordinal) ||
            !TryString(root, "sourceRevision", out sourceCommit) ||
            !IsSha(sourceCommit) ||
            root.TryGetProperty("dirty", out JsonElement dirty) && dirty.ValueKind != JsonValueKind.False ||
            root.TryGetProperty("buildConfiguration", out JsonElement configuration) &&
                !string.Equals(configuration.GetString(), "Release", StringComparison.Ordinal))
        {
            error = "The DevBridge2 release manifest is not a clean, non-production component release.";
            return false;
        }
        return true;
    }

    private static string WriteRuntimeManifest(
        string runtimeRoot,
        JsonElement releaseManifest,
        string sourceCommit)
    {
        var entries = new List<(string Path, string Sha256)>();
        foreach (JsonElement entry in releaseManifest.GetProperty("files").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString() ?? throw new InvalidDataException("DevBridge release file path is empty.");
            string expected = entry.GetProperty("sha256").GetString() ?? throw new InvalidDataException("DevBridge release file hash is empty.");
            string fullPath = SafeChildPath(runtimeRoot, path);
            if (!File.Exists(fullPath) || !string.Equals(ToolchainFileHash.Sha256(fullPath), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The DevBridge release manifest does not match its candidate payload.");
            entries.Add((path.Replace('\\', '/'), expected.ToUpperInvariant()));
        }
        string packageHash = ComputeRuntimePackageHash(entries);
        string manifestPath = Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json");
        var manifest = new
        {
            schemaVersion = "devbridge-runtime-manifest/v1",
            ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
            componentRole = "runtime",
            productionEligible = false,
            project = "DevBridge2",
            packageId = "lan.devbridge2",
            sourceRoot = releaseManifest.TryGetProperty("sourceRoot", out JsonElement sourceRoot)
                ? sourceRoot.GetString()
                : null,
            sourceCommit,
            sourceDirty = false,
            productVersion = releaseManifest.TryGetProperty("productVersion", out JsonElement version)
                ? version.GetString()
                : null,
            packageSha256 = packageHash,
            files = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .Select(entry => new { path = entry.Path, sha256 = entry.Sha256 })
                .ToArray()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        }));
        return manifestPath;
    }

    private static string ComputeRuntimePackageHash(IEnumerable<(string Path, string Sha256)> entries)
    {
        string text = string.Join("\n", entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => entry.Path.ToLowerInvariant() + "\0" + entry.Sha256));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static bool TryReadPinnedDevBridgeRevision(
        string sourceRoot,
        out string? revision)
    {
        revision = null;
        string path = Path.Combine(sourceRoot, "contracts", "cross-stack-compatibility.json");
        if (!File.Exists(path)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement value = document.RootElement
                .GetProperty("repositories")
                .GetProperty("devBridge2")
                .GetProperty("revision");
            revision = value.GetString();
            return IsSha(revision);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
    private static string ReadRuntimePackageHash(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("packageSha256").GetString() ?? string.Empty;
    }

    private static string SafeChildPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("DevBridge release path is unsafe.");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DevBridge release path escapes its package.");
        return full;
    }

    private static void WriteCandidateDescriptor(ToolchainCandidate candidate)
    {
        string path = Path.Combine(candidate.CandidateRoot, "candidate-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(candidate, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsSha(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 40 && value.All(Uri.IsHexDigit);
}
