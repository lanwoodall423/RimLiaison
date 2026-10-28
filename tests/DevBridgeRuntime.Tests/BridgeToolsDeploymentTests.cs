using System.Text;
using System.Xml.Linq;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestRealRimWorldManagedAssemblyPreflightContract()
    {
        string component = Path.Combine(FindWorkspaceRoot(), "src", "DevBridgeRuntime");
        XDocument props = XDocument.Load(Path.Combine(component, "Directory.Build.props"));
        XElement group = props.Root?.Element("PropertyGroup");
        Assert(group is not null, "DevBridge runtime requires its shared MSBuild property group");

        XElement[] sources = group.Elements("RimWorldManagedDir").ToArray();
        string[] fallbackValues =
        [
            "$(RIMWORLD_MANAGED_DIR)",
            "$(RIMWORLD_MANAGED_PATH)",
            "$(RIMWORLD_MANAGED_ASSEMBLIES)",
            "$(RIMWORLD_ROOT)\\RimWorldWin64_Data\\Managed",
            "$(RimWorldRootFromWorkspace)\\RimWorldWin64_Data\\Managed"
        ];
        Assert(sources.Length == fallbackValues.Length,
            "RimWorld assembly resolution must preserve five ordered explicit fallback sources");
        for (int i = 0; i < fallbackValues.Length; i++)
        {
            Assert(sources[i].Value == fallbackValues[i] &&
                   sources[i].Attribute("Condition")?.Value.StartsWith(
                       "'$(RimWorldManagedDir)' == ''", StringComparison.Ordinal) == true,
                "each fallback must preserve an explicitly supplied managed directory");
        }
        Assert(group.Element("RimWorldRootFromWorkspace") is not null,
            "legacy project-owned workspace discovery must remain available");

        XDocument mod = XDocument.Load(Path.Combine(component, "Mod", "DevBridge2.csproj"));
        Assert(!mod.Descendants("RimWorldManagedDir").Any(),
            "the game Mod must not override centralized explicit path precedence");
        XElement target = mod.Descendants("Target").SingleOrDefault(item =>
            item.Attribute("Name")?.Value == "ValidateRealRimWorldManagedAssemblies");
        Assert(target is not null &&
               target.Attribute("BeforeTargets")?.Value == "ResolveReferences" &&
               target.Attribute("Condition")?.Value.Contains(
                   "'$(DesignTimeBuild)' != 'true'", StringComparison.Ordinal) == true,
            "the real-game prerequisite gate must precede normal Mod reference resolution");

        XElement[] errors = target.Elements("Error").ToArray();
        Assert(errors.Select(item => item.Attribute("Code")?.Value).SequenceEqual(
                   ["RLW0001", "RLW0002", "RLW0003", "RLW0004"]),
            "preflight must distinguish missing configuration, directory and both real DLLs");
        foreach (XElement error in errors)
            Assert(error.Attribute("Condition")?.Value.Contains(
                       "$(RimWorldManagedDir)", StringComparison.Ordinal) == true &&
                   !string.IsNullOrWhiteSpace(error.Attribute("Text")?.Value),
                "every game-assembly prerequisite needs an actionable condition and message");
        Assert(errors[2].Attribute("Condition")?.Value.Contains(
                   "Assembly-CSharp.dll", StringComparison.Ordinal) == true &&
               errors[3].Attribute("Condition")?.Value.Contains(
                   "UnityEngine.CoreModule.dll", StringComparison.Ordinal) == true,
            "the game prerequisites must name the actual installed assemblies");

        foreach (string name in new[] { "Assembly-CSharp", "UnityEngine.CoreModule" })
        {
            XElement reference = mod.Descendants("Reference").SingleOrDefault(item =>
                item.Attribute("Include")?.Value == name);
            Assert(reference?.Element("HintPath")?.Value ==
                   "$(RimWorldManagedDir)\\" + name + ".dll" &&
                   reference.Element("Private")?.Value == "false",
                "the Mod must reference actual game DLLs without embedding local copies");
        }
    }

    private static void TestBridgeToolsPublishContract()
    {
        if (SkipUnselectedBridgeToolsCoverage())
            return;

        string root = FindWorkspaceRoot();
        string component = Path.Combine(root, "src", "DevBridgeRuntime");
        string companionProject = File.ReadAllText(Path.Combine(component, "BridgeTools",
            "DevBridge2.BridgeTools.csproj"));
        string companionTools = File.ReadAllText(Path.Combine(component, "BridgeTools",
            "DevBridgeGenerationTools.cs"));
        string verifierTools = File.ReadAllText(Path.Combine(component, "BridgeTools",
            "DevBridgeAssemblyTools.cs"));
        string coreProject = File.ReadAllText(Path.Combine(component, "Mod", "DevBridge2.csproj"));
        string coreSource = string.Join("\n", Directory.EnumerateFiles(
            Path.Combine(component, "Mod"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        string provenance = File.ReadAllText(Path.Combine(component, "provenance.json"));
        string packageRoot = Path.Combine(component, "Package");

        Assert(companionProject.Contains("RimBridgeServer.Sdk", StringComparison.Ordinal) &&
               companionProject.Contains("PrivateAssets=\"all\"", StringComparison.Ordinal) &&
               companionProject.Contains("ExcludeAssets=\"runtime\"", StringComparison.Ordinal) &&
               companionProject.Contains("$(DevBridgeRuntimePackageRoot)1.6\\BridgeTools\\",
                   StringComparison.Ordinal) &&
               !coreProject.Contains("RimBridgeServer.Sdk", StringComparison.OrdinalIgnoreCase) &&
               !coreSource.Contains("RimBridgeServer", StringComparison.OrdinalIgnoreCase),
            "the companion must use the active 1.6 load folder and keep its SDK reference compile-time-only");
        Assert(companionTools.Contains("public sealed class DevBridgeGenerationTools", StringComparison.Ordinal) &&
               companionTools.Contains("public DevBridgeGenerationContextPayload GetGenerationContext()", StringComparison.Ordinal) &&
               companionTools.Contains("public DevBridgeControlPolicyPayload GetControlPolicy()", StringComparison.Ordinal) &&
               verifierTools.Contains("[Tool(\"devbridge/verify_loaded_assembly\"", StringComparison.Ordinal) &&
               verifierTools.Contains("RequiresAuth = true", StringComparison.Ordinal),
            "the companion tools must retain the existing owned tools and exact verifier registration");
        Assert(provenance.Contains("312bc12123ea86723909586b80253fd1a63253c4", StringComparison.Ordinal) &&
               File.Exists(Path.Combine(packageRoot, "DevBridge.cmd")) &&
               File.Exists(Path.Combine(packageRoot, "About", "About.xml")),
            "the package must retain the imported source identity and runtime entrypoint");

        string releaseOutput = Path.Combine(packageRoot, "1.6", "BridgeTools");
        string companionDll = Path.Combine(releaseOutput, "DevBridge2.BridgeTools.dll");
        Assert(File.Exists(companionDll) &&
               !File.Exists(Path.Combine(packageRoot, "BridgeTools", "DevBridge2.BridgeTools.dll")) &&
               !Directory.EnumerateFiles(releaseOutput, "RimBridgeServer.Sdk.dll",
                   SearchOption.AllDirectories).Any() &&
               !Directory.EnumerateFiles(packageRoot, "RimBridgeServer.Sdk.dll",
                   SearchOption.AllDirectories).Any(),
            "the package must place BridgeTools below 1.6 and exclude a private SDK runtime copy");
    }

    private static void TestBridgeToolsPublishRefreshesStaleDll()
    {
        if (SkipUnselectedBridgeToolsCoverage())
            return;

        string root = FindWorkspaceRoot();
        string packageRoot = Path.Combine(root, "src", "DevBridgeRuntime", "Package");
        string[] requiredFiles =
        [
            "DevBridge.cmd",
            Path.Combine("About", "About.xml"),
            Path.Combine("1.6", "BridgeTools", "DevBridge2.BridgeTools.dll"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.exe"),
            Path.Combine("Coordinator", "DevBridge.Coordinator.dll"),
            Path.Combine("1.6", "Assemblies", "DevBridge2.dll")
        ];

        foreach (string relativePath in requiredFiles)
            Assert(File.Exists(Path.Combine(packageRoot, relativePath)),
                "the RimLiaison-owned runtime package must contain " + relativePath);

        Assert(!File.Exists(Path.Combine(packageRoot, "BridgeTools", "DevBridge2.BridgeTools.dll")) &&
               !Directory.Exists(Path.Combine(packageRoot, "BridgeTools", "Source")) &&
               !File.Exists(Path.Combine(packageRoot, "RimBridgeServer.Sdk.dll")),
            "the owned package must retain normal runtime assets without obsolete or private SDK output");
    }

    private static void TestBridgeToolsWrongLocationDiagnostic()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.STOPPED
        });
        string wrongLocation = Path.Combine(fixture.Root, "BridgeTools");
        Directory.CreateDirectory(wrongLocation);
        File.WriteAllBytes(Path.Combine(wrongLocation, "DevBridge2.BridgeTools.dll"),
            Encoding.UTF8.GetBytes("mod-local companion"));

        JsonCommandResponse response = RunDoctor(fixture, out _, out _);
        Assert(response.Findings.Any(value => value.Code == "BRIDGETOOLS_WRONG_LOCATION"),
            "doctor must identify root-level companion output outside the active 1.6 load folder");
    }

    private static void TestRimBridgeCompanionDiagnosticCategory()
    {
        RimBridgeIntegrationState state = new()
        {
            CompanionErrorCode = RimBridgeIntegrationConstants.CompanionUnavailableCode,
            CompanionError = "The optional DevBridge generation-context tool is not registered."
        };
        Assert(RimBridgeCompanionDiagnostics.Code(state) ==
                   RimBridgeIntegrationConstants.CompanionToolNotRegisteredDiagnostic,
            "legacy unavailable state must expose the nonfatal tool-not-registered category");
    }

    private static bool SkipUnselectedBridgeToolsCoverage()
    {
        string scope = Environment.GetEnvironmentVariable("DEVBRIDGE_OFFLINE_TEST_SCOPE") ?? string.Empty;
        if (!scope.Equals("coordinator", StringComparison.OrdinalIgnoreCase))
            return false;

        Console.WriteLine("SKIP BridgeTools deployment coverage: BridgeTools is outside this coordinator-only impact plan.");
        return true;
    }


    private static string FindWorkspaceRoot()
    {
        DirectoryInfo directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "DevBridgeRuntime",
                    "BridgeTools", "DevBridge2.BridgeTools.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("DevBridge2 workspace root could not be located");
    }
}
