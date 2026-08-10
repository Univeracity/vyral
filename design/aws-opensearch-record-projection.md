# AWS OpenSearch Record Projection

Status: preview, optional derived retrieval path
Last updated: 2026-07-25

## Purpose

`DynamoDbRecordCollectionStore` remains Vyral's AWS canonical record store.
Its portable query and search semantics remain correct, but vector search is a
scan and therefore unsuitable for a large corpus. `OpenSearchRecordSearchProjection`
is an optional scalable vector-candidate index; it does not change the
`IRecordCollectionStore` contract or make OpenSearch a source of truth.

```text
canonical Vyral write
  -> DynamoDB table (truth; revision increments)
  -> DynamoDB Stream, NEW_AND_OLD_IMAGES
  -> projection worker / Lambda
  -> OpenSearch derived document (external_gte revision fence)
  -> candidate identities + scores
  -> DynamoDB canonical hydration and revision check
  -> application result
```

At-least-once delivery and out-of-order stream records are expected. Every
projection write and delete uses the canonical `VyralRecord.Revision` as an
OpenSearch `external_gte` version. A late event therefore cannot overwrite—or
resurrect—a newer document. Hydration discards an index candidate when its
canonical record is gone or has a different revision.

An index delay can make a recent write absent from an eventual search result; it
cannot cause Vyral to return index-owned record content as canonical data.
Applications that require read-after-write search should query the canonical
store or wait for their projection checkpoint.

## Setup

1. Provision an OpenSearch domain or collection independently. Use a private
   endpoint, encryption at rest and in transit, a dedicated IAM role for the
   projection worker, and a separate query role if application topology needs
   one. The Vyral runtime itself need not have OpenSearch permissions.
2. Instantiate `AwsSigV4OpenSearchTransport` with the domain data-plane
   endpoint, region, and the worker's normal AWS credential chain. The managed
   OpenSearch Service signing name is `es`; serverless deployments may use their
   applicable signing name when constructing the transport.
3. Create `OpenSearchRecordSearchProjection` and call `EnsureCollectionAsync`
   during each projection-worker startup (and whenever a collection policy is
   introduced), or provide its canonical `IRecordCollectionStore` as the
   policy store. The projection fails closed rather than serializing a record
   until it knows that collection's policy. Mapping changes are immutable: make
   a fresh index and replay/rebuild rather than mutating a live mapping. The
   default name is stable for one mapping shape and remains valid for the
   longest portable collection name. A mapping change automatically selects a
   new immutable default generation; backfill it, switch readers, then retire
   the prior derived index. Configure `PolicyIndexNameFactory` when an operator
   needs a specific generation naming or alias convention. A worker using the
   default or policy-aware resolver needs the canonical policy for deletes as
   well as writes; only a deliberately configured legacy collection-only
   resolver can route deletes without it.
4. Enable `NEW_AND_OLD_IMAGES` on every canonical collection table's DynamoDB
   Stream. Configure `DynamoDbStreamsRecordProjectionConsumer` with a strict
   table-ARN-to-collection mapping. It intentionally fails a batch that lacks
   the required `doc` image, allowing the Lambda event source to retry or
   redrive instead of silently losing an index update.
5. Query with `SearchAndHydrateAsync(projection, canonicalStore, policy, query)`.
   The returned `HydratedRecordSearchProjectionResult` says `consistency:
   eventual` and reports stale candidates it discarded.

The projection document contains identity, revision, policy-declared vectors,
and scalar filter values only for paths explicitly declared in
`RecordCollectionPolicy.IndexedMetadata`. A declared object or array is stored
as a presence marker only, so `exists` remains usable without copying compound
data. It does not copy raw `content`, source references, or arbitrary metadata
into OpenSearch. This keeps the projection intentionally smaller than the
canonical document and avoids creating a second unrestricted content store.

## Supported preview shape

- Vector candidate retrieval for collection vector policies.
- Approximate HNSW/FAISS k-NN mappings, with the portable cosine, dot-product,
  and Euclidean distance policy names mapped to OpenSearch space types.
- `partitionKeys` plus `eq`, `neq`, `in`, `exists`, `contains`, and
  `startsWith` filters for `/partitionKey`, `/id`, `/type`, and fields declared
  in `RecordCollectionPolicy.IndexedMetadata`.
