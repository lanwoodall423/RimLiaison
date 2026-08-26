using System.Text.Json;
using RimContext.Core.Content;

namespace RimLiaison.Observability;

public static class ContentObservabilitySchemas
{
    public const string EventData = "rimliaison-content-intelligence-event/v1";
    public const string Projection = "rimliaison-content-intelligence-observability/v1";
}

public static class ContentObservabilityEventTypes
{
    public const string BlueprintCreated = "content.blueprint.created";
    public const string BlueprintUpdated = "content.blueprint.updated";
    public const string BlueprintValidated = "content.blueprint.validated";
    public const string PrecedentDetected = "content.precedent.detected";
    public const string PrecedentQualified = "content.precedent.qualified";
    public const string ReuseSelected = "content.reuse.selected";
    public const string PromotionStarted = "content.rimcontent.promotion.started";
    public const string PromotionCompleted = "content.rimcontent.promotion.completed";
    public const string PromotionRejected = "content.rimcontent.promotion.rejected";
    public const string ArchetypeUsed = "content.rimcontent.archetype.used";
    public const string RegressionDetected = "content.regression.detected";
    public const string ArchetypeQuarantined = "content.rimcontent.archetype.quarantined";
    public const string RollbackCompleted = "content.rimcontent.rollback.completed";
    public const string ProjectExclusionApplied = "content.project-exclusion.applied";
    public const string SourceIneligible = "content.source.ineligible";
}

public sealed record ContentObservabilityMetrics(
    long? ElapsedMilliseconds = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    int? ValidationAttempts = null,
    int? RepairCount = null,
    int? RetryCount = null,
    bool? Succeeded = null,
    bool? Available = null);

public sealed record ContentObservabilityEventData(
    string SchemaVersion,
    string State,
    string? ProjectId = null,
    string? BlueprintId = null,
    string? PrecedentId = null,
    string? PatternId = null,
    string? ArchetypeId = null,
    int? ArchetypeVersion = null,
    int? PreviousArchetypeVersion = null,
    string? EvidenceId = null,
    string? SourceFingerprint = null,
    string? ContentKind = null,
    string? GameplayRole = null,
    string? ReuseSource = null,
    string? Reason = null,
    string? ValidationResult = null,
    bool? Qualified = null,
    bool? ReplayPassed = null,
    string? Status = null,
    IReadOnlyList<string>? ReferenceIds = null,
    IReadOnlyList<string>? SupportingBlueprintIds = null,
    ContentObservabilityMetrics? Metrics = null,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? VanillaComparables = null,
    IReadOnlyList<string>? FrameworkRequirements = null,
    IReadOnlyList<string>? FrameworkDependencies = null,
    IReadOnlyList<string>? ValidationExpectations = null,
    string? ImplementationNovelty = null);

public sealed record ContentActivityRow(
    string EventId,
    long Timestamp,
    string Type,
    string State,
    string? ProjectId,
    string? BlueprintId,
    string? PrecedentId,
    string? ArchetypeId,
    int? ArchetypeVersion,
    string? ReuseSource,
    string Summary,
    string Reason,
    string LogicalAgentId,
    string SessionId,
    string RunId);

public sealed record ContentBlueprintRow(
    string BlueprintId,
    string? ProjectId,
    string? ContentKind,
    string? GameplayRole,
    string? LogicalAgentId,
    string? SessionId,
    string? RunId,
    string? ReuseSource,
    string? PrecedentId,
    string? ArchetypeId,
    int? ArchetypeVersion,
    string? State,
    string? ValidationResult,
    string? EvidenceId,
    string? Reason,
    int RepairCount,
    int ValidationAttempts,
    long? ElapsedMilliseconds,
    long? InputTokens,
    long? OutputTokens,
    IReadOnlyList<string> ReferenceIds,
    IReadOnlyDictionary<string, string>? DesignParameters = null,
    IReadOnlyList<string>? VanillaComparables = null,
    IReadOnlyList<string>? FrameworkRequirements = null,
    IReadOnlyList<string>? FrameworkDependencies = null,
    IReadOnlyList<string>? ValidationExpectations = null,
    string? ImplementationNovelty = null);

