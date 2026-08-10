from __future__ import annotations

import asyncio
from datetime import datetime, timezone
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any
import unittest

from jsonschema import Draft202012Validator

from vyral_runtime import (
    GraphDoctorRequest,
    GraphExportRequest,
    GraphImportRequest,
    GraphInspectionRequest,
    GraphService,
    GraphTraversalRequest,
    GraphTraversalTruncatedError,
    RecordCollectionPolicy,
    SQLiteRecordStore,
    VyralGraphAssertion,
    VyralGraphEdge,
    VyralGraphEnvelope,
    VyralGraphNode,
    VyralGraphReviewEvent,
    VyralGraphScope,
    VyralGraphSourceSpan,
    VyralGraphTraversalProfile,
    VyralRecord,
    graph_from_records,
    graph_record_id,
    graph_to_records,
    load_contract_bundle,
)


NOW = datetime(2026, 7, 30, 15, 0, tzinfo=timezone.utc)


class GraphServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.store = SQLiteRecordStore(
            Path(self.temporary_directory.name) / "graph.sqlite",
            clock=lambda: NOW,
        )
        self.service = GraphService(
            self.store,
            clock=lambda: NOW,
        )

    def tearDown(self) -> None:
        self.service.close()
        self.temporary_directory.cleanup()

    def envelope(self, *, dangling: bool = False) -> VyralGraphEnvelope:
        grounded = (
            VyralGraphSourceSpan(
                source_ref="vyral://guide/runtime",
                char_start=0,
                char_end=30,
                locator="runtime",
            ),
        )
        return VyralGraphEnvelope(
            scope=VyralGraphScope(
                graph_id="runtime",
                namespace="docs",
                collection="knowledge",
                tenant_id="tenant-a",
            ),
            metadata={"title": "Runtime graph"},
            nodes=(
                VyralGraphNode("a", "component", "Gateway", source_spans=grounded),
                VyralGraphNode("b", "component", "Worker", source_spans=grounded),
                VyralGraphNode("c", "component", "Store"),
            ),
            edges=(
                VyralGraphEdge(
                    "e1",
                    "a",
                    "missing" if dangling else "b",
                    "routes_to",
                    source_spans=grounded,
                    assertion_ids=("assert-e1",),
                ),
                VyralGraphEdge("e2", "b", "c", "persists_to"),
            ),
            assertions=(
                VyralGraphAssertion(
                    "assert-e1",
                    "e1",
                    subject_kind="edge",
                    status="accepted",
                    source_spans=grounded,
                ),
            ),
            reviews=(
                VyralGraphReviewEvent(
                    "review-e1",
                    "assert-e1",
                    status="verified",
                    reviewer="tester",
                ),
            ),
        )

    def test_mapper_matches_portable_record_shape_and_round_trips(self) -> None:
        envelope = self.envelope()
        records = graph_to_records(envelope, clock=lambda: NOW)

        self.assertEqual(8, len(records))
        self.assertEqual("g:node:YQ", graph_record_id("graph.node", "a"))
        node = next(record for record in records if record.type == "graph.node")
        self.assertEqual("tenant:tenant-a", node.partition_key)
        self.assertEqual("runtime", node.metadata["graphId"] if node.metadata else None)
        self.assertEqual("graphSourceSpan", node.sources[0]["kind"] if node.sources else None)

        restored = graph_from_records(reversed(records))
        self.assertEqual(envelope.to_dict(), restored.to_dict())
        json.dumps(restored.to_dict())

    def test_preflight_import_export_and_async_round_trip(self) -> None:
        request = GraphImportRequest(self.envelope())
        preflight = self.service.preflight_import("graph", request)
        self.assertTrue(preflight.ready_to_import)
        self.assertTrue(preflight.would_create_collection)
        self.assertEqual("created", preflight.collection_policy_status)

        imported = self.service.import_envelope("graph", request)
        self.assertEqual(8, imported.record_count)
        self.assertEqual(8, imported.records.succeeded)

        exported = self.service.export_envelope(
            "graph",
            GraphExportRequest(graph_id="runtime"),
        )
        self.assertIsNotNone(exported)
        assert exported is not None
        self.assertFalse(exported.truncated)
        self.assertEqual(self.envelope().to_dict(), exported.envelope.to_dict())

        async_export = asyncio.run(
            self.service.aexport_envelope("graph", {"graphId": "runtime"})
        )
        self.assertEqual(exported.envelope, async_export.envelope if async_export else None)

    def test_traversal_is_bounded_filtered_and_deterministic(self) -> None:
        self.service.import_envelope("graph", GraphImportRequest(self.envelope()))
        request = GraphTraversalRequest(
            start_node_ids=("a", "unknown"),
            profile=VyralGraphTraversalProfile(
                direction="outgoing",
                max_depth=2,
                predicates=("routes_to",),
                assertion_statuses=("accepted",),
                review_statuses=("verified",),
                require_source_grounding=True,
                include_path_explanations=True,
            ),
        )
        first = self.service.traverse("graph", request)
        second = self.service.traverse("graph", request)
        self.assertIsNotNone(first)
        self.assertIsNotNone(second)
        assert first is not None and second is not None
        self.assertEqual(("a", "b"), tuple(node.id for node in first.projection.nodes))
        self.assertEqual(("e1",), tuple(edge.id for edge in first.projection.edges))
        self.assertEqual(first.projection.id, second.projection.id)
        diagnostics: Any = first.projection.diagnostics
        self.assertEqual(["unknown"], diagnostics["missingStartNodeIds"])
        self.assertEqual(1, diagnostics["pathExplanations"]["b"][0]["depth"])

        with self.assertRaises(GraphTraversalTruncatedError):
            self.service.traverse(
                "graph",
                GraphTraversalRequest(
                    start_node_ids=("a",),
                    max_records=2,
                ),
            )

    def test_inspection_and_doctor_report_anomalies_and_seed_coverage(self) -> None:
        self.service.import_envelope("graph", GraphImportRequest(self.envelope()))
        inspection = self.service.inspect("graph", GraphInspectionRequest())
        self.assertIsNotNone(inspection)
        assert inspection is not None
        self.assertTrue(inspection.traversal_ready)
        self.assertAlmostEqual(2 / 3, inspection.source_grounding.node_coverage)

        self.store.create_collection(RecordCollectionPolicy(name="docs"))
        self.store.upsert_record(
            "docs",
            VyralRecord(
                id="doc-a",
                partition_key="tenant-a",
                metadata={"graphNodeId": "a"},
            ),
        )
        doctor = self.service.doctor(
            "graph",
            GraphDoctorRequest(
                target_collection="docs",
                seed_json_pointers=("/metadata/graphNodeId",),
            ),
        )
        self.assertIsNotNone(doctor)
        assert doctor is not None
        self.assertTrue(doctor.ready)
        self.assertEqual(
            1.0,
            doctor.seed_coverage.resolved_seed_coverage
            if doctor.seed_coverage
            else 0,
        )

        self.service.import_envelope(
            "bad-graph",
            GraphImportRequest(self.envelope(dangling=True)),
        )
        bad = self.service.inspect("bad-graph")
        self.assertIsNotNone(bad)
        assert bad is not None
        self.assertFalse(bad.traversal_ready)
        self.assertEqual(1, bad.dangling_edge_count)
        self.assertEqual("danglingEdge", bad.anomalies[0].kind)

    def test_truncated_inspection_reports_only_selected_record_types(self) -> None:
        self.service.import_envelope("graph", GraphImportRequest(self.envelope()))

        inspection = self.service.inspect(
            "graph",
            GraphInspectionRequest(
                max_records=1,
                allow_partial_graph=True,
            ),
        )

        self.assertIsNotNone(inspection)
        assert inspection is not None
        self.assertTrue(inspection.truncated)
        self.assertEqual(1, inspection.record_count)
        self.assertEqual(
            {"graph.assertion": 1},
            inspection.record_type_counts,
        )

    def test_non_graph_policy_requires_explicit_override(self) -> None:
        self.store.create_collection(RecordCollectionPolicy(name="plain"))
        preflight = self.service.preflight_import(
            "plain",
            GraphImportRequest(self.envelope()),
        )
        self.assertFalse(preflight.ready_to_import)
        self.assertIn("missing graph metadata indexes", preflight.errors[0])

        imported = self.service.import_envelope(
            "plain",
            GraphImportRequest(
                self.envelope(),
                allow_non_graph_policy=True,
            ),
        )
        self.assertEqual("existing_non_graph_policy_allowed", imported.policy_status)

    def test_public_graph_wire_models_validate_against_contract(self) -> None:
        envelope = self.envelope()
        import_request = GraphImportRequest(envelope)
        preflight = self.service.preflight_import("graph", import_request)
        imported = self.service.import_envelope("graph", import_request)
        export_request = GraphExportRequest(graph_id="runtime")
        exported = self.service.export_envelope("graph", export_request)
        traversal_request = GraphTraversalRequest(start_node_ids=("a",))
        traversed = self.service.traverse("graph", traversal_request)
        inspection_request = GraphInspectionRequest()
        inspected = self.service.inspect("graph", inspection_request)
        doctor_request = GraphDoctorRequest()
        doctored = self.service.doctor("graph", doctor_request)

        values = (
            ("VyralGraphEnvelope", envelope.to_dict()),
            ("VyralGraphCollectionImportRequest", import_request.to_dict()),
            (
                "VyralGraphCollectionImportPreflightResult",
                preflight.to_dict(),
            ),
            ("VyralGraphCollectionImportResult", imported.to_dict()),
            ("VyralGraphCollectionExportRequest", export_request.to_dict()),
            (
                "VyralGraphCollectionExportResult",
                exported.to_dict() if exported is not None else None,
            ),
            ("VyralGraphTraversalRequest", traversal_request.to_dict()),
            (
                "VyralGraphTraversalResult",
                traversed.to_dict() if traversed is not None else None,
            ),
            (
                "VyralGraphCollectionInspectionRequest",
                inspection_request.to_dict(),
            ),
            (
                "VyralGraphCollectionInspectionResult",
                inspected.to_dict() if inspected is not None else None,
            ),
            ("VyralGraphDoctorRequest", doctor_request.to_dict()),
            (
                "VyralGraphDoctorResult",
                doctored.to_dict() if doctored is not None else None,
            ),
        )
        schema = load_contract_bundle().schema
        definitions = schema["$defs"]
        for definition, value in values:
            with self.subTest(definition=definition):
                self.assertIsNotNone(value)
                validator = Draft202012Validator(
                    {
                        "$ref": f"#/$defs/{definition}",
                        "$defs": definitions,
                    }
                )
                errors = sorted(
                    validator.iter_errors(value),
                    key=lambda error: tuple(
                        str(part) for part in error.absolute_path
                    ),
                )
                self.assertEqual(
                    [],
                    [
                        f"{'.'.join(map(str, error.absolute_path))}: "
                        f"{error.message}"
                        for error in errors
                    ],
                )


if __name__ == "__main__":
    unittest.main()