- Revision-fenced upserts and deletes from DynamoDB Streams.
- A configured, hard candidate bound. A request whose vector `top` or page
  size exceeds the bound fails explicitly; the projection never silently
  truncates the candidate pool and presents that as a complete result.

The following are intentionally not represented as portable projection
semantics yet: lexical or hybrid search, ordering, continuation tokens, range
predicates, and `minScore`. OpenSearch's approximate k-NN score scale is
provider-shaped, so a caller should make any score threshold after hydration.
The regular canonical `IRecordCollectionStore` remains available whenever the
full portable query shape is required.

## Security and operations

- Grant the projection worker only the OpenSearch HTTP permissions for its
  projection indices and the DynamoDB Stream read performed by its event source.
  Do not grant product callers direct OpenSearch access by default.
- Keep the OpenSearch endpoint in a private network boundary. The signed
  transport rejects credential-bearing endpoints and non-HTTPS endpoints except
  `localhost` test endpoints; it never includes response bodies or credentials
  in its exceptions.
- Monitor DynamoDB Stream iterator age, Lambda failures/DLQ, and OpenSearch
  indexing errors. Replay a stream or perform a controlled rebuild when lag
  exceeds the consumer's freshness objective.
- Treat a collection policy/mapping migration as an index migration: provision
  a new index, backfill from DynamoDB, verify a projection watermark, switch
  readers, then remove the old derived index. Canonical data is unaffected.
- Projection deletion is safe to repeat. Deleting an OpenSearch index never
  deletes a canonical Vyral collection.

## Local data-plane qualification

OpenSearch itself is open source, so the repository provides a credential-free
localhost gate for the contract-relevant data plane. It starts a caller-supplied
unpacked OpenSearch distribution with the security plugin disabled, creates and
removes one unique derived index, and runs the projection's mapping,
revision-fenced upsert/delete, vector/filter query, and cleanup scenario.

```bash
VYRAL_OPENSEARCH_HOME=/path/to/opensearch \
scripts/validate-opensearch-local.sh
```

The gate requires the distribution's k-NN native library to be available at
`plugins/opensearch-knn/lib`; set `VYRAL_OPENSEARCH_NATIVE_LIB_DIR` only when a
compatible distribution places it elsewhere. `VYRAL_OPENSEARCH_LOCAL_PORT` and
`VYRAL_OPENSEARCH_JAVA_OPTS` allow an isolated port and JVM sizing when needed.
It accepts only a loopback HTTP endpoint and uses placeholder local credentials;
it is not an AWS authentication test.

Passing this gate demonstrates that Vyral's derived-index wire shape is
compatible with that OpenSearch version. It does not demonstrate AWS SigV4
authorization, IAM permissions, VPC reachability, managed-service encryption,
or DynamoDB Streams/Lambda delivery. Those remain deployment-topology concerns
and are covered by the managed opt-in gate below.

## Opt-in managed AWS data-plane qualification

The repository provides `scripts/validate-aws-opensearch-live.sh` for a real
managed AWS OpenSearch data-plane check. It requires a deployment owner to provide a
reachable, disposable domain or collection endpoint; it creates a uniquely
named derived index and removes that index in the test's `finally` block. It
never creates or destroys the surrounding OpenSearch resource, network policy,
or IAM roles.

```bash
VYRAL_AWS_OPENSEARCH_ENDPOINT=https://your-opensearch-data-plane-endpoint \
VYRAL_AWS_LIVE_REGION=us-east-1 \
scripts/validate-aws-opensearch-live.sh
```

Set `VYRAL_AWS_OPENSEARCH_SIGNING_SERVICE=aoss` only for a serverless endpoint
that requires that signing name; managed domains use the default `es`. The gate
proves managed-endpoint SigV4 transport and authorization, index provisioning,
nested vector/filter mappings, revision-fenced writes, eventual candidate
search, and derived-index cleanup. It is deliberately narrower than a complete
Stream-to-application rehearsal:
the deployment owner must additionally verify DynamoDB Streams/Lambda delivery,
canonical hydration, VPC reachability, and the least-privilege IAM bindings in
the actual topology.
