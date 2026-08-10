using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Server;

/// <summary>
/// Stages multipart bytes durably, then admits the cross-store artifact/record workflow to the
/// execution runtime. The staging write is not a published artifact and is safe to orphan/prune;
/// the execution run is the authoritative public admission boundary.
/// </summary>
public sealed class ExecutionRuntimeArtifactRecordIngestionAdapter
{
    public const string PluginId = "vyral.artifacts";
    public const string HandlerId = "vyral.artifacts.record-ingest";
    private const string StagingContainer = "vyral-admission-staging";

    private readonly IExecutionRuntime _runtime;
    private readonly IObjectStore _objects;
    private readonly ArtifactRecordIngestionOptions _options;

    public ExecutionRuntimeArtifactRecordIngestionAdapter(
        IExecutionRuntime runtime,
        IObjectStore objects,
        ArtifactRecordIngestionService ingestion,
        ArtifactRecordIngestionOptions options)
    {
        _runtime = runtime;
        _objects = objects;
        _options = options;
        _runtime.RegisterPlugin(new ArtifactRecordIngestionPlugin(objects, ingestion));
    }

    public async Task<ExecutionRun> StartAsync(
        ArtifactRecordIngestManifest manifest,
        Stream artifactContent,
        string? idempotencyKey,
        ExecutionScope? scope = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifactContent);
        ArtifactRecordIngestionService.ValidateManifest(manifest);
        var bytes = await ArtifactRecordIngestionService.ReadBoundedAsync(
            artifactContent,
            _options.MaxArtifactBytes,
            ct);
        var contentHash = ArtifactRecordIngestionService.ComputeSha256(bytes);
        var stagingKey = BuildStagingKey(idempotencyKey);
        var staged = await PutStagingObjectAsync(stagingKey, bytes, contentHash, ct);

