using System.Text.Json;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Execution;

namespace Vyral.Server;

/// <summary>
/// Admits collection lifecycle changes through the execution runtime. Some durable providers
/// provision or remove a collection across multiple remote resources, so these operations cannot
/// promise a portable synchronous transaction boundary.
/// </summary>
public sealed class ExecutionRuntimeCollectionManagementAdapter
{
    public const string PluginId = "vyral.collections";
    public const string CreateHandlerId = "vyral.collections.create";
    public const string DeleteHandlerId = "vyral.collections.delete";

    private readonly IExecutionRuntime _runtime;

    public ExecutionRuntimeCollectionManagementAdapter(
        IExecutionRuntime runtime,
        IRecordCollectionStore records)
    {
        _runtime = runtime;
        _runtime.RegisterPlugin(new CollectionManagementPlugin(records));
    }

    public Task<ExecutionRun> StartCreateAsync(
        RecordCollectionPolicy policy,
        string? idempotencyKey,
        ExecutionScope? scope = null,
        CancellationToken ct = default)
        => StartRunAsync(CreateCreateRunRequest(policy, idempotencyKey, scope), ct);

    public ExecutionRunRequest CreateCreateRunRequest(
        RecordCollectionPolicy policy,
        string? idempotencyKey,
        ExecutionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RecordIdentityValidator.ValidateCollectionName(policy.Name);
        return CreateRunRequest(
            CreateHandlerId,
            VyralAdmissionOperations.CreateCollection,
            new CollectionManagementPayload { Policy = policy },
            idempotencyKey,
            scope);
    }

    public Task<ExecutionRun> StartDeleteAsync(
        string collection,
        string? idempotencyKey,
        ExecutionScope? scope = null,
        CancellationToken ct = default)
        => StartRunAsync(CreateDeleteRunRequest(collection, idempotencyKey, scope), ct);

    public ExecutionRunRequest CreateDeleteRunRequest(
        string collection,
        string? idempotencyKey,
        ExecutionScope? scope = null)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        return CreateRunRequest(
            DeleteHandlerId,
            VyralAdmissionOperations.DeleteCollection,
            new CollectionManagementPayload { Collection = collection },
            idempotencyKey,
            scope);
    }

    public Task<ExecutionRun> StartRunAsync(
        ExecutionRunRequest request,
        CancellationToken ct = default) => _runtime.StartRunAsync(request, ct);

    private static ExecutionRunRequest CreateRunRequest(
        string handlerId,
        string operationId,
        CollectionManagementPayload payload,
        string? idempotencyKey,
        ExecutionScope? scope) =>
        new()
        {
            HandlerId = handlerId,
            PluginId = PluginId,
            Payload = JsonSerializer.SerializeToNode(payload, ExecutionJson.Options),
            IdempotencyKey = idempotencyKey,
            Scope = scope,
            RetryPolicy = new ExecutionRetryPolicy { MaxAttempts = 3 },
            Tags =
            {
                ["vyral.job"] = "collection-management",
                ["vyral.admission.operation-id"] = operationId
            }
        };

    private sealed class CollectionManagementPlugin : IExecutionPlugin
    {
        private readonly IReadOnlyList<IExecutionHandler> _handlers;

        public CollectionManagementPlugin(IRecordCollectionStore records)
        {
            _handlers =
            [
                new CreateCollectionHandler(records),
                new DeleteCollectionHandler(records)
            ];
        }

        public ExecutionPluginDescriptor Descriptor { get; } = new()
        {
            PluginId = PluginId,
            Name = "Vyral collection management",
            Version = "1.0.0",
            Handlers =
            {
                DescriptorFor(CreateHandlerId, "Create a record collection"),
                DescriptorFor(DeleteHandlerId, "Delete a record collection")
            }
        };

        public IReadOnlyList<IExecutionHandler> Handlers => _handlers;
    }

    private abstract class CollectionManagementHandler : IExecutionHandler
    {
        protected CollectionManagementHandler(IRecordCollectionStore records) => Records = records;

        protected IRecordCollectionStore Records { get; }
        public abstract ExecutionHandlerDescriptor Descriptor { get; }
        public abstract Task<ExecutionRunResult> ExecuteAsync(IExecutionRunContext context, CancellationToken ct = default);

        protected static CollectionManagementPayload Payload(IExecutionRunContext context) =>
            context.Run.Payload?.Deserialize<CollectionManagementPayload>(ExecutionJson.Options)
            ?? throw new InvalidOperationException("Collection management payload is required.");
    }

    private sealed class CreateCollectionHandler : CollectionManagementHandler
    {
        public CreateCollectionHandler(IRecordCollectionStore records) : base(records) { }

        public override ExecutionHandlerDescriptor Descriptor { get; } =
            DescriptorFor(CreateHandlerId, "Create a record collection");

        public override async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var policy = Payload(context).Policy
                ?? throw new InvalidOperationException("Collection policy is required.");
            await Records.CreateCollectionAsync(policy, ct);
            return ExecutionRunResult.Succeeded(JsonSerializer.SerializeToNode(policy, ExecutionJson.Options));
        }
    }

    private sealed class DeleteCollectionHandler : CollectionManagementHandler
    {
        public DeleteCollectionHandler(IRecordCollectionStore records) : base(records) { }

        public override ExecutionHandlerDescriptor Descriptor { get; } =
            DescriptorFor(DeleteHandlerId, "Delete a record collection");

        public override async Task<ExecutionRunResult> ExecuteAsync(
            IExecutionRunContext context,
            CancellationToken ct = default)
        {
            var collection = Payload(context).Collection;
            RecordIdentityValidator.ValidateCollectionName(collection);
            await Records.DeleteCollectionAsync(collection, ct);
            return ExecutionRunResult.Succeeded();
        }
    }

    private static ExecutionHandlerDescriptor DescriptorFor(string handlerId, string displayName) => new()
    {
        HandlerId = handlerId,
        PluginId = PluginId,
        DisplayName = displayName,
        MaxAttempts = 3,
        ConcurrencyKey = "vyral.collections.manage"
    };

    private sealed class CollectionManagementPayload
    {
        public string Collection { get; set; } = string.Empty;
        public RecordCollectionPolicy? Policy { get; set; }
    }
}
