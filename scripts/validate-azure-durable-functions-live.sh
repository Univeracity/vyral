#!/usr/bin/env bash
set -euo pipefail
umask 077

# Deploys the disposable Functions smoke host, proves ordinary run-owned state plus portable
# external-event and timer waits end to end, and deletes every resource it creates. Connection
# strings remain environment-only and are never printed. Required: the normal
# VYRAL_AZURE_COSMOS_* live-test variables plus the resource group and Cosmos account name below.
# Set VYRAL_AZURE_LIVE_COSMOS_RESOURCE_GROUP when the reusable Cosmos fixture is intentionally
# isolated from the resource group that receives disposable Function and Storage resources.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "$name is required." >&2
    exit 2
  fi
}

require VYRAL_AZURE_LIVE_RESOURCE_GROUP
require VYRAL_AZURE_LIVE_COSMOS_ACCOUNT
require VYRAL_AZURE_COSMOS_CONNECTION_STRING
require VYRAL_AZURE_COSMOS_DATABASE

LOCATION="${VYRAL_AZURE_LIVE_LOCATION:-centralus}"
COSMOS_RESOURCE_GROUP="${VYRAL_AZURE_LIVE_COSMOS_RESOURCE_GROUP:-$VYRAL_AZURE_LIVE_RESOURCE_GROUP}"
STAMP="$(date -u +%Y%m%d%H%M%S)"
FUNCTION="vyral-wait-${STAMP}"
STORAGE="vyralw${STAMP}st"
HUB="VyralWait${STAMP}"
STATUS_CONTAINER="vyral-wait-${STAMP}"
WORK_ROOT="${TMPDIR:-/tmp}/vyral-azure-durable-live-${STAMP}-$$"
FUNCTION_CREATED=false
STORAGE_CREATED=false
STATUS_CONTAINER_CLEANUP="not-created"
FUNCTION_CLEANUP="not-created"
STORAGE_CLEANUP="not-created"
ORDINARY_RUN_PASSED=false
EXTERNAL_EVENT_PASSED=false
TIMER_PASSED=false
LIVE_ASSERTIONS_PASSED=false
GATE_RESULT="failed"

