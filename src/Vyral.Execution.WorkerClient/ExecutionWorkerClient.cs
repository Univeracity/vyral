using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vyral.Execution;

namespace Vyral.Execution.WorkerClient;

/// <summary>Obtains an HTTP bearer credential without exposing it to worker telemetry.</summary>
public interface IExecutionWorkerTokenSource
{
    Task<string> GetTokenAsync(CancellationToken ct = default);
}

/// <summary>Adapts a delegate into a token source.</summary>
public sealed class DelegateExecutionWorkerTokenSource(Func<CancellationToken, Task<string>> getToken) : IExecutionWorkerTokenSource
{
    private readonly Func<CancellationToken, Task<string>> _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));

    public Task<string> GetTokenAsync(CancellationToken ct = default) => _getToken(ct);
}

/// <summary>
/// Gets a Google identity token from the platform metadata server. This is optional transport
/// plumbing, not a Google execution contract: any <see cref="IExecutionWorkerTokenSource"/> can
/// authenticate the same portable HTTP worker protocol.
/// </summary>
public sealed class GoogleMetadataOidcTokenSource : IExecutionWorkerTokenSource
{
    public const string DefaultEndpoint = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity";
    private const int MaximumTokenBytes = 16 * 1024;

    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _audience;

    public GoogleMetadataOidcTokenSource(string audience, HttpClient? client = null, string? endpoint = null)
    {
        _audience = Require(audience, "Google metadata OIDC audience");
        if (!Uri.TryCreate(endpoint ?? DefaultEndpoint, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo) ||
            (parsed.Scheme != Uri.UriSchemeHttps && !string.Equals(parsed.Host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Google metadata OIDC endpoint must be an absolute URL without user credentials.");
        _endpoint = parsed;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var existingQuery = _endpoint.Query.TrimStart('?');
        var audienceQuery = "audience=" + Uri.EscapeDataString(_audience);
        var requestUri = new Uri(_endpoint.GetLeftPart(UriPartial.Path) + "?" +
            (string.IsNullOrEmpty(existingQuery) ? audienceQuery : existingQuery + "&" + audienceQuery), UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Google metadata OIDC endpoint returned HTTP {(int)response.StatusCode}.");
        var token = (await ReadBoundedTokenAsync(response.Content, ct)).Trim();
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("Google metadata OIDC endpoint returned an empty token.")
            : token;
    }

    private static async Task<string> ReadBoundedTokenAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumTokenBytes)
            throw new InvalidOperationException("Google metadata OIDC endpoint returned an oversized token response.");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(bytes.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + read > MaximumTokenBytes)
                throw new InvalidOperationException("Google metadata OIDC endpoint returned an oversized token response.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Require(string value, string description) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{description} is required.") : value.Trim();
}

/// <summary>Token-safe telemetry emitted after an HTTP attempt. It never contains request or response bodies, bearer credentials, or lease tokens.</summary>
public sealed class ExecutionWorkerClientTelemetry
{
    public string Operation { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public int? StatusCode { get; init; }
    /// <summary>A sanitized category, never a raw exception message or response content.</summary>
    public string? Error { get; init; }
}

/// <summary>Configuration for the supported external-worker HTTP client.</summary>
public sealed class ExecutionWorkerClientOptions
{
    public required Uri BaseUri { get; init; }
    public required string WorkerId { get; init; }
    public required IReadOnlyList<string> HandlerIds { get; init; }
    public IExecutionWorkerTokenSource? TokenSource { get; init; }
    /// <summary>
    /// Optional API key supplied in addition to the worker identity. This is useful when the
    /// Vyral API retains API-key defense in depth behind an identity-aware gateway.
    /// </summary>
    public string? ApiKey { get; init; }
    public string ApiKeyHeader { get; init; } = "X-Vyral-Api-Key";
    public Action<ExecutionWorkerClientTelemetry>? Observe { get; init; }
}

/// <summary>An unsuccessful Vyral response, deliberately redacted of response content and credentials.</summary>
public sealed class ExecutionWorkerClientException : InvalidOperationException
{
    public ExecutionWorkerClientException(string operation, string path, HttpStatusCode statusCode)
        : base($"Vyral {operation} {path} returned HTTP {(int)statusCode}.")
    {
        Operation = operation;
        Path = path;
        StatusCode = statusCode;
    }

    public string Operation { get; }
    public string Path { get; }
    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// Supported .NET client for Vyral's portable external-worker HTTP contract. Lease tokens remain
/// request-body values and are never placed in URLs, exception messages, or observer telemetry.
/// This client owns no provider queue consumer or product handler logic.
/// </summary>
public sealed class ExecutionWorkerClient : IExecutionWorkerTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly string _workerId;
    private readonly IReadOnlyList<string> _handlerIds;
    private readonly IExecutionWorkerTokenSource? _tokenSource;
    private readonly string? _apiKey;
    private readonly string? _apiKeyHeader;
    private readonly Action<ExecutionWorkerClientTelemetry>? _observe;

    public ExecutionWorkerClient(HttpClient client, ExecutionWorkerClientOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        if (!options.BaseUri.IsAbsoluteUri || !string.IsNullOrEmpty(options.BaseUri.UserInfo))
            throw new InvalidOperationException("Vyral server base URI must be absolute and must not contain user credentials.");
        _baseUri = options.BaseUri.ToString().EndsWith("/", StringComparison.Ordinal) ? options.BaseUri : new Uri(options.BaseUri + "/", UriKind.Absolute);
        _workerId = Require(options.WorkerId, "Vyral worker id");
        _handlerIds = options.HandlerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (_handlerIds.Count == 0) throw new InvalidOperationException("At least one Vyral worker handler id is required.");
        _tokenSource = options.TokenSource;
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey.Trim();
        _apiKeyHeader = _apiKey is null ? null : RequireHeaderName(options.ApiKeyHeader);
        _observe = options.Observe;
    }

    public async Task<ExecutionExternalWorkerLease?> LeaseNextAsync(string? runId = null, double ttlSeconds = 60, CancellationToken ct = default) =>
        await SendAsync<ExecutionExternalWorkerLease>("lease", "/execution/workers/leases", runId, new ExecutionExternalWorkerLeaseRequest
        {
            WorkerId = _workerId,
            HandlerIds = _handlerIds.ToList(),
            RunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim(),
            TtlSeconds = ttlSeconds
        }, allowNoContent: true, ct: ct);

    public Task<ExecutionExternalWorkerLease> HeartbeatAsync(ExecutionExternalWorkerLease lease, double ttlSeconds = 60, CancellationToken ct = default) =>
        SendRequiredAsync<ExecutionExternalWorkerLease>("heartbeat", "/execution/workers/leases/heartbeat", lease.Run.Id, new ExecutionExternalWorkerHeartbeatRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), TtlSeconds = ttlSeconds
        }, ct);

    public Task<ExecutionCheckpoint> CheckpointAsync(ExecutionExternalWorkerLease lease, ExecutionCheckpointWrite checkpoint, CancellationToken ct = default) =>
        SendRequiredAsync<ExecutionCheckpoint>("checkpoint", "/execution/workers/leases/checkpoints", lease.Run.Id, new ExecutionExternalWorkerCheckpointRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), Checkpoint = checkpoint
        }, ct);

