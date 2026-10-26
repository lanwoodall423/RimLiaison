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

### ProjectNexus Windows tooling confidence

The version-controlled [`RimLiaison.Offline.slnf`](RimLiaison.Offline.slnf) selects
all 14 non-game solution projects, including the CLI, Windows desktop, RimContext,
RimError, coordinator, BridgeTools SDK integration, and offline tests. It excludes
only `src/DevBridgeRuntime/Mod/DevBridge2.csproj`, whose real RimWorld/Unity managed
assembly references cannot be resolved from source-only checkouts. The separate,
non-solution `src/RimLiaison.Cli/DevCheckRuntime/DirectValidationRuntimeSignal.csproj`
also references real game assemblies and is not built by this offline profile.
For a source-only Windows confidence build, use:

```text
dotnet build RimLiaison.Offline.slnf --configuration Release
```

ProjectNexus's declared `validation.build` targets this offline filter. The
existing named task-check label remains unchanged for already-materialized
ProjectNexus tasks; the exact command and capability title identify the bounded
offline build. Subsequent declared regression checks use `--no-build` only
after this build.

BridgeTools targets .NET Framework 4.7.2 and explicitly pins the private
`Microsoft.NETFramework.ReferenceAssemblies` package to version `1.0.3`, which
matches the tracked `src/DevBridgeRuntime/BridgeTools/packages.lock.json` on both
Windows and non-Windows restores. `RestoreLockedMode` makes an inconsistent package
graph fail instead of quietly modifying the tracked lockfile in an isolated
ProjectNexus candidate. When intentionally changing package dependencies, update
and review the project and lockfile together using an explicit owner-controlled
`dotnet restore src/DevBridgeRuntime/BridgeTools/DevBridge2.BridgeTools.csproj
-p:RestoreLockedMode=false --force-evaluate` outside an in-progress agent
validation, then rerun the source-integrity checks. A locked restore failure is
not a compilation PASS.

A new definition requires developer-local exact trust; an offline PASS does not
qualify the in-game mod or an installed/live RimWorld runtime. The full solution
build (`dotnet build RimLiaison.sln --configuration Release`) additionally requires
a verified installed RimWorld `RimWorldWin64_Data/Managed` directory containing
real `Assembly-CSharp.dll` and `UnityEngine.CoreModule.dll`, supplied through
trusted environment/workspace configuration. Managed runtime integration requires
separate RimLiaison-owned game lifecycle and validation; never launch RimWorld
manually or change user ModsConfig to satisfy an offline check.

### Change-aware repository validation

For repository changes, calculate the change-aware plan first and run only the selected components;
see [docs/ci-validation.md](docs/ci-validation.md). The proof-aware executor reuses only complete
successful deterministic validation when its content fingerprint is unchanged; see
[docs/validation-proofs.md](docs/validation-proofs.md). Use `-NoProofReuse` when diagnosing the
executor. The complete command sequence below is the conservative fallback for shared, unknown,
renamed, deleted, or otherwise high-risk changes, not a default requirement for every edit.

```text
dotnet build RimLiaison.sln --configuration Release
dotnet run --project tests/RimContext.Tests/RimContext.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/RimError.Core.Tests/RimError.Core.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tests/RimLiaison.Tests/RimLiaison.Tests.csproj --configuration Release --no-build --no-restore
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```

The custom `RimLiaison.Tests` runner executes each test in an isolated child
process with a bounded watchdog. Set `RIMLIAISON_TEST_TIMEOUT_SECONDS` for a
diagnostic timeout (clamped to 1–300 seconds); successful output stays compact,
while timeout output includes the active test and bounded recent-test/process
diagnostics.

The composition gate uses the pinned external DevBridge2 fake host. It proves the no-RimWorld
composition and bounded contracts; real RimWorld/RimBridgeServer smoke remains owned by DevBridge2.
