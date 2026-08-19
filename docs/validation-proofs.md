# Conservative validation proof reuse

`scripts/ci-validate.ps1` is the proof-aware executor for the deterministic stages selected by
`scripts/ci-plan.ps1`. It is the repository-validation command for this tooling checkout:

```powershell
$plan = & .\scripts\ci-plan.ps1 -BaseRevision $base -HeadRevision $head -Json
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-validate.ps1 `
    -PlanJson $plan -Json
```

When the plan selects `cross-stack`, the executor uses a sibling `DevBridge2` checkout by default;
pass `-DevBridgeRoot <path>` when it is elsewhere. A missing, dirty, or unpinned checkout is a
normal validation blocker and never becomes a reusable proof.

The normal run automatically looks for PASS-only records in the ignored
`.rimdev/validation-proofs` directory. A result reports only bounded counters, for example
`selected=3 executed=1 reused=2`. Use `-NoProofReuse` to force execution for diagnosis; it does
not make a prior failure reusable.

Each reusable stage derives a SHA-256 proof ID from its versioned stage ID, selected validation
IDs, validator/proof implementation, relevant source and test files, project/import/SDK/lock
inputs, and the required environment. The cross-stack stage also requires a clean DevBridge2
checkout and includes its exact Git revision. If the complete closure cannot be enumerated, the
stage runs and no proof is written.

Only a complete stage whose commands all exit successfully writes a compact `pass` record. A
failure, cancellation, timeout, incomplete result, malformed record, unavailable cache, or
external-state uncertainty is a cache miss. Records are written atomically and bounded to 64
records or 1 MiB with deterministic eviction; they contain hashes and compact metadata, never
logs or transcripts.

The proof layer does not apply to live RimWorld smoke, readiness/lifecycle/lease checks,
deployment, or current-generation artifact freshness. Those remain owned by DevBridge2 and the
normal `rimliaison affected --run --json` workflow. An offline proof can never establish that the
currently loaded live artifact is fresh.

Run the focused proof tests with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validation-proofs.tests.ps1
```
