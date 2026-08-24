# RimLiaison

RimLiaison is the canonical agent-facing RimWorld development toolchain. The active edit loop is:

```text
edit
→ rimliaison affected --run --fail-fast --json
→ fix immediately on failure
→ repeat
```

Once stable, run `rimliaison affected --run --json` for complete pre-submit validation. Fail-fast
only shortens a failure path; a fail-fast PASS still means every selected test executed.

Use `rimliaison doctor --json` as the bounded onboarding/recovery entrypoint when readiness is
unknown, and follow its `nextAction`. `rimliaison init`, `capabilities`, `ui`, `affected`, and the
catalog/recipe commands retain their existing names and semantics. `rimtest.cmd` remains a thin,
silent compatibility alias for old automation; it forwards every argument to `rimliaison.cmd`.

Use `rimliaison context --json` for a read-only, versioned cross-stack snapshot. It reports
topology, Git state, environment, testing, deployment/runtime evidence, recent execution, failures,
efficiency, and explicit selection decisions without launching RimWorld or writing generated state
into the repository. The direct `rimctx context --json` command is available for static-context
debugging. See [docs/context-bundle.md](docs/context-bundle.md) for the `rimctx-bundle/v1` contract.

Use `rimliaison publish check --json` to query whether a Git publication can reuse matching
validation evidence; it does not rerun tests or perform publication. Use `rimliaison benchmarks
--json` for the deterministic A–H golden workflow baseline. See
[docs/validation-provenance.md](docs/validation-provenance.md) for evidence identity, decision
codes, and reviewed failure knowledge.

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
