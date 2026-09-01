# RimLiaison agent observability

The architecture-stage redesign is specified in
[`observability-redesign-architecture.md`](observability-redesign-architecture.md).
That document is authoritative for the canonical owner-facing projection and
the migration away from independent presentation projections; this document
remains the compatibility and persistence contract.

RimLiaison now keeps product observability state locally and independently of
OpenTelemetry. One `AgentObservabilityRun` represents a RimLiaison invocation,
and each mod session belongs to a durable logical development agent when the
caller supplies one. Analysis, research, implementation, testing, packaging,
recovery, and completion are stages on that logical agent; they are not
separate user-facing agents.

## Authoritative state

`RimLiaison.Observability.AgentObservabilityStore` owns the durable structured
records:

- `AgentEvent` records have stable event IDs, run/session/mod identity, an
  optional durable logical-agent identity, a sequence for deterministic
  ordering, lifecycle stage, summary, bounded structured data, and optional
  trace/span IDs.
- `AgentIssue` records have stable issue IDs, run/session/mod identity, the optional logical
  agent identity, timestamp, category, severity, component owner, blocking state, current and
  resolution state, concise summary, supporting event/evidence references, affected validation,
  recommendation, related tools/commands/files, retry/occurrence counts, and an explicit recovery
  event when recovered.
`AgentSnapshot` exposes the logical-agent identity when available, the mod display name, status,
current stage/activity, last credible activity timestamp, start/completion state, and failure state.
Working state is current evidence only: an active lifecycle snapshot must have activity within the
bounded `WorkingStalenessThreshold`; startup and reload reconcile terminal events and stale
snapshots instead of trusting persisted `running` state.
The store subscription callback drives the desktop presentation layer; the CLI and desktop both
display and persist these records without a telemetry backend.
Selected issue IDs can be converted to a bounded
`AgentDiagnosticBundle` containing only their supporting events and related
evidence.

`AgentObservabilityIntegrityValidator.Validate` is the cheap deterministic
development/qualification check for these invariants. A canonical entity
identity is shared by agents, events, issues, navigation, and detail queries;
logical-agent identity aggregates sessions and runs for lifecycle purposes;
legacy records without a supplied logical identity remain separate rather than
being guessed together. It reports unresolved owners, disconnected aliases,
missing activity evidence, duplicate Working snapshots, broken issue/event
references, unresolvable top-level navigation, and suspicious tool-subject
inversions instead of repairing them silently.

Diagnostic exports use the `rimliaison-agent-diagnostic-bundle/v2` contract.
`selectedIssueIds` and `selectedIssues` preserve the exact checkbox selection;
`correlatedIssueIds` and `correlatedIssues` are a separate, bounded causal
closure. The closure follows stable event, operation, trace/span,
transaction/workflow, and structured relationship identifiers, while keeping
the `(logicalAgentId, runId, agentId, modId)` identity boundary. It does not use a broad time window or dump every event from a run. The bundle also exposes structured
command, build/deployment, tool-operation, repository, environment, recovery,
trace, and correlation evidence. `completeness.status` and
`completeness.missingEvidence` make missing command output or build/compiler
diagnostics explicit instead of presenting an empty but apparently complete
export.

The canonical root is resolved by
`AgentObservabilityStorage.ResolveCanonicalRoot()`. On Windows the default is
the application's local-data directory (`%LOCALAPPDATA%\\RimLiaison\\observability`),
not the current directory, repository root, mod root, or worktree root. An
absolute `RIMLIAISON_OBSERVABILITY_DIR` value is an explicit runtime override;
relative values are ignored so they cannot silently become repository-local
state. The CLI, desktop, issue detector, bundle builder, and runtime all use
the same store instance/root mechanism.

The current JSONL layout is application-level (`events.jsonl`, `issues.jsonl`,
`agents.jsonl`, and the sequence metadata under the canonical root). Run,
session, logical-agent, mod, event, issue, and optional trace/span identities
remain in every new record, so records from multiple runs share one transport
without becoming ambiguous. Records written before `logicalAgentId` existed
use a conservative `(runId, agentId)` fallback and are never merged merely
because their mod names match. The store uses per-file locks, a shared
sequence counter, atomic compaction, and a bounded file watcher refresh. A
desktop process therefore hydrates the same persisted records written by a
runtime process and receives cross-process updates without a local service or
telemetry backend.

