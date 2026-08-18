using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class ProjectIndexTests
{
    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "index-project");

    [Fact]
    public async Task Index_reads_projects_symbols_defs_and_references()
    {
        var index = await new ProjectIndexer().BuildOrLoadAsync(
            FixtureRoot,
            Path.Combine(CreateTemporaryDirectory(), "index.json"));

        var project = Assert.Single(index.Projects);
        Assert.Equal("FixtureMod", project.AssemblyName);
        Assert.Equal("com.example.fixturemod", project.PackageId);

        var tick = Assert.Single(index.Symbols, symbol =>
            symbol.Name == "CCM.CompAssembler.Tick");
        Assert.Equal("Source/Comps/CompAssembler.cs", tick.File);
        Assert.Equal(5, tick.Line);
        Assert.Equal("FixtureMod", tick.AssemblyName);

        Assert.Equal(2, index.Symbols.Count(symbol =>
            symbol.Name == "CCM.CompAssembler.Load"));
        Assert.Contains(index.Definitions, definition =>
            definition.Type == "ThingDef" &&
            definition.Name == "CCM_FancyWorkbench" &&
            definition.File == "Source/Defs/ThingDefs.xml");
        Assert.Contains(index.Definitions, definition =>
            definition.Type == "RecipeDef" &&
            definition.Name == "CCM_FancyWorkbenchRecipe");
        Assert.True(index.References.Count(reference =>
            reference.Name == "CCM_FancyWorkbench") >= 2);
        Assert.Contains(index.References, reference =>
            reference.File == "Source/Patches/References.xml" &&
            reference.Name == "CCM_MissingWorkbench");
    }

    [Fact]
    public async Task Exact_method_attribution_includes_line_symbol_and_assembly()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-source",
            Severity = DiagnosticSeverity.Error,
            Message = "Object reference not set",
            ExceptionType = "NullReferenceException",
            OriginatingType = "CCM.CompAssembler",
            OriginatingMethod = "Tick"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);

        Assert.Equal("Source/Comps/CompAssembler.cs", attributed.SourceFile);
        Assert.Equal(5, attributed.SourceLine);
        Assert.Equal("CCM.CompAssembler.Tick", attributed.SourceSymbol);
        Assert.Equal("FixtureMod", attributed.SourceAssembly);
        Assert.Equal("high", attributed.AttributionConfidence);
        Assert.Equal(diagnostic.Id, attributed.Id);
        Assert.Equal(
            DiagnosticFingerprint.Compute(diagnostic),
            DiagnosticFingerprint.Compute(attributed));
    }

    [Fact]
    public async Task Overloaded_method_is_file_attributed_without_a_false_exact_line()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-overload",
            Severity = DiagnosticSeverity.Error,
            Message = "failure",
            OriginatingType = "CCM.CompAssembler",
            OriginatingMethod = "Load"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);

        Assert.Equal("Source/Comps/CompAssembler.cs", attributed.SourceFile);
        Assert.Null(attributed.SourceLine);
        Assert.Equal("CCM.CompAssembler.Load", attributed.SourceSymbol);
        Assert.Equal("medium", attributed.AttributionConfidence);
        Assert.Equal(2, attributed.AttributionCandidates!.Length);
    }

    [Fact]
    public async Task Same_method_name_in_another_type_maps_to_the_other_file()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-other",
            Severity = DiagnosticSeverity.Error,
            Message = "failure",
            OriginatingType = "Other.OtherAssembler",
            OriginatingMethod = "Tick"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);

        Assert.Equal("Source/Other/OtherAssembler.cs", attributed.SourceFile);
        Assert.Equal(5, attributed.SourceLine);
        Assert.Equal("high", attributed.AttributionConfidence);
    }

    [Fact]
    public async Task Def_attribution_maps_declaration_and_referring_files()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-def",
            Severity = DiagnosticSeverity.Error,
            Message = "Could not resolve cross-reference: No Verse.ThingDef named CCM_FancyWorkbench found",
            DefType = "ThingDef",
            DefName = "CCM_FancyWorkbench"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);

        Assert.Equal("Source/Defs/ThingDefs.xml", attributed.DefSourceFile);
        Assert.Equal(2, attributed.DefSourceLine);
        Assert.Contains("Source/Defs/RecipeDefs.xml", attributed.DefReferenceFiles!);
        Assert.Contains("Source/Patches/References.xml", attributed.DefReferenceFiles!);
        Assert.Equal("high", attributed.AttributionConfidence);
    }

    [Fact]
    public async Task Missing_def_maps_likely_reference_file_without_inventing_declaration()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-missing-def",
            Severity = DiagnosticSeverity.Error,
            Message = "Could not resolve cross-reference: No ThingDef named CCM_MissingWorkbench found",
            DefType = "ThingDef",
            DefName = "CCM_MissingWorkbench"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);

        Assert.Null(attributed.DefSourceFile);
        Assert.Contains("Source/Patches/References.xml", attributed.DefReferenceFiles!);
        Assert.Equal("medium", attributed.AttributionConfidence);
    }

    [Fact]
    public async Task Cache_invalidates_for_content_changes_and_moves()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            CopyDirectory(FixtureRoot, directory);
            var cachePath = Path.Combine(directory, "cache", "index.json");
            var indexer = new ProjectIndexer();
            var first = await indexer.BuildOrLoadAsync(directory, cachePath);
            var firstCache = await File.ReadAllTextAsync(cachePath);

            var sourcePath = Path.Combine(directory, "Source", "Comps", "CompAssembler.cs");
            await File.AppendAllTextAsync(
                sourcePath,
                Environment.NewLine + "public partial class AddedOutsideScope { }" + Environment.NewLine);
            var second = await indexer.BuildOrLoadAsync(directory, cachePath);
            Assert.NotEqual(firstCache, await File.ReadAllTextAsync(cachePath));
            Assert.Contains(second.Symbols, symbol => symbol.Name == "CCM.CompAssembler.Tick");

            var oldPath = Path.Combine(directory, "Source", "Other", "OtherAssembler.cs");
            var newPath = Path.Combine(directory, "Source", "Other", "MovedAssembler.cs");
            File.Move(oldPath, newPath);
            var third = await indexer.BuildOrLoadAsync(directory, cachePath);
            Assert.DoesNotContain(third.Symbols, symbol =>
                symbol.File == "Source/Other/OtherAssembler.cs");
            Assert.Contains(third.Symbols, symbol =>
                symbol.File == "Source/Other/MovedAssembler.cs");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task Source_without_a_project_and_malformed_xml_remains_usable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Orphan.cs"),
                "namespace Orphan; public class Loader { public void Run() { } }");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Broken.xml"),
                "<Defs><ThingDef><defName>Broken");
            var index = await new ProjectIndexer().BuildOrLoadAsync(
                directory,
                Path.Combine(directory, "cache.json"));

            var symbol = Assert.Single(index.Symbols, item =>
                item.Name == "Orphan.Loader.Run");
            Assert.Null(symbol.Project);
            Assert.Null(symbol.AssemblyName);
            Assert.Empty(index.Definitions);

            var diagnostic = new DiagnosticRecord
            {
                Id = "d-orphan",
                Severity = DiagnosticSeverity.Error,
                Message = "failure",
                OriginatingType = "Orphan.Loader",
                OriginatingMethod = "Run"
            };
            var attributed = DiagnosticSourceAttributor.Enrich(diagnostic, index);
            Assert.Equal("Orphan.cs", attributed.SourceFile);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void Empty_index_does_not_change_a_diagnostic()
    {
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-empty",
            Severity = DiagnosticSeverity.Error,
            Message = "failure"
        };

        var attributed = DiagnosticSourceAttributor.Enrich(
            diagnostic,
            new ProjectIndex());

        Assert.Equal(diagnostic, attributed);
    }

    [Fact]
    public async Task Latest_summary_exposes_only_the_selected_location()
    {
        var index = await BuildFixtureIndexAsync();
        var diagnostic = DiagnosticSourceAttributor.Enrich(
            new DiagnosticRecord
            {
                Id = "d-summary",
                Severity = DiagnosticSeverity.Error,
                Category = "runtime_null_reference",
                Message = "failure",
                ExceptionType = "NullReferenceException",
                OriginatingType = "CCM.CompAssembler",
                OriginatingMethod = "Tick"
            },
            index);
        var report = DiagnosticLatestReportBuilder.Build(
            new DiagnosticStoreSnapshot { Items = [diagnostic] });
        var summary = Assert.Single(report.RootCauses!);

        Assert.Equal("Source/Comps/CompAssembler.cs", summary.Source);
        Assert.Equal(5, summary.Line);
        Assert.Equal("CCM.CompAssembler.Tick", summary.Symbol);
        Assert.Equal("medium", summary.Confidence);
    }

    private static async Task<ProjectIndex> BuildFixtureIndexAsync()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cachePath = Path.Combine(directory, "index.json");
            // The fixture is immutable during normal tests, so a cache outside
            // the fixture keeps parallel tests from competing over one cache.
            return await new ProjectIndexer().BuildOrLoadAsync(FixtureRoot, cachePath);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "rimerror-stage6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
