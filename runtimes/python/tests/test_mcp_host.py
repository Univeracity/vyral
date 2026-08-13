from __future__ import annotations

import asyncio
import base64
import json
from pathlib import Path
import sys
import tempfile
from typing import Any, Mapping
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (  # noqa: E402
    ApiKeyAuthorizer,
    McpApiKeyAuthorizer,
    RecordCollectionPolicy,
    VyralRuntime,
)
from vyral_runtime.host import (  # noqa: E402
    MCP_PROTOCOL_VERSION,
    McpApplicationConfig,
    StatelessMcpApplication,
)


_TASKS = "io.modelcontextprotocol/tasks"


def _rpc(
    method: str,
    *,
    params: Mapping[str, Any] | None = None,
    request_id: object = 1,
    tasks: bool = False,
    client_info: bool = True,
) -> dict[str, Any]:
    selected_params = dict(params or {})
    meta: dict[str, Any] = {
        "io.modelcontextprotocol/protocolVersion": (
            MCP_PROTOCOL_VERSION
        ),
        "io.modelcontextprotocol/clientCapabilities": (
            {"extensions": {_TASKS: {}}} if tasks else {}
        ),
    }
    if client_info:
        meta["io.modelcontextprotocol/clientInfo"] = {
            "name": "vyral-python-tests",
            "version": "1.0.0",
        }
    selected_params["_meta"] = meta
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": method,
        "params": selected_params,
    }


def _routing_name(message: Mapping[str, Any]) -> str | None:
    params = message.get("params")
    if not isinstance(params, Mapping):
        return None
    method = message.get("method")
    if method == "tools/call":
        value = params.get("name")
    elif method == "resources/read":
        value = params.get("uri")
    elif method in {"tasks/get", "tasks/update", "tasks/cancel"}:
        value = params.get("taskId")
    else:
        return None
    return value if isinstance(value, str) else None


async def _request(
    app: StatelessMcpApplication,
    message: Mapping[str, Any] | None,
    *,
    method: str = "POST",
    path: str = "/mcp",
    header_overrides: Mapping[str, str | None] | None = None,
    raw_body: bytes | None = None,
) -> tuple[int, dict[str, str], object | None]:
    body = (
        raw_body
        if raw_body is not None
        else json.dumps(message, separators=(",", ":")).encode("utf-8")
    )
    headers: dict[str, str] = {
        "accept": "application/json, text/event-stream",
        "content-type": "application/json",
        "content-length": str(len(body)),
    }
    if message is not None:
        headers["mcp-protocol-version"] = MCP_PROTOCOL_VERSION
        rpc_method = message.get("method")
        if isinstance(rpc_method, str):
            headers["mcp-method"] = rpc_method
        name = _routing_name(message)
        if name is not None:
            try:
                name.encode("ascii")
                headers["mcp-name"] = name
            except UnicodeEncodeError:
                encoded = base64.b64encode(name.encode("utf-8")).decode(
                    "ascii"
                )
                headers["mcp-name"] = f"=?base64?{encoded}?="
    for name, value in (header_overrides or {}).items():
        normalized = name.lower()
        if value is None:
            headers.pop(normalized, None)
        else:
            headers[normalized] = value
    sent: list[Mapping[str, Any]] = []
    received = False

    async def receive() -> Mapping[str, Any]:
        nonlocal received
        if received:
            return {"type": "http.disconnect"}
        received = True
        return {
            "type": "http.request",
            "body": body,
            "more_body": False,
        }

    async def send(value: Mapping[str, Any]) -> None:
        sent.append(value)

    await app(
        {
            "type": "http",
            "http_version": "1.1",
            "method": method,
            "path": path,
            "headers": [
                (name.encode("ascii"), value.encode("latin-1"))
                for name, value in headers.items()
            ],
        },
        receive,
        send,
    )
    start = next(
        item for item in sent if item["type"] == "http.response.start"
    )
    response_headers = {
        bytes(name).decode("ascii").lower(): bytes(value).decode(
            "latin-1"
        )
        for name, value in start.get("headers", [])
    }
    response_body = b"".join(
        bytes(item.get("body", b""))
        for item in sent
        if item["type"] == "http.response.body"
    )
    if response_headers.get("content-type") == "text/event-stream":
        decoded = [
            json.loads(line.removeprefix("data: "))
            for line in response_body.decode("utf-8").splitlines()
            if line.startswith("data: ")
        ]
    else:
        decoded = json.loads(response_body) if response_body else None
    return int(start["status"]), response_headers, decoded


