using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class RecipePropagationTests
{
    public static void ProjectRecipeReachesDirectRecipeCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-recipe-propagation-" + Guid.NewGuid().ToString("N"));
        string recipePath = Path.Combine(root, ".rimdev", "recipes", "fixture-anchor.json");
        string previousDirectory = Environment.CurrentDirectory;
        string? previousRimDevRoot = Environment.GetEnvironmentVariable("RIMDEV_ROOT");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recipePath)!);
            string gameRoot = Path.Combine(root, "RimWorld");
            string modsRoot = Path.Combine(gameRoot, "Mods");
            Directory.CreateDirectory(modsRoot);
            File.WriteAllText(
                Path.Combine(root, ".rimdev", "workspace.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimdev-workspace/v1",
                    rimWorldRoot = gameRoot,
                    activeModsRoot = modsRoot,
                    devBridgeRuntimeRoot = Path.Combine(modsRoot, "DevBridge2"),
                    repositories = new[] { new { path = "." } },
                    packageMappings = new { }
                }),
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(root, ".rimdev", "stack.json"),
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"FixtureProject\",\"devBridgeProject\":\"fixture-project\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\",\"workload\":\"fixture\",\"projectType\":\"rimworld-content-mod\",\"packageId\":\"fixture.project\",\"sourceProject\":\"Source/Fixture.csproj\",\"configuration\":\"Release\",\"expectedAssembly\":\"Fixture.dll\",\"deploymentTarget\":\"Fixture.dll\",\"testRecipe\":\"fixture-anchor\",\"testRecipePath\":\".rimdev/recipes/fixture-anchor.json\",\"runtimePackage\":{\"sourceRoot\":\".\",\"include\":[\"About/**\",\"1.*/**\"],\"exclude\":[\".rimdev/**\",\"Source/**\",\"bin/**\",\"obj/**\"]},\"dependencies\":[]}",
                Encoding.UTF8);
            File.WriteAllText(
                recipePath,
                "{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture-anchor\",\"description\":\"Fixture recipe\",\"projects\":[\"fixture-project\"],\"inputs\":{},\"requiresReady\":false,\"success\":{\"quicktestReady\":true}}",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "DevBridge.cmd"), string.Empty, Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(root, "About"));
            Directory.CreateDirectory(Path.Combine(root, "Source"));
            File.WriteAllText(
                Path.Combine(root, "About", "About.xml"),
                "<ModMetaData><packageId>fixture.project</packageId></ModMetaData>",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(root, "Source", "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>",
                Encoding.UTF8);
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "fixture@example.test");
            RunGit(root, "config", "user.name", "Fixture");
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "fixture");
            var stack = RimLiaison.Stack.StackManifestResolver.Discover(root);
            Assert(stack.Manifest is not null,
                $"fixture stack did not resolve: {stack.ErrorCode} {stack.Error}");
            Assert(string.Equals(stack.Manifest.Workload, "fixture", StringComparison.Ordinal),
                "recording transport scenario must be a nonproduction fixture");

            var transport = new RecordingTransport();
            Environment.SetEnvironmentVariable("RIMDEV_ROOT", root);
            Environment.CurrentDirectory = root;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    ["recipe", "show", "fixture-anchor", "--json", "--experimental", "--devbridge-root", root],
                    stdout,
                    stderr,
                    processTransport: transport)
                .GetAwaiter()
                .GetResult();

            Assert(exitCode == 0, $"explicit recipe show failed: stdout={stdout}; stderr={stderr}");
            DevBridgeProcessRequest request = transport.Requests.Single();
            int optionIndex = request.Arguments
                .Select((argument, index) => (argument, index))
                .Where(value => string.Equals(value.argument, "--recipe-file", StringComparison.Ordinal))
                .Select(value => value.index)
                .DefaultIfEmpty(-1)
                .First();
            Assert(optionIndex >= 0 && optionIndex + 1 < request.Arguments.Count,
                "direct recipe command omitted --recipe-file: " +
                string.Join("|", request.Arguments));
            AssertEqual(Path.GetFullPath(recipePath), request.Arguments[optionIndex + 1],
                "direct recipe command path");
            Assert(!Directory.Exists(Path.Combine(root, "TestRecipes")),
                "regression fixture unexpectedly supplied a central recipe catalog");
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("RIMDEV_ROOT", previousRimDevRoot);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
    private static void RunGit(string root, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start git fixture command.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Git fixture command failed: {string.Join(" ", arguments)} {process.StandardError.ReadToEnd()}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
    }

    private sealed class RecordingTransport : IDevBridgeProcessTransport
    {
        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new DevBridgeProcessResult(
                0,
                "{\"schemaVersion\":\"devbridge-test-recipe-show/v1\",\"recipe\":{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture-anchor\"},\"errorCode\":null,\"error\":null}",
                string.Empty));
        }
    }
}
