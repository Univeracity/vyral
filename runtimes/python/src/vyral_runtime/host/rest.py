from __future__ import annotations

from collections.abc import Awaitable, Callable
from dataclasses import dataclass
import json
import re
from typing import Any, Mapping, Protocol, cast
from urllib.parse import parse_qsl, unquote, urlsplit

from ..contracts import JSONValue
from ..execution import (
    ExecutionRuntimeConflictError,
    ExecutionRuntimeLeaseError,
    ExecutionRuntimePolicyError,
)
from ..local import (
    CollectionNotFoundError,
    CollectionPolicyConflictError,
    QueryValidationError,
    RecordPreconditionFailedError,
    RecordValidationError,
)
from ..runtime import VyralRuntime
from .auth import HostAuthenticationError
from .rest_operations import (
    RestNotFoundError,
    RestOperationDispatcher,
    RestOperationResult,
    RestOperationUnavailableError,
)


_PROBLEM_TYPE_PREFIX = "https://openvyral.com/problems/"


class RestAuthorizer(Protocol):
    async def authorize(
        self,
        operation_id: str,
        authorization_class: str,
        headers: Mapping[str, str],
        path_parameters: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> None: ...


@dataclass(frozen=True)
class RestApplicationConfig:
    max_request_body_bytes: int = 67_108_864
    max_header_count: int = 96
    max_header_bytes: int = 32_768
    max_query_bytes: int = 16_384
    allowed_origins: frozenset[str] = frozenset()
    allowed_hosts: frozenset[str] = frozenset(
        {"localhost", "127.0.0.1", "[::1]", "::1"}
    )

    def __post_init__(self) -> None:
        for name, value, maximum in (
            (
                "max_request_body_bytes",
                self.max_request_body_bytes,
                256 * 1024 * 1024,
            ),
            ("max_header_count", self.max_header_count, 256),
            ("max_header_bytes", self.max_header_bytes, 64 * 1024),
            ("max_query_bytes", self.max_query_bytes, 64 * 1024),
        ):
            if isinstance(value, bool) or not 0 < value <= maximum:
                raise ValueError(
                    f"{name} must be between one and {maximum}."
                )
        if not self.allowed_hosts or any(
            not value.strip() for value in self.allowed_hosts
        ):
            raise ValueError(
                "allowed_hosts must contain non-empty host names."
            )


@dataclass(frozen=True)
class _Route:
    operation_id: str
    method: str
    template: str
    pattern: re.Pattern[str]
    parameter_names: tuple[str, ...]
    authorization_class: str
    request_content_types: frozenset[str]

    def match(self, path: str) -> dict[str, str] | None:
        result = self.pattern.fullmatch(path)
        if result is None:
            return None
        return {
            name: unquote(value)
            for name, value in result.groupdict().items()
        }


class VyralRestApplication:
    """Dependency-free ASGI host for the authoritative Vyral OpenAPI surface."""

    def __init__(
        self,
        runtime: VyralRuntime,
        config: RestApplicationConfig | None = None,
        *,
        authorizer: RestAuthorizer | None = None,
    ) -> None:
        if runtime.config is None:
            raise ValueError(
                "The REST host requires an embedded local runtime."
            )
        self.runtime = runtime
        self.config = config or RestApplicationConfig()
        self.authorizer = authorizer
        self._routes = _load_routes(runtime)
        self._dispatcher = RestOperationDispatcher(runtime)

    @property
    def operation_ids(self) -> tuple[str, ...]:
        return tuple(route.operation_id for route in self._routes)

    async def __call__(
        self,
        scope: Mapping[str, Any],
        receive: Callable[[], Awaitable[Mapping[str, Any]]],
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        if scope.get("type") == "lifespan":
            await self._lifespan(receive, send)
            return
        if scope.get("type") != "http":
            await _send_problem(
                send,
                404,
                "Not Found",
                "Not found.",
                code="resource-not-found",
            )
            return
        raw_headers = cast(
            list[tuple[bytes, bytes]], scope.get("headers", [])
        )
        if len(raw_headers) > self.config.max_header_count:
            await _send_problem(
                send, 431, "Request Header Fields Too Large",
                "Too many request headers.",
                code="headers-too-large",
            )
            return
        if sum(len(name) + len(value) for name, value in raw_headers) > (
            self.config.max_header_bytes
        ):
            await _send_problem(
                send, 431, "Request Header Fields Too Large",
                "Request headers are too large.",
                code="headers-too-large",
            )
            return
        headers = _headers(raw_headers)
        host = headers.get("host")
        if (
            host is not None
            and not _host_allowed(host, self.config.allowed_hosts)
        ):
            await _send_problem(
                send,
                403,
                "Forbidden",
                "Host is not allowed.",
                code="host-not-allowed",
            )
            return
        origin = headers.get("origin")
        if (
            origin is not None
            and origin not in self.config.allowed_origins
        ):
            await _send_problem(
                send,
                403,
                "Forbidden",
                "Origin is not allowed.",
                code="origin-not-allowed",
            )
            return
        path = str(scope.get("path", ""))
        method = str(scope.get("method", "")).upper()
        route, path_parameters, allowed = self._route(method, path)
        if route is None:
            if allowed:
                await _send_problem(
                    send,
                    405,
                    "Method Not Allowed",
                    "The route does not allow this HTTP method.",
                    extra_headers=(
                        (b"allow", ", ".join(allowed).encode("ascii")),
                    ),
                    code="method-not-allowed",
                )
            else:
                await _send_problem(
                    send,
                    404,
                    "Not Found",
                    "Route was not found.",
                    code="route-not-found",
                )
            return
        raw_query = scope.get("query_string", b"")
        if not isinstance(raw_query, bytes):
            raw_query = b""
        if len(raw_query) > self.config.max_query_bytes:
            await _send_problem(
                send,
                414,
                "URI Too Long",
                "Query string is too large.",
                code="query-too-large",
            )
            return
        try:
            query = _query(raw_query)
        except (UnicodeDecodeError, ValueError):
            await _send_problem(
                send,
                400,
                "Bad Request",
                "Query parameters are invalid.",
                code="invalid-query",
            )
            return
        length = headers.get("content-length")
        if length is not None:
            try:
                parsed_length = int(length)
            except ValueError:
                parsed_length = -1
            if parsed_length < 0:
                await _send_problem(
                    send,
                    400,
                    "Bad Request",
                    "Invalid Content-Length.",
                    code="invalid-content-length",
                )
                return
            if parsed_length > self.config.max_request_body_bytes:
                await _send_problem(
                    send,
                    413,
                    "Content Too Large",
                    "Request body is too large.",
                    code="request-body-too-large",
                )
                return
        try:
            raw_body = await _read_body(
                receive, self.config.max_request_body_bytes
            )
        except _BodyTooLarge:
            await _send_problem(
                send,
                413,
                "Content Too Large",
                "Request body is too large.",
                code="request-body-too-large",
            )
            return
        try:
            body = _parse_body(route, headers, raw_body)
            if self.authorizer is not None:
                await self.authorizer.authorize(
                    route.operation_id,
                    route.authorization_class,
                    headers,
                    path_parameters,
                    query,
                    body,
                )
            result = await self._dispatcher.dispatch(
                route.operation_id,
                path_parameters,
                query,
                headers,
                body,
                raw_body,
            )
        except HostAuthenticationError:
            await _send_problem(
                send,
                401,
                "Unauthorized",
                "Valid Vyral API-key authentication is required.",
                code="authentication-required",
            )
            return
        except RestNotFoundError:
            await _send_problem(
                send,
                404,
                "Not Found",
                "The requested resource was not found.",
                code="resource-not-found",
            )
            return
        except RestOperationUnavailableError:
            await _send_problem(
                send,
                501,
                "Not Implemented",
                "This operation is not available in the active runtime.",
                code="operation-unavailable",
            )
            return
        except (
            ExecutionRuntimeConflictError,
            CollectionPolicyConflictError,
        ):
            await _send_problem(
                send,
                409,
                "Conflict",
                "The request conflicts with current state.",
                code="request-conflict",
            )
            return
        except RecordPreconditionFailedError:
            await _send_problem(
                send,
                412,
                "Precondition Failed",
                "The request precondition was not met.",
                code="precondition-failed",
            )
            return
        except (
            ExecutionRuntimeLeaseError,
            ExecutionRuntimePolicyError,
            PermissionError,
        ):
            await _send_problem(
                send,
                403,
                "Forbidden",
                "The request is not permitted.",
                code="request-forbidden",
            )
            return
        except (
            CollectionNotFoundError,
            LookupError,
        ):
            await _send_problem(
                send,
                404,
                "Not Found",
                "The requested resource was not found.",
                code="resource-not-found",
            )
            return
        except (
            json.JSONDecodeError,
            UnicodeDecodeError,
            QueryValidationError,
            RecordValidationError,
            TypeError,
            ValueError,
        ):
            await _send_problem(
                send,
                400,
                "Bad Request",
                "The request is invalid.",
                code="request-invalid",
            )
            return
        except Exception:
            await _send_problem(
                send,
                500,
                "Internal Server Error",
                "The Vyral REST request failed.",
                code="internal-error",
            )
            return
        await _send_result(send, result)

    def _route(
        self, method: str, path: str
    ) -> tuple[_Route | None, dict[str, str], tuple[str, ...]]:
        allowed: set[str] = set()
        for route in self._routes:
            parameters = route.match(path)
            if parameters is None:
                continue
            if route.method == method:
                return route, parameters, ()
            allowed.add(route.method)
        return None, {}, tuple(sorted(allowed))

    async def startup(self) -> None:
        await self._dispatcher.reconcile()

    async def shutdown(self) -> None:
        await self._dispatcher.shutdown()

    async def _lifespan(
        self,
        receive: Callable[[], Awaitable[Mapping[str, Any]]],
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        while True:
            message = await receive()
            message_type = message.get("type")
            if message_type == "lifespan.startup":
                await self.startup()
                await send({"type": "lifespan.startup.complete"})
            elif message_type == "lifespan.shutdown":
                await self.shutdown()
                await send({"type": "lifespan.shutdown.complete"})
                return


def _load_routes(runtime: VyralRuntime) -> tuple[_Route, ...]:
    catalog_operations = runtime.contracts.catalog.get("operations")
    if not isinstance(catalog_operations, list):
        raise ValueError("The operation catalog is malformed.")
    authorization: dict[str, str] = {}
    for item in catalog_operations:
        if not isinstance(item, Mapping):
            continue
        authorization_class = str(
            item.get("authorizationClass", "public")
        )
        operation_ids = item.get("restOperationIds", ())
        if isinstance(operation_ids, list):
            for operation_id in operation_ids:
                authorization[str(operation_id)] = (
                    authorization_class
                )
    routes: list[_Route] = []
    paths = runtime.contracts.openapi.get("paths")
    if not isinstance(paths, Mapping):
        raise ValueError("The OpenAPI path catalog is malformed.")
    for template, path_item in paths.items():
        if not isinstance(template, str) or not isinstance(
            path_item, Mapping
        ):
            continue
        for method, operation in path_item.items():
            if method.upper() not in {
                "GET", "POST", "PUT", "PATCH", "DELETE"
            } or not isinstance(operation, Mapping):
                continue
            operation_id = operation.get("operationId")
            if not isinstance(operation_id, str):
                raise ValueError(
                    f"OpenAPI operation at {template!r} has no operationId."
                )
            request_types: set[str] = set()
            request_body = operation.get("requestBody")
            if isinstance(request_body, Mapping):
                content = request_body.get("content")
                if isinstance(content, Mapping):
                    request_types.update(
                        str(content_type).lower()
                        for content_type in content
                    )
            pattern, names = _route_pattern(template)
            routes.append(
                _Route(
                    operation_id,
                    method.upper(),
                    template,
                    pattern,
                    names,
                    authorization.get(operation_id, "public"),
                    frozenset(request_types),
                )
            )
    routes.sort(
        key=lambda route: (
            -route.template.count("/"),
            route.template.count("{"),
            route.template,
            route.method,
        )
    )
    if len(routes) != runtime.contracts.summary.rest_operation_count:
        raise ValueError(
            "REST route count does not match the contract summary."
        )
    return tuple(routes)


def _route_pattern(
    template: str,
) -> tuple[re.Pattern[str], tuple[str, ...]]:
    names: list[str] = []
    parts: list[str] = []
    position = 0
    for match in re.finditer(r"\{([A-Za-z][A-Za-z0-9_]*)\}", template):
        parts.append(re.escape(template[position : match.start()]))
        name = match.group(1)
        names.append(name)
        parts.append(
            f"(?P<{name}>.+)"
            if name == "key" and match.end() == len(template)
            else f"(?P<{name}>[^/]+)"
        )
        position = match.end()
    parts.append(re.escape(template[position:]))
    return re.compile("".join(parts)), tuple(names)


def _headers(
    raw: list[tuple[bytes, bytes]],
) -> dict[str, str]:
    result: dict[str, str] = {}
    for key, value in raw:
        try:
            name = key.decode("ascii").lower()
            selected = value.decode("latin-1")
        except UnicodeDecodeError:
            continue
        if name in result:
            result[name] += "," + selected
        else:
            result[name] = selected
    return result


def _query(raw: bytes) -> dict[str, str]:
    text = raw.decode("utf-8")
    pairs = parse_qsl(
        text,
        keep_blank_values=True,
        strict_parsing=False,
        max_num_fields=256,
    )
    result: dict[str, str] = {}
    for name, value in pairs:
        if name in result:
            raise ValueError(
                f"Query parameter {name!r} may appear only once."
            )
        result[name] = value
    return result


def _parse_body(
    route: _Route,
    headers: Mapping[str, str],
    body: bytes,
) -> object | None:
    if not body:
        return None
    content_type = headers.get("content-type", "").split(";", 1)[0]
    normalized = content_type.strip().lower()
    if normalized == "application/json" or normalized.endswith("+json"):
        return cast(object, json.loads(body))
    if normalized in {
        "application/octet-stream",
        "multipart/form-data",
    }:
        return body
    if route.request_content_types and normalized not in (
        route.request_content_types
    ):
        raise ValueError(
            f"Unsupported Content-Type {normalized or '<missing>'!r}."
        )
    return body


class _BodyTooLarge(Exception):
    pass


def _host_allowed(
    host: str,
    allowed_hosts: frozenset[str],
) -> bool:
    normalized = host.strip().lower()
    normalized_allowed = {
        value.strip().lower() for value in allowed_hosts
    }
    if normalized in normalized_allowed:
        return True
    try:
        parsed = urlsplit(f"//{normalized}")
    except ValueError:
        return False
    hostname = parsed.hostname
    return hostname is not None and hostname.lower() in {
        value.strip("[]").lower() for value in normalized_allowed
    }


async def _read_body(
    receive: Callable[[], Awaitable[Mapping[str, Any]]],
    maximum: int,
) -> bytes:
    chunks: list[bytes] = []
    total = 0
    while True:
        message = await receive()
        if message.get("type") == "http.disconnect":
            return b""
        chunk = message.get("body", b"")
        if not isinstance(chunk, bytes):
            chunk = b""
        total += len(chunk)
        if total > maximum:
            raise _BodyTooLarge()
        chunks.append(chunk)
        if not message.get("more_body", False):
            return b"".join(chunks)


async def _send_result(
    send: Callable[[Mapping[str, Any]], Awaitable[None]],
    result: RestOperationResult,
) -> None:
    if result.status == 204:
        await send(
            {
                "type": "http.response.start",
                "status": 204,
                "headers": (),
            }
        )
        await send({"type": "http.response.body", "body": b""})
        return
    if isinstance(result.body, bytes):
        body = result.body
    elif isinstance(result.body, str) and result.content_type.startswith(
        "text/"
    ):
        body = result.body.encode("utf-8")
    else:
        body = json.dumps(
            result.body,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
    headers = [
        (b"content-type", result.content_type.encode("ascii")),
        (b"content-length", str(len(body)).encode("ascii")),
        *result.headers,
    ]
    await send(
        {
            "type": "http.response.start",
            "status": result.status,
            "headers": headers,
        }
    )
    await send({"type": "http.response.body", "body": body})


async def _send_problem(
    send: Callable[[Mapping[str, Any]], Awaitable[None]],
    status: int,
    title: str,
    detail: str,
    *,
    code: str = "request-failed",
    extra_headers: tuple[tuple[bytes, bytes], ...] = (),
) -> None:
    problem: dict[str, JSONValue] = {
        "type": _PROBLEM_TYPE_PREFIX + code,
        "title": title,
        "status": status,
        "detail": detail,
    }
    body = json.dumps(
        problem,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    await send(
        {
            "type": "http.response.start",
            "status": status,
            "headers": [
                (b"content-type", b"application/problem+json"),
                (b"content-length", str(len(body)).encode("ascii")),
                *extra_headers,
            ],
        }
    )
    await send({"type": "http.response.body", "body": body})


__all__ = [
    "RestApplicationConfig",
    "RestAuthorizer",
    "VyralRestApplication",
]
