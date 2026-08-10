#!/usr/bin/env bash
set -euo pipefail

# Read-only deployment preflight for the DynamoDB/SQS execution adapter. It never creates,
# updates, enqueues, consumes, deploys, or deletes an AWS resource. IAM simulation is used when
# role ARNs are supplied, but callers should still run the isolated live gate before cutover.

usage() {
  cat <<'EOF'
Usage:
  VYRAL_AWS_EXECUTION_DYNAMODB_TABLE=vyral-execution-production \
  VYRAL_AWS_EXECUTION_SQS_QUEUE_URL=https://sqs.REGION.amazonaws.com/ACCOUNT/vyral-execution-production \
  VYRAL_AWS_EXECUTION_DYNAMODB_ROOT=vyral-execution-production \
  VYRAL_AWS_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN=arn:aws:iam::ACCOUNT:role/vyral-runtime \
  VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN=arn:aws:iam::ACCOUNT:role/product-worker \
  deploy/preflight-aws-execution.sh

Required variables:
  VYRAL_AWS_EXECUTION_DYNAMODB_TABLE       Existing DynamoDB execution table.
  VYRAL_AWS_EXECUTION_SQS_QUEUE_URL        Existing standard or FIFO SQS execution queue URL.
  VYRAL_AWS_EXECUTION_DYNAMODB_ROOT        Environment-owned root prefix in the shared table.
  VYRAL_AWS_EXECUTION_HANDLER_IDS          Comma-separated external handler ids.

Recommended variables:
  VYRAL_AWS_EXECUTION_CONFIG_FILE          Candidate server env file (default:
                                            deploy/aws-execution.env.example).
  VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN     Runtime principal to IAM-simulate for DynamoDB and
                                            SQS SendMessage access.
  VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN
                                            SQS poller principal to IAM-simulate for receive,
                                            delete, and visibility changes.
  VYRAL_AWS_EXECUTION_EXPECTED_LEASE_SECONDS
                                            Longest normal worker lease TTL (default: 120).

The preflight validates the candidate server configuration, table schema/PITR, queue topology,
visibility versus configured leases, encryption, redrive policy, explicit handler routes, the
current AWS caller's read access, and optional IAM role simulations. It cannot prove application-
level HTTP/OIDC access; validate that separately at the Vyral server boundary.
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_FILE="${VYRAL_AWS_EXECUTION_CONFIG_FILE:-$ROOT/deploy/aws-execution.env.example}"
TABLE_NAME="${VYRAL_AWS_EXECUTION_DYNAMODB_TABLE:-}"
QUEUE_URL="${VYRAL_AWS_EXECUTION_SQS_QUEUE_URL:-}"
DYNAMODB_ROOT="${VYRAL_AWS_EXECUTION_DYNAMODB_ROOT:-}"
HANDLER_IDS_RAW="${VYRAL_AWS_EXECUTION_HANDLER_IDS:-}"
EXPECTED_LEASE_SECONDS="${VYRAL_AWS_EXECUTION_EXPECTED_LEASE_SECONDS:-120}"
RUNTIME_ROLE_ARN="${VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN:-}"
QUEUE_CONSUMER_ROLE_ARN="${VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN:-}"
REQUIRE_PITR="${VYRAL_AWS_EXECUTION_REQUIRE_PITR:-true}"
REQUIRE_SSE="${VYRAL_AWS_EXECUTION_REQUIRE_SSE:-true}"
REQUIRE_DLQ="${VYRAL_AWS_EXECUTION_REQUIRE_DLQ:-true}"
FAILURES=0

pass() { printf 'PASS %s\n' "$1"; }
warn() { printf 'WARN %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1" >&2; FAILURES=$((FAILURES + 1)); }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }
require_value() { [[ -n "$2" ]] && pass "$1 is set" || fail "$1 is required"; }

config_value() {
  local key="$1"
  [[ -f "$CONFIG_FILE" ]] || return 0
  sed -n "s/^${key}=//p" "$CONFIG_FILE" | tail -n 1
}

config_route_exists() {
  local handler="$1"
  local index candidate
  for index in $(seq 0 99); do
    candidate="$(config_value "ExecutionRuntime__Aws__WorkerRoutes__${index}__HandlerId")"
    [[ "$candidate" == "$handler" ]] && return 0
  done
  return 1
}

config_external_handler_exists() {
  local handler="$1"
  local index candidate
  for index in $(seq 0 99); do
    candidate="$(config_value "ExecutionRuntime__ExternalHandlers__${index}__HandlerId")"
    [[ "$candidate" == "$handler" ]] && return 0
  done
  return 1
}

simulate_role() {
  local role_arn="$1"
  local resource_arn="$2"
  shift 2
  local simulation
  if ! simulation="$(aws iam simulate-principal-policy --policy-source-arn "$role_arn" --action-names "$@" --resource-arns "$resource_arn" --output json 2>/dev/null)"; then
    fail "could not simulate IAM role $role_arn; grant iam:SimulatePrincipalPolicy to the preflight caller or inspect the role policy manually"
    return
  fi
  if jq -e 'all(.EvaluationResults[]?; .EvalDecision == "allowed")' <<<"$simulation" >/dev/null; then
    pass "IAM simulation allows required actions for $role_arn"
  else
    fail "IAM simulation denies one or more required actions for $role_arn"
  fi
}

require_command aws
require_command jq
require_value VYRAL_AWS_EXECUTION_DYNAMODB_TABLE "$TABLE_NAME"
require_value VYRAL_AWS_EXECUTION_SQS_QUEUE_URL "$QUEUE_URL"
require_value VYRAL_AWS_EXECUTION_DYNAMODB_ROOT "$DYNAMODB_ROOT"
require_value VYRAL_AWS_EXECUTION_HANDLER_IDS "$HANDLER_IDS_RAW"
if [[ ! -f "$CONFIG_FILE" ]]; then
  fail "candidate config file does not exist: $CONFIG_FILE"
fi
if ! [[ "$EXPECTED_LEASE_SECONDS" =~ ^[1-9][0-9]*$ ]]; then
  fail "VYRAL_AWS_EXECUTION_EXPECTED_LEASE_SECONDS must be a positive integer"
fi
if [[ "$FAILURES" -gt 0 ]]; then exit 2; fi

if CALLER_ARN="$(aws sts get-caller-identity --query Arn --output text 2>/dev/null)"; then
  pass "AWS caller is authenticated as $CALLER_ARN"
else
  fail "could not determine AWS caller identity"
fi

adapter="$(config_value ExecutionRuntime__Adapter)"
table_config="$(config_value ExecutionRuntime__Aws__DynamoDbTableName)"
queue_config="$(config_value ExecutionRuntime__Aws__SqsQueueUrl)"
region_config="$(config_value ExecutionRuntime__Aws__Region)"
root_config="$(config_value ExecutionRuntime__Aws__Root)"
create_table="$(config_value ExecutionRuntime__Aws__CreateTableIfMissing)"
explicit_routes="$(config_value ExecutionRuntime__Aws__RequireExplicitWorkerRoutes)"
[[ "$adapter" == "aws-dynamodb-sqs" ]] && pass "candidate config selects aws-dynamodb-sqs" || fail "ExecutionRuntime__Adapter must be aws-dynamodb-sqs"
[[ -n "$region_config" ]] && pass "candidate AWS region is explicitly configured" || fail "ExecutionRuntime__Aws__Region must be configured explicitly"
[[ "$table_config" == "$TABLE_NAME" ]] && pass "candidate DynamoDB table matches preflight table" || fail "candidate DynamoDB table must equal VYRAL_AWS_EXECUTION_DYNAMODB_TABLE"
[[ "$queue_config" == "$QUEUE_URL" ]] && pass "candidate SQS queue matches preflight queue" || fail "candidate SQS queue must equal VYRAL_AWS_EXECUTION_SQS_QUEUE_URL"
[[ "$root_config" == "$DYNAMODB_ROOT" && -n "$root_config" ]] && pass "candidate DynamoDB root is explicitly environment-scoped" || fail "candidate DynamoDB root must equal VYRAL_AWS_EXECUTION_DYNAMODB_ROOT"
[[ "$create_table" == "false" ]] && pass "candidate disables automatic DynamoDB table creation" || fail "ExecutionRuntime__Aws__CreateTableIfMissing must be false in deployment configuration"
[[ "$explicit_routes" == "true" ]] && pass "candidate requires explicit worker routes" || fail "ExecutionRuntime__Aws__RequireExplicitWorkerRoutes must be true"

IFS=',' read -r -a HANDLER_IDS <<< "$HANDLER_IDS_RAW"
for index in "${!HANDLER_IDS[@]}"; do
  HANDLER_IDS[$index]="${HANDLER_IDS[$index]//[[:space:]]/}"
  handler="${HANDLER_IDS[$index]}"
  [[ -n "$handler" ]] || { fail "VYRAL_AWS_EXECUTION_HANDLER_IDS contains an empty id"; continue; }
  config_route_exists "$handler" && pass "explicit AWS worker route exists for $handler" || fail "no ExecutionRuntime__Aws__WorkerRoutes entry for handler $handler"
  config_external_handler_exists "$handler" && pass "external handler registration exists for $handler" || fail "no ExecutionRuntime__ExternalHandlers entry for handler $handler"
done

TABLE_JSON=""
if TABLE_JSON="$(aws dynamodb describe-table --table-name "$TABLE_NAME" --output json 2>/dev/null)"; then
  status="$(jq -r '.Table.TableStatus // empty' <<<"$TABLE_JSON")"
  [[ "$status" == "ACTIVE" ]] && pass "DynamoDB table is ACTIVE" || fail "DynamoDB table must be ACTIVE (found ${status:-unknown})"
  jq -e '[.Table.KeySchema[]? | select(.AttributeName == "pk" and .KeyType == "HASH")] | length == 1' <<<"$TABLE_JSON" >/dev/null && pass "DynamoDB table has pk HASH key" || fail "DynamoDB table must define pk as HASH"
  jq -e '[.Table.KeySchema[]? | select(.AttributeName == "sk" and .KeyType == "RANGE")] | length == 1' <<<"$TABLE_JSON" >/dev/null && pass "DynamoDB table has sk RANGE key" || fail "DynamoDB table must define sk as RANGE"
  jq -e '[.Table.GlobalSecondaryIndexes[]? | select(.IndexName == "vyral_execution_work" and .Projection.ProjectionType == "ALL") | .KeySchema] | any(.[]; ([.[] | select(.AttributeName == "gsi1pk" and .KeyType == "HASH")] | length == 1) and ([.[] | select(.AttributeName == "gsi1sk" and .KeyType == "RANGE")] | length == 1))' <<<"$TABLE_JSON" >/dev/null && pass "DynamoDB runnable-work GSI has required keys and ALL projection" || fail "DynamoDB table must define vyral_execution_work over gsi1pk/gsi1sk with ALL projection"
else
  fail "could not describe DynamoDB table $TABLE_NAME"
fi

if [[ "$REQUIRE_PITR" == "true" ]]; then
  if backups="$(aws dynamodb describe-continuous-backups --table-name "$TABLE_NAME" --output json 2>/dev/null)" && [[ "$(jq -r '.ContinuousBackupsDescription.PointInTimeRecoveryDescription.PointInTimeRecoveryStatus // empty' <<<"$backups")" == "ENABLED" ]]; then
    pass "DynamoDB point-in-time recovery is enabled"
  else
    fail "DynamoDB point-in-time recovery must be enabled"
  fi
else
  warn "DynamoDB point-in-time recovery check disabled by VYRAL_AWS_EXECUTION_REQUIRE_PITR=false"
fi

QUEUE_JSON=""
if QUEUE_JSON="$(aws sqs get-queue-attributes --queue-url "$QUEUE_URL" --attribute-names All --output json 2>/dev/null)"; then
  QUEUE_ARN="$(jq -r '.Attributes.QueueArn // empty' <<<"$QUEUE_JSON")"
  visibility="$(jq -r '.Attributes.VisibilityTimeout // "0"' <<<"$QUEUE_JSON")"
  [[ -n "$QUEUE_ARN" ]] && pass "SQS queue is readable" || fail "SQS queue ARN is unavailable"
  if [[ "$visibility" =~ ^[0-9]+$ && "$visibility" -ge "$EXPECTED_LEASE_SECONDS" ]]; then
    pass "SQS visibility timeout (${visibility}s) covers expected lease (${EXPECTED_LEASE_SECONDS}s)"
  else
    fail "SQS visibility timeout (${visibility}s) must cover expected lease (${EXPECTED_LEASE_SECONDS}s)"
  fi
  if [[ "$REQUIRE_DLQ" == "true" ]]; then
    [[ -n "$(jq -r '.Attributes.RedrivePolicy // empty' <<<"$QUEUE_JSON")" ]] && pass "SQS queue has a dead-letter redrive policy" || fail "SQS queue must have a dead-letter redrive policy"
  else
    warn "SQS dead-letter queue check disabled by VYRAL_AWS_EXECUTION_REQUIRE_DLQ=false"
  fi
  if [[ "$REQUIRE_SSE" == "true" ]]; then
    [[ "$(jq -r '.Attributes.SqsManagedSseEnabled // "false"' <<<"$QUEUE_JSON")" == "true" || -n "$(jq -r '.Attributes.KmsMasterKeyId // empty' <<<"$QUEUE_JSON")" ]] && pass "SQS server-side encryption is enabled" || fail "SQS queue must enable SQS-managed or KMS server-side encryption"
  else
    warn "SQS encryption check disabled by VYRAL_AWS_EXECUTION_REQUIRE_SSE=false"
  fi
else
  fail "could not inspect SQS queue"
  QUEUE_ARN=""
fi

if [[ -n "$RUNTIME_ROLE_ARN" && -n "${QUEUE_ARN:-}" ]]; then
  table_arn="$(jq -r '.Table.TableArn // empty' <<<"$TABLE_JSON")"
  [[ -n "$table_arn" ]] && simulate_role "$RUNTIME_ROLE_ARN" "$table_arn" dynamodb:DescribeTable dynamodb:GetItem dynamodb:PutItem dynamodb:DeleteItem dynamodb:UpdateItem dynamodb:Query dynamodb:BatchWriteItem dynamodb:TransactWriteItems
  simulate_role "$RUNTIME_ROLE_ARN" "$QUEUE_ARN" sqs:SendMessage
else
  warn "runtime IAM simulation skipped; set VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN to validate policy bindings"
fi
if [[ -n "$QUEUE_CONSUMER_ROLE_ARN" && -n "${QUEUE_ARN:-}" ]]; then
  simulate_role "$QUEUE_CONSUMER_ROLE_ARN" "$QUEUE_ARN" sqs:GetQueueAttributes sqs:ReceiveMessage sqs:DeleteMessage sqs:ChangeMessageVisibility
else
  warn "queue-consumer IAM simulation skipped; set VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN to validate policy bindings"
fi

if [[ "$FAILURES" -gt 0 ]]; then
  printf 'AWS execution deployment preflight failed with %d violation(s).\n' "$FAILURES" >&2
  exit 1
fi

printf 'AWS execution deployment preflight passed.\n'
