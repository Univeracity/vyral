#!/usr/bin/env python3
"""Rehearse an installed Python-runtime schema upgrade and host restart."""

from __future__ import annotations

import argparse
import asyncio
from contextlib import contextmanager
from hashlib import sha256
import json
import os
from pathlib import Path
from secrets import token_hex
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
from typing import Any, Iterator, Mapping, cast
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import venv


MAX_RESPONSE_BYTES = 2 * 1024 * 1024
MCP_PROTOCOL_VERSION = "2026-07-28"


def _interpreter(environment: Path) -> Path:
    if sys.platform == "win32":
        return environment / "Scripts" / "python.exe"
    return environment / "bin" / "python"


def _run(*arguments: str) -> None:
    subprocess.run(arguments, check=True)


def _install(
    artifact: Path,
    destination: Path,
    *,
    server: bool,
) -> Path:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    requirement = f"{artifact}[server]" if server else str(artifact)
    _run(
        str(python),
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        requirement,
    )
    return python


def _seed_baseline(root: Path, state_path: Path) -> int:
    from vyral_runtime import (  # type: ignore[import-not-found]
        CanonicalDocument,
        CanonicalMutation,
        CanonicalTransactionRequest,
        ObjectWriteRequest,
        RUNTIME_VERSION,
        TraceRecord,
        VyralRuntime,
    )

    trace_id = "upgrade-trace"
    with VyralRuntime(root) as runtime:
        runtime.records.create_collection({"name": "upgrade-items"})
        runtime.records.upsert_record(
            "upgrade-items",
            {
                "id": "before-upgrade",
                "partitionKey": "tenant-a",
                "type": "upgrade-probe",
                "content": {"value": "baseline"},
            },
        )
        runtime.canonical.commit(
            CanonicalTransactionRequest(
                tenant_id="tenant-a",
                idempotency_key="upgrade-canonical",
                mutations=(
                    CanonicalMutation(
                        document=CanonicalDocument(
                            tenant_id="tenant-a",
                            document_type="upgrade-probe",
                            id="canonical-before-upgrade",
                            schema_version="v1",
                            data={"value": "baseline"},
                        )
                    ),
                ),
            )
        )
        runtime.objects.put_object(
            ObjectWriteRequest(
                "upgrade-objects",
                "before-upgrade.txt",
                b"baseline-object",
                content_type="text/plain",
                metadata={"phase": "baseline"},
            )
        )
        runtime.traces.write_trace(
            TraceRecord(
                id=trace_id,
                operation="python-runtime-upgrade",
                adapter="baseline",
                request={"phase": "baseline"},
                result_summary={"status": "seeded"},
                duration_ms=1.0,
            )
        )
        run = asyncio.run(
            runtime.execution.start_run(
                {
                    "handlerId": "upgrade.missing-handler.v1",
                    "payload": {"phase": "baseline"},
                    "idempotencyKey": "upgrade-execution",
                }
            )
        )
        if run.status != "rejected":
            raise RuntimeError(
                "The baseline execution probe did not reach durable rejection."
            )
        readiness = runtime.readiness()
        if readiness.status != "ok":
            raise RuntimeError("The baseline runtime was not ready after seeding.")

    state_path.write_text(
        json.dumps(
            {
                "baselineVersion": RUNTIME_VERSION,
                "runId": run.id,
                "traceId": trace_id,
            },
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return 0


def _unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _request(
    base_url: str,
    method: str,
    path: str,
    *,
    api_key: str | None,
    payload: Mapping[str, Any] | None = None,
    headers: Mapping[str, str] | None = None,
    timeout: float = 10.0,
) -> tuple[int, Mapping[str, str], bytes]:
    body = (
        json.dumps(
            payload,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        if payload is not None
        else None
    )
    selected_headers = {
        "Accept": "application/json",
        **({"Content-Type": "application/json"} if body is not None else {}),
        **dict(headers or {}),
    }
    if api_key is not None:
        selected_headers["X-Vyral-Api-Key"] = api_key
    request = Request(
        base_url + path,
        data=body,
        method=method,
        headers=selected_headers,
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            content = response.read(MAX_RESPONSE_BYTES + 1)
            if len(content) > MAX_RESPONSE_BYTES:
                raise RuntimeError(
                    f"{method} {path} exceeded the qualification response limit."
                )
            return response.status, dict(response.headers.items()), content
    except HTTPError as exc:
        content = exc.read(MAX_RESPONSE_BYTES + 1)
        return exc.code, dict(exc.headers.items()), content


def _json(
    base_url: str,
    method: str,
    path: str,
    *,
    api_key: str | None,
    payload: Mapping[str, Any] | None = None,
    headers: Mapping[str, str] | None = None,
    expected_status: int = 200,
) -> Any:
    status, _, content = _request(
        base_url,
        method,
        path,
        api_key=api_key,
        payload=payload,
        headers=headers,
    )
    if status != expected_status:
        details = content.decode("utf-8", errors="replace")[-2000:]
        raise RuntimeError(
            f"{method} {path} returned HTTP {status}, expected "
            f"{expected_status}: {details}"
        )
    return json.loads(content) if content else None


def _object(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise RuntimeError(f"{name} did not return a JSON object.")
    return cast(dict[str, Any], value)


def _wait_for_server(
    process: subprocess.Popen[bytes],
    base_url: str,
    log_path: Path,
) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            details = log_path.read_text(
                encoding="utf-8",
                errors="replace",
            )[-8000:]
            raise RuntimeError(
                "The candidate host exited during startup.\n" + details
            )
        try:
            status, _, _ = _request(
                base_url,
                "GET",
                "/health",
                api_key=None,
                timeout=1.0,
            )
            if status == 200:
                return
        except (URLError, TimeoutError):
            pass
        time.sleep(0.1)
    raise RuntimeError("Timed out waiting for the candidate host.")


@contextmanager
def _host(
    python: Path,
    runtime_root: Path,
    api_key: str,
    log_path: Path,
) -> Iterator[str]:
    port = _unused_port()
    base_url = f"http://127.0.0.1:{port}"
    environment = os.environ.copy()
    environment["VYRAL_API_KEY"] = api_key
    with log_path.open("ab") as log:
        process = subprocess.Popen(
            [
                str(python),
                "-m",
                "vyral_runtime.host",
                "--root",
                str(runtime_root),
                "--host",
                "127.0.0.1",
                "--port",
                str(port),
                "--log-level",
                "warning",
            ],
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        try:
            _wait_for_server(process, base_url, log_path)
            yield base_url
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


def _schema_details(readiness: Mapping[str, Any]) -> dict[str, Any]:
    checks = readiness.get("checks")
    if not isinstance(checks, list):
        raise RuntimeError("Readiness did not contain checks.")
    for value in checks:
        if isinstance(value, dict) and value.get("id") == "local.storage-schema":
            if value.get("status") not in {"passed", "ok"}:
                raise RuntimeError(
                    "The storage-schema readiness check failed: "
                    + json.dumps(value, sort_keys=True)
                )
            return _object(value.get("details"), "storage schema details")
    raise RuntimeError("Readiness omitted the storage-schema check.")


def _verify_persisted_state(
    base_url: str,
    api_key: str,
    state: Mapping[str, Any],
) -> None:
    record = _object(
        _json(
            base_url,
            "GET",
            "/collections/upgrade-items/records/tenant-a/before-upgrade",
            api_key=api_key,
        ),
        "baseline record",
    )
    if record.get("content") != {"value": "baseline"}:
        raise RuntimeError("The baseline record changed during upgrade.")

    canonical = _object(
        _json(
            base_url,
            "GET",
            (
                "/canonical/tenants/tenant-a/documents/"
                "upgrade-probe/canonical-before-upgrade"
            ),
            api_key=api_key,
        ),
        "canonical document",
    )
    if canonical.get("data") != {"value": "baseline"}:
        raise RuntimeError("The canonical document changed during upgrade.")

    status, _, content = _request(
        base_url,
        "GET",
        "/objects/upgrade-objects/before-upgrade.txt",
        api_key=api_key,
    )
    if status != 200 or content != b"baseline-object":
        raise RuntimeError("The baseline object changed during upgrade.")

    trace = _object(
        _json(
            base_url,
            "GET",
            f"/traces/{state['traceId']}",
            api_key=api_key,
        ),
        "trace",
    )
    if trace.get("operation") != "python-runtime-upgrade":
        raise RuntimeError("The baseline trace changed during upgrade.")

    run = _object(
        _json(
            base_url,
            "GET",
            f"/execution/runs/{state['runId']}",
            api_key=api_key,
        ),
        "execution run",
    )
    if run.get("status") != "rejected":
        raise RuntimeError("The baseline execution run changed during upgrade.")


def _verify_mcp(base_url: str, api_key: str) -> None:
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "server/discover",
        "params": {
            "_meta": {
                "io.modelcontextprotocol/protocolVersion": (
                    MCP_PROTOCOL_VERSION
                ),
                "io.modelcontextprotocol/clientCapabilities": {},
                "io.modelcontextprotocol/clientInfo": {
                    "name": "vyral-upgrade-qualification",
                    "version": "1.0.0",
                },
            }
        },
    }
    response = _object(
        _json(
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=payload,
            headers={
                "Accept": "application/json, text/event-stream",
                "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
                "MCP-Method": "server/discover",
            },
        ),
        "MCP discovery",
    )
    result = _object(response.get("result"), "MCP discovery result")
    if result.get("supportedVersions") != [MCP_PROTOCOL_VERSION]:
        raise RuntimeError("MCP discovery did not survive the host upgrade.")


def _artifact_hash(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return "sha256:" + digest.hexdigest()


def _candidate_environment(python: Path) -> dict[str, Any]:
    program = (
        "import json,platform,sqlite3,sys;"
        "from vyral_runtime import RUNTIME_VERSION;"
        "print(json.dumps({"
        "'runtimeVersion':RUNTIME_VERSION,"
        "'pythonVersion':platform.python_version(),"
        "'platform':platform.platform(),"
        "'sqliteVersion':sqlite3.sqlite_version,"
        "'implementation':sys.implementation.name"
        "},sort_keys=True))"
    )
    result = subprocess.run(
        [str(python), "-c", program],
        check=True,
        capture_output=True,
        text=True,
    )
    return cast(dict[str, Any], json.loads(result.stdout))


def _qualify(
    baseline_wheel: Path,
    candidate_wheel: Path,
    output: Path | None,
) -> None:
    with tempfile.TemporaryDirectory(
        prefix="vyral-python-upgrade-"
    ) as temporary:
        work = Path(temporary)
        runtime_root = work / "runtime"
        state_path = work / "baseline-state.json"
        baseline_python = _install(
            baseline_wheel,
            work / "baseline-environment",
            server=False,
        )
        _run(
            str(baseline_python),
            str(Path(__file__).resolve()),
            "_seed",
            str(runtime_root),
            str(state_path),
        )
        state = _object(
            json.loads(state_path.read_text(encoding="utf-8")),
            "baseline state",
        )
        candidate_python = _install(
            candidate_wheel,
            work / "candidate-environment",
            server=True,
        )
        environment = _candidate_environment(candidate_python)
        baseline_version = str(state["baselineVersion"])
        candidate_version = str(environment["runtimeVersion"])
        if not baseline_version.startswith("0.1."):
            raise RuntimeError(
                f"Baseline {baseline_version!r} is not on the 0.1.x line."
            )
        if not candidate_version.startswith("0.1."):
            raise RuntimeError(
                f"Candidate {candidate_version!r} is not on the 0.1.x line."
            )
        if baseline_version == candidate_version:
            raise RuntimeError(
                "Baseline and candidate runtime versions must differ."
            )

        api_key = token_hex(32)
        log_path = work / "candidate-host.log"
        with _host(
            candidate_python,
            runtime_root,
            api_key,
            log_path,
        ) as base_url:
            unauthorized, _, _ = _request(
                base_url,
                "GET",
                (
                    "/collections/upgrade-items/records/"
                    "tenant-a/before-upgrade"
                ),
                api_key=None,
            )
            if unauthorized != 401:
                raise RuntimeError(
                    "The upgraded host did not preserve authentication."
                )
            readiness = _object(
                _json(
                    base_url,
                    "GET",
                    "/readiness",
                    api_key=api_key,
                ),
                "first readiness",
            )
            first_schema = _schema_details(readiness)
            if (
                first_schema.get("fromVersion") != 0
                or first_schema.get("toVersion") != 1
                or first_schema.get("appliedVersions") != [1]
                or first_schema.get("upgraded") is not True
            ):
                raise RuntimeError(
                    "The first candidate start did not apply schema 0 -> 1."
                )
            _verify_persisted_state(base_url, api_key, state)
            _verify_mcp(base_url, api_key)
            created = _object(
                _json(
                    base_url,
                    "POST",
                    "/collections/upgrade-items/records",
                    api_key=api_key,
                    payload={
                        "id": "after-upgrade",
                        "partitionKey": "tenant-a",
                        "type": "upgrade-probe",
                        "content": {"value": "candidate"},
                    },
                    expected_status=201,
                ),
                "post-upgrade record",
            )
            if created.get("id") != "after-upgrade":
                raise RuntimeError("The upgraded host could not write data.")

        with _host(
            candidate_python,
            runtime_root,
            api_key,
            log_path,
        ) as base_url:
            readiness = _object(
                _json(
                    base_url,
                    "GET",
                    "/readiness",
                    api_key=api_key,
                ),
                "restart readiness",
            )
            second_schema = _schema_details(readiness)
            if (
                second_schema.get("fromVersion") != 1
                or second_schema.get("toVersion") != 1
                or second_schema.get("appliedVersions") != []
                or second_schema.get("upgraded") is not False
            ):
                raise RuntimeError(
                    "The restarted candidate did not report a no-op schema "
                    "decision."
                )
            _verify_persisted_state(base_url, api_key, state)
            _verify_mcp(base_url, api_key)
            after = _object(
                _json(
                    base_url,
                    "GET",
                    (
                        "/collections/upgrade-items/records/"
                        "tenant-a/after-upgrade"
                    ),
                    api_key=api_key,
                ),
                "post-upgrade record after restart",
            )
            if after.get("content") != {"value": "candidate"}:
                raise RuntimeError(
                    "The post-upgrade write did not survive restart."
                )

        with sqlite3.connect(runtime_root / "vyral.sqlite") as connection:
            integrity = str(connection.execute("PRAGMA integrity_check").fetchone()[0])
            foreign_key_violations = int(
                connection.execute(
                    "SELECT COUNT(*) FROM pragma_foreign_key_check"
                ).fetchone()[0]
            )
        if integrity.lower() != "ok" or foreign_key_violations:
            raise RuntimeError(
                "The upgraded database failed final SQLite integrity checks."
            )

        receipt = {
            "schemaVersion": "vyral.python-runtime-upgrade.v1",
            "status": "passed",
            "baseline": {
                "version": baseline_version,
                "artifact": baseline_wheel.name,
                "sha256": _artifact_hash(baseline_wheel),
            },
            "candidate": {
                "version": candidate_version,
                "artifact": candidate_wheel.name,
                "sha256": _artifact_hash(candidate_wheel),
            },
            "environment": environment,
            "storage": {
                "firstStart": first_schema,
                "secondStart": second_schema,
                "integrityCheck": integrity,
                "foreignKeyViolationCount": foreign_key_violations,
            },
            "checks": [
                "baseline-seed",
                "forward-schema-migration",
                "authentication-after-upgrade",
                "records-after-upgrade",
                "canonical-after-upgrade",
                "objects-after-upgrade",
                "traces-after-upgrade",
                "execution-after-upgrade",
                "stateless-mcp-after-upgrade",
                "post-upgrade-write",
                "combined-host-restart",
                "sqlite-integrity",
            ],
        }
        encoded = json.dumps(receipt, indent=2, sort_keys=True) + "\n"
        if output is not None:
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(encoded, encoding="utf-8")
        print(
            "python-runtime-upgrade=ok "
            f"baseline={baseline_version} candidate={candidate_version} "
            "schema=0->1 restart=passed auth=passed mcp=passed"
        )


def main() -> int:
    if len(sys.argv) == 4 and sys.argv[1] == "_seed":
        return _seed_baseline(Path(sys.argv[2]), Path(sys.argv[3]))

    parser = argparse.ArgumentParser()
    parser.add_argument(
        "baseline_wheel",
        type=Path,
        help="Previously qualified 0.1.x runtime wheel.",
    )
    parser.add_argument(
        "candidate_wheel",
        type=Path,
        help="Candidate 0.1.x runtime wheel.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional JSON qualification receipt.",
    )
    arguments = parser.parse_args()
    baseline = arguments.baseline_wheel.resolve()
    candidate = arguments.candidate_wheel.resolve()
    for label, path in (
        ("baseline", baseline),
        ("candidate", candidate),
    ):
        if not path.is_file() or path.suffix != ".whl":
            parser.error(f"{label} wheel does not exist: {path}")
    if baseline == candidate:
        parser.error("baseline and candidate wheels must be different")
    _qualify(
        baseline,
        candidate,
        arguments.output.resolve() if arguments.output else None,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
