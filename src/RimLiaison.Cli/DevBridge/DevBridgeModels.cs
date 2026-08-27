using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Recovery;

namespace RimLiaison.DevBridge;

public static class DevBridgeRecipeSchemas
{
    public const string Show = "devbridge-test-recipe-show/v1";
    public const string Plan = "devbridge-test-recipe-plan/v1";
    public const string Run = "devbridge-test-recipe-run/v1";
    public const string RecipeV1 = "devbridge-test-recipe/v1";
    public const string RecipeV2 = "devbridge-test-recipe/v2";
}

public static class DevBridgeDiagnosticSchemas
{
    public const string LogsQuery = "devbridge-logs-query/v1";
    public const string ScopedSource = "rimtest-devbridge-diagnostic-source/v1";
}

public static class DevBridgeProcessEnvironment
{
    public static IReadOnlyDictionary<string, string> ForWorkflow(string? workflowId)
    {
        string? configured = Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_AGENT");
        string agent = !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : string.IsNullOrWhiteSpace(workflowId)
                ? "rimtest-process-" + Environment.ProcessId.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                : "rimtest-workflow-" + StableSuffix(workflowId);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DEVBRIDGE_AGENT"] = agent
        };
    }

    private static string StableSuffix(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..24];
    }
}

public enum DevBridgeOutcomeKind
{
    Success,
    TestFailure,
    DevBridgeRefusal,
    InfrastructureFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public static class DevBridgeIdentityMismatchClassifications
{
    public const string InstallationRootOwner = "installationRootOwner";
    public const string CoordinatorIdentity = "coordinatorIdentity";
    public const string RuntimeGeneration = "runtimeGeneration";
    public const string RimWorldProcessIdentity = "rimWorldProcessIdentity";
    public const string StaleDescriptorProfileRegistration =
        "staleDescriptorProfileRegistration";
    public const string ProtocolSchema = "protocolSchema";
    public const string Unknown = "unknown";
}

public sealed record DevBridgeIdentityMismatch(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("expected")] string? Expected,
    [property: JsonPropertyName("actual")] string? Actual,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("recoverable")] bool Recoverable,
    [property: JsonPropertyName("authoritativeRoot")] string? AuthoritativeRoot = null,
    [property: JsonPropertyName("actualRoot")] string? ActualRoot = null,
    [property: JsonPropertyName("resolution")] string? Resolution = null);

public enum DevBridgeDiagnosticSourceOutcome
{
    Available,
    Unavailable,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public sealed record DevBridgeDiagnosticSourceStatus(
    DevBridgeDiagnosticSourceOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? Stderr = null)
{
    public bool IsAvailable => Outcome == DevBridgeDiagnosticSourceOutcome.Available;
}

public sealed record DevBridgeScopedDiagnosticSource(
    string SchemaVersion,
    int Generation,
    string Content,
    int SourceBytes,
    int RecordCount,
    bool Truncated,
    string Sha256,
    string? LaunchId = null);

public sealed record DevBridgeDiagnosticSourceResult(
    DevBridgeDiagnosticSourceStatus Status,
    DevBridgeScopedDiagnosticSource? Source);

public interface IDevBridgeDiagnosticSourceAdapter
{
    Task<DevBridgeDiagnosticSourceResult> AcquireAsync(
        string testId,
        DevBridgeRecipeRunResult run,
        CancellationToken cancellationToken = default);
}

public sealed record DevBridgeProcessResponse(
    bool? Success = null,
    bool? Healthy = null,
    int? ExitCode = null,
    string? ErrorCode = null,
    string? Error = null,
    string? NextAction = null,
    string? State = null,
    string? SchemaVersion = null,
    string? ProtocolVersion = null,
    string? BuildIdentity = null,
    JsonElement? Findings = null,
    JsonElement? RuntimeIdentity = null)
{
    public bool RepresentsFailure(int? processExitCode) =>
        ErrorCode is not null ||
        Success == false ||
        Healthy == false ||
        ExitCode is > 0 ||
        processExitCode is > 0;
}

public sealed record DevBridgeProcessEvidence(
    string? ResolvedExecutablePath,
    string? ToolRoot,
    string? WorkingDirectory,
    string? OperationKey,
    string? StdoutEvidenceId,
    string? StderrEvidenceId,
    string? ProcessEventId,
    long DurationMilliseconds,
    bool StdoutTruncated = false,
    bool StderrTruncated = false);
public sealed record DevBridgeAdapterStatus(
    DevBridgeOutcomeKind Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? Stderr = null,
    string? ResponseSchema = null,
    PrerequisiteRecoveryState RecoveryState = PrerequisiteRecoveryState.Ready,
    int RecoveryAttempts = 0,
    string? RecoveryAction = null,
    DevBridgeIdentityMismatch? IdentityMismatch = null,
    DevBridgeProcessResponse? Response = null,
    DevBridgeProcessEvidence? ProcessEvidence = null)
{
    public bool IsSuccess => Outcome == DevBridgeOutcomeKind.Success;
}

public sealed record DevBridgeProcessRequest(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaxStdoutBytes,
    int MaxStderrBytes,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? OperationKey = null);

public sealed record DevBridgeProcessResult(
    int? ExitCode,
    string? Stdout,
    string? Stderr,
    bool TimedOut = false,
    bool Cancelled = false,
    bool StdoutTruncated = false,
    bool StderrTruncated = false,
    string? StartError = null,
    DevBridgeProcessResponse? Response = null,
    DevBridgeProcessEvidence? Evidence = null);

public interface IDevBridgeProcessTransport
{
    Task<DevBridgeProcessResult> ExecuteAsync(
        DevBridgeProcessRequest request,
        CancellationToken cancellationToken);
}

public sealed record DevBridgeAdapterOptions
{
    public required string CommandPath { get; init; }
    public required string RootPath { get; init; }
    public string? SourceRootPath { get; init; }
    public string? RuntimeRootPath { get; init; }
    public string? PinnedWorktreeRootPath { get; init; }
    public TimeSpan ShowPlanTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(16);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    public static DevBridgeAdapterOptions Discover(
        string? commandPath = null,
        string? rootPath = null)
    {
        string selectedPath = ResolveCommandPath(commandPath);
        string fullPath = Path.GetFullPath(selectedPath);
        string selectedRoot = rootPath ??
            Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT") ??
            Path.GetDirectoryName(fullPath) ??
            Environment.CurrentDirectory;
        string fullRoot = Path.GetFullPath(selectedRoot);

        return new DevBridgeAdapterOptions
        {
            CommandPath = fullPath,
            RootPath = fullRoot,
            SourceRootPath = Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT") ??
                Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT") ??
                fullRoot,
            RuntimeRootPath = Environment.GetEnvironmentVariable("DEVBRIDGE_RUNTIME_ROOT"),
            PinnedWorktreeRootPath = Environment.GetEnvironmentVariable("DEVBRIDGE_PINNED_WORKTREE_ROOT") ??
                Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_PINNED_ROOT")
        };
    }

