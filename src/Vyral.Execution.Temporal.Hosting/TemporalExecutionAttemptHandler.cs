using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Execution.Temporal.Hosting;

/// <summary>
/// Executes ordinary Vyral handlers inside a Temporal activity while keeping all portable run
/// state in the durable projection. Handler code never runs inside the coordinator workflow.
/// </summary>
public sealed class TemporalExecutionAttemptHandler : ITemporalExecutionAttemptHandler
{
    private readonly ITemporalExecutionHandlerResolver _handlerResolver;
    private readonly ITemporalExecutionRuntimeStore _store;
    private readonly TemporalExecutionOptions _options;
    private readonly string _workerId;
    private readonly IObjectStore? _artifactObjectStore;

    public TemporalExecutionAttemptHandler(
        ITemporalExecutionHandlerResolver handlerResolver,
        ITemporalExecutionRuntimeStore store,
        TemporalExecutionOptions options,
        string workerId,
        IObjectStore? artifactObjectStore = null)
    {
        _handlerResolver = handlerResolver ?? throw new ArgumentNullException(nameof(handlerResolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (string.IsNullOrWhiteSpace(workerId) ||
            workerId.Length > _options.Limits.MaxIdChars ||
            workerId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Temporal worker id must be 1-{_options.Limits.MaxIdChars} non-control characters.");
        }
        _workerId = workerId.Trim();
        _artifactObjectStore = artifactObjectStore;
    }

    public async Task<TemporalExecutionAttemptOutcome> ExecuteAttemptAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RunId) || request.Generation < 1 || request.Attempt < 1)
            throw new InvalidOperationException("Temporal execution attempt identity is invalid.");

        var replayedOutcome = await _store.GetAttemptOutcomeAsync(request, ct);
        if (replayedOutcome is not null)
            return replayedOutcome;

        var durableRun = await _store.GetRunAsync(request.RunId, includeResult: true, ct)
            ?? throw new InvalidOperationException("Temporal execution attempt run was not found.");
        if (ExecutionRunStatuses.IsTerminal(durableRun.Status))
            return TerminalOutcome(durableRun.Status);
        if (durableRun.CancellationRequested)
            return await ProjectCancellationAsync(request, ct);

        var run = await _store.BeginAttemptAsync(
            request,
            CreateTrace(
                durableRun,
                request.Attempt,
                ExecutionEventTypes.RunStarted,
                ExecutionRunStatuses.Running,
                $"Execution attempt {request.Attempt} started."),
            ct);
        if (ExecutionRunStatuses.IsTerminal(run.Status))
            return TerminalOutcome(run.Status);
        if (run.CancellationRequested)
            return await ProjectCancellationAsync(request, ct);

