using Vyral.Execution;

namespace Vyral.Execution.Local;

public sealed class LocalExecutionRuntimeOptions
{
    public required string DatabasePath { get; init; }
    public string AdapterId { get; init; } = "local-sqlite";
    public ExecutionRuntimeLimits Limits { get; init; } = ExecutionRuntimeLimits.Default;
    public int MaxActiveRuns { get; init; } = 100;
    public int MaxRetainedTerminalRuns { get; init; } = 500;
    public int DefaultListLimit { get; init; } = 50;
    public int MaxListLimit { get; init; } = 200;
    public int ConcurrencyRetryDelayMs { get; init; } = 100;
    public string WorkerId { get; init; } = Environment.MachineName;
    public string? ArtifactDirectory { get; init; }
    public int BusyTimeoutMs { get; init; } = 5_000;
    public IReadOnlyList<ExecutionProductPolicy> ProductPolicies { get; init; } = Array.Empty<ExecutionProductPolicy>();
}
