using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Embeddings.Onnx;
using Vyral.Execution;
using Vyral.Execution.Aws;
using Vyral.Execution.Local;
using Vyral.Google;
using Vyral.Cloudflare;
using Vyral.Local;
using Vyral.Pgvector;
using Vyral.MySql;
using Vyral.Mcp;
using Vyral.Providers.Abstractions;
using Vyral.Providers.Cli;
using Vyral.Providers.Jules;
using Vyral.Providers.Local;
using Vyral.Providers.Onnx;
using Vyral.Server;
using CloudTasksClient = Google.Cloud.Tasks.V2.CloudTasksClient;

var builder = WebApplication.CreateBuilder(args);

// Register services
var startupOverall = LogStartupPhaseStarting("server.startup", $"environment={builder.Environment.EnvironmentName}");
var startupPhase = LogStartupPhaseStarting("configuration.paths");
var storageOptions = ServerStorageOptions.FromConfiguration(builder.Configuration);
var dbPath = storageOptions.DatabasePath;
var objectsPath = storageOptions.ObjectsPath;
LogStartupPhaseCompleted("configuration.paths", startupPhase, storageOptions.Describe());

startupPhase = LogStartupPhaseStarting("embedding.provider", $"provider={builder.Configuration["Embedding:Provider"] ?? "local-token-hash"}");
var embeddingRegistry = new EmbeddingProviderRegistry(LocalEmbeddingProviders.CreateFactories().Concat(OnnxEmbeddingProviders.CreateFactories()));
var embeddingOptions = embeddingRegistry.ResolveOptions(GetEmbeddingProviderOptions(builder.Configuration));
var embeddingProvider = embeddingRegistry.Create(embeddingOptions);
LogStartupPhaseCompleted("embedding.provider", startupPhase, $"provider={embeddingProvider.ProviderId}; model={embeddingProvider.ModelId}; dimensions={embeddingProvider.Dimensions}");

startupPhase = LogStartupPhaseStarting("record_store", $"backend={storageOptions.RecordStore}");
var store = await CreateRecordStoreAsync(storageOptions);
LogStartupPhaseCompleted("record_store", startupPhase, $"backend={storageOptions.RecordStore}; store={store.GetType().Name}");

startupPhase = LogStartupPhaseStarting("trace_store", $"backend={storageOptions.TraceStore}");
var traceStore = await CreateTraceStoreAsync(storageOptions);
LogStartupPhaseCompleted("trace_store", startupPhase, $"backend={storageOptions.TraceStore}; store={traceStore.GetType().Name}");

startupPhase = LogStartupPhaseStarting("object_store", $"backend={storageOptions.ObjectStore}");
var objectStore = CreateObjectStore(storageOptions);
LogStartupPhaseCompleted("object_store", startupPhase, $"backend={storageOptions.ObjectStore}; store={objectStore.GetType().Name}");

startupPhase = LogStartupPhaseStarting("canonical_store");
var canonicalStoreOptions = CanonicalStoreOptions.FromConfiguration(builder.Configuration, dbPath);
ICanonicalStore? canonicalStore = canonicalStoreOptions.Enabled ? CreateCanonicalStore(canonicalStoreOptions) : null;
LogStartupPhaseCompleted("canonical_store", startupPhase, canonicalStore is null
    ? canonicalStoreOptions.Describe()
    : $"store={canonicalStore.GetType().Name}; {canonicalStoreOptions.Describe()}");

startupPhase = LogStartupPhaseStarting("server.options");
var allowedCorsOrigins = GetConfiguredCorsOrigins(builder.Configuration);
var providerTargetRegistry = CreateProviderTargetRegistry(builder.Configuration);
var accessOptions = ServerAccessOptions.FromConfiguration(builder.Configuration);
var executionAccessOptions = VyralExecutionAccessOptions.FromConfiguration(builder.Configuration);
var canonicalAccessOptions = canonicalStoreOptions.Enabled ? VyralCanonicalAccessOptions.FromConfiguration(builder.Configuration) : null;
var canonicalRateLimitOptions = canonicalStoreOptions.Enabled ? CanonicalRateLimitOptions.FromConfiguration(builder.Configuration) : null;
var executionAccess = new VyralExecutionAccess(
    executionAccessOptions,
    builder.Environment,
    CreateExecutionIdentityAuthenticators(builder.Configuration));
var canonicalAccess = canonicalAccessOptions is null ? null : new VyralCanonicalAccess(
    canonicalAccessOptions,
    builder.Environment,
    CreateCanonicalIdentityAuthenticators(builder.Configuration));
ValidateExecutionAccessRuntimePolicies(builder.Configuration, executionAccessOptions);
var providerRunGuard = new ProviderRunGuard(ProviderRunGuardOptions.FromConfiguration(builder.Configuration));
var providerRunJobOptions = ProviderRunJobStoreOptions.FromConfiguration(builder.Configuration);
var retrievalEvaluationJobOptions = RetrievalEvaluationJobStoreOptions.FromConfiguration(builder.Configuration);
var artifactRecordIngestionOptions = ArtifactRecordIngestionOptions.FromConfiguration(builder.Configuration);
var mcpOptions = VyralMcpOptions.FromConfiguration(builder.Configuration);
var mcpRequestContextAccessor = new VyralMcpRequestContextAccessor();
if (mcpOptions.ConformanceMode && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Mcp:ConformanceMode can only be enabled in the Development environment.");
LogStartupPhaseCompleted("server.options", startupPhase, $"providers={providerTargetRegistry.GetProfiles().Count}; corsOrigins={allowedCorsOrigins.Length}");

startupPhase = LogStartupPhaseStarting("execution.runtime", $"adapter={builder.Configuration["ExecutionRuntime:Adapter"] ?? "local-sqlite"}");
var executionRuntime = CreateExecutionRuntime(builder.Configuration, storageOptions, dbPath, objectStore);
var externalExecutionRuntime = executionRuntime as IExternalExecutionWorkerRuntime;
var configuredExternalHandlers = GetExternalExecutionHandlers(builder.Configuration);
if (externalExecutionRuntime is null && configuredExternalHandlers.Count > 0)
{
    throw new InvalidOperationException("ExecutionRuntime:ExternalHandlers requires an adapter that implements IExternalExecutionWorkerRuntime.");
}
if (externalExecutionRuntime is not null)
{
    foreach (var handler in configuredExternalHandlers)
    {
        externalExecutionRuntime.RegisterExternalHandler(handler);
    }
}
var embeddingJobAdapter = new ExecutionRuntimeEmbeddingJobAdapter(executionRuntime, embeddingProvider, embeddingOptions);
LogStartupPhaseCompleted("execution.runtime", startupPhase, $"adapter={executionRuntime.Adapter.AdapterId}; handlers={executionRuntime.ListHandlers().Count}");

startupPhase = LogStartupPhaseStarting("service.registration");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddSingleton<IRecordCollectionStore>(store);
if (store is SqliteRecordCollectionStore sqliteRecordStore)
{
    builder.Services.AddSingleton(sqliteRecordStore);
}
builder.Services.AddSingleton<ITraceStore>(traceStore);
builder.Services.AddSingleton<IObjectStore>(objectStore);
if (canonicalStore is not null && canonicalAccessOptions is not null && canonicalAccess is not null && canonicalRateLimitOptions is not null)
{
    builder.Services.AddSingleton<ICanonicalStore>(canonicalStore);
    builder.Services.AddSingleton(canonicalStoreOptions);
    builder.Services.AddSingleton(canonicalRateLimitOptions);
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!context.Request.Path.StartsWithSegments("/canonical")) return RateLimitPartition.GetNoLimiter("non-canonical");
            var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(source, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = canonicalRateLimitOptions.PermitLimit,
                Window = TimeSpan.FromSeconds(canonicalRateLimitOptions.WindowSeconds),
                QueueLimit = canonicalRateLimitOptions.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
        });
    });
}
builder.Services.AddSingleton(storageOptions);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    // The artifact itself is a multipart file part; allow a small manifest and
    // MIME framing allowance without making the HTTP contract product-specific.
    options.MultipartBodyLengthLimit = artifactRecordIngestionOptions.MaxArtifactBytes + (256 * 1024);
});
builder.Services.AddSingleton(artifactRecordIngestionOptions);
builder.Services.AddSingleton<ArtifactRecordIngestionService>();
builder.Services.AddSingleton(embeddingOptions);
builder.Services.AddSingleton(embeddingRegistry);
builder.Services.AddSingleton(embeddingProvider);
builder.Services.AddSingleton<IRerankingService, ProviderBackedRerankingService>();
builder.Services.AddSingleton<IRetrievalService, LocalRetrievalService>();
builder.Services.AddSingleton<IRetrievalEvaluationService, LocalRetrievalEvaluationService>();
builder.Services.AddSingleton<ICollectionInspectionService, LocalCollectionInspectionService>();
builder.Services.AddSingleton<IRagContextService, LocalRagContextService>();
builder.Services.AddSingleton<IRagPromptService, LocalRagPromptService>();
builder.Services.AddSingleton<IRagIngestionService, LocalRagIngestionService>();
builder.Services.AddSingleton(providerTargetRegistry);
builder.Services.AddSingleton(accessOptions);
builder.Services.AddSingleton(executionAccessOptions);
builder.Services.AddSingleton(executionAccess);
builder.Services.AddSingleton<IVyralMcpExecutionAuthorizer>(executionAccess);
builder.Services.AddSingleton(mcpRequestContextAccessor);
if (canonicalAccessOptions is not null && canonicalAccess is not null)
{
    builder.Services.AddSingleton(canonicalAccessOptions);
    builder.Services.AddSingleton(canonicalAccess);
}
builder.Services.AddSingleton(providerRunGuard);
builder.Services.AddSingleton(providerRunJobOptions);
builder.Services.AddSingleton<IExecutionRuntimeAdapter>(executionRuntime);
builder.Services.AddSingleton<IExecutionRuntime>(executionRuntime);
if (externalExecutionRuntime is not null)
{
    builder.Services.AddSingleton<IExternalExecutionWorkerRuntime>(externalExecutionRuntime);
}
if (executionRuntime is LocalExecutionRuntime localExecutionRuntime)
{
    builder.Services.AddSingleton(localExecutionRuntime);
}
builder.Services.AddSingleton(embeddingJobAdapter);
builder.Services.AddSingleton<ExecutionRuntimeArtifactRecordIngestionAdapter>();
builder.Services.AddSingleton<ExecutionRuntimeCollectionManagementAdapter>();
builder.Services.AddSingleton<ExecutionRuntimeRagIngestionJobAdapter>(services =>
    new ExecutionRuntimeRagIngestionJobAdapter(
        services.GetRequiredService<IExecutionRuntime>(),
        services.GetRequiredService<IRagIngestionService>()));
builder.Services.AddSingleton<ExecutionRuntimeGraphJobAdapter>(services =>
    new ExecutionRuntimeGraphJobAdapter(
        services.GetRequiredService<IExecutionRuntime>(),
        services.GetRequiredService<IRecordCollectionStore>()));
builder.Services.AddSingleton<ExecutionRuntimeRecordImportJobAdapter>(services =>
    new ExecutionRuntimeRecordImportJobAdapter(
        services.GetRequiredService<IExecutionRuntime>(),
        services.GetRequiredService<IRecordCollectionStore>()));
builder.Services.AddSingleton<ExecutionRuntimeProviderRunJobAdapter>(services =>
    new ExecutionRuntimeProviderRunJobAdapter(
        services.GetRequiredService<IExecutionRuntime>(),
        services.GetRequiredService<ProviderTargetRegistry>(),
        services.GetRequiredService<ITraceStore>(),
        services.GetRequiredService<ProviderRunGuard>(),
        providerRunJobOptions));
builder.Services.AddSingleton(retrievalEvaluationJobOptions);
builder.Services.AddSingleton<ExecutionRuntimeRetrievalEvaluationJobAdapter>(services =>
    new ExecutionRuntimeRetrievalEvaluationJobAdapter(
        services.GetRequiredService<IExecutionRuntime>(),
        services.GetRequiredService<IRetrievalEvaluationService>(),
        retrievalEvaluationJobOptions));
if (mcpOptions.Enabled)
{
    var tasksEnabled = VyralMcpCatalog.Entries.Any(entry =>
        entry.Exposure == "task" && VyralMcpCatalog.IsEnabled(entry, mcpOptions));
    var taskStore = new VyralExecutionMcpTaskStore(
        objectStore,
        executionRuntime,
        executionAccess,
        mcpRequestContextAccessor,
        mcpOptions);
    builder.Services.AddSingleton(taskStore);
    var mcpBuilder = builder.Services.AddVyralMcp(mcpOptions, taskStore);
    if (tasksEnabled)
    {
        mcpBuilder.WithTools<VyralMcpTaskTools>();
    }
    if (mcpOptions.ConformanceMode)
    {
        mcpBuilder.WithTools<VyralMcpConformanceTools>();
        mcpBuilder.WithTools<VyralMcpCoreConformanceTools>();
        mcpBuilder.WithTools<VyralMcpMrtrConformanceTools>();
        mcpBuilder.WithPrompts<VyralMcpConformancePrompts>();
        mcpBuilder.WithPrompts<VyralMcpCoreConformancePrompts>();
        mcpBuilder.WithResources<VyralMcpCoreConformanceResources>();
        mcpBuilder.WithCompleteHandler((_, _) => ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion
            {
                Values = [],
                HasMore = false,
                Total = 0
            }
        }));
    }
}
LogStartupPhaseCompleted("service.registration", startupPhase);

startupPhase = LogStartupPhaseStarting("web_application.build");
var app = builder.Build();
LogStartupPhaseCompleted("web_application.build", startupPhase);
startupPhase = LogStartupPhaseStarting("execution.runtime.handlers");
_ = app.Services.GetRequiredService<ExecutionRuntimeProviderRunJobAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeRetrievalEvaluationJobAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeRagIngestionJobAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeGraphJobAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeRecordImportJobAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeArtifactRecordIngestionAdapter>();
_ = app.Services.GetRequiredService<ExecutionRuntimeCollectionManagementAdapter>();
LogStartupPhaseCompleted("execution.runtime.handlers", startupPhase, $"handlers={executionRuntime.ListHandlers().Count}");
var knownLiveProviderIds = new[] { "codex-cli", "claude-cli", "gemini-cli", "antigravity-cli", "grok-build-cli", "jules-api" };

app.Use(async (context, next) =>
{
    var requestedCorrelationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    var correlationId = IsSafeCorrelationId(requestedCorrelationId)
        ? requestedCorrelationId!
        : System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return Task.CompletedTask;
    });
    await next(context);
});

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
        var statusCode = GetStatusCode(exception);
        var exposeDetails = app.Environment.IsDevelopment() || IsSafePublicException(exception);
        if (statusCode >= StatusCodes.Status500InternalServerError && exception is not null)
        {
            app.Logger.LogError(exception, "Unhandled server error for {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else if (!exposeDetails && exception is not null)
        {
            app.Logger.LogWarning(exception, "Redacted server rejection for {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        context.Response.StatusCode = statusCode;
        await Results.Problem(
            title: exposeDetails
                ? exception?.GetType().Name ?? "Request failed"
                : statusCode >= StatusCodes.Status500InternalServerError ? "Internal server error" : "Request rejected",
            detail: exposeDetails
                ? exception?.Message
                : statusCode >= StatusCodes.Status500InternalServerError
                    ? "The server could not complete the request."
                    : "The request could not be completed.",
            statusCode: statusCode).ExecuteAsync(context);
    });
});

if (mcpOptions.Enabled) app.UseVyralMcpTelemetry(mcpOptions);
if (mcpOptions.Enabled) app.UseVyralMcpDnsRebindingProtection(mcpOptions);
if (mcpOptions.Enabled) app.UseVyralMcpRequestLimits(mcpOptions);
if (mcpOptions.Enabled) app.UseVyralMcpRoutingHeaderNormalization(mcpOptions);

// Keep the routing boundary explicit: MCP 2026-07-28 endpoint selection evaluates
// self-describing headers, so optional HTTP whitespace must be removed first.
app.UseRouting();
app.UseCors();
if (canonicalStoreOptions.Enabled) app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (!accessOptions.RequiresAuthentication(context) || accessOptions.IsAuthorized(context.Request))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await Results.Problem(
        title: "Unauthorized",
        detail: $"Supply a valid API key in the {accessOptions.ApiKeyHeader} header or Authorization bearer token.",
        statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
});

if (mcpOptions.Enabled) app.UseVyralMcpRequestContext(mcpOptions, mcpRequestContextAccessor);
if (mcpOptions.Enabled) app.MapMcp(mcpOptions.EndpointPath);

app.MapGet("/", () => "Vyral Server - Viability & Resilience Abstraction Layer");

app.MapGet("/health", (IRecordCollectionStore recordStore, IObjectStore objectStore, ITraceStore traceStore, IEmbeddingProvider embeddingProvider, ServerAccessOptions access, ProviderRunGuard guard, ExecutionRuntimeProviderRunJobAdapter jobs) =>
{
    return Results.Ok(BuildServerHealth(recordStore, objectStore, traceStore, canonicalStore, embeddingProvider, access, guard, jobs));
});

app.MapGet("/readiness", async (IRecordCollectionStore recordStore, IObjectStore objectStore, ITraceStore traceStore, IEmbeddingProvider embeddingProvider, EmbeddingProviderRegistry embeddingRegistry, ProviderTargetRegistry providerRegistry, ServerAccessOptions access, ProviderRunGuard guard, ExecutionRuntimeProviderRunJobAdapter jobs, ServerStorageOptions storage, CancellationToken ct) =>
{
    var report = await BuildServerReadinessReportAsync(recordStore, objectStore, traceStore, canonicalStore, embeddingProvider, embeddingRegistry, providerRegistry, access, guard, jobs, storage, embeddingOptions, app.Environment.IsDevelopment(), ct);
    return Results.Ok(report);
});

app.MapPost("/ingest/record-artifact", async (
    string? productId,
    string? tenantId,
    HttpContext httpContext,
    ExecutionRuntimeArtifactRecordIngestionAdapter ingestion,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    var request = httpContext.Request;
    if (!request.HasFormContentType)
    {
        throw new InvalidOperationException("Record-artifact ingest requires multipart/form-data.");
    }

    var form = await request.ReadFormAsync(ct);
    var manifestJson = form["manifest"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(manifestJson))
    {
        throw new InvalidOperationException("Record-artifact ingest requires a manifest form field.");
    }
    var manifest = JsonSerializer.Deserialize<ArtifactRecordIngestManifest>(manifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (manifest is null)
    {
        throw new InvalidOperationException("Record-artifact ingest manifest is required.");
    }
    var artifact = form.Files.GetFile("artifact");
    if (artifact is null)
    {
        throw new InvalidOperationException("Record-artifact ingest requires an artifact file part.");
    }

    var authorizationRequest = new ExecutionRunRequest
    {
        HandlerId = ExecutionRuntimeArtifactRecordIngestionAdapter.HandlerId,
        PluginId = ExecutionRuntimeArtifactRecordIngestionAdapter.PluginId,
        Scope = BuildExecutionScope(productId, tenantId)
    };
    await executionAccess.BindStartRunAsync(httpContext, authorizationRequest, ct);
    await using var content = artifact.OpenReadStream();
    var run = await ingestion.StartAsync(
        manifest,
        content,
        GetIdempotencyKey(request),
        authorizationRequest.Scope,
        ct);
    PreparePublicExecutionRun(run);
    return AdmissionHttpResults.From(run.Admission.StatusUri, run, run.Admission);
});

if (canonicalStoreOptions.Enabled)
{
app.MapGet("/canonical/migrations", async (HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeAdminAsync(httpContext, ct);
    return Results.Ok(await canonicalStore.ListMigrationsAsync(ct));
});

app.MapPost("/canonical/migrations", async (IReadOnlyList<CanonicalMigration> migrations, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeAdminAsync(httpContext, ct);
    await canonicalStore.ApplyMigrationsAsync(migrations, ct);
    return Results.NoContent();
});

app.MapGet("/canonical/preflight", async (HttpContext httpContext, ICanonicalStore canonicalStore, CanonicalStoreOptions canonicalOptions, CanonicalRateLimitOptions canonicalRateLimit, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeAdminAsync(httpContext, ct);
    var migrations = await canonicalStore.ListMigrationsAsync(ct);
    return Results.Ok(BuildCanonicalPreflightReport(canonicalStore, canonicalOptions, canonicalRateLimit, canonicalAccess, migrations.Count));
});

app.MapPost("/canonical/preflight/probe", async (HttpContext httpContext, ICanonicalStore canonicalStore, CanonicalStoreOptions canonicalOptions, CanonicalRateLimitOptions canonicalRateLimit, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeAdminAsync(httpContext, ct);
    var migrations = await canonicalStore.ListMigrationsAsync(ct);
    var probe = await canonicalStore.RunDataPlanePreflightAsync(ct);
    var report = BuildCanonicalPreflightReport(canonicalStore, canonicalOptions, canonicalRateLimit, canonicalAccess, migrations.Count, probe);
    return probe.Ready
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/canonical/tenants/{tenantId}/transactions", async (string tenantId, CanonicalTransactionRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "transaction");
    ValidateCanonicalEvidenceBriefDocuments(request);
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Write, ct);
    request.Actor = await canonicalAccess.GetVerifiedPrincipalAsync(httpContext, ct) ?? request.Actor;
    return Results.Ok(await canonicalStore.CommitAsync(request, ct));
});

app.MapGet("/canonical/tenants/{tenantId}/documents/{documentType}/{id}", async (string tenantId, string documentType, string id, bool? includeDeleted, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Read, ct);
    var document = await canonicalStore.GetDocumentAsync(tenantId, documentType, id, includeDeleted ?? false, ct);
    return document is null ? Results.NotFound() : Results.Ok(document);
});

app.MapPost("/canonical/tenants/{tenantId}/documents/read", async (string tenantId, CanonicalDocumentReadRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "document read");
    CanonicalContractValidator.ValidateDocumentRead(request);
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Read, ct);
    var document = await canonicalStore.GetDocumentAsync(tenantId, request.DocumentType, request.Id, request.IncludeDeleted, ct);
    return document is null ? Results.NotFound() : Results.Ok(document);
});

app.MapPost("/canonical/tenants/{tenantId}/documents/query", async (string tenantId, CanonicalDocumentQuery query, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, query.TenantId, "document query");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Read, ct);
    return Results.Ok(await canonicalStore.QueryDocumentsAsync(query, ct));
});

