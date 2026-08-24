# RimContext Context Bundle

The context bundle is the bounded, machine-readable snapshot for agents that need to decide
what to inspect, build, deploy, or run. Its current contract is `rimctx-bundle/v1`.

The standalone RimContext repository's latest `main` explicitly redirects maintained development
to the canonical RimLiaison repository. `RimContext.Core` and `RimContext.Cli` remain the primary
typed contract and static-context implementation here; RimLiaison supplies the maintained
cross-stack provider without moving owner state into RimContext.

## Entry points

Use the maintained orchestration entrypoint for a repository snapshot:

```text
rimliaison context --json
rimliaison context --json --verbose
```

Use the direct RimContext command for a lower-level static/environment snapshot or contract
debugging:

```text
rimctx context --root <workspace> --json
rimctx context --root <workspace> --json --verbose
```

The output is one top-level bundle object, not a second wrapper around another tool's response.
Compact JSON is the default. Verbose JSON preserves empty collections and indentation for human
inspection. Both forms carry the same facts and are deterministic for the same provider inputs.

## Shape and status semantics

The top-level fields are:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Compatibility identifier, currently `rimctx-bundle/v1`. |
| `generatedAtUtc` | UTC time at which this aggregation was made. |
| `snapshotStatus` | `complete` only when every core section is `available`; otherwise `partial`. |
| `stale` / `staleReasons` | Whether any supplied section exceeded its freshness budget. |
| `agentSummary` | Small action, blocker, reusable-evidence, ownership, and change summary. |
| `ownership` | The authoritative owner for each class of fact. |
| `topology` | Components, dependencies, versions, paths, and capabilities. |
| `repository` | Git identity, branch, revisions, divergence, source fingerprint, meaningful changes, and generated changes. |
| `relatedRepositories` | Read-only Git snapshots for explicitly configured companion roots, when available. |
| `environment` | OS/runtime/tool/configuration facts with `secretsExcluded: true`. |
| `deployment` | DevBridge2 build/deployment identity and artifact correspondence, when evidenced. |
| `runtime` | DevBridge2/RimBridgeServer process, generation, bridge, game, map, and lease facts. |
| `testing` | Catalog coverage, selection policy, evidence, cache/invalidation, benchmark summary, and validation need. |
| `recentExecutions` | Bounded recent build, test, command, retry, and recovery activity. |
| `failures` | Bounded failure signatures, recommended next actions, and reviewed knowledge matches when justified. |
| `efficiency` | Build/deployment/test timings, operation counts, evidence reuse/invalidation, launches, restarts, retries, and benchmark summary. |
| `decisions` | Why an action was chosen, skipped, reused, invalidated, retried, or blocked. |
| `extensions` | Provider-owned additive data identified by `(provider, key)`. |

Every core section is present even when its owner cannot answer. A section has a `status` of
`available`, `unknown`, `unavailable`, or `stale`. Unknown/unavailable sections carry a bounded
`reasonCode`, `message`, and optional `nextAction`; they never contain invented values. A stale
section may retain its last value, but consumers must treat it as non-authoritative until the
owner's next action or a fresh run proves it current.

`agentSummary` is derived from the canonical sections and bounded arrays; it is not a second
decision store. Its `status` is `healthy`, `action-required`, `blocked`, or `unknown`.
`reusableEvidence` contains only evidence projected as available, reused, or valid, while
`meaningfulChanges` excludes generated-only worktree state. Deployment correspondence distinguishes
source changes, build/deployment mismatches, runtime mismatch/staleness, synchronization, and
unknown correspondence.

Compact output is optimized for the first agent handoff. It keeps project/mod identifiers, owner
and status/reason-code fields, bounded meaningful changes, evidence summaries, and present hygiene
exceptions; absolute topology/repository/environment paths, absent convention paths, and full
external roots are omitted or represented symbolically. `--verbose` restores the complete
changed-file, generated-path, external-owner, execution, evidence, and path detail. This keeps
healthy startup snapshots small without hiding a diagnostic query.

The maintained compact command caps decisions, recent executions, and failures at eight items and
extensions/evidence projections at twelve. Verbose mode raises those limits to 32. Both remain
bounded and deterministically ordered; neither embeds logs.

## Ownership contract

RimContext owns aggregation and static-index facts. The bundle is a projection of owner state,
not a replacement database:

