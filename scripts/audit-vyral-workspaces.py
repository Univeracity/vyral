#!/usr/bin/env python3
"""Inventory Vyral checkouts without changing repositories or files."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import subprocess
import sys


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Workspace:
    path: Path
    registered: bool
    canonical: bool = False
    private: bool = False
    missing: bool = False


def run(*arguments: str, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        arguments,
        cwd=cwd,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
    )


def registered_worktrees() -> set[Path]:
    result = run("git", "worktree", "list", "--porcelain", "-z", cwd=REPOSITORY_ROOT)
    if result.returncode != 0:
        raise SystemExit("Unable to read Vyral's registered Git worktrees.")

    paths: set[Path] = set()
    for field in result.stdout.split("\0"):
        if field.startswith("worktree "):
            paths.add(Path(field.removeprefix("worktree ")).resolve())
    return paths


def candidates(projects_root: Path, registered: set[Path]) -> list[Workspace]:
    canonical = REPOSITORY_ROOT.resolve()
    private = (projects_root / "vyral-private").resolve()
    discovered = set(registered)

    discovered.update(path.resolve() for path in projects_root.glob("vyral*") if path.is_dir())
    worktrees_root = projects_root / "worktrees"
    if worktrees_root.is_dir():
        discovered.update(path.resolve() for path in worktrees_root.glob("vyral*") if path.is_dir())
        discovered.update(path.resolve() for path in worktrees_root.glob("*/vyral") if path.is_dir())

    return [
        Workspace(
            path=path,
            registered=path in registered,
            canonical=path == canonical,
            private=path == private,
            missing=not path.exists(),
        )
        for path in sorted(discovered, key=str)
    ]


def git_value(path: Path, *arguments: str) -> str:
    result = run("git", *arguments, cwd=path)
    return result.stdout.strip() if result.returncode == 0 else "-"


def disk_size(path: Path) -> str:
    result = run("du", "-sh", "--", str(path))
    if result.returncode != 0 or not result.stdout:
        return "-"
    return result.stdout.split(maxsplit=1)[0]


def describe(workspace: Workspace) -> tuple[str, str, str, str, str, str]:
    if workspace.missing:
        return "missing", "-", "-", "-", "-", str(workspace.path)

    if workspace.canonical:
        kind = "public"
    elif workspace.private:
        kind = "private"
    elif workspace.registered:
        kind = "worktree"
    else:
        kind = "unregistered"

    inside = git_value(workspace.path, "rev-parse", "--is-inside-work-tree") == "true"
    if not inside:
        return kind, "not-git", "-", "-", disk_size(workspace.path), str(workspace.path)

    status = run(
        "git", "status", "--porcelain=v1", "--untracked-files=all", cwd=workspace.path
    )
    dirty_count = len(status.stdout.splitlines()) if status.returncode == 0 else -1
    state = "clean" if dirty_count == 0 else f"dirty:{dirty_count}"
    branch = git_value(workspace.path, "branch", "--show-current") or "(detached)"
    head = git_value(workspace.path, "rev-parse", "--short=9", "HEAD")
    return kind, state, branch, head, disk_size(workspace.path), str(workspace.path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--projects-root", type=Path, default=Path("/projects"))
    parser.add_argument(
        "--strict",
        action="store_true",
        help="fail for missing worktrees, dirty secondary worktrees, or unregistered copies",
    )
    args = parser.parse_args()

    registered = registered_worktrees()
    inventory = candidates(args.projects_root.resolve(), registered)
    rows = [describe(workspace) for workspace in inventory]

    headers = ("kind", "state", "branch", "head", "size", "path")
    widths = [max(len(headers[index]), *(len(row[index]) for row in rows)) for index in range(6)]
    print("  ".join(headers[index].ljust(widths[index]) for index in range(6)))
    print("  ".join("-" * width for width in widths))
    for row in rows:
        print("  ".join(row[index].ljust(widths[index]) for index in range(6)))

    failures = []
    for workspace, row in zip(inventory, rows, strict=True):
        kind, state = row[0], row[1]
        if workspace.missing and workspace.registered:
            failures.append(f"missing registered worktree: {workspace.path}")
        elif kind == "worktree" and state != "clean":
            failures.append(f"secondary worktree is not clean: {workspace.path} ({state})")
        elif kind == "unregistered":
            failures.append(f"unregistered Vyral-like directory: {workspace.path}")

    if failures:
        print("\nReview required:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
    return 1 if args.strict and failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