        var handler = _handlerResolver.ResolveHandler(run.HandlerId);
        ExecutionRunResult result;
        if (handler is null)
        {
            result = new ExecutionRunResult
            {
                Status = ExecutionRunStatuses.Rejected,
                FailureClass = ExecutionFailureClasses.HandlerMissing,
                Error = $"Execution handler '{run.HandlerId}' is not registered in this Temporal worker."
            };
        }
        else if (!string.IsNullOrWhiteSpace(run.PluginId) &&
            !string.Equals(handler.Descriptor.PluginId, run.PluginId, StringComparison.Ordinal))
        {
            result = new ExecutionRunResult
            {
                Status = ExecutionRunStatuses.Rejected,
                FailureClass = ExecutionFailureClasses.PluginMismatch,
                Error = $"Execution handler '{run.HandlerId}' does not match durable plugin '{run.PluginId}'."
            };
        }
        else
        {
            try
            {
                var context = new TemporalExecutionRunContext(
                    _store,
                    _options,
                    _workerId,
                    request.Generation,
                    run,
                    ct,
                    _artifactObjectStore);
                result = await handler.ExecuteAsync(context, ct)
                    ?? throw new InvalidOperationException("Execution handler returned no result.");
            }
            catch (TemporalExecutionSuspendedException suspended)
            {
                return new TemporalExecutionAttemptOutcome
                {
                    Disposition = TemporalAttemptDispositions.Suspended,
                    WaitId = suspended.WaitId,
                    WaitKind = suspended.WaitKind,
                    ResumeAtUtc = suspended.ResumeAtUtc
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = ExecutionRunResult.Failed(
                    ExecutionFailureClasses.Unknown,
                    ExecutionContractValidator.BoundText(
                        ex.Message,
                        _options.Limits.MaxTraceMessageChars) ?? "Execution handler failed.");
            }
        }

        ExecutionContractValidator.ValidateRunResult(result, _options.Limits);
        var completion = await _store.CompleteAttemptAsync(
            run.Id,
            request.Generation,
            result,
            CreateTrace(
                run,
                run.Attempt,
                ExecutionEventTypes.RetryScheduled,
                ExecutionRunStatuses.Waiting,
                result.Error ?? "Execution retry scheduled.",
                result.StatusDetails),
            CreateTrace(
                run,
                run.Attempt,
                result.Status == ExecutionRunStatuses.Failed
                    ? ExecutionEventTypes.RunFailed
                    : ExecutionEventTypes.RunCompleted,
                result.Status,
                result.Error ?? $"Execution run {result.Status}.",
                result.StatusDetails,
                result.Status == ExecutionRunStatuses.Succeeded ? "info" : "warning"),
            ct);
        if (completion.RetryDelayMilliseconds.HasValue)
        {
            return new TemporalExecutionAttemptOutcome
            {
                Disposition = TemporalAttemptDispositions.Retryable,
                RetryDelayMilliseconds = completion.RetryDelayMilliseconds
            };
        }
        return TerminalOutcome(completion.Run.Status);
    }

    private async Task<TemporalExecutionAttemptOutcome> ProjectCancellationAsync(
        TemporalExecutionAttemptRequest request,
        CancellationToken ct)
    {
        await _store.ProjectCancellationAsync(new TemporalExecutionCancellation
        {
            RunId = request.RunId,
            Generation = request.Generation
        }, ct);
        return TerminalOutcome(ExecutionRunStatuses.Cancelled);
    }

    private static TemporalExecutionAttemptOutcome TerminalOutcome(string status) => new()
    {
        Disposition = status == ExecutionRunStatuses.Succeeded
            ? TemporalAttemptDispositions.Completed
            : TemporalAttemptDispositions.Terminal,
        TerminalStatus = status
    };

    private ExecutionTraceEvent CreateTrace(
        ExecutionRun run,
        int attempt,
        string type,
        string? status,
        string? message,
        JsonObject? details = null,
        string severity = "info") => TemporalExecutionRunContext.CreateTrace(
            run,
            attempt,
            type,
            status,
            message,
            details,
            severity,
            _options,
            _workerId);

    private sealed class TemporalExecutionRunContext : IExecutionRunContext
    {
        private readonly ITemporalExecutionRuntimeStore _store;
        private readonly TemporalExecutionOptions _options;
        private readonly string _workerId;
        private readonly int _generation;
        private readonly IObjectStore? _artifactObjectStore;

        public TemporalExecutionRunContext(
            ITemporalExecutionRuntimeStore store,
            TemporalExecutionOptions options,
            string workerId,
            int generation,
            ExecutionRun run,
            CancellationToken cancellationToken,
            IObjectStore? artifactObjectStore)
        {
            _store = store;
            _options = options;
            _workerId = workerId;
            _generation = generation;
            Run = Clone(run);
            CancellationToken = cancellationToken;
            _artifactObjectStore = artifactObjectStore;
        }

        public ExecutionRun Run { get; private set; }
        public CancellationToken CancellationToken { get; }

        public async Task<ExecutionRun> ReportAsync(
            ExecutionRunUpdate update,
            CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateRunUpdate(update, _options.Limits);
            if (!string.IsNullOrWhiteSpace(update.Status) && update.Status != ExecutionRunStatuses.Running)
            {
                throw new InvalidOperationException(
                    "Temporal handler progress may only report the running status; return a terminal result to complete the run.");
            }
            using var linked = Link(ct);
            Run = await _store.ReportRunAsync(
                Run.Id,
                _generation,
                update,
                Trace(
                    ExecutionEventTypes.RunStatus,
                    update.Status ?? Run.Status,
                    update.CurrentStep,
                    update.StatusDetails),
                linked.Token);
            return Clone(Run);
        }

