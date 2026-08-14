#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/vyral-azure-live-script-test.XXXXXX")"
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT

mkdir -p "$work/bin" "$work/temp" "$work/receipts"

cat > "$work/bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
case "${1:-}" in
  publish)
    output=""
    while (( $# > 0 )); do
      if [[ "$1" == --output ]]; then
        output="$2"
        break
      fi
      shift
    done
    [[ -n "$output" ]]
    mkdir -p "$output"
    printf '{"version":"2.0"}\n' > "$output/host.json"
    printf '%s\n' '[{"name":"VyralAzureDurableSmokeCancel"},{"name":"VyralAzureDurableSmokeGet"},{"name":"VyralAzureDurableSmokeRaiseEvent"},{"name":"VyralAzureDurableSmokeStart"},{"name":"VyralExecutionRuntimeOrchestrator"},{"name":"VyralExecutionRuntimeStart"},{"name":"VyralExecutionRuntimeStep"}]' > "$output/functions.metadata"
    ;;
  test)
    printf 'mock-live-assertions=passed\n'
    ;;
  *)
    exit 2
    ;;
esac
SH

cat > "$work/bin/zip" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == -qr ]]
touch "$2"
SH

cat > "$work/bin/sleep" <<'SH'
#!/usr/bin/env bash
exit 0
SH

cat > "$work/bin/timeout" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${VYRAL_TEST_DEPLOYMENT_TIMEOUT:-false}" == true && "$*" == *"functionapp deployment source config-zip"* ]]; then
  exit 124
fi
exec /usr/bin/timeout "$@"
SH

cat > "$work/bin/az" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
command="$*"
case "$command" in
  "storage account show-connection-string "*)
    printf 'DefaultEndpointsProtocol=https;AccountName=fixture;AccountKey=fixture;EndpointSuffix=core.windows.net\n'
    ;;
  "functionapp keys list "*)
    printf '%s\n' '{"functionKeys":{"default":"test-function-key-never-log"},"masterKey":"test-master-key-never-log"}'
    ;;
  "account show "*)
    printf 'test-subscription\n'
    ;;
  "rest --method post --url "*"/host/default/sync?api-version=2024-11-01 "*)
    if [[ "${VYRAL_TEST_SYNC_FAIL:-false}" == true ]]; then
      exit 1
    fi
    touch "$VYRAL_TEST_STATE/trigger-sync-issued"
    ;;
  "functionapp restart "*)
    touch "$VYRAL_TEST_STATE/host-restart-issued"
    ;;
  "functionapp show "*)
    if [[ "${VYRAL_TEST_HOSTING_PLAN:-flex_consumption}" == windows_consumption ]]; then
      printf '%s\n' '{"siteConfig":{"netFrameworkVersion":"v10.0"}}'
    else
      printf '%s\n' '{"functionAppConfig":{"runtime":{"name":"dotnet-isolated","version":"10.0"}}}'
    fi
    ;;
  "functionapp config appsettings list "*)
    printf '%s\n' '[{"name":"FUNCTIONS_WORKER_RUNTIME","value":"dotnet-isolated"}]'
    ;;
  "functionapp log deployment list "*)
    if [[ "${VYRAL_TEST_DEPLOYMENT_TIMEOUT:-false}" == true ]]; then
      printf '%s\n' '[]'
    elif [[ "${VYRAL_TEST_DEPLOYMENT_ALWAYS_FAIL:-false}" == true ]]; then
      printf '%s\n' '[{"status":6,"message":"Deployment was successful but Reset all workers endpoint responded with Response status code 503 (Site Unavailable)."}]'
    elif [[ "${VYRAL_TEST_DEPLOYMENT_FAIL_ONCE:-false}" == true && ! -e "$VYRAL_TEST_STATE/deployment-failed-once" ]]; then
      touch "$VYRAL_TEST_STATE/deployment-failed-once"
      printf '%s\n' '[{"status":6}]'
    else
      printf '%s\n' '[{"status":4}]'
    fi
    ;;
  "cosmosdb sql container show "*)
    if [[ "${VYRAL_TEST_FUNCTION_COUNT:-7}" != 7 && "${VYRAL_TEST_RECOVER_AFTER_SYNC:-false}" != true ]]; then
      exit 1
    fi
    ;;
  "storage account create "* | \
  "functionapp create "* | \
  "functionapp config appsettings set "* | \
  "functionapp deployment source config-zip "* | \
  "cosmosdb sql container delete "* | \
  "functionapp delete "* | \
  "storage account delete "*)
    ;;
  *)
    printf 'unexpected az command\n' >&2
    exit 2
    ;;
