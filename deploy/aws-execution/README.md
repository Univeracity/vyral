# Vyral AWS execution topology

This is a provider-specific deployment of Vyral's portable external-worker contract. DynamoDB is
the source of truth. SQS is only an at-least-once wake-up channel, so a consumer must never treat
an SQS message as authority to execute without leasing the run through Vyral first.

```text
Vyral server / execution host (runtime IAM role)
  ├─ DynamoDB execution table: runs, leases, events, waits, checkpoints, work GSI
  └─ SQS execution queue: immutable dispatch envelopes only
          ↓
Consumer-owned queue poller (queue-consumer role)
  └─ consumer worker → authenticated Vyral /execution/workers/leases* HTTP contract

Recurring maintenance caller → Vyral /execution/runtime/maintenance/reconcile
```

The worker can be Lambda, ECS, EKS, or a long-running container. A poller that calls the Vyral
HTTP worker protocol needs SQS receive/delete/visibility permissions; an HTTP-only worker behind
another poller does not. Vyral's runtime role needs only DynamoDB state access and `sqs:SendMessage`.
Do not share either role across unrelated consumers.

## Provision

The CloudFormation reference provisions the DynamoDB table (`pk`, `sk`, and the required
`vyral_execution_work` GSI), on-demand billing, SSE, point-in-time recovery, an encrypted standard
queue, DLQ, and two attachable least-privilege managed policies.

```bash
aws cloudformation deploy \
  --stack-name vyral-execution-production \
  --template-file deploy/aws-execution/cloudformation.yaml \
  --parameter-overrides \
    ExecutionTableName=vyral-execution-production \
    ExecutionQueueName=vyral-execution-production \
    QueueVisibilityTimeoutSeconds=120
```

Attach `RuntimePolicyArn` only to the Vyral execution host. Attach `QueueConsumerPolicyArn` only to
the component that receives SQS messages. Configure the server from
[`../aws-execution.env.example`](../aws-execution.env.example), using a distinct DynamoDB root per
environment, an explicit `ExecutionRuntime__Aws__Region` matching the queue/table region, and an
explicit route per handler. The host does not automatically create tables by default.

Set the SQS visibility timeout at least as high as the longest normal worker lease. A worker that
may run longer must heartbeat its Vyral lease and extend the queue visibility using its poller; no
queue visibility setting replaces the durable lease protocol.

## Maintenance and rollout

SQS cannot delay a message for more than 900 seconds. Run an authenticated, bounded recurring
call to `POST /execution/runtime/maintenance/reconcile`; it redrives durable timers once they are
due or within the SQS delay window. Also schedule maintenance pruning according to the product's
retention policy. These are operator responsibilities, not handler code.

Before directing consumer traffic, run the read-only preflight and isolated live gate:

```bash
VYRAL_AWS_EXECUTION_DYNAMODB_TABLE=vyral-execution-production \
VYRAL_AWS_EXECUTION_SQS_QUEUE_URL="$(aws cloudformation describe-stacks --stack-name vyral-execution-production --query 'Stacks[0].Outputs[?OutputKey==`SqsQueueUrl`].OutputValue' --output text)" \
VYRAL_AWS_EXECUTION_DYNAMODB_ROOT=vyral-execution-production \
VYRAL_AWS_EXECUTION_HANDLER_IDS=product.example.job \
VYRAL_AWS_EXECUTION_CONFIG_FILE=deploy/aws-execution.env \
VYRAL_AWS_EXECUTION_RUNTIME_ROLE_ARN=arn:aws:iam::ACCOUNT:role/vyral-runtime \
VYRAL_AWS_EXECUTION_QUEUE_CONSUMER_ROLE_ARN=arn:aws:iam::ACCOUNT:role/product-worker \
deploy/preflight-aws-execution.sh

scripts/validate-aws-execution-live.sh
```

The preflight is deliberately read-only. IAM simulation proves identity-policy decisions when the
caller has `iam:SimulatePrincipalPolicy`; it does not replace testing resource policies or the
consumer's HTTP authentication/authorization configuration. The live gate creates temporary
resources, exercises real DynamoDB and SQS, and cleans them up.
