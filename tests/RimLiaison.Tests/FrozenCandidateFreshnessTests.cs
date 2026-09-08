using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Results;
using RimLiaison.Stack;
using RimLiaison.Git;

namespace RimLiaison.Tests;

internal static class FrozenCandidateFreshnessTests
{
    public static void SuiteFreshnessPropagatesExplicitCandidate()
    {
        using Fixture fixture = new();
        string candidatePath = fixture.CreateCandidate();
        RecordingDevelopmentAdapter development = new();
        string previousDirectory = Environment.CurrentDirectory;
        string? previousRoot = Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT");
        Environment.SetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT", fixture.RepositoryRoot);
        try
        {

            Environment.CurrentDirectory = fixture.RepositoryRoot;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "suite", "run", "release", "--json", "--freshness-only",
                        "--candidate-manifest", candidatePath,
                        "--devbridge", Path.Combine(fixture.RepositoryRoot, "DevBridge.cmd"),
                        "--devbridge-project", "fixture",
                        "--catalog", Path.Combine(fixture.RepositoryRoot, "catalog.json")
                    ],
                    stdout,
                    stderr,
                    developmentAdapter: development)
                .GetAwaiter()
                .GetResult();
            using JsonDocument result = JsonDocument.Parse(stdout.ToString());
            string nextAction = result.RootElement.TryGetProperty("nextAction", out JsonElement next)
                ? next.GetString() ?? string.Empty
                : string.Empty;
            Assert(exitCode == 0 && result.RootElement.GetProperty("status").GetString() == "pass",
                $"candidate freshness canary failed: nextAction={nextAction}; stderr={stderr}");
            Assert(development.Context?.Candidate?.CandidateId == fixture.Manifest!.CandidateId &&
                   string.Equals(
                       development.Context?.CandidateManifestPath,
                       candidatePath,
                       StringComparison.OrdinalIgnoreCase) &&
                   development.Context?.Candidate?.SourceFingerprint == fixture.SourceFingerprint,
                "suite freshness did not propagate the explicit candidate into the execution context");
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT", previousRoot);
        }
    }

    public static void CandidateContextMismatchFailsBeforeAdapter()
    {
        using Fixture fixture = new();
        string candidatePath = fixture.CreateCandidate();
        RecordingDevelopmentAdapter development = new();
        ArtifactFreshnessTransactionResult result = new ArtifactFreshnessTransaction(development)
            .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                "fixture",
                fixture.RepositoryRoot,
                fixture.SourcePaths,
                fixture.SourceFingerprint,
                "candidate-test",
                Candidate: fixture.Manifest,
                CandidateManifestPath: null))
            .GetAwaiter()
            .GetResult();

        Assert(!result.Success && result.Status.ErrorCode == "RIMTEST_CANDIDATE_INVALID" &&
               development.Context is null,
            "candidate context without a manifest path was not rejected before adapter execution");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
    {
        public DevBridgeModDevelopmentExecutionContext? Context { get; private set; }

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("candidate context overload was not used");

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            DevBridgeModDevelopmentExecutionContext? executionContext,
            CancellationToken cancellationToken = default)
        {
            Context = executionContext;
            return Task.FromResult(new DevBridgeModDevelopmentResult(
                project,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                true,
                "tx-candidate",
                workflowId,
                1,
                null,
                new DevBridgeArtifactFreshness(
                    sourceFingerprint,
                    new string('a', 64),
                    new string('a', 64),
                    "unchanged",
                    1,
                    1,
                    1,
                    true,
                    "verified-candidate",
                    "tx-candidate",
                    workflowId,
                    "lease-candidate",
                    BuildSource: "verified-candidate",
                    BuildAction: "reused",
                    CandidateId: executionContext?.Candidate?.CandidateId,
                    BuildInvocationCount: 0)));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string RepositoryRoot { get; } = Path.Combine(
            Path.GetTempPath(), "rimliaison-frozen-candidate-" + Guid.NewGuid().ToString("N"));
        public string CandidateRoot { get; } = Path.Combine(
            Path.GetTempPath(), "rimliaison-frozen-candidate-output-" + Guid.NewGuid().ToString("N"));
        public string RuntimeRoot { get; private set; } = string.Empty;
        public string[] SourcePaths { get; }
        public string SourceFingerprint { get; }
        public ReadyToTestCandidateManifest? Manifest { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(RepositoryRoot);
            RunGit("init");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "Source"));
            File.WriteAllText(Path.Combine(RepositoryRoot, "DevBridge.cmd"), string.Empty);
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "stack.json"), """
                {"schemaVersion":"rimdev-stack/v1","project":"Fixture","devBridgeProject":"fixture","catalog":"catalog.json","rimBridge":"disabled","workload":"production","projectType":"rimworld-content-mod","packageId":"lan.fixture","sourceProject":"Source/Fixture.csproj","configuration":"Release","expectedAssembly":"Fixture.dll","deploymentTarget":"1.6/Assemblies/Fixture.dll","testRecipe":"fixture","testRecipePath":"recipe.json","runtimePackage":{"sourceRoot":".","include":[],"exclude":[]},"dependencies":[]}
                """);
            File.WriteAllText(Path.Combine(RepositoryRoot, "catalog.json"), """
                {"schemaVersion":"rimtest-catalog/v1","tests":[{"id":"fixture-release-test","recipe":"fixture","artifactFreshnessAnchor":true}],"suites":[{"id":"release","tests":["fixture-release-test"]}]}
                """);
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "recipe.json"), "{}");
            File.WriteAllText(Path.Combine(RepositoryRoot, "Source", "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(RepositoryRoot, "Source", "Fixture.cs"), "class Fixture {}");
            SourcePaths = Directory.EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
                .Where(path => !RepositoryChangeClassificationPolicy.Classify(path).IsGenerated)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            StackManifestResolution stack = StackManifestResolver.Discover(RepositoryRoot);
            Assert(stack.Found, $"fixture stack manifest not found: {stack.ErrorCode} {stack.Error}");
            Assert(WorktreeFingerprint.TryCompute(RepositoryRoot, SourcePaths, out string fingerprint, out string? error), error ?? "fingerprint failed");
            SourceFingerprint = fingerprint;
        }

        public string CreateCandidate()
        {
            string candidateBase = Path.Combine(CandidateRoot, "fixture", "d444b766f2637f1ae3e50b6678415f89");
            RuntimeRoot = Path.Combine(candidateBase, "runtime");
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "1.6", "Assemblies"));
            byte[] payload = Encoding.UTF8.GetBytes("frozen candidate payload");
            string target = Path.Combine(RuntimeRoot, "1.6", "Assemblies", "Fixture.dll");
            File.WriteAllBytes(target, payload);
            ReadyToTestCandidatePackageEntry entry = new(
                target,
                "1.6/Assemblies/Fixture.dll",
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                payload.Length);
            Manifest = new ReadyToTestCandidateManifest
            {
                CandidateId = "d444b766f2637f1ae3e50b6678415f89",
                Project = "Fixture",
                CanonicalProjectId = "fixture",
                PackageId = "lan.fixture",
                SourceRepositoryRoot = RepositoryRoot,
                SourceFingerprint = SourceFingerprint,
                StagedRuntimeRoot = RuntimeRoot,
                PackageSha256 = ReadyToTestCandidateVerifier.ComputePackageHash([entry]),
                BuildEvidenceIdentity = "candidate-build:frozen",
                BuildStatus = "PASS",
                StaticValidationIdentity = "candidate-validation:frozen",
                StaticValidationStatus = "PASS",
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            RunGit("add", ".");
            RunGit(
                "-c", "user.name=Fixture",
                "-c", "user.email=fixture@example.invalid",
                "commit", "-m", "fixture");
            string manifestPath = Path.Combine(candidateBase, "candidate.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(Manifest));
            return manifestPath;
        }
        private void RunGit(params string[] arguments)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "git",
                WorkingDirectory = RepositoryRoot,
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
            {
                throw new InvalidOperationException(
                    $"Git fixture command failed: {string.Join(" ", arguments)} {process.StandardError.ReadToEnd()}");
            }
        }

        public void Dispose()
        {
            DeleteTree(RepositoryRoot);
            DeleteTree(CandidateRoot);
        }

        private static void DeleteTree(string root)
        {
            if (!Directory.Exists(root))
                return;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Normal);
            File.SetAttributes(root, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
