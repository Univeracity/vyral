#!/usr/bin/env python3
"""Unit checks for Python runtime platform matrix aggregation."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/verify-python-runtime-platform-matrix.py"
SPEC = importlib.util.spec_from_file_location(
    "vyral_python_runtime_platform_matrix", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Unable to load the platform matrix verifier.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def _receipt(system: str, python: str) -> dict[str, object]:
    return {
        "schemaVersion": "vyral.python-runtime-platform.v1",
        "status": "passed",
        "scope": "portable-matrix",
        "source": {"commit": "a" * 40, "dirty": False},
        "environment": {
            "system": system,
            "pythonVersion": python + ".9",
            "platform": f"{system}-{python}",
            "sqliteVersion": "3.45.0",
            "fts5Available": True,
        },
        "runtime": {
            "version": "0.1.1",
            "contractVersion": "0.3.0",
            "fixtureVersion": "1.0.0",
            "profiles": [{"id": "portable", "available": True}],
        },
        "gates": sorted(MODULE.REQUIRED_GATES),
    }


def main() -> int:
    with tempfile.TemporaryDirectory(
        prefix="vyral-platform-matrix-test-"
    ) as temporary:
        root = Path(temporary)
        receipts: list[Path] = []
        for system, python in sorted(MODULE.EXPECTED_MATRIX):
            path = root / system / python / "platform.json"
            path.parent.mkdir(parents=True)
            path.write_text(
                json.dumps(_receipt(system, python)),
                encoding="utf-8",
            )
            receipts.append(path)
        result = MODULE.verify(receipts)
        if result["status"] != "passed" or result["cellCount"] != 9:
            raise SystemExit("A complete platform matrix did not pass.")

        duplicate = root / "duplicate" / "platform.json"
        duplicate.parent.mkdir()
        duplicate.write_text(
            json.dumps(_receipt("Linux", "3.10")),
            encoding="utf-8",
        )
        try:
            MODULE.verify([*receipts[:-1], duplicate])
        except MODULE.MatrixError:
            pass
        else:
            raise SystemExit("A duplicate/incomplete matrix was accepted.")
    print("python-runtime-platform-matrix-test=ok cells=9")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
