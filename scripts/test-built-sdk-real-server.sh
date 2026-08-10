#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PYTHON_WHEEL="${1:?Python wheel path is required}"
JAVASCRIPT_PACKAGE="${2:?JavaScript package path is required}"

if [[ ! -f "$PYTHON_WHEEL" ]]; then
  echo "Python wheel does not exist: $PYTHON_WHEEL" >&2
  exit 1
fi
if [[ ! -f "$JAVASCRIPT_PACKAGE" ]]; then
  echo "JavaScript package does not exist: $JAVASCRIPT_PACKAGE" >&2
  exit 1
fi

TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-built-sdk-XXXXXX")"
SERVER_PID=""
cleanup() {
  if [[ -n "$SERVER_PID" ]]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$TEST_ROOT"
}
trap cleanup EXIT

PORT="$(python3 - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
BASE_URL="http://127.0.0.1:$PORT"
API_KEY="built-sdk-test-key"

env \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="$BASE_URL" \
  DatabasePath="$TEST_ROOT/vyral.sqlite" \
  ObjectsPath="$TEST_ROOT/objects" \
  Providers__ArtifactDirectory="$TEST_ROOT/provider-runs" \
  VYRAL_API_KEY="$API_KEY" \
  dotnet run --no-launch-profile --project "$ROOT/src/Vyral.Server/Vyral.Server.csproj" \
  >"$TEST_ROOT/server.log" 2>&1 &
SERVER_PID="$!"

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
  echo "Vyral real-server SDK fixture failed to start." >&2
  sed -n '1,240p' "$TEST_ROOT/server.log" >&2
  exit 1
fi

python3 -m venv "$TEST_ROOT/python"
"$TEST_ROOT/python/bin/python" -m pip install --quiet --disable-pip-version-check "${PYTHON_WHEEL}[async,typecheck]"
"$TEST_ROOT/python/bin/python" -m mypy --strict --python-version 3.10 "$ROOT/clients/python/tests/typecheck_consumer.py"
env VYRAL_TEST_BASE_URL="$BASE_URL" VYRAL_TEST_API_KEY="$API_KEY" \
  "$TEST_ROOT/python/bin/python" "$ROOT/clients/python/tests/real_server_consumer.py"

mkdir -p "$TEST_ROOT/javascript"
npm install --ignore-scripts --no-audit --no-fund --prefix "$TEST_ROOT/javascript" "$JAVASCRIPT_PACKAGE"
cp "$ROOT/clients/javascript/test/real-server-consumer.mjs" "$TEST_ROOT/javascript/real-server-consumer.mjs"
env VYRAL_TEST_BASE_URL="$BASE_URL" VYRAL_TEST_API_KEY="$API_KEY" \
  node "$TEST_ROOT/javascript/real-server-consumer.mjs"

printf 'built-sdk-real-server=ok\n'
