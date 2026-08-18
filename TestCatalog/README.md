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
          "cost": "low",
          "isolation": {
            "mode": "pureRead",
            "reuseKey": "fixture-ready"
          }
        }
      ],
      "suites": [
        {"id": "smoke", "tests": ["assembler-smoke"]}
      ]
    }

The command output is compact JSON. list and suites intentionally return only ids and recipe mappings; show commands return detailed metadata. Run results may include the optional workflow-level workflowId; this is a correlation reference, not a replacement for DevBridge's runId, generation, lease, launch, or operation identifiers. The complete cross-stack contract is in docs/correlation-contract.md.

`affected --run --json` is the canonical source-change verification command. When changed paths
are build-relevant, RimTest invokes DevBridge2's `mod-test.ps1` transaction in owner mode before
running the selected recipes. The result's `artifactFreshness` object reports source/build/deploy
hashes, the deployment decision, generation and transaction identities, and the conservative
freshness proof. A missing, stale, or mismatched proof returns infrastructure status and cannot
produce a valid PASS. A direct `mod-development-smoke` recipe run only checks its declared
project/Quicktest readiness; by itself it is not evidence that current source was built and
loaded.

## Recipe isolation and safe reuse

The optional `isolation` object is an explicit, reviewed permission for sequential state reuse;
it is not inferred from a DevBridge recipe description. Missing metadata means `unknown` and takes
the safe per-recipe path. Supported modes are `pureRead`, `sameGenerationSafe`,
`fixtureResettable`, `freshGameRequired`, and `freshGenerationRequired`. Reusable modes require a
non-empty `reuseKey`; `fixtureResettable` additionally requires a deterministic `resetRecipe`.

Only contiguous compatible catalog tests are grouped. Execution remains sequential inside one
DevBridge-owned generation and lease. A failed/timeout/uncertain recipe, lost readiness, ownership
or generation mismatch, failed reset, or failed lease release invalidates reuse; RimTest then asks
DevBridge2 for a fresh safe state. It never launches or stops RimWorld itself. Suite JSON exposes a
bounded `reuse` summary with selected count, groups, generations, reset/relaunch counts, and the
first invalidation reason. A reuse invalidation makes an otherwise passing batch infrastructure,
except when a child already reports the underlying test failure.

`doctor --json` uses the versioned `rimtest-doctor/v1` envelope. Its `nextAction` values are
canonical RimTest onboarding/validation commands, or owner commands only when RimTest cannot
resolve the dependency; the response never includes installation paths, credentials, or owner
transcripts.

Catalog exit codes are 0 for success, 2 for invalid input or catalog data, 3 for an unresolved conservative selection without a fallback suite, 4 when a requested test or suite is absent, and 10 for an unexpected internal error. `run <test>` additionally uses 1 for a recipe test failure, 10 for DevBridge refusal or other infrastructure failure, 124 for a bounded client timeout, and 130 for cancellation. Direct adapter commands retain their operation-specific refusal/not-found code; malformed or incompatible DevBridge responses are infrastructure errors (10).

## DevBridge adapter

The catalog command run <test> maps the catalog test to its recipe and delegates the complete execution to DevBridge2. Direct adapter commands are recipe show <recipe>, recipe plan <recipe>, and recipe run <recipe>; the equivalent test recipe ... spelling is also accepted.

The recipe adapter invokes DevBridge.cmd with --json and accepts only the versioned DevBridge recipe response contracts. It preserves recipe, optional run, generation, lease, evidence, and failure-fingerprint identifiers. The adapter itself never starts or stops RimWorld, edits profiles or ModsConfig, acquires leases, calls RimBridgeServer, or retries/recoveries an accepted operation. For an explicitly reusable multi-test suite, RimTest's separate owner-lease adapter requests a DevBridge lease; DevBridge2 remains the lifecycle authority.

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

All RimTest command output is JSON; `--json` is accepted explicitly for agent scripts and does not add a second human transcript. Normal representative responses are budgeted at 256 bytes for a passing single test, 768 bytes for a failing single test, 256 bytes for a plain passing suite, 384 bytes for affected-test selection, and 2048 bytes for an affected/reuse suite with artifact freshness. Suite failure output may grow only with the number of failures. Detailed catalog metadata, RimContext reasons, DevBridge plans, and owner-system reports require their explicit commands/options; the common result path never includes logs, transcripts, operation lists, or full evidence.

## RimError failure analysis

After a DevBridge test failure, RimTest automatically asks DevBridge2 for its authoritative,
bounded `logs query --generation <generation> --since-launch --severity ERROR` response. It
passes that semantic source through a temporary internal handoff plus the versioned
`rimerror-integration/v1` identity envelope, then calls `rimerror latest --json --run <run>` so
nearby runs cannot be selected. The agent never needs to read or configure Player.log, the
generation log, or the RimError store. The normal automatic store is the current workspace's
`.rimerror/latest.json`, which makes `rimerror show <id>` the only usual drill-down command.

