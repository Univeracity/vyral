using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Tests.Local;

public class CollectionExportTests
{
    [Fact]
    public async Task ExportCollectionAsync_DefaultsToSnapshotRecordLimit()
    {
        var store = new PagedExportStore(CollectionSnapshotLimits.MaxRecords + 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExportCollectionAsync("records"));
        var partial = await store.ExportCollectionAsync("records", new CollectionExportRequest
        {
            FailOnLimitExceeded = false
        });

        Assert.Contains($"maxRecords ({CollectionSnapshotLimits.MaxRecords})", exception.Message);
        Assert.NotNull(partial);
        Assert.Equal(CollectionSnapshotLimits.MaxRecords, partial!.MaxRecords);
        Assert.Equal(CollectionSnapshotLimits.MaxRecords, partial.RecordCount);
        Assert.True(partial.Truncated);
        Assert.NotNull(partial.ContinuationToken);
    }

    private sealed class PagedExportStore : IRecordCollectionStore
    {
        private readonly int _recordCount;

        public PagedExportStore(int recordCount)
        {
            _recordCount = recordCount;
        }

        public Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<string>>(new[] { "records" });

        public Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default) =>
            Task.FromResult<RecordCollectionPolicy?>(new RecordCollectionPolicy { Name = collection });

        public Task DeleteCollectionAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
        {
            var offset = string.IsNullOrWhiteSpace(query.ContinuationToken)
                ? 0
                : int.Parse(query.ContinuationToken, System.Globalization.CultureInfo.InvariantCulture);
            var limit = query.Limit ?? _recordCount;
            var items = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, _recordCount - offset)))
                .Select(index => new VyralRecord
                {
                    Id = $"record-{index:D5}",
                    PartitionKey = "tenant"
                })
                .ToList();
            var next = offset + items.Count < _recordCount
                ? (offset + items.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

            return Task.FromResult(new RecordQueryResult
            {
                Items = items,
                ContinuationToken = next
            });
        }

        public Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
