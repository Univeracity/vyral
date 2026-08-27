#!/usr/bin/env python3
"""Regression checks for exact dirty-worktree evidence binding."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


HELPER = Path(__file__).with_name("compute-source-tree-evidence.py")


def _run(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [*arguments],
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def _evidence(root: Path) -> dict[str, object]:
    result = _run(sys.executable, str(HELPER), "--root", str(root))
    return json.loads(result.stdout)


def _commit_evidence(root: Path, commit: str) -> dict[str, object]:
    result = _run(
        sys.executable,
        str(HELPER),
        "--root",
        str(root),
        "--commit",
        commit,
    )
    return json.loads(result.stdout)


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="vyral-source-tree-evidence-") as directory:
        root = Path(directory)
        _run("git", "-C", str(root), "init", "--quiet")
        _run("git", "-C", str(root), "config", "user.name", "Vyral Test")
        _run("git", "-C", str(root), "config", "user.email", "test@example.invalid")
        (root / ".gitignore").write_text("ignored.tmp\n", encoding="utf-8")
        (root / "source.txt").write_text("one\n", encoding="utf-8")
        _run("git", "-C", str(root), "add", ".gitignore", "source.txt")
        _run("git", "-C", str(root), "commit", "--quiet", "-m", "fixture")

        clean = _evidence(root)
        repeated = _evidence(root)
        if clean != repeated or clean["sourceDirty"] is not False or clean["fileCount"] != 2:
            raise SystemExit("Clean source-tree evidence is not deterministic and exact.")
        committed = _commit_evidence(root, str(clean["sourceCommit"]))
        if committed != clean:
            raise SystemExit("Commit and clean-worktree evidence do not use the same digest model.")

        (root / "ignored.tmp").write_text("ignored\n", encoding="utf-8")
        if _evidence(root) != clean:
            raise SystemExit("Ignored local state changed source-tree evidence.")

        (root / "source.txt").write_text("two\n", encoding="utf-8")
        modified = _evidence(root)
        if modified["sourceDirty"] is not True or modified["sourceTreeDigest"] == clean["sourceTreeDigest"]:
            raise SystemExit("A tracked modification did not change dirty source-tree evidence.")
        if _commit_evidence(root, str(clean["sourceCommit"])) != clean:
            raise SystemExit("Dirty worktree state changed immutable commit evidence.")

        (root / "untracked.txt").write_text("new\n", encoding="utf-8")
        untracked = _evidence(root)
        if untracked["fileCount"] != 3 or untracked["sourceTreeDigest"] == modified["sourceTreeDigest"]:
            raise SystemExit("An untracked source file did not enter source-tree evidence.")

        os.symlink("source.txt", root / "untracked-link")
        rejected = _run(
            sys.executable,
            str(HELPER),
            "--root",
            str(root),
            check=False,
        )
        if rejected.returncode == 0 or "requires regular files" not in rejected.stderr:
            raise SystemExit("Source-tree evidence accepted a symbolic link.")

    print("source-tree-evidence-test=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
