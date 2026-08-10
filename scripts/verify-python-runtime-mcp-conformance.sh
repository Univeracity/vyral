#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_WHEEL="${1:?Python runtime wheel path is required}"
if [[ ! -f "$RUNTIME_WHEEL" ]]; then
  echo "Python runtime wheel does not exist: $RUNTIME_WHEEL" >&2
  exit 1
fi

readonly MCP_SPEC_VERSION="2026-07-28"
readonly MCP_CONFORMANCE_VERSION="0.2.0-alpha.11"
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-python-mcp-XXXXXX")"
EVIDENCE_ROOT="${VYRAL_PYTHON_MCP_CONFORMANCE_ARTIFACT_DIR:-$TEST_ROOT/evidence}"
RESULTS_ROOT="$EVIDENCE_ROOT/results"
mkdir -p "$RESULTS_ROOT"
SERVER_PID=""
cleanup() {
  local status=$?
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ "$status" -ne 0 && -s "$TEST_ROOT/server.log" ]]; then
    echo "Python MCP host log (last 120 lines):" >&2
    tail -120 "$TEST_ROOT/server.log" >&2
  fi
  if [[ -n "${VYRAL_PYTHON_MCP_CONFORMANCE_ARTIFACT_DIR:-}" && -s "$TEST_ROOT/server.log" ]]; then
    cp "$TEST_ROOT/server.log" "$EVIDENCE_ROOT/server.log"
  fi
  rm -rf "$TEST_ROOT"
  exit "$status"
}
trap cleanup EXIT INT TERM

if [[ -n "${VYRAL_PYTHON_MCP_CONFORMANCE_PORT:-}" ]]; then
  PORT="$VYRAL_PYTHON_MCP_CONFORMANCE_PORT"
else
  PORT="$(python3 - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
fi
if [[ ! "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1024 || PORT > 65535 )); then
  echo "VYRAL_PYTHON_MCP_CONFORMANCE_PORT must be 1024 through 65535." >&2
  exit 1
fi

python3 -m venv "$TEST_ROOT/venv"
"$TEST_ROOT/venv/bin/python" -m pip install \
  --quiet \
  --disable-pip-version-check \
  "${RUNTIME_WHEEL}[server]"

"$TEST_ROOT/venv/bin/vyral-runtime" \
  --root "$TEST_ROOT/state" \
  --host 127.0.0.1 \
  --port "$PORT" \
  --log-level warning \
  --mcp-conformance-diagnostics \
  >"$TEST_ROOT/server.log" 2>&1 &
SERVER_PID=$!

for _attempt in $(seq 1 120); do
  if curl --fail --silent --show-error \
    "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "The packaged Python MCP host exited during startup." >&2
    exit 1
  fi
  sleep 0.25
done
if ! curl --fail --silent --show-error \
  "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "The packaged Python MCP host did not become healthy." >&2
  exit 1
fi

npx -y "@modelcontextprotocol/conformance@$MCP_CONFORMANCE_VERSION" \
  server \
  --url "http://127.0.0.1:$PORT/mcp" \
  --requirements "$MCP_SPEC_VERSION" \
  --output-dir "$RESULTS_ROOT"

printf 'python-runtime-mcp-conformance=ok spec=%s runner=%s requirements=%s fullRequirements=true\n' \
  "$MCP_SPEC_VERSION" \
  "$MCP_CONFORMANCE_VERSION" \
  "$MCP_SPEC_VERSION"
