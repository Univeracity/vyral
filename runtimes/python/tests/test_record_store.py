from __future__ import annotations

from datetime import datetime, timedelta, timezone
from pathlib import Path
import sqlite3
from tempfile import TemporaryDirectory
import unittest

from vyral_runtime import (
    CollectionNotFoundError,
    CollectionPolicyConflictError,
    RecordCollectionPolicy,
    RecordPreconditionFailedError,
    RecordStoreError,
    RecordValidationError,
    RecordWritePrecondition,
    SQLiteRecordStore,
    VectorFieldPolicy,
    VyralRecord,
    VyralVector,
    run_bundled_record_store_scenario,
    run_bundled_record_store_scenarios,
)


class StepClock:
    def __init__(self) -> None:
        self._next = datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc)

    def __call__(self) -> datetime:
        current = self._next
        self._next += timedelta(seconds=1)
        return current


class SQLiteRecordStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "vyral.sqlite"
        self.clock = StepClock()
        self.store = SQLiteRecordStore(self.database_path, clock=self.clock)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    @staticmethod
    def policy(name: str = "items") -> RecordCollectionPolicy:
        return RecordCollectionPolicy(
            name=name,
            vector_policies=(
                VectorFieldPolicy(
                    name="embedding",
                    path="/vectors/embedding/values",
                    dimensions=3,
                ),
            ),
            indexed_metadata=("/metadata/status", "/metadata/score"),
        )

    @staticmethod
    def record(
        record_id: str = "record-1",
        *,
        status: str = "active",
        vector: tuple[float, ...] = (1.0, 0.0, -1.0),
    ) -> VyralRecord:
        return VyralRecord(
            id=record_id,
            partition_key="tenant-a",
            type="note",
            schema_version="1",
            metadata={"status": status, "score": 7},
            content={"title": "Portable retrieval", "body": "hello π"},
            vectors={
                "embedding": VyralVector(
                    values=vector,
                    model="fixture-embedding",
                )
            },
            additional_properties={"extension": {"enabled": True}},
        )

    def test_diagnostics_report_durable_wal_and_fts5(self) -> None:
        diagnostics = self.store.diagnostics()

        self.assertTrue(diagnostics.healthy)
        self.assertEqual("ok", diagnostics.quick_check)
        self.assertEqual(0, diagnostics.foreign_key_violation_count)
        self.assertEqual("wal", diagnostics.journal_mode.lower())
        self.assertTrue(diagnostics.fts5_available)
        self.assertTrue(diagnostics.database_exists)
        self.assertGreater(diagnostics.database_bytes, 0)

    def test_collection_lifecycle_is_deterministic_and_policy_safe(self) -> None:
        original = self.policy("z-items")
        equivalent = RecordCollectionPolicy(
            name="z-items",
            indexed_metadata=("/metadata/score", "/metadata/status"),
            vector_policies=original.vector_policies,
        )

        self.store.create_collection(original)
        self.store.create_collection(equivalent)
        self.store.create_collection(RecordCollectionPolicy(name="a-items"))

        self.assertEqual(("a-items", "z-items"), self.store.list_collections())
        self.assertEqual(original.to_dict(), self.store.get_collection_policy("z-items").to_dict())  # type: ignore[union-attr]

        with self.assertRaises(CollectionPolicyConflictError):
            self.store.create_collection(
                RecordCollectionPolicy(
                    name="z-items",
                    vector_policies=(
                        VectorFieldPolicy(
                            name="embedding",
                            path="/vectors/embedding/values",
                            dimensions=4,
                        ),
                    ),
                    indexed_metadata=original.indexed_metadata,
                )
            )

        self.store.delete_collection("z-items")
        self.store.delete_collection("z-items")
        self.assertIsNone(self.store.get_collection_policy("z-items"))

    def test_collection_policy_rejects_nonportable_shapes(self) -> None:
        invalid = (
            RecordCollectionPolicy(name="-items"),
            RecordCollectionPolicy(name="items", partition_key_path="/tenantId"),
            RecordCollectionPolicy(
                name="items",
                vector_policies=(
                    VectorFieldPolicy(
                        name="bad/name",
                        path="/vectors/bad/name/values",
                        dimensions=3,
                    ),
                ),
            ),
            RecordCollectionPolicy(
                name="items",
                indexed_metadata=("/metadata/status", "/metadata/status"),
            ),
        )

        for policy in invalid:
            with self.subTest(policy=policy):
                with self.assertRaises(RecordValidationError):
                    self.store.create_collection(policy)

    def test_record_roundtrip_revisions_and_unknown_properties(self) -> None:
        self.store.create_collection(self.policy())

        first = self.store.upsert_record("items", self.record())
        second = self.store.upsert_record(
            "items",
            VyralRecord(
                **{
                    **self.record(status="updated").__dict__,
                    "created_at": datetime(2000, 1, 1, tzinfo=timezone.utc),
                }
            ),
        )
        loaded = self.store.get_record("items", "tenant-a", "record-1")

        self.assertEqual(1, first.revision)
        self.assertEqual("rev:1", first.etag)
        self.assertEqual(3, first.vectors["embedding"].dimensions)  # type: ignore[index]
        self.assertEqual(2, second.revision)
        self.assertEqual("rev:2", second.etag)
        self.assertEqual(first.created_at, second.created_at)
        self.assertIsNotNone(first.updated_at)
        self.assertIsNotNone(second.updated_at)
        if first.updated_at is not None and second.updated_at is not None:
            self.assertLess(first.updated_at, second.updated_at)
        self.assertEqual(second.to_dict(), loaded.to_dict())  # type: ignore[union-attr]
        self.assertEqual({"enabled": True}, loaded.additional_properties["extension"])  # type: ignore[union-attr]

        reopened = SQLiteRecordStore(self.database_path)
        self.assertEqual(second.to_dict(), reopened.get_record("items", "tenant-a", "record-1").to_dict())  # type: ignore[union-attr]

    def test_record_identity_and_missing_collection_are_rejected(self) -> None:
        self.store.create_collection(self.policy())

        with self.assertRaises(RecordValidationError):
            self.store.upsert_record("items", self.record("bad/id"))
        with self.assertRaises(CollectionNotFoundError):
            self.store.upsert_record("missing", self.record())
        self.assertIsNone(self.store.get_record("items", "tenant-a", "record-1"))

    def test_vector_validation_is_failure_atomic(self) -> None:
        self.store.create_collection(self.policy())
        original = self.store.upsert_record("items", self.record())

        with self.assertRaises(RecordValidationError):
            self.store.upsert_record(
                "items",
                self.record(status="invalid", vector=(1.0, 2.0)),
            )

        loaded = self.store.get_record("items", "tenant-a", "record-1")
        self.assertEqual(original.to_dict(), loaded.to_dict())  # type: ignore[union-attr]
        with sqlite3.connect(self.database_path) as connection:
            counts = connection.execute(
                """
                SELECT
                    (SELECT count(*) FROM vyral_py_vectors),
                    (SELECT count(*) FROM vyral_py_metadata_index),
                    (SELECT count(*) FROM vyral_py_record_fts)
                """
            ).fetchone()
        self.assertEqual((1, 2, 1), counts)

    def test_preconditions_are_normalized_and_atomic(self) -> None:
        self.store.create_collection(self.policy())
        create_only = RecordWritePrecondition(if_none_match="*")
        first = self.store.upsert_record("items", self.record(), create_only)

        with self.assertRaisesRegex(RecordPreconditionFailedError, "already exists"):
            self.store.upsert_record("items", self.record(status="duplicate"), create_only)
        with self.assertRaisesRegex(RecordPreconditionFailedError, "cannot both"):
            self.store.upsert_record(
                "items",
                self.record(status="ambiguous"),
                RecordWritePrecondition(if_match=" rev:1 ", if_none_match=" * "),
            )
        with self.assertRaisesRegex(RecordPreconditionFailedError, "disagree"):
            self.store.upsert_record(
                "items",
                self.record(status="disagree"),
                RecordWritePrecondition(if_match='"rev:1"', expected_etag="rev:2"),
            )
        with self.assertRaisesRegex(RecordPreconditionFailedError, "revision"):
            self.store.upsert_record(
                "items",
                self.record(status="stale"),
                RecordWritePrecondition(expected_revision=0),
            )

        second = self.store.upsert_record(
            "items",
            self.record(status="updated"),
            RecordWritePrecondition(if_match=' "rev:1" ', expected_revision=1),
        )
        self.assertEqual(2, second.revision)
        self.assertEqual(first.created_at, second.created_at)

    def test_batch_stop_and_continue_semantics_match_contract(self) -> None:
        self.store.create_collection(self.policy())
        records = (
            self.record("one"),
            self.record("bad/id"),
            self.record("three"),
        )

        stopped = self.store.upsert_records("items", records)
        self.assertEqual(
            {
                "requested": 3,
                "attempted": 2,
                "succeeded": 1,
                "failed": 1,
                "stoppedOnError": True,
            },
            {
                key: stopped.to_dict()[key]
                for key in (
                    "requested",
                    "attempted",
                    "succeeded",
                    "failed",
                    "stoppedOnError",
                )
            },
        )
        self.assertEqual(("succeeded", "failed"), tuple(item.status for item in stopped.items))

        continued = self.store.upsert_records(
            "items",
            records,
            continue_on_error=True,
        )
        self.assertEqual((3, 3, 2, 1, False), (
            continued.requested,
            continued.attempted,
            continued.succeeded,
            continued.failed,
            continued.stopped_on_error,
        ))
        self.assertEqual(2, self.store.get_record("items", "tenant-a", "one").revision)  # type: ignore[union-attr]
        self.assertEqual(1, self.store.get_record("items", "tenant-a", "three").revision)  # type: ignore[union-attr]

    def test_delete_record_and_collection_cascade_all_indexes(self) -> None:
        self.store.create_collection(self.policy())
        self.store.upsert_record("items", self.record())

        self.store.delete_record("items", "tenant-a", "record-1")
        self.store.delete_record("items", "tenant-a", "record-1")
        self.assertIsNone(self.store.get_record("items", "tenant-a", "record-1"))

        self.store.upsert_record("items", self.record())
        self.store.delete_collection("items")
        with sqlite3.connect(self.database_path) as connection:
            counts = connection.execute(
                """
                SELECT
                    (SELECT count(*) FROM vyral_py_records),
                    (SELECT count(*) FROM vyral_py_vectors),
                    (SELECT count(*) FROM vyral_py_metadata_index),
                    (SELECT count(*) FROM vyral_py_record_fts)
                """
            ).fetchone()
        self.assertEqual((0, 0, 0, 0), counts)

    def test_clock_must_be_offset_aware(self) -> None:
        store = SQLiteRecordStore(
            Path(self.temporary_directory.name) / "naive.sqlite",
            clock=lambda: datetime(2026, 7, 30, 12, 0),
        )
        store.create_collection(RecordCollectionPolicy(name="items"))

        with self.assertRaisesRegex(RecordStoreError, "offset-aware"):
            store.upsert_record(
                "items",
                VyralRecord(id="one", partition_key="tenant-a"),
            )
        self.assertIsNone(store.get_record("items", "tenant-a", "one"))

    def test_bundled_portable_record_scenario(self) -> None:
        results = run_bundled_record_store_scenario()

        self.assertEqual(17, len(results))
        self.assertEqual("records.core-crud.v1", results[0].scenario_id)
        self.assertEqual("collection-is-gone", results[-1].step_id)

    def test_all_bundled_portable_record_scenarios(self) -> None:
        results = run_bundled_record_store_scenarios()

        self.assertEqual(30, len(results))
        self.assertEqual(
            {"records.core-crud.v1", "records.query-semantics.v1"},
            {result.scenario_id for result in results},
        )
        self.assertEqual("reject-non-scalar-filter", results[-1].step_id)


if __name__ == "__main__":
    unittest.main()
