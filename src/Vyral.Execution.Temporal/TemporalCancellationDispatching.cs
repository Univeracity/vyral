namespace Vyral.Execution.Temporal;

public sealed record TemporalCancellationDispatch
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required int AttemptCount { get; init; }
}

public interface ITemporalExecutionCancellationOutbox
{
    Task<IReadOnlyList<TemporalCancellationDispatch>> ListPendingCancellationsAsync(
        int limit,
        CancellationToken ct = default);

    Task MarkCancellationDeliveredAsync(string dispatchId, CancellationToken ct = default);
    Task RecordCancellationFailureAsync(
        string dispatchId,
        string failureClass,
        CancellationToken ct = default);
}

public sealed record TemporalCancellationReconcileItem
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? FailureClass { get; init; }
}

public sealed record TemporalCancellationReconcileResult
{
    public required DateTime CheckedAtUtc { get; init; }
    public required int Examined { get; init; }
    public required int Delivered { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<TemporalCancellationReconcileItem> Items { get; init; }
}

public sealed class TemporalExecutionCancellationReconciler
{
    private readonly ITemporalExecutionCancellationOutbox _outbox;
    private readonly ITemporalCoordinatorClient _client;
    private readonly string _adapterNamespace;

    public TemporalExecutionCancellationReconciler(
        ITemporalExecutionCancellationOutbox outbox,
        ITemporalCoordinatorClient client,
        string adapterNamespace)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _adapterNamespace = adapterNamespace ?? throw new ArgumentNullException(nameof(adapterNamespace));
        _ = TemporalExecutionIdentity.CreateWorkflowId(adapterNamespace, "validation");
    }

    public async Task<TemporalCancellationReconcileResult> ReconcileAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Reconciliation limit must be between 1 and 1000.");
        var pending = await _outbox.ListPendingCancellationsAsync(limit, ct);
        var items = new List<TemporalCancellationReconcileItem>(pending.Count);
        var delivered = 0;
        foreach (var dispatch in pending)
        {
            ct.ThrowIfCancellationRequested();
            ValidateDispatch(dispatch);
            try
            {
                await _client.RequestCancellationAsync(dispatch.WorkflowId, ct);
                await _outbox.MarkCancellationDeliveredAsync(dispatch.DispatchId, ct);
                delivered++;
                items.Add(Item(dispatch, "delivered"));
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
        return new TemporalCancellationReconcileResult
        {
            CheckedAtUtc = DateTime.UtcNow,
            Examined = items.Count,
            Delivered = delivered,
            Failed = items.Count - delivered,
            Items = items
        };
    }

    private void ValidateDispatch(TemporalCancellationDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (string.IsNullOrWhiteSpace(dispatch.DispatchId) || dispatch.DispatchId.Any(char.IsControl))
            throw new InvalidOperationException("Temporal cancellation dispatch id is required.");
        var expectedWorkflowId = TemporalExecutionIdentity.CreateWorkflowId(_adapterNamespace, dispatch.RunId);
        if (!string.Equals(expectedWorkflowId, dispatch.WorkflowId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Temporal cancellation workflow id does not match the durable Vyral run identity.");
        if (dispatch.Generation < 1 || dispatch.AttemptCount < 0)
            throw new InvalidOperationException("Temporal cancellation generation or delivery state is invalid.");
    }

    private async Task RecordFailureAsync(
        TemporalCancellationDispatch dispatch,
        string failureClass,
        List<TemporalCancellationReconcileItem> items,
        CancellationToken ct)
    {
        await _outbox.RecordCancellationFailureAsync(dispatch.DispatchId, failureClass, ct);
        items.Add(Item(dispatch, "failed", failureClass));
    }

    private static TemporalCancellationReconcileItem Item(
        TemporalCancellationDispatch dispatch,
        string status,
        string? failureClass = null) => new()
    {
        DispatchId = dispatch.DispatchId,
        RunId = dispatch.RunId,
        Status = status,
        FailureClass = failureClass
    };

    private static string NormalizeFailureClass(string value) => value switch
    {
        TemporalDispatchFailureClasses.IdentityConflict => TemporalDispatchFailureClasses.IdentityConflict,
        TemporalDispatchFailureClasses.Unavailable => TemporalDispatchFailureClasses.Unavailable,
        TemporalDispatchFailureClasses.Timeout => TemporalDispatchFailureClasses.Timeout,
        TemporalDispatchFailureClasses.Authorization => TemporalDispatchFailureClasses.Authorization,
        _ => TemporalDispatchFailureClasses.Unknown
    };
}
