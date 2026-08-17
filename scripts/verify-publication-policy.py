#!/usr/bin/env python3
"""Verify that first-cohort publishing is manual, bounded, and fail-closed."""

from __future__ import annotations

import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
AUTOMATION_GATE = (
    "github.event_name == 'workflow_dispatch' || "
    "vars.VYRAL_ENABLE_AUTOMATED_WORKFLOWS == 'true'"
)
GATED_WORKFLOWS = (
    "codeql.yml",
    "container-security.yml",
    "dependency-review.yml",
    "execution-runtime-consumer.yml",
    "release-integrity.yml",
    "temporal-container-qualification.yml",
)
PUBLISH_WORKFLOW = "publish-first-cohort.yml"
PUBLISH_PATTERNS = {
    "JavaScript package publish": re.compile(
        r"\b(?:npm\s+publish|pnpm\s+publish|yarn\s+npm\s+publish)\b",
        re.IGNORECASE,
    ),
    "NuGet push": re.compile(
        r"\b(?:dotnet\s+nuget|nuget)\s+push\b", re.IGNORECASE
    ),
    "Twine upload": re.compile(r"\btwine\s+upload\b", re.IGNORECASE),
    "Cargo publish": re.compile(r"\bcargo\s+publish\b", re.IGNORECASE),
    "Docker push": re.compile(
        r"\bdocker\s+push\b|\bdocker\s+buildx\s+build\b[\s\S]{0,2000}?--push\b",
        re.IGNORECASE,
    ),
    "OCI or Helm push": re.compile(r"\b(?:oras|helm)\s+push\b", re.IGNORECASE),
    "GitHub release creation": re.compile(
        r"\bgh\s+release\s+create\b", re.IGNORECASE
    ),
    "registry or release publishing action": re.compile(
        r"(?:pypa/gh-action-pypi-publish|docker/build-push-action|"
        r"softprops/action-gh-release|ncipollo/release-action|actions/create-release)@",
        re.IGNORECASE,
    ),
}


def _read(path: Path) -> str:
    if not path.is_file():
        raise SystemExit(f"Publication policy input is missing: {path.relative_to(ROOT)}")
    return path.read_text(encoding="utf-8")


