# Vyral.Azure

`Vyral.Azure` maps the provider-neutral Vyral record and object-store contracts to Azure Cosmos
DB for NoSQL and Azure Blob Storage. Application and plugin code should continue to depend on
`Vyral.Abstractions`; Azure client construction, credentials, account topology, and operations stay
at the composition boundary.

## Construction and Credentials

The adapters receive Azure SDK clients, so a host can use a connection string for isolated tests or
Microsoft Entra ID in deployed environments:

```csharp
var credential = new DefaultAzureCredential();
var records = new CosmosRecordCollectionStore(
    new CosmosClient(new Uri(cosmosEndpoint), credential),
    databaseId);
var objects = new AzureBlobObjectStore(
    new BlobServiceClient(new Uri(blobEndpoint), credential));
```

Do not place account keys or connection strings in source, package configuration, or Vyral record
metadata. Grant the deployed identity only the data-plane roles needed by its adapter and use
separate test resources from production data.

## Cosmos DB Record Collections

`CosmosRecordCollectionStore` creates one Cosmos container per Vyral collection with the portable
`/partitionKey` partition key. Vector policies map to Cosmos vector-embedding and vector-index
policies. A Cosmos account that will create vector collections must enable `EnableNoSQLVectorSearch`
*before* the collection is created; capability changes can take time to reach the data plane.

```bash
az cosmosdb update \
  --resource-group <resource-group> \
  --name <cosmos-account> \
  --capabilities EnableServerless EnableNoSQLVectorSearch
```

Preserve any capabilities already required by the account when running that command. The isolated
live gate uses a serverless account, but the adapter does not require serverless throughput.

The adapter supports portable metadata filters, ordering, continuation tokens, vector search, and
single-record or per-item batch write preconditions. It uses Cosmos `_etag` conditions to make
portable `If-Match`, `If-None-Match`, `expectedEtag`, and `expectedRevision` writes atomic. Vyral
continues to expose its provider-neutral `rev:<n>` ETag; the Cosmos `_etag` remains internal.

Cosmos calls the function `VectorDistance`, but its cosine and dot-product values are already
similarity scores (higher is better); Euclidean remains a distance. The adapter turns all forms
into Vyral's portable higher-is-better score before applying `minScore`:

- cosine: unchanged
- dot product: unchanged
- euclidean: `1 / (1 + distance)`

Cosmos's vector `ORDER BY` pipeline does not support server continuation tokens. The adapter pages
the bounded `vector.top` candidate set with a Vyral-owned continuation token, so vector paging is
portable but not a cross-request snapshot while writes are concurrent.

Lexical ranking is not supplied by this adapter. `indexedMetadata` remains a portable policy and
query-contract declaration, but this adapter presently uses Cosmos's default indexing policy rather
than translating it into a custom cost/index policy. Operators should define custom Cosmos indexing
policy separately if workload cost or write/index tradeoffs require it.

## Blob Objects

`AzureBlobObjectStore` stores the portable SHA-256 content hash in reserved blob metadata and hides
that implementation key from callers. It supports conditional writes/deletes, content type,
metadata, and paged prefix listing. Container lifecycle is operator-owned: create the intended
container with the required access policy before using the adapter. The adapter does not create
containers implicitly during a write.

For a typical private production account, require HTTPS, TLS 1.2 or newer, and disable anonymous
blob access. Private endpoints or firewall rules should replace the public-network setting used by
an isolated developer test account.

## Durable Execution State

`AzureCosmosExecutionStatusStore` is the persistent status-store composition for
`Vyral.Execution.AzureDurable`:

```csharp
var state = new AzureCosmosExecutionStatusStore(
    new CosmosClient(cosmosConnectionString),
    databaseId,
    "vyral-execution-status");
var host = new AzureDurableExecutionHost(options, registry, state);
```

The database must already exist; the store creates its dedicated container with `/partitionKey` on
first use. It keeps each run's records together, atomically reserves a run id with Cosmos create,
uses ETag conditions for named leases and run replacement, and fences stale activity writes once a
run is terminal or cancellation has been persisted. Durable waits live in the same run partition:
registration transitions a running run to `waiting` with the wait document (and timer where
applicable), while waking atomically stores the outcome, removes the wait, and returns the run to
`queued`. Run list predicates and active-run counts are
evaluated by Cosmos against projected run fields; tag filters retain a compatibility fallback for
documents written before tag projection. It is not itself an Azure Functions host. Use the
replay-safe `Vyral.Execution.AzureDurable.Functions` bridge and the deployable
`samples/Vyral.Execution.AzureDurableFunctionsSmoke` composition: the orchestrator calls start
and step activities, so this store's Cosmos I/O never runs inside the replayed orchestrator.

## Live Conformance

The Azure test project creates unique blob containers and Cosmos containers under a caller-provided
test root, then cleans those containers up. It never creates an account or prints credentials.

```bash
export VYRAL_AZURE_BLOB_CONNECTION_STRING='<private value>'
export VYRAL_AZURE_BLOB_CONTAINER_PREFIX='vyral-it'
export VYRAL_AZURE_COSMOS_CONNECTION_STRING='<private value>'
export VYRAL_AZURE_COSMOS_DATABASE='vyral-integration'
export VYRAL_AZURE_COSMOS_CONTAINER_PREFIX='vyral-it'

dotnet test tests/Vyral.Tests.Azure/Vyral.Tests.Azure.csproj --no-restore --nologo
```

When using a POSIX env file, load it with `. /path/to/env-file` rather than relying on `source`,
which is not defined by every CI shell.
