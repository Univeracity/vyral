using System.Diagnostics;
using System.Text;
using Vyral.Providers.Cli;

namespace Vyral.Tests.Providers;

public class ProviderProcessRunnerTests
{
    [Fact]
    public async Task SystemRunner_ParentDeathChildSurvivesCallingThreadExitUntilRunCompletes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
            throw Xunit.Sdk.SkipException.ForSkip("Parent-thread regression requires Linux and Python3.");
        var marker = Path.Combine(Path.GetTempPath(), "vyral-parent-thread-" + Guid.NewGuid().ToString("N"));
        Task<ProviderProcessRunResult>? run = null; Exception? launchError = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var launcher = new Thread(() =>
        {
            try
            {
                run = new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest {
                    Command = "/usr/bin/python3", Arguments = new[] { "-c",
                        "import ctypes,pathlib,time,sys; assert ctypes.CDLL(None).prctl(1,9,0,0,0)==0; " +
                        "pathlib.Path(sys.argv[1]).write_text('ready'); time.sleep(0.3); print('completed')", marker },
                    Timeout = TimeSpan.FromSeconds(3) }, cancellation.Token);
                // Let the child install its parent-thread death signal before
                // this caller thread exits; the owner process remains alive.
                while (!File.Exists(marker) && !run.IsCompleted && !cancellation.IsCancellationRequested) Thread.Sleep(5);
            }
            catch (Exception e) { launchError = e; }
        });
        try
        {
            launcher.Start(); Assert.True(launcher.Join(TimeSpan.FromSeconds(2)));
            Assert.Null(launchError); Assert.NotNull(run);
            var result = await run!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("completed", result.StandardOutput.Trim());
            Assert.False(result.Cancelled); Assert.False(result.TimedOut);
        }
        finally { cancellation.Cancel(); if (launcher.IsAlive) launcher.Join(TimeSpan.FromSeconds(1)); File.Delete(marker); }
    }

    [Fact]
    public async Task SystemRunner_PreCancelledRequestDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
        {
            Command = "missing-command-must-not-be-started",
            Timeout = TimeSpan.FromSeconds(1)
        }, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Null(result.StartError);
    }

    [Fact]
    public async Task SystemRunner_CancelsDescendantsWhileTheParentIsRunning()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
            throw Xunit.Sdk.SkipException.ForSkip("This descendant regression requires /bin/sh on Linux.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
        {
            Command = "/bin/sh",
            Arguments = new[] { "-c", "sleep 30 & echo $!; wait" },
            Timeout = TimeSpan.FromSeconds(5)
        }, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(int.TryParse(result.StandardOutput.Trim(), out var pid));
        try
        {
            using var child = Process.GetProcessById(pid);
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await child.WaitForExitAsync(safety.Token);
        }
        catch (ArgumentException) { } // Already reaped.
    }

    [Fact]
    public async Task SystemRunner_BoundsPipesInheritedAfterParentExit()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
            throw Xunit.Sdk.SkipException.ForSkip("This pipe regression requires /bin/sh on Linux.");
        int pid = 0;
        try
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
            {
                Command = "/bin/sh",
                Arguments = new[] { "-c", "sleep 10 & echo $!; exit 0" },
                Timeout = TimeSpan.FromMilliseconds(300)
            }, safety.Token);
            int.TryParse(result.StandardOutput.Trim(), out pid);
            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Contains("unresolved", result.StartError);
        }
        finally
        {
            if (pid != 0)
            {
                try { using var child = Process.GetProcessById(pid); child.Kill(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    [Fact]
    public async Task SystemRunner_TimeoutIncludesBlockedStandardInput()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
            throw Xunit.Sdk.SkipException.ForSkip("This pipe regression requires /bin/sh on Linux.");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await new SystemProviderProcessRunner().RunAsync(new ProviderProcessRunRequest
        {
            Command = "/bin/sh",
            Arguments = new[] { "-c", "sleep 10" },
            StandardInput = new string('x', 1024 * 1024),
            Timeout = TimeSpan.FromMilliseconds(100),
            MaxOutputBytes = 1024
        }, safety.Token);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
    }

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
