namespace RimLiaison.Observability;

internal sealed class AgentIssueDetector
{
    private const int MaximumRepeatedWorkKeys = 4_096;
    private readonly AgentObservabilityOptions options;
    private readonly Dictionary<string, Queue<WorkObservation>> repeatedWork =
        new(StringComparer.Ordinal);

    public AgentIssueDetector(AgentObservabilityOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<AgentIssue> Observe(
        AgentEvent eventRecord,
        IEnumerable<AgentIssue> currentIssues)
    {
        AgentIssue[] allKnown = currentIssues.ToArray();
        var known = allKnown
            .Where(issue =>
                string.Equals(issue.RunId, eventRecord.RunId, StringComparison.Ordinal) &&
                string.Equals(issue.AgentId, eventRecord.AgentId, StringComparison.Ordinal) &&
                string.Equals(issue.ModId, eventRecord.ModId, StringComparison.Ordinal))
            .ToDictionary(
            static issue => issue.Id,
            StringComparer.Ordinal);
        var updates = new Dictionary<string, AgentIssue>(StringComparer.Ordinal);
        string? capabilityFingerprint =
            AgentObservabilityData.GetString(eventRecord.Data, "fingerprint");

        AgentIssue? Find(
            AgentIssueCategory category,
            string? operationKey = null,
            bool includeRecovered = false)
        {
            IEnumerable<AgentIssue> candidates = category == AgentIssueCategory.CapabilityGap &&
                !string.IsNullOrWhiteSpace(capabilityFingerprint)
                ? updates.Values
                    .Concat(allKnown)
                    .Where(issue => string.Equals(
                        issue.Fingerprint,
                        capabilityFingerprint,
                        StringComparison.Ordinal))
                : updates.Values.Concat(known.Values);
            return candidates
                    .Where(issue =>
                        issue.Category == category &&
                        (includeRecovered || !issue.Recovered) &&
                        (category == AgentIssueCategory.CapabilityGap ||
                            operationKey is null ||
                            string.Equals(issue.OperationKey, operationKey, StringComparison.Ordinal)))
                    .OrderByDescending(static issue => issue.Timestamp)
                    .ThenByDescending(static issue => issue.Occurrences)
                    .FirstOrDefault();
        }

        AgentIssue AddOrUpdate(
            AgentIssue? existing,
            AgentIssueCategory category,
            AgentIssueSeverity severity,
            string summary,
            string? operationKey,
            IEnumerable<string> supportingEventIds,
            int occurrenceIncrement = 0)
        {
            AgentIssue value = existing is null
                ? CreateIssue(
                    eventRecord,
                    category,
                    severity,
                    summary,
                    operationKey,
                    supportingEventIds,
                    options.MaximumIssueEventReferences)
                : MergeSupport(
                    existing,
                    eventRecord,
                    occurrenceIncrement,
                    options.MaximumIssueEventReferences);
            updates[value.Id] = value;
            return value;
        }

        string operationKey = OperationKey(eventRecord);
        bool failure = IsFailure(eventRecord);
        bool success = IsSuccess(eventRecord);

        if (failure)
        {
            AgentIssueCategory failureCategory = FailureCategory(eventRecord);
            AgentIssue? errorIssue = Find(failureCategory, operationKey);
            AgentIssue primary = AddOrUpdate(
                errorIssue,
                failureCategory,
                FailureSeverity(eventRecord),
                FailureSummary(eventRecord),
                operationKey,
                [eventRecord.Id],
                occurrenceIncrement: errorIssue is null ? 0 : 1);

            bool repeatedFailure = primary.EventIds.Count > 1 ||
                eventRecord.Type is AgentEventTypes.RetryCompleted or AgentEventTypes.RetryStarted;
            if (repeatedFailure)
            {
                AgentIssue? retryIssue = Find(AgentIssueCategory.Retry, operationKey);
                AddOrUpdate(
                    retryIssue,
                    AgentIssueCategory.Retry,
                    AgentIssueSeverity.Warning,
                    "Repeated failed action detected for " + operationKey + ".",
                    operationKey,
                    primary.EventIds.Concat([eventRecord.Id]),
                    occurrenceIncrement: retryIssue is null ? 0 : 1);
            }
        }

        if (eventRecord.Type is AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted)
        {
            AgentIssue? primary = Find(AgentIssueCategory.Error, operationKey);
            AgentIssue? retryIssue = Find(AgentIssueCategory.Retry, operationKey);
            IReadOnlyList<string> eventIds = primary is null
                ? [eventRecord.Id]
                : primary.EventIds.Concat([eventRecord.Id]).Distinct(StringComparer.Ordinal).ToArray();
            AddOrUpdate(
                retryIssue,
                AgentIssueCategory.Retry,
                AgentIssueSeverity.Warning,
                "Retry activity detected for " + operationKey + ".",
                operationKey,
                eventIds,
                occurrenceIncrement: retryIssue is null ? 0 : 1);
            if (primary is not null)
            {
                AddOrUpdate(
                    primary,
                    primary.Category,
                    primary.Severity,
                    primary.Summary,
                    primary.OperationKey,
                    [eventRecord.Id],
                    occurrenceIncrement: 1);
            }
        }
        if (IsExplicitIssue(eventRecord, out AgentIssueCategory category, out AgentIssueSeverity severity))
        {
            AgentIssue? existing = Find(category, operationKey);
            AddOrUpdate(
                existing,
                category,
                severity,
                ExplicitIssueSummary(category, eventRecord.Summary),
                operationKey,
                [eventRecord.Id],
                occurrenceIncrement: existing is null ? 0 : 1);
        }

        if (IsStall(eventRecord))
        {
            AgentIssue? existing = Find(AgentIssueCategory.Stall, operationKey);
            AddOrUpdate(
                existing,
                AgentIssueCategory.Stall,
                AgentIssueSeverity.Warning,
                "Potential stall: " + AgentObservabilityData.BoundText(eventRecord.Summary, 420),
                operationKey,
                [eventRecord.Id],
                occurrenceIncrement: existing is null ? 0 : 1);
        }

        if (IsRepeatableWork(eventRecord))
        {
            string workKey = string.Join(
                "\u001f",
                "work",
                eventRecord.RunId,
                eventRecord.AgentId,
                eventRecord.ModId,
                operationKey);
            if (!repeatedWork.TryGetValue(workKey, out Queue<WorkObservation>? history))
            {
                if (repeatedWork.Count >= MaximumRepeatedWorkKeys)
                {
                    string? expiredKey = repeatedWork
                        .OrderBy(static pair => pair.Value.Count == 0
                            ? long.MinValue
                            : pair.Value.Peek().Timestamp)
                        .Select(static pair => pair.Key)
                        .FirstOrDefault();
                    if (expiredKey is not null)
                    {
                        repeatedWork.Remove(expiredKey);
                    }
                }

                history = new Queue<WorkObservation>();
                repeatedWork[workKey] = history;
            }

            history.Enqueue(new WorkObservation(eventRecord.Timestamp, eventRecord.Id));
            long cutoff = eventRecord.Timestamp - (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
            while (history.Count > 0 && history.Peek().Timestamp < cutoff)
            {
                history.Dequeue();
            }
            while (history.Count > 8)
            {
                history.Dequeue();
            }

            if (history.Count >= 3)
            {
                AgentIssue? existing = Find(AgentIssueCategory.RedundantWork, operationKey);
                AddOrUpdate(
                    existing,
                    AgentIssueCategory.RedundantWork,
                    AgentIssueSeverity.Info,
                    "Potential repeated work: " + operationKey + ".",
                    operationKey,
                    history.Select(static item => item.EventId),
                    occurrenceIncrement: existing is null ? 0 : 1);
            }
        }

        if (eventRecord.Type == AgentEventTypes.RecoveryCompleted ||
            AgentObservabilityData.GetBoolean(eventRecord.Data, "recovered"))
        {
            ResolveMatchingIssues(
                operationKey,
                eventRecord,
                updates,
                known,
                includeRedundant: true,
                maximumEventReferences: options.MaximumIssueEventReferences);
        }
        else if (success)
        {
            ResolveMatchingIssues(
                operationKey,
                eventRecord,
                updates,
                known,
                includeRedundant: false,
                maximumEventReferences: options.MaximumIssueEventReferences);
        }

        if (eventRecord.Type == "issue.resolved")
        {
            string? issueId = AgentObservabilityData.GetString(eventRecord.Data, "issueId");
            if (!string.IsNullOrWhiteSpace(issueId) &&
                (updates.TryGetValue(issueId, out AgentIssue? updated) ||
                    known.TryGetValue(issueId, out updated)) &&
                !updated.Recovered)
            {
                updates[issueId] = Resolve(
                    updated,
                    eventRecord,
                    options.MaximumIssueEventReferences);
            }
        }

        return updates.Values
            .OrderBy(static issue => issue.Timestamp)
            .ThenBy(static issue => issue.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ResolveMatchingIssues(
        string operationKey,
        AgentEvent eventRecord,
        IDictionary<string, AgentIssue> updates,
        IReadOnlyDictionary<string, AgentIssue> known,
        bool includeRedundant,
        int maximumEventReferences)
    {
        foreach (AgentIssue issue in updates.Values
                     .Concat(known.Values)
                     .Where(issue =>
                         string.Equals(issue.RunId, eventRecord.RunId, StringComparison.Ordinal) &&
                         string.Equals(issue.AgentId, eventRecord.AgentId, StringComparison.Ordinal) &&
                         string.Equals(issue.ModId, eventRecord.ModId, StringComparison.Ordinal) &&
                         !issue.Recovered &&
                         string.Equals(issue.OperationKey, operationKey, StringComparison.Ordinal) &&
                         (includeRedundant || issue.Category != AgentIssueCategory.RedundantWork))
                     .GroupBy(static issue => issue.Id, StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .ToArray())
        {
            updates[issue.Id] = Resolve(issue, eventRecord, maximumEventReferences);
        }
    }

    private static AgentIssue Resolve(
        AgentIssue issue,
        AgentEvent resolution,
        int maximumEventReferences) =>
        issue with
        {
            EventIds = AddEventId(issue.EventIds, resolution.Id, maximumEventReferences),
            Recovered = true,
            ResolutionEventId = resolution.Id,
            CurrentState = "resolved",
            ResolutionState = "resolved",
            SpanIds = MergeSpanIds(issue.SpanIds, resolution.SpanId),
            TraceId = issue.TraceId ?? resolution.TraceId,
            Occurrences = Math.Max(1, issue.Occurrences)
        };

    private static AgentIssue MergeSupport(
        AgentIssue issue,
        AgentEvent eventRecord,
        int occurrenceIncrement,
        int maximumEventReferences)
    {
        return issue with
        {
            EventIds = AddEventId(issue.EventIds, eventRecord.Id, maximumEventReferences),
            RelatedFiles = MergeValues(issue.RelatedFiles, RelatedFiles(eventRecord)),
            RelatedToolCalls = MergeValues(issue.RelatedToolCalls, RelatedToolCalls(eventRecord)),
            RelatedCommands = MergeValues(issue.RelatedCommands, RelatedCommands(eventRecord)),
            SpanIds = MergeSpanIds(issue.SpanIds, eventRecord.SpanId),
            TraceId = issue.TraceId ?? eventRecord.TraceId,
            RetryCount = issue.RetryCount +
                (eventRecord.Type is AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted ? 1 : 0),
            Occurrences = Math.Max(1, issue.Occurrences + occurrenceIncrement),
            AffectedAgentIds = MergeValues(issue.AffectedAgentIds, [eventRecord.AgentId]),
            AffectedRunIds = MergeValues(issue.AffectedRunIds, [eventRecord.RunId]),
            AffectedModIds = MergeValues(issue.AffectedModIds, [eventRecord.ModId]),
            ProbableOwner = issue.ProbableOwner ??
                AgentObservabilityData.GetString(eventRecord.Data, "probableOwner")
        };
    }

    private static AgentIssue CreateIssue(
        AgentEvent eventRecord,
        AgentIssueCategory category,
        AgentIssueSeverity severity,
        string summary,
        string? operationKey,
        IEnumerable<string> supportingEventIds,
        int maximumEventReferences) =>
        new()
        {
            Id = "issue-" + Guid.NewGuid().ToString("N"),
            RunId = eventRecord.RunId,
            AgentId = eventRecord.AgentId,
            LogicalAgentId = eventRecord.LogicalAgentId,
            SessionId = eventRecord.SessionId,
            ModId = eventRecord.ModId,
            EntityType = eventRecord.EntityType,
            CanonicalEntityId = eventRecord.CanonicalEntityId,
            DisplayName = eventRecord.DisplayName,
            Timestamp = eventRecord.Timestamp,
            Category = category,
            Severity = severity,
            Summary = AgentObservabilityData.BoundText(summary, 512),
            EventIds = supportingEventIds
                .Distinct(StringComparer.Ordinal)
                .Take(maximumEventReferences)
                .ToArray(),
            Stage = eventRecord.Stage,
            RelatedFiles = RelatedFiles(eventRecord),
            RelatedToolCalls = RelatedToolCalls(eventRecord),
            RelatedCommands = RelatedCommands(eventRecord),
            Recovered = false,
            TraceId = eventRecord.TraceId,
            SpanIds = MergeSpanIds(null, eventRecord.SpanId),
            OperationKey = operationKey,
            RetryCount = eventRecord.Type is AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted ? 1 : 0,
            Classification = AgentObservabilityData.GetString(eventRecord.Data, "issueKind") ??
                category switch
                {
                    AgentIssueCategory.CapabilityGap => "TOOLING_FAILURE",
                    AgentIssueCategory.OptionalValidationUnavailable =>
                        "OPTIONAL_VALIDATION_UNAVAILABLE",
                    AgentIssueCategory.ToolingImprovement => "TOOLING_IMPROVEMENT",
                    AgentIssueCategory.ModDefect => "MOD_DEFECT",
                    AgentIssueCategory.ToolingFailure => "TOOLING_FAILURE",
                    AgentIssueCategory.InformationalProductionEvent =>
                        "INFORMATIONAL_PRODUCTION_EVENT",
                    _ => null
                },
            ValidationClassification = AgentObservabilityData.GetString(
                eventRecord.Data,
                "validationClassification"),
            Blocking = AgentObservabilityData.GetBoolean(eventRecord.Data, "blocking") ||
                category is AgentIssueCategory.Error or AgentIssueCategory.ModDefect or
                    AgentIssueCategory.ToolingFailure or AgentIssueCategory.CapabilityGap,
            CurrentState = "open",
            ResolutionState = "unresolved",
            ComponentOwner = AgentObservabilityData.GetString(eventRecord.Data, "componentOwner") ??
                AgentObservabilityData.GetString(eventRecord.Data, "probableOwner"),
            EvidenceReference = AgentObservabilityData.GetString(
                eventRecord.Data,
                "evidenceReference") ??
                AgentObservabilityData.GetString(eventRecord.Data, "evidenceLink"),
            AffectedValidation = AgentObservabilityData.GetString(
                eventRecord.Data,
                "validationId"),
            Recommendation = AgentObservabilityData.GetString(
                eventRecord.Data,
                "recommendation") ??
                AgentObservabilityData.GetString(eventRecord.Data, "recommendedRemediation"),
            CapabilityId = AgentObservabilityData.GetString(
                eventRecord.Data,
                "requiredCapabilityId") ??
                AgentObservabilityData.GetString(eventRecord.Data, "capabilityId"),
            Fingerprint = AgentObservabilityData.GetString(eventRecord.Data, "fingerprint"),
            ProbableOwner = AgentObservabilityData.GetString(eventRecord.Data, "probableOwner"),
            AffectedAgentIds = [eventRecord.AgentId],
            AffectedRunIds = [eventRecord.RunId],
            AffectedModIds = [eventRecord.ModId]
        };

    private static string OperationKey(AgentEvent eventRecord)
    {
        string? explicitKey = AgentObservabilityData.GetString(eventRecord.Data, "operationKey") ??
            AgentObservabilityData.GetString(eventRecord.Data, "operation");
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return AgentObservabilityData.BoundText(explicitKey, 256);
        }

        string? value = AgentObservabilityData.GetString(eventRecord.Data, "toolCallId") ??
            AgentObservabilityData.GetString(eventRecord.Data, "toolName") ??
            AgentObservabilityData.GetString(eventRecord.Data, "command") ??
            AgentObservabilityData.GetString(eventRecord.Data, "filePath") ??
            AgentObservabilityData.GetString(eventRecord.Data, "query");
        return !string.IsNullOrWhiteSpace(value)
            ? AgentObservabilityData.BoundText(value, 256)
            : eventRecord.Type;
    }

    private static bool IsFailure(AgentEvent eventRecord)
    {
        if (eventRecord.Type is AgentEventTypes.ToolFailed or AgentEventTypes.ToolException or
            AgentEventTypes.CommandFailed or AgentEventTypes.CommandTimeout or
            AgentEventTypes.BuildFailed or AgentEventTypes.TestFailed or AgentEventTypes.AgentFailed or
            AgentEventTypes.IntegrationFailed)
        {
            return true;
        }

        long? exitCode = AgentObservabilityData.GetInt64(eventRecord.Data, "exitCode");
        return exitCode.HasValue && exitCode.Value != 0 &&
            (eventRecord.Type.StartsWith("command", StringComparison.Ordinal) ||
                eventRecord.Type.StartsWith("tool", StringComparison.Ordinal) ||
                eventRecord.Type.StartsWith("build", StringComparison.Ordinal) ||
                eventRecord.Type.StartsWith("test", StringComparison.Ordinal));
    }

    private static bool IsSuccess(AgentEvent eventRecord)
    {
        if (IsFailure(eventRecord))
        {
            return false;
        }

        if (AgentObservabilityData.GetInt64(eventRecord.Data, "exitCode") is long exitCode)
        {
            return exitCode == 0 &&
                (eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
                    eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
                    eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase));
        }

        return eventRecord.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
            eventRecord.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentIssueCategory FailureCategory(AgentEvent eventRecord)
    {
        string? issueKind = AgentObservabilityData.GetString(eventRecord.Data, "issueKind");
        if (string.Equals(issueKind, "MOD_DEFECT", StringComparison.Ordinal))
        {
            return AgentIssueCategory.ModDefect;
        }

        if (string.Equals(issueKind, "TOOLING_FAILURE", StringComparison.Ordinal))
        {
            return AgentIssueCategory.ToolingFailure;
        }

        return AgentIssueCategory.Error;
    }

    private static bool IsBlockingCapabilityGap(AgentEvent eventRecord)
    {
        return AgentObservabilityData.GetBoolean(eventRecord.Data, "blocking") ||
            AgentObservabilityData.GetString(
                eventRecord.Data,
                "validationClassification") is null or "REQUIRED";
    }

    private static AgentIssueSeverity FailureSeverity(AgentEvent eventRecord) =>
        eventRecord.Type is AgentEventTypes.CommandTimeout
            ? AgentIssueSeverity.Warning
            : AgentIssueSeverity.Error;

    private static string FailureSummary(AgentEvent eventRecord)
    {
        string? errorCode = AgentObservabilityData.GetString(eventRecord.Data, "errorCode");
        return string.IsNullOrWhiteSpace(errorCode)
            ? AgentObservabilityData.BoundText(eventRecord.Summary, 512)
            : AgentObservabilityData.BoundText(
                eventRecord.Summary + " (" + errorCode + ")",
                512);
    }

    private static bool IsExplicitIssue(
        AgentEvent eventRecord,
        out AgentIssueCategory category,
        out AgentIssueSeverity severity)
    {
        category = default;
        severity = AgentIssueSeverity.Warning;
        switch (eventRecord.Type)
        {
            case AgentEventTypes.WorkaroundApplied:
                category = AgentIssueCategory.Workaround;
                severity = AgentIssueSeverity.Warning;
                return true;
            case AgentEventTypes.ToolLimitation:
                category = AgentIssueCategory.ToolLimitation;
                severity = AgentIssueSeverity.Info;
                return true;
            case AgentEventTypes.ValidationCapabilityBlocked:
                bool blocking = IsBlockingCapabilityGap(eventRecord);
                category = blocking
                    ? AgentIssueCategory.CapabilityGap
                    : AgentIssueCategory.OptionalValidationUnavailable;
                severity = blocking
                    ? AgentIssueSeverity.Warning
                    : AgentIssueSeverity.Info;
                return true;
            case AgentEventTypes.ValidationRecommendationRecorded:
                category = AgentIssueCategory.ToolingImprovement;
                severity = AgentIssueSeverity.Info;
                return true;
            case AgentEventTypes.InformationalProductionEvent:
                category = AgentIssueCategory.InformationalProductionEvent;
                severity = AgentIssueSeverity.Info;
                return true;
            case AgentEventTypes.ContextIssue:
                category = AgentIssueCategory.ContextIssue;
                severity = AgentIssueSeverity.Warning;
                return true;
            case AgentEventTypes.IntegrationFailed:
                category = AgentIssueCategory.IntegrationIssue;
                severity = AgentIssueSeverity.Error;
                return true;
            case "rework.started":
            case "rework.detected":
                category = AgentIssueCategory.Rework;
                severity = AgentIssueSeverity.Info;
                return true;
            default:
                return false;
        }
    }
    private static string ExplicitIssueSummary(
        AgentIssueCategory category,
        string summary)
    {
        string bounded = AgentObservabilityData.BoundText(summary, 420);
        string prefix = category switch
        {
            AgentIssueCategory.OptionalValidationUnavailable =>
                "OPTIONAL VALIDATION NOT AVAILABLE: ",
            AgentIssueCategory.ToolingImprovement =>
                "TOOLING RECOMMENDATION: ",
            AgentIssueCategory.InformationalProductionEvent =>
                "PRODUCTION INFORMATION: ",
            AgentIssueCategory.ModDefect => "MOD DEFECT: ",
            AgentIssueCategory.ToolingFailure => "TOOLING FAILURE: ",
            AgentIssueCategory.ToolLimitation => "Possible tooling limitation: ",
            AgentIssueCategory.CapabilityGap => "CAPABILITY GAP / BLOCKED: ",
            AgentIssueCategory.ContextIssue => "Possible context issue: ",
            AgentIssueCategory.Rework => "Potential rework: ",
            AgentIssueCategory.Workaround => "Workaround applied: ",
            AgentIssueCategory.IntegrationIssue => "Integration failure: ",
            _ => string.Empty
        };
        return AgentObservabilityData.BoundText(prefix + bounded, 512);
    }

    private bool IsStall(AgentEvent eventRecord)
    {
        long? duration = AgentObservabilityData.GetInt64(eventRecord.Data, "durationMs");
        return AgentObservabilityData.GetBoolean(eventRecord.Data, "stalled") ||
            eventRecord.Type is "stall.detected" or "agent.stalled" ||
            duration >= (long)options.StallThreshold.TotalMilliseconds &&
                eventRecord.Type is AgentEventTypes.AgentWaiting or "waiting.completed";
    }

    private static bool IsRepeatableWork(AgentEvent eventRecord) =>
        eventRecord.Type is AgentEventTypes.FileInspected or
            AgentEventTypes.SearchStarted or AgentEventTypes.SearchCompleted or
            "research.search" or "source.search";

    private static IReadOnlyList<string>? RelatedFiles(AgentEvent eventRecord)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        string? file = AgentObservabilityData.GetString(eventRecord.Data, "filePath") ??
            AgentObservabilityData.GetString(eventRecord.Data, "path");
        if (!string.IsNullOrWhiteSpace(file))
        {
            values.Add(file);
        }
        foreach (string value in AgentObservabilityData.GetStrings(eventRecord.Data, "relatedFiles"))
        {
            values.Add(value);
        }
        return values.Count == 0 ? null : values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string>? RelatedToolCalls(AgentEvent eventRecord)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        string? tool = AgentObservabilityData.GetString(eventRecord.Data, "toolCallId") ??
            AgentObservabilityData.GetString(eventRecord.Data, "toolName");
        if (!string.IsNullOrWhiteSpace(tool))
        {
            values.Add(tool);
        }
        foreach (string value in AgentObservabilityData.GetStrings(eventRecord.Data, "relatedToolCalls"))
        {
            values.Add(value);
        }
        return values.Count == 0 ? null : values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string>? RelatedCommands(AgentEvent eventRecord)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        string? command = AgentObservabilityData.GetString(eventRecord.Data, "command");
        if (!string.IsNullOrWhiteSpace(command))
        {
            values.Add(AgentObservabilityData.SanitizeCommand(command));
        }
        foreach (string value in AgentObservabilityData.GetStrings(eventRecord.Data, "relatedCommands"))
        {
            values.Add(AgentObservabilityData.SanitizeCommand(value));
        }
        return values.Count == 0 ? null : values.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static string[] AddEventId(
        IReadOnlyList<string> current,
        string id,
        int maximumEventReferences)
    {
        if (current.Contains(id, StringComparer.Ordinal))
        {
            return current.Take(maximumEventReferences).ToArray();
        }

        if (current.Count < maximumEventReferences)
        {
            return current.Concat([id]).ToArray();
        }

        return current
            .Skip(1)
            .Concat([id])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string>? MergeValues(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        string[] values = (left ?? []).Concat(right ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static IReadOnlyList<string>? MergeSpanIds(
        IReadOnlyList<string>? current,
        string? spanId)
    {
        if (string.IsNullOrWhiteSpace(spanId) && (current is null || current.Count == 0))
        {
            return null;
        }

        string[] values = (current ?? [])
            .Concat(string.IsNullOrWhiteSpace(spanId) ? [] : [spanId!])
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private sealed record WorkObservation(long Timestamp, string EventId);
}
