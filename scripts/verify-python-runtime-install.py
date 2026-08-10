#!/usr/bin/env python3
"""Verify clean artifacts and the measured local developer path."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
from time import perf_counter
from typing import Any, Mapping
import venv


FIRST_CITATION_BUDGET_MS = 5 * 60 * 1_000

SMOKE = r"""
from vyral_runtime import (
    VyralRuntime,
    run_bundled_canonical_scenario,
    run_bundled_external_worker_scenario,
    run_bundled_goldens,
    run_bundled_native_execution_scenario,
    run_bundled_record_store_scenario,
)

runtime = VyralRuntime()
readiness = runtime.readiness()
assert readiness.status == "ok"
assert readiness.full_local_ready is False
assert readiness.contract is not None
assert readiness.contract.operation_count == 129
assert readiness.contract.rest_operation_count == 133
assert readiness.contract.schema_count == 263
assert len(run_bundled_goldens()) == 12
assert len(run_bundled_record_store_scenario()) == 17
assert len(run_bundled_external_worker_scenario()) == 3
assert len(run_bundled_canonical_scenario()) == 6
assert len(run_bundled_native_execution_scenario()) == 7
assert all(profile.available for profile in readiness.profiles)
"""


class InstallVerificationError(RuntimeError):
    """Raised when an installed artifact cannot complete the golden path."""


def _interpreter(environment: Path) -> Path:
    if sys.platform == "win32":
        return environment / "Scripts" / "python.exe"
    return environment / "bin" / "python"


def _command(
    *arguments: str,
    capture_output: bool = False,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        arguments,
        check=True,
        capture_output=capture_output,
        text=True,
    )


def _json_command(*arguments: str) -> Mapping[str, Any]:
    completed = _command(*arguments, capture_output=True)
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise InstallVerificationError(
            f"Command did not return JSON: {' '.join(arguments)}"
        ) from error
    if not isinstance(value, Mapping):
        raise InstallVerificationError("Command JSON must be an object.")
    return value


def _object(
    value: object,
    label: str,
) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise InstallVerificationError(f"{label} must be an object.")
    return value


def _number(value: object, label: str) -> float:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise InstallVerificationError(f"{label} must be numeric.")
    selected = float(value)
    if selected < 0:
        raise InstallVerificationError(f"{label} must not be negative.")
    return selected


def _quickstart_smoke(
    python: Path,
    state_root: Path,
) -> dict[str, object]:
    started_at = perf_counter()
    first = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "quickstart",
        "--root",
        str(state_root),
        "--json",
    )
    first_command_ms = round((perf_counter() - started_at) * 1_000, 3)
    retrieval = _object(first.get("retrieval"), "retrieval")
    execution = _object(first.get("execution"), "execution")
    timings = _object(first.get("timings"), "timings")
    citations = retrieval.get("citations")
    if not isinstance(citations, list) or not citations:
        raise InstallVerificationError(
            "The clean local quickstart returned no citations."
        )
    first_citation_ms = _number(
        timings.get("firstCitationMs"),
        "timings.firstCitationMs",
    )
    durable_receipt_ms = _number(
        timings.get("durableReceiptMs"),
        "timings.durableReceiptMs",
    )
    completed_ms = _number(
        timings.get("completedMs"),
        "timings.completedMs",
    )
    if first_citation_ms > FIRST_CITATION_BUDGET_MS:
        raise InstallVerificationError(
            "The clean local quickstart exceeded the five-minute "
            f"first-citation budget: {first_citation_ms:.3f} ms."
        )
    if first_command_ms > FIRST_CITATION_BUDGET_MS:
        raise InstallVerificationError(
            "The clean local quickstart command exceeded five minutes: "
            f"{first_command_ms:.3f} ms."
        )
    if not (
        first_citation_ms <= durable_receipt_ms <= completed_ms
    ):
        raise InstallVerificationError(
            "Quickstart timing milestones are not monotonic."
        )
    if (
        execution.get("admittedStatus") != "queued"
        or execution.get("persistedStatusAfterReopen") != "queued"
        or execution.get("completedStatus") != "succeeded"
        or execution.get("dispatchedRuns") != 1
    ):
        raise InstallVerificationError(
            "The clean quickstart did not prove receipt-before-dispatch "
            "and restart completion."
        )
    run_id = execution.get("runId")
    if not isinstance(run_id, str) or not run_id:
        raise InstallVerificationError(
            "The clean quickstart did not return a durable run ID."
        )

    replay_started_at = perf_counter()
    replay = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "quickstart",
        "--root",
        str(state_root),
        "--json",
    )
    replay_command_ms = round(
        (perf_counter() - replay_started_at) * 1_000,
        3,
    )
    replay_retrieval = _object(replay.get("retrieval"), "replay.retrieval")
    replay_execution = _object(replay.get("execution"), "replay.execution")
    if (
        replay_execution.get("runId") != run_id
        or replay_execution.get("admissionReplayed") is not True
        or replay_execution.get("dispatchedRuns") != 0
        or replay_retrieval.get("createdChunks") != 0
        or replay_retrieval.get("reusedChunks") != 3
    ):
        raise InstallVerificationError(
            "The second clean-process quickstart did not replay the same "
            "run and persisted corpus."
        )

    inspection = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "inspect",
        "--root",
        str(state_root),
        "--json",
    )
    providers = _object(inspection.get("providers"), "inspection.providers")
    embeddings = _object(
        providers.get("embeddings"),
        "inspection.providers.embeddings",
    )
    if (
        inspection.get("topology") != "local-single-node"
        or embeddings.get("semanticQuality") != "lexical"
        or embeddings.get("requiresNetwork") is not False
    ):
        raise InstallVerificationError(
            "Installed-runtime inspection did not disclose the local topology "
            "and model-free embedding limitation."
        )

    removed = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "quickstart",
        "--root",
        str(state_root),
        "--reset",
        "--json",
    )
    if not isinstance(removed.get("removedRootPath"), str) or state_root.exists():
        raise InstallVerificationError(
            "The installed quickstart did not safely remove its owned state."
        )

    return {
        "status": "passed",
        "citationCount": len(citations),
        "firstCommandMs": first_command_ms,
        "firstCitationMs": first_citation_ms,
        "durableReceiptMs": durable_receipt_ms,
        "completedMs": completed_ms,
        "replayCommandMs": replay_command_ms,
        "receiptBeforeDispatch": True,
        "restartPreservedRunIdentity": True,
        "secondProcessReplayed": True,
        "safeReset": True,
    }


def _install_and_smoke(
    artifact: Path,
    destination: Path,
    *,
    artifact_kind: str,
) -> dict[str, object]:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    install_started_at = perf_counter()
    _command(
        str(python),
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        "--no-deps",
        str(artifact),
    )
    install_ms = round((perf_counter() - install_started_at) * 1_000, 3)
    quickstart = _quickstart_smoke(python, destination / "quickstart-state")
    _command(str(python), "-c", SMOKE)
    return {
        "artifactKind": artifact_kind,
        "artifactName": artifact.name,
        "installMs": install_ms,
        "quickstart": quickstart,
    }


def _server_smoke(wheel: Path, destination: Path) -> None:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    _command(
        str(python),
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        f"{wheel}[server]",
    )
    _command(
        str(python),
        "-m",
        "vyral_runtime.host",
        "--help",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "artifact_directory",
        type=Path,
        help="Directory containing exactly one runtime wheel and sdist.",
    )
    parser.add_argument(
        "--server",
        action="store_true",
        help="Also install the server extra and exercise the CLI.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional path for a machine-readable clean-install receipt.",
    )
    arguments = parser.parse_args()
    root = arguments.artifact_directory.resolve()
    wheels = sorted(root.glob("vyral_runtime-*.whl"))
    sdists = sorted(root.glob("vyral_runtime-*.tar.gz"))
    if len(wheels) != 1 or len(sdists) != 1:
        parser.error(
            "artifact directory must contain exactly one runtime wheel and sdist"
        )

    with tempfile.TemporaryDirectory(
        prefix="vyral-runtime-install-"
    ) as temporary:
        destination = Path(temporary)
        artifact_results = [
            _install_and_smoke(
                wheels[0],
                destination / "wheel",
                artifact_kind="wheel",
            ),
            _install_and_smoke(
                sdists[0],
                destination / "sdist",
                artifact_kind="sdist",
            ),
        ]
        if arguments.server:
            _server_smoke(wheels[0], destination / "server")

    receipt: dict[str, object] = {
        "schemaVersion": "vyral.python-runtime-clean-install.v1",
        "status": "passed",
        "generatedAtUtc": datetime.now(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z"),
        "environment": {
            "implementation": sys.implementation.name,
            "pythonVersion": platform.python_version(),
            "system": platform.system(),
            "machine": platform.machine(),
        },
        "firstCitationBudgetMs": FIRST_CITATION_BUDGET_MS,
        "serverExtraVerified": arguments.server,
        "artifacts": artifact_results,
    }
    if arguments.output is not None:
        output = arguments.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(receipt, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )

    print(
        "python-runtime-clean-install=ok "
        f"python={sys.version_info.major}.{sys.version_info.minor} "
        f"platform={sys.platform} artifacts=2 "
        f"first-citation-budget-ms={FIRST_CITATION_BUDGET_MS}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
