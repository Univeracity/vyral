#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
export SOURCE_DATE_EPOCH=315532800

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-python-runtime-XXXXXX")"
cleanup() {
  rm -rf "$work_root"
}
trap cleanup EXIT

python3 scripts/sync-python-runtime-contracts.py
python3 scripts/verify-runtime-conformance.py
COVERAGE_FILE="$work_root/coverage" python3 -m coverage run \
  --branch \
  --source=runtimes/python/src/vyral_runtime \
  -m unittest discover -s runtimes/python/tests -p 'test_*.py'
COVERAGE_FILE="$work_root/coverage" python3 -m coverage report \
  --fail-under=77.5 \
  --precision=1
python3 -m mypy \
  --config-file runtimes/python/pyproject.toml \
  runtimes/python/src/vyral_runtime \
  runtimes/python/tests/typecheck_consumer.py \
  runtimes/python/tests/test_record_store.py

launcher_root="$work_root/source-launcher"
mkdir -p "$launcher_root"
(
  cd "$launcher_root"
  "$ROOT/scripts/vyral" >first-run.txt
  "$ROOT/scripts/vyral" inspect --json >inspection.json
  "$ROOT/scripts/vyral" quickstart --reset >/dev/null
)
if ! grep -q 'Citation-ready context' "$launcher_root/first-run.txt"; then
  echo "The source launcher did not produce useful local context." >&2
  exit 1
fi
if [[ -e "$launcher_root/.vyral/quickstart" ]]; then
  echo "The source launcher reset left its owned state directory." >&2
  exit 1
fi
printf 'python-runtime-source-launcher=ok\n'

python3 -m build \
  --sdist \
  --wheel \
  --outdir "$work_root/dist" \
  runtimes/python

wheel="$(find "$work_root/dist" -maxdepth 1 -type f -name 'vyral-0.1.2-*.whl' -print -quit)"
sdist="$work_root/dist/vyral-0.1.2.tar.gz"
if [[ -z "$wheel" || ! -f "$sdist" ]]; then
  echo "Python runtime build did not produce the expected wheel and source distribution." >&2
  exit 1
fi

python3 - "$wheel" "$sdist" <<'PY'
from __future__ import annotations

import sys
import tarfile
import zipfile

wheel_path, sdist_path = sys.argv[1:]
required_package_files = {
    "vyral_runtime/__main__.py",
    "vyral_runtime/_local_experience.py",
    "vyral_runtime/_starter.py",
    "vyral_runtime/_contracts/public-sdk-surface.json",
    "vyral_runtime/_contracts/vyral-public.schema.json",
    "vyral_runtime/_contracts/vyral.openapi.json",
    "vyral_runtime/integrations/ripgrep.py",
    "vyral_runtime/_conformance/runtime/v1/manifest.json",
    "vyral_runtime/_conformance/runtime/v1/manifest.schema.json",
    "vyral_runtime/_conformance/runtime/v1/scenario.schema.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/primitives-hashing.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/collection-snapshot-hash.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/record-search-projection-generation.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/embedding-vectors.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/rag-ingestion-plan.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/goldens/graph-record-mapping.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/records/core-crud.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/records/query-semantics.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/retrieval/generation-bound-lifecycle.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/external-workers/handler-lifecycle.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/canonical/strong-profile.json",
    "vyral_runtime/_conformance/runtime/v1/scenarios/execution/native-lifecycle.json",
    "vyral_runtime/py.typed",
}
with zipfile.ZipFile(wheel_path) as archive:
    names = set(archive.namelist())
    missing = sorted(required_package_files - names)
    if missing:
        raise SystemExit("Python runtime wheel is missing: " + ", ".join(missing))
    if not any(name.endswith(".dist-info/licenses/LICENSE") for name in names):
        raise SystemExit("Python runtime wheel is missing the Apache license.")
    entry_points = [
        name for name in names if name.endswith(".dist-info/entry_points.txt")
    ]
    if len(entry_points) != 1:
        raise SystemExit("Python runtime wheel is missing its server entry point.")
    console_scripts = archive.read(entry_points[0]).decode("utf-8")
    for command in ("vyral =", "vyral-runtime ="):
        if command not in console_scripts:
            raise SystemExit(
                f"Python runtime wheel is missing its {command[:-2]} command."
            )

