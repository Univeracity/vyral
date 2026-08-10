from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Mapping

from ..contracts import JSONValue
from ..local.models import JSONObject
from .models import (
    MAX_ARTIFACT_BYTES,
    MAX_ARTIFACT_INLINE_BYTES,
    MAX_LEASE_TTL_SECONDS,
    MAX_PAYLOAD_BYTES,
    MAX_RESULT_BYTES,
    MAX_RETRY_ATTEMPTS,
    MAX_STATUS_DETAILS_BYTES,
    MAX_TRACE_DETAILS_BYTES,
    MAX_TRACE_MESSAGE_CHARS,
    RUN_STATUSES,
    ExecutionModel,
    ExecutionScope,
    _boolean,
    _date,
    _integer,
    _json_object,
    _json_value,
    _mapping,
    _number,
    _optional_date,
    _optional_integer,
    _optional_text,
    _required_id,
    _string_map,
    _text,
    _validate_tags,
)


def _text_set(value: object, name: str) -> frozenset[str]:
    if value is None:
        return frozenset()
    if not isinstance(value, (list, tuple, set, frozenset)) or not all(
        isinstance(item, str) and item.strip() for item in value
    ):
        raise TypeError(f"{name} must be an array of non-empty strings.")
    return frozenset(item.strip() for item in value)


@dataclass(frozen=True)
class ExecutionProductPolicy(ExecutionModel):
    product_id: str
    allowed_handler_ids: frozenset[str] = frozenset()
    allowed_tenant_ids: frozenset[str] = frozenset()
    allowed_service_identities: frozenset[str] = frozenset()
    max_payload_bytes: int | None = None
    artifact_prefix: str | None = None
    redacted_json_property_names: frozenset[str] = frozenset()

    def __post_init__(self) -> None:
        product_id = _required_id(self.product_id, "Product id")
        if self.max_payload_bytes is not None and (
            isinstance(self.max_payload_bytes, bool)
            or self.max_payload_bytes <= 0
            or self.max_payload_bytes > MAX_PAYLOAD_BYTES
        ):
            raise ValueError(
                "Product max_payload_bytes must be between one and "
                f"{MAX_PAYLOAD_BYTES}."
            )
        prefix = (
            self.artifact_prefix.strip()
            if self.artifact_prefix is not None
            else None
        )
        if prefix == "":
            prefix = None
        object.__setattr__(self, "product_id", product_id)
        object.__setattr__(
            self,
            "allowed_handler_ids",
            _text_set(self.allowed_handler_ids, "allowed_handler_ids"),
        )
        object.__setattr__(
            self,
            "allowed_tenant_ids",
            _text_set(self.allowed_tenant_ids, "allowed_tenant_ids"),
        )
        object.__setattr__(
            self,
            "allowed_service_identities",
            _text_set(
                self.allowed_service_identities,
                "allowed_service_identities",
            ),
        )
        object.__setattr__(self, "artifact_prefix", prefix)
        object.__setattr__(
            self,
            "redacted_json_property_names",
            _text_set(
                self.redacted_json_property_names,
                "redacted_json_property_names",
            ),
        )

    @classmethod
    def from_value(
        cls,
        value: ExecutionProductPolicy | Mapping[str, Any],
    ) -> ExecutionProductPolicy:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution product policy")
        return cls(
            product_id=_text(item.get("productId"), "productId"),
            allowed_handler_ids=_text_set(
                item.get("allowedHandlerIds"), "allowedHandlerIds"
            ),
            allowed_tenant_ids=_text_set(
                item.get("allowedTenantIds"), "allowedTenantIds"
            ),
            allowed_service_identities=_text_set(
                item.get("allowedServiceIdentities"),
                "allowedServiceIdentities",
            ),
            max_payload_bytes=_optional_integer(
                item.get("maxPayloadBytes"), "maxPayloadBytes"
            ),
            artifact_prefix=_optional_text(
                item.get("artifactPrefix"), "artifactPrefix"
            ),
            redacted_json_property_names=_text_set(
                item.get("redactedJsonPropertyNames"),
                "redactedJsonPropertyNames",
            ),
        )


