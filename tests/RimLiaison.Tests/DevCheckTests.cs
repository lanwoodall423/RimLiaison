using System.Diagnostics;
using System.Text.Json;
using RimLiaison;

namespace RimLiaison.Tests;

internal static class DevCheckTests
{
    public static void ParsesOptionalWorkspace()
    {
        CliRequest request = CliParser.Parse(["dev-check", "C:/example-mod", "--json"]);
        if (request.Command != CliCommand.DevCheck || request.Id != "C:/example-mod")
            throw new InvalidOperationException("dev-check did not preserve its optional workspace positional.");
    }

    public static void MissingAboutFailsClosed()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.Run(["dev-check", workspace, "--json"], stdout, stderr);
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            if (exitCode != CliExitCodes.TestFailure ||
                result.RootElement.GetProperty("static").GetProperty("status").GetString() != "STATIC_FAIL" ||
                result.RootElement.GetProperty("runtime").GetProperty("status").GetString() != "RUNTIME_NOT_RUN" ||
                result.RootElement.GetProperty("cleanup").GetProperty("status").GetString() != "CLEANUP_PASS")
            {
                throw new InvalidOperationException("missing About.xml did not fail closed with truthful statuses.");
            }
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    public static void SelectsReferencedProductionProjects()
    {
        string workspace = CreateWorkspace();
        try
        {
            string source = Path.Combine(workspace, "Source", "Main.csproj");
            string library = Path.Combine(workspace, "Libraries", "Core.csproj");
            string tooling = Path.Combine(workspace, "DevTools", "Inspector.csproj");
            WriteProject(source, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Libraries/Core.csproj" /></ItemGroup>
                </Project>
                """);
            WriteProject(library, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Core</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteProject(tooling, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Inspector</AssemblyName></PropertyGroup>
                </Project>
                """);

            DevCheckCommand.ProjectSelectionResult selection = DevCheckCommand.SelectProjects(
                workspace,
                [source, library, tooling]);
            if (selection.IsAmbiguous ||
                selection.BuildRoots.Count != 1 ||
                selection.ProductionProjects.Count != 2 ||
                !selection.ProductionProjects.Contains(Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase) ||
                !selection.ProductionProjects.Contains(Path.GetFullPath(library), StringComparer.OrdinalIgnoreCase) ||
                selection.ExcludedProjects.Count != 1 ||
                !selection.ExcludedProjects.Contains(Path.GetFullPath(tooling), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("project-reference graph was not selected as one production build.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }
    public static void ExcludedTestReferencesDoNotDisqualifyProductionRoot()
    {
        string workspace = CreateWorkspace();
        try
        {
            string source = Path.Combine(workspace, "Source", "Main.csproj");
            string tests = Path.Combine(workspace, "Tests", "Main.Tests.csproj");
            WriteProject(source, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteProject(tests, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main.Tests</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Source/Main.csproj" /></ItemGroup>
                </Project>
                """);

            DevCheckCommand.ProjectSelectionResult selection = DevCheckCommand.SelectProjects(
                workspace,
                [source, tests]);
            if (selection.IsAmbiguous ||
                selection.BuildRoots.Count != 1 ||
                !selection.BuildRoots.Contains(Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase) ||
                selection.ProductionProjects.Count != 1 ||
                !selection.ProductionProjects.Contains(Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase) ||
                selection.ExcludedProjects.Count != 1 ||
                !selection.ExcludedProjects.Contains(Path.GetFullPath(tests), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("excluded test reference disqualified the production build root.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    public static void ExcludedToolingReferencesDoNotDisqualifyProductionRoot()
    {
        string workspace = CreateWorkspace();
        try
        {
            string source = Path.Combine(workspace, "Source", "Main.csproj");
            string tooling = Path.Combine(workspace, "DevTools", "Inspector.csproj");
            WriteProject(source, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteProject(tooling, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Inspector</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Source/Main.csproj" /></ItemGroup>
                </Project>
                """);

            DevCheckCommand.ProjectSelectionResult selection = DevCheckCommand.SelectProjects(
                workspace,
                [source, tooling]);
            if (selection.IsAmbiguous ||
                selection.BuildRoots.Count != 1 ||
                !selection.BuildRoots.Contains(Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase) ||
                selection.ProductionProjects.Count != 1 ||
                !selection.ProductionProjects.Contains(Path.GetFullPath(source), StringComparer.OrdinalIgnoreCase) ||
                selection.ExcludedProjects.Count != 1 ||
                !selection.ExcludedProjects.Contains(Path.GetFullPath(tooling), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("excluded tooling reference disqualified the production build root.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }


    public static void ProductionProjectBuildFailureIdentifiesOwner()
    {
        string workspace = CreateWorkspace();
        try
        {
            string source = Path.Combine(workspace, "Source", "Main.csproj");
            string library = Path.Combine(workspace, "Libraries", "Broken.csproj");
            WriteProject(source, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Libraries/Broken.csproj" /></ItemGroup>
                </Project>
                """);
            WriteProject(library, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Broken</AssemblyName></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(workspace, "Libraries", "Broken.cs"), "public class Broken { MissingType Value; }");

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.Run(["dev-check", workspace, "--json"], stdout, stderr);
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            string[] errors = result.RootElement.GetProperty("build").GetProperty("errors")
                .EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            if (exitCode != CliExitCodes.TestFailure ||
                result.RootElement.GetProperty("build").GetProperty("status").GetString() != "BUILD_FAIL" ||
                !errors.Any(error => error.Contains("Broken.csproj", StringComparison.OrdinalIgnoreCase) ||
                                     error.Contains("Broken.cs", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("production project compiler failure did not identify its owning project.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    public static void SupportProjectsAreNotProductionAssemblies()
    {
        string workspace = CreateWorkspace();
        try
        {
            string source = Path.Combine(workspace, "Source", "Main.csproj");
            string tests = Path.Combine(workspace, "Tests", "Main.Tests.csproj");
            WriteProject(source, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteProject(tests, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Main.Tests</AssemblyName></PropertyGroup>
                </Project>
                """);

            DevCheckCommand.ProjectSelectionResult selection = DevCheckCommand.SelectProjects(
                workspace,
                [source, tests]);
            if (selection.IsAmbiguous ||
                selection.ProductionProjects.Count != 1 ||
                !selection.ExcludedProjects.Contains(Path.GetFullPath(tests), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("test project was incorrectly required as a production assembly.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    public static void AmbiguousProductionProjectsFailClosed()
    {
        string workspace = CreateWorkspace();
        try
        {
            string first = Path.Combine(workspace, "Source", "First.csproj");
            string second = Path.Combine(workspace, "Source", "Second.csproj");
            WriteProject(first, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>First</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteProject(second, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Second</AssemblyName></PropertyGroup>
                </Project>
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.Run(["dev-check", workspace, "--json"], stdout, stderr);
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            if (exitCode != CliExitCodes.TestFailure ||
                result.RootElement.GetProperty("static").GetProperty("status").GetString() != "STATIC_FAIL" ||
                !result.RootElement.GetProperty("static").GetProperty("errors")
                    .EnumerateArray().Any(error => error.GetString()?.Contains("Multiple unreferenced production projects", StringComparison.Ordinal) == true))
            {
                throw new InvalidOperationException("ambiguous project ownership did not fail closed.");
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    public static void RuntimeProcessLifecycleWaitsForExitAndStreams()
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Test process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        DevCheckCommand.ProcessLifecycleResult result = DevCheckCommand.CompleteProcessAsync(
                process,
                stdout,
                stderr,
                terminateIfRunning: true,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!result.ControlledTermination || !process.HasExited)
            throw new InvalidOperationException("Runtime lifecycle did not terminate and confirm the test process.");
    }
    public static void StagesOnlyExplicitRuntimePayload()
    {
        string source = CreateWorkspace();
        string destination = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(source, "LoadFolders.xml"), "<loadFolders />");
            Directory.CreateDirectory(Path.Combine(source, "1.6", "Assemblies"));
            File.WriteAllText(Path.Combine(source, "1.6", "Assemblies", "Payload.dll"), "payload");
            Directory.CreateDirectory(Path.Combine(source, ".git", "objects"));
            File.WriteAllText(Path.Combine(source, ".git", "objects", "read-only-object"), "repository");
            Directory.CreateDirectory(Path.Combine(source, "Source"));
            File.WriteAllText(Path.Combine(source, "Source", "Payload.cs"), "source");
            File.WriteAllText(Path.Combine(source, "README.md"), "documentation");

            DevCheckCommand.CopyRuntimePayload(source, destination);

            string[] expected =
            [
                "About/About.xml",
                "LoadFolders.xml",
                "1.6/Assemblies/Payload.dll"
            ];
            foreach (string relative in expected)
            {
                if (!File.Exists(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar))))
                    throw new InvalidOperationException("Expected runtime payload was not staged: " + relative);
            }
            string[] forbidden = [".git", "Source", "README.md"];
            foreach (string relative in forbidden)
            {
                if (Directory.Exists(Path.Combine(destination, relative)) || File.Exists(Path.Combine(destination, relative)))
                    throw new InvalidOperationException("Repository-only content was staged: " + relative);
            }
        }
        finally
        {
            DeleteWorkspace(source);
            DeleteWorkspace(destination);
        }
    }

    public static void RuntimeSignalResultPathIsPassedToChildEnvironment()
    {
        string signalPath = Path.Combine(Path.GetTempPath(), "rimtest-signal-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo("RimWorldWin64.exe");
        DevCheckCommand.SetRuntimeSignalEnvironment(start, signalPath);
        if (!start.Environment.TryGetValue("DIRECT_VALIDATION_SIGNAL_RESULT", out string? configured) ||
            !string.Equals(configured, signalPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime signal result path was not passed to the launch environment.");
        }
    }


    public static void DeclaredRequiredDependencyIsIncluded()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["required.mod"]);
            WriteRuntimePackage(runtimeRoot, "RequiredPackage", "required.mod", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "required.mod")
                throw new InvalidOperationException("declared required dependency was not resolved.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }
    public static void NestedFixtureAboutIsNotDiscovered()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["nested.fixture"]);
            WriteRuntimePackageNested(runtimeRoot, "FixtureContainer", "PackageFixture", "nested.fixture");
            try
            {
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
                throw new InvalidOperationException("nested fixture metadata was discovered.");
            }
            catch (InvalidDataException exception) when (
                exception.Message == "REQUIRED_MOD_DEPENDENCY_MISSING: nested.fixture")
            {
            }
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void MalformedNestedFixtureDoesNotAbortDiscovery()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["required.mod"]);
            WriteRuntimePackage(runtimeRoot, "Required", "required.mod", []);
            WriteMalformedRuntimePackageNested(runtimeRoot, "FixtureContainer", "PackageFixture");
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "required.mod")
                throw new InvalidOperationException("malformed nested fixture interfered with dependency discovery.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void MalformedUnrelatedCandidateDoesNotLeakXmlException()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["required.mod"]);
            WriteRuntimePackage(runtimeRoot, "Required", "required.mod", []);
            WriteMalformedRuntimePackage(runtimeRoot, "Malformed");
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "required.mod")
                throw new InvalidOperationException("malformed unrelated candidate interfered with dependency discovery.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void HarmonyResolvesByPackageId()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["brrainz.harmony"]);
            WriteRuntimePackage(runtimeRoot, "InstalledHarmony", "brrainz.harmony", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            if (dependencies.Count != 1 ||
                dependencies[0].PackageId != "brrainz.harmony" ||
                !dependencies[0].RootPath.EndsWith("InstalledHarmony", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Harmony was not resolved by its package ID.");
            }
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void MissingRequiredDependencyFailsClosed()
    {
        string target = CreateWorkspace();
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["missing.required"]);
            try
            {
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    []);
                throw new InvalidOperationException("missing required dependency did not fail closed.");
            }
            catch (InvalidDataException exception) when (
                exception.Message == "REQUIRED_MOD_DEPENDENCY_MISSING: missing.required")
            {
            }
        }
        finally
        {
            DeleteWorkspace(target);
        }
    }

    public static void DependencyCyclesAndDuplicatesAreBounded()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["cycle.a", "cycle.a"]);
            WriteRuntimePackage(runtimeRoot, "A", "cycle.a", ["cycle.b"]);
            WriteRuntimePackage(runtimeRoot, "B", "cycle.b", ["cycle.a"]);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            string[] active = DevCheckCommand.BuildActiveModPackageIds("target.mod", dependencies).ToArray();
            if (active.Length != active.Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
                !active.SequenceEqual(
                    ["ludeon.rimworld", "cycle.b", "cycle.a", "direct.validation.runtime.signal", "target.mod"],
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("dependency cycle or duplicate produced an invalid active-mod list.");
            }
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void UnrelatedInstalledModsAreExcluded()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(
                Path.Combine(target, "About", "About.xml"),
                "target.mod",
                ["required.mod"]);
            WriteRuntimePackage(runtimeRoot, "Required", "required.mod", []);
            WriteRuntimePackage(runtimeRoot, "Unrelated", "unrelated.mod", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"),
                    [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "required.mod")
                throw new InvalidOperationException("unrelated installed mod entered the isolated dependency closure.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    private static string WriteRuntimePackage(
        string runtimeRoot,
        string directoryName,
        string packageId,
        IReadOnlyList<string> dependencies)
    {
        string packageRoot = Path.Combine(runtimeRoot, directoryName);
        Directory.CreateDirectory(Path.Combine(packageRoot, "About"));
        WriteAbout(Path.Combine(packageRoot, "About", "About.xml"), packageId, dependencies);
        return packageRoot;
    }
    private static string WriteRuntimePackageNested(
        string runtimeRoot,
        string containerName,
        string directoryName,
        string packageId)
    {
        string packageRoot = Path.Combine(runtimeRoot, containerName, directoryName);
        Directory.CreateDirectory(Path.Combine(packageRoot, "About"));
        WriteAbout(Path.Combine(packageRoot, "About", "About.xml"), packageId, []);
        return packageRoot;
    }

    private static string WriteMalformedRuntimePackageNested(
        string runtimeRoot,
        string containerName,
        string directoryName)
    {
        string packageRoot = Path.Combine(runtimeRoot, containerName, directoryName, "About");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "About.xml"), "about\r\n");
        return packageRoot;
    }

    private static string WriteMalformedRuntimePackage(string runtimeRoot, string directoryName)
    {
        string packageRoot = Path.Combine(runtimeRoot, directoryName, "About");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "About.xml"), "about\r\n");
        return packageRoot;
    }

    private static void WriteAbout(string path, string packageId, IReadOnlyList<string> dependencies)
    {
        string dependencyXml = dependencies.Count == 0
            ? string.Empty
            : "<modDependencies>" + string.Join(
                string.Empty,
                dependencies.Select(value => "<li><packageId>" + value + "</packageId><displayName>not-id</displayName></li>")) +
              "</modDependencies>";
        File.WriteAllText(
            path,
            "<ModMetaData><name>Test Mod</name><packageId>" + packageId + "</packageId>" +
            dependencyXml + "</ModMetaData>");
    }

    private static string CreateWorkspace()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "About"));
        File.WriteAllText(
            Path.Combine(workspace, "About", "About.xml"),
            "<ModMetaData><name>Test Mod</name><packageId>test.mod</packageId></ModMetaData>");
        return workspace;
    }

    private static void WriteProject(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }
}
