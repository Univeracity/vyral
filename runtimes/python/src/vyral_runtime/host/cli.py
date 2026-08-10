from __future__ import annotations

import argparse
from importlib import import_module
import ipaddress
import os
from pathlib import Path
from typing import Sequence

from .application import create_host_application
from .mcp import McpApplicationConfig
from .rest import RestApplicationConfig


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="vyral-runtime",
        description=(
            "Serve the local Vyral Python runtime over REST and "
            "stateless MCP."
        ),
    )
    parser.add_argument(
        "--root",
        default=os.environ.get("VYRAL_RUNTIME_ROOT"),
        help=(
            "Durable runtime directory. May also be set with "
            "VYRAL_RUNTIME_ROOT."
        ),
    )
    parser.add_argument(
        "--host",
        default="127.0.0.1",
        help="Bind address (default: 127.0.0.1).",
    )
    parser.add_argument(
        "--port",
        type=int,
        default=5220,
        help="TCP port (default: 5220).",
    )
    parser.add_argument(
        "--log-level",
        default="info",
        choices=(
            "critical",
            "error",
            "warning",
            "info",
            "debug",
            "trace",
        ),
    )
    parser.add_argument(
        "--allowed-host",
        action="append",
        default=[],
        help=(
            "Additional MCP Host value allowed for an intentional "
            "network deployment. Repeat for multiple hosts."
        ),
    )
    parser.add_argument(
        "--allowed-origin",
        action="append",
        default=[],
        help=(
            "Exact browser Origin allowed by REST and MCP. Repeat for "
            "multiple origins."
        ),
    )
    parser.add_argument(
        "--mcp-conformance-diagnostics",
        action="store_true",
        default=(
            os.environ.get(
                "VYRAL_MCP_CONFORMANCE_DIAGNOSTICS", ""
            ).strip().lower()
            in {"1", "true", "yes", "on"}
        ),
        help=(
            "Expose official-runner diagnostic tools. Intended only for "
            "isolated conformance qualification."
        ),
    )
    arguments = parser.parse_args(argv)
    if not arguments.root:
        parser.error(
            "--root or VYRAL_RUNTIME_ROOT is required; Vyral will "
            "not choose a durable data directory implicitly."
        )
    if not 1 <= arguments.port <= 65_535:
        parser.error("--port must be between 1 and 65535.")
    api_key = os.environ.get("VYRAL_API_KEY")
    if not _loopback_bind(arguments.host) and not api_key:
        parser.error(
            "A non-loopback --host requires VYRAL_API_KEY."
        )
    allowed_hosts = {
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1",
        *(value.strip() for value in arguments.allowed_host),
    }
    if (
        not _wildcard_bind(arguments.host)
        and arguments.host.strip()
    ):
        allowed_hosts.add(arguments.host.strip())
    if any(not value for value in allowed_hosts):
        parser.error("--allowed-host values must be non-empty.")
    allowed_origins = frozenset(
        value.strip() for value in arguments.allowed_origin
    )
    if any(not value for value in allowed_origins):
        parser.error("--allowed-origin values must be non-empty.")
    if (
        _wildcard_bind(arguments.host)
        and not arguments.allowed_host
    ):
        parser.error(
            "A wildcard --host requires at least one --allowed-host."
        )
    try:
        uvicorn = import_module("uvicorn")
    except ImportError as error:
        raise RuntimeError(
            "Serving Vyral requires the 'server' extra: "
            "pip install 'vyral-runtime[server]'."
        ) from error
    application = create_host_application(
        str(Path(arguments.root).expanduser().resolve()),
        api_key=api_key,
        rest_config=RestApplicationConfig(
            allowed_origins=allowed_origins,
            allowed_hosts=frozenset(allowed_hosts),
        ),
        mcp_config=McpApplicationConfig(
            allowed_origins=allowed_origins,
            allowed_hosts=frozenset(allowed_hosts),
            enable_conformance_diagnostics=(
                arguments.mcp_conformance_diagnostics
            )
        ),
    )
    uvicorn.run(
        application,
        host=arguments.host,
        port=arguments.port,
        log_level=arguments.log_level,
    )
    return 0


def _wildcard_bind(host: str) -> bool:
    return host.strip().casefold() in {"0.0.0.0", "::", "[::]"}


def _loopback_bind(host: str) -> bool:
    selected = host.strip().strip("[]")
    if selected.casefold() == "localhost":
        return True
    try:
        return ipaddress.ip_address(selected).is_loopback
    except ValueError:
        return False


__all__ = ["main"]
