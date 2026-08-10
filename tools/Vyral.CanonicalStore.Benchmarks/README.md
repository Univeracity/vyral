# CanonicalStore MySQL Contention Benchmark

This operator tool measures the deliberate one-row-per-tenant lock profile of
`MySqlCanonicalStore`. It creates and drops a uniquely named database and emits a redacted JSON
report with four workloads:

- concurrent document-plus-outbox commits against one hot tenant;
- the same commits sharded across one tenant per worker;
- immutable idempotent replays against one tenant, isolating row-lock pressure without state
  growth;
- the same replays sharded across tenants.

No connection string, endpoint, database name, tenant id, document body, exception message, or
lease token is present in the report. It records the server-default isolation separately from the
adapter's explicit `READ COMMITTED` mutation transactions, plus InnoDB flush and binary-log
settings. The source connection must be supplied through
`VYRAL_MYSQL_CONNECTION_STRING`, never a command-line argument. The credential needs temporary
database create/drop privileges, so use only a disposable or explicitly isolated benchmark
server.

## Disposable runners

On a host with MySQL 8 binaries installed, start an isolated native instance with durable InnoDB
commit behavior:

```bash
scripts/run-canonical-mysql-benchmark-native.sh \
  --output .vyral/benchmarks/canonical-store/mysql.json
```

With Docker, use the MySQL 8.4 service from the cutover composition:

```bash
deploy/canonical-cutover/run-mysql-benchmark.sh \
  --output .vyral/benchmarks/canonical-store/mysql.json
```

For an operator-owned disposable target:

```bash
VYRAL_MYSQL_CONNECTION_STRING='...' \
scripts/run-canonical-mysql-benchmark.sh \
  --workers 8 \
  --operations-per-worker 25 \
  --seed-documents-per-tenant 25 \
  --payload-bytes 256 \
  --max-same-tenant-p95-ms 100 \
  --min-same-tenant-ops-per-second 50 \
  --max-write-contention-ratio 4 \
  --output .vyral/benchmarks/canonical-store/mysql.json
```

Tune the workload to the expected hot-tenant size, payload, and concurrency. The defaults are
starter rollout budgets, not a universal provider claim:

- hot-tenant write p95 must be at most 100 ms;
- hot-tenant throughput must be at least 50 commits/second;
- hot-tenant p95 may be no more than 4x the tenant-sharded p95;
- every timed operation must complete without a classified failure.
- `innodb_flush_log_at_trx_commit` must be `1`; relaxed commit durability cannot pass the gate.

Exit code `0` means those selected budgets passed, `3` means the report was written but at least
one budget failed, and `2` means setup or execution could not produce a valid report. A failed
budget selects `postgresql_granular_or_dedicated_projection`; it does not claim PostgreSQL will
meet the workload without its own benchmark. Prefer PostgreSQL's granular CanonicalStore layout
for write-hot aggregates and a dedicated projection for join/reporting pressure.

The repository retains one explicitly local baseline under
[`benchmarks/canonical-store`](../../benchmarks/canonical-store/README.md). It is evidence for the
harness and threshold decision, not a managed-provider qualification. Release verification rejects
an incomplete, internally inconsistent, relaxed-durability, untraceable, or connection-bearing
receipt.
