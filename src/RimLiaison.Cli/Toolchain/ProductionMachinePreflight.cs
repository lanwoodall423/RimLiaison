using System.Diagnostics;
using System.Text.Json.Serialization;
using RimLiaison.RimDev;

namespace RimLiaison.Toolchain;

public sealed record ProductionMachinePreflightResult(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("rimWorldRoot")] string? RimWorldRoot,
    [property: JsonPropertyName("rimWorldExecutable")] string? RimWorldExecutable,
    [property: JsonPropertyName("managedDirectory")] string? ManagedDirectory,
    [property: JsonPropertyName("modsConfigPath")] string? ModsConfigPath,
    [property: JsonPropertyName("productionRuntimeRoot")] string? ProductionRuntimeRoot,
    [property: JsonPropertyName("rimWorldProcessRunning")] bool RimWorldProcessRunning)
{
    public bool Passed => string.Equals(Status, "ready", StringComparison.Ordinal);

    public static ProductionMachinePreflightResult Blocked(
        string code,
        string error,
        string? root = null,
        string? executable = null,
        string? managedDirectory = null,
        string? modsConfigPath = null,
        string? runtimeRoot = null,
        bool processRunning = false) => new(
        "rimliaison-production-machine-preflight/v1",
        "blocked",
        code,
        error,
        root,
        executable,
        managedDirectory,
        modsConfigPath,
        runtimeRoot,
        processRunning);
}

public interface IPromotionMachinePreflightVerifier
{
    ProductionMachinePreflightResult Verify(string sourceRoot, string productionRuntimeRoot);
}

internal sealed class ProductionMachinePreflightVerifier : IPromotionMachinePreflightVerifier
{
    private readonly Func<bool> processProbe;

    public ProductionMachinePreflightVerifier(Func<bool>? processProbe = null)
    {
        this.processProbe = processProbe ?? RimWorldProcessRunning;
    }
    public ProductionMachinePreflightResult Verify(string sourceRoot, string productionRuntimeRoot)
    {
        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(null, sourceRoot);
        string? configuredRoot = workspace.Configuration?.RimWorldRoot ??
            Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string? configuredExecutable = workspace.Configuration?.RimWorldExecutable ??
            Environment.GetEnvironmentVariable("RIMWORLD_EXECUTABLE");
        string? root = ResolvePath(configuredRoot, workspace.RootPath);
        string? executable = ResolvePath(configuredExecutable, workspace.RootPath);
        if (string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(executable))
            root = Directory.GetParent(executable)?.FullName;
        if (string.IsNullOrWhiteSpace(executable) && !string.IsNullOrWhiteSpace(root))
            executable = Path.Combine(root, "RimWorldWin64.exe");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return ProductionMachinePreflightResult.Blocked(
                "RIMWORLD_ROOT_NOT_FOUND",
                "The canonical RimWorld installation root does not exist.",
                root,
                executable,
                runtimeRoot: productionRuntimeRoot);
        string managedDirectory = Path.Combine(root, "RimWorldWin64_Data", "Managed");
        if (!File.Exists(executable))
            return ProductionMachinePreflightResult.Blocked(
                "RIMWORLD_EXECUTABLE_NOT_FOUND",
                "The canonical RimWorld executable does not exist.",
                root,
                executable,
                managedDirectory,
                runtimeRoot: productionRuntimeRoot);
        if (!Directory.Exists(managedDirectory) ||
            !File.Exists(Path.Combine(managedDirectory, "Assembly-CSharp.dll")) ||
            !File.Exists(Path.Combine(managedDirectory, "UnityEngine.CoreModule.dll")))
            return ProductionMachinePreflightResult.Blocked(
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                "The canonical RimWorld managed assemblies are incomplete.",
                root,
                executable,
                managedDirectory,
                runtimeRoot: productionRuntimeRoot);

        string modsConfig = ResolveModsConfigPath();
        if (!File.Exists(modsConfig))
            return ProductionMachinePreflightResult.Blocked(
                "RIMWORLD_PROFILE_NOT_INITIALIZED",
                "The RimWorld user profile has not initialized ModsConfig.xml.",
                root,
                executable,
                managedDirectory,
                modsConfig,
                productionRuntimeRoot);

        if (processProbe())
            return ProductionMachinePreflightResult.Blocked(
                "RIMWORLD_PROCESS_RUNNING",
                "RimWorld is running; canonical activation requires a quiescent process.",
                root,
                executable,
                managedDirectory,
                modsConfig,
                productionRuntimeRoot,
                processRunning: true);

        string runtimeRoot = Path.GetFullPath(productionRuntimeRoot);
        string? parent = Directory.GetParent(runtimeRoot)?.FullName;
        string expectedModsRoot = Path.Combine(root, "Mods");
        if (parent is null || !Directory.Exists(parent) ||
            !IsWithin(expectedModsRoot, runtimeRoot) ||
            File.Exists(runtimeRoot))
            return ProductionMachinePreflightResult.Blocked(
                "RIMLIAISON_RUNTIME_DESTINATION_INVALID",
                "The production runtime destination is not a valid RimWorld Mods destination.",
                root,
                executable,
                managedDirectory,
                modsConfig,
                runtimeRoot);
        if ((new DirectoryInfo(parent).Attributes & FileAttributes.ReadOnly) != 0)
            return ProductionMachinePreflightResult.Blocked(
                "RIMLIAISON_RUNTIME_DESTINATION_UNWRITABLE",
                "The production runtime destination parent is read-only.",
                root,
                executable,
                managedDirectory,
                modsConfig,
                runtimeRoot);

        return new(
            "rimliaison-production-machine-preflight/v1",
            "ready",
            null,
            null,
            root,
            executable,
            managedDirectory,
            modsConfig,
            runtimeRoot,
            false);
    }

    private static string ResolveModsConfigPath()
    {
        string? configured = Environment.GetEnvironmentVariable("RIMLIAISON_MODS_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(
            localAppData,
            "..",
            "LocalLow",
            "Ludeon Studios",
            "RimWorld by Ludeon Studios",
            "Config",
            "ModsConfig.xml"));
    }

    private static string? ResolvePath(string? configured, string baseRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(baseRoot, configured));
    }

    private static bool RimWorldProcessRunning() =>
        Process.GetProcessesByName("RimWorldWin64").Length > 0 ||
        Process.GetProcessesByName("RimWorldWin64Steam").Length > 0;

    private static bool IsWithin(string root, string path)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
