# Cross-stack workflow correlation

workflowId is an optional, caller-owned correlation value for one RimLiaison workflow. RimLiaison
generates a bounded rw-... value for recipe, catalog-test, suite, and affected-test runs. It is
context only: it is not a lease capability, generation identity, launch identity, or operation
authorization.

| Surface | Owner | Correlation fields |
| --- | --- | --- |
| RimLiaison result | RimLiaison.Cli | optional workflowId; affected source runs additionally expose artifactFreshness with transaction, run, operation, lease, and generation identities |
| DevBridge recipe result | DevBridge2 | optional root workflowId; each returned operation may carry operationId, workflowId, generation, and launchId |
| DevBridge RimBridge route | DevBridge2 / RimBridgeServer | optional workflowId in the route and provenance; operationId is copied only from explicit RimBridge payload metadata |
| RimError integration | RimError module | optional workflow context on DevBridge, RimBridge, and operation records; correlation still requires bounded timing and semantic/identity evidence |

The normal recipe path is:

RimLiaison workflowId -> test recipe run --workflow-id -> DevBridge recipe result and operation
metadata -> optional RimBridge route provenance -> RimError integration metadata -> compact RimLiaison
result and optional diagnostic reference.

For a failed recipe, DevBridge2 is also the authoritative diagnostic-source owner. RimLiaison issues
the bounded `logs query --generation <generation> --since-launch --severity ERROR` request and
hands RimError.Core only the resulting semantic records, never an unrestricted Player.log path. The
source is accepted only when its response and every record match the failed run's generation;
RimError.Core then filters the in-process snapshot by run so a nearby run cannot satisfy the request.
The direct `rimerror latest --run <run>` command remains available for a separately requested
drill-down.
Missing, stale, mismatched, or unavailable source state is reported as diagnostic unavailability
while preserving the underlying test failure.

The stable source contract is `rimtest-devbridge-diagnostic-source/v1`: it carries the matching
generation, bounded UTF-8 semantic content, record count, truncation flag, and SHA-256. In the
normal path this contract is represented as typed data in-process; direct/external compatibility
checks may serialize it. Workflow, run, evidence, and operation identities remain in the separate
`rimerror-integration/v1` envelope; the source does not become a second lifecycle or correlation
authority.

For `rimliaison affected --run`, build-relevant changes first cross the DevBridge2 owner transaction with the
same workflowId and a worktree source fingerprint. RimLiaison then projects the owner transaction
identity and the selected recipe run/operation identities into `artifactFreshness`. The generation
must match the selected catalog `artifactFreshnessAnchor` recipe result when an anchor is declared;
if no anchor is declared, it must match every selected passing recipe. A missing or mismatched
anchor generation changes that child to infrastructure failure instead of reporting PASS. Other
recipes may use a later generation when profile-aware planning correctly keeps them out of a shared
reuse group. This is chronology/ownership evidence, not direct runtime DLL-hash introspection.

When a catalog suite uses explicit compatible isolation metadata, the lease and generation are
shared only within that sequential group. Each child still retains its own runId and operationId;
workflowId is common caller context, not proof that child identities are interchangeable. A reset,
failure, readiness/ownership change, generation mismatch, or uncertain lease cleanup terminates
reuse and is reported in the suite's bounded `reuse` summary.

All additions are optional fields on the existing .../v1 envelopes. Older DevBridge responses
that omit workflowId remain valid; RimLiaison retains the requested value locally. A response that
explicitly returns a different workflow ID is rejected with
DEVBRIDGE_WORKFLOW_ID_MISMATCH. RimError rejects mismatched workflow or generation identity
when selecting an operation, and never treats workflow or time alone as proof of causality.
If an older DevBridge request parser explicitly returns TEST_RECIPE_USAGE for the additive option,
RimLiaison retries once without that option before any lifecycle mutation and retains the requested
workflow ID locally.

There is no show-run <workflowId> command and no second durable run database. Use the owner
records instead:

- DevBridge: DevBridge.cmd status --json, DevBridge.cmd agent snapshot --json, and
  DevBridge.cmd evidence show <evidence-id> --json.
- Generation/profile investigation: DevBridge.cmd history diagnose <generation>.
- RimError diagnosis: use the `rimerror show <diagnostic-id>` next action returned by RimLiaison;
  direct log/store configuration is a fallback only.

RimLiaison output remains compact: it emits identifiers and bounded references, not operations,
transcripts, logs, or full evidence. workflowId is excluded from failure-fingerprint inputs, so
adding or changing runtime correlation metadata does not change the fingerprint.
