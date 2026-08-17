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
  # A public history deliberately omits private canonical history. Preserve the original
  # benchmark receipt in that history, but accept it only when the immutable root and the current
  # clean tree match their deterministic public-export manifests and the receipt is unchanged from
  # that root. Canonical release validation continues to require normal Git ancestry.
  python3 - "$REPORT" <<'PY'
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys
from typing import Any


root = Path.cwd().resolve()
report = Path(sys.argv[1]).resolve()
manifest_name = "PUBLIC-EXPORT-MANIFEST.json"


def git_bytes(*arguments: str) -> bytes:
    return subprocess.run(
        ["git", *arguments],
        check=True,
        capture_output=True,
    ).stdout


def load_manifest(content: bytes, context: str) -> dict[str, Any]:
    try:
        value = json.loads(content.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SystemExit(f"{context} public-export manifest is invalid JSON.") from exc
    if not isinstance(value, dict):
        raise SystemExit(f"{context} public-export manifest is not an object.")
    return value


def validate_manifest(
    manifest: dict[str, Any],
    actual: dict[str, tuple[str, str]],
    context: str,
) -> None:
    if manifest.get("schemaVersion") != 1 or manifest.get("sourceDirty") is not False:
        raise SystemExit(f"{context} public-export manifest is not a clean schema-version 1 export.")

    entries = manifest.get("files")
    if not isinstance(entries, list) or manifest.get("fileCount") != len(entries):
        raise SystemExit(f"{context} public-export manifest file count is invalid.")

    manifest_paths: set[str] = set()
    ordered_paths: list[str] = []
    tree_hasher = hashlib.sha256()
    for entry in entries:
        if not isinstance(entry, dict):
            raise SystemExit(f"{context} public-export manifest contains a malformed file entry.")
        relative = entry.get("path")
        mode = entry.get("mode")
        expected_digest = entry.get("sha256")
        if (
            not isinstance(relative, str)
            or not isinstance(mode, str)
            or not isinstance(expected_digest, str)
            or relative in manifest_paths
        ):
            raise SystemExit(
                f"{context} public-export manifest contains an invalid or duplicate file entry."
            )
        if actual.get(relative) != (mode, expected_digest):
            raise SystemExit(f"{context} public-export manifest mismatch: {relative}")

        manifest_paths.add(relative)
        ordered_paths.append(relative)
        tree_hasher.update(f"{mode} {expected_digest} {relative}\n".encode("utf-8"))

    if ordered_paths != sorted(ordered_paths):
        raise SystemExit(f"{context} public-export manifest paths are not ordered.")
    if manifest_paths != set(actual):
        raise SystemExit(f"{context} public-export manifest paths do not exactly match its tree.")
    if tree_hasher.hexdigest() != manifest.get("treeSha256"):
        raise SystemExit(f"{context} public-export manifest tree digest is invalid.")

try:
    report_relative = report.relative_to(root).as_posix()
except ValueError as exc:
    raise SystemExit("Benchmark report is outside the public export root.") from exc

for command in (["git", "diff", "--quiet"], ["git", "diff", "--cached", "--quiet"]):
    if subprocess.run(command, check=False).returncode != 0:
        raise SystemExit("Public-export benchmark provenance requires a clean tracked tree.")

roots = git_bytes("rev-list", "--max-parents=0", "HEAD").decode("ascii").splitlines()
if len(roots) != 1:
    raise SystemExit("Public-export benchmark provenance requires exactly one history root.")
history_root = roots[0]

try:
    root_manifest = load_manifest(
        git_bytes("show", f"{history_root}:{manifest_name}"),
        "Root",
    )
except subprocess.CalledProcessError as exc:
    raise SystemExit("Initial public-export manifest is absent from the history root.") from exc

root_actual: dict[str, tuple[str, str]] = {}
for record in git_bytes("ls-tree", "-rz", history_root).split(b"\0"):
    if not record:
        continue
    metadata, encoded_path = record.split(b"\t", 1)
    git_mode, object_type, object_id = metadata.decode("ascii").split()
    relative = encoded_path.decode("utf-8")
    if relative == manifest_name:
        continue
    if object_type != "blob" or git_mode not in {"100644", "100755"}:
        raise SystemExit(f"Root public-export path is not a regular file: {relative}")
    content = git_bytes("cat-file", "blob", object_id)
    root_actual[relative] = (git_mode[-3:], hashlib.sha256(content).hexdigest())

validate_manifest(root_manifest, root_actual, "Root")
if report_relative not in root_actual:
    raise SystemExit("Benchmark report is absent from the initial public-export manifest.")

# The retained manifest is exact evidence for the immutable public-history root and for a release
# verification. It is intentionally not refreshed for ordinary commits: otherwise harmless
# dependency maintenance would require a release-evidence rewrite before it could merge. The
# benchmark receipt is instead protected by comparing the report itself to the root receipt.
current_report = root / report_relative
if current_report.is_symlink() or not current_report.is_file():
    raise SystemExit("Benchmark report is not a regular file in the current public tree.")
current_report_entry = (
    "755" if current_report.stat().st_mode & stat.S_IXUSR else "644",
    hashlib.sha256(current_report.read_bytes()).hexdigest(),
)
if current_report_entry != root_actual[report_relative]:
    raise SystemExit("Benchmark report has changed since the initial public-export root.")
PY
  provenance="public-export-lineage"
fi

printf 'canonical-mysql-benchmark-report=ok report=%s sourceCommit=%s provenance=%s\n' \
  "$REPORT" "$source_commit" "$provenance"
