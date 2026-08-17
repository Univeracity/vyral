#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

ARTIFACT_ROOT="${VYRAL_RELEASE_ARTIFACT_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/vyral-release-artifacts-XXXXXX")}"
if [[ "$ARTIFACT_ROOT" != /* ]]; then
  ARTIFACT_ROOT="$ROOT/$ARTIFACT_ROOT"
fi
KEEP_ARTIFACTS="${VYRAL_KEEP_RELEASE_ARTIFACTS:-0}"
PACKAGES_DIR="$ARTIFACT_ROOT/packages"
SBOM_DIR="$ARTIFACT_ROOT/sbom"
QUALIFICATION_DIR="$ARTIFACT_ROOT/qualification"

cleanup() {
  if [[ "$KEEP_ARTIFACTS" != "1" && -z "${VYRAL_RELEASE_ARTIFACT_DIR:-}" ]]; then
    rm -rf "$ARTIFACT_ROOT"
  else
    printf 'release-artifacts=%s\n' "$ARTIFACT_ROOT"
  fi
}
trap cleanup EXIT

readonly RELEASE_PROJECTS=(
  src/Vyral.Abstractions/Vyral.Abstractions.csproj
  src/Vyral.Aws/Vyral.Aws.csproj
  src/Vyral.Azure/Vyral.Azure.csproj
  src/Vyral.Bridge.SemanticKernel/Vyral.Bridge.SemanticKernel.csproj
  src/Vyral.Cloudflare/Vyral.Cloudflare.csproj
  src/Vyral.Embeddings.Onnx/Vyral.Embeddings.Onnx.csproj
  src/Vyral.Execution/Vyral.Execution.csproj
  src/Vyral.Execution.Aws/Vyral.Execution.Aws.csproj
  src/Vyral.Execution.AzureDurable/Vyral.Execution.AzureDurable.csproj
  src/Vyral.Execution.AzureDurable.Functions/Vyral.Execution.AzureDurable.Functions.csproj
  src/Vyral.Execution.Local/Vyral.Execution.Local.csproj
  src/Vyral.Execution.Temporal/Vyral.Execution.Temporal.csproj
  src/Vyral.Execution.Temporal.Hosting/Vyral.Execution.Temporal.Hosting.csproj
  src/Vyral.Execution.Temporal.Postgres/Vyral.Execution.Temporal.Postgres.csproj
  src/Vyral.Execution.WorkerClient/Vyral.Execution.WorkerClient.csproj
  src/Vyral.Google/Vyral.Google.csproj
  src/Vyral.Local/Vyral.Local.csproj
  src/Vyral.MySql/Vyral.MySql.csproj
  src/Vyral.Mcp/Vyral.Mcp.csproj
  src/Vyral.Pgvector/Vyral.Pgvector.csproj
  src/Vyral.Primitives/Vyral.Primitives.csproj
  src/Vyral.Providers.Abstractions/Vyral.Providers.Abstractions.csproj
  src/Vyral.Providers.Cli/Vyral.Providers.Cli.csproj
  src/Vyral.Providers.Jules/Vyral.Providers.Jules.csproj
  src/Vyral.Providers.Local/Vyral.Providers.Local.csproj
  src/Vyral.Providers.Onnx/Vyral.Providers.Onnx.csproj
)

# Do not let a newly publishable source project quietly evade release validation because somebody
# forgot to add it to RELEASE_PROJECTS. The two executable projects explicitly opt out of packing.
declare -A configured_release_projects=()
for project in "${RELEASE_PROJECTS[@]}"; do
  configured_release_projects["$project"]=1
  if [[ ! -f "$project" ]]; then
    echo "Configured release project does not exist: $project" >&2
    exit 1
  fi
  if rg -q '<IsPackable>false</IsPackable>' "$project"; then
    echo "Configured release project opts out of packing: $project" >&2
    exit 1
  fi
done

while IFS= read -r project; do
  if rg -q '<IsPackable>false</IsPackable>' "$project"; then
    continue
  fi
  if [[ -z "${configured_release_projects[$project]:-}" ]]; then
    echo "Publishable source project is missing from RELEASE_PROJECTS: $project" >&2
    exit 1
  fi
done < <(find src -type f -name '*.csproj' -print | sort)

mkdir -p \
  "$PACKAGES_DIR" \
  "$SBOM_DIR" \
  "$QUALIFICATION_DIR" \
  "$ARTIFACT_ROOT/javascript" \
  "$ARTIFACT_ROOT/python-runtime"

while IFS= read -r -d '' script; do
  bash -n "$script"
done < <(find scripts deploy -type f -name '*.sh' -print0 | sort -z)
printf 'release-shell-syntax=ok\n'
python3 scripts/verify-publication-policy.py
python3 scripts/verify-publication-cohort.py
python3 scripts/verify-container-security-release.py
python3 scripts/test-audit-github-launch-controls.py
python3 scripts/test-audit-github-workflow-health.py
python3 scripts/test-classify-ci-changes.py
python3 scripts/test-verify-oci-image-identity.py
scripts/test-run-dotnet-tests.sh
scripts/test-validate-azure-durable-functions-live.sh

python3 scripts/verify-version-policy.py
python3 scripts/verify-public-sdk-surface.py
python3 scripts/verify-public-contract-schemas.py
python3 scripts/verify-markdown-links.py
python3 scripts/generate-sdk-types.py
VYRAL_FRESH_DEVELOPER_ARTIFACT_DIR="$QUALIFICATION_DIR/fresh-developer" \
  scripts/verify-fresh-developer-path.sh
VYRAL_PUBLIC_EXPORT_EVIDENCE_DIR="$ARTIFACT_ROOT/public-export" scripts/verify-public-export.sh
scripts/scan-release-secrets.sh
scripts/test-scan-release-secrets.sh
scripts/scan-release-ownership.sh
scripts/test-scan-release-ownership.sh
scripts/test-validate-npm-pack-layout.sh
scripts/test-generate-adapter-qualification.sh
python3 scripts/test-render-adapter-qualification.py
python3 scripts/render-adapter-qualification.py --check
jq empty qualification/adapter-qualification.schema.json qualification/adapter-qualification.json
scripts/generate-adapter-qualification.sh "$QUALIFICATION_DIR/adapter-qualification.json"
cp qualification/adapter-qualification.schema.json "$QUALIFICATION_DIR/adapter-qualification.schema.json"
scripts/test-validate-google-execution-live.sh
scripts/test-validate-aws-live-qualification.sh
scripts/test-validate-aws-opensearch-live.sh
scripts/test-scan-release-history.sh
scripts/test-validate-canonical-mysql-benchmark-report.sh
python3 scripts/verify-ripgrep-retrieval-report.py \
  benchmarks/retrieval/ripgrep-vs-vyral-local-2026-08-11.json \
  --require-admission

ripgrep_comparison="$ARTIFACT_ROOT/ripgrep-comparison.json"
ripgrep_tampered="$ARTIFACT_ROOT/ripgrep-comparison-tampered.json"
ripgrep_user_result="$ARTIFACT_ROOT/ripgrep-user-path.json"
ripgrep_migration_result="$ARTIFACT_ROOT/ripgrep-migration-path.json"
canonical_cutover_result="$ARTIFACT_ROOT/canonical-cutover-path.json"
stateless_mcp_result="$ARTIFACT_ROOT/stateless-mcp-path.json"
python3 scripts/benchmark-ripgrep-retrieval.py \
  --output "$ripgrep_comparison" \
  --noise-documents 60 \
  --iterations 3 \
  --require-admission
python3 scripts/verify-ripgrep-retrieval-report.py \
  "$ripgrep_comparison" \
  --minimum-noise 60 \
  --minimum-iterations 3 \
  --require-admission
jq '.quality.variants["ripgrep-fixed"].cases[0].metrics.recallAtK = 0' \
  "$ripgrep_comparison" > "$ripgrep_tampered"
if python3 scripts/verify-ripgrep-retrieval-report.py \
  "$ripgrep_tampered" \
  --minimum-noise 60 \
  --minimum-iterations 3; then
  echo "The ripgrep comparison verifier accepted altered metrics." >&2
  exit 1
fi
python3 examples/python/source_native_search.py \
  RipgrepSearchAdapter \
  --root . \
  --limit 5 \
  --json > "$ripgrep_user_result"
jq -e '
  (.matches | length > 0)
  and all(
    .matches[];
    (.sourceUri | startswith("vyral-source://ripgrep/"))
    and (.sourceUri | contains("#L"))
    and (.sourceRevision | startswith("sha256:"))
  )
' "$ripgrep_user_result" >/dev/null
python3 examples/python/retrieval_migration.py \
  --json > "$ripgrep_migration_result"
jq -e '
  (.sourceNative.matches | length == 1)
  and (.sourceNative.reorderedMatchCount == 0)
  and (.indexed.results | length == 1)
  and (.indexed.results[0].recordId == "execution.md")
  and (.indexed.embeddingUsed == false)
' "$ripgrep_migration_result" >/dev/null
printf 'ripgrep-retrieval-admission-gate=ok\n'

python3 examples/python/canonical_store_cutover.py \
  --json > "$canonical_cutover_result"
jq -e '
  (.schemaVersion == "vyral.canonical-cutover-example.v1")
  and (.source.idempotentReplay == true)
  and (.transfer.hashVerifiedRestore == true)
  and (.transfer.chunkCount >= 1)
  and (.target.documentCount == 1)
  and (.target.revisionCount == 1)
  and (.target.outboxEventCount == 1)
  and (.target.transactionCount == 1)
  and (.target.idempotentReplay == true)
  and (.target.tenantIsolationPreserved == true)
  and (.source.transactionId == .target.transactionId)
' "$canonical_cutover_result" >/dev/null

python3 examples/python/stateless_mcp_round_robin.py \
  --json > "$stateless_mcp_result"
jq -e '
  (.protocolVersion == "2026-07-28")
  and (.topology.instanceCount == 2)
  and (.topology.sharedMcpSessionStore == false)
  and (.requests | length == 4)
  and all(
    .requests[];
    (.status == 200)
    and (.methodVisibleBeforeBody == true)
    and (.sessionHeaderPresent == false)
  )
  and (
    [.requests[] | select(.method == "server/discover") | .target]
    | unique
    | length == 2
  )
  and (
    [.requests[] | select(.method == "tools/list") | .target]
    | unique
    | length == 2
  )
  and (.catalogToolCount > 0)
  and (.equivalentResultsAcrossInstances == true)
  and (.headerBodyMismatchRejected == true)
' "$stateless_mcp_result" >/dev/null
printf 'portable-migration-and-stateless-mcp-examples=ok\n'

if [[ -n "${VYRAL_PUBLIC_HISTORY_DENYLIST_FILE:-}" || -n "${VYRAL_PUBLIC_HISTORY_DENYLIST:-}" ]]; then
  scripts/scan-release-history.sh
fi

python3 - <<'PY'
from pathlib import Path
import re

for path in sorted(Path('.github/workflows').glob('*.yml')):
    workflow = path.read_text(encoding='utf-8')
    references = re.findall(r'^\s*uses:\s*([^\s#]+)', workflow, re.MULTILINE)
    mutable = [reference for reference in references
               if reference.startswith(('actions/', 'github/')) and not re.search(r'@[0-9a-f]{40}$', reference)]
    if mutable:
        raise SystemExit(f'{path}: mutable first-party action reference(s): {", ".join(mutable)}')

print('release-workflow-action-pins=ok')
PY

dotnet restore Vyral.sln --locked-mode
python3 scripts/verify-azure-durable-package-graph.py
dotnet list Vyral.sln package --vulnerable --include-transitive --format json \
  > "$ARTIFACT_ROOT/dotnet-vulnerability-audit.json"
jq -e '
  [
    .projects[]
    | .frameworks[]?
    | ((.topLevelPackages // []) + (.transitivePackages // []))[]
    | select((.vulnerabilities // []) | length > 0)
  ]
  | length == 0
' "$ARTIFACT_ROOT/dotnet-vulnerability-audit.json" >/dev/null
printf 'release-dotnet-vulnerability-audit=ok\n'

for project in "${RELEASE_PROJECTS[@]}"; do
  dotnet pack "$project" --configuration Release --no-restore --output "$PACKAGES_DIR"
done

python3 - "$PACKAGES_DIR" "${RELEASE_PROJECTS[@]}" <<'PY'
from __future__ import annotations

import sys
import runpy
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

packages_dir = Path(sys.argv[1])
projects = sys.argv[2:]
packages = sorted(path for path in packages_dir.glob("*.nupkg") if not path.name.endswith(".symbols.nupkg"))
public_docs = set(
    runpy.run_path(
        "scripts/export-public-tree.py",
        run_name="vyral_public_export_policy",
    )["PUBLIC_DOC_FILES"]
)

if len(packages) != len(projects):
    raise SystemExit(f"Expected {len(projects)} release packages, found {len(packages)} in {packages_dir}.")

for package in packages:
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        nuspecs = [name for name in names if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise SystemExit(f"{package.name}: expected exactly one nuspec, found {len(nuspecs)}.")

        root = ET.fromstring(archive.read(nuspecs[0]))

        def child(parent: ET.Element, name: str) -> ET.Element | None:
            return next((element for element in parent if element.tag.rsplit("}", 1)[-1] == name), None)

        metadata = child(root, "metadata")
        if metadata is None:
            raise SystemExit(f"{package.name}: missing nuspec metadata.")

        def value(name: str) -> str:
            element = child(metadata, name)
            return (element.text or "").strip() if element is not None else ""

        required = ("id", "version", "authors", "description", "projectUrl", "readme")
        missing = [name for name in required if not value(name)]
        if missing:
            raise SystemExit(f"{package.name}: missing required metadata: {', '.join(missing)}.")

        package_id = value("id")
        package_version = value("version")
        expected_versions = {
            "Vyral.Primitives": "0.2.0",
            "Vyral.Execution": "0.2.0",
            "Vyral.Execution.Aws": "0.2.0",
            "Vyral.Execution.AzureDurable": "0.2.0",
            "Vyral.Execution.AzureDurable.Functions": "0.2.0",
            "Vyral.Execution.Local": "0.2.0",
            "Vyral.Execution.Temporal": "0.2.0",
            "Vyral.Execution.Temporal.Hosting": "0.2.0",
            "Vyral.Execution.Temporal.Postgres": "0.2.0",
            "Vyral.Execution.WorkerClient": "0.2.0",
            "Vyral.Mcp": "0.3.0",
        }
        expected_version = expected_versions.get(package_id, "0.3.0")
        if package_version != expected_version:
            raise SystemExit(
                f"{package.name}: expected version {expected_version} under the stability policy, found {package_version}."
            )

        if package_id in {
            "Vyral.Abstractions",
            "Vyral.Primitives",
            "Vyral.Providers.Abstractions",
            "Vyral.Execution",
        }:
            missing_tfms = [
                tfm for tfm in ("net8.0", "net10.0")
                if not any(name.startswith(f"lib/{tfm}/") and name.endswith(".dll") for name in names)
            ]
            if missing_tfms:
                raise SystemExit(
                    f"{package.name}: missing core contract target(s): {', '.join(missing_tfms)}."
                )

        license_element = child(metadata, "license")
        if license_element is None or license_element.attrib.get("type") != "expression" or (license_element.text or "").strip() != "Apache-2.0":
            raise SystemExit(f"{package.name}: expected Apache-2.0 license expression.")

        repository = child(metadata, "repository")
        if repository is None or repository.attrib.get("type") != "git" or repository.attrib.get("url") != "https://github.com/univeracity/vyral":
            raise SystemExit(f"{package.name}: expected the public Git repository metadata.")

        readme = value("readme")
        if readme not in names:
            raise SystemExit(f"{package.name}: declared README '{readme}' is not in the package.")
        if "EXECUTION-RUNTIME-LIMITATIONS.md" not in names:
            raise SystemExit(f"{package.name}: missing public execution-runtime limitations guide.")
        readme_text = archive.read(readme).decode("utf-8", errors="replace")
        if "](../" in readme_text:
            raise SystemExit(f"{package.name}: README has a relative link that escapes the package root.")

        # Package READMEs must not point at files that only exist in the repository checkout. Keep
        # external documentation links, anchors, and package-contained files intact.
        import posixpath
        import re
        for href in re.findall(r"\[[^\]]*\]\(([^)\s]+)(?:\s+[^)]*)?\)", readme_text):
            target = href.split("#", 1)[0]
            repository_prefix = "https://github.com/univeracity/vyral/blob/main/"
            if target.startswith(repository_prefix):
                repository_path = target.removeprefix(repository_prefix)
                if repository_path.startswith("docs/") and repository_path not in public_docs:
                    raise SystemExit(
                        f"{package.name}: README references non-exported documentation "
                        f"'{repository_path}'."
                    )
            if not target or target.startswith(("https://", "http://", "mailto:", "tel:")):
                continue
            if target.startswith("/"):
                raise SystemExit(f"{package.name}: README uses an absolute package link '{href}'.")
            resolved = posixpath.normpath(posixpath.join(posixpath.dirname(readme), target))
            if resolved not in names:
                raise SystemExit(f"{package.name}: README link '{href}' does not resolve inside the package.")

        symbols = package.with_suffix(".snupkg")
        if not symbols.exists():
            raise SystemExit(f"{package.name}: missing matching symbol package '{symbols.name}'.")

print(f"release-package-metadata=ok packages={len(packages)}")
PY

scripts/validate-mcp-package-consumer.sh "$PACKAGES_DIR"

dotnet tool restore
dotnet tool run dotnet-CycloneDX Vyral.sln \
  --output "$SBOM_DIR" \
  --filename vyral-dotnet.cdx.json \
  --output-format json \
  --exclude-test-projects \
  --configuration Release

test -s "$SBOM_DIR/vyral-dotnet.cdx.json"
jq -e '.bomFormat == "CycloneDX" and (.components | type == "array") and (.components | length > 0)' \
  "$SBOM_DIR/vyral-dotnet.cdx.json" >/dev/null
python3 - "$SBOM_DIR/vyral-dotnet.cdx.json" THIRD-PARTY-NOTICES.md <<'PY'
from __future__ import annotations

import json
import sys
from pathlib import Path

sbom = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
notices = Path(sys.argv[2]).read_text(encoding='utf-8')
opaque: list[str] = []

for component in sbom.get('components', []):
    if component.get('type') != 'library':
        continue
    licenses = component.get('licenses') or []
    labels = [
        entry.get('license', {}).get('id')
        or entry.get('license', {}).get('name')
        or entry.get('expression')
        or ''
        for entry in licenses
    ]
    if not labels or any(label.lower().startswith('unknown') for label in labels):
        opaque.append(component.get('name', 'unnamed component'))

undocumented = [name for name in opaque if f'`{name}`' not in notices]
if undocumented:
    raise SystemExit(
        'SBOM components with opaque license metadata are missing from THIRD-PARTY-NOTICES.md: '
        + ', '.join(undocumented))

print(f'release-sbom-license-review=ok opaque-components={len(opaque)}')
PY

(
  cd clients/javascript
  npm ci --ignore-scripts
  npm pack --dry-run --json > "$ARTIFACT_ROOT/javascript-pack.json"
  npm pack --json --pack-destination "$ARTIFACT_ROOT/javascript" > "$ARTIFACT_ROOT/javascript-package.json"
)
jq -e -f scripts/validate-npm-pack-layout.jq \
  "$ARTIFACT_ROOT/javascript-pack.json" >/dev/null
(
  cd clients/javascript
  npm run check
  npm run typecheck
  npm test
)

python3 -m build --outdir "$ARTIFACT_ROOT/python" clients/python
scripts/verify-python-runtime.sh
(
  extropic_venv="$(mktemp -d "${TMPDIR:-/tmp}/vyral-extropic-sdk-XXXXXX")"
  trap 'rm -rf "$extropic_venv"' EXIT
  python3 -m venv "$extropic_venv"
  "$extropic_venv/bin/python" -m pip install --disable-pip-version-check --no-cache-dir \
    --editable "runtimes/python[extropic]"
  "$extropic_venv/bin/python" scripts/verify-python-extropic-sdk-surface.py
)
python3 -m build \
  --outdir "$ARTIFACT_ROOT/python-runtime" \
  runtimes/python
(
  cd clients/python
  PYTHONPATH=src python3 -m unittest discover -s tests -p 'test_*.py'
)
python3 - "$ARTIFACT_ROOT/python" <<'PY'
from __future__ import annotations

import tarfile
import zipfile
from pathlib import Path

artifacts = Path(__import__('sys').argv[1])
wheels = list(artifacts.glob('*.whl'))
sdists = list(artifacts.glob('*.tar.gz'))
if len(wheels) != 1 or len(sdists) != 1:
    raise SystemExit('Expected one Python wheel and one source distribution.')

with zipfile.ZipFile(wheels[0]) as wheel:
    names = set(wheel.namelist())
    if not any(name.endswith('.dist-info/licenses/LICENSE') for name in names):
        raise SystemExit('Python wheel is missing the Apache license.')
    if 'vyral_client/client.py' not in names:
        raise SystemExit('Python wheel is missing the client source.')
    required_type_files = {
        'vyral_client/async_client.py',
        'vyral_client/client.pyi',
        'vyral_client/contracts.py',
        'vyral_client/__init__.pyi',
        'vyral_client/py.typed',
    }
    missing_type_files = sorted(required_type_files - names)
    if missing_type_files:
        raise SystemExit('Python wheel is missing public type files: ' + ', '.join(missing_type_files))

with tarfile.open(sdists[0]) as sdist:
    names = set(sdist.getnames())
    if not any(name.endswith('/LICENSE') for name in names):
        raise SystemExit('Python source distribution is missing the Apache license.')

print('python-package-layout=ok')
PY

python3 - "$ARTIFACT_ROOT/python-runtime" <<'PY'
from __future__ import annotations

import tarfile
import zipfile
from pathlib import Path
import sys

artifacts = Path(sys.argv[1])
wheels = list(artifacts.glob("vyral-0.1.1-*.whl"))
sdists = list(artifacts.glob("vyral-0.1.1.tar.gz"))
if len(wheels) != 1 or len(sdists) != 1:
    raise SystemExit(
        "Expected one vyral runtime wheel and one source distribution."
    )

with zipfile.ZipFile(wheels[0]) as wheel:
    names = set(wheel.namelist())
    required = {
        "vyral_runtime/host/application.py",
        "vyral_runtime/host/mcp.py",
        "vyral_runtime/host/rest.py",
        "vyral_runtime/py.typed",
    }
    missing = sorted(required - names)
    if missing:
        raise SystemExit(
            "Python runtime wheel is missing: " + ", ".join(missing)
        )
    if not any(
        name.endswith(".dist-info/licenses/LICENSE") for name in names
    ):
        raise SystemExit("Python runtime wheel is missing the Apache license.")
    entry_points = [
        name for name in names if name.endswith(".dist-info/entry_points.txt")
    ]
    if len(entry_points) != 1:
        raise SystemExit("Python runtime wheel is missing its CLI entry point.")
    console_scripts = wheel.read(entry_points[0]).decode("utf-8")
    for command in ("vyral =", "vyral-runtime ="):
        if command not in console_scripts:
            raise SystemExit(
                f"Python runtime CLI entry point is missing: {command[:-2]}"
            )

with tarfile.open(sdists[0]) as sdist:
    names = set(sdist.getnames())
    if not any(name.endswith("/LICENSE") for name in names):
        raise SystemExit(
            "Python runtime source distribution is missing the Apache license."
        )

print("python-runtime-package-layout=ok")
PY

python_wheels=("$ARTIFACT_ROOT"/python/*.whl)
javascript_packages=("$ARTIFACT_ROOT"/javascript/*.tgz)
python_runtime_wheels=("$ARTIFACT_ROOT"/python-runtime/*.whl)
if [[ \
  "${#python_wheels[@]}" != "1" \
  || "${#javascript_packages[@]}" != "1" \
  || "${#python_runtime_wheels[@]}" != "1" \
]]; then
  echo "Expected one Python client wheel, Python runtime wheel, and JavaScript package." >&2
  exit 1
fi
scripts/test-built-sdk-real-server.sh "${python_wheels[0]}" "${javascript_packages[0]}"
VYRAL_MCP_CONFORMANCE_ARTIFACT_DIR="$QUALIFICATION_DIR/mcp-conformance" \
  scripts/verify-mcp-conformance.sh
scripts/test-built-sdk-python-runtime.sh \
  "${python_runtime_wheels[0]}" \
  "${python_wheels[0]}" \
  "${javascript_packages[0]}"
scripts/verify-python-runtime-external-worker.sh \
  "${python_runtime_wheels[0]}"
VYRAL_PYTHON_MCP_CONFORMANCE_ARTIFACT_DIR="$QUALIFICATION_DIR/python-mcp-conformance" \
  scripts/verify-python-runtime-mcp-conformance.sh \
  "${python_runtime_wheels[0]}"
python3 scripts/verify-python-runtime-security.py \
  "${python_runtime_wheels[0]}" \
  --output "$QUALIFICATION_DIR/python-runtime-security.json"

scripts/validate-execution-runtime-package-consumer.sh
(
  cd clients/go
  go test ./...
)

printf 'release-artifact-verification=ok\n'
