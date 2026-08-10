#!/usr/bin/env python3
"""Verify that local Markdown links stay inside a tree and resolve to files."""

from __future__ import annotations

import argparse
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote


LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
EXTERNAL_SCHEMES = ("http://", "https://", "mailto:", "tel:", "data:")


def link_target(raw: str) -> str:
    raw = raw.strip()
    if raw.startswith("<") and ">" in raw:
        return raw[1 : raw.index(">")]
    return raw.split(maxsplit=1)[0]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", nargs="?", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--all", action="store_true", help="Scan every Markdown file under root")
    args = parser.parse_args()
    root = args.root.resolve()
    if not root.is_dir():
        parser.error("root must be a directory")

    if not args.all:
        result = subprocess.run(
            ["git", "-C", str(root), "ls-files", "-z", "--", "*.md"],
            check=True,
            stdout=subprocess.PIPE,
        )
        markdown_files = [root / item.decode("utf-8") for item in result.stdout.split(b"\0") if item]
    else:
        markdown_files = sorted(root.rglob("*.md"))

    failures: list[str] = []
    checked = 0
    for markdown in markdown_files:
        if not markdown.exists():
            continue
        if ".git" in markdown.relative_to(root).parts:
            continue
        content = markdown.read_text(encoding="utf-8")
        for match in LINK.finditer(content):
            target = unquote(link_target(match.group(1)))
            if not target or target.startswith("#") or target.lower().startswith(EXTERNAL_SCHEMES):
                continue
            relative_target = target.split("#", 1)[0].split("?", 1)[0]
            if not relative_target:
                continue
            if relative_target.startswith("/"):
                failures.append(f"{markdown.relative_to(root)}: absolute local link {target!r}")
                continue
            resolved = (markdown.parent / relative_target).resolve()
            try:
                resolved.relative_to(root)
            except ValueError:
                failures.append(f"{markdown.relative_to(root)}: link escapes public tree {target!r}")
                continue
            checked += 1
            if not resolved.exists():
                failures.append(f"{markdown.relative_to(root)}: missing local link target {target!r}")

    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        raise SystemExit(f"Markdown link verification failed with {len(failures)} error(s).")
    print(f"markdown-links=ok checked={checked}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
