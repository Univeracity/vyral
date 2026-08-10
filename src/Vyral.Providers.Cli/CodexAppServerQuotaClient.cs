using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Providers.Cli;

public sealed class CodexAppServerQuotaRequest
{
    public Uri? WebSocketUri { get; set; }
    public bool AutoStartWebSocket { get; set; }
    public string Command { get; set; } = "codex";
    public IReadOnlyList<string> LaunchArguments { get; set; } = Array.Empty<string>();
    public string? WorkingDirectory { get; set; }
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxOutputBytes { get; set; } = 64 * 1024;
}

public interface ICodexAppServerQuotaClient
{
    Task<ProviderProcessRunResult> ReadRateLimitsAsync(CodexAppServerQuotaRequest request, CancellationToken ct = default);
}

internal sealed record ListeningUriReadResult(Uri? Uri, string Text);

public sealed class CodexAppServerQuotaClient : ICodexAppServerQuotaClient
{
    public async Task<ProviderProcessRunResult> ReadRateLimitsAsync(CodexAppServerQuotaRequest request, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(request.Timeout);
        try
        {
            if (request.WebSocketUri is not null)
            {
                return await ReadFromWebSocketAsync(request.WebSocketUri, request.MaxOutputBytes, timeoutCts.Token);
            }

            if (!request.AutoStartWebSocket)
            {
                return new ProviderProcessRunResult
                {
                    ExitCode = -1,
                    StartError = "Codex app-server WebSocket URI is not configured.",
                    StandardError = "Codex app-server WebSocket URI is not configured."
                };
            }

            return await StartAppServerAndReadAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ProviderProcessRunResult { ExitCode = -1, Cancelled = true, StandardError = "cancelled" };
        }
        catch (OperationCanceledException)
        {
            return new ProviderProcessRunResult { ExitCode = -1, TimedOut = true, StandardError = "timeout" };
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
        {
            return new ProviderProcessRunResult
            {
                ExitCode = -1,
                StartError = ex.Message,
                StandardError = ex.Message
            };
        }
    }

    private static async Task<ProviderProcessRunResult> StartAppServerAndReadAsync(CodexAppServerQuotaRequest request, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        var launchArguments = request.LaunchArguments.Count == 0
            ? new[] { "app-server", "--listen", "ws://127.0.0.1:0" }
            : request.LaunchArguments;
        foreach (var argument in launchArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new ProviderProcessRunResult { ExitCode = -1, StartError = "Codex app-server failed to start." };
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProviderProcessRunResult
            {
                ExitCode = -1,
                StartError = ex.Message,
                StandardError = ex.Message
            };
        }

        try
        {
            var uri = await ReadListeningUriAsync(process, ct);
            var result = await ReadFromWebSocketAsync(uri, request.MaxOutputBytes, ct);
            return result;
        }
        finally
        {
            KillProcessTree(process);
            await WaitForExitQuietlyAsync(process);
        }
    }

    private static async Task<Uri> ReadListeningUriAsync(Process process, CancellationToken ct)
    {
        var stdoutTask = ReadListeningUriFromReaderAsync(process.StandardOutput, ct);
        var stderrTask = ReadListeningUriFromReaderAsync(process.StandardError, ct);
        var exitTask = process.WaitForExitAsync(ct);
        var pending = new List<Task> { stdoutTask, stderrTask, exitTask };

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            if (completed == stdoutTask)
            {
                var result = await stdoutTask;
                if (result.Uri is not null)
                {
                    return result.Uri;
                }

                pending.Remove(stdoutTask);
                continue;
            }

            if (completed == stderrTask)
            {
                var result = await stderrTask;
                if (result.Uri is not null)
                {
                    return result.Uri;
                }

                pending.Remove(stderrTask);
                continue;
            }

            pending.Remove(exitTask);
            await exitTask;
        }

        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result.Text : string.Empty;
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Text : string.Empty;
        var message = string.Join('\n', new[] { stdout, stderr }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? "Codex app-server did not report a WebSocket listening URI."
            : message);
    }

    private static async Task<ListeningUriReadResult> ReadListeningUriFromReaderAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return new ListeningUriReadResult(null, text.ToString());
            }

            text.AppendLine(line);
            var uri = TryExtractWebSocketUri(line);
            if (uri is not null)
            {
                return new ListeningUriReadResult(uri, text.ToString());
            }
        }
    }

    private static Uri? TryExtractWebSocketUri(string line)
    {
        var markerIndex = line.IndexOf("ws://", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            markerIndex = line.IndexOf("wss://", StringComparison.OrdinalIgnoreCase);
        }

        if (markerIndex < 0)
        {
            return null;
        }

        var uriText = line[markerIndex..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.TrimEnd('.', ',', ';');
        return Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static async Task<ProviderProcessRunResult> ReadFromWebSocketAsync(Uri uri, int maxOutputBytes, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, ct);
        await SendJsonAsync(socket, new JsonObject
        {
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "vyral-codex-quota",
                    ["version"] = "0.1"
                },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = true
                }
            }
        }, ct);
        await SendJsonAsync(socket, new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, ct);
        await SendJsonAsync(socket, new JsonObject { ["id"] = 1, ["method"] = "account/rateLimits/read" }, ct);

        var transcript = new StringBuilder();
        while (socket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextMessageAsync(socket, ct);
            if (message is null)
            {
                break;
            }

            transcript.AppendLine(message);
            if (Encoding.UTF8.GetByteCount(transcript.ToString()) > maxOutputBytes)
            {
                return new ProviderProcessRunResult
                {
                    ExitCode = 0,
                    StandardOutput = transcript.ToString(),
                    OutputTruncated = true
                };
            }

            if (IsRateLimitResponse(message))
            {
                return new ProviderProcessRunResult
                {
                    ExitCode = 0,
                    StandardOutput = transcript.ToString()
                };
            }
        }

        return new ProviderProcessRunResult
        {
            ExitCode = -1,
            StandardOutput = transcript.ToString(),
            StandardError = "Codex app-server WebSocket closed before returning rate limits."
        };
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, JsonObject payload, CancellationToken ct)
    {
        var json = payload.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<string?> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static bool IsRateLimitResponse(string text)
    {
        try
        {
            var json = JsonNode.Parse(text) as JsonObject;
            return json?["id"]?.GetValue<int>() == 1;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
