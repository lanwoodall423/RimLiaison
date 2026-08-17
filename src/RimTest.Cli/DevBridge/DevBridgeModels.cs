using System.Text.Json;

namespace RimTest.DevBridge;

public static class DevBridgeRecipeSchemas
{
    public const string Show = "devbridge-test-recipe-show/v1";
    public const string Plan = "devbridge-test-recipe-plan/v1";
    public const string Run = "devbridge-test-recipe-run/v1";
    public const string RecipeV1 = "devbridge-test-recipe/v1";
    public const string RecipeV2 = "devbridge-test-recipe/v2";
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

public sealed record DevBridgeAdapterStatus(
    DevBridgeOutcomeKind Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? Stderr = null,
    string? ResponseSchema = null)
{
    public bool IsSuccess => Outcome == DevBridgeOutcomeKind.Success;
}

public sealed record DevBridgeProcessRequest(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaxStdoutBytes,
    int MaxStderrBytes);

public sealed record DevBridgeProcessResult(
    int? ExitCode,
    string? Stdout,
    string? Stderr,
    bool TimedOut = false,
    bool Cancelled = false,
    bool StdoutTruncated = false,
    bool StderrTruncated = false,
    string? StartError = null);

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

        return new DevBridgeAdapterOptions
        {
            CommandPath = fullPath,
            RootPath = Path.GetFullPath(selectedRoot)
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