| Fact | Owner | Bundle role |
| --- | --- | --- |
| Repository state | Git | Read-only branch, revision, status, and generated-file classification. |
| Static source/dependency context | RimContext | Typed topology/static context and affected-impact inputs. |
| Test selection and evidence reuse | RimTest/RimLiaison | Selection decisions, cache state, invalidation, and validation policy. |
| Build/deployment/freshness | DevBridge2 | Existing transaction, artifact, generation, and proof evidence. |
| Live runtime and leases | DevBridge2/RimBridgeServer | Read-only doctor/bridge state; no game launch from `context`. |
| Failure classification | RimError/RimLiaison | Existing bounded issues and recovery references. |
| Orchestration | RimLiaison | Cross-owner correlation and compact execution projection. |

The context command does not edit `ModsConfig.xml`, launch RimWorld, create a daemon, or write a
context database. It uses only read-only DevBridge2 `doctor --json` and `agent snapshot --json`
probes; it never starts, stops, restarts, acquires a lease, or asks RimBridgeServer for mutating
game operations. Observability is read from the canonical app-data store. Generated/build state
remains outside the repository or is classified under `repository.generatedFiles`; it is never
reported as a meaningful source input.

Git dirtiness is reported only as repository state. It is not converted into a test-policy answer:
`testing.additionalValidationRequired` remains `null` until RimTest/RimLiaison supplies a decision,
an invalidation/failure, or current matching evidence. Likewise, an issue summary is not labeled a
known root cause unless the originating event or RimError's reviewed knowledge provides one.

When a RimError diagnostic store is configured, the provider reads its typed snapshot directly
and projects RimError's actionable root-cause ordering, stable diagnostic identifier, category,
originating assembly, and occurrence time. It does not ingest or parse `Player.log` during context
collection. RimLiaison observability issues remain a separate source and are deduplicated with the
RimError projection by signature and evidence identity.

The provider also exposes a bounded `extensions` entry named `stateHygiene`. It identifies the
canonical observability owner, known repository-local generated directories, and meaningful
configuration such as `.rimdev/stack.json`. Those paths are audited for reporting only; context
collection does not delete, rewrite, or broadly add ignore rules for them. DevBridge2-owned state
is reported by its configured root and remains outside RimLiaison's repository lifecycle.

Descriptor reconciliation preserves recoverable inputs under DevBridge2's existing ignored
`artifacts/descriptor-recovery` owner-state directory. Legacy
`DevelopmentProjects/*.recovery-backup-<guid>.json` and interrupted
`*.recovery-<guid>.tmp` files are narrowly classified as generated; the actual
`DevelopmentProjects/*.json` descriptors remain meaningful configuration and are never ignored.

## Decisions and provenance

Decision records make the agent-facing “why” explicit. A decision includes:

```json
{
  "decision": "affected-test-selection",
  "action": "RUN",
  "reasonCode": "AFFECTED_SELECTION_PROVEN",
  "explanation": "RimContext selected the affected validation set.",
  "relevantChangedInputs": ["Source/CompWidget.cs"],
  "evidenceReused": [],
  "evidenceInvalidated": [],
  "owner": "RimTest/RimLiaison",
  "observedAtUtc": "2026-08-21T00:00:00Z"
}
```

The action vocabulary is `RUN`, `REUSE`, `SKIP`, `INVALIDATE`, `RETRY`, and `BLOCK`. Evidence
references are identifiers and fingerprints, not copied logs or source files. Consumers should
follow the referenced owner contract when a decision is stale or blocked.

Validation evidence records also carry the immutable source, dependency, build, deployment,
suite/test, tool, configuration, environment, result, timestamp, and runtime-generation inputs
needed for safe reuse. `rimliaison publish check --json` queries those records without rerunning
validation; it returns a conservative block and `nextAction` when required evidence is absent or
invalid. `rimliaison benchmarks --json` exposes the deterministic A–H golden workflow baseline.
The compact `testing.invalidatedEvidence`, `testing.benchmarkSummary`, and efficiency counters
make redundant work visible without embedding logs.

## Provider and compatibility rules

`RimContext.Core` exposes `IRimContextBundleProvider`. Providers return typed section snapshots
and may add bounded decisions, executions, failures, and extensions. The builder orders providers,
selects the best available section, applies section freshness budgets, bounds collection sizes,
and converts provider exceptions into explicit failure evidence. Providers must not duplicate
authoritative stores or mutate the repository. RimLiaison's provider reads the actual affected-
selection and suite-completion events, including selected/executed/reused/skipped suites and
tests, cache invalidation, transaction/generation/fingerprint evidence, and structured
orchestration failure taxonomy. It does not infer those facts from console text.

Version `v1` is additive: consumers must ignore unknown extension keys and tolerate omitted compact
empty collections. New required semantics require a new schema version. The stable fields above,
status values, decision actions, owner names, and reason codes should be treated as the compatibility
surface for agent automation.
