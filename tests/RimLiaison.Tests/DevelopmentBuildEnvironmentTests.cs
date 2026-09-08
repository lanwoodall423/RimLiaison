using System.Security.Cryptography;
using System.Text;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class DevelopmentBuildEnvironmentTests
{
    public static void InternalBuildReceivesDiscoveredDependenciesAndDeploysOutput()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-build-env-" + id);
        string rimWorldRoot = Path.Combine(root, "RimWorld");
        string managed = Path.Combine(rimWorldRoot, "RimWorldWin64_Data", "Managed");
        string harmonyAssembly = Path.Combine(rimWorldRoot, "Mods", "Harmony", "Assemblies", "0Harmony.dll");
        string deploymentRoot = Path.Combine(Path.GetTempPath(), "rimliaison-build-deployed-" + id);
        byte[] builtBytes = "canonical-build-output"u8.ToArray();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".rimdev", "recipes"));
            Directory.CreateDirectory(Path.Combine(root, "Source"));
            Directory.CreateDirectory(Path.Combine(root, "About"));
            Directory.CreateDirectory(managed);
            Directory.CreateDirectory(Path.GetDirectoryName(harmonyAssembly)!);
            foreach (string name in new[]
                     {
                         "Assembly-CSharp.dll",
                         "UnityEngine.CoreModule.dll",
                         "UnityEngine.IMGUIModule.dll",
                         "UnityEngine.TextRenderingModule.dll"
                     })
            {
                File.WriteAllText(Path.Combine(managed, name), name);
            }
            File.WriteAllText(harmonyAssembly, "0Harmony");
            File.WriteAllText(Path.Combine(root, "About", "About.xml"),
                "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(root, "Source", "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, ".rimdev", "recipes", "fixture.json"),
                "{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"description\":\"fixture\",\"projects\":[\"fixture\"]}");
            File.WriteAllText(Path.Combine(root, ".rimdev", "stack.json"), """
                {
                  "schemaVersion":"rimdev-stack/v1","project":"Fixture","devBridgeProject":"fixture",
                  "catalog":"catalog.json","rimBridge":"disabled","workload":"production",
                  "projectType":"rimworld-content-mod","packageId":"lan.fixture",
                  "sourceProject":"Source/Fixture.csproj","configuration":"Release",
                  "expectedAssembly":"Fixture.dll","deploymentTarget":"1.6/Assemblies/Fixture.dll",
                  "testRecipe":"fixture","runtimePackage":{"sourceRoot":".","include":["About/**","1.*/**"],"exclude":[".rimdev/**","Source/**","bin/**","obj/**"]},"dependencies":[]
                }
                """);
            File.WriteAllText(Path.Combine(root, "catalog.json"), "{}");

            var transport = new RecordingTransport((request, _) =>
            {
                if (request.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    string output = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "Fixture.dll"), builtBytes);
                    return Result("", request);
                }

                if (request.Arguments.Contains("recipe", StringComparer.Ordinal) &&
                    request.Arguments.Contains("show", StringComparer.Ordinal))
                    return Result("{\"schemaVersion\":\"devbridge-test-recipe-show/v1\",\"recipe\":{\"schemaVersion\":\"devbridge-test-recipe/v1\",\"id\":\"fixture\",\"projects\":[\"fixture\"]}}", request);
                if (request.Arguments.Contains("recipe", StringComparer.Ordinal) &&
                    request.Arguments.Contains("plan", StringComparer.Ordinal))
                    return Result("{\"schemaVersion\":\"devbridge-test-recipe-plan/v1\",\"recipe\":\"fixture\",\"alreadySatisfied\":false,\"estimatedRimWorldLaunches\":1,\"nextAction\":\"run\",\"blockedBy\":[],\"steps\":[]}", request);
                if (request.Arguments.Contains("status", StringComparer.Ordinal))
                    return Result($"{{\"success\":true,\"rimworldRoot\":\"{Escape(rimWorldRoot)}\",\"generation\":1}}", request);
                if (request.Arguments.Contains("begin", StringComparer.Ordinal))
                    return Result("{\"success\":true,\"exitCode\":0,\"leaseId\":\"lease-00000000000000000000000000000001\",\"generation\":1}", request);
                if (request.Arguments.Contains("wait-ready", StringComparer.Ordinal))
                    return Result("{\"success\":true,\"state\":\"READY\",\"generation\":2}", request);
                if (request.Arguments.Contains("stop", StringComparer.Ordinal))
                    return Result("{\"success\":true,\"gameState\":\"STOPPED\"}", request);
                return Result("{\"success\":true,\"exitCode\":0}", request);
            });
            string deploymentLockName = "Global\\RimLiaison-DevBridge-Deployment-" +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(deploymentRoot))))
                    .ToLowerInvariant()[..24];
            using ManualResetEventSlim lockAcquired = new(false);
            Thread abandonedOwner = new(() =>
            {
                using Mutex mutex = new(false, deploymentLockName);
                mutex.WaitOne();
                lockAcquired.Set();
            });
            abandonedOwner.Start();
            Assert(lockAcquired.Wait(TimeSpan.FromSeconds(2)), "test must acquire deployment mutex");
            abandonedOwner.Join();

            var service = new InternalDevelopmentTransactionService(
                transport,
                new DevBridgeModDevelopmentAdapterOptions
                {
                    RootPath = root,
                    DeploymentRoot = deploymentRoot,
                    Timeout = TimeSpan.FromSeconds(10),
                    MaxStdoutBytes = 65536,
                    MaxStderrBytes = 4096
                });
            DevBridgeModDevelopmentResult result = service.RunAsync(
                    "fixture", root, new string('a', 64), "workflow-build-env")
                .GetAwaiter()
                .GetResult();

            Assert(result.Success == true, $"internal canonical build transaction must recover an abandoned deployment mutex: {result.Status.ErrorCode} {result.Status.Error} {result.Build?.Output} {result.Build?.DiagnosticOutput}");
            DevBridgeProcessRequest build = transport.Requests.Single(request =>
                request.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            Assert(build.EnvironmentVariables is not null, "build environment must be present");
            AssertEqual(managed, build.EnvironmentVariables!["RIMWORLD_MANAGED_PATH"], "managed path propagation");
            AssertEqual(managed, build.EnvironmentVariables["RIMWORLD_MANAGED_ASSEMBLIES"], "managed assemblies propagation");
            AssertEqual(harmonyAssembly, build.EnvironmentVariables["RIMWORLD_HARMONY_ASSEMBLY"], "Harmony assembly propagation");
            AssertEqual(Path.GetDirectoryName(harmonyAssembly)!, build.EnvironmentVariables["RIMWORLD_HARMONY_PATH"], "Harmony path propagation");
            Assert(
                build.Arguments.Any(argument =>
                    argument.StartsWith("-p:DefaultItemExcludes=", StringComparison.Ordinal) &&
                    argument.Contains(Path.Combine("Source", "obj", "**"), StringComparison.OrdinalIgnoreCase)),
                "isolated builds must retain the source project's obj exclusion");

            string deployed = Path.Combine(deploymentRoot, "1.6", "Assemblies", "Fixture.dll");
            string expectedHash = Convert.ToHexString(SHA256.HashData(builtBytes)).ToLowerInvariant();
            AssertEqual(expectedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(deployed))).ToLowerInvariant(), "deployed build artifact");
            AssertEqual(expectedHash, result.Freshness?.BuiltArtifactSha256, "reported built artifact");
            AssertEqual(expectedHash, result.Freshness?.DeployedArtifactSha256, "reported deployed artifact");
            AssertEqual(build.EnvironmentVariables["RIMWORLD_MANAGED_PATH"], result.Build?.EnvironmentVariables?["RIMWORLD_MANAGED_PATH"], "reported build environment");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            if (Directory.Exists(deploymentRoot))
                Directory.Delete(deploymentRoot, true);
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
    private static DevBridgeProcessResult Result(string stdout, DevBridgeProcessRequest request) =>
        new(0, stdout, string.Empty);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string? expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
    }

    private sealed class RecordingTransport : IDevBridgeProcessTransport
    {
        private readonly Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler;
        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public RecordingTransport(Func<DevBridgeProcessRequest, CancellationToken, DevBridgeProcessResult> handler) => this.handler = handler;

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}
