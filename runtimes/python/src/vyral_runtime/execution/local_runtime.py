from __future__ import annotations

import asyncio
from contextlib import contextmanager
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import hmac
import json
from pathlib import Path
from secrets import token_hex
import sqlite3
from threading import Lock
from typing import Any, Callable, Iterable, Iterator, Mapping, Protocol, cast
from uuid import uuid4

from .._datetime import parse_iso_datetime
from .._version import RUNTIME_VERSION
from ..contracts import JSONValue
from ..local.models import JSONObject
from .models import (
    MAX_ARTIFACT_BYTES,
    MAX_ARTIFACT_INLINE_BYTES,
    MAX_CHECKPOINT_BYTES,
    MAX_LEASE_TTL_SECONDS,
    MAX_PAYLOAD_BYTES,
    MAX_STATUS_DETAILS_BYTES,
    MAX_TRACE_DETAILS_BYTES,
    MAX_TRACE_MESSAGE_CHARS,
    TERMINAL_RUN_STATUSES,
    ExecutionArtifact,
    ExecutionArtifactWrite,
    ExecutionCheckpoint,
    ExecutionCheckpointWrite,
    ExecutionExternalEvent,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionRun,
    ExecutionRunResult,
    ExecutionRunUpdate,
    ExecutionTimer,
    ExecutionWaitResult,
    ExecutionWorkerLease,
    ExecutionWorkerWaitRequest,
    ExecutionWorkerWaitResponse,
    validate_run_result,
)
from .native_models import (
    ExecutionExternalEventRequest,
    ExecutionHistoryQuery,
    ExecutionLease,
    ExecutionLeaseRequest,
    ExecutionMaintenanceDispatchReconcileRequest,
    ExecutionMaintenanceDispatchReconcileResult,
    ExecutionMaintenancePruneRequest,
    ExecutionMaintenancePruneResult,
    ExecutionMaintenanceStatus,
    ExecutionOperationalPolicy,
    ExecutionProductPolicy,
    ExecutionRetryPolicy,
    ExecutionResumePolicy,
    ExecutionRunQuery,
    ExecutionRunRequest,
    ExecutionRuntimeAdapterDescriptor,
    ExecutionRuntimeAdapterStatus,
    ExecutionTimerRequest,
    ExecutionTraceEvent,
)
from .worker import (
    ExecutionHandler,
    ExecutionRunContext,
    StaticExecutionPlugin,
)


Clock = Callable[[], datetime]


class _ExecutionWorkerLeaseCredentials(Protocol):
    @property
    def lease_key(self) -> str: ...

    @property
    def lease_token(self) -> str: ...

    @property
    def worker_id(self) -> str: ...


class ExecutionRuntimeError(RuntimeError):
    """Base error for the native durable execution runtime."""


class ExecutionRuntimeConflictError(ExecutionRuntimeError):
    pass


class ExecutionRuntimeLeaseError(ExecutionRuntimeError):
    pass


class ExecutionRuntimePolicyError(ExecutionRuntimeError):
    pass


class _RunSuspended(Exception):
    pass


@dataclass(frozen=True)
class LocalExecutionRuntimeOptions:
    database_path: Path
    artifact_directory: Path | None = None
    adapter_id: str = "python-local-sqlite"
    max_active_runs: int = 100
    max_retained_terminal_runs: int = 500
    default_list_limit: int = 50
    max_list_limit: int = 200
    busy_timeout_ms: int = 5_000
    worker_id: str = "python-local"
    max_artifact_inline_bytes: int = MAX_ARTIFACT_INLINE_BYTES
    product_policies: tuple[ExecutionProductPolicy, ...] = ()

    def __post_init__(self) -> None:
        database_path = Path(self.database_path).expanduser().resolve()
        artifact_directory = (
            Path(self.artifact_directory).expanduser().resolve()
            if self.artifact_directory is not None
            else database_path.parent
            / (database_path.stem + "-execution-artifacts")
        )
        if not self.adapter_id.strip():
            raise ValueError("Execution adapter_id is required.")
        if self.max_active_runs <= 0:
            raise ValueError("max_active_runs must be positive.")
        if self.max_retained_terminal_runs < 0:
            raise ValueError(
                "max_retained_terminal_runs must be non-negative."
            )
        if not (
            0 < self.default_list_limit <= self.max_list_limit <= 1_000
        ):
            raise ValueError(
                "Execution list limits are invalid or unbounded."
            )
        if self.busy_timeout_ms < 0:
            raise ValueError("busy_timeout_ms must be non-negative.")
        if not (
            0
            <= self.max_artifact_inline_bytes
            <= MAX_ARTIFACT_BYTES
        ):
            raise ValueError(
                "max_artifact_inline_bytes must be between zero and "
                f"{MAX_ARTIFACT_BYTES}."
            )
        if not self.worker_id.strip():
            raise ValueError("Execution worker_id is required.")
        policies = tuple(
            ExecutionProductPolicy.from_value(policy)
            for policy in self.product_policies
        )
        if len({policy.product_id for policy in policies}) != len(
            policies
        ):
            raise ValueError(
                "Execution product policies contain a duplicate "
                "product id."
            )
        object.__setattr__(self, "database_path", database_path)
        object.__setattr__(
            self, "artifact_directory", artifact_directory
        )
        object.__setattr__(self, "product_policies", policies)


