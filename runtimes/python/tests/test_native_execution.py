from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sqlite3
import sys
import tempfile
from typing import Any
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from vyral_runtime import (  # noqa: E402
    DelegateExecutionHandler,
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionExternalEventRequest,
    ExecutionHandlerDescriptor,
    ExecutionHistoryQuery,
    ExecutionLeaseRequest,
    ExecutionMaintenanceDispatchReconcileRequest,
    ExecutionMaintenancePruneRequest,
    ExecutionPluginDescriptor,
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    ExecutionProductPolicy,
    ExecutionRetryPolicy,
    ExecutionRunQuery,
    ExecutionRunRequest,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionRuntimePolicyError,
    ExecutionScope,
    ExecutionRuntimeConflictError,
    LocalExecutionRuntime,
    LocalExecutionRuntimeOptions,
    StaticExecutionPlugin,
)


class MutableClock:
    def __init__(self) -> None:
        self.value = datetime(
            2026, 7, 30, 18, 0, tzinfo=timezone.utc
        )

    def __call__(self) -> datetime:
        return self.value

    def advance(self, seconds: float) -> None:
        self.value += timedelta(seconds=seconds)


class NativeExecutionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(
            prefix="vyral-python-execution-"
        )
        self.database_path = (
            Path(self.temporary.name) / "execution.sqlite"
        )
        self.clock = MutableClock()
        self.runtime = LocalExecutionRuntime(
            LocalExecutionRuntimeOptions(
                self.database_path,
                max_active_runs=10,
                max_retained_terminal_runs=2,
            ),
            clock=self.clock,
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    async def test_host_can_persist_durable_coordination_checkpoint(
        self,
    ) -> None:
        run = await self.runtime.start_run(
            ExecutionRunRequest("host-coordination")
        )
        checkpoint = await self.runtime.put_run_checkpoint(
            run.id,
            ExecutionCheckpointWrite(
                "mcp-input-requests",
                {"requestIds": ["confirm"]},
            ),
        )
        self.assertEqual("mcp-input-requests", checkpoint.key)
        persisted = await self.runtime.get_checkpoint(
            run.id, "mcp-input-requests"
        )
        self.assertIsNotNone(persisted)
        self.assertEqual(
            {"requestIds": ["confirm"]},
            persisted.content,  # type: ignore[union-attr]
        )

        with self.assertRaisesRegex(LookupError, "was not found"):
            await self.runtime.put_run_checkpoint(
                "missing-run",
                ExecutionCheckpointWrite("coordination", {}),
            )

    async def test_success_idempotency_progress_artifact_checkpoint_history(
        self,
    ) -> None:
        async def execute(context: Any) -> ExecutionRunResult:
            await context.report(
                ExecutionRunUpdate(
                    requested=1,
                    attempted=1,
                    progress=0.5,
                    current_step="persist",
                )
            )
            await context.record_event(
                "step.completed", message="persisted"
            )
            await context.put_checkpoint(
                ExecutionCheckpointWrite(
                    "cursor", {"offset": 1}
                )
            )
            await context.put_artifact(
                ExecutionArtifactWrite(
                    "summary", content={"written": 1}
                )
            )
            return ExecutionRunResult.succeeded_result(
                {"written": 1}
            )

        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor(
                    "records.ingest",
                    "Record ingestion",
                    plugin_id="records",
                    max_attempts=3,
                ),
                execute,
            )
        )
        request = ExecutionRunRequest(
            "records.ingest",
            plugin_id="records",
            payload={"items": [1]},
            idempotency_key="request-1",
            correlation_id="correlation-1",
            tags={"tenant": "tenant-a"},
            retry_policy=ExecutionRetryPolicy(max_attempts=2),
        )
        created = await self.runtime.start_run(request)
        replay = await self.runtime.start_run(request)
        self.assertEqual(created.id, replay.id)
        self.assertEqual(1, await self.runtime.dispatch_ready_runs())

        completed = await self.runtime.get_run(created.id)
        self.assertIsNotNone(completed)
        assert completed is not None
        self.assertEqual("succeeded", completed.status)
        self.assertEqual(1.0, completed.progress)
        self.assertIsNone(completed.current_step)
        self.assertEqual({"written": 1}, completed.result)
        self.assertEqual(
            [created.id],
            [
                item.id
                for item in await self.runtime.list_runs(
                    ExecutionRunQuery(tags={"tenant": "tenant-a"})
                )
            ],
        )
        self.assertEqual(
            {"offset": 1},
            (
                await self.runtime.get_checkpoint(
                    created.id, "cursor"
                )
            ).content,  # type: ignore[union-attr]
        )
        self.assertEqual(
            ["summary"],
            [
                item.name
                for item in await self.runtime.list_artifacts(
                    created.id
                )
            ],
        )
        history = await self.runtime.get_history(
            created.id, ExecutionHistoryQuery(limit=100)
        )
        self.assertEqual("run.created", history[0].type)
        self.assertEqual("run.completed", history[-1].type)

    async def test_large_artifact_offload_rehydrate_and_prune(
        self,
    ) -> None:
        runtime = LocalExecutionRuntime(
            LocalExecutionRuntimeOptions(
                self.database_path.parent / "artifact-offload.sqlite",
                artifact_directory=(
                    self.database_path.parent / "execution-artifacts"
                ),
                max_active_runs=10,
                max_retained_terminal_runs=0,
                max_artifact_inline_bytes=32,
            ),
            clock=self.clock,
        )

        async def execute(context: Any) -> ExecutionRunResult:
            await context.put_artifact(
                ExecutionArtifactWrite(
                    "large-result",
                    kind="text",
                    text="x" * 128,
                )
            )
            return ExecutionRunResult.succeeded_result()

        runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor(
                    "large-artifact", "Large artifact"
                ),
                execute,
            )
        )
        started = await runtime.start_run(
            ExecutionRunRequest("large-artifact")
        )
        await runtime.dispatch_ready_runs()

        listed = (await runtime.list_artifacts(started.id))[0]
        self.assertIsNone(listed.text)
        self.assertEqual("local-file", listed.metadata["storage"])
        self.assertEqual("true", listed.metadata["offloaded"])
        assert listed.uri is not None
        artifact_path = Path(listed.uri)
        self.assertTrue(artifact_path.is_file())
        fetched = await runtime.get_artifact(
            started.id, "large-result"
        )
        assert fetched is not None
        self.assertEqual("x" * 128, fetched.text)

        dry_run = await runtime.prune(
            ExecutionMaintenancePruneRequest(
                dry_run=True, retain_terminal_runs=0
            )
        )
        self.assertEqual(1, dry_run.artifact_directories)
        self.assertTrue(artifact_path.is_file())
        pruned = await runtime.prune(
            ExecutionMaintenancePruneRequest(
                dry_run=False, retain_terminal_runs=0
            )
        )
        self.assertEqual(1, pruned.artifact_directories)
        self.assertFalse(artifact_path.exists())

    async def test_product_policy_enforces_scope_redaction_and_artifacts(
        self,
    ) -> None:
        policy = ExecutionProductPolicy(
            product_id="product-a",
            allowed_handler_ids=frozenset(
                {"policy-handler", "external-policy-handler"}
            ),
            allowed_tenant_ids=frozenset({"tenant-a"}),
            allowed_service_identities=frozenset({"worker-allowed"}),
            max_payload_bytes=128,
            artifact_prefix="product-a/",
            redacted_json_property_names=frozenset(
                {"accessToken"}
            ),
        )
        runtime = LocalExecutionRuntime(
            LocalExecutionRuntimeOptions(
                self.database_path.parent / "policy.sqlite",
                worker_id="worker-denied",
                product_policies=(policy,),
            ),
            clock=self.clock,
        )

        async def execute(context: Any) -> ExecutionRunResult:
            await context.record_event(
                "provider.call",
                details={
                    "accessToken": "secret-value",
                    "model": "local",
                },
            )
            await context.put_artifact(
                ExecutionArtifactWrite(
                    "product-a/result",
                    content={"ok": True},
                )
            )
            return ExecutionRunResult.succeeded_result()

        runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor(
                    "policy-handler", "Policy handler"
                ),
                execute,
            )
        )
        runtime.register_external_handler(
            ExecutionHandlerDescriptor(
                "external-policy-handler",
                "External policy handler",
            )
        )
        with self.assertRaisesRegex(
            ExecutionRuntimePolicyError, "scope is required"
        ):
            await runtime.start_run(
                ExecutionRunRequest("policy-handler")
            )
        with self.assertRaisesRegex(
            ExecutionRuntimePolicyError, "not allowed"
        ):
            await runtime.start_run(
                ExecutionRunRequest(
                    "policy-handler",
                    scope=ExecutionScope(
                        "product-a", "tenant-b"
                    ),
                )
            )

        started = await runtime.start_run(
            ExecutionRunRequest(
                "policy-handler",
                scope=ExecutionScope("product-a", "tenant-a"),
                payload={"value": "within-policy"},
            )
        )
        await runtime.dispatch_ready_runs()
        completed = await runtime.get_run(started.id)
        self.assertEqual("succeeded", completed.status)  # type: ignore[union-attr]
        history = await runtime.get_history(started.id)
        provider_event = next(
            item for item in history if item.type == "provider.call"
        )
        assert provider_event.details is not None
        self.assertEqual(
            "[redacted]", provider_event.details["accessToken"]
        )
        self.assertEqual("local", provider_event.details["model"])

        external = await runtime.start_run(
            ExecutionRunRequest(
                "external-policy-handler",
                scope=ExecutionScope("product-a", "tenant-a"),
            )
        )
        self.assertIsNone(await runtime.lease_next(external.id))

    async def test_adapter_status_exposes_exact_operational_policy(
        self,
    ) -> None:
        status = await self.runtime.get_adapter_status()
        document = status.to_dict()

        self.assertTrue(status.available)
        self.assertEqual("ok", status.status)
        self.assertEqual("local.sqlite", status.adapter.runtime_kind)
        self.assertEqual(
            self.runtime.options.max_active_runs,
            status.operational_policy.max_active_runs,
        )
        self.assertEqual(
            self.runtime.options.max_artifact_inline_bytes,
            document["operationalPolicy"][
                "maxArtifactInlineBytes"
            ],
        )
        self.assertEqual(
            "restart_recovery",
            document["resumePolicy"]["mode"],
        )

    async def test_idempotency_plugin_and_handler_rejection_semantics(
        self,
    ) -> None:
        descriptor = ExecutionHandlerDescriptor(
            "known", "Known", plugin_id="plugin"
        )
        self.runtime.register_handler(
            DelegateExecutionHandler(
                descriptor,
                lambda _: ExecutionRunResult.succeeded_result(),
            )
        )
        await self.runtime.start_run(
            ExecutionRunRequest(
                "known",
                plugin_id="plugin",
                payload={"value": 1},
                idempotency_key="same",
            )
        )
        with self.assertRaises(ExecutionRuntimeConflictError):
            await self.runtime.start_run(
                ExecutionRunRequest(
                    "known",
                    plugin_id="plugin",
                    payload={"value": 2},
                    idempotency_key="same",
                )
            )
        mismatch = await self.runtime.start_run(
            ExecutionRunRequest("known", plugin_id="other")
        )
        missing = await self.runtime.start_run(
            ExecutionRunRequest("missing")
        )
        self.assertEqual(
            ("rejected", "plugin_mismatch"),
            (mismatch.status, mismatch.failure_class),
        )
        self.assertEqual(
            ("rejected", "handler_missing"),
            (missing.status, missing.failure_class),
        )

    async def test_retry_then_success_and_terminal_cancel_is_stable(
        self,
    ) -> None:
        attempts = 0

        async def execute(_: Any) -> ExecutionRunResult:
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                return ExecutionRunResult.failed_result(
                    "transient", "try again"
                )
            return ExecutionRunResult.succeeded_result(
                {"attempts": attempts}
            )

        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor(
                    "retry", "Retry", max_attempts=3
                ),
                execute,
            )
        )
        run = await self.runtime.start_run(
            ExecutionRunRequest(
                "retry",
                retry_policy=ExecutionRetryPolicy(
                    max_attempts=3,
                    initial_delay_seconds=0,
                    max_delay_seconds=0,
                ),
            )
        )
        await self.runtime.dispatch_ready_runs()
        waiting = await self.runtime.get_run(run.id)
        self.assertEqual("waiting", waiting.status)  # type: ignore[union-attr]
        await self.runtime.dispatch_ready_runs()
        completed = await self.runtime.get_run(run.id)
        self.assertEqual("succeeded", completed.status)  # type: ignore[union-attr]
        stable = await self.runtime.cancel_run(run.id)
        self.assertEqual("succeeded", stable.status)  # type: ignore[union-attr]

    async def test_durable_wait_replays_after_runtime_restart(self) -> None:
        async def execute(context: Any) -> ExecutionRunResult:
            checkpoint = await context.get_checkpoint("calls")
            calls = (
                int(checkpoint.content["calls"])
                if checkpoint is not None
                and isinstance(checkpoint.content, dict)
                else 0
            ) + 1
            await context.put_checkpoint(
                ExecutionCheckpointWrite(
                    "calls", {"calls": calls}
                )
            )
            outcome = await context.wait_for_external_event(
                "approval"
            )
            return ExecutionRunResult.succeeded_result(
                {
                    "calls": calls,
                    "outcome": outcome.outcome,
                    "approved": outcome.event.payload,
                }
            )

        descriptor = ExecutionHandlerDescriptor(
            "approval", "Approval", max_attempts=2
        )
        self.runtime.register_handler(
            DelegateExecutionHandler(descriptor, execute)
        )
        run = await self.runtime.start_run(
            ExecutionRunRequest("approval")
        )
        await self.runtime.dispatch_ready_runs()
        waiting = await self.runtime.get_run(run.id)
        self.assertEqual("waiting", waiting.status)  # type: ignore[union-attr]

        restarted = LocalExecutionRuntime(
            self.database_path, clock=self.clock
        )
        restarted.register_handler(
            DelegateExecutionHandler(descriptor, execute)
        )
        await restarted.raise_event(
            ExecutionExternalEventRequest(
                "approval",
                run_id=run.id,
                payload={"approved": True},
            )
        )
        await restarted.dispatch_ready_runs(
            recover_interrupted_runs=True
        )
        completed = await restarted.get_run(run.id)
        self.assertEqual(
            {
                "calls": 2,
                "outcome": "external_event",
                "approved": {"approved": True},
            },
            completed.result,  # type: ignore[union-attr]
        )

    async def test_scheduled_timer_and_event_timeout_are_durable(
        self,
    ) -> None:
        outcomes: list[str] = []

        async def timer_handler(context: Any) -> ExecutionRunResult:
            outcome = await context.wait_for_timer(
                "reminder", self.clock() + timedelta(seconds=10)
            )
            outcomes.append(outcome.outcome)
            return ExecutionRunResult.succeeded_result()

        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor("timer", "Timer"),
                timer_handler,
            )
        )
        scheduled = await self.runtime.start_run(
            ExecutionRunRequest(
                "timer",
                scheduled_at_utc=self.clock()
                + timedelta(seconds=5),
            )
        )
        self.assertEqual(0, await self.runtime.dispatch_ready_runs())
        self.clock.advance(5)
        await self.runtime.dispatch_ready_runs()
        self.assertEqual(
            "waiting",
            (await self.runtime.get_run(scheduled.id)).status,  # type: ignore[union-attr]
        )
        self.clock.advance(10)
        await self.runtime.dispatch_ready_runs()
        self.assertEqual(["timer"], outcomes)

    async def test_coordination_lease_ownership_and_expiry(self) -> None:
        first = await self.runtime.try_acquire_lease(
            ExecutionLeaseRequest("singleton", "owner-a")
        )
        self.assertIsNotNone(first)
        self.assertIsNone(
            await self.runtime.try_acquire_lease(
                ExecutionLeaseRequest("singleton", "owner-b")
            )
        )
        self.assertFalse(
            await self.runtime.release_lease(
                "singleton", "owner-b"
            )
        )
        self.clock.advance(61)
        second = await self.runtime.try_acquire_lease(
            ExecutionLeaseRequest("singleton", "owner-b")
        )
        self.assertEqual("owner-b", second.owner_id)  # type: ignore[union-attr]

    async def test_concurrency_key_serializes_claims(self) -> None:
        calls: list[str] = []

        async def execute(context: Any) -> ExecutionRunResult:
            calls.append(context.run.id)
            return ExecutionRunResult.succeeded_result()

        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor(
                    "serial",
                    "Serial",
                    concurrency_key="shared",
                ),
                execute,
            )
        )
        first = await self.runtime.start_run(
            ExecutionRunRequest("serial")
        )
        second = await self.runtime.start_run(
            ExecutionRunRequest("serial")
        )
        self.assertEqual(1, await self.runtime.dispatch_ready_runs())
        self.assertEqual(1, await self.runtime.dispatch_ready_runs())
        self.assertEqual({first.id, second.id}, set(calls))

    async def test_running_cancellation_wins_completion_race(self) -> None:
        entered = asyncio.Event()
        released = asyncio.Event()

        async def execute(context: Any) -> ExecutionRunResult:
            entered.set()
            await released.wait()
            return ExecutionRunResult.succeeded_result(
                {"cancelSeen": context.cancellation_requested}
            )

        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor("slow", "Slow"),
                execute,
            )
        )
        run = await self.runtime.start_run(
            ExecutionRunRequest("slow")
        )
        dispatch = asyncio.create_task(
            self.runtime.dispatch_ready_runs()
        )
        await entered.wait()
        requested = await self.runtime.cancel_run(run.id)
        self.assertTrue(requested.cancellation_requested)  # type: ignore[union-attr]
        released.set()
        await dispatch
        completed = await self.runtime.get_run(run.id)
        self.assertEqual("cancelled", completed.status)  # type: ignore[union-attr]
        self.assertEqual("cancelled", completed.failure_class)  # type: ignore[union-attr]

    async def test_external_worker_uses_same_durable_runtime(self) -> None:
        descriptor = ExecutionHandlerDescriptor(
            "external", "External", plugin_id="external-plugin"
        )

        async def execute(context: Any) -> ExecutionRunResult:
            await context.put_checkpoint(
                ExecutionCheckpointWrite("cursor", {"at": 1})
            )
            await context.put_artifact(
                ExecutionArtifactWrite(
                    "external-summary", content={"ok": True}
                )
            )
            return ExecutionRunResult.succeeded_result(
                {"worker": "python"}
            )

        handler = DelegateExecutionHandler(descriptor, execute)
        plugin = StaticExecutionPlugin(
            ExecutionPluginDescriptor(
                plugin_id="external-plugin",
                name="External",
                version="1.0.0",
                handlers=(descriptor,),
            ),
            (handler,),
        )
        self.runtime.register_external_handler(descriptor)
        worker = ExecutionPluginWorker(
            self.runtime,
            (plugin,),
            ExecutionPluginWorkerOptions(
                heartbeat_interval_seconds=None
            ),
        )
        run = await self.runtime.start_run(
            ExecutionRunRequest(
                "external", plugin_id="external-plugin"
            )
        )
        completed = await worker.run_once(run.id)
        self.assertEqual("succeeded", completed.status)  # type: ignore[union-attr]
        self.assertEqual(
            {"worker": "python"}, completed.result  # type: ignore[union-attr]
        )

    async def test_external_lease_expiry_recovery_and_completion_replay(
        self,
    ) -> None:
        descriptor = ExecutionHandlerDescriptor(
            "external", "External"
        )
        self.runtime.register_external_handler(descriptor)
        run = await self.runtime.start_run(
            ExecutionRunRequest("external")
        )
        first = await self.runtime.lease_next(
            run.id, ttl_seconds=5
        )
        self.assertIsNotNone(first)
        self.clock.advance(6)
        second = await self.runtime.lease_next(
            run.id, ttl_seconds=5
        )
        self.assertIsNotNone(second)
        assert first is not None and second is not None
        with self.assertRaises(Exception):
            await self.runtime.complete(
                first, ExecutionRunResult.succeeded_result()
            )
        result = ExecutionRunResult.succeeded_result({"ok": True})
        completed = await self.runtime.complete(second, result)
        replay = await self.runtime.complete(second, result)
        self.assertEqual(completed.id, replay.id)
        self.assertEqual(2, completed.attempt)

    async def test_external_lease_honors_worker_handler_scope_and_ttl(
        self,
    ) -> None:
        descriptor = ExecutionHandlerDescriptor(
            "external-scoped", "External scoped"
        )
        self.runtime.register_external_handler(descriptor)
        run = await self.runtime.start_run(
            ExecutionRunRequest("external-scoped")
        )
        self.assertIsNone(
            await self.runtime.lease_next(
                run.id,
                worker_id="worker-a",
                handler_ids=("different-handler",),
            )
        )
        lease = await self.runtime.lease_next(
            run.id,
            worker_id="worker-a",
            handler_ids=("external-scoped",),
        )
        self.assertIsNotNone(lease)
        self.assertEqual("worker-a", lease.worker_id)  # type: ignore[union-attr]
        with self.assertRaisesRegex(ValueError, "Lease TTL"):
            await self.runtime.heartbeat(lease, 0)  # type: ignore[arg-type]
        with self.assertRaisesRegex(ValueError, "Lease TTL"):
            await self.runtime.lease_next(
                ttl_seconds=86_401,
                worker_id="worker-b",
                handler_ids=("external-scoped",),
            )

    async def test_recovery_requeues_interrupted_native_run(self) -> None:
        calls = 0

        async def execute(_: Any) -> ExecutionRunResult:
            nonlocal calls
            calls += 1
            return ExecutionRunResult.succeeded_result()

        descriptor = ExecutionHandlerDescriptor(
            "recover", "Recover"
        )
        self.runtime.register_handler(
            DelegateExecutionHandler(descriptor, execute)
        )
        run = await self.runtime.start_run(
            ExecutionRunRequest("recover")
        )
        with sqlite3.connect(self.database_path) as connection:
            raw = await self.runtime.get_run(run.id)
            assert raw is not None
            running = raw.to_dict()
            running["status"] = "running"
            connection.execute(
                """
                UPDATE vyral_execution_runs
                SET status = 'running', run_json = ?
                WHERE run_id = ?
                """,
                (json_dump(running), run.id),
            )
        restarted = LocalExecutionRuntime(
            self.database_path, clock=self.clock
        )
        restarted.register_handler(
            DelegateExecutionHandler(descriptor, execute)
        )
        self.assertEqual(
            1,
            await restarted.dispatch_ready_runs(
                recover_interrupted_runs=True
            ),
        )
        self.assertEqual(1, calls)
        self.assertEqual(
            "succeeded",
            (await restarted.get_run(run.id)).status,  # type: ignore[union-attr]
        )

    async def test_maintenance_dry_run_prune_and_reconcile(self) -> None:
        self.runtime.register_handler(
            DelegateExecutionHandler(
                ExecutionHandlerDescriptor("done", "Done"),
                lambda _: ExecutionRunResult.succeeded_result(),
            )
        )
        ids: list[str] = []
        for _ in range(3):
            run = await self.runtime.start_run(
                ExecutionRunRequest("done")
            )
            ids.append(run.id)
            await self.runtime.dispatch_ready_runs()
            self.clock.advance(1)
        preview = await self.runtime.prune(
            ExecutionMaintenancePruneRequest(
                dry_run=True, retain_terminal_runs=1
            )
        )
        self.assertEqual(2, preview.runs)
        for run_id in preview.run_ids:
            self.assertIsNotNone(
                await self.runtime.get_run(run_id)
            )
        pruned = await self.runtime.prune(
            ExecutionMaintenancePruneRequest(
                dry_run=False, retain_terminal_runs=1
            )
        )
        self.assertEqual(2, pruned.runs)
        self.assertEqual(
            1,
            len(
                await self.runtime.list_runs(
                    ExecutionRunQuery(limit=10)
                )
            ),
        )
        reconcile = await self.runtime.reconcile_dispatch(
            ExecutionMaintenanceDispatchReconcileRequest(
                dry_run=True
            )
        )
        self.assertEqual((), reconcile.candidate_run_ids)
        status = await self.runtime.get_maintenance_status()
        self.assertEqual(1, status.run_counts["succeeded"])
        self.assertTrue(
            bool(self.runtime.diagnostics()["healthy"])
        )


def json_dump(value: object) -> str:
    import json

    return json.dumps(value, separators=(",", ":"))


if __name__ == "__main__":
    unittest.main()
