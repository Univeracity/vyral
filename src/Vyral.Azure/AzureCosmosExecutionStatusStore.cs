using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using NewtonsoftJsonProperty = Newtonsoft.Json.JsonPropertyAttribute;
using Vyral.Execution;
using Vyral.Execution.AzureDurable;

namespace Vyral.Azure;

/// <summary>
/// Durable execution status store backed by one Cosmos NoSQL container. Run-scoped state shares a
/// partition, while named leases use a stable lease-key partition. The store deliberately keeps
/// provider ETags private and serializes only Vyral execution contracts in its document payloads.
/// </summary>
public sealed class AzureCosmosExecutionStatusStore : IAzureDurableExecutionStatusStore
{
    private const string PartitionKeyPath = "/partitionKey";
    private const string RunKind = "run";
    private const string EventKind = "event";
    private const string ArtifactKind = "artifact";
    private const string CheckpointKind = "checkpoint";
    private const string LeaseKind = "lease";
    private const string TimerKind = "timer";
    private const string ExternalEventKind = "external-event";
    private const string WaitKind = "wait";
    private const string WaitOutcomeKind = "wait-outcome";
    private const string WaitDocumentId = "wait";
    private const string WaitOutcomeDocumentId = "wait-outcome";
    private readonly Database _database;
    private readonly Container _container;
    private readonly AzureDurableExecutionOptions _options;
    private readonly object _initializeSync = new();
    private Task? _initialize;

    public AzureCosmosExecutionStatusStore(
        CosmosClient client,
        string databaseId,
        string containerId,
        AzureDurableExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentException("Cosmos database id is required.", nameof(databaseId));
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentException("Cosmos container id is required.", nameof(containerId));

        _database = client.GetDatabase(databaseId.Trim());
        _container = _database.GetContainer(containerId.Trim());
        _options = options ?? new AzureDurableExecutionOptions { StatusStoreName = containerId.Trim() };
    }

    public async Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        RequireId(runId, "Run id");
        var document = await ReadDocumentAsync(RunPartition(runId), "run", ct);
        if (document is null) return null;

