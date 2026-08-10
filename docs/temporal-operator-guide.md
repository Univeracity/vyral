# Temporal adapter operator guide

This guide covers the prototype `Vyral.Execution.Temporal` adapter, its PostgreSQL projection,
worker host, and artifact object store. It is an operating contract, not a production-readiness
claim. The adapter remains `prototype_unqualified` until the required qualification receipts are
retained and reviewed.

## Authority and recovery boundary

Three durable systems participate in one Vyral execution topology:

| System | Authority | Backup owner |
| --- | --- | --- |
| Vyral PostgreSQL projection | Run identity and status, idempotency, history, checkpoints, artifact metadata, waits, leases, timers, and start/signal/cancellation outboxes | Vyral operator |
| Artifact object store | Offloaded artifact bodies referenced by projection metadata | Vyral operator |
| Temporal Service or Cloud namespace | Coordinator history, timers, signals, activity scheduling, and continue-as-new chains | Temporal service operator or Temporal Cloud |

Temporal visibility and retention do not replace the Vyral projection. A PostgreSQL backup does
not back up offloaded artifact bodies or the Temporal Service. A Temporal persistence backup does
not back up the Vyral projection or artifact bodies. Set one reviewed RPO and RTO for the complete
topology and rehearse all parts needed to meet it.

