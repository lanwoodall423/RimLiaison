# Agent usage

## Ordinary mod development

For normal iterative XML or C# RimWorld mod development, use the direct validation path:

```text
edit -> rimtest dev-check --json -> fix the reported mod defect -> repeat
```

When workspace resolution requires an explicit path:

```text
rimtest dev-check <workspace> --json
```

Interpret results as follows:

- `BUILD_FAIL`: fix compilation before continuing.
- `STATIC_FAIL`: fix structural or static problems.
- `RUNTIME_LOAD_FAIL`: inspect attributed mod, content, or runtime errors.
- `RUNTIME_LOAD_PASS`: the target package/content was proven through the isolated runtime path;
  it does not prove gameplay behavior that was not exercised.
- `INFRASTRUCTURE_FAIL`: distinguish environment/tooling failure from a mod failure.
- `CLEANUP_FAIL`: do not treat validation as healthy.

### Runtime layout selection (1.6 Unity / 1.7 Waylith)

The direct `dev-check` source candidate can inspect two **complete, mutually
exclusive** layouts before attempting to launch a game:

- 1.6 Unity: `RimWorldWin64.exe` with
  `RimWorldWin64_Data/Managed/Assembly-CSharp.dll`.
- 1.7 Waylith: `RimWorld.exe` with root-level
  `Assembly-CSharp.dll`, `Waylith.dll`, and `UnityStubs.dll`.

Both require the game's `Data` and `Mods` folders. Missing/mixed layouts
fail closed; an existing game folder is not sufficient proof. Set
`RIMWORLD_ROOT` only when the installed game is elsewhere.

On 1.7 the runtime-signal helper targets `net10.0` with installed Waylith
references and is staged in `1.7/Assemblies`; the first build therefore
requires a local .NET 10 SDK. An existing target mod built for 1.6 is
left in its declared `1.6/Assemblies` folder, rather than copied as a
supposed native 1.7 assembly. Waylith uses an isolated `-mod=` list and
`-Headless` with a temporary `-savedatafolder`; it does not read or
change the player's ordinary `ModsConfig.xml`. The helper build picks a
compatible .NET 10 SDK from an owned temporary directory, without modifying
the CLI repository's .NET 8 `global.json`.

**Waylith 1.7 does not relocate `Player.log` with `-savedatafolder`.**
It writes to `%USERPROFILE%/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log`.
The direct validator requires RimWorld to be closed first, records a
read-only prelaunch log snapshot and checks a postexit log's fresh startup
time, 1.7 version, exact log path, game DLL, unique temporary save-data
directory, exact `-mod=` IDs and `-Headless` command before reading it
as runtime evidence. An absent, stale, changed or unrelated log makes the
runtime check fail closed. The runner **never writes, deletes, rotates or
copies** the user's normal log. Files larger than 4 MiB are not accepted
as attributable logs in this diagnostic loop. The existing 1.6
isolated `-logfile` route is unchanged.

### Typed definitions and biome generation registration

The runtime signal checks changed `ThingDef` and `GenStepDef` names against
their distinct in-game Def databases. When a supported conditional
`BiomeDef.extraGenSteps` patch is present, it also confirms that the exact
`GenStepDef` is attached to the requested biome after game content loading.
Unsupported or malformed `extraGenSteps` patch shapes fail static validation
instead of silently becoming proof. A missing or false typed runtime response
is a load failure. This confirms registration, **not** that a map was
generated or any GenStep behavior was exercised.

These source changes are **not an assertion that 1.7 gameplay works**.
The required proof remains `BUILD_PASS`, `STATIC_PASS`,
`RUNTIME_LOAD_PASS`, `INFRASTRUCTURE_PASS` and `CLEANUP_PASS` for
the exact installed engine, with explicit mod/def/assembly signal evidence.
If an SDK, engine helper, or provably fresh runtime log cannot be
established, report infrastructure failure or incomplete runtime evidence,
never a mod PASS.

