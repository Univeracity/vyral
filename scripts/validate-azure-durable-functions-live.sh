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
FUNCTION_HOSTING_PLAN="${VYRAL_AZURE_FUNCTIONS_HOSTING_PLAN:-flex_consumption}"
EXPECTED_FUNCTION_NAMES_JSON='[]'
EXPECTED_FUNCTION_COUNT=0
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
TIMER_TERMINAL_STATUS="not-observed"
TIMER_FAILURE_CLASS="not-observed"
HTTP_TRANSPORT_RECOVERY_COUNT=0
HTTP_TRANSPORT_LAST_FAILED_OPERATION="not-observed"
HTTP_TRANSPORT_LAST_FAILED_EXIT_CODE="not-observed"
FUNCTIONS_HTTP_RESPONSE=""
LIVE_ASSERTIONS_PASSED=false
GATE_RESULT="failed"
DEPLOYMENT_PASSED=false
FUNCTIONS_DISCOVERED=false
FUNCTION_KEY_AVAILABLE=false
ENDPOINT_READY=false
DISCOVERED_FUNCTION_COUNT=0
READINESS_HTTP_CODE="not-attempted"
# Preserve the legacy receipt field so evidence from the earlier restart recovery remains comparable.
HOST_RESTART_ATTEMPTED=false
TRIGGER_SYNC_ATTEMPTED=false
TRIGGER_SYNC_API_VERSION="2024-11-01"
TRIGGER_SYNC_ROUTE="host-default-sync"
TRIGGER_SYNC_ERROR_CODE=""
DISCOVERED_FUNCTION_NAMES_JSON='[]'
DEPLOYMENT_ATTEMPTS=0
DEPLOYMENT_PROVIDER_STATUS="not-observed"
DEPLOYMENT_PROVIDER_FAILURE_CLASS="not-observed"
DEPLOYMENT_MAX_ATTEMPTS=3
DEPLOYMENT_COMMAND_TIMEOUT_SECONDS=330
FUNCTION_RUNTIME_NAME="not-observed"
FUNCTION_RUNTIME_VERSION="not-observed"
FUNCTION_RUNTIME_MATCHED=false
POST_DEPLOYMENT_RECOVERY_ATTEMPTED=false
POST_DEPLOYMENT_RESTART_ISSUED=false
POST_DEPLOYMENT_MASTER_KEY_AVAILABLE=false
POST_DEPLOYMENT_RUNTIME_FUNCTION_NAMES_JSON='[]'
POST_DEPLOYMENT_RUNTIME_FUNCTION_COUNT=0
POST_DEPLOYMENT_RUNTIME_FUNCTIONS_MATCHED=false
FAILURE_STAGE="publish"

case "$FUNCTION_HOSTING_PLAN" in
  flex_consumption | windows_consumption)
    ;;
  *)
    echo "VYRAL_AZURE_FUNCTIONS_HOSTING_PLAN must be flex_consumption or windows_consumption." >&2
    exit 2
    ;;
esac

classify_deployment_provider_failure() {
  local diagnostic="$1"
  local normalized
  normalized="$(tr '[:upper:]' '[:lower:]' <<<"$diagnostic")"
  case "$normalized" in
    *reset*worker*503* | *reset*worker*site*unavailable*)
      printf '%s\n' worker_reset_503
      ;;
    *site*unavailable* | *\ 503\ * | *status\ code\ 503*)
      printf '%s\n' site_unavailable
      ;;
    *authorization* | *forbidden*)
      printf '%s\n' authorization
      ;;
    *timeout*)
      printf '%s\n' timeout
      ;;
    *)
      printf 'provider_status_%s\n' "${DEPLOYMENT_PROVIDER_STATUS//[^A-Za-z0-9]/_}"
      ;;
  esac
}

