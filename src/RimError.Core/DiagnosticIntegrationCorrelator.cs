namespace RimError.Core;

/// <summary>
/// Associates diagnostics with bridge operations only when identity and
/// bounded evidence agree. Time proximity by itself is never a correlation.
/// </summary>
public static class DiagnosticIntegrationCorrelator
{
    private static readonly TimeSpan OperationAfterglow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationEarlyTolerance = TimeSpan.FromMilliseconds(250);

    public static DiagnosticStoreSnapshot Apply(
        DiagnosticStoreSnapshot snapshot,
        DiagnosticIntegrationState? integration = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        integration ??= snapshot.Integration;
        if (integration is null)
        {
            return snapshot;
        }

        var items = snapshot.Items
            .Select(diagnostic => Correlate(diagnostic, integration))
            .ToArray();
        return snapshot with
        {
            Integration = integration,
            Items = items
        };
    }

    private static DiagnosticRecord Correlate(
        DiagnosticRecord diagnostic,
        DiagnosticIntegrationState integration)
    {
        var contextRunId = integration.DevBridge?.RunId ?? integration.RimBridge?.RunId;
        var contextWorkflowId = integration.DevBridge?.WorkflowId ?? integration.RimBridge?.WorkflowId;
        var contextTestId = integration.DevBridge?.TestId ?? integration.DevBridge?.LeaseId;
        var baseRecord = diagnostic with
        {
            RunId = diagnostic.RunId ?? contextRunId,
            WorkflowId = diagnostic.WorkflowId ?? contextWorkflowId,
            TestId = diagnostic.TestId ?? contextTestId
        };

        if (integration.Operations is not { Length: > 0 })
        {
            return baseRecord;
        }

        if (HasContextConflict(integration) ||
            ConflictsWithIntegrationRun(baseRecord, integration) ||
            ConflictsWithIntegrationWorkflow(baseRecord, integration))
        {
            return baseRecord;
        }

        var candidates = integration.Operations
            .Select(operation => Score(baseRecord, operation, integration))
            .Where(candidate => candidate is not null)
            .Cast<ScoredOperation>()
            .OrderByDescending(candidate => ConfidenceRank(candidate.Confidence))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return baseRecord;
        }

        var top = candidates[0];
        var tied = candidates
            .Where(candidate =>
                ConfidenceRank(candidate.Confidence) == ConfidenceRank(top.Confidence) &&
                candidate.Score == top.Score)
            .ToArray();
        var candidateIds = candidates
            .Select(candidate => candidate.DisplayId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (tied.Length > 1)
        {
            return baseRecord with
            {
                CorrelationConfidence = "low",
                CorrelationSignals = ["ambiguous", "multiple-operations", .. top.Signals.OrderBy(value => value, StringComparer.Ordinal)],
                CorrelationCandidates = candidateIds
            };
        }

        if (top.Confidence == "low")
        {
            return baseRecord with
            {
                CorrelationConfidence = "low",
                CorrelationSignals = ["insufficient-identity", .. top.Signals.OrderBy(value => value, StringComparer.Ordinal)],
                CorrelationCandidates = candidateIds
            };
        }

        return baseRecord with
        {
            OperationId = baseRecord.OperationId ?? top.Operation.OperationId,
            OperationName = baseRecord.OperationName ??
                top.Operation.OperationName ??
                top.Operation.CapabilityId,
            CorrelationConfidence = top.Confidence,
            CorrelationSignals = top.Signals
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            CorrelationCandidates = null,
            CorrelationOperationStatus = top.Operation.Status,
            CorrelationOperationSuccess = top.Operation.Success,
            CorrelationLaunchId = top.Operation.LaunchId,
            CorrelationGeneration = top.Operation.Generation,
            CorrelationSessionId = top.Operation.SessionId,
            CorrelationProfileFingerprint = top.Operation.ProfileFingerprint
        };
    }

