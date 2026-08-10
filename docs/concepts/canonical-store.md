# CanonicalStore

CanonicalStore is Vyral's strong, tenant-scoped canonical-domain profile. Use it when a domain
change must atomically include the state change, its optimistic-concurrency condition, a durable
uniqueness or command fence, and an outbox command for asynchronous projections.

It is intentionally separate from `IRecordCollectionStore`: records optimize retrieval and RAG;
CanonicalStore owns authoritative domain state. A provider may implement either, both, or neither.
It must not claim `ICanonicalStore` unless it can provide every invariant below.

## Contract

`CommitAsync(CanonicalTransactionRequest)` is the only operation that changes canonical documents,
fences, and writes new outbox events. It performs all of the following in one provider transaction:

1. Upsert or tombstone up to 100 documents with `mustExist`, `mustNotExist`, or exact-revision
   preconditions.
2. Claim or release tenant-scoped named fences. A claim may be repeated by its owner but cannot
   silently move to another document.
3. Write up to 100 outbox events.
4. Persist a tenant-scoped idempotency receipt.

An identical resubmission returns the original transaction result with `replayed: true`; reusing
the idempotency key for a different request is rejected. Failed commits leave no document,
revision, fence, outbox event, or receipt behind.

`correlationId` and `actor` are retained in the transaction result and idempotency receipt, giving
consumers a portable audit/tracing join key without coupling CanonicalStore to a specific identity
provider. When CanonicalAccess is enabled, the server overwrites `actor` with the verified workload
principal rather than trusting a request body value.

Documents retain immutable revisions. Deletes retain a tombstone, which prevents a stale writer
from treating a deleted id as a new document. `Indexes` is an explicit map of consumer-projected
indexes—not an implicit extraction of arbitrary JSON paths—so index evolution remains visible and
portable. Queries support equality predicates plus one lexicographic range and stable ascending or
descending order over a projected index. Consumers encode numeric and temporal values in sortable
string form (for example fixed-width numbers and UTC ISO-8601 strings); Vyral deliberately does
not invent provider-specific JSON query semantics.

Outbox lease tokens are opaque credentials. Put them only in request bodies, never a URL, trace,
or application log. A worker can renew an active lease without creating another delivery. A repeated
acknowledgement with the same token is safe after a lost response. Expired leases become eligible
for another consumer.

Every release gets a five-second retry delay unless the caller supplies `notBeforeUtc` or
`retryAfterSeconds`; this prevents a failing worker from hot-looping. An outbox write may set
`maxDeliveryAttempts`; the event then moves to the durable dead-letter state at that ceiling.
Dispatch identities can inspect outbox state and explicitly replay a dead-lettered event. Backoff,
retry ceilings, and replay remain transparent consumer policy rather than cloud-queue behavior.

`ApplyMigrationsAsync` is a durable **namespace + migration-id** + checksum ledger. Each consumer
must use a stable, explicit migration namespace (such as `consumer-domain`) so a shared CanonicalStore
cannot collide unrelated migration identifiers. It protects a deployment from silently applying
different code under the same migration id; it does not execute consumer SQL or rewrite consumer
JSON. `ExportTenantAsync` and `RestoreTenantAsync` provide a complete tenant snapshot (documents,
revisions, fences, outbox state, and idempotency receipts). Restore verifies a content hash and
replaces that tenant atomically; it serializes concurrent tenant writers and clears ephemeral leases
so a restored outbox can be safely redelivered. The supplied snapshot object is never changed by
restore. Snapshots have a portable 64 MiB canonical-content limit, preventing an unbounded HTTP
export/restore from exhausting a shared service. The bounded snapshot is a convenience and recovery
surface, not a substitute for an archive system. `ExportTenantArchiveAsync` and
`RestoreTenantArchiveAsync` provide the large-tenant manifest-and-chunks profile: one tenant-wide
snapshot boundary, per-chunk hashes, an overall content hash, and atomic restore without forcing a
consumer to split a tenant. Archive chunks default to 8 MiB and are capped at 16 MiB; the profile is
intended for direct-store and offline backup tooling, not an unbounded shared HTTP response.

`tenantId` is the global data partition within one CanonicalStore deployment, not merely a display
label. Shared services should use consumer-qualified ids such as `tenant-prod` and grant those
exact ids through CanonicalAccess policies; a separate migration namespace does not merge or split
tenant data.

## HTTP example

