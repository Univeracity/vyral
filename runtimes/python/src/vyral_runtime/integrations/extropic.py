"""Experimental Extropic execution integration.

Vyral owns durable admission, lifecycle state, and public results. Extropic is
an optional downstream compute target for a function registered by the host.
The integration never accepts executable source in a run payload.

The Extropic 0.5 API does not accept an idempotency key when creating a job.
This adapter therefore records an intent checkpoint before creation and fails
closed if creation has an ambiguous outcome. Once a provider job id is known,
replay operates on that job and never creates a replacement automatically.
"""

from __future__ import annotations

import asyncio
import base64
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass, field
from hashlib import sha256
import importlib
import inspect
import json
import math
import sys
from types import FunctionType
from typing import Any, NoReturn, Protocol, TypeVar, cast

from ..contracts import JSONObject, JSONValue
from ..execution.models import (
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionRunResult,
    ExecutionRunUpdate,
)
from ..execution.worker import ExecutionRunContext


EXTROPIC_CHECKPOINT_KEY = "vyral.extropic.v1"
_CHECKPOINT_VERSION = 1
_SUPPORTED_TIERS = frozenset({"cpu", "l4", "a100", "h100"})
_IN_FLIGHT_STATUSES = frozenset(
    {"uploading", "pending", "provisioning", "running"}
)
_TERMINAL_STATUSES = frozenset(
    {
        "succeeded",
        "failed",
        "timeout",
        "cancelled",
        "out_of_credits",
        "preempted",
        "expired",
        "error",
        "at_capacity",
    }
)
_MAX_PROVIDER_ID_CHARS = 160
_CLOUDPICKLE_ENTRYPOINT = "__extropic_cloudpickle__"

ExtropicWorkload = Callable[[JSONValue], Any]
Sleep = Callable[[float], Awaitable[None]]
T = TypeVar("T")


class ExtropicIntegrationError(RuntimeError):
    """Base error for the optional Extropic integration."""


class ExtropicDependencyError(ExtropicIntegrationError):
    """The installed Extropic SDK is missing or incompatible."""


class ExtropicProviderError(ExtropicIntegrationError):
    """Extropic rejected or could not complete an operation."""


class ExtropicTransportError(ExtropicProviderError):
    """The provider outcome may be unknown because transport failed."""


class ExtropicOutOfCreditsError(ExtropicProviderError):
    """The Extropic account cannot cover the requested work."""


class ExtropicRateLimitedError(ExtropicProviderError):
    """Extropic asked the caller to back off."""

    def __init__(self, retry_after_seconds: float | None = None) -> None:
        super().__init__("Extropic rate limited the operation.")
        self.retry_after_seconds = retry_after_seconds


class ExtropicAtCapacityError(ExtropicProviderError):
    """The existing uploaded job could not start for lack of capacity."""

    def __init__(self, job_id: str) -> None:
        super().__init__("Extropic has no capacity for the existing job.")
        self.job_id = job_id


@dataclass(frozen=True)
class ExtropicPreparedJob:
    """Local submission bytes and the non-secret start manifest."""

    payload: bytes = field(repr=False)
    manifest: Mapping[str, JSONValue]

    def __post_init__(self) -> None:
        object.__setattr__(self, "payload", bytes(self.payload))
        object.__setattr__(self, "manifest", dict(self.manifest))


@dataclass(frozen=True)
class ExtropicCreatedJob:
    """A newly reserved provider job and its ephemeral upload grant."""

    job_id: str
    input_path: str = field(repr=False)
    input_token: str = field(repr=False)
    artifact_url: str | None = field(default=None, repr=False)

    def __post_init__(self) -> None:
        selected = self.job_id.strip()
        if not selected or len(selected) > _MAX_PROVIDER_ID_CHARS:
            raise ValueError("Extropic returned an invalid job id.")
        if not self.input_path or not self.input_token:
            raise ValueError("Extropic returned an incomplete upload grant.")
        object.__setattr__(self, "job_id", selected)


@dataclass(frozen=True)
class ExtropicJobSnapshot:
    """The provider state that Vyral is permitted to retain and expose."""

    job_id: str
    status: str
    progress: JSONValue = None

    def __post_init__(self) -> None:
        selected_id = self.job_id.strip()
        selected_status = self.status.strip().lower()
        if not selected_id or len(selected_id) > _MAX_PROVIDER_ID_CHARS:
            raise ValueError("Extropic returned an invalid job id.")
        if not selected_status:
            raise ValueError("Extropic returned an empty job status.")
        _json_bytes(self.progress)
        object.__setattr__(self, "job_id", selected_id)
        object.__setattr__(self, "status", selected_status)


