#!/usr/bin/env python3
"""Compute a content digest for the exact tracked-plus-untracked source worktree."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import stat
import subprocess


def _git(root: Path, *arguments: str) -> bytes:
    return subprocess.run(
        ["git", "-C", str(root), *arguments],
        check=True,
        stdout=subprocess.PIPE,
    ).stdout


def _content_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def compute(root: Path) -> dict[str, object]:
    root = root.resolve()
    if not (root / ".git").exists():
        raise SystemExit(f"Not a Git worktree: {root}")

    encoded_paths = _git(
        root,
        "ls-files",
        "-z",
        "--cached",
        "--others",
        "--exclude-standard",
    ).split(b"\0")
    paths = sorted(
        path.decode("utf-8", errors="strict")
        for path in encoded_paths
        if path
    )

    tree_digest = hashlib.sha256()
    file_count = 0
    for relative in paths:
        if "\n" in relative or "\r" in relative:
            raise SystemExit("Source-tree evidence does not permit control characters in paths.")
        source = root / relative
        if not source.exists():
            # A tracked deletion is represented by its absence from the resulting tree.
            continue
        if source.is_symlink() or not source.is_file():
            raise SystemExit(f"Source-tree evidence requires regular files: {relative}")
        mode = "755" if source.stat().st_mode & stat.S_IXUSR else "644"
        content_digest = _content_digest(source)
        tree_digest.update(f"{mode} {content_digest} {relative}\n".encode("utf-8"))
        file_count += 1

    status = _git(root, "status", "--porcelain=v1", "--untracked-files=all")
    commit = _git(root, "rev-parse", "HEAD").decode("ascii").strip()
    return {
        "schemaVersion": "vyral.source-tree-evidence.v1",
        "sourceCommit": commit,
        "sourceDirty": bool(status.strip()),
        "sourceTreeDigest": "sha256:" + tree_digest.hexdigest(),
        "fileCount": file_count,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    arguments = parser.parse_args()
    print(json.dumps(compute(arguments.root), sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
