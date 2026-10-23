"""Read-only local runtime inspection with a source-tree invariant."""

from __future__ import annotations

from pathlib import Path
import shutil
import sqlite3
import tempfile

from .embeddings import EmbeddingProviderRegistry
from .readiness import get_readiness
from .runtime import LocalRuntimeConfig
from .storage import (
    STORAGE_SCHEMA_COMPONENT,
    STORAGE_SCHEMA_VERSION,
    _SCHEMA_TABLE as _STORAGE_SCHEMA_TABLE,
)


_INSPECTION_SCHEMA = "vyral.local-runtime-inspection.v1"
_RECORD_TABLES = frozenset(
    {
        "vyral_py_collections",
        "vyral_py_metadata",
        "vyral_py_metadata_index",
        "vyral_py_record_fts",
        "vyral_py_records",
        "vyral_py_vectors",
    }
)
_CANONICAL_TABLES = frozenset(
    {
        "vyral_canonical_document_indexes",
        "vyral_canonical_documents",
        "vyral_canonical_fences",
        "vyral_canonical_migrations",
        "vyral_canonical_outbox",
        "vyral_canonical_revisions",
        "vyral_canonical_tenant_lifecycle",
        "vyral_canonical_tenant_purges",
        "vyral_canonical_transactions",
    }
)
_EXECUTION_TABLES = frozenset(
    {
        "vyral_execution_artifacts",
        "vyral_execution_checkpoints",
        "vyral_execution_external_events",
        "vyral_execution_history",
        "vyral_execution_leases",
        "vyral_execution_runs",
        "vyral_execution_timers",
        "vyral_execution_wait_outcomes",
        "vyral_execution_waits",
    }
)
_ACTIVE_EXECUTION_STATUSES = ("queued", "waiting", "running")
_OBJECT_METADATA_SUFFIX = ".metadata.json"
_OBJECT_TEMP_DIRECTORY = ".vyral-tmp"


def inspect_local_runtime(root_path: str | Path) -> dict[str, object]:
    requested = Path(root_path).expanduser()
    if requested.is_symlink():
        raise ValueError("The local runtime directory must not be a symbolic link.")
    root = requested.resolve()
    if not root.is_dir():
        raise ValueError(f"Local runtime directory does not exist: {root}")
    config = LocalRuntimeConfig(root_path=root)
    database_path = config.database_path
    object_root_path = config.object_root_path
    if database_path.is_symlink():
        raise ValueError("The local runtime database must not be a symbolic link.")
    if not database_path.is_file() or database_path.stat().st_size == 0:
        raise ValueError(
            "The local runtime is not initialized: expected a non-empty "
            f"database at {database_path}. Inspection never creates or "
            "migrates runtime state."
        )

    sqlite_diagnostics = _inspect_sqlite_database(
        database_path,
        busy_timeout_ms=config.busy_timeout_ms,
        max_active_runs=config.execution_max_active_runs,
    )
    object_diagnostics = _inspect_object_root(object_root_path)
    readiness = get_readiness()
    registry = EmbeddingProviderRegistry()
    provider = registry.create(config.embedding_options)
    descriptor = next(
        item
        for item in registry.get_providers()
        if item.provider == provider.provider_id
    )

    records = sqlite_diagnostics["records"]
    canonical = sqlite_diagnostics["canonical"]
    execution = sqlite_diagnostics["execution"]
    assert isinstance(records, dict)
    assert isinstance(canonical, dict)
    assert isinstance(execution, dict)
    local_blockers: list[str] = []
    if not bool(records["healthy"]):
        local_blockers.append("SQLite local storage diagnostics failed.")
    if not bool(canonical["healthy"]):
        local_blockers.append("SQLite CanonicalStore diagnostics failed.")
    if not bool(execution["healthy"]):
        local_blockers.append(
            "SQLite durable execution diagnostics failed."
        )
    if not bool(object_diagnostics["healthy"]):
        local_blockers.append("Filesystem object storage diagnostics failed.")
    blockers = list(dict.fromkeys((*readiness.blockers, *local_blockers)))
    warnings = list(dict.fromkeys(readiness.warnings))

    return {
        "schema": _INSPECTION_SCHEMA,
        "readOnly": True,
        "rootPath": str(root),
        "topology": "local-single-node",
        "storageSchema": sqlite_diagnostics["storageSchema"],
        "runtime": {
            "version": readiness.runtime_version,
            "contractVersion": readiness.contract_version,
            "status": "blocked" if blockers else readiness.status,
            "maturity": readiness.maturity,
            "fullLocalReady": readiness.full_local_ready,
        },
        "providers": {
            "records": {
                "adapter": "SQLiteRecordStore",
                "databasePath": str(database_path),
                **records,
            },
            "canonical": {
                "adapter": "SQLiteCanonicalStore",
                "databasePath": str(database_path),
                **canonical,
            },
            "objects": {
                "adapter": "FileObjectStore",
                "rootPath": str(object_root_path),
                **object_diagnostics,
            },
            "embeddings": {
                "provider": provider.provider_id,
                "model": provider.model_id,
                "dimensions": provider.dimensions,
                "semanticQuality": descriptor.semantic_quality,
                "requiresNetwork": descriptor.requires_network,
            },
            "execution": {
                "adapter": "python-local-sqlite",
                **execution,
            },
        },
        "warnings": warnings,
        "blockers": blockers,
    }


