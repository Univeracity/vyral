#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-ownership-scan-test-XXXXXX")"
trap 'rm -rf "$work_root"' EXIT

repository="$work_root/repository"
mkdir -p "$repository/scripts"
cp "$ROOT/scripts/scan-release-ownership.sh" "$repository/scripts/scan-release-ownership.sh"
chmod +x "$repository/scripts/scan-release-ownership.sh"
git -C "$repository" init -q
git -C "$repository" config user.email test@example.invalid
git -C "$repository" config user.name Test
printf '%s\n' '.release-ownership-denylist' > "$repository/.gitignore"
printf '%s\n' \
  'Portable release content with https://example.invalid and /app/data examples.' \
  'Placeholders: amazonaws.com/123456789012/resource subscriptions/00000000-0000-0000-0000-000000000000/resource projects/example-project/locations/test.' \
  > "$repository/README.md"
git -C "$repository" add .gitignore README.md scripts/scan-release-ownership.sh
git -C "$repository" commit -qm baseline

(cd "$repository" && env \
  -u VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE \
  -u VYRAL_RELEASE_OWNERSHIP_DENYLIST \
  scripts/scan-release-ownership.sh) >/dev/null

assert_generic_rejected() {
  local content="$1"
  local category="$2"
  printf '%s\n' "$content" > "$repository/README.md"
  if (cd "$repository" && env \
    -u VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE \
    -u VYRAL_RELEASE_OWNERSHIP_DENYLIST \
    scripts/scan-release-ownership.sh) >/dev/null 2>&1; then
    printf 'Ownership scan accepted %s.\n' "$category" >&2
    exit 1
  fi
}

assert_generic_rejected \
  'A workstation default points at /ho''me/developer/consumer-repository.' \
  'a developer-local absolute path'
assert_generic_rejected \
  'A cloud default points at amazonaws.com/'\
'999999999999/resource.' \
  'a concrete AWS account identifier'
assert_generic_rejected \
  'A cloud default points at subscriptions/'\
'11111111-1111-1111-1111-111111111111/resource.' \
  'a concrete Azure subscription identifier'
assert_generic_rejected \
  'A cloud default points at projects/'\
'sample-prod-123/locations/us-central1.' \
  'a concrete Google Cloud project resource name'

printf '%s\n' 'Portable release content.' > "$repository/README.md"
printf '%s\n' '[' > "$repository/.release-ownership-denylist"
if (cd "$repository" && env \
  -u VYRAL_RELEASE_OWNERSHIP_DENYLIST \
  VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE=.release-ownership-denylist \
  scripts/scan-release-ownership.sh) >/dev/null 2>&1; then
  echo "Ownership scan accepted an invalid operator regular expression." >&2
  exit 1
fi

printf '%s\n' 'private-consumer-identity' > "$repository/.release-ownership-denylist"
printf '%s\n' 'The private-consumer-identity deployment is the default.' >> "$repository/README.md"
if (cd "$repository" && env \
  -u VYRAL_RELEASE_OWNERSHIP_DENYLIST \
  VYRAL_RELEASE_OWNERSHIP_DENYLIST_FILE=.release-ownership-denylist \
  scripts/scan-release-ownership.sh) >/dev/null 2>&1; then
  echo "Ownership scan accepted an operator-denylisted identity." >&2
  exit 1
fi

printf 'release-ownership-scan-test=ok\n'
