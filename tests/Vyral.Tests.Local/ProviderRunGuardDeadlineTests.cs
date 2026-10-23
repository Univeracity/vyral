using Microsoft.Extensions.Configuration;
using Vyral.Providers.Abstractions;
using Vyral.Server;

namespace Vyral.Tests.Local;

public class ProviderRunGuardDeadlineTests
{
    [Fact]
    public void ExistingDefaultsAndConfiguredOperationalMetadataRemainDistinct()
    {
        var original = ProviderRunGuardOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(120, original.DefaultTimeoutSeconds); Assert.Equal(300, original.MaxTimeoutSeconds);
        var configured = ProviderRunGuardOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Providers:MaxRunTimeoutSeconds"] = "600" }).Build());
        Assert.Equal(120, configured.DefaultTimeoutSeconds); Assert.Equal(600, configured.MaxTimeoutSeconds);
        Assert.Equal(600, new ProviderRunGuard(configured).ToOperationalLimits(true)["maxTimeoutSeconds"]);
    }

    [Theory]
    [InlineData(270, true)] [InlineData(600, true)] [InlineData(601, false)]
    public async Task CommonJobGuardAdmitsOnlyConfiguredLongAllowance(int timeout, bool accepted)
    {
        var guard = new ProviderRunGuard(new() { MaxTimeoutSeconds = 600 });
        var request = new ProviderRunRequest { Capability = ProviderCapabilityIds.AiChat, Operation = "run", Mode = ProviderModes.Advisory,
            TimeoutSeconds = timeout };
        await using var admission = await guard.TryEnterAsync("no-launch-fixture", request, CancellationToken.None);
        Assert.Equal(accepted, admission.Accepted);
        if (!accepted) Assert.Equal("timeout_exceeds_policy", admission.RejectionResult!.ProviderStatus);
    }

    [Fact]
    public async Task ShorterRequestTimerAndOwnerCancellationAttenuateLongCeiling()
    {
        var guard = new ProviderRunGuard(new() { MaxTimeoutSeconds = 600 });
        var request = new ProviderRunRequest { Capability = ProviderCapabilityIds.AiChat, Operation = "run", Mode = ProviderModes.Advisory,
            TimeoutSeconds = 1 };
        await using var timed = await guard.TryEnterAsync("timer-fixture", request, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.Infinite, timed.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3)));
        using var owner = new CancellationTokenSource(); request.TimeoutSeconds = 270;
        await using var inherited = await guard.TryEnterAsync("owner-fixture", request, owner.Token);
        owner.Cancel(); Assert.True(inherited.CancellationToken.IsCancellationRequested);
        Assert.Equal(270, request.TimeoutSeconds);
    }

    [Theory]
    [InlineData(1800)] [InlineData(3600)]
    public async Task ExplicitTaskSizedHostConfigurationPreservesDefaultAndSelectedCeiling(int maximum)
    {
        var configured = ProviderRunGuardOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["Providers:MaxRunTimeoutSeconds"] = maximum.ToString() }).Build());
        var guard = new ProviderRunGuard(configured);
        Assert.Equal(120,configured.DefaultTimeoutSeconds);
        Assert.Equal(maximum,guard.ToOperationalLimits(true)["maxTimeoutSeconds"]);
        using var owner = new CancellationTokenSource();
        var request = new ProviderRunRequest { Capability=ProviderCapabilityIds.AiChat,Operation="run",Mode=ProviderModes.Advisory,
            TimeoutSeconds=maximum };
        await using var admitted = await guard.TryEnterAsync("no-launch-task-sized",request,owner.Token);
        Assert.True(admitted.Accepted);owner.Cancel();Assert.True(admitted.CancellationToken.IsCancellationRequested);
        request.TimeoutSeconds=maximum+1;
        await using var held = await guard.TryEnterAsync("no-launch-task-sized",request,CancellationToken.None);
        Assert.False(held.Accepted);Assert.Equal("timeout_exceeds_policy",held.RejectionResult!.ProviderStatus);
    }
}
