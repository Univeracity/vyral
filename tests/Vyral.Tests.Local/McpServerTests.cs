using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Protocol;
using Vyral.Execution;
using Vyral.Mcp;

namespace Vyral.Tests.Local;

public sealed class McpServerTests
{
    private const string ProtocolVersion = "2026-07-28";

    [Fact]
    public async Task McpRoutingHeaderNormalizationStripsOnlyHttpOptionalWhitespace()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var options = new VyralMcpOptions { EndpointPath = "/mcp" };
        StringValues observedMethod = default;
        StringValues observedName = default;
        string? observedUnrelated = null;

        app.UseVyralMcpRoutingHeaderNormalization(options);
        app.Run(context =>
        {
            observedMethod = context.Request.Headers["Mcp-Method"];
            observedName = context.Request.Headers["mcp-name"];
            observedUnrelated = context.Request.Headers["X-Unrelated"].ToString();
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers["Mcp-Method"] = "\ttools/call ";
        context.Request.Headers["mcp-name"] = new StringValues([" first ", "\tsecond\t"]);
        context.Request.Headers["X-Unrelated"] = " keep ";

        await app.Build()(context);

        Assert.Equal("tools/call", observedMethod.ToString());
        Assert.Equal(["first", "second"], observedName.Select(value => value!).ToArray());
        Assert.Equal(" keep ", observedUnrelated);
    }

    [Fact]
    public async Task McpIsDisabledByDefault()
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreateRequest("server/discover", 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task McpRejectsDnsRebindingHostsAndOrigins()
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();

        using var invalidHost = CreateRequest("server/discover", 1);
        invalidHost.Headers.Host = "evil.example.com";
        using var invalidHostResponse = await client.SendAsync(invalidHost);
        Assert.Equal(HttpStatusCode.Forbidden, invalidHostResponse.StatusCode);

        using var invalidOrigin = CreateRequest("server/discover", 2);
        invalidOrigin.Headers.Add("Origin", "https://evil.example.com");
        using var invalidOriginResponse = await client.SendAsync(invalidOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, invalidOriginResponse.StatusCode);

        using var validLoopback = CreateRequest("server/discover", 3);
        validLoopback.Headers.Host = "127.0.0.1:5220";
        validLoopback.Headers.Add("Origin", "http://127.0.0.1:5220");
        using var validLoopbackResponse = await client.SendAsync(validLoopback);
        _ = await ReadSuccessAsync(validLoopbackResponse);
    }

    [Fact]
    public async Task McpAcceptsOnlyExplicitSharedHostsAndOrigins()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:AllowedHosts:0"] = "mcp.example.com",
            ["Mcp:AllowedOrigins:0"] = "https://app.example.com"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();

        using var allowed = CreateRequest("server/discover", 1);
        allowed.Headers.Host = "mcp.example.com";
        allowed.Headers.Add("Origin", "https://app.example.com");
        using var allowedResponse = await client.SendAsync(allowed);
        _ = await ReadSuccessAsync(allowedResponse);

        using var wrongScheme = CreateRequest("server/discover", 2);
        wrongScheme.Headers.Host = "mcp.example.com";
        wrongScheme.Headers.Add("Origin", "http://app.example.com");
        using var wrongSchemeResponse = await client.SendAsync(wrongScheme);
        Assert.Equal(HttpStatusCode.Forbidden, wrongSchemeResponse.StatusCode);
    }

    [Theory]
    [InlineData("Mcp:AllowedHosts:0", "*")]
    [InlineData("Mcp:AllowedHosts:0", "mcp.example.com:443")]
    [InlineData("Mcp:AllowedHosts:0", "mcp.example.com/path")]
    [InlineData("Mcp:AllowedOrigins:0", "*")]
    [InlineData("Mcp:AllowedOrigins:0", "https://user@app.example.com")]
    [InlineData("Mcp:AllowedOrigins:0", "https://app.example.com/path")]
    [InlineData("Mcp:AllowedOrigins:0", "file://app.example.com")]
    public void McpDnsRebindingAllowlistsRequireExactHostsAndOrigins(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            [key] = value
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => VyralMcpOptions.FromConfiguration(configuration));

