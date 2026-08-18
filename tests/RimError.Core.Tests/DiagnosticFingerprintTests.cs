using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class DiagnosticFingerprintTests
{
    [Fact]
    public void NormalizeMessage_collapses_whitespace_without_rewriting_content()
    {
        var normalized = DiagnosticFingerprint.NormalizeMessage("  first\r\n\tsecond   value  ");

        Assert.Equal("first second value", normalized);
    }

    [Fact]
    public void Compute_ignores_runtime_occurrence_fields()
    {
        var first = CreateDiagnostic();
        var later = first with
        {
            Id = "different-input-id",
            FirstOccurrence = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            LastOccurrence = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            OccurrenceCount = 12,
            RunId = "run-2",
            WorkflowId = "rw-2",
            OperationId = "operation-2",
            BaselineState = BaselineState.Changed,
            Confidence = 0.8,
            SourceAttribution = "different-source"
        };

        Assert.Equal(
            DiagnosticFingerprint.Compute(first),
            DiagnosticFingerprint.Compute(later));
    }

    [Fact]
    public void Compute_changes_when_a_semantic_field_changes()
    {
        var first = CreateDiagnostic();
        var changed = first with { ExceptionType = "System.InvalidOperationException" };

        Assert.NotEqual(
            DiagnosticFingerprint.Compute(first),
            DiagnosticFingerprint.Compute(changed));
    }

    private static DiagnosticRecord CreateDiagnostic() => new()
    {
        Id = "input-id",
        Severity = DiagnosticSeverity.Error,
        Category = "Save",
        Message = "Could not load save",
        NormalizedMessage = "Could not load save",
        ExceptionType = "System.Exception",
        OriginatingAssembly = "Verse",
        OriginatingType = "Game.Load",
        OriginatingMethod = "LoadGame",
        DefType = "ThingDef",
        DefName = "Steel"
    };
}
