using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Google;

/// <summary>
/// IRecordCollectionStore backed by Google Cloud Firestore.
///
/// Firestore is treated here as a durable document store, not as the Vyral
/// ontology. The adapter stores portable Vyral records and applies the shared
/// query/filter semantics in process for the first Google Cloud path.
/// </summary>
public class FirestoreRecordCollectionStore : IRecordCollectionStore
{
    public const int SafeRecordDocumentBytes = 900_000;

    private const string PolicyCollectionSuffix = "_record_policies";
    private const string RecordCollectionSuffix = "_records";
    private const int DefaultPageLimit = 100;
    private const int MaxPageLimit = 5000;
    private const int DeleteBatchSize = 400;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FirestoreDb _db;
    private readonly string _rootCollection;

    public FirestoreRecordCollectionStore(FirestoreDb db, string rootCollection = "vyral")
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rootCollection = NormalizeRootCollection(rootCollection);
    }

    public async Task CreateCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ValidateCollectionPolicy(policy);

        var existing = await GetCollectionPolicyAsync(policy.Name, ct);
        if (existing != null)
        {
            if (!RecordCollectionPolicyComparer.AreEquivalent(existing, policy))
            {
                throw new InvalidOperationException($"Collection '{policy.Name}' already exists with a different policy.");
            }

            return;
        }

        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        await PolicyDocument(policy.Name).SetAsync(new Dictionary<string, object?>
        {
            ["name"] = policy.Name,
            ["policyJson"] = JsonSerializer.Serialize(policy, JsonOptions),
            ["createdAt"] = now,
            ["updatedAt"] = now
        }, cancellationToken: ct);
    }

    public async Task<IEnumerable<string>> GetCollectionsAsync(CancellationToken ct = default)
    {
        var snapshot = await Policies.GetSnapshotAsync(ct);
        return snapshot.Documents
            .Select(document => TryGetString(document, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RecordCollectionPolicy?> GetCollectionPolicyAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        var snapshot = await PolicyDocument(collection).GetSnapshotAsync(ct);
        if (!snapshot.Exists)
        {
            return null;
        }

        var policyJson = TryGetString(snapshot, "policyJson");
        return string.IsNullOrWhiteSpace(policyJson)
            ? null
            : JsonSerializer.Deserialize<RecordCollectionPolicy>(policyJson, JsonOptions);
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);

        await PolicyDocument(collection).DeleteAsync(cancellationToken: ct);
        while (true)
        {
            var snapshot = await Records
                .WhereEqualTo("collection", collection)
                .Limit(DeleteBatchSize)
                .GetSnapshotAsync(ct);
            if (snapshot.Count == 0)
            {
                break;
            }

            var batch = _db.StartBatch();
            foreach (var document in snapshot.Documents)
            {
                batch.Delete(document.Reference);
            }

            await batch.CommitAsync(ct);
        }
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, CancellationToken ct = default)
    {
        await UpsertRecordAsync(collection, record, precondition: null, ct);
    }

    public async Task UpsertRecordAsync(string collection, VyralRecord record, RecordWritePrecondition? precondition, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidateRecord(record);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        RecordVectorValidator.ValidateRecordVectors(collection, policy, record);

        var document = RecordDocument(collection, record.PartitionKey, record.Id);
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
            var existing = snapshot.Exists ? DeserializeRecord(snapshot) : null;
            RecordWritePreconditionValidator.EnsureSatisfied(precondition, snapshot.Exists, existing?.Etag, existing?.Revision);

            ApplyPortableVersion(record, existing, DateTime.UtcNow);
            var recordJson = JsonSerializer.Serialize(record, JsonOptions);
            ValidateRecordDocumentSize(recordJson);

            transaction.Set(document, BuildRecordDocument(collection, record, recordJson));
        }, cancellationToken: ct);
    }

    public async Task<RecordBatchUpsertResult> UpsertRecordsAsync(string collection, RecordBatchUpsertRequest request, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        request.ValidatePreconditionAlignment();

        var itemResults = new List<RecordBatchUpsertItemResult>(request.Records.Count);
        var succeeded = 0;
        var failed = 0;
        var stopped = false;

        for (var i = 0; i < request.Records.Count; i++)
        {
            var record = request.Records[i];
            try
            {
                await UpsertRecordAsync(collection, record, request.GetPrecondition(i), ct);
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
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
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
                if (!request.ContinueOnError)
                {
                    stopped = i + 1 < request.Records.Count;
                    break;
                }
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

    public async Task<VyralRecord?> GetRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        var snapshot = await RecordDocument(collection, partitionKey, id).GetSnapshotAsync(ct);
        return snapshot.Exists ? DeserializeRecord(snapshot) : null;
    }

    public async Task DeleteRecordAsync(string collection, string partitionKey, string id, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        RecordIdentityValidator.ValidatePartitionKey(partitionKey);
        RecordIdentityValidator.ValidateRecordId(id);

        await RecordDocument(collection, partitionKey, id).DeleteAsync(cancellationToken: ct);
    }

    public async Task<RecordQueryResult> QueryRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        ValidatePageLimit(query.Limit, "Query page size");
        FilterValueNormalizer.ValidateFilter(query.Filter);

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

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

    public async Task<RecordSearchResult> SearchRecordsPageAsync(string collection, QueryEnvelope query, CancellationToken ct = default)
    {
        RecordIdentityValidator.ValidateCollectionName(collection);
        FilterValueNormalizer.ValidateFilter(query.Filter);

        if (query.Lexical != null)
        {
            throw new NotSupportedException("Firestore record search does not provide lexical ranking. Use QueryRecordsPageAsync for filtered record scans or configure a search-capable store.");
        }

        if (query.Vector == null)
        {
            var records = await QueryRecordsPageAsync(collection, query, ct);
            return new RecordSearchResult
            {
                Items = records.Items.Select(record => new VyralRecordMatch
                {
                    Record = record,
                    Score = 1.0f
                }).ToList(),
                ContinuationToken = records.ContinuationToken
            };
        }

        var policy = await GetCollectionPolicyAsync(collection, ct);
        if (policy == null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        var fieldPolicy = RecordVectorValidator.ValidateSearchVector(collection, policy, query.Vector);
        ValidatePageLimit(query.Limit, "Search page size");

        var candidates = await LoadFilteredSortedAsync(collection, new QueryEnvelope
        {
            PartitionKeys = query.PartitionKeys,
            Filter = query.Filter,
            OrderBy = null
        }, ct);

        var ranked = new List<VyralRecordMatch>();
        foreach (var record in candidates)
        {
            if (record.Vectors == null ||
                !record.Vectors.TryGetValue(query.Vector.Field, out var vector))
            {
                continue;
            }

            var score = CalculateSimilarityScore(fieldPolicy.DistanceFunction, query.Vector.Value, vector.Values);
            if (query.Vector.MinScore == null || score >= query.Vector.MinScore)
            {
                ranked.Add(new VyralRecordMatch { Record = record, Score = score });
            }
        }

        ranked = ranked
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Record.PartitionKey, StringComparer.Ordinal)
            .ThenBy(match => match.Record.Id, StringComparer.Ordinal)
            .Take(query.Vector.Top)
            .ToList();

        var offset = DecodeContinuationToken(query.ContinuationToken);
        var pageSize = query.Limit ?? query.Vector.Top;
        var pageItems = ranked.Skip(offset).Take(pageSize).ToList();
        var next = offset + pageItems.Count < ranked.Count
            ? EncodeContinuationToken(offset + pageItems.Count)
            : null;
        return new RecordSearchResult { Items = pageItems, ContinuationToken = next };
    }

    public static string BuildPolicyDocumentId(string collection) => Sha256Base64Url(collection);

    public static string BuildRecordDocumentId(string collection, string partitionKey, string id) =>
        Sha256Base64Url($"{collection}\n{partitionKey}\n{id}");

    private CollectionReference Policies => _db.Collection(_rootCollection + PolicyCollectionSuffix);

    private CollectionReference Records => _db.Collection(_rootCollection + RecordCollectionSuffix);

    private DocumentReference PolicyDocument(string collection) => Policies.Document(BuildPolicyDocumentId(collection));

    private DocumentReference RecordDocument(string collection, string partitionKey, string id) =>
        Records.Document(BuildRecordDocumentId(collection, partitionKey, id));

    private async Task<List<VyralRecord>> LoadFilteredSortedAsync(
        string collection,
        QueryEnvelope query,
        CancellationToken ct)
    {
        Query firestoreQuery = Records.WhereEqualTo("collection", collection);
        if (query.PartitionKeys is { Count: 1 })
        {
            firestoreQuery = firestoreQuery.WhereEqualTo("partitionKey", query.PartitionKeys[0]);
        }

        var snapshot = await firestoreQuery.GetSnapshotAsync(ct);
        var all = snapshot.Documents
            .Select(DeserializeRecord)
            .Where(record => record != null)
            .Select(record => record!)
            .ToList();

        if (query.PartitionKeys is { Count: > 1 })
        {
            var partitionKeys = new HashSet<string>(query.PartitionKeys, StringComparer.Ordinal);
            all = all.Where(record => partitionKeys.Contains(record.PartitionKey)).ToList();
        }

        if (query.Filter != null)
        {
            all = all.Where(record => MatchesFilter(record, query.Filter)).ToList();
        }

        if (query.OrderBy?.Count > 0)
        {
            all = ApplyOrderBy(all, query.OrderBy);
        }
        else
        {
            all = all
                .OrderBy(record => record.PartitionKey, StringComparer.Ordinal)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .ToList();
        }

        return all;
    }

    private static VyralRecord? DeserializeRecord(DocumentSnapshot snapshot)
    {
        var recordJson = TryGetString(snapshot, "recordJson");
        return string.IsNullOrWhiteSpace(recordJson)
            ? null
            : JsonSerializer.Deserialize<VyralRecord>(recordJson, JsonOptions);
    }

    private static void ApplyPortableVersion(VyralRecord record, VyralRecord? existing, DateTime now)
    {
        record.UpdatedAt = now;
        record.CreatedAt = existing?.CreatedAt ?? record.CreatedAt ?? now;
        record.Revision = (existing?.Revision ?? 0) + 1;
        record.Etag = $"rev:{record.Revision}";
    }

    private static Dictionary<string, object?> BuildRecordDocument(string collection, VyralRecord record, string recordJson) =>
        new()
        {
            ["collection"] = collection,
            ["partitionKey"] = record.PartitionKey,
            ["id"] = record.Id,
            ["type"] = record.Type,
            ["schemaVersion"] = record.SchemaVersion,
            ["createdAt"] = ToTimestamp(record.CreatedAt),
            ["updatedAt"] = ToTimestamp(record.UpdatedAt),
            ["revision"] = record.Revision,
            ["etag"] = record.Etag,
            ["recordJson"] = recordJson
        };

    private static void ValidateCollectionPolicy(RecordCollectionPolicy policy)
    {
        RecordIdentityValidator.ValidateCollectionName(policy.Name);
        if (!string.Equals(policy.PartitionKeyPath, "/partitionKey", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Collection partition key path must be '/partitionKey'.");
        }

        if (policy.VectorPolicies.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy names must be unique within a collection.");
        }

        if (policy.VectorPolicies.GroupBy(item => item.Path, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Vector policy paths must be unique within a collection.");
        }
    }

    private static void ValidateRecordDocumentSize(string recordJson)
    {
        var byteCount = Encoding.UTF8.GetByteCount(recordJson);
        if (byteCount > SafeRecordDocumentBytes)
        {
            throw new InvalidOperationException($"Firestore record document would exceed the safe portable size budget ({byteCount} bytes > {SafeRecordDocumentBytes} bytes). Store large/raw payloads as objects and keep the record compact.");
        }
    }

    private static int ValidatePageLimit(int? limit, string description)
    {
        if (limit <= 0)
        {
            throw new InvalidOperationException($"{description} must be greater than zero.");
        }

        var effectiveLimit = limit ?? DefaultPageLimit;
        if (effectiveLimit > MaxPageLimit)
        {
            throw new InvalidOperationException($"{description} cannot exceed {MaxPageLimit}.");
        }

        return effectiveLimit;
    }

    private static bool MatchesFilter(VyralRecord record, FilterNode node)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record, JsonOptions));
        return MatchesFilterNode(doc.RootElement, node);
    }

    private static bool MatchesFilterNode(JsonElement root, FilterNode node)
    {
        if (node.Children != null && !string.IsNullOrEmpty(node.Combine))
        {
            if (string.Equals(node.Combine, FilterCombineModes.Any, StringComparison.OrdinalIgnoreCase))
            {
                return node.Children.Any(child => MatchesFilterNode(root, child));
            }

            return node.Children.All(child => MatchesFilterNode(root, child));
        }

        return node.Path == null || MatchesCondition(root, node.Path, node.Op ?? FilterOps.Eq, node.Value);
    }

    private static bool MatchesCondition(JsonElement root, string path, string op, object? value)
    {
        var found = TryGetJsonPointerValue(root, path, out var element);
        var normalizedOp = FilterValueNormalizer.NormalizeOperator(op);

        if (normalizedOp == FilterOps.Exists)
        {
            var shouldExist = FilterValueNormalizer.NormalizeExistsValue(value);
            return found ? shouldExist : !shouldExist;
        }

        var normalizedValue = normalizedOp == FilterOps.In
            ? null
            : FilterValueNormalizer.NormalizeScalar(value);

        if (!found)
        {
            // Missing and explicit null are distinct portable states. Use exists:false
            // to select a missing field; equality with null selects only explicit null.
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (normalizedOp == FilterOps.Eq) return normalizedValue == null;
            if (normalizedOp == FilterOps.Neq) return normalizedValue != null;
            return false;
        }

        if (normalizedOp == FilterOps.In)
        {
            return FilterValueNormalizer.NormalizeScalarList(value).Any(item => item != null && CompareToValue(element, item) == 0);
        }

        if (normalizedValue == null && normalizedOp is not FilterOps.Eq and not FilterOps.Neq)
        {
            throw new NotSupportedException($"Operator '{op}' cannot be used with null values.");
        }

        if (normalizedOp is FilterOps.Contains or FilterOps.StartsWith)
        {
            if (normalizedValue is not string text)
            {
                throw new NotSupportedException($"Filter operator '{op}' requires a string value.");
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return normalizedOp == FilterOps.Contains
                ? element.GetString()!.Contains(text, StringComparison.Ordinal)
                : element.GetString()!.StartsWith(text, StringComparison.Ordinal);
        }

        return normalizedOp switch
        {
            FilterOps.Eq => normalizedValue != null && CompareToValue(element, normalizedValue) == 0,
            FilterOps.Neq => normalizedValue == null || CompareToValue(element, normalizedValue) != 0,
            FilterOps.Gt => CompareToValue(element, normalizedValue) > 0,
            FilterOps.Gte => CompareToValue(element, normalizedValue) >= 0,
            FilterOps.Lt => CompareToValue(element, normalizedValue) < 0,
            FilterOps.Lte => CompareToValue(element, normalizedValue) <= 0,
            _ => false
        };
    }

    private static List<VyralRecord> ApplyOrderBy(IEnumerable<VyralRecord> records, IList<OrderExpression> orderBy)
    {
        IOrderedEnumerable<VyralRecord>? ordered = null;
        for (var i = 0; i < orderBy.Count; i++)
        {
            var expression = orderBy[i];
            var descending = string.Equals(expression.Direction, SortDirections.Desc, StringComparison.OrdinalIgnoreCase);

            JsonSortKey Selector(VyralRecord record)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record, JsonOptions));
                return TryGetJsonPointerValue(doc.RootElement, expression.Path, out var value)
                    ? new JsonSortKey(value)
                    : JsonSortKey.Absent;
            }

            ordered = i == 0
                ? descending ? records.OrderByDescending(Selector) : records.OrderBy(Selector)
                : descending ? ordered!.ThenByDescending(Selector) : ordered!.ThenBy(Selector);
        }

        return ordered?.ToList() ?? records.ToList();
    }

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareToValue(JsonElement element, object? value)
    {
        if (value == null)
        {
            return 1;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => string.Compare(element.GetString(), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal),
            JsonValueKind.Number when TryConvertToDouble(value, out var number) && element.TryGetDouble(out var elementNumber) => elementNumber.CompareTo(number),
            JsonValueKind.Number => string.Compare(element.ToString(), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal),
            JsonValueKind.True => value is bool boolean ? true.CompareTo(boolean) : 1,
            JsonValueKind.False => value is bool boolean ? false.CompareTo(boolean) : -1,
            _ => string.Compare(element.ToString(), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal)
        };
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;
        return value switch
        {
            double number => (result = number) == number,
            float number => (result = number) == number,
            int number => (result = number) == number,
            long number => (result = number) == number,
            decimal number => double.TryParse(number.ToString(CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result),
            _ => value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
        };
    }

    private static float CalculateSimilarityScore(string distanceFunction, float[] query, float[] stored)
    {
        if (query.Length != stored.Length)
        {
            throw new InvalidOperationException($"Vector dimensions differ: {query.Length} != {stored.Length}.");
        }

        var dot = 0d;
        var queryMagnitude = 0d;
        var storedMagnitude = 0d;
        var squaredDistance = 0d;
        for (var i = 0; i < query.Length; i++)
        {
            dot += query[i] * stored[i];
            queryMagnitude += query[i] * query[i];
            storedMagnitude += stored[i] * stored[i];
            var delta = query[i] - stored[i];
            squaredDistance += delta * delta;
        }

        return distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => queryMagnitude == 0 || storedMagnitude == 0
                ? 0
                : (float)(dot / (Math.Sqrt(queryMagnitude) * Math.Sqrt(storedMagnitude))),
            "dotproduct" => (float)dot,
            "euclidean" => (float)(1.0 / (1.0 + Math.Sqrt(squaredDistance))),
            _ => throw new InvalidOperationException($"Vector distance function '{distanceFunction}' is not supported.")
        };
    }

    private static Timestamp? ToTimestamp(DateTime? value)
    {
        return value.HasValue
            ? Timestamp.FromDateTime(DateTime.SpecifyKind(value.Value.ToUniversalTime(), DateTimeKind.Utc))
            : null;
    }

    private static string? TryGetString(DocumentSnapshot snapshot, string field)
    {
        return snapshot.TryGetValue<string>(field, out var value) ? value : null;
    }

    private static string EncodeContinuationToken(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return 0;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var offset = int.Parse(decoded, CultureInfo.InvariantCulture);
            if (offset < 0)
            {
                throw new InvalidOperationException("Continuation token offset must be non-negative.");
            }

            return offset;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Continuation token is not valid for the Firestore adapter.", ex);
        }
    }

    private static string Sha256Base64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NormalizeRootCollection(string rootCollection)
    {
        var value = string.IsNullOrWhiteSpace(rootCollection) ? "vyral" : rootCollection.Trim();
        if (value.Contains('/'))
        {
            throw new InvalidOperationException("Firestore root collection prefix must not contain '/'.");
        }

        if (value is "." or "..")
        {
            throw new InvalidOperationException("Firestore root collection prefix cannot be '.' or '..'.");
        }

        return value;
    }

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
            _number = element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) ? number : 0;
            _text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
        }

        public int CompareTo(JsonSortKey? other)
        {
            if (other == null) return 1;
            if (_absent && other._absent) return 0;
            if (_absent) return -1;
            if (other._absent) return 1;

            return _kind == JsonValueKind.Number && other._kind == JsonValueKind.Number
                ? _number.CompareTo(other._number)
                : string.Compare(_text, other._text, StringComparison.Ordinal);
        }

        public int CompareTo(object? obj) => CompareTo(obj as JsonSortKey);
    }
}
