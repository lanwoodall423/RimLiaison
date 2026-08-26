using System.Reflection;
using System.Text.Json;
using RimDev.Contracts;
using RimLiaison.Provenance;

namespace RimLiaison.Tests;

internal static class SharedContractsTests
{
    public static void ExactIdentityMatches()
    {
        ExecutionIdentity identity = BaseIdentity();
        IdentityComparisonResult result = ExecutionIdentityComparer.Compare(
            identity,
            identity with { },
            IdentityComparisonRequirements.Static);
        Assert(result.IsExact, "identical identities must compare exactly");
        Assert(result.IsApplicable(IdentityComparisonRequirements.Static), "exact identity must apply");
    }

    public static void SourceRevisionMismatchIsRejected()
    {
        IdentityComparisonResult result = ExecutionIdentityComparer.Compare(
            BaseIdentity() with { SourceRevision = "commit-new" },
            BaseIdentity(),
            IdentityComparisonRequirements.Static);
        Assert(result.IsMismatch, "source revision changes must mismatch");
        Assert(result.MismatchedFields.Contains("sourceRevision"), "source mismatch must be named");
    }

    public static void ArtifactAndGenerationMismatchesAreRejected()
    {
        ExecutionIdentity current = BaseIdentity();
        IdentityComparisonResult artifact = ExecutionIdentityComparer.Compare(
            current with { ArtifactHash = "sha-new" },
            current,
            IdentityComparisonRequirements.Runtime);
        IdentityComparisonResult generation = ExecutionIdentityComparer.Compare(
            current with { ProcessGeneration = 8 },
            current,
            IdentityComparisonRequirements.Runtime);
        Assert(artifact.IsMismatch && artifact.MismatchedFields.Contains("artifactHash"),
            "artifact hash changes must mismatch");
        Assert(generation.IsMismatch && generation.MismatchedFields.Contains("processGeneration"),
            "process generation changes must mismatch");
    }

    public static void MissingOptionalIdentityIsCompatible()
    {
        ExecutionIdentity current = BaseIdentity() with { RuntimeInstanceId = "runtime-1" };
        IdentityComparisonResult result = ExecutionIdentityComparer.Compare(
            BaseIdentity(),
            current,
            IdentityComparisonRequirements.Static);
        Assert(result.IsCompatible, "optional runtime identity must produce a compatible result");
        Assert(result.IsApplicable(IdentityComparisonRequirements.Static),
            "optional identity absence must not block static applicability");
    }

    public static void StaleEvidenceIsRejected()
    {
        ExecutionIdentity current = BaseIdentity();
        EvidenceRecord evidence = EvidenceRecord.Create(
            "RimTest",
            "validation.runtime",
            current with { ProcessGeneration = 6 },
            "pass",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        EvidenceApplicabilityResult result = EvidenceApplicability.Evaluate(evidence, current, runtimeRequired: true);
        Assert(!result.Applicable, "stale runtime evidence must not apply");
        Assert(result.Reason == "identity-mismatch", "stale evidence must report identity mismatch");
    }

    public static void SerializationAndVersioningAreStable()
    {
        string json = JsonSerializer.Serialize(BaseIdentity());
        ExecutionIdentity? roundTrip = JsonSerializer.Deserialize<ExecutionIdentity>(json);
        Assert(roundTrip is not null, "identity must deserialize");
        Assert(roundTrip!.ComputeFingerprint() == BaseIdentity().ComputeFingerprint(),
            "identity fingerprint must survive serialization");
        Assert(json.Contains("sourceRevision", StringComparison.Ordinal),
            "versioned identity must expose source revision");
        IdentityComparisonResult unsupported = ExecutionIdentityComparer.Compare(
            BaseIdentity() with { SchemaVersion = "rimdev-execution-identity/v9" },
            BaseIdentity());
        Assert(unsupported.IsInsufficient, "unsupported identity schema must not be treated as current");
    }

    public static void LegacyValidationEvidenceAdapts()
    {
        var legacy = new ValidationEvidenceIdentity
        {
            Repository = "git:test",
            CommitSha = "commit-1",
            ValidationKind = "static",
            CoveredKinds = [ValidationEvidenceKinds.Static],
            SuiteId = "suite",
            TestIds = ["test"],
            EnvironmentFingerprint = "environment"
        };
        ValidationEvidenceRecord record = ValidationEvidenceRecord.Create(
            legacy,
            "pass",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        EvidenceRecord shared = SharedContractAdapters.ToSharedEvidence(record);
        Assert(shared.IsPassing, "legacy passing evidence must remain passing");
        Assert(shared.Identity.SourceRevision == "commit-1", "legacy source revision must adapt");
        Assert(shared.Subjects.Single().Id == "test", "legacy test identity must adapt");
    }

    public static void PayloadsAndEventsAreBounded()
    {
        string large = new('x', 20_000);
        EvidenceRecord evidence = EvidenceRecord.Create(
            "RimTest",
            "diagnostic",
            BaseIdentity(),
            "pass",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            payload: new { output = large },
            maximumPayloadBytes: 512);
        ToolEventEnvelope toolEvent = ToolEventEnvelope.Create(
            "RimLiaison",
            "validation.completed",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            BaseIdentity(),
            payload: new { output = large },
            maximumPayloadBytes: 512);
        Assert(evidence.Payload?.Truncated == true, "large evidence payload must be truncated");
        Assert(toolEvent.Payload?.Truncated == true, "large event payload must be truncated");
        Assert(toolEvent.Payload?.Sha256 is not null, "truncated event must retain a digest");
    }

    public static void SharedAssemblyHasNoToolDependencies()
    {
        Assembly assembly = typeof(ExecutionIdentity).Assembly;
        string[] forbidden = assembly.GetReferencedAssemblies()
            .Select(static item => item.Name ?? string.Empty)
            .Where(static name => name is "RimLiaison.Cli" or "RimContext.Core" or "RimError.Core")
            .ToArray();
        Assert(forbidden.Length == 0, "shared contracts must not reference tool implementations");
    }

    private static ExecutionIdentity BaseIdentity() => new()
    {
        RepositoryId = "git:test",
        ProjectId = "project",
        SourceRevision = "commit-1",
        SourceFingerprint = "source-1",
        SourceInputs = ["src/Widget.cs"],
        BuildIdentity = "build-1",
        ArtifactHash = "sha-1",
        DeploymentIdentity = "deployment-1",
        ProcessGeneration = 7,
        ExecutionId = "run-1",
        TestIds = ["test-1"]
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
