# RimWorld Development

Use RimTest as the workflow entry point.

Normal loop:

1. `rimtest doctor --json` when environment readiness is unknown.
2. Use RimContext before broad source inspection.
3. Make the smallest code change.
4. `rimtest affected --run --json`.
5. If a diagnostic ID is returned, use `rimerror show <id>`.
6. Use owner tools directly only when deeper inspection is required.

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
