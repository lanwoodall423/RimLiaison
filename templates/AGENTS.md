# RimWorld Development

Use RimTest as the workflow entry point.

Normal loop:

1. Edit the smallest necessary change.
2. Run `rimtest affected --run --json`.
3. Inspect the compact result, then edit again.

Run `rimtest doctor --json` only when readiness is unknown. If it is blocked, follow its JSON
`nextAction` and use `rimtest init --json` with explicit missing manifest values until it reports
`status: "ready"`. Build-relevant affected runs automatically invoke DevBridge2's artifact
transaction before selected recipes. When a recipe fails, RimTest obtains a bounded,
current-generation, since-launch diagnostic source from DevBridge2 and passes it to RimError
automatically. If a diagnostic ID is returned, use `rimerror show <id>`.

When designing a new live-game test, validating UI behavior, or doing deeper in-game
inspection, use `rimtest capabilities --json` for bounded discovery through RimTest. It is
read-only and does not replace the normal `doctor` / `affected` loop.

For UI/layout/visual changes, functional tests alone are insufficient. After the relevant
RimTest suite passes, use RimTest's UI target/screenshot workflow to inspect the rendered
result and iterate before reporting PASS. Discover visible targets with
`rimtest ui targets --json`, then capture the smallest relevant target with
`rimtest ui screenshot --target <target-id> --json`.

Ownership:

- RimContext = source/Def/Harmony/dependency knowledge
- RimTest = test selection/orchestration/results
- DevBridge2 = lifecycle/profiles/generations/leases
- RimBridgeServer = live game
- RimError = diagnostics

Critical rules:

- DevBridge2 is the sole RimWorld lifecycle owner; never launch, kill, or restart RimWorld manually or through GABS.
- RimBridgeServer remains the live-game control surface, but do not persist ModsConfig/profile changes through it while DevBridge2 owns a generation.
- Never edit ModsConfig directly.
- Prefer compact JSON interfaces.
- Do not read Player.log directly during the normal workflow.
- Do not configure or discover Player.log, generation log, or RimError store paths for ordinary
  failures. Explicit RimError path options are fallback-only.
- `diagnosticStatus: "unavailable"` means the test failed but diagnostics were unavailable;
  it never means PASS. Infrastructure failures use `status: "infrastructure"`.
- Never treat a source-change PASS as valid unless
  `artifactFreshness.loadedArtifactFreshnessProven` is true.
- Recipe state reuse is opt-in catalog metadata. Missing or untrusted isolation metadata is the
  safe path; reuse is sequential and is invalidated by failures, reset/lease/readiness/generation
  uncertainty, ownership change, or cleanup failure.

For a real installed-game compatibility check, use the DevBridge2-owned
`scripts\live-stack-smoke.ps1 -Json` gate on a labeled self-hosted Windows runner. Its plan must
pass before the live command; hosted/offline validation must not claim RimWorld compatibility.
