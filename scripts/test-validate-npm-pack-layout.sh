#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

valid_package='{
  "files": [
    {"path": "LICENSE"},
    {"path": "README.md"},
    {"path": "src/index.js"},
    {"path": "src/index.d.ts"}
  ]
}'

printf '[%s]\n' "$valid_package" \
  | jq -e -f scripts/validate-npm-pack-layout.jq >/dev/null
printf '{"vyral-client":%s}\n' "$valid_package" \
  | jq -e -f scripts/validate-npm-pack-layout.jq >/dev/null

if printf '[%s,%s]\n' "$valid_package" "$valid_package" \
  | jq -e -f scripts/validate-npm-pack-layout.jq >/dev/null 2>&1; then
  echo "npm pack layout accepted multiple packages." >&2
  exit 1
fi

if printf '%s\n' "$valid_package" \
  | jq '.files += [{"path":"docs/private-notes.md"}]' \
  | jq -e -f scripts/validate-npm-pack-layout.jq >/dev/null 2>&1; then
  echo "npm pack layout accepted a private release path." >&2
  exit 1
fi

printf 'npm-pack-layout-shapes=ok\n'