esac
SH

cat > "$work/bin/curl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
method=GET
data=""
url=""
while (( $# > 0 )); do
  case "$1" in
    -X)
      method="$2"
      shift 2
      ;;
    --data)
      data="$2"
      shift 2
      ;;
    http*)
      url="$1"
      shift
      ;;
    *)
      shift
      ;;
  esac
done
[[ -n "$url" ]]

case "$url" in
  */admin/functions | */admin/functions/)
    function_count="${VYRAL_TEST_FUNCTION_COUNT:-7}"
    if [[ "${VYRAL_TEST_RECOVER_AFTER_SYNC:-false}" == true && \
      ( -e "$VYRAL_TEST_STATE/trigger-sync-issued" || -e "$VYRAL_TEST_STATE/host-restart-issued" ) ]]; then
      function_count=7
    fi
    case "$function_count" in
      7)
        printf '%s\n' '[{"name":"VyralAzureDurableSmokeCancel"},{"name":"VyralAzureDurableSmokeGet"},{"name":"VyralAzureDurableSmokeRaiseEvent"},{"name":"VyralAzureDurableSmokeStart"},{"name":"VyralExecutionRuntimeOrchestrator"},{"name":"VyralExecutionRuntimeStart"},{"name":"VyralExecutionRuntimeStep"}]'
        ;;
      1)
        printf '%s\n' '[{"name":"VyralAzureDurableSmokeCancel"}]'
        ;;
      0)
        printf '%s\n' '[]'
        ;;
      *)
        exit 2
        ;;
    esac
    ;;
  */runs/not-a-run)
    printf '404'
    ;;
  */runs)
    [[ "$method" == POST ]]
    if [[ "$data" == *waitForEvent* ]]; then
      printf '%s\n' '{"id":"wait"}'
    elif [[ "$data" == *waitForTimerAtUtc* ]]; then
      printf '%s\n' '{"id":"timer"}'
    else
      printf '%s\n' '{"id":"ordinary"}'
    fi
    ;;
  */runs/wait/events/approval)
    [[ "$method" == POST ]]
    touch "$VYRAL_TEST_STATE/event-received"
    ;;
  */runs/ordinary)
    printf '%s\n' '{"status":"succeeded"}'
    ;;
  */runs/wait)
    if [[ -e "$VYRAL_TEST_STATE/event-received" ]]; then
      printf '%s\n' '{"status":"succeeded"}'
    else
      printf '%s\n' '{"status":"waiting"}'
    fi
    ;;
  */runs/timer)
    if [[ "${VYRAL_TEST_TIMER_FAILURE:-false}" == true ]]; then
      printf '%s\n' '{"status":"failed","failureClass":"provider_failure"}'
    elif [[ -e "$VYRAL_TEST_STATE/timer-observed" ]]; then
      printf '%s\n' '{"status":"succeeded"}'
    else
      touch "$VYRAL_TEST_STATE/timer-observed"
      printf '%s\n' '{"status":"waiting"}'
    fi
    ;;
  *)
    printf 'unexpected curl URL\n' >&2
    exit 2
    ;;
esac
SH

chmod 0755 "$work/bin/az" "$work/bin/curl" "$work/bin/dotnet" "$work/bin/sleep" "$work/bin/timeout" "$work/bin/zip"