# Retries only failures which prove that curl never established a connection to the Functions
# host. That makes a retry safe even for an external-event POST: Azure never accepted the first
# request. HTTP failures and response timeouts are deliberately not retried here because their
# request-delivery outcome is ambiguous. The receipt records every recovered transport failure.
functions_http_request() {
  local operation="$1"
  shift
  local attempt=1
  local exit_code=0
  local diagnostic="$WORK_ROOT/http-${operation}.stderr"
  while true; do
    FUNCTIONS_HTTP_RESPONSE=""
    if FUNCTIONS_HTTP_RESPONSE="$(curl "$@" 2>"$diagnostic")"; then
      return 0
    fi
    exit_code=$?
    if (( attempt < 4 )) && grep -Eqi 'failed to connect|connection refused' "$diagnostic"; then
      HTTP_TRANSPORT_RECOVERY_COUNT=$((HTTP_TRANSPORT_RECOVERY_COUNT + 1))
      echo "azure-durable-functions-live-transport=retry operation:${operation} attempt:${attempt}" >&2
      attempt=$((attempt + 1))
      sleep 5
      continue
    fi

    HTTP_TRANSPORT_LAST_FAILED_OPERATION="$operation"
    HTTP_TRANSPORT_LAST_FAILED_EXIT_CODE="$exit_code"
    echo "azure-durable-functions-live-transport=failed operation:${operation} exit:${exit_code}" >&2
    return "$exit_code"
  done
}