## Retention and degraded state

The store is bounded by `MaximumEvents`, `MaximumIssues`, `MaximumAgents`,
`MaximumPersistedBytes`, `MaximumEvidenceEntries`, and the per-value byte limits
in `AgentObservabilityOptions`. JSONL compaction is threshold-triggered with
hysteresis rather than rewriting on every event; shutdown performs one final
maintenance pass. Compaction writes a bounded temporary file and atomically
replaces the primary file. Per-file locks serialize writers and compaction.

Retention prefers unresolved issues, active agents, recent events, and evidence
referenced by unresolved issues. Recovered issues and terminal agents remain
available while within the configured limits; older history is the first
candidate for removal. Missing, malformed, truncated, oversized, or temporarily
locked records are skipped without invalidating current runtime state. The
desktop reports this as degraded or partially unavailable state instead of
presenting incomplete history as complete.

The desktop starts with a bounded recent hydration so the window remains
responsive. The `History` action explicitly requests older records. Live records
continue through the watcher/fallback refresh path, and the status line exposes
loading, delayed, and degraded storage health.

Desktop startup failures are written as bounded text diagnostics under
`%LOCALAPPDATA%\\RimLiaison\\diagnostics`, outside any repository or mod
worktree. The UI displays that location and exits nonzero. `Open Observability
UI.cmd` treats a nonzero compiled-UI exit as abnormal, prints the exit code,
and pauses so the diagnostic location remains visible.

An unscoped desktop view aggregates sessions from all runs in that shared
store, including runs that arrive while the window is open. Navigation groups
top-level workspaces by `(entityType, canonicalEntityId)`, never by run,
session, process, correlation ID, timestamp, or display name. Canonical IDs are
trimmed, case-folded, and use `/` separators so normal Windows path spelling
variations remain one identity. Mod workspaces use canonical mod identity;
RimLiaison and related infrastructure use stable tooling identities. The tab
represents the newest active session, or the newest finished session when none
remain active. Callers that need a single-run history view can construct
`AgentObservabilityUi` with an explicit `runId`; that scope is preserved for
both initial hydration and live updates.

The persisted entity schema is carried by each agent, event, and issue:
`entityType`, `canonicalEntityId`, and `displayName`. The taxonomy is explicit:
`mod`, `tool`, `infrastructure`, `fixture`, `test`, `agent`, `user`,
`operator`, `process`, `session`, `run`, `activity`, `event`, `runtime`, and
`unknown`. Only a structurally valid `mod:<id>` or `tool:<id>` with
`workloadKind=production` can appear as a top-level navigation workspace;
qualification/test records remain available in activity and diagnostics but
cannot create production tabs. This rule is structural, not a display-name
blacklist.

Runtime/session/run identifiers remain separate fields for drill-down and
history. Legacy records are normalized through the centralized entity resolver
during load; known RimLiaison aliases, including `[Tool]` and temporary
worktree/test forms, become `tool:rimliaison`. Unclassified labels remain
`unknown` and are never promoted to a top-level workspace. Entity records are
upgraded to the current `v2` schema during migration. When normalization or
repeated record snapshots are found, startup rewrites the canonical JSONL files
under their existing per-file locks. It retains distinct sessions, events, and
issues while collapsing repeated snapshots by their existing record identity; a
locked or read-only store is left usable in memory and retried on the next
startup. A second startup is a no-op once the files are normalized.
The CLI carries an upstream worker identity from
`RIMLIAISON_LOGICAL_AGENT_ID` (or the compatibility alias
`RIMLIAISON_WORKER_ID`) into each new session. Separate concurrent workers
retain different session identities beneath the same tooling workspace.

## CLI subject attribution

The CLI resolves one subject identity before creating the observability session.
The executor remains `RimLiaison` in command/tool metadata and component ownership;
it is not used as the subject when a project target is present.

Available target inputs are command-scoped:

