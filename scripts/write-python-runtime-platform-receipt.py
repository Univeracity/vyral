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
from typing import Any, Mapping


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


def _mapping(value: object, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ValueError(f"{label} must be an object.")
    return value


def _load_clean_install_evidence(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(
            "Clean-install evidence must be valid UTF-8 JSON."
        ) from error
    root = _mapping(value, "clean-install evidence")
    if (
        root.get("schemaVersion")
        != "vyral.python-runtime-clean-install.v1"
        or root.get("status") != "passed"
    ):
        raise ValueError("Clean-install evidence is not a passing v1 receipt.")
    environment = _mapping(
        root.get("environment"),
        "clean-install environment",
    )
    if (
        environment.get("system") != platform.system()
        or environment.get("pythonVersion") != platform.python_version()
    ):
        raise ValueError(
            "Clean-install evidence was not produced by this platform cell."
        )
    budget = root.get("firstCitationBudgetMs")
    if not isinstance(budget, (int, float)) or isinstance(budget, bool):
        raise ValueError("Clean-install evidence has no numeric time budget.")
    artifacts = root.get("artifacts")
    if not isinstance(artifacts, list) or len(artifacts) != 2:
        raise ValueError(
            "Clean-install evidence must contain wheel and sdist results."
        )
    summaries: list[dict[str, Any]] = []
    kinds: set[str] = set()
    for index, artifact_value in enumerate(artifacts):
        artifact = _mapping(
            artifact_value,
            f"clean-install artifacts[{index}]",
        )
        kind = artifact.get("artifactKind")
        if not isinstance(kind, str):
            raise ValueError("Clean-install artifact kind must be a string.")
        kinds.add(kind)
        quickstart = _mapping(
            artifact.get("quickstart"),
            f"clean-install {kind} quickstart",
        )
        first_citation_ms = quickstart.get("firstCitationMs")
        first_command_ms = quickstart.get("firstCommandMs")
        if (
            not isinstance(first_citation_ms, (int, float))
            or isinstance(first_citation_ms, bool)
            or not isinstance(first_command_ms, (int, float))
            or isinstance(first_command_ms, bool)
            or float(first_citation_ms) > float(budget)
            or float(first_command_ms) > float(budget)
        ):
            raise ValueError(
                f"Clean-install {kind} exceeded the first-use time budget."
            )
        if any(
            quickstart.get(field) is not True
            for field in (
                "receiptBeforeDispatch",
                "restartPreservedRunIdentity",
                "secondProcessReplayed",
                "safeReset",
            )
        ):
            raise ValueError(
                f"Clean-install {kind} did not prove the complete local path."
            )
        summaries.append(
            {
                "artifactKind": kind,
                "artifactName": artifact.get("artifactName"),
                "installMs": artifact.get("installMs"),
                "firstCommandMs": first_command_ms,
                "firstCitationMs": first_citation_ms,
                "durableReceiptMs": quickstart.get("durableReceiptMs"),
                "completedMs": quickstart.get("completedMs"),
            }
        )
    if kinds != {"wheel", "sdist"}:
        raise ValueError(
            "Clean-install evidence must prove one wheel and one sdist."
        )
    return {
        "schemaVersion": root.get("schemaVersion"),
        "status": root.get("status"),
        "sha256": _hash(path),
        "firstCitationBudgetMs": budget,
        "serverExtraVerified": root.get("serverExtraVerified") is True,
        "artifacts": sorted(summaries, key=lambda item: str(item["artifactKind"])),
    }


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
    parser.add_argument(
        "--clean-install-evidence",
        type=Path,
        help=(
            "Passing clean wheel/sdist quickstart receipt. Required for "
            "portable-matrix evidence."
        ),
    )
    arguments = parser.parse_args()
    artifact_root = arguments.artifact_directory.resolve()
    wheels = sorted(artifact_root.glob("vyral_runtime-*.whl"))
    sdists = sorted(artifact_root.glob("vyral_runtime-*.tar.gz"))
    if len(wheels) != 1 or len(sdists) != 1:
        parser.error(
            "artifact directory must contain exactly one runtime wheel and sdist"
        )
    if (
        arguments.scope == "portable-matrix"
        and arguments.clean_install_evidence is None
    ):
        parser.error(
            "--clean-install-evidence is required for portable-matrix receipts"
        )
    clean_install = (
        _load_clean_install_evidence(
            arguments.clean_install_evidence.resolve()
        )
        if arguments.clean_install_evidence is not None
        else None
    )
    if clean_install is not None:
        clean_artifacts = clean_install.get("artifacts")
        assert isinstance(clean_artifacts, list)
        clean_names = {
            str(item.get("artifactName"))
            for item in clean_artifacts
            if isinstance(item, Mapping)
        }
        expected_names = {wheels[0].name, sdists[0].name}
        if clean_names != expected_names:
            parser.error(
                "clean-install evidence does not describe the supplied artifacts"
            )
        if (
            arguments.scope == "portable-matrix"
            and clean_install.get("serverExtraVerified") is not True
        ):
            parser.error(
                "portable-matrix clean-install evidence must verify the server extra"
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
        "localExperience": clean_install,
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
            *(
                (
                    "clean-wheel-local-quickstart",
                    "clean-sdist-local-quickstart",
                    "first-citation-under-five-minutes",
                    "receipt-before-dispatch",
                    "restart-preserved-run-identity",
                    "second-process-idempotent-replay",
                    "owned-state-safe-reset",
                )
                if clean_install is not None
                else ()
            ),
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