cleanup() {
  local command_status="$?"
  local cleanup_failed=false
  local final_status="$command_status"
  local result="failed"
  trap - EXIT

  if az cosmosdb sql container show \
    --resource-group "$COSMOS_RESOURCE_GROUP" \
    --account-name "$VYRAL_AZURE_LIVE_COSMOS_ACCOUNT" \
    --database-name "$VYRAL_AZURE_COSMOS_DATABASE" \
    --name "$STATUS_CONTAINER" --only-show-errors --output none 2>/dev/null; then
    if az cosmosdb sql container delete \
      --resource-group "$COSMOS_RESOURCE_GROUP" \
      --account-name "$VYRAL_AZURE_LIVE_COSMOS_ACCOUNT" \
      --database-name "$VYRAL_AZURE_COSMOS_DATABASE" \
      --name "$STATUS_CONTAINER" --yes --only-show-errors --output none; then
      STATUS_CONTAINER_CLEANUP="deleted"
    else
      STATUS_CONTAINER_CLEANUP="delete-pending"
      cleanup_failed=true
    fi
  else
    STATUS_CONTAINER_CLEANUP="not-created"
  fi
  if [[ "$FUNCTION_CREATED" == true ]]; then
    if az functionapp delete --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
      --name "$FUNCTION" --only-show-errors --output none; then
      FUNCTION_CLEANUP="deleted"
    else
      FUNCTION_CLEANUP="delete-pending"
      cleanup_failed=true
    fi
  fi
  if [[ "$STORAGE_CREATED" == true ]]; then
    if az storage account delete --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
      --name "$STORAGE" --yes --only-show-errors --output none; then
      STORAGE_CLEANUP="deleted"
    else
      STORAGE_CLEANUP="delete-pending"
      cleanup_failed=true
    fi
  fi
  rm -rf "$WORK_ROOT"

  if [[ "$cleanup_failed" == true && "$final_status" -eq 0 ]]; then
    final_status=1
  fi
  if [[ "$GATE_RESULT" == "passed" && "$final_status" -eq 0 ]]; then
    result="passed"
  fi

  if [[ -n "${VYRAL_AZURE_LIVE_RECEIPT_PATH:-}" ]]; then
    mkdir -p "$(dirname "$VYRAL_AZURE_LIVE_RECEIPT_PATH")"
    jq -n \
      --arg result "$result" \
      --arg tested_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      --arg status_container_cleanup "$STATUS_CONTAINER_CLEANUP" \
      --arg function_cleanup "$FUNCTION_CLEANUP" \
      --arg storage_cleanup "$STORAGE_CLEANUP" \
      --argjson ordinary "$ORDINARY_RUN_PASSED" \
      --argjson event_wait "$EXTERNAL_EVENT_PASSED" \
      --argjson timer "$TIMER_PASSED" \
      --argjson assertions "$LIVE_ASSERTIONS_PASSED" \
      --argjson cleanup_passed "$([[ "$cleanup_failed" == false ]] && echo true || echo false)" \
      '{
        schemaVersion: 1,
        gate: "azure_blob_cosmos_durable_functions_live",
        result: $result,
        environmentClass: "live_managed",
        testedAtUtc: $tested_at,
        authentication: {
          mode: "workload_identity",
          shortLivedSession: true,
          secretsRedacted: true
        },
        checks: {
          ordinaryRun: $ordinary,
          externalEventWait: $event_wait,
          durableTimer: $timer,
          liveAssertions: $assertions
        },
        cleanup: {
          result: (if $cleanup_passed then "passed" else "failed" end),
          statusContainer: $status_container_cleanup,
          functionApp: $function_cleanup,
          storageAccount: $storage_cleanup,
          identifiersRedacted: true
        },
        isolation: {
          runScopedResourceNames: true,
          disposableResourceGroup: true,
          identifiersRedacted: true
        },
        limitations: {
          automaticQualificationPromotion: false,
          consumerEnvironmentCovered: false
        }
      }' > "$VYRAL_AZURE_LIVE_RECEIPT_PATH"
    chmod 0600 "$VYRAL_AZURE_LIVE_RECEIPT_PATH"
  fi

  if [[ "$cleanup_failed" == true ]]; then
    echo "azure-durable-functions-live-cleanup=status-container:${STATUS_CONTAINER_CLEANUP} function:${FUNCTION_CLEANUP} storage:${STORAGE_CLEANUP}" >&2
  else
    echo "azure-durable-functions-live-cleanup=status-container:${STATUS_CONTAINER_CLEANUP} function:${FUNCTION_CLEANUP} storage:${STORAGE_CLEANUP}"
  fi
  exit "$final_status"
}
trap cleanup EXIT

mkdir -p "$WORK_ROOT/publish"
dotnet publish samples/Vyral.Execution.AzureDurableFunctionsSmoke/Vyral.Execution.AzureDurableFunctionsSmoke.csproj \
  --no-restore --configuration Release --output "$WORK_ROOT/publish" --nologo
(cd "$WORK_ROOT/publish" && zip -qr "$WORK_ROOT/app.zip" .)

az storage account create --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$STORAGE" \
  --location "$LOCATION" --sku Standard_LRS --kind StorageV2 --https-only true --min-tls-version TLS1_2 \
  --allow-blob-public-access false --only-show-errors --output none
STORAGE_CREATED=true
STORAGE_CONNECTION="$(az storage account show-connection-string --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
  --name "$STORAGE" --query connectionString --output tsv)"

az functionapp create --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
  --storage-account "$STORAGE" --flexconsumption-location "$LOCATION" --runtime dotnet-isolated \
  --runtime-version 10 --functions-version 4 --disable-app-insights true --https-only true \
  --only-show-errors --output none
FUNCTION_CREATED=true
az functionapp config appsettings set --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
  --settings "AzureWebJobsStorage=$STORAGE_CONNECTION" "VYRAL_AZURE_DURABLE_TASK_HUB=$HUB" \
  "VYRAL_AZURE_COSMOS_CONNECTION_STRING=$VYRAL_AZURE_COSMOS_CONNECTION_STRING" \
  "VYRAL_AZURE_COSMOS_DATABASE=$VYRAL_AZURE_COSMOS_DATABASE" \
  "VYRAL_AZURE_DURABLE_STATUS_CONTAINER=$STATUS_CONTAINER" --only-show-errors --output none

# A newly created Flex app can expose SCM before its worker is ready. Retry the Kudu zip operation
# instead of treating the transient 502 as a product failure.
sleep 90
deployed=false
for _ in $(seq 1 4); do
  if az functionapp deployment source config-zip --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
    --name "$FUNCTION" --src "$WORK_ROOT/app.zip" --timeout 600 --only-show-errors --output none; then
    deployed=true
    break
  fi
  sleep 30
