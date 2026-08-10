using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Abstractions;

public static class ProviderJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static T DeserializePayload<T>(ProviderRunRequest request)
    {
        return request.Payload.Deserialize<T>(Options)
            ?? throw new ArgumentException($"Provider payload could not be deserialized as {typeof(T).Name}.");
    }

    public static JsonObject ToJsonObject<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options) as JsonObject ?? new JsonObject();
    }
}
