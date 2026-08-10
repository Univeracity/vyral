from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from hashlib import sha256
import json
import math
from pathlib import Path
import sqlite3
from typing import Any, Callable, Mapping, Sequence
from uuid import uuid4

from .._datetime import parse_iso_datetime
from ..async_runtime import RuntimeExecutor
from .models import JSONObject


DEFAULT_TRACE_LIST_LIMIT = 100
MAX_TRACE_LIST_LIMIT = 5000
DEFAULT_TRACE_EXPORT_LIMIT = 100
MAX_TRACE_EXPORT_LIMIT = 5000
DEFAULT_TRACE_PRUNE_LIMIT = 100
MAX_TRACE_PRUNE_LIMIT = 5000
MAX_TRACE_SUMMARY_DISTINCT_VALUES = 64
MAX_TRACE_SUMMARY_VALUE_LENGTH = 128


@dataclass(frozen=True)
class TraceRecord:
    operation: str
    id: str = field(default_factory=lambda: str(uuid4()))
    adapter: str | None = None
    request: JSONObject = field(default_factory=dict)
    result_summary: JSONObject = field(default_factory=dict)
    started_at: datetime = field(
        default_factory=lambda: datetime.now(timezone.utc)
    )
    duration_ms: float = 0.0
    created_at: datetime = field(
        default_factory=lambda: datetime.now(timezone.utc)
    )

    def to_dict(self) -> JSONObject:
        return {
            "id": self.id,
            "operation": self.operation,
            "adapter": self.adapter,
            "request": dict(self.request),
            "resultSummary": dict(self.result_summary),
            "startedAt": _format_datetime(self.started_at),
            "durationMs": self.duration_ms,
            "createdAt": _format_datetime(self.created_at),
        }


@dataclass(frozen=True)
class TracePruneRequest:
    operation: str | None = None
    older_than: datetime | None = None
    keep_latest: int | None = None
    limit: int | None = None
    dry_run: bool = False


@dataclass(frozen=True)
class TraceExportRequest:
    operation: str | None = None
    limit: int | None = None
    fail_on_unsafe_content: bool = False


@dataclass(frozen=True)
class TraceOperationSummary:
    operation: str
    count: int
    first_created_at: datetime
    latest_created_at: datetime
    adapters: tuple[str, ...]
    status_counts: Mapping[str, int]
    failure_class_counts: Mapping[str, int]
    provider_status_counts: Mapping[str, int]
    provider_counts: Mapping[str, int]
    capability_counts: Mapping[str, int]

    def to_dict(self) -> JSONObject:
        return {
            "operation": self.operation,
            "count": self.count,
            "firstCreatedAt": _format_datetime(self.first_created_at),
            "latestCreatedAt": _format_datetime(self.latest_created_at),
            "adapters": list(self.adapters),
            "statusCounts": dict(self.status_counts),
            "failureClassCounts": dict(self.failure_class_counts),
            "providerStatusCounts": dict(self.provider_status_counts),
            "providerCounts": dict(self.provider_counts),
            "capabilityCounts": dict(self.capability_counts),
        }


@dataclass(frozen=True)
class TraceSummary:
    operation: str | None
    total_count: int
    status_counts: Mapping[str, int]
    failure_class_counts: Mapping[str, int]
    provider_status_counts: Mapping[str, int]
    provider_counts: Mapping[str, int]
    capability_counts: Mapping[str, int]
    operations: tuple[TraceOperationSummary, ...]

    def to_dict(self) -> JSONObject:
        return {
            "operation": self.operation,
            "totalCount": self.total_count,
            "statusCounts": dict(self.status_counts),
            "failureClassCounts": dict(self.failure_class_counts),
            "providerStatusCounts": dict(self.provider_status_counts),
            "providerCounts": dict(self.provider_counts),
            "capabilityCounts": dict(self.capability_counts),
            "operations": [item.to_dict() for item in self.operations],
        }


@dataclass(frozen=True)
class TraceExportWarning:
    trace_id: str
    location: str
    reason: str

    def to_dict(self) -> JSONObject:
        return {
            "traceId": self.trace_id,
            "location": self.location,
            "reason": self.reason,
        }


