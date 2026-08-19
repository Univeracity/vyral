using System.Text.Json;
using Vyral.Execution;
using Vyral.Execution.WorkerClient;
using Vyral.Google;
using Vyral.HostedWorker;
using Vyral.Server;

var builder = WebApplication.CreateBuilder(args);
var options = HostedWorkerOptions.FromConfiguration(builder.Configuration);
options.Validate();

var storageOptions = ServerStorageOptions.FromConfiguration(builder.Configuration);
var records = await ServerStorageFactory.CreateRecordStoreAsync(storageOptions);
var objects = ServerStorageFactory.CreateObjectStore(storageOptions);
var ingestionOptions = ArtifactRecordIngestionOptions.FromConfiguration(builder.Configuration);
var plugin = new ArtifactRecordIngestionHostedPlugin(
    objects,
    new ArtifactRecordIngestionService(records, objects, ingestionOptions));
var authentication = new HostedWorkerTaskAuthenticator(options.TaskAuthentication, builder.Environment);
var transport = new ExecutionWorkerClient(
    new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
    new ExecutionWorkerClientOptions
    {
        BaseUri = new Uri(options.VyralUrl, UriKind.Absolute),
        WorkerId = options.WorkerId,
        HandlerIds = options.HandlerIds,
        TokenSource = new GoogleMetadataOidcTokenSource(options.VyralUrl),
        ApiKey = options.ApiKey,
        ApiKeyHeader = options.ApiKeyHeader
    });
var worker = new ExecutionPluginWorker(
    transport,
    [plugin],
    new ExecutionPluginWorkerOptions
    {
        LeaseTtlSeconds = options.LeaseTtlSeconds,
        HeartbeatInterval = TimeSpan.FromSeconds(options.HeartbeatSeconds)
    });

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok", workerId = options.WorkerId, handlers = options.HandlerIds }));
app.MapPost(options.CallbackPath, async (HttpContext context, CancellationToken ct) =>
{
    if (!string.Equals(context.Request.Headers["X-Vyral-Execution-Dispatch"], "1", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Vyral execution dispatch header is required." });
    }

    if (!await authentication.IsAuthorizedAsync(context, ct))
    {
        return Results.Unauthorized();
    }

    var message = await JsonSerializer.DeserializeAsync<GoogleCloudExecutionDispatchMessage>(
        context.Request.Body,
        ExecutionJson.Options,
        ct);
    if (message is null || string.IsNullOrWhiteSpace(message.RunId))
    {
        return Results.BadRequest(new { error = "Vyral execution dispatch requires a run id." });
    }

    _ = await worker.RunOnceAsync(message.RunId, ct);
    return Results.NoContent();
});

await app.RunAsync();

internal sealed class HostedWorkerTaskAuthenticator
{
    private readonly HostedWorkerTaskAuthenticationOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly GoogleExecutionTokenValidator _google = new();

    public HostedWorkerTaskAuthenticator(HostedWorkerTaskAuthenticationOptions options, IHostEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    public async Task<bool> IsAuthorizedAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            var principal = string.Equals(_options.Mode, "google-oidc", StringComparison.Ordinal)
                ? await _google.ValidateAsync(GetBearerToken(context.Request) ?? string.Empty, _options.AllowedAudiences, ct)
                : GetDevelopmentPrincipal(context.Request);
            return _options.AllowedPrincipals.Contains(principal);
        }
        catch (ExecutionAccessDeniedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string GetDevelopmentPrincipal(HttpRequest request)
    {
        if (!_environment.IsDevelopment())
        {
            throw new ExecutionAccessDeniedException("Development-header worker authentication is disabled outside Development.");
        }
        var principal = request.Headers[_options.DevelopmentIdentityHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new ExecutionAccessDeniedException("A development worker identity header is required.");
        return principal;
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        var value = request.Headers["X-Serverless-Authorization"].ToString();
        if (string.IsNullOrWhiteSpace(value)) value = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..].Trim() : null;
    }
}
