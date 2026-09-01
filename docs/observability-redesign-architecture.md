# RimLiaison Observability redesign architecture

## Decision

The authoritative `AgentObservabilityStore` remains the source of truth. The redesign adds one deterministic, WinForms-independent projection between records and presentation: `ProjectObservabilityProjection`, which produces `ProjectObservabilityState` and `ToolingFinding` aggregates. Overview summaries and project detail must consume the same projection; neither may independently derive “latest” or state.

This stage changes the information architecture and projection contract. It does not reconstruct the WinForms layout.

## Current audit

The current path is:

```text
AgentObservabilitySession -> AgentEvent / AgentSnapshot
                         -> AgentIssueDetector -> AgentIssue
                         -> AgentObservabilityStore (JSONL authority)
                         -> AgentObservabilityUi (cache, grouping, filtering, projection)
                         -> ObservabilityMainForm (controls, rendering, navigation, handoff)
```

`AgentObservabilityStore` owns persisted events, issues, snapshots, evidence, sequence allocation, retention, hydration, stale-session reconciliation, and diagnostic bundles. `AgentObservabilityModels` is the record/schema boundary. `AgentObservabilityUi` currently owns indexing, entity grouping, filtering, navigation identity, activity sorting, issue grouping, session selection, content/reliability projections, and detail derivation. `ObservabilityMainForm.cs` additionally owns control construction, panel layout, incremental reconciliation, selection synchronization, formatting, hydration status, clipboard handoff, and content/reliability actions.

### Field classification

| Existing field/view | Classification | Decision |
|---|---|---|
| Event IDs, sequence, run/session/agent IDs, entity identity, persisted timestamps | authoritative | retain; use only for correlation and deterministic ordering |
| Event type, stage, summary, bounded data and evidence references | authoritative evidence | retain; interpret in the canonical projection |
| Agent status, completion, failure, current stage/activity | authoritative snapshot evidence, reconciled by store | retain; never use as a competing project state |
| Issue category, severity, blocking, recovery, references, owner, fingerprint | derived authoritative record | retain; project project problems and tooling findings separately |
| `AgentObservabilityProductionEntry.Latest*` | derived and currently ambiguous | retain only as compatibility output; populate from `ProjectObservabilityState` |
| `AgentObservabilityAllView.Activity` and agent `RecentActivity` | derived, duplicated | both must be slices of one canonical project timeline; global activity is secondary context |
| current operation/activity, elapsed time, stage progress | derived | expose only when evidence and active-session semantics support it |
| workload/toolchain/qualification fields | authoritative context | use structural eligibility; never use display-name heuristics |
| raw paths, commands, tool calls, stdout/stderr, traces, generations, packet internals | implementation/diagnostic | progressive disclosure in System / Diagnostics and bundles |
| Issues | owner-facing | unresolved/actionable first; recovered/history secondary |
| Recommendations | owner-facing tooling backlog, not project failure | subsume into Tooling Findings / assessment; keep compatibility route during migration |
| Reliability | campaign/system evidence | System / Diagnostics, not ordinary project status |
| Content Intelligence | useful specialized evidence | secondary dedicated surface under the shared host; project links remain available |
| Copy to ChatGPT and full diagnostic export | owner handoff | preserve as a projection of selected evidence; never invoke a model implicitly |
| navigation labels and display names | presentation only | never identity; route by canonical entity identity |

The existing UI therefore has useful evidence but too many independent derived fields. `LatestTimestamp`, `LatestEvent`, snapshot completion, activity rows, issue rows, and selected-session detail can disagree because they are separately projected.

## Contradiction investigation

The reported contradiction is possible in the pre-canonical implementation without a store corruption:

1. `BuildAllViewLocked` filtered cached events, globally bounded them to `MaximumActivityRows`, and then `BuildProductionEntriesLocked` chose a per-entity latest event from that already bounded list. Its fallback also considered snapshot start/completion timestamps.
2. `BuildAgentViewLocked` independently selected entity-matching events from the UI cache and took the last recent rows. It did not consume the overview's bounded event set or production entry.
3. Initial hydration is bounded; explicit detail operations can hydrate older operation history. Thus the two projections can see different evidence windows.
4. Agent, event, and issue filtering was applied independently. A query could leave a snapshot visible while hiding its event, or leave an event visible whose snapshot was not visible.
5. Top-level navigation groups canonical entity identity, while detail lookup accepts agent or mod input and then chooses a preferred session. Multiple sessions, concurrent logical workers, and run scope could therefore select different representatives.
6. Production navigation excludes qualification/test records structurally, but the old activity stream is broader. A qualification event could be visible beside production activity without being valid production project state.
7. Numeric event timestamps and sequence order are not the same as snapshot `StartTime`, `CompletedAt`, or generic “latest.” Local formatting is correctly late, but the semantics were not centralized.
8. Reloaded/stale snapshots and delayed watcher hydration could be newer or more complete in one cache than another.
9. A projection cache keyed only by store revision could retain `Working` after the clock crossed the staleness threshold when no new event arrived. Time-dependent state must be keyed by the projection evaluation time (or recomputed).

