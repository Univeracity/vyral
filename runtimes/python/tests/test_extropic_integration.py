from __future__ import annotations

import asyncio
from dataclasses import replace
from datetime import datetime, timezone
from hashlib import sha256
import json
from types import SimpleNamespace
from threading import Event
import unittest
from typing import Any, cast
from unittest.mock import patch

from vyral_runtime import (
    DelegateExecutionHandler,
    ExecutionArtifact,
    ExecutionArtifactWrite,
    ExecutionCheckpoint,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionHandlerHarness,
    ExecutionPluginDescriptor,
    ExecutionRun,
    ExecutionRunResult,
    ExecutionRunUpdate,
    StaticExecutionPlugin,
)
from vyral_runtime.contracts import JSONValue
from vyral_runtime.integrations.extropic import (
    EXTROPIC_CHECKPOINT_KEY,
    ExtropicAdapterOptions,
    ExtropicAtCapacityError,
    ExtropicCreatedJob,
    ExtropicDependencyError,
    ExtropicExecutionAdapter,
    ExtropicJobSnapshot,
    ExtropicOutOfCreditsError,
    ExtropicPreparedJob,
    ExtropicProviderError,
    ExtropicRateLimitedError,
    ExtropicSdkBackend,
    ExtropicTransportError,
)


NOW = datetime(2026, 8, 11, 12, 0, tzinfo=timezone.utc)


async def no_sleep(_: float) -> None:
    return None


class FakeExtropicBackend:
    def __init__(self) -> None:
        self.create_calls = 0
        self.upload_calls = 0
        self.start_calls = 0
        self.inspect_calls = 0
        self.cancel_calls = 0
        self.result_calls = 0
        self.create_error: Exception | None = None
        self.upload_error: Exception | None = None
        self.start_effects: list[ExtropicJobSnapshot | Exception] = []
        self.inspect_effects: list[ExtropicJobSnapshot | Exception] = []
        self.cancelled = False
        self.result_value: Any = {"samples": 32, "seed": 7}
        self.artifact_error: Exception | None = None
        self.artifacts = {
            "stdout": "simulation complete\n",
            "stderr": "",
            "traceback": None,
        }

    def prepare(
        self,
        workload: object,
        payload: JSONValue,
        timeout_seconds: float | None,
    ) -> ExtropicPreparedJob:
        del workload, payload
        return ExtropicPreparedJob(
            payload=b"registered-workload",
            manifest={"timeout_s": timeout_seconds},
        )

    def create_job(self, tier: str) -> ExtropicCreatedJob:
        self.create_calls += 1
        if self.create_error is not None:
            raise self.create_error
        return ExtropicCreatedJob(
            job_id="extropic-job-1",
            input_path=f"inputs/{tier}/one",
            input_token="DO-NOT-PERSIST-THIS-TOKEN",
            artifact_url="https://artifacts.example.invalid",
        )

    def upload_job(
        self,
        created: ExtropicCreatedJob,
        prepared: ExtropicPreparedJob,
    ) -> None:
        del created, prepared
        self.upload_calls += 1
        if self.upload_error is not None:
            raise self.upload_error

    def start_job(
        self,
        job_id: str,
        manifest: object,
    ) -> ExtropicJobSnapshot:
        del manifest
        self.start_calls += 1
        effect: ExtropicJobSnapshot | Exception = (
            self.start_effects.pop(0)
            if self.start_effects
            else ExtropicJobSnapshot(job_id, "pending")
        )
        if isinstance(effect, Exception):
            raise effect
        return effect

    def inspect_job(self, job_id: str) -> ExtropicJobSnapshot:
        self.inspect_calls += 1
        if self.cancelled:
            return ExtropicJobSnapshot(job_id, "cancelled")
        effect: ExtropicJobSnapshot | Exception = (
            self.inspect_effects.pop(0)
            if self.inspect_effects
            else ExtropicJobSnapshot(job_id, "succeeded")
        )
        if isinstance(effect, Exception):
            raise effect
        return effect

    def cancel_job(self, job_id: str) -> None:
        del job_id
        self.cancel_calls += 1
        self.cancelled = True

    def result(self, job_id: str) -> Any:
        del job_id
        self.result_calls += 1
        if isinstance(self.result_value, Exception):
            raise self.result_value
        return self.result_value

    def artifact_text(self, job_id: str, name: str) -> str | None:
        del job_id
        if self.artifact_error is not None:
            raise self.artifact_error
        return self.artifacts[name]