def main() -> int:
    errors: list[str] = []
    workflow_text: dict[str, str] = {}
    for path in sorted(
        set(WORKFLOWS.glob("*.yml")) | set(WORKFLOWS.glob("*.yaml"))
    ):
        workflow_text[path.name] = _read(path)

    for name in GATED_WORKFLOWS:
        text = workflow_text.get(name)
        if text is None:
            errors.append(f"required manually gated workflow is missing: {name}")
        elif AUTOMATION_GATE not in text:
            errors.append(f"{name} no longer fails closed behind the automation gate")

    cohort = json.loads(
        _read(ROOT / "packaging" / "publication-cohort.json")
    )
    if cohort.get("publicationAuthorized") is not True:
        errors.append(
            "publication-cohort.json must explicitly authorize the reviewed "
            "first cohort"
        )
    publisher = workflow_text.get(PUBLISH_WORKFLOW)
    if publisher is None:
        errors.append("the authorized first-cohort publisher workflow is missing")
        publisher = ""
    for name, text in workflow_text.items():
        if name == PUBLISH_WORKFLOW:
            continue
        if re.search(
            r"VYRAL_ENABLE_AUTOMATED_WORKFLOWS\s*[:=]\s*['\"]?true\b",
            text,
            re.IGNORECASE,
        ):
            errors.append(f"{name} enables VYRAL_ENABLE_AUTOMATED_WORKFLOWS in source")
        for label, pattern in PUBLISH_PATTERNS.items():
            if pattern.search(text):
                errors.append(
                    f"{name} contains {label}; only {PUBLISH_WORKFLOW} may publish"
                )

    for requirement in (
        "workflow_dispatch:",
        "release_tag:",
        "v0.3.0",
        "confirm:",
        "type: boolean",
        "npm_bootstrap_complete:",
        "refs/heads/main",
        "git cat-file -t \"refs/tags/${RELEASE_TAG}\"",
        ".verification.verified == true",
        "release-integrity.yml/runs?head_sha=",
        "name: publish-nuget",
        "name: publish-pypi",
        "name: publish-npm",
        "name: publish-container",
        "node-version: \"22.14.0\"",
        "npm@11.5.1",
        "Publish or verify npm first cohort",
        "Verify the one-time direct bootstrap archive",
        'npm view vyral-client@0.3.0 dist.integrity',
        "NuGet/login@8d196754b4036150537f80ac539e15c2f1028841",
        "pypa/gh-action-pypi-publish@dc37677b2e1c63e2034f94d8a5b11f265b73ba33",
        "npm publish vyral-client-0.3.0.tgz --access public --provenance",
        "docker/build-push-action@53b7df96c91f9c12dcc8a07bcb9ccacbed38856a",
        "ghcr.io/univeracity/vyral-server:0.3.0",
    ):
        if requirement not in publisher:
            errors.append(
                f"{PUBLISH_WORKFLOW} is missing authorization hook {requirement!r}"
            )
    if re.search(r"^  push:\s*$", publisher, re.MULTILINE):
        errors.append(f"{PUBLISH_WORKFLOW} must not have an automatic push trigger")
    for label in (
        "JavaScript package publish",
        "NuGet push",
        "registry or release publishing action",
    ):
        pattern = PUBLISH_PATTERNS[label]
        if not pattern.search(publisher):
            errors.append(f"{PUBLISH_WORKFLOW} is missing expected {label}")
    if re.search(
        r"VYRAL_ENABLE_AUTOMATED_WORKFLOWS\s*[:=]\s*['\"]?true\b",
        publisher,
        re.IGNORECASE,
    ):
        errors.append(f"{PUBLISH_WORKFLOW} enables VYRAL_ENABLE_AUTOMATED_WORKFLOWS in source")

    release_workflow = workflow_text.get("release-integrity.yml", "")
    for evidence_hook in (
        "aquasec/trivy:0.73.0@sha256:",
        "execution-smoke-worker-build-metadata.json",
        "execution-smoke-worker.oci",
        "scripts/verify-mcp-container.sh",
        "scripts/verify-execution-smoke-container.sh",
        "scripts/verify-release-artifacts.sh",
        "oci-archive:/evidence/$archive",
        "vyral-server.oci",
        "vyral-server-build-metadata.json",
        "always() && github.event_name != 'pull_request'",
    ):
        if evidence_hook not in release_workflow:
            errors.append(
                f"release-integrity.yml is missing evidence hook {evidence_hook!r}"
            )

    canonical_gate = _read(ROOT / "scripts" / "verify-mcp-conformance.sh")
    for requirement in (
        'MCP_CONFORMANCE_VERSION="0.2.0-alpha.11"',
        '--requirements "$MCP_REQUIREMENTS_VERSION"',
        '--upstream-port "$SECONDARY_PORT"',
        '--phase one-instance-failover',
        '--phase two-instance-recovered',
        '"routingHeaderOwsObservationCount"',
        '"wireParseErrorCount"',
        '"socketErrorCount"',
    ):
        if requirement not in canonical_gate:
            errors.append(f"canonical MCP gate is missing {requirement!r}")

    container_gate = _read(ROOT / "scripts" / "verify-mcp-container.sh")
    for requirement in (
        "imageConfiguredUser",
        "archivedConfigDigest",
        "archivedArtifactDigest",
        "read-only-root-filesystem",
        "all-linux-capabilities-dropped",
        "no-new-privileges",
        "expected_tools",
        "expected_resources",
        '"Origin": "https://rebinding.invalid"',
        'document["result"].get("resultType") != "complete"',
    ):
        if requirement not in container_gate:
            errors.append(f"packaged MCP gate is missing {requirement!r}")

    worker_container_gate = _read(
        ROOT / "scripts" / "verify-execution-smoke-container.sh"
    )
    for requirement in (
        "verify-oci-image-identity.py",
        "imageConfiguredUser",
        "archivedConfigDigest",
        "archivedArtifactDigest",
        "read-only-root-filesystem",
        "all-linux-capabilities-dropped",
        "no-new-privileges",
    ):
        if requirement not in worker_container_gate:
            errors.append(
                f"packaged execution-smoke gate is missing {requirement!r}"
            )

    python_gate = _read(
        ROOT / "scripts" / "verify-python-runtime-mcp-conformance.sh"
    )
    python_has_frozen_requirements = all(
        requirement in python_gate
        for requirement in (
            '--requirements "$MCP_SPEC_VERSION"',
            "fullRequirements=true",
            "VYRAL_PYTHON_MCP_CONFORMANCE_ARTIFACT_DIR",
        )
    )
    if not python_has_frozen_requirements:
        errors.append(
            "Python MCP gate must run and retain the frozen requirements profile"
        )

    if errors:
        raise SystemExit("Publication policy failed:\n- " + "\n- ".join(errors))
    python_status = (
        "frozen-requirements"
        if python_has_frozen_requirements
        else "frozen-requirements-missing"
    )
    print(
        "publication-policy=ok mode=authorized-first-cohort "
        f"gatedWorkflows={len(GATED_WORKFLOWS)} "
        f"pythonRuntimeMcp={python_status}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
