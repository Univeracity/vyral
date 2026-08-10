using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Vyral.Execution;

namespace Vyral.Execution.Aws;

/// <summary>
/// DynamoDB implementation of the durable execution state seam. One table holds run-owned
/// records, coordination leases, idempotency reservations, and a per-handler runnable-work GSI.
/// SQS messages contain no mutable state; workers lease this store before doing any work.
/// </summary>
public sealed class DynamoDbExecutionStateStore : IAwsDynamoExecutionStateStore
{
    private const string Pk = "pk";
    private const string Sk = "sk";
    private const string GsiPk = "gsi1pk";
    private const string GsiSk = "gsi1sk";
    private const string Kind = "kind";
    private const string Json = "json";
    private const string Active = "active";
    private const string LeaseOwner = "leaseOwner";
    private const string LeaseExpiresAt = "leaseExpiresAt";
    private const string GsiName = "vyral_execution_work";
    private const string RunStateSk = "run";
    private const string RunWorkSk = "work";
    private static readonly JsonSerializerOptions JsonOptions = ExecutionJson.Options;

    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbExecutionStateStoreOptions _options;
    private readonly object _tableEnsureLock = new();
    private Task? _tableEnsureTask;

    public DynamoDbExecutionStateStore(IAmazonDynamoDB client, DynamoDbExecutionStateStoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public DynamoDbExecutionStateStore(IAmazonDynamoDB client, string tableName, string root = "vyral-execution")
        : this(client, new DynamoDbExecutionStateStoreOptions { TableName = tableName, Root = root })
    {
    }

    public DynamoDbExecutionStateStoreOptions Options => _options;

    public async Task CreateRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        await EnsureTableAsync(ct);
        var writes = new List<TransactWriteItem>
        {
            PutWrite(BuildRunItem(run), "attribute_not_exists(#pk)")
        };
        AddWorkWrite(writes, run);
        AddActiveWrite(writes, ExecutionRunLifecycle.IsActive(run.Status) ? 1 : 0);
        await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
    }

