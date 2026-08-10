from __future__ import annotations

import math
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import TypeVar
import unittest

from vyral_runtime import (
    FilterNode,
    OrderExpression,
    QueryEnvelope,
    QueryValidationError,
    RecordCollectionPolicy,
    RecordValidationError,
    SQLiteRecordStore,
    VectorFieldPolicy,
    VectorSearchOptions,
    VyralRecord,
    VyralVector,
)

T = TypeVar("T")


class SQLiteQueryAndSearchTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "query.sqlite"
        self.store = SQLiteRecordStore(self.database_path)
        self.store.create_collection(
            RecordCollectionPolicy(
                name="items",
                vector_policies=(
                    VectorFieldPolicy(
                        name="cosine",
                        path="/vectors/cosine/values",
                        dimensions=2,
                    ),
                    VectorFieldPolicy(
                        name="dot",
                        path="/vectors/dot/values",
                        dimensions=2,
                        distance_function="dotProduct",
                    ),
                    VectorFieldPolicy(
                        name="distance",
                        path="/vectors/distance/values",
                        dimensions=2,
                        distance_function="euclidean",
                    ),
                ),
                indexed_metadata=(
                    "/metadata/status",
                    "/metadata/score",
                    "/metadata/nullable",
                ),
            )
        )

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def upsert(
        self,
        record_id: str,
        *,
        partition_key: str = "tenant-a",
        status: str = "active",
        score: int = 0,
        nullable: object = "missing",
        title: str = "",
        vector: tuple[float, float] | None = None,
        vector_field: str = "cosine",
        distance_function: str = "cosine",
    ) -> None:
        metadata: dict[str, object] = {"status": status, "score": score}
        if nullable != "missing":
            metadata["nullable"] = nullable
        vectors = (
            {
                vector_field: VyralVector(
                    values=vector,
                    distance_function=distance_function,
                )
            }
            if vector is not None
            else None
        )
        self.store.upsert_record(
            "items",
            VyralRecord(
                id=record_id,
                partition_key=partition_key,
                metadata=metadata,
                content={"title": title},
                vectors=vectors,
            ),
        )

    def test_compound_filters_partition_scope_and_indexed_order(self) -> None:
        self.upsert("one", status="active", score=10, title="retrieval guide")
        self.upsert("two", status="inactive", score=50, title="retrieval draft")
        self.upsert(
            "three",
            partition_key="tenant-b",
            status="preview",
            score=20,
            title="runtime guide",
        )
        self.upsert("four", status="active", score=5, title="unrelated")

        result = self.store.query_records(
            "items",
            QueryEnvelope(
                filter=FilterNode(
                    combine="all",
                    children=(
                        FilterNode(
                            path="/metadata/status",
                            op="in",
                            value=["active", "preview"],
                        ),
                        FilterNode(path="/metadata/score", op="gte", value=7),
                        FilterNode(path="/content/title", op="contains", value="guide"),
                    ),
                ),
                order_by=(
                    OrderExpression(path="/metadata/score", direction="desc"),
                ),
            ),
        )

        self.assertEqual(("three", "one"), tuple(record.id for record in result))
        scoped = self.store.query_records(
            "items",
            {
                "partitionKeys": ["tenant-a"],
                "filter": {
                    "path": "/metadata/status",
                    "op": "eq",
                    "value": "active",
                },
                "orderBy": [{"path": "/id"}],
            },
        )
        self.assertEqual(("four", "one"), tuple(record.id for record in scoped))

    def test_null_and_missing_have_distinct_filter_semantics(self) -> None:
        self.upsert("null", nullable=None)
        self.upsert("missing")

        exists = self.store.query_records(
            "items",
            {"filter": {"path": "/metadata/nullable", "op": "exists", "value": True}},
        )
        eq_null = self.store.query_records(
            "items",
            {"filter": {"path": "/metadata/nullable", "op": "eq", "value": None}},
        )
        missing = self.store.query_records(
            "items",
            {
                "filter": {
                    "path": "/metadata/nullable",
                    "op": "exists",
                    "value": False,
                }
            },
        )

        self.assertEqual(("null",), tuple(record.id for record in exists))
        self.assertEqual(("null",), tuple(record.id for record in eq_null))
        self.assertEqual(("missing",), tuple(record.id for record in missing))

    def test_pagination_tokens_match_the_portable_offset_format(self) -> None:
        for record_id in ("a", "b", "c", "d", "e"):
            self.upsert(record_id)

        first = self.store.query_records_page("items", {"limit": 2})
        second = self.store.query_records_page(
            "items",
            {"limit": 2, "continuationToken": first.continuation_token},
        )
        third = self.store.query_records_page(
            "items",
            {"limit": 2, "continuationToken": second.continuation_token},
        )

        self.assertEqual(("a", "b"), tuple(item.id for item in first.items))
        self.assertEqual("Mg==", first.continuation_token)
        self.assertEqual(("c", "d"), tuple(item.id for item in second.items))
        self.assertEqual("NA==", second.continuation_token)
        self.assertEqual(("e",), tuple(item.id for item in third.items))
        self.assertIsNone(third.continuation_token)

    def test_invalid_queries_fail_before_returning_data(self) -> None:
        self.upsert("one")
        invalid_queries = (
            {"limit": 0},
            {"continuationToken": "not base64!"},
            {"filter": {"path": "metadata/status", "op": "eq", "value": "active"}},
            {"filter": {"path": "/metadata/status", "op": "wat", "value": "active"}},
            {
                "filter": {
                    "path": "/metadata/status",
                    "op": "eq",
                    "value": {"nested": True},
                }
            },
            {
                "filter": {
                    "path": "/metadata/status",
                    "op": "contains",
                    "value": 3,
                }
            },
            {"orderBy": [{"path": "/id", "direction": "sideways"}]},
        )

        for query in invalid_queries:
            with self.subTest(query=query):
                with self.assertRaises(QueryValidationError):
                    self.store.query_records("items", query)

    def test_cosine_vector_search_filters_ranks_and_pages_stably(self) -> None:
        self.upsert("best-b", partition_key="tenant-b", vector=(1.0, 0.0), score=10)
        self.upsert("best-a", vector=(1.0, 0.0), score=10)
        self.upsert("near", vector=(0.8, 0.2), score=8)
        self.upsert("opposite", vector=(-1.0, 0.0), score=10)
        self.upsert("inactive", status="inactive", vector=(1.0, 0.0), score=10)

        first = self.store.search_records_page(
            "items",
            QueryEnvelope(
                filter=FilterNode(path="/metadata/status", op="eq", value="active"),
                vector=VectorSearchOptions(
                    field="cosine",
                    value=(1.0, 0.0),
                    top=4,
                ),
                limit=2,
            ),
        )
        second = self.store.search_records_page(
            "items",
            QueryEnvelope(
                filter=FilterNode(path="/metadata/status", op="eq", value="active"),
                vector=VectorSearchOptions(
                    field="cosine",
                    value=(1.0, 0.0),
                    top=4,
                ),
                limit=2,
                continuation_token=first.continuation_token,
            ),
        )

        self.assertEqual(("best-a", "best-b"), tuple(item.record.id for item in first.items))
        self.assertEqual(("near", "opposite"), tuple(item.record.id for item in second.items))
        self.assertAlmostEqual(1.0, first.items[0].score, places=6)
        self.assertAlmostEqual(-1.0, second.items[1].score, places=6)
        self.assertEqual("Mg==", first.continuation_token)
        diagnostics = first.items[0].diagnostics
        self.assertIsNotNone(diagnostics)
        if diagnostics is not None:
            self.assertEqual(1, diagnostics.details["rank"])
            self.assertEqual(4, diagnostics.candidate_counts["searchCandidatePool"])
            self.assertIn("rank.tie_break.applied", diagnostics.reason_codes)

    def test_dot_product_and_euclidean_use_policy_distance(self) -> None:
        self.upsert(
            "dot-small",
            vector=(1.0, 0.0),
            vector_field="dot",
            distance_function="dotProduct",
        )
        self.upsert(
            "dot-large",
            vector=(10.0, 0.0),
            vector_field="dot",
            distance_function="dotProduct",
        )
        dot = self.store.search_records(
            "items",
            {
                "vector": {"field": "dot", "value": [1.0, 0.0], "top": 2},
            },
        )
        self.assertEqual(("dot-large", "dot-small"), tuple(item.record.id for item in dot))
        self.assertEqual((10.0, 1.0), tuple(item.score for item in dot))

        self.upsert(
            "close",
            vector=(1.2, 0.0),
            vector_field="distance",
            distance_function="euclidean",
        )
        self.upsert(
            "far",
            vector=(10.0, 0.0),
            vector_field="distance",
            distance_function="euclidean",
        )
        distance = self.store.search_records(
            "items",
            {
                "vector": {
                    "field": "distance",
                    "value": [1.0, 0.0],
                    "top": 2,
                }
            },
        )
        self.assertEqual(("close", "far"), tuple(item.record.id for item in distance))
        self.assertGreater(distance[0].score, distance[1].score)

    def test_vector_search_rejects_invalid_values_and_dimensions(self) -> None:
        self.upsert("one", vector=(1.0, 0.0))

        with self.assertRaises(RecordValidationError):
            self.store.search_records(
                "items",
                {"vector": {"field": "cosine", "value": [1.0], "top": 2}},
            )
        with self.assertRaises(ValueError):
            self.store.search_records(
                "items",
                {
                    "vector": {
                        "field": "cosine",
                        "value": [math.inf, 0.0],
                        "top": 2,
                    }
                },
            )
        with self.assertRaises(QueryValidationError):
            self.store.search_records(
                "items",
                {"vector": {"field": "cosine", "value": [1.0, 0.0], "top": 0}},
            )

    def test_lexical_search_uses_fts_candidates_fields_and_diagnostics(self) -> None:
        for index in range(12):
            self.upsert(
                f"noise-{index:02d}",
                title="Routine scheduling background without the target phrase.",
            )
        self.store.upsert_record(
            "items",
            VyralRecord(
                id="zz-target",
                partition_key="tenant-a",
                metadata={"status": "active", "referenceId": "RECORD-000123"},
                content={
                    "text": (
                        "RECORD-000123 contains raretokenalpha and an exact update deadline."
                    )
                },
            ),
        )

        matches = self.store.search_records(
            "items",
            {
                "filter": {
                    "path": "/metadata/status",
                    "op": "eq",
                    "value": "active",
                },
                "lexical": {
                    "query": "RECORD-000123 update deadline raretokenalpha",
                    "fields": ["/content/text", "/metadata/referenceId"],
                    "scanLimit": 3,
                    "top": 5,
                },
                "limit": 5,
            },
        )

        match = self.assert_single(matches)
        self.assertEqual("zz-target", match.record.id)
        self.assertGreater(match.score, 0)
        self.assertIsNotNone(match.diagnostics)
        if match.diagnostics is not None:
            self.assertEqual(
                "sqlite_fts5",
                match.diagnostics.details["lexicalCandidateSource"],
            )
            self.assertIn("/metadata/referenceId", match.diagnostics.matched_fields)
            self.assertEqual(
                "lexical.bm25",
                match.diagnostics.score_normalization["lexicalScoreKind"],  # type: ignore[index]
            )
            self.assertIn("candidate.source.lexical", match.diagnostics.reason_codes)

    def test_lexical_phrase_prefix_and_required_groups_preserve_scalar_boundaries(self) -> None:
        self.store.upsert_record(
            "items",
            VyralRecord(
                id="a-split",
                partition_key="tenant-a",
                content={
                    "aliases": [
                        "browser network",
                        "diagnostics access",
                        "latency",
                    ]
                },
            ),
        )
        self.store.upsert_record(
            "items",
            VyralRecord(
                id="z-target",
                partition_key="tenant-a",
                content={
                    "aliases": [
                        "browser network diagnostics",
                        "access latency",
                        "preliminary injunction deadline",
                    ]
                },
            ),
        )

        required = self.store.search_records(
            "items",
            {
                "lexical": {
                    "query": "browser diagnostics latency",
                    "fields": ["/content/aliases"],
                    "requiredPhraseGroups": [
                        ["browser network diagnostics"],
                        ["loaded latency", "access latency"],
                    ],
                    "scanLimit": 1,
                    "top": 5,
                }
            },
        )
        self.assertEqual("z-target", self.assert_single(required).record.id)

        prefix = self.store.search_records(
            "items",
            {
                "lexical": {
                    "query": "prelim injunc deadl",
                    "fields": ["/content/aliases"],
                    "matchMode": "all",
                    "prefixMatching": True,
                    "prefixMinChars": 3,
                    "scanLimit": 1,
                    "top": 5,
                }
            },
        )
        match = self.assert_single(prefix)
        self.assertEqual("z-target", match.record.id)
        if match.diagnostics is not None:
            self.assertEqual(
                "prelim* AND injunc* AND deadl*",
                match.diagnostics.details["lexicalFtsExpression"],
            )
            self.assertEqual(
                ["deadl", "injunc", "prelim"],
                match.diagnostics.details["matchedPrefixTerms"],
            )

        phrase = self.store.search_records(
            "items",
            {
                "lexical": {
                    "query": '"preliminary injunction"',
                    "fields": ["/content/aliases"],
                    "top": 5,
                }
            },
        )
        phrase_match = self.assert_single(phrase)
        self.assertGreater(
            phrase_match.diagnostics.score_components["phraseBoost"],  # type: ignore[union-attr]
            0,
        )

    def test_lexical_options_reject_unbounded_or_ambiguous_shapes(self) -> None:
        self.upsert("one", title="browser diagnostics")
        invalid = (
            {"lexical": {"query": "", "top": 5}},
            {"lexical": {"query": "browser", "top": 0}},
            {"lexical": {"query": "browser", "scanLimit": 0}},
            {"lexical": {"query": "browser", "scoring": "mystery"}},
            {"lexical": {"query": "browser", "matchMode": "maybe"}},
            {"lexical": {"query": "browser", "bm25B": 2}},
            {
                "lexical": {
                    "query": "browser",
                    "requiredPhraseGroups": [[" "]],
                }
            },
        )
        for query in invalid:
            with self.subTest(query=query):
                with self.assertRaises(QueryValidationError):
                    self.store.search_records("items", query)

    def test_vector_and_lexical_cannot_be_combined_at_store_layer(self) -> None:
        self.upsert("one", title="browser", vector=(1.0, 0.0))
        with self.assertRaisesRegex(QueryValidationError, "retrieval service"):
            self.store.search_records(
                "items",
                {
                    "vector": {
                        "field": "cosine",
                        "value": [1.0, 0.0],
                    },
                    "lexical": {"query": "browser"},
                },
            )

    def assert_single(self, values: tuple[T, ...]) -> T:
        self.assertEqual(1, len(values))
        return values[0]


if __name__ == "__main__":
    unittest.main()
