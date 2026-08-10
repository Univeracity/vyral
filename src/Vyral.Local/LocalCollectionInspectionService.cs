using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;

namespace Vyral.Local;

public class LocalCollectionInspectionService : ICollectionInspectionService
{
    private const int MaxAnomalyLimit = 500;
    private const string RagChunkType = "rag.chunk";
    private const string RagManifestType = "rag.manifest";
    private readonly IRecordCollectionStore _recordStore;

    public LocalCollectionInspectionService(IRecordCollectionStore recordStore)
    {
        _recordStore = recordStore;
    }

    public async Task<CollectionInspectionResult> InspectAsync(
        string collection,
        CollectionInspectionRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var policy = await _recordStore.GetCollectionPolicyAsync(collection, ct);
        if (policy is null)
        {
            throw new InvalidOperationException($"Collection '{collection}' does not exist.");
        }

        var records = (await _recordStore.QueryAllRecordsAsync(collection, new QueryEnvelope(), ct)).ToList();
        var vectorInspections = policy.VectorPolicies
            .Select(CreateVectorInspection)
            .ToDictionary(item => item.Field, StringComparer.Ordinal);
        var policyFields = new HashSet<string>(vectorInspections.Keys, StringComparer.Ordinal);
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var partitions = new HashSet<string>(StringComparer.Ordinal);

        var result = new CollectionInspectionResult
        {
            Collection = collection,
            GeneratedAt = DateTime.UtcNow,
            Policy = policy,
            RecordCount = records.Count,
            Vectors = vectorInspections.Values.ToList()
        };

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            partitions.Add(record.PartitionKey);
            Increment(result.TypeCounts, TypeKey(record.Type));
            IncrementEmbeddingCounts(result, record);
            InspectRagShape(result, record, documentIds, policyFields);
            InspectPolicyVectors(result, record, vectorInspections, request);
            InspectExtraVectors(result, record, policyFields, request);
        }

        result.PartitionCount = partitions.Count;
        result.Rag.DocumentCount = documentIds.Count;
        foreach (var vector in result.Vectors)
        {
            vector.RecordCount = records.Count;
            var applicableCount = vector.PresentCount + vector.MissingCount;
            vector.PolicyCoverage = applicableCount == 0 ? 1.0 : vector.PresentCount / (double)applicableCount;
        }

        result.ReturnedAnomalyCount = result.Anomalies.Count;
        SortCounts(result.TypeCounts);
        SortCounts(result.EmbeddingProviderCounts);
        SortCounts(result.EmbeddingModelCounts);
        SortCounts(result.ExtraVectorFieldCounts);
        foreach (var vector in result.Vectors)
        {
            SortCounts(vector.ModelCounts);
            SortCounts(vector.SourceFieldCounts);
        }