        public async Task RecordEventAsync(
            string type,
            string? message = null,
            string severity = "info",
            JsonObject? details = null,
            CancellationToken ct = default)
        {
            var trace = Trace(type, Run.Status, message, details, severity);
            ExecutionContractValidator.ValidateTraceEvent(trace, _options.Limits);
            using var linked = Link(ct);
            await _store.RecordHistoryAsync(trace, linked.Token);
        }

        public async Task<ExecutionArtifact> PutArtifactAsync(
            ExecutionArtifactWrite artifact,
            CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateArtifactWrite(artifact, _options.Limits);
            var body = ArtifactBody(artifact);
            var persisted = new ExecutionArtifact
            {
                Id = OrderedId.CreateString(),
                RunId = Run.Id,
                Name = artifact.Name.Trim(),
                Kind = string.IsNullOrWhiteSpace(artifact.Kind) ? ExecutionArtifactKinds.Json : artifact.Kind.Trim(),
                MediaType = NormalizeOptional(artifact.MediaType),
                ContentHash = Sha256(body),
                SizeBytes = body.Length,
                Text = artifact.Text,
                Content = artifact.Content?.DeepClone(),
                Uri = NormalizeOptional(artifact.Uri),
                CreatedAtUtc = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>(artifact.Metadata, StringComparer.Ordinal)
            };
            using var linked = Link(ct);
            ObjectInfo? storedObject = null;
            if (persisted.Uri is null && body.Length > _options.Limits.MaxArtifactInlineBytes)
            {
                if (_artifactObjectStore is null)
                {
                    throw new InvalidOperationException(
                        $"Temporal artifacts larger than {_options.Limits.MaxArtifactInlineBytes} bytes require a configured durable artifact object store.");
                }
                var key = $"execution-artifacts/{persisted.RunId}/{persisted.Id}" +
                    (string.Equals(persisted.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal)
                        ? ".json"
                        : ".txt");
                await using var stream = new MemoryStream(body, writable: false);
                storedObject = await _artifactObjectStore.PutObjectAsync(new ObjectWriteRequest
                {
                    Container = _options.ArtifactObjectContainer,
                    Key = key,
                    Content = stream,
                    ContentType = persisted.MediaType ??
                        (string.Equals(persisted.Kind, ExecutionArtifactKinds.Json, StringComparison.Ordinal)
                            ? "application/json"
                            : "text/plain"),
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["execution_run_id"] = persisted.RunId,
                        ["execution_artifact_id"] = persisted.Id,
                        ["execution_content_hash"] = persisted.ContentHash
                    },
                    IfNoneMatch = "*"
                }, linked.Token);
                if (storedObject.ContentLength != persisted.SizeBytes)
                {
                    await DeleteStoredObjectAsync(storedObject, linked.Token);
                    throw new InvalidOperationException("Temporal artifact object-store write returned a different content length.");
                }
                persisted.Uri = $"vyral-object://{storedObject.Container}/{storedObject.Key}";
                persisted.Text = null;
                persisted.Content = null;
                AddArtifactMetadata(persisted.Metadata, "storage", "object-store");
                AddArtifactMetadata(persisted.Metadata, "offloaded", "true");
                AddArtifactMetadata(persisted.Metadata, "inline", "false");
            }
            else if (persisted.Uri is null)
            {
                AddArtifactMetadata(persisted.Metadata, "inline", "true");
            }

            try
            {
                var projected = await _store.PutArtifactMetadataAsync(
                    Run.Id,
                    _generation,
                    persisted,
                    Trace(
                        ExecutionEventTypes.ArtifactWritten,
                        Run.Status,
                        $"Artifact '{persisted.Name}' written."),
                    linked.Token);
                if (storedObject is not null &&
                    !string.Equals(projected.Id, persisted.Id, StringComparison.Ordinal))
                {
                    await DeleteStoredObjectAsync(storedObject, linked.Token);
                }
                return projected;
            }
            catch
            {
                if (storedObject is not null)
                {
                    await DeleteStoredObjectIgnoringFailureAsync(storedObject);
                }
                throw;
            }
        }

