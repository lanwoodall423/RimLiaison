# Cross-stack workflow correlation

workflowId is an optional, caller-owned correlation value for one RimTest workflow. RimTest
generates a bounded rw-... value for recipe, catalog-test, suite, and affected-test runs. It is
context only: it is not a lease capability, generation identity, launch identity, or operation
authorization.

| Surface | Owner | Correlation fields |
| --- | --- | --- |
| RimTest result | RimTest | optional workflowId; established runId, generation, evidenceId, and diagnostic fields keep their existing meanings |
| DevBridge recipe result | DevBridge2 | optional root workflowId; each returned operation may carry operationId, workflowId, generation, and launchId |
| DevBridge RimBridge route | DevBridge2 / RimBridgeServer | optional workflowId in the route and provenance; operationId is copied only from explicit RimBridge payload metadata |
| RimError integration | RimError | optional workflow context on DevBridge, RimBridge, and operation records; correlation still requires bounded timing and semantic/identity evidence |

The normal path is:

RimTest workflowId -> test recipe run --workflow-id -> DevBridge recipe result and operation
metadata -> optional RimBridge route provenance -> RimError integration metadata -> compact RimTest
result and optional diagnostic reference.

All additions are optional fields on the existing .../v1 envelopes. Older DevBridge responses
that omit workflowId remain valid; RimTest retains the requested value locally. A response that
explicitly returns a different workflow ID is rejected with
DEVBRIDGE_WORKFLOW_ID_MISMATCH. RimError rejects mismatched workflow or generation identity
when selecting an operation, and never treats workflow or time alone as proof of causality.
If an older DevBridge request parser explicitly returns TEST_RECIPE_USAGE for the additive option,
RimTest retries once without that option before any lifecycle mutation and retains the requested
workflow ID locally.

There is no show-run <workflowId> command and no second durable run database. Use the owner
records instead:

- DevBridge: DevBridge.cmd status --json, DevBridge.cmd agent snapshot --json, and
  DevBridge.cmd evidence show <evidence-id> --json.
- Generation/profile investigation: DevBridge.cmd history diagnose <generation>.
- RimError diagnosis: rimerror latest --json, then rimerror show <diagnostic-id>.

RimTest output remains compact: it emits identifiers and bounded references, not operations,
transcripts, logs, or full evidence. workflowId is excluded from failure-fingerprint inputs, so
adding or changing runtime correlation metadata does not change the fingerprint.
