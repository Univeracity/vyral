using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Server;

/// <summary>
/// Consumer-neutral implementation of the durable artifact/record publish workflow.
/// The same plugin may run in-process or through a Vyral hosted external worker.
/// </summary>
public sealed class ArtifactRecordIngestionHostedPlugin : IExecutionPlugin
{
    public const string PluginId = "vyral.artifacts";
    public const string HandlerId = "vyral.artifacts.record-ingest";
    public const string DefaultStagingContainer = "vyral-admission-staging";

    private readonly IReadOnlyList<IExecutionHandler> _handlers;

    public ArtifactRecordIngestionHostedPlugin(
        IObjectStore objects,
        ArtifactRecordIngestionService ingestion)
    {
        _handlers = [new ArtifactRecordIngestionHandler(objects, ingestion)];
    }

    public ExecutionPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = PluginId,
        Name = "Vyral artifact/record ingestion",
        Version = "1.0.0",
        Handlers = { CreateHandlerDescriptor() }
    };

    public IReadOnlyList<IExecutionHandler> Handlers => _handlers;

    public static ExecutionHandlerDescriptor CreateHandlerDescriptor() => new()
    {
        HandlerId = HandlerId,
        PluginId = PluginId,
        DisplayName = "Ingest an artifact and its record",
        Description = "Completes a staged cross-store artifact/record ingestion.",
        MaxAttempts = 3,
        ConcurrencyKey = HandlerId
    };

    private sealed class ArtifactRecordIngestionHandler : IExecutionHandler
    {
        private readonly IObjectStore _objects;
        private readonly ArtifactRecordIngestionService _ingestion;

        public ArtifactRecordIngestionHandler(IObjectStore objects, ArtifactRecordIngestionService ingestion)
        {
            _objects = objects;
            _ingestion = ingestion;
        }

        public ExecutionHandlerDescriptor Descriptor { get; } = CreateHandlerDescriptor();

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

            return ExecutionRunResult.Succeeded(JsonSerializer.SerializeToNode(receipt, ExecutionJson.Options));
        }
    }
}

internal sealed class ArtifactRecordIngestionPayload
{
    public ArtifactRecordIngestManifest Manifest { get; set; } = new();
    public string StagingContainer { get; set; } = string.Empty;
    public string StagingKey { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}
