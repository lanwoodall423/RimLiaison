# Change-aware repository validation

GitHub Actions uses `scripts/ci-plan.ps1` as the single validation selector. It compares the
workflow base and head revisions, emits the bounded `rimliaison-ci-plan/v1` JSON document, and
executes only the selected deterministic suites and composition gate. The planner is conservative:
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

The cross-stack workflow is a composition gate. It builds the pinned DevBridge2 host and the three
required command-line tools, then runs `scripts/cross-stack-contract.tests.ps1`; it does not rerun
the complete RimContext, RimError, or RimLiaison deterministic suites solely because they exist.
