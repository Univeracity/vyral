using Microsoft.Extensions.Configuration;

namespace Vyral.Mcp;

public sealed class VyralMcpOptions
{
    public const string SectionName = "Mcp";
    public const string ProtocolVersion = "2026-07-28";

    public bool Enabled { get; init; }
    public bool ConformanceMode { get; init; }
    public string EndpointPath { get; init; } = "/mcp";
    public long MaxRequestBodyBytes { get; init; } = 1_048_576;
    public int MaxHeaderCount { get; init; } = 64;
    public int MaxHeaderBytes { get; init; } = 16_384;
    public IReadOnlySet<string> AllowedHosts { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> AllowedOrigins { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan TaskTimeToLive { get; init; } = TimeSpan.FromHours(24);
    public long TaskPollIntervalMs { get; init; } = 1_000;
    public IReadOnlySet<string> EnabledOperationIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> DisabledOperationIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public static VyralMcpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var endpointPath = section["EndpointPath"]?.Trim();
        if (string.IsNullOrEmpty(endpointPath)) endpointPath = "/mcp";
        if (!endpointPath.StartsWith('/')) endpointPath = "/" + endpointPath;
        if (endpointPath.Any(char.IsWhiteSpace) || endpointPath.Contains('?') || endpointPath.Contains('#'))
            throw new InvalidOperationException("Mcp:EndpointPath must be an absolute path without whitespace, a query, or a fragment.");

        var result = new VyralMcpOptions
        {
            Enabled = ParseBool(section["Enabled"], false),
            ConformanceMode = ParseBool(section["ConformanceMode"], false),
            EndpointPath = endpointPath,
            MaxRequestBodyBytes = ParsePositiveLong(section["MaxRequestBodyBytes"], 1_048_576),
            MaxHeaderCount = ParsePositiveInt(section["MaxHeaderCount"], 64),
            MaxHeaderBytes = ParsePositiveInt(section["MaxHeaderBytes"], 16_384),
            AllowedHosts = ReadAllowedHosts(section),
            AllowedOrigins = ReadAllowedOrigins(section),
            TaskTimeToLive = TimeSpan.FromSeconds(ParsePositiveLong(section["TaskTimeToLiveSeconds"], 86_400)),
            TaskPollIntervalMs = ParsePositiveLong(section["TaskPollIntervalMs"], 1_000),
            EnabledOperationIds = ReadOperationIds(section, "EnabledOperationIds"),
            DisabledOperationIds = section.GetSection("DisabledOperationIds")
                .GetChildren()
                .Select(child => child.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal)
        };

        if (result.MaxRequestBodyBytes > 16 * 1024 * 1024)
            throw new InvalidOperationException("Mcp:MaxRequestBodyBytes cannot exceed 16 MiB.");
        if (result.MaxHeaderCount > 256)
            throw new InvalidOperationException("Mcp:MaxHeaderCount cannot exceed 256.");
        if (result.MaxHeaderBytes > 64 * 1024)
            throw new InvalidOperationException("Mcp:MaxHeaderBytes cannot exceed 64 KiB.");
        if (result.TaskTimeToLive > TimeSpan.FromDays(30))
            throw new InvalidOperationException("Mcp:TaskTimeToLiveSeconds cannot exceed 30 days.");
        if (result.TaskPollIntervalMs > 60_000)
            throw new InvalidOperationException("Mcp:TaskPollIntervalMs cannot exceed 60000.");
        if (result.ConformanceMode && !result.Enabled)
            throw new InvalidOperationException("Mcp:ConformanceMode requires Mcp:Enabled=true.");
        if (ReservedPublicPaths.Contains(result.EndpointPath))
            throw new InvalidOperationException($"Mcp:EndpointPath '{result.EndpointPath}' is reserved as an unauthenticated server route.");

        var knownIds = VyralMcpCatalog.Entries
            .SelectMany(entry => new[] { entry.OperationId, entry.McpId })
            .ToHashSet(StringComparer.Ordinal);
        var unknownIds = result.EnabledOperationIds.Concat(result.DisabledOperationIds)
            .Where(id => !knownIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownIds.Count > 0)
            throw new InvalidOperationException($"Unknown MCP operation id(s): {string.Join(", ", unknownIds)}.");

        return result;
    }

    private static IReadOnlySet<string> ReadOperationIds(IConfigurationSection section, string name) =>
        section.GetSection(name)
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> ReadAllowedHosts(IConfigurationSection section)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in ReadValues(section, "AllowedHosts"))
        {
            if (configured == "*" ||
                configured.Any(char.IsWhiteSpace) ||
                configured.Contains('/') ||
                configured.Contains(',') ||
                configured.Contains('@'))
            {
                throw new InvalidOperationException(
                    "Mcp:AllowedHosts entries must be exact host names or IP literals without wildcards, ports, or paths.");
            }

            var host = configured.StartsWith('[') && configured.EndsWith(']')
                ? configured[1..^1]
                : configured;
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new InvalidOperationException(
                    $"Mcp:AllowedHosts contains an invalid host: '{configured}'.");
            }
            result.Add(host);
        }
        return result;
    }

    private static IReadOnlySet<string> ReadAllowedOrigins(IConfigurationSection section)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in ReadValues(section, "AllowedOrigins"))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) ||
                (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(origin.UserInfo) ||
                origin.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(origin.Query) ||
                !string.IsNullOrEmpty(origin.Fragment))
            {
                throw new InvalidOperationException(
                    $"Mcp:AllowedOrigins must contain exact HTTP(S) origins without paths, queries, or fragments: '{configured}'.");
            }
            result.Add(origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        }
        return result;
    }

    private static IEnumerable<string> ReadValues(IConfigurationSection section, string name) =>
        section.GetSection(name)
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>();

    private static bool ParseBool(string? value, bool fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
        bool.TryParse(value, out var parsed) ? parsed :
        throw new InvalidOperationException($"Configuration value '{value}' must be true or false.");

    private static int ParsePositiveInt(string? value, int fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed :
        throw new InvalidOperationException($"Configuration value '{value}' must be a positive integer.");

    private static long ParsePositiveLong(string? value, long fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed :
        throw new InvalidOperationException($"Configuration value '{value}' must be a positive integer.");

    private static readonly IReadOnlySet<string> ReservedPublicPaths = new HashSet<string>(
        [
            "/",
            "/health",
            "/readiness",
            "/openapi/vyral.json",
            "/contracts/schemas/vyral-public.schema.json"
        ],
        StringComparer.OrdinalIgnoreCase);
}
