from __future__ import annotations

from dataclasses import dataclass, field, fields, is_dataclass
from datetime import datetime, timezone
import json
import math
from typing import Any, Mapping, cast

from .._datetime import parse_iso_datetime
from ..admission import create_admission_receipt
from ..contracts import JSONValue
from ..local.models import JSONObject


MAX_ID_CHARS = 160
MAX_DISPLAY_NAME_CHARS = 200
MAX_DESCRIPTION_CHARS = 1_000
MAX_PAYLOAD_BYTES = 1_048_576
MAX_RESULT_BYTES = 1_048_576
MAX_STATUS_DETAILS_BYTES = 65_536
MAX_TRACE_MESSAGE_CHARS = 4_096
MAX_TRACE_DETAILS_BYTES = 65_536
MAX_ARTIFACT_BYTES = 16_777_216
MAX_ARTIFACT_INLINE_BYTES = 1_048_576
MAX_ARTIFACT_NAME_CHARS = 160
MAX_CHECKPOINT_BYTES = 1_048_576
MAX_CHECKPOINT_KEY_CHARS = 160
MAX_TAG_COUNT = 32
MAX_TAG_KEY_CHARS = 80
MAX_TAG_VALUE_CHARS = 512
MAX_RETRY_ATTEMPTS = 25
MAX_LEASE_TTL_SECONDS = 86_400.0

RUN_STATUSES = frozenset(
    {
        "queued",
        "waiting",
        "running",
        "succeeded",
        "failed",
        "cancelled",
        "rejected",
        "timed_out",
    }
)
TERMINAL_RUN_STATUSES = frozenset(
    {"succeeded", "failed", "cancelled", "rejected", "timed_out"}
)
FAILURE_CLASSES = frozenset(
    {
        "cancelled",
        "handler_missing",
        "plugin_mismatch",
        "idempotency_conflict",
        "queue_full",
        "timeout",
        "transient",
        "validation",
        "platform",
        "unknown",
    }
)
ARTIFACT_KINDS = frozenset({"json", "text", "object_reference"})
WAIT_KINDS = frozenset({"external_event", "timer"})
WAIT_OUTCOMES = frozenset({"external_event", "timer", "timed_out"})


class ExecutionModel:
    def to_dict(self) -> JSONObject:
        return cast(JSONObject, _wire(self))


