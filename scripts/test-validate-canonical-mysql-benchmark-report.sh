#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-canonical-mysql-report-test.XXXXXX")"
trap 'rm -rf -- "$work_root"' EXIT
source_report="benchmarks/canonical-store/mysql-8.0.46-local.json"

scripts/validate-canonical-mysql-benchmark-report.sh "$source_report" >/dev/null

jq '.durability.innoDbFlushLogAtTransactionCommit = 2' "$source_report" > "$work_root/unsafe.json"
if scripts/validate-canonical-mysql-benchmark-report.sh "$work_root/unsafe.json" >/dev/null 2>&1; then
  echo "Canonical MySQL benchmark validator accepted relaxed commit durability." >&2
  exit 1
fi

jq '.connectionString = "Server=localhost;Password=secret"' "$source_report" > "$work_root/secret.json"
if scripts/validate-canonical-mysql-benchmark-report.sh "$work_root/secret.json" >/dev/null 2>&1; then
  echo "Canonical MySQL benchmark validator accepted connection material." >&2
  exit 1
fi

jq '.sameTenantWrites.operationsCompleted = 199' "$source_report" > "$work_root/incomplete.json"
if scripts/validate-canonical-mysql-benchmark-report.sh "$work_root/incomplete.json" >/dev/null 2>&1; then
  echo "Canonical MySQL benchmark validator accepted an incomplete workload." >&2
  exit 1
fi

public_root="$work_root/public-root"
mkdir -p "$public_root/scripts" "$public_root/benchmarks/canonical-store"
cp scripts/validate-canonical-mysql-benchmark-report.sh "$public_root/scripts/"
cp "$source_report" "$public_root/$source_report"
write_public_manifest() {
  python3 - "$public_root" <<'PY'
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import stat
import sys


root = Path(sys.argv[1])
entries = []
for path in sorted(
    item
    for item in root.rglob("*")
    if item.is_file()
    and ".git" not in item.relative_to(root).parts
    and item.name != "PUBLIC-EXPORT-MANIFEST.json"
):
    relative = path.relative_to(root).as_posix()
    mode = "755" if path.stat().st_mode & stat.S_IXUSR else "644"
    entries.append(
        {"path": relative, "mode": mode, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    )

tree_hasher = hashlib.sha256()
for entry in entries:
    tree_hasher.update(
        f"{entry['mode']} {entry['sha256']} {entry['path']}\n".encode("utf-8")
    )

manifest = {
    "fileCount": len(entries),
    "files": entries,
    "schemaVersion": 1,
    "sourceDirty": False,
    "treeSha256": tree_hasher.hexdigest(),
}
(root / "PUBLIC-EXPORT-MANIFEST.json").write_text(
    json.dumps(manifest, indent=2, sort_keys=True) + "\n",
    encoding="utf-8",
)
PY
}
write_public_manifest
git -C "$public_root" init --initial-branch=main --quiet
git -C "$public_root" add --all
git -C "$public_root" \
  -c user.name='Vyral Tests' \
  -c user.email='tests@openvyral.com' \
  commit --quiet --message='Initial public release'
public_result="$(
  cd "$public_root"
  scripts/validate-canonical-mysql-benchmark-report.sh "$source_report"
)"
if [[ "$public_result" != *"provenance=public-export-lineage"* ]]; then
  echo "Canonical MySQL benchmark validator did not recognize verified public-export provenance." >&2
  exit 1
fi

printf 'public maintenance\n' > "$public_root/README.md"
write_public_manifest
git -C "$public_root" add --all
git -C "$public_root" \
  -c user.name='Vyral Tests' \
  -c user.email='tests@openvyral.com' \
  commit --quiet --message='Public maintenance'
public_result="$(
  cd "$public_root"
  scripts/validate-canonical-mysql-benchmark-report.sh "$source_report"
)"
if [[ "$public_result" != *"provenance=public-export-lineage"* ]]; then
  echo "Canonical MySQL benchmark validator rejected a verified public-export descendant." >&2
  exit 1
fi

jq '.sourceDirty = true' "$public_root/PUBLIC-EXPORT-MANIFEST.json" \
  > "$work_root/dirty-manifest.json"
mv "$work_root/dirty-manifest.json" "$public_root/PUBLIC-EXPORT-MANIFEST.json"
git -C "$public_root" add PUBLIC-EXPORT-MANIFEST.json
git -C "$public_root" \
  -c user.name='Vyral Tests' \
  -c user.email='tests@openvyral.com' \
  commit --amend --quiet --no-edit
public_result="$(
  cd "$public_root"
  scripts/validate-canonical-mysql-benchmark-report.sh "$source_report"
)"
if [[ "$public_result" != *"provenance=public-export-lineage"* ]]; then
  echo "Canonical MySQL benchmark validator treated the release-only current manifest as receipt evidence." >&2
  exit 1
fi

jq '.generatedAtUtc = "2026-07-29T00:00:00Z"' "$public_root/$source_report" \
  > "$work_root/changed-report.json"
mv "$work_root/changed-report.json" "$public_root/$source_report"
git -C "$public_root" add "$source_report"
git -C "$public_root" \
  -c user.name='Vyral Tests' \
  -c user.email='tests@openvyral.com' \
  commit --quiet --message='Alter benchmark receipt'
if (
  cd "$public_root"
  scripts/validate-canonical-mysql-benchmark-report.sh "$source_report" >/dev/null 2>&1
); then
  echo "Canonical MySQL benchmark validator accepted an altered benchmark receipt." >&2
  exit 1
fi

printf 'canonical-mysql-benchmark-report-test=ok\n'
