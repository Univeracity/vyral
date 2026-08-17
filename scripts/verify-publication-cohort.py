#!/usr/bin/env python3
"""Verify the authorized first-publication cohort and its package metadata."""

from __future__ import annotations

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ElementTree


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "packaging" / "publication-cohort.json"
EXPECTED = (
    (
        "nuget",
        "Vyral.Abstractions",
        "0.3.0",
        "src/Vyral.Abstractions/Vyral.Abstractions.csproj",
        "publish-nuget",
        "public",
    ),
    (
        "nuget",
        "Vyral.Local",
        "0.3.0",
        "src/Vyral.Local/Vyral.Local.csproj",
        "publish-nuget",
        "public",
    ),
    (
        "nuget",
        "Vyral.Primitives",
        "0.2.0",
        "src/Vyral.Primitives/Vyral.Primitives.csproj",
        "publish-nuget",
        "public",
    ),
    (
        "nuget",
        "Vyral.Execution",
        "0.2.0",
        "src/Vyral.Execution/Vyral.Execution.csproj",
        "publish-nuget",
        "public",
    ),
    (
        "nuget",
        "Vyral.Execution.Local",
        "0.2.0",
        "src/Vyral.Execution.Local/Vyral.Execution.Local.csproj",
        "publish-nuget",
        "preview",
    ),
    (
        "pypi",
        "vyral",
        "0.1.1",
        "runtimes/python/pyproject.toml",
        "publish-pypi",
        "prototype",
    ),
    (
        "npm",
        "vyral-client",
        "0.3.0",
        "clients/javascript/package.json",
        "publish-npm",
        "public",
    ),
    (
        "container",
        "ghcr.io/univeracity/vyral-server",
        "0.3.0",
        "Dockerfile",
        "publish-container",
        "mixed",
    ),
)
EXCLUDED = {
    "cloud-provider packages",
    "provider-specific execution adapters",
    "Temporal packages",
    "Python HTTP client distribution (vyral-client)",
    "prototype integrations",
}
AUTHORIZATION = {
    "mode": "manual-protected-environment",
    "releaseTag": "v0.3.0",
    "workflow": ".github/workflows/publish-first-cohort.yml",
    "requirements": (
        "a GitHub-verified signed annotated release tag that resolves to current main",
        "a successful canonical Release Integrity push run for that exact commit",
        "the exact NuGet and PyPI registry trust relationships, plus the documented one-time npm bootstrap followed by its trusted publisher",
        "a manual dispatch from main through the named protected environment",
    ),
    "publishers": (
        (
            "nuget",
            "https://api.nuget.org/v3/index.json",
            "publish-first-cohort.yml",
            "publish-nuget",
            "GitHub Actions OIDC trusted publishing",
        ),
        (
            "pypi",
            "https://upload.pypi.org/legacy/",
            "publish-first-cohort.yml",
            "publish-pypi",
            "GitHub Actions OIDC trusted publishing",
        ),
        (
            "npm",
            "https://registry.npmjs.org/",
            "publish-first-cohort.yml",
            "publish-npm",
            "GitHub Actions OIDC trusted publishing",
        ),
        (
            "container",
            "https://ghcr.io",
            "publish-first-cohort.yml",
            "publish-container",
            "repository-scoped GitHub Actions GITHUB_TOKEN",
        ),
    ),
}


def _dotnet_identity(path: Path) -> tuple[str, str]:
    project = ElementTree.parse(path).getroot()
    package_id = next(
        (
            element.text
            for element in project.iter("PackageId")
            if element.text
        ),
        path.stem,
    )
    version = next(
        (
            element.text
            for element in project.iter("Version")
            if element.text
        ),
        None,
    )
    if version is None:
        defaults = ElementTree.parse(ROOT / "Directory.Build.props").getroot()
        version = next(
            (
                element.text
                for element in defaults.iter("VyralReleaseVersion")
                if element.text
            ),
            None,
        )
    if not package_id or not version:
        raise SystemExit(f"Unable to resolve NuGet identity for {path}.")
    return package_id, version


