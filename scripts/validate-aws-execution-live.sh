#!/usr/bin/env bash
set -euo pipefail
umask 077

# Creates an isolated DynamoDB table and temporary SQS queue, exercises the AWS queue-first
# execution runtime against both services, then removes every resource. It never prints AWS
# credentials or queue payloads.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required." >&2
    exit 2
  }
}

require_command aws
require_command dotnet

REGION="${VYRAL_AWS_LIVE_REGION:-${AWS_DEFAULT_REGION:-${AWS_REGION:-$(aws configure get region)}}}"
if [[ -z "$REGION" || "$REGION" == "None" ]]; then
  echo "Set VYRAL_AWS_LIVE_REGION, AWS_DEFAULT_REGION, AWS_REGION, or an AWS CLI default region." >&2
  exit 2
fi

echo "aws-execution-live-gate=checking-service-access region=${REGION}"
aws dynamodb list-tables --region "$REGION" --output text >/dev/null
aws sqs list-queues --region "$REGION" --output text >/dev/null

STAMP="$(date -u +%Y%m%d%H%M%S)"
TABLE="vyral-it-aws-exec-${STAMP}-${RANDOM}"
QUEUE_NAME="vyral-it-aws-exec-${STAMP}-${RANDOM}"
QUEUE_URL=""
TABLE_CLEANUP="not-created"
QUEUE_CLEANUP="not-created"

wait_for_table_deletion() {
  local attempt=0
  while (( attempt < 90 )); do
    if ! aws dynamodb describe-table --table-name "$TABLE" --region "$REGION" --output text >/dev/null 2>&1; then
      TABLE_CLEANUP="deleted"
      return 0
    fi
    ((attempt += 1))
    sleep 2
  done
  TABLE_CLEANUP="delete-pending"
  echo "aws-execution-live-gate=warning temporary DynamoDB cleanup is still pending" >&2
  return 1
}

cleanup() {
  local cleanup_failed=false

  if [[ -n "$QUEUE_URL" ]]; then
    if aws sqs delete-queue --queue-url "$QUEUE_URL" --region "$REGION" --output text >/dev/null 2>&1; then
      QUEUE_CLEANUP="deleted"
    else
      QUEUE_CLEANUP="delete-pending"
      cleanup_failed=true
    fi
  fi
  if ! aws dynamodb delete-table --table-name "$TABLE" --region "$REGION" --output text >/dev/null 2>&1; then
    cleanup_failed=true
  fi
  wait_for_table_deletion || cleanup_failed=true

  if [[ "$cleanup_failed" == true ]]; then
    echo "aws-execution-live-cleanup=table:${TABLE_CLEANUP} queue:${QUEUE_CLEANUP}" >&2
  else
    echo "aws-execution-live-cleanup=table:${TABLE_CLEANUP} queue:${QUEUE_CLEANUP}"
  fi
  return 0
}
trap cleanup EXIT

echo "aws-execution-live-gate=creating-isolated-resources region=${REGION}"
QUEUE_URL="$(aws sqs create-queue --queue-name "$QUEUE_NAME" --region "$REGION" \
  --attributes 'VisibilityTimeout=60,ReceiveMessageWaitTimeSeconds=5' --query QueueUrl --output text)"

echo "aws-execution-live-gate=running-live-test"
AWS_DEFAULT_REGION="$REGION" AWS_REGION="$REGION" \
VYRAL_AWS_EXECUTION_DYNAMODB_TABLE="$TABLE" \
VYRAL_AWS_EXECUTION_SQS_QUEUE_URL="$QUEUE_URL" \
dotnet test tests/Vyral.Tests.Aws/Vyral.Tests.Aws.csproj --no-restore \
  --filter 'FullyQualifiedName~AwsDynamoExecutionLive' --logger 'console;verbosity=minimal'

echo "aws-execution-live-gate=ok"
