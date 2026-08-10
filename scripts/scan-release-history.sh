#!/usr/bin/env bash
set -euo pipefail
umask 077

# Audits every reachable Git ref before the repository is made public. Current-tree scans and
# .gitignore cannot protect material that was committed in an older object, so the denylist is
# deliberately supplied by the release operator and must remain untracked.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

denylist_file="${VYRAL_PUBLIC_HISTORY_DENYLIST_FILE:-}"
denylist_inline="${VYRAL_PUBLIC_HISTORY_DENYLIST:-}"

if [[ -n "$denylist_file" && -n "$denylist_inline" ]]; then
  echo "Set only one of VYRAL_PUBLIC_HISTORY_DENYLIST_FILE or VYRAL_PUBLIC_HISTORY_DENYLIST." >&2
  exit 2
fi

if [[ -z "$denylist_file" && -z "$denylist_inline" ]]; then
  echo "Set VYRAL_PUBLIC_HISTORY_DENYLIST_FILE to an ignored, one-pattern-per-line policy file before a public release." >&2
  exit 2
fi

patterns_file="$(mktemp "${TMPDIR:-/tmp}/vyral-history-policy-XXXXXX")"
history_file="$(mktemp "${TMPDIR:-/tmp}/vyral-history-messages-XXXXXX")"
paths_file="$(mktemp "${TMPDIR:-/tmp}/vyral-history-paths-XXXXXX")"
matches_file="$(mktemp "${TMPDIR:-/tmp}/vyral-history-matches-XXXXXX")"
trap 'rm -f "$patterns_file" "$history_file" "$paths_file" "$matches_file"' EXIT

if [[ -n "$denylist_file" ]]; then
  if [[ ! -f "$denylist_file" ]]; then
    echo "VYRAL_PUBLIC_HISTORY_DENYLIST_FILE must name a readable file." >&2
    exit 2
  fi

  absolute_denylist_file="$(cd "$(dirname "$denylist_file")" && pwd)/$(basename "$denylist_file")"
  case "$absolute_denylist_file" in
    "$ROOT"/*) relative_denylist_file="${absolute_denylist_file#"$ROOT"/}" ;;
    *)
      echo "VYRAL_PUBLIC_HISTORY_DENYLIST_FILE must be inside the repository and ignored." >&2
      exit 2
      ;;
  esac

  if git ls-files --error-unmatch -- "$relative_denylist_file" >/dev/null 2>&1 || ! git check-ignore -q -- "$relative_denylist_file"; then
    echo "VYRAL_PUBLIC_HISTORY_DENYLIST_FILE must be untracked and covered by .gitignore." >&2
    exit 2
  fi

  cat "$absolute_denylist_file" > "$patterns_file"
else
  printf '%s\n' "$denylist_inline" > "$patterns_file"
fi

sed -i 's/\r$//' "$patterns_file"
sed -i '/^[[:space:]]*$/d; /^[[:space:]]*#/d' "$patterns_file"
if [[ ! -s "$patterns_file" ]]; then
  echo "The public-history denylist must contain at least one non-comment pattern." >&2
  exit 2
fi

git log --all --format='%B%n' > "$history_file"
git log --all --name-only --format='' > "$paths_file"

history_matches_content() {
  local pattern="$1"
  if ! git log --all --format='%H' -i -G "$pattern" -- . > "$matches_file"; then
    echo "The public-history denylist contains an invalid Git regular expression." >&2
    return 2
  fi
  [[ -s "$matches_file" ]]
}

history_matches_text_file() {
  local pattern="$1"
  local file="$2"
  rg -i --quiet -- "$pattern" "$file"
  local status=$?
  if [[ "$status" -gt 1 ]]; then
    echo "The public-history denylist contains an invalid search regular expression." >&2
    return 2
  fi
  return "$status"
}

for credential_pattern in \
  'AKIA[0-9A-Z]{16}' \
  'ASIA[0-9A-Z]{16}' \
  'gh[pousr]_[A-Za-z0-9_]{20,}' \
  'github_pat_[A-Za-z0-9_]{20,}' \
  'AIza[0-9A-Za-z_-]{35}' \
  '-----BEGIN.*PRIVATE'" KEY-----" \
  'AccountKey=[A-Za-z0-9+/]{40,}={0,2}'; do
  if history_matches_content "$credential_pattern"; then
    echo "Credential-shaped material appears in reachable Git history." >&2
    exit 1
  fi
done
printf 'release-history-credential-scan=ok\n'

while IFS= read -r pattern || [[ -n "$pattern" ]]; do
  if history_matches_content "$pattern" || history_matches_text_file "$pattern" "$history_file" || history_matches_text_file "$pattern" "$paths_file"; then
    echo "A private-history denylist pattern appears in reachable Git history." >&2
    exit 1
  fi
done < "$patterns_file"

printf 'release-history-policy-scan=ok\n'
