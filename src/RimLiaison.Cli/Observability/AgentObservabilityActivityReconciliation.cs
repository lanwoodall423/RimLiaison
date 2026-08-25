namespace RimLiaison.Observability;

public static class AgentObservabilityActivityReconciliation
{
    public static AgentObservabilityActivityReconciliationPlan Plan(
        IReadOnlyList<AgentObservabilityActivityListItem> current,
        IReadOnlyList<AgentObservabilityActivityRow> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var desiredById = desired.ToDictionary(row => row.Event.Id, StringComparer.Ordinal);
        var working = current.Select(item => item.EventId).ToList();
        var removed = new List<string>();
        for (int index = working.Count - 1; index >= 0; index--)
        {
            if (!desiredById.ContainsKey(working[index]))
            {
                removed.Add(working[index]);
                working.RemoveAt(index);
            }
        }

        var moved = new List<string>();
        var inserted = new List<string>();
        for (int index = 0; index < desired.Count; index++)
        {
            string eventId = desired[index].Event.Id;
            if (index < working.Count &&
                string.Equals(working[index], eventId, StringComparison.Ordinal))
            {
                continue;
            }

            int existingIndex = working.FindIndex(index, value =>
                string.Equals(value, eventId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                moved.Add(eventId);
                working.RemoveAt(existingIndex);
                working.Insert(index, eventId);
            }
            else
            {
                inserted.Add(eventId);
                working.Insert(index, eventId);
            }
        }

        var currentById = current.ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var updated = desired
            .Where(row =>
                currentById.TryGetValue(row.Event.Id, out AgentObservabilityActivityListItem? item) &&
                !RowsEqual(item.Row, row))
            .Select(row => row.Event.Id)
            .ToArray();

        removed.Reverse();
        return new AgentObservabilityActivityReconciliationPlan(
            removed,
            moved,
            updated,
            inserted);
    }

    private static bool RowsEqual(
        AgentObservabilityActivityRow left,
        AgentObservabilityActivityRow right) =>
        left.Event == right.Event &&
        string.Equals(left.ModName, right.ModName, StringComparison.Ordinal) &&
        left.AgentStatus == right.AgentStatus &&
        left.HasIssue == right.HasIssue &&
        left.IssueIds.SequenceEqual(right.IssueIds, StringComparer.Ordinal);
}

public sealed record AgentObservabilityStageReconciliationPlan(
    IReadOnlyList<DevelopmentStage> RemovedStages,
    IReadOnlyList<DevelopmentStage> MovedStages,
    IReadOnlyList<DevelopmentStage> UpdatedStages,
    IReadOnlyList<DevelopmentStage> InsertedStages)
{
    public bool HasChanges =>
        RemovedStages.Count > 0 ||
        MovedStages.Count > 0 ||
        UpdatedStages.Count > 0 ||
        InsertedStages.Count > 0;
}

public static class AgentObservabilityStageReconciliation
{
    public static AgentObservabilityStageReconciliationPlan Plan(
        IReadOnlyList<AgentObservabilityStageProgress> current,
        IReadOnlyList<AgentObservabilityStageProgress> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var desiredByStage = desired.ToDictionary(
            value => value.Stage);
        var working = current.Select(value => value.Stage).ToList();
        var removed = new List<DevelopmentStage>();
        for (int index = working.Count - 1; index >= 0; index--)
        {
            if (!desiredByStage.ContainsKey(working[index]))
            {
                removed.Add(working[index]);
                working.RemoveAt(index);
            }
        }

        var moved = new List<DevelopmentStage>();
        var inserted = new List<DevelopmentStage>();
        for (int index = 0; index < desired.Count; index++)
        {
            DevelopmentStage stage = desired[index].Stage;
            if (index < working.Count && working[index] == stage)
            {
                continue;
            }

            int existingIndex = working.FindIndex(index, value => value == stage);
            if (existingIndex >= 0)
            {
                moved.Add(stage);
                working.RemoveAt(existingIndex);
                working.Insert(index, stage);
            }
            else
            {
                inserted.Add(stage);
                working.Insert(index, stage);
            }
        }

        var currentByStage = current.ToDictionary(value => value.Stage);
        var updated = desired
            .Where(value =>
                currentByStage.TryGetValue(value.Stage, out AgentObservabilityStageProgress? existing) &&
                existing != value)
            .Select(static value => value.Stage)
            .ToArray();

        removed.Reverse();
        return new(
            removed,
            moved,
            updated,
            inserted);
    }
}

public sealed record AgentObservabilityIssueListItem(
    string IssueId,
    AgentObservabilityIssueRow Row);

public sealed record AgentObservabilityIssueReconciliationPlan(
    IReadOnlyList<string> RemovedIssueIds,
    IReadOnlyList<string> MovedIssueIds,
    IReadOnlyList<string> UpdatedIssueIds,
    IReadOnlyList<string> InsertedIssueIds)
{
    public bool HasChanges =>
        RemovedIssueIds.Count > 0 ||
        MovedIssueIds.Count > 0 ||
        UpdatedIssueIds.Count > 0 ||
        InsertedIssueIds.Count > 0;
}

public static class AgentObservabilityIssueReconciliation
{
    public static AgentObservabilityIssueReconciliationPlan Plan(
        IReadOnlyList<AgentObservabilityIssueListItem> current,
        IReadOnlyList<AgentObservabilityIssueListItem> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var desiredById = desired.ToDictionary(value => value.IssueId, StringComparer.Ordinal);
        var working = current.Select(value => value.IssueId).ToList();
        var removed = new List<string>();
        for (int index = working.Count - 1; index >= 0; index--)
        {
            if (!desiredById.ContainsKey(working[index]))
            {
                removed.Add(working[index]);
                working.RemoveAt(index);
            }
        }

        var moved = new List<string>();
        var inserted = new List<string>();
        for (int index = 0; index < desired.Count; index++)
        {
            string issueId = desired[index].IssueId;
            if (index < working.Count &&
                string.Equals(working[index], issueId, StringComparison.Ordinal))
            {
                continue;
            }

            int existingIndex = working.FindIndex(
                index,
                value => string.Equals(value, issueId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                moved.Add(issueId);
                working.RemoveAt(existingIndex);
                working.Insert(index, issueId);
            }
            else
            {
                inserted.Add(issueId);
                working.Insert(index, issueId);
            }
        }

        var currentById = current.ToDictionary(value => value.IssueId, StringComparer.Ordinal);
        var updated = desired
            .Where(value =>
                currentById.TryGetValue(value.IssueId, out AgentObservabilityIssueListItem? existing) &&
                !RowsEqual(existing.Row, value.Row))
            .Select(static value => value.IssueId)
            .ToArray();

        removed.Reverse();
        return new(removed, moved, updated, inserted);
    }

    private static bool RowsEqual(
        AgentObservabilityIssueRow left,
        AgentObservabilityIssueRow right) =>
        left.Issue == right.Issue &&
        string.Equals(left.ModName, right.ModName, StringComparison.Ordinal) &&
        left.AgentStatus == right.AgentStatus &&
        left.Selected == right.Selected &&
        left.SharedAgentCount == right.SharedAgentCount &&
        left.SharedTooling == right.SharedTooling;
}