        ExecutionRun run;
        try
        {
            run = await _runtime.StartRunAsync(new ExecutionRunRequest
            {
                HandlerId = HandlerId,
                PluginId = PluginId,
                Payload = JsonSerializer.SerializeToNode(new ArtifactRecordIngestionPayload
                {
                    Manifest = manifest,
                    StagingContainer = StagingContainer,
                    StagingKey = stagingKey,
                    StagingEtag = staged.Info.Etag,
                    ContentHash = contentHash
                }, ExecutionJson.Options),
                IdempotencyKey = idempotencyKey,
                Scope = scope,
                RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 3 },
                Tags =
                {
                    ["vyral.job"] = "artifact-record-ingestion",
                    ["vyral.admission.operation-id"] = VyralAdmissionOperations.IngestRecordArtifact
                }
            }, ct);
        }
        catch
        {
            if (staged.Created)
            {
                await TryDeleteStagingObjectAsync(stagingKey, staged.Info.Etag, CancellationToken.None);
            }

            throw;
        }

        if (run.AdmissionReplayed && staged.Created && ExecutionRunStatuses.IsTerminal(run.Status))
        {
            await TryDeleteStagingObjectAsync(stagingKey, staged.Info.Etag, ct);
        }

        return run;
    }

    private async Task<StagingWrite> PutStagingObjectAsync(
        string key,
        byte[] bytes,
        string contentHash,
        CancellationToken ct)
    {
        try
        {
            await using var content = new MemoryStream(bytes, writable: false);
            var info = await _objects.PutObjectAsync(new ObjectWriteRequest
            {
                Container = StagingContainer,
                Key = key,
                Content = content,
                ContentType = "application/octet-stream",
                IfNoneMatch = "*",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["admission_staging"] = "true",
                    ["content_hash"] = contentHash
                }
            }, ct);
            return new StagingWrite(info, Created: true);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("precondition", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await _objects.GetObjectAsync(new ObjectReadRequest
            {
                Container = StagingContainer,
                Key = key
            }, ct);
            if (existing is null ||
                !string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal))
            {
                if (existing is not null) await existing.Content.DisposeAsync();
                throw new InvalidOperationException(
                    "Idempotency-Key already has different staged artifact content.",
                    ex);
            }

            await using (existing.Content)
            {
                return new StagingWrite(new ObjectInfo
                {
                    Container = existing.Container,
                    Key = existing.Key,
                    ContentType = existing.ContentType,
                    ContentLength = existing.ContentLength,
                    Etag = existing.Etag,
                    ContentHash = existing.ContentHash,
                    Metadata = existing.Metadata,
                    UpdatedAt = existing.UpdatedAt
                }, Created: false);
            }
        }
    }

    private async Task TryDeleteStagingObjectAsync(string key, string? etag, CancellationToken ct)
    {
        try
        {
            await _objects.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = StagingContainer,
                Key = key,
                IfMatch = etag
            }, ct);
        }
        catch (InvalidOperationException)
        {
            // A replay or concurrent cleanup may already have removed the private staging object.
        }
    }

    private static string BuildStagingKey(string? idempotencyKey)
    {
        var identity = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"record-artifact/{identity}.bin";
    }

    private sealed record StagingWrite(ObjectInfo Info, bool Created);

    private sealed class ArtifactRecordIngestionPlugin : IExecutionPlugin
    {
        private readonly IReadOnlyList<IExecutionHandler> _handlers;

        public ArtifactRecordIngestionPlugin(
            IObjectStore objects,
            ArtifactRecordIngestionService ingestion)
        {
            _handlers = new[] { new ArtifactRecordIngestionHandler(objects, ingestion) };
        }

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Vyral artifact/record ingestion",
            Version = "1.0.0",
            Handlers =
            {
                new ExecutionHandlerDescriptor
                {
                    HandlerId = HandlerId,
                    PluginId = PluginId,
                    DisplayName = "Ingest an artifact and its record",
                    Description = "Completes a staged cross-store artifact/record ingestion.",
                    MaxAttempts = 3,
                    ConcurrencyKey = "vyral.artifacts.record-ingest"
                }
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers => _handlers;
    }

    private sealed class ArtifactRecordIngestionHandler : IExecutionHandler
    {
        private readonly IObjectStore _objects;
        private readonly ArtifactRecordIngestionService _ingestion;

        public ArtifactRecordIngestionHandler(
            IObjectStore objects,
            ArtifactRecordIngestionService ingestion)
        {
            _objects = objects;
            _ingestion = ingestion;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = new()
        {
            HandlerId = HandlerId,
            PluginId = PluginId,
            DisplayName = "Ingest an artifact and its record",
            MaxAttempts = 3,
            ConcurrencyKey = "vyral.artifacts.record-ingest"
        };

        public async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var payload = context.Run.Payload?.Deserialize<ArtifactRecordIngestionPayload>(ExecutionJson.Options)
                ?? throw new InvalidOperationException("Artifact record ingestion payload is required.");
            var staged = await _objects.GetObjectAsync(new ObjectReadRequest
            {
                Container = payload.StagingContainer,
                Key = payload.StagingKey
            }, ct) ?? throw new InvalidOperationException("Staged artifact content is missing.");

            ArtifactRecordIngestReceipt receipt;
            await using (staged.Content)
            {
                if (!string.Equals(staged.ContentHash, payload.ContentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Staged artifact content hash does not match its admission payload.");
                receipt = await _ingestion.IngestAsync(payload.Manifest, staged.Content, ct);
            }

            try
            {
                await _objects.DeleteObjectAsync(new ObjectDeleteRequest
                {
                    Container = payload.StagingContainer,
                    Key = payload.StagingKey,
                    IfMatch = staged.Etag
                }, ct);
            }
            catch (InvalidOperationException)
            {
                // Successful published work is authoritative; staging cleanup is best-effort.
            }

            return ExecutionRunResult.Succeeded(
                JsonSerializer.SerializeToNode(receipt, ExecutionJson.Options));
        }
    }

    private sealed class ArtifactRecordIngestionPayload
    {
        public ArtifactRecordIngestManifest Manifest { get; set; } = new();
        public string StagingContainer { get; set; } = string.Empty;
        public string StagingKey { get; set; } = string.Empty;
        public string? StagingEtag { get; set; }
        public string ContentHash { get; set; } = string.Empty;
    }
}
