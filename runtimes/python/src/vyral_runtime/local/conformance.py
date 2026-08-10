from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any, Mapping, Sequence, cast

from ..conformance import ConformanceError, load_conformance_manifest
from ..contracts import JSONValue
from .models import (
    RecordCollectionPolicy,
    RecordWritePrecondition,
    VyralRecord,
)
from .query_models import QueryEnvelope
from .record_store import (
    CollectionNotFoundError,
    CollectionPolicyConflictError,
    RecordPreconditionFailedError,
    RecordStoreError,
    RecordValidationError,
    SQLiteRecordStore,
)


_CORE_SCENARIO_ID = "records.core-crud.v1"
_RECORD_SCENARIO_PREFIX = "records."


@dataclass(frozen=True)
class StatefulResult:
    scenario_id: str
    step_id: str
    operation: str
    value: JSONValue


def _object(value: object, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ConformanceError(f"{label} must be an object.")
    return cast(Mapping[str, Any], value)


def _array(value: object, label: str) -> Sequence[Any]:
    if not isinstance(value, list):
        raise ConformanceError(f"{label} must be an array.")
    return value


def _string(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise ConformanceError(f"{label} must be a string.")
    return value


def _boolean(value: object, label: str) -> bool:
    if not isinstance(value, bool):
        raise ConformanceError(f"{label} must be a boolean.")
    return value


def _record_projection(record: VyralRecord) -> dict[str, JSONValue]:
    vectors = record.vectors or {}
    vector = vectors.get("embedding")
    return {
        "id": record.id,
        "partitionKey": record.partition_key,
        "etag": record.etag,
        "revision": record.revision,
        "vectorDimensions": vector.dimensions if vector is not None else None,
    }


def _record_read_projection(record: VyralRecord | None) -> JSONValue:
    if record is None:
        return None
    material = record.to_dict()
    extension = material.get("extension")
    vectors = record.vectors or {}
    vector = vectors.get("embedding")
    return {
        "id": record.id,
        "partitionKey": record.partition_key,
        "type": record.type,
        "schemaVersion": record.schema_version,
        "metadata": cast(JSONValue, record.metadata),
        "content": cast(JSONValue, record.content),
        "extension": cast(JSONValue, extension),
        "etag": record.etag,
        "revision": record.revision,
        "vectorDimensions": vector.dimensions if vector is not None else None,
    }


def _execute(
    store: SQLiteRecordStore,
    operation: str,
    arguments: Mapping[str, Any],
) -> JSONValue:
    if operation == "records.collection.create":
        store.create_collection(
            RecordCollectionPolicy.from_value(
                _object(arguments.get("policy"), "arguments.policy")
            )
        )
        return None
    if operation == "records.collection.list":
        return list(store.list_collections())
    if operation == "records.collection.delete":
        store.delete_collection(
            _string(arguments.get("collection"), "arguments.collection")
        )
        return None
    if operation == "records.record.upsert":
        precondition = arguments.get("precondition")
        stored = store.upsert_record(
            _string(arguments.get("collection"), "arguments.collection"),
            VyralRecord.from_value(_object(arguments.get("record"), "arguments.record")),
            RecordWritePrecondition.from_value(
                _object(precondition, "arguments.precondition")
                if precondition is not None
                else None
            ),
        )
        return _record_projection(stored)
    if operation == "records.record.get":
        record = store.get_record(
            _string(arguments.get("collection"), "arguments.collection"),
            _string(arguments.get("partitionKey"), "arguments.partitionKey"),
            _string(arguments.get("id"), "arguments.id"),
        )
        return _record_read_projection(record)
    if operation == "records.record.batch-upsert":
        records = tuple(
            VyralRecord.from_value(_object(value, "arguments.records[]"))
            for value in _array(arguments.get("records"), "arguments.records")
        )
        batch_result = store.upsert_records(
            _string(arguments.get("collection"), "arguments.collection"),
            records,
            continue_on_error=_boolean(
                arguments.get("continueOnError"),
                "arguments.continueOnError",
            ),
        )
        return {
            "collection": batch_result.collection,
            "requested": batch_result.requested,
            "attempted": batch_result.attempted,
            "succeeded": batch_result.succeeded,
            "failed": batch_result.failed,
            "stoppedOnError": batch_result.stopped_on_error,
            "statuses": [item.status for item in batch_result.items],
        }
    if operation == "records.query":
        query_result = store.query_records_page(
            _string(arguments.get("collection"), "arguments.collection"),
            QueryEnvelope.from_value(
                _object(arguments.get("query"), "arguments.query")
            ),
        )
        return {
            "ids": [record.id for record in query_result.items],
            "continuationToken": query_result.continuation_token,
        }
    if operation == "records.search":
        search_result = store.search_records_page(
            _string(arguments.get("collection"), "arguments.collection"),
            QueryEnvelope.from_value(
                _object(arguments.get("query"), "arguments.query")
            ),
        )
        return {
            "ids": [match.record.id for match in search_result.items],
            "scores": [match.score for match in search_result.items],
            "continuationToken": search_result.continuation_token,
        }
    raise ConformanceError(f"Unsupported record-store scenario operation {operation!r}.")


def _error_class(error: Exception) -> str:
    if isinstance(error, RecordPreconditionFailedError):
        return "precondition-failed"
    if isinstance(error, CollectionPolicyConflictError):
        return "policy-conflict"
    if isinstance(error, CollectionNotFoundError):
        return "not-found"
    if isinstance(error, (RecordValidationError, TypeError, ValueError)):
        return "validation"
    if isinstance(error, RecordStoreError):
        return "record-store"
    return "unexpected"


def _assert_expectation(
    *,
    step_id: str,
    expectation: Mapping[str, Any],
    value: JSONValue | None = None,
    error: Exception | None = None,
) -> JSONValue:
    if "value" in expectation:
        if error is not None:
            raise ConformanceError(
                f"Stateful step {step_id!r} failed unexpectedly with {_error_class(error)!r}."
            ) from error
        expected = cast(JSONValue, expectation["value"])
        if value != expected:
            raise ConformanceError(
                f"Stateful step {step_id!r} produced {value!r}, expected {expected!r}."
            )
        return value

    expected_error = _object(expectation.get("error"), f"step {step_id}.expect.error")
    if error is None:
        raise ConformanceError(f"Stateful step {step_id!r} did not raise the expected error.")
    actual_class = _error_class(error)
    expected_class = _string(expected_error.get("class"), "expect.error.class")
    if actual_class != expected_class:
        raise ConformanceError(
            f"Stateful step {step_id!r} raised {actual_class!r}, expected {expected_class!r}."
        ) from error
    message_contains = expected_error.get("messageContains")
    if message_contains is not None:
        needle = _string(message_contains, "expect.error.messageContains")
        if needle not in str(error):
            raise ConformanceError(
                f"Stateful step {step_id!r} error did not contain {needle!r}."
            ) from error
    return {"error": {"class": actual_class, "message": str(error)}}


def _run(
    store: SQLiteRecordStore,
    fixture_root: str | Path | None,
    scenario_id: str,
) -> tuple[StatefulResult, ...]:
    manifest = load_conformance_manifest(fixture_root)
    descriptor = next(
        (item for item in manifest.scenarios if item.scenario_id == scenario_id),
        None,
    )
    if descriptor is None:
        raise ConformanceError(
            f"Conformance scenario {scenario_id!r} is unavailable."
        )
    if descriptor.kind != "stateful":
        raise ConformanceError(
            f"Conformance scenario {scenario_id!r} must be stateful."
        )
    scenario = manifest.scenario(descriptor)

    results: list[StatefulResult] = []
    for raw_step in _array(scenario.get("steps"), "scenario.steps"):
        step = _object(raw_step, "scenario.steps[]")
        step_id = _string(step.get("id"), "step.id")
        operation = _string(step.get("operation"), "step.operation")
        arguments = _object(step.get("arguments"), "step.arguments")
        expectation = _object(step.get("expect"), "step.expect")
        value: JSONValue | None = None
        error: Exception | None = None
        try:
            value = _execute(store, operation, arguments)
        except Exception as exc:
            error = exc
        observed = _assert_expectation(
            step_id=step_id,
            expectation=expectation,
            value=value,
            error=error,
        )
        results.append(
            StatefulResult(
                scenario_id=descriptor.scenario_id,
                step_id=step_id,
                operation=operation,
                value=observed,
            )
        )
    return tuple(results)


def run_bundled_record_store_scenario(
    fixture_root: str | Path | None = None,
    *,
    database_path: str | Path | None = None,
) -> tuple[StatefulResult, ...]:
    """Run the portable record-core scenario against a new native SQLite store."""

    if database_path is not None:
        return _run(
            SQLiteRecordStore(database_path),
            fixture_root,
            _CORE_SCENARIO_ID,
        )
    with TemporaryDirectory(prefix="vyral-record-conformance-") as temporary:
        return _run(
            SQLiteRecordStore(Path(temporary) / "records.sqlite"),
            fixture_root,
            _CORE_SCENARIO_ID,
        )


def run_bundled_record_store_scenarios(
    fixture_root: str | Path | None = None,
) -> tuple[StatefulResult, ...]:
    """Run every language-neutral record-store scenario in isolated stores."""

    manifest = load_conformance_manifest(fixture_root)
    scenario_ids = tuple(
        descriptor.scenario_id
        for descriptor in manifest.scenarios
        if (
            descriptor.kind == "stateful"
            and descriptor.profile == "vyral.runtime.data-rag.v1"
            and descriptor.scenario_id.startswith(_RECORD_SCENARIO_PREFIX)
        )
    )
    if not scenario_ids:
        raise ConformanceError(
            "The conformance bundle contains no record scenarios."
        )

    results: list[StatefulResult] = []
    with TemporaryDirectory(prefix="vyral-record-conformance-") as temporary:
        root = Path(temporary)
        for index, scenario_id in enumerate(scenario_ids):
            results.extend(
                _run(
                    SQLiteRecordStore(root / f"records-{index}.sqlite"),
                    fixture_root,
                    scenario_id,
                )
            )
    return tuple(results)
