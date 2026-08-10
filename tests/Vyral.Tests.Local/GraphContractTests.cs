using System.Text.Json;
using System.Text.Json.Nodes;
using Vyral.Abstractions.Interfaces;
using Vyral.Abstractions.Models;
using Vyral.Local;

namespace Vyral.Tests.Local;

public class GraphContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void GraphEnvelope_RoundTripsRomanCamelCaseContract()
    {
        const string json = """
        {
          "schema": "roman.graph.v1",
          "scope": {
            "graphId": "software",
            "namespace": "tests",
            "collection": "fixtures",
            "tenantId": "tenant-a",
            "partitionKey": "repo:widget"
          },
          "metadata": {
            "exportedAt": "2026-06-18T22:00:00Z"
          },
          "nodes": [
            {
              "id": "file:cache.py",
              "type": "file",
              "label": "src/cache.py",
              "properties": {
                "score": 0.91
              },
              "sourceSpans": [
                {
                  "sourceRef": "file://src/cache.py",
                  "charStart": 0,
                  "charEnd": 120,
                  "unit": "utf16",
                  "locator": "src/cache.py",
                  "textHash": "sha256:abc"
                }
              ],
              "assertionIds": ["assertion:node"],
              "createdAt": "2026-06-18T22:00:00Z",
              "updatedAt": "2026-06-18T22:01:00Z"
            }
          ],
          "edges": [
            {
              "id": "edge:test-covers",
              "sourceId": "test:test_cache.py",
              "targetId": "file:cache.py",
              "predicate": "tests",
              "sourceSpans": [
                {
                  "sourceRef": "record:test",
                  "charStart": 10,
                  "charEnd": 20,
                  "unit": "utf16"
                }
              ],
              "assertionIds": ["assertion:edge"]
            }
          ],
          "assertions": [
            {
              "id": "assertion:edge",
              "subjectId": "edge:test-covers",
              "subjectKind": "edge",
              "status": "accepted",
              "method": "synthetic_fixture",
              "actor": "test",
              "confidence": 1.0,
              "sourceSpans": [
                {
                  "sourceRef": "record:test",
                  "charStart": 10,
                  "charEnd": 20,
                  "unit": "utf16"
                }
              ]
            }
          ],
          "reviews": [
            {
              "id": "review:1",
              "subjectId": "assertion:edge",
              "subjectKind": "assertion",
              "status": "accepted",
              "reviewer": "reviewer"
            }
          ],
          "projections": [
            {
              "id": "projection-1",
              "profile": {
                "id": "issue-impact",
                "direction": "both",
                "maxDepth": 2,
                "predicates": ["fixes", "part_of"],
                "edgeLimit": 20,
                "reviewStatuses": ["accepted"],
                "assertionStatuses": ["accepted"],
                "requireSourceGrounding": true,
                "minScore": 0.25,
                "includePathExplanations": true
              },
              "startNodeIds": ["issue:42"],
              "nodes": [],
              "edges": [],
              "diagnostics": {
                "nodeCount": 1,
                "edgeTruncated": false,
                "pathExplanations": {
                  "file:cache.py": [
                    {
                      "edgeId": "edge:test-covers"
                    }
                  ]
                }
              }
            }
          ]
        }
        """;

        var envelope = JsonSerializer.Deserialize<VyralGraphEnvelope>(json, JsonOptions);

        Assert.NotNull(envelope);
        Assert.Equal(VyralGraphSchemaVersions.RomanGraphV1, envelope!.Schema);
        Assert.Equal("software", envelope.Scope.GraphId);
        Assert.Equal("tenant-a", envelope.Scope.TenantId);
        var node = Assert.Single(envelope.Nodes);
        Assert.Equal("file:cache.py", node.Id);
        Assert.Equal(0.91, node.Properties?["score"]?.GetValue<double>());
        var span = Assert.Single(node.SourceSpans);
        Assert.Equal("file://src/cache.py", span.SourceRef);
        Assert.Equal(0, span.CharStart);
        Assert.Equal(120, span.CharEnd);
        Assert.Equal("utf16", span.Unit);
        Assert.Equal("sha256:abc", span.TextHash);
        var edge = Assert.Single(envelope.Edges);
        Assert.Equal("test:test_cache.py", edge.SourceId);
        Assert.Equal("file:cache.py", edge.TargetId);
        var assertion = Assert.Single(envelope.Assertions);
        Assert.Equal(VyralGraphAssertionStatuses.Accepted, assertion.Status);
        var projection = Assert.Single(envelope.Projections);
        Assert.Equal(2, projection.Profile.MaxDepth);
        Assert.True(projection.Profile.RequireSourceGrounding);
        Assert.Equal(20, projection.Profile.EdgeLimit);
        Assert.Equal("edge:test-covers", projection.Diagnostics?["pathExplanations"]?["file:cache.py"]?[0]?["edgeId"]?.GetValue<string>());

        var serialized = JsonSerializer.Serialize(envelope, JsonOptions);

        Assert.Contains("\"sourceSpans\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"startNodeIds\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source_spans", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("start_node_ids", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphEnvelope_AcceptsLegacySnakeCaseImportsWithoutEmittingLegacyKeys()
    {
        const string json = """
        {
          "schema": "roman.graph.v1",
          "scope": {
            "graph_id": "legacy",
            "tenant_id": "tenant-a",
            "partition_key": "pk-a"
          },
          "nodes": [
            {
              "node_id": "node:1",
              "node_type": "example",
              "source_spans": [
                {
                  "source_ref": "record:1",
                  "start": 5,
                  "end": 12,
                  "text_hash": "sha256:def"
                }
              ],
              "assertion_ids": ["assertion:1"],
              "created_at": "2026-06-18T22:00:00Z",
              "updated_at": "2026-06-18T22:01:00Z"
            }
          ],
          "edges": [
            {
              "edge_id": "edge:1",
              "source_id": "node:1",
              "target_id": "node:2",
              "predicate": "points_to",
              "source_spans": [
                {
                  "source_ref": "record:edge",
                  "start": 1,
                  "end": 2
                }
              ],
              "assertion_ids": ["assertion:edge"]
            }
          ],
          "assertions": [
            {
              "assertion_id": "assertion:1",
              "subject_id": "node:1",
              "subject_kind": "node",
              "source_spans": [
                {
                  "source_ref": "record:assertion",
                  "start": 3,
                  "end": 4
                }
              ],
              "created_at": "2026-06-18T22:02:00Z"
            }
          ],
          "reviews": [
            {
              "review_id": "review:1",
              "subject_id": "assertion:1",
              "subject_kind": "assertion",
              "status": "accepted",
              "reviewer": "reviewer",
              "created_at": "2026-06-18T22:03:00Z"
            }
          ],
          "projections": [
            {
              "projection_id": "projection:1",
              "profile": {
                "max_depth": 2,
                "edge_limit": 8,
                "include_start": false,
                "node_types": ["example"],
                "review_statuses": ["accepted"],
                "assertion_statuses": ["proposed"],
                "require_source_grounding": true,
                "min_score": 0.5,
                "include_path_explanations": false
              },
              "start_node_ids": ["node:1"],
              "nodes": [],
              "edges": []
            }
          ]
        }
        """;

        var envelope = JsonSerializer.Deserialize<VyralGraphEnvelope>(json, JsonOptions);

        Assert.NotNull(envelope);
        Assert.Equal("legacy", envelope!.Scope.GraphId);
        Assert.Equal("tenant-a", envelope.Scope.TenantId);
        Assert.Equal("pk-a", envelope.Scope.PartitionKey);
        var node = Assert.Single(envelope.Nodes);
        Assert.Equal("node:1", node.Id);
        Assert.Equal("example", node.Type);
        Assert.Equal(new[] { "assertion:1" }, node.AssertionIds);
        Assert.Equal("record:1", node.SourceSpans[0].SourceRef);
        Assert.Equal(5, node.SourceSpans[0].CharStart);
        Assert.Equal(12, node.SourceSpans[0].CharEnd);
        Assert.Equal("utf16", node.SourceSpans[0].Unit);
        Assert.Equal("sha256:def", node.SourceSpans[0].TextHash);
        var edge = Assert.Single(envelope.Edges);
        Assert.Equal("edge:1", edge.Id);
        Assert.Equal("node:1", edge.SourceId);
        Assert.Equal("node:2", edge.TargetId);
        var assertion = Assert.Single(envelope.Assertions);
        Assert.Equal("assertion:1", assertion.Id);
        Assert.Equal("node", assertion.SubjectKind);
        var review = Assert.Single(envelope.Reviews);
        Assert.Equal("review:1", review.Id);
        var projection = Assert.Single(envelope.Projections);
        Assert.Equal("projection:1", projection.Id);
        Assert.Equal(2, projection.Profile.MaxDepth);
        Assert.Equal(8, projection.Profile.EdgeLimit);
        Assert.False(projection.Profile.IncludeStart);
        Assert.Equal(new[] { "example" }, projection.Profile.NodeTypes);
        Assert.True(projection.Profile.RequireSourceGrounding);
        Assert.Equal(0.5, projection.Profile.MinScore);
        Assert.False(projection.Profile.IncludePathExplanations);

        var serialized = JsonSerializer.Serialize(envelope, JsonOptions);

        Assert.Contains("\"graphId\":\"legacy\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"sourceRef\":\"record:1\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"maxDepth\":2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("graph_id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source_ref", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("max_depth", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphProviderShapeCatalog_ExposesRomanProviderBoundary()
    {
        var shapes = VyralGraphProviderShapeCatalog.All;

        Assert.Contains(shapes, shape => shape.ProviderId == VyralGraphProviderShapeIds.LocalSqlite);
        Assert.Contains(shapes, shape => shape.ProviderId == VyralGraphProviderShapeIds.CosmosGremlin);
        Assert.Contains(shapes, shape => shape.ProviderId == VyralGraphProviderShapeIds.Neptune);
        Assert.Contains(shapes, shape => shape.ProviderId == VyralGraphProviderShapeIds.SpannerGraph);

        var vyral = VyralGraphProviderShapeCatalog.Get(VyralGraphProviderShapeIds.VyralCollection);

        Assert.Equal(vyral.ProviderId, vyral.Id);
        Assert.Equal(VyralGraphProviderKinds.VyralCollection, vyral.Kind);
        Assert.Equal("partitionKey", vyral.PartitionField);
        Assert.Equal("tenantId", vyral.TenantField);
        Assert.Contains("hybrid_retrieval_join", vyral.Capabilities);
        Assert.Contains("provider_trace_join", vyral.Capabilities);
    }

    [Fact]
    public void GraphProviderShape_AcceptsLegacySnakeCaseImports()
    {
        const string json = """
        {
          "provider_id": "vyral-collection",
          "kind": "vyral_collection",
          "graph_id_field": "graphId",
          "node_id_field": "id",
          "edge_id_field": "id",
          "source_field": "sourceId",
          "target_field": "targetId",
          "partition_field": "partitionKey",
          "tenant_field": "tenantId",
          "capabilities": ["hybrid_retrieval_join"]
        }
        """;

        var shape = JsonSerializer.Deserialize<VyralGraphProviderShape>(json, JsonOptions);

        Assert.NotNull(shape);
        Assert.Equal("vyral-collection", shape!.ProviderId);
        Assert.Equal("vyral_collection", shape.Kind);
        Assert.Equal("partitionKey", shape.PartitionField);
        Assert.Equal("tenantId", shape.TenantField);
        Assert.Contains("hybrid_retrieval_join", shape.Capabilities);

        var serialized = JsonSerializer.Serialize(shape, JsonOptions);
        Assert.Contains("\"providerId\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_id", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphRecordMapper_RoundTripsEnvelopeThroughSafeCollectionRecords()
    {
        var envelope = CreateGraphEnvelopeWithUnsafeIds();

        var records = VyralGraphRecordMapper.ToRecords(envelope);
        var roundTripped = VyralGraphRecordMapper.FromRecords(records);

        Assert.Equal(6, records.Count);
        Assert.All(records, record =>
        {
            Assert.StartsWith("g:", record.Id, StringComparison.Ordinal);
            Assert.DoesNotContain("/", record.Id, StringComparison.Ordinal);
            Assert.DoesNotContain("#", record.Id, StringComparison.Ordinal);
            Assert.Equal(VyralGraphRecordMapper.ResolvePartitionKey(envelope.Scope), record.PartitionKey);
            Assert.Equal("example-graph", record.Metadata![VyralGraphMetadataKeys.GraphId]!.GetValue<string>());
        });
        Assert.Contains(records, record => record.Type == VyralGraphRecordTypes.Envelope);
        Assert.Equal("example-graph", roundTripped.Scope.GraphId);
        Assert.Equal("tenant-a", roundTripped.Scope.TenantId);
        Assert.Equal("node/1#unsafe", Assert.Single(roundTripped.Nodes).Id);
        Assert.Equal("edge/1#unsafe", Assert.Single(roundTripped.Edges).Id);
        Assert.Equal("assertion/1#unsafe", Assert.Single(roundTripped.Assertions).Id);
        Assert.Equal("review/1#unsafe", Assert.Single(roundTripped.Reviews).Id);
        Assert.Equal("projection/1#unsafe", Assert.Single(roundTripped.Projections).Id);
    }

    [Fact]
    public async Task GraphCollectionStoreExtensions_ImportsAndExportsGraphEnvelope()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-graph-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        var envelope = CreateGraphEnvelopeWithUnsafeIds();

        var preflight = await store.PreflightGraphImportAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = envelope
        });

        Assert.True(preflight.ReadyToImport);
        Assert.True(preflight.WouldCreateCollection);
        Assert.Equal(VyralGraphImportPolicyStatuses.Created, preflight.CollectionPolicyStatus);
        Assert.Equal(6, preflight.RecordCount);
        Assert.Equal("example-graph", preflight.GraphId);

        var import = await store.ImportGraphEnvelopeAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = envelope
        });

        Assert.Equal(VyralGraphImportPolicyStatuses.Created, import.PolicyStatus);
        Assert.Equal(6, import.RecordCount);
        Assert.Equal(6, import.Records.Succeeded);
        var policy = await store.GetCollectionPolicyAsync("graphs");
        Assert.NotNull(policy);
        Assert.True(VyralGraphRecordMapper.IsGraphCollectionPolicy(policy!));

        var export = await store.ExportGraphEnvelopeAsync("graphs", new VyralGraphCollectionExportRequest
        {
            GraphId = "example-graph"
        });

        Assert.NotNull(export);
        Assert.Equal(6, export!.RecordCount);
        Assert.False(export.Truncated);
        Assert.Equal("example-graph", export.Envelope.Scope.GraphId);
        Assert.Equal("node/1#unsafe", Assert.Single(export.Envelope.Nodes).Id);
        Assert.Equal("edge/1#unsafe", Assert.Single(export.Envelope.Edges).Id);
        Assert.Equal("projection/1#unsafe", Assert.Single(export.Envelope.Projections).Id);

        var withoutProjections = await store.ExportGraphEnvelopeAsync("graphs", new VyralGraphCollectionExportRequest
        {
            GraphId = "example-graph",
            IncludeProjections = false
        });
        Assert.NotNull(withoutProjections);
        Assert.Empty(withoutProjections!.Envelope.Projections);
        Assert.Equal(5, withoutProjections.RecordCount);
    }

    [Fact]
    public async Task GraphCollectionStoreExtensions_TraverseGraphReturnsBoundedProjectionWithDiagnostics()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-graph-traverse-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.ImportGraphEnvelopeAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = CreateTraversalEnvelope()
        });

        var traversal = await store.TraverseGraphAsync("graphs", new VyralGraphTraversalRequest
        {
            GraphId = "traversal",
            StartNodeIds = new List<string> { "node:a", "node:missing" },
            Profile = new VyralGraphTraversalProfile
            {
                Id = "accepted-supports",
                Direction = VyralGraphTraversalDirections.Outgoing,
                MaxDepth = 2,
                Predicates = new List<string> { "supports" },
                AssertionStatuses = new List<string> { VyralGraphAssertionStatuses.Accepted },
                ReviewStatuses = new List<string> { VyralGraphReviewStatuses.Accepted },
                RequireSourceGrounding = true,
                EdgeLimit = 10,
                Limit = 10
            }
        });

        Assert.NotNull(traversal);
        Assert.Equal("traversal", traversal!.GraphId);
        Assert.Equal(new[] { "node:a", "node:b", "node:c" }, traversal.Projection.Nodes.Select(node => node.Id));
        Assert.Equal(new[] { "edge:a-b", "edge:b-c" }, traversal.Projection.Edges.Select(edge => edge.Id));
        Assert.Equal("node:missing", traversal.Projection.Diagnostics?["missingStartNodeIds"]?[0]?.GetValue<string>());
        Assert.Equal("edge:a-b", traversal.Projection.Diagnostics?["pathExplanations"]?["node:b"]?[0]?["edgeId"]?.GetValue<string>());
        Assert.Equal(1, traversal.Projection.Diagnostics?["filtered"]?["predicate"]?.GetValue<int>());
        Assert.Equal("filtered_graph_export", traversal.Projection.Diagnostics?["sourceScanMode"]?.GetValue<string>());
        Assert.Equal(VyralGraphCollectionLimits.MaxRecords, traversal.Projection.Diagnostics?["requestedMaxRecords"]?.GetValue<int>());
        Assert.False(traversal.Projection.Diagnostics?["allowPartialGraph"]?.GetValue<bool>());
        Assert.True(traversal.Projection.Diagnostics?["graphIdFilterApplied"]?.GetValue<bool>());
        Assert.False(traversal.Projection.Diagnostics?["namespaceFilterApplied"]?.GetValue<bool>());
        Assert.False(traversal.Projection.Diagnostics?["tenantFilterApplied"]?.GetValue<bool>());
        Assert.False(traversal.Projection.Diagnostics?["partitionFilterApplied"]?.GetValue<bool>());
        Assert.True(traversal.Projection.Diagnostics?["sourceExportDurationMs"]?.GetValue<double>() >= 0);
        Assert.True(traversal.Projection.Diagnostics?["traversalDurationMs"]?.GetValue<double>() >= 0);
        Assert.True(traversal.Projection.Diagnostics?["durationMs"]?.GetValue<double>() >= 0);

        var truncated = await store.TraverseGraphAsync("graphs", new VyralGraphTraversalRequest
        {
            GraphId = "traversal",
            StartNodeIds = new List<string> { "node:a" },
            Profile = new VyralGraphTraversalProfile
            {
                Direction = VyralGraphTraversalDirections.Outgoing,
                MaxDepth = 2,
                EdgeLimit = 1,
                Limit = 10
            }
        });

        Assert.NotNull(truncated);
        Assert.True(truncated!.Projection.Diagnostics?["edgeTruncated"]?.GetValue<bool>());
        Assert.Equal(1, truncated.EdgeCount);
    }

    [Fact]
    public async Task GraphCollectionStoreExtensions_InspectsGraphReadinessAndAnomalies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vyral-graph-inspect-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteRecordCollectionStore(dbPath);
        await store.InitializeAsync();
        await store.ImportGraphEnvelopeAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = CreateTraversalEnvelope()
        });

        var ready = await store.InspectGraphAsync("graphs", new VyralGraphCollectionInspectionRequest
        {
            GraphId = "traversal"
        });

        Assert.NotNull(ready);
        Assert.True(ready!.TraversalReady);
        Assert.Equal(14, ready.RecordCount);
        Assert.Equal(4, ready.NodeCount);
        Assert.Equal(3, ready.EdgeCount);
        Assert.Equal(1, ready.PredicateCounts["mentions"]);
        Assert.Equal(2, ready.PredicateCounts["supports"]);
        Assert.Equal(1.0, ready.SourceGrounding.NodeCoverage);
        Assert.Equal(1.0, ready.SourceGrounding.EdgeCoverage);
        Assert.Equal(0.0, ready.SourceGrounding.AssertionCoverage);
        Assert.Empty(ready.Warnings);
        Assert.Empty(ready.Anomalies);

        await store.CreateCollectionAsync(new RecordCollectionPolicy
        {
            Name = "chunks",
            IndexedMetadata = new List<string> { "/metadata/graphNodeId" }
        });
        await store.UpsertRecordAsync("chunks", new VyralRecord
        {
            Id = "chunk-a",
            PartitionKey = "tenant-a",
            Type = "rag.chunk",
            Metadata = new JsonObject
            {
                ["graphNodeId"] = "node:a"
            },
            Content = new JsonObject
            {
                ["text"] = "A"
            }
        });

        var doctor = await store.DoctorGraphAsync("graphs", new VyralGraphDoctorRequest
        {
            GraphId = "traversal",
            TargetCollection = "chunks",
            TargetPartitionKeys = new List<string> { "tenant-a" },
            SeedJsonPointers = new List<string> { "/metadata/graphNodeId" }
        });

        Assert.NotNull(doctor);
        Assert.True(doctor!.Ready);
        Assert.Equal("ready", doctor.Status);
        Assert.NotNull(doctor.SeedCoverage);
        Assert.Equal(1, doctor.SeedCoverage!.TargetRecordCount);
        Assert.Equal(1, doctor.SeedCoverage.ResolvedSeedNodeCount);
        Assert.Equal(1.0, doctor.SeedCoverage.ResolvedSeedCoverage);

        await store.ImportGraphEnvelopeAsync("graphs", new VyralGraphCollectionImportRequest
        {
            Envelope = CreateGraphEnvelopeWithUnsafeIds(),
            ReplaceExisting = true
        });

        var drifted = await store.InspectGraphAsync("graphs", new VyralGraphCollectionInspectionRequest
        {
            GraphId = "example-graph",
            AnomalyLimit = 1
        });

        Assert.NotNull(drifted);
        Assert.False(drifted!.TraversalReady);
        Assert.Equal(6, drifted.RecordCount);
        Assert.Equal(1, drifted.NodeCount);
        Assert.Equal(1, drifted.EdgeCount);
        Assert.Equal(1, drifted.DanglingEdgeCount);
        Assert.Equal(1, drifted.AnomalyCount);
        Assert.Equal(1, drifted.ReturnedAnomalyCount);
        Assert.Equal("danglingEdge", Assert.Single(drifted.Anomalies).Kind);
        Assert.Equal(0.0, drifted.SourceGrounding.EdgeCoverage);
        Assert.Contains(drifted.Warnings, warning => warning.Contains("missing source or target", StringComparison.Ordinal));
    }

    private static VyralGraphEnvelope CreateGraphEnvelopeWithUnsafeIds()
    {
        return new VyralGraphEnvelope
        {
            Scope = new VyralGraphScope
            {
                GraphId = "example-graph",
                Namespace = "documents",
                Collection = "pages",
                TenantId = "tenant-a",
                PartitionKey = "source:example"
            },
            Metadata = new JsonObject
            {
                ["source"] = "unit-test"
            },
            Nodes = new List<VyralGraphNode>
            {
                new()
                {
                    Id = "node/1#unsafe",
                    Type = "page",
                    Label = "Example page 1",
                    SourceSpans = new List<VyralGraphSourceSpan>
                    {
                        new()
                        {
                            SourceRef = "record:page-1",
                            CharStart = 0,
                            CharEnd = 40,
                            Unit = "utf16",
                            TextHash = "sha256:page1"
                        }
                    }
                }
            },
            Edges = new List<VyralGraphEdge>
            {
                new()
                {
                    Id = "edge/1#unsafe",
                    SourceId = "node/1#unsafe",
                    TargetId = "node/2#unsafe",
                    Predicate = "cites"
                }
            },
            Assertions = new List<VyralGraphAssertion>
            {
                new()
                {
                    Id = "assertion/1#unsafe",
                    SubjectId = "edge/1#unsafe",
                    SubjectKind = VyralGraphSubjectKinds.Edge,
                    Status = VyralGraphAssertionStatuses.Accepted,
                    Method = "fixture",
                    Actor = "test"
                }
            },
            Reviews = new List<VyralGraphReviewEvent>
            {
                new()
                {
                    Id = "review/1#unsafe",
                    SubjectId = "assertion/1#unsafe",
                    SubjectKind = VyralGraphSubjectKinds.Assertion,
                    Status = VyralGraphReviewStatuses.Accepted,
                    Reviewer = "tester"
                }
            },
            Projections = new List<VyralGraphProjection>
            {
                new()
                {
                    Id = "projection/1#unsafe",
                    StartNodeIds = new List<string> { "node/1#unsafe" },
                    Profile = new VyralGraphTraversalProfile
                    {
                        Id = "citation-neighborhood",
                        MaxDepth = 1
                    }
                }
            }
        };
    }

    private static VyralGraphEnvelope CreateTraversalEnvelope()
    {
        static List<VyralGraphSourceSpan> Source(string id) => new()
        {
            new()
            {
                SourceRef = id,
                CharStart = 0,
                CharEnd = 10,
                Unit = "utf16"
            }
        };

        return new VyralGraphEnvelope
        {
            Scope = new VyralGraphScope
            {
                GraphId = "traversal",
                Namespace = "tests",
                Collection = "graphs",
                TenantId = "tenant-a",
                PartitionKey = "graph:traversal"
            },
            Nodes = new List<VyralGraphNode>
            {
                new() { Id = "node:a", Type = "concept", Label = "A", SourceSpans = Source("source:a") },
                new() { Id = "node:b", Type = "concept", Label = "B", SourceSpans = Source("source:b") },
                new() { Id = "node:c", Type = "concept", Label = "C", SourceSpans = Source("source:c") },
                new() { Id = "node:d", Type = "concept", Label = "D", SourceSpans = Source("source:d") }
            },
            Edges = new List<VyralGraphEdge>
            {
                new()
                {
                    Id = "edge:a-b",
                    SourceId = "node:a",
                    TargetId = "node:b",
                    Predicate = "supports",
                    SourceSpans = Source("source:a-b"),
                    AssertionIds = new List<string> { "assertion:a-b" }
                },
                new()
                {
                    Id = "edge:b-c",
                    SourceId = "node:b",
                    TargetId = "node:c",
                    Predicate = "supports",
                    SourceSpans = Source("source:b-c"),
                    AssertionIds = new List<string> { "assertion:b-c" }
                },
                new()
                {
                    Id = "edge:a-d",
                    SourceId = "node:a",
                    TargetId = "node:d",
                    Predicate = "mentions",
                    SourceSpans = Source("source:a-d"),
                    AssertionIds = new List<string> { "assertion:a-d" }
                }
            },
            Assertions = new List<VyralGraphAssertion>
            {
                new()
                {
                    Id = "assertion:a-b",
                    SubjectId = "edge:a-b",
                    SubjectKind = VyralGraphSubjectKinds.Edge,
                    Status = VyralGraphAssertionStatuses.Accepted
                },
                new()
                {
                    Id = "assertion:b-c",
                    SubjectId = "edge:b-c",
                    SubjectKind = VyralGraphSubjectKinds.Edge,
                    Status = VyralGraphAssertionStatuses.Accepted
                },
                new()
                {
                    Id = "assertion:a-d",
                    SubjectId = "edge:a-d",
                    SubjectKind = VyralGraphSubjectKinds.Edge,
                    Status = VyralGraphAssertionStatuses.Rejected
                }
            },
            Reviews = new List<VyralGraphReviewEvent>
            {
                new()
                {
                    Id = "review:a-b",
                    SubjectId = "assertion:a-b",
                    SubjectKind = VyralGraphSubjectKinds.Assertion,
                    Status = VyralGraphReviewStatuses.Accepted,
                    Reviewer = "test"
                },
                new()
                {
                    Id = "review:b-c",
                    SubjectId = "assertion:b-c",
                    SubjectKind = VyralGraphSubjectKinds.Assertion,
                    Status = VyralGraphReviewStatuses.Accepted,
                    Reviewer = "test"
                },
                new()
                {
                    Id = "review:a-d",
                    SubjectId = "assertion:a-d",
                    SubjectKind = VyralGraphSubjectKinds.Assertion,
                    Status = VyralGraphReviewStatuses.Rejected,
                    Reviewer = "test"
                }
            }
        };
    }
}
