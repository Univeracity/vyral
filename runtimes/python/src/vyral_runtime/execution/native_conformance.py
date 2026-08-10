from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import timedelta
from pathlib import Path
import tempfile
from typing import Any, Awaitable, Callable, Mapping, cast

from ..conformance import ConformanceError, load_conformance_manifest
from ..contracts import JSONValue
from .local_runtime import (
    ExecutionRuntimeConflictError,
    LocalExecutionRuntime,
)
from .models import (
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionRunResult,
    ExecutionRunUpdate,
)
from .native_models import (
    ExecutionExternalEventRequest,
    ExecutionLeaseRequest,
    ExecutionMaintenanceDispatchReconcileRequest,
    ExecutionRunRequest,
    ExecutionTimerRequest,
)
from .worker import DelegateExecutionHandler


_SCENARIO_ID = "execution.native-lifecycle.v1"


@dataclass(frozen=True)
class NativeExecutionConformanceResult:
    scenario_id: str
    step_id: str
    operation: str
    value: JSONValue


def _object(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ConformanceError(f"{name} must be an object.")
    return cast(Mapping[str, Any], value)


def _text(value: object, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ConformanceError(f"{name} must be a non-empty string.")
    return value


class _Runner:
    def __init__(self, database_path: Path) -> None:
        self.database_path = database_path
        self.retry_attempts = 0
        self.failure_attempts = 0
        self.runtime = self._create_runtime()

    def _create_runtime(self) -> LocalExecutionRuntime:
        runtime = LocalExecutionRuntime(self.database_path)

        async def success(context: Any) -> ExecutionRunResult:
            payload = cast(
                Mapping[str, Any], context.run.payload
            )
            raw_items = payload.get("items")
            if not isinstance(raw_items, list):
                raise ValueError("Portable items must be an array.")
            total = sum(
                item
                for item in raw_items
                if isinstance(item, int)
                and not isinstance(item, bool)
            )
            await context.report(
                ExecutionRunUpdate(
                    requested=len(raw_items),
                    attempted=len(raw_items),
                    succeeded=len(raw_items),
                    failed=0,
                    progress=0.5,
                    current_step="persist",
                )
            )
            await context.put_checkpoint(
                ExecutionCheckpointWrite(
                    "progress", {"total": total}
                )
            )
            await context.put_artifact(
                ExecutionArtifactWrite(
                    "summary", content={"total": total}
                )
            )
            return ExecutionRunResult.succeeded_result(
                {"total": total}
            )

        async def retry(_: Any) -> ExecutionRunResult:
            self.retry_attempts += 1
            if self.retry_attempts == 1:
                return ExecutionRunResult.failed_result(
                    "transient", "portable retry"
                )
            return ExecutionRunResult.succeeded_result(
                {"attempts": self.retry_attempts}
            )

        async def failure(_: Any) -> ExecutionRunResult:
            self.failure_attempts += 1
            return ExecutionRunResult.failed_result(
                "validation", "portable validation failure"
            )

        async def wait(context: Any) -> ExecutionRunResult:
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
            approved = (
                bool(outcome.event.payload.get("approved"))
                if outcome.event is not None
                and isinstance(outcome.event.payload, dict)
                else False
            )
            return ExecutionRunResult.succeeded_result(
                {
                    "calls": calls,
                    "outcome": outcome.outcome,
                    "approved": approved,
                }
            )

        for descriptor, callback in (
            (
                ExecutionHandlerDescriptor(
                    "portable.success",
                    "Portable success",
                    plugin_id="portable",
                    max_attempts=2,
                ),
                success,
            ),
            (
                ExecutionHandlerDescriptor(
                    "portable.retry",
                    "Portable retry",
                    plugin_id="portable",
                    max_attempts=2,
                ),
                retry,
            ),
            (
                ExecutionHandlerDescriptor(
                    "portable.failure",
                    "Portable failure",
                    plugin_id="portable",
                    max_attempts=1,
                ),
                failure,
            ),
            (
                ExecutionHandlerDescriptor(
                    "portable.wait",
                    "Portable wait",
                    plugin_id="portable",
                    max_attempts=2,
                ),
                wait,
            ),
        ):
            runtime.register_handler(
                DelegateExecutionHandler(descriptor, callback)
            )
        return runtime

    async def execute(
        self, operation: str, arguments: Mapping[str, Any]
    ) -> JSONValue:
        handlers: dict[
            str, Callable[[Mapping[str, Any]], Awaitable[JSONValue]]
        ] = {
            "execution.native-success": self._success,
            "execution.native-rejections": self._rejections,
            "execution.native-retry": self._retry,
            "execution.native-failure": self._failure,
            "execution.native-cancel": self._cancel,
            "execution.native-wait-restart": self._wait_restart,
            "execution.native-coordination": self._coordination,
        }
        handler = handlers.get(operation)
        if handler is None:
            raise ConformanceError(
                f"Unsupported native execution operation "
                f"{operation!r}."
            )
        return await handler(arguments)

    async def _success(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        request = ExecutionRunRequest.from_value(
            _object(arguments.get("request"), "arguments.request")
        )
        first = await self.runtime.start_run(request)
        replay = await self.runtime.start_run(request)
        await self.runtime.dispatch_ready_runs()
        final = await self.runtime.get_run(first.id)
        if final is None:
            raise ConformanceError("Portable success run disappeared.")
        artifacts = await self.runtime.list_artifacts(first.id)
        checkpoint = await self.runtime.get_checkpoint(
            first.id, "progress"
        )
        history = await self.runtime.get_history(first.id)
        required = {
            "run.created",
            "run.started",
            "run.status",
            "artifact.written",
            "checkpoint.written",
            "run.completed",
        }
        result = cast(Mapping[str, Any], final.result)
        content = (
            cast(Mapping[str, Any], checkpoint.content)
            if checkpoint is not None
            else {}
        )
        return {
            "createdStatus": first.status,
            "replaySame": replay.id == first.id,
            "admissionVersion": first.admission.get("version"),
            "admissionStatus": first.admission.get("status"),
            "admissionIdStable": (
                first.admission.get("admissionId")
                == replay.admission.get("admissionId")
            ),
            "replayMarked": replay.admission.get("replayed"),
            "idempotencyKeyHash": first.admission.get(
                "idempotencyKeyHash"
            ),
            "admissionContainsRawKey": (
                request.idempotency_key in str(first.admission)
                if request.idempotency_key is not None
                else False
            ),
            "finalStatus": final.status,
            "attempt": final.attempt,
            "progress": final.progress,
            "resultTotal": result.get("total"),
            "artifactCount": len(artifacts),
            "checkpointTotal": content.get("total"),
            "requiredHistoryPassed": required.issubset(
                {item.type for item in history}
            ),
        }

    async def _rejections(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        missing = await self.runtime.start_run(
            ExecutionRunRequest("portable.missing")
        )
        mismatch = await self.runtime.start_run(
            ExecutionRunRequest(
                "portable.success", plugin_id="other"
            )
        )
        rejected = False
        try:
            await self.runtime.start_run(
                ExecutionRunRequest.from_value(
                    _object(
                        arguments.get("conflictingRequest"),
                        "arguments.conflictingRequest",
                    )
                )
            )
        except ExecutionRuntimeConflictError:
            rejected = True
        return {
            "missingStatus": missing.status,
            "missingFailureClass": missing.failure_class,
            "pluginStatus": mismatch.status,
            "pluginFailureClass": mismatch.failure_class,
            "idempotencyConflictRejected": rejected,
        }

    async def _retry(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        run = await self.runtime.start_run(
            ExecutionRunRequest.from_value(
                _object(
                    arguments.get("request"), "arguments.request"
                )
            )
        )
        await self.runtime.dispatch_ready_runs()
        await self.runtime.dispatch_ready_runs()
        final = await self.runtime.get_run(run.id)
        if final is None:
            raise ConformanceError("Portable retry run disappeared.")
        history = await self.runtime.get_history(run.id)
        return {
            "finalStatus": final.status,
            "attempt": final.attempt,
            "handlerAttempts": self.retry_attempts,
            "retryScheduled": any(
                item.type == "retry.scheduled" for item in history
            ),
        }

    async def _wait_restart(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        run = await self.runtime.start_run(
            ExecutionRunRequest.from_value(
                _object(
                    arguments.get("request"), "arguments.request"
                )
            )
        )
        await self.runtime.dispatch_ready_runs()
        suspended = await self.runtime.get_run(run.id)
        if suspended is None:
            raise ConformanceError("Portable wait run disappeared.")
        self.runtime = self._create_runtime()
        event = _object(arguments.get("event"), "arguments.event")
        await self.runtime.raise_event(
            ExecutionExternalEventRequest(
                name=_text(event.get("name"), "event.name"),
                run_id=run.id,
                payload=cast(JSONValue, event.get("payload")),
            )
        )
        await self.runtime.dispatch_ready_runs(
            recover_interrupted_runs=True
        )
        final = await self.runtime.get_run(run.id)
        checkpoint = await self.runtime.get_checkpoint(
            run.id, "calls"
        )
        history = await self.runtime.get_history(run.id)
        if final is None or checkpoint is None:
            raise ConformanceError(
                "Portable wait state disappeared."
            )
        result = cast(Mapping[str, Any], final.result)
        content = cast(Mapping[str, Any], checkpoint.content)
        types = {item.type for item in history}
        return {
            "suspendedStatus": suspended.status,
            "finalStatus": final.status,
            "attempt": final.attempt,
            "checkpointCalls": content.get("calls"),
            "outcome": result.get("outcome"),
            "approved": result.get("approved"),
            "waitRegistered": "wait.registered" in types,
            "waitResumed": "wait.resumed" in types,
        }

    async def _failure(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        run = await self.runtime.start_run(
            ExecutionRunRequest.from_value(
                _object(
                    arguments.get("request"), "arguments.request"
                )
            )
        )
        await self.runtime.dispatch_ready_runs()
        final = await self.runtime.get_run(run.id)
        if final is None:
            raise ConformanceError("Portable failure run disappeared.")
        history = await self.runtime.get_history(run.id)
        return {
            "finalStatus": final.status,
            "failureClass": final.failure_class,
            "attempt": final.attempt,
            "handlerAttempts": self.failure_attempts,
            "retryScheduled": any(
                item.type == "retry.scheduled" for item in history
            ),
        }

    async def _cancel(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        delay = arguments.get("scheduleDelaySeconds")
        if isinstance(delay, bool) or not isinstance(delay, int) or delay <= 0:
            raise ConformanceError(
                "arguments.scheduleDelaySeconds must be a positive integer."
            )
        request = replace(
            ExecutionRunRequest.from_value(
                _object(
                    arguments.get("request"), "arguments.request"
                )
            ),
            scheduled_at_utc=self.runtime._now()
            + timedelta(seconds=delay),
        )
        run = await self.runtime.start_run(request)
        cancelled = await self.runtime.cancel_run(run.id)
        stable = await self.runtime.cancel_run(run.id)
        history = await self.runtime.get_history(run.id)
        if cancelled is None or stable is None:
            raise ConformanceError("Portable cancellation run disappeared.")
        return {
            "createdStatus": run.status,
            "finalStatus": cancelled.status,
            "failureClass": cancelled.failure_class,
            "cancellationRequested": cancelled.cancellation_requested,
            "terminalCancelStable": stable.status == "cancelled",
            "cancellationRecorded": any(
                item.type == "run.cancellation_requested"
                for item in history
            ),
        }

    async def _coordination(
        self, arguments: Mapping[str, Any]
    ) -> JSONValue:
        lease_key = _text(
            arguments.get("leaseKey"), "arguments.leaseKey"
        )
        first = await self.runtime.try_acquire_lease(
            ExecutionLeaseRequest(lease_key, "owner-a")
        )
        conflict = await self.runtime.try_acquire_lease(
            ExecutionLeaseRequest(lease_key, "owner-b")
        )
        wrong = await self.runtime.release_lease(
            lease_key, "owner-b"
        )
        released = await self.runtime.release_lease(
            lease_key, "owner-a"
        )
        timer = await self.runtime.schedule_timer(
            ExecutionTimerRequest(
                name=_text(
                    arguments.get("timerName"),
                    "arguments.timerName",
                ),
                fire_at_utc=self.runtime._now()
                + timedelta(minutes=1),
            )
        )
        event = await self.runtime.raise_event(
            ExecutionExternalEventRequest(
                name=_text(
                    arguments.get("eventName"),
                    "arguments.eventName",
                )
            )
        )
        status = await self.runtime.get_maintenance_status()
        reconcile = await self.runtime.reconcile_dispatch(
            ExecutionMaintenanceDispatchReconcileRequest(
                dry_run=True
            )
        )
        return {
            "firstLeaseAcquired": first is not None,
            "conflictingLeaseRejected": conflict is None,
            "wrongOwnerRelease": wrong,
            "ownerRelease": released,
            "timerName": timer.name,
            "eventName": event.name,
            "maintenanceHealthy": (
                status.row_counts.get("runs", 0) >= 1
                and status.retention_scope == "run_owned"
            ),
            "reconcileDryRun": reconcile.dry_run,
        }


async def arun_bundled_native_execution_scenario(
    fixture_root: str | Path | None = None,
) -> tuple[NativeExecutionConformanceResult, ...]:
    manifest = load_conformance_manifest(fixture_root)
    descriptor = next(
        (
            item
            for item in manifest.scenarios
            if item.scenario_id == _SCENARIO_ID
        ),
        None,
    )
    if descriptor is None:
        raise ConformanceError(
            f"Conformance scenario {_SCENARIO_ID!r} is unavailable."
        )
    scenario = manifest.scenario(descriptor)
    raw_steps = scenario.get("steps")
    if not isinstance(raw_steps, list):
        raise ConformanceError(
            "Native execution scenario steps must be an array."
        )
    with tempfile.TemporaryDirectory(
        prefix="vyral-native-execution-conformance-"
    ) as temporary:
        runner = _Runner(
            Path(temporary) / "native-execution.sqlite"
        )
        results: list[NativeExecutionConformanceResult] = []
        for raw_step in raw_steps:
            step = _object(raw_step, "scenario.steps[]")
            step_id = _text(step.get("id"), "step.id")
            operation = _text(
                step.get("operation"), "step.operation"
            )
            expectation = _object(
                step.get("expect"), "step.expect"
            )
            actual = await runner.execute(
                operation,
                _object(
                    step.get("arguments"), "step.arguments"
                ),
            )
            expected = cast(JSONValue, expectation.get("value"))
            if actual != expected:
                raise ConformanceError(
                    f"Native execution step {step_id!r} produced "
                    f"{actual!r}, expected {expected!r}."
                )
            results.append(
                NativeExecutionConformanceResult(
                    descriptor.scenario_id,
                    step_id,
                    operation,
                    actual,
                )
            )
    return tuple(results)


def run_bundled_native_execution_scenario(
    fixture_root: str | Path | None = None,
) -> tuple[NativeExecutionConformanceResult, ...]:
    import asyncio

    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return asyncio.run(
            arun_bundled_native_execution_scenario(fixture_root)
        )
    from concurrent.futures import ThreadPoolExecutor

    with ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="vyral-native-conformance",
    ) as executor:
        return executor.submit(
            lambda: asyncio.run(
                arun_bundled_native_execution_scenario(
                    fixture_root
                )
            )
        ).result()


__all__ = [
    "NativeExecutionConformanceResult",
    "arun_bundled_native_execution_scenario",
    "run_bundled_native_execution_scenario",
]
