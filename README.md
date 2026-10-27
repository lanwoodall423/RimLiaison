# RimLiaison

For ordinary iterative XML or C# RimWorld mod development, the default agent validation path is:

```text
edit
→ rimtest dev-check --json
→ fix BUILD/STATIC/RUNTIME mod defects
→ repeat
```

Use `rimtest dev-check <workspace> --json` when the workspace cannot be inferred. A
`RUNTIME_LOAD_PASS` proves the target package/content through the isolated runtime path; it does
not prove gameplay behavior that was not exercised. `INFRASTRUCTURE_FAIL` and `CLEANUP_FAIL` are
tooling/environment health results, not healthy mod validation.

RimLiaison and DevBridge2 are not required for this ordinary inner loop. RimLiaison remains
available for explicitly documented release, qualification, promotion, and other workflows that
genuinely require heavier orchestration. `rimtest.cmd` remains a thin compatibility alias and
forwards the command to the installed CLI.

Use `rimliaison doctor --json` for explicit RimLiaison workflow diagnosis. The existing
`rimliaison`, `rimctx`, and `rimerror` commands retain their names and semantics.

For routine human multi-repository Git/build/test/deploy/push work, use the guarded `rimdev`
workflow. See [docs/rimdev.md](docs/rimdev.md) for workspace discovery, command semantics, safety
rules, exit codes, and the explicit merge confirmation flow.

## For the repository owner

Double-click [Open RimDev Terminal.cmd](Open%20RimDev%20Terminal.cmd), then start with
`rimdev status`. The short beginner guide is [docs/HUMAN_WORKFLOW.md](docs/HUMAN_WORKFLOW.md).

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

The graphical frontend is `RimLiaison.Desktop`, a small WinForms `net8.0-windows` executable.
WinForms is the lowest-complexity fit here: the repository is already C#/.NET, the target
environment is Windows, and it adds no web server, browser runtime, or third-party UI framework.
For a double-click entrypoint, use [Open Observability UI.cmd](Open%20Observability%20UI.cmd);
it launches the Release executable when available and otherwise builds it with the .NET 8 SDK.
You can also launch it with `dotnet run --project src/RimLiaison.Desktop/RimLiaison.Desktop.csproj`.
The `RimLiaison.Cli` executable and wrapper remain available for automation and diagnostics. The
  desktop form consumes the shared application-level structured observability store directly;
  that runtime state lives outside mod worktrees. It does not call an LLM or require OpenTelemetry
  to render, filter, update, or prepare issue bundles. The solution is
[RimLiaison.sln](RimLiaison.sln). Stable `rimtest-*` JSON schemas, `RIMTEST_*`
environment/error identifiers, `.rimdev`, catalog fields, and DevBridge protocol fields remain
unchanged for compatibility.

Detailed catalog behavior is documented in [TestCatalog/README.md](TestCatalog/README.md). The
maintained target-repository handoff is [templates/AGENTS.md](templates/AGENTS.md), and stack
manifest onboarding is documented in [docs/stack-manifest.md](docs/stack-manifest.md).

Repository CI is change-aware. The planner in [docs/ci-validation.md](docs/ci-validation.md)
calculates the smallest safe validation set from the Git diff, while escalating uncertain or
boundary changes conservatively. Agents should use that selector rather than manually stacking
all internal suites.

## Offline validation

For repository changes, calculate the change-aware plan first and run only the selected components;
see [docs/ci-validation.md](docs/ci-validation.md). The proof-aware executor reuses only complete
successful deterministic validation when its content fingerprint is unchanged; see
[docs/validation-proofs.md](docs/validation-proofs.md). Use `-NoProofReuse` when diagnosing the
executor. The complete command sequence below is the conservative fallback for shared, unknown,
renamed, deleted, or otherwise high-risk changes, not a default requirement for every edit.

```text
dotnet build RimLiaison.Offline.slnf --configuration Release --nologo
dotnet run --project tests/RimContext.Tests/RimContext.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/RimError.Core.Tests/RimError.Core.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tests/RimLiaison.Tests/RimLiaison.Tests.csproj --configuration Release --no-build --no-restore
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```

The offline solution filter deliberately omits only the RimWorld-dependent DevBridge2 Mod
project. It still builds the CLI, Desktop, RimContext, RimError, DevBridge coordinator,
BridgeTools and offline tests. The full `RimLiaison.sln` and direct DevBridge2 Mod builds
remain distinct game-backed checks requiring the matching RimWorld/Unity managed assemblies;
neither the offline build nor these portable tests qualify a live game/runtime or production
toolchain. A changed ProjectNexus `validation.build` command requires fresh exact-definition
developer-local trust before the new filter may execute.

The custom `RimLiaison.Tests` runner executes each test in an isolated child
process with a bounded watchdog. Set `RIMLIAISON_TEST_TIMEOUT_SECONDS` for a
diagnostic timeout (clamped to 1–300 seconds); successful output stays compact,
while timeout output includes the active test and bounded recent-test/process
diagnostics.

The composition gate uses the pinned external DevBridge2 fake host. It proves the no-RimWorld
composition and bounded contracts; real RimWorld/RimBridgeServer smoke remains owned by DevBridge2.
