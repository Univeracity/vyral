#!/usr/bin/env bash
set -euo pipefail
umask 077

# Scans tracked release content for developer-local paths and concrete cloud identities. Release
# operators can add adjacent-repository names, consumer deployment identities, and other private
# defaults through an ignored policy file without publishing those values in this repository.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

denylist_file="${VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE:-}"
denylist_inline="${VYRAL_RELEASE_OWNERSHIP_DENYLIST:-}"
if [[ -n "$denylist_file" && -n "$denylist_inline" ]]; then
  echo "Set only one release ownership denylist source." >&2
  exit 2
fi

patterns_file="$(mktemp "${TMPDIR:-/tmp}/vyral-ownership-policy-XXXXXX")"
trap 'rm -f "$patterns_file"' EXIT

if [[ -n "$denylist_file" ]]; then
  if [[ ! -f "$denylist_file" ]]; then
    echo "VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE must name a readable file." >&2
    exit 2
  fi

  absolute_denylist_file="$(cd "$(dirname "$denylist_file")" && pwd)/$(basename "$denylist_file")"
  case "$absolute_denylist_file" in
    "$ROOT"/*) relative_denylist_file="${absolute_denylist_file#"$ROOT"/}" ;;
    *)
      echo "The release ownership denylist must be inside the repository and ignored." >&2
      exit 2
      ;;
  esac
  if git ls-files --error-unmatch -- "$relative_denylist_file" >/dev/null 2>&1 ||
    ! git check-ignore -q -- "$relative_denylist_file"; then
    echo "The release ownership denylist must be untracked and covered by .gitignore." >&2
    exit 2
  fi
  cp "$absolute_denylist_file" "$patterns_file"
elif [[ -n "$denylist_inline" ]]; then
  printf '%s\n' "$denylist_inline" > "$patterns_file"
fi

sed -i 's/\r$//' "$patterns_file"
sed -i '/^[[:space:]]*$/d; /^[[:space:]]*#/d' "$patterns_file"

scan_pattern() {
  local pattern="$1"
  local category="$2"
  local status
  set +e
  python3 - "$pattern" <<'PY'
from pathlib import Path
import re
import subprocess
import sys

try:
    pattern = re.compile(sys.argv[1], re.IGNORECASE)
except re.error:
    raise SystemExit(12)

tracked = subprocess.run(
    ["git", "ls-files", "-z"],
    check=True,
    stdout=subprocess.PIPE,
).stdout.split(b"\0")
for encoded_path in tracked:
    if not encoded_path:
        continue
    path = Path(encoded_path.decode(errors="surrogateescape"))
    if pattern.search(path.as_posix()):
        raise SystemExit(10)
    if not path.is_file():
        continue
    content = path.read_bytes()
    if b"\0" in content:
        continue
    if pattern.search(content.decode("utf-8", errors="ignore")):
        raise SystemExit(11)
PY
  status=$?
  set -e
  case "$status" in
    0) return 0 ;;
    10)
      printf 'Release ownership scan found %s in a tracked path.\n' "$category" >&2
      return 1
      ;;
    11)
      printf 'Release ownership scan found %s in tracked content.\n' "$category" >&2
      return 1
      ;;
    12)
      printf 'Release ownership scan received an invalid regular expression for %s.\n' "$category" >&2
      return 2
      ;;
    *)
      printf 'Release ownership scan could not inspect tracked content for %s.\n' "$category" >&2
      return 2
      ;;
  esac
}

# These forms are never portable release defaults. Loopback development URLs, provider
# placeholders, relative repository links, and container paths remain valid.
scan_pattern '(^|[^A-Za-z0-9])(/home/[A-Za-z0-9._-]+|/Users/[A-Za-z0-9._-]+|[A-Za-z]:\\Users\\[A-Za-z0-9._-]+|/(projects|workspaces)/[A-Za-z0-9._-]+)' 'a developer-local absolute path'
scan_pattern 'amazonaws\.com/(?!123456789012(?:[/:]|$))[0-9]{12}([/:]|$)' 'a concrete AWS account identifier'
scan_pattern 'subscriptions/(?!00000000-0000-0000-0000-000000000000(?:[/?]|$))[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}([/?]|$)' 'a concrete Azure subscription identifier'
scan_pattern 'projects/(?!(?:your|example|test|local)-)[a-z][a-z0-9-]{4,28}/(locations|databases|serviceAccounts)/' 'a concrete Google Cloud project resource name'

while IFS= read -r pattern || [[ -n "$pattern" ]]; do
  if ! scan_pattern "$pattern" 'an operator-denylisted private identifier'; then
    exit 1
  fi
done < "$patterns_file"

printf 'release-ownership-scan=ok operator-policy=%s\n' "$([[ -s "$patterns_file" ]] && echo supplied || echo generic)"
