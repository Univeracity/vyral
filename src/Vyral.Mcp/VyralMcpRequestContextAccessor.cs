using System.Threading;
using Microsoft.AspNetCore.Http;

namespace Vyral.Mcp;

/// <summary>
/// Carries an immutable-enough copy of bounded HTTP routing and identity headers into task
/// execution. Unlike <see cref="IHttpContextAccessor"/>, clearing the originating request does
/// not clear copies already captured by the task's execution context.
/// </summary>
public sealed class VyralMcpRequestContextAccessor
{
    private readonly AsyncLocal<HttpContext?> _current = new();

    public HttpContext? Current => _current.Value;

    internal IDisposable Push(HttpContext source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = _current.Value;
        _current.Value = CreateSnapshot(source);
        return new RestoreScope(this, previous);
    }

    private static HttpContext CreateSnapshot(HttpContext source)
    {
        var snapshot = new DefaultHttpContext();
        snapshot.TraceIdentifier = source.TraceIdentifier;
        snapshot.Request.Method = source.Request.Method;
        snapshot.Request.Scheme = source.Request.Scheme;
        snapshot.Request.Host = source.Request.Host;
        snapshot.Request.PathBase = source.Request.PathBase;
        snapshot.Request.Path = source.Request.Path;
        snapshot.Request.QueryString = source.Request.QueryString;
        snapshot.Connection.RemoteIpAddress = source.Connection.RemoteIpAddress;
        snapshot.User = source.User;
        foreach (var header in source.Request.Headers)
            snapshot.Request.Headers[header.Key] = header.Value.ToArray();
        return snapshot;
    }

    private sealed class RestoreScope(
        VyralMcpRequestContextAccessor owner,
        HttpContext? previous) : IDisposable
    {
        private VyralMcpRequestContextAccessor? _owner = owner;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is not null) currentOwner._current.Value = previous;
        }
    }
}
