# RimTest

RimTest is the agent-facing frontend for the RimWorld development toolchain.

The normal agent loop is:

    rimtest doctor --json       # only when readiness is unknown
    # make code changes
    rimtest affected --run --json

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

Use the owner commands only when RimTest points to deeper inspection or the task specifically requires it. Catalog authoring and the detailed output contracts are documented in [TestCatalog/README.md](TestCatalog/README.md).