```bash
curl -X POST http://127.0.0.1:5220/canonical/tenants/tenant-a/transactions \
  -H 'Content-Type: application/json' \
  -d '{
    "tenantId":"tenant-a",
    "idempotencyKey":"review:claim-42:approve:v1",
    "mutations":[{
      "operation":"upsert",
      "document":{
        "tenantId":"tenant-a",
        "documentType":"claim",
        "id":"claim-42",
        "schemaVersion":"v3",
        "data":{"status":"approved","reviewId":"review-8"},
        "indexes":{"status":"approved","reviewId":"review-8"}
      },
      "precondition":{"expectedRevision":6,"mustExist":true}
    }],
    "fences":[{
      "operation":"claim",
      "name":"approved-claim",
      "value":"claim-42",
      "ownerDocumentType":"claim",
      "ownerDocumentId":"claim-42"
    }],
    "outbox":[{
      "topic":"canon.claim.approved",
      "key":"claim-42",
      "payload":{"claimId":"claim-42","revision":7}
    }]
  }'
```

The caller can lease the event with `POST /canonical/tenants/tenant-a/outbox/leases`, renew it at
`.../outbox/{eventId}/renew` while the projection is running, then send the returned token in the
JSON body to `.../outbox/{eventId}/ack` after its projection succeeds. Use
`.../outbox/query` to inspect ready, leased, scheduled, delivered, or dead-lettered events, and
`.../outbox/{eventId}/replay` for an explicit dead-letter recovery.

Tenant ids and caller-supplied outbox event ids are single HTTP path segments; `/`, `\\`, `%`, `?`,
and `#` are rejected. For an exact read, clients use `POST /canonical/tenants/{tenantId}/documents/read` with
`tenantId`, `documentType`, and `id` in the body; revisions use the matching
`POST .../documents/revisions` endpoint. These body forms preserve opaque document identities that
contain path separators. The GET document and revision paths remain convenient shortcuts for
route-safe identities.

## Provider profile

The local `SqliteCanonicalStore` is a single-node reference implementation. It uses one SQLite
transaction for each canonical commit and is suitable for development and a single-node deployment.

`PostgresCanonicalStore` is the remote relational implementation. It uses PostgreSQL transactions,
row locks for existing documents/fences, advisory locks for idempotency contention, and `FOR UPDATE
SKIP LOCKED` for concurrent outbox leasing. It is intended for Cloud SQL for PostgreSQL and similar
managed PostgreSQL services. Its schema uses ordinary relational primitives and JSON text, not
Cloud SQL- or Google-only APIs.

`MySqlCanonicalStore` is the MySQL 8/InnoDB implementation, suitable for Cloud SQL for MySQL. It
locks one durable tenant-state row for each tenant mutation, so documents, fences, outbox changes,
idempotency receipts, and tenant restore are serialized only within that tenant and committed by one
InnoDB transaction. This is an intentionally correctness-first provider: projected-index queries
are evaluated over that locked tenant state rather than relying on MySQL-specific JSON query syntax.
It therefore has the same portable semantics, but deployments with very large, highly concurrent
tenants should run the checked-in
[`CanonicalStore MySQL contention benchmark`](../../tools/Vyral.CanonicalStore.Benchmarks/README.md).
The redacted report compares one hot tenant with tenant-sharded writes and idempotent replays, then
applies explicit p95, throughput, failure, and contention-ratio budgets. A budget failure directs
the operator to evaluate PostgreSQL's more granular relational layout or a dedicated projection;
it never promotes another provider without that provider's own workload evidence. The
[`retained local MySQL 8.0.46 receipt`](../../benchmarks/canonical-store/README.md) missed the starter
hot-tenant p95 and throughput budgets and is deliberately scoped to its recorded machine/workload;
operators must rerun it against their target topology. Configure
MySQL's `max_allowed_packet` and the connector command timeout for the largest tenant state or
archive being moved; individual canonical documents and ordinary snapshots retain their portable
limits.

The application enforces tenant isolation; PostgreSQL does not automatically install row-level
security policies because their role model is deployment-specific. Grant the CanonicalStore database
credential only to Vyral, keep the database on private/TLS connectivity, and add database RLS or a
dedicated schema/database when other applications hold database credentials.

The PostgreSQL implementation carries the same executable conformance suite as SQLite. It is an
opt-in local database gate—no hosted database is contacted by default—and creates a unique schema
per test:

