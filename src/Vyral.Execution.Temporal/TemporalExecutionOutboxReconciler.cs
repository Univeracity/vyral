namespace Vyral.Execution.Temporal;

public sealed record TemporalExecutionOutboxReconcileResult
{
    public required DateTime CheckedAtUtc { get; init; }
    public required TemporalDispatchReconcileResult Starts { get; init; }
    public required TemporalSignalReconcileResult Signals { get; init; }
    public required TemporalCancellationReconcileResult Cancellations { get; init; }

    public int Examined => Starts.Examined + Signals.Examined + Cancellations.Examined;
    public int Delivered => Starts.Delivered + Signals.Delivered + Cancellations.Delivered;
    public int Failed => Starts.Failed + Signals.Failed + Cancellations.Failed;
}

/// <summary>
/// Drains every durable Temporal delivery plane with one provider-owned host operation. Calling
/// this method concurrently is safe when the projection implementation supplies claimed outbox
/// rows, as the PostgreSQL implementation does.
/// </summary>
public sealed class TemporalExecutionOutboxReconciler
{
    private readonly ITemporalExecutionRuntimeStore _store;
    private readonly ITemporalCoordinatorClient _client;
    private readonly TemporalExecutionOptions _options;

    public TemporalExecutionOutboxReconciler(
        ITemporalExecutionRuntimeStore store,
        ITemporalCoordinatorClient client,
        TemporalExecutionOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<TemporalExecutionOutboxReconcileResult> ReconcileAsync(
        int? limit = null,
        CancellationToken ct = default)
    {
        var batchSize = limit ?? _options.ReconciliationBatchSize;
        if (batchSize is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Reconciliation limit must be between 1 and 1000.");
        var starts = await new TemporalExecutionDispatchReconciler(
            _store,
            _client,
            _options.AdapterNamespace).ReconcileAsync(batchSize, ct);
        var signals = await new TemporalExecutionSignalReconciler(
            _store,
            _client,
            _options.AdapterNamespace).ReconcileAsync(batchSize, ct);
        var cancellations = await new TemporalExecutionCancellationReconciler(
            _store,
            _client,
            _options.AdapterNamespace).ReconcileAsync(batchSize, ct);
        return new TemporalExecutionOutboxReconcileResult
        {
            CheckedAtUtc = DateTime.UtcNow,
            Starts = starts,
            Signals = signals,
            Cancellations = cancellations
        };
    }
}