app.MapGet("/canonical/tenants/{tenantId}/documents/{documentType}/{id}/revisions", async (string tenantId, string documentType, string id, int? limit, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Read, ct);
    return Results.Ok(await canonicalStore.GetRevisionsAsync(tenantId, documentType, id, limit ?? 100, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/documents/revisions", async (string tenantId, CanonicalDocumentRevisionQuery query, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, query.TenantId, "document revision query");
    CanonicalContractValidator.ValidateDocumentRevisionQuery(query);
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Read, ct);
    return Results.Ok(await canonicalStore.GetRevisionsAsync(tenantId, query.DocumentType, query.Id, query.Limit ?? 100, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/leases", async (string tenantId, CanonicalOutboxLeaseRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "outbox lease");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    return Results.Ok(await canonicalStore.LeaseOutboxAsync(request, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/query", async (string tenantId, CanonicalOutboxQuery query, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, query.TenantId, "outbox query");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    return Results.Ok(await canonicalStore.QueryOutboxAsync(query, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/{eventId}/renew", async (string tenantId, string eventId, CanonicalOutboxLeaseRenewalRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "outbox lease renewal");
    if (!string.Equals(eventId, request.EventId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical outbox event id must match the route event id.");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    return Results.Ok(await canonicalStore.RenewOutboxLeaseAsync(request, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/{eventId}/ack", async (string tenantId, string eventId, CanonicalOutboxAcknowledgement acknowledgement, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    await canonicalStore.AcknowledgeOutboxAsync(tenantId, eventId, acknowledgement.LeaseToken, ct);
    return Results.NoContent();
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/{eventId}/nack", async (string tenantId, string eventId, CanonicalOutboxNackRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "outbox release");
    if (!string.Equals(eventId, request.EventId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical outbox event id must match the route event id.");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    await canonicalStore.NackOutboxAsync(request, ct);
    return Results.NoContent();
});

app.MapPost("/canonical/tenants/{tenantId}/outbox/{eventId}/replay", async (string tenantId, string eventId, CanonicalOutboxReplayRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.TenantId, "outbox replay");
    if (!string.Equals(eventId, request.EventId, StringComparison.Ordinal)) throw new InvalidOperationException("Canonical outbox event id must match the route event id.");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Dispatch, ct);
    await canonicalStore.ReplayOutboxAsync(request, ct);
    return Results.NoContent();
});

app.MapGet("/canonical/tenants/{tenantId}/export", async (string tenantId, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Export, ct);
    return Results.Ok(await canonicalStore.ExportTenantAsync(tenantId, ct));
});

app.MapPost("/canonical/tenants/{tenantId}/restore", async (string tenantId, CanonicalRestoreRequest request, HttpContext httpContext, ICanonicalStore canonicalStore, VyralCanonicalAccess canonicalAccess, CancellationToken ct) =>
{
    RequireCanonicalTenantMatch(tenantId, request.Snapshot.TenantId, "restore snapshot");
    await canonicalAccess.AuthorizeTenantAsync(httpContext, tenantId, CanonicalAccessOperations.Restore, ct);
    await canonicalAccess.AuthorizeAdminAsync(httpContext, ct);
    await canonicalStore.RestoreTenantAsync(request, ct);
    return Results.NoContent();
});
}

app.MapGet("/execution/runtime", async (HttpContext httpContext, IExecutionRuntimeAdapter runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    // The unfiltered catalog is operational data. Shared consumer identities use the effective
    // discovery route below; only a maintenance identity may inspect the global catalog.
    await executionAccess.AuthorizeMaintenanceAsync(httpContext, ct);
    return Results.Ok(new ExecutionRuntimeSurface(
        await runtime.GetAdapterStatusAsync(ct),
        runtime.ListPlugins(),
        runtime.ListHandlers()));
});

app.MapGet("/execution/runtime/effective", async (
    string? productId,
    string? tenantId,
    HttpContext httpContext,
    IExecutionRuntimeAdapter runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    var effective = await executionAccess.GetEffectiveRuntimeAccessAsync(httpContext, productId, tenantId, ct);
    var status = await runtime.GetAdapterStatusAsync(ct);
    var safeStatus = new ExecutionRuntimeAdapterStatus
    {
        Adapter = status.Adapter,
        Available = status.Available,
        Status = status.Status,
        CheckedAtUtc = status.CheckedAtUtc,
        ActiveRuns = status.ActiveRuns,
        OperationalPolicy = status.OperationalPolicy,
        ResumePolicy = status.ResumePolicy
    };
    var handlers = runtime.ListHandlers()
        .Where(handler => effective.AllowsAnyHandler || effective.AllowedHandlerIds.Contains(handler.HandlerId))
        .ToList();
    return Results.Ok(new EffectiveExecutionRuntimeSurface(
        safeStatus,
        new ExecutionRuntimeDiscoveryScope
        {
            SharedExecution = effective.SharedExecution,
            ScopeRequired = effective.ScopeRequired,
            ProductId = effective.ProductId,
            TenantId = effective.TenantId
        },
        handlers));
});

app.MapGet("/execution/runs", async (
    string? handlerId,
    string? pluginId,
    string? status,
    string? correlationId,
    string? idempotencyKey,
    DateTime? createdAfterUtc,
    DateTime? createdBeforeUtc,
    DateTime? updatedAfterUtc,
    DateTime? updatedBeforeUtc,
    int? limit,
    bool? includeResult,
    HttpRequest httpRequest,
    IExecutionRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    var runs = await runtime.ListRunsAsync(new ExecutionRunQuery
    {
        HandlerId = handlerId,
        PluginId = pluginId,
        Status = status,
        CorrelationId = correlationId,
        IdempotencyKey = idempotencyKey,
        CreatedAfterUtc = createdAfterUtc,
        CreatedBeforeUtc = createdBeforeUtc,
        UpdatedAfterUtc = updatedAfterUtc,
        UpdatedBeforeUtc = updatedBeforeUtc,
        Limit = limit,
        IncludeResult = includeResult == true,
        Tags = ParseExecutionTagFilters(httpRequest.Query)
    }, ct);
    var readable = await executionAccess.FilterReadableRunsAsync(httpRequest.HttpContext, runs, ct);
    return Results.Ok(readable.Select(PreparePublicExecutionRun).ToList());
});

app.MapPost("/execution/runs", async (ExecutionRunRequest request, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    BindExecutionIdempotencyKey(httpContext.Request, request);
    await executionAccess.BindStartRunAsync(httpContext, request, ct);
    var run = await runtime.StartRunAsync(request, ct);
    PreparePublicExecutionRun(run);
    return AdmissionHttpResults.From($"/execution/runs/{run.Id}", run, run.Admission);
});

app.MapGet("/execution/runs/{runId}", async (string runId, bool? includeResult, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var run = await runtime.GetRunAsync(runId, includeResult != false, ct);
    await executionAccess.AuthorizeRunAsync(httpContext, run, ExecutionAccessOperations.ReadRun, ct);
    return run is not null ? Results.Ok(PreparePublicExecutionRun(run)) : Results.NotFound();
});

app.MapDelete("/execution/runs/{runId}", async (string runId, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.CancelRun, ct);
    var run = await runtime.CancelRunAsync(runId, ct);
    return run is not null ? Results.Ok(PreparePublicExecutionRun(run)) : Results.NotFound();
});

app.MapGet("/execution/runs/{runId}/history", async (string runId, int? limit, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.ReadRun, ct);
    return Results.Ok(await runtime.GetHistoryAsync(runId, new ExecutionHistoryQuery { Limit = limit }, ct));
});

app.MapGet("/execution/runs/{runId}/artifacts", async (string runId, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.ReadRun, ct);
    return Results.Ok(await runtime.ListArtifactsAsync(runId, ct));
});

app.MapGet("/execution/runs/{runId}/artifacts/{artifactRef}", async (string runId, string artifactRef, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.ReadRun, ct);
    var artifact = await runtime.GetArtifactAsync(runId, artifactRef, ct);
    return artifact is not null ? Results.Ok(artifact) : Results.NotFound();
});

app.MapGet("/execution/runs/{runId}/checkpoints/{key}", async (string runId, string key, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.ReadRun, ct);
    var checkpoint = await runtime.GetCheckpointAsync(runId, key, ct);
    return checkpoint is not null ? Results.Ok(checkpoint) : Results.NotFound();
});

app.MapPost("/execution/runs/{runId}/events", async (
    string runId,
    ExecutionExternalEventRequest request,
    HttpContext httpContext,
    IExecutionRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(request.RunId) && !string.Equals(request.RunId, runId, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("External event runId must match the route run id.");
    }

    request.RunId = runId;
    await executionAccess.AuthorizeRunAsync(httpContext, await runtime.GetRunAsync(runId, false, ct), ExecutionAccessOperations.RaiseEvent, ct);
    return Results.Ok(await runtime.RaiseEventAsync(request, ct));
});

if (externalExecutionRuntime is not null)
{
app.MapPost("/execution/workers/leases", async (
    ExecutionExternalWorkerLeaseRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request, ct);
    var lease = await runtime.LeaseNextRunAsync(request, ct);
    return lease is null
        ? Results.NoContent()
        : Results.Ok(PreparePublicExecutionLease(lease));
});

app.MapPost("/execution/workers/leases/heartbeat", async (
    ExecutionExternalWorkerHeartbeatRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    return Results.Ok(PreparePublicExecutionLease(
        await runtime.HeartbeatExternalLeaseAsync(request, ct)));
});

app.MapPost("/execution/workers/leases/reports", async (
    ExecutionExternalWorkerReportRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    return Results.Ok(PreparePublicExecutionRun(
        await runtime.ReportExternalLeaseAsync(request, ct)));
});

app.MapPost("/execution/workers/leases/events", async (
    ExecutionExternalWorkerEventRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    await runtime.RecordExternalLeaseEventAsync(request, ct);
    return Results.NoContent();
});

app.MapPost("/execution/workers/leases/artifacts", async (
    ExecutionExternalWorkerArtifactRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    return Results.Ok(await runtime.PutExternalLeaseArtifactAsync(request, ct));
});

app.MapPost("/execution/workers/leases/checkpoints", async (
    ExecutionExternalWorkerCheckpointRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    return Results.Ok(await runtime.CheckpointExternalLeaseAsync(request, ct));
});

app.MapPost("/execution/workers/leases/checkpoints/read", async (
    ExecutionExternalWorkerCheckpointReadRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    var checkpoint = await runtime.GetExternalLeaseCheckpointAsync(request, ct);
    return checkpoint is null ? Results.NotFound() : Results.Ok(checkpoint);
});

app.MapPost("/execution/workers/leases/wait", async (
    ExecutionExternalWorkerWaitRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    var response = await runtime.WaitExternalLeaseAsync(request, ct);
    PreparePublicExecutionRun(response.Run);
    return Results.Ok(response);
});

app.MapPost("/execution/workers/leases/complete", async (
    ExecutionExternalWorkerCompletionRequest request,
    HttpContext httpContext,
    IExternalExecutionWorkerRuntime runtime,
    VyralExecutionAccess executionAccess,
    CancellationToken ct) =>
{
    await executionAccess.BindWorkerAsync(httpContext, request.WorkerId, ct);
    return Results.Ok(PreparePublicExecutionRun(
        await runtime.CompleteExternalLeaseAsync(request, ct)));
});
}

app.MapGet("/execution/runtime/maintenance", async (HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeMaintenanceAsync(httpContext, ct);
    return runtime is IExecutionRuntimeMaintenance maintenance
        ? Results.Ok(await maintenance.GetMaintenanceStatusAsync(ct))
        : Results.NotFound();
});

app.MapPost("/execution/runtime/maintenance/prune", async (ExecutionMaintenancePruneRequest request, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeMaintenanceAsync(httpContext, ct);
    return runtime is IExecutionRuntimeMaintenance maintenance
        ? Results.Ok(await maintenance.PruneAsync(request ?? new ExecutionMaintenancePruneRequest(), ct))
        : Results.NotFound();
});

app.MapPost("/execution/runtime/maintenance/reconcile", async (ExecutionMaintenanceDispatchReconcileRequest request, HttpContext httpContext, IExecutionRuntime runtime, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    await executionAccess.AuthorizeMaintenanceAsync(httpContext, ct);
    return runtime is IExecutionRuntimeMaintenance maintenance
        ? Results.Ok(await maintenance.ReconcileDispatchAsync(request ?? new ExecutionMaintenanceDispatchReconcileRequest(), ct))
        : Results.NotFound();
});

app.MapGet("/embedding-providers", (EmbeddingProviderRegistry registry) =>
{
    return Results.Ok(registry.GetProviders());
});

app.MapGet("/embedding-providers/guidance", (EmbeddingProviderRegistry registry) =>
{
    return Results.Ok(registry.GetProviders().Select(BuildEmbeddingProviderGuidance).ToList());
});

app.MapGet("/embedding-providers/doctor", (EmbeddingProviderRegistry registry, IEmbeddingProvider embeddingProvider) =>
{
    return Results.Ok(DiagnoseEmbeddingProvider(embeddingProvider, registry, embeddingOptions));
});

app.MapPost("/embeddings", async (EmbeddingRequest request, IEmbeddingProvider embeddingProvider, EmbeddingProviderOptions embeddingOptions, HttpContext httpContext, CancellationToken ct) =>
{
    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, httpContext.RequestAborted);
    var cancellationToken = linkedCancellation.Token;
    var texts = GetEmbeddingTexts(request);
    var purpose = EmbeddingTextPreparer.NormalizePurpose(request.Purpose);
    var items = new List<EmbeddingResult>(texts.Count);
    for (var i = 0; i < texts.Count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = await GenerateEmbeddingResultAsync(
            request,
            texts[i],
            i,
            purpose,
            embeddingProvider,
            embeddingOptions,
            cancellationToken);
        items.Add(item);
        cancellationToken.ThrowIfCancellationRequested();
    }

    return Results.Ok(new EmbeddingResponse
    {
        Provider = embeddingProvider.ProviderId,
        ModelId = embeddingProvider.ModelId,
        Dimensions = embeddingProvider.Dimensions,
        Purpose = purpose,
        Items = items
    });
});

app.MapPost("/embeddings/jobs", async (EmbeddingRequest request, ExecutionRuntimeEmbeddingJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartAsync(request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/embeddings/jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/embeddings/jobs", async (int? limit, bool? includeResult, ExecutionRuntimeEmbeddingJobAdapter jobs, CancellationToken ct) =>
{
    return Results.Ok(await jobs.ListAsync(limit, includeResult == true, ct));
});

app.MapGet("/embeddings/jobs/{jobId}", async (string jobId, ExecutionRuntimeEmbeddingJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.GetAsync(jobId, true, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/embeddings/jobs/{jobId}", async (string jobId, ExecutionRuntimeEmbeddingJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapGet("/providers", (ProviderTargetRegistry registry) =>
{
    return Results.Ok(registry.GetProfiles());
});

app.MapGet("/providers/capabilities", (ProviderTargetRegistry registry, IConfiguration configuration, ProviderRunGuard guard, ServerAccessOptions access) =>
{
    var enableLiveTargets = ParseOptionalBool(configuration["Providers:EnableLiveTargets"], "Providers:EnableLiveTargets") ?? false;
    return Results.Ok(BuildProviderCapabilityMatrix(registry, enableLiveTargets, knownLiveProviderIds, guard, access));
});

app.MapGet("/providers/readiness", async (ProviderTargetRegistry registry, IConfiguration configuration, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct) =>
{
    var envelope = new ProviderReadinessEnvelope();
    foreach (var profile in registry.GetProfiles())
    {
        var readiness = await GetProviderReadinessAsync(profile.Id, registry, traces, guard, access, ct);
        if (readiness is not null)
        {
            envelope.Items.AddRange(readiness.Items);
        }
    }

    var enableLiveTargets = ParseOptionalBool(configuration["Providers:EnableLiveTargets"], "Providers:EnableLiveTargets") ?? false;
    if (!enableLiveTargets)
    {
        foreach (var disabledId in knownLiveProviderIds)
        {
            envelope.DisabledProviders.Add(BuildDisabledProviderInfo(disabledId));
        }
    }

    return Results.Ok(envelope);
});

app.MapGet("/providers/doctor", async (ProviderTargetRegistry registry, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct) =>
{
    var results = new List<ProviderDoctorResult>();
    foreach (var profile in registry.GetProfiles())
    {
        var result = await DiagnoseProviderAsync(profile.Id, registry, traces, guard, access, ct);
        if (result is not null)
        {
            results.Add(result);
        }
    }

    return Results.Ok(results);
});

app.MapGet("/providers/quotas", async (ProviderTargetRegistry registry, CancellationToken ct) =>
{
    var results = new List<ProviderQuotaResult>();
    foreach (var profile in registry.GetProfiles())
    {
        var quota = await GetProviderQuotaAsync(profile.Id, registry, ct);
        if (quota is not null)
        {
            results.Add(quota);
        }
    }

    return Results.Ok(results);
});

app.MapGet("/providers/{provider}", (string provider, ProviderTargetRegistry registry) =>
{
    var descriptor = registry.GetDescriptor(provider);
    return descriptor is not null ? Results.Ok(descriptor) : Results.NotFound();
});

app.MapGet("/providers/{provider}/doctor", async (string provider, ProviderTargetRegistry registry, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct) =>
{
    var result = await DiagnoseProviderAsync(provider, registry, traces, guard, access, ct);
    return result is not null ? Results.Ok(result) : Results.NotFound();
});

app.MapGet("/providers/{provider}/readiness", async (string provider, ProviderTargetRegistry registry, IConfiguration configuration, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct) =>
{
    var readiness = await GetProviderReadinessAsync(provider, registry, traces, guard, access, ct);
    if (readiness is not null)
    {
        return Results.Ok(readiness);
    }

    var enableLiveTargets = ParseOptionalBool(configuration["Providers:EnableLiveTargets"], "Providers:EnableLiveTargets") ?? false;
    if (!enableLiveTargets && knownLiveProviderIds.Contains(provider, StringComparer.OrdinalIgnoreCase))
    {
        var disabledReadiness = BuildDisabledProviderReadiness(provider, guard, access);
        if (disabledReadiness is not null)
        {
            return Results.Ok(disabledReadiness);
        }
    }

    return Results.NotFound();
});

app.MapGet("/providers/{provider}/quota", async (string provider, ProviderTargetRegistry registry, CancellationToken ct) =>
{
    var quota = await GetProviderQuotaAsync(provider, registry, ct);
    return Results.Ok(quota ?? ProviderQuotaResult.NotRegistered(provider));
});

app.MapGet("/providers/{provider}/qualifications", async (string provider, ProviderTargetRegistry registry, ITraceStore traces, CancellationToken ct) =>
{
    var qualifications = await GetProviderQualificationsAsync(provider, registry, traces, ct);
    return qualifications is not null ? Results.Ok(qualifications) : Results.NotFound();
});

app.MapGet("/providers/{provider}/models", async (string provider, ProviderTargetRegistry registry, CancellationToken ct) =>
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return Results.Ok(ProviderModelListResult.NotRegistered(provider));
    }

    if (target is not IProviderModelCatalog catalog)
    {
        return Results.Ok(ProviderModelListResult.Unsupported(provider));
    }

    return Results.Ok(await catalog.ListModelsAsync(ct));
});

app.MapPost("/providers/{provider}/qualify", async (string provider, ProviderQualificationRequest request, ProviderTargetRegistry registry, IConfiguration configuration, ITraceStore traces, ProviderRunGuard guard, CancellationToken ct) =>
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return Results.NotFound();
    }

    var qualifications = await QualifyProviderAsync(target, request, configuration, traces, guard, ct);
    return Results.Ok(qualifications);
});

