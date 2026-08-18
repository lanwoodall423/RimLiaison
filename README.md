# RimTest

RimTest is the agent-facing frontend for the RimWorld development toolchain.

The canonical agent loop is:

    edit
    rimtest affected --run --json
    inspect the result
    edit again

Run `rimtest doctor --json` once when readiness is unknown, and follow its bounded
`nextAction` if onboarding or recovery is required. For build-relevant changes,
`affected --run` automatically asks DevBridge2 to build into staging, hash, compare,
deploy when needed, establish an owned generation, prove artifact freshness, and only
then execute the selected recipes. An agent does not need a separate build or deploy step.

When designing a new live-game test, validating UI behavior, or doing deeper in-game
inspection, use `rimtest capabilities --json`. It returns a bounded, machine-readable
projection of the live RimBridgeServer registry; `--query <text>`, `--category`,
`--provider`, `--source`, and `--limit` narrow the authoring surface. Discovery is read-only:
it does not start RimWorld, change profiles or ModsConfig, acquire a lease, or expose a
generic live-game execution command. If the live bridge is unavailable, follow the returned
`nextAction` rather than probing the bridge directly; if DevBridge requires a current
owner-managed test session, RimTest reports that requirement without creating one.

For UI/layout/visual changes, functional tests alone are insufficient. After the relevant
suite passes, reach the required live UI state through the supported test/companion workflow,
use `rimtest ui targets --json`, capture the smallest relevant target with
`rimtest ui screenshot --target <target-id> --json`, inspect the screenshot, and iterate before
the final targeted capture. RimTest acquires the evidence and metadata; the coding agent
evaluates the rendered image.

Recommended UI loop:

    edit UI
    → rimtest affected --run --json
    → reach the required live UI state through the supported test/companion workflow
    → rimtest ui targets --json
    → capture the smallest relevant target
    → inspect the screenshot
    → iterate if necessary
    → final targeted capture
    → report functional + visual validation

`doctor` uses read-only RimContext and DevBridge checks; it does not start or restart RimWorld.

Target mod repositories can commit `.rimdev/stack.json` and initialize the small agent handoff with
`rimtest init --json`. The manifest and discovery rules are documented in
[docs/stack-manifest.md](docs/stack-manifest.md); the maintained target-repository template is
[templates/AGENTS.md](templates/AGENTS.md).

For a fresh or partial target repository, follow the compact `nextAction` in each blocked
`rimtest doctor --json` response. `rimtest init --json` safely fills missing manifest fields and
preserves existing configuration and `AGENTS.md`; use the explicit `--devbridge-project` and
`--fallback-suite` values supplied by the handoff. `--manifest-only --force` repairs a manifest
without replacing `AGENTS.md`; plain `--force` retains its intentional overwrite behavior.

When this stack is active, DevBridge2 is the sole RimWorld lifecycle owner. Do not start RimWorld
through GABS; RimBridgeServer remains the live-game control surface, and DevBridge2 owns the
generation's profile and ModsConfig mutations.

RimTest keeps catalog selection, orchestration, and compact result aggregation in one entry point. The ownership boundaries are:

- RimContext: source, Def, Harmony, dependency knowledge, and affected analysis.
- RimTest: test selection, orchestration, and result aggregation.
- DevBridge2: RimWorld lifecycle, profiles, generations, readiness, and leases.
- RimBridgeServer: live-game inspection and control.
- RimError: diagnostic compression and root-cause reporting.

For a source-changing affected run, the suite result includes `artifactFreshness` with the
source fingerprint, built/deployed SHA-256 values, deployment decision, DevBridge generation,
workflow/transaction/lease identities, and `loadedArtifactFreshnessProven`. RimTest reports a
source-change PASS only when DevBridge2 proves the tested generation conservatively corresponds
to the built/deployed artifact. Unknown or mismatched freshness is an infrastructure failure;
it is never treated as a successful test.

When a selected recipe fails, RimTest automatically requests DevBridge2's bounded
`logs query --generation <generation> --since-launch --severity ERROR` projection and sends only
that scoped semantic source, together with workflow/run/generation/operation identities, to
RimError. The agent does not need to know a Player.log path, generation log path, or diagnostic
store path. A result with `status: "fail"` and `diagnostic` includes the compact root cause;
`status: "fail"` with `diagnosticStatus: "unavailable"` means the test failed but diagnostic
infrastructure could not complete; `status: "infrastructure"` means the test infrastructure
itself did not establish a trustworthy test result. `--rimerror-log` and `--rimerror-store`
remain manual fallbacks for unusual environments.

Compatible recipes can safely reuse one sequential DevBridge generation during a suite when their
catalog entries explicitly declare compatible `isolation.mode` and `reuseKey` metadata. Unknown,
fresh-state, or incompatible recipes remain isolated by default. Fixture-resettable recipes must
name a deterministic `resetRecipe`, and reuse is invalidated on any failed reset, failure, timeout,
ownership/readiness/generation uncertainty, or cleanup failure. The compact suite result reports
`reuse.groupsPlanned`, `generationsUsed`, `fixtureResets`, `relaunches`, and any invalidation; it
does not expose child transcripts or operations.

Use the owner commands only when RimTest points to deeper inspection or the task specifically requires it. Catalog authoring and the detailed output contracts are documented in [TestCatalog/README.md](TestCatalog/README.md).

## Cross-stack contract gate

The pinned no-RimWorld composition gate is documented in
[docs/cross-stack-contract.md](docs/cross-stack-contract.md). After building the four checked-out
Release projects, run:

    pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\cross-stack-contract.tests.ps1 -Json

It fails on pinned-revision, schema, identity, artifact-freshness, correlation, or output-bound
drift. The real RimWorld/RimBridgeServer compatibility claim remains separate and is made only by
DevBridge2's configured self-hosted live smoke.
