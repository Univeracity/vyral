from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from jsonschema import Draft202012Validator

from vyral_runtime import (
    EmbeddingOptions,
    GraphImportRequest,
    GraphService,
    LexicalSearchOptions,
    LocalTokenHashEmbeddingProvider,
    RagContextAssemblyOptions,
    RagContextEvaluationCase,
    RagContextEvaluationRequest,
    RagContextExpectedGraph,
    RagContextGraphExpansionOptions,
    RagContextGroupBudget,
    RagContextRequest,
    RagContextService,
    RagIngestTextRequest,
    RagIngestionOptions,
    RagIngestionService,
    RagPromptRequest,
    RagPromptService,
    RagPromptTemplateOptions,
    RecordCollectionPolicy,
    RetrievalRequest,
    RetrievalService,
    SQLiteRecordStore,
    VectorFieldPolicy,
    VyralGraphEdge,
    VyralGraphEnvelope,
    VyralGraphNode,
    VyralGraphScope,
    VyralGraphSourceSpan,
    VyralGraphTraversalProfile,
    load_contract_bundle,
)


class RagContextAndPromptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.store = SQLiteRecordStore(
            Path(self.temporary_directory.name) / "context.sqlite",
            clock=lambda: datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc),
        )
        self.store.create_collection(
            RecordCollectionPolicy(
                name="knowledge",
                vector_policies=(
                    VectorFieldPolicy(
                        name="contentEmbedding",
                        path="/vectors/contentEmbedding/values",
                        dimensions=64,
                    ),
                ),
            )
        )
        provider = LocalTokenHashEmbeddingProvider(dimensions=64)
        ingestion = RagIngestionService(self.store, provider)
        ingestion.ingest_text(
            "knowledge",
            RagIngestTextRequest(
                document_id="official",
                partition_key="tenant-a",
                text=(
                    "The Python runtime supports deterministic local retrieval, "
                    "SQLite records, and citation-ready context assembly."
                ),
                embedding=EmbeddingOptions(field="contentEmbedding"),
                metadata={"graphNodeId": "a"},
                source_uri="https://docs.example.test/runtime",
                source_kind="documentation",
                source_label="Official runtime guide",
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                ),
            ),
        )
        ingestion.ingest_text(
            "knowledge",
            RagIngestTextRequest(
                document_id="discussion",
                partition_key="tenant-a",
                text=(
                    "A community discussion compares Python SDK calls with "
                    "native Python runtime execution."
                ),
                embedding=EmbeddingOptions(field="contentEmbedding"),
                sources=(
                    {
                        "id": "discussion",
                        "kind": "community",
                        "uri": "https://forum.example.test/42",
                        "label": "Runtime discussion",
                        "span": {"line": 12, "charStart": 100, "charEnd": 300},
                    },
                    {
                        "id": "mirror",
                        "kind": "archive",
                        "uri": "https://archive.example.test/42",
                        "label": "Archived copy",
                    },
                ),
                options=RagIngestionOptions(
                    chunk_chars=500,
                    chunk_overlap_chars=0,
                ),
            ),
        )
        self.retrieval = RetrievalService(self.store, provider)
        self.graph = GraphService(self.store)
        self.graph.import_envelope(
            "graph",
            GraphImportRequest(
                VyralGraphEnvelope(
                    scope=VyralGraphScope(graph_id="runtime"),
                    nodes=(
                        VyralGraphNode(
                            "a",
                            "component",
                            "Gateway",
                            source_spans=(
                                VyralGraphSourceSpan("guide", 0, 10),
                            ),
                        ),
                        VyralGraphNode(
                            "b",
                            "component",
                            "Worker",
                            source_spans=(
                                VyralGraphSourceSpan("guide", 11, 20),
                            ),
                        ),
                    ),
                    edges=(
                        VyralGraphEdge(
                            "routes",
                            "a",
                            "b",
                            "routes_to",
                            source_spans=(
                                VyralGraphSourceSpan("guide", 0, 20),
                            ),
                        ),
                    ),
                )
            ),
        )
        self.context_service = RagContextService(
            self.retrieval,
            graph_service=self.graph,
        )
        self.prompt_service = RagPromptService(self.context_service)

    def tearDown(self) -> None:
        self.prompt_service.close()
        self.context_service.close()
        self.graph.close()
        self.retrieval.close()
        self.temporary_directory.cleanup()

    def retrieval_request(self) -> RetrievalRequest:
        return RetrievalRequest(
            query="Python runtime retrieval",
            collections=("knowledge",),
            search_mode="lexical",
            lexical=LexicalSearchOptions(fields=("/content/text",)),
            limit=5,
        )

    def test_context_is_budgeted_cited_traceable_and_hash_stable(self) -> None:
        request = RagContextRequest(
            retrieval=self.retrieval_request(),
            max_chars=120,
            max_chars_per_chunk=70,
            max_citations_per_chunk=1,
            include_records=True,
            include_citations=True,
            include_context_text=True,
            include_trace=True,
        )

        first = self.context_service.build_context(request)
        second = self.context_service.build_context(request)

        self.assertGreaterEqual(len(first.chunks), 1)
        self.assertLessEqual(first.total_chars, 120)
        self.assertTrue(all(len(chunk.text) <= 70 for chunk in first.chunks))
        self.assertTrue(any(chunk.truncated for chunk in first.chunks))
        self.assertTrue(all(chunk.record is not None for chunk in first.chunks))
        self.assertTrue(all(chunk.citation_ids for chunk in first.chunks))
        self.assertEqual(
            first.context_text_hash,
            second.context_text_hash,
        )
        self.assertIsNotNone(first.context_text)
        assert first.context_text is not None
        self.assertTrue(first.context_text.startswith("Context:"))
        self.assertIn("Citations:", first.context_text)
        self.assertIsNotNone(first.trace)
        assert first.trace is not None
        self.assertEqual(len(first.chunks), first.trace["chunkCount"])
        self.assertEqual(len(first.citations), first.trace["citationCount"])
        for citation in first.citations:
            self.assertEqual(
                citation.context_excerpt_hash,
                first.chunks[citation.chunk_rank - 1].context_excerpt_hash,
            )

    def test_citation_cap_tracks_omissions_and_included_span(self) -> None:
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=RetrievalRequest(
                    query="community discussion",
                    collections=("knowledge",),
                    search_mode="lexical",
                    limit=1,
                ),
                max_citations_per_chunk=1,
                include_citations=True,
                include_context_text=True,
            )
        )

        self.assertEqual(1, len(context.chunks))
        self.assertEqual(1, len(context.citations))
        self.assertEqual(1, context.omitted_citation_count)
        citation = context.citations[0]
        self.assertIsNotNone(citation.included_source_span)
        assert citation.included_source_span is not None
        self.assertEqual(0, citation.included_source_span["charStart"])
        self.assertGreater(citation.included_source_span["charEnd"], 0)
        self.assertEqual(12, citation.included_source_span["line"])

    def test_group_authority_ordering_and_required_group_enforcement(self) -> None:
        assembly = RagContextAssemblyOptions(
            group_by="sourceKind",
            default_max_chunks_per_group=1,
            groups=(
                RagContextGroupBudget(
                    key="documentation",
                    priority=0,
                    required=True,
                    min_chunks=1,
                ),
                RagContextGroupBudget(
                    key="community",
                    priority=1,
                    max_chunks=1,
                ),
            ),
        )
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=self.retrieval_request(),
                context_assembly=assembly,
                include_trace=True,
            )
        )

        self.assertEqual("documentation", context.chunks[0].group_key)
        self.assertLessEqual(
            sum(chunk.group_key == "documentation" for chunk in context.chunks),
            1,
        )
        self.assertIsNotNone(context.trace)
        assert context.trace is not None
        self.assertTrue(
            context.trace["groupStats"]["documentation"]["satisfied"]
        )

        with self.assertRaisesRegex(ValueError, "required groups"):
            self.context_service.build_context(
                RagContextRequest(
                    retrieval=self.retrieval_request(),
                    context_assembly=RagContextAssemblyOptions(
                        group_by="sourceKind",
                        fail_on_unsatisfied_required_groups=True,
                        groups=(
                            RagContextGroupBudget(
                                key="regulatory",
                                required=True,
                            ),
                        ),
                    ),
                )
            )

    def test_context_can_hide_citations_while_rendering_prompt_markers(self) -> None:
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=self.retrieval_request(),
                include_citations=False,
                include_context_text=True,
            )
        )

        self.assertEqual((), context.citations)
        self.assertTrue(context.chunks[0].citation_ids)
        self.assertIsNotNone(context.context_text)
        assert context.context_text is not None
        self.assertIn("[c1]", context.context_text)
        self.assertIn("Citations:", context.context_text)

    def test_prompt_assembly_is_deterministic_and_enforces_limits(self) -> None:
        request = RagPromptRequest(
            context=RagContextRequest(
                retrieval=self.retrieval_request(),
                include_trace=True,
            ),
            template=RagPromptTemplateOptions(
                user_instruction="What does the Python runtime support?",
            ),
        )
        first = self.prompt_service.build_prompt(request)
        second = self.prompt_service.build_prompt(request)

        self.assertEqual(first.prompt_hash, second.prompt_hash)
        self.assertEqual(("system", "user"), tuple(item.role for item in first.messages))
        self.assertTrue(first.prompt.startswith("SYSTEM:"))
        self.assertIn("Citation rule:", first.messages[1].content)
        self.assertIn("Citations:", first.messages[1].content)
        self.assertIsNotNone(first.trace)

        with self.assertRaisesRegex(ValueError, "exceeds maxPromptChars"):
            self.prompt_service.build_prompt(
                RagPromptRequest(
                    context=request.context,
                    template=RagPromptTemplateOptions(max_prompt_chars=20),
                )
            )
        with self.assertRaisesRegex(ValueError, "not supported"):
            self.prompt_service.build_prompt(
                RagPromptRequest(
                    context=request.context,
                    template=RagPromptTemplateOptions(format="plain-text"),
                )
            )

    def test_graph_expansion_adds_bounded_context_and_provenance(self) -> None:
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=self.retrieval_request(),
                graph_expansion=RagContextGraphExpansionOptions(
                    collection="graph",
                    graph_id="runtime",
                    max_graph_context_chars=500,
                ),
                include_context_text=True,
                include_trace=True,
            )
        )

        self.assertIsNotNone(context.graph_context)
        self.assertIsNotNone(context.graph_expansion)
        assert context.graph_context is not None
        assert context.graph_expansion is not None
        self.assertEqual("succeeded", context.graph_context["status"])
        self.assertEqual("a", context.graph_context["seedNodeIds"][0])
        self.assertEqual(3, len(context.graph_context["seedNodeIds"]))
        self.assertEqual(2, context.graph_context["nodeCount"])
        self.assertEqual(1, context.graph_context["edgeCount"])
        self.assertEqual(3, len(context.graph_context["provenance"]))
        self.assertEqual("succeeded", context.graph_expansion["status"])
        self.assertTrue(
            context.graph_expansion["graphContextInfluencedContextText"]
        )
        self.assertIn("Graph context:", context.context_text or "")
        self.assertIsNotNone(context.trace)
        assert context.trace is not None
        self.assertEqual(
            "succeeded",
            context.trace["graphExpansion"]["status"],
        )

    def test_graph_expansion_fallback_reports_missing_graph(self) -> None:
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=self.retrieval_request(),
                graph_expansion=RagContextGraphExpansionOptions(
                    collection="missing-graph",
                    seed_node_ids=("a",),
                ),
            )
        )
        self.assertIsNotNone(context.graph_context)
        assert context.graph_context is not None
        self.assertEqual("graph_not_found", context.graph_context["status"])

        with self.assertRaisesRegex(ValueError, "graph expansion failed"):
            self.context_service.build_context(
                RagContextRequest(
                    retrieval=self.retrieval_request(),
                    graph_expansion=RagContextGraphExpansionOptions(
                        collection="missing-graph",
                        seed_node_ids=("a",),
                        fallback_on_failure=False,
                    ),
                )
            )

    def test_graph_seed_diagnostics_are_bounded_with_exact_omission_count(
        self,
    ) -> None:
        context = self.context_service.build_context(
            RagContextRequest(
                retrieval=self.retrieval_request(),
                graph_expansion=RagContextGraphExpansionOptions(
                    collection="graph",
                    graph_id="runtime",
                    seed_node_ids=("a",) * 205,
                    seed_json_pointers=(),
                ),
            )
        )

        self.assertIsNotNone(context.graph_context)
        assert context.graph_context is not None
        self.assertEqual(200, len(context.graph_context["seedDiagnostics"]))
        self.assertEqual(
            5,
            context.graph_context["omittedSeedDiagnosticCount"],
        )
        self.assertEqual(0, context.graph_context["droppedSeedCount"])

    def test_context_evaluation_measures_graph_quality_and_failures(self) -> None:
        evaluation_case = RagContextEvaluationCase(
            name="routing",
            metadata={"queryId": "runtime-routing"},
            request=RagContextRequest(
                retrieval=self.retrieval_request(),
                graph_expansion=RagContextGraphExpansionOptions(
                    collection="graph",
                    graph_id="runtime",
                ),
                include_context_text=True,
            ),
            expected_graph=RagContextExpectedGraph(
                node_ids=("a", "b"),
                edge_ids=("routes",),
                provenance_entity_ids=("a", "routes"),
                require_source_grounded_provenance=True,
                require_graph_context_text=True,
                require_context_text_not_truncated=True,
            ),
        )
        result = self.context_service.evaluate_context(
            RagContextEvaluationRequest(
                cases=(evaluation_case,),
                include_context=True,
            )
        )

        self.assertEqual(
            (1, 1, 0),
            (result.attempted, result.succeeded, result.failed),
        )
        self.assertEqual(1.0, result.pass_rate)
        self.assertEqual(1.0, result.node_hit_rate)
        self.assertEqual(1.0, result.edge_hit_rate)
        self.assertEqual(1.0, result.provenance_hit_rate)
        case = result.cases[0]
        self.assertEqual("runtime-routing", case.query_id)
        self.assertEqual(("a", "b"), case.graph_expanded_node_ids)
        self.assertEqual(("routes",), case.graph_expanded_edge_ids)
        self.assertEqual(3, case.graph_contribution_count)
        self.assertIsNotNone(case.context)

        missed = self.context_service.evaluate_context(
            RagContextEvaluationRequest(
                cases=(
                    RagContextEvaluationCase(
                        request=evaluation_case.request,
                        expected_graph=RagContextExpectedGraph(
                            node_ids=("missing",)
                        ),
                    ),
                )
            )
        )
        self.assertEqual(0.0, missed.pass_rate)
        self.assertEqual(
            ("expected_node_missing",),
            missed.cases[0].failure_categories,
        )

        asynchronous = asyncio.run(
            self.context_service.aevaluate_context(
                RagContextEvaluationRequest(cases=(evaluation_case,))
            )
        )
        self.assertEqual(1.0, asynchronous.pass_rate)

        schema = load_contract_bundle().schema
        definitions = schema["$defs"]
        evaluation_request = RagContextEvaluationRequest(
            cases=(evaluation_case,),
            include_context=True,
        )
        values = (
            ("RagContextRequest", evaluation_case.request.to_dict()),
            (
                "RagContextEnvelope",
                result.cases[0].context.to_dict()
                if result.cases[0].context is not None
                else None,
            ),
            (
                "RagContextEvaluationRequest",
                evaluation_request.to_dict(),
            ),
            ("RagContextEvaluationResult", result.to_dict()),
        )
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

    def test_failed_context_evaluation_resolves_graph_profile_name(self) -> None:
        result = self.context_service.evaluate_context(
            RagContextEvaluationRequest(
                cases=(
                    RagContextEvaluationCase(
                        name="invalid-budget",
                        request=RagContextRequest(
                            retrieval=self.retrieval_request(),
                            graph_expansion=RagContextGraphExpansionOptions(
                                collection="graph",
                                profile=VyralGraphTraversalProfile(
                                    id="graph-quality"
                                ),
                                max_graph_context_chars=0,
                            ),
                        ),
                    ),
                )
            )
        )

        self.assertEqual("failed", result.cases[0].status)
        self.assertEqual("graph-quality", result.cases[0].profile_name)
        self.assertEqual(("case_error",), result.cases[0].failure_categories)


if __name__ == "__main__":
    unittest.main()
