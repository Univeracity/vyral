using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Vyral.Abstractions.Interfaces;
using Vyral.Execution;

namespace Vyral.Mcp;

[McpServerResourceType]
public sealed class VyralMcpResources
{
    [McpServerResource(
        UriTemplate = "vyral://health/v1",
        Name = "vyral_health_v1",
        Title = "Vyral health",
        MimeType = "application/json")]
    [Description("A small, non-secret health summary for this Vyral instance.")]
    public static string GetHealth(
        IRecordCollectionStore records,
        IExecutionRuntimeAdapter execution) =>
        JsonSerializer.Serialize(new
        {
            status = "ok",
            protocolVersion = VyralMcpOptions.ProtocolVersion,
            mcp = new { enabled = true, stateless = true },
            recordStore = records.GetType().Name,
            executionAdapter = execution.Adapter.AdapterId
        }, JsonOptions);

    [McpServerResource(
        UriTemplate = "vyral://readiness/v1",
        Name = "vyral_readiness_v1",
        Title = "Vyral readiness",
        MimeType = "application/json")]
    [Description("A bounded readiness check for the configured record and execution services.")]
    public static async Task<string> GetReadiness(
        IRecordCollectionStore records,
        IExecutionRuntimeAdapter execution,
        CancellationToken cancellationToken)
    {
        var collections = (await records.GetCollectionsAsync(cancellationToken)).Take(1).Count();
        var adapter = await execution.GetAdapterStatusAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            status = adapter.Available ? "ready" : "not_ready",
            checkedAtUtc = adapter.CheckedAtUtc,
            protocolVersion = VyralMcpOptions.ProtocolVersion,
            mcp = new { enabled = true, stateless = true },
            recordStoreReachable = collections is 0 or 1,
            execution = adapter
        }, JsonOptions);
    }

    [McpServerResource(
        UriTemplate = "vyral://open_api_contract/v1",
        Name = "vyral_open_api_contract_v1",
        Title = "Vyral OpenAPI contract",
        MimeType = "application/json")]
    [Description("The authoritative Vyral REST/OpenAPI 3.1 contract.")]
    public static Task<string> GetOpenApiContract(CancellationToken cancellationToken) =>
        ReadEmbeddedAsync("Vyral.Mcp.contracts.vyral.openapi.json", cancellationToken);

    [McpServerResource(
        UriTemplate = "vyral://public_schema_contract/v1",
        Name = "vyral_public_schema_contract_v1",
        Title = "Vyral public JSON Schema contract",
        MimeType = "application/schema+json")]
    [Description("The canonical JSON Schema 2020-12 bundle for Vyral public contracts.")]
    public static Task<string> GetPublicSchemaContract(CancellationToken cancellationToken) =>
        ReadEmbeddedAsync("Vyral.Mcp.contracts.vyral-public.schema.json", cancellationToken);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<string> ReadEmbeddedAsync(string name, CancellationToken cancellationToken)
    {
        await using var stream = typeof(VyralMcpResources).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded MCP resource '{name}' is unavailable.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
