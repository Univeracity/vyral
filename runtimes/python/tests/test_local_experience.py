from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from vyral_runtime._local_experience import (
    inspect_local_runtime,
    prepare_local_quickstart_root,
    reset_local_quickstart,
    run_local_quickstart_sync,
)
from vyral_runtime.local import QueryEnvelope
from vyral_runtime.runtime import VyralRuntime


class LocalExperienceTests(unittest.TestCase):
    def test_citations_and_execution_survive_a_real_reopen(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-experience-"
        ) as temporary:
            root = Path(temporary) / "state"
            messages: list[str] = []

            first = run_local_quickstart_sync(root, emit=messages.append)

            self.assertEqual("prototype", first.maturity)
            self.assertFalse(first.full_local_ready)
            self.assertEqual("lexical", first.retrieval_mode)
            self.assertFalse(first.embedding_used)
            self.assertEqual("local-token-hash", first.embedding_provider)
            self.assertEqual("lexical", first.embedding_semantic_quality)
            self.assertEqual(3, first.created_chunks)
            self.assertEqual(0, first.reused_chunks)
            self.assertGreaterEqual(len(first.citations), 1)
            self.assertIn("Citations:", first.context_text)
            self.assertEqual("queued", first.admitted_status)
            self.assertTrue(first.admission_replayed)
            self.assertEqual("queued", first.persisted_status)
            self.assertEqual("succeeded", first.completed_status)
            self.assertEqual(1, first.dispatched_runs)
            self.assertGreaterEqual(first.first_citation_ms, 0)
            self.assertGreaterEqual(
                first.durable_receipt_ms,
                first.first_citation_ms,
            )
            self.assertGreaterEqual(first.restart_recovery_ms, 0)
            self.assertGreaterEqual(
                first.completed_ms,
                first.durable_receipt_ms,
            )
            timings = first.to_dict()["timings"]
            self.assertIsInstance(timings, dict)
            assert isinstance(timings, dict)
            self.assertEqual(
                first.first_citation_ms,
                timings["firstCitationMs"],
            )
            self.assertIsInstance(first.completed_result, dict)
            assert isinstance(first.completed_result, dict)
            self.assertEqual(
                "accepted work survived the runtime restart",
                first.completed_result["message"],
            )
            serialized = first.to_dict()
            retrieval = serialized["retrieval"]
            embedding = serialized["embedding"]
            self.assertIsInstance(retrieval, dict)
            self.assertIsInstance(embedding, dict)
            assert isinstance(retrieval, dict)
            assert isinstance(embedding, dict)
            self.assertEqual("lexical", retrieval["mode"])
            self.assertFalse(embedding["used"])
            self.assertLess(
                next(
                    index
                    for index, message in enumerate(messages)
                    if message.startswith("Accepted receipt:")
                ),
                next(
                    index
                    for index, message in enumerate(messages)
                    if message == "Closed the first runtime instance."
                ),
            )

            second = run_local_quickstart_sync(root)

            self.assertEqual(first.run_id, second.run_id)
            self.assertEqual(0, second.created_chunks)
            self.assertEqual(3, second.reused_chunks)
            self.assertEqual(0, second.dispatched_runs)
            self.assertTrue(second.admission_replayed)

            with VyralRuntime.open_local(root) as runtime:
                policy = runtime.records.get_collection_policy(
                    "vyral-quickstart"
                )
                self.assertIsNotNone(policy)
                assert policy is not None
                self.assertEqual((), policy.vector_policies)
                records = runtime.records.query_all_records(
                    "vyral-quickstart",
                    QueryEnvelope(partition_keys=("local",)),
                )
                self.assertEqual(3, len(records))
                self.assertTrue(all(not record.vectors for record in records))

            inspection = inspect_local_runtime(root)
            self.assertEqual("local-single-node", inspection["topology"])
            providers = inspection["providers"]
            self.assertIsInstance(providers, dict)
            assert isinstance(providers, dict)
            embeddings = providers["embeddings"]
            self.assertIsInstance(embeddings, dict)
            assert isinstance(embeddings, dict)
            self.assertEqual("lexical", embeddings["semanticQuality"])
            self.assertFalse(embeddings["requiresNetwork"])

    def test_reset_requires_a_dedicated_owned_directory(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-reset-"
        ) as temporary:
            parent = Path(temporary)
            foreign = parent / "foreign"
            foreign.mkdir()
            (foreign / "important.txt").write_text(
                "keep", encoding="utf-8"
            )
            with self.assertRaisesRegex(ValueError, "dedicated empty"):
                prepare_local_quickstart_root(foreign)
            with self.assertRaisesRegex(ValueError, "Refusing to reset"):
                reset_local_quickstart(foreign)
            self.assertEqual(
                "keep",
                (foreign / "important.txt").read_text(encoding="utf-8"),
            )

            owned = prepare_local_quickstart_root(parent / "owned")
            (owned / "state.txt").write_text("state", encoding="utf-8")
            removed = reset_local_quickstart(owned)
            self.assertEqual(owned, removed)
            self.assertFalse(owned.exists())

    def test_broad_roots_are_rejected(self) -> None:
        for root in (Path.cwd(), Path.home()):
            with self.subTest(root=root), self.assertRaisesRegex(
                ValueError, "broad"
            ):
                prepare_local_quickstart_root(root)


if __name__ == "__main__":
    unittest.main()