class ExtropicBackend(Protocol):
    """Narrow provider boundary used by the durable adapter and its tests."""

    def prepare(
        self,
        workload: ExtropicWorkload,
        payload: JSONValue,
        timeout_seconds: float | None,
    ) -> ExtropicPreparedJob: ...

    def create_job(self, tier: str) -> ExtropicCreatedJob: ...

    def upload_job(
        self,
        created: ExtropicCreatedJob,
        prepared: ExtropicPreparedJob,
    ) -> None: ...

    def start_job(
        self,
        job_id: str,
        manifest: Mapping[str, JSONValue],
    ) -> ExtropicJobSnapshot: ...

    def inspect_job(self, job_id: str) -> ExtropicJobSnapshot: ...

    def cancel_job(self, job_id: str) -> None: ...

    def result(self, job_id: str) -> Any: ...

    def artifact_text(self, job_id: str, name: str) -> str | None: ...


@dataclass(frozen=True)
class ExtropicAdapterOptions:
    """Bounded policy for one registered Extropic workload."""

    tier: str = "cpu"
    timeout_seconds: float | None = 300.0
    poll_interval_seconds: float = 2.0
    capacity_retry_attempts: int = 6
    provider_error_retries: int = 3
    max_serialized_bytes: int = 16 * 1024 * 1024
    max_artifact_bytes: int = 512 * 1024
    collect_output: bool = True
    require_seed: bool = False
    seed_field: str = "seed"

    def __post_init__(self) -> None:
        tier = self.tier.strip().lower()
        if tier not in _SUPPORTED_TIERS:
            raise ValueError(
                "Extropic tier must be one of cpu, l4, a100, or h100."
            )
        if self.timeout_seconds is not None and (
            not math.isfinite(self.timeout_seconds)
            or self.timeout_seconds <= 0
        ):
            raise ValueError("Extropic timeout must be positive and finite.")
        if (
            not math.isfinite(self.poll_interval_seconds)
            or not 0 < self.poll_interval_seconds <= 60
        ):
            raise ValueError(
                "Extropic poll interval must be between zero and 60 seconds."
            )
        if not 0 <= self.capacity_retry_attempts <= 100:
            raise ValueError(
                "Extropic capacity retries must be between zero and 100."
            )
        if not 0 <= self.provider_error_retries <= 25:
            raise ValueError(
                "Extropic provider retries must be between zero and 25."
            )
        if not 1 <= self.max_serialized_bytes <= 100_000_000:
            raise ValueError(
                "Extropic serialized input limit must be between one and "
                "100,000,000 bytes."
            )
        if not 1 <= self.max_artifact_bytes <= 1_048_576:
            raise ValueError(
                "Extropic artifact limit must be between one byte and 1 MiB."
            )
        seed_field = self.seed_field.strip()
        if not seed_field:
            raise ValueError("Extropic seed field is required.")
        object.__setattr__(self, "tier", tier)
        object.__setattr__(self, "seed_field", seed_field)


def _transport_workload(workload: ExtropicWorkload) -> ExtropicWorkload:
    """Make a plain registered function self-contained in cloudpickle.

    Cloudpickle normally serializes an importable module-level function by
    reference. The Extropic sandbox does not contain the host application's
    module, so that otherwise turns a successful upload into an import error
    at execution time. A shallow function clone identified as ``__main__`` is
    serialized by value while referenced third-party modules, such as Torx and
    JAX, remain ordinary sandbox imports.

    Callable objects and extension functions retain cloudpickle's native
    behavior. Registered Extropic workloads should therefore be plain Python
    functions unless their defining package is known to exist in the sandbox.
    """

    if not inspect.isfunction(workload):
        return workload
    function = cast(FunctionType, workload)
    transported = FunctionType(
        function.__code__,
        function.__globals__,
        function.__name__,
        function.__defaults__,
        function.__closure__,
    )
    transported.__kwdefaults__ = function.__kwdefaults__
    transported.__doc__ = function.__doc__
    transported.__module__ = "__main__"
    transported.__qualname__ = function.__name__
    return cast(ExtropicWorkload, transported)


