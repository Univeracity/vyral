#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REPORT="${1:-benchmarks/canonical-store/mysql-8.0.46-local.json}"
if [[ ! -s "$REPORT" ]]; then
  echo "Canonical MySQL benchmark report is missing or empty: $REPORT" >&2
  exit 1
fi

jq -e '
  .schemaVersion == "canonical-mysql-contention/v1" and
  .databaseProduct == "mysql" and
  (.databaseVersion | type == "string" and length > 0 and length <= 160) and
  (.sourceCommit | type == "string" and test("^[0-9a-f]{40}$")) and
  .mutationTransactionIsolation == "READ-COMMITTED" and
  .durability.innoDbFlushLogAtTransactionCommit == 1 and
  (.durability.syncBinlog | type == "number" and . >= 0) and
  (.durability.binaryLogEnabled | type == "boolean") and
  .workload.workers == 8 and
  .workload.operationsPerWorker == 25 and
  .workload.warmupOperations == 8 and
  .workload.seedDocumentsPerTenant == 25 and
  .workload.payloadBytes == 256 and
  .workload.mutationShape == "one_document_plus_one_outbox_event" and
  .budgets.maxSameTenantP95Ms == 100 and
  .budgets.minSameTenantOperationsPerSecond == 50 and
  .budgets.maxWriteContentionRatio == 4 and
  ([
    .sameTenantWrites,
    .distinctTenantWrites,
    .sameTenantReplays,
    .distinctTenantReplays
  ] | all(.[];
    .operationsRequested == 200 and
    .operationsCompleted == 200 and
    .operationsFailed == 0 and
    (.failureClasses | type == "object" and length == 0) and
    (.operationsPerSecond | type == "number" and . > 0) and
    (.p95Ms | type == "number" and . > 0) and
    (.initialMaxTenantStateBytes | type == "number" and . > 0) and
    (.finalMaxTenantStateBytes | type == "number" and . > 0)
  )) and
  .sameTenantWrites.tenantCount == 1 and
  .distinctTenantWrites.tenantCount == 8 and
  .sameTenantReplays.tenantCount == 1 and
  .distinctTenantReplays.tenantCount == 8 and
  .sameTenantWrites.finalMaxTenantStateBytes > .sameTenantWrites.initialMaxTenantStateBytes and
  .sameTenantReplays.finalMaxTenantStateBytes == .sameTenantReplays.initialMaxTenantStateBytes and
  (.decision.writeContentionRatio == ((.sameTenantWrites.p95Ms / .distinctTenantWrites.p95Ms) * 1000 | round / 1000)) and
  (.decision.replayContentionRatio == ((.sameTenantReplays.p95Ms / .distinctTenantReplays.p95Ms) * 1000 | round / 1000)) and
  (.decision.withinBudget == (
    .sameTenantWrites.p95Ms <= .budgets.maxSameTenantP95Ms and
    .sameTenantWrites.operationsPerSecond >= .budgets.minSameTenantOperationsPerSecond and
    .decision.writeContentionRatio <= .budgets.maxWriteContentionRatio
  )) and
  (.decision.recommendedProfile == (if .decision.withinBudget then "mysql_tenant_row" else "postgresql_granular_or_dedicated_projection" end)) and
  ((.decision.violations | index("same_tenant_p95_exceeds_budget") | type != "null") ==
    (.sameTenantWrites.p95Ms > .budgets.maxSameTenantP95Ms)) and
  ((.decision.violations | index("same_tenant_throughput_below_budget") | type != "null") ==
    (.sameTenantWrites.operationsPerSecond < .budgets.minSameTenantOperationsPerSecond)) and
  ((.decision.violations | index("write_contention_ratio_exceeds_budget") | type != "null") ==
    (.decision.writeContentionRatio > .budgets.maxWriteContentionRatio)) and
  ([.. | objects | keys[]] | all(.[];
    test("^(connection(String)?|endpoint|host(Name)?|databaseName|tenantId|document|documentBody|payload|exception(Message)?|leaseToken)$"; "i") | not
  )) and
  ([.. | strings] | all(.[];
    test("password=|server=|host=|127\\.0\\.0\\.1|localhost"; "i") | not
  ))
