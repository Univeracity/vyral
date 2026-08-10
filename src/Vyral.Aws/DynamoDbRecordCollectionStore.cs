using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

/// <summary>
/// IRecordCollectionStore backed by Amazon DynamoDB.
///
/// Collection policies are stored in a shared catalog table (user-supplied name).
/// Each collection's records are stored in a dedicated DynamoDB table named
/// {collectionTablePrefix}{collectionName}. Records are stored as a full JSON blob
/// in the "doc" attribute.
///
/// Query and vector search execute in memory after a full scan or per-partition-key
/// Query — equivalent to the SQLite flat-scan fallback. DynamoDB does not provide
/// native vector search.
///
/// Authentication uses the default AWS credential chain injected via IAmazonDynamoDB.
/// </summary>
public class DynamoDbRecordCollectionStore : IRecordCollectionStore
{
    private const string SupportedPartitionKeyPath = "/partitionKey";
    private const string CatalogPk = "policy";
    private const string PkAttribute = "pk";
    private const string SkAttribute = "sk";
    private const string DocAttribute = "doc";
    private const string PolicyJsonAttribute = "policy_json";

    private static readonly HashSet<string> SupportedDatatypes =
        new(StringComparer.OrdinalIgnoreCase) { VectorDatatypes.Float32 };

    private static readonly HashSet<string> SupportedDistanceFunctions =
        new(StringComparer.OrdinalIgnoreCase) { DistanceFunctions.Cosine, DistanceFunctions.DotProduct, DistanceFunctions.Euclidean };

    private static readonly HashSet<string> SupportedIndexTypes =
        new(StringComparer.OrdinalIgnoreCase) { IndexTypes.Flat, IndexTypes.QuantizedFlat, IndexTypes.DiskAnn };

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IAmazonDynamoDB _client;
    private readonly string _catalogTableName;
    private readonly string _collectionTablePrefix;

    private int _catalogEnsured;

    public DynamoDbRecordCollectionStore(
        IAmazonDynamoDB client,
        string catalogTableName,
        string collectionTablePrefix = "")
    {
        _client = client;
        _catalogTableName = catalogTableName;
        _collectionTablePrefix = collectionTablePrefix;
    }

    // ---------------------------------------------------------------------------
    // Collection management
    // ---------------------------------------------------------------------------

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ValidateCollectionPolicy(policy);
        await EnsureCatalogTableAsync(ct);

        var existing = await GetCollectionPolicyAsync(policy.Name, ct);
        if (existing != null)
        {
            if (!RecordCollectionPolicyComparer.AreEquivalent(existing, policy))
            {
                throw new InvalidOperationException(
                    $"Collection '{policy.Name}' already exists with a different policy.");
            }

            return;
        }

        var tableName = CollectionTableName(policy.Name);
        await EnsureTableAsync(tableName, ct);