    private static string ResolveCommandPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? configured =
            Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_CMD") ??
            Environment.GetEnvironmentVariable("DEVBRIDGE_CMD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = new List<string>();
        AddSiblingCandidate(candidates, Environment.CurrentDirectory);
        AddSiblingCandidate(candidates, AppContext.BaseDirectory);

        return candidates.FirstOrDefault(File.Exists) ??
            candidates.FirstOrDefault() ??
            Path.Combine(Environment.CurrentDirectory, "..", "DevBridge2", "DevBridge.cmd");
    }

    private static void AddSiblingCandidate(
        ICollection<string> candidates,
        string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            candidates.Add(Path.Combine(directory, "..", "DevBridge2", "DevBridge.cmd"));
            candidates.Add(Path.Combine(directory, "DevBridge2", "DevBridge.cmd"));
            directory = Directory.GetParent(directory)?.FullName;
        }
    }
}

public sealed record DevBridgeRecipeShowResult(
    string RecipeId,
    DevBridgeAdapterStatus Status,
    JsonElement? Definition);

public sealed record DevBridgeRecipeExecutionContext(
    string? LeaseId = null,
    string? SourceFingerprint = null);

public sealed record DevBridgeLeaseResult(
    DevBridgeAdapterStatus Status,
    string? LeaseId,
    int? Generation)
{
    public bool IsUsable => Status.IsSuccess &&
        !string.IsNullOrWhiteSpace(LeaseId) &&
        Generation is > 0;
}

public sealed record DevBridgeResetResult(
    DevBridgeAdapterStatus Status,
    int? Generation,
    string? LeaseId)
{
    public bool IsUsable => Status.IsSuccess &&
        Generation is > 0 &&
        !string.IsNullOrWhiteSpace(LeaseId);
}

public sealed record DevBridgeFreshGenerationResult(
    DevBridgeAdapterStatus Status,
    int? Generation,
    int LaunchesConsumed = 0)
{
    public bool IsUsable => Status.IsSuccess && Generation is > 0;
}

public interface IDevBridgeLeaseAdapter
{
    Task<DevBridgeLeaseResult> BeginLeaseAsync(
        string? workflowId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeLeaseResult> RenewLeaseAsync(
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default);

    Task<DevBridgeLeaseResult> EndLeaseAsync(
        string leaseId,
        string? workflowId,
        CancellationToken cancellationToken = default);
}

public interface IDevBridgeFixtureResetAdapter
{
    Task<DevBridgeResetResult> ResetAsync(
        string resetRecipeId,
        string leaseId,
        int expectedGeneration,
        string? workflowId,
        CancellationToken cancellationToken = default);
}

public interface IDevBridgeFreshGenerationAdapter
{
    Task<DevBridgeFreshGenerationResult> EnsureFreshGenerationAsync(
        string recipeId,
        int? previousGeneration,
        string? workflowId,
        CancellationToken cancellationToken = default);
}

public sealed record DevBridgeRecipePlanStep(
    string Action,
    string? ReasonCode,
    string? Condition,
    string? Recipe);

public sealed record DevBridgeRecipePlan(
    string RecipeId,
    bool AlreadySatisfied,
    int EstimatedRimWorldLaunches,
    IReadOnlyList<DevBridgeRecipePlanStep> Steps,
    string? NextAction,
    IReadOnlyList<string> BlockedBy);

public sealed record DevBridgeRecipePlanResult(
    string RecipeId,
    DevBridgeAdapterStatus Status,
    DevBridgeRecipePlan? Plan);

public sealed record DevBridgeOperationSummary(
    string Tool,
    bool Success,
    string? ErrorCode,
    IReadOnlyList<string> FailedAssertionPointers,
    string? OperationId = null,
    string? WorkflowId = null,
    int? Generation = null,
    string? LaunchId = null);

public sealed record DevBridgeRecipeRunResult(
    string RecipeId,
    DevBridgeAdapterStatus Status,
    bool? Passed,
    string? RunId,
    int? Generation,
    string? LeaseId,
    string? Evidence,
    string? EvidenceId,
    string? FailureFingerprint,
    string? FinalNextAction,
    bool? RestartRequired,
    int? LaunchesConsumed,
    IReadOnlyList<DevBridgeOperationSummary> Operations,
    string? WorkflowId = null);
