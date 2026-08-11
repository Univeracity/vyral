#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/vyral-aws-live-test-XXXXXX")"
cleanup() {
  rm -rf -- "$WORK"
}
trap cleanup EXIT
mkdir -p "$WORK/bin"

cat >"$WORK/bin/aws" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1 $2" == "sts get-caller-identity" ]]; then
  printf '%s\n' 'arn:aws:sts::123456789012:assumed-role/test-role/test-session'
  exit 0
fi
exit 90
EOF

cat >"$WORK/storage" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' 'private-test-marker'
printf '%s\n' 'aws-storage-live-gate=ok'
printf '%s\n' 'aws-storage-live-cleanup=dynamodb:deleted bucket:deleted'
EOF

cat >"$WORK/execution" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' 'private-test-marker'
printf '%s\n' 'aws-execution-live-gate=ok'
printf 'aws-execution-live-cleanup=table:deleted queue:%s\n' "${QUEUE_RESULT:-deleted}"
EOF
chmod +x "$WORK/bin/aws" "$WORK/storage" "$WORK/execution"

output="$(
  PATH="$WORK/bin:$PATH" \
  VYRAL_AWS_LIVE_TEST_MODE=true \
  VYRAL_AWS_LIVE_AUTH_MODE=test_harness \
  VYRAL_AWS_LIVE_PROVIDER_VERSION=test-managed \
  VYRAL_AWS_LIVE_RESULT_PATH="$WORK/passed.json" \
  VYRAL_AWS_STORAGE_GATE_COMMAND="$WORK/storage" \
  VYRAL_AWS_EXECUTION_GATE_COMMAND="$WORK/execution" \
  "$ROOT/scripts/validate-aws-live-qualification.sh"
)"

[[ "$output" == *"result:passed"* ]]
[[ "$output" != *"private-test-marker"* ]]
jq -e '
  .schemaVersion == 1 and
  .gate == "aws_s3_dynamodb_sqs_live" and
  .result == "passed" and
  .environmentClass == "deterministic_fixture" and
  .authentication.mode == "test_harness" and
  .authentication.rootCaller == false and
  .isolation.identifiersRedacted == true and
  .checks.storage == true and
  .checks.execution == true and
  .cleanup.result == "passed" and
  .limitations.managedOpenSearchCovered == false and
  .limitations.automaticQualificationPromotion == false
' "$WORK/passed.json" >/dev/null
! rg -q '123456789012|test-role|test-session|private-test-marker' \
  "$WORK/passed.json"

set +e
failed_output="$(
  PATH="$WORK/bin:$PATH" \
  QUEUE_RESULT=delete-pending \
  VYRAL_AWS_LIVE_TEST_MODE=true \
  VYRAL_AWS_LIVE_AUTH_MODE=test_harness \
  VYRAL_AWS_LIVE_PROVIDER_VERSION=test-managed \
  VYRAL_AWS_LIVE_RESULT_PATH="$WORK/failed.json" \
  VYRAL_AWS_STORAGE_GATE_COMMAND="$WORK/storage" \
  VYRAL_AWS_EXECUTION_GATE_COMMAND="$WORK/execution" \
  "$ROOT/scripts/validate-aws-live-qualification.sh" 2>&1
)"
failed_status="$?"
set -e
[[ "$failed_status" -eq 1 ]]
[[ "$failed_output" == *"result:failed"* ]]
[[ "$failed_output" != *"private-test-marker"* ]]
jq -e '
  .result == "failed" and
  .checks.storage == true and
  .checks.execution == false and
  .cleanup.result == "failed" and
  .cleanup.executionResourcesRemoved == false
' "$WORK/failed.json" >/dev/null

printf '%s\n' 'aws-live-qualification-gate-test=ok'
