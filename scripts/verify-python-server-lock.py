#!/usr/bin/env python3
"""Verify the committed, hash-verified Python server deployment profile."""

from __future__ import annotations

import sys
import tomllib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "runtimes" / "python"
INPUT = RUNTIME / "requirements-server.in"
LOCK = RUNTIME / "requirements-server.lock"


def fail(message: str) -> None:
    raise SystemExit(f"python-server-lock: {message}")


def main() -> int:
    project = tomllib.loads((RUNTIME / "pyproject.toml").read_text(encoding="utf-8"))
    version = project["project"]["version"]
    server_dependencies = project["project"]["optional-dependencies"]["server"]
    if not any(dependency.startswith("uvicorn>=") for dependency in server_dependencies):
        fail("the server extra must retain an explicit uvicorn compatibility range")

    source = INPUT.read_text(encoding="utf-8")
    expected_input = f"vyral[server]=={version}"
    if expected_input not in source.splitlines():
        fail(f"requirements-server.in must pin {expected_input}")

    lock = LOCK.read_text(encoding="utf-8")
    if "--generate-hashes" not in lock:
        fail("requirements-server.lock must document hash generation")
    expected_packages = {"click", "h11", "uvicorn", "vyral"}
    hashed: dict[str, int] = {}
    lines = lock.splitlines()
    for index, line in enumerate(lines):
        if "==" not in line or not line.endswith(" \\"):
            continue
        name, _ = line.split("==", 1)
        if name not in expected_packages:
            continue
        hash_count = 0
        for following in lines[index + 1 :]:
            if not following.startswith("    "):
                break
            if following.strip().startswith("--hash=sha256:"):
                hash_count += 1
        hashed[name] = hash_count

    if set(hashed) != expected_packages:
        fail(f"lock entries must all be hash-verified; found {sorted(hashed)!r}")
    if not any(line == f"vyral=={version} \\" for line in lines):
        fail(f"requirements-server.lock must pin vyral=={version}")
    for name, hash_count in hashed.items():
        if hash_count < 2:
            fail(f"{name} must retain wheel and source hashes")

    print(f"python-server-lock=ok version={version} packages={len(hashed)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