class LocalExecutionRuntime:
    """SQLite-backed durable runtime for native Python handlers and workers."""

    capabilities = (
        "local.dispatch",
        "in_process.handlers",
        "external.workers",
        "durable.runs",
        "durable.timers",
        "external.events",
        "durable.waits",
        "cancellation",
        "retries",
        "restart.resume",
        "leases",
        "artifacts",
        "trace.history",
        "idempotency",
    )

    def __init__(
        self,
        options: LocalExecutionRuntimeOptions | str | Path,
        *,
        clock: Clock | None = None,
        token_factory: Callable[[], str] | None = None,
    ) -> None:
        self.options = (
            options
            if isinstance(options, LocalExecutionRuntimeOptions)
            else LocalExecutionRuntimeOptions(Path(options))
        )
        self.options.database_path.parent.mkdir(
            parents=True, exist_ok=True
        )
        assert self.options.artifact_directory is not None
        self.options.artifact_directory.mkdir(
            parents=True, exist_ok=True
        )
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._token_factory = token_factory or (
            lambda: token_hex(32)
        )
        self._handlers: dict[str, ExecutionHandler] = {}
        self._handler_descriptors: dict[
            str, ExecutionHandlerDescriptor
        ] = {}
        self._external_handlers: set[str] = set()
        self._plugins: dict[str, ExecutionPluginDescriptor] = {}
        self._product_policies = {
            policy.product_id: policy
            for policy in self.options.product_policies
        }
        self._cancel_signals: dict[str, asyncio.Event] = {}
        self._init_lock = Lock()
        self._initialized = False
        self._ensure_initialized()

    def register_handler(self, handler: ExecutionHandler) -> None:
        descriptor = ExecutionHandlerDescriptor.from_value(
            handler.descriptor
        )
        existing = self._handler_descriptors.get(
            descriptor.handler_id
        )
        if existing is not None and existing != descriptor:
            raise ExecutionRuntimeConflictError(
                f"Handler {descriptor.handler_id!r} is already registered "
                "with a different descriptor."
            )
        self._handlers[descriptor.handler_id] = handler
        self._handler_descriptors[descriptor.handler_id] = descriptor
        self._external_handlers.discard(descriptor.handler_id)

    def register_external_handler(
        self,
        descriptor: ExecutionHandlerDescriptor | Mapping[str, Any],
    ) -> None:
        selected = ExecutionHandlerDescriptor.from_value(descriptor)
        existing = self._handler_descriptors.get(selected.handler_id)
        if existing is not None and existing != selected:
            raise ExecutionRuntimeConflictError(
                f"Handler {selected.handler_id!r} is already registered "
                "with a different descriptor."
            )
        self._handler_descriptors[selected.handler_id] = selected
        self._external_handlers.add(selected.handler_id)
        self._handlers.pop(selected.handler_id, None)

    def register_plugin(self, plugin: StaticExecutionPlugin) -> None:
        descriptor = ExecutionPluginDescriptor.from_value(
            plugin.descriptor
        )
        existing = self._plugins.get(descriptor.plugin_id)
        if existing is not None and existing != descriptor:
            raise ExecutionRuntimeConflictError(
                f"Plugin {descriptor.plugin_id!r} is already registered "
                "with a different descriptor."
            )
        for handler in plugin.handlers:
            self.register_handler(handler)
        self._plugins[descriptor.plugin_id] = descriptor

    def list_plugins(self) -> tuple[ExecutionPluginDescriptor, ...]:
        return tuple(
            self._plugins[key] for key in sorted(self._plugins)
        )

    def list_handlers(self) -> tuple[ExecutionHandlerDescriptor, ...]:
        return tuple(
            self._handler_descriptors[key]
            for key in sorted(self._handler_descriptors)
        )

    async def start_run(
        self,
        request: ExecutionRunRequest | Mapping[str, Any],
    ) -> ExecutionRun:
        selected = ExecutionRunRequest.from_value(request)
        payload_size = len(_canonical_bytes(selected.payload))
        if payload_size > MAX_PAYLOAD_BYTES:
            raise ValueError(
                f"Execution payload exceeds {MAX_PAYLOAD_BYTES} bytes."
            )
        self._ensure_run_boundary(selected, payload_size)
        descriptor = self._handler_descriptors.get(
            selected.handler_id
        )
        now = self._now()
        payload_hash = _hash_json(selected.payload)
        plugin_id = selected.plugin_id
        rejected_status: tuple[str, str] | None = None
        concurrency_key: str | None = None
        max_attempts = selected.retry_policy.max_attempts
        if descriptor is None:
            rejected_status = (
                "handler_missing",
                "Execution handler is not registered.",
            )
        else:
            concurrency_key = descriptor.concurrency_key
            if plugin_id is None:
                plugin_id = descriptor.plugin_id
            elif plugin_id != descriptor.plugin_id:
                rejected_status = (
                    "plugin_mismatch",
                    "Execution handler does not belong to the "
                    "requested plugin.",
                )
        fingerprint = _hash_json(
            {
                "handlerId": selected.handler_id,
                "pluginId": plugin_id,
                "payloadHash": payload_hash,
            }
        )
        correlation_id = selected.correlation_id or _id("corr")
        run = ExecutionRun(
            id=_id("run"),
            handler_id=selected.handler_id,
            plugin_id=plugin_id,
            status=(
                "rejected"
                if rejected_status is not None
                else (
                    "waiting"
                    if selected.scheduled_at_utc is not None
                    and selected.scheduled_at_utc > now
                    else "queued"
                )
            ),
            attempt=0,
            max_attempts=max_attempts,
            retry_policy=selected.retry_policy.to_dict(),
            idempotency_key=selected.idempotency_key,
            correlation_id=correlation_id,
            scope=selected.scope,
            payload_hash=payload_hash,
            payload=selected.payload,
            created_at_utc=now,
            scheduled_at_utc=selected.scheduled_at_utc,
            updated_at_utc=now,
            completed_at_utc=(
                now if rejected_status is not None else None
            ),
            cancellation_requested=False,
            failure_class=(
                rejected_status[0]
                if rejected_status is not None
                else None
            ),
            error=(
                rejected_status[1]
                if rejected_status is not None
                else None
            ),
            tags=selected.tags,
        )
        with self._transaction() as connection:
            if selected.idempotency_key is not None:
                existing = connection.execute(
                    """
                    SELECT run_json, request_fingerprint
                    FROM vyral_execution_runs
                    WHERE idempotency_key = ?
                    """,
                    (selected.idempotency_key,),
                ).fetchone()
                if existing is not None:
                    if (
                        str(existing["request_fingerprint"])
                        != fingerprint
                    ):
                        raise ExecutionRuntimeConflictError(
                            f"Execution idempotency key "
                            f"{selected.idempotency_key!r} already belongs "
                            "to a different run request."
                        )
                    return replace(
                        _run(str(existing["run_json"])),
                        admission_replayed=True,
                    )
            active = int(
                connection.execute(
                    """
                    SELECT COUNT(*)
                    FROM vyral_execution_runs
                    WHERE status IN ('queued', 'waiting', 'running')
                    """
                ).fetchone()[0]
            )
            if (
                rejected_status is None
                and active >= self.options.max_active_runs
            ):
                run = replace(
                    run,
                    status="rejected",
                    failure_class="queue_full",
                    error="Execution runtime active-run limit was reached.",
                    completed_at_utc=now,
                )
            self._insert_run(
                connection, run, fingerprint, concurrency_key
            )
            self._append_event(
                connection,
                run,
                "run.created",
                "Execution run created.",
            )
            if run.status == "rejected":
                self._append_event(
                    connection,
                    run,
                    "run.rejected",
                    run.error,
                    severity="warning",
                )
        return run

    async def get_run(
        self, run_id: str, *, include_result: bool = True
    ) -> ExecutionRun | None:
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT run_json
                FROM vyral_execution_runs
                WHERE run_id = ?
                """,
                (_required(run_id, "Run id"),),
            ).fetchone()
        if row is None:
            return None
        run = _run(str(row["run_json"]))
        return run if include_result else replace(run, result=None)

    async def list_runs(
        self,
        query: ExecutionRunQuery | Mapping[str, Any] | None = None,
    ) -> tuple[ExecutionRun, ...]:
        selected = ExecutionRunQuery.from_value(query)
        clauses: list[str] = []
        parameters: list[object] = []
        columns = (
            ("handler_id", selected.handler_id),
            ("plugin_id", selected.plugin_id),
            ("status", selected.status),
            ("correlation_id", selected.correlation_id),
            ("idempotency_key", selected.idempotency_key),
        )
        for column, value in columns:
            if value is not None:
                clauses.append(f"{column} = ?")
                parameters.append(value)
        date_columns: tuple[
            tuple[str, str, datetime | None], ...
        ] = (
            ("created_at_utc", ">", selected.created_after_utc),
            ("created_at_utc", "<", selected.created_before_utc),
            ("updated_at_utc", ">", selected.updated_after_utc),
            ("updated_at_utc", "<", selected.updated_before_utc),
        )
        for date_column, operator, date_value in date_columns:
            if date_value is not None:
                clauses.append(f"{date_column} {operator} ?")
                parameters.append(_format(date_value))
        limit = min(
            selected.limit or self.options.default_list_limit,
            self.options.max_list_limit,
        )
        sql = "SELECT run_json FROM vyral_execution_runs"
        if clauses:
            sql += " WHERE " + " AND ".join(clauses)
        sql += " ORDER BY created_at_utc DESC, run_id DESC LIMIT ?"
        parameters.append(limit)
        with self._connection() as connection:
            rows = connection.execute(sql, parameters).fetchall()
        runs = [_run(str(row["run_json"])) for row in rows]
        filtered = [
            run
            for run in runs
            if all(run.tags.get(key) == value for key, value in selected.tags.items())
        ]
        if not selected.include_result:
            filtered = [replace(run, result=None) for run in filtered]
        return tuple(filtered)

    async def cancel_run(self, run_id: str) -> ExecutionRun | None:
        selected = _required(run_id, "Run id")
        with self._transaction() as connection:
            current = self._load_run(connection, selected)
            if current is None:
                return None
            if current.status in TERMINAL_RUN_STATUSES:
                return current
            now = self._now()
            if current.status in {"queued", "waiting"}:
                updated = replace(
                    current,
                    status="cancelled",
                    cancellation_requested=True,
                    failure_class="cancelled",
                    error="Execution run was cancelled.",
                    updated_at_utc=now,
                    completed_at_utc=now,
                    current_step=None,
                )
                connection.execute(
                    "DELETE FROM vyral_execution_waits WHERE run_id = ?",
                    (selected,),
                )
            else:
                updated = replace(
                    current,
                    cancellation_requested=True,
                    updated_at_utc=now,
                )
            self._update_run(connection, updated)
            self._append_event(
                connection,
                updated,
                "run.cancellation_requested",
                "Execution cancellation requested.",
                severity="warning",
            )
        signal = self._cancel_signals.get(selected)
        if signal is not None:
            signal.set()
        return updated

    async def get_history(
        self,
        run_id: str,
        query: ExecutionHistoryQuery | None = None,
    ) -> tuple[ExecutionTraceEvent, ...]:
        limit = min(
            (query or ExecutionHistoryQuery()).limit
            or self.options.max_list_limit,
            self.options.max_list_limit,
        )
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT event_json
                FROM vyral_execution_history
                WHERE run_id = ?
                ORDER BY sequence DESC
                LIMIT ?
                """,
                (_required(run_id, "Run id"), limit),
            ).fetchall()
        return tuple(
            ExecutionTraceEvent.from_value(
                _load(str(row["event_json"]))
            )
            for row in reversed(rows)
        )

    async def list_artifacts(
        self, run_id: str
    ) -> tuple[ExecutionArtifact, ...]:
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT artifact_json
                FROM vyral_execution_artifacts
                WHERE run_id = ?
                ORDER BY created_at_utc, artifact_id
                """,
                (_required(run_id, "Run id"),),
            ).fetchall()
        return tuple(
            ExecutionArtifact.from_value(
                _load(str(row["artifact_json"]))
            )
            for row in rows
        )

    async def get_artifact(
        self, run_id: str, artifact_ref: str
    ) -> ExecutionArtifact | None:
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT artifact_json
                FROM vyral_execution_artifacts
                WHERE run_id = ?
                  AND (artifact_id = ? OR name = ?)
                ORDER BY created_at_utc DESC
                LIMIT 1
                """,
                (
                    _required(run_id, "Run id"),
                    _required(artifact_ref, "Artifact reference"),
                    artifact_ref.strip(),
                ),
            ).fetchone()
        return (
            None
            if row is None
            else self._rehydrate_artifact(
                ExecutionArtifact.from_value(
                    _load(str(row["artifact_json"]))
                )
            )
        )

    async def get_checkpoint(
        self,
        run_id: str | ExecutionWorkerLease,
        key: str,
    ) -> ExecutionCheckpoint | None:
        if isinstance(run_id, ExecutionWorkerLease):
            return await self.get_external_checkpoint(run_id, key)
        with self._connection() as connection:
            row = connection.execute(
                """
                SELECT checkpoint_json
                FROM vyral_execution_checkpoints
                WHERE run_id = ? AND checkpoint_key = ?
                """,
                (
                    _required(run_id, "Run id"),
                    _required(key, "Checkpoint key"),
                ),
            ).fetchone()
        return (
            None
            if row is None
            else ExecutionCheckpoint.from_value(
                _load(str(row["checkpoint_json"]))
            )
        )

    async def put_run_checkpoint(
        self,
        run_id: str,
        checkpoint: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint:
        """Persist host-owned coordination state for an existing run."""
        with self._transaction() as connection:
            run = self._load_run(
                connection, _required(run_id, "Run id")
            )
            if run is None:
                raise LookupError(f"Execution run {run_id!r} was not found.")
            return self._put_checkpoint(connection, run, checkpoint)

    async def try_acquire_lease(
        self,
        request: ExecutionLeaseRequest,
    ) -> ExecutionLease | None:
        now = self._now()
        lease = ExecutionLease(
            lease_key=request.lease_key,
            owner_id=request.owner_id,
            run_id=request.run_id,
            acquired_at_utc=now,
            expires_at_utc=now
            + timedelta(seconds=request.ttl_seconds),
            metadata=request.metadata,
        )
        with self._transaction() as connection:
            row = connection.execute(
                """
                SELECT lease_json, expires_at_utc
                FROM vyral_execution_leases
                WHERE lease_key = ?
                """,
                (request.lease_key,),
            ).fetchone()
            if row is not None:
                existing = ExecutionLease.from_value(
                    _load(str(row["lease_json"]))
                )
                if (
                    existing.expires_at_utc > now
                    and existing.owner_id != request.owner_id
                ):
                    return None
            connection.execute(
                """
                INSERT INTO vyral_execution_leases
                    (lease_key, owner_id, run_id, expires_at_utc,
                     lease_json, protocol, token_hash, state)
                VALUES (?, ?, ?, ?, ?, 'coordination', NULL, 'active')
                ON CONFLICT(lease_key) DO UPDATE SET
                    owner_id = excluded.owner_id,
                    run_id = excluded.run_id,
                    expires_at_utc = excluded.expires_at_utc,
                    lease_json = excluded.lease_json,
                    protocol = excluded.protocol,
                    token_hash = NULL,
                    state = 'active'
                """,
                (
                    lease.lease_key,
                    lease.owner_id,
                    lease.run_id,
                    _format(lease.expires_at_utc),
                    _dump(lease.to_dict()),
                ),
            )
            if request.run_id is not None:
                run = self._load_run(connection, request.run_id)
                if run is not None:
                    self._append_event(
                        connection,
                        run,
                        "lease.acquired",
                        f"Lease {request.lease_key!r} acquired.",
                    )
        return lease

    async def release_lease(
        self, lease_key: str, owner_id: str
    ) -> bool:
        with self._transaction() as connection:
            row = connection.execute(
                """
                SELECT run_id
                FROM vyral_execution_leases
                WHERE lease_key = ? AND owner_id = ?
                  AND protocol = 'coordination'
                """,
                (
                    _required(lease_key, "Lease key"),
                    _required(owner_id, "Lease owner id"),
                ),
            ).fetchone()
            if row is None:
                return False
            connection.execute(
                "DELETE FROM vyral_execution_leases WHERE lease_key = ?",
                (lease_key,),
            )
            if row["run_id"] is not None:
                run = self._load_run(
                    connection, str(row["run_id"])
                )
                if run is not None:
                    self._append_event(
                        connection,
                        run,
                        "lease.released",
                        f"Lease {lease_key!r} released.",
                    )
        return True

    async def schedule_timer(
        self, request: ExecutionTimerRequest
    ) -> ExecutionTimer:
        timer = ExecutionTimer(
            id=_id("timer"),
            name=request.name,
            run_id=request.run_id,
            fire_at_utc=request.fire_at_utc,
            created_at_utc=self._now(),
            payload=request.payload,
        )
        with self._transaction() as connection:
            self._insert_timer(connection, timer)
            if timer.run_id is not None:
                run = self._load_run(connection, timer.run_id)
                if run is not None:
                    self._append_event(
                        connection,
                        run,
                        "timer.scheduled",
                        f"Timer {timer.name!r} scheduled.",
                    )
        return timer

    async def raise_event(
        self, request: ExecutionExternalEventRequest
    ) -> ExecutionExternalEvent:
        event = ExecutionExternalEvent(
            id=_id("event"),
            name=request.name,
            run_id=request.run_id,
            raised_at_utc=self._now(),
            payload=request.payload,
        )
        with self._transaction() as connection:
            connection.execute(
                """
                INSERT INTO vyral_execution_external_events
                    (event_id, run_id, name, raised_at_utc,
                     consumed, event_json)
                VALUES (?, ?, ?, ?, 0, ?)
                """,
                (
                    event.id,
                    event.run_id,
                    event.name,
                    _format(event.raised_at_utc),
                    _dump(event.to_dict()),
                ),
            )
            if event.run_id is not None:
                run = self._load_run(connection, event.run_id)
                if run is not None:
                    self._append_event(
                        connection,
                        run,
                        "external_event.raised",
                        f"External event {event.name!r} raised.",
                    )
                    wait = connection.execute(
                        """
                        SELECT kind, name
                        FROM vyral_execution_waits
                        WHERE run_id = ? AND kind = 'external_event'
                          AND name = ?
                        """,
                        (event.run_id, event.name),
                    ).fetchone()
                    if wait is not None:
                        outcome = ExecutionWaitResult(
                            name=event.name,
                            outcome="external_event",
                            event=event,
                        )
                        self._resume_wait(
                            connection,
                            run,
                            "external_event",
                            event.name,
                            outcome,
                        )
                        resumed = self._load_run(
                            connection, event.run_id
                        )
                        if resumed is not None:
                            self._append_event(
                                connection,
                                resumed,
                                "wait.resumed",
                                f"External event "
                                f"{event.name!r} resumed execution.",
                            )
                        connection.execute(
                            """
                            UPDATE vyral_execution_external_events
                            SET consumed = 1
                            WHERE event_id = ?
                            """,
                            (event.id,),
                        )
        return event

    async def dispatch_ready_runs(
        self,
        *,
        recover_interrupted_runs: bool = False,
        limit: int | None = None,
    ) -> int:
        if recover_interrupted_runs:
            self._recover_interrupted_runs()
        self._activate_due_work()
        maximum = min(
            limit or self.options.max_active_runs,
            self.options.max_active_runs,
        )
        claimed: list[ExecutionRun] = []
        for _ in range(maximum):
            run = self._claim_next_in_process()
            if run is None:
                break
            claimed.append(run)
        if claimed:
            await asyncio.gather(
                *(self._execute_claimed(run) for run in claimed)
            )
        return len(claimed)

    async def reconcile_dispatch(
        self,
        request: ExecutionMaintenanceDispatchReconcileRequest,
    ) -> ExecutionMaintenanceDispatchReconcileResult:
        self._activate_due_work()
        limit = min(
            request.limit or self.options.max_list_limit,
            self.options.max_list_limit,
        )
        with self._connection() as connection:
            rows = connection.execute(
                """
                SELECT run_id
                FROM vyral_execution_runs
                WHERE status = 'queued'
                ORDER BY created_at_utc, run_id
                LIMIT ?
                """,
                (limit,),
            ).fetchall()
        candidates = tuple(str(row["run_id"]) for row in rows)
        dispatched = (
            0
            if request.dry_run
            else await self.dispatch_ready_runs(limit=limit)
        )
        return ExecutionMaintenanceDispatchReconcileResult(
            dry_run=request.dry_run,
            limit=limit,
            reconciled_at_utc=self._now(),
            candidate_run_ids=candidates,
            dispatched=dispatched,
            failures={},
        )

    async def get_maintenance_status(
        self,
    ) -> ExecutionMaintenanceStatus:
        with self._connection() as connection:
            run_counts = {
                str(row["status"]): int(row["count"])
                for row in connection.execute(
                    """
                    SELECT status, COUNT(*) AS count
                    FROM vyral_execution_runs
                    GROUP BY status
                    """
                ).fetchall()
            }
            tables = {
                "runs": "vyral_execution_runs",
                "events": "vyral_execution_history",
                "artifacts": "vyral_execution_artifacts",
                "checkpoints": "vyral_execution_checkpoints",
                "timers": "vyral_execution_timers",
                "externalEvents": (
                    "vyral_execution_external_events"
                ),
                "leases": "vyral_execution_leases",
                "waits": "vyral_execution_waits",
            }
            row_counts = {
                name: int(
                    connection.execute(
                        f"SELECT COUNT(*) FROM {table}"
                    ).fetchone()[0]
                )
                for name, table in tables.items()
            }
        assert self.options.artifact_directory is not None
        files = tuple(
            path
            for path in self.options.artifact_directory.rglob("*")
            if path.is_file()
        )
        return ExecutionMaintenanceStatus(
            adapter_id=self.options.adapter_id,
            runtime_kind="python-local-sqlite",
            checked_at_utc=self._now(),
            retention_scope="run_owned",
            max_retained_terminal_runs=(
                self.options.max_retained_terminal_runs
            ),
            run_counts=run_counts,
            row_counts=row_counts,
            artifact_directory=str(
                self.options.artifact_directory
            ),
            artifact_directory_count=sum(
                1
                for path in self.options.artifact_directory.iterdir()
                if path.is_dir()
            ),
            artifact_file_count=len(files),
            artifact_bytes=sum(path.stat().st_size for path in files),
        )

    async def get_adapter_status(
        self,
    ) -> ExecutionRuntimeAdapterStatus:
        diagnostics = self.diagnostics()
        healthy = bool(diagnostics["healthy"])
        active_value = diagnostics["activeRuns"]
        if isinstance(active_value, bool) or not isinstance(
            active_value, int
        ):
            raise ExecutionRuntimeError(
                "Execution diagnostics returned an invalid active-run "
                "count."
            )
        return ExecutionRuntimeAdapterStatus(
            adapter=ExecutionRuntimeAdapterDescriptor(
                adapter_id=self.options.adapter_id,
                runtime_kind="local.sqlite",
                display_name="Python local SQLite execution runtime",
                version=RUNTIME_VERSION,
                capabilities=self.capabilities,
                metadata={
                    "databasePath": str(self.options.database_path),
                    "artifactDirectory": str(
                        self.options.artifact_directory
                    ),
                    "journalMode": str(
                        diagnostics["journalMode"]
                    ),
                    "busyTimeoutMs": str(
                        self.options.busy_timeout_ms
                    ),
                },
            ),
            available=healthy,
            status="ok" if healthy else "unavailable",
            checked_at_utc=self._now(),
            active_runs=active_value,
            operational_policy=ExecutionOperationalPolicy(
                max_active_runs=self.options.max_active_runs,
                max_retained_terminal_runs=(
                    self.options.max_retained_terminal_runs
                ),
                default_list_limit=self.options.default_list_limit,
                max_list_limit=self.options.max_list_limit,
                default_history_limit=(
                    self.options.default_list_limit
                ),
                max_history_limit=self.options.max_list_limit,
                max_artifact_inline_bytes=(
                    self.options.max_artifact_inline_bytes
                ),
            ),
            resume_policy=ExecutionResumePolicy(),
            details={
                "quickCheck": diagnostics["quickCheck"],
                "foreignKeys": diagnostics["foreignKeys"],
                "configuredProductPolicies": len(
                    self._product_policies
                ),
            },
        )

    async def prune(
        self,
        request: ExecutionMaintenancePruneRequest,
    ) -> ExecutionMaintenancePruneResult:
        retain = (
            request.retain_terminal_runs
            if request.retain_terminal_runs is not None
            else self.options.max_retained_terminal_runs
        )
        placeholders = ",".join(
            "?" for _ in TERMINAL_RUN_STATUSES
        )
        with self._transaction() as connection:
            rows = connection.execute(
                f"""
                SELECT run_id
                FROM vyral_execution_runs
                WHERE status IN ({placeholders})
                ORDER BY completed_at_utc DESC, run_id DESC
                LIMIT -1 OFFSET ?
                """,
                (*sorted(TERMINAL_RUN_STATUSES), retain),
            ).fetchall()
            run_ids = tuple(str(row["run_id"]) for row in rows)
            counts = self._owned_counts(connection, run_ids)
            artifact_directories = sum(
                1
                for run_id in run_ids
                if self._artifact_run_directory(run_id).is_dir()
            )
            if not request.dry_run and run_ids:
                marks = ",".join("?" for _ in run_ids)
                for table in (
                    "vyral_execution_history",
                    "vyral_execution_artifacts",
                    "vyral_execution_checkpoints",
                    "vyral_execution_timers",
                    "vyral_execution_external_events",
                    "vyral_execution_leases",
                    "vyral_execution_waits",
                    "vyral_execution_wait_outcomes",
                ):
                    connection.execute(
                        f"DELETE FROM {table} "
                        f"WHERE run_id IN ({marks})",
                        run_ids,
                    )
                connection.execute(
                    f"DELETE FROM vyral_execution_runs "
                    f"WHERE run_id IN ({marks})",
                    run_ids,
                )
        deleted_artifact_directories = 0
        if not request.dry_run:
            for run_id in run_ids:
                if self._delete_artifact_run_directory(run_id):
                    deleted_artifact_directories += 1
        return ExecutionMaintenancePruneResult(
            dry_run=request.dry_run,
            retain_terminal_runs=retain,
            pruned_at_utc=self._now(),
            run_ids=run_ids,
            runs=len(run_ids),
            events=counts["events"],
            artifacts=counts["artifacts"],
            checkpoints=counts["checkpoints"],
            timers=counts["timers"],
            external_events=counts["externalEvents"],
            leases=counts["leases"],
            artifact_directories=(
                artifact_directories
                if request.dry_run
                else deleted_artifact_directories
            ),
        )

    def diagnostics(self) -> dict[str, JSONValue]:
        with self._connection() as connection:
            quick_check = str(
                connection.execute(
                    "PRAGMA quick_check"
                ).fetchone()[0]
            )
            foreign_keys = bool(
                connection.execute(
                    "PRAGMA foreign_keys"
                ).fetchone()[0]
            )
            journal_mode = str(
                connection.execute(
                    "PRAGMA journal_mode"
                ).fetchone()[0]
            )
            active = int(
                connection.execute(
                    """
                    SELECT COUNT(*) FROM vyral_execution_runs
                    WHERE status IN ('queued', 'waiting', 'running')
                    """
                ).fetchone()[0]
            )
        return {
            "runtime": "LocalExecutionRuntime",
            "databasePath": str(self.options.database_path),
            "healthy": (
                quick_check.lower() == "ok"
                and foreign_keys
                and journal_mode.lower() == "wal"
            ),
            "quickCheck": quick_check,
            "foreignKeys": foreign_keys,
            "journalMode": journal_mode,
            "activeRuns": active,
            "maxActiveRuns": self.options.max_active_runs,
            "capabilities": list(self.capabilities),
        }

    # ExecutionWorkerTransport implementation.
    async def lease_next(
        self,
        run_id: str | None = None,
        ttl_seconds: float = 60.0,
        *,
        worker_id: str | None = None,
        handler_ids: Iterable[str] | None = None,
    ) -> ExecutionWorkerLease | None:
        ttl_seconds = _lease_ttl(ttl_seconds)
        selected_worker_id = _required(
            worker_id or self.options.worker_id, "Worker id"
        )
        requested_handlers = (
            frozenset(
                _required(handler_id, "Handler id")
                for handler_id in handler_ids
            )
            if handler_ids is not None
            else None
        )
        if requested_handlers is not None and not requested_handlers:
            return None
        self._activate_due_work()
        now = self._now()
        with self._transaction() as connection:
            selected: sqlite3.Row | None = None
            if run_id is not None:
                selected = connection.execute(
                    """
                    SELECT *
                    FROM vyral_execution_runs
                    WHERE run_id = ? AND status IN ('queued', 'running')
                    """,
                    (_required(run_id, "Run id"),),
                ).fetchone()
            else:
                eligible_handlers = sorted(
                    self._external_handlers
                    if requested_handlers is None
                    else self._external_handlers & requested_handlers
                )
                if not eligible_handlers:
                    return None
                marks = ",".join("?" for _ in eligible_handlers)
                selected = connection.execute(
                    f"""
                    SELECT *
                    FROM vyral_execution_runs
                    WHERE status IN ('queued', 'running')
                      AND handler_id IN ({marks})
                    ORDER BY created_at_utc, run_id
                    LIMIT 1
                    """,
                    eligible_handlers,
                ).fetchone()
            if selected is None:
                return None
            run = _run(str(selected["run_json"]))
            if run.handler_id not in self._external_handlers:
                return None
            if (
                requested_handlers is not None
                and run.handler_id not in requested_handlers
            ):
                return None
            if not self._external_worker_permitted(run):
                return None
            lease_key = "external:" + run.id
            existing = connection.execute(
                """
                SELECT * FROM vyral_execution_leases
                WHERE lease_key = ?
                """,
                (lease_key,),
            ).fetchone()
            if run.status == "running":
                if (
                    existing is not None
                    and str(existing["state"]) == "active"
                    and parse_iso_datetime(
                        str(existing["expires_at_utc"])
                    )
                    > now
                ):
                    return None
                run = replace(
                    run,
                    status="queued",
                    updated_at_utc=now,
                )
            if self._has_concurrency_conflict(
                connection,
                run.id,
                cast(str | None, selected["concurrency_key"]),
            ):
                return None
            token = self._token_factory()
            expires = now + timedelta(seconds=ttl_seconds)
            run = replace(
                run,
                status="running",
                attempt=run.attempt + 1,
                started_at_utc=run.started_at_utc or now,
                scheduled_at_utc=None,
                updated_at_utc=now,
            )
            self._update_run(connection, run)
            connection.execute(
                """
                INSERT INTO vyral_execution_leases
                    (lease_key, owner_id, run_id, expires_at_utc,
                     lease_json, protocol, token_hash, state)
                VALUES (?, ?, ?, ?, ?, 'external-worker', ?, 'active')
                ON CONFLICT(lease_key) DO UPDATE SET
                    owner_id = excluded.owner_id,
                    run_id = excluded.run_id,
                    expires_at_utc = excluded.expires_at_utc,
                    lease_json = excluded.lease_json,
                    protocol = excluded.protocol,
                    token_hash = excluded.token_hash,
                    state = 'active'
                """,
                (
                    lease_key,
                    selected_worker_id,
                    run.id,
                    _format(expires),
                    "{}",
                    _hash_text(token),
                ),
            )
            self._append_event(
                connection,
                run,
                "lease.acquired",
                "External worker lease acquired.",
            )
            self._append_event(
                connection,
                run,
                "run.started",
                "External worker run started.",
            )
        return ExecutionWorkerLease(
            lease_key=lease_key,
            lease_token=token,
            worker_id=selected_worker_id,
            run=run,
            acquired_at_utc=now,
            expires_at_utc=expires,
        )

    async def heartbeat(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        ttl_seconds: float = 60.0,
    ) -> ExecutionWorkerLease:
        ttl_seconds = _lease_ttl(ttl_seconds)
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            expires = self._now() + timedelta(seconds=ttl_seconds)
            connection.execute(
                """
                UPDATE vyral_execution_leases
                SET expires_at_utc = ?
                WHERE lease_key = ?
                """,
                (_format(expires), lease.lease_key),
            )
        return (
            replace(lease, run=run, expires_at_utc=expires)
            if isinstance(lease, ExecutionWorkerLease)
            else ExecutionWorkerLease(
                lease_key=lease.lease_key,
                lease_token=lease.lease_token,
                worker_id=lease.worker_id,
                run=run,
                acquired_at_utc=run.started_at_utc or run.updated_at_utc,
                expires_at_utc=expires,
            )
        )

    async def report(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        update: ExecutionRunUpdate,
    ) -> ExecutionRun:
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            updated = self._apply_update(run, update)
            self._update_run(connection, updated)
            self._append_event(
                connection,
                updated,
                "run.status",
                "External worker reported progress.",
            )
        return updated

    async def record_event(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None:
        self._validate_trace(message, details)
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            self._append_event(
                connection,
                run,
                _required(event_type, "Event type"),
                message,
                severity=severity,
                details=details,
            )

    async def put_artifact(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        artifact: ExecutionArtifactWrite,
    ) -> ExecutionArtifact:
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            return self._put_artifact(connection, run, artifact)

    async def checkpoint(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        checkpoint: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint:
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            return self._put_checkpoint(connection, run, checkpoint)

    async def get_external_checkpoint(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        key: str,
    ) -> ExecutionCheckpoint | None:
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            row = connection.execute(
                """
                SELECT checkpoint_json
                FROM vyral_execution_checkpoints
                WHERE run_id = ? AND checkpoint_key = ?
                """,
                (run.id, _required(key, "Checkpoint key")),
            ).fetchone()
        return (
            None
            if row is None
            else ExecutionCheckpoint.from_value(
                _load(str(row["checkpoint_json"]))
            )
        )

    async def wait(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWorkerWaitResponse:
        with self._transaction() as connection:
            run = self._require_external_lease(connection, lease)
            outcome = self._register_wait(connection, run, request)
            if outcome is not None:
                return ExecutionWorkerWaitResponse(
                    run=run, suspended=False, outcome=outcome
                )
            waiting = replace(
                run,
                status="waiting",
                updated_at_utc=self._now(),
            )
            self._update_run(connection, waiting)
            connection.execute(
                """
                UPDATE vyral_execution_leases
                SET state = 'suspended', expires_at_utc = ?
                WHERE lease_key = ?
                """,
                (_format(self._now()), lease.lease_key),
            )
        return ExecutionWorkerWaitResponse(
            run=waiting, suspended=True
        )

    async def complete(
        self,
        lease: _ExecutionWorkerLeaseCredentials,
        result: ExecutionRunResult,
    ) -> ExecutionRun:
        validate_run_result(result)
        with self._transaction() as connection:
            row = connection.execute(
                """
                SELECT * FROM vyral_execution_leases
                WHERE lease_key = ?
                """,
                (lease.lease_key,),
            ).fetchone()
            if (
                row is not None
                and str(row["state"]) == "completed"
                and self._lease_token_matches(row, lease)
            ):
                completed = self._load_run(
                    connection, str(row["run_id"])
                )
                if completed is None:
                    raise ExecutionRuntimeLeaseError(
                        "External worker completion state is corrupt."
                    )
                return completed
            run = self._require_external_lease(connection, lease)
            completed = self._complete_run(connection, run, result)
            connection.execute(
                """
                UPDATE vyral_execution_leases
                SET state = 'completed', expires_at_utc = ?
                WHERE lease_key = ?
                """,
                (_format(self._now()), lease.lease_key),
            )
        return completed

    def _recover_interrupted_runs(self) -> None:
        now = self._now()
        with self._transaction() as connection:
            rows = connection.execute(
                """
                SELECT run_json FROM vyral_execution_runs
                WHERE status = 'running'
                """
            ).fetchall()
            for row in rows:
                run = _run(str(row["run_json"]))
                recovered = replace(
                    run,
                    status="queued",
                    updated_at_utc=now,
                )
                self._update_run(connection, recovered)
                self._append_event(
                    connection,
                    recovered,
                    "run.status",
                    "Interrupted execution recovered for replay.",
                    severity="warning",
                )
            connection.execute(
                """
                DELETE FROM vyral_execution_leases
                WHERE protocol = 'external-worker'
                  AND state <> 'completed'
                """
            )

    def _activate_due_work(self) -> None:
        now = self._now()
        with self._transaction() as connection:
            waits = connection.execute(
                """
                SELECT * FROM vyral_execution_waits
                WHERE fire_at_utc IS NOT NULL AND fire_at_utc <= ?
                """,
                (_format(now),),
            ).fetchall()
            for wait in waits:
                run = self._load_run(
                    connection, str(wait["run_id"])
                )
                if run is None or run.status != "waiting":
                    continue
                kind = str(wait["kind"])
                name = str(wait["name"])
                if kind == "timer":
                    timer = ExecutionTimer.from_value(
                        _load(str(wait["timer_json"]))
                    )
                    outcome = ExecutionWaitResult(
                        name=name,
                        outcome="timer",
                        timer=timer,
                    )
                    event_type = "wait.resumed"
                else:
                    outcome = ExecutionWaitResult(
                        name=name, outcome="timed_out"
                    )
                    event_type = "wait.timed_out"
                self._resume_wait(
                    connection, run, kind, name, outcome
                )
                queued = self._load_run(connection, run.id)
                assert queued is not None
                self._append_event(
                    connection,
                    queued,
                    event_type,
                    f"Durable wait {name!r} became runnable.",
                )
            rows = connection.execute(
                """
                SELECT run_json
                FROM vyral_execution_runs
                WHERE status = 'waiting'
                  AND scheduled_at_utc IS NOT NULL
                  AND scheduled_at_utc <= ?
                  AND run_id NOT IN (
                      SELECT run_id FROM vyral_execution_waits
                  )
                """,
                (_format(now),),
            ).fetchall()
            for row in rows:
                run = _run(str(row["run_json"]))
                self._update_run(
                    connection,
                    replace(
                        run,
                        status="queued",
                        scheduled_at_utc=None,
                        updated_at_utc=now,
                    ),
                )

    def _claim_next_in_process(self) -> ExecutionRun | None:
        with self._transaction() as connection:
            rows = connection.execute(
                """
                SELECT *
                FROM vyral_execution_runs
                WHERE status = 'queued'
                ORDER BY created_at_utc, run_id
                """
            ).fetchall()
            for row in rows:
                run = _run(str(row["run_json"]))
                if run.handler_id not in self._handlers:
                    continue
                concurrency_key = cast(
                    str | None, row["concurrency_key"]
                )
                if self._has_concurrency_conflict(
                    connection, run.id, concurrency_key
                ):
                    continue
                now = self._now()
                claimed = replace(
                    run,
                    status="running",
                    attempt=run.attempt + 1,
                    started_at_utc=run.started_at_utc or now,
                    scheduled_at_utc=None,
                    updated_at_utc=now,
                )
                cursor = connection.execute(
                    """
                    UPDATE vyral_execution_runs
                    SET status = 'running', attempt = ?,
                        scheduled_at_utc = NULL,
                        updated_at_utc = ?, run_json = ?
                    WHERE run_id = ? AND status = 'queued'
                    """,
                    (
                        claimed.attempt,
                        _format(now),
                        _dump(claimed.to_dict()),
                        claimed.id,
                    ),
                )
                if cursor.rowcount != 1:
                    continue
                self._append_event(
                    connection,
                    claimed,
                    "run.started",
                    "Execution handler started.",
                )
                return claimed
        return None

    async def _execute_claimed(self, run: ExecutionRun) -> None:
        handler = self._handlers.get(run.handler_id)
        if handler is None:
            with self._transaction() as connection:
                current = self._load_run(connection, run.id)
                if current is not None:
                    self._complete_run(
                        connection,
                        current,
                        ExecutionRunResult(
                            status="failed",
                            failure_class="handler_missing",
                            error="Execution handler is not registered.",
                        ),
                    )
            return
        signal = asyncio.Event()
        self._cancel_signals[run.id] = signal
        context = _LocalExecutionRunContext(self, run, signal)
        try:
            result = await handler.execute(
                cast(ExecutionRunContext, context)
            )
            validate_run_result(result)
        except _RunSuspended:
            return
        except asyncio.CancelledError:
            result = ExecutionRunResult.cancelled_result()
        except Exception:
            result = ExecutionRunResult(
                status="failed",
                failure_class="unknown",
                error="Python execution handler failed.",
            )
        finally:
            self._cancel_signals.pop(run.id, None)
        with self._transaction() as connection:
            current = self._load_run(connection, run.id)
            if current is None or current.status != "running":
                return
            self._complete_run(connection, current, result)

    def _complete_run(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        result: ExecutionRunResult,
    ) -> ExecutionRun:
        now = self._now()
        status = result.status
        if run.cancellation_requested and status != "timed_out":
            status = "cancelled"
            result = ExecutionRunResult.cancelled_result(
                result.result
            )
        completed = replace(
            run,
            status=status,
            result=(
                result.result
                if result.result is not None
                else run.result
            ),
            failure_class=(
                None
                if status == "succeeded"
                else result.failure_class or run.failure_class
            ),
            error=(
                None
                if status == "succeeded"
                else result.error or run.error
            ),
            status_details=(
                result.status_details
                if result.status_details is not None
                else run.status_details
            ),
            completed_at_utc=now,
            updated_at_utc=now,
            duration_ms=(
                (now - (run.started_at_utc or now)).total_seconds()
                * 1000
            ),
            progress=1.0 if status == "succeeded" else run.progress,
            current_step=None,
            cancellation_requested=(
                run.cancellation_requested or status == "cancelled"
            ),
        )
        if (
            status in {"failed", "timed_out"}
            and not completed.cancellation_requested
            and completed.attempt < max(1, completed.max_attempts)
        ):
            policy = ExecutionRetryPolicy.from_value(
                completed.retry_policy
            )
            seconds = min(
                policy.max_delay_seconds,
                policy.initial_delay_seconds
                * (
                    policy.backoff_multiplier
                    ** max(0, completed.attempt - 1)
                ),
            )
            completed = replace(
                completed,
                status="waiting",
                scheduled_at_utc=now + timedelta(seconds=seconds),
                completed_at_utc=None,
                duration_ms=None,
            )
            self._update_run(connection, completed)
            self._append_event(
                connection,
                completed,
                "retry.scheduled",
                f"Retry {completed.attempt + 1} of "
                f"{completed.max_attempts} scheduled.",
                severity="warning",
            )
            return completed
        self._update_run(connection, completed)
        self._append_event(
            connection,
            completed,
            (
                "run.completed"
                if status in {"succeeded", "cancelled"}
                else "run.failed"
            ),
            (
                "Execution run completed."
                if status == "succeeded"
                else completed.error
            ),
            severity=(
                "info" if status == "succeeded" else "warning"
            ),
        )
        connection.execute(
            "DELETE FROM vyral_execution_waits WHERE run_id = ?",
            (run.id,),
        )
        return completed

    def _apply_update(
        self, run: ExecutionRun, update: ExecutionRunUpdate
    ) -> ExecutionRun:
        if update.status not in {None, "running"}:
            raise ValueError(
                "Progress reports cannot make terminal lifecycle "
                "transitions."
            )
        changes: dict[str, Any] = {
            "updated_at_utc": self._now()
        }
        for name in (
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
        return replace(run, **changes)

    def _put_artifact(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        write: ExecutionArtifactWrite,
    ) -> ExecutionArtifact:
        self._ensure_artifact_boundary(run, write.name)
        content = _artifact_bytes(write)
        if len(content) > MAX_ARTIFACT_BYTES:
            raise ValueError(
                f"Artifact exceeds {MAX_ARTIFACT_BYTES} bytes."
            )
        artifact_id = _id("artifact")
        metadata = dict(write.metadata)
        text = write.text
        artifact_content = write.content
        uri = write.uri
        if (
            uri is None
            and len(content) > self.options.max_artifact_inline_bytes
        ):
            uri = str(
                self._offload_artifact(
                    run.id,
                    artifact_id,
                    write.kind,
                    content,
                )
            )
            text = None
            artifact_content = None
            metadata.update(
                {
                    "storage": "local-file",
                    "offloaded": "true",
                    "inline": "false",
                }
            )
        artifact = ExecutionArtifact(
            id=artifact_id,
            run_id=run.id,
            name=write.name,
            kind=write.kind,
            media_type=write.media_type,
            content_hash="sha256:" + sha256(content).hexdigest(),
            size_bytes=len(content),
            text=text,
            content=artifact_content,
            uri=uri,
            created_at_utc=self._now(),
            metadata=metadata,
        )
        connection.execute(
            """
            INSERT INTO vyral_execution_artifacts
                (artifact_id, run_id, name, created_at_utc,
                 artifact_json)
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                artifact.id,
                run.id,
                artifact.name,
                _format(artifact.created_at_utc),
                _dump(artifact.to_dict()),
            ),
        )
        self._append_event(
            connection,
            run,
            "artifact.written",
            f"Artifact {artifact.name!r} written.",
        )
        return artifact

    def _offload_artifact(
        self,
        run_id: str,
        artifact_id: str,
        kind: str,
        content: bytes,
    ) -> Path:
        directory = self._artifact_run_directory(run_id)
        directory.mkdir(parents=True, exist_ok=True)
        extension = ".json" if kind == "json" else ".txt"
        path = directory / (
            _safe_path_segment(artifact_id) + extension
        )
        temporary = path.with_name(path.name + ".tmp")
        try:
            temporary.write_bytes(content)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
        return path

    def _rehydrate_artifact(
        self, artifact: ExecutionArtifact
    ) -> ExecutionArtifact:
        if (
            artifact.text is not None
            or artifact.content is not None
            or artifact.uri is None
            or artifact.metadata.get("storage") != "local-file"
        ):
            return artifact
        root = cast(Path, self.options.artifact_directory).resolve()
        path = Path(artifact.uri).expanduser().resolve()
        try:
            path.relative_to(root)
        except ValueError as exc:
            raise ExecutionRuntimeError(
                "Execution artifact uri is outside the local "
                "artifact directory."
            ) from exc
        if not path.is_file():
            return artifact
        material = path.read_bytes()
        if (
            "sha256:" + sha256(material).hexdigest()
            != artifact.content_hash
        ):
            raise ExecutionRuntimeError(
                "Execution artifact content hash does not match its "
                "durable projection."
            )
        if artifact.kind == "json":
            return replace(
                artifact,
                content=cast(JSONValue, json.loads(material)),
            )
        return replace(artifact, text=material.decode("utf-8"))

    def _artifact_run_directory(self, run_id: str) -> Path:
        root = cast(Path, self.options.artifact_directory)
        return root / _safe_path_segment(run_id)

    def _delete_artifact_run_directory(
        self, run_id: str
    ) -> bool:
        directory = self._artifact_run_directory(run_id)
        if not directory.is_dir():
            return False
        for path in sorted(
            directory.rglob("*"),
            key=lambda item: len(item.parts),
            reverse=True,
        ):
            if path.is_file() or path.is_symlink():
                path.unlink(missing_ok=True)
            elif path.is_dir():
                path.rmdir()
        directory.rmdir()
        return True

    def _put_checkpoint(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        write: ExecutionCheckpointWrite,
    ) -> ExecutionCheckpoint:
        material = _canonical_bytes(write.content)
        if len(material) > MAX_CHECKPOINT_BYTES:
            raise ValueError(
                f"Checkpoint exceeds {MAX_CHECKPOINT_BYTES} bytes."
            )
        checkpoint = ExecutionCheckpoint(
            run_id=run.id,
            key=write.key,
            content_hash="sha256:" + sha256(material).hexdigest(),
            updated_at_utc=self._now(),
            content=write.content,
            metadata=write.metadata,
        )
        connection.execute(
            """
            INSERT INTO vyral_execution_checkpoints
                (run_id, checkpoint_key, updated_at_utc,
                 checkpoint_json)
            VALUES (?, ?, ?, ?)
            ON CONFLICT(run_id, checkpoint_key) DO UPDATE SET
                updated_at_utc = excluded.updated_at_utc,
                checkpoint_json = excluded.checkpoint_json
            """,
            (
                run.id,
                checkpoint.key,
                _format(checkpoint.updated_at_utc),
                _dump(checkpoint.to_dict()),
            ),
        )
        self._append_event(
            connection,
            run,
            "checkpoint.written",
            f"Checkpoint {checkpoint.key!r} written.",
        )
        return checkpoint

    def _register_wait(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        request: ExecutionWorkerWaitRequest,
    ) -> ExecutionWaitResult | None:
        existing = connection.execute(
            """
            SELECT outcome_json
            FROM vyral_execution_wait_outcomes
            WHERE run_id = ? AND kind = ? AND name = ?
            """,
            (run.id, request.kind, request.name),
        ).fetchone()
        if existing is not None:
            connection.execute(
                """
                DELETE FROM vyral_execution_wait_outcomes
                WHERE run_id = ? AND kind = ? AND name = ?
                """,
                (run.id, request.kind, request.name),
            )
            return ExecutionWaitResult.from_value(
                _load(str(existing["outcome_json"]))
            )
        now = self._now()
        if request.kind == "external_event":
            prior = connection.execute(
                """
                SELECT event_id, event_json
                FROM vyral_execution_external_events
                WHERE run_id = ? AND name = ? AND consumed = 0
                ORDER BY raised_at_utc, event_id
                LIMIT 1
                """,
                (run.id, request.name),
            ).fetchone()
            if prior is not None:
                connection.execute(
                    """
                    UPDATE vyral_execution_external_events
                    SET consumed = 1 WHERE event_id = ?
                    """,
                    (str(prior["event_id"]),),
                )
                return ExecutionWaitResult(
                    name=request.name,
                    outcome="external_event",
                    event=ExecutionExternalEvent.from_value(
                        _load(str(prior["event_json"]))
                    ),
                )
            if (
                request.timeout_at_utc is not None
                and request.timeout_at_utc <= now
            ):
                return ExecutionWaitResult(
                    name=request.name, outcome="timed_out"
                )
            fire_at = request.timeout_at_utc
            timer_json = None
        else:
            assert request.fire_at_utc is not None
            timer = ExecutionTimer(
                id=_id("timer"),
                name=request.name,
                run_id=run.id,
                fire_at_utc=request.fire_at_utc,
                created_at_utc=now,
                payload=request.payload,
            )
            if timer.fire_at_utc <= now:
                return ExecutionWaitResult(
                    name=request.name,
                    outcome="timer",
                    timer=timer,
                )
            self._insert_timer(connection, timer)
            fire_at = timer.fire_at_utc
            timer_json = _dump(timer.to_dict())
        connection.execute(
            """
            INSERT INTO vyral_execution_waits
                (run_id, kind, name, fire_at_utc, timer_json)
            VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(run_id) DO UPDATE SET
                kind = excluded.kind,
                name = excluded.name,
                fire_at_utc = excluded.fire_at_utc,
                timer_json = excluded.timer_json
            """,
            (
                run.id,
                request.kind,
                request.name,
                _format(fire_at) if fire_at is not None else None,
                timer_json,
            ),
        )
        self._append_event(
            connection,
            run,
            "wait.registered",
            f"Waiting for {request.kind} {request.name!r}.",
        )
        return None

    def _resume_wait(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        kind: str,
        name: str,
        outcome: ExecutionWaitResult,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_execution_wait_outcomes
                (run_id, kind, name, outcome_json)
            VALUES (?, ?, ?, ?)
            ON CONFLICT(run_id, kind, name) DO UPDATE SET
                outcome_json = excluded.outcome_json
            """,
            (run.id, kind, name, _dump(outcome.to_dict())),
        )
        connection.execute(
            "DELETE FROM vyral_execution_waits WHERE run_id = ?",
            (run.id,),
        )
        self._update_run(
            connection,
            replace(
                run,
                status="queued",
                scheduled_at_utc=None,
                updated_at_utc=self._now(),
            ),
        )

    def _insert_timer(
        self,
        connection: sqlite3.Connection,
        timer: ExecutionTimer,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_execution_timers
                (timer_id, run_id, name, fire_at_utc, timer_json)
            VALUES (?, ?, ?, ?, ?)
            """,
            (
                timer.id,
                timer.run_id,
                timer.name,
                _format(timer.fire_at_utc),
                _dump(timer.to_dict()),
            ),
        )

    def _require_external_lease(
        self,
        connection: sqlite3.Connection,
        lease: _ExecutionWorkerLeaseCredentials,
    ) -> ExecutionRun:
        row = connection.execute(
            """
            SELECT * FROM vyral_execution_leases
            WHERE lease_key = ?
            """,
            (lease.lease_key,),
        ).fetchone()
        if (
            row is None
            or str(row["state"]) != "active"
            or not self._lease_token_matches(row, lease)
            or parse_iso_datetime(
                str(row["expires_at_utc"])
            )
            <= self._now()
        ):
            raise ExecutionRuntimeLeaseError(
                "External worker lease is invalid or expired."
            )
        run = self._load_run(connection, str(row["run_id"]))
        if run is None or run.status != "running":
            raise ExecutionRuntimeLeaseError(
                "External worker lease is no longer active."
            )
        return run

    def _lease_token_matches(
        self, row: sqlite3.Row, lease: _ExecutionWorkerLeaseCredentials
    ) -> bool:
        expected = str(row["token_hash"] or "")
        return (
            str(row["owner_id"]) == lease.worker_id
            and bool(expected)
            and hmac.compare_digest(
                expected, _hash_text(lease.lease_token)
            )
        )

    def _has_concurrency_conflict(
        self,
        connection: sqlite3.Connection,
        run_id: str,
        concurrency_key: str | None,
    ) -> bool:
        if not concurrency_key:
            return False
        return (
            connection.execute(
                """
                SELECT 1
                FROM vyral_execution_runs
                WHERE run_id <> ? AND status = 'running'
                  AND concurrency_key = ?
                LIMIT 1
                """,
                (run_id, concurrency_key),
            ).fetchone()
            is not None
        )

    def _append_event(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        event_type: str,
        message: str | None,
        *,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> ExecutionTraceEvent:
        redacted_details = self._redact_trace_details(run, details)
        self._validate_trace(message, redacted_details)
        sequence = int(
            connection.execute(
                """
                SELECT COALESCE(MAX(sequence), 0) + 1
                FROM vyral_execution_history
                WHERE run_id = ?
                """,
                (run.id,),
            ).fetchone()[0]
        )
        event = ExecutionTraceEvent(
            id=_id("history"),
            sequence_id=f"{sequence:020d}",
            run_id=run.id,
            type=event_type,
            timestamp_utc=self._now(),
            attempt=run.attempt,
            status=run.status,
            severity=severity,
            message=message,
            details=redacted_details,
            context={
                key: value
                for key, value in (
                    (
                        "productId",
                        run.scope.product_id
                        if run.scope is not None
                        else "",
                    ),
                    (
                        "tenantId",
                        run.scope.tenant_id
                        if run.scope is not None
                        else "",
                    ),
                )
                if value
            },
        )
        connection.execute(
            """
            INSERT INTO vyral_execution_history
                (run_id, sequence, event_json)
            VALUES (?, ?, ?)
            """,
            (run.id, sequence, _dump(event.to_dict())),
        )
        return event

    def _ensure_run_boundary(
        self,
        request: ExecutionRunRequest,
        payload_size: int,
    ) -> None:
        if not self._product_policies:
            return
        scope = request.scope
        if scope is None:
            raise ExecutionRuntimePolicyError(
                "Execution scope is required when product policies "
                "are configured."
            )
        policy = self._product_policy(scope.product_id)
        if not scope.tenant_id.strip():
            raise ExecutionRuntimePolicyError(
                "Execution tenant id is required when product "
                "policies are configured."
            )
        if (
            policy.allowed_handler_ids
            and request.handler_id not in policy.allowed_handler_ids
        ):
            raise ExecutionRuntimePolicyError(
                f"Handler {request.handler_id!r} is not allowed for "
                f"product {scope.product_id!r}."
            )
        if (
            policy.allowed_tenant_ids
            and scope.tenant_id not in policy.allowed_tenant_ids
        ):
            raise ExecutionRuntimePolicyError(
                f"Tenant {scope.tenant_id!r} is not allowed for "
                f"product {scope.product_id!r}."
            )
        if (
            policy.max_payload_bytes is not None
            and payload_size > policy.max_payload_bytes
        ):
            raise ExecutionRuntimePolicyError(
                f"Run payload exceeds the "
                f"{policy.max_payload_bytes} byte limit for product "
                f"{scope.product_id!r}."
            )

    def _ensure_artifact_boundary(
        self, run: ExecutionRun, artifact_name: str
    ) -> None:
        if not self._product_policies:
            return
        if run.scope is None:
            raise ExecutionRuntimePolicyError(
                "Scoped execution run is missing its scope."
            )
        policy = self._product_policy(run.scope.product_id)
        if (
            policy.artifact_prefix is not None
            and not artifact_name.startswith(policy.artifact_prefix)
        ):
            raise ExecutionRuntimePolicyError(
                f"Artifact {artifact_name!r} must use product "
                f"{run.scope.product_id!r} prefix "
                f"{policy.artifact_prefix!r}."
            )

    def _external_worker_permitted(
        self, run: ExecutionRun
    ) -> bool:
        if not self._product_policies:
            return True
        if run.scope is None:
            raise ExecutionRuntimePolicyError(
                "Scoped execution run is missing its scope."
            )
        allowed = self._product_policy(
            run.scope.product_id
        ).allowed_service_identities
        return not allowed or self.options.worker_id in allowed

    def _product_policy(
        self, product_id: str
    ) -> ExecutionProductPolicy:
        policy = self._product_policies.get(product_id)
        if policy is None:
            raise ExecutionRuntimePolicyError(
                f"Execution product {product_id!r} is not configured."
            )
        return policy

    def _redact_trace_details(
        self,
        run: ExecutionRun,
        details: JSONObject | None,
    ) -> JSONObject | None:
        if details is None:
            return None
        selected = dict(details)
        if not self._product_policies or run.scope is None:
            return selected
        policy = self._product_policy(run.scope.product_id)
        for key in policy.redacted_json_property_names:
            if key in selected:
                selected[key] = "[redacted]"
        return selected

    def _validate_trace(
        self,
        message: str | None,
        details: JSONObject | None,
    ) -> None:
        if (
            message is not None
            and len(message) > MAX_TRACE_MESSAGE_CHARS
        ):
            raise ValueError(
                f"Execution trace message exceeds "
                f"{MAX_TRACE_MESSAGE_CHARS} characters."
            )
        if len(_canonical_bytes(details)) > MAX_TRACE_DETAILS_BYTES:
            raise ValueError(
                f"Execution trace details exceed "
                f"{MAX_TRACE_DETAILS_BYTES} bytes."
            )

    def _insert_run(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
        fingerprint: str,
        concurrency_key: str | None,
    ) -> None:
        connection.execute(
            """
            INSERT INTO vyral_execution_runs
                (run_id, handler_id, plugin_id, status, attempt,
                 concurrency_key, idempotency_key,
                 request_fingerprint, correlation_id,
                 created_at_utc, updated_at_utc,
                 scheduled_at_utc, completed_at_utc, run_json)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                run.id,
                run.handler_id,
                run.plugin_id,
                run.status,
                run.attempt,
                concurrency_key,
                run.idempotency_key,
                fingerprint,
                run.correlation_id,
                _format(run.created_at_utc),
                _format(run.updated_at_utc),
                (
                    _format(run.scheduled_at_utc)
                    if run.scheduled_at_utc is not None
                    else None
                ),
                (
                    _format(run.completed_at_utc)
                    if run.completed_at_utc is not None
                    else None
                ),
                _dump(run.to_dict()),
            ),
        )

    def _update_run(
        self,
        connection: sqlite3.Connection,
        run: ExecutionRun,
    ) -> None:
        connection.execute(
            """
            UPDATE vyral_execution_runs
            SET status = ?, attempt = ?, updated_at_utc = ?,
                scheduled_at_utc = ?, completed_at_utc = ?,
                run_json = ?
            WHERE run_id = ?
            """,
            (
                run.status,
                run.attempt,
                _format(run.updated_at_utc),
                (
                    _format(run.scheduled_at_utc)
                    if run.scheduled_at_utc is not None
                    else None
                ),
                (
                    _format(run.completed_at_utc)
                    if run.completed_at_utc is not None
                    else None
                ),
                _dump(run.to_dict()),
                run.id,
            ),
        )

    def _load_run(
        self, connection: sqlite3.Connection, run_id: str
    ) -> ExecutionRun | None:
        row = connection.execute(
            """
            SELECT run_json FROM vyral_execution_runs
            WHERE run_id = ?
            """,
            (run_id,),
        ).fetchone()
        return None if row is None else _run(str(row["run_json"]))

    def _owned_counts(
        self,
        connection: sqlite3.Connection,
        run_ids: tuple[str, ...],
    ) -> dict[str, int]:
        names = {
            "events": "vyral_execution_history",
            "artifacts": "vyral_execution_artifacts",
            "checkpoints": "vyral_execution_checkpoints",
            "timers": "vyral_execution_timers",
            "externalEvents": "vyral_execution_external_events",
            "leases": "vyral_execution_leases",
        }
        if not run_ids:
            return {name: 0 for name in names}
        marks = ",".join("?" for _ in run_ids)
        return {
            name: int(
                connection.execute(
                    f"SELECT COUNT(*) FROM {table} "
                    f"WHERE run_id IN ({marks})",
                    run_ids,
                ).fetchone()[0]
            )
            for name, table in names.items()
        }

    def _now(self) -> datetime:
        value = self._clock()
        if value.tzinfo is None:
            value = value.replace(tzinfo=timezone.utc)
        return value.astimezone(timezone.utc)

    @contextmanager
    def _connection(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(
            self.options.database_path,
            timeout=self.options.busy_timeout_ms / 1000,
            isolation_level=None,
        )
        connection.row_factory = sqlite3.Row
        try:
            connection.execute("PRAGMA foreign_keys = ON")
            connection.execute(
                f"PRAGMA busy_timeout = "
                f"{self.options.busy_timeout_ms}"
            )
            yield connection
        finally:
            connection.close()

    @contextmanager
    def _transaction(self) -> Iterator[sqlite3.Connection]:
        with self._connection() as connection:
            connection.execute("BEGIN IMMEDIATE")
            try:
                yield connection
                connection.commit()
            except BaseException:
                connection.rollback()
                raise

    def _ensure_initialized(self) -> None:
        if self._initialized:
            return
        with self._init_lock:
            if self._initialized:
                return
            with self._connection() as connection:
                connection.execute("PRAGMA journal_mode = WAL")
                connection.executescript(_SCHEMA)
            self._initialized = True


class _LocalExecutionRunContext:
    def __init__(
        self,
        runtime: LocalExecutionRuntime,
        run: ExecutionRun,
        cancellation: asyncio.Event,
    ) -> None:
        self.runtime = runtime
        self._run = run
        self.cancellation = cancellation

    @property
    def run(self) -> ExecutionRun:
        return self._run

    @property
    def cancellation_requested(self) -> bool:
        return self.cancellation.is_set()

    async def report(
        self,
        update: ExecutionRunUpdate | Mapping[str, Any],
    ) -> ExecutionRun:
        selected = ExecutionRunUpdate.from_value(update)
        with self.runtime._transaction() as connection:
            current = self.runtime._load_run(
                connection, self._run.id
            )
            if current is None or current.status != "running":
                raise ExecutionRuntimeError(
                    "Execution run is no longer running."
                )
            updated = self.runtime._apply_update(
                current, selected
            )
            self.runtime._update_run(connection, updated)
            self.runtime._append_event(
                connection,
                updated,
                "run.status",
                "Execution handler reported progress.",
            )
        self._run = updated
        return updated

    async def record_event(
        self,
        event_type: str,
        *,
        message: str | None = None,
        severity: str = "info",
        details: JSONObject | None = None,
    ) -> None:
        with self.runtime._transaction() as connection:
            current = self._required_run(connection)
            self.runtime._append_event(
                connection,
                current,
                _required(event_type, "Event type"),
                message,
                severity=severity,
                details=details,
            )

    async def put_artifact(
        self,
        artifact: ExecutionArtifactWrite | Mapping[str, Any],
    ) -> ExecutionArtifact:
        selected = ExecutionArtifactWrite.from_value(artifact)
        with self.runtime._transaction() as connection:
            return self.runtime._put_artifact(
                connection,
                self._required_run(connection),
                selected,
            )

    async def put_checkpoint(
        self,
        checkpoint: ExecutionCheckpointWrite | Mapping[str, Any],
    ) -> ExecutionCheckpoint:
        selected = ExecutionCheckpointWrite.from_value(checkpoint)
        with self.runtime._transaction() as connection:
            return self.runtime._put_checkpoint(
                connection,
                self._required_run(connection),
                selected,
            )

    async def get_checkpoint(
        self, key: str
    ) -> ExecutionCheckpoint | None:
        return await self.runtime.get_checkpoint(self._run.id, key)

    async def try_acquire_lease(
        self,
        lease_key: str,
        ttl_seconds: float = 60.0,
        metadata: JSONObject | None = None,
    ) -> ExecutionLease | None:
        return await self.runtime.try_acquire_lease(
            ExecutionLeaseRequest(
                lease_key=lease_key,
                owner_id=self._run.id,
                run_id=self._run.id,
                ttl_seconds=ttl_seconds,
                metadata=metadata,
            )
        )

    async def release_lease(self, lease_key: str) -> bool:
        return await self.runtime.release_lease(
            lease_key, self._run.id
        )

    async def schedule_timer(
        self,
        name: str,
        fire_at_utc: datetime,
        payload: JSONValue = None,
    ) -> ExecutionTimer:
        return await self.runtime.schedule_timer(
            ExecutionTimerRequest(
                name=name,
                run_id=self._run.id,
                fire_at_utc=fire_at_utc,
                payload=payload,
            )
        )

    async def raise_event(
        self,
        name: str,
        payload: JSONValue = None,
    ) -> ExecutionExternalEvent:
        return await self.runtime.raise_event(
            ExecutionExternalEventRequest(
                name=name,
                run_id=self._run.id,
                payload=payload,
            )
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
        self, request: ExecutionWorkerWaitRequest
    ) -> ExecutionWaitResult:
        with self.runtime._transaction() as connection:
            current = self._required_run(connection)
            outcome = self.runtime._register_wait(
                connection, current, request
            )
            if outcome is not None:
                return outcome
            waiting = replace(
                current,
                status="waiting",
                updated_at_utc=self.runtime._now(),
            )
            self.runtime._update_run(connection, waiting)
        self._run = waiting
        raise _RunSuspended()

    def _required_run(
        self, connection: sqlite3.Connection
    ) -> ExecutionRun:
        run = self.runtime._load_run(connection, self._run.id)
        if run is None:
            raise ExecutionRuntimeError(
                "Execution run no longer exists."
            )
        self._run = run
        return run


def _id(prefix: str) -> str:
    return prefix + "_" + uuid4().hex


def _lease_ttl(value: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError("Lease TTL must be a number.")
    selected = float(value)
    if not 0 < selected <= MAX_LEASE_TTL_SECONDS:
        raise ValueError(
            "Lease TTL must be greater than zero and no more than "
            f"{MAX_LEASE_TTL_SECONDS:g} seconds."
        )
    return selected


def _required(value: str, name: str) -> str:
    selected = value.strip()
    if not selected:
        raise ValueError(f"{name} is required.")
    if len(selected) > 160:
        raise ValueError(f"{name} exceeds 160 characters.")
    return selected


def _safe_path_segment(value: str) -> str:
    selected = "".join(
        character
        if character.isalnum() or character in {"-", "_", "."}
        else "_"
        for character in value
    )
    return selected or "_"


def _format(value: datetime) -> str:
    if value.tzinfo is None:
        raise ValueError("Execution timestamps require an offset.")
    return value.astimezone(timezone.utc).isoformat()


def _canonical_bytes(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _hash_json(value: object) -> str:
    return "sha256:" + sha256(_canonical_bytes(value)).hexdigest()


def _hash_text(value: str) -> str:
    return "sha256:" + sha256(value.encode("utf-8")).hexdigest()


def _artifact_bytes(value: ExecutionArtifactWrite) -> bytes:
    if value.kind == "text":
        if value.text is None:
            raise ValueError("Text artifacts require text.")
        return value.text.encode("utf-8")
    if value.kind == "object_reference":
        if value.uri is None:
            raise ValueError(
                "Object-reference artifacts require uri."
            )
        return value.uri.encode("utf-8")
    return _canonical_bytes(value.content)


def _dump(value: object) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    )


def _load(value: str) -> Mapping[str, Any]:
    selected = json.loads(value)
    if not isinstance(selected, Mapping):
        raise ExecutionRuntimeError(
            "Execution state did not contain a JSON object."
        )
    return cast(Mapping[str, Any], selected)


def _run(value: str) -> ExecutionRun:
    return ExecutionRun.from_value(_load(value))


_SCHEMA = """
CREATE TABLE IF NOT EXISTS vyral_execution_runs (
    run_id TEXT PRIMARY KEY,
    handler_id TEXT NOT NULL,
    plugin_id TEXT NULL,
    status TEXT NOT NULL,
    attempt INTEGER NOT NULL,
    concurrency_key TEXT NULL,
    idempotency_key TEXT NULL UNIQUE,
    request_fingerprint TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    scheduled_at_utc TEXT NULL,
    completed_at_utc TEXT NULL,
    run_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_runs_ready
ON vyral_execution_runs(status, scheduled_at_utc, created_at_utc);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_runs_concurrency
ON vyral_execution_runs(status, concurrency_key);
CREATE TABLE IF NOT EXISTS vyral_execution_history (
    run_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    event_json TEXT NOT NULL,
    PRIMARY KEY (run_id, sequence),
    FOREIGN KEY (run_id) REFERENCES vyral_execution_runs(run_id)
        ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS vyral_execution_artifacts (
    artifact_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    name TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    artifact_json TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES vyral_execution_runs(run_id)
        ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_artifacts_name
ON vyral_execution_artifacts(run_id, name, created_at_utc);
CREATE TABLE IF NOT EXISTS vyral_execution_checkpoints (
    run_id TEXT NOT NULL,
    checkpoint_key TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    checkpoint_json TEXT NOT NULL,
    PRIMARY KEY (run_id, checkpoint_key),
    FOREIGN KEY (run_id) REFERENCES vyral_execution_runs(run_id)
        ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS vyral_execution_leases (
    lease_key TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    run_id TEXT NULL,
    expires_at_utc TEXT NOT NULL,
    lease_json TEXT NOT NULL,
    protocol TEXT NOT NULL,
    token_hash TEXT NULL,
    state TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_leases_run
ON vyral_execution_leases(run_id);
CREATE TABLE IF NOT EXISTS vyral_execution_timers (
    timer_id TEXT PRIMARY KEY,
    run_id TEXT NULL,
    name TEXT NOT NULL,
    fire_at_utc TEXT NOT NULL,
    timer_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_timers_due
ON vyral_execution_timers(run_id, fire_at_utc);
CREATE TABLE IF NOT EXISTS vyral_execution_external_events (
    event_id TEXT PRIMARY KEY,
    run_id TEXT NULL,
    name TEXT NOT NULL,
    raised_at_utc TEXT NOT NULL,
    consumed INTEGER NOT NULL,
    event_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_vyral_execution_events_inbox
ON vyral_execution_external_events(run_id, name, consumed, raised_at_utc);
CREATE TABLE IF NOT EXISTS vyral_execution_waits (
    run_id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    fire_at_utc TEXT NULL,
    timer_json TEXT NULL,
    FOREIGN KEY (run_id) REFERENCES vyral_execution_runs(run_id)
        ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS vyral_execution_wait_outcomes (
    run_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    outcome_json TEXT NOT NULL,
    PRIMARY KEY (run_id, kind, name),
    FOREIGN KEY (run_id) REFERENCES vyral_execution_runs(run_id)
        ON DELETE CASCADE
);
"""


__all__ = [
    "ExecutionRuntimeConflictError",
    "ExecutionRuntimeError",
    "ExecutionRuntimeLeaseError",
    "ExecutionRuntimePolicyError",
    "LocalExecutionRuntime",
    "LocalExecutionRuntimeOptions",
]
