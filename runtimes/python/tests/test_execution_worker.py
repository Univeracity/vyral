from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from threading import Thread
import unittest

from jsonschema import Draft202012Validator

from vyral_runtime import (
    DelegateExecutionHandler,
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionHandlerHarness,
    ExecutionPluginDescriptor,
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    ExecutionRunContext,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionWorkerHttpError,
    ExecutionWorkerTelemetry,
    ExecutionWorkerWaitRequest,
    HttpExecutionWorkerTransport,
    InMemoryExecutionWorkerTransport,
    StaticExecutionWorkerTokenSource,
    StaticExecutionPlugin,
    load_contract_bundle,
)


NOW = datetime(2026, 7, 30, 16, 0, tzinfo=timezone.utc)


def plugin_for(
    plugin_id: str,
    handler_id: str,
    execute: object,
) -> StaticExecutionPlugin:
    descriptor = ExecutionHandlerDescriptor(
        handler_id=handler_id,
        plugin_id=plugin_id,
        display_name=handler_id,
    )
    handler = DelegateExecutionHandler(descriptor, execute)  # type: ignore[arg-type]
    return StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=plugin_id,
            name=plugin_id,
            version="1.0.0",
        ),
        (handler,),
    )


class ExecutionWorkerTests(unittest.TestCase):
    def test_handler_harness_supports_progress_events_artifacts_and_checkpoints(
        self,
    ) -> None:
        async def execute(context: ExecutionRunContext) -> ExecutionRunResult:
            self.assertIsNone(await context.get_checkpoint("state"))
            await context.put_checkpoint(
                ExecutionCheckpointWrite(
                    key="state",
                    content={"step": 1},
                )
            )
            await context.report(
                ExecutionRunUpdate(
                    progress=0.5,
                    current_step="transform",
                )
            )
            await context.record_event(
                "step.completed",
                message="transformed",
                details={"count": 1},
            )
            await context.put_artifact(
                ExecutionArtifactWrite(
                    name="summary",
                    kind="json",
                    content={"ok": True},
                )
            )
            return ExecutionRunResult.succeeded_result({"ok": True})

        plugin = plugin_for(
            "python.worker.plugin",
            "python.worker.handler",
            execute,
        )
        harness = ExecutionHandlerHarness(
            plugin,
            clock=lambda: NOW,
        )

        completed = asyncio.run(
            harness.run(
                "python.worker.handler",
                payload={"value": 42},
                run_id="run-one",
            )
        )

        self.assertEqual("succeeded", completed.status)
        self.assertEqual({"ok": True}, completed.result)
        self.assertEqual(1.0, completed.progress)
        self.assertIsNone(completed.current_step)
        artifacts = asyncio.run(
            harness.transport.get_artifacts(completed.id)
        )
        checkpoints = asyncio.run(
            harness.transport.get_checkpoints(completed.id)
        )
        events = asyncio.run(harness.transport.get_events(completed.id))
        self.assertEqual(("summary",), tuple(item.name for item in artifacts))
        self.assertEqual(("state",), tuple(item.key for item in checkpoints))
        self.assertEqual(("step.completed",), tuple(item.type for item in events))

        self._assert_schema(
            "ExecutionPluginDescriptor",
            plugin.descriptor.to_dict(),
        )
        self._assert_schema("ExecutionRun", completed.to_dict())
        self._assert_schema("ExecutionArtifact", artifacts[0].to_dict())
        self._assert_schema("ExecutionCheckpoint", checkpoints[0].to_dict())

    def test_durable_wait_suspends_and_replays_without_handler_changes(
        self,
    ) -> None:
        async def execute(context: ExecutionRunContext) -> ExecutionRunResult:
            if await context.get_checkpoint("before-wait") is None:
                await context.put_checkpoint(
                    {
                        "key": "before-wait",
                        "content": {"ready": True},
                        "metadata": {},
                    }
                )
            outcome = await context.wait_for_external_event(
                "approval",
                timeout_at_utc=datetime(
                    2026, 7, 30, 17, 0, tzinfo=timezone.utc
                ),
            )
            approved = bool(
                outcome.event
                and isinstance(outcome.event.payload, dict)
                and outcome.event.payload.get("approved") is True
            )
            await context.put_artifact(
                {
                    "name": "approval-summary",
                    "kind": "json",
                    "content": {"approved": approved},
                    "metadata": {},
                }
            )
            return ExecutionRunResult.succeeded_result(
                {"approved": approved}
            )

        plugin = plugin_for(
            "python.wait.plugin",
            "python.wait.handler",
            execute,
        )
        transport = InMemoryExecutionWorkerTransport(
            "worker-a",
            ("python.wait.handler",),
            clock=lambda: NOW,
            token_factory=lambda: "lease-secret",
        )
        worker = ExecutionPluginWorker(
            transport,
            (plugin,),
            ExecutionPluginWorkerOptions(
                heartbeat_interval_seconds=None,
            ),
        )

        async def scenario() -> tuple[object, object]:
            accepted = await transport.enqueue_run(
                "python.wait.handler",
                plugin_id="python.wait.plugin",
                run_id="run-wait",
            )
            waiting = await worker.run_once(accepted.id)
            await transport.raise_external_event(
                accepted.id,
                "approval",
                {"approved": True},
            )
            completed = await worker.run_once(accepted.id)
            return waiting, completed

        waiting, completed = asyncio.run(scenario())
        self.assertIsNotNone(waiting)
        self.assertIsNotNone(completed)
        assert hasattr(waiting, "status") and hasattr(completed, "status")
        self.assertEqual("waiting", waiting.status)
        self.assertEqual("succeeded", completed.status)
        self.assertEqual({"approved": True}, completed.result)
        artifacts = asyncio.run(transport.get_artifacts("run-wait"))
        checkpoints = asyncio.run(transport.get_checkpoints("run-wait"))
        self.assertEqual("approval-summary", artifacts[0].name)
        self.assertEqual("before-wait", checkpoints[0].key)

    def test_heartbeat_cancellation_completes_portable_cancelled_result(
        self,
    ) -> None:
        started = asyncio.Event()

        async def execute(_: ExecutionRunContext) -> ExecutionRunResult:
            started.set()
            await asyncio.Event().wait()
            return ExecutionRunResult.succeeded_result()

        plugin = plugin_for(
            "python.cancel.plugin",
            "python.cancel.handler",
            execute,
        )

        async def scenario() -> tuple[object, InMemoryExecutionWorkerTransport]:
            transport = InMemoryExecutionWorkerTransport(
                "worker-a",
                ("python.cancel.handler",),
            )
            accepted = await transport.enqueue_run(
                "python.cancel.handler",
                plugin_id="python.cancel.plugin",
                run_id="run-cancel",
            )
            worker = ExecutionPluginWorker(
                transport,
                (plugin,),
                ExecutionPluginWorkerOptions(
                    lease_ttl_seconds=1,
                    heartbeat_interval_seconds=0.01,
                ),
            )
            task = asyncio.create_task(worker.run_once(accepted.id))
            await asyncio.wait_for(started.wait(), timeout=1)
            await transport.request_cancellation(accepted.id)
            completed = await asyncio.wait_for(task, timeout=1)
            return completed, transport

        completed, transport = asyncio.run(scenario())
        self.assertIsNotNone(completed)
        assert hasattr(completed, "status")
        self.assertEqual("cancelled", completed.status)
        self.assertEqual("cancelled", completed.failure_class)
        self.assertGreater(transport.heartbeat_count, 0)

    def test_handler_failure_is_redacted_and_completion_is_not_retried(
        self,
    ) -> None:
        def fail(_: ExecutionRunContext) -> ExecutionRunResult:
            raise RuntimeError("dependency-secret-must-not-escape")

        failed_plugin = plugin_for(
            "python.failure.plugin",
            "python.failure.handler",
            fail,
        )

        async def failure_scenario() -> object:
            transport = InMemoryExecutionWorkerTransport(
                "worker-a",
                ("python.failure.handler",),
                token_factory=lambda: "lease-secret",
            )
            accepted = await transport.enqueue_run(
                "python.failure.handler",
                plugin_id="python.failure.plugin",
            )
            worker = ExecutionPluginWorker(
                transport,
                (failed_plugin,),
                ExecutionPluginWorkerOptions(
                    heartbeat_interval_seconds=None,
                ),
            )
            return await worker.run_once(accepted.id)

        failed = asyncio.run(failure_scenario())
        self.assertIsNotNone(failed)
        assert hasattr(failed, "error")
        self.assertEqual("External plugin handler failed.", failed.error)
        self.assertNotIn("secret", failed.error or "")

        async def succeed(_: ExecutionRunContext) -> ExecutionRunResult:
            return ExecutionRunResult.succeeded_result()

        plugin = plugin_for(
            "python.complete.plugin",
            "python.complete.handler",
            succeed,
        )

        async def completion_scenario() -> int:
            transport = InMemoryExecutionWorkerTransport(
                "worker-a",
                ("python.complete.handler",),
            )
            accepted = await transport.enqueue_run(
                "python.complete.handler",
                plugin_id="python.complete.plugin",
            )
            transport.fail_completion = True
            worker = ExecutionPluginWorker(
                transport,
                (plugin,),
                ExecutionPluginWorkerOptions(
                    heartbeat_interval_seconds=None,
                ),
            )
            with self.assertRaisesRegex(
                RuntimeError, "transport completion failed"
            ):
                await worker.run_once(accepted.id)
            return transport.completion_count

        self.assertEqual(1, asyncio.run(completion_scenario()))

    def test_lease_secret_is_excluded_from_repr_and_safe_summary(self) -> None:
        async def scenario() -> object:
            transport = InMemoryExecutionWorkerTransport(
                "worker-a",
                ("handler-a",),
                clock=lambda: NOW,
                token_factory=lambda: "lease-secret",
            )
            run = await transport.enqueue_run(
                "handler-a",
                run_id="run-a",
            )
            return await transport.lease_next(run.id)

        lease = asyncio.run(scenario())
        self.assertIsNotNone(lease)
        assert hasattr(lease, "safe_summary")
        self.assertNotIn("lease-secret", repr(lease))
        self.assertNotIn("lease-secret", str(lease.safe_summary()))
        self._assert_schema("ExecutionExternalWorkerLease", lease.to_dict())

    def test_http_transport_covers_portable_protocol_without_token_leaks(
        self,
    ) -> None:
        requests: list[tuple[str, dict[str, str], dict[str, object]]] = []
        fail_paths: set[str] = set()
        run = {
            "id": "run-http",
            "handlerId": "handler-a",
            "pluginId": "plugin-a",
            "status": "running",
            "attempt": 1,
            "maxAttempts": 1,
            "retryPolicy": {},
            "idempotencyKey": None,
            "correlationId": "correlation-a",
            "scope": None,
            "payloadHash": "sha256:payload",
            "payload": {"value": 1},
            "createdAtUtc": "2026-07-30T16:00:00Z",
            "scheduledAtUtc": None,
            "startedAtUtc": "2026-07-30T16:00:00Z",
            "updatedAtUtc": "2026-07-30T16:00:00Z",
            "completedAtUtc": None,
            "durationMs": None,
            "cancellationRequested": False,
            "requested": None,
            "attempted": None,
            "succeeded": None,
            "failed": None,
            "progress": None,
            "currentStep": None,
            "failureClass": None,
            "error": None,
            "result": None,
            "statusDetails": None,
            "tags": {},
        }
        lease_document = {
            "leaseKey": "lease-a",
            "leaseToken": "lease-secret",
            "workerId": "worker-a",
            "run": run,
            "acquiredAtUtc": "2026-07-30T16:00:00Z",
            "expiresAtUtc": "2026-07-30T16:01:00Z",
        }

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length))
                requests.append(
                    (
                        self.path,
                        {key: value for key, value in self.headers.items()},
                        body,
                    )
                )
                if self.path in fail_paths:
                    self.send_response(500)
                    self.send_header("Content-Type", "text/plain")
                    self.end_headers()
                    self.wfile.write(b"response-secret lease-secret")
                    return
                if self.path.endswith("/events"):
                    self.send_response(204)
                    self.end_headers()
                    return
                if self.path.endswith("/checkpoints/read"):
                    self.send_response(404)
                    self.end_headers()
                    return
                if self.path == "/execution/workers/leases":
                    response: object = lease_document
                elif self.path.endswith("/heartbeat"):
                    response = lease_document
                elif self.path.endswith("/reports"):
                    response = {**run, "progress": 0.5}
                elif self.path.endswith("/artifacts"):
                    response = {
                        "id": "artifact-a",
                        "runId": "run-http",
                        "name": "summary",
                        "kind": "json",
                        "mediaType": "application/json",
                        "contentHash": "sha256:artifact",
                        "sizeBytes": 11,
                        "text": None,
                        "content": {"ok": True},
                        "uri": None,
                        "createdAtUtc": "2026-07-30T16:00:01Z",
                        "metadata": {},
                    }
                elif self.path.endswith("/checkpoints"):
                    response = {
                        "runId": "run-http",
                        "key": "state",
                        "contentHash": "sha256:checkpoint",
                        "updatedAtUtc": "2026-07-30T16:00:01Z",
                        "content": {"step": 1},
                        "metadata": {},
                    }
                elif self.path.endswith("/wait"):
                    response = {
                        "run": run,
                        "suspended": False,
                        "outcome": {
                            "name": "approval",
                            "outcome": "timed_out",
                            "event": None,
                            "timer": None,
                        },
                    }
                elif self.path.endswith("/complete"):
                    result = body["result"]
                    response = {
                        **run,
                        "status": result["status"],
                        "result": result.get("result"),
                        "failureClass": result.get("failureClass"),
                        "error": result.get("error"),
                        "completedAtUtc": "2026-07-30T16:00:02Z",
                    }
                else:
                    self.send_response(404)
                    self.end_headers()
                    return
                encoded = json.dumps(response).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)

            def log_message(self, format: str, *args: object) -> None:
                return

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        telemetry: list[ExecutionWorkerTelemetry] = []
        transport = HttpExecutionWorkerTransport(
            f"http://127.0.0.1:{server.server_port}",
            "worker-a",
            ("handler-a",),
            token_source=StaticExecutionWorkerTokenSource("auth-secret"),
            observe=telemetry.append,
            allow_insecure_http=True,
        )

        async def scenario() -> None:
            lease = await transport.lease_next("run-http")
            self.assertIsNotNone(lease)
            assert lease is not None
            await transport.heartbeat(lease)
            await transport.report(
                lease,
                ExecutionRunUpdate(progress=0.5),
            )
            await transport.record_event(
                lease,
                "log",
                message="safe",
            )
            artifact = await transport.put_artifact(
                lease,
                ExecutionArtifactWrite(
                    name="summary",
                    content={"ok": True},
                ),
            )
            checkpoint = await transport.checkpoint(
                lease,
                ExecutionCheckpointWrite(
                    key="state",
                    content={"step": 1},
                ),
            )
            self.assertIsNone(
                await transport.get_checkpoint(lease, "missing")
            )
            wait = await transport.wait(
                lease,
                ExecutionWorkerWaitRequest(
                    kind="external_event",
                    name="approval",
                ),
            )
            completed = await transport.complete(
                lease,
                ExecutionRunResult.succeeded_result({"ok": True}),
            )
            self.assertEqual("artifact-a", artifact.id)
            self.assertEqual("state", checkpoint.key)
            self.assertEqual("timed_out", wait.outcome.outcome if wait.outcome else None)
            self.assertEqual("succeeded", completed.status)

            fail_paths.add("/execution/workers/leases/complete")
            with self.assertRaises(ExecutionWorkerHttpError) as captured:
                await transport.complete(
                    lease,
                    ExecutionRunResult.succeeded_result(),
                )
            self.assertNotIn("secret", str(captured.exception).lower())

        try:
            asyncio.run(scenario())
        finally:
            transport.close()
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

        self.assertEqual(
            "Bearer auth-secret",
            requests[0][1]["Authorization"],
        )
        self.assertTrue(all("secret" not in path for path, _, _ in requests))
        self.assertTrue(
            all("secret" not in repr(item).lower() for item in telemetry)
        )
        self.assertEqual("http_failure", telemetry[-1].error)
        definitions = (
            "ExecutionExternalWorkerLeaseRequest",
            "ExecutionExternalWorkerHeartbeatRequest",
            "ExecutionExternalWorkerReportRequest",
            "ExecutionExternalWorkerEventRequest",
            "ExecutionExternalWorkerArtifactRequest",
            "ExecutionExternalWorkerCheckpointRequest",
            "ExecutionExternalWorkerCheckpointReadRequest",
            "ExecutionExternalWorkerWaitRequest",
            "ExecutionExternalWorkerCompletionRequest",
            "ExecutionExternalWorkerCompletionRequest",
        )
        for definition, (_, _, body) in zip(definitions, requests):
            self._assert_schema(definition, body)

    def _assert_schema(self, name: str, value: object) -> None:
        schema = load_contract_bundle().schema
        validator = Draft202012Validator(
            {
                "$ref": f"#/$defs/{name}",
                "$defs": schema["$defs"],
            }
        )
        errors = sorted(
            validator.iter_errors(value),
            key=lambda error: tuple(
                str(part) for part in error.absolute_path
            ),
        )
        self.assertEqual(
            [],
            [
                f"{'.'.join(map(str, error.absolute_path))}: "
                f"{error.message}"
                for error in errors
            ],
        )


if __name__ == "__main__":
    unittest.main()
