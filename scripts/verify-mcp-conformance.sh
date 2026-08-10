#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

readonly MCP_SPEC_VERSION="2026-07-28"
readonly MCP_REQUIREMENTS_VERSION="2026-07-28"
readonly MCP_CONFORMANCE_VERSION="0.2.0-alpha.11"
STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-mcp-conformance-XXXXXX")"
SERVER_LOG="$STATE_ROOT/server.log"
SECONDARY_SERVER_LOG="$STATE_ROOT/server-secondary.log"
SERVER_PID=""
SECONDARY_SERVER_PID=""
WIRE_PROXY_PID=""
EVIDENCE_ROOT="${VYRAL_MCP_CONFORMANCE_ARTIFACT_DIR:-$STATE_ROOT/evidence}"
if [[ "$EVIDENCE_ROOT" != /* ]]; then
  EVIDENCE_ROOT="$ROOT/$EVIDENCE_ROOT"
fi
RESULTS_ROOT="$EVIDENCE_ROOT/results"
WIRE_LOG="$EVIDENCE_ROOT/wire.jsonl"
mkdir -p "$RESULTS_ROOT"

cleanup() {
  local status=$?
  if [[ -n "$WIRE_PROXY_PID" ]] && kill -0 "$WIRE_PROXY_PID" 2>/dev/null; then
    kill "$WIRE_PROXY_PID" 2>/dev/null || true
    wait "$WIRE_PROXY_PID" 2>/dev/null || true
  fi
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ -n "$SECONDARY_SERVER_PID" ]] && kill -0 "$SECONDARY_SERVER_PID" 2>/dev/null; then
    kill "$SECONDARY_SERVER_PID" 2>/dev/null || true
    wait "$SECONDARY_SERVER_PID" 2>/dev/null || true
  fi
  if [[ -n "${VYRAL_MCP_CONFORMANCE_ARTIFACT_DIR:-}" && -s "$SERVER_LOG" ]]; then
    cp "$SERVER_LOG" "$EVIDENCE_ROOT/server-primary.log"
  fi
  if [[ -n "${VYRAL_MCP_CONFORMANCE_ARTIFACT_DIR:-}" && -s "$SECONDARY_SERVER_LOG" ]]; then
    cp "$SECONDARY_SERVER_LOG" "$EVIDENCE_ROOT/server-secondary.log"
  fi
  if [[ "$status" -ne 0 && -s "$SERVER_LOG" ]]; then
    echo "MCP conformance server log (last 120 lines):" >&2
    tail -120 "$SERVER_LOG" >&2
    while IFS= read -r checks; do
      failed_checks="$(jq -r \
        '.[] | select(.status == "FAILURE") | "- \(.id): \(.errorMessage // .description)"' \
        "$checks" 2>/dev/null || true)"
      if [[ -n "$failed_checks" ]]; then
        echo "MCP conformance failed checks from $checks:" >&2
        printf '%s\n' "$failed_checks" >&2
      fi
    done < <(find "$RESULTS_ROOT" -type f -name checks.json -print 2>/dev/null)
  fi
  if [[ "$status" -ne 0 && -s "$SECONDARY_SERVER_LOG" ]]; then
    echo "Secondary MCP conformance server log (last 120 lines):" >&2
    tail -120 "$SECONDARY_SERVER_LOG" >&2
  fi
  rm -rf "$STATE_ROOT"
  exit "$status"
}
trap cleanup EXIT INT TERM

allocate_port() {
  python3 - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

if [[ -n "${VYRAL_MCP_CONFORMANCE_PORT:-}" ]]; then
  PORT="$VYRAL_MCP_CONFORMANCE_PORT"
else
  PORT="$(allocate_port)"
fi

if [[ ! "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1024 || PORT > 65535 )); then
  echo "VYRAL_MCP_CONFORMANCE_PORT must be an integer from 1024 through 65535." >&2
  exit 1
fi

{
  printf 'schemaVersion=vyral.mcp-conformance-toolchain.v1\n'
  printf 'spec=%s\nrequirements=%s\nrunner=%s\n' \
    "$MCP_SPEC_VERSION" "$MCP_REQUIREMENTS_VERSION" "$MCP_CONFORMANCE_VERSION"
  printf 'node=%s\n' "$(node --version)"
  printf 'npm=%s\n' "$(npm --version)"
  dotnet --info
  uname -srm
  if command -v ldd >/dev/null 2>&1; then ldd --version 2>&1 | sed -n '1p'; fi
} > "$EVIDENCE_ROOT/toolchain.txt"

dotnet build src/Vyral.Server/Vyral.Server.csproj --configuration Release

start_secondary_server() {
  env \
    Mcp__Enabled=true \
    Mcp__ConformanceMode=true \
    Mcp__TaskPollIntervalMs=10 \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://127.0.0.1:$SECONDARY_PORT" \
    DatabasePath="$STATE_ROOT/vyral.sqlite" \
    ObjectsPath="$STATE_ROOT/objects" \
    dotnet run \
      --configuration Release \
      --no-build \
      --no-launch-profile \
      --project src/Vyral.Server/Vyral.Server.csproj \
      >>"$SECONDARY_SERVER_LOG" 2>&1 &
  SECONDARY_SERVER_PID=$!
}

wait_for_secondary_server() {
  for _ in {1..120}; do
    if curl --fail --silent --show-error "http://127.0.0.1:$SECONDARY_PORT/health" >/dev/null 2>&1; then
      return
    fi
    if ! kill -0 "$SECONDARY_SERVER_PID" 2>/dev/null; then
      echo "The secondary MCP conformance server exited during startup." >&2
      exit 1
    fi
    sleep 0.25
  done
  echo "The secondary MCP conformance server did not become healthy within 30 seconds." >&2
  exit 1
}

env \
  Mcp__Enabled=true \
  Mcp__ConformanceMode=true \
  Mcp__TaskPollIntervalMs=10 \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
  DatabasePath="$STATE_ROOT/vyral.sqlite" \
  ObjectsPath="$STATE_ROOT/objects" \
  dotnet run \
    --configuration Release \
    --no-build \
    --no-launch-profile \
    --project src/Vyral.Server/Vyral.Server.csproj \
    >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!

for _ in {1..120}; do
  if curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "The MCP conformance server exited during startup." >&2
    exit 1
  fi
  sleep 0.25
done

if ! curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "The MCP conformance server did not become healthy within 30 seconds." >&2
  exit 1
fi

SECONDARY_PORT="$(allocate_port)"
start_secondary_server
wait_for_secondary_server

WIRE_PROXY_PORT="$(allocate_port)"
python3 scripts/mcp-wire-proxy.py \
  --bind-port "$WIRE_PROXY_PORT" \
  --upstream-port "$PORT" \
  --upstream-port "$SECONDARY_PORT" \
  --output "$WIRE_LOG" &
WIRE_PROXY_PID=$!
for _ in {1..40}; do
  if python3 - "$WIRE_PROXY_PORT" 2>/dev/null <<'PY'
import socket
import sys

with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=0.25):
    pass
PY
  then
    break
  fi
  if ! kill -0 "$WIRE_PROXY_PID" 2>/dev/null; then
    echo "The MCP wire-evidence proxy exited during startup." >&2
    exit 1
  fi
  sleep 0.1
done

npx -y "@modelcontextprotocol/conformance@$MCP_CONFORMANCE_VERSION" \
  server \
  --url "http://127.0.0.1:$WIRE_PROXY_PORT/mcp" \
  --requirements "$MCP_REQUIREMENTS_VERSION" \
  --output-dir "$RESULTS_ROOT"

python3 scripts/verify-mcp-load.py \
  --url "http://127.0.0.1:$WIRE_PROXY_PORT/mcp" \
  --requests 128 \
  --concurrency 16 \
  --phase two-instance \
  --output "$EVIDENCE_ROOT/load-two-instance.json"

if ! kill -0 "$SECONDARY_SERVER_PID" 2>/dev/null; then
  echo "The secondary MCP server was unavailable before the failover probe." >&2
  exit 1
fi
kill "$SECONDARY_SERVER_PID"
wait "$SECONDARY_SERVER_PID" 2>/dev/null || true
SECONDARY_SERVER_PID=""
python3 scripts/verify-mcp-load.py \
  --url "http://127.0.0.1:$WIRE_PROXY_PORT/mcp" \
  --requests 64 \
  --concurrency 16 \
  --phase one-instance-failover \
  --output "$EVIDENCE_ROOT/load-one-instance-failover.json"

start_secondary_server
wait_for_secondary_server
python3 scripts/verify-mcp-load.py \
  --url "http://127.0.0.1:$WIRE_PROXY_PORT/mcp" \
  --requests 64 \
  --concurrency 16 \
  --phase two-instance-recovered \
  --output "$EVIDENCE_ROOT/load-two-instance-recovered.json"

python3 - \
  "$WIRE_LOG" \
  "$EVIDENCE_ROOT/summary.json" \
  "$MCP_SPEC_VERSION" \
  "$MCP_REQUIREMENTS_VERSION" \
  "$MCP_CONFORMANCE_VERSION" \
  "$EVIDENCE_ROOT/load-two-instance.json" \
  "$EVIDENCE_ROOT/load-one-instance-failover.json" \
  "$EVIDENCE_ROOT/load-two-instance-recovered.json" <<'PY'
from __future__ import annotations

from collections import Counter
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

wire_path = Path(sys.argv[1])
summary_path = Path(sys.argv[2])
entries = [
    json.loads(line)
    for line in wire_path.read_text(encoding="utf-8").splitlines()
]
connections = [entry for entry in entries if entry.get("event") == "connected"]
requests = [entry for entry in entries if entry.get("direction") == "request"]
parse_errors = [entry for entry in entries if entry.get("parseError")]
socket_errors = [entry for entry in entries if entry.get("event") == "socket-error"]
if parse_errors:
    raise SystemExit(
        f"The MCP wire recorder could not parse {len(parse_errors)} HTTP message(s)"
    )
if socket_errors:
    raise SystemExit(
        f"The MCP wire proxy recorded {len(socket_errors)} unexpected socket error(s)"
    )
upstreams = Counter(entry["upstreamIndex"] for entry in connections)
fallbacks = sum(bool(entry.get("fallback")) for entry in connections)
if set(upstreams) != {0, 1} or not all(upstreams.values()):
    raise SystemExit(
        f"The MCP conformance run did not reach both upstreams: {dict(upstreams)}"
    )
connection_upstreams = {
    entry["connection"]: entry["upstreamIndex"] for entry in connections
}
requests_by_upstream = Counter(
    connection_upstreams[entry["connection"]]
    for entry in requests
    if entry["connection"] in connection_upstreams
)
if set(requests_by_upstream) != {0, 1} or not all(requests_by_upstream.values()):
    raise SystemExit(
        "The MCP conformance run did not carry parsed HTTP requests through both "
        f"upstreams: {dict(requests_by_upstream)}"
    )
whitespace_routing_headers = [
    header
    for entry in requests
    for header in entry.get("headers", [])
    if header.get("name", "").lower() == "mcp-name"
    and (
        len(bytes.fromhex(header.get("leadingOwsHex", ""))) > 1
        or bool(header.get("trailingOwsHex"))
    )
]
if not whitespace_routing_headers:
    raise SystemExit(
        "The official MCP whitespace scenario did not produce observable raw OWS evidence"
    )
two_instance_connections = connections[-256:-128]
failover_connections = connections[-128:-64]
recovered_connections = connections[-64:]
if (
    len(two_instance_connections) != 128
    or {entry["upstreamIndex"] for entry in two_instance_connections} != {0, 1}
    or any(entry.get("fallback") for entry in two_instance_connections)
):
    raise SystemExit("The MCP two-instance load probe did not use both round-robin hosts")
if (
    len(failover_connections) != 64
    or {entry["upstreamIndex"] for entry in failover_connections} != {0}
    or not any(entry.get("fallback") for entry in failover_connections)
):
    raise SystemExit("The MCP failover probe did not route every request to the surviving host")
if (
    len(recovered_connections) != 64
    or {entry["upstreamIndex"] for entry in recovered_connections} != {0, 1}
    or any(entry.get("fallback") for entry in recovered_connections)
):
    raise SystemExit("The MCP recovery probe did not restore both round-robin upstreams")
summary = {
    "schemaVersion": "vyral.mcp-conformance.v1",
    "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "specification": sys.argv[3],
    "requirements": sys.argv[4],
    "runner": f"@modelcontextprotocol/conformance@{sys.argv[5]}",
    "status": "passed",
    "scoredFailureCount": 0,
    "topology": {
        "mode": "round-robin-per-connection",
        "serverProcesses": 2,
        "connectionCount": len(connections),
        "requestCount": len(requests),
        "connectionsByUpstream": {
            str(key): value for key, value in sorted(upstreams.items())
        },
        "requestsByUpstream": {
            str(key): value for key, value in sorted(requests_by_upstream.items())
        },
        "fallbackConnectionCount": fallbacks,
        "sessionAffinity": False,
    },
    "evidence": {
        "officialClient": True,
        "wireBytesPreservedByProxy": True,
        "nonRoutingMcpHeaderValuesHashed": True,
        "routingHeaderOwsObservationCount": len(whitespace_routing_headers),
        "wireParseErrorCount": 0,
        "socketErrorCount": 0,
    },
    "loadAndRecovery": [
        json.loads(Path(path).read_text(encoding="utf-8"))
        for path in sys.argv[6:]
    ],
}
summary_path.write_text(
    json.dumps(summary, indent=2, sort_keys=True) + "\n",
    encoding="utf-8",
)
PY

printf 'mcp-conformance=ok spec=%s runner=%s requirements=%s evidence=%s\n' \
  "$MCP_SPEC_VERSION" \
  "$MCP_CONFORMANCE_VERSION" \
  "$MCP_REQUIREMENTS_VERSION" \
  "$EVIDENCE_ROOT"
