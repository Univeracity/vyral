# Vyral.Execution.Temporal.Postgres

This prototype package is the transactional PostgreSQL projection and outbox layer for
`Vyral.Execution.Temporal`. It keeps portable run state independent of Temporal visibility and
retention. Temporal remains the coordination engine; this database remains the authority for Vyral
run identity, idempotency, wait/event matching, dispatch state, and the portable status, history,
artifact-metadata, checkpoint, timer, and fenced-lease state surfaces.

The version-4 schema creates run/idempotency rows, start, signal, and cancellation outboxes,
external-event bodies,
waits and outcomes, history, artifact metadata, checkpoints, fenced leases, and timers. Creating a
run and its start outbox row is one transaction. Persisting an external event and its signal outbox
row is another; recording cancellation intent and its delivery row is also atomic. Dispatch claims
use bounded leases and PostgreSQL `FOR UPDATE SKIP LOCKED`; duplicate delivery remains safe because
workflow ids and event id/revision pairs are stable.

Attempt start, progress, retry/terminal completion, artifact metadata, checkpoints, and their trace
records use transactional projection operations. A wait outcome is claimed by one logical handler
attempt, can be reread by a Temporal transport retry of that same attempt, and is consumed only
when the attempt completes or registers its next wait. Already projected retry, suspension, and
terminal outcomes are replayed without invoking the handler. Artifact writes with the same run,
name, body hash, kind, and media type replay the original metadata instead of creating a duplicate.
Future-dated starts remain in the start outbox until due instead of consuming a Temporal activity
attempt merely to wait.

Provider topology is operator configuration. Production connections require PostgreSQL `sslmode`
`Require`, `VerifyCA`, or `VerifyFull`; TLS can be disabled only for a loopback development target.
Diagnostics hash host, database, and schema names and never expose the connection string, username,
password, certificate, endpoint, tenant, or event body.

The operator material linked from the
[repository's Execution Runtime guide](https://github.com/univeracity/vyral#execution-runtime)
defines the projection and object-store recovery boundary, quiesced backup procedure, isolated
restore rehearsal, and incident activation rules.

This package remains prototype work. The versioned schema, API-side reads, transactional event
creation, cancellation intent, timers, fenced lease operations, and standard handler projection
operations are present, and the standard hosting package can offload oversized artifact bodies to
`IObjectStore`. Runtime status and preflight expose redacted outbox, active-run, active-coordinator,
and bounded coordinator-consistency counts. The disposable container gate includes a hash-verified
projection backup/restore drill into a fresh database, but migrations from pre-release schemas,
production backup automation, object-store inventory verification, and the remaining maintenance
surfaces are not complete. Do not infer `local_conformant` or `live_qualified` from the presence of
these tables or the checked-in drill.