        public async Task<ExecutionCheckpoint> PutCheckpointAsync(
            ExecutionCheckpointWrite checkpoint,
            CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateCheckpointWrite(checkpoint, _options.Limits);
            var content = checkpoint.Content?.DeepClone() ?? new JsonObject();
            var persisted = new ExecutionCheckpoint
            {
                RunId = Run.Id,
                Key = checkpoint.Key.Trim(),
                ContentHash = Sha256(CanonicalJsonBytes(content)),
                UpdatedAtUtc = DateTime.UtcNow,
                Content = content,
                Metadata = new Dictionary<string, string>(checkpoint.Metadata, StringComparer.Ordinal)
            };
            using var linked = Link(ct);
            return await _store.PutCheckpointAsync(
                Run.Id,
                _generation,
                persisted,
                Trace(
                    ExecutionEventTypes.CheckpointWritten,
                    Run.Status,
                    $"Checkpoint '{persisted.Key}' written."),
                linked.Token);
        }

        public async Task<ExecutionCheckpoint?> GetCheckpointAsync(
            string key,
            CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateCheckpointWrite(new ExecutionCheckpointWrite
            {
                Key = key
            }, _options.Limits);
            using var linked = Link(ct);
            return await _store.GetCheckpointAsync(Run.Id, key.Trim(), linked.Token);
        }

        public async Task<ExecutionLease?> TryAcquireLeaseAsync(
            string leaseKey,
            double ttlSeconds = 60,
            JsonObject? metadata = null,
            CancellationToken ct = default)
        {
            var request = new ExecutionLeaseRequest
            {
                LeaseKey = leaseKey,
                OwnerId = Run.Id,
                RunId = Run.Id,
                TtlSeconds = ttlSeconds,
                Metadata = metadata?.DeepClone() as JsonObject
            };
            ExecutionContractValidator.ValidateLeaseRequest(request, _options.Limits);
            using var linked = Link(ct);
            var lease = await _store.TryAcquireLeaseAsync(request, linked.Token);
            if (lease is not null)
            {
                await _store.RecordHistoryAsync(
                    Trace(
                        ExecutionEventTypes.LeaseAcquired,
                        Run.Status,
                        $"Lease '{lease.LeaseKey}' acquired."),
                    linked.Token);
            }
            return lease;
        }

        public async Task<bool> ReleaseLeaseAsync(
            string leaseKey,
            CancellationToken ct = default)
        {
            var validation = new ExecutionLeaseRequest
            {
                LeaseKey = leaseKey,
                OwnerId = Run.Id,
                RunId = Run.Id,
                TtlSeconds = 1
            };
            ExecutionContractValidator.ValidateLeaseRequest(validation, _options.Limits);
            using var linked = Link(ct);
            var released = await _store.ReleaseLeaseAsync(leaseKey.Trim(), Run.Id, linked.Token);
            if (released)
            {
                await _store.RecordHistoryAsync(
                    Trace(
                        ExecutionEventTypes.LeaseReleased,
                        Run.Status,
                        $"Lease '{leaseKey.Trim()}' released."),
                    linked.Token);
            }
            return released;
        }

        public async Task<ExecutionTimer> ScheduleTimerAsync(
            string name,
            DateTime fireAtUtc,
            JsonNode? payload = null,
            CancellationToken ct = default)
        {
            var request = new ExecutionTimerRequest
            {
                Name = name,
                RunId = Run.Id,
                FireAtUtc = fireAtUtc,
                Payload = payload?.DeepClone()
            };
            ExecutionContractValidator.ValidateTimerRequest(request, _options.Limits);
            using var linked = Link(ct);
            return await _store.ScheduleTimerAsync(request, linked.Token);
        }

        public async Task<ExecutionExternalEvent> RaiseEventAsync(
            string name,
            JsonNode? payload = null,
            CancellationToken ct = default)
        {
            var request = new ExecutionExternalEventRequest
            {
                Name = name,
                RunId = Run.Id,
                Payload = payload?.DeepClone()
            };
            ExecutionContractValidator.ValidateExternalEventRequest(request, _options.Limits);
            var externalEvent = new ExecutionExternalEvent
            {
                Id = OrderedId.CreateString(),
                Name = name.Trim(),
                RunId = Run.Id,
                RaisedAtUtc = DateTime.UtcNow,
                Payload = payload?.DeepClone()
            };
            using var linked = Link(ct);
            var dispatch = await _store.CreateExternalEventWithPendingSignalAsync(
                externalEvent,
                OrderedId.CreateString(),
                linked.Token);
            return dispatch.Event;
        }

