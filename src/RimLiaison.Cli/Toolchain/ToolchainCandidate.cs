using System.Text.Json.Serialization;
using RimLiaison.Git;
using RimLiaison.RimDev;

namespace RimLiaison.Toolchain;

public sealed record ToolchainCandidate(
    string SourceCommit,
    string CandidateRoot,
    string RimLiaisonExecutablePath,
    string RimLiaisonAssemblyPath,
    string DevBridgeRuntimeRoot,
    string DevBridgeRuntimeArtifactRoot,
    string DevBridgePackageSha256,
    string DevBridgeCoordinatorSha256,
    string TransactionConsumerPath,
    string RuntimeProtocolContract,
    string? RimWorldRoot = null,
    string? RimWorldManagedDirectory = null)
{
    [JsonPropertyName("rimLiaisonCliDeploymentRoot")]
    public string? RimLiaisonCliDeploymentRoot { get; init; }

    [JsonPropertyName("rimLiaisonCliDeploymentManifestPath")]
    public string? RimLiaisonCliDeploymentManifestPath { get; init; }

    [JsonPropertyName("rimLiaisonCliDeploymentManifestSha256")]
    public string? RimLiaisonCliDeploymentManifestSha256 { get; init; }

    [JsonPropertyName("rimLiaisonCliDeploymentPackageSha256")]
    public string? RimLiaisonCliDeploymentPackageSha256 { get; init; }

    [JsonPropertyName("rimLiaisonCliTargetFramework")]
    public string? RimLiaisonCliTargetFramework { get; init; }

    [JsonPropertyName("rimLiaisonExecutableSha256")]
    public string RimLiaisonExecutableSha256 =>
        ToolchainFileHash.Sha256(RimLiaisonExecutablePath);

    [JsonPropertyName("rimLiaisonAssemblySha256")]
    public string RimLiaisonAssemblySha256 =>
        ToolchainFileHash.Sha256(RimLiaisonAssemblyPath);

    [JsonPropertyName("transactionConsumerSha256")]
    public string TransactionConsumerSha256 =>
        ToolchainFileHash.Sha256(TransactionConsumerPath);
    [JsonPropertyName("devBridgeModSha256")]
    public string? DevBridgeModSha256 { get; init; }

    [JsonPropertyName("devBridgeRuntimeManifestSha256")]
    public string? DevBridgeRuntimeManifestSha256 { get; init; }
}

internal sealed record RimWorldManagedAssemblyResolution(
    bool Succeeded,
    string? RimWorldRoot,
    string? ManagedDirectory,
    string? MissingRequiredFile,
    string ResolutionSource,
    string? OldCheckoutRelativePath,
    string? ErrorCode = null,
    string? Error = null,
    string? NextAction = null,
    bool? ReleaseModBuilt = null)
{
    public object ToEvidence() => new
    {
        owner = "RimLiaison",
        status = Succeeded ? "ready" : "blocked",
        rimWorldRoot = RimWorldRoot,
        managedDirectory = ManagedDirectory,
        missingRequiredFile = MissingRequiredFile,
        resolutionSource = ResolutionSource,
        oldCheckoutRelativePath = OldCheckoutRelativePath,
        releaseModBuilt = ReleaseModBuilt,
        projectImplicated = false,
        errorCode = ErrorCode,
        error = Error,
        nextAction = NextAction
    };
}

internal static class RimWorldManagedAssemblyResolver
{
    private static readonly string[] RequiredAssemblies =
    [
        "Assembly-CSharp.dll",
        "UnityEngine.CoreModule.dll"
    ];

    public static RimWorldManagedAssemblyResolution Resolve(
        string sourceRoot,
        string devBridgeRoot)
    {
        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(null, sourceRoot);
        if (!workspace.Succeeded)
        {
            return Failure(
                null,
                null,
                workspace.Error ?? "The managed RimWorld environment configuration could not be resolved.",
                workspace.ErrorCode ?? "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                "managed workspace discovery",
                null,
                "Repair the managed .rimdev/workspace.json, then retry the DevBridge2 candidate build.");
        }

        string? configuredRoot = workspace.Configuration?.RimWorldRoot;
        string resolutionSource = "rimdev-workspace";
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
            resolutionSource = "RIMWORLD_ROOT";
        }
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Failure(
                null,
                null,
                "The canonical RimWorld installation root is unknown.",
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                resolutionSource,
                OldCheckoutRelativePath(devBridgeRoot),
                "Configure rimWorldRoot in the managed .rimdev/workspace.json or set RIMWORLD_ROOT.");
        }

        string rimWorldRoot;
        try
        {
            rimWorldRoot = Path.GetFullPath(
                Path.IsPathRooted(configuredRoot)
                    ? configuredRoot
                    : Path.Combine(workspace.RootPath, configuredRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Failure(
                configuredRoot,
                null,
                "The configured RimWorld installation root is invalid.",
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                resolutionSource,
                OldCheckoutRelativePath(devBridgeRoot),
                "Repair rimWorldRoot in the managed .rimdev/workspace.json or set RIMWORLD_ROOT.");
        }

        string managedDirectory = Path.Combine(
            rimWorldRoot,
            "RimWorldWin64_Data",
            "Managed");
        if (!Directory.Exists(managedDirectory))
        {
            return Failure(
                rimWorldRoot,
                managedDirectory,
                "The resolved RimWorld managed directory does not exist.",
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                resolutionSource,
                OldCheckoutRelativePath(devBridgeRoot),
                "Install or repair RimWorld, or correct rimWorldRoot in the managed .rimdev/workspace.json.");
        }

        string? missing = RequiredAssemblies
            .Select(name => Path.Combine(managedDirectory, name))
            .FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
        {
            return Failure(
                rimWorldRoot,
                managedDirectory,
                "A required RimWorld managed assembly is missing: " + Path.GetFileName(missing),
                "RIMWORLD_MANAGED_ASSEMBLIES_MISSING",
                resolutionSource,
                OldCheckoutRelativePath(devBridgeRoot),
                "Install or repair the RimWorld managed assemblies, then retry the DevBridge2 candidate build.",
                missing);
        }

        return new(
            true,
            rimWorldRoot,
            Path.GetFullPath(managedDirectory),
            null,
            resolutionSource,
            OldCheckoutRelativePath(devBridgeRoot));
    }

    private static RimWorldManagedAssemblyResolution Failure(
        string? rimWorldRoot,
        string? managedDirectory,
        string error,
        string errorCode,
        string resolutionSource,
        string? oldCheckoutRelativePath,
        string nextAction,
        string? missingRequiredFile = null) =>
        new(
            false,
            rimWorldRoot,
            managedDirectory,
            missingRequiredFile,
            resolutionSource,
            oldCheckoutRelativePath,
            errorCode,
            error,
            nextAction);

    private static string? OldCheckoutRelativePath(string devBridgeRoot)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(
                devBridgeRoot,
                "..",
                "..",
                "RimWorldWin64_Data",
                "Managed"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

internal sealed record ToolchainCandidateMaterializationResult(
    ToolchainCandidate? Candidate,
    string? ErrorCode,
    string? Error,
    string? NextAction)
{
    public RimWorldManagedAssemblyResolution? RimWorldManagedAssemblies { get; init; }

    public bool Succeeded => Candidate is not null;

    public static ToolchainCandidateMaterializationResult Failure(
        string code,
        string error,
        string nextAction,
        RimWorldManagedAssemblyResolution? rimWorldManagedAssemblies = null) =>
        new(null, code, error, nextAction)
        {
            RimWorldManagedAssemblies = rimWorldManagedAssemblies
        };
}
