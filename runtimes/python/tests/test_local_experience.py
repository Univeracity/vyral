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
            self.assertIsInstance(first.completed_result, dict)
            assert isinstance(first.completed_result, dict)
            self.assertEqual(
                "accepted work survived the runtime restart",
                first.completed_result["message"],
            )
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
