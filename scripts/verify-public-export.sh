#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export_args=()
release_mode=false
while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --allow-dirty)
      export_args+=(--allow-dirty --include-untracked)
      ;;
    --release)
      release_mode=true
      ;;
    *)
      echo "Usage: scripts/verify-public-export.sh [--allow-dirty] [--release]" >&2
      exit 2
      ;;
  esac
  shift
done

if [[ "$release_mode" == "true" && "${#export_args[@]}" -gt 0 ]]; then
  echo "Release verification requires a clean Git tree." >&2
  exit 2
fi

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-public-export-XXXXXX")"
first="$work_root/first"
second="$work_root/second"
cleanup() {
  rm -rf "$work_root"
}
trap cleanup EXIT

if [[ -n "${VYRAL_PUBLIC_HISTORY_DENYLIST_FILE:-}" && -n "${VYRAL_PUBLIC_HISTORY_DENYLIST:-}" ]]; then
  echo "Set only one public-history denylist source." >&2
  exit 2
fi
if [[ -n "${VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE:-}" && -n "${VYRAL_RELEASE_OWNERSHIP_DENYLIST:-}" ]]; then
  echo "Set only one release-ownership denylist source." >&2
  exit 2
fi
if [[ "$release_mode" == "true" ]]; then
  if [[ -z "${VYRAL_PUBLIC_HISTORY_DENYLIST_FILE:-}" && -z "${VYRAL_PUBLIC_HISTORY_DENYLIST:-}" ]]; then
    echo "Release verification requires an operator-owned public-history denylist." >&2
    exit 2
  fi
  if [[ -z "${VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE:-}" && -z "${VYRAL_RELEASE_OWNERSHIP_DENYLIST:-}" ]]; then
    echo "Release verification requires an operator-owned release-ownership denylist." >&2
    exit 2
  fi
  printf 'public-export-operator-policies=required\n'
fi

python3 scripts/test-export-public-tree.py

python3 scripts/export-public-tree.py "${export_args[@]}" "$first"
python3 scripts/export-public-tree.py "${export_args[@]}" "$second"
diff --brief --recursive "$first" "$second" >/dev/null
printf 'public-export-reproducibility=ok\n'

python3 - "$first" <<'PY'
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import sys

root = Path(sys.argv[1])
manifest_path = root / "PUBLIC-EXPORT-MANIFEST.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
files = manifest.get("files")
if not isinstance(files, list) or manifest.get("fileCount") != len(files):
    raise SystemExit("Public export manifest has an invalid file list or count.")

listed = {entry["path"]: entry for entry in files}
actual = {
    path.relative_to(root).as_posix()
    for path in root.rglob("*")
    if path.is_file() and path != manifest_path
}
if set(listed) != actual:
    raise SystemExit("Public export manifest does not exactly describe the exported files.")

tree_hasher = hashlib.sha256()
for relative in sorted(listed):
    entry = listed[relative]
    path = root / relative
    sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
    mode = f"{path.stat().st_mode & 0o777:o}"
    if entry.get("sha256") != sha256 or entry.get("mode") != mode:
        raise SystemExit(f"Public export manifest mismatch: {relative}")
    tree_hasher.update(f"{mode} {sha256} {relative}\n".encode("utf-8"))
if tree_hasher.hexdigest() != manifest.get("treeSha256"):
    raise SystemExit("Public export tree digest is invalid.")

forbidden_parts = {
    ".agent-artifacts", ".agent-state", ".agents", ".claude", ".codex", ".git",
    ".mypy_cache", ".pytest_cache", ".terraform", ".venv", "Inbox", "__pycache__",
    "artifacts", "bin", "dist", "node_modules", "obj",
}
for relative in actual:
    path = Path(relative)
    if any(part in forbidden_parts or part.startswith(".vyral") for part in path.parts):
        raise SystemExit(f"Forbidden generated/private path in public export: {relative}")
    if path.suffix.lower() in {".dll", ".key", ".nupkg", ".p12", ".pdb", ".pem", ".pfx", ".pyc", ".so", ".snupkg", ".sqlite", ".whl"}:
        raise SystemExit(f"Forbidden artifact in public export: {relative}")

