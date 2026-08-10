#!/usr/bin/env python3
"""Qualify the Python worker against a restarted Vyral host."""

from __future__ import annotations

import argparse
import asyncio
from contextlib import contextmanager
import json
import os
from pathlib import Path
from secrets import token_hex
import socket
import subprocess
import sys
import tempfile
import time
from typing import Any, Iterator, Mapping, Sequence, cast
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_SOURCE = ROOT / "runtimes/python/src"
RUNTIME_ROOT = ROOT / "runtimes/python"
sys.path.insert(0, str(RUNTIME_ROOT))
sys.path.insert(0, str(RUNTIME_SOURCE))

from examples.external_worker import (  # noqa: E402
    HANDLER_ID,
    PLUGIN_ID,
    create_plugin,
)
from vyral_runtime import (  # noqa: E402
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    ExecutionRunResult,
    HttpExecutionWorkerTransport,
    StaticExecutionWorkerTokenSource,
)


def _json_request(
    base_url: str,
    method: str,
    path: str,
    payload: Mapping[str, Any] | None = None,
    *,
    timeout: float = 10.0,
    api_key: str | None = None,
) -> tuple[int, Any]:
    body = (
        json.dumps(
            payload,
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8")
        if payload is not None
        else None
    )
    headers = (
        {
            "Accept": "application/json",
            "Content-Type": "application/json",
        }
        if body is not None
        else {"Accept": "application/json"}
    )
    if api_key is not None:
        headers["X-Vyral-Api-Key"] = api_key
    request = Request(
        base_url + path,
        data=body,
        method=method,
        headers=headers,
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            raw = response.read(2 * 1024 * 1024 + 1)
            if len(raw) > 2 * 1024 * 1024:
                raise RuntimeError("Vyral integration response was too large.")
            value = json.loads(raw) if raw else None
            return response.status, value
    except HTTPError as exc:
        raise RuntimeError(
            f"{method} {path} returned HTTP {exc.code}."
        ) from None


def _object(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise RuntimeError(f"{name} did not return a JSON object.")
    return cast(dict[str, Any], value)


def _unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _wait_for_server(
    process: subprocess.Popen[bytes],
    base_url: str,
    log_path: Path,
    timeout: float = 30.0,
) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            details = log_path.read_text(
                encoding="utf-8", errors="replace"
            )[-8000:]
            raise RuntimeError(
                "Vyral server exited during startup.\n" + details
            )
        try:
            status, _ = _json_request(
                base_url, "GET", "/health", timeout=1.0
            )
            if status == 200:
                return
        except (RuntimeError, URLError, TimeoutError):
            pass
        time.sleep(0.1)
    raise RuntimeError("Timed out waiting for the Vyral server.")


def _stop_server(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


@contextmanager
def _server(
    server_dll: Path,
    base_url: str,
    database_path: Path,
    objects_path: Path,
    log_path: Path,
    api_key: str | None,
) -> Iterator[None]:
    environment = os.environ.copy()
    environment.update(
        {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DatabasePath": str(database_path),
            "ObjectsPath": str(objects_path),
            "ExecutionRuntime__ExternalHandlers__0__HandlerId": HANDLER_ID,
            "ExecutionRuntime__ExternalHandlers__0__PluginId": PLUGIN_ID,
            "ExecutionRuntime__ExternalHandlers__0__DisplayName": (
                "Python approval integration"
            ),
            "Logging__LogLevel__Default": "Warning",
            "Logging__LogLevel__Microsoft.AspNetCore": "Warning",
        }
    )
    if api_key is not None:
        environment["VYRAL_API_KEY"] = api_key
    with log_path.open("ab") as log:
        process = subprocess.Popen(
            [
                "dotnet",
                str(server_dll),
                "--urls",
                base_url,
            ],
            cwd=ROOT,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        try:
            _wait_for_server(process, base_url, log_path)
            yield
        finally:
            _stop_server(process)


@contextmanager
def _python_server(
    python_executable: Path,
    base_url: str,
    runtime_root: Path,
    log_path: Path,
    api_key: str | None,
) -> Iterator[None]:
    environment = os.environ.copy()
    existing_python_path = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = os.pathsep.join(
        value
        for value in (
            str(ROOT),
            existing_python_path,
        )
        if value
    )
    environment["VYRAL_RUNTIME_ROOT"] = str(runtime_root)
    if api_key is not None:
        environment["VYRAL_API_KEY"] = api_key
    host, port_text = base_url.removeprefix("http://").rsplit(":", 1)
    with log_path.open("ab") as log:
        process = subprocess.Popen(
            [
                str(python_executable),
                "-m",
                "uvicorn",
                (
                    "runtimes.python.examples.external_worker_host:"
                    "create_application"
                ),
                "--factory",
                "--host",
                host,
                "--port",
                port_text,
                "--log-level",
                "warning",
            ],
            cwd=ROOT,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        try:
            _wait_for_server(process, base_url, log_path)
            yield
        finally:
            _stop_server(process)


async def _qualify(
    base_url: str, restart: Any, api_key: str | None
) -> None:
    plugin = create_plugin()
    async with HttpExecutionWorkerTransport(
        base_url,
        "python-live-worker",
        (HANDLER_ID,),
        token_source=(
            StaticExecutionWorkerTokenSource(api_key)
            if api_key is not None
            else None
        ),
        allow_insecure_http=True,
    ) as transport:
        worker = ExecutionPluginWorker(
            transport,
            (plugin,),
            ExecutionPluginWorkerOptions(
                heartbeat_interval_seconds=None,
            ),
        )
        status, accepted_value = await asyncio.to_thread(
            _json_request,
            base_url,
            "POST",
            "/execution/runs",
            {
                "handlerId": HANDLER_ID,
                "pluginId": PLUGIN_ID,
                "payload": {"value": 42},
                "idempotencyKey": "python-live-wait",
            },
            api_key=api_key,
        )
        if status != 202:
            raise RuntimeError("Vyral did not accept the Python worker run.")
        accepted = _object(accepted_value, "start run")
        run_id = str(accepted["id"])

        waiting = await worker.run_once(run_id)
        if waiting is None or waiting.status != "waiting":
            raise RuntimeError("Python handler did not suspend durably.")

        await asyncio.to_thread(restart)

        _, restored_value = await asyncio.to_thread(
            _json_request,
            base_url,
            "GET",
            f"/execution/runs/{run_id}",
            api_key=api_key,
        )
        restored = _object(restored_value, "restored run")
        if restored.get("status") != "waiting":
            raise RuntimeError("Waiting run did not survive server restart.")

        await asyncio.to_thread(
            _json_request,
            base_url,
            "POST",
            f"/execution/runs/{run_id}/events",
            {
                "name": "approval",
                "payload": {"approved": True},
            },
            api_key=api_key,
        )
        completed = await worker.run_once(run_id)
        if (
            completed is None
            or completed.status != "succeeded"
            or completed.result != {"approved": True, "value": 42}
        ):
            raise RuntimeError(
                "Python handler did not resume unchanged after restart."
            )

        _, checkpoint_value = await asyncio.to_thread(
            _json_request,
            base_url,
            "GET",
            f"/execution/runs/{run_id}/checkpoints/before-approval",
            api_key=api_key,
        )
        checkpoint = _object(checkpoint_value, "checkpoint")
        if checkpoint.get("content") != {"ready": True}:
            raise RuntimeError("Python checkpoint did not survive restart.")

        _, artifacts_value = await asyncio.to_thread(
            _json_request,
            base_url,
            "GET",
            f"/execution/runs/{run_id}/artifacts",
            api_key=api_key,
        )
        if not isinstance(artifacts_value, list) or [
            item.get("name")
            for item in artifacts_value
            if isinstance(item, dict)
        ] != ["approval-summary"]:
            raise RuntimeError("Python artifact projection did not match.")

        _, replay_value = await asyncio.to_thread(
            _json_request,
            base_url,
            "POST",
            "/execution/runs",
            {
                "handlerId": HANDLER_ID,
                "pluginId": PLUGIN_ID,
                "payload": {"value": "replay"},
                "idempotencyKey": "python-live-completion-replay",
            },
            api_key=api_key,
        )
        replay_run = _object(replay_value, "replay run")
        replay_lease = await transport.lease_next(str(replay_run["id"]))
        if replay_lease is None:
            raise RuntimeError("Replay-safety run could not be leased.")
        result = ExecutionRunResult.succeeded_result(
            {"completionReplay": True}
        )
        first = await transport.complete(replay_lease, result)
        second = await transport.complete(replay_lease, result)
        if (
            first.id != second.id
            or second.status != "succeeded"
            or second.result != {"completionReplay": True}
        ):
            raise RuntimeError("External-worker completion was not replay-safe.")


def _build_server() -> Path:
    subprocess.run(
        [
            "dotnet",
            "build",
            "src/Vyral.Server/Vyral.Server.csproj",
            "--no-restore",
        ],
        cwd=ROOT,
        check=True,
    )
    candidate = (
        ROOT
        / "src/Vyral.Server/bin/Debug/net10.0/Vyral.Server.dll"
    )
    if not candidate.is_file():
        raise RuntimeError("The Vyral server build did not produce its DLL.")
    return candidate


def parse_arguments(values: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--server-dll",
        type=Path,
        help="Use an already-built Vyral.Server.dll.",
    )
    parser.add_argument(
        "--authenticated",
        action="store_true",
        help="Qualify API-key and bearer-token authentication too.",
    )
    parser.add_argument(
        "--server-kind",
        choices=("dotnet", "python"),
        default="dotnet",
        help="Host implementation to qualify (default: dotnet).",
    )
    parser.add_argument(
        "--python-executable",
        type=Path,
        default=Path(sys.executable),
        help=(
            "Python executable containing vyral-runtime[server] when "
            "--server-kind=python."
        ),
    )
    return parser.parse_args(values)


def main(values: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(values)
    server_dll: Path | None = None
    if arguments.server_kind == "dotnet":
        server_dll = (
            arguments.server_dll.resolve()
            if arguments.server_dll is not None
            else _build_server()
        )
        if not server_dll.is_file():
            raise SystemExit(
                f"Vyral server DLL is unavailable: {server_dll}"
            )
    elif arguments.server_dll is not None:
        raise SystemExit(
            "--server-dll applies only to --server-kind=dotnet."
        )
    python_executable = arguments.python_executable.expanduser().absolute()
    if (
        arguments.server_kind == "python"
        and not python_executable.is_file()
    ):
        raise SystemExit(
            f"Python executable is unavailable: {python_executable}"
        )

    with tempfile.TemporaryDirectory(
        prefix="vyral-python-worker-live-"
    ) as temporary:
        root = Path(temporary)
        port = _unused_port()
        base_url = f"http://127.0.0.1:{port}"
        database_path = root / "vyral.sqlite"
        objects_path = root / "objects"
        runtime_root = root / "python-runtime"
        log_path = root / "server.log"
        active: list[Any] = []
        api_key = token_hex(32) if arguments.authenticated else None

        def start() -> Any:
            manager = (
                _server(
                    cast(Path, server_dll),
                    base_url,
                    database_path,
                    objects_path,
                    log_path,
                    api_key,
                )
                if arguments.server_kind == "dotnet"
                else _python_server(
                    python_executable,
                    base_url,
                    runtime_root,
                    log_path,
                    api_key,
                )
            )
            manager.__enter__()
            active.append(manager)
            return manager

        def stop() -> None:
            if active:
                active.pop().__exit__(None, None, None)

        def restart() -> None:
            stop()
            start()

        start()
        try:
            asyncio.run(_qualify(base_url, restart, api_key))
        finally:
            stop()

    print(
        "python-external-worker-integration=ok "
        f"server={arguments.server_kind}-local "
        f"authenticated={str(arguments.authenticated).lower()} "
        "restart=passed completion-replay=passed"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
