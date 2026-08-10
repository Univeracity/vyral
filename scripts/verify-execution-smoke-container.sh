#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

IMAGE="${1:?Container image reference is required}"
OUTPUT="${2:?Qualification receipt path is required}"
OCI_ARCHIVE="${3:?OCI archive path is required}"
BUILD_METADATA="${4:?Build metadata path is required}"
if [[ "$IMAGE" == -* || ! "$IMAGE" =~ ^[A-Za-z0-9._:/@-]+$ ]]; then
  echo "Container image reference contains unsupported characters." >&2
  exit 1
fi
for variable in OUTPUT OCI_ARCHIVE BUILD_METADATA; do
  value="${!variable}"
  if [[ "$value" != /* ]]; then
    printf -v "$variable" '%s/%s' "$ROOT" "$value"
  fi
done
if [[ ! -f "$OCI_ARCHIVE" || ! -f "$BUILD_METADATA" ]]; then
  echo "The execution-smoke OCI archive or build metadata is missing." >&2
  exit 1
fi
mkdir -p "$(dirname "$OUTPUT")"

IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$IMAGE")"
IMAGE_USER="$(docker image inspect --format '{{.Config.User}}' "$IMAGE")"
IMAGE_OS="$(docker image inspect --format '{{.Os}}' "$IMAGE")"
IMAGE_ARCHITECTURE="$(docker image inspect --format '{{.Architecture}}' "$IMAGE")"
case "${IMAGE_USER,,}" in
  ""|0|0:*|root|root:*)
    echo "The execution-smoke image must configure a non-root runtime user." >&2
    exit 1
    ;;
esac
OCI_IDENTITY="$(python3 "$ROOT/scripts/verify-oci-image-identity.py" \
  "$OCI_ARCHIVE" \
  "$BUILD_METADATA" \
  --os "$IMAGE_OS" \
  --architecture "$IMAGE_ARCHITECTURE")"
IFS=$'\t' read -r ARCHIVED_CONFIG_DIGEST ARCHIVED_ARTIFACT_DIGEST <<<"$OCI_IDENTITY"
if [[ "$IMAGE_ID" != "$ARCHIVED_CONFIG_DIGEST" ]]; then
  echo "The loaded execution-smoke image does not match the archived OCI artifact." >&2
  exit 1
fi

PORT="${VYRAL_CONTAINER_QUALIFICATION_PORT:-}"
if [[ -z "$PORT" ]]; then
  PORT="$(python3 - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
fi
if [[ ! "$PORT" =~ ^[0-9]+$ ]] || (( PORT < 1024 || PORT > 65535 )); then
  echo "VYRAL_CONTAINER_QUALIFICATION_PORT must be an unprivileged TCP port." >&2
  exit 1
fi
PUBLISH_HOST="${VYRAL_CONTAINER_QUALIFICATION_PUBLISH_HOST:-127.0.0.1}"
if [[ "$PUBLISH_HOST" != "127.0.0.1" && "$PUBLISH_HOST" != "0.0.0.0" ]]; then
  echo "VYRAL_CONTAINER_QUALIFICATION_PUBLISH_HOST must be 127.0.0.1 or 0.0.0.0." >&2
  exit 1
fi
CONTAINER_NAME="vyral-execution-smoke-qualification-$PORT-$$"
CONTAINER_ID=""

cleanup() {
  local status=$?
  if [[ -n "$CONTAINER_ID" ]]; then
    if [[ "$status" -ne 0 ]]; then
      echo "Execution-smoke container log (last 120 lines):" >&2
      docker logs --tail 120 "$CONTAINER_NAME" >&2 || true
    fi
    docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

CONTAINER_ID="$(docker run \
  --detach \
  --name "$CONTAINER_NAME" \
  --publish "$PUBLISH_HOST:$PORT:8080" \
  --read-only \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  --pids-limit 128 \
  --env PORT=8080 \
  --env VYRAL_SERVER_URL=http://127.0.0.1:1 \
  --env VYRAL_SERVER_AUTH_MODE=none \
  --env VYRAL_WORKER_ID=qualification-worker \
  --env VYRAL_HANDLER_IDS=qualification.echo \
  "$IMAGE")"

for _ in {1..120}; do
  if curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null | grep -qx true; then
    echo "The execution-smoke container exited during startup." >&2
    exit 1
  fi
  sleep 0.25
done
if ! curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "The execution-smoke container did not become healthy within 30 seconds." >&2
  exit 1
fi

python3 - \
  "$PORT" \
  "$OUTPUT" \
  "$IMAGE" \
  "$IMAGE_ID" \
  "$IMAGE_USER" \
  "$PUBLISH_HOST" \
  "$ARCHIVED_CONFIG_DIGEST" \
  "$ARCHIVED_ARTIFACT_DIGEST" <<'PY'
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import http.client
import json
from pathlib import Path
import sys
import time


port = int(sys.argv[1])


def request(method: str, path: str, body: bytes | None = None) -> int:
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        headers = {"Content-Type": "application/json"} if body is not None else {}
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        response.read()
        return response.status
    finally:
        connection.close()


if request("GET", "/health") != 200:
    raise SystemExit("Execution-smoke health endpoint failed.")
if request("GET", "/tasks/execution") != 405:
    raise SystemExit("Execution-smoke task endpoint accepted an unsupported method.")
if request("POST", "/tasks/execution", b"{}") != 400:
    raise SystemExit("Execution-smoke task endpoint accepted a missing run id.")

started = time.perf_counter()
with ThreadPoolExecutor(max_workers=8) as executor:
    statuses = list(executor.map(lambda _: request("GET", "/health"), range(32)))
duration = time.perf_counter() - started
if any(status != 200 for status in statuses):
    raise SystemExit("Execution-smoke concurrent health probe failed.")

receipt = {
    "schemaVersion": "vyral.execution-smoke-container-qualification.v1",
    "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "status": "passed",
    "image": sys.argv[3],
    "imageId": sys.argv[4],
    "imageConfiguredUser": sys.argv[5],
    "containerPublishHost": sys.argv[6],
    "archivedConfigDigest": sys.argv[7],
    "archivedArtifactDigest": sys.argv[8],
    "concurrentRequests": len(statuses),
    "concurrency": 8,
    "durationMs": round(duration * 1000, 3),
    "runtimeRestrictions": [
        "read-only-root-filesystem",
        "all-linux-capabilities-dropped",
        "no-new-privileges",
        "pids-limit-128",
    ],
    "assertions": [
        "the loaded image config digest matches the archived BuildKit artifact",
        "the image config selects a non-root runtime user",
        "the worker starts under a read-only, capability-free runtime profile",
        "the health endpoint succeeds under concurrency",
        "the task endpoint rejects unsupported methods and missing run ids",
    ],
}
Path(sys.argv[2]).write_text(
    json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8"
)
print(
    "execution-smoke-container-qualification=ok "
    f"concurrentRequests={len(statuses)}"
)
PY
