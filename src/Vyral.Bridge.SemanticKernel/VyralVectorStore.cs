using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Bridge.SemanticKernel;

public class VyralVectorStore : VectorStore
{
    private readonly IRecordCollectionStore _recordStore;
    private readonly Dictionary<string, object> _collectionOptions = new(StringComparer.Ordinal);

    public VyralVectorStore(IRecordCollectionStore recordStore)
    {
        _recordStore = recordStore;
    }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? setup = null)
    {
        _collectionOptions.TryGetValue(name, out var options);
        return new VyralVectorStoreRecordCollection<TKey, TRecord>(_recordStore, name, options as VyralVectorStoreCollectionOptions<TKey, TRecord>);
    }

    public VyralVectorStoreRecordCollection<TKey, TRecord> GetMappedCollection<TKey, TRecord>(
        string name,
        VyralVectorStoreCollectionOptions<TKey, TRecord> options)
        where TKey : notnull
        where TRecord : class
    {
        _collectionOptions[name] = options;
        return new VyralVectorStoreRecordCollection<TKey, TRecord>(_recordStore, name, options);
    }

    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var names = await _recordStore.GetCollectionsAsync(cancellationToken);
        foreach (var name in names)
        {
            yield return name;
        }
    }

    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        var names = await _recordStore.GetCollectionsAsync(cancellationToken);
        return names.Contains(name, StringComparer.Ordinal);
    }

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        return _recordStore.DeleteCollectionAsync(name, cancellationToken);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition? setup = null)
    {
        throw new NotSupportedException("Dynamic Semantic Kernel collections are not implemented by the VYRAL bridge yet.");
    }
}

public class VyralVectorStoreRecordCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly IRecordCollectionStore _recordStore;
    private readonly string _name;
    private readonly VyralVectorStoreCollectionOptions<TKey, TRecord>? _options;

    public override string Name => _name;

    public VyralVectorStoreRecordCollection(
        IRecordCollectionStore recordStore,
        string name,
        VyralVectorStoreCollectionOptions<TKey, TRecord>? options = null)
        : base()
    {
        _recordStore = recordStore;
        _name = name;
        _options = options;
    }

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        var policy = CreatePolicy(RequireOptions());
        return _recordStore.CreateCollectionAsync(policy, cancellationToken);
    }

    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        var names = await _recordStore.GetCollectionsAsync(cancellationToken);
        return names.Contains(_name, StringComparer.Ordinal);
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        return _recordStore.DeleteCollectionAsync(_name, cancellationToken);
    }

    public override async Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        var mapping = RequireOptions();
        var record = await _recordStore.GetRecordAsync(
            _name,
            mapping.GetPartitionKey(key),
            mapping.GetRecordId(key),
            cancellationToken);
        return record == null ? null : mapping.FromVyralRecord(record);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(
        Expression<Func<TRecord, bool>> predicate,
        int limit = 1,
        FilteredRecordRetrievalOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            yield break;
        }

        var mapping = RequireOptions();
        var skip = options?.Skip ?? 0;
        var requestedLimit = checked(limit + skip);
        var query = new QueryEnvelope
        {
            Filter = VyralFilterTranslator.Translate(predicate, mapping.FilterPropertyPaths),
            OrderBy = VyralFilterTranslator.TranslateOrderBy<TRecord>(options?.OrderBy, mapping.FilterPropertyPaths),
            Limit = requestedLimit
        };

        var records = await _recordStore.QueryAllRecordsAsync(_name, query, cancellationToken);
        foreach (var record in records.Skip(skip))
        {
            yield return mapping.FromVyralRecord(record);
        }
    }

    public override async Task<TKey> UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        var mapping = RequireOptions();
        var key = mapping.GetKey(record);
        var vyralRecord = mapping.ToVyralRecord(record);
        if (string.IsNullOrWhiteSpace(vyralRecord.Id))
        {
            vyralRecord.Id = mapping.GetRecordId(key);
        }

        if (string.IsNullOrWhiteSpace(vyralRecord.PartitionKey))
        {
            vyralRecord.PartitionKey = mapping.GetPartitionKey(key);
        }

        await _recordStore.UpsertRecordAsync(_name, vyralRecord, cancellationToken);
        return key;
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            await UpsertAsync(record, cancellationToken);
        }
    }

    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var mapping = RequireOptions();
        return _recordStore.DeleteRecordAsync(
            _name,
            mapping.GetPartitionKey(key),
            mapping.GetRecordId(key),
            cancellationToken);
    }

    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            await DeleteAsync(key, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput vector,
        int top,
        VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options?.VectorProperty != null)
        {
            throw new NotSupportedException("Configure the VYRAL vector field on VyralVectorStoreCollectionOptions instead of per-call SK vector properties.");
        }

        var mapping = RequireOptions();
        var skip = options?.Skip ?? 0;
        var requestedTop = checked(top + skip);
        var query = new QueryEnvelope
        {
            Limit = requestedTop,
            Filter = options?.Filter == null ? null : VyralFilterTranslator.Translate(options.Filter, mapping.FilterPropertyPaths),
            Vector = new VectorSearchOptions
            {
                Field = mapping.VectorField,
                Value = ConvertVector(vector),
                Top = requestedTop,
                MinScore = options?.ScoreThreshold is null ? null : Convert.ToSingle(options.ScoreThreshold.Value)
            }
        };

        var matches = await _recordStore.SearchAllRecordsAsync(_name, query, cancellationToken);
        foreach (var match in matches.Skip(skip))
        {
            yield return new VectorSearchResult<TRecord>(mapping.FromVyralRecord(match.Record), match.Score);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType.IsInstanceOfType(this)) return this;
        if (serviceType == typeof(IRecordCollectionStore)) return _recordStore;
        return null;
    }

    private VyralVectorStoreCollectionOptions<TKey, TRecord> RequireOptions()
    {
        return _options ?? throw new NotSupportedException("Configure VyralVectorStoreCollectionOptions before using this Semantic Kernel collection.");
    }

    private RecordCollectionPolicy CreatePolicy(VyralVectorStoreCollectionOptions<TKey, TRecord> options)
    {
        if (options.CollectionPolicy == null)
        {
            return new RecordCollectionPolicy { Name = _name };
        }

        if (!string.Equals(options.CollectionPolicy.Name, _name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Collection policy name '{options.CollectionPolicy.Name}' does not match Semantic Kernel collection name '{_name}'.");
        }

        return options.CollectionPolicy;
    }

    private static float[] ConvertVector<TInput>(TInput vector)
    {
        return vector switch
        {
            float[] values => values,
            ReadOnlyMemory<float> values => values.ToArray(),
            Memory<float> values => values.ToArray(),
            IEnumerable<float> values => values.ToArray(),
            double[] values => values.Select(Convert.ToSingle).ToArray(),
            IEnumerable<double> values => values.Select(Convert.ToSingle).ToArray(),
            _ => throw new NotSupportedException($"Vector input type '{typeof(TInput).Name}' is not supported by the VYRAL Semantic Kernel bridge.")
        };
    }

    private static async IAsyncEnumerable<T> ThrowAsync<T>(string message)
    {
        await Task.CompletedTask;
        if (!string.IsNullOrEmpty(message))
        {
            throw new NotSupportedException(message);
        }
        yield break;
    }
}
