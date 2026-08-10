from __future__ import annotations

import argparse
from importlib import import_module
import ipaddress
import json
import os
from pathlib import Path
import sys
from typing import Sequence

from .._local_experience import (
    inspect_local_runtime,
    reset_local_quickstart,
    run_local_quickstart_sync,
)
from .application import create_host_application
from .mcp import McpApplicationConfig
from .rest import RestApplicationConfig


def main(argv: Sequence[str] | None = None) -> int:
    selected = list(sys.argv[1:] if argv is None else argv)
    if selected and selected[0] == "quickstart":
        return _quickstart_main(selected[1:])
    if selected and selected[0] == "inspect":
        return _inspect_main(selected[1:])
    if selected and selected[0] == "serve":
        selected = selected[1:]
    return _serve_main(selected)


def _serve_main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="vyral-runtime",
        description=(
            "Serve the local Vyral Python runtime over REST and "
            "stateless MCP."
        ),
        epilog=(
            "Local single-player commands: "
            "'vyral-runtime quickstart --root ./.vyral/quickstart' "
            "and 'vyral-runtime inspect --root ./.vyral/quickstart'. "
            "The explicit 'serve' command is also accepted before the "
            "server options."
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


def _quickstart_main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="vyral-runtime quickstart",
        description=(
            "Run cited local retrieval and prove that one idempotently "
            "admitted execution survives a close/reopen boundary."
        ),
    )
    parser.add_argument(
        "--root",
        required=True,
        help=(
            "Dedicated durable directory. The quickstart refuses a "
            "non-empty directory it did not create."
        ),
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Write one machine-readable result instead of progress text.",
    )
    parser.add_argument(
        "--reset",
        action="store_true",
        help=(
            "Delete only a directory bearing the Vyral quickstart "
            "ownership marker, then exit."
        ),
    )
    arguments = parser.parse_args(argv)
    try:
        if arguments.reset:
            removed = reset_local_quickstart(arguments.root)
            if arguments.json:
                print(json.dumps({"removedRootPath": str(removed)}))
            else:
                print(f"Removed Vyral quickstart state: {removed}")
            return 0
        result = run_local_quickstart_sync(
            arguments.root,
            emit=None if arguments.json else print,
        )
    except (OSError, RuntimeError, ValueError) as error:
        parser.error(str(error))
    if arguments.json:
        print(json.dumps(result.to_dict(), indent=2, sort_keys=True))
    else:
        print("\nCitation-ready context:\n")
        print(result.context_text)
        print("\nInspect this state:")
        print(f"  vyral-runtime inspect --root {result.root_path}")
        print("Reset only this quickstart-owned directory:")
        print(
            "  vyral-runtime quickstart "
            f"--root {result.root_path} --reset"
        )
    return 0


def _inspect_main(argv: Sequence[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="vyral-runtime inspect",
        description=(
            "Explain the active providers, topology, readiness, and "
            "limitations for an existing local runtime directory."
        ),
    )
    parser.add_argument(
        "--root",
        required=True,
        help="Existing durable local runtime directory.",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Write the complete inspection as JSON.",
    )
    arguments = parser.parse_args(argv)
    try:
        inspection = inspect_local_runtime(arguments.root)
    except (OSError, RuntimeError, ValueError) as error:
        parser.error(str(error))
    if arguments.json:
        print(json.dumps(inspection, indent=2, sort_keys=True))
        return 0
    runtime = inspection["runtime"]
    providers = inspection["providers"]
    assert isinstance(runtime, dict)
    assert isinstance(providers, dict)
    print(f"Local state: {inspection['rootPath']}")
    print(f"Topology: {inspection['topology']}")
    print(
        "Runtime: "
        f"{runtime['version']} / contract {runtime['contractVersion']} / "
        f"{runtime['maturity']} / fullLocalReady="
        f"{str(runtime['fullLocalReady']).lower()}"
    )
    for name in ("records", "objects", "embeddings", "execution"):
        provider = providers[name]
        assert isinstance(provider, dict)
        identity = (
            provider.get("adapter")
            or provider.get("provider")
            or "unknown"
        )
        healthy = provider.get("healthy")
        suffix = (
            f" / healthy={str(healthy).lower()}"
            if healthy is not None
            else ""
        )
        print(f"{name.capitalize()}: {identity}{suffix}")
    warnings = inspection["warnings"]
    assert isinstance(warnings, list)
    for warning in warnings:
        print(f"Warning: {warning}")
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