class ExtropicSdkBackend:
    """Adapter for the public ``extro-sim`` 0.5.x Python SDK."""

    def __init__(self, client: Any = None) -> None:
        self._supplied_client = client
        self._client: Any = None
        self._sdk: Any = None

    def prepare(
        self,
        workload: ExtropicWorkload,
        payload: JSONValue,
        timeout_seconds: float | None,
    ) -> ExtropicPreparedJob:
        self._ensure_sdk()
        try:
            cloudpickle = importlib.import_module("cloudpickle")
            transport_workload = _transport_workload(workload)
            encoded = cloudpickle.dumps((transport_workload, (payload,), {}))
            envelope = base64.b64encode(
                json.dumps(
                    {
                        "python_version": "%d.%d" % sys.version_info[:2],
                        "cloudpickle_version": cloudpickle.__version__,
                        "pickle": base64.b64encode(encoded).decode("ascii"),
                    },
                    allow_nan=False,
                    separators=(",", ":"),
                ).encode("utf-8")
            ).decode("ascii")
            submission = json.dumps(
                {
                    "code": envelope,
                    "entrypoint": _CLOUDPICKLE_ENTRYPOINT,
                    "args": [],
                    "kwargs": {},
                },
                allow_nan=False,
                separators=(",", ":"),
            ).encode("utf-8")
        except Exception as exc:
            raise ExtropicIntegrationError(
                "The registered Extropic workload could not be serialized."
            ) from exc
        return ExtropicPreparedJob(
            payload=submission,
            manifest={
                "python_version": "%d.%d" % sys.version_info[:2],
                "cloudpickle_version": cast(str, cloudpickle.__version__),
                "timeout_s": timeout_seconds,
            },
        )

    def create_job(self, tier: str) -> ExtropicCreatedJob:
        self._ensure_sdk()
        record = self._call(lambda: self._client.create_job(tier))
        try:
            return ExtropicCreatedJob(
                job_id=str(record["id"]),
                input_path=str(record["input_path"]),
                input_token=str(record["input_token"]),
                artifact_url=(
                    str(record["artifact_url"])
                    if record.get("artifact_url")
                    else None
                ),
            )
        except (KeyError, TypeError, ValueError) as exc:
            raise ExtropicProviderError(
                "Extropic returned an invalid create response."
            ) from exc

    def upload_job(
        self,
        created: ExtropicCreatedJob,
        prepared: ExtropicPreparedJob,
    ) -> None:
        self._ensure_sdk()
        self._call(
            lambda: self._client.upload_input(
                created.input_path,
                prepared.payload,
                token=created.input_token,
                artifact_url=created.artifact_url,
            ),
            job_id=created.job_id,
        )

    def start_job(
        self,
        job_id: str,
        manifest: Mapping[str, JSONValue],
    ) -> ExtropicJobSnapshot:
        self._ensure_sdk()
        record = self._call(
            lambda: self._client.start_job(job_id, dict(manifest)),
            job_id=job_id,
        )
        return _snapshot_from(record, job_id)

    def inspect_job(self, job_id: str) -> ExtropicJobSnapshot:
        self._ensure_sdk()
        record = self._call(
            lambda: self._client.get(job_id),
            job_id=job_id,
        )
        return _snapshot_from(record, job_id)

    def cancel_job(self, job_id: str) -> None:
        self._ensure_sdk()
        self._call(lambda: self._client.cancel(job_id), job_id=job_id)

    def result(self, job_id: str) -> Any:
        self._ensure_sdk()
        job = self._sdk.Job(job_id, self._client)
        return self._call(lambda: job.result(), job_id=job_id)

    def artifact_text(self, job_id: str, name: str) -> str | None:
        self._ensure_sdk()
        data = self._call(
            lambda: self._client.download_artifact(job_id, name),
            job_id=job_id,
        )
        if data is None:
            return None
        if not isinstance(data, bytes):
            raise ExtropicProviderError(
                "Extropic returned an invalid artifact response."
            )
        return data.decode("utf-8", "replace")

    def _ensure_sdk(self) -> None:
        if self._sdk is not None:
            return
        try:
            sdk = importlib.import_module("extro_sim")
        except ImportError as exc:
            raise ExtropicDependencyError(
                "Install vyral-runtime[extropic] to use Extropic execution."
            ) from exc
        version = str(getattr(sdk, "__version__", ""))
        if not version.startswith("0.5."):
            raise ExtropicDependencyError(
                "The Extropic integration currently requires extro-sim 0.5.x."
            )
        self._sdk = sdk
        self._client = self._supplied_client or sdk.Client()

    def _call(
        self,
        operation: Callable[[], T],
        *,
        job_id: str | None = None,
    ) -> T:
        try:
            return operation()
        except Exception as exc:
            self._raise_translated(exc, job_id=job_id)

    def _raise_translated(
        self,
        error: Exception,
        *,
        job_id: str | None,
    ) -> NoReturn:
        sdk = self._sdk
        if isinstance(error, sdk.RateLimited):
            retry_after = getattr(error, "retry_after", None)
            raise ExtropicRateLimitedError(
                float(retry_after) if retry_after is not None else None
            ) from error
        if isinstance(error, sdk.OutOfCredits):
            raise ExtropicOutOfCreditsError(
                "Extropic reported insufficient credits."
            ) from error
        if isinstance(error, sdk.AtCapacity):
            selected_id = job_id
            handle = getattr(error, "job", None)
            if handle is not None and getattr(handle, "id", None):
                selected_id = str(handle.id)
            if selected_id is None:
                raise ExtropicProviderError(
                    "Extropic reported capacity pressure without a job id."
                ) from error
            raise ExtropicAtCapacityError(selected_id) from error
        if isinstance(error, (TimeoutError, OSError)):
            raise ExtropicTransportError(
                "The Extropic operation did not return a definitive outcome."
            ) from error
        if isinstance(error, sdk.ExtropicError):
            raise ExtropicProviderError(
                "Extropic could not complete the provider operation."
            ) from error
        raise ExtropicProviderError(
            "The Extropic SDK failed during the provider operation."
        ) from error


