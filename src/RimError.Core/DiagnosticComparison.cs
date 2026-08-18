using System.Text.Json.Serialization;

namespace RimError.Core;

public enum DiagnosticComparisonKind
{
    New,
    Existing,
    Resolved,
    FrequencyChanged,
    SeverityChanged
}

public enum BaselineCompatibilityStatus
{
    Compatible,
    Uncertain,
    Incompatible
}

public sealed record BaselineCompatibility
{
    public required BaselineCompatibilityStatus Status { get; init; }

    public string? Reason { get; init; }
}

public sealed record DiagnosticComparisonChange
{
    public required string Id { get; init; }

    public required DiagnosticComparisonKind Kind { get; init; }

    public DiagnosticRecord? Current { get; init; }

    public DiagnosticRecord? Baseline { get; init; }
}

public sealed record DiagnosticComparisonResult
{
    public required string BaselineName { get; init; }

    public required BaselineCompatibility Compatibility { get; init; }

    public DiagnosticComparisonChange[] Changes { get; init; } = [];
}

public sealed record DiagnosticComparisonReport
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("newErrors")]
    public int NewErrors { get; init; }

    [JsonPropertyName("newWarnings")]
    public int NewWarnings { get; init; }

    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticSummary[]? Diagnostics { get; init; }

    [JsonPropertyName("resolved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Resolved { get; init; }

    [JsonPropertyName("frequencyChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FrequencyChanged { get; init; }

    [JsonPropertyName("severityChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SeverityChanged { get; init; }

    [JsonPropertyName("baseline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Baseline { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticChangeSummary[]? Changes { get; init; }
}

public sealed record DiagnosticChangeSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

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

    [JsonPropertyName("count")]
    public long Count { get; init; }
}

public static class DiagnosticComparisonEngine
{
    public static DiagnosticComparisonResult Compare(
        DiagnosticStoreSnapshot current,
        DiagnosticBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        var compatibility = CheckCompatibility(current, baseline);
        if (compatibility.Status == BaselineCompatibilityStatus.Incompatible)
        {
            return new DiagnosticComparisonResult
            {
                BaselineName = baseline.Name,
                Compatibility = compatibility
            };
        }

        var currentById = ToUniqueMap(current.Items, "current run");
        var baselineById = ToUniqueMap(baseline.Items, "baseline");
        var changes = new List<DiagnosticComparisonChange>(
            currentById.Count + baselineById.Count);

        foreach (var diagnostic in currentById.Values.OrderBy(
                     item => item.Id,
                     StringComparer.Ordinal))
        {
            if (!baselineById.TryGetValue(diagnostic.Id, out var prior))
            {
                changes.Add(new DiagnosticComparisonChange
                {
                    Id = diagnostic.Id,
                    Kind = DiagnosticComparisonKind.New,
                    Current = diagnostic
                });
                continue;
            }

            changes.Add(new DiagnosticComparisonChange
            {
                Id = diagnostic.Id,
                Kind = DiagnosticComparisonKind.Existing,
                Current = diagnostic,
                Baseline = prior
            });

            if (MeaningfulFrequencyChange(prior.OccurrenceCount, diagnostic.OccurrenceCount))
            {
                changes.Add(new DiagnosticComparisonChange
                {
                    Id = diagnostic.Id,
                    Kind = DiagnosticComparisonKind.FrequencyChanged,
                    Current = diagnostic,
                    Baseline = prior
                });
            }

            if (prior.Severity != diagnostic.Severity)
            {
                changes.Add(new DiagnosticComparisonChange
                {
                    Id = diagnostic.Id,
                    Kind = DiagnosticComparisonKind.SeverityChanged,
                    Current = diagnostic,
                    Baseline = prior
                });
            }
        }

        foreach (var diagnostic in baselineById.Values.OrderBy(
                     item => item.Id,
                     StringComparer.Ordinal))
        {
            if (currentById.ContainsKey(diagnostic.Id))
            {
                continue;
            }

            changes.Add(new DiagnosticComparisonChange
            {
                Id = diagnostic.Id,
                Kind = DiagnosticComparisonKind.Resolved,
                Baseline = diagnostic
            });
        }

        return new DiagnosticComparisonResult
        {
            BaselineName = baseline.Name,
            Compatibility = compatibility,
            Changes = changes.ToArray()
        };
    }

    public static DiagnosticComparisonReport ToReport(
        DiagnosticComparisonResult result,
        bool includeAll = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Compatibility.Status == BaselineCompatibilityStatus.Incompatible)
        {
            return new DiagnosticComparisonReport
            {
                Status = "incompatible",
                NewErrors = 0,
                NewWarnings = 0,
                Error = "baseline_incompatible",
                Reason = result.Compatibility.Reason
            };
        }

        var newDiagnostics = result.Changes
            .Where(change =>
                change.Kind == DiagnosticComparisonKind.New &&
                change.Current is not null)
            .Select(change => change.Current!)
            .Where(diagnostic =>
                DiagnosticLatestReportBuilder.IsActionableError(diagnostic) ||
                DiagnosticLatestReportBuilder.IsWarning(diagnostic))
            .ToArray();
        var newErrors = newDiagnostics.Count(DiagnosticLatestReportBuilder.IsActionableError);
        var newWarnings = newDiagnostics.Count(DiagnosticLatestReportBuilder.IsWarning);
        var severityChangedErrors = result.Changes.Count(change =>
            change.Kind == DiagnosticComparisonKind.SeverityChanged &&
            change.Current is not null &&
            DiagnosticLatestReportBuilder.IsActionableError(change.Current));
        var status = newErrors > 0 || severityChangedErrors > 0
            ? "fail"
            : newWarnings > 0
                ? "warn"
                : "clean";
        var orderedNew = DiagnosticLatestReportBuilder.OrderForReport(newDiagnostics)
            .Select(DiagnosticLatestReportBuilder.Summarize)
            .ToArray();
        var resolved = result.Changes.Count(
            change => change.Kind == DiagnosticComparisonKind.Resolved);
        var frequencyChanged = result.Changes.Count(
            change => change.Kind == DiagnosticComparisonKind.FrequencyChanged);
        var severityChanged = result.Changes.Count(
            change => change.Kind == DiagnosticComparisonKind.SeverityChanged);

        return new DiagnosticComparisonReport
        {
            Status = status,
            NewErrors = newErrors,
            NewWarnings = newWarnings,
            Diagnostics = orderedNew.Length == 0 ? null : orderedNew,
            Resolved = resolved == 0 ? null : resolved,
            FrequencyChanged = frequencyChanged == 0 ? null : frequencyChanged,
            SeverityChanged = severityChanged == 0 ? null : severityChanged,
            Baseline = result.Compatibility.Status == BaselineCompatibilityStatus.Uncertain
                ? "uncertain"
                : null,
            Changes = includeAll
                ? result.Changes
                    .OrderBy(ChangeOrder)
                    .ThenBy(change => change.Id, StringComparer.Ordinal)
                    .Select(ToChangeSummary)
                    .ToArray()
                : null
        };
    }

    private static BaselineCompatibility CheckCompatibility(
        DiagnosticStoreSnapshot current,
        DiagnosticBaseline baseline)
    {
        var incompatibleReasons = new List<string>();
        var uncertainReasons = new List<string>();
        if (baseline.StoreSchemaVersion is null ||
            baseline.StoreSchemaVersion != current.SchemaVersion)
        {
            incompatibleReasons.Add("store schema differs");
        }

        if (baseline.FingerprintSchemaVersion is null ||
            current.FingerprintSchemaVersion is null ||
            baseline.FingerprintSchemaVersion != current.FingerprintSchemaVersion)
        {
            incompatibleReasons.Add("fingerprint schema differs");
        }

        CompareEnvironment(
            baseline.RimWorldVersion,
            current.RimWorldVersion,
            "RimWorld version",
            incompatibleReasons,
            uncertainReasons,
            RimWorldVersionSeriesDiffers);
        CompareEnvironment(
            baseline.ModProfile,
            current.ModProfile,
            "mod profile",
            incompatibleReasons,
            uncertainReasons,
            (left, right) => !left.Equals(right, StringComparison.Ordinal));

        if (incompatibleReasons.Count > 0)
        {
            return new BaselineCompatibility
            {
                Status = BaselineCompatibilityStatus.Incompatible,
                Reason = string.Join("; ", incompatibleReasons)
            };
        }

        return new BaselineCompatibility
        {
            Status = uncertainReasons.Count == 0
                ? BaselineCompatibilityStatus.Compatible
                : BaselineCompatibilityStatus.Uncertain,
            Reason = uncertainReasons.Count == 0
                ? null
                : string.Join("; ", uncertainReasons)
        };
    }

    private static void CompareEnvironment(
        string? baselineValue,
        string? currentValue,
        string label,
        ICollection<string> incompatibleReasons,
        ICollection<string> uncertainReasons,
        Func<string, string, bool> differs)
    {
        if (baselineValue is not null && currentValue is not null)
        {
            if (differs(baselineValue, currentValue))
            {
                incompatibleReasons.Add($"{label} differs");
            }

            return;
        }

        if (baselineValue is not null || currentValue is not null)
        {
            uncertainReasons.Add($"{label} metadata missing");
        }
    }

    private static bool RimWorldVersionSeriesDiffers(string left, string right)
    {
        var leftSeries = VersionSeries(left);
        var rightSeries = VersionSeries(right);
        return leftSeries is null || rightSeries is null
            ? !left.Equals(right, StringComparison.Ordinal)
            : !leftSeries.Equals(rightSeries, StringComparison.Ordinal);
    }

    private static string? VersionSeries(string value)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        return $"{major}.{minor}";
    }

    private static Dictionary<string, DiagnosticRecord> ToUniqueMap(
        DiagnosticRecord[] records,
        string label)
    {
        var map = new Dictionary<string, DiagnosticRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!map.TryAdd(record.Id, record))
            {
                throw new InvalidDataException($"Duplicate diagnostic ID in {label}: {record.Id}");
            }
        }

        return map;
    }

    private static bool MeaningfulFrequencyChange(long previous, long current)
    {
        if (previous == current)
        {
            return false;
        }

        var low = Math.Min(previous, current);
        var high = Math.Max(previous, current);
        var difference = high - low;
        return low == 0 || difference >= 3 || difference >= low;
    }

    private static int ChangeOrder(DiagnosticComparisonChange change) =>
        change.Kind switch
        {
            DiagnosticComparisonKind.New => 0,
            DiagnosticComparisonKind.SeverityChanged => 1,
            DiagnosticComparisonKind.FrequencyChanged => 2,
            DiagnosticComparisonKind.Resolved => 3,
            _ => 4
        };

    private static DiagnosticChangeSummary ToChangeSummary(
        DiagnosticComparisonChange change)
    {
        var record = change.Current ?? change.Baseline!;
        var summary = DiagnosticLatestReportBuilder.Summarize(record);
        return new DiagnosticChangeSummary
        {
            Id = summary.Id,
            Status = change.Kind switch
            {
                DiagnosticComparisonKind.FrequencyChanged => "frequency-changed",
                DiagnosticComparisonKind.SeverityChanged => "severity-changed",
                _ => change.Kind.ToString().ToLowerInvariant()
            },
            Severity = summary.Severity,
            Category = summary.Category,
            Type = summary.Type,
            Method = summary.Method,
            Count = summary.Count
        };
    }
}
