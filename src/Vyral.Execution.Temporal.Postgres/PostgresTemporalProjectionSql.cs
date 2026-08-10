namespace Vyral.Execution.Temporal.Postgres;

internal static class PostgresTemporalProjectionSql
{
    public const int SchemaVersion = 4;

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS vyral_temporal_metadata (
            key text PRIMARY KEY,
            value text NOT NULL,
            updated_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_runs (
            run_id text PRIMARY KEY,
            workflow_id text NOT NULL UNIQUE,
            generation integer NOT NULL CHECK (generation > 0),
            projection_revision bigint NOT NULL CHECK (projection_revision > 0),
            temporal_run_id text NULL,
            status text NOT NULL,
            idempotency_key text NULL,
            request_hash text NOT NULL,
            active_wait_id text NULL,
            cancellation_requested boolean NOT NULL DEFAULT FALSE,
            run_json jsonb NOT NULL,
            created_at_utc timestamptz NOT NULL,
            updated_at_utc timestamptz NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_vyral_temporal_runs_idempotency
            ON vyral_temporal_runs (idempotency_key)
            WHERE idempotency_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_vyral_temporal_runs_status
            ON vyral_temporal_runs (status, updated_at_utc, run_id);

        CREATE TABLE IF NOT EXISTS vyral_temporal_idempotency (
            idempotency_key text PRIMARY KEY,
            request_hash text NOT NULL,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            created_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_start_outbox (
            dispatch_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            workflow_id text NOT NULL,
            generation integer NOT NULL CHECK (generation > 0),
            projection_revision bigint NOT NULL CHECK (projection_revision > 0),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            next_attempt_at_utc timestamptz NOT NULL,
            claimed_by text NULL,
            claimed_until_utc timestamptz NULL,
            delivered_at_utc timestamptz NULL,
            failure_class text NULL,
            created_at_utc timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_vyral_temporal_start_outbox_pending
            ON vyral_temporal_start_outbox (next_attempt_at_utc, created_at_utc, dispatch_id)
            WHERE delivered_at_utc IS NULL;

        CREATE TABLE IF NOT EXISTS vyral_temporal_external_events (
            event_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            event_revision bigint NOT NULL CHECK (event_revision > 0),
            name text NOT NULL,
            event_json jsonb NOT NULL,
            raised_at_utc timestamptz NOT NULL,
            UNIQUE (run_id, event_revision)
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_signal_outbox (
            dispatch_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            workflow_id text NOT NULL,
            generation integer NOT NULL CHECK (generation > 0),
            event_id text NOT NULL REFERENCES vyral_temporal_external_events(event_id) ON DELETE CASCADE,
            event_revision bigint NOT NULL CHECK (event_revision > 0),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            next_attempt_at_utc timestamptz NOT NULL,
            claimed_by text NULL,
            claimed_until_utc timestamptz NULL,
            delivered_at_utc timestamptz NULL,
            failure_class text NULL,
            created_at_utc timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_vyral_temporal_signal_outbox_event
            ON vyral_temporal_signal_outbox (event_id);
        CREATE INDEX IF NOT EXISTS ix_vyral_temporal_signal_outbox_pending
            ON vyral_temporal_signal_outbox (next_attempt_at_utc, created_at_utc, dispatch_id)
            WHERE delivered_at_utc IS NULL;

        CREATE TABLE IF NOT EXISTS vyral_temporal_cancellation_outbox (
            dispatch_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            workflow_id text NOT NULL,
            generation integer NOT NULL CHECK (generation > 0),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            next_attempt_at_utc timestamptz NOT NULL,
            claimed_by text NULL,
            claimed_until_utc timestamptz NULL,
            delivered_at_utc timestamptz NULL,
            failure_class text NULL,
            created_at_utc timestamptz NOT NULL,
            UNIQUE (run_id, generation)
        );
        CREATE INDEX IF NOT EXISTS ix_vyral_temporal_cancellation_outbox_pending
            ON vyral_temporal_cancellation_outbox (next_attempt_at_utc, created_at_utc, dispatch_id)
            WHERE delivered_at_utc IS NULL;

        CREATE TABLE IF NOT EXISTS vyral_temporal_waits (
            wait_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            generation integer NOT NULL CHECK (generation > 0),
            kind text NOT NULL CHECK (kind IN ('external_event', 'timer')),
            name text NOT NULL,
            resume_at_utc timestamptz NULL,
            status text NOT NULL CHECK (status IN ('waiting', 'resolved')),
            created_at_utc timestamptz NOT NULL,
            resolved_at_utc timestamptz NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_vyral_temporal_waits_active_run
            ON vyral_temporal_waits (run_id)
            WHERE status = 'waiting';

        CREATE TABLE IF NOT EXISTS vyral_temporal_wait_outcomes (
            wait_id text PRIMARY KEY REFERENCES vyral_temporal_waits(wait_id) ON DELETE CASCADE,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            resolution text NOT NULL CHECK (resolution IN ('external_event', 'timer', 'timeout')),
            event_id text NULL,
            event_revision bigint NULL,
            outcome_json jsonb NOT NULL,
            resolved_at_utc timestamptz NOT NULL,
            claimed_attempt integer NULL CHECK (claimed_attempt > 0),
            consumed_at_utc timestamptz NULL
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_event_consumptions (
            event_id text PRIMARY KEY REFERENCES vyral_temporal_external_events(event_id) ON DELETE CASCADE,
            wait_id text NOT NULL REFERENCES vyral_temporal_waits(wait_id) ON DELETE CASCADE,
            consumed_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_history (
            event_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            sequence_id text NOT NULL,
            event_json jsonb NOT NULL,
            created_at_utc timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_vyral_temporal_history_run
            ON vyral_temporal_history (run_id, sequence_id, event_id);

        CREATE TABLE IF NOT EXISTS vyral_temporal_artifacts (
            artifact_id text PRIMARY KEY,
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            name text NOT NULL,
            content_hash text NOT NULL,
            metadata_json jsonb NOT NULL,
            created_at_utc timestamptz NOT NULL,
            UNIQUE (run_id, name)
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_checkpoints (
            run_id text NOT NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            checkpoint_key text NOT NULL,
            content_hash text NOT NULL,
            checkpoint_json jsonb NOT NULL,
            updated_at_utc timestamptz NOT NULL,
            PRIMARY KEY (run_id, checkpoint_key)
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_leases (
            lease_key text PRIMARY KEY,
            owner_id text NOT NULL,
            run_id text NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            fencing_token bigint NOT NULL CHECK (fencing_token > 0),
            expires_at_utc timestamptz NOT NULL,
            lease_json jsonb NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vyral_temporal_timers (
            timer_id text PRIMARY KEY,
            run_id text NULL REFERENCES vyral_temporal_runs(run_id) ON DELETE CASCADE,
            name text NOT NULL,
            fire_at_utc timestamptz NOT NULL,
            timer_json jsonb NOT NULL
        );
        """;
}
