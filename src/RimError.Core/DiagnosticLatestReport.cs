using System.Text.Json.Serialization;

namespace RimError.Core;

public sealed record DiagnosticLatestReport
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    [JsonPropertyName("warnings")]
    public int Warnings { get; init; }

    [JsonPropertyName("rootCauses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticRootCauseSummary[]? RootCauses { get; init; }

    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticSummary[]? Diagnostics { get; init; }
}

public sealed record DiagnosticSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonPropertyName("def")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Def { get; init; }

    [JsonPropertyName("member")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Member { get; init; }

    [JsonPropertyName("asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; init; }

    [JsonPropertyName("package")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Package { get; init; }

    [JsonPropertyName("dependency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dependency { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Confidence { get; init; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    [JsonPropertyName("test")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Test { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }
}

public static class DiagnosticLatestReportBuilder
{
    private const int DefaultDiagnosticLimit = 20;
    private const int MaxSummaryMessageLength = 240;

    private static readonly HashSet<string> GenericCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trace",
        "Debug",
        "Info",
        "Warning",
        "Error",
        "Fatal",
        "Exception",
        "StackTrace",
        "Partial"
    };

    /// <summary>
    /// Returns a report input scoped to one completed run. The causal graph is
    /// discarded because it may have been computed from records belonging to
    /// another run in the persistent store.
    /// </summary>
    public static DiagnosticStoreSnapshot FilterByRun(
        DiagnosticStoreSnapshot? snapshot,
        string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return (snapshot ?? new DiagnosticStoreSnapshot()) with
        {
            Items = (snapshot?.Items ?? [])
                .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                .ToArray(),
            CausalAnalysis = null
        };
    }

    public static DiagnosticLatestReport Build(
        DiagnosticStoreSnapshot? snapshot,
        bool includeAll = false,
        bool includeDiagnostics = true)
    {
        var items = snapshot?.Items ?? [];
        var errors = items.Count(IsActionableError);
        var warnings = items.Count(IsWarning);
        var status = errors > 0 ? "fail" : warnings > 0 ? "warn" : "clean";

        DiagnosticRootCauseSummary[]? rootCauses = null;
        DiagnosticSummary[]? diagnostics = null;
        if (includeDiagnostics)
        {
            if (errors > 0)
            {
                var analysis = snapshot?.CausalAnalysis ??
                    DiagnosticRootCauseEngine.Analyze(items);
                var rootRecords = DiagnosticRootCauseEngine
                    .OrderRootCauses(items, analysis)
                    .Where(IsActionableError)
                    .ToArray();
                if (rootRecords.Length == 0)
                {
                    rootRecords = OrderForReport(items.Where(IsActionableError)).ToArray();
                }

                rootCauses = rootRecords
                    .Select(record =>
                    {
                        var group = analysis.Groups.FirstOrDefault(candidate =>
                            candidate.RootId.Equals(record.Id, StringComparison.Ordinal)) ??
                            new DiagnosticRootCauseGroup
                            {
                                RootId = record.Id,
                                Confidence = record.ExceptionType is null ? "low" : "medium",
                                Signals = ["independent"]
                            };
                        return DiagnosticRootCauseEngine.Summarize(record, group);
                    })
                    .ToArray();
                if (rootCauses.Length == 0)
                {
                    rootCauses = null;
                }
            }

            var selected = includeAll || rootCauses is null
                ? includeAll
                    ? OrderForReport(items)
                    : SelectDefault(items, errors)
                : [];
            diagnostics = selected
                .Take(includeAll ? int.MaxValue : DefaultDiagnosticLimit)
                .Select(Summarize)
                .ToArray();
            if (diagnostics.Length == 0)
            {
                diagnostics = null;
            }
        }

        return new DiagnosticLatestReport
        {
            Status = status,
            Errors = errors,
            Warnings = warnings,
            RootCauses = rootCauses,
            Diagnostics = diagnostics
        };
    }

    private static IEnumerable<DiagnosticRecord> SelectDefault(
        DiagnosticRecord[] items,
        int errorCount)
    {
        // Warning-only runs are common startup noise. Keep the default query
        // count-only; latest --all remains the explicit warning drill-down.
        return errorCount > 0
            ? OrderForReport(items.Where(IsActionableError))
            : [];
    }

    public static IOrderedEnumerable<DiagnosticRecord> OrderForReport(
        IEnumerable<DiagnosticRecord> items) =>
        items
            .OrderByDescending(DisplaySeverity)
            .ThenByDescending(diagnostic => diagnostic.OccurrenceCount)
            .ThenBy(diagnostic => diagnostic.Category ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal);

    public static DiagnosticSummary Summarize(DiagnosticRecord diagnostic)
    {
        var type = ShortName(diagnostic.ExceptionType);
        var method = ComposeMethod(diagnostic);
        var def = ComposeDef(diagnostic);
        var hasStructuredContext = type is not null ||
            method is not null ||
            def is not null ||
            diagnostic.MissingMember is not null ||
            diagnostic.Asset is not null ||
            diagnostic.PackageId is not null ||
            diagnostic.Dependency is not null ||
            diagnostic.BuildCode is not null;
        var message = hasStructuredContext
            ? null
            : Trim(DiagnosticNormalizer.NormalizeMessage(diagnostic.Message), MaxSummaryMessageLength);

        return new DiagnosticSummary
        {
            Id = diagnostic.Id,
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Category = diagnostic.Category,
            Type = type,
            Method = method,
            Def = def,
            Member = diagnostic.MissingMember,
            Asset = diagnostic.Asset,
            Package = diagnostic.PackageId,
            Dependency = diagnostic.Dependency,
            Code = diagnostic.BuildCode,
            Message = string.IsNullOrWhiteSpace(message) ? null : message,
            Source = ChooseSource(diagnostic),
            Line = ChooseLine(diagnostic),
            Symbol = diagnostic.SourceSymbol,
            Confidence = diagnostic.AttributionConfidence,
            Operation = MaterialCorrelation(diagnostic)
                ? diagnostic.OperationName ?? diagnostic.OperationId
                : null,
            Test = MaterialCorrelation(diagnostic) ? diagnostic.TestId : null,
            Count = diagnostic.OccurrenceCount
        };
    }

    public static bool IsActionableError(DiagnosticRecord diagnostic) =>
        !IsWarning(diagnostic) &&
        (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Fatal ||
         diagnostic.Severity == DiagnosticSeverity.Unknown &&
         diagnostic.Category is not null &&
         !GenericCategories.Contains(diagnostic.Category));

    public static bool IsWarning(DiagnosticRecord diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Warning ||
        diagnostic.Category?.Contains("warning", StringComparison.OrdinalIgnoreCase) == true;

    private static int DisplaySeverity(DiagnosticRecord diagnostic) =>
        IsActionableError(diagnostic)
            ? 3
            : IsWarning(diagnostic)
                ? 2
                : diagnostic.Severity == DiagnosticSeverity.Info ? 1 : 0;

    private static string? ComposeMethod(DiagnosticRecord diagnostic)
    {
        var type = diagnostic.OriginatingType ?? diagnostic.TargetType;
        var method = diagnostic.OriginatingMethod ?? diagnostic.TargetMethod;
        if (type is null)
        {
            return method;
        }

        return method is null ? type : $"{type}.{method}";
    }

    private static string? ComposeDef(DiagnosticRecord diagnostic)
    {
        if (diagnostic.DefType is null)
        {
            return diagnostic.DefName;
        }

        return diagnostic.DefName is null
            ? diagnostic.DefType
            : $"{diagnostic.DefType}:{diagnostic.DefName}";
    }

    private static string? ShortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lastSeparator = value.LastIndexOf('.');
        return lastSeparator >= 0 ? value[(lastSeparator + 1)..] : value;
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? ChooseSource(DiagnosticRecord diagnostic) =>
        diagnostic.SourceFile ??
        diagnostic.DefSourceFile ??
        diagnostic.DefReferenceFiles?.FirstOrDefault();

    private static int? ChooseLine(DiagnosticRecord diagnostic) =>
        diagnostic.SourceFile is not null
            ? diagnostic.SourceLine
            : diagnostic.DefSourceFile is not null
                ? diagnostic.DefSourceLine
                : null;

    private static bool MaterialCorrelation(DiagnosticRecord diagnostic) =>
        diagnostic.CorrelationConfidence is "high" or "medium" &&
        (!string.IsNullOrWhiteSpace(diagnostic.OperationName) ||
         !string.IsNullOrWhiteSpace(diagnostic.OperationId));
}