' "$REPORT" >/dev/null

source_commit="$(jq -r '.sourceCommit' "$REPORT")"
provenance="ancestor"
if ! git cat-file -e "$source_commit^{commit}" 2>/dev/null ||
   ! git merge-base --is-ancestor "$source_commit" HEAD; then
  # A fresh public root deliberately omits private canonical history. Preserve the original
  # benchmark receipt in that tree, but accept it only when the entire clean, one-commit tree is
  # byte-for-byte identical to its deterministic public-export manifest. Canonical release
  # validation continues to require normal Git ancestry.
  python3 - "$REPORT" <<'PY'
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys


root = Path.cwd().resolve()
report = Path(sys.argv[1]).resolve()
manifest_path = root / "PUBLIC-EXPORT-MANIFEST.json"

try:
    report_relative = report.relative_to(root).as_posix()
except ValueError as exc:
    raise SystemExit("Benchmark report is outside the public export root.") from exc

if not manifest_path.is_file():
    raise SystemExit(
        "Canonical MySQL benchmark sourceCommit is not an ancestor and no public-export "
        "manifest is present."
    )

commit_count = subprocess.run(
    ["git", "rev-list", "--count", "HEAD"],
    check=True,
    capture_output=True,
    text=True,
).stdout.strip()
if commit_count != "1":
    raise SystemExit("Public-export benchmark provenance requires a one-commit history.")

for command in (["git", "diff", "--quiet"], ["git", "diff", "--cached", "--quiet"]):
    if subprocess.run(command, check=False).returncode != 0:
        raise SystemExit("Public-export benchmark provenance requires a clean tracked tree.")

try:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
except (OSError, json.JSONDecodeError) as exc:
    raise SystemExit("Public-export manifest is unreadable or invalid JSON.") from exc

if manifest.get("schemaVersion") != 1 or manifest.get("sourceDirty") is not False:
    raise SystemExit("Public-export manifest is not a clean schema-version 1 export.")

entries = manifest.get("files")
if not isinstance(entries, list) or manifest.get("fileCount") != len(entries):
    raise SystemExit("Public-export manifest file count is invalid.")

tracked_output = subprocess.run(
    ["git", "ls-files", "-z"],
    check=True,
    capture_output=True,
).stdout
tracked = {
    item.decode("utf-8")
    for item in tracked_output.split(b"\0")
    if item and item.decode("utf-8") != manifest_path.name
}

manifest_paths: set[str] = set()
tree_hasher = hashlib.sha256()
for entry in entries:
    if not isinstance(entry, dict):
        raise SystemExit("Public-export manifest contains a malformed file entry.")
    relative = entry.get("path")
    mode = entry.get("mode")
    expected_digest = entry.get("sha256")
    if (
        not isinstance(relative, str)
        or not isinstance(mode, str)
        or not isinstance(expected_digest, str)
        or relative in manifest_paths
    ):
        raise SystemExit("Public-export manifest contains an invalid or duplicate file entry.")

    path = root / relative
    try:
        path.resolve().relative_to(root)
    except ValueError as exc:
        raise SystemExit(f"Public-export path escapes the root: {relative}") from exc
    if path.is_symlink() or not path.is_file():
        raise SystemExit(f"Public-export path is not a regular file: {relative}")

    actual_mode = "755" if path.stat().st_mode & stat.S_IXUSR else "644"
    actual_digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if mode != actual_mode or expected_digest != actual_digest:
        raise SystemExit(f"Public-export manifest mismatch: {relative}")

    manifest_paths.add(relative)
    tree_hasher.update(f"{mode} {expected_digest} {relative}\n".encode("utf-8"))

if manifest_paths != tracked:
    raise SystemExit("Public-export manifest paths do not exactly match the tracked tree.")
if report_relative not in manifest_paths:
    raise SystemExit("Benchmark report is absent from the public-export manifest.")
if tree_hasher.hexdigest() != manifest.get("treeSha256"):
    raise SystemExit("Public-export manifest tree digest is invalid.")
PY
  provenance="public-export"
fi

printf 'canonical-mysql-benchmark-report=ok report=%s sourceCommit=%s provenance=%s\n' \
  "$REPORT" "$source_commit" "$provenance"