class ExtropicExecutionAdapter:
    """Run one registered Python workload as a Vyral-managed Extropic job."""

    def __init__(
        self,
        workload_id: str,
        workload: ExtropicWorkload,
        *,
        options: ExtropicAdapterOptions | None = None,
        backend: ExtropicBackend | None = None,
        sleep: Sleep = asyncio.sleep,
    ) -> None:
        selected_id = workload_id.strip()
        if not selected_id or len(selected_id) > _MAX_PROVIDER_ID_CHARS:
            raise ValueError("Extropic workload id is required and must be bounded.")
        if not callable(workload):
            raise TypeError("Extropic workload must be callable.")
        if inspect.iscoroutinefunction(workload):
            raise TypeError("Extropic workloads must be synchronous functions.")
        self.workload_id = selected_id
        self.workload = workload
        self.options = options or ExtropicAdapterOptions()
        self.backend = backend or ExtropicSdkBackend()
        self._sleep = sleep

    async def execute(
        self,
        context: ExecutionRunContext,
    ) -> ExecutionRunResult:
        payload = context.run.payload
        if context.cancellation_requested:
            return ExecutionRunResult.cancelled_result()
        seed_error = self._validate_seed(payload)
        if seed_error is not None:
            return self._failure("validation", seed_error)

        try:
            prepared = await asyncio.to_thread(
                self.backend.prepare,
                self.workload,
                payload,
                self.options.timeout_seconds,
            )
        except ExtropicIntegrationError:
            return self._failure(
                "validation",
                "The registered Extropic workload could not be prepared.",
            )
        except Exception:
            return self._failure(
                "validation",
                "The registered Extropic workload could not be prepared.",
            )
        if len(prepared.payload) > self.options.max_serialized_bytes:
            return self._failure(
                "validation",
                "The serialized Extropic workload exceeds the configured limit.",
            )

        fingerprint = self._fingerprint(context)
        checkpoint = await context.get_checkpoint(EXTROPIC_CHECKPOINT_KEY)
        state = self._read_state(
            checkpoint.content if checkpoint is not None else None,
            fingerprint,
        )
        if isinstance(state, ExecutionRunResult):
            return state

        provider_job_id = _optional_string(state.get("providerJobId"))
        try:
            if not state:
                state = self._state("creating", fingerprint)
                await self._checkpoint(context, state)
                await context.report(
                    ExecutionRunUpdate(
                        status="waiting",
                        requested=1,
                        current_step="reserving Extropic compute",
                        status_details=self._status_details(None, "creating"),
                    )
                )
                try:
                    created = await self._create_job_cancellation_safe()
                except ExtropicOutOfCreditsError:
                    return self._failure(
                        "platform",
                        "The Extropic account has insufficient credits.",
                        details=self._status_details(None, "out_of_credits"),
                    )
                except ExtropicProviderError:
                    return self._failure(
                        "transient",
                        "Extropic job creation has an ambiguous outcome; "
                        "Vyral will not resubmit it automatically.",
                        details={
                            **self._status_details(None, "creating"),
                            "submissionAmbiguous": True,
                        },
                    )
                provider_job_id = created.job_id
                state = self._state(
                    "created",
                    fingerprint,
                    provider_job_id=provider_job_id,
                    provider_status="uploading",
                )
                try:
                    await self._checkpoint(context, state)
                except Exception:
                    await self._best_effort_cancel(provider_job_id)
                    raise
                await context.record_event(
                    "extropic.job.created",
                    message="Extropic job reserved.",
                    details={"providerJobId": provider_job_id},
                )
                await context.report(
                    ExecutionRunUpdate(
                        status="waiting",
                        attempted=1,
                        current_step="uploading Extropic input",
                        status_details=self._status_details(
                            provider_job_id, "uploading"
                        ),
                    )
                )
                try:
                    await asyncio.to_thread(
                        self.backend.upload_job,
                        created,
                        prepared,
                    )
                except ExtropicOutOfCreditsError:
                    return self._failure(
                        "platform",
                        "The Extropic account has insufficient credits.",
                        details=self._status_details(
                            provider_job_id, "out_of_credits"
                        ),
                    )
                except ExtropicProviderError:
                    return self._failure(
                        "transient",
                        "Extropic input upload did not complete. The existing "
                        "provider job was retained for reconciliation.",
                        details=self._status_details(
                            provider_job_id, "uploading"
                        ),
                    )
                state = self._state(
                    "uploaded",
                    fingerprint,
                    provider_job_id=provider_job_id,
                    provider_status="uploading",
                )
                try:
                    await self._checkpoint(context, state)
                except Exception:
                    await self._best_effort_cancel(provider_job_id)
                    raise
            elif provider_job_id is None:
                return self._failure(
                    "transient",
                    "Extropic job creation has an ambiguous outcome; Vyral "
                    "will not resubmit it automatically.",
                    details={
                        **self._status_details(None, "creating"),
                        "submissionAmbiguous": True,
                    },
                )

            assert provider_job_id is not None
            snapshot = await self._inspect_with_retries(provider_job_id)
            if isinstance(snapshot, ExecutionRunResult):
                return snapshot
            if snapshot.status == "uploading":
                state = self._state(
                    "starting",
                    fingerprint,
                    provider_job_id=provider_job_id,
                    provider_status=snapshot.status,
                )
                await self._checkpoint(context, state)
                snapshot = await self._start_with_retries(
                    context,
                    provider_job_id,
                    prepared,
                    fingerprint,
                )
                if isinstance(snapshot, ExecutionRunResult):
                    return snapshot

            return await self._supervise(
                context,
                provider_job_id,
                snapshot,
                fingerprint,
            )
        except asyncio.CancelledError:
            if provider_job_id is not None:
                await self._best_effort_cancel(provider_job_id)
            raise

    async def __call__(
        self,
        context: ExecutionRunContext,
    ) -> ExecutionRunResult:
        return await self.execute(context)

    async def _start_with_retries(
        self,
        context: ExecutionRunContext,
        job_id: str,
        prepared: ExtropicPreparedJob,
        fingerprint: str,
    ) -> ExtropicJobSnapshot | ExecutionRunResult:
        attempt = 0
        while True:
            await context.report(
                ExecutionRunUpdate(
                    status="waiting",
                    current_step="starting Extropic compute",
                    status_details=self._status_details(job_id, "uploading"),
                )
            )
            try:
                return await asyncio.to_thread(
                    self.backend.start_job,
                    job_id,
                    prepared.manifest,
                )
            except ExtropicAtCapacityError:
                if attempt >= self.options.capacity_retry_attempts:
                    return self._failure(
                        "transient",
                        "Extropic capacity was unavailable. The uploaded job "
                        "was retained and can be started again safely.",
                        details=self._status_details(job_id, "at_capacity"),
                    )
                attempt += 1
                await self._checkpoint(
                    context,
                    self._state(
                        "capacity_wait",
                        fingerprint,
                        provider_job_id=job_id,
                        provider_status="at_capacity",
                    ),
                )
                await self._sleep(self.options.poll_interval_seconds)
            except ExtropicRateLimitedError as error:
                if attempt >= self.options.provider_error_retries:
                    return self._failure(
                        "transient",
                        "Extropic rate limited the start operation.",
                        details=self._status_details(job_id, "uploading"),
                    )
                attempt += 1
                await self._sleep(self._retry_delay(error, attempt))
            except ExtropicOutOfCreditsError:
                return self._failure(
                    "platform",
                    "The Extropic account has insufficient credits.",
                    details=self._status_details(job_id, "out_of_credits"),
                )
            except ExtropicProviderError:
                return self._failure(
                    "transient",
                    "Extropic did not confirm that the existing job started. "
                    "Vyral will only retry that same provider job.",
                    details=self._status_details(job_id, "starting"),
                )

    async def _inspect_with_retries(
        self,
        job_id: str,
    ) -> ExtropicJobSnapshot | ExecutionRunResult:
        for attempt in range(self.options.provider_error_retries + 1):
            try:
                return await asyncio.to_thread(
                    self.backend.inspect_job,
                    job_id,
                )
            except ExtropicRateLimitedError as error:
                if attempt >= self.options.provider_error_retries:
                    break
                await self._sleep(self._retry_delay(error, attempt + 1))
            except ExtropicProviderError:
                if attempt >= self.options.provider_error_retries:
                    break
                await self._sleep(
                    min(
                        60.0,
                        self.options.poll_interval_seconds * (2 ** attempt),
                    )
                )
        return self._failure(
            "transient",
            "Extropic job status could not be confirmed.",
            details=self._status_details(job_id, "unknown"),
        )

    async def _supervise(
        self,
        context: ExecutionRunContext,
        job_id: str,
        snapshot: ExtropicJobSnapshot,
        fingerprint: str,
    ) -> ExecutionRunResult:
        last_status: str | None = None
        cancel_sent = False
        while True:
            status = snapshot.status
            if status not in _IN_FLIGHT_STATUSES | _TERMINAL_STATUSES:
                return self._failure(
                    "platform",
                    "Extropic returned an unsupported job status.",
                    details=self._status_details(job_id, "unknown"),
                )
            if status != last_status:
                await self._checkpoint(
                    context,
                    self._state(
                        "terminal" if status in _TERMINAL_STATUSES else "submitted",
                        fingerprint,
                        provider_job_id=job_id,
                        provider_status=status,
                        cancel_requested=cancel_sent,
                    ),
                )
                await context.record_event(
                    "extropic.status.changed",
                    message="Extropic job status changed.",
                    details={
                        "providerJobId": job_id,
                        "providerStatus": status,
                    },
                )
                last_status = status

            progress = _progress_fraction(snapshot.progress)
            await context.report(
                ExecutionRunUpdate(
                    status="running" if status == "running" else "waiting",
                    requested=1,
                    attempted=1,
                    progress=progress,
                    current_step=f"Extropic: {status.replace('_', ' ')}",
                    status_details=self._status_details(job_id, status),
                )
            )
            if status in _TERMINAL_STATUSES:
                return await self._terminal_result(context, job_id, status)

            if context.cancellation_requested and not cancel_sent:
                cancel_sent = True
                await self._checkpoint(
                    context,
                    self._state(
                        "cancelling",
                        fingerprint,
                        provider_job_id=job_id,
                        provider_status=status,
                        cancel_requested=True,
                    ),
                )
                try:
                    await asyncio.to_thread(self.backend.cancel_job, job_id)
                except ExtropicProviderError:
                    return self._failure(
                        "transient",
                        "Extropic did not confirm the cancellation request.",
                        details=self._status_details(job_id, status),
                    )
                await context.record_event(
                    "extropic.cancellation.requested",
                    message="Extropic cancellation requested.",
                    details={"providerJobId": job_id},
                )

            await self._sleep(self.options.poll_interval_seconds)
            inspected = await self._inspect_with_retries(job_id)
            if isinstance(inspected, ExecutionRunResult):
                return inspected
            snapshot = inspected

    async def _terminal_result(
        self,
        context: ExecutionRunContext,
        job_id: str,
        status: str,
    ) -> ExecutionRunResult:
        if self.options.collect_output:
            names = ["stdout", "stderr"]
            if status == "failed":
                names.append("traceback")
            for name in names:
                await self._collect_artifact(context, job_id, name)

        details = self._status_details(job_id, status)
        if status == "succeeded":
            try:
                result = await asyncio.to_thread(self.backend.result, job_id)
                _json_bytes(result)
            except (TypeError, ValueError, OverflowError):
                return self._failure(
                    "validation",
                    "The Extropic workload returned a value that is not valid "
                    "Vyral JSON.",
                    details=details,
                )
            except ExtropicProviderError:
                return self._failure(
                    "transient",
                    "The Extropic result artifact could not be collected.",
                    details=details,
                )
            return ExecutionRunResult.succeeded_result(
                cast(JSONValue, result),
                status_details=details,
            )
        if status == "cancelled":
            return ExecutionRunResult(
                status="cancelled",
                failure_class="cancelled",
                error="Execution run was cancelled.",
                status_details=details,
            )
        if status == "timeout":
            return ExecutionRunResult(
                status="timed_out",
                failure_class="timeout",
                error="The Extropic workload reached its configured timeout.",
                status_details=details,
            )
        if status in {"preempted", "at_capacity"}:
            return self._failure(
                "transient",
                "Extropic compute was unavailable before the workload completed.",
                details=details,
            )
        if status == "out_of_credits":
            return self._failure(
                "platform",
                "The Extropic account ran out of credits.",
                details=details,
            )
        if status == "failed":
            return self._failure(
                "unknown",
                "The registered Extropic workload failed. See retained artifacts.",
                details=details,
            )
        return self._failure(
            "platform",
            "Extropic did not complete the provider job.",
            details=details,
        )

    async def _collect_artifact(
        self,
        context: ExecutionRunContext,
        job_id: str,
        name: str,
    ) -> None:
        try:
            text = await asyncio.to_thread(
                self.backend.artifact_text,
                job_id,
                name,
            )
        except ExtropicProviderError:
            return
        if text is None:
            return
        selected, truncated = _truncate_utf8(
            text,
            self.options.max_artifact_bytes,
        )
        await context.put_artifact(
            ExecutionArtifactWrite(
                name=f"extropic.{name}",
                kind="text",
                media_type="text/plain; charset=utf-8",
                text=selected,
                metadata={
                    "provider": "extropic",
                    "providerJobId": job_id,
                    "providerArtifact": name,
                    "truncated": str(truncated).lower(),
                },
            )
        )

    async def _checkpoint(
        self,
        context: ExecutionRunContext,
        state: JSONObject,
    ) -> None:
        await context.put_checkpoint(
            ExecutionCheckpointWrite(
                key=EXTROPIC_CHECKPOINT_KEY,
                content=state,
                metadata={"provider": "extropic", "version": "1"},
            )
        )

    async def _best_effort_cancel(self, job_id: str) -> None:
        try:
            await asyncio.shield(
                asyncio.to_thread(self.backend.cancel_job, job_id)
            )
        except Exception:
            pass

    async def _create_job_cancellation_safe(self) -> ExtropicCreatedJob:
        """Resolve a cancelled create call so a returned job is not orphaned.

        Extropic creation is not idempotent. Shielding the thread alone would
        leave it running after this task was cancelled and discard a job id
        that arrived later. We instead settle that one bounded SDK call and
        cancel any job it minted before propagating cancellation.
        """

        task = asyncio.create_task(
            asyncio.to_thread(self.backend.create_job, self.options.tier)
        )
        try:
            return await asyncio.shield(task)
        except asyncio.CancelledError:
            try:
                created = await task
            except Exception:
                pass
            else:
                await self._best_effort_cancel(created.job_id)
            raise

    def _read_state(
        self,
        value: JSONValue,
        fingerprint: str,
    ) -> JSONObject | ExecutionRunResult:
        if value is None:
            return {}
        if not isinstance(value, dict):
            return self._failure(
                "validation",
                "The Extropic durable checkpoint is invalid.",
            )
        if value.get("version") != _CHECKPOINT_VERSION:
            return self._failure(
                "validation",
                "The Extropic durable checkpoint version is unsupported.",
            )
        if value.get("requestFingerprint") != fingerprint:
            return self._failure(
                "idempotency_conflict",
                "The Extropic checkpoint does not match this admitted request.",
            )
        if value.get("workloadId") != self.workload_id:
            return self._failure(
                "idempotency_conflict",
                "The Extropic checkpoint belongs to another workload.",
            )
        if value.get("tier") != self.options.tier:
            return self._failure(
                "idempotency_conflict",
                "The Extropic checkpoint belongs to another compute tier.",
            )
        return dict(value)

    def _state(
        self,
        phase: str,
        fingerprint: str,
        *,
        provider_job_id: str | None = None,
        provider_status: str | None = None,
        cancel_requested: bool = False,
    ) -> JSONObject:
        return {
            "version": _CHECKPOINT_VERSION,
            "phase": phase,
            "requestFingerprint": fingerprint,
            "workloadId": self.workload_id,
            "tier": self.options.tier,
            "providerJobId": provider_job_id,
            "providerStatus": provider_status,
            "cancelRequested": cancel_requested,
        }

    def _fingerprint(self, context: ExecutionRunContext) -> str:
        value = {
            "handlerId": context.run.handler_id,
            "payloadHash": context.run.payload_hash,
            "workloadId": self.workload_id,
            "tier": self.options.tier,
            "timeoutSeconds": self.options.timeout_seconds,
        }
        return "sha256:" + sha256(_json_bytes(value)).hexdigest()

    def _validate_seed(self, payload: JSONValue) -> str | None:
        if not self.options.require_seed:
            return None
        if not isinstance(payload, dict) or self.options.seed_field not in payload:
            return (
                "The Extropic workload requires an explicit "
                f"{self.options.seed_field!r} seed."
            )
        seed = payload[self.options.seed_field]
        if isinstance(seed, bool) or not isinstance(seed, (int, str)):
            return "The Extropic workload seed must be an integer or string."
        return None

    def _status_details(
        self,
        job_id: str | None,
        status: str,
    ) -> JSONObject:
        return {
            "provider": "extropic",
            "providerJobId": job_id,
            "providerStatus": status,
            "tier": self.options.tier,
            "workloadId": self.workload_id,
        }

    def _failure(
        self,
        failure_class: str,
        error: str,
        *,
        details: JSONObject | None = None,
    ) -> ExecutionRunResult:
        return ExecutionRunResult(
            status="failed",
            failure_class=failure_class,
            error=error,
            status_details=details,
        )

    def _retry_delay(
        self,
        error: ExtropicRateLimitedError,
        attempt: int,
    ) -> float:
        supplied = error.retry_after_seconds
        if supplied is not None and math.isfinite(supplied) and supplied > 0:
            return min(60.0, supplied)
        return float(min(
            60.0,
            self.options.poll_interval_seconds * (2 ** max(0, attempt - 1)),
        ))


