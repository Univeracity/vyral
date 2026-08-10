using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Vyral.Execution;

namespace Vyral.Execution.Temporal.Postgres;

public sealed partial class PostgresTemporalExecutionProjectionStore : ITemporalExecutionRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string _claimOwner = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresTemporalExecutionProjectionStore(PostgresTemporalProjectionOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        _connectionString = options.ConnectionString;
    }

    public PostgresTemporalExecutionProjectionStore(
        NpgsqlDataSource dataSource,
        PostgresTemporalProjectionOptions? options = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        Options = options ?? new PostgresTemporalProjectionOptions();
        Options.Validate(requireConnectionString: false);
        Options.ValidateConnectionString(dataSource.ConnectionString);
    }

    public PostgresTemporalProjectionOptions Options { get; }

    public async Task InitializeAsync(CancellationToken ct = default) => await EnsureInitializedAsync(ct);

    public async Task<TemporalProjectionRunStartResult> CreateRunWithPendingStartAsync(
        TemporalProjectionRunStart start,
        CancellationToken ct = default)
    {
        ValidateStart(start);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(start.Run.IdempotencyKey))
            {
                await ExecuteAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", ct,
                    ("key", start.Run.IdempotencyKey));
                var receipt = await ReadIdempotencyAsync(connection, transaction, start.Run.IdempotencyKey!, ct);
                if (receipt is not null)
                {
                    if (!string.Equals(receipt.Value.RequestHash, start.RequestHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("Temporal projection idempotency key belongs to a different run request.");
                    var replayedRun = await ReadRunRowAsync(connection, transaction, receipt.Value.RunId, lockForUpdate: true, ct)
                        ?? throw new InvalidOperationException("Temporal projection idempotency receipt has no run.");
                    var replayedDispatch = await ReadStartDispatchIdAsync(connection, transaction, replayedRun.Run.Id, ct)
                        ?? throw new InvalidOperationException("Temporal projection run has no start dispatch.");
                    await transaction.CommitAsync(ct);
                    return new TemporalProjectionRunStartResult
                    {
                        Run = Clone(replayedRun.Run),
                        DispatchId = replayedDispatch,
                        Replayed = true
                    };
                }
            }

            var existing = await ReadRunRowAsync(connection, transaction, start.Run.Id, lockForUpdate: true, ct);
            if (existing is not null)
            {
                if (!SameStart(existing, start))
                    throw new InvalidOperationException("Temporal projection run identity conflicts with an existing run.");
                var dispatchId = await ReadStartDispatchIdAsync(connection, transaction, start.Run.Id, ct)
                    ?? throw new InvalidOperationException("Temporal projection run has no start dispatch.");
                await transaction.CommitAsync(ct);
                return new TemporalProjectionRunStartResult
                {
                    Run = Clone(existing.Run),
                    DispatchId = dispatchId,
                    Replayed = true
                };
            }

            var now = DateTime.UtcNow;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_runs
                    (run_id, workflow_id, generation, projection_revision, status, idempotency_key,
                     request_hash, cancellation_requested, run_json, created_at_utc, updated_at_utc)
                VALUES
                    (@run_id, @workflow_id, @generation, @projection_revision, @status, @idempotency_key,
                     @request_hash, @cancellation_requested, @run_json::jsonb, @created_at_utc, @updated_at_utc);
                """, ct,
                ("run_id", start.Run.Id), ("workflow_id", start.WorkflowId),
                ("generation", start.Generation), ("projection_revision", start.ProjectionRevision),
                ("status", start.Run.Status),
                ("idempotency_key", DbValue(start.Run.IdempotencyKey, NpgsqlDbType.Text)),
                ("request_hash", start.RequestHash), ("cancellation_requested", start.Run.CancellationRequested),
                ("run_json", Serialize(start.Run)), ("created_at_utc", Utc(start.Run.CreatedAtUtc)),
                ("updated_at_utc", Utc(start.Run.UpdatedAtUtc)));

            if (!string.IsNullOrWhiteSpace(start.Run.IdempotencyKey))
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_temporal_idempotency
                        (idempotency_key, request_hash, run_id, created_at_utc)
                    VALUES (@idempotency_key, @request_hash, @run_id, @created_at_utc);
                    """, ct,
                    ("idempotency_key", start.Run.IdempotencyKey), ("request_hash", start.RequestHash),
                    ("run_id", start.Run.Id), ("created_at_utc", now));
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_start_outbox
                    (dispatch_id, run_id, workflow_id, generation, projection_revision,
                     next_attempt_at_utc, created_at_utc)
                VALUES
                    (@dispatch_id, @run_id, @workflow_id, @generation, @projection_revision,
                     @next_attempt_at_utc, @created_at_utc);
                """, ct,
                ("dispatch_id", start.DispatchId), ("run_id", start.Run.Id),
                ("workflow_id", start.WorkflowId), ("generation", start.Generation),
                ("projection_revision", start.ProjectionRevision),
                ("next_attempt_at_utc", start.Run.ScheduledAtUtc is { } scheduled && scheduled > now
                    ? scheduled.ToUniversalTime()
                    : now),
                ("created_at_utc", now));

            await transaction.CommitAsync(ct);
            return new TemporalProjectionRunStartResult
            {
                Run = Clone(start.Run),
                DispatchId = start.DispatchId,
                Replayed = false
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RegisterWaitAsync(
        TemporalProjectionWaitRegistration registration,
        ExecutionTraceEvent registeredEvent,
        CancellationToken ct = default)
    {
        ValidateWait(registration);
        ValidateTrace(registeredEvent, registration.RunId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, registration.RunId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal projection run was not found.");
            if (row.Generation != registration.Generation || ExecutionRunStatuses.IsTerminal(row.Run.Status))
                throw new InvalidOperationException("Temporal projection wait generation or run state is stale.");

            var existing = await ReadWaitAsync(connection, transaction, registration.WaitId, ct);
            if (existing is not null)
            {
                if (!SameWait(existing.Value, registration))
                    throw new InvalidOperationException("Temporal projection wait identity conflicts with an existing wait.");
                await transaction.CommitAsync(ct);
                return;
            }
            if (!string.IsNullOrWhiteSpace(row.ActiveWaitId))
                throw new InvalidOperationException("Temporal projection run already has a different active wait.");

            var now = DateTime.UtcNow;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_waits
                    (wait_id, run_id, generation, kind, name, resume_at_utc, status, created_at_utc)
                VALUES (@wait_id, @run_id, @generation, @kind, @name, @resume_at_utc, 'waiting', @created_at_utc);
                """, ct,
                ("wait_id", registration.WaitId), ("run_id", registration.RunId),
                ("generation", registration.Generation), ("kind", registration.Kind),
                ("name", registration.Name),
                ("resume_at_utc", DbValue(registration.ResumeAtUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz)),
                ("created_at_utc", now));

            ExecutionRunLifecycle.EnsureTransition(
                row.Run.Status,
                ExecutionRunStatuses.Waiting,
                ExecutionTransitionKind.DurableWait);
            row.Run.Status = ExecutionRunStatuses.Waiting;
            row.Run.ScheduledAtUtc = registration.ResumeAtUtc?.ToUniversalTime();
            row.Run.CurrentStep = registration.Name;
            row.Run.UpdatedAtUtc = now;
            await UpdateRunAsync(connection, transaction, row.Run, row.ProjectionRevision + 1,
                registration.WaitId, row.Run.CancellationRequested, ct);
            await ConsumeClaimedWaitOutcomesAsync(
                connection,
                transaction,
                row.Run.Id,
                row.Run.Attempt,
                ct);
            await InsertHistoryAsync(
                connection,
                transaction,
                NormalizeTrace(registeredEvent, row.Run, ExecutionEventTypes.WaitRegistered),
                ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task PersistExternalEventWithPendingSignalAsync(
        TemporalProjectionExternalEventWrite write,
        CancellationToken ct = default)
    {
        ValidateEvent(write);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, write.Event.RunId!, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal projection event run was not found.");
            if (row.Generation != write.Generation || !string.Equals(row.WorkflowId, write.WorkflowId, StringComparison.Ordinal))
                throw new InvalidOperationException("Temporal projection event coordinator identity is stale.");

            var existing = await ReadExternalEventAsync(connection, transaction, write.Event.Id, ct);
            if (existing is not null)
            {
                if (existing.Value.Revision != write.EventRevision ||
                    !JsonEquivalent(existing.Value.Event, write.Event))
                    throw new InvalidOperationException("Temporal projection event id conflicts with different event content.");
            }
            else
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_temporal_external_events
                        (event_id, run_id, event_revision, name, event_json, raised_at_utc)
                    VALUES (@event_id, @run_id, @event_revision, @name, @event_json::jsonb, @raised_at_utc);
                    """, ct,
                    ("event_id", write.Event.Id), ("run_id", write.Event.RunId),
                    ("event_revision", write.EventRevision), ("name", write.Event.Name),
                    ("event_json", Serialize(write.Event)), ("raised_at_utc", Utc(write.Event.RaisedAtUtc)));
            }

            var now = DateTime.UtcNow;
            var signalInserted = await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_signal_outbox
                    (dispatch_id, run_id, workflow_id, generation, event_id, event_revision,
                     next_attempt_at_utc, created_at_utc)
                VALUES
                    (@dispatch_id, @run_id, @workflow_id, @generation, @event_id, @event_revision,
                     @next_attempt_at_utc, @created_at_utc)
                ON CONFLICT DO NOTHING;
                """, ct,
                ("dispatch_id", write.DispatchId), ("run_id", write.Event.RunId),
                ("workflow_id", write.WorkflowId), ("generation", write.Generation),
                ("event_id", write.Event.Id), ("event_revision", write.EventRevision),
                ("next_attempt_at_utc", now), ("created_at_utc", now));
            if (signalInserted == 0)
            {
                var existingSignal = await ReadSignalIdentityAsync(connection, transaction, write.Event.Id, ct);
                if (existingSignal is null ||
                    !string.Equals(existingSignal.Value.DispatchId, write.DispatchId, StringComparison.Ordinal) ||
                    !string.Equals(existingSignal.Value.RunId, write.Event.RunId, StringComparison.Ordinal) ||
                    !string.Equals(existingSignal.Value.WorkflowId, write.WorkflowId, StringComparison.Ordinal) ||
                    existingSignal.Value.Generation != write.Generation ||
                    existingSignal.Value.EventRevision != write.EventRevision)
                    throw new InvalidOperationException("Temporal projection signal dispatch conflicts with existing durable delivery state.");
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<TemporalStartDispatch>> ListPendingStartsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        var results = new List<TemporalStartDispatch>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
                SELECT dispatch_id
                FROM vyral_temporal_start_outbox
                WHERE delivered_at_utc IS NULL
                  AND next_attempt_at_utc <= CURRENT_TIMESTAMP
                  AND (claimed_until_utc IS NULL OR claimed_until_utc <= CURRENT_TIMESTAMP)
                ORDER BY created_at_utc, dispatch_id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE vyral_temporal_start_outbox AS outbox
            SET claimed_by = @claimed_by,
                claimed_until_utc = CURRENT_TIMESTAMP + (@claim_seconds * INTERVAL '1 second'),
                attempt_count = outbox.attempt_count + 1
            FROM candidates
            WHERE outbox.dispatch_id = candidates.dispatch_id
            RETURNING outbox.dispatch_id, outbox.run_id, outbox.workflow_id,
                      outbox.projection_revision, outbox.generation, outbox.attempt_count;
            """;
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("claimed_by", _claimOwner);
        command.Parameters.AddWithValue("claim_seconds", Options.DispatchClaimSeconds);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(new TemporalStartDispatch
                {
                    DispatchId = reader.GetString(0), RunId = reader.GetString(1),
                    WorkflowId = reader.GetString(2), ProjectionRevision = reader.GetInt64(3),
                    Generation = reader.GetInt32(4), AttemptCount = reader.GetInt32(5)
                });
            }
        }
        await transaction.CommitAsync(ct);
        return results;
    }

    public Task MarkStartDeliveredAsync(
        string dispatchId,
        TemporalCoordinationReference reference,
        CancellationToken ct = default) => CompleteStartAsync(dispatchId, reference, ct);

    public Task RecordStartFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default) =>
        RecordDispatchFailureAsync("vyral_temporal_start_outbox", dispatchId, failureClass, ct);

    public async Task<IReadOnlyList<TemporalSignalDispatch>> ListPendingSignalsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        var results = new List<TemporalSignalDispatch>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
                SELECT dispatch_id
                FROM vyral_temporal_signal_outbox
                WHERE delivered_at_utc IS NULL
                  AND next_attempt_at_utc <= CURRENT_TIMESTAMP
                  AND (claimed_until_utc IS NULL OR claimed_until_utc <= CURRENT_TIMESTAMP)
                ORDER BY created_at_utc, dispatch_id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE vyral_temporal_signal_outbox AS outbox
            SET claimed_by = @claimed_by,
                claimed_until_utc = CURRENT_TIMESTAMP + (@claim_seconds * INTERVAL '1 second'),
                attempt_count = outbox.attempt_count + 1
            FROM candidates
            WHERE outbox.dispatch_id = candidates.dispatch_id
            RETURNING outbox.dispatch_id, outbox.run_id, outbox.workflow_id, outbox.generation,
                      outbox.event_id, outbox.event_revision, outbox.attempt_count;
            """;
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("claimed_by", _claimOwner);
        command.Parameters.AddWithValue("claim_seconds", Options.DispatchClaimSeconds);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(new TemporalSignalDispatch
                {
                    DispatchId = reader.GetString(0), RunId = reader.GetString(1),
                    WorkflowId = reader.GetString(2), Generation = reader.GetInt32(3),
                    EventId = reader.GetString(4), EventRevision = reader.GetInt64(5),
                    AttemptCount = reader.GetInt32(6)
                });
            }
        }
        await transaction.CommitAsync(ct);
        return results;
    }

    public Task MarkSignalDeliveredAsync(string dispatchId, CancellationToken ct = default) =>
        MarkDispatchDeliveredAsync("vyral_temporal_signal_outbox", dispatchId, ct);

    public Task RecordSignalFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default) =>
        RecordDispatchFailureAsync("vyral_temporal_signal_outbox", dispatchId, failureClass, ct);

    public async Task<TemporalExecutionWaitProjectionResult> ProjectWaitResolutionAsync(
        TemporalExecutionWaitResolution resolution,
        CancellationToken ct = default)
    {
        ValidateResolution(resolution);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var prior = await ReadWaitOutcomeAsync(connection, transaction, resolution.WaitId, ct);
            if (prior is not null)
            {
                var acceptedReplay = string.Equals(prior.Value.RunId, resolution.RunId, StringComparison.Ordinal) &&
                    prior.Value.Generation == resolution.Generation &&
                    string.Equals(prior.Value.Resolution, resolution.Resolution, StringComparison.Ordinal) &&
                    string.Equals(prior.Value.EventId, resolution.EventId, StringComparison.Ordinal) &&
                    prior.Value.EventRevision == resolution.EventRevision;
                await transaction.CommitAsync(ct);
                return new TemporalExecutionWaitProjectionResult { Accepted = acceptedReplay };
            }

            var row = await ReadRunRowAsync(connection, transaction, resolution.RunId, lockForUpdate: true, ct);
            var wait = await ReadWaitAsync(connection, transaction, resolution.WaitId, ct);
            if (row is null || wait is null || row.Generation != resolution.Generation ||
                !string.Equals(row.ActiveWaitId, resolution.WaitId, StringComparison.Ordinal) ||
                !string.Equals(wait.Value.RunId, resolution.RunId, StringComparison.Ordinal) ||
                wait.Value.Generation != resolution.Generation || wait.Value.Status != "waiting")
            {
                await transaction.CommitAsync(ct);
                return new TemporalExecutionWaitProjectionResult { Accepted = false };
            }

            if (!ResolutionMatchesWait(wait.Value, resolution))
            {
                await transaction.CommitAsync(ct);
                return new TemporalExecutionWaitProjectionResult { Accepted = false };
            }

            if (resolution.Resolution == "external_event")
            {
                var externalEvent = await ReadExternalEventAsync(connection, transaction, resolution.EventId!, ct);
                if (externalEvent is null || externalEvent.Value.Revision != resolution.EventRevision ||
                    !string.Equals(externalEvent.Value.Event.RunId, resolution.RunId, StringComparison.Ordinal) ||
                    !string.Equals(externalEvent.Value.Event.Name, wait.Value.Name, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(ct);
                    return new TemporalExecutionWaitProjectionResult { Accepted = false };
                }
                var consumed = await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_temporal_event_consumptions (event_id, wait_id, consumed_at_utc)
                    VALUES (@event_id, @wait_id, @consumed_at_utc)
                    ON CONFLICT (event_id) DO NOTHING;
                    """, ct,
                    ("event_id", resolution.EventId), ("wait_id", resolution.WaitId),
                    ("consumed_at_utc", DateTime.UtcNow));
                if (consumed == 0)
                {
                    await transaction.CommitAsync(ct);
                    return new TemporalExecutionWaitProjectionResult { Accepted = false };
                }
            }

            var now = DateTime.UtcNow;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_wait_outcomes
                    (wait_id, run_id, resolution, event_id, event_revision, outcome_json, resolved_at_utc)
                VALUES
                    (@wait_id, @run_id, @resolution, @event_id, @event_revision,
                     @outcome_json::jsonb, @resolved_at_utc);
                """, ct,
                ("wait_id", resolution.WaitId), ("run_id", resolution.RunId),
                ("resolution", resolution.Resolution),
                ("event_id", DbValue(resolution.EventId, NpgsqlDbType.Text)),
                ("event_revision", DbValue(resolution.EventRevision, NpgsqlDbType.Bigint)),
                ("outcome_json", Serialize(resolution)),
                ("resolved_at_utc", now));
            await ExecuteAsync(connection, transaction, """
                UPDATE vyral_temporal_waits
                SET status = 'resolved', resolved_at_utc = @resolved_at_utc
                WHERE wait_id = @wait_id;
                """, ct, ("resolved_at_utc", now), ("wait_id", resolution.WaitId));

            ExecutionRunLifecycle.EnsureTransition(row.Run.Status, ExecutionRunStatuses.Queued);
            row.Run.Status = ExecutionRunStatuses.Queued;
            row.Run.ScheduledAtUtc = null;
            row.Run.CurrentStep = null;
            row.Run.UpdatedAtUtc = now;
            await UpdateRunAsync(connection, transaction, row.Run, row.ProjectionRevision + 1,
                activeWaitId: null, row.Run.CancellationRequested, ct);
            await transaction.CommitAsync(ct);
            return new TemporalExecutionWaitProjectionResult { Accepted = true };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ProjectCancellationAsync(TemporalExecutionCancellation cancellation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ValidateText(cancellation.RunId, "Temporal cancellation run id", 200);
        if (cancellation.Generation < 1) throw new InvalidOperationException("Temporal cancellation generation is invalid.");
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, cancellation.RunId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal projection cancellation run was not found.");
            if (row.Generation != cancellation.Generation)
                throw new InvalidOperationException("Temporal projection cancellation generation is stale.");
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status))
            {
                await transaction.CommitAsync(ct);
                return;
            }

            var now = DateTime.UtcNow;
            ExecutionRunLifecycle.EnsureTransition(row.Run.Status, ExecutionRunStatuses.Cancelled);
            row.Run.CancellationRequested = true;
            row.Run.Status = ExecutionRunStatuses.Cancelled;
            row.Run.FailureClass = ExecutionFailureClasses.Cancelled;
            row.Run.Error = "Execution run was cancelled.";
            row.Run.CompletedAtUtc = now;
            row.Run.UpdatedAtUtc = now;
            row.Run.DurationMs = Math.Max(0, (now - row.Run.CreatedAtUtc).TotalMilliseconds);
            if (!string.IsNullOrWhiteSpace(row.ActiveWaitId))
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE vyral_temporal_waits
                    SET status = 'resolved', resolved_at_utc = @resolved_at_utc
                    WHERE wait_id = @wait_id AND status = 'waiting';
                    """, ct, ("resolved_at_utc", now), ("wait_id", row.ActiveWaitId));
            }
            await UpdateRunAsync(connection, transaction, row.Run, row.ProjectionRevision + 1,
                activeWaitId: null, cancellationRequested: true, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PostgresTemporalProjectionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT value::integer FROM vyral_temporal_metadata WHERE key = 'schemaVersion'),
                (SELECT count(*)::integer FROM vyral_temporal_start_outbox WHERE delivered_at_utc IS NULL),
                (SELECT count(*)::integer FROM vyral_temporal_signal_outbox WHERE delivered_at_utc IS NULL),
                (SELECT count(*)::integer FROM vyral_temporal_cancellation_outbox WHERE delivered_at_utc IS NULL),
                (SELECT min(created_at_utc) FROM (
                    SELECT created_at_utc FROM vyral_temporal_start_outbox WHERE delivered_at_utc IS NULL
                    UNION ALL
                    SELECT created_at_utc FROM vyral_temporal_signal_outbox WHERE delivered_at_utc IS NULL
                    UNION ALL
                    SELECT created_at_utc FROM vyral_temporal_cancellation_outbox WHERE delivered_at_utc IS NULL
                ) pending),
                (SELECT count(*)::integer FROM vyral_temporal_runs
                 WHERE status NOT IN ('succeeded', 'failed', 'cancelled', 'rejected', 'timed_out')),
                (SELECT count(*)::integer
                 FROM vyral_temporal_runs AS runs
                 WHERE runs.status NOT IN ('succeeded', 'failed', 'cancelled', 'rejected', 'timed_out')
                   AND EXISTS (
                       SELECT 1
                       FROM vyral_temporal_start_outbox AS starts
                       WHERE starts.run_id = runs.run_id
                         AND starts.delivered_at_utc IS NOT NULL));
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Temporal projection status query returned no row.");
        return new PostgresTemporalProjectionStatus
        {
            SchemaVersion = reader.GetInt32(0),
            PendingStartDispatches = reader.GetInt32(1),
            PendingSignalDispatches = reader.GetInt32(2),
            PendingCancellationDispatches = reader.GetInt32(3),
            OldestPendingDispatchAtUtc = reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToUniversalTime(),
            ActiveRuns = reader.GetInt32(5),
            ActiveCoordinators = reader.GetInt32(6)
        };
    }

    private async Task CompleteStartAsync(
        string dispatchId,
        TemporalCoordinationReference reference,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateText(dispatchId, "Temporal start dispatch id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT run_id, workflow_id, generation, delivered_at_utc
                FROM vyral_temporal_start_outbox
                WHERE dispatch_id = @dispatch_id
                FOR UPDATE;
                """;
            command.Parameters.AddWithValue("dispatch_id", dispatchId);
            string runId;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Temporal start dispatch was not found.");
                runId = reader.GetString(0);
                if (!string.Equals(reader.GetString(1), reference.WorkflowId, StringComparison.Ordinal) ||
                    reader.GetInt32(2) != reference.Generation)
                    throw new InvalidOperationException("Temporal start delivery reference conflicts with its durable dispatch.");
                if (!reader.IsDBNull(3))
                {
                    await reader.CloseAsync();
                    await transaction.CommitAsync(ct);
                    return;
                }
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE vyral_temporal_start_outbox
                SET delivered_at_utc = CURRENT_TIMESTAMP, claimed_by = NULL, claimed_until_utc = NULL,
                    failure_class = NULL
                WHERE dispatch_id = @dispatch_id;
                """, ct, ("dispatch_id", dispatchId));
            await ExecuteAsync(connection, transaction, """
                UPDATE vyral_temporal_runs
                SET updated_at_utc = CURRENT_TIMESTAMP,
                    projection_revision = projection_revision + 1,
                    temporal_run_id = @temporal_run_id
                WHERE run_id = @run_id;
            """, ct,
            ("temporal_run_id", DbValue(reference.TemporalRunId, NpgsqlDbType.Text)),
            ("run_id", runId));
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task MarkDispatchDeliveredAsync(string table, string dispatchId, CancellationToken ct)
    {
        ValidateOutboxTable(table);
        ValidateText(dispatchId, "Temporal dispatch id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var affected = await ExecuteAsync(connection, null, $"""
            UPDATE {table}
            SET delivered_at_utc = COALESCE(delivered_at_utc, CURRENT_TIMESTAMP),
                claimed_by = NULL, claimed_until_utc = NULL, failure_class = NULL
            WHERE dispatch_id = @dispatch_id;
            """, ct, ("dispatch_id", dispatchId));
        if (affected == 0) throw new InvalidOperationException("Temporal dispatch was not found.");
    }

    private async Task RecordDispatchFailureAsync(
        string table,
        string dispatchId,
        string failureClass,
        CancellationToken ct)
    {
        ValidateOutboxTable(table);
        ValidateText(dispatchId, "Temporal dispatch id", 200);
        ValidateText(failureClass, "Temporal dispatch failure class", 160);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var affected = await ExecuteAsync(connection, null, $"""
            UPDATE {table}
            SET failure_class = @failure_class,
                next_attempt_at_utc = CURRENT_TIMESTAMP + (@retry_seconds * INTERVAL '1 second'),
                claimed_by = NULL,
                claimed_until_utc = NULL
            WHERE dispatch_id = @dispatch_id AND delivered_at_utc IS NULL;
            """, ct,
            ("failure_class", failureClass), ("retry_seconds", Options.DispatchRetrySeconds),
            ("dispatch_id", dispatchId));
        if (affected == 0) throw new InvalidOperationException("Pending Temporal dispatch was not found.");
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenUnconfiguredAsync(ct);
            await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                var schema = PostgresTemporalProjectionOptions.QuoteSchema(Options.DatabaseSchema);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    CREATE SCHEMA IF NOT EXISTS {schema};
                    SET search_path TO {schema}, public;
                    {PostgresTemporalProjectionSql.Schema}
                    INSERT INTO vyral_temporal_metadata (key, value, updated_at_utc)
                    VALUES ('schemaVersion', @schema_version, CURRENT_TIMESTAMP)
                    ON CONFLICT (key) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("schema_version", PostgresTemporalProjectionSql.SchemaVersion.ToString());
                await command.ExecuteNonQueryAsync(ct);

                await using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "SELECT value FROM vyral_temporal_metadata WHERE key = 'schemaVersion';";
                var storedVersion = (string?)await versionCommand.ExecuteScalarAsync(ct);
                if (!string.Equals(
                    storedVersion,
                    PostgresTemporalProjectionSql.SchemaVersion.ToString(),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Temporal projection schema version is not supported by this package.");
                }

                await transaction.CommitAsync(ct);
                _initialized = true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await OpenUnconfiguredAsync(ct);
        try
        {
            var schema = PostgresTemporalProjectionOptions.QuoteSchema(Options.DatabaseSchema);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO {schema}, public;";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenUnconfiguredAsync(CancellationToken ct)
    {
        if (_dataSource is not null) return await _dataSource.OpenConnectionAsync(ct);
        var connection = new NpgsqlConnection(_connectionString!);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<RunRow?> ReadRunRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        bool lockForUpdate,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT run_json::text, workflow_id, generation, projection_revision,
                   active_wait_id, request_hash
            FROM vyral_temporal_runs
            WHERE run_id = @run_id
            {(lockForUpdate ? "FOR UPDATE" : string.Empty)};
            """;
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new RunRow(
            Deserialize<ExecutionRun>(reader.GetString(0)),
            reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5));
    }

    private static async Task UpdateRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionRun run,
        long projectionRevision,
        string? activeWaitId,
        bool cancellationRequested,
        CancellationToken ct) =>
        _ = await ExecuteAsync(connection, transaction, """
            UPDATE vyral_temporal_runs
            SET status = @status,
                projection_revision = @projection_revision,
                active_wait_id = @active_wait_id,
                cancellation_requested = @cancellation_requested,
                run_json = @run_json::jsonb,
                updated_at_utc = @updated_at_utc
            WHERE run_id = @run_id;
            """, ct,
            ("status", run.Status), ("projection_revision", projectionRevision),
            ("active_wait_id", DbValue(activeWaitId, NpgsqlDbType.Text)),
            ("cancellation_requested", cancellationRequested),
            ("run_json", Serialize(run)), ("updated_at_utc", Utc(run.UpdatedAtUtc)), ("run_id", run.Id));

    private static async Task<(string RequestHash, string RunId)?> ReadIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, run_id
            FROM vyral_temporal_idempotency
            WHERE idempotency_key = @key
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task<string?> ReadStartDispatchIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT dispatch_id FROM vyral_temporal_start_outbox WHERE run_id = @run_id ORDER BY created_at_utc LIMIT 1;";
        command.Parameters.AddWithValue("run_id", runId);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    private static async Task<WaitRow?> ReadWaitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string waitId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, generation, kind, name, resume_at_utc, status
            FROM vyral_temporal_waits
            WHERE wait_id = @wait_id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("wait_id", waitId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new WaitRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToUniversalTime(), reader.GetString(5));
    }

    private static async Task<(string RunId, int Generation, string Resolution, string? EventId, long? EventRevision)?> ReadWaitOutcomeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string waitId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outcome.run_id, wait.generation, outcome.resolution, outcome.event_id, outcome.event_revision
            FROM vyral_temporal_wait_outcomes AS outcome
            INNER JOIN vyral_temporal_waits AS wait ON wait.wait_id = outcome.wait_id
            WHERE outcome.wait_id = @wait_id
            FOR UPDATE OF outcome, wait;
            """;
        command.Parameters.AddWithValue("wait_id", waitId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    private static async Task<(string DispatchId, string RunId, string WorkflowId, int Generation, long EventRevision)?> ReadSignalIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT dispatch_id, run_id, workflow_id, generation, event_revision
            FROM vyral_temporal_signal_outbox
            WHERE event_id = @event_id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4))
            : null;
    }

    private static async Task<(ExecutionExternalEvent Event, long Revision)?> ReadExternalEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_json::text, event_revision
            FROM vyral_temporal_external_events
            WHERE event_id = @event_id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (Deserialize<ExecutionExternalEvent>(reader.GetString(0)), reader.GetInt64(1))
            : null;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            if (value is TypedDatabaseNull typedNull)
            {
                var parameter = command.Parameters.Add(name, typedNull.Type);
                parameter.Value = DBNull.Value;
            }
            else
            {
                command.Parameters.AddWithValue(name, value ?? throw new InvalidOperationException(
                    $"SQL parameter '{name}' requires an explicit PostgreSQL type when null."));
            }
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static bool SameStart(RunRow row, TemporalProjectionRunStart start) =>
        row.Generation == start.Generation && row.ProjectionRevision == start.ProjectionRevision &&
        string.Equals(row.WorkflowId, start.WorkflowId, StringComparison.Ordinal) &&
        string.Equals(row.Run.HandlerId, start.Run.HandlerId, StringComparison.Ordinal) &&
        string.Equals(row.Run.PayloadHash, start.Run.PayloadHash, StringComparison.Ordinal) &&
        string.Equals(row.Run.IdempotencyKey, start.Run.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(row.RequestHash, start.RequestHash, StringComparison.Ordinal);

    private static bool SameWait(WaitRow row, TemporalProjectionWaitRegistration wait) =>
        row.RunId == wait.RunId && row.Generation == wait.Generation && row.Kind == wait.Kind &&
        row.Name == wait.Name && row.ResumeAtUtc == wait.ResumeAtUtc?.ToUniversalTime();

    private static bool ResolutionMatchesWait(WaitRow wait, TemporalExecutionWaitResolution resolution) =>
        wait.Kind switch
        {
            "timer" => resolution.Resolution == "timer" && resolution.EventId is null && resolution.EventRevision is null,
            "external_event" =>
                (resolution.Resolution == "external_event" && resolution.EventId is not null && resolution.EventRevision > 0) ||
                (resolution.Resolution == "timeout" && resolution.EventId is null && resolution.EventRevision is null),
            _ => false
        };

    private static void ValidateStart(TemporalProjectionRunStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(start.Run);
        ValidateText(start.Run.Id, "Temporal projection run id", 200);
        ValidateText(start.Run.HandlerId, "Temporal projection handler id", 160);
        ValidateText(start.WorkflowId, "Temporal projection workflow id", 255);
        ValidateText(start.DispatchId, "Temporal projection dispatch id", 200);
        if (start.Generation < 1 || start.ProjectionRevision < 1)
            throw new InvalidOperationException("Temporal projection generation and revision must be positive.");
        if (string.IsNullOrWhiteSpace(start.RequestHash) || start.RequestHash.Length != 64 ||
            start.RequestHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Temporal projection request hash must be a SHA-256 hex digest.");
        if (start.Run.CreatedAtUtc.Kind == DateTimeKind.Unspecified || start.Run.UpdatedAtUtc.Kind == DateTimeKind.Unspecified)
            throw new InvalidOperationException("Temporal projection run timestamps must include a time zone.");
    }

    private static void ValidateWait(TemporalProjectionWaitRegistration wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        ValidateText(wait.RunId, "Temporal projection wait run id", 200);
        ValidateText(wait.WaitId, "Temporal projection wait id", 200);
        ValidateText(wait.Name, "Temporal projection wait name", 160);
        if (wait.Generation < 1 || wait.Kind is not ("external_event" or "timer"))
            throw new InvalidOperationException("Temporal projection wait generation or kind is invalid.");
        if (wait.Kind == "timer" && !wait.ResumeAtUtc.HasValue)
            throw new InvalidOperationException("Temporal projection timer wait requires a resume time.");
    }

    private static void ValidateEvent(TemporalProjectionExternalEventWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Event);
        ValidateText(write.Event.Id, "Temporal projection event id", 200);
        ValidateText(write.Event.RunId, "Temporal projection event run id", 200);
        ValidateText(write.Event.Name, "Temporal projection event name", 160);
        ValidateText(write.WorkflowId, "Temporal projection event workflow id", 255);
        ValidateText(write.DispatchId, "Temporal projection signal dispatch id", 200);
        if (write.EventRevision < 1 || write.Generation < 1)
            throw new InvalidOperationException("Temporal projection event revision and generation must be positive.");
    }

    private static void ValidateResolution(TemporalExecutionWaitResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ValidateText(resolution.RunId, "Temporal projection resolution run id", 200);
        ValidateText(resolution.WaitId, "Temporal projection resolution wait id", 200);
        if (resolution.Generation < 1 || resolution.Resolution is not ("external_event" or "timer" or "timeout"))
            throw new InvalidOperationException("Temporal projection wait resolution is invalid.");
        if (resolution.Resolution == "external_event")
        {
            ValidateText(resolution.EventId, "Temporal projection resolution event id", 200);
            if (resolution.EventRevision < 1) throw new InvalidOperationException("Temporal projection event revision is invalid.");
        }
        else if (resolution.EventId is not null || resolution.EventRevision is not null)
        {
            throw new InvalidOperationException("Temporal timer and timeout resolutions cannot contain an event identity.");
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static void ValidateText(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new InvalidOperationException($"{name} must be 1-{maximumLength} non-control characters.");
    }

    private static void ValidateOutboxTable(string table)
    {
        if (table is not (
            "vyral_temporal_start_outbox" or
            "vyral_temporal_signal_outbox" or
            "vyral_temporal_cancellation_outbox"))
            throw new InvalidOperationException("Temporal outbox table is invalid.");
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private static object DbValue(object? value, NpgsqlDbType nullType) => value ?? new TypedDatabaseNull(nullType);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static bool JsonEquivalent<T>(T left, T right) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(left, JsonOptions),
        JsonSerializer.SerializeToNode(right, JsonOptions));
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException($"Temporal projection JSON did not contain {typeof(T).Name}.");
    private static T Clone<T>(T value) => Deserialize<T>(Serialize(value));

    private sealed record RunRow(
        ExecutionRun Run,
        string WorkflowId,
        int Generation,
        long ProjectionRevision,
        string? ActiveWaitId,
        string RequestHash);

    private readonly record struct WaitRow(
        string RunId,
        int Generation,
        string Kind,
        string Name,
        DateTime? ResumeAtUtc,
        string Status);

    private readonly record struct TypedDatabaseNull(NpgsqlDbType Type);
}
