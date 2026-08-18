# RimLiaison/RimError integration contract

RimError is RimLiaison's internal bounded diagnostic module and accepts optional JSON projections. It
has no runtime or compile-time dependency on DevBridge2 or RimBridgeServer. Missing, unavailable,
or unrecognized integration data leaves the normal diagnostic pipeline usable; a supplied malformed
file is reported on stderr and does not silently become an asserted correlation.

## Input envelope

The stable interchange envelope is `rimerror-integration/v1`:

```json
{
  "schemaVersion": "rimerror-integration/v1",
  "devBridge": {
    "schemaVersion": "devbridge-generation-context/v1",
    "workflowId": "rw-17",
    "runId": "run-17",
    "testId": "lease-abc",
    "launchId": "launch-17",
    "generation": 17,
    "processId": 4242,
    "profileFingerprint": "sha256..."
  },
  "rimBridge": {
    "operations": [
      {
        "operationId": "op-1",
        "workflowId": "rw-17",
        "capabilityId": "mymod/create_assembler",
        "status": "Completed",
        "success": true,
        "startedAtUtc": "2026-08-17T12:00:01Z",
        "completedAtUtc": "2026-08-17T12:00:02Z",
        "runId": "run-17",
        "launchId": "launch-17",
        "generation": 17
      }
    ]
  }
}
```

The CLI also accepts current native projections directly:

- DevBridge2 `devbridge-agent-snapshot/v1`, `devbridge-agent-event/v1`,
  `devbridge-generation-context/v1`, and bounded quicktest/failure objects.
- RimBridgeServer operation envelopes/events, `rimbridge/list_operation_events` results,
  `rimbridge/list_logs` results, and DevBridge-routed results with `provenance`.

The adapters retain only bounded identity, operation, timestamp, status, and error fields. They do
not retain bridge tokens, complete tool results, or duplicate raw logs.

## Direct CLI drill-down

The normal RimLiaison diagnostic path passes bounded DevBridge2 semantic records directly to
`RimError.Core` in-process. For an independent diagnostic handoff or compatibility check, capture
the JSON outputs from the owning tools and pass them to the direct CLI:

```text
rimerror ingest Player.log --devbridge agent-snapshot.json --rimbridge bridge-events.json
```

`--integration <file>` accepts the combined envelope. `--run`, `--test`, `--operation`, and
`--operation-name` remain explicit caller metadata overrides. Multiple bridge files may be supplied.

## Correlation contract

RimError can attach `run`, `test`, `op`, `opName`, `corr`, and bounded `corrSignals` to a stored
diagnostic. Fingerprints never include these fields. High confidence requires an explicit operation
ID, or matching bridge identity plus a bounded time window and semantic/log context. Medium confidence
requires matching identity plus time. Time proximity alone is rejected. Mismatched run, launch, or
generation identities and stale operations are not correlated. Tied candidates remain unassigned and
are exposed as `corrCandidates` with low confidence for drill-down.

workflowId is optional caller context shared by the participating surfaces; it does not replace
runId, lease, generation, launch, or operation identity. RimError may preserve it on the diagnostic
record, but workflow identity alone never correlates an operation. Explicit workflow or generation
mismatches fail closed, and ambiguous nearby operations remain unassigned. Older envelopes that
omit the field remain valid. The field is not an input to failure fingerprints.

DevBridge2 remains the authority for lifecycle, leases, generations, and profiles. RimBridgeServer
remains the authority for live operations and logs. No durable show-run index is created; callers
use the owning DevBridge evidence/generation commands and rimerror show <id> for drill-down.
