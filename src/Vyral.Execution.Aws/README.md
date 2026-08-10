# Vyral.Execution.Aws

`Vyral.Execution.Aws` maps Vyral's provider-neutral external-worker execution contract onto a
queue-first AWS deployment:

- DynamoDB is the durable run, idempotency, lease, checkpoint, artifact metadata, event, wait,
  trace, and runnable-work store.
- SQS carries only a run id, dispatch reason, schedule marker, and dispatch id. It is explicitly
  at-least-once; workers must lease the run before observing mutable state.
- A worker can be a Lambda SQS consumer, a container polling SQS, ECS, EKS, or another AWS-hosted
  process. Its application protocol remains `IExternalExecutionWorkerRuntime`, not an AWS SDK.
- S3 can be supplied as the artifact object store through `Vyral.Aws.S3ObjectStore` when artifacts
  exceed the inline DynamoDB limit.

Run payloads and results are each capped at 128 KiB (with 32 KiB for status details) because all
three live in the same DynamoDB run item. Checkpoints and inline artifacts have a 256 KiB cap in
their own items; larger artifacts require an object store.

The state table has primary keys `pk` and `sk` (both strings) and a required GSI named
`vyral_execution_work` over `gsi1pk` and `gsi1sk` (both strings). The store creates an on-demand
table with that schema by default, or validates the GSI on an existing table. Production operators
should own IAM, encryption, backups/PITR, TTL/retention policy, alarms, and deployment topology.

The runtime identity needs DynamoDB `DescribeTable`, `GetItem`, `PutItem`, `DeleteItem`,
`UpdateItem`, `Query`, `BatchWriteItem`, and `TransactWriteItems` against its execution table
(plus `CreateTable` only when automatic provisioning is enabled). Its dispatcher needs
`sqs:SendMessage`; SQS-polling workers additionally need `ReceiveMessage`, `DeleteMessage`, and
`ChangeMessageVisibility`. Scope all permissions to the specific table, its index, and queue. No
AWS credentials, queue URLs, account identifiers, or state payloads are put in dispatch logs.

```csharp
var state = new DynamoDbExecutionStateStore(
    new AmazonDynamoDBClient(),
    new DynamoDbExecutionStateStoreOptions { TableName = "vyral-execution" });
var dispatch = new AwsSqsExecutionDispatcher(
    new AwsSqsExecutionQueue(new AmazonSQSClient()),
    new AwsSqsExecutionDispatchOptions { QueueUrl = "https://sqs.us-east-1.amazonaws.com/ACCOUNT/vyral-execution" });
var runtime = new AwsDynamoExecutionRuntimeAdapter(
    state,
    dispatch,
    new AwsDynamoExecutionRuntimeOptions
    {
        WorkerDispatchers =
        [
            new AwsDynamoExecutionWorkerDispatcher
            {
                HandlerId = "example.worker",
                Dispatcher = dispatch
            }
        ]
    });
```

SQS can delay a message only 900 seconds. The adapter stores longer waits/timers durably and
expects an operator-run `ReconcileDispatchAsync` maintenance sweep to enqueue them once due (or
within the SQS delay window). Run that sweep on a bounded, recurring schedule; it is part of the
AWS deployment contract, not an application-handler concern. Cancellation, event wake-up, timer
wake-up, and completion use DynamoDB conditional transactions: a stale worker completion cannot
undo a durable cancellation fence, and an event/timer race consumes just one durable wait.

For the server-hosted deployment shape, use `ExecutionRuntime:Adapter=aws-dynamodb-sqs` with an
explicit `ExecutionRuntime:Aws:Region`, `ExecutionRuntime:Aws:WorkerRoutes`, table name, queue
URL, durable root, and consumer-owned HTTP authorization/policies. The reference topology and a
read-only preflight are
in [the AWS execution deployment guide](https://github.com/univeracity/vyral/blob/main/deploy/aws-execution/README.md);
the preflight checks table/GSI, PITR, queue encryption/DLQ/visibility, explicit routes, and
optional least-privilege IAM roles.

For an isolated account-level deployment check, run:

```bash
scripts/validate-aws-execution-live.sh
```

The gate creates a temporary standard SQS queue and DynamoDB table, executes the shared
external-worker conformance cases plus a real queue smoke (leases, checkpoints, waits/events, and
completion), then removes both resources.
