using System.Text;
using RimError.Core;
using Xunit;
using Xunit.Abstractions;

namespace RimError.Core.Tests;

public sealed class PersistenceAndReportingTests
{
    private static readonly DateTimeOffset FixedIngestionTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public PersistenceAndReportingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Clean_report_is_minimal_golden_json()
    {
        var report = DiagnosticLatestReportBuilder.Build(new DiagnosticStoreSnapshot());

        Assert.Equal(
            "{\"status\":\"clean\",\"errors\":0,\"warnings\":0}",
            DiagnosticJson.Serialize(report));
    }

    [Fact]
    public void Warning_only_default_report_is_count_only_but_all_keeps_drilldown()
    {
        var snapshot = Snapshot(
            new DiagnosticRecord
            {
                Id = "d-warning",
                Severity = DiagnosticSeverity.Warning,
                Category = "startup_warning",
                Message = "Known harmless startup warning"
            });

        Assert.Equal(
            "{\"status\":\"warn\",\"errors\":0,\"warnings\":1}",
            DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot)));
        Assert.Contains(
            "\"diagnostics\":[",
            DiagnosticJson.Serialize(
                DiagnosticLatestReportBuilder.Build(snapshot, includeAll: true)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Run_filter_excludes_nearby_diagnostics_and_discards_old_causal_graph()
    {
        var snapshot = new DiagnosticStoreSnapshot
        {
            CausalAnalysis = new DiagnosticCausalAnalysis
            {
                Groups = []
            },
            Items =
            [
                new DiagnosticRecord
                {
                    Id = "d-current",
                    Severity = DiagnosticSeverity.Error,
                    Category = "runtime",
                    Message = "current run",
                    RunId = "run-current"
                },
                new DiagnosticRecord
                {
                    Id = "d-nearby",
                    Severity = DiagnosticSeverity.Error,
                    Category = "runtime",
                    Message = "nearby run",
                    RunId = "run-nearby"
                }
            ]
        };

        var filtered = DiagnosticLatestReportBuilder.FilterByRun(snapshot, "run-current");
        var report = DiagnosticLatestReportBuilder.Build(filtered);

        var diagnostic = Assert.Single(filtered.Items);
        Assert.Equal("d-current", diagnostic.Id);
        Assert.Null(filtered.CausalAnalysis);
        Assert.Contains("d-current", DiagnosticJson.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("d-nearby", DiagnosticJson.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public void One_error_report_contains_only_compact_actionable_context()
    {
        var snapshot = Snapshot(
            new DiagnosticRecord
            {
                Id = "d-error",
                Severity = DiagnosticSeverity.Error,
                Category = "runtime_null_reference",
                Message = "Object reference not set",
                ExceptionType = "System.NullReferenceException",
                OriginatingType = "CCM.CompAssembler",
                OriginatingMethod = "Tick",
                OccurrenceCount = 1
            });

        Assert.Equal(
            "{\"status\":\"fail\",\"errors\":1,\"warnings\":0," +
            "\"rootCauses\":[{\"id\":\"d-error\",\"type\":\"NullReferenceException\"," +
            "\"method\":\"CCM.CompAssembler.Tick\",\"confidence\":\"medium\",\"count\":1}]}",
            DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot)));
    }

    [Fact]
    public void Repeated_error_report_keeps_one_id_and_count()
    {
        var snapshot = Snapshot(
            new DiagnosticRecord
            {
                Id = "d-repeated",
                Severity = DiagnosticSeverity.Error,
                Category = "runtime_null_reference",
                Message = "Object reference not set",
                ExceptionType = "NullReferenceException",
                OccurrenceCount = 8_127
            });

        var json = DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot));

        Assert.Contains("\"errors\":1", json);
        Assert.Contains("\"id\":\"d-repeated\"", json);
        Assert.Contains("\"count\":8127", json);
        Assert.DoesNotContain("frames", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_errors_have_deterministic_count_then_id_order()
    {
        var snapshot = Snapshot(
            new DiagnosticRecord
            {
                Id = "d-second",
                Severity = DiagnosticSeverity.Error,
                Category = "missing_def",
                Message = "Could not find ThingDef named Steel",
                DefType = "ThingDef",
                DefName = "Steel",
                OccurrenceCount = 1
            },
            new DiagnosticRecord
            {
                Id = "d-first",
                Severity = DiagnosticSeverity.Error,
                Category = "runtime_exception",
                Message = "failure",
                ExceptionType = "System.Exception",
                OccurrenceCount = 4
            },
            new DiagnosticRecord
            {
                Id = "d-warning",
                Severity = DiagnosticSeverity.Warning,
                Category = "build_compile_warning",
                Message = "warning",
                BuildCode = "CS0168"
            });

        Assert.Equal(
            "{\"status\":\"fail\",\"errors\":2,\"warnings\":1," +
            "\"rootCauses\":[{\"id\":\"d-first\",\"type\":\"Exception\",\"confidence\":\"medium\",\"count\":4}," +
            "{\"id\":\"d-second\",\"category\":\"missing_def\",\"def\":\"ThingDef:Steel\"," +
            "\"confidence\":\"low\",\"count\":1}]}",
            DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot)));
    }

    [Fact]
    public void Unknown_generic_is_omitted_by_default_but_available_with_all()
    {
        var snapshot = Snapshot(
            new DiagnosticRecord
            {
                Id = "d-generic",
                Severity = DiagnosticSeverity.Unknown,
                Message = "Unrecognized diagnostic with no special marker"
            });

        Assert.Equal(
            "{\"status\":\"clean\",\"errors\":0,\"warnings\":0}",
            DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot)));
        Assert.Equal(
            "{\"status\":\"clean\",\"errors\":0,\"warnings\":0," +
            "\"diagnostics\":[{\"id\":\"d-generic\",\"severity\":\"unknown\"," +
            "\"message\":\"Unrecognized diagnostic with no special marker\",\"count\":1}]}",
            DiagnosticJson.Serialize(
                DiagnosticLatestReportBuilder.Build(snapshot, includeAll: true)));
    }

    [Fact]
    public void Detailed_show_json_is_a_golden_full_evidence_record()
    {
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-show",
            Severity = DiagnosticSeverity.Error,
            Category = "runtime_exception",
            Message = "failure",
            NormalizedMessage = "failure",
            RepresentativeSample = "System.Exception: failure",
            ExceptionType = "System.Exception",
            StackFrames = ["at Verse.Game.Tick()"],
            Source = "Player.log",
            OccurrenceCount = 2
        };

        Assert.Equal(
            "{\"id\":\"d-show\",\"sev\":\"Error\",\"cat\":\"runtime_exception\"," +
            "\"msg\":\"failure\",\"norm\":\"failure\",\"sample\":\"System.Exception: failure\"," +
            "\"ex\":\"System.Exception\",\"frames\":[\"at Verse.Game.Tick()\"]," +
            "\"src\":\"Player.log\",\"count\":2}",
            DiagnosticJson.Serialize(diagnostic, includeStack: true));
    }

    [Fact]
    public async Task File_store_round_trip_is_full_and_byte_stable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "latest.json");
            var store = new JsonFileDiagnosticStore(path);
            var snapshot = new DiagnosticStoreSnapshot
            {
                InputBytes = 123,
                RawOccurrenceCount = 4,
                LinesRead = 4,
                SourceCount = 1,
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "d-store",
                        Severity = DiagnosticSeverity.Error,
                        Message = "stored",
                        StackFrames = ["at Verse.Game.Tick()"]
                    }
                ]
            };

            await store.WriteAsync(snapshot);
            var firstBytes = await File.ReadAllBytesAsync(path);
            var restored = await store.ReadAsync();
            await store.WriteAsync(restored!);
            var secondBytes = await File.ReadAllBytesAsync(path);

            Assert.Equal(firstBytes, secondBytes);
            Assert.Equal(123, restored!.InputBytes);
            Assert.Equal("at Verse.Game.Tick()", Assert.Single(restored.Items).StackFrames![0]);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Typed_service_ingests_correlates_and_persists_without_cli_projection()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new JsonFileDiagnosticStore(Path.Combine(directory, "latest.json"));
            var result = await new RimErrorService().IngestAsync(
                new RimErrorIngestRequest(
                    [new DiagnosticSourceInput
                    {
                        Source = "synthetic",
                        Reader = new StringReader(
                            "System.NullReferenceException: service failure\n" +
                            "  at Test.Component.Tick()\n"),
                        Metadata = new DiagnosticIngestionMetadata { RunId = "run-core" }
                    }],
                    new DiagnosticIngestionOptions { IngestionTime = FixedIngestionTime },
                    Store: store));

            Assert.Equal("fail", result.Report.Status);
            Assert.Equal("run-core", Assert.Single(result.Snapshot.Items).RunId);

            var latest = await new RimErrorService().LatestAsync(store, "run-core");
            Assert.Equal("fail", latest.Status);
            Assert.Equal(
                Assert.Single(result.Snapshot.Items).Id,
                Assert.Single(latest.RootCauses!).Id);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Repetition_has_large_raw_to_persisted_and_latest_size_reduction()
    {
        const int repetitions = 8_127;
        var input = string.Concat(
            Enumerable.Repeat("System.InvalidOperationException: repeated failure\n", repetitions));
        var rawBytes = Encoding.UTF8.GetByteCount(input);
        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "synthetic",
            options: new DiagnosticIngestionOptions { IngestionTime = FixedIngestionTime });
        var snapshot = result.ToSnapshot();
        var latestJson = DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot));
        var directory = CreateTemporaryDirectory();

        try
        {
            var path = Path.Combine(directory, "latest.json");
            await new JsonFileDiagnosticStore(path).WriteAsync(snapshot);
            var persistedBytes = new FileInfo(path).Length;
            var latestBytes = Encoding.UTF8.GetByteCount(latestJson);

            _output.WriteLine(
                $"compression raw_bytes={rawBytes} persisted_bytes={persistedBytes} " +
                $"latest_bytes={latestBytes} raw_to_persisted={rawBytes / (double)persistedBytes:F1}x " +
                $"raw_to_latest={rawBytes / (double)latestBytes:F1}x");

            Assert.Single(result.Diagnostics);
            Assert.Equal(repetitions, result.Diagnostics[0].OccurrenceCount);
            Assert.True(persistedBytes < rawBytes / 10);
            Assert.True(latestBytes < persistedBytes);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static DiagnosticStoreSnapshot Snapshot(params DiagnosticRecord[] items) =>
        new() { Items = items };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "rimerror-stage4-" + Guid.NewGuid().ToString("N"));
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