@dataclass(frozen=True)
class ExecutionRuntimeAdapterDescriptor(ExecutionModel):
    adapter_id: str
    runtime_kind: str
    display_name: str
    version: str
    capabilities: tuple[str, ...] = ()
    metadata: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ExecutionOperationalPolicy(ExecutionModel):
    max_active_runs: int
    max_retained_terminal_runs: int | None
    default_list_limit: int
    max_list_limit: int
    default_history_limit: int
    max_history_limit: int
    max_payload_bytes: int = MAX_PAYLOAD_BYTES
    max_result_bytes: int = MAX_RESULT_BYTES
    max_status_details_bytes: int = MAX_STATUS_DETAILS_BYTES
    max_artifact_bytes: int = MAX_ARTIFACT_BYTES
    max_artifact_inline_bytes: int = MAX_ARTIFACT_INLINE_BYTES
    max_trace_message_chars: int = MAX_TRACE_MESSAGE_CHARS
    max_trace_details_bytes: int = MAX_TRACE_DETAILS_BYTES
    max_retry_attempts: int = MAX_RETRY_ATTEMPTS
    max_retry_delay_seconds: float = MAX_LEASE_TTL_SECONDS
    max_lease_ttl_seconds: float = MAX_LEASE_TTL_SECONDS
    concurrency_key_policy: str = "serialize_running_runs"
    concurrency_retry_delay_ms: int | None = 100
    default_trace_severity: str = "info"
    retention_scope: str = "run_owned"


@dataclass(frozen=True)
class ExecutionResumePolicy(ExecutionModel):
    mode: str = "restart_recovery"
    interrupted_running_behavior: str = "may_reexecute_handler"
    scheduled_waiting_behavior: str = "dispatch_when_due"
    terminal_behavior: str = "never_resume"
    plugin_checkpoint_behavior: str = "plugin_owned"
    idempotency_scope: str = "handler_plugin_payload"
    creates_linked_follow_up_runs: bool = False


@dataclass(frozen=True)
class ExecutionRuntimeAdapterStatus(ExecutionModel):
    adapter: ExecutionRuntimeAdapterDescriptor
    available: bool
    status: str
    checked_at_utc: datetime
    active_runs: int | None = None
    operational_policy: ExecutionOperationalPolicy | None = None
    resume_policy: ExecutionResumePolicy | None = None
    details: JSONObject | None = None


@dataclass(frozen=True)
class ExecutionRetryPolicy(ExecutionModel):
    max_attempts: int = 1
    initial_delay_seconds: float = 1.0
    max_delay_seconds: float = 60.0
    backoff_multiplier: float = 2.0

    def __post_init__(self) -> None:
        if not 1 <= self.max_attempts <= MAX_RETRY_ATTEMPTS:
            raise ValueError(
                f"Retry maxAttempts must be between 1 and "
                f"{MAX_RETRY_ATTEMPTS}."
            )
        if self.initial_delay_seconds < 0:
            raise ValueError(
                "Retry initialDelaySeconds must be non-negative."
            )
        if not (
            self.initial_delay_seconds
            <= self.max_delay_seconds
            <= MAX_LEASE_TTL_SECONDS
        ):
            raise ValueError(
                "Retry maxDelaySeconds must be at least the initial "
                f"delay and no more than {MAX_LEASE_TTL_SECONDS:g}."
            )
        if self.backoff_multiplier <= 0:
            raise ValueError(
                "Retry backoffMultiplier must be greater than zero."
            )

    @classmethod
    def from_value(
        cls,
        value: ExecutionRetryPolicy | Mapping[str, Any] | None,
    ) -> ExecutionRetryPolicy:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution retry policy")
        return cls(
            max_attempts=_integer(
                item.get("maxAttempts"), "maxAttempts", 1
            ),
            initial_delay_seconds=_number(
                item.get("initialDelaySeconds"),
                "initialDelaySeconds",
                1.0,
            ),
            max_delay_seconds=_number(
                item.get("maxDelaySeconds"),
                "maxDelaySeconds",
                60.0,
            ),
            backoff_multiplier=_number(
                item.get("backoffMultiplier"),
                "backoffMultiplier",
                2.0,
            ),
        )