public sealed record ContentPrecedentRow(
    string PrecedentId,
    string? ProjectId,
    string? ContentKind,
    string? GameplayRole,
    string State,
    bool Qualified,
    int SuccessfulUses,
    int DistinctProjects,
    int DistinctRuns,
    int RepairCount,
    int ValidationAttempts,
    double? RepairRate,
    bool? ReplayPassed,
    IReadOnlyList<string> SupportingBlueprintIds,
    string? Reason,
    long LastTimestamp);

public sealed record ContentArchetypeRow(
    string ArchetypeId,
    int Version,
    string State,
    string? ContentKind,
    string? GameplayRole,
    int SuccessfulUses,
    int FailedUses,
    int RegressionCount,
    int RollbackCount,
    int? PriorStableVersion,
    bool? ReplayPassed,
    string? Reason,
    long LastTimestamp);

public sealed record ContentRegressionRow(
    string EventId,
    long Timestamp,
    string? ProjectId,
    string? BlueprintId,
    string? ArchetypeId,
    int? ArchetypeVersion,
    string State,
    string Reason,
    string EvidenceId,
    string LogicalAgentId,
    string SessionId);

public sealed record ContentReuseDistribution(
    int RimContent,
    int ProvenPrecedent,
    int VanillaReference,
    int Novel,
    int Unknown);

public sealed record ContentEfficiencyView(
    int CompletedFeatures,
    double? MedianElapsedMilliseconds,
    long? ExactInputTokens,
    long? ExactOutputTokens,
    int ValidationAttempts,
    int RepairCount,
    int RetryCount,
    int RegressionCount,
    int RollbackCount,
    double? ErrorRate,
    double? RimContentGenerationSuccessRate,
    double? PrecedentReuseSuccessRate,
    ContentReuseDistribution ReuseDistribution,
    string? TokenAvailability,
    string? TimeAvailability);

public sealed record ContentIntelligenceObservabilityView(
    string SchemaVersion,
    IReadOnlyList<ContentActivityRow> LiveActivity,
    IReadOnlyList<ContentBlueprintRow> Blueprints,
    IReadOnlyList<ContentPrecedentRow> ProvenPrecedents,
    IReadOnlyList<ContentArchetypeRow> Archetypes,
    IReadOnlyList<ContentRegressionRow> Regressions,
    ContentEfficiencyView Efficiency,
    string? SelectedBlueprintId = null,
    string? EmptyState = null);

