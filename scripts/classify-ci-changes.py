#!/usr/bin/env python3
"""Select the smallest safe CI surface for a change.

Pull requests may skip language-specific build and analysis work only when every
changed path is understood. Pushes, tags, schedules, and manual runs always use
the complete surface so the canonical branch retains full evidence.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import PurePosixPath
import subprocess
from typing import Iterable


SCOPES = ("dotnet", "csharp", "javascript", "python", "go")

CENTRAL_PREFIXES = (
    ".github/workflows/",
    ".config/",
    "conformance/",
    "contracts/",
    "deploy/",
    "packaging/",
    "scripts/",
)

DOTNET_PREFIXES = (
    "samples/",
    "src/",
    "tests/",
    "tools/",
)

JAVASCRIPT_PREFIXES = ("clients/javascript/",)
PYTHON_PREFIXES = (
    "clients/python/",
    "examples/python/",
    "runtimes/python/",
)
GO_PREFIXES = (
    "clients/go/",
    "workers/execution-smoke-go/",
)

DOC_ONLY_PREFIXES = (
    "benchmarks/",
    "design/",
    "docs/",
    "qualification/",
)

DOC_ONLY_ROOTS = {
    "CODE_OF_CONDUCT.md",
    "CONTRIBUTING.md",
    "LICENSE",
    "PUBLIC-EXPORT-MANIFEST.json",
    "README.md",
    "ROADMAP.md",
    "SECURITY.md",
    "THIRD-PARTY-NOTICES.md",
    "TRADEMARKS.md",
}

DOTNET_ROOTS = {
    "Directory.Build.props",
    "Vyral.sln",
}

CENTRAL_ROOTS = {
    ".dockerignore",
    ".gitattributes",
    ".gcloudignore",
    ".gitignore",
    "Dockerfile",
}


def _all_scopes() -> dict[str, bool]:
    return {scope: True for scope in SCOPES}


def _normalize(paths: Iterable[str]) -> list[str]:
    selected: list[str] = []
    for raw in paths:
        value = raw.strip().replace("\\", "/")
        while value.startswith("./"):
            value = value[2:]
        if value and value not in selected:
            selected.append(value)
    return sorted(selected)


def classify(paths: Iterable[str], *, full: bool = False) -> dict[str, bool]:
    """Return conservative language/build scopes for *paths*."""

    normalized = _normalize(paths)
    if full or not normalized:
        return _all_scopes()

    result = {scope: False for scope in SCOPES}
    unknown = False
    for path in normalized:
        # Reject absolute paths and parent traversal rather than interpreting an
        # unsafe or malformed diff as documentation-only.
        pure = PurePosixPath(path)
        if pure.is_absolute() or ".." in pure.parts:
            unknown = True
            continue

        if path in DOC_ONLY_ROOTS or path.startswith(DOC_ONLY_PREFIXES):
            continue
        if path in CENTRAL_ROOTS or path.startswith(CENTRAL_PREFIXES):
            return _all_scopes()
        if path in DOTNET_ROOTS or path.startswith(DOTNET_PREFIXES):
            result["dotnet"] = True
            result["csharp"] = True
            continue
        if path.startswith(JAVASCRIPT_PREFIXES):
            result["javascript"] = True
            continue
        if path.startswith(PYTHON_PREFIXES):
            result["python"] = True
            continue
        if path.startswith(GO_PREFIXES):
            result["go"] = True
            continue

        # Markdown outside an understood source surface cannot affect a build.
        # Everything else fails open to the complete suite.
        if path.lower().endswith(".md"):
            continue
        unknown = True

    if unknown:
        return _all_scopes()
    return result


def changed_paths(base: str, head: str) -> list[str]:
    if not base or not head:
        raise ValueError("pull-request classification requires base and head SHAs")
    completed = subprocess.run(
        ["git", "diff", "--name-only", "--no-renames", f"{base}...{head}"],
        check=True,
        capture_output=True,
        text=True,
    )
    return _normalize(completed.stdout.splitlines())


def _write_github_output(path: str, result: dict[str, bool]) -> None:
    clients = result["javascript"] or result["python"] or result["go"]
    languages = {
        "csharp": result["csharp"],
        "javascript": result["javascript"],
        "python": result["python"],
        "go": result["go"],
    }
    values: dict[str, str] = {
        **{key: str(value).lower() for key, value in result.items()},
        "clients": str(clients).lower(),
        "languages": json.dumps(languages, separators=(",", ":")),
    }
    with open(path, "a", encoding="utf-8", newline="\n") as output:
        for key, value in values.items():
            output.write(f"{key}={value}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", required=True)
    parser.add_argument("--base", default="")
    parser.add_argument("--head", default="")
    parser.add_argument("--path", action="append", dest="paths")
    parser.add_argument("--github-output", default=os.environ.get("GITHUB_OUTPUT", ""))
    arguments = parser.parse_args()

    full = arguments.event != "pull_request"
    paths = arguments.paths
    if paths is None and not full:
        paths = changed_paths(arguments.base, arguments.head)
    result = classify(paths or (), full=full)
    if arguments.github_output:
        _write_github_output(arguments.github_output, result)

    payload = {
        "event": arguments.event,
        "mode": "full" if full else "change-scoped",
        "paths": _normalize(paths or ()),
        "scopes": result,
        "clients": result["javascript"] or result["python"] or result["go"],
    }
    print(json.dumps(payload, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
