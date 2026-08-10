#!/usr/bin/env python3
"""Write machine-readable evidence after a Python runtime platform gate."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path
import platform
import sqlite3
import subprocess
import sys
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_SOURCE = ROOT / "runtimes/python/src"
sys.path.insert(0, str(RUNTIME_SOURCE))

from vyral_runtime import VyralRuntime  # noqa: E402


def _hash(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return "sha256:" + digest.hexdigest()


def _git(*arguments: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(ROOT), *arguments],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def _fts5_available() -> bool:
    with sqlite3.connect(":memory:") as connection:
        try:
            connection.execute(
                "CREATE VIRTUAL TABLE probe USING fts5(content)"
            )
        except sqlite3.OperationalError:
            return False
    return True


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "artifact_directory",
        type=Path,
        help="Directory containing one runtime wheel and one sdist.",
    )
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--scope",
        default="portable-matrix",
        choices=("portable-matrix", "local-rehearsal"),
    )
    arguments = parser.parse_args()
    artifact_root = arguments.artifact_directory.resolve()
    wheels = sorted(artifact_root.glob("vyral_runtime-*.whl"))
    sdists = sorted(artifact_root.glob("vyral_runtime-*.tar.gz"))
    if len(wheels) != 1 or len(sdists) != 1:
        parser.error(
            "artifact directory must contain exactly one runtime wheel and sdist"
        )

    readiness = VyralRuntime().readiness()
    if readiness.status != "ok" or readiness.contract is None:
        raise SystemExit("Runtime readiness was not healthy while writing evidence.")
    if not all(profile.available for profile in readiness.profiles):
        raise SystemExit("A required runtime profile was unavailable.")
    source_status = _git(
        "status",
        "--porcelain",
        "--untracked-files=all",
        "--",
        ".",
        ":(exclude)artifacts",
    )
    receipt: dict[str, Any] = {
        "schemaVersion": "vyral.python-runtime-platform.v1",
        "status": "passed",
        "scope": arguments.scope,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "source": {
            "commit": _git("rev-parse", "HEAD"),
            "dirty": bool(source_status),
        },
        "environment": {
            "implementation": sys.implementation.name,
            "pythonVersion": platform.python_version(),
            "pythonExecutable": sys.executable,
            "system": platform.system(),
            "release": platform.release(),
            "machine": platform.machine(),
            "platform": platform.platform(),
            "sqliteVersion": sqlite3.sqlite_version,
            "fts5Available": _fts5_available(),
        },
        "runtime": {
            "version": readiness.runtime_version,
            "contractVersion": readiness.contract_version,
            "fixtureVersion": readiness.fixture_version,
            "operationCount": readiness.contract.operation_count,
            "restOperationCount": readiness.contract.rest_operation_count,
            "schemaCount": readiness.contract.schema_count,
            "maturity": readiness.maturity,
            "fullLocalReady": readiness.full_local_ready,
            "profiles": [
                {
                    "id": profile.profile_id.value,
                    "available": profile.available,
                    "maturity": profile.maturity.value,
                }
                for profile in readiness.profiles
            ],
        },
        "artifacts": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": _hash(path),
            }
            for path in (*wheels, *sdists)
        ],
        "gates": [
            "contract-sync",
            "language-neutral-conformance",
            "unittest-discovery",
            "branch-coverage-regression-floor-77.5",
            "strict-mypy",
            "wheel-build",
            "sdist-build",
            "clean-wheel-install",
            "clean-sdist-install",
            "server-extra-install",
        ],
    }
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        "python-runtime-platform-receipt=ok "
        f"python={platform.python_version()} system={platform.system()} "
        f"runtime={readiness.runtime_version} output={output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