```bash
VYRAL_PGVECTOR_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=vyral_test;Username=postgres;Password=postgres' \
dotnet test tests/Vyral.Tests.Pgvector/Vyral.Tests.Pgvector.csproj \
  --filter FullyQualifiedName~PostgresCanonicalStoreConformanceTests
```

On a development host with MySQL installed, the native runner starts a passwordless server only on
a validated loopback port, runs the full MySQL CanonicalStore suite (including the reversible
data-plane preflight), and removes its isolated `mktemp` state:

```bash
scripts/run-canonical-mysql-conformance-native.sh
```

For an isolated managed build worker with an ephemeral PostgreSQL 16 container, run the checked-in
gate (it does not contact a production CanonicalStore):

```bash
gcloud builds submit --config deploy/canonical-cutover/cloudbuild-postgres.yaml --project YOUR_TEST_PROJECT
```

The matching isolated MySQL 8.4/InnoDB gate is checked in as well:

```bash
gcloud builds submit --config deploy/canonical-cutover/cloudbuild-mysql.yaml --project YOUR_TEST_PROJECT
```

For local operator workflows, the CLI provides hash-verified tenant backup and restore:

```bash
dotnet run --project src/Vyral.Cli -- canonical-export \
  --db .vyral/vyral.sqlite --tenant tenant-a --file tenant-a.canonical.json
dotnet run --project src/Vyral.Cli -- canonical-restore \
  --db .vyral/vyral.sqlite --tenant tenant-a --file tenant-a.canonical.json \
  --expected-content-hash sha256:...
```

For a tenant that exceeds the 64 MiB JSON snapshot limit, use the archive commands. They write a
small `manifest.json` plus numbered binary chunks; copying all files together is required. Restore
verifies the manifest, every chunk, the archive hash, and the contained snapshot hash before it
replaces the tenant atomically:

```bash
dotnet run --project src/Vyral.Cli -- canonical-archive-export \
  --db .vyral/vyral.sqlite --tenant tenant-prod --directory tenant-prod.archive
dotnet run --project src/Vyral.Cli -- canonical-archive-restore \
  --db .vyral/vyral.sqlite --tenant tenant-prod --directory tenant-prod.archive \
  --expected-content-hash sha256:...
```

By default those commands use `.vyral/vyral.canonical.sqlite`; use `--canonical-db` to point at
another local CanonicalStore database. `canonical-migrate --file migrations.json` records a JSON
array of migration namespace/id/checksum receipts, for example
`[{"namespace":"consumer-domain","id":"20260712.canon-v1","checksum":"sha256:..."}]`.

No remote database is contacted merely by constructing the store. Choose it in the server with:

```text
CanonicalStore:Provider=postgresql
CanonicalStore:ConnectionString=<Npgsql connection string>
```

For Cloud SQL for MySQL, configure the same server surface with MySqlConnector syntax:

```text
CanonicalStore:Provider=mysql
CanonicalStore:ConnectionString=Server=/cloudsql/PROJECT:REGION:INSTANCE;Database=vyral;User ID=vyral;Password=...;ConnectionProtocol=UnixSocket
```

### MySQL-to-PostgreSQL upgrade path

CanonicalStore data is portable across providers. A consumer can use Cloud SQL MySQL today and
move a tenant to PostgreSQL later without interpreting Vyral's internal MySQL table: export a
hash-verified `CanonicalTenantArchive` from `MySqlCanonicalStore`, verify and copy its manifest and
chunks, then call `RestoreTenantArchiveAsync` on `PostgresCanonicalStore`. Restore verifies the
archive, its chunks, and the contained snapshot before replacing that tenant atomically. Apply the
consumer's namespaced migration ledger and perform the consumer-owned Cloud SQL gate before cutover.

The migration namespace, tenant id, document schemas, projected indexes, fences, and outbox topics
remain unchanged. Only the provider connection and operational characteristics change.

Vyral includes a disposable operator gate for this exact path:

```bash
deploy/canonical-cutover/run.sh
```

It starts isolated MySQL 8.4 and PostgreSQL 16 containers, seeds a tenant with a document,
revision, uniqueness fence, idempotency receipt, and active outbox lease, verifies every archive
chunk and hash, restores a fresh PostgreSQL schema, checks that ephemeral leases were cleared,
replays the original idempotent commit, rejects a conflicting fence claim, deploys the same
namespaced migration receipt, rebuilds a relational projection, and applies a post-cutover update
through the target outbox. Operators with their own disposable databases can instead set both
connection strings and run `scripts/validate-canonical-mysql-postgres-cutover.sh`. The gate creates
and drops databases/schemas, so never point it at production infrastructure.