Therefore Overview could report T2 from one event set while project detail only had T1. The redesign does not patch this case in the form. The canonical projection loads one evidence view, derives one timeline, and requires every summary timestamp/event to reference an entry in that timeline. If an entry is outside the loaded history, the projection reports Partial/Degraded evidence instead of claiming it as complete. The UI now evaluates the shared projection for one snapshot time and re-evaluates time-dependent state as that time changes.

## Source-of-truth hierarchy

1. Authoritative persisted records and evidence in `AgentObservabilityStore`.
2. Deterministic reconciliation already owned by the store: sequence, identity normalization, terminal-event reconciliation, stale-session handling, retention and degraded-history status.
3. `ProjectObservabilityProjection` for owner-facing project state, semantic timestamps, timeline inclusion, and tooling-finding aggregation.
4. Surface-specific presenters that select and label canonical projection data without redefining state.
5. WinForms controls and formatting only.

Filtering, row limits, selected session, and progressive disclosure are never allowed to redefine state. A filtered surface visibly reports that it is filtered.

## Canonical project projection

`ProjectObservabilityState` contains:

- `ObservabilityEntityIdentity` and display name;
- `Healthy`, `Working`, `NeedsAttention`, `Blocked`, or `Unknown` state;
- `ActionRequired`;
- current operation and last meaningful operation;
- `LastMeaningfulActivityAt`;
- `LatestAttempt`;
- `LastSuccessfulValidation`;
- `LastFailureOrProblem`, current unresolved problem, owner, and classification;
- meaningful active sessions only, preserving run, session, and logical-worker identity;
- one chronological canonical timeline containing every event that can support a displayed last-activity claim;
- project-linked `ToolingFindings`;
- `Complete`, `Partial`, `Degraded`, or `Unknown` completeness and stale-session evidence.

State rules:

- an active Running/Waiting snapshot or other complete, non-stale activity evidence is `Working` unless an unresolved blocking problem makes it `Blocked`;
- unresolved project problems or an unrecovered failed project attempt are `NeedsAttention`;
- an explicit successful terminal outcome with complete evidence is `Healthy`;
- missing/degraded history, no meaningful evidence for inactive or terminal records, or no reliable terminal result is `Unknown`;
- stale/abandoned sessions are not `Working` and require attention;
- recovered tooling failures and successful workarounds remain findings; they do not turn a successful project into a failed project;
- an unresolved tooling block may be `Blocked` only when it actually blocks the project attempt;
- qualification, test, and internal records cannot create production projects; they remain diagnostic/system evidence.

The projection distinguishes last meaningful activity, latest attempt, last successful validation, last failure/problem, and current operation as separate values. It uses persisted Unix milliseconds, then sequence, then stable event ID for deterministic ordering. It never calls a model or infers absent evidence.
## Tooling Findings

`ToolingFinding` is a first-class derived owner-facing concept, not an `AgentIssue` alias and not a project result. Deterministic sources include explicit capability/limitation/workaround events and classified tooling, stall, redundant-work, context, and recovered infrastructure issues. Retry evidence is included only when it is explicitly excessive/repeated or represented by a qualifying retry issue; an ordinary single retry is not a finding. Vague “this might be inefficient” text is not sufficient.

Each finding has a stable identity derived from a trustworthy fingerprint, capability, or causal-issue identity, with an explicit operation key as a discriminator. If no trustworthy cross-run identity exists, the record identity is used and the occurrence remains separate; summaries and error codes alone never merge records. Similar findings with different stable identities remain separate. Each issue contributes one bounded occurrence, with all supporting event IDs retained; direct qualifying tooling events contribute their own occurrences. Each aggregate retains every bounded underlying occurrence with project, run/session/logical-agent identity, timestamp, operation, workaround, confidence, provenance, supporting event IDs, evidence IDs, recovery state, error code, bounded output/diagnostics, retry/delay/runtime/repeated-work measurements, and missing-evidence markers.

