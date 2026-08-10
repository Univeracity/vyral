#!/usr/bin/env python3
"""Run a bounded concurrent stateless MCP discovery probe."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import http.client
import ipaddress
import json
import math
import os
from pathlib import Path
import time
from typing import Any
from urllib.parse import urlsplit


PROTOCOL_VERSION = "2026-07-28"


def _positive(value: str) -> int:
    try:
        result = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("value must be an integer") from exc
    if result <= 0:
        raise argparse.ArgumentTypeError("value must be positive")
    return result


def _endpoint(value: str) -> tuple[str, int, str]:
    parsed = urlsplit(value)
    if parsed.scheme != "http" or parsed.username or parsed.password:
        raise argparse.ArgumentTypeError(
            "URL must be an unauthenticated loopback HTTP endpoint"
        )
    try:
        address = ipaddress.ip_address(parsed.hostname or "")
    except ValueError as exc:
        raise argparse.ArgumentTypeError("URL host must be a loopback IP literal") from exc
    if not address.is_loopback or parsed.query or parsed.fragment:
        raise argparse.ArgumentTypeError("URL must be a loopback HTTP endpoint")
    port = parsed.port
    if port is None or port < 1024 or port > 65535:
        raise argparse.ArgumentTypeError("URL must include a port from 1024 through 65535")
    return str(address), port, parsed.path or "/mcp"


def _percentile(values: list[float], percentile: float) -> float:
    index = max(0, math.ceil(len(values) * percentile) - 1)
    return values[index]


def _parse_response(content: bytes, content_type: str | None) -> dict[str, Any]:
    text = content.decode("utf-8")
    if content_type and "text/event-stream" in content_type.lower():
        messages: list[dict[str, Any]] = []
        for event in text.replace("\r\n", "\n").split("\n\n"):
            data = "\n".join(
                line[5:].lstrip()
                for line in event.splitlines()
                if line.startswith("data:")
            )
            if data:
                parsed = json.loads(data)
                if isinstance(parsed, dict):
                    messages.append(parsed)
        if not messages:
            raise ValueError("finite SSE response contained no JSON message")
        return messages[-1]
    parsed = json.loads(text)
    if not isinstance(parsed, dict):
        raise ValueError("MCP response was not a JSON object")
    return parsed


def _request(endpoint: tuple[str, int, str], request_id: int) -> dict[str, Any]:
    host, port, path = endpoint
    payload = json.dumps(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "server/discover",
            "params": {
                "_meta": {
                    "io.modelcontextprotocol/protocolVersion": PROTOCOL_VERSION,
                    "io.modelcontextprotocol/clientInfo": {
                        "name": "vyral-load-probe",
                        "version": "1.0.0",
                    },
                    "io.modelcontextprotocol/clientCapabilities": {},
                }
            },
        },
        separators=(",", ":"),
    ).encode("utf-8")
    started = time.perf_counter()
    connection = http.client.HTTPConnection(host, port, timeout=10)
    try:
        connection.request(
            "POST",
            path,
            body=payload,
            headers={
                "Accept": "application/json, text/event-stream",
                "Content-Type": "application/json",
                "MCP-Protocol-Version": PROTOCOL_VERSION,
                "Mcp-Method": "server/discover",
            },
        )
        response = connection.getresponse()
        content = response.read()
        if response.status != 200:
            raise RuntimeError(f"unexpected HTTP status {response.status}")
        if response.getheader("Mcp-Session-Id") is not None:
            raise RuntimeError("stateless endpoint returned Mcp-Session-Id")
        document = _parse_response(content, response.getheader("Content-Type"))
        result = document.get("result")
        if not isinstance(result, dict) or result.get("resultType") != "complete":
            raise RuntimeError("response was not a complete discovery result")
        return {
            "latencyMs": (time.perf_counter() - started) * 1000,
            "status": "passed",
        }
    except Exception as exc:  # The receipt records types, never response bodies.
        return {
            "latencyMs": (time.perf_counter() - started) * 1000,
            "status": "failed",
            "errorType": type(exc).__name__,
        }
    finally:
        connection.close()


def main() -> int:
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, type=_endpoint)
    parser.add_argument("--requests", default=128, type=_positive)
    parser.add_argument("--concurrency", default=16, type=_positive)
    parser.add_argument("--phase", required=True)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    if arguments.requests > 10_000 or arguments.concurrency > 256:
        parser.error("probe bounds are 10,000 requests and 256 workers")

    started = time.perf_counter()
    with ThreadPoolExecutor(max_workers=arguments.concurrency) as executor:
        results = list(
            executor.map(
                lambda request_id: _request(arguments.url, request_id),
                range(1, arguments.requests + 1),
            )
        )
    duration = time.perf_counter() - started
    passed = [result for result in results if result["status"] == "passed"]
    failed = [result for result in results if result["status"] == "failed"]
    latencies = sorted(float(result["latencyMs"]) for result in passed)
    receipt: dict[str, Any] = {
        "schemaVersion": "vyral.mcp-load-smoke.v1",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "phase": arguments.phase,
        "protocolVersion": PROTOCOL_VERSION,
        "status": "passed" if not failed else "failed",
        "requests": arguments.requests,
        "concurrency": arguments.concurrency,
        "passed": len(passed),
        "failed": len(failed),
        "durationMs": round(duration * 1000, 3),
        "throughputPerSecond": round(len(passed) / duration, 3) if duration else None,
        "latencyMs": (
            {
                "p50": round(_percentile(latencies, 0.50), 3),
                "p95": round(_percentile(latencies, 0.95), 3),
                "p99": round(_percentile(latencies, 0.99), 3),
                "max": round(latencies[-1], 3),
            }
            if latencies
            else None
        ),
        "errorTypes": dict(
            sorted(
                {
                    error_type: sum(
                        result.get("errorType") == error_type for result in failed
                    )
                    for error_type in {
                        str(result.get("errorType")) for result in failed
                    }
                }.items()
            )
        ),
        "assertions": [
            "all responses are HTTP 200 complete discovery results",
            "no response contains Mcp-Session-Id",
            "each request uses a new connection and is independently self-describing",
        ],
    }
    output = arguments.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        f"mcp-load-smoke={receipt['status']} phase={arguments.phase} "
        f"passed={len(passed)} failed={len(failed)}"
    )
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
