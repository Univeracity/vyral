using Npgsql;

namespace Vyral.Execution.Temporal.Postgres;

public sealed partial class PostgresTemporalExecutionProjectionStore
{
    public async Task<IReadOnlyList<TemporalCancellationDispatch>> ListPendingCancellationsAsync(
        int limit,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
        var results = new List<TemporalCancellationDispatch>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
                SELECT dispatch_id
                FROM vyral_temporal_cancellation_outbox
                WHERE delivered_at_utc IS NULL
                  AND next_attempt_at_utc <= CURRENT_TIMESTAMP
                  AND (claimed_until_utc IS NULL OR claimed_until_utc <= CURRENT_TIMESTAMP)
                ORDER BY created_at_utc, dispatch_id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE vyral_temporal_cancellation_outbox AS outbox
            SET claimed_by = @claimed_by,
                claimed_until_utc = CURRENT_TIMESTAMP + (@claim_seconds * INTERVAL '1 second'),
                attempt_count = outbox.attempt_count + 1
            FROM candidates
            WHERE outbox.dispatch_id = candidates.dispatch_id
            RETURNING outbox.dispatch_id, outbox.run_id, outbox.workflow_id,
                      outbox.generation, outbox.attempt_count;
            """;
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("claimed_by", _claimOwner);
        command.Parameters.AddWithValue("claim_seconds", Options.DispatchClaimSeconds);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(new TemporalCancellationDispatch
                {
                    DispatchId = reader.GetString(0),
                    RunId = reader.GetString(1),
                    WorkflowId = reader.GetString(2),
                    Generation = reader.GetInt32(3),
                    AttemptCount = reader.GetInt32(4)
                });
            }
        }
        await transaction.CommitAsync(ct);
        return results;
    }

    public Task MarkCancellationDeliveredAsync(
        string dispatchId,
        CancellationToken ct = default) =>
        MarkDispatchDeliveredAsync("vyral_temporal_cancellation_outbox", dispatchId, ct);

    public Task RecordCancellationFailureAsync(
        string dispatchId,
        string failureClass,
        CancellationToken ct = default) =>
        RecordDispatchFailureAsync(
            "vyral_temporal_cancellation_outbox",
            dispatchId,
            failureClass,
            ct);
}