class FakeSdkError(Exception):
    pass


class FakeSdkRateLimited(FakeSdkError):
    def __init__(self, retry_after: float | None = None) -> None:
        super().__init__("rate limited")
        self.retry_after = retry_after


class FakeSdkOutOfCredits(FakeSdkError):
    pass


class FakeSdkAtCapacity(FakeSdkError):
    def __init__(self, job: object | None = None) -> None:
        super().__init__("at capacity")
        self.job = job


class FakeSdkClient:
    def __init__(self) -> None:
        self.error: Exception | None = None
        self.uploaded: tuple[object, ...] | None = None
        self.cancelled: str | None = None
        self.artifact: bytes | None = b"artifact text"

    def _effect(self) -> None:
        if self.error is not None:
            error, self.error = self.error, None
            raise error

    def create_job(self, tier: str) -> dict[str, object]:
        self._effect()
        return {
            "id": "sdk-job",
            "status": "uploading",
            "input_path": f"input/{tier}",
            "input_token": "sdk-secret",
            "artifact_url": "https://artifacts.example.invalid",
        }

    def upload_input(self, *args: object, **kwargs: object) -> None:
        self._effect()
        self.uploaded = (*args, kwargs)

    def start_job(self, job_id: str, manifest: object) -> dict[str, object]:
        del manifest
        self._effect()
        return {"id": job_id, "status": "provisioning"}

    def get(self, job_id: str) -> dict[str, object]:
        self._effect()
        return {"id": job_id, "status": "running", "progress": 0.25}

    def cancel(self, job_id: str) -> dict[str, object]:
        self._effect()
        self.cancelled = job_id
        return {"id": job_id, "status": "cancelled"}

    def download_artifact(self, job_id: str, name: str) -> bytes | None:
        del job_id, name
        self._effect()
        return self.artifact


class FakeSdkJob:
    def __init__(self, job_id: str, client: FakeSdkClient) -> None:
        self.id = job_id
        self.client = client

    def result(self) -> dict[str, bool]:
        self.client._effect()
        return {"sdk": True}


class FakeSdk:
    __version__ = "0.5.0"
    ExtropicError = FakeSdkError
    RateLimited = FakeSdkRateLimited
    OutOfCredits = FakeSdkOutOfCredits
    AtCapacity = FakeSdkAtCapacity
    Job = FakeSdkJob

    def __init__(self, client: FakeSdkClient) -> None:
        self._client = client

    def Client(self) -> FakeSdkClient:
        return self._client


class MemoryRunContext:
    def __init__(
        self,
        payload: JSONValue,
        *,
        cancellation_requested: bool = False,
        cancel_on_status: str | None = None,
    ) -> None:
        encoded = json.dumps(
            payload,
            sort_keys=True,
            allow_nan=False,
            separators=(",", ":"),
        ).encode()
        self.run = ExecutionRun(
            id="run-extropic",
            handler_id="example.extropic",
            status="running",
            attempt=1,
            max_attempts=3,
            correlation_id="run-extropic",
            payload_hash="sha256:" + sha256(encoded).hexdigest(),
            payload=payload,
            created_at_utc=NOW,
            updated_at_utc=NOW,
            cancellation_requested=cancellation_requested,
            tags={},
        )
        self.checkpoints: dict[str, ExecutionCheckpoint] = {}
        self.artifacts: list[ExecutionArtifact] = []
        self.events: list[tuple[str, str | None, dict[str, Any] | None]] = []
        self.cancel_on_status = cancel_on_status

    @property
    def cancellation_requested(self) -> bool:
        return self.run.cancellation_requested

    async def report(self, update: object) -> ExecutionRun:
        selected = ExecutionRunUpdate.from_value(cast(Any, update))
        changes = {
            name: getattr(selected, name)
            for name in (
                "status",
                "requested",
                "attempted",
                "succeeded",
                "failed",
                "progress",
                "current_step",
                "failure_class",
                "error",
                "status_details",
            )
            if getattr(selected, name) is not None
        }
        self.run = replace(self.run, **changes)
        if selected.status == self.cancel_on_status:
            self.run = replace(self.run, cancellation_requested=True)
        return self.run

    async def record_event(
        self,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: dict[str, Any] | None = None,
    ) -> None:
        del severity
        self.events.append((event_type, message, details))

    async def put_artifact(self, artifact: object) -> ExecutionArtifact:
        selected = ExecutionArtifactWrite.from_value(cast(Any, artifact))
        result = ExecutionArtifact(
            id=f"artifact-{len(self.artifacts) + 1}",
            run_id=self.run.id,
            name=selected.name,
            kind=selected.kind,
            media_type=selected.media_type,
            content_hash="sha256:test",
            size_bytes=len((selected.text or "").encode()),
            text=selected.text,
            content=selected.content,
            uri=selected.uri,
            created_at_utc=NOW,
            metadata=selected.metadata,
        )
        self.artifacts.append(result)
        return result

    async def put_checkpoint(self, checkpoint: object) -> ExecutionCheckpoint:
        selected = ExecutionCheckpointWrite.from_value(cast(Any, checkpoint))
        result = ExecutionCheckpoint(
            run_id=self.run.id,
            key=selected.key,
            content_hash="sha256:test",
            updated_at_utc=NOW,
            content=selected.content,
            metadata=selected.metadata,
        )
        self.checkpoints[selected.key] = result
        return result

    async def get_checkpoint(self, key: str) -> ExecutionCheckpoint | None:
        return self.checkpoints.get(key)

    async def wait_for_external_event(self, *_: object, **__: object) -> Any:
        raise AssertionError("not used")

    async def wait_for_timer(self, *_: object, **__: object) -> Any:
        raise AssertionError("not used")


