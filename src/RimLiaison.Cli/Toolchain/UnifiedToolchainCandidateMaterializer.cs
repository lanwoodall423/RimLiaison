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
    private static readonly string[] RequiredRepositoryRecipeFiles =
    [
        "quicktest-smoke.json",
        "knowledge-framework-development-smoke.json",
        "live-stack-smoke.json",
        "live-stack-diagnostic.json",
        "aquaculture-development-smoke.json",
        "horticulture-in-game-suite.json",
        "wildlife-in-game-suite.json"
    ];

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
            string candidateMod = Path.Combine(runtimeCandidateRoot, "1.7", "Assemblies", "DevBridge2.dll");

            string cliDeploymentRoot = Path.Combine(fullCandidateRoot, "cli");
            CliPublishResult cliPublish = await PublishCliAsync(
                    sourceRoot,
                    cliDeploymentRoot,
                    source.State.HeadSha!,
                    cancellationToken)
                .ConfigureAwait(false);
            object completeBuildEvidence = BuildEvidence(
                build,
                managedAssemblies,
                cliPublish);
            if (!cliPublish.Succeeded)
            {
                return Failure(
                    cliPublish.ErrorCode ?? "RIMLIAISON_CLI_PUBLISH_FAILED",
                    cliPublish.Error ?? "The RimLiaison CLI source build and publish failed.",
                    "Build and publish the complete RimLiaison CLI deployment from the qualified source, then retry qualification.",
                    managedAssemblies,
                    completeBuildEvidence);
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
                    "RIMLIAISON_CLI_PUBLISH_INCOMPLETE",
                    "The source-built RimLiaison CLI deployment is missing its executable, assembly, or manifest entries.",
                    "Publish a complete source-built RimLiaison CLI deployment, then retry qualification.",
                    managedAssemblies,
                    completeBuildEvidence);
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
                    cliManifestError ?? "The source-built RimLiaison CLI deployment failed complete closure verification.",
                    "Publish a complete source-built RimLiaison CLI deployment, then retry qualification.",
                    managedAssemblies,
                    completeBuildEvidence);
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
                    completeBuildEvidence);
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
                RuntimeBuildEvidence = completeBuildEvidence
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

    internal sealed record CliProcessEvidence(
        string[] Arguments,
        int? ExitCode,
        string Stdout,
        string Stderr,
        string? StartError)
    {
        public object ToEvidence() => new
        {
            command = "dotnet " + string.Join(" ", Arguments.Select(QuoteCommandArgument)),
            arguments = Arguments,
            exitCode = ExitCode,
            stdout = Stdout,
            stderr = Stderr,
            startError = StartError
        };

        private static string QuoteCommandArgument(string argument) =>
            argument.IndexOfAny([' ', '\t', '"']) < 0
                ? argument
                : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    internal sealed record CliPublishResult(
        bool Succeeded,
        string SourceRevision,
        string ProjectPath,
        string GlobalJsonPath,
        string SelectedSdk,
        string IntermediateRoot,
        string IsolationPropertiesPath,
        string StagingPath,
        IReadOnlyList<CliProcessEvidence> Commands,
        string? ErrorCode,
        string? Error)
    {
        public object ToEvidence() => new
        {
            schemaVersion = "rimliaison-cli-build/v1",
            status = Succeeded ? "ready" : "blocked",
            sourceRevision = SourceRevision,
            projectPath = ProjectPath,
            globalJsonPath = GlobalJsonPath,
            selectedSdk = SelectedSdk,
            intermediateRoot = IntermediateRoot,
            isolationPropertiesPath = IsolationPropertiesPath,
            stagingPath = StagingPath,
            commands = Commands.Select(command => command.ToEvidence()).ToArray(),
            errorCode = ErrorCode,
            error = Error
        };
    }

    internal static async Task<CliPublishResult> PublishCliAsync(
        string sourceRoot,
        string destination,
        string sourceRevision,
        CancellationToken cancellationToken)
    {
        string fullSourceRoot = Path.GetFullPath(sourceRoot);
        string project = Path.Combine(fullSourceRoot, "src", "RimLiaison.Cli", "RimLiaison.Cli.csproj");
        string globalJson = Path.Combine(fullSourceRoot, "global.json");
        string fullDestination = Path.GetFullPath(destination);
        string cliBuildRoot = Path.Combine(Path.GetDirectoryName(fullDestination)!, "cli-build");
        string intermediateRoot = Path.Combine(cliBuildRoot, "obj");
        string isolationPropertiesPath = Path.Combine(cliBuildRoot, "Directory.Build.props");
        List<CliProcessEvidence> commands = [];

        CliPublishResult Failure(string code, string error, string selectedSdk = "") =>
            new(
                false,
                sourceRevision,
                project,
                globalJson,
                selectedSdk,
                intermediateRoot,
                isolationPropertiesPath,
                fullDestination,
                commands,
                code,
                error);

        if (!File.Exists(globalJson))
            return Failure(
                "RIMLIAISON_CLI_SDK_CONFIGURATION_MISSING",
                "The qualified RimLiaison source checkout does not contain its pinned global.json.");
        if (!File.Exists(project))
            return Failure(
                "RIMLIAISON_CLI_PROJECT_MISSING",
                "The RimLiaison CLI project is missing from the qualified source checkout.");

        Directory.CreateDirectory(fullDestination);
        Directory.CreateDirectory(cliBuildRoot);
        try
        {
            WriteCliIsolationProps(isolationPropertiesPath, cliBuildRoot);
        }
        catch (IOException exception)
        {
            return Failure(
                "RIMLIAISON_CLI_BUILD_SETUP_FAILED",
                "The candidate-owned CLI isolation properties could not be written: " + exception.Message);
        }
        string[] isolatedProperties =
        [
            "/p:CustomBeforeDirectoryBuildProps=" + isolationPropertiesPath
        ];

        CliProcessEvidence sdk = await RunDotnetAsync(
                fullSourceRoot,
                ["--version"],
                cancellationToken)
            .ConfigureAwait(false);
        commands.Add(sdk);
        string selectedSdk = sdk.Stdout.Trim();
        if (sdk.ExitCode != 0)
        {
            return Failure(
                "RIMLIAISON_CLI_SDK_RESOLUTION_FAILED",
                DescribeProcessFailure(sdk, "The pinned RimLiaison SDK could not be selected."),
                selectedSdk);
        }

        CliProcessEvidence restore = await RunDotnetAsync(
                fullSourceRoot,
                ["restore", project, "--nologo", ..isolatedProperties],
                cancellationToken)
            .ConfigureAwait(false);
        commands.Add(restore);
        if (restore.ExitCode != 0)
        {
            return Failure(
                "RIMLIAISON_CLI_RESTORE_FAILED",
                DescribeProcessFailure(restore, "The RimLiaison CLI restore failed."),
                selectedSdk);
        }

        CliProcessEvidence publish = await RunDotnetAsync(
                fullSourceRoot,
                [
                    "publish",
                    project,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--no-self-contained",
                    "--output",
                    fullDestination,
                    "--nologo",
                    ..isolatedProperties
                ],
                cancellationToken)
            .ConfigureAwait(false);
        commands.Add(publish);
        if (publish.ExitCode != 0)
        {
            return Failure(
                ClassifyCliPublishFailure(publish),
                DescribeProcessFailure(publish, "The RimLiaison CLI source build and publish failed."),
                selectedSdk);
        }

        string[] requiredFiles =
        [
            "rimliaison.exe",
            "rimliaison.dll",
            "rimliaison.deps.json",
            "rimliaison.runtimeconfig.json"
        ];
        string? missing = requiredFiles
            .Select(file => Path.Combine(fullDestination, file))
            .FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
        {
            return Failure(
                "RIMLIAISON_CLI_PUBLISH_INCOMPLETE",
                "The source-built RimLiaison CLI publish is missing " + Path.GetFileName(missing) + ".",
                selectedSdk);
        }

        return new(
            true,
            sourceRevision,
            project,
            globalJson,
            selectedSdk,
            intermediateRoot,
            isolationPropertiesPath,
            fullDestination,
            commands,
            null,
            null);
    }

    private static void WriteCliIsolationProps(
        string propertiesPath,
        string cliBuildRoot)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(cliBuildRoot));
        string escapedRoot = SecurityElement.Escape(root) ?? root;
        File.WriteAllText(
            propertiesPath,
            "<Project>" + Environment.NewLine +
            "  <PropertyGroup>" + Environment.NewLine +
            $"    <BaseIntermediateOutputPath>{escapedRoot}obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>{Environment.NewLine}" +
            $"    <MSBuildProjectExtensionsPath>{escapedRoot}obj/$(MSBuildProjectName)/</MSBuildProjectExtensionsPath>{Environment.NewLine}" +
            $"    <BaseOutputPath>{escapedRoot}bin/$(MSBuildProjectName)/</BaseOutputPath>{Environment.NewLine}" +
            "    <DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)\\obj\\**;$(MSBuildProjectDirectory)\\bin\\**</DefaultItemExcludes>" + Environment.NewLine +
            "  </PropertyGroup>" + Environment.NewLine +
            "</Project>" + Environment.NewLine);
    }

    private static async Task<CliProcessEvidence> RunDotnetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start())
            {
                return new(
                    arguments.ToArray(),
                    null,
                    string.Empty,
                    string.Empty,
                    "The dotnet process could not be started.");
            }
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                arguments.ToArray(),
                process.ExitCode,
                BoundBuildOutput(await outputTask.ConfigureAwait(false)),
                BoundBuildOutput(await errorTask.ConfigureAwait(false)),
                null);
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
                arguments.ToArray(),
                null,
                string.Empty,
                string.Empty,
                exception.Message);
        }
    }

    private static string ClassifyCliPublishFailure(CliProcessEvidence process)
    {
        string combined = process.Stdout + "\n" + process.Stderr;
        return combined.Contains("MSB3030", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("publish", StringComparison.OrdinalIgnoreCase)
            ? "RIMLIAISON_CLI_PUBLISH_FAILED"
            : "RIMLIAISON_CLI_BUILD_FAILED";
    }

    private static string DescribeProcessFailure(CliProcessEvidence process, string fallback)
    {
        string output = string.Join(
            Environment.NewLine,
            new[] { process.StartError, process.Stderr, process.Stdout }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(output) ? fallback : output;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

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
        string? Error,
        string? ErrorCode)
    {
        public object ToEvidence() => new
        {
            schemaVersion = "rimliaison-runtime-build/v1",
            status = Succeeded ? "ready" : "blocked",
            phase = "restore+build",
            projectPath = ProjectPath,
            buildTarget = BuildTarget,
            exitCode = ExitCode,
            stdout = Stdout,
            stderr = Stderr,
            isolatedPackageRoot = IsolatedPackageRoot,
            rimWorldManagedDirectory = RimWorldManagedDirectory,
            errorCode = ErrorCode,
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
        using Semaphore buildGate = new(1, 1, @"Global\RimLiaison.RuntimeCandidateBuild");
        bool lockHeld = buildGate.WaitOne(TimeSpan.FromMinutes(10));
        if (!lockHeld)
        {
            string project = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(isolatedPackageRoot))!,
                "runtime-build.proj");
            return new(
                false,
                project,
                "Restore -> Build",
                null,
                string.Empty,
                string.Empty,
                Path.GetFullPath(isolatedPackageRoot),
                Path.GetFullPath(rimWorldManagedDirectory),
                "The isolated runtime build lock could not be acquired within 10 minutes.",
                "RIMLIAISON_RUNTIME_BUILD_LOCK_TIMEOUT");
        }
        try
        {
            return await BuildOwnedRuntimeCoreAsync(
                    sourceRoot,
                    sourcePackageRoot,
                    isolatedPackageRoot,
                    rimWorldManagedDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            buildGate.Release();
        }
    }

    private static async Task<RuntimeBuildResult> BuildOwnedRuntimeCoreAsync(
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
        const string buildTarget = "Restore -> Build";
        (string Project, string IntermediateSuffix, bool BuildProjectReferences)[] projects =
        [
            (
                Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "Coordinator.Core", "DevBridge.Coordinator.Core.csproj"),
                "Coordinator",
                true),
            (
                Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "BridgeTools", "DevBridge2.BridgeTools.csproj"),
                "BridgeTools",
                true),
            (
                Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "Mod", "DevBridge2.csproj"),
                "Mod",
                true),
            (
                Path.Combine(fullSourceRoot, "src", "DevBridgeRuntime", "Coordinator", "DevBridge.Coordinator.csproj"),
                "Coordinator",
                false)
        ];

        foreach ((string runtimeProject, _, _) in projects)
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
                    "Required runtime project is missing: " + runtimeProject,
                    "RIMLIAISON_RUNTIME_PROJECT_MISSING");
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
                "RimWorld managed assembly directory is missing: " + fullManagedDirectory,
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING");
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
                exception.Message,
                "RIMLIAISON_RUNTIME_BUILD_SETUP_FAILED");
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
        string packageRootProperty = fullIsolatedPackageRoot.Replace('\\', '/') + "/";
        string intermediateRoot = Path.Combine(runtimeBuildRoot, "obj").Replace('\\', '/') + "/";
        foreach (string argument in new[]
        {
            "build",
            project,
            "/t:Build",
            "--configuration",
            "Release",
            "--nologo",
            "/p:DevBridgeRuntimePackageRoot=" + packageRootProperty,
            "/p:RimWorldManagedDir=" + fullManagedDirectory,
            "/p:BaseIntermediateOutputPath=" + intermediateRoot,
            "/p:DevBridgeRuntimeIsolatedBuild=true",
            "/nr:false"
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
                    "The isolated RimLiaison-owned runtime build process could not be started.",
                    "RIMLIAISON_RUNTIME_BUILD_START_FAILED");
            }
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = BoundBuildOutput(await outputTask.ConfigureAwait(false));
            string error = BoundBuildOutput(await errorTask.ConfigureAwait(false));
            int exitCode = process.ExitCode;
            string? errorCode = exitCode == 0
                ? null
                : ClassifyRuntimeBuildFailure(output, error);
            return new(
                exitCode == 0,
                project,
                buildTarget,
                exitCode,
                output,
                error,
                fullIsolatedPackageRoot,
                fullManagedDirectory,
                errorCode is null
                    ? null
                    : "The isolated runtime project graph build failed.",
                errorCode);
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
                exception.Message,
                "RIMLIAISON_RUNTIME_BUILD_START_FAILED");
        }
    }

    private static object BuildEvidence(
        RuntimeBuildResult build,
        RimWorldManagedAssemblyResolution managedAssemblies,
        CliPublishResult? cliPublish = null) => new
        {
            schemaVersion = "rimliaison-candidate-build/v1",
            runtimeBuild = build.ToEvidence(),
            cliBuild = cliPublish?.ToEvidence(),
            rimWorldManagedAssemblies = managedAssemblies.ToEvidence()
        };

    private static void SeedStaticPackageAssets(string sourcePackageRoot, string destinationPackageRoot)
    {
        if (!Directory.Exists(sourcePackageRoot))
            throw new DirectoryNotFoundException("Runtime package source is missing: " + sourcePackageRoot);
        string recipeRoot = Path.Combine(sourcePackageRoot, "TestRecipes");
        if (!Directory.Exists(recipeRoot))
            throw new DirectoryNotFoundException("Runtime package TestRecipes source is missing: " + recipeRoot);
        foreach (string requiredRecipe in RequiredRepositoryRecipeFiles)
        {
            if (!File.Exists(Path.Combine(recipeRoot, requiredRecipe)))
                throw new FileNotFoundException(
                    "Runtime package builtin recipe source is missing: " + requiredRecipe,
                    Path.Combine(recipeRoot, requiredRecipe));
        }
        string[] generatedDirectories = ["Coordinator", "1.6", "1.7"];
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

    private static string BuildProjectFile(
        IEnumerable<(string Project, string IntermediateSuffix, bool BuildProjectReferences)> projects)
    {
        StringBuilder builder = new("<Project>\n");
        string commonProperties =
            "DevBridgeRuntimePackageRoot=$(DevBridgeRuntimePackageRoot);" +
            "RimWorldManagedDir=$(RimWorldManagedDir);" +
            "DevBridgeRuntimeIsolatedBuild=$(DevBridgeRuntimeIsolatedBuild);" +
            "Configuration=$(Configuration);";
        builder.Append("  <Target Name=\"Restore\">\n");
        foreach ((string project, string suffix, _) in projects)
        {
            string properties = commonProperties +
                "BaseIntermediateOutputPath=$(BaseIntermediateOutputPath)" + suffix + "/";
            builder.Append("    <MSBuild Projects=\"")
                .Append(SecurityElement.Escape(project))
                .Append("\" Targets=\"Restore\" BuildInParallel=\"false\" Properties=\"")
                .Append(SecurityElement.Escape(properties))
                .Append("\" />\n");
        }
        builder.Append("  </Target>\n");
        builder.Append("  <Target Name=\"Build\" DependsOnTargets=\"Restore\">\n");
        foreach ((string project, string suffix, bool buildProjectReferences) in projects)
        {
            string properties = commonProperties +
                "BaseIntermediateOutputPath=$(BaseIntermediateOutputPath)" + suffix + "/";
            if (!buildProjectReferences)
                properties += ";BuildProjectReferences=false";
            builder.Append("    <MSBuild Projects=\"")
                .Append(SecurityElement.Escape(project))
                .Append("\" Targets=\"Build\" BuildInParallel=\"false\" Properties=\"")
                .Append(SecurityElement.Escape(properties))
                .Append("\" />\n");
        }
        return builder.Append(
            "  </Target>\n" +
            "</Project>\n").ToString();
    }

    private static string ClassifyRuntimeBuildFailure(string stdout, string stderr)
    {
        string combined = stdout + "\n" + stderr;
        return combined.Contains("Microsoft.NETFramework.ReferenceAssemblies.net472",
                StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("MSB3644", StringComparison.OrdinalIgnoreCase)
            ? "RIMLIAISON_RUNTIME_REFERENCE_ASSEMBLIES_UNAVAILABLE"
            : "RIMLIAISON_RUNTIME_BUILD_FAILED";
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
            Path.Combine("1.7", "BridgeTools", "DevBridge2.BridgeTools.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.exe"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.Core.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.runtimeconfig.json"),
            Path.Combine("1.7", "Assemblies", "DevBridge2.dll"),
            ..RequiredRepositoryRecipeFiles.Select(file => Path.Combine("TestRecipes", file))
        ];
        string obsoleteRoot = Path.Combine(packageRoot, "BridgeTools");
        if (Directory.Exists(obsoleteRoot) &&
            Directory.EnumerateFiles(obsoleteRoot, "*", SearchOption.AllDirectories).Any())
        {
            error = "Obsolete root-level BridgeTools output is not allowed: BridgeTools/DevBridge2.BridgeTools.dll.";
            return false;
        }
        try
        {
            foreach (string requiredFile in requiredFiles)
            {
                if (!File.Exists(Path.Combine(packageRoot, requiredFile)))
                {
                    error = "Runtime package is missing required file: " + requiredFile;
                    return false;
                }
            }
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
