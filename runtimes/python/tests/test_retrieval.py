from __future__ import annotations

import asyncio
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from vyral_runtime import (
    EmbeddingOptions,
    HybridSearchOptions,
    LexicalSearchOptions,
    LocalTokenHashEmbeddingProvider,
    RecordCollectionPolicy,
    RerankOptions,
    RetrievalRequest,
    RetrievalService,
    RetrievalVectorFieldQuery,
    SQLiteRecordStore,
    SQLiteTraceStore,
    VectorFieldPolicy,
    VyralRecord,
    VyralVector,
    apply_retrieval_profile,
)


class RetrievalServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.provider = LocalTokenHashEmbeddingProvider(dimensions=64)
        self.database_path = (
            Path(self.temporary_directory.name) / "retrieval.sqlite"
        )
        self.store = SQLiteRecordStore(self.database_path)
        for collection in ("docs-a", "docs-b"):
            self.store.create_collection(
                RecordCollectionPolicy(
                    name=collection,
                    vector_policies=(
                        VectorFieldPolicy(
                            name="contentEmbedding",
                            path="/vectors/contentEmbedding/values",
                            dimensions=64,
                        ),
                        VectorFieldPolicy(
                            name="titleEmbedding",
                            path="/vectors/titleEmbedding/values",
                            dimensions=64,
                        ),
                    ),
                    indexed_metadata=("/metadata/topic",),
                )
            )
        self.traces = SQLiteTraceStore(self.database_path)
        self.service = RetrievalService(
            self.store,
            self.provider,
            trace_store=self.traces,
        )

    def tearDown(self) -> None:
        self.service.close()
        self.traces.close()
        self.temporary_directory.cleanup()

    def upsert(
        self,
        collection: str,
        record_id: str,
        text: str,
        *,
        title: str | None = None,
        topic: str = "runtime",
        partition_key: str = "tenant-a",
    ) -> None:
        selected_title = title or text
        self.store.upsert_record(
            collection,
            VyralRecord(
                id=record_id,
                partition_key=partition_key,
                type="rag-chunk",
                metadata={"topic": topic, "title": selected_title},
                content={"text": text, "title": selected_title},
                vectors={
                    "contentEmbedding": VyralVector(
                        values=self.provider.generate_embedding(text),
                        model=self.provider.model_id,
                        source_field="/content/text",
                    ),
                    "titleEmbedding": VyralVector(
                        values=self.provider.generate_embedding(selected_title),
                        model=self.provider.model_id,
                        source_field="/content/title",
                    ),
                },
            ),
        )

    def seed(self) -> None:
        self.upsert(
            "docs-a",
            "python-runtime",
            "Python runtime framework with durable handlers and local storage",
            title="Python runtime",
        )
        self.upsert(
            "docs-a",
            "unrelated",
            "Astronomy telescope observations of a distant spiral galaxy",
            topic="science",
        )
        self.upsert(
            "docs-b",
            "mcp-routing",
            "Stateless MCP requests use self describing routing authorization headers",
            title="MCP gateway routing",
        )
        self.upsert(
            "docs-b",
            "python-sdk",
            "Python SDK package for calling the Vyral HTTP service",
            title="Python SDK",
        )

    def test_lexical_search_spans_collections_and_has_stable_ties(self) -> None:
        self.upsert("docs-b", "b", "identical portable retrieval")
        self.upsert("docs-a", "a", "identical portable retrieval")

        result = self.service.search(
            RetrievalRequest(
                query="identical portable retrieval",
                collections=("docs-b", "docs-a"),
                search_mode="lexical",
                lexical=LexicalSearchOptions(fields=("/content/text",)),
                limit=2,
                include_trace=True,
            )
        )

        self.assertEqual(
            (("docs-a", "a"), ("docs-b", "b")),
            tuple((item.collection, item.record.id) for item in result.results),
        )
        self.assertEqual((1, 2), tuple(item.rank for item in result.results))
        self.assertIsNotNone(result.trace)
        assert result.trace is not None
        self.assertTrue(result.trace["tracePersisted"])
        self.assertTrue(
            all(
                item.diagnostics is not None
                and "rank.tie_break.applied" in item.diagnostics.reason_codes
                for item in result.results
            )
        )

    def test_vector_and_hybrid_modes_rank_semantic_candidates(self) -> None:
        self.seed()

        vector = self.service.search(
            {
                "query": "gateway authorization routing headers",
                "collections": ["docs-a", "docs-b"],
                "embedding": {"field": "contentEmbedding", "purpose": "query"},
                "limit": 2,
                "includeTrace": True,
            }
        )
        hybrid = self.service.search(
            RetrievalRequest(
                query="Python runtime local storage",
                collections=("docs-a", "docs-b"),
                search_mode="hybrid",
                embedding=EmbeddingOptions(field="contentEmbedding", purpose="query"),
                lexical=LexicalSearchOptions(fields=("/content/text",)),
                hybrid=HybridSearchOptions(
                    vector_weight=0.4,
                    lexical_weight=0.6,
                    fusion="weighted",
                ),
                limit=3,
                include_trace=True,
            )
        )

        self.assertEqual("mcp-routing", vector.results[0].record.id)
        self.assertEqual("python-runtime", hybrid.results[0].record.id)
        hybrid_diagnostics = hybrid.results[0].diagnostics
        self.assertIsNotNone(hybrid_diagnostics)
        assert hybrid_diagnostics is not None
        self.assertEqual(
            {"lexical", "vector"},
            set(hybrid_diagnostics.candidate_sources),
        )
        self.assertIn(
            "fusion.weighted",
            hybrid_diagnostics.reason_codes,
        )

    def test_rrf_multi_vector_fusion_and_partition_filtering(self) -> None:
        self.upsert(
            "docs-a",
            "content-wins",
            "durable Python execution runtime",
            title="miscellaneous notes",
        )
        self.upsert(
            "docs-a",
            "title-wins",
            "miscellaneous notes",
            title="durable Python execution runtime",
        )
        self.upsert(
            "docs-a",
            "other-tenant",
            "durable Python execution runtime",
            partition_key="tenant-b",
        )

        result = self.service.search(
            RetrievalRequest(
                query="durable Python execution runtime",
                collections=("docs-a",),
                partition_keys=("tenant-a",),
                search_mode="hybrid",
                lexical=LexicalSearchOptions(fields=("/content/text",)),
                vector_fields=(
                    RetrievalVectorFieldQuery(
                        field="contentEmbedding",
                        weight=3.0,
                    ),
                    RetrievalVectorFieldQuery(
                        field="titleEmbedding",
                        weight=1.0,
                    ),
                ),
                hybrid=HybridSearchOptions(fusion="rrf", rrf_k=20),
                limit=3,
                include_trace=True,
            )
        )

        self.assertEqual("content-wins", result.results[0].record.id)
        self.assertNotIn(
            "other-tenant",
            {item.record.id for item in result.results},
        )
        diagnostics = result.results[0].diagnostics
        self.assertIsNotNone(diagnostics)
        assert diagnostics is not None
        self.assertEqual(2, diagnostics.details["vectorFieldCount"])
        self.assertIn("fusion.multi_vector", diagnostics.reason_codes)
        self.assertIn("fusion.rrf", diagnostics.reason_codes)

    def test_profiles_reranking_and_async_facade(self) -> None:
        self.seed()
        baseline = apply_retrieval_profile(
            RetrievalRequest(
                profile="ragBaseline",
                query="Python runtime",
                collections=("docs-a", "docs-b"),
            )
        )
        self.assertEqual("lexical", baseline.search_mode)
        self.assertEqual(8, baseline.limit)
        self.assertTrue(baseline.include_trace)

        reranked = self.service.search(
            RetrievalRequest(
                query="Python durable runtime",
                collections=("docs-a", "docs-b"),
                search_mode="lexical",
                rerank=RerankOptions(
                    enabled=True,
                    candidate_limit=4,
                    rerank_score_weight=0.8,
                    original_score_weight=0.2,
                ),
                limit=2,
                include_trace=True,
            )
        )
        asynchronous = asyncio.run(
            self.service.asearch(
                RetrievalRequest(
                    query="Python durable runtime",
                    collections=("docs-a", "docs-b"),
                    search_mode="lexical",
                    limit=2,
                )
            )
        )

        self.assertEqual("python-runtime", reranked.results[0].record.id)
        rerank_diagnostics = reranked.results[0].diagnostics
        self.assertIsNotNone(rerank_diagnostics)
        assert rerank_diagnostics is not None
        self.assertIn(
            "rerank.applied",
            rerank_diagnostics.reason_codes,
        )
        self.assertEqual(
            tuple(item.record.id for item in reranked.results),
            tuple(item.record.id for item in asynchronous.results),
        )

    def test_semantic_validation_fails_before_search(self) -> None:
        self.seed()
        invalid_requests: tuple[RetrievalRequest | dict[str, object], ...] = (
            {"query": "", "collections": ["docs-a"]},
            {"query": "x", "collections": []},
            {"query": "x", "collections": ["docs-a"], "limit": 0},
            {
                "query": "x",
                "collections": ["docs-a"],
                "searchMode": "unknown",
            },
            RetrievalRequest(
                query="x",
                collections=("docs-a",),
                search_mode="vector",
                vector_fields=(
                    RetrievalVectorFieldQuery(field="contentEmbedding", weight=0),
                ),
            ),
            RetrievalRequest(
                query="x",
                collections=("docs-a",),
                hybrid=HybridSearchOptions(
                    vector_weight=0,
                    lexical_weight=0,
                ),
            ),
            RetrievalRequest(
                query="x",
                collections=("docs-a",),
                rerank=RerankOptions(timeout_seconds=0),
            ),
        )
        for request in invalid_requests:
            with self.subTest(request=request):
                with self.assertRaises((TypeError, ValueError)):
                    self.service.search(request)

        wrong_dimensions = RetrievalService(
            self.store,
            LocalTokenHashEmbeddingProvider(dimensions=32),
        )
        with self.assertRaisesRegex(ValueError, "expects a different size"):
            wrong_dimensions.search(
                {
                    "query": "x",
                    "collections": ["docs-a"],
                    "embedding": {"field": "contentEmbedding"},
                }
            )


if __name__ == "__main__":
    unittest.main()
