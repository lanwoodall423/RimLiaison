using System.Globalization;
using System.Text.Json;

namespace RimError.Core;

/// <summary>
/// Adapts the current DevBridge2 and RimBridgeServer JSON projections into a
/// small RimError-owned representation. These adapters intentionally have no
/// reference to either external project.
/// </summary>
public static class DiagnosticIntegrationAdapter
{
    private const int MaxOperations = 256;
    private const int MaxLogs = 256;
    private const int MaxWarnings = 32;
    private const int MaxJsonDepth = 32;
    private const int MaxJsonLength = 1_048_576;

    public static DiagnosticIntegrationParseResult ParseDevBridge(string json)
    {
        return Parse(json, ParseDevBridgeDocument);
    }

    public static DiagnosticIntegrationParseResult ParseRimBridge(string json)
    {
        return Parse(json, ParseRimBridgeDocument);
    }

    public static DiagnosticIntegrationParseResult ParseIntegration(string json)
    {
        var dev = ParseDevBridge(json);
        var rim = ParseRimBridge(json);
        var warnings = dev.Warnings
            .Concat(rim.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var state = Combine(dev.State, rim.State);
        return new DiagnosticIntegrationParseResult
        {
            State = state,
            Recognized = state is not null,
            Warnings = warnings
        };
    }

    public static DiagnosticIntegrationState? Combine(
        params DiagnosticIntegrationState?[] states)
    {
        var usable = states
            .Where(state => state is not null)
            .Cast<DiagnosticIntegrationState>()
            .ToArray();
        if (usable.Length == 0)
        {
            return null;
        }

        var warnings = new List<string>();
        var schemas = usable
            .SelectMany(state => state.SourceSchemas ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var dev = MergeDev(usable.Select(state => state.DevBridge), warnings);
        var rim = MergeRim(usable.Select(state => state.RimBridge), warnings);
        var operations = MergeOperations(usable.SelectMany(state => state.Operations ?? []));
        var logs = MergeLogs(usable.SelectMany(state => state.Logs ?? []));
        warnings.AddRange(usable.SelectMany(state => state.Warnings ?? []));

        return new DiagnosticIntegrationState
        {
            SourceSchemas = schemas.Length == 0 ? null : schemas,
            DevBridge = dev,
            RimBridge = rim,
            Operations = operations.Length == 0 ? null : operations,
            Logs = logs.Length == 0 ? null : logs,
            Warnings = BoundWarnings(warnings)
        };
    }

    private static DiagnosticIntegrationParseResult Parse(
        string json,
        Func<JsonElement, List<string>, DiagnosticIntegrationState?> parser)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DiagnosticIntegrationParseResult
            {
                Warnings = ["integration payload is empty"]
            };
        }

        if (json.Length > MaxJsonLength)
        {
            return new DiagnosticIntegrationParseResult
            {
                Warnings = ["integration payload exceeds the 1 MiB safety limit"]
            };
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = MaxJsonDepth
                });
            var warnings = new List<string>();
            var state = parser(document.RootElement, warnings);
            if (state is null)
            {
                warnings.Add("integration payload was not recognized");
            }

