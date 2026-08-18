# RimWorld Development

Use RimTest as the workflow entry point.

Normal loop:

1. `rimtest doctor --json` when environment readiness is unknown.
2. If doctor is blocked, follow its JSON `nextAction`; use `rimtest init --json` with explicit
   missing manifest values and repeat doctor until it reports `status: "ready"`.
3. Use RimContext before broad source inspection.
4. Make the smallest code change.
5. `rimtest affected --run --json`.
6. If a diagnostic ID is returned, use `rimerror show <id>`.
7. Use owner tools directly only when deeper inspection is required.

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
