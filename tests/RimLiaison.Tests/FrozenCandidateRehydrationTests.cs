using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.Stack;

namespace RimLiaison.Tests;

internal static class FrozenCandidateRehydrationTests
{
    public static void RehydrateExactArtifactWithoutBuild()
    {
        using Fixture fixture = new();
        string artifact = Path.Combine(Path.GetTempPath(), "rimliaison-frozen-artifact-" + Guid.NewGuid().ToString("N") + ".dll");
        string staging = Path.Combine(Path.GetTempPath(), "rimliaison-rehydrate-calculation-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(artifact, Encoding.UTF8.GetBytes("frozen artifact bytes"));
        string artifactSha = HashFile(artifact);
        List<ReadyToTestCandidatePreparationService.PackageEntry> entries =
            ReadyToTestCandidatePreparationService.BuildPackage(
                fixture.Root,
                Path.Combine(staging, "runtime"),
                artifact,
                fixture.Manifest.RuntimePackage,
                fixture.Manifest.DeploymentTarget!);
        string packageSha = ReadyToTestCandidatePreparationService.ComputePackageHash(entries);
        string candidateId = ReadyToTestCandidatePreparationService.HashText(
            "fixture\0" + fixture.Root + "\0" + fixture.SourceFingerprint + "\0" + packageSha)[..32];
        string store = Path.Combine(fixture.Root, "durable-candidates");

        ReadyToTestCandidateResult result = new ReadyToTestCandidateRehydrationService()
            .RehydrateAsync(
                "fixture",
                fixture.Root,
                new ReadyToTestCandidateRehydrationOptions
                {
                    CandidateId = candidateId,
                    PackageSha256 = packageSha,
                    ArtifactPath = artifact,
                    ArtifactSha256 = artifactSha,
                    SourceCommit = fixture.Commit,
                    CandidateStoreRoot = store
                })
            .GetAwaiter()
            .GetResult();

        Assert(result.Success && result.Manifest is not null, result.Error ?? "rehydration failed");
        Assert(result.Manifest!.Provenance == "rehydrated-frozen-candidate" &&
               result.Manifest.BuildPerformed == false &&
               result.Manifest.ArtifactIdentityVerified == true &&
               result.Manifest.BuildStatus == "NOT_PERFORMED" &&
               result.Manifest.PackageSha256 == packageSha,
            "rehydrated candidate provenance or package identity was not retained");
        Assert(Path.GetFullPath(result.Manifest.SourceRepositoryRoot) == Path.GetFullPath(fixture.Root) &&
               result.Manifest.StagedRuntimeRoot.StartsWith(Path.GetFullPath(store), StringComparison.OrdinalIgnoreCase),
            "rehydrated candidate was not retained in the durable candidate store");
        Assert(ReadyToTestCandidateVerifier.TryVerify(
                result.ManifestPath!, out VerifiedReadyToTestCandidate? verified, out string code, out string error) &&
               verified is not null,
            $"rehydrated candidate did not verify: {code} {error}");
    }

    public static void RehydrateRejectsWrongArtifactIdentity()
    {
        using Fixture fixture = new();
        string artifact = Path.Combine(fixture.Root, "frozen.dll");
        File.WriteAllBytes(artifact, Encoding.UTF8.GetBytes("frozen artifact bytes"));
        ReadyToTestCandidateResult result = new ReadyToTestCandidateRehydrationService()
            .RehydrateAsync(
                "fixture",
                fixture.Root,
                new ReadyToTestCandidateRehydrationOptions
                {
                    CandidateId = new string('a', 32),
                    PackageSha256 = new string('b', 64),
                    ArtifactPath = artifact,
                    ArtifactSha256 = new string('c', 64),
                    SourceCommit = fixture.Commit,
                    CandidateStoreRoot = Path.Combine(fixture.Root, "durable-candidates")
                })
            .GetAwaiter()
            .GetResult();
        Assert(!result.Success && result.ErrorCode == "FROZEN_CANDIDATE_REHYDRATION_IDENTITY_MISMATCH",
            "wrong frozen artifact identity was not rejected before candidate retention");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-rehydrate-" + Guid.NewGuid().ToString("N"));
        public RimDevStackManifest Manifest { get; }
        public string SourceFingerprint { get; }
        public string Commit { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            RunGit("init");
            Directory.CreateDirectory(Path.Combine(Root, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(Root, "About"));
            Directory.CreateDirectory(Path.Combine(Root, "Source"));
            Directory.CreateDirectory(Path.Combine(Root, "1.6"));
            File.WriteAllText(Path.Combine(Root, "Source", "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "Source", "Fixture.cs"), "public sealed class Fixture {}");
            File.WriteAllText(Path.Combine(Root, "About", "About.xml"), "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(Root, "LoadFolders.xml"), "<loadFolders />");
            File.WriteAllText(Path.Combine(Root, "catalog.json"), "{\"schemaVersion\":\"rimtest-catalog/v1\",\"tests\":[],\"suites\":[]}");
            File.WriteAllText(Path.Combine(Root, ".rimdev", "stack.json"), """
                {"schemaVersion":"rimdev-stack/v1","project":"Fixture","devBridgeProject":"fixture","catalog":"catalog.json","rimBridge":"disabled","workload":"production","projectType":"rimworld-content-mod","packageId":"lan.fixture","sourceProject":"Source/Fixture.csproj","configuration":"Release","expectedAssembly":"Fixture.dll","deploymentTarget":"1.6/Assemblies/Fixture.dll","testRecipe":"fixture","testRecipePath":"recipe.json","runtimePackage":{"sourceRoot":".","include":["About/**","1.*/**"],"exclude":[".rimdev/**","Source/**","bin/**","obj/**"]},"dependencies":[]}
                """);
            StackManifestResolution resolution = StackManifestResolver.Discover(Root);
            Assert(resolution.Manifest is not null, $"rehydration fixture stack manifest missing: {resolution.ErrorCode} {resolution.Error}");
            Manifest = resolution.Manifest!;
            string[] paths = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
                .Where(path => !RepositoryChangeClassificationPolicy.Classify(path).IsGenerated)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert(WorktreeFingerprint.TryCompute(Root, paths, out string fingerprint, out string? error), error ?? "fingerprint failed");
            SourceFingerprint = fingerprint;
            RunGit("add", ".");
            RunGit("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
            Commit = RunGit("rev-parse", "HEAD").Trim();
        }

        private string RunGit(params string[] arguments)
        {
            ProcessStartInfo start = new() { FileName = "git", WorkingDirectory = Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git fixture command.");
            process.WaitForExit();
            string output = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0) throw new InvalidOperationException(process.StandardError.ReadToEnd());
            return output;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            foreach (string directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