## Imports larger than one transaction

The 100-mutation ceiling is deliberate: every portable provider must be able to atomically validate,
write, and recover the whole transaction. A source import with hundreds or thousands of documents
therefore uses a **staged visibility manifest**, not a weakened oversized transaction.

For one immutable source version `source-17@v4`, choose an import id such as
`source-17-v4-3f2b` and use it in every staged document's explicit indexes:

```json
{
  "documentType": "source-passage",
  "id": "source-17:v4:0182",
  "schemaVersion": "v1",
  "data": { "importId": "source-17-v4-3f2b", "sourceId": "source-17", "version": 4 },
  "indexes": {
    "importId": "source-17-v4-3f2b",
    "sourceId": "source-17",
    "sourceVersion": "0000000004"
  }
}
```

1. Split the staged source documents into deterministic chunks of at most 100 mutations. Each
   chunk gets a stable idempotency key such as `import:source-17:v4:chunk:0002`; it writes only
   data that the consumer's reader policy treats as unreachable without a publish manifest. Record
   the source count and a consumer-computed source hash in the final manifest payload.
2. Retry a chunk only with its original idempotency key and identical content. A changed chunk is a
   new import id/version, never a reuse of an old key.
3. In one final CanonicalStore transaction, upsert a `source-version` manifest with
   `status: "published"`, claim a durable `(source-id, version)` fence, and emit one
   `canon.source.published` outbox event containing the import id, expected count, and source hash.
   The final transaction is the sole visibility boundary.
4. Readers first resolve a published `source-version` manifest and then query only documents with
   its `importId`. Projections consume the publish outbox event and treat the declared count/hash as
   their completeness fence. They never discover staged documents by scanning all passages.
5. A failed or abandoned import has no publish manifest and is invisible. A maintenance job may
   tombstone its documents in bounded chunks after a consumer-defined grace period. Do not release
   the published `(source-id, version)` fence during ordinary cleanup; create a new version for a
   replacement import.

The same pattern covers a consumer's domain rows, review rows, and derived projection inputs: only
the manifest, its fence, and its publish outbox message require a cross-record atomic boundary.

## Aggregate and projection boundary

CanonicalStore is an aggregate-write substrate, not a portable relational-query engine. Model a
canonical commit around the decision or source-version aggregate that must be correct together: its
document updates, optimistic revision checks, uniqueness/command fence, and outbox event belong in
one transaction. Do not translate a join-heavy table graph into one canonical transaction or expect
CanonicalStore to execute joins, temporal window queries, or unbounded relationship traversal.

Use the transactional outbox to build the views that need those shapes: a relational QA/reporting
projection, a temporal index, and a bounded graph projection can each consume the same published
aggregate version. The canonical document revision history remains the authoritative change record;
projection stores are disposable/rebuildable consumers of the source-version manifest and its
outbox events. This is how a consumer can move by aggregate while preserving relational and
graph-oriented read models.

[`samples/Vyral.CanonicalProjectionStarter`](../../samples/Vyral.CanonicalProjectionStarter/README.md)
is the checked-in relational starter. Its SQLite read model transactionally commits each projected
mutation with the outbox event-id checkpoint, then acknowledges the CanonicalStore lease. A full
rebuild hash-verifies the authoritative tenant snapshot, replaces the tenant read model, records a
snapshot-content-hash fence, and checkpoints every outbox event already represented by that
snapshot. This makes restored ready events safe duplicates after a provider cutover. Projection
failures use a fixed nack diagnostic and remain visible through CanonicalStore's existing retry,
dead-letter, and explicit replay surfaces. CanonicalStore leasing is tenant-wide, so multi-topic
applications must use one dispatcher that routes a lease to the appropriate projection handler;
independent topic consumers must not race on the same tenant queue.

Run a consumer-owned isolated integration gate before production rollout. It should create a fresh
database/schema and verify the deployment's own IAM, private connectivity/TLS, backup/restore
policy, namespaced migration deployment, lease renewal and recovery, dead-letter replay, and
outbox consumer behavior. Those operational controls are intentionally outside the portable storage
contract.

