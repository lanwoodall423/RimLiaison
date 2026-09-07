using System.Text;
using System.Diagnostics;
using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class ArtifactFreshnessAnchorTests
{
    public static void StaleAnchorBlocksRecipeExecution()
    {
        RunSuiteWithFreshness(
            builtHash: Hash('a'),
            deployedHash: Hash('b'),
            expectedExitCode: 10,
            expectedRecipeCalls: 0,
            expectedStatus: "infrastructure");
    }

    public static void FreshAnchorAvoidsUnnecessaryRedeploymentAndRunsRecipe()
    {
        RunSuiteWithFreshness(
            builtHash: Hash('a'),
            deployedHash: Hash('a'),
            expectedExitCode: 0,
            expectedRecipeCalls: 1,
            expectedStatus: "pass");
    }

    private static void RunSuiteWithFreshness(
        string builtHash,
        string deployedHash,
        int expectedExitCode,
        int expectedRecipeCalls,
        string expectedStatus)
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".rimdev"));
        string catalogPath = Path.Combine(root, "catalog.json");
        string stackPath = Path.Combine(root, ".rimdev", "stack.json");
        File.WriteAllText(
            stackPath,
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"FixtureProject\",\"devBridgeProject\":\"fixture-project\",\"catalog\":\"catalog.json\",\"rimBridge\":\"via-devbridge\",\"workload\":\"production\",\"projectType\":\"rimworld-content-mod\",\"packageId\":\"fixture.project\",\"sourceProject\":\"Fixture.csproj\",\"configuration\":\"Release\",\"expectedAssembly\":\"Fixture.dll\",\"deploymentTarget\":\"Fixture.dll\",\"testRecipe\":\"fixture-anchor\",\"testRecipePath\":\"recipe.json\",\"runtimePackage\":{\"sourceRoot\":\".\",\"include\":[],\"exclude\":[]},\"dependencies\":[]}",
            Encoding.UTF8);
        File.WriteAllText(
            catalogPath,
            "{\"schemaVersion\":\"rimtest-catalog/v1\",\"tests\":[{\"id\":\"fixture-anchor-test\",\"recipe\":\"fixture-anchor\",\"artifactFreshnessAnchor\":true}],\"suites\":[{\"id\":\"release\",\"tests\":[\"fixture-anchor-test\"]}]}",
            Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(root, "Source"));
        File.WriteAllText(Path.Combine(root, "Source", "Fixture.cs"), "class Fixture {}", Encoding.UTF8);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "fixture@example.test");
        RunGit(root, "config", "user.name", "Fixture");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "fixture");

        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var development = new ScriptedDevelopmentAdapter((sourceFingerprint, workflowId) =>
                new DevBridgeModDevelopmentResult(
                    "fixture-project",
                    new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                    true,
                    "tx-fixture",
                    workflowId,
                    1,
                    "lease-fixture",
                    new DevBridgeArtifactFreshness(
                        sourceFingerprint,
                        builtHash,
                        deployedHash,
                        builtHash == deployedHash ? "unchanged" : "deployed",
                        0,
                        1,
                        1,
                        true,
                        "fixture-proof",
                        "tx-fixture",
                        workflowId,
                        "lease-fixture")));
            var recipe = new CountingRecipeAdapter();
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "suite", "run", "release", "--json",
                        "--catalog", catalogPath,
                        "--devbridge-project", "fixture-project",
                        "--devbridge-root", root
                    ],
                    stdout,
                    stderr,
                    recipeAdapter: recipe,
                    developmentAdapter: development,
                    processTransport: new NoopProcessTransport())
                .GetAwaiter()
                .GetResult();

            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            if (exitCode != expectedExitCode)
            {
                throw new InvalidOperationException(
                    $"freshness anchor exit code: expected {expectedExitCode}, got {exitCode}; stdout={stdout}; stderr={stderr}");
            }
            AssertEqual(expectedStatus, result.RootElement.GetProperty("status").GetString(), "freshness anchor status");

            AssertEqual(expectedRecipeCalls, recipe.RunCalls, "recipe execution count");
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            DeleteTree(root);
        }
    }

    private static void DeleteTree(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
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
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start git fixture command.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git fixture command failed: {string.Join(" ", arguments)} {process.StandardError.ReadToEnd()}");
        }
    }

    private static string Hash(char value) => new(value, 64);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}.");
    }

    private sealed class ScriptedDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
    {
        private readonly Func<string, string?, DevBridgeModDevelopmentResult> factory;

        public ScriptedDevelopmentAdapter(Func<string, string?, DevBridgeModDevelopmentResult> factory) =>
            this.factory = factory;

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(sourceFingerprint, workflowId));
    }

    private sealed class CountingRecipeAdapter : IDevBridgeRecipeAdapter
    {
        public int RunCalls { get; private set; }

        public Task<DevBridgeRecipeShowResult> ShowAsync(string recipeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevBridgeRecipeShowResult(recipeId, new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success), null));

        public Task<DevBridgeRecipePlanResult> PlanAsync(string recipeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DevBridgeRecipePlanResult(recipeId, new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success), null));

        public Task<DevBridgeRecipeRunResult> RunAsync(string recipeId, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult(new DevBridgeRecipeRunResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                true,
                "run-fixture",
                1,
                "lease-fixture",
                "fixture-evidence",
                "evidence-fixture",
                null,
                null,
                false,
                0,
                [],
                "workflow-fixture"));
        }
    }

    private sealed class NoopProcessTransport : IDevBridgeProcessTransport
    {
        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DevBridgeProcessResult(0, "{}", string.Empty));
    }
}
