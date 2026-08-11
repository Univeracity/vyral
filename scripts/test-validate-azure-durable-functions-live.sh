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
  "cosmosdb sql container show "*)
    if [[ "${VYRAL_TEST_FUNCTION_COUNT:-7}" != 7 ]]; then
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
    case "${VYRAL_TEST_FUNCTION_COUNT:-7}" in
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
    if [[ -e "$VYRAL_TEST_STATE/timer-observed" ]]; then
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

chmod 0755 "$work/bin/az" "$work/bin/curl" "$work/bin/dotnet" "$work/bin/sleep" "$work/bin/zip"

run_case() {
  local name="$1"
  local function_count="$2"
  local expected_status="$3"
  local expected_result="$4"
  local expected_stage="$5"
  local state="$work/state-$name"
  local receipt="$work/receipts/$name.json"
  local output="$work/$name.log"
  mkdir -p "$state"

  set +e
  PATH="$work/bin:$PATH" \
  TMPDIR="$work/temp" \
  VYRAL_TEST_STATE="$state" \
  VYRAL_TEST_FUNCTION_COUNT="$function_count" \
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
    [[ "$(jq -r '.cleanup.statusContainer' "$receipt")" == not-created ]]
  fi
}

run_case success 7 0 passed complete
run_case discovery-failure 0 1 failed function-discovery
run_case incomplete-discovery 1 1 failed function-discovery

if find "$work/temp" -mindepth 1 -print -quit | grep -q .; then
  echo 'Azure live script left temporary residue.' >&2
  exit 1
fi

printf 'azure-live-script=ok complete-discovery-required=true receipts-redacted=true cleanup=true\n'
