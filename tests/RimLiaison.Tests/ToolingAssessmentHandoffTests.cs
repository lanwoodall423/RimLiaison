using System.Runtime.InteropServices;
using System.Text.Json;
using RimLiaison.Observability;

namespace RimLiaison.Tests;

internal static class ToolingAssessmentHandoffTests
{
    public static void ClipboardSuccessIsAutomatic()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(
            ProjectObservabilityProjection.BuildAssessment([], ProjectObservabilityCompleteness.Complete));
        string? clipboard = null;
        bool exported = false;

        ToolingAssessmentDeliveryResult result = ToolingAssessmentHandoff.Deliver(
            packet,
            value => clipboard = value,
            _ =>
            {
                exported = true;
                return true;
            });

        Equal(ToolingAssessmentDeliveryTransport.Clipboard, result.Transport);
        True(result.Succeeded);
        True(!result.UsedFallback);
        True(!exported);
        Equal(packet.ClipboardText, clipboard);
    }

    public static void ClipboardFailureFallsBackToExport()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(Assessment());
        string? exportedPayload = null;
        ToolingAssessmentDeliveryResult result = ToolingAssessmentHandoff.Deliver(
            packet,
            _ => throw new ExternalException("clipboard unavailable"),
            value =>
            {
                exportedPayload = value;
                return true;
            });

        Equal(ToolingAssessmentDeliveryTransport.Export, result.Transport);
        True(result.Succeeded);
        True(result.UsedFallback);
        Equal(packet.ExportText, exportedPayload);
    }

    public static void ExcessivePayloadFallsBackWithoutClipboardAttempt()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(
            Assessment(summary: new string('x', 2_000)),
            maximumClipboardCharacters: 128);
        bool clipboardAttempted = false;
        bool exportAttempted = false;

        ToolingAssessmentDeliveryResult result = ToolingAssessmentHandoff.Deliver(
            packet,
            _ => clipboardAttempted = true,
            _ =>
            {
                exportAttempted = true;
                return true;
            });

        True(!packet.ClipboardSufficient);
        Equal(ToolingAssessmentDeliveryTransport.Export, result.Transport);
        True(!clipboardAttempted);
        True(exportAttempted);
        True(packet.ExportText.Length > 2_000);
        True(packet.OmittedEvidence.Any(value => value.Contains("clipboard limit", StringComparison.Ordinal)));
    }

    public static void CriticalOmissionForcesExport()
    {
        ToolingAssessment assessment = Assessment(missingEvidence: ["critical: causal diagnostic was not retained"]);
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(assessment);
        bool exported = false;

        ToolingAssessmentDeliveryResult result = ToolingAssessmentHandoff.Deliver(
            packet,
            _ => throw new InvalidOperationException("must not use clipboard"),
            _ =>
            {
                exported = true;
                return true;
            });

        True(!packet.ClipboardSufficient);
        Equal(ToolingAssessmentDeliveryTransport.Export, result.Transport);
        True(exported);
    }

    public static void BoundedNoncriticalGapAllowsClipboard()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(
            Assessment(missingEvidence: ["optional: older retry transcript unavailable"]));
        True(packet.ClipboardSufficient);
        True(packet.ClipboardText.Contains("optional: older retry transcript", StringComparison.Ordinal));
    }

    public static void ClipboardAndExportContainEquivalentAssessment()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(Assessment());
        string clipboardJson = packet.ClipboardText[(packet.ClipboardText.IndexOf("{", StringComparison.Ordinal))..];
        using JsonDocument clipboard = JsonDocument.Parse(clipboardJson);
        using JsonDocument exported = JsonDocument.Parse(packet.ExportText);
        True(
            JsonSerializer.Serialize(clipboard.RootElement) ==
            JsonSerializer.Serialize(exported.RootElement));
    }

    public static void HandoffRedactsSecrets()
    {
        ToolingAssessmentHandoffPacket packet = ToolingAssessmentHandoff.Prepare(
            Assessment(command: "rimliaison doctor --token supersecret"));
        True(packet.ClipboardText.Contains("[REDACTED]", StringComparison.Ordinal));
        True(!packet.ClipboardText.Contains("supersecret", StringComparison.Ordinal));
        True(!packet.ExportText.Contains("supersecret", StringComparison.Ordinal));
    }

    public static void AssessmentCanBeBuiltFromOccurrence()
    {
        ToolingFindingOccurrence occurrence = Occurrence();
        ToolingAssessment assessment = ProjectObservabilityProjection.BuildAssessment(
            occurrence,
            ProjectObservabilityCompleteness.Complete);
        Equal(1, assessment.Findings.Count);
        Equal(occurrence.OccurrenceId, assessment.Findings.Single().SupportingEventIds!.Single());
    }

    public static void AssessmentPreservesAggregateRecurrence()
    {
        ToolingFindingOccurrence first = Occurrence("event-1", timestamp: 100);
        ToolingFindingOccurrence second = Occurrence("event-2", timestamp: 200);
        ToolingFinding aggregate = new(
            first.FindingIdentity,
            first.Kind,
            first.Summary,
            first.Confidence,
            [first, second],
            false,
            true,
            [first.ProjectId, second.ProjectId],
            [first.LogicalAgentId!],
            first.Timestamp,
            second.Timestamp,
            []);

        ToolingAssessment assessment = ProjectObservabilityProjection.BuildAssessment(
            [aggregate],
            ProjectObservabilityCompleteness.Complete);
        Equal(2, assessment.Recurrence.Single().OccurrenceCount);
        Equal(2, assessment.Recurrence.Single().ProjectCount);
        Equal(2, assessment.Findings.Single().SupportingEventIds!.Count);
    }

    private static ToolingAssessment Assessment(
        string summary = "Tooling friction observed",
        string? command = "rimliaison affected --json",
        IReadOnlyList<string>? missingEvidence = null) =>
        ProjectObservabilityProjection.BuildAssessment(
            Occurrence(summary: summary, command: command, missingEvidence: missingEvidence),
            missingEvidence is null
                ? ProjectObservabilityCompleteness.Complete
                : ProjectObservabilityCompleteness.Partial,
            missingEvidence);

    private static ToolingFindingOccurrence Occurrence(
        string occurrenceId = "event-1",
        long timestamp = 100,
        string summary = "Tooling friction observed",
        string? command = "rimliaison affected --json",
        IReadOnlyList<string>? missingEvidence = null) =>
        new(
            occurrenceId,
            "finding:tooling-friction",
            ToolingFindingKind.RecoveredFailure,
            summary,
            timestamp,
            "mod:alpha",
            "Alpha",
            "run-1",
            "agent-1",
            "logical-agent-1",
            "session-1",
            "validation",
            null,
            "DevBridge",
            "observed",
            ["event:" + occurrenceId],
            [occurrenceId],
            "DB_TIMEOUT",
            command,
            "--json",
            "bounded stdout",
            "bounded stderr",
            "diagnostic",
            ["retry"],
            1,
            250,
            1,
            0,
            null,
            "validation recovered",
            null,
            null,
            new Dictionary<string, string> { ["tool"] = "DevBridge" },
            new Dictionary<string, string> { ["os"] = "windows" },
            false,
            true,
            true,
            missingEvidence,
            false,
            ["evidence-1"]);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }
}
