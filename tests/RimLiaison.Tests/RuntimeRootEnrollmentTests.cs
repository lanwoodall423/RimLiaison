using RimLiaison.DevBridge;
using System.Text.Json;
using RimLiaison.RimDev;
using RimLiaison.Stack;

namespace RimLiaison.Tests;

internal static class RuntimeRootEnrollmentTests
{
    public static void NewValidProjectAutoEnrolls()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        ProjectRuntimeBindingResult result = environment.Resolve();

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "binding failed");
        Assert(result.RepairOccurred, "first discovery must update the machine-local workspace");
        Assert(result.WorkspaceEntryStatus == "updated", "first discovery must report updated enrollment");
        Assert(result.RuntimeRoot == Path.Combine(environment.ModsRoot, "DemoMod"), "runtime root must use the portable project identity");
        Assert(File.ReadAllText(environment.WorkspacePath).Contains("DemoMod", StringComparison.Ordinal), "workspace enrollment was not persisted");
    }

    public static void AlreadyEnrolledProjectIsUnchanged()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Assert(environment.Resolve().Succeeded, "initial enrollment failed");
        ProjectRuntimeBindingResult result = environment.Resolve();

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "binding failed");
        Assert(!result.RepairOccurred, "an enrolled project must not be rewritten");
        Assert(result.WorkspaceEntryStatus == "unchanged", "second discovery must report unchanged enrollment");
    }

    public static void StaleSourcePathIsRefreshedSafely()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "Deleted/DemoMod" } },
            packageMappings = new { }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(environment.WorkspacePath));
        string[] paths = document.RootElement.GetProperty("repositories")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString()!)
            .ToArray();
        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "binding failed");
        Assert(paths.Contains("DemoMod", StringComparer.OrdinalIgnoreCase), "the active source path was not enrolled");
        Assert(paths.Count(path => path.Equals("DemoMod", StringComparison.OrdinalIgnoreCase)) == 1, "stale active bindings must be refreshed, not duplicated");
    }

    public static void RimWorldMoveUpdatesDerivedRoot()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Assert(environment.Resolve().Succeeded, "initial enrollment failed");
        string movedRoot = Path.Combine(environment.Root, "RimWorldMoved");
        string movedMods = Path.Combine(movedRoot, "Mods");
        Directory.CreateDirectory(movedMods);
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = movedRoot,
            activeModsRoot = movedMods,
            repositories = new[] { new { path = "DemoMod" } },
            packageMappings = new { DemoMod = "DemoMod" }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "binding failed");
        Assert(result.RuntimeRoot == Path.Combine(movedMods, "DemoMod"), "derived runtime root did not follow the installation move");
    }

    public static void RuntimeRootNeverBecomesSourceRoot()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string rimWorld = Path.Combine(environment.ProjectRoot, "RimWorld");
        Directory.CreateDirectory(Path.Combine(rimWorld, "Mods"));
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = rimWorld,
            activeModsRoot = Path.Combine(rimWorld, "Mods"),
            repositories = Array.Empty<object>(),
            packageMappings = new { DemoMod = ".." }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "a source collision must fail closed");
        Assert(result.ErrorCode is "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS" or "PROJECT_RUNTIME_ROOT_INVALID", "source collision returned the wrong structured error");
    }

    public static void RuntimeRootOutsideModsIsRejected()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = Path.Combine(environment.Root, "Other"),
            repositories = Array.Empty<object>(),
            packageMappings = new { }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "an active root outside canonical Mods must fail");
        Assert(result.ErrorCode == "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS", "outside Mods must have a precise error");
    }

    public static void TwoProjectsClaimingRuntimeRootFailClosed()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string second = environment.CreateSecondProject("OtherMod", "lan.other");
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "DemoMod" }, new { path = "OtherMod" } },
            packageMappings = new Dictionary<string, string>
            {
                ["DemoMod"] = "Shared",
                ["OtherMod"] = "Shared"
            }
        });
        Directory.CreateDirectory(Path.Combine(environment.ModsRoot, "Shared"));
        File.WriteAllText(Path.Combine(environment.ModsRoot, "Shared", "About.xml"), "");

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "duplicate runtime ownership must fail closed");
        Assert(result.ErrorCode == "PROJECT_RUNTIME_ROOT_CONFLICT", "duplicate ownership must have a conflict error");
        Assert(second.Length > 0, "second project setup failed");
    }

    public static void AmbiguousRuntimeFolderIdentityFailsClosed()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        foreach (string folder in new[] { "One", "Two" })
        {
            string about = Path.Combine(environment.ModsRoot, folder, "About");
            Directory.CreateDirectory(about);
            File.WriteAllText(Path.Combine(about, "About.xml"), "<ModMetaData><packageId>lan.demo</packageId></ModMetaData>");
        }
        environment.SetPackageId("lan.demo");

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "two package matches must not be guessed");
        Assert(result.ErrorCode == "PROJECT_RUNTIME_ROOT_AMBIGUOUS", "ambiguous identity must have an ambiguity error");
    }

    public static void MissingRimWorldRootFailsEarly()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = Path.Combine(environment.Root, "missing-rimworld"),
            activeModsRoot = Path.Combine(environment.Root, "missing-rimworld", "Mods"),
            repositories = Array.Empty<object>(),
            packageMappings = new { }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "missing RimWorld root must fail before runtime work");
        Assert(result.ErrorCode == "PROJECT_RIMWORLD_ROOT_MISSING", "missing root must be reported precisely");
    }

    public static void ConcurrentEnrollmentIsIdempotent()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        ProjectRuntimeBindingResult[] results = Task.WhenAll(
                Enumerable.Range(0, 12).Select(_ => Task.Run(environment.Resolve)))
            .GetAwaiter()
            .GetResult();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(environment.WorkspacePath));
        string[] paths = document.RootElement.GetProperty("repositories")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString()!)
            .ToArray();

        Assert(results.All(result => result.Succeeded), "concurrent enrollment must succeed for every observer");
        Assert(paths.Count(path => path.Equals("DemoMod", StringComparison.OrdinalIgnoreCase)) == 1, "concurrent enrollment duplicated the active entry");
    }

    public static void ProjectMetadataRemainsPortable()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        ProjectRuntimeBindingResult result = environment.Resolve();
        string manifest = File.ReadAllText(Path.Combine(environment.ProjectRoot, ".rimdev", "stack.json"));

        Assert(result.Succeeded, result.Error ?? result.ErrorCode ?? "binding failed");
        Assert(!manifest.Contains(environment.RimWorldRoot, StringComparison.OrdinalIgnoreCase), "project metadata contains the machine RimWorld root");
        Assert(!manifest.Contains(environment.Root, StringComparison.OrdinalIgnoreCase), "project metadata contains a machine source path");
    }

    public static void SuccessfulSelfHealProvidesMaterializerRoot()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        ProjectRuntimeBindingResult binding = environment.Resolve();
        ProjectOwnedDescriptorMaterialization? materialization = ProjectOwnedDescriptorMaterializer.Materialize(
            "DemoMod",
            environment.ProjectRoot,
            binding.RuntimeRoot,
            out string? errorCode,
            out string? error);
        try
        {
            Assert(binding.Succeeded, binding.Error ?? binding.ErrorCode ?? "binding failed");
            Assert(materialization is not null, error ?? errorCode ?? "materialization failed after self-heal");
        }
        finally
        {
            if (materialization is not null)
            {
                ProjectOwnedDescriptorMaterializer.Delete(materialization);
            }
        }
    }

    public static void FailedSelfHealReturnsStructuredReason()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = Array.Empty<object>(),
            packageMappings = new { DemoMod = "One", Other = "Two" }
        });
        foreach (string folder in new[] { "One", "Two" })
        {
            string about = Path.Combine(environment.ModsRoot, folder, "About");
            Directory.CreateDirectory(about);
            File.WriteAllText(Path.Combine(about, "About.xml"), "<ModMetaData><packageId>lan.demo</packageId></ModMetaData>");
        }
        environment.SetPackageId("lan.demo");

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "unsafe self-heal must fail");
        Assert(!string.IsNullOrWhiteSpace(result.ErrorCode) && !string.IsNullOrWhiteSpace(result.NextAction), "failed self-heal must return structured reason and one next action");
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
        private TestEnvironment(string root, string projectRoot, string rimWorldRoot, string modsRoot)
        {
            Root = root;
            ProjectRoot = projectRoot;
            RimWorldRoot = rimWorldRoot;
            ModsRoot = modsRoot;
            WorkspacePath = Path.Combine(root, ".rimdev", "workspace.json");
        }

        public string Root { get; }
        public string ProjectRoot { get; }
        public string RimWorldRoot { get; }
        public string ModsRoot { get; }
        public string WorkspacePath { get; }

        public static TestEnvironment Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "rimliaison-runtime-binding-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(root, "DemoMod");
            string rimWorld = Path.Combine(root, "RimWorld");
            string mods = Path.Combine(rimWorld, "Mods");
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            Directory.CreateDirectory(Path.Combine(project, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(project, "Source"));
            Directory.CreateDirectory(Path.Combine(project, "About"));
            Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(project, "Source", "DemoMod.csproj"), "<Project><PropertyGroup><AssemblyName>DemoMod</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(project, "About", "About.xml"), "<ModMetaData><packageId>lan.demo</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(project, ".rimdev", "stack.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = RimDevStackSchema.Current,
                project = "DemoMod",
                devBridgeProject = "demo",
                catalog = "catalog.json",
                rimBridge = "disabled",
                workload = "production",
                projectType = "rimworld-content-mod",
                packageId = "lan.demo",
                sourceProject = "Source/DemoMod.csproj",
                configuration = "Release",
                expectedAssembly = "DemoMod.dll",
                deploymentTarget = "1.6/Assemblies/DemoMod.dll",
                testRecipe = "demo-smoke",
                runtimePackage = new
                {
                    sourceRoot = ".",
                    include = new[] { "About/**", "1.*/**" },
                    exclude = new[] { ".rimdev/**", "Source/**", "bin/**", "obj/**" }
                }
            }));
            var environment = new TestEnvironment(root, project, rimWorld, mods);
            environment.WriteWorkspace(new
            {
                schemaVersion = RimDevSchemas.Workspace,
                rimWorldRoot = rimWorld,
                activeModsRoot = mods,
                repositories = Array.Empty<object>(),
                packageMappings = new { }
            });
            return environment;
        }

        public ProjectRuntimeBindingResult Resolve() =>
            ProjectRuntimeBindingResolver.Resolve(
                ProjectRoot,
                StackManifestResolver.Discover(ProjectRoot).Manifest!);

        public string CreateSecondProject(string name, string packageId)
        {
            string project = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            Directory.CreateDirectory(Path.Combine(project, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(project, "Source"));
            Directory.CreateDirectory(Path.Combine(project, "About"));
            File.WriteAllText(Path.Combine(project, "Source", name + ".csproj"), $"<Project><PropertyGroup><AssemblyName>{name}</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(project, "About", "About.xml"), $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(project, ".rimdev", "stack.json"), File.ReadAllText(Path.Combine(ProjectRoot, ".rimdev", "stack.json"))
                .Replace("DemoMod", name, StringComparison.Ordinal)
                .Replace("lan.demo", packageId, StringComparison.Ordinal));
            return project;
        }

        public void SetPackageId(string packageId)
        {
            string path = Path.Combine(ProjectRoot, "About", "About.xml");
            File.WriteAllText(path, $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
            string manifestPath = Path.Combine(ProjectRoot, ".rimdev", "stack.json");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("lan.demo", packageId, StringComparison.Ordinal));
        }

        public void WriteWorkspace(object document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkspacePath)!);
            File.WriteAllText(WorkspacePath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        }
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
