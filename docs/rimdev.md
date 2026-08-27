# RimDev human workflow

`rimdev` is the small, guarded command surface for routine work across the RimWorld
repositories. The canonical invocation is:

```text
rimliaison rimdev <operation>
```

On Windows, `rimdev.cmd` is a thin convenience alias for the same command. It does not
implement a second workflow.

For a novice-friendly start, double-click **Open RimDev Terminal.cmd** at the repository root.
It opens a terminal in the correct folder, enables the repository-local command for that
session only, and prints the common commands. Type `rimdev` for a quick menu or
`rimdev help` for plain-language help. See [HUMAN_WORKFLOW.md](HUMAN_WORKFLOW.md) for the
short normal workflow.

## Workspace discovery

RimDev uses each repository's existing `.rimdev/stack.json` manifest. Starting in a managed
repository, it discovers the parent workspace and its direct managed children. A workspace can
make the set and deployment metadata explicit with `.rimdev/workspace.json`:

```json
{
  "schemaVersion": "rimdev-workspace/v1",
  "deploymentRoot": "Staging",
  "repositories": [
    {
      "path": "Repos/Frontier",
      "dependsOn": [],
      "deploymentRoot": "Staging/Frontier",
      "deploymentTarget": "1.6/Assemblies/Frontier.dll",
      "buildProject": "Source/Frontier.csproj",
      "configuration": "Release"
    }
  ]
}
```

Repository paths are relative to the workspace. `deploymentRoot` and `deploymentTarget` are
configuration, not personal hard-coded defaults; the existing DevBridge development descriptor
is used when those fields are omitted. Use `--root <workspace-root>` when discovery should start
from a particular workspace.

The same machine-local manifest may also carry the runtime identity used by managed live gates:

```json
{
  "rimWorldRoot": "C:/Games/Steam/steamapps/common/RimWorld",
  "rimWorldExecutable": "C:/Games/Steam/steamapps/common/RimWorld/RimWorldWin64.exe",
  "devBridgeRuntimeRoot": "C:/Games/Steam/steamapps/common/RimWorld/Mods/DevBridge2",
  "devBridgeSourceRoot": "C:/RimDev/Repos/DevBridge2"
}
```

These values identify the installed runtime, not a repository or pinned worktree. `RIMWORLD_ROOT`
and `RIMWORLD_EXECUTABLE` are deliberate per-process overrides and take precedence over the
manifest. A missing or invalid explicit override fails closed; there is no unrelated-install
fallback. The manifest is next, followed only by DevBridge's validated installed-layout fallback.
Publish, sync, and source-only build operations do not require these live identity values.

## Operations

| Command | Behavior |
| --- | --- |
| `rimdev status` | Read-only table of repository paths, branches, worktree state, ahead/behind, build/deployment cache state, and PR readiness when GitHub information is available. |
| `rimdev sync` | Fetches remotes and performs only safe `--ff-only` updates. Dirty-behind, detached, missing-upstream, and diverged repositories are reported without changing them. |
| `rimdev build` | Builds only repositories with affected build inputs, plus downstream repositories whose configured dependencies changed, in dependency order; source/output identity is recorded outside the worktree. |
| `rimdev test` | Delegates affected selection and execution to `rimliaison affected --run --json`, including downstream consumers of changed workspace dependencies; a proven unchanged source identity can reuse a prior RimTest result. |
| `rimdev push` | Fetches first, then requires the canonical RimTest/RimLiaison publication-evidence check for each repository before pushing committed ahead work. Valid reusable evidence is reused; missing, failed, invalidated, stale, or infrastructure-blocked evidence prevents that repository's push. Dirty files are left alone and explained. |
| `rimdev merge` | Finds one open, non-draft, mergeable PR with passing checks and matching source/target identities. If several candidates exist, it asks for the exact PR number. It prints a compact plan and asks for explicit confirmation; Enter means No. `--yes` is available for a deliberate scripted confirmation. |
| `rimdev all` | Runs sync, affected test, affected build, deployment, canonical publication-evidence check, and safe push per repository, then reports PR readiness. It never merges; run `rimdev merge` separately. One repository's failure does not globally abort unrelated repositories; configured dependents are conservatively blocked. |

The default output is concise and human-readable. Add `--json` for the versioned
`rimdev-result/v1` envelope. `--yes`/`--confirm` is accepted only by `rimdev merge`.

Exit code `0` means the requested operation completed safely, `1` means a build/test/deploy/merge
operation failed, and `3` means a repository or infrastructure condition blocked or partially
completed the operation. A blocked DevBridge lease/readiness result is infrastructure evidence,
not proof that source code failed; follow the reported next action, normally
`rimliaison doctor --json`.

## Safety and state

RimDev never runs `git reset --hard`, force-pushes, auto-commits, auto-stashes, invents conflict
resolutions, or silently checks out another branch. One blocked repository does not prevent safe
read-only processing of the others. Multi-repository mutating commands return a partial summary so
successful repositories and blockers remain visible together.

Build/test evidence is stored outside repositories under the local RimDev state directory. Set
`RIMDEV_STATE_ROOT` to choose that location. RimDev uses canonical RimTest/RimLiaison validation
provenance for test validity, reuse, invalidation, and publication; legacy RimDev test evidence
files cannot authorize work. RimDev build evidence remains operational metadata for locating and
hash-checking produced outputs and never satisfies a test-evidence requirement. Change
classification is owner/path-aware: known transient locations such as `bin`, `obj`, `.rimctx`,
and `.rimdev/observability` are generated, while `.rimdev/stack.json`, arbitrary tracked
`.dll`/`.pdb` files, and declared production artifacts remain meaningful. DevBridge build-output
provenance is still required before a tracked build-owned artifact mutation is accepted. A
worktree with only provider-classified generated changes is not treated as user-dirty; mixed or
unknown changes remain conservative and require attention.
