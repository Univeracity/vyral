using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Execution;
using Vyral.Execution.Local;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class ExecutionRuntimeFactoryTests
{
    [Fact]
    public async Task GoogleExecutionTokenValidator_MapsMalformedTokensToAccessDenied()
    {
        var validator = new GoogleExecutionTokenValidator();

        var error = await Assert.ThrowsAsync<ExecutionAccessDeniedException>(() =>
            validator.ValidateAsync(
                "not-a-google-identity-token",
                new HashSet<string>(StringComparer.Ordinal) { "https://vyral.example.test" }));

        Assert.Equal("Google OIDC identity token is invalid.", error.Message);
    }

    [Fact]
    public async Task Server_CanComposeConfiguredProviderFactoryWithoutChangingRuntimeSwitch()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-configured-runtime-factory-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-configured-runtime-factory-objects-{Guid.NewGuid():N}");
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DatabasePath", dbPath);
                builder.UseSetting("ObjectsPath", objectsPath);
                builder.UseSetting("ExecutionRuntime:Adapter", "test-configured");
                builder.UseSetting("ExecutionRuntime:FactoryType", typeof(TestConfiguredExecutionRuntimeFactory).AssemblyQualifiedName);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabasePath"] = dbPath,
                    ["ObjectsPath"] = objectsPath,
                    ["ExecutionRuntime:Adapter"] = "test-configured",
                    ["ExecutionRuntime:FactoryType"] = typeof(TestConfiguredExecutionRuntimeFactory).AssemblyQualifiedName
                }));
            });

        var client = factory.CreateClient();
        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.Equal("test-configured", runtime!.Status.Adapter.AdapterId);
        Assert.Contains(ExecutionCapabilityIds.InProcessHandlers, runtime.Status.Adapter.Capabilities);
    }

    [Fact]
    public async Task Server_CanComposeAnInProcessOnlyAdapterWithoutExternalWorkerRoutes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-in-process-runtime-factory-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-in-process-runtime-factory-objects-{Guid.NewGuid():N}");
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DatabasePath", dbPath);
                builder.UseSetting("ObjectsPath", objectsPath);
                builder.UseSetting("ExecutionRuntime:Adapter", "test-in-process");
                builder.UseSetting("ExecutionRuntime:FactoryType", typeof(TestInProcessExecutionRuntimeFactory).AssemblyQualifiedName);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DatabasePath"] = dbPath,
                    ["ObjectsPath"] = objectsPath,
                    ["ExecutionRuntime:Adapter"] = "test-in-process",
                    ["ExecutionRuntime:FactoryType"] = typeof(TestInProcessExecutionRuntimeFactory).AssemblyQualifiedName
                }));
            });

        var client = factory.CreateClient();
        var runtime = await client.GetFromJsonAsync<ExecutionRuntimeSurface>("/execution/runtime");
        Assert.NotNull(runtime);
        Assert.Contains(ExecutionCapabilityIds.InProcessHandlers, runtime!.Status.Adapter.Capabilities);
        Assert.DoesNotContain(ExecutionCapabilityIds.ExternalWorkers, runtime.Status.Adapter.Capabilities);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.PostAsync("/execution/workers/leases", null)).StatusCode);
    }

    [Fact]
    public async Task Server_ComposesAwsDynamoDbSqsRuntimeFromExplicitDeploymentConfiguration()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-aws-runtime-host-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-aws-runtime-host-objects-{Guid.NewGuid():N}");
        const string handlerId = "test.aws.worker";
        var settings = new Dictionary<string, string?>
        {
            ["DatabasePath"] = dbPath,
            ["ObjectsPath"] = objectsPath,
            ["ExecutionRuntime:Adapter"] = "aws-dynamodb-sqs",
            ["ExecutionRuntime:Aws:Region"] = "us-east-1",
            ["ExecutionRuntime:Aws:DynamoDbTableName"] = "vyral-test-execution",
            ["ExecutionRuntime:Aws:SqsQueueUrl"] = "https://sqs.us-east-1.amazonaws.com/123456789012/vyral-test-execution",
            ["ExecutionRuntime:Aws:Root"] = "vyral-test-execution",
            ["ExecutionRuntime:Aws:CreateTableIfMissing"] = "false",
            ["ExecutionRuntime:Aws:RequireExplicitWorkerRoutes"] = "true",
            ["ExecutionRuntime:Aws:WorkerRoutes:0:HandlerId"] = handlerId,
            ["ExecutionRuntime:ExternalHandlers:0:HandlerId"] = handlerId,
            ["ExecutionRuntime:ExternalHandlers:0:PluginId"] = "test.aws"
        };
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                foreach (var (key, value) in settings) builder.UseSetting(key, value);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            });

        var client = factory.CreateClient();
        using var openApi = JsonDocument.Parse(await client.GetStringAsync("/openapi/vyral.json"));
        Assert.True(openApi.RootElement.GetProperty("paths").TryGetProperty("/execution/workers/leases", out _));
    }

    [Fact]
    public async Task Server_CanComposeConfiguredExecutionIdentityAuthenticatorWithoutGoogleCoupling()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-configured-identity-{Guid.NewGuid():N}.sqlite");
        var objectsPath = Path.Combine(Path.GetTempPath(), $"vyral-configured-identity-objects-{Guid.NewGuid():N}");
        const string handlerId = "test.configured.identity.worker";
        var authenticatorType = typeof(TestHeaderExecutionIdentityAuthenticator).AssemblyQualifiedName
            ?? throw new InvalidOperationException("Test authenticator type name is required.");
        var settings = new Dictionary<string, string?>
        {
            ["DatabasePath"] = dbPath,
            ["ObjectsPath"] = objectsPath,
            ["Server:ExecutionAccess:AuthenticationMode"] = TestHeaderExecutionIdentityAuthenticator.Mode,
            ["Server:ExecutionAccess:AuthenticatorType"] = authenticatorType,
            ["ExecutionRuntime:ExternalHandlers:0:HandlerId"] = handlerId,
            ["ExecutionRuntime:ExternalHandlers:0:PluginId"] = "test.configured.identity",
            ["ExecutionRuntime:ProductPolicies:0:ProductId"] = "product-a",
            ["ExecutionRuntime:ProductPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:0"] = handlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:Principal"] = "custom-runtime@example.test",
            ["Server:ExecutionAccess:IdentityPolicies:0:ProductId"] = "product-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:0"] = handlerId,
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:0"] = ExecutionAccessOperations.StartRun
        };
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestHeaderExecutionIdentityAuthenticator.HeaderName, "custom-runtime@example.test");
        var response = await client.PostAsJsonAsync("/execution/runs", new ExecutionRunRequest
        {
            HandlerId = handlerId,
            Scope = new ExecutionScope { ProductId = "product-a", TenantId = "tenant-a", ServiceIdentity = "spoofed" }
        });
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<ExecutionRun>();
        Assert.Equal("custom-runtime@example.test", run!.Scope!.ServiceIdentity);
    }
}

