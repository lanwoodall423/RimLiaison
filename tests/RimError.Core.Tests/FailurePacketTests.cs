using RimDev.Contracts;
using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class FailurePacketTests
{
    [Fact]
    public async Task Structured_packet_diagnosis_uses_bounded_context()
    {
        var identity = new ExecutionIdentity
        {
            RepositoryId = "repo",
            ProjectId = "project",
            SourceRevision = "source-v1",
            ArtifactHash = "artifact-v1",
            DeploymentIdentity = "deployment-v1",
            ProcessGeneration = 1,
            ExecutionId = "execution-v1"
        };
        var packet = new FailureEvidencePacket
        {
            Identity = identity,
            FailedValidation = new EntityReference
            {
                Kind = EntityReferenceKinds.RuntimeSubject,
                Id = "plant-runtime",
                Name = "Plant runtime"
            },
            Classification = "runtime_assertion",
            Error = "Plant growth assertion failed",
            StackOrLog = new EvidenceReference
            {
                Kind = "log",
                Uri = "evidence://run/runtime.log"
            },
            ChangedSourceFiles = ["Source/Plant.cs"],
            AffectedEntities =
            [
                new EntityReference { Kind = EntityReferenceKinds.Def, Id = "Plant" }
            ],
            Dependencies =
            [
                new EntityReference { Kind = "framework", Id = "CoreFramework" }
            ],
            Frameworks =
            [
                new EntityReference { Kind = "framework", Id = "RimWorld-1.6" }
            ],
            PrecedingEvidence =
            [
                new EvidenceReference { Kind = "validation", Uri = "evidence://run/build.json" }
            ]
        };

        StructuredFailureDiagnosisResult result = await new RimErrorService()
            .DiagnoseAsync(packet);

        Assert.True(result.UsedStructuredContext);
        Assert.Equal("runtime_assertion", result.Diagnosis.Packet.Classification);
        Assert.Contains("Plant runtime", result.Diagnosis.LikelyRootCause);
        Assert.Contains(result.Diagnosis.ReproductionContext, value => value.Id == "plant-runtime");
        Assert.Contains(result.Diagnosis.ReductionCandidates, value => value.Id == "CoreFramework");
        Assert.Contains(result.Diagnosis.RelevantEvidence, value => value.Uri == "evidence://run/runtime.log");
        Assert.Contains(result.Diagnosis.AdditionalRequirements, value => value.RuntimeRequired);
        Assert.Contains(result.InspectionActions, value => value == "inspect referenced stack or log");
    }

    [Fact]
    public void Failure_packet_normalization_bounds_references()
    {
        var packet = new FailureEvidencePacket
        {
            Classification = "  compile  ",
            Error = new string('x', 2_000),
            ChangedSourceFiles = Enumerable.Repeat("Source/Plant.cs", 100).ToArray(),
            References = Enumerable.Range(0, 100)
                .Select(index => new EvidenceReference
                {
                    Kind = "log",
                    Uri = "evidence://log/" + index
                })
                .ToArray()
        }.Normalize();

        Assert.Equal("compile", packet.Classification);
        Assert.Equal(1_024, packet.Error.Length);
        Assert.True(packet.References.Count == 16);
    }
}