def _inspect_sqlite_database(
    database_path: Path,
    *,
    busy_timeout_ms: int,
    max_active_runs: int,
) -> dict[str, object]:
    wal_path = Path(f"{database_path}-wal")
    if wal_path.is_symlink():
        raise ValueError("The local runtime SQLite WAL must not be a symbolic link.")
    if wal_path.is_file() and wal_path.stat().st_size > 0:
        before = _sqlite_source_signature(database_path)
        with tempfile.TemporaryDirectory(prefix="vyral-inspect-") as temporary:
            snapshot = Path(temporary) / database_path.name
            shutil.copyfile(database_path, snapshot)
            shutil.copyfile(wal_path, Path(f"{snapshot}-wal"))
            if before != _sqlite_source_signature(database_path):
                raise RuntimeError(
                    "The local runtime changed while its read-only inspection "
                    "snapshot was being captured. Retry after active writers "
                    "are quiet."
                )
            return _inspect_sqlite_image(
                snapshot,
                busy_timeout_ms=busy_timeout_ms,
                max_active_runs=max_active_runs,
                immutable=False,
            )

    before = _sqlite_source_signature(database_path)
    inspection = _inspect_sqlite_image(
        database_path,
        busy_timeout_ms=busy_timeout_ms,
        max_active_runs=max_active_runs,
        immutable=True,
    )
    if before != _sqlite_source_signature(database_path):
        raise RuntimeError(
            "The local runtime changed during read-only inspection. Retry "
            "after active writers are quiet."
        )
    return inspection


def _sqlite_source_signature(
    database_path: Path,
) -> tuple[tuple[object, ...], ...]:
    signature: list[tuple[object, ...]] = []
    for path in (database_path, Path(f"{database_path}-wal")):
        if path.is_file():
            metadata = path.stat()
            signature.append(
                (
                    path.name,
                    metadata.st_dev,
                    metadata.st_ino,
                    metadata.st_size,
                    metadata.st_mtime_ns,
                )
            )
        else:
            signature.append((path.name, None))
    return tuple(signature)


def _sqlite_journal_mode(database_path: Path) -> str:
    with database_path.open("rb") as database:
        header = database.read(20)
    if len(header) < 20 or header[:16] != b"SQLite format 3\x00":
        raise ValueError("The local runtime database has an invalid SQLite header.")
    write_version = header[18]
    read_version = header[19]
    if read_version == 2 and write_version == 2:
        return "wal"
    if read_version == 1 and write_version == 1:
        return "delete"
    return "unknown"


