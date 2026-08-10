using System.Text;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public class ProviderProcessRunnerTests
{
    [Fact]
    public async Task SystemRunner_BoundsAndDrainsMultiMegabyteOutput()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            throw Xunit.Sdk.SkipException.ForSkip("This process-output regression test requires /bin/sh on Linux.");
        }

        const int outputLimit = 128 * 1024;
        var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
        {
            Command = "/bin/sh",
            Arguments = new[]
            {
                "-c",
                "yes stdout | head -c 8388608; yes stderr | head -c 8388608 >&2"
            },
            Timeout = TimeSpan.FromSeconds(30),
            MaxOutputBytes = outputLimit
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(result.OutputTruncated);
        Assert.Equal(outputLimit, result.StandardOutputBytes);
        Assert.Equal(outputLimit, result.StandardErrorBytes);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardOutput), 0, outputLimit);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardError), 0, outputLimit);
    }

    [Fact]
    public async Task SystemRunner_DropsAnIncompleteUtf8SuffixAtTheOutputLimit()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            throw Xunit.Sdk.SkipException.ForSkip("This process-output regression test requires /bin/sh on Linux.");
        }

        var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
        {
            Command = "/bin/sh",
            Arguments = new[] { "-c", "printf '€€'" },
            MaxOutputBytes = 5
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.Equal("€", result.StandardOutput);
        Assert.Equal(3, result.StandardOutputBytes);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardOutput), 0, 5);
    }
}
