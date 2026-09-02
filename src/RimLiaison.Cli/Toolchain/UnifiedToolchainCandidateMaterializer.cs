using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimLiaison.Git;

namespace RimLiaison.Toolchain;

internal static class ToolchainCandidateMaterializer
{
    private const string RuntimeManifestName = ".devbridge-runtime-manifest.json";
    private const string RuntimePackageSchema = "devbridge-runtime-manifest/v1";
    private const string RuntimePackageId = "lan.devbridge2";
    private const string ConsumerRelativePath = "transaction-components/mod-test.ps1";
    private const int MaximumBuildOutputCharacters = 16 * 1024;

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
                return Failure(
                    "CANDIDATE_SOURCE_UNRESOLVED",
                    "The RimLiaison source identity could not be resolved before candidate creation.",
                    "Resolve the RimLiaison source checkout, then retry qualification.");
            }
            if (RepositoryChangeClassificationPolicy.HasMeaningfulChanges(source.State.Changes))
            {
                return Failure(
                    "CANDIDATE_SOURCE_DIRTY",
                    "Candidate creation refuses meaningful changes in the RimLiaison source checkout.",
                    "Commit or restore the source checkout, then retry qualification.");
            }

            string fullCandidateRoot = Path.GetFullPath(candidateRoot);
            if (Directory.Exists(fullCandidateRoot))
            {
                return Failure(
                    "CANDIDATE_DESTINATION_EXISTS",
                    "The immutable candidate destination already exists; refusing to reuse it.",
                    "Remove only the abandoned candidate through the owning workflow, then retry qualification.");
            }

            string componentRoot = Path.Combine(Path.GetFullPath(sourceRoot), "src", "DevBridgeRuntime");
            string packageRoot = Path.Combine(componentRoot, "Package");
            string consumerSource = Path.Combine(componentRoot, "Transaction", "mod-test.ps1");
            if (!Directory.Exists(componentRoot) || !File.Exists(consumerSource))
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_COMPONENT_MISSING",
                    "The RimLiaison-owned DevBridge runtime component or transaction consumer is missing.",
                    "Restore src/DevBridgeRuntime and retry qualification.");
            }

            RimWorldManagedAssemblyResolution managedAssemblies =
                RimWorldManagedAssemblyResolver.Resolve(sourceRoot, componentRoot);
            if (!managedAssemblies.Succeeded)
            {
                return Failure(
                    managedAssemblies.ErrorCode ?? "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                    managedAssemblies.Error ?? "The RimWorld managed assemblies are unavailable.",
                    managedAssemblies.NextAction ?? "Repair the RimWorld managed assembly installation, then retry qualification.",
                    managedAssemblies);
            }

            (bool built, string buildError) = await BuildOwnedRuntimeAsync(
                    sourceRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!built)
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_BUILD_FAILED",
                    buildError,
                    "Repair the RimLiaison-owned runtime build, then retry qualification.",
                    managedAssemblies);
            }

            if (!ValidateRuntimePackage(packageRoot, out string? packageError))
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_PACKAGE_INVALID",
                    packageError ?? "The RimLiaison-owned runtime package is incomplete.",
                    "Build a complete RimLiaison-owned runtime package, then retry qualification.",
                    managedAssemblies);
            }

            Directory.CreateDirectory(fullCandidateRoot);
            string runtimeCandidateRoot = Path.Combine(fullCandidateRoot, "runtime");
            CopyDirectory(packageRoot, runtimeCandidateRoot);
            string candidateRuntimeManifest = Path.Combine(runtimeCandidateRoot, RuntimeManifestName);
            WriteRuntimeManifest(runtimeCandidateRoot, source.State.HeadSha!, runtimeProtocolContract,
                candidateRuntimeManifest);
            string packageHash = ReadRuntimePackageHash(candidateRuntimeManifest);
            string candidateCoordinator = Path.Combine(runtimeCandidateRoot, "Coordinator", "DevBridge.Coordinator.exe");
            string candidateMod = Path.Combine(runtimeCandidateRoot, "1.6", "Assemblies", "DevBridge2.dll");
            string cliDeploymentRoot = Path.Combine(fullCandidateRoot, "cli");
            (bool published, string publishError) = await PublishCliAsync(
                    sourceRoot,
                    cliDeploymentRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!published)
            {
                return Failure(
                    "RIMLIAISON_CLI_PUBLISH_FAILED",
                    publishError,
                    "Publish the complete RimLiaison CLI deployment, then retry qualification.",
                    managedAssemblies);
            }
            CliDeploymentManifest cliManifest = CliDeploymentManifestService.Write(
                cliDeploymentRoot,
                source.State.HeadSha!,
                "net8.0");
            string cliManifestPath = Path.Combine(cliDeploymentRoot, CliDeploymentManifestService.FileName);
            string candidateExecutable = Path.Combine(cliDeploymentRoot, "rimliaison.exe");
            string candidateAssembly = Path.Combine(cliDeploymentRoot, "rimliaison.dll");
            if (!File.Exists(candidateExecutable) || !File.Exists(candidateAssembly) ||
                !CliDeploymentManifestService.ContainsFile(cliManifest, "rimliaison.exe") ||
                !CliDeploymentManifestService.ContainsFile(cliManifest, "rimliaison.dll"))
            {
                return Failure(
                    "RIMLIAISON_CLI_PUBLISH_INVALID",
                    "The published RimLiaison CLI deployment is missing its executable, assembly, or manifest entries.",
                    "Publish a complete RimLiaison CLI deployment, then retry qualification.",
                    managedAssemblies);
            }
            string? cliManifestError = null;
            if (!CliDeploymentManifestService.Verify(
                    cliDeploymentRoot,
                    cliManifestPath,
                    ToolchainFileHash.Sha256(cliManifestPath),
                    cliManifest.PackageSha256,
                    out _,
                    out cliManifestError))
            {
                return Failure(
                    "RIMLIAISON_CLI_DEPLOYMENT_INVALID",
                    cliManifestError ?? "The published RimLiaison CLI deployment failed complete closure verification.",
                    "Publish a complete RimLiaison CLI deployment, then retry qualification.",
                    managedAssemblies);
            }

            string candidateConsumer = Path.Combine(fullCandidateRoot, ConsumerRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(candidateConsumer)!);
            File.Copy(consumerSource, candidateConsumer, overwrite: false);

            if (!File.Exists(candidateRuntimeManifest) || !File.Exists(candidateCoordinator) ||
                !File.Exists(candidateMod))
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_PACKAGE_INVALID",
                    "The copied RimLiaison-owned runtime package is incomplete.",
                    "Build a complete RimLiaison-owned runtime package, then retry qualification.",
                    managedAssemblies);
            }
            ToolchainCandidate candidate = new(
                source.State.HeadSha!,
                fullCandidateRoot,
                candidateExecutable,
                candidateAssembly,
                Path.GetFullPath(runtimeRoot),
                runtimeCandidateRoot,
                packageHash,
                ToolchainFileHash.Sha256(candidateCoordinator),
                candidateConsumer,
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                managedAssemblies.RimWorldRoot,
                managedAssemblies.ManagedDirectory)
            {
                RimLiaisonCliDeploymentRoot = cliDeploymentRoot,
                RimLiaisonCliDeploymentManifestPath = cliManifestPath,
                RimLiaisonCliDeploymentManifestSha256 = ToolchainFileHash.Sha256(cliManifestPath),
                RimLiaisonCliDeploymentPackageSha256 = cliManifest.PackageSha256,
                RimLiaisonCliTargetFramework = cliManifest.TargetFramework,
                DevBridgeModSha256 = ToolchainFileHash.Sha256(candidateMod),
                DevBridgeRuntimeManifestSha256 = ToolchainFileHash.Sha256(candidateRuntimeManifest)
            };
            WriteCandidateDescriptor(candidate);
            return new(candidate, null, null, null)
            {
                RimWorldManagedAssemblies = managedAssemblies
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(
                "CANDIDATE_BUILD_CANCELLED",
                "Candidate materialization was cancelled.",
                "Retry qualification when the owning build workflow is available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or
            Win32Exception)
        {
            return Failure(
                exception is InvalidDataException
                    ? "RIMLIAISON_RUNTIME_PACKAGE_INVALID"
                    : "RIMLIAISON_RUNTIME_BUILD_FAILED",
                exception.Message,
                "Inspect the RimLiaison-owned runtime build diagnostics, then retry qualification.");
        }
    }

    private static async Task<(bool Succeeded, string Error)> PublishCliAsync(
        string sourceRoot,
        string destination,
        CancellationToken cancellationToken)
    {
        string project = Path.Combine(Path.GetFullPath(sourceRoot), "src", "RimLiaison.Cli", "RimLiaison.Cli.csproj");
        if (!File.Exists(project))
            return (false, "The RimLiaison CLI project is missing from the qualified source checkout.");
        Directory.CreateDirectory(destination);
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetFullPath(sourceRoot),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (string argument in new[]
        {
            "publish", project, "--configuration", "Release", "--no-build", "--no-restore",
            "--no-self-contained", "--output", Path.GetFullPath(destination), "--nologo"
        })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start())
                return (false, "The RimLiaison CLI publish process could not be started.");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = BoundBuildOutput(await outputTask.ConfigureAwait(false));
            string error = BoundBuildOutput(await errorTask.ConfigureAwait(false));
            if (process.ExitCode == 0)
                return (true, string.Empty);
            return (false, string.Join(" ", new[] { error, output }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Win32Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static ToolchainCandidateMaterializationResult Failure(
        string code,
        string error,
        string nextAction,
        RimWorldManagedAssemblyResolution? managedAssemblies = null) =>
        ToolchainCandidateMaterializationResult.Failure(
            code,
            error,
            nextAction,
            managedAssemblies);

    private static async Task<(bool Succeeded, string Error)> BuildOwnedRuntimeAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        string solution = Path.Combine(Path.GetFullPath(sourceRoot), "RimLiaison.sln");
        if (!File.Exists(solution))
            return (false, "The RimLiaison solution is missing from the qualified source checkout.");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetFullPath(sourceRoot),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (string argument in new[] { "build", solution, "--configuration", "Release", "--nologo" })
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            return (false, "The RimLiaison-owned runtime build process could not be started.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        string output = BoundBuildOutput(await outputTask.ConfigureAwait(false));
        string error = BoundBuildOutput(await errorTask.ConfigureAwait(false));
        if (process.ExitCode == 0)
            return (true, string.Empty);
        return (false, string.Join(" ", new[] { error, output }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string BoundBuildOutput(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= MaximumBuildOutputCharacters
            ? trimmed
            : trimmed[..MaximumBuildOutputCharacters] + " [truncated]";
    }

    private static bool ValidateRuntimePackage(string packageRoot, out string? error)
    {
        error = null;
        string[] requiredFiles =
        [
            "DevBridge.cmd",
            Path.Combine("About", "About.xml"),
            "LoadFolders.xml",
            "RimBridgeProtocolCompatibility.json",
            Path.Combine("BridgeTools", "DevBridge2.BridgeTools.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.exe"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.Core.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.runtimeconfig.json"),
            Path.Combine("1.6", "Assemblies", "DevBridge2.dll")
        ];
        foreach (string relativePath in requiredFiles)
        {
            if (!File.Exists(Path.Combine(packageRoot, relativePath)))
            {
                error = "Required runtime package file is missing: " + relativePath;
                return false;
            }
        }

        try
        {
            string about = File.ReadAllText(Path.Combine(packageRoot, "About", "About.xml"));
            Match packageId = Regex.Match(about, "<packageId>([^<]+)</packageId>", RegexOptions.CultureInvariant);
            if (!packageId.Success || !string.Equals(packageId.Groups[1].Value.Trim(), RuntimePackageId,
                    StringComparison.Ordinal))
            {
                error = "About.xml does not identify the expected lan.devbridge2 package.";
                return false;
            }
            using JsonDocument protocol = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot,
                "RimBridgeProtocolCompatibility.json")));
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            error = "Runtime package metadata is invalid: " + exception.Message;
            return false;
        }
    }

    private static void WriteRuntimeManifest(
        string packageRoot,
        string sourceCommit,
        string runtimeProtocolContract,
        string manifestPath)
    {
        string staleFrameworkOutput = Path.Combine(packageRoot, "Coordinator", "net8.0");
        if (Directory.Exists(staleFrameworkOutput))
            Directory.Delete(staleFrameworkOutput, recursive: true);

        var entries = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath),
                StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                Sha256: ToolchainFileHash.Sha256(path)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidDataException("The runtime package contains no files.");

        string packageHash = ComputePackageHash(entries);
        var manifest = new
        {
            schemaVersion = RuntimePackageSchema,
            ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
            componentRole = "DevBridge runtime",
            productionEligible = false,
            project = ToolchainPromotionSchemas.OwnerProduct,
            packageId = RuntimePackageId,
            sourceCommit,
            sourceRoot = (string?)null,
            sourceDirty = false,
            runtimeProtocolContract,
            packageSha256 = packageHash,
            files = entries.Select(entry => new { path = entry.Path, sha256 = entry.Sha256 }).ToArray()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        }));
    }

    private static string ReadRuntimePackageHash(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("packageSha256").GetString() ?? string.Empty;
    }

    private static string ComputePackageHash(IEnumerable<(string Path, string Sha256)> entries)
    {
        string text = string.Join("\n", entries.Select(entry =>
            entry.Path.ToLowerInvariant() + "\0" + entry.Sha256));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static void WriteCandidateDescriptor(ToolchainCandidate candidate)
    {
        File.WriteAllText(
            Path.Combine(candidate.CandidateRoot, "candidate-manifest.json"),
            JsonSerializer.Serialize(candidate, new JsonSerializerOptions
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
}
