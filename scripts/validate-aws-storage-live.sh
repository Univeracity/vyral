#!/usr/bin/env bash
set -euo pipefail
umask 077

# Creates an isolated private S3 bucket and a unique DynamoDB table prefix, runs the AWS storage
# conformance suite, then removes every resource created by the gate. It uses the active AWS CLI
# identity and never prints credentials.

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

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text --region "$REGION")"

# Validate service activation and caller permissions before creating any temporary resource. A
# newly created AWS account can authenticate successfully while S3 or DynamoDB is not yet enabled.
echo "aws-storage-live-gate=checking-service-access region=${REGION}"
aws s3api list-buckets --region "$REGION" --output text >/dev/null
aws dynamodb list-tables --region "$REGION" --output text >/dev/null

STAMP="$(date -u +%Y%m%d%H%M%S)"
BUCKET="vyral-it-${ACCOUNT_ID}-${STAMP}-${RANDOM}"
DYNAMO_PREFIX="vyral-it-aws-${STAMP}-${RANDOM}"
BUCKET_CREATED=false
DYNAMODB_CLEANUP="not-created"
BUCKET_CLEANUP="not-created"

delete_dynamo_tables() {
  local attempt=0 tables table
  while (( attempt < 90 )); do
    tables="$(aws dynamodb list-tables --region "$REGION" \
      --query "TableNames[?starts_with(@, \`${DYNAMO_PREFIX}\`)]" --output text 2>/dev/null || true)"
    if [[ -z "$tables" ]]; then
      DYNAMODB_CLEANUP="deleted"
      return 0
    fi

    for table in $tables; do
      # ResourceInUse is expected when a test's scoped store has already begun
      # deleting the same table. The next poll observes its disappearance.
      aws dynamodb delete-table --table-name "$table" --region "$REGION" \
        --output text >/dev/null 2>&1 || true
    done

    ((attempt += 1))
    sleep 2
  done

  DYNAMODB_CLEANUP="delete-pending"
  echo "aws-storage-live-gate=warning temporary DynamoDB cleanup is still pending" >&2
  return 1
}

cleanup() {
  local cleanup_failed=false

  delete_dynamo_tables || cleanup_failed=true
  if [[ "$BUCKET_CREATED" == true ]]; then
    if aws s3 rm "s3://${BUCKET}" --recursive --region "$REGION" --only-show-errors && \
      aws s3api delete-bucket --bucket "$BUCKET" --region "$REGION" --output text >/dev/null; then
      BUCKET_CLEANUP="deleted"
    else
      BUCKET_CLEANUP="delete-pending"
      cleanup_failed=true
    fi
  fi

  if [[ "$cleanup_failed" == true ]]; then
    echo "aws-storage-live-cleanup=dynamodb:${DYNAMODB_CLEANUP} bucket:${BUCKET_CLEANUP}" >&2
  else
    echo "aws-storage-live-cleanup=dynamodb:${DYNAMODB_CLEANUP} bucket:${BUCKET_CLEANUP}"
  fi
  return 0
}
trap cleanup EXIT

echo "aws-storage-live-gate=creating-isolated-resources region=${REGION}"
if [[ "$REGION" == "us-east-1" ]]; then
  aws s3api create-bucket --bucket "$BUCKET" --region "$REGION" --output text >/dev/null
else
  aws s3api create-bucket --bucket "$BUCKET" --region "$REGION" \
    --create-bucket-configuration "LocationConstraint=${REGION}" --output text >/dev/null
fi
BUCKET_CREATED=true

aws s3api put-public-access-block --bucket "$BUCKET" --region "$REGION" \
  --public-access-block-configuration \
  'BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true' \
  --output text >/dev/null
aws s3api put-bucket-encryption --bucket "$BUCKET" --region "$REGION" \
  --server-side-encryption-configuration \
  '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}' \
  --output text >/dev/null

echo "aws-storage-live-gate=running-conformance"
AWS_DEFAULT_REGION="$REGION" AWS_REGION="$REGION" \
VYRAL_AWS_DYNAMODB_TABLE_PREFIX="$DYNAMO_PREFIX" \
VYRAL_AWS_S3_BUCKET="$BUCKET" \
dotnet test tests/Vyral.Tests.Aws/Vyral.Tests.Aws.csproj --no-restore \
  --logger 'console;verbosity=minimal'

echo "aws-storage-live-gate=ok"
