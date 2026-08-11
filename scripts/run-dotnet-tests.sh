#!/usr/bin/env bash
set -euo pipefail
umask 077

if (( $# == 0 )); then
  echo "usage: scripts/run-dotnet-tests.sh PROJECT_OR_SOLUTION [dotnet test arguments...]" >&2
  exit 2
fi

temp_base="${TMPDIR:-/tmp}"
test_root="$(mktemp -d "$temp_base/vyral-dotnet-tests.XXXXXX")"
cleanup() {
  local status="$?"
  trap - EXIT
  case "$test_root" in
    "$temp_base"/vyral-dotnet-tests.*)
      rm -rf -- "$test_root"
      ;;
    *)
      echo "Refusing to remove unexpected test root: $test_root" >&2
      status=1
      ;;
  esac
  exit "$status"
}
trap cleanup EXIT

# Path.GetTempPath() honors TMPDIR on Unix. Keeping the complete test process
# tree under one owned directory makes cleanup deterministic even when an
# individual fixture does not remove its SQLite sidecars or artifact folders.
export TMPDIR="$test_root"
dotnet test "$@"
