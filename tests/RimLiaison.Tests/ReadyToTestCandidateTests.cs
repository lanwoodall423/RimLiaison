using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class ReadyToTestCandidateTests
{
    public static void BuildFailureDoesNotTouchRuntime()
    {
        using Fixture fixture = new();
        var transport = new RecordingTransport((request, _) =>
            new DevBridgeProcessResult(1, "compile failed", "error CS1002"));
        string modsConfig = Path.Combine(fixture.RimWorldRoot, "Config", "ModsConfig.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(modsConfig)!);
        File.WriteAllText(modsConfig, "baseline");
        ReadyToTestCandidateResult result = fixture.Prepare(transport);
        Assert(!result.Success && result.ErrorCode == "DEVELOPMENT_BUILD_FAILED",
            "compile failure must be returned as a candidate build failure");
        Assert(transport.Requests.Count == 1 && transport.Requests[0].FileName == "dotnet",
            "candidate build failure must not invoke a runtime command");
        Assert(File.ReadAllText(modsConfig) == "baseline" && !Directory.Exists(fixture.CanonicalModRoot),
            "compile failure touched the canonical runtime or ModsConfig");
    }

    public static void SuccessfulPreparationWritesReadyManifest()
    {
        using Fixture fixture = new();
        ReadyToTestCandidateResult result = fixture.Prepare(new RecordingTransport(fixture.Build));
        Assert(result.Success && result.Manifest is not null && result.Manifest.State == "READY_TO_TEST",
            result.Error ?? "candidate preparation did not complete");
        Assert(File.Exists(result.ManifestPath!), "READY_TO_TEST manifest was not written");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
        Assert(document.RootElement.GetProperty("state").GetString() == "READY_TO_TEST" &&
            document.RootElement.GetProperty("packageSha256").GetString() is { Length: 64 },
            "candidate manifest omitted required identity or state");
        Assert(Path.GetFullPath(result.Manifest!.StagedRuntimeRoot).StartsWith(
                Path.GetFullPath(fixture.CandidateRoot), StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFullPath(result.Manifest.StagedRuntimeRoot).StartsWith(
                Path.GetFullPath(fixture.CanonicalModRoot), StringComparison.OrdinalIgnoreCase),
            "candidate payload was not staged outside canonical Mods");
    }

    public static void SourceOnlyFilesNeverEnterRuntimePayload()
    {
        using Fixture fixture = new();
        ReadyToTestCandidateResult result = fixture.Prepare(new RecordingTransport(fixture.Build));
        Assert(result.Success, result.Error ?? "candidate preparation failed");
        string[] files = Directory.EnumerateFiles(result.Manifest!.StagedRuntimeRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(result.Manifest.StagedRuntimeRoot, path).Replace('\\', '/'))
            .ToArray();
        Assert(files.Any(path => path == "About/About.xml" || path == "1.6/Defs/Thing.xml"),
            "runtime payload omitted runtime content");
        Assert(!files.Any(path => path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Source/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Source/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("DevCheckRuntime/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/DevCheckRuntime/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".rimdev/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/.rimdev/", StringComparison.OrdinalIgnoreCase)),
            "source, build, test, or DevCheckRuntime content entered runtime payload");
    }

    public static void IdenticalInputsProduceIdenticalPackageIdentity()
    {
        using Fixture fixture = new();
        ReadyToTestCandidateResult first = fixture.Prepare(new RecordingTransport(fixture.Build));
        ReadyToTestCandidateResult second = fixture.Prepare(new RecordingTransport(fixture.Build));
        Assert(first.Success && second.Success &&
            first.Manifest!.CandidateId == second.Manifest!.CandidateId &&
            first.Manifest.PackageSha256 == second.Manifest.PackageSha256,
            "identical candidate inputs produced different package identity");
    }

    public static void SourceChangesChangeCandidateIdentity()
    {
        using Fixture fixture = new();
        var firstTransport = new RecordingTransport(fixture.Build);
        var service = new ReadyToTestCandidatePreparationService(
            firstTransport,
            new ReadyToTestCandidatePreparationOptions { CandidateStagingRoot = fixture.CandidateRoot });
        ReadyToTestCandidateResult first = service.PrepareAsync(
                "fixture", fixture.RepositoryRoot)
            .GetAwaiter().GetResult();
        File.AppendAllText(
            Path.Combine(fixture.RepositoryRoot, "Source", "Fixture.csproj"),
            "<!-- source change -->");
        ReadyToTestCandidateResult second = service.PrepareAsync(
                "fixture", fixture.RepositoryRoot)
            .GetAwaiter().GetResult();
        Assert(first.Success && second.Success &&
            first.Manifest!.CandidateId != second.Manifest!.CandidateId &&
            first.Manifest.SourceFingerprint != second.Manifest.SourceFingerprint,
            "source changes did not change candidate/source identity");
    }

    public static void PreparationDoesNotRequireRimWorldRestart()
    {
        using Fixture fixture = new();
        var transport = new RecordingTransport(fixture.Build);
        ReadyToTestCandidateResult result = fixture.Prepare(transport);
        Assert(result.Success && transport.Requests.All(request => request.FileName == "dotnet"),
            "candidate preparation required a RimWorld/runtime command");
    }

    public static void ExternalDevBridgeCheckoutIsNotCandidateIdentity()
    {
        using Fixture fixture = new();
        var transport = new RecordingTransport(fixture.Build);
        ReadyToTestCandidateResult result = new ReadyToTestCandidatePreparationService(
                transport,
                new ReadyToTestCandidatePreparationOptions
                {
                    CandidateStagingRoot = fixture.CandidateRoot
                })
            .PrepareAsync("fixture", fixture.RepositoryRoot, "external-independent-source")
            .GetAwaiter().GetResult();
        Assert(result.Success && result.Manifest!.SourceRepositoryRoot == Path.GetFullPath(fixture.RepositoryRoot) &&
            transport.Requests.All(request => request.FileName == "dotnet"),
            "candidate preparation depended on an external DevBridge2 checkout");
    }

    private sealed class Fixture : IDisposable
    {
        public string RepositoryRoot { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-candidate-test-" + Guid.NewGuid().ToString("N"));
        public string CandidateRoot { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-candidate-output-" + Guid.NewGuid().ToString("N"));
        public string RimWorldRoot { get; }
        public string CanonicalModRoot { get; }

        public Fixture()
        {
            Directory.CreateDirectory(CandidateRoot);
            RimWorldRoot = Path.Combine(Path.GetTempPath(), "rimliaison-candidate-rimworld-" + Guid.NewGuid().ToString("N"));
            CanonicalModRoot = Path.Combine(RimWorldRoot, "Mods", "Fixture");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".rimdev", "recipes"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "Source"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "About"));
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "1.6", "Defs"));
            File.WriteAllText(Path.Combine(RepositoryRoot, "About", "About.xml"), "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(RepositoryRoot, "1.6", "Defs", "Thing.xml"), "<ThingDef />");
            File.WriteAllText(Path.Combine(RepositoryRoot, "Source", "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "recipes", "fixture.json"), "{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"description\":\"fixture\",\"projects\":[\"fixture\"]}");
            File.WriteAllText(Path.Combine(RepositoryRoot, ".rimdev", "stack.json"), """
                {
                  "schemaVersion":"rimdev-stack/v1",
                  "project":"Fixture",
                  "devBridgeProject":"fixture",
                  "catalog":"catalog.json",
                  "rimBridge":"disabled",
                  "workload":"production",
                  "projectType":"rimworld-content-mod",
                  "packageId":"lan.fixture",
                  "sourceProject":"Source/Fixture.csproj",
                  "configuration":"Release",
                  "expectedAssembly":"Fixture.dll",
                  "deploymentTarget":"1.6/Assemblies/Fixture.dll",
                  "testRecipe":"fixture",
                  "runtimePackage":{"sourceRoot":".","include":["About/**","1.*/**","Source/**","bin/**","obj/**",".git/**","tests/**","TestResults/**","DevCheckRuntime/**"],"exclude":[".rimdev/**","Source/**","bin/**","obj/**"]},
                  "dependencies":["Core"]
                }
                """);
            File.WriteAllText(Path.Combine(RepositoryRoot, "catalog.json"), "{}");
            foreach (string path in new[] { ".git/config", "bin/old.dll", "obj/old.cache", "tests/result.xml", "TestResults/result.trx", "DevCheckRuntime/obj/generated.cache", "1.6/.rimdev/profiles/stale.json" })
            {
                string full = Path.Combine(RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "generated");
            }
        }

        public ReadyToTestCandidateResult Prepare(RecordingTransport transport, string sourceFingerprint = "source-stable") =>
            new ReadyToTestCandidatePreparationService(
                    transport,
                    new ReadyToTestCandidatePreparationOptions { CandidateStagingRoot = CandidateRoot })
                .PrepareAsync("fixture", RepositoryRoot, sourceFingerprint)
                .GetAwaiter().GetResult();

        public DevBridgeProcessResult Build(DevBridgeProcessRequest request, CancellationToken cancellationToken)
        {
            string output = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "Fixture.dll"), "fixture-assembly");
            return new DevBridgeProcessResult(0, "build succeeded", string.Empty);
        }

        public void Dispose()
        {
            Delete(RepositoryRoot);
            Delete(CandidateRoot);
            Delete(RimWorldRoot);
        }

        private static void Delete(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingTransport : IDevBridgeProcessTransport
    {
        private readonly Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler;
        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public RecordingTransport(Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler) => this.handler = handler;

        public Task<DevBridgeProcessResult> ExecuteAsync(DevBridgeProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request, cancellationToken));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
