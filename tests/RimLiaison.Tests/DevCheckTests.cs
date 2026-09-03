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
