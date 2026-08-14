#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PACKAGE_VERSION="${VYRAL_EXECUTION_PACKAGE_VERSION:-0.2.0}"
WORK_ROOT="${TMPDIR:-/tmp}/vyral-execution-preview-handoff-$(date +%s)-$$"
PYTHON_DIR="$WORK_ROOT/python-consumer"
JAVASCRIPT_DIR="$WORK_ROOT/javascript-consumer"

cleanup() {
  if [[ "${VYRAL_KEEP_EXECUTION_PREVIEW_HANDOFF:-0}" != "1" ]]; then
    rm -rf "$WORK_ROOT"
  else
    echo "kept-work-root=$WORK_ROOT"
  fi
}
trap cleanup EXIT

mkdir -p "$PYTHON_DIR" "$JAVASCRIPT_DIR"

echo "runtime-package-version=$PACKAGE_VERSION"
echo "dotnet-package-consumer=running"
dotnet_output_file="$WORK_ROOT/dotnet-package-consumer.txt"
if ! VYRAL_EXECUTION_PACKAGE_VERSION="$PACKAGE_VERSION" scripts/validate-execution-runtime-package-consumer.sh 2>&1 | tee "$dotnet_output_file"; then
  exit 1
fi
dotnet_output="$(cat "$dotnet_output_file")"

for expected in \
  "artifactFetchByName=True" \
  "artifactFetchById=True" \
  "checkpoint=digest" \
  "pruneDryRun=True" \
  "reconcileDryRun=True"
do
  if [[ "$dotnet_output" != *"$expected"* ]]; then
    echo "missing expected .NET handoff output: $expected" >&2
    exit 1
  fi
done
echo "dotnet-package-consumer=ok"

echo "go-external-worker-client=running"
(
  cd "$ROOT/clients/go"
  go test ./...
)
(
  cd "$ROOT/workers/execution-smoke-go"
  go test ./...
)
"$ROOT/deploy/test-preflight-google-execution.sh"
echo "go-external-worker-client=ok"

python3 -m venv "$PYTHON_DIR/.venv"
"$PYTHON_DIR/.venv/bin/python" -m pip install "$ROOT/clients/python" >/dev/null
cat > "$PYTHON_DIR/check_client.py" <<'PY'
import importlib.metadata
import json
import pathlib
import sys

from vyral_client import VyralClient, is_execution_run_terminal
import vyral_client.client as client_module

