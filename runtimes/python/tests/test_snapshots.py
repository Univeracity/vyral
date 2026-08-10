from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from vyral_runtime import (
    CollectionExportEnvelope,
    CollectionImportRequest,
    RecordCollectionPolicy,
    RecordValidationError,
    SQLiteRecordStore,
    VectorFieldPolicy,
    VyralRecord,
    VyralVector,
    compute_collection_snapshot_hash,
)


class CollectionSnapshotTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.store = SQLiteRecordStore(
            Path(self.temporary_directory.name) / "snapshot.sqlite",
            clock=lambda: datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc),
        )
        self.policy = RecordCollectionPolicy(
            name="items",
            vector_policies=(
                VectorFieldPolicy(
                    name="embedding",
                    path="/vectors/embedding/values",
                    dimensions=2,
                ),
            ),
            indexed_metadata=("/metadata/status",),
        )
        self.store.create_collection(self.policy)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    @staticmethod
    def explicit_snapshot() -> CollectionExportEnvelope:
        return CollectionExportEnvelope.from_value(
            {
                "collection": "items",
                "policy": {
                    "name": "items",
                    "partitionKeyPath": "/partitionKey",
                    "vectorPolicies": [
                        {
                            "name": "embedding",
                            "path": "/vectors/embedding/values",
                            "dimensions": 2,
                            "datatype": "float32",
                            "distanceFunction": "cosine",
                            "indexType": "flat",
                        }
                    ],
                    "indexedMetadata": ["/metadata/status"],
                },
                "records": [
                    {
                        "id": "r1",
                        "partitionKey": "tenant-a",
                        "type": "note",
                        "schemaVersion": "1",
                        "metadata": {"status": "active", "ratio": 0.1},
                        "content": {"text": "hello π"},
                        "sources": None,
                        "vectors": {
                            "embedding": {
                                "values": [0.1, -0.0],
                                "dimensions": 2,
                                "model": "m",
                                "datatype": "float32",
                                "distanceFunction": "cosine",
                                "generatedAt": "2026-07-30T12:00:00.12Z",
                                "sourceField": "/content/text",
                            }
                        },
                        "etag": "rev:1",
                        "revision": 1,
                        "createdAt": "2026-07-30T12:00:00.12Z",
                        "updatedAt": "2026-07-30T12:00:01Z",
                        "extension": {"enabled": True},
                    }
                ],
                "recordCount": 1,
                "truncated": False,
            }
        )

    def add_records(self, count: int = 3) -> None:
        for index in range(count):
            self.store.upsert_record(
                "items",
                VyralRecord(
                    id=f"record-{index}",
                    partition_key="tenant-a",
                    metadata={
                        "status": "active" if index % 2 == 0 else "inactive"
                    },
                    content={"text": f"portable record {index} π"},
                    vectors={
                        "embedding": VyralVector(
                            values=(0.1 + index, -0.0),
                        )
                    },
                ),
            )

    def test_snapshot_hash_matches_dotnet_web_json_semantics(self) -> None:
        snapshot = self.explicit_snapshot()

        self.assertEqual(
            "sha256:a1ccf1c255253c9c4bb291218471be3cb4c6ec293e95fe67748d5974cc60be53",
            compute_collection_snapshot_hash(snapshot),
        )
        wire_roundtrip = CollectionExportEnvelope.from_value(snapshot.to_dict())
        self.assertEqual(
            compute_collection_snapshot_hash(snapshot),
            compute_collection_snapshot_hash(wire_roundtrip),
        )

    def test_export_is_bounded_filterable_and_self_verifying(self) -> None:
        self.add_records(3)

        with self.assertRaisesRegex(RecordValidationError, "exceeded maxRecords"):
            self.store.export_collection("items", {"maxRecords": 2})

        snapshot = self.store.export_collection(
            "items",
            {
                "maxRecords": 2,
                "failOnLimitExceeded": False,
                "query": {
                    "filter": {
                        "path": "/metadata/status",
                        "op": "eq",
                        "value": "active",
                    }
                },
            },
        )

        self.assertIsNotNone(snapshot)
        if snapshot is None:
            self.fail("snapshot was unexpectedly absent")
        self.assertEqual(2, snapshot.record_count)
        self.assertFalse(snapshot.truncated)
        self.assertIsNone(snapshot.continuation_token)
        self.assertEqual(
            ("record-0", "record-2"),
            tuple(record.id for record in snapshot.records),
        )
        self.assertEqual(
            snapshot.content_hash,
            compute_collection_snapshot_hash(snapshot),
        )

        truncated = self.store.export_collection(
            "items",
            {"maxRecords": 2, "failOnLimitExceeded": False},
        )
        self.assertTrue(truncated.truncated)  # type: ignore[union-attr]
        self.assertEqual("Mg==", truncated.continuation_token)  # type: ignore[union-attr]

    def test_export_import_roundtrip_and_rename(self) -> None:
        self.add_records(2)
        snapshot = self.store.export_collection("items")
        self.assertIsNotNone(snapshot)
        if snapshot is None:
            self.fail("snapshot was unexpectedly absent")

        imported = self.store.import_collection(
            "items-copy",
            CollectionImportRequest(
                snapshot=CollectionExportEnvelope.from_value(snapshot.to_dict()),
                allow_collection_rename=True,
            ),
        )

        self.assertEqual("created", imported.policy_status)
        self.assertTrue(imported.content_hash_comparison.compared)
        self.assertTrue(imported.content_hash_comparison.matches)
        self.assertEqual(2, imported.records.succeeded)
        self.assertEqual(
            ("record-0", "record-1"),
            tuple(
                record.id
                for record in self.store.query_records("items-copy")
            ),
        )
        copied_policy = self.store.get_collection_policy("items-copy")
        self.assertEqual("items-copy", copied_policy.name)  # type: ignore[union-attr]

        repeated = self.store.import_collection(
            "items-copy",
            {
                "snapshot": snapshot.to_dict(),
                "allowCollectionRename": True,
            },
        )
        self.assertEqual("existing_equivalent", repeated.policy_status)
        self.assertEqual(2, repeated.records.succeeded)
        self.assertEqual(
            (2, 2),
            tuple(
                record.revision
                for record in self.store.query_records("items-copy")
            ),
        )

    def test_import_rejects_hash_drift_and_partial_snapshots_before_mutating(self) -> None:
        self.add_records(2)
        snapshot = self.store.export_collection("items")
        self.assertIsNotNone(snapshot)
        if snapshot is None:
            self.fail("snapshot was unexpectedly absent")
        wire = snapshot.to_dict()
        wire["contentHash"] = "sha256:" + "0" * 64

        with self.assertRaisesRegex(RecordValidationError, "hash mismatch"):
            self.store.import_collection(
                "drifted",
                {
                    "snapshot": wire,
                    "allowCollectionRename": True,
                },
            )
        self.assertIsNone(self.store.get_collection_policy("drifted"))

        partial = dict(snapshot.to_dict())
        partial["truncated"] = True
        partial["contentHash"] = None
        with self.assertRaisesRegex(RecordValidationError, "truncated"):
            self.store.import_collection(
                "partial",
                {
                    "snapshot": partial,
                    "allowCollectionRename": True,
                },
            )
        accepted = self.store.import_collection(
            "partial",
            {
                "snapshot": partial,
                "allowCollectionRename": True,
                "allowPartialSnapshot": True,
            },
        )
        self.assertEqual(2, accepted.records.succeeded)

    def test_import_validates_record_count_and_rename_intent(self) -> None:
        snapshot = self.explicit_snapshot()
        wire = snapshot.to_dict()
        wire["contentHash"] = compute_collection_snapshot_hash(snapshot)
        wire["recordCount"] = 2

        with self.assertRaisesRegex(RecordValidationError, "recordCount"):
            self.store.import_collection(
                "items-copy",
                {"snapshot": wire, "allowCollectionRename": True},
            )

        wire["recordCount"] = 1
        with self.assertRaisesRegex(RecordValidationError, "allowCollectionRename"):
            self.store.import_collection("items-copy", {"snapshot": wire})


if __name__ == "__main__":
    unittest.main()