def _python_identity(path: Path) -> tuple[str, str]:
    content = path.read_text(encoding="utf-8")
    name = re.search(r'^name\s*=\s*"([^"]+)"', content, re.MULTILINE)
    version = re.search(r'^version\s*=\s*"([^"]+)"', content, re.MULTILINE)
    if name is None or version is None:
        raise SystemExit(f"Unable to resolve Python identity for {path}.")
    return name.group(1), version.group(1)


def _source_identity(
    ecosystem: str,
    source: Path,
    declared_name: str,
    declared_version: str,
) -> tuple[str, str]:
    if ecosystem == "nuget":
        return _dotnet_identity(source)
    if ecosystem == "pypi":
        return _python_identity(source)
    if ecosystem == "npm":
        package = json.loads(source.read_text(encoding="utf-8"))
        return str(package.get("name")), str(package.get("version"))
    if ecosystem == "container":
        return declared_name, declared_version
    raise SystemExit(f"Unsupported publication ecosystem: {ecosystem}")


def main() -> int:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != "vyral.publication-cohort.v1":
        raise SystemExit("Publication cohort schemaVersion is invalid.")
    if manifest.get("publicationAuthorized") is not True:
        raise SystemExit(
            "The first publication cohort must remain explicitly authorized."
        )
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list):
        raise SystemExit("Publication cohort artifacts must be an array.")
    actual = tuple(
        (
            item.get("ecosystem"),
            item.get("name"),
            item.get("version"),
            item.get("source"),
            item.get("environment"),
            item.get("maturity"),
        )
        for item in artifacts
        if isinstance(item, dict)
    )
    if actual != EXPECTED:
        raise SystemExit(
            "The first publication cohort changed without updating its "
            "reviewed verifier boundary."
        )
    excluded = manifest.get("excludedFromFirstCohort")
    if not isinstance(excluded, list) or set(excluded) != EXCLUDED:
        raise SystemExit("Publication cohort exclusions are incomplete.")

    authorization = manifest.get("authorization")
    if not isinstance(authorization, dict):
        raise SystemExit("Publication cohort authorization is missing.")
    if (
        authorization.get("mode") != AUTHORIZATION["mode"]
        or authorization.get("releaseTag") != AUTHORIZATION["releaseTag"]
        or authorization.get("workflow") != AUTHORIZATION["workflow"]
    ):
        raise SystemExit("Publication cohort authorization boundary changed.")
    if tuple(authorization.get("requirements", ())) != AUTHORIZATION["requirements"]:
        raise SystemExit("Publication authorization requirements changed.")
    publishers = authorization.get("publishers")
    actual_publishers = tuple(
        (
            item.get("ecosystem"),
            item.get("registry"),
            item.get("workflowFile"),
            item.get("environment"),
            item.get("authentication"),
        )
        for item in publishers
        if isinstance(item, dict)
    ) if isinstance(publishers, list) else ()
    if actual_publishers != AUTHORIZATION["publishers"]:
        raise SystemExit("Publication registry-publisher boundary changed.")

    for ecosystem, name, version, relative, _environment, _maturity in EXPECTED:
        source = ROOT / relative
        if not source.is_file():
            raise SystemExit(f"Publication source is missing: {relative}")
        actual_name, actual_version = _source_identity(
            ecosystem,
            source,
            name,
            version,
        )
        if (actual_name, actual_version) != (name, version):
            raise SystemExit(
                f"Publication identity drift for {relative}: "
                f"{actual_name} {actual_version}"
            )

    print(
        "publication-cohort=ok mode=authorized-first-cohort "
        f"artifacts={len(EXPECTED)} publishers={len(AUTHORIZATION['publishers'])} "
        f"excluded={len(EXCLUDED)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
