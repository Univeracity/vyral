# Workspace and build-artifact hygiene

Vyral uses one canonical public checkout and, when needed, short-lived Git worktrees.
This keeps `/projects` legible and makes cleanup depend on repository evidence instead
of directory names or age.

## Directory layout

| Purpose | Location | Retention |
| --- | --- | --- |
| Canonical public checkout | `$PROJECTS_ROOT/vyral` | Persistent |
| Adjacent private repository | Its own canonical checkout | Persistent and separate |
| Isolated public worktree | `$PROJECTS_ROOT/worktrees/vyral-<purpose>-<YYYYMMDD>` | Until its handoff is published |
| Reproducible disposable build | `/scratch/builds/vyral-<purpose>-<YYYYMMDD>` | Only while `/scratch` is mounted writable |
| Build output | Ignored path inside its checkout | Remove after validation or handoff |

Set `PROJECTS_ROOT` to the host's repository root (for example, `/projects`). Do not
create new Vyral siblings directly beneath it. Do not place archives, extracted package
trees, test results, or ad hoc copies there. A consumer that needs a stable checkout
should have its own clearly named repository or a registered worktree, not a copied
Vyral directory.

The private repository is not a cache or a second public worktree. Never consolidate it
by copying one repository over the other. Move a public-safe change across that boundary
only as a reviewed patch or through the deterministic public-export process. Keep raw
receipts, credentials, private vocabulary, local databases, models, BUGGER state, and
agent state outside public Git history.

## Creating a worktree

Start from current remote state, choose a descriptive branch, and register the checkout:

```bash
git fetch --prune origin
git worktree add -b codex/<purpose>-<YYYYMMDD> \
  "$PROJECTS_ROOT/worktrees/vyral-<purpose>-<YYYYMMDD>" origin/main
```

Use the canonical checkout when the task does not require isolation. Unique or unpushed
work must remain on storage the operator considers trusted. `/scratch` is suitable only
for reproducible output whose loss is acceptable, and only after
`findmnt --mountpoint /scratch` shows a writable mount.

## Handoff and removal

Before removing a worktree:

1. Run `git status --short --untracked-files=all` in that worktree and resolve every
   tracked or untracked file deliberately.
2. Record the branch and `HEAD`, then prove the needed commit is pushed, merged, tagged,
   or retained by another named ref. A deleted remote branch is not evidence of a merge.
3. Preserve any unique private receipt or operational evidence in its authoritative
   private location. Do not publish it merely to make cleanup easier.
4. Remove the checkout with `git worktree remove <path>`. Delete its local branch only
   after a separate reachability check.
5. Run `git worktree prune --dry-run` before pruning stale metadata, especially when a
   removable volume is unavailable or read-only.

Use the read-only inventory from the canonical checkout before and after maintenance:

```bash
python3 scripts/audit-vyral-workspaces.py
python3 scripts/audit-vyral-workspaces.py --strict
```

The strict form reports a failure when it finds a missing registered worktree, a dirty
noncanonical worktree, or a Vyral-like directory that is neither a registered worktree
nor one of the two canonical repositories.

## Build artifacts

Preview ignored cleanup with `git clean -ndX`, but do not apply that command wholesale:
ignored paths also contain private evidence and local runtime state. Remove only named,
reproducible classes such as `bin`, `obj`, `node_modules`, `__pycache__`, `.pytest_cache`,
`.mypy_cache`, package staging directories, and disposable test output. Re-run the build
or focused validation when cleanup itself could affect a handoff.

Keep large reusable models in the configured model cache. Keep retained review evidence
in the canonical private evidence location with a source commit, validation command, and
content hash so a later maintainer can distinguish evidence from disposable output.
