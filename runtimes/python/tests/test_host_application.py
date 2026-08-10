from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable
import json
from pathlib import Path
import sys
import tempfile
from typing import Any, Mapping
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import VyralRuntime  # noqa: E402
from vyral_runtime.host import (  # noqa: E402
    MCP_PROTOCOL_VERSION,
    VyralHostApplication,
    create_host_application,
)


async def _http_request(
    app: VyralHostApplication,
    method: str,
    path: str,
    *,
    body: Mapping[str, Any] | None = None,
    headers: Mapping[str, str] | None = None,
) -> tuple[int, object | None]:
    encoded = (
        json.dumps(body, separators=(",", ":")).encode("utf-8")
        if body is not None
        else b""
    )
    selected_headers = {
        "host": "127.0.0.1:5220",
        "content-length": str(len(encoded)),
        **({"content-type": "application/json"} if body is not None else {}),
        **dict(headers or {}),
    }
    received = False
    sent: list[Mapping[str, Any]] = []

    async def receive() -> Mapping[str, Any]:
        nonlocal received
        if received:
            return {"type": "http.disconnect"}
        received = True
        return {
            "type": "http.request",
            "body": encoded,
            "more_body": False,
        }

    async def send(message: Mapping[str, Any]) -> None:
        sent.append(message)

    await app(
        {
            "type": "http",
            "http_version": "1.1",
            "method": method,
            "path": path,
            "query_string": b"",
            "headers": [
                (
                    name.encode("ascii"),
                    value.encode("latin-1"),
                )
                for name, value in selected_headers.items()
            ],
        },
        receive,
        send,
    )
    start = next(
        message
        for message in sent
        if message["type"] == "http.response.start"
    )
    response_body = b"".join(
        bytes(message.get("body", b""))
        for message in sent
        if message["type"] == "http.response.body"
    )
    return (
        int(start["status"]),
        json.loads(response_body) if response_body else None,
    )


async def _lifespan(
    app: VyralHostApplication,
    *message_types: str,
) -> list[Mapping[str, Any]]:
    messages = iter(message_types)
    sent: list[Mapping[str, Any]] = []

    async def receive() -> Mapping[str, Any]:
        return {"type": next(messages)}

    async def send(message: Mapping[str, Any]) -> None:
        sent.append(message)

    await app({"type": "lifespan"}, receive, send)
    return sent


