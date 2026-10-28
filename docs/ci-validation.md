# Change-aware repository validation

## Explicit tooling-only Windows build (NatureTiles blocker #12)

RimLiaison's full solution includes one real RimWorld game Mod whose build
requires the actual installed game's managed assemblies. The separate
headless tooling-only build is an explicit preparation gate: a PASS is
never proof that the game Mod compiled or loaded in RimWorld.

In a developer-approved Windows environment with the pinned .NET SDK,
an authorized operator may run:

    pwsh -NoProfile -File .\scripts\ci-validate.ps1 -StageId offline-tooling-build -NoProofReuse -Json

This stage reads the tracked RimLiaison.sln and requires exactly its
14 reviewed tooling/test projects plus the one excluded game Mod:
src/DevBridgeRuntime/Mod/DevBridge2.csproj. It fails closed if a project
is added, removed, duplicated, missing, or refers to the excluded game
Mod through ProjectReference. It restores each selected project, uses
--locked-mode for the BridgeTools net472 dependency graph, then builds
all 14 under Release using --no-restore. A failed restore or build
terminates the stage. It snapshots the checked-in BridgeTools
packages.lock.json SHA-256 before restore and rejects any lockfile drift.
Before reporting PASS, it also requires nonempty, non-link compiled
regression files from the expected Release frameworks: RimContext.Tests
under net8.0, RimError.Core.Tests under net8.0, and RimLiaison.Tests
under net8.0-windows. The two executable suites also require their
runtimeconfig.json files. Missing files or a changed dependency lock
produce a failed stage instead of an apparent build PASS.

These output checks prove only file presence and unchanged locked
dependencies. They do not prove fresh compilation, exact source-to-binary
provenance, or successful regression execution. The original three
ProjectNexus regression checks must still run after the build is
authorized.

This source-only route requires no newly tracked RimLiaison.Offline.slnf
file. It is deliberately non-reusable in validation-proof tracking,
because build outputs and lockfile residue are mutable. It is not wired
into the automatic CI planner, production release, or ProjectNexus trust.
The PN agent must independently review and authorize the exact declared
build check before its output can satisfy #12. Run the original game-backed
build and isolated 1.6/1.7 runtime qualification under their distinct
real-assembly gates; neither is waived by this tooling stage.


GitHub Actions uses one workflow (`.github/workflows/ci.yml`) and one authoritative planning job.
That job runs the planner/proof/cache infrastructure checks once, then fans out to selected
deterministic validation and a conditional composition gate. `scripts/ci-plan.ps1` compares the
workflow base and head revisions, emits the bounded `rimliaison-ci-plan/v1` JSON document, and
controls both downstream jobs. The planner is conservative:
unknown paths, project or SDK configuration, wrappers, renames, deletions, and unparseable Git
status all select the complete internal validation and the cross-stack gate.

For local planning, provide the same revisions used by CI:

```powershell
$base = git merge-base origin/main HEAD
$head = git rev-parse HEAD
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-plan.ps1 `
    -BaseRevision $base -HeadRevision $head -Json
```

Pass that bounded plan to `scripts/ci-validate.ps1` to execute the selected stages. The executor
automatically reuses only complete PASS proofs whose content fingerprint is unchanged and emits
bounded `selected`, `executed`, and `reused` counters. See
[validation-proofs.md](validation-proofs.md) for the proof contract and `-NoProofReuse` diagnostic
bypass.

The JSON `selectedValidation` and `skippedValidation` arrays are authoritative. Do not manually
stack all internal suites when a narrower plan is selected. The planner's deterministic coverage
is checked with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-plan.tests.ps1
```

The normal selection matrix is:

| Change set | Selected validation | Omitted validation |
| --- | --- | --- |
| Documentation-only | diff check, planner tests | deterministic suites, formatting, composition |
| RimContext implementation | RimContext and dependent RimLiaison suites, formatting; composition when the public boundary can be affected | RimError suite |
| RimError implementation | RimError and dependent RimLiaison suites, formatting; composition when the public boundary can be affected | RimContext suite |
| RimLiaison-only implementation | RimLiaison suite and formatting | RimContext, RimError, composition unless the file is an integration boundary |
| Catalog or ordinary fixture | Owning deterministic suite | unrelated suites and composition |
| Cross-stack contract, adapter, freshness, or harness | composition gate | unrelated deterministic suites |
| Unknown, renamed, deleted, or shared configuration | complete deterministic validation and composition | none |

The cross-stack job is a composition gate. It builds the pinned DevBridge2 host when its exact
binary cache misses, builds the three required command-line tools, then runs
`scripts/cross-stack-contract.tests.ps1`; it does not rerun the complete RimContext, RimError, or
RimLiaison deterministic suites solely because they exist. The former standalone cross-stack
workflow is intentionally gone, so planner and proof infrastructure setup cannot drift between
the two gates.

CI has three separate cache/proof layers:

- The NuGet cache may use a broad OS-scoped restore key because packages are inputs to a later
  restore/build and are independently validated.
- The DevBridge2 binary cache is exact-only. `scripts/devbridge-binary-cache.ps1` derives a key
  from the pinned commit, runner OS/architecture, .NET SDK, Release/net8.0 configuration, and
  all relevant source/project/build-import/package-lock inputs. It caches only immutable `bin`/
  `obj` outputs for Coordinator, Coordinator.Core, and FakeRimWorld; it has no restore key. An
  exact hit skips only the proven-identical external restore/build, and the cross-stack contract
  still executes.
- Validation proof reuse means a complete deterministic or cross-stack validation closure already
  passed with the same fingerprint. It is independent of binary artifact reuse and may skip the
  entire stage only when the proof contract permits it.
