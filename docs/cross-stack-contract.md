# Cross-stack contract gate

`scripts/cross-stack-contract.tests.ps1` is the canonical deterministic composition gate for
RimLiaison, its internal RimContext and RimError modules, and external DevBridge2. It does not
require RimWorld, RimBridgeServer, or a proprietary installation.

Run it from a Windows checkout after the merged Release build and the pinned DevBridge2 fake-host
build:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json
```

The script defaults to the current RimLiaison checkout and the sibling `DevBridge2` checkout. CI
checks out only DevBridge2 at the full commit SHA in `contracts/cross-stack-compatibility.json`;
RimContext and RimError are built from the same `RimLiaison.sln` checkout. A DevBridge2 SHA
mismatch fails with a machine-readable pin error rather than silently testing a moving branch.

The checked fixture exercises the real external CLI boundaries in one bounded workflow while the
RimLiaison hot path uses the shared Core APIs in-process:

1. The direct RimContext CLI indexes a fixture mod and resolves a deterministic source change to
   affected coverage, preserving its standalone contract.
2. RimLiaison selects the affected catalog test and invokes its artifact-freshness transaction.
3. A deterministic DevBridge-compatible fake builds and hashes the fixture assembly, reports the
   deployment decision and generation, and returns a versioned recipe result with workflow/run/
   generation/lease/operation identities; the same host returns a bounded capability registry.
4. RimLiaison discovers and projects that registry as `rimtest-capabilities/v1`, then returns a
   bounded `rimtest-suite-result/v1` pass only with proven artifact freshness.
5. A controlled diagnostic is passed through the versioned `rimerror-integration/v1` envelope to the
   direct RimError CLI for compatibility coverage; the normal RimLiaison diagnostic path calls
   RimError.Core directly and the resulting diagnostic is checked for run and operation correlation.

Each component still owns its independent guarantee:

- RimContext is an internal module that owns static indexing and affected-impact analysis
  (`rimctx/v1`); RimLiaison consumes `RimContext.Core` in-process, while the merged Windows CI
  also runs the complete deterministic direct-CLI fixture executable.
- RimLiaison owns catalog selection, orchestration, compact results, and artifact-freshness
  policy; the merged CI runs its offline CLI suite and this composition gate.
- DevBridge2 owns lifecycle, profiles, generations, leases, artifact deployment, and recipe
  semantics and remains a separate repository; its existing Windows validation runs the coordinator
  tests, deployment tests, and full fake/process-host E2E suite.
- RimError is an internal module that owns bounded diagnostic parsing, root-cause reporting, and
  correlation; RimLiaison consumes `RimError.Core` in-process, while the merged Windows CI runs its
  complete deterministic xUnit suite and direct-CLI compatibility coverage.

Only the DevBridge2 self-hosted `live-stack-smoke.yml` / `scripts/live-stack-smoke.ps1` gate proves
compatibility with an installed RimWorld and a live RimBridgeServer runtime. The offline gate does
not add a runtime-version claim.