done
[[ "$deployed" == true ]]

function_key="$(az functionapp keys list --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
  --name "$FUNCTION" --query functionKeys.default --output tsv)"
CURL_CONFIG="$WORK_ROOT/function-key.curlrc"
printf 'header = "x-functions-key: %s"\n' "$function_key" > "$CURL_CONFIG"
unset function_key
base_url="https://${FUNCTION}.azurewebsites.net"
http_code=""
for _ in $(seq 1 72); do
  http_code="$(curl --config "$CURL_CONFIG" -sS -o /dev/null -w '%{http_code}' "$base_url/api/vyral-smoke/runs/not-a-run" || true)"
  [[ "$http_code" == 404 ]] && break
  sleep 5
done
[[ "$http_code" == 404 ]]

smoke_body="{\"idempotencyKey\":\"ordinary-smoke-${STAMP}\",\"payload\":{}}"
smoke_started="$(curl --config "$CURL_CONFIG" -sS --fail -X POST \
  "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' \
  --data "$smoke_body")"
smoke_run_id="$(printf '%s' "$smoke_started" | jq -r .id)"
[[ -n "$smoke_run_id" && "$smoke_run_id" != null ]]
smoke_status=""
for _ in $(seq 1 72); do
  smoke_status="$(curl --config "$CURL_CONFIG" -sS --fail \
    "$base_url/api/vyral-smoke/runs/$smoke_run_id" | jq -r .status)"
  [[ "$smoke_status" == succeeded ]] && break
  sleep 3
done
[[ "$smoke_status" == succeeded ]]
ORDINARY_RUN_PASSED=true

timeout_at="$(date -u -d '+10 minutes' +%Y-%m-%dT%H:%M:%SZ)"
body="{\"idempotencyKey\":\"wait-smoke-${STAMP}\",\"payload\":{\"waitForEvent\":\"approval\",\"waitTimeoutAtUtc\":\"${timeout_at}\"}}"
first="$(curl --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$body")"
run_id="$(printf '%s' "$first" | jq -r .id)"
[[ -n "$run_id" && "$run_id" != null ]]
second="$(curl --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$body")"
[[ "$(printf '%s' "$second" | jq -r .id)" == "$run_id" ]]

status=""
for _ in $(seq 1 48); do
  status="$(curl --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$run_id" | jq -r .status)"
  [[ "$status" == waiting ]] && break
  sleep 3
done
[[ "$status" == waiting ]]
curl --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs/$run_id/events/approval" \
  -H 'content-type: application/json' --data '{"approved":true}' >/dev/null
for _ in $(seq 1 72); do
  status="$(curl --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$run_id" | jq -r .status)"
  [[ "$status" == succeeded ]] && break
  sleep 3
done
[[ "$status" == succeeded ]]
EXTERNAL_EVENT_PASSED=true

timer_at="$(date -u -d '+90 seconds' +%Y-%m-%dT%H:%M:%SZ)"
timer_body="{\"idempotencyKey\":\"timer-smoke-${STAMP}\",\"payload\":{\"waitForTimerAtUtc\":\"${timer_at}\"}}"
timer_started="$(curl --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$timer_body")"
timer_run_id="$(printf '%s' "$timer_started" | jq -r .id)"
[[ -n "$timer_run_id" && "$timer_run_id" != null ]]
timer_status=""
for _ in $(seq 1 48); do
  timer_status="$(curl --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$timer_run_id" | jq -r .status)"
  [[ "$timer_status" == waiting ]] && break
  sleep 3
done
[[ "$timer_status" == waiting ]]
for _ in $(seq 1 72); do
  timer_status="$(curl --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$timer_run_id" | jq -r .status)"
  [[ "$timer_status" == succeeded ]] && break
  sleep 3
done
[[ "$timer_status" == succeeded ]]
TIMER_PASSED=true

VYRAL_AZURE_DURABLE_STATUS_CONTAINER="$STATUS_CONTAINER" \
VYRAL_AZURE_DURABLE_SMOKE_RUN_ID="$smoke_run_id" \
VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID="$run_id" \
VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID="$timer_run_id" \
dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj --no-restore \
  --filter FullyQualifiedName~AzureDurableFunctionsSmokeLiveTests --logger 'console;verbosity=minimal'
LIVE_ASSERTIONS_PASSED=true
GATE_RESULT="passed"
echo 'azure-durable-functions-live-gate=ok'
