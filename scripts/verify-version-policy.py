#!/usr/bin/env python3
"""Verify that source manifests match Vyral's documented pre-1.0 version lines."""

from __future__ import annotations

import json
from pathlib import Path
import re
import sys
import tomllib
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent
PRODUCT_VERSION = "0.3.0"
EXECUTION_VERSION = "0.2.0"
PYTHON_RUNTIME_VERSION = "0.1.1"
PYTHON_RUNTIME_FIXTURE_MINIMUM = "0.1.0"
GO_MINIMUM_VERSION = "1.25.0"
GO_BUILD_VERSION = "1.26.6"
BUBBLEWRAP_BUILD_VERSION = "0.11.2"
BUBBLEWRAP_BUILD_SHA256 = (
    "69abc30005d2186baf7737feacd8da35633b93cf5af38838ecff17c5f8e924f6"
)
SKOPEO_IMAGE = (
    "quay.io/skopeo/stable@sha256:"
    "c7d3c512612f52805023cd38351081dad7e2729fc13d14b701e47c7c8bdd6615"
)

CORE_TFM_PROJECTS = (
    "src/Vyral.Abstractions/Vyral.Abstractions.csproj",
    "src/Vyral.Primitives/Vyral.Primitives.csproj",
    "src/Vyral.Providers.Abstractions/Vyral.Providers.Abstractions.csproj",
    "src/Vyral.Execution/Vyral.Execution.csproj",
)

EXECUTION_PROJECTS = (
    "src/Vyral.Primitives/Vyral.Primitives.csproj",
    "src/Vyral.Execution/Vyral.Execution.csproj",
    "src/Vyral.Execution.Aws/Vyral.Execution.Aws.csproj",
    "src/Vyral.Execution.AzureDurable/Vyral.Execution.AzureDurable.csproj",
    "src/Vyral.Execution.AzureDurable.Functions/Vyral.Execution.AzureDurable.Functions.csproj",
    "src/Vyral.Execution.Local/Vyral.Execution.Local.csproj",
    "src/Vyral.Execution.Temporal/Vyral.Execution.Temporal.csproj",
    "src/Vyral.Execution.Temporal.Hosting/Vyral.Execution.Temporal.Hosting.csproj",
    "src/Vyral.Execution.Temporal.Postgres/Vyral.Execution.Temporal.Postgres.csproj",
    "src/Vyral.Execution.WorkerClient/Vyral.Execution.WorkerClient.csproj",
)


def project_properties(relative: str) -> dict[str, str]:
    root = ET.parse(ROOT / relative).getroot()
    result: dict[str, str] = {}
    for group in root.findall("PropertyGroup"):
        for child in group:
            if child.text:
                result[child.tag] = child.text.strip()
    return result


def require(actual: object, expected: object, label: str) -> None:
    if actual != expected:
        raise SystemExit(f"{label}: expected {expected!r}, found {actual!r}")


def require_python_constant(path: Path, name: str, expected: str) -> None:
    text = path.read_text(encoding="utf-8")
    match = re.search(rf"^{re.escape(name)}\s*=\s*[\"']([^\"']+)[\"']\s*$", text, re.MULTILINE)
    require(match.group(1) if match else None, expected, f"{path.relative_to(ROOT)} {name}")