    private static ScoredOperation? Score(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation,
        DiagnosticIntegrationState integration)
    {
        if (ConflictsWithDiagnosticRun(diagnostic, operation) ||
            ConflictsWithDiagnosticWorkflow(diagnostic, operation) ||
            ConflictsWithBridgeContext(operation, integration))
        {
            return null;
        }

        var signals = new HashSet<string>(StringComparer.Ordinal);
        var score = 0;
        var explicitId = !string.IsNullOrWhiteSpace(diagnostic.OperationId) &&
            !string.IsNullOrWhiteSpace(operation.OperationId) &&
            diagnostic.OperationId.Equals(operation.OperationId, StringComparison.Ordinal);
        if (explicitId)
        {
            score += 100;
            signals.Add("explicit-operation-id");
        }

        var identityCount = AddIdentitySignals(diagnostic, operation, integration, signals);
        score += identityCount * 30;

        var temporal = IsTemporallyRelated(diagnostic, operation);
        if (temporal)
        {
            score += 20;
            signals.Add("bounded-time-window");
        }

        var semantic = HasSemanticContext(diagnostic, operation);
        if (semantic)
        {
            score += 20;
            signals.Add("matching-operation-context");
        }

        if (HasMatchingBridgeLog(diagnostic, operation, integration))
        {
            score += 35;
            signals.Add("matching-bridge-log");
        }

        if (operation.Success is not null)
        {
            signals.Add(operation.Success.Value
                ? "operation-result-success"
                : "operation-result-failure");
        }

        if (!explicitId && !temporal)
        {
            return null;
        }

        var confidence = explicitId
            ? "high"
            : temporal && identityCount > 0 && (semantic || signals.Contains("matching-bridge-log"))
                ? "high"
                : temporal && identityCount > 0
                    ? "medium"
                    : "low";
        if (confidence == "low" && !semantic)
        {
            return null;
        }

        var displayId = operation.OperationId ??
            operation.OperationName ??
            operation.CapabilityId ??
            "operation";
        return new ScoredOperation
        {
            Operation = operation,
            DisplayId = displayId,
            Confidence = confidence,
            Score = score,
            Signals = signals
        };
    }

    private static int AddIdentitySignals(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation,
        DiagnosticIntegrationState integration,
        ISet<string> signals)
    {
        var count = 0;
        if (Same(diagnostic.RunId, operation.RunId))
        {
            count++;
            signals.Add("shared-run");
        }

        if (Same(diagnostic.WorkflowId, operation.WorkflowId))
        {
            count++;
            signals.Add("shared-workflow");
        }

        if (Same(integration.DevBridge?.LaunchId, operation.LaunchId) ||
            Same(integration.RimBridge?.LaunchId, operation.LaunchId))
        {
            count++;
            signals.Add("shared-launch");
        }

        if (Same(integration.DevBridge?.SessionId, operation.SessionId) ||
            Same(integration.RimBridge?.SessionId, operation.SessionId))
        {
            count++;
            signals.Add("shared-session");
        }

        if (integration.DevBridge?.Generation is { } devGeneration &&
            operation.Generation is { } operationGeneration &&
            devGeneration == operationGeneration)
        {
            count++;
            signals.Add("shared-generation");
        }

        if (integration.DevBridge?.ProcessId is { } devProcessId &&
            operation.ProcessId is { } operationProcessId &&
            devProcessId == operationProcessId)
        {
            count++;
            signals.Add("shared-process");
        }

        if (Same(integration.DevBridge?.ProfileFingerprint, operation.ProfileFingerprint) ||
            Same(integration.RimBridge?.ProfileFingerprint, operation.ProfileFingerprint))
        {
            count++;
            signals.Add("shared-profile");
        }

        return count;
    }

    private static bool ConflictsWithDiagnosticRun(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation)
    {
        return diagnostic.RunId is not null &&
            operation.RunId is not null &&
            !diagnostic.RunId.Equals(operation.RunId, StringComparison.Ordinal);
    }

