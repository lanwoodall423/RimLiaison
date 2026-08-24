# Cross-stack contract gate

`scripts/cross-stack-contract.tests.ps1` is the canonical deterministic composition gate for
RimLiaison, its internal RimContext and RimError modules, and external DevBridge2. It does not
require RimWorld, RimBridgeServer, or a proprietary installation.

Run it from a Windows checkout after the merged Release build and the pinned DevBridge2
Coordinator/FakeRimWorld build:

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
5. The pinned DevBridge2 `scripts/process-e2e.tests.ps1 -OnlyBuildFailure` path creates an
   intentionally invalid C# project and writes the real `scripts/mod-test.ps1 -Json` response. The
   gate checks the `devbridge-mod-development/v1` build-failure projection, including its bounded
   command/output, source project, exit/timeout state, truncation flag, transaction/workflow IDs,
   and failure stage/code/message.
6. The generated response is passed through the compiled RimLiaison adapter and observability test.
   That test exercises the child-process serialization/parsing boundary and verifies the
   `DEVELOPMENT_BUILD_FAILED` primary issue is correlated with the `DEVBRIDGE_COMMAND_FAILED`,
   `DEVBRIDGE_BUILD_FAILED`, and top-level command failures in one complete v2 bundle.
7. A controlled diagnostic is passed through the versioned `rimerror-integration/v1` envelope to the
   direct RimError CLI for compatibility coverage; the normal RimLiaison diagnostic path calls
   RimError.Core directly and the resulting diagnostic is checked for run and operation correlation.

The fixture repository starts with an old tracked deployment DLL and exactly one source edit. Its
runtime path uses one Git-discovered `rimliaison affected --run --json` invocation and requires the
new DLL bytes, descriptor-derived output path, DevBridge transaction ID, deployment decision, and
built/deployed SHA-256 to agree before treating the DLL mutation as build-owned. This is a per-output
provenance rule, not a DLL or directory exemption. A separate integration probe mutates source during
an active transaction and must still fail with `RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION` before
runtime validation can be claimed.

The merged `.github/workflows/ci.yml` composition job checks out DevBridge2 at the exact manifest
SHA. It derives an exact binary-cache identity from that SHA, the runner/runtime assumptions, and
the source/project/build-import/package-lock closure. A cache hit reuses only the Coordinator,
Coordinator.Core, and FakeRimWorld compiled `bin`/`obj` outputs; it never caches mutable runtime
state and never skips this contract execution. The independent validation-proof layer may skip the
whole stage only after a complete PASS proof is found.

Each component still owns its independent guarantee:

- RimContext is an internal module that owns static indexing and affected-impact analysis
  (`rimctx/v1`); RimLiaison consumes `RimContext.Core` in-process, while the merged Windows CI
  also runs the complete deterministic direct-CLI fixture executable.
- RimLiaison owns catalog selection, orchestration, compact results, and artifact-freshness
  policy; the merged CI runs its offline CLI suite and this composition gate.
- DevBridge2 owns lifecycle, profiles, generations, leases, artifact deployment, recipe semantics,
  and raw/bounded build diagnostics. `scripts/mod-test.ps1` owns the bounded compiler/MSBuild
  capture and the `devbridge-mod-development/v1` wire projection; its `failure` object repeats the
  primary stage/code/message and truncation state for older consumers. Its focused process E2E
  fixture and existing Windows validation cover the owner-side serialization.
- RimLiaison owns persistence of the bounded owner response as canonical evidence and assembles the
  user-facing `rimliaison-agent-diagnostic-bundle/v2`; it must not launch a replacement build or
  read an unbounded log to fill missing fields. The full-SHA manifest and CI checkout select the
  DevBridge2 revision used by validation; normal agents resolve the configured `DevBridge.cmd`
  installation/sibling and should use the same published revision.
- RimError is an internal module that owns bounded diagnostic parsing, root-cause reporting, and
  correlation; RimLiaison consumes `RimError.Core` in-process, while the merged Windows CI runs its
  complete deterministic xUnit suite and direct-CLI compatibility coverage.

Only the DevBridge2 self-hosted `live-stack-smoke.yml` / `scripts/live-stack-smoke.ps1` gate proves
compatibility with an installed RimWorld and a live RimBridgeServer runtime. The offline gate does
not add a runtime-version claim.