class _RecordingAuthorizer:
    def __init__(self) -> None:
        self.calls: list[
            tuple[str, Mapping[str, str], Mapping[str, Any]]
        ] = []

    async def authorize(
        self,
        operation_id: str,
        headers: Mapping[str, str],
        arguments: Mapping[str, Any],
    ) -> None:
        self.calls.append((operation_id, headers, arguments))


class StatelessMcpHostTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(
            prefix="vyral-python-mcp-"
        )
        self.root = Path(self.temporary.name)
        self.runtime = VyralRuntime(self.root)

    async def asyncTearDown(self) -> None:
        self.runtime.close()
        self.temporary.cleanup()

    async def test_discovery_is_sessionless_and_self_describing(
        self,
    ) -> None:
        app = StatelessMcpApplication(self.runtime)
        status, headers, response = await _request(
            app,
            _rpc("server/discover", client_info=False),
        )

        self.assertEqual(200, status)
        self.assertNotIn("mcp-session-id", headers)
        assert isinstance(response, dict)
        result = response["result"]
        self.assertEqual("complete", result["resultType"])
        self.assertEqual(
            [MCP_PROTOCOL_VERSION], result["supportedVersions"]
        )
        self.assertEqual(
            {"tools": {}, "resources": {}}, result["capabilities"]
        )
        self.assertEqual(
            "vyral-python",
            result["_meta"][
                "io.modelcontextprotocol/serverInfo"
            ]["name"],
        )

    async def test_transport_is_post_only_and_bounded(self) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(max_request_body_bytes=256),
        )
        status, headers, _ = await _request(
            app, None, method="GET", raw_body=b""
        )
        self.assertEqual(405, status)
        self.assertEqual("POST", headers["allow"])

        status, _, _ = await _request(
            app,
            _rpc("server/discover"),
            header_overrides={"origin": "https://attacker.invalid"},
        )
        self.assertEqual(403, status)

        status, _, _ = await _request(
            app,
            None,
            raw_body=b"{" + (b"x" * 300) + b"}",
            header_overrides={"content-length": "302"},
        )
        self.assertEqual(413, status)

    async def test_routing_headers_are_required_and_verified(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        discover = _rpc("server/discover")
        status, _, response = await _request(
            app,
            discover,
            header_overrides={"mcp-method": None},
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32020, response["error"]["code"])

        status, _, response = await _request(
            app,
            discover,
            header_overrides={
                "mcp-protocol-version": "1900-01-01"
            },
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32022, response["error"]["code"])

        call = _rpc(
            "tools/call",
            params={
                "name": "vyral_list_collections_v1",
                "arguments": {},
            },
        )
        status, _, response = await _request(
            app,
            call,
            header_overrides={"mcp-name": "another-tool"},
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32020, response["error"]["code"])

    async def test_base64_mcp_name_is_decoded(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        request = _rpc(
            "resources/read",
            params={"uri": "vyral://health/v1"},
        )
        encoded = base64.b64encode(
            b"vyral://health/v1"
        ).decode("ascii")
        status, _, response = await _request(
            app,
            request,
            header_overrides={
                "mcp-name": f"=?base64?{encoded}?="
            },
        )
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertEqual(
            "vyral://health/v1",
            response["result"]["contents"][0]["uri"],
        )

    async def test_default_catalog_has_read_surface_only(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        status, _, tool_response = await _request(
            app, _rpc("tools/list")
        )
        self.assertEqual(200, status)
        assert isinstance(tool_response, dict)
        tools = tool_response["result"]["tools"]
        self.assertEqual(17, len(tools))
        self.assertNotIn(
            "extensions",
            (await _request(app, _rpc("server/discover")))[2][
                "result"
            ]["capabilities"],
        )
        self.assertTrue(
            all(
                tool["annotations"]["readOnlyHint"]
                for tool in tools
            )
        )

        status, _, resource_response = await _request(
            app, _rpc("resources/list")
        )
        self.assertEqual(200, status)
        assert isinstance(resource_response, dict)
        self.assertEqual(
            {
                "vyral://health/v1",
                "vyral://readiness/v1",
                "vyral://open_api_contract/v1",
                "vyral://public_schema_contract/v1",
            },
            {
                resource["uri"]
                for resource in resource_response["result"]["resources"]
            },
        )

    async def test_tool_calls_use_services_and_authorize_each_call(
        self,
    ) -> None:
        await self.runtime.async_records.create_collection(
            RecordCollectionPolicy("documents")
        )
        authorizer = _RecordingAuthorizer()
        app = StatelessMcpApplication(
            self.runtime, authorizer=authorizer
        )
        status, _, response = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "vyral_list_collections_v1",
                    "arguments": {},
                },
            ),
            header_overrides={"authorization": "Bearer test"},
        )

        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertEqual(
            "documents",
            response["result"]["structuredContent"][0],
        )
        self.assertEqual(1, len(authorizer.calls))
        operation, headers, arguments = authorizer.calls[0]
        self.assertEqual("listCollections", operation)
        self.assertEqual("Bearer test", headers["authorization"])
        self.assertEqual({}, arguments)

    async def test_authenticated_catalogs_do_not_leak_without_key(
        self,
    ) -> None:
        policy = ApiKeyAuthorizer("catalog-secret")
        app = StatelessMcpApplication(
            self.runtime,
            authorizer=McpApiKeyAuthorizer(policy),
        )
        for method in (
            "tools/list",
            "resources/list",
            "resources/templates/list",
        ):
            with self.subTest(method=method):
                status, _, response = await _request(
                    app, _rpc(method)
                )
                self.assertEqual(401, status)
                assert isinstance(response, dict)
                self.assertEqual(
                    -32000, response["error"]["code"]
                )

                status, _, _ = await _request(
                    app,
                    _rpc(method),
                    header_overrides={
                        "x-vyral-api-key": "catalog-secret"
                    },
                )
                self.assertEqual(200, status)

    async def test_task_tool_requires_client_capability(self) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(
                enabled_operation_ids=frozenset({"startEmbeddingJob"})
            ),
        )
        status, _, response = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "vyral_start_embedding_job_v1",
                    "arguments": {
                        "request": {"texts": ["portable"]}
                    },
                },
            ),
        )

        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32021, response["error"]["code"])

    async def test_conformance_diagnostics_are_explicit_and_bounded(
        self,
    ) -> None:
        default_app = StatelessMcpApplication(self.runtime)
        status, _, listed = await _request(
            default_app, _rpc("tools/list")
        )
        self.assertEqual(200, status)
        assert isinstance(listed, dict)
        self.assertNotIn(
            "test_missing_capability",
            {
                tool["name"]
                for tool in listed["result"]["tools"]
            },
        )

        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(
                enable_conformance_diagnostics=True
            ),
        )
        status, _, listed = await _request(app, _rpc("tools/list"))
        self.assertEqual(200, status)
        assert isinstance(listed, dict)
        self.assertIn(
            "test_missing_capability",
            {
                tool["name"]
                for tool in listed["result"]["tools"]
            },
        )
        status, _, discovered = await _request(
            app, _rpc("server/discover")
        )
        self.assertEqual(200, status)
        assert isinstance(discovered, dict)
        self.assertIn(
            "prompts", discovered["result"]["capabilities"]
        )
        self.assertIn(
            "completions", discovered["result"]["capabilities"]
        )
        status, _, prompts = await _request(
            app, _rpc("prompts/list")
        )
        self.assertEqual(200, status)
        assert isinstance(prompts, dict)
        self.assertEqual(
            {
                "test_simple_prompt",
                "test_prompt_with_arguments",
                "test_prompt_with_embedded_resource",
                "test_prompt_with_image",
                "test_input_required_result_prompt",
            },
            {
                prompt["name"]
                for prompt in prompts["result"]["prompts"]
            },
        )
        self.assertEqual(
            "private", prompts["result"]["cacheScope"]
        )
        status, _, response = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "test_missing_capability",
                    "arguments": {},
                },
            ),
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32021, response["error"]["code"])
        self.assertEqual(
            {"sampling": {}},
            response["error"]["data"]["requiredCapabilities"],
        )
        custom = _rpc(
            "tools/call",
            params={
                "name": "test_custom_header",
                "arguments": {"value": "Hello"},
            },
        )
        status, _, response = await _request(
            app,
            custom,
            header_overrides={"mcp-param-value": "Hello"},
        )
        self.assertEqual(200, status)
        status, _, response = await _request(app, custom)
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32020, response["error"]["code"])

    async def test_core_conformance_fixtures_are_diagnostic_only(
        self,
    ) -> None:
        default_app = StatelessMcpApplication(self.runtime)
        status, _, response = await _request(
            default_app,
            _rpc(
                "tools/call",
                params={"name": "test_simple_text", "arguments": {}},
            ),
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32602, response["error"]["code"])

        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(enable_conformance_diagnostics=True),
        )
        expected_types = {
            "test_simple_text": "text",
            "test_image_content": "image",
            "test_audio_content": "audio",
            "test_embedded_resource": "resource",
        }
        for name, content_type in expected_types.items():
            with self.subTest(name=name):
                status, _, response = await _request(
                    app,
                    _rpc(
                        "tools/call",
                        params={"name": name, "arguments": {}},
                    ),
                )
                self.assertEqual(200, status)
                assert isinstance(response, dict)
                self.assertEqual(
                    content_type,
                    response["result"]["content"][0]["type"],
                )

        status, _, response = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "test_multiple_content_types",
                    "arguments": {},
                },
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertEqual(
            {"text", "image", "resource"},
            {
                item["type"]
                for item in response["result"]["content"]
            },
        )

        status, _, response = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "test_error_handling",
                    "arguments": {},
                },
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertTrue(response["result"]["isError"])

    async def test_conformance_resources_prompts_and_completion(
        self,
    ) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(enable_conformance_diagnostics=True),
        )
        for uri, field in (
            ("test://static-text", "text"),
            ("test://static-binary", "blob"),
            ("test://template/123/data", "text"),
        ):
            with self.subTest(uri=uri):
                status, _, response = await _request(
                    app, _rpc("resources/read", params={"uri": uri})
                )
                self.assertEqual(200, status)
                assert isinstance(response, dict)
                content = response["result"]["contents"][0]
                self.assertIn(field, content)
                if "template" in uri:
                    self.assertIn("123", content[field])

        prompt_cases = (
            ("test_simple_prompt", {}),
            (
                "test_prompt_with_arguments",
                {"arguments": {"arg1": "one", "arg2": "two"}},
            ),
            (
                "test_prompt_with_embedded_resource",
                {"arguments": {"resourceUri": "test://example"}},
            ),
            ("test_prompt_with_image", {}),
        )
        for name, extra in prompt_cases:
            with self.subTest(prompt=name):
                status, _, response = await _request(
                    app,
                    _rpc(
                        "prompts/get",
                        params={"name": name, **extra},
                    ),
                )
                self.assertEqual(200, status)
                assert isinstance(response, dict)
                self.assertTrue(response["result"]["messages"])

        status, _, response = await _request(
            app,
            _rpc(
                "completion/complete",
                params={
                    "ref": {
                        "type": "ref/prompt",
                        "name": "test_prompt_with_arguments",
                    },
                    "argument": {"name": "arg1", "value": "test"},
                },
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertEqual([], response["result"]["completion"]["values"])

    async def test_ephemeral_mrtr_and_progress_fixtures(self) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(enable_conformance_diagnostics=True),
        )
        initial = _rpc(
            "tools/call",
            params={
                "name": "test_input_required_result_elicitation",
                "arguments": {},
            },
        )
        status, _, response = await _request(app, initial)
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertEqual(
            "input_required", response["result"]["resultType"]
        )
        complete = _rpc(
            "tools/call",
            params={
                "name": "test_input_required_result_elicitation",
                "arguments": {},
                "inputResponses": {
                    "user_name": {
                        "action": "accept",
                        "content": {"name": "Ada"},
                    }
                },
            },
        )
        status, _, response = await _request(app, complete)
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        self.assertIn("Ada", response["result"]["content"][0]["text"])

        progress = _rpc(
            "tools/call",
            params={
                "name": "test_tool_with_progress",
                "arguments": {},
            },
        )
        progress["params"]["_meta"]["progressToken"] = "progress-1"
        status, headers, response = await _request(app, progress)
        self.assertEqual(200, status)
        self.assertEqual("text/event-stream", headers["content-type"])
        assert isinstance(response, list)
        self.assertEqual(4, len(response))
        self.assertEqual(
            [0, 50, 100],
            [item["params"]["progress"] for item in response[:3]],
        )
        self.assertEqual("complete", response[3]["result"]["resultType"])

    async def test_shutdown_does_not_wait_for_durable_task_work(
        self,
    ) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(
                enable_conformance_diagnostics=True
            ),
        )
        status, _, created = await _request(
            app,
            _rpc(
                "tools/call",
                params={
                    "name": "slow_compute",
                    "arguments": {"seconds": 60},
                },
                tasks=True,
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(created, dict)
        self.assertEqual("task", created["result"]["resultType"])
        await asyncio.wait_for(app.shutdown(), timeout=0.5)

    async def test_same_origin_loopback_is_allowed(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        status, _, _ = await _request(
            app,
            _rpc("server/discover"),
            header_overrides={
                "host": "127.0.0.1:5220",
                "origin": "http://127.0.0.1:5220",
            },
        )
        self.assertEqual(200, status)

    async def test_explicit_origin_mode_rejects_same_host_fallback(self) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(require_explicit_origins=True),
        )
        routing = {
            "host": "127.0.0.1:5220",
            "origin": "http://127.0.0.1:5220",
        }
        status, _, _ = await _request(
            app,
            _rpc("server/discover"),
            header_overrides=routing,
        )
        self.assertEqual(403, status)

        allowed = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(
                require_explicit_origins=True,
                allowed_origins=frozenset(
                    {"http://127.0.0.1:5220"}
                ),
            ),
        )
        status, _, _ = await _request(
            allowed,
            _rpc("server/discover"),
            header_overrides=routing,
        )
        self.assertEqual(200, status)

    async def test_readiness_resource_redacts_host_local_paths(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        status, _, response = await _request(
            app,
            _rpc(
                "resources/read",
                params={"uri": "vyral://readiness/v1"},
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(response, dict)
        content = response["result"]["contents"][0]["text"]
        self.assertIsInstance(content, str)
        assert isinstance(content, str)
        self.assertNotIn(str(self.root), content)
        self.assertNotIn("databasePath", content)

    async def test_dispatch_errors_do_not_expose_exception_text(self) -> None:
        app = StatelessMcpApplication(self.runtime)
        secret = "vyral-mcp-internal-secret"

        async def invalid(*_arguments: object) -> dict[str, object]:
            raise ValueError(secret)

        app._dispatch = invalid  # type: ignore[assignment]
        status, _, response = await _request(
            app, _rpc("server/discover")
        )
        self.assertEqual(400, status)
        assert isinstance(response, dict)
        error = response["error"]
        self.assertEqual("Invalid parameters.", error["message"])
        self.assertEqual(
            "vyral.request.invalid_parameters", error["data"]["code"]
        )
        self.assertNotIn(secret, json.dumps(response))

    async def test_task_is_durable_and_pollable_on_another_instance(
        self,
    ) -> None:
        config = McpApplicationConfig(
            enabled_operation_ids=frozenset({"startEmbeddingJob"})
        )
        first = StatelessMcpApplication(self.runtime, config)
        status, _, created = await _request(
            first,
            _rpc(
                "tools/call",
                params={
                    "name": "vyral_start_embedding_job_v1",
                    "arguments": {
                        "request": {
                            "texts": ["portable", "runtime"],
                            "purpose": "symmetric",
                        }
                    },
                },
                tasks=True,
            ),
        )
        self.assertEqual(200, status)
        assert isinstance(created, dict)
        task = created["result"]
        self.assertEqual("task", task["resultType"])
        self.assertEqual("working", task["status"])
        task_id = task["taskId"]

        if first._dispatch_tasks:
            await asyncio.gather(*first._dispatch_tasks)

        second_runtime = VyralRuntime(self.root)
        try:
            second = StatelessMcpApplication(second_runtime, config)
            status, headers, polled = await _request(
                second,
                _rpc(
                    "tasks/get",
                    params={"taskId": task_id},
                    tasks=True,
                ),
            )
            self.assertEqual(200, status)
            self.assertNotIn("mcp-session-id", headers)
            assert isinstance(polled, dict)
            result = polled["result"]
            self.assertEqual("complete", result["resultType"])
            self.assertEqual("completed", result["status"])
            self.assertEqual(task_id, result["taskId"])
            self.assertEqual(
                2,
                len(
                    result["result"]["structuredContent"]["items"]
                ),
            )

            status, _, updated = await _request(
                second,
                _rpc(
                    "tasks/update",
                    params={
                        "taskId": task_id,
                        "inputResponses": {"not-outstanding": {}},
                    },
                    tasks=True,
                ),
            )
            self.assertEqual(200, status)
            assert isinstance(updated, dict)
            self.assertEqual(
                {"resultType": "complete"},
                {
                    key: value
                    for key, value in updated["result"].items()
                    if key != "_meta"
                },
            )
        finally:
            second_runtime.close()

    async def test_task_methods_require_task_id_routing_header(
        self,
    ) -> None:
        app = StatelessMcpApplication(
            self.runtime,
            McpApplicationConfig(
                enabled_operation_ids=frozenset({"startEmbeddingJob"})
            ),
        )
        status, _, response = await _request(
            app,
            _rpc(
                "tasks/get",
                params={"taskId": "run_opaque"},
                tasks=True,
            ),
            header_overrides={"mcp-name": None},
        )

        self.assertEqual(400, status)
        assert isinstance(response, dict)
        self.assertEqual(-32020, response["error"]["code"])


if __name__ == "__main__":
    unittest.main()
