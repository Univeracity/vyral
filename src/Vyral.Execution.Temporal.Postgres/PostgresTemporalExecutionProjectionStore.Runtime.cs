using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.Temporal.Postgres;

public sealed partial class PostgresTemporalExecutionProjectionStore
{
    public async Task<TemporalProjectionRunCreationResult> CreateRunWithoutPendingStartAsync(
        TemporalProjectionRunCreation creation,
        CancellationToken ct = default)
    {
        ValidateCreation(creation);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(creation.Run.IdempotencyKey))
            {
                await ExecuteAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", ct,
                    ("key", creation.Run.IdempotencyKey));
                var receipt = await ReadIdempotencyAsync(
                    connection,
                    transaction,
                    creation.Run.IdempotencyKey!,
                    ct);
                if (receipt is not null)
                {
                    if (!string.Equals(receipt.Value.RequestHash, creation.RequestHash, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Temporal projection idempotency key belongs to a different run request.");
                    var replayed = await ReadRunRowAsync(
                        connection,
                        transaction,
                        receipt.Value.RunId,
                        lockForUpdate: true,
                        ct) ?? throw new InvalidOperationException("Temporal projection idempotency receipt has no run.");
                    await transaction.CommitAsync(ct);
                    return new TemporalProjectionRunCreationResult
                    {
                        Run = Clone(replayed.Run),
                        Replayed = true
                    };
                }
            }

            var existing = await ReadRunRowAsync(
                connection,
                transaction,
                creation.Run.Id,
                lockForUpdate: true,
                ct);
            if (existing is not null)
            {
                if (!SameCreation(existing, creation))
                    throw new InvalidOperationException("Temporal projection run identity conflicts with an existing run.");
                await transaction.CommitAsync(ct);
                return new TemporalProjectionRunCreationResult
                {
                    Run = Clone(existing.Run),
                    Replayed = true
                };
            }

            await InsertRunAsync(
                connection,
                transaction,
                creation.Run,
                creation.WorkflowId,
                creation.Generation,
                creation.ProjectionRevision,
                creation.RequestHash,
                ct);
            await InsertHistoryAsync(connection, transaction, new ExecutionTraceEvent
            {
                RunId = creation.Run.Id,
                Type = ExecutionEventTypes.RunRejected,
                TimestampUtc = creation.Run.UpdatedAtUtc,
                Attempt = creation.Run.Attempt,
                Status = creation.Run.Status,
                Severity = "warning",
                Message = creation.Run.Error ?? "Execution run was rejected."
            }, ct);
            if (!string.IsNullOrWhiteSpace(creation.Run.IdempotencyKey))
            {
                await InsertIdempotencyAsync(
                    connection,
                    transaction,
                    creation.Run.IdempotencyKey!,
                    creation.RequestHash,
                    creation.Run.Id,
                    ct);
            }

            await transaction.CommitAsync(ct);
            return new TemporalProjectionRunCreationResult
            {
                Run = Clone(creation.Run),
                Replayed = false
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionRun?> GetRunAsync(
        string runId,
        bool includeResult = true,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal projection run id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json::text FROM vyral_temporal_runs WHERE run_id = @run_id;";
        command.Parameters.AddWithValue("run_id", runId);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        if (json is null) return null;
        var run = Deserialize<ExecutionRun>(json);
        if (!includeResult) run.Result = null;
        return run;
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(
        ExecutionRunQuery? query = null,
        CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        var limit = query.Limit ?? 100;
        ValidateLimit(limit);
        ValidateOptionalQueryText(query.HandlerId, "Temporal run handler id", 200);
        ValidateOptionalQueryText(query.PluginId, "Temporal run plugin id", 200);
        ValidateOptionalQueryText(query.CorrelationId, "Temporal run correlation id", 200);
        ValidateOptionalQueryText(query.IdempotencyKey, "Temporal run idempotency key", 200);
        if (!string.IsNullOrWhiteSpace(query.Status) && !ExecutionRunStatuses.IsKnown(query.Status))
            throw new InvalidOperationException("Temporal run query status is invalid.");
        ValidateQueryTimestamp(query.CreatedAfterUtc, "created-after");
        ValidateQueryTimestamp(query.CreatedBeforeUtc, "created-before");
        ValidateQueryTimestamp(query.UpdatedAfterUtc, "updated-after");
        ValidateQueryTimestamp(query.UpdatedBeforeUtc, "updated-before");
        if (query.Tags.Count > 100)
            throw new InvalidOperationException("Temporal run query cannot contain more than 100 tags.");

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_json::text
            FROM vyral_temporal_runs
            WHERE (@handler_id IS NULL OR run_json ->> 'handlerId' = @handler_id)
              AND (@plugin_id IS NULL OR run_json ->> 'pluginId' = @plugin_id)
              AND (@status IS NULL OR status = @status)
              AND (@correlation_id IS NULL OR run_json ->> 'correlationId' = @correlation_id)
              AND (@idempotency_key IS NULL OR idempotency_key = @idempotency_key)
              AND (@created_after IS NULL OR created_at_utc >= @created_after)
              AND (@created_before IS NULL OR created_at_utc <= @created_before)
              AND (@updated_after IS NULL OR updated_at_utc >= @updated_after)
              AND (@updated_before IS NULL OR updated_at_utc <= @updated_before)
              AND (@tags IS NULL OR (run_json -> 'tags') @> @tags)
            ORDER BY created_at_utc DESC, run_id DESC
            LIMIT @limit;
            """;
        AddNullableParameter(command, "handler_id", query.HandlerId?.Trim(), NpgsqlDbType.Text);
        AddNullableParameter(command, "plugin_id", query.PluginId?.Trim(), NpgsqlDbType.Text);
        AddNullableParameter(command, "status", query.Status?.Trim(), NpgsqlDbType.Text);
        AddNullableParameter(command, "correlation_id", query.CorrelationId?.Trim(), NpgsqlDbType.Text);
        AddNullableParameter(command, "idempotency_key", query.IdempotencyKey?.Trim(), NpgsqlDbType.Text);
        AddNullableParameter(command, "created_after", query.CreatedAfterUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        AddNullableParameter(command, "created_before", query.CreatedBeforeUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        AddNullableParameter(command, "updated_after", query.UpdatedAfterUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        AddNullableParameter(command, "updated_before", query.UpdatedBeforeUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        AddNullableParameter(
            command,
            "tags",
            query.Tags.Count == 0 ? null : JsonSerializer.Serialize(query.Tags, JsonOptions),
            NpgsqlDbType.Jsonb);
        command.Parameters.AddWithValue("limit", limit);

        var results = new List<ExecutionRun>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var run = Deserialize<ExecutionRun>(reader.GetString(0));
            if (!query.IncludeResult) run.Result = null;
            results.Add(run);
        }
        return results;
    }

    public async Task<TemporalActiveCoordinatorSnapshot> GetActiveCoordinatorSnapshotAsync(
        int limit,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH active AS (
                SELECT runs.workflow_id, runs.generation, runs.updated_at_utc, runs.run_id
                FROM vyral_temporal_runs AS runs
                WHERE runs.status NOT IN ('succeeded', 'failed', 'cancelled', 'rejected', 'timed_out')
                  AND EXISTS (
                      SELECT 1
                      FROM vyral_temporal_start_outbox AS starts
                      WHERE starts.run_id = runs.run_id
                        AND starts.delivered_at_utc IS NOT NULL)
            )
            SELECT workflow_id, generation, (SELECT count(*)::integer FROM active)
            FROM active
            ORDER BY updated_at_utc, run_id
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", limit);

        var results = new List<TemporalActiveCoordinator>();
        var totalCount = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            totalCount = reader.GetInt32(2);
            results.Add(new TemporalActiveCoordinator
            {
                WorkflowId = reader.GetString(0),
                Generation = reader.GetInt32(1)
            });
        }
        return new TemporalActiveCoordinatorSnapshot
        {
            TotalCount = totalCount,
            Coordinators = results
        };
    }

    public async Task<bool> IsActiveCoordinatorAsync(
        string workflowId,
        int generation,
        CancellationToken ct = default)
    {
        ValidateText(workflowId, "Temporal workflow id", 255);
        if (generation < 1)
            throw new InvalidOperationException("Temporal coordinator generation must be positive.");
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM vyral_temporal_runs AS runs
                WHERE runs.workflow_id = @workflow_id
                  AND runs.generation = @generation
                  AND runs.status NOT IN ('succeeded', 'failed', 'cancelled', 'rejected', 'timed_out')
                  AND EXISTS (
                      SELECT 1
                      FROM vyral_temporal_start_outbox AS starts
                      WHERE starts.run_id = runs.run_id
                        AND starts.delivered_at_utc IS NOT NULL));
            """;
        command.Parameters.AddWithValue("workflow_id", workflowId);
        command.Parameters.AddWithValue("generation", generation);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(
        string runId,
        int limit,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal history run id", 200);
        ValidateLimit(limit);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_json::text
            FROM vyral_temporal_history
            WHERE run_id = @run_id
            ORDER BY sequence_id, event_id
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("limit", limit);
        var results = new List<ExecutionTraceEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(Deserialize<ExecutionTraceEvent>(reader.GetString(0)));
        return results;
    }

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(
        string runId,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal artifact run id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata_json::text
            FROM vyral_temporal_artifacts
            WHERE run_id = @run_id
            ORDER BY created_at_utc, artifact_id;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        var results = new List<ExecutionArtifact>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(Deserialize<ExecutionArtifact>(reader.GetString(0)));
        return results;
    }

    public async Task<ExecutionArtifact?> GetArtifactAsync(
        string runId,
        string artifactRef,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal artifact run id", 200);
        ValidateText(artifactRef, "Temporal artifact reference", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata_json::text
            FROM vyral_temporal_artifacts
            WHERE run_id = @run_id AND (artifact_id = @artifact_ref OR name = @artifact_ref)
            ORDER BY CASE WHEN artifact_id = @artifact_ref THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("artifact_ref", artifactRef);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null ? null : Deserialize<ExecutionArtifact>(json);
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(
        string runId,
        string key,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal checkpoint run id", 200);
        ValidateText(key, "Temporal checkpoint key", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT checkpoint_json::text
            FROM vyral_temporal_checkpoints
            WHERE run_id = @run_id AND checkpoint_key = @checkpoint_key;
            """;
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("checkpoint_key", key);
        var json = (string?)await command.ExecuteScalarAsync(ct);
        return json is null ? null : Deserialize<ExecutionCheckpoint>(json);
    }

    public async Task<TemporalProjectionCancellationRequestResult> RequestCancellationAsync(
        string runId,
        CancellationToken ct = default)
    {
        ValidateText(runId, "Temporal cancellation run id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, runId, lockForUpdate: true, ct);
            if (row is null)
            {
                await transaction.CommitAsync(ct);
                return new TemporalProjectionCancellationRequestResult { NewlyRequested = false };
            }
            if (ExecutionRunStatuses.IsTerminal(row.Run.Status))
            {
                await transaction.CommitAsync(ct);
                return new TemporalProjectionCancellationRequestResult
                {
                    Run = Clone(row.Run),
                    WorkflowId = row.WorkflowId,
                    Generation = row.Generation,
                    NewlyRequested = false
                };
            }

            var newlyRequested = !row.Run.CancellationRequested;
            if (newlyRequested)
            {
                var now = DateTime.UtcNow;
                row.Run.CancellationRequested = true;
                row.Run.UpdatedAtUtc = now;
                await UpdateRunAsync(
                    connection,
                    transaction,
                    row.Run,
                    row.ProjectionRevision + 1,
                    row.ActiveWaitId,
                    cancellationRequested: true,
                    ct);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_temporal_cancellation_outbox
                        (dispatch_id, run_id, workflow_id, generation, next_attempt_at_utc, created_at_utc)
                    VALUES
                        (@dispatch_id, @run_id, @workflow_id, @generation, @next_attempt_at_utc, @created_at_utc);
                    """, ct,
                    ("dispatch_id", OrderedId.CreateString()), ("run_id", row.Run.Id),
                    ("workflow_id", row.WorkflowId), ("generation", row.Generation),
                    ("next_attempt_at_utc", now), ("created_at_utc", now));
            }
            await transaction.CommitAsync(ct);
            return new TemporalProjectionCancellationRequestResult
            {
                Run = Clone(row.Run),
                WorkflowId = row.WorkflowId,
                Generation = row.Generation,
                NewlyRequested = newlyRequested
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TemporalProjectionExternalEventDispatch> CreateExternalEventWithPendingSignalAsync(
        ExecutionExternalEvent externalEvent,
        string dispatchId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(externalEvent);
        ValidateText(externalEvent.Id, "Temporal projection event id", 200);
        ValidateText(externalEvent.RunId, "Temporal projection event run id", 200);
        ValidateText(externalEvent.Name, "Temporal projection event name", 160);
        ValidateText(dispatchId, "Temporal projection signal dispatch id", 200);
        if (externalEvent.RaisedAtUtc.Kind == DateTimeKind.Unspecified)
            throw new InvalidOperationException("Temporal projection event timestamp must include a time zone.");

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await ReadRunRowAsync(connection, transaction, externalEvent.RunId!, lockForUpdate: true, ct)
                ?? throw new InvalidOperationException("Temporal projection event run was not found.");
            var terminal = ExecutionRunStatuses.IsTerminal(row.Run.Status);

            await using var revisionCommand = connection.CreateCommand();
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText = """
                SELECT COALESCE(max(event_revision), 0) + 1
                FROM vyral_temporal_external_events
                WHERE run_id = @run_id;
                """;
            revisionCommand.Parameters.AddWithValue("run_id", externalEvent.RunId!);
            var eventRevision = (long)(await revisionCommand.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("Temporal projection event revision was not allocated."));
            var now = DateTime.UtcNow;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_external_events
                    (event_id, run_id, event_revision, name, event_json, raised_at_utc)
                VALUES (@event_id, @run_id, @event_revision, @name, @event_json::jsonb, @raised_at_utc);
                """, ct,
                ("event_id", externalEvent.Id), ("run_id", externalEvent.RunId),
                ("event_revision", eventRevision), ("name", externalEvent.Name),
                ("event_json", Serialize(externalEvent)), ("raised_at_utc", Utc(externalEvent.RaisedAtUtc)));
            if (!terminal)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO vyral_temporal_signal_outbox
                        (dispatch_id, run_id, workflow_id, generation, event_id, event_revision,
                         next_attempt_at_utc, created_at_utc)
                    VALUES
                        (@dispatch_id, @run_id, @workflow_id, @generation, @event_id, @event_revision,
                         @next_attempt_at_utc, @created_at_utc);
                    """, ct,
                    ("dispatch_id", dispatchId), ("run_id", externalEvent.RunId),
                    ("workflow_id", row.WorkflowId), ("generation", row.Generation),
                    ("event_id", externalEvent.Id), ("event_revision", eventRevision),
                    ("next_attempt_at_utc", now), ("created_at_utc", now));
            }
            await InsertHistoryAsync(connection, transaction, new ExecutionTraceEvent
            {
                RunId = row.Run.Id,
                Type = ExecutionEventTypes.ExternalEventRaised,
                TimestampUtc = now,
                Attempt = row.Run.Attempt,
                Status = row.Run.Status,
                Message = $"External event '{externalEvent.Name}' raised."
            }, ct);
            await transaction.CommitAsync(ct);
            return new TemporalProjectionExternalEventDispatch
            {
                Event = Clone(externalEvent),
                EventRevision = eventRevision,
                WorkflowId = row.WorkflowId,
                Generation = row.Generation,
                DispatchId = dispatchId
            };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(
        ExecutionLeaseRequest request,
        CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateLeaseRequest(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.RunId) &&
                !await RunExistsAsync(connection, transaction, request.RunId, ct))
            {
                throw new InvalidOperationException("Temporal lease run was not found.");
            }

            var existing = await ReadLeaseAsync(connection, transaction, request.LeaseKey, ct);
            var now = DateTime.UtcNow;
            if (existing is not null && existing.Value.ExpiresAtUtc > now &&
                !string.Equals(existing.Value.OwnerId, request.OwnerId, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var acquiredAt = existing is not null && existing.Value.ExpiresAtUtc > now &&
                string.Equals(existing.Value.OwnerId, request.OwnerId, StringComparison.Ordinal)
                ? existing.Value.Lease.AcquiredAtUtc
                : now;
            var fencingToken = existing is null
                ? 1
                : existing.Value.ExpiresAtUtc > now && string.Equals(existing.Value.OwnerId, request.OwnerId, StringComparison.Ordinal)
                    ? existing.Value.FencingToken
                    : checked(existing.Value.FencingToken + 1);
            var lease = new ExecutionLease
            {
                LeaseKey = request.LeaseKey.Trim(),
                OwnerId = request.OwnerId.Trim(),
                RunId = string.IsNullOrWhiteSpace(request.RunId) ? null : request.RunId.Trim(),
                AcquiredAtUtc = acquiredAt,
                ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
                Metadata = request.Metadata?.DeepClone() as System.Text.Json.Nodes.JsonObject
            };
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_leases
                    (lease_key, owner_id, run_id, fencing_token, expires_at_utc, lease_json)
                VALUES
                    (@lease_key, @owner_id, @run_id, @fencing_token, @expires_at_utc, @lease_json::jsonb)
                ON CONFLICT (lease_key) DO UPDATE SET
                    owner_id = EXCLUDED.owner_id,
                    run_id = EXCLUDED.run_id,
                    fencing_token = EXCLUDED.fencing_token,
                    expires_at_utc = EXCLUDED.expires_at_utc,
                    lease_json = EXCLUDED.lease_json;
                """, ct,
                ("lease_key", lease.LeaseKey), ("owner_id", lease.OwnerId),
                ("run_id", DbValue(lease.RunId, NpgsqlDbType.Text)),
                ("fencing_token", fencingToken), ("expires_at_utc", lease.ExpiresAtUtc),
                ("lease_json", Serialize(lease)));
            await transaction.CommitAsync(ct);
            return lease;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> ReleaseLeaseAsync(
        string leaseKey,
        string ownerId,
        CancellationToken ct = default)
    {
        ValidateText(leaseKey, "Temporal lease key", 200);
        ValidateText(ownerId, "Temporal lease owner id", 200);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var affected = await ExecuteAsync(connection, null, """
            DELETE FROM vyral_temporal_leases
            WHERE lease_key = @lease_key AND owner_id = @owner_id;
            """, ct, ("lease_key", leaseKey), ("owner_id", ownerId));
        return affected > 0;
    }

    public async Task<ExecutionTimer> ScheduleTimerAsync(
        ExecutionTimerRequest request,
        CancellationToken ct = default)
    {
        ExecutionContractValidator.ValidateTimerRequest(request);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            RunRow? row = null;
            if (!string.IsNullOrWhiteSpace(request.RunId))
            {
                row = await ReadRunRowAsync(connection, transaction, request.RunId, lockForUpdate: true, ct);
                if (row is null) throw new InvalidOperationException("Temporal timer run was not found.");
            }

            var timer = new ExecutionTimer
            {
                Id = OrderedId.CreateString(),
                Name = request.Name.Trim(),
                RunId = string.IsNullOrWhiteSpace(request.RunId) ? null : request.RunId.Trim(),
                FireAtUtc = request.FireAtUtc.ToUniversalTime(),
                CreatedAtUtc = DateTime.UtcNow,
                Payload = request.Payload?.DeepClone()
            };
            await ExecuteAsync(connection, transaction, """
                INSERT INTO vyral_temporal_timers
                    (timer_id, run_id, name, fire_at_utc, timer_json)
                VALUES (@timer_id, @run_id, @name, @fire_at_utc, @timer_json::jsonb);
                """, ct,
                ("timer_id", timer.Id), ("run_id", DbValue(timer.RunId, NpgsqlDbType.Text)),
                ("name", timer.Name), ("fire_at_utc", timer.FireAtUtc),
                ("timer_json", Serialize(timer)));
            if (row is not null)
            {
                await InsertHistoryAsync(connection, transaction, new ExecutionTraceEvent
                {
                    RunId = row.Run.Id,
                    Type = ExecutionEventTypes.TimerScheduled,
                    TimestampUtc = timer.CreatedAtUtc,
                    Attempt = row.Run.Attempt,
                    Status = row.Run.Status,
                    Message = $"Timer '{timer.Name}' scheduled."
                }, ct);
            }
            await transaction.CommitAsync(ct);
            return timer;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<TemporalExecutionProjectionStatus> GetRuntimeStatusAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        return new TemporalExecutionProjectionStatus
        {
            SchemaVersion = status.SchemaVersion,
            PendingStartDispatches = status.PendingStartDispatches,
            PendingSignalDispatches = status.PendingSignalDispatches,
            PendingCancellationDispatches = status.PendingCancellationDispatches,
            OldestPendingDispatchAtUtc = status.OldestPendingDispatchAtUtc,
            ActiveRuns = status.ActiveRuns,
            ActiveCoordinators = status.ActiveCoordinators
        };
    }

    private static async Task<bool> RunExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM vyral_temporal_runs WHERE run_id = @run_id);";
        command.Parameters.AddWithValue("run_id", runId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task InsertRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionRun run,
        string workflowId,
        int generation,
        long projectionRevision,
        string requestHash,
        CancellationToken ct) =>
        _ = await ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_temporal_runs
                (run_id, workflow_id, generation, projection_revision, status, idempotency_key,
                 request_hash, cancellation_requested, run_json, created_at_utc, updated_at_utc)
            VALUES
                (@run_id, @workflow_id, @generation, @projection_revision, @status, @idempotency_key,
                 @request_hash, @cancellation_requested, @run_json::jsonb, @created_at_utc, @updated_at_utc);
            """, ct,
            ("run_id", run.Id), ("workflow_id", workflowId),
            ("generation", generation), ("projection_revision", projectionRevision),
            ("status", run.Status), ("idempotency_key", DbValue(run.IdempotencyKey, NpgsqlDbType.Text)),
            ("request_hash", requestHash), ("cancellation_requested", run.CancellationRequested),
            ("run_json", Serialize(run)), ("created_at_utc", Utc(run.CreatedAtUtc)),
            ("updated_at_utc", Utc(run.UpdatedAtUtc)));

    private static async Task InsertIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        string requestHash,
        string runId,
        CancellationToken ct) =>
        _ = await ExecuteAsync(connection, transaction, """
            INSERT INTO vyral_temporal_idempotency
                (idempotency_key, request_hash, run_id, created_at_utc)
            VALUES (@idempotency_key, @request_hash, @run_id, @created_at_utc);
            """, ct,
            ("idempotency_key", idempotencyKey), ("request_hash", requestHash),
            ("run_id", runId), ("created_at_utc", DateTime.UtcNow));

    private static async Task<(string OwnerId, DateTime ExpiresAtUtc, long FencingToken, ExecutionLease Lease)?> ReadLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string leaseKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT owner_id, expires_at_utc, fencing_token, lease_json::text
            FROM vyral_temporal_leases
            WHERE lease_key = @lease_key
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("lease_key", leaseKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (
            reader.GetString(0),
            reader.GetDateTime(1).ToUniversalTime(),
            reader.GetInt64(2),
            Deserialize<ExecutionLease>(reader.GetString(3)));
    }

    private static void AddNullableParameter(
        NpgsqlCommand command,
        string name,
        object? value,
        NpgsqlDbType type)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static void ValidateOptionalQueryText(string? value, string name, int maximumLength)
    {
        if (value is null) return;
        ValidateText(value, name, maximumLength);
    }

    private static void ValidateQueryTimestamp(DateTime? value, string name)
    {
        if (value?.Kind == DateTimeKind.Unspecified)
            throw new InvalidOperationException($"Temporal run query {name} timestamp must include a time zone.");
    }

    private static void ValidateCreation(TemporalProjectionRunCreation creation)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(creation.Run);
        ValidateText(creation.Run.Id, "Temporal projection run id", 200);
        ValidateText(creation.Run.HandlerId, "Temporal projection handler id", 160);
        ValidateText(creation.WorkflowId, "Temporal projection workflow id", 255);
        if (creation.Generation < 1 || creation.ProjectionRevision < 1)
            throw new InvalidOperationException("Temporal projection generation and revision must be positive.");
        if (string.IsNullOrWhiteSpace(creation.RequestHash) || creation.RequestHash.Length != 64 ||
            creation.RequestHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("Temporal projection request hash must be a SHA-256 hex digest.");
        }
        if (!ExecutionRunStatuses.IsKnown(creation.Run.Status))
            throw new InvalidOperationException("Temporal projection run status is invalid.");
        if (creation.Run.CreatedAtUtc.Kind == DateTimeKind.Unspecified ||
            creation.Run.UpdatedAtUtc.Kind == DateTimeKind.Unspecified)
        {
            throw new InvalidOperationException("Temporal projection run timestamps must include a time zone.");
        }
    }

    private static bool SameCreation(RunRow row, TemporalProjectionRunCreation creation) =>
        row.Generation == creation.Generation && row.ProjectionRevision == creation.ProjectionRevision &&
        string.Equals(row.WorkflowId, creation.WorkflowId, StringComparison.Ordinal) &&
        string.Equals(row.Run.HandlerId, creation.Run.HandlerId, StringComparison.Ordinal) &&
        string.Equals(row.Run.PayloadHash, creation.Run.PayloadHash, StringComparison.Ordinal) &&
        string.Equals(row.Run.IdempotencyKey, creation.Run.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(row.RequestHash, creation.RequestHash, StringComparison.Ordinal);
}
