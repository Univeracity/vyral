#!/usr/bin/env python3
"""Reject incompatible Durable Task minor-version mixtures in the Functions host."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_ASSETS = (
    ROOT
    / "samples/Vyral.Execution.AzureDurableFunctionsSmoke/obj/project.assets.json"
)
DURABLE_PACKAGES = {
    "Microsoft.DurableTask.Abstractions",
    "Microsoft.DurableTask.Client",
    "Microsoft.DurableTask.Client.Grpc",
    "Microsoft.DurableTask.Grpc",
    "Microsoft.DurableTask.Worker",
    "Microsoft.DurableTask.Worker.Grpc",
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "assets",
        nargs="?",
        type=Path,
        default=DEFAULT_ASSETS,
        help="Restored project.assets.json for the Azure Functions smoke host.",
    )
    arguments = parser.parse_args()
    assets = arguments.assets.resolve()
    if not assets.is_file():
        raise SystemExit(
            f"Azure Functions package graph is unavailable: {assets}. "
            "Restore the smoke-host project first."
        )

    document = json.loads(assets.read_text(encoding="utf-8"))
    target = document.get("targets", {}).get("net10.0")
    if not isinstance(target, dict):
        raise SystemExit("Azure Functions package graph has no net10.0 target.")

    resolved: dict[str, str] = {}
    for identity in target:
        name, separator, version = identity.rpartition("/")
        if separator and name in DURABLE_PACKAGES:
            resolved[name] = version

    missing = sorted(DURABLE_PACKAGES - resolved.keys())
    if missing:
        raise SystemExit(
            "Azure Functions package graph is missing Durable Task packages: "
            + ", ".join(missing)
        )

    release_lines: dict[tuple[int, int], list[str]] = {}
    for name, version in resolved.items():
        match = re.fullmatch(r"(\d+)\.(\d+)\.\d+(?:[-+].*)?", version)
        if match is None:
            raise SystemExit(
                f"Azure Functions package graph has an unrecognized version: "
                f"{name} {version}"
            )
        line = (int(match.group(1)), int(match.group(2)))
        release_lines.setdefault(line, []).append(f"{name} {version}")

    if len(release_lines) != 1:
        details = "; ".join(
            ", ".join(sorted(packages))
            for _, packages in sorted(release_lines.items())
        )
        raise SystemExit(
            "Azure Functions package graph mixes incompatible Durable Task "
            f"minor-version lines: {details}"
        )

    major, minor = next(iter(release_lines))
    print(
        "azure-durable-package-graph=ok "
        f"line={major}.{minor} packages={len(resolved)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
