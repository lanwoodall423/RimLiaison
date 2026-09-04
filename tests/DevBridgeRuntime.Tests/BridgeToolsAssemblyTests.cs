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
        string mvid = typeof(OfflineTests).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName + ".dll", hash, mvid);

        Assert(result.Success && result.Loaded && result.UniqueMatch && result.HashMatches &&
               result.MvidMatches && result.VerificationMethod == "sha256+mvid" &&
               string.Equals(result.AssemblyName, assemblyName, StringComparison.Ordinal) &&
               string.Equals(result.Location, location, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.Sha256, hash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.RuntimeSha256, hash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.LoadedMvid, mvid, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(result.ExpectedMvid, mvid, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(result.AssemblyFullName) &&
               !string.IsNullOrWhiteSpace(result.ModuleName),
            "exact loaded assembly identity, hash, and MVID must be proven together");
    }

    private static void TestAssemblyVerifierRejectsAbsentAndWrongHash()
    {
        DevBridgeAssemblyTools tools = new();
        DevBridgeAssemblyVerificationPayload absent = tools.VerifyLoadedAssembly(
            "RimTest.NoSuchLoadedAssembly", new string('0', 64),
            Guid.NewGuid().ToString("D"));
        Assert(!absent.Success && !absent.Loaded && !absent.UniqueMatch &&
               absent.ErrorCode == "TARGET_ASSEMBLY_NOT_LOADED",
            "absent assemblies must fail with an explicit not-loaded code");

        string location = typeof(OfflineTests).Assembly.Location;
        string assemblyName = typeof(OfflineTests).Assembly.GetName().Name!;
        string mvid = typeof(OfflineTests).Assembly.ManifestModule.ModuleVersionId.ToString("D");
        DevBridgeAssemblyVerificationPayload wrong = tools.VerifyLoadedAssembly(
            assemblyName, new string('0', 64), mvid);
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
            .VerifyLoadedAssembly(assemblyName, new string('0', 64), Guid.NewGuid().ToString("D"));

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
            typeof(OfflineTests).Assembly.GetName().Name!, hash,
            typeof(OfflineTests).Assembly.ManifestModule.ModuleVersionId.ToString("D")));

        Assert(!json.Contains(unrelatedName, StringComparison.Ordinal) &&
               !json.Contains("System.Private.CoreLib", StringComparison.Ordinal) &&
               json.Length < 16 * 1024,
            "successful evidence must omit unrelated and locationless assemblies and stay bounded");
    }
    private static void TestAssemblyVerifierMvidFallback()
    {
        string assemblyName = "RimTest.LocationlessMvid";
        Assembly locationless = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        string mvid = locationless.ManifestModule.ModuleVersionId.ToString("D");
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName, new string('0', 64), mvid);

        Assert(result.Success && result.Loaded && result.UniqueMatch &&
               !result.BackingFileReadable && !result.HashMatches &&
               result.MvidMatches && result.VerificationMethod == "mvid" &&
               result.ErrorCode is null,
            "a locationless assembly with the expected MVID must use the SHA-anchored MVID fallback");
    }

    private static void TestAssemblyVerifierRejectsWrongMvid()
    {
        string location = typeof(OfflineTests).Assembly.Location;
        string assemblyName = typeof(OfflineTests).Assembly.GetName().Name!;
        string hash = HashFile(location);
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName, hash, Guid.NewGuid().ToString("D"));

        Assert(!result.Success && result.Loaded && result.UniqueMatch &&
               result.HashMatches && !result.MvidMatches &&
               result.ErrorCode == "TARGET_ASSEMBLY_IDENTITY_MISMATCH",
            "a matching file hash cannot override a wrong loaded MVID");
    }

    private static void TestAssemblyVerifierRejectsWrongLocationlessMvid()
    {
        string assemblyName = "RimTest.LocationlessWrongMvid";
        AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName, new string('0', 64), Guid.Empty.ToString("D"));

        Assert(!result.Success && result.Loaded && result.UniqueMatch &&
               result.ErrorCode == "TARGET_ASSEMBLY_IDENTITY_MISMATCH",
            "a locationless assembly with a wrong available MVID must fail as identity mismatch");
    }

    private static void TestAssemblyVerifierRequiresExpectedMvid()
    {
        string location = typeof(OfflineTests).Assembly.Location;
        string assemblyName = typeof(OfflineTests).Assembly.GetName().Name!;
        DevBridgeAssemblyVerificationPayload result = new DevBridgeAssemblyTools()
            .VerifyLoadedAssembly(assemblyName, HashFile(location), string.Empty);

        Assert(!result.Success && result.ErrorCode == "TARGET_ASSEMBLY_MVID_INVALID",
            "assembly verification must not accept a missing expected MVID");
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }
}
