#!/usr/bin/env python3
"""Loopback-only transparent TCP proxy for MCP conformance wire evidence."""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import signal
import socket
import threading
from typing import Any, Callable


_MAX_HEADER_BYTES = 256 * 1024
_RAW_ROUTING_HEADERS = {
    b"mcp-method",
    b"mcp-name",
    b"mcp-protocol-version",
}


def _loopback(value: str, label: str) -> str:
    try:
        address = ipaddress.ip_address(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"{label} must be a loopback IP literal"
        ) from exc
    if not address.is_loopback:
        raise argparse.ArgumentTypeError(f"{label} must be loopback")
    return value


def _port(value: str) -> int:
    try:
        port = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("port must be an integer") from exc
    if port < 1024 or port > 65535:
        raise argparse.ArgumentTypeError("port must be 1024 through 65535")
    return port


def _ows(value: bytes) -> tuple[bytes, bytes]:
    leading_length = len(value) - len(value.lstrip(b" \t"))
    trailing_length = len(value) - len(value.rstrip(b" \t"))
    trailing = value[len(value) - trailing_length :] if trailing_length else b""
    return value[:leading_length], trailing


def _header_evidence(header_block: bytes) -> dict[str, Any]:
    lines = header_block.split(b"\r\n")
    selected: list[dict[str, Any]] = []
    for line in lines[1:]:
        name, separator, value = line.partition(b":")
        if not separator:
            continue
        normalized_name = name.strip().lower()
        if not normalized_name.startswith(b"mcp-"):
            continue
        leading, trailing = _ows(value)
        item: dict[str, Any] = {
            "name": name.decode("ascii", errors="backslashreplace"),
            "valueBytes": len(value),
            "leadingOwsHex": leading.hex(),
            "trailingOwsHex": trailing.hex(),
            "sha256": hashlib.sha256(value).hexdigest(),
        }
        if normalized_name in _RAW_ROUTING_HEADERS:
            item["rawValueHex"] = value.hex()
        selected.append(item)
    return {
        "startLine": lines[0].decode("ascii", errors="backslashreplace"),
        "headers": selected,
    }


class _HttpMessageRecorder:
    def __init__(self, callback: Callable[[dict[str, Any]], None]) -> None:
        self._callback = callback
        self._buffer = bytearray()
        self._mode = "headers"
        self._remaining = 0
        self._chunk_remaining: int | None = None

    def feed(self, content: bytes) -> None:
        self._buffer.extend(content)
        if len(self._buffer) > _MAX_HEADER_BYTES and self._mode == "headers":
            self._callback({"parseError": "header block exceeded diagnostic limit"})
            self._buffer.clear()
            self._mode = "until-close"
            return
        while self._buffer:
            if self._mode == "headers":
                marker = self._buffer.find(b"\r\n\r\n")
                if marker < 0:
                    return
                block = bytes(self._buffer[:marker])
                del self._buffer[: marker + 4]
                evidence = _header_evidence(block)
                self._callback(evidence)
                content_length, chunked = self._body_shape(block)
                if chunked:
                    self._mode = "chunked"
                    self._chunk_remaining = None
                elif content_length > 0:
                    self._mode = "content-length"
                    self._remaining = content_length
                else:
                    self._mode = "headers"
            elif self._mode == "content-length":
                consumed = min(self._remaining, len(self._buffer))
                del self._buffer[:consumed]
                self._remaining -= consumed
                if self._remaining:
                    return
                self._mode = "headers"
            elif self._mode == "chunked":
                if not self._consume_chunked():
                    return
            else:
                self._buffer.clear()
                return

    @staticmethod
    def _body_shape(block: bytes) -> tuple[int, bool]:
        content_length = 0
        chunked = False
        for line in block.split(b"\r\n")[1:]:
            name, separator, value = line.partition(b":")
            if not separator:
                continue
            normalized_name = name.strip().lower()
            normalized_value = value.strip().lower()
            if normalized_name == b"content-length":
                try:
                    content_length = int(normalized_value)
                except ValueError:
                    content_length = 0
            elif normalized_name == b"transfer-encoding":
                chunked = b"chunked" in normalized_value.split(b",")
        return content_length, chunked

    def _consume_chunked(self) -> bool:
        if self._chunk_remaining is None:
            marker = self._buffer.find(b"\r\n")
            if marker < 0:
                return False
            size_line = bytes(self._buffer[:marker]).partition(b";")[0]
            try:
                self._chunk_remaining = int(size_line, 16)
            except ValueError:
                self._mode = "until-close"
                return False
            del self._buffer[: marker + 2]
            if self._chunk_remaining == 0:
                if self._buffer.startswith(b"\r\n"):
                    del self._buffer[:2]
                else:
                    trailer_end = self._buffer.find(b"\r\n\r\n")
                    if trailer_end < 0:
                        return False
                    del self._buffer[: trailer_end + 4]
                self._mode = "headers"
                self._chunk_remaining = None
                return True
        required = self._chunk_remaining + 2
        if len(self._buffer) < required:
            return False
        del self._buffer[:required]
        self._chunk_remaining = None
        return True


class _EvidenceWriter:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._stream = path.open("a", encoding="utf-8", buffering=1)
        self._lock = threading.Lock()

    def write(self, value: dict[str, Any]) -> None:
        with self._lock:
            self._stream.write(json.dumps(value, sort_keys=True) + "\n")

    def close(self) -> None:
        with self._lock:
            self._stream.close()


