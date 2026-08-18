using System.Text;
using RimError.Core;
using Xunit;
using Xunit.Abstractions;

namespace RimError.Core.Tests;

public sealed class DiagnosticRootCauseTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public DiagnosticRootCauseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Explicit_inner_exception_links_specific_failure_to_wrapper()
    {
        var inner = Error(
            "inner",
            "runtime_null_reference",
            "NullReferenceException",
            "Settings.Loader",
            "Load",
            "settings was null",
            Start);
        var wrapper = Error(
            "wrapper",
            "runtime_type_initialization",
            "TypeInitializationException",
            "Settings.Loader",
            "Load",
            "type initializer failed",
            Start.AddMilliseconds(2)) with
        {
            InnerExceptionType = "System.NullReferenceException",
            InnerExceptionMessage = "settings was null"
        };

        var analysis = DiagnosticRootCauseEngine.Analyze([inner, wrapper]);

        var link = Assert.Single(analysis.Links);
        Assert.Equal("inner", link.ParentId);
        Assert.Equal("wrapper", link.ChildId);
        Assert.Equal("high", link.Confidence);
        Assert.Contains("inner_exception", link.Signals);
        var group = Assert.Single(analysis.Groups);
        Assert.Equal("inner", group.RootId);
        Assert.Equal(["wrapper"], group.ChildIds);
    }

    [Fact]
    public void Initialization_failure_outranks_repeated_tick_downstream()
    {
        var initialization = Error(
            "init",
            "runtime_type_initialization",
            "TypeInitializationException",
            "WorldComponent",
            "Initialize",
            "settings initialization failed",
            Start);
        var ticking = Error(
            "tick",
            "runtime_ticking",
            "NullReferenceException",
            "WorldComponent",
            "Tick",
            "Exception ticking WorldComponent",
            Start.AddSeconds(1),
            count: 392) with
        {
            OperationId = "test-1",
            RunId = "run-1"
        };
        initialization = initialization with
        {
            OperationId = "test-1",
            RunId = "run-1"
        };

        var analysis = DiagnosticRootCauseEngine.Analyze([initialization, ticking]);

        var link = Assert.Single(analysis.Links);
        Assert.Equal("init", link.ParentId);
        Assert.Equal("tick", link.ChildId);
        Assert.Contains("initialization_before_downstream", link.Signals);
        Assert.Single(
            DiagnosticRootCauseEngine.OrderRootCauses(
                [initialization, ticking],
                analysis),
            record => record.Id == "init");
        Assert.DoesNotContain(
            DiagnosticRootCauseEngine.OrderRootCauses(
                [initialization, ticking],
                analysis),
            record => record.Id == "tick");
    }

    [Fact]
    public void Millisecond_proximity_does_not_group_unrelated_errors()
    {
        var first = Error(
            "first",
            "runtime_exception",
            "InvalidOperationException",
            "First.Component",
            "Run",
            "first failed",
            Start,
            assembly: "Shared.Mod") with
        {
            StackFrames = ["at First.Component.Run()"]
        };
        var second = Error(
            "second",
            "runtime_exception",
            "NullReferenceException",
            "Second.Component",
            "Run",
            "second failed",
            Start.AddMilliseconds(1),
            assembly: "Shared.Mod") with
        {
            StackFrames = ["at Second.Component.Run()"]
        };

        var analysis = DiagnosticRootCauseEngine.Analyze([first, second]);

        Assert.Empty(analysis.Links);
        Assert.Equal(2, analysis.Groups.Length);
    }

    [Fact]
    public void Same_assembly_with_unrelated_stacks_does_not_group()
    {
        var first = Error(
            "assembly-a",
            "runtime_exception",
            "Exception",
            "First.Component",
            "Run",
            "first",
            Start,
            assembly: "Shared.Mod") with
        {
            StackFrames = ["at First.Component.Run()"]
        };
        var second = Error(
            "assembly-b",
            "runtime_exception",
            "Exception",
            "Second.Component",
            "Run",
            "second",
            Start.AddSeconds(1),
            assembly: "Shared.Mod") with
        {
            StackFrames = ["at Second.Component.Run()"]
        };

        var analysis = DiagnosticRootCauseEngine.Analyze([first, second]);

        Assert.Empty(analysis.Links);
        Assert.Equal(2, analysis.Groups.Length);
    }

    [Fact]
    public void Same_def_name_with_independent_problems_does_not_group()
    {
        var first = Error(
            "def-a",
            "missing_def",
            exceptionType: null,
            type: "Defs.Loader",
            method: "Load",
            message: "Could not find ThingDef named SharedThing",
            first: Start) with
        {
            DefType = "ThingDef",
            DefName = "SharedThing"
        };
        var second = Error(
            "def-b",
            "duplicate_def",
            exceptionType: null,
            type: "Defs.Validator",
            method: "Validate",
            message: "Duplicate ThingDef named SharedThing",
            first: Start.AddMilliseconds(1)) with
        {
            DefType = "ThingDef",
            DefName = "SharedThing"
        };

        var analysis = DiagnosticRootCauseEngine.Analyze([first, second]);

        Assert.Empty(analysis.Links);
        Assert.Equal(2, analysis.Groups.Length);
    }

    [Fact]
    public void Wrappers_with_different_origins_are_not_cross_grouped()
    {
        var rootA = Error(
            "root-a",
            "runtime_null_reference",
            "NullReferenceException",
            "A.Component",
            "Tick",
            "A failed",
            Start);
        var wrapperA = Error(
            "wrapper-a",
            "runtime_ticking",
            "Exception",
            "A.Component",
            "Tick",
            "Exception ticking A",
            Start.AddSeconds(1),
            count: 40);
        var rootB = Error(
            "root-b",
            "runtime_null_reference",
            "NullReferenceException",
            "B.Component",
            "Tick",
            "B failed",
            Start.AddSeconds(2));
        var wrapperB = Error(
            "wrapper-b",
            "runtime_ticking",
            "Exception",
            "B.Component",
            "Tick",
            "Exception ticking B",
            Start.AddSeconds(3),
            count: 40);

        var analysis = DiagnosticRootCauseEngine.Analyze(
            [rootA, wrapperA, rootB, wrapperB]);

        Assert.Equal(2, analysis.Links.Length);
        Assert.Contains(analysis.Links, link =>
            link.ParentId == "root-a" && link.ChildId == "wrapper-a");
        Assert.Contains(analysis.Links, link =>
            link.ParentId == "root-b" && link.ChildId == "wrapper-b");
        Assert.DoesNotContain(analysis.Links, link =>
            link.ParentId == "root-a" && link.ChildId == "wrapper-b");
        Assert.Equal(2, analysis.Groups.Length);
    }

    [Fact]
    public void Multiple_simultaneous_initialization_failures_remain_separate()
    {
        var initA = Error(
            "init-a",
            "runtime_type_initialization",
            "TypeInitializationException",
            "A.Component",
            "Initialize",
            "A initialization failed",
            Start);
        var tickA = Error(
            "tick-a",
            "runtime_ticking",
            "NullReferenceException",
            "A.Component",
            "Tick",
            "A tick",
            Start.AddSeconds(1));
        var initB = Error(
            "init-b",
            "runtime_type_initialization",
            "TypeInitializationException",
            "B.Component",
            "Initialize",
            "B initialization failed",
            Start.AddSeconds(2));
        var tickB = Error(
            "tick-b",
            "runtime_ticking",
            "NullReferenceException",
            "B.Component",
            "Tick",
            "B tick",
            Start.AddSeconds(3));

        var analysis = DiagnosticRootCauseEngine.Analyze(
            [initA, tickA, initB, tickB]);

        Assert.Equal(2, analysis.Groups.Length);
        Assert.Contains(analysis.Groups, group =>
            group.RootId == "init-a" && group.ChildIds.SequenceEqual(["tick-a"]));
        Assert.Contains(analysis.Groups, group =>
            group.RootId == "init-b" && group.ChildIds.SequenceEqual(["tick-b"]));
    }

    [Fact]
    public void Apply_and_show_expose_parent_children_and_signals()
    {
        var root = Error(
            "root",
            "runtime_type_initialization",
            "TypeInitializationException",
            "World.Component",
            "Initialize",
            "initialization failed",
            Start);
        var child = Error(
            "child",
            "runtime_ticking",
            "NullReferenceException",
            "World.Component",
            "Tick",
            "tick failed",
            Start.AddSeconds(1));
        var applied = DiagnosticRootCauseEngine.Apply(
            new DiagnosticStoreSnapshot { Items = [root, child] });
        var shownChild = DiagnosticRootCauseEngine.EnrichForShow(
            applied,
            applied.Items.Single(item => item.Id == "child"));
        var shownRoot = DiagnosticRootCauseEngine.EnrichForShow(
            applied,
            applied.Items.Single(item => item.Id == "root"));

        Assert.Equal("root", shownChild.ParentId);
        Assert.Contains("initialization_before_downstream", shownChild.CausalSignals!);
        Assert.Contains("child", shownRoot.CausalChildren!);
        Assert.Equal("root", shownRoot.CausalRole);
        Assert.NotNull(applied.CausalAnalysis);
    }

    [Fact]
    public void Root_cause_default_output_is_smaller_than_stage6_style_output()
    {
        var root = Error(
            "root-size",
            "runtime_type_initialization",
            "TypeInitializationException",
            "World.Component",
            "Initialize",
            "initialization failed",
            Start);
        var downstream = Error(
            "downstream-size",
            "runtime_ticking",
            "NullReferenceException",
            "World.Component",
            "Tick",
            "tick failed",
            Start.AddSeconds(1),
            count: 392);
        var snapshot = new DiagnosticStoreSnapshot { Items = [root, downstream] };
        var compact = DiagnosticJson.Serialize(
            DiagnosticLatestReportBuilder.Build(snapshot));
        var stage6Style = DiagnosticJson.Serialize(
            new DiagnosticLatestReport
            {
                Status = "fail",
                Errors = 2,
                Warnings = 0,
                Diagnostics = DiagnosticLatestReportBuilder
                    .OrderForReport(snapshot.Items)
                    .Select(DiagnosticLatestReportBuilder.Summarize)
                    .ToArray()
            });

        _output.WriteLine(
            $"grouped_bytes={Encoding.UTF8.GetByteCount(compact)} " +
            $"stage6_style_bytes={Encoding.UTF8.GetByteCount(stage6Style)} " +
            $"grouped={compact}");
        Assert.True(
            Encoding.UTF8.GetByteCount(compact) <
            Encoding.UTF8.GetByteCount(stage6Style));
        Assert.Contains("rootCauses", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("downstream-size", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Causal_analysis_json_is_deterministic()
    {
        var records = new[]
        {
            Error(
                "root-deterministic",
                "runtime_type_initialization",
                "TypeInitializationException",
                "World.Component",
                "Initialize",
                "initialization failed",
                Start),
            Error(
                "child-deterministic",
                "runtime_ticking",
                "NullReferenceException",
                "World.Component",
                "Tick",
                "tick failed",
                Start.AddSeconds(1))
        };

        var first = DiagnosticJson.Serialize(
            DiagnosticRootCauseEngine.Analyze(records));
        var second = DiagnosticJson.Serialize(
            DiagnosticRootCauseEngine.Analyze([records[1], records[0]]));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Multi_symptom_fixture_groups_downstream_noise_and_keeps_unrelated_failure()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "stage7-grouped.log");
        var result = await new DiagnosticIngestor().IngestFilesAsync(
            [fixture],
            options: new DiagnosticIngestionOptions { IngestionTime = Start });
        var applied = DiagnosticRootCauseEngine.Apply(result.ToSnapshot());

        Assert.Equal(5, applied.Items.Length);
        Assert.Equal(2, applied.CausalAnalysis!.Groups.Length);
        var initializationGroup = Assert.Single(
            applied.CausalAnalysis.Groups,
            group => group.RootId == applied.Items.First(item =>
                item.Category == "runtime_type_initialization").Id);
        Assert.Equal(3, initializationGroup.ChildIds.Length);
        Assert.Contains(applied.Items, item =>
            item.Message.Contains("unrelated save failure", StringComparison.Ordinal) &&
            item.CausalRole is null);
    }

    [Fact]
    public async Task Ingestion_retains_bounded_inner_exception_signature()
    {
        var input = string.Join(
            Environment.NewLine,
            "System.TypeInitializationException: outer failure",
            " ---> System.NullReferenceException: inner failure",
            "   at Settings.Loader.Initialize()");
        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "synthetic",
            options: new DiagnosticIngestionOptions { IngestionTime = Start });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("System.NullReferenceException", diagnostic.InnerExceptionType);
        Assert.Equal("inner failure", diagnostic.InnerExceptionMessage);
    }

    private static DiagnosticRecord Error(
        string id,
        string category,
        string? exceptionType,
        string type,
        string method,
        string message,
        DateTimeOffset first,
        long count = 1,
        string? assembly = null) =>
        new()
        {
            Id = id,
            Severity = DiagnosticSeverity.Error,
            Category = category,
            Message = message,
            ExceptionType = exceptionType,
            OriginatingType = type,
            OriginatingMethod = method,
            OriginatingAssembly = assembly,
            FirstOccurrence = first,
            LastOccurrence = first,
            OccurrenceCount = count
        };
}
