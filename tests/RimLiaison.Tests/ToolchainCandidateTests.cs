using System.Diagnostics;
using System.Text.Json;
using RimLiaison.RimDev;
using RimLiaison.Stack;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class ToolchainCandidateTests
{
    public static void IsolatedDevBridgeCheckoutUsesExplicitManagedPath()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        RimWorldManagedAssemblyResolution result = environment.Resolve(
            Path.Combine(environment.Root, "isolated", "pinned", "DevBridge2"));

        Assert(result.Succeeded, $"{result.Error} root={result.RimWorldRoot} managed={result.ManagedDirectory} expected={environment.ManagedDirectory}");
        Assert(result.ManagedDirectory == environment.ManagedDirectory, "isolated checkout must use the configured managed directory");
        Assert(result.OldCheckoutRelativePath != result.ManagedDirectory, "isolated checkout must not depend on its relative fallback");
    }

    public static void NormalCheckoutUnderModsUsesExplicitManagedPath()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        RimWorldManagedAssemblyResolution result = environment.Resolve(
            Path.Combine(environment.RimWorldRoot, "Mods", "DevBridge2"));

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "resolution failed");
        Assert(result.ManagedDirectory == environment.ManagedDirectory, "normal checkout must still receive the explicit managed directory");
    }

    public static void ValidRimWorldRootAllowsBuildToProceed()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "valid managed directory was rejected");
        Assert(result.MissingRequiredFile is null, "valid managed directory must have no missing assembly");
    }

    public static void MissingAssemblyCSharpFailsBeforeBuild()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        File.Delete(Path.Combine(environment.ManagedDirectory, "Assembly-CSharp.dll"));

        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);

        Assert(!result.Succeeded, "missing Assembly-CSharp.dll must block the candidate build");
        Assert(result.ErrorCode == "RIMWORLD_MANAGED_ASSEMBLIES_MISSING", "missing Assembly-CSharp.dll returned the wrong code");
        Assert(result.MissingRequiredFile == Path.Combine(environment.ManagedDirectory, "Assembly-CSharp.dll"), "missing file evidence is incomplete");
        Assert(result.ToEvidence() is not null, "missing prerequisite must have structured evidence");
    }

    public static void MissingUnityCoreModuleFailsBeforeBuild()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        File.Delete(Path.Combine(environment.ManagedDirectory, "UnityEngine.CoreModule.dll"));

        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);

        Assert(!result.Succeeded, "missing UnityEngine.CoreModule.dll must block the candidate build");
        Assert(result.ErrorCode == "RIMWORLD_MANAGED_ASSEMBLIES_MISSING", "missing UnityEngine.CoreModule.dll returned the wrong code");
        Assert(result.MissingRequiredFile == Path.Combine(environment.ManagedDirectory, "UnityEngine.CoreModule.dll"), "missing file evidence is incomplete");
    }

    public static void WrongConfiguredRootFailsClearly()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string wrongRoot = Path.Combine(environment.Root, "not-a-rimworld-installation");
        environment.WriteWorkspace(wrongRoot);

        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);

        Assert(!result.Succeeded, "a stale RimWorld root must block the candidate build");
        Assert(result.ErrorCode == "RIMWORLD_MANAGED_ASSEMBLIES_MISSING", "stale root returned the wrong code");
        Assert(result.RimWorldRoot == Path.GetFullPath(wrongRoot), "stale root evidence is missing");
        Assert(result.NextAction is not null, "stale root must provide an actionable next action");
    }

    public static void CurrentRimWorldInstallationResolvesToManagedDirectory()
    {
        const string root = @"C:\Games\Steam\steamapps\common\RimWorld";
        if (!Directory.Exists(root))
        {
            return;
        }

        using TestEnvironment environment = TestEnvironment.Create(root);
        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "current RimWorld installation did not resolve");
        Assert(result.RimWorldRoot == Path.GetFullPath(root), "current RimWorld root resolved incorrectly");
        Assert(result.ManagedDirectory == Path.Combine(Path.GetFullPath(root), "RimWorldWin64_Data", "Managed"), "current managed directory resolved incorrectly");
    }

    public static void FirstInstallQualificationResolvesCanonicalDestination()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Directory.CreateDirectory(Path.Combine(environment.RimWorldRoot, "Mods"));
        string manifest = Path.Combine(environment.Root, "missing-production-toolchain.json");
        var result = environment.ResolveQualificationDestination(manifest);

        Assert(result.Ok, result.Error ?? "the clean first-install destination was rejected");
        Assert(result.RuntimeRoot == Path.Combine(environment.RimWorldRoot, "Mods", "DevBridge2"),
            "first-install runtime destination diverged from the configured canonical Mods root");
        Assert(result.Contract == ToolchainPromotionSchemas.RuntimeProtocolContract,
            "first-install qualification selected a different runtime protocol");
        Assert(!File.Exists(manifest), "qualification wrote a production toolchain manifest");
    }

    public static void FirstInstallQualificationRejectsInvalidExistingManifest()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Directory.CreateDirectory(Path.Combine(environment.RimWorldRoot, "Mods"));
        string manifest = Path.Combine(environment.Root, "production-toolchain.json");
        File.WriteAllText(manifest, "{invalid JSON");
        var result = environment.ResolveQualificationDestination(manifest);

        Assert(!result.Ok, "invalid existing production manifest was treated as first installation");
        Assert(File.ReadAllText(manifest) == "{invalid JSON",
            "qualification changed an invalid installed manifest");
    }

    public static void FirstInstallQualificationRejectsNoncanonicalDestination()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Directory.CreateDirectory(Path.Combine(environment.RimWorldRoot, "Mods"));
        environment.WriteWorkspace(environment.RimWorldRoot,
            Path.Combine(environment.Root, "outside", "DevBridge2"));
        var result = environment.ResolveQualificationDestination(
            Path.Combine(environment.Root, "production-toolchain.json"));

        Assert(!result.Ok && result.Error?.Contains("canonical", StringComparison.OrdinalIgnoreCase) == true,
            "first-install qualification accepted a destination outside canonical RimWorld Mods");
    }

    public static void FirstInstallQualificationRequiresExplicitDestination()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Directory.CreateDirectory(Path.Combine(environment.RimWorldRoot, "Mods"));
        environment.WriteWorkspace(environment.RimWorldRoot, "");
        var result = environment.ResolveQualificationDestination(
            Path.Combine(environment.Root, "production-toolchain.json"));

        Assert(!result.Ok, "qualification inferred an unconfigured production destination");
    }

    public static void CandidateContractDoesNotExposeExternalReleaseWorkflow()
    {
        Assert(!typeof(ToolchainCandidate).GetProperties().Any(property =>
            property.Name.Contains("DevBridgeRelease", StringComparison.Ordinal) ||
            property.Name.Contains("DevBridgeSource", StringComparison.Ordinal)),
            "candidate contract must not expose an external DevBridge checkout or release manifest");
        Assert(ToolchainPromotionSchemas.OwnerProduct == "RimLiaison", "candidate ownership must remain RimLiaison");
    }

    public static void CandidateEvidenceMarksProjectUnimplicated()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        File.Delete(Path.Combine(environment.ManagedDirectory, "Assembly-CSharp.dll"));

        RimWorldManagedAssemblyResolution result = environment.Resolve(environment.DevBridgeRoot);
        string evidence = JsonSerializer.Serialize(result.ToEvidence());

        Assert(evidence.Contains("\"projectImplicated\":false", StringComparison.Ordinal), "prerequisite evidence must not implicate the project");
        Assert(evidence.Contains("\"owner\":\"RimLiaison\"", StringComparison.Ordinal), "prerequisite evidence must identify its owner");
    }
    public static void CliMaterializerBuildsCleanSourceOnlyCheckout()
    {
        using CliSourceEnvironment environment = CliSourceEnvironment.Create();
        Assert(!Directory.Exists(Path.Combine(environment.Root, "src", "RimLiaison.Cli", "bin")),
            "clean source fixture unexpectedly contains bin output");
        Assert(!Directory.Exists(Path.Combine(environment.Root, "src", "RimLiaison.Cli", "obj")),
            "clean source fixture unexpectedly contains obj output");

        ToolchainCandidateMaterializer.CliPublishResult result = environment.Publish();

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "source-only CLI publish failed");
        Assert(result.SelectedSdk == "8.0.424", "CLI publish did not use the pinned SDK");
        Assert(File.Exists(Path.Combine(result.StagingPath, "rimliaison.dll")),
            "source-only CLI publish did not emit the assembly");
        Assert(result.Commands.Any(command =>
                command.Arguments.FirstOrDefault() == "publish" &&
                !command.Arguments.Contains("--no-build") &&
                command.Arguments.Contains("--no-restore")),
            "CLI publish did not build from restored source");
        Assert(result.IntermediateRoot.StartsWith(
                Path.Combine(Path.GetDirectoryName(result.StagingPath)!, "cli-build"),
                StringComparison.OrdinalIgnoreCase),
            "CLI intermediate output escaped candidate-owned staging");
    }

    public static void CliMaterializerIgnoresStaleSourceOutput()
    {
        using CliSourceEnvironment environment = CliSourceEnvironment.Create();
        string stalePath = Path.Combine(
            environment.Root,
            "src",
            "RimLiaison.Cli",
            "bin",
            "Release",
            "net8.0",
            "rimliaison.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
        File.WriteAllText(stalePath, "stale-invalid-cli");

        ToolchainCandidateMaterializer.CliPublishResult result = environment.Publish();

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "stale-output CLI publish failed");
        Assert(File.ReadAllText(stalePath) == "stale-invalid-cli",
            "source-tree stale output was modified");
        Assert(File.ReadAllText(Path.Combine(result.StagingPath, "rimliaison.dll")) != "stale-invalid-cli",
            "stale source output was reused as the candidate CLI");
    }

    public static void CliMaterializerFailsClosedOnBuildFailure()
    {
        using CliSourceEnvironment environment = CliSourceEnvironment.Create();
        File.WriteAllText(
            Path.Combine(environment.Root, "src", "RimLiaison.Cli", "Program.cs"),
            "public class Broken { MissingType Value; }");

        ToolchainCandidateMaterializer.CliPublishResult result = environment.Publish();

        Assert(!result.Succeeded, "invalid source unexpectedly produced a CLI candidate");
        Assert(result.ErrorCode == "RIMLIAISON_CLI_BUILD_FAILED",
            "invalid source returned the wrong structured failure code: " + result.ErrorCode);
        Assert(!File.Exists(Path.Combine(result.StagingPath, "rimliaison.dll")),
            "failed source build left a complete CLI assembly");
    }

    public static void CliMaterializerRecordsCompleteClosureIdentity()
    {
        using CliSourceEnvironment environment = CliSourceEnvironment.Create();
        ToolchainCandidateMaterializer.CliPublishResult result = environment.Publish();
        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "identity CLI publish failed");

        CliDeploymentManifest manifest = CliDeploymentManifestService.Write(
            result.StagingPath,
            result.SourceRevision,
            "net8.0");
        string manifestPath = Path.Combine(result.StagingPath, CliDeploymentManifestService.FileName);
        bool verified = CliDeploymentManifestService.Verify(
            result.StagingPath,
            manifestPath,
            ToolchainFileHash.Sha256(manifestPath),
            manifest.PackageSha256,
            out _,
            out string? error);

        Assert(verified, error ?? "source-built CLI closure identity did not verify");
        Assert(manifest.SourceCommit == result.SourceRevision,
            "CLI deployment identity did not record the source revision");
        Assert(manifest.Files.Any(file => file.Path == "rimliaison.dll"),
            "CLI deployment identity omitted the assembly");
        Assert(manifest.Files.Any(file => file.Path == "rimliaison.deps.json"),
            "CLI deployment identity omitted the dependency closure");
    }
    public static void CliMaterializerIsolatesReferencedProjectIntermediates()
    {
        using CliSourceEnvironment environment = CliSourceEnvironment.Create(projectGraph: true);
        environment.SeedStaleOutputs();
        ToolchainCandidateMaterializer.CliPublishResult result = environment.Publish();
        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "project graph CLI publish failed");
        HashSet<string> intermediateRoots = new(StringComparer.OrdinalIgnoreCase);
        string candidateBuildRoot = Path.Combine(Path.GetDirectoryName(result.StagingPath)!, "cli-build");
        foreach ((string projectName, string projectPath) in environment.ProjectFiles)
        {
            using JsonDocument inspection = RunMsbuild(
                environment.Root,
                projectPath,
                result.IsolationPropertiesPath,
                "-getProperty:MSBuildProjectName,BaseIntermediateOutputPath,MSBuildProjectExtensionsPath,BaseOutputPath,RimLiaisonTestRepositoryProperty",
                "-getItem:Compile");
            JsonElement values = inspection.RootElement.GetProperty("Properties");
            Assert(values.GetProperty("RimLiaisonTestRepositoryProperty").GetString() == "active",
                "repository Directory.Build.props did not remain active");
            string intermediate = Path.GetFullPath(values.GetProperty("BaseIntermediateOutputPath").GetString()!);
            string extensions = Path.GetFullPath(values.GetProperty("MSBuildProjectExtensionsPath").GetString()!);
            string output = Path.GetFullPath(values.GetProperty("BaseOutputPath").GetString()!);
            Assert(intermediateRoots.Add(intermediate),
                "referenced projects share an evaluated intermediate root");
            Assert(intermediate.StartsWith(
                    Path.Combine(candidateBuildRoot, "obj") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "project intermediate root escaped candidate-owned obj");
            Assert(extensions == intermediate,
                "project extension path does not follow its isolated intermediate root");
            Assert(output.StartsWith(
                    Path.Combine(candidateBuildRoot, "bin") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "project output root escaped candidate-owned bin");

            string compileText = inspection.RootElement.GetProperty("Items").GetProperty("Compile").GetRawText();
            Assert(!compileText.Contains(
                    candidateBuildRoot,
                    StringComparison.OrdinalIgnoreCase),
                "a project Compile item consumed another project's generated intermediate files");
            Assert(!compileText.Contains(
                    Path.Combine(Path.GetDirectoryName(projectPath)!, "obj"),
                    StringComparison.OrdinalIgnoreCase),
                "a project Compile item consumed stale source-tree obj files");
        }

        Assert(intermediateRoots.Count == environment.ProjectFiles.Count,
            "the project graph did not receive one intermediate root per project");
        Assert(Directory.EnumerateFiles(result.IntermediateRoot, "*AssemblyInfo.cs", SearchOption.AllDirectories).Any(),
            "the isolated build did not emit generated assembly metadata");
        Assert(File.ReadAllText(environment.StaleBinaryPath) == "stale-source-bin",
            "the isolated build modified the stale source-tree bin artifact");
        Assert(File.ReadAllText(Path.Combine(result.StagingPath, "RimDev.Contracts.dll")) != "stale-source-bin",
            "the isolated publish consumed the stale source-tree bin artifact");
    }

    private static JsonDocument RunMsbuild(
        string workingDirectory,
        string projectPath,
        string isolationPropertiesPath,
        params string[] inspectionArguments)
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
        foreach (string argument in new[] { "msbuild", projectPath, "-nologo" }
                     .Concat(inspectionArguments)
                     .Append("-p:CustomBeforeDirectoryBuildProps=" + isolationPropertiesPath))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        Assert(process.Start(), "MSBuild inspection process could not start");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        Assert(process.ExitCode == 0, "MSBuild isolation inspection failed: " + stderr + stdout);
        Assert(!stderr.Contains("MSB4011", StringComparison.OrdinalIgnoreCase) &&
               !stdout.Contains("MSB4011", StringComparison.OrdinalIgnoreCase),
            "MSBuild isolation inspection repeated Directory.Build.props");
        return JsonDocument.Parse(stdout);
    }

    private sealed class CliSourceEnvironment : IDisposable
    {
        private CliSourceEnvironment(
            string root,
            IReadOnlyDictionary<string, string> projectFiles)
        {
            Root = root;
            Destination = Path.Combine(root, "candidate", "cli");
            ProjectFiles = projectFiles;
            StaleBinaryPath = Path.Combine(
                root,
                "src",
                "RimDev.Contracts",
                "bin",
                "Release",
                "net8.0",
                "RimDev.Contracts.dll");
        }

        public string Root { get; }
        public string Destination { get; }
        public IReadOnlyDictionary<string, string> ProjectFiles { get; }
        public string StaleBinaryPath { get; }

        public static CliSourceEnvironment Create(bool projectGraph = false)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "rimliaison-cli-source-" + Guid.NewGuid().ToString("N"));
            string cliRoot = Path.Combine(root, "src", "RimLiaison.Cli");
            Directory.CreateDirectory(cliRoot);
            File.WriteAllText(
                Path.Combine(root, "global.json"),
                """
                {
                  "sdk": {
                    "version": "8.0.424",
                    "rollForward": "disable",
                    "allowPrerelease": false
                  }
                }
                """);
            File.WriteAllText(
                Path.Combine(root, "Directory.Build.props"),
                """
                <Project>
                  <PropertyGroup>
                    <RimLiaisonTestRepositoryProperty>active</RimLiaisonTestRepositoryProperty>
                  </PropertyGroup>
                </Project>
                """);
            string references = projectGraph
                ? """
                  <ItemGroup>
                    <ProjectReference Include="..\RimContext.Core\RimContext.Core.csproj" />
                    <ProjectReference Include="..\RimError.Core\RimError.Core.csproj" />
                    <ProjectReference Include="..\RimDev.Contracts\RimDev.Contracts.csproj" />
                  </ItemGroup>
                """
                : string.Empty;
            File.WriteAllText(
                Path.Combine(cliRoot, "RimLiaison.Cli.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AssemblyName>rimliaison</AssemblyName>
                  </PropertyGroup>
                {references}
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(cliRoot, "Program.cs"),
                "Console.WriteLine(\"source-built-cli\");");

            Dictionary<string, string> projectFiles = new(StringComparer.OrdinalIgnoreCase)
            {
                ["RimLiaison.Cli"] = Path.Combine(cliRoot, "RimLiaison.Cli.csproj")
            };
            if (projectGraph)
            {
                string contextRoot = Path.Combine(root, "src", "RimContext.Core");
                string errorRoot = Path.Combine(root, "src", "RimError.Core");
                string contractsRoot = Path.Combine(root, "src", "RimDev.Contracts");
                Directory.CreateDirectory(contextRoot);
                Directory.CreateDirectory(errorRoot);
                Directory.CreateDirectory(contractsRoot);
                File.WriteAllText(
                    Path.Combine(contextRoot, "RimContext.Core.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <AssemblyName>RimContext.Core</AssemblyName>
                      </PropertyGroup>
                      <ItemGroup>
                        <ProjectReference Include="..\RimDev.Contracts\RimDev.Contracts.csproj" />
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(errorRoot, "RimError.Core.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <AssemblyName>RimError.Core</AssemblyName>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(
                    Path.Combine(contractsRoot, "RimDev.Contracts.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <AssemblyName>RimDev.Contracts</AssemblyName>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(contextRoot, "Context.cs"), "public sealed class ContextType {}");
                File.WriteAllText(Path.Combine(errorRoot, "Error.cs"), "public sealed class ErrorType {}");
                File.WriteAllText(Path.Combine(contractsRoot, "Contract.cs"), "public sealed class ContractType {}");
                projectFiles["RimContext.Core"] = Path.Combine(contextRoot, "RimContext.Core.csproj");
                projectFiles["RimError.Core"] = Path.Combine(errorRoot, "RimError.Core.csproj");
                projectFiles["RimDev.Contracts"] = Path.Combine(contractsRoot, "RimDev.Contracts.csproj");
            }
            return new(root, projectFiles);
        }
        public void SeedStaleOutputs()
        {
            string contractsRoot = Path.Combine(Root, "src", "RimDev.Contracts");
            string sourceObj = Path.Combine(contractsRoot, "obj", "Release", "net8.0");
            Directory.CreateDirectory(sourceObj);
            File.WriteAllText(
                Path.Combine(sourceObj, ".NETCoreApp,Version=v8.0.AssemblyAttributes.cs"),
                "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v8.0\")]");
            File.WriteAllText(
                Path.Combine(sourceObj, "RimDev.Contracts.AssemblyInfo.cs"),
                "[assembly: System.Reflection.AssemblyCompany(\"stale-source-obj\")]");
            Directory.CreateDirectory(Path.GetDirectoryName(StaleBinaryPath)!);
            File.WriteAllText(StaleBinaryPath, "stale-source-bin");
        }


        public ToolchainCandidateMaterializer.CliPublishResult Publish() =>
            ToolchainCandidateMaterializer.PublishCliAsync(
                    Root,
                    Destination,
                    "test-source-revision",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }


    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root, string projectRoot, string rimWorldRoot)
        {
            Root = root;
            ProjectRoot = projectRoot;
            RimWorldRoot = rimWorldRoot;
            ManagedDirectory = Path.Combine(rimWorldRoot, "RimWorldWin64_Data", "Managed");
            DevBridgeRoot = Path.Combine(root, "far-away", "DevBridge2");
        }

        public string Root { get; }
        public string ProjectRoot { get; }
        public string RimWorldRoot { get; }
        public string ManagedDirectory { get; }
        public string DevBridgeRoot { get; }
        private string WorkspacePath => Path.Combine(Root, ".rimdev", "workspace.json");

        public static TestEnvironment Create(string? rimWorldRoot = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "rimliaison-candidate-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(root, "Project");
            string rimWorld = rimWorldRoot ?? Path.Combine(root, "RimWorld");
            Directory.CreateDirectory(Path.Combine(root, ".rimdev"));
            var environment = new TestEnvironment(root, project, rimWorld);
            Directory.CreateDirectory(Path.Combine(project, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(project, "Source"));
            Directory.CreateDirectory(Path.Combine(project, "About"));
            if (rimWorldRoot is null)
            {
                Directory.CreateDirectory(environment.ManagedDirectory);
            }
            Directory.CreateDirectory(environment.DevBridgeRoot);
            File.WriteAllText(Path.Combine(project, "About", "About.xml"), "<ModMetaData><packageId>lan.candidate</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(project, ".rimdev", "stack.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = RimDevStackSchema.Current,
                project = "CandidateProject",
                devBridgeProject = "candidate",
                catalog = "catalog.json",
                rimBridge = "disabled",
                testRecipe = "candidate-smoke",
                workload = "production",
                projectType = "rimworld-content-mod",
                packageId = "lan.candidate",
                sourceProject = "Source/CandidateProject.csproj",
                configuration = "Release",
                expectedAssembly = "CandidateProject.dll",
                deploymentTarget = "1.6/Assemblies/CandidateProject.dll",
                runtimePackage = new { sourceRoot = ".", include = new[] { "About/**", "1.*/**" }, exclude = new[] { "Source/**" } }
            }));
            File.WriteAllText(Path.Combine(project, "Source", "CandidateProject.csproj"), "<Project />");
            if (rimWorldRoot is null)
            {
                File.WriteAllText(Path.Combine(environment.ManagedDirectory, "Assembly-CSharp.dll"), "fixture");
                File.WriteAllText(Path.Combine(environment.ManagedDirectory, "UnityEngine.CoreModule.dll"), "fixture");
            }
            environment.WriteWorkspace(rimWorld);
            return environment;
        }

        public (bool Ok, string? RuntimeRoot, string Contract, string? Error)
            ResolveQualificationDestination(string manifest)
        {
            string? previous = Environment.GetEnvironmentVariable("RIMDEV_ROOT");
            Environment.SetEnvironmentVariable("RIMDEV_ROOT", Root);
            try
            {
                bool ok = ToolchainPromotionService.TryResolveQualificationDestination(
                    ProjectRoot, manifest,
                    out string? runtimeRoot, out string contract, out string? error);
                return (ok, runtimeRoot, contract, error);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RIMDEV_ROOT", previous);
            }
        }

        public RimWorldManagedAssemblyResolution Resolve(string devBridgeRoot)
        {
            string? previous = Environment.GetEnvironmentVariable("RIMDEV_ROOT");
            Environment.SetEnvironmentVariable("RIMDEV_ROOT", Root);
            try
            {
                return RimWorldManagedAssemblyResolver.Resolve(ProjectRoot, devBridgeRoot);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RIMDEV_ROOT", previous);
            }
        }

        public void WriteWorkspace(string rimWorldRoot, string? runtimeRootOverride = null)
        {
            File.WriteAllText(
                WorkspacePath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = RimDevSchemas.Workspace,
                    rimWorldRoot,
                    devBridgeRuntimeRoot = runtimeRootOverride ??
                        Path.Combine(rimWorldRoot, "Mods", "DevBridge2"),
                    activeModsRoot = Path.Combine(rimWorldRoot, "Mods"),
                    repositories = new[] { new { path = "Project" } },
                    packageMappings = new { }
                }));
        }

        public void Dispose()
        {
            if (Path.GetFullPath(RimWorldRoot).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
