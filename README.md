# RimLiaison

RimLiaison is the canonical agent-facing RimWorld development toolchain. The normal loop is:

```text
edit
→ rimliaison affected --run --json
→ inspect the bounded result
→ edit again
```

Use `rimliaison doctor --json` as the bounded onboarding/recovery entrypoint when readiness is
unknown, and follow its `nextAction`. `rimliaison init`, `capabilities`, `ui`, `affected`, and the
catalog/recipe commands retain their existing names and semantics. `rimtest.cmd` remains a thin,
silent compatibility alias for old automation; it forwards every argument to `rimliaison.cmd`.

## Ownership

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

RimContext (`rimctx`) and RimError (`rimerror`) are narrow direct tools for drill-down or
debugging. Start with RimLiaison; do not probe the lower-level CLIs before the normal workflow or
its `nextAction` directs you there. DevBridge2 remains the lifecycle/deployment owner and
RimBridgeServer remains a separate live-game project.

For UI or visual changes, use the semantic UI surface: run the affected workflow, enumerate with
`rimliaison ui targets --json`, then capture the smallest relevant target with
`rimliaison ui screenshot --target <target-id> --json`. Inspect that targeted evidence; do not
capture the whole screen when a smaller semantic target is available.

The frontend is `RimLiaison.Cli`, with the canonical `rimliaison` executable and wrapper. The
solution is [RimLiaison.sln](RimLiaison.sln). Stable `rimtest-*` JSON schemas, `RIMTEST_*`
environment/error identifiers, `.rimdev`, catalog fields, and DevBridge protocol fields remain
unchanged for compatibility.

Detailed catalog behavior is documented in [TestCatalog/README.md](TestCatalog/README.md). The
maintained target-repository handoff is [templates/AGENTS.md](templates/AGENTS.md), and stack
manifest onboarding is documented in [docs/stack-manifest.md](docs/stack-manifest.md).

## Offline validation

```text
dotnet build RimLiaison.sln --configuration Release
dotnet run --project tests/RimContext.Tests/RimContext.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/RimError.Core.Tests/RimError.Core.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tests/RimLiaison.Tests/RimLiaison.Tests.csproj --configuration Release --no-build --no-restore
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```

The composition gate uses the pinned external DevBridge2 fake host. It proves the no-RimWorld
composition and bounded contracts; real RimWorld/RimBridgeServer smoke remains owned by DevBridge2.
