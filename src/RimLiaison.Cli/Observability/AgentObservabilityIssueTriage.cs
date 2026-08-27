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
    public string? Orchestrator { get; init; }
    public string? FailureSurface { get; init; }
    public string? OwnershipBasis { get; init; }
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
public sealed record AgentObservabilityChatPacket(
    string Text,
    AgentDiagnosticCompleteness Completeness);



public static class AgentObservabilityIssueTriageBuilder
{
    private static readonly Regex CompilerCode = new(
        @"\b(?:CS|MSB|NU)\d{3,5}\b",
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

        string? tool = FirstValue(events, [
            "causalComponent",
            "toolName",
            "tool",
            "component",
            "service"]);
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

        bool strong = !string.IsNullOrWhiteSpace(errorCode) &&
            IsToolingComponent(component);
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
        bool buildFailure = events.Any(value =>
            value.Type == AgentEventTypes.BuildFailed ||
            (value.Type == AgentEventTypes.BuildDiagnostics &&
                int.TryParse(
                    AgentObservabilityData.GetString(value.Data, "exitCode"),
                    out int exitCode) &&
                exitCode != 0) ||
            (value.Type == AgentEventTypes.FailureDetected &&
                AgentObservabilityData.GetString(value.Data, "failureSurface")
                    ?.Contains("build", StringComparison.OrdinalIgnoreCase) == true));
        if (buildFailure)
        {
            string? causalOwner = FirstValue(events, ["causalComponent", "causalOwner", "likelyOwner"]);
            string? ownershipConfidence = FirstValue(events, ["ownershipConfidence"]);
            string? ownershipBasis = FirstValue(events, ["ownershipBasis"]);
            string causalText = string.Join(
                " ",
                events.SelectMany(value => new[]
                {
                    AgentObservabilityData.GetString(value.Data, "causalDiagnostic"),
                    AgentObservabilityData.GetString(value.Data, "diagnosticSignature")
                }).Where(static value => !string.IsNullOrWhiteSpace(value)));
            if (string.Equals(causalOwner, "DevBridge2", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    "DevBridge2",
                    ownershipConfidence ?? "high",
                    ownershipBasis ?? "the controlled build failed while the equivalent native build passed.");
            }
            if (string.Equals(causalOwner, "project", StringComparison.OrdinalIgnoreCase) ||
                CompilerCode.IsMatch(causalText))
            {
                return new(
                    "Mod / project",
                    ownershipConfidence ?? "high",
                    ownershipBasis ?? "the causal compiler/MSBuild/NuGet diagnostic identifies the project build; DevBridge2 is only the orchestrator.");
            }
            if (string.IsNullOrWhiteSpace(causalText))
            {
                return new(
                    "Unknown",
                    "unproven",
                    "the build exited unsuccessfully, but its causal diagnostic is missing or unavailable; orchestration does not prove ownership.");
            }
            return new(
                "Unknown",
                "unproven",
                "the build diagnostic is present but does not establish whether the project or DevBridge-controlled inputs caused it.");
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
        string? orchestrator = FirstValue(supportingEvents, ["orchestrator"]);
        string? failureSurface = FirstValue(supportingEvents, ["failureSurface"]);
        string? ownershipBasis = FirstValue(supportingEvents, ["ownershipBasis"]);
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
            Orchestrator = AgentObservabilityData.BoundIdentifier(orchestrator, 128),
            FailureSurface = AgentObservabilityData.BoundText(failureSurface, 256),
            OwnershipBasis = AgentObservabilityData.BoundText(ownershipBasis ?? owner.Reason, 1_024),
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

    public const int MaximumChatPacketCharacters = 8_000;
    public const int MaximumChatPacketIssues = 8;

    public static string FormatChatPacket(
        AgentObservabilityIssueTriage triage,
        AgentIssue issue,
        AgentDiagnosticBundle bundle) =>
        FormatChatPacket([(issue, triage, bundle)]).Text;

    public static AgentObservabilityChatPacket FormatChatPacket(
        IReadOnlyList<(AgentIssue Issue, AgentObservabilityIssueTriage Triage, AgentDiagnosticBundle Bundle)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one issue is required.", nameof(items));
        }
        if (items.Count > MaximumChatPacketIssues)
        {
            throw new ArgumentException(
                $"At most {MaximumChatPacketIssues} issues are supported.",
                nameof(items));
        }

        AgentDiagnosticCompleteness completeness = CombineCompleteness(items);
        var builder = new StringBuilder();
        builder.AppendLine("RimLiaison Observability diagnostic handoff");
        builder.AppendLine("Please assess whether this is a tooling/infrastructure issue.");
        builder.AppendLine($"Selected issues: {items.Count}");
        builder.AppendLine($"Evidence completeness: {(completeness.IsComplete ? "Complete" : "Incomplete")}");
        builder.AppendLine($"Handoff budget: {MaximumChatPacketCharacters} characters; output is bounded and redacted.");
        int issueBudget = Math.Max(
            512,
            (MaximumChatPacketCharacters - builder.Length - (items.Count * 2)) / items.Count);
        foreach ((AgentIssue issue, AgentObservabilityIssueTriage triage, AgentDiagnosticBundle bundle) item in items)
        {
            var issueBuilder = new StringBuilder();
            AppendIssue(issueBuilder, item.issue, item.triage, item.bundle);
            builder.AppendLine();
            builder.Append(AgentObservabilityData.BoundText(issueBuilder.ToString(), issueBudget));
        }

        return new AgentObservabilityChatPacket(
            AgentObservabilityData.BoundText(
                builder.ToString(),
                MaximumChatPacketCharacters),
            completeness);
    }

    private static void AppendIssue(
        StringBuilder builder,
        AgentIssue issue,
        AgentObservabilityIssueTriage triage,
        AgentDiagnosticBundle bundle)
    {
        IReadOnlyList<AgentEvent> events = bundle.SupportingEvents;
        AgentEvent? failure = events.FirstOrDefault(value =>
            string.Equals(value.Id, triage.FailureEventId, StringComparison.Ordinal)) ??
            events.Where(IsFailureEvent)
                .OrderByDescending(static value => value.Sequence)
                .FirstOrDefault();
        AgentEvent? primary = FindEventWithCode(
            events,
            triage.UnderlyingErrorCode ?? triage.ErrorCode);
        AgentDiagnosticCommandEvidence? commandEvidence =
            bundle.CommandEvidence.FirstOrDefault(value =>
                string.Equals(value.EventId, triage.FailureEventId, StringComparison.Ordinal)) ??
            bundle.CommandEvidence.FirstOrDefault();
        string? packetCommand = triage.Command ??
            commandEvidence?.Command ??
            bundle.Commands.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        AgentDiagnosticBuildEvidence? causalBuild = bundle.BuildEvidence
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value.CausalDiagnostic) ||
                !string.IsNullOrWhiteSpace(value.DiagnosticSignature));
        builder.AppendLine($"Causal/root diagnostic: {AgentObservabilityData.BoundText(causalBuild?.CausalDiagnostic, 1_200)}");
        builder.AppendLine($"Ownership conclusion: {triage.ProbableOwner.Owner} — {triage.ProbableOwner.Confidence}");
        builder.AppendLine($"Ownership basis: {triage.OwnershipBasis ?? triage.ProbableOwner.Reason}");
        builder.AppendLine($"Orchestrator: {triage.Orchestrator ?? "not recorded"}; failure surface: {triage.FailureSurface ?? "not recorded"}");

