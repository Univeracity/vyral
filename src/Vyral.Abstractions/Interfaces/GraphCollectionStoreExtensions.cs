using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vyral.Abstractions.Models;

namespace Vyral.Abstractions.Interfaces;

public static class GraphCollectionStoreExtensions
{
    private const int MaxGraphInspectionAnomalyLimit = 500;
    private const string MissingCountKey = "(missing)";

    public static async Task<VyralGraphCollectionImportResult> ImportGraphEnvelopeAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphCollectionImportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        RecordIdentityValidator.ValidateCollectionName(collection);

        var preflight = await store.PreflightGraphImportAsync(collection, request, ct);
        if (!preflight.ReadyToImport)
        {
            var errors = preflight.Errors.Count == 0
                ? "Graph import preflight failed."
                : string.Join(" ", preflight.Errors);
            throw new InvalidOperationException(errors);
        }

        var envelope = request.Envelope ?? throw new InvalidOperationException("Graph import request must include an envelope.");
        var records = VyralGraphRecordMapper.ToRecords(envelope);
        if (records.Count > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph import supports at most {VyralGraphCollectionLimits.MaxRecords} collection records.");
        }

        var policyStatus = await EnsureGraphCollectionPolicyAsync(store, collection, request, ct);
        var upsert = await store.UpsertRecordsAsync(collection, new RecordBatchUpsertRequest
        {
            Records = records,
            ContinueOnError = request.ContinueOnError
        }, ct);