with tarfile.open(sdist_path, "r:gz") as archive:
    names = {name.split("/", 1)[1] for name in archive.getnames() if "/" in name}
    required_sdist_files = {
        "LICENSE",
        "pyproject.toml",
        "README.md",
        "src/vyral_runtime/__main__.py",
        "src/vyral_runtime/_local_experience.py",
        "src/vyral_runtime/_starter.py",
        "src/vyral_runtime/_contracts/public-sdk-surface.json",
        "src/vyral_runtime/_contracts/vyral-public.schema.json",
        "src/vyral_runtime/_contracts/vyral.openapi.json",
        "src/vyral_runtime/integrations/ripgrep.py",
        "src/vyral_runtime/_conformance/runtime/v1/manifest.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/primitives-hashing.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/collection-snapshot-hash.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/record-search-projection-generation.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/embedding-vectors.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/rag-ingestion-plan.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/goldens/graph-record-mapping.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/records/core-crud.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/records/query-semantics.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/retrieval/generation-bound-lifecycle.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/external-workers/handler-lifecycle.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/canonical/strong-profile.json",
        "src/vyral_runtime/_conformance/runtime/v1/scenarios/execution/native-lifecycle.json",
    }
    missing = sorted(required_sdist_files - names)
    if missing:
        raise SystemExit("Python runtime source distribution is missing: " + ", ".join(missing))
PY

python3 -m venv "$work_root/venv"
"$work_root/venv/bin/python" -m pip install \
  --disable-pip-version-check \
  --no-deps \
  "$wheel"
"$work_root/venv/bin/python" - <<'PY'
from vyral_runtime import (
    VyralRuntime,
    run_bundled_canonical_scenario,
    run_bundled_native_execution_scenario,
    run_bundled_external_worker_scenario,
    run_bundled_goldens,
    run_bundled_projection_generation_scenario,
    run_bundled_record_store_scenario,
    run_bundled_record_store_scenarios,
)

runtime = VyralRuntime()
readiness = runtime.readiness()
assert readiness.status == "ok"
assert readiness.full_local_ready is False
assert readiness.contract is not None
assert readiness.contract.operation_count == 129
assert readiness.contract.rest_operation_count == 133
assert readiness.contract.schema_count == 263
assert len(run_bundled_goldens()) == 13
assert len(run_bundled_projection_generation_scenario()) == 15
assert len(run_bundled_record_store_scenario()) == 17
assert len(run_bundled_record_store_scenarios()) == 30
assert len(run_bundled_external_worker_scenario()) == 3
assert len(run_bundled_canonical_scenario()) == 6
assert len(run_bundled_native_execution_scenario()) == 7
print(
    "python-runtime-wheel=ok "
    f"operations={readiness.contract.operation_count} "
    f"schemas={readiness.contract.schema_count} "
    f"goldens={len(run_bundled_goldens())}"
)
PY

quickstart_root="$work_root/local-quickstart"
"$work_root/venv/bin/vyral" quickstart \
  --root "$quickstart_root" \
  --json >"$work_root/quickstart-first.json"
"$work_root/venv/bin/vyral" inspect \
  --root "$quickstart_root" \
  --json >"$work_root/quickstart-inspection.json"
"$work_root/venv/bin/python" -m vyral_runtime quickstart \
  --root "$quickstart_root" \
  --json >"$work_root/quickstart-replay.json"
"$work_root/venv/bin/python" - \
  "$work_root/quickstart-first.json" \
  "$work_root/quickstart-inspection.json" \
  "$work_root/quickstart-replay.json" <<'PY'