        Assert.Contains("Allowed", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/readiness")]
    [InlineData("/openapi/vyral.json")]
    public void McpCannotReplaceAnUnauthenticatedServerRoute(string endpointPath)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:EndpointPath"] = endpointPath
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => VyralMcpOptions.FromConfiguration(configuration));

        Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatelessDiscoveryListsToolsAndReadsResources()
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();

        using var discover = await client.SendAsync(CreateRequest("server/discover", 1));
        var discoverJson = await ReadSuccessAsync(discover);
        Assert.Equal("complete", discoverJson.GetProperty("result").GetProperty("resultType").GetString());
        Assert.Contains(ProtocolVersion, discoverJson.GetProperty("result").GetProperty("supportedVersions").EnumerateArray().Select(item => item.GetString()));
        Assert.False(discover.Headers.Contains("Mcp-Session-Id"));

        using var list = await client.SendAsync(CreateRequest("tools/list", 2));
        var listJson = await ReadSuccessAsync(list);
        var tools = listJson.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("vyral_list_collections_v1", tools);
        Assert.Contains("vyral_get_execution_run_v1", tools);
        Assert.DoesNotContain("vyral_start_execution_run_v1", tools);
        var expectedTools = VyralMcpCatalog.Entries
            .Where(entry => entry.DefaultEnabled && entry.Exposure == "tool")
            .Select(entry => entry.McpId).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedTools.SetEquals(tools!));

        using var resources = await client.SendAsync(CreateRequest("resources/list", 3));
        var resourcesJson = await ReadSuccessAsync(resources);
        var uris = resourcesJson.GetProperty("result").GetProperty("resources").EnumerateArray()
            .Select(resource => resource.GetProperty("uri").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("vyral://health/v1", uris);
        Assert.Contains("vyral://public_schema_contract/v1", uris);
        var expectedResources = VyralMcpCatalog.Entries
            .Where(entry => entry.DefaultEnabled && entry.Exposure == "resource")
            .Select(entry => entry.McpId).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedResources.SetEquals(uris!));

        using var read = await client.SendAsync(CreateRequest(
            "resources/read",
            4,
            new { uri = "vyral://health/v1" },
            "vyral://health/v1"));
        var readJson = await ReadSuccessAsync(read);
        var text = readJson.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString();
        using var health = JsonDocument.Parse(Assert.IsType<string>(text));
        Assert.True(health.RootElement.GetProperty("mcp").GetProperty("stateless").GetBoolean());
    }

    [Fact]
    public async Task DevelopmentConformanceFixturesDoNotRequireProductCatalogEntries()
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:ConformanceMode"] = "true"
        });
        using var client = factory.CreateClient();

        using var tools = await client.SendAsync(CreateRequest("tools/list", 1));
        var toolsJson = await ReadSuccessAsync(tools);
        Assert.Contains(
            toolsJson.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "test_simple_text");

        using var resources = await client.SendAsync(CreateRequest("resources/list", 2));
        var resourcesJson = await ReadSuccessAsync(resources);
        Assert.Contains(
            resourcesJson.GetProperty("result").GetProperty("resources").EnumerateArray(),
            resource => resource.GetProperty("uri").GetString() == "test://static-text");

        using var call = await client.SendAsync(CreateRequest(
            "tools/call",
            3,
            new { name = "test_simple_text", arguments = new { } },
            "test_simple_text"));
        var callJson = await ReadSuccessAsync(call);
        Assert.Contains(
            "This is a simple text response for testing.",
            callJson.GetProperty("result").GetRawText(),
            StringComparison.Ordinal);

        using var read = await client.SendAsync(CreateRequest(
            "resources/read",
            4,
            new { uri = "test://static-text" },
            "test://static-text"));
        var readJson = await ReadSuccessAsync(read);
        Assert.Equal(
            "This is the content of the static text resource.",
            readJson.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ConformanceModeCannotStartOutsideDevelopment()
    {
        var root = CreateTempRoot();
        using var factory = CreateFactory(root, new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:ConformanceMode"] = "true"
        }, environment: "Production");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Development", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentPolicyCanRemoveCatalogOperationsFromDiscoveryAndDispatch()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:DisabledOperationIds:0"] = "listCollections",
            ["Mcp:DisabledOperationIds:1"] = "vyral://health/v1"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();

        using var list = await client.SendAsync(CreateRequest("tools/list", 1));
        var listJson = await ReadSuccessAsync(list);
        Assert.DoesNotContain(
            listJson.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "vyral_list_collections_v1");

        using var call = await client.SendAsync(CreateRequest(
            "tools/call",
            2,
            new { name = "vyral_list_collections_v1", arguments = new { } },
            "vyral_list_collections_v1"));
        var callJson = await ReadJsonAsync(call);
        Assert.True(callJson.TryGetProperty("error", out var disabledError));
        Assert.Contains("unknown tool", disabledError.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        using var resources = await client.SendAsync(CreateRequest("resources/list", 3));
        var resourcesJson = await ReadSuccessAsync(resources);
        Assert.DoesNotContain(
            resourcesJson.GetProperty("result").GetProperty("resources").EnumerateArray(),
            resource => resource.GetProperty("uri").GetString() == "vyral://health/v1");
    }

    [Fact]
    public async Task ToolCallsAreDirectBoundedAndSessionFree()
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(CreateRequest(
            "tools/call",
            1,
            new { name = "vyral_list_collections_v1", arguments = new { } },
            "vyral_list_collections_v1"));
        var firstJson = await ReadSuccessAsync(first);
        Assert.Equal("complete", firstJson.GetProperty("result").GetProperty("resultType").GetString());
        Assert.False(first.Headers.Contains("Mcp-Session-Id"));

        using var second = await client.SendAsync(CreateRequest(
            "tools/call",
            2,
            new { name = "vyral_list_graph_provider_shapes_v1", arguments = new { } },
            "vyral_list_graph_provider_shapes_v1"));
        var secondJson = await ReadSuccessAsync(second);
        Assert.True(secondJson.GetProperty("result").GetProperty("structuredContent").GetArrayLength() > 0);
        Assert.False(second.Headers.Contains("Mcp-Session-Id"));

        using var get = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var delete = await client.DeleteAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    [Fact]
    public async Task McpResponsesCorrelateAndEmitOnlyCatalogTelemetryDimensions()
    {
        var measurements = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == VyralMcpTelemetry.MeterName &&
                    instrument.Name == VyralMcpTelemetry.RequestCountInstrumentName)
                    current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            measurements.Enqueue(tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value)));
        listener.Start();

        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();
        using var request = CreateRequest(
            "tools/call",
            1,
            new { name = "vyral_list_collections_v1", arguments = new { } },
            "vyral_list_collections_v1");
        request.Headers.Add("X-Correlation-ID", "mcp-telemetry-test");

        using var response = await client.SendAsync(request);
        _ = await ReadSuccessAsync(response);

        Assert.Equal("mcp-telemetry-test", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Contains(measurements, tags =>
            Equals(tags["mcp.method"], "tools/call") &&
            Equals(tags["vyral.operation.id"], "listCollections") &&
            Equals(tags["vyral.authorization.class"], "server-authenticated") &&
            Equals(tags["mcp.capability"], "tool") &&
            Equals(tags["outcome"], "success"));
    }

    [Theory]
    [InlineData(null, "server/discover")]
    [InlineData("tools/list", "server/discover")]
    public async Task MissingOrMismatchedMethodHeaderFailsSafely(string? methodHeader, string bodyMethod)
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();
        using var request = CreateRequest(bodyMethod, 1);
        request.Headers.Remove("Mcp-Method");
        if (methodHeader is not null) request.Headers.Add("Mcp-Method", methodHeader);

        using var response = await client.SendAsync(request);
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32020, json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NameMismatchUnsupportedVersionAndLimitsFailBeforeDispatch()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:MaxRequestBodyBytes"] = "1024",
            ["Mcp:MaxHeaderBytes"] = "1024"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();

        using var mismatch = CreateRequest(
            "tools/call",
            1,
            new { name = "vyral_list_collections_v1", arguments = new { } },
            "vyral_get_collection_v1");
        using var mismatchResponse = await client.SendAsync(mismatch);
        var mismatchJson = await ReadJsonAsync(mismatchResponse);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchResponse.StatusCode);
        Assert.Equal(-32020, mismatchJson.GetProperty("error").GetProperty("code").GetInt32());

        using var unsupported = CreateRequest("server/discover", 2);
        unsupported.Headers.Remove("MCP-Protocol-Version");
        unsupported.Headers.Add("MCP-Protocol-Version", "2099-01-01");
        using var unsupportedResponse = await client.SendAsync(unsupported);
        var unsupportedJson = await ReadJsonAsync(unsupportedResponse);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedResponse.StatusCode);
        Assert.Equal(-32022, unsupportedJson.GetProperty("error").GetProperty("code").GetInt32());

        using var oversizedBody = CreateRequest("server/discover", 3, new { padding = new string('x', 2_000) });
        using var oversizedBodyResponse = await client.SendAsync(oversizedBody);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedBodyResponse.StatusCode);

        using var oversizedHeader = CreateRequest("server/discover", 4);
        oversizedHeader.Headers.Add("X-Oversized", new string('x', 2_000));
        using var oversizedHeaderResponse = await client.SendAsync(oversizedHeader);
        Assert.Equal((HttpStatusCode)431, oversizedHeaderResponse.StatusCode);
    }

    [Theory]
    [InlineData("tasks/get")]
    [InlineData("tasks/cancel")]
    public async Task UnknownTaskIdsReturnInvalidParams(string method)
    {
        var root = CreateTempRoot();
        await using var factory = CreateFactory(root, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" });
        using var client = factory.CreateClient();
        const string taskId = "tasks-conformance-nonexistent-12345";

        using var response = await client.SendAsync(CreateRequest(
            method,
            1,
            new { taskId },
            taskId,
            taskCapable: true));
        var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(-32602, json.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpUsesTheSameServerAuthenticationBoundaryAsRest()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Server:RequireApiKey"] = "true",
            ["Server:ApiKey"] = "test-secret"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();

        using var denied = await client.SendAsync(CreateRequest("tools/list", 1));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var allowedRequest = CreateRequest("tools/list", 2);
        allowedRequest.Headers.Add("X-Vyral-Api-Key", "test-secret");
        using var allowed = await client.SendAsync(allowedRequest);
        _ = await ReadSuccessAsync(allowed);
    }

    [Fact]
    public async Task McpExecutionReadsEnforceTheSameProductTenantAndHandlerPolicyAsRest()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Server:ExecutionAccess:AuthenticationMode"] = "development-header",
            ["ExecutionRuntime:ProductPolicies:0:ProductId"] = "product-a",
            ["ExecutionRuntime:ProductPolicies:0:AllowedServiceIdentities:0"] = "product-a@tests.example",
            ["ExecutionRuntime:ProductPolicies:0:AllowedHandlerIds:0"] = "vyral.embedding.batch",
            ["ExecutionRuntime:ProductPolicies:1:ProductId"] = "product-b",
            ["ExecutionRuntime:ProductPolicies:1:AllowedServiceIdentities:0"] = "product-b@tests.example",
            ["ExecutionRuntime:ProductPolicies:1:AllowedHandlerIds:0"] = "vyral.embedding.batch",
            ["Server:ExecutionAccess:IdentityPolicies:0:Principal"] = "product-a@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:0:ProductId"] = "product-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedHandlerIds:0"] = "vyral.embedding.batch",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:0"] = "start_run",
            ["Server:ExecutionAccess:IdentityPolicies:0:AllowedOperations:1"] = "read_run",
            ["Server:ExecutionAccess:IdentityPolicies:1:Principal"] = "product-b@tests.example",
            ["Server:ExecutionAccess:IdentityPolicies:1:ProductId"] = "product-b",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedTenantIds:0"] = "tenant-b",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedHandlerIds:0"] = "vyral.embedding.batch",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:0"] = "start_run",
            ["Server:ExecutionAccess:IdentityPolicies:1:AllowedOperations:1"] = "read_run"
        };
        await using var factory = CreateFactory(root, settings);
        using var productA = factory.CreateClient();
        productA.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "product-a@tests.example");
        using var productB = factory.CreateClient();
        productB.DefaultRequestHeaders.Add("X-Vyral-Development-Identity", "product-b@tests.example");

        using var start = await productA.PostAsJsonAsync("/execution/runs", new
        {
            handlerId = "vyral.embedding.batch",
            pluginId = "vyral.embeddings",
            payload = new { text = "policy parity" },
            scope = new { productId = "product-a", tenantId = "tenant-a" },
            scheduledAtUtc = DateTime.UtcNow.AddMinutes(5),
            retryPolicy = new { maxAttempts = 1 }
        });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var run = Assert.IsType<ExecutionRun>(await start.Content.ReadFromJsonAsync<ExecutionRun>());

        using var restAllowed = await productA.GetAsync($"/execution/runs/{run.Id}?includeResult=false");
        Assert.Equal(HttpStatusCode.OK, restAllowed.StatusCode);
        using var restDenied = await productB.GetAsync($"/execution/runs/{run.Id}?includeResult=false");
        Assert.Equal(HttpStatusCode.Forbidden, restDenied.StatusCode);

        using var mcpAllowedRequest = CreateRequest(
            "tools/call",
            1,
            new { name = "vyral_get_execution_run_v1", arguments = new { runId = run.Id, includeResult = false } },
            "vyral_get_execution_run_v1");
        using var mcpAllowed = await productA.SendAsync(mcpAllowedRequest);
        var allowedJson = await ReadSuccessAsync(mcpAllowed);
        Assert.Equal(run.Id, allowedJson.GetProperty("result").GetProperty("structuredContent").GetProperty("id").GetString());

        using var mcpDeniedRequest = CreateRequest(
            "tools/call",
            2,
            new { name = "vyral_get_execution_run_v1", arguments = new { runId = run.Id, includeResult = false } },
            "vyral_get_execution_run_v1");
        using var mcpDenied = await productB.SendAsync(mcpDeniedRequest);
        var deniedJson = await ReadSuccessAsync(mcpDenied);
        Assert.True(deniedJson.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task OptInWriteToolUsesDurableTasksAcrossStatelessRequests()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:EnabledOperationIds:0"] = "startEmbeddingJob",
            ["Mcp:TaskPollIntervalMs"] = "10"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();

        using var discover = await client.SendAsync(CreateRequest("server/discover", 0));
        var discoverJson = await ReadSuccessAsync(discover);
        Assert.True(discoverJson.GetProperty("result").GetProperty("capabilities")
            .GetProperty("extensions").TryGetProperty("io.modelcontextprotocol/tasks", out _));

        using var list = await client.SendAsync(CreateRequest("tools/list", 1));
        var listJson = await ReadSuccessAsync(list);
        var tools = listJson.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("vyral_start_embedding_job_v1", tools);
        Assert.DoesNotContain("vyral_start_execution_run_v1", tools);

        using var inline = await client.SendAsync(CreateRequest(
            "tools/call",
            2,
            new
            {
                name = "vyral_start_embedding_job_v1",
                arguments = new { request = new { text = "inline execution" } }
            },
            "vyral_start_embedding_job_v1"));
        var inlineJson = await ReadJsonAsync(inline);
        Assert.Equal(-32021, inlineJson.GetProperty("error").GetProperty("code").GetInt32());

        using var start = await client.SendAsync(CreateRequest(
            "tools/call",
            3,
            new
            {
                name = "vyral_start_embedding_job_v1",
                arguments = new { request = new { text = "durable task execution" } }
            },
            "vyral_start_embedding_job_v1",
            taskCapable: true));
        var startJson = await ReadSuccessAsync(start);
        var created = startJson.GetProperty("result");
        Assert.True(created.GetProperty("resultType").GetString() == "task", created.GetRawText());
        Assert.Equal("working", created.GetProperty("status").GetString());
        var taskId = Assert.IsType<string>(created.GetProperty("taskId").GetString());
        Assert.True(File.Exists(Path.Combine(root, "objects", "vyral-mcp", "tasks", $"{taskId}.json")));

        await using var secondFactory = CreateFactory(root, settings);
        using var secondClient = secondFactory.CreateClient();
        JsonElement task = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await secondClient.SendAsync(CreateRequest(
                "tasks/get",
                4 + attempt,
                new { taskId },
                taskId,
                taskCapable: true));
            var pollJson = await ReadSuccessAsync(poll);
            task = pollJson.GetProperty("result");
            if (task.GetProperty("status").GetString() == "completed") break;
            await Task.Delay(20);
        }

        Assert.Equal("completed", task.GetProperty("status").GetString());
        var result = task.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var structured = result.GetProperty("structuredContent");
        Assert.Equal("succeeded", structured.GetProperty("status").GetString());
        var admission = structured.GetProperty("admission");
        Assert.Equal("vyral.admission.v1", admission.GetProperty("version").GetString());
        Assert.Equal("startEmbeddingJob", admission.GetProperty("operationId").GetString());
        Assert.Equal("accepted", admission.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(admission.GetProperty("admissionId").GetString()));
    }

    [Fact]
    public async Task ExistingDurableTaskRemainsPollableAfterItsStartToolIsDisabled()
    {
        var root = CreateTempRoot();
        var enabledSettings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:EnabledOperationIds:0"] = "startExecutionRun",
            ["Mcp:TaskPollIntervalMs"] = "10"
        };
        await using var enabledFactory = CreateFactory(root, enabledSettings);
        using var enabledClient = enabledFactory.CreateClient();

        using var start = await enabledClient.SendAsync(CreateRequest(
            "tools/call",
            1,
            new
            {
                name = "vyral_start_execution_run_v1",
                arguments = new
                {
                    request = new
                    {
                        handlerId = "vyral.embedding.batch",
                        pluginId = "vyral.embeddings",
                        payload = new { text = "survive tool disable" },
                        scheduledAtUtc = DateTime.UtcNow.AddMinutes(5),
                        retryPolicy = new { maxAttempts = 1 }
                    }
                }
            },
            "vyral_start_execution_run_v1",
            taskCapable: true));
        var startJson = await ReadSuccessAsync(start);
        var taskId = Assert.IsType<string>(startJson.GetProperty("result").GetProperty("taskId").GetString());

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await enabledClient.SendAsync(CreateRequest(
                "tasks/get",
                2 + attempt,
                new { taskId },
                taskId,
                taskCapable: true));
            var pollJson = await ReadSuccessAsync(poll);
            if (pollJson.GetProperty("result").GetProperty("statusMessage").GetString()?.Contains("queued", StringComparison.Ordinal) == true)
                break;
            await Task.Delay(10);
        }

        await using var disabledFactory = CreateFactory(root, new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:TaskPollIntervalMs"] = "10"
        });
        using var disabledClient = disabledFactory.CreateClient();
        using var list = await disabledClient.SendAsync(CreateRequest("tools/list", 200));
        var listJson = await ReadSuccessAsync(list);
        Assert.DoesNotContain(
            listJson.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "vyral_start_execution_run_v1");

        using var resumedPoll = await disabledClient.SendAsync(CreateRequest(
            "tasks/get",
            201,
            new { taskId },
            taskId,
            taskCapable: true));
        var resumedJson = await ReadSuccessAsync(resumedPoll);
        Assert.Equal("working", resumedJson.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DurableTaskInputResponsesRelayAcrossServerInstances()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:TaskPollIntervalMs"] = "10"
        };
        await using var firstFactory = CreateFactory(root, settings);
        await using var secondFactory = CreateFactory(root, settings);
        var firstStore = firstFactory.Services.GetRequiredService<VyralExecutionMcpTaskStore>();
        var secondStore = secondFactory.Services.GetRequiredService<VyralExecutionMcpTaskStore>();
        var task = await firstStore.CreateTaskAsync();
        var received = new TaskCompletionSource<InputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstStore.InputResponseReceived += args =>
        {
            if (args.TaskId == task.TaskId && args.RequestId == "request-1")
                received.TrySetResult(args.Response);
        };

        await firstStore.SetInputRequestsAsync(task.TaskId, new Dictionary<string, InputRequest>
        {
            ["request-1"] = new InputRequest
            {
                Method = "elicitation/create",
                Params = JsonSerializer.SerializeToElement(new { message = "Choose a value." })
            }
        });
        await secondStore.ResolveInputRequestsAsync(task.TaskId, new Dictionary<string, InputResponse>
        {
            ["request-1"] = new InputResponse
            {
                RawValue = JsonSerializer.SerializeToElement(new { action = "accept", content = new { value = "chosen" } })
            }
        });

        var response = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("accept", response.RawValue.GetProperty("action").GetString());
        Assert.Equal("chosen", response.RawValue.GetProperty("content").GetProperty("value").GetString());
    }

    [Fact]
    public async Task DurableTaskCancellationCancelsTheExecutionRun()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:EnabledOperationIds:0"] = "startExecutionRun",
            ["Mcp:TaskPollIntervalMs"] = "10"
        };
        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();
        var scheduledAt = DateTime.UtcNow.AddMinutes(5);

        using var start = await client.SendAsync(CreateRequest(
            "tools/call",
            1,
            new
            {
                name = "vyral_start_execution_run_v1",
                arguments = new
                {
                    request = new
                    {
                        handlerId = "vyral.embedding.batch",
                        pluginId = "vyral.embeddings",
                        payload = new { text = "cancel before dispatch" },
                        scheduledAtUtc = scheduledAt,
                        retryPolicy = new { maxAttempts = 1 }
                    }
                }
            },
            "vyral_start_execution_run_v1",
            taskCapable: true));
        var startJson = await ReadSuccessAsync(start);
        var taskId = Assert.IsType<string>(startJson.GetProperty("result").GetProperty("taskId").GetString());

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await client.SendAsync(CreateRequest(
                "tasks/get",
                2 + attempt,
                new { taskId },
                taskId,
                taskCapable: true));
            var pollJson = await ReadSuccessAsync(poll);
            if (pollJson.GetProperty("result").GetProperty("statusMessage").GetString()?.Contains("queued", StringComparison.Ordinal) == true)
                break;
            await Task.Delay(10);
        }

        using var cancel = await client.SendAsync(CreateRequest(
            "tasks/cancel",
            200,
            new { taskId },
            taskId,
            taskCapable: true));
        _ = await ReadSuccessAsync(cancel);

        JsonElement task = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await client.SendAsync(CreateRequest(
                "tasks/get",
                201 + attempt,
                new { taskId },
                taskId,
                taskCapable: true));
            var pollJson = await ReadSuccessAsync(poll);
            task = pollJson.GetProperty("result");
            if (task.GetProperty("status").GetString() == "cancelled") break;
            await Task.Delay(10);
        }

        Assert.Equal("cancelled", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImmediateCancellationOnAnotherInstanceDoesNotOrphanTheLateExecutionRun()
    {
        var root = CreateTempRoot();
        var settings = new Dictionary<string, string?>
        {
            ["Mcp:Enabled"] = "true",
            ["Mcp:EnabledOperationIds:0"] = "startExecutionRun",
            ["Mcp:TaskPollIntervalMs"] = "10"
        };
        await using var firstFactory = CreateFactory(root, settings);
        await using var secondFactory = CreateFactory(root, settings);
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();

        using var start = await firstClient.SendAsync(CreateRequest(
            "tools/call",
            1,
            new
            {
                name = "vyral_start_execution_run_v1",
                arguments = new
                {
                    request = new
                    {
                        handlerId = "vyral.embedding.batch",
                        pluginId = "vyral.embeddings",
                        payload = new { text = "cross-instance early cancellation" },
                        scheduledAtUtc = DateTime.UtcNow.AddMinutes(5),
                        retryPolicy = new { maxAttempts = 1 }
                    }
                }
            },
            "vyral_start_execution_run_v1",
            taskCapable: true));
        var startJson = await ReadSuccessAsync(start);
        var taskId = Assert.IsType<string>(startJson.GetProperty("result").GetProperty("taskId").GetString());

        using var cancel = await secondClient.SendAsync(CreateRequest(
            "tasks/cancel",
            2,
            new { taskId },
            taskId,
            taskCapable: true));
        _ = await ReadSuccessAsync(cancel);

        ExecutionRun? associatedRun = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var runs = await secondClient.GetFromJsonAsync<List<ExecutionRun>>(
                "/execution/runs?handlerId=vyral.embedding.batch&includeResult=false");
            associatedRun = runs?.SingleOrDefault();
            if (associatedRun is not null &&
                (associatedRun.CancellationRequested || associatedRun.Status == ExecutionRunStatuses.Cancelled))
                break;
            await Task.Delay(10);
        }

        Assert.NotNull(associatedRun);
        Assert.True(
            associatedRun.CancellationRequested || associatedRun.Status == ExecutionRunStatuses.Cancelled,
            $"Late-associated run remained active: {associatedRun.Status}.");
        using var poll = await secondClient.SendAsync(CreateRequest(
            "tasks/get",
            3,
            new { taskId },
            taskId,
            taskCapable: true));
        var pollJson = await ReadSuccessAsync(poll);
        Assert.Equal("cancelled", pollJson.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AllCatalogTaskToolsCanBeExplicitlyEnabled()
    {
        var root = CreateTempRoot();
        var taskEntries = VyralMcpCatalog.Entries.Where(entry => entry.Exposure == "task").ToList();
        var settings = new Dictionary<string, string?> { ["Mcp:Enabled"] = "true" };
        for (var index = 0; index < taskEntries.Count; index++)
            settings[$"Mcp:EnabledOperationIds:{index}"] = taskEntries[index].OperationId;

        await using var factory = CreateFactory(root, settings);
        using var client = factory.CreateClient();
        using var list = await client.SendAsync(CreateRequest("tools/list", 1));
        var listJson = await ReadSuccessAsync(list);
        var actual = listJson.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        var expected = VyralMcpCatalog.Entries
            .Where(entry => entry.DefaultEnabled || entry.Exposure == "task")
            .Where(entry => entry.Exposure is "tool" or "task")
            .Select(entry => entry.McpId).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(12, taskEntries.Count);
        Assert.True(expected.SetEquals(actual!));
    }

    private static HttpRequestMessage CreateRequest(
        string method,
        int id,
        object? parameters = null,
        string? name = null,
        bool taskCapable = false)
    {
        parameters ??= new { };
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = AddRequiredMetadata(parameters, taskCapable)
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        request.Headers.Add("Mcp-Method", method);
        if (name is not null) request.Headers.Add("Mcp-Name", name);
        return request;
    }

    private static Dictionary<string, object?> AddRequiredMetadata(object parameters, bool taskCapable)
    {
        var json = JsonSerializer.SerializeToElement(parameters);
        var result = json.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        result["_meta"] = new Dictionary<string, object?>
        {
            ["io.modelcontextprotocol/protocolVersion"] = ProtocolVersion,
            ["io.modelcontextprotocol/clientInfo"] = new { name = "vyral-tests", version = "1.0.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = taskCapable
                ? new
                {
                    extensions = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/tasks"] = new { }
                    }
                }
                : new { }
        };
        return result;
    }

    private static async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response)
    {
        var json = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.TryGetProperty("error", out _));
        return json;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var payloads = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].TrimStart())
                .Where(line => line.Length > 0)
                .ToList();
            Assert.Single(payloads);
            text = payloads[0];
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                $"Expected JSON but received {(int)response.StatusCode} {response.Content.Headers.ContentType}: {text}",
                error);
        }
        using (document)
        return document.RootElement.Clone();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "vyral-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string root,
        IReadOnlyDictionary<string, string?>? settings = null,
        string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("DatabasePath", Path.Combine(root, "vyral.sqlite"));
            builder.UseSetting("ObjectsPath", Path.Combine(root, "objects"));
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["DatabasePath"] = Path.Combine(root, "vyral.sqlite"),
                    ["ObjectsPath"] = Path.Combine(root, "objects")
                };
                foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                    values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        });
}
