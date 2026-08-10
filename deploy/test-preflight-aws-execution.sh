#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat >"$WORK/aws-execution.env" <<'EOF'
ExecutionRuntime__Adapter=aws-dynamodb-sqs
ExecutionRuntime__Aws__Region=us-east-1
ExecutionRuntime__Aws__DynamoDbTableName=vyral-execution-test
ExecutionRuntime__Aws__SqsQueueUrl=https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution-test
ExecutionRuntime__Aws__Root=vyral-execution-test
ExecutionRuntime__Aws__CreateTableIfMissing=false
ExecutionRuntime__Aws__RequireExplicitWorkerRoutes=true
ExecutionRuntime__Aws__WorkerRoutes__0__HandlerId=product.example.job
ExecutionRuntime__ExternalHandlers__0__HandlerId=product.example.job
EOF

mkdir "$WORK/bin"
cat >"$WORK/bin/aws" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
arguments=" $* "
if [[ "$arguments" == *" sts get-caller-identity "* ]]; then
  printf '%s\n' 'arn:aws:iam::123456789012:role/deployer'
elif [[ "$arguments" == *" dynamodb describe-table "* ]]; then
  printf '%s\n' '{"Table":{"TableStatus":"ACTIVE","TableArn":"arn:aws:dynamodb:us-east-1:123456789012:table/vyral-execution-test","KeySchema":[{"AttributeName":"pk","KeyType":"HASH"},{"AttributeName":"sk","KeyType":"RANGE"}],"GlobalSecondaryIndexes":[{"IndexName":"vyral_execution_work","Projection":{"ProjectionType":"ALL"},"KeySchema":[{"AttributeName":"gsi1pk","KeyType":"HASH"},{"AttributeName":"gsi1sk","KeyType":"RANGE"}]}]}}'
elif [[ "$arguments" == *" dynamodb describe-continuous-backups "* ]]; then
  printf '%s\n' '{"ContinuousBackupsDescription":{"PointInTimeRecoveryDescription":{"PointInTimeRecoveryStatus":"ENABLED"}}}'
elif [[ "$arguments" == *" sqs get-queue-attributes "* ]]; then
  printf '%s\n' '{"Attributes":{"QueueArn":"arn:aws:sqs:us-east-1:123456789012:vyral-execution-test","VisibilityTimeout":"120","RedrivePolicy":"{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:123456789012:vyral-execution-test-dlq\"}","SqsManagedSseEnabled":"true"}}'
elif [[ "$arguments" == *" iam simulate-principal-policy "* ]]; then
  printf '%s\n' '{"EvaluationResults":[{"EvalDecision":"allowed"}]}'
else
  printf 'unexpected AWS preflight command: %s\n' "$*" >&2
  exit 90
fi
EOF
chmod +x "$WORK/bin/aws"

output="$(PATH="$WORK/bin:$PATH" \
  VYRAL_AWS_EXECUTION_DYNAMODB_TABLE=vyral-execution-test \
  VYRAL_AWS_EXECUTION_SQS_QUEUE_URL=https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution-test \
  VYRAL_AWS_EXECUTION_DYNAMODB_ROOT=vyral-execution-test \
  VYRAL_AWS_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_AWS_EXECUTION_CONFIG_FILE="$WORK/aws-execution.env" \
  VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN=arn:aws:iam::123456789012:role/vyral-runtime \
  VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN=arn:aws:iam::123456789012:role/product-worker \
  "$ROOT/deploy/preflight-aws-execution.sh")"

if [[ "$output" != *"AWS execution deployment preflight passed."* ]]; then
  printf 'expected AWS preflight to pass, output follows:\n%s\n' "$output" >&2
  exit 1
fi

sed '/^ExecutionRuntime__Aws__Region=/d' "$WORK/aws-execution.env" >"$WORK/aws-execution-no-region.env"
if PATH="$WORK/bin:$PATH" \
  VYRAL_AWS_EXECUTION_DYNAMODB_TABLE=vyral-execution-test \
  VYRAL_AWS_EXECUTION_SQS_QUEUE_URL=https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution-test \
  VYRAL_AWS_EXECUTION_DYNAMODB_ROOT=vyral-execution-test \
  VYRAL_AWS_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_AWS_EXECUTION_CONFIG_FILE="$WORK/aws-execution-no-region.env" \
  "$ROOT/deploy/preflight-aws-execution.sh" >"$WORK/missing-region.txt" 2>&1; then
  printf 'expected AWS preflight to reject a missing explicit region\n' >&2
  exit 1
fi
grep -q 'ExecutionRuntime__Aws__Region must be configured explicitly' "$WORK/missing-region.txt"

sed -i 's/ExecutionRuntime__Aws__RequireExplicitWorkerRoutes=true/ExecutionRuntime__Aws__RequireExplicitWorkerRoutes=false/' "$WORK/aws-execution.env"
if PATH="$WORK/bin:$PATH" \
  VYRAL_AWS_EXECUTION_DYNAMODB_TABLE=vyral-execution-test \
  VYRAL_AWS_EXECUTION_SQS_QUEUE_URL=https://sqs.us-east-1.amazonaws.com/123456789012/vyral-execution-test \
  VYRAL_AWS_EXECUTION_DYNAMODB_ROOT=vyral-execution-test \
  VYRAL_AWS_EXECUTION_HANDLER_IDS=product.example.job \
  VYRAL_AWS_EXECUTION_CONFIG_FILE="$WORK/aws-execution.env" \
  "$ROOT/deploy/preflight-aws-execution.sh" >"$WORK/failure.txt" 2>&1; then
  printf 'expected AWS preflight to reject disabled explicit worker routes\n' >&2
  exit 1
fi
if ! grep -q 'RequireExplicitWorkerRoutes must be true' "$WORK/failure.txt"; then
  printf 'expected explicit-route failure, output follows:\n' >&2
  sed -n '1,200p' "$WORK/failure.txt" >&2
  exit 1
fi

printf 'aws-execution-preflight-test=ok\n'
