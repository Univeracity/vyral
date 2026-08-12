#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-fresh-developer-XXXXXX")"
cleanup() {
  rm -rf "$work_root"
}
trap cleanup EXIT

public_root="$work_root/public"
receipt_root="$work_root/receipts"
mkdir -p "$receipt_root"

python3 scripts/export-public-tree.py "$public_root" >/dev/null
(
  cd "$public_root"
  python3 -I scripts/verify-source-quickstart.py \
    > "$receipt_root/source-first-use.json"
  python3 examples/python/canonical_store_cutover.py \
    --json > "$receipt_root/canonical-cutover.json"
  python3 examples/python/stateless_mcp_round_robin.py \
    --json > "$receipt_root/stateless-mcp.json"
)

jq -e '
  (.status == "passed")
  and (.isolatedInterpreter == true)
  and (.packageInstallationRequired == false)
  and (.networkRequired == false)
  and (.citationCount > 0)
  and (.durableRestartCompleted == true)
  and (.secondProcessReplayed == true)
  and (.generatedApplicationCompleted == true)
  and (.generatedApplicationReplayed == true)
  and (.safeReset == true)
  and (.completePathMs <= .budgetMs)
' "$receipt_root/source-first-use.json" >/dev/null

jq -e '
  (.transfer.hashVerifiedRestore == true)
  and (.target.tenantIsolationPreserved == true)
  and (.source.transactionId == .target.transactionId)
' "$receipt_root/canonical-cutover.json" >/dev/null

jq -e '
  (.protocolVersion == "2026-07-28")
  and (.topology.instanceCount == 2)
  and (.topology.sharedMcpSessionStore == false)
  and all(.requests[]; .status == 200 and .sessionHeaderPresent == false)
  and (.equivalentResultsAcrossInstances == true)
  and (.headerBodyMismatchRejected == true)
' "$receipt_root/stateless-mcp.json" >/dev/null

if [[ -n "${VYRAL_FRESH_DEVELOPER_ARTIFACT_DIR:-}" ]]; then
  artifact_root="$VYRAL_FRESH_DEVELOPER_ARTIFACT_DIR"
  if [[ "$artifact_root" != /* ]]; then
    artifact_root="$ROOT/$artifact_root"
  fi
  mkdir -p "$artifact_root"
  cp "$receipt_root"/*.json "$artifact_root/"
  printf 'fresh-developer-evidence=%s\n' "$artifact_root"
fi

first_use_ms="$(jq -r '.firstCommandMs' "$receipt_root/source-first-use.json")"
complete_ms="$(jq -r '.completePathMs' "$receipt_root/source-first-use.json")"
printf 'fresh-developer-path=ok export=clean install=none network=none first-command-ms=%s complete-ms=%s\n' \
  "$first_use_ms" \
  "$complete_ms"
