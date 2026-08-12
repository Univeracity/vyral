#!/usr/bin/env python3
"""Run installed-wheel adversarial security and bounded-work qualification."""

from __future__ import annotations

import argparse
import asyncio
from contextlib import contextmanager
from hashlib import sha256
import json
import os
from pathlib import Path
import platform
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


MCP_PROTOCOL_VERSION = "2026-07-28"
MAX_RESPONSE_BYTES = 2 * 1024 * 1024


def _interpreter(environment: Path) -> Path:
    if sys.platform == "win32":
        return environment / "Scripts" / "python.exe"
    return environment / "bin" / "python"


def _install(wheel: Path, destination: Path) -> Path:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    subprocess.run(
        [
            str(python),
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            f"{wheel}[server]",
        ],
        check=True,
    )
    return python


def _unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _request(
    base_url: str,
    method: str,
    path: str,
    *,
    api_key: str | None = None,
    bearer: str | None = None,
    payload: Mapping[str, Any] | None = None,
    raw_body: bytes | None = None,
    headers: Mapping[str, str] | None = None,
    timeout: float = 10.0,
) -> tuple[int, Mapping[str, str], bytes]:
    body = (
        raw_body
        if raw_body is not None
        else (
            json.dumps(
                payload,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
            ).encode("utf-8")
            if payload is not None
            else None
        )
    )
    selected_headers = {
        "Accept": "application/json",
        **({"Content-Type": "application/json"} if body is not None else {}),
        **dict(headers or {}),
    }
    if api_key is not None:
        selected_headers["X-Vyral-Api-Key"] = api_key
    if bearer is not None:
        selected_headers["Authorization"] = f"Bearer {bearer}"
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
                    f"{method} {path} exceeded the response limit."
                )
            return response.status, dict(response.headers.items()), content
    except HTTPError as exc:
        content = exc.read(MAX_RESPONSE_BYTES + 1)
        return exc.code, dict(exc.headers.items()), content


def _expect(
    expected: int,
    base_url: str,
    method: str,
    path: str,
    **kwargs: Any,
) -> bytes:
    status, _, body = _request(
        base_url,
        method,
        path,
        **kwargs,
    )
    if status != expected:
        details = body.decode("utf-8", errors="replace")[-2000:]
        raise RuntimeError(
            f"{method} {path} returned HTTP {status}, expected "
            f"{expected}: {details}"
        )
    return body


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
                "The security-qualification host exited during startup.\n"
                + details
            )
        try:
            status, _, _ = _request(base_url, "GET", "/health", timeout=1)
            if status == 200:
                return
        except (URLError, TimeoutError):
            pass
        time.sleep(0.1)
    raise RuntimeError("Timed out waiting for the qualification host.")


