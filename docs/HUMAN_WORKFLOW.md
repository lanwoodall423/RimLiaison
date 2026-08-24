# RimDev: the short human guide

If you are not comfortable with Git, use the guarded RimDev launcher instead of repairing
branches by hand.

## Normal workflow

1. Double-click **Open RimDev Terminal.cmd** in the RimLiaison repository folder.
2. Type `rimdev status`.
3. If it says everything is ready, type `rimdev all`.
4. If it says work is ready to merge, type `rimdev merge`.
5. Confirm the merge plan shown on screen; pressing Enter keeps the safe **No** default.

The commands you will usually need are:

```text
rimdev status
rimdev all
rimdev merge
```

`PASS` means an operation completed. `BLOCKED` means RimDev stopped safely and needs a
decision or a prerequisite. `SKIPPED` means there was no affected work to do. `FAIL` means
a check or operation failed and needs attention.

RimDev does not intentionally discard local work, reset files, or force-push. When it says
agent attention is required, leave the files alone and ask your development agent to follow
the **Next** instruction. Do not run risky Git repair commands yourself.

For the full command list, type `rimdev help`. The detailed workflow and workspace contract
are in [docs/rimdev.md](rimdev.md).
