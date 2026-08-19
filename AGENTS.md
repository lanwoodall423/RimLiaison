# RimLiaison agent handoff

Use `rimliaison` as the canonical agent-facing entrypoint. During an active edit iteration, use
the opt-in fail-fast path so the first trustworthy ordinary test failure returns promptly:

```text
edit -> rimliaison affected --run --fail-fast --json -> fix immediately on failure -> repeat
```

Once the change is stable, run `rimliaison affected --run --json` as the complete pre-submit
validation. A fail-fast PASS is never a partial PASS: it proves that every selected test ran.

Use `rimliaison doctor --json` when readiness is unknown and follow its bounded `nextAction`.
Use `rimliaison capabilities --json` for live capability discovery. For UI evidence, enumerate
semantic targets with `rimliaison ui targets --json` and capture only the smallest relevant target
with `rimliaison ui screenshot --target <target-id> --json`.

`rimtest.cmd` is retained as a silent compatibility alias and forwards to the same implementation.
It is not a second orchestration frontend. `rimctx` and `rimerror` are direct drill-down/debugging
tools only; begin with RimLiaison and use their `nextAction` when directed.

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

DevBridge2 owns RimWorld lifecycle, profiles, generations, readiness, leases, deployment, and
recovery. Do not launch RimWorld manually, edit ModsConfig, read Player.log directly, or bypass
the owner workflow.

Preserve stable `rimtest-*` schemas, `RIMTEST_*` identifiers, `.rimdev` conventions, catalog
fields, bounded-output rules, and DevBridge contracts.

For this tooling repository, use the canonical solution and authoritative validation:

```text
dotnet build RimLiaison.sln --configuration Release
dotnet run --project tests/RimContext.Tests/RimContext.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/RimError.Core.Tests/RimError.Core.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tests/RimLiaison.Tests/RimLiaison.Tests.csproj --configuration Release --no-build --no-restore
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```