If the scoped source is missing, stale, cross-generation, bounded-output-invalid, or RimError
is unavailable, the test remains `status: "fail"` and emits `diagnosticStatus: "unavailable"`;
that condition is never converted into PASS. A completed diagnosis emits only the bounded
`diagnostic` projection and preserves its id. `status: "infrastructure"` is reserved for a
DevBridge/test-infrastructure failure that did not establish a trustworthy test result.

Configure `--rimerror-log` or `--rimerror-store` (or the existing environment variables) only
as an explicit fallback for unusual environments where automatic DevBridge-scoped acquisition
is not available. Direct Player.log reading is unnecessary and prohibited in the normal loop.

## RimContext affected-test selection

`affected [<changed-path> ...]` delegates indexing and impact computation to the current RimContext CLI command. Before asking for impact, RimTest refreshes RimContext's own index so edits made after `doctor` cannot be evaluated against stale definitions or types. With no paths, RimTest discovers tracked, staged, and relevant untracked Git changes automatically; explicit paths take precedence. Use `--base <git-ref>` when a Git base is required:

    rimctx affected PATH [PATH ...] --root PATH --depth 1..8 --limit N --json

RimTest accepts only the versioned `rimctx/v1` `affected` envelope. It maps each returned `kind`/`name` pair from `data.direct`, `data.dependent`, and `data.runtime_risk` to the catalog's `covers` entries. It does not parse C#, XML, Defs, Git history, or the RimContext index. Coverage names must match RimContext's current names exactly; current RimContext does not emit a separate `feature` impact entity, but feature entries remain valid catalog metadata for a future/currently supplied impact kind.

Normal output is a compact `rimtest-selection/v1` object, for example:

    {"schemaVersion":"rimtest-selection/v1","status":"ok","tests":["assembler-smoke"],"reasonCount":1}

`--explain` adds bounded impact-to-test reasons. Tests are sorted by ordinal id and duplicate matches are removed. For changed paths, a complete zero-impact RimContext result is `status: "conservative"` with error code `RIMCONTEXT_NO_TESTS`; it uses a valid non-empty fallback suite when configured and otherwise returns exit code 3 with an actionable next action. A truncated, unavailable, malformed, incompatible, uncovered, deleted-path, or renamed-path result is also `status: "conservative"`; it never means that no tests are needed. Use `--fallback-suite <suite>` (or `RIMTEST_FALLBACK_SUITE`) to select a broader configured suite. Without a usable fallback suite, the command returns exit code 3 so an agent must handle the uncertainty explicitly. A fallback suite selected successfully still returns exit code 0 and retains `status: "conservative"`.

When automatic Git discovery finds no changes, RimTest returns `status: "ok"` with an empty test list and does not invoke DevBridge, including when `--run` is present. This is the explicit clean-worktree fast path; a Git discovery failure is `status: "blocked"` and never means that no tests are needed.

RimContext command discovery uses `--rimcontext`, then `RIMTEST_RIMCONTEXT_CMD`/`RIMCONTEXT_CMD`, then a sibling RimContext/rimctx.cmd. The workspace root uses `--rimcontext-root`, `RIMTEST_RIMCONTEXT_ROOT`/`RIMCONTEXT_ROOT`, or the current directory. An optional store uses `--rimcontext-store` or `RIMTEST_RIMCONTEXT_STORE`/`RIMCONTEXT_STORE`. `--depth` defaults to 8 and `--limit` defaults to 100; both are forwarded unchanged to RimContext. Ctrl+C cancels the bounded RimContext client wait.

## Suite execution

`suite run <suite>` and `affected [<changed-path> ...] --run` execute selected catalog tests sequentially through the existing DevBridge recipe adapter. A suite with more than one test first calls each recipe's existing DevBridge `test recipe plan` operation. Explicit catalog isolation metadata may then authorize one DevBridge-owned lease for a compatible group; RimTest passes the lease to each recipe and verifies returned generation/lease identity. `alreadySatisfied` is used only to avoid an unnecessary restart before that lease. If planning cannot establish that every recipe is executable, no recipe run is started.

The policy is continue after ordinary test failures so the result identifies all failures; Ctrl+C or a DevBridge cancellation stops launching new children. There is no parallel or distributed execution. DevBridge2 remains authoritative for lifecycle, readiness, leases, profiles, recovery, and generation boundaries; RimTest only plans from explicit catalog permissions and validates owner responses.

Suite output uses `rimtest-suite-result/v1` and summarizes successful children numerically. An empty execution is `status: "conservative"` with `RIMTEST_EMPTY_EXECUTION`, never a normal pass. Failure output contains only failure-scaled references: test id, optional diagnostic id, failure fingerprint, evidence id, and error code. Optional `skipped`, `cancelled`, and conservative `selectionStatus` fields explain fallback or cancellation without embedding child DevBridge responses, logs, operations, or evidence. A known-safe affected run omits redundant `selectionStatus: "ok"`; an affected fallback run retains `selectionStatus: "conservative"` and `fallbackSuite` even when all fallback tests pass.
