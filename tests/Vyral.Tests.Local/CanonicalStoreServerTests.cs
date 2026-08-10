using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Server;

namespace Vyral.Tests.Local;

public sealed class CanonicalStoreServerTests
{
    [Fact]
    public void CanonicalStoreOptions_RecognizeCloudSqlMySql()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CanonicalStore:Provider"] = "cloud-sql-mysql",
            ["CanonicalStore:MySql:ConnectionString"] = "Server=localhost;Database=vyral;User ID=test;Password=test"
        }).Build();

        var options = CanonicalStoreOptions.FromConfiguration(configuration, "vyral.sqlite");

        Assert.Equal(CanonicalStoreOptions.MySql, options.Provider);
        Assert.Equal("Server=localhost;Database=vyral;User ID=test;Password=test", options.ConnectionString);
        Assert.Equal("provider=mysql; connectionString=configured", options.Describe());
    }

    [Fact]
    public async Task Server_ExposesCanonicalTransactionOutboxAndSnapshotSurface()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-server-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-server-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath);
        var client = factory.CreateClient();
        var openApi = JsonNode.Parse(await client.GetStringAsync("/openapi/vyral.json"))!;
        Assert.Equal("commitCanonicalTransaction", openApi["paths"]!["/canonical/tenants/{tenantId}/transactions"]!["post"]!["operationId"]!.GetValue<string>());
        Assert.Equal("readCanonicalDocument", openApi["paths"]!["/canonical/tenants/{tenantId}/documents/read"]!["post"]!["operationId"]!.GetValue<string>());
        Assert.NotNull(openApi["paths"]!["/canonical/tenants/{tenantId}/outbox/{eventId}/renew"]);
        Assert.Equal("probeCanonicalStoreDataPlane", openApi["paths"]!["/canonical/preflight/probe"]!["post"]!["operationId"]!.GetValue<string>());
        Assert.NotNull(openApi["components"]!["schemas"]!["CanonicalTenantSnapshot"]);
        Assert.NotNull(openApi["components"]!["schemas"]!["CanonicalDataPlanePreflightResult"]);
        var health = await client.GetFromJsonAsync<ServerHealthStatus>("/health");
        Assert.Equal("SqliteCanonicalStore", health!.Storage.CanonicalStore);
        var readiness = await client.GetFromJsonAsync<ServerReadinessReport>("/readiness");
        Assert.Equal("ok", Assert.Single(readiness!.Checks, item => item.Id == "storage.canonical").Status);
        var passivePreflight = await client.GetFromJsonAsync<CanonicalPreflightReport>("/canonical/preflight");
        Assert.Null(passivePreflight!.DataPlaneProbe);
        using var probeResponse = await client.PostAsync("/canonical/preflight/probe", content: null);
        probeResponse.EnsureSuccessStatusCode();
        var probeJson = await probeResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("vyral-preflight-", probeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantId", probeJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", probeJson, StringComparison.OrdinalIgnoreCase);
        var preflight = await probeResponse.Content.ReadFromJsonAsync<CanonicalPreflightReport>();
        Assert.True(preflight!.DataPlaneProbe!.Ready);
        Assert.True(preflight.DataPlaneProbe.BackupRestoreVerified);
        Assert.True(preflight.DataPlaneProbe.TenantIsolationVerified);
        Assert.True(preflight.DataPlaneProbe.CleanupVerified);
        var applyMigration = await client.PostAsJsonAsync("/canonical/migrations", new[]
        {
            new CanonicalMigration { Namespace = "server-test", Id = "canonical.server.v1", Checksum = "sha256:canonical-server-v1" }
        });
        Assert.Equal(HttpStatusCode.NoContent, applyMigration.StatusCode);
        Assert.Equal("canonical.server.v1", Assert.Single((await client.GetFromJsonAsync<List<CanonicalMigrationReceipt>>("/canonical/migrations"))!).Id);
        var request = CreateTransaction("tenant-a", "create-a", "a-1", "Ada", includeOutbox: true);

        var committed = await PostAsync<CanonicalTransactionResult>(client, "/canonical/tenants/tenant-a/transactions", request);
        Assert.False(committed.Replayed);
        Assert.Equal("a-1", Assert.Single(committed.Documents).Id);
        var document = await client.GetFromJsonAsync<CanonicalDocument>("/canonical/tenants/tenant-a/documents/author/a-1");
        Assert.Equal("Ada", document!.Data!["name"]!.GetValue<string>());
        var slashIdentity = CreateTransaction("tenant-a", "create-slash", "a/with-slash", "Encoded");
        slashIdentity.Mutations[0].Document!.DocumentType = "author/type";
        await PostAsync<CanonicalTransactionResult>(client, "/canonical/tenants/tenant-a/transactions", slashIdentity);
        var slashResponse = await client.PostAsJsonAsync("/canonical/tenants/tenant-a/documents/read", new CanonicalDocumentReadRequest
        {
            TenantId = "tenant-a",
            DocumentType = "author/type",
            Id = "a/with-slash"
        });
        slashResponse.EnsureSuccessStatusCode();
        var slashDocument = await slashResponse.Content.ReadFromJsonAsync<CanonicalDocument>();
        Assert.Equal("a/with-slash", slashDocument!.Id);
        var query = await PostAsync<CanonicalDocumentQueryResult>(client, "/canonical/tenants/tenant-a/documents/query", new CanonicalDocumentQuery
        {
            TenantId = "tenant-a", DocumentType = "author", Indexes = new Dictionary<string, string> { ["email"] = "a-1@example.test" }
        });
        Assert.Equal("a-1", Assert.Single(query.Items).Id);

        var lease = Assert.Single(await PostAsync<List<CanonicalOutboxLease>>(client, "/canonical/tenants/tenant-a/outbox/leases", new CanonicalOutboxLeaseRequest
        {
            TenantId = "tenant-a", ConsumerId = "server-test", LeaseSeconds = 60
        }));
        var renewal = await PostAsync<CanonicalOutboxLeaseRenewal>(client, $"/canonical/tenants/tenant-a/outbox/{lease.Event.Id}/renew", new CanonicalOutboxLeaseRenewalRequest
        {
            TenantId = "tenant-a", EventId = lease.Event.Id, LeaseToken = lease.LeaseToken, LeaseSeconds = 120
        });
        Assert.True(renewal.ExpiresAtUtc > lease.ExpiresAtUtc);
        var outbox = await PostAsync<CanonicalOutboxQueryResult>(client, "/canonical/tenants/tenant-a/outbox/query", new CanonicalOutboxQuery { TenantId = "tenant-a", State = CanonicalOutboxStates.Leased });
        Assert.Equal(lease.Event.Id, Assert.Single(outbox.Items).Id);
        var acknowledgement = await client.PostAsJsonAsync($"/canonical/tenants/tenant-a/outbox/{lease.Event.Id}/ack", new CanonicalOutboxAcknowledgement { LeaseToken = lease.LeaseToken });
        Assert.Equal(HttpStatusCode.NoContent, acknowledgement.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/canonical/tenants/tenant-a/outbox/{lease.Event.Id}/ack", new CanonicalOutboxAcknowledgement { LeaseToken = lease.LeaseToken })).StatusCode);

        var exported = await client.GetFromJsonAsync<CanonicalTenantSnapshot>("/canonical/tenants/tenant-a/export");
        Assert.NotNull(exported);
        Assert.StartsWith("sha256:", exported!.ContentHash);
        await PostAsync<CanonicalTransactionResult>(client, "/canonical/tenants/tenant-a/transactions", CreateTransaction("tenant-a", "create-b", "a-2", "Grace"));
        var restore = await client.PostAsJsonAsync("/canonical/tenants/tenant-a/restore", new CanonicalRestoreRequest { Snapshot = exported, ExpectedContentHash = exported.ContentHash });
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/canonical/tenants/tenant-a/documents/author/a-2")).StatusCode);
    }

    [Fact]
    public async Task Server_EnforcesTenantScopedCanonicalPolicy()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-access-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-access-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath, new Dictionary<string, string?>
        {
            ["Server:CanonicalAccess:AuthenticationMode"] = TestCanonicalIdentityAuthenticator.Mode,
            ["Server:CanonicalAccess:AuthenticatorType"] = typeof(TestCanonicalIdentityAuthenticator).AssemblyQualifiedName,
            ["Server:CanonicalAccess:IdentityPolicies:0:Principal"] = "canon-a@tests.example",
            ["Server:CanonicalAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:0"] = CanonicalAccessOperations.Write,
            ["Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:1"] = CanonicalAccessOperations.Read,
            ["Server:CanonicalAccess:IdentityPolicies:1:Principal"] = "canon-a@tests.example",
            ["Server:CanonicalAccess:IdentityPolicies:1:AllowedTenantIds:0"] = "*",
            ["Server:CanonicalAccess:IdentityPolicies:1:AllowedOperations:0"] = CanonicalAccessOperations.Admin
        });
        var allowed = factory.CreateClient();
        allowed.DefaultRequestHeaders.Add(TestCanonicalIdentityAuthenticator.HeaderName, "canon-a@tests.example");
        Assert.Equal(HttpStatusCode.OK, (await allowed.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", CreateTransaction("tenant-a", "allowed", "a-1", "Ada"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await allowed.PostAsJsonAsync("/canonical/tenants/tenant-b/transactions", CreateTransaction("tenant-b", "denied", "b-1", "Grace"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().GetAsync("/canonical/tenants/tenant-a/documents/author/a-1")).StatusCode);
        var preflight = await allowed.GetFromJsonAsync<CanonicalPreflightReport>("/canonical/preflight");
        Assert.True(preflight!.TenantPoliciesEnforced);
        Assert.Equal(CanonicalStoreOptions.LocalSqlite, preflight.Provider);
        using var probe = await allowed.PostAsync("/canonical/preflight/probe", content: null);
        probe.EnsureSuccessStatusCode();
        Assert.True((await probe.Content.ReadFromJsonAsync<CanonicalPreflightReport>())!.DataPlaneProbe!.Ready);
    }

    [Fact]
    public async Task Server_RequiresBothGenericApiKeyAndCanonicalIdentityWhenConfigured()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-layered-access-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-layered-access-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath, new Dictionary<string, string?>
        {
            ["Server:ApiKey"] = "canonical-test-key",
            ["Server:CanonicalAccess:AuthenticationMode"] = TestCanonicalIdentityAuthenticator.Mode,
            ["Server:CanonicalAccess:AuthenticatorType"] = typeof(TestCanonicalIdentityAuthenticator).AssemblyQualifiedName,
            ["Server:CanonicalAccess:IdentityPolicies:0:Principal"] = "canon-a@tests.example",
            ["Server:CanonicalAccess:IdentityPolicies:0:AllowedTenantIds:0"] = "tenant-a",
            ["Server:CanonicalAccess:IdentityPolicies:0:AllowedOperations:0"] = CanonicalAccessOperations.Write
        });
        var missingApiKey = factory.CreateClient();
        missingApiKey.DefaultRequestHeaders.Add(TestCanonicalIdentityAuthenticator.HeaderName, "canon-a@tests.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await missingApiKey.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", CreateTransaction("tenant-a", "missing-api-key", "a-1", "Ada"))).StatusCode);

        var allowed = factory.CreateClient();
        allowed.DefaultRequestHeaders.Add("X-Vyral-Api-Key", "canonical-test-key");
        allowed.DefaultRequestHeaders.Add(TestCanonicalIdentityAuthenticator.HeaderName, "canon-a@tests.example");
        Assert.Equal(HttpStatusCode.OK, (await allowed.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", CreateTransaction("tenant-a", "layered-access", "a-1", "Ada"))).StatusCode);
    }

    [Fact]
    public async Task Server_RateLimitsCanonicalRoutesBeforeStorageWork()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-rate-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-rate-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath, new Dictionary<string, string?>
        {
            ["Server:CanonicalRateLimit:PermitLimit"] = "1",
            ["Server:CanonicalRateLimit:WindowSeconds"] = "60"
        });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/canonical/migrations")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.GetAsync("/canonical/migrations")).StatusCode);
    }

    [Fact]
    public void ProductionServer_RequiresExplicitCanonicalIdentityPolicies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-production-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-production-store-{Guid.NewGuid():N}.sqlite");
        using var factory = CreateFactory(dbPath, canonicalPath, environment: Environments.Production);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("IdentityPolicies", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionExecutionOnlyServer_ExplicitlyDisablesCanonicalStoreSurface()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-disabled-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-disabled-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath, new Dictionary<string, string?>
        {
            ["CanonicalStore:Enabled"] = "false",
            ["CanonicalStore:Provider"] = "unsupported-provider"
        }, Environments.Production);
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<ServerHealthStatus>("/health");
        Assert.Equal("disabled", health!.Storage.CanonicalStore);
        var readiness = await client.GetFromJsonAsync<ServerReadinessReport>("/readiness");
        var canonicalReadiness = Assert.Single(readiness!.Checks, item => item.Id == "storage.canonical");
        Assert.Equal("ok", canonicalReadiness.Status);
        Assert.Equal("Canonical store is disabled for this server.", canonicalReadiness.Message);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/canonical/preflight")).StatusCode);
        var openApi = JsonNode.Parse(await client.GetStringAsync("/openapi/vyral.json"))!;
        Assert.Null(openApi["paths"]!["/canonical/preflight"]);
        Assert.Null(openApi["paths"]!["/canonical/preflight/probe"]);
    }

    [Fact]
    public async Task Server_RejectsPathUnsafeTenantAndOutboxIds()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-route-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-canonical-route-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath);
        var client = factory.CreateClient();
        var unsafeTenant = CreateTransaction("tenant/unsafe", "unsafe-tenant", "a-1", "Ada");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", unsafeTenant)).StatusCode);

        var unsafeEvent = CreateTransaction("tenant-a", "unsafe-event", "a-1", "Ada", includeOutbox: true);
        unsafeEvent.Outbox[0].Id = "event/unsafe";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", unsafeEvent)).StatusCode);
    }

    [Fact]
    public async Task Server_EvidenceBriefHelpers_UseCanonicalRoutesAndRejectInvalidRawDocuments()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-evidence-brief-server-{Guid.NewGuid():N}.sqlite");
        var canonicalPath = Path.Combine(Path.GetTempPath(), $"vyral-evidence-brief-server-store-{Guid.NewGuid():N}.sqlite");
        await using var factory = CreateFactory(dbPath, canonicalPath);
        var client = factory.CreateClient();
        var request = new EvidenceBriefWriteRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "brief:rates:server:v1",
            Brief = EvidenceBriefTestData.Create()
        };

        var committed = await client.StoreEvidenceBriefAsync(request);
        var stored = await client.GetEvidenceBriefAsync("tenant-a", request.Brief.Id);

        Assert.Equal(request.Brief.Id, Assert.Single(committed.Documents).Id);
        Assert.NotNull(stored);
        Assert.Equal(request.Brief.Question, stored!.Brief.Question);

        var invalid = EvidenceBriefContract.CreateUpsertTransaction(new EvidenceBriefWriteRequest
        {
            TenantId = "tenant-a",
            IdempotencyKey = "brief:rates:invalid:v1",
            Brief = EvidenceBriefTestData.Create()
        });
        invalid.Mutations[0].Document!.Data!["sourceSnapshots"]![0]!["uri"] = "https://example.test/rates/schedule?signed=not-allowed";
        var response = await client.PostAsJsonAsync("/canonical/tenants/tenant-a/transactions", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static CanonicalTransactionRequest CreateTransaction(string tenantId, string idempotencyKey, string id, string name, bool includeOutbox = false) => new()
    {
        TenantId = tenantId,
        IdempotencyKey = idempotencyKey,
        Mutations = new List<CanonicalMutation>
        {
            new()
            {
                Document = new CanonicalDocument
                {
                    TenantId = tenantId, DocumentType = "author", Id = id, SchemaVersion = "v1",
                    Data = new JsonObject { ["name"] = name, ["email"] = $"{id}@example.test" },
                    Indexes = new Dictionary<string, string> { ["email"] = $"{id}@example.test" }
                }
            }
        },
        Outbox = includeOutbox ? new List<CanonicalOutboxWrite> { new() { Topic = "author.changed", Key = id, Payload = new JsonObject { ["id"] = id } } } : new List<CanonicalOutboxWrite>()
    };

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static WebApplicationFactory<Program> CreateFactory(string dbPath, string canonicalPath, IReadOnlyDictionary<string, string?>? settings = null, string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            var values = new Dictionary<string, string?>
            {
                ["DatabasePath"] = dbPath,
                ["ObjectsPath"] = Path.Combine(Path.GetTempPath(), $"vyral-canonical-objects-{Guid.NewGuid():N}"),
                ["CanonicalStore:Provider"] = CanonicalStoreOptions.LocalSqlite,
                ["CanonicalStore:DatabasePath"] = canonicalPath
            };
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) values[key] = value;
            foreach (var (key, value) in values) builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
        });
}

public sealed class TestCanonicalIdentityAuthenticator : ICanonicalIdentityAuthenticator
{
    public const string Mode = "test-canonical-header";
    public const string HeaderName = "X-Test-Canonical-Identity";
    public string AuthenticationMode => Mode;

    public Task<string> AuthenticateAsync(HttpContext context, VyralCanonicalAccessOptions options, IHostEnvironment environment, CancellationToken ct = default)
    {
        var principal = context.Request.Headers[HeaderName].ToString().Trim();
        if (string.IsNullOrWhiteSpace(principal)) throw new CanonicalAccessDeniedException("A test canonical identity header is required.");
        return Task.FromResult(principal);
    }
}
