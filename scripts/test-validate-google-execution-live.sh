#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/vyral-google-live-gate-test-XXXXXX")"

cleanup() {
  rm -rf "$WORK"
}
trap cleanup EXIT

mkdir -p "$WORK/bin"

cat > "$WORK/bin/gcloud" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ -n "${GCLOUD_CALL_LOG:-}" ]]; then
  printf '%s\n' "$*" >>"$GCLOUD_CALL_LOG"
fi

if [[ "$1" == "auth" && "$2" == "print-access-token" ]]; then
  printf '%s\n' 'test-access-token-do-not-log'
  exit 0
fi

if [[ "$1" == "tasks" && "$2" == "queues" && "$3" == "purge" && "${FAIL_QUEUE_PURGE:-}" == true ]]; then
  exit 88
fi

if { [[ "$1" == "firestore" && "$2" == "databases" && "$3" == "describe" ]]; } ||
   { [[ "$1" == "iam" && "$2" == "service-accounts" && "$3" == "describe" ]]; } ||
   { [[ "$1" == "tasks" && "$2" == "queues" && ( "$3" == "create" || "$3" == "pause" || "$3" == "delete" || "$3" == "purge" ) ]]; }; then
  exit 0
fi

if [[ "$1" == "tasks" && "$2" == "queues" && "$3" == "describe" ]]; then
  printf '%s\n' "${EXPECTED_QUEUE_STATE:-PAUSED}"
  exit 0
fi

if [[ "$1" == "tasks" && "$2" == "list" ]]; then
  exit 0
fi

printf 'unexpected gcloud call: %s\n' "$*" >&2
exit 91
EOF

cat > "$WORK/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

