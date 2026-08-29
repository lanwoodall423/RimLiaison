using System.Diagnostics;

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
        Assert(result.ErrorCode is "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS" or "PROJECT_RUNTIME_ROOT_INVALID", "source collision returned the wrong structured error");
        Assert(result.Health is ProjectBindingHealthStates.RuntimeOutsideMods or ProjectBindingHealthStates.SourceEqualsRuntime, "source collision returned the wrong health state");
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
        Assert(result.ErrorCode == "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS", "outside Mods must have a precise error");
        Assert(result.Health == ProjectBindingHealthStates.RuntimeOutsideMods, "outside Mods must have a precise health state");
    }

    public static void TwoProjectsClaimingRuntimeRootFailClosed()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string second = environment.CreateSecondProject("OtherMod", "lan.other");
        string secondManifestPath = Path.Combine(second, ".rimdev", "stack.json");
        File.WriteAllText(
            secondManifestPath,
            File.ReadAllText(secondManifestPath).Replace(
                "\"devBridgeProject\":\"demo\"",
                "\"devBridgeProject\":\"other\"",
                StringComparison.Ordinal));
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
        Assert(result.Health is ProjectBindingHealthStates.RuntimeRootConflict or ProjectBindingHealthStates.ProjectIdentityConflict, "duplicate ownership must have a conflict health state");
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
        Assert(result.Health == ProjectBindingHealthStates.RimWorldRootMissing, "missing root must have a precise health state");
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

    public static void WorkspaceAuditReportsHealthyProjects()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Assert(environment.Resolve().Succeeded, "initial enrollment failed");

        WorkspaceIntegrityAuditResult audit = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: false);
        WorkspaceIntegrityEntry project = audit.Projects.Single();

        Assert(audit.Succeeded && audit.Status == "READY", "healthy workspace audit must be ready");
        Assert(project.Health == ProjectBindingHealthStates.Healthy, "enrolled project must be healthy");
        Assert(project.IssueCode is null, "healthy project must not expose an issue");
        string evidence = JsonSerializer.Serialize(audit.ToEvidence());
        Assert(evidence.Contains("rimliaison-workspace-integrity/v1", StringComparison.Ordinal), "audit evidence schema is unstable");
    }

    public static void WorkspaceAuditRepairsMissingEnrollment()
    {
        using TestEnvironment environment = TestEnvironment.Create();

        WorkspaceIntegrityAuditResult before = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: false);
        Assert(before.Projects.Single().Health == ProjectBindingHealthStates.MissingRegistrationRepairable, "missing enrollment must be repairable");

        WorkspaceIntegrityAuditResult repaired = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: true);
        Assert(repaired.Projects.Single().Health == ProjectBindingHealthStates.Repaired, "safe missing enrollment was not repaired");
        Assert(ProjectRuntimeBindingResolver.Audit(environment.ProjectRoot, repair: false).Projects.Single().Health == ProjectBindingHealthStates.Healthy, "repaired enrollment did not become healthy");
    }

    public static void WorkspaceAuditRepairsStaleRuntimeMapping()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "DemoMod" } },
            packageMappings = new Dictionary<string, string> { ["demo"] = "OldFolder" }
        });
        string about = Path.Combine(environment.ModsRoot, "NewFolder", "About");
        Directory.CreateDirectory(about);
        File.WriteAllText(Path.Combine(about, "About.xml"), "<ModMetaData><packageId>lan.demo</packageId></ModMetaData>");

        WorkspaceIntegrityAuditResult before = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: false);
        Assert(before.Projects.Single().Health == ProjectBindingHealthStates.StaleRuntimeRootRepairable, "stale runtime mapping was not classified");

        WorkspaceIntegrityAuditResult repaired = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: true);
        WorkspaceIntegrityEntry project = repaired.Projects.Single();
        Assert(project.Health == ProjectBindingHealthStates.Repaired, "stale runtime mapping was not repaired");
        Assert(project.OriginalRuntimeRoot?.EndsWith("OldFolder", StringComparison.OrdinalIgnoreCase) == true, "original runtime mapping was not retained");
        Assert(project.RepairedRuntimeRoot?.EndsWith("NewFolder", StringComparison.OrdinalIgnoreCase) == true, "repaired runtime mapping was not reported");
    }

    public static void WorkspaceAuditIsolatesBlockedProject()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string second = environment.CreateSecondProject("OtherMod", "lan.other");
        string secondManifestPath = Path.Combine(second, ".rimdev", "stack.json");
        File.WriteAllText(
            secondManifestPath,
            File.ReadAllText(secondManifestPath).Replace("\"devBridgeProject\":\"demo\"", "\"devBridgeProject\":\"other\"", StringComparison.Ordinal));
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "DemoMod" }, new { path = "OtherMod" } },
            packageMappings = new Dictionary<string, string> { ["DemoMod"] = "DemoMod", ["other"] = "../outside" }
        });

        WorkspaceIntegrityAuditResult audit = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: true);
        WorkspaceIntegrityEntry healthy = audit.Projects.Single(project => project.Project == "demo");
        WorkspaceIntegrityEntry blocked = audit.Projects.Single(project => project.Project == "other");
        Assert(healthy.Health is ProjectBindingHealthStates.Healthy or ProjectBindingHealthStates.Repaired, "one project was corrupted by another project's failure");
        Assert(blocked.IssueCode == "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS", "blocked project did not retain its precise issue");
        Assert(audit.Status == "BLOCKED", "mixed audit must report blocked status");
        Assert(second.Length > 0, "second project setup failed");
    }

    public static void WorkspaceAuditReportsDisappearedProject()
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

        WorkspaceIntegrityAuditResult audit = ProjectRuntimeBindingResolver.Audit(
            environment.ProjectRoot,
            repair: true);
        WorkspaceIntegrityEntry missing = audit.Projects.Single(project =>
            project.IssueCode == "PROJECT_SOURCE_ROOT_MISSING");
        Assert(missing.Health == ProjectBindingHealthStates.Unknown, "disappeared project must be blocked explicitly");
        Assert(!missing.Repairable, "a disappeared project must not be auto-repaired");
    }

    public static void CleanWholeModPackageMaterializesContract()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Directory.CreateDirectory(Path.Combine(environment.ProjectRoot, "1.6", "Assemblies"));
        File.WriteAllText(Path.Combine(environment.ProjectRoot, "1.6", "Assemblies", "DemoMod.dll"), "assembly");
        File.WriteAllText(Path.Combine(environment.ProjectRoot, "LoadFolders.xml"), "<loadFolders />");
        string manifestPath = Path.Combine(environment.ProjectRoot, ".rimdev", "stack.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                "\"1.*/**\"",
                "\"1.*/**\", \"LoadFolders.xml\"",
                StringComparison.Ordinal));

        ProjectRuntimeBindingResult binding = environment.Resolve();
        ProjectOwnedDescriptorMaterialization? materialization = ProjectOwnedDescriptorMaterializer.Materialize(
            "demo",
            environment.ProjectRoot,
            binding.RuntimeRoot,
            out string? errorCode,
            out string? error);
        try
        {
            Assert(binding.Succeeded, binding.Error ?? binding.ErrorCode ?? "binding failed");
            Assert(materialization is not null, error ?? errorCode ?? "contract materialization failed");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(materialization!.DescriptorPath));
            JsonElement package = document.RootElement.GetProperty("runtimePackage");
            string packageJson = package.GetRawText();
            Assert(packageJson.Contains("LoadFolders.xml", StringComparison.Ordinal), "whole-mod package contract must retain non-DLL content");
            Assert(packageJson.Contains("1.*/**", StringComparison.Ordinal), "whole-mod package contract must retain versioned content");
            Assert(!Path.GetFullPath(materialization.TemporaryRoot).StartsWith(
                Path.GetFullPath(environment.ProjectRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "execution contract must remain outside the source root");
            Assert(!Path.GetFullPath(binding.RuntimeRoot!).StartsWith(
                Path.GetFullPath(environment.ProjectRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "runtime root must remain outside the source root");
        }
        finally
        {
            if (materialization is not null)
            {
                ProjectOwnedDescriptorMaterializer.Delete(materialization);
            }
        }
    }

    public static void WorkspaceEnrollmentSurvivesProcessRestart()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        Assert(environment.Resolve().Succeeded, "initial enrollment failed");
        using Process process = StartProbe(environment, "workspace enrollment process probe");
        Assert(process.WaitForExit(120_000), "restart probe exceeded its bound");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.ExitCode == 0, "a new RimLiaison process could not read the persisted enrollment: " + output);
    }

    public static void ConcurrentProcessEnrollmentIsIdempotent()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        using Process first = StartProbe(environment, "workspace enrollment process probe");
        using Process second = StartProbe(environment, "workspace enrollment process probe");
        Assert(first.WaitForExit(120_000), "first concurrent enrollment exceeded its bound");
        Assert(second.WaitForExit(120_000), "second concurrent enrollment exceeded its bound");
        string firstOutput = first.StandardOutput.ReadToEnd();
        string secondOutput = second.StandardOutput.ReadToEnd();
        Assert(first.ExitCode == 0 && second.ExitCode == 0,
            "concurrent processes did not receive a consistent result: " + firstOutput + secondOutput);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(environment.WorkspacePath));
        string[] paths = document.RootElement.GetProperty("repositories")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString()!)
            .ToArray();
        Assert(paths.Count(path => path.Equals("DemoMod", StringComparison.OrdinalIgnoreCase)) == 1,
            "concurrent process enrollment duplicated the registration");
    }

    public static void DuplicateProjectIdentityFailsBeforeEnrollment()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        string second = environment.CreateSecondProject("OtherMod", "lan.other");
        string secondManifest = Path.Combine(second, ".rimdev", "stack.json");
        File.WriteAllText(
            secondManifest,
            File.ReadAllText(secondManifest)
                .Replace("\"devBridgeProject\":\"demo\"", "\"devBridgeProject\":\"other\"", StringComparison.Ordinal)
                .Replace("\"lan.other\"", "\"lan.demo\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(second, "About", "About.xml"), "<ModMetaData><packageId>lan.demo</packageId></ModMetaData>");
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "DemoMod" }, new { path = "OtherMod" } },
            packageMappings = new { }
        });

        ProjectRuntimeBindingResult result = environment.Resolve();
        Assert(!result.Succeeded, "duplicate package identity must fail closed");
        Assert(result.ErrorCode == "PROJECT_IDENTITY_CONFLICT", "duplicate package identity returned the wrong error");
        Assert(result.Health == ProjectBindingHealthStates.ProjectIdentityConflict, "duplicate identity returned the wrong health");
    }

    public static void MalformedAboutXmlFailsBeforeEnrollment()
    {
        using TestEnvironment environment = TestEnvironment.Create();
        RimDevStackManifest manifest = StackManifestResolver.Discover(environment.ProjectRoot).Manifest!;
        File.WriteAllText(Path.Combine(environment.ProjectRoot, "About", "About.xml"), "<ModMetaData>");
        environment.WriteWorkspace(new
        {
            schemaVersion = RimDevSchemas.Workspace,
            rimWorldRoot = environment.RimWorldRoot,
            activeModsRoot = environment.ModsRoot,
            repositories = new[] { new { path = "DemoMod" } },
            packageMappings = new { }
        });


        ProjectRuntimeBindingResult result = ProjectRuntimeBindingResolver.Resolve(
            environment.ProjectRoot,
            manifest);
        Assert(!result.Succeeded, "malformed About.xml must fail closed");
        Assert(result.ErrorCode == "PROJECT_METADATA_IDENTITY_CONTRADICTION", "malformed About.xml returned the wrong error");
        Assert(result.Health == ProjectBindingHealthStates.ProjectIdentityConflict, "malformed About.xml returned the wrong health");
    }

    public static void SourceUnderModsFailsBeforeEnrollment()
    {
        using TestEnvironment environment = TestEnvironment.Create(sourceUnderMods: true);
        ProjectRuntimeBindingResult result = environment.Resolve();

        Assert(!result.Succeeded, "a source checkout under Mods must fail closed");
        Assert(result.ErrorCode == "PROJECT_SOURCE_ROOT_IN_MODS", "source under Mods returned the wrong error");
        Assert(result.Health == ProjectBindingHealthStates.SourceUnderMods, "source under Mods returned the wrong health");
    }

    public static void WorkspaceEnrollmentProcessProbe()
    {
        string? root = Environment.GetEnvironmentVariable("RIMTEST_WORKSPACE_PROBE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        WorkspaceIntegrityAuditResult audit = ProjectRuntimeBindingResolver.Audit(
            Path.Combine(root!, "DemoMod"),
            repair: true);
        Assert(audit.Status == "READY", "workspace probe did not resolve a ready workspace");
        Assert(audit.Projects.Single().Health is ProjectBindingHealthStates.Healthy or ProjectBindingHealthStates.Repaired,
            "workspace probe did not observe a canonical healthy enrollment");
    }

    private static Process StartProbe(TestEnvironment environment, string filter)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("test process path is unavailable");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add(filter);
        start.Environment["RIMTEST_WORKSPACE_PROBE_ROOT"] = environment.Root;
        start.Environment["RIMDEV_ROOT"] = environment.Root;
        return Process.Start(start) ?? throw new InvalidOperationException("workspace probe did not start");
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

        public static TestEnvironment Create(bool sourceUnderMods = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "rimliaison-runtime-binding-" + Guid.NewGuid().ToString("N"));
            string rimWorld = Path.Combine(root, "RimWorld");
            string mods = Path.Combine(rimWorld, "Mods");
            string project = sourceUnderMods ? Path.Combine(mods, "DemoMod") : Path.Combine(root, "DemoMod");
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
