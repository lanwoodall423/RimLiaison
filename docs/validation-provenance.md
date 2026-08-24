# Validation provenance and publication checks

RimLiaison records immutable validation evidence after suite execution. The
evidence identity includes the repository, relevant source/content inputs,
dependencies, suite and test IDs, tool/configuration/environment inputs, and
build/deployment/runtime identity when runtime validation was required. The
record also keeps the result, timestamp, transaction, and source proof. A
timestamp alone never changes the identity; a correctness-relevant input does.

The stable decision actions are `RUN`, `REUSE`, `SKIP`, `INVALIDATE`, and
`BLOCK`. The reason code is part of the machine-readable result. Documentation
and generated owner state can therefore be skipped, XML/data changes select
static validation, runtime source changes require runtime evidence, and a
deployment mismatch can invalidate runtime evidence while preserving matching
static evidence.

## Publication query

Use the read-only publication query after the relevant validation has run:

```powershell
rimliaison publish check --json
rimliaison publish check --base origin/main --json
```

The query reads Git state and existing observability evidence. It does not run
tests, build, deploy, launch RimWorld, or modify Git state. It returns
`publicationAction: reuse` only when every required evidence kind matches the
current relevant inputs. Missing, stale, incomplete, or deployment-mismatched
evidence returns a conservative `block` result with a bounded `nextAction`.
The usual next action is `rimliaison affected --run --json`, which records new
evidence before the query is repeated.

## Golden workflows

Run the deterministic operation-count benchmark with:

```powershell
rimliaison benchmarks --json
```

The baseline covers documentation-only, XML/data-only, C# runtime/UI,
no-relevant-source, stale deployment, generated observability state,
infrastructure failure, and dependency change workflows. It compares build,
deployment, test, reuse, invalidation, and expensive-operation counts without
depending on wall-clock nanoseconds. Genuine RimWorld execution remains owned
by DevBridge2 and is represented as a bounded scenario metric.

The v1 baseline is explicitly the `current-implementation` baseline: no
pre-change historical run is assumed. `baseline` values are operation-count
expectations, not invented historical timings. Each scenario's stable
`totalWorkflowMs` and `testExecutionMs` values are a deterministic cost
envelope (`durationBasis=deterministic-cost-envelope-v1`) and are not used for
the regression gate. The CLI also reports best-effort `measuredDurationMs`
values for the current benchmark invocation; timing is informative only.

The validation-cost contract is:

| Layer | Trigger | Invalidated by | Reusable? | Owner | Publication use |
| --- | --- | --- | --- | --- | --- |
| RimContext index/impact | changed or missing static index | relevant source, Defs, project, dependency, or index configuration | yes, as static selection input | RimContext | indirectly selects affected tests |
| static validation | affected source/data/dependency change | relevant content/dependencies/tool/configuration | yes, with matching evidence | RimTest/RimLiaison | reuse when required static kinds match |
| build/deployment | runtime-required validation or stale artifact | source/build inputs, artifact, deployment target | build/deploy evidence is reusable only through DevBridge2 freshness | DevBridge2 | never inferred from Git alone |
| Quicktest/runtime | runtime/UI behavior or current artifact required | runtime generation, loaded artifact, source, environment, or deployment mismatch | yes only with matching generation and artifact correspondence | DevBridge2/RimBridgeServer via RimTest | reuse only when publication gate proves correspondence |
| publication check | commit/merge/push readiness | current relevant inputs or missing evidence | read-only decision; it does not rerun tests | RimLiaison | reuses valid evidence or returns a bounded block |

Infrastructure failures remain a separate retry/block dimension and do not become
source failures merely because they prevented evidence from being recorded.

## Failure knowledge

RimError exposes a reviewed, descriptive corpus for recurring development
failures. The seeded generated-state transaction entry explains why generated
observability/state worktree changes must be separated from source failures,
what action is appropriate, what source-debugging action is premature, and how
the incident affects evidence. Matching knowledge is projected into the
compact Context Bundle; it never performs autonomous remediation or source
mutation.
