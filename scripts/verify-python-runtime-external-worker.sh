#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_WHEEL="${1:?Python runtime wheel path is required}"
if [[ ! -f "$RUNTIME_WHEEL" ]]; then
  echo "Python runtime wheel does not exist: $RUNTIME_WHEEL" >&2
  exit 1
fi

TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-python-worker-host-XXXXXX")"
cleanup() {
  rm -rf "$TEST_ROOT"
}
trap cleanup EXIT INT TERM

python3 -m venv "$TEST_ROOT/venv"
"$TEST_ROOT/venv/bin/python" -m pip install \
  --quiet \
  --disable-pip-version-check \
  "${RUNTIME_WHEEL}[server]"
python3 "$ROOT/scripts/verify-python-external-worker-integration.py" \
  --server-kind python \
  --python-executable "$TEST_ROOT/venv/bin/python" \
  --authenticated
