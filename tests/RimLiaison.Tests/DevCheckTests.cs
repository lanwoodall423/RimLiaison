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

    public static void WaylithOnlyLayoutRejectsLegacyAndPartialGame()
    {
        string root = CreateWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            Directory.CreateDirectory(Path.Combine(root, "Mods"));
            File.WriteAllText(Path.Combine(root, "RimWorldWin64.exe"), "inert");
            try
            {
                DevCheckCommand.ValidateWaylithGameRoot(root);
                throw new InvalidOperationException("1.6 executable was accepted as Waylith.");
            }
            catch (InvalidDataException) { }
            File.Delete(Path.Combine(root, "RimWorldWin64.exe"));
            foreach (string name in new[]
                     { "RimWorld.exe", "Assembly-CSharp.dll", "Waylith.dll", "UnityStubs.dll" })
                File.WriteAllText(Path.Combine(root, name), "inert");
            if (DevCheckCommand.ValidateWaylithGameRoot(root) != Path.GetFullPath(root))
                throw new InvalidOperationException("Complete 1.7 layout was not accepted.");
            File.Delete(Path.Combine(root, "Waylith.dll"));
            try
            {
                DevCheckCommand.ValidateWaylithGameRoot(root);
                throw new InvalidOperationException("Partial Waylith root was accepted.");
            }
            catch (FileNotFoundException) { }
            File.WriteAllText(Path.Combine(root, "Waylith.dll"), "inert");
            File.WriteAllText(Path.Combine(root, "RimWorldWin64.exe"), "inert");
            try
            {
                DevCheckCommand.ValidateWaylithGameRoot(root);
                throw new InvalidOperationException("Mixed Waylith and Unity root was accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    public static void WaylithLaunchIsIsolatedAndHasBoundedStartup()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimtest-waylith-fixture");
        string savedata = Path.Combine(root, "owned-test-profile");
        ProcessStartInfo start = DevCheckCommand.CreateWaylithRuntimeStartInfo(
            root, savedata,
            ["ludeon.rimworld", "required.mod", "direct.validation.runtime.signal", "target.mod",
             "target.mod"]);
        if (start.FileName != Path.Combine(root, "RimWorld.exe") ||
            !start.ArgumentList.Contains("-savedatafolder=" + savedata) ||
            !start.ArgumentList.Contains("-mod=required.mod;direct.validation.runtime.signal;target.mod") ||
            !start.ArgumentList.Contains("-Headless") ||
            start.ArgumentList.Contains("-logfile") ||
            !DevCheckCommand.IsWaylithTargetSelected(start, "target.mod") ||
            DevCheckCommand.IsWaylithTargetSelected(start, "other.mod") ||
            !start.RedirectStandardOutput || !start.RedirectStandardError ||
            DevCheckCommand.WaylithContentLoadTimeout != TimeSpan.FromSeconds(120))
            throw new InvalidOperationException("Waylith launch lost isolated profile, mod identity or budget.");

        try
        {
            DevCheckCommand.CreateWaylithRuntimeStartInfo(
                root, savedata, ["ludeon.rimworld", "target.mod"]);
            throw new InvalidOperationException("Waylith start accepted a missing helper.");
        }
        catch (InvalidDataException) { }
    }

    public static void WaylithHelperSdkSelectorStaysInsideFixture()
    {
        string root = CreateWorkspace();
        try
        {
            string unrelatedSource = Path.Combine(root, "source");
            string ownedStaging = Path.Combine(root, "staging");
            Directory.CreateDirectory(unrelatedSource);
            Directory.CreateDirectory(ownedStaging);
            string working = DevCheckCommand.PrepareWaylithHelperBuildDirectory(ownedStaging);
            using JsonDocument json = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(ownedStaging, "global.json")));
            JsonElement sdk = json.RootElement.GetProperty("sdk");
            if (working != ownedStaging ||
                sdk.GetProperty("version").GetString() != "10.0.100" ||
                sdk.GetProperty("rollForward").GetString() != "latestFeature" ||
                sdk.GetProperty("allowPrerelease").GetBoolean() ||
                File.Exists(Path.Combine(unrelatedSource, "global.json")))
                throw new InvalidOperationException("Private Waylith SDK selection escaped staging.");
            try
            {
                DevCheckCommand.PrepareWaylithHelperBuildDirectory(
                    Path.Combine(root, "absent"));
                throw new InvalidOperationException("Missing SDK staging was created implicitly.");
            }
            catch (DirectoryNotFoundException) { }
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    // The CLI solution build intentionally copies this source as Content rather
    // than compiling it. This game-free regression proves the real net10 helper
    // compiles with the separately pinned Waylith reference assemblies.
    public static void WaylithRuntimeHelperCompilesAgainstPinnedReferences()
    {
        string? sourceRoot = Path.GetFullPath(AppContext.BaseDirectory);
        string? helperProject = null;
        string? helperSource = null;
        for (int depth = 0; depth < 10 && sourceRoot is not null; depth++)
        {
            string candidate = Path.Combine(sourceRoot, "src", "RimLiaison.Cli", "DevCheckRuntime");
            string project = Path.Combine(candidate, "DirectValidationRuntimeSignal.csproj");
            string source = Path.Combine(candidate, "RuntimeSignal.cs");
            if (File.Exists(project) && File.Exists(source))
            {
                helperProject = project;
                helperSource = source;
                break;
            }
            sourceRoot = Directory.GetParent(sourceRoot)?.FullName;
        }
        if (helperProject is null || helperSource is null)
            throw new InvalidOperationException("Tracked standalone Waylith helper source was not found.");

        static void RequirePinnedHelper(string project, string source)
        {
            if (!File.Exists(project) || !File.Exists(source) || new FileInfo(source).Length == 0)
                throw new InvalidDataException("Standalone Waylith helper inputs are missing.");
            var document = System.Xml.Linq.XDocument.Load(project);
            string? framework = document.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "TargetFramework")?.Value.Trim();
            string? outputType = document.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "OutputType")?.Value.Trim();
            var pins = document.Descendants()
                .Where(x => x.Name.LocalName == "PackageReference" &&
                    (string?)x.Attribute("Include") == "Krafs.Rimworld.Ref")
                .ToArray();
            if (framework != "net10.0" || outputType != "Library" ||
                pins.Length != 1 || (string?)pins[0].Attribute("Version") != "1.7.4990-unstable")
                throw new InvalidDataException("Standalone Waylith helper version or references drifted.");
        }

        RequirePinnedHelper(helperProject, helperSource);
        string staging = Path.Combine(Path.GetTempPath(),
            "rimliaison-waylith-helper-compile-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ownedSource = Path.Combine(staging, "helper");
            Directory.CreateDirectory(ownedSource);
            string project = Path.Combine(ownedSource, "DirectValidationRuntimeSignal.csproj");
            string source = Path.Combine(ownedSource, "RuntimeSignal.cs");
            File.Copy(helperProject, project);
            File.Copy(helperSource, source);
            RequirePinnedHelper(project, source);
            string working = DevCheckCommand.PrepareWaylithHelperBuildDirectory(staging);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = working,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "build", project, "--configuration", "Release", "--nologo",
                "-p:DevCheckRuntimePackageRoot=" + staging + Path.DirectorySeparatorChar
            })
                start.ArgumentList.Add(argument);
            start.Environment["DOTNET_CLI_HOME"] = staging;
            start.Environment["DOTNET_NOLOGO"] = "1";
            using Process child = Process.Start(start) ??
                throw new InvalidOperationException("Pinned Waylith helper compiler process did not start.");
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(150_000))
            {
                child.Kill(entireProcessTree: true);
                if (!child.WaitForExit(10_000))
                    throw new InvalidOperationException("Waylith helper compiler process exit not confirmed.");
                throw new InvalidOperationException("Waylith helper compilation exceeded its timeout.");
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (child.ExitCode != 0)
            {
                string message = (output.Result + Environment.NewLine + error.Result);
                throw new InvalidOperationException("Pinned Waylith helper compilation failed: " +
                    message[..Math.Min(message.Length, 1800)]);
            }
            string assembly = Path.Combine(staging, "1.7", "Assemblies",
                "DirectValidationRuntimeSignal.dll");
            if (!File.Exists(assembly) || new FileInfo(assembly).Length < 2)
                throw new InvalidOperationException("Pinned Waylith compiler produced no runtime helper DLL.");
            using (FileStream compiled = File.OpenRead(assembly))
            {
                if (compiled.ReadByte() != 'M' || compiled.ReadByte() != 'Z')
                    throw new InvalidOperationException("Pinned Waylith helper output is not a PE assembly.");
            }

            // The input guards are exercised on disposable copies; no game files.
            string tampered = File.ReadAllText(project)
                .Replace("1.7.4990-unstable", "1.6.4871", StringComparison.Ordinal);
            File.WriteAllText(project, tampered);
            try
            {
                RequirePinnedHelper(project, source);
                throw new InvalidOperationException("Wrong Waylith reference version was accepted.");
            }
            catch (InvalidDataException) { }
            File.Delete(source);
            try
            {
                RequirePinnedHelper(project, source);
                throw new InvalidOperationException("Missing helper source was accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally
        {
            DeleteWorkspace(staging);
        }
    }

    public static void WaylithLogMustMatchExactFreshInvocation()
    {
        string root = CreateWorkspace();
        try
        {
            string logPath = Path.Combine(root, "Logs", "Player.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            string savedata = Path.Combine(root, "rimtest-dev-check-savedata-unique-123456");
            ProcessStartInfo start = DevCheckCommand.CreateWaylithRuntimeStartInfo(
                root, savedata,
                ["ludeon.rimworld", "direct.validation.runtime.signal", "target.mod"]);
            DateTimeOffset began = DateTimeOffset.UtcNow.AddSeconds(-5);
            DateTimeOffset logStarted = began.AddSeconds(1);
            DateTimeOffset exited = began.AddSeconds(10);
            string header = "Started:   " +
                logStarted.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz",
                    System.Globalization.CultureInfo.InvariantCulture) +
                "\nVersion:   1.7.4990.1069\n" +
                "Log file:  " + logPath + "\n" +
                "Command:   " + Path.Combine(root, "RimWorld.dll") + "\n" +
                "-savedatafolder=" + savedata + "\n" +
                "-mod=direct.validation.runtime.signal;target.mod -Headless\n" +
                "Other diagnostic output follows.\n";

            if (!DevCheckCommand.VerifyWaylithLogHeader(
                    header, logPath, root, start, began, exited, out string reason) ||
                reason != "WAYLITH_LOG_VERIFIED")
                throw new InvalidOperationException("Valid isolated Waylith log header rejected.");

            void Reject(string altered, string label, DateTimeOffset? alteredBegan = null)
            {
                if (DevCheckCommand.VerifyWaylithLogHeader(
                    altered, logPath, root, start, alteredBegan ?? began,
                    exited, out _))
                    throw new InvalidOperationException("Invalid Waylith " + label + " log accepted.");
            }
            Reject(header.Replace("unique-123456", "other-run-789"), "wrong run ID");
            Reject(header.Replace("target.mod -Headless", "other.mod -Headless"), "wrong mods");
            Reject(header.Replace("1.7.4990.1069", "1.6.4990.1069"), "wrong version");
            Reject(header.Replace("Log file:  " + logPath, "Log file:  C:\\unrelated.log"), "wrong log path");
            Reject(header.Replace("-Headless", ""), "nonheadless");
            Reject(header, "stale timestamp", began.AddMinutes(1));

            File.WriteAllText(logPath, "old log");
            DevCheckCommand.WaylithLogSnapshot prior =
                DevCheckCommand.CaptureWaylithLogSnapshot(logPath);
            File.WriteAllText(logPath, header);
            if (!DevCheckCommand.TryReadVerifiedWaylithLog(
                    prior, root, start, began, exited, out string verified,
                    out string verifiedReason) ||
                verified != header || verifiedReason != "WAYLITH_LOG_VERIFIED")
                throw new InvalidOperationException("Fresh matched global Waylith log was rejected.");

            DevCheckCommand.WaylithLogSnapshot now =
                DevCheckCommand.CaptureWaylithLogSnapshot(logPath);
            if (DevCheckCommand.TryReadVerifiedWaylithLog(
                    now, root, start, began, exited, out _, out string unchanged) ||
                unchanged != "WAYLITH_LOG_UNCHANGED")
                throw new InvalidOperationException("Unchanged Waylith log was accepted.");
            File.Delete(logPath);
            if (DevCheckCommand.TryReadVerifiedWaylithLog(
                    prior, root, start, began, exited, out _, out _))
                throw new InvalidOperationException("Missing Waylith log was accepted.");
        }
        finally
        {
            DeleteWorkspace(root);
        }
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
            Directory.CreateDirectory(Path.Combine(source, "1.7", "Assemblies"));
            File.WriteAllText(Path.Combine(source, "1.7", "Assemblies", "Payload.dll"), "payload");
            Directory.CreateDirectory(Path.Combine(source, "1.6", "Defs"));
            File.WriteAllText(Path.Combine(source, "1.6", "Defs", "Legacy.xml"), "<Defs />");
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
                "1.7/Assemblies/Payload.dll"
            ];
            foreach (string relative in expected)
            {
                if (!File.Exists(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar))))
                    throw new InvalidOperationException("Expected runtime payload was not staged: " + relative);
            }
            string[] forbidden = [".git", "Source", "README.md", "1.6"];
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
        VerifyWaylithTypedDefinitionFixtures();
    }

    // This method is called from an already registered regression case:
    // no suite/Program edits, game launch, SDK execution or host fixtures.
    private static void VerifyWaylithTypedDefinitionFixtures()
    {
        string root = CreateWorkspace();
        try
        {
            string activeDirectory = Path.Combine(root, "1.7", "Defs", "GenStepDefs");
            string plainDirectory = Path.Combine(root, "Defs", "GenStepDefs");
            string legacyDirectory = Path.Combine(root, "1.6", "Defs");
            Directory.CreateDirectory(activeDirectory);
            Directory.CreateDirectory(plainDirectory);
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(activeDirectory, "NatureTiles.xml"), """
                <Defs>
                  <ThingDef><defName>NT_Bramble</defName></ThingDef>
                  <GenStepDef><defName>NT_BrambleThickets</defName></GenStepDef>
                  <TileMutatorDef MayRequire="Ludeon.RimWorld.Odyssey">
                    <defName>NT_BrambleThicketMutator</defName>
                  </TileMutatorDef>
                </Defs>
                """);
            File.WriteAllText(Path.Combine(plainDirectory, "Unversioned.xml"), """
                <Defs><GenStepDef><defName>UnversionedStep</defName></GenStepDef></Defs>
                """);
            File.WriteAllText(Path.Combine(legacyDirectory, "Old.xml"), """
                <Defs><ThingDef><defName>InactiveLegacyPlant</defName></ThingDef></Defs>
                """);

            IReadOnlyList<string> expected = DevCheckCommand.InspectActiveDefinitionExpectations(root);
            string[] names =
            [
                "ThingDef|NT_Bramble",
                "GenStepDef|NT_BrambleThickets",
                "TileMutatorDef|NT_BrambleThicketMutator|Ludeon.RimWorld.Odyssey",
                "GenStepDef|UnversionedStep"
            ];
            if (expected.Count != names.Length ||
                names.Any(item => !expected.Contains(item, StringComparer.Ordinal)) ||
                expected.Any(item => item.Contains("InactiveLegacyPlant", StringComparison.Ordinal)))
                throw new InvalidOperationException("Versioned and unversioned typed definitions were not isolated correctly.");

            const string positive = """
                {"requestedDefs":[
                  {"defType":"ThingDef","defName":"NT_Bramble","applicable":true,"exists":true},
                  {"defType":"GenStepDef","defName":"NT_BrambleThickets","applicable":true,"exists":true},
                  {"defType":"TileMutatorDef","defName":"NT_BrambleThicketMutator","applicable":true,"exists":true},
                  {"defType":"GenStepDef","defName":"UnversionedStep","applicable":true,"exists":true}
                ]}
                """;
            string withoutOdyssey = positive.Replace(
                "\"defType\":\"TileMutatorDef\",\"defName\":\"NT_BrambleThicketMutator\",\"applicable\":true,\"exists\":true",
                "\"defType\":\"TileMutatorDef\",\"defName\":\"NT_BrambleThicketMutator\",\"applicable\":false,\"exists\":false",
                StringComparison.Ordinal);
            string[] noOdyssey = ["ludeon.rimworld", "target.mod"];
            string[] withOdyssey = ["ludeon.rimworld", "Ludeon.RimWorld.Odyssey", "target.mod"];
            bool Proves(string json, string[] packages)
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return DevCheckCommand.ValidateTypedDefinitionEvidence(
                    expected, document.RootElement, packages);
            }

            if (!Proves(positive, withOdyssey) ||
                !Proves(withoutOdyssey, noOdyssey) ||
                Proves(withoutOdyssey, withOdyssey) ||
                Proves(positive, noOdyssey))
                throw new InvalidOperationException("Optional Odyssey definition proof was not matched to the selected profile.");

            void MustReject(string altered, string label)
            {
                if (Proves(altered, withOdyssey))
                    throw new InvalidOperationException("Invalid typed-Def evidence accepted: " + label);
            }
            MustReject("{}", "missing results");
            MustReject("""{"requestedDefs":[]}""", "empty results");
            MustReject(positive.Replace("\"defType\":\"GenStepDef\",\"defName\":\"NT_BrambleThickets\"",
                "\"defType\":\"ThingDef\",\"defName\":\"NT_BrambleThickets\"", StringComparison.Ordinal), "wrong type");
            MustReject(positive.Replace("\"defName\":\"NT_BrambleThickets\"",
                "\"defName\":\"AbsentGenStep\"", StringComparison.Ordinal), "missing GenStep");
            MustReject(positive.Replace("\"defName\":\"NT_BrambleThickets\",\"applicable\":true,\"exists\":true",
                "\"defName\":\"NT_BrambleThickets\",\"applicable\":true,\"exists\":false", StringComparison.Ordinal), "false existence");
            MustReject(positive.Replace("{\"defType\":\"GenStepDef\",\"defName\":\"NT_BrambleThickets\",\"applicable\":true,\"exists\":true},",
                "", StringComparison.Ordinal), "missing one result");
            MustReject(positive.Replace("\"defName\":\"NT_BrambleThickets\",\"applicable\":true",
                "\"defName\":\"NT_BrambleThickets\"", StringComparison.Ordinal), "missing applicability");
            MustReject(positive.Replace("\"defName\":\"NT_BrambleThickets\",\"applicable\":true",
                "\"defName\":\"NT_BrambleThickets\",\"applicable\":\"true\"", StringComparison.Ordinal), "invalid applicability type");
            MustReject(positive.Replace("\"defName\":\"UnversionedStep\"",
                "\"defName\":\"NT_BrambleThickets\"", StringComparison.Ordinal), "duplicate result");
            if (!DevCheckCommand.ValidateTypedDefinitionEvidence([], null, noOdyssey))
                throw new InvalidOperationException("Legitimate no-Def mod was rejected.");

            string duplicate = Path.Combine(activeDirectory, "Duplicate.xml");
            File.WriteAllText(duplicate, """
                <Defs><ThingDef><defName>NT_Bramble</defName></ThingDef></Defs>
                """);
            bool rejectedDuplicate = false;
            try { DevCheckCommand.InspectActiveDefinitionExpectations(root); }
            catch (InvalidDataException) { rejectedDuplicate = true; }
            if (!rejectedDuplicate)
                throw new InvalidOperationException("Duplicate active definitions did not fail static discovery.");
        }
        finally
        {
            DeleteWorkspace(root);
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
    public static void RootPackageIdWinsOverEarlierDependencyId()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(Path.Combine(target, "About", "About.xml"), "target.mod", ["required.mod"]);
            WriteRuntimePackageWithDependencyBeforeIdentity(
                runtimeRoot, "Required", "Ordinary Required Mod", "required.mod", ["unrelated.mod"]);
            WriteRuntimePackage(runtimeRoot, "Unrelated", "unrelated.mod", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"), [runtimeRoot]);
            if (dependencies.Count != 2 ||
                !dependencies.Any(dependency => dependency.PackageId == "required.mod"))
                throw new InvalidOperationException("the direct root package ID was not used.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void HarmonyDependencyDoesNotMasqueradeAsAnotherMod()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(Path.Combine(target, "About", "About.xml"), "target.mod", ["brrainz.harmony"]);
            WriteRuntimePackage(runtimeRoot, "Harmony", "brrainz.harmony", []);
            WriteRuntimePackageWithDependencyBeforeIdentity(
                runtimeRoot, "ArchitectIcons", "Architect Icons", "com.bymarcin.ArchitectIcons", ["brrainz.harmony"]);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"), [runtimeRoot]);
            if (dependencies.Count != 1 ||
                dependencies[0].PackageId != "brrainz.harmony" ||
                !dependencies[0].RootPath.EndsWith("Harmony", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("a Harmony dependency was mistaken for another package identity.");
            }
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void ActualHarmonyMetadataResolvesAsHarmony()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(Path.Combine(target, "About", "About.xml"), "target.mod", ["brrainz.harmony"]);
            WriteRuntimePackageWithDependencyBeforeIdentity(
                runtimeRoot, "InstalledHarmony", "Harmony", "brrainz.harmony", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"), [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "brrainz.harmony")
                throw new InvalidOperationException("actual Harmony metadata did not resolve as brrainz.harmony.");
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void MissingRootPackageIdDoesNotFallbackToDependencyId()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(Path.Combine(target, "About", "About.xml"), "target.mod", ["brrainz.harmony"]);
            WriteRuntimePackageWithoutRootPackageId(
                runtimeRoot, "Broken", "Broken Mod", ["brrainz.harmony"]);
            try
            {
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"), [runtimeRoot]);
                throw new InvalidOperationException("a dependency package ID became a fallback package identity.");
            }
            catch (InvalidDataException exception) when (
                exception.Message == "REQUIRED_MOD_DEPENDENCY_MISSING: brrainz.harmony")
            {
            }
        }
        finally
        {
            DeleteWorkspace(target);
            DeleteWorkspace(runtimeRoot);
        }
    }

    public static void OrdinaryRootMetadataStillParses()
    {
        string target = CreateWorkspace();
        string runtimeRoot = Path.Combine(Path.GetTempPath(), "rimtest-runtime-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAbout(Path.Combine(target, "About", "About.xml"), "target.mod", ["ordinary.mod"]);
            WriteRuntimePackageWithDependencyBeforeIdentity(
                runtimeRoot, "Ordinary", "Ordinary Mod", "ordinary.mod", []);
            IReadOnlyList<DevCheckCommand.RuntimeModPackage> dependencies =
                DevCheckCommand.ResolveRequiredRuntimeDependencies(
                    Path.Combine(target, "About", "About.xml"), [runtimeRoot]);
            if (dependencies.Count != 1 || dependencies[0].PackageId != "ordinary.mod")
                throw new InvalidOperationException("ordinary root About metadata did not parse.");
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
    private static string WriteRuntimePackageWithDependencyBeforeIdentity(
        string runtimeRoot,
        string directoryName,
        string name,
        string packageId,
        IReadOnlyList<string> dependencies)
    {
        string packageRoot = Path.Combine(runtimeRoot, directoryName);
        Directory.CreateDirectory(Path.Combine(packageRoot, "About"));
        string dependencyXml = dependencies.Count == 0
            ? string.Empty
            : "<modDependencies>" + string.Join(
                string.Empty,
                dependencies.Select(value => "<li><packageId>" + value + "</packageId><displayName>dependency</displayName></li>")) +
              "</modDependencies>";
        File.WriteAllText(
            Path.Combine(packageRoot, "About", "About.xml"),
            "<ModMetaData><name>" + name + "</name>" + dependencyXml +
            "<packageId>" + packageId + "</packageId></ModMetaData>");
        return packageRoot;
    }

    private static string WriteRuntimePackageWithoutRootPackageId(
        string runtimeRoot,
        string directoryName,
        string name,
        IReadOnlyList<string> dependencies)
    {
        string packageRoot = Path.Combine(runtimeRoot, directoryName);
        Directory.CreateDirectory(Path.Combine(packageRoot, "About"));
        string dependencyXml = "<modDependencies>" + string.Join(
            string.Empty,
            dependencies.Select(value => "<li><packageId>" + value + "</packageId><displayName>dependency</displayName></li>")) +
          "</modDependencies>";
        File.WriteAllText(
            Path.Combine(packageRoot, "About", "About.xml"),
            "<ModMetaData><name>" + name + "</name>" + dependencyXml + "</ModMetaData>");
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