@dataclass(frozen=True)
class TraceExportBundle:
    format_version: str
    exported_at: datetime
    operation: str | None
    limit: int
    trace_count: int
    warning_count: int
    content_hash: str
    warnings: tuple[TraceExportWarning, ...]
    traces: tuple[TraceRecord, ...]

    def to_dict(self) -> JSONObject:
        return {
            "formatVersion": self.format_version,
            "exportedAt": _format_datetime(self.exported_at),
            "operation": self.operation,
            "limit": self.limit,
            "traceCount": self.trace_count,
            "warningCount": self.warning_count,
            "contentHash": self.content_hash,
            "warnings": [item.to_dict() for item in self.warnings],
            "traces": [item.to_dict() for item in self.traces],
        }


@dataclass(frozen=True)
class TracePruneResult:
    operation: str | None
    older_than: datetime | None
    keep_latest: int | None
    limit: int
    dry_run: bool
    matched_count: int
    deleted_count: int
    matched_ids: tuple[str, ...]
    deleted_ids: tuple[str, ...]

    def to_dict(self) -> JSONObject:
        return {
            "operation": self.operation,
            "olderThan": (
                _format_datetime(self.older_than)
                if self.older_than is not None
                else None
            ),
            "keepLatest": self.keep_latest,
            "limit": self.limit,
            "dryRun": self.dry_run,
            "matchedCount": self.matched_count,
            "deletedCount": self.deleted_count,
            "matchedIds": list(self.matched_ids),
            "deletedIds": list(self.deleted_ids),
        }


@dataclass
class _MutableOperation:
    operation: str
    count: int = 0
    first_created_at: datetime | None = None
    latest_created_at: datetime | None = None
    adapters: set[str] = field(default_factory=set)
    status_counts: dict[str, int] = field(default_factory=dict)
    failure_class_counts: dict[str, int] = field(default_factory=dict)
    provider_status_counts: dict[str, int] = field(default_factory=dict)
    provider_counts: dict[str, int] = field(default_factory=dict)
    capability_counts: dict[str, int] = field(default_factory=dict)


