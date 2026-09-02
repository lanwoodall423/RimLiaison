using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class InternalRuntimeCommunicationTests
{
    public static void ExistingClientCommunicatesWithOwnedCoordinator()
    {
        string repositoryRoot = FindRepositoryRoot();
        string packageRoot = Path.Combine(repositoryRoot, "src", "DevBridgeRuntime", "Package");
        string coordinator = Path.Combine(packageRoot, "Coordinator", "DevBridge.Coordinator.exe");
        Assert(File.Exists(coordinator), "the internally built coordinator must be available");

        string runtimeRoot = Path.Combine(Path.GetTempPath(),
            "rimliaison-owned-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        string fakeRimWorld = Path.Combine(runtimeRoot, "RimWorldWin64.exe");
        File.WriteAllText(fakeRimWorld, string.Empty);
        SystemDevBridgeProcessTransport transport = new();
        bool serverStarted = false;
        try
        {
            DevBridgeProcessResult status = Execute(transport, coordinator, packageRoot, runtimeRoot,
                fakeRimWorld, "status");
            serverStarted = true;
            Assert(status.StartError is null && status.ExitCode == 0 && status.Response is not null &&
                   !string.IsNullOrWhiteSpace(status.Response.State),
                "the existing RimLiaison process client must receive a structured status from the owned coordinator");
            Assert(status.Evidence is not null &&
                   string.Equals(status.Evidence.ResolvedExecutablePath, coordinator,
                       StringComparison.OrdinalIgnoreCase),
                "client evidence must identify the internally built coordinator executable");

            DevBridgeProcessResult shutdown = Execute(transport, coordinator, packageRoot, runtimeRoot,
                fakeRimWorld, "coordinator", "shutdown");
            Assert(shutdown.StartError is null && shutdown.ExitCode == 0 && shutdown.Response is not null,
                "the existing RimLiaison process client must complete the owned coordinator shutdown");
        }
        finally
        {
            if (serverStarted)
            {
                try
                {
                    Execute(transport, coordinator, packageRoot, runtimeRoot, fakeRimWorld,
                        "coordinator", "shutdown");
                }
                catch
                {
                }
            }

            try
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static DevBridgeProcessResult Execute(
        SystemDevBridgeProcessTransport transport,
        string coordinator,
        string packageRoot,
        string runtimeRoot,
        string fakeRimWorld,
        params string[] command) =>
        transport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    coordinator,
                    packageRoot,
                    ["--root", runtimeRoot, .. command, "--json"],
                    TimeSpan.FromSeconds(30),
                    256 * 1024,
                    64 * 1024,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["DEVBRIDGE_TEST_RIMWORLD_PATH"] = fakeRimWorld,
                        ["RIMWORLD_ROOT"] = string.Empty,
                        ["RIMWORLD_EXECUTABLE"] = string.Empty
                    },
                    "rimliaison-owned-runtime-test"),
            CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RimLiaison.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("RimLiaison repository root was not found");
    }
}
