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
    run_bundled_projection_generation_scenario,
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
assert len(run_bundled_goldens()) == 13
assert len(run_bundled_projection_generation_scenario()) == 15
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


def _starter_run_id(output: str, label: str) -> str:
    for line in output.splitlines():
        if not line.startswith("Accepted receipt: "):
            continue
        for field in line.split():
            if field.startswith("run=") and len(field) > 4:
                return field.removeprefix("run=")
    raise InstallVerificationError(
        f"The generated starter {label} returned no durable run ID."
    )


def _starter_smoke(
    python: Path,
    project_root: Path,
) -> dict[str, object]:
    target = project_root / "vyral_app.py"
    create_started_at = perf_counter()
    created = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "init",
        "--path",
        str(target),
        "--json",
    )
    create_command_ms = round(
        (perf_counter() - create_started_at) * 1_000,
        3,
    )
    created_path = created.get("createdPath")
    state_root_path = created.get("stateRootPath")
    if (
        not isinstance(created_path, str)
        or Path(created_path).resolve() != target.resolve()
        or not isinstance(state_root_path, str)
    ):
        raise InstallVerificationError(
            "The installed runtime generated an invalid starter receipt."
        )
    if "@vyral(" not in target.read_text(encoding="utf-8"):
        raise InstallVerificationError(
            "The installed runtime starter does not use the @vyral surface."
        )

    first_started_at = perf_counter()
    first = _command(
        str(python),
        "-m",
        "vyral_runtime",
        "run",
        str(target),
        capture_output=True,
    )
    first_run_ms = round((perf_counter() - first_started_at) * 1_000, 3)
    first_run_id = _starter_run_id(first.stdout, "first run")
    if not all(
        marker in first.stdout
        for marker in (
            "status=queued replayed=false",
            "Closed the first runtime instance before dispatch.",
            "Recovered: ",
            "status=succeeded dispatched=1",
            'result={"message": "Hello, Vyral!"}',
        )
    ):
        raise InstallVerificationError(
            "The generated starter did not prove first-run admission, "
            "restart, and completion."
        )

    replay_started_at = perf_counter()
    replay = _command(
        str(python),
        "-m",
        "vyral_runtime",
        "run",
        str(target),
        capture_output=True,
    )
    replay_run_ms = round((perf_counter() - replay_started_at) * 1_000, 3)
    replay_run_id = _starter_run_id(replay.stdout, "replay")
    if (
        replay_run_id != first_run_id
        or "status=succeeded replayed=true" not in replay.stdout
        or "status=succeeded dispatched=0" not in replay.stdout
    ):
        raise InstallVerificationError(
            "The generated starter did not idempotently replay its durable run."
        )

    source = target.read_text(encoding="utf-8")
    versioned_source = source.replace(
        "RUN_VERSION = 1",
        "RUN_VERSION = 2",
        1,
    ).replace(
        'payload={"name": "Vyral",',
        'payload={"name": "Vyral 2",',
        1,
    )
    if versioned_source == source:
        raise InstallVerificationError(
            "The generated starter has no editable RUN_VERSION contract."
        )
    with target.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(versioned_source)
    versioned_started_at = perf_counter()
    versioned = _command(
        str(python),
        "-m",
        "vyral_runtime",
        "run",
        str(target),
        capture_output=True,
    )
    versioned_run_ms = round(
        (perf_counter() - versioned_started_at) * 1_000,
        3,
    )
    versioned_run_id = _starter_run_id(versioned.stdout, "versioned run")
    if (
        versioned_run_id == first_run_id
        or "status=queued replayed=false" not in versioned.stdout
        or "status=succeeded dispatched=1" not in versioned.stdout
        or 'result={"message": "Hello, Vyral 2!"}' not in versioned.stdout
    ):
        raise InstallVerificationError(
            "Incrementing the generated starter RUN_VERSION did not admit "
            "new work."
        )

    state_root = Path(state_root_path).resolve()
    inspection = _json_command(
        str(python),
        "-m",
        "vyral_runtime",
        "inspect",
        "--root",
        str(state_root),
        "--json",
    )
    if inspection.get("topology") != "local-single-node":
        raise InstallVerificationError(
            "The generated starter did not leave inspectable local state."
        )
    return {
        "status": "passed",
        "createdPath": str(target.resolve()),
        "stateRootPath": str(state_root),
        "createCommandMs": create_command_ms,
        "firstRunMs": first_run_ms,
        "replayRunMs": replay_run_ms,
        "versionedRunMs": versioned_run_ms,
        "receiptBeforeDispatch": True,
        "restartPreservedRunIdentity": True,
        "secondProcessReplayed": True,
        "versionedNewRun": True,
        "inspectableState": True,
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
    starter = _starter_smoke(python, destination / "starter-project")
    quickstart = _quickstart_smoke(python, destination / "quickstart-state")
    quickstart_first_command_ms = _number(
        quickstart.get("firstCommandMs"),
        "quickstart.firstCommandMs",
    )
    starter_create_ms = _number(
        starter.get("createCommandMs"),
        "starter.createCommandMs",
    )
    starter_first_run_ms = _number(
        starter.get("firstRunMs"),
        "starter.firstRunMs",
    )
    install_to_quickstart_ms = round(
        install_ms + quickstart_first_command_ms,
        3,
    )
    install_to_editable_result_ms = round(
        install_ms + starter_create_ms + starter_first_run_ms,
        3,
    )
    if max(
        install_to_quickstart_ms,
        install_to_editable_result_ms,
    ) > FIRST_CITATION_BUDGET_MS:
        raise InstallVerificationError(
            "The installed artifact exceeded the five-minute clean-install "
            "to useful-result budget."
        )
    _command(str(python), "-c", SMOKE)
    return {
        "artifactKind": artifact_kind,
        "artifactName": artifact.name,
        "installMs": install_ms,
        "installToQuickstartCompleteMs": install_to_quickstart_ms,
        "installToEditableResultMs": install_to_editable_result_ms,
        "quickstart": quickstart,
        "starter": starter,
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
    wheels = sorted(root.glob("vyral-[0-9]*.whl"))
    sdists = sorted(root.glob("vyral-[0-9]*.tar.gz"))
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
        "schemaVersion": "vyral.python-runtime-clean-install.v2",
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
        "firstUseBudgetMs": FIRST_CITATION_BUDGET_MS,
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
