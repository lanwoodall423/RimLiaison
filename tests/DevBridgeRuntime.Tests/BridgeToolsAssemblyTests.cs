using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using DevBridge2.BridgeTools;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestAssemblyVerifierRequiresAuthenticatedToolMetadata()
    {
        string source = File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(), "src", "DevBridgeRuntime", "BridgeTools",
            "DevBridgeAssemblyTools.cs"));
        Assert(source.Contains("[Tool(\"devbridge/verify_loaded_assembly\"", StringComparison.Ordinal) &&
               source.Contains("RequiresAuth = true", StringComparison.Ordinal) &&
               source.Contains("[ToolResponse(", StringComparison.Ordinal),
            "assembly verification must be exposed as one authenticated owned BridgeTools tool");
    }

    private static void TestAssemblyVerifierMatchesExactLoadedAssembly()
    {
        string location = typeof(OfflineTests).Assembly.Location;
        string assemblyName = typeof(OfflineTests).Assembly.GetName().Name!;
        string hash = HashFile(location);
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName + ".dll", hash);

        Assert(result.Success && result.Loaded && result.UniqueMatch && result.HashMatches &&
               string.Equals(result.AssemblyName, assemblyName, StringComparison.Ordinal) &&
               string.Equals(result.Location, location, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.Sha256, hash, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(result.AssemblyFullName) &&
               !string.IsNullOrWhiteSpace(result.Mvid),
            "exact loaded assembly identity and file hash must be proven together");
    }

    private static void TestAssemblyVerifierRejectsAbsentAndWrongHash()
    {
        DevBridgeAssemblyTools tools = new();
        DevBridgeAssemblyVerificationPayload absent = tools.VerifyLoadedAssembly(
            "RimTest.NoSuchLoadedAssembly", new string('0', 64));
        Assert(!absent.Success && !absent.Loaded && !absent.UniqueMatch &&
               absent.ErrorCode == "TARGET_ASSEMBLY_NOT_LOADED",
            "absent assemblies must fail with an explicit not-loaded code");

        string location = typeof(OfflineTests).Assembly.Location;
        string assemblyName = typeof(OfflineTests).Assembly.GetName().Name!;
        DevBridgeAssemblyVerificationPayload wrong = tools.VerifyLoadedAssembly(
            assemblyName, new string('0', 64));
        Assert(!wrong.Success && wrong.Loaded && wrong.UniqueMatch && !wrong.HashMatches &&
               wrong.ErrorCode == "TARGET_ASSEMBLY_IDENTITY_MISMATCH" &&
               string.Equals(wrong.Sha256, HashFile(location), StringComparison.OrdinalIgnoreCase),
            "a loaded assembly with the wrong backing hash must fail closed");
    }

    private static void TestAssemblyVerifierRejectsDuplicateSimpleNames()
    {
        string assemblyName = "RimTest.DuplicateAssembly";
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);

        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName, new string('0', 64));

        Assert(!result.Success && result.Loaded && !result.UniqueMatch &&
               result.ErrorCode == "TARGET_ASSEMBLY_AMBIGUOUS" &&
               result.Matches is { Count: >= 2 and <= 4 },
            "duplicate simple names must be reported as bounded ambiguity");
    }

    private static void TestAssemblyVerifierIgnoresUnrelatedAndLocationlessAssemblies()
    {
        string unrelatedName = "RimTest.LocationlessUnrelated";
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unrelatedName), AssemblyBuilderAccess.Run);
        string location = typeof(OfflineTests).Assembly.Location;
        string hash = HashFile(location);
        string json = JsonSerializer.Serialize(new DevBridgeAssemblyTools().VerifyLoadedAssembly(
            typeof(OfflineTests).Assembly.GetName().Name!, hash));

        Assert(!json.Contains(unrelatedName, StringComparison.Ordinal) &&
               !json.Contains("System.Private.CoreLib", StringComparison.Ordinal) &&
               json.Length < 16 * 1024,
            "successful evidence must omit unrelated and locationless assemblies and stay bounded");
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }
}
