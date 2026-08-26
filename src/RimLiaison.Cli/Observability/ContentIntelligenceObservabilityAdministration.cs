using RimContext.Core.Content;

namespace RimLiaison.Observability;

public sealed class ContentIntelligenceObservabilityAdministration
{
    private readonly ContentIntelligenceAdministration content;
    private readonly IAgentObservabilityStore observability;

    public ContentIntelligenceObservabilityAdministration(
        ContentIntelligenceAdministration content,
        IAgentObservabilityStore observability)
    {
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.observability = observability ?? throw new ArgumentNullException(nameof(observability));
    }

    public ContentAdministrationResult QuarantineArchetype(
        string archetypeId,
        int version,
        string reason)
    {
        ContentAdministrationResult result = content.QuarantineArchetype(archetypeId, version, reason);
        Audit(
            ContentObservabilityEventTypes.ArchetypeQuarantined,
            result,
            archetypeId,
            version,
            "quarantined");
        return result;
    }

    public ContentAdministrationResult RollbackArchetype(
        string archetypeId,
        int targetVersion,
        string reason)
    {
        ContentAdministrationResult result = content.RollbackArchetype(archetypeId, targetVersion, reason);
        Audit(
            ContentObservabilityEventTypes.RollbackCompleted,
            result,
            archetypeId,
            targetVersion,
            "rolled-back");
        return result;
    }

    public ContentAdministrationResult ExcludeForProject(
        string precedentId,
        string project,
        string reason)
    {
        ContentAdministrationResult result = content.ExcludeForProject(precedentId, project, reason);
        Audit(
            ContentObservabilityEventTypes.ProjectExclusionApplied,
            result,
            precedentId: precedentId,
            state: "excluded");
        return result;
    }

    public ContentAdministrationResult MarkSourceIneligible(
        string blueprintId,
        string reason)
    {
        ContentAdministrationResult result = content.MarkSourceIneligible(blueprintId, reason);
        Audit(
            ContentObservabilityEventTypes.SourceIneligible,
            result,
            blueprintId: blueprintId,
            state: "ineligible");
        return result;
    }

    private void Audit(
        string eventType,
        ContentAdministrationResult result,
        string? archetypeId = null,
        int? archetypeVersion = null,
        string? state = null,
        string? precedentId = null,
        string? blueprintId = null)
    {
        if (!result.Applied)
        {
            return;
        }

        AgentEvent? context = observability.GetEvents(limit: 10_000)
            .Where(value => value.Type.StartsWith("content.", StringComparison.Ordinal))
            .OrderByDescending(value => value.Sequence)
            .FirstOrDefault();
        if (context is null)
        {
            return;
        }

        observability.AppendEvent(new AgentEventRequest(
            context.RunId,
            context.AgentId,
            context.ModId,
            DevelopmentStage.Implementation,
            eventType,
            "Content Intelligence administrative intervention applied.",
            new ContentObservabilityEventData(
                ContentObservabilitySchemas.EventData,
                state ?? result.Action,
                result.Project,
                blueprintId ?? result.BlueprintId,
                precedentId,
                precedentId,
                archetypeId ?? result.ArchetypeId,
                archetypeVersion ?? result.ArchetypeVersion,
                result.TargetVersion,
                Reason: result.Reason),
            SessionId: context.SessionId,
            LogicalAgentId: context.LogicalAgentId));
    }
}