class SQLiteTraceStore:
    def __init__(
        self,
        database_path: str | Path,
        *,
        clock: Callable[[], datetime] | None = None,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.database_path = str(database_path)
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None
        self._initialize()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(
            self.database_path,
            timeout=30,
            isolation_level=None,
        )
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA busy_timeout=30000")
        return connection

    def _initialize(self) -> None:
        with self._connect() as connection:
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS vyral_py_traces (
                    id TEXT PRIMARY KEY,
                    operation TEXT NOT NULL,
                    adapter TEXT NULL,
                    request_json TEXT NOT NULL,
                    result_summary_json TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    duration_ms REAL NOT NULL,
                    created_at TEXT NOT NULL
                )
                """
            )
            connection.execute(
                """
                CREATE INDEX IF NOT EXISTS ix_vyral_py_traces_operation_created
                ON vyral_py_traces(operation, created_at DESC, id ASC)
                """
            )

    def write_trace(self, trace: TraceRecord) -> None:
        if not trace.id.strip():
            raise ValueError("Trace id is required.")
        if not trace.operation.strip():
            raise ValueError("Trace operation is required.")
        if not math.isfinite(trace.duration_ms):
            raise ValueError("Trace durationMs must be finite.")
        started_at = _utc(trace.started_at, "Trace startedAt")
        created_at = _utc(trace.created_at, "Trace createdAt")
        with self._connect() as connection:
            try:
                connection.execute(
                    """
                    INSERT INTO vyral_py_traces(
                        id, operation, adapter, request_json,
                        result_summary_json, started_at, duration_ms, created_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        trace.id,
                        trace.operation,
                        trace.adapter,
                        _json(trace.request),
                        _json(trace.result_summary),
                        _format_datetime(started_at),
                        trace.duration_ms,
                        _format_datetime(created_at),
                    ),
                )
            except sqlite3.IntegrityError as exc:
                raise ValueError(f"Trace id {trace.id!r} already exists.") from exc

    async def awrite_trace(self, trace: TraceRecord) -> None:
        await self.executor.run(lambda: self.write_trace(trace))

    def get_trace(self, trace_id: str) -> TraceRecord | None:
        if not trace_id.strip():
            raise ValueError("Trace id is required.")
        with self._connect() as connection:
            row = connection.execute(
                """
                SELECT id, operation, adapter, request_json,
                       result_summary_json, started_at, duration_ms, created_at
                FROM vyral_py_traces WHERE id=?
                """,
                (trace_id,),
            ).fetchone()
        return _read_trace(row) if row is not None else None

    async def aget_trace(self, trace_id: str) -> TraceRecord | None:
        return await self.executor.run(lambda: self.get_trace(trace_id))

    def list_traces(
        self,
        operation: str | None = None,
        limit: int | None = None,
    ) -> tuple[TraceRecord, ...]:
        selected = _limit(
            limit,
            DEFAULT_TRACE_LIST_LIMIT,
            MAX_TRACE_LIST_LIMIT,
            "Trace list limit",
        )
        normalized = operation.strip() if operation and operation.strip() else None
        with self._connect() as connection:
            rows = connection.execute(
                """
                SELECT id, operation, adapter, request_json,
                       result_summary_json, started_at, duration_ms, created_at
                FROM vyral_py_traces
                WHERE (? IS NULL OR operation=?)
                ORDER BY created_at DESC, id ASC
                LIMIT ?
                """,
                (normalized, normalized, selected),
            ).fetchall()
        return tuple(_read_trace(row) for row in rows)

    async def alist_traces(
        self,
        operation: str | None = None,
        limit: int | None = None,
    ) -> tuple[TraceRecord, ...]:
        return await self.executor.run(lambda: self.list_traces(operation, limit))

    def summarize_traces(self, operation: str | None = None) -> TraceSummary:
        normalized = operation.strip() if operation and operation.strip() else None
        with self._connect() as connection:
            rows = connection.execute(
                """
                SELECT id, operation, adapter, request_json,
                       result_summary_json, started_at, duration_ms, created_at
                FROM vyral_py_traces
                WHERE (? IS NULL OR operation=?)
                ORDER BY operation ASC, created_at ASC, id ASC
                """,
                (normalized, normalized),
            ).fetchall()
        totals: dict[str, dict[str, int]] = {
            "status": {},
            "failure": {},
            "provider_status": {},
            "provider": {},
            "capability": {},
        }
        operations: dict[str, _MutableOperation] = {}
        for row in rows:
            trace = _read_trace(row)
            item = operations.setdefault(
                trace.operation,
                _MutableOperation(trace.operation),
            )
            item.count += 1
            item.first_created_at = (
                trace.created_at
                if item.first_created_at is None
                else min(item.first_created_at, trace.created_at)
            )
            item.latest_created_at = (
                trace.created_at
                if item.latest_created_at is None
                else max(item.latest_created_at, trace.created_at)
            )
            if trace.adapter and trace.adapter.strip():
                item.adapters.add(trace.adapter)
            values = (
                ("status", trace.result_summary.get("status")),
                ("failure", trace.result_summary.get("failureClass")),
                (
                    "provider_status",
                    trace.result_summary.get("providerStatus"),
                ),
                (
                    "provider",
                    trace.request.get("provider")
                    or trace.result_summary.get("provider"),
                ),
                (
                    "capability",
                    trace.request.get("capability")
                    or trace.result_summary.get("capability"),
                ),
            )
            for kind, raw in values:
                value = _summary_scalar(raw)
                if value is None:
                    continue
                _increment(totals[kind], value)
                target = {
                    "status": item.status_counts,
                    "failure": item.failure_class_counts,
                    "provider_status": item.provider_status_counts,
                    "provider": item.provider_counts,
                    "capability": item.capability_counts,
                }[kind]
                _increment(target, value)
        summaries: list[TraceOperationSummary] = []
        for item in sorted(operations.values(), key=lambda value: value.operation):
            assert item.first_created_at is not None
            assert item.latest_created_at is not None
            summaries.append(
                TraceOperationSummary(
                    operation=item.operation,
                    count=item.count,
                    first_created_at=item.first_created_at,
                    latest_created_at=item.latest_created_at,
                    adapters=tuple(sorted(item.adapters)),
                    status_counts=_sorted_counts(item.status_counts),
                    failure_class_counts=_sorted_counts(
                        item.failure_class_counts
                    ),
                    provider_status_counts=_sorted_counts(
                        item.provider_status_counts
                    ),
                    provider_counts=_sorted_counts(item.provider_counts),
                    capability_counts=_sorted_counts(item.capability_counts),
                )
            )
        return TraceSummary(
            operation=normalized,
            total_count=len(rows),
            status_counts=_sorted_counts(totals["status"]),
            failure_class_counts=_sorted_counts(totals["failure"]),
            provider_status_counts=_sorted_counts(totals["provider_status"]),
            provider_counts=_sorted_counts(totals["provider"]),
            capability_counts=_sorted_counts(totals["capability"]),
            operations=tuple(summaries),
        )

    async def asummarize_traces(
        self,
        operation: str | None = None,
    ) -> TraceSummary:
        return await self.executor.run(lambda: self.summarize_traces(operation))

    def export_traces(self, request: TraceExportRequest) -> TraceExportBundle:
        limit = _limit(
            request.limit,
            DEFAULT_TRACE_EXPORT_LIMIT,
            MAX_TRACE_EXPORT_LIMIT,
            "Trace export limit",
        )
        operation = (
            request.operation.strip()
            if request.operation and request.operation.strip()
            else None
        )
        traces = self.list_traces(operation, limit)
        warnings = tuple(
            warning
            for trace in traces
            for warning in _unsafe_warnings(trace)
        )
        if request.fail_on_unsafe_content and warnings:
            raise ValueError(
                f"Trace export detected {len(warnings)} potentially unsafe "
                "trace field(s)."
            )
        exported_at = _utc(self._clock(), "Trace export clock")
        payload = {
            "formatVersion": "vyral.trace-export.v1",
            "operation": operation,
            "limit": limit,
            "traceCount": len(traces),
            "warningCount": len(warnings),
            "warnings": [item.to_dict() for item in warnings],
            "traces": [item.to_dict() for item in traces],
        }
        content_hash = "sha256:" + sha256(
            _json(payload).encode("utf-8")
        ).hexdigest()
        return TraceExportBundle(
            format_version="vyral.trace-export.v1",
            exported_at=exported_at,
            operation=operation,
            limit=limit,
            trace_count=len(traces),
            warning_count=len(warnings),
            content_hash=content_hash,
            warnings=warnings,
            traces=traces,
        )

    async def aexport_traces(
        self,
        request: TraceExportRequest,
    ) -> TraceExportBundle:
        return await self.executor.run(lambda: self.export_traces(request))

    def prune_traces(self, request: TracePruneRequest) -> TracePruneResult:
        if request.keep_latest is not None and request.keep_latest < 0:
            raise ValueError("Trace prune keepLatest must be non-negative.")
        limit = _limit(
            request.limit,
            DEFAULT_TRACE_PRUNE_LIMIT,
            MAX_TRACE_PRUNE_LIMIT,
            "Trace prune limit",
        )
        operation = (
            request.operation.strip()
            if request.operation and request.operation.strip()
            else None
        )
        older_than = (
            _utc(request.older_than, "Trace prune olderThan")
            if request.older_than is not None
            else None
        )
        if (
            operation is None
            and older_than is None
            and request.keep_latest is None
        ):
            raise ValueError(
                "Trace prune requires at least one constraint: operation, "
                "olderThan, or keepLatest."
            )
        with self._connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            rows = connection.execute(
                """
                SELECT id FROM vyral_py_traces
                WHERE (? IS NULL OR operation=?)
                  AND (? IS NULL OR created_at < ?)
                ORDER BY created_at DESC, id ASC
                """,
                (
                    operation,
                    operation,
                    _format_datetime(older_than) if older_than else None,
                    _format_datetime(older_than) if older_than else None,
                ),
            ).fetchall()
            keep = request.keep_latest or 0
            ids = tuple(str(row["id"]) for row in rows[keep : keep + limit])
            if not request.dry_run:
                connection.executemany(
                    "DELETE FROM vyral_py_traces WHERE id=?",
                    ((trace_id,) for trace_id in ids),
                )
            connection.execute("COMMIT")
        deleted = () if request.dry_run else ids
        return TracePruneResult(
            operation=operation,
            older_than=older_than,
            keep_latest=request.keep_latest,
            limit=limit,
            dry_run=request.dry_run,
            matched_count=len(ids),
            deleted_count=len(deleted),
            matched_ids=ids,
            deleted_ids=deleted,
        )

    async def aprune_traces(
        self,
        request: TracePruneRequest,
    ) -> TracePruneResult:
        return await self.executor.run(lambda: self.prune_traces(request))

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()


