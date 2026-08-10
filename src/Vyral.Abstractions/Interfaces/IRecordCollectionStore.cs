using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public interface IRecordCollectionStore
{
    Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default);
    Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default);
    Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);
    Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default);
    Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordWritePreconditionValidator.ThrowIfUnsupported(precondition, GetType().Name);
        return UpsertRecordAsync(collection, record, ct);
    }
    Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default);
    Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default);
    Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default);
    Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default);
    Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default);
}
