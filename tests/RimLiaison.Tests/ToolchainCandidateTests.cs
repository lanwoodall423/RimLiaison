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

    public static void ReleaseArgumentsPassExactManagedDirectory()
    {
        string managed = Path.Combine("C:\\Games", "RimWorldWin64_Data", "Managed");
        string[] arguments = ToolchainCandidateMaterializer.BuildReleaseArguments(
            "C:\\pinned\\DevBridge2\\scripts\\release.ps1",
            "C:\\candidate\\devbridge-build",
            managed);

        int option = Array.IndexOf(arguments, "-RimWorldManagedDir");
        Assert(option >= 0 && option + 1 < arguments.Length, "release arguments omitted RimWorldManagedDir");
        Assert(arguments[option + 1] == managed, "release arguments changed the resolved managed directory");
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

        public void WriteWorkspace(string rimWorldRoot)
        {
            File.WriteAllText(
                WorkspacePath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = RimDevSchemas.Workspace,
                    rimWorldRoot,
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
