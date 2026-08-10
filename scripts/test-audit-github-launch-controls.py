#!/usr/bin/env python3
"""Regression checks for GitHub launch-control evidence parsing."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import subprocess
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "scripts/audit-github-launch-controls.py"
SPEC = importlib.util.spec_from_file_location(
    "vyral_audit_github_launch_controls", MODULE_PATH
)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Unable to load the GitHub launch-control audit.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def main() -> int:
    expected_checks = {
        "Analyze csharp",
        "Analyze go",
        "Analyze javascript-typescript",
        "Analyze python",
        "Build and test .NET",
        "Review dependency changes",
        "Test language clients",
        "Verify contracts and public export",
        "Verify releasable artifacts",
    }
    if MODULE.REQUIRED_CHECK_CONTEXTS != expected_checks:
        raise SystemExit("Required checks must use GitHub's emitted job contexts.")
    if not MODULE._contains_required_checks(expected_checks | {"Optional check"}):
        raise SystemExit("The complete hosted-check set must satisfy the audit.")
    if MODULE._contains_required_checks(expected_checks - {"Analyze go"}):
        raise SystemExit("The audit accepted an incomplete CodeQL matrix.")

    response = subprocess.CompletedProcess(
        args=("gh", "api"),
        returncode=0,
        stdout=(
            "HTTP/2.0 204 No Content\n"
            "Github-Authentication-Token-Expiration: 2099-01-01\n"
            "X-Oauth-Scopes: repo, workflow\n"
        ),
        stderr="",
    )
    with patch.object(MODULE, "_run", return_value=response):
        status, payload = MODULE._api("repos/example/vulnerability-alerts")
    if status != 204 or payload is not None:
        raise SystemExit("A 204 response must not retain response headers.")

    error = subprocess.CompletedProcess(
        args=("gh", "api"),
        returncode=1,
        stdout=(
            "HTTP/2.0 403 Forbidden\n"
            "X-Oauth-Scopes: repo\n\n"
            '{"message":"Resource not accessible"}\n'
        ),
        stderr="",
    )
    with patch.object(MODULE, "_run", return_value=error):
        status, payload = MODULE._api("repos/example/protection")
    if status != 403 or payload != {"message": "Resource not accessible"}:
        raise SystemExit("An error response must retain only its JSON body.")

    print("github-launch-controls-parser-test=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
