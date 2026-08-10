# Azure Durable Functions Smoke Host

This sample is the concrete host composition for the Azure Durable Functions adapter. It is an
isolated, disposable integration gate, not an application template: it registers one handler that
writes a trace event, checkpoint, and artifact before succeeding.

The orchestration function calls only Durable Task APIs. The start and step activities own Cosmos
state transitions and handler execution, preserving the Durable Functions replay constraint.

## Required settings

Copy `local.settings.sample.json` to `local.settings.json` for local use. A deployed app needs:

- `AzureWebJobsStorage` — the Azure Storage connection string used by the Durable task hub.
- `VYRAL_AZURE_DURABLE_TASK_HUB` — a unique task-hub name for this isolated host.
- `VYRAL_AZURE_COSMOS_CONNECTION_STRING` and `VYRAL_AZURE_COSMOS_DATABASE`.
- `VYRAL_AZURE_DURABLE_STATUS_CONTAINER` — a disposable Cosmos container name. The database must
  exist; the container is created on first use with `/partitionKey` as its partition key.

Do not check connection strings, function keys, or `local.settings.json` into source control.

## Gate

Deploy to a disposable Function App, then invoke the function-authenticated endpoint. Keep the
function key out of URLs, which can be captured by access logs:

```bash
curl -X POST "$BASE_URL/api/vyral-smoke/runs" \
  -H "x-functions-key: $FUNCTION_KEY" \
  -H 'content-type: application/json' \
  --data '{"idempotencyKey":"azure-smoke-001","payload":{"source":"gate"}}'
```

Poll `GET $BASE_URL/api/vyral-smoke/runs/<run-id>` with the same `x-functions-key` header until
`status` is `succeeded`, then inspect the Cosmos status container for the run, its `smoke` checkpoint,
`smoke-result` artifact, and trace history. Repeat the identical POST once: its run id must be
unchanged and it must not schedule a duplicate orchestration.

The same gate can exercise a portable handler-side external-event wait. Start it with a future
timeout, poll until it is `waiting`, then post the named event; the handler is replayed and the
run completes:

```bash
curl -X POST "$BASE_URL/api/vyral-smoke/runs" \
  -H "x-functions-key: $FUNCTION_KEY" \
  -H 'content-type: application/json' \
  --data '{"idempotencyKey":"azure-wait-001","payload":{"waitForEvent":"approval","waitTimeoutAtUtc":"2030-01-01T00:00:00Z"}}'

curl -X POST "$BASE_URL/api/vyral-smoke/runs/<run-id>/events/approval" \
  -H "x-functions-key: $FUNCTION_KEY" \
  -H 'content-type: application/json' \
  --data '{"approved":true}'
```

For a timer wait, supply `payload.waitForTimerAtUtc` as a future ISO-8601 timestamp instead. The
sample deliberately checkpoints before either wait, demonstrating the required replay discipline.

To exercise that active-replay case, include a near-future ISO-8601 `scheduledAtUtc` value, repeat
the same request before it is due, then poll through successful completion. Durable task-hub
instance storage must contain one instance for the returned run id.

The repository includes a read-only assertion for that final state. It does not need a Function
key and never changes the deployed host:

```bash
VYRAL_AZURE_DURABLE_STATUS_CONTAINER='vyral-execution-smoke-...' \
VYRAL_AZURE_DURABLE_SMOKE_RUN_ID='<run-id>' \
dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj \
  --filter FullyQualifiedName~AzureDurableFunctionsSmokeLiveTests
```

For the external-event replay gate, set `VYRAL_AZURE_DURABLE_SMOKE_WAIT_RUN_ID` instead of the
ordinary run id. For the timer replay gate, use
`VYRAL_AZURE_DURABLE_SMOKE_TIMER_RUN_ID`. The read-only tests assert wait registration, the
provider wake, handler replay, and eventual completion.

For the complete disposable deployment, ordinary run-owned state, idempotency, external-event and
timer replay, and all three read-only Cosmos assertions in one command, use the repository gate.
It requires the ordinary Azure Cosmos live-test settings plus the resource group and Cosmos account
name; it never prints connection strings and deletes its temporary Function App, storage account,
and Cosmos container.

```bash
VYRAL_AZURE_LIVE_RESOURCE_GROUP='rg-for-isolated-tests' \
VYRAL_AZURE_LIVE_COSMOS_ACCOUNT='cosmos-account-name' \
scripts/validate-azure-durable-functions-live.sh
```

It also requires the normal `VYRAL_AZURE_COSMOS_CONNECTION_STRING` and
`VYRAL_AZURE_COSMOS_DATABASE` test settings. The repository gate supplies all three run-specific
identifiers; a manually invoked read-only assertion skips cleanly when its matching identifier is
absent.

Delete the disposable Function App and its temporary task-hub resources after the gate. The Cosmos
container is an independent disposable test root and should be deleted separately.