public static class ContentIntelligenceObservabilityProjection
{
    public static ContentIntelligenceObservabilityView Build(
        IReadOnlyList<AgentEvent> events,
        string? selectedBlueprintId = null,
        int maximumActivity = 200,
        int maximumRows = 500)
    {
        ArgumentNullException.ThrowIfNull(events);
        var contentEvents = events
            .Select(TryRead)
            .Where(value => value is not null)
            .Select(value => value!)
            .OrderBy(value => value.Event.Sequence)
            .ThenBy(value => value.Event.Id, StringComparer.Ordinal)
            .ToArray();
        if (contentEvents.Length == 0)
        {
            return new ContentIntelligenceObservabilityView(
                ContentObservabilitySchemas.Projection,
                [],
                [],
                [],
                [],
                [],
                EmptyEfficiency(),
                selectedBlueprintId,
                "No Content Intelligence events have been recorded.");
        }

        var blueprints = new Dictionary<string, BlueprintAccumulator>(StringComparer.Ordinal);
        var precedents = new Dictionary<string, PrecedentAccumulator>(StringComparer.Ordinal);
        var archetypes = new Dictionary<string, ArchetypeAccumulator>(StringComparer.Ordinal);
        var regressions = new List<ContentRegressionRow>();
        foreach (ProjectionEvent item in contentEvents)
        {
            ContentObservabilityEventData data = item.Data;
            if (!string.IsNullOrWhiteSpace(data.BlueprintId))
            {
                BlueprintAccumulator blueprint = blueprints.GetValueOrDefault(data.BlueprintId!) ??
                    new BlueprintAccumulator(data.BlueprintId!);
                blueprint.Apply(item);
                blueprints[data.BlueprintId!] = blueprint;
            }

            string? precedentId = data.PrecedentId ?? data.PatternId;
            if (!string.IsNullOrWhiteSpace(precedentId))
            {
                PrecedentAccumulator precedent = precedents.GetValueOrDefault(precedentId!) ??
                    new PrecedentAccumulator(precedentId!);
                precedent.Apply(item);
                precedents[precedentId!] = precedent;
            }

            if (!string.IsNullOrWhiteSpace(data.ArchetypeId))
            {
                ArchetypeAccumulator archetype = archetypes.GetValueOrDefault(data.ArchetypeId!) ??
                    new ArchetypeAccumulator(data.ArchetypeId!);
                archetype.Apply(item);
                archetypes[data.ArchetypeId!] = archetype;
            }

            if (item.Event.Type == ContentObservabilityEventTypes.RegressionDetected)
            {
                regressions.Add(new ContentRegressionRow(
                    item.Event.Id,
                    item.Event.Timestamp,
                    data.ProjectId,
                    data.BlueprintId,
                    data.ArchetypeId,
                    data.ArchetypeVersion,
                    data.Status ?? "detected",
                    data.Reason ?? item.Event.Summary,
                    data.EvidenceId ?? "unavailable",
                    item.Event.LogicalAgentId ?? "legacy/session-scoped",
                    item.Event.SessionId ?? item.Event.RunId));
            }
        }
        foreach (PrecedentAccumulator precedent in precedents.Values)
        {
            precedent.Finalize(contentEvents);
        }

        ContentEfficiencyView efficiency = BuildEfficiency(contentEvents);
        return new ContentIntelligenceObservabilityView(
            ContentObservabilitySchemas.Projection,
            contentEvents
                .OrderByDescending(value => value.Event.Sequence)
                .Take(Math.Clamp(maximumActivity, 1, 2_000))
                .Select(ToActivity)
                .ToArray(),
            blueprints.Values
                .OrderByDescending(value => value.LastTimestamp)
                .ThenBy(value => value.BlueprintId, StringComparer.Ordinal)
                .Take(Math.Clamp(maximumRows, 1, 5_000))
                .Select(value => value.ToRow())
                .ToArray(),
            precedents.Values
                .OrderByDescending(value => value.Qualified)
                .ThenByDescending(value => value.LastTimestamp)
                .ThenBy(value => value.PrecedentId, StringComparer.Ordinal)
                .Take(Math.Clamp(maximumRows, 1, 5_000))
                .Select(value => value.ToRow())
                .ToArray(),
            archetypes.Values
                .OrderByDescending(value => value.State == "healthy")
                .ThenByDescending(value => value.LastTimestamp)
                .ThenBy(value => value.ArchetypeId, StringComparer.Ordinal)
                .Take(Math.Clamp(maximumRows, 1, 5_000))
                .Select(value => value.ToRow())
                .ToArray(),
            regressions
                .OrderByDescending(value => value.Timestamp)
                .Take(Math.Clamp(maximumRows, 1, 5_000))
                .ToArray(),
            efficiency,
            selectedBlueprintId);
    }

