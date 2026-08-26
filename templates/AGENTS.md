# RimWorld Development

Use the narrow Golden Path for ordinary mod-development tasks:

1. Run `rimliaison preflight --json`.
2. Establish task and repository requirements.
3. Edit the mod.
4. Run `rimliaison golden-path --json` (also available as `rimliaison develop --json`).

Golden Path owns affected selection, build/deploy, Quicktest startup/readiness when required,
supported runtime validation, evidence, classification, Observability publication, and the
completion result. Continuously rely on structured production state; do not scrape natural-language
output.

Do not independently redesign, patch, or mutate supporting tooling repositories during a mod task.
RimLiaison may perform one bounded safe recovery retry. Persistent infrastructure problems become
separate structured incidents/recommendations. Continue unaffected validation and finish the mod
whenever defined requirements and REQUIRED validation permit it.

For a short edit loop when preflight is already known:

```text
rimliaison affected --run --fail-fast --json
```

Once stable, run the complete Golden Path.

For content-development tasks, provide only the semantic input that cannot be inferred:

```text
rimliaison affected --run --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

RimLiaison automatically captures `content-blueprint/v1` before selection and `content-evidence/v1`
after validation. Phase 2 fingerprints structure before reuse, requires independent objective
qualification and historical replay, and promotes only data-only archetypes. Attributable failures
quarantine the active archetype while preserving usage evidence; fallback is
`RimContent -> precedent -> vanilla/reference -> novel`. Project exclusions, private assumptions,
and constraints remain local.

Query shared proven references with:

```text
rimliaison content query --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

Use `rimctx content` only for direct static drill-down. Do not manually copy source into precedent
records; the system stores bounded metadata and stable source references.

The desktop Content Intelligence projection is incremental and bounded. It groups sessions by
durable `logicalAgentId`, retains run/session drill-down, and reports only exact recorded elapsed
and token metrics; unavailable values stay unavailable. Emergency quarantine, rollback, project
exclusion, and source-ineligibility controls write audit events and never replace normal validation
or add an approval gate.

Run `rimliaison doctor --json` only when readiness is unknown. If it is blocked, follow its JSON
`nextAction` and use `rimliaison init --json` with the explicit missing manifest values until it
reports `status: "ready"`. Build-relevant affected runs automatically use DevBridge2's owner
transaction before selected recipes. A failed recipe receives a bounded current-generation
diagnostic from DevBridge2 and RimError; use `rimerror show <id>` only when that handoff directs
you to a diagnostic.

For live-game discovery, use `rimliaison capabilities --json`. It is read-only and does not replace
the normal `doctor` / `affected` loop. For UI or visual changes, functional tests alone are insufficient:
run the functional workflow,
enumerate semantic targets with `rimliaison ui targets --json`, and capture the smallest relevant
target with `rimliaison ui screenshot --target <target-id> --json`. Do not capture the whole screen
when a smaller target is available. For responsive validation, request a temporary supported
viewport with `--viewport wide`, `--viewport narrow`, or `--viewport current`; RimLiaison captures
the effective dimensions and restores the prior live window state through the canonical lease.

Ownership:

```text
RimLiaison
  ├─ RimContext: static source/dependency/affected knowledge
  ├─ orchestration: test selection, execution, bounded results
  ├─ RimError: bounded diagnostic/root-cause analysis
  └─ DevBridge client
       ↓
     DevBridge2: lifecycle/deploy/generations/leases
       ↓
     RimBridgeServer: live in-game control/inspection
```

`rimctx` and `rimerror` are narrow direct tools for drill-down/debugging, not the default
workflow. DevBridge2 is the sole RimWorld lifecycle owner. Never launch, kill, or restart RimWorld
manually, edit ModsConfig, or read Player.log directly during normal development.

Preserve stable `rimtest-*` schemas, `RIMTEST_*` identifiers, `.rimdev` conventions, catalog
fields, and DevBridge protocol contracts. Treat
`artifactFreshness.loadedArtifactFreshnessProven: true` as required evidence for a source-change
PASS. Recipe reuse is opt-in catalog metadata and is invalidated by failure, reset, lease,
readiness, generation, ownership, or cleanup uncertainty.

Validation capability blockers are first-class results. When JSON reports `status: "blocked"` with
`errorCode: "VALIDATION_CAPABILITY_UNAVAILABLE"` or
`"VALIDATION_CAPABILITY_INCOMPATIBLE"`:

- Never modify mod production behavior because a validation capability is absent.
- Never claim the mod failed when the validation was not executable.
- Report the exact `requiredCapabilityId` and `probableOwner`.
- Do not repeatedly retry after capability discovery has proven the capability absent.
- A capability blocker prevents claiming validation completion, but is not negative evidence about the mod.
