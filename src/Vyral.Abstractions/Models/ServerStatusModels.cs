using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Vyral.Abstractions.Models;

public class ServerHealthStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = ServerReadinessStatuses.Ok;

    [JsonPropertyName("service")]
    public string Service { get; set; } = "vyral-server";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.3.0";

    [JsonPropertyName("contractPath")]
    public string ContractPath { get; set; } = "/openapi/vyral.json";

    [JsonPropertyName("schemaContractPath")]
    public string SchemaContractPath { get; set; } = "/contracts/schemas/vyral-public.schema.json";

    [JsonPropertyName("storage")]
    public ServerStorageStatus Storage { get; set; } = new();

    [JsonPropertyName("embedding")]
    public ServerEmbeddingStatus Embedding { get; set; } = new();

    [JsonPropertyName("security")]
    public ServerSecurityStatus Security { get; set; } = new();

    [JsonPropertyName("checkedAt")]
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public class ServerStorageStatus
{
    [JsonPropertyName("recordStore")]
    public string RecordStore { get; set; } = string.Empty;

    [JsonPropertyName("objectStore")]
    public string ObjectStore { get; set; } = string.Empty;

    [JsonPropertyName("traceStore")]
    public string TraceStore { get; set; } = string.Empty;

    [JsonPropertyName("canonicalStore")]
    public string CanonicalStore { get; set; } = string.Empty;
}

public class ServerEmbeddingStatus
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("runtime")]
    public Dictionary<string, object?>? Runtime { get; set; }
}

public class ServerSecurityStatus
{
    [JsonPropertyName("apiKeyRequired")]
    public bool ApiKeyRequired { get; set; }

    [JsonPropertyName("apiKeyHeader")]
    public string ApiKeyHeader { get; set; } = "X-Vyral-Api-Key";

    [JsonPropertyName("providerRunLimits")]
    public Dictionary<string, object?> ProviderRunLimits { get; set; } = new();
}

public class ServerReadinessReport
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = ServerReadinessStatuses.Unknown;

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("checkedAt")]
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("health")]
    public ServerHealthStatus Health { get; set; } = new();

    [JsonPropertyName("checks")]
    public List<ServerReadinessCheck> Checks { get; set; } = new();

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("embedding")]
    public Dictionary<string, object?> Embedding { get; set; } = new();

    [JsonPropertyName("providers")]
    public ServerProviderReadinessSummary Providers { get; set; } = new();

    [JsonPropertyName("operationalLimits")]
    public Dictionary<string, object?> OperationalLimits { get; set; } = new();
}

public class ServerReadinessCheck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = ServerReadinessStatuses.Unknown;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new();
}

public class ServerProviderReadinessSummary
{
    [JsonPropertyName("providerCount")]
    public int ProviderCount { get; set; }

    [JsonPropertyName("capabilityCount")]
    public int CapabilityCount { get; set; }

    [JsonPropertyName("callableCapabilityCount")]
    public int CallableCapabilityCount { get; set; }

    [JsonPropertyName("readyCapabilityCount")]
    public int ReadyCapabilityCount { get; set; }

    [JsonPropertyName("unvalidatedCapabilityCount")]
    public int UnvalidatedCapabilityCount { get; set; }

    [JsonPropertyName("networkProviderCount")]
    public int NetworkProviderCount { get; set; }

    [JsonPropertyName("authProviderCount")]
    public int AuthProviderCount { get; set; }
}

public static class ServerReadinessStatuses
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}
