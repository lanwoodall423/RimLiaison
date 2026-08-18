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

For UI/layout/visual changes, functional tests alone are insufficient. After the relevant
RimTest suite passes, use `rimtest ui targets --json` and a targeted
`rimtest ui screenshot --target <target-id> --json` capture to inspect the rendered result and
iterate before reporting PASS.

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
