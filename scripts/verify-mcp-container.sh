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
    echo "The packaged MCP image must configure a non-root runtime user." >&2
    exit 1
    ;;
esac
ARCHIVED_CONFIG_DIGEST=""
ARCHIVED_ARTIFACT_DIGEST=""
if [[ -n "$OCI_ARCHIVE" ]]; then
  if [[ ! -f "$OCI_ARCHIVE" ]]; then
    echo "The archived OCI image is missing." >&2
    exit 1
  fi
  if [[ ! -f "$BUILD_METADATA" ]]; then
    echo "The archived image build metadata is missing." >&2
    exit 1
  fi
  OCI_IDENTITY="$(python3 "$ROOT/scripts/verify-oci-image-identity.py" \
    "$OCI_ARCHIVE" \
    "$BUILD_METADATA" \
    --os "$IMAGE_OS" \
    --architecture "$IMAGE_ARCHITECTURE")"
  IFS=$'\t' read -r ARCHIVED_CONFIG_DIGEST ARCHIVED_ARTIFACT_DIGEST <<<"$OCI_IDENTITY"

  if [[ "$ARCHIVED_CONFIG_DIGEST" != "$IMAGE_ID" ]]; then
    echo "The loaded MCP image config does not match the archived OCI artifact." >&2
    exit 1
  fi
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
CONTAINER_NAME="vyral-mcp-qualification-$PORT-$$"
CONTAINER_ID=""

cleanup() {
  local status=$?
  if [[ -n "$CONTAINER_ID" ]]; then
    if [[ "$status" -ne 0 ]]; then
      echo "Packaged MCP container log (last 120 lines):" >&2
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
  --tmpfs /app/.vyral:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=0700 \
  --cap-drop ALL \
  --security-opt no-new-privileges=true \
  --pids-limit 256 \
  --env Mcp__Enabled=true \
  "$IMAGE")"

for _ in {1..120}; do
  if curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null | grep -qx true; then
    echo "The packaged MCP container exited during startup." >&2
    exit 1
  fi
  sleep 0.25
done
if ! curl --fail --silent --show-error "http://127.0.0.1:$PORT/health" >/dev/null; then
  echo "The packaged MCP container did not become healthy within 30 seconds." >&2
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
  "$ARCHIVED_ARTIFACT_DIGEST" \
  "$ROOT/contracts/public-sdk-surface.json" <<'PY'
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import http.client
import json
from pathlib import Path
import sys
import time


port = int(sys.argv[1])
output = Path(sys.argv[2])
protocol_version = "2026-07-28"
catalog = json.loads(Path(sys.argv[9]).read_text(encoding="utf-8"))
expected_tools = {
    operation["mcp"]["id"]
    for operation in catalog["operations"]
    if operation["mcp"]["exposure"] == "tool"
    and operation["mcp"]["defaultEnabled"]
}
expected_resources = {
    operation["mcp"]["id"]
    for operation in catalog["operations"]
    if operation["mcp"]["exposure"] == "resource"
    and operation["mcp"]["defaultEnabled"]
}


def payload(request_id: int, method: str) -> bytes:
    return json.dumps(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": {
                "_meta": {
                    "io.modelcontextprotocol/protocolVersion": protocol_version,
                    "io.modelcontextprotocol/clientInfo": {
                        "name": "vyral-container-qualification",
                        "version": "1.0.0",
                    },
                    "io.modelcontextprotocol/clientCapabilities": {},
                }
            },
        },
        separators=(",", ":"),
    ).encode("utf-8")


def parse_response(content: bytes, content_type: str | None) -> dict[str, object]:
    text = content.decode("utf-8")
    if content_type and "text/event-stream" in content_type.lower():
        messages: list[dict[str, object]] = []
        for event in text.replace("\r\n", "\n").split("\n\n"):
            data = "\n".join(
                line[5:].lstrip()
                for line in event.splitlines()
                if line.startswith("data:")
            )
            if data:
                parsed = json.loads(data)
                if isinstance(parsed, dict):
                    messages.append(parsed)
        if not messages:
            raise ValueError("finite SSE response contained no JSON message")
        return messages[-1]
    parsed = json.loads(text) if text else {}
    if not isinstance(parsed, dict):
        raise ValueError("MCP response was not a JSON object")
    return parsed


def request(request_id: int, method: str = "server/discover") -> tuple[int, dict[str, object], bool]:
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        connection.request(
            "POST",
            "/mcp",
            body=payload(request_id, method),
            headers={
                "Accept": "application/json, text/event-stream",
                "Content-Type": "application/json",
                "MCP-Protocol-Version": protocol_version,
                "Mcp-Method": method,
            },
        )
        response = connection.getresponse()
        content = response.read()
        document = parse_response(content, response.getheader("Content-Type"))
        return response.status, document, response.getheader("Mcp-Session-Id") is not None
    finally:
        connection.close()


status, discovery, has_session = request(1)
if status != 200 or has_session:
    raise SystemExit("Packaged MCP discovery was not a stateless HTTP 200 response.")
result = discovery.get("result")
if not isinstance(result, dict) or result.get("resultType") != "complete":
    raise SystemExit("Packaged MCP discovery did not return a complete result.")
if protocol_version not in result.get("supportedVersions", []):
    raise SystemExit("Packaged MCP discovery omitted the supported protocol version.")

