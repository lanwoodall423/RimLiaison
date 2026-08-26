using RimDev.Contracts;

namespace RimContext.Core.Impact;

public static class SharedValidationRequirementAdapter
{
    public static RimDev.Contracts.ValidationRequirement ToSharedRequirement(
        this RimContext.Core.Impact.ValidationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        string subjectId = requirement.TestId ?? requirement.RequirementId;
        return new RimDev.Contracts.ValidationRequirement
        {
            RequirementId = requirement.RequirementId,
            Subject = new EntityReference
            {
                Kind = requirement.TestId is null
                    ? EntityReferenceKinds.BuildArtifact
                    : EntityReferenceKinds.Test,
                Id = subjectId
            },
            Assertion = requirement.Reason,
            PreferredEvidenceLevel = requirement.Tier,
            StaticEvidenceAllowed = !string.Equals(
                requirement.Kind,
                ValidationRequirementKinds.Runtime,
                StringComparison.OrdinalIgnoreCase),
            RuntimeRequired = string.Equals(
                requirement.Kind,
                ValidationRequirementKinds.Runtime,
                StringComparison.OrdinalIgnoreCase),
            Severity = requirement.Classification,
            Producer = "RimContext",
            Source = requirement.Source
        };
    }
}
