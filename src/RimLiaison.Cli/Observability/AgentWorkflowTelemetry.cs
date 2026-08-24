using System.Text.Json;

namespace RimLiaison.Observability;

/// <summary>
/// Compact, derived metrics for one real RimLiaison workflow. The source is
/// the canonical bounded event stream; no console or source content is kept.
/// </summary>
public sealed record AgentWorkflowTelemetrySummary(
    string? Repository,
    string? Operation,
    int BuildCount,
    long? BuildDurationMs,
    int DeploymentCount,
    long? DeploymentDurationMs,
    int SelectedTestCount,
    int ExecutedTestCount,
    int ReusedEvidenceCount,
    int InvalidatedEvidenceCount,
    int RuntimeLaunchCount,
    int InfrastructureRetryCount,
    int ExpensiveOperationCount,
    string? PublicationAction,
    string? FailureClassification)
{
    public static AgentWorkflowTelemetrySummary FromEvents(
        IReadOnlyList<AgentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        AgentEvent? started = events.FirstOrDefault(
            static value => value.Type == AgentEventTypes.CommandStarted);
        AgentEvent? publication = events.LastOrDefault(
            static value => value.Type == AgentEventTypes.PublicationChecked);
        int buildCount = events.Count(static value =>
            value.Type is AgentEventTypes.BuildSucceeded or AgentEventTypes.BuildFailed);
        int deploymentCount = events.Count(static value =>
            value.Type == AgentEventTypes.SuiteCompleted &&
            Object(value, "artifactFreshness").ValueKind == JsonValueKind.Object);
        int selectedTests = events
            .Where(static value => value.Type == AgentEventTypes.SuiteCompleted)
            .Sum(static value => ArrayLength(value, "selectedTests"));
        int executedTests = events
            .Where(static value => value.Type == AgentEventTypes.SuiteCompleted)
            .Sum(static value => ArrayLength(value, "executedTests"));
        int reusedEvidence = events.Count(static value =>
                value.Type == AgentEventTypes.ValidationEvidenceDecision &&
                String(value, "action") == "reuse") +
            events.Count(static value =>
                value.Type == AgentEventTypes.PublicationChecked &&
                String(value, "publicationAction") == "reuse");
        int invalidatedEvidence = events.Count(static value =>
            value.Type == AgentEventTypes.ValidationEvidenceDecision &&
            String(value, "action") == "invalidate");
        int launches = checked((int)events.Sum(
            static value => Number(value, "launchesConsumed") ?? 0L));
        int retries = events.Count(static value =>
            value.Type == AgentEventTypes.RetryStarted);
        int expensive = buildCount + deploymentCount + events.Count(static value =>
            value.Type is AgentEventTypes.SuiteCompleted or AgentEventTypes.TestStarted);
        string? failure = events
            .Where(static value => value.Type is AgentEventTypes.CommandFailed or
                AgentEventTypes.BuildFailed or
                AgentEventTypes.TestFailed or
                AgentEventTypes.IntegrationFailed or
                AgentEventTypes.SuiteCompleted)
            .Select(static value => String(value, "failureKind", "errorCode", "code") ??
                (value.Type == AgentEventTypes.SuiteCompleted
                    ? null
                    : value.Type))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        return new(
            events.FirstOrDefault()?.ModId,
            String(started, "command"),
            buildCount,
            SumOperationDuration(events, "build:"),
            deploymentCount,
            SumOperationDuration(events, "deploy:"),
            selectedTests,
            executedTests,
            reusedEvidence,
            invalidatedEvidence,
            launches,
            retries,
            expensive,
            String(publication, "publicationAction"),
            failure);
    }

    private static long? SumOperationDuration(
        IReadOnlyList<AgentEvent> events,
        string operationPrefix)
    {
        long total = 0;
        bool found = false;
        foreach (AgentEvent value in events)
        {
            if (value.Type is not (AgentEventTypes.ToolCompleted or
                AgentEventTypes.ToolFailed or AgentEventTypes.ToolException) ||
                String(value, "operationKey") is not string operationKey ||
                !operationKey.StartsWith(operationPrefix, StringComparison.Ordinal) ||
                !Number(value, "durationMs").HasValue)
            {
                continue;
            }

            total += Number(value, "durationMs")!.Value;
            found = true;
        }

        return found ? total : null;
    }

    private static JsonElement Object(AgentEvent value, string name) =>
        value.Data is JsonElement data &&
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement result)
            ? result
            : default;

    private static int ArrayLength(AgentEvent value, string name)
    {
        JsonElement element = Object(value, name);
        return element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() : 0;
    }

    private static long? Number(AgentEvent value, string name) =>
        value.Data is JsonElement data &&
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement result) &&
        result.ValueKind == JsonValueKind.Number &&
        result.TryGetInt64(out long number)
            ? number
            : null;

    private static string? String(AgentEvent? value, params string[] names)
    {
        if (value?.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (data.TryGetProperty(name, out JsonElement result) &&
                result.ValueKind == JsonValueKind.String)
            {
                string? text = result.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}
