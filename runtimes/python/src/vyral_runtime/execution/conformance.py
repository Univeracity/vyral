from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, cast

from ..conformance import ConformanceError, load_conformance_manifest
from ..contracts import JSONValue
from .harness import InMemoryExecutionWorkerTransport
from .models import (
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionRunResult,
    ExecutionRunUpdate,
)
from .worker import (
    DelegateExecutionHandler,
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    ExecutionRunContext,
    StaticExecutionPlugin,
)


_SCENARIO_ID = "external-workers.handler-lifecycle.v1"
_NOW = datetime(2026, 7, 30, 16, 0, tzinfo=timezone.utc)


@dataclass(frozen=True)
class ExternalWorkerConformanceResult:
    scenario_id: str
    step_id: str
    operation: str
    value: JSONValue


def _object(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ConformanceError(f"{name} must be an object.")
    return value


def _text(value: object, name: str) -> str:
    if not isinstance(value, str) or not value:
        raise ConformanceError(f"{name} must be a non-empty string.")
    return value


def _plugin(
    plugin_id: str,
    handler_id: str,
    execute: Any,
) -> StaticExecutionPlugin:
    handler = DelegateExecutionHandler(
        ExecutionHandlerDescriptor(
            handler_id=handler_id,
            plugin_id=plugin_id,
            display_name=handler_id,
        ),
        execute,
    )
    return StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=plugin_id,
            name=plugin_id,
            version="1.0.0",
        ),
        (handler,),
    )


async def _success(arguments: Mapping[str, Any]) -> JSONValue:
    plugin_id = _text(arguments.get("pluginId"), "arguments.pluginId")
    handler_id = _text(arguments.get("handlerId"), "arguments.handlerId")
    payload = cast(JSONValue, arguments.get("payload"))

    async def execute(context: ExecutionRunContext) -> ExecutionRunResult:
        await context.put_checkpoint(
            ExecutionCheckpointWrite(
                key="state",
                content={"ready": True},
            )
        )
        await context.report(
            ExecutionRunUpdate(progress=0.5, current_step="work")
        )
        await context.record_event("step.completed", message="done")
        await context.put_artifact(
            ExecutionArtifactWrite(
                name="summary",
                content={"ok": True},
            )
        )
        return ExecutionRunResult.succeeded_result(context.run.payload)

    plugin = _plugin(plugin_id, handler_id, execute)
    transport = InMemoryExecutionWorkerTransport(
        "portable-worker",
        (handler_id,),
        clock=lambda: _NOW,
        token_factory=lambda: "portable-lease-token",
    )
    accepted = await transport.enqueue_run(
        handler_id,
        plugin_id=plugin_id,
        payload=payload,
        run_id="portable-success-run",
    )
    worker = ExecutionPluginWorker(
        transport,
        (plugin,),
        ExecutionPluginWorkerOptions(heartbeat_interval_seconds=None),
    )
    completed = await worker.run_once(accepted.id)
    if completed is None:
        raise ConformanceError("Portable success handler was not leased.")
    artifacts = await transport.get_artifacts(completed.id)
    checkpoints = await transport.get_checkpoints(completed.id)
    events = await transport.get_events(completed.id)
    return {
        "status": completed.status,
        "result": completed.result,
        "progress": completed.progress,
        "currentStep": completed.current_step,
        "artifactNames": cast(
            JSONValue, sorted(item.name for item in artifacts)
        ),
        "checkpointKeys": cast(
            JSONValue, sorted(item.key for item in checkpoints)
        ),
        "eventTypes": cast(
            JSONValue,
            sorted(
                item.type
                for item in events
                if item.type == "step.completed"
            ),
        ),
        "completionCount": transport.completion_count,
    }


