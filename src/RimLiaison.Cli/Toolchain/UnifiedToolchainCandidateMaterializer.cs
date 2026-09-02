using System.ComponentModel;
using System.Diagnostics;
using System.Security;
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
        string fullCandidateRoot = Path.GetFullPath(candidateRoot);
        bool ownsCandidateRoot = false;
        bool completed = false;
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

            if (Directory.Exists(fullCandidateRoot))
            {
                return Failure(
                    "CANDIDATE_DESTINATION_EXISTS",
                    "The immutable candidate destination already exists; refusing to reuse it.",
                    "Remove only the abandoned candidate through the owning workflow, then retry qualification.");
            }

            string componentRoot = Path.Combine(Path.GetFullPath(sourceRoot), "src", "DevBridgeRuntime");
            string sourcePackageRoot = Path.Combine(componentRoot, "Package");
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

            Directory.CreateDirectory(fullCandidateRoot);
            ownsCandidateRoot = true;
            string runtimeBuildRoot = Path.Combine(fullCandidateRoot, "runtime-build");
            string isolatedPackageRoot = Path.Combine(runtimeBuildRoot, "package");
            RuntimeBuildResult build = await BuildOwnedRuntimeAsync(
                    sourceRoot,
                    sourcePackageRoot,
                    isolatedPackageRoot,
                    managedAssemblies.ManagedDirectory!,
                    cancellationToken)
                .ConfigureAwait(false);
            object buildEvidence = BuildEvidence(build, managedAssemblies);
            if (!build.Succeeded)
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_BUILD_FAILED",
                    build.Error ?? "The isolated RimLiaison-owned runtime build failed.",
                    "Repair the isolated RimLiaison-owned runtime build, then retry qualification.",
                    managedAssemblies,
                    buildEvidence);
            }

            if (!ValidateRuntimePackage(isolatedPackageRoot, out string? packageError))
            {
                return Failure(
                    "RIMLIAISON_RUNTIME_PACKAGE_INVALID",
                    packageError ?? "The isolated RimLiaison-owned runtime package is incomplete.",
                    "Build a complete isolated RimLiaison-owned runtime package, then retry qualification.",
                    managedAssemblies,
                    buildEvidence);
            }

            string isolatedRuntimeManifest = Path.Combine(isolatedPackageRoot, RuntimeManifestName);
            WriteRuntimeManifest(
                isolatedPackageRoot,
                source.State.HeadSha!,
                runtimeProtocolContract,
                isolatedRuntimeManifest);
            string packageHash = ReadRuntimePackageHash(isolatedRuntimeManifest);
            string runtimeCandidateRoot = Path.Combine(fullCandidateRoot, "runtime");
            CopyDirectory(isolatedPackageRoot, runtimeCandidateRoot);
            string candidateRuntimeManifest = Path.Combine(runtimeCandidateRoot, RuntimeManifestName);
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
                    managedAssemblies,
                    buildEvidence);
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
                    managedAssemblies,
                    buildEvidence);
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
                    managedAssemblies,
                    buildEvidence);
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
                    "Build a complete isolated RimLiaison-owned runtime package, then retry qualification.",
                    managedAssemblies,
                    buildEvidence);
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
            completed = true;
            return new(candidate, null, null, null)
            {
                RimWorldManagedAssemblies = managedAssemblies,
                RuntimeBuildEvidence = buildEvidence
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
        finally
        {
            if (ownsCandidateRoot && !completed && Directory.Exists(fullCandidateRoot))
            {
                try
                {
                    Directory.Delete(fullCandidateRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
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
        RimWorldManagedAssemblyResolution? managedAssemblies = null,
        object? runtimeBuildEvidence = null)
    {
        ToolchainCandidateMaterializationResult result = ToolchainCandidateMaterializationResult.Failure(
            code,
            error,
            nextAction,
            managedAssemblies);
        return result with { RuntimeBuildEvidence = runtimeBuildEvidence };
    }

    internal sealed record RuntimeBuildResult(
        bool Succeeded,
        string ProjectPath,
        string BuildTarget,
        int? ExitCode,
        string Stdout,
        string Stderr,
        string IsolatedPackageRoot,
        string RimWorldManagedDirectory,
        string? Error)
    {
        public object ToEvidence() => new
        {
            schemaVersion = "rimliaison-runtime-build/v1",
            status = Succeeded ? "ready" : "blocked",
            projectPath = ProjectPath,
            buildTarget = BuildTarget,
            exitCode = ExitCode,
            stdout = Stdout,
            stderr = Stderr,
            isolatedPackageRoot = IsolatedPackageRoot,
            rimWorldManagedDirectory = RimWorldManagedDirectory,
            error = Error
        };
    }

    internal static async Task<RuntimeBuildResult> BuildOwnedRuntimeAsync(
        string sourceRoot,
        string sourcePackageRoot,
        string isolatedPackageRoot,
        string rimWorldManagedDirectory,
        CancellationToken cancellationToken)
    {
        string fullSourceRoot = Path.GetFullPath(sourceRoot);
        string fullSourcePackageRoot = Path.GetFullPath(sourcePackageRoot);
        string fullIsolatedPackageRoot = Path.GetFullPath(isolatedPackageRoot);
        string fullManagedDirectory = Path.GetFullPath(rimWorldManagedDirectory);
        string runtimeBuildRoot = Path.GetDirectoryName(fullIsolatedPackageRoot)!;
        string project = Path.Combine(runtimeBuildRoot, "runtime-build.proj");
        const string buildTarget = "Build";
        string[] projects =
        [
            Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "Coordinator", "DevBridge.Coordinator.csproj"),
            Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "BridgeTools", "DevBridge2.BridgeTools.csproj"),
            Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "Mod", "DevBridge2.csproj")
        ];

        foreach (string runtimeProject in projects)
        {
            if (!File.Exists(runtimeProject))
            {
                return new(
                    false,
                    project,
                    buildTarget,
                    null,
                    string.Empty,
                    string.Empty,
                    fullIsolatedPackageRoot,
                    fullManagedDirectory,
                    "Required runtime project is missing: " + runtimeProject);
            }
        }
        if (!Directory.Exists(fullManagedDirectory))
        {
            return new(
                false,
                project,
                buildTarget,
                null,
                string.Empty,
                string.Empty,
                fullIsolatedPackageRoot,
                fullManagedDirectory,
                "RimWorld managed assembly directory is missing: " + fullManagedDirectory);
        }

        try
        {
            Directory.CreateDirectory(fullIsolatedPackageRoot);
            SeedStaticPackageAssets(fullSourcePackageRoot, fullIsolatedPackageRoot);
            File.WriteAllText(project, BuildProjectFile(projects));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                false,
                project,
                buildTarget,
                null,
                string.Empty,
                string.Empty,
                fullIsolatedPackageRoot,
                fullManagedDirectory,
                exception.Message);
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = fullSourceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        string packageRootProperty = fullIsolatedPackageRoot + Path.DirectorySeparatorChar;
        string intermediateRoot = Path.Combine(runtimeBuildRoot, "obj") + Path.DirectorySeparatorChar;
        foreach (string argument in new[]
        {
            "build",
            project,
            "--configuration",
            "Release",
            "--nologo",
            "/p:DevBridgeRuntimePackageRoot=" + packageRootProperty,
            "/p:RimWorldManagedDir=" + fullManagedDirectory,
            "/p:BaseIntermediateOutputPath=" + intermediateRoot
        })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
            {
                return new(
                    false,
                    project,
                    buildTarget,
                    null,
                    string.Empty,
                    string.Empty,
                    fullIsolatedPackageRoot,
                    fullManagedDirectory,
                    "The isolated RimLiaison-owned runtime build process could not be started.");
            }
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = BoundBuildOutput(await outputTask.ConfigureAwait(false));
            string error = BoundBuildOutput(await errorTask.ConfigureAwait(false));
            return new(
                process.ExitCode == 0,
                project,
                buildTarget,
                process.ExitCode,
                output,
                error,
                fullIsolatedPackageRoot,
                fullManagedDirectory,
                process.ExitCode == 0
                    ? null
                    : "The isolated runtime project graph build failed.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Win32Exception exception)
        {
            return new(
                false,
                project,
                buildTarget,
                null,
                string.Empty,
                string.Empty,
                fullIsolatedPackageRoot,
                fullManagedDirectory,
                exception.Message);
        }
    }

    private static object BuildEvidence(
        RuntimeBuildResult build,
        RimWorldManagedAssemblyResolution managedAssemblies) => new
        {
            schemaVersion = "rimliaison-candidate-build/v1",
            runtimeBuild = build.ToEvidence(),
            rimWorldManagedAssemblies = managedAssemblies.ToEvidence()
        };

    private static void SeedStaticPackageAssets(string sourcePackageRoot, string destinationPackageRoot)
    {
        if (!Directory.Exists(sourcePackageRoot))
            throw new DirectoryNotFoundException("Runtime package source is missing: " + sourcePackageRoot);
        string[] generatedDirectories = ["Coordinator", "BridgeTools", "1.6"];
        foreach (string sourceFile in Directory.EnumerateFiles(sourcePackageRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourcePackageRoot, sourceFile);
            string topLevel = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)[0];
            if (generatedDirectories.Contains(topLevel, StringComparer.OrdinalIgnoreCase))
                continue;
            string destinationFile = Path.Combine(destinationPackageRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
        }
    }

    private static string BuildProjectFile(IEnumerable<string> projects)
    {
        StringBuilder builder = new(
            "<Project>\n" +
            "  <ItemGroup>\n");
        foreach (string project in projects)
            builder.Append("    <ProjectReference Include=\"")
                .Append(SecurityElement.Escape(project))
                .Append("\" />\n");
        return builder.Append(
            "  </ItemGroup>\n" +
            "  <Target Name=\"Build\">\n" +
            "    <MSBuild Projects=\"@(ProjectReference)\" Targets=\"Build\" BuildInParallel=\"true\" />\n" +
            "  </Target>\n" +
            "</Project>\n").ToString();
    }

    private static string BoundBuildOutput(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= MaximumBuildOutputCharacters
            ? trimmed
            : trimmed[..MaximumBuildOutputCharacters] + " [truncated]";
    }

    internal static bool ValidateRuntimePackage(string packageRoot, out string? error)
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
