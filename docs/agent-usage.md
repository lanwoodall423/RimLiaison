# RimLiaison agent usage

RimLiaison is the default agent-facing workflow. Use the bounded recovery entrypoint when
readiness is unknown, then use the short feedback loop while editing:

```text
rimliaison doctor --json                 # only when readiness is unknown
edit
rimliaison affected --run --fail-fast --json
fix immediately on failure
repeat

# once stable, run the complete pre-submit validation
rimliaison affected --run --json
```

Both affected commands own changed-file impact selection, test selection, execution,
DevBridge2 coordination, and bounded results. It calls `RimContext.Core` and `RimError.Core`
in-process; it does not launch `rimctx`, `rimerror`, or temporary JSON handoff files for the normal
path. DevBridge2 remains the external lifecycle/deployment boundary and RimBridgeServer remains the
external live-game control boundary.

When an agent needs cross-stack state without starting a validation run, request the read-only
context bundle:

```text
rimliaison context --json
rimliaison context --json --verbose       # same facts, indented and with empty collections
```

The bundle's `ownership`, section `status`, `staleReasons`, and `decisions` fields are the handoff
contract. Treat `unknown`, `unavailable`, and `stale` as explicit uncertainty; follow a supplied
`nextAction` or owner rather than filling in the missing fact. Use `rimctx context --json` only for
direct static-context debugging. The full contract is in [docs/context-bundle.md](context-bundle.md).

## Minimal agent handoff

Ask for `rimliaison context --json` once at task start. The compact bundle is the startup handoff:
`agentSummary` puts blockers, actions, meaningful changes, reusable evidence, and deployment
correspondence first; the sections identify the project/mod, Git state, DevBridge2 deployment and
runtime state, selected/executed/reused tests, and owner-specific next actions. Use `--verbose` for
complete changed-file, hygiene, execution, and evidence detail.

After editing, ask RimTest for the affected validation it owns:

```text
rimliaison affected --run --fail-fast --json   # short edit loop
rimliaison affected --run --json               # complete pre-submit validation
rimliaison publish check --json                # read-only publication reuse gate
```

Do not run tests manually to replace the selector, launch RimWorld outside DevBridge2, or treat a
DevBridge2/lease/bridge/readiness error as a mod failure. Runtime-required validation uses the
catalog's Quicktest recipe and its artifact-freshness/generation evidence. Publication reuses
matching evidence and does not rerun expensive validation merely because Git is being published.
Generated observability, profiles, indexes, and proof records belong to their ignored or external
owners; they are not source changes and should not be committed.

For an affected run, Tooling also owns routine prerequisite recovery: safe descriptor reconciliation,
one partial-index rebuild/retry, bounded readiness recovery, routine lease acquisition/release, and
transactional viewport restoration. Inspect `orchestration` in the JSON result for separate
source/build, static/test, deployment/artifact, runtime, and infrastructure dimensions. In
particular, `artifactFreshness.evaluationStatus=NOT_EVALUATED` means the transaction never reached
freshness evaluation; it is not evidence of a stale deployed artifact. Only ambiguity, active
contention, unsafe or unwritable state, owner refusal, or another result explicitly marked as
requiring intervention should stop for manual repair.

Affected JSON includes additive `validationDiagnosis` evidence for the chain
`source -> build -> deploy -> artifact-freshness -> readiness -> lease -> runtime -> evidence`.
Use `result` as the agent-facing classification:

- `PASS`: every boundary completed with current-artifact evidence.
- `PROJECT_VALIDATION_FAILED`: project runtime executed and returned an assertion failure.
- `INFRASTRUCTURE_BLOCKED`: a tooling boundary stopped validation before a project assertion,
  or runtime infrastructure failed.
- `NOT_PROVEN`: evidence is incomplete or ambiguous; do not infer project failure.

`firstFailedBoundary`, `code`, `probableOwner`, `ownershipConfidence`,
`projectRuntimeExecuted`, `artifactFreshness`, `readiness`, `lease`, and `evidenceIds` explain
where the chain stopped. A `DEVELOPMENT_BUILD_FAILED` result before lease/runtime is
`INFRASTRUCTURE_BLOCKED`, never a mod failure. Follow `nextAction` and rerun the canonical
affected command after the owning boundary is repaired.

