using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.Local;

public sealed class LocalExecutionRuntime : IExecutionRuntimeAdapter, IExecutionRuntimeMaintenance, IExternalExecutionWorkerRuntime
{
    private const int CurrentSchemaVersion = 2;
    private const string LocalArtifactStorage = "local-file";
    private const string ExternalWorkerLeasePrefix = "external-worker-run-";
    private const string ExternalWorkerLeaseProtocol = "external_worker";

    private readonly ConcurrentDictionary<string, IExecutionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionHandlerDescriptor> _externalHandlers = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, ExecutionProductPolicy> _productPolicies;
    private readonly ConcurrentDictionary<string, ExecutionPluginDescriptor> _plugins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlightRuns = new(StringComparer.Ordinal);
    private readonly string _connectionString;
    private readonly string _artifactDirectory;

    public LocalExecutionRuntime(LocalExecutionRuntimeOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _productPolicies = BuildProductPolicies(options.ProductPolicies);
        EnsureDirectoryForFile(options.DatabasePath);
        _artifactDirectory = ResolveArtifactDirectory(options);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
        Adapter = new ExecutionRuntimeAdapterDescriptor
        {
            AdapterId = string.IsNullOrWhiteSpace(options.AdapterId) ? "local-sqlite" : options.AdapterId,
            RuntimeKind = LocalExecutionRuntimeKindIds.Sqlite,
            DisplayName = "Local SQLite execution runtime",
            Version = "0.2.0",
            Capabilities =
            {
                ExecutionCapabilityIds.LocalDispatch,
                ExecutionCapabilityIds.InProcessHandlers,
                ExecutionCapabilityIds.DurableRuns,
                ExecutionCapabilityIds.DurableTimers,
                ExecutionCapabilityIds.ExternalEvents,
                ExecutionCapabilityIds.DurableWaits,
                ExecutionCapabilityIds.Cancellation,
                ExecutionCapabilityIds.Retries,
                ExecutionCapabilityIds.RestartResume,
                ExecutionCapabilityIds.Leases,
                ExecutionCapabilityIds.Artifacts,
                ExecutionCapabilityIds.TraceHistory,
                ExecutionCapabilityIds.Idempotency,
                ExecutionCapabilityIds.ExternalWorkers
            },
            Metadata =
            {
                ["databasePath"] = options.DatabasePath,
                ["schemaVersion"] = CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
                ["journalMode"] = "wal",
                ["synchronous"] = "normal",
                ["busyTimeoutMs"] = Math.Max(0, options.BusyTimeoutMs).ToString(CultureInfo.InvariantCulture),
                ["artifactDirectory"] = _artifactDirectory,
                ["maxPayloadBytes"] = Limits.MaxPayloadBytes.ToString(CultureInfo.InvariantCulture),
                ["maxArtifactBytes"] = Limits.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture),
                ["maxArtifactInlineBytes"] = Limits.MaxArtifactInlineBytes.ToString(CultureInfo.InvariantCulture),
                ["maxTraceMessageChars"] = Limits.MaxTraceMessageChars.ToString(CultureInfo.InvariantCulture),
                ["maxActiveRuns"] = Options.MaxActiveRuns.ToString(CultureInfo.InvariantCulture),
                ["maxRetainedTerminalRuns"] = Options.MaxRetainedTerminalRuns.ToString(CultureInfo.InvariantCulture),
                ["defaultListLimit"] = Options.DefaultListLimit.ToString(CultureInfo.InvariantCulture),
                ["maxListLimit"] = Options.MaxListLimit.ToString(CultureInfo.InvariantCulture),
                ["concurrencyKeyPolicy"] = "serialize_running_runs"
            }
        };
        ExecutionContractValidator.ValidateAdapterDescriptor(Adapter, Limits);
    }

    public LocalExecutionRuntimeOptions Options { get; }
    public ExecutionRuntimeAdapterDescriptor Adapter { get; }
    private ExecutionRuntimeLimits Limits => Options.Limits ?? ExecutionRuntimeLimits.Default;

    private ExecutionOperationalPolicy BuildOperationalPolicy()
    {
        return new ExecutionOperationalPolicy
        {
            MaxActiveRuns = Options.MaxActiveRuns,
            MaxRetainedTerminalRuns = Options.MaxRetainedTerminalRuns,
            DefaultListLimit = Options.DefaultListLimit,
            MaxListLimit = Options.MaxListLimit,
            DefaultHistoryLimit = Options.DefaultListLimit,
            MaxHistoryLimit = Options.MaxListLimit,
            MaxPayloadBytes = Limits.MaxPayloadBytes,
            MaxResultBytes = Limits.MaxResultBytes,
            MaxStatusDetailsBytes = Limits.MaxStatusDetailsBytes,
            MaxArtifactBytes = Limits.MaxArtifactBytes,
            MaxArtifactInlineBytes = Limits.MaxArtifactInlineBytes,
            MaxTraceMessageChars = Limits.MaxTraceMessageChars,
            MaxTraceDetailsBytes = Limits.MaxTraceDetailsBytes,
            MaxRetryAttempts = Limits.MaxRetryAttempts,
            MaxRetryDelaySeconds = Limits.MaxRetryDelaySeconds,
            MaxLeaseTtlSeconds = Limits.MaxLeaseTtlSeconds,
            ConcurrencyKeyPolicy = "serialize_running_runs",
            ConcurrencyRetryDelayMs = Math.Max(10, Options.ConcurrencyRetryDelayMs),
            DefaultTraceSeverity = "info",
            RetentionScope = "run_owned"
        };
    }

    public void RegisterHandler(IExecutionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler.Descriptor, Limits);
        var handlerId = NormalizeRequired(handler.Descriptor.HandlerId, "Handler id");
        if (_externalHandlers.ContainsKey(handlerId))
        {
            throw new InvalidOperationException($"Execution handler '{handlerId}' is already registered for external workers.");
        }

        _handlers[handlerId] = handler;
        _ = DispatchReadyRunsAsync();
    }

    public void RegisterPlugin(IExecutionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ExecutionContractValidator.ValidatePluginDescriptor(plugin.Descriptor, Limits);
        foreach (var handler in plugin.Handlers)
        {
            RegisterHandler(handler);
        }

        _plugins[NormalizeRequired(plugin.Descriptor.PluginId, "Plugin id")] = Clone(plugin.Descriptor);
    }

    public void RegisterExternalHandler(ExecutionHandlerDescriptor handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExecutionContractValidator.ValidateHandlerDescriptor(handler, Limits);
        var handlerId = NormalizeRequired(handler.HandlerId, "Handler id");
        if (_handlers.ContainsKey(handlerId))
        {
            throw new InvalidOperationException($"Execution handler '{handlerId}' is already registered in process.");
        }

        _externalHandlers[handlerId] = Clone(handler);
    }

    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins()
    {
        return _plugins.Values
            .Select(Clone)
            .OrderBy(descriptor => descriptor.PluginId, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers()
    {
        return _handlers.Values.Select(handler => handler.Descriptor)
            .Concat(_externalHandlers.Values)
            .Select(Clone)
            .OrderBy(descriptor => descriptor.HandlerId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default)
    {
        return new ExecutionRuntimeAdapterStatus
        {
            Adapter = Clone(Adapter),
            Available = true,
            Status = "ok",
            CheckedAtUtc = DateTime.UtcNow,
            ActiveRuns = await CountActiveRunsAsync(ct),
            OperationalPolicy = BuildOperationalPolicy(),
            ResumePolicy = BuildResumePolicy(),
            Details = new JsonObject
            {
                ["registeredHandlers"] = _handlers.Count,
                ["databasePath"] = Options.DatabasePath,
                ["schemaVersion"] = CurrentSchemaVersion,
                ["artifactDirectory"] = _artifactDirectory,
                ["journalMode"] = "wal",
                ["busyTimeoutMs"] = Math.Max(0, Options.BusyTimeoutMs)
            }
        };
    }

    public async Task<ExecutionMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken ct = default)
    {
        var artifactMetrics = MeasureArtifactDirectory();
        return new ExecutionMaintenanceStatus
        {
            AdapterId = Adapter.AdapterId,
            RuntimeKind = Adapter.RuntimeKind,
            CheckedAtUtc = DateTime.UtcNow,
            RetentionScope = "run_owned",
            MaxRetainedTerminalRuns = Options.MaxRetainedTerminalRuns,
            RunCounts = await CountRunsByStatusAsync(ct),
            RowCounts = await CountMaintenanceRowsAsync(ct),
            ArtifactDirectory = _artifactDirectory,
            ArtifactDirectoryCount = artifactMetrics.DirectoryCount,
            ArtifactFileCount = artifactMetrics.FileCount,
            ArtifactBytes = artifactMetrics.Bytes
        };
    }

    public Task<ExecutionMaintenancePruneResult> PruneAsync(ExecutionMaintenancePruneRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RetainTerminalRuns.HasValue && request.RetainTerminalRuns.Value < 0)
        {
            throw new InvalidOperationException("Execution maintenance retainTerminalRuns must be non-negative.");
        }

        return PruneTerminalRunsAsync(request.RetainTerminalRuns ?? Options.MaxRetainedTerminalRuns, request.DryRun, ct);
    }

    public Task<ExecutionMaintenanceDispatchReconcileResult> ReconcileDispatchAsync(ExecutionMaintenanceDispatchReconcileRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = request.Limit ?? Options.MaxListLimit;
        if (limit <= 0 || limit > Options.MaxListLimit) throw new InvalidOperationException($"Execution maintenance reconcile limit must be between 1 and {Options.MaxListLimit}.");
        // SQLite dispatch is owned by this process and restart recovery schedules persisted work
        // directly. There is no separate queue/outbox boundary to redrive.
        return Task.FromResult(new ExecutionMaintenanceDispatchReconcileResult
        {
            DryRun = request.DryRun,
            Limit = limit,
            ReconciledAtUtc = DateTime.UtcNow
        });
    }

    private static ExecutionResumePolicy BuildResumePolicy()
    {
        return new ExecutionResumePolicy
        {
            Mode = ExecutionResumePolicyModes.RestartRecovery,
            InterruptedRunningBehavior = ExecutionResumePolicyBehaviors.MayReexecuteHandler,
            ScheduledWaitingBehavior = ExecutionResumePolicyBehaviors.DispatchWhenDue,
            TerminalBehavior = ExecutionResumePolicyBehaviors.NeverResume,
            PluginCheckpointBehavior = ExecutionResumePolicyBehaviors.PluginOwned,
            IdempotencyScope = "handler_plugin_payload",
            CreatesLinkedFollowUpRuns = false
        };
    }

    public async Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateRunRequest(request, Limits);
        var retryPolicy = ExecutionContractValidator.NormalizeRetryPolicy(request.RetryPolicy, Limits);
        var handlerId = NormalizeRequired(request.HandlerId, "Handler id");
        EnsureRunBoundary(request, handlerId);
        var handlerRegistered = _handlers.TryGetValue(handlerId, out var registeredHandler);
        var externalHandlerRegistered = _externalHandlers.TryGetValue(handlerId, out var registeredExternalHandler);
        var handlerDescriptor = registeredHandler?.Descriptor ?? registeredExternalHandler;
        var requestedPluginId = NormalizeOptional(request.PluginId);
        var handlerPluginId = NormalizeOptional(handlerDescriptor?.PluginId);
        var pluginMismatch = requestedPluginId is not null &&
            handlerPluginId is not null &&
            !string.Equals(requestedPluginId, handlerPluginId, StringComparison.Ordinal);
        var effectivePluginId = requestedPluginId ?? handlerPluginId;
        var payloadHash = Sha256(SerializeNode(request.Payload));
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await FindRunByIdempotencyKeyAsync(request.IdempotencyKey!, ct);
            if (existing is not null)
            {
                EnsureIdempotentReplay(existing, handlerId, effectivePluginId, payloadHash, request.IdempotencyKey!);
                existing.AdmissionReplayed = true;
                return existing;
            }
        }

        var now = DateTime.UtcNow;
        var run = new ExecutionRun
        {
            Id = OrderedId.CreateString(),
            HandlerId = handlerId,
            PluginId = effectivePluginId,
            Status = ExecutionRunStatuses.Queued,
            Attempt = 0,
            MaxAttempts = retryPolicy.MaxAttempts,
            RetryPolicy = Clone(retryPolicy),
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey,
            CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? string.Empty : request.CorrelationId,
            Scope = request.Scope is null ? null : Clone(request.Scope),
            Payload = CloneNode(request.Payload),
            PayloadHash = payloadHash,
            CreatedAtUtc = now,
            ScheduledAtUtc = request.ScheduledAtUtc,
            UpdatedAtUtc = now,
            Tags = new Dictionary<string, string>(request.Tags, StringComparer.Ordinal)
        };
        run.CorrelationId = string.IsNullOrWhiteSpace(run.CorrelationId) ? run.Id : run.CorrelationId;

        if (pluginMismatch)
        {
            ExecutionRunLifecycle.EnsureCreationStatus(ExecutionRunStatuses.Rejected);
            run.Status = ExecutionRunStatuses.Rejected;
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
            run.FailureClass = ExecutionFailureClasses.PluginMismatch;
            run.Error = $"Execution handler '{handlerId}' belongs to plugin '{handlerPluginId}', not '{requestedPluginId}'.";
            await UpsertRunAsync(run, ct);
            await AppendEventAsync(run.Id, ExecutionEventTypes.RunRejected, run.Attempt, run.Status, run.Error, "warning", null, ct);
            return Clone(run);
        }

        if (!handlerRegistered && !externalHandlerRegistered)
        {
            ExecutionRunLifecycle.EnsureCreationStatus(ExecutionRunStatuses.Rejected);
            run.Status = ExecutionRunStatuses.Rejected;
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
            run.FailureClass = ExecutionFailureClasses.HandlerMissing;
            run.Error = $"Execution handler '{handlerId}' is not registered.";
            await UpsertRunAsync(run, ct);
            await AppendEventAsync(run.Id, ExecutionEventTypes.RunRejected, run.Attempt, run.Status, run.Error, "warning", null, ct);
            return Clone(run);
        }

        if (await CountActiveRunsAsync(ct) >= Options.MaxActiveRuns)
        {
            ExecutionRunLifecycle.EnsureCreationStatus(ExecutionRunStatuses.Rejected);
            run.Status = ExecutionRunStatuses.Rejected;
            run.CompletedAtUtc = now;
            run.DurationMs = 0;
            run.FailureClass = ExecutionFailureClasses.QueueFull;
            run.Error = $"Execution run queue is full. Max active runs: {Options.MaxActiveRuns}.";
            await UpsertRunAsync(run, ct);
            await AppendEventAsync(run.Id, ExecutionEventTypes.RunRejected, run.Attempt, run.Status, run.Error, "warning", null, ct);
            await PruneTerminalRunsAsync(ct);
            return Clone(run);
        }

        if (request.ScheduledAtUtc.HasValue && request.ScheduledAtUtc.Value > now)
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting);
            run.Status = ExecutionRunStatuses.Waiting;
        }

        ExecutionRunLifecycle.EnsureCreationStatus(run.Status);
        await UpsertRunAsync(run, ct);
        await AppendEventAsync(run.Id, ExecutionEventTypes.RunCreated, run.Attempt, run.Status, "Execution run created.", "info", null, ct);

        if (handlerRegistered && run.Status == ExecutionRunStatuses.Queued)
        {
            StartWorker(run.Id);
        }
        else if (handlerRegistered && run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue)
        {
            StartWorker(run.Id, run.ScheduledAtUtc.Value - DateTime.UtcNow);
        }

        return Clone(run);
    }

    public async Task<int> DispatchReadyRunsAsync(bool recoverInterruptedRuns = false, CancellationToken ct = default)
    {
        var runs = await LoadActiveRunsAsync(ct);
        var dispatched = 0;
        var now = DateTime.UtcNow;
        foreach (var run in runs)
        {
            if (!_handlers.ContainsKey(run.HandlerId))
            {
                continue;
            }

            if (run.Status == ExecutionRunStatuses.Running && recoverInterruptedRuns)
            {
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery);
                run.Status = ExecutionRunStatuses.Queued;
                run.CurrentStep = null;
                run.UpdatedAtUtc = now;
                await UpsertRunAsync(run, ct);
                await AppendEventAsync(run.Id, ExecutionEventTypes.RunStatus, run.Attempt, run.Status, "Interrupted execution run requeued.", "warning", null, ct);
            }

            if (run.Status == ExecutionRunStatuses.Queued)
            {
                StartWorker(run.Id);
                dispatched++;
                continue;
            }

            if (run.Status == ExecutionRunStatuses.Waiting)
            {
                if (await HasDurableWaitAsync(run.Id, ct))
                {
                    if (await ResumeDueWaitAsync(run.Id, ct))
                    {
                        StartLocalHandlerIfRegistered(run.Id);
                    }

                    continue;
                }

                var dueAt = run.ScheduledAtUtc;
                var delay = dueAt.HasValue ? dueAt.Value - now : TimeSpan.Zero;
                StartWorker(run.Id, delay);
                dispatched++;
            }
        }

        return dispatched;
    }

    public async Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default)
    {
        var run = await LoadRunAsync(runId, ct);
        if (run is null)
        {
            return null;
        }

        // Local execution has no separate scheduler process. A status read is therefore also a
        // safe recovery opportunity for a persisted due timer/timeout whose in-process delayed
        // dispatch was interrupted or starved. The durable wait transaction remains the authority
        // and only one worker can claim the resulting queued run.
        if (run.Status == ExecutionRunStatuses.Waiting &&
            run.ScheduledAtUtc.HasValue &&
            run.ScheduledAtUtc.Value <= DateTime.UtcNow &&
            _handlers.ContainsKey(run.HandlerId))
        {
            if (await ResumeDueWaitAsync(run.Id, ct))
            {
                run = await LoadRunAsync(run.Id, ct) ?? run;
            }

            if (run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting)
            {
                StartWorker(run.Id);
            }
        }

        if (!includeResult)
        {
            run.Result = null;
        }

        return run;
    }

    public async Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionRunQuery();
        var limit = ValidateLimit(query.Limit);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.HandlerId))
        {
            filters.Add("handler_id = $handler_id");
            command.Parameters.AddWithValue("$handler_id", query.HandlerId);
        }

        if (!string.IsNullOrWhiteSpace(query.PluginId))
        {
            filters.Add("plugin_id = $plugin_id");
            command.Parameters.AddWithValue("$plugin_id", query.PluginId);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filters.Add("status = $status");
            command.Parameters.AddWithValue("$status", query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            filters.Add("correlation_id = $correlation_id");
            command.Parameters.AddWithValue("$correlation_id", query.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(query.IdempotencyKey))
        {
            filters.Add("idempotency_key = $idempotency_key");
            command.Parameters.AddWithValue("$idempotency_key", query.IdempotencyKey);
        }

        if (query.CreatedAfterUtc.HasValue)
        {
            filters.Add("created_at_utc >= $created_after_utc");
            command.Parameters.AddWithValue("$created_after_utc", query.CreatedAfterUtc.Value.ToUniversalTime().ToString("O"));
        }

        if (query.CreatedBeforeUtc.HasValue)
        {
            filters.Add("created_at_utc <= $created_before_utc");
            command.Parameters.AddWithValue("$created_before_utc", query.CreatedBeforeUtc.Value.ToUniversalTime().ToString("O"));
        }

        if (query.UpdatedAfterUtc.HasValue)
        {
            filters.Add("updated_at_utc >= $updated_after_utc");
            command.Parameters.AddWithValue("$updated_after_utc", query.UpdatedAfterUtc.Value.ToUniversalTime().ToString("O"));
        }

        if (query.UpdatedBeforeUtc.HasValue)
        {
            filters.Add("updated_at_utc <= $updated_before_utc");
            command.Parameters.AddWithValue("$updated_before_utc", query.UpdatedBeforeUtc.Value.ToUniversalTime().ToString("O"));
        }

        var hasTagFilters = query.Tags.Count > 0;
        command.CommandText = $@"
            SELECT run_json
            FROM vyral_execution_runs
            {(filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters))}
            ORDER BY created_at_utc DESC, id ASC
            {(hasTagFilters ? "" : "LIMIT $limit")};";
        if (!hasTagFilters)
        {
            command.Parameters.AddWithValue("$limit", limit);
        }

        var runs = new List<ExecutionRun>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct) && runs.Count < limit)
        {
            var run = Deserialize<ExecutionRun>(reader.GetString(0));
            if (run is null)
            {
                continue;
            }

            if (!MatchesTagFilters(run, query.Tags))
            {
                continue;
            }

            if (!query.IncludeResult)
            {
                run.Result = null;
            }

            runs.Add(run);
        }

        return runs;
    }

    private static bool MatchesTagFilters(ExecutionRun run, IReadOnlyDictionary<string, string> filters)
    {
        foreach (var (key, value) in filters)
        {
            if (!run.Tags.TryGetValue(key, out var actual) ||
                !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        ExecutionRun run;
        var cancelledBeforeClaim = false;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var loaded = await LoadRunAsync(connection, tx, runId, ct);
            if (loaded is null)
            {
                tx.Commit();
                return null;
            }

            run = loaded;

            if (ExecutionRunStatuses.IsTerminal(run.Status))
            {
                tx.Commit();
                return Clone(run);
            }

            run.CancellationRequested = true;
            run.UpdatedAtUtc = DateTime.UtcNow;
            if (run.Status is ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting)
            {
                // Claiming also changes a queued/waiting run in SQLite. Do the
                // pending cancellation in the same immediate transaction so a
                // queued run cannot be claimed after cancellation is accepted.
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Cancelled);
                run.Status = ExecutionRunStatuses.Cancelled;
                run.FailureClass = ExecutionFailureClasses.Cancelled;
                run.Error = "Execution run was cancelled before it started.";
                CompleteTiming(run);
                await ClearDurableWaitStateAsync(connection, tx, run.Id, ct);
                cancelledBeforeClaim = true;
            }

            await UpsertRunAsync(connection, tx, run, ct);
            tx.Commit();
        }

        await AppendEventAsync(run.Id, ExecutionEventTypes.RunCancellationRequested, run.Attempt, run.Status, "Execution cancellation requested.", "info", null, ct);

        if (cancelledBeforeClaim)
        {
            await AppendEventAsync(run.Id, ExecutionEventTypes.RunCompleted, run.Attempt, run.Status, run.Error, "warning", null, ct);
            await PruneTerminalRunsAsync(ct);
            return Clone(run);
        }

        if (_cancellations.TryGetValue(run.Id, out var cancellation))
        {
            await cancellation.CancelAsync();
        }

        return Clone(run);
    }

    public async Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default)
    {
        query ??= new ExecutionHistoryQuery();
        var limit = ValidateLimit(query.Limit);
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT event_json
            FROM vyral_execution_events
            WHERE run_id = $run_id
            ORDER BY timestamp_utc ASC, id ASC
            LIMIT $limit;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<ExecutionTraceEvent>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = Deserialize<ExecutionTraceEvent>(reader.GetString(0));
            if (item is not null)
            {
                events.Add(item);
            }
        }

        return events;
    }

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT artifact_json
            FROM vyral_execution_artifacts
            WHERE run_id = $run_id
            ORDER BY created_at_utc ASC, id ASC;";
        command.Parameters.AddWithValue("$run_id", runId);

        var artifacts = new List<ExecutionArtifact>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = Deserialize<ExecutionArtifact>(reader.GetString(0));
            if (item is not null)
            {
                artifacts.Add(item);
            }
        }

        return artifacts;
    }

    public async Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default)
    {
        var normalizedRunId = NormalizeRequired(runId, "Run id");
        var normalizedArtifactRef = NormalizeRequired(artifactRef, "Artifact reference");
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT artifact_json
            FROM vyral_execution_artifacts
            WHERE run_id = $run_id
              AND (id = $artifact_ref OR name = $artifact_ref)
            ORDER BY created_at_utc DESC, id DESC
            LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$artifact_ref", normalizedArtifactRef);

        var json = await command.ExecuteScalarAsync(ct) as string;
        if (json is null)
        {
            return null;
        }

        var artifact = Deserialize<ExecutionArtifact>(json);
        return artifact is null ? null : await RehydrateArtifactAsync(artifact, ct);
    }

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default)
    {
        var normalizedRunId = NormalizeRequired(runId, "Run id");
        var normalizedKey = NormalizeRequired(key, "Checkpoint key");
        using var connection = await OpenAsync(ct);
        return await LoadCheckpointAsync(connection, null, normalizedRunId, normalizedKey, ct);
    }

    private static async Task<ExecutionCheckpoint?> LoadCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string runId,
        string key,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            SELECT checkpoint_json
            FROM vyral_execution_checkpoints
            WHERE run_id = $run_id AND key = $key
            LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$key", key);

        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : Deserialize<ExecutionCheckpoint>(json);
    }

    public async Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateLeaseRequest(request, Limits);
        var now = DateTime.UtcNow;
        var lease = new ExecutionLease
        {
            LeaseKey = NormalizeRequired(request.LeaseKey, "Lease key"),
            OwnerId = NormalizeRequired(request.OwnerId, "Lease owner id"),
            RunId = request.RunId,
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(Math.Max(1, request.TtlSeconds)),
            Metadata = CloneObject(request.Metadata)
        };

        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction(deferred: false);
        var existing = await LoadLeaseAsync(connection, tx, lease.LeaseKey, ct);
        if (existing is not null && existing.ExpiresAtUtc > now && !string.Equals(existing.OwnerId, lease.OwnerId, StringComparison.Ordinal))
        {
            tx.Rollback();
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_leases (lease_key, owner_id, run_id, expires_at_utc, lease_json)
            VALUES ($lease_key, $owner_id, $run_id, $expires_at_utc, $lease_json)
            ON CONFLICT(lease_key) DO UPDATE SET
                owner_id = excluded.owner_id,
                run_id = excluded.run_id,
                expires_at_utc = excluded.expires_at_utc,
                lease_json = excluded.lease_json;";
        command.Parameters.AddWithValue("$lease_key", lease.LeaseKey);
        command.Parameters.AddWithValue("$owner_id", lease.OwnerId);
        command.Parameters.AddWithValue("$run_id", lease.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$expires_at_utc", lease.ExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$lease_json", Serialize(lease));
        await command.ExecuteNonQueryAsync(ct);
        tx.Commit();

        if (!string.IsNullOrWhiteSpace(lease.RunId))
        {
            await AppendEventAsync(lease.RunId!, ExecutionEventTypes.LeaseAcquired, 0, null, $"Lease '{lease.LeaseKey}' acquired.", "info", null, ct);
        }

        return Clone(lease);
    }

    public async Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default)
    {
        leaseKey = NormalizeRequired(leaseKey, "Lease key");
        ownerId = NormalizeRequired(ownerId, "Lease owner id");
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        var lease = await LoadLeaseAsync(connection, tx, leaseKey, ct);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM vyral_execution_leases WHERE lease_key = $lease_key AND owner_id = $owner_id;";
        command.Parameters.AddWithValue("$lease_key", leaseKey);
        command.Parameters.AddWithValue("$owner_id", ownerId);
        var released = await command.ExecuteNonQueryAsync(ct) > 0;
        tx.Commit();

        if (released && !string.IsNullOrWhiteSpace(lease?.RunId))
        {
            await AppendEventAsync(lease!.RunId!, ExecutionEventTypes.LeaseReleased, 0, null, $"Lease '{lease.LeaseKey}' released.", "info", null, ct);
        }

        return released;
    }

    public async Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateTimerRequest(request, Limits);
        var timer = new ExecutionTimer
        {
            Id = OrderedId.CreateString(),
            Name = NormalizeRequired(request.Name, "Timer name"),
            RunId = request.RunId,
            FireAtUtc = request.FireAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            Payload = CloneNode(request.Payload)
        };

        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_execution_timers (id, name, run_id, fire_at_utc, timer_json)
            VALUES ($id, $name, $run_id, $fire_at_utc, $timer_json);";
        command.Parameters.AddWithValue("$id", timer.Id);
        command.Parameters.AddWithValue("$name", timer.Name);
        command.Parameters.AddWithValue("$run_id", timer.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fire_at_utc", timer.FireAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$timer_json", Serialize(timer));
        await command.ExecuteNonQueryAsync(ct);

        if (!string.IsNullOrWhiteSpace(timer.RunId))
        {
            await AppendEventAsync(timer.RunId!, ExecutionEventTypes.TimerScheduled, 0, null, $"Timer '{timer.Name}' scheduled.", "info", null, ct);
        }

        return Clone(timer);
    }

    public async Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalEventRequest(request, Limits);
        var externalEvent = new ExecutionExternalEvent
        {
            Id = OrderedId.CreateString(),
            Name = NormalizeRequired(request.Name, "External event name"),
            RunId = request.RunId,
            RaisedAtUtc = DateTime.UtcNow,
            Payload = CloneNode(request.Payload)
        };

        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_execution_external_events (id, name, run_id, raised_at_utc, event_json)
            VALUES ($id, $name, $run_id, $raised_at_utc, $event_json);";
        command.Parameters.AddWithValue("$id", externalEvent.Id);
        command.Parameters.AddWithValue("$name", externalEvent.Name);
        command.Parameters.AddWithValue("$run_id", externalEvent.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$raised_at_utc", externalEvent.RaisedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$event_json", Serialize(externalEvent));
        await command.ExecuteNonQueryAsync(ct);

        if (!string.IsNullOrWhiteSpace(externalEvent.RunId))
        {
            await AppendEventAsync(externalEvent.RunId!, ExecutionEventTypes.ExternalEventRaised, 0, null, $"External event '{externalEvent.Name}' raised.", "info", null, ct);
            await ResumeExternalEventWaitAsync(externalEvent, ct);
        }

        return Clone(externalEvent);
    }

    public async Task<ExecutionExternalWorkerLease?> LeaseNextRunAsync(
        ExecutionExternalWorkerLeaseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerLeaseRequest(request, Limits);
        var workerId = NormalizeRequired(request.WorkerId, "External worker id");
        var handlerIds = new HashSet<string>(request.HandlerIds.Select(id => NormalizeRequired(id, "External worker handler id")), StringComparer.Ordinal);

        IReadOnlyList<ExecutionRun> candidates;
        if (!string.IsNullOrWhiteSpace(request.RunId))
        {
            var run = await LoadRunAsync(NormalizeRequired(request.RunId, "Execution run id"), ct);
            candidates = run is null ? Array.Empty<ExecutionRun>() : [run];
        }
        else
        {
            candidates = await LoadActiveRunsAsync(ct);
        }

        foreach (var candidate in candidates)
        {
            var current = candidate;
            if (current.Status == ExecutionRunStatuses.Waiting && await ResumeDueWaitAsync(current.Id, ct))
            {
                current = await LoadRunAsync(current.Id, ct) ?? current;
            }

            if (!handlerIds.Contains(current.HandlerId) || !_externalHandlers.ContainsKey(current.HandlerId))
            {
                continue;
            }

            var lease = await TryAcquireExternalWorkerLeaseAsync(current.Id, workerId, request.TtlSeconds, ct);
            if (lease is not null)
            {
                return lease;
            }
        }

        return null;
    }

    public async Task<ExecutionExternalWorkerLease> HeartbeatExternalLeaseAsync(
        ExecutionExternalWorkerHeartbeatRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerHeartbeatRequest(request, Limits);
        var now = DateTime.UtcNow;
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction(deferred: false);
        var (lease, run) = await RequireActiveExternalWorkerLeaseAsync(connection, tx, request.LeaseKey, request.LeaseToken, request.WorkerId, now, ct);
        lease.ExpiresAtUtc = now.AddSeconds(request.TtlSeconds);
        await UpsertLeaseAsync(connection, tx, lease, ct);
        tx.Commit();

        return new ExecutionExternalWorkerLease
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = request.LeaseToken,
            WorkerId = lease.OwnerId,
            Run = Clone(run),
            AcquiredAtUtc = lease.AcquiredAtUtc,
            ExpiresAtUtc = lease.ExpiresAtUtc
        };
    }

    public async Task<ExecutionRun> ReportExternalLeaseAsync(
        ExecutionExternalWorkerReportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerReportRequest(request, Limits);
        ExecutionRun updated;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            ApplyUpdate(run, request.Update);
            run.UpdatedAtUtc = DateTime.UtcNow;
            await UpsertRunAsync(connection, tx, run, ct);
            tx.Commit();
            updated = Clone(run);
        }

        await AppendEventAsync(updated.Id, ExecutionEventTypes.RunStatus, updated.Attempt, updated.Status, updated.CurrentStep, "info", request.Update.StatusDetails, ct);
        return updated;
    }

    public async Task RecordExternalLeaseEventAsync(
        ExecutionExternalWorkerEventRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerEventRequest(request, Limits);
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            var item = CreateTraceEvent(
                run,
                run.Id,
                request.Type,
                run.Attempt,
                run.Status,
                request.Message,
                request.Severity,
                request.Details);
            await InsertEventAsync(connection, tx, item, ct);
            tx.Commit();
        }
    }

    public async Task<ExecutionArtifact> PutExternalLeaseArtifactAsync(
        ExecutionExternalWorkerArtifactRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerArtifactRequest(request, Limits);
        ExecutionArtifact artifact;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            artifact = await CreateArtifactAsync(run, request.Artifact, ct);
            await InsertArtifactAsync(connection, tx, artifact, ct);
            tx.Commit();
        }

        await AppendEventAsync(artifact.RunId, ExecutionEventTypes.ArtifactWritten, 0, null, $"Artifact '{artifact.Name}' written.", "info", null, ct);
        return Clone(artifact);
    }

    public async Task<ExecutionCheckpoint> CheckpointExternalLeaseAsync(
        ExecutionExternalWorkerCheckpointRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerCheckpointRequest(request, Limits);
        ExecutionCheckpoint checkpoint;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            checkpoint = CreateCheckpoint(run.Id, request.Checkpoint);
            await UpsertCheckpointAsync(connection, tx, checkpoint, ct);
            tx.Commit();
        }

        await AppendEventAsync(checkpoint.RunId, ExecutionEventTypes.CheckpointWritten, 0, null, $"Checkpoint '{checkpoint.Key}' written.", "info", null, ct);
        return Clone(checkpoint);
    }

    public async Task<ExecutionCheckpoint?> GetExternalLeaseCheckpointAsync(
        ExecutionExternalWorkerCheckpointReadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerCheckpointReadRequest(request, Limits);
        ExecutionCheckpoint? checkpoint;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            checkpoint = await LoadCheckpointAsync(connection, tx, run.Id, request.Key, ct);
            tx.Commit();
        }

        return checkpoint;
    }

    public async Task<ExecutionExternalWorkerWaitResponse> WaitExternalLeaseAsync(
        ExecutionExternalWorkerWaitRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerWaitRequest(request, Limits);
        ExecutionRun run;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            (_, run) = await RequireActiveExternalWorkerLeaseAsync(
                connection,
                tx,
                request.LeaseKey,
                request.LeaseToken,
                request.WorkerId,
                DateTime.UtcNow,
                ct);
            tx.Commit();
        }

        var binding = new ExternalWorkerLeaseBinding
        {
            LeaseKey = request.LeaseKey,
            LeaseToken = request.LeaseToken,
            WorkerId = request.WorkerId
        };
        var outcome = request.Kind == ExecutionExternalWorkerWaitKinds.ExternalEvent
            ? await RegisterExternalEventWaitAsync(run.Id, request.Name, request.TimeoutAtUtc, ct, binding)
            : await RegisterTimerWaitAsync(run.Id, request.Name, request.FireAtUtc!.Value, request.Payload, ct, binding);
        if (outcome is not null)
        {
            return new ExecutionExternalWorkerWaitResponse
            {
                Run = Clone(run),
                Suspended = false,
                Outcome = outcome
            };
        }

        var suspended = await LoadRunAsync(run.Id, ct) ?? throw new InvalidOperationException("External worker run was not found after durable wait registration.");

        return new ExecutionExternalWorkerWaitResponse
        {
            Run = suspended,
            Suspended = true
        };
    }

    public async Task<ExecutionRun> CompleteExternalLeaseAsync(
        ExecutionExternalWorkerCompletionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionContractValidator.ValidateExternalWorkerCompletionRequest(request, Limits);
        var now = DateTime.UtcNow;
        ExecutionRun completed;
        var shouldRetry = false;
        var retryDelay = TimeSpan.Zero;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var lease = await LoadLeaseAsync(connection, tx, request.LeaseKey, ct);
            var runId = GetRunIdFromExternalWorkerLeaseKey(request.LeaseKey);
            var run = await LoadRunAsync(connection, tx, runId, ct);
            if (lease is null || run is null ||
                !ExternalWorkerLeaseMatches(lease, request.LeaseToken, request.WorkerId))
            {
                throw new InvalidOperationException("External worker lease is invalid.");
            }

            if (ExecutionRunStatuses.IsTerminal(run.Status) && IsCompletedExternalWorkerLease(lease))
            {
                tx.Commit();
                return Clone(run);
            }

            EnsureActiveExternalWorkerLease(lease, run, now);
            var result = Clone(request.Result);
            if (run.CancellationRequested && result.Status != ExecutionRunStatuses.TimedOut)
            {
                result = ExecutionRunResult.Cancelled(result.Result);
            }

            var terminalStatus = NormalizeTerminalStatus(result.Status);
            ExecutionRunLifecycle.EnsureTransition(run.Status, terminalStatus);
            run.Status = terminalStatus;
            run.Result = CloneNode(result.Result ?? run.Result);
            run.StatusDetails = CloneObject(result.StatusDetails ?? run.StatusDetails);
            if (run.Status == ExecutionRunStatuses.Succeeded)
            {
                run.FailureClass = null;
                run.Error = null;
            }
            else
            {
                run.FailureClass = result.FailureClass ?? run.FailureClass;
                run.Error = result.Error ?? run.Error;
            }

            run.CancellationRequested = run.CancellationRequested || run.Status == ExecutionRunStatuses.Cancelled;
            shouldRetry = ShouldRetry(run);
            if (shouldRetry)
            {
                retryDelay = CalculateRetryDelay(run);
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
                run.Status = ExecutionRunStatuses.Waiting;
                run.ScheduledAtUtc = now.Add(retryDelay);
                run.UpdatedAtUtc = now;
                run.CurrentStep = null;
            }
            else
            {
                CompleteTiming(run);
            }

            lease.ExpiresAtUtc = now;
            lease.Metadata ??= new JsonObject();
            lease.Metadata["state"] = "completed";
            await UpsertRunAsync(connection, tx, run, ct);
            await UpsertLeaseAsync(connection, tx, lease, ct);
            tx.Commit();
            completed = Clone(run);
        }

        if (shouldRetry)
        {
            await AppendEventAsync(
                completed.Id,
                ExecutionEventTypes.RetryScheduled,
                completed.Attempt,
                completed.Status,
                $"Retry {completed.Attempt + 1} of {completed.MaxAttempts} scheduled.",
                "warning",
                new JsonObject
                {
                    ["delaySeconds"] = retryDelay.TotalSeconds,
                    ["failureClass"] = completed.FailureClass,
                    ["error"] = completed.Error
                },
                ct);
        }
        else
        {
            await AppendEventAsync(
                completed.Id,
                completed.Status == ExecutionRunStatuses.Failed ? ExecutionEventTypes.RunFailed : ExecutionEventTypes.RunCompleted,
                completed.Attempt,
                completed.Status,
                completed.Error ?? $"Execution run {completed.Status}.",
                completed.Status == ExecutionRunStatuses.Succeeded ? "info" : "warning",
                null,
                ct);
            await PruneTerminalRunsAsync(ct);
        }

        return completed;
    }

    private async Task<ExecutionExternalWorkerLease?> TryAcquireExternalWorkerLeaseAsync(
        string runId,
        string workerId,
        double ttlSeconds,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseKey = GetExternalWorkerLeaseKey(runId);
        ExecutionRun? claimed = null;
        ExecutionLease? workerLease = null;
        string? leaseToken = null;
        var recovered = false;

        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var run = await LoadRunAsync(connection, tx, runId, ct);
            if (run is null || !_externalHandlers.TryGetValue(run.HandlerId, out var handler))
            {
                tx.Rollback();
                return null;
            }

            if (!IsExternalWorkerPermitted(run, workerId))
            {
                tx.Rollback();
                return null;
            }

            if (run.Status == ExecutionRunStatuses.Waiting && run.ScheduledAtUtc.HasValue && run.ScheduledAtUtc.Value > now)
            {
                tx.Rollback();
                return null;
            }

            var existingLease = await LoadLeaseAsync(connection, tx, leaseKey, ct);
            if (existingLease is not null && existingLease.ExpiresAtUtc > now)
            {
                tx.Rollback();
                return null;
            }

            if (run.Status == ExecutionRunStatuses.Running)
            {
                if (existingLease is null || !IsExternalWorkerLease(existingLease))
                {
                    tx.Rollback();
                    return null;
                }

                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued, ExecutionTransitionKind.Recovery);
                run.Status = ExecutionRunStatuses.Queued;
                run.CurrentStep = null;
                recovered = true;
            }
            else if (run.Status is not (ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting))
            {
                tx.Rollback();
                return null;
            }

            if (await HasConcurrencyConflictAsync(connection, tx, run.Id, handler.ConcurrencyKey, ct))
            {
                tx.Rollback();
                return null;
            }

            ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
            run.Status = ExecutionRunStatuses.Running;
            run.Attempt += 1;
            run.StartedAtUtc ??= now;
            run.UpdatedAtUtc = now;
            leaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            workerLease = new ExecutionLease
            {
                LeaseKey = leaseKey,
                OwnerId = workerId,
                RunId = run.Id,
                AcquiredAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(ttlSeconds),
                Metadata = new JsonObject
                {
                    ["protocol"] = ExternalWorkerLeaseProtocol,
                    ["tokenHash"] = Sha256(leaseToken),
                    ["state"] = "active"
                }
            };
            await UpsertRunAsync(connection, tx, run, ct);
            await UpsertLeaseAsync(connection, tx, workerLease, ct);
            tx.Commit();
            claimed = Clone(run);
        }

        if (recovered)
        {
            await AppendEventAsync(claimed!.Id, ExecutionEventTypes.RunStatus, claimed.Attempt, claimed.Status, "Expired external worker lease recovered.", "warning", null, ct);
        }

        await AppendEventAsync(claimed!.Id, ExecutionEventTypes.LeaseAcquired, claimed.Attempt, claimed.Status, "External worker lease acquired.", "info", null, ct);
        await AppendEventAsync(claimed.Id, ExecutionEventTypes.RunStarted, claimed.Attempt, claimed.Status, "External worker run started.", "info", null, ct);
        return new ExecutionExternalWorkerLease
        {
            LeaseKey = workerLease!.LeaseKey,
            LeaseToken = leaseToken!,
            WorkerId = workerLease.OwnerId,
            Run = claimed,
            AcquiredAtUtc = workerLease.AcquiredAtUtc,
            ExpiresAtUtc = workerLease.ExpiresAtUtc
        };
    }

    private async Task<(ExecutionLease Lease, ExecutionRun Run)> RequireActiveExternalWorkerLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string leaseKey,
        string leaseToken,
        string workerId,
        DateTime now,
        CancellationToken ct)
    {
        var runId = GetRunIdFromExternalWorkerLeaseKey(leaseKey);
        var lease = await LoadLeaseAsync(connection, tx, leaseKey, ct);
        var run = await LoadRunAsync(connection, tx, runId, ct);
        if (lease is null || run is null || !ExternalWorkerLeaseMatches(lease, leaseToken, workerId))
        {
            throw new InvalidOperationException("External worker lease is invalid.");
        }

        EnsureActiveExternalWorkerLease(lease, run, now);
        return (lease, run);
    }

    private static async Task SuspendExternalWorkerLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ExecutionLease lease,
        CancellationToken ct)
    {
        lease.ExpiresAtUtc = DateTime.UtcNow;
        lease.Metadata ??= new JsonObject();
        lease.Metadata["state"] = "suspended";
        await UpsertLeaseAsync(connection, tx, lease, ct);
    }

    private async Task<bool> HasConcurrencyConflictAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        string? concurrencyKey,
        CancellationToken ct)
    {
        var handlerIds = GetHandlerIdsForConcurrencyKey(concurrencyKey);
        if (handlerIds.Count == 0)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        var parameters = new List<string>(handlerIds.Count);
        for (var i = 0; i < handlerIds.Count; i++)
        {
            var name = "$handler_" + i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(name);
            command.Parameters.AddWithValue(name, handlerIds[i]);
        }

        command.CommandText = $@"
            SELECT COUNT(*)
            FROM vyral_execution_runs
            WHERE id <> $run_id
                AND status = 'running'
                AND handler_id IN ({string.Join(", ", parameters)});";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static string GetExternalWorkerLeaseKey(string runId)
    {
        return ExternalWorkerLeasePrefix + runId;
    }

    private sealed class ExternalWorkerLeaseBinding
    {
        public required string LeaseKey { get; init; }
        public required string LeaseToken { get; init; }
        public required string WorkerId { get; init; }
    }

    private static string GetRunIdFromExternalWorkerLeaseKey(string leaseKey)
    {
        if (!leaseKey.StartsWith(ExternalWorkerLeasePrefix, StringComparison.Ordinal) ||
            leaseKey.Length == ExternalWorkerLeasePrefix.Length)
        {
            throw new InvalidOperationException("External worker lease key is invalid.");
        }

        return leaseKey[ExternalWorkerLeasePrefix.Length..];
    }

    private static bool IsExternalWorkerLease(ExecutionLease lease)
    {
        return string.Equals(GetLeaseMetadataString(lease, "protocol"), ExternalWorkerLeaseProtocol, StringComparison.Ordinal);
    }

    private static bool IsCompletedExternalWorkerLease(ExecutionLease lease)
    {
        return IsExternalWorkerLease(lease) &&
            string.Equals(GetLeaseMetadataString(lease, "state"), "completed", StringComparison.Ordinal);
    }

    private static bool ExternalWorkerLeaseMatches(ExecutionLease lease, string leaseToken, string workerId)
    {
        var expectedTokenHash = GetLeaseMetadataString(lease, "tokenHash");
        if (!IsExternalWorkerLease(lease) ||
            !string.Equals(lease.OwnerId, workerId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(expectedTokenHash))
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(Sha256(leaseToken));
        var expectedBytes = Encoding.UTF8.GetBytes(expectedTokenHash);
        return actualBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string? GetLeaseMetadataString(ExecutionLease lease, string key)
    {
        return lease.Metadata is not null &&
            lease.Metadata.TryGetPropertyValue(key, out var value) &&
            value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static void EnsureActiveExternalWorkerLease(ExecutionLease lease, ExecutionRun run, DateTime now)
    {
        if (run.Status != ExecutionRunStatuses.Running ||
            !string.Equals(GetLeaseMetadataString(lease, "state"), "active", StringComparison.Ordinal) ||
            lease.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("External worker lease is no longer active.");
        }
    }

    private static async Task UpsertLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ExecutionLease lease,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_leases (lease_key, owner_id, run_id, expires_at_utc, lease_json)
            VALUES ($lease_key, $owner_id, $run_id, $expires_at_utc, $lease_json)
            ON CONFLICT(lease_key) DO UPDATE SET
                owner_id = excluded.owner_id,
                run_id = excluded.run_id,
                expires_at_utc = excluded.expires_at_utc,
                lease_json = excluded.lease_json;";
        command.Parameters.AddWithValue("$lease_key", lease.LeaseKey);
        command.Parameters.AddWithValue("$owner_id", lease.OwnerId);
        command.Parameters.AddWithValue("$run_id", lease.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$expires_at_utc", lease.ExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$lease_json", Serialize(lease));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<ExecutionWaitResult> WaitForExternalEventAsync(
        string runId,
        string name,
        DateTime? timeoutAtUtc,
        CancellationToken ct)
    {
        ExecutionContractValidator.ValidateExternalEventRequest(new ExecutionExternalEventRequest
        {
            RunId = runId,
            Name = name
        }, Limits);
        var result = await RegisterExternalEventWaitAsync(runId, NormalizeRequired(name, "External event name"), timeoutAtUtc, ct);
        if (result is not null)
        {
            return result;
        }

        throw new ExecutionRunSuspendedException();
    }

    private async Task<ExecutionWaitResult> WaitForTimerAsync(
        string runId,
        string name,
        DateTime fireAtUtc,
        JsonNode? payload,
        CancellationToken ct)
    {
        ExecutionContractValidator.ValidateTimerRequest(new ExecutionTimerRequest
        {
            RunId = runId,
            Name = name,
            FireAtUtc = fireAtUtc,
            Payload = payload
        }, Limits);
        var result = await RegisterTimerWaitAsync(runId, NormalizeRequired(name, "Timer name"), fireAtUtc, payload, ct);
        if (result is not null)
        {
            return result;
        }

        throw new ExecutionRunSuspendedException();
    }

    private async Task<ExecutionWaitResult?> RegisterExternalEventWaitAsync(
        string runId,
        string name,
        DateTime? timeoutAtUtc,
        CancellationToken ct,
        ExternalWorkerLeaseBinding? externalLease = null)
    {
        var now = DateTime.UtcNow;
        DateTime? scheduleAt = timeoutAtUtc?.ToUniversalTime();
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var activeLease = externalLease is null ? null : (await RequireActiveExternalWorkerLeaseAsync(
                connection, tx, externalLease.LeaseKey, externalLease.LeaseToken, externalLease.WorkerId, now, ct)).Lease;
            var outcome = await TakeWaitOutcomeAsync(connection, tx, runId, DurableWaitKinds.ExternalEvent, name, ct);
            if (outcome is not null)
            {
                tx.Commit();
                return outcome;
            }

            var priorEvent = await FindUnconsumedExternalEventAsync(connection, tx, runId, name, ct);
            if (priorEvent is not null)
            {
                await MarkExternalEventConsumedAsync(connection, tx, priorEvent, ct);
                tx.Commit();
                return new ExecutionWaitResult
                {
                    Name = name,
                    Outcome = ExecutionWaitOutcomes.ExternalEvent,
                    Event = Clone(priorEvent)
                };
            }

            if (scheduleAt.HasValue && scheduleAt.Value <= now)
            {
                tx.Commit();
                return new ExecutionWaitResult
                {
                    Name = name,
                    Outcome = ExecutionWaitOutcomes.TimedOut
                };
            }

            await SetDurableWaitAsync(
                connection,
                tx,
                new DurableWait
                {
                    RunId = runId,
                    Kind = DurableWaitKinds.ExternalEvent,
                    Name = name,
                    FireAtUtc = scheduleAt
                },
                ct);
            if (activeLease is not null) await SuspendExternalWorkerLeaseAsync(connection, tx, activeLease, ct);
            tx.Commit();
        }

        await AppendEventAsync(runId, ExecutionEventTypes.WaitRegistered, 0, ExecutionRunStatuses.Waiting, $"Waiting for external event '{name}'.", "info", null, ct);
        if (scheduleAt.HasValue)
        {
            StartLocalHandlerIfRegistered(runId, scheduleAt.Value - DateTime.UtcNow);
        }

        return null;
    }

    private async Task<ExecutionWaitResult?> RegisterTimerWaitAsync(
        string runId,
        string name,
        DateTime fireAtUtc,
        JsonNode? payload,
        CancellationToken ct,
        ExternalWorkerLeaseBinding? externalLease = null)
    {
        var now = DateTime.UtcNow;
        var fireAt = fireAtUtc.ToUniversalTime();
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var activeLease = externalLease is null ? null : (await RequireActiveExternalWorkerLeaseAsync(
                connection, tx, externalLease.LeaseKey, externalLease.LeaseToken, externalLease.WorkerId, now, ct)).Lease;
            var outcome = await TakeWaitOutcomeAsync(connection, tx, runId, DurableWaitKinds.Timer, name, ct);
            if (outcome is not null)
            {
                tx.Commit();
                return outcome;
            }

            var timer = new ExecutionTimer
            {
                Id = OrderedId.CreateString(),
                Name = name,
                RunId = runId,
                FireAtUtc = fireAt,
                CreatedAtUtc = now,
                Payload = CloneNode(payload)
            };
            if (fireAt <= now)
            {
                tx.Commit();
                return new ExecutionWaitResult
                {
                    Name = name,
                    Outcome = ExecutionWaitOutcomes.Timer,
                    Timer = timer
                };
            }

            await InsertTimerAsync(connection, tx, timer, ct);
            await SetDurableWaitAsync(
                connection,
                tx,
                new DurableWait
                {
                    RunId = runId,
                    Kind = DurableWaitKinds.Timer,
                    Name = name,
                    FireAtUtc = fireAt,
                    Timer = timer
                },
                ct);
            if (activeLease is not null) await SuspendExternalWorkerLeaseAsync(connection, tx, activeLease, ct);
            tx.Commit();
        }

        await AppendEventAsync(runId, ExecutionEventTypes.WaitRegistered, 0, ExecutionRunStatuses.Waiting, $"Waiting for timer '{name}'.", "info", null, ct);
        StartLocalHandlerIfRegistered(runId, fireAt - DateTime.UtcNow);
        return null;
    }

    private async Task ResumeExternalEventWaitAsync(ExecutionExternalEvent externalEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalEvent.RunId))
        {
            return;
        }

        var resumed = false;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var wait = await LoadDurableWaitAsync(connection, tx, externalEvent.RunId!, ct);
            if (wait is null ||
                !string.Equals(wait.Kind, DurableWaitKinds.ExternalEvent, StringComparison.Ordinal) ||
                !string.Equals(wait.Name, externalEvent.Name, StringComparison.Ordinal))
            {
                tx.Commit();
                return;
            }

            await MarkExternalEventConsumedAsync(connection, tx, externalEvent, ct);
            await UpsertWaitOutcomeAsync(connection, tx, wait.RunId, new ExecutionWaitResult
            {
                Name = wait.Name,
                Outcome = ExecutionWaitOutcomes.ExternalEvent,
                Event = Clone(externalEvent)
            }, wait.Kind, ct);
            await DeleteDurableWaitAsync(connection, tx, wait.RunId, ct);
            resumed = await QueueResumedWaitRunAsync(connection, tx, wait.RunId, ct);
            tx.Commit();
        }

        if (resumed)
        {
            await AppendEventAsync(externalEvent.RunId!, ExecutionEventTypes.WaitResumed, 0, ExecutionRunStatuses.Queued, $"External event '{externalEvent.Name}' resumed execution.", "info", null, ct);
            StartLocalHandlerIfRegistered(externalEvent.RunId!);
        }
    }

    private async Task<bool> ResumeDueWaitAsync(string runId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        DurableWait? completedWait = null;
        var resumed = false;
        using (var connection = await OpenAsync(ct))
        using (var tx = connection.BeginTransaction(deferred: false))
        {
            var wait = await LoadDurableWaitAsync(connection, tx, runId, ct);
            if (wait?.FireAtUtc is null || wait.FireAtUtc.Value > now)
            {
                tx.Commit();
                return false;
            }

            var outcome = wait.Kind == DurableWaitKinds.Timer
                ? new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.Timer, Timer = Clone(wait.Timer!) }
                : new ExecutionWaitResult { Name = wait.Name, Outcome = ExecutionWaitOutcomes.TimedOut };
            await UpsertWaitOutcomeAsync(connection, tx, wait.RunId, outcome, wait.Kind, ct);
            await DeleteDurableWaitAsync(connection, tx, wait.RunId, ct);
            resumed = await QueueResumedWaitRunAsync(connection, tx, wait.RunId, ct);
            tx.Commit();
            completedWait = wait;
        }

        if (resumed && completedWait is not null)
        {
            var eventType = completedWait.Kind == DurableWaitKinds.Timer
                ? ExecutionEventTypes.WaitResumed
                : ExecutionEventTypes.WaitTimedOut;
            var message = completedWait.Kind == DurableWaitKinds.Timer
                ? $"Timer '{completedWait.Name}' resumed execution."
                : $"Wait for external event '{completedWait.Name}' timed out.";
            await AppendEventAsync(completedWait.RunId, eventType, 0, ExecutionRunStatuses.Queued, message, "info", null, ct);
            StartLocalHandlerIfRegistered(completedWait.RunId);
        }

        return resumed;
    }

    private void StartLocalHandlerIfRegistered(string runId, TimeSpan? delay = null)
    {
        var _ = Task.Run(async () =>
        {
            var run = await LoadRunAsync(runId, CancellationToken.None);
            if (run is not null && _handlers.ContainsKey(run.HandlerId))
            {
                StartWorker(runId, delay);
            }
        });
    }

    private void StartWorker(string runId, TimeSpan? delay = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay.HasValue && delay.Value > TimeSpan.Zero)
                {
                    await Task.Delay(delay.Value);
                }

                await RunAsync(runId, CancellationToken.None);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                StartWorker(runId, TimeSpan.FromMilliseconds(Math.Max(10, Options.ConcurrencyRetryDelayMs)));
            }
        });
    }

    private async Task RunAsync(string runId, CancellationToken outerCt)
    {
        if (!_inFlightRuns.TryAdd(runId, 0))
        {
            // A timer/event dispatch can arrive just before the invocation that registered its
            // durable wait releases the in-flight marker. Do not drop that only wake-up: retry
            // through the same bounded local dispatcher after the current invocation has had a
            // chance to finish its transaction and cleanup.
            StartWorker(runId, TimeSpan.FromMilliseconds(Math.Max(10, Options.ConcurrencyRetryDelayMs)));
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _cancellations[runId] = cts;
        try
        {
            var run = await LoadRunAsync(runId, cts.Token);
            if (run is null || ExecutionRunStatuses.IsTerminal(run.Status))
            {
                return;
            }

            if (run.Status == ExecutionRunStatuses.Waiting && await ResumeDueWaitAsync(run.Id, cts.Token))
            {
                run = await LoadRunAsync(runId, cts.Token) ?? run;
            }

            if (run.Status == ExecutionRunStatuses.Waiting &&
                run.ScheduledAtUtc.HasValue &&
                run.ScheduledAtUtc.Value > DateTime.UtcNow)
            {
                StartWorker(run.Id, run.ScheduledAtUtc.Value - DateTime.UtcNow);
                return;
            }

            if (run.Status == ExecutionRunStatuses.Waiting && await HasDurableWaitAsync(run.Id, cts.Token))
            {
                return;
            }

            if (!_handlers.TryGetValue(run.HandlerId, out var handler))
            {
                ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Rejected);
                run.Status = ExecutionRunStatuses.Rejected;
                run.FailureClass = ExecutionFailureClasses.HandlerMissing;
                run.Error = $"Execution handler '{run.HandlerId}' is not registered.";
                CompleteTiming(run);
                await UpsertRunAsync(run, CancellationToken.None);
                await AppendEventAsync(run.Id, ExecutionEventTypes.RunRejected, run.Attempt, run.Status, run.Error, "warning", null, CancellationToken.None);
                return;
            }

            var claimed = await TryClaimRunAsync(run, handler.Descriptor.ConcurrencyKey, cts.Token);
            if (claimed is null)
            {
                var pending = await LoadRunAsync(run.Id, cts.Token);
                if (pending is not null && ExecutionRunLifecycle.IsActive(pending.Status))
                {
                    StartWorker(run.Id, TimeSpan.FromMilliseconds(Math.Max(10, Options.ConcurrencyRetryDelayMs)));
                }

                return;
            }

            run = claimed;
            await AppendEventAsync(run.Id, ExecutionEventTypes.RunStarted, run.Attempt, run.Status, "Execution run started.", "info", null, cts.Token);

            ExecutionRunResult result;
            try
            {
                if (run.CancellationRequested)
                {
                    throw new OperationCanceledException(cts.Token);
                }

                var context = new LocalExecutionRunContext(this, run, cts.Token);
                result = await handler.ExecuteAsync(context, cts.Token);
                if ((await LoadRunAsync(run.Id, CancellationToken.None))?.CancellationRequested == true &&
                    result.Status != ExecutionRunStatuses.TimedOut)
                {
                    result = ExecutionRunResult.Cancelled(result.Result);
                }

                ExecutionContractValidator.ValidateRunResult(result, Limits);
            }
            catch (OperationCanceledException)
            {
                result = ExecutionRunResult.Cancelled((await LoadRunAsync(run.Id, CancellationToken.None))?.Result);
            }
            catch (ExecutionRunSuspendedException)
            {
                return;
            }
            catch (Exception ex)
            {
                result = ExecutionRunResult.Failed(ExecutionFailureClasses.Unknown, ExecutionContractValidator.BoundText(ex.Message, Limits.MaxTraceMessageChars) ?? "Execution handler failed.");
            }

            ExecutionContractValidator.ValidateRunResult(result, Limits);
            var latest = await LoadRunAsync(run.Id, CancellationToken.None) ?? run;
            var terminalStatus = NormalizeTerminalStatus(result.Status);
            ExecutionRunLifecycle.EnsureTransition(latest.Status, terminalStatus);
            latest.Status = terminalStatus;
            latest.Result = CloneNode(result.Result ?? latest.Result);
            latest.StatusDetails = CloneObject(result.StatusDetails ?? latest.StatusDetails);
            if (latest.Status == ExecutionRunStatuses.Succeeded)
            {
                latest.FailureClass = null;
                latest.Error = null;
            }
            else
            {
                latest.FailureClass = result.FailureClass ?? latest.FailureClass;
                latest.Error = result.Error ?? latest.Error;
            }
            latest.CancellationRequested = latest.CancellationRequested || latest.Status == ExecutionRunStatuses.Cancelled;

            if (ShouldRetry(latest))
            {
                var retryDelay = CalculateRetryDelay(latest);
                ExecutionRunLifecycle.EnsureTransition(latest.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.Retry);
                latest.Status = ExecutionRunStatuses.Waiting;
                latest.ScheduledAtUtc = DateTime.UtcNow.Add(retryDelay);
                latest.UpdatedAtUtc = DateTime.UtcNow;
                latest.CurrentStep = null;
                await UpsertRunAsync(latest, CancellationToken.None);
                await AppendEventAsync(
                    latest.Id,
                    ExecutionEventTypes.RetryScheduled,
                    latest.Attempt,
                    latest.Status,
                    $"Retry {latest.Attempt + 1} of {latest.MaxAttempts} scheduled.",
                    "warning",
                    new JsonObject
                    {
                        ["delaySeconds"] = retryDelay.TotalSeconds,
                        ["failureClass"] = latest.FailureClass,
                        ["error"] = latest.Error
                    },
                    CancellationToken.None);
                StartWorker(latest.Id, retryDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : retryDelay);
                return;
            }

            CompleteTiming(latest);
            await UpsertRunAsync(latest, CancellationToken.None);
            await AppendEventAsync(
                latest.Id,
                latest.Status == ExecutionRunStatuses.Failed ? ExecutionEventTypes.RunFailed : ExecutionEventTypes.RunCompleted,
                latest.Attempt,
                latest.Status,
                latest.Error ?? $"Execution run {latest.Status}.",
                latest.Status == ExecutionRunStatuses.Succeeded ? "info" : "warning",
                null,
                CancellationToken.None);
            await PruneTerminalRunsAsync(CancellationToken.None);
        }
        finally
        {
            _cancellations.TryRemove(runId, out _);
            _inFlightRuns.TryRemove(runId, out _);
            cts.Dispose();
        }
    }

    private async Task<ExecutionRun> ReportAsync(string runId, ExecutionRunUpdate update, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateRunUpdate(update, Limits);
        var run = await LoadRunAsync(runId, ct) ?? throw new InvalidOperationException($"Execution run '{runId}' was not found.");
        if (ExecutionRunStatuses.IsTerminal(run.Status))
        {
            return run;
        }

        ApplyUpdate(run, update);
        run.UpdatedAtUtc = DateTime.UtcNow;
        await UpsertRunAsync(run, ct);
        await AppendEventAsync(run.Id, ExecutionEventTypes.RunStatus, run.Attempt, run.Status, run.CurrentStep, "info", update.StatusDetails, ct);
        return Clone(run);
    }

    private async Task<ExecutionArtifact> PutArtifactAsync(string runId, ExecutionArtifactWrite artifactWrite, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateArtifactWrite(artifactWrite, Limits);
        var run = await LoadRunAsync(runId, ct) ?? throw new InvalidOperationException($"Execution run '{runId}' was not found.");
        var artifact = await CreateArtifactAsync(run, artifactWrite, ct);

        using var connection = await OpenAsync(ct);
        await InsertArtifactAsync(connection, null, artifact, ct);
        await AppendEventAsync(runId, ExecutionEventTypes.ArtifactWritten, 0, null, $"Artifact '{artifact.Name}' written.", "info", null, ct);
        return Clone(artifact);
    }

    private async Task<ExecutionArtifact> CreateArtifactAsync(ExecutionRun run, ExecutionArtifactWrite artifactWrite, CancellationToken ct)
    {
        EnsureArtifactBoundary(run, artifactWrite.Name);
        var text = artifactWrite.Text;
        var content = CloneNode(artifactWrite.Content);
        if (text is null && content is not null)
        {
            text = content.ToJsonString(ExecutionJson.Options);
        }

        text ??= artifactWrite.Uri ?? string.Empty;
        var artifact = new ExecutionArtifact
        {
            Id = OrderedId.CreateString(),
            RunId = run.Id,
            Name = NormalizeRequired(artifactWrite.Name, "Artifact name"),
            Kind = string.IsNullOrWhiteSpace(artifactWrite.Kind) ? ExecutionArtifactKinds.Json : artifactWrite.Kind,
            MediaType = artifactWrite.MediaType,
            Content = content,
            Text = artifactWrite.Text,
            Uri = artifactWrite.Uri,
            ContentHash = Sha256(text),
            SizeBytes = Encoding.UTF8.GetByteCount(text),
            CreatedAtUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(artifactWrite.Metadata, StringComparer.Ordinal)
        };

        if (artifact.Uri is null && artifact.SizeBytes > Limits.MaxArtifactInlineBytes)
        {
            artifact.Uri = await OffloadArtifactAsync(artifact, text, ct);
            artifact.Text = null;
            artifact.Content = null;
            AddArtifactMetadata(artifact.Metadata, "storage", LocalArtifactStorage);
            AddArtifactMetadata(artifact.Metadata, "offloaded", "true");
            AddArtifactMetadata(artifact.Metadata, "inline", "false");
        }

        return artifact;
    }

    private static async Task InsertArtifactAsync(SqliteConnection connection, SqliteTransaction? tx, ExecutionArtifact artifact, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_artifacts (
                id,
                run_id,
                name,
                created_at_utc,
                content_hash,
                artifact_json
            )
            VALUES (
                $id,
                $run_id,
                $name,
                $created_at_utc,
                $content_hash,
                $artifact_json
            );";
        command.Parameters.AddWithValue("$id", artifact.Id);
        command.Parameters.AddWithValue("$run_id", artifact.RunId);
        command.Parameters.AddWithValue("$name", artifact.Name);
        command.Parameters.AddWithValue("$created_at_utc", artifact.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$content_hash", artifact.ContentHash);
        command.Parameters.AddWithValue("$artifact_json", Serialize(artifact));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<ExecutionCheckpoint> PutCheckpointAsync(string runId, ExecutionCheckpointWrite checkpointWrite, CancellationToken ct)
    {
        ExecutionContractValidator.ValidateCheckpointWrite(checkpointWrite, Limits);
        var checkpoint = CreateCheckpoint(runId, checkpointWrite);

        using var connection = await OpenAsync(ct);
        await UpsertCheckpointAsync(connection, null, checkpoint, ct);
        await AppendEventAsync(runId, ExecutionEventTypes.CheckpointWritten, 0, null, $"Checkpoint '{checkpoint.Key}' written.", "info", null, ct);
        return Clone(checkpoint);
    }

    private ExecutionCheckpoint CreateCheckpoint(string runId, ExecutionCheckpointWrite checkpointWrite)
    {
        var content = CloneNode(checkpointWrite.Content) ?? new JsonObject();
        var contentText = content.ToJsonString(ExecutionJson.Options);
        return new ExecutionCheckpoint
        {
            RunId = NormalizeRequired(runId, "Run id"),
            Key = NormalizeRequired(checkpointWrite.Key, "Checkpoint key"),
            Content = content,
            ContentHash = Sha256(contentText),
            UpdatedAtUtc = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(checkpointWrite.Metadata, StringComparer.Ordinal)
        };
    }

    private static async Task UpsertCheckpointAsync(SqliteConnection connection, SqliteTransaction? tx, ExecutionCheckpoint checkpoint, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_checkpoints (
                run_id,
                key,
                updated_at_utc,
                content_hash,
                checkpoint_json
            )
            VALUES (
                $run_id,
                $key,
                $updated_at_utc,
                $content_hash,
                $checkpoint_json
            )
            ON CONFLICT(run_id, key) DO UPDATE SET
                updated_at_utc = excluded.updated_at_utc,
                content_hash = excluded.content_hash,
                checkpoint_json = excluded.checkpoint_json;";
        command.Parameters.AddWithValue("$run_id", checkpoint.RunId);
        command.Parameters.AddWithValue("$key", checkpoint.Key);
        command.Parameters.AddWithValue("$updated_at_utc", checkpoint.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$content_hash", checkpoint.ContentHash);
        command.Parameters.AddWithValue("$checkpoint_json", Serialize(checkpoint));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> OffloadArtifactAsync(ExecutionArtifact artifact, string content, CancellationToken ct)
    {
        var runDirectory = Path.Combine(_artifactDirectory, SafePathSegment(artifact.RunId));
        Directory.CreateDirectory(runDirectory);

        var extension = string.Equals(artifact.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal)
            ? ".json"
            : ".txt";
        var path = Path.Combine(runDirectory, SafePathSegment(artifact.Id) + extension);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ct);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
        return path;
    }

    private async Task<ExecutionArtifact> RehydrateArtifactAsync(ExecutionArtifact artifact, CancellationToken ct)
    {
        var clone = Clone(artifact);
        if ((clone.Text is null && clone.Content is null) &&
            !string.IsNullOrWhiteSpace(clone.Uri) &&
            string.Equals(GetArtifactMetadata(clone.Metadata, "storage"), LocalArtifactStorage, StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(clone.Uri);
            var root = Path.GetFullPath(_artifactDirectory);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(path, root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Execution artifact uri is outside the local artifact directory.");
            }

            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
                if (string.Equals(clone.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal))
                {
                    clone.Content = JsonNode.Parse(text);
                }
                else
                {
                    clone.Text = text;
                }
            }
        }

        return clone;
    }

    private void AddArtifactMetadata(Dictionary<string, string> metadata, string key, string value)
    {
        if (metadata.ContainsKey(key) || metadata.Count < Limits.MaxTagCount)
        {
            metadata[key] = value;
        }
    }

    private static string? GetArtifactMetadata(Dictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value : null;
    }

    private async Task RecordEventAsync(string runId, string type, string? message, string severity, JsonObject? details, CancellationToken ct)
    {
        var run = await LoadRunAsync(runId, ct);
        await AppendEventAsync(runId, type, run?.Attempt ?? 0, run?.Status, message, severity, details, ct);
    }

    private async Task<ExecutionRun?> LoadRunAsync(string runId, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json FROM vyral_execution_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<ExecutionRun>(json) : null;
    }

    private static async Task<ExecutionRun?> LoadRunAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT run_json FROM vyral_execution_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<ExecutionRun>(json) : null;
    }

    private async Task<ExecutionRun?> FindRunByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_json FROM vyral_execution_runs WHERE idempotency_key = $idempotency_key LIMIT 1;";
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<ExecutionRun>(json) : null;
    }

    private async Task<IReadOnlyList<ExecutionRun>> LoadActiveRunsAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT run_json
            FROM vyral_execution_runs
            WHERE status IN ('queued', 'waiting', 'running')
            ORDER BY created_at_utc ASC, id ASC;";

        var runs = new List<ExecutionRun>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var run = Deserialize<ExecutionRun>(reader.GetString(0));
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private async Task UpsertRunAsync(ExecutionRun run, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO vyral_execution_runs (
                id,
                handler_id,
                plugin_id,
                status,
                idempotency_key,
                correlation_id,
                created_at_utc,
                updated_at_utc,
                scheduled_at_utc,
                started_at_utc,
                completed_at_utc,
                run_json
            )
            VALUES (
                $id,
                $handler_id,
                $plugin_id,
                $status,
                $idempotency_key,
                $correlation_id,
                $created_at_utc,
                $updated_at_utc,
                $scheduled_at_utc,
                $started_at_utc,
                $completed_at_utc,
                $run_json
            )
            ON CONFLICT(id) DO UPDATE SET
                handler_id = excluded.handler_id,
                plugin_id = excluded.plugin_id,
                status = excluded.status,
                idempotency_key = excluded.idempotency_key,
                correlation_id = excluded.correlation_id,
                updated_at_utc = excluded.updated_at_utc,
                scheduled_at_utc = excluded.scheduled_at_utc,
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                run_json = excluded.run_json;";
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$handler_id", run.HandlerId);
        command.Parameters.AddWithValue("$plugin_id", run.PluginId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$idempotency_key", run.IdempotencyKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlation_id", run.CorrelationId);
        command.Parameters.AddWithValue("$created_at_utc", run.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", run.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$scheduled_at_utc", run.ScheduledAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$started_at_utc", run.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completed_at_utc", run.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$run_json", Serialize(run));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ExecutionRun run,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_runs (
                id,
                handler_id,
                plugin_id,
                status,
                idempotency_key,
                correlation_id,
                created_at_utc,
                updated_at_utc,
                scheduled_at_utc,
                started_at_utc,
                completed_at_utc,
                run_json
            )
            VALUES (
                $id,
                $handler_id,
                $plugin_id,
                $status,
                $idempotency_key,
                $correlation_id,
                $created_at_utc,
                $updated_at_utc,
                $scheduled_at_utc,
                $started_at_utc,
                $completed_at_utc,
                $run_json
            )
            ON CONFLICT(id) DO UPDATE SET
                handler_id = excluded.handler_id,
                plugin_id = excluded.plugin_id,
                status = excluded.status,
                idempotency_key = excluded.idempotency_key,
                correlation_id = excluded.correlation_id,
                updated_at_utc = excluded.updated_at_utc,
                scheduled_at_utc = excluded.scheduled_at_utc,
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                run_json = excluded.run_json;";
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$handler_id", run.HandlerId);
        command.Parameters.AddWithValue("$plugin_id", run.PluginId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$idempotency_key", run.IdempotencyKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlation_id", run.CorrelationId);
        command.Parameters.AddWithValue("$created_at_utc", run.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", run.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$scheduled_at_utc", run.ScheduledAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$started_at_utc", run.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completed_at_utc", run.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$run_json", Serialize(run));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<ExecutionRun?> TryClaimRunAsync(ExecutionRun run, string? concurrencyKey, CancellationToken ct)
    {
        if (run.Status is not (ExecutionRunStatuses.Queued or ExecutionRunStatuses.Waiting))
        {
            return null;
        }

        var claimed = Clone(run);
        var start = DateTime.UtcNow;
        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Running);
        claimed.Status = ExecutionRunStatuses.Running;
        claimed.Attempt += 1;
        claimed.StartedAtUtc ??= start;
        claimed.UpdatedAtUtc = start;

        var concurrencyHandlerIds = GetHandlerIdsForConcurrencyKey(concurrencyKey);
        var sql = new StringBuilder(@"
            UPDATE vyral_execution_runs
            SET
                status = $status,
                updated_at_utc = $updated_at_utc,
                started_at_utc = $started_at_utc,
                run_json = $run_json
            WHERE id = $id
                AND status IN ('queued', 'waiting')");
        if (concurrencyHandlerIds.Count > 0)
        {
            sql.Append(@"
                AND NOT EXISTS (
                    SELECT 1
                    FROM vyral_execution_runs
                    WHERE id <> $id
                        AND status = 'running'
                        AND handler_id IN (");
            for (var i = 0; i < concurrencyHandlerIds.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append("$concurrency_handler_");
                sql.Append(i.ToString(CultureInfo.InvariantCulture));
            }

            sql.Append(@")
                )");
        }

        sql.Append(';');

        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$id", claimed.Id);
        command.Parameters.AddWithValue("$status", claimed.Status);
        command.Parameters.AddWithValue("$updated_at_utc", claimed.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$started_at_utc", claimed.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$run_json", Serialize(claimed));
        for (var i = 0; i < concurrencyHandlerIds.Count; i++)
        {
            command.Parameters.AddWithValue(
                $"$concurrency_handler_{i.ToString(CultureInfo.InvariantCulture)}",
                concurrencyHandlerIds[i]);
        }

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? claimed : null;
    }

    private IReadOnlyList<string> GetHandlerIdsForConcurrencyKey(string? concurrencyKey)
    {
        var normalized = NormalizeOptional(concurrencyKey);
        if (normalized is null)
        {
            return Array.Empty<string>();
        }

        return _handlers.Values.Select(handler => handler.Descriptor)
            .Concat(_externalHandlers.Values)
            .Where(descriptor => string.Equals(NormalizeOptional(descriptor.ConcurrencyKey), normalized, StringComparison.Ordinal))
            .Select(descriptor => descriptor.HandlerId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(handlerId => handlerId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task AppendEventAsync(
        string runId,
        string type,
        int attempt,
        string? status,
        string? message,
        string severity,
        JsonObject? details,
        CancellationToken ct)
    {
        var run = await LoadRunAsync(runId, ct);
        var item = CreateTraceEvent(run, runId, type, attempt, status, message, severity, details);

        using var connection = await OpenAsync(ct);
        await InsertEventAsync(connection, null, item, ct);
    }

    private ExecutionTraceEvent CreateTraceEvent(
        ExecutionRun? run,
        string runId,
        string type,
        int attempt,
        string? status,
        string? message,
        string severity,
        JsonObject? details)
    {
        message = ExecutionContractValidator.BoundText(message, Limits.MaxTraceMessageChars);
        var item = new ExecutionTraceEvent
        {
            Id = OrderedId.CreateString(),
            SequenceId = OrderedId.CreateString(),
            RunId = runId,
            Type = type,
            TimestampUtc = DateTime.UtcNow,
            Attempt = attempt,
            Status = status,
            Message = message,
            Severity = string.IsNullOrWhiteSpace(severity) ? "info" : severity,
            Details = RedactTraceDetails(run, details),
            Context = BuildEventContext(runId, run)
        };
        ExecutionContractValidator.ValidateTraceEvent(item, Limits);
        return item;
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        ExecutionTraceEvent item,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_events (
                id,
                run_id,
                type,
                timestamp_utc,
                event_json
            )
            VALUES (
                $id,
                $run_id,
                $type,
                $timestamp_utc,
                $event_json
            );";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$run_id", item.RunId);
        command.Parameters.AddWithValue("$type", item.Type);
        command.Parameters.AddWithValue("$timestamp_utc", item.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$event_json", Serialize(item));
        await command.ExecuteNonQueryAsync(ct);
    }

    private Dictionary<string, string> BuildEventContext(string runId, ExecutionRun? run)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapterId"] = Adapter.AdapterId,
            ["runtimeKind"] = Adapter.RuntimeKind,
            ["workerId"] = Options.WorkerId,
            ["runId"] = runId
        };

        if (run is null)
        {
            return context;
        }

        AddContextValue(context, "correlationId", run.CorrelationId);
        AddContextValue(context, "handlerId", run.HandlerId);
        AddContextValue(context, "pluginId", run.PluginId);
        AddContextValue(context, "productId", run.Scope?.ProductId);
        AddContextValue(context, "tenantId", run.Scope?.TenantId);
        AddContextValue(context, "serviceIdentity", run.Scope?.ServiceIdentity);
        return context;
    }

    private static void AddContextValue(Dictionary<string, string> context, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context[key] = value;
        }
    }

    private async Task<int> CountActiveRunsAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM vyral_execution_runs
            WHERE status IN ('queued', 'waiting', 'running');";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    private async Task<Dictionary<string, int>> CountRunsByStatusAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT status, COUNT(*)
            FROM vyral_execution_runs
            GROUP BY status
            ORDER BY status;";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private async Task<Dictionary<string, int>> CountMaintenanceRowsAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["runs"] = await CountTableRowsAsync(connection, "vyral_execution_runs", ct),
            ["events"] = await CountTableRowsAsync(connection, "vyral_execution_events", ct),
            ["artifacts"] = await CountTableRowsAsync(connection, "vyral_execution_artifacts", ct),
            ["checkpoints"] = await CountTableRowsAsync(connection, "vyral_execution_checkpoints", ct),
            ["leases"] = await CountTableRowsAsync(connection, "vyral_execution_leases", ct),
            ["timers"] = await CountTableRowsAsync(connection, "vyral_execution_timers", ct),
            ["externalEvents"] = await CountTableRowsAsync(connection, "vyral_execution_external_events", ct),
            ["waits"] = await CountTableRowsAsync(connection, "vyral_execution_waits", ct),
            ["waitOutcomes"] = await CountTableRowsAsync(connection, "vyral_execution_wait_outcomes", ct),
            ["externalEventConsumptions"] = await CountTableRowsAsync(connection, "vyral_execution_external_event_consumptions", ct)
        };
        return counts;
    }

    private static async Task<int> CountTableRowsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    private Task PruneTerminalRunsAsync(CancellationToken ct) =>
        PruneTerminalRunsAsync(Options.MaxRetainedTerminalRuns, dryRun: false, ct);

    private async Task<ExecutionMaintenancePruneResult> PruneTerminalRunsAsync(int retainTerminalRuns, bool dryRun, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        var prunedRunIds = await SelectPrunableTerminalRunIdsAsync(connection, tx, retainTerminalRuns, ct);
        var result = new ExecutionMaintenancePruneResult
        {
            DryRun = dryRun,
            RetainTerminalRuns = Math.Max(0, retainTerminalRuns),
            PrunedAtUtc = DateTime.UtcNow,
            RunIds = prunedRunIds.ToList(),
            Runs = prunedRunIds.Count,
            Events = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_events", prunedRunIds, ct),
            Artifacts = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_artifacts", prunedRunIds, ct),
            Checkpoints = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_checkpoints", prunedRunIds, ct),
            Timers = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_timers", prunedRunIds, ct),
            ExternalEvents = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_external_events", prunedRunIds, ct),
            Leases = await CountRowsByRunIdAsync(connection, tx, "vyral_execution_leases", prunedRunIds, ct),
            ArtifactDirectories = CountArtifactDirectoriesForRuns(prunedRunIds)
        };

        if (prunedRunIds.Count == 0 || dryRun)
        {
            tx.Commit();
            return result;
        }

        result.Events = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_events", prunedRunIds, ct);
        result.Artifacts = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_artifacts", prunedRunIds, ct);
        result.Checkpoints = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_checkpoints", prunedRunIds, ct);
        result.Timers = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_timers", prunedRunIds, ct);
        result.ExternalEvents = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_external_events", prunedRunIds, ct);
        _ = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_waits", prunedRunIds, ct);
        _ = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_wait_outcomes", prunedRunIds, ct);
        _ = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_external_event_consumptions", prunedRunIds, ct);
        result.Leases = await DeleteRowsByRunIdAsync(connection, tx, "vyral_execution_leases", prunedRunIds, ct);
        result.Runs = await DeleteRowsByIdAsync(connection, tx, "vyral_execution_runs", prunedRunIds, ct);
        tx.Commit();

        var deletedArtifactDirectories = 0;
        foreach (var runId in prunedRunIds)
        {
            if (DeleteArtifactDirectoryForRun(runId))
            {
                deletedArtifactDirectories++;
            }
        }

        result.ArtifactDirectories = deletedArtifactDirectories;
        return result;
    }

    private static async Task<IReadOnlyList<string>> SelectPrunableTerminalRunIdsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        int retainTerminalRuns,
        CancellationToken ct)
    {
        var prunedRunIds = new List<string>();
        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = @"
            SELECT id
            FROM vyral_execution_runs
            WHERE status NOT IN ('queued', 'waiting', 'running')
                AND id NOT IN (
                    SELECT id
                    FROM vyral_execution_runs
                    WHERE status NOT IN ('queued', 'waiting', 'running')
                    ORDER BY created_at_utc DESC, id ASC
                    LIMIT $max_retained
                );";
        select.Parameters.AddWithValue("$max_retained", Math.Max(0, retainTerminalRuns));
        using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            prunedRunIds.Add(reader.GetString(0));
        }

        return prunedRunIds;
    }

    private Task<int> CountRowsByRunIdAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        IReadOnlyList<string> runIds,
        CancellationToken ct) =>
        CountRowsByIdColumnAsync(connection, tx, tableName, "run_id", runIds, ct);

    private static Task<int> CountRowsByIdColumnAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        string columnName,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        return CountOrDeleteRowsByIdColumnAsync(connection, tx, tableName, columnName, ids, delete: false, ct);
    }

    private static Task<int> DeleteRowsByRunIdAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        IReadOnlyList<string> runIds,
        CancellationToken ct) =>
        DeleteRowsByIdColumnAsync(connection, tx, tableName, "run_id", runIds, ct);

    private static Task<int> DeleteRowsByIdAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        IReadOnlyList<string> ids,
        CancellationToken ct) =>
        DeleteRowsByIdColumnAsync(connection, tx, tableName, "id", ids, ct);

    private static Task<int> DeleteRowsByIdColumnAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        string columnName,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        return CountOrDeleteRowsByIdColumnAsync(connection, tx, tableName, columnName, ids, delete: true, ct);
    }

    private static async Task<int> CountOrDeleteRowsByIdColumnAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tableName,
        string columnName,
        IReadOnlyList<string> ids,
        bool delete,
        CancellationToken ct)
    {
        var affected = 0;
        for (var offset = 0; offset < ids.Count; offset += 100)
        {
            var count = Math.Min(100, ids.Count - offset);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            var parameters = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var parameterName = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, ids[offset + i]);
            }

            command.CommandText = delete
                ? $"DELETE FROM {tableName} WHERE {columnName} IN ({string.Join(", ", parameters)});"
                : $"SELECT COUNT(*) FROM {tableName} WHERE {columnName} IN ({string.Join(", ", parameters)});";
            if (delete)
            {
                affected += await command.ExecuteNonQueryAsync(ct);
            }
            else
            {
                affected += Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            }
        }

        return affected;
    }

    private async Task<ExecutionLease?> LoadLeaseAsync(SqliteConnection connection, SqliteTransaction tx, string leaseKey, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT lease_json FROM vyral_execution_leases WHERE lease_key = $lease_key;";
        command.Parameters.AddWithValue("$lease_key", leaseKey);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<ExecutionLease>(json) : null;
    }

    private static async Task<ExecutionWaitResult?> TakeWaitOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        string kind,
        string name,
        CancellationToken ct)
    {
        using var select = connection.CreateCommand();
        select.Transaction = tx;
        select.CommandText = @"
            SELECT outcome_json
            FROM vyral_execution_wait_outcomes
            WHERE run_id = $run_id AND kind = $kind AND name = $name
            LIMIT 1;";
        select.Parameters.AddWithValue("$run_id", runId);
        select.Parameters.AddWithValue("$kind", kind);
        select.Parameters.AddWithValue("$name", name);
        var value = await select.ExecuteScalarAsync(ct);
        if (value is not string json)
        {
            return null;
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = @"
            DELETE FROM vyral_execution_wait_outcomes
            WHERE run_id = $run_id AND kind = $kind AND name = $name;";
        delete.Parameters.AddWithValue("$run_id", runId);
        delete.Parameters.AddWithValue("$kind", kind);
        delete.Parameters.AddWithValue("$name", name);
        await delete.ExecuteNonQueryAsync(ct);
        return Deserialize<ExecutionWaitResult>(json);
    }

    private static async Task<ExecutionExternalEvent?> FindUnconsumedExternalEventAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        string name,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            SELECT event.event_json
            FROM vyral_execution_external_events AS event
            LEFT JOIN vyral_execution_external_event_consumptions AS consumption
                ON consumption.event_id = event.id
            WHERE event.run_id = $run_id
                AND event.name = $name
                AND consumption.event_id IS NULL
            ORDER BY event.raised_at_utc ASC, event.id ASC
            LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<ExecutionExternalEvent>(json) : null;
    }

    private static async Task MarkExternalEventConsumedAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ExecutionExternalEvent externalEvent,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_external_event_consumptions (event_id, run_id, consumed_at_utc)
            VALUES ($event_id, $run_id, $consumed_at_utc)
            ON CONFLICT(event_id) DO NOTHING;";
        command.Parameters.AddWithValue("$event_id", externalEvent.Id);
        command.Parameters.AddWithValue("$run_id", externalEvent.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$consumed_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DurableWait?> LoadDurableWaitAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT wait_json FROM vyral_execution_waits WHERE run_id = $run_id LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? Deserialize<DurableWait>(json) : null;
    }

    private async Task<bool> HasDurableWaitAsync(string runId, CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM vyral_execution_waits WHERE run_id = $run_id LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ClearDurableWaitStateAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        CancellationToken ct)
    {
        await DeleteDurableWaitAsync(connection, tx, runId, ct);
        using var outcomes = connection.CreateCommand();
        outcomes.Transaction = tx;
        outcomes.CommandText = "DELETE FROM vyral_execution_wait_outcomes WHERE run_id = $run_id;";
        outcomes.Parameters.AddWithValue("$run_id", runId);
        await outcomes.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteDurableWaitAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM vyral_execution_waits WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertWaitOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        ExecutionWaitResult outcome,
        string kind,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_wait_outcomes (run_id, kind, name, outcome_json)
            VALUES ($run_id, $kind, $name, $outcome_json)
            ON CONFLICT(run_id, kind, name) DO UPDATE SET outcome_json = excluded.outcome_json;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", outcome.Name);
        command.Parameters.AddWithValue("$outcome_json", Serialize(outcome));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertTimerAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        ExecutionTimer timer,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_timers (id, name, run_id, fire_at_utc, timer_json)
            VALUES ($id, $name, $run_id, $fire_at_utc, $timer_json);";
        command.Parameters.AddWithValue("$id", timer.Id);
        command.Parameters.AddWithValue("$name", timer.Name);
        command.Parameters.AddWithValue("$run_id", timer.RunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fire_at_utc", timer.FireAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$timer_json", Serialize(timer));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SetDurableWaitAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        DurableWait wait,
        CancellationToken ct)
    {
        var run = await LoadRunAsync(connection, tx, wait.RunId, ct)
            ?? throw new InvalidOperationException($"Execution run '{wait.RunId}' was not found.");
        if (run.Status != ExecutionRunStatuses.Running)
        {
            throw new InvalidOperationException($"Execution run '{wait.RunId}' is not running and cannot register a durable wait.");
        }

        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Waiting, ExecutionTransitionKind.DurableWait);
        run.Status = ExecutionRunStatuses.Waiting;
        run.CurrentStep = $"waiting:{wait.Kind}:{wait.Name}";
        run.ScheduledAtUtc = wait.FireAtUtc;
        run.UpdatedAtUtc = DateTime.UtcNow;
        await UpsertRunAsync(connection, tx, run, ct);

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = @"
            INSERT INTO vyral_execution_waits (run_id, kind, name, fire_at_utc, wait_json)
            VALUES ($run_id, $kind, $name, $fire_at_utc, $wait_json)
            ON CONFLICT(run_id) DO UPDATE SET
                kind = excluded.kind,
                name = excluded.name,
                fire_at_utc = excluded.fire_at_utc,
                wait_json = excluded.wait_json;";
        command.Parameters.AddWithValue("$run_id", wait.RunId);
        command.Parameters.AddWithValue("$kind", wait.Kind);
        command.Parameters.AddWithValue("$name", wait.Name);
        command.Parameters.AddWithValue("$fire_at_utc", wait.FireAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$wait_json", Serialize(wait));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> QueueResumedWaitRunAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string runId,
        CancellationToken ct)
    {
        var run = await LoadRunAsync(connection, tx, runId, ct);
        if (run is null || run.Status != ExecutionRunStatuses.Waiting)
        {
            return false;
        }

        ExecutionRunLifecycle.EnsureTransition(run.Status, ExecutionRunStatuses.Queued);
        run.Status = ExecutionRunStatuses.Queued;
        run.ScheduledAtUtc = null;
        run.CurrentStep = null;
        run.UpdatedAtUtc = DateTime.UtcNow;
        await UpsertRunAsync(connection, tx, run, ct);
        return true;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await ConfigureConnectionAsync(connection, ct);
        return connection;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(_artifactDirectory);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ConfigureConnection(connection);
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            PRAGMA journal_mode=WAL;
            PRAGMA user_version = {CurrentSchemaVersion};

            CREATE TABLE IF NOT EXISTS vyral_execution_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            INSERT INTO vyral_execution_metadata (key, value, updated_at_utc)
            VALUES
                ('schemaVersion', $schema_version, $now),
                ('journalMode', 'wal', $now),
                ('synchronous', 'normal', $now),
                ('artifactDirectory', $artifact_directory, $now)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at_utc = excluded.updated_at_utc;

            CREATE TABLE IF NOT EXISTS vyral_execution_runs (
                id TEXT PRIMARY KEY,
                handler_id TEXT NOT NULL,
                plugin_id TEXT NULL,
                status TEXT NOT NULL,
                idempotency_key TEXT NULL,
                correlation_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                scheduled_at_utc TEXT NULL,
                started_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                run_json TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_vyral_execution_runs_idempotency
                ON vyral_execution_runs(idempotency_key)
                WHERE idempotency_key IS NOT NULL;

            CREATE INDEX IF NOT EXISTS ix_vyral_execution_runs_status
                ON vyral_execution_runs(status, created_at_utc);

            CREATE TABLE IF NOT EXISTS vyral_execution_events (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                type TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                event_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_vyral_execution_events_run
                ON vyral_execution_events(run_id, timestamp_utc);

            CREATE TABLE IF NOT EXISTS vyral_execution_artifacts (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                artifact_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vyral_execution_checkpoints (
                run_id TEXT NOT NULL,
                key TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                checkpoint_json TEXT NOT NULL,
                PRIMARY KEY (run_id, key)
            );

            CREATE TABLE IF NOT EXISTS vyral_execution_leases (
                lease_key TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL,
                run_id TEXT NULL,
                expires_at_utc TEXT NOT NULL,
                lease_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vyral_execution_timers (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                run_id TEXT NULL,
                fire_at_utc TEXT NOT NULL,
                timer_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vyral_execution_external_events (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                run_id TEXT NULL,
                raised_at_utc TEXT NOT NULL,
                event_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_vyral_execution_external_events_wait
                ON vyral_execution_external_events(run_id, name, raised_at_utc);

            CREATE TABLE IF NOT EXISTS vyral_execution_external_event_consumptions (
                event_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                consumed_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vyral_execution_waits (
                run_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                fire_at_utc TEXT NULL,
                wait_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_vyral_execution_waits_due
                ON vyral_execution_waits(fire_at_utc);

            CREATE TABLE IF NOT EXISTS vyral_execution_wait_outcomes (
                run_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                outcome_json TEXT NOT NULL,
                PRIMARY KEY (run_id, kind, name)
            );";
        command.Parameters.AddWithValue("$schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$artifact_directory", _artifactDirectory);
        command.ExecuteNonQuery();
    }

    private void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            PRAGMA busy_timeout={Math.Max(0, Options.BusyTimeoutMs)};
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            PRAGMA busy_timeout={Math.Max(0, Options.BusyTimeoutMs)};
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string ResolveArtifactDirectory(LocalExecutionRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ArtifactDirectory))
        {
            return Path.GetFullPath(options.ArtifactDirectory);
        }

        return Path.GetFullPath(options.DatabasePath) + ".artifacts";
    }

    private static void EnsureDirectoryForFile(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private (int DirectoryCount, int FileCount, long Bytes) MeasureArtifactDirectory()
    {
        if (!Directory.Exists(_artifactDirectory))
        {
            return (0, 0, 0);
        }

        var directoryCount = 0;
        var fileCount = 0;
        var bytes = 0L;
        foreach (var directory in Directory.EnumerateDirectories(_artifactDirectory, "*", SearchOption.AllDirectories))
        {
            _ = directory;
            directoryCount++;
        }

        foreach (var file in Directory.EnumerateFiles(_artifactDirectory, "*", SearchOption.AllDirectories))
        {
            fileCount++;
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch
            {
                // Best-effort diagnostics should not make runtime maintenance unavailable.
            }
        }

        return (directoryCount, fileCount, bytes);
    }

    private int CountArtifactDirectoriesForRuns(IReadOnlyList<string> runIds)
    {
        var count = 0;
        foreach (var runId in runIds)
        {
            var directory = Path.Combine(_artifactDirectory, SafePathSegment(runId));
            if (Directory.Exists(directory))
            {
                count++;
            }
        }

        return count;
    }

    private bool DeleteArtifactDirectoryForRun(string runId)
    {
        var directory = Path.Combine(_artifactDirectory, SafePathSegment(runId));
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
        }
        catch
        {
            // Artifact metadata is already pruned from SQLite. A later retention pass or operator
            // cleanup can remove orphaned local files if the filesystem is temporarily unavailable.
        }

        return false;
    }

    private static string SafePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private int ValidateLimit(int? limit)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new InvalidOperationException("Execution list limit must be greater than zero.");
        }

        var effective = limit ?? Options.DefaultListLimit;
        if (effective > Options.MaxListLimit)
        {
            throw new InvalidOperationException($"Execution list limit cannot exceed {Options.MaxListLimit}.");
        }

        return effective;
    }

    private static void ApplyUpdate(ExecutionRun run, ExecutionRunUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.Status))
        {
            ExecutionRunLifecycle.EnsureTransition(run.Status, update.Status!);
            run.Status = update.Status!;
        }

        run.Requested = update.Requested ?? run.Requested;
        run.Attempted = update.Attempted ?? run.Attempted;
        run.Succeeded = update.Succeeded ?? run.Succeeded;
        run.Failed = update.Failed ?? run.Failed;
        run.Progress = update.Progress.HasValue ? Math.Clamp(update.Progress.Value, 0, 1) : run.Progress;
        run.CurrentStep = update.CurrentStep ?? run.CurrentStep;
        run.FailureClass = update.FailureClass ?? run.FailureClass;
        run.Error = update.Error ?? run.Error;
        run.Result = CloneNode(update.Result ?? run.Result);
        run.StatusDetails = CloneObject(update.StatusDetails ?? run.StatusDetails);
    }

    private static void CompleteTiming(ExecutionRun run)
    {
        var completedAt = DateTime.UtcNow;
        var startedAt = run.StartedAtUtc ?? completedAt;
        run.StartedAtUtc = startedAt;
        run.CompletedAtUtc = completedAt;
        run.UpdatedAtUtc = completedAt;
        run.DurationMs = (completedAt - startedAt).TotalMilliseconds;
        run.CurrentStep = null;
        if (run.Status == ExecutionRunStatuses.Succeeded)
        {
            run.Progress = 1;
        }
    }

    private static string NormalizeTerminalStatus(string status)
    {
        return ExecutionRunStatuses.IsTerminal(status) ? status : ExecutionRunStatuses.Failed;
    }

    private static bool ShouldRetry(ExecutionRun run)
    {
        if (run.CancellationRequested || run.Status is not (ExecutionRunStatuses.Failed or ExecutionRunStatuses.TimedOut))
        {
            return false;
        }

        return run.Attempt < Math.Max(1, run.MaxAttempts);
    }

    private static void EnsureIdempotentReplay(
        ExecutionRun existing,
        string handlerId,
        string? pluginId,
        string payloadHash,
        string idempotencyKey)
    {
        if (string.Equals(existing.HandlerId, handlerId, StringComparison.Ordinal) &&
            string.Equals(existing.PluginId, pluginId, StringComparison.Ordinal) &&
            string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Execution idempotency key '{idempotencyKey}' already belongs to a different run request.");
    }

    private static TimeSpan CalculateRetryDelay(ExecutionRun run)
    {
        var policy = run.RetryPolicy ?? new ExecutionRetryPolicy();
        var initial = Math.Max(0, policy.InitialDelaySeconds);
        var max = Math.Max(initial, policy.MaxDelaySeconds);
        var multiplier = policy.BackoffMultiplier <= 0 ? 1 : policy.BackoffMultiplier;
        var exponent = Math.Max(0, run.Attempt - 1);
        var seconds = initial * Math.Pow(multiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(max, seconds));
    }

    private static IReadOnlyDictionary<string, ExecutionProductPolicy> BuildProductPolicies(
        IReadOnlyList<ExecutionProductPolicy>? policies)
    {
        var result = new Dictionary<string, ExecutionProductPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies ?? Array.Empty<ExecutionProductPolicy>())
        {
            var productId = NormalizeRequired(policy.ProductId, "Execution product policy product id");
            if (policy.MaxPayloadBytes is <= 0)
            {
                throw new InvalidOperationException("Execution product policy max payload bytes must be positive.");
            }

            if (!result.TryAdd(productId, policy))
            {
                throw new InvalidOperationException($"Execution product policy '{productId}' is duplicated.");
            }
        }

        return result;
    }

    private void EnsureRunBoundary(ExecutionRunRequest request, string handlerId)
    {
        if (_productPolicies.Count == 0)
        {
            return;
        }

        var scope = request.Scope ?? throw new InvalidOperationException("Execution scope is required when product policies are configured.");
        var policy = GetProductPolicy(scope);
        if (policy.AllowedHandlerIds.Count > 0 && !policy.AllowedHandlerIds.Contains(handlerId))
        {
            throw new InvalidOperationException($"Handler '{handlerId}' is not allowed for product '{scope.ProductId}'.");
        }

        if (policy.AllowedTenantIds.Count > 0 && !policy.AllowedTenantIds.Contains(scope.TenantId))
        {
            throw new InvalidOperationException($"Tenant '{scope.TenantId}' is not allowed for product '{scope.ProductId}'.");
        }

        if (policy.MaxPayloadBytes.HasValue &&
            Encoding.UTF8.GetByteCount(SerializeNode(request.Payload)) > policy.MaxPayloadBytes.Value)
        {
            throw new InvalidOperationException($"Run payload exceeds the {policy.MaxPayloadBytes.Value} byte limit for product '{scope.ProductId}'.");
        }
    }

    private bool IsExternalWorkerPermitted(ExecutionRun run, string workerId)
    {
        if (_productPolicies.Count == 0)
        {
            return true;
        }

        var policy = GetProductPolicy(run.Scope ?? throw new InvalidOperationException("Scoped execution run is missing its scope."));
        return policy.AllowedServiceIdentities.Count == 0 || policy.AllowedServiceIdentities.Contains(workerId);
    }

    private void EnsureArtifactBoundary(ExecutionRun run, string artifactName)
    {
        if (_productPolicies.Count == 0)
        {
            return;
        }

        var policy = GetProductPolicy(run.Scope ?? throw new InvalidOperationException("Scoped execution run is missing its scope."));
        if (!string.IsNullOrWhiteSpace(policy.ArtifactPrefix) &&
            !artifactName.StartsWith(policy.ArtifactPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Artifact '{artifactName}' must use product '{run.Scope!.ProductId}' prefix '{policy.ArtifactPrefix}'.");
        }
    }

    private ExecutionProductPolicy GetProductPolicy(ExecutionScope scope)
    {
        if (!_productPolicies.TryGetValue(scope.ProductId, out var policy))
        {
            throw new InvalidOperationException($"Execution product '{scope.ProductId}' is not configured.");
        }

        return policy;
    }

    private JsonObject? RedactTraceDetails(ExecutionRun? run, JsonObject? details)
    {
        var clone = CloneObject(details);
        if (clone is null || run?.Scope is null || _productPolicies.Count == 0)
        {
            return clone;
        }

        var policy = GetProductPolicy(run.Scope);
        foreach (var key in policy.RedactedJsonPropertyNames)
        {
            if (clone.ContainsKey(key))
            {
                clone[key] = "[redacted]";
            }
        }

        return clone;
    }

    private static string NormalizeRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static T Clone<T>(T value)
    {
        return Deserialize<T>(Serialize(value))!;
    }

    private static JsonNode? CloneNode(JsonNode? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToJsonString(ExecutionJson.Options));
    }

    private static JsonObject? CloneObject(JsonObject? value)
    {
        return CloneNode(value) as JsonObject;
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, ExecutionJson.Options);
    }

    private static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, ExecutionJson.Options);
    }

    private static string SerializeNode(JsonNode? node)
    {
        return node?.ToJsonString(ExecutionJson.Options) ?? "{}";
    }

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static class DurableWaitKinds
    {
        public const string ExternalEvent = "external_event";
        public const string Timer = "timer";
    }

    private sealed class DurableWait
    {
        public string RunId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime? FireAtUtc { get; set; }
        public ExecutionTimer? Timer { get; set; }
    }

    private sealed class ExecutionRunSuspendedException : Exception
    {
    }

    private sealed class LocalExecutionRunContext : IExecutionRunContext
    {
        private readonly LocalExecutionRuntime _runtime;

        public LocalExecutionRunContext(LocalExecutionRuntime runtime, ExecutionRun run, CancellationToken cancellationToken)
        {
            _runtime = runtime;
            Run = Clone(run);
            CancellationToken = cancellationToken;
        }

        public ExecutionRun Run { get; private set; }
        public CancellationToken CancellationToken { get; }

        public async Task<ExecutionRun> ReportAsync(ExecutionRunUpdate update, CancellationToken ct = default)
        {
            Run = await _runtime.ReportAsync(Run.Id, update, Link(ct));
            return Clone(Run);
        }

        public Task RecordEventAsync(string type, string? message = null, string severity = "info", JsonObject? details = null, CancellationToken ct = default)
        {
            return _runtime.RecordEventAsync(Run.Id, type, message, severity, details, Link(ct));
        }

        public Task<ExecutionArtifact> PutArtifactAsync(ExecutionArtifactWrite artifact, CancellationToken ct = default)
        {
            return _runtime.PutArtifactAsync(Run.Id, artifact, Link(ct));
        }

        public Task<ExecutionCheckpoint> PutCheckpointAsync(ExecutionCheckpointWrite checkpoint, CancellationToken ct = default)
        {
            return _runtime.PutCheckpointAsync(Run.Id, checkpoint, Link(ct));
        }

        public Task<ExecutionCheckpoint?> GetCheckpointAsync(string key, CancellationToken ct = default)
        {
            return _runtime.GetCheckpointAsync(Run.Id, key, Link(ct));
        }

        public Task<ExecutionLease?> TryAcquireLeaseAsync(string leaseKey, double ttlSeconds = 60, JsonObject? metadata = null, CancellationToken ct = default)
        {
            return _runtime.TryAcquireLeaseAsync(new ExecutionLeaseRequest
            {
                LeaseKey = leaseKey,
                OwnerId = Run.Id,
                RunId = Run.Id,
                TtlSeconds = ttlSeconds,
                Metadata = metadata
            }, Link(ct));
        }

        public Task<bool> ReleaseLeaseAsync(string leaseKey, CancellationToken ct = default)
        {
            return _runtime.ReleaseLeaseAsync(leaseKey, Run.Id, Link(ct));
        }

        public Task<ExecutionTimer> ScheduleTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            return _runtime.ScheduleTimerAsync(new ExecutionTimerRequest
            {
                Name = name,
                RunId = Run.Id,
                FireAtUtc = fireAtUtc,
                Payload = payload
            }, Link(ct));
        }

        public Task<ExecutionExternalEvent> RaiseEventAsync(string name, JsonNode? payload = null, CancellationToken ct = default)
        {
            return _runtime.RaiseEventAsync(new ExecutionExternalEventRequest
            {
                Name = name,
                RunId = Run.Id,
                Payload = payload
            }, Link(ct));
        }

        public Task<ExecutionWaitResult> WaitForExternalEventAsync(string name, DateTime? timeoutAtUtc = null, CancellationToken ct = default)
        {
            return _runtime.WaitForExternalEventAsync(Run.Id, name, timeoutAtUtc, Link(ct));
        }

        public Task<ExecutionWaitResult> WaitForTimerAsync(string name, DateTime fireAtUtc, JsonNode? payload = null, CancellationToken ct = default)
        {
            return _runtime.WaitForTimerAsync(Run.Id, name, fireAtUtc, payload, Link(ct));
        }

        private CancellationToken Link(CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
            {
                return CancellationToken;
            }

            return ct;
        }
    }
}