        await _client.PutItemAsync(new PutItemRequest
        {
            TableName = _catalogTableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = CatalogPk },
                [SkAttribute] = new AttributeValue { S = policy.Name },
                [PolicyJsonAttribute] = new AttributeValue
                {
                    S = JsonSerializer.Serialize(policy, JsonOptions)
                }
            }
        }, ct);
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await EnsureCatalogTableAsync(ct);

        var results = new List<string>();
        string? lastEvaluatedKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = _catalogTableName,
                KeyConditionExpression = "#pk = :pkval",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#pk"] = PkAttribute
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pkval"] = new AttributeValue { S = CatalogPk }
                }
            };

            if (lastEvaluatedKey != null)
            {
                request.ExclusiveStartKey = new Dictionary<string, AttributeValue>
                {
                    [PkAttribute] = new AttributeValue { S = CatalogPk },
                    [SkAttribute] = new AttributeValue { S = lastEvaluatedKey }
                };
            }

            var response = await _client.QueryAsync(request, ct);
            foreach (var item in response.Items)
            {
                if (item.TryGetValue(SkAttribute, out var sk))
                {
                    results.Add(sk.S);
                }
            }

            lastEvaluatedKey = response.LastEvaluatedKey is { Count: > 0 }
                ? response.LastEvaluatedKey[SkAttribute].S
                : null;
        }
        while (lastEvaluatedKey != null);

        return results.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(
        string collection,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        await EnsureCatalogTableAsync(ct);

        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _catalogTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = CatalogPk },
                [SkAttribute] = new AttributeValue { S = collection }
            }
        }, ct);

        if (!response.IsItemSet) return null;

        if (!response.Item.TryGetValue(PolicyJsonAttribute, out var policyJson))
            return null;

        return JsonSerializer.Deserialize<RecordCollectionPolicy>(policyJson.S, JsonOptions);
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        await EnsureCatalogTableAsync(ct);

        // Delete policy from catalog
        await _client.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _catalogTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = CatalogPk },
                [SkAttribute] = new AttributeValue { S = collection }
            }
        }, ct);

        // Delete all items from the collection table (if it exists)
        var tableName = CollectionTableName(collection);
        if (!await TableExistsAsync(tableName, ct)) return;

        await DeleteAllItemsAsync(tableName, ct);
    }

    // ---------------------------------------------------------------------------
    // Record operations
    // ---------------------------------------------------------------------------

    public async Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
    {
        await UpsertRecordAsync(collection, record, precondition: null, ct);
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordWritePreconditionValidator.ThrowIfUnsupported(precondition, nameof(DynamoDbRecordCollectionStore));
        RecordIdentityValidator.ValidateRecord(record);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        var tableName = CollectionTableName(collection);
        await UpsertRecordCoreAsync(collection, tableName, policy, record, ct);
    }

    private async Task UpsertRecordCoreAsync(
        string collection,
        string tableName,
        RecordCollectionPolicy policy,
        VyralRecord record,
        CancellationToken ct)
    {
        RecordIdentityValidator.ValidateRecord(record);
        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        var existing = await GetRecordAsync(collection, record.PartitionKey, record.Id, ct);
        var now = DateTime.UtcNow;
        record.UpdatedAt = now;
        record.CreatedAt = existing?.CreatedAt ?? record.CreatedAt ?? now;
        record.Revision = (existing?.Revision ?? 0) + 1;
        record.Etag = $"rev:{record.Revision}";

        await _client.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = record.PartitionKey },
                [SkAttribute] = new AttributeValue { S = record.Id },
                [DocAttribute] = new AttributeValue
                {
                    S = JsonSerializer.Serialize(record, JsonOptions)
                }
            }
        }, ct);
    }

    public async Task<RecordBatchUpsertResult> UpsertRecordsAsync(
        string collection,
        RecordBatchUpsertRequest request,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        request.ValidatePreconditionAlignment();
        RecordWritePreconditionValidator.ThrowIfUnsupported(request, nameof(DynamoDbRecordCollectionStore));
        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var tableName = CollectionTableName(collection);
        var itemResults = new List<RecordBatchUpsertItemResult>(request.Records.Count);
        int succeeded = 0, failed = 0;
        bool stopped = false;

        for (int i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            try
            {
                await UpsertRecordCoreAsync(collection, tableName, policy, record, ct);
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i,
                    Id = record.Id,
                    PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Succeeded,
                    Etag = record.Etag,
                    Revision = record.Revision
                });
                succeeded++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                itemResults.Add(new RecordBatchUpsertItemResult
                {
                    Index = i,
                    Id = record.Id,
                    PartitionKey = record.PartitionKey,
                    Status = RecordUpsertStatuses.Failed,
                    Error = ex.Message
                });
                failed++;
                if (!request.ContinueOnError) { stopped = i + 1 < request.Records.Count; break; }
            }
        }

        return new RecordBatchUpsertResult
        {
            Collection = collection,
            Requested = request.Records.Count,
            Attempted = succeeded + failed,
            Succeeded = succeeded,
            Failed = failed,
            StoppedOnError = stopped,
            Items = itemResults
        };
    }

    public async Task<VyralRecord?> GetRecordAsync(
        string collection,
        string partitionKey,
        string id,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        var tableName = CollectionTableName(collection);
        if (!await TableExistsAsync(tableName, ct)) return null;

        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = partitionKey },
                [SkAttribute] = new AttributeValue { S = id }
            }
        }, ct);

        if (!response.IsItemSet) return null;
        return DeserializeRecord(response.Item);
    }

    public async Task DeleteRecordAsync(
        string collection,
        string partitionKey,
        string id,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        var tableName = CollectionTableName(collection);
        if (!await TableExistsAsync(tableName, ct)) return;

        await _client.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [PkAttribute] = new AttributeValue { S = partitionKey },
                [SkAttribute] = new AttributeValue { S = id }
            }
        }, ct);
    }

    // ---------------------------------------------------------------------------
    // Query
    // ---------------------------------------------------------------------------

    public async Task<RecordQueryResult> QueryRecordsPageAsync(
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        ValidatePageLimit(query.Limit, "Query page size");
        FilterValueNormalizer.ValidateFilter(query.Filter);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var all = await LoadFilteredSortedAsync(collection, query, ct);

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit;

        if (!pageSize.HasValue)
        {
            return new RecordQueryResult { Items = all.Skip(offset).ToList() };
        }

        var items = all.Skip(offset).Take(pageSize.Value).ToList();
        var next = offset + items.Count < all.Count
            ? EncodeContinuationToken(offset + items.Count)
            : null;
        return new RecordQueryResult { Items = items, ContinuationToken = next };
    }

    // ---------------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------------

    public async Task<RecordSearchResult> SearchRecordsPageAsync(
        string collection,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        FilterValueNormalizer.ValidateFilter(query.Filter);

        if (query.Vector == null)
        {
            var records = await QueryRecordsPageAsync(collection, query, ct);
            return new RecordSearchResult
            {
                Items = records.Items.Select(r => new VyralRecordMatch
                {
                    Record = r,
                    Score = 1.0f
                }).ToList(),
                ContinuationToken = records.ContinuationToken
            };
        }

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");

        var fieldPolicy = RecordVectorValidator.ValidateSearchVector(collection, policy, query.Vector);
        ValidatePageLimit(query.Limit, "Search page size");

        // Collect candidates
        var candidates = await ScanCollectionAsync(collection, ct);

        if (query.PartitionKeys?.Count > 0)
        {
            var pkSet = new HashSet<string>(query.PartitionKeys, StringComparer.Ordinal);
            candidates = candidates.Where(r => pkSet.Contains(r.PartitionKey)).ToList();
        }

        if (query.Filter != null)
        {
            candidates = candidates.Where(r => MatchesFilter(r, query.Filter)).ToList();
        }

        // Score
        var results = new List<VyralRecordMatch>();
        foreach (var record in candidates)
        {
            if (record.Vectors == null ||
                !record.Vectors.TryGetValue(query.Vector.Field, out var vector))
            {
                continue;
            }

            var score = CalculateSimilarityScore(
                fieldPolicy.DistanceFunction, query.Vector.Value, vector.Values);

            if (query.Vector.MinScore == null || score >= query.Vector.MinScore)
            {
                results.Add(new VyralRecordMatch { Record = record, Score = score });
            }
        }

        // Rank
        var ranked = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Record.PartitionKey, StringComparer.Ordinal)
            .ThenBy(r => r.Record.Id, StringComparer.Ordinal)
            .Take(query.Vector.Top)
            .ToList();

        // Paginate
        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? query.Vector.Top;
        var pageItems = ranked.Skip(offset).Take(pageSize).ToList();
        var nextToken = offset + pageItems.Count < ranked.Count
            ? EncodeContinuationToken(offset + pageItems.Count)
            : null;

        return new RecordSearchResult { Items = pageItems, ContinuationToken = nextToken };
    }

    // ---------------------------------------------------------------------------
    // Internal helpers — data access
    // ---------------------------------------------------------------------------

    private async Task<List<VyralRecord>> LoadFilteredSortedAsync(
        string collection,
        QueryEnvelope query,
        CancellationToken ct)
    {
        List<VyralRecord> all;

        if (query.PartitionKeys?.Count > 0)
        {
            var tasks = query.PartitionKeys.Select(pk =>
                QueryByPartitionKeyAsync(collection, pk, ct));
            var pages = await Task.WhenAll(tasks);
            all = pages.SelectMany(x => x).ToList();
        }
        else
        {
            all = await ScanCollectionAsync(collection, ct);
        }

        if (query.Filter != null)
        {
            all = all.Where(r => MatchesFilter(r, query.Filter)).ToList();
        }

        if (query.OrderBy?.Count > 0)
        {
            all = ApplyOrderBy(all, query.OrderBy);
        }
        else
        {
            all = all
                .OrderBy(r => r.PartitionKey, StringComparer.Ordinal)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
        }

        return all;
    }

    private async Task<List<VyralRecord>> QueryByPartitionKeyAsync(
        string collection,
        string partitionKey,
        CancellationToken ct)
    {
        var tableName = CollectionTableName(collection);
        if (!await TableExistsAsync(tableName, ct)) return new List<VyralRecord>();

        var results = new List<VyralRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "#pk = :pkval",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#pk"] = PkAttribute
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pkval"] = new AttributeValue { S = partitionKey }
                },
                ExclusiveStartKey = lastKey
            };

            var response = await _client.QueryAsync(request, ct);
            foreach (var item in response.Items)
            {
                var record = DeserializeRecord(item);
                if (record != null) results.Add(record);
            }

            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (lastKey != null);

        return results;
    }

    private async Task<List<VyralRecord>> ScanCollectionAsync(
        string collection,
        CancellationToken ct)
    {
        var tableName = CollectionTableName(collection);
        if (!await TableExistsAsync(tableName, ct)) return new List<VyralRecord>();

        var results = new List<VyralRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new ScanRequest
            {
                TableName = tableName,
                ExclusiveStartKey = lastKey
            };

            var response = await _client.ScanAsync(request, ct);
            foreach (var item in response.Items)
            {
                var record = DeserializeRecord(item);
                if (record != null) results.Add(record);
            }

            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (lastKey != null);

        return results;
    }

    private async Task DeleteAllItemsAsync(string tableName, CancellationToken ct)
    {
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var scanRequest = new ScanRequest
            {
                TableName = tableName,
                ProjectionExpression = "#pk, #sk",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#pk"] = PkAttribute,
                    ["#sk"] = SkAttribute
                },
                ExclusiveStartKey = lastKey
            };

            var scanResponse = await _client.ScanAsync(scanRequest, ct);

            // Batch delete in groups of 25 (DynamoDB BatchWrite limit)
            for (var i = 0; i < scanResponse.Items.Count; i += 25)
            {
                var batch = scanResponse.Items.Skip(i).Take(25).Select(item =>
                    new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                [PkAttribute] = item[PkAttribute],
                                [SkAttribute] = item[SkAttribute]
                            }
                        }
                    }).ToList();

                if (batch.Count > 0)
                {
                    await _client.BatchWriteItemAsync(new BatchWriteItemRequest
                    {
                        RequestItems = new Dictionary<string, List<WriteRequest>>
                        {
                            [tableName] = batch
                        }
                    }, ct);
                }
            }

            lastKey = scanResponse.LastEvaluatedKey is { Count: > 0 }
                ? scanResponse.LastEvaluatedKey
                : null;
        }
        while (lastKey != null);
    }

    // ---------------------------------------------------------------------------
    // Internal helpers — table lifecycle
    // ---------------------------------------------------------------------------

    private async Task EnsureCatalogTableAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _catalogEnsured, 1) == 0)
        {
            await EnsureTableAsync(_catalogTableName, ct);
        }
    }

    private async Task EnsureTableAsync(string tableName, CancellationToken ct)
    {
        if (await TableExistsAsync(tableName, ct)) return;

        await _client.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = new List<KeySchemaElement>
            {
                new() { AttributeName = PkAttribute, KeyType = KeyType.HASH },
                new() { AttributeName = SkAttribute, KeyType = KeyType.RANGE }
            },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new() { AttributeName = PkAttribute, AttributeType = ScalarAttributeType.S },
                new() { AttributeName = SkAttribute, AttributeType = ScalarAttributeType.S }
            },
            BillingMode = BillingMode.PAY_PER_REQUEST
        }, ct);

        await WaitForTableActiveAsync(tableName, ct);
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken ct)
    {
        try
        {
            var response = await _client.DescribeTableAsync(tableName, ct);
            return response.Table.TableStatus == TableStatus.ACTIVE;
        }
        catch (ResourceNotFoundException)
        {
            return false;
        }
    }

    private async Task WaitForTableActiveAsync(string tableName, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var response = await _client.DescribeTableAsync(tableName, ct);
                if (response.Table.TableStatus == TableStatus.ACTIVE) return;
            }
            catch (ResourceNotFoundException)
            {
                // Not yet visible
            }

            await Task.Delay(500, ct);
        }
    }

    // ---------------------------------------------------------------------------
    // Internal helpers — filter and sort
    // ---------------------------------------------------------------------------

    private static bool MatchesFilter(VyralRecord record, FilterNode node)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record, JsonOptions));
        return MatchesFilterNode(doc.RootElement, node);
    }

    private static bool MatchesFilterNode(JsonElement root, FilterNode node)
    {
        if (node.Children != null && !string.IsNullOrEmpty(node.Combine))
        {
            if (string.Equals(node.Combine, "any", StringComparison.OrdinalIgnoreCase))
                return node.Children.Any(c => MatchesFilterNode(root, c));
            return node.Children.All(c => MatchesFilterNode(root, c));
        }

        if (node.Path != null)
            return MatchesCondition(root, node.Path, node.Op ?? FilterOps.Eq, node.Value);

        return true;
    }

    private static bool MatchesCondition(JsonElement root, string path, string op, object? value)
    {
        var found = TryGetJsonPointerValue(root, path, out var element);
        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);

        if (normalizedOp == "exists")
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(value);
            if (!found) return !shouldExist;
            // A field that exists but is null counts as "exists" for purposes of exists:true
            return shouldExist;
        }

        var normalizedValue = normalizedOp == "in" ? null : FilterValueNormalizer.NormalizeScalar(value);

        if (!found)
        {
            // Missing and explicit null are distinct portable states. Use exists:false
            // to select a missing field; equality with null selects only explicit null.
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (normalizedOp == "eq") return normalizedValue == null;
            if (normalizedOp == "neq") return normalizedValue != null;
            return false;
        }

        if (normalizedOp == "in")
        {
            return IsInValues(element, FilterValueNormalizer.NormalizeScalarList(value));
        }

        if (normalizedValue == null && normalizedOp is not "eq" and not "neq")
        {
            throw new NotSupportedException($"Operator '{op}' cannot be used with null values.");
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (normalizedValue is not string text)
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");

            if (element.ValueKind != JsonValueKind.String)
                return false;

            return normalizedOp == FilterOps.Contains
                ? element.GetString()!.Contains(text, StringComparison.Ordinal)
                : element.GetString()!.StartsWith(text, StringComparison.Ordinal);
        }

        return normalizedOp switch
        {
            "eq" => normalizedValue != null && CompareToValue(element, normalizedValue) == 0,
            "neq" => normalizedValue == null || CompareToValue(element, normalizedValue) != 0,
            "gt" => CompareToValue(element, normalizedValue) > 0,
            "gte" => CompareToValue(element, normalizedValue) >= 0,
            "lt" => CompareToValue(element, normalizedValue) < 0,
            "lte" => CompareToValue(element, normalizedValue) <= 0,
            _ => false
        };
    }

    private static int CompareToValue(JsonElement element, object? value)
    {
        if (value == null) return 1; // non-null > null

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return string.Compare(element.GetString(), value.ToString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                if (TryConvertToDouble(value, out var dv) && element.TryGetDouble(out var ed))
                    return ed.CompareTo(dv);
                return string.Compare(element.ToString(), value.ToString(), StringComparison.Ordinal);

            case JsonValueKind.True:
                return value is bool bv1 ? true.CompareTo(bv1) : 1;

            case JsonValueKind.False:
                return value is bool bv2 ? false.CompareTo(bv2) : -1;

            default:
                return string.Compare(element.ToString(), value.ToString(), StringComparison.Ordinal);
        }
    }

    private static bool IsInValues(JsonElement element, IReadOnlyList<object?> values)
    {
        return values.Any(v => v != null && CompareToValue(element, v) == 0);
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;
        if (value == null) return false;
        return value switch
        {
            double d => (result = d) == d,
            float f => (result = f) == f,
            int i => (result = i) == i,
            long l => (result = l) == l,
            decimal dec => double.TryParse(dec.ToString(CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out result),
            _ => double.TryParse(value.ToString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out result)
        };
    }

    private static List<VyralRecord> ApplyOrderBy(
        IEnumerable<VyralRecord> records,
        IList<OrderExpression> orderBy)
    {
        IOrderedEnumerable<VyralRecord>? ordered = null;

        for (var i = 0; i < orderBy.Count; i++)
        {
            var expr = orderBy[i];
            var path = expr.Path;
            var descending = string.Equals(expr.Direction, "desc", StringComparison.OrdinalIgnoreCase);

            JsonSortKey Selector(VyralRecord record)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record, JsonOptions));
                return TryGetJsonPointerValue(doc.RootElement, path, out var val)
                    ? new JsonSortKey(val)
                    : JsonSortKey.Absent;
            }

            if (i == 0)
            {
                ordered = descending
                    ? records.OrderByDescending(Selector)
                    : records.OrderBy(Selector);
            }
            else
            {
                ordered = descending
                    ? ordered!.ThenByDescending(Selector)
                    : ordered!.ThenBy(Selector);
            }
        }

        return ordered?.ToList() ?? records.ToList();
    }

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal)) return false;

        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------------------
    // Internal helpers — vector similarity
    // ---------------------------------------------------------------------------

    private static float CalculateSimilarityScore(
        string distanceFunction,
        float[] query,
        float[] stored)
    {
        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => TensorPrimitives.CosineSimilarity(query.AsSpan(), stored.AsSpan()),
            "dotproduct" => TensorPrimitives.Dot(query.AsSpan(), stored.AsSpan()),
            "euclidean" => 1.0f / (1.0f + TensorPrimitives.Distance(query.AsSpan(), stored.AsSpan())),
            _ => throw new InvalidOperationException(
                $"Vector distance function '{distanceFunction}' is not supported.")
        };
    }

    // ---------------------------------------------------------------------------
    // Internal helpers — misc
    // ---------------------------------------------------------------------------

    private string CollectionTableName(string collection) =>
        _collectionTablePrefix + collection;

    private static VyralRecord? DeserializeRecord(Dictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue(DocAttribute, out var doc)) return null;
        return JsonSerializer.Deserialize<VyralRecord>(doc.S, JsonOptions);
    }

    private static void ValidateCollectionPolicy(RecordCollectionPolicy policy)
    {
        RecordIdentityValidator.ValidateCollectionName(policy.Name);

        if (!string.Equals(policy.PartitionKeyPath, SupportedPartitionKeyPath,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Collection partition key path must be '{SupportedPartitionKeyPath}'.");
        }

        if (policy.VectorPolicies.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Vector policy names must be unique within a collection.");

        if (policy.VectorPolicies.GroupBy(p => p.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Vector policy paths must be unique within a collection.");

        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            if (string.IsNullOrWhiteSpace(vectorPolicy.Name))
                throw new InvalidOperationException("Vector policy name is required.");

            if (!vectorPolicy.Name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                throw new InvalidOperationException(
                    $"Vector policy name '{vectorPolicy.Name}' contains unsupported characters.");

            var expectedPath = $"/vectors/{vectorPolicy.Name}/values";
            if (!string.Equals(vectorPolicy.Path, expectedPath, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Vector policy '{vectorPolicy.Name}' path must be '{expectedPath}'.");

            if (vectorPolicy.Dimensions <= 0)
                throw new InvalidOperationException(
                    $"Vector policy '{vectorPolicy.Name}' dimensions must be greater than zero.");

            if (!SupportedDatatypes.Contains(vectorPolicy.Datatype))
                throw new InvalidOperationException(
                    $"Vector policy '{vectorPolicy.Name}' datatype '{vectorPolicy.Datatype}' is not supported.");

            if (!SupportedDistanceFunctions.Contains(vectorPolicy.DistanceFunction))
                throw new InvalidOperationException(
                    $"Vector policy '{vectorPolicy.Name}' distance function '{vectorPolicy.DistanceFunction}' is not supported.");

            if (!SupportedIndexTypes.Contains(vectorPolicy.IndexType))
                throw new InvalidOperationException(
                    $"Vector policy '{vectorPolicy.Name}' index type '{vectorPolicy.IndexType}' is not supported.");
        }
    }

    private static void ValidatePageLimit(int? limit, string description)
    {
        if (limit <= 0)
            throw new InvalidOperationException($"{description} must be greater than zero.");
    }

    private static string EncodeContinuationToken(int offset) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var offset = int.Parse(decoded, CultureInfo.InvariantCulture);
            if (offset < 0)
                throw new InvalidOperationException("Continuation token offset must be non-negative.");
            return offset;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Continuation token is not valid for the DynamoDB adapter.", ex);
        }
    }

    // ---------------------------------------------------------------------------
    // JsonSortKey helper
    // ---------------------------------------------------------------------------

    private sealed class JsonSortKey : IComparable<JsonSortKey>, IComparable
    {
        public static readonly JsonSortKey Absent = new(absent: true);

        private readonly bool _absent;
        private readonly JsonValueKind _kind;
        private readonly double _number;
        private readonly string _text;

        private JsonSortKey(bool absent)
        {
            _absent = absent;
            _kind = JsonValueKind.Undefined;
            _number = 0;
            _text = string.Empty;
        }

        public JsonSortKey(JsonElement element)
        {
            _absent = false;
            _kind = element.ValueKind;
            _number = element.ValueKind == JsonValueKind.Number &&
                      element.TryGetDouble(out var d)
                ? d
                : 0;
            _text = element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.ToString();
        }

        public int CompareTo(JsonSortKey? other)
        {
            if (other == null) return 1;
            if (_absent && other._absent) return 0;
            if (_absent) return -1;
            if (other._absent) return 1;

            if (_kind == JsonValueKind.Number && other._kind == JsonValueKind.Number)
                return _number.CompareTo(other._number);

            return string.Compare(_text, other._text, StringComparison.Ordinal);
        }

        public int CompareTo(object? obj) => CompareTo(obj as JsonSortKey);
    }
}