        builder.AppendLine($"## Issue {issue.Id}");
        builder.AppendLine($"Issue: {issue.Id}; state={triage.ResolutionState}; blocked={YesNo(triage.IsBlocked)}");
        builder.AppendLine($"Agent/mod: {triage.ModName} ({issue.ModId}); entity={issue.EntityType}/{issue.CanonicalEntityId}");
        builder.AppendLine($"Date/time: {AgentObservabilityTime.FormatLocal(issue.Timestamp)}; session={triage.SessionKind}; run={triage.RunId}; agent={triage.AgentId}");
        builder.AppendLine($"State: {triage.ResolutionState}; severity={issue.Severity}; blocked={YesNo(triage.IsBlocked)}; category={issue.Category}");
        builder.AppendLine($"Failure event: {triage.FailureEventId ?? "not recorded"}");
        builder.AppendLine($"Failure: {triage.WhatFailed}");
        builder.AppendLine($"Stage: {triage.Stage}; attempted operation: {triage.AttemptedOperation}");
        builder.AppendLine($"Tool/component: {triage.ToolOrComponent}");
        builder.AppendLine($"Primary/root failure: code={triage.UnderlyingErrorCode ?? triage.ErrorCode ?? "not recorded"}; " +
            $"message={EventMessage(primary) ?? triage.WhatFailed}; owner={triage.ProbableOwner.Owner}");
        builder.AppendLine($"Propagation: outerCode={triage.OuterErrorCode ?? "none"}; " +
            $"surfaceCode={triage.ErrorCode ?? "not recorded"}; failureEvent={failure?.Type ?? "not recorded"}");
        builder.AppendLine($"Top-level workflow: operation={packetCommand ?? triage.AttemptedOperation}; " +
            $"state={triage.ResolutionState}; blocked={YesNo(triage.IsBlocked)}");
        builder.AppendLine($"Error code: {triage.ErrorCode ?? "not recorded"}");
        if (triage.OuterErrorCode is not null)
        {
            builder.AppendLine($"Outer error code: {triage.OuterErrorCode}");
        }
        if (triage.UnderlyingErrorCode is not null)
        {
            builder.AppendLine($"Underlying error code: {triage.UnderlyingErrorCode}");
        }
        builder.AppendLine($"Command: {packetCommand ?? "not recorded"}");
        builder.AppendLine($"Immediately before: {triage.ImmediatelyBefore}");
        builder.AppendLine($"Retry: {YesNo(triage.Retried)} ({triage.RetryCount}); fingerprint={triage.FailureFingerprint ?? "not recorded"}");
        builder.AppendLine($"Recovery: {(triage.Recovered ? "recovered" : "not recovered")}");
        builder.AppendLine($"Probable owner: {triage.ProbableOwner.Owner} — {triage.ProbableOwner.Confidence}");
        builder.AppendLine($"Reason: {triage.ProbableOwner.Reason}");
        builder.AppendLine($"Last successful operation: {triage.LastSuccessfulOperation ?? "not recorded"}");
        if (issue.Recommendation is not null)
        {
            builder.AppendLine($"Recommended next action: {issue.Recommendation}");
        }
        if (issue.Category == AgentIssueCategory.CapabilityGap)
        {
            builder.AppendLine("Classification: BLOCKED / CAPABILITY GAP");
            builder.AppendLine($"Required capability: {triage.CapabilityId ?? "not recorded"}");
            builder.AppendLine("Validation operation attempted: no");
            builder.AppendLine("This is not negative evidence about the mod.");
        }