    public async Task<ExecutionCheckpoint?> GetCheckpointAsync(ExecutionExternalWorkerLease lease, string key, CancellationToken ct = default) =>
        await SendAsync<ExecutionCheckpoint>("get-checkpoint", "/execution/workers/leases/checkpoints/read", lease.Run.Id, new ExecutionExternalWorkerCheckpointReadRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), Key = key
        }, allowNoContent: false, ct: ct, allowNotFound: true);

    public Task<ExecutionRun> ReportAsync(ExecutionExternalWorkerLease lease, ExecutionRunUpdate update, CancellationToken ct = default) =>
        SendRequiredAsync<ExecutionRun>("report", "/execution/workers/leases/reports", lease.Run.Id, new ExecutionExternalWorkerReportRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), Update = update
        }, ct);

    public async Task RecordEventAsync(ExecutionExternalWorkerLease lease, ExecutionExternalWorkerEventRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workerRequest = new ExecutionExternalWorkerEventRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Type = request.Type,
            Message = request.Message,
            Severity = request.Severity,
            Details = request.Details
        };
        _ = await SendAsync<object>("record-event", "/execution/workers/leases/events", lease.Run.Id, workerRequest, allowNoContent: true, ct: ct);
    }

    public Task<ExecutionArtifact> PutArtifactAsync(ExecutionExternalWorkerLease lease, ExecutionArtifactWrite artifact, CancellationToken ct = default) =>
        SendRequiredAsync<ExecutionArtifact>("put-artifact", "/execution/workers/leases/artifacts", lease.Run.Id, new ExecutionExternalWorkerArtifactRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), Artifact = artifact
        }, ct);

    public Task<ExecutionExternalWorkerWaitResponse> WaitAsync(ExecutionExternalWorkerLease lease, ExecutionExternalWorkerWaitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workerRequest = new ExecutionExternalWorkerWaitRequest
        {
            LeaseKey = lease.LeaseKey,
            LeaseToken = lease.LeaseToken,
            WorkerId = WorkerId(lease),
            Kind = request.Kind,
            Name = request.Name,
            TimeoutAtUtc = request.TimeoutAtUtc,
            FireAtUtc = request.FireAtUtc,
            Payload = request.Payload
        };
        return SendRequiredAsync<ExecutionExternalWorkerWaitResponse>("wait", "/execution/workers/leases/wait", lease.Run.Id, workerRequest, ct);
    }

    public Task<ExecutionRun> CompleteAsync(ExecutionExternalWorkerLease lease, ExecutionRunResult result, CancellationToken ct = default) =>
        SendRequiredAsync<ExecutionRun>("complete", "/execution/workers/leases/complete", lease.Run.Id, new ExecutionExternalWorkerCompletionRequest
        {
            LeaseKey = lease.LeaseKey, LeaseToken = lease.LeaseToken, WorkerId = WorkerId(lease), Result = result
        }, ct);

    private async Task<T> SendRequiredAsync<T>(string operation, string path, string? runId, object payload, CancellationToken ct) where T : class =>
        await SendAsync<T>(operation, path, runId, payload, allowNoContent: false, ct: ct)
            ?? throw new InvalidOperationException($"Vyral {operation} returned no response body.");

    private async Task<T?> SendAsync<T>(string operation, string path, string? runId, object payload, bool allowNoContent, CancellationToken ct, bool allowNotFound = false) where T : class
    {
        HttpStatusCode? statusCode = null;
        Exception? failure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path.TrimStart('/')))
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            if (_tokenSource is not null)
            {
                var token = (await _tokenSource.GetTokenAsync(ct)).Trim();
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Vyral worker token source returned an empty token.");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            if (_apiKey is not null)
            {
                request.Headers.Add(_apiKeyHeader!, _apiKey);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            statusCode = response.StatusCode;
            if (allowNoContent && response.StatusCode == HttpStatusCode.NoContent) return null;
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new ExecutionWorkerClientException(operation, path, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Observe(operation, path, runId, statusCode, failure);
        }
    }

    private string WorkerId(ExecutionExternalWorkerLease lease) => string.IsNullOrWhiteSpace(lease.WorkerId) ? _workerId : lease.WorkerId;
    private void Observe(string operation, string path, string? runId, HttpStatusCode? statusCode, Exception? error) =>
        _observe?.Invoke(new ExecutionWorkerClientTelemetry
        {
            Operation = operation,
            Path = path,
            RunId = runId,
            StatusCode = statusCode is null ? null : (int)statusCode.Value,
            Error = error switch
            {
                null => null,
                OperationCanceledException => "cancelled",
                ExecutionWorkerClientException => "http_failure",
                _ => "transport_failure"
            }
        });
    private static string Require(string value, string description) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{description} is required.") : value.Trim();

    private static string RequireHeaderName(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new InvalidOperationException("Vyral worker API-key header must contain only letters, digits, and hyphens.");
        }

        return candidate;
    }
}