@dataclass(frozen=True)
class ExecutionRunRequest(ExecutionModel):
    handler_id: str
    plugin_id: str | None = None
    payload: JSONValue = None
    idempotency_key: str | None = None
    correlation_id: str | None = None
    scope: ExecutionScope | None = None
    scheduled_at_utc: datetime | None = None
    retry_policy: ExecutionRetryPolicy = ExecutionRetryPolicy()
    tags: Mapping[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        object.__setattr__(
            self,
            "handler_id",
            _required_id(self.handler_id, "Handler id"),
        )
        if self.plugin_id is not None:
            object.__setattr__(
                self,
                "plugin_id",
                _required_id(self.plugin_id, "Plugin id"),
            )
        if self.idempotency_key is not None:
            object.__setattr__(
                self,
                "idempotency_key",
                _required_id(
                    self.idempotency_key, "Idempotency key"
                ),
            )
        if self.correlation_id is not None:
            object.__setattr__(
                self,
                "correlation_id",
                _required_id(
                    self.correlation_id, "Correlation id"
                ),
            )
        object.__setattr__(
            self, "payload", _json_value(self.payload, "run payload")
        )
        object.__setattr__(
            self,
            "retry_policy",
            ExecutionRetryPolicy.from_value(self.retry_policy),
        )
        object.__setattr__(
            self, "tags", _validate_tags(dict(self.tags))
        )
        if self.scheduled_at_utc is not None:
            object.__setattr__(
                self,
                "scheduled_at_utc",
                _date(self.scheduled_at_utc, "scheduledAtUtc"),
            )

    @classmethod
    def from_value(
        cls, value: ExecutionRunRequest | Mapping[str, Any]
    ) -> ExecutionRunRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution run request")
        raw_scope = item.get("scope")
        return cls(
            handler_id=_text(item.get("handlerId"), "handlerId"),
            plugin_id=_optional_text(
                item.get("pluginId"), "pluginId"
            ),
            payload=_json_value(item.get("payload"), "run payload"),
            idempotency_key=_optional_text(
                item.get("idempotencyKey"), "idempotencyKey"
            ),
            correlation_id=_optional_text(
                item.get("correlationId"), "correlationId"
            ),
            scope=ExecutionScope.from_value(
                raw_scope if isinstance(raw_scope, Mapping) else None
            ),
            scheduled_at_utc=_optional_date(
                item.get("scheduledAtUtc"), "scheduledAtUtc"
            ),
            retry_policy=ExecutionRetryPolicy.from_value(
                item.get("retryPolicy")
                if isinstance(item.get("retryPolicy"), Mapping)
                else None
            ),
            tags=_string_map(item.get("tags"), "tags"),
        )


@dataclass(frozen=True)
class ExecutionTraceEvent(ExecutionModel):
    id: str
    sequence_id: str
    run_id: str
    type: str
    timestamp_utc: datetime
    attempt: int
    severity: str = "info"
    step_id: str | None = None
    status: str | None = None
    message: str | None = None
    details: JSONObject | None = None
    context: Mapping[str, str] = field(default_factory=dict)

    @classmethod
    def from_value(
        cls, value: ExecutionTraceEvent | Mapping[str, Any]
    ) -> ExecutionTraceEvent:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution trace event")
        return cls(
            id=_text(item.get("id"), "event id"),
            sequence_id=_text(
                item.get("sequenceId"), "sequenceId"
            ),
            run_id=_text(item.get("runId"), "runId"),
            type=_text(item.get("type"), "event type"),
            timestamp_utc=_date(
                item.get("timestampUtc"), "timestampUtc"
            ),
            attempt=_integer(item.get("attempt"), "attempt"),
            step_id=_optional_text(item.get("stepId"), "stepId"),
            status=_optional_text(item.get("status"), "status"),
            severity=_text(
                item.get("severity"), "severity", "info"
            ),
            message=_optional_text(item.get("message"), "message"),
            details=_json_object(item.get("details"), "details"),
            context=_string_map(item.get("context"), "context"),
        )


@dataclass(frozen=True)
class ExecutionRunQuery:
    handler_id: str | None = None
    plugin_id: str | None = None
    status: str | None = None
    correlation_id: str | None = None
    idempotency_key: str | None = None
    created_after_utc: datetime | None = None
    created_before_utc: datetime | None = None
    updated_after_utc: datetime | None = None
    updated_before_utc: datetime | None = None
    tags: Mapping[str, str] = field(default_factory=dict)
    include_result: bool = True
    limit: int | None = None

    def __post_init__(self) -> None:
        if self.status is not None and self.status not in RUN_STATUSES:
            raise ValueError(f"Unknown execution status {self.status!r}.")
        if self.limit is not None and self.limit <= 0:
            raise ValueError("Run query limit must be greater than zero.")
        object.__setattr__(self, "tags", dict(self.tags))

    @classmethod
    def from_value(
        cls, value: ExecutionRunQuery | Mapping[str, Any] | None
    ) -> ExecutionRunQuery:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution run query")
        return cls(
            handler_id=_optional_text(
                item.get("handlerId"), "handlerId"
            ),
            plugin_id=_optional_text(
                item.get("pluginId"), "pluginId"
            ),
            status=_optional_text(item.get("status"), "status"),
            correlation_id=_optional_text(
                item.get("correlationId"), "correlationId"
            ),
            idempotency_key=_optional_text(
                item.get("idempotencyKey"), "idempotencyKey"
            ),
            created_after_utc=_optional_date(
                item.get("createdAfterUtc"), "createdAfterUtc"
            ),
            created_before_utc=_optional_date(
                item.get("createdBeforeUtc"), "createdBeforeUtc"
            ),
            updated_after_utc=_optional_date(
                item.get("updatedAfterUtc"), "updatedAfterUtc"
            ),
            updated_before_utc=_optional_date(
                item.get("updatedBeforeUtc"), "updatedBeforeUtc"
            ),
            tags=_string_map(item.get("tags"), "tags"),
            include_result=_boolean(
                item.get("includeResult"), "includeResult", True
            ),
            limit=_optional_integer(item.get("limit"), "limit"),
        )


@dataclass(frozen=True)
class ExecutionHistoryQuery:
    limit: int | None = None

    def __post_init__(self) -> None:
        if self.limit is not None and self.limit <= 0:
            raise ValueError("History limit must be greater than zero.")


@dataclass(frozen=True)
class ExecutionLeaseRequest(ExecutionModel):
    lease_key: str
    owner_id: str
    run_id: str | None = None
    ttl_seconds: float = 60.0
    metadata: JSONObject | None = None

    def __post_init__(self) -> None:
        object.__setattr__(
            self,
            "lease_key",
            _required_id(self.lease_key, "Lease key"),
        )
        object.__setattr__(
            self,
            "owner_id",
            _required_id(self.owner_id, "Lease owner id"),
        )
        if not 0 < self.ttl_seconds <= MAX_LEASE_TTL_SECONDS:
            raise ValueError(
                "Lease ttlSeconds must be positive and within the "
                "portable ceiling."
            )


@dataclass(frozen=True)
class ExecutionLease(ExecutionModel):
    lease_key: str
    owner_id: str
    acquired_at_utc: datetime
    expires_at_utc: datetime
    run_id: str | None = None
    metadata: JSONObject | None = None

    @classmethod
    def from_value(
        cls, value: ExecutionLease | Mapping[str, Any]
    ) -> ExecutionLease:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution lease")
        return cls(
            lease_key=_text(item.get("leaseKey"), "leaseKey"),
            owner_id=_text(item.get("ownerId"), "ownerId"),
            run_id=_optional_text(item.get("runId"), "runId"),
            acquired_at_utc=_date(
                item.get("acquiredAtUtc"), "acquiredAtUtc"
            ),
            expires_at_utc=_date(
                item.get("expiresAtUtc"), "expiresAtUtc"
            ),
            metadata=_json_object(item.get("metadata"), "metadata"),
        )


@dataclass(frozen=True)
class ExecutionTimerRequest(ExecutionModel):
    name: str
    fire_at_utc: datetime
    run_id: str | None = None
    payload: JSONValue = None

    def __post_init__(self) -> None:
        object.__setattr__(
            self, "name", _required_id(self.name, "Timer name")
        )
        object.__setattr__(
            self,
            "fire_at_utc",
            _date(self.fire_at_utc, "fireAtUtc"),
        )
        object.__setattr__(
            self, "payload", _json_value(self.payload, "timer payload")
        )


@dataclass(frozen=True)
class ExecutionExternalEventRequest(ExecutionModel):
    name: str
    run_id: str | None = None
    payload: JSONValue = None

    def __post_init__(self) -> None:
        object.__setattr__(
            self,
            "name",
            _required_id(self.name, "External event name"),
        )
        object.__setattr__(
            self,
            "payload",
            _json_value(self.payload, "external event payload"),
        )


@dataclass(frozen=True)
class ExecutionMaintenancePruneRequest:
    dry_run: bool = True
    retain_terminal_runs: int | None = None

    def __post_init__(self) -> None:
        if (
            self.retain_terminal_runs is not None
            and self.retain_terminal_runs < 0
        ):
            raise ValueError(
                "retainTerminalRuns must be non-negative."
            )


@dataclass(frozen=True)
class ExecutionMaintenancePruneResult(ExecutionModel):
    dry_run: bool
    retain_terminal_runs: int
    pruned_at_utc: datetime
    run_ids: tuple[str, ...]
    runs: int
    events: int
    artifacts: int
    checkpoints: int
    timers: int
    external_events: int
    leases: int
    artifact_directories: int = 0


@dataclass(frozen=True)
class ExecutionMaintenanceDispatchReconcileRequest:
    dry_run: bool = False
    limit: int | None = None

    def __post_init__(self) -> None:
        if self.limit is not None and self.limit <= 0:
            raise ValueError("Reconciliation limit must be positive.")


@dataclass(frozen=True)
class ExecutionMaintenanceDispatchReconcileResult(ExecutionModel):
    dry_run: bool
    limit: int
    reconciled_at_utc: datetime
    candidate_run_ids: tuple[str, ...]
    dispatched: int
    failures: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ExecutionMaintenanceStatus(ExecutionModel):
    adapter_id: str
    runtime_kind: str
    checked_at_utc: datetime
    retention_scope: str
    max_retained_terminal_runs: int
    run_counts: Mapping[str, int]
    row_counts: Mapping[str, int]
    artifact_directory: str | None = None
    artifact_directory_count: int | None = None
    artifact_file_count: int | None = None
    artifact_bytes: int | None = None


__all__ = [
    "ExecutionExternalEventRequest",
    "ExecutionHistoryQuery",
    "ExecutionLease",
    "ExecutionLeaseRequest",
    "ExecutionMaintenanceDispatchReconcileRequest",
    "ExecutionMaintenanceDispatchReconcileResult",
    "ExecutionMaintenancePruneRequest",
    "ExecutionMaintenancePruneResult",
    "ExecutionMaintenanceStatus",
    "ExecutionOperationalPolicy",
    "ExecutionProductPolicy",
    "ExecutionResumePolicy",
    "ExecutionRetryPolicy",
    "ExecutionRuntimeAdapterDescriptor",
    "ExecutionRuntimeAdapterStatus",
    "ExecutionRunQuery",
    "ExecutionRunRequest",
    "ExecutionTimerRequest",
    "ExecutionTraceEvent",
]
