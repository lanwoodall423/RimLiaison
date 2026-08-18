# Cross-stack contract gate

`scripts/cross-stack-contract.tests.ps1` is the canonical deterministic composition gate for
RimContext, RimTest, DevBridge2, and RimError. It does not require RimWorld, RimBridgeServer, or a
proprietary installation.

Run it from a Windows checkout after the four Release builds:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```

The script defaults to sibling checkouts. CI passes explicit checkout roots and checks out
RimContext, DevBridge2, and RimError at the full commit SHAs in
`contracts/cross-stack-compatibility.json`; RimTest is pinned to the workflow SHA. Update those
external revisions deliberately, then run the gate before merging. A SHA mismatch fails with a
machine-readable pin error rather than silently testing a moving branch.

The checked fixture exercises the real CLI boundaries in one bounded workflow:

1. RimContext indexes a fixture mod and resolves a deterministic source change to affected coverage.
2. RimTest selects the affected catalog test and invokes its artifact-freshness transaction.
3. A deterministic DevBridge-compatible fake builds and hashes the fixture assembly, reports the
   deployment decision and generation, and returns a versioned recipe result with workflow/run/
   generation/lease/operation identities; the same host returns a bounded capability registry.
4. RimTest discovers and projects that registry as `rimtest-capabilities/v1`, then returns a
   bounded `rimtest-suite-result/v1` pass only with proven artifact freshness.
5. A controlled diagnostic is passed through the versioned `rimerror-integration/v1` envelope to
   RimError, and the resulting diagnostic is checked for run and operation correlation.

Each repository still owns its independent guarantee:

- RimContext owns static indexing and affected-impact analysis (`rimctx/v1`); its Windows CI runs
  the complete deterministic fixture executable.
- RimTest owns catalog selection, orchestration, compact results, and artifact-freshness policy;
  its CI runs the offline CLI suite and this composition gate.
- DevBridge2 owns lifecycle, profiles, generations, leases, artifact deployment, and recipe
  semantics; its existing Windows validation runs the coordinator tests, deployment tests, and
  full fake/process-host E2E suite.
- RimError owns bounded diagnostic parsing, root-cause reporting, and correlation; its Windows CI
  runs the complete deterministic xUnit suite.

Only the DevBridge2 self-hosted `live-stack-smoke.yml` / `scripts/live-stack-smoke.ps1` gate proves
compatibility with an installed RimWorld and a live RimBridgeServer runtime. The offline gate does
not add a runtime-version claim.