private_terms = ("valu" + "ation", "annual recurring " + "revenue", "competitor " + "comps")
private_language = re.compile(
    r"\b(?:" + "|".join(re.escape(term) for term in private_terms) + r")\b",
    re.IGNORECASE,
)
for relative in sorted(actual):
    path = root / relative
    try:
        content = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    if private_language.search(content):
        raise SystemExit(f"Private strategy language appears in public content: {relative}")

print(f"public-export-manifest=ok files={len(actual)}")
PY

# The public repository retains an export manifest as release evidence. Every invocation verifies a
# regenerated deterministic export; release mode additionally requires the retained manifest to
# describe this exact clean commit. Pull requests deliberately do not require that exact match:
# dependency automation cannot regenerate provenance evidence, while the release procedure performs
# the exact check from a clean reviewed commit.
if [[ -f "$ROOT/PUBLIC-EXPORT-MANIFEST.json" ]]; then
  if [[ "$release_mode" == "true" ]]; then
    python3 - "$ROOT/PUBLIC-EXPORT-MANIFEST.json" "$first/PUBLIC-EXPORT-MANIFEST.json" <<'PY'
from __future__ import annotations

import json
from pathlib import Path
import sys


checked = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
generated = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
if checked.get("sourceDirty") is not False:
    raise SystemExit("Checked-in public-export manifest must describe a clean source tree.")

fields = ("schemaVersion", "fileCount", "files", "treeSha256")
if any(checked.get(field) != generated.get(field) for field in fields):
    raise SystemExit(
        "Checked-in public-export manifest is stale; regenerate it from the current public tree."
    )

print("public-export-current-manifest=ok")
PY
  else
    printf 'public-export-current-manifest=release-only\n'
  fi
else
  printf 'public-export-current-manifest=not-applicable\n'
fi

while IFS= read -r -d '' json_file; do
  jq empty "$json_file"
done < <(find "$first" -type f -name '*.json' -print0 | sort -z)
printf 'public-export-json=ok\n'

python3 "$first/scripts/verify-markdown-links.py" --all "$first"

(
  cd "$first"
  git init --quiet
  git add --all
  git -c user.name='Vyral Release Bot' -c user.email='release@example.invalid' \
    commit --quiet -m 'Initial public export'

  history_policy='(^|[^A-Za-z0-9_])valu''ation([^A-Za-z0-9_]|$)|annual recurring ''revenue|competitor ''comps'
  if [[ -n "${VYRAL_PUBLIC_HISTORY_DENYLIST_FILE:-}" ]]; then
    history_file="$VYRAL_PUBLIC_HISTORY_DENYLIST_FILE"
    if [[ "$history_file" != /* ]]; then
      history_file="$ROOT/$history_file"
    fi
    history_policy+=$'\n'
    history_policy+="$(< "$history_file")"
  elif [[ -n "${VYRAL_PUBLIC_HISTORY_DENYLIST:-}" ]]; then
    history_policy+=$'\n'
    history_policy+="$VYRAL_PUBLIC_HISTORY_DENYLIST"
  fi

  ownership_policy="${VYRAL_RELEASE_OWNERSHIP_DENYLIST:-}"
  if [[ -n "${VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE:-}" ]]; then
    ownership_file="$VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE"
    if [[ "$ownership_file" != /* ]]; then
      ownership_file="$ROOT/$ownership_file"
    fi
    ownership_policy="$(< "$ownership_file")"
  fi

  scripts/scan-release-secrets.sh
  env -u VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE \
    VYRAL_RELEASE_OWNERSHIP_DENYLIST="$ownership_policy" \
    scripts/scan-release-ownership.sh
  env -u VYRAL_PUBLIC_HISTORY_DENYLIST_FILE \
    VYRAL_PUBLIC_HISTORY_DENYLIST="$history_policy" \
    scripts/scan-release-history.sh
)

if [[ -n "${VYRAL_PUBLIC_EXPORT_EVIDENCE_DIR:-}" ]]; then
  evidence_dir="$VYRAL_PUBLIC_EXPORT_EVIDENCE_DIR"
  if [[ "$evidence_dir" != /* ]]; then
    evidence_dir="$ROOT/$evidence_dir"
  fi
  mkdir -p "$evidence_dir"
  cp "$first/PUBLIC-EXPORT-MANIFEST.json" "$evidence_dir/PUBLIC-EXPORT-MANIFEST.json"
  printf 'public-export-evidence=%s\n' "$evidence_dir/PUBLIC-EXPORT-MANIFEST.json"
fi

printf 'public-export-verification=ok\n'
