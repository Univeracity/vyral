#!/usr/bin/env python3
"""Synchronize canonical Vyral contracts into the self-contained Python runtime package."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "runtimes/python/src/vyral_runtime"
CONTRACT_TARGET = PACKAGE / "_contracts"
CONFORMANCE_SOURCE = ROOT / "conformance/runtime/v1"
CONFORMANCE_TARGET = PACKAGE / "_conformance/runtime/v1"

CONTRACT_FILES = {
    ROOT / "contracts/public-sdk-surface.json": CONTRACT_TARGET / "public-sdk-surface.json",
    ROOT / "contracts/schemas/vyral-public.schema.json": CONTRACT_TARGET / "vyral-public.schema.json",
    ROOT / "src/Vyral.Server/contracts/vyral.openapi.json": CONTRACT_TARGET / "vyral.openapi.json",
}


def expected_files() -> dict[Path, bytes]:
    files = {target: source.read_bytes() for source, target in CONTRACT_FILES.items()}
    for source in sorted(CONFORMANCE_SOURCE.rglob("*.json")):
        target = CONFORMANCE_TARGET / source.relative_to(CONFORMANCE_SOURCE)
        files[target] = source.read_bytes()
    return files


def stale_generated_files(expected: set[Path]) -> list[Path]:
    candidates = set(CONTRACT_TARGET.glob("*.json"))
    if CONFORMANCE_TARGET.exists():
        candidates.update(CONFORMANCE_TARGET.rglob("*.json"))
    return sorted(candidates - expected)


def check(files: dict[Path, bytes]) -> list[str]:
    errors: list[str] = []
    for target, expected in files.items():
        if not target.is_file():
            errors.append(f"missing generated runtime resource: {target.relative_to(ROOT)}")
            continue
        if target.read_bytes() != expected:
            errors.append(f"stale generated runtime resource: {target.relative_to(ROOT)}")
    for target in stale_generated_files(set(files)):
        errors.append(f"unexpected generated runtime resource: {target.relative_to(ROOT)}")
    return errors


def write(files: dict[Path, bytes]) -> None:
    for target in stale_generated_files(set(files)):
        target.unlink()
    for target, content in files.items():
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write",
        action="store_true",
        help="Write canonical source bytes into the Python runtime package.",
    )
    args = parser.parse_args()

    files = expected_files()
    if args.write:
        write(files)

    errors = check(files)
    if errors:
        raise SystemExit(
            "Python runtime contract synchronization failed:\n- " + "\n- ".join(errors)
        )
    print(f"python-runtime-contracts=ok resources={len(files)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
