# Agent-facing RimWorld development

For ordinary iterative XML or C# mod development, use the thin direct validation path:

```text
edit -> rimtest dev-check --json -> fix the reported mod defect -> repeat
```

If workspace resolution requires an explicit path, use:

```text
rimtest dev-check <workspace> --json
```

Result semantics:

- `BUILD_FAIL`: fix compilation before continuing.
- `STATIC_FAIL`: fix structural or static problems.
- `RUNTIME_LOAD_FAIL`: inspect attributed mod, content, or runtime errors.
- `RUNTIME_LOAD_PASS`: the target package/content was proven through the isolated runtime path;
  it does not prove gameplay behavior that was not exercised.
- `INFRASTRUCTURE_FAIL`: distinguish environment/tooling failure from a mod failure.
- `CLEANUP_FAIL`: do not treat validation as healthy.

RimLiaison and DevBridge2 are not part of this ordinary development inner loop. Do not invoke them
merely for affected selection, static checks, C# builds, routine RimWorld load validation, diagnosis,
or development-loop evidence. Use the direct `dev-check` result to decide whether the requested mod
change is complete.

RimLiaison remains available for explicitly documented release, qualification, promotion, and other
workflows that genuinely require its heavier orchestration. Do not delete or bypass those workflows.

`rimtest.cmd` remains a silent compatibility alias and forwards to the same command implementation.
Do not launch RimWorld manually, edit the normal user `ModsConfig`, or read `Player.log` directly;
`dev-check` owns its isolated runtime inputs and cleanup.

Canonical tooling promotion is owned by RimLiaison. After the complete burn-in
and a verified promotion package, run:

```text
rimliaison qualification promote --promotion-package <qualified-toolchain-package.json> --json
```

The command verifies qualification provenance, artifact hashes, the pinned internal runtime
component contract, an exclusive promotion lock, atomic production-manifest replacement, installed
identity, and production doctor. Agents MUST NOT copy binaries or edit production manifests manually.

Affected selection also computes `rimtest-validation-plan/v1` from the actual diff when indexed
impact and catalog coverage are available. Treat required plan entries as non-reducible; agents may
add validation, but removal requires an accepted source-identity-matching override. Learned impact
is causal-evidence-gated, project-local by default, and stale source/index/framework/RimWorld
identity never authorizes reuse. RimError attribution is consumed only through structured diagnostic
evidence; prediction never substitutes for actual impact or current artifact proof.

Lifecycle observability remains in the same persistent agent event stream: packet status and
expansion, predicted/actual impact, validation rationale/outcome, stale evidence, and learning
overrides use stable task/project/source/index/packet/plan/relationship identities. The desktop
shows this in the existing agent detail surface; unavailable metrics remain unavailable and safe
administrative exclusions are audited without approval gates.

For ordinary content-development tasks, use `rimtest dev-check --json` (or provide the workspace
path) and fix the reported build, static, runtime, infrastructure, or cleanup result.

The following RimLiaison content commands are retained only for an explicitly requested
content-intelligence or precedent workflow; they are not part of routine mod iteration:

```text
rimliaison affected --run --content-kind ThingDef --content-role "early-game ranged weapon" --json
rimliaison content query --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

Those commands retain their existing `content-blueprint/v1` and `content-evidence/v1` semantics.

Ownership:

```text
RimLiaison
  ├─ RimContext: static source/dependency/affected knowledge
  ├─ orchestration: test selection, execution, bounded results
  ├─ RimError: bounded diagnostic/root-cause analysis
  └─ RimLiaison.Runtime: lifecycle/deploy/generations/leases
       ↓
     RimBridgeServer: live in-game control/inspection
```

RimLiaison owns the production product, qualification, promotion, and reliability identity.
`RimLiaison.Runtime` is the modular internal implementation of lifecycle, profiles, generations,
readiness, leases, deployment, and recovery. The installed runtime under `RimWorld\Mods` is not an
independent DevBridge product. Do not launch RimWorld manually, edit ModsConfig, read Player.log
directly, or bypass the owner workflow.

Preserve stable `rimtest-*` schemas, `RIMTEST_*` identifiers, `.rimdev` conventions, catalog
fields, bounded-output rules, and DevBridge contracts.

Project metadata ownership:

- Production project identity is declared by the owning repository's `.rimdev/stack.json`.
- The production manifest owns workload, package identity, source project, configuration,
  expected assembly, deployment target, test recipe, runtime package, and dependencies.
- RimLiaison materializes the temporary RimLiaison.Runtime execution contract from that manifest;
  it must not resolve production metadata from `DevBridge2/DevelopmentProjects`.
- Missing, malformed, or contradictory production metadata fails closed with a structured
  `PROJECT_METADATA_*` error. Repair the owning repository; do not add a tooling descriptor.
- Tooling repositories may retain only explicitly classified `fixture`, `test`, `internal`, or
  `example` descriptors with `productionEligible: false`.

For changes to this tooling repository, use the single change-aware validation selector used by
CI instead of manually stacking all internal suites. Calculate the plan from the actual Git base
and head, then execute only its `selectedValidation` components:

```powershell
$base = git merge-base origin/main HEAD
$head = git rev-parse HEAD
$plan = & .\scripts\ci-plan.ps1 -BaseRevision $base -HeadRevision $head -Json
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-validate.ps1 `
    -PlanJson $plan -Json
```

Run `pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\ci-plan.tests.ps1` when changing
the planner, and `pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validation-proofs.tests.ps1`
when changing proof storage or fingerprinting. The validator reuses only complete deterministic
PASS proofs by content fingerprint; use `-NoProofReuse` for a diagnostic bypass. The planner
escalates unknown paths, wrappers, shared build configuration, renames, deletions, and
status/parser uncertainty to complete deterministic plus cross-stack validation. See
[docs/ci-validation.md](docs/ci-validation.md) and [docs/validation-proofs.md](docs/validation-proofs.md)
For target mod projects, the normal development validation is `rimtest dev-check --json`; it provides
the isolated runtime proof and does not replace explicitly required release or qualification
workflows. The tooling-repository CI selector above remains authoritative for changes to RimTest.

Workspace layout:

- Edit RimDev-managed RimWorld mod source only under `C:\RimDev\Repos`; `RimWorld\Mods` is a runtime destination.
- For the RimLiaison tooling repository itself, an explicitly registered ProjectNexus checkout such as `C:\Projects\RimLiaison` may be used only as a source reference; scoped edits belong in ProjectNexus-owned isolated Git task worktrees and require exact candidate validation and developer review before integration. This exception does not change where RimDev-managed mod sources or live game files may be edited.
- `.projectnexus/project.json` declares local Windows `.NET` development-confidence checks for ProjectNexus. The declarations grant no execution trust: local developer approval remains required, and these checks do not replace the change-aware repository validation plan, live RimWorld proof, or tooling qualification/promotion.
- `RimLiaison.Offline.slnf` builds source-only tooling and test projects while excluding only the `DevBridge2` game-side mod project. An offline tooling PASS is never equivalent to the full `RimLiaison.sln` game-assembly compile gate or a managed in-game runtime/qualification PASS; those gates require separately proven, real installed RimWorld inputs and the existing owner-controlled lifecycle.
- Use `C:\RimDev\.rimdev\workspace.json` for managed repository discovery and `C:\RimDev\Staging` for deployment staging.
- `RIMTEST_ROOT` and `RIMTEST_DEVBRIDGE_ROOT` identify RimLiaison/RimTest and DevBridge2; pinned compatibility revisions use isolated worktrees.
