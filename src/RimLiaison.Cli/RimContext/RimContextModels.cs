using System.Text.Json.Serialization;

using RimLiaison.Recovery;

namespace RimLiaison.RimContext;

public static class RimContextSchemas
{
    public const string Envelope = "rimctx/v1";
}

public enum RimContextImpactOutcome
{
    Success,
    Unknown,
    Unavailable,
    InfrastructureFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema,
    InvalidInput
}

public sealed record RimContextAdapterStatus(
    RimContextImpactOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? ResponseSchema = null,
    PrerequisiteRecoveryState RecoveryState = PrerequisiteRecoveryState.Ready,
    int RecoveryAttempts = 0,
    string? RecoveryAction = null)
{
    public bool IsSuccess => Outcome == RimContextImpactOutcome.Success;
}

public interface IRimContextImpactAdapter
{
    Task<RimContextImpactResult> AffectedAsync(
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default);
}

public sealed record RimContextImpact(
    string Tier,
    string Kind,
    string Id,
    string? Name,
    string? File,
    int? Line,
    string? Reason,
    string? Confidence);

public sealed record RimContextImpactResult(
    RimContextAdapterStatus Status,
    IReadOnlyList<string> Changed,
    IReadOnlyList<RimContextImpact> Impacts,
    bool Truncated);

public sealed record RimContextAdapterOptions
{
    /// <summary>
    /// Retained for older configuration objects; RimLiaison does not launch
    /// this path during normal operation.
    /// </summary>
    public string? CommandPath { get; init; }
    public required string RootPath { get; init; }
    public string? StorePath { get; init; }
    public int Depth { get; init; } = 8;
    public int Limit { get; init; } = 100;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(75);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    public static RimContextAdapterOptions Discover(
        string? commandPath = null,
        string? rootPath = null,
        string? storePath = null,
        int depth = 8,
        int limit = 100)
    {
        string selectedPath = ResolveCommandPath(commandPath);
        string commandForProcess = Path.IsPathRooted(selectedPath) ||
            selectedPath.Contains(Path.DirectorySeparatorChar) ||
            selectedPath.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(selectedPath)
            : selectedPath;
        string selectedRoot = rootPath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT") ??
            Environment.CurrentDirectory;
        string? selectedStore = storePath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_STORE") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_STORE");

        return new RimContextAdapterOptions
        {
            CommandPath = commandForProcess,
            RootPath = Path.GetFullPath(selectedRoot),
            StorePath = string.IsNullOrWhiteSpace(selectedStore)
                ? null
                : Path.GetFullPath(selectedStore),
            Depth = depth,
            Limit = limit
        };
    }

    private static string ResolveCommandPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? configured =
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_CMD") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_CMD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = new List<string>();
        AddBundledCandidates(candidates, Environment.CurrentDirectory);
        AddBundledCandidates(candidates, AppContext.BaseDirectory);
        return candidates.FirstOrDefault(File.Exists) ??
            "rimctx";
    }

    private static void AddBundledCandidates(
        ICollection<string> candidates,
        string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);
        for (int depth = 0; depth < 10 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            candidates.Add(Path.Combine(directory, "rimctx.cmd"));
            candidates.Add(Path.Combine(
                directory,
                "src",
                "RimContext.Cli",
                "bin",
                "Release",
                "net8.0",
                "rimctx.exe"));
            candidates.Add(Path.Combine(
                directory,
                "src",
                "RimContext.Cli",
                "bin",
                "Debug",
                "net8.0",
                "rimctx.exe"));
            directory = Directory.GetParent(directory)?.FullName;
        }
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentException("RimContext root path is required.");
        }

        if (Depth is < 1 or > 8 ||
            Limit is < 1 or > 100 ||
            Timeout <= TimeSpan.Zero ||
            MaxStdoutBytes <= 0 ||
            MaxStderrBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RimContextAdapterOptions));
        }
    }
}

public static class RimTestSelectionSchema
{
    public const string Current = "rimtest-selection/v1";
}

public sealed class RimTestSelectionResult
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = RimTestSelectionSchema.Current;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("tests")]
    public IReadOnlyList<string> Tests { get; init; } = [];

    [JsonPropertyName("reasonCount")]
    public int ReasonCount { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }

    [JsonPropertyName("recoveryState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryState { get; init; }

    [JsonPropertyName("recoveryAttempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RecoveryAttempts { get; init; }

    [JsonPropertyName("recoveryAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryAction { get; init; }

    [JsonPropertyName("fallbackSuite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackSuite { get; init; }

    [JsonPropertyName("reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestSelectionReason>? Reasons { get; init; }

    [JsonPropertyName("reasonsTruncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReasonsTruncated { get; init; }

    [JsonPropertyName("executionPacket")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::RimContext.Core.Impact.ExecutionPacket? ExecutionPacket { get; init; }

    [JsonPropertyName("impactPrediction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::RimContext.Core.Impact.PredictedImpact? ImpactPrediction { get; init; }

    [JsonPropertyName("impactAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::RimContext.Core.Impact.ActualImpact? ImpactAnalysis { get; init; }

    [JsonPropertyName("validationPlan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public global::RimContext.Core.Impact.ValidationPlan? ValidationPlan { get; init; }


    public RimTestSelectionResult WithImpact(
        global::RimContext.Core.Impact.ExecutionPacket? packet,
        global::RimContext.Core.Impact.PredictedImpact? prediction,
        global::RimContext.Core.Impact.ActualImpact? analysis,
        global::RimContext.Core.Impact.ValidationPlan? validationPlan = null) =>
        new()
        {
            SchemaVersion = SchemaVersion,
            Status = Status,
            Tests = Tests,
            ReasonCount = ReasonCount,
            ErrorCode = ErrorCode,
            NextAction = NextAction,
            RecoveryState = RecoveryState,
            RecoveryAttempts = RecoveryAttempts,
            RecoveryAction = RecoveryAction,
            FallbackSuite = FallbackSuite,
            Reasons = Reasons,
            ReasonsTruncated = ReasonsTruncated,
            ExecutionPacket = packet,
            ImpactPrediction = prediction,
            ImpactAnalysis = analysis,
            ValidationPlan = validationPlan
        };

    public RimTestSelectionResult WithTests(
        IReadOnlyList<string> tests,
        string? status = null) =>
        new()
        {
            SchemaVersion = SchemaVersion,
            Status = status ?? Status,
            Tests = tests,
            ReasonCount = ReasonCount,
            ErrorCode = ErrorCode,
            NextAction = NextAction,
            RecoveryState = RecoveryState,
            RecoveryAttempts = RecoveryAttempts,
            RecoveryAction = RecoveryAction,
            FallbackSuite = FallbackSuite,
            Reasons = Reasons,
            ReasonsTruncated = ReasonsTruncated,
            ExecutionPacket = ExecutionPacket,
            ImpactPrediction = ImpactPrediction,
            ImpactAnalysis = ImpactAnalysis,
            ValidationPlan = ValidationPlan
        };
}

public sealed class RimTestSelectionReason
{
    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Confidence { get; init; }

    [JsonPropertyName("tests")]
    public IReadOnlyList<string> Tests { get; init; } = [];
}
