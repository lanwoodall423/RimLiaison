using System.Text.Json;

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
    string? ErrorCode = null,
    string? BuiltPackageSha256 = null,
    string? DeployedPackageSha256 = null,
    string? DeploymentManifestPath = null,
    string? RecipeId = null,
    string? RecipeOwner = null,
    string? RecipeSource = null,
    string? RecipeSha256 = null,
    string? RecipeSchemaVersion = null);

/// <summary>
/// Structured diagnostics returned by the DevBridge2 mod-development
/// transaction. Keep this separate from the adapter status so a failed
/// transaction can carry the compiler/build reason all the way to
/// observability without flattening it into a short error message.
/// </summary>
public sealed record DevBridgeBuildDiagnostics(
    string? Command,
    int? ExitCode,
    string? Output,
    string? SourceProject,
    string? StagingPath,
    bool? TimedOut,
    string? BuiltSha256,
    string? DiagnosticOutput = null,
    string? ErrorOutput = null,
    string? Configuration = null,
    bool? Cancelled = null,
    string? WorkingDirectory = null,
    string? SourceFingerprint = null,
    string? FailureMessage = null,
    string? TransactionId = null,
    string? WorkflowId = null,
    string? ErrorCode = null,
    bool? OutputTruncated = null,
    string? CausalDiagnostic = null,
    bool? CausalDiagnosticTruncated = null,
    string? DiagnosticSignature = null,
    string? RawStdoutPath = null,
    string? RawStderrPath = null,
    string? RawNativeStdoutPath = null,
    string? RawNativeStderrPath = null,
    string? Orchestrator = null,
    string? FailureSurface = null,
    string? LikelyOwner = null,
    string? OwnershipConfidence = null,
    string? OwnershipBasis = null,
    JsonElement? Discrimination = null);

public sealed record DevBridgeBuildOutputEvidence(
    string RepositoryPath,
    string Sha256,
    string? TransactionId);

public sealed record DevBridgeModDevelopmentResult(
    string Project,
    DevBridgeAdapterStatus Status,
    bool? Success,
    string? TransactionId,
    string? WorkflowId,
    int? Generation,
    string? LeaseId,
    DevBridgeArtifactFreshness? Freshness,
    DevBridgeBuildDiagnostics? Build = null,
    IReadOnlyList<DevBridgeBuildOutputEvidence>? BuildOutputs = null);

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
    public string? ScriptRootPath { get; init; }
    public string? TransactionConsumerPath { get; init; }
    public string? DescriptorPath { get; init; }
    public string? DeploymentRoot { get; init; }
    public IReadOnlyList<string>? ChangedPaths { get; init; }
    public string? TestRecipe { get; init; }
    public string? Configuration { get; init; }
    public string? DeploymentTarget { get; init; }
    public bool EnableDescriptorRecovery { get; init; } = true;
    public bool PreserveDescriptorBackup { get; init; } = true;
    public bool UseInternalTransaction { get; init; }
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
            ScriptRootPath = Path.GetFullPath(
                Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT") ??
                Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT") ??
                rootPath),
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
