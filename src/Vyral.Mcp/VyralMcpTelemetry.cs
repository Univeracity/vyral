using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;

namespace Vyral.Mcp;

/// <summary>
/// Low-cardinality transport telemetry for the stateless MCP boundary. No caller-provided
/// parameter value, task id, principal, credential, or unrecognized method/name is emitted.
/// </summary>
public static class VyralMcpTelemetry
{
    public const string MeterName = "Vyral.Mcp";
    public const string RequestCountInstrumentName = "vyral.mcp.requests";
    public const string RequestDurationInstrumentName = "vyral.mcp.request.duration";

    private static readonly Meter Meter = new(MeterName, "0.3.0");
    private static readonly Counter<long> RequestCount = Meter.CreateCounter<long>(
        RequestCountInstrumentName,
        unit: "{request}",
        description: "Stateless MCP HTTP requests by bounded protocol and catalog dimensions.");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        RequestDurationInstrumentName,
        unit: "ms",
        description: "Stateless MCP HTTP request duration.");

    private static readonly IReadOnlySet<string> KnownMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "server/discover",
        "tools/list",
        "tools/call",
        "resources/list",
        "resources/templates/list",
        "resources/read",
        "prompts/list",
        "prompts/get",
        "tasks/get",
        "tasks/update",
        "tasks/cancel",
        "subscriptions/listen"
    };

    internal static async Task ObserveAsync(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        var methodCandidate = context.Request.Headers["Mcp-Method"].FirstOrDefault()?.Trim();
        var method = methodCandidate is not null && KnownMethods.Contains(methodCandidate)
            ? methodCandidate
            : "unknown";
        var nameCandidate = context.Request.Headers["Mcp-Name"].FirstOrDefault()?.Trim();
        VyralMcpCatalogEntry? entry = null;
        if (!string.IsNullOrEmpty(nameCandidate) &&
            VyralMcpCatalog.TryGetByMcpId(nameCandidate, out var catalogEntry))
            entry = catalogEntry;

        var operation = entry?.OperationId ?? (string.IsNullOrEmpty(nameCandidate) ? "protocol" : "unknown");
        var authorizationClass = entry?.AuthorizationClass ?? "protocol";
        var capability = entry?.Exposure ?? "protocol";

        Activity.Current?.SetTag("mcp.method", method);
        Activity.Current?.SetTag("vyral.operation.id", operation);
        Activity.Current?.SetTag("vyral.authorization.class", authorizationClass);
        Activity.Current?.SetTag("mcp.capability", capability);

        try
        {
            await next(context);
        }
        finally
        {
            var outcome = context.Response.StatusCode switch
            {
                < 400 => "success",
                < 500 => "client_error",
                _ => "server_error"
            };
            var tags = new TagList
            {
                { "mcp.method", method },
                { "vyral.operation.id", operation },
                { "vyral.authorization.class", authorizationClass },
                { "mcp.capability", capability },
                { "outcome", outcome }
            };
            RequestCount.Add(1, tags);
            RequestDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            Activity.Current?.SetTag("mcp.outcome", outcome);
        }
    }
}
