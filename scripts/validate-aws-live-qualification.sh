#!/usr/bin/env bash
set -euo pipefail
umask 077

# Runs the isolated S3/DynamoDB storage gate and DynamoDB/SQS execution gate
# under one short-lived, non-root identity, then emits a redacted receipt.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    printf '%s is required.\n' "$name" >&2
    exit 2
  fi
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$1" >&2
    exit 2
  }
}

require_command aws
require_command git
require_command jq
require VYRAL_AWS_LIVE_PROVIDER_VERSION
require VYRAL_AWS_LIVE_AUTH_MODE
require VYRAL_AWS_LIVE_RESULT_PATH

PROVIDER_VERSION="$VYRAL_AWS_LIVE_PROVIDER_VERSION"
AUTH_MODE="$VYRAL_AWS_LIVE_AUTH_MODE"
RESULT_PATH="$VYRAL_AWS_LIVE_RESULT_PATH"
TEST_MODE="${VYRAL_AWS_LIVE_TEST_MODE:-false}"

if [[ ! "$PROVIDER_VERSION" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,159}$ ]]; then
  printf '%s\n' 'VYRAL_AWS_LIVE_PROVIDER_VERSION has an invalid format.' >&2
  exit 2
fi
if [[ "$TEST_MODE" == true ]]; then
  if [[ "$AUTH_MODE" != "test_harness" ]]; then
    printf '%s\n' 'AWS live test mode requires test_harness authentication.' >&2
    exit 2
  fi
  require VYRAL_AWS_STORAGE_GATE_COMMAND
  require VYRAL_AWS_EXECUTION_GATE_COMMAND
  STORAGE_GATE="$VYRAL_AWS_STORAGE_GATE_COMMAND"
  EXECUTION_GATE="$VYRAL_AWS_EXECUTION_GATE_COMMAND"
else
  if [[ "$AUTH_MODE" != "github_oidc" && "$AUTH_MODE" != "assume_role" ]]; then
    printf '%s\n' 'AWS live authentication must be github_oidc or assume_role.' >&2
    exit 2
  fi
  STORAGE_GATE="$ROOT/scripts/validate-aws-storage-live.sh"
  EXECUTION_GATE="$ROOT/scripts/validate-aws-execution-live.sh"
  if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
    printf '%s\n' 'AWS live qualification requires a clean source commit.' >&2
    exit 2
  fi
fi

if [[ ! -x "$STORAGE_GATE" || ! -x "$EXECUTION_GATE" ]]; then
  printf '%s\n' 'AWS live qualification gate commands must be executable.' >&2
  exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/vyral-aws-live-XXXXXX")"
cleanup() {
  rm -rf -- "$WORK"
}
trap cleanup EXIT
mkdir -p "$(dirname "$RESULT_PATH")"

if ! CALLER_ARN="$(
  aws sts get-caller-identity --query Arn --output text \
    >"$WORK/caller.txt" 2>"$WORK/sts.err" && cat "$WORK/caller.txt"
)"; then
  printf '%s\n' 'AWS live qualification could not verify its caller.' >&2
  exit 2
fi
if [[ -z "$CALLER_ARN" || "$CALLER_ARN" == *":root" ]]; then
  printf '%s\n' 'AWS live qualification refuses a root caller.' >&2
  exit 2
fi

set +e
"$STORAGE_GATE" >"$WORK/storage.log" 2>&1
STORAGE_EXIT="$?"
"$EXECUTION_GATE" >"$WORK/execution.log" 2>&1
EXECUTION_EXIT="$?"
set -e

STORAGE_CLEANUP=false
if grep -Fq \
  'aws-storage-live-cleanup=dynamodb:deleted bucket:deleted' \
  "$WORK/storage.log"; then
  STORAGE_CLEANUP=true
fi
EXECUTION_CLEANUP=false
if grep -Fq \
  'aws-execution-live-cleanup=table:deleted queue:deleted' \
  "$WORK/execution.log"; then
  EXECUTION_CLEANUP=true