- ordinary commands use the canonical stack manifest discovered from the current
  directory, or the explicit `--rimcontext-root`/`--root` path where that command
  supports one;
- a valid stack manifest supplies the explicit project identity and repository root;
- a mod root supplies `About/About.xml` package ID and display name;
- repository origin is the stable fallback when package metadata is absent;
- `devBridgeProject` is runtime/tool configuration, not a subject inference by itself.

Resolution first validates the supplied or current project root through stack discovery.
Within that root, `About.xml` package ID is authoritative; the manifest project is the
explicit project fallback, followed by repository identity. A root must have a valid
project manifest or mod metadata before it can become a `mod` subject. The RimLiaison
repository is recognized by its repository identity or source layout and remains
`tool:rimliaison`. Unscoped administration and diagnostics therefore remain tool-scoped.
The resulting `entityType` and `canonicalEntityId` are created once at the CLI boundary;
legacy `ModId` values remain compatibility data and never override them. Startup can
reassociate a legacy RimLiaison-tool record only when its structured project/repository
evidence matches an already-known canonical mod; otherwise historical activity is
preserved unchanged.

## Desktop surface

`RimLiaison.Desktop` is the graphical consumer of the store. It is a native
WinForms `net8.0-windows` application selected because RimLiaison is already a
C#/.NET Windows toolchain and has no existing web or cross-platform frontend
runtime to reuse. The form keeps navigation and filtering local to the
store-backed `AgentObservabilityUi` presentation layer:

Each `AgentObservabilitySession` represents one runtime activity within a run.
Sessions may have distinct logical worker, process, and correlation identities,
but those values are never navigation/workspace identities.

The desktop keeps the primary owner-facing surfaces intentionally small:

- `All` is the default production overview. It groups top-level workspaces by
  stable entity identity and shows state, action required, current/last meaningful
  operation, last attempt result, last meaningful activity time, current problem,
  completeness, and tooling-finding count. The activity list is secondary context.
- `Issues` shows unresolved and recovered structured problems, supports bounded checkbox
  selection, opens supporting activity, and exposes `Copy to ChatGPT` plus full-bundle
  actions. Standard human triage is: select/check the current issue(s), click `Copy to
  ChatGPT`, paste the clipboard contents into ChatGPT. No export or LLM/browser step is
  required. Categories are rendered as project defects, validation blockers,
  tooling/infrastructure incidents, recovered incidents, or optional validation gaps.
- `Recommendations` remains a compatibility surface for non-blocking improvement
  records. New owner-facing tooling assessment is sourced from canonical Tooling Findings.
- Each `(entityType, canonicalEntityId)` gets one navigation item and an end-to-end
  entity view with stage progress, current activity, files, tools, commands,
  build/test results, problem state, and selectable current/past sessions.
- The Issues and Recommendations surfaces accept a bounded text filter for mod, tool,
  agent, stage, category, operation, owner, and evidence terms. A filter hides rows;
  it never changes canonical project state.

The desktop presentation uses persisted Unix-millisecond timestamps from events,
issues, and agent snapshots. Production and activity rows sort by numeric timestamp
descending; invalid or missing values sort last, with stable identity tie-breakers.
Local date/time formatting is applied only after sorting. Owner-facing column labels
use explicit meanings such as `Last meaningful activity`, `Last attempt`,
`Event time`, `Meaningful event`, `Current problem`, and `Tooling findings`.

Issues and recommendations are projected as groups, not repeated display rows.
Grouping uses a centralized identity: normalized persisted fingerprint first,
then capability or operation identity; records without a trustworthy stable key
remain separate. Each parent retains the newest record and exposes occurrence
records in newest-first order. Agent-sharing counts use only distinct supplied
logical-agent identities. Generated session/agent IDs and unknown identities
do not become agents; those records use occurrence wording instead.

The store subscription is coalesced through a bounded WinForms refresh timer,
so new authoritative runtime events update the window without forcing the
user's scroll position. The timer also polls the shared store as a bounded
fallback if a host does not deliver file-watcher notifications. Issue
navigation uses stable event IDs; no view switch, filter, ChatGPT handoff, or bundle preparation
invokes an LLM. OpenTelemetry remains optional instrumentation for correlation
and export only.