async def _wait_replay(arguments: Mapping[str, Any]) -> JSONValue:
    plugin_id = _text(arguments.get("pluginId"), "arguments.pluginId")
    handler_id = _text(arguments.get("handlerId"), "arguments.handlerId")
    event_name = _text(arguments.get("eventName"), "arguments.eventName")
    event_payload = cast(JSONValue, arguments.get("eventPayload"))

    async def execute(context: ExecutionRunContext) -> ExecutionRunResult:
        if await context.get_checkpoint("before-wait") is None:
            await context.put_checkpoint(
                ExecutionCheckpointWrite(
                    key="before-wait",
                    content={"ready": True},
                )
            )
        outcome = await context.wait_for_external_event(event_name)
        approved = bool(
            outcome.event
            and isinstance(outcome.event.payload, Mapping)
            and outcome.event.payload.get("approved") is True
        )
        await context.put_artifact(
            ExecutionArtifactWrite(
                name="approval-summary",
                content={"approved": approved},
            )
        )
        return ExecutionRunResult.succeeded_result(
            {"approved": approved}
        )

    plugin = _plugin(plugin_id, handler_id, execute)
    transport = InMemoryExecutionWorkerTransport(
        "portable-worker",
        (handler_id,),
        clock=lambda: _NOW,
        token_factory=lambda: "portable-lease-token",
    )
    accepted = await transport.enqueue_run(
        handler_id,
        plugin_id=plugin_id,
        run_id="portable-wait-run",
    )
    worker = ExecutionPluginWorker(
        transport,
        (plugin,),
        ExecutionPluginWorkerOptions(heartbeat_interval_seconds=None),
    )
    waiting = await worker.run_once(accepted.id)
    if waiting is None:
        raise ConformanceError("Portable wait handler was not leased.")
    await transport.raise_external_event(
        accepted.id,
        event_name,
        event_payload,
    )
    completed = await worker.run_once(accepted.id)
    if completed is None:
        raise ConformanceError("Portable wait replay was not leased.")
    artifacts = await transport.get_artifacts(completed.id)
    checkpoints = await transport.get_checkpoints(completed.id)
    return {
        "waitingStatus": waiting.status,
        "status": completed.status,
        "result": completed.result,
        "artifactNames": cast(
            JSONValue, sorted(item.name for item in artifacts)
        ),
        "checkpointKeys": cast(
            JSONValue, sorted(item.key for item in checkpoints)
        ),
        "completionCount": transport.completion_count,
    }


async def _failure(arguments: Mapping[str, Any]) -> JSONValue:
    plugin_id = _text(arguments.get("pluginId"), "arguments.pluginId")
    handler_id = _text(arguments.get("handlerId"), "arguments.handlerId")
    secret = _text(
        arguments.get("secretMessage"), "arguments.secretMessage"
    )

    def execute(_: ExecutionRunContext) -> ExecutionRunResult:
        raise RuntimeError(secret)

    plugin = _plugin(plugin_id, handler_id, execute)
    transport = InMemoryExecutionWorkerTransport(
        "portable-worker",
        (handler_id,),
        clock=lambda: _NOW,
        token_factory=lambda: "portable-lease-token",
    )
    accepted = await transport.enqueue_run(
        handler_id,
        plugin_id=plugin_id,
        run_id="portable-failure-run",
    )
    worker = ExecutionPluginWorker(
        transport,
        (plugin,),
        ExecutionPluginWorkerOptions(heartbeat_interval_seconds=None),
    )
    completed = await worker.run_once(accepted.id)
    if completed is None:
        raise ConformanceError("Portable failure handler was not leased.")
    return {
        "status": completed.status,
        "failureClass": completed.failure_class,
        "error": completed.error,
        "completionCount": transport.completion_count,
    }


_OPERATIONS = {
    "external-worker.handler-success": _success,
    "external-worker.durable-wait-replay": _wait_replay,
    "external-worker.handler-failure": _failure,
}


async def _run(
    fixture_root: str | Path | None,
) -> tuple[ExternalWorkerConformanceResult, ...]:
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
    if descriptor.kind != "stateful":
        raise ConformanceError(
            f"Conformance scenario {_SCENARIO_ID!r} must be stateful."
        )
    scenario = manifest.scenario(descriptor)
    raw_steps = scenario.get("steps")
    if not isinstance(raw_steps, list):
        raise ConformanceError("External-worker scenario steps must be an array.")
    results: list[ExternalWorkerConformanceResult] = []
    for raw_step in raw_steps:
        step = _object(raw_step, "scenario.steps[]")
        step_id = _text(step.get("id"), "step.id")
        operation = _text(step.get("operation"), "step.operation")
        arguments = _object(step.get("arguments"), "step.arguments")
        expectation = _object(step.get("expect"), "step.expect")
        if "value" not in expectation:
            raise ConformanceError(
                "External-worker error expectations are not implemented."
            )
        handler = _OPERATIONS.get(operation)
        if handler is None:
            raise ConformanceError(
                f"Unsupported external-worker operation {operation!r}."
            )
        actual = await handler(arguments)
        expected = cast(JSONValue, expectation["value"])
        if actual != expected:
            raise ConformanceError(
                f"External-worker step {step_id!r} produced {actual!r}, "
                f"expected {expected!r}."
            )
        results.append(
            ExternalWorkerConformanceResult(
                scenario_id=descriptor.scenario_id,
                step_id=step_id,
                operation=operation,
                value=actual,
            )
        )
    return tuple(results)


def run_bundled_external_worker_scenario(
    fixture_root: str | Path | None = None,
) -> tuple[ExternalWorkerConformanceResult, ...]:
    """Run the portable handler lifecycle against the Python test runtime."""

    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return asyncio.run(_run(fixture_root))
    raise RuntimeError(
        "The synchronous external-worker conformance runner cannot run inside "
        "an active event loop."
    )


async def arun_bundled_external_worker_scenario(
    fixture_root: str | Path | None = None,
) -> tuple[ExternalWorkerConformanceResult, ...]:
    return await _run(fixture_root)