probe_host_after_failed_deployment() {
  POST_DEPLOYMENT_RECOVERY_ATTEMPTED=true
  if ! az functionapp restart --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
    --name "$FUNCTION" --only-show-errors --output none; then
    return
  fi
  POST_DEPLOYMENT_RESTART_ISSUED=true

  local master_key=""
  local key_payload=""
  local admin_config="$WORK_ROOT/post-deployment-master-key.curlrc"
  local admin_payload=""
  local inventory_matches=false
  for _ in $(seq 1 24); do
    key_payload="$(az functionapp keys list --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
      --name "$FUNCTION" --output json --only-show-errors 2>/dev/null || true)"
    master_key="$(jq -r '.masterKey // empty' <<<"$key_payload" 2>/dev/null || true)"
    unset key_payload
    if [[ -z "$master_key" ]]; then
      sleep 5
      continue
    fi
    POST_DEPLOYMENT_MASTER_KEY_AVAILABLE=true
    printf 'header = "x-functions-key: %s"\nconnect-timeout = 10\nmax-time = 30\n' \
      "$master_key" > "$admin_config"
    chmod 0600 "$admin_config"
    unset master_key
    if functions_http_request "post-deployment-runtime-inventory" --config "$admin_config" -sS --fail --max-time 10 \
      "https://${FUNCTION}.azurewebsites.net/admin/functions/" 2>/dev/null; then
      admin_payload="$FUNCTIONS_HTTP_RESPONSE"
    else
      admin_payload=""
    fi
    POST_DEPLOYMENT_RUNTIME_FUNCTION_NAMES_JSON="$(jq -cer '
      if type == "array" and all(.[]; (.name? | type) == "string")
      then [.[].name] | sort
      else []
      end
    ' <<<"$admin_payload" 2>/dev/null || echo '[]')"
    POST_DEPLOYMENT_RUNTIME_FUNCTION_COUNT="$(jq -r 'length' <<<"$POST_DEPLOYMENT_RUNTIME_FUNCTION_NAMES_JSON")"
    inventory_matches="$(jq -r --argjson expected "$EXPECTED_FUNCTION_NAMES_JSON" '
      . == ($expected | sort)
    ' <<<"$POST_DEPLOYMENT_RUNTIME_FUNCTION_NAMES_JSON" 2>/dev/null || echo false)"
    unset admin_payload
    if [[ "$inventory_matches" == true ]]; then
      POST_DEPLOYMENT_RUNTIME_FUNCTIONS_MATCHED=true
      break
    fi
    sleep 5
  done
}

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
      --arg failure_stage "$FAILURE_STAGE" \
      --arg readiness_http_code "$READINESS_HTTP_CODE" \
      --argjson ordinary "$ORDINARY_RUN_PASSED" \
      --argjson event_wait "$EXTERNAL_EVENT_PASSED" \
      --argjson timer "$TIMER_PASSED" \
      --argjson assertions "$LIVE_ASSERTIONS_PASSED" \
      --argjson deployment "$DEPLOYMENT_PASSED" \
      --argjson functions_discovered "$FUNCTIONS_DISCOVERED" \
      --argjson function_key_available "$FUNCTION_KEY_AVAILABLE" \
      --argjson endpoint_ready "$ENDPOINT_READY" \
      --argjson host_restart_attempted "$HOST_RESTART_ATTEMPTED" \
      --argjson trigger_sync_attempted "$TRIGGER_SYNC_ATTEMPTED" \
      --arg trigger_sync_api_version "$TRIGGER_SYNC_API_VERSION" \
      --arg trigger_sync_route "$TRIGGER_SYNC_ROUTE" \
      --arg trigger_sync_error_code "$TRIGGER_SYNC_ERROR_CODE" \
      --argjson discovered_function_count "$DISCOVERED_FUNCTION_COUNT" \
      --argjson discovered_function_names "$DISCOVERED_FUNCTION_NAMES_JSON" \
      --argjson expected_function_count "$EXPECTED_FUNCTION_COUNT" \
      --argjson expected_function_names "$EXPECTED_FUNCTION_NAMES_JSON" \
      --argjson deployment_attempts "$DEPLOYMENT_ATTEMPTS" \
      --arg deployment_provider_status "$DEPLOYMENT_PROVIDER_STATUS" \
      --arg deployment_provider_failure_class "$DEPLOYMENT_PROVIDER_FAILURE_CLASS" \
      --arg function_hosting_plan "$FUNCTION_HOSTING_PLAN" \
      --arg function_runtime_name "$FUNCTION_RUNTIME_NAME" \
      --arg function_runtime_version "$FUNCTION_RUNTIME_VERSION" \
      --argjson function_runtime_matched "$FUNCTION_RUNTIME_MATCHED" \
      --arg timer_terminal_status "$TIMER_TERMINAL_STATUS" \
      --arg timer_failure_class "$TIMER_FAILURE_CLASS" \
      --argjson http_transport_recovery_count "$HTTP_TRANSPORT_RECOVERY_COUNT" \
      --arg http_transport_last_failed_operation "$HTTP_TRANSPORT_LAST_FAILED_OPERATION" \
      --arg http_transport_last_failed_exit_code "$HTTP_TRANSPORT_LAST_FAILED_EXIT_CODE" \
      --argjson post_deployment_recovery_attempted "$POST_DEPLOYMENT_RECOVERY_ATTEMPTED" \
      --argjson post_deployment_restart_issued "$POST_DEPLOYMENT_RESTART_ISSUED" \
      --argjson post_deployment_master_key_available "$POST_DEPLOYMENT_MASTER_KEY_AVAILABLE" \
      --argjson post_deployment_runtime_function_names "$POST_DEPLOYMENT_RUNTIME_FUNCTION_NAMES_JSON" \
      --argjson post_deployment_runtime_function_count "$POST_DEPLOYMENT_RUNTIME_FUNCTION_COUNT" \
      --argjson post_deployment_runtime_functions_matched "$POST_DEPLOYMENT_RUNTIME_FUNCTIONS_MATCHED" \
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
          deployment: $deployment,
          functionsDiscovered: $functions_discovered,
          functionKeyAvailable: $function_key_available,
          endpointReady: $endpoint_ready,
          ordinaryRun: $ordinary,
          externalEventWait: $event_wait,
          durableTimer: $timer,
          liveAssertions: $assertions
        },
        recovery: {
          partialInventoryTriggerSyncAttempted: $trigger_sync_attempted,
          partialInventoryTriggerSyncApiVersion: (
            if $trigger_sync_attempted then $trigger_sync_api_version else null end
          ),
          partialInventoryTriggerSyncRoute: (
            if $trigger_sync_attempted then $trigger_sync_route else null end
          ),
          partialInventoryTriggerSyncErrorCode: (
            if $trigger_sync_error_code == "" then null else $trigger_sync_error_code end
          ),
          partialInventoryRestartAttempted: $host_restart_attempted
        },
        diagnostics: {
          deploymentAttempts: $deployment_attempts,
          deploymentProviderStatus: $deployment_provider_status,
          deploymentProviderFailureClass: $deployment_provider_failure_class,
          hostingPlan: $function_hosting_plan,
          configuredRuntime: {
            name: $function_runtime_name,
            version: $function_runtime_version,
            matchedExpectedDotnetIsolated10: $function_runtime_matched
          },
          durableTimer: {
            terminalStatus: $timer_terminal_status,
            failureClass: (
              if $timer_failure_class == "not-observed" then null else $timer_failure_class end
            )
          },
          functionsHostTransport: {
            recoveredConnectionFailures: $http_transport_recovery_count,
            unrecoveredOperation: (
              if $http_transport_last_failed_operation == "not-observed"
              then null else $http_transport_last_failed_operation end
            ),
            unrecoveredExitCode: (
              if $http_transport_last_failed_exit_code == "not-observed"
              then null else ($http_transport_last_failed_exit_code | tonumber) end
            )
          },
          packagedFunctionNames: $expected_function_names,
          runtimeFunctionNames: $discovered_function_names,
          postDeploymentFailureRecovery: {
            attempted: $post_deployment_recovery_attempted,
            restartIssued: $post_deployment_restart_issued,
            masterKeyAvailable: $post_deployment_master_key_available,
            runtimeFunctionNames: $post_deployment_runtime_function_names,
            runtimeFunctionCount: $post_deployment_runtime_function_count,
            runtimeFunctionInventoryMatched: $post_deployment_runtime_functions_matched
          }
        },
        failure: (
          if $result == "passed" then null
          else {
            stage: $failure_stage,
            readinessHttpCode: $readiness_http_code,
            discoveredFunctionCount: $discovered_function_count,
            expectedFunctionCount: $expected_function_count
          }
          end
        ),
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
EXPECTED_FUNCTION_NAMES_JSON="$(jq -cer '
  if type == "array" and length > 0 and all(.[]; (.name? | type) == "string")
  then [.[].name] | sort
  else error("invalid packaged function metadata")
  end
