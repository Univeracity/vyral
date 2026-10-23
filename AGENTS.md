# Vyral agent guidance

Follow the user's current instructions and the global agent guidance first.

## Public and private repositories

- This repository is the canonical public checkout. Commit only distributable source,
  tests, public documentation, and redacted qualification records here.
- Keep any adjacent private repository separate. Do not overlay, copy, or synchronize
  its tree into the public checkout. Transfer a specific change only through a reviewed
  patch or the repository's public-export procedure.
- Keep credentials, private denylist terms, BUGGER state, agent state, raw receipts,
  local databases, models, and operational evidence out of public Git history.

## Worktrees and generated files

- Use the canonical checkout when isolation is unnecessary.
- Put durable Vyral worktrees under
  `$PROJECTS_ROOT/worktrees/vyral-<purpose>-<YYYYMMDD>`. Do not create new Vyral
  siblings directly under the projects root. Use `/scratch/builds` only for fully
  reproducible work after verifying that `/scratch` is mounted writable.
- Keep generated output inside its checkout and in an ignored directory. Do not put
  temporary archives, package staging trees, or test output directly under `/projects`.
- Before removing a worktree, verify that it is clean and that every needed commit is
  pushed, merged, or otherwise retained by a named ref. Use `git worktree remove`; never
  delete a registered worktree directory directly.
- Run `python3 scripts/audit-vyral-workspaces.py` before and after workspace cleanup.
  Follow [the workspace hygiene guide](docs/contributing/worktree-hygiene.md) for the
  evidence and teardown procedure.
