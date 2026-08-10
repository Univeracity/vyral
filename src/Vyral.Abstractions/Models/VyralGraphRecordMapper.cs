using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vyral.Abstractions.Models;

public static class VyralGraphRecordMapper
{
    public static RecordCollectionPolicy CreateDefaultCollectionPolicy(string collection)
    {
        return new RecordCollectionPolicy
        {
            Name = collection,
            IndexedMetadata = VyralGraphMetadataPaths.DefaultIndexed.ToList()
        };
    }

    public static bool IsGraphCollectionPolicy(RecordCollectionPolicy policy)
    {
        var indexed = new HashSet<string>(policy.IndexedMetadata, StringComparer.Ordinal);
        return VyralGraphMetadataPaths.DefaultIndexed.All(indexed.Contains);
    }

    public static IReadOnlyList<string> GetMissingGraphMetadataIndexes(RecordCollectionPolicy policy)
    {
        var indexed = new HashSet<string>(policy.IndexedMetadata, StringComparer.Ordinal);
        return VyralGraphMetadataPaths.DefaultIndexed
            .Where(path => !indexed.Contains(path))
            .ToList();
    }

    public static List<VyralRecord> ToRecords(VyralGraphEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        NormalizeEnvelope(envelope);
        ValidateEnvelope(envelope);

        var records = new List<VyralRecord>
        {
            CreateEnvelopeRecord(envelope)
        };

        records.AddRange(envelope.Nodes.Select(node => CreateNodeRecord(envelope, node)));
        records.AddRange(envelope.Edges.Select(edge => CreateEdgeRecord(envelope, edge)));
        records.AddRange(envelope.Assertions.Select(assertion => CreateAssertionRecord(envelope, assertion)));
        records.AddRange(envelope.Reviews.Select(review => CreateReviewRecord(envelope, review)));
        records.AddRange(envelope.Projections.Select(projection => CreateProjectionRecord(envelope, projection)));
        return records;
    }

    public static VyralGraphEnvelope FromRecords(IEnumerable<VyralRecord> records, bool includeProjections = true)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ordered = records
            .Where(record => record.Type.StartsWith("graph.", StringComparison.Ordinal))
            .OrderBy(record => record.Type, StringComparer.Ordinal)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        var envelopeRecord = ordered.FirstOrDefault(record => record.Type == VyralGraphRecordTypes.Envelope);
        var envelope = new VyralGraphEnvelope();
        if (envelopeRecord is not null)
        {
            envelope.Schema = ReadContentString(envelopeRecord, "schema") ?? VyralGraphSchemaVersions.RomanGraphV1;
            envelope.Scope = DeserializeContent<VyralGraphScope>(envelopeRecord, "scope") ?? BuildScopeFromMetadata(envelopeRecord);
            envelope.Metadata = DeserializeContent<JsonObject>(envelopeRecord, "metadata");
        }
        else if (ordered.Count > 0)
        {
            envelope.Scope = BuildScopeFromMetadata(ordered[0]);
        }

        foreach (var record in ordered)
        {
            switch (record.Type)
            {
                case VyralGraphRecordTypes.Node:
                    AddIfNotNull(envelope.Nodes, DeserializeContent<VyralGraphNode>(record, "node"));
                    break;
                case VyralGraphRecordTypes.Edge:
                    AddIfNotNull(envelope.Edges, DeserializeContent<VyralGraphEdge>(record, "edge"));
                    break;
                case VyralGraphRecordTypes.Assertion:
                    AddIfNotNull(envelope.Assertions, DeserializeContent<VyralGraphAssertion>(record, "assertion"));
                    break;
                case VyralGraphRecordTypes.Review:
                    AddIfNotNull(envelope.Reviews, DeserializeContent<VyralGraphReviewEvent>(record, "review"));
                    break;
                case VyralGraphRecordTypes.Projection when includeProjections:
                    AddIfNotNull(envelope.Projections, DeserializeContent<VyralGraphProjection>(record, "projection"));
                    break;
            }
        }

