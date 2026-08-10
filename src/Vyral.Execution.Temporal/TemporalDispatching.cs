namespace Vyral.Execution.Temporal;

public sealed record TemporalStartDispatch
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string WorkflowId { get; init; }
    public required long ProjectionRevision { get; init; }
    public required int Generation { get; init; }
    public required int AttemptCount { get; init; }
}

public sealed record TemporalCoordinationReference
{
    public required string WorkflowId { get; init; }
    public string? TemporalRunId { get; init; }
    public required int Generation { get; init; }
}

public sealed record TemporalWorkflowIdentity
{
    public required string RunId { get; init; }
    public required int Generation { get; init; }
}

public interface ITemporalExecutionDispatchOutbox
{
    Task<IReadOnlyList<TemporalStartDispatch>> ListPendingStartsAsync(int limit, CancellationToken ct = default);
    Task MarkStartDeliveredAsync(string dispatchId, TemporalCoordinationReference reference, CancellationToken ct = default);
    Task RecordStartFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default);
}

public interface ITemporalCoordinatorClient
{
    Task<TemporalCoordinationReference> StartAsync(TemporalStartDispatch dispatch, CancellationToken ct = default);
    Task<TemporalWorkflowIdentity?> GetIdentityAsync(string workflowId, CancellationToken ct = default);
    Task SignalExternalEventAsync(TemporalSignalDispatch dispatch, CancellationToken ct = default);
    Task RequestCancellationAsync(string workflowId, CancellationToken ct = default);
}

public class TemporalCoordinatorClientException : Exception
{
    public TemporalCoordinatorClientException(string failureClass, Exception? innerException = null)
        : base("Temporal coordinator operation failed.", innerException)
    {
        if (string.IsNullOrWhiteSpace(failureClass)) throw new ArgumentException("Failure class is required.", nameof(failureClass));
        FailureClass = failureClass;
    }

    public string FailureClass { get; }
}

public sealed class TemporalWorkflowAlreadyStartedException : TemporalCoordinatorClientException
{
    public TemporalWorkflowAlreadyStartedException(Exception? innerException = null)
        : base(TemporalDispatchFailureClasses.AlreadyStarted, innerException)
    {
    }
}

public static class TemporalDispatchFailureClasses
{
    public const string AlreadyStarted = "already_started";
    public const string IdentityConflict = "identity_conflict";
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Authorization = "authorization";
    public const string Unknown = "unknown";
}

public sealed record TemporalDispatchReconcileItem
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? FailureClass { get; init; }
    public bool ReplayedExistingWorkflow { get; init; }
}

public sealed record TemporalDispatchReconcileResult
{
    public required DateTime CheckedAtUtc { get; init; }
    public required int Examined { get; init; }
    public required int Delivered { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<TemporalDispatchReconcileItem> Items { get; init; }
}

public sealed class TemporalExecutionDispatchReconciler
{
    private readonly ITemporalExecutionDispatchOutbox _outbox;
    private readonly ITemporalCoordinatorClient _client;
    private readonly string _adapterNamespace;

    public TemporalExecutionDispatchReconciler(
        ITemporalExecutionDispatchOutbox outbox,
        ITemporalCoordinatorClient client,
        string adapterNamespace)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _adapterNamespace = adapterNamespace ?? throw new ArgumentNullException(nameof(adapterNamespace));
        _ = TemporalExecutionIdentity.CreateWorkflowId(adapterNamespace, "validation");
    }

