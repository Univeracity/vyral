using System.Data;
using System.Text.Json.Nodes;
using Npgsql;
using Vyral.Execution;

namespace Vyral.Execution.Temporal.Postgres;

public sealed partial class PostgresTemporalExecutionProjectionStore
{
    public async Task<ExecutionRun> BeginAttemptAsync(
        TemporalExecutionAttemptRequest request,
        ExecutionTraceEvent startedEvent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.RunId, "Temporal attempt run id", 200);
        if (request.Generation < 1 || request.Attempt < 1)
            throw new InvalidOperationException("Temporal attempt generation and number must be positive.");
        ValidateTrace(startedEvent, request.RunId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, request.RunId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal attempt run was not found.");
            if (row.Generation != request.Generation)
                throw new InvalidOperationException("Temporal attempt generation is stale.");
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status) || row.Run.CancellationRequested)
            {
                await transaction.CommitAsync(ct);
                return Clone(row.Run);
            }
            if (row.Run.Status == ExecutionRunStatuses.Running && row.Run.Attempt == request.Attempt)
            {
                await transaction.CommitAsync(ct);
                return Clone(row.Run);
            }
            if (request.Attempt != row.Run.Attempt + 1 ||
                row.Run.Status is not (ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting))
            {
                throw new InvalidOperationException("Temporal attempt state does not match the next durable run attempt.");
            }

            ExecutionRunLifecycle.EnsureTransition(row.Run.Status, ExecutionRunStatuses.Running);
            var now = DateTime.UtcNow;
            row.Run.Status = ExecutionRunStatuses.Running;
            row.Run.Attempt = request.Attempt;
            row.Run.StartedAtUtc ??= now;
            row.Run.ScheduledAtUtc = null;
            row.Run.UpdatedAtUtc = now;
            await UpdateRunAsync(
                connection,
                transaction,
                row.Run,
                row.ProjectionRevision + 1,
                row.ActiveWaitId,
                row.Run.CancellationRequested,
                ct);
            await InsertHistoryAsync(
                connection,
                transaction,
                NormalizeTrace(startedEvent, row.Run, ExecutionEventTypes.RunStarted),
                ct);
            await transaction.CommitAsync(ct);
            return Clone(row.Run);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TemporalExecutionAttemptOutcome?> GetAttemptOutcomeAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.RunId, "Temporal attempt replay run id", 200);
        if (request.Generation < 1 || request.Attempt < 1)
            throw new InvalidOperationException("Temporal attempt replay generation and number must be positive.");
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, request.RunId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal attempt replay run was not found.");
            if (row.Generation != request.Generation)
                throw new InvalidOperationException("Temporal attempt replay generation is stale.");
            if (row.Run.Attempt != request.Attempt)
            {
                await transaction.CommitAsync(ct);
                return null;
            }
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status))
            {
                await transaction.CommitAsync(ct);
                return new TemporalExecutionAttemptOutcome
                {
                    Disposition = row.Run.Status == ExecutionRunStatuses.Succeeded
                        ? TemporalAttemptDispositions.Completed
                        : TemporalAttemptDispositions.Terminal,
                    TerminalStatus = row.Run.Status
                };
            }
            if (row.Run.Status == ExecutionRunStatuses.Waiting && row.ActiveWaitId is not null)
            {
                var wait = await ReadWaitAsync(connection, transaction, row.ActiveWaitId, ct)
                    ?? throw new InvalidOperationException("Temporal attempt replay active wait was not found.");
                if (wait.Status != "waiting")
                    throw new InvalidOperationException("Temporal attempt replay active wait is not waiting.");
                await transaction.CommitAsync(ct);
                return new TemporalExecutionAttemptOutcome
                {
                    Disposition = TemporalAttemptDispositions.Suspended,
                    WaitId = row.ActiveWaitId,
                    WaitKind = wait.Kind,
                    ResumeAtUtc = wait.ResumeAtUtc
                };
            }
            if (row.Run.Status == ExecutionRunStatuses.Waiting && row.Run.ScheduledAtUtc.HasValue)
            {
                var remaining = Math.Clamp(
                    (int)Math.Ceiling((row.Run.ScheduledAtUtc.Value - DateTime.UtcNow).TotalMilliseconds),
                    1,
                    86_400_000);
                await transaction.CommitAsync(ct);
                return new TemporalExecutionAttemptOutcome
                {
                    Disposition = TemporalAttemptDispositions.Retryable,
                    RetryDelayMilliseconds = remaining
                };
            }

            await transaction.CommitAsync(ct);
            return null;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionRun> ReportRunAsync(
        string runId,
        int generation,
        ExecutionRunUpdate update,
        ExecutionTraceEvent statusEvent,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal report run id", 200);
        if (generation < 1) throw new InvalidOperationException("Temporal report generation is invalid.");
        ExecutionContractValidator.ValidateRunUpdate(update);
        if (!string.IsNullOrWhiteSpace(update.Status) && update.Status != ExecutionRunStatuses.Running)
            throw new InvalidOperationException("Temporal activity progress may only report the running status.");
        ValidateTrace(statusEvent, runId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, runId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal report run was not found.");
            if (row.Generation != generation)
                throw new InvalidOperationException("Temporal report generation is stale.");
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status))
            {
                await transaction.CommitAsync(ct);
                return Clone(row.Run);
            }
            if (row.Run.Status != ExecutionRunStatuses.Running)
                throw new InvalidOperationException("Temporal activity progress requires a running run.");

            ApplyUpdate(row.Run, update);
            row.Run.UpdatedAtUtc = DateTime.UtcNow;
            await UpdateRunAsync(
                connection,
                transaction,
                row.Run,
                row.ProjectionRevision + 1,
                row.ActiveWaitId,
                row.Run.CancellationRequested,
                ct);
            await InsertHistoryAsync(
                connection,
                transaction,
                NormalizeTrace(statusEvent, row.Run, ExecutionEventTypes.RunStatus),
                ct);
            await transaction.CommitAsync(ct);
            return Clone(row.Run);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RecordHistoryAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        ValidateTrace(traceEvent, traceEvent.RunId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            if (!await RunExistsAsync(connection, transaction, traceEvent.RunId, ct))
                throw new InvalidOperationException("Temporal trace run was not found.");
            await InsertHistoryAsync(connection, transaction, traceEvent, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionArtifact> PutArtifactMetadataAsync(
        string runId,
        int generation,
        ExecutionArtifact artifact,
        ExecutionTraceEvent writtenEvent,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal artifact run id", 200);
        if (generation < 1) throw new InvalidOperationException("Temporal artifact generation is invalid.");
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateText(artifact.Id, "Temporal artifact id", 200);
        ValidateText(artifact.Name, "Temporal artifact name", 160);
        if (!string.Equals(runId, artifact.RunId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(artifact.ContentHash) || artifact.SizeBytes < 0)
        {
            throw new InvalidOperationException("Temporal artifact metadata is invalid.");
        }
        ValidateTrace(writtenEvent, runId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, runId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal artifact run was not found.");
            if (row.Generation != generation || ExecutionRunStatuses.IsTerminal(row.Run.Status))
                throw new InvalidOperationException("Temporal artifact run generation or state is stale.");

            var inserted = await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_artifacts
                    (artifact_id, run_id, name, content_hash, metadata_json, created_at_utc)
                VALUES
                    (@artifact_id, @run_id, @name, @content_hash, @metadata_json::jsonb, @created_at_utc)
                ON CONFLICT DO NOTHING;
                """, ct,
                ("artifact_id", artifact.Id), ("run_id", runId), ("name", artifact.Name),
                ("content_hash", artifact.ContentHash), ("metadata_json", Serialize(artifact)),
                ("created_at_utc", Utc(artifact.CreatedAtUtc)));
            if (inserted == 0)
            {
                var existing = await ReadArtifactForUpdateAsync(connection, transaction, runId, artifact.Name, ct);
                if (existing is null ||
                    !string.Equals(existing.ContentHash, artifact.ContentHash, StringComparison.Ordinal) ||
                    !string.Equals(existing.Kind, artifact.Kind, StringComparison.Ordinal) ||
                    !string.Equals(existing.MediaType, artifact.MediaType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Temporal artifact name conflicts with existing metadata.");
                }
                await transaction.CommitAsync(ct);
                return existing;
            }
            await InsertHistoryAsync(
                connection,
                transaction,
                NormalizeTrace(writtenEvent, row.Run, ExecutionEventTypes.ArtifactWritten),
                ct);
            await transaction.CommitAsync(ct);
            return Clone(artifact);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionCheckpoint> PutCheckpointAsync(
        string runId,
        int generation,
        ExecutionCheckpoint checkpoint,
        ExecutionTraceEvent writtenEvent,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal checkpoint run id", 200);
        if (generation < 1) throw new InvalidOperationException("Temporal checkpoint generation is invalid.");
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateText(checkpoint.Key, "Temporal checkpoint key", 160);
        if (!string.Equals(runId, checkpoint.RunId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(checkpoint.ContentHash))
        {
            throw new InvalidOperationException("Temporal checkpoint metadata is invalid.");
        }
        ValidateTrace(writtenEvent, runId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, runId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal checkpoint run was not found.");
            if (row.Generation != generation || ExecutionRunStatuses.IsTerminal(row.Run.Status))
                throw new InvalidOperationException("Temporal checkpoint run generation or state is stale.");
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_checkpoints
                    (run_id, checkpoint_key, content_hash, checkpoint_json, updated_at_utc)
                VALUES
                    (@run_id, @checkpoint_key, @content_hash, @checkpoint_json::jsonb, @updated_at_utc)
                ON CONFLICT (run_id, checkpoint_key) DO UPDATE SET
                    content_hash = EXCLUDED.content_hash,
                    checkpoint_json = EXCLUDED.checkpoint_json,
                    updated_at_utc = EXCLUDED.updated_at_utc;
                """, ct,
                ("run_id", runId), ("checkpoint_key", checkpoint.Key),
                ("content_hash", checkpoint.ContentHash), ("checkpoint_json", Serialize(checkpoint)),
                ("updated_at_utc", Utc(checkpoint.UpdatedAtUtc)));
            await InsertHistoryAsync(
                connection,
                transaction,
                NormalizeTrace(writtenEvent, row.Run, ExecutionEventTypes.CheckpointWritten),
                ct);
            await transaction.CommitAsync(ct);
            return Clone(checkpoint);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionWaitResult?> ConsumeWaitResultAsync(
        string runId,
        int generation,
        int attempt,
        string kind,
        string name,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal wait result run id", 200);
        ValidateText(name, "Temporal wait result name", 160);
        if (generation < 1 || attempt < 1 || kind is not ("external_event" or "timer"))
            throw new InvalidOperationException("Temporal wait result generation, attempt, or kind is invalid.");
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT outcome.resolution, outcome.event_id, outcome.event_revision, wait.resume_at_utc
                FROM vyral_temporal_wait_outcomes AS outcome
                INNER JOIN vyral_temporal_waits AS wait ON wait.wait_id = outcome.wait_id
                WHERE outcome.run_id = @run_id
                  AND wait.generation = @generation
                  AND wait.kind = @kind
                  AND wait.name = @name
                  AND outcome.consumed_at_utc IS NULL
                  AND (outcome.claimed_attempt IS NULL OR outcome.claimed_attempt = @attempt)
                ORDER BY outcome.resolved_at_utc, outcome.wait_id
                LIMIT 1
                FOR UPDATE OF outcome, wait;
                """;
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("generation", generation);
            command.Parameters.AddWithValue("attempt", attempt);
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("name", name);
            string? resolution = null;
            string? eventId = null;
            DateTime? resumeAt = null;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    resolution = reader.GetString(0);
                    eventId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    resumeAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToUniversalTime();
                }
            }
            if (resolution is null)
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            ExecutionExternalEvent? externalEvent = null;
            ExecutionTimer? timer = null;
            if (resolution == "external_event")
            {
                var persisted = await ReadExternalEventAsync(connection, transaction, eventId!, ct)
                    ?? throw new InvalidOperationException("Temporal wait outcome event was not found.");
                externalEvent = persisted.Event;
            }
            else if (resolution == "timer")
            {
                timer = await ReadTimerAsync(connection, transaction, runId, name, resumeAt, ct)
                    ?? throw new InvalidOperationException("Temporal wait outcome timer was not found.");
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE vyral_temporal_wait_outcomes
                SET claimed_attempt = @attempt
                WHERE wait_id = (
                    SELECT outcome.wait_id
                    FROM vyral_temporal_wait_outcomes AS outcome
                    INNER JOIN vyral_temporal_waits AS wait ON wait.wait_id = outcome.wait_id
                    WHERE outcome.run_id = @run_id
                      AND wait.generation = @generation
                      AND wait.kind = @kind
                      AND wait.name = @name
                      AND outcome.consumed_at_utc IS NULL
                      AND (outcome.claimed_attempt IS NULL OR outcome.claimed_attempt = @attempt)
                    ORDER BY outcome.resolved_at_utc, outcome.wait_id
                    LIMIT 1
                );
                """, ct,
                ("run_id", runId), ("generation", generation), ("attempt", attempt),
                ("kind", kind), ("name", name));
            await transaction.CommitAsync(ct);
            return new ExecutionWaitResult
            {
                Name = name,
                Outcome = resolution == "timeout" ? ExecutionWaitOutcomes.TimedOut : resolution,
                Event = externalEvent is null ? null : Clone(externalEvent),
                Timer = timer is null ? null : Clone(timer)
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TemporalProjectionAttemptCompletion> CompleteAttemptAsync(
        string runId,
        int generation,
        ExecutionRunResult result,
        ExecutionTraceEvent retryEvent,
        ExecutionTraceEvent terminalEvent,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal completion run id", 200);
        if (generation < 1) throw new InvalidOperationException("Temporal completion generation is invalid.");
        ExecutionContractValidator.ValidateRunResult(result);
        ValidateTrace(retryEvent, runId);
        ValidateTrace(terminalEvent, runId);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, runId, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal completion run was not found.");
            if (row.Generation != generation)
                throw new InvalidOperationException("Temporal completion generation is stale.");
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status))
            {
                await transaction.CommitAsync(ct);
                return new TemporalProjectionAttemptCompletion { Run = Clone(row.Run) };
            }
            if (row.Run.Status == ExecutionRunStatuses.Waiting && row.Run.Attempt > 0 && row.Run.ScheduledAtUtc.HasValue)
            {
                var remaining = Math.Max(1, (int)Math.Ceiling(
                    (row.Run.ScheduledAtUtc.Value - DateTime.UtcNow).TotalMilliseconds));
                await transaction.CommitAsync(ct);
                return new TemporalProjectionAttemptCompletion
                {
                    Run = Clone(row.Run),
                    RetryDelayMilliseconds = remaining
                };
            }
            if (row.Run.Status != ExecutionRunStatuses.Running)
                throw new InvalidOperationException("Temporal completion requires a running run.");

            var effective = row.Run.CancellationRequested
                ? ExecutionRunResult.Cancelled(result.Result)
                : result;
            var terminalStatus = ExecutionRunStatuses.IsTerminal(effective.Status)
                ? effective.Status
                : ExecutionRunStatuses.Failed;
            row.Run.Result = effective.Result?.DeepClone() ?? row.Run.Result;
            row.Run.StatusDetails = effective.StatusDetails?.DeepClone() as JsonObject ?? row.Run.StatusDetails;
            row.Run.FailureClass = terminalStatus == ExecutionRunStatuses.Succeeded
                ? null
                : effective.FailureClass ?? row.Run.FailureClass;
            row.Run.Error = terminalStatus == ExecutionRunStatuses.Succeeded
                ? null
                : effective.Error ?? row.Run.Error;
            row.Run.CancellationRequested |= terminalStatus == ExecutionRunStatuses.Cancelled;

            int? retryDelayMilliseconds = null;
            ExecutionTraceEvent trace;
            if (!row.Run.CancellationRequested &&
                terminalStatus is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut &&
                row.Run.Attempt < Math.Max(1, row.Run.MaxAttempts))
            {
                var retryDelay = CalculateRetryDelay(row.Run);
                retryDelayMilliseconds = Math.Clamp((int)Math.Ceiling(retryDelay.TotalMilliseconds), 1, 86_400_000);
                ExecutionRunLifecycle.EnsureTransition(row.Run.Status, terminalStatus);
                ExecutionRunLifecycle.EnsureTransition(
                    terminalStatus,
                    ExecutionRunStatuses.Waiting,
                    ExecutionTransitionKind.Retry);
                row.Run.Status = ExecutionRunStatuses.Waiting;
                row.Run.ScheduledAtUtc = DateTime.UtcNow.AddMilliseconds(retryDelayMilliseconds.Value);
                row.Run.UpdatedAtUtc = DateTime.UtcNow;
                row.Run.CurrentStep = null;
                trace = NormalizeTrace(retryEvent, row.Run, ExecutionEventTypes.RetryScheduled);
            }
            else
            {
                ExecutionRunLifecycle.EnsureTransition(row.Run.Status, terminalStatus);
                row.Run.Status = terminalStatus;
                CompleteTiming(row.Run);
                trace = NormalizeTrace(
                    terminalEvent,
                    row.Run,
                    terminalStatus == ExecutionRunStatuses.Failed
                        ? ExecutionEventTypes.RunFailed
                        : ExecutionEventTypes.RunCompleted);
            }

            await UpdateRunAsync(
                connection,
                transaction,
                row.Run,
                row.ProjectionRevision + 1,
                activeWaitId: null,
                row.Run.CancellationRequested,
                ct);
            await ConsumeClaimedWaitOutcomesAsync(
                connection,
                transaction,
                row.Run.Id,
                row.Run.Attempt,
                ct);
            await InsertHistoryAsync(connection, transaction, trace, ct);
            await transaction.CommitAsync(ct);
            return new TemporalProjectionAttemptCompletion
            {
                Run = Clone(row.Run),
                RetryDelayMilliseconds = retryDelayMilliseconds
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ConsumeClaimedWaitOutcomesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        int attempt,
        CancellationToken ct)
    {
        if (attempt < 1) return;
        _ = await ExecuteAsync(connection, transaction, """
            UPDATE vyral_temporal_wait_outcomes
            SET consumed_at_utc = COALESCE(consumed_at_utc, CURRENT_TIMESTAMP)
            WHERE run_id = @run_id
              AND claimed_attempt = @attempt
              AND consumed_at_utc IS NULL;
            """, ct, ("run_id", runId), ("attempt", attempt));
    }

    private static async Task InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionTraceEvent traceEvent,
        CancellationToken ct)
    {
        var inserted = await ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_temporal_history
                (event_id, run_id, sequence_id, event_json, created_at_utc)
            VALUES
                (@event_id, @run_id, @sequence_id, @event_json::jsonb, @created_at_utc)
            ON CONFLICT (event_id) DO NOTHING;
            """, ct,
            ("event_id", traceEvent.Id), ("run_id", traceEvent.RunId),
            ("sequence_id", traceEvent.SequenceId), ("event_json", Serialize(traceEvent)),
            ("created_at_utc", Utc(traceEvent.TimestampUtc)));
        if (inserted != 0) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_json::text FROM vyral_temporal_history WHERE event_id = @event_id FOR UPDATE;";
        command.Parameters.AddWithValue("event_id", traceEvent.Id);
        var existingJson = (string?)await command.ExecuteScalarAsync(ct);
        if (existingJson is null || !JsonEquivalent(Deserialize<ExecutionTraceEvent>(existingJson), traceEvent))
            throw new InvalidOperationException("Temporal trace event id conflicts with existing history.");
    }

    private static async Task<ExecutionArtifact?> ReadArtifactForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        string name,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT metadata_json::text
            FROM vyral_temporal_artifacts
            WHERE run_id = @run_id AND name = @name
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("name", name);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null ? null : Deserialize<ExecutionArtifact>(json);
    }

    private static async Task<ExecutionTimer?> ReadTimerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        string name,
        DateTime? resumeAt,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT timer_json::text
            FROM vyral_temporal_timers
            WHERE run_id = @run_id AND name = @name
              AND (@resume_at IS NULL OR fire_at_utc = @resume_at)
            ORDER BY fire_at_utc DESC, timer_id DESC
            LIMIT 1
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("name", name);
        AddNullableParameter(command, "resume_at", resumeAt, NpgsqlTypes.NpgsqlDbType.TimestampTz);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null ? null : Deserialize<ExecutionTimer>(json);
    }

    private static ExecutionTraceEvent NormalizeTrace(
        ExecutionTraceEvent traceEvent,
        ExecutionRun run,
        string type)
    {
        var normalized = Clone(traceEvent);
        normalized.RunId = run.Id;
        normalized.Type = type;
        normalized.Attempt = run.Attempt;
        normalized.Status = run.Status;
        normalized.TimestampUtc = DateTime.UtcNow;
        return normalized;
    }

    private static void ValidateTrace(ExecutionTraceEvent traceEvent, string runId)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        ValidateText(traceEvent.Id, "Temporal trace event id", 200);
        ValidateText(traceEvent.SequenceId, "Temporal trace sequence id", 200);
        ValidateText(traceEvent.RunId, "Temporal trace run id", 200);
        ValidateText(traceEvent.Type, "Temporal trace type", 160);
        ValidateText(traceEvent.Severity, "Temporal trace severity", 160);
        if (!string.Equals(traceEvent.RunId, runId, StringComparison.Ordinal) ||
            traceEvent.TimestampUtc.Kind == DateTimeKind.Unspecified)
        {
            throw new InvalidOperationException("Temporal trace identity or timestamp is invalid.");
        }
    }

    private static void ApplyUpdate(ExecutionRun run, ExecutionRunUpdate update)
    {
        run.Requested = update.Requested ?? run.Requested;
        run.Attempted = update.Attempted ?? run.Attempted;
        run.Succeeded = update.Succeeded ?? run.Succeeded;
        run.Failed = update.Failed ?? run.Failed;
        run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
        run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
        run.FailureClass = update.FailureClass ?? run.FailureClass;
        run.Error = update.Error ?? run.Error;
        run.Result = update.Result?.DeepClone() ?? run.Result;
        run.StatusDetails = update.StatusDetails?.DeepClone() as JsonObject ?? run.StatusDetails;
    }

    private static TimeSpan CalculateRetryDelay(ExecutionRun run)
    {
        var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
        var initial = Math.Max(0, policy.InitialDelaySeconds);
        var maximum = Math.Max(initial, policy.MaxDelaySeconds);
        var multiplier = policy.BackoffMultiplier <= 0 ? 1 : policy.BackoffMultiplier;
        var seconds = initial * Math.Pow(multiplier, Math.Max(0, run.Attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(maximum, seconds));
    }

    private static void CompleteTiming(ExecutionRun run)
    {
        var completedAt = DateTime.UtcNow;
        run.StartedAtUtc ??= completedAt;
        run.CompletedAtUtc = completedAt;
        run.UpdatedAtUtc = completedAt;
        run.DurationMs = Math.Max(0, (completedAt - run.StartedAtUtc.Value).TotalMilliseconds);
        run.CurrentStep = null;
        run.ScheduledAtUtc = null;
        if (run.Status == ExecutionRunStatuses.Succeeded) run.Progress = 1;
    }
}
