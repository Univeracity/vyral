#!/usr/bin/env python3
"""Verify the unauthenticated ``extro-sim`` surface used by Vyral.

This deliberately does not construct a client, authenticate, or create a
provider job. It detects a dependency-line change before a live Extropic
verification would consume credits.
"""

from __future__ import annotations

import importlib
import inspect
import json
from typing import Any


def _parameters(value: Any, label: str, *required: str) -> None:
    try:
        parameters = inspect.signature(value).parameters
    except (TypeError, ValueError) as exc:
        raise SystemExit(f"{label} does not expose an inspectable signature.") from exc
    missing = [name for name in required if name not in parameters]
    if missing:
        raise SystemExit(f"{label} is missing parameter(s): {', '.join(missing)}.")


def _type(sdk: Any, name: str) -> type[BaseException]:
    value = getattr(sdk, name, None)
    if not isinstance(value, type) or not issubclass(value, BaseException):
        raise SystemExit(f"extro_sim.{name} is not an exception type.")
    return value


def main() -> int:
    try:
        sdk = importlib.import_module("extro_sim")
    except ImportError as exc:
        raise SystemExit(
            'Install the optional compatibility line first: pip install -e "runtimes/python[extropic]"'
        ) from exc

    version = str(getattr(sdk, "__version__", ""))
    if not version.startswith("0.5."):
        raise SystemExit(
            "Vyral's Extropic adapter requires extro-sim 0.5.x; "
            f"found {version or 'an unversioned module'}."
        )

    client = getattr(sdk, "Client", None)
    job = getattr(sdk, "Job", None)
    if not isinstance(client, type) or not isinstance(job, type):
        raise SystemExit("extro_sim must expose Client and Job classes.")

    _parameters(client, "extro_sim.Client", "token")
    _parameters(client.create_job, "extro_sim.Client.create_job", "self", "tier")
    _parameters(
        client.upload_input,
        "extro_sim.Client.upload_input",
        "self",
        "path",
        "data",
        "token",
        "artifact_url",
    )
    _parameters(client.start_job, "extro_sim.Client.start_job", "self", "job_id", "manifest")
    _parameters(client.get, "extro_sim.Client.get", "self", "job_id")
    _parameters(client.cancel, "extro_sim.Client.cancel", "self", "job_id")
    _parameters(
        client.download_artifact,
        "extro_sim.Client.download_artifact",
        "self",
        "job_id",
        "name",
    )
    _parameters(job, "extro_sim.Job", "id", "client")
    _parameters(job.result, "extro_sim.Job.result", "self")

    base_error = _type(sdk, "ExtropicError")
    for name in ("RateLimited", "OutOfCredits", "AtCapacity"):
        if not issubclass(_type(sdk, name), base_error):
            raise SystemExit(f"extro_sim.{name} no longer derives from ExtropicError.")

    print(
        json.dumps(
            {
                "schema": "vyral.extropic.sdk-surface.v1",
                "authOrProviderJobCreated": False,
                "extroSimVersion": version,
                "requiredClientMethods": [
                    "create_job",
                    "upload_input",
                    "start_job",
                    "get",
                    "cancel",
                    "download_artifact",
                ],
            },
            sort_keys=True,
            separators=(",", ":"),
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