class CombinedHostApplicationTests(unittest.IsolatedAsyncioTestCase):
    async def test_host_creation_verifies_bundled_assets(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-host-assets-"
        ) as root, patch(
            "vyral_runtime.runtime.run_bundled_goldens"
        ) as goldens:
            app = create_host_application(root)
            app.runtime.close()

        goldens.assert_called_once_with()

    async def test_combined_host_routes_rest_and_mcp_with_one_policy(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-host-"
        ) as root:
            app = create_host_application(root, api_key="shared-secret")
            status, _ = await _http_request(app, "GET", "/health")
            self.assertEqual(200, status)

            status, _ = await _http_request(
                app,
                "GET",
                "/collections",
            )
            self.assertEqual(401, status)
            status, collections = await _http_request(
                app,
                "GET",
                "/collections",
                headers={"x-vyral-api-key": "shared-secret"},
            )
            self.assertEqual(200, status)
            self.assertEqual([], collections)

            message = {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {
                    "_meta": {
                        "io.modelcontextprotocol/protocolVersion": (
                            MCP_PROTOCOL_VERSION
                        ),
                        "io.modelcontextprotocol/clientCapabilities": {},
                    }
                },
            }
            routing_headers = {
                "content-type": "application/json",
                "mcp-protocol-version": MCP_PROTOCOL_VERSION,
                "mcp-method": "tools/list",
            }
            status, _ = await _http_request(
                app,
                "POST",
                "/mcp",
                body=message,
                headers=routing_headers,
            )
            self.assertEqual(401, status)
            status, response = await _http_request(
                app,
                "POST",
                "/mcp",
                body=message,
                headers={
                    **routing_headers,
                    "x-vyral-api-key": "shared-secret",
                },
            )
            self.assertEqual(200, status)
            self.assertIsInstance(response, dict)
            app.runtime.close()

    async def test_host_owned_runtime_closes_after_clean_shutdown(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-lifespan-"
        ) as root:
            app = create_host_application(root)
            sent = await _lifespan(
                app,
                "lifespan.startup",
                "lifespan.shutdown",
            )
            self.assertEqual(
                [
                    "lifespan.startup.complete",
                    "lifespan.shutdown.complete",
                ],
                [message["type"] for message in sent],
            )
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = app.runtime.records

    async def test_startup_cancellation_cleans_up_and_propagates(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-startup-cancel-"
        ) as root:
            app = create_host_application(root)
            rest_shutdown = False

            async def cancel_mcp_startup() -> None:
                raise asyncio.CancelledError

            async def record_rest_shutdown() -> None:
                nonlocal rest_shutdown
                rest_shutdown = True

            app.mcp.startup = cancel_mcp_startup  # type: ignore[method-assign]
            app.rest.shutdown = record_rest_shutdown  # type: ignore[method-assign]

            with self.assertRaises(asyncio.CancelledError):
                await _lifespan(app, "lifespan.startup")

            self.assertTrue(rest_shutdown)
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = app.runtime.records

    async def test_startup_failure_rolls_back_and_redacts_error(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-startup-failure-"
        ) as root:
            app = create_host_application(root)
            rest_shutdown = False
            mcp_shutdown = False

            async def fail_startup() -> None:
                raise RuntimeError("startup-secret-must-not-escape")

            async def record_rest_shutdown() -> None:
                nonlocal rest_shutdown
                rest_shutdown = True

            async def record_mcp_shutdown() -> None:
                nonlocal mcp_shutdown
                mcp_shutdown = True

            app.mcp.startup = fail_startup  # type: ignore[method-assign]
            app.mcp.shutdown = record_mcp_shutdown  # type: ignore[method-assign]
            app.rest.shutdown = record_rest_shutdown  # type: ignore[method-assign]
            sent = await _lifespan(app, "lifespan.startup")

            self.assertTrue(rest_shutdown)
            self.assertTrue(mcp_shutdown)
            self.assertEqual("lifespan.startup.failed", sent[0]["type"])
            self.assertEqual(
                "Vyral host startup failed.",
                sent[0]["message"],
            )
            self.assertNotIn("secret", str(sent[0]))
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = app.runtime.records

    async def test_first_adapter_startup_failure_is_unwound(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-rest-startup-failure-"
        ) as root:
            app = create_host_application(root)
            rest_shutdown = False
            mcp_started = False

            async def fail_rest_startup() -> None:
                raise RuntimeError("partially initialized")

            async def record_rest_shutdown() -> None:
                nonlocal rest_shutdown
                rest_shutdown = True

            async def record_mcp_startup() -> None:
                nonlocal mcp_started
                mcp_started = True

            app.rest.startup = fail_rest_startup  # type: ignore[method-assign]
            app.rest.shutdown = record_rest_shutdown  # type: ignore[method-assign]
            app.mcp.startup = record_mcp_startup  # type: ignore[method-assign]

            sent = await _lifespan(app, "lifespan.startup")

            self.assertTrue(rest_shutdown)
            self.assertFalse(mcp_started)
            self.assertEqual("lifespan.startup.failed", sent[0]["type"])
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = app.runtime.records

    async def test_shutdown_runs_both_adapters_and_redacts_failure(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-shutdown-failure-"
        ) as root:
            runtime = VyralRuntime(root)
            app = VyralHostApplication(
                runtime,
                close_runtime_on_shutdown=True,
            )
            rest_shutdown = False

            async def fail_mcp_shutdown() -> None:
                raise RuntimeError("shutdown-secret-must-not-escape")

            async def record_rest_shutdown() -> None:
                nonlocal rest_shutdown
                rest_shutdown = True

            app.mcp.shutdown = fail_mcp_shutdown  # type: ignore[method-assign]
            app.rest.shutdown = record_rest_shutdown  # type: ignore[method-assign]
            sent = await _lifespan(app, "lifespan.shutdown")

            self.assertTrue(rest_shutdown)
            self.assertEqual("lifespan.shutdown.failed", sent[0]["type"])
            self.assertEqual(
                "Vyral host shutdown failed.",
                sent[0]["message"],
            )
            self.assertNotIn("secret", str(sent[0]))
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = runtime.records

    async def test_shutdown_cancellation_finishes_cleanup_and_propagates(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-combined-shutdown-cancel-"
        ) as root:
            runtime = VyralRuntime(root)
            app = VyralHostApplication(
                runtime,
                close_runtime_on_shutdown=True,
            )
            rest_shutdown = False

            async def cancel_mcp_shutdown() -> None:
                raise asyncio.CancelledError

            async def record_rest_shutdown() -> None:
                nonlocal rest_shutdown
                rest_shutdown = True

            app.mcp.shutdown = cancel_mcp_shutdown  # type: ignore[method-assign]
            app.rest.shutdown = record_rest_shutdown  # type: ignore[method-assign]

            with self.assertRaises(asyncio.CancelledError):
                await _lifespan(app, "lifespan.shutdown")

            self.assertTrue(rest_shutdown)
            with self.assertRaisesRegex(RuntimeError, "closed"):
                _ = runtime.records


if __name__ == "__main__":
    unittest.main()
