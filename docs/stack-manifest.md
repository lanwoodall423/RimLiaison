# Stack manifest

Target mod repositories may commit `.rimdev/stack.json` using `rimdev-stack/v1`:

```json
{
  "schemaVersion": "rimdev-stack/v1",
  "project": "MyMod",
  "devBridgeProject": "mymod",
  "catalog": "TestCatalog/rimtest.catalog.json",
  "fallbackSuite": "smoke",
  "rimBridge": "via-devbridge"
}
```

`catalog` is repository-relative. `devBridgeProject` and `fallbackSuite` may be omitted while a
repository is being initialized. When the catalog is valid, `rimtest init --json` selects a
deterministic non-empty fallback suite (preferring `smoke`) but never guesses a DevBridge project
alias. `rimBridge` is `via-devbridge` for the active stack or `disabled` for a project that
intentionally has no live bridge integration.

RimTest discovers the manifest from the current directory upward, using the Git root when no
manifest exists. Explicit CLI options override manifest values when a manifest is being created or
when `--force` is supplied. Without `--force`, `rimtest init --json` merges only missing or
semantically unusable optional fields and preserves existing project, catalog, bridge, and agent
instructions. The manifest contains no credentials, tokens, absolute paths, or runtime state.

The onboarding handoff is intentionally RimTest-owned:

1. Run `rimtest doctor --json`.
2. Follow its `nextAction` for `STACK_MANIFEST_MISSING`, missing project/fallback configuration,
   or catalog path validation. Missing project aliases are supplied explicitly; RimTest does not
   infer them from lower-level DevBridge state.
3. For malformed manifests, use the reported `rimtest init --json --manifest-only --force` action.
   This repairs only `.rimdev/stack.json`. For a missing catalog path, the handoff uses the same
   manifest-only form with `--catalog`; catalog contents remain target-repository-owned and are
   checked with `rimtest validate --json --catalog <path>`.
4. Repeat `rimtest doctor --json` until it reports `status: "ready"`, then follow its next action.

Plain `rimtest init --json --force` intentionally replaces the canonical `AGENTS.md` and rewrites
the manifest. Use `--manifest-only` when an existing agent handoff must be preserved.
