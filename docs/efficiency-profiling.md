# RimLiaison efficiency profiles

RimLiaison emits one always-on local execution profile per CLI invocation. The
profile is an efficiency evidence artifact, not a general observability trace:
completed OpenTelemetry `Activity` instances are aggregated in memory and only
one compact sidecar is written during shutdown.

## Coverage boundary

The profile observes work performed by the RimLiaison process at these central
boundaries:

- the top-level invocation and command phases;
- affected Git discovery initiated by RimLiaison and affected selection;
- build/deploy/artifact-freshness transactions;
- suite, test, and recipe execution;
- DevBridge child-process operations, including lifecycle operations;
- RimError diagnostic-source acquisition and diagnosis;
- retries, failures, cancellation, generation, lease, reset, and freshness
  transitions when those identities are already available.

It does not claim visibility into file edits, shell commands, Git operations,
searches, model calls, prompts, reasoning, or other work outside this process.
It does not collect full traces, payloads, stdout/stderr, compiler output,
test logs, screenshots, prompts, model messages, source contents, diffs,
credentials, tokens, environment values, or exception stacks.

## Artifact and schema

Profiles are written atomically under:

```text
.rimdev/profiles/rimliaison-<run-id>.json
```

The schema identifier is `rimliaison-efficiency-profile/v1`. Retention is at
most 20 generated profiles and 256 KiB total. Each profile is hard-bounded to
16 KiB. The command result and its existing stdout/stderr/JSON contracts are
independent of profile serialization or persistence failures.

`usefulWork.cumulativeActivityMs` is inclusive: parent and child activity
durations can overlap. Use `outcome.wallTimeMs` for elapsed process time and
the cumulative value for attribution across nested operations.

The compact schema contains:

- `identity`, `coverage`, and `outcome` for run identity, boundary, exit
  status, cancellation, and wall time;
- `usefulWork` and `phaseTiming` for activity totals and phase durations;
- `operationCounts` with deterministic semantic fingerprints, runs,
  cumulative time, outcome counts, generation identities, retry counts, and
  bounded error codes;
- `testing` and `buildDeploy` summaries;
- `repeatedOperations`, `unchangedStateRepetition`, `retryFailureGroups`,
  `noOpEvidence`, `slowest`, and bounded `churn` evidence;
- `overflow` counters for omitted operation groups, phase groups, category
  groups, generation values, error codes, processor failures, and output
  trimming. General timing sections retain the costliest groups; specialized
  sections prioritize the signal they describe (repetitions, failures/retries,
  unchanged generations, or no-ops).

Logical targets, scopes, workflows, and other correlation values are stored as
short deterministic hashes. Only stable error-code-shaped values are retained;
other diagnostic values are hashed. Arbitrary activity tags are ignored.

## Bounded fail-fast ordering

`--fail-fast` may use the profiles as an ordering hint, but never as a
selection or correctness decision. The reuse planner first establishes the
complete deterministic execution order and its compatible groups. Historical
ordering can only permute the members of one already-proven reusable group;
it cannot split a group, cross a fresh-generation boundary, create a group, or
skip a selected test. Non-fail-fast runs do not use this hint and continue to
execute the complete selected set, subject only to the existing PASS-proof
reuse mechanism.

The ordering policy is versioned as
`rimliaison-fail-fast-ordering/v1`. A profile is usable only when its
`identity.orderingSchema`, fixed-size `identity.orderingContext` hash, normal
profile schema, outcome, and timestamp are valid for the current selected
catalog/reuse shape. Profiles older than 14 days, future-dated beyond a small
clock-skew allowance, malformed, oversized, incompatible, or with fewer than
2 observations for a recipe are ignored. At most the newest 32 profile files
are inspected; the existing 20-profile/256 KiB retention and 16 KiB per-file
limits remain authoritative. No raw source, prompt, log, stack trace, recipe
input, or test output is persisted for this feature.

For each recipe with sufficient evidence, the inspectable integer score uses
bounded percentages and average duration:

- failure rate, weighted by `10,000`;
- retry rate, weighted by `1,000`;
- cheapness (`100 - min(100, averageMs / 250)`), weighted by `10`;
- a generation-count penalty capped at 20, weighted by `-100`;
- a no-op-rate penalty capped at 20, weighted by `-10`.

Higher scores run first within their proven group. Ties are resolved in this
order: known evidence, score, failure rate, lower average duration, retry
rate, lower generation count, lower no-op rate, observed runs, and finally
ordinal test ID. Missing or insufficient usable history falls back to the
reuse planner's deterministic order. The bounded result field
`failFast.historicalOrdering` reports only `used`, `reason`, and `policy`; it
does not expose the underlying history or score values. A preflight/artifact
failure that prevents planning reports `reason: "not-attempted"` rather than
implying that history was consulted.

## Evaluator guidance

Treat the profile as objective evidence. Compare `runs`, `cumulativeMs`,
`generations`, `retries`, `noOpRuns`, `failures`, and `overflow` counters to
identify repeated work, retries, unchanged-state operations, expensive phases,
and broad or disproportionate execution. For example, repeated tests against
one generation are represented as grouped runs and generation counts; no field
labels the behavior as wasteful. A later evaluator supplies that judgment.
