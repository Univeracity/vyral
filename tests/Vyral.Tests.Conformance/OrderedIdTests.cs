using Vyral.Primitives;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Conformance;

public sealed class OrderedIdTests
{
    [Fact]
    public void OrderedId_CreatesMonotonicSortableStrings()
    {
        var ids = Enumerable.Range(0, 64)
            .Select(_ => OrderedId.CreateString())
            .ToList();

        Assert.All(ids, id =>
        {
            Assert.Equal(OrderedId.SortableStringLength, id.Length);
            Assert.True(OrderedId.TryParse(id, out _));
        });
        Assert.Equal(ids, ids.OrderBy(id => id, StringComparer.Ordinal).ToList());
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OrderedId_DecomposesAndBuildsTimestampReferences()
    {
        var timestamp = new DateTimeOffset(2026, 06, 24, 06, 00, 00, TimeSpan.Zero);
        var before = OrderedId.Reference(timestamp, OrderedIdReferenceDirection.Before);
        var after = OrderedId.Reference(timestamp, OrderedIdReferenceDirection.After);

        Assert.True(before.CompareTo(after) < 0);
        Assert.Equal(timestamp.ToUnixTimeSeconds(), before.Decompose().TimestampUtc.ToUnixTimeSeconds());
        Assert.Equal(timestamp.ToUnixTimeSeconds(), after.Decompose().TimestampUtc.ToUnixTimeSeconds());
        Assert.True(OrderedId.Parse(after.ToString()).CompareTo(before) > 0);
    }

    [Fact]
    public void OrderedId_CreatesJitteredAndNonThrowingIds()
    {
        var jittered = OrderedId.CreateJitteredString(maxJitterMicroseconds: 1);

        Assert.Equal(OrderedId.SortableStringLength, jittered.Length);
        Assert.True(OrderedId.TryParse(jittered, out _));
        Assert.True(OrderedId.TryCreate(out var created));
        Assert.True(created.Value > 0);
        Assert.True(OrderedId.TryCreateString(out var createdString));
        Assert.True(OrderedId.TryParse(createdString, out _));
        Assert.True(OrderedId.CreateStringOrFallback().Length > 0);
    }

    [Fact]
    public void TraceRecord_DefaultIdUsesOrderedId()
    {
        var trace = new TraceRecord();

        Assert.True(OrderedId.TryParse(trace.Id, out _));
    }
}
