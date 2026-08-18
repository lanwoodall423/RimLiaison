using RimError.Core;
using Xunit;
using Xunit.Abstractions;

namespace RimError.Core.Tests;

public sealed class RimWorldClassificationTests
{
    private static readonly DateTimeOffset FixedIngestionTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public RimWorldClassificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> SupportedCases()
    {
        yield return ["System.NullReferenceException: Object reference not set", "runtime_null_reference"];
        yield return ["MissingMethodException: Method not found: 'Verse.Workbench.DoWork()'", "runtime_missing_method"];
        yield return ["MissingFieldException: Field not found: 'Verse.Workbench.field'", "runtime_missing_field"];
        yield return ["TypeLoadException: Could not load type 'Verse.MissingType' from assembly 'Verse'", "runtime_type_load"];
        yield return ["ReflectionTypeLoadException: Unable to load one or more types", "runtime_reflection_type_load"];
        yield return ["TypeInitializationException: The type initializer for 'Verse.StaticType' threw an exception", "runtime_type_initialization"];
        yield return ["System.InvalidOperationException: generic failure\n   at Verse.Game.Tick()", "runtime_exception"];
        yield return ["Exception ticking: Pawn tick failed", "runtime_ticking"];
        yield return ["Exception drawing: Window draw failed", "runtime_drawing"];
        yield return ["Exception in LongEventHandler: long event failed", "runtime_long_event"];
        yield return ["Static constructor for 'Verse.StaticType' failed", "runtime_static_initialization"];

        yield return ["Could not resolve cross-reference: No Verse.ThingDef named CCM_FancyWorkbench found", "missing_def"];
        yield return ["Could not find ThingDef named Steel", "missing_def"];
        yield return ["Duplicate defName ThingDef.Steel", "duplicate_def"];
        yield return ["Config error: invalid value for setting", "config_error"];
        yield return ["XML error: Could not find parent node", "xml_parent"];
        yield return ["Malformed XML: unexpected end tag", "xml_malformed"];
        yield return ["PatchOperation failed: xpath did not match", "patch_operation"];
        yield return ["Could not load XML file for ThingDef", "xml_load"];

        yield return ["HarmonyException: Patching exception in method", "harmony_exception"];
        yield return ["Harmony patch target not found: 'Verse.Target.Run'", "harmony_target"];
        yield return ["Exception in prefix patch for 'Verse.Target.Run'", "harmony_prefix_postfix"];
        yield return ["Harmony signature mismatch for 'Verse.Target.Run'", "harmony_signature"];
        yield return ["Harmony transpiler failed for 'Verse.Target.Run'", "harmony_transpiler"];
        yield return ["Harmony patch processing failed", "harmony_processing"];

        yield return ["Could not load texture 'Textures/Workbench.png'", "missing_texture"];
        yield return ["Shader 'Custom/Workbench' not found", "missing_shader"];
        yield return ["Missing resource 'Sounds/Click.ogg'", "missing_asset"];
        yield return ["AssetBundle failed to load asset 'Things/Workbench.prefab'", "unity_asset_load"];

        yield return ["Could not load file or assembly 'Some.Dependency'", "assembly_load"];
        yield return ["Could not load dependency 'com.example.base'", "dependency_failure"];
        yield return ["Package ID 'com.example.mod' is invalid", "package_id"];
        yield return ["Mod load order failure: Core must load before Addon", "load_order"];

        yield return ["Mods/MyMod/Thing.cs(12,4): error CS0246: The type could not be found", "build_compile_error"];
        yield return ["Mods/MyMod/Thing.cs(14,4): warning CS0168: The variable is unused", "build_compile_warning"];
        yield return ["error MSB3270: There was a mismatch between processor architectures", "build_msbuild"];
        yield return ["error NU1101: Unable to find package Some.Package", "build_restore"];
    }

    [Theory]
    [MemberData(nameof(SupportedCases))]
    public async Task Recognizes_supported_category(string input, string expectedCategory)
    {
        var diagnostic = await IngestSingleAsync(input);

        Assert.Equal(expectedCategory, diagnostic.Category);
    }