        var run = Deserialize<ExecutionRun>(document.Document.Json);
        if (!includeResult) run.Result = null;
        return run;
    }

    public async Task<ExecutionRun?> FindRunByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        RequireId(idempotencyKey, "Idempotency key");
        var documents = await QueryDocumentsAsync(
            new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind AND c.idempotencyKey = @idempotencyKey")
                .WithParameter("@kind", RunKind)
                .WithParameter("@idempotencyKey", idempotencyKey.Trim()),
            ct);
        return documents
            .Select(document => Deserialize<ExecutionRun>(document.Json))
            .OrderBy(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        var limit = ValidateLimit(query.Limit);
        var documents = await QueryDocumentsAsync(BuildRunQuery(query), ct);

        return documents
            .Select(document => Deserialize<ExecutionRun>(document.Json))
            .Where(run => MatchesQuery(run, query))
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenBy(run => run.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(run => WithoutResult(run, query.IncludeResult))
            .ToList();
    }

    public async Task<int> CountActiveRunsAsync(CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.kind = @kind AND ARRAY_CONTAINS(@activeStatuses, c.status)")
            .WithParameter("@kind", RunKind)
            .WithParameter("@activeStatuses", ExecutionRunLifecycle.ActiveStatuses);
        return await QuerySingleValueAsync<int>(query, ct);
    }

    public async Task<AzureDurableRunCreation> CreateRunIfAbsentAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        RequireId(run.Id, "Run id");
        await EnsureInitializedAsync(ct);

        var document = BuildRunDocument(run);
        try
        {
            await _container.CreateItemAsync(document, new PartitionKey(document.PartitionKey), cancellationToken: ct);
            return new AzureDurableRunCreation { Created = true, Run = Clone(run) };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetRunAsync(run.Id, true, ct);
            if (existing is null)
            {
                throw new InvalidOperationException($"Cosmos run reservation for '{run.Id}' conflicted without a readable run.", ex);
            }

            return new AzureDurableRunCreation { Created = false, Run = existing };
        }
    }

    public async Task<ExecutionRun> UpsertRunAsync(ExecutionRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        RequireId(run.Id, "Run id");
        await EnsureInitializedAsync(ct);
        var partitionKey = RunPartition(run.Id);
        const string id = "run";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existing = await ReadDocumentAsync(partitionKey, id, ct);
            if (existing is not null)
            {
                var current = Deserialize<ExecutionRun>(existing.Document.Json);
                if (ExecutionRunStatuses.IsTerminal(current.Status) ||
                    (current.CancellationRequested && run.Status != ExecutionRunStatuses.Cancelled))
                {
                    return Clone(current);
                }
            }

            var document = BuildRunDocument(run);
            try
            {
                if (existing is null)
                {
                    await _container.CreateItemAsync(document, new PartitionKey(partitionKey), cancellationToken: ct);
                }
                else
                {
                    await _container.ReplaceItemAsync(
                        document,
                        id,
                        new PartitionKey(partitionKey),
                        new ItemRequestOptions { IfMatchEtag = existing.ETag },
                        ct);
                }

                return Clone(run);
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 4)
                {
                    throw new InvalidOperationException($"Execution run '{run.Id}' changed repeatedly while persisting it.", ex);
                }
            }
        }

        throw new InvalidOperationException($"Execution run '{run.Id}' could not be persisted.");
    }

    public Task AppendEventAsync(ExecutionTraceEvent traceEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);
        RequireId(traceEvent.RunId, "Trace-event run id");
        return UpsertDocumentAsync(BuildDocument(
            "event:" + RequireId(traceEvent.Id, "Trace-event id"),
            RunPartition(traceEvent.RunId),
            EventKind,
            traceEvent,
            runId: traceEvent.RunId,
            createdAtUtc: traceEvent.TimestampUtc), ct);
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
    {
        RequireId(runId, "Run id");
        query ??= new ExecutionHistoryQuery();
        var limit = ValidateLimit(query.Limit);
        var documents = await QueryPartitionKindAsync(RunPartition(runId), EventKind, ct);
        return documents
            .Select(document => Deserialize<ExecutionTraceEvent>(document.Json))
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.SequenceId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public Task PutArtifactAsync(ExecutionArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireId(artifact.RunId, "Artifact run id");
        return UpsertDocumentAsync(BuildDocument(
            "artifact:" + RequireId(artifact.Id, "Artifact id"),
            RunPartition(artifact.RunId),
            ArtifactKind,
            artifact,
            runId: artifact.RunId,
            name: artifact.Name,
            createdAtUtc: artifact.CreatedAtUtc), ct);
    }

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
    {
        RequireId(runId, "Run id");
        var documents = await QueryPartitionKindAsync(RunPartition(runId), ArtifactKind, ct);
        return documents
            .Select(document => Deserialize<ExecutionArtifact>(document.Json))
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
    {
        RequireId(runId, "Run id");
        RequireId(artifactRef, "Artifact reference");
        var partitionKey = RunPartition(runId);
        var byId = await ReadDocumentAsync(partitionKey, "artifact:" + artifactRef.Trim(), ct);
        if (byId is not null && string.Equals(byId.Document.Kind, ArtifactKind, StringComparison.Ordinal))
        {
            return Deserialize<ExecutionArtifact>(byId.Document.Json);
        }

        var artifacts = await QueryDocumentsAsync(
            new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind AND c.name = @name")
                .WithParameter("@kind", ArtifactKind)
                .WithParameter("@name", artifactRef.Trim()),
            ct,
            new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });
        return artifacts
            .Select(document => Deserialize<ExecutionArtifact>(document.Json))
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public Task PutCheckpointAsync(ExecutionCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireId(checkpoint.RunId, "Checkpoint run id");
        return UpsertDocumentAsync(BuildDocument(
            "checkpoint:" + HashId(RequireId(checkpoint.Key, "Checkpoint key")),
            RunPartition(checkpoint.RunId),
            CheckpointKind,
            checkpoint,
            runId: checkpoint.RunId,
            name: checkpoint.Key,
            updatedAtUtc: checkpoint.UpdatedAtUtc), ct);
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
    {
        RequireId(runId, "Run id");
        var document = await ReadDocumentAsync(RunPartition(runId), "checkpoint:" + HashId(RequireId(key, "Checkpoint key")), ct);
        return document is null ? null : Deserialize<ExecutionCheckpoint>(document.Document.Json);
    }

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        RequireId(lease.LeaseKey, "Lease key");
        RequireId(lease.OwnerId, "Lease owner id");
        var partitionKey = LeasePartition(lease.LeaseKey);
        const string id = "lease";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existing = await ReadDocumentAsync(partitionKey, id, ct);
            var now = DateTime.UtcNow;
            if (existing is not null)
            {
                var current = Deserialize<ExecutionLease>(existing.Document.Json);
                if (current.ExpiresAtUtc > now && !string.Equals(current.OwnerId, lease.OwnerId, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            var replacement = BuildDocument(id, partitionKey, LeaseKind, lease, runId: lease.RunId, updatedAtUtc: now);
            try
            {
                await EnsureInitializedAsync(ct);
                if (existing is null)
                {
                    await _container.CreateItemAsync(replacement, new PartitionKey(partitionKey), cancellationToken: ct);
                }
                else
                {
                    await _container.ReplaceItemAsync(
                        replacement,
                        id,
                        new PartitionKey(partitionKey),
                        new ItemRequestOptions { IfMatchEtag = existing.ETag },
                        ct);
                }

                return Clone(lease);
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 4) throw new InvalidOperationException($"Lease '{lease.LeaseKey}' changed repeatedly while acquiring it.", ex);
            }
        }

        throw new InvalidOperationException($"Lease '{lease.LeaseKey}' could not be acquired.");
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        RequireId(leaseKey, "Lease key");
        RequireId(ownerId, "Lease owner id");
        var partitionKey = LeasePartition(leaseKey);
        const string id = "lease";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var existing = await ReadDocumentAsync(partitionKey, id, ct);
            if (existing is null || !string.Equals(Deserialize<ExecutionLease>(existing.Document.Json).OwnerId, ownerId, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                await _container.DeleteItemAsync<StoredDocument>(
                    id,
                    new PartitionKey(partitionKey),
                    new ItemRequestOptions { IfMatchEtag = existing.ETag },
                    ct);
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 4)
            {
            }
        }

        return false;
    }

    public async Task<ExecutionLease?> GetLeaseAsync(string leaseKey, CancellationToken ct = default)
    {
        RequireId(leaseKey, "Lease key");
        var document = await ReadDocumentAsync(LeasePartition(leaseKey), "lease", ct);
        return document is null ? null : Deserialize<ExecutionLease>(document.Document.Json);
    }

    public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimer timer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var runId = RequireId(timer.RunId, "Timer run id");
        return UpsertAndReturnAsync(BuildDocument(
            "timer:" + RequireId(timer.Id, "Timer id"),
            RunPartition(runId),
            TimerKind,
            timer,
            runId: runId,
            name: timer.Name,
            createdAtUtc: timer.CreatedAtUtc), timer, ct);
    }

    public Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEvent externalEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(externalEvent);
        var runId = RequireId(externalEvent.RunId, "External-event run id");
        return UpsertAndReturnAsync(BuildDocument(
            "external-event:" + RequireId(externalEvent.Id, "External-event id"),
            RunPartition(runId),
            ExternalEventKind,
            externalEvent,
            runId: runId,
            name: externalEvent.Name,
            createdAtUtc: externalEvent.RaisedAtUtc), externalEvent, ct);
    }

    public async Task<AzureDurableWait?> GetDurableWaitAsync(string runId, CancellationToken ct = default)
    {
        var document = await ReadDocumentAsync(RunPartition(RequireId(runId, "Run id")), WaitDocumentId, ct);
        return document is null ? null : Deserialize<AzureDurableWait>(document.Document.Json);
    }

    public async Task<AzureDurableWait> RegisterDurableWaitAsync(AzureDurableWait wait, string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        var normalizedRunId = RequireId(runId, "Run id");
        var normalizedWait = NormalizeWait(wait);
        var partitionKey = RunPartition(normalizedRunId);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var runDocument = await ReadDocumentAsync(partitionKey, "run", ct)
                ?? throw new InvalidOperationException($"Execution run '{normalizedRunId}' was not found.");
            var existingWait = await ReadDocumentAsync(partitionKey, WaitDocumentId, ct);
            if (existingWait is not null)
            {
                var persisted = Deserialize<AzureDurableWait>(existingWait.Document.Json);
                if (WaitsMatch(persisted, normalizedWait)) return Clone(persisted);
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' already has a different durable wait.");
            }

            var run = Deserialize<ExecutionRun>(runDocument.Document.Json);
            if (ExecutionRunStatuses.IsTerminal(run.Status) || run.CancellationRequested)
            {
                throw new OperationCanceledException($"Execution run '{normalizedRunId}' was cancelled before its durable wait could be registered.");
            }

            if (run.Status != ExecutionRunStatuses.Running)
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' is not running and cannot register a durable wait.");
            }

            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
            run.Status = ExecutionRunStatuses.Waiting;
            run.CurrentStep = $"waiting:{normalizedWait.Kind}:{normalizedWait.Name}";
            run.ScheduledAtUtc = normalizedWait.FireAtUtc;
            run.UpdatedAtUtc = DateTime.UtcNow;

            var batch = _container.CreateTransactionalBatch(new PartitionKey(partitionKey))
                .ReplaceItem("run", BuildRunDocument(run), new TransactionalBatchItemRequestOptions { IfMatchEtag = runDocument.ETag })
                .CreateItem(BuildDocument(
                    WaitDocumentId,
                    partitionKey,
                    WaitKind,
                    normalizedWait,
                    runId: normalizedRunId,
                    name: normalizedWait.Name,
                    createdAtUtc: DateTime.UtcNow));
            if (normalizedWait.Timer is not null)
            {
                batch.UpsertItem(BuildDocument(
                    "timer:" + RequireId(normalizedWait.Timer.Id, "Timer id"),
                    partitionKey,
                    TimerKind,
                    normalizedWait.Timer,
                    runId: normalizedRunId,
                    name: normalizedWait.Timer.Name,
                    createdAtUtc: normalizedWait.Timer.CreatedAtUtc));
            }

            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return Clone(normalizedWait);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 4) throw new InvalidOperationException($"Execution run '{normalizedRunId}' changed repeatedly while registering a durable wait.");
                continue;
            }

            throw new InvalidOperationException($"Could not register a durable wait for execution run '{normalizedRunId}': {response.StatusCode}.");
        }

        throw new InvalidOperationException($"Could not register a durable wait for execution run '{normalizedRunId}'.");
    }

    public async Task<ExecutionRun> ResumeDurableWaitAsync(string runId, ExecutionWaitResult outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var normalizedRunId = RequireId(runId, "Run id");
        var partitionKey = RunPartition(normalizedRunId);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var runDocument = await ReadDocumentAsync(partitionKey, "run", ct)
                ?? throw new InvalidOperationException($"Execution run '{normalizedRunId}' was not found.");
            var run = Deserialize<ExecutionRun>(runDocument.Document.Json);
            if (ExecutionRunStatuses.IsTerminal(run.Status)) return Clone(run);

            var waitDocument = await ReadDocumentAsync(partitionKey, WaitDocumentId, ct);
            if (waitDocument is null)
            {
                var existingOutcome = await ReadDocumentAsync(partitionKey, WaitOutcomeDocumentId, ct);
                if (existingOutcome is not null) return Clone(run);

                // A Durable Task activity may be replayed after the first wake result was
                // persisted and consumed by the handler replay. There is no remaining wait or
                // outcome document in that case, but a queued/running/terminal run is the
                // authoritative idempotent result rather than an error.
                if (run.Status != ExecutionRunStatuses.Waiting) return Clone(run);
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' has no durable wait to resume.");
            }

            var wait = Deserialize<AzureDurableWait>(waitDocument.Document.Json);
            var normalizedOutcome = NormalizeWaitOutcome(outcome, wait);
            if (run.Status != ExecutionRunStatuses.Waiting)
            {
                throw new InvalidOperationException($"Execution run '{normalizedRunId}' is not waiting and cannot resume a durable wait.");
            }

            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued);
            run.Status = ExecutionRunStatuses.Queued;
            run.CurrentStep = null;
            run.ScheduledAtUtc = null;
            run.UpdatedAtUtc = DateTime.UtcNow;
            var batch = _container.CreateTransactionalBatch(new PartitionKey(partitionKey))
                .ReplaceItem("run", BuildRunDocument(run), new TransactionalBatchItemRequestOptions { IfMatchEtag = runDocument.ETag })
                .UpsertItem(BuildDocument(
                    WaitOutcomeDocumentId,
                    partitionKey,
                    WaitOutcomeKind,
                    normalizedOutcome,
                    runId: normalizedRunId,
                    name: normalizedOutcome.Name,
                    createdAtUtc: DateTime.UtcNow))
                .DeleteItem(WaitDocumentId);
            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return Clone(run);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                if (attempt == 4) throw new InvalidOperationException($"Execution run '{normalizedRunId}' changed repeatedly while resuming a durable wait.");
                continue;
            }

            throw new InvalidOperationException($"Could not resume a durable wait for execution run '{normalizedRunId}': {response.StatusCode}.");
        }

        throw new InvalidOperationException($"Could not resume a durable wait for execution run '{normalizedRunId}'.");
    }

    public async Task<ExecutionWaitResult?> TakeDurableWaitOutcomeAsync(string runId, string kind, string name, CancellationToken ct = default)
    {
        var partitionKey = RunPartition(RequireId(runId, "Run id"));
        var expectedKind = NormalizeWaitKind(kind);
        var expectedName = RequireId(name, "Wait name");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var document = await ReadDocumentAsync(partitionKey, WaitOutcomeDocumentId, ct);
            if (document is null) return null;

            var outcome = Deserialize<ExecutionWaitResult>(document.Document.Json);
            if (!string.Equals(outcome.Name, expectedName, StringComparison.Ordinal) ||
                !OutcomeMatchesWaitKind(outcome, expectedKind))
            {
                return null;
            }

            try
            {
                await _container.DeleteItemAsync<StoredDocument>(
                    WaitOutcomeDocumentId,
                    new PartitionKey(partitionKey),
                    new ItemRequestOptions { IfMatchEtag = document.ETag },
                    ct);
                return Clone(outcome);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 4)
            {
            }
        }

        throw new InvalidOperationException($"Durable wait outcome for run '{runId}' changed repeatedly while being consumed.");
    }

    private async Task<T> UpsertAndReturnAsync<T>(StoredDocument document, T value, CancellationToken ct)
    {
        await UpsertDocumentAsync(document, ct);
        return Clone(value);
    }

    private async Task UpsertDocumentAsync(StoredDocument document, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _container.UpsertItemAsync(document, new PartitionKey(document.PartitionKey), cancellationToken: ct);
    }

    private async Task<StoredDocumentWithEtag?> ReadDocumentAsync(string partitionKey, string id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            var response = await _container.ReadItemAsync<StoredDocument>(id, new PartitionKey(partitionKey), cancellationToken: ct);
            return new StoredDocumentWithEtag(response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<List<StoredDocument>> QueryPartitionKindAsync(string partitionKey, string kind, CancellationToken ct)
    {
        return await QueryDocumentsAsync(
            new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind").WithParameter("@kind", kind),
            ct,
            new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) });
    }

    private async Task<List<StoredDocument>> QueryDocumentsAsync(QueryDefinition query, CancellationToken ct, QueryRequestOptions? options = null)
    {
        await EnsureInitializedAsync(ct);
        var results = new List<StoredDocument>();
        using var iterator = _container.GetItemQueryIterator<StoredDocument>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        return results;
    }

    private async Task<T> QuerySingleValueAsync<T>(QueryDefinition query, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        using var iterator = _container.GetItemQueryIterator<T>(query);
        if (!iterator.HasMoreResults)
        {
            return default!;
        }

        var response = await iterator.ReadNextAsync(ct);
        return response.Resource.FirstOrDefault()!;
    }

    private static QueryDefinition BuildRunQuery(ExecutionRunQuery query)
    {
        var clauses = new List<string> { "c.kind = @kind" };
        var parameters = new Dictionary<string, object?> { ["@kind"] = RunKind };

        AddOptionalEquality("handlerId", query.HandlerId);
        AddOptionalEquality("pluginId", query.PluginId);
        AddOptionalEquality("status", query.Status);
        AddOptionalEquality("correlationId", query.CorrelationId);
        AddOptionalEquality("idempotencyKey", query.IdempotencyKey);
        AddOptionalDate("createdAtUtc", ">=", query.CreatedAfterUtc, "createdAfterUtc");
        AddOptionalDate("createdAtUtc", "<=", query.CreatedBeforeUtc, "createdBeforeUtc");
        AddOptionalDate("updatedAtUtc", ">=", query.UpdatedAfterUtc, "updatedAfterUtc");
        AddOptionalDate("updatedAtUtc", "<=", query.UpdatedBeforeUtc, "updatedBeforeUtc");

        // Execution runs created before tag projection was introduced keep their tags only in the
        // opaque contract payload. Include those documents and retain MatchesQuery below so a
        // rolling deployment preserves portable tag-query results while new runs use Cosmos's
        // indexed ARRAY_CONTAINS predicate.
        var tagIndex = 0;
        foreach (var tag in query.Tags.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var parameter = "@tag" + tagIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            clauses.Add($"(ARRAY_CONTAINS(c.tags, {parameter}, true) OR NOT IS_DEFINED(c.tags))");
            parameters[parameter] = new StoredTag { Key = tag.Key, Value = tag.Value };
            tagIndex++;
        }

        var definition = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", clauses));
        foreach (var (parameter, value) in parameters)
        {
            definition.WithParameter(parameter, value);
        }

        return definition;

        void AddOptionalEquality(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var parameter = "@" + field;
            clauses.Add($"c.{field} = {parameter}");
            parameters[parameter] = value.Trim();
        }

        void AddOptionalDate(string field, string operation, DateTime? value, string parameterName)
        {
            if (!value.HasValue) return;
            var parameter = "@" + parameterName;
            clauses.Add($"c.{field} {operation} {parameter}");
            parameters[parameter] = value.Value.ToUniversalTime();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        Task initialization;
        lock (_initializeSync)
        {
            _initialize ??= InitializeAsync();
            initialization = _initialize;
        }

        await initialization.WaitAsync(ct);
    }

    private async Task InitializeAsync()
    {
        await _database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(_container.Id, PartitionKeyPath),
            cancellationToken: CancellationToken.None);
    }

    private StoredDocument BuildRunDocument(ExecutionRun run)
    {
        return BuildDocument(
            "run",
            RunPartition(run.Id),
            RunKind,
            run,
            runId: run.Id,
            handlerId: run.HandlerId,
            pluginId: run.PluginId,
            status: run.Status,
            correlationId: run.CorrelationId,
            idempotencyKey: run.IdempotencyKey,
            createdAtUtc: run.CreatedAtUtc,
            updatedAtUtc: run.UpdatedAtUtc);
    }

    private static StoredDocument BuildDocument<T>(
        string id,
        string partitionKey,
        string kind,
        T value,
        string? runId = null,
        string? handlerId = null,
        string? pluginId = null,
        string? status = null,
        string? correlationId = null,
        string? idempotencyKey = null,
        string? name = null,
        DateTime? createdAtUtc = null,
        DateTime? updatedAtUtc = null)
    {
        return new StoredDocument
        {
            Id = id,
            PartitionKey = partitionKey,
            Kind = kind,
            Json = JsonSerializer.Serialize(value, ExecutionJson.Options),
            RunId = runId,
            HandlerId = handlerId,
            PluginId = pluginId,
            Status = status,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            Name = name,
            Tags = value is ExecutionRun run
                ? run.Tags
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new StoredTag { Key = item.Key, Value = item.Value })
                    .ToList()
                : null,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc
        };
    }

    private int ValidateLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0) throw new InvalidOperationException("Execution list limit must be greater than zero.");
        var effective = limit ?? _options.DefaultListLimit;
        if (effective > _options.MaxListLimit) throw new InvalidOperationException($"Execution list limit cannot exceed {_options.MaxListLimit}.");
        return effective;
    }

    private static bool MatchesQuery(ExecutionRun run, ExecutionRunQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.HandlerId) && !string.Equals(run.HandlerId, query.HandlerId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(query.PluginId) && !string.Equals(run.PluginId, query.PluginId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(query.Status) && !string.Equals(run.Status, query.Status, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(query.CorrelationId) && !string.Equals(run.CorrelationId, query.CorrelationId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(query.IdempotencyKey) && !string.Equals(run.IdempotencyKey, query.IdempotencyKey, StringComparison.Ordinal)) return false;
        if (query.CreatedAfterUtc.HasValue && run.CreatedAtUtc < query.CreatedAfterUtc.Value.ToUniversalTime()) return false;
        if (query.CreatedBeforeUtc.HasValue && run.CreatedAtUtc > query.CreatedBeforeUtc.Value.ToUniversalTime()) return false;
        if (query.UpdatedAfterUtc.HasValue && run.UpdatedAtUtc < query.UpdatedAfterUtc.Value.ToUniversalTime()) return false;
        if (query.UpdatedBeforeUtc.HasValue && run.UpdatedAtUtc > query.UpdatedBeforeUtc.Value.ToUniversalTime()) return false;

        return query.Tags.All(filter => run.Tags.TryGetValue(filter.Key, out var value) && string.Equals(value, filter.Value, StringComparison.Ordinal));
    }

    private static ExecutionRun WithoutResult(ExecutionRun run, bool includeResult)
    {
        var clone = Clone(run);
        if (!includeResult) clone.Result = null;
        return clone;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ExecutionJson.Options) ?? throw new InvalidOperationException($"Stored {typeof(T).Name} could not be deserialized.");

    private static T Clone<T>(T value) => Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options));

    private static string RunPartition(string runId) => "run:" + runId.Trim();

    private static string LeasePartition(string leaseKey) => "lease:" + HashId(leaseKey);

    private static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AzureDurableWait NormalizeWait(AzureDurableWait wait)
    {
        var normalized = Clone(wait);
        normalized.Kind = NormalizeWaitKind(normalized.Kind);
        normalized.Name = RequireId(normalized.Name, "Wait name");
        normalized.FireAtUtc = normalized.FireAtUtc?.ToUniversalTime();
        if (normalized.Kind == AzureDurableWaitKinds.Timer &&
            (normalized.Timer is null || !normalized.FireAtUtc.HasValue))
        {
            throw new InvalidOperationException("A durable timer wait requires a timer and fire time.");
        }

        return normalized;
    }

    private static ExecutionWaitResult NormalizeWaitOutcome(ExecutionWaitResult outcome, AzureDurableWait wait)
    {
        var normalized = Clone(outcome);
        if (!string.Equals(normalized.Name, wait.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Durable wait outcome '{normalized.Name}' does not match wait '{wait.Name}'.");
        }

        var expectedOutcome = wait.Kind == AzureDurableWaitKinds.Timer
            ? ExecutionWaitOutcomes.Timer
            : normalized.Outcome == ExecutionWaitOutcomes.TimedOut
                ? ExecutionWaitOutcomes.TimedOut
                : ExecutionWaitOutcomes.ExternalEvent;
        if (!string.Equals(normalized.Outcome, expectedOutcome, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Durable wait outcome '{normalized.Outcome}' is not valid for '{wait.Kind}'.");
        }

        if (expectedOutcome == ExecutionWaitOutcomes.Timer)
        {
            normalized.Timer = Clone(wait.Timer!);
            normalized.Event = null;
        }
        else if (expectedOutcome == ExecutionWaitOutcomes.TimedOut)
        {
            normalized.Event = null;
            normalized.Timer = null;
        }
        else if (normalized.Event is null || !string.Equals(normalized.Event.Name, wait.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external-event durable wait outcome requires the matching event.");
        }

        return normalized;
    }

    private static bool WaitsMatch(AzureDurableWait first, AzureDurableWait second) =>
        string.Equals(first.Kind, second.Kind, StringComparison.Ordinal) &&
        string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
        first.FireAtUtc == second.FireAtUtc;

    private static bool OutcomeMatchesWaitKind(ExecutionWaitResult outcome, string kind) => kind switch
    {
        AzureDurableWaitKinds.Timer => string.Equals(outcome.Outcome, ExecutionWaitOutcomes.Timer, StringComparison.Ordinal),
        AzureDurableWaitKinds.ExternalEvent =>
            string.Equals(outcome.Outcome, ExecutionWaitOutcomes.ExternalEvent, StringComparison.Ordinal) ||
            string.Equals(outcome.Outcome, ExecutionWaitOutcomes.TimedOut, StringComparison.Ordinal),
        _ => false
    };

    private static string NormalizeWaitKind(string? kind) => kind switch
    {
        AzureDurableWaitKinds.ExternalEvent => AzureDurableWaitKinds.ExternalEvent,
        AzureDurableWaitKinds.Timer => AzureDurableWaitKinds.Timer,
        _ => throw new InvalidOperationException($"Durable wait kind '{kind ?? "(null)"}' is not supported.")
    };

    private static string RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
        return value.Trim();
    }

    private sealed class StoredDocument
    {
        [NewtonsoftJsonProperty("id")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [NewtonsoftJsonProperty("partitionKey")]
        [JsonPropertyName("partitionKey")]
        public string PartitionKey { get; set; } = string.Empty;

        [NewtonsoftJsonProperty("kind")]
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [NewtonsoftJsonProperty("json")]
        [JsonPropertyName("json")]
        public string Json { get; set; } = string.Empty;

        [NewtonsoftJsonProperty("runId")]
        [JsonPropertyName("runId")]
        public string? RunId { get; set; }

        [NewtonsoftJsonProperty("handlerId")]
        [JsonPropertyName("handlerId")]
        public string? HandlerId { get; set; }

        [NewtonsoftJsonProperty("pluginId")]
        [JsonPropertyName("pluginId")]
        public string? PluginId { get; set; }

        [NewtonsoftJsonProperty("status")]
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [NewtonsoftJsonProperty("correlationId")]
        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; set; }

        [NewtonsoftJsonProperty("idempotencyKey")]
        [JsonPropertyName("idempotencyKey")]
        public string? IdempotencyKey { get; set; }

        [NewtonsoftJsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [NewtonsoftJsonProperty("tags")]
        [JsonPropertyName("tags")]
        public List<StoredTag>? Tags { get; set; }

        [NewtonsoftJsonProperty("createdAtUtc")]
        [JsonPropertyName("createdAtUtc")]
        public DateTime? CreatedAtUtc { get; set; }

        [NewtonsoftJsonProperty("updatedAtUtc")]
        [JsonPropertyName("updatedAtUtc")]
        public DateTime? UpdatedAtUtc { get; set; }
    }

    private sealed class StoredTag
    {
        [NewtonsoftJsonProperty("key")]
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [NewtonsoftJsonProperty("value")]
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    private sealed record StoredDocumentWithEtag(StoredDocument Document, string ETag);
}