def _inspect_sqlite_image(
    database_path: Path,
    *,
    busy_timeout_ms: int,
    max_active_runs: int,
    immutable: bool,
) -> dict[str, object]:
    query = "mode=ro&immutable=1" if immutable else "mode=ro"
    uri = f"{database_path.as_uri()}?{query}"
    journal_mode = _sqlite_journal_mode(database_path)
    try:
        connection = sqlite3.connect(
            uri,
            uri=True,
            timeout=busy_timeout_ms / 1_000,
            isolation_level=None,
        )
    except sqlite3.Error as error:
        raise RuntimeError(
            f"The local runtime database could not be opened read-only: {error}"
        ) from error
    connection.row_factory = sqlite3.Row
    try:
        connection.execute(f"PRAGMA busy_timeout={busy_timeout_ms}")
        connection.execute("PRAGMA query_only=ON")
        connection.execute("PRAGMA foreign_keys=ON")
        query_only = bool(connection.execute("PRAGMA query_only").fetchone()[0])
        if not query_only:
            raise RuntimeError(
                "SQLite did not honor the read-only inspection boundary."
            )

        tables = frozenset(
            str(row[0])
            for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type = 'table'"
            )
        )
        schema = _inspect_storage_schema(connection, tables)
        quick_rows = tuple(
            str(row[0]) for row in connection.execute("PRAGMA quick_check")
        )
        quick_check = (
            "ok"
            if quick_rows == ("ok",)
            else "; ".join(quick_rows) or "no result"
        )
        foreign_key_violations = tuple(
            connection.execute("PRAGMA foreign_key_check")
        )
        common_healthy = (
            quick_check.casefold() == "ok"
            and not foreign_key_violations
            and journal_mode.casefold() == "wal"
        )
        missing_record_tables = sorted(_RECORD_TABLES - tables)
        missing_canonical_tables = sorted(_CANONICAL_TABLES - tables)
        missing_execution_tables = sorted(_EXECUTION_TABLES - tables)
        active_runs = 0
        if "vyral_execution_runs" in tables:
            placeholders = ", ".join("?" for _ in _ACTIVE_EXECUTION_STATUSES)
            row = connection.execute(
                "SELECT COUNT(*) FROM vyral_execution_runs "
                f"WHERE status IN ({placeholders})",
                _ACTIVE_EXECUTION_STATUSES,
            ).fetchone()
            if row is None:
                raise RuntimeError("SQLite did not report active run count.")
            active_runs = int(row[0])
        database_bytes = database_path.stat().st_size
        common = {
            "quickCheck": quick_check,
            "foreignKeyViolationCount": len(foreign_key_violations),
            "journalMode": journal_mode,
        }
        return {
            "storageSchema": schema,
            "records": {
                "healthy": common_healthy and not missing_record_tables,
                **common,
                "fts5Available": "vyral_py_record_fts" in tables,
                "missingTables": missing_record_tables,
                "databaseBytes": database_bytes,
            },
            "canonical": {
                "healthy": common_healthy and not missing_canonical_tables,
                **common,
                "missingTables": missing_canonical_tables,
            },
            "execution": {
                "healthy": common_healthy and not missing_execution_tables,
                **common,
                "missingTables": missing_execution_tables,
                "activeRuns": active_runs,
                "maxActiveRuns": max_active_runs,
            },
        }
    except ValueError:
        raise
    except (OverflowError, sqlite3.Error, TypeError) as error:
        raise RuntimeError(
            f"The local runtime database could not be inspected read-only: {error}"
        ) from error
    finally:
        connection.close()