' "$WORK_ROOT/publish/functions.metadata")"
EXPECTED_FUNCTION_COUNT="$(jq -r 'length' <<<"$EXPECTED_FUNCTION_NAMES_JSON")"
(cd "$WORK_ROOT/publish" && zip -qr "$WORK_ROOT/app.zip" .)

FAILURE_STAGE="storage-provisioning"
az storage account create --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$STORAGE" \
  --location "$LOCATION" --sku Standard_LRS --kind StorageV2 --https-only true --min-tls-version TLS1_2 \
  --allow-blob-public-access false --only-show-errors --output none
STORAGE_CREATED=true
STORAGE_CONNECTION="$(az storage account show-connection-string --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
  --name "$STORAGE" --query connectionString --output tsv)"

FAILURE_STAGE="function-provisioning"
case "$FUNCTION_HOSTING_PLAN" in
  flex_consumption)
    az functionapp create --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
      --storage-account "$STORAGE" --flexconsumption-location "$LOCATION" --runtime dotnet-isolated \
      --runtime-version 10 --functions-version 4 --disable-app-insights true --https-only true \
      --only-show-errors --output none
    ;;
  windows_consumption)
    az functionapp create --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
      --storage-account "$STORAGE" --consumption-plan-location "$LOCATION" --os-type Windows \
      --runtime dotnet-isolated --runtime-version 10 --functions-version 4 \
      --disable-app-insights true --https-only true --only-show-errors --output none
    ;;
esac
FUNCTION_CREATED=true
FAILURE_STAGE="function-configuration"
function_settings=(
  "AzureWebJobsStorage=$STORAGE_CONNECTION"
  "VYRAL_AZURE_DURABLE_TASK_HUB=$HUB"
  "VYRAL_AZURE_COSMOS_CONNECTION_STRING=$VYRAL_AZURE_COSMOS_CONNECTION_STRING"
  "VYRAL_AZURE_COSMOS_DATABASE=$VYRAL_AZURE_COSMOS_DATABASE"
  "VYRAL_AZURE_DURABLE_STATUS_CONTAINER=$STATUS_CONTAINER"
)
if [[ "$FUNCTION_HOSTING_PLAN" == windows_consumption ]]; then
  # Windows Consumption reads the worker-model selection from the app setting; Flex stores
  # equivalent runtime metadata in functionAppConfig instead.
  function_settings+=("FUNCTIONS_WORKER_RUNTIME=dotnet-isolated")
fi
az functionapp config appsettings set --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
  --settings "${function_settings[@]}" --only-show-errors --output none
unset function_settings

FAILURE_STAGE="function-runtime-configuration"
runtime_configuration="$(az functionapp show --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
  --name "$FUNCTION" --only-show-errors --output json)"
case "$FUNCTION_HOSTING_PLAN" in
  flex_consumption)
    FUNCTION_RUNTIME_NAME="$(jq -r '
      .functionAppConfig.runtime.name? // .properties.functionAppConfig.runtime.name? // empty
    ' <<<"$runtime_configuration")"
    FUNCTION_RUNTIME_VERSION="$(jq -r '
      .functionAppConfig.runtime.version? // .properties.functionAppConfig.runtime.version? // empty
    ' <<<"$runtime_configuration")"
    ;;
  windows_consumption)
    runtime_settings="$(az functionapp config appsettings list \
      --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
      --only-show-errors --output json)"
    FUNCTION_RUNTIME_NAME="$(jq -r '
      map(select(.name == "FUNCTIONS_WORKER_RUNTIME")) | .[0].value? // empty
    ' <<<"$runtime_settings")"
    FUNCTION_RUNTIME_VERSION="$(jq -r '
      .siteConfig.netFrameworkVersion? // .properties.siteConfig.netFrameworkVersion? // empty
    ' <<<"$runtime_configuration")"
    unset runtime_settings
    ;;
