using System.Text;
using System.Text.RegularExpressions;

namespace RimLiaison.Observability;

public sealed record AgentObservabilityProbableOwner(
    string Owner,
    string Confidence,
    string Reason);

public sealed record AgentObservabilityIssueSignature(
    string? ErrorCode,
    string? Component,
    string Fingerprint,
    bool IsStrong,
    string? Tool,
    string? Command);

public sealed record AgentObservabilitySharedToolingHint(
    string FailureCode,
    string Component,
    int AffectedAgentCount,
    IReadOnlyList<string> AffectedModIds,
    int AffectedSessionCount = 0)
{
    public int OtherAffectedAgentCount => Math.Max(0, AffectedAgentCount - 1);
}

public sealed record AgentObservabilityIssueTriage
{
    public required string WhatFailed { get; init; }
    public required string AttemptedOperation { get; init; }
    public required string Stage { get; init; }
    public required bool IsBlocked { get; init; }
    public required string ImmediatelyBefore { get; init; }
    public required bool Retried { get; init; }
    public required bool Recovered { get; init; }
    public required string ResolutionState { get; init; }
    public string? Command { get; init; }
    public string? FailureEventId { get; init; }
    public string? ErrorCode { get; init; }
    public string? OuterErrorCode { get; init; }
    public string? UnderlyingErrorCode { get; init; }
    public string? CapabilityId { get; init; }
    public required string ToolOrComponent { get; init; }
    public required bool EvidenceComplete { get; init; }
    public required IReadOnlyList<string> MissingEvidence { get; init; }
    public required AgentObservabilityProbableOwner ProbableOwner { get; init; }
    public AgentObservabilitySharedToolingHint? SharedTooling { get; init; }
    public int RetryCount { get; init; }
    public string? LastSuccessfulOperation { get; init; }
    public IReadOnlyList<string> TransactionIds { get; init; } = [];
    public IReadOnlyList<string> WorkflowIds { get; init; } = [];
    public required string SessionKind { get; init; }
    public string? LogicalAgentId { get; init; }
    public required string RunId { get; init; }
    public required string AgentId { get; init; }
    public required string ModName { get; init; }
    public string? FailureFingerprint { get; init; }
}