            return new DiagnosticIntegrationParseResult
            {
                State = state is null
                    ? null
                    : state with { Warnings = BoundWarnings(warnings.Concat(state.Warnings ?? [])) },
                Recognized = state is not null,
                Warnings = BoundWarnings(warnings)
            };
        }
        catch (JsonException exception)
        {
            return new DiagnosticIntegrationParseResult
            {
                Warnings = [$"integration JSON invalid: {Bound(exception.Message, 160)}"]
            };
        }
    }

    private static DiagnosticIntegrationState? ParseDevBridgeDocument(
        JsonElement root,
        List<string> warnings)
    {
        var states = new List<DiagnosticIntegrationState>();
        ParseDevElement(root, states, warnings, depth: 0);
        return Combine(states.ToArray());
    }

    private static void ParseDevElement(
        JsonElement element,
        ICollection<DiagnosticIntegrationState> states,
        List<string> warnings,
        int depth)
    {
        if (states.Count >= MaxWarnings * 2)
        {
            return;
        }

        if (depth > 6 || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().Take(32))
            {
                ParseDevElement(child, states, warnings, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var schema = IntegrationJson.String(element, "schemaVersion");
        if (schema is null &&
            IntegrationJson.Property(element, "failureCode") is not null &&
            IntegrationJson.Property(element, "exceptionType") is not null)
        {
            schema = "devbridge-quicktest-failure/v1";
        }
        var nested = IntegrationJson.Property(element, "devBridge") ??
            IntegrationJson.Property(element, "devbridge");
        if (nested is { ValueKind: JsonValueKind.Object })
        {
            ParseDevElement(nested.Value, states, warnings, depth + 1);
        }

        var result = IntegrationJson.Property(element, "result");
        if (result is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } &&
            !HasDevIdentity(element))
        {
            ParseDevElement(result.Value, states, warnings, depth + 1);
        }

        var delta = IntegrationJson.Property(element, "delta");
        if (delta is { ValueKind: JsonValueKind.Object })
        {
            ParseDevElement(delta.Value, states, warnings, depth + 1);
        }

        var context = ParseDevContext(element, schema);
        if (context is null)
        {
            return;
        }

        var sourceSchemas = string.IsNullOrWhiteSpace(schema)
            ? null
            : new[] { schema! };
        states.Add(new DiagnosticIntegrationState
        {
            SourceSchemas = sourceSchemas,
            DevBridge = context
        });
    }

    private static DiagnosticDevBridgeContext? ParseDevContext(
        JsonElement root,
        string? schema)
    {
        var profile = IntegrationJson.Property(root, "acceptedProfile");
        var lease = IntegrationJson.Property(root, "requestingAgentLease") ??
            IntegrationJson.Property(root, "lease");
        var quicktest = IntegrationJson.Property(root, "quicktest");
        var failure = IntegrationJson.Property(root, "failure");

        var workflowId = IntegrationJson.FirstString(root, "workflowId", "workflow");
        var runId = IntegrationJson.FirstString(root, "runId", "run");
        var leaseId = IntegrationJson.FirstString(root, "leaseId", "testId", "test") ??
            IntegrationJson.String(lease, "leaseId", "id");
        var testId = IntegrationJson.FirstString(root, "testId", "test") ?? leaseId;
        var sessionId = IntegrationJson.FirstString(root, "sessionId", "session", "epoch");
        var launchId = IntegrationJson.FirstString(root, "launchId", "launch");
        var generation = IntegrationJson.Int(root, "generation", "gen");
        var processId = IntegrationJson.Int(root, "processId", "pid");
        var processStartTicks = IntegrationJson.Long(
            root,
            "processStartUtcTicks",
            "processStartTicks");
        var profileFingerprint = IntegrationJson.FirstString(
            root,
            "profileFingerprint",
            "profile");
        if (profile is { ValueKind: JsonValueKind.Object })
        {
            profileFingerprint ??= IntegrationJson.String(profile, "fingerprint");
        }

        var profileMode = IntegrationJson.FirstString(root, "profileMode", "mode") ??
            IntegrationJson.String(profile, "mode");
        var baselineFingerprint = IntegrationJson.FirstString(
            root,
            "baselineFingerprint",
            "baseProfile") ??
            IntegrationJson.String(profile, "baselineFingerprint", "baseProfile");
        var projects = IntegrationJson.StringArray(root, "projects") ??
            IntegrationJson.StringArray(profile, "projects");
        var phase = IntegrationJson.FirstString(root, "phase", "state");
        var captured = IntegrationJson.DateTimeOffset(
            root,
            "timestampUtc",
            "capturedAtUtc",
            "captured",
            "at");
        var startedAt = IntegrationJson.DateTimeOffset(
            root,
            "startedAtUtc",
            "startUtc",
            "lifecycleStartedUtc",
            "createdUtc");
        var endedAt = IntegrationJson.DateTimeOffset(
            root,
            "endedAtUtc",
            "endUtc",
            "lifecycleEndedUtc",
            "completedAtUtc");
        var diagnosticPaths = IntegrationJson.StringArray(root, "diagnosticPaths") ??
            IntegrationJson.StringArray(root, "paths");
        var failureCode = IntegrationJson.FirstString(root, "failureCode", "errorCode") ??
            IntegrationJson.String(failure, "code", "failureCode") ??
            IntegrationJson.String(quicktest, "failureCode", "code");
        var failureType = IntegrationJson.FirstString(root, "exceptionType", "failureType") ??
            IntegrationJson.String(failure, "exceptionType", "type") ??
            IntegrationJson.String(quicktest, "exceptionType", "type");
        var evidence = IntegrationJson.FirstString(root, "evidence", "diagnosticDetail") ??
            IntegrationJson.String(failure, "evidence", "summary", "diagnosticDetail") ??
            IntegrationJson.String(quicktest, "evidence");

        var hasIdentity = !string.IsNullOrWhiteSpace(runId) ||
            !string.IsNullOrWhiteSpace(workflowId) ||
            !string.IsNullOrWhiteSpace(testId) ||
            !string.IsNullOrWhiteSpace(launchId) ||
            generation is not null ||
            processId is not null ||
            !string.IsNullOrWhiteSpace(profileFingerprint) ||
            !string.IsNullOrWhiteSpace(phase) ||
            !string.IsNullOrWhiteSpace(failureCode) ||
            string.Equals(schema, DiagnosticIntegrationContract.DevBridgeAgentSnapshot, StringComparison.Ordinal) ||
            string.Equals(schema, DiagnosticIntegrationContract.DevBridgeAgentEvent, StringComparison.Ordinal) ||
            string.Equals(schema, DiagnosticIntegrationContract.DevBridgeGenerationContext, StringComparison.Ordinal) ||
            root.TryGetProperty("failureCode", out _);
        if (!hasIdentity)
        {
            return null;
        }

        return new DiagnosticDevBridgeContext
        {
            SourceSchema = schema,
            WorkflowId = Bound(workflowId, 128),
            RunId = runId,
            TestId = testId,
            LeaseId = leaseId,
            SessionId = sessionId,
            RuntimeSlotId = IntegrationJson.FirstString(root, "runtimeSlotId", "slot"),
            LaunchId = launchId,
            Generation = generation,
            ProcessId = processId,
            ProcessStartUtcTicks = processStartTicks,
            ProfileFingerprint = profileFingerprint,
            BaselineFingerprint = baselineFingerprint,
            ProfileMode = profileMode,
            Projects = BoundArray(projects, 16, 96),
            Phase = Bound(phase, 64),
            CapturedAtUtc = captured,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            DiagnosticPaths = BoundArray(diagnosticPaths, 8, 240),
            FailureCode = Bound(failureCode, 128),
            FailureType = Bound(failureType, 256),
            Evidence = Bound(evidence, 512)
        };
    }

    private static bool HasDevIdentity(JsonElement element) =>
        IntegrationJson.FirstString(
            element,
            "schemaVersion",
            "workflowId",
            "runId",
            "launchId",
            "profileFingerprint",
            "phase",
            "failureCode") is not null ||
        IntegrationJson.Int(element, "generation", "processId") is not null ||
        IntegrationJson.Property(element, "acceptedProfile") is not null ||
        IntegrationJson.Property(element, "requestingAgentLease") is not null;

    private static DiagnosticIntegrationState? ParseRimBridgeDocument(
        JsonElement root,
        List<string> warnings)
    {
        var builder = new RimBridgeBuilder();
        ParseRimElement(
            root,
            builder,
            inheritedEventType: null,
            inheritedContext: null,
            depth: 0);
        return builder.ToState(warnings);
    }

    private static void ParseRimElement(
        JsonElement element,
        RimBridgeBuilder builder,
        string? inheritedEventType,
        DiagnosticRimBridgeContext? inheritedContext,
        int depth)
    {
        if (depth > 8 || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().Take(64))
            {
                ParseRimElement(child, builder, inheritedEventType, inheritedContext, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var schema = IntegrationJson.String(element, "schemaVersion");
        if (!string.IsNullOrWhiteSpace(schema))
        {
            builder.AddSchema(schema!);
        }

        var context = MergeRimContexts(
            inheritedContext,
            ParseRimContext(element, schema));
        if (context is not null)
        {
            builder.MergeContext(context);
        }

        var eventType = IntegrationJson.FirstString(element, "type", "eventType") ??
            inheritedEventType;
        var operationEvent = IntegrationJson.Property(element, "operationEvent");
        if (operationEvent is { ValueKind: JsonValueKind.Object })
        {
            builder.AddOperation(ParseOperation(operationEvent.Value, eventType, context));
        }

        var logEntry = IntegrationJson.Property(element, "logEntry");
        if (logEntry is { ValueKind: JsonValueKind.Object })
        {
            builder.AddLog(ParseLog(logEntry.Value));
        }

        foreach (var key in new[] { "operations", "operationEvents", "events", "logs", "entries" })
        {
            var collection = IntegrationJson.Property(element, key);
            if (collection is not { ValueKind: JsonValueKind.Array })
            {
                continue;
            }

            foreach (var child in collection.Value.EnumerateArray().Take(64))
            {
                ParseRimElement(child, builder, eventType, context, depth + 1);
            }
        }

        var nested = IntegrationJson.Property(element, "rimBridge") ??
            IntegrationJson.Property(element, "rimbridge");
        if (nested is { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
        {
            ParseRimElement(nested.Value, builder, eventType, context, depth + 1);
        }

        var result = IntegrationJson.Property(element, "result") ??
            IntegrationJson.Property(element, "payload");
        var routeTool = IntegrationJson.FirstString(element, "toolName", "tool");
        var routeSuccess = IntegrationJson.Bool(element, "success", "ok");
        if (!string.IsNullOrWhiteSpace(routeTool) &&
            result is { } &&
            IntegrationJson.Property(element, "operationId") is null &&
            IntegrationJson.Property(element, "operationEvent") is null &&
            IntegrationJson.Property(element, "events") is null &&
            IntegrationJson.Property(element, "operations") is null)
        {
            builder.AddOperation(new DiagnosticBridgeOperation
            {
                OperationName = Bound(routeTool, 200),
                Success = routeSuccess,
                Status = routeSuccess switch
                {
                    true => "Completed",
                    false => "Failed",
                    _ => null
                },
                TimestampUtc = context?.CapturedAtUtc,
                LaunchId = context?.LaunchId,
                Generation = context?.Generation,
                ProcessId = context?.ProcessId,
                ProfileFingerprint = context?.ProfileFingerprint,
                ErrorCode = Bound(IntegrationJson.String(element, "errorCode", "code"), 128),
                ErrorMessage = Bound(IntegrationJson.String(element, "error"), 512)
            });
        }
        if (result is { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
        {
            ParseRimElement(result.Value, builder, eventType, context, depth + 1);
        }

        if (IsOperationObject(element))
        {
            builder.AddOperation(ParseOperation(element, eventType, context));
        }

        if (IsLogObject(element))
        {
            builder.AddLog(ParseLog(element));
        }

        var attentionOperationId = IntegrationJson.String(element, "causalOperationId");
        if (!string.IsNullOrWhiteSpace(attentionOperationId))
        {
            builder.AddOperation(new DiagnosticBridgeOperation
            {
                OperationId = Bound(attentionOperationId, 160),
                OperationName = Bound(IntegrationJson.String(element, "causalMethod"), 160),
                EventType = "attention.causal",
                TimestampUtc = IntegrationJson.DateTimeOffset(element, "openedAtUtc", "timestampUtc")
            });
        }
    }

    private static DiagnosticRimBridgeContext? ParseRimContext(
        JsonElement root,
        string? schema)
    {
        var provenance = IntegrationJson.Property(root, "provenance");
        var workflowId = IntegrationJson.FirstString(root, "workflowId", "workflow") ??
            IntegrationJson.String(provenance, "workflowId", "workflow");
        var launchId = IntegrationJson.FirstString(root, "launchId", "launch") ??
            IntegrationJson.String(provenance, "launchId", "launch");
        var runId = IntegrationJson.FirstString(root, "runId", "run");
        var sessionId = IntegrationJson.FirstString(root, "sessionId", "session");
        var generation = IntegrationJson.Int(root, "generation", "gen") ??
            IntegrationJson.Int(provenance, "generation", "gen");
        var processId = IntegrationJson.Int(root, "processId", "pid") ??
            IntegrationJson.Int(provenance, "processId", "pid");
        var profile = IntegrationJson.FirstString(root, "profileFingerprint", "profile") ??
            IntegrationJson.String(provenance, "profileFingerprint", "profile");
        var captured = IntegrationJson.DateTimeOffset(
            root,
            "timestampUtc",
            "invocationTimestampUtc",
            "capturedAtUtc") ??
            IntegrationJson.DateTimeOffset(provenance, "invocationTimestampUtc", "timestampUtc");

        if (string.IsNullOrWhiteSpace(launchId) &&
            string.IsNullOrWhiteSpace(workflowId) &&
            string.IsNullOrWhiteSpace(runId) &&
            string.IsNullOrWhiteSpace(sessionId) &&
            generation is null &&
            processId is null &&
            string.IsNullOrWhiteSpace(profile) &&
            captured is null &&
            provenance is null)
        {
            return null;
        }

        return new DiagnosticRimBridgeContext
        {
            SourceSchema = schema,
            WorkflowId = Bound(workflowId, 128),
            RunId = Bound(runId, 160),
            SessionId = Bound(sessionId, 160),
            LaunchId = Bound(launchId, 160),
            Generation = generation,
            ProcessId = processId,
            ProfileFingerprint = Bound(profile, 160),
            CapturedAtUtc = captured
        };
    }

    private static DiagnosticRimBridgeContext? MergeRimContexts(
        DiagnosticRimBridgeContext? inherited,
        DiagnosticRimBridgeContext? local)
    {
        return inherited is null
            ? local
            : local is null
                ? inherited
                : MergeRimPair(inherited, local, new List<string>());
    }

    private static bool IsOperationObject(JsonElement element) =>
        IntegrationJson.Property(element, "operationId") is not null ||
        IntegrationJson.Property(element, "eventId") is not null ||
        IntegrationJson.Property(element, "startedAtUtc") is not null ||
        IntegrationJson.Property(element, "capabilityId") is not null &&
        (IntegrationJson.Property(element, "status") is not null ||
         IntegrationJson.Property(element, "success") is not null);

    private static bool IsLogObject(JsonElement element) =>
        IntegrationJson.Property(element, "entryId") is not null &&
        IntegrationJson.Property(element, "message") is not null &&
        (IntegrationJson.Property(element, "level") is not null ||
         IntegrationJson.Property(element, "timestampUtc") is not null);

    private static DiagnosticBridgeOperation ParseOperation(
        JsonElement root,
        string? inheritedEventType,
        DiagnosticRimBridgeContext? inheritedContext)
    {
        var error = IntegrationJson.Property(root, "error");
        var metadata = IntegrationJson.Property(root, "metadata");
        var operationId = IntegrationJson.FirstString(root, "operationId", "id");
        var name = IntegrationJson.FirstString(
            root,
            "operationName",
            "capabilityId",
            "toolName",
            "method");
        var capability = IntegrationJson.String(root, "capabilityId");
        var status = IntegrationJson.EnumText(root, "status");
        var eventType = IntegrationJson.FirstString(root, "eventType") ?? inheritedEventType;
        var started = IntegrationJson.DateTimeOffset(root, "startedAtUtc", "startUtc", "start");
        var completed = IntegrationJson.DateTimeOffset(root, "completedAtUtc", "endUtc", "end");
        var timestamp = IntegrationJson.DateTimeOffset(root, "timestampUtc", "timestamp", "ts");
        var success = IntegrationJson.Bool(root, "success", "ok");
        var errorCode = IntegrationJson.FirstString(root, "errorCode", "code") ??
            IntegrationJson.String(error, "code");
        var errorMessage = IntegrationJson.FirstString(root, "errorMessage", "message") ??
            IntegrationJson.String(error, "message");
        var context = inheritedContext;

        var workflowId = IntegrationJson.FirstString(root, "workflowId", "workflow") ??
            IntegrationJson.String(metadata, "workflowId", "workflow") ?? context?.WorkflowId;
        var runId = IntegrationJson.FirstString(root, "runId", "run") ??
            IntegrationJson.String(metadata, "runId", "run") ?? context?.RunId;
        var sessionId = IntegrationJson.FirstString(root, "sessionId", "session") ??
            IntegrationJson.String(metadata, "sessionId", "session") ?? context?.SessionId;
        var launchId = IntegrationJson.FirstString(root, "launchId", "launch") ?? context?.LaunchId;
        var generation = IntegrationJson.Int(root, "generation", "gen") ?? context?.Generation;
        var processId = IntegrationJson.Int(root, "processId", "pid") ?? context?.ProcessId;
        var profile = IntegrationJson.FirstString(root, "profileFingerprint", "profile") ?? context?.ProfileFingerprint;

        return new DiagnosticBridgeOperation
        {
            OperationId = Bound(operationId, 160),
            WorkflowId = Bound(workflowId, 128),
            OperationName = Bound(name, 200),
            EventType = Bound(eventType, 96),
            Status = Bound(status, 48),
            Success = success,
            StartedAtUtc = started,
            CompletedAtUtc = completed,
            TimestampUtc = timestamp ?? completed ?? started,
            RunId = Bound(runId, 160),
            SessionId = Bound(sessionId, 160),
            LaunchId = Bound(launchId, 160),
            Generation = generation,
            ProcessId = processId,
            ProfileFingerprint = Bound(profile, 160),
            CapabilityId = Bound(capability, 200),
            ParentOperationId = Bound(IntegrationJson.FirstString(root, "parentOperationId", "parent"), 160),
            RootOperationId = Bound(IntegrationJson.FirstString(root, "rootOperationId", "root"), 160),
            ErrorCode = Bound(errorCode, 128),
            ErrorMessage = Bound(errorMessage, 512),
            Sequence = IntegrationJson.Long(root, "sequence", "seq"),
            WarningCount = IntegrationJson.Int(root, "warningCount", "warn"),
            ResultWasTruncated = IntegrationJson.Bool(root, "resultWasTruncated", "truncated", "trunc"),
            ScriptStatementId = Bound(IntegrationJson.FirstString(root, "scriptStatementId", "stmt"), 160),
            ScriptStepId = Bound(IntegrationJson.FirstString(root, "scriptStepId", "step"), 160),
            ScriptCall = Bound(IntegrationJson.FirstString(root, "scriptCall", "call"), 240)
        };
    }

    private static DiagnosticBridgeLogEntry ParseLog(JsonElement root)
    {
        return new DiagnosticBridgeLogEntry
        {
            EntryId = Bound(IntegrationJson.FirstString(root, "entryId", "id"), 160),
            Level = Bound(IntegrationJson.String(root, "level"), 24),
            Message = Bound(IntegrationJson.FirstString(root, "message", "msg"), 512),
            OperationId = Bound(IntegrationJson.FirstString(root, "operationId", "op"), 160),
            CapabilityId = Bound(IntegrationJson.FirstString(root, "capabilityId", "cap"), 200),
            Source = Bound(IntegrationJson.String(root, "source"), 80),
            ParentOperationId = Bound(IntegrationJson.FirstString(root, "parentOperationId", "parent"), 160),
            RootOperationId = Bound(IntegrationJson.FirstString(root, "rootOperationId", "root"), 160),
            FirstSeenAtUtc = IntegrationJson.DateTimeOffset(root, "firstSeenAtUtc", "first"),
            TimestampUtc = IntegrationJson.DateTimeOffset(root, "timestampUtc", "last", "timestamp"),
            RepeatCount = IntegrationJson.Int(root, "repeatCount", "repeat")
        };
    }

    private static DiagnosticDevBridgeContext? MergeDev(
        IEnumerable<DiagnosticDevBridgeContext?> values,
        List<string> warnings)
    {
        var current = (DiagnosticDevBridgeContext?)null;
        foreach (var value in values.Where(value => value is not null))
        {
            current = current is null
                ? value
                : MergeDevPair(current, value!, warnings);
        }

        return current;
    }

    private static DiagnosticRimBridgeContext? MergeRim(
        IEnumerable<DiagnosticRimBridgeContext?> values,
        List<string> warnings)
    {
        var current = (DiagnosticRimBridgeContext?)null;
        foreach (var value in values.Where(value => value is not null))
        {
            current = current is null
                ? value
                : MergeRimPair(current, value!, warnings);
        }

        return current;
    }

    private static DiagnosticDevBridgeContext MergeDevPair(
        DiagnosticDevBridgeContext left,
        DiagnosticDevBridgeContext right,
        ICollection<string> warnings)
    {
        return new DiagnosticDevBridgeContext
        {
            SourceSchema = MergeText(left.SourceSchema, right.SourceSchema, "dev.schema", warnings),
            WorkflowId = MergeText(left.WorkflowId, right.WorkflowId, "dev.workflow", warnings),
            RunId = MergeText(left.RunId, right.RunId, "dev.run", warnings),
            TestId = MergeText(left.TestId, right.TestId, "dev.test", warnings),
            LeaseId = MergeText(left.LeaseId, right.LeaseId, "dev.lease", warnings),
            SessionId = MergeText(left.SessionId, right.SessionId, "dev.session", warnings),
            RuntimeSlotId = MergeText(left.RuntimeSlotId, right.RuntimeSlotId, "dev.slot", warnings),
            LaunchId = MergeText(left.LaunchId, right.LaunchId, "dev.launch", warnings),
            Generation = MergeNumber(left.Generation, right.Generation, "dev.gen", warnings),
            ProcessId = MergeNumber(left.ProcessId, right.ProcessId, "dev.pid", warnings),
            ProcessStartUtcTicks = MergeNumber(left.ProcessStartUtcTicks, right.ProcessStartUtcTicks, "dev.start", warnings),
            ProfileFingerprint = MergeText(left.ProfileFingerprint, right.ProfileFingerprint, "dev.profile", warnings),
            BaselineFingerprint = MergeText(left.BaselineFingerprint, right.BaselineFingerprint, "dev.baseProfile", warnings),
            ProfileMode = MergeText(left.ProfileMode, right.ProfileMode, "dev.mode", warnings),
            Projects = MergeArray(left.Projects, right.Projects),
            Phase = MergeText(left.Phase, right.Phase, "dev.phase", warnings),
            CapturedAtUtc = Later(left.CapturedAtUtc, right.CapturedAtUtc),
            StartedAtUtc = Earlier(left.StartedAtUtc, right.StartedAtUtc),
            EndedAtUtc = Later(left.EndedAtUtc, right.EndedAtUtc),
            DiagnosticPaths = MergeArray(left.DiagnosticPaths, right.DiagnosticPaths),
            FailureCode = MergeText(left.FailureCode, right.FailureCode, "dev.failure", warnings),
            FailureType = MergeText(left.FailureType, right.FailureType, "dev.failureType", warnings),
            Evidence = MergeText(left.Evidence, right.Evidence, "dev.evidence", warnings)
        };
    }

    private static DiagnosticRimBridgeContext MergeRimPair(
        DiagnosticRimBridgeContext left,
        DiagnosticRimBridgeContext right,
        ICollection<string> warnings)
    {
        return new DiagnosticRimBridgeContext
        {
            SourceSchema = MergeText(left.SourceSchema, right.SourceSchema, "rim.schema", warnings),
            WorkflowId = MergeText(left.WorkflowId, right.WorkflowId, "rim.workflow", warnings),
            RunId = MergeText(left.RunId, right.RunId, "rim.run", warnings),
            SessionId = MergeText(left.SessionId, right.SessionId, "rim.session", warnings),
            LaunchId = MergeText(left.LaunchId, right.LaunchId, "rim.launch", warnings),
            Generation = MergeNumber(left.Generation, right.Generation, "rim.gen", warnings),
            ProcessId = MergeNumber(left.ProcessId, right.ProcessId, "rim.pid", warnings),
            ProfileFingerprint = MergeText(left.ProfileFingerprint, right.ProfileFingerprint, "rim.profile", warnings),
            CapturedAtUtc = Later(left.CapturedAtUtc, right.CapturedAtUtc)
        };
    }

    private static DiagnosticBridgeOperation[] MergeOperations(
        IEnumerable<DiagnosticBridgeOperation> values)
    {
        var bounded = values
            .Where(value => value is not null &&
                (!string.IsNullOrWhiteSpace(value.OperationId) ||
                 !string.IsNullOrWhiteSpace(value.OperationName)))
            .Take(MaxOperations * 2)
            .ToArray();
        return bounded
            .GroupBy(OperationKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(value => value.TimestampUtc ?? value.CompletedAtUtc ?? value.StartedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(value => value.EventType ?? string.Empty, StringComparer.Ordinal)
                .Aggregate((left, right) => MergeOperation(left, right)))
            .OrderBy(value => value.TimestampUtc ?? value.CompletedAtUtc ?? value.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(value => value.OperationId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.OperationName ?? string.Empty, StringComparer.Ordinal)
            .Take(MaxOperations)
            .ToArray();
    }

    private static DiagnosticBridgeLogEntry[] MergeLogs(
        IEnumerable<DiagnosticBridgeLogEntry> values)
    {
        return values
            .Where(value => value is not null &&
                (!string.IsNullOrWhiteSpace(value.EntryId) ||
                 !string.IsNullOrWhiteSpace(value.Message)))
            .Take(MaxLogs * 2)
            .GroupBy(LogKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(value => value.TimestampUtc ?? DateTimeOffset.MaxValue)
                .Aggregate((left, right) => MergeLog(left, right)))
            .OrderBy(value => value.TimestampUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(value => value.EntryId ?? string.Empty, StringComparer.Ordinal)
            .Take(MaxLogs)
            .ToArray();
    }

    private static DiagnosticBridgeOperation MergeOperation(
        DiagnosticBridgeOperation left,
        DiagnosticBridgeOperation right)
    {
        return left with
        {
            OperationId = left.OperationId ?? right.OperationId,
            WorkflowId = left.WorkflowId ?? right.WorkflowId,
            OperationName = left.OperationName ?? right.OperationName,
            EventType = PreferEvent(left.EventType, right.EventType),
            Status = PreferStatus(left.Status, right.Status),
            Success = right.Success ?? left.Success,
            StartedAtUtc = Earlier(left.StartedAtUtc, right.StartedAtUtc),
            CompletedAtUtc = Later(left.CompletedAtUtc, right.CompletedAtUtc),
            TimestampUtc = Later(left.TimestampUtc, right.TimestampUtc),
            RunId = left.RunId ?? right.RunId,
            SessionId = left.SessionId ?? right.SessionId,
            LaunchId = left.LaunchId ?? right.LaunchId,
            Generation = left.Generation ?? right.Generation,
            ProcessId = left.ProcessId ?? right.ProcessId,
            ProfileFingerprint = left.ProfileFingerprint ?? right.ProfileFingerprint,
            CapabilityId = left.CapabilityId ?? right.CapabilityId,
            ParentOperationId = left.ParentOperationId ?? right.ParentOperationId,
            RootOperationId = left.RootOperationId ?? right.RootOperationId,
            ErrorCode = left.ErrorCode ?? right.ErrorCode,
            ErrorMessage = left.ErrorMessage ?? right.ErrorMessage,
            Sequence = Later(left.Sequence, right.Sequence),
            WarningCount = left.WarningCount ?? right.WarningCount,
            ResultWasTruncated = left.ResultWasTruncated ?? right.ResultWasTruncated,
            ScriptStatementId = left.ScriptStatementId ?? right.ScriptStatementId,
            ScriptStepId = left.ScriptStepId ?? right.ScriptStepId,
            ScriptCall = left.ScriptCall ?? right.ScriptCall
        };
    }

    private static DiagnosticBridgeLogEntry MergeLog(
        DiagnosticBridgeLogEntry left,
        DiagnosticBridgeLogEntry right)
    {
        return left with
        {
            EntryId = left.EntryId ?? right.EntryId,
            Level = left.Level ?? right.Level,
            Message = left.Message ?? right.Message,
            OperationId = left.OperationId ?? right.OperationId,
            CapabilityId = left.CapabilityId ?? right.CapabilityId,
            Source = left.Source ?? right.Source,
            ParentOperationId = left.ParentOperationId ?? right.ParentOperationId,
            RootOperationId = left.RootOperationId ?? right.RootOperationId,
            FirstSeenAtUtc = Earlier(left.FirstSeenAtUtc, right.FirstSeenAtUtc),
            TimestampUtc = Later(left.TimestampUtc, right.TimestampUtc),
            RepeatCount = MergeRepeatCount(left.RepeatCount, right.RepeatCount)
        };
    }

    private static int? MergeRepeatCount(int? left, int? right)
    {
        var count = Math.Max(left ?? 0, right ?? 0);
        return count > 0 ? count : null;
    }

    private static string OperationKey(DiagnosticBridgeOperation value) =>
        !string.IsNullOrWhiteSpace(value.OperationId)
            ? "id:" + value.OperationId
            : "value:" + string.Join(
                "|",
                value.OperationName,
                value.TimestampUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                value.StartedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                value.Sequence?.ToString(CultureInfo.InvariantCulture));

    private static string LogKey(DiagnosticBridgeLogEntry value) =>
        !string.IsNullOrWhiteSpace(value.EntryId)
            ? "id:" + value.EntryId
            : "value:" + string.Join(
                "|",
                value.OperationId,
                value.TimestampUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DiagnosticNormalizer.NormalizeMessage(value.Message));

    private static string? MergeText(
        string? left,
        string? right,
        string field,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right) || left.Equals(right, StringComparison.Ordinal))
        {
            return left;
        }

        warnings.Add($"{field}:conflict");
        return null;
    }

    private static T? MergeNumber<T>(
        T? left,
        T? right,
        string field,
        ICollection<string> warnings)
        where T : struct, IEquatable<T>
    {
        if (left is null)
        {
            return right;
        }

        if (right is null || left.Value.Equals(right.Value))
        {
            return left;
        }

        warnings.Add($"{field}:conflict");
        return null;
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left <= right ? left : right;

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left >= right ? left : right;

    private static long? Later(long? left, long? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static string? PreferEvent(string? left, string? right) =>
        string.IsNullOrWhiteSpace(right) ? left : right;

    private static string? PreferStatus(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        return StatusRank(right) >= StatusRank(left) ? right : left;
    }

    private static int StatusRank(string status) => status.ToLowerInvariant() switch
    {
        "failed" or "timedout" or "timed_out" or "cancelled" => 4,
        "completed" => 3,
        "running" => 2,
        "pending" => 1,
        _ => 0
    };

    private static string[]? BoundArray(
        string[]? values,
        int maximumItems,
        int maximumLength)
    {
        if (values is null)
        {
            return null;
        }

        var bounded = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, maximumLength)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(maximumItems)
            .ToArray();
        return bounded.Length == 0 ? null : bounded;
    }

    private static string[]? MergeArray(string[]? left, string[]? right) =>
        BoundArray((left ?? []).Concat(right ?? []).ToArray(), 16, 96);

    private static string[] BoundWarnings(IEnumerable<string> warnings) =>
        warnings
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, 200)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaxWarnings)
            .ToArray();

    private static string? Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    private static class IntegrationJson
    {
        public static JsonElement? Property(JsonElement? element, string name)
        {
            if (element is not { ValueKind: JsonValueKind.Object } value ||
                !TryGetProperty(value, name, out var property))
            {
                return null;
            }

            return property;
        }

        private static bool TryGetProperty(
            JsonElement value,
            string name,
            out JsonElement property)
        {
            if (value.TryGetProperty(name, out property))
            {
                return true;
            }

            foreach (var candidate in value.EnumerateObject())
            {
                if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }

            property = default;
            return false;
        }

        public static string? String(JsonElement? element, params string[] names)
        {
            if (element is not { } value)
            {
                return null;
            }

            foreach (var name in names)
            {
                var property = Property(value, name);
                var result = StringValue(property);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }

            return null;
        }

        public static string? FirstString(JsonElement element, params string[] names) =>
            String(element, names);

        public static int? Int(JsonElement? element, params string[] names)
        {
            if (element is not { } value)
            {
                return null;
            }

            foreach (var name in names)
            {
                var property = Property(value, name);
                if (property is not { } candidate)
                {
                    continue;
                }

                if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt32(out var number))
                {
                    return number;
                }

                if (candidate.ValueKind == JsonValueKind.String &&
                    int.TryParse(candidate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    return number;
                }
            }

            return null;
        }

        public static long? Long(JsonElement? element, params string[] names)
        {
            if (element is not { } value)
            {
                return null;
            }

            foreach (var name in names)
            {
                var property = Property(value, name);
                if (property is not { } candidate)
                {
                    continue;
                }

                if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out var number))
                {
                    return number;
                }

                if (candidate.ValueKind == JsonValueKind.String &&
                    long.TryParse(candidate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    return number;
                }
            }

            return null;
        }

        public static bool? Bool(JsonElement? element, params string[] names)
        {
            if (element is not { } value)
            {
                return null;
            }

            foreach (var name in names)
            {
                var property = Property(value, name);
                if (property is not { } candidate)
                {
                    continue;
                }

                if (candidate.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (candidate.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                if (candidate.ValueKind == JsonValueKind.String &&
                    bool.TryParse(candidate.GetString(), out var result))
                {
                    return result;
                }
            }

            return null;
        }

        public static DateTimeOffset? DateTimeOffset(
            JsonElement? element,
            params string[] names)
        {
            if (element is not { } value)
            {
                return null;
            }

            foreach (var name in names)
            {
                var property = Property(value, name);
                if (property is not { } candidate)
                {
                    continue;
                }

                if (candidate.ValueKind == JsonValueKind.String &&
                    System.DateTimeOffset.TryParse(
                        candidate.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }

        public static string? EnumText(JsonElement? element, string name)
        {
            var property = Property(element, name);
            if (property is not { } value)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number switch
                {
                    0 => "Pending",
                    1 => "Running",
                    2 => "Completed",
                    3 => "Failed",
                    4 => "TimedOut",
                    5 => "Cancelled",
                    _ => number.ToString(CultureInfo.InvariantCulture)
                };
            }

            return null;
        }

        public static string[]? StringArray(JsonElement? element, string name)
        {
            var property = Property(element, name);
            if (property is not { ValueKind: JsonValueKind.Array } value)
            {
                return null;
            }

            return value.EnumerateArray()
                .Take(32)
                .Select(item => StringValue(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray();
        }

        private static string? StringValue(JsonElement? element)
        {
            if (element is not { } value)
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null
            };
        }
    }

    private sealed class RimBridgeBuilder
    {
        private readonly List<DiagnosticBridgeOperation> _operations = [];
        private readonly List<DiagnosticBridgeLogEntry> _logs = [];
        private readonly List<string> _schemas = [];
        private DiagnosticRimBridgeContext? _context;

        public void AddSchema(string schema)
        {
            if (_schemas.Count < 64 && !string.IsNullOrWhiteSpace(schema))
            {
                _schemas.Add(Bound(schema, 96)!);
            }
        }

        public void MergeContext(DiagnosticRimBridgeContext context)
        {
            _context = _context is null
                ? context
                : MergeRimPair(_context, context, new List<string>());
        }

        public void AddOperation(DiagnosticBridgeOperation operation)
        {
            if (_operations.Count < MaxOperations * 2)
            {
                _operations.Add(operation);
            }
        }

        public void AddLog(DiagnosticBridgeLogEntry log)
        {
            if (_logs.Count < MaxLogs * 2)
            {
                _logs.Add(log);
            }
        }

        public DiagnosticIntegrationState? ToState(List<string> warnings)
        {
            var operations = MergeOperations(_operations);
            var logs = MergeLogs(_logs);
            var schemas = _schemas
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (_context is null && operations.Length == 0 && logs.Length == 0 && schemas.Length == 0)
            {
                return null;
            }

            return new DiagnosticIntegrationState
            {
                SourceSchemas = schemas.Length == 0 ? null : schemas,
                RimBridge = _context,
                Operations = operations.Length == 0 ? null : operations,
                Logs = logs.Length == 0 ? null : logs,
                Warnings = BoundWarnings(warnings)
            };
        }
    }
}
