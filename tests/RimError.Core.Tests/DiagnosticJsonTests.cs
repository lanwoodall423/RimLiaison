using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class DiagnosticJsonTests
{
    [Fact]
    public void Compact_serialization_uses_short_names_and_omits_stack_by_default()
    {
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-fixture",
            Severity = DiagnosticSeverity.Error,
            Message = "Example failure",
            StackFrames = ["Verse.Game.Load", "RimError.Adapter.Read"]
        };

        var json = DiagnosticJson.Serialize(diagnostic);

        Assert.Equal("{\"id\":\"d-fixture\",\"sev\":\"Error\",\"msg\":\"Example failure\",\"count\":1}", json);
    }

    [Fact]
    public void Full_serialization_includes_stack_when_requested()
    {
        var diagnostic = new DiagnosticRecord
        {
            Id = "d-fixture",
            Severity = DiagnosticSeverity.Error,
            Message = "Example failure",
            StackFrames = ["Verse.Game.Load"]
        };

        var json = DiagnosticJson.Serialize(diagnostic, includeStack: true);

        Assert.Contains("\"frames\":[\"Verse.Game.Load\"]", json);
    }

    [Fact]
    public void Snapshot_round_trips_through_compact_json()
    {
        var snapshot = new DiagnosticStoreSnapshot
        {
            CapturedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            Items =
            [
                new DiagnosticRecord
                {
                    Id = "d-one",
                    Severity = DiagnosticSeverity.Warning,
                    Message = "A warning",
                    BaselineState = BaselineState.New
                }
            ]
        };

        var json = DiagnosticJson.Serialize(snapshot);
        var restored = DiagnosticJson.Deserialize<DiagnosticStoreSnapshot>(json);

        Assert.NotNull(restored);
        Assert.Equal(1, restored!.SchemaVersion);
        Assert.Equal("d-one", Assert.Single(restored.Items).Id);
        Assert.Equal(BaselineState.New, restored.Items[0].BaselineState);
    }
}
