using System.Net;
using Npgsql;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Postgres;

namespace Vyral.Tests.Temporal;

public sealed class PostgresTemporalProjectionTests
{
    [Fact]
    public void Options_RequireTlsOutsideLoopbackAndRedactConnectionMaterial()
    {
        var options = new PostgresTemporalProjectionOptions
        {
            ConnectionString = "Host=postgres.example.invalid;Database=vyral;Username=operator;Password=secret;SSL Mode=VerifyFull",
            DatabaseSchema = "vyral_temporal_preview"
        };

        options.Validate();
        var metadata = options.ToDiagnosticMetadata();

        Assert.Equal("true", metadata["tlsRequired"]);
        Assert.DoesNotContain("postgres.example.invalid", string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("operator", string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", string.Join('|', metadata.Values), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new PostgresTemporalProjectionOptions
        {
            ConnectionString = "Host=postgres.example.invalid;Database=vyral;SSL Mode=Disable",
            RequireTls = false
        }.Validate());

        new PostgresTemporalProjectionOptions
        {
            ConnectionString = "Host=127.0.0.1;Database=vyral;SSL Mode=Disable",
            RequireTls = false
        }.Validate();
    }

    [Fact]
    public void Schema_ContainsTransactionalProjectionPlanesAndNoSignalBodyColumn()
    {
        var schema = PostgresTemporalProjectionSql.Schema;

        Assert.Contains("vyral_temporal_runs", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_idempotency", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_start_outbox", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_signal_outbox", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_cancellation_outbox", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_external_events", schema, StringComparison.Ordinal);
        Assert.Contains("vyral_temporal_wait_outcomes", schema, StringComparison.Ordinal);
        Assert.Contains("claimed_attempt integer NULL", schema, StringComparison.Ordinal);
        Assert.Contains("consumed_at_utc timestamptz NULL", schema, StringComparison.Ordinal);
        Assert.Equal(4, PostgresTemporalProjectionSql.SchemaVersion);

        var signalStart = schema.IndexOf("CREATE TABLE IF NOT EXISTS vyral_temporal_signal_outbox", StringComparison.Ordinal);
        var signalEnd = schema.IndexOf("CREATE INDEX IF NOT EXISTS ix_vyral_temporal_signal_outbox_pending", signalStart, StringComparison.Ordinal);
        var signalTable = schema[signalStart..signalEnd];
        Assert.DoesNotContain("event_json", signalTable, StringComparison.Ordinal);
        Assert.Contains("event_id", signalTable, StringComparison.Ordinal);
        Assert.Contains("event_revision", signalTable, StringComparison.Ordinal);
    }

    [TemporalPostgresLiveFact]
    public async Task ProjectionStore_PersistsStartEventBeforeWaitAndIdempotentResolution_WhenPostgresIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING is required for Temporal projection integration tests.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var loopback = string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(builder.Host, out var address) && IPAddress.IsLoopback(address);
        var schema = $"vyral_temporal_test_{Guid.NewGuid():N}";
        var options = new PostgresTemporalProjectionOptions
        {
            ConnectionString = connectionString,
            DatabaseSchema = schema,
            RequireTls = !loopback,
            DispatchClaimSeconds = 5,
            DispatchRetrySeconds = 1
        };
        var store = new PostgresTemporalExecutionProjectionStore(options);
        try
        {
            await store.InitializeAsync();
            var now = DateTime.UtcNow;
            var run = new ExecutionRun
            {
                Id = "run-1",
                HandlerId = "sample.handler",
                Status = ExecutionRunStatuses.Queued,
                IdempotencyKey = "sample-key",
                CorrelationId = "run-1",
                PayloadHash = new string('b', 64),
                MaxAttempts = 2,
                RetryPolicy = new ExecutionRetryPolicy
                {
                    MaxAttempts = 2,
                    InitialDelaySeconds = 0,
                    MaxDelaySeconds = 0,
                    BackoffMultiplier = 1
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["suite"] = "temporal" }
            };
            var start = new TemporalProjectionRunStart
            {
                Run = run,
                WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("test", run.Id),
                Generation = 1,
                ProjectionRevision = 1,
                DispatchId = "start-1",
                RequestHash = new string('a', 64)
            };

            Assert.False((await store.CreateRunWithPendingStartAsync(start)).Replayed);
            Assert.True((await store.CreateRunWithPendingStartAsync(start)).Replayed);
            var pendingStart = Assert.Single(await store.ListPendingStartsAsync(10));
            await store.MarkStartDeliveredAsync(pendingStart.DispatchId, new TemporalCoordinationReference
            {
                WorkflowId = pendingStart.WorkflowId,
                TemporalRunId = "temporal-run-1",
                Generation = pendingStart.Generation
            });
            var activeCoordinators = await store.GetActiveCoordinatorSnapshotAsync(10);
            Assert.Equal(1, activeCoordinators.TotalCount);
            var activeCoordinator = Assert.Single(activeCoordinators.Coordinators);
            Assert.Equal(start.WorkflowId, activeCoordinator.WorkflowId);
            Assert.Equal(1, activeCoordinator.Generation);
            Assert.Equal(1, (await store.GetStatusAsync()).ActiveCoordinators);

            await store.PersistExternalEventWithPendingSignalAsync(new TemporalProjectionExternalEventWrite
            {
                Event = new ExecutionExternalEvent
                {
                    Id = "event-1",
                    Name = "approved",
                    RunId = run.Id,
                    RaisedAtUtc = now,
                    Payload = new System.Text.Json.Nodes.JsonObject { ["sensitive"] = "body" }
                },
                EventRevision = 1,
                WorkflowId = start.WorkflowId,
                Generation = 1,
                DispatchId = "signal-1"
            });
            var pendingSignal = Assert.Single(await store.ListPendingSignalsAsync(10));
            Assert.Equal("event-1", pendingSignal.EventId);
            await store.MarkSignalDeliveredAsync(pendingSignal.DispatchId);

            await store.RegisterWaitAsync(new TemporalProjectionWaitRegistration
            {
                RunId = run.Id,
                Generation = 1,
                WaitId = "wait-1",
                Kind = "external_event",
                Name = "approved"
            }, Trace(run.Id, "wait-1-registered", ExecutionEventTypes.WaitRegistered));
            var resolution = new TemporalExecutionWaitResolution
            {
                RunId = run.Id,
                Generation = 1,
                WaitId = "wait-1",
                Resolution = "external_event",
                EventId = "event-1",
                EventRevision = 1
            };
            Assert.True((await store.ProjectWaitResolutionAsync(resolution)).Accepted);
            Assert.True((await store.ProjectWaitResolutionAsync(resolution)).Accepted);

            var loaded = await store.GetRunAsync(run.Id);
            Assert.NotNull(loaded);
            Assert.Equal(ExecutionRunStatuses.Queued, loaded!.Status);
            Assert.Null((await store.GetRunAsync(run.Id, includeResult: false))!.Result);
            Assert.Equal(run.Id, Assert.Single(await store.ListRunsAsync(new ExecutionRunQuery
            {
                HandlerId = run.HandlerId,
                Tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["suite"] = "temporal" },
                Limit = 10
            })).Id);

            var attemptOne = await store.BeginAttemptAsync(new TemporalExecutionAttemptRequest
            {
                RunId = run.Id,
                Generation = 1,
                Attempt = 1
            }, Trace(run.Id, "attempt-1-start", ExecutionEventTypes.RunStarted));
            Assert.Equal(ExecutionRunStatuses.Running, attemptOne.Status);
            var waitResult = await store.ConsumeWaitResultAsync(run.Id, 1, 1, "external_event", "approved");
            Assert.Equal(ExecutionWaitOutcomes.ExternalEvent, waitResult!.Outcome);
            Assert.Equal("event-1", waitResult.Event!.Id);
            Assert.Equal(
                "event-1",
                (await store.ConsumeWaitResultAsync(run.Id, 1, 1, "external_event", "approved"))!.Event!.Id);

            var reported = await store.ReportRunAsync(run.Id, 1, new ExecutionRunUpdate
            {
                Status = ExecutionRunStatuses.Running,
                Progress = 0.5,
                CurrentStep = "project"
            }, Trace(run.Id, "attempt-1-status", ExecutionEventTypes.RunStatus));
            Assert.Equal(0.5, reported.Progress);
            var artifact = await store.PutArtifactMetadataAsync(run.Id, 1, new ExecutionArtifact
            {
                Id = "artifact-1",
                RunId = run.Id,
                Name = "summary.json",
                Kind = ExecutionArtifactKinds.Json,
                ContentHash = "sha256:" + new string('c', 64),
                SizeBytes = 2,
                Content = new System.Text.Json.Nodes.JsonObject(),
                CreatedAtUtc = DateTime.UtcNow
            }, Trace(run.Id, "attempt-1-artifact", ExecutionEventTypes.ArtifactWritten));
            var checkpoint = await store.PutCheckpointAsync(run.Id, 1, new ExecutionCheckpoint
            {
                RunId = run.Id,
                Key = "cursor",
                ContentHash = "sha256:" + new string('d', 64),
                Content = new System.Text.Json.Nodes.JsonObject { ["offset"] = 1 },
                UpdatedAtUtc = DateTime.UtcNow
            }, Trace(run.Id, "attempt-1-checkpoint", ExecutionEventTypes.CheckpointWritten));
            var replayedArtifact = await store.PutArtifactMetadataAsync(run.Id, 1, new ExecutionArtifact
            {
                Id = "artifact-replayed-transport-id",
                RunId = run.Id,
                Name = artifact.Name,
                Kind = artifact.Kind,
                ContentHash = artifact.ContentHash,
                SizeBytes = artifact.SizeBytes,
                Content = new System.Text.Json.Nodes.JsonObject(),
                CreatedAtUtc = DateTime.UtcNow
            }, Trace(run.Id, "attempt-1-artifact-replay", ExecutionEventTypes.ArtifactWritten));
            Assert.Equal(artifact.Id, replayedArtifact.Id);
            Assert.Equal(artifact.Id, (await store.GetArtifactAsync(run.Id, artifact.Name))!.Id);
            Assert.Equal(checkpoint.ContentHash, (await store.GetCheckpointAsync(run.Id, checkpoint.Key))!.ContentHash);

            var retry = await store.CompleteAttemptAsync(
                run.Id,
                1,
                ExecutionRunResult.Failed(ExecutionFailureClasses.Transient, "retry"),
                Trace(run.Id, "attempt-1-retry", ExecutionEventTypes.RetryScheduled),
                Trace(run.Id, "attempt-1-terminal", ExecutionEventTypes.RunFailed));
            Assert.NotNull(retry.RetryDelayMilliseconds);
            Assert.Equal(ExecutionRunStatuses.Waiting, retry.Run.Status);
            Assert.Null(await store.ConsumeWaitResultAsync(run.Id, 1, 1, "external_event", "approved"));
            var attemptTwo = await store.BeginAttemptAsync(new TemporalExecutionAttemptRequest
            {
                RunId = run.Id,
                Generation = 1,
                Attempt = 2
            }, Trace(run.Id, "attempt-2-start", ExecutionEventTypes.RunStarted));
            Assert.Equal(2, attemptTwo.Attempt);
            var completion = await store.CompleteAttemptAsync(
                run.Id,
                1,
                ExecutionRunResult.Succeeded(new System.Text.Json.Nodes.JsonObject { ["ok"] = true }),
                Trace(run.Id, "attempt-2-retry", ExecutionEventTypes.RetryScheduled),
                Trace(run.Id, "attempt-2-terminal", ExecutionEventTypes.RunCompleted));
            Assert.Equal(ExecutionRunStatuses.Succeeded, completion.Run.Status);
            Assert.Null(completion.RetryDelayMilliseconds);
            Assert.NotEmpty(await store.GetHistoryAsync(run.Id, 20));
            Assert.Single(await store.ListArtifactsAsync(run.Id));
            Assert.Null(await store.GetArtifactAsync(run.Id, "missing"));
            Assert.Null(await store.GetCheckpointAsync(run.Id, "missing"));

            var cancellationRun = new ExecutionRun
            {
                Id = "run-cancel",
                HandlerId = "sample.handler",
                Status = ExecutionRunStatuses.Queued,
                CorrelationId = "run-cancel",
                PayloadHash = new string('e', 64),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var cancellationStart = new TemporalProjectionRunStart
            {
                Run = cancellationRun,
                WorkflowId = TemporalExecutionIdentity.CreateWorkflowId("test", cancellationRun.Id),
                Generation = 1,
                ProjectionRevision = 1,
                DispatchId = "start-cancel",
                RequestHash = new string('f', 64)
            };
            await store.CreateRunWithPendingStartAsync(cancellationStart);
            var cancellationStartDispatch = Assert.Single(await store.ListPendingStartsAsync(10));
            await store.MarkStartDeliveredAsync(
                cancellationStartDispatch.DispatchId,
                new TemporalCoordinationReference
                {
                    WorkflowId = cancellationStartDispatch.WorkflowId,
                    Generation = cancellationStartDispatch.Generation
                });
            var requestedCancellation = await store.RequestCancellationAsync(cancellationRun.Id);
            Assert.True(requestedCancellation.NewlyRequested);
            var cancellationDispatch = Assert.Single(await store.ListPendingCancellationsAsync(10));
            Assert.Equal(cancellationRun.Id, cancellationDispatch.RunId);
            await store.MarkCancellationDeliveredAsync(cancellationDispatch.DispatchId);
            await store.ProjectCancellationAsync(new TemporalExecutionCancellation
            {
                RunId = cancellationRun.Id,
                Generation = 1
            });
            Assert.Equal(
                ExecutionRunStatuses.Cancelled,
                (await store.GetRunAsync(cancellationRun.Id))!.Status);

            var lease = await store.TryAcquireLeaseAsync(new ExecutionLeaseRequest
            {
                LeaseKey = "lease-1",
                OwnerId = "owner-1",
                RunId = run.Id,
                TtlSeconds = 30
            });
            Assert.NotNull(lease);
            Assert.Null(await store.TryAcquireLeaseAsync(new ExecutionLeaseRequest
            {
                LeaseKey = "lease-1",
                OwnerId = "owner-2",
                RunId = run.Id,
                TtlSeconds = 30
            }));
            Assert.False(await store.ReleaseLeaseAsync("lease-1", "owner-2"));
            Assert.True(await store.ReleaseLeaseAsync("lease-1", "owner-1"));

            var timer = await store.ScheduleTimerAsync(new ExecutionTimerRequest
            {
                Name = "resume",
                RunId = run.Id,
                FireAtUtc = now.AddMinutes(1)
            });
            Assert.Equal(run.Id, timer.RunId);
            Assert.Equal("resume", timer.Name);

            var status = await store.GetStatusAsync();
            Assert.Equal(PostgresTemporalProjectionSql.SchemaVersion, status.SchemaVersion);
            Assert.Equal(0, status.PendingStartDispatches);
            Assert.Equal(0, status.PendingSignalDispatches);
            Assert.Equal(0, status.PendingCancellationDispatches);
            Assert.Equal(0, status.ActiveCoordinators);
            Assert.Equal(0, (await store.GetActiveCoordinatorSnapshotAsync(10)).TotalCount);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS {PostgresTemporalProjectionOptions.QuoteSchema(schema)} CASCADE;";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static ExecutionTraceEvent Trace(string runId, string eventId, string type) => new()
    {
        Id = eventId,
        SequenceId = eventId,
        RunId = runId,
        Type = type,
        TimestampUtc = DateTime.UtcNow,
        Severity = "info"
    };

}

public sealed class TemporalPostgresLiveFactAttribute : FactAttribute
{
    public TemporalPostgresLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING")))
        {
            Skip = "Set VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING to run Temporal projection integration tests.";
        }
    }
}
