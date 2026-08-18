# RimLiaison agent usage

RimLiaison is the default agent-facing workflow. Use the bounded recovery entrypoint when
readiness is unknown, then follow the normal affected loop:

```text
rimliaison doctor --json                 # only when readiness is unknown
edit
rimliaison affected --run --json
inspect the bounded result
edit again
```

`rimliaison affected --run --json` owns changed-file impact selection, test selection, execution,
DevBridge2 coordination, and bounded results. It calls `RimContext.Core` and `RimError.Core`
in-process; it does not launch `rimctx`, `rimerror`, or temporary JSON handoff files for the normal
path. DevBridge2 remains the external lifecycle/deployment boundary and RimBridgeServer remains the
external live-game control boundary.

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
targeted evidence over whole-screen image capture.