def _wire(value: Any) -> Any:
    if is_dataclass(value) and not isinstance(value, type):
        return {
            _camel(item.name): _wire(getattr(value, item.name))
            for item in fields(value)
            if item.metadata.get("wire", True)
        }
    if isinstance(value, datetime):
        return _format_date(value)
    if isinstance(value, Mapping):
        return {str(key): _wire(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_wire(item) for item in value]
    return value


def _camel(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(item[:1].upper() + item[1:] for item in tail)


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TypeError(f"{name} must be an object.")
    return value


def _text(
    value: object,
    name: str,
    default: str | None = None,
) -> str:
    if value is None and default is not None:
        return default
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string.")
    return value


def _optional_text(value: object, name: str) -> str | None:
    return None if value is None else _text(value, name)


def _integer(value: object, name: str, default: int = 0) -> int:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError(f"{name} must be an integer.")
    return value


def _optional_integer(value: object, name: str) -> int | None:
    return None if value is None else _integer(value, name)


def _number(value: object, name: str, default: float = 0.0) -> float:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(f"{name} must be a number.")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError(f"{name} must be finite.")
    return result


def _optional_number(value: object, name: str) -> float | None:
    return None if value is None else _number(value, name)


def _boolean(value: object, name: str, default: bool = False) -> bool:
    if value is None:
        return default
    if not isinstance(value, bool):
        raise TypeError(f"{name} must be a boolean.")
    return value


def _date(value: object, name: str) -> datetime:
    if isinstance(value, datetime):
        selected = value
    elif isinstance(value, str):
        try:
            selected = parse_iso_datetime(value)
        except ValueError as exc:
            raise ValueError(f"{name} must be an ISO timestamp.") from exc
    else:
        raise TypeError(f"{name} must be an ISO timestamp.")
    if selected.tzinfo is None:
        raise ValueError(f"{name} must include an offset.")
    return selected.astimezone(timezone.utc)


def _optional_date(value: object, name: str) -> datetime | None:
    return None if value is None else _date(value, name)


def _format_date(value: datetime) -> str:
    if value.tzinfo is None:
        raise ValueError("Execution timestamps must include an offset.")
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _string_map(value: object, name: str) -> Mapping[str, str]:
    if value is None:
        return {}
    item = _mapping(value, name)
    if not all(isinstance(key, str) and isinstance(entry, str) for key, entry in item.items()):
        raise TypeError(f"{name} must contain string keys and values.")
    return {str(key): str(entry) for key, entry in item.items()}


def _json_object(value: object, name: str) -> JSONObject | None:
    if value is None:
        return None
    item = _mapping(value, name)
    return cast(JSONObject, {str(key): entry for key, entry in item.items()})


def _json_value(value: object, name: str) -> JSONValue:
    try:
        encoded = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise TypeError(f"{name} must be a JSON value.") from exc
    if len(encoded) > MAX_PAYLOAD_BYTES:
        raise ValueError(
            f"{name} exceeds the portable {MAX_PAYLOAD_BYTES}-byte limit."
        )
    return cast(JSONValue, value)


def _required_id(value: str, name: str) -> str:
    selected = value.strip()
    if not selected:
        raise ValueError(f"{name} is required.")
    if len(selected) > MAX_ID_CHARS:
        raise ValueError(f"{name} exceeds {MAX_ID_CHARS} characters.")
    return selected


def _validate_tags(tags: Mapping[str, str]) -> Mapping[str, str]:
    if len(tags) > MAX_TAG_COUNT:
        raise ValueError(f"Execution tags support at most {MAX_TAG_COUNT} entries.")
    output: dict[str, str] = {}
    for key, value in tags.items():
        normalized = key.strip()
        if not normalized:
            raise ValueError("Execution tag keys are required.")
        if len(normalized) > MAX_TAG_KEY_CHARS:
            raise ValueError(
                f"Execution tag keys support at most {MAX_TAG_KEY_CHARS} characters."
            )
        if len(value) > MAX_TAG_VALUE_CHARS:
            raise ValueError(
                f"Execution tag values support at most {MAX_TAG_VALUE_CHARS} characters."
            )
        output[normalized] = value
    return output


@dataclass(frozen=True)
class ExecutionHandlerDescriptor(ExecutionModel):
    handler_id: str
    display_name: str
    plugin_id: str | None = None
    description: str | None = None
    max_attempts: int = 1
    concurrency_key: str | None = None
    tags: Mapping[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        object.__setattr__(
            self, "handler_id", _required_id(self.handler_id, "Handler id")
        )
        display_name = self.display_name.strip()
        if not display_name:
            raise ValueError("Handler display name is required.")
        if len(display_name) > MAX_DISPLAY_NAME_CHARS:
            raise ValueError(
                f"Handler display name exceeds {MAX_DISPLAY_NAME_CHARS} characters."
            )
        object.__setattr__(self, "display_name", display_name)
        plugin_id = (
            _required_id(self.plugin_id, "Plugin id")
            if self.plugin_id is not None and self.plugin_id.strip()
            else None
        )
        object.__setattr__(self, "plugin_id", plugin_id)
        if self.description is not None and len(self.description) > MAX_DESCRIPTION_CHARS:
            raise ValueError(
                f"Handler description exceeds {MAX_DESCRIPTION_CHARS} characters."
            )
        if not 0 <= self.max_attempts <= MAX_RETRY_ATTEMPTS:
            raise ValueError(
                f"Handler maxAttempts must be between 0 and {MAX_RETRY_ATTEMPTS}."
            )
        if (
            self.concurrency_key is not None
            and len(self.concurrency_key) > MAX_ID_CHARS
        ):
            raise ValueError(
                f"Handler concurrencyKey exceeds {MAX_ID_CHARS} characters."
            )
        object.__setattr__(self, "tags", _validate_tags(dict(self.tags)))

    @classmethod
    def from_value(
        cls,
        value: ExecutionHandlerDescriptor | Mapping[str, Any],
    ) -> ExecutionHandlerDescriptor:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution handler descriptor")
        return cls(
            handler_id=_text(item.get("handlerId"), "handlerId"),
            display_name=_text(item.get("displayName"), "displayName"),
            plugin_id=_optional_text(item.get("pluginId"), "pluginId"),
            description=_optional_text(item.get("description"), "description"),
            max_attempts=_integer(item.get("maxAttempts"), "maxAttempts", 1),
            concurrency_key=_optional_text(
                item.get("concurrencyKey"), "concurrencyKey"
            ),
            tags=_string_map(item.get("tags"), "tags"),
        )


@dataclass(frozen=True)
class ExecutionPluginDescriptor(ExecutionModel):
    plugin_id: str
    name: str
    version: str
    handlers: tuple[ExecutionHandlerDescriptor, ...] = ()

    def __post_init__(self) -> None:
        plugin_id = _required_id(self.plugin_id, "Plugin id")
        name = self.name.strip()
        version = self.version.strip()
        if not name:
            raise ValueError("Plugin name is required.")
        if len(name) > MAX_DISPLAY_NAME_CHARS:
            raise ValueError(
                f"Plugin name exceeds {MAX_DISPLAY_NAME_CHARS} characters."
            )
        if not version:
            raise ValueError("Plugin version is required.")
        normalized: list[ExecutionHandlerDescriptor] = []
        seen: set[str] = set()
        for handler in self.handlers:
            selected = ExecutionHandlerDescriptor.from_value(handler)
            if selected.plugin_id not in {None, plugin_id}:
                raise ValueError(
                    f"Handler {selected.handler_id!r} does not belong to plugin "
                    f"{plugin_id!r}."
                )
            if selected.handler_id in seen:
                raise ValueError(
                    f"Plugin {plugin_id!r} repeats handler "
                    f"{selected.handler_id!r}."
                )
            seen.add(selected.handler_id)
            normalized.append(
                ExecutionHandlerDescriptor(
                    handler_id=selected.handler_id,
                    display_name=selected.display_name,
                    plugin_id=plugin_id,
                    description=selected.description,
                    max_attempts=selected.max_attempts,
                    concurrency_key=selected.concurrency_key,
                    tags=selected.tags,
                )
            )
        object.__setattr__(self, "plugin_id", plugin_id)
        object.__setattr__(self, "name", name)
        object.__setattr__(self, "version", version)
        object.__setattr__(self, "handlers", tuple(normalized))

    @classmethod
    def from_value(
        cls,
        value: ExecutionPluginDescriptor | Mapping[str, Any],
    ) -> ExecutionPluginDescriptor:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution plugin descriptor")
        raw_handlers = item.get("handlers", ())
        if not isinstance(raw_handlers, (list, tuple)):
            raise TypeError("handlers must be an array.")
        return cls(
            plugin_id=_text(item.get("pluginId"), "pluginId"),
            name=_text(item.get("name"), "name"),
            version=_text(item.get("version"), "version"),
            handlers=tuple(
                ExecutionHandlerDescriptor.from_value(handler)
                for handler in raw_handlers
            ),
        )


@dataclass(frozen=True)
class ExecutionScope(ExecutionModel):
    product_id: str = ""
    tenant_id: str = ""
    service_identity: str | None = None

    @classmethod
    def from_value(
        cls, value: ExecutionScope | Mapping[str, Any] | None
    ) -> ExecutionScope | None:
        if value is None or isinstance(value, cls):
            return value
        item = _mapping(value, "execution scope")
        return cls(
            product_id=_text(item.get("productId"), "productId", ""),
            tenant_id=_text(item.get("tenantId"), "tenantId", ""),
            service_identity=_optional_text(
                item.get("serviceIdentity"), "serviceIdentity"
            ),
        )


@dataclass(frozen=True)
class ExecutionRun(ExecutionModel):
    id: str
    handler_id: str
    status: str
    attempt: int
    max_attempts: int
    correlation_id: str
    payload_hash: str
    created_at_utc: datetime
    updated_at_utc: datetime
    cancellation_requested: bool
    tags: Mapping[str, str]
    plugin_id: str | None = None
    retry_policy: JSONObject = field(default_factory=dict)
    idempotency_key: str | None = None
    scope: ExecutionScope | None = None
    payload: JSONValue = None
    scheduled_at_utc: datetime | None = None
    started_at_utc: datetime | None = None
    completed_at_utc: datetime | None = None
    duration_ms: float | None = None
    requested: int | None = None
    attempted: int | None = None
    succeeded: int | None = None
    failed: int | None = None
    progress: float | None = None
    current_step: str | None = None
    failure_class: str | None = None
    error: str | None = None
    result: JSONValue = None
    status_details: JSONObject | None = None
    admission_replayed: bool = field(
        default=False,
        compare=False,
        repr=False,
        metadata={"wire": False},
    )
    admission: JSONObject = field(
        default_factory=dict,
        init=False,
        compare=False,
        repr=False,
    )

    def __post_init__(self) -> None:
        if self.status not in RUN_STATUSES:
            raise ValueError(f"Unknown execution run status {self.status!r}.")
        if self.progress is not None and not 0.0 <= self.progress <= 1.0:
            raise ValueError("Execution progress must be between zero and one.")
        object.__setattr__(self, "tags", dict(self.tags))
        object.__setattr__(self, "retry_policy", dict(self.retry_policy))
        operation_id = self.tags.get(
            "vyral.admission.operation-id", "startExecutionRun"
        )
        rejected = self.status == "rejected"
        object.__setattr__(
            self,
            "admission",
            create_admission_receipt(
                operation_id=operation_id,
                resource_id=self.id,
                request_hash=self.payload_hash,
                idempotency_key=self.idempotency_key,
                replayed=self.admission_replayed,
                admitted_at_utc=self.created_at_utc,
                status_uri=f"/execution/runs/{self.id}",
                status="rejected" if rejected else "accepted",
                failure_class=self.failure_class,
                error=self.error,
            ).to_dict(),
        )

    @classmethod
    def from_value(
        cls, value: ExecutionRun | Mapping[str, Any]
    ) -> ExecutionRun:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution run")
        scope = item.get("scope")
        payload = _json_value(item.get("payload"), "run payload")
        result = _json_value(item.get("result"), "run result")
        return cls(
            id=_text(item.get("id"), "run id"),
            handler_id=_text(item.get("handlerId"), "handlerId"),
            plugin_id=_optional_text(item.get("pluginId"), "pluginId"),
            status=_text(item.get("status"), "status"),
            attempt=_integer(item.get("attempt"), "attempt"),
            max_attempts=_integer(item.get("maxAttempts"), "maxAttempts", 1),
            retry_policy=(
                _json_object(item.get("retryPolicy"), "retryPolicy") or {}
            ),
            idempotency_key=_optional_text(
                item.get("idempotencyKey"), "idempotencyKey"
            ),
            correlation_id=_text(
                item.get("correlationId"), "correlationId", ""
            ),
            scope=ExecutionScope.from_value(
                scope if isinstance(scope, Mapping) else None
            ),
            payload_hash=_text(
                item.get("payloadHash"), "payloadHash", ""
            ),
            payload=payload,
            created_at_utc=_date(item.get("createdAtUtc"), "createdAtUtc"),
            scheduled_at_utc=_optional_date(
                item.get("scheduledAtUtc"), "scheduledAtUtc"
            ),
            started_at_utc=_optional_date(
                item.get("startedAtUtc"), "startedAtUtc"
            ),
            updated_at_utc=_date(item.get("updatedAtUtc"), "updatedAtUtc"),
            completed_at_utc=_optional_date(
                item.get("completedAtUtc"), "completedAtUtc"
            ),
            duration_ms=_optional_number(item.get("durationMs"), "durationMs"),
            cancellation_requested=_boolean(
                item.get("cancellationRequested"), "cancellationRequested"
            ),
            requested=_optional_integer(item.get("requested"), "requested"),
            attempted=_optional_integer(item.get("attempted"), "attempted"),
            succeeded=_optional_integer(item.get("succeeded"), "succeeded"),
            failed=_optional_integer(item.get("failed"), "failed"),
            progress=_optional_number(item.get("progress"), "progress"),
            current_step=_optional_text(item.get("currentStep"), "currentStep"),
            failure_class=_optional_text(
                item.get("failureClass"), "failureClass"
            ),
            error=_optional_text(item.get("error"), "error"),
            result=result,
            status_details=_json_object(
                item.get("statusDetails"), "statusDetails"
            ),
            tags=_string_map(item.get("tags"), "tags"),
        )


@dataclass(frozen=True)
class ExecutionRunUpdate(ExecutionModel):
    status: str | None = None
    requested: int | None = None
    attempted: int | None = None
    succeeded: int | None = None
    failed: int | None = None
    progress: float | None = None
    current_step: str | None = None
    failure_class: str | None = None
    error: str | None = None
    result: JSONValue = None
    status_details: JSONObject | None = None

    def __post_init__(self) -> None:
        if self.status is not None and self.status not in RUN_STATUSES:
            raise ValueError(f"Unknown execution run status {self.status!r}.")
        if self.progress is not None and not 0.0 <= self.progress <= 1.0:
            raise ValueError("Execution progress must be between zero and one.")
        _json_value(self.result, "run update result")

    @classmethod
    def from_value(
        cls, value: ExecutionRunUpdate | Mapping[str, Any]
    ) -> ExecutionRunUpdate:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution run update")
        return cls(
            status=_optional_text(item.get("status"), "status"),
            requested=_optional_integer(item.get("requested"), "requested"),
            attempted=_optional_integer(item.get("attempted"), "attempted"),
            succeeded=_optional_integer(item.get("succeeded"), "succeeded"),
            failed=_optional_integer(item.get("failed"), "failed"),
            progress=_optional_number(item.get("progress"), "progress"),
            current_step=_optional_text(item.get("currentStep"), "currentStep"),
            failure_class=_optional_text(
                item.get("failureClass"), "failureClass"
            ),
            error=_optional_text(item.get("error"), "error"),
            result=_json_value(item.get("result"), "run update result"),
            status_details=_json_object(
                item.get("statusDetails"), "statusDetails"
            ),
        )


@dataclass(frozen=True)
class ExecutionRunResult(ExecutionModel):
    status: str = "succeeded"
    result: JSONValue = None
    failure_class: str | None = None
    error: str | None = None
    status_details: JSONObject | None = None

    def __post_init__(self) -> None:
        validate_run_result(self)

    @classmethod
    def from_value(
        cls, value: ExecutionRunResult | Mapping[str, Any]
    ) -> ExecutionRunResult:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution run result")
        return cls(
            status=_text(item.get("status"), "status", "succeeded"),
            result=_json_value(item.get("result"), "run result"),
            failure_class=_optional_text(
                item.get("failureClass"), "failureClass"
            ),
            error=_optional_text(item.get("error"), "error"),
            status_details=_json_object(
                item.get("statusDetails"), "statusDetails"
            ),
        )

    @classmethod
    def succeeded_result(
        cls,
        result: JSONValue = None,
        *,
        status_details: JSONObject | None = None,
    ) -> ExecutionRunResult:
        return cls(
            status="succeeded",
            result=result,
            status_details=status_details,
        )

    @classmethod
    def failed_result(
        cls,
        failure_class: str,
        error: str,
        *,
        result: JSONValue = None,
    ) -> ExecutionRunResult:
        return cls(
            status="failed",
            result=result,
            failure_class=failure_class,
            error=error,
        )

    @classmethod
    def cancelled_result(
        cls, result: JSONValue = None
    ) -> ExecutionRunResult:
        return cls(
            status="cancelled",
            result=result,
            failure_class="cancelled",
            error="Execution run was cancelled.",
        )


def validate_run_result(result: ExecutionRunResult) -> None:
    if result.status not in TERMINAL_RUN_STATUSES:
        raise ValueError(
            "Execution handler results must use a terminal run status."
        )
    if result.failure_class is not None and len(result.failure_class) > MAX_ID_CHARS:
        raise ValueError(
            f"Execution failureClass exceeds {MAX_ID_CHARS} characters."
        )
    if result.error is not None and len(result.error) > MAX_TRACE_MESSAGE_CHARS:
        raise ValueError(
            f"Execution error exceeds {MAX_TRACE_MESSAGE_CHARS} characters."
        )
    encoded = json.dumps(
        result.result,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")
    if len(encoded) > MAX_RESULT_BYTES:
        raise ValueError(
            f"Execution result exceeds {MAX_RESULT_BYTES} bytes."
        )
    details = json.dumps(
        result.status_details,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")
    if len(details) > MAX_STATUS_DETAILS_BYTES:
        raise ValueError(
            "Execution statusDetails exceeds "
            f"{MAX_STATUS_DETAILS_BYTES} bytes."
        )


@dataclass(frozen=True)
class ExecutionArtifactWrite(ExecutionModel):
    name: str
    kind: str = "json"
    media_type: str | None = None
    text: str | None = None
    content: JSONValue = None
    uri: str | None = None
    metadata: Mapping[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        name = self.name.strip()
        if not name:
            raise ValueError("Artifact name is required.")
        if len(name) > MAX_ARTIFACT_NAME_CHARS:
            raise ValueError(
                f"Artifact name exceeds {MAX_ARTIFACT_NAME_CHARS} characters."
            )
        if self.kind not in ARTIFACT_KINDS:
            raise ValueError(f"Unknown artifact kind {self.kind!r}.")
        object.__setattr__(self, "name", name)
        object.__setattr__(self, "metadata", dict(self.metadata))
        _json_value(self.content, "artifact content")

    @classmethod
    def from_value(
        cls, value: ExecutionArtifactWrite | Mapping[str, Any]
    ) -> ExecutionArtifactWrite:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "artifact write")
        return cls(
            name=_text(item.get("name"), "artifact name"),
            kind=_text(item.get("kind"), "artifact kind", "json"),
            media_type=_optional_text(item.get("mediaType"), "mediaType"),
            text=_optional_text(item.get("text"), "artifact text"),
            content=_json_value(item.get("content"), "artifact content"),
            uri=_optional_text(item.get("uri"), "artifact uri"),
            metadata=_string_map(item.get("metadata"), "artifact metadata"),
        )


@dataclass(frozen=True)
class ExecutionArtifact(ExecutionModel):
    id: str
    run_id: str
    name: str
    kind: str
    content_hash: str
    size_bytes: int
    created_at_utc: datetime
    metadata: Mapping[str, str] = field(default_factory=dict)
    media_type: str | None = None
    text: str | None = None
    content: JSONValue = None
    uri: str | None = None

    @classmethod
    def from_value(
        cls, value: ExecutionArtifact | Mapping[str, Any]
    ) -> ExecutionArtifact:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution artifact")
        return cls(
            id=_text(item.get("id"), "artifact id"),
            run_id=_text(item.get("runId"), "runId"),
            name=_text(item.get("name"), "artifact name"),
            kind=_text(item.get("kind"), "artifact kind"),
            media_type=_optional_text(item.get("mediaType"), "mediaType"),
            content_hash=_text(item.get("contentHash"), "contentHash"),
            size_bytes=_integer(item.get("sizeBytes"), "sizeBytes"),
            text=_optional_text(item.get("text"), "artifact text"),
            content=_json_value(item.get("content"), "artifact content"),
            uri=_optional_text(item.get("uri"), "artifact uri"),
            created_at_utc=_date(item.get("createdAtUtc"), "createdAtUtc"),
            metadata=_string_map(item.get("metadata"), "artifact metadata"),
        )


@dataclass(frozen=True)
class ExecutionCheckpointWrite(ExecutionModel):
    key: str
    content: JSONValue = None
    metadata: Mapping[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        key = self.key.strip()
        if not key:
            raise ValueError("Checkpoint key is required.")
        if len(key) > MAX_CHECKPOINT_KEY_CHARS:
            raise ValueError(
                f"Checkpoint key exceeds {MAX_CHECKPOINT_KEY_CHARS} characters."
            )
        object.__setattr__(self, "key", key)
        object.__setattr__(self, "metadata", dict(self.metadata))
        _json_value(self.content, "checkpoint content")

    @classmethod
    def from_value(
        cls, value: ExecutionCheckpointWrite | Mapping[str, Any]
    ) -> ExecutionCheckpointWrite:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "checkpoint write")
        return cls(
            key=_text(item.get("key"), "checkpoint key"),
            content=_json_value(item.get("content"), "checkpoint content"),
            metadata=_string_map(item.get("metadata"), "checkpoint metadata"),
        )


@dataclass(frozen=True)
class ExecutionCheckpoint(ExecutionModel):
    run_id: str
    key: str
    content_hash: str
    updated_at_utc: datetime
    content: JSONValue = None
    metadata: Mapping[str, str] = field(default_factory=dict)

    @classmethod
    def from_value(
        cls, value: ExecutionCheckpoint | Mapping[str, Any]
    ) -> ExecutionCheckpoint:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution checkpoint")
        return cls(
            run_id=_text(item.get("runId"), "runId"),
            key=_text(item.get("key"), "checkpoint key"),
            content_hash=_text(item.get("contentHash"), "contentHash"),
            updated_at_utc=_date(item.get("updatedAtUtc"), "updatedAtUtc"),
            content=_json_value(item.get("content"), "checkpoint content"),
            metadata=_string_map(item.get("metadata"), "checkpoint metadata"),
        )


@dataclass(frozen=True)
class ExecutionExternalEvent(ExecutionModel):
    id: str
    name: str
    raised_at_utc: datetime
    run_id: str | None = None
    payload: JSONValue = None

    @classmethod
    def from_value(
        cls, value: ExecutionExternalEvent | Mapping[str, Any]
    ) -> ExecutionExternalEvent:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution external event")
        return cls(
            id=_text(item.get("id"), "event id"),
            name=_text(item.get("name"), "event name"),
            run_id=_optional_text(item.get("runId"), "runId"),
            raised_at_utc=_date(item.get("raisedAtUtc"), "raisedAtUtc"),
            payload=_json_value(item.get("payload"), "event payload"),
        )


@dataclass(frozen=True)
class ExecutionTimer(ExecutionModel):
    id: str
    name: str
    fire_at_utc: datetime
    created_at_utc: datetime
    run_id: str | None = None
    payload: JSONValue = None

    @classmethod
    def from_value(
        cls, value: ExecutionTimer | Mapping[str, Any]
    ) -> ExecutionTimer:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution timer")
        return cls(
            id=_text(item.get("id"), "timer id"),
            name=_text(item.get("name"), "timer name"),
            run_id=_optional_text(item.get("runId"), "runId"),
            fire_at_utc=_date(item.get("fireAtUtc"), "fireAtUtc"),
            created_at_utc=_date(item.get("createdAtUtc"), "createdAtUtc"),
            payload=_json_value(item.get("payload"), "timer payload"),
        )


@dataclass(frozen=True)
class ExecutionWaitResult(ExecutionModel):
    name: str
    outcome: str
    event: ExecutionExternalEvent | None = None
    timer: ExecutionTimer | None = None

    def __post_init__(self) -> None:
        if self.outcome not in WAIT_OUTCOMES:
            raise ValueError(f"Unknown wait outcome {self.outcome!r}.")

    @classmethod
    def from_value(
        cls, value: ExecutionWaitResult | Mapping[str, Any]
    ) -> ExecutionWaitResult:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "execution wait result")
        raw_event = item.get("event")
        raw_timer = item.get("timer")
        return cls(
            name=_text(item.get("name"), "wait name"),
            outcome=_text(item.get("outcome"), "wait outcome"),
            event=(
                ExecutionExternalEvent.from_value(raw_event)
                if isinstance(raw_event, Mapping)
                else None
            ),
            timer=(
                ExecutionTimer.from_value(raw_timer)
                if isinstance(raw_timer, Mapping)
                else None
            ),
        )


@dataclass(frozen=True)
class ExecutionWorkerWaitRequest(ExecutionModel):
    kind: str
    name: str
    timeout_at_utc: datetime | None = None
    fire_at_utc: datetime | None = None
    payload: JSONValue = None

    def __post_init__(self) -> None:
        if self.kind not in WAIT_KINDS:
            raise ValueError(f"Unknown external-worker wait kind {self.kind!r}.")
        name = self.name.strip()
        if not name:
            raise ValueError("External-worker wait name is required.")
        object.__setattr__(self, "name", name)
        if self.timeout_at_utc is not None:
            object.__setattr__(
                self,
                "timeout_at_utc",
                _date(self.timeout_at_utc, "timeoutAtUtc"),
            )
        if self.fire_at_utc is not None:
            object.__setattr__(
                self,
                "fire_at_utc",
                _date(self.fire_at_utc, "fireAtUtc"),
            )
        if self.kind == "timer" and self.fire_at_utc is None:
            raise ValueError("Timer waits require fireAtUtc.")
        _json_value(self.payload, "wait payload")

    @classmethod
    def from_value(
        cls, value: ExecutionWorkerWaitRequest | Mapping[str, Any]
    ) -> ExecutionWorkerWaitRequest:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "external-worker wait request")
        return cls(
            kind=_text(item.get("kind"), "kind"),
            name=_text(item.get("name"), "name"),
            timeout_at_utc=_optional_date(
                item.get("timeoutAtUtc"), "timeoutAtUtc"
            ),
            fire_at_utc=_optional_date(
                item.get("fireAtUtc"), "fireAtUtc"
            ),
            payload=cast(JSONValue, item.get("payload")),
        )


@dataclass(frozen=True)
class ExecutionWorkerWaitResponse(ExecutionModel):
    run: ExecutionRun
    suspended: bool
    outcome: ExecutionWaitResult | None = None

    @classmethod
    def from_value(
        cls, value: ExecutionWorkerWaitResponse | Mapping[str, Any]
    ) -> ExecutionWorkerWaitResponse:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "external-worker wait response")
        outcome = item.get("outcome")
        return cls(
            run=ExecutionRun.from_value(
                _mapping(item.get("run"), "external-worker wait run")
            ),
            suspended=_boolean(item.get("suspended"), "suspended"),
            outcome=(
                ExecutionWaitResult.from_value(outcome)
                if isinstance(outcome, Mapping)
                else None
            ),
        )


@dataclass(frozen=True)
class ExecutionWorkerLease(ExecutionModel):
    lease_key: str
    lease_token: str = field(repr=False)
    worker_id: str
    run: ExecutionRun
    acquired_at_utc: datetime = field(default_factory=_now)
    expires_at_utc: datetime = field(default_factory=_now)

    def __post_init__(self) -> None:
        if not self.lease_key.strip():
            raise ValueError("External-worker leaseKey is required.")
        if not self.lease_token.strip():
            raise ValueError("External-worker leaseToken is required.")

    @classmethod
    def from_value(
        cls, value: ExecutionWorkerLease | Mapping[str, Any]
    ) -> ExecutionWorkerLease:
        if isinstance(value, cls):
            return value
        item = _mapping(value, "external-worker lease")
        return cls(
            lease_key=_text(item.get("leaseKey"), "leaseKey"),
            lease_token=_text(item.get("leaseToken"), "leaseToken"),
            worker_id=_text(item.get("workerId"), "workerId"),
            run=ExecutionRun.from_value(
                _mapping(item.get("run"), "external-worker lease run")
            ),
            acquired_at_utc=_date(
                item.get("acquiredAtUtc"), "acquiredAtUtc"
            ),
            expires_at_utc=_date(item.get("expiresAtUtc"), "expiresAtUtc"),
        )

    def safe_summary(self) -> JSONObject:
        return {
            "leaseKey": self.lease_key,
            "workerId": self.worker_id,
            "runId": self.run.id,
            "expiresAtUtc": _format_date(self.expires_at_utc),
        }
