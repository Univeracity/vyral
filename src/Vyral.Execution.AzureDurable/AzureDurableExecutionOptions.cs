using Vyral.Execution;

namespace Vyral.Execution.AzureDurable;

public sealed class AzureDurableExecutionOptions
{
    public string AdapterId { get; init; } = "azure-durable";
    public ExecutionRuntimeLimits Limits { get; init; } = ExecutionRuntimeLimits.Default;
    public string TaskHubName { get; init; } = "VyralExecution";
    public string OrchestratorName { get; init; } = AzureDurableExecutionNames.Orchestrator;
    public string ActivityName { get; init; } = AzureDurableExecutionNames.Activity;
    public string StartActivityName { get; init; } = AzureDurableExecutionNames.StartActivity;
    public string StepActivityName { get; init; } = AzureDurableExecutionNames.StepActivity;
    public string ExternalEventName { get; init; } = AzureDurableExecutionNames.ExternalEvent;
    public string StatusStoreName { get; init; } = "runtime-status";
    public string ArtifactContainerName { get; init; } = "runtime-artifacts";
    public string WorkerId { get; init; } = Environment.MachineName;
    public int MaxActiveRuns { get; init; } = 1_000;
    public int DefaultListLimit { get; init; } = 100;
    public int MaxListLimit { get; init; } = 1_000;
}