public static class AgentObservabilityIssueTriageBuilder
{
    private static readonly Regex CompilerCode = new(
        @"\b(?:CS|MSB)\d{3,5}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex StructuredCode = new(
        @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static AgentObservabilityIssueSignature Describe(
        AgentIssue issue,
        IReadOnlyList<AgentEvent> events)
    {
        string? underlyingErrorCode = FirstValue(events, ["underlyingErrorCode"]);
        string? errorCode = underlyingErrorCode ?? FirstValue(
            events,
            ["errorCode", "failureCode", "diagnosticCode", "code"]);
        errorCode ??= CompilerCode.Match(string.Join(" ", events.Select(value => value.Summary))).Value;
        errorCode = NormalizeCode(errorCode);

        string? tool = FirstValue(events, ["toolName", "tool", "component", "service"]);
        string? command = FirstValue(events, ["command", "commandText"]);
        string component = IdentifyComponent(errorCode, tool, command, events);
        if (issue.Category == AgentIssueCategory.CapabilityGap)
        {
            string? capabilityId = issue.CapabilityId ??
                FirstValue(events, ["requiredCapabilityId", "capabilityId"]);
            string? provider = FirstValue(events, ["expectedProvider", "discoveredProvider"]);
            string fingerprint = "capability|" + (capabilityId ?? "unknown") +
                "|provider|" + (provider ?? "any");
            return new AgentObservabilityIssueSignature(
                errorCode,
                component == "Unknown" ? "Validation capability registry" : component,
                fingerprint,
                IsStrong: true,
                AgentObservabilityData.BoundIdentifier(tool, 128),
                AgentObservabilityData.SanitizeCommand(command, 512));
        }

        bool strong = !string.IsNullOrWhiteSpace(errorCode) && IsToolingComponent(component);
        string normalFingerprint = string.IsNullOrWhiteSpace(errorCode)
            ? string.Empty
            : component + "|" + errorCode;
        return new AgentObservabilityIssueSignature(
            errorCode,
            component == "Unknown" ? null : component,
            normalFingerprint,
            strong,
            AgentObservabilityData.BoundIdentifier(tool, 128),
            AgentObservabilityData.SanitizeCommand(command, 512));
    }

    public static AgentObservabilityProbableOwner Classify(
        AgentIssue issue,
        IReadOnlyList<AgentEvent> events,
        AgentObservabilityIssueSignature signature)
    {
        string code = signature.ErrorCode ?? string.Empty;
        string component = signature.Component ?? "Unknown";
        if (issue.Category == AgentIssueCategory.CapabilityGap)
        {
            string owner = issue.ProbableOwner ??
                FirstValue(events, ["probableOwner", "expectedProvider"]) ??
                "validation capability provider";
            return new(
                owner,
                "high",
                "the validation capability registry identified a missing or incompatible declared capability.");
        }
        if (component == "DevBridge2" ||
            code.StartsWith("DEVBRIDGE_", StringComparison.Ordinal) ||
            code.StartsWith("READINESS_", StringComparison.Ordinal) ||
            code.StartsWith("LEASE_", StringComparison.Ordinal) ||
            code.StartsWith("GENERATION_", StringComparison.Ordinal) ||
            code.StartsWith("DEPLOYMENT_", StringComparison.Ordinal) ||
            code.StartsWith("ARTIFACT_", StringComparison.Ordinal))
        {
            return new(
                "DevBridge2",
                string.IsNullOrWhiteSpace(signature.ErrorCode) ? "medium" : "high",
                "readiness, lease, generation, deployment, or artifact evidence identifies the lifecycle owner.");
        }

        if (component is "RimTest" or "RimContext" or "RimError" or "RimLiaison")
        {
            return new(
                component,
                signature.ErrorCode is null ? "medium" : "high",
                "structured tooling identity and failure evidence identify the component boundary.");
        }

        if (component == "Git / GitHub")
        {
            return new(
                component,
                signature.ErrorCode is null ? "medium" : "high",
                "the failure is attached to a Git/GitHub command or structured repository result.");
        }

        bool compilerFailure = CompilerCode.IsMatch(code) ||
            events.Any(value =>
                value.Type.StartsWith("build", StringComparison.OrdinalIgnoreCase) &&
                (value.Summary.Contains("compiler", StringComparison.OrdinalIgnoreCase) ||
                 value.Summary.Contains("CS", StringComparison.Ordinal)));
        if (compilerFailure &&
            events.Any(value => value.Data is not null) &&
            (issue.RelatedFiles?.Count > 0 || issue.RelatedCommands?.Count > 0 ||
             events.Any(value => value.Type.StartsWith("build", StringComparison.OrdinalIgnoreCase))))
        {
            return new(
                "Mod / project",
                "high",
                "compiler/build evidence identifies the affected project and source operation.");
        }

        if (component == "Environment / machine")
        {
            return new(
                component,
                signature.ErrorCode is null ? "low" : "medium",
                "machine, runtime, or process evidence is present without a stronger project/tool boundary.");
        }

        if (issue.Category is AgentIssueCategory.Stall or AgentIssueCategory.RedundantWork)
        {
            return new(
                "Agent behavior",
                "medium",
                "the issue category describes stalled or repeated agent work rather than an external tool failure.");
        }

        return new(
            "Unknown",
            "low",
            "available evidence does not identify a responsible component conservatively.");
    }

    public static AgentObservabilityIssueTriage Build(
        AgentIssue issue,
        AgentSnapshot? agent,
        IReadOnlyList<AgentEvent> supportingEvents,
        AgentDiagnosticBundle bundle,
        bool currentSession,
        AgentObservabilitySharedToolingHint? sharedTooling,
        AgentObservabilityIssueSignature? signature = null)
    {
        AgentEvent? failure = supportingEvents
            .Where(IsFailureEvent)
            .OrderByDescending(static value => value.Sequence)
            .FirstOrDefault() ?? supportingEvents.LastOrDefault();
        int failureIndex = failure is null
            ? supportingEvents.Count
            : supportingEvents
                .Select((value, index) => (value, index))
                .First(pair => string.Equals(pair.value.Id, failure.Id, StringComparison.Ordinal))
                .index;
        AgentEvent? immediatelyBefore = failureIndex > 0
            ? supportingEvents[failureIndex - 1]
            : null;
        signature ??= Describe(issue, supportingEvents);
        AgentObservabilityProbableOwner owner = Classify(issue, supportingEvents, signature);
        string? outerErrorCode = FirstValue(supportingEvents, ["outerErrorCode"]);
        string? underlyingErrorCode = FirstValue(
            supportingEvents,
            ["underlyingErrorCode"]);
        string? operation = issue.OperationKey ??
            FirstValue(supportingEvents, ["operationKey", "operation", "command", "commandText"]);
        string? tool = signature.Component ?? signature.Tool ??
            FirstValue(supportingEvents, ["toolName", "tool", "component"]);
        int retryCount = issue.RetryCount + supportingEvents.Count(value =>
            value.Type is AgentEventTypes.RetryStarted or AgentEventTypes.RetryCompleted);
        AgentEvent? lastSuccess = supportingEvents
            .Where(value => failure is null || value.Sequence < failure.Sequence)
            .Where(IsSuccessfulEvent)
            .OrderByDescending(static value => value.Sequence)
            .FirstOrDefault();
        string? lastSuccessfulOperation = lastSuccess is null
            ? null
            : AgentObservabilityData.GetString(lastSuccess.Data, "operationKey") ??
              AgentObservabilityData.GetString(lastSuccess.Data, "operation") ??
              lastSuccess.Summary;
        bool optionalUnavailable =
            issue.Category == AgentIssueCategory.OptionalValidationUnavailable;
        bool nonBlocking = optionalUnavailable ||
            issue.Category is AgentIssueCategory.ToolingImprovement or
                AgentIssueCategory.InformationalProductionEvent or
                AgentIssueCategory.ToolLimitation;
        bool blocked = issue.Blocking ||
            (!nonBlocking &&
                !issue.Recovered &&
                agent is not null &&
                agent.Status != AgentStatus.Completed);
        string? capabilityId = issue.CapabilityId ??
            FirstValue(supportingEvents, ["requiredCapabilityId", "capabilityId"]);
        return new AgentObservabilityIssueTriage
        {
            WhatFailed = issue.Category == AgentIssueCategory.CapabilityGap
                ? AgentObservabilityData.BoundText(
                    "Validation blocked: required capability " + (capabilityId ?? "unknown") +
                    " is unavailable. No product failure was observed.",
                    512)
                : optionalUnavailable
                    ? AgentObservabilityData.BoundText(
                        "Optional validation unavailable: " + (capabilityId ?? "unknown") +
                        ". No product failure was observed.",
                        512)
                    : AgentObservabilityData.BoundText(issue.Summary, 512),
            AttemptedOperation = issue.Category is AgentIssueCategory.CapabilityGap or
                    AgentIssueCategory.OptionalValidationUnavailable
                ? "Validation capability preflight (recipe execution not attempted)"
                : AgentObservabilityData.BoundText(operation, 512) is { Length: > 0 } value
                    ? value
                    : "No operation was recorded.",
            Stage = issue.Stage?.ToString() ?? failure?.Stage.ToString() ?? "Unknown",
            IsBlocked = blocked,
            ImmediatelyBefore = immediatelyBefore is null
                ? "No preceding event was recorded."
                : AgentObservabilityData.BoundText(immediatelyBefore.Summary, 512),
            Retried = retryCount > 0,
            Recovered = issue.Recovered,
            ResolutionState = issue.Recovered ? "recovered" : "unresolved",
            ToolOrComponent = AgentObservabilityData.BoundText(tool, 256) is { Length: > 0 } toolValue
                ? toolValue
                : "No tool/component was recorded.",
            EvidenceComplete = bundle.Completeness.IsComplete,
            MissingEvidence = bundle.Completeness.MissingEvidence,
            ProbableOwner = owner,
            SharedTooling = sharedTooling,
            ErrorCode = signature.ErrorCode,
            CapabilityId = capabilityId,
            OuterErrorCode = outerErrorCode,
            UnderlyingErrorCode = underlyingErrorCode,
            FailureEventId = failure?.Id,
            LastSuccessfulOperation = AgentObservabilityData.BoundText(lastSuccessfulOperation, 512) is { Length: > 0 } successValue
                ? successValue
                : null,
            TransactionIds = bundle.Correlations
                .Where(value => value.Kind == "transaction")
                .Select(value => value.Value)
                .Take(8)
                .ToArray(),
            WorkflowIds = bundle.Correlations
                .Where(value => value.Kind == "workflow")
                .Select(value => value.Value)
                .ToArray(),
            SessionKind = currentSession ? "current/latest" : "historical",
            RunId = issue.RunId,
            AgentId = issue.AgentId,
            LogicalAgentId = issue.LogicalAgentId,
            ModName = agent?.ModName ?? issue.ModId,
            FailureFingerprint = string.IsNullOrWhiteSpace(signature.Fingerprint)
                ? null
                : signature.Fingerprint
        };
    }

    public static string FormatChatPacket(
        AgentObservabilityIssueTriage triage,
        AgentIssue issue,
        AgentDiagnosticBundle bundle)
    {
        var builder = new StringBuilder();
        string? packetCommand = triage.Command ??
            bundle.CommandEvidence
                .Select(static value => value.Command)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        builder.AppendLine("RimLiaison Observability diagnostic handoff");
        builder.AppendLine("Please assess whether this is a tooling/infrastructure issue.");
        builder.AppendLine($"Agent/mod: {triage.ModName}");
        builder.AppendLine($"Session: {triage.SessionKind}; logicalAgent={triage.LogicalAgentId ?? "legacy/session-scoped"}; run={triage.RunId}; agent={triage.AgentId}");
        builder.AppendLine($"Issue: {issue.Id}; state={triage.ResolutionState}; blocked={YesNo(triage.IsBlocked)}");
        builder.AppendLine($"Failure event: {triage.FailureEventId ?? "not recorded"}");
        builder.AppendLine($"Command: {packetCommand ?? "not recorded"}");
        builder.AppendLine($"Failure: {triage.WhatFailed}");
        builder.AppendLine($"Stage: {triage.Stage}");
        builder.AppendLine($"Tool/component: {triage.ToolOrComponent}");
        builder.AppendLine($"Error code: {triage.ErrorCode ?? "not recorded"}");
        if (triage.OuterErrorCode is not null)
        {
            builder.AppendLine($"Outer error code: {triage.OuterErrorCode}");
        }
        if (triage.UnderlyingErrorCode is not null)
        {
            builder.AppendLine($"Underlying error code: {triage.UnderlyingErrorCode}");
        }
        builder.AppendLine($"Immediately before: {triage.ImmediatelyBefore}");
        builder.AppendLine($"Retry: {YesNo(triage.Retried)} ({triage.RetryCount})");
        builder.AppendLine($"Recovery: {(triage.Recovered ? "recovered" : "not recovered")}");
        builder.AppendLine($"Probable owner: {triage.ProbableOwner.Owner} — {triage.ProbableOwner.Confidence}");
        if (issue.Category == AgentIssueCategory.CapabilityGap)
        {
            builder.AppendLine("Classification: BLOCKED / CAPABILITY GAP");
            builder.AppendLine($"Required capability: {triage.CapabilityId ?? "not recorded"}");
            builder.AppendLine("Validation operation attempted: no");
            builder.AppendLine("This is not negative evidence about the mod.");
        }
        builder.AppendLine($"Reason: {triage.ProbableOwner.Reason}");
        builder.AppendLine($"Last successful operation: {triage.LastSuccessfulOperation ?? "not recorded"}");
        if (triage.SharedTooling is not null)
        {
            builder.AppendLine($"Shared tooling: {triage.SharedTooling.AffectedAgentCount} logical agents affected by {triage.SharedTooling.FailureCode}");
            if (triage.SharedTooling.AffectedSessionCount > triage.SharedTooling.AffectedAgentCount)
            {
                builder.AppendLine($"Affected sessions: {triage.SharedTooling.AffectedSessionCount}");
            }
            builder.AppendLine($"Shared component: {triage.SharedTooling.Component}");
        }

        if (bundle.Repository is not null)
        {
            builder.AppendLine($"Repository: {bundle.Repository.Project ?? bundle.Repository.SourceProject ?? bundle.Repository.RepositoryRoot ?? "not recorded"}");
            builder.AppendLine($"Branch/commit: {bundle.Repository.Branch ?? "not recorded"}/{bundle.Repository.CommitSha ?? "not recorded"}");
        }
        AppendValues(builder, "Transactions", triage.TransactionIds);
        AppendValues(builder, "Workflows", triage.WorkflowIds);
        string[] output = bundle.CommandEvidence
            .SelectMany(value => new[] { value.DiagnosticOutput, value.Stderr, value.Stdout })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => AgentObservabilityData.BoundText(value, 900))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        AppendValues(builder, "Supporting output", output);
        builder.AppendLine($"Evidence: {(triage.EvidenceComplete ? "Complete" : "Incomplete")}");
        if (!triage.EvidenceComplete)
        {
            AppendValues(builder, "Missing evidence", triage.MissingEvidence);
        }

        return AgentObservabilityData.BoundText(builder.ToString(), 8_000);
    }

