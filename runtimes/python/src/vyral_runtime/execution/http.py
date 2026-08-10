from __future__ import annotations

import asyncio
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
import json
from typing import Any, Mapping, Protocol
from urllib.error import HTTPError
from urllib.parse import urljoin, urlsplit
from urllib.request import (
    HTTPRedirectHandler,
    Request,
    build_opener,
)

from ..async_runtime import RuntimeExecutor
from ..contracts import JSONValue
from ..local.models import JSONObject
from .models import (
    ExecutionArtifact,
    ExecutionArtifactWrite,
    ExecutionCheckpoint,
    ExecutionCheckpointWrite,
    ExecutionRun,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionWorkerLease,
    ExecutionWorkerWaitRequest,
    ExecutionWorkerWaitResponse,
)


MAX_WORKER_RESPONSE_BYTES = 2 * 1024 * 1024


class ExecutionWorkerTokenSource(Protocol):
    async def get_token(self) -> str: ...


@dataclass(frozen=True)
class StaticExecutionWorkerTokenSource:
    token: str = field(repr=False)

    def __post_init__(self) -> None:
        if not self.token.strip():
            raise ValueError("Execution worker token is required.")

    async def get_token(self) -> str:
        return self.token


class DelegateExecutionWorkerTokenSource:
    def __init__(
        self,
        get_token: Callable[[], Any],
    ) -> None:
        if not callable(get_token):
            raise TypeError("Execution worker token source must be callable.")
        self._get_token = get_token

    async def get_token(self) -> str:
        import inspect

        token = self._get_token()
        if inspect.isawaitable(token):
            token = await token
        if not isinstance(token, str) or not token.strip():
            raise ValueError(
                "Execution worker token source returned an empty token."
            )
        return token


@dataclass(frozen=True)
class ExecutionWorkerTelemetry:
    operation: str
    path: str
    run_id: str | None
    status_code: int | None
    error: str | None


class ExecutionWorkerHttpError(RuntimeError):
    def __init__(self, operation: str, path: str, status_code: int) -> None:
        super().__init__(
            f"Vyral {operation} {path} returned HTTP {status_code}."
        )
        self.operation = operation
        self.path = path
        self.status_code = status_code


class ExecutionWorkerTransportError(RuntimeError):
    """Redacted worker transport failure.

    The originating exception is intentionally not interpolated into the
    message because HTTP libraries can include URLs, headers, or response
    content in their diagnostics.
    """

    def __init__(self, operation: str, path: str) -> None:
        super().__init__(f"Vyral {operation} {path} transport failed.")
        self.operation = operation
        self.path = path


class _NoRedirects(HTTPRedirectHandler):
    def redirect_request(
        self,
        req: Any,
        fp: Any,
        code: int,
        msg: str,
        headers: Any,
        newurl: str,
    ) -> None:
        return None