esac
unset runtime_configuration
FUNCTION_RUNTIME_NAME="${FUNCTION_RUNTIME_NAME:-not-observed}"
FUNCTION_RUNTIME_VERSION="${FUNCTION_RUNTIME_VERSION:-not-observed}"
# App Service prefixes its Windows .NET framework version with `v`; normalize that provider
# representation so receipts describe the same runtime version across hosting plans.
FUNCTION_RUNTIME_VERSION="${FUNCTION_RUNTIME_VERSION#v}"
# Flex reports platform runtime metadata, while Windows Consumption pairs the worker-model app
# setting with its App Service .NET version. Both must resolve to the requested isolated .NET 10 host.
if [[ "$FUNCTION_RUNTIME_NAME" != dotnet-isolated || ! "$FUNCTION_RUNTIME_VERSION" =~ ^10(\.0+)?$ ]]; then
  echo "azure-durable-functions-live-runtime=invalid plan:${FUNCTION_HOSTING_PLAN} name:${FUNCTION_RUNTIME_NAME} version:${FUNCTION_RUNTIME_VERSION}" >&2
  false
fi
FUNCTION_RUNTIME_MATCHED=true

# Azure CLI may return before provider-side trigger synchronization completes. Require the
# deployment record itself to report success, regardless of the selected hosting plan. Bound each
# attempt and the retry count so a provider outage still reaches the recovery probe and receipt.
FAILURE_STAGE="deployment"
deployed=false
deployment_diagnostic="$WORK_ROOT/deployment-attempt.log"
for _ in $(seq 1 "$DEPLOYMENT_MAX_ATTEMPTS"); do
  DEPLOYMENT_ATTEMPTS=$((DEPLOYMENT_ATTEMPTS + 1))
  deployment_exit_code=0
  timeout --signal=TERM --kill-after=30s "${DEPLOYMENT_COMMAND_TIMEOUT_SECONDS}s" \
    az functionapp deployment source config-zip --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
    --name "$FUNCTION" --src "$WORK_ROOT/app.zip" --timeout 300 --only-show-errors --output none \
    2>"$deployment_diagnostic" || deployment_exit_code=$?
  deployment_records="$(az functionapp log deployment list \
    --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" --name "$FUNCTION" \
    --only-show-errors --output json 2>/dev/null || true)"
  deployment_status="$(jq -r '
      if type == "array" and length > 0 and (.[0].status? | type) == "number"
      then .[0].status | tostring
      else empty
      end
    ' <<<"$deployment_records" 2>/dev/null || true)"
  DEPLOYMENT_PROVIDER_STATUS="${deployment_status:-not-observed}"
  if [[ "$deployment_status" == "4" ]]; then
    DEPLOYMENT_PROVIDER_FAILURE_CLASS="none"
    deployed=true
    unset deployment_records deployment_exit_code
    break
  fi
  provider_diagnostic="$(jq -r '[.. | strings] | join(" ")' <<<"$deployment_records" 2>/dev/null || true)"
  if [[ -f "$deployment_diagnostic" ]]; then
    provider_diagnostic+=" $(<"$deployment_diagnostic")"
  fi
  if [[ "$deployment_exit_code" -eq 124 && "$DEPLOYMENT_PROVIDER_STATUS" == not-observed ]]; then
    DEPLOYMENT_PROVIDER_FAILURE_CLASS="timeout"
  else
    DEPLOYMENT_PROVIDER_FAILURE_CLASS="$(classify_deployment_provider_failure "$provider_diagnostic")"
  fi
  unset deployment_records deployment_exit_code provider_diagnostic
  sleep 20
done
if [[ "$deployed" != true ]]; then
  probe_host_after_failed_deployment
  echo "azure-durable-functions-live-deployment=failed attempts:${DEPLOYMENT_ATTEMPTS} provider-status:${DEPLOYMENT_PROVIDER_STATUS}" >&2
  false
fi
DEPLOYMENT_PASSED=true
echo "azure-durable-functions-live-deployment=passed attempts:${DEPLOYMENT_ATTEMPTS} provider-status:${DEPLOYMENT_PROVIDER_STATUS}"

