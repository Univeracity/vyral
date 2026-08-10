from __future__ import annotations

import asyncio
from collections.abc import Callable, Iterable
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import json
from secrets import token_urlsafe
from typing import Any, Mapping

from ..contracts import JSONValue
from ..local.models import JSONObject
from .models import (
    MAX_ARTIFACT_BYTES,
    ExecutionArtifact,
    ExecutionArtifactWrite,
    ExecutionCheckpoint,
    ExecutionCheckpointWrite,
    ExecutionExternalEvent,
    ExecutionRun,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionTimer,
    ExecutionWaitResult,
    ExecutionWorkerLease,
    ExecutionWorkerWaitRequest,
    ExecutionWorkerWaitResponse,
)
from .worker import (
    ExecutionHandler,
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    StaticExecutionPlugin,
)


@dataclass(frozen=True)
class RecordedWorkerEvent:
    run_id: str
    type: str
    message: str | None
    severity: str
    details: JSONObject | None
    recorded_at_utc: datetime


class InMemoryExecutionWorkerTransport:
    """Deterministic external-worker runtime for handler tests.

    It models lease ownership, reports, checkpoints, artifacts, cancellation,
    and replayable durable waits without pretending to be durable process
    storage. Native durable execution arrives in Phase 4.
    """

    def __init__(
        self,
        worker_id: str,
        handler_ids: Iterable[str],
        *,
        clock: Callable[[], datetime] | None = None,
        token_factory: Callable[[], str] | None = None,
    ) -> None:
        selected_worker = worker_id.strip()
        if not selected_worker:
            raise ValueError("Execution worker id is required.")
        selected_handlers = frozenset(
            handler.strip() for handler in handler_ids if handler.strip()
        )
        if not selected_handlers:
            raise ValueError(
                "At least one execution worker handler id is required."
            )
        self.worker_id = selected_worker
        self.handler_ids = selected_handlers
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._token_factory = token_factory or (
            lambda: token_urlsafe(32)
        )
        self._lock = asyncio.Lock()
        self._runs: dict[str, ExecutionRun] = {}
        self._queue: list[str] = []
        self._leases: dict[str, ExecutionWorkerLease] = {}
        self._checkpoints: dict[tuple[str, str], ExecutionCheckpoint] = {}
        self._artifacts: dict[str, list[ExecutionArtifact]] = {}
        self._events: dict[str, list[RecordedWorkerEvent]] = {}
        self._wait_outcomes: dict[
            tuple[str, str, str], ExecutionWaitResult
        ] = {}
        self._waiting: dict[str, ExecutionWorkerWaitRequest] = {}
        self._sequence = 0
        self.heartbeat_count = 0
        self.completion_count = 0
        self.fail_completion = False

    async def enqueue_run(
        self,
        handler_id: str,
        *,
        plugin_id: str | None = None,
        payload: JSONValue = None,
        run_id: str | None = None,
        max_attempts: int = 1,
        correlation_id: str | None = None,
        tags: Mapping[str, str] | None = None,
    ) -> ExecutionRun:
        selected_handler = handler_id.strip()
        if not selected_handler:
            raise ValueError("Execution handler id is required.")
        now = self._utc()
        encoded = _canonical_bytes(payload)
        async with self._lock:
            selected_id = run_id.strip() if run_id and run_id.strip() else self._id("run")
            if selected_id in self._runs:
                raise ValueError(f"Execution run {selected_id!r} already exists.")
            run = ExecutionRun(
                id=selected_id,
                handler_id=selected_handler,
                plugin_id=plugin_id,
                status="queued",
                attempt=0,
                max_attempts=max_attempts,
                correlation_id=correlation_id or selected_id,
                payload_hash="sha256:" + sha256(encoded).hexdigest(),
                payload=payload,
                created_at_utc=now,
                updated_at_utc=now,
                cancellation_requested=False,
                tags=dict(tags or {}),
            )
            self._runs[run.id] = run
            self._queue.append(run.id)
            return run

    async def lease_next(
        self,
        run_id: str | None = None,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease | None:
        if ttl_seconds <= 0:
            raise ValueError("Worker lease TTL must be positive.")
        async with self._lock:
            selected_id: str | None = None
            if run_id is not None:
                candidate = self._runs.get(run_id)
                if (
                    candidate is not None
                    and candidate.status == "queued"
                    and candidate.handler_id in self.handler_ids
                ):
                    selected_id = run_id
            else:
                for candidate_id in self._queue:
                    candidate = self._runs[candidate_id]
                    if (
                        candidate.status == "queued"
                        and candidate.handler_id in self.handler_ids
                    ):
                        selected_id = candidate_id
                        break
            if selected_id is None:
                return None
            self._queue = [
                candidate for candidate in self._queue
                if candidate != selected_id
            ]
            current = self._runs[selected_id]
            now = self._utc()
            run = replace(
                current,
                status="running",
                attempt=current.attempt + 1,
                started_at_utc=current.started_at_utc or now,
                updated_at_utc=now,
            )
            self._runs[run.id] = run
            lease = ExecutionWorkerLease(
                lease_key=self._id("lease"),
                lease_token=self._token_factory(),
                worker_id=self.worker_id,
                run=run,
                acquired_at_utc=now,
                expires_at_utc=now + timedelta(seconds=ttl_seconds),
            )
            self._leases[lease.lease_key] = lease
            return lease

    async def heartbeat(
        self,
        lease: ExecutionWorkerLease,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease:
        async with self._lock:
            active = self._require_lease(lease)
            self.heartbeat_count += 1
            now = self._utc()
            refreshed = replace(
                active,
                run=self._runs[active.run.id],
                expires_at_utc=now + timedelta(seconds=ttl_seconds),
            )
            self._leases[active.lease_key] = refreshed
            return refreshed

    async def checkpoint(
        self,
        lease: ExecutionWorkerLease,
        checkpoint: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint:
        async with self._lock:
            active = self._require_lease(lease)
            content = _canonical_bytes(checkpoint.content)
            result = ExecutionCheckpoint(
                run_id=active.run.id,
                key=checkpoint.key,
                content_hash="sha256:" + sha256(content).hexdigest(),
                updated_at_utc=self._utc(),
                content=checkpoint.content,
                metadata=dict(checkpoint.metadata),
            )
            self._checkpoints[(active.run.id, checkpoint.key)] = result
            return result

    async def get_checkpoint(
        self,
        lease: ExecutionWorkerLease,
        key: str,
    ) -> ExecutionCheckpoint | None:
        async with self._lock:
            active = self._require_lease(lease)
            return self._checkpoints.get((active.run.id, key))

    async def report(
        self,
        lease: ExecutionWorkerLease,
        update: ExecutionRunUpdate,
    ) -> ExecutionRun:
        async with self._lock:
            active = self._require_lease(lease)
            current = self._runs[active.run.id]
            changes: dict[str, Any] = {"updated_at_utc": self._utc()}
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
            ):
                value = getattr(update, name)
                if value is not None:
                    changes[name] = value
            if update.result is not None:
                changes["result"] = update.result
            run = replace(current, **changes)
            self._runs[run.id] = run
            self._leases[active.lease_key] = replace(active, run=run)
            return run

    async def record_event(
        self,
        lease: ExecutionWorkerLease,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None:
        async with self._lock:
            active = self._require_lease(lease)
            self._events.setdefault(active.run.id, []).append(
                RecordedWorkerEvent(
                    run_id=active.run.id,
                    type=event_type,
                    message=message,
                    severity=severity,
                    details=dict(details) if details is not None else None,
                    recorded_at_utc=self._utc(),
                )
            )

    async def put_artifact(
        self,
        lease: ExecutionWorkerLease,
        artifact: ExecutionArtifactWrite,
    ) -> ExecutionArtifact:
        async with self._lock:
            active = self._require_lease(lease)
            content = _artifact_bytes(artifact)
            if len(content) > MAX_ARTIFACT_BYTES:
                raise ValueError(
                    f"Artifact exceeds {MAX_ARTIFACT_BYTES} bytes."
                )
            result = ExecutionArtifact(
                id=self._id("artifact"),
                run_id=active.run.id,
                name=artifact.name,
                kind=artifact.kind,
                media_type=artifact.media_type,
                content_hash="sha256:" + sha256(content).hexdigest(),
                size_bytes=len(content),
                text=artifact.text,
                content=artifact.content,
                uri=artifact.uri,
                created_at_utc=self._utc(),
                metadata=dict(artifact.metadata),
            )
            self._artifacts.setdefault(active.run.id, []).append(result)
            return result

    async def wait(
        self,
        lease: ExecutionWorkerLease,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWorkerWaitResponse:
        async with self._lock:
            active = self._require_lease(lease)
            key = (active.run.id, request.kind, request.name)
            outcome = self._wait_outcomes.pop(key, None)
            if outcome is not None:
                run = self._runs[active.run.id]
                return ExecutionWorkerWaitResponse(
                    run=run,
                    suspended=False,
                    outcome=outcome,
                )
            now = self._utc()
            run = replace(
                self._runs[active.run.id],
                status="waiting",
                updated_at_utc=now,
            )
            self._runs[run.id] = run
            self._waiting[run.id] = request
            self._leases.pop(active.lease_key, None)
            return ExecutionWorkerWaitResponse(
                run=run,
                suspended=True,
            )

    async def complete(
        self,
        lease: ExecutionWorkerLease,
        result: ExecutionRunResult,
    ) -> ExecutionRun:
        async with self._lock:
            active = self._require_lease(lease)
            self.completion_count += 1
            if self.fail_completion:
                raise RuntimeError("transport completion failed")
            now = self._utc()
            current = self._runs[active.run.id]
            duration_ms = (
                (now - current.started_at_utc).total_seconds() * 1000
                if current.started_at_utc is not None
                else None
            )
            run = replace(
                current,
                status=result.status,
                result=(
                    result.result
                    if result.result is not None
                    else current.result
                ),
                failure_class=(
                    None
                    if result.status == "succeeded"
                    else result.failure_class or current.failure_class
                ),
                error=(
                    None
                    if result.status == "succeeded"
                    else result.error or current.error
                ),
                status_details=(
                    result.status_details
                    if result.status_details is not None
                    else current.status_details
                ),
                completed_at_utc=now,
                updated_at_utc=now,
                duration_ms=duration_ms,
                progress=(
                    1.0
                    if result.status == "succeeded"
                    else current.progress
                ),
                current_step=None,
                cancellation_requested=(
                    current.cancellation_requested
                    or result.status == "cancelled"
                ),
            )
            self._runs[run.id] = run
            self._leases.pop(active.lease_key, None)
            self._waiting.pop(run.id, None)
            return run

    async def raise_external_event(
        self,
        run_id: str,
        name: str,
        payload: JSONValue = None,
    ) -> ExecutionExternalEvent:
        async with self._lock:
            run = self._runs.get(run_id)
            if run is None:
                raise LookupError(f"Execution run {run_id!r} was not found.")
            event = ExecutionExternalEvent(
                id=self._id("event"),
                name=name,
                run_id=run_id,
                raised_at_utc=self._utc(),
                payload=payload,
            )
            self._wait_outcomes[(run_id, "external_event", name)] = (
                ExecutionWaitResult(
                    name=name,
                    outcome="external_event",
                    event=event,
                )
            )
            self._resume_waiting(run_id)
            return event

    async def fire_timer(
        self,
        run_id: str,
        name: str,
        *,
        fire_at_utc: datetime | None = None,
        payload: JSONValue = None,
    ) -> ExecutionTimer:
        async with self._lock:
            run = self._runs.get(run_id)
            if run is None:
                raise LookupError(f"Execution run {run_id!r} was not found.")
            now = self._utc()
            timer = ExecutionTimer(
                id=self._id("timer"),
                name=name,
                run_id=run_id,
                fire_at_utc=fire_at_utc or now,
                created_at_utc=now,
                payload=payload,
            )
            self._wait_outcomes[(run_id, "timer", name)] = (
                ExecutionWaitResult(
                    name=name,
                    outcome="timer",
                    timer=timer,
                )
            )
            self._resume_waiting(run_id)
            return timer

    async def request_cancellation(self, run_id: str) -> ExecutionRun:
        async with self._lock:
            run = self._runs.get(run_id)
            if run is None:
                raise LookupError(f"Execution run {run_id!r} was not found.")
            updated = replace(
                run,
                cancellation_requested=True,
                updated_at_utc=self._utc(),
            )
            self._runs[run_id] = updated
            for key, lease in tuple(self._leases.items()):
                if lease.run.id == run_id:
                    self._leases[key] = replace(lease, run=updated)
            return updated

    async def get_run(self, run_id: str) -> ExecutionRun | None:
        async with self._lock:
            return self._runs.get(run_id)

    async def get_artifacts(
        self, run_id: str
    ) -> tuple[ExecutionArtifact, ...]:
        async with self._lock:
            return tuple(self._artifacts.get(run_id, ()))

    async def get_events(
        self, run_id: str
    ) -> tuple[RecordedWorkerEvent, ...]:
        async with self._lock:
            return tuple(self._events.get(run_id, ()))

    async def get_checkpoints(
        self, run_id: str
    ) -> tuple[ExecutionCheckpoint, ...]:
        async with self._lock:
            return tuple(
                checkpoint
                for (selected_run, _), checkpoint in sorted(
                    self._checkpoints.items()
                )
                if selected_run == run_id
            )

    def _require_lease(
        self, supplied: ExecutionWorkerLease
    ) -> ExecutionWorkerLease:
        active = self._leases.get(supplied.lease_key)
        if (
            active is None
            or active.lease_token != supplied.lease_token
            or active.worker_id != (supplied.worker_id or self.worker_id)
            or active.expires_at_utc <= self._utc()
        ):
            raise RuntimeError(
                "External-worker lease is invalid or expired."
            )
        return active

    def _resume_waiting(self, run_id: str) -> None:
        run = self._runs[run_id]
        if run.status != "waiting":
            return
        resumed = replace(
            run,
            status="queued",
            updated_at_utc=self._utc(),
        )
        self._runs[run_id] = resumed
        if run_id not in self._queue:
            self._queue.append(run_id)

    def _id(self, kind: str) -> str:
        self._sequence += 1
        return f"{kind}-{self._sequence:08d}"

    def _utc(self) -> datetime:
        selected = self._clock()
        if selected.tzinfo is None:
            raise ValueError("Execution harness clock must include an offset.")
        return selected.astimezone(timezone.utc)


class ExecutionHandlerHarness:
    def __init__(
        self,
        plugin: StaticExecutionPlugin,
        *,
        worker_id: str = "python-test-worker",
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.plugin = plugin
        self.transport = InMemoryExecutionWorkerTransport(
            worker_id,
            (handler.descriptor.handler_id for handler in plugin.handlers),
            clock=clock,
        )
        self.worker = ExecutionPluginWorker(
            self.transport,
            (plugin,),
            ExecutionPluginWorkerOptions(
                heartbeat_interval_seconds=None,
            ),
        )

    async def run(
        self,
        handler_id: str,
        *,
        payload: JSONValue = None,
        run_id: str | None = None,
    ) -> ExecutionRun:
        accepted = await self.transport.enqueue_run(
            handler_id,
            plugin_id=self.plugin.descriptor.plugin_id,
            payload=payload,
            run_id=run_id,
        )
        completed = await self.worker.run_once(accepted.id)
        if completed is None:
            raise RuntimeError("Handler harness did not lease the accepted run.")
        return completed


def _canonical_bytes(value: JSONValue) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")


def _artifact_bytes(artifact: ExecutionArtifactWrite) -> bytes:
    if artifact.kind == "text":
        return (artifact.text or "").encode("utf-8")
    if artifact.kind == "object_reference":
        return (artifact.uri or "").encode("utf-8")
    return _canonical_bytes(artifact.content)