Aggregation metadata is calculated from all observed occurrences even when the presentation occurrence list is bounded, and the retained slice is newest-first. A task may pass while producing a recovered-failure or successful-workaround finding. `ProductionWorkFailed` and `RecoverySucceeded` are explicit fields. Aggregation spans projects, sessions, and logical agents only by the stable finding identity; occurrence records are never discarded merely because an aggregate exists.

## Assessment contract

`ProjectObservabilityProjection.BuildAssessment` emits `rimliaison-tooling-assessment/v1`. It is designed to be handed directly to an engineering assessment agent. It contains:

- executive summary;
- finding classification, stable identity, confidence, observed facts, derived interpretation, and separately marked suggested investigation areas;
- production-failure and recovery flags;
- workaround, operation, project, likely component owners (each value marked `observed:` or `derived:`), commands, arguments, error codes, supporting event IDs, evidence IDs, and recovery attempts;
- retries, measured added delay, runtime launches, bounded stdout/stderr/diagnostics, and measured token counts only when present;
- validation/build/runtime impact;
- tool/repository/version/fingerprint and environment maps when captured;
- recurrence aggregates with occurrence/project/logical-agent counts;
- evidence completeness and explicitly missing evidence.

The contract deliberately does not invent root cause, ownership, impact, timing, token metrics, or recommendations. Suggested investigation areas are derived prompts for assessment, never observed fact. `ToolingAssessmentHandoff.Prepare` creates the single `Prepare tooling assessment` packet. It prefers a bounded clipboard representation and automatically falls back to exporting the complete JSON assessment when clipboard delivery is unavailable, unsafe, or diagnostically insufficient. Clipboard and export use the same semantic assessment; missing or omitted evidence is explicit. Diagnostic bundle export remains the raw-evidence fallback.

## Information architecture

### Overview

One row/card per canonical production project or meaningful tool. Show project, canonical state, action required, current/last meaningful operation, last attempt result, last meaningful activity time, concise current problem, completeness, and a small tooling-finding count. Any filter is visible and only hides rows.

### Project

Project-centered header from `ProjectObservabilityState`; canonical timeline first; unresolved project problems; validation/attempt history; tooling findings; active sessions and history as a drill-down. Raw event data, commands, traces, and evidence appear only after selecting an entry.

### Problems

Unresolved/actionable project problems first, with project, ownership/classification, severity, occurrence, recovery state, and trustworthy next action. Recovered/history is secondary. Tooling Findings are linked but do not inherit project-failure styling.

### System / Diagnostics

Infrastructure and RimLiaison/DevBridge/runtime health, storage/degraded state, qualification, reliability campaigns, generations, transactions, validation proofs/cache, provenance/fingerprints, Tooling Finding history/aggregates, Content Intelligence, and raw diagnostic evidence. Content Intelligence remains a secondary dedicated surface with project links; it does not dominate ordinary development status.

## Desktop implementation

`ProjectObservabilityProjection` is the state and finding authority. `ObservabilityDestinationPresenters` supplies owner-facing Overview, Project, Problems, and System labels without reading the store or deriving state. `ObservabilityMainForm` remains the WinForms host: it owns control construction, bounded refresh/reconciliation, identity-based selection, content/reliability actions, and assessment/diagnostic delivery. It does not decide project state or finding grouping.

The current form has not yet been decomposed into one presenter per surface. That decomposition is intentionally deferred rather than represented as unused abstractions. Existing controls are mounted under the shared content host, and compatibility views remain until their callers migrate.

No control may redefine project state, latest semantics, eligibility, or finding grouping. Filtering, selection, and row limits are presentation concerns; the canonical projection remains unfiltered and reports incomplete/degraded evidence explicitly.

## Migration status

1. Store schemas, diagnostic bundles, live subscriptions, and WinForms application choice remain compatible.
2. Canonical projection and adversarial deterministic tests are landed.
3. Compatibility `AllView.Production` and project detail consume canonical project state.
4. Overview, Project, Problems, and System labels are centralized in `ObservabilityDestinationPresenters`; the form still hosts the controls and actions.
5. Recommendations remain a compatibility surface while tooling findings provide the canonical assessment path.
6. Reliability and Content Intelligence remain secondary dedicated surfaces under the shared host, with project links.
7. Further form decomposition and deletion of compatibility views require presenter parity and caller migration; they are not represented by unused layers.

Compatibility constraints: preserve `rimtest-*`/`RIMTEST_*` identifiers, JSONL authority and retention behavior, canonical entity identity, bounded evidence/redaction, diagnostic bundle schemas, live-store subscriptions, and the existing WinForms application choice. No cosmetic framework replacement is part of this stage.
