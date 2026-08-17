using System.Text.Json.Serialization;

namespace RimTest.RimError;

public static class RimErrorSchemas
{
    public const string Integration = "rimerror-integration/v1";
}

public enum RimErrorDiagnosisOutcome
{
    Available,
    Empty,
    Unavailable,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompatibleSchema
}

public sealed record RimErrorAdapterStatus(
    RimErrorDiagnosisOutcome Outcome,
    string? ErrorCode = null,
    string? Error = null,
    int? ProcessExitCode = null,
    string? Stderr = null)
{
    public bool IsAvailable => Outcome == RimErrorDiagnosisOutcome.Available;
}

public sealed record RimErrorDiagnosisResult(
    RimErrorDiagnosisOutcome Outcome,
    RimErrorAdapterStatus Status,
    RimErrorDiagnosticSummary? Diagnosis,
    string? ReportStatus)
{
    public bool IsAvailable => Outcome == RimErrorDiagnosisOutcome.Available;
}

public sealed record RimErrorDiagnosisRequest(
    string TestId,
    string? RunId,
    int? Generation,
    string? EvidenceId,
    string? FailureFingerprint,
    string? FailureCode);

/// <summary>
/// The bounded fields RimTest exposes from RimError's current latest --json
/// root-cause/diagnostic summaries. Field names follow RimError's report.
/// </summary>
public sealed record RimErrorDiagnosticSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }

    [JsonPropertyName("def")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Def { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Confidence { get; init; }
}

public sealed record RimErrorAdapterOptions
{
    public required string CommandPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? LogPath { get; init; }
    public string? StorePath { get; init; }
    public TimeSpan IngestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan LatestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(LogPath) ||
        !string.IsNullOrWhiteSpace(StorePath);

    public static RimErrorAdapterOptions Discover(
        string? commandPath = null,
        string? logPath = null,
        string? storePath = null)
    {
        string selectedCommand = ResolveCommandPath(commandPath);
        string commandForProcess = Path.IsPathRooted(selectedCommand) ||
            selectedCommand.Contains(Path.DirectorySeparatorChar) ||
            selectedCommand.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(selectedCommand)
            : selectedCommand;
        string workingDirectory = Path.GetDirectoryName(
                Path.GetFullPath(selectedCommand)) ??
            Environment.CurrentDirectory;

        string? selectedLog = logPath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMERROR_LOG") ??
            Environment.GetEnvironmentVariable("RIMERROR_LOG");
        string? selectedStore = storePath ??
            Environment.GetEnvironmentVariable("RIMTEST_RIMERROR_STORE") ??
            Environment.GetEnvironmentVariable("RIMERROR_STATE_PATH");

        if (string.IsNullOrWhiteSpace(selectedLog))
        {
            selectedLog = null;
        }
        else
        {
            selectedLog = Path.GetFullPath(selectedLog);
            selectedStore ??= Path.Combine(
                Environment.CurrentDirectory,
                ".rimerror",
                "latest.json");
        }

        if (string.IsNullOrWhiteSpace(selectedStore))
        {
            selectedStore = null;
        }
        else
        {
            selectedStore = Path.GetFullPath(selectedStore);
        }

        return new RimErrorAdapterOptions
        {
            CommandPath = commandForProcess,
            WorkingDirectory = workingDirectory,
            LogPath = selectedLog,
            StorePath = selectedStore
        };
    }

    private static string ResolveCommandPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? configured =
            Environment.GetEnvironmentVariable("RIMTEST_RIMERROR_CMD") ??
            Environment.GetEnvironmentVariable("RIMERROR_CMD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = new List<string>();
        AddSiblingCandidates(candidates, Environment.CurrentDirectory);
        AddSiblingCandidates(candidates, AppContext.BaseDirectory);
        return candidates.FirstOrDefault(File.Exists) ?? "rimerror";
    }

    private static void AddSiblingCandidates(
        ICollection<string> candidates,
        string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            candidates.Add(Path.Combine(
                directory,
                "..",
                "RimError",
                "src",
                "RimError.Cli",
                "bin",
                "Release",
                "net8.0",
                "rimerror.exe"));
            candidates.Add(Path.Combine(
                directory,
                "RimError",
                "src",
                "RimError.Cli",
                "bin",
                "Release",
                "net8.0",
                "rimerror.exe"));
            directory = Directory.GetParent(directory)?.FullName;
        }
    }
}
