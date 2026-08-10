from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Lock
import sys
import time
from typing import Callable
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (
    AsyncSQLiteRecordStore,
    FileObjectStore,
    ObjectReadRequest,
    ObjectWriteRequest,
    RecordCollectionPolicy,
    RuntimeExecutor,
    SQLiteRecordStore,
    SQLiteTraceStore,
    TraceRecord,
    VyralRecord,
)


class RuntimeExecutorTests(unittest.TestCase):
    def test_executor_bounds_active_workers_and_rejects_after_close(self) -> None:
        executor = RuntimeExecutor(max_workers=2, max_pending=3)
        lock = Lock()
        active = 0
        maximum = 0

        def operation(index: int) -> int:
            nonlocal active, maximum
            with lock:
                active += 1
                maximum = max(maximum, active)
            time.sleep(0.01)
            with lock:
                active -= 1
            return index

        def bind(index: int) -> Callable[[], int]:
            return lambda: operation(index)

        async def run_all() -> tuple[int, ...]:
            return tuple(
                await asyncio.gather(
                    *(executor.run(bind(index)) for index in range(12))
                )
            )

        self.assertEqual(tuple(range(12)), asyncio.run(run_all()))
        self.assertLessEqual(maximum, 2)
        executor.close()
        with self.assertRaisesRegex(RuntimeError, "closed"):
            asyncio.run(executor.run(lambda: 1))

    def test_shared_executor_drives_storage_async_facades(self) -> None:
        with TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            executor = RuntimeExecutor(max_workers=2, max_pending=8)
            records = SQLiteRecordStore(root / "runtime.sqlite")
            async_records = AsyncSQLiteRecordStore(records, executor=executor)
            objects = FileObjectStore(root / "objects", executor=executor)
            traces = SQLiteTraceStore(
                root / "runtime.sqlite",
                executor=executor,
                clock=lambda: datetime(2026, 7, 30, tzinfo=timezone.utc),
            )

            async def exercise() -> None:
                await async_records.create_collection(
                    RecordCollectionPolicy(name="items")
                )
                stored = await async_records.upsert_record(
                    "items",
                    VyralRecord(id="one", partition_key="tenant-a"),
                )
                self.assertEqual(
                    stored,
                    await async_records.get_record(
                        "items",
                        "tenant-a",
                        "one",
                    ),
                )
                await objects.aput_object(
                    ObjectWriteRequest("artifacts", "one.bin", b"one")
                )
                result = await objects.aget_object(
                    ObjectReadRequest("artifacts", "one.bin")
                )
                self.assertIsNotNone(result)
                assert result is not None
                with result:
                    self.assertEqual(b"one", result.read())
                await traces.awrite_trace(
                    TraceRecord(
                        id="trace-one",
                        operation="test",
                        started_at=datetime(
                            2026,
                            7,
                            30,
                            tzinfo=timezone.utc,
                        ),
                        created_at=datetime(
                            2026,
                            7,
                            30,
                            tzinfo=timezone.utc,
                        ),
                    )
                )
                self.assertEqual(1, len(await traces.alist_traces()))

            asyncio.run(exercise())
            async_records.close()
            objects.close()
            traces.close()
            executor.close()


if __name__ == "__main__":
    unittest.main()
