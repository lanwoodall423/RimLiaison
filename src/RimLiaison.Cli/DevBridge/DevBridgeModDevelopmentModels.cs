namespace RimLiaison.DevBridge;

public static class DevBridgeModDevelopmentSchemas
{
    public const string Current = "devbridge-mod-development/v1";
}

public sealed record DevBridgeArtifactFreshness(
    string? SourceFingerprint,
    string? BuiltArtifactSha256,
    string? DeployedArtifactSha256,
    string? DeploymentDecision,
    int? GenerationBefore,
    int? GenerationAfter,
    int? Generation,
    bool LoadedArtifactFreshnessProven,
    string? Proof,
    string? TransactionId,
    string? WorkflowId,
    string? LeaseId,
    string? ErrorCode = null);

public sealed record DevBridgeModDevelopmentResult(
    string Project,
    DevBridgeAdapterStatus Status,
    bool? Success,
    string? TransactionId,
    string? WorkflowId,
    int? Generation,
    string? LeaseId,
    DevBridgeArtifactFreshness? Freshness);

public sealed record DevBridgeModDevelopmentExecutionContext(
    string? LeaseId = null);

public interface IDevBridgeModDevelopmentAdapter
{
    Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        DevBridgeModDevelopmentExecutionContext? executionContext,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            project,
            repositoryRoot,
            sourceFingerprint,
            workflowId,
            cancellationToken);
}

public sealed record DevBridgeModDevelopmentAdapterOptions
{
    public required string RootPath { get; init; }
    public string? DescriptorPath { get; init; }
    public string? DeploymentRoot { get; init; }
    public IReadOnlyList<string>? ChangedPaths { get; init; }
    public string? TestRecipe { get; init; }
    public string? Configuration { get; init; }
    public string? DeploymentTarget { get; init; }
    public bool EnableDescriptorRecovery { get; init; } = true;
    public bool PreserveDescriptorBackup { get; init; } = true;
    public string PowerShellPath { get; init; } = "pwsh";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    public static DevBridgeModDevelopmentAdapterOptions Discover(
        string rootPath,
        string? descriptorPath = null,
        string? deploymentRoot = null)
    {
        string? configuredPowerShell =
            Environment.GetEnvironmentVariable("RIMTEST_POWERSHELL") ??
            Environment.GetEnvironmentVariable("POWERSHELL");

        return new DevBridgeModDevelopmentAdapterOptions
        {
            RootPath = Path.GetFullPath(rootPath),
            DescriptorPath = string.IsNullOrWhiteSpace(descriptorPath)
                ? null
                : Path.GetFullPath(descriptorPath),
            DeploymentRoot = string.IsNullOrWhiteSpace(deploymentRoot)
                ? null
                : Path.GetFullPath(deploymentRoot),
            PowerShellPath = string.IsNullOrWhiteSpace(configuredPowerShell)
                ? "pwsh"
                : configuredPowerShell
        };
    }
}
