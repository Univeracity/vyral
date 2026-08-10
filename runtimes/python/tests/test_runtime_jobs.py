from __future__ import annotations

from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (  # noqa: E402
    BUILTIN_JOB_PLUGIN_ID,
    ExecutionRunRequest,
    RecordCollectionPolicy,
    RuntimeJobHandlerIds,
    VyralRuntime,
)


class RuntimeJobAdapterTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(
            prefix="vyral-python-jobs-"
        )
        self.runtime = VyralRuntime(Path(self.temporary.name))

    async def asyncTearDown(self) -> None:
        self.runtime.close()
        self.temporary.cleanup()

    async def test_runtime_registers_complete_builtin_job_catalog(
        self,
    ) -> None:
        expected = {
            value
            for name, value in vars(RuntimeJobHandlerIds).items()
            if name.isupper()
        }
        descriptors = {
            item.handler_id: item
            for item in self.runtime.execution.list_handlers()
        }

        self.assertEqual(expected, set(descriptors))
        self.assertTrue(
            all(
                descriptor.plugin_id == BUILTIN_JOB_PLUGIN_ID
                for descriptor in descriptors.values()
            )
        )
        self.assertEqual(
            [BUILTIN_JOB_PLUGIN_ID],
            [
                plugin.plugin_id
                for plugin in self.runtime.execution.list_plugins()
            ],
        )

    async def test_embedding_job_persists_result_and_owned_state(
        self,
    ) -> None:
        started = await self.runtime.execution.start_run(
            ExecutionRunRequest(
                RuntimeJobHandlerIds.EMBEDDINGS,
                plugin_id=BUILTIN_JOB_PLUGIN_ID,
                payload={
                    "request": {
                        "texts": ["portable", "runtime"],
                        "purpose": "symmetric",
                    }
                },
            )
        )

        self.assertEqual(
            1, await self.runtime.execution.dispatch_ready_runs()
        )
        completed = await self.runtime.execution.get_run(started.id)
        assert completed is not None
        self.assertEqual("succeeded", completed.status)
        self.assertEqual(2, len(completed.result["items"]))
        checkpoint = await self.runtime.execution.get_checkpoint(
            started.id, "completed"
        )
        assert checkpoint is not None
        self.assertEqual("completed", checkpoint.content["status"])
        artifact = await self.runtime.execution.get_artifact(
            started.id, "result"
        )
        assert artifact is not None
        self.assertEqual(completed.result, artifact.content)

    async def test_record_batch_job_uses_shared_embedded_store(
        self,
    ) -> None:
        await self.runtime.async_records.create_collection(
            RecordCollectionPolicy("documents")
        )
        started = await self.runtime.execution.start_run(
            ExecutionRunRequest(
                RuntimeJobHandlerIds.RECORD_BATCH_UPSERT,
                payload={
                    "collection": "documents",
                    "request": {
                        "records": [
                            {
                                "id": "record-1",
                                "partitionKey": "tenant-a",
                                "type": "document",
                                "content": {"text": "hello"},
                            }
                        ]
                    },
                },
            )
        )

        await self.runtime.execution.dispatch_ready_runs()
        completed = await self.runtime.execution.get_run(started.id)
        assert completed is not None
        self.assertEqual("succeeded", completed.status)
        self.assertEqual(1, completed.result["succeeded"])
        stored = await self.runtime.async_records.get_record(
            "documents", "tenant-a", "record-1"
        )
        assert stored is not None
        self.assertEqual({"text": "hello"}, stored.content)

    async def test_invalid_job_payload_is_a_validation_failure(
        self,
    ) -> None:
        started = await self.runtime.execution.start_run(
            ExecutionRunRequest(
                RuntimeJobHandlerIds.EMBEDDINGS,
                payload=["not", "an", "object"],
            )
        )

        await self.runtime.execution.dispatch_ready_runs()
        completed = await self.runtime.execution.get_run(started.id)
        assert completed is not None
        self.assertEqual("failed", completed.status)
        self.assertEqual("validation", completed.failure_class)
        self.assertIn("payload must be an object", completed.error or "")

    async def test_builtin_jobs_can_be_disabled(self) -> None:
        self.runtime.close()
        self.runtime = VyralRuntime(
            Path(self.temporary.name) / "without-jobs",
            register_builtin_jobs=False,
        )

        self.assertEqual((), self.runtime.execution.list_plugins())
        self.assertEqual((), self.runtime.execution.list_handlers())


if __name__ == "__main__":
    unittest.main()
