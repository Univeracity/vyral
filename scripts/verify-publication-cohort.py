#!/usr/bin/env python3
"""Verify the inert first-publication cohort and its package metadata."""

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
        "vyral-client",
        "0.3.0",
        "clients/python/pyproject.toml",
        "publish-pypi",
        "public",
    ),
    (
        "npm",
        "@vyral/client",
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
    "vyral-runtime",
    "prototype integrations",
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
    if manifest.get("publicationAuthorized") is not False:
        raise SystemExit(
            "The repository must remain build-only until an explicit release "
            "authorization changes the publication policy."
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
        "publication-cohort=ok mode=build-only "
        f"artifacts={len(EXPECTED)} excluded={len(EXCLUDED)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
