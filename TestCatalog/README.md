# RimTest catalog

The catalog is deliberately metadata-only. Each test names an existing DevBridge2 recipe; it does not describe game operations.

## Agent workflow

The canonical agent loop is documented in the repository [README](../README.md). Run it from the repository root with its `TestCatalog/rimtest.catalog.json` and the configured sibling tools. The target repository supplies this metadata-only catalog; RimTest never infers it by indexing source. A successful command returns one compact suite result and exits 0; no follow-up context retrieval is needed. A failed command returns only the failed test references and, when available, the RimError diagnostic id needed for the next source-level action. Use the explicit owner commands documented below only when deeper inspection is needed.

RimTest is an execution frontend and result aggregator, not a lifecycle manager, game-operation runner, repository indexer, or diagnostic analyzer. It delegates those responsibilities to the existing ecosystem boundaries.

The default file is `catalog` from the discovered `.rimdev/stack.json`; without a manifest it remains
`TestCatalog/rimtest.catalog.json` relative to the current working directory. Pass `--catalog` to
select another file. `--fallback-suite` overrides the manifest's fallback suite, and the existing
`RIMTEST_FALLBACK_SUITE` environment variable remains higher priority than the manifest.

Use --recipes followed by a DevBridge2 devbridge-test-recipe-list/v1 JSON file when deterministic recipe-reference checking is available. Without that option, recipe existence is reported as skipped.

Example:

    {
      "schemaVersion": "rimtest-catalog/v1",
      "tests": [
        {
          "id": "assembler-smoke",
          "recipe": "assembler-fixture",
          "tags": ["assembler", "crafting"],
          "covers": [
            {"kind": "def", "name": "CCM_Assembler"},
            {"kind": "csharp_type", "name": "CompAssembler"}
          ],
          "cost": "low"
        }
      ],
      "suites": [
        {"id": "smoke", "tests": ["assembler-smoke"]}
      ]
    }

The command output is compact JSON. list and suites intentionally return only ids and recipe mappings; show commands return detailed metadata. Run results may include the optional workflow-level workflowId; this is a correlation reference, not a replacement for DevBridge's runId, generation, lease, launch, or operation identifiers. The complete cross-stack contract is in docs/correlation-contract.md.

`doctor --json` uses the versioned `rimtest-doctor/v1` envelope. Its `nextAction` values are
canonical RimTest onboarding/validation commands, or owner commands only when RimTest cannot
resolve the dependency; the response never includes installation paths, credentials, or owner
transcripts.

Catalog exit codes are 0 for success, 2 for invalid input or catalog data, 3 for an unresolved conservative selection without a fallback suite, 4 when a requested test or suite is absent, and 10 for an unexpected internal error. `run <test>` additionally uses 1 for a recipe test failure, 10 for DevBridge refusal or other infrastructure failure, 124 for a bounded client timeout, and 130 for cancellation. Direct adapter commands retain their operation-specific refusal/not-found code; malformed or incompatible DevBridge responses are infrastructure errors (10).

## DevBridge adapter

The catalog command run <test> maps the catalog test to its recipe and delegates the complete execution to DevBridge2. Direct adapter commands are recipe show <recipe>, recipe plan <recipe>, and recipe run <recipe>; the equivalent test recipe ... spelling is also accepted.

The adapter invokes DevBridge.cmd with --json and accepts only the versioned DevBridge recipe response contracts. It preserves recipe, optional run, generation, lease, evidence, and failure-fingerprint identifiers. It never starts or stops RimWorld, edits profiles or ModsConfig, acquires leases, calls RimBridgeServer, or retries/recoveries an accepted operation.

DevBridge command discovery uses --devbridge, then RIMTEST_DEVBRIDGE_CMD or DEVBRIDGE_CMD, then a sibling DevBridge2/DevBridge.cmd near the current directory or application directory. The root uses --devbridge-root, RIMTEST_DEVBRIDGE_ROOT, or the command's directory.

Ctrl+C cancels RimTest's client wait. If DevBridge already accepted a long-running recipe operation, cancellation does not claim that coordinator operation was rolled back; DevBridge remains the owner.

## Live-game capability discovery

