from __future__ import annotations

import asyncio
import base64
import binascii
from collections import Counter
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, fields, is_dataclass
from datetime import datetime
import hashlib
import hmac
import json
from typing import Any, Mapping, Protocol, cast
import uuid
from urllib.parse import urlsplit

from .._version import CONTRACT_VERSION, RUNTIME_VERSION
from ..contracts import JSONValue
from ..execution import (
    BUILTIN_JOB_PLUGIN_ID,
    DelegateExecutionHandler,
    ExecutionCheckpointWrite,
    ExecutionExternalEventRequest,
    ExecutionHandlerDescriptor,
    ExecutionHistoryQuery,
    ExecutionMaintenanceDispatchReconcileRequest,
    ExecutionRun,
    ExecutionRunRequest,
    ExecutionRunResult,
    ExecutionRunContext,
    ExecutionScope,
    RuntimeJobHandlerIds,
)
from ..graph import (
    get_graph_provider_shape,
    list_graph_provider_shapes,
)
from ..retrieval import get_retrieval_profiles
from ..runtime import VyralRuntime
from .auth import HostAuthenticationError
from .diagnostics import public_readiness_document


MCP_PROTOCOL_VERSION = "2026-07-28"
_HEADER_MISMATCH = -32020
_MISSING_CAPABILITY = -32021
_UNSUPPORTED_VERSION = -32022
_PARSE_ERROR = -32700
_INVALID_REQUEST = -32600
_METHOD_NOT_FOUND = -32601
_INVALID_PARAMS = -32602
_INTERNAL_ERROR = -32603
_TASK_CAPABILITY = "io.modelcontextprotocol/tasks"
_CONFORMANCE_PLUGIN_ID = "mcp-conformance"
_CONFORMANCE_TASK_HANDLERS = {
    "slow_compute": "mcp.conformance.slow_compute",
    "failing_job": "mcp.conformance.failing_job",
    "protocol_error_job": "mcp.conformance.protocol_error_job",
    "confirm_delete": "mcp.conformance.confirm_delete",
    "multi_input": "mcp.conformance.multi_input",
    "test_tool_with_task": "mcp.conformance.composed",
}
_CONFORMANCE_INPUT_CHECKPOINT = "mcp-input-requests"
_CONFORMANCE_IMAGE_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHw"
    "AFBQIAX8jx0gAAAABJRU5ErkJggg=="
)
_CONFORMANCE_AUDIO_BASE64 = (
    "UklGRiYAAABXQVZFZm10IBAAAAABAAEAQB8AAAB9AAACABAAZGF0YQIAAAA="
)
_CORE_CONFORMANCE_TOOLS = frozenset(
    {
        "test_simple_text",
        "test_image_content",
        "test_audio_content",
        "test_embedded_resource",
        "test_multiple_content_types",
        "test_error_handling",
        "test_tool_with_progress",
        "json_schema_2020_12_tool",
    }
)
_MRTR_CONFORMANCE_TOOLS = frozenset(
    {
        "test_input_required_result_elicitation",
        "test_input_required_result_sampling",
        "test_input_required_result_list_roots",
        "test_input_required_result_request_state",
        "test_input_required_result_multiple_inputs",
        "test_input_required_result_multi_round",
        "test_incomplete_result_elicitation",
        "test_input_required_result_tampered_state",
        "test_input_required_result_capabilities",
    }
)
_CONFORMANCE_PROMPTS = frozenset(
    {
        "test_simple_prompt",
        "test_prompt_with_arguments",
        "test_prompt_with_embedded_resource",
        "test_prompt_with_image",
        "test_input_required_result_prompt",
    }
)
_CONFORMANCE_STATIC_RESOURCES = frozenset(
    {"test://static-text", "test://static-binary"}
)
_CONFORMANCE_TOOL_NAMES = (
    _CORE_CONFORMANCE_TOOLS
    | _MRTR_CONFORMANCE_TOOLS
    | frozenset(_CONFORMANCE_TASK_HANDLERS)
    | {
        "test_custom_header",
        "greet",
        "test_logging_tool",
        "test_missing_capability",
        "test_streaming_elicitation",
    }
)


class McpAuthorizer(Protocol):
    async def authorize(
        self,
        operation_id: str,
        headers: Mapping[str, str],
        arguments: Mapping[str, Any],
    ) -> None: ...


@dataclass(frozen=True)
class McpApplicationConfig:
    endpoint_path: str = "/mcp"
    max_request_body_bytes: int = 1_048_576
    max_header_count: int = 64
    max_header_bytes: int = 16_384
    allowed_origins: frozenset[str] = frozenset()
    allowed_hosts: frozenset[str] = frozenset(
        {"localhost", "127.0.0.1", "[::1]", "::1"}
    )
    enabled_operation_ids: frozenset[str] = frozenset()
    disabled_operation_ids: frozenset[str] = frozenset()
    list_ttl_ms: int = 300_000
    resource_ttl_ms: int = 60_000
    task_ttl_ms: int = 86_400_000
    task_poll_interval_ms: int = 1_000
    require_explicit_origins: bool = False
    enable_conformance_diagnostics: bool = False

    def __post_init__(self) -> None:
        if (
            not self.endpoint_path.startswith("/")
            or any(character.isspace() for character in self.endpoint_path)
            or "?" in self.endpoint_path
            or "#" in self.endpoint_path
        ):
            raise ValueError(
                "MCP endpoint_path must be an absolute path without "
                "whitespace, query, or fragment."
            )
        for name, value, maximum in (
            (
                "max_request_body_bytes",
                self.max_request_body_bytes,
                16 * 1024 * 1024,
            ),
            ("max_header_count", self.max_header_count, 256),
            ("max_header_bytes", self.max_header_bytes, 64 * 1024),
            ("list_ttl_ms", self.list_ttl_ms, 86_400_000),
            ("resource_ttl_ms", self.resource_ttl_ms, 86_400_000),
            ("task_ttl_ms", self.task_ttl_ms, 30 * 86_400_000),
            (
                "task_poll_interval_ms",
                self.task_poll_interval_ms,
                60_000,
            ),
        ):
            if isinstance(value, bool) or not 0 < value <= maximum:
                raise ValueError(
                    f"{name} must be between one and {maximum}."
                )
        if not isinstance(self.require_explicit_origins, bool):
            raise TypeError("require_explicit_origins must be a boolean.")
        if not isinstance(self.enable_conformance_diagnostics, bool):
            raise TypeError(
                "enable_conformance_diagnostics must be a boolean."
            )


@dataclass(frozen=True)
class _CatalogEntry:
    operation_id: str
    exposure: str
    mcp_id: str
    default_enabled: bool
    authorization_class: str


