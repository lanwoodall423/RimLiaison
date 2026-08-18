using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class BaselineComparisonTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Same_diagnostics_reordered_between_runs_are_existing()
    {
        var baselineSnapshot = await IngestSnapshotAsync(
            "System.Exception: first\nSystem.Exception: second",
            FixedTime);
        var currentSnapshot = await IngestSnapshotAsync(
            "System.Exception: second\nSystem.Exception: first",
            FixedTime.AddMinutes(1));
        var result = Compare(currentSnapshot, baselineSnapshot);

        Assert.Equal(BaselineCompatibilityStatus.Compatible, result.Compatibility.Status);
        Assert.Equal(2, result.Changes.Count(change =>
            change.Kind == DiagnosticComparisonKind.Existing));
        Assert.DoesNotContain(result.Changes, change =>
            change.Kind is DiagnosticComparisonKind.New or DiagnosticComparisonKind.Resolved);
    }

    [Fact]
    public async Task Timestamp_changes_remain_existing()
    {
        var baselineSnapshot = await IngestSnapshotAsync(
            "[2026-01-01 10:00:00Z] System.Exception: stable",
            FixedTime);
        var currentSnapshot = await IngestSnapshotAsync(
            "[2026-02-01 10:00:00Z] System.Exception: stable",
            FixedTime.AddDays(1));
        var result = Compare(currentSnapshot, baselineSnapshot);

        Assert.DoesNotContain(result.Changes, change =>
            change.Kind is DiagnosticComparisonKind.New or DiagnosticComparisonKind.Resolved);
    }

    [Fact]
    public async Task Transient_object_ids_remain_existing()
    {
        var baselineSnapshot = await IngestSnapshotAsync(
            "ERROR Thread: 7 object Thing_12345 at 0xABCDEF request 11111111-2222-3333-4444-555555555555",
            FixedTime);
        var currentSnapshot = await IngestSnapshotAsync(
            "ERROR Thread: 19 object Thing_98765 at 0x123456 request 66666666-7777-8888-9999-aaaaaaaaaaaa",
            FixedTime.AddMinutes(1));
        var result = Compare(currentSnapshot, baselineSnapshot);

        Assert.DoesNotContain(result.Changes, change =>
            change.Kind is DiagnosticComparisonKind.New or DiagnosticComparisonKind.Resolved);
    }

    [Fact]
    public void One_genuinely_new_diagnostic_is_new_and_actionable()
    {
        var baseline = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));
        var current = Snapshot(
            Record("d-known", DiagnosticSeverity.Error, count: 1),
            Record(
                "d-new",
                DiagnosticSeverity.Error,
                category: "runtime_null_reference",
                exceptionType: "System.NullReferenceException",
                count: 8_127));
        var result = Compare(current, baseline);
        var report = DiagnosticComparisonEngine.ToReport(result);

        Assert.Contains(result.Changes, change =>
            change.Id == "d-new" && change.Kind == DiagnosticComparisonKind.New);
        Assert.Equal(1, report.NewErrors);
        Assert.Equal("fail", report.Status);
        var diagnostic = Assert.Single(report.Diagnostics!);
        Assert.Equal("d-new", diagnostic.Id);
        Assert.Equal(8_127, diagnostic.Count);
    }

    [Fact]
    public void One_resolved_diagnostic_is_compactly_summarized()
    {
        var baseline = Snapshot(
            Record("d-resolved", DiagnosticSeverity.Error, count: 3));
        var current = Snapshot();
        var result = Compare(current, baseline);
        var report = DiagnosticComparisonEngine.ToReport(result);

        Assert.Contains(result.Changes, change =>
            change.Id == "d-resolved" && change.Kind == DiagnosticComparisonKind.Resolved);
        Assert.Equal("clean", report.Status);
        Assert.Equal(1, report.Resolved);
        Assert.Null(report.Diagnostics);
    }

    [Fact]
    public void Meaningful_frequency_change_is_classified_without_becoming_new()
    {
        var baseline = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 2));
        var current = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 5));
        var result = Compare(current, baseline);
        var report = DiagnosticComparisonEngine.ToReport(result);

        Assert.Contains(result.Changes, change =>
            change.Kind == DiagnosticComparisonKind.FrequencyChanged);
        Assert.DoesNotContain(result.Changes, change =>
            change.Kind == DiagnosticComparisonKind.New);
        Assert.Equal(1, report.FrequencyChanged);
        Assert.Equal("clean", report.Status);
    }

    [Fact]
    public void Severity_change_is_classified_and_error_escalation_is_not_silent()
    {
        var baseline = Snapshot(Record("d-known", DiagnosticSeverity.Warning, count: 1));
        var current = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));
        var result = Compare(current, baseline);
        var report = DiagnosticComparisonEngine.ToReport(result);

        Assert.Contains(result.Changes, change =>
            change.Kind == DiagnosticComparisonKind.SeverityChanged);
        Assert.Equal(1, report.SeverityChanged);
        Assert.Equal("fail", report.Status);
    }

    [Theory]
    [InlineData(2, 1, "store schema differs")]
    [InlineData(1, 2, "fingerprint schema differs")]
    public void Incompatible_baseline_schema_is_not_trusted(
        int storeSchemaVersion,
        int fingerprintSchemaVersion,
        string expectedReason)
    {
        var current = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));
        var baseline = new DiagnosticBaseline
        {
            Name = "default",
            CreatedAt = FixedTime,
            StoreSchemaVersion = storeSchemaVersion,
            FingerprintSchemaVersion = fingerprintSchemaVersion,
            Items = current.Items
        };
        var report = DiagnosticComparisonEngine.ToReport(
            DiagnosticComparisonEngine.Compare(current, baseline));

        Assert.Equal("incompatible", report.Status);
        Assert.Equal("baseline_incompatible", report.Error);
        Assert.Contains(expectedReason, report.Reason);
    }

    [Fact]
    public void Empty_baseline_is_compatible_and_current_diagnostics_are_new()
    {
        var baseline = Snapshot();
        var current = Snapshot(Record("d-new", DiagnosticSeverity.Error, count: 1));
        var report = DiagnosticComparisonEngine.ToReport(Compare(current, baseline));

        Assert.Equal(1, report.NewErrors);
        Assert.Equal("fail", report.Status);
    }

    [Fact]
    public async Task Named_and_default_baselines_are_listed_deterministically()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new JsonFileBaselineStore(directory);
            var snapshot = Snapshot(Record("d-one", DiagnosticSeverity.Error, count: 1));
            await store.WriteAsync(DiagnosticBaseline.FromSnapshot(
                DiagnosticBaselineNames.Default,
                snapshot,
                FixedTime));
            await store.WriteAsync(DiagnosticBaseline.FromSnapshot(
                "release-1",
                snapshot,
                FixedTime));

            var names = (await store.ListAsync()).Select(baseline => baseline.Name).ToArray();

            Assert.Equal(["default", "release-1"], names);
            Assert.NotNull(await store.ReadAsync("release-1"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void Clean_comparison_json_is_token_minimal()
    {
        var baseline = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));
        var current = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));
        var report = DiagnosticComparisonEngine.ToReport(Compare(current, baseline));

        Assert.Equal(
            "{\"status\":\"clean\",\"newErrors\":0,\"newWarnings\":0}",
            DiagnosticJson.Serialize(report));
    }

    [Fact]
    public async Task Missing_environment_metadata_is_marked_uncertain_not_compatible()
    {
        var baseline = Snapshot(
            [Record("d-known", DiagnosticSeverity.Error, count: 1)],
            rimWorldVersion: "1.5.4104",
            modProfile: "profile-a");
        var current = Snapshot(Record("d-known", DiagnosticSeverity.Error, count: 1));

        var result = Compare(current, baseline);

        Assert.Equal(BaselineCompatibilityStatus.Uncertain, result.Compatibility.Status);
        Assert.Contains("metadata missing", result.Compatibility.Reason);
    }

    [Fact]
    public void RimWorld_major_series_change_is_incompatible()
    {
        var baseline = Snapshot(
            [Record("d-known", DiagnosticSeverity.Error, count: 1)],
            rimWorldVersion: "1.5.4104");
        var current = Snapshot(
            [Record("d-known", DiagnosticSeverity.Error, count: 1)],
            rimWorldVersion: "1.6.1000");

        var result = Compare(current, baseline);

        Assert.Equal(BaselineCompatibilityStatus.Incompatible, result.Compatibility.Status);
        Assert.Contains("RimWorld version differs", result.Compatibility.Reason);
    }

    [Fact]
    public void Different_mod_profile_is_incompatible()
    {
        var baseline = Snapshot(
            [Record("d-known", DiagnosticSeverity.Error, count: 1)],
            modProfile: "profile-a");
        var current = Snapshot(
            [Record("d-known", DiagnosticSeverity.Error, count: 1)],
            modProfile: "profile-b");

        var result = Compare(current, baseline);

        Assert.Equal(BaselineCompatibilityStatus.Incompatible, result.Compatibility.Status);
        Assert.Contains("mod profile differs", result.Compatibility.Reason);
    }

    private static DiagnosticComparisonResult Compare(
        DiagnosticStoreSnapshot current,
        DiagnosticStoreSnapshot baselineSnapshot)
    {
        var baseline = DiagnosticBaseline.FromSnapshot(
            "default",
            baselineSnapshot,
            FixedTime);
        return DiagnosticComparisonEngine.Compare(current, baseline);
    }

    private static async Task<DiagnosticStoreSnapshot> IngestSnapshotAsync(
        string input,
        DateTimeOffset ingestionTime)
    {
        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "synthetic",
            options: new DiagnosticIngestionOptions { IngestionTime = ingestionTime });
        return result.ToSnapshot();
    }

    private static DiagnosticStoreSnapshot Snapshot(
        params DiagnosticRecord[] items) =>
        new()
        {
            FingerprintSchemaVersion = DiagnosticFingerprint.CurrentSchemaVersion,
            Items = items
        };

    private static DiagnosticStoreSnapshot Snapshot(
        DiagnosticRecord[] items,
        string? rimWorldVersion = null,
        string? modProfile = null) =>
        new()
        {
            FingerprintSchemaVersion = DiagnosticFingerprint.CurrentSchemaVersion,
            RimWorldVersion = rimWorldVersion,
            ModProfile = modProfile,
            Items = items
        };

    private static DiagnosticRecord Record(
        string id,
        DiagnosticSeverity severity,
        string category = "runtime_exception",
        string? exceptionType = "System.Exception",
        long count = 1) =>
        new()
        {
            Id = id,
            Severity = severity,
            Category = category,
            Message = "failure",
            ExceptionType = exceptionType,
            OccurrenceCount = count
        };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "rimerror-stage5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