The new launch arguments and 1.7 engine/reference contract are described by
[Ludeon's 1.7 Modder Primer](https://docs.google.com/document/d/e/2PACX-1vSykw5_DYaaw42nEznI9zmBZVT_Vgg2bxYq_v2XWvAsHC3QSKFuDK2OyiQ5WC9jvVRUcrjpFt7xLLF5/pub).

For multi-project workspaces, `dev-check` builds one unambiguous production root and follows its
`ProjectReference` graph. Projects under obvious test, tooling, sample, benchmark, or example
paths are excluded unless a production project references them. Multiple unreferenced production
roots fail closed as `STATIC_FAIL`; the validator never guesses which assembly is the mod.

External DevBridge2 is not part of this ordinary inner loop; it is a transitional parity/reference
oracle only. RimLiaison's `candidate` command may prepare a staged `READY_TO_TEST` payload, but it
does not acquire a runtime lease, register a project, launch or stop RimWorld, edit the normal user
`ModsConfig`, mutate canonical `Mods`, or scrape `Player.log`. Routine runtime testing remains a
separate managed consumer operation.

`rimliaison candidate [<project> [<changed-path> ...]]` writes a candidate under
`%TEMP%\RimLiaison\candidates\<canonical-project-id>\<candidate-id>\`, with the runtime payload
in `runtime\` and the machine-readable manifest in `candidate.json`. The candidate contract is
`rimliaison-ready-to-test-candidate/v1` and ends in `state: READY_TO_TEST`. Its preparation is
source/build/package work only; the next managed-runtime operation consumes it and owns shared
RimWorld mutation.

Changing a mod does not require RimLiaison qualification or promotion. Promotion is required only
when the RimLiaison toolchain itself changes and an intentional new version replaces the installed
production toolchain.

## Native rimtest development-runner track (RL-RIMTEST-01A)

The source-only native runner entrypoint is separate from the promoted production toolchain and
from the legacy `rimtest.cmd` batch alias (which still forwards through `rimliaison.cmd`).
ProjectNexus runs declared executables directly on Windows: a batch alias or a development
checkout that is not present on the executor's PATH does **not** make `rimtest` available.

A **separately published** native apphost named `rimtest.exe` uses a restricted entrypoint:

- Only `rimtest.exe dev-check [<workspace>] --json` is admitted. No qualification, promotion,
  experimental, arbitrary-command or self-install invocation is available through this native
  name. A rejected command produces `rimtest-dev-runner/v1` blocked JSON and nonzero exit.
- With no workspace argument, the mod source is bound to the **invoking process working
  directory**, including a ProjectNexus-managed mod worktree. It does not default to the
  RimLiaison source checkout or the runner's own installation directory. The target must contain
  `About/About.xml`. A deliberate explicit workspace is resolved against the invocation cwd.
- Before invoking `dev-check`, the native runner requires a local sidecar
  `rimtest-dev-runner.json` in its own directory, with exactly four properties:
  `schemaVersion: "rimtest-dev-runner/v1"`, a lowercase 40-hex `sourceCommit`, and lowercase
  64-hex `runnerExeSha256` and `cliAssemblySha256` matching the actual `rimtest.exe` and
  sibling `rimliaison.dll` file bytes. Missing, malformed, duplicated, symlinked or mutated
  provenance blocks the normal managed entrypoint; the inspected identities are written to
  standard error. These two hashes check **the native apphost and CLI assembly bytes only**;
  they do not prove dependency-closure integrity, the true Git source revision or resistance
  to intentional local binary replacement. The later packager must validate and bind the
  real clean source commit, all dependency bytes and the trusted Windows installation before
  treating a runner as qualified for ProjectNexus execution.
- The ordinary `rimliaison.exe` and `dotnet rimliaison.dll` entrypoints remain unchanged;
  production routing still requires its own qualified and promoted manifest.

**Current delivery boundary:** This 01A source candidate defines the restricted native dispatch
and deterministic fixture tests only. It does not publish, install, register PATH or qualify a
native `rimtest.exe` and must **not** be treated as an available ProjectNexus check yet. Next:
a separately reviewed, reproducible .NET 8 native apphost packager with source/provenance
verification, safe account-local versioned deployment and a direct Windows process-spawn test.
Then requalify the installed Waylith-capable CLI, the exact NatureTiles source workspace and
the independent ProjectNexus `validation.rimtest` check under its own trust/review gates.
Never hand-copy a detached candidate binary into production or relabel a manual test as a native
ProjectNexus PASS. RimWorld 1.6/1.7 runtime and cleanup evidence remain separately required.

## Native rimtest package candidates (RL-RIMTEST-01B)

The candidate package service is separate from RimLiaison's promoted production toolchain.
An account-local candidate is staged under
`%LOCALAPPDATA%/RimLiaison/Rimtest/CandidatePackages/rimtest-<40-hex-source-commit>`.
The service uses a read-only Git worktree snapshot before and after package preparation:
the exact source HEAD must match, the source must remain clean, and a second source
observation must still match immediately before the non-replacing directory move.
The caller supplies a `dotnet publish` CLI output directory and its expected clean source
commit. The service checks for `rimliaison.exe`, `rimliaison.dll`, `rimliaison.deps.json` and
`rimliaison.runtimeconfig.json`, copies published dependencies without following links,
and renames the copied apphost to `rimtest.exe` without shipping the unrestricted
`rimliaison.exe`. It generates the four-field `rimtest-dev-runner/v1` sidecar and
a full-closure `rimliaison-cli-manifest/v1`, then verifies all file hashes.
The restricted native entrypoint independently re-verifies that complete closure
before every dev-check launch; changed dependencies, missing files or an invalid
manifest block startup. The hashes do not constitute a cryptographic publisher signature. If the
version directory already exists, it is never overwritten; failed work cleans only
its new temporary staging directory.

**Scope:** This is a source-only internal packaging primitive and deterministic fixture
coverage, not a published or qualified Windows `rimtest.exe`. An independently
reviewed exact .NET build must still prove the executable really came from the clean
source commit; matching package hashes are not build provenance or a security signature.
Packaging does not install, change PATH, run RimWorld, or bypass any ProjectNexus check.
The restricted native `rimtest.exe` dispatch, package-staging source, and Waylith-capable
runtime now coexist in this isolated candidate. They still require an actual combined
Windows compilation and regression pass, source-to-binary provenance verification,
separately authorized installation/PATH registration, and the exact NatureTiles
ProjectNexus runtime check. This does not make a native runner currently available.

## Portable recipe resolver contract tests

The built-in ecosystem recipe-resolution tests use short-lived synthetic project-owned
`.rimdev/recipes/*.json` fixtures. They verify exact canonical project owners,
recipe IDs, path conventions, schema and SHA-256 without relying on
`C:\\RimDev\\Repos` or any other developer-private external checkout.
A missing external source/recipe is explicitly tested as `PROJECT_RECIPE_NOT_FOUND`,
not treated as a successful real-world recipe. The synthetic tests do **not**
prove that DeferredRealityFramework, Frontier, InsightCanvas or KnowledgeFramework
is installed, that its actual recipe exists, or that any external mod will load.
Real external-project recipe availability, version identity and live RimWorld
qualification remain separate checks requiring genuine source/artifacts.

## Explicit release and qualification workflows

RimLiaison remains available for explicitly documented release, qualification, promotion, and other
workflows that genuinely require heavier orchestration. Use those commands only when that workflow
is the requested operation; they are not a prerequisite for ordinary mod changes.

The complete ownership contract is in [docs/architecture.md](architecture.md). `RimLiaison.Runtime`
contains the internal lifecycle/deployment implementation and remains modular. The installed
runtime is not an independently released DevBridge product.

## Advanced RimLiaison reference

The following sections document explicit RimLiaison workflows and their contracts. They do not
change the ordinary-development default above.

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
