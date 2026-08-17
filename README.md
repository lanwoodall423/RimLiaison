# RimTest

RimTest is the agent-facing frontend for the RimWorld development toolchain.

The normal agent loop is:

    rimtest doctor --json       # only when readiness is unknown
    # make code changes
    rimtest affected --run --json

`doctor` uses read-only RimContext and DevBridge checks; it does not start or restart RimWorld.

Target mod repositories can commit `.rimdev/stack.json` and initialize the small agent handoff with
`rimtest init --json`. The manifest and discovery rules are documented in
[docs/stack-manifest.md](docs/stack-manifest.md); the maintained target-repository template is
[templates/AGENTS.md](templates/AGENTS.md).

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