def _inspect_storage_schema(
    connection: sqlite3.Connection,
    tables: frozenset[str],
) -> dict[str, object]:
    if _STORAGE_SCHEMA_TABLE not in tables:
        raise ValueError(
            "The local runtime is not initialized with the portable-local "
            "storage schema ledger. Inspection never adopts or migrates "
            "legacy state."
        )
    row = connection.execute(
        f"""
        SELECT schema_version, migrated_by_runtime_version
        FROM {_STORAGE_SCHEMA_TABLE}
        WHERE component = ?
        """,
        (STORAGE_SCHEMA_COMPONENT,),
    ).fetchone()
    if row is None:
        raise ValueError(
            "The local runtime storage schema ledger has no portable-local "
            "entry. Inspection never initializes or migrates state."
        )
    version: object = row["schema_version"]
    migrated_by = row["migrated_by_runtime_version"]
    if isinstance(version, bool) or not isinstance(version, int) or version < 0:
        raise ValueError(
            "The local runtime storage schema ledger contains an invalid version."
        )
    if not isinstance(migrated_by, str) or not migrated_by.strip():
        raise ValueError(
            "The local runtime storage schema ledger has an empty runtime version."
        )
    if version > STORAGE_SCHEMA_VERSION:
        raise ValueError(
            "The local runtime uses portable-local storage schema "
            f"{version}, but this runtime supports at most "
            f"{STORAGE_SCHEMA_VERSION}. Upgrade Vyral before inspecting it."
        )
    if version < STORAGE_SCHEMA_VERSION:
        raise ValueError(
            "The local runtime uses portable-local storage schema "
            f"{version}, but inspection requires current schema "
            f"{STORAGE_SCHEMA_VERSION}. Open it through Vyral's runtime "
            "migration path before inspecting it."
        )
    return {
        "component": STORAGE_SCHEMA_COMPONENT,
        "version": version,
        "supportedVersion": STORAGE_SCHEMA_VERSION,
        "migratedByRuntimeVersion": migrated_by,
        "status": "current",
    }


def _inspect_object_root(root_path: Path) -> dict[str, object]:
    empty = {
        "containerCount": 0,
        "objectCount": 0,
        "metadataSidecarCount": 0,
        "missingMetadataCount": 0,
        "orphanMetadataCount": 0,
        "temporaryFileCount": 0,
        "symbolicLinkCount": 0,
        "contentBytes": 0,
        "temporaryBytes": 0,
    }
    if root_path.is_symlink():
        return {
            "healthy": False,
            "rootExists": root_path.exists(),
            **empty,
            "symbolicLinkCount": 1,
        }
    if not root_path.is_dir():
        return {"healthy": False, "rootExists": root_path.exists(), **empty}

    containers = tuple(
        path
        for path in root_path.iterdir()
        if not path.is_symlink()
        and path.is_dir()
        and path.name != _OBJECT_TEMP_DIRECTORY
    )
    object_count = 0
    sidecars = 0
    missing = 0
    orphan = 0
    temporary = 0
    symlinks = 0
    content_bytes = 0
    temporary_bytes = 0
    for path in root_path.rglob("*"):
        if path.is_symlink():
            symlinks += 1
            continue
        if not path.is_file():
            continue
        relative_parts = path.relative_to(root_path).parts
        if _OBJECT_TEMP_DIRECTORY in relative_parts:
            temporary += 1
            temporary_bytes += path.stat().st_size
        elif path.name.endswith(_OBJECT_METADATA_SUFFIX):
            sidecars += 1
            content_path = Path(str(path)[: -len(_OBJECT_METADATA_SUFFIX)])
            if content_path.is_symlink() or not content_path.is_file():
                orphan += 1
        else:
            object_count += 1
            content_bytes += path.stat().st_size
            metadata_path = Path(str(path) + _OBJECT_METADATA_SUFFIX)
            if metadata_path.is_symlink() or not metadata_path.is_file():
                missing += 1
    return {
        "healthy": not (missing or orphan or temporary or symlinks),
        "rootExists": True,
        "containerCount": len(containers),
        "objectCount": object_count,
        "metadataSidecarCount": sidecars,
        "missingMetadataCount": missing,
        "orphanMetadataCount": orphan,
        "temporaryFileCount": temporary,
        "symbolicLinkCount": symlinks,
        "contentBytes": content_bytes,
        "temporaryBytes": temporary_bytes,
    }


__all__ = ["inspect_local_runtime"]