Shared DevBridge runtime transitions are bounded and owner-mediated. For a
`READINESS_IDENTITY_MISMATCH`, RimLiaison classifies the returned field before acting:
generation/process churn and same-root stale descriptor/profile registration are recovery
candidates; coordinator changes require same-root evidence; installation/root/owner,
protocol/schema, and unknown mismatches are hard failures. A recoverable mismatch records the
authoritative configured root and responding identity, refreshes/reconciles through DevBridge2,
waits for a fresh READY generation, rechecks the source fingerprint, and retries the complete
development/freshness transaction up to three times. Successful recovery reports `recovered`;
refusal or exhaustion reports `INFRASTRUCTURE_FAILURE` with `NOT_RUN`, the mismatch details, and
the recovery attempt count. The old generation, lease, artifact proof, and runtime evidence are
never reused. Agents must not kill, restart, or take over another valid DevBridge/RimWorld owner.

### Validation capability gaps

Catalog tests may declare `requiredCapabilities`. RimLiaison negotiates these through the
read-only DevBridge capability registry before recipe execution. A missing or incompatible
capability is `status: "blocked"` / `CAPABILITY GAP`, not a mod failure:

- Never change mod production behavior to compensate for an unavailable validation capability.
- Never claim a mod failed when `operationAttempted` is `false`.
- Report the exact capability ID and probable owner from `capabilityBlocker`.
- Do not repeatedly retry a capability discovery result that proves the capability absent.
- A capability blocker prevents claiming validation completion, but is not negative evidence about the mod.

## Human multi-repository workflow

For routine work that does not need an agent to perform each Git/build/deploy step, use the
guarded `rimdev` surface:

```text
rimdev status
rimdev all                 # sync, affected test, build, deploy, push; never merges
rimdev merge               # show one safe PR candidate and ask before merging
rimdev merge --yes         # execute the exact printed plan explicitly
```

The full command and workspace contract is in [docs/rimdev.md](rimdev.md). `rimdev test` still
delegates affected selection and freshness to RimTest/RimLiaison; `rimdev build` and `rimdev deploy`
use the same repository manifests and DevBridge deployment metadata. A blocked repository is
reported with a beginner-friendly next action while independent repositories continue where safe.
For a double-click entrypoint, use [Open RimDev Terminal.cmd](../Open%20RimDev%20Terminal.cmd) and
the short [human workflow guide](HUMAN_WORKFLOW.md).

Without `--fail-fast`, ordinary test failures continue to aggregate as before. With `--fail-fast`,
the same selection, freshness, generation, lease, and ownership checks occur; after the first
trustworthy ordinary test failure, tests not yet started are left unlaunched. A PASS still requires
every selected test to have executed. Fail-fast may use bounded recent efficiency history only to
order tests inside the reuse planner's already-proven compatible groups. It never changes selected
membership or lifecycle boundaries; missing or incompatible history falls back to deterministic
planner order. The final non-fail-fast command remains the complete validation path.

## Narrow drill-down tools

Use `rimctx` only when the bounded RimLiaison result or a focused debugging task asks for static
context. It is a direct CLI over the same `RimContext.Core` API:

```text
rimctx definition ThingDef/MyWeapon --json
rimctx refs ThingDef/MyWeapon --json
rimctx harmony Verse.Pawn.Tick --json
rimctx file Source/CompWidget.cs --json
rimctx find CompWidget --limit 10 --json
```

Use `rimerror` only for a diagnostic handoff or direct diagnostic debugging:

```text
rimerror show <diagnostic-id> --json
rimerror latest --run <run-id> --json
```

Both direct CLIs retain their versioned JSON contracts and stable error codes. They are not a
recommended preflight before `rimliaison`.

## Bounded output and UI evidence

Keep JSON as the automation interface. If a result is truncated, narrow the selector or change the
explicit limit deliberately; do not request unrestricted source, logs, or screenshots.

For visual work, enumerate semantic targets with `rimliaison ui targets --json`, then capture only
the smallest relevant target with `rimliaison ui screenshot --target <target-id> --json`. Prefer
targeted evidence over whole-screen image capture. For responsive checks, add `--viewport wide`,
`--viewport narrow`, or `--viewport current` to request a temporary lease-bound viewport and
receive effective-dimension plus restoration evidence.
