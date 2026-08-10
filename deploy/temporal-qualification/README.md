# Temporal container qualification dependencies

This composition provides the isolated Temporal Server and PostgreSQL dependencies used by
`scripts/validate-temporal-container.sh`. It follows the current Temporal `samples-server`
PostgreSQL layout: schema initialization is explicit, the server uses the production-shaped
`temporalio/server` image, and a dedicated short-retention namespace is created before tests run.

The gate binds development-only ports to loopback, uses a random Compose project name, creates a
per-run database password and Vyral projection schema, and uses a per-run file-backed
`IObjectStore`. The gate also uses the PostgreSQL 16 client tools to dump that projection, restore
it into a random fresh database, verify portable state and resumed store operations, and remove the
target database. The gate always attempts to remove its containers, network, volume, projection
schema, restore database, dump, and object directory.

These settings are only for disposable local/CI qualification. They are not an example of a
production Temporal deployment, authentication policy, or secret-management strategy.

For a dedicated operator-provisioned TLS/authenticated namespace and PostgreSQL target, use
`scripts/validate-temporal-live.sh` instead. That script reuses the behavioral suite but does not
start, reconfigure, or delete Temporal or PostgreSQL infrastructure.
