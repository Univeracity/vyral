#!/usr/bin/env python3
"""Aggregate and verify the complete Python runtime platform receipt matrix."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path
import re
from typing import Any, Mapping


EXPECTED_MATRIX = {
    (system, python)
    for system in ("Linux", "Darwin", "Windows")
    for python in ("3.10", "3.11", "3.12")
}
REQUIRED_GATES = {
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
    "clean-wheel-local-quickstart",
    "clean-sdist-local-quickstart",
    "first-citation-under-five-minutes",
    "receipt-before-dispatch",
    "restart-preserved-run-identity",
    "second-process-idempotent-replay",
    "owned-state-safe-reset",
    "generated-starter",
    "generated-starter-first-run",
    "generated-starter-idempotent-replay",
    "generated-starter-versioned-new-run",
    "install-to-use-under-five-minutes",
}


class MatrixError(ValueError):
    pass


def _object(value: object, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise MatrixError(f"{label} must be an object.")
    return value


def _text(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise MatrixError(f"{label} must be a non-empty string.")
    return value.strip()


def _digest(path: Path) -> str:
    return "sha256:" + sha256(path.read_bytes()).hexdigest()


def _python_line(version: str) -> str:
    match = re.fullmatch(r"(3\.(?:10|11|12))(?:\.\d+.*)?", version)
    if match is None:
        raise MatrixError(
            f"Unsupported Python matrix version {version!r}."
        )
    return match.group(1)


def verify(receipt_paths: list[Path]) -> dict[str, Any]:
    if len(receipt_paths) != len(EXPECTED_MATRIX):
        raise MatrixError(
            "Python runtime promotion requires exactly nine platform "
            f"receipts; found {len(receipt_paths)}."
        )
    cells: dict[tuple[str, str], dict[str, Any]] = {}
    source_commit: str | None = None
    runtime_identity: tuple[str, str, str] | None = None
    for path in sorted(receipt_paths):
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise MatrixError(
                f"Platform receipt {path} is not valid UTF-8 JSON."
            ) from exc
        root = _object(document, str(path))
        if root.get("schemaVersion") != "vyral.python-runtime-platform.v1":
            raise MatrixError(f"Platform receipt {path} has an unknown schema.")
        if root.get("status") != "passed" or root.get("scope") != "portable-matrix":
            raise MatrixError(
                f"Platform receipt {path} is not a passing portable-matrix receipt."
            )
        source = _object(root.get("source"), f"{path}: source")
        commit = _text(source.get("commit"), f"{path}: source.commit")
        if source.get("dirty") is not False:
            raise MatrixError(f"Platform receipt {path} was produced from a dirty tree.")
        if source_commit is None:
            source_commit = commit
        elif source_commit != commit:
            raise MatrixError("Platform receipts do not share one source commit.")

        environment = _object(
            root.get("environment"), f"{path}: environment"
        )
        system = _text(environment.get("system"), f"{path}: system")
        python = _python_line(
            _text(
                environment.get("pythonVersion"),
                f"{path}: pythonVersion",
            )
        )
        cell = (system, python)
        if cell not in EXPECTED_MATRIX:
            raise MatrixError(f"Unexpected platform matrix cell {cell!r}.")
        if cell in cells:
            raise MatrixError(f"Duplicate platform matrix cell {cell!r}.")

        runtime = _object(root.get("runtime"), f"{path}: runtime")
        identity = (
            _text(runtime.get("version"), f"{path}: runtime.version"),
            _text(
                runtime.get("contractVersion"),
                f"{path}: runtime.contractVersion",
            ),
            _text(
                runtime.get("fixtureVersion"),
                f"{path}: runtime.fixtureVersion",
            ),
        )
        if runtime_identity is None:
            runtime_identity = identity
        elif runtime_identity != identity:
            raise MatrixError(
                "Platform receipts disagree on runtime, contract, or fixture version."
            )
        profiles = runtime.get("profiles")
        if not isinstance(profiles, list) or not profiles:
            raise MatrixError(f"Platform receipt {path} has no profile evidence.")
        if any(
            not isinstance(profile, Mapping)
            or profile.get("available") is not True
            for profile in profiles
        ):
            raise MatrixError(
                f"Platform receipt {path} contains an unavailable profile."
            )
        gates = root.get("gates")
        if not isinstance(gates, list) or not REQUIRED_GATES.issubset(
            {str(gate) for gate in gates}
        ):
            raise MatrixError(
                f"Platform receipt {path} does not prove every required gate."
            )
        local_experience = _object(
            root.get("localExperience"),
            f"{path}: localExperience",
        )
        if (
            local_experience.get("schemaVersion")
            != "vyral.python-runtime-clean-install.v2"
            or local_experience.get("status") != "passed"
            or local_experience.get("serverExtraVerified") is not True
        ):
            raise MatrixError(
                f"Platform receipt {path} has no passing clean-install evidence."
            )
        first_citation_budget = local_experience.get(
            "firstCitationBudgetMs"
        )
        if (
            not isinstance(first_citation_budget, (int, float))
            or isinstance(first_citation_budget, bool)
            or float(first_citation_budget) <= 0
        ):
            raise MatrixError(
                f"Platform receipt {path} has no first-citation budget."
            )
        first_use_budget = local_experience.get("firstUseBudgetMs")
        if (
            not isinstance(first_use_budget, (int, float))
            or isinstance(first_use_budget, bool)
            or float(first_use_budget) != float(first_citation_budget)
        ):
            raise MatrixError(
                f"Platform receipt {path} has no consistent first-use budget."
            )
        experience_artifacts = local_experience.get("artifacts")
        if (
            not isinstance(experience_artifacts, list)
            or len(experience_artifacts) != 2
        ):
            raise MatrixError(
                f"Platform receipt {path} must prove wheel and sdist quickstarts."
            )
        artifact_kinds: set[str] = set()
        first_citation_values: list[float] = []
        first_command_values: list[float] = []
        install_to_quickstart_values: list[float] = []
        install_to_editable_values: list[float] = []
        starter_first_run_values: list[float] = []
        for artifact_value in experience_artifacts:
            artifact = _object(
                artifact_value,
                f"{path}: localExperience artifact",
            )
            kind = _text(
                artifact.get("artifactKind"),
                f"{path}: localExperience artifactKind",
            )
            artifact_kinds.add(kind)
            first_citation = artifact.get("firstCitationMs")
            first_command = artifact.get("firstCommandMs")
            install_to_quickstart = artifact.get(
                "installToQuickstartCompleteMs"
            )
            install_to_editable = artifact.get(
                "installToEditableResultMs"
            )
            if (
                not isinstance(first_citation, (int, float))
                or isinstance(first_citation, bool)
                or not isinstance(first_command, (int, float))
                or isinstance(first_command, bool)
                or not isinstance(install_to_quickstart, (int, float))
                or isinstance(install_to_quickstart, bool)
                or not isinstance(install_to_editable, (int, float))
                or isinstance(install_to_editable, bool)
                or min(
                    float(first_citation),
                    float(first_command),
                    float(install_to_quickstart),
                    float(install_to_editable),
                ) < 0
                or float(first_citation) > float(first_citation_budget)
                or float(first_command) > float(first_citation_budget)
                or float(install_to_quickstart) > float(first_use_budget)
                or float(install_to_editable) > float(first_use_budget)
            ):
                raise MatrixError(
                    f"Platform receipt {path} exceeded its local time budget."
                )
            first_citation_values.append(float(first_citation))
            first_command_values.append(float(first_command))
            install_to_quickstart_values.append(
                float(install_to_quickstart)
            )
            install_to_editable_values.append(float(install_to_editable))
            starter = _object(
                artifact.get("starter"),
                f"{path}: localExperience starter",
            )
            starter_timings = {
                field: starter.get(field)
                for field in (
                    "createCommandMs",
                    "firstRunMs",
                    "replayRunMs",
                    "versionedRunMs",
                )
            }
            if (
                starter.get("status") != "passed"
                or any(
                    starter.get(field) is not True
                    for field in (
                        "receiptBeforeDispatch",
                        "restartPreservedRunIdentity",
                        "secondProcessReplayed",
                        "versionedNewRun",
                        "inspectableState",
                    )
                )
                or any(
                    not isinstance(metric, (int, float))
                    or isinstance(metric, bool)
                    or float(metric) < 0
                    for metric in starter_timings.values()
                )
            ):
                raise MatrixError(
                    f"Platform receipt {path} has no passing editable starter."
                )
            starter_first_run = starter_timings["firstRunMs"]
            assert isinstance(starter_first_run, (int, float))
            starter_first_run_values.append(float(starter_first_run))
        if artifact_kinds != {"wheel", "sdist"}:
            raise MatrixError(
                f"Platform receipt {path} has incomplete artifact kinds."
            )
        cells[cell] = {
            "system": system,
            "python": python,
            "receipt": path.as_posix(),
            "sha256": _digest(path),
            "platform": environment.get("platform"),
            "sqliteVersion": environment.get("sqliteVersion"),
            "fts5Available": environment.get("fts5Available"),
            "localExperience": {
                "firstCitationBudgetMs": first_citation_budget,
                "firstUseBudgetMs": first_use_budget,
                "maxFirstCitationMs": max(first_citation_values),
                "maxFirstCommandMs": max(first_command_values),
                "maxInstallToQuickstartCompleteMs": max(
                    install_to_quickstart_values
                ),
                "maxInstallToEditableResultMs": max(
                    install_to_editable_values
                ),
                "maxStarterFirstRunMs": max(starter_first_run_values),
            },
        }

    missing = sorted(EXPECTED_MATRIX - set(cells))
    if missing:
        raise MatrixError(f"Platform matrix is missing cells: {missing!r}.")
    assert source_commit is not None
    assert runtime_identity is not None
    return {
        "schemaVersion": "vyral.python-runtime-platform-matrix.v1",
        "status": "passed",
        "generatedAtUtc": datetime.now(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z"),
        "sourceCommit": source_commit,
        "runtimeVersion": runtime_identity[0],
        "contractVersion": runtime_identity[1],
        "fixtureVersion": runtime_identity[2],
        "cellCount": len(cells),
        "cells": [cells[cell] for cell in sorted(cells)],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "receipt_root",
        type=Path,
        help="Directory containing the downloaded platform.json receipts.",
    )
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    root = arguments.receipt_root.resolve()
    receipts = sorted(root.rglob("platform.json"))
    try:
        result = verify(receipts)
    except MatrixError as exc:
        parser.error(str(exc))
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(result, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        "python-runtime-platform-matrix=ok "
        f"cells={result['cellCount']} commit={result['sourceCommit']} "
        f"output={output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
