namespace RimContext.Core.Content;

public sealed record ContentAdministrationResult(
    string Action,
    bool Applied,
    string? ArchetypeId = null,
    int? ArchetypeVersion = null,
    int? TargetVersion = null,
    string? BlueprintId = null,
    string? Project = null,
    string? Reason = null);

public sealed class ContentIntelligenceAdministration
{
    private readonly ContentIntelligenceStore store;

    public ContentIntelligenceAdministration(ContentIntelligenceStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ContentAdministrationResult QuarantineArchetype(
        string archetypeId,
        int version,
        string reason)
    {
        ContentArchetype? archetype = FindArchetype(archetypeId, version);
        if (archetype is null || archetype.Status != "active")
        {
            return new("quarantine", false, archetypeId, version, Reason: reason);
        }

        store.SaveArchetype(archetype with
        {
            Status = "quarantined",
            QuarantinedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            QuarantineReason = string.IsNullOrWhiteSpace(reason) ? "ADMIN_QUARANTINE" : reason.Trim()
        });
        return new("quarantine", true, archetypeId, version, Reason: reason);
    }

    public ContentAdministrationResult RollbackArchetype(
        string archetypeId,
        int targetVersion,
        string reason)
    {
        ContentIntelligenceSnapshot snapshot = store.Snapshot();
        ContentArchetype? target = snapshot.Archetypes.FirstOrDefault(item =>
            item.ArchetypeId == archetypeId && item.Version == targetVersion);
        if (target is null)
        {
            return new("rollback", false, archetypeId, TargetVersion: targetVersion, Reason: reason);
        }

        foreach (ContentArchetype archetype in snapshot.Archetypes
                     .Where(item => item.ArchetypeId == archetypeId && item.Version != targetVersion && item.Status == "active")
                     .OrderBy(item => item.Version))
        {
            store.SaveArchetype(archetype with
            {
                Status = "deprecated",
                QuarantineReason = string.IsNullOrWhiteSpace(reason) ? "ADMIN_ROLLBACK" : reason.Trim()
            });
        }

        if (target.Status != "active")
        {
            store.SaveArchetype(target with { Status = "active", QuarantineReason = null });
        }

        return new(
            "rollback",
            true,
            archetypeId,
            target.Version,
            target.Version,
            Reason: reason);
    }

    public ContentAdministrationResult ExcludeForProject(
        string precedentId,
        string project,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(precedentId) || string.IsNullOrWhiteSpace(project))
        {
            return new("project-exclusion", false, Project: project, Reason: reason);
        }

        store.SetPolicy(new ContentPrecedentPolicy(
            precedentId.Trim(),
            project.Trim(),
            Excluded: true,
            UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O")));
        return new("project-exclusion", true, Project: project, Reason: reason);
    }

    public ContentAdministrationResult MarkSourceIneligible(
        string blueprintId,
        string reason)
    {
        ContentBlueprint? blueprint = store.GetBlueprint(blueprintId);
        if (blueprint is null)
        {
            return new("source-ineligible", false, BlueprintId: blueprintId, Reason: reason);
        }

        store.SaveBlueprint(blueprint with { ExcludedFromGlobalReuse = true });
        return new("source-ineligible", true, BlueprintId: blueprintId, Reason: reason);
    }

    private ContentArchetype? FindArchetype(string archetypeId, int version) =>
        store.Snapshot().Archetypes.FirstOrDefault(item =>
            item.ArchetypeId == archetypeId && item.Version == version);
}
