#!/usr/bin/env python3
"""Verify clean wheel/sdist installation on the current Python platform."""

from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
import venv


SMOKE = r"""
from vyral_runtime import (
    VyralRuntime,
    run_bundled_canonical_scenario,
    run_bundled_external_worker_scenario,
    run_bundled_goldens,
    run_bundled_native_execution_scenario,
    run_bundled_record_store_scenario,
)

runtime = VyralRuntime()
readiness = runtime.readiness()
assert readiness.status == "ok"
assert readiness.full_local_ready is False
assert readiness.contract is not None
assert readiness.contract.operation_count == 129
assert readiness.contract.rest_operation_count == 133
assert readiness.contract.schema_count == 263
assert len(run_bundled_goldens()) == 12
assert len(run_bundled_record_store_scenario()) == 17
assert len(run_bundled_external_worker_scenario()) == 3
assert len(run_bundled_canonical_scenario()) == 6
assert len(run_bundled_native_execution_scenario()) == 7
assert all(profile.available for profile in readiness.profiles)
"""


def _interpreter(environment: Path) -> Path:
    if sys.platform == "win32":
        return environment / "Scripts" / "python.exe"
    return environment / "bin" / "python"


def _command(*arguments: str) -> None:
    subprocess.run(arguments, check=True)


def _install_and_smoke(artifact: Path, destination: Path) -> None:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    _command(
        str(python),
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        "--no-deps",
        str(artifact),
    )
    _command(str(python), "-c", SMOKE)


def _server_smoke(wheel: Path, destination: Path) -> None:
    venv.EnvBuilder(with_pip=True, clear=True).create(destination)
    python = _interpreter(destination)
    _command(
        str(python),
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        f"{wheel}[server]",
    )
    _command(
        str(python),
        "-m",
        "vyral_runtime.host",
        "--help",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "artifact_directory",
        type=Path,
        help="Directory containing exactly one runtime wheel and sdist.",
    )
    parser.add_argument(
        "--server",
        action="store_true",
        help="Also install the server extra and exercise the CLI.",
    )
    arguments = parser.parse_args()
    root = arguments.artifact_directory.resolve()
    wheels = sorted(root.glob("vyral_runtime-*.whl"))
    sdists = sorted(root.glob("vyral_runtime-*.tar.gz"))
    if len(wheels) != 1 or len(sdists) != 1:
        parser.error(
            "artifact directory must contain exactly one runtime wheel and sdist"
        )

    with tempfile.TemporaryDirectory(
        prefix="vyral-runtime-install-"
    ) as temporary:
        destination = Path(temporary)
        _install_and_smoke(wheels[0], destination / "wheel")
        _install_and_smoke(sdists[0], destination / "sdist")
        if arguments.server:
            _server_smoke(wheels[0], destination / "server")

    print(
        "python-runtime-clean-install=ok "
        f"python={sys.version_info.major}.{sys.version_info.minor} "
        f"platform={sys.platform}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