[[ "$1" == "test" ]]
[[ "$2" == "tests/Vyral.Tests.Google/Vyral.Tests.Google.csproj" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_PROJECT_ID" == "test-project" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_DATABASE_ID" == "${EXPECTED_DATABASE_ID:-(default)}" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_FIRESTORE_ROOT" == vyral_handoff_* ]]
[[ "$VYRAL_GOOGLE_EXECUTION_TASKS_LOCATION" == "us-central1" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_TASKS_QUEUE" == vyral-it-google-exec-* ]]
[[ "$VYRAL_GOOGLE_EXECUTION_WORKER_URL" == "https://worker.example/tasks/execution" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL" == "tasks@test-project.iam.gserviceaccount.com" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_OIDC_AUDIENCE" == "https://worker.example/tasks/execution" ]]
[[ "$VYRAL_GOOGLE_EXECUTION_ACCESS_TOKEN" == "test-access-token-do-not-log" ]]
printf '%s\n' 'fake-google-live-tests=ok'
EOF

chmod +x "$WORK/bin/gcloud" "$WORK/bin/dotnet"

output="$(
  PATH="$WORK/bin:$PATH" \
  VYRAL_GOOGLE_EXECUTION_PROJECT_ID=test-project \
  VYRAL_GOOGLE_EXECUTION_WORKER_URL=https://worker.example/tasks/execution \
  VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL=tasks@test-project.iam.gserviceaccount.com \
  "$ROOT/scripts/validate-google-execution-live.sh"
)"

[[ "$output" == *"fake-google-live-tests=ok"* ]]
[[ "$output" == *"google-execution-live-gate=ok"* ]]
[[ "$output" == *"google-execution-live-cleanup=queue-deleted"* ]]
[[ "$output" != *"test-access-token-do-not-log"* ]]

reused_output="$(
  PATH="$WORK/bin:$PATH" \
  EXPECTED_DATABASE_ID=test-qualification-db \
  VYRAL_GOOGLE_EXECUTION_PROJECT_ID=test-project \
  VYRAL_GOOGLE_EXECUTION_DATABASE_ID=test-qualification-db \
  VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE=vyral-it-google-exec-test-qualification \
  VYRAL_GOOGLE_EXECUTION_WORKER_URL=https://worker.example/tasks/execution \
  VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL=tasks@test-project.iam.gserviceaccount.com \
  VYRAL_GOOGLE_EXECUTION_RESULT_PATH="$WORK/google-live-gate.json" \
  VYRAL_GOOGLE_LIVE_PROVIDER_VERSION=managed-2026-08-08 \
  VYRAL_GOOGLE_LIVE_AUTH_MODE=workload_identity_federation \
  "$ROOT/scripts/validate-google-execution-live.sh"
)"

[[ "$reused_output" == *"fake-google-live-tests=ok"* ]]
[[ "$reused_output" == *"google-execution-live-cleanup=queue-purged"* ]]
[[ "$reused_output" != *"test-access-token-do-not-log"* ]]
jq -e '
  .schemaVersion == 1 and
  .gate == "google_firestore_cloud_tasks_live" and
  .result == "passed" and
  .environmentClass == "live_managed" and
  .providerVersion == "managed-2026-08-08" and
  (.testCommit | test("^[0-9a-f]{40}$")) and
  .authentication.mode == "workload_identity_federation" and
  .authentication.serviceAccountKeyFile == false and
  .isolation.namedDatabase == true and
  .isolation.reusablePausedQueue == true and
  (.checks | index("adapter_recreation_resume") != null) and
  .cleanup.result == "passed" and
  .cleanup.queueAction == "queue-purged" and
  .cleanup.queueEmptyObserved == true and
  .limitations.automaticQualificationPromotion == false
' "$WORK/google-live-gate.json" >/dev/null
! rg -q 'test-project|tasks@|vyral-it-google-exec-test-qualification|test-qualification-db|worker\.example' \
  "$WORK/google-live-gate.json"

set +e
failed_cleanup_output="$(
  PATH="$WORK/bin:$PATH" \
  FAIL_QUEUE_PURGE=true \
  VYRAL_GOOGLE_EXECUTION_PROJECT_ID=test-project \
  VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE=vyral-it-google-exec-test-qualification \
  VYRAL_GOOGLE_EXECUTION_WORKER_URL=https://worker.example/tasks/execution \
  VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL=tasks@test-project.iam.gserviceaccount.com \
  VYRAL_GOOGLE_EXECUTION_RESULT_PATH="$WORK/google-live-cleanup-failed.json" \
  VYRAL_GOOGLE_LIVE_PROVIDER_VERSION=managed-2026-08-08 \
  VYRAL_GOOGLE_LIVE_AUTH_MODE=workload_identity_federation \
  "$ROOT/scripts/validate-google-execution-live.sh" 2>&1
)"
failed_cleanup_status=$?
set -e

[[ "$failed_cleanup_status" -eq 1 ]]
[[ "$failed_cleanup_output" == *"google-execution-live-cleanup=queue-purge-pending"* ]]
jq -e '.result == "failed" and .cleanup.result == "failed"' \
  "$WORK/google-live-cleanup-failed.json" >/dev/null

set +e
active_output="$(
  PATH="$WORK/bin:$PATH" \
  GCLOUD_CALL_LOG="$WORK/active-gcloud.log" \
  EXPECTED_QUEUE_STATE=RUNNING \
  VYRAL_GOOGLE_EXECUTION_PROJECT_ID=test-project \
  VYRAL_GOOGLE_EXECUTION_EXISTING_QUEUE=vyral-it-google-exec-active \
  VYRAL_GOOGLE_EXECUTION_WORKER_URL=https://worker.example/tasks/execution \
  VYRAL_GOOGLE_EXECUTION_SERVICE_ACCOUNT_EMAIL=tasks@test-project.iam.gserviceaccount.com \
  "$ROOT/scripts/validate-google-execution-live.sh" 2>&1
)"
active_status=$?
set -e

[[ "$active_status" -eq 2 ]]
[[ "$active_output" == *"Reusable Google execution queue must be PAUSED"* ]]
[[ "$active_output" == *"google-execution-live-cleanup=queue-not-owned"* ]]
! grep -q 'tasks queues purge' "$WORK/active-gcloud.log"

printf '%s\n' 'google-execution-live-gate-test=ok'