## Tenant authorization

Outside `Development`, the server refuses to start CanonicalStore routes without
`Server:CanonicalAccess:IdentityPolicies`. This is intentionally fail-closed: a generic API key
cannot express tenant isolation. Every policy must list tenants; `*` is the explicit all-tenant
grant and cannot be combined with named tenants. The global `admin` operation requires its own
`*` policy. Tenant restore requires both tenant-scoped `restore` and global `admin`, preventing a
routine writer/dispatcher from replacing a whole tenant.

An execution-only deployment can explicitly set `CanonicalStore__Enabled=false`. This is a
feature-surface opt-out, not an authorization bypass: Vyral does not construct the canonical
provider, register canonical services, or map `/canonical` routes. The default remains enabled,
and an enabled production CanonicalStore still requires explicit tenant policies. The shared
Google execution example and its preflight require this setting; a deployment that needs both
surfaces must set it to `true` and supply canonical identity policy separately.

A shared CanonicalStore service configures an explicit tenant policy, for example:

```text
Server:CanonicalAccess:AuthenticationMode=google-oidc
Server:CanonicalAccess:AllowedAudiences:0=https://vyral.example.com
Server:CanonicalAccess:IdentityPolicies:0:Principal=canonical-worker@your-gcp-project.iam.gserviceaccount.com
Server:CanonicalAccess:IdentityPolicies:0:AllowedTenantIds:0=tenant-prod
Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:0=read
Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:1=write
Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:2=dispatch
```

Canonical HTTP routes are also fixed-window rate limited per observed source address before storage
is opened (120 requests/minute by default, no queue). Operators tune
`Server:CanonicalRateLimit:PermitLimit`, `WindowSeconds`, and `QueueLimit` for their ingress and
workload. The preflight response reports the effective limit without exposing credentials.

If the server also uses its generic API-key middleware, send the Vyral API key in
`X-Vyral-Api-Key` and the workload OIDC token in `X-Serverless-Authorization`. This keeps the
two credential types separate; putting a Google ID token in `Authorization` alone is interpreted
as an API-key attempt before CanonicalStore identity authorization runs.

The mapping is intentionally authentication-provider-neutral. Implement
`ICanonicalIdentityAuthenticator` with a public parameterless constructor and configure its type as
`Server:CanonicalAccess:AuthenticatorType` to use Azure Entra, AWS IAM/mTLS, or a platform-specific
mechanism. The authenticator returns a stable principal; Vyral applies the same tenant/operation
policy after that.

An administrator can read the passive deployed preflight from the CLI before rollout. It verifies
the configured CanonicalStore provider, enforced tenant-policy boundary, migration ledger
reachability, and required operations without writing probe data or printing secrets:

```bash
dotnet run --project src/Vyral.Cli -- canonical-preflight \
  --server https://vyral.example.com \
  --identity-token "$VYRAL_ID_TOKEN"
```

Add `--probe` for the explicit data-plane gate:

```bash
dotnet run --project src/Vyral.Cli -- canonical-preflight \
  --server https://vyral.example.com \
  --identity-token "$VYRAL_ID_TOKEN" \
  --probe
```

The probe creates two random ephemeral tenants, commits independent document/fence/outbox state,
exports and independently verifies a chunked archive, mutates and restores only the first tenant,
proves the second tenant's content hash is unchanged, then restores both probe tenants to empty and
verifies cleanup. It is implemented once against `ICanonicalStore` and is part of the SQLite,
MySQL, and PostgreSQL conformance fixtures. The response contains fixed check ids/messages and
counts only—never probe tenant ids, payloads, hashes, connection details, or provider exception
messages. A probe or cleanup failure returns HTTP 503. This verifies Vyral's portable archive and
tenant boundary; managed backup/PITR enablement, retention, and restore drills remain
operator-owned and must be checked separately.

## What remains consumer-owned

- Canonical document type and JSON schema, including field-level validation.
- Versioned transformations and the code associated with migration ids/checksums.
- Rich query models, joins, reporting, and consumer-specific secondary projections beyond the
  portable projected-index range/order profile.
- Retention and backup cadence beyond the portable export/restore operation.
- Domain decisions for fence names, idempotency-key construction, outbox topics, retry/backoff,
  and downstream delivery idempotency.

This boundary is deliberate: it supplies the shared correctness substrate without making every
consumer's domain model a Vyral platform feature.