    public async Task<TemporalDispatchReconcileResult> ReconcileAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit), "Reconciliation limit must be between 1 and 1000.");
        var pending = await _outbox.ListPendingStartsAsync(limit, ct);
        var items = new List<TemporalDispatchReconcileItem>(pending.Count);
        var delivered = 0;

        foreach (var dispatch in pending)
        {
            ct.ThrowIfCancellationRequested();
            ValidateDispatch(dispatch);
            try
            {
                var reference = await _client.StartAsync(dispatch, ct);
                ValidateReference(dispatch, reference);
                await _outbox.MarkStartDeliveredAsync(dispatch.DispatchId, reference, ct);
                delivered++;
                items.Add(Succeeded(dispatch, replayed: false));
            }
            catch (TemporalWorkflowAlreadyStartedException)
            {
                try
                {
                    var identity = await _client.GetIdentityAsync(dispatch.WorkflowId, ct);
                    if (identity is not null &&
                        string.Equals(identity.RunId, dispatch.RunId, StringComparison.Ordinal) &&
                        identity.Generation == dispatch.Generation)
                    {
                        await _outbox.MarkStartDeliveredAsync(dispatch.DispatchId, new TemporalCoordinationReference
                        {
                            WorkflowId = dispatch.WorkflowId,
                            Generation = dispatch.Generation
                        }, ct);
                        delivered++;
                        items.Add(Succeeded(dispatch, replayed: true));
                    }
                    else
                    {
                        await RecordFailureAsync(dispatch, TemporalDispatchFailureClasses.IdentityConflict, items, ct);
                    }
                }
                catch (TemporalCoordinatorClientException ex)
                {
                    await RecordFailureAsync(dispatch, NormalizeFailureClass(ex.FailureClass), items, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    await RecordFailureAsync(dispatch, TemporalDispatchFailureClasses.Unknown, items, ct);
                }
            }
            catch (TemporalCoordinatorClientException ex)
            {
                await RecordFailureAsync(dispatch, NormalizeFailureClass(ex.FailureClass), items, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await RecordFailureAsync(dispatch, TemporalDispatchFailureClasses.Unknown, items, ct);
            }
        }

        return new TemporalDispatchReconcileResult
        {
            CheckedAtUtc = DateTime.UtcNow,
            Examined = items.Count,
            Delivered = delivered,
            Failed = items.Count - delivered,
            Items = items
        };
    }

    private void ValidateDispatch(TemporalStartDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (string.IsNullOrWhiteSpace(dispatch.DispatchId) || dispatch.DispatchId.Any(char.IsControl))
            throw new InvalidOperationException("Temporal dispatch id is required.");
        var expectedWorkflowId = TemporalExecutionIdentity.CreateWorkflowId(_adapterNamespace, dispatch.RunId);
        if (!string.Equals(expectedWorkflowId, dispatch.WorkflowId, StringComparison.Ordinal))
            throw new InvalidOperationException("Temporal workflow id does not match the durable Vyral run identity.");
        if (dispatch.Generation < 1 || dispatch.ProjectionRevision < 1 || dispatch.AttemptCount < 0)
            throw new InvalidOperationException("Temporal dispatch generation, projection revision, or attempt count is invalid.");
    }

    private static void ValidateReference(TemporalStartDispatch dispatch, TemporalCoordinationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(dispatch.WorkflowId, reference.WorkflowId, StringComparison.Ordinal) || dispatch.Generation != reference.Generation)
            throw new TemporalCoordinatorClientException(TemporalDispatchFailureClasses.IdentityConflict);
    }

    private async Task RecordFailureAsync(
        TemporalStartDispatch dispatch,
        string failureClass,
        List<TemporalDispatchReconcileItem> items,
        CancellationToken ct)
    {
        await _outbox.RecordStartFailureAsync(dispatch.DispatchId, failureClass, ct);
        items.Add(new TemporalDispatchReconcileItem
        {
            DispatchId = dispatch.DispatchId,
            RunId = dispatch.RunId,
            Status = "failed",
            FailureClass = failureClass
        });
    }

    private static TemporalDispatchReconcileItem Succeeded(TemporalStartDispatch dispatch, bool replayed) => new()
    {
        DispatchId = dispatch.DispatchId,
        RunId = dispatch.RunId,
        Status = "delivered",
        ReplayedExistingWorkflow = replayed
    };

    private static string NormalizeFailureClass(string value) => value switch
    {
        TemporalDispatchFailureClasses.AlreadyStarted => TemporalDispatchFailureClasses.AlreadyStarted,
        TemporalDispatchFailureClasses.IdentityConflict => TemporalDispatchFailureClasses.IdentityConflict,
        TemporalDispatchFailureClasses.Unavailable => TemporalDispatchFailureClasses.Unavailable,
        TemporalDispatchFailureClasses.Timeout => TemporalDispatchFailureClasses.Timeout,
        TemporalDispatchFailureClasses.Authorization => TemporalDispatchFailureClasses.Authorization,
        _ => TemporalDispatchFailureClasses.Unknown
    };
}