The ChatGPT handoff is compact, bounded to 64,000 characters, redacted, and causal:
it labels primary/root failure, propagation, and top-level workflow separately, then
includes the selected issue identity, date/state/blocking fields, operation/stage,
owner/reason, process paths, exit/timeout/cancel state, bounded stdout/stderr/
diagnostic output, repository and tool versions, evidence references, retries,
recovery, shared-impact counts, and explicit complete/incomplete evidence status.
Multiple checked issues are included in one handoff, with a maximum of eight.
Missing evidence is reported rather than inferred; legacy/session-scoped identities
cannot produce a trustworthy durable-agent impact count. Clipboard failure leaves
selection state unchanged. Full diagnostic JSON remains the fallback when the
compact handoff is insufficient.

Events, issues, and agent snapshots use bounded append-only JSONL files with
compaction. Output excerpts, summaries, commands, and arbitrary event data are
bounded and common credential fields are redacted. Diagnostic bundles are
created in memory; consequential process and build output is retained as
bounded, redacted evidence files under the canonical observability root rather
than in event JSON. Evidence persistence is best effort and cannot fail the
observed command. Only the desktop's explicit Save dialog writes a diagnostic
export, including when the user deliberately chooses a path inside a mod
worktree. In the Issues view, checked issues directly enable `Export diagnostic
bundle`; the action rebuilds the current selection before opening the Save
dialog, and the UI reports complete versus incomplete evidence afterward.

DevBridge2 owns raw build execution and bounded compiler/MSBuild capture. Its
`scripts/mod-test.ps1` `devbridge-mod-development/v1` response carries the
bounded build command, source project, staging path, configuration, exit code,
timeout state, compiler output, explicit `outputTruncated` state, and
transaction/workflow IDs. The nested failure projection repeats the stage,
command, exit code, error code/message, output, and truncation state so older
consumers can still diagnose a failed build. The pinned cross-stack fixture
executes this real owner serializer with an intentionally failing C# project.

RimLiaison's adapter parses and persists those fields as canonical,
identity-safe evidence. Event records retain only small excerpts and structured
identifiers so the event-data byte bound cannot replace the complete event with
a generic truncation sentinel; the full owner-bounded text is reloaded through
the evidence IDs. The observability store owns bounded evidence reload and
causal grouping; the desktop/diagnostic bundle layer assembles the user-facing
`rimliaison-agent-diagnostic-bundle/v2`. A compiler failure such as `CS0246`
therefore remains diagnosable without another log lookup, while an explicit
truncation flag is preserved rather than being mistaken for missing evidence.

`.rimdev/observability` is legacy repository-local state from older builds. It
is not imported, rewritten, or treated as authoritative by the new default.
The repository's ignore entry remains defensive for stale legacy files, but
worktree cleanliness no longer depends on Git ignoring active observability.

## Runtime coverage

The runtime emits lifecycle events from `CliApplication`, file activity from
Git change discovery, tool/command events from the DevBridge process transport,
build events from the DevBridge development transaction, test events from the
catalog recipe runner, and retry/recovery events from the existing recipe
fallback path. These hooks do not perform model calls or add narration to model
context.

Clear failures are detected deterministically from tool exceptions, nonzero
commands, timeouts, build/test failures, and failed-agent state. Conservative
heuristics identify repeated failed actions, repeated searches/file inspection,
explicit long waits, workarounds, tool limitations, context issues, and
integration failures. A single ordinary retry is retained as lifecycle evidence
but does not become a Tooling Finding; retry findings require explicit repeated/
excessive evidence. Low-confidence repeated work is reported as
“Potential repeated work” rather than as a definitive inefficiency claim.

Issues retain the original failure event IDs, retry/rework evidence, related
files/tools/commands, trace/span references, and—when recovered—the resolution
event. An abandoned session is finalized as a cancelled terminal failure so
the UI does not leave it looking permanently active. Issue event references,
event data, persisted JSONL, evidence collections, and repeated-work indexes
are bounded. Text values use key-aware and pattern-aware redaction for
credentials, tokens, authorization values, and common API-key formats before
they reach the store or a diagnostic bundle.

