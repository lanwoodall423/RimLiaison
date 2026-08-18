using RimError.Core;

namespace RimError.Cli;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int DiagnosticFailureExitCode = 1;
    private const int OperationalFailureExitCode = 2;
    private const string DefaultStorePath = ".rimerror/latest.json";

    private static readonly string[] Commands =
    [
        "ingest",
        "status",
        "latest",
        "show",
        "compare",
        "baseline",
        "export"
    ];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args))
            {
                PrintUsage();
                return SuccessExitCode;
            }

            if (!Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return OperationalFailureExitCode;
            }

            return args[0].ToLowerInvariant() switch
            {
                "ingest" => await RunIngestAsync(args[1..]),
                "status" => await RunStatusAsync(args[1..]),
                "latest" => await RunLatestAsync(args[1..]),
                "show" => await RunShowAsync(args[1..]),
                "compare" => await RunCompareAsync(args[1..]),
                "baseline" => await RunBaselineAsync(args[1..]),
                "export" => await RunExportAsync(args[1..]),
                _ => RunReservedCommand(args[0])
            };
        }
        catch (OperationCanceledException exception)
        {
            Console.Error.WriteLine($"rimerror: operation cancelled: {exception.Message}");
            return OperationalFailureExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"rimerror: {exception.Message}");
            return OperationalFailureExitCode;
        }
    }

    private static int RunReservedCommand(string command)
    {
        Console.Error.WriteLine(
            $"'{command}' is reserved but not implemented yet.");
        return OperationalFailureExitCode;
    }

    private static async Task<int> RunIngestAsync(string[] args)
    {
        if (args.Any(IsHelpFlag))
        {
            PrintIngestUsage();
            return SuccessExitCode;
        }

        var parsed = ParseStoreOption(args);
        var paths = new List<string>();
        var readStdin = false;
        string? runId = null;
        string? testId = null;
        string? operationId = null;
        string? operationName = null;
        string? attribution = null;
        string? rimWorldVersion = null;
        string? modProfile = null;
        string? projectPath = null;
        string? indexCachePath = null;
        var integrationPaths = new List<(string Path, string Provider)>();

        for (var index = 0; index < parsed.Arguments.Count; index++)
        {
            var argument = parsed.Arguments[index];
            switch (argument)
            {
                case "--stdin":
                    readStdin = true;
                    break;
                case "--json":
                case "--stack":
                    // Ingest always stores full bounded evidence and emits a compact report.
                    break;
                case "--run":
                    runId = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--test":
                    testId = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--operation":
                    operationId = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--operation-name":
                    operationName = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--attribution":
                    attribution = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--rimworld-version":
                    rimWorldVersion = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--mod-profile":
                    modProfile = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--project":
                    projectPath = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--index-cache":
                    indexCachePath = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--devbridge":
                    integrationPaths.Add((
                        ReadOptionValue(parsed.Arguments, ref index, argument),
                        "devbridge"));
                    break;
                case "--rimbridge":
                    integrationPaths.Add((
                        ReadOptionValue(parsed.Arguments, ref index, argument),
                        "rimbridge"));
                    break;
                case "--integration":
                    integrationPaths.Add((
                        ReadOptionValue(parsed.Arguments, ref index, argument),
                        "integration"));
                    break;
                default:
                    RejectUnknownOption(argument);
                    paths.Add(argument);
                    break;
            }
        }

        if (readStdin && paths.Count > 0)
        {
            throw new ArgumentException("Use --stdin or input paths, not both.");
        }

        var integration = await LoadIntegrationAsync(integrationPaths);
        var metadata = new DiagnosticIngestionMetadata
        {
            RunId = runId,
            TestId = testId,
            OperationId = operationId,
            OperationName = operationName,
            SourceAttribution = attribution,
            RimWorldVersion = rimWorldVersion,
            ModProfile = modProfile,
            Integration = integration
        };
        var store = CreateStore(parsed.StorePath);
        var service = new RimErrorService();
        RimErrorIngestResult result = paths.Count == 0
            ? await service.IngestAsync(
                new RimErrorIngestRequest(
                    [new DiagnosticSourceInput
                    {
                        Source = "stdin",
                        Reader = Console.In,
                        Metadata = metadata
                    }],
                    ProjectPath: projectPath,
                    IndexCachePath: indexCachePath,
                    Store: store))
            : await service.IngestFilesAsync(
                paths,
                metadata,
                projectPath: projectPath,
                indexCachePath: indexCachePath,
                store: store);
        Console.WriteLine(DiagnosticJson.Serialize(result.Report));
        return ExitCodeFor(result.Report);
    }

    private static async Task<int> RunBaselineAsync(string[] args)
    {
        if (args.Any(IsHelpFlag))
        {
            PrintBaselineUsage();
            return SuccessExitCode;
        }

        var parsed = ParseStoreOption(args);
        if (parsed.Arguments.Count == 0)
        {
            throw new ArgumentException(
                "Usage: rimerror baseline create [name] | list | show <name>");
        }

        var command = parsed.Arguments[0].ToLowerInvariant();
        var baselineStore = CreateBaselineStore(parsed.StorePath);
        switch (command)
        {
            case "create":
                {
                    if (parsed.Arguments.Count > 2)
                    {
                        throw new ArgumentException("Usage: rimerror baseline create [name]");
                    }

                    var name = parsed.Arguments.Count == 2
                        ? parsed.Arguments[1]
                        : DiagnosticBaselineNames.Default;
                    var snapshot = await ReadSnapshotAsync(parsed.StorePath);
                    if (snapshot is null)
                    {
                        throw new FileNotFoundException(
                            "No latest run exists; ingest diagnostics before creating a baseline.");
                    }

                    var baseline = DiagnosticBaseline.FromSnapshot(
                        name,
                        snapshot,
                        DateTimeOffset.UtcNow);
                    await baselineStore.WriteAsync(baseline);
                    Console.WriteLine(
                        $"created baseline {name} diagnostics={baseline.Items.Length}");
                    return SuccessExitCode;
                }
            case "list":
                EnsureArgumentCount(parsed.Arguments, 1, "baseline list");
                foreach (var baseline in await baselineStore.ListAsync())
                {
                    Console.WriteLine(baseline.Name);
                }

                return SuccessExitCode;
            case "show":
                {
                    EnsureArgumentCount(parsed.Arguments, 2, "baseline show <name>");
                    var baseline = await baselineStore.ReadAsync(parsed.Arguments[1]);
                    if (baseline is null)
                    {
                        throw new KeyNotFoundException(
                            $"Baseline not found: {parsed.Arguments[1]}");
                    }

                    Console.WriteLine(DiagnosticJson.Serialize(baseline, includeStack: true));
                    return SuccessExitCode;
                }
            default:
                throw new ArgumentException(
                    "Usage: rimerror baseline create [name] | list | show <name>");
        }
    }

    private static async Task<int> RunCompareAsync(string[] args)
    {
        var parsed = ParseStoreOption(args);
        var baselineName = DiagnosticBaselineNames.Default;
        var json = false;
        var includeAll = false;
        for (var index = 0; index < parsed.Arguments.Count; index++)
        {
            var argument = parsed.Arguments[index];
            switch (argument)
            {
                case "--baseline":
                    baselineName = ReadOptionValue(parsed.Arguments, ref index, argument);
                    break;
                case "--json":
                    json = true;
                    break;
                case "--all":
                    includeAll = true;
                    break;
                default:
                    RejectUnknownOption(argument);
                    break;
            }
        }

        var snapshot = await ReadSnapshotAsync(parsed.StorePath);
        if (snapshot is null)
        {
            throw new FileNotFoundException(
                "No latest run exists; ingest diagnostics before comparing.");
        }

        var baseline = await CreateBaselineStore(parsed.StorePath).ReadAsync(baselineName);
        if (baseline is null)
        {
            throw new KeyNotFoundException($"Baseline not found: {baselineName}");
        }

        var comparison = DiagnosticComparisonEngine.Compare(snapshot, baseline);
        var report = DiagnosticComparisonEngine.ToReport(comparison, includeAll);
        if (json)
        {
            Console.WriteLine(DiagnosticJson.Serialize(report));
        }
        else
        {
            WriteHumanComparison(report);
        }

        return comparison.Compatibility.Status == BaselineCompatibilityStatus.Incompatible
            ? OperationalFailureExitCode
            : report.Status == "fail"
                ? DiagnosticFailureExitCode
                : SuccessExitCode;
    }

    private static async Task<int> RunStatusAsync(string[] args)
    {
        var parsed = ParseStoreOption(args);
        EnsureNoArguments(parsed.Arguments, "status");
        var snapshot = await ReadSnapshotAsync(parsed.StorePath);
        var report = DiagnosticLatestReportBuilder.Build(
            snapshot,
            includeDiagnostics: false);
        Console.WriteLine(DiagnosticJson.Serialize(report));
        return ExitCodeFor(report);
    }

    private static async Task<int> RunLatestAsync(string[] args)
    {
        var parsed = ParseStoreOption(args);
        var json = false;
        var includeAll = false;
        string? runId = null;
        for (int index = 0; index < parsed.Arguments.Count; index++)
        {
            string argument = parsed.Arguments[index];
            switch (argument)
            {
                case "--json":
                    json = true;
                    break;
                case "--all":
                    includeAll = true;
                    break;
                case "--run":
                    runId = ReadOptionValue(parsed.Arguments, ref index, "--run");
                    break;
                default:
                    RejectUnknownOption(argument);
                    break;
            }
        }

        var snapshot = await ReadSnapshotAsync(parsed.StorePath);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            snapshot = DiagnosticLatestReportBuilder.FilterByRun(snapshot, runId);
        }

        var report = DiagnosticLatestReportBuilder.Build(snapshot, includeAll);
        if (json)
        {
            Console.WriteLine(DiagnosticJson.Serialize(report));
        }
        else
        {
            WriteHumanReport(report);
        }

        return ExitCodeFor(report);
    }

    private static async Task<int> RunShowAsync(string[] args)
    {
        var parsed = ParseStoreOption(args);
        EnsureArgumentCount(parsed.Arguments, 1, "show <id>");
        var snapshot = await ReadSnapshotAsync(parsed.StorePath);
        var diagnostic = snapshot?.Items.FirstOrDefault(
            item => item.Id.Equals(parsed.Arguments[0], StringComparison.Ordinal));
        if (diagnostic is null)
        {
            throw new KeyNotFoundException($"Diagnostic not found: {parsed.Arguments[0]}");
        }

        Console.WriteLine(
            DiagnosticJson.Serialize(
                DiagnosticRootCauseEngine.EnrichForShow(snapshot!, diagnostic),
                includeStack: true));
        return SuccessExitCode;
    }

    private static async Task<int> RunExportAsync(string[] args)
    {
        var parsed = ParseStoreOption(args);
        foreach (var argument in parsed.Arguments)
        {
            if (!argument.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                RejectUnknownOption(argument);
            }
        }

        var snapshot = await ReadSnapshotAsync(parsed.StorePath) ?? new DiagnosticStoreSnapshot();
        Console.WriteLine(DiagnosticJson.Serialize(snapshot, includeStack: true));
        return SuccessExitCode;
    }

    private static async Task<DiagnosticStoreSnapshot?> ReadSnapshotAsync(string? storePath) =>
        await CreateStore(storePath).ReadAsync();

    private static async Task<DiagnosticIntegrationState?> LoadIntegrationAsync(
        IReadOnlyList<(string Path, string Provider)> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        var states = new List<DiagnosticIntegrationState?>();
        foreach (var (path, provider) in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"Integration metadata not found: {fullPath}",
                    fullPath);
            }

            var length = new FileInfo(fullPath).Length;
            if (length > 1_048_576)
            {
                throw new InvalidDataException(
                    $"Integration metadata exceeds the 1 MiB safety limit: {fullPath}");
            }

            var json = await File.ReadAllTextAsync(fullPath);
            var parsed = provider switch
            {
                "devbridge" => DiagnosticIntegrationAdapter.ParseDevBridge(json),
                "rimbridge" => DiagnosticIntegrationAdapter.ParseRimBridge(json),
                _ => DiagnosticIntegrationAdapter.ParseIntegration(json)
            };
            foreach (var warning in parsed.Warnings)
            {
                Console.Error.WriteLine($"rimerror: integration: {warning}");
            }

            states.Add(parsed.State);
        }

        return DiagnosticIntegrationAdapter.Combine(states.ToArray());
    }

    private static JsonFileDiagnosticStore CreateStore(string? storePath) =>
        new(storePath ??
            Environment.GetEnvironmentVariable("RIMERROR_STATE_PATH") ??
            Path.Combine(Environment.CurrentDirectory, DefaultStorePath));

    private static JsonFileBaselineStore CreateBaselineStore(string? storePath)
    {
        var store = CreateStore(storePath);
        var directory = Path.GetDirectoryName(store.FilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The diagnostic store path has no directory.");
        }

        return new JsonFileBaselineStore(Path.Combine(directory, "baselines"));
    }

    private static ParsedArguments ParseStoreOption(string[] args)
    {
        var remaining = new List<string>();
        string? storePath = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.Equals("--store", StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(argument);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Missing value for --store.");
            }

            storePath = args[++index];
        }

        return new ParsedArguments(storePath, remaining);
    }

    private static string ReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static void EnsureNoArguments(IReadOnlyList<string> args, string command)
    {
        if (args.Count != 0)
        {
            throw new ArgumentException($"Usage: rimerror {command}");
        }
    }

    private static void EnsureArgumentCount(
        IReadOnlyList<string> args,
        int expected,
        string usage)
    {
        if (args.Count != expected)
        {
            throw new ArgumentException($"Usage: rimerror {usage}");
        }
    }

    private static void RejectUnknownOption(string argument)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown option: {argument}");
        }
    }

    private static int ExitCodeFor(DiagnosticLatestReport report) =>
        report.Errors > 0 ? DiagnosticFailureExitCode : SuccessExitCode;

    private static void WriteHumanReport(DiagnosticLatestReport report)
    {
        Console.WriteLine(
            $"{report.Status} errors={report.Errors} warnings={report.Warnings}");
        if (report.RootCauses is not null)
        {
            foreach (var root in report.RootCauses)
            {
                var context = root.Type ??
                    root.Method ??
                    root.Def ??
                    root.Member ??
                    root.Asset ??
                    root.Code ??
                    root.Category ??
                    string.Empty;
                Console.WriteLine(
                    $"root {root.Id} confidence={root.Confidence} count={root.Count} {context}".TrimEnd());
            }

        }

        if (report.Diagnostics is null)
        {
            return;
        }

        foreach (var diagnostic in report.Diagnostics)
        {
            var context = diagnostic.Type ??
                diagnostic.Method ??
                diagnostic.Def ??
                diagnostic.Member ??
                diagnostic.Asset ??
                diagnostic.Code ??
                diagnostic.Message ??
                diagnostic.Category ??
                string.Empty;
            Console.WriteLine(
                $"{diagnostic.Severity} {diagnostic.Id} count={diagnostic.Count} {context}".TrimEnd());
        }
    }

    private static void WriteHumanComparison(DiagnosticComparisonReport report)
    {
        Console.WriteLine(
            $"{report.Status} newErrors={report.NewErrors} newWarnings={report.NewWarnings}");
        if (report.Baseline is not null)
        {
            Console.WriteLine($"baseline={report.Baseline}");
        }

        if (report.Error is not null)
        {
            Console.WriteLine($"error={report.Error} reason={report.Reason}");
            return;
        }

        if (report.Resolved is not null)
        {
            Console.WriteLine($"resolved={report.Resolved}");
        }

        if (report.FrequencyChanged is not null)
        {
            Console.WriteLine($"frequencyChanged={report.FrequencyChanged}");
        }

        if (report.SeverityChanged is not null)
        {
            Console.WriteLine($"severityChanged={report.SeverityChanged}");
        }

        if (report.Diagnostics is not null)
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                Console.WriteLine(
                    $"new {diagnostic.Severity} {diagnostic.Id} count={diagnostic.Count}".TrimEnd());
            }
        }

        if (report.Changes is not null)
        {
            foreach (var change in report.Changes)
            {
                Console.WriteLine(
                    $"{change.Status} {change.Severity} {change.Id} count={change.Count}");
            }
        }
    }

    private static bool IsHelp(string[] args) =>
        args.Length == 1 && IsHelpFlag(args[0]);

    private static bool IsHelpFlag(string argument) =>
        argument.Equals("help", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static void PrintUsage()
    {
        Console.WriteLine("rimerror <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  ingest <path> [--store <path>] [--project <path>] [--run <id>] [--test <id>] [--operation <id>]");
        Console.WriteLine("  ingest --stdin [--store <path>] [--project <path>] [--devbridge <json>] [--rimbridge <json>]");
        Console.WriteLine("  status [--store <path>]");
        Console.WriteLine("  latest [--json] [--all] [--run <id>] [--store <path>]");
        Console.WriteLine("  show <id> [--store <path>]");
        Console.WriteLine("  baseline create [name] | list | show <name>");
        Console.WriteLine("  compare [--baseline <name>] [--json] [--all]");
        Console.WriteLine("  export --json [--store <path>]");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 success, 1 actionable diagnostics, 2 RimError/usage failure.");
        Console.WriteLine("Default store: .rimerror/latest.json; override with --store or RIMERROR_STATE_PATH.");
    }

    private static void PrintIngestUsage() =>
        Console.WriteLine(
            "rimerror ingest <path> [--store <path>] [--project <path>] [--index-cache <path>] [--run <id>] [--test <id>] [--operation <id>] [--operation-name <name>] [--devbridge <json>] [--rimbridge <json>] [--integration <json>]\n" +
            "rimerror ingest --stdin [--store <path>] [--project <path>] [--devbridge <json>] [--rimbridge <json>]");

    private static void PrintBaselineUsage() =>
        Console.WriteLine(
            "rimerror baseline create [name] | list | show <name> [--store <path>]");

    private sealed record ParsedArguments(string? StorePath, List<string> Arguments);
}
