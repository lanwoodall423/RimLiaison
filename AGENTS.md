# RimLiaison agent handoff

Use `rimliaison` as the canonical agent-facing entrypoint. During an active edit iteration, use
the opt-in fail-fast path so the first trustworthy ordinary test failure returns promptly:

```text
edit -> rimliaison affected --run --fail-fast --json -> fix immediately on failure -> repeat
```

Once the change is stable, run `rimliaison affected --run --json` as the complete pre-submit
validation. A fail-fast PASS is never a partial PASS: it proves that every selected test ran.

Use `rimliaison doctor --json` when readiness is unknown and follow its bounded `nextAction`.
Use `rimliaison capabilities --json` for live capability discovery. For UI evidence, enumerate
semantic targets with `rimliaison ui targets --json` and capture only the smallest relevant target
with `rimliaison ui screenshot --target <target-id> --json`.

`rimtest.cmd` is retained as a silent compatibility alias and forwards to the same implementation.
It is not a second orchestration frontend. `rimctx` and `rimerror` are direct drill-down/debugging
tools only; begin with RimLiaison and use their `nextAction` when directed.

For substantive work, consume the automatic Execution Packet from `rimliaison preflight --json`
before broad repository reads. Expand only its bounded RimContext handles. After edits, use
`rimliaison affected --run --json`; its actual-diff impact is authoritative for validation and
its packet prediction is advisory. Missing static packet evidence remains explicit and does not
authorize narrower validation.

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

For content-development tasks, pass only the semantic intent that cannot be derived:

```text
rimliaison affected --run --content-kind ThingDef --content-role "early-game ranged weapon" --json
```

RimLiaison automatically captures `content-blueprint/v1` before selection and
`content-evidence/v1` after validation in the shared canonical-workspace precedent store. Source,
Def/entity, dependency, agent/session/run, and source/tool identity fields are derived when
available; unknown values remain `null`. Query reusable references with
`rimliaison content query --content-kind ThingDef --content-role "early-game ranged weapon" --json`.
Creative novelty is not scored; reuse is ordered `RimContent -> precedent -> vanilla/reference -> novel`.

Ownership:

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

DevBridge2 owns RimWorld lifecycle, profiles, generations, readiness, leases, deployment, and
recovery. Do not launch RimWorld manually, edit ModsConfig, read Player.log directly, or bypass
the owner workflow.

Preserve stable `rimtest-*` schemas, `RIMTEST_*` identifiers, `.rimdev` conventions, catalog
fields, bounded-output rules, and DevBridge contracts.

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
for the selector and proof contracts. The canonical `rimliaison affected --run --json` workflow
above remains the normal agent-facing validation for target RimWorld projects, and its live
artifact-freshness evidence is never replaced by an offline proof.

Workspace layout:

- Edit source only under `C:\RimDev\Repos`; `RimWorld\Mods` is a runtime destination.
- Use `C:\RimDev\.rimdev\workspace.json` for managed repository discovery and `C:\RimDev\Staging` for deployment staging.
- `RIMTEST_ROOT` and `RIMTEST_DEVBRIDGE_ROOT` identify RimLiaison/RimTest and DevBridge2; pinned compatibility revisions use isolated worktrees.
