#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVER_DLL="$ROOT/src/Vyral.Server/bin/Release/net10.0/Vyral.Server.dll"
if [[ ! -f "$SERVER_DLL" ]]; then
  echo "Build Vyral.sln in Release mode before running this gate." >&2
  exit 2
fi

work_root="$(mktemp -d "${TMPDIR:-/tmp}/vyral-lexical-http-XXXXXX")"
server_pid=""
cleanup() {
  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
  rm -rf "$work_root"
}
trap cleanup EXIT

port="$(python3 - <<'PY'
from __future__ import annotations

import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
url="http://127.0.0.1:$port"

ASPNETCORE_ENVIRONMENT=Development \
DOTNET_ENVIRONMENT=Development \
ASPNETCORE_URLS="$url" \
DatabasePath="$work_root/vyral.sqlite" \
ObjectsPath="$work_root/objects" \
Providers__ArtifactDirectory="$work_root/provider-runs" \
dotnet "$SERVER_DLL" >"$work_root/server.log" 2>&1 &
server_pid="$!"

ready=false
for _ in $(seq 1 100); do
  if curl --fail --silent --max-time 1 "$url/health" >/dev/null; then
    ready=true
    break
  fi
  if ! kill -0 "$server_pid" 2>/dev/null; then
    break
  fi
  sleep 0.1
done
if [[ "$ready" != "true" ]]; then
  cat "$work_root/server.log" >&2
  echo "The isolated Vyral server did not become ready." >&2
  exit 1
fi

(
  cd "$ROOT"
  VYRAL_URL="$url" python3 examples/python/rag_quickstart.py \
    >"$work_root/python.txt"
  VYRAL_URL="$url" node examples/javascript/rag-quickstart.mjs \
    >"$work_root/javascript.txt"
)

for output in "$work_root/python.txt" "$work_root/javascript.txt"; do
  grep -q '^retrieval=lexical embeddings=unused$' "$output"
  grep -q '^\[c1\] memory://quickstart/retention$' "$output"
  grep -q '^contextTextHash=sha256:' "$output"
  grep -q '^promptHash=sha256:' "$output"
done

printf 'lexical-http-quickstarts=ok server=%s languages=python,javascript\n' "$url"