        return result;
    }

    private static VectorFieldInspection CreateVectorInspection(VectorFieldPolicy policy)
    {
        return new VectorFieldInspection
        {
            Field = policy.Name,
            Path = policy.Path,
            PolicyDimensions = policy.Dimensions,
            Datatype = policy.Datatype,
            DistanceFunction = policy.DistanceFunction,
            IndexType = policy.IndexType
        };
    }

    private static void InspectRagShape(
        CollectionInspectionResult result,
        VyralRecord record,
        HashSet<string> documentIds,
        HashSet<string> policyFields)
    {
        if (string.Equals(record.Type, RagManifestType, StringComparison.Ordinal))
        {
            result.Rag.ManifestCount++;
            AddDocumentIdToSet(record, documentIds);
            return;
        }

        if (!string.Equals(record.Type, RagChunkType, StringComparison.Ordinal))
        {
            return;
        }

        result.Rag.ChunkCount++;
        if (AddDocumentIdToSet(record, documentIds))
        {
            result.Rag.ChunkRecordsWithDocumentIdCount++;
        }

        var hasPolicyVector = record.Vectors is not null &&
            record.Vectors.Keys.Any(field => policyFields.Contains(field));
        if (hasPolicyVector)
        {
            result.Rag.ChunkRecordsWithVectorCount++;
        }
        else
        {
            result.Rag.ChunkRecordsWithoutVectorCount++;
        }
    }

    private static void InspectPolicyVectors(
        CollectionInspectionResult result,
        VyralRecord record,
        Dictionary<string, VectorFieldInspection> vectorInspections,
        CollectionInspectionRequest request)
    {
        foreach (var (field, inspection) in vectorInspections)
        {
            if (record.Vectors is null || !record.Vectors.TryGetValue(field, out var vector))
            {
                if (!IsVectorBearingRecord(record))
                {
                    inspection.NotApplicableCount++;
                    continue;
                }

                inspection.MissingCount++;
                AddAnomaly(result, request, new CollectionInspectionAnomaly
                {
                    Kind = "missingPolicyVector",
                    Id = record.Id,
                    PartitionKey = record.PartitionKey,
                    Type = EmptyToNull(record.Type),
                    Field = field,
                    Message = $"Record '{record.Id}' is missing policy vector field '{field}'."
                });
                continue;
            }

            inspection.PresentCount++;
            Increment(inspection.ModelCounts, FirstNonBlank(vector.Model, ReadMetadataString(record, "embeddingModel")));
            Increment(inspection.SourceFieldCounts, vector.SourceField);

            if (vector.Values.Length == 0)
            {
                inspection.EmptyCount++;
                AddAnomaly(result, request, new CollectionInspectionAnomaly
                {
                    Kind = "emptyVector",
                    Id = record.Id,
                    PartitionKey = record.PartitionKey,
                    Type = EmptyToNull(record.Type),
                    Field = field,
                    Message = $"Record '{record.Id}' has an empty vector for field '{field}'."
                });
            }

            var actualDimensions = vector.Values.Length;
            if (actualDimensions != inspection.PolicyDimensions || vector.Dimensions != inspection.PolicyDimensions)
            {
                inspection.DimensionMismatchCount++;
                AddAnomaly(result, request, new CollectionInspectionAnomaly
                {
                    Kind = "dimensionMismatch",
                    Id = record.Id,
                    PartitionKey = record.PartitionKey,
                    Type = EmptyToNull(record.Type),
                    Field = field,
                    Message = $"Record '{record.Id}' vector field '{field}' does not match the collection policy dimensions.",
                    Details = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["policyDimensions"] = inspection.PolicyDimensions,
                        ["vectorDimensions"] = vector.Dimensions,
                        ["valueCount"] = actualDimensions
                    }
                });
            }
        }
    }

    private static void InspectExtraVectors(
        CollectionInspectionResult result,
        VyralRecord record,
        HashSet<string> policyFields,
        CollectionInspectionRequest request)
    {
        if (record.Vectors is null)
        {
            return;
        }

        foreach (var (field, _) in record.Vectors)
        {
            if (policyFields.Contains(field))
            {
                continue;
            }

            Increment(result.ExtraVectorFieldCounts, field);
            AddAnomaly(result, request, new CollectionInspectionAnomaly
            {
                Kind = "undeclaredVectorField",
                Id = record.Id,
                PartitionKey = record.PartitionKey,
                Type = EmptyToNull(record.Type),
                Field = field,
                Message = $"Record '{record.Id}' carries vector field '{field}' that is not declared by the collection policy."
            });
        }
    }

    private static bool AddDocumentIdToSet(VyralRecord record, HashSet<string> documentIds)
    {
        var documentId = ReadMetadataString(record, "documentId");
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return false;
        }

        documentIds.Add(documentId);
        return true;
    }

    private static void IncrementEmbeddingCounts(CollectionInspectionResult result, VyralRecord record)
    {
        Increment(result.EmbeddingProviderCounts, ReadMetadataString(record, "embeddingProvider"));
        var models = new HashSet<string>(StringComparer.Ordinal);
        AddIfPresent(models, ReadMetadataString(record, "embeddingModel"));
        if (record.Vectors is null)
        {
            foreach (var model in models)
            {
                Increment(result.EmbeddingModelCounts, model);
            }
            return;
        }

        foreach (var (_, vector) in record.Vectors)
        {
            AddIfPresent(models, vector.Model);
        }

        foreach (var model in models)
        {
            Increment(result.EmbeddingModelCounts, model);
        }
    }

    private static void AddAnomaly(
        CollectionInspectionResult result,
        CollectionInspectionRequest request,
        CollectionInspectionAnomaly anomaly)
    {
        result.AnomalyCount++;
        if (!request.IncludeAnomalies || result.Anomalies.Count >= request.AnomalyLimit)
        {
            return;
        }

        result.Anomalies.Add(anomaly);
    }

    private static bool IsVectorBearingRecord(VyralRecord record)
    {
        return !string.Equals(record.Type, RagManifestType, StringComparison.Ordinal);
    }

    private static string? ReadMetadataString(VyralRecord record, string key)
    {
        var node = record.Metadata?[key];
        if (node is not System.Text.Json.Nodes.JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? EmptyToNull(s) : EmptyToNull(v.ToJsonString());
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string TypeKey(string? type)
    {
        return string.IsNullOrWhiteSpace(type) ? "(none)" : type;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void Increment(Dictionary<string, int> counts, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static void AddIfPresent(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void SortCounts(Dictionary<string, int> counts)
    {
        var ordered = counts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
        counts.Clear();
        foreach (var item in ordered)
        {
            counts[item.Key] = item.Value;
        }
    }

    private static void ValidateRequest(CollectionInspectionRequest request)
    {
        if (request.AnomalyLimit < 0 || request.AnomalyLimit > MaxAnomalyLimit)
        {
            throw new InvalidOperationException($"Collection inspection anomalyLimit must be between 0 and {MaxAnomalyLimit}.");
        }
    }
}