When authoring a new live-game test, validating UI behavior, or planning deeper in-game
inspection, use `rimtest capabilities --json`. RimTest returns a bounded projection of the
registered RimBridgeServer capabilities, including ids/aliases, summaries, category/provider
source metadata, and authoring parameters. Narrow it with `--query <text>`, `--category`,
`--provider`, `--source`, or `--limit` (default 20). The command is discovery-only and does
not start or restart RimWorld, change profiles or ModsConfig, acquire a lease, or expose a
generic RimBridge execution command. An unavailable or incompatible bridge response is
reported as compact JSON with a DevBridge owner handoff in `nextAction`; do not probe the
bridge directly. If DevBridge requires a current owner-managed test session/lease, RimTest
reports that requirement without creating one.

## Visual UI validation

For UI/layout changes, functional tests alone are insufficient. Use `tags: ["ui"]` as the
catalog convention for tests that establish or validate a live UI state; the tag is
machine-readable metadata and does not turn a functional pass into a visual-pass failure.
After the functional suite passes, use RimTest's UI workflow to enumerate visible targets,
capture the smallest relevant target, inspect the screenshot, iterate, and make a final
targeted capture. The compact loop is:

    edit UI
    → rimtest affected --run --json
    → reach the required live UI state through the supported test/companion workflow
    → rimtest ui targets --json
    → rimtest ui screenshot --target <target-id> --json
    → inspect and iterate
    → final targeted capture
    → report functional + visual validation

Cell/map-region captures may use `rimtest ui screenshot --cell-rect <x,z,width,height> --json`
when that live capability is registered. If the bridge or visual readiness is unavailable,
follow the compact `nextAction`; do not probe RimBridgeServer directly.

## Catalog test results

`run <test>` emits one compact `rimtest-result/v1` JSON object. A pass contains `schemaVersion`, `status`, `test`, `durationMs`, and optional `workflowId` and stable `runId`. A failure contains those identifying fields plus optional `failureFingerprint`, `evidenceId`, `generation`, and `errorCode`. An available RimError diagnosis is projected to `id`, `category`, `method`, `source`, and `line`; the full report remains with RimError. It does not copy logs, operations, stack traces, or evidence contents. Invalid catalog/configuration, infrastructure, timeout, and cancellation results use `invalid`, `infrastructure`, or `cancelled` statuses with a compact `errorCode`. A missing requested test uses `status: "invalid"`, `errorCode: "TEST_NOT_FOUND"`, and exit code 4, with no human stderr transcript.

When a deterministic recovery step exists, the result also includes `nextAction`: stale or missing context points to `rimctx index --json`, DevBridge infrastructure issues point to `DevBridge.cmd doctor --json`, and an available diagnosis points to `rimerror show <id>`. Passing results and failures without a meaningful next step omit the field.

## Agent-facing output budgets

All RimTest command output is JSON; `--json` is accepted explicitly for agent scripts and does not add a second human transcript. Normal representative responses are budgeted at 256 bytes for a passing single test, 768 bytes for a failing single test, 256 bytes for a passing suite, 384 bytes for affected-test selection, and 256 bytes for a passing affected suite. Suite failure output may grow only with the number of failures. Detailed catalog metadata, RimContext reasons, DevBridge plans, and owner-system reports require their explicit commands/options; the common result path never includes logs, transcripts, operation lists, or full evidence.

## RimError failure analysis

After a DevBridge test failure, RimTest uses RimError's current CLI contract: when `--rimerror-log` is configured, it calls `rimerror ingest <log> --integration <temporary rimerror-integration/v1 file> --run <run> --test <test>`, then calls `rimerror latest --json`. With only `--rimerror-store`, it requests the existing store's latest report. RimTest passes the identifiers supported by RimError v1 (`run`, `test`, `generation`, and `evidence`) and retains the DevBridge failure fingerprint locally because RimError v1 has no fingerprint input field.

Configure the command with `--rimerror` or `RIMTEST_RIMERROR_CMD`/`RIMERROR_CMD`; configure a log with `--rimerror-log` or `RIMTEST_RIMERROR_LOG`/`RIMERROR_LOG`; configure the store with `--rimerror-store` or `RIMTEST_RIMERROR_STORE`/`RIMERROR_STATE_PATH`. A missing or failing RimError does not change the test result. An available diagnosis is represented by the compact `diagnostic` object; `diagnosticStatus` is emitted only for `"empty"` or `"unavailable"` degraded outcomes. Available output is a bounded projection of RimError's current `latest --json` root-cause/diagnostic summary and preserves its diagnostic `id` for `rimerror show <id>` drill-down.

