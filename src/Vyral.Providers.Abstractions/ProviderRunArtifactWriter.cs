using System.Text.Json;

namespace Vyral.Providers.Abstractions;

public static class ProviderRunArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static IReadOnlyList<string> WriteArtifacts(
        string? artifactDirectory,
        ProviderRunRequest request,
        string prompt,
        string rawOutput,
        ProviderRunResult result)
    {
        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            return Array.Empty<string>();
        }

        var traceEvent = result.Trace ??= new ProviderTraceEvent
        {
            Provider = result.Provider,
            Capability = result.Capability,
            Operation = result.Operation,
            Mode = result.Mode,
            FailureClass = result.FailureClass
        };
        if (string.IsNullOrWhiteSpace(traceEvent.TraceId))
        {
            traceEvent.TraceId = Guid.NewGuid().ToString("N");
        }

        var runDirectory = Path.Combine(artifactDirectory, traceEvent.TraceId);
        Directory.CreateDirectory(runDirectory);

        var files = new List<string>();
        Write(Path.Combine(runDirectory, "request.json"), JsonSerializer.Serialize(request, JsonOptions), files);
        Write(Path.Combine(runDirectory, "prompt.txt"), prompt, files);
        Write(Path.Combine(runDirectory, "output.txt"), rawOutput, files);
        traceEvent.ArtifactRefs = files.ToList();
        Write(Path.Combine(runDirectory, "result.json"), JsonSerializer.Serialize(result, JsonOptions), files);
        traceEvent.ArtifactRefs = files.ToList();
        return files;
    }

    private static void Write(string path, string content, List<string> files)
    {
        File.WriteAllText(path, content);
        files.Add(path);
    }
}
