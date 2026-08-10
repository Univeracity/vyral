from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable, Iterable
from dataclasses import dataclass, replace
import inspect
from typing import Any, Mapping, Protocol

from ..contracts import JSONValue
from ..local.models import JSONObject
from .models import (
    MAX_LEASE_TTL_SECONDS,
    ExecutionArtifact,
    ExecutionArtifactWrite,
    ExecutionCheckpoint,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionRun,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionWaitResult,
    ExecutionWorkerLease,
    ExecutionWorkerWaitRequest,
    ExecutionWorkerWaitResponse,
    validate_run_result,
)


HANDLER_FAILURE_MESSAGE = "External plugin handler failed."


class ExecutionWorkerTransport(Protocol):
    async def lease_next(
        self,
        run_id: str | None = None,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease | None: ...

    async def heartbeat(
        self,
        lease: ExecutionWorkerLease,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease: ...

    async def checkpoint(
        self,
        lease: ExecutionWorkerLease,
        checkpoint: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint: ...

    async def get_checkpoint(
        self,
        lease: ExecutionWorkerLease,
        key: str,
    ) -> ExecutionCheckpoint | None: ...

    async def report(
        self,
        lease: ExecutionWorkerLease,
        update: ExecutionRunUpdate,
    ) -> ExecutionRun: ...

    async def record_event(
        self,
        lease: ExecutionWorkerLease,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None: ...

    async def put_artifact(
        self,
        lease: ExecutionWorkerLease,
        artifact: ExecutionArtifactWrite,
    ) -> ExecutionArtifact: ...

    async def wait(
        self,
        lease: ExecutionWorkerLease,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWorkerWaitResponse: ...

    async def complete(
        self,
        lease: ExecutionWorkerLease,
        result: ExecutionRunResult,
    ) -> ExecutionRun: ...


class ExecutionRunContext(Protocol):
    @property
    def run(self) -> ExecutionRun: ...

    @property
    def cancellation_requested(self) -> bool: ...

    async def report(
        self,
        update: ExecutionRunUpdate | Mapping[str, Any],
    ) -> ExecutionRun: ...

    async def record_event(
        self,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None: ...

    async def put_artifact(
        self,
        artifact: ExecutionArtifactWrite | Mapping[str, Any],
    ) -> ExecutionArtifact: ...

    async def put_checkpoint(
        self,
        checkpoint: ExecutionCheckpointWrite | Mapping[str, Any],
    ) -> ExecutionCheckpoint: ...

    async def get_checkpoint(
        self, key: str
    ) -> ExecutionCheckpoint | None: ...

    async def wait_for_external_event(
        self,
        name: str,
        *,
        timeout_at_utc: Any = None,
    ) -> ExecutionWaitResult: ...

    async def wait_for_timer(
        self,
        name: str,
        fire_at_utc: Any,
        *,
        payload: JSONValue = None,
    ) -> ExecutionWaitResult: ...


class ExecutionHandler(Protocol):
    @property
    def descriptor(self) -> ExecutionHandlerDescriptor: ...

    async def execute(
        self, context: ExecutionRunContext
    ) -> ExecutionRunResult: ...


HandlerCallable = Callable[
    [ExecutionRunContext],
    ExecutionRunResult
    | Mapping[str, Any]
    | Awaitable[ExecutionRunResult | Mapping[str, Any]],
]


class DelegateExecutionHandler:
    def __init__(
        self,
        descriptor: ExecutionHandlerDescriptor | Mapping[str, Any],
        execute: HandlerCallable,
    ) -> None:
        if not callable(execute):
            raise TypeError("Execution handler callback must be callable.")
        self._descriptor = ExecutionHandlerDescriptor.from_value(descriptor)
        self._execute = execute

    @property
    def descriptor(self) -> ExecutionHandlerDescriptor:
        return self._descriptor

    async def execute(
        self, context: ExecutionRunContext
    ) -> ExecutionRunResult:
        result = self._execute(context)
        if inspect.isawaitable(result):
            result = await result
        return ExecutionRunResult.from_value(result)

    async def __call__(
        self, context: ExecutionRunContext
    ) -> ExecutionRunResult:
        """Invoke the normalized handler directly in tests and composition."""

        return await self.execute(context)


@dataclass(frozen=True)
class StaticExecutionPlugin:
    descriptor: ExecutionPluginDescriptor
    handlers: tuple[ExecutionHandler, ...]

    def __init__(
        self,
        descriptor: ExecutionPluginDescriptor | Mapping[str, Any],
        handlers: Iterable[ExecutionHandler],
    ) -> None:
        selected_handlers = tuple(handlers)
        if not selected_handlers:
            raise ValueError("At least one execution plugin handler is required.")
        normalized_descriptor = ExecutionPluginDescriptor.from_value(descriptor)
        handler_descriptors: list[ExecutionHandlerDescriptor] = []
        seen: set[str] = set()
        for handler in selected_handlers:
            selected = ExecutionHandlerDescriptor.from_value(handler.descriptor)
            if selected.plugin_id not in {
                None,
                normalized_descriptor.plugin_id,
            }:
                raise ValueError(
                    f"Handler {selected.handler_id!r} does not belong to plugin "
                    f"{normalized_descriptor.plugin_id!r}."
                )
            if selected.handler_id in seen:
                raise ValueError(
                    f"Plugin {normalized_descriptor.plugin_id!r} repeats "
                    f"handler {selected.handler_id!r}."
                )
            seen.add(selected.handler_id)
            handler_descriptors.append(
                replace(
                    selected,
                    plugin_id=normalized_descriptor.plugin_id,
                )
            )
        if normalized_descriptor.handlers:
            declared = {
                handler.handler_id: handler
                for handler in normalized_descriptor.handlers
            }
            if set(declared) != seen:
                raise ValueError(
                    "Plugin descriptor handlers must match registered handlers."
                )
            handler_descriptors = [declared[item.handler_id] for item in handler_descriptors]
        else:
            normalized_descriptor = replace(
                normalized_descriptor,
                handlers=tuple(handler_descriptors),
            )
        object.__setattr__(self, "descriptor", normalized_descriptor)
        object.__setattr__(self, "handlers", selected_handlers)


@dataclass(frozen=True)
class ExecutionPluginWorkerOptions:
    lease_ttl_seconds: float = 60.0
    heartbeat_interval_seconds: float | None = 20.0
    idle_delay_seconds: float = 1.0

    def __post_init__(self) -> None:
        if not 0 < self.lease_ttl_seconds <= MAX_LEASE_TTL_SECONDS:
            raise ValueError(
                "Worker lease TTL must be greater than zero and no more than "
                f"{MAX_LEASE_TTL_SECONDS:g} seconds."
            )
        interval = self.heartbeat_interval_seconds
        if interval is not None and not (
            0 < interval < self.lease_ttl_seconds
        ):
            raise ValueError(
                "Worker heartbeat interval must be positive and shorter than "
                "the lease TTL."
            )
        if not 0 <= self.idle_delay_seconds <= 60:
            raise ValueError(
                "Worker idle delay must be between zero and 60 seconds."
            )


@dataclass
class _LeaseState:
    lease: ExecutionWorkerLease


class _WorkerSuspended(Exception):
    def __init__(self, run: ExecutionRun) -> None:
        super().__init__("External-worker execution suspended for a durable wait.")
        self.run = run


class _ExternalExecutionRunContext:
    def __init__(
        self,
        transport: ExecutionWorkerTransport,
        state: _LeaseState,
    ) -> None:
        self._transport = transport
        self._state = state

    @property
    def run(self) -> ExecutionRun:
        return self._state.lease.run

    @property
    def cancellation_requested(self) -> bool:
        return self.run.cancellation_requested

    async def report(
        self,
        update: ExecutionRunUpdate | Mapping[str, Any],
    ) -> ExecutionRun:
        run = await self._transport.report(
            self._state.lease,
            ExecutionRunUpdate.from_value(update),
        )
        self._update_run(run)
        return run

    async def record_event(
        self,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None:
        await self._transport.record_event(
            self._state.lease,
            event_type,
            message=message,
            severity=severity,
            details=details,
        )

    async def put_artifact(
        self,
        artifact: ExecutionArtifactWrite | Mapping[str, Any],
    ) -> ExecutionArtifact:
        return await self._transport.put_artifact(
            self._state.lease,
            ExecutionArtifactWrite.from_value(artifact),
        )

    async def put_checkpoint(
        self,
        checkpoint: ExecutionCheckpointWrite | Mapping[str, Any],
    ) -> ExecutionCheckpoint:
        return await self._transport.checkpoint(
            self._state.lease,
            ExecutionCheckpointWrite.from_value(checkpoint),
        )

    async def get_checkpoint(
        self, key: str
    ) -> ExecutionCheckpoint | None:
        selected = key.strip()
        if not selected:
            raise ValueError("Checkpoint key is required.")
        return await self._transport.get_checkpoint(
            self._state.lease,
            selected,
        )

    async def wait_for_external_event(
        self,
        name: str,
        *,
        timeout_at_utc: Any = None,
    ) -> ExecutionWaitResult:
        return await self._wait(
            ExecutionWorkerWaitRequest(
                kind="external_event",
                name=name,
                timeout_at_utc=timeout_at_utc,
            )
        )

    async def wait_for_timer(
        self,
        name: str,
        fire_at_utc: Any,
        *,
        payload: JSONValue = None,
    ) -> ExecutionWaitResult:
        return await self._wait(
            ExecutionWorkerWaitRequest(
                kind="timer",
                name=name,
                fire_at_utc=fire_at_utc,
                payload=payload,
            )
        )

    async def _wait(
        self,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWaitResult:
        response = await self._transport.wait(self._state.lease, request)
        self._update_run(response.run)
        if response.suspended:
            raise _WorkerSuspended(response.run)
        if response.outcome is None:
            raise RuntimeError(
                "External worker wait completed without an outcome."
            )
        return response.outcome

    def _update_run(self, run: ExecutionRun) -> None:
        self._state.lease = replace(self._state.lease, run=run)

    async def try_acquire_lease(self, *_: Any, **__: Any) -> None:
        raise NotImplementedError(
            "The external-worker protocol does not expose coordination leases."
        )

    async def release_lease(self, *_: Any, **__: Any) -> None:
        raise NotImplementedError(
            "The external-worker protocol does not expose coordination leases."
        )

    async def schedule_timer(self, *_: Any, **__: Any) -> None:
        raise NotImplementedError(
            "The external-worker protocol does not expose standalone timers."
        )

    async def raise_event(self, *_: Any, **__: Any) -> None:
        raise NotImplementedError(
            "The external-worker protocol does not expose raising events."
        )


class ExecutionPluginWorker:
    def __init__(
        self,
        transport: ExecutionWorkerTransport,
        plugins: Iterable[StaticExecutionPlugin],
        options: ExecutionPluginWorkerOptions | None = None,
    ) -> None:
        self.transport = transport
        self.options = options or ExecutionPluginWorkerOptions()
        handlers: dict[str, tuple[str, ExecutionHandler]] = {}
        for plugin in plugins:
            for handler in plugin.handlers:
                handler_id = handler.descriptor.handler_id
                if handler_id in handlers:
                    raise ValueError(
                        f"Execution handler {handler_id!r} is registered by "
                        "more than one worker plugin."
                    )
                handlers[handler_id] = (
                    plugin.descriptor.plugin_id,
                    handler,
                )
        if not handlers:
            raise ValueError(
                "At least one execution plugin handler is required."
            )
        self._handlers = handlers

    @property
    def handlers(self) -> tuple[ExecutionHandlerDescriptor, ...]:
        return tuple(
            sorted(
                (
                    handler.descriptor
                    for _, handler in self._handlers.values()
                ),
                key=lambda descriptor: descriptor.handler_id,
            )
        )

    async def run_once(self, run_id: str | None = None) -> ExecutionRun | None:
        lease = await self.transport.lease_next(
            run_id.strip() if run_id is not None and run_id.strip() else None,
            self.options.lease_ttl_seconds,
        )
        if lease is None:
            return None
        registration = self._handlers.get(lease.run.handler_id)
        if registration is None:
            return await self.transport.complete(
                lease,
                ExecutionRunResult.failed_result(
                    "handler_missing",
                    f"Execution handler {lease.run.handler_id!r} is not "
                    "registered in this worker.",
                ),
            )
        plugin_id, handler = registration
        if lease.run.plugin_id and lease.run.plugin_id != plugin_id:
            return await self.transport.complete(
                lease,
                ExecutionRunResult.failed_result(
                    "plugin_mismatch",
                    f"Execution handler {lease.run.handler_id!r} belongs to "
                    f"plugin {plugin_id!r}.",
                ),
            )
        return await self._execute(lease, handler)

    async def run(self) -> None:
        while True:
            result = await self.run_once()
            if result is None and self.options.idle_delay_seconds:
                await asyncio.sleep(self.options.idle_delay_seconds)

    async def _execute(
        self,
        lease: ExecutionWorkerLease,
        handler: ExecutionHandler,
    ) -> ExecutionRun:
        state = _LeaseState(lease)
        context = _ExternalExecutionRunContext(self.transport, state)
        handler_task = asyncio.create_task(handler.execute(context))
        heartbeat_task: asyncio.Task[bool] | None = None
        interval = self.options.heartbeat_interval_seconds
        if interval is not None:
            heartbeat_task = asyncio.create_task(
                self._heartbeat_loop(state, interval)
            )
        try:
            server_cancelled = False
            if heartbeat_task is None:
                result = await self._handler_result(handler_task, context)
            else:
                done, _ = await asyncio.wait(
                    {handler_task, heartbeat_task},
                    return_when=asyncio.FIRST_COMPLETED,
                )
                if heartbeat_task in done:
                    server_cancelled = await heartbeat_task
                    if not server_cancelled:
                        raise RuntimeError(
                            "External-worker heartbeat stopped unexpectedly."
                        )
                    handler_task.cancel()
                    try:
                        await handler_task
                    except asyncio.CancelledError:
                        pass
                    except Exception:
                        pass
                    result = ExecutionRunResult.cancelled_result(
                        context.run.result
                    )
                else:
                    result = await self._handler_result(handler_task, context)
            validate_run_result(result)
            if (
                heartbeat_task is not None
                and heartbeat_task.done()
                and not server_cancelled
            ):
                # Propagate a lease/transport failure before attempting a
                # terminal write. Completion must never be retried or
                # reinterpreted as a handler failure.
                await heartbeat_task
            return await self.transport.complete(state.lease, result)
        except _WorkerSuspended as suspended:
            return suspended.run
        except asyncio.CancelledError:
            handler_task.cancel()
            raise
        finally:
            if not handler_task.done():
                handler_task.cancel()
            if heartbeat_task is not None and not heartbeat_task.done():
                heartbeat_task.cancel()
            await _settle(handler_task)
            if heartbeat_task is not None:
                await _settle(heartbeat_task)

    async def _handler_result(
        self,
        task: asyncio.Task[ExecutionRunResult],
        context: _ExternalExecutionRunContext,
    ) -> ExecutionRunResult:
        try:
            result = await asyncio.shield(task)
            validate_run_result(result)
            return result
        except _WorkerSuspended:
            raise
        except asyncio.CancelledError:
            if not task.cancelled():
                raise
            return ExecutionRunResult.cancelled_result(context.run.result)
        except Exception:
            return ExecutionRunResult.failed_result(
                "unknown",
                HANDLER_FAILURE_MESSAGE,
            )

    async def _heartbeat_loop(
        self,
        state: _LeaseState,
        interval: float,
    ) -> bool:
        while True:
            await asyncio.sleep(interval)
            refreshed = await self.transport.heartbeat(
                state.lease,
                self.options.lease_ttl_seconds,
            )
            state.lease = refreshed
            if refreshed.run.cancellation_requested:
                return True


async def _settle(task: asyncio.Task[Any]) -> None:
    try:
        await task
    except (asyncio.CancelledError, Exception):
        pass
