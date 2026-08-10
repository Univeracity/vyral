from __future__ import annotations

from datetime import datetime, timedelta, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from vyral_runtime import (
    SQLiteRecordStore,
    SQLiteTraceStore,
    TraceExportRequest,
    TracePruneRequest,
    TraceRecord,
)


class SQLiteTraceStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = TemporaryDirectory()
        self.database_path = Path(self.temporary_directory.name) / "runtime.sqlite"
        self.now = datetime(2026, 7, 30, 12, 0, tzinfo=timezone.utc)
        self.store = SQLiteTraceStore(
            self.database_path,
            clock=lambda: self.now,
        )

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def trace(
        self,
        trace_id: str,
        operation: str,
        *,
        created_offset: int = 0,
        request: dict[str, object] | None = None,
        result: dict[str, object] | None = None,
        adapter: str | None = "local",
    ) -> TraceRecord:
        created = self.now + timedelta(seconds=created_offset)
        return TraceRecord(
            id=trace_id,
            operation=operation,
            adapter=adapter,
            request=request or {},
            result_summary=result or {},
            started_at=created - timedelta(milliseconds=10),
            duration_ms=10.0,
            created_at=created,
        )

    def test_write_get_list_and_database_coexistence(self) -> None:
        SQLiteRecordStore(self.database_path)
        self.store.write_trace(self.trace("b", "retrieval.search"))
        self.store.write_trace(self.trace("a", "retrieval.search"))
        self.store.write_trace(
            self.trace("c", "rag.ingest", created_offset=1)
        )

        fetched = self.store.get_trace("a")
        self.assertIsNotNone(fetched)
        assert fetched is not None
        self.assertEqual("retrieval.search", fetched.operation)
        self.assertEqual(10.0, fetched.duration_ms)
        self.assertEqual(
            ("c", "a", "b"),
            tuple(item.id for item in self.store.list_traces()),
        )
        self.assertEqual(
            ("a", "b"),
            tuple(
                item.id
                for item in self.store.list_traces(
                    operation="retrieval.search",
                    limit=10,
                )
            ),
        )
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.store.write_trace(self.trace("a", "duplicate"))

    def test_list_limits_are_bounded(self) -> None:
        for index in range(4):
            self.store.write_trace(
                self.trace(
                    f"trace-{index}",
                    "operation",
                    created_offset=index,
                )
            )
        self.assertEqual(2, len(self.store.list_traces(limit=2)))
        for limit in (0, 5001):
            with self.assertRaises(ValueError):
                self.store.list_traces(limit=limit)

    def test_summary_aggregates_operational_diagnostics(self) -> None:
        self.store.write_trace(
            self.trace(
                "one",
                "provider.run",
                request={"provider": "local", "capability": "embedding"},
                result={"status": "succeeded", "providerStatus": "ready"},
            )
        )
        self.store.write_trace(
            self.trace(
                "two",
                "provider.run",
                created_offset=1,
                request={"provider": "local", "capability": "embedding"},
                result={
                    "status": "failed",
                    "failureClass": "timeout",
                    "providerStatus": "degraded",
                },
                adapter="worker",
            )
        )
        self.store.write_trace(
            self.trace(
                "three",
                "retrieval.search",
                created_offset=2,
                result={"status": "succeeded"},
            )
        )

        summary = self.store.summarize_traces()
        provider = next(
            item for item in summary.operations if item.operation == "provider.run"
        )

        self.assertEqual(3, summary.total_count)
        self.assertEqual({"succeeded": 2, "failed": 1}, summary.status_counts)
        self.assertEqual({"timeout": 1}, summary.failure_class_counts)
        self.assertEqual({"local": 2}, summary.provider_counts)
        self.assertEqual({"embedding": 2}, summary.capability_counts)
        self.assertEqual(("local", "worker"), provider.adapters)
        self.assertEqual(2, provider.count)

    def test_export_warns_for_sensitive_content_and_hashes_bundle(self) -> None:
        self.store.write_trace(
            self.trace(
                "unsafe",
                "provider.run",
                request={
                    "authorization": "Bearer abc",
                    "nested": [{"api_key": "value"}],
                },
                result={"token": "not-a-bearer"},
            )
        )

        first = self.store.export_traces(TraceExportRequest())
        second = self.store.export_traces(TraceExportRequest())

        self.assertEqual(first.content_hash, second.content_hash)
        self.assertEqual(4, first.warning_count)
        self.assertEqual(
            {
                "sensitive_field_name",
                "bearer_token_value",
            },
            {item.reason for item in first.warnings},
        )
        self.assertTrue(first.content_hash.startswith("sha256:"))
        with self.assertRaisesRegex(ValueError, "potentially unsafe"):
            self.store.export_traces(
                TraceExportRequest(fail_on_unsafe_content=True)
            )

    def test_prune_supports_dry_run_keep_latest_scope_and_limit(self) -> None:
        for index in range(5):
            self.store.write_trace(
                self.trace(
                    f"search-{index}",
                    "retrieval.search",
                    created_offset=index,
                )
            )
        self.store.write_trace(
            self.trace("other", "rag.ingest", created_offset=-100)
        )
        request = TracePruneRequest(
            operation="retrieval.search",
            keep_latest=2,
            limit=2,
            dry_run=True,
        )
        planned = self.store.prune_traces(request)
        committed = self.store.prune_traces(
            TracePruneRequest(
                operation=request.operation,
                keep_latest=request.keep_latest,
                limit=request.limit,
            )
        )

        self.assertEqual(("search-2", "search-1"), planned.matched_ids)
        self.assertEqual(0, planned.deleted_count)
        self.assertEqual(planned.matched_ids, committed.deleted_ids)
        self.assertEqual(
            ("search-4", "search-3", "search-0"),
            tuple(
                item.id
                for item in self.store.list_traces(
                    operation="retrieval.search",
                )
            ),
        )
        self.assertIsNotNone(self.store.get_trace("other"))
        with self.assertRaisesRegex(ValueError, "at least one constraint"):
            self.store.prune_traces(TracePruneRequest())


if __name__ == "__main__":
    unittest.main()