FAILURE_STAGE="function-key"
function_key=""
master_key=""
for _ in $(seq 1 24); do
  key_payload="$(az functionapp keys list --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
    --name "$FUNCTION" --output json --only-show-errors 2>/dev/null || true)"
  master_key="$(jq -r '.masterKey // empty' <<<"$key_payload" 2>/dev/null || true)"
  function_key="$(jq -r '.functionKeys.default // .masterKey // empty' <<<"$key_payload" 2>/dev/null || true)"
  unset key_payload
  if [[ -n "$function_key" && -n "$master_key" ]]; then
    break
  fi
  sleep 5
done
[[ -n "$function_key" && -n "$master_key" ]]
FUNCTION_KEY_AVAILABLE=true
CURL_CONFIG="$WORK_ROOT/function-key.curlrc"
ADMIN_CURL_CONFIG="$WORK_ROOT/master-key.curlrc"
printf 'header = "x-functions-key: %s"\nconnect-timeout = 10\nmax-time = 30\n' \
  "$function_key" > "$CURL_CONFIG"
printf 'header = "x-functions-key: %s"\nconnect-timeout = 10\nmax-time = 30\n' \
  "$master_key" > "$ADMIN_CURL_CONFIG"
unset function_key master_key
base_url="https://${FUNCTION}.azurewebsites.net"

# The ARM function inventory is eventually consistent and can expose a transient one-function
# snapshot while a newly deployed host is still indexing. Query the authenticated runtime inventory
# instead; it both wakes the deployed host and proves the exact functions that host can execute.
FAILURE_STAGE="function-discovery"
partial_inventory_observations=0
for _ in $(seq 1 48); do
  if functions_http_request "function-discovery" --config "$ADMIN_CURL_CONFIG" -sS --fail \
    --max-time 10 "$base_url/admin/functions/" 2>/dev/null; then
    admin_payload="$FUNCTIONS_HTTP_RESPONSE"
  else
    admin_payload=""
  fi
  candidate_count="$(jq -r 'if type == "array" then length else 0 end' \
    <<<"$admin_payload" 2>/dev/null || echo 0)"
  if [[ "$candidate_count" =~ ^[0-9]+$ ]]; then
    DISCOVERED_FUNCTION_COUNT="$candidate_count"
    if (( candidate_count > 0 && candidate_count < EXPECTED_FUNCTION_COUNT )); then
      partial_inventory_observations=$((partial_inventory_observations + 1))
    fi
  fi
  DISCOVERED_FUNCTION_NAMES_JSON="$(jq -cer '
    if type == "array" and all(.[]; (.name? | type) == "string")
    then [.[].name] | sort
    else []
    end
  ' <<<"$admin_payload" 2>/dev/null || echo '[]')"
  inventory_matches="$(jq -r --argjson expected "$EXPECTED_FUNCTION_NAMES_JSON" '
    if type == "array" and all(.[]; (.name? | type) == "string")
    then ([.[].name] | sort) == ($expected | sort)
    else false
    end
  ' <<<"$admin_payload" 2>/dev/null || echo false)"
  unset admin_payload
  if [[ "$inventory_matches" == true ]]; then
    FUNCTIONS_DISCOVERED=true
    break
  fi
  if [[ "$TRIGGER_SYNC_ATTEMPTED" == false && "$partial_inventory_observations" -ge 6 ]]; then
    FAILURE_STAGE="function-discovery-trigger-sync"
    TRIGGER_SYNC_ATTEMPTED=true
    subscription_id="$(az account show --query id --output tsv --only-show-errors)"
    [[ -n "$subscription_id" ]]
    trigger_sync_response="$WORK_ROOT/function-trigger-sync.response"
    if ! az rest --method post \
      --url "https://management.azure.com/subscriptions/${subscription_id}/resourceGroups/${VYRAL_AZURE_LIVE_RESOURCE_GROUP}/providers/Microsoft.Web/sites/${FUNCTION}/host/default/sync?api-version=${TRIGGER_SYNC_API_VERSION}" \
      --only-show-errors --output json >"$trigger_sync_response" \
      2>"$WORK_ROOT/function-trigger-sync.log"; then
      TRIGGER_SYNC_ERROR_CODE="$(
        jq -r '.error.code? // .code? // empty' "$trigger_sync_response" \
          2>/dev/null || true
      )"
      if [[ -z "$TRIGGER_SYNC_ERROR_CODE" ]]; then
        trigger_sync_diagnostic="$(tr '[:upper:]' '[:lower:]' < "$WORK_ROOT/function-trigger-sync.log")"
        case "$trigger_sync_diagnostic" in
          *invalidapiversion*) TRIGGER_SYNC_ERROR_CODE="InvalidApiVersion" ;;
          *authorization* | *forbidden*) TRIGGER_SYNC_ERROR_CODE="AuthorizationFailed" ;;
          *notfound*) TRIGGER_SYNC_ERROR_CODE="ResourceNotFound" ;;
          *conflict*) TRIGGER_SYNC_ERROR_CODE="Conflict" ;;
          *badrequest* | *bad\ request*) TRIGGER_SYNC_ERROR_CODE="BadRequest" ;;
          *timeout*) TRIGGER_SYNC_ERROR_CODE="Timeout" ;;
          *) TRIGGER_SYNC_ERROR_CODE="unknown" ;;
        esac
        unset trigger_sync_diagnostic
      fi
      unset trigger_sync_response
      unset subscription_id
      echo "azure-durable-functions-live-trigger-sync=failed code:${TRIGGER_SYNC_ERROR_CODE}" >&2
      case "$TRIGGER_SYNC_ERROR_CODE" in
        InvalidApiVersion | AuthorizationFailed | ResourceNotFound)
          false
          ;;
        *)
          # A newly deployed host can reject an ARM sync request before worker indexing has
          # settled. Restart only the disposable app, then preserve the full discovery window.
          FAILURE_STAGE="function-discovery-host-restart"
          HOST_RESTART_ATTEMPTED=true
          az functionapp restart --resource-group "$VYRAL_AZURE_LIVE_RESOURCE_GROUP" \
            --name "$FUNCTION" --only-show-errors --output none
          echo 'azure-durable-functions-live-host-restart=issued'
          FAILURE_STAGE="function-discovery"
          DISCOVERED_FUNCTION_COUNT=0
          DISCOVERED_FUNCTION_NAMES_JSON='[]'
          partial_inventory_observations=0
          sleep 10
          ;;
      esac
    fi
    unset trigger_sync_response
    unset subscription_id
    echo 'azure-durable-functions-live-trigger-sync=issued'
    DISCOVERED_FUNCTION_COUNT=0
    DISCOVERED_FUNCTION_NAMES_JSON='[]'
    partial_inventory_observations=0
    FAILURE_STAGE="function-discovery"
    sleep 10
  fi
  sleep 5