def _read_trace(row: sqlite3.Row) -> TraceRecord:
    request = json.loads(str(row["request_json"]))
    summary = json.loads(str(row["result_summary_json"]))
    if not isinstance(request, dict) or not isinstance(summary, dict):
        raise ValueError("Stored trace JSON must contain objects.")
    return TraceRecord(
        id=str(row["id"]),
        operation=str(row["operation"]),
        adapter=str(row["adapter"]) if row["adapter"] is not None else None,
        request=request,
        result_summary=summary,
        started_at=_parse_datetime(str(row["started_at"])),
        duration_ms=float(row["duration_ms"]),
        created_at=_parse_datetime(str(row["created_at"])),
    )


def _limit(
    value: int | None,
    default: int,
    maximum: int,
    description: str,
) -> int:
    if value is not None and value <= 0:
        raise ValueError(f"{description} must be greater than zero.")
    selected = value if value is not None else default
    if selected > maximum:
        raise ValueError(f"{description} cannot exceed {maximum}.")
    return selected


def _summary_scalar(value: object) -> str | None:
    if value is None or isinstance(value, (dict, list, tuple)):
        return None
    if isinstance(value, bool):
        text = "true" if value else "false"
    else:
        text = str(value).strip()
    if not text:
        return None
    return text[:MAX_TRACE_SUMMARY_VALUE_LENGTH]


