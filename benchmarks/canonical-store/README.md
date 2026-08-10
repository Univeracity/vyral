# CanonicalStore Benchmark Evidence

[`mysql-8.0.46-local.json`](mysql-8.0.46-local.json) is a retained, redacted run of the default
`canonical-mysql-contention/v1` workload from harness commit
`f289a8752af298a90a7eb48e521835f7ba3f70d5`.

The local MySQL 8.0.46 run used 8 workers, 25 operations per worker, 25 pre-seeded documents per
tenant, 256-byte payloads, explicit `READ COMMITTED` CanonicalStore mutations, and
`innodb_flush_log_at_trx_commit=1`. All 800 timed operations across four scenarios completed with
no classified failure.

The one-hot-tenant write scenario produced 346.782 ms p95 and 44.834 commits/second while its
serialized tenant state grew from 50,697 to 746,173 bytes. It therefore missed the starter 100 ms
p95 and 50 commits/second budgets. The report mechanically recommends evaluating PostgreSQL's
granular layout or a dedicated projection. The result is local machine evidence, not a claim about
MySQL 8.4, Cloud SQL, a production topology, or PostgreSQL performance. Operators must rerun the
same harness with representative state, concurrency, networking, durability, and SLO budgets.

Release verification validates the receipt's schema, commit ancestry, operation counts,
durability, decision arithmetic, and absence of connection/tenant/payload fields:

```bash
scripts/validate-canonical-mysql-benchmark-report.sh
```
