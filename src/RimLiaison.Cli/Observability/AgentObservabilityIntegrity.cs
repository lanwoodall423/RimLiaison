namespace RimLiaison.Observability;

public sealed record AgentObservabilityIntegrityFinding(
    string Code,
    string Message,
    string? RunId = null,
    string? AgentId = null,
    string? EventId = null,
    string? CanonicalEntityId = null);

public sealed record AgentObservabilityIntegrityReport(
    IReadOnlyList<AgentObservabilityIntegrityFinding> Findings)
{
    public bool IsValid => Findings.Count == 0;
}

/// <summary>
/// Performs a bounded, deterministic consistency check over the public store
/// contract. It is intentionally diagnostic-only: it never repairs or rewrites
/// observability state.
/// </summary>
public static class AgentObservabilityIntegrityValidator
{
    private const int MaximumRecords = 10_000;

    public static AgentObservabilityIntegrityReport Validate(
        IAgentObservabilityStore store,
        TimeSpan? workingStalenessThreshold = null,
        Func<long>? nowMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        TimeSpan threshold = workingStalenessThreshold ?? TimeSpan.FromMinutes(5);
        if (threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(workingStalenessThreshold));
        }

        long now = Math.Max(
            0,
            (nowMilliseconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))());
        AgentSnapshot[] agents = store.GetAgents(limit: MaximumRecords).ToArray();
        AgentEvent[] events = store.GetEvents(limit: 50_000).ToArray();
        AgentIssue[] issues = store.GetIssues(limit: MaximumRecords).ToArray();
        var findings = new List<AgentObservabilityIntegrityFinding>();
        var agentsBySession = agents.ToDictionary(
            static agent => new AgentObservabilityAgentIdentity(agent.RunId, agent.AgentId));

        ValidateAgents(agents, events, now, threshold, findings);
        ValidateEvents(agentsBySession, events, findings);
        ValidateSuspiciousToolSubjects(agentsBySession, events, findings);
        ValidateIssues(agentsBySession, events, issues, findings);
        ValidateNavigation(store, findings);
        return new AgentObservabilityIntegrityReport(findings);
    }

    private static void ValidateAgents(
        IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        long now,
        TimeSpan threshold,
        ICollection<AgentObservabilityIntegrityFinding> findings)
    {
        var canonicalAgents = new Dictionary<string, List<AgentSnapshot>>(
            StringComparer.OrdinalIgnoreCase);
        Dictionary<AgentObservabilityAgentIdentity, long> latestActivity = events
            .GroupBy(static eventRecord =>
                new AgentObservabilityAgentIdentity(eventRecord.RunId, eventRecord.AgentId))
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(static eventRecord => eventRecord.Timestamp));
        foreach (AgentSnapshot agent in agents)
        {
            if (string.IsNullOrWhiteSpace(agent.SessionId))
            {
                Add(
                    findings,
                    "agent.session.missing",
                    "Canonical agent has no session identity.",
                    agent);
            }

            ObservabilityEntityIdentity expected = ObservabilityEntityIdentityResolver.ForPersisted(
                agent.EntityType,
                agent.CanonicalEntityId,
                agent.ModId,
                agent.ModName,
                agent.WorkloadKind,
                agent.QualificationProfile);
            if (!string.Equals(
                    expected.EntityType,
                    agent.EntityType,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    expected.CanonicalEntityId,
                    agent.CanonicalEntityId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    findings,
                    "agent.identity.disconnected",
                    "Agent identity is not normalized by the canonical resolver.",
                    agent,
                    canonicalEntityId: agent.CanonicalEntityId);
            }

            string canonicalAgentKey = CanonicalAgentKey(agent);
            if (!canonicalAgents.TryGetValue(canonicalAgentKey, out List<AgentSnapshot>? group))
            {
                group = [];
                canonicalAgents.Add(canonicalAgentKey, group);
            }
            group.Add(agent);

            if (agent.Status is AgentStatus.Running or AgentStatus.Waiting)
            {
                long lastActivity = Math.Max(
                    agent.StartTime,
                    Math.Max(
                        agent.LastActivityAt.GetValueOrDefault(),
                        latestActivity.GetValueOrDefault(
                            new AgentObservabilityAgentIdentity(agent.RunId, agent.AgentId))));
                if (lastActivity <= 0 || now - lastActivity > threshold.TotalMilliseconds)
                {
                    Add(
                        findings,
                        "agent.working.no-credible-evidence",
                        "Working agent has no recent credible activity evidence.",
                        agent);
                }
            }
        }

        foreach ((string key, List<AgentSnapshot> group) in canonicalAgents)
        {
            if (group.Count(agent =>
                    agent.Status is (AgentStatus.Running or AgentStatus.Waiting)) > 1)
            {
                AgentSnapshot representative = group
                    .OrderByDescending(static agent => agent.LastActivityAt ?? agent.StartTime)
                    .ThenByDescending(static agent => agent.RunId, StringComparer.Ordinal)
                    .First();
                Add(
                    findings,
                    "agent.canonical.multiple-working",
                    "One canonical logical agent has multiple Working snapshots.",
                    representative,
                    canonicalEntityId: key);
            }
        }

    }
    private static void ValidateEvents(
        IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        ICollection<AgentObservabilityIntegrityFinding> findings)
    {
        foreach (AgentEvent eventRecord in events)
        {
            AgentObservabilityAgentIdentity key =
                new(eventRecord.RunId, eventRecord.AgentId);
            if (!agents.TryGetValue(key, out AgentSnapshot? owner))
            {
                Add(
                    findings,
                    "event.owner.unresolved",
                    "Accepted activity event has no resolvable agent owner.",
                    eventRecord: eventRecord,
                    canonicalEntityId: eventRecord.CanonicalEntityId);
                continue;
            }

            ObservabilityEntityIdentity expected = ObservabilityEntityIdentityResolver.ForPersisted(
                owner.EntityType,
                owner.CanonicalEntityId,
                eventRecord.ModId,
                eventRecord.DisplayName,
                owner.WorkloadKind,
                owner.QualificationProfile);
            if (!string.Equals(
                    expected.CanonicalEntityId,
                    eventRecord.CanonicalEntityId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    owner.CanonicalEntityId,
                    eventRecord.CanonicalEntityId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    findings,
                    "event.entity.disconnected",
                    "Activity event identity differs from its canonical agent entity.",
                    owner,
                    eventRecord,
                    eventRecord.CanonicalEntityId);
            }

            if (!string.IsNullOrWhiteSpace(owner.SessionId) &&
                !string.Equals(owner.SessionId, eventRecord.SessionId, StringComparison.Ordinal))
            {
                Add(
                    findings,
                    "event.session.disconnected",
                    "Activity event references a session different from its agent owner.",
                    owner,
                    eventRecord);
            }
        }
    }

    private static void ValidateSuspiciousToolSubjects(
        IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        ICollection<AgentObservabilityIntegrityFinding> findings)
    {
        foreach (AgentEvent eventRecord in events)
        {
            AgentObservabilityAgentIdentity key =
                new(eventRecord.RunId, eventRecord.AgentId);
            if (!agents.TryGetValue(key, out AgentSnapshot? owner) ||
                owner.EntityType != ObservabilityEntityTypes.Tool ||
                !string.Equals(
                    owner.CanonicalEntityId,
                    "tool:rimliaison",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] projectTargets =
                new[]
                {
                    AgentObservabilityData.GetString(eventRecord.Data, "project"),
                    AgentObservabilityData.GetString(eventRecord.Data, "repository")
                }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (projectTargets.Any(static value =>
                    !string.Equals(value, "rimliaison", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "tool:rimliaison", StringComparison.OrdinalIgnoreCase)))
            {
                Add(
                    findings,
                    "subject.tool-inversion.suspected",
                    "RimLiaison tool activity contains a project target but has no project subject.",
                    owner,
                    eventRecord,
                    canonicalEntityId: owner.CanonicalEntityId);
            }
        }
    }


    private static void ValidateIssues(
        IReadOnlyDictionary<AgentObservabilityAgentIdentity, AgentSnapshot> agents,
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<AgentIssue> issues,
        ICollection<AgentObservabilityIntegrityFinding> findings)
    {
        HashSet<string> eventIds = events
            .Select(static eventRecord => eventRecord.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (AgentIssue issue in issues)
        {
            AgentObservabilityAgentIdentity key = new(issue.RunId, issue.AgentId);
            if (!agents.ContainsKey(key))
            {
                Add(
                    findings,
                    "issue.owner.unresolved",
                    "Issue references an unknown canonical agent owner.",
                    eventId: issue.Id,
                    canonicalEntityId: issue.CanonicalEntityId);
            }
            else
            {
                AgentSnapshot owner = agents[key];
                ObservabilityEntityIdentity expected =
                    ObservabilityEntityIdentityResolver.ForPersisted(
                        owner.EntityType,
                        owner.CanonicalEntityId,
                        issue.ModId,
                        issue.DisplayName,
                        owner.WorkloadKind,
                        owner.QualificationProfile);
                bool subjectAttributed =
                    !string.IsNullOrWhiteSpace(issue.AffectedProject) ||
                    !string.IsNullOrWhiteSpace(issue.ReportingModId) &&
                        !string.Equals(
                            issue.ReportingModId,
                            issue.ModId,
                            StringComparison.Ordinal);
                if (!subjectAttributed &&
                    (!string.Equals(
                        expected.CanonicalEntityId,
                        issue.CanonicalEntityId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        owner.CanonicalEntityId,
                        issue.CanonicalEntityId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    Add(
                        findings,
                        "issue.entity.disconnected",
                        "Issue identity differs from its canonical agent entity.",
                        owner,
                        eventId: issue.Id,
                        canonicalEntityId: issue.CanonicalEntityId);
                }
            }

            foreach (string eventId in issue.EventIds)
            {
                if (!eventIds.Contains(eventId))
                {
                    Add(
                        findings,
                        "issue.event.unresolved",
                        "Issue references activity that is not queryable in the store.",
                        eventId: eventId,
                        canonicalEntityId: issue.CanonicalEntityId);
                }
            }
        }
    }

    private static void ValidateNavigation(
        IAgentObservabilityStore store,
        ICollection<AgentObservabilityIntegrityFinding> findings)
    {
        using var ui = new AgentObservabilityUi(store);
        AgentObservabilityUiNavigationItem[] items = ui.Snapshot.Navigation.Items
            .Where(static item => item.Kind == "agent")
            .ToArray();
        foreach (IGrouping<string, AgentObservabilityUiNavigationItem> duplicate in items
                     .GroupBy(
                         static item => item.Key,
                         StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            Add(
                findings,
                "navigation.entity.duplicate",
                "Top-level navigation contains duplicate canonical entities.",
                canonicalEntityId: duplicate.Key);
        }

        foreach (AgentObservabilityUiNavigationItem item in items)
        {
            string? selector = item.CanonicalEntityId ?? item.ModId;
            if (selector is null || item.RunId is null)
            {
                Add(
                    findings,
                    "navigation.entity.unresolvable",
                    "Top-level entity has no resolvable detail-query path.",
                    canonicalEntityId: item.CanonicalEntityId);
                continue;
            }

            AgentObservabilityUiSnapshot detail = ui.ShowAgent(
                selector,
                item.RunId);
            if (detail.Agent is null ||
                !string.Equals(
                    detail.Agent.Agent.CanonicalEntityId,
                    item.CanonicalEntityId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    findings,
                    "navigation.entity.unresolvable",
                    "Top-level entity has no resolvable detail-query path.",
                    canonicalEntityId: item.CanonicalEntityId);
            }
        }
    }
    private static string CanonicalAgentKey(AgentSnapshot agent)
    {
        string logical = AgentObservabilityLogicalIdentity.For(agent);
        return logical.StartsWith("legacy:", StringComparison.Ordinal)
            ? "session:" + agent.RunId + "\u001f" + agent.AgentId
            : "logical:" + logical + "\u001f" +
                AgentObservabilityEntityIdentity.GroupKey(agent);
    }

    private static void Add(
        ICollection<AgentObservabilityIntegrityFinding> findings,
        string code,
        string message,
        AgentSnapshot? agent = null,
        AgentEvent? eventRecord = null,
        string? eventId = null,
        string? canonicalEntityId = null)
    {
        findings.Add(new AgentObservabilityIntegrityFinding(
            code,
            message,
            agent?.RunId ?? eventRecord?.RunId,
            agent?.AgentId ?? eventRecord?.AgentId,
            eventId ?? eventRecord?.Id,
            canonicalEntityId ?? agent?.CanonicalEntityId ?? eventRecord?.CanonicalEntityId));
    }
}
