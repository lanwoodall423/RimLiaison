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

## Evaluator guidance

Treat the profile as objective evidence. Compare `runs`, `cumulativeMs`,
`generations`, `retries`, `noOpRuns`, `failures`, and `overflow` counters to
identify repeated work, retries, unchanged-state operations, expensive phases,
and broad or disproportionate execution. For example, repeated tests against
one generation are represented as grouped runs and generation counts; no field
labels the behavior as wasteful. A later evaluator supplies that judgment.