        public async Task<ExecutionWaitResult> WaitForExternalEventAsync(
            string name,
            DateTime? timeoutAtUtc = null,
            CancellationToken ct = default)
        {
            ExecutionContractValidator.ValidateExternalEventRequest(new ExecutionExternalEventRequest
            {
                Name = name,
                RunId = Run.Id
            }, _options.Limits);
            var normalizedName = name.Trim();
            var timeout = timeoutAtUtc?.ToUniversalTime();
            using var linked = Link(ct);
            var outcome = await _store.ConsumeWaitResultAsync(
                Run.Id,
                _generation,
                Run.Attempt,
                TemporalWaitKinds.ExternalEvent,
                normalizedName,
                linked.Token);
            if (outcome is not null)
            {
                await RecordWaitOutcomeAsync(outcome, linked.Token);
                return outcome;
            }
            if (timeout.HasValue && timeout.Value <= DateTime.UtcNow)
            {
                var timedOut = new ExecutionWaitResult
                {
                    Name = normalizedName,
                    Outcome = ExecutionWaitOutcomes.TimedOut
                };
                await RecordWaitOutcomeAsync(timedOut, linked.Token);
                return timedOut;
            }
            return await SuspendAsync(
                TemporalWaitKinds.ExternalEvent,
                normalizedName,
                timeout,
                linked.Token);
        }

        public async Task<ExecutionWaitResult> WaitForTimerAsync(
            string name,
            DateTime fireAtUtc,
            JsonNode? payload = null,
            CancellationToken ct = default)
        {
            var timerRequest = new ExecutionTimerRequest
            {
                Name = name,
                RunId = Run.Id,
                FireAtUtc = fireAtUtc,
                Payload = payload?.DeepClone()
            };
            ExecutionContractValidator.ValidateTimerRequest(timerRequest, _options.Limits);
            var normalizedName = name.Trim();
            var fireAt = fireAtUtc.ToUniversalTime();
            using var linked = Link(ct);
            var outcome = await _store.ConsumeWaitResultAsync(
                Run.Id,
                _generation,
                Run.Attempt,
                TemporalWaitKinds.Timer,
                normalizedName,
                linked.Token);
            if (outcome is not null)
            {
                await RecordWaitOutcomeAsync(outcome, linked.Token);
                return outcome;
            }

            var timer = await _store.ScheduleTimerAsync(timerRequest, linked.Token);
            if (fireAt <= DateTime.UtcNow)
            {
                var elapsed = new ExecutionWaitResult
                {
                    Name = normalizedName,
                    Outcome = ExecutionWaitOutcomes.Timer,
                    Timer = timer
                };
                await RecordWaitOutcomeAsync(elapsed, linked.Token);
                return elapsed;
            }
            return await SuspendAsync(
                TemporalWaitKinds.Timer,
                normalizedName,
                fireAt,
                linked.Token);
        }

        private async Task<ExecutionWaitResult> SuspendAsync(
            string kind,
            string name,
            DateTime? resumeAtUtc,
            CancellationToken ct)
        {
            var waitId = OrderedId.CreateString();
            await _store.RegisterWaitAsync(
                new TemporalProjectionWaitRegistration
                {
                    RunId = Run.Id,
                    Generation = _generation,
                    WaitId = waitId,
                    Kind = kind,
                    Name = name,
                    ResumeAtUtc = resumeAtUtc
                },
                Trace(
                    ExecutionEventTypes.WaitRegistered,
                    ExecutionRunStatuses.Waiting,
                    $"Waiting for {kind.Replace('_', ' ')} '{name}'."),
                ct);
            throw new TemporalExecutionSuspendedException(waitId, kind, resumeAtUtc);
        }

