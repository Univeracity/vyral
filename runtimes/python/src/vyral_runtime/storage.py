from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import sqlite3

from ._version import RUNTIME_VERSION
from .contracts import JSONValue


STORAGE_SCHEMA_COMPONENT = "portable-local"
STORAGE_SCHEMA_VERSION = 1
_SCHEMA_TABLE = "vyral_py_runtime_schema"


class StorageSchemaError(RuntimeError):
    """Raised when durable local state cannot be safely opened or upgraded."""


@dataclass(frozen=True)
class StorageSchemaReceipt:
    """Evidence for the schema decision made while opening a local runtime."""

    component: str
    from_version: int
    to_version: int
    applied_versions: tuple[int, ...]
    migrated_by_runtime_version: str
    database_preexisting: bool
    legacy_table_count: int

    @property
    def upgraded(self) -> bool:
        return bool(self.applied_versions)

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "component": self.component,
            "fromVersion": self.from_version,
            "toVersion": self.to_version,
            "appliedVersions": list(self.applied_versions),
            "migratedByRuntimeVersion": self.migrated_by_runtime_version,
            "databasePreexisting": self.database_preexisting,
            "legacyTableCount": self.legacy_table_count,
            "upgraded": self.upgraded,
        }


def ensure_storage_schema(
    database_path: str | Path,
    *,
    busy_timeout_ms: int = 5_000,
) -> StorageSchemaReceipt:
    """Atomically adopt or upgrade the composed portable-local schema.

    Version zero represents either a new database or a Python-runtime database
    created before the composition-level schema ledger existed. Individual
    stores retain ownership of their tables; this ledger is the fail-closed
    boundary that prevents an older runtime from opening a newer schema.
    """

    if (
        isinstance(busy_timeout_ms, bool)
        or not isinstance(busy_timeout_ms, int)
        or busy_timeout_ms < 0
    ):
        raise ValueError("busy_timeout_ms must be a non-negative integer.")
    path = Path(database_path).expanduser().resolve()
    if not path.name:
        raise ValueError("Storage SQLite database path is required.")
    path.parent.mkdir(parents=True, exist_ok=True)
    database_preexisting = path.is_file() and path.stat().st_size > 0
    connection = sqlite3.connect(
        path,
        timeout=busy_timeout_ms / 1000,
        isolation_level=None,
    )
    connection.row_factory = sqlite3.Row
    try:
        connection.execute(f"PRAGMA busy_timeout={busy_timeout_ms}")
        connection.execute("BEGIN IMMEDIATE")
        legacy_table_count = _legacy_table_count(connection)
        connection.execute(
            f"""
            CREATE TABLE IF NOT EXISTS {_SCHEMA_TABLE} (
                component TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                migrated_by_runtime_version TEXT NOT NULL,
                CHECK (schema_version >= 0)
            )
            """
        )
        row = connection.execute(
            f"""
            SELECT schema_version, migrated_by_runtime_version
            FROM {_SCHEMA_TABLE}
            WHERE component = ?
            """,
            (STORAGE_SCHEMA_COMPONENT,),
        ).fetchone()
        from_version = 0 if row is None else _stored_version(row)
        if from_version > STORAGE_SCHEMA_VERSION:
            raise StorageSchemaError(
                "The local database uses portable-local storage schema "
                f"{from_version}, but this runtime supports at most "
                f"{STORAGE_SCHEMA_VERSION}. Upgrade the Vyral runtime before "
                "opening this database."
            )

        applied_versions: list[int] = []
        for version in range(from_version + 1, STORAGE_SCHEMA_VERSION + 1):
            _apply_migration(connection, version)
            applied_versions.append(version)

        if applied_versions:
            connection.execute(
                f"""
                INSERT INTO {_SCHEMA_TABLE}(
                    component,
                    schema_version,
                    migrated_by_runtime_version
                )
                VALUES (?, ?, ?)
                ON CONFLICT(component) DO UPDATE SET
                    schema_version = excluded.schema_version,
                    migrated_by_runtime_version =
                        excluded.migrated_by_runtime_version
                """,
                (
                    STORAGE_SCHEMA_COMPONENT,
                    STORAGE_SCHEMA_VERSION,
                    RUNTIME_VERSION,
                ),
            )
            migrated_by = RUNTIME_VERSION
        else:
            assert row is not None
            migrated_by = str(row["migrated_by_runtime_version"])
            if not migrated_by.strip():
                raise StorageSchemaError(
                    "The local storage schema ledger has an empty runtime "
                    "version."
                )
        connection.commit()
        return StorageSchemaReceipt(
            component=STORAGE_SCHEMA_COMPONENT,
            from_version=from_version,
            to_version=STORAGE_SCHEMA_VERSION,
            applied_versions=tuple(applied_versions),
            migrated_by_runtime_version=migrated_by,
            database_preexisting=database_preexisting,
            legacy_table_count=legacy_table_count,
        )
    except StorageSchemaError:
        if connection.in_transaction:
            connection.rollback()
        raise
    except (OverflowError, sqlite3.DatabaseError, TypeError, ValueError) as exc:
        if connection.in_transaction:
            connection.rollback()
        raise StorageSchemaError(
            f"The local storage schema ledger is invalid: {exc}"
        ) from exc
    finally:
        connection.close()


def _legacy_table_count(connection: sqlite3.Connection) -> int:
    row = connection.execute(
        """
        SELECT COUNT(*)
        FROM sqlite_master
        WHERE type = 'table'
          AND name LIKE 'vyral_py_%'
          AND name <> ?
        """,
        (_SCHEMA_TABLE,),
    ).fetchone()
    assert row is not None
    return int(row[0])


def _stored_version(row: sqlite3.Row) -> int:
    value: object = row["schema_version"]
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise StorageSchemaError(
            "The local storage schema ledger contains an invalid version."
        )
    return value


def _apply_migration(
    connection: sqlite3.Connection,
    version: int,
) -> None:
    if version == 1:
        # Version 1 establishes the composition-level ledger. Existing 0.1.0
        # store tables already have the required durable shape and are adopted
        # transactionally by recording the version after validation.
        return
    raise StorageSchemaError(
        f"No portable-local storage migration is registered for version {version}."
    )


__all__ = [
    "STORAGE_SCHEMA_COMPONENT",
    "STORAGE_SCHEMA_VERSION",
    "StorageSchemaError",
    "StorageSchemaReceipt",
    "ensure_storage_schema",
]