    private static ProjectionEvent? TryRead(AgentEvent eventRecord)
    {
        if (!eventRecord.Type.StartsWith("content.", StringComparison.Ordinal) ||
            eventRecord.Data is not JsonElement data ||
            data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            ContentObservabilityEventData? value = data.Deserialize<ContentObservabilityEventData>(AgentObservabilityJson.Options);
            return value is null || value.SchemaVersion != ContentObservabilitySchemas.EventData
                ? null
                : new ProjectionEvent(eventRecord, value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContentActivityRow ToActivity(ProjectionEvent item) =>
        new(
            item.Event.Id,
            item.Event.Timestamp,
            item.Event.Type,
            item.Data.State,
            item.Data.ProjectId,
            item.Data.BlueprintId,
            item.Data.PrecedentId ?? item.Data.PatternId,
            item.Data.ArchetypeId,
            item.Data.ArchetypeVersion,
            item.Data.ReuseSource,
            item.Event.Summary,
            item.Data.Reason ?? item.Event.Summary,
            item.Event.LogicalAgentId ?? "legacy/session-scoped",
            item.Event.SessionId ?? item.Event.RunId,
            item.Event.RunId);

    private static ContentEfficiencyView BuildEfficiency(IReadOnlyList<ProjectionEvent> events)
    {
        ProjectionEvent[] validationEvents = events
            .Where(value => value.Event.Type == ContentObservabilityEventTypes.BlueprintValidated)
            .ToArray();
        ProjectionEvent[] completed = validationEvents
            .Where(value => string.Equals(value.Data.ValidationResult, "PASS", StringComparison.OrdinalIgnoreCase))
            .GroupBy(value => value.Data.BlueprintId ?? value.Event.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(value => value.Event.Sequence).First())
            .ToArray();
        long[] durations = completed
            .Select(value => value.Data.Metrics?.ElapsedMilliseconds)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        long? inputTokens = SumIfComplete(validationEvents.Select(value => value.Data.Metrics?.InputTokens));
        long? outputTokens = SumIfComplete(validationEvents.Select(value => value.Data.Metrics?.OutputTokens));
        int validationAttempts = validationEvents.Sum(value => value.Data.Metrics?.ValidationAttempts ?? 1);
        int repairCount = validationEvents.Sum(value => value.Data.Metrics?.RepairCount ?? 0);
        int retryCount = validationEvents.Sum(value => value.Data.Metrics?.RetryCount ?? 0);
        int regressionCount = events.Count(value => value.Event.Type == ContentObservabilityEventTypes.RegressionDetected);
        int rollbackCount = events.Count(value => value.Event.Type == ContentObservabilityEventTypes.RollbackCompleted);
        int errors = validationEvents.Count(value =>
            string.Equals(value.Data.ValidationResult, "FAIL", StringComparison.OrdinalIgnoreCase));
        ContentReuseDistribution distribution = new(
            events.Count(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected && value.Data.ReuseSource == ContentReuseSources.RimContent),
            events.Count(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected && value.Data.ReuseSource == ContentReuseSources.Precedent),
            events.Count(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected && value.Data.ReuseSource == ContentReuseSources.VanillaReference),
            events.Count(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected && value.Data.ReuseSource == ContentReuseSources.Novel),
            events.Count(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected &&
                value.Data.ReuseSource is not (ContentReuseSources.RimContent or ContentReuseSources.Precedent or ContentReuseSources.VanillaReference or ContentReuseSources.Novel)));
        ProjectionEvent[] archetypeUses = events
            .Where(value => value.Event.Type == ContentObservabilityEventTypes.ArchetypeUsed)
            .ToArray();
        ProjectionEvent[] precedentUses = events
            .Where(value => value.Event.Type == ContentObservabilityEventTypes.ReuseSelected &&
                value.Data.ReuseSource == ContentReuseSources.Precedent)
            .ToArray();
        bool?[] precedentOutcomes = precedentUses
            .Select(value => ValidationOutcome(validationEvents, value.Data.BlueprintId))
            .Where(value => value.HasValue)
            .ToArray();
        return new ContentEfficiencyView(
            completed.Length,
            durations.Length == 0 ? null : durations.Length % 2 == 1
                ? durations[durations.Length / 2]
                : (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2d,
            inputTokens,
            outputTokens,
            validationAttempts,
            repairCount,
            retryCount,
            regressionCount,
            rollbackCount,
            validationAttempts == 0 ? null : errors / (double)validationAttempts,
            Rate(archetypeUses),
            precedentOutcomes.Length == 0
                ? null
                : precedentOutcomes.Count(value => value == true) / (double)precedentOutcomes.Length,
            distribution,
            inputTokens.HasValue && outputTokens.HasValue ? "available" : "unavailable",
            durations.Length == 0 ? "unavailable" : "available");
    }

    private static bool? ValidationOutcome(
        IReadOnlyList<ProjectionEvent> validationEvents,
        string? blueprintId)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
        {
            return null;
        }

        ProjectionEvent? validation = validationEvents
            .Where(value => value.Data.BlueprintId == blueprintId)
            .OrderByDescending(value => value.Event.Sequence)
            .FirstOrDefault();
        if (validation?.Data.Metrics?.Succeeded is bool succeeded)
        {
            return succeeded;
        }

        return validation?.Data.ValidationResult?.ToUpperInvariant() switch
        {
            "PASS" or "SUCCESS" => true,
            "FAIL" or "FAILED" => false,
            _ => null
        };
    }

    private static double? Rate(IReadOnlyList<ProjectionEvent> events)
    {
        bool[] outcomes = events
            .Select(value => value.Data.Metrics?.Succeeded)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return outcomes.Length == 0 ? null : outcomes.Count(value => value) / (double)outcomes.Length;
    }

    private static long? SumIfComplete(IEnumerable<long?> values)
    {
        long?[] valuesArray = values.ToArray();
        return valuesArray.Length == 0 || valuesArray.Any(value => !value.HasValue)
            ? null
            : valuesArray.Sum(value => value!.Value);
    }

    private static ContentEfficiencyView EmptyEfficiency() =>
        new(0, null, null, null, 0, 0, 0, 0, 0, null, null, null, new(0, 0, 0, 0, 0), "unavailable", "unavailable");

    private sealed record ProjectionEvent(AgentEvent Event, ContentObservabilityEventData Data);

    private sealed class BlueprintAccumulator(string blueprintId)
    {
        public string BlueprintId { get; } = blueprintId;
        public string? ProjectId { get; private set; }
        public string? ContentKind { get; private set; }
        public string? GameplayRole { get; private set; }
        public string? LogicalAgentId { get; private set; }
        public string? SessionId { get; private set; }
        public string? RunId { get; private set; }
        public string? ReuseSource { get; private set; }
        public string? PrecedentId { get; private set; }
        public string? ArchetypeId { get; private set; }
        public int? ArchetypeVersion { get; private set; }
        public string? State { get; private set; }
        public string? ValidationResult { get; private set; }
        public string? EvidenceId { get; private set; }
        public string? Reason { get; private set; }
        public int RepairCount { get; private set; }
        public int ValidationAttempts { get; private set; }
        public long? ElapsedMilliseconds { get; private set; }
        public long? InputTokens { get; private set; }
        public long? OutputTokens { get; private set; }
        public IReadOnlyDictionary<string, string>? DesignParameters { get; private set; }
        public IReadOnlyList<string>? VanillaComparables { get; private set; }
        public IReadOnlyList<string>? FrameworkRequirements { get; private set; }
        public IReadOnlyList<string>? FrameworkDependencies { get; private set; }
        public IReadOnlyList<string>? ValidationExpectations { get; private set; }
        public string? ImplementationNovelty { get; private set; }
        public List<string> ReferenceIds { get; } = [];
        public long LastTimestamp { get; private set; }

        public void Apply(ProjectionEvent item)
        {
            ContentObservabilityEventData data = item.Data;
            ProjectId ??= data.ProjectId;
            DesignParameters ??= data.DesignParameters;
            VanillaComparables ??= data.VanillaComparables;
            FrameworkRequirements ??= data.FrameworkRequirements;
            FrameworkDependencies ??= data.FrameworkDependencies;
            ValidationExpectations ??= data.ValidationExpectations;
            ImplementationNovelty ??= data.ImplementationNovelty;
            ContentKind ??= data.ContentKind;
            GameplayRole ??= data.GameplayRole;
            LogicalAgentId = item.Event.LogicalAgentId ?? LogicalAgentId;
            SessionId = item.Event.SessionId ?? SessionId;
            RunId = item.Event.RunId;
            ReuseSource = data.ReuseSource ?? ReuseSource;
            PrecedentId = data.PrecedentId ?? data.PatternId ?? PrecedentId;
            ArchetypeId = data.ArchetypeId ?? ArchetypeId;
            ArchetypeVersion = data.ArchetypeVersion ?? ArchetypeVersion;
            State = data.State;
            ValidationResult = data.ValidationResult ?? ValidationResult;
            EvidenceId = data.EvidenceId ?? EvidenceId;
            Reason = data.Reason ?? Reason;
            if (item.Event.Type == ContentObservabilityEventTypes.BlueprintValidated)
            {
                RepairCount += data.Metrics?.RepairCount ?? 0;
                ValidationAttempts += data.Metrics?.ValidationAttempts ?? 1;
                ElapsedMilliseconds = data.Metrics?.ElapsedMilliseconds ?? ElapsedMilliseconds;
                InputTokens = data.Metrics?.InputTokens ?? InputTokens;
                OutputTokens = data.Metrics?.OutputTokens ?? OutputTokens;
            }
            if (data.ReferenceIds is not null)
            {
                ReferenceIds.AddRange(data.ReferenceIds.Where(value => !ReferenceIds.Contains(value, StringComparer.Ordinal)));
            }
            LastTimestamp = Math.Max(LastTimestamp, item.Event.Timestamp);
        }

        public ContentBlueprintRow ToRow() => new(
            BlueprintId,
            ProjectId,
            ContentKind,
            GameplayRole,
            LogicalAgentId,
            SessionId,
            RunId,
            ReuseSource,
            PrecedentId,
            ArchetypeId,
            ArchetypeVersion,
            State,
            ValidationResult,
            EvidenceId,
            Reason,
            RepairCount,
            ValidationAttempts,
            ElapsedMilliseconds,
            InputTokens,
            OutputTokens,
            ReferenceIds.Distinct(StringComparer.Ordinal).ToArray(),
            DesignParameters,
            VanillaComparables,
            FrameworkRequirements,
            FrameworkDependencies,
            ValidationExpectations,
            ImplementationNovelty);
    }

    private sealed class PrecedentAccumulator(string precedentId)
    {
        public string PrecedentId { get; } = precedentId;
        public string? ProjectId { get; private set; }
        public string? ContentKind { get; private set; }
        public string? GameplayRole { get; private set; }
        public string State { get; private set; } = "observed";
        public bool Qualified { get; private set; }
        public int SuccessfulUses { get; private set; }
        public int DistinctProjects => projects.Count;
        public int DistinctRuns => runs.Count;
        public int RepairCount { get; private set; }
        public int ValidationAttempts { get; private set; }
        public double? RepairRate => ValidationAttempts == 0 ? null : RepairCount / (double)ValidationAttempts;
        public bool? ReplayPassed { get; private set; }
        public List<string> SupportingBlueprintIds { get; } = [];
        public string? Reason { get; private set; }
        public long LastTimestamp { get; private set; }
        private readonly HashSet<string> projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> runs = new(StringComparer.Ordinal);
        private readonly HashSet<string> pendingUseBlueprintIds = new(StringComparer.Ordinal);

        public void Apply(ProjectionEvent item)
        {
            ContentObservabilityEventData data = item.Data;
            ProjectId ??= data.ProjectId;
            ContentKind ??= data.ContentKind;
            GameplayRole ??= data.GameplayRole;
            if (data.Qualified == true)
            {
                State = "proven";
            }
            else if (State != "proven")
            {
                State = data.State;
            }
            Qualified |= data.Qualified == true;
            ReplayPassed = data.ReplayPassed ?? ReplayPassed;
            Reason = data.Reason ?? Reason;
            if (!string.IsNullOrWhiteSpace(data.ProjectId)) projects.Add(data.ProjectId!);
            runs.Add(item.Event.RunId);
            if (!string.IsNullOrWhiteSpace(data.BlueprintId) && !SupportingBlueprintIds.Contains(data.BlueprintId, StringComparer.Ordinal))
            {
                SupportingBlueprintIds.Add(data.BlueprintId!);
            }
            if (data.SupportingBlueprintIds is not null)
            {
                SupportingBlueprintIds.AddRange(data.SupportingBlueprintIds.Where(value => !SupportingBlueprintIds.Contains(value, StringComparer.Ordinal)));
            }
            if (item.Event.Type == ContentObservabilityEventTypes.ReuseSelected &&
                data.ReuseSource == ContentReuseSources.Precedent)
            {
                if (data.Metrics?.Succeeded == true)
                {
                    SuccessfulUses++;
                }
                else if (data.Metrics?.Succeeded is null && data.BlueprintId is not null)
                {
                    pendingUseBlueprintIds.Add(data.BlueprintId);
                }
            }
            if (item.Event.Type == ContentObservabilityEventTypes.PrecedentDetected)
            {
                RepairCount += data.Metrics?.RepairCount ?? 0;
                ValidationAttempts += data.Metrics?.ValidationAttempts ?? 0;
            }
            LastTimestamp = Math.Max(LastTimestamp, item.Event.Timestamp);
        }

        public void Finalize(IReadOnlyList<ProjectionEvent> events)
        {
            foreach (string blueprintId in pendingUseBlueprintIds)
            {
                if (ValidationOutcome(events, blueprintId) == true)
                {
                    SuccessfulUses++;
                }
            }
        }

        public ContentPrecedentRow ToRow() => new(
            PrecedentId,
            ProjectId,
            ContentKind,
            GameplayRole,
            State,
            Qualified,
            SuccessfulUses,
            DistinctProjects,
            DistinctRuns,
            RepairCount,
            ValidationAttempts,
            RepairRate,
            ReplayPassed,
            SupportingBlueprintIds.Distinct(StringComparer.Ordinal).ToArray(),
            Reason,
            LastTimestamp);
    }

    private sealed class ArchetypeAccumulator(string archetypeId)
    {
        public string ArchetypeId { get; } = archetypeId;
        public int Version { get; private set; }
        public string State { get; private set; } = "observed";
        public string? ContentKind { get; private set; }
        public string? GameplayRole { get; private set; }
        public int SuccessfulUses { get; private set; }
        public int FailedUses { get; private set; }
        public int RegressionCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int? PriorStableVersion { get; private set; }
        public bool? ReplayPassed { get; private set; }
        public string? Reason { get; private set; }
        public long LastTimestamp { get; private set; }

        public void Apply(ProjectionEvent item)
        {
            ContentObservabilityEventData data = item.Data;
            Version = Math.Max(Version, data.ArchetypeVersion ?? 0);
            ContentKind ??= data.ContentKind;
            GameplayRole ??= data.GameplayRole;
            State = item.Event.Type switch
            {
                ContentObservabilityEventTypes.ArchetypeQuarantined => "quarantined",
                ContentObservabilityEventTypes.RollbackCompleted => "rolled-back",
                ContentObservabilityEventTypes.PromotionCompleted => "healthy",
                _ => State
            };
            if (item.Event.Type == ContentObservabilityEventTypes.RollbackCompleted)
            {
                PriorStableVersion = data.ArchetypeVersion ?? PriorStableVersion;
            }
            ReplayPassed = data.ReplayPassed ?? ReplayPassed;
            Reason = data.Reason ?? Reason;
            if (item.Event.Type == ContentObservabilityEventTypes.ArchetypeUsed)
            {
                if (data.Metrics?.Succeeded == false) FailedUses++;
                else SuccessfulUses++;
            }
            if (item.Event.Type == ContentObservabilityEventTypes.RegressionDetected) RegressionCount++;
            if (item.Event.Type == ContentObservabilityEventTypes.RollbackCompleted) RollbackCount++;
            LastTimestamp = Math.Max(LastTimestamp, item.Event.Timestamp);
        }

        public ContentArchetypeRow ToRow() => new(
            ArchetypeId,
            Version,
            State,
            ContentKind,
            GameplayRole,
            SuccessfulUses,
            FailedUses,
            RegressionCount,
            RollbackCount,
            PriorStableVersion,
            ReplayPassed,
            Reason,
            LastTimestamp);
    }
}