/// <summary>Represents the shape an independently packaged Azure/AWS adapter factory would use.</summary>
public sealed class TestConfiguredExecutionRuntimeFactory : IExecutionRuntimeAdapterFactory
{
    public IExecutionRuntimeAdapter Create(ExecutionRuntimeAdapterFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var databasePath = context.Settings["ExecutionRuntime:Host:DatabasePath"]
            ?? throw new InvalidOperationException("The host database path is required for this fixture.");
        return new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = databasePath,
            AdapterId = context.AdapterId
        });
    }
}

public sealed class TestInProcessExecutionRuntimeFactory : IExecutionRuntimeAdapterFactory
{
    public IExecutionRuntimeAdapter Create(ExecutionRuntimeAdapterFactoryContext context)
    {
        var databasePath = context.Settings["ExecutionRuntime:Host:DatabasePath"]
            ?? throw new InvalidOperationException("The host database path is required for this fixture.");
        return new TestInProcessExecutionRuntimeAdapter(new LocalExecutionRuntime(new LocalExecutionRuntimeOptions
        {
            DatabasePath = databasePath,
            AdapterId = context.AdapterId
        }));
    }
}

/// <summary>Hides local's optional worker protocol to exercise an in-process-only provider shape.</summary>
public sealed class TestInProcessExecutionRuntimeAdapter : IExecutionRuntimeAdapter
{
    private readonly IExecutionRuntimeAdapter _inner;