def adapter_for(
    backend: FakeExtropicBackend,
    *,
    options: ExtropicAdapterOptions | None = None,
) -> ExtropicExecutionAdapter:
    def registered_workload(payload: JSONValue) -> JSONValue:
        return payload

    return ExtropicExecutionAdapter(
        "example.thermodynamic-simulation.v1",
        registered_workload,
        options=options
        or ExtropicAdapterOptions(
            poll_interval_seconds=0.001,
            max_artifact_bytes=64,
        ),
        backend=backend,
        sleep=no_sleep,
    )


class ExtropicIntegrationTests(unittest.TestCase):
    def test_sdk_backend_uses_staged_public_operations(self) -> None:
        client = FakeSdkClient()
        sdk = FakeSdk(client)
        cloudpickle = SimpleNamespace(
            __version__="3.1.1",
            dumps=lambda value: b"serialized-registered-workload",
        )

        def imported(name: str) -> object:
            if name == "extro_sim":
                return sdk
            if name == "cloudpickle":
                return cloudpickle
            raise AssertionError(f"unexpected import {name}")

        with patch(
            "vyral_runtime.integrations.extropic.importlib.import_module",
            side_effect=imported,
        ):
            backend = ExtropicSdkBackend(client)
            prepared = backend.prepare(lambda value: value, {"seed": 7}, 30)
            created = backend.create_job("l4")
            backend.upload_job(created, prepared)
            started = backend.start_job(created.job_id, prepared.manifest)
            inspected = backend.inspect_job(created.job_id)
            result = backend.result(created.job_id)
            artifact = backend.artifact_text(created.job_id, "stdout")
            backend.cancel_job(created.job_id)

        submission = json.loads(prepared.payload)
        self.assertEqual("__extropic_cloudpickle__", submission["entrypoint"])
        self.assertEqual("sdk-job", created.job_id)
        self.assertEqual("provisioning", started.status)
        self.assertEqual(0.25, inspected.progress)
        self.assertEqual({"sdk": True}, result)
        self.assertEqual("artifact text", artifact)
        self.assertEqual("sdk-job", client.cancelled)
        self.assertIsNotNone(client.uploaded)

    def test_sdk_backend_translates_provider_errors_without_detail(self) -> None:
        client = FakeSdkClient()
        sdk = FakeSdk(client)
        backend = ExtropicSdkBackend(client)
        with patch(
            "vyral_runtime.integrations.extropic.importlib.import_module",
            return_value=sdk,
        ):
            backend.inspect_job("sdk-job")

        client.error = FakeSdkRateLimited(4.0)
        with self.assertRaises(ExtropicRateLimitedError) as rate_limited:
            backend.inspect_job("sdk-job")
        self.assertEqual(4.0, rate_limited.exception.retry_after_seconds)

        client.error = FakeSdkOutOfCredits("private billing detail")
        with self.assertRaises(ExtropicOutOfCreditsError) as credits:
            backend.create_job("cpu")
        self.assertNotIn("private billing detail", str(credits.exception))

        client.error = FakeSdkAtCapacity(SimpleNamespace(id="safe-job"))
        with self.assertRaises(ExtropicAtCapacityError) as capacity:
            backend.start_job("sdk-job", {})
        self.assertEqual("safe-job", capacity.exception.job_id)

        client.error = OSError("private network detail")
        with self.assertRaises(ExtropicTransportError) as transport:
            backend.inspect_job("sdk-job")
        self.assertNotIn("private network detail", str(transport.exception))

        client.error = FakeSdkError("private provider detail")
        with self.assertRaises(ExtropicProviderError) as provider:
            backend.inspect_job("sdk-job")
        self.assertNotIn("private provider detail", str(provider.exception))

        client.error = ValueError("private sdk detail")
        with self.assertRaises(ExtropicProviderError) as unknown:
            backend.inspect_job("sdk-job")
        self.assertNotIn("private sdk detail", str(unknown.exception))

    def test_sdk_backend_rejects_missing_or_incompatible_sdk(self) -> None:
        backend = ExtropicSdkBackend()
        with patch(
            "vyral_runtime.integrations.extropic.importlib.import_module",
            side_effect=ImportError("missing"),
        ):
            with self.assertRaises(ExtropicDependencyError):
                backend.prepare(lambda value: value, None, None)

        incompatible = ExtropicSdkBackend()
        with patch(
            "vyral_runtime.integrations.extropic.importlib.import_module",
            return_value=SimpleNamespace(__version__="0.6.0"),
        ):
            with self.assertRaises(ExtropicDependencyError):
                incompatible.prepare(lambda value: value, None, None)

    def test_configuration_and_provider_models_validate_boundaries(self) -> None:
        invalid_options = (
            {"tier": "tpu"},
            {"timeout_seconds": 0},
            {"poll_interval_seconds": 0},
            {"capacity_retry_attempts": 101},
            {"provider_error_retries": 26},
            {"max_serialized_bytes": 0},
            {"max_artifact_bytes": 1_048_577},
            {"seed_field": " "},
        )
        for values in invalid_options:
            with self.subTest(values=values):
                with self.assertRaises(ValueError):
                    ExtropicAdapterOptions(**values)  # type: ignore[arg-type]

        with self.assertRaises(ValueError):
            ExtropicCreatedJob("", "input", "token")
        with self.assertRaises(ValueError):
            ExtropicCreatedJob("job", "", "token")
        with self.assertRaises(ValueError):
            ExtropicJobSnapshot("job", "")
        with self.assertRaises(ValueError):
            ExtropicJobSnapshot("job", "running", float("nan"))
        with self.assertRaises(ValueError):
            ExtropicExecutionAdapter("", lambda value: value)
        with self.assertRaises(TypeError):
            ExtropicExecutionAdapter("workload", cast(Any, None))

        async def async_workload(value: JSONValue) -> JSONValue:
            return value

        with self.assertRaises(TypeError):
            ExtropicExecutionAdapter("workload", async_workload)

    def test_success_is_managed_by_vyral_and_retains_bounded_artifacts(self) -> None:
        backend = FakeExtropicBackend()
        backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot(
                "extropic-job-1",
                "running",
                {"completed": 1, "total": 2},
            ),
            ExtropicJobSnapshot("extropic-job-1", "succeeded"),
        ]
        backend.start_effects = [
            ExtropicJobSnapshot("extropic-job-1", "provisioning")
        ]
        backend.artifacts["stdout"] = "x" * 100
        adapter = adapter_for(backend)
        descriptor = ExecutionHandlerDescriptor(
            handler_id="example.extropic",
            plugin_id="example.plugin",
            display_name="Extropic simulation",
        )
        plugin = StaticExecutionPlugin(
            ExecutionPluginDescriptor(
                plugin_id="example.plugin",
                name="Example",
                version="1.0.0",
            ),
            (DelegateExecutionHandler(descriptor, adapter.execute),),
        )
        harness = ExecutionHandlerHarness(plugin, clock=lambda: NOW)

        completed = asyncio.run(
            harness.run(
                "example.extropic",
                payload={"seed": 7, "samples": 32},
                run_id="run-extropic-success",
            )
        )

        self.assertEqual("succeeded", completed.status)
        self.assertEqual({"samples": 32, "seed": 7}, completed.result)
        self.assertEqual("extropic-job-1", completed.status_details["providerJobId"])
        self.assertEqual(1, backend.create_calls)
        self.assertEqual(1, backend.upload_calls)
        self.assertEqual(1, backend.start_calls)
        checkpoints = asyncio.run(
            harness.transport.get_checkpoints(completed.id)
        )
        checkpoint_text = json.dumps(checkpoints[0].content)
        self.assertNotIn("DO-NOT-PERSIST", checkpoint_text)
        self.assertEqual("terminal", checkpoints[0].content["phase"])
        artifacts = asyncio.run(harness.transport.get_artifacts(completed.id))
        self.assertEqual(
            {"extropic.stdout", "extropic.stderr"},
            {artifact.name for artifact in artifacts},
        )
        stdout = next(
            artifact for artifact in artifacts if artifact.name == "extropic.stdout"
        )
        self.assertEqual(64, stdout.size_bytes)
        self.assertEqual("true", stdout.metadata["truncated"])

    def test_replay_reuses_known_provider_job_after_ambiguous_start(self) -> None:
        backend = FakeExtropicBackend()
        backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot("extropic-job-1", "succeeded"),
        ]
        backend.start_effects = [
            ExtropicTransportError("start outcome unknown"),
            ExtropicJobSnapshot("extropic-job-1", "pending"),
        ]
        adapter = adapter_for(backend)
        context = MemoryRunContext({"seed": 7})

        first = asyncio.run(adapter.execute(cast(Any, context)))
        second = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("failed", first.status)
        self.assertEqual("transient", first.failure_class)
        self.assertEqual("succeeded", second.status)
        self.assertEqual(1, backend.create_calls)
        self.assertEqual(1, backend.upload_calls)
        self.assertEqual(2, backend.start_calls)
        self.assertEqual(
            "extropic-job-1",
            context.checkpoints[EXTROPIC_CHECKPOINT_KEY].content["providerJobId"],
        )

    def test_ambiguous_creation_fails_closed_without_resubmission(self) -> None:
        backend = FakeExtropicBackend()
        backend.create_error = ExtropicTransportError("response lost")
        adapter = adapter_for(backend)
        context = MemoryRunContext({"seed": 7})

        first = asyncio.run(adapter.execute(cast(Any, context)))
        second = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("failed", first.status)
        self.assertEqual("failed", second.status)
        self.assertTrue(first.status_details["submissionAmbiguous"])
        self.assertEqual(1, backend.create_calls)
        state = context.checkpoints[EXTROPIC_CHECKPOINT_KEY].content
        self.assertEqual("creating", state["phase"])
        self.assertIsNone(state["providerJobId"])

    def test_capacity_retry_starts_the_same_uploaded_job(self) -> None:
        backend = FakeExtropicBackend()
        backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot("extropic-job-1", "succeeded"),
        ]
        backend.start_effects = [
            ExtropicAtCapacityError("extropic-job-1"),
            ExtropicJobSnapshot("extropic-job-1", "running"),
        ]
        adapter = adapter_for(backend)
        context = MemoryRunContext({"seed": 7})

        result = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("succeeded", result.status)
        self.assertEqual(2, backend.start_calls)
        self.assertEqual(1, backend.create_calls)

    def test_vyral_cancellation_is_forwarded_to_extropic(self) -> None:
        backend = FakeExtropicBackend()
        backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading")
        ]
        backend.start_effects = [
            ExtropicJobSnapshot("extropic-job-1", "running")
        ]
        adapter = adapter_for(backend)
        context = MemoryRunContext(
            {"seed": 7}, cancel_on_status="running"
        )

        result = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("cancelled", result.status)
        self.assertEqual(1, backend.cancel_calls)
        self.assertTrue(
            any(event[0] == "extropic.cancellation.requested" for event in context.events)
        )

    def test_already_cancelled_run_never_reserves_provider_compute(self) -> None:
        backend = FakeExtropicBackend()
        adapter = adapter_for(backend)
        context = MemoryRunContext(
            {"seed": 7}, cancellation_requested=True
        )

        result = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("cancelled", result.status)
        self.assertEqual(0, backend.create_calls)

    def test_cancellation_during_create_settles_and_cancels_returned_job(self) -> None:
        backend = FakeExtropicBackend()
        started = Event()
        release = Event()
        original_create = backend.create_job

        def blocked_create(tier: str) -> ExtropicCreatedJob:
            started.set()
            if not release.wait(timeout=2):
                raise AssertionError("test did not release provider create")
            return original_create(tier)

        backend.create_job = blocked_create  # type: ignore[method-assign]
        adapter = adapter_for(backend)
        context = MemoryRunContext({"seed": 7})

        async def scenario() -> None:
            task = asyncio.create_task(adapter.execute(cast(Any, context)))
            self.assertTrue(await asyncio.to_thread(started.wait, 2))
            task.cancel()
            release.set()
            with self.assertRaises(asyncio.CancelledError):
                await task

        asyncio.run(scenario())

        self.assertEqual(1, backend.create_calls)
        self.assertEqual(1, backend.cancel_calls)
        self.assertEqual(
            "creating",
            context.checkpoints[EXTROPIC_CHECKPOINT_KEY].content["phase"],
        )

    def test_non_json_result_is_rejected_without_exposing_repr(self) -> None:
        backend = FakeExtropicBackend()
        backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot("extropic-job-1", "succeeded"),
        ]
        backend.start_effects = [
            ExtropicJobSnapshot("extropic-job-1", "running")
        ]
        backend.result_value = SimpleNamespace(secret="do-not-leak")
        adapter = adapter_for(backend)
        context = MemoryRunContext({"seed": 7})

        result = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("failed", result.status)
        self.assertEqual("validation", result.failure_class)
        self.assertNotIn("do-not-leak", result.error)

    def test_provider_terminal_states_map_to_portable_results(self) -> None:
        expected = {
            "timeout": ("timed_out", "timeout"),
            "failed": ("failed", "unknown"),
            "out_of_credits": ("failed", "platform"),
            "preempted": ("failed", "transient"),
            "expired": ("failed", "platform"),
            "error": ("failed", "platform"),
            "at_capacity": ("failed", "transient"),
        }
        for provider_status, portable in expected.items():
            with self.subTest(provider_status=provider_status):
                backend = FakeExtropicBackend()
                backend.inspect_effects = [
                    ExtropicJobSnapshot("extropic-job-1", "uploading"),
                    ExtropicJobSnapshot("extropic-job-1", provider_status),
                ]
                backend.start_effects = [
                    ExtropicJobSnapshot("extropic-job-1", "running")
                ]
                adapter = adapter_for(backend)
                result = asyncio.run(
                    adapter.execute(cast(Any, MemoryRunContext({"seed": 7})))
                )
                self.assertEqual(portable[0], result.status)
                self.assertEqual(portable[1], result.failure_class)

    def test_upload_and_result_failures_retain_safe_provider_identity(self) -> None:
        upload_backend = FakeExtropicBackend()
        upload_backend.upload_error = ExtropicProviderError("private upload detail")
        upload_result = asyncio.run(
            adapter_for(upload_backend).execute(
                cast(Any, MemoryRunContext({"seed": 7}))
            )
        )
        self.assertEqual("transient", upload_result.failure_class)
        self.assertEqual(
            "extropic-job-1", upload_result.status_details["providerJobId"]
        )
        self.assertNotIn("private upload detail", upload_result.error)

        result_backend = FakeExtropicBackend()
        result_backend.inspect_effects = [
            ExtropicJobSnapshot("extropic-job-1", "uploading"),
            ExtropicJobSnapshot("extropic-job-1", "succeeded"),
        ]
        result_backend.start_effects = [
            ExtropicJobSnapshot("extropic-job-1", "running")
        ]
        result_backend.result_value = ExtropicProviderError("private result detail")
        result_backend.artifact_error = ExtropicProviderError("private artifact detail")
        result = asyncio.run(
            adapter_for(result_backend).execute(
                cast(Any, MemoryRunContext({"seed": 7}))
            )
        )
        self.assertEqual("transient", result.failure_class)
        self.assertNotIn("private", result.error)

    def test_reproducible_workload_can_require_explicit_seed(self) -> None:
        backend = FakeExtropicBackend()
        adapter = adapter_for(
            backend,
            options=ExtropicAdapterOptions(
                poll_interval_seconds=0.001,
                require_seed=True,
            ),
        )
        context = MemoryRunContext({"samples": 32})

        result = asyncio.run(adapter.execute(cast(Any, context)))

        self.assertEqual("failed", result.status)
        self.assertEqual("validation", result.failure_class)
        self.assertEqual(0, backend.create_calls)


if __name__ == "__main__":
    unittest.main()
