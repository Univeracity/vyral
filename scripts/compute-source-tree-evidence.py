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


def compute_commit(root: Path, commit: str) -> dict[str, object]:
    """Compute the same source digest from one immutable Git commit."""
    root = root.resolve()
    if not (root / ".git").exists():
        raise SystemExit(f"Not a Git worktree: {root}")
    if not commit or any(character not in "0123456789abcdef" for character in commit):
        raise SystemExit("Commit evidence requires a lowercase hexadecimal Git object ID.")
    resolved = _git(root, "rev-parse", "--verify", f"{commit}^{{commit}}")
    resolved_commit = resolved.decode("ascii").strip()
    tree = _git(root, "ls-tree", "-r", "-z", "--full-tree", resolved_commit)
    entries: list[tuple[str, str, str]] = []
    for encoded in tree.split(b"\0"):
        if not encoded:
            continue
        metadata, encoded_path = encoded.split(b"\t", 1)
        mode, object_type, object_id = metadata.decode("ascii").split(" ")
        if object_type != "blob":
            raise SystemExit("Source-tree evidence permits only Git blobs.")
        relative = encoded_path.decode("utf-8", errors="strict")
        if "\n" in relative or "\r" in relative:
            raise SystemExit("Source-tree evidence does not permit control characters in paths.")
        if mode == "120000":
            raise SystemExit(f"Source-tree evidence requires regular files: {relative}")
        normalized_mode = "755" if mode == "100755" else "644"
        content = _git(root, "cat-file", "blob", object_id)
        entries.append((relative, normalized_mode, hashlib.sha256(content).hexdigest()))

    tree_digest = hashlib.sha256()
    for relative, mode, content_digest in sorted(entries):
        tree_digest.update(f"{mode} {content_digest} {relative}\n".encode("utf-8"))
    return {
        "schemaVersion": "vyral.source-tree-evidence.v1",
        "sourceCommit": resolved_commit,
        "sourceDirty": False,
        "sourceTreeDigest": "sha256:" + tree_digest.hexdigest(),
        "fileCount": len(entries),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--commit", help="compute evidence for an immutable commit")
    arguments = parser.parse_args()
    evidence = (
        compute_commit(arguments.root, arguments.commit)
        if arguments.commit
        else compute(arguments.root)
    )
    print(json.dumps(evidence, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
