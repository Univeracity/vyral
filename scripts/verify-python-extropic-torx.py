#!/usr/bin/env python3
"""Prove a real Torx workload locally without creating a provider job."""

from __future__ import annotations

import base64
from datetime import datetime, timezone
from hashlib import sha256
import importlib.metadata
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
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
from vyral_runtime.integrations.extropic import (  # noqa: E402
    ExtropicSdkBackend,
)


EXPECTED_DISTRIBUTION = [0.5, 0.0, 0.25, 0.25]


def _verify_result(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise SystemExit("Torx proof returned a non-object result.")
    distribution = value.get("distribution")
    if not isinstance(distribution, list) or len(distribution) != 4:
        raise SystemExit("Torx proof returned an invalid distribution.")
    if any(
        isinstance(item, bool) or not isinstance(item, (int, float))
        for item in distribution
    ):
        raise SystemExit("Torx proof returned a non-numeric distribution.")
    selected = [float(item) for item in distribution]
    if any(
        not math.isclose(actual, expected, rel_tol=0, abs_tol=1e-6)
        for actual, expected in zip(selected, EXPECTED_DISTRIBUTION)
    ):
        raise SystemExit(f"Torx proof distribution changed: {selected}")
    if not math.isclose(sum(selected), 1.0, rel_tol=0, abs_tol=1e-6):
        raise SystemExit("Torx proof distribution is not normalized.")
    if value.get("seed") != EXAMPLE_PAYLOAD["seed"]:
        raise SystemExit("Torx proof did not preserve its explicit seed.")
    return value


def _prepared_pickle() -> tuple[bytes, dict[str, Any], int]:
    prepared = ExtropicSdkBackend(object()).prepare(
        torx_density_workload,
        EXAMPLE_PAYLOAD,
        60,
    )
    submission = json.loads(prepared.payload)
    if submission.get("entrypoint") != "__extropic_cloudpickle__":
        raise SystemExit("Torx proof used an unexpected Extropic entrypoint.")
    envelope = json.loads(base64.b64decode(submission["code"]))
    encoded = base64.b64decode(envelope["pickle"])
    return encoded, dict(prepared.manifest), len(prepared.payload)


def _run_in_clean_process(encoded: bytes) -> dict[str, Any]:
    runner = """
import base64
import cloudpickle
import importlib.util
import json
import sys

message = json.load(sys.stdin)
if importlib.util.find_spec("extropic_torx_workload") is not None:
    raise SystemExit("application example module unexpectedly importable")
workload, args, kwargs = cloudpickle.loads(base64.b64decode(message["pickle"]))
print(json.dumps({
    "module": workload.__module__,
    "result": workload(*args, **kwargs),
}, sort_keys=True))
"""
    environment = os.environ.copy()
    environment.pop("PYTHONPATH", None)
    environment["PYTHONNOUSERSITE"] = "1"
    with tempfile.TemporaryDirectory(prefix="vyral-extropic-torx-") as directory:
        completed = subprocess.run(
            [sys.executable, "-c", runner],
            input=json.dumps(
                {"pickle": base64.b64encode(encoded).decode("ascii")}
            ),
            text=True,
            capture_output=True,
            cwd=directory,
            env=environment,
            timeout=120,
            check=False,
        )
    if completed.returncode != 0:
        detail = completed.stderr.strip()[-1000:]
        raise SystemExit(f"Clean-process Torx proof failed: {detail}")
    response = json.loads(completed.stdout)
    if response.get("module") != "__main__":
        raise SystemExit("Torx workload was not serialized by value.")
    return _verify_result(response.get("result"))


def main() -> int:
    if sys.version_info < (3, 11):
        raise SystemExit("The Torx proof requires Python 3.11 or newer.")

    import jax
    import extro_sim

    torx_version = importlib.metadata.version("extro-torx")
    if torx_version != "0.0.1":
        raise SystemExit(
            "The Torx proof requires extro-torx 0.0.1, got "
            f"{torx_version}."
        )
    if not jax.__version__.startswith("0.11."):
        raise SystemExit(f"The Torx proof requires JAX 0.11.x, got {jax.__version__}.")
    if not extro_sim.__version__.startswith("0.5."):
        raise SystemExit(
            "The Torx proof requires extro-sim 0.5.x, got "
            f"{extro_sim.__version__}."
        )

    local_result = _verify_result(torx_density_workload(EXAMPLE_PAYLOAD))
    encoded, manifest, submission_bytes = _prepared_pickle()
    sandbox_result = _run_in_clean_process(encoded)

    receipt = {
        "schema": "vyral.extropic.torx-local-proof.v1",
        "recordedAtUtc": datetime.now(timezone.utc).isoformat().replace(
            "+00:00", "Z"
        ),
        "workloadId": WORKLOAD_ID,
        "providerJobCreated": False,
        "applicationModuleRequired": False,
        "pythonVersion": "%d.%d" % sys.version_info[:2],
        "extroSimVersion": extro_sim.__version__,
        "extroTorxVersion": torx_version,
        "jaxVersion": jax.__version__,
        "jaxBackend": jax.default_backend(),
        "submissionBytes": submission_bytes,
        "payloadSha256": "sha256:"
        + sha256(
            json.dumps(
                EXAMPLE_PAYLOAD,
                sort_keys=True,
                separators=(",", ":"),
            ).encode()
        ).hexdigest(),
        "manifest": manifest,
        "localResult": local_result,
        "cleanProcessResult": sandbox_result,
    }
    print(json.dumps(receipt, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
