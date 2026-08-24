# RimLiaison agent observability

RimLiaison now keeps product observability state locally and independently of
OpenTelemetry. One `AgentObservabilityRun` represents a RimLiaison invocation,
and each mod is represented by one `AgentObservabilitySession`. Analysis,
research, implementation, testing, packaging, recovery, and completion are
stages on that same mod agent; they are not separate user-facing agents.

## Authoritative state

`RimLiaison.Observability.AgentObservabilityStore` owns the durable structured
records:

- `AgentEvent` records have stable event IDs, run/agent/mod identity, a
  sequence for deterministic ordering, lifecycle stage, summary, bounded
  structured data, and optional trace/span IDs.
- `AgentIssue` records have stable issue IDs, supporting event IDs, category,
  severity, related tools/commands/files, retry/occurrence counts, and an
  explicit recovery event when recovered.
- `AgentSnapshot` exposes the mod display name, status, current stage/activity,
  start/completion state, and failure state.

The store supports all-event, issues-only, and agent-scoped queries. Its
subscription callback drives the desktop presentation layer; the CLI and
desktop both display and persist these records without a telemetry backend.
Selected issue IDs can be converted to a bounded
`AgentDiagnosticBundle` containing only their supporting events and related
evidence.

Diagnostic exports use the `rimliaison-agent-diagnostic-bundle/v2` contract.
`selectedIssueIds` and `selectedIssues` preserve the exact checkbox selection;
`correlatedIssueIds` and `correlatedIssues` are a separate, bounded causal
closure. The closure follows stable event, operation, trace/span,
transaction/workflow, and structured relationship identifiers, while keeping
the `(runId, agentId, modId)` identity boundary. It does not use a broad time
window or dump every event from a run. The bundle also exposes structured
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
agent, mod, event, issue, and optional trace/span identities remain in every
record, so records from multiple runs share one transport without becoming
ambiguous. The store uses per-file locks, a shared sequence counter, atomic
compaction, and a bounded file watcher refresh. A desktop process therefore
hydrates the same persisted records written by a runtime process and receives
cross-process updates without a local service or telemetry backend.

An unscoped desktop view aggregates agents from all runs in that shared store,
including runs that arrive while the window is open. Navigation groups those
invocations by stable mod identity, so repeated commands for `Wildlife`, for
example, remain one tab. The tab represents the newest active invocation, or
the newest finished invocation when none remain active. Callers that need a
single-run history view can construct `AgentObservabilityUi` with an explicit
`runId`; that scope is preserved for both initial hydration and live updates.

## Desktop surface

`RimLiaison.Desktop` is the graphical consumer of the store. It is a native
WinForms `net8.0-windows` application selected because RimLiaison is already a
C#/.NET Windows toolchain and has no existing web or cross-platform frontend
runtime to reuse. The form keeps navigation and filtering local to the
store-backed `AgentObservabilityUi` presentation layer:

One `AgentObservabilitySession` is created per mod within a run. That same
session owns analysis, research, implementation, testing, packaging, recovery,
and completion; creating a second role-specific agent for the same mod returns
the existing session.

- `All` is the default chronological activity view and shows concurrent mod
  agents together.
- `Issues` shows unresolved and recovered structured issues, supports multiple
  selection, opens supporting activity, and prepares/copies/exports assessment
  bundles.
- Each mod gets a navigation item and an end-to-end agent view with stage
  progress, current activity, files, tools, commands, build/test results, and
  issue state.

The store subscription is coalesced through a bounded WinForms refresh timer,
so new authoritative runtime events update the window without forcing the
user's scroll position. The timer also polls the shared store as a bounded
fallback if a host does not deliver file-watcher notifications. Issue
navigation uses stable event IDs; no view switch, filter, or bundle preparation
invokes an LLM. OpenTelemetry remains optional instrumentation for correlation
and export only.

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
integration failures. Low-confidence repeated work is reported as
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
