using System;
using System.Collections.Generic;
using Vyral.Abstractions.Models;

namespace Vyral.Bridge.SemanticKernel;

public sealed class VyralVectorStoreCollectionOptions<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    public required Func<TKey, string> GetRecordId { get; init; }

    public required Func<TKey, string> GetPartitionKey { get; init; }

    public required Func<TRecord, TKey> GetKey { get; init; }

    public required Func<TRecord, VyralRecord> ToVyralRecord { get; init; }

    public required Func<VyralRecord, TRecord> FromVyralRecord { get; init; }

    public RecordCollectionPolicy? CollectionPolicy { get; init; }

    public string VectorField { get; init; } = "contentEmbedding";

    public Dictionary<string, string> FilterPropertyPaths { get; init; } = new(StringComparer.Ordinal);
}
