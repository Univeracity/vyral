using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Npgsql;
using Vyral.Execution;
using Vyral.Execution.Temporal;
using Vyral.Execution.Temporal.Postgres;

namespace Vyral.Tests.Temporal;

public sealed class TemporalProjectionBackupRestoreTests
{
    private static readonly TimeSpan PostgresToolTimeout = TimeSpan.FromSeconds(60);

    [TemporalProjectionBackupRestoreFact]
    public async Task ProjectionBackup_RestoresPortableStateAndResumesStoreOperations()
    {
        var connectionString = Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Temporal projection backup gate connection is required.");
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (!IsLoopback(Required(connection.Host, "host")))
            throw new InvalidOperationException("Temporal projection backup gate requires a loopback database.");

        var suffix = Guid.NewGuid().ToString("N");
        var sourceSchema = $"vyral_temporal_backup_{suffix[..16]}";
        var restoredDatabase = $"vyral_temporal_restore_{suffix[..16]}";
        var dumpPath = Path.Combine(Path.GetTempPath(), $"vyral-temporal-{suffix}.dump");
        var sourceOptions = Options(connectionString, sourceSchema);
        var source = new PostgresTemporalExecutionProjectionStore(sourceOptions);
        var restoredDatabaseCreated = false;
        try
        {
            await source.InitializeAsync();
            var now = DateTime.UtcNow;
            var activeRun = Run("backup-active", "backup.handler", "backup-active-key", now);
            var activeStart = Start(activeRun, sourceSchema, "backup-active-start", 'a');
            Assert.False((await source.CreateRunWithPendingStartAsync(activeStart)).Replayed);
            var activeDispatch = Assert.Single(await source.ListPendingStartsAsync(10));
            await source.MarkStartDeliveredAsync(activeDispatch.DispatchId, new TemporalCoordinationReference
            {
                WorkflowId = activeDispatch.WorkflowId,
                TemporalRunId = "backup-temporal-run",
                Generation = activeDispatch.Generation
            });
            _ = await source.BeginAttemptAsync(new TemporalExecutionAttemptRequest
            {
                RunId = activeRun.Id,
                Generation = 1,
                Attempt = 1
            }, Trace(activeRun.Id, "backup-attempt", ExecutionEventTypes.RunStarted));
            _ = await source.PutCheckpointAsync(activeRun.Id, 1, new ExecutionCheckpoint
            {
                RunId = activeRun.Id,
                Key = "cursor",
                ContentHash = "sha256:" + new string('c', 64),
                Content = new JsonObject { ["offset"] = 7 },
                UpdatedAtUtc = now
            }, Trace(activeRun.Id, "backup-checkpoint", ExecutionEventTypes.CheckpointWritten));
            _ = await source.PutArtifactMetadataAsync(activeRun.Id, 1, new ExecutionArtifact
            {
                Id = "backup-artifact",
                RunId = activeRun.Id,
                Name = "summary.json",
                Kind = ExecutionArtifactKinds.Json,
                MediaType = "application/json",
                ContentHash = "sha256:" + new string('d', 64),
                SizeBytes = 12,
                Content = new JsonObject { ["restorable"] = true },
                CreatedAtUtc = now
            }, Trace(activeRun.Id, "backup-artifact", ExecutionEventTypes.ArtifactWritten));
            _ = await source.CreateExternalEventWithPendingSignalAsync(new ExecutionExternalEvent
            {
                Id = "backup-event",
                RunId = activeRun.Id,
                Name = "approval",
                Payload = new JsonObject { ["decision"] = "approved" },
                RaisedAtUtc = now
            }, "backup-signal");
            Assert.True((await source.RequestCancellationAsync(activeRun.Id)).NewlyRequested);
            Assert.NotNull(await source.TryAcquireLeaseAsync(new ExecutionLeaseRequest
            {
                LeaseKey = "backup-lease",
                OwnerId = "backup-owner",
                RunId = activeRun.Id,
                TtlSeconds = 300
            }));

            var pendingRun = Run("backup-pending", "backup.handler", "backup-pending-key", now.AddSeconds(1));
            var pendingStart = Start(pendingRun, sourceSchema, "backup-pending-start", 'b');
            Assert.False((await source.CreateRunWithPendingStartAsync(pendingStart)).Replayed);
            var sourceStatus = await source.GetRuntimeStatusAsync();
            Assert.Equal(2, sourceStatus.ActiveRuns);
            Assert.Equal(1, sourceStatus.ActiveCoordinators);
            Assert.Equal(1, sourceStatus.PendingStartDispatches);
            Assert.Equal(1, sourceStatus.PendingSignalDispatches);
            Assert.Equal(1, sourceStatus.PendingCancellationDispatches);

            await DumpSchemaAsync(connection, sourceSchema, dumpPath);
            Assert.True(new FileInfo(dumpPath).Length > 0);
            var firstHash = await HashAsync(dumpPath);
            Assert.Equal(64, firstHash.Length);
            await CreateDatabaseAsync(connection, restoredDatabase);
            restoredDatabaseCreated = true;
            await RestoreDatabaseAsync(connection, restoredDatabase, dumpPath);
            Assert.Equal(firstHash, await HashAsync(dumpPath));

            var restoredConnection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = restoredDatabase
            };
            var restored = new PostgresTemporalExecutionProjectionStore(
                Options(restoredConnection.ConnectionString, sourceSchema));
            await restored.InitializeAsync();
            var restoredStatus = await restored.GetRuntimeStatusAsync();
            Assert.Equal(PostgresTemporalProjectionSql.SchemaVersion, restoredStatus.SchemaVersion);
            Assert.Equal(2, restoredStatus.ActiveRuns);
            Assert.Equal(1, restoredStatus.ActiveCoordinators);
            Assert.Equal(1, restoredStatus.PendingStartDispatches);
            Assert.Equal(1, restoredStatus.PendingSignalDispatches);
            Assert.Equal(1, restoredStatus.PendingCancellationDispatches);

            var restoredActive = await restored.GetRunAsync(activeRun.Id);
            Assert.NotNull(restoredActive);
            Assert.Equal(ExecutionRunStatuses.Running, restoredActive!.Status);
            Assert.True(restoredActive.CancellationRequested);
            Assert.Equal(1, restoredActive.Attempt);
            var restoredCheckpoint = await restored.GetCheckpointAsync(activeRun.Id, "cursor");
            Assert.NotNull(restoredCheckpoint);
            Assert.NotNull(restoredCheckpoint!.Content);
            Assert.Equal(7, restoredCheckpoint.Content!["offset"]!.GetValue<int>());
            Assert.Equal("backup-artifact", (await restored.GetArtifactAsync(activeRun.Id, "summary.json"))!.Id);
            Assert.NotEmpty(await restored.GetHistoryAsync(activeRun.Id, 100));
            Assert.True(await restored.IsActiveCoordinatorAsync(activeStart.WorkflowId, 1));

            var replay = await restored.CreateRunWithPendingStartAsync(pendingStart);
            Assert.True(replay.Replayed);
            Assert.Equal(pendingRun.Id, replay.Run.Id);
            Assert.Single(await restored.ListPendingStartsAsync(10));
            var pendingSignal = Assert.Single(await restored.ListPendingSignalsAsync(10));
            await restored.MarkSignalDeliveredAsync(pendingSignal.DispatchId);
            var pendingCancellation = Assert.Single(await restored.ListPendingCancellationsAsync(10));
            await restored.MarkCancellationDeliveredAsync(pendingCancellation.DispatchId);
            Assert.True(await restored.ReleaseLeaseAsync("backup-lease", "backup-owner"));
            var drained = await restored.GetRuntimeStatusAsync();
            Assert.Equal(0, drained.PendingSignalDispatches);
            Assert.Equal(0, drained.PendingCancellationDispatches);
        }
        finally
        {
            try
            {
                if (restoredDatabaseCreated)
                    await DropDatabaseAsync(connection, restoredDatabase);
            }
            finally
            {
                try
                {
                    if (File.Exists(dumpPath)) File.Delete(dumpPath);
                }
                finally
                {
                    await DropSchemaAsync(connectionString, sourceSchema);
                }
            }
        }
    }

    private static PostgresTemporalProjectionOptions Options(string connectionString, string schema) => new()
    {
        ConnectionString = connectionString,
        DatabaseSchema = schema,
        RequireTls = false,
        DispatchClaimSeconds = 5,
        DispatchRetrySeconds = 1
    };

    private static ExecutionRun Run(
        string id,
        string handlerId,
        string idempotencyKey,
        DateTime now) => new()
    {
        Id = id,
        HandlerId = handlerId,
        Status = ExecutionRunStatuses.Queued,
        IdempotencyKey = idempotencyKey,
        CorrelationId = id,
        PayloadHash = new string('f', 64),
        MaxAttempts = 2,
        RetryPolicy = new ExecutionRetryPolicy
        {
            MaxAttempts = 2,
            InitialDelaySeconds = 1,
            MaxDelaySeconds = 1,
            BackoffMultiplier = 1
        },
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static TemporalProjectionRunStart Start(
        ExecutionRun run,
        string adapterNamespace,
        string dispatchId,
        char requestHashCharacter) => new()
    {
        Run = run,
        WorkflowId = TemporalExecutionIdentity.CreateWorkflowId(adapterNamespace, run.Id),
        Generation = 1,
        ProjectionRevision = 1,
        DispatchId = dispatchId,
        RequestHash = new string(requestHashCharacter, 64)
    };

    private static ExecutionTraceEvent Trace(string runId, string eventId, string type) => new()
    {
        Id = eventId,
        SequenceId = eventId,
        RunId = runId,
        Type = type,
        TimestampUtc = DateTime.UtcNow,
        Severity = "info"
    };

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static async Task<string> HashAsync(string path)
    {
        await using var content = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(content)).ToLowerInvariant();
    }

    private static async Task DumpSchemaAsync(
        NpgsqlConnectionStringBuilder connection,
        string schema,
        string outputPath)
    {
        using var process = StartPostgresTool(connection,
            "pg_dump",
            "--host", Required(connection.Host, "host"),
            "--port", connection.Port.ToString(),
            "--username", Required(connection.Username, "username"),
            "--dbname", Required(connection.Database, "database"),
            "--schema", schema,
            "--format", "custom",
            "--no-owner",
            "--no-privileges");
        await using var output = File.Create(outputPath);
        using var timeout = new CancellationTokenSource(PostgresToolTimeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await stderr;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await TerminateAsync(process);
            throw new TimeoutException("Projection schema backup timed out.");
        }
        EnsureSuccess(process, "Projection schema backup");
    }

    private static async Task RestoreDatabaseAsync(
        NpgsqlConnectionStringBuilder connection,
        string database,
        string inputPath)
    {
        using var process = StartPostgresTool(connection,
            "pg_restore",
            "--host", Required(connection.Host, "host"),
            "--port", connection.Port.ToString(),
            "--username", Required(connection.Username, "username"),
            "--dbname", database,
            "--no-owner",
            "--no-privileges");
        using var timeout = new CancellationTokenSource(PostgresToolTimeout);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await using var input = File.OpenRead(inputPath);
            await input.CopyToAsync(process.StandardInput.BaseStream, timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stderr;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.StandardInput.Close();
            await TerminateAsync(process);
            throw new TimeoutException("Projection schema restore timed out.");
        }
        EnsureSuccess(process, "Projection schema restore");
    }

    private static Task CreateDatabaseAsync(NpgsqlConnectionStringBuilder connection, string database) =>
        RunPsqlAsync(connection, $"CREATE DATABASE {database};", "Projection restore database creation");

    private static Task DropDatabaseAsync(NpgsqlConnectionStringBuilder connection, string database) =>
        RunPsqlAsync(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE);", "Projection restore database cleanup");

    private static async Task RunPsqlAsync(
        NpgsqlConnectionStringBuilder connection,
        string command,
        string operation)
    {
        using var process = StartPostgresTool(connection,
            "psql",
            "--host", Required(connection.Host, "host"),
            "--port", connection.Port.ToString(),
            "--username", Required(connection.Username, "username"),
            "--dbname", "postgres",
            "--set", "ON_ERROR_STOP=1",
            "--command", command);
        using var timeout = new CancellationTokenSource(PostgresToolTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            _ = await stderr;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await TerminateAsync(process);
            throw new TimeoutException($"{operation} timed out.");
        }
        EnsureSuccess(process, operation);
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var cleanup = new NpgsqlConnection(connectionString);
        await cleanup.OpenAsync();
        await using var command = cleanup.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {PostgresTemporalProjectionOptions.QuoteSchema(schema)} CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static Process StartPostgresTool(
        NpgsqlConnectionStringBuilder connection,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--rm");
        start.ArgumentList.Add("--interactive");
        start.ArgumentList.Add("--network");
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--env");
        start.ArgumentList.Add("PGPASSWORD");
        start.ArgumentList.Add("postgres:16");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = connection.Password;
        return Process.Start(start) ??
            throw new InvalidOperationException("PostgreSQL backup tool process did not start.");
    }

    private static void EnsureSuccess(Process process, string operation)
    {
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed with exit code {process.ExitCode}.");
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Temporal projection backup {name} is required.")
            : value;
}

public sealed class TemporalProjectionBackupRestoreFactAttribute : FactAttribute
{
    public TemporalProjectionBackupRestoreFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_CONTAINER_GATE"),
            "1",
            StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VYRAL_TEMPORAL_POSTGRES_CONNECTION_STRING")))
        {
            Skip = "Run scripts/validate-temporal-container.sh to enable the disposable projection backup gate.";
        }
    }
}
