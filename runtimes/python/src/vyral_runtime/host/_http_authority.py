from __future__ import annotations

from dataclasses import dataclass
from ipaddress import IPv4Address, IPv6Address


@dataclass(frozen=True)
class HttpAuthority:
    hostname: str
    port: int | None


def parse_http_authority(value: str) -> HttpAuthority | None:
    """Parse a Host-style authority without accepting URL syntax."""

    if not value or value != value.strip():
        return None
    if any(
        character.isspace()
        or ord(character) < 0x21
        or ord(character) == 0x7F
        or character in "@/?#\\,"
        for character in value
    ):
        return None

    normalized = value.casefold()
    hostname: str
    port: int | None = None
    if normalized.startswith("["):
        closing = normalized.find("]")
        if closing < 0:
            return None
        hostname = normalized[1:closing]
        remainder = normalized[closing + 1 :]
        if remainder:
            if not remainder.startswith(":"):
                return None
            port = _parse_port(remainder[1:])
            if port is None:
                return None
        try:
            hostname = str(IPv6Address(hostname))
        except ValueError:
            return None
    elif normalized.count(":") > 1:
        try:
            hostname = str(IPv6Address(normalized))
        except ValueError:
            return None
    elif ":" in normalized:
        hostname, port_text = normalized.rsplit(":", 1)
        port = _parse_port(port_text)
        if not hostname or port is None:
            return None
    else:
        hostname = normalized

    if not _valid_hostname(hostname):
        return None
    return HttpAuthority(hostname=hostname, port=port)


def host_allowed(host: str, allowed_hosts: frozenset[str]) -> bool:
    candidate = parse_http_authority(host)
    if candidate is None:
        return False
    for value in allowed_hosts:
        allowed = parse_http_authority(value)
        if allowed is None or allowed.hostname != candidate.hostname:
            continue
        if allowed.port is None or allowed.port == candidate.port:
            return True
    return False


def validate_allowed_hosts(allowed_hosts: frozenset[str]) -> None:
    if not allowed_hosts or any(
        parse_http_authority(value) is None for value in allowed_hosts
    ):
        raise ValueError(
            "allowed_hosts must contain valid HTTP host names or authorities."
        )


def _parse_port(value: str) -> int | None:
    if not value.isascii() or not value.isdigit():
        return None
    port = int(value)
    return port if 1 <= port <= 65_535 else None


def _valid_hostname(value: str) -> bool:
    try:
        IPv4Address(value)
        return True
    except ValueError:
        pass
    try:
        IPv6Address(value)
        return True
    except ValueError:
        pass
    if not value.isascii() or len(value) > 253:
        return False
    labels = value.split(".")
    return all(
        label
        and len(label) <= 63
        and label[0].isalnum()
        and label[-1].isalnum()
        and all(character.isalnum() or character == "-" for character in label)
        for label in labels
    )
