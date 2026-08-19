#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

IMAGE="${1:?Container image reference is required}"
OUTPUT="${2:?Qualification receipt path is required}"
OCI_ARCHIVE="${3:-}"
BUILD_METADATA="${4:-}"
if [[ "$IMAGE" == -* || ! "$IMAGE" =~ ^[A-Za-z0-9._:/@-]+$ ]]; then
  echo "Container image reference contains unsupported characters." >&2
  exit 1
fi
if [[ "$OUTPUT" != /* ]]; then
  OUTPUT="$ROOT/$OUTPUT"
fi
if [[ -n "$OCI_ARCHIVE" && "$OCI_ARCHIVE" != /* ]]; then
  OCI_ARCHIVE="$ROOT/$OCI_ARCHIVE"
fi
if [[ -n "$BUILD_METADATA" && "$BUILD_METADATA" != /* ]]; then
  BUILD_METADATA="$ROOT/$BUILD_METADATA"
fi
if [[ -n "$BUILD_METADATA" && -z "$OCI_ARCHIVE" ]]; then
  echo "Build metadata requires the corresponding OCI archive." >&2
  exit 1
fi
mkdir -p "$(dirname "$OUTPUT")"

IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$IMAGE")"
IMAGE_USER="$(docker image inspect --format '{{.Config.User}}' "$IMAGE")"
IMAGE_OS="$(docker image inspect --format '{{.Os}}' "$IMAGE")"
IMAGE_ARCHITECTURE="$(docker image inspect --format '{{.Architecture}}' "$IMAGE")"
case "${IMAGE_USER,,}" in
  ""|0|0:*|root|root:*)
    echo "The packaged hosted-worker image must configure a non-root runtime user." >&2
    exit 1
    ;;
esac

ARCHIVED_CONFIG_DIGEST=""
ARCHIVED_ARTIFACT_DIGEST=""
if [[ -n "$OCI_ARCHIVE" ]]; then
  if [[ ! -f "$OCI_ARCHIVE" || ! -f "$BUILD_METADATA" ]]; then
    echo "The archived image or its build metadata is missing." >&2
    exit 1
  fi
  OCI_IDENTITY="$(python3 "$ROOT/scripts/verify-oci-image-identity.py" \
    "$OCI_ARCHIVE" \
    "$BUILD_METADATA" \
    --os "$IMAGE_OS" \
    --architecture "$IMAGE_ARCHITECTURE")"
  IFS=$'\t' read -r ARCHIVED_CONFIG_DIGEST ARCHIVED_ARTIFACT_DIGEST <<<"$OCI_IDENTITY"
  if [[ "$ARCHIVED_CONFIG_DIGEST" != "$IMAGE_ID" ]]; then
    echo "The loaded hosted-worker image config does not match the archived OCI artifact." >&2
    exit 1
  fi
fi

PORT="${VYRAL_HOSTED_WORKER_QUALIFICATION_PORT:-}"
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
  echo "VYRAL_HOSTED_WORKER_QUALIFICATION_PORT must be an unprivileged TCP port." >&2
  exit 1
fi

CONTAINER_NAME="vyral-hosted-worker-qualification-$PORT-$$"
CONTAINER_ID=""
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/vyral-hosted-worker-qualification-XXXXXX")"
cleanup() {
  local status=$?
  if [[ -n "$CONTAINER_ID" ]]; then
    if [[ "$status" -ne 0 ]]; then
      echo "Packaged hosted-worker container log (last 120 lines):" >&2
      docker logs --tail 120 "$CONTAINER_NAME" >&2 || true
    fi
    docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  fi
  rm -rf "$TEMP_DIR"
  exit "$status"
}
trap cleanup EXIT INT TERM

CONTAINER_ID="$(docker run \
  --detach \
  --name "$CONTAINER_NAME" \
  --publish "127.0.0.1:${PORT}:8080" \
  --read-only \
  --tmpfs /app/.vyral:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=0700 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  --pids-limit 256 \
  --env ASPNETCORE_ENVIRONMENT=Development \
  --env HostedWorker__VyralUrl=http://127.0.0.1:9 \
  --env HostedWorker__WorkerId=qualification-worker \
  --env HostedWorker__HandlerIds__0=vyral.artifacts.record-ingest \
  --env HostedWorker__TaskAuthentication__Mode=development-header \
  --env HostedWorker__TaskAuthentication__AllowedPrincipals__0=qualification-dispatcher \
  --entrypoint dotnet \
  "$IMAGE" \
  worker/Vyral.HostedWorker.dll)"

HEALTH="$TEMP_DIR/health.json"
for _ in {1..80}; do
  if curl --fail --silent --show-error "http://127.0.0.1:${PORT}/health" > "$HEALTH"; then
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null | grep -qx true; then
    echo "The packaged hosted worker exited before becoming healthy." >&2
    exit 1
  fi
  sleep 0.25
done
python3 - "$HEALTH" <<'PY'
import json
from pathlib import Path
import sys

payload = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
if payload.get("status") != "ok" or payload.get("workerId") != "qualification-worker":
    raise SystemExit("The hosted-worker health response has the wrong identity.")
if payload.get("handlers") != ["vyral.artifacts.record-ingest"]:
    raise SystemExit("The hosted-worker health response has the wrong handler catalog.")
PY

MISSING_DISPATCH_STATUS="$(curl --silent --output "$TEMP_DIR/missing-dispatch.json" --write-out '%{http_code}' \
  --request POST \
  --header 'Content-Type: application/json' \
  --data '{"runId":"qualification-run"}' \
  "http://127.0.0.1:${PORT}/tasks/execution")"
if [[ "$MISSING_DISPATCH_STATUS" != "400" ]]; then
  echo "The hosted worker did not reject a callback without the dispatch marker." >&2
  exit 1
fi
UNAUTHENTICATED_STATUS="$(curl --silent --output "$TEMP_DIR/unauthenticated.json" --write-out '%{http_code}' \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'X-Vyral-Execution-Dispatch: 1' \
  --data '{"runId":"qualification-run"}' \
  "http://127.0.0.1:${PORT}/tasks/execution")"
if [[ "$UNAUTHENTICATED_STATUS" != "401" ]]; then
  echo "The hosted worker did not reject an unauthenticated marked callback." >&2
  exit 1
fi

IMAGE="$IMAGE" \
IMAGE_ID="$IMAGE_ID" \
IMAGE_USER="$IMAGE_USER" \
IMAGE_OS="$IMAGE_OS" \
IMAGE_ARCHITECTURE="$IMAGE_ARCHITECTURE" \
ARCHIVED_CONFIG_DIGEST="$ARCHIVED_CONFIG_DIGEST" \
ARCHIVED_ARTIFACT_DIGEST="$ARCHIVED_ARTIFACT_DIGEST" \
OUTPUT="$OUTPUT" \
python3 - <<'PY'
import json
import os
from pathlib import Path

receipt = {
    "schemaVersion": "vyral.hosted-worker-container-receipt.v1",
    "image": os.environ["IMAGE"],
    "imageId": os.environ["IMAGE_ID"],
    "imageConfiguredUser": os.environ["IMAGE_USER"],
    "imageOs": os.environ["IMAGE_OS"],
    "imageArchitecture": os.environ["IMAGE_ARCHITECTURE"],
    "archivedConfigDigest": os.environ["ARCHIVED_CONFIG_DIGEST"] or None,
    "archivedArtifactDigest": os.environ["ARCHIVED_ARTIFACT_DIGEST"] or None,
    "entrypoint": ["dotnet", "worker/Vyral.HostedWorker.dll"],
    "workerId": "qualification-worker",
    "handlerIds": ["vyral.artifacts.record-ingest"],
    "checks": {
        "healthy": True,
        "missingDispatchMarkerRejected": True,
        "unauthenticatedDispatchRejected": True,
        "readOnlyRootFilesystem": True,
        "allLinuxCapabilitiesDropped": True,
        "noNewPrivileges": True,
        "nonRootRuntimeUser": True,
    },
}
Path(os.environ["OUTPUT"]).write_text(
    json.dumps(receipt, indent=2, sort_keys=True) + "\n",
    encoding="utf-8",
)
PY

printf 'hosted-worker-container=ok image=%s receipt=%s\n' "$IMAGE" "$OUTPUT"
