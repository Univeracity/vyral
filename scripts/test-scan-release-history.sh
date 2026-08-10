#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-history-gate-test-XXXXXX")"
trap 'rm -rf "$STATE_ROOT"' EXIT

prepare_repository() {
  local repository="$1"
  mkdir -p "$repository/scripts"
  cp "$ROOT/scripts/scan-release-history.sh" "$repository/scripts/scan-release-history.sh"
  chmod +x "$repository/scripts/scan-release-history.sh"

  git -C "$repository" init --quiet
  git -C "$repository" config user.name "history-gate-test"
  git -C "$repository" config user.email "history-gate-test@example.test"
  printf '%s\n' '.release-history-denylist' > "$repository/.gitignore"
  printf '%s\n' 'ordinary fixture' > "$repository/record.txt"
  git -C "$repository" add .gitignore record.txt scripts/scan-release-history.sh
  git -C "$repository" commit --quiet -m 'initial fixture'
  printf '%s\n' 'internal-identifier' > "$repository/.release-history-denylist"
}

assert_rejected() {
  local repository="$1"
  if (cd "$repository" && env \
    -u VYRAL_PUBLIC_HISTORY_DENYLIST \
    VYRAL_PUBLIC_HISTORY_DENYLIST_FILE=.release-history-denylist \
    scripts/scan-release-history.sh) >/dev/null 2>&1; then
    echo "Expected the history denylist gate to reject reachable private vocabulary." >&2
    exit 1
  fi
}

content_repository="$STATE_ROOT/content-repository"
prepare_repository "$content_repository"
(cd "$content_repository" && env \
  -u VYRAL_PUBLIC_HISTORY_DENYLIST \
  VYRAL_PUBLIC_HISTORY_DENYLIST_FILE=.release-history-denylist \
  scripts/scan-release-history.sh) >/dev/null
printf '%s\n' 'internal-identifier' > "$content_repository/record.txt"
git -C "$content_repository" add record.txt
git -C "$content_repository" commit --quiet -m 'add fixture'
assert_rejected "$content_repository"

message_repository="$STATE_ROOT/message-repository"
prepare_repository "$message_repository"
git -C "$message_repository" commit --quiet --allow-empty -m 'internal-identifier fixture'
assert_rejected "$message_repository"

path_repository="$STATE_ROOT/path-repository"
prepare_repository "$path_repository"
printf '%s\n' 'ordinary fixture' > "$path_repository/internal-identifier.txt"
git -C "$path_repository" add internal-identifier.txt
git -C "$path_repository" commit --quiet -m 'add fixture path'
assert_rejected "$path_repository"

credential_repository="$STATE_ROOT/credential-repository"
prepare_repository "$credential_repository"
printf '%s%s\n' 'AKIA' 'ABCDEFGHIJKLMNOP' > "$credential_repository/credential.txt"
git -C "$credential_repository" add credential.txt
git -C "$credential_repository" commit --quiet -m 'add credential-shaped fixture'
assert_rejected "$credential_repository"

printf '%s\n' 'history-release-gate-test=ok'
