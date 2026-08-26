using RimDev.Contracts;

namespace RimError.Core;

public sealed record StructuredFailureDiagnosisResult(
    FailureDiagnosis Diagnosis,
    bool UsedStructuredContext,
    IReadOnlyList<string> InspectionActions);

public static class FailurePacketDiagnosis
{
    public static StructuredFailureDiagnosisResult Diagnose(FailureEvidencePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        FailureEvidencePacket normalized = packet.Normalize();
        string diagnosticId = normalized.FailedValidation?.Id ??
            normalized.Identity?.ExecutionId ??
            "failure-packet";
        var diagnostic = new DiagnosticRecord
        {
            Id = diagnosticId,
            Severity = DiagnosticSeverity.Error,
            Category = normalized.Classification,
            Message = normalized.Error,
            TestId = normalized.FailedValidation?.Kind == EntityReferenceKinds.Test
                ? normalized.FailedValidation.Id
                : null,
            SourceFile = normalized.ChangedSourceFiles.FirstOrDefault(),
            Dependency = normalized.Dependencies.FirstOrDefault()?.Id,
            PackageId = normalized.FailedValidation?.Kind == EntityReferenceKinds.Mod
                ? normalized.FailedValidation.Id
                : null
        };
        DiagnosticCausalAnalysis analysis = DiagnosticRootCauseEngine.Analyze([diagnostic]);
        string likelyRootCause = BuildRootCause(normalized);
        string confidence = normalized.AffectedEntities.Count > 0 ||
            normalized.Dependencies.Count > 0 ||
            normalized.StackOrLog is not null
            ? "medium"
            : "low";
        var evidence = new List<EvidenceReference>();
        if (normalized.StackOrLog is not null)
        {
            evidence.Add(normalized.StackOrLog);
        }
        evidence.AddRange(normalized.PrecedingEvidence);
        evidence.AddRange(normalized.References);
        EvidenceReference[] relevantEvidence = evidence
            .GroupBy(reference => reference.Uri, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(16)
            .ToArray();
        var diagnosis = new FailureDiagnosis
        {
            Packet = normalized,
            LikelyRootCause = likelyRootCause,
            Confidence = confidence,
            RelevantEvidence = relevantEvidence,
            AdditionalRequirements = normalized.FailedValidation?.Kind == EntityReferenceKinds.RuntimeSubject
                ? [new ValidationRequirement
                {
                    RequirementId = "rimerror-runtime-follow-up",
                    Subject = normalized.FailedValidation,
                    Assertion = "reproduce the structured failure with matching runtime identity",
                    RuntimeRequired = true,
                    Prerequisites = ["artifact-correspondence", "runtime-ready"],
                    Producer = "RimError",
                    Source = normalized.Classification
                }]
                : [],
            ReproductionContext = BuildReproductionContext(normalized),
            ReductionCandidates = normalized.Dependencies
                .Concat(normalized.Frameworks)
                .Take(8)
                .ToArray(),
            Provenance = new ContractProvenance
            {
                Source = "RimError.Core/failure-packet",
                References = relevantEvidence
            }
        };
        return new StructuredFailureDiagnosisResult(
            diagnosis,
            UsedStructuredContext: true,
            InspectionActions: BuildInspectionActions(normalized, analysis));
    }

    private static EntityReference[] BuildReproductionContext(FailureEvidencePacket packet)
    {
        var entities = new List<EntityReference>();
        if (packet.FailedValidation is not null)
        {
            entities.Add(packet.FailedValidation);
        }
        entities.AddRange(packet.AffectedEntities);
        entities.AddRange(packet.Dependencies);
        entities.AddRange(packet.Frameworks);
        return entities
            .GroupBy(entity => entity.Kind + "\0" + entity.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(16)
            .ToArray();
    }

    private static string BuildRootCause(FailureEvidencePacket packet)
    {
        string subject = packet.FailedValidation?.Name ?? packet.FailedValidation?.Id ?? "validation";
        string dependency = packet.Dependencies.FirstOrDefault()?.Name ??
            packet.Dependencies.FirstOrDefault()?.Id ??
            "no dependency identified";
        return $"{packet.Classification} in {subject}; inspect {dependency} and the referenced evidence before changing source.";
    }

    private static string[] BuildInspectionActions(
        FailureEvidencePacket packet,
        DiagnosticCausalAnalysis analysis)
    {
        var actions = new List<string>();
        if (packet.StackOrLog is not null)
        {
            actions.Add("inspect referenced stack or log");
        }
        if (packet.AffectedEntities.Count > 0)
        {
            actions.Add("inspect affected entity graph");
        }
        if (packet.Dependencies.Count > 0 || packet.Frameworks.Count > 0)
        {
            actions.Add("check direct dependency and framework compatibility");
        }
        if (analysis.Groups.Length == 0)
        {
            actions.Add("request additional bounded evidence");
        }
        return actions.Take(8).ToArray();
    }
}
