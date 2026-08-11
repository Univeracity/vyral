#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/vyral-dotnet-test-wrapper.XXXXXX")"
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT

mkdir -p "$work/bin" "$work/temp"
cat > "$work/bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == test ]]
printf '%s\n' "$TMPDIR" > "$VYRAL_TEST_CAPTURE"
touch "$TMPDIR/fixture-residue.sqlite"
exit "${VYRAL_TEST_EXIT:-0}"
SH
chmod 0755 "$work/bin/dotnet"

set +e
PATH="$work/bin:$PATH" \
TMPDIR="$work/temp" \
VYRAL_TEST_CAPTURE="$work/captured" \
VYRAL_TEST_EXIT=23 \
  "$ROOT/scripts/run-dotnet-tests.sh" fake.sln >/dev/null 2>&1
status="$?"
set -e

[[ "$status" -eq 23 ]]
test_root="$(cat "$work/captured")"
[[ "$test_root" == "$work/temp"/vyral-dotnet-tests.* ]]
[[ ! -e "$test_root" ]]
printf 'dotnet-test-wrapper=ok exit-preserved=true residue-removed=true\n'