    [Fact]
    public async Task Cross_reference_extracts_def_type_and_name()
    {
        var diagnostic = await IngestSingleAsync(
            "Could not resolve cross-reference: No Verse.ThingDef named CCM_FancyWorkbench found");

        Assert.Equal("missing_def", diagnostic.Category);
        Assert.Equal("ThingDef", diagnostic.DefType);
        Assert.Equal("CCM_FancyWorkbench", diagnostic.DefName);
    }

    [Fact]
    public async Task Missing_method_extracts_member_and_target()
    {
        var diagnostic = await IngestSingleAsync(
            "MissingMethodException: Method not found: 'Verse.Workbench.DoWork()'");

        Assert.Equal("Verse.Workbench.DoWork()", diagnostic.MissingMember);
        Assert.Equal("Verse.Workbench", diagnostic.TargetType);
        Assert.Equal("DoWork", diagnostic.TargetMethod);
    }

    [Fact]
    public async Task Static_initialization_does_not_extract_spurious_target()
    {
        var diagnostic = await IngestSingleAsync(
            "Static constructor for 'Verse.StaticType' failed");

        Assert.Equal("runtime_static_initialization", diagnostic.Category);
        Assert.Null(diagnostic.TargetType);
        Assert.Null(diagnostic.TargetMethod);
    }

    [Fact]
    public async Task Harmony_target_extracts_intended_type_and_method()
    {
        var diagnostic = await IngestSingleAsync(
            "Harmony patch target not found: 'Verse.Target.Run'");

        Assert.Equal("harmony_target", diagnostic.Category);
        Assert.Equal("Verse.Target", diagnostic.TargetType);
        Assert.Equal("Run", diagnostic.TargetMethod);
    }

    [Fact]
    public async Task Asset_and_build_fields_are_extracted()
    {
        var asset = await IngestSingleAsync("Could not load texture 'Textures/Workbench.png'");
        var build = await IngestSingleAsync("error CS0246: The type could not be found");

        Assert.Equal("Textures/Workbench.png", asset.Asset);
        Assert.Equal("CS0246", build.BuildCode);
    }

    [Fact]
    public async Task Informational_exception_wording_is_not_runtime_exception()
    {
        var diagnostic = await IngestSingleAsync("Info: Exception handling is enabled");

        Assert.Null(diagnostic.ExceptionType);
        Assert.DoesNotContain("runtime", diagnostic.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generic_xml_message_is_not_missing_def()
    {
        var diagnostic = await IngestSingleAsync("XML error: document could not be parsed");

        Assert.NotEqual("missing_def", diagnostic.Category);
    }

    [Fact]
    public async Task Arbitrary_harmony_mention_is_not_harmony_failure()
    {
        var diagnostic = await IngestSingleAsync("Info: Harmony support is enabled");

        Assert.DoesNotContain("harmony", diagnostic.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fixture_has_specific_generic_and_unrecognized_coverage()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "stage3-classification.log");
        var fixture = await File.ReadAllTextAsync(fixturePath);
        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(fixture),
            "stage3-fixture",
            options: TestOptions);

        var genericCategories = new HashSet<string>(StringComparer.Ordinal)
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
        var recognized = result.Diagnostics.Count(
            diagnostic => diagnostic.Category is not null &&
                !genericCategories.Contains(diagnostic.Category));
        var intentionallyGeneric = result.Diagnostics.Count(
            diagnostic => diagnostic.Category is not null &&
                genericCategories.Contains(diagnostic.Category));
        var unrecognized = result.Diagnostics.Count(diagnostic => diagnostic.Category is null);

        _output.WriteLine(
            $"fixture_coverage diagnostics={result.RawOccurrenceCount} recognized={recognized} " +
            $"generic={intentionallyGeneric} unrecognized={unrecognized}");

        Assert.True(recognized >= 35);
        Assert.True(intentionallyGeneric >= 1);
        Assert.True(unrecognized >= 1);
    }

    private static async Task<DiagnosticRecord> IngestSingleAsync(string input)
    {
        var result = await new DiagnosticIngestor().IngestAsync(
            new StringReader(input),
            "classification",
            options: TestOptions);
        return Assert.Single(result.Diagnostics);
    }

    private static DiagnosticIngestionOptions TestOptions => new()
    {
        IngestionTime = FixedIngestionTime
    };
}
