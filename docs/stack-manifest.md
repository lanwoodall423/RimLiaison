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
repository is being initialized; `doctor` reports missing project configuration. `rimBridge` is
`via-devbridge` for the active stack or `disabled` for a project that intentionally has no live
bridge integration.

RimTest discovers the manifest from the current directory upward, using the Git root when no
manifest exists. Explicit CLI options override manifest values; existing environment variables
remain ahead of manifest defaults. The manifest contains no credentials, tokens, absolute paths, or
runtime state. Use `rimtest init --json` to create missing files safely; it never overwrites an
existing `AGENTS.md` or manifest without `--force`.