    private static bool ConflictsWithDiagnosticWorkflow(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation)
    {
        return diagnostic.WorkflowId is not null &&
            operation.WorkflowId is not null &&
            !diagnostic.WorkflowId.Equals(operation.WorkflowId, StringComparison.Ordinal);
    }

    private static bool ConflictsWithIntegrationRun(
        DiagnosticRecord diagnostic,
        DiagnosticIntegrationState integration)
    {
        var runId = integration.DevBridge?.RunId ?? integration.RimBridge?.RunId;
        return diagnostic.RunId is not null &&
            runId is not null &&
            !diagnostic.RunId.Equals(runId, StringComparison.Ordinal);
    }

    private static bool ConflictsWithIntegrationWorkflow(
        DiagnosticRecord diagnostic,
        DiagnosticIntegrationState integration)
    {
        var workflowId = integration.DevBridge?.WorkflowId ?? integration.RimBridge?.WorkflowId;
        return diagnostic.WorkflowId is not null &&
            workflowId is not null &&
            !diagnostic.WorkflowId.Equals(workflowId, StringComparison.Ordinal);
    }

    private static bool ConflictsWithBridgeContext(
        DiagnosticBridgeOperation operation,
        DiagnosticIntegrationState integration)
    {
        var dev = integration.DevBridge;
        var rim = integration.RimBridge;
        if (operation.WorkflowId is not null &&
            ((dev?.WorkflowId is not null && !Same(dev.WorkflowId, operation.WorkflowId)) ||
             (rim?.WorkflowId is not null && !Same(rim.WorkflowId, operation.WorkflowId))))
        {
            return true;
        }

        if (operation.LaunchId is not null &&
            ((dev?.LaunchId is not null && !Same(dev.LaunchId, operation.LaunchId)) ||
             (rim?.LaunchId is not null && !Same(rim.LaunchId, operation.LaunchId))))
        {
            return true;
        }

        if (operation.Generation is { } generation &&
            ((dev?.Generation is { } devGeneration && devGeneration != generation) ||
             (rim?.Generation is { } rimGeneration && rimGeneration != generation)))
        {
            return true;
        }

        if (operation.ProcessId is { } processId &&
            dev?.ProcessId is { } devProcessId &&
            processId != devProcessId)
        {
            return true;
        }

        return false;
    }

