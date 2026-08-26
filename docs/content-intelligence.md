# RimWorld Content Intelligence

Phase 1 is a deterministic learning substrate: it captures compact intent and objective outcomes
during normal RimLiaison validation, stores reusable references across the canonical workspace, and
answers bounded precedent queries. It does not generate content or measure fun.

The maintained capability also exposes the versioned structural analysis APIs described below; they
remain data-only and do not add an executable content generator.

## Contracts

- `content-blueprint/v1` records semantic intent: kind, gameplay role, design parameters, vanilla
  comparables, framework requirements or deliberate absence, project constraints, validation
  expectations, and reuse source.
- `content-evidence/v1` records static/reference, build, affected-test, runtime, serialization,
  error/warning, repair, retry, and final outcomes separately from blueprint intent.
- Derived metadata is attached when available: repository/project, agent/session/run identity,
  indexed source files and entities, dependencies, framework dependencies, source/commit/tool
  identities, timestamps, evidence references, and measured elapsed time. Unknown values stay null.

Evidence is accepted for reuse only when its source identity matches the blueprint identity. A changed
source fingerprint, commit, workspace, tool, project, or repository makes old evidence stale.

The reuse contract is explicit and ordered:

`RimContent proven archetype -> proven RimContext precedent -> vanilla/reference pattern -> novel implementation`

## Qualification and promotion

Structural fingerprints are computed before semantic reuse. They include normalized content kind,
gameplay role, design shape, entity shape, dependencies, framework requirements, and validation
expectations; different shapes form different clusters.

Qualification uses versioned, inspectable criteria (`content-qualification-criteria/v1`):
minimum successful implementations, distinct projects and runs, repair-rate limit, fresh source
identity, and every applicable validation result. A vague model confidence score is never sufficient.
Repeated failures, stale evidence, missing validation, or private project constraints fail closed.

Qualified candidates are historically replayed before automatic promotion to a versioned,
data-only `content-archetype/v1`. Replay checks structural and intent compatibility plus deterministic
validation. Promotion is rejected when qualification or replay fails. A failed attributable use records
`content-archetype-usage/v1` evidence, quarantines the active archetype, and falls back through the
same hierarchy; quarantined data remains available for diagnosis and cannot be selected.

Archetypes contain templates, defaults, constraints, validation expectations, and supporting IDs.
They contain no executable generator or opaque learned behavior. Project exclusions, private assumptions,
and project-specific constraints remain local policies and do not qualify global reuse.

## Agent workflow

For content-development work, provide only the semantic fields that cannot be derived:

```text
rimliaison affected --run --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

Use the same `--content-kind`, `--content-role`, and optional `--content-reuse-source` options with
`rimliaison golden-path --json`. RimLiaison automatically writes the blueprint before affected
selection and appends evidence after the selected validation. Normal work does not require a second
capture command.

Query compact precedent knowledge through the canonical entrypoint:

```text
rimliaison content query --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

Direct static drill-down remains available:

```text
rimctx content --kind ThingDef --role "early-game ranged weapon" --limit 10 --json
```

Both queries return ranked structured references, not source dumps. Results are bounded by limit and
byte budget. The shared store defaults to the canonical workspace `.rimdev/content-intelligence.jsonl`
when the repository is under a workspace containing `.rimdev`; `RIMCONTEXT_CONTENT_STORE` or
`--store` selects an explicit store for diagnostics and tests.

Project policy can exclude a precedent or add project constraints. Global/shared precedent is the
default; project metadata remains on each record.

## Observability: Content Intelligence

RimLiaison emits canonical `content.*` events into the existing append-only agent
observability store; it does not create a second content database. The lifecycle includes
blueprint creation/update/validation, precedent detection/qualification, reuse selection,
RimContent promotion, archetype use, regression, quarantine, rollback, project exclusion,
and source ineligibility. `logicalAgentId` is the durable agent association across runs and
sessions; run and session IDs remain the drill-down scope.

The desktop Content Intelligence view is a bounded incremental projection of those events.
It shows live lifecycle activity, blueprint/detail history, proven precedent history, archetype
versions and regressions, reuse distribution, and exact efficiency fields. Elapsed time is
reported only from recorded measurements. Input/output tokens remain `unavailable` unless
the producer records exact values for every included validation; no token or time estimate is
invented. Repair, retry, validation, rollback, and error counts are event-derived.

Administrative buttons are emergency operational controls, not normal approval gates:
quarantine archetype, rollback to a prior stable version, exclude a precedent for one project,
and mark a source ineligible. Each successful action writes an audit event. Failed or missing
targets are no-ops; they do not alter reuse selection.
