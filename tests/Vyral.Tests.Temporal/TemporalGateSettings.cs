using System.Text.RegularExpressions;
using Temporalio.Client;

namespace Vyral.Tests.Temporal;

internal static class TemporalGateSettings
{
    private static readonly Regex ResourcePrefixPattern = new(
        "^[a-z][a-z0-9]{5,23}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool LiveGateEnabled => Enabled("VYRAL_TEMPORAL_LIVE_GATE");

    public static bool IntegrationGateEnabled =>
        Enabled("VYRAL_TEMPORAL_CONTAINER_GATE") || LiveGateEnabled;

    public static bool TemporalTlsRequired =>
        Boolean("VYRAL_EXECUTION_TEMPORAL_REQUIRE_TLS", LiveGateEnabled);

    public static bool PostgresTlsRequired =>
        Boolean("VYRAL_TEMPORAL_POSTGRES_REQUIRE_TLS", LiveGateEnabled);

    public static string? ResourcePrefix
    {
        get
        {
            var prefix = Optional("VYRAL_TEMPORAL_GATE_RESOURCE_PREFIX");
            if (prefix is not null && !ResourcePrefixPattern.IsMatch(prefix))
            {
                throw new InvalidOperationException(
                    "Temporal gate resource prefix must be 6-24 lowercase alphanumeric characters and start with a letter.");
            }
            return prefix;
        }
    }

    public static string SchemaName(string fallback, string purpose, string suffix) =>
        ResourcePrefix is { } prefix
            ? $"{prefix}_{purpose}_{suffix[..8]}"
            : fallback;

    public static string PortableName(string fallback, string purpose, string suffix) =>
        ResourcePrefix is { } prefix
            ? $"{prefix}-{purpose}-{suffix[..8]}"
            : fallback;

    public static TemporalClientConnectOptions ClientOptions(string targetHost, string temporalNamespace)
    {
        var apiKey = Optional("VYRAL_TEMPORAL_API_KEY");
        var rootCa = Optional("VYRAL_TEMPORAL_TLS_ROOT_CA_PATH");
        var clientCert = Optional("VYRAL_TEMPORAL_TLS_CLIENT_CERT_PATH");
        var clientKey = Optional("VYRAL_TEMPORAL_TLS_CLIENT_KEY_PATH");
        if ((clientCert is null) != (clientKey is null))
            throw new InvalidOperationException("Temporal gate mTLS certificate and key must be configured together.");
        if (!TemporalTlsRequired && (apiKey is not null || clientCert is not null || rootCa is not null))
            throw new InvalidOperationException("Temporal gate credentials and certificate settings require TLS.");

        var tls = TemporalTlsRequired
            ? new TlsOptions
            {
                ServerRootCACert = ReadPem(rootCa, "root CA"),
                Domain = Optional("VYRAL_TEMPORAL_TLS_DOMAIN"),
                ClientCert = ReadPem(clientCert, "client certificate"),
                ClientPrivateKey = ReadPem(clientKey, "client private key")
            }
            : new TlsOptions { Disabled = true };
        return new TemporalClientConnectOptions(targetHost)
        {
            Namespace = temporalNamespace,
            Tls = tls,
            ApiKey = apiKey
        };
    }

    private static byte[]? ReadPem(string? path, string description)
    {
        if (path is null) return null;
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length is < 1 or > 1_048_576)
            throw new InvalidOperationException($"Temporal gate {description} file is missing or invalid.");
        return File.ReadAllBytes(fullPath);
    }

    private static bool Enabled(string name) =>
        string.Equals(Optional(name), "1", StringComparison.Ordinal);

    private static bool Boolean(string name, bool fallback)
    {
        var value = Optional(name);
        if (value is null) return fallback;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Temporal gate setting '{name}' must be true or false.");
    }

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
