from __future__ import annotations

import hashlib
from importlib import resources
import json
import os
from pathlib import Path
import shutil
import sqlite3
import tempfile
import unittest

from jsonschema import Draft202012Validator

from vyral_runtime._local_experience import (
    inspect_local_runtime,
    prepare_local_quickstart_root,
    reset_local_quickstart,
    run_local_quickstart_sync,
)
from vyral_runtime.local import QueryEnvelope
from vyral_runtime.runtime import VyralRuntime
from vyral_runtime.storage import (
    STORAGE_SCHEMA_COMPONENT,
    STORAGE_SCHEMA_VERSION,
)


def _tree_snapshot(root: Path) -> dict[str, tuple[object, ...]]:
    paths = (root, *sorted(root.rglob("*"), key=lambda path: path.as_posix()))
    snapshot: dict[str, tuple[object, ...]] = {}
    for path in paths:
        relative = "." if path == root else path.relative_to(root).as_posix()
        metadata = path.lstat()
        digest = None
        target = None
        if path.is_symlink():
            target = os.readlink(path)
        elif path.is_file():
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
        snapshot[relative] = (
            metadata.st_mode,
            metadata.st_size,
            metadata.st_mtime_ns,
            digest,
            target,
        )
    return snapshot


def _validate_inspection(inspection: dict[str, object]) -> None:
    schema_resource = resources.files("vyral_runtime").joinpath(
        "_local_runtime_inspection.schema.json"
    )
    schema = json.loads(schema_resource.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema).validate(inspection)


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

            before_inspection = _tree_snapshot(root)
            inspection = inspect_local_runtime(root)
            self.assertEqual(before_inspection, _tree_snapshot(root))
            self.assertEqual(
                "vyral.local-runtime-inspection.v1",
                inspection["schema"],
            )
            self.assertTrue(inspection["readOnly"])
            _validate_inspection(inspection)
            self.assertEqual("local-single-node", inspection["topology"])
            self.assertEqual([], inspection["blockers"])
            providers = inspection["providers"]
            self.assertIsInstance(providers, dict)
            assert isinstance(providers, dict)
            embeddings = providers["embeddings"]
            self.assertIsInstance(embeddings, dict)
            assert isinstance(embeddings, dict)
            self.assertEqual("lexical", embeddings["semanticQuality"])
            self.assertFalse(embeddings["requiresNetwork"])

    def test_inspect_does_not_initialize_an_empty_directory(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-empty-"
        ) as temporary:
            root = Path(temporary) / "state"
            root.mkdir()
            before = _tree_snapshot(root)

            with self.assertRaisesRegex(ValueError, "not initialized"):
                inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))

    def test_inspect_rejects_a_non_sqlite_file_without_changing_it(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-foreign-"
        ) as temporary:
            root = Path(temporary) / "state"
            root.mkdir()
            (root / "vyral.sqlite").write_bytes(b"not a SQLite database")
            before = _tree_snapshot(root)

            with self.assertRaisesRegex(ValueError, "invalid SQLite header"):
                inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))

    def test_inspect_does_not_adopt_a_legacy_database(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-legacy-"
        ) as temporary:
            root = Path(temporary) / "state"
            root.mkdir()
            database = sqlite3.connect(root / "vyral.sqlite")
            try:
                database.execute(
                    "CREATE TABLE vyral_py_legacy(value TEXT NOT NULL)"
                )
                database.commit()
            finally:
                database.close()
            before = _tree_snapshot(root)

            with self.assertRaisesRegex(ValueError, "never adopts or migrates"):
                inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))

    def test_inspect_refuses_a_newer_schema_without_changing_it(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-newer-"
        ) as temporary:
            root = Path(temporary) / "state"
            with VyralRuntime.open_local(root):
                pass
            database = sqlite3.connect(root / "vyral.sqlite")
            try:
                database.execute(
                    """
                    UPDATE vyral_py_runtime_schema
                    SET schema_version = ?
                    WHERE component = ?
                    """,
                    (STORAGE_SCHEMA_VERSION + 1, STORAGE_SCHEMA_COMPONENT),
                )
                database.commit()
            finally:
                database.close()
            before = _tree_snapshot(root)

            with self.assertRaisesRegex(ValueError, "supports at most"):
                inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))

    def test_inspect_reports_missing_object_state_without_recreating_it(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-objects-"
        ) as temporary:
            root = Path(temporary) / "state"
            with VyralRuntime.open_local(root):
                pass
            shutil.rmtree(root / "objects")
            before = _tree_snapshot(root)

            inspection = inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))
            self.assertFalse((root / "objects").exists())
            _validate_inspection(inspection)
            self.assertEqual("blocked", inspection["runtime"]["status"])
            self.assertIn(
                "Filesystem object storage diagnostics failed.",
                inspection["blockers"],
            )

    def test_inspect_reports_a_missing_table_without_repairing_it(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="vyral-local-inspect-table-"
        ) as temporary:
            root = Path(temporary) / "state"
            with VyralRuntime.open_local(root):
                pass
            database = sqlite3.connect(root / "vyral.sqlite")
            try:
                database.execute("DROP TABLE vyral_execution_timers")
                database.commit()
            finally:
                database.close()
            before = _tree_snapshot(root)

            inspection = inspect_local_runtime(root)

            self.assertEqual(before, _tree_snapshot(root))
            _validate_inspection(inspection)
            providers = inspection["providers"]
            self.assertIsInstance(providers, dict)
            assert isinstance(providers, dict)
            execution = providers["execution"]
            self.assertIsInstance(execution, dict)
            assert isinstance(execution, dict)
            self.assertFalse(execution["healthy"])
            self.assertIn(
                "vyral_execution_timers",
                execution["missingTables"],
            )
            self.assertIn(
                "SQLite durable execution diagnostics failed.",
                inspection["blockers"],
            )

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
