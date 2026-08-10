using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Aws;

/// <summary>
/// Optional vector-search projection for a DynamoDB-backed Vyral collection.
/// It owns only derived OpenSearch documents. Feed it from DynamoDB Streams
/// (not from the request path) and hydrate every candidate from DynamoDB using
/// <see cref="RecordSearchProjectionExtensions.SearchAndHydrateAsync"/>.
/// </summary>
public sealed class OpenSearchRecordSearchProjection : IRecordSearchProjection, IRecordSearchProjectionProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOpenSearchTransport _transport;
    private readonly OpenSearchRecordSearchProjectionOptions _options;
    private readonly IRecordCollectionStore? _policyStore;
    private readonly ConcurrentDictionary<string, RecordCollectionPolicy> _policies = new(StringComparer.Ordinal);

    public OpenSearchRecordSearchProjection(
        IOpenSearchTransport transport,
        OpenSearchRecordSearchProjectionOptions options,
        IRecordCollectionStore? policyStore = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaximumCandidates is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum OpenSearch candidates must be between 1 and 10,000.");
        _policyStore = policyStore;
    }

    public async Task EnsureCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ValidatePolicy(policy);
        var index = _options.GetIndexName(policy);
        var response = await _transport.SendAsync(HttpMethod.Put, $"/{index}",
            BuildCreateIndexPayload(policy).ToJsonString(JsonOptions), ct);

        // Resource-already-exists makes provisioning idempotent. A policy
        // mapping change is intentionally not applied in place: OpenSearch
        // field types are immutable, so consumers migrate to a new index.
        if (response.StatusCode is >= 200 and < 300 || response.StatusCode == 400 &&
            response.Body.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            _policies[policy.Name] = ClonePolicy(policy);
            return;
        }

        ThrowForFailure("ensure the OpenSearch projection index", response);
    }

    public async Task DeleteCollectionAsync(RecordCollectionPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var response = await _transport.SendAsync(HttpMethod.Delete, $"/{_options.GetIndexName(policy)}", null, ct);
        if (response.StatusCode is >= 200 and < 300 or 404)
        {
            _policies.TryRemove(policy.Name, out _);
            return;
        }
        ThrowForFailure("delete the OpenSearch projection index", response);
    }

    public async Task ProjectAsync(RecordSearchProjectionChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        var policy = await ResolvePolicyAsync(change.Collection, ct);
        if (policy is not null) ValidatePolicy(policy);
        var index = policy is null ? _options.GetIndexName(change.Collection) : _options.GetIndexName(policy);
        var identity = ProjectionDocumentId(change.PartitionKey, change.Id);
        var path = $"/{index}/_doc/{identity}?version={change.Revision.ToString(CultureInfo.InvariantCulture)}&version_type=external_gte";
        string? body = null;
        if (change.Operation == RecordSearchProjectionOperations.Upsert)
        {
            if (policy is null)
            {
                throw new InvalidOperationException(
                    $"OpenSearch projection cannot project collection '{change.Collection}' without its canonical collection policy. " +
                    "Call EnsureCollectionAsync during worker startup or provide the canonical policy store.");
            }
            ValidatePolicy(policy);
            body = BuildDocument(change.Record!, policy).ToJsonString(JsonOptions);
        }
        var response = await _transport.SendAsync(
            change.Operation == RecordSearchProjectionOperations.Upsert ? HttpMethod.Put : HttpMethod.Delete,
            path,
            body,
            ct);

        // Version conflicts are successful no-ops: an at-least-once stream has
        // delivered this mutation after a later canonical revision.
        if (response.StatusCode is >= 200 and < 300 or 409) return;
        ThrowForFailure("project the canonical record into OpenSearch", response);
    }

    public async Task<RecordSearchProjectionResult> SearchAsync(
        RecordCollectionPolicy policy,
        QueryEnvelope query,
        CancellationToken ct = default)
    {
        ValidatePolicy(policy);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Vector is null)
            throw new NotSupportedException("The OpenSearch projection currently supports vector candidate retrieval only.");
        if (query.Lexical is not null || query.OrderBy is { Count: > 0 })
            throw new NotSupportedException("The OpenSearch projection does not combine lexical retrieval or ordering with vector candidate retrieval.");
        if (!string.IsNullOrWhiteSpace(query.ContinuationToken))
            throw new NotSupportedException("The OpenSearch projection intentionally does not expose continuation tokens for approximate k-NN retrieval.");

        FilterValueNormalizer.ValidateFilter(query.Filter);
        var vectorPolicy = RecordVectorValidator.ValidateSearchVector(policy.Name, policy, query.Vector);
        if (query.Vector.MinScore.HasValue)
            throw new NotSupportedException("OpenSearch k-NN scores are provider-shaped; use client-side thresholds after canonical hydration.");

        var requested = query.Limit ?? query.Vector.Top;
        if (requested <= 0) throw new InvalidOperationException("Search page size must be greater than zero.");
        // A candidate cap is an operational safety boundary, not an approximate
        // pagination mechanism. Silently reducing k would let a request claim a
        // larger result set than the projection ever considered and could hide a
        // recall shortfall. Require callers to choose a bounded request instead.
        if (query.Vector.Top > _options.MaximumCandidates || requested > _options.MaximumCandidates)
        {
            throw new InvalidOperationException(
                $"OpenSearch vector search top and page size must not exceed the configured maximum candidate count ({_options.MaximumCandidates}).");
        }

        var candidates = Math.Max(query.Vector.Top, requested);
        var filter = BuildFilter(policy, query);
        var knn = new JsonObject
        {
            ["vector"] = new JsonArray(query.Vector.Value.Select(value => JsonValue.Create(value)).ToArray()),
            ["k"] = candidates
        };
        if (filter is not null) knn["filter"] = filter;

        var request = new JsonObject
        {
            ["size"] = requested,
            ["_source"] = new JsonArray("partitionKey", "id", "revision"),
            ["track_total_hits"] = false,
            ["query"] = new JsonObject
            {
                // Vector values live beneath the deliberately closed `vectors`
                // object. A leaf name is correct while writing the document or
                // declaring its mapping, but OpenSearch query DSL addresses the
                // complete field path.
                ["knn"] = new JsonObject { [VectorPath(vectorPolicy.Name)] = knn }
            }
        };
        var response = await _transport.SendAsync(HttpMethod.Post,
            $"/{_options.GetIndexName(policy)}/_search",
            request.ToJsonString(JsonOptions),
            ct);
        if (response.StatusCode is < 200 or >= 300) ThrowForFailure("search the OpenSearch projection", response);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var hits = document.RootElement.GetProperty("hits").GetProperty("hits");
            var results = new List<RecordSearchProjectionCandidate>();
            foreach (var hit in hits.EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                if (!source.TryGetProperty("partitionKey", out var partitionKey) ||
                    !source.TryGetProperty("id", out var id) ||
                    !source.TryGetProperty("revision", out var revision) ||
                    partitionKey.ValueKind != JsonValueKind.String ||
                    id.ValueKind != JsonValueKind.String ||
                    !revision.TryGetInt32(out var revisionValue))
                {
                    throw new InvalidOperationException("OpenSearch returned a projection document without a canonical record identity.");
                }
                var partitionKeyValue = partitionKey.GetString();
                var idValue = id.GetString();
                if (partitionKeyValue is null || idValue is null)
                    throw new InvalidOperationException("OpenSearch returned an empty projection document identity.");

                results.Add(new RecordSearchProjectionCandidate
                {
                    PartitionKey = partitionKeyValue,
                    Id = idValue,
                    Revision = revisionValue,
                    Score = hit.TryGetProperty("_score", out var score) && score.TryGetSingle(out var scoreValue)
                        ? scoreValue
                        : 0
                });
            }

            return new RecordSearchProjectionResult { Items = results };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("OpenSearch returned an invalid projection search response.", exception);
        }
    }

    private static JsonObject BuildCreateIndexPayload(RecordCollectionPolicy policy)
    {
        var vectorProperties = new JsonObject();
        foreach (var vector in policy.VectorPolicies)
        {
            vectorProperties[VectorField(vector.Name)] = new JsonObject
            {
                ["type"] = "knn_vector",
                ["dimension"] = vector.Dimensions,
                ["method"] = new JsonObject
                {
                    ["name"] = "hnsw",
                    ["engine"] = "faiss",
                    ["space_type"] = vector.DistanceFunction.ToLowerInvariant() switch
                    {
                        DistanceFunctions.Cosine => "cosinesimil",
                        DistanceFunctions.DotProduct => "innerproduct",
                        DistanceFunctions.Euclidean => "l2",
                        _ => throw new InvalidOperationException($"Unsupported projection distance function '{vector.DistanceFunction}'.")
                    }
                }
            };
        }

        var filterProperties = new JsonObject
        {
            [FilterField("/partitionKey")] = new JsonObject { ["type"] = "keyword" },
            [FilterField("/id")] = new JsonObject { ["type"] = "keyword" },
            [FilterField("/type")] = new JsonObject { ["type"] = "keyword" }
        };
        foreach (var path in policy.IndexedMetadata.Distinct(StringComparer.Ordinal))
        {
            filterProperties[FilterField(path)] = new JsonObject { ["type"] = "keyword" };
        }

        return new JsonObject
        {
            ["settings"] = new JsonObject { ["index.knn"] = true },
            ["mappings"] = new JsonObject
            {
                ["dynamic"] = false,
                ["properties"] = new JsonObject
                {
                    ["partitionKey"] = new JsonObject { ["type"] = "keyword" },
                    ["id"] = new JsonObject { ["type"] = "keyword" },
                    ["type"] = new JsonObject { ["type"] = "keyword" },
                    ["revision"] = new JsonObject { ["type"] = "integer" },
                    ["vectors"] = new JsonObject { ["dynamic"] = false, ["properties"] = vectorProperties },
                    ["filters"] = new JsonObject { ["dynamic"] = false, ["properties"] = filterProperties }
                }
            }
        };
    }

    private static JsonObject BuildDocument(VyralRecord record, RecordCollectionPolicy policy)
    {
        var vectors = new JsonObject();
        foreach (var vectorPolicy in policy.VectorPolicies)
        {
            if (record.Vectors is null || !record.Vectors.TryGetValue(vectorPolicy.Name, out var vector)) continue;
            if (vector.Values.Length != vectorPolicy.Dimensions)
                throw new InvalidOperationException($"Projection record vector '{vectorPolicy.Name}' does not match its canonical policy dimensions.");
            vectors[VectorField(vectorPolicy.Name)] = new JsonArray(vector.Values.Select(value => JsonValue.Create(value)).ToArray());
        }

        var filters = new JsonObject();
        // All portable top-level identity fields are indexed for tenant/type
        // constraints even when a collection policy omits them.
        filters[FilterField("/partitionKey")] = ScalarKey(record.PartitionKey);
        filters[FilterField("/id")] = ScalarKey(record.Id);
        filters[FilterField("/type")] = ScalarKey(record.Type);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record, JsonOptions));
        foreach (var path in policy.IndexedMetadata.Distinct(StringComparer.Ordinal))
        {
            if (TryGetJsonPointerValue(document.RootElement, path, out var value))
                filters[FilterField(path)] = ScalarKey(value);
        }

        return new JsonObject
        {
            ["partitionKey"] = record.PartitionKey,
            ["id"] = record.Id,
            ["type"] = record.Type,
            ["revision"] = record.Revision,
            ["vectors"] = vectors,
            ["filters"] = filters
        };
    }

    private static JsonNode? BuildFilter(RecordCollectionPolicy policy, QueryEnvelope query)
    {
        var filters = new List<JsonNode>();
        if (query.PartitionKeys is { Count: > 0 })
        {
            filters.Add(new JsonObject
            {
                ["terms"] = new JsonObject
                {
                    ["partitionKey"] = new JsonArray(query.PartitionKeys.Distinct(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray())
                }
            });
        }

        if (query.Filter is not null)
            filters.Add(BuildFilterNode(policy, query.Filter));

        return filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => new JsonObject { ["bool"] = new JsonObject { ["filter"] = new JsonArray(filters.ToArray()) } }
        };
    }

    private static JsonNode BuildFilterNode(RecordCollectionPolicy policy, FilterNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Combine))
        {
            if (node.Children is not { Count: > 0 } || node.Combine is not FilterCombineModes.All and not FilterCombineModes.Any)
                throw new NotSupportedException("OpenSearch projection filters require a non-empty 'all' or 'any' group.");
            var boolean = new JsonObject
            {
                [node.Combine == FilterCombineModes.All ? "filter" : "should"] = new JsonArray(node.Children.Select(child => BuildFilterNode(policy, child)).ToArray())
            };
            if (node.Combine == FilterCombineModes.Any) boolean["minimum_should_match"] = 1;
            return new JsonObject { ["bool"] = boolean };
        }

        if (string.IsNullOrWhiteSpace(node.Path))
            throw new NotSupportedException("OpenSearch projection filters require a path.");
        var field = ProjectionFilterPath(policy, node.Path);
        return FilterValueNormalizer.NormalizeOperator(node.Op) switch
        {
            FilterOps.Eq => new JsonObject { ["term"] = new JsonObject { [field] = ScalarKey(node.Value) } },
            FilterOps.In => new JsonObject { ["terms"] = new JsonObject { [field] = ToScalarArray(node.Value) } },
            FilterOps.Exists => ExistsFilter(field, node.Value),
            FilterOps.Neq => new JsonObject { ["bool"] = new JsonObject { ["must_not"] = new JsonArray(new JsonObject { ["term"] = new JsonObject { [field] = ScalarKey(node.Value) } }) } },
            FilterOps.Contains => WildcardFilter(field, node.Value, "*{0}*"),
            FilterOps.StartsWith => WildcardFilter(field, node.Value, "{0}*"),
            _ => throw new NotSupportedException($"OpenSearch projection filters do not support '{node.Op}'. Use equality, inequality, in, exists, contains, or startsWith.")
        };
    }

    private static JsonNode ExistsFilter(string field, object? value)
    {
        var exists = new JsonObject { ["exists"] = new JsonObject { ["field"] = field } };
        return FilterValueNormalizer.NormalizeExistsValue(value)
            ? exists
            : new JsonObject
            {
                ["bool"] = new JsonObject { ["must_not"] = new JsonArray(exists) }
            };
    }

    private static JsonNode WildcardFilter(string field, object? value, string pattern)
    {
        if (FilterValueNormalizer.NormalizeScalar(value) is not string text)
            throw new NotSupportedException("String projection filters require a string value.");
        return new JsonObject { ["wildcard"] = new JsonObject { [field] = string.Format(CultureInfo.InvariantCulture, pattern, EscapeWildcard("s:" + text)) } };
    }

    private static JsonArray ToScalarArray(object? value)
    {
        var result = new JsonArray();
        foreach (var item in FilterValueNormalizer.NormalizeScalarList(value)) result.Add(ScalarKey(item));
        return result;
    }

    private static string ProjectionFilterPath(RecordCollectionPolicy policy, string path)
    {
        if (path is "/partitionKey" or "/id" or "/type") return FilterPath(path);
        if (!policy.IndexedMetadata.Contains(path, StringComparer.Ordinal))
            throw new NotSupportedException($"Projection filter path '{path}' is not listed in the collection's indexedMetadata policy.");
        return FilterPath(path);
    }

    private static string ProjectionDocumentId(string partitionKey, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partitionKey + "\0" + id))).ToLowerInvariant();

    private static string VectorField(string name) => "v_" + StableHash(name);
    private static string FilterField(string path) => "f_" + StableHash(path);
    private static string VectorPath(string name) => "vectors." + VectorField(name);
    private static string FilterPath(string path) => "filters." + FilterField(path);
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();

    private static string ScalarKey(object? value) => value switch
    {
        null => "z:",
        string text => "s:" + text,
        bool boolean => boolean ? "b:true" : "b:false",
        byte number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        sbyte number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        short number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        ushort number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        int number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        uint number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        long number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        ulong number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        float number => "n:" + number.ToString("R", CultureInfo.InvariantCulture),
        double number => "n:" + number.ToString("R", CultureInfo.InvariantCulture),
        decimal number => "n:" + number.ToString(CultureInfo.InvariantCulture),
        JsonNode node => ScalarKeyFromNode(node),
        _ => throw new NotSupportedException("OpenSearch projection filters support only null, string, bool, and numeric scalar values.")
    };

    private static string ScalarKeyFromNode(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return ScalarKey(text);
            if (value.TryGetValue<bool>(out var boolean)) return ScalarKey(boolean);
            if (value.TryGetValue<long>(out var integer)) return ScalarKey(integer);
            if (value.TryGetValue<decimal>(out var decimalValue)) return ScalarKey(decimalValue);
            if (value.TryGetValue<double>(out var doubleValue)) return ScalarKey(doubleValue);
        }
        throw new NotSupportedException("OpenSearch projection filters support only null, string, bool, and numeric scalar values.");
    }

    private static string ScalarKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => ScalarKey((object?)null),
        JsonValueKind.String => ScalarKey(value.GetString()),
        JsonValueKind.True => ScalarKey(true),
        JsonValueKind.False => ScalarKey(false),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => ScalarKey(integer),
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => ScalarKey(decimalValue),
        JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => ScalarKey(doubleValue),
        // Portable filters never compare compound values, but `exists` must
        // still work for a policy-declared object or array. Preserve only its
        // presence; never serialize compound canonical data into the derived
        // index.
        JsonValueKind.Array or JsonValueKind.Object => "j:",
        _ => throw new NotSupportedException("OpenSearch projection filters support only null, string, bool, and numeric scalar values.")
    };

    private static bool TryGetJsonPointerValue(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (!path.StartsWith("/", StringComparison.Ordinal)) return false;
        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        }
        return true;
    }

    private async Task<RecordCollectionPolicy?> ResolvePolicyAsync(string collection, CancellationToken ct)
    {
        if (_policies.TryGetValue(collection, out var policy)) return policy;
        if (_policyStore is null) return null;
        var resolved = await _policyStore.GetCollectionPolicyAsync(collection, ct);
        if (resolved is not null) _policies[collection] = ClonePolicy(resolved);
        return resolved;
    }

    private static RecordCollectionPolicy ClonePolicy(RecordCollectionPolicy policy) =>
        JsonSerializer.Deserialize<RecordCollectionPolicy>(JsonSerializer.Serialize(policy, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("Could not clone the canonical collection policy.");

    private static string EscapeWildcard(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("?", "\\?", StringComparison.Ordinal);

    private static void ValidatePolicy(RecordCollectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RecordIdentityValidator.ValidateCollectionName(policy.Name);
        if (policy.VectorPolicies.Count == 0)
            throw new InvalidOperationException("An OpenSearch projection requires at least one vector policy.");
        foreach (var vector in policy.VectorPolicies)
        {
            if (vector.Dimensions <= 0 || string.IsNullOrWhiteSpace(vector.Name))
                throw new InvalidOperationException("OpenSearch projection vector policies require a name and positive dimensions.");
            if (vector.DistanceFunction is not DistanceFunctions.Cosine and not DistanceFunctions.DotProduct and not DistanceFunctions.Euclidean)
                throw new InvalidOperationException($"OpenSearch projection distance function '{vector.DistanceFunction}' is not supported.");
        }
    }

    private static void ThrowForFailure(string operation, OpenSearchTransportResponse response)
    {
        // Do not include the response body: it can contain indexed identifiers,
        // query details, or provider diagnostics that do not belong in logs.
        throw new InvalidOperationException($"OpenSearch could not {operation} (HTTP {response.StatusCode}).");
    }
}
