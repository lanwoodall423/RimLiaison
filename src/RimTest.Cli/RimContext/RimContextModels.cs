using System.Text.Json.Serialization;

namespace RimTest.RimContext;

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
    string? ResponseSchema = null)
{
    public bool IsSuccess => Outcome == RimContextImpactOutcome.Success;
}

public sealed record RimContextProcessRequest(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaxStdoutBytes,
    int MaxStderrBytes);

public sealed record RimContextProcessResult(
    int? ExitCode,
    string? Stdout,
    string? Stderr,
    bool TimedOut = false,
    bool Cancelled = false,
    bool StdoutTruncated = false,
    bool StderrTruncated = false,
    string? StartError = null);

public interface IRimContextProcessTransport
{
    Task<RimContextProcessResult> ExecuteAsync(
        RimContextProcessRequest request,
        CancellationToken cancellationToken);
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
    public required string CommandPath { get; init; }
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
        string fullPath = Path.GetFullPath(selectedPath);
        string selectedRoot = rootPath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT") ??
            Environment.CurrentDirectory;
        string? selectedStore = storePath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_STORE") ??
            Environment.GetEnvironmentVariable("RIMCONTEXT_STORE");

        return new RimContextAdapterOptions
        {
            CommandPath = fullPath,
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
        AddSiblingCandidates(candidates, Environment.CurrentDirectory);
        AddSiblingCandidates(candidates, AppContext.BaseDirectory);
        return candidates.FirstOrDefault(File.Exists) ??
            candidates.FirstOrDefault() ??
            Path.Combine(Environment.CurrentDirectory, "..", "RimContext", "rimctx.cmd");
    }

    private static void AddSiblingCandidates(
        ICollection<string> candidates,
        string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            candidates.Add(Path.Combine(directory, "..", "RimContext", "rimctx.cmd"));
            candidates.Add(Path.Combine(directory, "RimContext", "rimctx.cmd"));
            directory = Directory.GetParent(directory)?.FullName;
        }
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(CommandPath) ||
            string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentException("RimContext command path and root path are required.");
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

    [JsonPropertyName("fallbackSuite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackSuite { get; init; }

    [JsonPropertyName("reasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestSelectionReason>? Reasons { get; init; }

    [JsonPropertyName("reasonsTruncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReasonsTruncated { get; init; }
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