        NormalizeEnvelope(envelope);
        return envelope;
    }

    public static string ResolvePartitionKey(VyralGraphScope scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.PartitionKey))
        {
            return scope.PartitionKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(scope.TenantId))
        {
            return $"tenant:{scope.TenantId.Trim()}";
        }

        return $"graph:{(string.IsNullOrWhiteSpace(scope.GraphId) ? "default" : scope.GraphId.Trim())}";
    }

    private static VyralRecord CreateEnvelopeRecord(VyralGraphEnvelope envelope)
    {
        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Envelope,
            "envelope",
            BuildScopeKey(envelope.Scope),
            "envelope",
            null,
            new JsonObject
            {
                ["schema"] = envelope.Schema,
                ["scope"] = JsonSerializer.SerializeToNode(envelope.Scope, VyralGraphJson.Options),
                ["metadata"] = envelope.Metadata?.DeepClone(),
                ["text"] = $"graph envelope {envelope.Scope.GraphId}"
            },
            Enumerable.Empty<VyralGraphSourceSpan>());
    }

    private static VyralRecord CreateNodeRecord(VyralGraphEnvelope envelope, VyralGraphNode node)
    {
        var metadata = new JsonObject
        {
            [VyralGraphMetadataKeys.NodeId] = node.Id,
            [VyralGraphMetadataKeys.NodeType] = node.Type
        };

        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Node,
            VyralGraphSubjectKinds.Node,
            node.Id,
            "node",
            metadata,
            new JsonObject
            {
                ["node"] = JsonSerializer.SerializeToNode(node, VyralGraphJson.Options),
                ["text"] = BuildNodeText(node)
            },
            node.SourceSpans);
    }

    private static VyralRecord CreateEdgeRecord(VyralGraphEnvelope envelope, VyralGraphEdge edge)
    {
        var metadata = new JsonObject
        {
            [VyralGraphMetadataKeys.EdgeId] = edge.Id,
            [VyralGraphMetadataKeys.SourceId] = edge.SourceId,
            [VyralGraphMetadataKeys.TargetId] = edge.TargetId,
            [VyralGraphMetadataKeys.Predicate] = edge.Predicate
        };

        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Edge,
            VyralGraphSubjectKinds.Edge,
            edge.Id,
            "edge",
            metadata,
            new JsonObject
            {
                ["edge"] = JsonSerializer.SerializeToNode(edge, VyralGraphJson.Options),
                ["text"] = BuildEdgeText(edge)
            },
            edge.SourceSpans);
    }

    private static VyralRecord CreateAssertionRecord(VyralGraphEnvelope envelope, VyralGraphAssertion assertion)
    {
        var metadata = new JsonObject
        {
            [VyralGraphMetadataKeys.AssertionId] = assertion.Id,
            [VyralGraphMetadataKeys.SubjectId] = assertion.SubjectId,
            [VyralGraphMetadataKeys.SubjectKind] = assertion.SubjectKind,
            [VyralGraphMetadataKeys.AssertionStatus] = assertion.Status
        };

        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Assertion,
            VyralGraphSubjectKinds.Assertion,
            assertion.Id,
            "assertion",
            metadata,
            new JsonObject
            {
                ["assertion"] = JsonSerializer.SerializeToNode(assertion, VyralGraphJson.Options),
                ["text"] = BuildAssertionText(assertion)
            },
            assertion.SourceSpans);
    }

    private static VyralRecord CreateReviewRecord(VyralGraphEnvelope envelope, VyralGraphReviewEvent review)
    {
        var metadata = new JsonObject
        {
            [VyralGraphMetadataKeys.ReviewId] = review.Id,
            [VyralGraphMetadataKeys.SubjectId] = review.SubjectId,
            [VyralGraphMetadataKeys.SubjectKind] = review.SubjectKind,
            [VyralGraphMetadataKeys.ReviewStatus] = review.Status
        };

        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Review,
            "review",
            review.Id,
            "review",
            metadata,
            new JsonObject
            {
                ["review"] = JsonSerializer.SerializeToNode(review, VyralGraphJson.Options),
                ["text"] = BuildReviewText(review)
            },
            Enumerable.Empty<VyralGraphSourceSpan>());
    }

    private static VyralRecord CreateProjectionRecord(VyralGraphEnvelope envelope, VyralGraphProjection projection)
    {
        var metadata = new JsonObject
        {
            [VyralGraphMetadataKeys.ProjectionId] = projection.Id
        };

        return CreateRecord(
            envelope,
            VyralGraphRecordTypes.Projection,
            VyralGraphSubjectKinds.Projection,
            projection.Id,
            "projection",
            metadata,
            new JsonObject
            {
                ["projection"] = JsonSerializer.SerializeToNode(projection, VyralGraphJson.Options),
                ["text"] = BuildProjectionText(projection)
            },
            Enumerable.Empty<VyralGraphSourceSpan>());
    }

    private static VyralRecord CreateRecord(
        VyralGraphEnvelope envelope,
        string type,
        string kind,
        string subjectId,
        string contentKind,
        JsonObject? metadata,
        JsonObject content,
        IEnumerable<VyralGraphSourceSpan> sourceSpans)
    {
        var now = DateTime.UtcNow;
        var scope = envelope.Scope;
        var partitionKey = ResolvePartitionKey(scope);
        var mergedMetadata = BuildScopeMetadata(scope, kind, subjectId);
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                mergedMetadata[key] = value?.DeepClone();
            }
        }

        content["kind"] = contentKind;
        content["scope"] = JsonSerializer.SerializeToNode(scope, VyralGraphJson.Options);

        return new VyralRecord
        {
            Id = BuildRecordId(type, subjectId),
            PartitionKey = partitionKey,
            Type = type,
            SchemaVersion = envelope.Schema,
            Metadata = mergedMetadata,
            Content = content,
            Sources = BuildSourceReferences(sourceSpans).ToList(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static JsonObject BuildScopeMetadata(VyralGraphScope scope, string graphKind, string subjectId)
    {
        return new JsonObject
        {
            [VyralGraphMetadataKeys.GraphKind] = graphKind,
            [VyralGraphMetadataKeys.GraphId] = scope.GraphId,
            [VyralGraphMetadataKeys.Namespace] = scope.Namespace,
            [VyralGraphMetadataKeys.ScopeCollection] = scope.Collection,
            [VyralGraphMetadataKeys.TenantId] = scope.TenantId,
            [VyralGraphMetadataKeys.GraphPartitionKey] = ResolvePartitionKey(scope),
            [VyralGraphMetadataKeys.SubjectId] = subjectId
        };
    }

    private static IEnumerable<VyralSourceReference> BuildSourceReferences(IEnumerable<VyralGraphSourceSpan> spans)
    {
        foreach (var span in spans)
        {
            var extensions = new Dictionary<string, JsonElement>
            {
                ["unit"] = JsonSerializer.SerializeToElement(span.Unit, VyralGraphJson.Options)
            };
            if (!string.IsNullOrWhiteSpace(span.TextHash))
            {
                extensions["textHash"] = JsonSerializer.SerializeToElement(span.TextHash, VyralGraphJson.Options);
            }

            yield return new VyralSourceReference
            {
                Id = string.IsNullOrWhiteSpace(span.SourceRef) ? "graph-source" : span.SourceRef,
                Kind = "graphSourceSpan",
                Uri = string.IsNullOrWhiteSpace(span.SourceRef) ? "graph://source" : span.SourceRef,
                Label = span.Locator,
                Span = new VyralSourceSpan
                {
                    CharStart = span.CharStart,
                    CharEnd = span.CharEnd,
                    Anchor = span.Locator,
                    Extensions = extensions
                }
            };
        }
    }

    private static VyralGraphScope BuildScopeFromMetadata(VyralRecord record)
    {
        return new VyralGraphScope
        {
            GraphId = ReadMetadataString(record, VyralGraphMetadataKeys.GraphId) ?? "default",
            Namespace = ReadMetadataString(record, VyralGraphMetadataKeys.Namespace) ?? "default",
            Collection = ReadMetadataString(record, VyralGraphMetadataKeys.ScopeCollection) ?? "default",
            TenantId = ReadMetadataString(record, VyralGraphMetadataKeys.TenantId) ?? string.Empty,
            PartitionKey = ReadMetadataString(record, VyralGraphMetadataKeys.GraphPartitionKey) ?? record.PartitionKey
        };
    }

    private static string? ReadMetadataString(VyralRecord record, string key)
    {
        var value = record.Metadata?[key];
        return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString();
    }

    private static string? ReadContentString(VyralRecord record, string key)
    {
        var value = record.Content?[key];
        return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static T? DeserializeContent<T>(VyralRecord record, string key)
    {
        var node = record.Content?[key];
        return node is null ? default : node.Deserialize<T>(VyralGraphJson.Options);
    }

    private static void AddIfNotNull<T>(ICollection<T> values, T? value)
        where T : class
    {
        if (value is not null)
        {
            values.Add(value);
        }
    }

    private static string BuildRecordId(string recordType, string subjectId)
    {
        var kind = recordType["graph.".Length..];
        var encoded = EncodeId(subjectId);
        if (encoded.Length > 900)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(subjectId));
            encoded = "sha256-" + Convert.ToHexString(bytes).ToLowerInvariant();
        }

        return $"g:{kind}:{encoded}";
    }

    private static string BuildScopeKey(VyralGraphScope scope)
    {
        return string.Join("|", new[]
        {
            scope.GraphId,
            scope.Namespace,
            scope.Collection,
            scope.TenantId,
            ResolvePartitionKey(scope)
        });
    }

    private static string EncodeId(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(value) ? "default" : value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void NormalizeEnvelope(VyralGraphEnvelope envelope)
    {
        envelope.Schema = string.IsNullOrWhiteSpace(envelope.Schema) ? VyralGraphSchemaVersions.RomanGraphV1 : envelope.Schema.Trim();
        envelope.Scope ??= new VyralGraphScope();
        envelope.Scope.GraphId = string.IsNullOrWhiteSpace(envelope.Scope.GraphId) ? "default" : envelope.Scope.GraphId.Trim();
        envelope.Scope.Namespace = string.IsNullOrWhiteSpace(envelope.Scope.Namespace) ? "default" : envelope.Scope.Namespace.Trim();
        envelope.Scope.Collection = string.IsNullOrWhiteSpace(envelope.Scope.Collection) ? "default" : envelope.Scope.Collection.Trim();
        envelope.Scope.TenantId = envelope.Scope.TenantId?.Trim() ?? string.Empty;
        envelope.Scope.PartitionKey = envelope.Scope.PartitionKey?.Trim() ?? string.Empty;
        envelope.Nodes ??= new List<VyralGraphNode>();
        envelope.Edges ??= new List<VyralGraphEdge>();
        envelope.Assertions ??= new List<VyralGraphAssertion>();
        envelope.Reviews ??= new List<VyralGraphReviewEvent>();
        envelope.Projections ??= new List<VyralGraphProjection>();
    }

    private static void ValidateEnvelope(VyralGraphEnvelope envelope)
    {
        var totalRecords = 1 + envelope.Nodes.Count + envelope.Edges.Count + envelope.Assertions.Count + envelope.Reviews.Count + envelope.Projections.Count;
        if (totalRecords > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph envelope supports at most {VyralGraphCollectionLimits.MaxRecords} collection records.");
        }

        ValidateDistinct(envelope.Nodes.Select(node => node.Id), "node");
        ValidateDistinct(envelope.Edges.Select(edge => edge.Id), "edge");
        ValidateDistinct(envelope.Assertions.Select(assertion => assertion.Id), "assertion");
        ValidateDistinct(envelope.Reviews.Select(review => review.Id), "review");
        ValidateDistinct(envelope.Projections.Select(projection => projection.Id), "projection");
    }

    private static void ValidateDistinct(IEnumerable<string> ids, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"Graph {kind} id is required.");
            }

            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Graph {kind} id '{id}' appears more than once in the envelope.");
            }
        }
    }

    private static string BuildNodeText(VyralGraphNode node)
        => string.Join(" ", new[] { node.Id, node.Type, node.Label }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildEdgeText(VyralGraphEdge edge)
        => string.Join(" ", new[] { edge.Id, edge.SourceId, edge.Predicate, edge.TargetId, edge.Label }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildAssertionText(VyralGraphAssertion assertion)
        => string.Join(" ", new[] { assertion.Id, assertion.SubjectKind, assertion.SubjectId, assertion.Status, assertion.Method, assertion.Actor }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildReviewText(VyralGraphReviewEvent review)
        => string.Join(" ", new[] { review.Id, review.SubjectKind, review.SubjectId, review.Status, review.Reviewer, review.Notes }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string BuildProjectionText(VyralGraphProjection projection)
        => string.Join(" ", new[] { projection.Id, projection.Profile?.Id }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
