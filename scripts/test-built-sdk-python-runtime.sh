#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_WHEEL="${1:?Python runtime wheel path is required}"
PYTHON_CLIENT_WHEEL="${2:?Python client wheel path is required}"
JAVASCRIPT_PACKAGE="${3:?JavaScript client package path is required}"

for artifact in \
  "$RUNTIME_WHEEL" \
  "$PYTHON_CLIENT_WHEEL" \
  "$JAVASCRIPT_PACKAGE"; do
  if [[ ! -f "$artifact" ]]; then
    echo "Package artifact does not exist: $artifact" >&2
    exit 1
  fi
done

TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-python-host-sdk-XXXXXX")"
SERVER_PID=""
cleanup() {
  local status=$?
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ "$status" -ne 0 && -s "$TEST_ROOT/server.log" ]]; then
    echo "Python runtime host log (last 120 lines):" >&2
    tail -120 "$TEST_ROOT/server.log" >&2
  fi
  rm -rf "$TEST_ROOT"
  exit "$status"
}
trap cleanup EXIT INT TERM

PORT="$(python3 - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
BASE_URL="http://127.0.0.1:$PORT"
API_KEY="built-sdk-python-runtime-key"

python3 -m venv "$TEST_ROOT/venv"
"$TEST_ROOT/venv/bin/python" -m pip install \
  --quiet \
  --disable-pip-version-check \
  "${RUNTIME_WHEEL}[server]" \
  "${PYTHON_CLIENT_WHEEL}[async]"

env \
  VYRAL_API_KEY="$API_KEY" \
  "$TEST_ROOT/venv/bin/vyral-runtime" \
    --root "$TEST_ROOT/state" \
    --host 127.0.0.1 \
    --port "$PORT" \
    --log-level warning \
    >"$TEST_ROOT/server.log" 2>&1 &
SERVER_PID=$!

ready=0
for _attempt in $(seq 1 120); do
  if curl --fail --silent --show-error "$BASE_URL/health" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    break
  fi
  sleep 0.25
done
if [[ "$ready" != "1" ]]; then
  echo "The packaged Python runtime host failed to start." >&2
  exit 1
fi

env \
  VYRAL_TEST_BASE_URL="$BASE_URL" \
  VYRAL_TEST_API_KEY="$API_KEY" \
  "$TEST_ROOT/venv/bin/python" \
  "$ROOT/clients/python/tests/real_server_consumer.py"

mkdir -p "$TEST_ROOT/javascript"
npm install \
  --ignore-scripts \
  --no-audit \
  --no-fund \
  --prefix "$TEST_ROOT/javascript" \
  "$JAVASCRIPT_PACKAGE"
cp \
  "$ROOT/clients/javascript/test/real-server-consumer.mjs" \
  "$TEST_ROOT/javascript/real-server-consumer.mjs"
env \
  VYRAL_TEST_BASE_URL="$BASE_URL" \
  VYRAL_TEST_API_KEY="$API_KEY" \
  node "$TEST_ROOT/javascript/real-server-consumer.mjs"

printf 'python-runtime-built-sdk-real-server=ok\n'