    public async Task<bool> TryCreateRunWithActiveCapacityAsync(ExecutionRun run, int maxActiveRuns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maxActiveRuns <= 0) throw new InvalidOperationException("Execution max active runs must be positive.");
        if (!ExecutionRunLifecycle.IsActive(run.Status)) throw new InvalidOperationException("Capacity-controlled execution creation requires an active run.");
        await EnsureTableAsync(ct);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var writes = new List<TransactWriteItem> { PutWrite(BuildRunItem(run), "attribute_not_exists(#pk)") };
            AddWorkWrite(writes, run);
            AddActiveWrite(writes, 1, maxActiveRuns);
            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return true;
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                if (await GetActiveRunCountAsync(ct) >= maxActiveRuns) return false;
            }
        }

        return false;
    }

    public async Task<AwsDynamoExecutionRunCreation> CreateRunAtomicallyAsync(
        ExecutionRun run,
        ExecutionRun? capacityRejectedRun,
        int maxActiveRuns,
        string? idempotencyScopeKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maxActiveRuns <= 0) throw new InvalidOperationException("Execution max active runs must be positive.");
        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        await EnsureTableAsync(ct);

        var reservationKey = string.IsNullOrWhiteSpace(idempotencyScopeKey) ? null : IdempotencyKey(idempotencyScopeKey);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (reservationKey is not null)
            {
                var existing = await ReadAsync<AwsDynamoExecutionReservation>(IdempotencyPk(), reservationKey, ct);
                if (existing is not null)
                {
                    return new AwsDynamoExecutionRunCreation
                    {
                        Created = false,
                        RunId = existing.RunId,
                        HandlerId = existing.HandlerId,
                        PayloadHash = existing.PayloadHash
                    };
                }
            }

            var active = ExecutionRunLifecycle.IsActive(run.Status);
            var persisted = active && await GetActiveRunCountAsync(ct) >= maxActiveRuns
                ? capacityRejectedRun ?? throw new InvalidOperationException("An active execution run requires a capacity rejection shape.")
                : run;
            var writes = new List<TransactWriteItem> { PutWrite(BuildRunItem(persisted), "attribute_not_exists(#pk)") };
            AddWorkWrite(writes, persisted);
            if (active && ReferenceEquals(persisted, run)) AddActiveWrite(writes, 1, maxActiveRuns);
            if (reservationKey is not null)
            {
                writes.Add(PutWrite(BuildItem(IdempotencyPk(), reservationKey, "idempotency", new AwsDynamoExecutionReservation
                {
                    RunId = persisted.Id,
                    HandlerId = persisted.HandlerId,
                    PayloadHash = persisted.PayloadHash
                }), "attribute_not_exists(#pk)"));
            }

            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return new AwsDynamoExecutionRunCreation
                {
                    Created = true,
                    RunId = persisted.Id,
                    HandlerId = persisted.HandlerId,
                    PayloadHash = persisted.PayloadHash,
                    CreatedRun = Clone(persisted)
                };
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // A racing reservation is returned on the next loop; otherwise retry an active
                // counter transition before treating capacity as exhausted.
            }
        }

        throw new InvalidOperationException("Could not atomically create the execution run after concurrent DynamoDB updates.");
    }

    public async Task UpsertRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureTableAsync(ct);
        var previous = await ReadAsync<ExecutionRun>(RunPk(run.Id), RunStateSk, ct);
        var delta = (ExecutionRunLifecycle.IsActive(run.Status) ? 1 : 0) -
            (previous is not null && ExecutionRunLifecycle.IsActive(previous.Status) ? 1 : 0);
        var writes = new List<TransactWriteItem> { PutWrite(BuildRunItem(run)) };
        AddWorkWrite(writes, run);
        AddActiveWrite(writes, delta);
        await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
    }

    public async Task<ExecutionRun?> CancelRunAtomicallyAsync(string runId, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var normalizedRunId = Require(runId, "Execution run id");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var storedRun = await ReadStoredAsync(RunPk(normalizedRunId), RunStateSk, ct);
            if (storedRun is null) return null;
            var run = Read<ExecutionRun>(storedRun.Item) ?? throw new InvalidOperationException("Execution run is malformed.");
            if (ExecutionRunStatuses.IsTerminal(run.Status)) return run;

            var now = DateTime.UtcNow;
            var terminal = run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting;
            run.CancellationRequested = true;
            run.UpdatedAtUtc = now;
            if (terminal)
            {
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
                run.Status = ExecutionRunStatuses.Cancelled;
                run.FailureClass = ExecutionFailureClasses.Cancelled;
                run.Error = "Execution run was cancelled.";
                run.CompletedAtUtc = now;
                run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
            }

            var writes = new List<TransactWriteItem>
            {
                PutWrite(BuildRunItem(run), "#json = :old", storedRun.Json)
            };
            AddWorkWrite(writes, run);
            if (terminal)
            {
                var storedWait = await ReadStoredAsync(RunPk(normalizedRunId), "wait", ct);
                if (storedWait is not null)
                    writes.Add(DeleteWrite(RunPk(normalizedRunId), "wait", "#json = :old", storedWait.Json));
                AddActiveWrite(writes, -1);
            }

            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return run;
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // Reload every record involved in the transition. A competing completion,
                // timer, or event is allowed to win, but a stale cancellation write is not.
            }
        }

        throw new InvalidOperationException("Execution run changed repeatedly while requesting cancellation.");
    }

    public async Task<ExecutionRun?> TryResumeWaitAsync(
        string runId,
        string expectedKind,
        string expectedName,
        ExecutionWaitResult outcome,
        string? consumedEventId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await EnsureTableAsync(ct);
        var normalizedRunId = Require(runId, "Execution run id");
        var kind = Require(expectedKind, "Execution wait kind");
        var name = Require(expectedName, "Execution wait name");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var storedRun = await ReadStoredAsync(RunPk(normalizedRunId), RunStateSk, ct);
            var storedWait = await ReadStoredAsync(RunPk(normalizedRunId), "wait", ct);
            if (storedRun is null || storedWait is null) return null;
            var run = Read<ExecutionRun>(storedRun.Item) ?? throw new InvalidOperationException("Execution run is malformed.");
            var wait = Read<AwsDynamoExecutionWait>(storedWait.Item) ?? throw new InvalidOperationException("Execution wait is malformed.");
            if (run.Status != ExecutionRunStatuses.Waiting || !string.Equals(wait.Kind, kind, StringComparison.Ordinal) || !string.Equals(wait.Name, name, StringComparison.Ordinal))
                return null;

            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued);
            run.Status = ExecutionRunStatuses.Queued;
            run.ScheduledAtUtc = null;
            run.CurrentStep = null;
            run.UpdatedAtUtc = DateTime.UtcNow;
            var writes = new List<TransactWriteItem>
            {
                PutWrite(BuildRunItem(run), "#json = :old", storedRun.Json),
                DeleteWrite(RunPk(normalizedRunId), "wait", "#json = :old", storedWait.Json),
                PutWrite(BuildItem(RunPk(normalizedRunId), $"outcome#{WaitKey(kind, name)}", "outcome", outcome))
            };
            if (!string.IsNullOrWhiteSpace(consumedEventId))
            {
                writes.Add(PutWrite(
                    BuildItem(RunPk(normalizedRunId), $"consumed#{HashKey(consumedEventId)}", "consumed", new AwsDynamoExecutionConsumption { EventId = consumedEventId }),
                    "attribute_not_exists(#pk)"));
            }
            AddWorkWrite(writes, run);

            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return run;
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // The wait is a single-consumer rendezvous. A failed condition means another
                // transition won; retry only to observe its durable result, never to overwrite it.
            }
        }

        return null;
    }

    public async Task<int> GetActiveRunCountAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct);
        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _options.TableName,
            Key = Key(MetadataPk(), "active-runs"),
            ConsistentRead = true
        }, ct);
        return response.IsItemSet && response.Item.TryGetValue(Active, out var value) && int.TryParse(value.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var active)
            ? Math.Max(0, active)
            : 0;
    }

    public async Task<IReadOnlyList<string>> ListDueExternalRunIdsAsync(IEnumerable<string> handlerIds, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) throw new InvalidOperationException("Execution due-work limit must be positive.");
        await EnsureTableAsync(ct);
        var now = DateTime.UtcNow;
        var candidates = new List<(string RunId, DateTime DueAtUtc)>();
        foreach (var handlerId in handlerIds.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.Ordinal))
        {
            var response = await _client.QueryAsync(new QueryRequest
            {
                TableName = _options.TableName,
                IndexName = GsiName,
                KeyConditionExpression = "#gpk = :gpk AND #gsk <= :gsk",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#gpk"] = GsiPk, ["#gsk"] = GsiSk },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gpk"] = S(WorkGsiPk(handlerId)),
                    [":gsk"] = S(WorkGsiSk(now, "~"))
                },
                Limit = limit
            }, ct);
            foreach (var item in response.Items)
            {
                var work = Read<AwsDynamoExecutionWorkItem>(item);
                if (work is not null && work.DueAtUtc <= now) candidates.Add((work.RunId, work.DueAtUtc));
            }
        }

        return candidates.OrderBy(item => item.DueAtUtc).ThenBy(item => item.RunId, StringComparer.Ordinal)
            .Select(item => item.RunId).Distinct(StringComparer.Ordinal).Take(limit).ToList();
    }

    public async Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await ReadAsync<ExecutionRun>(RunPk(Require(runId, "Execution run id")), RunStateSk, ct);
        if (run is not null && !includeResult) run.Result = null;
        return run;
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        var limit = query.Limit ?? 100;
        if (limit <= 0) throw new InvalidOperationException("Execution run list limit must be positive.");
        await EnsureTableAsync(ct);
        var response = await _client.QueryAsync(new QueryRequest
        {
            TableName = _options.TableName,
            IndexName = GsiName,
            KeyConditionExpression = "#gpk = :gpk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#gpk"] = GsiPk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":gpk"] = S(RunsGsiPk()) },
            ScanIndexForward = false,
            Limit = limit
        }, ct);
        return response.Items.Select(Read<ExecutionRun>).Where(run => run is not null).Select(run => run!)
            .Where(run => Matches(run, query)).Take(limit).Select(run =>
            {
                if (!query.IncludeResult) run.Result = null;
                return run;
            }).ToList();
    }

    public async Task AppendHistoryAsync(ExecutionTraceEvent item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExecutionContractValidator.ValidateTraceEvent(item);
        await PutAsync(RunPk(item.RunId), $"history#{Ticks(item.TimestampUtc)}#{Require(item.Id, "Execution history id")}", "history", item, ct);
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, int limit = 100, CancellationToken ct = default) =>
        (await QueryRunPrefixAsync<ExecutionTraceEvent>(runId, "history#", limit, ct)).OrderBy(item => item.TimestampUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();

    public Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default) =>
        PutAsync(RunPk(checkpoint.RunId), $"checkpoint#{HashKey(Require(checkpoint.Key, "Checkpoint key"))}", "checkpoint", checkpoint, ct);

    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) =>
        ReadAsync<ExecutionCheckpoint>(RunPk(runId), $"checkpoint#{HashKey(Require(key, "Checkpoint key"))}", ct);

    public Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default) =>
        PutAsync(RunPk(artifact.RunId), $"artifact#{Ticks(artifact.CreatedAtUtc)}#{Require(artifact.Id, "Artifact id")}", "artifact", artifact, ct);

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) =>
        (await QueryRunPrefixAsync<ExecutionArtifact>(runId, "artifact#", null, ct)).OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();

    public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) =>
        (await ListArtifactsAsync(runId, ct)).FirstOrDefault(item => item.Id == artifactRef || item.Name == artifactRef);

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        await EnsureTableAsync(ct);
        var now = DateTime.UtcNow;
        var lease = NewLease(request, now);
        var put = new PutItemRequest
        {
            TableName = _options.TableName,
            Item = BuildLeaseItem(lease),
            ConditionExpression = "attribute_not_exists(#pk) OR #expires <= :now OR #owner = :owner",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Pk, ["#expires"] = LeaseExpiresAt, ["#owner"] = LeaseOwner },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":now"] = N(now.Ticks), [":owner"] = S(lease.OwnerId) }
        };
        try
        {
            await _client.PutItemAsync(put, ct);
            return lease;
        }
        catch (ConditionalCheckFailedException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<AwsDynamoExecutionLeaseClaim?> TryClaimExternalRunAsync(string runId, ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        await EnsureTableAsync(ct);
        var storedRun = await ReadStoredAsync(RunPk(Require(runId, "Execution run id")), RunStateSk, ct);
        var run = storedRun is null ? null : Read<ExecutionRun>(storedRun.Item);
        if (run is null) return null;
        var priorRun = storedRun!;
        var now = DateTime.UtcNow;
        var existingLease = await GetLeaseAsync(request.LeaseKey, ct);
        if (existingLease is not null && existingLease.ExpiresAtUtc > now) return null;

        var recovered = false;
        if (run.Status == ExecutionRunStatuses.Running)
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery);
            run.Status = ExecutionRunStatuses.Queued;
            run.CurrentStep = null;
            run.UpdatedAtUtc = now;
            recovered = true;
        }
        else if (!IsDue(run, now)) return null;

        if (run.CancellationRequested)
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
            run.Status = ExecutionRunStatuses.Cancelled;
            run.FailureClass = ExecutionFailureClasses.Cancelled;
            run.Error = "Execution run was cancelled.";
            run.CompletedAtUtc = now;
            run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
            run.CurrentStep = null;
            run.UpdatedAtUtc = now;
            var cancellationWrites = new List<TransactWriteItem>
            {
                PutWrite(BuildRunItem(run), "#json = :old", priorRun.Json),
                LeaseAvailabilityConditionWrite(request.LeaseKey, now)
            };
            var storedWait = await ReadStoredAsync(RunPk(run.Id), "wait", ct);
            if (storedWait is not null)
                cancellationWrites.Add(DeleteWrite(RunPk(run.Id), "wait", "#json = :old", storedWait.Json));
            AddWorkWrite(cancellationWrites, run);
            AddActiveWrite(cancellationWrites, -1);
            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = cancellationWrites }, ct);
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // A valid worker may have renewed its lease or another transition won. The
                // dispatcher will observe the durable state on its next at-least-once delivery.
            }
            return null;
        }

        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
        run.Status = ExecutionRunStatuses.Running;
        run.Attempt++;
        run.StartedAtUtc ??= now;
        run.UpdatedAtUtc = now;
        var lease = NewLease(request, now, run.Id);
        var writes = new List<TransactWriteItem>
        {
            PutWrite(BuildRunItem(run), "#json = :old", priorRun.Json),
            PutWrite(BuildLeaseItem(lease), "attribute_not_exists(#pk) OR #expires <= :now")
        };
        AddWorkWrite(writes, run);
        try
        {
            await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
            return new AwsDynamoExecutionLeaseClaim { Run = Clone(run), Lease = Clone(lease), Recovered = recovered };
        }
        catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<ExecutionLease?> RenewLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request);
        var stored = await GetStoredLeaseAsync(request.LeaseKey, ct);
        var now = DateTime.UtcNow;
        if (stored is null || stored.Lease.ExpiresAtUtc <= now || !string.Equals(stored.Lease.OwnerId, request.OwnerId, StringComparison.Ordinal)) return null;
        var existing = Clone(stored.Lease);
        existing.ExpiresAtUtc = now.AddSeconds(request.TtlSeconds);
        existing.Metadata = request.Metadata is null ? existing.Metadata : Clone(request.Metadata);
        try
        {
            await _client.PutItemAsync(new PutItemRequest
            {
                TableName = _options.TableName,
                Item = BuildLeaseItem(existing),
                ConditionExpression = "#json = :old AND #expires > :now",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json, ["#expires"] = LeaseExpiresAt },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(stored.Stored.Json), [":now"] = N(now.Ticks) }
            }, ct);
            return existing;
        }
        catch (ConditionalCheckFailedException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<ExecutionRun> UpdateExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, null, ct);
            var storedRun = await ReadStoredAsync(RunPk(lease.Lease.RunId!), RunStateSk, ct) ?? throw new InvalidOperationException("External worker run was not found.");
            var run = Read<ExecutionRun>(storedRun.Item) ?? throw new InvalidOperationException("External worker run was not found.");
            if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");
            ApplyUpdate(run, update);
            run.UpdatedAtUtc = DateTime.UtcNow;
            var writes = new List<TransactWriteItem>
            {
                LeaseConditionWrite(lease, run.UpdatedAtUtc),
                PutWrite(BuildRunItem(run), "#json = :old", storedRun.Json)
            };
            AddWorkWrite(writes, run);
            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return run;
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // A heartbeat or concurrent state transition changed the lease/run. Reload both
                // and reapply this idempotent update rather than allowing a stale writer through.
            }
        }

        throw new InvalidOperationException("External worker lease changed while updating the execution run.");
    }

    public async Task AppendHistoryUnderLeaseAsync(string leaseKey, string ownerId, ExecutionTraceEvent item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExecutionContractValidator.ValidateTraceEvent(item);
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, item.RunId, ct);
        await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                LeaseConditionWrite(lease, DateTime.UtcNow),
                PutWrite(BuildItem(RunPk(item.RunId), $"history#{Ticks(item.TimestampUtc)}#{Require(item.Id, "Execution history id")}", "history", item))
            ]
        }, ct);
    }

    public async Task PutArtifactUnderLeaseAsync(string leaseKey, string ownerId, ExecutionArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, artifact.RunId, ct);
        await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                LeaseConditionWrite(lease, DateTime.UtcNow),
                PutWrite(BuildItem(RunPk(artifact.RunId), $"artifact#{Ticks(artifact.CreatedAtUtc)}#{Require(artifact.Id, "Artifact id")}", "artifact", artifact))
            ]
        }, ct);
    }

    public async Task PutCheckpointUnderLeaseAsync(string leaseKey, string ownerId, ExecutionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, checkpoint.RunId, ct);
        await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                LeaseConditionWrite(lease, DateTime.UtcNow),
                PutWrite(BuildItem(RunPk(checkpoint.RunId), $"checkpoint#{HashKey(Require(checkpoint.Key, "Checkpoint key"))}", "checkpoint", checkpoint))
            ]
        }, ct);
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointUnderLeaseAsync(string leaseKey, string ownerId, string key, CancellationToken ct = default)
    {
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, null, ct);
        return await GetCheckpointAsync(lease.Lease.RunId!, key, ct);
    }

    public async Task<ExecutionWaitResult?> TakeWaitOutcomeUnderLeaseAsync(string leaseKey, string ownerId, string runId, string kind, string name, CancellationToken ct = default)
    {
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, runId, ct);
        return await TakeWaitOutcomeAsync(runId, kind, name, lease, ct);
    }

    public async Task<ExecutionExternalEvent?> TakeExternalEventUnderLeaseAsync(string leaseKey, string ownerId, string runId, string name, CancellationToken ct = default)
    {
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, runId, ct);
        return await TakeExternalEventAsync(runId, name, lease, ct);
    }

    public async Task<ExecutionRun> SuspendExternalRunUnderLeaseAsync(string leaseKey, string ownerId, AwsDynamoExecutionWait wait, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        var lease = await RequireStoredLeaseAsync(leaseKey, ownerId, wait.RunId, ct);
        var storedRun = await ReadStoredAsync(RunPk(wait.RunId), RunStateSk, ct) ?? throw new InvalidOperationException("External worker run was not found.");
        var run = Read<ExecutionRun>(storedRun.Item) ?? throw new InvalidOperationException("External worker run was not found.");
        if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");
        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
        run.Status = ExecutionRunStatuses.Waiting;
        run.ScheduledAtUtc = wait.FireAtUtc;
        run.CurrentStep = $"waiting:{wait.Kind}:{wait.Name}";
        run.UpdatedAtUtc = DateTime.UtcNow;
        var writes = new List<TransactWriteItem>
        {
            PutWrite(BuildRunItem(run), "#json = :old", storedRun.Json),
            PutWrite(BuildItem(RunPk(wait.RunId), "wait", "wait", wait)),
            DeleteLeaseWrite(lease, run.UpdatedAtUtc)
        };
        if (wait.Timer is not null)
            writes.Add(PutWrite(BuildItem(RunPk(wait.Timer.RunId ?? throw new InvalidOperationException("AWS execution timers must be run-owned.")), $"timer#{Ticks(wait.Timer.FireAtUtc)}#{wait.Timer.Id}", "timer", wait.Timer)));
        AddWorkWrite(writes, run);
        try
        {
            await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
        }
        catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("External worker lease changed while suspending the execution run.");
        }
        return run;
    }

    public async Task<AwsDynamoExecutionExternalCompletion> CompleteExternalRunUnderLeaseAsync(string leaseKey, string ownerId, ExecutionRunResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var lease = await GetStoredLeaseAsync(leaseKey, ct) ?? throw new InvalidOperationException("External worker lease is no longer active.");
            var storedRun = await ReadStoredAsync(RunPk(lease.Lease.RunId!), RunStateSk, ct) ?? throw new InvalidOperationException("External worker run was not found.");
            var run = Read<ExecutionRun>(storedRun.Item) ?? throw new InvalidOperationException("External worker run was not found.");
            if (!string.Equals(lease.Lease.OwnerId, ownerId, StringComparison.Ordinal)) throw new InvalidOperationException("External worker lease is no longer active.");
            if (IsCompletedLease(lease.Lease) && (ExecutionRunStatuses.IsTerminal(run.Status) || run.Status == ExecutionRunStatuses.Waiting))
                return new AwsDynamoExecutionExternalCompletion { Run = run, AlreadyCompleted = true };
            if (lease.Lease.ExpiresAtUtc <= DateTime.UtcNow) throw new InvalidOperationException("External worker lease is no longer active.");
            if (run.Status != ExecutionRunStatuses.Running) throw new InvalidOperationException("External worker run is not running.");

            var now = DateTime.UtcNow;
            var terminal = run.CancellationRequested && result.Status != ExecutionRunStatuses.TimedOut ? ExecutionRunStatuses.Cancelled : result.Status;
            ExecutionRunLifecycle.EnsureTransition(run.Status, terminal);
            run.Status = terminal;
            run.Result = Clone(result.Result);
            run.StatusDetails = Clone(result.StatusDetails);
            run.FailureClass = terminal == ExecutionRunStatuses.Cancelled ? ExecutionFailureClasses.Cancelled : result.FailureClass;
            run.Error = terminal == ExecutionRunStatuses.Cancelled ? "Execution run was cancelled." : result.Error;
            var retry = !run.CancellationRequested && terminal is ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut && run.Attempt < Math.Max(1, run.MaxAttempts);
            if (retry)
            {
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
                run.Status = ExecutionRunStatuses.Waiting;
                run.ScheduledAtUtc = now.Add(RetryDelay(run));
                run.CurrentStep = null;
            }
            else
            {
                run.CompletedAtUtc = now;
                run.DurationMs = (now - (run.StartedAtUtc ?? now)).TotalMilliseconds;
                if (run.Status == ExecutionRunStatuses.Succeeded) run.Progress = 1;
            }
            run.UpdatedAtUtc = now;
            var completedLease = Clone(lease.Lease);
            completedLease.ExpiresAtUtc = now;
            completedLease.Metadata ??= new JsonObject();
            completedLease.Metadata["state"] = "completed";
            var writes = new List<TransactWriteItem>
            {
                PutWrite(BuildRunItem(run), "#json = :old", storedRun.Json),
                PutLeaseWrite(completedLease, lease, now)
            };
            AddWorkWrite(writes, run);
            if (!ExecutionRunLifecycle.IsActive(run.Status)) AddActiveWrite(writes, -1);
            try
            {
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return new AwsDynamoExecutionExternalCompletion { Run = run, RetryScheduled = retry };
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cancellation writes only the run fence. Reloading it lets the same completion
                // turn into a durable cancellation instead of surfacing a transient race to the
                // external worker; renewed or replaced leases still fail their next check.
            }
        }

        throw new InvalidOperationException("External worker lease changed while completing the execution run.");
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        var lease = await GetStoredLeaseAsync(leaseKey, ct);
        if (lease is null || !string.Equals(lease.Lease.OwnerId, ownerId, StringComparison.Ordinal)) return false;
        try
        {
            await _client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _options.TableName,
                Key = LeaseKey(leaseKey),
                ConditionExpression = "#json = :old",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(lease.Stored.Json) }
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default) =>
        ReadAsync<ExecutionLease>(LeasePk(), LeaseSk(Require(leaseKey, "Lease key")), ct);

    public Task PutTimerAsync(ExecutionTimer timer, CancellationToken ct = default) =>
        PutAsync(RunPk(timer.RunId ?? throw new InvalidOperationException("AWS execution timers must be run-owned.")), $"timer#{Ticks(timer.FireAtUtc)}#{timer.Id}", "timer", timer, ct);

    public Task PutExternalEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default) =>
        PutAsync(RunPk(externalEvent.RunId ?? throw new InvalidOperationException("AWS execution events must be run-owned.")), $"event#{Ticks(externalEvent.RaisedAtUtc)}#{externalEvent.Id}", "event", externalEvent, ct);

    public Task PutWaitAsync(AwsDynamoExecutionWait wait, CancellationToken ct = default) =>
        PutAsync(RunPk(wait.RunId), "wait", "wait", wait, ct);

    public Task<AwsDynamoExecutionWait?> GetWaitAsync(string runId, CancellationToken ct = default) => ReadAsync<AwsDynamoExecutionWait>(RunPk(runId), "wait", ct);

    public Task DeleteWaitAsync(string runId, CancellationToken ct = default) => DeleteAsync(RunPk(runId), "wait", ct);

    public Task PutWaitOutcomeAsync(string runId, AwsDynamoExecutionWait wait, ExecutionWaitResult outcome, CancellationToken ct = default) =>
        PutAsync(RunPk(runId), $"outcome#{WaitKey(wait.Kind, wait.Name)}", "outcome", outcome, ct);

    public Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default) =>
        TakeWaitOutcomeAsync(runId, kind, name, null, ct);

    private async Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(string runId, string kind, string name, StoredLease? lease, CancellationToken ct)
    {
        var pk = RunPk(runId);
        var sk = $"outcome#{WaitKey(kind, name)}";
        var stored = await ReadStoredAsync(pk, sk, ct);
        if (stored is null) return null;
        var delete = new Delete
        {
            TableName = _options.TableName,
            Key = Key(pk, sk),
            ConditionExpression = "#json = :old",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(stored.Json) }
        };
        var writes = new List<TransactWriteItem>();
        if (lease is not null) writes.Add(LeaseConditionWrite(lease, DateTime.UtcNow));
        writes.Add(new TransactWriteItem { Delete = delete });
        try
        {
            await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
        }
        catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        return Read<ExecutionWaitResult>(stored.Item);
    }

    public Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, CancellationToken ct = default) =>
        TakeExternalEventAsync(runId, name, null, ct);

    private async Task<ExecutionExternalEvent?> TakeExternalEventAsync(string runId, string name, StoredLease? lease, CancellationToken ct)
    {
        var events = await QueryRunPrefixAsync<ExecutionExternalEvent>(runId, "event#", null, ct);
        foreach (var externalEvent in events.Where(item => item.Name == name).OrderBy(item => item.RaisedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var consumedSk = $"consumed#{HashKey(externalEvent.Id)}";
            try
            {
                var writes = new List<TransactWriteItem>();
                if (lease is not null) writes.Add(LeaseConditionWrite(lease, DateTime.UtcNow));
                writes.Add(new TransactWriteItem { Put = new Put
                {
                    TableName = _options.TableName,
                    Item = BuildItem(RunPk(runId), consumedSk, "consumed", new AwsDynamoExecutionConsumption { EventId = externalEvent.Id }),
                    ConditionExpression = "attribute_not_exists(#pk)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Pk }
                }});
                await _client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
                return externalEvent;
            }
            catch (TransactionCanceledException) when (!ct.IsCancellationRequested)
            {
                // Another worker consumed this immutable event first, or the lease changed.
            }
        }
        return null;
    }

    public async Task<AwsDynamoExecutionRunDeletion> DeleteRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var pk = RunPk(run.Id);
        var items = await QueryItemsAsync(pk, ct);
        var result = new AwsDynamoExecutionRunDeletion { RunId = run.Id };
        foreach (var item in items)
        {
            var sk = item[Sk].S;
            if (sk == RunStateSk) result.Runs++;
            else if (sk.StartsWith("history#", StringComparison.Ordinal)) result.History++;
            else if (sk.StartsWith("checkpoint#", StringComparison.Ordinal)) result.Checkpoints++;
            else if (sk.StartsWith("artifact#", StringComparison.Ordinal)) result.Artifacts++;
            else if (sk.StartsWith("timer#", StringComparison.Ordinal)) result.Timers++;
            else if (sk.StartsWith("event#", StringComparison.Ordinal)) result.ExternalEvents++;
            else result.Coordination++;
        }
        await BatchDeleteAsync(items, ct);
        var leases = await QueryItemsAsync(LeasePk(), ct);
        var runLeases = leases.Where(item =>
        {
            var lease = Read<ExecutionLease>(item);
            return string.Equals(lease?.RunId, run.Id, StringComparison.Ordinal);
        }).ToList();
        if (runLeases.Count > 0)
        {
            await BatchDeleteAsync(runLeases, ct);
            result.Coordination += runLeases.Count;
        }
        if (!string.IsNullOrWhiteSpace(run.IdempotencyKey)) await DeleteAsync(IdempotencyPk(), IdempotencyKey(BuildIdempotencyScopeKey(run)), ct);
        if (ExecutionRunLifecycle.IsActive(run.Status)) await AdjustActiveAsync(-1, ct);
        return result;
    }

    private Task EnsureTableAsync(CancellationToken ct)
    {
        Task task;
        lock (_tableEnsureLock)
        {
            // Provisioning is shared across concurrent callers. It deliberately runs without a
            // request cancellation token so one abandoned request cannot strand the store in a
            // half-created state for all future workers.
            task = _tableEnsureTask ??= EnsureTableCoreAsync();
        }
        return task.WaitAsync(ct);
    }

    private async Task EnsureTableCoreAsync()
    {
        try
        {
            await WaitForExecutionTableAsync();
        }
        catch (ResourceNotFoundException) when (_options.CreateTableIfMissing)
        {
            try
            {
                await _client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = _options.TableName,
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                    AttributeDefinitions =
                    [
                        new AttributeDefinition(Pk, ScalarAttributeType.S),
                        new AttributeDefinition(Sk, ScalarAttributeType.S),
                        new AttributeDefinition(GsiPk, ScalarAttributeType.S),
                        new AttributeDefinition(GsiSk, ScalarAttributeType.S)
                    ],
                    KeySchema = [new KeySchemaElement(Pk, KeyType.HASH), new KeySchemaElement(Sk, KeyType.RANGE)],
                    GlobalSecondaryIndexes =
                    [
                        new GlobalSecondaryIndex
                        {
                            IndexName = GsiName,
                            KeySchema = [new KeySchemaElement(GsiPk, KeyType.HASH), new KeySchemaElement(GsiSk, KeyType.RANGE)],
                            Projection = new Projection { ProjectionType = ProjectionType.ALL }
                        }
                    ]
                }, CancellationToken.None);
            }
            catch (ResourceInUseException)
            {
                // Another process created the configured table between our describe and create.
            }
            await WaitForExecutionTableAsync();
        }
    }

    private async Task WaitForExecutionTableAsync()
    {
        while (true)
        {
            var response = await _client.DescribeTableAsync(_options.TableName, CancellationToken.None);
            var table = response.Table;
            ValidateExecutionTable(table);
            var index = table.GlobalSecondaryIndexes!.Single(candidate => candidate.IndexName == GsiName);
            if (table.TableStatus == TableStatus.ACTIVE && index.IndexStatus == IndexStatus.ACTIVE) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }
    }

    private void ValidateExecutionTable(TableDescription table)
    {
        if (!HasKeySchema(table.KeySchema, Pk, KeyType.HASH) || !HasKeySchema(table.KeySchema, Sk, KeyType.RANGE))
            throw new InvalidOperationException($"DynamoDB execution table '{_options.TableName}' must use '{Pk}' as HASH and '{Sk}' as RANGE keys.");
        if (!HasStringAttribute(table.AttributeDefinitions, Pk) || !HasStringAttribute(table.AttributeDefinitions, Sk))
            throw new InvalidOperationException($"DynamoDB execution table '{_options.TableName}' must define '{Pk}' and '{Sk}' as string attributes.");
        var index = table.GlobalSecondaryIndexes?.SingleOrDefault(candidate => candidate.IndexName == GsiName);
        if (index is null || !HasKeySchema(index.KeySchema, GsiPk, KeyType.HASH) || !HasKeySchema(index.KeySchema, GsiSk, KeyType.RANGE) ||
            index.Projection?.ProjectionType != ProjectionType.ALL || !HasStringAttribute(table.AttributeDefinitions, GsiPk) || !HasStringAttribute(table.AttributeDefinitions, GsiSk))
            throw new InvalidOperationException($"DynamoDB execution table '{_options.TableName}' must define GSI '{GsiName}' over '{GsiPk}' (HASH) and '{GsiSk}' (RANGE).");
    }

    private static bool HasKeySchema(IEnumerable<KeySchemaElement>? schema, string name, string type) =>
        schema?.Any(element => element.AttributeName == name && element.KeyType == type) == true;

    private static bool HasStringAttribute(IEnumerable<AttributeDefinition>? definitions, string name) =>
        definitions?.Any(definition => definition.AttributeName == name && definition.AttributeType == ScalarAttributeType.S) == true;

    private async Task PutAsync<T>(string pk, string sk, string kind, T value, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        await _client.PutItemAsync(new PutItemRequest { TableName = _options.TableName, Item = BuildItem(pk, sk, kind, value) }, ct);
    }

    private async Task DeleteAsync(string pk, string sk, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        await _client.DeleteItemAsync(new DeleteItemRequest { TableName = _options.TableName, Key = Key(pk, sk) }, ct);
    }

    private async Task<T?> ReadAsync<T>(string pk, string sk, CancellationToken ct)
    {
        var stored = await ReadStoredAsync(pk, sk, ct);
        return stored is null ? default : Read<T>(stored.Item);
    }

    private async Task<StoredItem?> ReadStoredAsync(string pk, string sk, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var response = await _client.GetItemAsync(new GetItemRequest { TableName = _options.TableName, Key = Key(pk, sk), ConsistentRead = true }, ct);
        return response.IsItemSet ? new StoredItem(response.Item, response.Item[Json].S) : null;
    }

    private async Task<IReadOnlyList<T>> QueryRunPrefixAsync<T>(string runId, string prefix, int? limit, CancellationToken ct)
    {
        var items = await QueryPrefixAsync(RunPk(runId), prefix, limit, ct);
        return items.Select(Read<T>).Where(value => value is not null).Select(value => value!).ToList();
    }

    private async Task<List<Dictionary<string, AttributeValue>>> QueryPrefixAsync(string pk, string prefix, int? limit, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var results = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? last = null;
        do
        {
            var response = await _client.QueryAsync(new QueryRequest
            {
                TableName = _options.TableName,
                KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :prefix)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Pk, ["#sk"] = Sk },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(pk), [":prefix"] = S(prefix) },
                ExclusiveStartKey = last,
                Limit = limit
            }, ct);
            results.AddRange(response.Items);
            if (limit.HasValue && results.Count >= limit.Value) break;
            last = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        } while (last is not null);
        return results;
    }

    private async Task<List<Dictionary<string, AttributeValue>>> QueryItemsAsync(string pk, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var results = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? last = null;
        do
        {
            var response = await _client.QueryAsync(new QueryRequest
            {
                TableName = _options.TableName,
                KeyConditionExpression = "#pk = :pk",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Pk },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(pk) },
                ExclusiveStartKey = last
            }, ct);
            results.AddRange(response.Items);
            last = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        } while (last is not null);
        return results;
    }

    private async Task BatchDeleteAsync(IEnumerable<Dictionary<string, AttributeValue>> items, CancellationToken ct)
    {
        foreach (var batch in items.Chunk(25))
        {
            var pending = batch.Select(item => new WriteRequest { DeleteRequest = new DeleteRequest { Key = Key(item[Pk].S, item[Sk].S) } }).ToList();
            while (pending.Count > 0)
            {
                var response = await _client.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>> { [_options.TableName] = pending }
                }, ct);
                pending = response.UnprocessedItems.TryGetValue(_options.TableName, out var unprocessed) ? unprocessed : [];
                if (pending.Count > 0) await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    private async Task<StoredLease?> GetStoredLeaseAsync(string leaseKey, CancellationToken ct)
    {
        var normalized = Require(leaseKey, "Lease key");
        var stored = await ReadStoredAsync(LeasePk(), LeaseSk(normalized), ct);
        var lease = stored is null ? null : Read<ExecutionLease>(stored.Item);
        return lease is null || stored is null ? null : new StoredLease(stored, lease);
    }

    private async Task<StoredLease> RequireStoredLeaseAsync(string leaseKey, string ownerId, string? runId, CancellationToken ct)
    {
        var lease = await GetStoredLeaseAsync(leaseKey, ct);
        if (lease is null || lease.Lease.ExpiresAtUtc <= DateTime.UtcNow || !string.Equals(lease.Lease.OwnerId, ownerId, StringComparison.Ordinal) ||
            (runId is not null && !string.Equals(lease.Lease.RunId, runId, StringComparison.Ordinal)))
            throw new InvalidOperationException("External worker lease is no longer active.");
        return lease;
    }

    private TransactWriteItem LeaseConditionWrite(StoredLease lease, DateTime now) => new()
    {
        ConditionCheck = new ConditionCheck
        {
            TableName = _options.TableName,
            Key = LeaseKey(lease.Lease.LeaseKey),
            ConditionExpression = "#json = :old AND #expires > :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json, ["#expires"] = LeaseExpiresAt },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(lease.Stored.Json), [":now"] = N(now.Ticks) }
        }
    };

    private TransactWriteItem LeaseAvailabilityConditionWrite(string leaseKey, DateTime now) => new()
    {
        ConditionCheck = new ConditionCheck
        {
            TableName = _options.TableName,
            Key = LeaseKey(leaseKey),
            ConditionExpression = "attribute_not_exists(#pk) OR #expires <= :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Pk, ["#expires"] = LeaseExpiresAt },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":now"] = N(now.Ticks) }
        }
    };

    private TransactWriteItem PutLeaseWrite(ExecutionLease replacement, StoredLease expected, DateTime now) => new()
    {
        Put = new Put
        {
            TableName = _options.TableName,
            Item = BuildLeaseItem(replacement),
            ConditionExpression = "#json = :old AND #expires > :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json, ["#expires"] = LeaseExpiresAt },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(expected.Stored.Json), [":now"] = N(now.Ticks) }
        }
    };

    private TransactWriteItem DeleteLeaseWrite(StoredLease expected, DateTime now) => new()
    {
        Delete = new Delete
        {
            TableName = _options.TableName,
            Key = LeaseKey(expected.Lease.LeaseKey),
            ConditionExpression = "#json = :old AND #expires > :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#json"] = Json, ["#expires"] = LeaseExpiresAt },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":old"] = S(expected.Stored.Json), [":now"] = N(now.Ticks) }
        }
    };

    private static ExecutionLease NewLease(ExecutionLeaseRequest request, DateTime now, string? runId = null) => new()
    {
        LeaseKey = Require(request.LeaseKey, "Lease key"),
        OwnerId = Require(request.OwnerId, "Lease owner id"),
        RunId = runId ?? request.RunId,
        AcquiredAtUtc = now,
        ExpiresAtUtc = now.AddSeconds(request.TtlSeconds),
        Metadata = request.Metadata is null ? null : Clone(request.Metadata)
    };

    private void AddWorkWrite(List<TransactWriteItem> writes, ExecutionRun run)
    {
        if (IsRunnable(run)) writes.Add(PutWrite(BuildWorkItem(run)));
        else writes.Add(new TransactWriteItem { Delete = new Delete { TableName = _options.TableName, Key = Key(RunPk(run.Id), RunWorkSk) } });
    }

    private void AddActiveWrite(List<TransactWriteItem> writes, int delta, int? maximum = null)
    {
        if (delta == 0) return;
        var update = new Update
        {
            TableName = _options.TableName,
            Key = Key(MetadataPk(), "active-runs"),
            UpdateExpression = "ADD #active :delta",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#active"] = Active },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":delta"] = N(delta) }
        };
        if (maximum.HasValue)
        {
            update.ConditionExpression = "attribute_not_exists(#active) OR #active < :max";
            update.ExpressionAttributeValues[":max"] = N(maximum.Value);
        }
        writes.Add(new TransactWriteItem { Update = update });
    }

    private async Task AdjustActiveAsync(int delta, CancellationToken ct)
    {
        if (delta == 0) return;
        await EnsureTableAsync(ct);
        await _client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _options.TableName,
            Key = Key(MetadataPk(), "active-runs"),
            UpdateExpression = "ADD #active :delta",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#active"] = Active },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":delta"] = N(delta) }
        }, ct);
    }

    private Dictionary<string, AttributeValue> BuildRunItem(ExecutionRun run)
    {
        var item = BuildItem(RunPk(run.Id), RunStateSk, "run", run);
        item[Active] = N(ExecutionRunLifecycle.IsActive(run.Status) ? 1 : 0);
        item[GsiPk] = S(RunsGsiPk());
        item[GsiSk] = S($"{Ticks(run.CreatedAtUtc)}#{run.Id}");
        return item;
    }

    private Dictionary<string, AttributeValue> BuildWorkItem(ExecutionRun run)
    {
        var dueAt = run.ScheduledAtUtc?.ToUniversalTime() ?? run.UpdatedAtUtc.ToUniversalTime();
        var item = BuildItem(RunPk(run.Id), RunWorkSk, "work", new AwsDynamoExecutionWorkItem { RunId = run.Id, DueAtUtc = dueAt });
        item[GsiPk] = S(WorkGsiPk(run.HandlerId));
        item[GsiSk] = S(WorkGsiSk(dueAt, run.Id));
        return item;
    }

    private Dictionary<string, AttributeValue> BuildLeaseItem(ExecutionLease lease)
    {
        var item = BuildItem(LeasePk(), LeaseSk(lease.LeaseKey), "lease", lease);
        item[LeaseOwner] = S(lease.OwnerId);
        item[LeaseExpiresAt] = N(lease.ExpiresAtUtc.Ticks);
        return item;
    }

    private static Dictionary<string, AttributeValue> BuildItem<T>(string pk, string sk, string kind, T value) => new()
    {
        [Pk] = S(pk),
        [Sk] = S(sk),
        [Kind] = S(kind),
        [Json] = S(Serialize(value))
    };

    private TransactWriteItem PutWrite(Dictionary<string, AttributeValue> item, string? condition = null, string? previousJson = null)
    {
        var put = new Put { TableName = _options.TableName, Item = item };
        if (!string.IsNullOrWhiteSpace(condition))
        {
            put.ConditionExpression = condition;
            var names = new Dictionary<string, string>();
            var values = new Dictionary<string, AttributeValue>();
            if (condition.Contains("#pk", StringComparison.Ordinal)) names["#pk"] = Pk;
            if (condition.Contains("#json", StringComparison.Ordinal)) names["#json"] = Json;
            if (condition.Contains("#expires", StringComparison.Ordinal)) names["#expires"] = LeaseExpiresAt;
            if (condition.Contains(":now", StringComparison.Ordinal)) values[":now"] = N(DateTime.UtcNow.Ticks);
            if (condition.Contains(":old", StringComparison.Ordinal) && previousJson is not null) values[":old"] = S(previousJson);
            if (names.Count > 0) put.ExpressionAttributeNames = names;
            if (values.Count > 0) put.ExpressionAttributeValues = values;
        }
        return new TransactWriteItem { Put = put };
    }

    private TransactWriteItem DeleteWrite(string pk, string sk, string? condition = null, string? previousJson = null)
    {
        var delete = new Delete { TableName = _options.TableName, Key = Key(pk, sk) };
        if (!string.IsNullOrWhiteSpace(condition))
        {
            delete.ConditionExpression = condition;
            var names = new Dictionary<string, string>();
            var values = new Dictionary<string, AttributeValue>();
            if (condition.Contains("#pk", StringComparison.Ordinal)) names["#pk"] = Pk;
            if (condition.Contains("#json", StringComparison.Ordinal)) names["#json"] = Json;
            if (condition.Contains(":old", StringComparison.Ordinal) && previousJson is not null) values[":old"] = S(previousJson);
            if (names.Count > 0) delete.ExpressionAttributeNames = names;
            if (values.Count > 0) delete.ExpressionAttributeValues = values;
        }
        return new TransactWriteItem { Delete = delete };
    }

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new() { [Pk] = S(pk), [Sk] = S(sk) };
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static T? Read<T>(Dictionary<string, AttributeValue> item) => item.TryGetValue(Json, out var value) ? JsonSerializer.Deserialize<T>(value.S, JsonOptions) : default;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), JsonOptions)!;
    private static JsonNode? Clone(JsonNode? value) => value is null ? null : JsonNode.Parse(value.ToJsonString(JsonOptions));
    private static string Require(string value, string description) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{description} is required.") : value.Trim();
    private static string HashKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Ticks(DateTime value) => value.ToUniversalTime().Ticks.ToString("D19", CultureInfo.InvariantCulture);
    private static string WaitKey(string kind, string name) => HashKey(kind + "\n" + name);
    private static string BuildIdempotencyScopeKey(ExecutionRun run) => string.Join("\n", run.Scope?.ProductId ?? string.Empty, run.Scope?.TenantId ?? string.Empty, run.HandlerId, run.IdempotencyKey ?? string.Empty);
    private static bool IsRunnable(ExecutionRun run) => run.Status == ExecutionRunStatuses.Queued || (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue);
    private static bool IsDue(ExecutionRun run, DateTime now) => run.Status == ExecutionRunStatuses.Queued || (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc <= now);
    private static bool IsCompletedLease(ExecutionLease lease) => string.Equals(lease.Metadata?["state"]?.GetValue<string>(), "completed", StringComparison.Ordinal);
    private static TimeSpan RetryDelay(ExecutionRun run)
    {
        var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
        var seconds = Math.Min(Math.Max(policy.InitialDelaySeconds, 0) * Math.Pow(Math.Max(policy.BackoffMultiplier, 1), Math.Max(0, run.Attempt - 1)), Math.Max(policy.InitialDelaySeconds, policy.MaxDelaySeconds));
        return TimeSpan.FromSeconds(seconds);
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
        run.Result = update.Result is null ? run.Result : Clone(update.Result);
        run.StatusDetails = update.StatusDetails is null ? run.StatusDetails : Clone(update.StatusDetails);
    }
    private static bool Matches(ExecutionRun run, ExecutionRunQuery query) =>
        (string.IsNullOrWhiteSpace(query.HandlerId) || run.HandlerId == query.HandlerId) &&
        (string.IsNullOrWhiteSpace(query.PluginId) || run.PluginId == query.PluginId) &&
        (string.IsNullOrWhiteSpace(query.Status) || run.Status == query.Status) &&
        (string.IsNullOrWhiteSpace(query.CorrelationId) || run.CorrelationId == query.CorrelationId) &&
        (string.IsNullOrWhiteSpace(query.IdempotencyKey) || run.IdempotencyKey == query.IdempotencyKey) &&
        (!query.CreatedAfterUtc.HasValue || run.CreatedAtUtc >= query.CreatedAfterUtc) &&
        (!query.CreatedBeforeUtc.HasValue || run.CreatedAtUtc <= query.CreatedBeforeUtc) &&
        (!query.UpdatedAfterUtc.HasValue || run.UpdatedAtUtc >= query.UpdatedAfterUtc) &&
        (!query.UpdatedBeforeUtc.HasValue || run.UpdatedAtUtc <= query.UpdatedBeforeUtc) &&
        query.Tags.All(filter => run.Tags.TryGetValue(filter.Key, out var value) && value == filter.Value);

    private string RunPk(string runId) => $"{_options.Root}#run#{Require(runId, "Execution run id")}";
    private string LeasePk() => $"{_options.Root}#leases";
    private string LeaseSk(string leaseKey) => HashKey(leaseKey);
    private Dictionary<string, AttributeValue> LeaseKey(string leaseKey) => Key(LeasePk(), LeaseSk(Require(leaseKey, "Lease key")));
    private string IdempotencyPk() => $"{_options.Root}#idempotency";
    private static string IdempotencyKey(string scopeKey) => HashKey(scopeKey);
    private string MetadataPk() => $"{_options.Root}#metadata";
    private string RunsGsiPk() => $"{_options.Root}#runs";
    private string WorkGsiPk(string handlerId) => $"{_options.Root}#work#{HashKey(handlerId)}";
    private static string WorkGsiSk(DateTime dueAtUtc, string runId) => $"{Ticks(dueAtUtc)}#{runId}";

    private sealed record StoredItem(Dictionary<string, AttributeValue> Item, string Json);
    private sealed record StoredLease(StoredItem Stored, ExecutionLease Lease);
    private sealed class AwsDynamoExecutionReservation { public string RunId { get; init; } = string.Empty; public string HandlerId { get; init; } = string.Empty; public string PayloadHash { get; init; } = string.Empty; }
    private sealed class AwsDynamoExecutionWorkItem { public string RunId { get; init; } = string.Empty; public DateTime DueAtUtc { get; init; } }
    private sealed class AwsDynamoExecutionConsumption { public string EventId { get; init; } = string.Empty; }
}

public sealed class DynamoDbExecutionStateStoreOptions
{
    public string TableName { get; init; } = string.Empty;
    public string Root { get; init; } = "vyral-execution";
    public bool CreateTableIfMissing { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TableName) || TableName.Length is < 3 or > 255 || !TableName.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
            throw new InvalidOperationException("DynamoDB execution table name must contain only letters, digits, '_', '-', or '.'.");
        if (string.IsNullOrWhiteSpace(Root) || Root.Length > 256 || Root.Contains('\n') || Root.Contains('\r'))
            throw new InvalidOperationException("DynamoDB execution root must be non-empty, bounded, and single-line.");
    }
}