        AppendSharedImpact(builder, issue, triage.SharedTooling);
        AppendProcessEvidence(builder, events, commandEvidence);
        AppendRepositoryAndEnvironment(builder, bundle);
        AppendValues(builder, "Transactions", triage.TransactionIds);
        AppendValues(builder, "Workflows", triage.WorkflowIds);
        AppendEvidenceReferences(builder, issue, events);
        AppendCommandOutput(builder, commandEvidence, events);
        AppendBuildEvidence(builder, bundle);
        if (bundle.RecoveryPath.Count > 0)
        {
            AppendValues(
                builder,
                "Recovery path",
                bundle.RecoveryPath.Select(value => value.Type + "/" + value.EventId + ": " + value.Summary));
        }

        builder.AppendLine($"Evidence: {(triage.EvidenceComplete ? "Complete" : "Incomplete")}");
        if (!triage.EvidenceComplete)
        {
            AppendValues(builder, "Missing evidence", triage.MissingEvidence);
        }
    }

    private static AgentDiagnosticCompleteness CombineCompleteness(
        IReadOnlyList<(AgentIssue Issue, AgentObservabilityIssueTriage Triage, AgentDiagnosticBundle Bundle)> items)
    {
        string[] missing = items
            .SelectMany(static item => item.Bundle.Completeness.MissingEvidence)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
        return new(
            missing.Length == 0
                ? AgentDiagnosticCompletenessStatuses.Complete
                : AgentDiagnosticCompletenessStatuses.Incomplete,
            missing);
    }

    private static void AppendSharedImpact(
        StringBuilder builder,
        AgentIssue issue,
        AgentObservabilitySharedToolingHint? shared)
    {
        builder.AppendLine($"Affected occurrences: {Math.Max(1, issue.Occurrences)}");
        if (shared is null)
        {
            builder.AppendLine("Affected sessions: 1 selected; cross-session impact not established");
            builder.AppendLine("Distinct durable logical agents: unknown; no trustworthy shared-impact identity evidence");
            return;
        }

        builder.AppendLine($"Affected sessions: {Math.Max(1, shared.AffectedSessionCount)}");
        if (shared.AffectedAgentCount > 0)
        {
            builder.AppendLine($"Distinct durable logical agents: {shared.AffectedAgentCount}");
            builder.AppendLine("Identity quality: durable logical-agent identities");
        }
        else
        {
            builder.AppendLine("Distinct durable logical agents: unknown; identities are legacy/session-scoped");
        }
        builder.AppendLine($"Shared impact basis: {shared.Component}/{shared.FailureCode}");
        AppendValues(builder, "Affected mods", shared.AffectedModIds);
    }

    private static void AppendProcessEvidence(
        StringBuilder builder,
        IReadOnlyList<AgentEvent> events,
        AgentDiagnosticCommandEvidence? command)
    {
        string? executable = FirstNestedValue(events, ["resolvedExecutablePath"]);
        string? toolRoot = FirstNestedValue(events, ["resolvedToolRoot", "toolRoot"]);
        string? workingDirectory = command?.WorkingDirectory ??
            FirstNestedValue(events, ["workingDirectory", "cwd"]);
        builder.AppendLine($"Process: resolvedExecutablePath={executable ?? "not recorded"}; " +
            $"resolvedToolRoot={toolRoot ?? "not recorded"}; " +
            $"workingDirectory={workingDirectory ?? "not recorded"}");

        string? dirty = FirstNestedValue(events, ["worktreeDirty", "repositoryDirty", "dirty"]);
        builder.AppendLine($"Worktree/build state: dirty={dirty ?? "not recorded"}");
    }

    private static void AppendRepositoryAndEnvironment(
        StringBuilder builder,
        AgentDiagnosticBundle bundle)
    {
        if (bundle.Repository is not null)
        {
            builder.AppendLine($"Repository: root={bundle.Repository.RepositoryRoot ?? "not recorded"}; " +
                $"project={bundle.Repository.Project ?? bundle.Repository.SourceProject ?? "not recorded"}; " +
                $"configuration={bundle.Repository.Configuration ?? "not recorded"}");
            builder.AppendLine($"Branch/commit: {bundle.Repository.Branch ?? "not recorded"}/{bundle.Repository.CommitSha ?? "not recorded"}");
            AppendValues(builder, "Changed files", bundle.Repository.ChangedFiles);
        }

        if (bundle.Environment is not null)
        {
            AppendValues(
                builder,
                "Environment",
                bundle.Environment.Values.Select(value => value.Key + "=" + value.Value));
            AppendValues(
                builder,
                "Tool versions",
                bundle.Environment.ToolVersions.Select(value => value.Key + "=" + value.Value));
        }
    }

    private static void AppendEvidenceReferences(
        StringBuilder builder,
        AgentIssue issue,
        IReadOnlyList<AgentEvent> events)
    {
        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(issue.EvidenceReference))
        {
            references.Add(issue.EvidenceReference);
        }
        references.AddRange(
            events.SelectMany(value => new[]
                {
                    FirstNestedValue(value, ["stdoutEvidenceId", "rawStdoutEvidenceId"]),
                    FirstNestedValue(value, ["stderrEvidenceId", "rawStderrEvidenceId"]),
                    FirstNestedValue(value, ["diagnosticEvidenceId", "causalDiagnosticEvidenceId"]),
                    FirstNestedValue(value, ["outputEvidenceId"])
                })
                .Where(static value => value is not null)
                .Select(static value => value!));
        AppendValues(builder, "Evidence references", references.Distinct(StringComparer.Ordinal));
    }

    private static void AppendCommandOutput(
        StringBuilder builder,
        AgentDiagnosticCommandEvidence? command,
        IReadOnlyList<AgentEvent> events)
    {
        if (command is null)
        {
            builder.AppendLine($"Command evidence: not recorded; exitCode={FirstNestedValue(events, ["exitCode"]) ?? "not recorded"}; " +
                $"timeout={FirstNestedValue(events, ["timedOut", "timeout"]) ?? "not recorded"}; " +
                $"cancelled={FirstNestedValue(events, ["cancelled", "canceled"]) ?? "not recorded"}");
            return;
        }

        builder.AppendLine($"Command evidence: event={command.EventId}; tool={command.Tool ?? "not recorded"}; " +
            $"exitCode={command.ExitCode?.ToString() ?? "not recorded"}; timeout={YesNo(command.TimedOut)}; " +
            $"cancelled={YesNo(command.Cancelled)}");
        if (command.StdoutTruncated || command.StderrTruncated || command.DiagnosticOutputTruncated)
        {
            builder.AppendLine($"Output truncation: stdout={YesNo(command.StdoutTruncated)}; " +
                $"stderr={YesNo(command.StderrTruncated)}; diagnostic={YesNo(command.DiagnosticOutputTruncated)}");
        }
        AppendLabeledOutput(builder, "stdout", command.Stdout);
        AppendLabeledOutput(builder, "stderr", command.Stderr);
        AppendLabeledOutput(builder, "diagnostic", command.DiagnosticOutput);
    }

    private static void AppendBuildEvidence(
        StringBuilder builder,
        AgentDiagnosticBundle bundle)
    {
        foreach (AgentDiagnosticBuildEvidence build in bundle.BuildEvidence.Take(2))
        {
            builder.AppendLine($"Build evidence: event={build.EventId}; project={build.Project ?? build.SourceProject ?? "not recorded"}; " +
                $"configuration={build.Configuration ?? "not recorded"}; exitCode={build.ExitCode?.ToString() ?? "not recorded"}; " +
                $"timeout={YesNo(build.TimedOut)}; cancelled={YesNo(build.Cancelled)}; errorCode={build.ErrorCode ?? "not recorded"}");
            AppendLabeledOutput(builder, "compiler output", build.Output);
            AppendLabeledOutput(builder, "compiler errors", build.ErrorOutput);
            if (!string.Equals(build.DiagnosticOutput, build.CausalDiagnostic, StringComparison.Ordinal))
            {
                AppendLabeledOutput(builder, "build diagnostic", build.DiagnosticOutput);
            }
            if (build.OutputTruncated || build.ErrorOutputTruncated || build.DiagnosticOutputTruncated)
            {
                builder.AppendLine($"Build output truncation: output={YesNo(build.OutputTruncated)}; " +
                    $"errors={YesNo(build.ErrorOutputTruncated)}; diagnostic={YesNo(build.DiagnosticOutputTruncated)}");
            }
        }
    }

    private static void AppendLabeledOutput(
        StringBuilder builder,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine($"{label}: {AgentObservabilityData.BoundText(value, 700)}");
        }
    }

    private static AgentEvent? FindEventWithCode(
        IReadOnlyList<AgentEvent> events,
        string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        return events
            .Where(value => string.Equals(
                AgentObservabilityData.GetString(value.Data, "underlyingErrorCode") ??
                    AgentObservabilityData.GetString(value.Data, "errorCode") ??
                    AgentObservabilityData.GetString(value.Data, "failureCode"),
                code,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static value => value.Sequence)
            .FirstOrDefault();
    }

    private static string? EventMessage(AgentEvent? eventRecord) =>
        eventRecord is null
            ? null
            : FirstNestedValue(
                [eventRecord],
                ["underlyingError", "error", "failureMessage", "message"]) ??
              AgentObservabilityData.BoundText(eventRecord.Summary, 700);

    private static string? FirstNestedValue(
        IReadOnlyList<AgentEvent> events,
        IReadOnlyList<string> names)
    {
        foreach (AgentEvent eventRecord in events.OrderByDescending(static value => value.Sequence))
        {
            string? value = FirstNestedValue(eventRecord, names);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? FirstNestedValue(
        AgentEvent eventRecord,
        IReadOnlyList<string> names)
    {
        if (eventRecord.Data is not { ValueKind: System.Text.Json.JsonValueKind.Object } data)
        {
            return null;
        }
        foreach (string name in names)
        {
            if (data.TryGetProperty(name, out System.Text.Json.JsonElement value) &&
                JsonValueText(value) is { } direct)
            {
                return direct;
            }
        }
        if (!data.TryGetProperty("processEvidence", out System.Text.Json.JsonElement processEvidence) ||
            processEvidence.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }
        foreach (string name in names)
        {
            if (processEvidence.TryGetProperty(name, out System.Text.Json.JsonElement value) &&
                JsonValueText(value) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static string? JsonValueText(System.Text.Json.JsonElement value) =>
        value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String =>
                AgentObservabilityData.BoundText(value.GetString(), 700),
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or
                System.Text.Json.JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

    private static string IdentityQuality(string? logicalAgentId) =>
        string.IsNullOrWhiteSpace(logicalAgentId) ||
        logicalAgentId.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase) ||
        logicalAgentId.StartsWith("agent-", StringComparison.OrdinalIgnoreCase) ||
        logicalAgentId.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
            ? "legacy/session-scoped"
            : "durable";

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
