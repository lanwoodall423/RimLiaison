# RimLiaison qualification and promotion

## Operating model

Normal mod production runs on the one promoted RimLiaison product described by
`qualification/toolchain-known-good.json`. Its internal `RimLiaison.Runtime` component is
qualified and fingerprinted as part of that product; it has no independent production release,
promotion, or reliability campaign. Production remains the primary operator workflow and is
visible in Observability as workload `production` with toolchain state `promoted`.

Tooling changes are qualified independently with `qualification/fixture`, a small reference mod
that is not a release dependency for production mods. Qualification runs are visible as workload
`qualification` with toolchain state `experimental`. Experimental changes do not automatically
change the normal agent path.

Genuine required-capability blockers are recorded as blocking issues and fixed promptly. Optional
validation gaps, unsupported obscure checks, and broader improvement opportunities are recorded as
non-blocking recommendations. They remain inspectable in the existing Observability Recommendations
surface and in `.rimdev/qualification/tooling-improvement-backlog.json`.
Project-owned validation failures are reported as `MOD_FAILURE` in the unified orchestration
envelope and do not fail tooling qualification. They remain project health findings; only current
tooling/runtime infrastructure failures block the unified production qualification gate.


## Fixture and scenarios

The fixture manifest is `qualification/fixture/qualification-fixture.json`. It covers deterministic simulations for successful and failed build, deployment and artifact freshness, Quicktest launch, readiness identity, successful and failed runtime validation, restart/recovery, structured evidence, optional-validation-unavailable behavior, recommendation generation, Observability publication, and clean completion.

Expected fixture failures are evidence scenarios, not failures of the qualification run. Infrastructure failures and fixture failures are separate aggregate counters.

## Commands

Iteration profile:

```powershell
rimliaison qualification --runs 1
```

Promotion profile:

```powershell
rimliaison qualification burn-in --runs 25
```

Both commands emit machine-readable aggregate JSON and write:

- `.rimdev/qualification/latest.json`
- `.rimdev/qualification/tooling-improvement-backlog.json`

The aggregate includes source-commit provenance and qualified artifact hashes.

Supported promotion command:

```powershell
rimliaison qualification promote `
  --promotion-package <qualified-toolchain-package.json> --json
```

The package must reference the complete burn-in artifact and exact published RimLiaison files.
Promotion verifies the source commit, qualification hash, artifact hashes, the pinned internal
runtime protocol contract, an exclusive lock, atomic production-manifest replacement, installed
hashes, and production doctor. A failed verification leaves the previous production manifest
active. DevBridge runtime release scripts are component-build tools only; they cannot publish an
independent production identity.

## Promotion criteria

Promote only after:

1. every deterministic qualification scenario passes;
2. the 25-run profile has zero unexplained infrastructure failures;
3. restart/recovery and clean-start qualification pass;
4. parallel/concurrency coverage passes when the stack supports it;
5. the qualification artifact and component versions are recorded in the single promoted manifest.

A working-tree or experimental manifest is never used as the normal mod-agent version merely because
its local build succeeds. Update `qualification/toolchain-known-good.json` only after the promotion
profile succeeds and the change is intentionally promoted.

## Backlog discipline

The backlog is a projection of structured Observability issues and recommendations, not a second issue-management system. Blocking issues retain their required status. Non-blocking recommendations do not fail ordinary mod production unless they are later adopted as an established required capability.
