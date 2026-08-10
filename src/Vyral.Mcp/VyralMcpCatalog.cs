using System.Reflection;
using System.Text.Json;

namespace Vyral.Mcp;

public sealed record VyralMcpCatalogEntry(
    string OperationId,
    string Exposure,
    string McpId,
    bool DefaultEnabled,
    string AuthorizationClass,
    string Mutation,
    string Idempotency,
    IReadOnlyList<string> HeaderSafeFields);

public static class VyralMcpCatalog
{
    private static readonly Lazy<IReadOnlyList<VyralMcpCatalogEntry>> EntriesValue =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<VyralMcpCatalogEntry> Entries => EntriesValue.Value;

    public static bool IsDisabled(VyralMcpCatalogEntry entry, IReadOnlySet<string> disabled) =>
        disabled.Contains(entry.OperationId) || disabled.Contains(entry.McpId);

    public static bool IsEnabled(VyralMcpCatalogEntry entry, VyralMcpOptions options) =>
        !IsDisabled(entry, options.DisabledOperationIds) &&
        (entry.DefaultEnabled ||
            options.EnabledOperationIds.Contains(entry.OperationId) ||
            options.EnabledOperationIds.Contains(entry.McpId));

    public static VyralMcpCatalogEntry GetByMcpId(string mcpId) =>
        Entries.Single(entry => string.Equals(entry.McpId, mcpId, StringComparison.Ordinal));

    public static bool TryGetByMcpId(string mcpId, out VyralMcpCatalogEntry entry)
    {
        entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.McpId, mcpId, StringComparison.Ordinal))!;
        return entry is not null;
    }

    private static IReadOnlyList<VyralMcpCatalogEntry> Load()
    {
        using var stream = typeof(VyralMcpCatalog).Assembly.GetManifestResourceStream(
            "Vyral.Mcp.contracts.public-sdk-surface.json")
            ?? throw new InvalidOperationException("The embedded public SDK catalog is unavailable.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("operations").EnumerateArray()
            .Where(operation => operation.GetProperty("mcp").GetProperty("exposure").GetString() != "none")
            .Select(operation =>
            {
                var mcp = operation.GetProperty("mcp");
                return new VyralMcpCatalogEntry(
                    operation.GetProperty("id").GetString()!,
                    mcp.GetProperty("exposure").GetString()!,
                    mcp.GetProperty("id").GetString()!,
                    mcp.GetProperty("defaultEnabled").GetBoolean(),
                    operation.GetProperty("authorizationClass").GetString()!,
                    operation.GetProperty("mutation").GetString()!,
                    operation.GetProperty("idempotency").GetString()!,
                    operation.GetProperty("headerSafeFields").EnumerateArray()
                        .Select(field => field.GetString()!).ToList());
            })
            .ToList();
    }
}
