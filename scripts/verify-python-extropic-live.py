#!/usr/bin/env python3
"""Run one explicit, bounded, credit-consuming Extropic integration smoke."""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import secrets
import sys
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
RUNTIME_SOURCE = ROOT / "runtimes" / "python" / "src"
sys.path.insert(0, str(RUNTIME_SOURCE))

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


def live_probe(payload: Any) -> dict[str, Any]:
    """Dependency-free remote function with an exact, inspectable result."""

    if not isinstance(payload, dict):
        raise TypeError("probe payload must be an object")
    return {
        "marker": payload["marker"],
        "seed": payload["seed"],
        "value": int(payload["value"]) * 2,
    }


async def run() -> dict[str, Any]:
    try:
        import extro_sim
    except ImportError as exc:
        raise SystemExit(
            'Install the optional extra first: pip install -e "runtimes/python[extropic]"'
        ) from exc

    token = os.environ.get("EXTROPIC_TOKEN", "").strip()
    if not token:
        raise SystemExit(
            "EXTROPIC_TOKEN is required; the live gate never starts browser login."
        )

    client = extro_sim.Client(token=token)
    backend = ExtropicSdkBackend(client)
    adapter = ExtropicExecutionAdapter(
        "vyral.qualification.extropic.cpu-probe.v1",
        live_probe,
        options=ExtropicAdapterOptions(
            tier="cpu",
            timeout_seconds=30,
            poll_interval_seconds=1,
            capacity_retry_attempts=3,
            provider_error_retries=3,
            max_serialized_bytes=1024 * 1024,
            max_artifact_bytes=64 * 1024,
            require_seed=True,
        ),
        backend=backend,
    )
    descriptor = ExecutionHandlerDescriptor(
        handler_id="vyral.qualification.extropic.cpu-probe",
        plugin_id="vyral.qualification.extropic",
        display_name="Extropic CPU live probe",
        max_attempts=1,
    )
    plugin = StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id="vyral.qualification.extropic",
            name="Vyral Extropic qualification",
            version="0.1.0",
        ),
        (DelegateExecutionHandler(descriptor, adapter.execute),),
    )
    harness = ExecutionHandlerHarness(plugin)
    marker = "vyral-live-" + secrets.token_hex(8)
    payload = {"marker": marker, "seed": 1729, "value": 21}
    completed = await harness.run(
        descriptor.handler_id,
        payload=payload,
        run_id=marker,
    )
    details = completed.status_details or {}
    provider_job_id = details.get("providerJobId")
    if completed.status != "succeeded" or completed.result != {
        "marker": marker,
        "seed": 1729,
        "value": 42,
    }:
        if isinstance(provider_job_id, str) and provider_job_id:
            try:
                client.cancel(provider_job_id)
            except Exception:
                pass
        raise SystemExit(
            "Extropic live smoke failed safely: "
            f"status={completed.status} failureClass={completed.failure_class} "
            f"providerJobId={provider_job_id or 'unknown'}"
        )

    if not isinstance(provider_job_id, str) or not provider_job_id:
        raise SystemExit("Extropic live smoke completed without a provider job id.")
    final_provider_status = str(client.get(provider_job_id).get("status", ""))
    artifacts = await harness.transport.get_artifacts(completed.id)
    return {
        "schema": "vyral.extropic.live-smoke.v1",
        "recordedAtUtc": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "vyralRunId": completed.id,
        "provider": "extropic",
        "providerSdkVersion": extro_sim.__version__,
        "providerJobId": provider_job_id,
        "providerStatus": final_provider_status,
        "tier": "cpu",
        "timeoutSeconds": 30,
        "resultVerified": True,
        "artifactNames": [artifact.name for artifact in artifacts],
        "artifactBytes": sum(artifact.size_bytes for artifact in artifacts),
    }


def main() -> int:
    if os.environ.get("VYRAL_EXTROPIC_LIVE") != "1":
        raise SystemExit(
            "Refusing credit-consuming work. Set VYRAL_EXTROPIC_LIVE=1 explicitly."
        )
    receipt = asyncio.run(run())
    print(json.dumps(receipt, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
