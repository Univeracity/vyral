using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Vyral.Execution.Temporal;

/// <summary>
/// Starts and identifies the internal Vyral coordinator without exposing Temporal failures to
/// dispatch persistence.
/// </summary>
public sealed class TemporalSdkCoordinatorClient : ITemporalCoordinatorClient
{
    private readonly ITemporalClient _client;
    private readonly string _taskQueue;

    public TemporalSdkCoordinatorClient(ITemporalClient client, string taskQueue)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(taskQueue) || taskQueue.Length > 255 || taskQueue.Any(char.IsControl))
            throw new InvalidOperationException("Temporal coordinator task queue must be 1-255 non-control characters.");
        _taskQueue = taskQueue;
    }

    public async Task<TemporalCoordinationReference> StartAsync(TemporalStartDispatch dispatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        try
        {
            var handle = await _client.StartWorkflowAsync<ITemporalRunCoordinatorWorkflow, TemporalCoordinatorResult>(
                workflow => workflow.RunAsync(new TemporalCoordinatorInput
                {
                    RunId = dispatch.RunId,
                    ProjectionRevision = dispatch.ProjectionRevision,
                    Generation = dispatch.Generation
                }),
                new WorkflowOptions(dispatch.WorkflowId, _taskQueue)
                {
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    Rpc = RpcOptions(ct)
                });

            return new TemporalCoordinationReference
            {
                WorkflowId = handle.Id,
                TemporalRunId = handle.ResultRunId,
                Generation = dispatch.Generation
            };
        }
        catch (WorkflowAlreadyStartedException ex)
        {
            throw new TemporalWorkflowAlreadyStartedException(ex);
        }
        catch (RpcTimeoutOrCanceledException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal start was cancelled.", ex, ct);
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Timeout, ex);
        }
        catch (RpcException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal start was cancelled.", ex, ct);
            throw Mapped(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unknown, ex);
        }
    }

    public async Task<TemporalWorkflowIdentity?> GetIdentityAsync(string workflowId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new ArgumentException("Temporal workflow id is required.", nameof(workflowId));
        try
        {
            var handle = _client.GetWorkflowHandle<ITemporalRunCoordinatorWorkflow>(workflowId);
            var identity = await handle.QueryAsync(
                workflow => workflow.GetIdentity(),
                new WorkflowQueryOptions { Rpc = RpcOptions(ct) });
            return new TemporalWorkflowIdentity { RunId = identity.RunId, Generation = identity.Generation };
        }
        catch (RpcTimeoutOrCanceledException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal identity query was cancelled.", ex, ct);
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Timeout, ex);
        }
        catch (RpcException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal identity query was cancelled.", ex, ct);
            throw Mapped(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unknown, ex);
        }
    }

    public async Task SignalExternalEventAsync(TemporalSignalDispatch dispatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        try
        {
            var handle = _client.GetWorkflowHandle<ITemporalRunCoordinatorWorkflow>(dispatch.WorkflowId);
            await handle.SignalAsync(
                workflow => workflow.NotifyExternalEventAsync(new TemporalCoordinatorSignal
                {
                    EventId = dispatch.EventId,
                    EventRevision = dispatch.EventRevision
                }),
                new WorkflowSignalOptions { Rpc = RpcOptions(ct) });
        }
        catch (RpcTimeoutOrCanceledException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal signal was cancelled.", ex, ct);
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Timeout, ex);
        }
        catch (RpcException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal signal was cancelled.", ex, ct);
            throw Mapped(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unknown, ex);
        }
    }

    public async Task RequestCancellationAsync(string workflowId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId) || workflowId.Any(char.IsControl))
            throw new ArgumentException("Temporal workflow id is required.", nameof(workflowId));
        try
        {
            var handle = _client.GetWorkflowHandle(workflowId);
            await handle.CancelAsync(new WorkflowCancelOptions { Rpc = RpcOptions(ct) });
        }
        catch (RpcTimeoutOrCanceledException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal cancellation was cancelled.", ex, ct);
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Timeout, ex);
        }
        catch (RpcException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Temporal cancellation was cancelled.", ex, ct);
            throw Mapped(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.Unknown, ex);
        }
    }

    private static RpcOptions RpcOptions(CancellationToken ct) => new() { CancellationToken = ct };

    private static TemporalCoordinatorClientException Mapped(RpcException exception) => new(
        exception.Code switch
        {
            RpcException.StatusCode.DeadlineExceeded => TemporalDispatchFailureClasses.Timeout,
            RpcException.StatusCode.PermissionDenied or RpcException.StatusCode.Unauthenticated =>
                TemporalDispatchFailureClasses.Authorization,
            RpcException.StatusCode.Unavailable or RpcException.StatusCode.ResourceExhausted =>
                TemporalDispatchFailureClasses.Unavailable,
            _ => TemporalDispatchFailureClasses.Unknown
        },
        exception);
}
