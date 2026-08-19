using Microsoft.Extensions.Configuration;
using Vyral.Server;

namespace Vyral.HostedWorker;

/// <summary>
/// Deployment-owned configuration for the Vyral generic-handler worker. It deliberately has no
/// consumer, queue, collection, or provenance defaults.
/// </summary>
public sealed class HostedWorkerOptions
{
    public string VyralUrl { get; init; } = string.Empty;
    public string WorkerId { get; init; } = string.Empty;
    public IReadOnlyList<string> HandlerIds { get; init; } = Array.Empty<string>();
    public string? ApiKey { get; init; }
    public string ApiKeyHeader { get; init; } = "X-Vyral-Api-Key";
    public string CallbackPath { get; init; } = "/tasks/execution";
    public double LeaseTtlSeconds { get; init; } = 60;
    public int HeartbeatSeconds { get; init; } = 20;
    public HostedWorkerTaskAuthenticationOptions TaskAuthentication { get; init; } = new();

    public static HostedWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("HostedWorker");
        return new HostedWorkerOptions
        {
            VyralUrl = FirstNonEmpty(section["VyralUrl"], section["vyralUrl"], configuration["VYRAL_HOSTED_WORKER_VYRAL_URL"]) ?? string.Empty,
            WorkerId = FirstNonEmpty(section["WorkerId"], section["workerId"], configuration["VYRAL_HOSTED_WORKER_ID"]) ?? string.Empty,
            HandlerIds = ReadValues(section, "HandlerIds", "handlerIds", configuration["VYRAL_HOSTED_WORKER_HANDLER_IDS"]),
            ApiKey = FirstNonEmpty(section["ApiKey"], section["apiKey"], configuration["VYRAL_HOSTED_WORKER_API_KEY"]),
            ApiKeyHeader = FirstNonEmpty(section["ApiKeyHeader"], section["apiKeyHeader"], configuration["VYRAL_HOSTED_WORKER_API_KEY_HEADER"], "X-Vyral-Api-Key")!,
            CallbackPath = FirstNonEmpty(section["CallbackPath"], section["callbackPath"], configuration["VYRAL_HOSTED_WORKER_CALLBACK_PATH"], "/tasks/execution")!,
            LeaseTtlSeconds = ParsePositiveDouble(FirstNonEmpty(section["LeaseTtlSeconds"], section["leaseTtlSeconds"], configuration["VYRAL_HOSTED_WORKER_LEASE_TTL_SECONDS"]), 60),
            HeartbeatSeconds = ParsePositiveInt(FirstNonEmpty(section["HeartbeatSeconds"], section["heartbeatSeconds"], configuration["VYRAL_HOSTED_WORKER_HEARTBEAT_SECONDS"]), 20),
            TaskAuthentication = HostedWorkerTaskAuthenticationOptions.FromConfiguration(configuration)
        };
    }

    public void Validate()
    {
        if (!Uri.TryCreate(VyralUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("HostedWorker:VyralUrl must be an absolute URL without user credentials.");
        }
        if (string.IsNullOrWhiteSpace(WorkerId)) throw new InvalidOperationException("HostedWorker:WorkerId is required.");
        if (HandlerIds.Count == 0) throw new InvalidOperationException("HostedWorker:HandlerIds requires at least one Vyral hosted handler.");
        if (HandlerIds.Any(id => !string.Equals(id, ArtifactRecordIngestionHostedPlugin.HandlerId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("HostedWorker:HandlerIds contains an unsupported Vyral hosted handler.");
        }
        if (!CallbackPath.StartsWith("/", StringComparison.Ordinal) || CallbackPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("HostedWorker:CallbackPath must be an absolute path.");
        }
        if (!double.IsFinite(LeaseTtlSeconds) || LeaseTtlSeconds <= 0 || LeaseTtlSeconds > 900)
        {
            throw new InvalidOperationException("HostedWorker:LeaseTtlSeconds must be greater than zero and no more than 900.");
        }
        if (HeartbeatSeconds <= 0 || HeartbeatSeconds >= LeaseTtlSeconds)
        {
            throw new InvalidOperationException("HostedWorker:HeartbeatSeconds must be positive and shorter than HostedWorker:LeaseTtlSeconds.");
        }
        TaskAuthentication.Validate();
    }

    private static IReadOnlyList<string> ReadValues(IConfigurationSection section, string name, string alternateName, string? environmentValue)
    {
        var values = section.GetSection(name).GetChildren().Select(item => item.Value)
            .Concat(section.GetSection(alternateName).GetChildren().Select(item => item.Value))
            .Append(section[name])
            .Append(section[alternateName])
            .Append(environmentValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .SelectMany(item => item!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        return values;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private static int ParsePositiveInt(string? value, int fallback) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    private static double ParsePositiveDouble(string? value, double fallback) => double.TryParse(value, out var parsed) && double.IsFinite(parsed) && parsed > 0 ? parsed : fallback;
}

public sealed class HostedWorkerTaskAuthenticationOptions
{
    public string Mode { get; init; } = "google-oidc";
    public IReadOnlySet<string> AllowedAudiences { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedPrincipals { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string DevelopmentIdentityHeader { get; init; } = "X-Vyral-Development-Identity";

    public static HostedWorkerTaskAuthenticationOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("HostedWorker:TaskAuthentication");
        return new HostedWorkerTaskAuthenticationOptions
        {
            Mode = section["Mode"] ?? section["mode"] ?? configuration["VYRAL_HOSTED_WORKER_TASK_AUTH_MODE"] ?? "google-oidc",
            AllowedAudiences = ReadSet(section, "AllowedAudiences", "allowedAudiences", configuration["VYRAL_HOSTED_WORKER_TASK_ALLOWED_AUDIENCES"]),
            AllowedPrincipals = ReadSet(section, "AllowedPrincipals", "allowedPrincipals", configuration["VYRAL_HOSTED_WORKER_TASK_ALLOWED_PRINCIPALS"]),
            DevelopmentIdentityHeader = section["DevelopmentIdentityHeader"] ?? section["developmentIdentityHeader"] ?? "X-Vyral-Development-Identity"
        };
    }

    public void Validate()
    {
        if (!string.Equals(Mode, "google-oidc", StringComparison.Ordinal) &&
            !string.Equals(Mode, "development-header", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("HostedWorker:TaskAuthentication:Mode must be google-oidc or development-header.");
        }
        if (AllowedPrincipals.Count == 0)
        {
            throw new InvalidOperationException("HostedWorker:TaskAuthentication:AllowedPrincipals requires at least one callback identity.");
        }
        if (string.Equals(Mode, "google-oidc", StringComparison.Ordinal) && AllowedAudiences.Count == 0)
        {
            throw new InvalidOperationException("HostedWorker:TaskAuthentication:AllowedAudiences is required for Google OIDC.");
        }
    }

    private static IReadOnlySet<string> ReadSet(IConfigurationSection section, string name, string alternateName, string? environmentValue) =>
        new HashSet<string>(
            section.GetSection(name).GetChildren().Select(item => item.Value)
                .Concat(section.GetSection(alternateName).GetChildren().Select(item => item.Value))
                .Append(section[name])
                .Append(section[alternateName])
                .Append(environmentValue)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .SelectMany(item => item!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.Ordinal);
}