def _snapshot_from(value: object, fallback_job_id: str) -> ExtropicJobSnapshot:
    if not isinstance(value, Mapping):
        raise ExtropicProviderError(
            "Extropic returned an invalid job response."
        )
    raw_progress = value.get("progress")
    progress: JSONValue = None
    if raw_progress is not None:
        try:
            _json_bytes(raw_progress)
            progress = cast(JSONValue, raw_progress)
        except (TypeError, ValueError, OverflowError):
            progress = None
    try:
        return ExtropicJobSnapshot(
            job_id=str(value.get("id") or fallback_job_id),
            status=str(value["status"]),
            progress=progress,
        )
    except (KeyError, TypeError, ValueError) as exc:
        raise ExtropicProviderError(
            "Extropic returned an invalid job response."
        ) from exc


def _progress_fraction(value: JSONValue) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        selected = float(value)
        return selected if math.isfinite(selected) and 0 <= selected <= 1 else None
    if not isinstance(value, dict):
        return None
    for key in ("progress", "fraction"):
        nested = value.get(key)
        if isinstance(nested, bool):
            continue
        if isinstance(nested, (int, float)):
            selected = float(nested)
            if math.isfinite(selected) and 0 <= selected <= 1:
                return selected
    completed = value.get("completed")
    total = value.get("total")
    if (
        not isinstance(completed, bool)
        and isinstance(completed, (int, float))
        and not isinstance(total, bool)
        and isinstance(total, (int, float))
        and math.isfinite(float(completed))
        and math.isfinite(float(total))
        and float(total) > 0
    ):
        return min(1.0, max(0.0, float(completed) / float(total)))
    return None


def _truncate_utf8(value: str, limit: int) -> tuple[str, bool]:
    encoded = value.encode("utf-8")
    if len(encoded) <= limit:
        return value, False
    return encoded[:limit].decode("utf-8", "ignore"), True


def _optional_string(value: object) -> str | None:
    return value if isinstance(value, str) and value else None


def _json_bytes(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")
