namespace Vyral.Execution.Temporal;

public sealed record TemporalSignalDispatch
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string WorkflowId { get; init; }
    public required int Generation { get; init; }
    public required string EventId { get; init; }
    public required long EventRevision { get; init; }
    public required int AttemptCount { get; init; }
}

public interface ITemporalExecutionSignalOutbox
{
    Task<IReadOnlyList<TemporalSignalDispatch>> ListPendingSignalsAsync(int limit, CancellationToken ct = default);
    Task MarkSignalDeliveredAsync(string dispatchId, CancellationToken ct = default);
    Task RecordSignalFailureAsync(string dispatchId, string failureClass, CancellationToken ct = default);
}

public sealed record TemporalSignalReconcileItem
{
    public required string DispatchId { get; init; }
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public string? FailureClass { get; init; }
}

public sealed record TemporalSignalReconcileResult
{
    public required DateTime CheckedAtUtc { get; init; }
    public required int Examined { get; init; }
    public required int Delivered { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<TemporalSignalReconcileItem> Items { get; init; }
}

public sealed class TemporalExecutionSignalReconciler
{
    private readonly ITemporalExecutionSignalOutbox _outbox;
    private readonly ITemporalCoordinatorClient _client;
    private readonly string _adapterNamespace;

    public TemporalExecutionSignalReconciler(
        ITemporalExecutionSignalOutbox outbox,
        ITemporalCoordinatorClient client,
        string adapterNamespace)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _adapterNamespace = adapterNamespace ?? throw new ArgumentNullException(nameof(adapterNamespace));
        _ = TemporalExecutionIdentity.CreateWorkflowId(adapterNamespace, "validation");
    }

    public async Task<TemporalSignalReconcileResult> ReconcileAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Reconciliation limit must be between 1 and 1000.");
        var pending = await _outbox.ListPendingSignalsAsync(limit, ct);
        var items = new List<TemporalSignalReconcileItem>(pending.Count);
        var delivered = 0;

        foreach (var dispatch in pending)
        {
            ct.ThrowIfCancellationRequested();
            ValidateDispatch(dispatch);
            try
            {
                await _client.SignalExternalEventAsync(dispatch, ct);
                await _outbox.MarkSignalDeliveredAsync(dispatch.DispatchId, ct);
                delivered++;
                items.Add(new TemporalSignalReconcileItem
                {
                    DispatchId = dispatch.DispatchId,
                    RunId = dispatch.RunId,
                    Status = "delivered"
                });
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

        return new TemporalSignalReconcileResult
        {
            CheckedAtUtc = DateTime.UtcNow,
            Examined = items.Count,
            Delivered = delivered,
            Failed = items.Count - delivered,
            Items = items
        };
    }

    private void ValidateDispatch(TemporalSignalDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (string.IsNullOrWhiteSpace(dispatch.DispatchId) || dispatch.DispatchId.Any(char.IsControl))
            throw new InvalidOperationException("Temporal signal dispatch id is required.");
        var expectedWorkflowId = TemporalExecutionIdentity.CreateWorkflowId(_adapterNamespace, dispatch.RunId);
        if (!string.Equals(expectedWorkflowId, dispatch.WorkflowId, StringComparison.Ordinal))
            throw new InvalidOperationException("Temporal signal workflow id does not match the durable Vyral run identity.");
        if (dispatch.Generation < 1 || dispatch.EventRevision < 1 || dispatch.AttemptCount < 0 ||
            string.IsNullOrWhiteSpace(dispatch.EventId) || dispatch.EventId.Any(char.IsControl))
            throw new InvalidOperationException("Temporal signal event identity or delivery state is invalid.");
    }

    private async Task RecordFailureAsync(
        TemporalSignalDispatch dispatch,
        string failureClass,
        List<TemporalSignalReconcileItem> items,
        CancellationToken ct)
    {
        await _outbox.RecordSignalFailureAsync(dispatch.DispatchId, failureClass, ct);
        items.Add(new TemporalSignalReconcileItem
        {
            DispatchId = dispatch.DispatchId,
            RunId = dispatch.RunId,
            Status = "failed",
            FailureClass = failureClass
        });
    }

    private static string NormalizeFailureClass(string value) => value switch
    {
        TemporalDispatchFailureClasses.IdentityConflict => TemporalDispatchFailureClasses.IdentityConflict,
        TemporalDispatchFailureClasses.Unavailable => TemporalDispatchFailureClasses.Unavailable,
        TemporalDispatchFailureClasses.Timeout => TemporalDispatchFailureClasses.Timeout,
        TemporalDispatchFailureClasses.Authorization => TemporalDispatchFailureClasses.Authorization,
        _ => TemporalDispatchFailureClasses.Unknown
    };
}
