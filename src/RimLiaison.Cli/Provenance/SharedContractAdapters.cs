using RimDev.Contracts;

namespace RimLiaison.Provenance;

public static class SharedContractAdapters
{
    public static ExecutionIdentity ToSharedIdentity(this ValidationEvidenceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ExecutionIdentity
        {
            RepositoryId = identity.Repository,
            SourceRevision = identity.CommitSha,
            SourceFingerprint = identity.ContentFingerprint,
            SourceInputs = identity.SelectedSourceInputs,
            DependencyFingerprints = identity.DependencyFingerprints,
            BuildIdentity = identity.BuildArtifactSha256,
            ArtifactHash = identity.DeploymentArtifactSha256 ?? identity.BuildArtifactSha256,
            DeploymentIdentity = identity.DeploymentCorrespondence,
            ProcessGeneration = identity.RuntimeGeneration,
            ExecutionId = identity.SuiteId,
            TestIds = identity.TestIds,
            ToolVersions = identity.ToolVersions,
            Configuration = identity.Configuration,
            EnvironmentFingerprint = identity.EnvironmentFingerprint
        };
    }

    public static EvidenceRecord ToSharedEvidence(this ValidationEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        EvidenceRecord shared = EvidenceRecord.Create(
            producer: "RimLiaison",
            evidenceType: "validation." + record.Identity.ValidationKind,
            identity: record.Identity.ToSharedIdentity(),
            status: record.Result,
            recordedAtUtc: record.RecordedAtUtc,
            subjects: record.Identity.TestIds.Select(static testId => new EntityReference
            {
                Kind = EntityReferenceKinds.Test,
                Id = testId
            }),
            payload: record.SourceProof is null ? null : new { sourceProof = record.SourceProof },
            provenance: new ContractProvenance
            {
                Source = "rimliaison-validation-evidence/v1",
                ProducerVersion = record.SchemaVersion
            });
        return shared with { EvidenceId = record.EvidenceId };

    }
    public static bool MatchesSharedIdentity(
        ValidationEvidenceIdentity evidence,
        ValidationEvidenceIdentity current,
        string kind,
        out string reason)
    {
        IdentityComparisonResult comparison = ExecutionIdentityComparer.Compare(
            evidence.ToSharedIdentity(),
            current.ToSharedIdentity(),
            kind == ValidationEvidenceKinds.Runtime
                ? IdentityComparisonRequirements.Runtime
                : IdentityComparisonRequirements.Static);
        reason = comparison.MismatchedFields.Any(static field =>
                field is "artifactHash" or "buildIdentity" or "deploymentIdentity" or "processGeneration")
            ? ValidationDecisionReasonCodes.EvidenceDeploymentMismatch
            : comparison.MismatchedFields.Contains("testIds", StringComparer.Ordinal) ||
              comparison.MissingFields.Contains("testIds", StringComparer.Ordinal)
                ? ValidationDecisionReasonCodes.EvidenceTestIdentityMismatch
                : comparison.IsInsufficient && kind == ValidationEvidenceKinds.Runtime
                    ? ValidationDecisionReasonCodes.EvidenceRuntimeGenerationMissing
                    : ValidationDecisionReasonCodes.EvidenceInputMismatch;
        return comparison.IsApplicable(kind == ValidationEvidenceKinds.Runtime
            ? IdentityComparisonRequirements.Runtime
            : IdentityComparisonRequirements.Static);
    }
}
