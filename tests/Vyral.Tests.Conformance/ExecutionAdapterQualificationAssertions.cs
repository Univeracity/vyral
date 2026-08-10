using System.Reflection;
using System.Text.Json;
using Vyral.Execution;

namespace Vyral.Tests.Conformance;

public static class ExecutionAdapterQualificationAssertions
{
    private const string ResourceName = "Vyral.Tests.Conformance.adapter-qualification.json";

    public static void AssertMatchesPublishedProfile(ExecutionRuntimeAdapterDescriptor actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        using var stream = typeof(ExecutionAdapterQualificationAssertions).Assembly
            .GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);
        var adapters = document.RootElement.GetProperty("adapters");
        var matches = adapters.EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("adapterId").GetString(), actual.AdapterId, StringComparison.Ordinal))
            .ToList();
        var published = Assert.Single(matches);

        Assert.Equal(actual.RuntimeKind, published.GetProperty("runtimeKind").GetString());
        Assert.Equal(actual.DisplayName, published.GetProperty("displayName").GetString());
        Assert.Equal(actual.Version, published.GetProperty("adapterVersion").GetString());
        var advertised = published.GetProperty("advertisedCapabilities").EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Equal(actual.Capabilities.Cast<string?>().ToList(), advertised);

        var qualified = published.GetProperty("qualification").GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Equal(advertised, qualified);
    }
}
