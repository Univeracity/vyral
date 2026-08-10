using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Vyral.Providers.Cli;

public sealed class ProviderProcessRunRequest
{
    public string Command { get; set; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();
    public string? WorkingDirectory { get; set; }
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
    public bool ClearEnvironment { get; set; }
    public string? StandardInput { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
    public int MaxOutputBytes { get; set; } = 128 * 1024;
}

public sealed class ProviderProcessRunResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    /// <summary>Number of stdout bytes retained in <see cref="StandardOutput"/>.</summary>
    public int StandardOutputBytes { get; set; }
    /// <summary>Number of stderr bytes retained in <see cref="StandardError"/>.</summary>
    public int StandardErrorBytes { get; set; }
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public bool OutputTruncated { get; set; }
    public string? StartError { get; set; }
}

public interface IProviderProcessRunner
{
    Task<ProviderProcessRunResult> RunAsync(ProviderProcessRunRequest request, CancellationToken ct = default);
}

public sealed class SystemProviderProcessRunner : IProviderProcessRunner
{
    public async Task<ProviderProcessRunResult> RunAsync(ProviderProcessRunRequest request, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.ClearEnvironment)
        {
            startInfo.Environment.Clear();
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
                return new ProviderProcessRunResult { ExitCode = -1, StartError = "Process failed to start." };
            }
        }
        catch (Win32Exception ex)
        {
            return new ProviderProcessRunResult
            {
                ExitCode = -1,
                StartError = ex.Message,
                StandardError = ex.Message
            };
        }

        var stdoutTask = ReadWithLimitAsync(process.StandardOutput, request.MaxOutputBytes);
        var stderrTask = ReadWithLimitAsync(process.StandardError, request.MaxOutputBytes);

        var timedOut = false;
        var cancelled = false;
        try
        {
            if (request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), ct);
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            KillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            KillProcessTree(process);
        }

        if (timedOut || cancelled)
        {
            await WaitForExitQuietlyAsync(process);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ProviderProcessRunResult
        {
            ExitCode = timedOut || cancelled ? -1 : process.ExitCode,
            StandardOutput = stdout.Text,
            StandardError = stderr.Text,
            StandardOutputBytes = stdout.CapturedBytes,
            StandardErrorBytes = stderr.CapturedBytes,
            TimedOut = timedOut,
            Cancelled = cancelled,
            OutputTruncated = stdout.Truncated || stderr.Truncated
        };
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

    /// <summary>
    /// Drains a process pipe while retaining only its bounded UTF-8 prefix.
    /// Incremental character accounting avoids repeatedly re-encoding the
    /// complete transcript; continuing to drain after the limit prevents the
    /// child process from blocking on a full pipe.
    /// </summary>
    private static async Task<BoundedProcessOutput> ReadWithLimitAsync(StreamReader reader, int maxBytes)
    {
        var capture = new BoundedUtf8Capture(Math.Max(0, maxBytes));
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }

            capture.Append(buffer.AsSpan(0, read));
        }

        return capture.Complete();
    }

    /// <summary>Tracks the UTF-8 size of retained text without re-encoding the accumulated buffer.</summary>
    private sealed class BoundedUtf8Capture
    {
        private readonly int _limit;
        private readonly StringBuilder _text = new();
        private char? _pendingHighSurrogate;
        private int _retainedBytes;
        private bool _truncated;

        public BoundedUtf8Capture(int limit)
        {
            _limit = limit;
        }

        public void Append(ReadOnlySpan<char> characters)
        {
            foreach (var character in characters)
            {
                if (_pendingHighSurrogate is { } highSurrogate)
                {
                    if (char.IsLowSurrogate(character))
                    {
                        AppendScalar(highSurrogate, character, 4);
                        _pendingHighSurrogate = null;
                        continue;
                    }

                    // Match Encoding.UTF8's replacement behavior for an
                    // unmatched UTF-16 surrogate before considering the next
                    // scalar value.
                    AppendScalar(highSurrogate, null, 3);
                    _pendingHighSurrogate = null;
                }

                if (char.IsHighSurrogate(character))
                {
                    _pendingHighSurrogate = character;
                }
                else
                {
                    AppendScalar(character, null, GetUtf8ByteCount(character));
                }
            }
        }

        public BoundedProcessOutput Complete()
        {
            if (_pendingHighSurrogate is { } highSurrogate)
            {
                AppendScalar(highSurrogate, null, 3);
                _pendingHighSurrogate = null;
            }

            return new BoundedProcessOutput(_text.ToString(), _retainedBytes, _truncated);
        }

        private void AppendScalar(char first, char? second, int utf8Bytes)
        {
            if (_retainedBytes > _limit - utf8Bytes)
            {
                _truncated = true;
                return;
            }

            _text.Append(first);
            if (second is { } secondCharacter)
            {
                _text.Append(secondCharacter);
            }

            _retainedBytes += utf8Bytes;
        }

        private static int GetUtf8ByteCount(char character) => character switch
        {
            <= '\u007f' => 1,
            <= '\u07ff' => 2,
            _ => 3
        };
    }

    private sealed record BoundedProcessOutput(string Text, int CapturedBytes, bool Truncated);
}