class StatelessMcpApplication:
    """A stateless MCP 2026-07-28 ASGI endpoint over Vyral services."""

    def __init__(
        self,
        runtime: VyralRuntime,
        config: McpApplicationConfig | None = None,
        *,
        authorizer: McpAuthorizer | None = None,
    ) -> None:
        if runtime.config is None:
            raise ValueError(
                "The MCP host requires an embedded local runtime."
            )
        self.runtime = runtime
        self.config = config or McpApplicationConfig()
        self.authorizer = authorizer
        self._catalog = _load_catalog(runtime)
        known = {
            item.operation_id for item in self._catalog
        } | {item.mcp_id for item in self._catalog}
        unknown = (
            self.config.enabled_operation_ids
            | self.config.disabled_operation_ids
        ) - known
        if unknown:
            raise ValueError(
                "Unknown MCP operation id(s): "
                + ", ".join(sorted(unknown))
            )
        self._dispatch_tasks: set[asyncio.Task[None]] = set()
        if self.config.enable_conformance_diagnostics:
            self._register_conformance_handlers()

    async def __call__(
        self,
        scope: Mapping[str, Any],
        receive: Callable[[], Awaitable[Mapping[str, Any]]],
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        if scope.get("type") == "lifespan":
            await self._lifespan(receive, send)
            return
        if (
            scope.get("type") != "http"
            or scope.get("path") != self.config.endpoint_path
        ):
            await _send_json(send, 404, {"detail": "Not found."})
            return
        if scope.get("method") != "POST":
            await _send_json(
                send,
                405,
                {"detail": "The MCP endpoint accepts POST only."},
                extra_headers=((b"allow", b"POST"),),
            )
            return
        raw_headers = cast(
            list[tuple[bytes, bytes]], scope.get("headers", [])
        )
        if len(raw_headers) > self.config.max_header_count:
            await _send_json(
                send, 431, {"detail": "Too many request headers."}
            )
            return
        if sum(len(key) + len(value) for key, value in raw_headers) > (
            self.config.max_header_bytes
        ):
            await _send_json(
                send, 431, {"detail": "Request headers are too large."}
            )
            return
        headers = _headers(raw_headers)
        if not _request_origin_allowed(
            headers,
            self.config.allowed_origins,
            self.config.allowed_hosts,
            self.config.require_explicit_origins,
        ):
            await _send_json(
                send, 403, {"detail": "Origin is not allowed."}
            )
            return
        length = headers.get("content-length")
        if length is not None:
            try:
                parsed_length = int(length)
            except ValueError:
                parsed_length = -1
            if parsed_length < 0:
                await _send_json(
                    send, 400, {"detail": "Invalid Content-Length."}
                )
                return
            if parsed_length > self.config.max_request_body_bytes:
                await _send_json(
                    send, 413, {"detail": "Request body is too large."}
                )
                return
        content_type = headers.get("content-type", "").split(";", 1)[0]
        if content_type.strip().lower() != "application/json":
            await _send_json(
                send,
                415,
                {"detail": "MCP requests require application/json."},
            )
            return
        try:
            body = await _read_body(
                receive, self.config.max_request_body_bytes
            )
        except _BodyTooLarge:
            await _send_json(
                send, 413, {"detail": "Request body is too large."}
            )
            return
        try:
            message = json.loads(body)
        except (UnicodeDecodeError, json.JSONDecodeError):
            await self._error(
                send,
                400,
                None,
                _PARSE_ERROR,
                "Parse error",
            )
            return
        if not isinstance(message, Mapping):
            await self._error(
                send,
                400,
                None,
                _INVALID_REQUEST,
                "Invalid Request",
            )
            return
        request_id = message.get("id")
        try:
            method, params, meta = _validate_request(
                message, headers
            )
            if self.config.enable_conformance_diagnostics:
                _validate_conformance_headers(
                    method, params, headers
                )
        except _McpError as error:
            await self._error(
                send,
                error.http_status,
                request_id,
                error.code,
                error.message,
                error.data,
            )
            return
        if request_id is None:
            await _send_empty(send, 202)
            return
        try:
            result = await self._dispatch(
                method, params, meta, headers
            )
        except HostAuthenticationError:
            await self._error(
                send,
                401,
                request_id,
                -32000,
                "Unauthorized",
                {"code": "vyral.authentication.required"},
            )
            return
        except _McpError as error:
            await self._error(
                send,
                error.http_status,
                request_id,
                error.code,
                error.message,
                error.data,
            )
            return
        except (TypeError, ValueError, LookupError):
            await self._error(
                send,
                400,
                request_id,
                _INVALID_PARAMS,
                "Invalid parameters.",
                {"code": "vyral.request.invalid_parameters"},
            )
            return
        except Exception:
            await self._error(
                send,
                500,
                request_id,
                _INTERNAL_ERROR,
                "The Vyral MCP request failed.",
            )
            return
        result.setdefault(
            "_meta",
            {
                "io.modelcontextprotocol/serverInfo": {
                    "name": "vyral-python",
                    "version": RUNTIME_VERSION,
                }
            },
        )
        progress_token = _conformance_progress_token(
            method, params, meta, self.config.enable_conformance_diagnostics
        )
        if progress_token is not None:
            progress_messages: list[dict[str, JSONValue]] = []
            for progress in (0, 50, 100):
                progress_messages.append(
                    {
                        "jsonrpc": "2.0",
                        "method": "notifications/progress",
                        "params": {
                            "progressToken": progress_token,
                            "progress": progress,
                            "total": 100,
                        },
                    }
                )
            progress_messages.append(
                {
                    "jsonrpc": "2.0",
                    "id": cast(JSONValue, request_id),
                    "result": result,
                }
            )
            await _send_sse_json_rpc(send, progress_messages)
            return
        await _send_json(
            send,
            200,
            {"jsonrpc": "2.0", "id": request_id, "result": result},
        )

    async def _dispatch(
        self,
        method: str,
        params: Mapping[str, Any],
        meta: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        if method == "server/discover":
            return self._discover()
        if method == "tools/list":
            await self._authorize_catalog("tools/list", headers)
            return self._list_tools()
        if method == "resources/list":
            await self._authorize_catalog("resources/list", headers)
            return self._list_resources()
        if method == "resources/templates/list":
            await self._authorize_catalog(
                "resources/templates/list", headers
            )
            return self._list_resource_templates()
        if (
            method == "prompts/list"
            and self.config.enable_conformance_diagnostics
        ):
            await self._authorize_catalog("prompts/list", headers)
            return self._list_conformance_prompts()
        if (
            method == "prompts/get"
            and self.config.enable_conformance_diagnostics
        ):
            await self._authorize_catalog("prompts/get", headers)
            return self._get_conformance_prompt(params)
        if (
            method == "completion/complete"
            and self.config.enable_conformance_diagnostics
        ):
            await self._authorize_catalog("completion/complete", headers)
            return {
                "resultType": "complete",
                "completion": {
                    "values": [],
                    "hasMore": False,
                    "total": 0,
                },
            }
        if method == "resources/read":
            return await self._read_resource(params, headers)
        if method == "tools/call":
            return await self._call_tool(
                params, meta, headers
            )
        if method == "tasks/get":
            _require_task_capability(meta)
            return await self._get_task(params, headers)
        if method == "tasks/update":
            _require_task_capability(meta)
            return await self._update_task(params, headers)
        if method == "tasks/cancel":
            _require_task_capability(meta)
            return await self._cancel_task(params, headers)
        raise _McpError(
            404, _METHOD_NOT_FOUND, f"Method {method!r} was not found."
        )

    async def _authorize_catalog(
        self,
        method: str,
        headers: Mapping[str, str],
    ) -> None:
        if self.authorizer is not None:
            await self.authorizer.authorize(method, headers, {})

    def _discover(self) -> dict[str, JSONValue]:
        capabilities: dict[str, JSONValue] = {
            "tools": {},
            "resources": {},
        }
        if self.config.enable_conformance_diagnostics:
            capabilities["prompts"] = {}
            capabilities["completions"] = {}
        if any(
            item.exposure == "task" and self._enabled(item)
            for item in self._catalog
        ) or self.config.enable_conformance_diagnostics:
            capabilities["extensions"] = {
                _TASK_CAPABILITY: {}
            }
        return {
            "resultType": "complete",
            "supportedVersions": [MCP_PROTOCOL_VERSION],
            "capabilities": capabilities,
            "_meta": {
                "io.modelcontextprotocol/serverInfo": {
                    "name": "vyral-python",
                    "version": RUNTIME_VERSION,
                }
            },
            "instructions": (
                "Use Vyral for bounded local record, retrieval, RAG, "
                "graph, and durable execution operations. Use REST for "
                "binary transfer. Every request is stateless and "
                "independently authorized."
            ),
            "ttlMs": self.config.list_ttl_ms,
            "cacheScope": "private",
        }

    def _list_tools(self) -> dict[str, JSONValue]:
        tools: list[JSONValue] = []
        if self.config.enable_conformance_diagnostics:
            tools.extend(
                [
                    {
                        "name": "test_logging_tool",
                        "description": (
                            "MCP conformance diagnostic; never enabled "
                            "in ordinary Vyral deployments."
                        ),
                        "inputSchema": {
                            "$schema": (
                                "https://json-schema.org/draft/"
                                "2020-12/schema"
                            ),
                            "type": "object",
                            "properties": {},
                            "additionalProperties": False,
                        },
                    },
                    {
                        "name": "test_custom_header",
                        "description": (
                            "MCP custom-header conformance diagnostic."
                        ),
                        "inputSchema": {
                            "$schema": (
                                "https://json-schema.org/draft/"
                                "2020-12/schema"
                            ),
                            "type": "object",
                            "properties": {
                                "value": {
                                    "type": "string",
                                    "x-mcp-header": "Value",
                                }
                            },
                            "required": ["value"],
                            "additionalProperties": False,
                        },
                    },
                ]
            )
            for name in (
                "test_missing_capability",
                "test_streaming_elicitation",
            ):
                tools.append(
                    {
                        "name": name,
                        "description": (
                            "MCP conformance diagnostic; never enabled "
                            "in ordinary Vyral deployments."
                        ),
                        "inputSchema": {
                            "$schema": (
                                "https://json-schema.org/draft/"
                                "2020-12/schema"
                            ),
                            "type": "object",
                            "properties": {},
                            "additionalProperties": False,
                        },
                    }
                )
            tools.extend(_conformance_task_descriptors())
            tools.extend(_core_conformance_tool_descriptors())
            tools.extend(_mrtr_conformance_tool_descriptors())
        for item in sorted(
            self._catalog, key=lambda entry: entry.mcp_id
        ):
            if item.exposure not in {"tool", "task"}:
                continue
            if not self._enabled(item) or not _implemented(item.mcp_id):
                continue
            schema = _TOOL_SCHEMAS.get(
                item.mcp_id,
                {"type": "object", "additionalProperties": True},
            )
            tools.append(
                {
                    "name": item.mcp_id,
                    "description": _TOOL_DESCRIPTIONS.get(
                        item.mcp_id,
                        f"Vyral operation {item.operation_id}.",
                    ),
                    "inputSchema": schema,
                    "annotations": {
                        "readOnlyHint": item.exposure == "tool",
                        "destructiveHint": (
                            item.exposure == "task"
                            and item.operation_id
                            in {
                                "startExecutionRun",
                                "startCollectionImportJob",
                                "startRecordBatchUpsertJob",
                                "startRagIngestionTextJob",
                                "startRagIngestionBatchJob",
                                "startGraphImportJob",
                            }
                        ),
                    },
                }
            )
        return {
            "resultType": "complete",
            "tools": tools,
            "ttlMs": self.config.list_ttl_ms,
            "cacheScope": "private",
        }

    def _list_resources(self) -> dict[str, JSONValue]:
        resources: list[JSONValue] = []
        if self.config.enable_conformance_diagnostics:
            resources.extend(_conformance_resource_descriptors())
        for item in sorted(
            self._catalog, key=lambda entry: entry.mcp_id
        ):
            if item.exposure != "resource" or not self._enabled(item):
                continue
            resources.append(_RESOURCE_DESCRIPTORS[item.mcp_id])
        return {
            "resultType": "complete",
            "resources": resources,
            "ttlMs": self.config.list_ttl_ms,
            "cacheScope": "private",
        }

    def _list_resource_templates(self) -> dict[str, JSONValue]:
        templates: list[JSONValue] = []
        if self.config.enable_conformance_diagnostics:
            templates.append(
                {
                    "uriTemplate": "test://template/{id}/data",
                    "name": "Resource Template",
                    "description": (
                        "MCP conformance diagnostic; never enabled "
                        "in ordinary Vyral deployments."
                    ),
                    "mimeType": "application/json",
                }
            )
        return {
            "resultType": "complete",
            "resourceTemplates": templates,
            "ttlMs": self.config.list_ttl_ms,
            "cacheScope": (
                "private"
                if self.config.enable_conformance_diagnostics
                else "public"
            ),
        }

    def _list_conformance_prompts(self) -> dict[str, JSONValue]:
        return {
            "resultType": "complete",
            "prompts": _conformance_prompt_descriptors(),
            "ttlMs": self.config.list_ttl_ms,
            "cacheScope": "private",
        }

    def _get_conformance_prompt(
        self, params: Mapping[str, Any]
    ) -> dict[str, JSONValue]:
        name = _required_text(params, "name")
        if name not in _CONFORMANCE_PROMPTS:
            raise LookupError(f"Prompt {name!r} was not found.")
        arguments_value = params.get("arguments", {})
        if not isinstance(arguments_value, Mapping):
            raise TypeError("Prompt arguments must be an object.")
        arguments = cast(Mapping[str, Any], arguments_value)
        if name == "test_input_required_result_prompt":
            input_responses = params.get("inputResponses")
            if not isinstance(input_responses, Mapping):
                return {
                    "resultType": "input_required",
                    "inputRequests": {
                        "user_context": _elicitation_input(
                            "What context should the prompt use?",
                            "context",
                        )
                    },
                }
            context = _elicited_value(
                input_responses, "user_context", "context"
            )
            return {
                "resultType": "complete",
                "description": (
                    "Prompt customized with elicited user context."
                ),
                "messages": [
                    _prompt_text(
                        f"Please continue using context: {context}"
                    )
                ],
            }
        messages: list[JSONValue]
        if name == "test_simple_prompt":
            messages = [
                _prompt_text(
                    "This is a simple prompt for testing."
                )
            ]
        elif name == "test_prompt_with_arguments":
            arg1 = _required_text(arguments, "arg1")
            arg2 = _required_text(arguments, "arg2")
            messages = [
                _prompt_text(
                    f"Prompt with arguments: arg1={arg1}, arg2={arg2}"
                )
            ]
        elif name == "test_prompt_with_embedded_resource":
            resource_uri = _required_text(arguments, "resourceUri")
            messages = [
                {
                    "role": "user",
                    "content": {
                        "type": "resource",
                        "resource": {
                            "uri": resource_uri,
                            "mimeType": "text/plain",
                            "text": (
                                "Embedded resource content for testing."
                            ),
                        },
                    },
                },
                _prompt_text(
                    "Please process the embedded resource above."
                ),
            ]
        else:
            messages = [
                {
                    "role": "user",
                    "content": {
                        "type": "image",
                        "data": _CONFORMANCE_IMAGE_BASE64,
                        "mimeType": "image/png",
                    },
                },
                _prompt_text("Please analyze the image above."),
            ]
        return {
            "resultType": "complete",
            "messages": messages,
        }

    async def _read_resource(
        self,
        params: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        uri = _required_text(params, "uri")
        if (
            self.config.enable_conformance_diagnostics
            and _is_conformance_resource(uri)
        ):
            await self._authorize_catalog("resources/read", headers)
            return _read_conformance_resource(
                uri, self.config.resource_ttl_ms
            )
        item = self._entry(uri, "resource")
        await self._authorize(item, headers, {"uri": uri})
        if uri == "vyral://health/v1":
            value: object = {
                "status": "ok",
                "protocolVersion": MCP_PROTOCOL_VERSION,
                "mcp": {"enabled": True, "stateless": True},
                "recordStore": type(self.runtime.records).__name__,
                "executionAdapter": (
                    self.runtime.execution.options.adapter_id
                ),
            }
            mime_type = "application/json"
        elif uri == "vyral://readiness/v1":
            value = public_readiness_document(
                await self.runtime.areadiness()
            )
            mime_type = "application/json"
        elif uri == "vyral://open_api_contract/v1":
            value = self.runtime.contracts.openapi
            mime_type = "application/json"
        elif uri == "vyral://public_schema_contract/v1":
            value = self.runtime.contracts.schema
            mime_type = "application/schema+json"
        else:
            raise LookupError(f"Resource {uri!r} was not found.")
        return {
            "resultType": "complete",
            "contents": [
                {
                    "uri": uri,
                    "mimeType": mime_type,
                    "text": json.dumps(
                        _wire(value),
                        ensure_ascii=False,
                        separators=(",", ":"),
                    ),
                }
            ],
            "ttlMs": self.config.resource_ttl_ms,
            "cacheScope": "private",
        }

    async def _call_tool(
        self,
        params: Mapping[str, Any],
        meta: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        name = _required_text(params, "name")
        arguments_value = params.get("arguments", {})
        if not isinstance(arguments_value, Mapping):
            raise TypeError("Tool arguments must be an object.")
        arguments = cast(Mapping[str, Any], arguments_value)
        if (
            self.config.enable_conformance_diagnostics
            and name in _CONFORMANCE_TOOL_NAMES
        ):
            await self._authorize_catalog("tools/call", headers)
        if (
            self.config.enable_conformance_diagnostics
            and name in _CORE_CONFORMANCE_TOOLS
        ):
            return _call_core_conformance_tool(name, meta)
        if (
            self.config.enable_conformance_diagnostics
            and name in _MRTR_CONFORMANCE_TOOLS
        ):
            return _call_mrtr_conformance_tool(
                name, params, meta
            )
        if (
            self.config.enable_conformance_diagnostics
            and name
            in {
                "test_custom_header",
                "greet",
                "test_logging_tool",
                "test_missing_capability",
                "test_streaming_elicitation",
            }
        ):
            return self._call_conformance_tool(
                name, meta, arguments
            )
        if (
            self.config.enable_conformance_diagnostics
            and name == "test_tool_with_task"
        ):
            return await self._call_composed_conformance_task(
                params, meta
            )
        if (
            self.config.enable_conformance_diagnostics
            and name in _CONFORMANCE_TASK_HANDLERS
        ):
            if not _has_task_capability(meta):
                if name != "slow_compute":
                    _require_task_capability(meta)
                return await self._call_conformance_task_sync(
                    name, arguments
                )
            run = await self._start_conformance_task(
                name, arguments
            )
            self._track_dispatch()
            return self._create_task_result(run)
        item = self._entry(name, None)
        await self._authorize(item, headers, arguments)
        if item.exposure == "task":
            _require_task_capability(meta)
            run = await self._start_task(name, arguments, headers)
            self._track_dispatch()
            return self._create_task_result(run)
        result = await self._invoke_read_tool(name, arguments)
        structured = cast(JSONValue, _wire(result))
        return {
            "resultType": "complete",
            "content": [
                {
                    "type": "text",
                    "text": json.dumps(
                        structured,
                        ensure_ascii=False,
                        separators=(",", ":"),
                    ),
                }
            ],
            "structuredContent": structured,
            "isError": False,
        }

    async def _call_composed_conformance_task(
        self,
        params: Mapping[str, Any],
        meta: Mapping[str, Any],
    ) -> dict[str, JSONValue]:
        input_responses = params.get("inputResponses")
        if not isinstance(input_responses, Mapping):
            return {
                "resultType": "input_required",
                "inputRequests": {
                    "user_name": _elicitation_request(
                        "Provide a name for the durable task.",
                        "name",
                    )
                },
            }
        response = input_responses.get("user_name")
        content = (
            response.get("content")
            if isinstance(response, Mapping)
            else None
        )
        name = (
            content.get("name")
            if isinstance(content, Mapping)
            else None
        )
        if not isinstance(name, str) or not name:
            raise TypeError(
                "The user_name response must contain a name."
            )
        _require_task_capability(meta)
        run = await self._start_conformance_task(
            "test_tool_with_task", {"name": name}
        )
        self._track_dispatch()
        return self._create_task_result(run)

    async def _call_conformance_task_sync(
        self, name: str, arguments: Mapping[str, Any]
    ) -> dict[str, JSONValue]:
        if name == "slow_compute":
            seconds_value = arguments.get("seconds", 0)
            seconds = (
                float(seconds_value)
                if isinstance(seconds_value, (int, float))
                and not isinstance(seconds_value, bool)
                else 0.0
            )
            await asyncio.sleep(max(0.0, min(seconds, 0.1)))
            is_error = False
            structured: JSONValue = {
                "label": cast(JSONValue, arguments.get("label")),
                "seconds": seconds,
            }
        elif name == "failing_job":
            is_error = True
            structured = {
                "message": "Intentional conformance tool error."
            }
        else:
            raise _McpError(
                500,
                _INTERNAL_ERROR,
                "Intentional conformance protocol error.",
            )
        return {
            "resultType": "complete",
            "content": [
                {
                    "type": "text",
                    "text": json.dumps(
                        structured, separators=(",", ":")
                    ),
                }
            ],
            "structuredContent": structured,
            "isError": is_error,
        }

    def _call_conformance_tool(
        self,
        name: str,
        meta: Mapping[str, Any],
        arguments: Mapping[str, Any],
    ) -> dict[str, JSONValue]:
        capabilities = meta.get(
            "io.modelcontextprotocol/clientCapabilities"
        )
        if (
            name == "test_missing_capability"
            and (
                not isinstance(capabilities, Mapping)
                or not isinstance(capabilities.get("sampling"), Mapping)
            )
        ):
            raise _McpError(
                400,
                _MISSING_CAPABILITY,
                "The request requires the sampling capability.",
                {"requiredCapabilities": {"sampling": {}}},
            )
        text = (
            f"Hello, {arguments.get('name', 'World')}!"
            if name == "greet"
            else "conformance diagnostic complete"
        )
        return {
            "resultType": "complete",
            "content": [
                {
                    "type": "text",
                    "text": text,
                }
            ],
            "structuredContent": {
                "diagnostic": name,
                "completed": True,
            },
            "isError": False,
        }

    async def _start_conformance_task(
        self, name: str, arguments: Mapping[str, Any]
    ) -> ExecutionRun:
        handler_id = _CONFORMANCE_TASK_HANDLERS[name]
        return await self.runtime.execution.start_run(
            ExecutionRunRequest(
                handler_id,
                plugin_id=_CONFORMANCE_PLUGIN_ID,
                payload=cast(
                    dict[str, JSONValue], dict(arguments)
                ),
            )
        )

    def _create_task_result(
        self, run: ExecutionRun
    ) -> dict[str, JSONValue]:
        return {
            "resultType": "task",
            "content": [],
            "taskId": run.id,
            "status": _task_status(run),
            "statusMessage": f"Execution run is {run.status}.",
            "createdAt": _date(run.created_at_utc),
            "lastUpdatedAt": _date(run.updated_at_utc),
            "ttlMs": self.config.task_ttl_ms,
            "pollIntervalMs": self.config.task_poll_interval_ms,
        }

    def _register_conformance_handlers(self) -> None:
        for name, handler_id in _CONFORMANCE_TASK_HANDLERS.items():
            callback = (
                _conformance_slow_compute
                if name == "slow_compute"
                else (
                    _conformance_tool_error
                    if name == "failing_job"
                    else (
                        _conformance_protocol_error
                        if name == "protocol_error_job"
                        else (
                            _conformance_input_task
                            if name
                            in {"confirm_delete", "multi_input"}
                            else _conformance_composed_task
                        )
                    )
                )
            )
            self.runtime.execution.register_handler(
                DelegateExecutionHandler(
                    ExecutionHandlerDescriptor(
                        handler_id,
                        f"MCP conformance {name}",
                        plugin_id=_CONFORMANCE_PLUGIN_ID,
                    ),
                    callback,
                )
            )

    async def _invoke_read_tool(
        self, name: str, arguments: Mapping[str, Any]
    ) -> object:
        if name == "vyral_list_graph_provider_shapes_v1":
            return list_graph_provider_shapes()
        if name == "vyral_get_graph_provider_shape_v1":
            provider_shape = get_graph_provider_shape(
                _required_text(arguments, "providerId")
            )
            if provider_shape is None:
                raise LookupError("Graph provider shape was not found.")
            return provider_shape
        if name == "vyral_list_collections_v1":
            return await self.runtime.async_records.list_collections()
        if name == "vyral_get_collection_v1":
            collection_policy = (
                await self.runtime.async_records.get_collection_policy(
                    _required_text(arguments, "collection")
                )
            )
            if collection_policy is None:
                raise LookupError("Collection was not found.")
            return collection_policy
        if name == "vyral_inspect_collection_v1":
            return await self._inspect_collection(arguments)
        if name == "vyral_get_record_v1":
            record = await self.runtime.async_records.get_record(
                _required_text(arguments, "collection"),
                _required_text(arguments, "partitionKey"),
                _required_text(arguments, "id"),
            )
            if record is None:
                raise LookupError("Record was not found.")
            return record
        if name == "vyral_query_records_v1":
            return await self.runtime.async_records.query_records_page(
                _required_text(arguments, "collection"),
                _required_mapping(arguments, "query"),
            )
        if name == "vyral_search_records_v1":
            return await self.runtime.async_records.search_records_page(
                _required_text(arguments, "collection"),
                _required_mapping(arguments, "query"),
            )
        if name == "vyral_list_retrieval_profiles_v1":
            return get_retrieval_profiles()
        if name == "vyral_retrieve_v1":
            return await self.runtime.retrieval.asearch(
                _required_mapping(arguments, "request")
            )
        if name == "vyral_build_rag_context_v1":
            return await self.runtime.rag_context.abuild_context(
                _required_mapping(arguments, "request")
            )
        if name == "vyral_build_rag_prompt_v1":
            return await self.runtime.rag_prompts.abuild_prompt(
                _required_mapping(arguments, "request")
            )
        if name == "vyral_traverse_graph_v1":
            graph_result: object | None = await self.runtime.graph.atraverse(
                _required_text(arguments, "collection"),
                _required_mapping(arguments, "request"),
            )
        elif name == "vyral_inspect_graph_collection_v1":
            graph_result = await self.runtime.graph.ainspect(
                _required_text(arguments, "collection"),
                _required_mapping(arguments, "request"),
            )
        elif name == "vyral_doctor_graph_collection_v1":
            graph_result = await self.runtime.graph.adoctor(
                _required_text(arguments, "collection"),
                _required_mapping(arguments, "request"),
            )
        elif name == "vyral_get_execution_run_v1":
            graph_result = await self.runtime.execution.get_run(
                _required_text(arguments, "runId"),
                include_result=_optional_bool(
                    arguments, "includeResult", True
                ),
            )
        elif name == "vyral_get_execution_run_history_v1":
            graph_result = await self.runtime.execution.get_history(
                _required_text(arguments, "runId"),
                ExecutionHistoryQuery(
                    limit=_optional_int(arguments, "limit")
                ),
            )
        else:
            raise LookupError(f"Tool {name!r} was not found.")
        if graph_result is None:
            raise LookupError("Requested Vyral value was not found.")
        return graph_result

    async def _inspect_collection(
        self, arguments: Mapping[str, Any]
    ) -> dict[str, JSONValue]:
        collection = _required_text(arguments, "collection")
        limit = _optional_int(arguments, "anomalyLimit") or 50
        if not 1 <= limit <= 500:
            raise ValueError("anomalyLimit must be between 1 and 500.")
        policy = await self.runtime.async_records.get_collection_policy(
            collection
        )
        if policy is None:
            raise LookupError("Collection was not found.")
        records = await self.runtime.async_records.query_all_records(
            collection
        )
        type_counts = Counter(record.type or "" for record in records)
        partitions = {record.partition_key for record in records}
        rag_chunks = [
            record for record in records if record.type == "rag.chunk"
        ]
        rag_manifests = [
            record for record in records if record.type == "rag.manifest"
        ]
        document_ids = {
            str(record.metadata["documentId"])
            for record in (*rag_chunks, *rag_manifests)
            if record.metadata is not None
            and record.metadata.get("documentId") is not None
        }
        vectors: list[JSONValue] = []
        anomalies: list[JSONValue] = []
        declared = {item.name for item in policy.vector_policies}
        extra: Counter[str] = Counter()
        for vector_policy in policy.vector_policies:
            present = missing = not_applicable = empty = mismatch = 0
            models: Counter[str] = Counter()
            sources: Counter[str] = Counter()
            for record in records:
                vector = (
                    record.vectors.get(vector_policy.name)
                    if record.vectors is not None
                    else None
                )
                if vector is None:
                    if record.type in {
                        "rag.manifest",
                        "graph.envelope",
                    }:
                        not_applicable += 1
                    else:
                        missing += 1
                    continue
                present += 1
                if not vector.values:
                    empty += 1
                if (
                    len(vector.values) != vector_policy.dimensions
                    or vector.dimensions != vector_policy.dimensions
                ):
                    mismatch += 1
                if vector.model:
                    models[vector.model] += 1
                if vector.source_field:
                    sources[vector.source_field] += 1
            applicable = present + missing
            vectors.append(
                {
                    "field": vector_policy.name,
                    "path": vector_policy.path,
                    "policyDimensions": vector_policy.dimensions,
                    "datatype": vector_policy.datatype,
                    "distanceFunction": (
                        vector_policy.distance_function
                    ),
                    "indexType": vector_policy.index_type,
                    "recordCount": len(records),
                    "presentCount": present,
                    "missingCount": missing,
                    "notApplicableCount": not_applicable,
                    "emptyCount": empty,
                    "dimensionMismatchCount": mismatch,
                    "policyCoverage": (
                        1.0 if applicable == 0 else present / applicable
                    ),
                    "modelCounts": dict(sorted(models.items())),
                    "sourceFieldCounts": dict(sorted(sources.items())),
                }
            )
        for record in records:
            for field_name in (record.vectors or {}):
                if field_name not in declared:
                    extra[field_name] += 1
                    if len(anomalies) < limit:
                        anomalies.append(
                            {
                                "kind": "undeclaredVectorField",
                                "id": record.id,
                                "partitionKey": record.partition_key,
                                "type": record.type or None,
                                "field": field_name,
                                "message": (
                                    f"Record {record.id!r} carries vector "
                                    f"field {field_name!r} that is not "
                                    "declared by the collection policy."
                                ),
                                "details": {},
                            }
                        )
        chunk_with_vector = sum(
            1
            for record in rag_chunks
            if any(
                field_name in declared
                for field_name in (record.vectors or {})
            )
        )
        return {
            "collection": collection,
            "generatedAt": _date(datetime.now().astimezone()),
            "policy": policy.to_dict(),
            "recordCount": len(records),
            "partitionCount": len(partitions),
            "typeCounts": dict(sorted(type_counts.items())),
            "embeddingProviderCounts": {},
            "embeddingModelCounts": {},
            "rag": {
                "documentCount": len(document_ids),
                "chunkCount": len(rag_chunks),
                "manifestCount": len(rag_manifests),
                "chunkRecordsWithDocumentIdCount": sum(
                    1
                    for record in rag_chunks
                    if record.metadata is not None
                    and record.metadata.get("documentId") is not None
                ),
                "chunkRecordsWithVectorCount": chunk_with_vector,
                "chunkRecordsWithoutVectorCount": (
                    len(rag_chunks) - chunk_with_vector
                ),
            },
            "vectors": vectors,
            "extraVectorFieldCounts": dict(sorted(extra.items())),
            "anomalyCount": sum(extra.values()),
            "returnedAnomalyCount": len(anomalies),
            "anomalies": anomalies,
        }

    async def _start_task(
        self,
        name: str,
        arguments: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> ExecutionRun:
        idempotency_key = headers.get("idempotency-key") or headers.get(
            "x-idempotency-key"
        )
        scope = _scope(arguments)
        if name == "vyral_start_execution_run_v1":
            request = ExecutionRunRequest.from_value(
                _required_mapping(arguments, "request")
            )
            return await self.runtime.execution.start_run(request)
        handler_id = _TASK_HANDLERS.get(name)
        if handler_id is None:
            raise LookupError(f"Task tool {name!r} is not implemented.")
        payload: dict[str, JSONValue] = {
            "request": cast(
                dict[str, JSONValue],
                dict(_required_mapping(arguments, "request")),
            )
        }
        if "collection" in arguments:
            payload["collection"] = _required_text(
                arguments, "collection"
            )
        return await self.runtime.execution.start_run(
            ExecutionRunRequest(
                handler_id,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                payload=payload,
                idempotency_key=idempotency_key,
                scope=scope,
            )
        )

    async def _get_task(
        self,
        params: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        task_id = _required_text(params, "taskId")
        run = await self.runtime.execution.get_run(task_id)
        if run is None:
            raise LookupError(f"Task {task_id!r} was not found.")
        item = self._entry_for_handler(run.handler_id)
        await self._authorize(item, headers, {"taskId": task_id})
        if run.status in {"queued", "waiting"}:
            self._track_dispatch()
        input_requests: Mapping[str, JSONValue] | None = None
        if run.handler_id in {
            _CONFORMANCE_TASK_HANDLERS["confirm_delete"],
            _CONFORMANCE_TASK_HANDLERS["multi_input"],
        }:
            checkpoint = await self.runtime.execution.get_checkpoint(
                task_id, _CONFORMANCE_INPUT_CHECKPOINT
            )
            checkpoint_content = (
                checkpoint.content
                if checkpoint is not None
                and isinstance(checkpoint.content, Mapping)
                else {}
            )
            pending = checkpoint_content.get("pending")
            if isinstance(pending, Mapping) and pending:
                input_requests = cast(
                    Mapping[str, JSONValue], pending
                )
        result: dict[str, JSONValue] = {
            "resultType": "complete",
            "taskId": run.id,
            "status": (
                "input_required"
                if input_requests is not None
                else _task_status(run)
            ),
            "statusMessage": f"Execution run is {run.status}.",
            "createdAt": _date(run.created_at_utc),
            "lastUpdatedAt": _date(run.updated_at_utc),
            "ttlMs": self.config.task_ttl_ms,
            "pollIntervalMs": self.config.task_poll_interval_ms,
        }
        if input_requests is not None:
            result["inputRequests"] = dict(input_requests)
        if run.status == "succeeded":
            tool_error = (
                isinstance(run.result, Mapping)
                and run.result.get("_mcpToolError") is not None
            )
            structured_result = (
                {
                    "message": run.result.get("_mcpToolError")
                }
                if tool_error and isinstance(run.result, Mapping)
                else run.result
            )
            result["result"] = {
                "resultType": "complete",
                "content": [
                    {
                        "type": "text",
                        "text": json.dumps(
                            structured_result,
                            ensure_ascii=False,
                            separators=(",", ":"),
                        ),
                    }
                ],
                "structuredContent": structured_result,
                "isError": tool_error,
            }
        elif run.status in {"failed", "rejected", "timed_out"}:
            result["error"] = {
                "code": _INTERNAL_ERROR,
                "message": run.error or "Execution failed.",
                "data": {
                    "failureClass": run.failure_class,
                    "runId": run.id,
                },
            }
        return result

    async def _update_task(
        self,
        params: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        task_id = _required_text(params, "taskId")
        input_responses = params.get("inputResponses")
        if not isinstance(input_responses, Mapping):
            raise TypeError("inputResponses must be an object.")
        run = await self.runtime.execution.get_run(
            task_id, include_result=False
        )
        if run is None:
            raise LookupError(f"Task {task_id!r} was not found.")
        item = self._entry_for_handler(run.handler_id)
        await self._authorize(
            item,
            headers,
            {
                "taskId": task_id,
                "inputResponses": dict(input_responses),
            },
        )
        checkpoint = await self.runtime.execution.get_checkpoint(
            task_id, _CONFORMANCE_INPUT_CHECKPOINT
        )
        content = (
            checkpoint.content
            if checkpoint is not None
            and isinstance(checkpoint.content, Mapping)
            else None
        )
        if content is not None:
            pending_value = content.get("pending")
            responses_value = content.get("responses")
            pending = (
                dict(pending_value)
                if isinstance(pending_value, Mapping)
                else {}
            )
            responses = (
                dict(responses_value)
                if isinstance(responses_value, Mapping)
                else {}
            )
            accepted: list[tuple[str, JSONValue]] = []
            for key, response in input_responses.items():
                if key not in pending:
                    continue
                pending.pop(key)
                responses[str(key)] = cast(JSONValue, response)
                accepted.append((str(key), cast(JSONValue, response)))
            if accepted:
                await self.runtime.execution.put_run_checkpoint(
                    task_id,
                    ExecutionCheckpointWrite(
                        _CONFORMANCE_INPUT_CHECKPOINT,
                        {
                            "pending": pending,
                            "responses": responses,
                        },
                    ),
                )
                for key, response in accepted:
                    await self.runtime.execution.raise_event(
                        ExecutionExternalEventRequest(
                            f"mcp-input:{key}",
                            run_id=task_id,
                            payload=response,
                        )
                    )
                self._track_dispatch()
        # Native Vyral jobs do not otherwise enter input_required. The Tasks
        # extension directs servers to ignore responses for keys that are not
        # outstanding, so acknowledging unknown keys is intentional.
        return {"resultType": "complete"}

    async def _cancel_task(
        self,
        params: Mapping[str, Any],
        headers: Mapping[str, str],
    ) -> dict[str, JSONValue]:
        task_id = _required_text(params, "taskId")
        run = await self.runtime.execution.get_run(
            task_id, include_result=False
        )
        if run is None:
            raise LookupError(f"Task {task_id!r} was not found.")
        item = self._entry_for_handler(run.handler_id)
        await self._authorize(item, headers, {"taskId": task_id})
        await self.runtime.execution.cancel_run(task_id)
        return {"resultType": "complete"}

    async def _authorize(
        self,
        item: _CatalogEntry,
        headers: Mapping[str, str],
        arguments: Mapping[str, Any],
    ) -> None:
        if self.authorizer is not None:
            await self.authorizer.authorize(
                item.operation_id, headers, arguments
            )

    def _entry(
        self, mcp_id: str, exposure: str | None
    ) -> _CatalogEntry:
        item = next(
            (
                candidate
                for candidate in self._catalog
                if candidate.mcp_id == mcp_id
            ),
            None,
        )
        if (
            item is None
            or (exposure is not None and item.exposure != exposure)
            or not self._enabled(item)
            or not _implemented(item.mcp_id)
        ):
            raise LookupError(f"MCP capability {mcp_id!r} was not found.")
        return item

    def _entry_for_handler(
        self, handler_id: str
    ) -> _CatalogEntry:
        conformance_name = next(
            (
                name
                for name, selected in (
                    _CONFORMANCE_TASK_HANDLERS.items()
                )
                if selected == handler_id
            ),
            None,
        )
        if (
            self.config.enable_conformance_diagnostics
            and conformance_name is not None
        ):
            return _CatalogEntry(
                operation_id=handler_id,
                exposure="task",
                mcp_id=conformance_name,
                default_enabled=True,
                authorization_class="anonymous",
            )
        mcp_id = next(
            (
                tool_id
                for tool_id, selected_handler in _TASK_HANDLERS.items()
                if selected_handler == handler_id
            ),
            "vyral_start_execution_run_v1",
        )
        return self._entry(mcp_id, "task")

    def _enabled(self, item: _CatalogEntry) -> bool:
        if (
            item.operation_id in self.config.disabled_operation_ids
            or item.mcp_id in self.config.disabled_operation_ids
        ):
            return False
        return (
            item.default_enabled
            or item.operation_id in self.config.enabled_operation_ids
            or item.mcp_id in self.config.enabled_operation_ids
        )

    def _track_dispatch(self) -> None:
        task = asyncio.create_task(self._dispatch_once())
        self._dispatch_tasks.add(task)
        task.add_done_callback(self._dispatch_tasks.discard)

    async def startup(self) -> None:
        await self.runtime.execution.reconcile_dispatch(
            ExecutionMaintenanceDispatchReconcileRequest(
                dry_run=False,
                limit=self.runtime.execution.options.max_active_runs,
            )
        )

    async def shutdown(self) -> None:
        if self._dispatch_tasks:
            for task in self._dispatch_tasks:
                task.cancel()
            await asyncio.gather(
                *self._dispatch_tasks,
                return_exceptions=True,
            )

    async def _dispatch_once(self) -> None:
        await self.runtime.execution.dispatch_ready_runs(
            recover_interrupted_runs=True
        )

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

    async def _error(
        self,
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
        status: int,
        request_id: object,
        code: int,
        message: str,
        data: Mapping[str, JSONValue] | None = None,
    ) -> None:
        error: dict[str, JSONValue] = {
            "code": code,
            "message": message,
        }
        if data is not None:
            error["data"] = dict(data)
        await _send_json(
            send,
            status,
            {
                "jsonrpc": "2.0",
                "id": cast(JSONValue, request_id),
                "error": error,
            },
        )


class _BodyTooLarge(Exception):
    pass


class _McpError(Exception):
    def __init__(
        self,
        http_status: int,
        code: int,
        message: str,
        data: Mapping[str, JSONValue] | None = None,
    ) -> None:
        super().__init__(message)
        self.http_status = http_status
        self.code = code
        self.message = message
        self.data = data


def _validate_request(
    message: Mapping[str, Any],
    headers: Mapping[str, str],
) -> tuple[str, Mapping[str, Any], Mapping[str, Any]]:
    if message.get("jsonrpc") != "2.0":
        raise _McpError(
            400, _INVALID_REQUEST, "jsonrpc must be '2.0'."
        )
    method = message.get("method")
    if not isinstance(method, str) or not method:
        raise _McpError(400, _INVALID_REQUEST, "method is required.")
    params_value = message.get("params", {})
    if not isinstance(params_value, Mapping):
        raise _McpError(
            400, _INVALID_PARAMS, "params must be an object."
        )
    params = cast(Mapping[str, Any], params_value)
    meta_value = params.get("_meta")
    if not isinstance(meta_value, Mapping):
        raise _McpError(
            400, _INVALID_PARAMS, "params._meta is required."
        )
    meta = cast(Mapping[str, Any], meta_value)
    version_header = headers.get("mcp-protocol-version")
    method_header = headers.get("mcp-method")
    if version_header is None or method_header is None:
        raise _McpError(
            400,
            _HEADER_MISMATCH,
            "Header mismatch: required MCP routing headers are missing.",
        )
    body_version = meta.get(
        "io.modelcontextprotocol/protocolVersion"
    )
    if not isinstance(body_version, str) or not body_version:
        raise _McpError(
            400,
            _INVALID_PARAMS,
            "Required per-request client metadata is malformed.",
        )
    if version_header != MCP_PROTOCOL_VERSION:
        raise _McpError(
            400,
            _UNSUPPORTED_VERSION,
            "Unsupported protocol version",
            {
                "supported": [MCP_PROTOCOL_VERSION],
                "requested": version_header,
            },
        )
    if body_version != version_header or method_header != method:
        raise _McpError(
            400,
            _HEADER_MISMATCH,
            "Header mismatch: MCP headers do not match the request body.",
        )
    client_info = meta.get("io.modelcontextprotocol/clientInfo")
    client_capabilities = meta.get(
        "io.modelcontextprotocol/clientCapabilities"
    )
    client_info_valid = client_info is None or (
        isinstance(client_info, Mapping)
        and isinstance(client_info.get("name"), str)
        and isinstance(client_info.get("version"), str)
    )
    if not client_info_valid or not isinstance(
        client_capabilities, Mapping
    ):
        raise _McpError(
            400,
            _INVALID_PARAMS,
            "Required per-request client metadata is malformed.",
        )
    name_source: object | None = None
    if method == "tools/call":
        name_source = params.get("name")
    elif method == "resources/read":
        name_source = params.get("uri")
    elif method in {"tasks/get", "tasks/update", "tasks/cancel"}:
        name_source = params.get("taskId")
    if name_source is not None:
        if not isinstance(name_source, str):
            raise _McpError(
                400, _INVALID_PARAMS, "MCP name must be a string."
            )
        name_header = headers.get("mcp-name")
        if name_header is None:
            raise _McpError(
                400,
                _HEADER_MISMATCH,
                "Header mismatch: Mcp-Name is required.",
            )
        try:
            decoded = _decode_header_value(name_header)
        except ValueError as error:
            raise _McpError(
                400,
                _HEADER_MISMATCH,
                "Header mismatch: Mcp-Name is invalid.",
            ) from error
        if decoded != name_source:
            raise _McpError(
                400,
                _HEADER_MISMATCH,
                "Header mismatch: Mcp-Name does not match the body.",
            )
    return method, params, meta


def _decode_header_value(value: str) -> str:
    prefix = "=?base64?"
    suffix = "?="
    if not (
        value.startswith(prefix) and value.endswith(suffix)
    ):
        if not value.isascii():
            raise ValueError(
                "Header mismatch: MCP routing headers must be ASCII."
            )
        return value
    encoded = value[len(prefix) : -len(suffix)]
    try:
        return base64.b64decode(
            encoded, validate=True
        ).decode("utf-8")
    except (binascii.Error, ValueError, UnicodeDecodeError) as error:
        raise ValueError(
            "Header mismatch: malformed Base64 MCP header."
        ) from error


def _validate_conformance_headers(
    method: str,
    params: Mapping[str, Any],
    headers: Mapping[str, str],
) -> None:
    if (
        method != "tools/call"
        or params.get("name") != "test_custom_header"
    ):
        return
    arguments = params.get("arguments")
    body_value = (
        arguments.get("value")
        if isinstance(arguments, Mapping)
        else None
    )
    if not isinstance(body_value, str):
        raise _McpError(
            400, _INVALID_PARAMS, "value must be a string."
        )
    header_value = headers.get("mcp-param-value")
    if header_value is None:
        raise _McpError(
            400,
            _HEADER_MISMATCH,
            "Header mismatch: Mcp-Param-Value is required.",
        )
    try:
        decoded = _decode_header_value(header_value)
    except ValueError as error:
        raise _McpError(
            400,
            _HEADER_MISMATCH,
            "Header mismatch: Mcp-Param-Value is invalid.",
        ) from error
    if decoded != body_value:
        raise _McpError(
            400,
            _HEADER_MISMATCH,
            "Header mismatch: Mcp-Param-Value does not match the body.",
        )


def _conformance_progress_token(
    method: str,
    params: Mapping[str, Any],
    meta: Mapping[str, Any],
    enabled: bool,
) -> JSONValue | None:
    if (
        not enabled
        or method != "tools/call"
        or params.get("name") != "test_tool_with_progress"
    ):
        return None
    token = meta.get("progressToken")
    if isinstance(token, (str, int)) and not isinstance(token, bool):
        return cast(JSONValue, token)
    return None


def _request_origin_allowed(
    headers: Mapping[str, str],
    allowed_origins: frozenset[str],
    allowed_hosts: frozenset[str],
    require_explicit_origins: bool = False,
) -> bool:
    host = headers.get("host")
    if host is not None and not _host_allowed(host, allowed_hosts):
        return False
    origin = headers.get("origin")
    if origin is None:
        return True
    if origin in allowed_origins:
        return True
    if require_explicit_origins:
        return False
    try:
        parsed = urlsplit(origin)
    except ValueError:
        return False
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        return False
    if host is None or parsed.netloc.lower() != host.lower():
        return False
    return _host_allowed(parsed.netloc, allowed_hosts)


def _host_allowed(
    host: str, allowed_hosts: frozenset[str]
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


def _require_task_capability(meta: Mapping[str, Any]) -> None:
    if _has_task_capability(meta):
        return
    raise _McpError(
        400,
        _MISSING_CAPABILITY,
        "The request requires the MCP Tasks extension.",
        {
            "requiredCapabilities": {
                "extensions": {_TASK_CAPABILITY: {}}
            }
        },
    )


def _has_task_capability(meta: Mapping[str, Any]) -> bool:
    capabilities = meta.get(
        "io.modelcontextprotocol/clientCapabilities"
    )
    extensions = (
        capabilities.get("extensions")
        if isinstance(capabilities, Mapping)
        else None
    )
    return (
        isinstance(extensions, Mapping)
        and _TASK_CAPABILITY in extensions
    )


def _conformance_task_descriptors() -> list[JSONValue]:
    return [
        {
            "name": "greet",
            "description": "Synchronous MCP Tasks conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {"name": {"type": "string"}},
                "additionalProperties": False,
            },
        },
        {
            "name": "slow_compute",
            "description": "Durable MCP Tasks conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {
                    "seconds": {"type": "number", "minimum": 0},
                    "label": {"type": "string"},
                },
                "additionalProperties": True,
            },
        },
        {
            "name": "failing_job",
            "description": "Tool-error MCP Tasks conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {},
                "additionalProperties": False,
            },
        },
        {
            "name": "protocol_error_job",
            "description": "Protocol-error MCP Tasks conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {},
                "additionalProperties": False,
            },
        },
        {
            "name": "confirm_delete",
            "description": "MCP Tasks update-ack conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {"filename": {"type": "string"}},
                "additionalProperties": True,
            },
        },
        {
            "name": "multi_input",
            "description": "Multi-input MCP Tasks conformance fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {},
                "additionalProperties": False,
            },
        },
        {
            "name": "test_tool_with_task",
            "description": "MRTR-to-task composition fixture.",
            "inputSchema": {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "properties": {},
                "additionalProperties": False,
            },
        },
    ]


def _conformance_tool_descriptor(
    name: str,
    description: str,
    schema: Mapping[str, JSONValue] | None = None,
) -> dict[str, JSONValue]:
    return {
        "name": name,
        "description": description,
        "inputSchema": dict(schema or _schema()),
    }


def _core_conformance_tool_descriptors() -> list[JSONValue]:
    descriptions = {
        "test_simple_text": "Returns the MCP text fixture.",
        "test_image_content": "Returns the MCP image fixture.",
        "test_audio_content": "Returns the MCP audio fixture.",
        "test_embedded_resource": (
            "Returns the MCP embedded-resource fixture."
        ),
        "test_multiple_content_types": (
            "Returns the MCP mixed-content fixture."
        ),
        "test_error_handling": "Returns the MCP tool-error fixture.",
        "test_tool_with_progress": (
            "Emits the bounded MCP progress fixture."
        ),
    }
    result: list[JSONValue] = [
        _conformance_tool_descriptor(name, description)
        for name, description in descriptions.items()
    ]
    result.append(
        _conformance_tool_descriptor(
            "json_schema_2020_12_tool",
            "Tool with JSON Schema 2020-12 features.",
            {
                "$schema": (
                    "https://json-schema.org/draft/2020-12/schema"
                ),
                "type": "object",
                "$defs": {
                    "address": {
                        "$anchor": "addressDef",
                        "type": "object",
                        "properties": {
                            "street": {"type": "string"},
                            "city": {"type": "string"},
                        },
                    }
                },
                "properties": {
                    "name": {"type": "string"},
                    "address": {"$ref": "#/$defs/address"},
                    "contactMethod": {
                        "type": "string",
                        "enum": ["phone", "email"],
                    },
                    "phone": {"type": "string"},
                    "email": {"type": "string"},
                },
                "allOf": [
                    {
                        "anyOf": [
                            {"required": ["phone"]},
                            {"required": ["email"]},
                        ]
                    }
                ],
                "if": {
                    "properties": {
                        "contactMethod": {"const": "phone"}
                    },
                    "required": ["contactMethod"],
                },
                "then": {"required": ["phone"]},
                "else": {"required": ["email"]},
                "additionalProperties": False,
            },
        )
    )
    return result


def _mrtr_conformance_tool_descriptors() -> list[JSONValue]:
    return [
        _conformance_tool_descriptor(
            name,
            "MCP MRTR conformance diagnostic; never enabled in "
            "ordinary Vyral deployments.",
        )
        for name in sorted(_MRTR_CONFORMANCE_TOOLS)
    ]


def _conformance_resource_descriptors() -> list[JSONValue]:
    return [
        {
            "uri": "test://static-text",
            "name": "Static Text Resource",
            "description": "MCP conformance text fixture.",
            "mimeType": "text/plain",
        },
        {
            "uri": "test://static-binary",
            "name": "Static Binary Resource",
            "description": "MCP conformance binary fixture.",
            "mimeType": "image/png",
        },
    ]


def _conformance_prompt_descriptors() -> list[JSONValue]:
    return [
        {
            "name": "test_simple_prompt",
            "description": "MCP conformance prompt fixture.",
        },
        {
            "name": "test_prompt_with_arguments",
            "description": "MCP conformance argument prompt fixture.",
            "arguments": [
                {
                    "name": "arg1",
                    "description": "First test argument.",
                    "required": True,
                },
                {
                    "name": "arg2",
                    "description": "Second test argument.",
                    "required": True,
                },
            ],
        },
        {
            "name": "test_prompt_with_embedded_resource",
            "description": "MCP conformance resource prompt fixture.",
            "arguments": [
                {
                    "name": "resourceUri",
                    "description": "Resource URI to embed.",
                    "required": True,
                }
            ],
        },
        {
            "name": "test_prompt_with_image",
            "description": "MCP conformance image prompt fixture.",
        },
        {
            "name": "test_input_required_result_prompt",
            "description": "MCP MRTR prompt fixture.",
        },
    ]


def _is_conformance_resource(uri: str) -> bool:
    return uri in _CONFORMANCE_STATIC_RESOURCES or (
        uri.startswith("test://template/") and uri.endswith("/data")
    )


def _read_conformance_resource(
    uri: str, ttl_ms: int
) -> dict[str, JSONValue]:
    if uri == "test://static-text":
        content: dict[str, JSONValue] = {
            "uri": uri,
            "mimeType": "text/plain",
            "text": "This is the content of the static text resource.",
        }
    elif uri == "test://static-binary":
        content = {
            "uri": uri,
            "mimeType": "image/png",
            "blob": _CONFORMANCE_IMAGE_BASE64,
        }
    else:
        identifier = uri[len("test://template/") : -len("/data")]
        if not identifier:
            raise LookupError(f"Resource {uri!r} was not found.")
        content = {
            "uri": uri,
            "mimeType": "application/json",
            "text": json.dumps(
                {
                    "id": identifier,
                    "templateTest": True,
                    "data": f"Data for ID: {identifier}",
                },
                separators=(",", ":"),
            ),
        }
    return {
        "resultType": "complete",
        "contents": [content],
        "ttlMs": ttl_ms,
        "cacheScope": "private",
    }


def _prompt_text(text: str) -> dict[str, JSONValue]:
    return {
        "role": "user",
        "content": {"type": "text", "text": text},
    }


def _complete_text(text: str) -> dict[str, JSONValue]:
    return {
        "resultType": "complete",
        "content": [{"type": "text", "text": text}],
        "isError": False,
    }


def _call_core_conformance_tool(
    name: str, meta: Mapping[str, Any]
) -> dict[str, JSONValue]:
    del meta
    if name == "test_simple_text":
        return _complete_text(
            "This is a simple text response for testing."
        )
    if name == "test_image_content":
        content: list[JSONValue] = [
            {
                "type": "image",
                "data": _CONFORMANCE_IMAGE_BASE64,
                "mimeType": "image/png",
            }
        ]
    elif name == "test_audio_content":
        content = [
            {
                "type": "audio",
                "data": _CONFORMANCE_AUDIO_BASE64,
                "mimeType": "audio/wav",
            }
        ]
    elif name == "test_embedded_resource":
        content = [
            {
                "type": "resource",
                "resource": {
                    "uri": "test://embedded-resource",
                    "mimeType": "text/plain",
                    "text": "This is an embedded resource content.",
                },
            }
        ]
    elif name == "test_multiple_content_types":
        content = [
            {
                "type": "text",
                "text": "Multiple content types test:",
            },
            {
                "type": "image",
                "data": _CONFORMANCE_IMAGE_BASE64,
                "mimeType": "image/png",
            },
            {
                "type": "resource",
                "resource": {
                    "uri": "test://mixed-content-resource",
                    "mimeType": "application/json",
                    "text": '{"test":"data","value":123}',
                },
            },
        ]
    elif name == "test_error_handling":
        return {
            "resultType": "complete",
            "content": [
                {
                    "type": "text",
                    "text": (
                        "This tool intentionally returns an error "
                        "for testing."
                    ),
                }
            ],
            "isError": True,
        }
    else:
        return _complete_text("conformance diagnostic complete")
    return {
        "resultType": "complete",
        "content": content,
        "isError": False,
    }


def _elicitation_input(
    message: str,
    property_name: str,
    type_name: str = "string",
) -> dict[str, JSONValue]:
    return {
        "method": "elicitation/create",
        "params": {
            "message": message,
            "mode": "form",
            "requestedSchema": {
                "type": "object",
                "properties": {
                    property_name: {"type": type_name}
                },
                "required": [property_name],
            },
        },
    }


def _sampling_input(prompt: str) -> dict[str, JSONValue]:
    return {
        "method": "sampling/createMessage",
        "params": {
            "messages": [
                {
                    "role": "user",
                    "content": {"type": "text", "text": prompt},
                }
            ],
            "maxTokens": 100,
        },
    }


def _roots_input() -> dict[str, JSONValue]:
    return {"method": "roots/list", "params": {}}


def _input_required(
    requests: Mapping[str, JSONValue],
    request_state: str | None = None,
) -> dict[str, JSONValue]:
    result: dict[str, JSONValue] = {
        "resultType": "input_required",
        "inputRequests": dict(requests),
    }
    if request_state is not None:
        result["requestState"] = request_state
    return result


def _input_responses(
    params: Mapping[str, Any]
) -> Mapping[str, Any] | None:
    value = params.get("inputResponses")
    if value is None:
        return None
    if not isinstance(value, Mapping):
        raise TypeError("inputResponses must be an object.")
    return cast(Mapping[str, Any], value)


def _elicited_value(
    responses: Mapping[str, Any], key: str, property_name: str
) -> str:
    response = responses.get(key)
    if not isinstance(response, Mapping):
        raise TypeError(f"Input response {key!r} must be an object.")
    content = response.get("content")
    if not isinstance(content, Mapping):
        raise TypeError(f"Input response {key!r} requires content.")
    value = content.get(property_name)
    return str(value) if value is not None else "(unknown)"


def _sampling_text(
    responses: Mapping[str, Any], key: str
) -> str:
    response = responses.get(key)
    if not isinstance(response, Mapping):
        raise TypeError(f"Input response {key!r} must be an object.")
    content = response.get("content")
    if isinstance(content, Mapping):
        text = content.get("text")
        return str(text) if text is not None else "(no text)"
    if isinstance(content, list):
        for item in content:
            if isinstance(item, Mapping) and item.get("type") == "text":
                text = item.get("text")
                return str(text) if text is not None else "(no text)"
    return "(no text)"


def _signed_request_state() -> str:
    nonce = uuid.uuid4().hex
    digest = hashlib.sha256(
        f"mrtr-conformance-state-v1:{nonce}".encode("utf-8")
    ).hexdigest().upper()
    return f"{nonce}.{digest}"


def _valid_request_state(state: str) -> bool:
    nonce, separator, received = state.rpartition(".")
    if not separator or not nonce or not received:
        return False
    expected = hashlib.sha256(
        f"mrtr-conformance-state-v1:{nonce}".encode("utf-8")
    ).hexdigest().upper()
    return hmac.compare_digest(received, expected)


def _call_mrtr_conformance_tool(
    name: str,
    params: Mapping[str, Any],
    meta: Mapping[str, Any],
) -> dict[str, JSONValue]:
    responses = _input_responses(params)
    state_value = params.get("requestState")
    state = state_value if isinstance(state_value, str) else None
    if name in {
        "test_input_required_result_elicitation",
        "test_incomplete_result_elicitation",
    }:
        if responses is None or "user_name" not in responses:
            return _input_required(
                {
                    "user_name": _elicitation_input(
                        "What is your name?", "name"
                    )
                }
            )
        return _complete_text(
            f"Hello, {_elicited_value(responses, 'user_name', 'name')}!"
        )
    if name == "test_input_required_result_sampling":
        if responses is None or "capital_question" not in responses:
            return _input_required(
                {
                    "capital_question": _sampling_input(
                        "What is the capital of France?"
                    )
                }
            )
        return _complete_text(
            "Sampling said: "
            + _sampling_text(responses, "capital_question")
        )
    if name == "test_input_required_result_list_roots":
        if responses is None or "client_roots" not in responses:
            return _input_required({"client_roots": _roots_input()})
        response = responses.get("client_roots")
        roots = (
            response.get("roots")
            if isinstance(response, Mapping)
            else None
        )
        count = len(roots) if isinstance(roots, list) else 0
        return _complete_text(f"Got {count} root(s) from the client.")
    if name == "test_input_required_result_request_state":
        if state is not None:
            return _complete_text(
                "state-ok"
                if state == "mrtr-conformance-state-v1"
                else "state-mismatch"
            )
        return _input_required(
            {
                "confirm": _elicitation_input(
                    "Please confirm", "ok", "boolean"
                )
            },
            "mrtr-conformance-state-v1",
        )
    if name == "test_input_required_result_multiple_inputs":
        if responses is not None and len(responses) >= 3:
            return _complete_text("multiple-inputs-ok")
        return _input_required(
            {
                "user_name": _elicitation_input(
                    "What is your name?", "name"
                ),
                "greeting": _sampling_input("Generate a greeting"),
                "client_roots": _roots_input(),
            },
            "multi-input-state",
        )
    if name == "test_input_required_result_multi_round":
        if state is None:
            return _input_required(
                {
                    "step1": _elicitation_input(
                        "Step 1: What is your name?", "name"
                    )
                },
                "round-1",
            )
        if state == "round-1":
            return _input_required(
                {
                    "step2": _elicitation_input(
                        "Step 2: What is your favorite color?",
                        "color",
                    )
                },
                "round-2",
            )
        return _complete_text("multi-round-ok")
    if name == "test_input_required_result_tampered_state":
        if state is not None:
            if not _valid_request_state(state):
                raise _McpError(
                    400,
                    _INVALID_PARAMS,
                    "requestState failed integrity verification.",
                )
            return _complete_text("tampered-state-ok")
        return _input_required(
            {
                "confirm": _elicitation_input(
                    "Please confirm", "ok", "boolean"
                )
            },
            _signed_request_state(),
        )
    if responses is not None and responses:
        return _complete_text("capability-check-ok")
    capabilities = meta.get(
        "io.modelcontextprotocol/clientCapabilities"
    )
    requests: dict[str, JSONValue] = {}
    if isinstance(capabilities, Mapping):
        if isinstance(capabilities.get("sampling"), Mapping):
            requests["capital_question"] = _sampling_input(
                "What is the capital of France?"
            )
        if isinstance(capabilities.get("elicitation"), Mapping):
            requests["user_name"] = _elicitation_input(
                "What is your name?", "name"
            )
        if isinstance(capabilities.get("roots"), Mapping):
            requests["client_roots"] = _roots_input()
    if not requests:
        return _complete_text(
            "capability-check-ok: no MRTR capabilities"
        )
    return _input_required(requests)


async def _conformance_slow_compute(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    payload = context.run.payload
    seconds_value = (
        payload.get("seconds")
        if isinstance(payload, Mapping)
        else 0
    )
    seconds = (
        float(seconds_value)
        if isinstance(seconds_value, (int, float))
        and not isinstance(seconds_value, bool)
        else 0.0
    )
    remaining = max(0.0, min(seconds, 60.0))
    loop = asyncio.get_running_loop()
    deadline = loop.time() + remaining
    while loop.time() < deadline:
        if context.cancellation_requested:
            return ExecutionRunResult.cancelled_result()
        await asyncio.sleep(min(0.05, deadline - loop.time()))
    if context.cancellation_requested:
        return ExecutionRunResult.cancelled_result()
    label = (
        payload.get("label")
        if isinstance(payload, Mapping)
        else None
    )
    return ExecutionRunResult.succeeded_result(
        {"label": label, "seconds": seconds}
    )


async def _conformance_tool_error(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    del context
    await asyncio.sleep(0.05)
    return ExecutionRunResult.succeeded_result(
        {"_mcpToolError": "Intentional conformance tool error."}
    )


async def _conformance_protocol_error(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    del context
    await asyncio.sleep(0.05)
    raise RuntimeError("Intentional conformance protocol error.")


async def _conformance_input_task(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    checkpoint = await context.get_checkpoint(
        _CONFORMANCE_INPUT_CHECKPOINT
    )
    if checkpoint is None:
        if context.run.handler_id == (
            _CONFORMANCE_TASK_HANDLERS["multi_input"]
        ):
            pending: dict[str, JSONValue] = {
                "first": _elicitation_request(
                    "Provide the first response.", "name"
                ),
                "second": _elicitation_request(
                    "Provide the second response.", "confirm"
                ),
            }
        else:
            pending = {
                "confirm": _elicitation_request(
                    "Confirm the requested deletion.", "confirm"
                )
            }
        responses: dict[str, JSONValue] = {}
        await context.put_checkpoint(
            ExecutionCheckpointWrite(
                _CONFORMANCE_INPUT_CHECKPOINT,
                {
                    "pending": pending,
                    "responses": responses,
                },
            )
        )
    else:
        content = (
            checkpoint.content
            if isinstance(checkpoint.content, Mapping)
            else {}
        )
        raw_pending = content.get("pending")
        raw_responses = content.get("responses")
        pending = (
            cast(dict[str, JSONValue], dict(raw_pending))
            if isinstance(raw_pending, Mapping)
            else {}
        )
        responses = (
            cast(dict[str, JSONValue], dict(raw_responses))
            if isinstance(raw_responses, Mapping)
            else {}
        )
    if pending:
        key = next(iter(pending))
        await context.wait_for_external_event(f"mcp-input:{key}")
    return ExecutionRunResult.succeeded_result(
        {"responses": responses}
    )


async def _conformance_composed_task(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    payload = context.run.payload
    name = (
        payload.get("name")
        if isinstance(payload, Mapping)
        else None
    )
    return ExecutionRunResult.succeeded_result(
        {"message": f"Hello, {name}!"}
    )


def _elicitation_request(
    message: str, property_name: str
) -> dict[str, JSONValue]:
    property_schema: dict[str, JSONValue] = (
        {"type": "boolean"}
        if property_name == "confirm"
        else {"type": "string"}
    )
    return {
        "method": "elicitation/create",
        "params": {
            "message": message,
            "mode": "form",
            "requestedSchema": {
                "type": "object",
                "properties": {
                    property_name: property_schema
                },
                "required": [property_name],
            },
        },
    }


def _load_catalog(runtime: VyralRuntime) -> tuple[_CatalogEntry, ...]:
    operations = runtime.contracts.catalog.get("operations")
    if not isinstance(operations, list):
        raise ValueError("The Vyral operation catalog is malformed.")
    entries: list[_CatalogEntry] = []
    for value in operations:
        if not isinstance(value, Mapping):
            continue
        mcp = value.get("mcp")
        if not isinstance(mcp, Mapping) or mcp.get("exposure") == "none":
            continue
        entries.append(
            _CatalogEntry(
                operation_id=str(value["id"]),
                exposure=str(mcp["exposure"]),
                mcp_id=str(mcp["id"]),
                default_enabled=bool(mcp["defaultEnabled"]),
                authorization_class=str(value["authorizationClass"]),
            )
        )
    return tuple(entries)


def _implemented(mcp_id: str) -> bool:
    return (
        mcp_id in _RESOURCE_DESCRIPTORS
        or mcp_id in _TOOL_SCHEMAS
        or mcp_id in _TASK_HANDLERS
        or mcp_id == "vyral_start_execution_run_v1"
    )


def _scope(arguments: Mapping[str, Any]) -> ExecutionScope | None:
    product = arguments.get("productId")
    tenant = arguments.get("tenantId")
    if product is None and tenant is None:
        return None
    if (
        not isinstance(product, str)
        or not product.strip()
        or not isinstance(tenant, str)
        or not tenant.strip()
    ):
        raise TypeError(
            "Execution scope requires both productId and tenantId."
        )
    return ExecutionScope(product.strip(), tenant.strip())


def _task_status(run: ExecutionRun) -> str:
    return {
        "succeeded": "completed",
        "failed": "failed",
        "rejected": "failed",
        "timed_out": "failed",
        "cancelled": "cancelled",
    }.get(run.status, "working")


def _date(value: datetime) -> str:
    return value.isoformat().replace("+00:00", "Z")


def _wire(value: object) -> object:
    if hasattr(value, "to_dict"):
        return _wire(value.to_dict())
    if is_dataclass(value) and not isinstance(value, type):
        return {
            field.name: _wire(getattr(value, field.name))
            for field in fields(value)
        }
    if isinstance(value, datetime):
        return _date(value)
    if isinstance(value, Mapping):
        return {str(key): _wire(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    return value


def _headers(
    raw: list[tuple[bytes, bytes]],
) -> dict[str, str]:
    result: dict[str, str] = {}
    for key, value in raw:
        try:
            name = key.decode("ascii").lower()
            selected = value.decode("latin-1").strip(" \t")
        except UnicodeDecodeError:
            continue
        if name in result:
            result[name] += "," + selected
        else:
            result[name] = selected
    return result


async def _read_body(
    receive: Callable[[], Awaitable[Mapping[str, Any]]],
    maximum: int,
) -> bytes:
    chunks: list[bytes] = []
    size = 0
    while True:
        message = await receive()
        if message.get("type") == "http.disconnect":
            return b""
        chunk = message.get("body", b"")
        if not isinstance(chunk, bytes):
            chunk = b""
        size += len(chunk)
        if size > maximum:
            raise _BodyTooLarge()
        chunks.append(chunk)
        if not message.get("more_body", False):
            return b"".join(chunks)


async def _send_json(
    send: Callable[[Mapping[str, Any]], Awaitable[None]],
    status: int,
    value: object,
    *,
    extra_headers: tuple[tuple[bytes, bytes], ...] = (),
) -> None:
    body = json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")
    await send(
        {
            "type": "http.response.start",
            "status": status,
            "headers": [
                (b"content-type", b"application/json"),
                (b"content-length", str(len(body)).encode("ascii")),
                *extra_headers,
            ],
        }
    )
    await send(
        {"type": "http.response.body", "body": body}
    )


async def _send_sse_json_rpc(
    send: Callable[[Mapping[str, Any]], Awaitable[None]],
    messages: list[dict[str, JSONValue]],
) -> None:
    await send(
        {
            "type": "http.response.start",
            "status": 200,
            "headers": [
                (b"content-type", b"text/event-stream"),
                (b"cache-control", b"no-cache"),
            ],
        }
    )
    for index, message in enumerate(messages):
        payload = json.dumps(
            message,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        await send(
            {
                "type": "http.response.body",
                "body": b"event: message\ndata: " + payload + b"\n\n",
                "more_body": index < len(messages) - 1,
            }
        )


async def _send_empty(
    send: Callable[[Mapping[str, Any]], Awaitable[None]],
    status: int,
) -> None:
    await send(
        {
            "type": "http.response.start",
            "status": status,
            "headers": [(b"content-length", b"0")],
        }
    )
    await send({"type": "http.response.body", "body": b""})


def _required_text(value: Mapping[str, Any], name: str) -> str:
    selected = value.get(name)
    if not isinstance(selected, str) or not selected.strip():
        raise TypeError(f"{name} must be a non-empty string.")
    return selected.strip()


def _required_mapping(
    value: Mapping[str, Any], name: str
) -> Mapping[str, Any]:
    selected = value.get(name)
    if not isinstance(selected, Mapping):
        raise TypeError(f"{name} must be an object.")
    return cast(Mapping[str, Any], selected)


def _optional_bool(
    value: Mapping[str, Any], name: str, fallback: bool
) -> bool:
    selected = value.get(name)
    if selected is None:
        return fallback
    if not isinstance(selected, bool):
        raise TypeError(f"{name} must be a boolean.")
    return selected


def _optional_int(
    value: Mapping[str, Any], name: str
) -> int | None:
    selected = value.get(name)
    if selected is None:
        return None
    if isinstance(selected, bool) or not isinstance(selected, int):
        raise TypeError(f"{name} must be an integer.")
    return selected


_RESOURCE_DESCRIPTORS: dict[str, dict[str, JSONValue]] = {
    "vyral://health/v1": {
        "uri": "vyral://health/v1",
        "name": "vyral_health_v1",
        "title": "Vyral health",
        "description": "A small non-secret Vyral health summary.",
        "mimeType": "application/json",
    },
    "vyral://readiness/v1": {
        "uri": "vyral://readiness/v1",
        "name": "vyral_readiness_v1",
        "title": "Vyral readiness",
        "description": "Bounded local runtime readiness.",
        "mimeType": "application/json",
    },
    "vyral://open_api_contract/v1": {
        "uri": "vyral://open_api_contract/v1",
        "name": "vyral_open_api_contract_v1",
        "title": "Vyral OpenAPI contract",
        "description": "The authoritative OpenAPI 3.1 contract.",
        "mimeType": "application/json",
    },
    "vyral://public_schema_contract/v1": {
        "uri": "vyral://public_schema_contract/v1",
        "name": "vyral_public_schema_contract_v1",
        "title": "Vyral public JSON Schema contract",
        "description": "The canonical JSON Schema 2020-12 bundle.",
        "mimeType": "application/schema+json",
    },
}


def _schema(
    required: tuple[str, ...] = (),
    *,
    properties: Mapping[str, JSONValue] | None = None,
) -> dict[str, JSONValue]:
    result: dict[str, JSONValue] = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "properties": dict(properties or {}),
        "additionalProperties": False,
    }
    if required:
        result["required"] = list(required)
    return result


_TEXT: dict[str, JSONValue] = {
    "type": "string",
    "minLength": 1,
}
_OBJECT: dict[str, JSONValue] = {"type": "object"}
_TOOL_SCHEMAS: dict[str, dict[str, JSONValue]] = {
    "vyral_list_graph_provider_shapes_v1": _schema(),
    "vyral_get_graph_provider_shape_v1": _schema(
        ("providerId",), properties={"providerId": _TEXT}
    ),
    "vyral_list_collections_v1": _schema(),
    "vyral_get_collection_v1": _schema(
        ("collection",), properties={"collection": _TEXT}
    ),
    "vyral_inspect_collection_v1": _schema(
        ("collection",),
        properties={
            "collection": _TEXT,
            "includeAnomalies": {"type": "boolean"},
            "anomalyLimit": {
                "type": "integer",
                "minimum": 1,
                "maximum": 500,
            },
        },
    ),
    "vyral_get_record_v1": _schema(
        ("collection", "partitionKey", "id"),
        properties={
            "collection": _TEXT,
            "partitionKey": _TEXT,
            "id": _TEXT,
        },
    ),
    "vyral_query_records_v1": _schema(
        ("collection", "query"),
        properties={"collection": _TEXT, "query": _OBJECT},
    ),
    "vyral_search_records_v1": _schema(
        ("collection", "query"),
        properties={"collection": _TEXT, "query": _OBJECT},
    ),
    "vyral_list_retrieval_profiles_v1": _schema(),
    "vyral_retrieve_v1": _schema(
        ("request",), properties={"request": _OBJECT}
    ),
    "vyral_build_rag_context_v1": _schema(
        ("request",), properties={"request": _OBJECT}
    ),
    "vyral_build_rag_prompt_v1": _schema(
        ("request",), properties={"request": _OBJECT}
    ),
    "vyral_traverse_graph_v1": _schema(
        ("collection", "request"),
        properties={"collection": _TEXT, "request": _OBJECT},
    ),
    "vyral_inspect_graph_collection_v1": _schema(
        ("collection", "request"),
        properties={"collection": _TEXT, "request": _OBJECT},
    ),
    "vyral_doctor_graph_collection_v1": _schema(
        ("collection", "request"),
        properties={"collection": _TEXT, "request": _OBJECT},
    ),
    "vyral_get_execution_run_v1": _schema(
        ("runId",),
        properties={
            "runId": _TEXT,
            "includeResult": {"type": "boolean"},
        },
    ),
    "vyral_get_execution_run_history_v1": _schema(
        ("runId",),
        properties={
            "runId": _TEXT,
            "limit": {"type": "integer", "minimum": 1},
        },
    ),
    "vyral_start_execution_run_v1": _schema(
        ("request",), properties={"request": _OBJECT}
    ),
}

for _task_tool in (
    "vyral_start_embedding_job_v1",
    "vyral_start_retrieval_evaluation_comparison_job_v1",
    "vyral_start_retrieval_evaluation_job_v1",
):
    _TOOL_SCHEMAS[_task_tool] = _schema(
        ("request",), properties={"request": _OBJECT}
    )
for _task_tool in (
    "vyral_start_collection_import_job_v1",
    "vyral_start_record_batch_upsert_job_v1",
    "vyral_start_rag_ingestion_text_job_v1",
    "vyral_start_rag_ingestion_batch_job_v1",
    "vyral_start_graph_import_job_v1",
    "vyral_start_graph_inspection_job_v1",
    "vyral_start_graph_doctor_job_v1",
):
    _TOOL_SCHEMAS[_task_tool] = _schema(
        ("collection", "request"),
        properties={
            "collection": _TEXT,
            "request": _OBJECT,
            "productId": _TEXT,
            "tenantId": _TEXT,
        },
    )

_TOOL_DESCRIPTIONS = {
    "vyral_list_collections_v1": (
        "Lists record collections visible to this caller."
    ),
    "vyral_get_collection_v1": "Gets a record collection policy.",
    "vyral_get_record_v1": "Gets one record by durable identity.",
    "vyral_query_records_v1": "Runs a bounded structured query.",
    "vyral_search_records_v1": (
        "Runs a bounded lexical or vector search."
    ),
    "vyral_retrieve_v1": (
        "Runs bounded provider-neutral retrieval."
    ),
    "vyral_build_rag_context_v1": (
        "Builds bounded citation-aware RAG context."
    ),
    "vyral_build_rag_prompt_v1": (
        "Builds a bounded prompt from Vyral RAG context."
    ),
}

_TASK_HANDLERS = {
    "vyral_start_embedding_job_v1": RuntimeJobHandlerIds.EMBEDDINGS,
    "vyral_start_collection_import_job_v1": (
        RuntimeJobHandlerIds.COLLECTION_IMPORT
    ),
    "vyral_start_record_batch_upsert_job_v1": (
        RuntimeJobHandlerIds.RECORD_BATCH_UPSERT
    ),
    "vyral_start_rag_ingestion_text_job_v1": (
        RuntimeJobHandlerIds.RAG_INGEST_TEXT
    ),
    "vyral_start_rag_ingestion_batch_job_v1": (
        RuntimeJobHandlerIds.RAG_INGEST_BATCH
    ),
    "vyral_start_retrieval_evaluation_comparison_job_v1": (
        RuntimeJobHandlerIds.RETRIEVAL_COMPARE
    ),
    "vyral_start_retrieval_evaluation_job_v1": (
        RuntimeJobHandlerIds.RETRIEVAL_EVALUATE
    ),
    "vyral_start_graph_import_job_v1": (
        RuntimeJobHandlerIds.GRAPH_IMPORT
    ),
    "vyral_start_graph_inspection_job_v1": (
        RuntimeJobHandlerIds.GRAPH_INSPECT
    ),
    "vyral_start_graph_doctor_job_v1": (
        RuntimeJobHandlerIds.GRAPH_DOCTOR
    ),
}


__all__ = [
    "MCP_PROTOCOL_VERSION",
    "McpApplicationConfig",
    "McpAuthorizer",
    "StatelessMcpApplication",
]
