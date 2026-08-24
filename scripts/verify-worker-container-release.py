#!/usr/bin/env python3
"""Verify the narrowly authorized worker-capable GHCR server release."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "packaging" / "worker-container-release.json"
WORKFLOW = ROOT / ".github" / "workflows" / "publish-worker-container.yml"

EXPECTED = {
    "schemaVersion": "vyral.worker-container-release.v1",
    "publicationAuthorized": True,
    "image": {
        "name": "ghcr.io/univeracity/vyral-server",
        "version": "0.3.2",
        "source": "Dockerfile",
        "environment": "publish-container",
        "maturity": "mixed",
        "hostedWorker": {
            "entrypoint": "dotnet worker/Vyral.HostedWorker.dll",
            "handlerIds": ["vyral.artifacts.record-ingest"],
            "maturity": "preview",
        },
    },
    "authorization": {
        "mode": "manual-protected-environment",
        "releaseTag": "server-v0.3.2",
        "workflow": ".github/workflows/publish-worker-container.yml",
        "requirements": [
            "a GitHub-verified signed annotated server tag that resolves to current main",
            "a successful canonical Release Integrity run for that exact commit, including the packaged hosted-worker receipt",
            "a manual dispatch from main through the publish-container protected environment",
            "a passing hosted-worker qualification and vulnerability scan against the exact published digest",
        ],
        "publisher": {
            "ecosystem": "container",
            "registry": "https://ghcr.io",
            "workflowFile": "publish-worker-container.yml",
            "environment": "publish-container",
            "authentication": "repository-scoped GitHub Actions GITHUB_TOKEN",
        },
    },
}


def main() -> int:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if manifest != EXPECTED:
        raise SystemExit("Worker container release manifest changed without review.")

    workflow = WORKFLOW.read_text(encoding="utf-8")
    for requirement in (
        "workflow_dispatch:",
        "server-v0.3.2",
        'test "$GITHUB_REF" = "refs/heads/main"',
        'git cat-file -t "refs/tags/${RELEASE_TAG}"',
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
        "VYRAL_IMAGE_VERSION=0.3.2",
        "ghcr.io/univeracity/vyral-server:0.3.2",
        "scripts/verify-hosted-worker-container.sh",
        "aquasec/trivy:0.73.0@sha256:",
        "worker-container-server-v0.3.2",
    ):
        if requirement not in workflow:
            raise SystemExit(f"Worker container publisher is missing {requirement!r}.")
    for forbidden in ("dotnet nuget push", "twine upload", "npm publish", "gh release create"):
        if forbidden in workflow:
            raise SystemExit(f"Worker container publisher must not contain {forbidden!r}.")
    if "\n  push:" in workflow:
        raise SystemExit("Worker container publisher must not have an automatic push trigger.")

    print("worker-container-release=ok tag=server-v0.3.2 image=0.3.2")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
