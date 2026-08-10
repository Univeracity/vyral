namespace Vyral.Execution.Local;

/// <summary>
/// Execution-boundary policy for one product. Empty allowlists mean no additional restriction;
/// configuring any product policy makes scoped product and tenant identity mandatory for new runs.
/// </summary>
/// <remarks>
/// Use <see cref="ExecutionProductPolicy"/> for new hosts. This compatibility type remains so
/// existing local-host configuration code can migrate without a source break.
/// </remarks>
public sealed class LocalExecutionProductPolicy : ExecutionProductPolicy
{
}
