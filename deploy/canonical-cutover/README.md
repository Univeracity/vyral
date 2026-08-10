# Disposable CanonicalStore Cutover Gate

This operator-run gate starts isolated MySQL 8.4 and PostgreSQL 16 containers, creates unique
source/target namespaces, and runs the real MySQL-to-PostgreSQL tenant cutover scenario:

```bash
deploy/canonical-cutover/run.sh
```

The test seeds a document, revision, uniqueness fence, idempotency receipt, and leased outbox event
in MySQL; exports and independently reads the chunk/hash archive; restores PostgreSQL; verifies
lease clearing, idempotent replay, fence rejection, and migration-ledger deployment; rebuilds a
fresh relational projection from the target; acknowledges restored events through the rebuild
fence; and applies a new target-side update through the outbox pump.

The script always tears down its project and volumes. Override `VYRAL_CUTOVER_MYSQL_PORT` or
`VYRAL_CUTOVER_POSTGRES_PORT` if the defaults are occupied. To use operator-supplied disposable
databases instead of Docker, set both connection strings and run:

```bash
VYRAL_MYSQL_CONNECTION_STRING='...' \
VYRAL_PGVECTOR_CONNECTION_STRING='...' \
scripts/validate-canonical-mysql-postgres-cutover.sh
```

Never point this gate at a production database: it creates and drops uniquely named databases and
schemas and requires administrative DDL privileges.

The same disposable MySQL 8.4 service can run the correctness-first tenant lock benchmark:

```bash
deploy/canonical-cutover/run-mysql-benchmark.sh \
  --output .vyral/benchmarks/canonical-store/mysql.json
```

This starts only MySQL, creates and drops an isolated benchmark database, writes the redacted
report, and tears down the Compose project and volumes. A budget miss exits with code `3` after the
report is safely written.