fi
STORAGE_PASSED=false
if (( STORAGE_EXIT == 0 )) && \
   grep -Fq 'aws-storage-live-gate=ok' "$WORK/storage.log" && \
   [[ "$STORAGE_CLEANUP" == true ]]; then
  STORAGE_PASSED=true
fi
EXECUTION_PASSED=false
if (( EXECUTION_EXIT == 0 )) && \
   grep -Fq 'aws-execution-live-gate=ok' "$WORK/execution.log" && \
   [[ "$EXECUTION_CLEANUP" == true ]]; then
  EXECUTION_PASSED=true
fi

GATE_RESULT=failed
if [[ "$STORAGE_PASSED" == true && "$EXECUTION_PASSED" == true ]]; then
  GATE_RESULT=passed
fi
SOURCE_TREE_CLEAN=true
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
  SOURCE_TREE_CLEAN=false
fi
ENVIRONMENT_CLASS=live_managed
SHORT_LIVED=true
if [[ "$TEST_MODE" == true ]]; then
  ENVIRONMENT_CLASS=deterministic_fixture
  SHORT_LIVED=false
fi

jq --null-input \
  --arg result "$GATE_RESULT" \
  --arg environmentClass "$ENVIRONMENT_CLASS" \
  --arg providerVersion "$PROVIDER_VERSION" \
  --arg authMode "$AUTH_MODE" \
  --arg testedAtUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg testCommit "$(git rev-parse HEAD)" \
  --argjson sourceTreeClean "$SOURCE_TREE_CLEAN" \
  --argjson shortLived "$SHORT_LIVED" \
  --argjson storageExitCode "$STORAGE_EXIT" \
  --argjson executionExitCode "$EXECUTION_EXIT" \
  --argjson storagePassed "$STORAGE_PASSED" \
  --argjson executionPassed "$EXECUTION_PASSED" \
  --argjson storageCleanup "$STORAGE_CLEANUP" \
  --argjson executionCleanup "$EXECUTION_CLEANUP" \
  '{
    schemaVersion: 1,
    gate: "aws_s3_dynamodb_sqs_live",
    result: $result,
    environmentClass: $environmentClass,
    scope: "s3_dynamodb_storage_and_dynamodb_sqs_execution",
    providerVersion: $providerVersion,
    testedAtUtc: $testedAtUtc,
    testCommit: $testCommit,
    sourceTreeClean: $sourceTreeClean,
    authentication: {
      mode: $authMode,
      shortLivedSession: $shortLived,
      rootCaller: false,
      secretsRedacted: true
    },
    isolation: {
      runScopedResourceNames: true,
      privateEncryptedBucket: true,
      prefixedDynamoTables: true,
      prefixedSqsQueue: true,
      identifiersRedacted: true
    },
    storageExitCode: $storageExitCode,
    executionExitCode: $executionExitCode,
    checks: {
      storage: $storagePassed,
      execution: $executionPassed,
      s3: $storagePassed,
      dynamodb: ($storagePassed and $executionPassed),
      sqs: $executionPassed
    },
    cleanup: {
      result: (if $storageCleanup and $executionCleanup then "passed" else "failed" end),
      storageResourcesRemoved: $storageCleanup,
      executionResourcesRemoved: $executionCleanup,
      identifiersRedacted: true
    },
    limitations: {
      managedOpenSearchCovered: false,
      consumerEnvironmentCovered: false,
      automaticQualificationPromotion: false
    }
  }' >"$RESULT_PATH"

printf 'aws-live-qualification=result:%s storage:%s execution:%s cleanup:%s\n' \
  "$GATE_RESULT" "$STORAGE_PASSED" "$EXECUTION_PASSED" \
  "$([[ "$STORAGE_CLEANUP" == true && "$EXECUTION_CLEANUP" == true ]] && printf passed || printf failed)"
if [[ "$GATE_RESULT" != passed ]]; then
  printf '%s\n' \
    'AWS live qualification failed; raw provider logs were not published.' >&2
  exit 1
fi
