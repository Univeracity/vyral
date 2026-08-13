#!/usr/bin/env python3
"""Regression tests for the human adapter qualification view."""

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parent.parent
RENDERER = ROOT / "scripts/render-adapter-qualification.py"
SOURCE = ROOT / "qualification/adapter-qualification.json"
CHECKED = ROOT / "qualification/README.md"


def run(*arguments: str, ok: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [sys.executable, str(RENDERER), *arguments],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    if (result.returncode == 0) is not ok:
        raise AssertionError(
            f"renderer return code {result.returncode}: {result.stderr}"
        )
    return result


def main() -> None:
    with tempfile.TemporaryDirectory(
        prefix="vyral-qualification-view-"
    ) as temporary:
        root = Path(temporary)
        first = root / "first.md"
        second = root / "second.md"
        run(str(SOURCE), str(first))
        run(str(SOURCE), str(second))
        if first.read_bytes() != second.read_bytes():
            raise AssertionError("qualification rendering is not deterministic")
        if first.read_bytes() != CHECKED.read_bytes():
            raise AssertionError("checked qualification README is stale")
        run(str(SOURCE), str(CHECKED), "--check")

        rendered = first.read_text(encoding="utf-8")
        required = (
            "Claims must be supported by evidence.",
            "AWS DynamoDB and SQS execution runtime",
            "Azure Durable Functions execution runtime",
            "Local conformant",
            "1 live-qualified",
            "[canonical JSON](adapter-qualification.json)",
        )
        if any(value not in rendered for value in required):
            raise AssertionError("qualification view omitted required content")
        forbidden = (
            "manual `Google Live Qualification`",
            "manual `AWS Live Qualification`",
        )
        if any(value in rendered for value in forbidden):
            raise AssertionError("qualification view retained workflow history")

        invalid = json.loads(SOURCE.read_text(encoding="utf-8"))
        invalid["summary"]["adapterCount"] += 1
        invalid_path = root / "invalid-summary.json"
        invalid_path.write_text(json.dumps(invalid), encoding="utf-8")
        run(str(invalid_path), str(root / "invalid.md"), ok=False)

        injected = json.loads(SOURCE.read_text(encoding="utf-8"))
        injected["adapters"][0]["displayName"] = "unsafe\nrow"
        injected_path = root / "injected.json"
        injected_path.write_text(json.dumps(injected), encoding="utf-8")
        run(str(injected_path), str(root / "injected.md"), ok=False)

    print("adapter-qualification-view-test=ok")


if __name__ == "__main__":
    main()
