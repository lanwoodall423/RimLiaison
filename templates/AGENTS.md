# RimWorld Development

Use `rimliaison` as the workflow entrypoint. `rimtest` remains a silent compatibility alias for
older target repositories and automation.

Active edit loop:

1. Edit the smallest necessary change.
2. Run `rimliaison affected --run --fail-fast --json`.
3. Fix the first failure immediately and repeat.

Once the change is stable, run `rimliaison affected --run --json` as the complete pre-submit
validation. A fail-fast PASS still proves that every selected test executed; it only shortens a
failure path.

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
when a smaller target is available.

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
