using Vyral.Providers.Abstractions;
using Vyral.Providers.Local;

namespace Vyral.Tests.Providers;

public class NativeDeadlineTests
{
    private static LlamaCppRuntimeOptions Options() => new() {
        Endpoint = new("http://127.0.0.1:1/"), ModelId = "no-launch-fixture", ModelPath = "/nonexistent/no-launch-model",
        ModelSha256 = new('a', 64), BuildInfo = "fixture", TemplateSha256 = new('b', 64),
        TemplateProbeSha256 = new('c', 64), ContextTokens = 4096, MaxOutputTokens = 512 };

    [Theory]
    [InlineData(120)] [InlineData(270)] [InlineData(600)]
    [InlineData(1500)] [InlineData(1800)] [InlineData(3600)]
    public void ConfiguredCeilingIsPublicAndDoesNotChangeNativeFactoryBinding(int maximum)
    {
        var options = Options(); var original = new LocalLlamaCppProviderTarget(options);
        var target = new LocalLlamaCppProviderTarget(options, maximum);
        Assert.Equal(120, original.MaxExecutionSeconds);
        Assert.Equal(maximum, target.MaxExecutionSeconds);
        Assert.Equal(maximum, Assert.Single(Assert.Single(target.Capabilities).ModePolicies).TimeoutSeconds);
        Assert.Equal(original.Profile.ConfigHash, target.Profile.ConfigHash);
        Assert.Equal(new LlamaCppRuntimeSessionFactory(options).BindingId, target.Profile.ConfigHash);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(3601)]
    public void InvalidCeilingRefusesDuringConstruction(int maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalLlamaCppProviderTarget(Options(), maximum));

    [Theory]
    [InlineData(120, 121)] [InlineData(270, 271)] [InlineData(600, 601)]
    [InlineData(1500,1501)] [InlineData(1800,1801)] [InlineData(3600,3601)]
    public async Task AboveConfiguredCeilingRefusesBeforeRuntime(int maximum, int requested)
    {
        var options = Options();
        var request = ProviderRunRequests.ForChat(new() { Messages = new() { new() { Role = AiRoles.User, Content = "fixture" } },
            MaxOutputTokens = 2 }, modelId: options.ModelId, timeoutSeconds: requested);
        var result = await new LocalLlamaCppProviderTarget(options, maximum).RunAsync(request);
        Assert.Equal(ProviderRunStatus.Rejected, result.Status);
        Assert.Equal("timeout_exceeds_policy", result.ProviderStatus);
        Assert.Empty(result.MeteringMeasurements);
        Assert.Contains("were not dispatched", result.Rejection!.Message);
    }

    [Theory]
    [InlineData(270)] [InlineData(600)]
    [InlineData(1500)] [InlineData(1800)] [InlineData(3600)]
    public async Task SupportedLongRequestStillObeysAlreadyCancelledOwner(int maximum)
    {
        var options = Options(); using var owner = new CancellationTokenSource(); owner.Cancel();
        var request = ProviderRunRequests.ForChat(new() { Messages = new() { new() { Role = AiRoles.User, Content = "fixture" } },
            MaxOutputTokens = 2 }, modelId: options.ModelId, timeoutSeconds: maximum);
        var result = await new LocalLlamaCppProviderTarget(options, maximum).RunAsync(request, owner.Token);
        Assert.Equal(ProviderRunStatus.Cancelled, result.Status);
        Assert.Equal("no_native_completion_started", result.ProviderStatus);
        Assert.Empty(result.MeteringMeasurements);
    }

    [Theory]
    [InlineData(270, 250)] [InlineData(600, 599)] [InlineData(120, 1)]
    [InlineData(1800,1750)] [InlineData(3600,3599)]
    public void ControllerReturnsOnlyOriginalUtcRemainder(int maximum, int remaining)
    {
        var now = DateTimeOffset.Parse("2026-10-10T10:00:00Z"); var original = now.AddSeconds(remaining);
        Assert.Equal(TimeSpan.FromSeconds(remaining), OwnedNativeDeadline.Remaining(original, maximum, now));
        Assert.Equal(TimeSpan.FromSeconds(remaining - .5), OwnedNativeDeadline.Remaining(original, maximum, now.AddMilliseconds(500)));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(3601, 1)] [InlineData(270, 271)] [InlineData(270, 0)] [InlineData(270, -1)]
    [InlineData(1800,1801)] [InlineData(3600,3601)]
    public void ControllerRefusesInvalidExcessOrExpiredAllowance(int maximum, int remaining)
    {
        var now = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
        Assert.Throws<ArgumentException>(() => OwnedNativeDeadline.Remaining(now.AddSeconds(remaining), maximum, now));
    }

    [Fact]
    public void ControllerRefusesNonUtcDeadline()
    {
        var now = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
        Assert.Throws<ArgumentException>(() => OwnedNativeDeadline.Remaining(now.AddSeconds(1).ToOffset(TimeSpan.FromHours(1)), 270, now));
    }
}
