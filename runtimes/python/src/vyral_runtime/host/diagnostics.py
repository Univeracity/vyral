from __future__ import annotations

from typing import Mapping, cast

from ..contracts import JSONValue
from ..readiness import RuntimeReadiness


def public_readiness_document(
    readiness: RuntimeReadiness,
) -> dict[str, JSONValue]:
    """Return readiness suitable for a remote host response.

    Runtime-owned readiness retains local diagnostic detail for the process
    owner. The REST and MCP host surfaces intentionally expose only stable
    status, identity, and profile information: storage paths and future
    provider-specific diagnostic values do not cross a network boundary.
    """
    document = readiness.to_dict()
    checks_value = document.get("checks", [])
    checks = (
        cast(list[object], checks_value)
        if isinstance(checks_value, list)
        else []
    )
    warnings = document.get("warnings", [])
    blockers = document.get("blockers", [])
    return {
        "runtime": document["runtime"],
        "runtimeVersion": document["runtimeVersion"],
        "contractVersion": document["contractVersion"],
        "fixtureVersion": document["fixtureVersion"],
        "status": document["status"],
        "maturity": document["maturity"],
        "fullLocalReady": document["fullLocalReady"],
        "contract": document["contract"],
        "profiles": document["profiles"],
        "checks": [
            _public_check(check)
            for check in checks
            if isinstance(check, Mapping)
        ],
        "warnings": (
            ["One or more runtime warnings are present."]
            if isinstance(warnings, list) and warnings
            else []
        ),
        "blockers": (
            ["One or more runtime readiness checks did not pass."]
            if isinstance(blockers, list) and blockers
            else []
        ),
    }


def _public_check(value: Mapping[object, object]) -> dict[str, JSONValue]:
    check_id = value.get("id")
    status = value.get("status")
    check_id_text = check_id if isinstance(check_id, str) else "unknown"
    status_text = status if isinstance(status, str) else "unknown"
    message = value.get("message")
    return {
        "id": check_id_text,
        "status": status_text,
        "message": (
            message
            if status_text == "passed" and isinstance(message, str)
            else "The runtime readiness check did not pass."
        ),
    }


__all__ = ["public_readiness_document"]
