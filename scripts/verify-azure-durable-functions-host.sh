#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

for command in azurite curl dotnet func jq pgrep setsid; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Azure Functions host startup gate requires: $command" >&2
    exit 2
  fi
done

work="$(mktemp -d "${TMPDIR:-/tmp}/vyral-azure-host.XXXXXX")"
azurite_pid=""
host_pid=""
host_group_pid=""

cleanup() {
  local status="$?"
  trap - EXIT
  set +e
  if [[ -n "$host_group_pid" ]] && kill -0 -- "-$host_group_pid" 2>/dev/null; then
    kill -TERM -- "-$host_group_pid" 2>/dev/null
    for _ in $(seq 1 20); do
      kill -0 -- "-$host_group_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL -- "-$host_group_pid" 2>/dev/null
  fi
  if [[ -n "$host_pid" ]]; then
    wait "$host_pid" 2>/dev/null
  fi
  if [[ -n "$azurite_pid" ]] && kill -0 "$azurite_pid" 2>/dev/null; then
    kill -TERM "$azurite_pid" 2>/dev/null
    wait "$azurite_pid" 2>/dev/null
  fi
  rm -rf -- "$work"
  exit "$status"
}
trap cleanup EXIT

dotnet publish \
  samples/Vyral.Execution.AzureDurableFunctionsSmoke/Vyral.Execution.AzureDurableFunctionsSmoke.csproj \
  --configuration Release \
  --no-restore \
  --output "$work/publish" \
  --nologo >/dev/null

function_count="$(jq 'length' "$work/publish/functions.metadata")"
if [[ "$function_count" != 7 ]]; then
  echo "azure-durable-functions-host=failed class=metadata-count count:$function_count" >&2
  exit 1
fi

azurite \
  --silent \
  --location "$work/azurite" \
  --debug "$work/azurite.log" \
  >"$work/azurite.stdout" 2>&1 &
azurite_pid="$!"

azurite_ready=false
for _ in $(seq 1 30); do
  if curl --silent --output /dev/null \
    'http://127.0.0.1:10000/devstoreaccount1?comp=list'; then
    azurite_ready=true
    break
  fi
  kill -0 "$azurite_pid" 2>/dev/null || break
  sleep 1
done
if [[ "$azurite_ready" != true ]]; then
  echo 'azure-durable-functions-host=failed class=azurite-startup' >&2
  exit 1
fi

port="$(python3 - <<'PY'
import socket

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"

export AzureWebJobsStorage='UseDevelopmentStorage=true'
export VYRAL_AZURE_DURABLE_TASK_HUB='VyralHostStartupGate'
export VYRAL_AZURE_COSMOS_CONNECTION_STRING='AccountEndpoint=https://127.0.0.1:8081/;AccountKey=VmlyYWxIb3N0U3RhcnR1cEdhdGVLZXk=;'
export VYRAL_AZURE_COSMOS_DATABASE='host-startup-gate'
export VYRAL_AZURE_DURABLE_STATUS_CONTAINER='host-startup-gate'

setsid --fork --wait func start \
  --script-root "$work/publish" \
  --port "$port" \
  --dotnet-isolated \
  >"$work/functions.log" 2>&1 &
host_pid="$!"
for _ in $(seq 1 20); do
  host_group_pid="$(pgrep -P "$host_pid" 2>/dev/null | head -n 1 || true)"
  [[ -n "$host_group_pid" ]] && break
  kill -0 "$host_pid" 2>/dev/null || break
  sleep 0.1
done
if [[ -z "$host_group_pid" ]]; then
  echo 'azure-durable-functions-host=failed class=process-startup' >&2
  exit 1
fi

host_ready=false
for _ in $(seq 1 45); do
  if curl --silent --output /dev/null --max-time 2 \
    "http://127.0.0.1:${port}/"; then
    host_ready=true
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || break
  sleep 1
done

if [[ "$host_ready" != true ]]; then
  failure_class="host-startup"
  if grep -Fq 'System.MissingMethodException' "$work/functions.log"; then
    failure_class="incompatible-package-api"
  elif grep -Fq 'Language Worker Process exited' "$work/functions.log"; then
    failure_class="language-worker-exited"
  elif grep -Fq 'A host error has occurred during startup' "$work/functions.log"; then
    failure_class="host-initialization"
  fi
  echo "azure-durable-functions-host=failed class:$failure_class" >&2
  exit 1
fi

printf 'azure-durable-functions-host=ok functions=%s\n' "$function_count"
