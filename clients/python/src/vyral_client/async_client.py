from __future__ import annotations

import asyncio
import importlib
import json
from typing import Any, BinaryIO, Mapping, cast

from .client import (
    VyralClientError,
    _has_header,
    _header_value,
    _normalize_base_url,
    _parse_problem_body,
    _require_secure_credential_transport,
    _retry_delay_seconds,
)


class AsyncVyralClient:
    """Optional httpx-based async transport for direct and streaming SDK calls.

    Importing the package does not require httpx. Constructing this client without
    an injected transport requires installing ``vyral-client[async]``.
    """

    def __init__(
        self,
        base_url: str = "http://localhost:5220",
        *,
        timeout: float = 30.0,
        api_key: str | None = None,
        bearer_token: str | None = None,
        default_headers: Mapping[str, str] | None = None,
        correlation_id: str | None = None,
        max_retries: int = 0,
        retry_backoff_seconds: float = 0.25,
        transport: Any | None = None,
    ):
        if timeout <= 0:
            raise ValueError("timeout must be greater than zero")
        if max_retries < 0:
            raise ValueError("max_retries must be non-negative")
        if retry_backoff_seconds < 0:
            raise ValueError("retry_backoff_seconds must be non-negative")
        if api_key and bearer_token:
            raise ValueError("api_key and bearer_token are mutually exclusive")

        self.base_url = _normalize_base_url(base_url)
        self.timeout = timeout
        self.api_key = api_key
        self.bearer_token = bearer_token
        self.default_headers = dict(default_headers or {})
        self.correlation_id = correlation_id
        self.max_retries = max_retries
        self.retry_backoff_seconds = retry_backoff_seconds
        configured_headers = dict(self.default_headers)
        if self.bearer_token and not _has_header(configured_headers, "Authorization"):
            configured_headers.setdefault("Authorization", f"Bearer {self.bearer_token}")
        if self.api_key and not _has_header(configured_headers, "Authorization", "X-Vyral-Api-Key"):
            configured_headers.setdefault("X-Vyral-Api-Key", self.api_key)
        _require_secure_credential_transport(self.base_url, configured_headers)
        self._owns_transport = transport is None
        if transport is None:
            try:
                httpx = importlib.import_module("httpx")
            except ModuleNotFoundError as exc:
                raise RuntimeError(
                    "AsyncVyralClient requires the optional async extra: pip install 'vyral-client[async]'"
                ) from exc
            transport = httpx.AsyncClient()
        self._transport = transport

    async def __aenter__(self) -> "AsyncVyralClient":
        return self

    async def __aexit__(self, *_: object) -> None:
        await self.aclose()

    async def aclose(self) -> None:
        if self._owns_transport:
            await self._transport.aclose()

    async def request_json(
        self,
        method: str,
        path: str,
        *,
        payload: Mapping[str, Any] | None = None,
        content: bytes | BinaryIO | None = None,
        headers: Mapping[str, str] | None = None,
        data: Mapping[str, Any] | None = None,
        files: Mapping[str, Any] | None = None,
    ) -> Any:
        response = await self._request(
            method,
            path,
            payload=payload,
            content=content,
            headers=headers,
            data=data,
            files=files,
        )
        return json.loads(response.decode("utf-8")) if response else None

    async def request_bytes(
        self,
        method: str,
        path: str,
        *,
        content: bytes | BinaryIO | None = None,
        headers: Mapping[str, str] | None = None,
    ) -> bytes:
        return await self._request(method, path, content=content, headers=headers)

    async def health(self) -> dict[str, Any]:
        return cast(dict[str, Any], await self.request_json("GET", "/health"))

    async def readiness(self) -> dict[str, Any]:
        return cast(dict[str, Any], await self.request_json("GET", "/readiness"))

    async def ingest_record_artifact(
        self,
        manifest: Mapping[str, Any],
        artifact: BinaryIO | bytes,
        *,
        filename: str = "artifact.bin",
        content_type: str = "application/octet-stream",
        idempotency_key: str | None = None,
        headers: Mapping[str, str] | None = None,
    ) -> dict[str, Any]:
        if not filename or "\r" in filename or "\n" in filename:
            raise ValueError("filename must be non-empty and cannot contain line breaks")
        if not content_type or "\r" in content_type or "\n" in content_type:
            raise ValueError("content_type must be non-empty and cannot contain line breaks")
        selected_headers = dict(headers or {})
        if idempotency_key:
            selected_headers["Idempotency-Key"] = idempotency_key
        return cast(dict[str, Any], await self.request_json(
            "POST",
            "/ingest/record-artifact",
            headers=selected_headers,
            files={
                "manifest": (None, json.dumps(dict(manifest), separators=(",", ":")), "application/json"),
                "artifact": (filename, artifact, content_type),
            },
        ))

    async def raise_execution_event(
        self,
        run_id: str,
        request: Mapping[str, Any],
        *,
        headers: Mapping[str, str] | None = None,
    ) -> dict[str, Any]:
        from urllib.parse import quote

        body_run_id = request.get("runId")
        if body_run_id not in (None, "", run_id):
            raise ValueError("request.runId must match run_id")
        return cast(dict[str, Any], await self.request_json(
            "POST",
            f"/execution/runs/{quote(run_id, safe='')}/events",
            payload=request,
            headers=headers,
        ))

    async def _request(
        self,
        method: str,
        path: str,
        *,
        payload: Mapping[str, Any] | None = None,
        content: bytes | BinaryIO | None = None,
        headers: Mapping[str, str] | None = None,
        data: Mapping[str, Any] | None = None,
        files: Mapping[str, Any] | None = None,
    ) -> bytes:
        request_headers = dict(self.default_headers)
        request_headers.update(headers or {})
        if self.bearer_token and not _has_header(request_headers, "Authorization"):
            request_headers["Authorization"] = f"Bearer {self.bearer_token}"
        if self.api_key and not _has_header(request_headers, "Authorization", "X-Vyral-Api-Key"):
            request_headers["X-Vyral-Api-Key"] = self.api_key
        if self.correlation_id and "X-Correlation-ID" not in request_headers:
            request_headers["X-Correlation-ID"] = self.correlation_id
        _require_secure_credential_transport(self.base_url, request_headers)
        carries_credentials = any(
            name.lower() in {"authorization", "x-vyral-api-key"}
            and bool(value)
            for name, value in request_headers.items()
        )

        method_upper = method.upper()
        can_retry = method_upper in {"GET", "HEAD", "OPTIONS"} or any(
            name.lower() in {"idempotency-key", "x-idempotency-key"}
            for name in request_headers
        )
        if files is not None or (content is not None and not isinstance(content, bytes)):
            can_retry = False

        for attempt in range(self.max_retries + 1):
            try:
                request_options: dict[str, Any] = {
                    "json": dict(payload) if payload is not None else None,
                    "content": content,
                    "headers": request_headers,
                    "data": data,
                    "files": files,
                    "timeout": self.timeout,
                }
                if carries_credentials:
                    request_options["follow_redirects"] = False
                response = await self._transport.request(
                    method_upper,
                    f"{self.base_url}{path}",
                    **request_options,
                )
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                if can_retry and attempt < self.max_retries:
                    await asyncio.sleep(self.retry_backoff_seconds * (2 ** attempt))
                    continue
                failure_name = type(exc).__name__.lower()
                if "timeout" in failure_name:
                    raise VyralClientError.timeout(str(exc) or f"Request timed out after {self.timeout} seconds") from exc
                raise VyralClientError(
                    0,
                    str(exc),
                    problem={"title": "Transport failure", "detail": str(exc), "status": 0},
                    failure_class="transport",
                ) from exc

            await response.aread()
            if 200 <= response.status_code < 300:
                return bytes(response.content)

            retry_after = _header_value(response.headers, "Retry-After")
            correlation_id = _header_value(response.headers, "X-Correlation-ID", "X-Request-ID")
            if can_retry and attempt < self.max_retries and response.status_code in (408, 429, 502, 503, 504):
                await response.aclose()
                await asyncio.sleep(
                    _retry_delay_seconds(retry_after, self.retry_backoff_seconds * (2 ** attempt))
                )
                continue
            body_text = response.content.decode("utf-8", errors="replace")
            raise VyralClientError(
                response.status_code,
                body_text,
                problem=_parse_problem_body(body_text),
                retry_after=retry_after,
                correlation_id=correlation_id,
            )

        raise AssertionError("request retry loop exhausted")
