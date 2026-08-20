# Change-aware repository validation

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
