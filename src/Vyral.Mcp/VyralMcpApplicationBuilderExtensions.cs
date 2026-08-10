using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Vyral.Mcp;

public static class VyralMcpApplicationBuilderExtensions
{
    /// <summary>
    /// Emits bounded metrics and trace tags for the MCP endpoint without recording arbitrary
    /// header values, task ids, principals, or request bodies.
    /// </summary>
    public static IApplicationBuilder UseVyralMcpTelemetry(
        this IApplicationBuilder app,
        VyralMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        return app.Use((context, next) =>
            context.Request.Path == new PathString(options.EndpointPath)
                ? VyralMcpTelemetry.ObserveAsync(context, next)
                : next(context));
    }

    /// <summary>
    /// Enforces Vyral's bounded MCP envelope before authentication, JSON parsing, or tool dispatch.
    /// Only names and aggregate sizes are inspected; header values are never logged.
    /// </summary>
    public static IApplicationBuilder UseVyralMcpRequestLimits(
        this IApplicationBuilder app,
        VyralMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path != new PathString(options.EndpointPath))
            {
                await next(context);
                return;
            }

            var headerCount = context.Request.Headers.Count;
            var headerBytes = context.Request.Headers.Sum(header =>
                Encoding.UTF8.GetByteCount(header.Key) +
                header.Value.Sum(value => Encoding.UTF8.GetByteCount(value ?? string.Empty)));
            if (headerCount > options.MaxHeaderCount || headerBytes > options.MaxHeaderBytes)
            {
                context.Response.StatusCode = StatusCodes.Status431RequestHeaderFieldsTooLarge;
                await Results.Problem(
                    title: "MCP request headers are too large",
                    detail: "Reduce the number or aggregate size of request headers.",
                    statusCode: StatusCodes.Status431RequestHeaderFieldsTooLarge).ExecuteAsync(context);
                return;
            }

            if (context.Request.ContentLength > options.MaxRequestBodyBytes)
            {
                await WritePayloadTooLargeAsync(context);
                return;
            }

            // Buffering is intentionally bounded. It makes the configured limit effective in
            // TestServer and behind hosts that do not expose IHttpMaxRequestBodySizeFeature.
            await using var boundedBody = new MemoryStream();
            var buffer = new byte[16 * 1024];
            long total = 0;
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (read == 0) break;
                total += read;
                if (total > options.MaxRequestBodyBytes)
                {
                    await WritePayloadTooLargeAsync(context);
                    return;
                }
                await boundedBody.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }

            boundedBody.Position = 0;
            context.Request.Body = boundedBody;
            context.Request.ContentLength = total;

            await next(context);
        });
    }

    /// <summary>
    /// Rejects untrusted Host and Origin values at the MCP endpoint. Loopback hosts and origins
    /// are accepted by default for local development; shared deployments must name every public
    /// host and browser origin explicitly in <see cref="VyralMcpOptions"/>.
    /// </summary>
    public static IApplicationBuilder UseVyralMcpDnsRebindingProtection(
        this IApplicationBuilder app,
        VyralMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path != new PathString(options.EndpointPath))
            {
                await next(context);
                return;
            }

            var hostValues = context.Request.Headers.Host;
            var host = context.Request.Host.Host.Trim('[', ']');
            if (hostValues.Count != 1 ||
                string.IsNullOrWhiteSpace(host) ||
                (!IsLoopbackHost(host) && !options.AllowedHosts.Any(allowed =>
                    string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase))))
            {
                await WriteForbiddenEndpointAsync(context);
                return;
            }

            var origins = context.Request.Headers.Origin;
            if (origins.Count > 1 ||
                (origins.Count == 1 && !IsAllowedOrigin(origins[0], options.AllowedOrigins)))
            {
                await WriteForbiddenEndpointAsync(context);
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Removes optional HTTP whitespace from MCP field values before routing validation.
    /// RFC 9110 requires field parsing to exclude leading and trailing SP/HTAB, but host
    /// runtime versions have not exposed parsed values consistently. Keeping this boundary
    /// explicit makes header-based MCP routing independent of the Kestrel patch version.
    /// </summary>
    public static IApplicationBuilder UseVyralMcpRoutingHeaderNormalization(
        this IApplicationBuilder app,
        VyralMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == new PathString(options.EndpointPath))
            {
                var mcpHeaderNames = context.Request.Headers.Keys
                    .Where(name => name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var headerName in mcpHeaderNames)
                {
                    var values = context.Request.Headers[headerName];
                    var normalizedValues = new string[values.Count];
                    for (var index = 0; index < values.Count; index++)
                    {
                        normalizedValues[index] = values[index]?.Trim(' ', '\t') ?? string.Empty;
                    }

                    context.Request.Headers[headerName] = normalizedValues;
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// Captures the already-bounded, already-authenticated request metadata needed by background
    /// task tools. Header values remain in memory only and are never emitted to logs.
    /// </summary>
    public static IApplicationBuilder UseVyralMcpRequestContext(
        this IApplicationBuilder app,
        VyralMcpOptions options,
        VyralMcpRequestContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(accessor);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path != new PathString(options.EndpointPath))
            {
                await next(context);
                return;
            }

            using (accessor.Push(context))
            {
                await next(context);
            }
        });
    }

    private static Task WritePayloadTooLargeAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return Results.Problem(
            title: "MCP request body is too large",
            detail: "Reduce the JSON-RPC request body size.",
            statusCode: StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
    }

    private static bool IsAllowedOrigin(string? value, IReadOnlySet<string> allowedOrigins)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        if (IsLoopbackHost(origin.Host)) return true;
        var normalized = origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return allowedOrigins.Any(allowed =>
            string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static Task WriteForbiddenEndpointAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Results.Problem(
            title: "MCP endpoint origin is not allowed",
            detail: "Configure an exact MCP host and browser origin for this deployment.",
            statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
    }

}
