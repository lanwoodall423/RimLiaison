using RimError.Core;
using Xunit;

namespace RimError.Core.Tests;

public sealed class DiagnosticIntegrationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "integration", name);

    [Fact]
    public void DevBridge_snapshot_and_generation_context_preserve_current_identity_fields()
    {
        var snapshot = DiagnosticIntegrationAdapter.ParseDevBridge(
            File.ReadAllText(Fixture("devbridge-agent-snapshot.json")));
        var context = DiagnosticIntegrationAdapter.ParseDevBridge(
            File.ReadAllText(Fixture("devbridge-generation-context.json")));

        Assert.True(snapshot.Recognized);
        Assert.True(context.Recognized);
        var combined = DiagnosticIntegrationAdapter.Combine(snapshot.State, context.State);
        Assert.NotNull(combined);
        var dev = combined!.DevBridge;
        Assert.NotNull(dev);
        Assert.Equal("rw-stage8", dev.WorkflowId);
        Assert.Equal("epoch-stage8", dev.SessionId);
        Assert.Equal("lease-stage8", dev.LeaseId);
        Assert.Equal("launch-stage8", dev.LaunchId);
        Assert.Equal(17, dev.Generation);
        Assert.Equal(4242, dev.ProcessId);
        Assert.Equal("profile-stage8", dev.ProfileFingerprint);
        Assert.Equal("baseline-stage8", dev.BaselineFingerprint);
        Assert.Equal(638909280000000000, dev.ProcessStartUtcTicks);
        Assert.Equal("projects", dev.ProfileMode);
    }

    [Fact]
    public void RimBridge_current_operation_and_log_projections_are_adapted()
    {
        var operations = DiagnosticIntegrationAdapter.ParseRimBridge(
            File.ReadAllText(Fixture("rimbridge-operation-events.json")));
        var logs = DiagnosticIntegrationAdapter.ParseRimBridge(
            File.ReadAllText(Fixture("rimbridge-logs.json")));

        Assert.True(operations.Recognized);
        Assert.True(logs.Recognized);
        var combined = DiagnosticIntegrationAdapter.Combine(operations.State, logs.State);
        Assert.NotNull(combined);
        Assert.Contains(combined!.Operations!, operation =>
            operation.OperationId == "op-create" &&
            operation.OperationName == "mymod/create_assembler" &&
            operation.Success == true);
        Assert.Contains(combined.Logs!, log =>
            log.OperationId == "op-create" &&
            log.CapabilityId == "mymod/create_assembler");
        Assert.Equal("launch-stage8", combined.RimBridge!.LaunchId);
        Assert.Equal("rw-stage8", combined.RimBridge.WorkflowId);
        Assert.Equal(17, combined.RimBridge.Generation);
    }

    [Fact]
    public async Task Both_sources_produce_high_confidence_action_correlation()
    {
        var integration = LoadBoth();
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata
            {
                RunId = "run-stage8",
                Integration = integration
            });
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration);
        var diagnostic = Assert.Single(snapshot.Items);

        Assert.Equal("op-create", diagnostic.OperationId);
        Assert.Equal("mymod/create_assembler", diagnostic.OperationName);
        Assert.Equal("lease-stage8", diagnostic.TestId);
        Assert.Equal("high", diagnostic.CorrelationConfidence);
        Assert.Contains("shared-run", diagnostic.CorrelationSignals!);
        Assert.Contains("shared-workflow", diagnostic.CorrelationSignals!);
        Assert.Contains("shared-generation", diagnostic.CorrelationSignals!);
        Assert.Contains("matching-operation-context", diagnostic.CorrelationSignals!);
        var show = DiagnosticJson.Serialize(diagnostic, includeStack: true);
        Assert.Contains("\"bridgeStatus\":\"Completed\"", show);
        Assert.Contains("\"bridgeLaunch\":\"launch-stage8\"", show);
    }

    [Fact]
    public async Task Compact_root_report_includes_only_material_high_confidence_operation_context()
    {
        var integration = LoadBoth();
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata
            {
                RunId = "run-stage8",
                Integration = integration
            });
        var snapshot = DiagnosticRootCauseEngine.Apply(
            DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration));

        var json = DiagnosticJson.Serialize(DiagnosticLatestReportBuilder.Build(snapshot));

        Assert.Contains("\"operation\":\"mymod/create_assembler\"", json);
        Assert.Contains("\"test\":\"lease-stage8\"", json);
        Assert.DoesNotContain("rimerror-integration", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_operation_metadata_is_high_confidence_without_guessing()
    {
        var integration = LoadBoth();
        var result = await Ingest(
            "NullReferenceException: unrelated wording",
            new DiagnosticIngestionMetadata
            {
                OperationId = "op-create",
                RunId = "run-stage8",
                Integration = integration
            });
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration);

        var diagnostic = Assert.Single(snapshot.Items);
        Assert.Equal("op-create", diagnostic.OperationId);
        Assert.Equal("high", diagnostic.CorrelationConfidence);
        Assert.Contains("explicit-operation-id", diagnostic.CorrelationSignals!);
    }

    [Fact]
    public async Task Neither_source_leaves_core_diagnostic_independent()
    {
        var result = await Ingest("NullReferenceException: no bridge context");
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot());
        var diagnostic = Assert.Single(snapshot.Items);

        Assert.Null(snapshot.Integration);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.OperationName);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Workflow_context_without_rimbridge_operation_preserves_identity_without_guessing()
    {
        var integration = new DiagnosticIntegrationState
        {
            DevBridge = new DiagnosticDevBridgeContext
            {
                WorkflowId = "rw-no-operation",
                Generation = 4
            }
        };
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: no routed operation",
            new DiagnosticIngestionMetadata
            {
                WorkflowId = "rw-no-operation",
                Integration = integration
            });

        var diagnostic = Assert.Single(
            DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration).Items);
        Assert.Equal("rw-no-operation", diagnostic.WorkflowId);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Mismatched_run_ids_do_not_correlate_even_when_times_match()
    {
        var integration = LoadBoth();
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata
            {
                RunId = "run-different",
                Integration = integration
            });
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration);

        var diagnostic = Assert.Single(snapshot.Items);
        Assert.Equal("run-different", diagnostic.RunId);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Mismatched_workflow_ids_do_not_correlate_even_when_semantics_match()
    {
        var integration = new DiagnosticIntegrationState
        {
            DevBridge = new DiagnosticDevBridgeContext
            {
                WorkflowId = "rw-current",
                Generation = 4
            },
            Operations =
            [
                new DiagnosticBridgeOperation
                {
                    OperationId = "op-other-workflow",
                    WorkflowId = "rw-other",
                    OperationName = "mymod/create_assembler",
                    Generation = 4,
                    TimestampUtc = Start.AddSeconds(1)
                }
            ]
        };
        var result = await Ingest(
            "[2026-08-17T12:00:01Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata
            {
                WorkflowId = "rw-current",
                Integration = integration
            });

        var diagnostic = Assert.Single(
            DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration).Items);
        Assert.Equal("rw-current", diagnostic.WorkflowId);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Mismatched_generations_do_not_correlate_even_when_workflow_matches()
    {
        var integration = new DiagnosticIntegrationState
        {
            DevBridge = new DiagnosticDevBridgeContext
            {
                WorkflowId = "rw-current",
                Generation = 4
            },
            Operations =
            [
                new DiagnosticBridgeOperation
                {
                    OperationId = "op-old-generation",
                    WorkflowId = "rw-current",
                    OperationName = "mymod/create_assembler",
                    Generation = 3,
                    TimestampUtc = Start.AddSeconds(1)
                }
            ]
        };
        var result = await Ingest(
            "[2026-08-17T12:00:01Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata
            {
                WorkflowId = "rw-current",
                Integration = integration
            });

        var diagnostic = Assert.Single(
            DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration).Items);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Stale_operation_metadata_does_not_correlate_by_identity_alone()
    {
        var integration = new DiagnosticIntegrationState
        {
            DevBridge = new DiagnosticDevBridgeContext
            {
                RunId = "run-1",
                LaunchId = "launch-1",
                Generation = 4,
                ProfileFingerprint = "profile-1"
            },
            Operations =
            [
                new DiagnosticBridgeOperation
                {
                    OperationId = "old-op",
                    OperationName = "mymod/create_assembler",
                    RunId = "run-1",
                    LaunchId = "launch-1",
                    Generation = 4,
                    ProfileFingerprint = "profile-1",
                    TimestampUtc = Start.AddMinutes(-10)
                }
            ]
        };
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: mymod/create_assembler",
            new DiagnosticIngestionMetadata { RunId = "run-1" });
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration);

        var diagnostic = Assert.Single(snapshot.Items);
        Assert.Null(diagnostic.OperationId);
        Assert.Null(diagnostic.CorrelationConfidence);
    }

    [Fact]
    public async Task Multiple_nearby_operations_are_reported_as_ambiguous()
    {
        var integration = new DiagnosticIntegrationState
        {
            DevBridge = new DiagnosticDevBridgeContext
            {
                RunId = "run-1",
                LaunchId = "launch-1",
                Generation = 4
            },
            Operations =
            [
                Operation("op-a", "mymod/action_a"),
                Operation("op-b", "mymod/action_b")
            ]
        };
        var result = await Ingest(
            "[2026-08-17T12:00:03Z] ERROR NullReferenceException: object reference not set",
            new DiagnosticIngestionMetadata { RunId = "run-1" });
        var snapshot = DiagnosticIntegrationCorrelator.Apply(result.ToSnapshot(), integration);

        var diagnostic = Assert.Single(snapshot.Items);
        Assert.Null(diagnostic.OperationId);
        Assert.Equal("low", diagnostic.CorrelationConfidence);
        Assert.Equal(["op-a", "op-b"], diagnostic.CorrelationCandidates!);
        Assert.Contains("ambiguous", diagnostic.CorrelationSignals!);
    }

    [Fact]
    public void Normalized_envelope_is_versioned_and_compact()
    {
        const string json = """
            {
              "schemaVersion":"rimerror-integration/v1",
              "devBridge":{"schemaVersion":"devbridge-generation-context/v1","launchId":"launch-1","generation":2},
              "rimBridge":{"operations":[{"operationId":"op-1","capabilityId":"mymod/action","success":true}]}
            }
            """;

        var parsed = DiagnosticIntegrationAdapter.ParseIntegration(json);

        Assert.True(parsed.Recognized);
        Assert.NotNull(parsed.State);
        Assert.Equal("launch-1", parsed.State!.DevBridge!.LaunchId);
        Assert.Equal("op-1", Assert.Single(parsed.State.Operations!).OperationId);
        Assert.Contains("rimerror-integration/v1", parsed.State.SourceSchemas!);
    }

    private static DiagnosticIntegrationState LoadBoth()
    {
        var dev = DiagnosticIntegrationAdapter.Combine(
            DiagnosticIntegrationAdapter.ParseDevBridge(
                File.ReadAllText(Fixture("devbridge-agent-snapshot.json"))).State,
            DiagnosticIntegrationAdapter.ParseDevBridge(
                File.ReadAllText(Fixture("devbridge-generation-context.json"))).State);
        var rim = DiagnosticIntegrationAdapter.ParseRimBridge(
            File.ReadAllText(Fixture("rimbridge-operation-events.json"))).State;
        var combined = DiagnosticIntegrationAdapter.Combine(dev, rim);
        Assert.NotNull(combined);
        return combined!;
    }

    private static DiagnosticBridgeOperation Operation(string id, string name) =>
        new()
        {
            OperationId = id,
            OperationName = name,
            RunId = "run-1",
            LaunchId = "launch-1",
            Generation = 4,
            TimestampUtc = Start.AddSeconds(1)
        };

    private static async Task<DiagnosticIngestionResult> Ingest(
        string text,
        DiagnosticIngestionMetadata? metadata = null)
    {
        return await new DiagnosticIngestor().IngestAsync(
            new StringReader(text),
            "fixture.log",
            metadata,
            new DiagnosticIngestionOptions { IngestionTime = Start });
    }
}