class HttpExecutionWorkerTransport:
    """Dependency-free, token-safe transport for the portable worker API."""

    def __init__(
        self,
        base_url: str,
        worker_id: str,
        handler_ids: Iterable[str],
        *,
        token_source: ExecutionWorkerTokenSource | None = None,
        observe: Callable[[ExecutionWorkerTelemetry], None] | None = None,
        timeout_seconds: float = 30.0,
        max_response_bytes: int = MAX_WORKER_RESPONSE_BYTES,
        allow_insecure_http: bool = False,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        parsed = urlsplit(base_url)
        if (
            parsed.scheme not in {"http", "https"}
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.query
            or parsed.fragment
        ):
            raise ValueError(
                "Vyral server base URL must be an absolute HTTP(S) URL "
                "without credentials, query, or fragment."
            )
        if parsed.scheme == "http" and not allow_insecure_http:
            raise ValueError(
                "Plain HTTP worker transport requires allow_insecure_http=True."
            )
        selected_worker = worker_id.strip()
        if not selected_worker:
            raise ValueError("Vyral worker id is required.")
        selected_handlers = tuple(
            sorted(
                {
                    handler.strip()
                    for handler in handler_ids
                    if handler.strip()
                }
            )
        )
        if not selected_handlers:
            raise ValueError(
                "At least one Vyral worker handler id is required."
            )
        if timeout_seconds <= 0:
            raise ValueError("Worker HTTP timeout must be positive.")
        if max_response_bytes <= 0:
            raise ValueError("Worker max response bytes must be positive.")
        self.base_url = base_url.rstrip("/") + "/"
        self.worker_id = selected_worker
        self.handler_ids = selected_handlers
        self.token_source = token_source
        self.observe = observe
        self.timeout_seconds = timeout_seconds
        self.max_response_bytes = max_response_bytes
        self.executor = executor or RuntimeExecutor(
            max_workers=4,
            max_pending=32,
            thread_name_prefix="vyral-worker-http",
        )
        self._owns_executor = executor is None
        self._closed = False

    async def lease_next(
        self,
        run_id: str | None = None,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease | None:
        selected_run = run_id.strip() if run_id and run_id.strip() else None
        response = await self._post(
            "lease",
            "/execution/workers/leases",
            selected_run,
            {
                "workerId": self.worker_id,
                "handlerIds": list(self.handler_ids),
                "runId": selected_run,
                "ttlSeconds": ttl_seconds,
            },
            allow_no_content=True,
        )
        return (
            ExecutionWorkerLease.from_value(response)
            if response is not None
            else None
        )

    async def heartbeat(
        self,
        lease: ExecutionWorkerLease,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease:
        response = await self._required(
            "heartbeat",
            "/execution/workers/leases/heartbeat",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "ttlSeconds": ttl_seconds,
            },
        )
        return ExecutionWorkerLease.from_value(response)

    async def checkpoint(
        self,
        lease: ExecutionWorkerLease,
        checkpoint: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint:
        response = await self._required(
            "checkpoint",
            "/execution/workers/leases/checkpoints",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "checkpoint": checkpoint.to_dict(),
            },
        )
        return ExecutionCheckpoint.from_value(response)

    async def get_checkpoint(
        self,
        lease: ExecutionWorkerLease,
        key: str,
    ) -> ExecutionCheckpoint | None:
        response = await self._post(
            "get-checkpoint",
            "/execution/workers/leases/checkpoints/read",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "key": key,
            },
            allow_not_found=True,
        )
        return (
            ExecutionCheckpoint.from_value(response)
            if response is not None
            else None
        )

    async def report(
        self,
        lease: ExecutionWorkerLease,
        update: ExecutionRunUpdate,
    ) -> ExecutionRun:
        response = await self._required(
            "report",
            "/execution/workers/leases/reports",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "update": update.to_dict(),
            },
        )
        return ExecutionRun.from_value(response)

    async def record_event(
        self,
        lease: ExecutionWorkerLease,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None:
        await self._post(
            "record-event",
            "/execution/workers/leases/events",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "type": event_type,
                "message": message,
                "severity": severity,
                "details": details,
            },
            allow_no_content=True,
        )

    async def put_artifact(
        self,
        lease: ExecutionWorkerLease,
        artifact: ExecutionArtifactWrite,
    ) -> ExecutionArtifact:
        response = await self._required(
            "put-artifact",
            "/execution/workers/leases/artifacts",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "artifact": artifact.to_dict(),
            },
        )
        return ExecutionArtifact.from_value(response)

    async def wait(
        self,
        lease: ExecutionWorkerLease,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWorkerWaitResponse:
        response = await self._required(
            "wait",
            "/execution/workers/leases/wait",
            lease.run.id,
            {
                **self._lease_auth(lease),
                **request.to_dict(),
            },
        )
        return ExecutionWorkerWaitResponse.from_value(response)

    async def complete(
        self,
        lease: ExecutionWorkerLease,
        result: ExecutionRunResult,
    ) -> ExecutionRun:
        response = await self._required(
            "complete",
            "/execution/workers/leases/complete",
            lease.run.id,
            {
                **self._lease_auth(lease),
                "result": result.to_dict(),
            },
        )
        return ExecutionRun.from_value(response)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        if self._owns_executor:
            self.executor.close()

    def __enter__(self) -> HttpExecutionWorkerTransport:
        return self

    def __exit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()

    async def __aenter__(self) -> HttpExecutionWorkerTransport:
        return self

    async def __aexit__(
        self,
        exc_type: object,
        exc: object,
        traceback: object,
    ) -> None:
        self.close()

    def _lease_auth(self, lease: ExecutionWorkerLease) -> JSONObject:
        return {
            "leaseKey": lease.lease_key,
            "leaseToken": lease.lease_token,
            "workerId": lease.worker_id or self.worker_id,
        }

    async def _required(
        self,
        operation: str,
        path: str,
        run_id: str | None,
        payload: JSONObject,
    ) -> Mapping[str, Any]:
        response = await self._post(
            operation,
            path,
            run_id,
            payload,
        )
        if response is None:
            raise ExecutionWorkerTransportError(operation, path)
        return response

    async def _post(
        self,
        operation: str,
        path: str,
        run_id: str | None,
        payload: JSONObject,
        *,
        allow_no_content: bool = False,
        allow_not_found: bool = False,
    ) -> Mapping[str, Any] | None:
        if self._closed:
            raise RuntimeError("Execution worker transport is closed.")
        try:
            token: str | None = None
            if self.token_source is not None:
                selected = (await self.token_source.get_token()).strip()
                if not selected:
                    raise ValueError("empty worker token")
                token = selected
            status, raw = await self.executor.run(
                lambda: self._send(
                    path,
                    payload,
                    token,
                    allow_no_content=allow_no_content,
                    allow_not_found=allow_not_found,
                )
            )
        except ExecutionWorkerHttpError as exc:
            self._notify(
                operation,
                path,
                run_id,
                exc.status_code,
                "http_failure",
            )
            raise ExecutionWorkerHttpError(
                operation, path, exc.status_code
            ) from None
        except asyncio.CancelledError:
            self._notify(operation, path, run_id, None, "cancelled")
            raise
        except Exception:
            self._notify(
                operation, path, run_id, None, "transport_failure"
            )
            raise ExecutionWorkerTransportError(operation, path) from None
        self._notify(operation, path, run_id, status, None)
        if raw is None:
            return None
        try:
            value = json.loads(raw)
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ExecutionWorkerTransportError(operation, path) from None
        if not isinstance(value, Mapping):
            raise ExecutionWorkerTransportError(operation, path)
        return value

    def _send(
        self,
        path: str,
        payload: JSONObject,
        token: str | None,
        *,
        allow_no_content: bool,
        allow_not_found: bool,
    ) -> tuple[int, bytes | None]:
        url = urljoin(self.base_url, path.lstrip("/"))
        body = json.dumps(
            payload,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        headers = {
            "Accept": "application/json",
            "Content-Type": "application/json; charset=utf-8",
        }
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        request = Request(url, data=body, headers=headers, method="POST")
        opener = build_opener(_NoRedirects())
        try:
            with opener.open(request, timeout=self.timeout_seconds) as response:
                status = int(response.status)
                if allow_no_content and status == 204:
                    return status, None
                raw = self._read_bounded(response)
                return status, raw
        except HTTPError as exc:
            status = int(exc.code)
            try:
                if allow_no_content and status == 204:
                    return status, None
                if allow_not_found and status == 404:
                    return status, None
            finally:
                exc.close()
            raise ExecutionWorkerHttpError("request", path, status) from None

    def _read_bounded(self, response: Any) -> bytes:
        raw_length = response.headers.get("Content-Length")
        if raw_length is not None:
            try:
                content_length = int(raw_length)
            except ValueError:
                content_length = -1
            if content_length > self.max_response_bytes:
                raise ValueError("Worker response exceeded the configured limit.")
        output = bytearray()
        while True:
            chunk = response.read(min(65_536, self.max_response_bytes + 1))
            if not chunk:
                return bytes(output)
            output.extend(chunk)
            if len(output) > self.max_response_bytes:
                raise ValueError("Worker response exceeded the configured limit.")

    def _notify(
        self,
        operation: str,
        path: str,
        run_id: str | None,
        status_code: int | None,
        error: str | None,
    ) -> None:
        if self.observe is None:
            return
        self.observe(
            ExecutionWorkerTelemetry(
                operation=operation,
                path=path,
                run_id=run_id,
                status_code=status_code,
                error=error,
            )
        )
