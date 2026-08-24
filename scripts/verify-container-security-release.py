#!/usr/bin/env python3
"""Verify the narrowly authorized GHCR server-container security release."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "packaging" / "container-security-release.json"
WORKFLOW = ROOT / ".github" / "workflows" / "publish-container-security-patch.yml"

EXPECTED = {
    "schemaVersion": "vyral.container-security-release.v1",
    "publicationAuthorized": True,
    "image": {
        "name": "ghcr.io/univeracity/vyral-server",
        "version": "0.3.1",
        "source": "Dockerfile",
        "environment": "publish-container",
        "maturity": "mixed",
    },
    "authorization": {
        "mode": "manual-protected-environment",
        "releaseTag": "server-v0.3.1",
        "workflow": ".github/workflows/publish-container-security-patch.yml",
        "requirements": [
            "a GitHub-verified signed annotated server tag that resolves to current main",
            "a successful canonical Release Integrity push run for that exact commit",
            "a manual dispatch from main through the publish-container protected environment",
        ],
        "publisher": {
            "ecosystem": "container",
            "registry": "https://ghcr.io",
            "workflowFile": "publish-container-security-patch.yml",
            "environment": "publish-container",
            "authentication": "repository-scoped GitHub Actions GITHUB_TOKEN",
        },
    },
}


def main() -> int:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if manifest != EXPECTED:
        raise SystemExit("Container security release manifest changed without review.")

    workflow = WORKFLOW.read_text(encoding="utf-8")
    for requirement in (
        "workflow_dispatch:",
        "server-v0.3.1",
        "test \"$GITHUB_REF\" = \"refs/heads/main\"",
        "git cat-file -t \"refs/tags/${RELEASE_TAG}\"",
        ".verification.verified == true",
        "release-integrity.yml/runs?head_sha=",
        "environment:",
        "name: publish-container",
        "packages: write",
        "attestations: write",
        "id-token: write",
        "docker/setup-buildx-action@37fe631027851001ddb9b187196cc803df7f5f0e",
        "driver: docker-container",
        "docker/login-action@dbcb813823bdd20940b903addbd779551569679f",
        "docker/build-push-action@53b7df96c91f9c12dcc8a07bcb9ccacbed38856a",
        "sbom: true",
        "provenance: mode=max",
        "VYRAL_IMAGE_VERSION=0.3.1",
        "ghcr.io/univeracity/vyral-server:0.3.1",
    ):
        if requirement not in workflow:
            raise SystemExit(f"Container security publisher is missing {requirement!r}.")
    for forbidden in ("dotnet nuget push", "twine upload", "npm publish", "gh release create"):
        if forbidden in workflow:
            raise SystemExit(f"Container security publisher must not contain {forbidden!r}.")
    if "\n  push:" in workflow:
        raise SystemExit("Container security publisher must not have an automatic push trigger.")

    print("container-security-release=ok tag=server-v0.3.1 image=0.3.1")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
