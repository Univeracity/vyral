#!/usr/bin/env python3
"""Read-only audit of GitHub controls required for a public Vyral launch."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
REQUIRED_CHECK_CONTEXTS = {
    "Build and test .NET",
    "Test language clients",
    "Verify contracts and public export",
    "Analyze csharp",
    "Analyze javascript-typescript",
    "Analyze python",
    "Analyze go",
    "Review dependency changes",
    "Verify releasable artifacts",
}


def _run(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        arguments,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


def _repository_from_remote(remote: str) -> str:
    value = remote.strip()
    match = re.search(
        r"(?:github\.com[:/])(?P<repo>[^/\s]+/[^/\s]+?)(?:\.git)?$",
        value,
    )
    if match is None:
        raise ValueError(
            "The selected Git remote is not a GitHub owner/repository URL."
        )
    return match.group("repo")


def _api(path: str) -> tuple[int, Any]:
    result = _run(
        "gh",
        "api",
        "--include",
        path,
    )
    combined = result.stdout
    header, separator, body = combined.partition("\r\n\r\n")
    if not separator:
        header, separator, body = combined.partition("\n\n")
    status_match = re.search(r"^HTTP/\S+\s+(\d{3})", header, re.MULTILINE)
    status = (
        int(status_match.group(1))
        if status_match is not None
        else (200 if result.returncode == 0 else 0)
    )
    if status in {204, 205}:
        return status, None
    try:
        payload: Any = json.loads(body or result.stdout or "null")
    except json.JSONDecodeError:
        payload = {
            # Never retain response headers in evidence. Besides being noisy,
            # they can reveal account-level authentication metadata.
            "message": (result.stderr or body).strip()
        }
    return status, payload


def _control(
    name: str,
    passed: bool,
    *,
    state: str,
    evidence: Any,
) -> dict[str, Any]:
    return {
        "name": name,
        "status": "passed" if passed else state,
        "evidence": evidence,
    }


def _required_checks(protection: Any) -> set[str]:
    if not isinstance(protection, dict):
        return set()
    required = protection.get("required_status_checks")
    if not isinstance(required, dict):
        return set()
    checks = required.get("checks", [])
    contexts = required.get("contexts", [])
    output = {
        str(item.get("context"))
        for item in checks
        if isinstance(item, dict) and item.get("context")
    }
    output.update(str(item) for item in contexts if item)
    return output


def _contains_required_checks(actual: set[str]) -> bool:
    return REQUIRED_CHECK_CONTEXTS.issubset(actual)


def audit(repository: str) -> dict[str, Any]:
    repo_status, repo = _api(f"repos/{repository}")
    if repo_status != 200 or not isinstance(repo, dict):
        raise RuntimeError(
            f"Unable to read GitHub repository {repository!r}: {repo}"
        )
    branch = str(repo.get("default_branch") or "")
    controls: list[dict[str, Any]] = []
    controls.append(
        _control(
            "public_repository",
            repo.get("visibility") == "public",
            state="pending",
            evidence={
                "visibility": repo.get("visibility"),
                "private": repo.get("private"),
            },
        )
    )

    protection_status, protection = _api(
        f"repos/{repository}/branches/{branch}/protection"
    )
    checks = _required_checks(protection)
    protection_unavailable = protection_status in {403, 404}
    controls.append(
        _control(
            "protected_default_branch",
            protection_status == 200,
            state=(
                "unavailable"
                if protection_unavailable
                else "pending"
            ),
            evidence={
                "branch": branch,
                "httpStatus": protection_status,
                "message": (
                    protection.get("message")
                    if isinstance(protection, dict)
                    else None
                ),
            },
        )
    )
    controls.append(
        _control(
            "required_release_checks",
            protection_status == 200 and _contains_required_checks(checks),
            state=(
                "unavailable"
                if protection_unavailable
                else "pending"
            ),
            evidence={
                "configured": sorted(checks),
                "requiredContexts": sorted(REQUIRED_CHECK_CONTEXTS),
            },
        )
    )

    reporting_status, reporting = _api(
        f"repos/{repository}/private-vulnerability-reporting"
    )
    reporting_enabled = (
        reporting_status == 200
        and isinstance(reporting, dict)
        and reporting.get("enabled") is True
    )
    controls.append(
        _control(
            "private_vulnerability_reporting",
            reporting_enabled,
            state=(
                "unavailable"
                if reporting_status in {403, 404}
                else "pending"
            ),
            evidence={
                "httpStatus": reporting_status,
                "enabled": (
                    reporting.get("enabled")
                    if isinstance(reporting, dict)
                    else None
                ),
            },
        )
    )

    vulnerability_status, vulnerability_alerts = _api(
        f"repos/{repository}/vulnerability-alerts"
    )
    vulnerability_alerts_enabled = vulnerability_status == 204
    # GitHub's enable-vulnerability-alerts endpoint also enables the dependency
    # graph. The repository response can omit security_and_analysis for private
    # personal repositories, so that field alone is not reliable evidence.
    for name in ("dependency_graph", "dependabot_alerts"):
        controls.append(
            _control(
                name,
                vulnerability_alerts_enabled,
                state=(
                    "unavailable"
                    if vulnerability_status in {403, 404}
                    else "pending"
                ),
                evidence={
                    "httpStatus": vulnerability_status,
                    "enabledBy": "vulnerability-alerts",
                    "message": (
                        vulnerability_alerts.get("message")
                        if isinstance(vulnerability_alerts, dict)
                        else None
                    ),
                },
            )
        )

    security = repo.get("security_and_analysis")
    security = security if isinstance(security, dict) else {}
    for name, field in (
        ("secret_scanning", "secret_scanning"),
        (
            "secret_scanning_push_protection",
            "secret_scanning_push_protection",
        ),
    ):
        value = security.get(field)
        enabled = (
            isinstance(value, dict) and value.get("status") == "enabled"
        )
        controls.append(
            _control(
                name,
                enabled,
                state="unavailable" if not security else "pending",
                evidence=value,
            )
        )

    workflow_status, workflow = _api(
        f"repos/{repository}/actions/workflows/ci.yml"
    )
    workflow_active = (
        workflow_status == 200
        and isinstance(workflow, dict)
        and workflow.get("state") == "active"
    )
    controls.append(
        _control(
            "baseline_ci_active",
            workflow_active,
            state="pending",
            evidence={
                "httpStatus": workflow_status,
                "state": (
                    workflow.get("state")
                    if isinstance(workflow, dict)
                    else None
                ),
            },
        )
    )

    passed = all(item["status"] == "passed" for item in controls)
    return {
        "schemaVersion": "vyral.github-launch-controls.v1",
        "generatedAtUtc": datetime.now(timezone.utc)
        .isoformat()
        .replace("+00:00", "Z"),
        "repository": repository,
        "defaultBranch": branch,
        "status": "passed" if passed else "incomplete",
        "controls": controls,
        "operatorOnly": [
            "registry trusted-publisher configuration",
            "release-environment approvals",
            "public-repository creation or visibility change",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", help="GitHub owner/repository")
    parser.add_argument("--remote", default="origin")
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--allow-incomplete",
        action="store_true",
        help="Write an evidence snapshot without failing on pending controls.",
    )
    arguments = parser.parse_args()
    repository = arguments.repository
    if repository is None:
        remote = _run(
            "git", "remote", "get-url", arguments.remote
        )
        if remote.returncode != 0:
            parser.error(remote.stderr.strip() or "Git remote is unavailable.")
        try:
            repository = _repository_from_remote(remote.stdout)
        except ValueError as exc:
            parser.error(str(exc))
    try:
        result = audit(repository)
    except RuntimeError as exc:
        parser.error(str(exc))
    rendered = json.dumps(result, indent=2, sort_keys=True) + "\n"
    if arguments.output is not None:
        output = arguments.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered, encoding="utf-8")
    else:
        print(rendered, end="")
    return (
        0
        if result["status"] == "passed" or arguments.allow_incomplete
        else 1
    )


if __name__ == "__main__":
    raise SystemExit(main())