## Observed performance

`RimLiaisonContextBundleProvider` projects observed workflow samples from the
same canonical events. Each completed command also records a compact
`telemetry` summary containing repository/operation identity, build and
deployment counts, selected/executed tests, evidence reuse/invalidation,
runtime launches, retries, publication outcome, failure classification, and
expensive-operation count. It reports bounded median/p90 duration, validation
reuse, expensive-operation count, runtime launches, retry rate, and top failure
classification only after at least two completed workflows. Fewer samples are
marked `insufficient-data`; synthetic golden-workflow benchmarks remain under
`benchmarkSummary` and are never mixed with observed metrics. The projection
uses run identity and the store's bounded retention; it does not start a
telemetry service or create a second database.

## Production reliability burn-in

`AgentReliabilityProjection` derives a bounded campaign projection from the
canonical agent snapshots, events, issues, validation evidence, runtime
freshness fields, and efficiency profiles. It emits
`rimliaison-reliability-workflow/v1` workflow records and
`rimliaison-reliability-campaign/v1` campaign projections. Campaign identity
and configuration are persisted beside the canonical JSONL store in
`reliability-campaigns.jsonl`; the projection is recomputed rather than stored
as a second telemetry database.

Production workflows are eligible only when they are bound to the exact
promoted manifest fingerprint. The fingerprint uses
`rimliaison-toolchain-fingerprint/v1`, the promoted manifest version/state,
sorted component identities, promotion criteria, and qualification artifact
identity. Qualification workloads, experimental toolchains, unknown
fingerprints, and mismatches are excluded or marked incomplete; they never
silently contribute to the promoted campaign.

Infrastructure incidents are deduplicated within `(runId, agentId)` by
`CausalIssueKey ?? Fingerprint ?? issue:<id>`. A duplicate causal group is
recovered only when every record in that group is recovered. Structured
tooling/capability/integration failures are infrastructure incidents.
`MOD_DEFECT` and source/test failures remain source outcomes and do not fail
the infrastructure campaign. An unrecovered infrastructure incident or a
passing runtime check against stale/mismatched artifact identity is a campaign
failure.

Timing is evidence-only. RimLiaison wall time comes from the explicit command
duration or the matching `rimliaison-efficiency-profile/v1` outcome. Phase
timings remain separate cumulative observations and may overlap nested work;
they are never summed into total task time. Missing profiles produce null
timings, not fabricated zeros. Runtime generation, deployment, live
validation, controlled restart, and validation-proof reuse are reported only
when their structured evidence is present.

Campaign state is `PASS` only after the minimum completed promoted workflow
target and all required coverage are proven. It is `FAIL` for stale-runtime
or unrecovered-tooling failures, `COLLECTING` while valid evidence is below
the target or coverage is not yet complete, and `INCOMPLETE` when persisted
history is partial/degraded or required identity/evidence is unknown.
Concurrency is `established` only for overlapping complete workflow
intervals with distinct logical agents and a shared observed runtime
generation; otherwise it is `unknown` or `not-covered`, never inferred from
workflow count alone.

## OpenTelemetry

`OpenTelemetryAgentTelemetry` creates this hierarchy when enabled:

```text
rimliaison.run
└── rimliaison.mod-agent
    ├── rimliaison.devbridge.*
    ├── rimliaison.retry
    ├── rimliaison.build.deploy
    └── rimliaison.test.recipe.run
```

Stable attributes use the `rimliaison.*` namespace. Metrics use only
low-cardinality stage, operation, outcome, and issue-category labels; unique
IDs, paths, commands, and mod identifiers remain in product records or trace
attributes where needed.

Local instrumentation is on by default. Set `RIMLIAISON_OTEL_DISABLED=true` to
disable it. Set `RIMLIAISON_OTEL_ENDPOINT` or the standard
`OTEL_EXPORTER_OTLP_ENDPOINT` to opt into OTLP export. Provider/export setup and
export failures are caught and never change agent execution or the local event
and issue store.
