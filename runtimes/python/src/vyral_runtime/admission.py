"""Durable, language-neutral admission receipts for asynchronous work."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from hashlib import sha256
from typing import Any


ADMISSION_VERSION = "vyral.admission.v1"
ADMISSION_STATUSES = frozenset({"accepted", "rejected"})


def _text(value: str, name: str) -> str:
    selected = value.strip()
    if not selected:
        raise ValueError(f"{name} is required.")
    return selected


def _timestamp(value: datetime) -> str:
    if value.tzinfo is None:
        raise ValueError("Admission timestamps must include an offset.")
    return value.astimezone(timezone.utc).isoformat().replace(
        "+00:00", "Z"
    )


def _hash(value: str) -> str:
    return sha256(value.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class AdmissionReceipt:
    """Durable proof that one exact request was admitted or rejected."""

    admission_id: str
    operation_id: str
    status: str
    resource_id: str
    request_hash: str
    replayed: bool
    admitted_at_utc: datetime
    status_uri: str
    idempotency_key_hash: str | None = None
    result_uri: str | None = None
    failure_class: str | None = None
    error: str | None = None
    version: str = ADMISSION_VERSION

    def __post_init__(self) -> None:
        _text(self.admission_id, "Admission id")
        _text(self.operation_id, "Admission operation id")
        _text(self.resource_id, "Admission resource id")
        _text(self.status_uri, "Admission status URI")
        if self.status not in ADMISSION_STATUSES:
            raise ValueError(
                f"Unknown admission status {self.status!r}."
            )
        if self.version != ADMISSION_VERSION:
            raise ValueError(
                f"Admission version must be {ADMISSION_VERSION!r}."
            )
        _timestamp(self.admitted_at_utc)
        if self.status == "accepted" and (
            self.failure_class is not None or self.error is not None
        ):
            raise ValueError(
                "Accepted admissions cannot carry rejection details."
            )

    def to_dict(self) -> dict[str, Any]:
        return {
            "version": self.version,
            "admissionId": self.admission_id,
            "operationId": self.operation_id,
            "status": self.status,
            "resourceId": self.resource_id,
            "requestHash": self.request_hash,
            "idempotencyKeyHash": self.idempotency_key_hash,
            "replayed": self.replayed,
            "admittedAtUtc": _timestamp(self.admitted_at_utc),
            "statusUri": self.status_uri,
            "resultUri": self.result_uri,
            "failureClass": self.failure_class,
            "error": self.error,
        }


def create_admission_receipt(
    *,
    operation_id: str,
    resource_id: str,
    request_hash: str,
    idempotency_key: str | None,
    replayed: bool,
    admitted_at_utc: datetime,
    status_uri: str,
    status: str = "accepted",
    result_uri: str | None = None,
    failure_class: str | None = None,
    error: str | None = None,
) -> AdmissionReceipt:
    """Create the deterministic ``vyral.admission.v1`` wire receipt."""

    selected_operation = _text(operation_id, "Admission operation id")
    selected_resource = _text(resource_id, "Admission resource id")
    selected_status_uri = _text(status_uri, "Admission status URI")
    material = (
        f"{ADMISSION_VERSION}\n{selected_operation}\n"
        f"{selected_resource}"
    )
    return AdmissionReceipt(
        admission_id="adm_" + _hash(material),
        operation_id=selected_operation,
        status=status,
        resource_id=selected_resource,
        request_hash=request_hash,
        idempotency_key_hash=(
            _hash(idempotency_key) if idempotency_key else None
        ),
        replayed=replayed,
        admitted_at_utc=admitted_at_utc,
        status_uri=selected_status_uri,
        result_uri=result_uri,
        failure_class=failure_class if status == "rejected" else None,
        error=error if status == "rejected" else None,
    )


__all__ = [
    "ADMISSION_STATUSES",
    "ADMISSION_VERSION",
    "AdmissionReceipt",
    "create_admission_receipt",
]