run_case() {
  local name="$1"
  local function_count="$2"
  local expected_status="$3"
  local expected_result="$4"
  local expected_stage="$5"
  local expected_sync="$6"
  local recover_after_sync="${7:-false}"
  local sync_fail="${8:-false}"
  local expected_restart="${9:-false}"
  local deployment_fail_once="${10:-false}"
  local expected_deployment_attempts="${11:-1}"
  local deployment_always_fail="${12:-false}"
  local expected_deployment_failure_class="${13:-worker_reset_503}"
  local deployment_timeout="${14:-false}"
  local hosting_plan="${15:-flex_consumption}"
  local timer_failure="${16:-false}"
  local state="$work/state-$name"
  local receipt="$work/receipts/$name.json"
  local output="$work/$name.log"
  mkdir -p "$state"

  set +e
  PATH="$work/bin:$PATH" \
  TMPDIR="$work/temp" \
  VYRAL_TEST_STATE="$state" \
  VYRAL_TEST_FUNCTION_COUNT="$function_count" \
  VYRAL_TEST_RECOVER_AFTER_SYNC="$recover_after_sync" \
  VYRAL_TEST_SYNC_FAIL="$sync_fail" \
  VYRAL_TEST_DEPLOYMENT_FAIL_ONCE="$deployment_fail_once" \
  VYRAL_TEST_DEPLOYMENT_ALWAYS_FAIL="$deployment_always_fail" \
  VYRAL_TEST_DEPLOYMENT_TIMEOUT="$deployment_timeout" \
  VYRAL_TEST_HOSTING_PLAN="$hosting_plan" \
  VYRAL_TEST_TIMER_FAILURE="$timer_failure" \
  VYRAL_AZURE_FUNCTIONS_HOSTING_PLAN="$hosting_plan" \
  VYRAL_AZURE_LIVE_RESOURCE_GROUP=test-disposable \
  VYRAL_AZURE_LIVE_COSMOS_ACCOUNT=test-cosmos \
  VYRAL_AZURE_COSMOS_CONNECTION_STRING='AccountEndpoint=https://fixture.invalid;AccountKey=fixture;' \
  VYRAL_AZURE_COSMOS_DATABASE=test-database \
  VYRAL_AZURE_LIVE_RECEIPT_PATH="$receipt" \
    "$ROOT/scripts/validate-azure-durable-functions-live.sh" >"$output" 2>&1
  status="$?"
  set -e

  [[ "$status" -eq "$expected_status" ]]
  [[ "$(jq -r .result "$receipt")" == "$expected_result" ]]
  [[ "$(jq -r '.cleanup.result' "$receipt")" == passed ]]
  [[ "$(jq -r '.cleanup.functionApp' "$receipt")" == deleted ]]
  [[ "$(jq -r '.cleanup.storageAccount' "$receipt")" == deleted ]]
  [[ "$(jq -r '.recovery.partialInventoryTriggerSyncAttempted' "$receipt")" == "$expected_sync" ]]
  if [[ "$expected_sync" == true ]]; then
    [[ "$(jq -r '.recovery.partialInventoryTriggerSyncApiVersion' "$receipt")" == 2024-11-01 ]]
    [[ "$(jq -r '.recovery.partialInventoryTriggerSyncRoute' "$receipt")" == host-default-sync ]]
  else
    [[ "$(jq -r '.recovery.partialInventoryTriggerSyncApiVersion' "$receipt")" == null ]]
    [[ "$(jq -r '.recovery.partialInventoryTriggerSyncRoute' "$receipt")" == null ]]
  fi
  [[ "$(jq -r '.recovery.partialInventoryRestartAttempted' "$receipt")" == "$expected_restart" ]]
  [[ "$(jq -r '.diagnostics.deploymentAttempts' "$receipt")" == "$expected_deployment_attempts" ]]
  [[ "$(jq -r '.diagnostics.hostingPlan' "$receipt")" == "$hosting_plan" ]]
  [[ "$(jq -r '.diagnostics.configuredRuntime.name' "$receipt")" == dotnet-isolated ]]
  [[ "$(jq -r '.diagnostics.configuredRuntime.version' "$receipt")" == 10.0 ]]
  [[ "$(jq -r '.diagnostics.configuredRuntime.matchedExpectedDotnetIsolated10' "$receipt")" == true ]]
  if [[ "$expected_stage" == durable-timer ]]; then
    [[ "$(jq -r '.diagnostics.durableTimer.terminalStatus' "$receipt")" == failed ]]
    [[ "$(jq -r '.diagnostics.durableTimer.failureClass' "$receipt")" == provider_failure ]]
  elif [[ "$expected_result" == passed ]]; then
    [[ "$(jq -r '.diagnostics.durableTimer.terminalStatus' "$receipt")" == succeeded ]]
    [[ "$(jq -r '.diagnostics.durableTimer.failureClass' "$receipt")" == null ]]
  fi
  if [[ "$expected_stage" == deployment ]]; then
    if [[ "$expected_deployment_failure_class" == timeout ]]; then
      [[ "$(jq -r '.diagnostics.deploymentProviderStatus' "$receipt")" == not-observed ]]
    else
      [[ "$(jq -r '.diagnostics.deploymentProviderStatus' "$receipt")" == 6 ]]
    fi
    [[ "$(jq -r '.diagnostics.deploymentProviderFailureClass' "$receipt")" == "$expected_deployment_failure_class" ]]
    [[ "$(jq -r '.diagnostics.postDeploymentFailureRecovery.attempted' "$receipt")" == true ]]
    [[ "$(jq -r '.diagnostics.postDeploymentFailureRecovery.restartIssued' "$receipt")" == true ]]
    [[ "$(jq -r '.diagnostics.postDeploymentFailureRecovery.masterKeyAvailable' "$receipt")" == true ]]
    [[ "$(jq -r '.diagnostics.postDeploymentFailureRecovery.runtimeFunctionCount' "$receipt")" == "$function_count" ]]
  else
    [[ "$(jq -r '.diagnostics.deploymentProviderStatus' "$receipt")" == 4 ]]
    [[ "$(jq -r '.diagnostics.deploymentProviderFailureClass' "$receipt")" == none ]]
    [[ "$(jq -r '.diagnostics.postDeploymentFailureRecovery.attempted' "$receipt")" == false ]]
  fi
  [[ "$(jq -r '.diagnostics.packagedFunctionNames | length' "$receipt")" == 7 ]]
  [[ "$(stat -c '%a' "$receipt")" == 600 ]]
  ! grep -Fq 'test-function-key-never-log' "$output"
  ! grep -Fq 'test-master-key-never-log' "$output"
  if [[ "$expected_result" == passed ]]; then
    jq -e '
      .failure == null and
      ([.checks[]] | all) and
      .cleanup.statusContainer == "deleted"
    ' "$receipt" >/dev/null
  else
    [[ "$(jq -r '.failure.stage' "$receipt")" == "$expected_stage" ]]
    [[ "$(jq -r '.failure.discoveredFunctionCount' "$receipt")" == "$function_count" ]]
    [[ "$(jq -r '.failure.expectedFunctionCount' "$receipt")" == 7 ]]
    [[ "$(jq -r '.diagnostics.runtimeFunctionNames | length' "$receipt")" == "$function_count" ]]
    if [[ "$expected_stage" == durable-timer ]]; then
      [[ "$(jq -r '.cleanup.statusContainer' "$receipt")" == deleted ]]
    else
      [[ "$(jq -r '.cleanup.statusContainer' "$receipt")" == not-created ]]
    fi
    if [[ "$expected_stage" == function-discovery-trigger-sync ]]; then
      [[ "$(jq -r '.recovery.partialInventoryTriggerSyncErrorCode' "$receipt")" == unknown ]]
    fi
  fi
}

run_case success 7 0 passed complete false
run_case windows-consumption-success 7 0 passed complete false false false false false 1 false worker_reset_503 false windows_consumption
run_case deployment-status-retry 7 0 passed complete false false false false true 2
run_case deployment-status-failure 0 1 failed deployment false false false false false 3 true
run_case deployment-command-timeout 0 1 failed deployment false false false false false 3 false timeout true
run_case durable-timer-failure 7 1 failed durable-timer false false false false false 1 false worker_reset_503 false flex_consumption true
run_case discovery-failure 0 1 failed function-discovery false
run_case incomplete-discovery 1 1 failed function-discovery true
run_case trigger-sync-recovery 1 0 passed complete true true
run_case trigger-sync-failure-recovery 1 0 passed complete true true true true
run_case trigger-sync-failure 1 1 failed function-discovery true false true true

if find "$work/temp" -mindepth 1 -print -quit | grep -q .; then
  echo 'Azure live script left temporary residue.' >&2
  exit 1
fi

printf 'azure-live-script=ok complete-discovery-required=true receipts-redacted=true cleanup=true\n'
