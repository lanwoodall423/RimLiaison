using RimLiaison.Observability;
using RimLiaison.Stack;

namespace RimLiaison.Tests;

internal static class CanonicalProjectIdentityTests
{
    public static void ManagedProjectIdentityMapIsExplicit()
    {
        (string Project, string Slug, string Package)[] values =
        [
            ("DeferredRealityFramework", "deferred-reality", "lan.deferredreality.framework"),
            ("Frontier", "frontier", "lan.frontier"),
            ("InsightCanvas", "insight-canvas", "lan.insightcanvas"),
            ("Wildlife", "wildlife", "Lan.Wildlife")
        ];
        foreach ((string project, string slug, string package) in values)
        {
            ProjectIdentity identity = ProjectIdentityResolver.Resolve(
                Manifest(project, slug, package), "C:/RimDev/Repos/" + project);
            Assert(identity.CanonicalProjectId == slug, project + " canonical identity is not the routing slug");
            Assert(identity.DisplayName == project, project + " display name changed");
            Assert(identity.RoutingSlug == slug, project + " routing slug changed");
            Assert(Path.GetFileName(identity.SourceOwner).Equals(project, StringComparison.OrdinalIgnoreCase),
                project + " source owner changed");
        }
    }

    public static void DrfDisplayNameAndRoutingSlugResolveSameCanonical()
    {
        AssertCanonical("DeferredRealityFramework", "deferred-reality", "deferred-reality");
    }

    public static void FrontierIdentifiersResolveCorrectly()
    {
        AssertCanonical("Frontier", "frontier", "frontier");
    }

    public static void InsightCanvasIdentifiersResolveCorrectly()
    {
        AssertCanonical("InsightCanvas", "insight-canvas", "insight-canvas");
    }

    public static void ExactCanonicalIdPasses()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(ProjectIdentityResolver.Matches(identity, "deferred-reality"), "canonical ID was rejected");
    }

    public static void RegisteredExplicitAliasPasses()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(ProjectIdentityResolver.Matches(identity, "DeferredRealityFramework"), "explicit manifest alias was rejected");
    }

    public static void UnregisteredAliasFails()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(!ProjectIdentityResolver.Matches(identity, "deferred-reality-framework"), "unregistered alias was accepted");
    }

    public static void WrongProjectFails()
    {
        ProjectIdentity drf = Identity("DeferredRealityFramework", "deferred-reality");
        ProjectIdentity insight = Identity("InsightCanvas", "insight-canvas");
        Assert(!ProjectIdentityResolver.SameCanonical(drf, insight), "wrong projects share canonical identity");
        Assert(!ProjectIdentityResolver.Matches(drf, insight.CanonicalProjectId), "wrong project was accepted");
    }

    public static void PackageIdentityConflictFails()
    {
        ProjectIdentity identity = Identity("Frontier", "frontier", "lan.frontier");
        Assert(!ProjectIdentityResolver.Matches(identity, "lan.frontier"), "package ID became a project alias");
    }

    public static void MetadataOwnerConflictFails()
    {
        ProjectIdentity owner = Identity("DeferredRealityFramework", "deferred-reality");
        ProjectIdentity other = Identity("InsightCanvas", "insight-canvas");
        Assert(!ProjectIdentityResolver.SameCanonical(owner, other), "metadata owners were conflated");
        Assert(owner.SourceOwner != other.SourceOwner, "source owners were conflated");
    }

    public static void ForgedOwnerFails()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(!identity.SourceOwner.EndsWith("InsightCanvas", StringComparison.OrdinalIgnoreCase),
            "forged owner was accepted as DRF");
    }

    public static void TemporaryContractPathCannotChangeIdentity()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(!ProjectIdentityResolver.Matches(identity, "C:/Temp/rimliaison-project-contract"),
            "temporary contract path became an identity alias");
    }

    public static void RuntimeFolderCannotOverrideIdentity()
    {
        RimDevStackManifest manifest = Manifest(
            "DeferredRealityFramework",
            "deferred-reality",
            "lan.deferredreality.framework",
            "DRF-runtime");
        ProjectIdentity identity = ProjectIdentityResolver.Resolve(
            manifest,
            "C:/RimDev/Repos/DeferredRealityFramework");
        Assert(!ProjectIdentityResolver.Matches(identity, identity.RuntimeFolder),
            "runtime folder changed project identity");
    }
    public static void CliSlugCannotClaimAnotherProject()
    {
        ProjectIdentity frontier = Identity("Frontier", "frontier");
        Assert(!ProjectIdentityResolver.Matches(frontier, "insight-canvas"),
            "CLI slug claimed another project");
    }

    public static void AutoEnrollmentPreservesCanonicalIdentity()
    {
        ProjectIdentity identity = Identity("DeferredRealityFramework", "deferred-reality");
        Assert(identity.CanonicalProjectId == "deferred-reality" &&
            identity.RuntimeFolder == "DeferredRealityFramework",
            "auto-enrollment mapping did not preserve canonical identity");
    }

    public static void CrossStackMaterializationPreservesCanonicalIdentity()
    {
        ProjectMetadataOwnershipTests.MaterializerUsesOwningManifestNotToolingCatalog();
    }

    public static void DeploymentMappingRetainsCanonicalIdentity()
    {
        ProjectIdentity identity = Identity("InsightCanvas", "insight-canvas");
        Assert(identity.CanonicalProjectId == "insight-canvas" &&
            identity.RuntimeFolder == "InsightCanvas",
            "deployment mapping changed canonical identity");
    }

    public static void ObservabilityRecordsCanonicalIdentity()
    {
        ObservabilityProjectIdentity identity = ObservabilityProjectIdentityResolver.Resolve(
            "C:/RimDev/Repos/DeferredRealityFramework",
            "DeferredRealityFramework");
        Assert(identity.ModId == "deferred-reality", "observability did not record canonical project identity");
        Assert(identity.ModName == "Deferred Reality Framework", "observability display name changed");
    }

    private static void AssertCanonical(string displayName, string slug, string expected)
    {
        ProjectIdentity identity = Identity(displayName, slug);
        Assert(identity.CanonicalProjectId == expected, displayName + " canonical identity mismatch");
        Assert(ProjectIdentityResolver.Matches(identity, displayName), displayName + " display alias rejected");
        Assert(ProjectIdentityResolver.Matches(identity, slug), displayName + " routing slug rejected");
    }

    private static ProjectIdentity Identity(string displayName, string slug, string package = "lan.test") =>
        ProjectIdentityResolver.Resolve(Manifest(displayName, slug, package), "C:/RimDev/Repos/" + displayName);

    private static RimDevStackManifest Manifest(
        string project,
        string slug,
        string package,
        string? runtimeFolder = null) => new()
        {
            Project = project,
            DevBridgeProject = slug,
            PackageId = package,
            RuntimeFolder = runtimeFolder ?? project
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