    public static bool IsToolingComponent(string? component) => component is
        "DevBridge2" or "RimLiaison" or "RimTest" or "RimContext" or "RimError" or
        "Validation capability registry" or
        "Git / GitHub" or "Environment / machine";

    private static string IdentifyComponent(
        string? errorCode,
        string? tool,
        string? command,
        IReadOnlyList<AgentEvent> events)
    {
        string text = string.Join(
            " ",
            new[] { errorCode, tool, command }
                .Concat(events.Select(value => value.Summary))
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (ContainsAny(text, "devbridge", "dev bridge", "rimbridge", "readiness", "lease", "generation", "deployment", "artifact freshness"))
        {
            return "DevBridge2";
        }
        if (ContainsAny(text, "rimtest", "rim test"))
        {
            return "RimTest";
        }
        if (ContainsAny(text, "rimcontext", "rim context"))
        {
            return "RimContext";
        }
        if (ContainsAny(text, "rimerror", "rim error"))
        {
            return "RimError";
        }
        if (ContainsAny(text, "rimliaison", "rim liaison"))
        {
            return "RimLiaison";
        }
        if (ContainsAny(text, "github", "git ", "git/"))
        {
            return "Git / GitHub";
        }
        if (ContainsAny(text, "compiler", "msbuild", "dotnet build", "csproj", "source file"))
        {
            return "Mod / project";
        }
        if (ContainsAny(text, "out of memory", "permission denied", "access denied", "runtime", "machine", "process"))
        {
            return "Environment / machine";
        }
        return "Unknown";
    }

    private static string? FirstValue(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<string> names)
    {
        foreach (AgentEvent eventRecord in events.OrderByDescending(static value => value.Sequence))
        {
            foreach (string name in names)
            {
                string? value = AgentObservabilityData.GetString(eventRecord.Data, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string normalized = value.Trim().ToUpperInvariant();
        if (CompilerCode.IsMatch(normalized))
        {
            return CompilerCode.Match(normalized).Value;
        }
        Match structured = StructuredCode.Match(normalized);
        return structured.Success ? structured.Value : AgentObservabilityData.BoundIdentifier(normalized, 128);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool IsFailureEvent(AgentEvent value) =>
        value.Type.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        value.Type.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        value.Type.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessfulEvent(AgentEvent value) =>
        value.Type.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ||
        value.Type.EndsWith("succeeded", StringComparison.OrdinalIgnoreCase) ||
        value.Type.EndsWith("passed", StringComparison.OrdinalIgnoreCase) ||
        AgentObservabilityData.GetString(value.Data, "outcome") is "success" or "passed";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static void AppendValues(StringBuilder builder, string title, IEnumerable<string> values)
    {
        string[] bounded = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => AgentObservabilityData.BoundText(value, 900))
            .Take(8)
            .ToArray();
        if (bounded.Length == 0)
        {
            return;
        }
        builder.AppendLine(title + ": " + string.Join(" | ", bounded));
    }
}
