# RimLiaison agent usage

Use the only ordinary agent workflow:

```text
rimliaison affected --run --fail-fast --json
```

This command coordinates affected selection, build/deploy, readiness, runtime validation, evidence
collection, classification, and cleanup. It emits structured production state; agents must not
scrape prose, invoke DevBridge commands, select a transaction consumer, or repair supporting
repositories.

`rimliaison preflight --json` is a read-only startup packet when readiness is unknown. Use
`rimliaison doctor --workspace --json` for diagnosis. Human administration—`doctor`, `status`,
`reset`, `recover`, qualification, and promotion—is exposed through RimLiaison; do not operate
the installed runtime independently.

The complete ownership contract is in [docs/architecture.md](architecture.md). `RimLiaison.Runtime`
contains the internal lifecycle/deployment implementation and remains modular. The runtime is
installed under `RimWorld\Mods` but is not an independently released DevBridge product.

`rimliaison preflight`, `affected`, and production release workflows perform a bounded static
workspace-binding check before expensive work. When diagnosis is needed, use
`rimliaison doctor --workspace --json` to audit every managed production project. Consume its
health, repairability, issue code, and `nextAction`; never hand-edit workspace paths, copy source
into `RimWorld\Mods`, create links, or bypass the production toolchain.

The affected command owns changed-file impact selection, test selection, execution, internal
`RimLiaison.Runtime` coordination, and bounded results. It calls `RimContext.Core` and
`RimError.Core` in-process; it does not launch `rimctx`, `rimerror`, or temporary JSON handoff files
for the normal path. `RimBridgeServer` remains the external live-game control boundary.

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

RimLiaison also produces a compact Execution Packet as part of preflight when the static index is
available. Consume it before broad repository reads, expand only its `rimctx://impact/<id>` handles
when needed, and let the affected command compare the predicted scope with the actual diff. The
packet is byte/entry bounded, provenance-backed, revision-aware, and advisory; actual diff impact
is authoritative for validation. A missing packet is explicit static-context unavailability, not a
runtime or mod failure.

Affected selection also emits a minimum-safe validation plan when current indexed impact is available.
The plan is actual-diff-driven and may only add catalog-required tests to the ordinary selector result.
Read `validationPlan.status`, `tier`, `required`, `additional`, source identity, and expansion reasons;
`incomplete` or `broader_canonical` means stay conservative. Do not remove a required entry without
the explicit accepted override recorded in the plan. Learned relationships are not assumptions:
they require causal evidence, are project-local by default, and must match current source/index and
framework/RimWorld identity. Prediction is useful for preparation, never proof of validation.

Generators may emit bounded shared `rimdev-validation-requirement/v1` records beside an
artifact. RimContext carries them into the plan without generator-specific execution code;
runtime-required records produce compact runtime requests containing the assertion, prerequisites,
required evidence, and excluded work. Exact current PASS evidence is attached to matching
requirements before scheduling; source, plan, index, project, and repository identity mismatches
never qualify for reuse.

### Lifecycle observability

The existing agent event stream records packet status and expansion, predicted versus actual
impact, why validation was required, validation outcomes, stale evidence, and learned or
administrative relationship changes. The desktop exposes these facts in the existing
`Execution / impact / validation` agent detail tab; it does not add an approval gate. Treat
unavailable metrics as unavailable. Stable task, project, source, index, packet, plan, relationship,
run, session, and logical-agent identities are the correlation keys for persistent history.

### Failure diagnosis and remediation precedents

Validation failures are represented as bounded `rimdev-failure-packet/v1` records. The packet
contains the failed subject, current execution identity, classification, error summary, changed
files, affected entities, dependencies, frameworks, and references to raw stack/log evidence.
RimError consumes this structured packet for diagnosis and reproduction/reduction guidance; it does
not rediscover repository state, execute tests, or own lifecycle/deployment decisions.