    private static bool HasContextConflict(DiagnosticIntegrationState integration)
    {
        if (integration.DevBridge?.WorkflowId is { } devWorkflow &&
            integration.RimBridge?.WorkflowId is { } rimWorkflow &&
            !devWorkflow.Equals(rimWorkflow, StringComparison.Ordinal))
        {
            return true;
        }

        if (integration.DevBridge?.RunId is { } devRun &&
            integration.RimBridge?.RunId is { } rimRun &&
            !devRun.Equals(rimRun, StringComparison.Ordinal))
        {
            return true;
        }

        if (integration.DevBridge?.LaunchId is { } devLaunch &&
            integration.RimBridge?.LaunchId is { } rimLaunch &&
            !devLaunch.Equals(rimLaunch, StringComparison.Ordinal))
        {
            return true;
        }

        if (integration.DevBridge?.Generation is { } devGeneration &&
            integration.RimBridge?.Generation is { } rimGeneration &&
            devGeneration != rimGeneration)
        {
            return true;
        }

        if (integration.DevBridge?.ProcessId is { } devProcess &&
            integration.RimBridge?.ProcessId is { } rimProcess &&
            devProcess != rimProcess)
        {
            return true;
        }

        if (integration.DevBridge?.ProfileFingerprint is { } devProfile &&
            integration.RimBridge?.ProfileFingerprint is { } rimProfile &&
            !devProfile.Equals(rimProfile, StringComparison.Ordinal))
        {
            return true;
        }

        return integration.Warnings?.Any(warning =>
            warning.StartsWith("dev.workflow:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("dev.launch:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("dev.gen:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("dev.pid:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("dev.profile:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("rim.launch:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("rim.workflow:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("rim.gen:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("rim.pid:conflict", StringComparison.Ordinal) ||
            warning.StartsWith("rim.profile:conflict", StringComparison.Ordinal)) == true;
    }

    private static bool IsTemporallyRelated(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation)
    {
        var recordTimes = new[] { diagnostic.FirstOccurrence, diagnostic.LastOccurrence }
            .Where(value => value is not null)
            .Cast<DateTimeOffset>()
            .ToArray();
        if (recordTimes.Length == 0)
        {
            return false;
        }

        var start = operation.StartedAtUtc ?? operation.TimestampUtc;
        var end = operation.CompletedAtUtc ?? operation.TimestampUtc ?? operation.StartedAtUtc;
        if (start is null && end is null)
        {
            return false;
        }

        return recordTimes.Any(time =>
            (start is null || time >= start.Value - OperationEarlyTolerance) &&
            (end is null || time <= end.Value + OperationAfterglow));
    }

    private static bool HasSemanticContext(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation)
    {
        var operationNames = new[]
        {
            operation.OperationName,
            operation.CapabilityId
        };
        var diagnosticValues = new[]
        {
            diagnostic.Message,
            diagnostic.NormalizedMessage,
            diagnostic.OriginatingAssembly,
            diagnostic.OriginatingType,
            diagnostic.OriginatingMethod,
            diagnostic.TargetType,
            diagnostic.TargetMethod,
            diagnostic.DefName,
            diagnostic.Asset,
            diagnostic.Source
        };

        foreach (var name in operationNames.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var boundedName = name!.Trim();
            if (boundedName.Length < 4)
            {
                continue;
            }

            if (diagnosticValues.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Contains(boundedName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(operation.OperationId) &&
            diagnosticValues.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Contains(operation.OperationId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMatchingBridgeLog(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeOperation operation,
        DiagnosticIntegrationState integration)
    {
        if (string.IsNullOrWhiteSpace(operation.OperationId) || integration.Logs is not { Length: > 0 })
        {
            return false;
        }

        var diagnosticMessage = DiagnosticNormalizer.NormalizeMessage(
            diagnostic.NormalizedMessage ?? diagnostic.Message);
        return integration.Logs.Any(log =>
            Same(log.OperationId, operation.OperationId) &&
            IsLogTimeRelated(diagnostic, log) &&
            (!string.IsNullOrWhiteSpace(log.Message) &&
             (diagnosticMessage.Equals(
                  DiagnosticNormalizer.NormalizeMessage(log.Message),
                  StringComparison.Ordinal) ||
              diagnosticMessage.Contains(
                  DiagnosticNormalizer.NormalizeMessage(log.Message),
                  StringComparison.Ordinal))));
    }

    private static bool IsLogTimeRelated(
        DiagnosticRecord diagnostic,
        DiagnosticBridgeLogEntry log)
    {
        if (log.TimestampUtc is null)
        {
            return false;
        }

        var times = new[] { diagnostic.FirstOccurrence, diagnostic.LastOccurrence }
            .Where(value => value is not null)
            .Cast<DateTimeOffset>();
        return times.Any(time =>
            (time - log.TimestampUtc.Value).Duration() <= OperationAfterglow);
    }

    private static bool Same(string? left, string? right) =>
        left is not null && right is not null &&
        left.Equals(right, StringComparison.Ordinal);

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1
    };

    private sealed class ScoredOperation
    {
        public required DiagnosticBridgeOperation Operation { get; init; }

        public required string DisplayId { get; init; }

        public required string Confidence { get; init; }

        public required int Score { get; init; }

        public required ISet<string> Signals { get; init; }
    }
}