done
echo "azure-durable-functions-live-discovery=count:${DISCOVERED_FUNCTION_COUNT}"
[[ "$FUNCTIONS_DISCOVERED" == true ]]

http_code=""
FAILURE_STAGE="endpoint-readiness"
for _ in $(seq 1 72); do
  if functions_http_request "endpoint-readiness" --config "$CURL_CONFIG" -sS -o /dev/null -w '%{http_code}' "$base_url/api/vyral-smoke/runs/not-a-run"; then
    http_code="$FUNCTIONS_HTTP_RESPONSE"
  else
    http_code=""
  fi
  [[ "$http_code" == 404 ]] && break
  sleep 5
done
READINESS_HTTP_CODE="${http_code:-unavailable}"
echo "azure-durable-functions-live-readiness=http-${READINESS_HTTP_CODE}"
[[ "$http_code" == 404 ]]
ENDPOINT_READY=true

FAILURE_STAGE="ordinary-run"
smoke_body="{\"idempotencyKey\":\"ordinary-smoke-${STAMP}\",\"payload\":{}}"
functions_http_request "ordinary-start" --config "$CURL_CONFIG" -sS --fail -X POST \
  "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' \
  --data "$smoke_body"
smoke_started="$FUNCTIONS_HTTP_RESPONSE"
smoke_run_id="$(printf '%s' "$smoke_started" | jq -r .id)"
[[ -n "$smoke_run_id" && "$smoke_run_id" != null ]]
smoke_status=""
for _ in $(seq 1 72); do
  functions_http_request "ordinary-status" --config "$CURL_CONFIG" -sS --fail \
    "$base_url/api/vyral-smoke/runs/$smoke_run_id"
  smoke_status="$(jq -r .status <<<"$FUNCTIONS_HTTP_RESPONSE")"
  case "$smoke_status" in
    succeeded | failed | rejected | cancelled | timed_out) break ;;
  esac
  sleep 3
done
[[ "$smoke_status" == succeeded ]]
ORDINARY_RUN_PASSED=true

