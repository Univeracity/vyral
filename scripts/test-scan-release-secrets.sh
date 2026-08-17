#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-secret-scan-test-XXXXXX")"
trap 'rm -rf "$work_root"' EXIT

repository="$work_root/repository"
mkdir -p "$repository/scripts"
cp "$ROOT/scripts/scan-release-secrets.sh" "$repository/scripts/scan-release-secrets.sh"
chmod +x "$repository/scripts/scan-release-secrets.sh"
git -C "$repository" init -q
git -C "$repository" config user.email test@example.invalid
git -C "$repository" config user.name Test
printf '%s\n' 'Portable release content.' > "$repository/README.md"
git -C "$repository" add README.md scripts/scan-release-secrets.sh
git -C "$repository" commit -qm baseline

(cd "$repository" && scripts/scan-release-secrets.sh) >/dev/null

printf '%s%s\n' 'github_pat_' 'ABCDEFGHIJKLMNOPQRSTUVWXYZ' > "$repository/README.md"
if (cd "$repository" && scripts/scan-release-secrets.sh) >/dev/null 2>&1; then
  echo "Secret scan accepted a credential-shaped token." >&2
  exit 1
fi
git -C "$repository" checkout -- README.md

printf '%s\n' 'tracked-ignored.txt' > "$repository/.gitignore"
printf '%s\n' 'This file is both tracked and ignored.' > "$repository/tracked-ignored.txt"
git -C "$repository" add --force .gitignore tracked-ignored.txt
git -C "$repository" commit -qm tracked-ignored-file
if (cd "$repository" && scripts/scan-release-secrets.sh) >/dev/null 2>&1; then
  echo "Secret scan accepted a tracked ignored file." >&2
  exit 1
fi

printf 'release-secret-scan-test=ok\n'
