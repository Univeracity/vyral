#!/usr/bin/env bash
set -euo pipefail
umask 077

# Qualifies Vyral's optional OpenSearch projection against a caller-supplied, unpacked local
# OpenSearch distribution. The gate is loopback-only and runs with the OpenSearch security plugin
# disabled, so it proves data-plane mapping/index/query behavior but never AWS IAM or networking.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required." >&2
    exit 2
  }
}

require_command curl
require_command dotnet

OPENSEARCH_HOME_INPUT="${VYRAL_OPENSEARCH_HOME:-}"
if [[ -z "$OPENSEARCH_HOME_INPUT" ]]; then
  echo "Set VYRAL_OPENSEARCH_HOME to an unpacked local OpenSearch distribution with the k-NN plugin." >&2
  exit 2
fi
OPENSEARCH_HOME="$(cd "$OPENSEARCH_HOME_INPUT" && pwd)"
OPENSEARCH_BIN="$OPENSEARCH_HOME/bin/opensearch"
NATIVE_LIB_DIR="${VYRAL_OPENSEARCH_NATIVE_LIB_DIR:-$OPENSEARCH_HOME/plugins/opensearch-knn/lib}"

if [[ ! -x "$OPENSEARCH_BIN" ]]; then
  echo "VYRAL_OPENSEARCH_HOME must contain an executable bin/opensearch." >&2
  exit 2
fi
if [[ ! -d "$NATIVE_LIB_DIR" ]]; then
  echo "Set VYRAL_OPENSEARCH_NATIVE_LIB_DIR to the OpenSearch k-NN native library directory." >&2
  exit 2
fi
NATIVE_LIB_DIR="$(cd "$NATIVE_LIB_DIR" && pwd)"

PORT="${VYRAL_OPENSEARCH_LOCAL_PORT:-19200}"
if [[ ! "$PORT" =~ ^[1-9][0-9]{0,4}$ ]] || (( PORT > 65535 )); then
  echo "VYRAL_OPENSEARCH_LOCAL_PORT must be a TCP port from 1 through 65535." >&2
  exit 2
fi

STATE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vyral-opensearch-local.XXXXXX")"
LOG_FILE="$STATE_ROOT/opensearch.log"
NODE_PID=""

cleanup() {
  local exit_code="$1"
  local node_status="not-started"
  if [[ -n "$NODE_PID" ]]; then
    if kill -0 "$NODE_PID" >/dev/null 2>&1; then
      kill "$NODE_PID" >/dev/null 2>&1 || true
      wait "$NODE_PID" >/dev/null 2>&1 || true
      node_status="stopped"
    else
      wait "$NODE_PID" >/dev/null 2>&1 || true
      node_status="already-exited"
    fi
  fi
  rm -rf "$STATE_ROOT"
  echo "opensearch-local-cleanup=node:${node_status} state:deleted"
  return "$exit_code"
}
trap 'cleanup "$?"' EXIT

echo "opensearch-local-gate=starting-data-plane-qualification"
(
  cd "$OPENSEARCH_HOME"
  export LD_LIBRARY_PATH="$NATIVE_LIB_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  export OPENSEARCH_JAVA_OPTS="${VYRAL_OPENSEARCH_JAVA_OPTS:--Xms512m -Xmx512m}"
  export OPENSEARCH_INITIAL_ADMIN_PASSWORD="VyralLocal-${RANDOM}-${RANDOM}!a"
  exec "$OPENSEARCH_BIN" \
    -Ediscovery.type=single-node \
    -Eplugins.security.disabled=true \
    -Enetwork.host=127.0.0.1 \
    -Ehttp.port="$PORT" \
    -Epath.data="$STATE_ROOT/data" \
    -Epath.logs="$STATE_ROOT/logs"
) >"$LOG_FILE" 2>&1 &
NODE_PID="$!"

ENDPOINT="http://localhost:${PORT}/"
for _ in $(seq 1 60); do
  if curl --fail --silent --show-error "$ENDPOINT" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$NODE_PID" >/dev/null 2>&1; then
    echo "Local OpenSearch exited before becoming ready." >&2
    tail -n 40 "$LOG_FILE" >&2 || true
    exit 1
  fi
  sleep 1
done
if ! curl --fail --silent --show-error "$ENDPOINT" >/dev/null; then
  echo "Local OpenSearch did not become ready within 60 seconds." >&2
  tail -n 40 "$LOG_FILE" >&2 || true
  exit 1
fi

VYRAL_OPENSEARCH_LOCAL_ENDPOINT="$ENDPOINT" \
dotnet test tests/Vyral.Tests.Aws/Vyral.Tests.Aws.csproj --no-restore \
  --filter 'FullyQualifiedName~OpenSearchRecordSearchProjectionLocalTests' \
  --logger 'console;verbosity=minimal'

echo "opensearch-local-gate=ok"