responses = [
    {"adapter": {"adapterId": "local-sqlite"}, "plugins": [], "handlers": []},
    {"adapterId": "local-sqlite", "runtimeKind": "local.sqlite", "rowCounts": {"runs": 1}},
    {"dryRun": True, "retainTerminalRuns": 10, "runs": 0, "runIds": []},
    {"id": "run-1", "status": "queued"},
    [{"id": "run-1", "status": "queued"}],
    {"id": "run-1", "status": "running"},
    {"id": "run-1", "status": "succeeded", "result": {"ok": True}},
    {"dryRun": False, "dispatched": 1},
    {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a", "run": {"id": "run-1"}},
    {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a", "run": {"id": "run-1"}},
    {"id": "run-1", "status": "running"},
    {},
    {"id": "artifact-1"},
    {"runId": "run-1", "key": "cursor"},
    {"runId": "run-1", "key": "cursor"},
    {"run": {"id": "run-1"}, "suspended": True},
    {"id": "run-1", "status": "succeeded"},
]
seen = []

class FakeResponse:
    def __init__(self, body):
        self._body = body

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return None

    def read(self):
        return self._body

def fake_urlopen(request, timeout):
    del timeout
    body = getattr(request, "data", None)
    seen.append({
        "method": getattr(request, "get_method")(),
        "url": getattr(request, "full_url"),
        "body": json.loads(body.decode("utf-8")) if body else None,
    })
    return FakeResponse(json.dumps(responses.pop(0)).encode("utf-8"))

client_module.urlopen = fake_urlopen
client = VyralClient("http://vyral.local")
runtime = client.get_execution_runtime()
maintenance = client.get_execution_runtime_maintenance()
prune = client.prune_execution_runtime_maintenance(retain_terminal_runs=10)
started = client.start_execution_run({
    "handlerId": "sample.handler",
    "pluginId": "sample.plugin",
    "payload": {"ok": True},
    "idempotencyKey": "sample:1",
    "correlationId": "corr-1",
    "tags": {"projectId": "project-a"},
})
runs = client.list_execution_runs(
    plugin_id="sample.plugin",
    correlation_id="corr-1",
    tags={"projectId": "project-a"},
    limit=5,
)
run = client.wait_execution_run("run-1", timeout_seconds=1, poll_interval_seconds=0)
worker_request = {"leaseKey": "lease-1", "leaseToken": "token-1", "workerId": "worker-a"}
reconcile = client.reconcile_execution_runtime_dispatch(limit=10)
lease = client.lease_external_execution_run({"workerId": "worker-a", "handlerIds": ["sample.handler"]})
client.heartbeat_external_execution_lease({**worker_request, "ttlSeconds": 30})
client.report_external_execution_lease({**worker_request, "update": {"progress": 0.5}})
client.record_external_execution_lease_event({**worker_request, "type": "log", "severity": "info"})
client.put_external_execution_lease_artifact({**worker_request, "artifact": {"name": "summary", "content": {}}})
client.put_external_execution_lease_checkpoint({**worker_request, "checkpoint": {"key": "cursor", "content": {}}})
checkpoint = client.get_external_execution_lease_checkpoint({**worker_request, "key": "cursor"})
wait = client.wait_external_execution_lease({**worker_request, "kind": "external_event", "name": "approval"})
completed = client.complete_external_execution_lease({**worker_request, "result": {"status": "succeeded"}})

assert runtime["adapter"]["adapterId"] == "local-sqlite"
assert maintenance["runtimeKind"] == "local.sqlite"
assert prune["dryRun"] is True
assert started["status"] == "queued"
assert runs[0]["id"] == "run-1"
assert is_execution_run_terminal(run)
assert reconcile["dispatched"] == 1
assert lease["leaseKey"] == "lease-1"
assert checkpoint["key"] == "cursor"
assert wait["suspended"] is True
assert completed["status"] == "succeeded"
assert seen[0]["url"] == "http://vyral.local/execution/runtime"
assert seen[1]["url"] == "http://vyral.local/execution/runtime/maintenance"
assert seen[2]["body"] == {"dryRun": True, "retainTerminalRuns": 10}
assert seen[3]["method"] == "POST"
assert seen[3]["url"] == "http://vyral.local/execution/runs"
assert seen[3]["body"]["handlerId"] == "sample.handler"
assert seen[4]["url"] == "http://vyral.local/execution/runs?pluginId=sample.plugin&correlationId=corr-1&limit=5&tag.projectId=project-a"
assert [item["url"] for item in seen[-10:]] == [
    "http://vyral.local/execution/runtime/maintenance/reconcile",
    "http://vyral.local/execution/workers/leases",
    "http://vyral.local/execution/workers/leases/heartbeat",
    "http://vyral.local/execution/workers/leases/reports",
    "http://vyral.local/execution/workers/leases/events",
    "http://vyral.local/execution/workers/leases/artifacts",
    "http://vyral.local/execution/workers/leases/checkpoints",
    "http://vyral.local/execution/workers/leases/checkpoints/read",
    "http://vyral.local/execution/workers/leases/wait",
    "http://vyral.local/execution/workers/leases/complete",
]

print(f"python-client-version={importlib.metadata.version('vyral-client')}")
print("python-client-execution-helpers=ok")
PY
"$PYTHON_DIR/.venv/bin/python" "$PYTHON_DIR/check_client.py"

pushd "$JAVASCRIPT_DIR" >/dev/null
npm init -y >/dev/null
tarball_name="$(npm pack "$ROOT/clients/javascript" --pack-destination "$JAVASCRIPT_DIR" 2>/dev/null | tail -n 1)"
npm install "$JAVASCRIPT_DIR/$tarball_name" >/dev/null
cat > check-client.mjs <<'JS'
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { VyralClient, isExecutionRunTerminal } from "vyral-client";

const responses = [
  { adapter: { adapterId: "local-sqlite" }, plugins: [], handlers: [] },
  { adapterId: "local-sqlite", runtimeKind: "local.sqlite", rowCounts: { runs: 1 } },
  { dryRun: true, retainTerminalRuns: 10, runs: 0, runIds: [] },
  { id: "run-1", status: "queued" },
  [{ id: "run-1", status: "queued" }],
  { id: "run-1", status: "running" },
  { id: "run-1", status: "succeeded", result: { ok: true } },
  { dryRun: false, dispatched: 1 },
  { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a", run: { id: "run-1" } },
  { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a", run: { id: "run-1" } },
  { id: "run-1", status: "running" },
  {},
  { id: "artifact-1" },
  { runId: "run-1", key: "cursor" },
  { runId: "run-1", key: "cursor" },
  { run: { id: "run-1" }, suspended: true },
  { id: "run-1", status: "succeeded" }
];
const seen = [];
const client = new VyralClient("http://vyral.local", {
  fetch: async (url, init = {}) => {
    seen.push({
      method: init.method,
      url,
      body: init.body ? JSON.parse(init.body) : null
    });
    return new Response(JSON.stringify(responses.shift()), { status: 200 });
  }
});

const runtime = await client.getExecutionRuntime();
const maintenance = await client.getExecutionRuntimeMaintenance();
const prune = await client.pruneExecutionRuntimeMaintenance({ retainTerminalRuns: 10 });
const started = await client.startExecutionRun({
  handlerId: "sample.handler",
  pluginId: "sample.plugin",
  payload: { ok: true },
  idempotencyKey: "sample:1",
  correlationId: "corr-1",
  tags: { projectId: "project-a" }
});
const runs = await client.listExecutionRuns({
  pluginId: "sample.plugin",
  correlationId: "corr-1",
  tags: { projectId: "project-a" },
  limit: 5
});
const run = await client.waitExecutionRun("run-1", { timeoutMs: 1000, pollIntervalMs: 0 });
const workerRequest = { leaseKey: "lease-1", leaseToken: "token-1", workerId: "worker-a" };
const reconcile = await client.reconcileExecutionRuntimeDispatch({ limit: 10 });
const lease = await client.leaseExternalExecutionRun({ workerId: "worker-a", handlerIds: ["sample.handler"] });
await client.heartbeatExternalExecutionLease({ ...workerRequest, ttlSeconds: 30 });
await client.reportExternalExecutionLease({ ...workerRequest, update: { progress: 0.5 } });
await client.recordExternalExecutionLeaseEvent({ ...workerRequest, type: "log", severity: "info" });
await client.putExternalExecutionLeaseArtifact({ ...workerRequest, artifact: { name: "summary", content: {} } });
await client.putExternalExecutionLeaseCheckpoint({ ...workerRequest, checkpoint: { key: "cursor", content: {} } });
const workerCheckpoint = await client.getExternalExecutionLeaseCheckpoint({ ...workerRequest, key: "cursor" });
const workerWait = await client.waitExternalExecutionLease({ ...workerRequest, kind: "external_event", name: "approval" });
const workerCompletion = await client.completeExternalExecutionLease({ ...workerRequest, result: { status: "succeeded" } });

assert.equal(runtime.adapter.adapterId, "local-sqlite");
assert.equal(maintenance.runtimeKind, "local.sqlite");
assert.equal(prune.dryRun, true);
assert.equal(started.status, "queued");
assert.equal(runs[0].id, "run-1");
assert.equal(isExecutionRunTerminal(run), true);
assert.equal(reconcile.dispatched, 1);
assert.equal(lease.leaseKey, "lease-1");
assert.equal(workerCheckpoint.key, "cursor");
assert.equal(workerWait.suspended, true);
assert.equal(workerCompletion.status, "succeeded");
assert.equal(seen[0].url, "http://vyral.local/execution/runtime");
assert.equal(seen[1].url, "http://vyral.local/execution/runtime/maintenance");
assert.deepEqual(seen[2].body, { dryRun: true, retainTerminalRuns: 10 });
assert.equal(seen[3].method, "POST");
assert.equal(seen[3].url, "http://vyral.local/execution/runs");
assert.equal(seen[3].body.handlerId, "sample.handler");
assert.equal(seen[4].url, "http://vyral.local/execution/runs?pluginId=sample.plugin&correlationId=corr-1&tag.projectId=project-a&limit=5");
assert.deepEqual(seen.slice(-10).map((item) => item.url), [
  "http://vyral.local/execution/runtime/maintenance/reconcile",
  "http://vyral.local/execution/workers/leases",
  "http://vyral.local/execution/workers/leases/heartbeat",
  "http://vyral.local/execution/workers/leases/reports",
  "http://vyral.local/execution/workers/leases/events",
  "http://vyral.local/execution/workers/leases/artifacts",
  "http://vyral.local/execution/workers/leases/checkpoints",
  "http://vyral.local/execution/workers/leases/checkpoints/read",
  "http://vyral.local/execution/workers/leases/wait",
  "http://vyral.local/execution/workers/leases/complete"
]);

const packageJson = JSON.parse(readFileSync(new URL("./node_modules/vyral-client/package.json", import.meta.url), "utf-8"));
console.log(`javascript-client-version=${packageJson.version}`);
console.log("javascript-client-execution-helpers=ok");
JS
node check-client.mjs
popd >/dev/null

echo "preview-handoff=ok"