@contextmanager
def _host(root: Path, api_key: str, log_path: Path) -> Iterator[str]:
    root.mkdir(parents=True, exist_ok=True)
    port = _unused_port()
    base_url = f"http://127.0.0.1:{port}"
    environment = os.environ.copy()
    environment["VYRAL_API_KEY"] = api_key
    with log_path.open("ab") as log:
        process = subprocess.Popen(
            [
                sys.executable,
                "-m",
                "vyral_runtime.host",
                "--root",
                str(root),
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


def _mcp_message(
    method: str,
    *,
    params: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    selected = dict(params or {})
    selected["_meta"] = {
        "io.modelcontextprotocol/protocolVersion": MCP_PROTOCOL_VERSION,
        "io.modelcontextprotocol/clientCapabilities": {},
        "io.modelcontextprotocol/clientInfo": {
            "name": "vyral-security-qualification",
            "version": "1.0.0",
        },
    }
    return {
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": selected,
    }


def _mcp_headers(
    method: str,
    *,
    name: str | None = None,
) -> dict[str, str]:
    output = {
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": MCP_PROTOCOL_VERSION,
        "MCP-Method": method,
    }
    if name is not None:
        output["MCP-Name"] = name
    return output


def _network_probes(root: Path, api_key: str) -> list[str]:
    checks: list[str] = []
    no_auth_environment = os.environ.copy()
    no_auth_environment.pop("VYRAL_API_KEY", None)
    remote = subprocess.run(
        [
            sys.executable,
            "-m",
            "vyral_runtime.host",
            "--root",
            str(root / "remote-bind"),
            "--host",
            "0.0.0.0",
        ],
        env=no_auth_environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if remote.returncode != 2:
        raise RuntimeError(
            "The CLI accepted a non-loopback bind without authentication."
        )
    wildcard_environment = no_auth_environment.copy()
    wildcard_environment["VYRAL_API_KEY"] = api_key
    wildcard = subprocess.run(
        [
            sys.executable,
            "-m",
            "vyral_runtime.host",
            "--root",
            str(root / "wildcard-bind"),
            "--host",
            "0.0.0.0",
        ],
        env=wildcard_environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if wildcard.returncode != 2:
        raise RuntimeError(
            "The CLI accepted a wildcard bind without an allowed host."
        )
    checks.extend(
        (
            "remote-bind-requires-authentication",
            "wildcard-bind-requires-host-allowlist",
        )
    )
    with _host(root, api_key, root / "security-host.log") as base_url:
        _expect(200, base_url, "GET", "/health")
        checks.append("anonymous-health-only")

        protected = "/collections"
        _expect(401, base_url, "GET", protected)
        _expect(
            401,
            base_url,
            "GET",
            protected,
            api_key="incorrect-key",
        )
        _expect(
            401,
            base_url,
            "GET",
            protected,
            bearer="incorrect-key",
        )
        _expect(
            401,
            base_url,
            "GET",
            protected,
            api_key=api_key,
            bearer="conflicting-key",
        )
        _expect(200, base_url, "GET", protected, api_key=api_key)
        _expect(200, base_url, "GET", protected, bearer=api_key)
        checks.extend(
            (
                "rest-missing-key-denied",
                "rest-wrong-key-denied",
                "rest-conflicting-credentials-denied",
                "rest-api-key-accepted",
                "rest-bearer-accepted",
            )
        )

        _expect(
            403,
            base_url,
            "GET",
            "/health",
            headers={"Host": "attacker.invalid"},
        )
        _expect(
            403,
            base_url,
            "GET",
            "/health",
            headers={"Origin": "https://attacker.invalid"},
        )
        _expect(
            414,
            base_url,
            "GET",
            "/collections?" + ("q" * 16_385),
            api_key=api_key,
        )
        checks.extend(
            (
                "rest-host-rebinding-denied",
                "rest-origin-denied",
                "rest-query-limit",
            )
        )

        list_message = _mcp_message("tools/list")
        list_headers = _mcp_headers("tools/list")
        _expect(
            401,
            base_url,
            "POST",
            "/mcp",
            payload=list_message,
            headers=list_headers,
        )
        tools_body = _expect(
            200,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=list_message,
            headers=list_headers,
        )
        tools = cast(
            Mapping[str, Any],
            json.loads(tools_body),
        ).get("result")
        if not isinstance(tools, Mapping):
            raise RuntimeError("MCP tools/list omitted its result.")
        serialized_tools = json.dumps(tools, sort_keys=True)
        for diagnostic in (
            "slow_compute",
            "failing_job",
            "confirm_delete",
        ):
            if diagnostic in serialized_tools:
                raise RuntimeError(
                    "MCP conformance diagnostics leaked into production "
                    f"catalog: {diagnostic}"
                )
        checks.extend(
            (
                "mcp-catalog-authentication",
                "mcp-diagnostics-disabled",
            )
        )

        discover = _mcp_message("server/discover")
        _expect(
            403,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=discover,
            headers={
                **_mcp_headers("server/discover"),
                "Host": "attacker.invalid",
            },
        )
        _expect(
            403,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=discover,
            headers={
                **_mcp_headers("server/discover"),
                "Origin": "https://attacker.invalid",
            },
        )
        _expect(
            400,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=discover,
            headers=_mcp_headers("tools/list"),
        )
        _expect(
            400,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            payload=discover,
            headers={
                **_mcp_headers("server/discover"),
                "MCP-Protocol-Version": "1900-01-01",
            },
        )
        _expect(
            413,
            base_url,
            "POST",
            "/mcp",
            api_key=api_key,
            raw_body=b"{" + (b"x" * 1_048_576) + b"}",
            headers=_mcp_headers("server/discover"),
        )
        _expect(405, base_url, "GET", "/mcp", api_key=api_key)
        checks.extend(
            (
                "mcp-host-rebinding-denied",
                "mcp-origin-denied",
                "mcp-routing-header-mismatch",
                "mcp-version-mismatch",
                "mcp-body-limit",
                "mcp-post-only",
            )
        )
    return checks


def _direct_probes(root: Path) -> list[str]:
    from vyral_runtime import (
        CanonicalArchiveRestoreRequest,
        CanonicalDocument,
        CanonicalIntegrityError,
        CanonicalMutation,
        CanonicalTransactionRequest,
        CollectionExportEnvelope,
        CollectionImportRequest,
        ExecutionRunRequest,
        FileObjectStore,
        InMemoryExecutionWorkerTransport,
        MAX_ARCHIVE_CHUNKS,
        MAX_COLLECTION_SNAPSHOT_RECORDS,
        ObjectWriteRequest,
        RecordValidationError,
        SQLiteCanonicalStore,
        SQLiteRecordStore,
        STORAGE_SCHEMA_COMPONENT,
        STORAGE_SCHEMA_VERSION,
        StorageSchemaError,
        VyralRuntime,
    )

    checks: list[str] = []
    object_store = FileObjectStore(root / "object-probes")
    try:
        object_store.put_object(
            ObjectWriteRequest(
                "security",
                "../escape",
                b"must-not-write",
            )
        )
    except ValueError:
        checks.append("object-path-traversal-denied")
    else:
        raise RuntimeError("Object storage accepted a traversal key.")
    finally:
        object_store.close()

    records = SQLiteRecordStore(root / "record-probes.sqlite")
    records.create_collection({"name": "source"})
    records.upsert_record(
        "source",
        {
            "id": "record-1",
            "partitionKey": "tenant-a",
            "type": "probe",
            "content": {"value": "trusted"},
        },
    )
    snapshot = records.export_collection("source")
    if snapshot is None:
        raise RuntimeError("Record snapshot probe did not export.")
    corrupted = snapshot.to_dict()
    corrupted["contentHash"] = "sha256:" + ("0" * 64)
    try:
        records.import_collection(
            "destination",
            CollectionImportRequest(
                snapshot=CollectionExportEnvelope.from_value(corrupted),
                allow_collection_rename=True,
            ),
        )
    except RecordValidationError:
        checks.append("record-import-hash-corruption-denied")
    else:
        raise RuntimeError("Record import accepted a corrupt content hash.")
    try:
        records.export_collection(
            "source",
            {"maxRecords": MAX_COLLECTION_SNAPSHOT_RECORDS + 1},
        )
    except RecordValidationError:
        checks.append("record-export-bound-enforced")
    else:
        raise RuntimeError("Record export accepted an unbounded request.")

    canonical_path = root / "canonical-probes.sqlite"
    canonical = SQLiteCanonicalStore(canonical_path)
    canonical.commit(
        CanonicalTransactionRequest(
            tenant_id="tenant-a",
            idempotency_key="security-archive",
            mutations=(
                CanonicalMutation(
                    document=CanonicalDocument(
                        tenant_id="tenant-a",
                        document_type="probe",
                        id="document-1",
                        schema_version="v1",
                        data={"value": "trusted"},
                    )
                ),
            ),
        )
    )
    archive = canonical.export_tenant_archive(
        "tenant-a",
        chunk_bytes=128,
    )
    first = archive.chunks[0]
    damaged = bytearray(first.content)
    damaged[0] ^= 1
    chunks = list(archive.chunks)
    chunks[0] = type(first)(
        index=first.index,
        content=bytes(damaged),
        length=first.length,
        content_hash=first.content_hash,
    )
    corrupt_archive = type(archive)(
        profile=archive.profile,
        tenant_id=archive.tenant_id,
        exported_at_utc=archive.exported_at_utc,
        snapshot_content_hash=archive.snapshot_content_hash,
        content_hash=archive.content_hash,
        chunks=tuple(chunks),
    )
    try:
        canonical.restore_tenant_archive(
            CanonicalArchiveRestoreRequest(
                archive=corrupt_archive,
                expected_content_hash=corrupt_archive.content_hash,
            )
        )
    except CanonicalIntegrityError:
        checks.append("canonical-archive-corruption-denied")
    else:
        raise RuntimeError("CanonicalStore accepted a corrupt archive.")
    reopened = SQLiteCanonicalStore(canonical_path)
    document = reopened.get_document(
        "tenant-a",
        "probe",
        "document-1",
    )
    if document is None or document.data != {"value": "trusted"}:
        raise RuntimeError(
            "Canonical archive rejection mutated trusted state."
        )
    checks.append("canonical-corruption-is-atomic")

    excessive_chunks = tuple(
        type(first)(
            index=index,
            content=first.content,
            length=first.length,
            content_hash=first.content_hash,
        )
        for index in range(MAX_ARCHIVE_CHUNKS + 1)
    )
    oversized_archive = type(archive)(
        profile=archive.profile,
        tenant_id=archive.tenant_id,
        exported_at_utc=archive.exported_at_utc,
        snapshot_content_hash=archive.snapshot_content_hash,
        content_hash=archive.content_hash,
        chunks=excessive_chunks,
    )
    try:
        canonical.restore_tenant_archive(
            CanonicalArchiveRestoreRequest(archive=oversized_archive)
        )
    except CanonicalIntegrityError:
        checks.append("canonical-archive-chunk-count-bound-enforced")
    else:
        raise RuntimeError(
            "CanonicalStore accepted an excessive archive chunk count."
        )

    oversized_payload = "x" * (1024 * 1024 + 1)
    try:
        ExecutionRunRequest(
            handler_id="security.probe",
            payload=oversized_payload,
        )
    except (TypeError, ValueError):
        checks.append("execution-payload-bound-enforced")
    else:
        raise RuntimeError("Execution accepted an oversized payload.")

    async def lease_probe() -> None:
        transport = InMemoryExecutionWorkerTransport(
            "security-worker",
            ("security.handler",),
            token_factory=lambda: "lease-secret-must-not-leak",
        )
        run = await transport.enqueue_run("security.handler")
        lease = await transport.lease_next(run.id)
        if lease is None:
            raise RuntimeError("Worker token probe did not obtain a lease.")
        if (
            "lease-secret-must-not-leak" in repr(lease)
            or "lease-secret-must-not-leak" in str(lease.safe_summary())
        ):
            raise RuntimeError("Worker lease secret leaked into diagnostics.")

    asyncio.run(lease_probe())
    checks.append("worker-lease-token-redacted")

    storage_root = root / "future-storage"
    with VyralRuntime(storage_root):
        pass
    database_path = storage_root / "vyral.sqlite"
    with sqlite3.connect(database_path) as connection:
        connection.execute(
            """
            UPDATE vyral_py_runtime_schema
            SET schema_version = ?
            WHERE component = ?
            """,
            (
                STORAGE_SCHEMA_VERSION + 1,
                STORAGE_SCHEMA_COMPONENT,
            ),
        )
    try:
        VyralRuntime(storage_root)
    except StorageSchemaError:
        checks.append("future-storage-schema-denied")
    else:
        raise RuntimeError("Runtime opened a newer storage schema.")
    return checks


def _artifact_hash(path: Path) -> str:
    digest = sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return "sha256:" + digest.hexdigest()


def _probe(wheel: Path, output: Path | None) -> int:
    from vyral_runtime import RUNTIME_VERSION

    with tempfile.TemporaryDirectory(
        prefix="vyral-python-security-probes-"
    ) as temporary:
        root = Path(temporary)
        api_key = token_hex(32)
        checks = _network_probes(root / "host", api_key)
        checks.extend(_direct_probes(root / "direct"))
    receipt = {
        "schemaVersion": "vyral.python-runtime-security.v1",
        "status": "passed",
        "runtime": {
            "version": RUNTIME_VERSION,
            "artifact": wheel.name,
            "sha256": _artifact_hash(wheel),
        },
        "environment": {
            "implementation": sys.implementation.name,
            "pythonVersion": platform.python_version(),
            "platform": platform.platform(),
            "sqliteVersion": sqlite3.sqlite_version,
        },
        "checks": checks,
        "checkCount": len(checks),
        "reviewBoundary": (
            "Executable adversarial qualification; independent human "
            "security review remains a promotion requirement."
        ),
    }
    encoded = json.dumps(receipt, indent=2, sort_keys=True) + "\n"
    if output is not None:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(encoded, encoding="utf-8")
    print(
        "python-runtime-security=ok "
        f"runtime={RUNTIME_VERSION} checks={len(checks)} "
        "auth=passed parsers=passed tokens=passed bounds=passed"
    )
    return 0


def main() -> int:
    if len(sys.argv) in {3, 4} and sys.argv[1] == "_probe":
        return _probe(
            Path(sys.argv[2]).resolve(),
            Path(sys.argv[3]).resolve() if len(sys.argv) == 4 else None,
        )

    parser = argparse.ArgumentParser()
    parser.add_argument(
        "runtime_wheel",
        type=Path,
        help="Candidate Python runtime wheel.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional JSON qualification receipt.",
    )
    arguments = parser.parse_args()
    wheel = arguments.runtime_wheel.resolve()
    if not wheel.is_file() or wheel.suffix != ".whl":
        parser.error(f"runtime wheel does not exist: {wheel}")
    with tempfile.TemporaryDirectory(
        prefix="vyral-python-security-environment-"
    ) as temporary:
        python = _install(wheel, Path(temporary) / "environment")
        command = [
            str(python),
            str(Path(__file__).resolve()),
            "_probe",
            str(wheel),
        ]
        if arguments.output is not None:
            command.append(str(arguments.output.resolve()))
        subprocess.run(command, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