class _Proxy:
    def __init__(
        self,
        bind: tuple[str, int],
        upstreams: list[tuple[str, int]],
        evidence: _EvidenceWriter,
    ) -> None:
        if not upstreams:
            raise ValueError("at least one upstream is required")
        self._upstreams = upstreams
        self._evidence = evidence
        self._stop = threading.Event()
        self._connections: set[socket.socket] = set()
        self._connections_lock = threading.Lock()
        self._sequence = 0
        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(bind)
        self._listener.listen()
        self._listener.settimeout(0.5)

    def serve(self) -> None:
        while not self._stop.is_set():
            try:
                client, _ = self._listener.accept()
            except TimeoutError:
                continue
            except OSError:
                if self._stop.is_set():
                    return
                raise
            self._sequence += 1
            connection_id = self._sequence
            with self._connections_lock:
                self._connections.add(client)
            threading.Thread(
                target=self._handle,
                args=(connection_id, client),
                daemon=True,
            ).start()

    def close(self) -> None:
        self._stop.set()
        self._listener.close()
        with self._connections_lock:
            connections = list(self._connections)
        for connection in connections:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            connection.close()

    def _handle(self, connection_id: int, client: socket.socket) -> None:
        upstream: socket.socket | None = None
        try:
            preferred_index = (connection_id - 1) % len(self._upstreams)
            attempted: list[int] = []
            upstream_index = preferred_index
            upstream_address = self._upstreams[upstream_index]
            last_error: OSError | None = None
            for offset in range(len(self._upstreams)):
                upstream_index = (preferred_index + offset) % len(self._upstreams)
                upstream_address = self._upstreams[upstream_index]
                attempted.append(upstream_index)
                candidate = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                candidate.settimeout(1.0)
                try:
                    candidate.connect(upstream_address)
                    candidate.settimeout(None)
                    upstream = candidate
                    break
                except OSError as exc:
                    last_error = exc
                    candidate.close()
            if upstream is None:
                if last_error is not None:
                    raise last_error
                raise OSError("no MCP upstream was available")
            with self._connections_lock:
                self._connections.add(upstream)
            self._evidence.write(
                {
                    "connection": connection_id,
                    "event": "connected",
                    "preferredUpstreamIndex": preferred_index,
                    "upstreamIndex": upstream_index,
                    "upstreamPort": upstream_address[1],
                    "fallback": upstream_index != preferred_index,
                    "attemptedUpstreams": attempted,
                }
            )
            request_recorder = _HttpMessageRecorder(
                lambda value: self._evidence.write(
                    {"connection": connection_id, "direction": "request", **value}
                )
            )
            response_recorder = _HttpMessageRecorder(
                lambda value: self._evidence.write(
                    {"connection": connection_id, "direction": "response", **value}
                )
            )
            request_thread = threading.Thread(
                target=self._relay,
                args=(client, upstream, request_recorder),
                daemon=True,
            )
            response_thread = threading.Thread(
                target=self._relay,
                args=(upstream, client, response_recorder),
                daemon=True,
            )
            request_thread.start()
            response_thread.start()
            request_thread.join()
            response_thread.join()
        except OSError as exc:
            self._evidence.write(
                {
                    "connection": connection_id,
                    "event": "socket-error",
                    "errorType": type(exc).__name__,
                }
            )
        finally:
            for connection in (client, upstream):
                if connection is None:
                    continue
                try:
                    connection.close()
                except OSError:
                    pass
                with self._connections_lock:
                    self._connections.discard(connection)
            self._evidence.write(
                {"connection": connection_id, "event": "closed"}
            )

    @staticmethod
    def _relay(
        source: socket.socket,
        destination: socket.socket,
        recorder: _HttpMessageRecorder,
    ) -> None:
        try:
            while True:
                content = source.recv(64 * 1024)
                if not content:
                    break
                recorder.feed(content)
                destination.sendall(content)
        except OSError:
            pass
        finally:
            try:
                destination.shutdown(socket.SHUT_WR)
            except OSError:
                pass


def main() -> int:
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind-host", default="127.0.0.1")
    parser.add_argument("--bind-port", required=True, type=_port)
    parser.add_argument("--upstream-host", default="127.0.0.1")
    parser.add_argument(
        "--upstream-port", required=True, action="append", type=_port
    )
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    bind_host = _loopback(arguments.bind_host, "bind host")
    upstream_host = _loopback(arguments.upstream_host, "upstream host")
    evidence = _EvidenceWriter(arguments.output.resolve())
    evidence.write(
        {
            "event": "started",
            "schemaVersion": "vyral.mcp-wire-evidence.v1",
            "redaction": (
                "Only Mcp-Method, Mcp-Name, and Mcp-Protocol-Version values are "
                "recorded as bytes; all other MCP values are hashed."
            ),
        }
    )
    proxy = _Proxy(
        (bind_host, arguments.bind_port),
        [
            (upstream_host, upstream_port)
            for upstream_port in arguments.upstream_port
        ],
        evidence,
    )

    def stop(_signum: int, _frame: object) -> None:
        proxy.close()

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)
    try:
        proxy.serve()
    finally:
        proxy.close()
        evidence.write({"event": "stopped"})
        evidence.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