## RimContext affected-test selection

`affected [<changed-path> ...]` delegates indexing and impact computation to the current RimContext CLI command. Before asking for impact, RimTest refreshes RimContext's own index so edits made after `doctor` cannot be evaluated against stale definitions or types. With no paths, RimTest discovers tracked, staged, and relevant untracked Git changes automatically; explicit paths take precedence. Use `--base <git-ref>` when a Git base is required:

    rimctx affected PATH [PATH ...] --root PATH --depth 1..8 --limit N --json

RimTest accepts only the versioned `rimctx/v1` `affected` envelope. It maps each returned `kind`/`name` pair from `data.direct`, `data.dependent`, and `data.runtime_risk` to the catalog's `covers` entries. It does not parse C#, XML, Defs, Git history, or the RimContext index. Coverage names must match RimContext's current names exactly; current RimContext does not emit a separate `feature` impact entity, but feature entries remain valid catalog metadata for a future/currently supplied impact kind.

Normal output is a compact `rimtest-selection/v1` object, for example:

    {"schemaVersion":"rimtest-selection/v1","status":"ok","tests":["assembler-smoke"],"reasonCount":1}

`--explain` adds bounded impact-to-test reasons. Tests are sorted by ordinal id and duplicate matches are removed. For changed paths, a complete zero-impact RimContext result is `status: "conservative"` with error code `RIMCONTEXT_NO_TESTS`; it uses a valid non-empty fallback suite when configured and otherwise returns exit code 3 with an actionable next action. A truncated, unavailable, malformed, incompatible, uncovered, deleted-path, or renamed-path result is also `status: "conservative"`; it never means that no tests are needed. Use `--fallback-suite <suite>` (or `RIMTEST_FALLBACK_SUITE`) to select a broader configured suite. Without a usable fallback suite, the command returns exit code 3 so an agent must handle the uncertainty explicitly. A fallback suite selected successfully still returns exit code 0 and retains `status: "conservative"`.

When automatic Git discovery finds no changes, RimTest returns `status: "ok"` with an empty test list and does not invoke DevBridge, including when `--run` is present. This is the explicit clean-worktree result; a Git discovery failure is `status: "blocked"` and never means that no tests are needed.

RimContext command discovery uses `--rimcontext`, then `RIMTEST_RIMCONTEXT_CMD`/`RIMCONTEXT_CMD`, then a sibling RimContext/rimctx.cmd. The workspace root uses `--rimcontext-root`, `RIMTEST_RIMCONTEXT_ROOT`/`RIMCONTEXT_ROOT`, or the current directory. An optional store uses `--rimcontext-store` or `RIMTEST_RIMCONTEXT_STORE`/`RIMCONTEXT_STORE`. `--depth` defaults to 8 and `--limit` defaults to 100; both are forwarded unchanged to RimContext. Ctrl+C cancels the bounded RimContext client wait.

## Suite execution

`suite run <suite>` and `affected [<changed-path> ...] --run` execute selected catalog tests sequentially through the existing DevBridge recipe adapter. A suite with more than one test first calls each recipe's existing DevBridge `test recipe plan` operation. Planning is a preflight gate only: RimTest does not interpret it as permission to share a generation, lease, game state, or launch, and it does not skip a recipe because `alreadySatisfied` is true. If planning cannot establish that every recipe is executable, no recipe run is started.

The MVP policy is continue after ordinary test or infrastructure failures so the result identifies all failures; Ctrl+C or a DevBridge cancellation stops launching new children. There is no parallel or distributed execution. This leaves lifecycle, readiness, leases, profiles, recovery, and any safe state reuse entirely with DevBridge2.

Suite output uses `rimtest-suite-result/v1` and summarizes successful children numerically. An empty execution is `status: "conservative"` with `RIMTEST_EMPTY_EXECUTION`, never a normal pass. Failure output contains only failure-scaled references: test id, optional diagnostic id, failure fingerprint, evidence id, and error code. Optional `skipped`, `cancelled`, and conservative `selectionStatus` fields explain fallback or cancellation without embedding child DevBridge responses, logs, operations, or evidence. A known-safe affected run omits redundant `selectionStatus: "ok"`; an affected fallback run retains `selectionStatus: "conservative"` and `fallbackSuite` even when all fallback tests pass.
