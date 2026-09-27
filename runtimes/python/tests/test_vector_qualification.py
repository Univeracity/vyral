from __future__ import annotations

import json
from pathlib import Path
import random
import sqlite3
import struct
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

from vyral_runtime import SQLiteRecordStore, VyralRecord
from vyral_runtime.local import record_store


class VectorQualificationTests(unittest.TestCase):
    def test_prepared_384_dimension_scores_preserve_frozen_float32_bits(self) -> None:
        # Frozen against the pre-optimization Python kernel, not NumPy/float64.
        randomizer = random.Random(381)

        def vector() -> tuple[float, ...]:
            return tuple(struct.unpack("<f", struct.pack("<f", randomizer.uniform(-1, 1)))[0]
                         for _ in range(384))

        query, stored = vector(), vector()
        for kind, expected_hex in (
            ("cosine", "512e09bd"), ("dotproduct", "f22488c0"), ("euclidean", "68036e3d"),
        ):
            prepared = record_store._PreparedSimilarity(kind, query)
            for _ in range(3):
                actual = prepared.score(stored)
                self.assertEqual(expected_hex, struct.pack("<f", actual).hex())
            self.assertEqual(expected_hex, struct.pack("<f", record_store._similarity_score(kind, query, stored)).hex())

    def test_zero_dimension_nonfinite_overflow_and_metric_errors_remain_explicit(self) -> None:
        for kind, expected in (("cosine", 0.0), ("dotproduct", 0.0), ("euclidean", 1.0)):
            self.assertEqual(expected, record_store._PreparedSimilarity(kind, ()).score(()))
        zero = record_store._PreparedSimilarity("cosine", (0.0, 0.0))
        self.assertEqual(0.0, zero.score((1.0, 0.0)))
        self.assertEqual(0.0, zero.score((0.0, 1.0)))
        with self.assertRaisesRegex(record_store.RecordStoreError, "Vector dimensions differ: 2 != 1"):
            zero.score((1.0,))
        for value, error in ((float("nan"), "non-finite"), (float("inf"), "non-finite"),
                             (3.4e38, "outside float32 range")):
            with self.assertRaisesRegex(record_store.RecordValidationError, error):
                record_store._PreparedSimilarity("cosine", (value,)).score((value,))
        with self.assertRaisesRegex(record_store.RecordValidationError, "not supported"):
            record_store._PreparedSimilarity("other", (1.0,)).score((1.0,))

    def test_full_record_validation_remains_fail_closed_and_diagnostics_only_cover_top(self) -> None:
        with TemporaryDirectory() as root:
            store = SQLiteRecordStore(Path(root) / "vectors.sqlite")
            store.create_collection({"name": "items", "vectorPolicies": [{
                "name": "embedding", "path": "/vectors/embedding/values", "dimensions": 2,
            }]})
            for index in range(10):
                store.upsert_record("items", {
                    "id": f"{index:02d}", "partitionKey": "p", "content": {"text": "record"},
                    "vectors": {"embedding": {"values": [1.0, 0.0]}},
                })
            request = {"vector": {"field": "embedding", "value": [1.0, 0.0], "top": 3}, "limit": 2}
            with patch.object(record_store, "_vector_diagnostics", wraps=record_store._vector_diagnostics) as diagnostics:
                page = store.search_records_page("items", request)
            self.assertEqual(3, diagnostics.call_count)
            self.assertEqual(["00", "01"], [match.record.id for match in page.items])
            self.assertTrue(page.continuation_token)
            self.assertEqual({"searchCandidatePool": 10, "returnedCandidates": 3},
                             page.items[0].diagnostics.candidate_counts)
            next_page = store.search_records_page("items", {**request, "continuationToken": page.continuation_token})
            self.assertEqual(["02"], [match.record.id for match in next_page.items])
            with sqlite3.connect(store.database_path) as connection:
                connection.execute("UPDATE vyral_py_vectors SET vector_data=? WHERE record_id='09'",
                                   (b"invalid",))
            with self.assertRaisesRegex(record_store.RecordStoreError, "invalid byte length"):
                store.search_records("items", request)
            with sqlite3.connect(store.database_path) as connection:
                connection.execute("UPDATE vyral_py_vectors SET vector_data=? WHERE record_id='09'",
                                   (struct.pack("<2f", 1, 0),))
                connection.execute("UPDATE vyral_py_records SET record_json=? WHERE id='09'", ("{broken",))
            with self.assertRaises(json.JSONDecodeError):
                store.search_records("items", request)


if __name__ == "__main__":
    unittest.main()
