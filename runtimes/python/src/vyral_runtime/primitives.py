from __future__ import annotations

from hashlib import sha256


def _require_text(value: object, name: str) -> str:
    if not isinstance(value, str):
        raise TypeError(f"{name} must be a string")
    return value


def sha256_utf8(text: str) -> str:
    """Return the portable Vyral SHA-256 shape for UTF-8 text."""

    material = _require_text(text, "text").encode("utf-8")
    return "sha256:" + sha256(material).hexdigest()


def canonical_transaction_id(tenant_id: str, idempotency_key: str) -> str:
    """Match CanonicalTransactionHasher.CreateTransactionId."""

    tenant = _require_text(tenant_id, "tenant_id").strip()
    key = _require_text(idempotency_key, "idempotency_key").strip()
    digest = sha256(f"{tenant}\n{key}".encode("utf-8")).hexdigest()
    return "ctx_" + digest[:32]


def hash_lease_token(token: str) -> str:
    """Match the non-reversible CanonicalStore lease-token hash."""

    return sha256_utf8(_require_text(token, "token"))