        private Task RecordWaitOutcomeAsync(ExecutionWaitResult result, CancellationToken ct) =>
            _store.RecordHistoryAsync(
                Trace(
                    result.Outcome == ExecutionWaitOutcomes.TimedOut
                        ? ExecutionEventTypes.WaitTimedOut
                        : ExecutionEventTypes.WaitResumed,
                    Run.Status,
                    result.Outcome == ExecutionWaitOutcomes.TimedOut
                        ? $"Wait for '{result.Name}' timed out."
                        : $"Wait for '{result.Name}' resumed."),
                ct);

        private ExecutionTraceEvent Trace(
            string type,
            string? status,
            string? message,
            JsonObject? details = null,
            string severity = "info") => CreateTrace(
                Run,
                Run.Attempt,
                type,
                status,
                message,
                details,
                severity,
                _options,
                _workerId);

        internal static ExecutionTraceEvent CreateTrace(
            ExecutionRun run,
            int attempt,
            string type,
            string? status,
            string? message,
            JsonObject? details,
            string severity,
            TemporalExecutionOptions options,
            string workerId)
        {
            var context = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runId"] = run.Id,
                ["correlationId"] = run.CorrelationId,
                ["handlerId"] = run.HandlerId,
                ["adapterId"] = options.AdapterId,
                ["runtimeKind"] = TemporalExecutionRuntimeKindIds.Temporal,
                ["workerId"] = workerId
            };
            if (!string.IsNullOrWhiteSpace(run.PluginId)) context["pluginId"] = run.PluginId;
            return new ExecutionTraceEvent
            {
                Id = OrderedId.CreateString(),
                SequenceId = OrderedId.CreateString(),
                RunId = run.Id,
                Type = type,
                TimestampUtc = DateTime.UtcNow,
                Attempt = attempt,
                Status = status,
                Severity = severity,
                Message = ExecutionContractValidator.BoundText(message, options.Limits.MaxTraceMessageChars),
                Details = details?.DeepClone() as JsonObject,
                Context = context
            };
        }

        private CancellationTokenSource Link(CancellationToken ct) => ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, ct)
            : CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        private static byte[] ArtifactBody(ExecutionArtifactWrite artifact)
        {
            if (artifact.Text is not null) return Encoding.UTF8.GetBytes(artifact.Text);
            if (artifact.Content is not null) return CanonicalJsonBytes(artifact.Content);
            return Encoding.UTF8.GetBytes(artifact.Uri ?? string.Empty);
        }

        private static byte[] CanonicalJsonBytes(JsonNode value)
        {
            using var document = JsonDocument.Parse(value.ToJsonString(ExecutionJson.Options));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(document.RootElement, writer);
            return stream.ToArray();
        }

        private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static string Sha256(ReadOnlySpan<byte> value) =>
            $"sha256:{Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()}";

        private void AddArtifactMetadata(Dictionary<string, string> metadata, string key, string value)
        {
            if (metadata.ContainsKey(key) || metadata.Count < _options.Limits.MaxTagCount)
                metadata[key] = value;
        }

        private Task DeleteStoredObjectAsync(ObjectInfo storedObject, CancellationToken ct) =>
            _artifactObjectStore!.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = storedObject.Container,
                Key = storedObject.Key,
                IfMatch = string.IsNullOrWhiteSpace(storedObject.Etag) ? null : storedObject.Etag
            }, ct);

        private async Task DeleteStoredObjectIgnoringFailureAsync(ObjectInfo storedObject)
        {
            try
            {
                await DeleteStoredObjectAsync(storedObject, CancellationToken.None);
            }
            catch
            {
                // The exact orphan is recoverable by its run-owned prefix; preserve the projection
                // exception and leave maintenance to remove it if the provider cleanup failed.
            }
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static T Clone<T>(T value) =>
            JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExecutionJson.Options), ExecutionJson.Options)
            ?? throw new InvalidOperationException($"Temporal execution context could not clone {typeof(T).Name}.");
    }

    private sealed class TemporalExecutionSuspendedException(
        string waitId,
        string waitKind,
        DateTime? resumeAtUtc) : Exception
    {
        public string WaitId { get; } = waitId;
        public string WaitKind { get; } = waitKind;
        public DateTime? ResumeAtUtc { get; } = resumeAtUtc;
    }
}