        return new VyralGraphCollectionImportResult
        {
            Collection = collection,
            GraphId = envelope.Scope.GraphId,
            PartitionKey = VyralGraphRecordMapper.ResolvePartitionKey(envelope.Scope),
            PolicyStatus = policyStatus,
            NodeCount = envelope.Nodes.Count,
            EdgeCount = envelope.Edges.Count,
            AssertionCount = envelope.Assertions.Count,
            ReviewCount = envelope.Reviews.Count,
            ProjectionCount = envelope.Projections.Count,
            RecordCount = records.Count,
            Records = upsert
        };
    }

    public static async Task<VyralGraphCollectionImportPreflightResult> PreflightGraphImportAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphCollectionImportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        RecordIdentityValidator.ValidateCollectionName(collection);

        var result = new VyralGraphCollectionImportPreflightResult
        {
            Collection = collection,
            GeneratedAt = DateTime.UtcNow,
            CreateCollectionIfMissing = request.CreateCollectionIfMissing,
            ReplaceExisting = request.ReplaceExisting,
            AllowNonGraphPolicy = request.AllowNonGraphPolicy
        };

        var envelope = request.Envelope;
        if (envelope is null)
        {
            result.Errors.Add("Graph import request must include an envelope.");
            FinalizePreflight(result);
            return result;
        }

        envelope.Scope ??= new VyralGraphScope();
        result.GraphId = envelope.Scope.GraphId;
        result.Namespace = envelope.Scope.Namespace;
        result.TenantId = envelope.Scope.TenantId;
        result.PartitionKey = VyralGraphRecordMapper.ResolvePartitionKey(envelope.Scope);
        result.NodeCount = envelope.Nodes?.Count ?? 0;
        result.EdgeCount = envelope.Edges?.Count ?? 0;
        result.AssertionCount = envelope.Assertions?.Count ?? 0;
        result.ReviewCount = envelope.Reviews?.Count ?? 0;
        result.ProjectionCount = envelope.Projections?.Count ?? 0;

        try
        {
            var records = VyralGraphRecordMapper.ToRecords(envelope);
            result.RecordCount = records.Count;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            result.Errors.Add(ex.Message);
        }

        if (result.RecordCount > result.MaxRecords)
        {
            result.Errors.Add($"Graph import supports at most {VyralGraphCollectionLimits.MaxRecords} collection records.");
        }

        var existing = await store.GetCollectionPolicyAsync(collection, ct);
        result.CollectionExists = existing is not null;
        if (existing is null)
        {
            if (!request.CreateCollectionIfMissing)
            {
                result.Errors.Add($"Collection '{collection}' does not exist and createCollectionIfMissing is false.");
            }
            else
            {
                result.WouldCreateCollection = true;
                result.CollectionPolicyStatus = VyralGraphImportPolicyStatuses.Created;
            }

            FinalizePreflight(result);
            return result;
        }

        if (request.ReplaceExisting)
        {
            result.WouldReplaceCollection = true;
            result.CollectionPolicyStatus = VyralGraphImportPolicyStatuses.Replaced;
            result.Warnings.Add("replaceExisting will delete and recreate the target collection before importing graph records.");
            FinalizePreflight(result);
            return result;
        }

        if (VyralGraphRecordMapper.IsGraphCollectionPolicy(existing))
        {
            result.CollectionPolicyStatus = VyralGraphImportPolicyStatuses.ExistingGraphPolicy;
            FinalizePreflight(result);
            return result;
        }

        if (request.AllowNonGraphPolicy)
        {
            result.WouldAllowNonGraphPolicy = true;
            result.CollectionPolicyStatus = VyralGraphImportPolicyStatuses.ExistingNonGraphPolicyAllowed;
            result.Warnings.Add("Target collection does not have the default graph metadata indexes; traversal and inspection may be slower or provider-specific.");
            FinalizePreflight(result);
            return result;
        }

        var missing = VyralGraphRecordMapper.GetMissingGraphMetadataIndexes(existing);
        result.Errors.Add($"Collection '{collection}' is missing graph metadata indexes: {string.Join(", ", missing)}. Import into a graph collection, set replaceExisting to true, or set allowNonGraphPolicy to true for local-only experimentation.");
        FinalizePreflight(result);
        return result;
    }

    public static async Task<VyralGraphCollectionExportResult?> ExportGraphEnvelopeAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphCollectionExportRequest? request = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RecordIdentityValidator.ValidateCollectionName(collection);
        request ??= new VyralGraphCollectionExportRequest();
        ValidateExportRequest(request);

        var export = await store.ExportCollectionAsync(collection, new CollectionExportRequest
        {
            Query = BuildGraphExportQuery(request),
            MaxRecords = request.MaxRecords ?? VyralGraphCollectionLimits.MaxRecords,
            FailOnLimitExceeded = request.FailOnLimitExceeded
        }, ct);
        if (export is null)
        {
            return null;
        }

        return new VyralGraphCollectionExportResult
        {
            Collection = collection,
            Envelope = VyralGraphRecordMapper.FromRecords(export.Records, request.IncludeProjections),
            RecordCount = export.RecordCount ?? export.Records.Count,
            Truncated = export.Truncated,
            ContinuationToken = export.ContinuationToken,
            ExportedAt = export.ExportedAt
        };
    }

    public static QueryEnvelope BuildGraphExportQuery(VyralGraphCollectionExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var recordTypes = request.IncludeProjections
            ? VyralGraphRecordTypes.All
            : VyralGraphRecordTypes.All.Where(type => type != VyralGraphRecordTypes.Projection).ToList();
        var filters = new List<FilterNode>
        {
            FilterNode.In("/type", recordTypes.Cast<object?>())
        };

        AddOptionalFilter(filters, VyralGraphMetadataPaths.GraphId, request.GraphId);
        AddOptionalFilter(filters, VyralGraphMetadataPaths.Namespace, request.Namespace);
        AddOptionalFilter(filters, VyralGraphMetadataPaths.TenantId, request.TenantId);
        AddOptionalFilter(filters, VyralGraphMetadataPaths.GraphPartitionKey, request.PartitionKey);

        return new QueryEnvelope
        {
            PartitionKeys = string.IsNullOrWhiteSpace(request.PartitionKey)
                ? null
                : new List<string> { request.PartitionKey.Trim() },
            Filter = filters.Count == 1 ? filters[0] : FilterNode.All(filters.ToArray()),
            OrderBy = new List<OrderExpression>
            {
                new() { Path = "/type", Direction = SortDirections.Asc },
                new() { Path = "/id", Direction = SortDirections.Asc }
            }
        };
    }

    public static async Task<VyralGraphTraversalResult?> TraverseGraphAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphTraversalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        RecordIdentityValidator.ValidateCollectionName(collection);
        ValidateTraversalRequest(request);

        var requestedMaxRecords = request.MaxRecords ?? VyralGraphCollectionLimits.MaxRecords;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exportStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var export = await store.ExportGraphEnvelopeAsync(collection, new VyralGraphCollectionExportRequest
        {
            GraphId = request.GraphId,
            Namespace = request.Namespace,
            TenantId = request.TenantId,
            PartitionKey = request.PartitionKey,
            IncludeProjections = false,
            MaxRecords = requestedMaxRecords,
            FailOnLimitExceeded = false
        }, ct);
        exportStopwatch.Stop();
        if (export is null)
        {
            return null;
        }

        if (export.Truncated && !request.AllowPartialGraph)
        {
            throw new VyralGraphTraversalTruncatedException(
                "Graph traversal source export was truncated. Increase maxRecords or set allowPartialGraph to true to traverse a partial graph intentionally.",
                collection,
                request.GraphId,
                request.Namespace,
                request.TenantId,
                request.PartitionKey,
                requestedMaxRecords,
                export.RecordCount,
                export.ContinuationToken);
        }

        var traversalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var projection = TraverseEnvelope(export.Envelope, request.StartNodeIds, request.Profile, export);
        traversalStopwatch.Stop();
        totalStopwatch.Stop();
        AddTraversalExecutionDiagnostics(
            projection,
            request,
            requestedMaxRecords,
            export,
            exportStopwatch.Elapsed,
            traversalStopwatch.Elapsed,
            totalStopwatch.Elapsed);

        return new VyralGraphTraversalResult
        {
            Collection = collection,
            GraphId = export.Envelope.Scope.GraphId,
            Projection = projection,
            NodeCount = projection.Nodes.Count,
            EdgeCount = projection.Edges.Count,
            SourceRecordCount = export.RecordCount,
            SourceTruncated = export.Truncated,
            RequestedMaxRecords = requestedMaxRecords,
            ExportedRecordCount = export.RecordCount,
            EstimatedRequiredRecordCount = export.Truncated ? export.RecordCount + 1 : export.RecordCount,
            SourceContinuationToken = export.ContinuationToken
        };
    }

    public static async Task<VyralGraphDoctorResult?> DoctorGraphAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphDoctorRequest? request = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RecordIdentityValidator.ValidateCollectionName(collection);
        request ??= new VyralGraphDoctorRequest();
        ValidateDoctorRequest(request);

        var inspection = await store.InspectGraphAsync(collection, new VyralGraphCollectionInspectionRequest
        {
            GraphId = request.GraphId,
            Namespace = request.Namespace,
            TenantId = request.TenantId,
            PartitionKey = request.PartitionKey,
            MaxRecords = request.MaxGraphRecords,
            AllowPartialGraph = request.AllowPartialGraph,
            IncludeAnomalies = request.IncludeAnomalies,
            AnomalyLimit = request.AnomalyLimit
        }, ct);
        if (inspection is null)
        {
            return null;
        }

        var result = new VyralGraphDoctorResult
        {
            Collection = collection,
            GeneratedAt = DateTime.UtcNow,
            GraphReady = inspection.TraversalReady,
            GraphRecordCount = inspection.RecordCount,
            GraphNodeCount = inspection.NodeCount,
            GraphEdgeCount = inspection.EdgeCount,
            GraphTruncated = inspection.Truncated,
            Inspection = inspection
        };

        if (!string.IsNullOrWhiteSpace(request.TargetCollection))
        {
            result.SeedCoverage = await BuildSeedCoverageAsync(store, collection, request, ct);
        }

        ApplyDoctorStatus(result);
        return result;
    }

    public static async Task<VyralGraphCollectionInspectionResult?> InspectGraphAsync(
        this IRecordCollectionStore store,
        string collection,
        VyralGraphCollectionInspectionRequest? request = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        RecordIdentityValidator.ValidateCollectionName(collection);
        request ??= new VyralGraphCollectionInspectionRequest();
        ValidateInspectionRequest(request);

        var exportRequest = new VyralGraphCollectionExportRequest
        {
            GraphId = request.GraphId,
            Namespace = request.Namespace,
            TenantId = request.TenantId,
            PartitionKey = request.PartitionKey,
            IncludeProjections = true,
            MaxRecords = request.MaxRecords ?? VyralGraphCollectionLimits.MaxRecords,
            FailOnLimitExceeded = false
        };
        var export = await store.ExportCollectionAsync(collection, new CollectionExportRequest
        {
            Query = BuildGraphExportQuery(exportRequest),
            MaxRecords = exportRequest.MaxRecords,
            FailOnLimitExceeded = false
        }, ct);
        if (export is null)
        {
            return null;
        }

        var envelope = VyralGraphRecordMapper.FromRecords(export.Records);
        var result = new VyralGraphCollectionInspectionResult
        {
            Collection = collection,
            GeneratedAt = DateTime.UtcNow,
            GraphId = FirstNonBlank(request.GraphId, envelope.Scope.GraphId),
            Namespace = FirstNonBlank(request.Namespace, envelope.Scope.Namespace),
            TenantId = FirstNonBlank(request.TenantId, envelope.Scope.TenantId),
            PartitionKey = FirstNonBlank(request.PartitionKey, envelope.Scope.PartitionKey),
            RecordCount = export.RecordCount ?? export.Records.Count,
            Truncated = export.Truncated,
            ContinuationToken = export.ContinuationToken,
            NodeCount = envelope.Nodes.Count,
            EdgeCount = envelope.Edges.Count,
            AssertionCount = envelope.Assertions.Count,
            ReviewCount = envelope.Reviews.Count,
            ProjectionCount = envelope.Projections.Count
        };

        InspectRecordScope(export.Records, result);
        InspectEnvelope(envelope, request, result);
        AddInspectionWarnings(request, result);
        SortInspectionCounts(result);
        result.ReturnedAnomalyCount = result.Anomalies.Count;
        result.WarningCount = result.Warnings.Count;
        result.TraversalReady = IsTraversalReady(request, result);
        return result;
    }

    private static void AddTraversalExecutionDiagnostics(
        VyralGraphProjection projection,
        VyralGraphTraversalRequest request,
        int requestedMaxRecords,
        VyralGraphCollectionExportResult sourceExport,
        TimeSpan sourceExportDuration,
        TimeSpan traversalDuration,
        TimeSpan duration)
    {
        projection.Diagnostics ??= new JsonObject();
        projection.Diagnostics["sourceScanMode"] = "filtered_graph_export";
        projection.Diagnostics["requestedMaxRecords"] = requestedMaxRecords;
        projection.Diagnostics["exportedRecordCount"] = sourceExport.RecordCount;
        projection.Diagnostics["estimatedRequiredRecordCount"] = sourceExport.Truncated ? sourceExport.RecordCount + 1 : sourceExport.RecordCount;
        projection.Diagnostics["sourceContinuationToken"] = sourceExport.ContinuationToken ?? string.Empty;
        projection.Diagnostics["allowPartialGraph"] = request.AllowPartialGraph;
        projection.Diagnostics["graphIdFilterApplied"] = !string.IsNullOrWhiteSpace(request.GraphId);
        projection.Diagnostics["namespaceFilterApplied"] = !string.IsNullOrWhiteSpace(request.Namespace);
        projection.Diagnostics["tenantFilterApplied"] = !string.IsNullOrWhiteSpace(request.TenantId);
        projection.Diagnostics["partitionFilterApplied"] = !string.IsNullOrWhiteSpace(request.PartitionKey);
        projection.Diagnostics["sourceExportDurationMs"] = Milliseconds(sourceExportDuration);
        projection.Diagnostics["traversalDurationMs"] = Milliseconds(traversalDuration);
        projection.Diagnostics["durationMs"] = Milliseconds(duration);
    }

    private static double Milliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 3);

    private static void FinalizePreflight(VyralGraphCollectionImportPreflightResult result)
    {
        result.WarningCount = result.Warnings.Count;
        result.ErrorCount = result.Errors.Count;
        result.Valid = result.ErrorCount == 0;
        result.ReadyToImport = result.Valid;
    }

    private static async Task<VyralGraphSeedCoverage?> BuildSeedCoverageAsync(
        IRecordCollectionStore store,
        string graphCollection,
        VyralGraphDoctorRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetCollection))
        {
            return null;
        }

        var targetCollection = request.TargetCollection.Trim();
        var coverage = new VyralGraphSeedCoverage
        {
            TargetCollection = targetCollection,
            SeedJsonPointers = request.SeedJsonPointers.ToList()
        };

        var graphExport = await store.ExportGraphEnvelopeAsync(graphCollection, new VyralGraphCollectionExportRequest
        {
            GraphId = request.GraphId,
            Namespace = request.Namespace,
            TenantId = request.TenantId,
            PartitionKey = request.PartitionKey,
            IncludeProjections = false,
            MaxRecords = request.MaxGraphRecords ?? VyralGraphCollectionLimits.MaxRecords,
            FailOnLimitExceeded = false
        }, ct);
        var nodeIds = graphExport?.Envelope.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var targetQuery = new QueryEnvelope();
        if (request.TargetPartitionKeys.Count > 0)
        {
            targetQuery.PartitionKeys = request.TargetPartitionKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var targetExport = await store.ExportCollectionAsync(targetCollection, new CollectionExportRequest
        {
            Query = targetQuery,
            MaxRecords = request.MaxTargetRecords,
            FailOnLimitExceeded = false
        }, ct);
        if (targetExport is null)
        {
            return coverage;
        }

        coverage.TargetRecordCount = targetExport.RecordCount ?? targetExport.Records.Count;
        coverage.TargetTruncated = targetExport.Truncated;

        var uniqueSeeds = new HashSet<string>(StringComparer.Ordinal);
        var recordsWithSeeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in targetExport.Records)
        {
            foreach (var pointer in request.SeedJsonPointers)
            {
                var values = ResolveSeedValues(record, pointer).ToList();
                if (values.Count == 0)
                {
                    continue;
                }

                recordsWithSeeds.Add(record.Id);
                foreach (var value in values)
                {
                    coverage.SeedValueCount++;
                    uniqueSeeds.Add(value);
                }
            }
        }

        coverage.RecordsWithSeedMetadataCount = recordsWithSeeds.Count;
        coverage.UniqueSeedValueCount = uniqueSeeds.Count;
        coverage.ResolvedSeedNodeIds = uniqueSeeds
            .Where(nodeIds.Contains)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        coverage.UnresolvedSeedNodeIds = uniqueSeeds
            .Where(id => !nodeIds.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        coverage.ResolvedSeedNodeCount = uniqueSeeds.Count(nodeIds.Contains);
        coverage.UnresolvedSeedNodeCount = Math.Max(0, uniqueSeeds.Count - coverage.ResolvedSeedNodeCount);
        coverage.SeedCoverage = Coverage(coverage.RecordsWithSeedMetadataCount, coverage.TargetRecordCount);
        coverage.ResolvedSeedCoverage = Coverage(coverage.ResolvedSeedNodeCount, uniqueSeeds.Count);
        return coverage;
    }

    private static void ApplyDoctorStatus(VyralGraphDoctorResult result)
    {
        if (result.Inspection is null)
        {
            result.Status = "graph_missing";
            result.FailureMode = "graph_missing";
            result.RecommendedActions.Add("Create or import the graph collection before enabling GraphRAG.");
            return;
        }

        if (result.GraphTruncated)
        {
            result.Status = "budget_truncated";
            result.FailureMode = "budget_truncated";
            result.RecommendedActions.Add("Increase maxGraphRecords or allow an intentional partial graph inspection.");
        }
        else if (!result.GraphReady)
        {
            result.Status = "graph_not_ready";
            result.FailureMode = "graph_anomalies";
            result.RecommendedActions.Add("Inspect graph warnings and anomalies before enabling GraphRAG.");
        }
        else if (result.SeedCoverage is { TargetRecordCount: > 0, UniqueSeedValueCount: 0 })
        {
            result.Status = "no_seeds";
            result.FailureMode = "no_seeds";
            result.RecommendedActions.Add("Stamp target records with graph seed metadata or configure seedJsonPointers.");
        }
        else if (result.SeedCoverage is { UniqueSeedValueCount: > 0, ResolvedSeedNodeCount: 0 })
        {
            result.Status = "seed_node_not_found";
            result.FailureMode = "seed_node_not_found";
            result.RecommendedActions.Add("Align target record seed ids with graph node ids.");
        }
        else if (result.GraphNodeCount > 0 && result.GraphEdgeCount == 0)
        {
            result.Status = "traversal_empty";
            result.FailureMode = "traversal_empty";
            result.RecommendedActions.Add("Add graph edges or broaden traversal predicates/depth before relying on expansion.");
        }
        else
        {
            result.Status = "ready";
        }

        if (result.SeedCoverage?.TargetTruncated == true)
        {
            if (!result.RecommendedActions.Contains("Increase maxTargetRecords to inspect full seed coverage.", StringComparer.Ordinal))
            {
                result.RecommendedActions.Add("Increase maxTargetRecords to inspect full seed coverage.");
            }
        }

        result.Ready = string.Equals(result.Status, "ready", StringComparison.Ordinal);
    }

    private static async Task<string> EnsureGraphCollectionPolicyAsync(
        IRecordCollectionStore store,
        string collection,
        VyralGraphCollectionImportRequest request,
        CancellationToken ct)
    {
        var existing = await store.GetCollectionPolicyAsync(collection, ct);
        if (existing is null)
        {
            if (!request.CreateCollectionIfMissing)
            {
                throw new InvalidOperationException($"Collection '{collection}' does not exist.");
            }

            await store.CreateCollectionAsync(VyralGraphRecordMapper.CreateDefaultCollectionPolicy(collection), ct);
            return VyralGraphImportPolicyStatuses.Created;
        }

        if (request.ReplaceExisting)
        {
            await store.DeleteCollectionAsync(collection, ct);
            await store.CreateCollectionAsync(VyralGraphRecordMapper.CreateDefaultCollectionPolicy(collection), ct);
            return VyralGraphImportPolicyStatuses.Replaced;
        }

        if (VyralGraphRecordMapper.IsGraphCollectionPolicy(existing))
        {
            return VyralGraphImportPolicyStatuses.ExistingGraphPolicy;
        }

        if (request.AllowNonGraphPolicy)
        {
            return VyralGraphImportPolicyStatuses.ExistingNonGraphPolicyAllowed;
        }

        var missing = VyralGraphRecordMapper.GetMissingGraphMetadataIndexes(existing);
        throw new InvalidOperationException($"Collection '{collection}' is missing graph metadata indexes: {string.Join(", ", missing)}. Import into a graph collection, set replaceExisting to true, or set allowNonGraphPolicy to true for local-only experimentation.");
    }

    private static VyralGraphProjection TraverseEnvelope(
        VyralGraphEnvelope envelope,
        IReadOnlyList<string> requestedStartNodeIds,
        VyralGraphTraversalProfile requestedProfile,
        VyralGraphCollectionExportResult sourceExport)
    {
        var profile = CloneProfile(requestedProfile);
        var nodesById = envelope.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = envelope.Edges
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var incoming = envelope.Edges
            .GroupBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var assertionIdsBySubject = envelope.Assertions
            .GroupBy(assertion => assertion.SubjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(assertion => assertion.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var assertionsById = envelope.Assertions.ToDictionary(assertion => assertion.Id, StringComparer.Ordinal);
        var reviewStatusesByAssertionId = envelope.Reviews
            .GroupBy(review => review.SubjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(review => review.Status).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        var includedNodes = new Dictionary<string, VyralGraphNode>(StringComparer.Ordinal);
        var includedEdges = new Dictionary<string, VyralGraphEdge>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<(string NodeId, int Depth)>();
        var missingStartNodeIds = new List<string>();
        var filtered = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["nodeType"] = 0,
            ["predicate"] = 0,
            ["sourceGrounding"] = 0,
            ["score"] = 0,
            ["assertionStatus"] = 0,
            ["reviewStatus"] = 0,
            ["nodeLimit"] = 0,
            ["edgeLimit"] = 0
        };
        var pathExplanations = new JsonObject();
        var edgeTruncated = false;
        var nodeLimitReached = false;

        foreach (var startNodeId in requestedStartNodeIds.Distinct(StringComparer.Ordinal))
        {
            if (!nodesById.TryGetValue(startNodeId, out var startNode))
            {
                missingStartNodeIds.Add(startNodeId);
                continue;
            }

            visited.Add(startNodeId);
            if (profile.IncludeStart && ShouldIncludeNode(startNode, profile, assertionIdsBySubject, assertionsById, reviewStatusesByAssertionId, filtered))
            {
                includedNodes[startNode.Id] = startNode;
                if (profile.IncludePathExplanations)
                {
                    pathExplanations[startNode.Id] = new JsonArray();
                }
            }

            frontier.Enqueue((startNodeId, 0));
        }

        while (frontier.Count > 0)
        {
            var (nodeId, depth) = frontier.Dequeue();
            if (depth >= profile.MaxDepth)
            {
                continue;
            }

            foreach (var edge in GetCandidateEdges(nodeId, profile.Direction, outgoing, incoming))
            {
                if (includedEdges.ContainsKey(edge.Id))
                {
                    continue;
                }

                if (!ShouldIncludeEdge(edge, profile, assertionIdsBySubject, assertionsById, reviewStatusesByAssertionId, filtered))
                {
                    continue;
                }

                if (includedEdges.Count >= profile.EdgeLimit)
                {
                    edgeTruncated = true;
                    filtered["edgeLimit"]++;
                    break;
                }

                var nextNodeId = ResolveNextNodeId(nodeId, edge, profile.Direction);
                if (nextNodeId is null || !nodesById.TryGetValue(nextNodeId, out var nextNode))
                {
                    continue;
                }

                if (!ShouldIncludeNode(nextNode, profile, assertionIdsBySubject, assertionsById, reviewStatusesByAssertionId, filtered))
                {
                    continue;
                }

                if (!includedNodes.ContainsKey(nextNode.Id) && includedNodes.Count >= profile.Limit)
                {
                    nodeLimitReached = true;
                    filtered["nodeLimit"]++;
                    continue;
                }

                includedEdges[edge.Id] = edge;
                includedNodes[nextNode.Id] = nextNode;
                if (profile.IncludePathExplanations)
                {
                    AddPathExplanation(pathExplanations, nextNode.Id, edge, nodeId, nextNode.Id, depth + 1);
                }

                if (visited.Add(nextNode.Id))
                {
                    frontier.Enqueue((nextNode.Id, depth + 1));
                }
            }

            if (edgeTruncated)
            {
                break;
            }
        }

        var projection = new VyralGraphProjection
        {
            Id = $"projection:{envelope.Scope.GraphId}:{Guid.NewGuid():N}",
            Profile = profile,
            StartNodeIds = requestedStartNodeIds.Distinct(StringComparer.Ordinal).ToList(),
            Nodes = includedNodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            Edges = includedEdges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList(),
            CreatedAt = DateTime.UtcNow,
            Diagnostics = new JsonObject
            {
                ["sourceRecordCount"] = sourceExport.RecordCount,
                ["sourceTruncated"] = sourceExport.Truncated,
                ["availableNodeCount"] = envelope.Nodes.Count,
                ["availableEdgeCount"] = envelope.Edges.Count,
                ["nodeCount"] = includedNodes.Count,
                ["edgeCount"] = includedEdges.Count,
                ["missingStartNodeIds"] = ToJsonArray(missingStartNodeIds),
                ["edgeTruncated"] = edgeTruncated,
                ["nodeLimitReached"] = nodeLimitReached,
                ["filtered"] = ToJsonObject(filtered)
            }
        };

        if (profile.IncludePathExplanations)
        {
            projection.Diagnostics["pathExplanations"] = pathExplanations;
        }

        return projection;
    }

    private static IEnumerable<VyralGraphEdge> GetCandidateEdges(
        string nodeId,
        string direction,
        IReadOnlyDictionary<string, List<VyralGraphEdge>> outgoing,
        IReadOnlyDictionary<string, List<VyralGraphEdge>> incoming)
    {
        var normalized = string.IsNullOrWhiteSpace(direction) ? VyralGraphTraversalDirections.Both : direction;
        if ((normalized == VyralGraphTraversalDirections.Outgoing || normalized == VyralGraphTraversalDirections.Both) &&
            outgoing.TryGetValue(nodeId, out var outgoingEdges))
        {
            foreach (var edge in outgoingEdges)
            {
                yield return edge;
            }
        }

        if ((normalized == VyralGraphTraversalDirections.Incoming || normalized == VyralGraphTraversalDirections.Both) &&
            incoming.TryGetValue(nodeId, out var incomingEdges))
        {
            foreach (var edge in incomingEdges)
            {
                yield return edge;
            }
        }
    }

    private static string? ResolveNextNodeId(string currentNodeId, VyralGraphEdge edge, string direction)
    {
        var normalized = string.IsNullOrWhiteSpace(direction) ? VyralGraphTraversalDirections.Both : direction;
        if (normalized == VyralGraphTraversalDirections.Outgoing)
        {
            return string.Equals(edge.SourceId, currentNodeId, StringComparison.Ordinal) ? edge.TargetId : null;
        }

        if (normalized == VyralGraphTraversalDirections.Incoming)
        {
            return string.Equals(edge.TargetId, currentNodeId, StringComparison.Ordinal) ? edge.SourceId : null;
        }

        if (string.Equals(edge.SourceId, currentNodeId, StringComparison.Ordinal))
        {
            return edge.TargetId;
        }

        return string.Equals(edge.TargetId, currentNodeId, StringComparison.Ordinal) ? edge.SourceId : null;
    }

    private static bool ShouldIncludeNode(
        VyralGraphNode node,
        VyralGraphTraversalProfile profile,
        IReadOnlyDictionary<string, HashSet<string>> assertionIdsBySubject,
        IReadOnlyDictionary<string, VyralGraphAssertion> assertionsById,
        IReadOnlyDictionary<string, HashSet<string>> reviewStatusesByAssertionId,
        Dictionary<string, int> filtered)
    {
        if (profile.NodeTypes.Count > 0 && !profile.NodeTypes.Contains(node.Type, StringComparer.Ordinal))
        {
            filtered["nodeType"]++;
            return false;
        }

        return PassesSharedEntityFilters(
            node.Id,
            node.AssertionIds,
            node.SourceSpans,
            node.Properties,
            profile,
            applyAssertionReviewFilters: false,
            assertionIdsBySubject,
            assertionsById,
            reviewStatusesByAssertionId,
            filtered);
    }

    private static bool ShouldIncludeEdge(
        VyralGraphEdge edge,
        VyralGraphTraversalProfile profile,
        IReadOnlyDictionary<string, HashSet<string>> assertionIdsBySubject,
        IReadOnlyDictionary<string, VyralGraphAssertion> assertionsById,
        IReadOnlyDictionary<string, HashSet<string>> reviewStatusesByAssertionId,
        Dictionary<string, int> filtered)
    {
        if (profile.Predicates.Count > 0 && !profile.Predicates.Contains(edge.Predicate, StringComparer.Ordinal))
        {
            filtered["predicate"]++;
            return false;
        }

        return PassesSharedEntityFilters(
            edge.Id,
            edge.AssertionIds,
            edge.SourceSpans,
            edge.Properties,
            profile,
            applyAssertionReviewFilters: true,
            assertionIdsBySubject,
            assertionsById,
            reviewStatusesByAssertionId,
            filtered);
    }

    private static bool PassesSharedEntityFilters(
        string subjectId,
        IReadOnlyCollection<string> explicitAssertionIds,
        IReadOnlyCollection<VyralGraphSourceSpan> sourceSpans,
        JsonObject? properties,
        VyralGraphTraversalProfile profile,
        bool applyAssertionReviewFilters,
        IReadOnlyDictionary<string, HashSet<string>> assertionIdsBySubject,
        IReadOnlyDictionary<string, VyralGraphAssertion> assertionsById,
        IReadOnlyDictionary<string, HashSet<string>> reviewStatusesByAssertionId,
        Dictionary<string, int> filtered)
    {
        if (profile.RequireSourceGrounding && sourceSpans.Count == 0)
        {
            filtered["sourceGrounding"]++;
            return false;
        }

        if (profile.MinScore.HasValue && ReadScore(properties) is { } score && score < profile.MinScore.Value)
        {
            filtered["score"]++;
            return false;
        }

        var assertionIds = ResolveAssertionIds(subjectId, explicitAssertionIds, assertionIdsBySubject);
        if (applyAssertionReviewFilters && profile.AssertionStatuses.Count > 0)
        {
            var matchesAssertionStatus = assertionIds.Any(id =>
                assertionsById.TryGetValue(id, out var assertion) &&
                profile.AssertionStatuses.Contains(assertion.Status, StringComparer.Ordinal));
            if (!matchesAssertionStatus)
            {
                filtered["assertionStatus"]++;
                return false;
            }
        }

        if (applyAssertionReviewFilters && profile.ReviewStatuses.Count > 0)
        {
            var matchesReviewStatus = assertionIds.Any(id =>
                reviewStatusesByAssertionId.TryGetValue(id, out var statuses) &&
                statuses.Any(status => profile.ReviewStatuses.Contains(status, StringComparer.Ordinal)));
            if (!matchesReviewStatus)
            {
                filtered["reviewStatus"]++;
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> ResolveAssertionIds(
        string subjectId,
        IEnumerable<string> explicitAssertionIds,
        IReadOnlyDictionary<string, HashSet<string>> assertionIdsBySubject)
    {
        var assertionIds = explicitAssertionIds.ToHashSet(StringComparer.Ordinal);
        if (assertionIdsBySubject.TryGetValue(subjectId, out var bySubject))
        {
            assertionIds.UnionWith(bySubject);
        }

        return assertionIds;
    }

    private static double? ReadScore(JsonObject? properties)
    {
        if (properties?["score"] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
        if (value.TryGetValue<float>(out var floatValue)) return floatValue;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        return null;
    }

    private static VyralGraphTraversalProfile CloneProfile(VyralGraphTraversalProfile profile)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(profile, options);
        return JsonSerializer.Deserialize<VyralGraphTraversalProfile>(json, options)
            ?? new VyralGraphTraversalProfile();
    }

    private static void InspectRecordScope(
        IReadOnlyList<VyralRecord> records,
        VyralGraphCollectionInspectionResult result)
    {
        foreach (var record in records)
        {
            Increment(result.RecordTypeCounts, NormalizeCountKey(record.Type));
            Increment(result.GraphIdCounts, NormalizeCountKey(ReadMetadataString(record, VyralGraphMetadataKeys.GraphId)));
            Increment(result.NamespaceCounts, NormalizeCountKey(ReadMetadataString(record, VyralGraphMetadataKeys.Namespace)));
            Increment(result.TenantIdCounts, NormalizeCountKey(ReadMetadataString(record, VyralGraphMetadataKeys.TenantId)));
            Increment(result.PartitionKeyCounts, NormalizeCountKey(ReadMetadataString(record, VyralGraphMetadataKeys.GraphPartitionKey)));
        }
    }

    private static void InspectEnvelope(
        VyralGraphEnvelope envelope,
        VyralGraphCollectionInspectionRequest request,
        VyralGraphCollectionInspectionResult result)
    {
        var nodeIds = envelope.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edgeIds = envelope.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        var assertionIds = envelope.Assertions.Select(assertion => assertion.Id).ToHashSet(StringComparer.Ordinal);
        var projectionIds = envelope.Projections.Select(projection => projection.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var node in envelope.Nodes)
        {
            Increment(result.NodeTypeCounts, NormalizeCountKey(node.Type));
        }

        foreach (var edge in envelope.Edges)
        {
            Increment(result.PredicateCounts, NormalizeCountKey(edge.Predicate));
        }

        foreach (var assertion in envelope.Assertions)
        {
            Increment(result.AssertionStatusCounts, NormalizeCountKey(assertion.Status));
        }

        foreach (var review in envelope.Reviews)
        {
            Increment(result.ReviewStatusCounts, NormalizeCountKey(review.Status));
        }

        result.DuplicateNodeIdCount = AddDuplicateIdAnomalies(
            envelope.Nodes.Select(node => node.Id),
            "duplicateNodeId",
            "node",
            result,
            request);
        result.DuplicateEdgeIdCount = AddDuplicateIdAnomalies(
            envelope.Edges.Select(edge => edge.Id),
            "duplicateEdgeId",
            "edge",
            result,
            request);
        result.DuplicateAssertionIdCount = AddDuplicateIdAnomalies(
            envelope.Assertions.Select(assertion => assertion.Id),
            "duplicateAssertionId",
            "assertion",
            result,
            request);
        result.DuplicateReviewIdCount = AddDuplicateIdAnomalies(
            envelope.Reviews.Select(review => review.Id),
            "duplicateReviewId",
            "review",
            result,
            request);
        result.DuplicateProjectionIdCount = AddDuplicateIdAnomalies(
            envelope.Projections.Select(projection => projection.Id),
            "duplicateProjectionId",
            "projection",
            result,
            request);

        foreach (var edge in envelope.Edges)
        {
            var sourceExists = nodeIds.Contains(edge.SourceId);
            var targetExists = nodeIds.Contains(edge.TargetId);
            if (sourceExists && targetExists)
            {
                continue;
            }

            result.DanglingEdgeCount++;
            AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
            {
                Kind = "danglingEdge",
                Id = edge.Id,
                Message = $"Edge '{edge.Id}' references a missing source or target node.",
                Details = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceId"] = edge.SourceId,
                    ["sourceExists"] = sourceExists,
                    ["targetId"] = edge.TargetId,
                    ["targetExists"] = targetExists
                }
            });
        }

        foreach (var assertion in envelope.Assertions)
        {
            if (SubjectExists(assertion.SubjectKind, assertion.SubjectId, nodeIds, edgeIds, assertionIds, projectionIds))
            {
                continue;
            }

            result.OrphanAssertionCount++;
            AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
            {
                Kind = "orphanAssertion",
                Id = assertion.Id,
                SubjectId = assertion.SubjectId,
                SubjectKind = assertion.SubjectKind,
                Message = $"Assertion '{assertion.Id}' references missing {assertion.SubjectKind} subject '{assertion.SubjectId}'."
            });
        }

        foreach (var review in envelope.Reviews)
        {
            if (SubjectExists(review.SubjectKind, review.SubjectId, nodeIds, edgeIds, assertionIds, projectionIds))
            {
                continue;
            }

            result.OrphanReviewCount++;
            AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
            {
                Kind = "orphanReview",
                Id = review.Id,
                SubjectId = review.SubjectId,
                SubjectKind = review.SubjectKind,
                Message = $"Review '{review.Id}' references missing {review.SubjectKind} subject '{review.SubjectId}'."
            });
        }

        foreach (var node in envelope.Nodes)
        {
            InspectAssertionReferences(node.Id, node.AssertionIds, assertionIds, result, request);
        }

        foreach (var edge in envelope.Edges)
        {
            InspectAssertionReferences(edge.Id, edge.AssertionIds, assertionIds, result, request);
        }

        foreach (var projection in envelope.Projections)
        {
            foreach (var startNodeId in projection.StartNodeIds)
            {
                if (nodeIds.Contains(startNodeId))
                {
                    continue;
                }

                result.DanglingProjectionStartNodeCount++;
                AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
                {
                    Kind = "danglingProjectionStartNode",
                    Id = projection.Id,
                    SubjectId = startNodeId,
                    SubjectKind = VyralGraphSubjectKinds.Node,
                    Message = $"Projection '{projection.Id}' references missing start node '{startNodeId}'."
                });
            }
        }

        result.SourceGrounding = InspectSourceGrounding(envelope);
    }

    private static VyralGraphSourceGroundingInspection InspectSourceGrounding(VyralGraphEnvelope envelope)
    {
        var nodeGrounded = envelope.Nodes.Count(node => node.SourceSpans.Count > 0);
        var edgeGrounded = envelope.Edges.Count(edge => edge.SourceSpans.Count > 0);
        var assertionGrounded = envelope.Assertions.Count(assertion => assertion.SourceSpans.Count > 0);
        return new VyralGraphSourceGroundingInspection
        {
            NodeGroundedCount = nodeGrounded,
            NodeUngroundedCount = envelope.Nodes.Count - nodeGrounded,
            NodeCoverage = Coverage(nodeGrounded, envelope.Nodes.Count),
            EdgeGroundedCount = edgeGrounded,
            EdgeUngroundedCount = envelope.Edges.Count - edgeGrounded,
            EdgeCoverage = Coverage(edgeGrounded, envelope.Edges.Count),
            AssertionGroundedCount = assertionGrounded,
            AssertionUngroundedCount = envelope.Assertions.Count - assertionGrounded,
            AssertionCoverage = Coverage(assertionGrounded, envelope.Assertions.Count)
        };
    }

    private static void InspectAssertionReferences(
        string subjectId,
        IEnumerable<string> references,
        HashSet<string> assertionIds,
        VyralGraphCollectionInspectionResult result,
        VyralGraphCollectionInspectionRequest request)
    {
        foreach (var assertionId in references.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            if (assertionIds.Contains(assertionId))
            {
                continue;
            }

            result.DanglingAssertionReferenceCount++;
            AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
            {
                Kind = "danglingAssertionReference",
                Id = assertionId,
                SubjectId = subjectId,
                Message = $"Subject '{subjectId}' references missing assertion '{assertionId}'."
            });
        }
    }

    private static bool SubjectExists(
        string subjectKind,
        string subjectId,
        HashSet<string> nodeIds,
        HashSet<string> edgeIds,
        HashSet<string> assertionIds,
        HashSet<string> projectionIds)
    {
        return subjectKind switch
        {
            VyralGraphSubjectKinds.Node => nodeIds.Contains(subjectId),
            VyralGraphSubjectKinds.Edge => edgeIds.Contains(subjectId),
            VyralGraphSubjectKinds.Assertion => assertionIds.Contains(subjectId),
            VyralGraphSubjectKinds.Projection => projectionIds.Contains(subjectId),
            _ => false
        };
    }

    private static int AddDuplicateIdAnomalies(
        IEnumerable<string> ids,
        string kind,
        string subjectKind,
        VyralGraphCollectionInspectionResult result,
        VyralGraphCollectionInspectionRequest request)
    {
        var duplicateGroups = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            AddInspectionAnomaly(result, request, new VyralGraphInspectionAnomaly
            {
                Kind = kind,
                Id = group.Key,
                SubjectKind = subjectKind,
                Message = $"{subjectKind} id '{group.Key}' appears {group.Count()} times.",
                Details = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["count"] = group.Count()
                }
            });
        }

        return duplicateGroups.Count;
    }

    private static void AddInspectionWarnings(
        VyralGraphCollectionInspectionRequest request,
        VyralGraphCollectionInspectionResult result)
    {
        if (result.Truncated && !request.AllowPartialGraph)
        {
            result.Warnings.Add("Graph inspection source was truncated. Increase maxRecords or set allowPartialGraph to true for an intentional partial inspection.");
        }

        if (result.NodeCount == 0)
        {
            result.Warnings.Add("Graph has no nodes.");
        }

        if (result.DanglingEdgeCount > 0)
        {
            result.Warnings.Add("Graph contains edges with missing source or target nodes.");
        }

        if (result.OrphanAssertionCount > 0)
        {
            result.Warnings.Add("Graph contains assertions whose subjects cannot be resolved.");
        }

        if (result.OrphanReviewCount > 0)
        {
            result.Warnings.Add("Graph contains reviews whose subjects cannot be resolved.");
        }

        if (result.DanglingAssertionReferenceCount > 0)
        {
            result.Warnings.Add("Graph contains node or edge assertion references that cannot be resolved.");
        }

        if (result.DanglingProjectionStartNodeCount > 0)
        {
            result.Warnings.Add("Graph contains projections with missing start nodes.");
        }

        if (result.DuplicateNodeIdCount + result.DuplicateEdgeIdCount + result.DuplicateAssertionIdCount + result.DuplicateReviewIdCount + result.DuplicateProjectionIdCount > 0)
        {
            result.Warnings.Add("Graph contains duplicate entity ids.");
        }
    }

    private static bool IsTraversalReady(
        VyralGraphCollectionInspectionRequest request,
        VyralGraphCollectionInspectionResult result)
    {
        var duplicateCount = result.DuplicateNodeIdCount
            + result.DuplicateEdgeIdCount
            + result.DuplicateAssertionIdCount
            + result.DuplicateReviewIdCount
            + result.DuplicateProjectionIdCount;
        return result.NodeCount > 0
            && (request.AllowPartialGraph || !result.Truncated)
            && result.DanglingEdgeCount == 0
            && result.OrphanAssertionCount == 0
            && result.OrphanReviewCount == 0
            && duplicateCount == 0;
    }

    private static void ValidateTraversalRequest(VyralGraphTraversalRequest request)
    {
        if (request.StartNodeIds.Count == 0)
        {
            throw new InvalidOperationException("Graph traversal request must include at least one startNodeId.");
        }

        request.Profile ??= new VyralGraphTraversalProfile();
        if (request.Profile.MaxDepth < 0)
        {
            throw new InvalidOperationException("Graph traversal maxDepth cannot be negative.");
        }

        if (request.Profile.Limit <= 0)
        {
            throw new InvalidOperationException("Graph traversal limit must be greater than zero.");
        }

        if (request.Profile.EdgeLimit <= 0)
        {
            throw new InvalidOperationException("Graph traversal edgeLimit must be greater than zero.");
        }

        if (request.MaxRecords.HasValue && request.MaxRecords.Value <= 0)
        {
            throw new InvalidOperationException("Graph traversal maxRecords must be greater than zero.");
        }

        if (request.MaxRecords.HasValue && request.MaxRecords.Value > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph traversal maxRecords cannot exceed {VyralGraphCollectionLimits.MaxRecords}.");
        }
    }

    private static void ValidateInspectionRequest(VyralGraphCollectionInspectionRequest request)
    {
        if (request.MaxRecords.HasValue && request.MaxRecords.Value <= 0)
        {
            throw new InvalidOperationException("Graph inspection maxRecords must be greater than zero.");
        }

        if (request.MaxRecords.HasValue && request.MaxRecords.Value > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph inspection maxRecords cannot exceed {VyralGraphCollectionLimits.MaxRecords}.");
        }

        if (request.AnomalyLimit < 0)
        {
            throw new InvalidOperationException("Graph inspection anomalyLimit cannot be negative.");
        }

        if (request.AnomalyLimit > MaxGraphInspectionAnomalyLimit)
        {
            throw new InvalidOperationException($"Graph inspection anomalyLimit cannot exceed {MaxGraphInspectionAnomalyLimit}.");
        }
    }

    private static void ValidateDoctorRequest(VyralGraphDoctorRequest request)
    {
        if (request.MaxGraphRecords.HasValue && request.MaxGraphRecords.Value <= 0)
        {
            throw new InvalidOperationException("Graph doctor maxGraphRecords must be greater than zero.");
        }

        if (request.MaxGraphRecords.HasValue && request.MaxGraphRecords.Value > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph doctor maxGraphRecords cannot exceed {VyralGraphCollectionLimits.MaxRecords}.");
        }

        if (request.MaxTargetRecords <= 0)
        {
            throw new InvalidOperationException("Graph doctor maxTargetRecords must be greater than zero.");
        }

        if (request.AnomalyLimit < 0)
        {
            throw new InvalidOperationException("Graph doctor anomalyLimit cannot be negative.");
        }

        if (request.AnomalyLimit > MaxGraphInspectionAnomalyLimit)
        {
            throw new InvalidOperationException($"Graph doctor anomalyLimit cannot exceed {MaxGraphInspectionAnomalyLimit}.");
        }

        foreach (var pointer in request.SeedJsonPointers)
        {
            if (string.IsNullOrWhiteSpace(pointer) || !pointer.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Graph doctor seedJsonPointers must be JSON pointers.");
            }
        }
    }

    private static void ValidateExportRequest(VyralGraphCollectionExportRequest request)
    {
        if (request.MaxRecords.HasValue && request.MaxRecords.Value <= 0)
        {
            throw new InvalidOperationException("Graph export maxRecords must be greater than zero.");
        }

        if (request.MaxRecords.HasValue && request.MaxRecords.Value > VyralGraphCollectionLimits.MaxRecords)
        {
            throw new InvalidOperationException($"Graph export maxRecords cannot exceed {VyralGraphCollectionLimits.MaxRecords}.");
        }
    }

    private static void AddPathExplanation(JsonObject pathExplanations, string nodeId, VyralGraphEdge edge, string from, string to, int depth)
    {
        if (pathExplanations[nodeId] is not JsonArray path)
        {
            path = new JsonArray();
            pathExplanations[nodeId] = path;
        }

        path.Add(new JsonObject
        {
            ["edgeId"] = edge.Id,
            ["from"] = from,
            ["to"] = to,
            ["predicate"] = edge.Predicate,
            ["depth"] = depth
        });
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static IEnumerable<string> ResolveSeedValues(VyralRecord record, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(record));
        if (!TryGetJsonPointerValue(document.RootElement, path, out var value))
        {
            yield break;
        }

        foreach (var seed in JsonElementToSeedValues(value))
        {
            yield return seed;
        }
    }

    private static IEnumerable<string> JsonElementToSeedValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var itemSeed in JsonElementToSeedValues(item))
                {
                    yield return itemSeed;
                }
            }

            yield break;
        }

        var seed = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(seed))
        {
            yield return seed!.Trim();
        }
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
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    return false;
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            return false;
        }

        return true;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, int> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj;
    }

    private static void AddOptionalFilter(List<FilterNode> filters, string path, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            filters.Add(FilterNode.Eq(path, value.Trim()));
        }
    }

    private static void AddInspectionAnomaly(
        VyralGraphCollectionInspectionResult result,
        VyralGraphCollectionInspectionRequest request,
        VyralGraphInspectionAnomaly anomaly)
    {
        result.AnomalyCount++;
        if (!request.IncludeAnomalies || result.Anomalies.Count >= request.AnomalyLimit)
        {
            return;
        }

        result.Anomalies.Add(anomaly);
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? ReadMetadataString(VyralRecord record, string key)
    {
        if (record.Metadata is null || record.Metadata[key] is null)
        {
            return null;
        }

        return record.Metadata[key] switch
        {
            JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue,
            var node => node?.ToJsonString()
        };
    }

    private static string NormalizeCountKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? MissingCountKey : value.Trim();
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static double Coverage(int grounded, int total)
    {
        return total == 0 ? 1.0 : grounded / (double)total;
    }

    private static void SortInspectionCounts(VyralGraphCollectionInspectionResult result)
    {
        SortCounts(result.RecordTypeCounts);
        SortCounts(result.GraphIdCounts);
        SortCounts(result.NamespaceCounts);
        SortCounts(result.TenantIdCounts);
        SortCounts(result.PartitionKeyCounts);
        SortCounts(result.NodeTypeCounts);
        SortCounts(result.PredicateCounts);
        SortCounts(result.AssertionStatusCounts);
        SortCounts(result.ReviewStatusCounts);
    }

    private static void SortCounts(Dictionary<string, int> counts)
    {
        var sorted = counts.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
        counts.Clear();
        foreach (var (key, value) in sorted)
        {
            counts[key] = value;
        }
    }
}
