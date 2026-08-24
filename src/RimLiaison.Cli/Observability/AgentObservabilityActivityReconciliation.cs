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
