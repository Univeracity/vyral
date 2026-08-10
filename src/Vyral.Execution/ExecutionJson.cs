using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyral.Execution;

public static class ExecutionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