def _increment(counts: dict[str, int], value: str) -> None:
    selected = value
    if (
        selected not in counts
        and len(counts) >= MAX_TRACE_SUMMARY_DISTINCT_VALUES
    ):
        selected = "_other"
    counts[selected] = counts.get(selected, 0) + 1


def _sorted_counts(values: Mapping[str, int]) -> Mapping[str, int]:
    return dict(sorted(values.items(), key=lambda item: (-item[1], item[0])))


def _unsafe_warnings(trace: TraceRecord) -> tuple[TraceExportWarning, ...]:
    output: list[TraceExportWarning] = []
    _scan_unsafe(trace.id, "request", trace.request, output)
    _scan_unsafe(trace.id, "resultSummary", trace.result_summary, output)
    return tuple(output)


def _scan_unsafe(
    trace_id: str,
    location: str,
    value: object,
    output: list[TraceExportWarning],
) -> None:
    if isinstance(value, Mapping):
        for key, item in value.items():
            child = location + "." + str(key)
            if _sensitive_name(str(key)):
                output.append(
                    TraceExportWarning(
                        trace_id,
                        child,
                        "sensitive_field_name",
                    )
                )
            _scan_unsafe(trace_id, child, item, output)
    elif isinstance(value, (list, tuple)):
        for index, item in enumerate(value):
            _scan_unsafe(trace_id, f"{location}[{index}]", item, output)
    elif isinstance(value, str) and value.lower().startswith("bearer "):
        output.append(
            TraceExportWarning(
                trace_id,
                location,
                "bearer_token_value",
            )
        )


def _sensitive_name(value: str) -> bool:
    normalized = value.replace("_", "").replace("-", "").lower()
    return normalized in {
        "apikey",
        "xapikey",
        "authorization",
        "password",
        "passwd",
        "secret",
        "clientsecret",
        "accesstoken",
        "refreshtoken",
        "privatekey",
        "credential",
        "credentials",
        "token",
        "authtoken",
    }


def _json(value: object) -> str:
    return json.dumps(
        value,
        ensure_ascii=True,
        separators=(",", ":"),
        sort_keys=False,
        allow_nan=False,
    )


def _format_datetime(value: datetime) -> str:
    normalized = _utc(value, "datetime")
    base = normalized.strftime("%Y-%m-%dT%H:%M:%S")
    fraction = f"{normalized.microsecond:06d}".rstrip("0")
    return base + (f".{fraction}" if fraction else "") + "Z"


def _parse_datetime(value: str) -> datetime:
    parsed = parse_iso_datetime(value)
    return _utc(parsed, "stored trace datetime")


def _utc(value: datetime, name: str) -> datetime:
    if value.tzinfo is None:
        raise ValueError(f"{name} must include a UTC offset.")
    return value.astimezone(timezone.utc)