Only a completed PASS with matching repository, project, source, build, runtime, and test identity
may be stored as a `rimdev-remediation-precedent/v1`. Tentative or mismatched remediation is
never reused. Proven precedents are bounded in the same RimContext impact-learning JSONL store,
remain auditable, and may be marked ineligible through the exceptional administration API. The
observability stream records failure, diagnosis, precedent storage/reuse, identity, and
administrative decisions; the desktop surfaces these events as read-only diagnostic history.

### Content Intelligence

Content-development runs can capture reusable intent and validation without a second workflow:

```text
rimliaison affected --run --content-kind ThingDef --content-role "early-game ranged weapon" --json
rimliaison content query --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

The first command automatically writes `content-blueprint/v1` before affected selection and
`content-evidence/v1` after validation. Derived source, entity, dependency, source-identity, run,
session, and measured elapsed fields are attached when available; missing facts remain `null`.
Phase 2 fingerprints structural shape before semantic reuse, qualifies only with independent
objective evidence, replays history, and automatically promotes only data-only archetypes. An
attributable failure records usage evidence and quarantines the active archetype; fallback is
`RimContent -> precedent -> vanilla/reference -> novel`.
Shared proven precedent is the default across canonical-workspace projects. Project exclusions,
private assumptions, and constraints remain local metadata. The query is ranked and byte/limit
bounded and returns source references rather than source contents. See
[content-intelligence.md](content-intelligence.md).

The desktop Content Intelligence section is read-only during normal runs and updates
incrementally from the same events. Its administrative controls are bounded emergency actions
with audit events: quarantine, rollback, project exclusion, and source ineligibility. They do not
replace affected-test selection or introduce a human approval gate. Durable grouping uses the
producer's `logicalAgentId`; run/session IDs remain visible for diagnosis.
Generated observability, profiles, indexes, and proof records belong to their ignored or external
owners; they are not source changes and should not be committed.

For an affected run, RimLiaison owns routine prerequisite recovery through the same bounded
service used by `doctor`, `affected`, and release/promotion operations. Its fixed escalation
ladder is:

`normal -> RECONCILE -> COORDINATOR_RECYCLE -> FULL_RUNTIME_RESET -> ready -> retry once`

`RECONCILE` reconnects and re-probes. `COORDINATOR_RECYCLE` uses the managed coordinator
control plane and only the exact owned coordinator. `FULL_RUNTIME_RESET` uses the managed
runtime shutdown, RimWorld restart, wait-ready, and readiness verification commands. The
full reset is permitted only at `PRE_MUTATION`; after deployment, lease acquisition, or
assertions begin, recovery is conservative and never replays a stateful operation.
The original operation is retried at most once after readiness is proven. Successful internal
repair reports `toolchainRecovery` plus the compatibility `toolchainRecoveryCount` and
`toolchainRecoveryTypes` evidence without changing the project result.

Affected JSON includes additive `validationDiagnosis` evidence for the chain
`source -> build -> deploy -> artifact-freshness -> readiness -> lease -> runtime -> evidence`.
The canonical agent result is:

- `PASS`: validation completed with current-artifact evidence, with or without internal recovery.
- `MOD_FAILURE`: the toolchain executed and found a project compile, runtime assertion, or
  project-owned configuration/recipe failure. Fix the project and rerun.
- `TOOLCHAIN_FATAL`: RimLiaison exhausted one safe recovery cycle or could not prove identity,
  freshness, or replay safety. Stop project changes and report the attached handoff.

The failure object preserves the original code, classification, recovery attempt/result, last safe
checkpoint, workflow/transaction/lease/generation identities, and evidence references. Internal
codes remain diagnostic; agents do not decide ownership from them.

RimLiaison may recover a known legacy central project recipe only when the exact owner, schema, id,
and SHA-256 are proven. It records the legacy source and resolved identity; it never invents test
semantics. A genuinely missing project recipe is `MOD_FAILURE`. Missing toolchain builtin recipes
are reconciled as toolchain state, or become `TOOLCHAIN_FATAL` if qualification is ambiguous.

For timeouts or no-response results, RimLiaison first determines whether the request was
pre-mutation/idempotent or reconciles durable transaction evidence. It retries a stateful action
only with at-most-once proof. Otherwise it fails `TOOLCHAIN_FATAL`; agents must not replay it.

Agents must not manually run coordinator recovery, edit workspace bindings, copy runtime files,
acquire leases, choose slots/endpoints/generations, or invoke old DevBridge recovery commands.

### Production-first validation policy

Validation has exactly three classifications:

- `REQUIRED`: declared by task requirements, repository policy, or an explicit toolchain contract
  before validation starts. A failure blocks production. If infrastructure cannot execute it, the
  result is `VALIDATION_INCOMPLETE` with the owning blocker; it is never misreported as a mod
  failure.
- `BEST_EFFORT`: execute when available and relevant. An executed check that finds a genuine mod
  defect remains a mod failure. Missing tooling or unavailable execution is
  `OPTIONAL_VALIDATION_UNAVAILABLE` and does not block production.
- `RECOMMENDED`: newly discovered tests, deeper evidence, fault injection, observability,
  isolation, or efficiency opportunities. Record them as recommendations; they never block the
  current task.

Agents MUST NOT promote a discovered validation idea to `REQUIRED` during the same task. The
original task/repository/toolchain contract must have made it mandatory. The completion contract is
`PASS`: defined requirements satisfied and all executable `REQUIRED` validation passed. PASS does
not claim that every desirable validation exists or ran.

The default is: **Produce the mod; report tooling improvements separately.**

### Validation capability gaps

Catalog tests may declare `requiredCapabilities`. RimLiaison negotiates these through the
read-only DevBridge capability registry before recipe execution. A missing or incompatible
capability is `CAPABILITY GAP` for `REQUIRED`, and `OPTIONAL_VALIDATION_UNAVAILABLE` for
`BEST_EFFORT`/`RECOMMENDED`:


- Never change mod production behavior to compensate for an unavailable validation capability.
- Never claim a mod failed when `operationAttempted` is `false`.
- Report the exact capability ID, validation classification, component owner, evidence reference,
  and recommendation from the structured result.
- Do not repeatedly retry a capability discovery result that proves the capability absent.
- A required capability blocker prevents claiming PASS; an optional capability gap does not.

Observability persists issue/recommendation records in its JSONL store after the run ends. Each
record includes identity, category, owner, severity, blocking state, resolution state, evidence,
affected validation, and recommendation so the UI and later queries retain the distinction between
mod defects, tooling failures, unavailable optional checks, improvements, and production events.
### Production state and tooling separation

Every Golden Path run carries stable `modId`, `agentId`, `runId`, `sessionId`, current stage,
operation, blocking state, and completion result. RimLiaison emits state-change events to the
Observability store as work progresses; the UI does not need to scrape agent output. Lifecycle
generations, coordinator roots, handshake details, evidence paths, and process ownership remain
expandable diagnostic evidence.

RimLiaison owns orchestration and the production product; `RimLiaison.Runtime` owns lifecycle,
deployment, and readiness implementation; RimTest owns selection and validation; RimContext owns
compact static context; RimError owns diagnosis and classification. A bounded safe retry may run
once. Persistent infrastructure failure creates a tooling incident and never starts tooling-repository
development or mutates a supporting repo. Unrelated deterministic validation continues, and
successful evidence remains credited. Optional runtime capability gaps can still produce `PASS` with
a visible recommendation; required gaps block only the claims that depend on them.

Agents must finish the mod whenever defined requirements and REQUIRED validation permit it.
Report tooling opportunities separately with owner, capability gap, impact, evidence/reproduction,
and priority; do not promote a discovered recommendation to REQUIRED during the current task.
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