def main() -> int:
    directory_props = project_properties("Directory.Build.props")
    require(directory_props.get("VyralReleaseVersion"), PRODUCT_VERSION, "default product version")

    catalog = json.loads((ROOT / "contracts/public-sdk-surface.json").read_text(encoding="utf-8"))
    openapi = json.loads((ROOT / "src/Vyral.Server/contracts/vyral.openapi.json").read_text(encoding="utf-8"))
    schema = json.loads((ROOT / "contracts/schemas/vyral-public.schema.json").read_text(encoding="utf-8"))
    python_project = tomllib.loads((ROOT / "clients/python/pyproject.toml").read_text(encoding="utf-8"))
    python_runtime = tomllib.loads((ROOT / "runtimes/python/pyproject.toml").read_text(encoding="utf-8"))
    javascript = json.loads((ROOT / "clients/javascript/package.json").read_text(encoding="utf-8"))
    javascript_lock = json.loads((ROOT / "clients/javascript/package-lock.json").read_text(encoding="utf-8"))

    require(catalog.get("catalogVersion"), PRODUCT_VERSION, "SDK catalog version")
    require(openapi.get("info", {}).get("version"), PRODUCT_VERSION, "OpenAPI version")
    require(schema.get("version"), PRODUCT_VERSION, "JSON Schema version")
    require(python_project.get("project", {}).get("version"), PRODUCT_VERSION, "Python package version")
    require(
        python_runtime.get("project", {}).get("version"),
        PYTHON_RUNTIME_VERSION,
        "Python runtime package version",
    )
    require(
        python_runtime.get("tool", {}).get("vyral", {}).get("contract-version"),
        PRODUCT_VERSION,
        "Python runtime contract version",
    )
    require(
        python_runtime.get("tool", {}).get("vyral", {}).get("fixture-version"),
        "1.0.0",
        "Python runtime fixture version",
    )
    runtime_version_source = ROOT / "runtimes/python/src/vyral_runtime/_version.py"
    require_python_constant(runtime_version_source, "RUNTIME_VERSION", PYTHON_RUNTIME_VERSION)
    require_python_constant(runtime_version_source, "CONTRACT_VERSION", PRODUCT_VERSION)
    require_python_constant(runtime_version_source, "FIXTURE_VERSION", "1.0.0")
    runtime_manifest = json.loads((ROOT / "conformance/runtime/v1/manifest.json").read_text(encoding="utf-8"))
    require(runtime_manifest.get("fixtureVersion"), "1.0.0", "runtime fixture manifest version")
    require(runtime_manifest.get("contractVersion"), PRODUCT_VERSION, "runtime fixture contract version")
    require(
        runtime_manifest.get("minimumRunnerVersion"),
        PYTHON_RUNTIME_FIXTURE_MINIMUM,
        "runtime fixture minimum runner version",
    )
    require(javascript.get("version"), PRODUCT_VERSION, "JavaScript package version")
    require(javascript_lock.get("version"), PRODUCT_VERSION, "JavaScript lockfile version")
    require(javascript_lock.get("packages", {}).get("", {}).get("version"), PRODUCT_VERSION, "JavaScript root lock version")

    mcp = project_properties("src/Vyral.Mcp/Vyral.Mcp.csproj")
    require(mcp.get("Version"), PRODUCT_VERSION, "MCP assembly version")
    require(mcp.get("PackageVersion"), PRODUCT_VERSION, "MCP package version")

    for relative in CORE_TFM_PROJECTS:
        properties = project_properties(relative)
        require(properties.get("TargetFrameworks"), "net8.0;net10.0", f"{relative} target frameworks")

    for relative in EXECUTION_PROJECTS:
        properties = project_properties(relative)
        require(properties.get("Version"), EXECUTION_VERSION, f"{relative} assembly version")
        require(properties.get("PackageVersion"), EXECUTION_VERSION, f"{relative} package version")

    qualification = json.loads((ROOT / "qualification/adapter-qualification.json").read_text(encoding="utf-8"))
    require(qualification.get("coreContractVersion"), EXECUTION_VERSION, "qualification core contract version")
    for adapter in qualification.get("adapters", []):
        require(adapter.get("adapterVersion"), EXECUTION_VERSION, f"qualification {adapter.get('adapterId')} version")

    go_module = (ROOT / "clients/go/go.mod").read_text(encoding="utf-8")
    if not go_module.startswith("module github.com/univeracity/vyral/clients/go\n"):
        raise SystemExit("Go worker client module path is not the documented nested module.")
    worker_go_module = (ROOT / "workers/execution-smoke-go/go.mod").read_text(encoding="utf-8")
    for relative, content in (
        ("clients/go/go.mod", go_module),
        ("workers/execution-smoke-go/go.mod", worker_go_module),
    ):
        if f"\ngo {GO_MINIMUM_VERSION}\n" not in f"\n{content}":
            raise SystemExit(
                f"{relative} must declare the supported Go minimum {GO_MINIMUM_VERSION}."
            )

    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")
    worker_dockerfile = (ROOT / "workers/execution-smoke-go/Dockerfile").read_text(encoding="utf-8")
    for relative, content in (
        ("Dockerfile", dockerfile),
        ("workers/execution-smoke-go/Dockerfile", worker_dockerfile),
    ):
        if f"ARG VYRAL_IMAGE_VERSION={PRODUCT_VERSION}" not in content:
            raise SystemExit(f"{relative} image version is not {PRODUCT_VERSION}.")
    if f"FROM golang:{GO_BUILD_VERSION}@sha256:" not in worker_dockerfile:
        raise SystemExit(
            "workers/execution-smoke-go/Dockerfile is not pinned to the supported Go build version."
        )
    for requirement in ("--locked-mode", "Server__RequireApiKey=true"):
        if requirement not in dockerfile:
            raise SystemExit(
                f"Dockerfile is missing the required security build/runtime setting {requirement!r}."
            )

    for relative in (
        ".github/workflows/ci.yml",
        ".github/workflows/codeql.yml",
        ".github/workflows/execution-runtime-consumer.yml",
        ".github/workflows/release-integrity.yml",
    ):
        content = (ROOT / relative).read_text(encoding="utf-8")
        if f'go-version: "{GO_BUILD_VERSION}"' not in content:
            raise SystemExit(f"{relative} is not pinned to Go {GO_BUILD_VERSION}.")
    release_workflow = (ROOT / ".github/workflows/release-integrity.yml").read_text(
        encoding="utf-8"
    )
    if f"--build-arg VYRAL_IMAGE_VERSION={PRODUCT_VERSION}" not in release_workflow:
        raise SystemExit("release-integrity.yml does not apply the current product image version.")
    if f'BUBBLEWRAP_VERSION: "{BUBBLEWRAP_BUILD_VERSION}"' not in release_workflow:
        raise SystemExit(
            "release-integrity.yml does not build the supported Bubblewrap security boundary."
        )
    if f"BUBBLEWRAP_SHA256: {BUBBLEWRAP_BUILD_SHA256}" not in release_workflow:
        raise SystemExit(
            "release-integrity.yml does not authenticate the pinned Bubblewrap source."
        )
    if "VYRAL_TEST_BUBBLEWRAP: /usr/local/bin/bwrap" not in release_workflow:
        raise SystemExit(
            "release-integrity.yml does not direct sandbox tests to the pinned Bubblewrap binary."
        )
    if "--driver docker-container" not in release_workflow:
        raise SystemExit(
            "release-integrity.yml does not use an attestation-capable BuildKit driver."
        )
    if f"VYRAL_SKOPEO_IMAGE: {SKOPEO_IMAGE}" not in release_workflow:
        raise SystemExit(
            "release-integrity.yml does not pin the OCI importer by digest."
        )

    stability = (ROOT / "docs/reference/stability.md").read_text(encoding="utf-8")
    if "clients/go/vMAJOR.MINOR.PATCH" not in stability:
        raise SystemExit("The stability policy must document the nested Go module tag format.")
    for label in ("`public`", "`preview`", "`prototype`"):
        if label not in stability:
            raise SystemExit(f"The stability policy is missing the {label} maturity label.")
    if "| Python runtime | `0.1.x` |" not in stability:
        raise SystemExit("The stability policy must document the Python runtime version line.")

    allowed_explicit_versions = {PRODUCT_VERSION, EXECUTION_VERSION}
    for project in sorted((ROOT / "src").glob("*/**/*.csproj")):
        text = project.read_text(encoding="utf-8-sig")
        explicit = set(re.findall(r"<(?:Version|PackageVersion)>([^<$]+)</", text))
        unexpected = explicit - allowed_explicit_versions
        if unexpected:
            raise SystemExit(
                f"{project.relative_to(ROOT)} has an undocumented explicit version: {', '.join(sorted(unexpected))}"
            )

    print(
        f"version-policy=ok product={PRODUCT_VERSION} execution={EXECUTION_VERSION} "
        f"python-runtime={PYTHON_RUNTIME_VERSION} go-minimum={GO_MINIMUM_VERSION} "
        f"go-build={GO_BUILD_VERSION} bubblewrap-build={BUBBLEWRAP_BUILD_VERSION} "
        "core-tfms=net8.0,net10.0"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
