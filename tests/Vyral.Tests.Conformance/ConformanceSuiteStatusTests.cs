using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public class ConformanceSuiteStatusTests
{
    [Fact]
    public void ConformanceSuite_ProvidesReusableAdapterContracts()
    {
        Assert.True(typeof(RecordCollectionStoreConformanceTests).IsAbstract);
        Assert.True(typeof(ObjectStoreConformanceTests).IsAbstract);
        Assert.True(typeof(CanonicalStoreConformanceTests).IsAbstract);
        Assert.True(typeof(ExecutionRuntimeConformanceTests).IsAbstract);
    }

    [Fact]
    public void PublicContractConstants_MatchWireVocabulary()
    {
        Assert.Equal("startsWith", FilterOps.StartsWith);
        Assert.Equal(FilterOps.StartsWith, FilterValueNormalizer.NormalizeOperator("startsWith"));
        Assert.Equal(FilterOps.StartsWith, FilterValueNormalizer.NormalizeOperator("startswith"));
        Assert.Equal("dotProduct", DistanceFunctions.DotProduct);
        Assert.Equal("dotProduct", DistanceFunctions.Dot);
        Assert.Equal("quantizedFlat", IndexTypes.QuantizedFlat);
        Assert.Equal("diskANN", IndexTypes.DiskAnn);
    }

    [Fact]
    public void FilterValueNormalizer_NormalizesExistsContract()
    {
        Assert.True(FilterValueNormalizer.NormalizeExistsValue(null));
        Assert.True(FilterValueNormalizer.NormalizeExistsValue(true));
        Assert.False(FilterValueNormalizer.NormalizeExistsValue(false));
        Assert.Throws<NotSupportedException>(() => FilterValueNormalizer.NormalizeExistsValue("true"));
        Assert.Throws<NotSupportedException>(() => FilterValueNormalizer.NormalizeExistsValue(1));
    }
}
