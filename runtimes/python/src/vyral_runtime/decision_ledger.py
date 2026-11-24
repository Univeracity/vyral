"""Explicit-path local storage for privacy-minimized AI decision receipts."""

from __future__ import annotations

from datetime import datetime
import json
import os
from pathlib import Path
import sqlite3
from typing import Any, Mapping, Sequence

from .decisions import (
    DecisionPolicyError,
    create_decision_ledger_entry,
    validate_decision_ledger_entry,
    verify_decision_ledger_chain,
)


SQLITE_LEDGER_SCHEMA = "vyral.ai-decision-ledger.sqlite.v1"
LEDGER_VERIFICATION_SCHEMA = (
    "vyral.local-ai-decision-ledger-verification.v1"
)
MAX_LEDGER_ENTRIES = 100_000
MAX_ENTRY_BYTES = 1024 * 1024
_APPLICATION_ID = 0x5659444C
_USER_VERSION = 1
_EXPECTED_TABLES = frozenset(
    {"vyral_decision_ledger_meta", "vyral_decision_ledger_entries"}
)
_EXPECTED_SCHEMA_OBJECTS = frozenset(
    {
        ("table", "vyral_decision_ledger_meta"),
        ("table", "vyral_decision_ledger_entries"),
        ("index", "vyral_decision_ledger_policy_outcome"),
    }
)


class DecisionLedgerError(ValueError):
    """Raised when local ledger state is unsafe, invalid, or inconsistent."""


