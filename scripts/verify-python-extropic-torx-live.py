#!/usr/bin/env python3
"""Run one explicit, credit-consuming Torx workload on Extropic's L4 tier."""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone
import importlib.metadata
import json
import math
import os
from pathlib import Path
import secrets
import sys
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
RUNTIME_SOURCE = ROOT / "runtimes" / "python" / "src"
EXAMPLES = ROOT / "examples" / "python"
sys.path.insert(0, str(RUNTIME_SOURCE))
sys.path.insert(0, str(EXAMPLES))

from extropic_torx_workload import (  # noqa: E402
    EXAMPLE_PAYLOAD,
    WORKLOAD_ID,
    torx_density_workload,
)
from vyral_runtime import (  # noqa: E402
    DelegateExecutionHandler,
    ExecutionHandlerDescriptor,
    ExecutionHandlerHarness,
    ExecutionPluginDescriptor,
    StaticExecutionPlugin,
)
from vyral_runtime.integrations.extropic import (  # noqa: E402
    ExtropicAdapterOptions,
    ExtropicExecutionAdapter,
    ExtropicSdkBackend,
)


def _result_verified(value: Any) -> bool:
    if not isinstance(value, dict) or value.get("seed") != 7:
        return False
    distribution = value.get("distribution")
    if not isinstance(distribution, list) or len(distribution) != 4:
        return False
    expected = [0.5, 0.0, 0.25, 0.25]
    try:
        selected = [float(item) for item in distribution]
        normalization = float(value.get("normalization"))
    except (TypeError, ValueError):
        return False
    return all(
        math.isclose(actual, target, rel_tol=0, abs_tol=1e-6)
        for actual, target in zip(selected, expected)
    ) and math.isclose(normalization, 1.0, rel_tol=0, abs_tol=1e-6)


async def run() -> dict[str, Any]:
    try:
        import extro_sim
    except ImportError as exc:
        raise SystemExit(
            "Install the optional extra first: "
            'pip install -e "runtimes/python[extropic-torx]"'
        ) from exc

    token = os.environ.get("EXTROPIC_TOKEN", "").strip()
    if not token:
        raise SystemExit(
            "EXTROPIC_TOKEN is required; the live gate never starts browser login."
        )

    client = extro_sim.Client(token=token)
    adapter = ExtropicExecutionAdapter(
        WORKLOAD_ID,
        torx_density_workload,
        options=ExtropicAdapterOptions(
            tier="l4",
            timeout_seconds=60,
            poll_interval_seconds=1,
            capacity_retry_attempts=3,
            provider_error_retries=3,
            max_serialized_bytes=1024 * 1024,
            max_artifact_bytes=128 * 1024,
            require_seed=True,
        ),
        backend=ExtropicSdkBackend(client),
    )
    descriptor = ExecutionHandlerDescriptor(
        handler_id="vyral.preview.extropic.torx-density",
        plugin_id="vyral.preview.extropic.torx",
        display_name="Extropic Torx live proof",
        max_attempts=1,
    )
    plugin = StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=descriptor.plugin_id,
            name="Vyral Extropic Torx preview",
            version="0.1.0",
        ),
        (DelegateExecutionHandler(descriptor, adapter.execute),),
    )
    marker = "vyral-torx-live-" + secrets.token_hex(8)
    completed = await ExecutionHandlerHarness(plugin).run(
        descriptor.handler_id,
        payload=EXAMPLE_PAYLOAD,
        run_id=marker,
    )
    details = completed.status_details or {}
    provider_job_id = details.get("providerJobId")
    if completed.status != "succeeded" or not _result_verified(completed.result):
        if isinstance(provider_job_id, str) and provider_job_id:
            try:
                client.cancel(provider_job_id)
            except Exception:
                pass
        raise SystemExit(
            "Extropic Torx live proof failed safely: "
            f"status={completed.status} failureClass={completed.failure_class} "
            f"providerJobId={provider_job_id or 'unknown'}"
        )
    if not isinstance(provider_job_id, str) or not provider_job_id:
        raise SystemExit("Extropic Torx proof completed without a provider job id.")

    final_provider_status = str(client.get(provider_job_id).get("status", ""))
    return {
        "schema": "vyral.extropic.torx-live-proof.v1",
        "recordedAtUtc": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "vyralRunId": completed.id,
        "provider": "extropic",
        "providerSdkVersion": extro_sim.__version__,
        "extroTorxVersion": importlib.metadata.version("extro-torx"),
        "providerJobId": provider_job_id,
        "providerStatus": final_provider_status,
        "tier": "l4",
        "timeoutSeconds": 60,
        "resultVerified": True,
    }


def main() -> int:
    if os.environ.get("VYRAL_EXTROPIC_TORX_LIVE") != "1":
        raise SystemExit(
            "Refusing credit-consuming work. Set "
            "VYRAL_EXTROPIC_TORX_LIVE=1 explicitly."
        )
    print(json.dumps(asyncio.run(run()), sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
