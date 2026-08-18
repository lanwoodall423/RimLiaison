using System.Diagnostics;
using System.Text;
using RimError.Core;
using Xunit;
using Xunit.Abstractions;

namespace RimError.Core.Tests;

public sealed class DiagnosticIngestionTests
{
    private static readonly DateTimeOffset FixedIngestionTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public DiagnosticIngestionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Timestamp_variants_deduplicate()
    {
        var input = string.Join(
            Environment.NewLine,
            "[2026-01-01 10:00:00.000Z] System.NullReferenceException: save failed",
            "[2026-01-01 10:01:00.000Z] System.NullReferenceException: save failed");

        var result = await IngestAsync(input);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(2, diagnostic.OccurrenceCount);
        Assert.Equal(2, result.RawOccurrenceCount);
    }

    [Fact]
    public async Task Transient_object_values_deduplicate()
    {
        var input = string.Join(
            Environment.NewLine,
            "[2026-01-01 11:00:00Z] ERROR Thread: 7 object Thing_12345 at 0xABCDEF request 11111111-2222-3333-4444-555555555555",
            "[2026-01-01 11:01:00Z] ERROR Thread: 19 object Thing_98765 at 0x123456 request 66666666-7777-8888-9999-aaaaaaaaaaaa");

        var result = await IngestAsync(input);

        Assert.Equal(1, result.UniqueDiagnosticCount);
        Assert.Equal(2, Assert.Single(result.Diagnostics).OccurrenceCount);
    }

    [Fact]
    public async Task Different_exception_types_do_not_collide()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.NullReferenceException: same message",
            "System.InvalidOperationException: same message");

        var result = await IngestAsync(input);

        Assert.Equal(2, result.UniqueDiagnosticCount);
    }

    [Fact]
    public async Task Different_def_names_do_not_collide()
    {
        var input = string.Join(
            Environment.NewLine,
            "Could not find ThingDef named Steel",
            "Could not find ThingDef named Wood");

        var result = await IngestAsync(input);

        Assert.Equal(2, result.UniqueDiagnosticCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.DefName == "Steel");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.DefName == "Wood");
    }

    [Fact]
    public async Task Meaningfully_different_stack_origins_do_not_collide()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.Exception: same message",
            "   at RimError.Loader.Load()",
            "System.Exception: same message",
            "   at RimError.Saver.Save()");

        var result = await IngestAsync(input);

        Assert.Equal(2, result.UniqueDiagnosticCount);
    }

    [Fact]
    public async Task Machine_specific_paths_and_line_numbers_do_not_change_identity()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.Exception: path failure",
            "   at RimError.Loader.Load() in C:\\Users\\Alice\\Mods\\Loader.cs:line 12",
            "System.Exception: path failure",
            "   at RimError.Loader.Load() in D:\\Build\\Mods\\Loader.cs:line 99");

        var result = await IngestAsync(input);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(2, diagnostic.OccurrenceCount);
    }

    [Fact]
    public async Task Fingerprint_is_stable_across_repeated_runs()
    {
        const string input = "System.Exception: stable\n   at RimError.Loader.Load()";

        var first = Assert.Single((await IngestAsync(input)).Diagnostics).Id;
        var second = Assert.Single((await IngestAsync(input)).Diagnostics).Id;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Ten_thousand_equivalent_occurrences_store_one_record()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 10_000; index++)
        {
            builder.Append("System.InvalidOperationException: repeated failure");
            builder.Append('\n');
        }

        var input = builder.ToString();
        var inputBytes = Encoding.UTF8.GetByteCount(input);
        var stopwatch = Stopwatch.StartNew();
        var result = await IngestAsync(input);
        stopwatch.Stop();
        var outputBytes = Encoding.UTF8.GetByteCount(
            DiagnosticJson.Serialize(result.ToSnapshot()));

        _output.WriteLine(
            $"benchmark input_bytes={inputBytes} raw_occurrences={result.RawOccurrenceCount} " +
            $"unique={result.UniqueDiagnosticCount} output_bytes={outputBytes} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F2}");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(10_000, diagnostic.OccurrenceCount);
        Assert.Equal(10_000, result.RawOccurrenceCount);
    }

    [Fact]
    public async Task Adjacent_multiline_exceptions_remain_separate()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.Exception: first",
            "   at RimError.First.Run()",
            "[Ref 1001]",
            "System.InvalidOperationException: second",
            "   at RimError.Second.Run()");

        var result = await IngestAsync(input);

        Assert.Equal(2, result.UniqueDiagnosticCount);
        Assert.All(result.Diagnostics, diagnostic => Assert.Single(diagnostic.StackFrames!));
    }

    [Fact]
    public async Task Malformed_and_pathological_input_does_not_crash_ingestion()
    {
        var input = "System.Exception: " + new string('x', 1_000) + "\0\nSystem.Exception: valid";
        var options = TestOptions with { MaxLineLength = 64 };

        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "malformed",
            options: options);

        Assert.True(result.MalformedLineCount > 0);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task Multiple_sources_and_correlation_metadata_are_supported()
    {
        var result = await new DiagnosticIngestor().IngestAsync(
            [
                new DiagnosticSourceInput
                {
                    Source = "player-a.log",
                    Reader = new StringReader("System.Exception: shared")
                },
                new DiagnosticSourceInput
                {
                    Source = "player-b.log",
                    Reader = new StringReader("System.Exception: shared"),
                    Metadata = new DiagnosticIngestionMetadata
                    {
                        RunId = "run-42",
                        OperationId = "test-7",
                        SourceAttribution = "synthetic"
                    }
                }
            ],
            TestOptions);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(2, diagnostic.OccurrenceCount);
        Assert.Equal("run-42", diagnostic.RunId);
        Assert.Equal("test-7", diagnostic.OperationId);
        Assert.Equal(2, result.SourceCount);
    }

    [Fact]
    public async Task Unique_diagnostic_limit_bounds_retained_memory()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.Exception: one",
            "System.Exception: two");

        var result = await IngestAsync(input, TestOptions with { MaxUniqueDiagnostics = 1 });

        Assert.Single(result.Diagnostics);
        Assert.Equal(1, result.DroppedDiagnosticCount);
    }

    private static Task<DiagnosticIngestionResult> IngestAsync(string input) =>
        IngestAsync(input, TestOptions);

    private static Task<DiagnosticIngestionResult> IngestAsync(
        string input,
        DiagnosticIngestionOptions options) =>
        new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "synthetic",
            options: options).AsTask();

    private static DiagnosticIngestionOptions TestOptions => new()
    {
        IngestionTime = FixedIngestionTime
    };
}
