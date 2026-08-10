from __future__ import annotations

import asyncio
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any
import unittest

from vyral_runtime import (
    LexicalSearchOptions,
    RecordCollectionPolicy,
    RetrievalEvaluationCase,
    RetrievalEvaluationComparisonRequest,
    RetrievalEvaluationExpectedMatch,
    RetrievalEvaluationHardNegativeMatch,
    RetrievalEvaluationRequest,
    RetrievalEvaluationResult,
    RetrievalEvaluationService,
    RetrievalEvaluationVariant,
    RetrievalRequest,
    RetrievalService,
    SQLiteRecordStore,
    VyralRecord,
)


class RetrievalEvaluationServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.store = SQLiteRecordStore(
            Path(self.temporary_directory.name) / "evaluation.sqlite"
        )
        self.store.create_collection(
            RecordCollectionPolicy(name="docs", indexed_metadata=())
        )
        self.store.upsert_record(
            "docs",
            VyralRecord(
                id="expected",
                partition_key="tenant-a",
                metadata={"aliases": ["expected-alias"]},
                content={"text": "alpha runtime protocol routing"},
                sources=(
                    {
                        "id": "source-a",
                        "kind": "guide",
                        "uri": "vyral://guide/a",
                        "span": {"charStart": 0, "charEnd": 40},
                    },
                ),
            ),
        )
        self.store.upsert_record(
            "docs",
            VyralRecord(
                id="hard-negative",
                partition_key="tenant-a",
                content={"text": "alpha runtime unrelated"},
            ),
        )
        self.retrieval = RetrievalService(self.store)
        self.evaluation = RetrievalEvaluationService(self.retrieval)

    def tearDown(self) -> None:
        self.evaluation.close()
        self.retrieval.close()
        self.temporary_directory.cleanup()

    def case(self) -> RetrievalEvaluationCase:
        return RetrievalEvaluationCase(
            name="runtime-routing",
            request=RetrievalRequest(
                query="alpha runtime protocol",
                collections=("docs",),
                search_mode="lexical",
                lexical=LexicalSearchOptions(fields=("/content/text",)),
                limit=2,
            ),
            expected=(
                RetrievalEvaluationExpectedMatch(
                    aliases=("expected-alias",),
                    relevance=2.0,
                ),
            ),
            hard_negatives=(
                RetrievalEvaluationHardNegativeMatch(
                    id="hard-negative",
                    reason="shares generic runtime terms",
                ),
            ),
            k=2,
        )

    def test_metrics_expected_aliases_hard_negatives_and_top_results(self) -> None:
        progress: list[RetrievalEvaluationResult] = []
        result = self.evaluation.evaluate(
            RetrievalEvaluationRequest(cases=(self.case(),)),
            progress=progress.append,
        )

        self.assertEqual((1, 1, 0), (result.attempted, result.succeeded, result.failed))
        self.assertEqual(1.0, result.hit_rate)
        self.assertEqual(1.0, result.mean_reciprocal_rank)
        self.assertEqual(0.5, result.mean_precision_at_k)
        self.assertEqual(1.0, result.mean_recall_at_k)
        self.assertEqual(1.0, result.mean_ndcg_at_k)
        self.assertEqual(1.0, result.hard_negative_hit_rate)
        case = result.cases[0]
        self.assertEqual(1, case.expected[0].rank)
        self.assertEqual(2, case.hard_negatives[0].rank)
        self.assertTrue(case.top_results[0].matched_expected)
        self.assertTrue(case.top_results[1].matched_hard_negative)
        self.assertGreaterEqual(len(progress), 3)
        document: Any = result.to_dict()
        self.assertEqual(1.0, document["meanReciprocalRank"])
        self.assertEqual("runtime-routing", document["cases"][0]["name"])
        json.dumps(document)

    def test_source_span_identity_matches_expected_record(self) -> None:
        test_case = replace_expected(
            self.case(),
            RetrievalEvaluationExpectedMatch(
                sources=(
                    {
                        "kind": "guide",
                        "uri": "vyral://guide/a",
                        "span": {"charStart": 5, "charEnd": 20},
                    },
                )
            ),
        )
        result = self.evaluation.evaluate(
            RetrievalEvaluationRequest(cases=(test_case,))
        )
        self.assertTrue(result.cases[0].hit)

    def test_comparison_applies_variants_and_reports_baseline_deltas(self) -> None:
        result = self.evaluation.compare(
            RetrievalEvaluationComparisonRequest(
                cases=(self.case(),),
                variants=(
                    RetrievalEvaluationVariant(id="baseline"),
                    RetrievalEvaluationVariant(id="excluded", min_score=2.0),
                ),
                include_case_results=True,
            )
        )

        self.assertEqual((2, 2, 0), (
            result.variants_attempted,
            result.variants_succeeded,
            result.variants_failed,
        ))
        self.assertIsNone(result.variants[0].delta_from_baseline)
        delta = result.variants[1].delta_from_baseline
        self.assertIsNotNone(delta)
        assert delta is not None
        self.assertEqual(-1.0, delta.hit_rate)
        self.assertEqual(1, len(result.variants[1].cases))

    def test_validation_rejects_overlap_and_async_facade_matches(self) -> None:
        overlapping = RetrievalEvaluationCase(
            request=self.case().request,
            expected=(RetrievalEvaluationExpectedMatch(id="same"),),
            hard_negatives=(RetrievalEvaluationHardNegativeMatch(id="same"),),
        )
        invalid = self.evaluation.evaluate(
            RetrievalEvaluationRequest(cases=(overlapping,))
        )
        self.assertEqual("failed", invalid.cases[0].status)
        self.assertIn("overlaps", invalid.cases[0].error or "")

        result = asyncio.run(
            self.evaluation.aevaluate(
                RetrievalEvaluationRequest(cases=(self.case(),))
            )
        )
        self.assertEqual(1.0, result.hit_rate)


def replace_expected(
    test_case: RetrievalEvaluationCase,
    expected: RetrievalEvaluationExpectedMatch,
) -> RetrievalEvaluationCase:
    return RetrievalEvaluationCase(
        request=test_case.request,
        expected=(expected,),
        name=test_case.name,
        hard_negatives=test_case.hard_negatives,
        k=test_case.k,
        metadata=test_case.metadata,
    )


if __name__ == "__main__":
    unittest.main()
