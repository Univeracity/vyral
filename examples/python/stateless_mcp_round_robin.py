from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path
import sys
import tempfile
from typing import Any, Mapping


sys.path.insert(
    0,
    str(Path(__file__).resolve().parents[2] / "runtimes/python/src"),
)

from vyral_runtime import VyralRuntime  # noqa: E402
from vyral_runtime.host import (  # noqa: E402
    MCP_PROTOCOL_VERSION,
    StatelessMcpApplication,
)


def _rpc(request_id: int, method: str) -> dict[str, object]:
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "method": method,
        "params": {
            "_meta": {
                "io.modelcontextprotocol/protocolVersion": (
                    MCP_PROTOCOL_VERSION
                ),
                "io.modelcontextprotocol/clientCapabilities": {},
                "io.modelcontextprotocol/clientInfo": {
                    "name": "vyral-stateless-example",
                    "version": "1.0.0",
                },
            }
        },
    }


async def _request(
    app: StatelessMcpApplication,
    message: Mapping[str, object],
    *,
    method_header: str,
) -> tuple[int, dict[str, str], dict[str, Any]]:
    body = json.dumps(message, separators=(",", ":")).encode("utf-8")
    headers = (
        (b"host", b"127.0.0.1"),
        (b"accept", b"application/json"),
        (b"content-type", b"application/json"),
        (b"content-length", str(len(body)).encode("ascii")),
        (b"mcp-protocol-version", MCP_PROTOCOL_VERSION.encode("ascii")),
        (b"mcp-method", method_header.encode("ascii")),
    )
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
            "method": "POST",
            "path": "/mcp",
            "headers": list(headers),
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
    decoded = json.loads(response_body)
    if not isinstance(decoded, dict):
        raise RuntimeError("The MCP response was not a JSON object.")
    return int(start["status"]), response_headers, decoded


async def run_round_robin() -> dict[str, object]:
    with tempfile.TemporaryDirectory(
        prefix="vyral-stateless-mcp-"
    ) as temporary:
        root = Path(temporary)
        runtimes = (
            VyralRuntime.open_local(root / "instance-a"),
            VyralRuntime.open_local(root / "instance-b"),
        )
        apps = tuple(StatelessMcpApplication(runtime) for runtime in runtimes)
        try:
            requests: list[dict[str, object]] = []
            baseline: dict[str, object] = {}
            route_plan = (
                ("server/discover", 0),
                ("tools/list", 1),
                ("server/discover", 1),
                ("tools/list", 0),
            )
            for index, (method, target) in enumerate(route_plan):
                status, headers, response = await _request(
                    apps[target],
                    _rpc(index + 1, method),
                    method_header=method,
                )
                if status != 200 or "result" not in response:
                    raise RuntimeError(
                        f"{method} failed on instance {target + 1}."
                    )
                result = response["result"]
                if method in baseline and baseline[method] != result:
                    raise RuntimeError(
                        f"{method} changed across healthy instances."
                    )
                baseline[method] = result
                requests.append(
                    {
                        "method": method,
                        "methodVisibleBeforeBody": True,
                        "target": f"instance-{target + 1}",
                        "status": status,
                        "sessionHeaderPresent": (
                            "mcp-session-id" in headers
                        ),
                    }
                )

            mismatch_status, _, _ = await _request(
                apps[0],
                _rpc(99, "server/discover"),
                method_header="tools/list",
            )
            if mismatch_status != 400:
                raise RuntimeError(
                    "The host accepted a body/header routing mismatch."
                )
            if any(
                item["sessionHeaderPresent"] is not False
                for item in requests
            ):
                raise RuntimeError("An MCP response created a session.")

            tools = baseline["tools/list"]
            if not isinstance(tools, dict):
                raise RuntimeError("The tools/list result was malformed.")
            catalog = tools.get("tools")
            if not isinstance(catalog, list):
                raise RuntimeError("The tools/list catalog was malformed.")

            return {
                "schemaVersion": "vyral.stateless-mcp-example.v1",
                "protocolVersion": MCP_PROTOCOL_VERSION,
                "topology": {
                    "gateway": "header-aware-round-robin",
                    "instanceCount": len(apps),
                    "sharedMcpSessionStore": False,
                },
                "requests": requests,
                "catalogToolCount": len(catalog),
                "equivalentResultsAcrossInstances": True,
                "headerBodyMismatchRejected": True,
                "claim": (
                    "Discovery and catalog requests landed on either healthy "
                    "instance without an MCP session, while the host still "
                    "verified that routing headers matched the JSON-RPC body."
                ),
            }
        finally:
            for runtime in runtimes:
                runtime.close()


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Route self-describing MCP requests across two independent "
            "Vyral instances."
        )
    )
    parser.add_argument("--json", action="store_true")
    arguments = parser.parse_args()
    result = asyncio.run(run_round_robin())
    if arguments.json:
        print(json.dumps(result, indent=2, sort_keys=True))
        return

    topology = result["topology"]
    requests = result["requests"]
    assert isinstance(topology, dict)
    assert isinstance(requests, list)
    print(
        f"protocol={result['protocolVersion']} "
        f"instances={topology['instanceCount']} session-store=none"
    )
    for item in requests:
        assert isinstance(item, dict)
        print(
            f"{item['method']} -> {item['target']} "
            f"status={item['status']} session=none"
        )
    print("routing mismatch -> rejected")
    print(result["claim"])


if __name__ == "__main__":
    main()