from __future__ import annotations

import json
from pathlib import Path
import sys

first_path, inspection_path, replay_path = map(Path, sys.argv[1:])
first = json.loads(first_path.read_text(encoding="utf-8"))
inspection = json.loads(inspection_path.read_text(encoding="utf-8"))
replay = json.loads(replay_path.read_text(encoding="utf-8"))

assert first["topology"] == "local-single-node"
assert first["maturity"] == "prototype"
assert first["fullLocalReady"] is False
assert first["retrieval"]["mode"] == "lexical"
assert first["embedding"]["used"] is False
assert first["embedding"]["provider"] == "local-token-hash"
assert first["embedding"]["requiresNetwork"] is False
assert len(first["retrieval"]["citations"]) >= 1
assert first["execution"]["admittedStatus"] == "queued"
assert first["execution"]["persistedStatusAfterReopen"] == "queued"
assert first["execution"]["completedStatus"] == "succeeded"
assert first["execution"]["dispatchedRuns"] == 1
assert 0 <= first["timings"]["firstCitationMs"] <= 300_000
assert (
    first["timings"]["firstCitationMs"]
    <= first["timings"]["durableReceiptMs"]
    <= first["timings"]["completedMs"]
)
assert inspection["topology"] == "local-single-node"
assert inspection["providers"]["records"]["healthy"] is True
assert inspection["providers"]["execution"]["healthy"] is True
assert replay["execution"]["runId"] == first["execution"]["runId"]
assert replay["execution"]["admissionReplayed"] is True
assert replay["execution"]["dispatchedRuns"] == 0
assert replay["retrieval"]["createdChunks"] == 0
assert replay["retrieval"]["reusedChunks"] == 3
print(
    "python-runtime-local-quickstart=ok "
    f"run={first['execution']['runId']} "
    f"citations={len(first['retrieval']['citations'])} "
    f"first-citation-ms={first['timings']['firstCitationMs']}"
)
PY
"$work_root/venv/bin/vyral" quickstart \
  --root "$quickstart_root" \
  --reset >/dev/null
if [[ -e "$quickstart_root" ]]; then
  echo "Python runtime quickstart reset left its owned state directory." >&2
  exit 1
fi

python3 -m venv "$work_root/sdist-venv"
"$work_root/sdist-venv/bin/python" -m pip install \
  --disable-pip-version-check \
  --no-deps \
  "$sdist"
"$work_root/sdist-venv/bin/python" - <<'PY'
from vyral_runtime import (
    VyralRuntime,
    run_bundled_canonical_scenario,
    run_bundled_native_execution_scenario,
    run_bundled_external_worker_scenario,
    run_bundled_goldens,
    run_bundled_projection_generation_scenario,
    run_bundled_record_store_scenario,
)

runtime = VyralRuntime()
readiness = runtime.readiness()
assert readiness.status == "ok"
assert readiness.contract is not None
assert readiness.contract.operation_count == 129
assert len(run_bundled_goldens()) == 13
assert len(run_bundled_projection_generation_scenario()) == 15
assert len(run_bundled_record_store_scenario()) == 17
assert len(run_bundled_external_worker_scenario()) == 3
assert len(run_bundled_canonical_scenario()) == 6
assert len(run_bundled_native_execution_scenario()) == 7
print("python-runtime-sdist=ok")
PY

python3 -m venv "$work_root/server-venv"
"$work_root/server-venv/bin/python" -m pip install \
  --quiet \
  --disable-pip-version-check \
  "${wheel}[server]"
"$work_root/server-venv/bin/vyral" --help >/dev/null
"$work_root/server-venv/bin/vyral-runtime" --help >/dev/null
"$work_root/server-venv/bin/python" -m vyral_runtime.host --help \
  >/dev/null
printf 'python-runtime-server-extra=ok\n'

python3 scripts/verify-python-runtime-install.py \
  --server \
  "$work_root/dist"