class DecisionLedger:
    """A bounded SQLite ledger containing decision receipts and no source bodies."""

    def __init__(
        self,
        path: str | os.PathLike[str],
        *,
        create: bool = True,
    ) -> None:
        selected = Path(path).expanduser()
        if create:
            selected.parent.mkdir(parents=True, exist_ok=True)
        self.path = selected.parent.resolve() / selected.name
        if self.path.is_symlink():
            raise DecisionLedgerError("ledger path cannot be a symbolic link")
        existed = self.path.exists()
        if not existed and not create:
            raise DecisionLedgerError("ledger file does not exist")
        if existed and not self.path.is_file():
            raise DecisionLedgerError("ledger path must be a file")
        if not existed:
            try:
                descriptor = os.open(
                    self.path,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                    0o600,
                )
            except FileExistsError:
                raise DecisionLedgerError(
                    "ledger path changed during creation; retry explicitly"
                ) from None
            except OSError as error:
                raise DecisionLedgerError(
                    "ledger file could not be created safely"
                ) from error
            else:
                os.close(descriptor)
        try:
            self._connection = sqlite3.connect(
                self.path.as_uri() + "?mode=rw",
                timeout=5,
                isolation_level=None,
                uri=True,
            )
            self._connection.row_factory = sqlite3.Row
            self._configure()
            self._ensure_schema()
        except (OSError, sqlite3.Error, DecisionLedgerError) as error:
            connection = getattr(self, "_connection", None)
            if connection is not None:
                connection.close()
            if isinstance(error, DecisionLedgerError):
                raise
            raise DecisionLedgerError(
                "ledger SQLite state could not be initialized"
            ) from error
        if not existed and os.name != "nt":
            self.path.chmod(0o600)

    def __enter__(self) -> DecisionLedger:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def close(self) -> None:
        self._connection.close()

    def append(
        self,
        receipt: Mapping[str, Any],
        *,
        recorded_at: datetime | None = None,
    ) -> dict[str, Any]:
        """Append one receipt after verifying the complete existing chain."""
        try:
            self._connection.execute("BEGIN IMMEDIATE")
            integrity_errors = self._database_integrity_errors()
            if integrity_errors:
                raise DecisionLedgerError("; ".join(integrity_errors))
            entries = self._read_all()
            self._raise_for_chain_errors(entries)
            if len(entries) >= MAX_LEDGER_ENTRIES:
                raise DecisionLedgerError(
                    f"ledger cannot exceed {MAX_LEDGER_ENTRIES} entries"
                )
            previous = entries[-1]["entryHash"] if entries else None
            entry = create_decision_ledger_entry(
                receipt,
                sequence=len(entries),
                previous_entry_hash=(
                    previous if isinstance(previous, str) else None
                ),
                recorded_at=recorded_at,
            )
            encoded = _encode_entry(entry)
            self._connection.execute(
                """
                INSERT INTO vyral_decision_ledger_entries (
                    sequence,
                    entry_hash,
                    previous_entry_hash,
                    receipt_hash,
                    recorded_at_utc,
                    policy_id,
                    outcome,
                    entry_json
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    entry["sequence"],
                    entry["entryHash"],
                    entry["previousEntryHash"],
                    entry["receiptHash"],
                    entry["recordedAtUtc"],
                    entry["receipt"]["policyId"],
                    entry["receipt"]["outcome"],
                    encoded,
                ),
            )
            stored = self._read_all()
            self._raise_for_chain_errors(stored)
            if (
                len(stored) != len(entries) + 1
                or stored[-1]["entryHash"] != entry["entryHash"]
            ):
                raise DecisionLedgerError(
                    "ledger append did not retain the exact entry"
                )
            self._connection.execute("COMMIT")
            return stored[-1]
        except (DecisionPolicyError, sqlite3.Error, DecisionLedgerError) as error:
            if self._connection.in_transaction:
                self._connection.execute("ROLLBACK")
            if isinstance(error, DecisionLedgerError):
                raise
            raise DecisionLedgerError(str(error)) from error

    def get(
        self,
        *,
        sequence: int | None = None,
        entry_hash: str | None = None,
    ) -> dict[str, Any] | None:
        """Read one entry by exactly one stable local identity."""
        if (sequence is None) == (entry_hash is None):
            raise DecisionLedgerError(
                "select exactly one of sequence or entry_hash"
            )
        entries = self._verified_entries()
        for entry in entries:
            if sequence is not None and entry["sequence"] == sequence:
                return entry
            if entry_hash is not None and entry["entryHash"] == entry_hash:
                return entry
        return None

    def list_entries(
        self,
        *,
        limit: int = 20,
        policy_id: str | None = None,
        outcome: str | None = None,
    ) -> list[dict[str, Any]]:
        """Return newest-first privacy-minimized entry summaries."""
        if not 1 <= limit <= 1000:
            raise DecisionLedgerError("limit must be between 1 and 1000")
        if policy_id is not None and not policy_id.strip():
            raise DecisionLedgerError("policy_id must be non-empty")
        if outcome is not None and outcome not in {
            "accept",
            "reject",
            "escalate",
        }:
            raise DecisionLedgerError(
                "outcome must be accept, reject, or escalate"
            )
        entries = self._verified_entries()
        selected: list[dict[str, Any]] = []
        for entry in reversed(entries):
            receipt = entry["receipt"]
            if policy_id is not None and receipt["policyId"] != policy_id:
                continue
            if outcome is not None and receipt["outcome"] != outcome:
                continue
            selected.append(
                {
                    "sequence": entry["sequence"],
                    "entryHash": entry["entryHash"],
                    "previousEntryHash": entry["previousEntryHash"],
                    "receiptHash": entry["receiptHash"],
                    "recordedAtUtc": entry["recordedAtUtc"],
                    "evaluatedAtUtc": receipt["evaluatedAtUtc"],
                    "policyId": receipt["policyId"],
                    "policyVersion": receipt["policyVersion"],
                    "questionId": receipt["questionId"],
                    "outcome": receipt["outcome"],
                    "reason": receipt["reason"],
                    "provider": receipt["provider"],
                    "modelId": receipt["modelId"],
                }
            )
            if len(selected) == limit:
                break
        return selected

    def verify(
        self,
        *,
        expected_head: str | None = None,
        expected_count: int | None = None,
    ) -> dict[str, Any]:
        """Verify storage columns, entry hashes, links, and optional checkpoint."""
        errors: list[str] = []
        entries: list[dict[str, Any]] = []
        errors.extend(self._database_integrity_errors())
        try:
            entries = self._read_all()
        except DecisionLedgerError as error:
            errors.append(str(error))
        errors.extend(verify_decision_ledger_chain(entries))
        head = entries[-1]["entryHash"] if entries else None
        if expected_head is not None and head != expected_head:
            errors.append("ledger head does not match expected_head")
        if expected_count is not None and len(entries) != expected_count:
            errors.append("ledger entry count does not match expected_count")
        return {
            "schema": LEDGER_VERIFICATION_SCHEMA,
            "valid": not errors,
            "entryCount": len(entries),
            "headEntryHash": head,
            "expectedHeadEntryHash": expected_head,
            "expectedEntryCount": expected_count,
            "errors": errors,
        }

    def _configure(self) -> None:
        self._connection.execute("PRAGMA trusted_schema = OFF")
        self._connection.execute("PRAGMA foreign_keys = ON")
        self._connection.execute("PRAGMA busy_timeout = 5000")
        self._connection.execute("PRAGMA synchronous = FULL")
        self._connection.execute("PRAGMA journal_mode = DELETE")

    def _ensure_schema(self) -> None:
        rows = self._connection.execute(
            """
            SELECT type, name
            FROM sqlite_master
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """
        ).fetchall()
        objects = frozenset(
            (str(row["type"]), str(row["name"])) for row in rows
        )
        existing = frozenset(
            name for kind, name in objects if kind == "table"
        )
        if not objects:
            self._connection.execute("BEGIN IMMEDIATE")
            try:
                self._connection.execute(
                    """
                    CREATE TABLE vyral_decision_ledger_meta (
                        schema_version TEXT PRIMARY KEY NOT NULL
                    ) STRICT
                    """
                )
                self._connection.execute(
                    """
                    CREATE TABLE vyral_decision_ledger_entries (
                        sequence INTEGER PRIMARY KEY,
                        entry_hash TEXT UNIQUE NOT NULL,
                        previous_entry_hash TEXT,
                        receipt_hash TEXT NOT NULL,
                        recorded_at_utc TEXT NOT NULL,
                        policy_id TEXT NOT NULL,
                        outcome TEXT NOT NULL,
                        entry_json TEXT NOT NULL,
                        CHECK (sequence >= 0),
                        CHECK (outcome IN ('accept', 'reject', 'escalate')),
                        CHECK (
                            (sequence = 0 AND previous_entry_hash IS NULL)
                            OR
                            (sequence > 0 AND previous_entry_hash IS NOT NULL)
                        )
                    ) STRICT
                    """
                )
                self._connection.execute(
                    """
                    CREATE INDEX vyral_decision_ledger_policy_outcome
                    ON vyral_decision_ledger_entries (
                        policy_id,
                        outcome,
                        sequence DESC
                    )
                    """
                )
                self._connection.execute(
                    """
                    INSERT INTO vyral_decision_ledger_meta (schema_version)
                    VALUES (?)
                    """,
                    (SQLITE_LEDGER_SCHEMA,),
                )
                self._connection.execute(
                    f"PRAGMA application_id = {_APPLICATION_ID}"
                )
                self._connection.execute(
                    f"PRAGMA user_version = {_USER_VERSION}"
                )
                self._connection.execute("COMMIT")
            except sqlite3.Error:
                self._connection.execute("ROLLBACK")
                raise
            return
        if (
            existing != _EXPECTED_TABLES
            or objects != _EXPECTED_SCHEMA_OBJECTS
        ):
            raise DecisionLedgerError(
                "ledger file contains an unknown or incomplete schema object set"
            )
        application_id = int(
            self._connection.execute("PRAGMA application_id").fetchone()[0]
        )
        user_version = int(
            self._connection.execute("PRAGMA user_version").fetchone()[0]
        )
        if (
            application_id != _APPLICATION_ID
            or user_version != _USER_VERSION
        ):
            raise DecisionLedgerError("ledger SQLite identity is invalid")
        versions = self._connection.execute(
            "SELECT schema_version FROM vyral_decision_ledger_meta"
        ).fetchall()
        if (
            len(versions) != 1
            or versions[0]["schema_version"] != SQLITE_LEDGER_SCHEMA
        ):
            raise DecisionLedgerError("ledger schema version is invalid")

    def _read_all(self) -> list[dict[str, Any]]:
        try:
            rows = self._connection.execute(
                """
                SELECT
                    sequence,
                    entry_hash,
                    previous_entry_hash,
                    receipt_hash,
                    recorded_at_utc,
                    policy_id,
                    outcome,
                    entry_json
                FROM vyral_decision_ledger_entries
                ORDER BY sequence
                LIMIT ?
                """,
                (MAX_LEDGER_ENTRIES + 1,),
            ).fetchall()
        except sqlite3.Error as error:
            raise DecisionLedgerError(
                "ledger entry table cannot be read"
            ) from error
        if len(rows) > MAX_LEDGER_ENTRIES:
            raise DecisionLedgerError(
                f"ledger exceeds the {MAX_LEDGER_ENTRIES}-entry local limit"
            )
        return [_decode_row(row) for row in rows]

    def _verified_entries(self) -> list[dict[str, Any]]:
        integrity_errors = self._database_integrity_errors()
        if integrity_errors:
            raise DecisionLedgerError("; ".join(integrity_errors))
        entries = self._read_all()
        self._raise_for_chain_errors(entries)
        return entries

    def _database_integrity_errors(self) -> list[str]:
        try:
            rows = self._connection.execute("PRAGMA quick_check(1)").fetchall()
        except sqlite3.Error:
            return ["ledger SQLite integrity check could not run"]
        findings = [str(row[0]) for row in rows]
        if findings == ["ok"]:
            return []
        return [
            "ledger SQLite integrity check failed: " + finding
            for finding in findings
        ]

    @staticmethod
    def _raise_for_chain_errors(
        entries: Sequence[Mapping[str, Any]],
    ) -> None:
        errors = verify_decision_ledger_chain(entries)
        if errors:
            raise DecisionLedgerError("; ".join(errors))


def _encode_entry(entry: Mapping[str, Any]) -> str:
    encoded = json.dumps(
        entry,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    )
    if len(encoded.encode("utf-8")) > MAX_ENTRY_BYTES:
        raise DecisionLedgerError(
            f"ledger entry exceeds the {MAX_ENTRY_BYTES}-byte local limit"
        )
    return encoded


def _decode_row(row: sqlite3.Row) -> dict[str, Any]:
    encoded = row["entry_json"]
    if not isinstance(encoded, str):
        raise DecisionLedgerError("ledger entry JSON is not text")
    if len(encoded.encode("utf-8")) > MAX_ENTRY_BYTES:
        raise DecisionLedgerError("ledger entry exceeds the local byte limit")
    try:
        value = json.loads(encoded)
    except (json.JSONDecodeError, UnicodeError) as error:
        raise DecisionLedgerError("ledger entry contains invalid JSON") from error
    if not isinstance(value, dict):
        raise DecisionLedgerError("ledger entry JSON must be an object")
    errors = validate_decision_ledger_entry(value)
    if errors:
        raise DecisionLedgerError("; ".join(errors))
    receipt = value["receipt"]
    comparisons = {
        "sequence": value["sequence"],
        "entry_hash": value["entryHash"],
        "previous_entry_hash": value["previousEntryHash"],
        "receipt_hash": value["receiptHash"],
        "recorded_at_utc": value["recordedAtUtc"],
        "policy_id": receipt["policyId"],
        "outcome": receipt["outcome"],
    }
    for column, expected in comparisons.items():
        if row[column] != expected:
            raise DecisionLedgerError(
                f"ledger column {column} does not match entry JSON"
            )
    return value


__all__ = [
    "DecisionLedger",
    "DecisionLedgerError",
    "LEDGER_VERIFICATION_SCHEMA",
    "MAX_ENTRY_BYTES",
    "MAX_LEDGER_ENTRIES",
    "SQLITE_LEDGER_SCHEMA",
]
