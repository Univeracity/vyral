using Microsoft.AspNetCore.Http;
using Vyral.Execution;

namespace Vyral.Mcp;

/// <summary>
/// Host seam that keeps MCP execution authorization identical to the REST boundary.
/// </summary>
public interface IVyralMcpExecutionAuthorizer
{
    Task BindStartRunAsync(
        HttpContext context,
        ExecutionRunRequest request,
        CancellationToken cancellationToken = default);

    Task AuthorizeRunAsync(
        HttpContext context,
        ExecutionRun? run,
        string operation,
        CancellationToken cancellationToken = default);
}

public static class VyralMcpExecutionOperations
{
    public const string ReadRun = "read_run";
    public const string CancelRun = "cancel_run";
}