FAILURE_STAGE="external-event-wait"
timeout_at="$(date -u -d '+10 minutes' +%Y-%m-%dT%H:%M:%SZ)"
body="{\"idempotencyKey\":\"wait-smoke-${STAMP}\",\"payload\":{\"waitForEvent\":\"approval\",\"waitTimeoutAtUtc\":\"${timeout_at}\"}}"
functions_http_request "external-event-start" --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$body"
first="$FUNCTIONS_HTTP_RESPONSE"
run_id="$(printf '%s' "$first" | jq -r .id)"
[[ -n "$run_id" && "$run_id" != null ]]
functions_http_request "external-event-idempotency" --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$body"
second="$FUNCTIONS_HTTP_RESPONSE"
[[ "$(printf '%s' "$second" | jq -r .id)" == "$run_id" ]]

status=""
for _ in $(seq 1 48); do
  functions_http_request "external-event-wait-status" --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$run_id"
  status="$(jq -r .status <<<"$FUNCTIONS_HTTP_RESPONSE")"
  case "$status" in
    waiting | failed | rejected | cancelled | timed_out) break ;;
  esac
  sleep 3
done
[[ "$status" == waiting ]]
functions_http_request "external-event-raise" --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs/$run_id/events/approval" \
  -H 'content-type: application/json' --data '{"approved":true}'
for _ in $(seq 1 72); do
  functions_http_request "external-event-resume-status" --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$run_id"
  status="$(jq -r .status <<<"$FUNCTIONS_HTTP_RESPONSE")"
  case "$status" in
    succeeded | failed | rejected | cancelled | timed_out) break ;;
  esac
  sleep 3
done
[[ "$status" == succeeded ]]
EXTERNAL_EVENT_PASSED=true

FAILURE_STAGE="durable-timer"
timer_at="$(date -u -d '+90 seconds' +%Y-%m-%dT%H:%M:%SZ)"
timer_body="{\"idempotencyKey\":\"timer-smoke-${STAMP}\",\"payload\":{\"waitForTimerAtUtc\":\"${timer_at}\"}}"
functions_http_request "durable-timer-start" --config "$CURL_CONFIG" -sS --fail -X POST "$base_url/api/vyral-smoke/runs" -H 'content-type: application/json' --data "$timer_body"
timer_started="$FUNCTIONS_HTTP_RESPONSE"
timer_run_id="$(printf '%s' "$timer_started" | jq -r .id)"
[[ -n "$timer_run_id" && "$timer_run_id" != null ]]
timer_status=""
timer_payload=""
for _ in $(seq 1 48); do
  functions_http_request "durable-timer-wait-status" --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$timer_run_id"
  timer_payload="$FUNCTIONS_HTTP_RESPONSE"
  timer_status="$(jq -r .status <<<"$timer_payload")"
  case "$timer_status" in
    waiting) break ;;
    failed | rejected | cancelled | timed_out)
      TIMER_TERMINAL_STATUS="$timer_status"
      TIMER_FAILURE_CLASS="$(jq -r '.failureClass // "not-observed"' <<<"$timer_payload")"
      break
      ;;
  esac
  sleep 3
done
[[ "$timer_status" == waiting ]]
for _ in $(seq 1 72); do
  functions_http_request "durable-timer-resume-status" --config "$CURL_CONFIG" -sS --fail "$base_url/api/vyral-smoke/runs/$timer_run_id"
  timer_payload="$FUNCTIONS_HTTP_RESPONSE"
  timer_status="$(jq -r .status <<<"$timer_payload")"
  case "$timer_status" in
    succeeded | failed | rejected | cancelled | timed_out) break ;;
  esac
  sleep 3
done
TIMER_TERMINAL_STATUS="${timer_status:-not-observed}"
TIMER_FAILURE_CLASS="$(jq -r '.failureClass // "not-observed"' <<<"$timer_payload")"
[[ "$timer_status" == succeeded ]]
TIMER_PASSED=true

FAILURE_STAGE="live-assertions"
VYRAL_AZURE_DURABLE_STATUS_CONTAINER="$STATUS_CONTAINER" \
VYRAL_AZURE_DURABLE_SMOKE_RUN_ID="$smoke_run_id" \
VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID="$run_id" \
VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID="$timer_run_id" \
dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj --no-restore \
  --filter FullyQualifiedName~AzureDurableFunctionsSmokeLiveTests --logger 'console;verbosity=minimal'
LIVE_ASSERTIONS_PASSED=true
GATE_RESULT="passed"
FAILURE_STAGE="complete"
echo 'azure-durable-functions-live-gate=ok'
