namespace Vyral.Execution.Temporal.Postgres;

public sealed record PostgresTemporalProjectionStatus
{
    public required int SchemaVersion { get; init; }
    public required int PendingStartDispatches { get; init; }
    public required int PendingSignalDispatches { get; init; }
    public required int PendingCancellationDispatches { get; init; }
    public DateTime? OldestPendingDispatchAtUtc { get; init; }
    public required int ActiveRuns { get; init; }
    public required int ActiveCoordinators { get; init; }
}