app.MapPost("/providers/{provider}/run", async (string provider, ProviderRunRequest request, ProviderTargetRegistry registry, IConfiguration configuration, ExecutionRuntimeProviderRunJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return UnknownProviderProblem(provider);
    }

    request.Provider = provider;
    request.ArtifactDirectory = GetProviderArtifactDirectory(configuration);
    var job = await jobs.StartAsync(
        provider,
        request,
        request.ArtifactDirectory,
        GetIdempotencyKey(httpContext.Request),
        VyralAdmissionOperations.RunProviderCapability,
        ct);
    return AdmissionHttpResults.From($"/provider-jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/providers/{provider}/jobs", async (string provider, ProviderRunRequest request, ProviderTargetRegistry registry, IConfiguration configuration, ExecutionRuntimeProviderRunJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return UnknownProviderProblem(provider);
    }

    request.Provider = provider;
    request.ArtifactDirectory = GetProviderArtifactDirectory(configuration);
    var job = await jobs.StartAsync(
        provider,
        request,
        request.ArtifactDirectory,
        GetIdempotencyKey(httpContext.Request),
        VyralAdmissionOperations.StartProviderRunJob,
        ct);
    return AdmissionHttpResults.From($"/provider-jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/provider-jobs", async (string? provider, int? limit, bool? includeResult, ExecutionRuntimeProviderRunJobAdapter jobs, CancellationToken ct) =>
{
    return Results.Ok(await jobs.ListAsync(provider, limit, includeResult == true, ct));
});

app.MapGet("/provider-jobs/{jobId}", async (string jobId, ExecutionRuntimeProviderRunJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.GetAsync(jobId, true, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/provider-jobs/{jobId}", async (string jobId, ExecutionRuntimeProviderRunJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapGet("/openapi/vyral.json", async (CancellationToken ct) =>
{
    await using var stream = typeof(Program).Assembly.GetManifestResourceStream("Vyral.Server.contracts.vyral.openapi.json");
    if (stream is null)
    {
        return Results.Problem("OpenAPI contract resource was not found.", statusCode: StatusCodes.Status500InternalServerError);
    }

    using var reader = new StreamReader(stream);
    var json = await reader.ReadToEndAsync(ct);
    if (!canonicalStoreOptions.Enabled)
    {
        var contract = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("OpenAPI contract resource is not a JSON object.");
        if (contract["paths"] is JsonObject paths)
        {
            foreach (var path in paths.Select(item => item.Key).Where(path => path.StartsWith("/canonical", StringComparison.Ordinal)).ToList())
            {
                paths.Remove(path);
            }
        }
        json = contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    return Results.Text(json, "application/json; charset=utf-8");
});

app.MapGet("/contracts/schemas/vyral-public.schema.json", async (CancellationToken ct) =>
{
    await using var stream = typeof(Program).Assembly.GetManifestResourceStream("Vyral.Server.contracts.vyral-public.schema.json");
    if (stream is null)
    {
        return Results.Problem("Public JSON Schema contract resource was not found.", statusCode: StatusCodes.Status500InternalServerError);
    }

    using var reader = new StreamReader(stream);
    return Results.Text(await reader.ReadToEndAsync(ct), "application/schema+json; charset=utf-8");
});

app.MapGet("/graph/provider-shapes", () =>
{
    return Results.Ok(VyralGraphProviderShapeCatalog.All);
});

app.MapGet("/graph/provider-shapes/{providerId}", (string providerId) =>
{
    return VyralGraphProviderShapeCatalog.TryGet(providerId, out var shape)
        ? Results.Ok(shape)
        : Results.NotFound();
});

app.MapGet("/collections", async (IRecordCollectionStore recordStore) =>
{
    var collections = await recordStore.GetCollectionsAsync();
    return Results.Ok(collections);
});

app.MapPost("/collections", async (string? productId, string? tenantId, RecordCollectionPolicy policy, ExecutionRuntimeCollectionManagementAdapter collections, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var request = collections.CreateCreateRunRequest(
        policy,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId));
    await executionAccess.BindStartRunAsync(httpContext, request, ct);
    var run = await collections.StartRunAsync(request, ct);
    PreparePublicExecutionRun(run);
    return AdmissionHttpResults.From(run.Admission.StatusUri, run, run.Admission);
});

app.MapGet("/collections/{collection}", async (string collection, IRecordCollectionStore recordStore) =>
{
    var policy = await recordStore.GetCollectionPolicyAsync(collection);
    return policy is not null ? Results.Ok(policy) : Results.NotFound();
});

app.MapGet("/collections/{collection}/export", async (string collection, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    var export = await recordStore.ExportCollectionAsync(collection, ct: ct);
    return export is not null ? Results.Ok(export) : Results.NotFound();
});

app.MapPost("/collections/{collection}/export", async (string collection, CollectionExportRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    ValidateCollectionExportRequest(request);
    var export = await recordStore.ExportCollectionAsync(collection, request, ct);
    return export is not null ? Results.Ok(export) : Results.NotFound();
});

app.MapPost("/collections/{collection}/import", async (string collection, string? productId, string? tenantId, CollectionImportRequest request, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    ValidateCollectionImportRequest(collection, request);
    var runRequest = jobs.CreateCollectionImportRunRequest(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId),
        VyralAdmissionOperations.ImportCollection);
    await executionAccess.BindStartRunAsync(httpContext, runRequest, ct);
    var job = await jobs.StartRunAsync(runRequest, ct);
    return AdmissionHttpResults.From($"/record-import/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/import/jobs", async (string collection, string? productId, string? tenantId, CollectionImportRequest request, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    ValidateCollectionImportRequest(collection, request);
    var runRequest = jobs.CreateCollectionImportRunRequest(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId));
    await executionAccess.BindStartRunAsync(httpContext, runRequest, ct);
    var job = await jobs.StartRunAsync(runRequest, ct);
    return AdmissionHttpResults.From($"/record-import/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/graph/import", async (string collection, VyralGraphCollectionImportRequest request, ExecutionRuntimeGraphJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    ValidateGraphImportRequest(request);
    var job = await jobs.StartImportAsync(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        ct,
        VyralAdmissionOperations.ImportGraphEnvelope);
    return AdmissionHttpResults.From($"/graph/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/graph/import/preflight", async (string collection, VyralGraphCollectionImportRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    ValidateGraphImportRequest(request);
    return Results.Ok(await recordStore.PreflightGraphImportAsync(collection, request, ct));
});

app.MapPost("/collections/{collection}/graph/export", async (string collection, VyralGraphCollectionExportRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    try
    {
        var export = await recordStore.ExportGraphEnvelopeAsync(collection, request, ct);
        return export is not null ? Results.Ok(export) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/collections/{collection}/graph/traverse", async (string collection, VyralGraphTraversalRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    try
    {
        var result = await recordStore.TraverseGraphAsync(collection, request, ct);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/collections/{collection}/graph/inspect", async (string collection, VyralGraphCollectionInspectionRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    try
    {
        var result = await recordStore.InspectGraphAsync(collection, request, ct);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/collections/{collection}/graph/doctor", async (string collection, VyralGraphDoctorRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    try
    {
        var result = await recordStore.DoctorGraphAsync(collection, request, ct);
        return result is not null ? Results.Ok(result) : Results.NotFound();
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/collections/{collection}/graph/import/jobs", async (string collection, VyralGraphCollectionImportRequest request, ExecutionRuntimeGraphJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    ValidateGraphImportRequest(request);
    var job = await jobs.StartImportAsync(collection, request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/graph/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/graph/inspect/jobs", async (string collection, VyralGraphCollectionInspectionRequest request, ExecutionRuntimeGraphJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartInspectionAsync(collection, request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/graph/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/graph/doctor/jobs", async (string collection, VyralGraphDoctorRequest request, ExecutionRuntimeGraphJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartDoctorAsync(collection, request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/graph/jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/graph/jobs", async (int? limit, bool? includeResult, ExecutionRuntimeGraphJobAdapter jobs, CancellationToken ct) =>
{
    return Results.Ok(await jobs.ListAsync(limit, includeResult == true, ct));
});

app.MapGet("/graph/jobs/{jobId}", async (string jobId, ExecutionRuntimeGraphJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.GetAsync(jobId, true, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/graph/jobs/{jobId}", async (string jobId, ExecutionRuntimeGraphJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapGet("/collections/{collection}/inspect", async (string collection, bool? includeAnomalies, int? anomalyLimit, ICollectionInspectionService inspectionService, CancellationToken ct) =>
{
    try
    {
        var result = await inspectionService.InspectAsync(collection, new CollectionInspectionRequest
        {
            IncludeAnomalies = includeAnomalies ?? true,
            AnomalyLimit = anomalyLimit ?? 50
        }, ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapDelete("/collections/{collection}", async (string collection, string? productId, string? tenantId, ExecutionRuntimeCollectionManagementAdapter collections, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var request = collections.CreateDeleteRunRequest(
        collection,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId));
    await executionAccess.BindStartRunAsync(httpContext, request, ct);
    var run = await collections.StartRunAsync(request, ct);
    PreparePublicExecutionRun(run);
    return AdmissionHttpResults.From(run.Admission.StatusUri, run, run.Admission);
});

app.MapGet("/collections/{collection}/records/{pk}/{id}", async (string collection, string pk, string id, IRecordCollectionStore recordStore) =>
{
    var record = await recordStore.GetRecordAsync(collection, pk, id);
    return record is not null ? Results.Ok(record) : Results.NotFound();
});

app.MapPost("/collections/{collection}/records", async (string collection, VyralRecord record, HttpRequest request, IRecordCollectionStore recordStore, CancellationToken ct) =>
{
    await recordStore.UpsertRecordAsync(collection, record, BuildRecordWritePrecondition(request), ct);
    return Results.Created($"/collections/{collection}/records/{record.PartitionKey}/{record.Id}", record);
});

app.MapPost("/collections/{collection}/records/batch", async (string collection, string? productId, string? tenantId, RecordBatchUpsertRequest request, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    ValidateBatchUpsertRequest(request);
    var runRequest = jobs.CreateBatchUpsertRunRequest(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId),
        VyralAdmissionOperations.UpsertRecords);
    await executionAccess.BindStartRunAsync(httpContext, runRequest, ct);
    var job = await jobs.StartRunAsync(runRequest, ct);
    return AdmissionHttpResults.From($"/record-import/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/records/batch/jobs", async (string collection, string? productId, string? tenantId, RecordBatchUpsertRequest request, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    ValidateBatchUpsertRequest(request);
    var runRequest = jobs.CreateBatchUpsertRunRequest(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        BuildExecutionScope(productId, tenantId));
    await executionAccess.BindStartRunAsync(httpContext, runRequest, ct);
    var job = await jobs.StartRunAsync(runRequest, ct);
    return AdmissionHttpResults.From($"/record-import/jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/record-import/jobs", async (int? limit, bool? includeResult, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var runs = await jobs.ListRunsAsync(limit, includeResult == true, ct);
    var readable = await executionAccess.FilterReadableRunsAsync(httpContext, runs, ct);
    return Results.Ok(jobs.MapRuns(readable));
});

app.MapGet("/record-import/jobs/{jobId}", async (string jobId, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var run = await jobs.GetRunAsync(jobId, true, ct);
    if (run is null) return Results.NotFound();
    await executionAccess.AuthorizeRunAsync(httpContext, run, ExecutionAccessOperations.ReadRun, ct);
    return Results.Ok(jobs.MapRun(run));
});

app.MapDelete("/record-import/jobs/{jobId}", async (string jobId, ExecutionRuntimeRecordImportJobAdapter jobs, HttpContext httpContext, VyralExecutionAccess executionAccess, CancellationToken ct) =>
{
    var existing = await jobs.GetRunAsync(jobId, false, ct);
    if (existing is null) return Results.NotFound();
    await executionAccess.AuthorizeRunAsync(httpContext, existing, ExecutionAccessOperations.CancelRun, ct);
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapPost("/collections/{collection}/rag/ingest-text", async (string collection, RagIngestTextRequest request, ExecutionRuntimeRagIngestionJobAdapter jobs, IRagIngestionService ragIngestionService, HttpContext httpContext, CancellationToken ct) =>
{
    if (request.Options?.DryRun == true)
    {
        return Results.Ok(await ragIngestionService.IngestTextAsync(collection, request, ct));
    }
    var job = await jobs.StartTextAsync(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        ct,
        VyralAdmissionOperations.IngestRagText);
    return AdmissionHttpResults.From($"/rag/ingestion/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/rag/ingest-text/batch", async (string collection, RagIngestTextBatchRequest request, ExecutionRuntimeRagIngestionJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartBatchAsync(
        collection,
        request,
        GetIdempotencyKey(httpContext.Request),
        ct,
        VyralAdmissionOperations.IngestRagTextBatch);
    return AdmissionHttpResults.From($"/rag/ingestion/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/rag/ingest-text/jobs", async (string collection, RagIngestTextRequest request, ExecutionRuntimeRagIngestionJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartTextAsync(collection, request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/rag/ingestion/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/collections/{collection}/rag/ingest-text/batch/jobs", async (string collection, RagIngestTextBatchRequest request, ExecutionRuntimeRagIngestionJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartBatchAsync(collection, request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/rag/ingestion/jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/rag/ingestion/jobs", async (int? limit, bool? includeResult, ExecutionRuntimeRagIngestionJobAdapter jobs, CancellationToken ct) =>
{
    return Results.Ok(await jobs.ListAsync(limit, includeResult == true, ct));
});

app.MapGet("/rag/ingestion/jobs/{jobId}", async (string jobId, ExecutionRuntimeRagIngestionJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.GetAsync(jobId, true, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/rag/ingestion/jobs/{jobId}", async (string jobId, ExecutionRuntimeRagIngestionJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/collections/{collection}/records/{pk}/{id}", async (string collection, string pk, string id, IRecordCollectionStore recordStore) =>
{
    await recordStore.DeleteRecordAsync(collection, pk, id);
    return Results.NoContent();
});

app.MapPost("/collections/{collection}/query", async (string collection, QueryEnvelope query, IRecordCollectionStore recordStore) =>
{
    try
    {
        var results = await recordStore.QueryRecordsPageAsync(collection, query);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/collections/{collection}/search", async (string collection, QueryEnvelope query, IRecordCollectionStore recordStore) =>
{
    try
    {
        var results = await recordStore.SearchRecordsPageAsync(collection, query);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPut("/objects/{container}/{**key}", async (string container, string key, HttpRequest request, IObjectStore objects) =>
{
    var result = await objects.PutObjectAsync(new ObjectWriteRequest
    {
        Container = container,
        Key = key,
        Content = request.Body,
        ContentType = request.ContentType,
        Metadata = ExtractObjectMetadata(request),
        IfMatch = EmptyToNull(request.Headers["If-Match"].ToString()),
        IfNoneMatch = EmptyToNull(request.Headers["If-None-Match"].ToString())
    });
    return Results.Ok(result);
});

app.MapGet("/objects/{container}", async (string container, string? prefix, int? limit, string? continuationToken, IObjectStore objects) =>
{
    var result = await objects.ListObjectsAsync(new ObjectListRequest
    {
        Container = container,
        Prefix = prefix,
        Limit = limit,
        ContinuationToken = continuationToken
    });
    return Results.Ok(result);
});

app.MapGet("/objects/{container}/{**key}", async (string container, string key, HttpResponse response, IObjectStore objects) =>
{
    var result = await objects.GetObjectAsync(new ObjectReadRequest { Container = container, Key = key });
    if (result == null) return Results.NotFound();

    response.Headers["ETag"] = result.Etag;
    response.Headers["X-Vyral-Content-Hash"] = result.ContentHash;
    foreach (var (metadataKey, metadataValue) in result.Metadata)
    {
        response.Headers[$"X-Vyral-Meta-{metadataKey}"] = metadataValue;
    }

    return Results.Stream(result.Content, result.ContentType ?? "application/octet-stream");
});

app.MapDelete("/objects/{container}/{**key}", async (string container, string key, HttpRequest request, IObjectStore objects) =>
{
    await objects.DeleteObjectAsync(new ObjectDeleteRequest
    {
        Container = container,
        Key = key,
        IfMatch = EmptyToNull(request.Headers["If-Match"].ToString())
    });
    return Results.NoContent();
});

app.MapGet("/retrieval/profiles", () => Results.Ok(RetrievalProfileCatalog.GetProfiles()));

app.MapPost("/search", async (RetrievalRequest request, IRetrievalService retrievalService) =>
{
    try
    {
        var results = await retrievalService.SearchAsync(request);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/retrieval/evaluate", async (RetrievalEvaluationRequest request, IRetrievalEvaluationService evaluationService, CancellationToken ct) =>
{
    try
    {
        var results = await evaluationService.EvaluateAsync(request, ct);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/retrieval/evaluate/compare", async (RetrievalEvaluationComparisonRequest request, IRetrievalEvaluationService evaluationService, CancellationToken ct) =>
{
    try
    {
        var results = await evaluationService.CompareAsync(request, ct);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/retrieval/evaluate/jobs", async (RetrievalEvaluationRequest request, ExecutionRuntimeRetrievalEvaluationJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartEvaluationAsync(request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/retrieval/evaluate/jobs/{job.Id}", job, job.Admission);
});

app.MapPost("/retrieval/evaluate/compare/jobs", async (RetrievalEvaluationComparisonRequest request, ExecutionRuntimeRetrievalEvaluationJobAdapter jobs, HttpContext httpContext, CancellationToken ct) =>
{
    var job = await jobs.StartComparisonAsync(request, GetIdempotencyKey(httpContext.Request), ct);
    return AdmissionHttpResults.From($"/retrieval/evaluate/jobs/{job.Id}", job, job.Admission);
});

app.MapGet("/retrieval/evaluate/jobs", async (int? limit, bool? includeResult, ExecutionRuntimeRetrievalEvaluationJobAdapter jobs, CancellationToken ct) =>
{
    return Results.Ok(await jobs.ListAsync(limit, includeResult == true, ct));
});

app.MapGet("/retrieval/evaluate/jobs/{jobId}", async (string jobId, ExecutionRuntimeRetrievalEvaluationJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.GetAsync(jobId, true, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapDelete("/retrieval/evaluate/jobs/{jobId}", async (string jobId, ExecutionRuntimeRetrievalEvaluationJobAdapter jobs, CancellationToken ct) =>
{
    var job = await jobs.CancelAsync(jobId, ct);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

app.MapPost("/rag/context", async (RagContextRequest request, IRagContextService ragContextService, CancellationToken ct) =>
{
    try
    {
        var results = await ragContextService.BuildContextAsync(request, ct);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/rag/context/evaluate", async (RagContextEvaluationRequest request, IRagContextService ragContextService, CancellationToken ct) =>
{
    try
    {
        var results = await ragContextService.EvaluateContextAsync(request, ct);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapPost("/rag/prompt", async (RagPromptRequest request, IRagPromptService ragPromptService) =>
{
    try
    {
        var results = await ragPromptService.BuildPromptAsync(request);
        return Results.Ok(results);
    }
    catch (InvalidOperationException ex) when (IsMissingCollectionError(ex))
    {
        return MissingCollectionProblem(ex);
    }
});

app.MapGet("/traces", async (string? operation, int? limit, ITraceStore traces) =>
{
    var results = await traces.ListTracesAsync(operation, limit);
    return Results.Ok(results);
});

app.MapGet("/traces/summary", async (string? operation, ITraceStore traces, CancellationToken ct) =>
{
    var result = await traces.SummarizeTracesAsync(operation, ct);
    return Results.Ok(result);
});

app.MapPost("/traces/prune", async (TracePruneRequest request, ITraceStore traces, CancellationToken ct) =>
{
    var result = await traces.PruneTracesAsync(request, ct);
    return Results.Ok(result);
});

app.MapPost("/traces/export", async (TraceExportRequest request, ITraceStore traces, CancellationToken ct) =>
{
    var result = await traces.ExportTracesAsync(request, ct);
    return Results.Ok(result);
});

app.MapGet("/traces/{id}", async (string id, ITraceStore traces) =>
{
    var trace = await traces.GetTraceAsync(id);
    return trace is not null ? Results.Ok(trace) : Results.NotFound();
});

LogStartupPhaseCompleted("server.startup", startupOverall, "readyToListen=true");
app.Run();

static DateTimeOffset LogStartupPhaseStarting(string phase, string? detail = null)
{
    var startedAt = DateTimeOffset.UtcNow;
    LogStartupPhase(phase, "starting", null, detail);
    return startedAt;
}

static void LogStartupPhaseCompleted(string phase, DateTimeOffset startedAt, string? detail = null)
{
    LogStartupPhase(phase, "completed", DateTimeOffset.UtcNow - startedAt, detail);
}

static void LogStartupPhase(string phase, string status, TimeSpan? elapsed, string? detail)
{
    var elapsedPart = elapsed.HasValue ? $" elapsedMs={(long)elapsed.Value.TotalMilliseconds}" : string.Empty;
    var detailPart = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail=\"{detail.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] vyral.startup phase={phase} status={status}{elapsedPart}{detailPart}");
}

static Dictionary<string, string>? ExtractObjectMetadata(HttpRequest request)
{
    const string prefix = "X-Vyral-Meta-";
    var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var header in request.Headers)
    {
        if (!header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

        var key = header.Key[prefix.Length..];
        if (string.IsNullOrWhiteSpace(key)) continue;

        metadata[key] = header.Value.ToString();
    }

    return metadata.Count == 0 ? null : metadata;
}

static string? EmptyToNull(string value)
{
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static bool IsSafeCorrelationId(string? value)
{
    return !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => character is >= (char)0x21 and <= (char)0x7E);
}

static string? GetIdempotencyKey(HttpRequest request)
{
    var value = request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? request.Headers["X-Idempotency-Key"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static void BindExecutionIdempotencyKey(HttpRequest httpRequest, ExecutionRunRequest runRequest)
{
    var headerKey = GetIdempotencyKey(httpRequest);
    var bodyKey = string.IsNullOrWhiteSpace(runRequest.IdempotencyKey)
        ? null
        : runRequest.IdempotencyKey.Trim();
    if (headerKey is not null && bodyKey is not null &&
        !string.Equals(headerKey, bodyKey, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Idempotency-Key must match request.idempotencyKey when both are supplied.");
    }

    runRequest.IdempotencyKey = headerKey ?? bodyKey;
}

static ExecutionRun PreparePublicExecutionRun(ExecutionRun run)
{
    var admission = VyralAdmissionOperations.Resolve(run);
    ExecutionAdmission.Attach(
        run,
        admission.OperationId,
        admission.StatusUri);
    return run;
}

static ExecutionExternalWorkerLease PreparePublicExecutionLease(ExecutionExternalWorkerLease lease)
{
    PreparePublicExecutionRun(lease.Run);
    return lease;
}

static ExecutionScope? BuildExecutionScope(string? productId, string? tenantId)
{
    var normalizedProductId = EmptyToNull(productId ?? string.Empty);
    var normalizedTenantId = EmptyToNull(tenantId ?? string.Empty);
    if (normalizedProductId is null && normalizedTenantId is null)
    {
        return null;
    }

    if (normalizedProductId is null || normalizedTenantId is null)
    {
        throw new InvalidOperationException("Execution scope requires both productId and tenantId.");
    }

    return new ExecutionScope
    {
        ProductId = normalizedProductId,
        TenantId = normalizedTenantId
    };
}

static IReadOnlyList<string> GetEmbeddingTexts(EmbeddingRequest request)
{
    const int maxBatchSize = 128;
    const int maxTextLength = 100_000;
    var texts = new List<string>();

    if (request.Text is not null)
    {
        texts.Add(request.Text);
    }

    if (request.Texts is not null)
    {
        texts.AddRange(request.Texts);
    }

    if (texts.Count == 0)
    {
        throw new InvalidOperationException("Embedding request must include text or texts.");
    }

    if (texts.Count > maxBatchSize)
    {
        throw new InvalidOperationException($"Embedding request supports at most {maxBatchSize} texts.");
    }

    foreach (var text in texts)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Embedding request text values cannot be empty.");
        }

        if (text.Length > maxTextLength)
        {
            throw new InvalidOperationException($"Embedding request text values cannot exceed {maxTextLength} characters.");
        }
    }

    return texts;
}

static async Task<EmbeddingResult> GenerateEmbeddingResultAsync(
    EmbeddingRequest request,
    string text,
    int index,
    string purpose,
    IEmbeddingProvider embeddingProvider,
    EmbeddingProviderOptions embeddingOptions,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    var prepared = EmbeddingTextPreparer.Prepare(
        text,
        purpose,
        request.QueryPrefix ?? embeddingOptions.QueryPrefix,
        request.PassagePrefix ?? embeddingOptions.PassagePrefix,
        request.SymmetricPrefix ?? embeddingOptions.SymmetricPrefix);
    var values = await embeddingProvider.GenerateEmbeddingAsync(prepared.PreparedText, ct);
    ct.ThrowIfCancellationRequested();
    return new EmbeddingResult
    {
        Index = index,
        TextLength = text.Length,
        PreparedTextLength = prepared.PreparedText.Length,
        PrefixApplied = prepared.PrefixApplied,
        PrefixLength = prepared.PrefixLength,
        Values = values
    };
}

static void ValidateBatchUpsertRequest(RecordBatchUpsertRequest request)
{
    const int maxBatchSize = 1000;
    if (request.Records.Count == 0)
    {
        throw new InvalidOperationException("Batch upsert request must include at least one record.");
    }

    if (request.Records.Count > maxBatchSize)
    {
        throw new InvalidOperationException($"Batch upsert request supports at most {maxBatchSize} records.");
    }

    request.ValidatePreconditionAlignment();
}

static RecordWritePrecondition? BuildRecordWritePrecondition(HttpRequest request)
{
    var precondition = new RecordWritePrecondition
    {
        IfMatch = EmptyToNull(request.Headers["If-Match"].ToString()),
        IfNoneMatch = EmptyToNull(request.Headers["If-None-Match"].ToString())
    };
    return precondition.HasConditions ? precondition : null;
}

static void ValidateCollectionImportRequest(string targetCollection, CollectionImportRequest request)
{
    if (request.Snapshot is null)
    {
        throw new InvalidOperationException("Collection import request must include a snapshot.");
    }

    if (request.Snapshot.Records.Count > CollectionSnapshotLimits.MaxRecords)
    {
        throw new InvalidOperationException($"Collection import snapshot supports at most {CollectionSnapshotLimits.MaxRecords} records.");
    }

    var snapshot = request.Snapshot;
    var sourceCollection = string.IsNullOrWhiteSpace(snapshot.Collection)
        ? snapshot.Policy?.Name
        : snapshot.Collection.Trim();
    if (string.IsNullOrWhiteSpace(sourceCollection))
    {
        throw new InvalidOperationException("Collection import snapshot requires a source collection name.");
    }
    if (!request.AllowCollectionRename && !string.Equals(sourceCollection, targetCollection, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Collection import snapshot is for '{sourceCollection}', but target collection is '{targetCollection}'. Set allowCollectionRename to true to import under a different collection name.");
    }
    if (snapshot.Policy is null || string.IsNullOrWhiteSpace(snapshot.Policy.Name))
    {
        throw new InvalidOperationException("Collection import snapshot requires a collection policy.");
    }
    if (snapshot.RecordCount.HasValue && snapshot.RecordCount.Value != snapshot.Records.Count)
    {
        throw new InvalidOperationException($"Collection import snapshot recordCount is {snapshot.RecordCount.Value}, but records contains {snapshot.Records.Count} item(s).");
    }
    if (snapshot.Truncated && !request.AllowPartialSnapshot)
    {
        throw new InvalidOperationException("Collection import snapshot is truncated. Set allowPartialSnapshot to true to import a partial snapshot intentionally.");
    }
    var actualHash = RecordCollectionStoreExtensions.ComputeCollectionSnapshotHash(snapshot);
    var expectedHash = string.IsNullOrWhiteSpace(request.ExpectedContentHash)
        ? snapshot.ContentHash
        : request.ExpectedContentHash.Trim();
    if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash.Trim(), actualHash, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Collection import content hash mismatch. Expected {expectedHash.Trim()}, actual {actualHash}.");
    }
}

static void ValidateCollectionExportRequest(CollectionExportRequest request)
{
    if (request.MaxRecords.HasValue && request.MaxRecords.Value <= 0)
    {
        throw new InvalidOperationException("Collection export maxRecords must be greater than zero.");
    }

    if (request.MaxRecords.HasValue && request.MaxRecords.Value > CollectionSnapshotLimits.MaxRecords)
    {
        throw new InvalidOperationException($"Collection export maxRecords cannot exceed {CollectionSnapshotLimits.MaxRecords}.");
    }
}

static void ValidateGraphImportRequest(VyralGraphCollectionImportRequest request)
{
    if (request.Envelope is null)
    {
        throw new InvalidOperationException("Graph import request must include an envelope.");
    }

    var recordCount = 1 +
        (request.Envelope.Nodes?.Count ?? 0) +
        (request.Envelope.Edges?.Count ?? 0) +
        (request.Envelope.Assertions?.Count ?? 0) +
        (request.Envelope.Reviews?.Count ?? 0) +
        (request.Envelope.Projections?.Count ?? 0);
    if (recordCount > VyralGraphCollectionLimits.MaxRecords)
    {
        throw new InvalidOperationException($"Graph import supports at most {VyralGraphCollectionLimits.MaxRecords} collection records.");
    }
}

static string? GetProviderArtifactDirectory(IConfiguration configuration)
{
    return EmptyToNull(configuration["Providers:ArtifactDirectory"] ?? string.Empty);
}

static async Task PersistProviderTraceAsync(ProviderRunRequest request, ProviderRunResult result, ITraceStore traces, CancellationToken ct, string? jobId = null)
{
    var traceEvent = result.Trace ??= new ProviderTraceEvent
    {
        Provider = result.Provider,
        Capability = result.Capability,
        Operation = result.Operation,
        Mode = result.Mode,
        ModelId = request.ModelId,
        InputHash = ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options)),
        FailureClass = result.FailureClass
    };

    if (string.IsNullOrWhiteSpace(traceEvent.TraceId))
    {
        traceEvent.TraceId = Guid.NewGuid().ToString("N");
    }

    var trace = new TraceRecord
    {
        Id = traceEvent.TraceId,
        Operation = "provider.run",
        Adapter = result.Provider,
        StartedAt = traceEvent.Timestamp == default ? DateTime.UtcNow : traceEvent.Timestamp,
        DurationMs = traceEvent.DurationMs,
        Request = new Dictionary<string, object?>
        {
            ["provider"] = result.Provider,
            ["capability"] = request.Capability,
            ["operation"] = request.Operation,
            ["mode"] = request.Mode,
            ["modelId"] = request.ModelId,
            ["correlationId"] = request.CorrelationId,
            ["jobId"] = jobId,
            ["contextRefs"] = request.ContextRefs,
            ["timeoutSeconds"] = request.TimeoutSeconds,
            ["maxOutputBytes"] = request.MaxOutputBytes,
            ["payloadHash"] = traceEvent.InputHash ?? ProviderHash.Sha256(request.Payload.ToJsonString(ProviderJson.Options))
        },
        ResultSummary = new Dictionary<string, object?>
        {
            ["status"] = result.Status.ToString(),
            ["failureClass"] = result.FailureClass,
            ["providerStatus"] = result.ProviderStatus,
            ["inputHash"] = traceEvent.InputHash,
            ["outputHash"] = traceEvent.OutputHash,
            ["modelId"] = traceEvent.ModelId,
            ["adapterId"] = traceEvent.AdapterId,
            ["configHash"] = traceEvent.ConfigHash,
            ["authorityBoundary"] = traceEvent.AuthorityBoundary,
            ["artifactRefs"] = traceEvent.ArtifactRefs
        }
    };

    await traces.WriteTraceAsync(trace, ct);
}

static async Task<IReadOnlyList<ProviderQualification>> QualifyProviderAsync(IProviderTarget target, ProviderQualificationRequest request, IConfiguration configuration, ITraceStore traces, ProviderRunGuard guard, CancellationToken ct)
{
    if (target is not IProviderQualificationPlanner planner)
    {
        var staticQualifications = FilterQualifications(ProviderQualificationBuilder.Describe(target), request.Capability);
        foreach (var qualification in staticQualifications)
        {
            await PersistProviderQualificationTraceAsync(target, qualification, request.Mode, traces, ct);
        }

        return staticQualifications;
    }

    var capabilities = target.Capabilities
        .Where(capability => string.IsNullOrWhiteSpace(request.Capability) || string.Equals(capability.Id, request.Capability, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (capabilities.Count == 0)
    {
        return Array.Empty<ProviderQualification>();
    }

    var qualificationRequests = planner.CreateQualificationRequests(request);
    var results = new List<ProviderRunResult>(qualificationRequests.Count);
    foreach (var qualificationRequest in qualificationRequests)
    {
        qualificationRequest.Provider = target.Profile.Id;
        qualificationRequest.ArtifactDirectory = GetProviderArtifactDirectory(configuration);
        var result = await RunProviderWithGuardAsync(target, qualificationRequest, guard, ct);
        await PersistProviderTraceAsync(qualificationRequest, result, traces, ct);
        results.Add(result);
    }

    var qualifications = capabilities
        .Select(capability =>
        {
            var result = results.FirstOrDefault(item => string.Equals(item.Capability, capability.Id, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                return ProviderQualificationBuilder.Create(target, capability, ProviderQualificationStatuses.Unsupported);
            }

            var status = result.Status == ProviderRunStatus.Succeeded
                ? ProviderQualificationStatuses.Validated
                : ProviderQualificationStatuses.Failed;
            var evidenceRefs = new List<string>();
            var resultTrace = result.Trace;
            if (!string.IsNullOrWhiteSpace(resultTrace?.TraceId))
            {
                evidenceRefs.Add($"trace:{resultTrace.TraceId}");
            }

            if (!string.IsNullOrWhiteSpace(resultTrace?.InputHash))
            {
                evidenceRefs.Add($"input:{resultTrace.InputHash}");
            }

            if (!string.IsNullOrWhiteSpace(resultTrace?.OutputHash))
            {
                evidenceRefs.Add($"output:{resultTrace.OutputHash}");
            }

            return ProviderQualificationBuilder.Create(
                target,
                capability,
                status,
                status == ProviderQualificationStatuses.Validated ? DateTime.UtcNow : null,
                evidenceRefs);
        })
        .OrderBy(qualification => qualification.Capability, StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var qualification in qualifications)
    {
        await PersistProviderQualificationTraceAsync(target, qualification, request.Mode, traces, ct);
    }

    return qualifications;
}

static async Task<ProviderRunResult> RunProviderWithGuardAsync(IProviderTarget target, ProviderRunRequest request, ProviderRunGuard guard, CancellationToken ct)
{
    await using var admission = await guard.TryEnterAsync(target.Profile.Id, request, ct);
    if (!admission.Accepted)
    {
        return admission.RejectionResult!;
    }

    try
    {
        return await target.RunAsync(request, admission.CancellationToken);
    }
    catch (OperationCanceledException) when (admission.TimedOut && !ct.IsCancellationRequested)
    {
        return guard.CreateTimeoutResult(target.Profile.Id, request);
    }
}

static IReadOnlyList<ProviderQualification> FilterQualifications(IReadOnlyList<ProviderQualification> qualifications, string? capability)
{
    return string.IsNullOrWhiteSpace(capability)
        ? qualifications
        : qualifications.Where(qualification => string.Equals(qualification.Capability, capability, StringComparison.OrdinalIgnoreCase)).ToList();
}

static async Task<IReadOnlyList<ProviderQualification>?> GetProviderQualificationsAsync(string provider, ProviderTargetRegistry registry, ITraceStore traces, CancellationToken ct)
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return null;
    }

    var qualifications = ProviderQualificationBuilder.Describe(target).ToList();
    var history = (await traces.ListTracesAsync("provider.qualification", limit: 500, ct))
        .Where(trace => string.Equals(trace.Adapter, target.Profile.Id, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(GetTraceFreshness)
        .ToList();
    var runHistory = (await traces.ListTracesAsync("provider.run", limit: 500, ct))
        .Where(trace => string.Equals(trace.Adapter, target.Profile.Id, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(GetTraceFreshness)
        .ToList();

    foreach (var qualification in qualifications)
    {
        var latest = history.FirstOrDefault(trace =>
            string.Equals(GetTraceValue(trace.Request, "capability"), qualification.Capability, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetTraceValue(trace.ResultSummary, "configHash"), qualification.ConfigHash, StringComparison.Ordinal));
        var latestSuccessfulRun = runHistory.FirstOrDefault(trace =>
            string.Equals(GetTraceValue(trace.Request, "capability"), qualification.Capability, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetTraceValue(trace.ResultSummary, "configHash"), qualification.ConfigHash, StringComparison.Ordinal) &&
            string.Equals(GetTraceValue(trace.ResultSummary, "status"), ProviderRunStatus.Succeeded.ToString(), StringComparison.OrdinalIgnoreCase));

        if (latest is null ||
            (latestSuccessfulRun is not null &&
                !string.Equals(GetTraceValue(latest.ResultSummary, "status"), ProviderQualificationStatuses.Validated, StringComparison.OrdinalIgnoreCase) &&
                GetTraceFreshness(latestSuccessfulRun) > GetTraceFreshness(latest)))
        {
            if (latestSuccessfulRun is null)
            {
                continue;
            }

            qualification.Status = ProviderQualificationStatuses.Validated;
            qualification.LastValidatedAt = latestSuccessfulRun.StartedAt;
            qualification.EvidenceRefs = BuildProviderRunEvidenceRefs(latestSuccessfulRun);
            continue;
        }

        ApplyQualificationTrace(qualification, latest);
    }

    return qualifications;
}

static void ApplyQualificationTrace(ProviderQualification qualification, TraceRecord trace)
{
    qualification.Status = GetTraceValue(trace.ResultSummary, "status") ?? qualification.Status;
    qualification.LastValidatedAt = qualification.Status == ProviderQualificationStatuses.Validated ? GetTraceFreshness(trace) : null;
    qualification.EvidenceRefs = GetTraceList(trace.ResultSummary, "evidenceRefs");
    qualification.DriftTriggers = GetTraceList(trace.ResultSummary, "driftTriggers");
    qualification.UnsupportedFeatures = GetTraceList(trace.ResultSummary, "unsupportedFeatures");
}

static DateTime GetTraceFreshness(TraceRecord trace)
{
    if (trace.StartedAt != default)
    {
        return trace.StartedAt;
    }

    return trace.CreatedAt == default ? DateTime.MinValue : trace.CreatedAt;
}

static List<string> BuildProviderRunEvidenceRefs(TraceRecord trace)
{
    var refs = new List<string> { $"trace:{trace.Id}" };
    var inputHash = GetTraceValue(trace.ResultSummary, "inputHash");
    if (!string.IsNullOrWhiteSpace(inputHash))
    {
        refs.Add($"input:{inputHash}");
    }

    var outputHash = GetTraceValue(trace.ResultSummary, "outputHash");
    if (!string.IsNullOrWhiteSpace(outputHash))
    {
        refs.Add($"output:{outputHash}");
    }

    return refs;
}

static async Task<ProviderReadinessEnvelope?> GetProviderReadinessAsync(string provider, ProviderTargetRegistry registry, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct)
{
    var descriptor = registry.GetDescriptor(provider);
    if (descriptor is null)
    {
        return null;
    }

    var target = registry.GetTarget(provider);
    var doctorFailed = false;
    var commandResolutionFailed = false;
    if (target is IProviderDoctor doctor)
    {
        var diagnosis = await doctor.DiagnoseAsync(ct);
        doctorFailed = diagnosis.Checks.Any(check => string.Equals(check.Status, ProviderDoctorStatuses.Failed, StringComparison.OrdinalIgnoreCase));
        commandResolutionFailed = diagnosis.Checks.Any(check =>
            string.Equals(check.Id, "command.resolution", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(check.Status, ProviderDoctorStatuses.Failed, StringComparison.OrdinalIgnoreCase));
    }

    var qualifications = await GetProviderQualificationsAsync(provider, registry, traces, ct);
    var envelope = new ProviderReadinessEnvelope();
    foreach (var capability in descriptor.Capabilities.OrderBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase))
    {
        var qualification = qualifications?.FirstOrDefault(item => string.Equals(item.Capability, capability.Id, StringComparison.OrdinalIgnoreCase));
        var qualificationStatus = qualification?.Status ?? ProviderQualificationStatuses.Unvalidated;
        var callable = !doctorFailed && capability.Operations.Count > 0 && !string.Equals(qualificationStatus, ProviderQualificationStatuses.Unsupported, StringComparison.OrdinalIgnoreCase);
        var ready = callable && string.Equals(qualificationStatus, ProviderQualificationStatuses.Validated, StringComparison.OrdinalIgnoreCase);

        envelope.Items.Add(new ProviderCapabilityReadiness
        {
            Provider = descriptor.Profile.Id,
            Capability = capability.Id,
            RegistrationStatus = "registered",
            Operations = capability.Operations.ToList(),
            Modes = capability.ModePolicies.Select(policy => policy.Id).OrderBy(mode => mode, StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigHash = qualification?.ConfigHash ?? descriptor.Profile.ConfigHash,
            QualificationStatus = qualificationStatus,
            LastValidatedAt = qualification?.LastValidatedAt,
            Callable = callable,
            Ready = ready,
            CanRunUnvalidated = callable && string.Equals(qualificationStatus, ProviderQualificationStatuses.Unvalidated, StringComparison.OrdinalIgnoreCase),
            Reason = commandResolutionFailed ? "command_not_found" : doctorFailed ? "provider_doctor_failed" : GetProviderReadinessReason(callable, qualificationStatus),
            EvidenceRefs = qualification?.EvidenceRefs.ToList() ?? new List<string>(),
            DriftTriggers = qualification?.DriftTriggers.ToList() ?? new List<string> { "config_hash_changed" },
            UnsupportedFeatures = capability.UnsupportedFeatures
                .Concat(qualification?.UnsupportedFeatures ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(feature => feature, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Local = descriptor.Profile.Local,
            RequiresNetwork = descriptor.Profile.RequiresNetwork,
            Auth = descriptor.Profile.Auth,
            AuthRequired = access.Enabled,
            OperationalLimits = guard.ToOperationalLimits(access.Enabled)
        });
    }

    return envelope;
}

static async Task<ProviderDoctorResult?> DiagnoseProviderAsync(string provider, ProviderTargetRegistry registry, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct)
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return null;
    }

    var result = target is IProviderDoctor doctor
        ? await doctor.DiagnoseAsync(ct)
        : CreateGenericProviderDoctorResult(target);

    await AddModelCatalogDoctorCheckAsync(target, result, ct);
    await AddReadinessDoctorChecksAsync(target, result, registry, traces, guard, access, ct);
    result.Status = ProviderDoctorStatuses.Aggregate(result.Checks);
    result.Summary = result.Status switch
    {
        ProviderDoctorStatuses.Ok => "Provider passed local doctor checks.",
        ProviderDoctorStatuses.Warning => "Provider is registered but has warnings to review.",
        ProviderDoctorStatuses.Failed => "Provider failed one or more doctor checks.",
        _ => "Provider doctor state is unknown."
    };
    return result;
}

static ProviderDoctorResult CreateGenericProviderDoctorResult(IProviderTarget target)
{
    return new ProviderDoctorResult
    {
        Provider = target.Profile.Id,
        Checks = new List<ProviderDoctorCheck>
        {
            new()
            {
                Id = "provider.registration",
                Status = ProviderDoctorStatuses.Ok,
                Message = "Provider is registered with the local server.",
                Details = new Dictionary<string, object?>
                {
                    ["family"] = target.Profile.Family,
                    ["local"] = target.Profile.Local,
                    ["requiresNetwork"] = target.Profile.RequiresNetwork,
                    ["auth"] = target.Profile.Auth
                }
            }
        },
        Metadata = new Dictionary<string, object?>
        {
            ["family"] = target.Profile.Family,
            ["configHash"] = target.Profile.ConfigHash
        }
    };
}

static async Task AddModelCatalogDoctorCheckAsync(IProviderTarget target, ProviderDoctorResult result, CancellationToken ct)
{
    if (target is not IProviderModelCatalog catalog)
    {
        result.Checks.Add(new ProviderDoctorCheck
        {
            Id = "model.catalog",
            Status = ProviderDoctorStatuses.Warning,
            Message = "Provider does not expose model catalog discovery.",
            Details = new Dictionary<string, object?>()
        });
        return;
    }

    var catalogResult = await catalog.ListModelsAsync(ct);
    var status = catalogResult.Status switch
    {
        ProviderModelCatalogStatuses.Succeeded => string.IsNullOrWhiteSpace(catalogResult.DefaultModelId) ? ProviderDoctorStatuses.Warning : ProviderDoctorStatuses.Ok,
        ProviderModelCatalogStatuses.Unsupported => ProviderDoctorStatuses.Warning,
        ProviderModelCatalogStatuses.Failed => ProviderDoctorStatuses.Failed,
        _ => ProviderDoctorStatuses.Unknown
    };

    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "model.catalog",
        Status = status,
        Message = catalogResult.Status == ProviderModelCatalogStatuses.Succeeded
            ? $"Model catalog returned {catalogResult.Items.Count} model(s)."
            : $"Model catalog status is {catalogResult.Status}.",
        Details = new Dictionary<string, object?>
        {
            ["catalogStatus"] = catalogResult.Status,
            ["source"] = catalogResult.Source,
            ["defaultModelId"] = catalogResult.DefaultModelId,
            ["modelIds"] = catalogResult.Items.Select(model => model.Id).ToList(),
            ["failureClass"] = catalogResult.FailureClass,
            ["providerStatus"] = catalogResult.ProviderStatus
        }
    });
}

static async Task<ProviderQuotaResult?> GetProviderQuotaAsync(string provider, ProviderTargetRegistry registry, CancellationToken ct)
{
    var target = registry.GetTarget(provider);
    if (target is null)
    {
        return null;
    }

    if (target is not IProviderQuotaReporter quotaReporter)
    {
        return ProviderQuotaResult.Unsupported(provider);
    }

    return await quotaReporter.GetQuotaAsync(ct);
}

static async Task AddReadinessDoctorChecksAsync(IProviderTarget target, ProviderDoctorResult result, ProviderTargetRegistry registry, ITraceStore traces, ProviderRunGuard guard, ServerAccessOptions access, CancellationToken ct)
{
    var readiness = await GetProviderReadinessAsync(target.Profile.Id, registry, traces, guard, access, ct);
    if (readiness is null)
    {
        result.Checks.Add(new ProviderDoctorCheck
        {
            Id = "readiness",
            Status = ProviderDoctorStatuses.Failed,
            Message = "Provider readiness could not be resolved.",
            Details = new Dictionary<string, object?>()
        });
        return;
    }

    foreach (var item in readiness.Items)
    {
        result.Checks.Add(new ProviderDoctorCheck
        {
            Id = $"readiness.{item.Capability}",
            Status = item.Ready ? ProviderDoctorStatuses.Ok : item.Callable ? ProviderDoctorStatuses.Warning : ProviderDoctorStatuses.Failed,
            Message = item.Ready
                ? $"Capability {item.Capability} is validated."
                : item.Callable
                    ? $"Capability {item.Capability} is callable but {item.Reason}."
                    : $"Capability {item.Capability} is not callable: {item.Reason}.",
            Details = new Dictionary<string, object?>
            {
                ["capability"] = item.Capability,
                ["qualificationStatus"] = item.QualificationStatus,
                ["callable"] = item.Callable,
                ["ready"] = item.Ready,
                ["canRunUnvalidated"] = item.CanRunUnvalidated,
                ["modes"] = item.Modes,
                ["operations"] = item.Operations,
                ["evidenceRefs"] = item.EvidenceRefs
            }
        });
    }
}

static string GetProviderReadinessReason(bool callable, string qualificationStatus)
{
    if (!callable)
    {
        return "not_callable";
    }

    return qualificationStatus switch
    {
        ProviderQualificationStatuses.Validated => "validated",
        ProviderQualificationStatuses.Failed => "qualification_failed",
        ProviderQualificationStatuses.Unsupported => "unsupported",
        ProviderQualificationStatuses.Unvalidated => "unvalidated",
        _ => qualificationStatus
    };
}

static async Task PersistProviderQualificationTraceAsync(IProviderTarget target, ProviderQualification qualification, string mode, ITraceStore traces, CancellationToken ct)
{
    var startedAt = qualification.LastValidatedAt ?? DateTime.UtcNow;
    var trace = new TraceRecord
    {
        Operation = "provider.qualification",
        Adapter = target.Profile.Id,
        StartedAt = startedAt,
        DurationMs = 0,
        Request = new Dictionary<string, object?>
        {
            ["provider"] = target.Profile.Id,
            ["capability"] = qualification.Capability,
            ["mode"] = string.IsNullOrWhiteSpace(mode) ? "mechanics" : mode,
            ["configHash"] = qualification.ConfigHash
        },
        ResultSummary = new Dictionary<string, object?>
        {
            ["status"] = qualification.Status,
            ["configHash"] = qualification.ConfigHash,
            ["operationSet"] = qualification.OperationSet,
            ["driftTriggers"] = qualification.DriftTriggers,
            ["unsupportedFeatures"] = qualification.UnsupportedFeatures,
            ["evidenceRefs"] = qualification.EvidenceRefs
        }
    };

    await traces.WriteTraceAsync(trace, ct);
}

static string? GetTraceValue(IReadOnlyDictionary<string, object?> values, string key)
{
    if (!values.TryGetValue(key, out var value) || value is null)
    {
        return null;
    }

    return value switch
    {
        string text => text,
        JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
        JsonElement json when json.ValueKind == JsonValueKind.Null => null,
        JsonElement json => json.ToString(),
        _ => value.ToString()
    };
}

static List<string> GetTraceList(IReadOnlyDictionary<string, object?> values, string key)
{
    if (!values.TryGetValue(key, out var value) || value is null)
    {
        return new List<string>();
    }

    if (value is IEnumerable<string> items)
    {
        return items.ToList();
    }

    if (value is JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.Array => json.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToList(),
            JsonValueKind.String => new List<string> { json.GetString()! },
            _ => new List<string>()
        };
    }

    return new List<string> { value.ToString()! };
}

static async Task<IRecordCollectionStore> CreateRecordStoreAsync(ServerStorageOptions options)
{
    switch (options.RecordStore)
    {
        case ServerStorageBackendIds.Sqlite:
        {
            var store = new SqliteRecordCollectionStore(options.DatabasePath);
            await store.InitializeAsync();
            return store;
        }
        case ServerStorageBackendIds.GoogleFirestore:
            return new FirestoreRecordCollectionStore(CreateFirestoreDb(options), options.GoogleFirestoreRootCollection);
        case ServerStorageBackendIds.GoogleAlloyDb:
        {
            if (string.IsNullOrWhiteSpace(options.GoogleAlloyDbConnectionString))
            {
                throw new InvalidOperationException("Google AlloyDB record store requires Google:AlloyDb:ConnectionString or VYRAL_ALLOYDB_CONNECTION_STRING.");
            }

            var store = new AlloyDbRecordCollectionStore(options.GoogleAlloyDbConnectionString);
            await store.InitializeAsync();
            return store;
        }
        default:
            throw new InvalidOperationException($"Record store backend '{options.RecordStore}' is not supported by this server.");
    }
}

static async Task<ITraceStore> CreateTraceStoreAsync(ServerStorageOptions options)
{
    switch (options.TraceStore)
    {
        case ServerStorageBackendIds.Sqlite:
        {
            var traceStore = new SqliteTraceStore(options.DatabasePath);
            await traceStore.InitializeAsync();
            return traceStore;
        }
        case ServerStorageBackendIds.GoogleFirestore:
            return new FirestoreTraceStore(CreateFirestoreDb(options), options.GoogleFirestoreRootCollection);
        default:
            throw new InvalidOperationException($"Trace store backend '{options.TraceStore}' is not supported by this server.");
    }
}

static IObjectStore CreateObjectStore(ServerStorageOptions options)
{
    return options.ObjectStore switch
    {
        ServerStorageBackendIds.File => new FileObjectStore(options.ObjectsPath),
        ServerStorageBackendIds.GoogleCloudStorage => new CloudStorageObjectStore(StorageClient.Create()),
        ServerStorageBackendIds.CloudflareR2 => R2ObjectStore.Create(new CloudflareR2Options
        {
            AccountId = options.CloudflareAccountId,
            AccessKeyId = options.CloudflareR2AccessKeyId,
            SecretAccessKey = options.CloudflareR2SecretAccessKey,
            ServiceUrl = options.CloudflareR2ServiceUrl
        }),
        _ => throw new InvalidOperationException($"Object store backend '{options.ObjectStore}' is not supported by this server.")
    };
}

static ICanonicalStore CreateCanonicalStore(CanonicalStoreOptions options)
{
    return options.Provider switch
    {
        CanonicalStoreOptions.LocalSqlite => new SqliteCanonicalStore(options.DatabasePath),
        CanonicalStoreOptions.PostgreSql => new PostgresCanonicalStore(options.ConnectionString!),
        CanonicalStoreOptions.MySql => new MySqlCanonicalStore(options.ConnectionString!),
        _ => throw new InvalidOperationException($"Canonical store provider '{options.Provider}' is not supported by this server.")
    };
}

static CanonicalPreflightReport BuildCanonicalPreflightReport(
    ICanonicalStore canonicalStore,
    CanonicalStoreOptions canonicalOptions,
    CanonicalRateLimitOptions canonicalRateLimit,
    VyralCanonicalAccess canonicalAccess,
    int migrationReceiptCount,
    CanonicalDataPlanePreflightResult? dataPlaneProbe = null) => new()
{
    Store = canonicalStore.GetType().Name,
    Provider = canonicalOptions.Provider,
    AuthenticationMode = canonicalAccess.Options.AuthenticationMode,
    TenantPoliciesEnforced = canonicalAccess.Enabled,
    IdentityPolicyCount = canonicalAccess.Options.IdentityPolicies.Count,
    MigrationReceiptCount = migrationReceiptCount,
    RateLimitPermitLimit = canonicalRateLimit.PermitLimit,
    RateLimitWindowSeconds = canonicalRateLimit.WindowSeconds,
    RequiredOperations = [CanonicalAccessOperations.Read, CanonicalAccessOperations.Write, CanonicalAccessOperations.Dispatch, CanonicalAccessOperations.Export, CanonicalAccessOperations.Restore, CanonicalAccessOperations.Admin],
    DataPlaneProbe = dataPlaneProbe
};

static void RequireCanonicalTenantMatch(string routeTenantId, string requestTenantId, string requestName)
{
    CanonicalContractValidator.ValidateTenantId(routeTenantId);
    if (!string.Equals(routeTenantId, requestTenantId?.Trim(), StringComparison.Ordinal))
        throw new InvalidOperationException($"Canonical {requestName} tenant id must match the route tenant id.");
}

// CanonicalStore deliberately accepts consumer-owned document types. The server still validates
// Vyral-owned typed artifacts before committing them, so an HTTP caller cannot bypass the
// EvidenceBrief schema by posting a raw generic canonical transaction.
static void ValidateCanonicalEvidenceBriefDocuments(CanonicalTransactionRequest request)
{
    foreach (var mutation in request.Mutations)
    {
        if (mutation.Operation == CanonicalMutationOperations.Upsert &&
            string.Equals(mutation.Document?.DocumentType, EvidenceBriefContract.CanonicalDocumentType, StringComparison.Ordinal))
        {
            _ = EvidenceBriefContract.FromCanonicalDocument(mutation.Document!);
        }
    }
}

static FirestoreDb CreateFirestoreDb(ServerStorageOptions options)
{
    if (string.IsNullOrWhiteSpace(options.GoogleProjectId))
    {
        throw new InvalidOperationException("Google Firestore store requires Google:ProjectId, Google:Firestore:ProjectId, GOOGLE_CLOUD_PROJECT, or VYRAL_GCP_PROJECT_ID.");
    }

    var builder = new FirestoreDbBuilder
    {
        ProjectId = options.GoogleProjectId
    };
    if (!string.IsNullOrWhiteSpace(options.GoogleFirestoreDatabaseId))
    {
        builder.DatabaseId = options.GoogleFirestoreDatabaseId;
    }

    return builder.Build();
}

static ServerHealthStatus BuildServerHealth(
    IRecordCollectionStore recordStore,
    IObjectStore objectStore,
    ITraceStore traceStore,
    ICanonicalStore? canonicalStore,
    IEmbeddingProvider embeddingProvider,
    ServerAccessOptions access,
    ProviderRunGuard guard,
    ExecutionRuntimeProviderRunJobAdapter jobs)
{
    return new ServerHealthStatus
    {
        Storage = new ServerStorageStatus
        {
            RecordStore = recordStore.GetType().Name,
            ObjectStore = objectStore.GetType().Name,
            TraceStore = traceStore.GetType().Name,
            CanonicalStore = canonicalStore?.GetType().Name ?? "disabled"
        },
        Embedding = new ServerEmbeddingStatus
        {
            Provider = embeddingProvider.ProviderId,
            ModelId = embeddingProvider.ModelId,
            Dimensions = embeddingProvider.Dimensions,
            Runtime = GetEmbeddingRuntime(embeddingProvider)
        },
        Security = new ServerSecurityStatus
        {
            ApiKeyRequired = access.Enabled,
            ApiKeyHeader = access.ApiKeyHeader,
            ProviderRunLimits = BuildProviderOperationalLimits(access, guard, jobs)
        }
    };
}

static async Task<ServerReadinessReport> BuildServerReadinessReportAsync(
    IRecordCollectionStore recordStore,
    IObjectStore objectStore,
    ITraceStore traceStore,
    ICanonicalStore? canonicalStore,
    IEmbeddingProvider embeddingProvider,
    EmbeddingProviderRegistry embeddingRegistry,
    ProviderTargetRegistry providerRegistry,
    ServerAccessOptions access,
    ProviderRunGuard guard,
    ExecutionRuntimeProviderRunJobAdapter jobs,
    ServerStorageOptions storageOptions,
    EmbeddingProviderOptions embeddingOptions,
    bool includeExceptionDetails,
    CancellationToken ct)
{
    var report = new ServerReadinessReport
    {
        Health = BuildServerHealth(recordStore, objectStore, traceStore, canonicalStore, embeddingProvider, access, guard, jobs),
        OperationalLimits = BuildProviderOperationalLimits(access, guard, jobs)
    };

    await AddStorageReadinessChecksAsync(report, recordStore, objectStore, traceStore, canonicalStore, storageOptions, includeExceptionDetails, ct);
    AddSecurityReadinessCheck(report, access);
    AddProviderLimitReadinessCheck(report, guard, jobs, access);
    AddEmbeddingReadinessChecks(report, embeddingProvider, embeddingRegistry, embeddingOptions);
    await AddProviderReadinessChecksAsync(report, providerRegistry, traceStore, guard, access, ct);

    report.Blockers = report.Checks
        .Where(check => string.Equals(check.Status, ProviderDoctorStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        .Select(check => check.Message)
        .Distinct(StringComparer.Ordinal)
        .ToList();
    report.Warnings = report.Checks
        .Where(check => string.Equals(check.Status, ProviderDoctorStatuses.Warning, StringComparison.OrdinalIgnoreCase))
        .Select(check => check.Message)
        .Distinct(StringComparer.Ordinal)
        .ToList();
    report.Status = AggregateReadinessStatus(report.Checks);
    report.Ready = !string.Equals(report.Status, ProviderDoctorStatuses.Failed, StringComparison.OrdinalIgnoreCase);
    report.Summary = report.Status switch
    {
        ProviderDoctorStatuses.Ok => "Local Vyral server is ready for consumer development.",
        ProviderDoctorStatuses.Warning => "Local Vyral server is usable, with warnings consumers should review before beta handoff.",
        ProviderDoctorStatuses.Failed => "Local Vyral server has blockers that should be resolved before consumer handoff.",
        _ => "Local Vyral server readiness is unknown."
    };

    return report;
}

static async Task AddStorageReadinessChecksAsync(
    ServerReadinessReport report,
    IRecordCollectionStore recordStore,
    IObjectStore objectStore,
    ITraceStore traceStore,
    ICanonicalStore? canonicalStore,
    ServerStorageOptions storageOptions,
    bool includeExceptionDetails,
    CancellationToken ct)
{
    try
    {
        var collections = (await recordStore.GetCollectionsAsync(ct)).ToList();
        var details = new Dictionary<string, object?>
        {
            ["store"] = recordStore.GetType().Name,
            ["collectionCount"] = collections.Count
        };
        var status = ProviderDoctorStatuses.Ok;
        var message = "Record store is reachable.";
        if (recordStore is SqliteRecordCollectionStore sqliteStore)
        {
            var sqlite = await sqliteStore.GetStorageDiagnosticsAsync(ct);
            details["sqlite"] = sqlite;
            if (!sqlite.Healthy)
            {
                status = ProviderDoctorStatuses.Failed;
                message = "Record store SQLite diagnostics failed.";
            }
        }

        AddReadinessCheck(report, "storage.records", status, message, details);
    }
    catch (Exception ex)
    {
        AddReadinessCheck(report, "storage.records", ProviderDoctorStatuses.Failed, "Record store is not reachable.", ExceptionDetails(ex, includeExceptionDetails));
    }

    if (canonicalStore is null)
    {
        AddReadinessCheck(report, "storage.canonical", ProviderDoctorStatuses.Ok, "Canonical store is disabled for this server.", new Dictionary<string, object?>
        {
            ["enabled"] = false
        });
    }
    else try
    {
        var migrations = await canonicalStore.ListMigrationsAsync(ct);
        AddReadinessCheck(report, "storage.canonical", ProviderDoctorStatuses.Ok, "Canonical store is reachable.", new Dictionary<string, object?>
        {
            ["store"] = canonicalStore.GetType().Name,
            ["migrationCount"] = migrations.Count
        });
    }
    catch (Exception ex)
    {
        AddReadinessCheck(report, "storage.canonical", ProviderDoctorStatuses.Failed, "Canonical store is not reachable.", ExceptionDetails(ex, includeExceptionDetails));
    }

    await AddObjectStoreReadinessCheckAsync(report, objectStore, storageOptions, includeExceptionDetails, ct);

    try
    {
        var summary = await traceStore.SummarizeTracesAsync(ct: ct);
        AddReadinessCheck(report, "storage.traces", ProviderDoctorStatuses.Ok, "Trace store is reachable.", new Dictionary<string, object?>
        {
            ["store"] = traceStore.GetType().Name,
            ["totalCount"] = summary.TotalCount
        });
    }
    catch (Exception ex)
    {
        AddReadinessCheck(report, "storage.traces", ProviderDoctorStatuses.Failed, "Trace store is not reachable.", ExceptionDetails(ex, includeExceptionDetails));
    }
}

static async Task AddObjectStoreReadinessCheckAsync(
    ServerReadinessReport report,
    IObjectStore objectStore,
    ServerStorageOptions storageOptions,
    bool includeExceptionDetails,
    CancellationToken ct)
{
    var probeContainer = storageOptions.ObjectProbeContainer;
    var probeKey = $"probes/{Guid.NewGuid():N}.txt";
    var probeBytes = System.Text.Encoding.UTF8.GetBytes("vyral-object-store-readiness");
    ObjectInfo? write = null;

    try
    {
        await using (var content = new MemoryStream(probeBytes))
        {
            write = await objectStore.PutObjectAsync(new ObjectWriteRequest
            {
                Container = probeContainer,
                Key = probeKey,
                Content = content,
                ContentType = "text/plain",
                Metadata = new Dictionary<string, string>
                {
                    ["purpose"] = "readiness"
                },
                IfNoneMatch = "*"
            }, ct);
        }

        var read = await objectStore.GetObjectAsync(new ObjectReadRequest
        {
            Container = probeContainer,
            Key = probeKey
        }, ct);
        if (read is null)
        {
            throw new InvalidOperationException("Object store readiness probe wrote an object, but could not read it back.");
        }

        await using (read.Content)
        await using (var buffer = new MemoryStream())
        {
            await read.Content.CopyToAsync(buffer, ct);
            if (!buffer.ToArray().SequenceEqual(probeBytes))
            {
                throw new InvalidOperationException("Object store readiness probe content did not round trip.");
            }
        }

        await objectStore.DeleteObjectAsync(new ObjectDeleteRequest
        {
            Container = probeContainer,
            Key = probeKey,
            IfMatch = write.Etag
        }, ct);

        var details = new Dictionary<string, object?>
        {
            ["store"] = objectStore.GetType().Name,
            ["probe"] = "write_read_delete",
            ["container"] = probeContainer,
            ["contentLength"] = write.ContentLength,
            ["contentHash"] = write.ContentHash
        };
        var status = ProviderDoctorStatuses.Ok;
        var message = "Object store is reachable.";
        if (objectStore is FileObjectStore fileStore)
        {
            var filesystem = await fileStore.GetStorageDiagnosticsAsync(ct);
            details["filesystem"] = filesystem;
            if (!filesystem.Healthy)
            {
                status = ProviderDoctorStatuses.Warning;
                message = "Object store is reachable, with filesystem drift to review.";
            }
        }

        AddReadinessCheck(report, "storage.objects", status, message, details);
    }
    catch (Exception ex)
    {
        try
        {
            await objectStore.DeleteObjectAsync(new ObjectDeleteRequest
            {
                Container = probeContainer,
                Key = probeKey,
                IfMatch = write?.Etag
            }, ct);
        }
        catch
        {
            // Best-effort cleanup after a failed readiness probe.
        }

        AddReadinessCheck(report, "storage.objects", ProviderDoctorStatuses.Failed, "Object store is not reachable.", ExceptionDetails(ex, includeExceptionDetails));
    }
}

static void AddSecurityReadinessCheck(ServerReadinessReport report, ServerAccessOptions access)
{
    AddReadinessCheck(
        report,
        "security.api_key",
        access.Enabled ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Warning,
        access.Enabled
            ? "API key authentication is required for protected routes."
            : "API key authentication is disabled; use this posture only on trusted localhost development networks.",
        new Dictionary<string, object?>
        {
            ["apiKeyRequired"] = access.Enabled,
            ["apiKeyHeader"] = access.ApiKeyHeader,
            ["publicPaths"] = access.PublicPaths.ToList()
        });
}

static void AddProviderLimitReadinessCheck(
    ServerReadinessReport report,
    ProviderRunGuard guard,
    ExecutionRuntimeProviderRunJobAdapter jobs,
    ServerAccessOptions access)
{
    var limits = BuildProviderOperationalLimits(access, guard, jobs);
    var valid = guard.Options.MaxConcurrentRuns > 0 &&
        guard.Options.MaxRunsPerWindow > 0 &&
        guard.Options.DefaultTimeoutSeconds > 0 &&
        guard.Options.MaxTimeoutSeconds >= guard.Options.DefaultTimeoutSeconds &&
        guard.Options.MaxOutputBytes > 0 &&
        jobs.Options.MaxActiveJobs > 0;

    AddReadinessCheck(report, "providers.limits", valid ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Failed,
        valid ? "Provider run limits are configured." : "Provider run limits are invalid.",
        limits);
}

static void AddEmbeddingReadinessChecks(
    ServerReadinessReport report,
    IEmbeddingProvider embeddingProvider,
    EmbeddingProviderRegistry embeddingRegistry,
    EmbeddingProviderOptions embeddingOptions)
{
    var doctor = DiagnoseEmbeddingProvider(embeddingProvider, embeddingRegistry, embeddingOptions);
    report.Embedding = new Dictionary<string, object?>
    {
        ["provider"] = embeddingProvider.ProviderId,
        ["modelId"] = embeddingProvider.ModelId,
        ["dimensions"] = embeddingProvider.Dimensions,
        ["status"] = doctor.Status,
        ["runtime"] = GetEmbeddingRuntime(embeddingProvider)
    };

    foreach (var check in doctor.Checks)
    {
        AddReadinessCheck(report, check.Id, check.Status, check.Message, check.Details);
    }
}

static async Task AddProviderReadinessChecksAsync(
    ServerReadinessReport report,
    ProviderTargetRegistry providerRegistry,
    ITraceStore traces,
    ProviderRunGuard guard,
    ServerAccessOptions access,
    CancellationToken ct)
{
    var profiles = providerRegistry.GetProfiles();
    var readinessItems = new List<ProviderCapabilityReadiness>();
    foreach (var profile in profiles)
    {
        var readiness = await GetProviderReadinessAsync(profile.Id, providerRegistry, traces, guard, access, ct);
        if (readiness != null)
        {
            readinessItems.AddRange(readiness.Items);
        }
    }

    report.Providers = new ServerProviderReadinessSummary
    {
        ProviderCount = profiles.Count,
        CapabilityCount = readinessItems.Count,
        CallableCapabilityCount = readinessItems.Count(item => item.Callable),
        ReadyCapabilityCount = readinessItems.Count(item => item.Ready),
        UnvalidatedCapabilityCount = readinessItems.Count(item => string.Equals(item.QualificationStatus, ProviderQualificationStatuses.Unvalidated, StringComparison.OrdinalIgnoreCase)),
        NetworkProviderCount = profiles.Count(profile => profile.RequiresNetwork),
        AuthProviderCount = profiles.Count(profile => !string.Equals(profile.Auth, "none", StringComparison.OrdinalIgnoreCase))
    };

    var status = report.Providers.ProviderCount == 0 || report.Providers.CallableCapabilityCount == 0
        ? ProviderDoctorStatuses.Failed
        : report.Providers.ReadyCapabilityCount == 0
            ? ProviderDoctorStatuses.Warning
            : ProviderDoctorStatuses.Ok;
    var message = status switch
    {
        ProviderDoctorStatuses.Failed => "No callable provider capabilities are registered.",
        ProviderDoctorStatuses.Warning => "Provider capabilities are callable but not qualified as ready.",
        _ => "At least one provider capability is qualified as ready."
    };

    AddReadinessCheck(report, "providers.readiness", status, message, new Dictionary<string, object?>
    {
        ["providerCount"] = report.Providers.ProviderCount,
        ["capabilityCount"] = report.Providers.CapabilityCount,
        ["callableCapabilityCount"] = report.Providers.CallableCapabilityCount,
        ["readyCapabilityCount"] = report.Providers.ReadyCapabilityCount,
        ["unvalidatedCapabilityCount"] = report.Providers.UnvalidatedCapabilityCount,
        ["networkProviderCount"] = report.Providers.NetworkProviderCount,
        ["authProviderCount"] = report.Providers.AuthProviderCount
    });
}

static Dictionary<string, object?> BuildProviderOperationalLimits(
    ServerAccessOptions access,
    ProviderRunGuard guard,
    ExecutionRuntimeProviderRunJobAdapter jobs)
{
    var providerRunLimits = guard.ToOperationalLimits(access.Enabled);
    providerRunLimits["maxActiveJobs"] = jobs.Options.MaxActiveJobs;
    providerRunLimits["maxRetainedTerminalJobs"] = jobs.Options.MaxRetainedTerminalJobs;
    providerRunLimits["defaultJobListLimit"] = jobs.Options.DefaultListLimit;
    providerRunLimits["maxJobListLimit"] = jobs.Options.MaxListLimit;
    providerRunLimits["jobPersistence"] = jobs.PersistenceKind;
    return providerRunLimits;
}

static void AddReadinessCheck(
    ServerReadinessReport report,
    string id,
    string status,
    string message,
    Dictionary<string, object?>? details = null)
{
    report.Checks.Add(new ServerReadinessCheck
    {
        Id = id,
        Status = status,
        Message = message,
        Details = details ?? new Dictionary<string, object?>()
    });
}

static string AggregateReadinessStatus(IEnumerable<ServerReadinessCheck> checks)
{
    var statuses = checks.Select(check => check.Status).ToList();
    if (statuses.Any(status => string.Equals(status, ProviderDoctorStatuses.Failed, StringComparison.OrdinalIgnoreCase)))
    {
        return ProviderDoctorStatuses.Failed;
    }

    if (statuses.Any(status => string.Equals(status, ProviderDoctorStatuses.Warning, StringComparison.OrdinalIgnoreCase)))
    {
        return ProviderDoctorStatuses.Warning;
    }

    return statuses.Count == 0 ? ProviderDoctorStatuses.Unknown : ProviderDoctorStatuses.Ok;
}

static Dictionary<string, object?> ExceptionDetails(Exception ex, bool includeDetails)
{
    if (!includeDetails)
    {
        return new Dictionary<string, object?>();
    }

    return new Dictionary<string, object?>
    {
        ["type"] = ex.GetType().Name,
        ["message"] = ex.Message
    };
}

static Dictionary<string, object?>? GetEmbeddingRuntime(IEmbeddingProvider embeddingProvider)
{
    if (embeddingProvider is not OnnxTransformerEmbeddingProvider onnxProvider)
    {
        return null;
    }

    var runtime = new Dictionary<string, object?>
    {
        ["activeExecutionProvider"] = onnxProvider.ActiveExecutionProvider
    };

    if (onnxProvider.IntraOpNumThreads.HasValue)
    {
        runtime["intraOpNumThreads"] = onnxProvider.IntraOpNumThreads.Value;
    }

    if (onnxProvider.InterOpNumThreads.HasValue)
    {
        runtime["interOpNumThreads"] = onnxProvider.InterOpNumThreads.Value;
    }

    if (onnxProvider.CudaMemoryLimitMb.HasValue)
    {
        runtime["cudaMemoryLimitMb"] = onnxProvider.CudaMemoryLimitMb.Value;
    }

    if (!string.IsNullOrWhiteSpace(onnxProvider.ExecutionProviderFallbackReason))
    {
        runtime["fallbackReason"] = onnxProvider.ExecutionProviderFallbackReason;
    }

    return runtime;
}

static EmbeddingProviderGuidance BuildEmbeddingProviderGuidance(EmbeddingProviderDescriptor descriptor)
{
    var provider = descriptor.Provider.ToLowerInvariant();
    var semantic = string.Equals(descriptor.SemanticQuality, "semantic", StringComparison.OrdinalIgnoreCase);
    var lexical = string.Equals(descriptor.SemanticQuality, "lexical", StringComparison.OrdinalIgnoreCase);
    var mechanical = string.Equals(descriptor.SemanticQuality, "mechanical", StringComparison.OrdinalIgnoreCase);
    var isOnnx = provider.StartsWith("onnx-", StringComparison.Ordinal);
    var isBge = provider.Contains("bge", StringComparison.Ordinal);
    var isE5 = provider.Contains("e5", StringComparison.Ordinal);
    var isMiniLm = provider.Contains("minilm", StringComparison.Ordinal) || provider.Contains("multi-qa", StringComparison.Ordinal);
    var guidance = new EmbeddingProviderGuidance
    {
        Provider = descriptor.Provider,
        DisplayName = descriptor.DisplayName,
        SemanticQuality = descriptor.SemanticQuality,
        DefaultDimensions = descriptor.DefaultDimensions,
        HardwareProfile = descriptor.CpuOnly
            ? "cpu-only"
            : "gpu-preferred-with-cpu-fallback",
        RequiresModelFiles = isOnnx,
        RealisticForSemanticRetrieval = semantic,
        DefaultQueryPrefix = descriptor.DefaultQueryPrefix,
        DefaultPassagePrefix = descriptor.DefaultPassagePrefix
    };

    if (mechanical)
    {
        guidance.RecommendedFor.AddRange(new[]
        {
            "deterministic mechanics tests",
            "schema and pipeline smoke tests"
        });
        guidance.Cautions.Add("Not suitable for semantic retrieval quality decisions.");
        guidance.SuggestedRetrievalProfiles.Add(RetrievalProfileIds.RagBaseline);
        guidance.SuggestedEvaluationVariants.Add("mechanics-baseline");
        guidance.SelectionNotes.Add("Use only when repeatability matters more than retrieval realism.");
        return guidance;
    }

    if (lexical)
    {
        guidance.RecommendedFor.AddRange(new[]
        {
            "local lexical fallback",
            "token-overlap RAG development",
            "offline integration tests without model files"
        });
        guidance.Cautions.Add("Treat vector results as lexical/token-overlap behavior, not semantic understanding.");
        guidance.SuggestedRetrievalProfiles.AddRange(new[] { RetrievalProfileIds.Evidence, RetrievalProfileIds.RagBaseline });
        guidance.SuggestedEvaluationVariants.AddRange(new[] { "lexical-baseline", "token-hash-vector" });
        guidance.SelectionNotes.Add("Keep BM25 lexical retrieval as the operational baseline for verified evidence workflows.");
        return guidance;
    }

    if (isBge)
    {
        guidance.RecommendedFor.AddRange(new[]
        {
            "semantic RAG experiments",
            "exploratory discovery",
            "hybrid retrieval characterization"
        });
        guidance.SelectionNotes.Add("BGE is the current preferred local semantic embedding family for CPU-first quality checks.");
    }
    else if (isE5)
    {
        guidance.RecommendedFor.AddRange(new[]
        {
            "asymmetric query/passage retrieval",
            "semantic RAG experiments",
            "corpora that benefit from explicit query and passage prefixes"
        });
        guidance.SelectionNotes.Add("Use query/passage embedding purposes so E5 prefixes are applied consistently.");
    }
    else if (isMiniLm)
    {
        guidance.RecommendedFor.AddRange(new[]
        {
            "fast semantic smoke tests",
            "low-latency discovery checks",
            "small local corpora"
        });
        guidance.Cautions.Add("Characterize quality against your corpus before relying on MiniLM for evidence-sensitive ranking.");
    }
    else
    {
        guidance.RecommendedFor.Add("semantic retrieval experiments");
    }

    if (descriptor.CpuOnly)
    {
        guidance.SelectionNotes.Add("CPU-only provider; prefer it when GPU availability is unstable or unavailable.");
    }
    else
    {
        guidance.SelectionNotes.Add("GPU-preferred provider; review embedding doctor runtime details for CPU fallback before benchmarking.");
    }

    guidance.Cautions.Add("Do not promote semantic/vector ranking until /retrieval/evaluate/compare shows corpus-specific gains.");
    guidance.SuggestedRetrievalProfiles.AddRange(new[]
    {
        RetrievalProfileIds.RagBaseline,
        RetrievalProfileIds.Discovery,
        RetrievalProfileIds.ProductOptimization
    });
    guidance.SuggestedEvaluationVariants.AddRange(new[]
    {
        "lexical-baseline",
        "semantic-discovery",
        "hybrid-fusion",
        "rerank-polish"
    });
    return guidance;
}

static ProviderDoctorResult DiagnoseEmbeddingProvider(
    IEmbeddingProvider embeddingProvider,
    EmbeddingProviderRegistry registry,
    EmbeddingProviderOptions options)
{
    var descriptor = registry.GetProviders()
        .FirstOrDefault(candidate => string.Equals(candidate.Provider, embeddingProvider.ProviderId, StringComparison.OrdinalIgnoreCase));
    var result = new ProviderDoctorResult
    {
        Provider = embeddingProvider.ProviderId,
        Metadata =
        {
            ["modelId"] = embeddingProvider.ModelId,
            ["dimensions"] = embeddingProvider.Dimensions,
            ["configuredProvider"] = options.Provider,
            ["configuredModelId"] = options.ModelId,
            ["runtime"] = GetEmbeddingRuntime(embeddingProvider)
        }
    };

    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "embedding.provider",
        Status = descriptor is null ? ProviderDoctorStatuses.Warning : ProviderDoctorStatuses.Ok,
        Message = descriptor is null
            ? "Active embedding provider is not present in the provider registry."
            : "Active embedding provider is registered.",
        Details = descriptor is null
            ? new Dictionary<string, object?> { ["provider"] = embeddingProvider.ProviderId }
            : new Dictionary<string, object?>
            {
                ["provider"] = descriptor.Provider,
                ["displayName"] = descriptor.DisplayName,
                ["defaultModelId"] = descriptor.DefaultModelId,
                ["defaultDimensions"] = descriptor.DefaultDimensions,
                ["semanticQuality"] = descriptor.SemanticQuality,
                ["local"] = descriptor.Local,
                ["cpuOnly"] = descriptor.CpuOnly,
                ["requiresNetwork"] = descriptor.RequiresNetwork
            }
    });

    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "embedding.dimensions",
        Status = embeddingProvider.Dimensions > 0 ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Failed,
        Message = embeddingProvider.Dimensions > 0
            ? "Embedding provider reports a usable dimension count."
            : "Embedding provider reports an invalid dimension count.",
        Details = new Dictionary<string, object?>
        {
            ["dimensions"] = embeddingProvider.Dimensions,
            ["configuredDimensions"] = options.Dimensions
        }
    });

    AddEmbeddingModelFileDoctorCheck(result, embeddingProvider);
    AddEmbeddingRuntimeDoctorCheck(result, embeddingProvider);
    AddEmbeddingQualityDoctorCheck(result, descriptor);

    result.Status = ProviderDoctorStatuses.Aggregate(result.Checks);
    result.Summary = result.Status switch
    {
        ProviderDoctorStatuses.Ok => "Embedding provider passed local doctor checks.",
        ProviderDoctorStatuses.Warning => "Embedding provider is usable but has warnings to review.",
        ProviderDoctorStatuses.Failed => "Embedding provider failed one or more doctor checks.",
        _ => "Embedding provider doctor state is unknown."
    };

    return result;
}

static void AddEmbeddingModelFileDoctorCheck(ProviderDoctorResult result, IEmbeddingProvider embeddingProvider)
{
    if (embeddingProvider is not OnnxTransformerEmbeddingProvider onnxProvider)
    {
        result.Checks.Add(new ProviderDoctorCheck
        {
            Id = "embedding.model_files",
            Status = ProviderDoctorStatuses.Ok,
            Message = "Embedding provider does not require external model files.",
            Details = new Dictionary<string, object?>
            {
                ["requiresModelFiles"] = false
            }
        });
        return;
    }

    var modelExists = File.Exists(onnxProvider.ModelPath);
    var vocabExists = File.Exists(onnxProvider.VocabPath);
    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "embedding.model_files",
        Status = modelExists && vocabExists ? ProviderDoctorStatuses.Ok : ProviderDoctorStatuses.Failed,
        Message = modelExists && vocabExists
            ? "ONNX model and tokenizer vocabulary files are available."
            : "ONNX model or tokenizer vocabulary file is missing.",
        Details = new Dictionary<string, object?>
        {
            ["requiresModelFiles"] = true,
            ["modelPath"] = onnxProvider.ModelPath,
            ["modelExists"] = modelExists,
            ["vocabPath"] = onnxProvider.VocabPath,
            ["vocabExists"] = vocabExists
        }
    });
}

static void AddEmbeddingRuntimeDoctorCheck(ProviderDoctorResult result, IEmbeddingProvider embeddingProvider)
{
    if (embeddingProvider is not OnnxTransformerEmbeddingProvider onnxProvider)
    {
        result.Checks.Add(new ProviderDoctorCheck
        {
            Id = "embedding.runtime",
            Status = ProviderDoctorStatuses.Ok,
            Message = "Embedding provider uses local managed code and has no ONNX Runtime dependency.",
            Details = new Dictionary<string, object?>
            {
                ["runtime"] = "managed"
            }
        });
        return;
    }

    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "embedding.runtime",
        Status = string.IsNullOrWhiteSpace(onnxProvider.ExecutionProviderFallbackReason)
            ? ProviderDoctorStatuses.Ok
            : ProviderDoctorStatuses.Warning,
        Message = string.IsNullOrWhiteSpace(onnxProvider.ExecutionProviderFallbackReason)
            ? "ONNX Runtime initialized with the requested execution provider."
            : "ONNX Runtime fell back from the requested execution provider.",
        Details = new Dictionary<string, object?>
        {
            ["runtime"] = "onnxruntime",
            ["activeExecutionProvider"] = onnxProvider.ActiveExecutionProvider,
            ["fallbackReason"] = onnxProvider.ExecutionProviderFallbackReason,
            ["maxTokens"] = onnxProvider.MaxTokens,
            ["lowercase"] = onnxProvider.Lowercase,
            ["normalize"] = onnxProvider.Normalize,
            ["pooling"] = onnxProvider.Pooling,
            ["outputName"] = onnxProvider.OutputName,
            ["intraOpNumThreads"] = onnxProvider.IntraOpNumThreads,
            ["interOpNumThreads"] = onnxProvider.InterOpNumThreads,
            ["executionMode"] = onnxProvider.ExecutionModeName,
            ["cudaMemoryLimitMb"] = onnxProvider.CudaMemoryLimitMb
        }
    });
}

static void AddEmbeddingQualityDoctorCheck(ProviderDoctorResult result, EmbeddingProviderDescriptor? descriptor)
{
    if (descriptor is null)
    {
        return;
    }

    var guidance = BuildEmbeddingProviderGuidance(descriptor);
    var status = string.Equals(descriptor.SemanticQuality, "mechanical", StringComparison.OrdinalIgnoreCase)
        ? ProviderDoctorStatuses.Warning
        : ProviderDoctorStatuses.Ok;
    result.Checks.Add(new ProviderDoctorCheck
    {
        Id = "embedding.quality",
        Status = status,
        Message = status == ProviderDoctorStatuses.Ok
            ? "Embedding provider quality profile is suitable for local retrieval development."
            : "Embedding provider is intended for mechanics-only tests, not realistic retrieval quality.",
        Details = new Dictionary<string, object?>
        {
            ["semanticQuality"] = descriptor.SemanticQuality,
            ["realisticForSemanticRetrieval"] = guidance.RealisticForSemanticRetrieval,
            ["hardwareProfile"] = guidance.HardwareProfile,
            ["recommendedFor"] = guidance.RecommendedFor,
            ["cautions"] = guidance.Cautions,
            ["suggestedRetrievalProfiles"] = guidance.SuggestedRetrievalProfiles,
            ["suggestedEvaluationVariants"] = guidance.SuggestedEvaluationVariants,
            ["selectionNotes"] = guidance.SelectionNotes
        }
    });
}

static EmbeddingProviderOptions GetEmbeddingProviderOptions(IConfiguration configuration)
{
    var provider = configuration["Embedding:Provider"] ?? LocalTokenHashEmbeddingProviderFactory.Provider;
    var modelId = configuration["Embedding:ModelId"];
    int? dimensions = null;

    var rawDimensions = configuration["Embedding:Dimensions"];
    if (!string.IsNullOrWhiteSpace(rawDimensions))
    {
        if (!int.TryParse(rawDimensions, out var parsedDimensions) || parsedDimensions <= 0)
        {
            throw new InvalidOperationException("Embedding:Dimensions must be a positive integer.");
        }

        dimensions = parsedDimensions;
    }

    return new EmbeddingProviderOptions
    {
        Provider = provider,
        ModelId = modelId,
        Dimensions = dimensions,
        ModelPath = configuration["Embedding:ModelPath"],
        VocabPath = configuration["Embedding:VocabPath"],
        ExecutionProvider = configuration["Embedding:ExecutionProvider"],
        MaxTokens = ParseOptionalInt(configuration["Embedding:MaxTokens"], "Embedding:MaxTokens"),
        Lowercase = ParseOptionalBool(configuration["Embedding:Lowercase"], "Embedding:Lowercase"),
        Normalize = ParseOptionalBool(configuration["Embedding:Normalize"], "Embedding:Normalize"),
        Pooling = configuration["Embedding:Pooling"],
        OutputName = configuration["Embedding:OutputName"],
        IntraOpNumThreads = ParseOptionalInt(configuration["Embedding:IntraOpNumThreads"], "Embedding:IntraOpNumThreads"),
        InterOpNumThreads = ParseOptionalInt(configuration["Embedding:InterOpNumThreads"], "Embedding:InterOpNumThreads"),
        ExecutionMode = configuration["Embedding:ExecutionMode"],
        CudaDeviceId = ParseOptionalZeroBasedInt(configuration["Embedding:CudaDeviceId"], "Embedding:CudaDeviceId"),
        CudaMemoryLimitMb = ParseOptionalLong(configuration["Embedding:CudaMemoryLimitMb"], "Embedding:CudaMemoryLimitMb"),
        QueryPrefix = configuration["Embedding:QueryPrefix"],
        PassagePrefix = configuration["Embedding:PassagePrefix"],
        SymmetricPrefix = configuration["Embedding:SymmetricPrefix"]
    };
}

static LocalExecutionRuntimeOptions GetLocalExecutionRuntimeOptions(IConfiguration configuration, string dbPath)
{
    return new LocalExecutionRuntimeOptions
    {
        DatabasePath = dbPath,
        AdapterId = configuration["ExecutionRuntime:AdapterId"] ?? "local-sqlite",
        MaxActiveRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxActiveRuns"], "ExecutionRuntime:MaxActiveRuns") ?? 100,
        MaxRetainedTerminalRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxRetainedTerminalRuns"], "ExecutionRuntime:MaxRetainedTerminalRuns") ?? 500,
        DefaultListLimit = ParseOptionalInt(configuration["ExecutionRuntime:DefaultListLimit"], "ExecutionRuntime:DefaultListLimit") ?? 50,
        MaxListLimit = ParseOptionalInt(configuration["ExecutionRuntime:MaxListLimit"], "ExecutionRuntime:MaxListLimit") ?? 200,
        ConcurrencyRetryDelayMs = ParseOptionalInt(configuration["ExecutionRuntime:ConcurrencyRetryDelayMs"], "ExecutionRuntime:ConcurrencyRetryDelayMs") ?? 100,
        WorkerId = configuration["ExecutionRuntime:WorkerId"] ?? Environment.MachineName,
        ProductPolicies = GetExecutionProductPolicies(configuration)
    };
}

static IExecutionRuntimeAdapter CreateExecutionRuntime(
    IConfiguration configuration,
    ServerStorageOptions storageOptions,
    string dbPath,
    IObjectStore objectStore)
{
    var adapter = (configuration["ExecutionRuntime:Adapter"] ?? "local-sqlite").Trim().ToLowerInvariant();
    var configuredFactoryType = configuration["ExecutionRuntime:FactoryType"]?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredFactoryType))
    {
        var factoryType = Type.GetType(configuredFactoryType, throwOnError: false) ??
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(configuredFactoryType, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(type => type is not null);
        if (factoryType is null || !typeof(IExecutionRuntimeAdapterFactory).IsAssignableFrom(factoryType) || factoryType.IsAbstract)
        {
            throw new InvalidOperationException($"Execution runtime factory type '{configuredFactoryType}' must resolve to a concrete {nameof(IExecutionRuntimeAdapterFactory)}.");
        }

        if (Activator.CreateInstance(factoryType) is not IExecutionRuntimeAdapterFactory factory)
        {
            throw new InvalidOperationException($"Execution runtime factory type '{configuredFactoryType}' could not be constructed. Factories require a public parameterless constructor.");
        }

        return factory.Create(CreateExecutionRuntimeFactoryContext(configuration, dbPath, adapter));
    }

    return adapter switch
    {
        "local" or "local-sqlite" or "sqlite" => new LocalExecutionRuntime(GetLocalExecutionRuntimeOptions(configuration, dbPath)),
        "aws" or "aws-dynamodb-sqs" or "dynamodb-sqs" => CreateAwsExecutionRuntime(configuration, objectStore),
        "google" or "google-firestore-cloud-tasks" or "firestore-cloud-tasks" => CreateGoogleExecutionRuntime(configuration, storageOptions, objectStore),
        _ => throw new InvalidOperationException($"Execution runtime adapter '{adapter}' is not supported by this server.")
    };
}

static AwsDynamoExecutionRuntimeAdapter CreateAwsExecutionRuntime(IConfiguration configuration, IObjectStore objectStore)
{
    var section = configuration.GetSection("ExecutionRuntime:Aws");
    var tableName = FirstNonEmpty(
        section["DynamoDbTableName"],
        section["dynamoDbTableName"],
        section["TableName"],
        section["tableName"],
        configuration["VYRAL_EXECUTION_AWS_DYNAMODB_TABLE"]);
    var queueUrl = FirstNonEmpty(
        section["SqsQueueUrl"],
        section["sqsQueueUrl"],
        section["QueueUrl"],
        section["queueUrl"],
        configuration["VYRAL_EXECUTION_AWS_SQS_QUEUE_URL"]);
    if (string.IsNullOrWhiteSpace(tableName))
        throw new InvalidOperationException("AWS execution runtime requires ExecutionRuntime:Aws:DynamoDbTableName or VYRAL_EXECUTION_AWS_DYNAMODB_TABLE.");
    if (string.IsNullOrWhiteSpace(queueUrl))
        throw new InvalidOperationException("AWS execution runtime requires ExecutionRuntime:Aws:SqsQueueUrl or VYRAL_EXECUTION_AWS_SQS_QUEUE_URL.");

    var regionName = FirstNonEmpty(
        section["Region"],
        section["region"],
        configuration["VYRAL_EXECUTION_AWS_REGION"],
        configuration["AWS_REGION"],
        configuration["AWS_DEFAULT_REGION"]);
    if (string.IsNullOrWhiteSpace(regionName))
        throw new InvalidOperationException("AWS execution runtime requires ExecutionRuntime:Aws:Region or VYRAL_EXECUTION_AWS_REGION.");
    var region = RegionEndpoint.GetBySystemName(regionName);

    var root = FirstNonEmpty(section["Root"], section["root"], configuration["VYRAL_EXECUTION_AWS_DYNAMODB_ROOT"], "vyral-execution")!;
    var artifactObjectContainer = FirstNonEmpty(
        section["ArtifactObjectContainer"],
        section["artifactObjectContainer"],
        configuration["VYRAL_EXECUTION_AWS_ARTIFACT_OBJECT_CONTAINER"]);
    var dispatchOptions = CreateAwsExecutionDispatchOptions(section, queueUrl);
    var dispatcher = new AwsSqsExecutionDispatcher(new AwsSqsExecutionQueue(new AmazonSQSClient(region)), dispatchOptions);
    var workerDispatchers = GetAwsExecutionWorkerDispatchers(configuration, dispatchOptions, region);
    return new AwsDynamoExecutionRuntimeAdapter(
        new DynamoDbExecutionStateStore(new AmazonDynamoDBClient(region), new DynamoDbExecutionStateStoreOptions
        {
            TableName = tableName,
            Root = root,
            // Infrastructure ownership belongs to the deployment plane. A server should never
            // gain create-table power merely because a configuration name was mistyped.
            CreateTableIfMissing = ParseOptionalBool(section["CreateTableIfMissing"] ?? section["createTableIfMissing"], "ExecutionRuntime:Aws:CreateTableIfMissing") ?? false
        }),
        dispatcher,
        new AwsDynamoExecutionRuntimeOptions
        {
            AdapterId = configuration["ExecutionRuntime:AdapterId"] ?? "aws-dynamodb-sqs",
            Limits = string.IsNullOrWhiteSpace(artifactObjectContainer)
                ? AwsDynamoExecutionLimits.Default
                : AwsDynamoExecutionLimits.WithArtifactOffload,
            ArtifactObjectContainer = artifactObjectContainer,
            ProductPolicies = GetExecutionProductPolicies(configuration),
            WorkerDispatchers = workerDispatchers,
            RequireExplicitWorkerRoutes = ParseOptionalBool(section["RequireExplicitWorkerRoutes"] ?? section["requireExplicitWorkerRoutes"], "ExecutionRuntime:Aws:RequireExplicitWorkerRoutes") ?? true,
            MaxActiveRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxActiveRuns"], "ExecutionRuntime:MaxActiveRuns") ?? 1_000,
            MaxRetainedTerminalRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxRetainedTerminalRuns"], "ExecutionRuntime:MaxRetainedTerminalRuns") ?? 500,
            DefaultListLimit = ParseOptionalInt(configuration["ExecutionRuntime:DefaultListLimit"], "ExecutionRuntime:DefaultListLimit") ?? 100,
            MaxListLimit = ParseOptionalInt(configuration["ExecutionRuntime:MaxListLimit"], "ExecutionRuntime:MaxListLimit") ?? 1_000,
            DefaultHistoryLimit = ParseOptionalInt(configuration["ExecutionRuntime:DefaultHistoryLimit"], "ExecutionRuntime:DefaultHistoryLimit") ?? 100,
            MaxHistoryLimit = ParseOptionalInt(configuration["ExecutionRuntime:MaxHistoryLimit"], "ExecutionRuntime:MaxHistoryLimit") ?? 1_000,
            MaintenanceScanLimit = ParseOptionalInt(section["MaintenanceScanLimit"] ?? section["maintenanceScanLimit"], "ExecutionRuntime:Aws:MaintenanceScanLimit") ?? 10_000
        },
        string.IsNullOrWhiteSpace(artifactObjectContainer) ? null : objectStore);
}

static AwsSqsExecutionDispatchOptions CreateAwsExecutionDispatchOptions(IConfigurationSection section, string queueUrl)
{
    var maximumDelay = ParseOptionalInt(section["MaximumDelaySeconds"] ?? section["maximumDelaySeconds"], "ExecutionRuntime:Aws:MaximumDelaySeconds")
        ?? AwsSqsExecutionDispatchOptions.MaximumSupportedDelaySeconds;
    return new AwsSqsExecutionDispatchOptions
    {
        QueueUrl = queueUrl,
        Fifo = ParseOptionalBool(section["Fifo"] ?? section["fifo"], "ExecutionRuntime:Aws:Fifo") ?? false,
        MessageGroupId = FirstNonEmpty(section["MessageGroupId"], section["messageGroupId"], "vyral-execution")!,
        MaximumDelaySeconds = maximumDelay
    };
}

static IReadOnlyList<AwsDynamoExecutionWorkerDispatcher> GetAwsExecutionWorkerDispatchers(
    IConfiguration configuration,
    AwsSqsExecutionDispatchOptions defaults,
    RegionEndpoint region)
{
    return configuration.GetSection("ExecutionRuntime:Aws:WorkerRoutes").GetChildren().Select(section =>
    {
        var handlerId = section["HandlerId"] ?? section["handlerId"] ?? throw new InvalidOperationException("AWS execution worker routes require HandlerId.");
        var options = new AwsSqsExecutionDispatchOptions
        {
            QueueUrl = FirstNonEmpty(section["SqsQueueUrl"], section["sqsQueueUrl"], section["QueueUrl"], section["queueUrl"], defaults.QueueUrl)!,
            Fifo = ParseOptionalBool(section["Fifo"] ?? section["fifo"], $"ExecutionRuntime:Aws:WorkerRoutes:{section.Key}:Fifo") ?? defaults.Fifo,
            MessageGroupId = FirstNonEmpty(section["MessageGroupId"], section["messageGroupId"], defaults.MessageGroupId)!,
            MaximumDelaySeconds = ParseOptionalInt(section["MaximumDelaySeconds"] ?? section["maximumDelaySeconds"], $"ExecutionRuntime:Aws:WorkerRoutes:{section.Key}:MaximumDelaySeconds") ?? defaults.MaximumDelaySeconds
        };
        return new AwsDynamoExecutionWorkerDispatcher
        {
            HandlerId = handlerId,
            Dispatcher = new AwsSqsExecutionDispatcher(new AwsSqsExecutionQueue(new AmazonSQSClient(region)), options)
        };
    }).ToList();
}

static ExecutionRuntimeAdapterFactoryContext CreateExecutionRuntimeFactoryContext(
    IConfiguration configuration,
    string dbPath,
    string adapterId)
{
    var settings = configuration.AsEnumerable()
        .Where(item => !string.IsNullOrWhiteSpace(item.Key))
        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
    settings["ExecutionRuntime:Host:DatabasePath"] = dbPath;
    return new ExecutionRuntimeAdapterFactoryContext
    {
        AdapterId = adapterId,
        Settings = settings
    };
}

static IReadOnlyList<IExecutionIdentityAuthenticator> CreateExecutionIdentityAuthenticators(IConfiguration configuration)
{
    var authenticators = new List<IExecutionIdentityAuthenticator>
    {
        new DevelopmentHeaderExecutionIdentityAuthenticator(),
        new GoogleOidcExecutionIdentityAuthenticator()
    };
    var configuredType = configuration["Server:ExecutionAccess:AuthenticatorType"]?.Trim();
    if (string.IsNullOrWhiteSpace(configuredType)) return authenticators;

    var authenticatorType = Type.GetType(configuredType, throwOnError: false) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(configuredType, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);
    if (authenticatorType is null || !typeof(IExecutionIdentityAuthenticator).IsAssignableFrom(authenticatorType) || authenticatorType.IsAbstract)
    {
        throw new InvalidOperationException($"Execution identity authenticator type '{configuredType}' must resolve to a concrete {nameof(IExecutionIdentityAuthenticator)}.");
    }

    if (Activator.CreateInstance(authenticatorType) is not IExecutionIdentityAuthenticator authenticator)
    {
        throw new InvalidOperationException($"Execution identity authenticator type '{configuredType}' could not be constructed. Authenticators require a public parameterless constructor.");
    }

    authenticators.RemoveAll(existing => string.Equals(existing.AuthenticationMode, authenticator.AuthenticationMode, StringComparison.Ordinal));
    authenticators.Add(authenticator);
    return authenticators;
}

static IReadOnlyList<ICanonicalIdentityAuthenticator> CreateCanonicalIdentityAuthenticators(IConfiguration configuration)
{
    var authenticators = new List<ICanonicalIdentityAuthenticator>
    {
        new DevelopmentHeaderCanonicalIdentityAuthenticator(),
        new GoogleOidcCanonicalIdentityAuthenticator()
    };
    var configuredType = configuration["Server:CanonicalAccess:AuthenticatorType"]?.Trim();
    if (string.IsNullOrWhiteSpace(configuredType)) return authenticators;

    var authenticatorType = Type.GetType(configuredType, throwOnError: false) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(configuredType, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);
    if (authenticatorType is null || !typeof(ICanonicalIdentityAuthenticator).IsAssignableFrom(authenticatorType) || authenticatorType.IsAbstract)
        throw new InvalidOperationException($"Canonical identity authenticator type '{configuredType}' must resolve to a concrete {nameof(ICanonicalIdentityAuthenticator)}.");
    if (Activator.CreateInstance(authenticatorType) is not ICanonicalIdentityAuthenticator authenticator)
        throw new InvalidOperationException($"Canonical identity authenticator type '{configuredType}' could not be constructed. Authenticators require a public parameterless constructor.");

    authenticators.RemoveAll(existing => string.Equals(existing.AuthenticationMode, authenticator.AuthenticationMode, StringComparison.Ordinal));
    authenticators.Add(authenticator);
    return authenticators;
}

static void ValidateExecutionAccessRuntimePolicies(IConfiguration configuration, VyralExecutionAccessOptions accessOptions)
{
    if (accessOptions.IdentityPolicies.Count == 0) return;
    var productPolicies = GetExecutionRuntimeProductPolicies(configuration);
    if (productPolicies.Count == 0)
    {
        throw new InvalidOperationException("Execution identity policies require ExecutionRuntime:ProductPolicies so worker claims remain product-scoped.");
    }

    foreach (var identityPolicy in accessOptions.IdentityPolicies)
    {
        var productPolicy = productPolicies.FirstOrDefault(policy => string.Equals(policy.ProductId, identityPolicy.ProductId, StringComparison.Ordinal));
        if (productPolicy is null)
        {
            throw new InvalidOperationException($"Execution identity policy '{identityPolicy.Principal}' requires an execution runtime product policy for '{identityPolicy.ProductId}'.");
        }

        if (identityPolicy.AllowedOperations.Contains(ExecutionAccessOperations.Worker) &&
            (productPolicy.AllowedServiceIdentities.Count == 0 || !productPolicy.AllowedServiceIdentities.Contains(identityPolicy.WorkerId!)))
        {
            throw new InvalidOperationException($"Execution runtime product policy '{identityPolicy.ProductId}' must explicitly allow worker id '{identityPolicy.WorkerId}'.");
        }
    }
}

static IReadOnlyList<ExecutionRuntimeProductPolicyShape> GetExecutionRuntimeProductPolicies(IConfiguration configuration)
{
    return configuration.GetSection("ExecutionRuntime:ProductPolicies").GetChildren().Select(section => new ExecutionRuntimeProductPolicyShape
    {
        ProductId = section["ProductId"] ?? section["productId"] ?? string.Empty,
        AllowedServiceIdentities = ReadExecutionPolicyValues(section, "AllowedServiceIdentities", "allowedServiceIdentities")
    }).ToList();
}

static GoogleCloudExecutionRuntimeAdapter CreateGoogleExecutionRuntime(
    IConfiguration configuration,
    ServerStorageOptions storageOptions,
    IObjectStore objectStore)
{
    var section = configuration.GetSection("ExecutionRuntime:Google");
    var projectId = FirstNonEmpty(
        section["ProjectId"],
        section["projectId"],
        configuration["VYRAL_EXECUTION_GCP_PROJECT_ID"],
        storageOptions.GoogleProjectId);
    var locationId = FirstNonEmpty(
        section["LocationId"],
        section["locationId"],
        configuration["VYRAL_EXECUTION_TASKS_LOCATION"]);
    var queueId = FirstNonEmpty(
        section["QueueId"],
        section["queueId"],
        configuration["VYRAL_EXECUTION_TASKS_QUEUE"]);
    var workerUrl = FirstNonEmpty(
        section["WorkerUrl"],
        section["workerUrl"],
        configuration["VYRAL_EXECUTION_WORKER_URL"]);
    var firestoreRoot = FirstNonEmpty(
        section["FirestoreRootCollection"],
        section["firestoreRootCollection"],
        configuration["VYRAL_EXECUTION_FIRESTORE_ROOT_COLLECTION"],
        "vyral_execution");
    var artifactObjectContainer = FirstNonEmpty(
        section["ArtifactObjectContainer"],
        section["artifactObjectContainer"],
        configuration["VYRAL_EXECUTION_ARTIFACT_OBJECT_CONTAINER"]);

    if (string.IsNullOrWhiteSpace(projectId))
    {
        throw new InvalidOperationException("Google execution runtime requires ExecutionRuntime:Google:ProjectId, VYRAL_EXECUTION_GCP_PROJECT_ID, or a configured Google storage project.");
    }
    if (!string.IsNullOrWhiteSpace(artifactObjectContainer) && storageOptions.ObjectStore != ServerStorageBackendIds.GoogleCloudStorage)
    {
        throw new InvalidOperationException("Google execution artifact offload requires Storage:ObjectStore=google-cloud-storage.");
    }

    var firestoreOptions = new ServerStorageOptions
    {
        GoogleProjectId = projectId,
        GoogleFirestoreDatabaseId = FirstNonEmpty(
            section["FirestoreDatabaseId"],
            section["firestoreDatabaseId"],
            configuration["VYRAL_EXECUTION_FIRESTORE_DATABASE_ID"],
            storageOptions.GoogleFirestoreDatabaseId)
    };
    var dispatchOptions = new GoogleCloudExecutionDispatchOptions
    {
        ProjectId = projectId,
        LocationId = locationId ?? string.Empty,
        QueueId = queueId ?? string.Empty,
        WorkerUrl = workerUrl ?? string.Empty,
        ServiceAccountEmail = FirstNonEmpty(section["ServiceAccountEmail"], section["serviceAccountEmail"], configuration["VYRAL_EXECUTION_TASKS_SERVICE_ACCOUNT"]),
        OidcAudience = FirstNonEmpty(section["OidcAudience"], section["oidcAudience"], configuration["VYRAL_EXECUTION_TASKS_OIDC_AUDIENCE"])
    };
    dispatchOptions.Validate();
    var workerDispatchers = GetGoogleExecutionWorkerDispatchers(configuration, projectId, dispatchOptions);
    return new GoogleCloudExecutionRuntimeAdapter(
        new FirestoreExecutionStateStore(CreateFirestoreDb(firestoreOptions), firestoreRoot!),
        new GoogleCloudExecutionDispatcher(new CloudTasksHttpJsonQueue(CloudTasksClient.Create()), dispatchOptions),
        new GoogleCloudExecutionRuntimeOptions
        {
            AdapterId = configuration["ExecutionRuntime:AdapterId"] ?? "google-firestore-cloud-tasks",
            Limits = string.IsNullOrWhiteSpace(artifactObjectContainer)
                ? GoogleFirestoreExecutionLimits.Default
                : GoogleFirestoreExecutionLimits.WithArtifactOffload,
            ArtifactObjectContainer = artifactObjectContainer,
            ProductPolicies = GetExecutionProductPolicies(configuration),
            WorkerDispatchers = workerDispatchers,
            RequireExplicitWorkerRoutes = ParseOptionalBool(configuration["ExecutionRuntime:Google:RequireExplicitWorkerRoutes"], "ExecutionRuntime:Google:RequireExplicitWorkerRoutes") ?? true,
            MaxActiveRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxActiveRuns"], "ExecutionRuntime:MaxActiveRuns") ?? 1_000,
            MaxRetainedTerminalRuns = ParseOptionalInt(configuration["ExecutionRuntime:MaxRetainedTerminalRuns"], "ExecutionRuntime:MaxRetainedTerminalRuns") ?? 500,
            DefaultListLimit = ParseOptionalInt(configuration["ExecutionRuntime:DefaultListLimit"], "ExecutionRuntime:DefaultListLimit") ?? 100,
            MaxListLimit = ParseOptionalInt(configuration["ExecutionRuntime:MaxListLimit"], "ExecutionRuntime:MaxListLimit") ?? 1_000,
            DefaultHistoryLimit = ParseOptionalInt(configuration["ExecutionRuntime:DefaultHistoryLimit"], "ExecutionRuntime:DefaultHistoryLimit") ?? 100,
            MaxHistoryLimit = ParseOptionalInt(configuration["ExecutionRuntime:MaxHistoryLimit"], "ExecutionRuntime:MaxHistoryLimit") ?? 1_000,
            MaintenanceScanLimit = ParseOptionalInt(configuration["ExecutionRuntime:Google:MaintenanceScanLimit"], "ExecutionRuntime:Google:MaintenanceScanLimit") ?? 10_000
        }, string.IsNullOrWhiteSpace(artifactObjectContainer) ? null : objectStore);
}

static IReadOnlyList<GoogleCloudExecutionWorkerDispatcher> GetGoogleExecutionWorkerDispatchers(
    IConfiguration configuration,
    string projectId,
    GoogleCloudExecutionDispatchOptions defaults)
{
    return configuration.GetSection("ExecutionRuntime:Google:WorkerRoutes").GetChildren().Select(section =>
    {
        var handlerId = section["HandlerId"] ?? section["handlerId"] ?? throw new InvalidOperationException("Google execution worker routes require HandlerId.");
        var options = new GoogleCloudExecutionDispatchOptions
        {
            ProjectId = FirstNonEmpty(section["ProjectId"], section["projectId"], projectId)!,
            LocationId = FirstNonEmpty(section["LocationId"], section["locationId"], defaults.LocationId)!,
            QueueId = FirstNonEmpty(section["QueueId"], section["queueId"], defaults.QueueId)!,
            WorkerUrl = FirstNonEmpty(section["WorkerUrl"], section["workerUrl"], defaults.WorkerUrl)!,
            ServiceAccountEmail = FirstNonEmpty(section["ServiceAccountEmail"], section["serviceAccountEmail"], defaults.ServiceAccountEmail),
            OidcAudience = FirstNonEmpty(section["OidcAudience"], section["oidcAudience"], defaults.OidcAudience)
        };
        options.Validate();
        return new GoogleCloudExecutionWorkerDispatcher
        {
            HandlerId = handlerId,
            Dispatcher = new GoogleCloudExecutionDispatcher(new CloudTasksHttpJsonQueue(CloudTasksClient.Create()), options)
        };
    }).ToList();
}

static IReadOnlyList<ExecutionProductPolicy> GetExecutionProductPolicies(IConfiguration configuration)
{
    return configuration.GetSection("ExecutionRuntime:ProductPolicies").GetChildren()
        .Select(section => new ExecutionProductPolicy
        {
            ProductId = section["ProductId"] ?? section["productId"] ?? string.Empty,
            AllowedHandlerIds = ReadExecutionPolicyValues(section, "AllowedHandlerIds", "allowedHandlerIds"),
            AllowedTenantIds = ReadExecutionPolicyValues(section, "AllowedTenantIds", "allowedTenantIds"),
            AllowedServiceIdentities = ReadExecutionPolicyValues(section, "AllowedServiceIdentities", "allowedServiceIdentities"),
            MaxPayloadBytes = ParseOptionalInt(section["MaxPayloadBytes"] ?? section["maxPayloadBytes"], $"ExecutionRuntime:ProductPolicies:{section.Key}:MaxPayloadBytes"),
            ArtifactPrefix = section["ArtifactPrefix"] ?? section["artifactPrefix"],
            RedactedJsonPropertyNames = ReadExecutionPolicyValues(section, "RedactedJsonPropertyNames", "redactedJsonPropertyNames")
        })
        .ToList();
}

static IReadOnlySet<string> ReadExecutionPolicyValues(IConfigurationSection section, string pascalName, string camelName)
{
    var values = section.GetSection(pascalName).GetChildren()
        .Concat(section.GetSection(camelName).GetChildren())
        .Select(child => child.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim());
    return new HashSet<string>(values, StringComparer.Ordinal);
}

static string? FirstNonEmpty(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

static IReadOnlyList<ExecutionHandlerDescriptor> GetExternalExecutionHandlers(IConfiguration configuration)
{
    var handlers = new List<ExecutionHandlerDescriptor>();
    foreach (var section in configuration.GetSection("ExecutionRuntime:ExternalHandlers").GetChildren())
    {
        var handlerId = section["HandlerId"] ?? section["handlerId"];
        if (string.IsNullOrWhiteSpace(handlerId))
        {
            throw new InvalidOperationException("ExecutionRuntime:ExternalHandlers entries require HandlerId.");
        }

        handlers.Add(new ExecutionHandlerDescriptor
        {
            HandlerId = handlerId,
            PluginId = section["PluginId"] ?? section["pluginId"],
            DisplayName = section["DisplayName"] ?? section["displayName"] ?? handlerId,
            Description = section["Description"] ?? section["description"],
            MaxAttempts = ParseOptionalInt(section["MaxAttempts"] ?? section["maxAttempts"], $"ExecutionRuntime:ExternalHandlers:{section.Key}:MaxAttempts") ?? 1,
            ConcurrencyKey = section["ConcurrencyKey"] ?? section["concurrencyKey"]
        });
    }

    return handlers;
}

static int? ParseOptionalInt(string? value, string name)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!int.TryParse(value, out var parsed) || parsed <= 0)
    {
        throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    return parsed;
}

static int? ParseOptionalZeroBasedInt(string? value, string name)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!int.TryParse(value, out var parsed) || parsed < 0)
    {
        throw new InvalidOperationException($"{name} must be a non-negative integer.");
    }

    return parsed;
}

static long? ParseOptionalLong(string? value, string name)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!long.TryParse(value, out var parsed) || parsed <= 0)
    {
        throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    return parsed;
}

static bool? ParseOptionalBool(string? value, string name)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    switch (value.Trim().ToLowerInvariant())
    {
        case "true":
        case "t":
        case "1":
        case "yes":
        case "y":
        case "on":
        case "enabled":
        case "enable":
            return true;
        case "false":
        case "f":
        case "0":
        case "no":
        case "n":
        case "off":
        case "disabled":
        case "disable":
            return false;
    }

    if (!bool.TryParse(value, out var parsed))
    {
        throw new InvalidOperationException($"{name} must be a boolean value.");
    }

    return parsed;
}

static int GetStatusCode(Exception? exception)
{
    return exception switch
    {
        ExecutionAccessDeniedException => StatusCodes.Status403Forbidden,
        CanonicalAccessDeniedException => StatusCodes.Status403Forbidden,
        InvalidOperationException ex when IsMissingCollectionError(ex) => StatusCodes.Status404NotFound,
        InvalidOperationException ex when ex.Message.Contains("precondition failed", StringComparison.OrdinalIgnoreCase) => StatusCodes.Status412PreconditionFailed,
        JsonException => StatusCodes.Status400BadRequest,
        ArgumentException => StatusCodes.Status400BadRequest,
        InvalidOperationException => StatusCodes.Status400BadRequest,
        NotSupportedException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
}

static bool IsSafePublicException(Exception? exception)
{
    return exception switch
    {
        JsonException => true,
        ArgumentException => true,
        InvalidOperationException ex when IsMissingCollectionError(ex) => true,
        InvalidOperationException ex when ex.Message.Contains("precondition failed", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };
}

static Dictionary<string, string> ParseExecutionTagFilters(IQueryCollection query)
{
    var tags = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (key, values) in query)
    {
        var tagKey = key.StartsWith("tag.", StringComparison.Ordinal)
            ? key["tag.".Length..]
            : key.StartsWith("tags.", StringComparison.Ordinal)
                ? key["tags.".Length..]
                : null;
        if (string.IsNullOrWhiteSpace(tagKey))
        {
            continue;
        }

        var value = values.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags[tagKey] = value!;
        }
    }

    return tags;
}

static string[] GetConfiguredCorsOrigins(IConfiguration configuration)
{
    var configured = configuration.GetSection("Cors:AllowedOrigins")
        .GetChildren()
        .Select(section => section.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToArray();

    return configured.Length == 0
        ? new[]
        {
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:8080",
            "http://127.0.0.1:3000",
            "http://127.0.0.1:5173",
            "http://127.0.0.1:5174",
            "http://127.0.0.1:8080"
        }
        : configured;
}

static bool IsMissingCollectionError(InvalidOperationException ex)
{
    return ex.Message.StartsWith("Collection '", StringComparison.Ordinal) &&
        ex.Message.EndsWith("' does not exist.", StringComparison.Ordinal);
}

static IResult MissingCollectionProblem(InvalidOperationException ex)
{
    return Results.Problem(
        title: "Collection not found",
        detail: ex.Message,
        statusCode: StatusCodes.Status404NotFound);
}

static IResult UnknownProviderProblem(string provider)
{
    return Results.Problem(
        title: "Provider not registered",
        detail: $"Provider '{provider}' is not registered with this Vyral server. If this is a live CLI or Jules provider, ensure the server was started with Providers:EnableLiveTargets=true.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["providerId"] = provider });
}

static ProviderDisabledInfo BuildDisabledProviderInfo(string providerId)
{
    return new ProviderDisabledInfo
    {
        ProviderId = providerId,
        RegistrationStatus = "disabled",
        EnableLiveTargets = false,
        Hint = $"Provider '{providerId}' is a known live target. Set Providers:EnableLiveTargets=true to register live CLI and Jules providers."
    };
}

static ProviderCapabilityMatrix BuildProviderCapabilityMatrix(
    ProviderTargetRegistry registry,
    bool enableLiveTargets,
    IReadOnlyList<string> knownLiveProviderIds,
    ProviderRunGuard guard,
    ServerAccessOptions access)
{
    var knownCapabilityIds = GetKnownProviderCapabilityIds();
    var matrix = new ProviderCapabilityMatrix
    {
        CapabilityIds = knownCapabilityIds.ToList(),
        FailureClasses = GetKnownProviderFailureClasses().ToList(),
        OperationalLimits = guard.ToOperationalLimits(access.Enabled),
        Notes = new List<string>
        {
            "Only status=Succeeded is provider success; non-success envelopes must not be parsed as capability output.",
            "supportsAsyncJobs means the Vyral server can queue the provider through /providers/{provider}/jobs.",
            "supportsArtifacts means the provider advertises artifact-producing capability shapes; actual artifact refs remain provider/run specific."
        }
    };

    foreach (var profile in registry.GetProfiles())
    {
        var descriptor = registry.GetDescriptor(profile.Id);
        var target = registry.GetTarget(profile.Id);
        if (descriptor is not null)
        {
            matrix.Items.Add(BuildProviderCapabilityMatrixItem(
                descriptor,
                target,
                knownCapabilityIds,
                registered: true,
                enabled: true,
                registrationStatus: "registered",
                registrationHint: null));
        }
    }

    if (!enableLiveTargets)
    {
        var registered = matrix.Items
            .Select(item => item.Provider)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in knownLiveProviderIds.Where(providerId => !registered.Contains(providerId)))
        {
            var descriptor = CreateKnownLiveProviderDescriptor(providerId);
            if (descriptor is null)
            {
                continue;
            }

            var disabled = BuildDisabledProviderInfo(descriptor.Profile.Id);
            matrix.DisabledProviders.Add(disabled);
            matrix.Items.Add(BuildProviderCapabilityMatrixItem(
                descriptor,
                null,
                knownCapabilityIds,
                registered: false,
                enabled: false,
                registrationStatus: disabled.RegistrationStatus,
                registrationHint: disabled.Hint));
        }
    }

    matrix.Items = matrix.Items
        .OrderBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
        .ToList();
    matrix.DisabledProviders = matrix.DisabledProviders
        .OrderBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
        .ToList();
    return matrix;
}

static ProviderCapabilityMatrixItem BuildProviderCapabilityMatrixItem(
    ProviderTargetDescriptor descriptor,
    IProviderTarget? target,
    IReadOnlyList<string> knownCapabilityIds,
    bool registered,
    bool enabled,
    string registrationStatus,
    string? registrationHint)
{
    var descriptorByCapability = descriptor.Capabilities
        .ToDictionary(capability => capability.Id, StringComparer.OrdinalIgnoreCase);
    var capabilityIds = descriptorByCapability.Keys
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var capabilities = new Dictionary<string, ProviderCapabilitySupport>(StringComparer.OrdinalIgnoreCase);

    foreach (var capabilityId in knownCapabilityIds)
    {
        if (descriptorByCapability.TryGetValue(capabilityId, out var capability))
        {
            var unsupportedFeatures = capability.UnsupportedFeatures.ToList();
            if (!enabled)
            {
                unsupportedFeatures.Add("provider_disabled_by_configuration");
            }

            capabilities[capabilityId] = new ProviderCapabilitySupport
            {
                Supported = enabled,
                Operations = capability.Operations.ToList(),
                Modes = capability.ModePolicies.Select(policy => policy.Id).OrderBy(mode => mode, StringComparer.OrdinalIgnoreCase).ToList(),
                ToolPolicy = capability.ToolPolicy,
                UnsupportedFeatures = unsupportedFeatures
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(feature => feature, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }
        else
        {
            capabilities[capabilityId] = new ProviderCapabilitySupport
            {
                Supported = false,
                UnsupportedFeatures = new List<string> { "capability_not_advertised" }
            };
        }
    }

    return new ProviderCapabilityMatrixItem
    {
        Provider = descriptor.Profile.Id,
        DisplayName = descriptor.Profile.DisplayName,
        Family = descriptor.Profile.Family,
        Registered = registered,
        Enabled = enabled,
        RegistrationStatus = registrationStatus,
        RegistrationHint = registrationHint,
        Local = descriptor.Profile.Local,
        RequiresNetwork = descriptor.Profile.RequiresNetwork,
        Auth = descriptor.Profile.Auth,
        CapabilityIds = capabilityIds,
        Capabilities = capabilities,
        SupportsModelListing = enabled && target is IProviderModelCatalog,
        SupportsQuota = enabled && target is IProviderQuotaReporter,
        SupportsAsyncJobs = enabled,
        SupportsArtifacts = enabled && capabilityIds.Contains(ProviderCapabilityIds.AiScaffold, StringComparer.OrdinalIgnoreCase)
    };
}

static IReadOnlyList<string> GetKnownProviderCapabilityIds()
{
    return new[]
    {
        ProviderCapabilityIds.AiChat,
        ProviderCapabilityIds.AiEmbedding,
        ProviderCapabilityIds.AiExtract,
        ProviderCapabilityIds.AiRerank,
        ProviderCapabilityIds.AiReview,
        ProviderCapabilityIds.AiScaffold,
        ProviderCapabilityIds.AiToolPlan,
        ProviderCapabilityIds.AgentJob,
        ProviderCapabilityIds.AgentWorkspace,
        ProviderCapabilityIds.ComputeJob,
        ProviderCapabilityIds.RetrievalIndex,
        ProviderCapabilityIds.RetrievalSearch,
        ProviderCapabilityIds.StorageObject
    };
}

static IReadOnlyList<string> GetKnownProviderFailureClasses()
{
    return new[]
    {
        ProviderFailureClasses.Auth,
        ProviderFailureClasses.Cancelled,
        ProviderFailureClasses.Configuration,
        ProviderFailureClasses.Network,
        ProviderFailureClasses.Policy,
        ProviderFailureClasses.ProviderUnavailable,
        ProviderFailureClasses.Quota,
        ProviderFailureClasses.RateLimit,
        ProviderFailureClasses.Schema,
        ProviderFailureClasses.Timeout,
        ProviderFailureClasses.Tool,
        ProviderFailureClasses.Trust,
        ProviderFailureClasses.Unknown,
        ProviderFailureClasses.Unsupported
    };
}

static ProviderReadinessEnvelope? BuildDisabledProviderReadiness(string providerId, ProviderRunGuard guard, ServerAccessOptions access)
{
    var descriptor = CreateKnownLiveProviderDescriptor(providerId);
    if (descriptor is null)
    {
        return null;
    }

    var disabledInfo = BuildDisabledProviderInfo(descriptor.Profile.Id);
    var envelope = new ProviderReadinessEnvelope();
    envelope.DisabledProviders.Add(disabledInfo);
    foreach (var capability in descriptor.Capabilities.OrderBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase))
    {
        envelope.Items.Add(new ProviderCapabilityReadiness
        {
            Provider = descriptor.Profile.Id,
            Capability = capability.Id,
            RegistrationStatus = "disabled",
            RegistrationHint = disabledInfo.Hint,
            Operations = capability.Operations.ToList(),
            Modes = capability.ModePolicies.Select(policy => policy.Id).OrderBy(mode => mode, StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigHash = descriptor.Profile.ConfigHash,
            QualificationStatus = ProviderQualificationStatuses.Unvalidated,
            Callable = false,
            Ready = false,
            CanRunUnvalidated = false,
            Reason = "provider_disabled",
            DriftTriggers = new List<string> { "registration_disabled" },
            UnsupportedFeatures = capability.UnsupportedFeatures
                .Concat(new[] { "provider_disabled_by_configuration" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(feature => feature, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Local = descriptor.Profile.Local,
            RequiresNetwork = descriptor.Profile.RequiresNetwork,
            Auth = descriptor.Profile.Auth,
            AuthRequired = access.Enabled,
            OperationalLimits = guard.ToOperationalLimits(access.Enabled)
        });
    }

    return envelope;
}

static ProviderTargetDescriptor? CreateKnownLiveProviderDescriptor(string providerId)
{
    IProviderTarget? target = providerId.ToLowerInvariant() switch
    {
        "codex-cli" => CliProviderTargets.CreateCodex(),
        "claude-cli" => CliProviderTargets.CreateClaude(),
        "gemini-cli" => CliProviderTargets.CreateGemini(),
        "antigravity-cli" => CliProviderTargets.CreateAntigravity(),
        "grok-build-cli" => CliProviderTargets.CreateGrokBuild(),
        "jules-api" => new JulesProviderTarget(new JulesProviderOptions()),
        _ => null
    };

    return target is null
        ? null
        : new ProviderTargetDescriptor
        {
            Profile = target.Profile,
            Capabilities = target.Capabilities.ToList()
        };
}

static ProviderTargetRegistry CreateProviderTargetRegistry(IConfiguration configuration)
{
    var targets = new List<IProviderTarget>
    {
        new DeterministicAiProviderTarget(),
        new LocalTokenOverlapRerankerProviderTarget(),
        OnnxCrossEncoderRerankerProviderTargets.CreateCpu(GetOnnxRerankerProviderOptions(configuration, "Providers:OnnxReranker:Cpu")),
        OnnxCrossEncoderRerankerProviderTargets.CreateGpu(GetOnnxRerankerProviderOptions(configuration, "Providers:OnnxReranker:Gpu"))
    };

    var enableLiveTargets = ParseOptionalBool(configuration["Providers:EnableLiveTargets"], "Providers:EnableLiveTargets") ?? false;
    if (!enableLiveTargets)
    {
        return new ProviderTargetRegistry(targets);
    }

    targets.Add(CliProviderTargets.CreateCodex(overrides: GetCliProviderOptions(configuration, "Providers:Codex")));
    targets.Add(CliProviderTargets.CreateClaude(overrides: GetCliProviderOptions(configuration, "Providers:Claude")));
    targets.Add(CliProviderTargets.CreateGemini(overrides: GetCliProviderOptions(configuration, "Providers:Gemini")));
    targets.Add(CliProviderTargets.CreateAntigravity(overrides: GetCliProviderOptions(configuration, "Providers:Antigravity")));
    targets.Add(CliProviderTargets.CreateGrokBuild(overrides: GetGrokBuildProviderOptions(configuration, "Providers:GrokBuild")));

    if (ParseOptionalBool(configuration["Providers:WorkspaceAgent:Enabled"], "Providers:WorkspaceAgent:Enabled") == true)
    {
        if (!ServerAccessOptions.FromConfiguration(configuration).Enabled)
        {
            throw new InvalidOperationException("Providers:WorkspaceAgent:Enabled requires Server API-key authentication. Configure Server:RequireApiKey and Server:ApiKey before registering a source-writing target.");
        }

        targets.Add(CliProviderTargets.CreateWorkspaceCodingAgent(GetCliWorkspaceCodingAgentOptions(configuration)));
    }

    targets.Add(new JulesProviderTarget(new JulesProviderOptions
    {
        ApiKey = configuration["Providers:Jules:ApiKey"] ?? Environment.GetEnvironmentVariable("JULES_API_KEY"),
        BaseUri = Uri.TryCreate(configuration["Providers:Jules:BaseUri"], UriKind.Absolute, out var baseUri)
            ? baseUri
            : new Uri("https://jules.googleapis.com/v1alpha/"),
        Source = configuration["Providers:Jules:Source"],
        StartingBranch = configuration["Providers:Jules:StartingBranch"] ?? "master",
        QualificationSessionId = configuration["Providers:Jules:QualificationSessionId"],
        DefaultAutomationMode = configuration["Providers:Jules:AutomationMode"],
        RequirePlanApproval = ParseOptionalBool(configuration["Providers:Jules:RequirePlanApproval"], "Providers:Jules:RequirePlanApproval") ?? true
    }));

    return new ProviderTargetRegistry(targets);
}

static OnnxCrossEncoderRerankerProviderOptions GetOnnxRerankerProviderOptions(IConfiguration configuration, string section)
{
    return new OnnxCrossEncoderRerankerProviderOptions
    {
        ProviderId = configuration[$"{section}:ProviderId"] ?? string.Empty,
        DisplayName = configuration[$"{section}:DisplayName"] ?? string.Empty,
        ModelId = configuration[$"{section}:ModelId"],
        ModelPath = configuration[$"{section}:ModelPath"],
        VocabPath = configuration[$"{section}:VocabPath"],
        ExecutionProvider = configuration[$"{section}:ExecutionProvider"] ?? string.Empty,
        MaxTokens = ParseOptionalInt(configuration[$"{section}:MaxTokens"], $"{section}:MaxTokens") ?? 0,
        BatchSize = ParseOptionalInt(configuration[$"{section}:BatchSize"], $"{section}:BatchSize") ?? 0,
        Lowercase = ParseOptionalBool(configuration[$"{section}:Lowercase"], $"{section}:Lowercase"),
        OutputName = configuration[$"{section}:OutputName"],
        ScoreMode = configuration[$"{section}:ScoreMode"] ?? string.Empty,
        IntraOpNumThreads = ParseOptionalInt(configuration[$"{section}:IntraOpNumThreads"], $"{section}:IntraOpNumThreads"),
        InterOpNumThreads = ParseOptionalInt(configuration[$"{section}:InterOpNumThreads"], $"{section}:InterOpNumThreads"),
        ExecutionMode = configuration[$"{section}:ExecutionMode"],
        CudaDeviceId = ParseOptionalZeroBasedInt(configuration[$"{section}:CudaDeviceId"], $"{section}:CudaDeviceId"),
        CudaMemoryLimitMb = ParseOptionalLong(configuration[$"{section}:CudaMemoryLimitMb"], $"{section}:CudaMemoryLimitMb")
    };
}

static CliProviderOptions GetCliProviderOptions(IConfiguration configuration, string section)
{
    return new CliProviderOptions
    {
        Command = configuration[$"{section}:Command"] ?? string.Empty,
        WorkingDirectory = configuration[$"{section}:WorkingDirectory"],
        Environment = configuration.GetSection($"{section}:Environment")
            .GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Key))
            .ToDictionary(child => child.Key, child => child.Value, StringComparer.Ordinal),
        ClearEnvironment = ParseOptionalBool(configuration[$"{section}:ClearEnvironment"], $"{section}:ClearEnvironment") ?? false,
        ModelId = configuration[$"{section}:ModelId"],
        PromptTransport = configuration[$"{section}:PromptTransport"] ?? string.Empty,
        PromptFileDirectory = configuration[$"{section}:PromptFileDirectory"],
        QuotaSource = configuration[$"{section}:QuotaSource"] ?? string.Empty,
        QuotaCommand = configuration[$"{section}:QuotaCommand"] ?? string.Empty,
        QuotaSocketPath = configuration[$"{section}:QuotaSocketPath"],
        QuotaWebSocketUri = configuration[$"{section}:QuotaWebSocketUri"],
        QuotaAutoStartWebSocket = ParseOptionalBool(configuration[$"{section}:QuotaAutoStartWebSocket"], $"{section}:QuotaAutoStartWebSocket") ?? true,
        QuotaTimeoutSeconds = ParseOptionalInt(configuration[$"{section}:QuotaTimeoutSeconds"], $"{section}:QuotaTimeoutSeconds") ?? 5,
        QuotaMaxOutputBytes = ParseOptionalInt(configuration[$"{section}:QuotaMaxOutputBytes"], $"{section}:QuotaMaxOutputBytes") ?? 64 * 1024,
        QuotaArguments = configuration.GetSection($"{section}:QuotaArguments")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        QuotaWebSocketLaunchArguments = configuration.GetSection($"{section}:QuotaWebSocketLaunchArguments")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        KnownModels = configuration.GetSection($"{section}:KnownModels")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList()
    };
}

static GrokBuildProviderOptions GetGrokBuildProviderOptions(IConfiguration configuration, string section)
{
    return new GrokBuildProviderOptions
    {
        Command = configuration[$"{section}:Command"] ?? string.Empty,
        ModelId = configuration[$"{section}:ModelId"],
        KnownModels = configuration.GetSection($"{section}:KnownModels")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        WorkingDirectory = configuration[$"{section}:WorkingDirectory"],
        PromptFileDirectory = configuration[$"{section}:PromptFileDirectory"],
        Environment = configuration.GetSection($"{section}:Environment")
            .GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Key))
            .ToDictionary(child => child.Key, child => child.Value, StringComparer.Ordinal),
        SandboxProfile = configuration[$"{section}:SandboxProfile"],
        ToolDenyRules = configuration.GetSection($"{section}:ToolDenyRules")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        TimeoutSeconds = ParseOptionalInt(configuration[$"{section}:TimeoutSeconds"], $"{section}:TimeoutSeconds") ?? 120,
        MaxOutputBytes = ParseOptionalInt(configuration[$"{section}:MaxOutputBytes"], $"{section}:MaxOutputBytes") ?? 128 * 1024,
        Auth = configuration[$"{section}:Auth"] ?? "external-cli"
    };
}

static CliWorkspaceCodingAgentOptions GetCliWorkspaceCodingAgentOptions(IConfiguration configuration)
{
    const string section = "Providers:WorkspaceAgent";
    return new CliWorkspaceCodingAgentOptions
    {
        ProviderId = configuration[$"{section}:ProviderId"] ?? "workspace-cli",
        DisplayName = configuration[$"{section}:DisplayName"] ?? "Workspace CLI coding agent",
        AgentProfile = configuration[$"{section}:AgentProfile"] ?? "configured-cli",
        AgentCommand = configuration[$"{section}:AgentCommand"] ?? string.Empty,
        AgentArguments = configuration.GetSection($"{section}:AgentArguments")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        PromptTransport = configuration[$"{section}:PromptTransport"] ?? CliPromptTransports.StandardInput,
        ModelId = configuration[$"{section}:ModelId"],
        Environment = configuration.GetSection($"{section}:Environment")
            .GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Key))
            .ToDictionary(child => child.Key, child => child.Value, StringComparer.Ordinal),
        AllowedWorkspaceRoots = configuration.GetSection($"{section}:AllowedWorkspaceRoots")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        StagingRoot = configuration[$"{section}:StagingRoot"] ?? string.Empty,
        BubblewrapCommand = configuration[$"{section}:BubblewrapCommand"] ?? "bwrap",
        GitCommand = configuration[$"{section}:GitCommand"] ?? "git",
        RuntimeReadOnlyPaths = configuration.GetSection($"{section}:RuntimeReadOnlyPaths")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        ToolSearchPaths = configuration.GetSection($"{section}:ToolSearchPaths")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList(),
        MaxOutputBytes = ParseOptionalInt(configuration[$"{section}:MaxOutputBytes"], $"{section}:MaxOutputBytes") ?? 128 * 1024,
        PreparationTimeoutSeconds = ParseOptionalInt(configuration[$"{section}:PreparationTimeoutSeconds"], $"{section}:PreparationTimeoutSeconds") ?? 30,
        RequiresNetwork = ParseOptionalBool(configuration[$"{section}:RequiresNetwork"], $"{section}:RequiresNetwork") ?? false,
        Auth = configuration[$"{section}:Auth"] ?? ProviderAuthTypes.ExternalCli
    };
}

public partial class Program;
