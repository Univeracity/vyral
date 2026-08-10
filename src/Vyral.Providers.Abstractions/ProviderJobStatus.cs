using System.Text.Json.Serialization;

namespace Vyral.Providers.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderJobStatus>))]
public enum ProviderJobStatus
{
    Succeeded = 0,
    Failed = 1,
    TimedOut = 2,
    Rejected = 3,
    Unsupported = 4,
    NotConfigured = 5,
    Cancelled = 6,
    Queued = 7,
    Running = 8
}
