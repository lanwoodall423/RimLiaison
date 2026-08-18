using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RimError.Core;
using Xunit;
using Xunit.Abstractions;

namespace RimError.Core.Tests;

public sealed class ProductionReadinessAuditTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public ProductionReadinessAuditTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Stage9_fixture_matrix_is_bounded_and_measured()
    {
        var metrics = new List<AuditMetric>();

        var clean = await IngestFixtureAsync("clean-startup.log");
        var cleanSnapshot = Prepare(clean);
        var cleanBaseline = DiagnosticBaseline.FromSnapshot(
            DiagnosticBaselineNames.Default,
            cleanSnapshot,
            FixedTime);
        var cleanReport = DiagnosticComparisonEngine.ToReport(
            DiagnosticComparisonEngine.Compare(cleanSnapshot, cleanBaseline));
        Assert.Equal("clean", cleanReport.Status);
        Assert.Equal(0, cleanReport.NewErrors);
        Assert.Equal(0, cleanReport.NewWarnings);
        Assert.Equal(
            "{\"status\":\"clean\",\"newErrors\":0,\"newWarnings\":0}",
            DiagnosticJson.Serialize(cleanReport));
        metrics.Add(CompareMetric("clean-startup", clean, cleanReport, cleanSnapshot));

        var missingDef = await IngestFixtureAsync("new-missing-def.log");
        var missingDefSnapshot = Prepare(missingDef);
        var missingDefRecord = Assert.Single(missingDef.Diagnostics);
        Assert.Equal("missing_def", missingDefRecord.Category);
        Assert.Equal("ThingDef", missingDefRecord.DefType);
        Assert.Equal("CCM_FancyWorkbench", missingDefRecord.DefName);
        metrics.Add(LatestMetric("new-missing-def", missingDef, missingDefSnapshot));

        var repeated = await IngestRepeatedRuntimeAsync(10_000);
        var repeatedSnapshot = Prepare(repeated);
        Assert.Equal(10_000, repeated.RawOccurrenceCount);
        Assert.Single(repeated.Diagnostics);
        Assert.Equal(10_000, repeated.Diagnostics[0].OccurrenceCount);
        metrics.Add(LatestMetric("repeated-runtime", repeated, repeatedSnapshot));

        var initialization = await IngestExpandedInitializationAsync(1_000);
        var initializationSnapshot = Prepare(initialization);
        Assert.True(initialization.RawOccurrenceCount > 2_000);
        Assert.Equal(1, RootCount(initializationSnapshot));
        metrics.Add(LatestMetric(
            "initialization-downstream",
            initialization,
            initializationSnapshot));

        var harmony = await IngestFixtureAsync("harmony-missing-target.log");
        var harmonySnapshot = Prepare(harmony);
        Assert.Equal("harmony_target", Assert.Single(harmony.Diagnostics).Category);
        metrics.Add(LatestMetric("harmony-missing-target", harmony, harmonySnapshot));

        var compiler = await IngestFixtureAsync("compiler-build.log");
        var compilerSnapshot = Prepare(compiler);
        Assert.Contains(compiler.Diagnostics, diagnostic => diagnostic.BuildCode == "CS0246");
        Assert.Contains(compiler.Diagnostics, diagnostic => diagnostic.BuildCode == "MSB3270");
        metrics.Add(LatestMetric("compiler-build", compiler, compilerSnapshot));

        var asset = await IngestFixtureAsync("missing-asset.log");
        var assetSnapshot = Prepare(asset);
        Assert.Equal("missing_texture", Assert.Single(asset.Diagnostics).Category);
        metrics.Add(LatestMetric("missing-asset", asset, assetSnapshot));

        var integration = await IngestOperationFixtureAsync();
        var integrationSnapshot = Prepare(integration);
        var integrated = Assert.Single(integrationSnapshot.Items);
        Assert.Equal("high", integrated.CorrelationConfidence);
        Assert.Equal("mymod/create_assembler", integrated.OperationName);
        metrics.Add(LatestMetric("operation-correlated", integration, integrationSnapshot));

        var baselineV1 = await IngestFixtureAsync("baseline-transients-v1.log");
        var baselineV2 = await IngestFixtureAsync("baseline-transients-v2.log");
        var baselineV1Snapshot = Prepare(baselineV1);
        var baselineV2Snapshot = Prepare(baselineV2);
        Assert.Equal(
            Assert.Single(baselineV1.Diagnostics).Id,
            Assert.Single(baselineV2.Diagnostics).Id);
        var unchanged = DiagnosticComparisonEngine.ToReport(
            DiagnosticComparisonEngine.Compare(
                baselineV2Snapshot,
                DiagnosticBaseline.FromSnapshot("default", baselineV1Snapshot, FixedTime)));
        Assert.Equal("clean", unchanged.Status);
        metrics.Add(CompareMetric("baseline-transients", baselineV2, unchanged, baselineV2Snapshot));

        var resolvedCurrent = await IngestFixtureAsync("resolved-current.log");
        var resolvedSnapshot = Prepare(resolvedCurrent);
        var resolvedReport = DiagnosticComparisonEngine.ToReport(
            DiagnosticComparisonEngine.Compare(
                resolvedSnapshot,
                DiagnosticBaseline.FromSnapshot("default", missingDefSnapshot, FixedTime)));
        Assert.Equal("clean", resolvedReport.Status);
        Assert.Equal(1, resolvedReport.Resolved);
        metrics.Add(CompareMetric("resolved-diagnostic", resolvedCurrent, resolvedReport, resolvedSnapshot));

        var independent = await IngestFixtureAsync("independent-errors.log");
        var independentSnapshot = Prepare(independent);
        Assert.Equal(4, independent.RawOccurrenceCount);
        Assert.Equal(4, RootCount(independentSnapshot));
        metrics.Add(LatestMetric("independent-errors", independent, independentSnapshot));

        var malformed = await IngestMalformedFixtureAsync();
        var malformedSnapshot = Prepare(malformed);
        Assert.True(malformed.MalformedLineCount > 0);
        Assert.True(malformed.TruncatedLineCount > 0);
        metrics.Add(LatestMetric("malformed-truncated", malformed, malformedSnapshot));

        _output.WriteLine(
            "scenario|input_bytes|raw_occurrences|unique|root_causes|output_bytes|raw_to_output|elapsed_ms");
        foreach (var metric in metrics)
        {
            _output.WriteLine(metric.ToString());
            Assert.True(metric.OutputBytes > 0);
            Assert.True(metric.RawBytes > 0);
            Assert.True(metric.UniqueDiagnostics <= metric.RawOccurrences);
        }

        Assert.True(
            metrics.Single(metric => metric.Name == "repeated-runtime").OutputBytes <
            metrics.Single(metric => metric.Name == "repeated-runtime").RawBytes / 10);
        Assert.True(
            metrics.Single(metric => metric.Name == "initialization-downstream").OutputBytes <
            metrics.Single(metric => metric.Name == "initialization-downstream").RawBytes / 10);
    }

    [Fact]
    public async Task One_million_repeated_lines_remain_one_bounded_record()
    {
        const int repetitions = 1_000_000;
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "million.log");
        try
        {
            await using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       64 * 1024))
            {
                for (var index = 0; index < repetitions; index++)
                {
                    writer.WriteLine("System.Exception: repeated million-line failure");
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var result = await IngestFileAsync(path);
            stopwatch.Stop();
            var output = DiagnosticJson.Serialize(
                DiagnosticLatestReportBuilder.Build(Prepare(result)));
            var outputBytes = Encoding.UTF8.GetByteCount(output);

            _output.WriteLine(
                $"stress input_bytes={result.InputBytes} raw_occurrences={result.RawOccurrenceCount} " +
                $"unique={result.UniqueDiagnosticCount} output_bytes={outputBytes} " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F2}");
            Assert.Equal(repetitions, result.RawOccurrenceCount);
            Assert.Single(result.Diagnostics);
            Assert.Equal(repetitions, result.Diagnostics[0].OccurrenceCount);
            Assert.True(outputBytes < result.InputBytes / 10);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Malformed_store_is_an_operational_error_not_a_diagnostic()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "latest.json");
            await File.WriteAllTextAsync(path, "{\"v\":");

            await Assert.ThrowsAsync<JsonException>(async () =>
                await new JsonFileDiagnosticStore(path).ReadAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Unique_limit_unicode_paths_and_partial_lines_stay_bounded()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 1_000; index++)
        {
            builder.Append("System.Exception: unique failure ");
            builder.Append(index);
            builder.Append(" 🔧 in C:\\Users\\Agent\\Mods\\Example.cs:line ");
            builder.Append(index);
            builder.AppendLine();
        }

        builder.Append("System.Exception: ");
        builder.Append('x', 100_000);
        builder.Append('\0');
        builder.AppendLine();
        builder.Append("System.Exception: final partial line");

        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(builder.ToString()),
            "unicode-pathological",
            options: new DiagnosticIngestionOptions
            {
                IngestionTime = FixedTime,
                MaxLineLength = 128,
                MaxMessageLength = 128,
                MaxRawSampleLength = 128,
                MaxUniqueDiagnostics = 64
            });

        Assert.Equal(64, result.UniqueDiagnosticCount);
        Assert.True(result.DroppedDiagnosticCount > 900);
        Assert.True(result.MalformedLineCount > 0);
        Assert.True(result.TruncatedLineCount > 0);
        Assert.All(
            result.Diagnostics,
            diagnostic => Assert.True((diagnostic.Message?.Length ?? 0) <= 128));
    }

    private async Task<DiagnosticIngestionResult> IngestFixtureAsync(
        string name,
        DiagnosticIngestionMetadata? metadata = null) =>
        await IngestFileAsync(Fixture(name), metadata);

    private static async Task<DiagnosticIngestionResult> IngestFileAsync(
        string path,
        DiagnosticIngestionMetadata? metadata = null,
        DiagnosticIngestionOptions? options = null) =>
        await new DiagnosticIngestor().IngestFilesAsync(
            [path],
            metadata,
            options ?? AuditOptions);

    private async Task<DiagnosticIngestionResult> IngestRepeatedRuntimeAsync(int repetitions)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "repeated-runtime.log");
        try
        {
            await using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       64 * 1024))
            {
                for (var index = 0; index < repetitions; index++)
                {
                    var timestamp = FixedTime.AddMilliseconds(index)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                    writer.Write('[');
                    writer.Write(timestamp);
                    writer.Write("] ERROR Thread: ");
                    writer.Write(index % 31);
                    writer.Write(" System.NullReferenceException: component Thing_");
                    writer.Write(10_000 + index);
                    writer.Write(" at 0x");
                    writer.Write((index + 0xABCDEF).ToString("X", CultureInfo.InvariantCulture));
                    writer.WriteLine();
                    writer.WriteLine(
                        "   at CCM.CompAssembler.Tick() in C:\\Agent\\Mods\\Example\\Source\\Comps\\CompAssembler.cs:line 128");
                }
            }

            return await IngestFileAsync(path);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private async Task<DiagnosticIngestionResult> IngestExpandedInitializationAsync(
        int repetitions)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "initialization-downstream.log");
        try
        {
            await using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       64 * 1024))
            {
                var scenarioStart = new DateTimeOffset(
                    2026,
                    8,
                    17,
                    12,
                    0,
                    10,
                    TimeSpan.Zero);
                writer.Write(await File.ReadAllTextAsync(Fixture("initialization-downstream.log")));
                for (var index = 0; index < repetitions; index++)
                {
                    var timestamp = scenarioStart.AddSeconds(index)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                    writer.Write('[');
                    writer.Write(timestamp);
                    writer.WriteLine("] System.NullReferenceException: component state was not initialized");
                    writer.WriteLine(
                        "   at CCM.CompAssembler.Tick() in C:\\Agent\\Mods\\Example\\Source\\Comps\\CompAssembler.cs:line 128");
                    writer.Write('[');
                    writer.Write(timestamp);
                    writer.WriteLine("] Exception ticking: CCM.CompAssembler");
                    writer.WriteLine(
                        "   at CCM.CompAssembler.Tick() in C:\\Agent\\Mods\\Example\\Source\\Comps\\CompAssembler.cs:line 128");
                    writer.Write('[');
                    writer.Write(timestamp);
                    writer.WriteLine("] Exception drawing: CCM.CompAssembler");
                    writer.WriteLine(
                        "   at CCM.CompAssembler.Draw() in C:\\Agent\\Mods\\Example\\Source\\Comps\\CompAssembler.cs:line 141");
                }
            }

            return await IngestFileAsync(path);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private async Task<DiagnosticIngestionResult> IngestOperationFixtureAsync()
    {
        var devSnapshot = DiagnosticIntegrationAdapter.ParseDevBridge(
            await File.ReadAllTextAsync(IntegrationFixture("devbridge-agent-snapshot.json")));
        var devGeneration = DiagnosticIntegrationAdapter.ParseDevBridge(
            await File.ReadAllTextAsync(IntegrationFixture("devbridge-generation-context.json")));
        var rimOperations = DiagnosticIntegrationAdapter.ParseRimBridge(
            await File.ReadAllTextAsync(IntegrationFixture("rimbridge-operation-events.json")));
        var rimLogs = DiagnosticIntegrationAdapter.ParseRimBridge(
            await File.ReadAllTextAsync(IntegrationFixture("rimbridge-logs.json")));
        var integration = DiagnosticIntegrationAdapter.Combine(
            devSnapshot.State,
            devGeneration.State,
            rimOperations.State,
            rimLogs.State);
        var metadata = new DiagnosticIngestionMetadata
        {
            RunId = "run-stage8",
            Integration = integration
        };
        var result = await IngestFixtureAsync("operation-correlated.log", metadata);
        return result with { Integration = integration };
    }

    private async Task<DiagnosticIngestionResult> IngestMalformedFixtureAsync()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "malformed-truncated.log");
        try
        {
            var input = await File.ReadAllTextAsync(Fixture("malformed-truncated.log")) +
                Environment.NewLine +
                "System.Exception: " + new string('x', 100_000) + "\0" +
                Environment.NewLine +
                "System.Exception: after malformed input";
            await File.WriteAllTextAsync(path, input, new UTF8Encoding(false));
            return await IngestFileAsync(
                path,
                options: AuditOptions with
                {
                    MaxLineLength = 256,
                    MaxMessageLength = 256,
                    MaxRawSampleLength = 256
                });
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static DiagnosticStoreSnapshot Prepare(DiagnosticIngestionResult result)
    {
        var snapshot = result.ToSnapshot(FixedTime);
        snapshot = DiagnosticIntegrationCorrelator.Apply(snapshot, result.Integration);
        return DiagnosticRootCauseEngine.Apply(snapshot);
    }

    private static int RootCount(DiagnosticStoreSnapshot snapshot)
    {
        var analysis = snapshot.CausalAnalysis ?? DiagnosticRootCauseEngine.Analyze(snapshot);
        return DiagnosticRootCauseEngine
            .OrderRootCauses(snapshot.Items, analysis)
            .Count(DiagnosticLatestReportBuilder.IsActionableError);
    }

    private static AuditMetric LatestMetric(
        string name,
        DiagnosticIngestionResult result,
        DiagnosticStoreSnapshot snapshot)
    {
        var output = DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot));
        return new AuditMetric(
            name,
            result.InputBytes,
            result.RawOccurrenceCount,
            result.UniqueDiagnosticCount,
            RootCount(snapshot),
            Encoding.UTF8.GetByteCount(output),
            result.Elapsed.TotalMilliseconds);
    }

    private static AuditMetric CompareMetric(
        string name,
        DiagnosticIngestionResult result,
        DiagnosticComparisonReport report,
        DiagnosticStoreSnapshot snapshot)
    {
        var output = DiagnosticJson.Serialize(report);
        return new AuditMetric(
            name,
            result.InputBytes,
            result.RawOccurrenceCount,
            result.UniqueDiagnosticCount,
            RootCount(snapshot),
            Encoding.UTF8.GetByteCount(output),
            result.Elapsed.TotalMilliseconds);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "stage9", name);

    private static string IntegrationFixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "integration", name);

    private static DiagnosticIngestionOptions AuditOptions => new()
    {
        IngestionTime = FixedTime
    };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "rimerror-stage9-" + Guid.NewGuid().ToString("N"));
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

    private sealed record AuditMetric(
        string Name,
        long RawBytes,
        long RawOccurrences,
        int UniqueDiagnostics,
        int RootCauses,
        int OutputBytes,
        double ElapsedMilliseconds)
    {
        public override string ToString() =>
            string.Join(
                '|',
                Name,
                RawBytes,
                RawOccurrences,
                UniqueDiagnostics,
                RootCauses,
                OutputBytes,
                RawBytes / (double)Math.Max(OutputBytes, 1),
                ElapsedMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
    }
}