For self-hosted Temporal, use the official production checklist, persistence, visibility, schema
upgrade, and disaster-recovery guidance for the deployed Temporal version. Temporal documents
multi-cluster replication as its service-level failover mechanism; Vyral does not add a second
Temporal persistence recovery mechanism. See the [self-hosted Temporal guide](https://docs.temporal.io/self-hosted-guide).

## Production topology

- Use a dedicated Temporal namespace per approved environment and security/retention boundary.
  Do not share development and production namespaces.
- Use a stable, private task queue per compatible worker topology. A task queue is routing, not a
  tenant authorization boundary. Do not accept namespace, task queue, workflow type, endpoint, or
  worker identity from a run request.
- Keep `VYRAL_EXECUTION_TEMPORAL_ADAPTER_NAMESPACE` stable. It is part of the deterministic
  workflow id. Changing it while runs are active changes coordinator identity.
- Set `VYRAL_EXECUTION_TEMPORAL_WORKER_DEPLOYMENT_NAME` to one stable application deployment name
  without periods. Set `VYRAL_EXECUTION_TEMPORAL_WORKER_BUILD_ID` to an opaque immutable release
  identifier shared by all instances of the same worker code and configuration. If omitted, Vyral
  derives a build id from the coordinator and hosting package versions.
- Keep the PostgreSQL schema name stable for a topology and isolate its database role. The current
  projection schema version is `4`; this prototype has no upgrade path from older pre-release
  schemas and rejects a mismatched version.
- Run API clients and workers as separate deployments. Workers need the registered handler set and
  durable object-store access. The API does not need consumer handler assemblies.
- Compose both processes with `AddVyralTemporalExecution` after registering host-owned
  `ITemporalClient`, projection-store, and object-store services. Add portable plugins and
  `AddHostedWorker` only in the worker deployment. The hosted worker uses Temporal's official
  Generic Host lifecycle and runs the bounded aggregate outbox pump; do not add a second ad hoc
  reconciler loop in the same process.
- Use a durable, versioned object-store implementation in production. The sample file object store
  is only for local or disposable qualification use.
- Keep the API, worker, PostgreSQL, object-store, and Temporal endpoints private. Vyral remains the
  caller-facing authorization boundary; Temporal UI and direct API access are operator-only.

Changing the namespace or task queue is a migration, not a routine configuration rollout. Existing
workflow histories continue to need workers compatible with their original routing and code.

## TLS, authentication, and secrets

Remote Temporal and PostgreSQL connections must use TLS. The adapter accepts disabled TLS only for
loopback development endpoints. Prefer PostgreSQL `SSL Mode=VerifyFull` with reviewed trust roots;
use a weaker accepted mode only when the deployment's threat model explicitly requires it.

Temporal Cloud currently supports API keys and mTLS for namespace access. For API-key deployments,
prefer a namespace-scoped service account per client or worker deployment, inject its key from a
secret manager, rotate by overlapping old and new credentials, and remove the old key only after
connectivity is verified. For mTLS deployments, the host owns `TemporalClient` construction and
certificate lifecycle; the sample host is an API-key example, not a complete mTLS production host.
See Temporal's [Cloud access-control guidance](https://docs.temporal.io/best-practices/cloud-access-control),
[API-key guidance](https://docs.temporal.io/cloud/api-keys), and
[mTLS guidance](https://docs.temporal.io/cloud/certificates).

Use separate credentials where the provider supports useful separation:

- API client: only the namespace operations needed to start, describe, signal, and cancel the fixed
  coordinator.
- Worker: only the namespace and task-queue access needed to poll and complete workflow/activity
  tasks.
- Projection application role: only the configured Vyral projection schema.
- Projection backup role: read access sufficient for backup, without application mutation rights.
- Restore role: create an isolated target database/schema during rehearsals; do not grant that
  authority to the steady-state application identity.
- Object-store identity: only the approved Vyral artifact prefix and required object operations.

Never place a connection string, API key, certificate, private key, object-store credential,
payload, event body, checkpoint body, or artifact body in a command argument, status response,
trace, qualification receipt, or support bundle. The included status and preflight surfaces return
fixed codes, counts, and hashed topology metadata.

## Worker rollout and rollback

The current Vyral worker package does not configure Temporal Worker Versioning. Temporal recommends
Worker Versioning for new production deployments where the environment and SDK integration support
it; until Vyral integrates that feature, every worker release on a shared task queue must remain
replay-compatible with existing `Vyral.RunCoordinator.v1` histories. See Temporal's
[worker deployment guidance](https://docs.temporal.io/production-deployment/worker-deployments).
Vyral does send the configured deployment/build metadata with `UseWorkerVersioning=false`; this is
diagnostic metadata only and does not route or pin workflow tasks.

Use this rollout sequence:

1. Review workflow changes for determinism and retain every existing patch branch needed by open
   histories. Run the checked-in offline coordinator replay fixtures as part of the build; the
   pre-continue-as-new fixture must replay with the candidate worker. Ordinary handler code remains
   activity code and must preserve its portable contract.
2. Take or verify a current projection/object-store recovery point. Confirm the Temporal service's
   own recovery posture separately.
3. Deploy a small set of new workers alongside compatible old workers on the same queue.
4. Run preflight while a worker is active. Require healthy Temporal and projection checks, fresh
   workflow and activity pollers for the intended build, a successful object probe, and no
   unexplained coordinator consistency warning. The report exposes hashes and counts only.
5. Observe outbox counts, oldest pending age, active coordinator counts, activity failures, and
   worker poller health through at least one representative run and wait/resume path.
6. Scale the new workers up, then remove old workers only after their supported histories no longer
   require them.

Rollback means restoring the last replay-compatible worker image and configuration. Do not delete
projection outbox rows, reset delivered markers, terminate workflows, or change adapter namespace
to force progress. Keep older compatible binaries and patch branches for the longest open workflow
history, not merely for the deployment window. Vyral continues as new after 32 coordination
transitions (or an earlier Temporal server suggestion), but the logical workflow chain still
requires compatible workflow code. The qualification scenario drives 37 transitions and inspects
every exact Temporal run in the chain. It fails above 512 events or 1 MiB of encoded protobuf
history in any run, above eight runs in that scenario, on a broken chain, or when the final run is
not completed. These are conservative Vyral qualification guardrails rather than Temporal service
limits. Re-measure and review them whenever coordinator commands, activity envelopes, patch markers,
or rollover policy change.

## Routine readiness

The neutral sample exposes the same redacted checks expected from a production composition:

```bash
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- status
dotnet run --project samples/Vyral.Execution.TemporalSample/Vyral.Execution.TemporalSample.csproj -- preflight
```

Run preflight with an intended worker active. It checks configuration, Temporal workflow-service
and namespace access, fresh workflow/activity pollers, aggregate worker build/mode compatibility,
projection schema and backlog state, bounded coordinator consistency, and an isolated object
write/read/delete probe. It does not start a workflow. Missing pollers, stale coordinators,
incomplete coordinator inspection, or backlog age are explicit warnings and require operator
classification; they are not silently converted to success. `worker.mixed_builds_observed` is
expected during a controlled overlap but requires the operator to confirm every observed build is
replay-compatible. `worker.compatibility_metadata_missing` identifies legacy or foreign pollers
whose build cannot be attributed, and
`worker.unexpected_versioned_pollers` identifies routing-mode drift. The response never includes
the observed worker identity, deployment name, or build id.

Alert on trends rather than a single nonzero count:

- pending start, signal, or cancellation rows whose oldest age keeps increasing;
- active projection coordinators reported missing or closed by Temporal;
- no fresh workflow or activity pollers for the configured queue;
- repeated object probe, projection, authentication, or namespace failures;
- worker replay/nondeterminism failures or activity timeout growth.

## Projection and artifact backup

The simplest supported recovery point is quiesced. Stop new run/event/cancellation writes, stop all
outbox reconcilers, and drain or stop workers before the backup. Verify no application process can
write projection rows or artifact bodies during the backup window. Temporal coordinators can remain
durable and idle while workers are stopped.

Back up the projection and object store under the same change freeze:

1. Record the application version, Vyral adapter version, projection schema name, Temporal
   namespace/task queue identifiers in an access-controlled manifest, object-store prefix/version,
   cutoff time, and retention class. Do not put credentials in the manifest.
2. Create a PostgreSQL custom-format dump scoped to the configured projection schema.
3. Hash the completed dump and protect both files against mutation.
4. Snapshot or copy every object version reachable beneath the topology's artifact prefix. Record
   the provider-native snapshot/version id and an inventory hash when supported.
5. Resume writers only after both backups and the manifest are complete.

The following pattern keeps the password out of the process argument list. Provision the
`PGPASSFILE` through the deployment's secret mechanism; do not check it into source control.

```bash
umask 077
export PGHOST=projection-db.internal
export PGPORT=5432
export PGDATABASE=vyral
export PGUSER=vyral_projection_backup
export PGSSLMODE=verify-full
export PGSSLROOTCERT=/run/secrets/projection-db-ca.pem
export PGPASSFILE=/run/secrets/vyral-projection.pgpass
export VYRAL_TEMPORAL_POSTGRES_SCHEMA=vyral_temporal

backup_dir=/secure-backups/vyral-temporal/2026-07-28T120000Z
mkdir -p "$backup_dir"
dump="$backup_dir/projection.dump"
pg_dump --format=custom \
  --schema="$VYRAL_TEMPORAL_POSTGRES_SCHEMA" \
  --no-owner \
  --no-privileges \
  --file="$dump"
sha256sum "$dump" >"$dump.sha256"
chmod 600 "$dump" "$dump.sha256"
```

Do not infer artifact completeness from projection metadata alone. Inline artifact bodies are in
PostgreSQL, but offloaded bodies require the matching object-store recovery point. Backup cadence,
retention, encryption, geographic separation, and RPO/RTO are deployment-owned policies.

## Restore rehearsal

Always restore into a fresh, isolated database first. Never run a rehearsal over the active
projection. Keep API writers, workers, and outbox reconcilers disconnected from the restored copy.

```bash
dump=/secure-backups/vyral-temporal/2026-07-28T120000Z/projection.dump
sha256sum --check "$dump.sha256"

restore_db=vyral_temporal_restore_20260728
createdb --maintenance-db=postgres "$restore_db"
pg_restore \
  --exit-on-error \
  --no-owner \
  --no-privileges \
  --dbname="$restore_db" \
  "$dump"
```

Then, without starting a reconciler or worker:

1. Point an isolated status/preflight composition at the restored database and restored object
   prefix. Keep the production Temporal identity available only for read-only consistency checks.
2. Require schema version `4`; compare run/status/history/checkpoint/artifact-metadata and pending
   outbox counts with the backup manifest.
3. Verify every referenced offloaded artifact against its recorded content hash and size.
4. Inspect active coordinator consistency. Classify missing, closed, and uninspectable workflows;
   never repair delivered markers by hand.
5. Record dump hash, object snapshot/version, restore start/end time, assertions, application
   version, and cleanup result. Remove the isolated database and object prefix after retention of
   the redacted receipt.

The disposable container gate automates the projection portion of this rehearsal. It seeds active
and pending runs, coordinator delivery, idempotency, history, checkpoint, artifact metadata,
external-event/signal, cancellation, lease, and all three outboxes; takes a custom-format `pg_dump`;
restores into a random fresh database; verifies the unchanged dump hash and portable state; performs
post-restore store mutations; and requires target/database cleanup. It does not prove production
credentials, object-store snapshots, Temporal persistence recovery, or a deployment's RPO/RTO.

## Disaster recovery activation

Choose the recovery mode before enabling any writer:

- **Projection-only forensic restore:** keep workers and reconcilers off. Use status, read-only
  queries, and artifact verification. This is the safe default for a rehearsal.
- **Projection/object-store recovery with the original Temporal namespace intact:** restore both to
  the same recovery point, preserve adapter namespace and routing, compare every active projection
  coordinator with Temporal, classify data after the RPO cutoff, then canary the compatible worker
  and reconciler before reopening writes.
- **Temporal namespace or service loss:** stop. The prototype cannot reconstruct already-delivered
  coordinators in an empty namespace and has no namespace migration tool. Restore/fail over the
  Temporal service through its supported process, or escalate for an explicit migration. Do not
  drain restored outboxes against an empty namespace.

Before reopening traffic, capture approval for the selected recovery point and any accepted data
loss, confirm compatible worker code, verify both stores, run preflight, start one worker, start one
reconciler, observe redelivery/idempotency behavior, and only then scale out and enable API writes.

## Incident response

| Symptom | Immediate containment | Recovery rule |
| --- | --- | --- |
| Pending outbox age grows | Keep rows intact; stop rollout; verify Temporal health, auth, namespace, queue, and reconciler | Restore connectivity and let the idempotent reconciler redrive. Never delete or mark rows delivered manually. |
| No fresh worker pollers | Stop new submissions if capacity is exhausted; verify worker process and credentials | Restore a replay-compatible worker on the configured queue, then rerun preflight. |
| Projection coordinator is missing or closed in Temporal | Pause writes for affected topology and preserve projection/history evidence | Distinguish an inspection race from a stable mismatch. Do not create a replacement workflow or mutate delivered state without a reviewed recovery plan. |
| PostgreSQL projection unavailable or corrupt | Stop API writers, workers, and reconcilers | Restore into isolation, verify schema and object references, then follow disaster-recovery activation. |
| Offloaded object missing or corrupt | Stop destructive retention; preserve metadata and provider audit logs | Restore the exact object version and verify projection hash/size before serving it. Do not rewrite metadata to match a different body. |
| Worker nondeterminism/replay failure | Halt the rollout and retain failing history identifiers in an access-controlled incident record | Redeploy compatible workflow code and its patch branches. Do not reset workflow history. |
| Temporal unavailable | Keep projection and outboxes durable; prevent unbounded new-work admission | Recover/fail over Temporal, verify namespace and pollers, then allow reconciler redelivery. |
| Credential expiry or revocation | Stop repeated rollout churn and identify affected machine identity | Overlap and validate replacement credentials before removing the old credential whenever possible. Never expose the credential while debugging. |

Preserve redacted timestamps, fixed error classes, counts, deployment versions, and receipt hashes.
Keep raw provider errors and workflow identifiers only in access-controlled operator systems. Do not
copy secrets or tenant payloads into incident tickets.

## Qualification and known limits

Run the disposable gate from the repository root when Docker is available:

```bash
scripts/validate-temporal-container.sh
```

The gate provisions loopback-only Temporal and PostgreSQL dependencies, a per-run object directory,
real worker processes, conformance and recovery scenarios, and the projection restore rehearsal. It
emits a redacted JSON receipt and requires container, volume, schema, database, and object-directory
cleanup. The receipt records the enforced continue-as-new threshold and history guardrails without
workflow ids, run ids, payloads, or raw history, and records that the checked-in legacy coordinator
history replayed with the candidate code. It also records the sentinel boundary check proving run,
event, checkpoint, artifact, trace, status, and result bodies remained in the Vyral projection and
were absent from serialized Temporal history, and an end-to-end Generic Host run proving the
hosted outbox pump and official worker lifecycle execute a portable handler. A skipped test or a
checked-in test is not qualification evidence.

For an operator-provisioned endpoint, use a dedicated qualification namespace and a PostgreSQL role
that can create and drop only isolated gate schemas. The live gate requires TLS for both services,
requires either an API key or mTLS client identity, generates random adapter/queue/workflow/schema
identities, reruns shared conformance and the real process-restart drill, and performs a separate
zero-schema cleanup audit:

```bash
export VYRAL_EXECUTION_TEMPORAL_TARGET_HOST=QUALIFICATION_ENDPOINT:7233
export VYRAL_EXECUTION_TEMPORAL_NAMESPACE=DEDICATED_QUALIFICATION_NAMESPACE
export VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING=FROM_SECRET_MANAGER
export VYRAL_TEMPORAL_LIVE_ENVIRONMENT_CLASS=live_managed
export VYRAL_TEMPORAL_LIVE_PROVIDER_VERSION=TEMPORAL_PROVIDER_VERSION
export VYRAL_TEMPORAL_LIVE_AUTH_MODE=api_key
export VYRAL_TEMPORAL_API_KEY=FROM_SECRET_MANAGER
export VYRAL_TEMPORAL_LIVE_CONFIRM=dedicated-qualification-namespace

scripts/validate-temporal-live.sh
```

For mTLS, set `VYRAL_TEMPORAL_LIVE_AUTH_MODE=mtls`, omit the API key, and provide readable PEM
paths through `VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH` and
`VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH`; optionally provide a private root through
`VYRAL_TEMPORAL_TLS_ROOT_CA_PATH` and an SNI override through `VYRAL_TEMPORAL_TLS_DOMAIN`.
PostgreSQL TLS remains connection-string configuration and must use `Require`, `VerifyCA`, or
`VerifyFull` while the gate's TLS requirement is active.

The manual `Temporal Live Qualification` workflow uses the protected GitHub environment
`temporal-live-qualification`. Configure its target, namespace, PostgreSQL connection, API key or
base64 PEM secrets, and optional TLS domain there and require environment approval. Completed
workflow histories remain in the dedicated namespace until its configured retention removes them;
the gate never deletes a namespace supplied by an operator.

The live receipt deliberately records `temporal_postgres_with_process_local_object_store` scope.
It proves remote Temporal/PostgreSQL behavior and TLS/auth posture but does not qualify a production
artifact provider, repeat the projection backup drill, or automatically promote the adapter. A
promotion still requires the complete reviewed evidence set.

Current limitations remain material:

- no retained receipt means no promotion from `prototype_unqualified`;
- no pre-release projection schema migration path;
- no automated production backup scheduler, object-store inventory verifier, or retention policy;
- no reconstruction of delivered coordinators into a new Temporal namespace;
- no Vyral integration with Temporal Worker Versioning;
- no retained live self-hosted TLS/auth or Temporal Cloud qualification receipt.

See the [portable qualification design](../design/portable-runtime-qualification-and-temporal-adapter.md),
[Temporal adapter package](../src/Vyral.Execution.Temporal/README.md),
[worker host](../src/Vyral.Execution.Temporal.Hosting/README.md), and
[PostgreSQL projection](../src/Vyral.Execution.Temporal.Postgres/README.md) for the underlying
contract and implementation boundaries.