    public TestInProcessExecutionRuntimeAdapter(IExecutionRuntimeAdapter inner)
    {
        _inner = inner;
        Adapter = JsonSerializer.Deserialize<ExecutionRuntimeAdapterDescriptor>(JsonSerializer.Serialize(inner.Adapter, ExecutionJson.Options), ExecutionJson.Options)!;
        Adapter.Capabilities.Remove(ExecutionCapabilityIds.ExternalWorkers);
    }

    public ExecutionRuntimeAdapterDescriptor Adapter { get; }
    public void RegisterHandler(IExecutionHandler handler) => _inner.RegisterHandler(handler);
    public void RegisterPlugin(IExecutionPlugin plugin) => _inner.RegisterPlugin(plugin);
    public IReadOnlyList<ExecutionPluginDescriptor> ListPlugins() => _inner.ListPlugins();
    public IReadOnlyList<ExecutionHandlerDescriptor> ListHandlers() => _inner.ListHandlers();
    public Task<ExecutionRun> StartRunAsync(ExecutionRunRequest request, CancellationToken ct = default) => _inner.StartRunAsync(request, ct);
    public Task<ExecutionRun?> GetRunAsync(string runId, bool includeResult = true, CancellationToken ct = default) => _inner.GetRunAsync(runId, includeResult, ct);
    public Task<IReadOnlyList<ExecutionRun>> ListRunsAsync(ExecutionRunQuery? query = null, CancellationToken ct = default) => _inner.ListRunsAsync(query, ct);
    public Task<ExecutionRun?> CancelRunAsync(string runId, CancellationToken ct = default) => _inner.CancelRunAsync(runId, ct);
    public Task<IReadOnlyList<ExecutionTraceEvent>> GetHistoryAsync(string runId, ExecutionHistoryQuery? query = null, CancellationToken ct = default) => _inner.GetHistoryAsync(runId, query, ct);
    public Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(string runId, CancellationToken ct = default) => _inner.ListArtifactsAsync(runId, ct);
    public Task<ExecutionArtifact?> GetArtifactAsync(string runId, string artifactRef, CancellationToken ct = default) => _inner.GetArtifactAsync(runId, artifactRef, ct);
    public Task<ExecutionCheckpoint?> GetCheckpointAsync(string runId, string key, CancellationToken ct = default) => _inner.GetCheckpointAsync(runId, key, ct);
    public Task<ExecutionLease?> TryAcquireLeaseAsync(ExecutionLeaseRequest request, CancellationToken ct = default) => _inner.TryAcquireLeaseAsync(request, ct);
    public Task<bool> ReleaseLeaseAsync(string leaseKey, string ownerId, CancellationToken ct = default) => _inner.ReleaseLeaseAsync(leaseKey, ownerId, ct);
    public Task<ExecutionTimer> ScheduleTimerAsync(ExecutionTimerRequest request, CancellationToken ct = default) => _inner.ScheduleTimerAsync(request, ct);
    public Task<ExecutionExternalEvent> RaiseEventAsync(ExecutionExternalEventRequest request, CancellationToken ct = default) => _inner.RaiseEventAsync(request, ct);

    public async Task<ExecutionRuntimeAdapterStatus> GetAdapterStatusAsync(CancellationToken ct = default)
    {
        var status = await _inner.GetAdapterStatusAsync(ct);
        status.Adapter = JsonSerializer.Deserialize<ExecutionRuntimeAdapterDescriptor>(JsonSerializer.Serialize(Adapter, ExecutionJson.Options), ExecutionJson.Options)!;
        return status;
    }
}

/// <summary>Represents an Azure/AWS-style host authenticator supplied without changing Vyral.Server.</summary>
public sealed class TestHeaderExecutionIdentityAuthenticator : IExecutionIdentityAuthenticator
{
    public const string Mode = "test-header";
    public const string HeaderName = "X-Test-Execution-Identity";

    public string AuthenticationMode => Mode;

    public Task<string> AuthenticateAsync(HttpContext context, VyralExecutionAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        var principal = context.Request.Headers[HeaderName].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new ExecutionAccessDeniedException("A test execution identity header is required.");
        return Task.FromResult(principal);
    }
}
