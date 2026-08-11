#!/usr/bin/env python3
"""Prove the useful source-checkout path without installing Vyral."""

from __future__ import annotations

import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tempfile
from time import perf_counter
from typing import Any, Mapping


ROOT = Path(__file__).resolve().parents[1]
LAUNCHER = ROOT / "scripts" / "vyral"
FIRST_USE_BUDGET_MS = 5 * 60 * 1_000


class SourceQuickstartError(RuntimeError):
    """Raised when a clean source-checkout experience is not useful."""


def _run(
    working_directory: Path,
    *arguments: str,
) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment["PYTHONNOUSERSITE"] = "1"
    return subprocess.run(
        [sys.executable, "-I", str(LAUNCHER), *arguments],
        cwd=working_directory,
        env=environment,
        check=True,
        capture_output=True,
        text=True,
        timeout=300,
    )


def _json(
    working_directory: Path,
    *arguments: str,
) -> Mapping[str, Any]:
    completed = _run(working_directory, *arguments)
    try:
        payload = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise SourceQuickstartError(
            f"Source command did not return JSON: {' '.join(arguments)}"
        ) from error
    if not isinstance(payload, Mapping):
        raise SourceQuickstartError("Source command JSON must be an object.")
    return payload


def _mapping(value: object, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise SourceQuickstartError(f"{label} must be an object.")
    return value


def _number(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise SourceQuickstartError(f"{label} must be numeric.")
    selected = float(value)
    if selected < 0:
        raise SourceQuickstartError(f"{label} must not be negative.")
    return selected


def _run_id(output: str) -> str:
    match = re.search(r"Accepted receipt: run=([^ ]+)", output)
    if match is None:
        raise SourceQuickstartError(
            "The editable starter returned no durable run identity."
        )
    return match.group(1)


def main() -> int:
    started = perf_counter()
    with tempfile.TemporaryDirectory(
        prefix="vyral-source-first-use-"
    ) as temporary:
        working_directory = Path(temporary)
        state_root = working_directory / "quickstart-state"

        first_started = perf_counter()
        first = _json(
            working_directory,
            "quickstart",
            "--root",
            str(state_root),
            "--json",
        )
        first_command_ms = round(
            (perf_counter() - first_started) * 1_000,
            3,
        )
        retrieval = _mapping(first.get("retrieval"), "retrieval")
        embedding = _mapping(first.get("embedding"), "embedding")
        execution = _mapping(first.get("execution"), "execution")
        timings = _mapping(first.get("timings"), "timings")
        citations = retrieval.get("citations")
        if (
            first.get("topology") != "local-single-node"
            or retrieval.get("mode") != "lexical"
            or not isinstance(citations, list)
            or not citations
            or embedding.get("used") is not False
            or embedding.get("requiresNetwork") is not False
            or execution.get("admittedStatus") != "queued"
            or execution.get("persistedStatusAfterReopen") != "queued"
            or execution.get("completedStatus") != "succeeded"
            or execution.get("dispatchedRuns") != 1
        ):
            raise SourceQuickstartError(
                "The source quickstart did not prove cited zero-embedding "
                "retrieval and durable restart completion."
            )
        first_citation_ms = _number(
            timings.get("firstCitationMs"),
            "timings.firstCitationMs",
        )
        if max(first_command_ms, first_citation_ms) > FIRST_USE_BUDGET_MS:
            raise SourceQuickstartError(
                "The source quickstart exceeded the five-minute first-use "
                "budget."
            )

        replay = _json(
            working_directory,
            "quickstart",
            "--root",
            str(state_root),
            "--json",
        )
        replay_retrieval = _mapping(
            replay.get("retrieval"), "replay.retrieval"
        )
        replay_execution = _mapping(
            replay.get("execution"), "replay.execution"
        )
        if (
            replay_execution.get("runId") != execution.get("runId")
            or replay_execution.get("admissionReplayed") is not True
            or replay_execution.get("dispatchedRuns") != 0
            or replay_retrieval.get("createdChunks") != 0
            or replay_retrieval.get("reusedChunks") != 3
        ):
            raise SourceQuickstartError(
                "The second isolated source command did not replay the "
                "persisted corpus and run."
            )

        inspection = _json(
            working_directory,
            "inspect",
            "--root",
            str(state_root),
            "--json",
        )
        providers = _mapping(
            inspection.get("providers"), "inspection.providers"
        )
        if (
            inspection.get("topology") != "local-single-node"
            or _mapping(providers.get("records"), "records").get("healthy")
            is not True
            or _mapping(
                providers.get("execution"), "execution"
            ).get("healthy")
            is not True
        ):
            raise SourceQuickstartError(
                "The source quickstart did not leave inspectable healthy state."
            )

        reset = _json(
            working_directory,
            "quickstart",
            "--root",
            str(state_root),
            "--reset",
            "--json",
        )
        if state_root.exists() or not isinstance(
            reset.get("removedRootPath"), str
        ):
            raise SourceQuickstartError(
                "The source quickstart did not safely reset its owned state."
            )

        app_path = working_directory / "vyral_app.py"
        created = _json(
            working_directory,
            "init",
            "--path",
            str(app_path),
            "--json",
        )
        run_arguments = created.get("runArguments")
        if (
            created.get("createdPath") != str(app_path.resolve())
            or not isinstance(run_arguments, list)
            or len(run_arguments) != 3
            or run_arguments[1:] != ["run", str(app_path.resolve())]
        ):
            raise SourceQuickstartError(
                "Source init did not return one truthful launcher command."
            )

        starter_first = _run(
            working_directory,
            "run",
            str(app_path),
        )
        starter_replay = _run(
            working_directory,
            "run",
            str(app_path),
        )
        first_run_id = _run_id(starter_first.stdout)
        replay_run_id = _run_id(starter_replay.stdout)
        if (
            first_run_id != replay_run_id
            or "status=queued replayed=false" not in starter_first.stdout
            or "status=succeeded dispatched=1" not in starter_first.stdout
            or "status=succeeded replayed=true" not in starter_replay.stdout
            or "status=succeeded dispatched=0" not in starter_replay.stdout
        ):
            raise SourceQuickstartError(
                "The source-generated application did not complete and replay "
                "through the advertised launcher."
            )

        elapsed_ms = round((perf_counter() - started) * 1_000, 3)
        if elapsed_ms > FIRST_USE_BUDGET_MS:
            raise SourceQuickstartError(
                "The complete source-checkout path exceeded five minutes."
            )

    print(
        json.dumps(
            {
                "schemaVersion": "vyral.source-first-use.v1",
                "status": "passed",
                "platform": platform.system().lower(),
                "pythonVersion": platform.python_version(),
                "isolatedInterpreter": True,
                "packageInstallationRequired": False,
                "networkRequired": False,
                "firstCommandMs": first_command_ms,
                "firstCitationMs": first_citation_ms,
                "citationCount": len(citations),
                "durableRestartCompleted": True,
                "secondProcessReplayed": True,
                "generatedApplicationCompleted": True,
                "generatedApplicationReplayed": True,
                "safeReset": True,
                "completePathMs": elapsed_ms,
                "budgetMs": FIRST_USE_BUDGET_MS,
            },
            indent=2,
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