status, listed, has_session = request(2, "tools/list")
if status != 200 or has_session:
    raise SystemExit("Packaged MCP tools/list was not a stateless HTTP 200 response.")
listed_result = listed.get("result")
if not isinstance(listed_result, dict):
    raise SystemExit("Packaged MCP tools/list omitted its result.")
tools = listed_result.get("tools")
if not isinstance(tools, list):
    raise SystemExit("Packaged MCP tools/list omitted its tool array.")
names = {
    str(tool.get("name"))
    for tool in tools
    if isinstance(tool, dict) and tool.get("name")
}
if names != expected_tools:
    raise SystemExit(
        "Packaged MCP tool catalog mismatch; "
        f"missing={sorted(expected_tools - names)!r}, extra={sorted(names - expected_tools)!r}."
    )

status, listed_resources, has_session = request(3, "resources/list")
if status != 200 or has_session:
    raise SystemExit("Packaged MCP resources/list was not a stateless HTTP 200 response.")
listed_resource_result = listed_resources.get("result")
if not isinstance(listed_resource_result, dict):
    raise SystemExit("Packaged MCP resources/list omitted its result.")
resources = listed_resource_result.get("resources")
if not isinstance(resources, list):
    raise SystemExit("Packaged MCP resources/list omitted its resource array.")
resource_uris = {
    str(resource.get("uri"))
    for resource in resources
    if isinstance(resource, dict) and resource.get("uri")
}
if resource_uris != expected_resources:
    raise SystemExit(
        "Packaged MCP resource catalog mismatch; "
        f"missing={sorted(expected_resources - resource_uris)!r}, "
        f"extra={sorted(resource_uris - expected_resources)!r}."
    )

wrong_host = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
try:
    content = payload(4, "server/discover")
    wrong_host.putrequest("POST", "/mcp", skip_host=True)
    wrong_host.putheader("Host", "rebinding.invalid")
    wrong_host.putheader("Accept", "application/json")
    wrong_host.putheader("Content-Type", "application/json")
    wrong_host.putheader("Content-Length", str(len(content)))
    wrong_host.putheader("MCP-Protocol-Version", protocol_version)
    wrong_host.putheader("Mcp-Method", "server/discover")
    wrong_host.endheaders(content)
    response = wrong_host.getresponse()
    response.read()
    if response.status != 403:
        raise SystemExit(
            f"Packaged MCP endpoint accepted a rebinding Host with HTTP {response.status}."
        )
finally:
    wrong_host.close()

wrong_origin = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
try:
    content = payload(5, "server/discover")
    wrong_origin.request(
        "POST",
        "/mcp",
        body=content,
        headers={
            "Accept": "application/json",
            "Content-Type": "application/json",
            "MCP-Protocol-Version": protocol_version,
            "Mcp-Method": "server/discover",
            "Origin": "https://rebinding.invalid",
        },
    )
    response = wrong_origin.getresponse()
    response.read()
    if response.status != 403:
        raise SystemExit(
            f"Packaged MCP endpoint accepted an untrusted Origin with HTTP {response.status}."
        )
finally:
    wrong_origin.close()

started = time.perf_counter()
with ThreadPoolExecutor(max_workers=16) as executor:
    responses = list(executor.map(request, range(100, 164)))
duration = time.perf_counter() - started
if any(
    status != 200
    or has_session
    or not isinstance(document.get("result"), dict)
    or document["result"].get("resultType") != "complete"
    for status, document, has_session in responses
):
    raise SystemExit("Packaged MCP concurrent stateless discovery probe failed.")

assertions = [
    "the image config selects a non-root runtime user",
    "the server starts with a read-only root filesystem and writable data tmpfs",
    "all Linux capabilities are dropped and no-new-privileges is enforced",
    "discovery, tools/list, and resources/list are stateless and session-free",
    "the packaged tool and resource catalogs exactly match the canonical default surface",
    "Development-only conformance fixtures are absent from that canonical surface",
    "untrusted Host and Origin values are rejected with HTTP 403",
    "64 concurrent self-describing discovery requests succeed",
]
if sys.argv[7]:
    assertions.insert(
        0, "the loaded image config digest matches the archived BuildKit artifact"
    )

receipt = {
    "schemaVersion": "vyral.mcp-container-qualification.v1",
    "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "status": "passed",
    "image": sys.argv[3],
    "imageId": sys.argv[4],
    "imageConfiguredUser": sys.argv[5],
    "containerPublishHost": sys.argv[6],
    "archivedConfigDigest": sys.argv[7] or None,
    "archivedArtifactDigest": sys.argv[8] or None,
    "protocolVersion": protocol_version,
    "productionCatalogToolCount": len(names),
    "productionCatalogResourceCount": len(resource_uris),
    "concurrentRequests": len(responses),
    "concurrency": 16,
    "durationMs": round(duration * 1000, 3),
    "runtimeRestrictions": [
        "read-only-root-filesystem",
        "writable-data-tmpfs",
        "all-linux-capabilities-dropped",
        "no-new-privileges",
        "pids-limit-256",
    ],
    "assertions": assertions,
}
output.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
print(
    "mcp-container-qualification=ok "
    f"tools={len(names)} concurrentRequests={len(responses)}"
)
PY
