from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from hashlib import sha256
from importlib import resources
import json
from pathlib import Path, PurePosixPath
import struct
from typing import Any, Callable, Mapping, Protocol

from ._datetime import parse_iso_datetime
from ._version import CONTRACT_VERSION, FIXTURE_VERSION, RUNTIME_VERSION
from .admission import create_admission_receipt
from .contracts import JSONValue
from .primitives import canonical_transaction_id, hash_lease_token, sha256_utf8


class ConformanceError(RuntimeError):
    """Raised when fixture integrity, shape, or expected behavior fails."""


class _FixtureSource(Protocol):
    def read_bytes(self, relative: str) -> bytes: ...


@dataclass(frozen=True)
class _PathFixtureSource:
    root: Path

    def read_bytes(self, relative: str) -> bytes:
        path = self.root.joinpath(*_safe_parts(relative))
        if not path.is_file():
            raise ConformanceError(f"Conformance fixture {relative!r} does not exist.")
        return path.read_bytes()


@dataclass(frozen=True)
class _ResourceFixtureSource:
    root: Any

    def read_bytes(self, relative: str) -> bytes:
        resource = self.root
        for part in _safe_parts(relative):
            resource = resource.joinpath(part)
        if not resource.is_file():
            raise ConformanceError(f"Bundled conformance fixture {relative!r} does not exist.")
        return bytes(resource.read_bytes())


@dataclass(frozen=True)
class ScenarioDescriptor:
    scenario_id: str
    path: str
    sha256: str
    profile: str
    kind: str


@dataclass(frozen=True)
class ConformanceManifest:
    fixture_version: str
    contract_version: str
    runner_version: str
    profiles: tuple[str, ...]
    scenarios: tuple[ScenarioDescriptor, ...]
    _source: _FixtureSource

    def scenario(self, descriptor: ScenarioDescriptor) -> Mapping[str, Any]:
        raw = self._source.read_bytes(descriptor.path)
        _require_digest(descriptor.path, raw, descriptor.sha256)
        document = _parse_object(descriptor.path, raw)
        _validate_scenario(document, descriptor, self)
        return document


@dataclass(frozen=True)
class GoldenResult:
    scenario_id: str
    step_id: str
    operation: str
    value: JSONValue


def _safe_parts(relative: str) -> tuple[str, ...]:
    path = PurePosixPath(relative)
    if path.is_absolute() or not path.parts or any(part in {"", ".", ".."} for part in path.parts):
        raise ConformanceError(f"Conformance path {relative!r} is not portable.")
    return path.parts


def _parse_object(label: str, raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ConformanceError(f"Conformance fixture {label!r} is not valid UTF-8 JSON.") from exc
    if not isinstance(value, dict):
        raise ConformanceError(f"Conformance fixture {label!r} must be a JSON object.")
    return value


def _require_string(value: object, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise ConformanceError(f"{label} must be a non-empty string.")
    return value


def _require_text(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise ConformanceError(f"{label} must be a string.")
    return value


def _require_digest(label: str, raw: bytes, expected: str) -> None:
    actual = "sha256:" + sha256(raw).hexdigest()
    if actual != expected:
        raise ConformanceError(
            f"Conformance fixture {label!r} has digest {actual!r}, expected {expected!r}."
        )


def _repository_fixture_root() -> Path | None:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "conformance/runtime/v1/manifest.json"
        if candidate.is_file():
            return candidate.parent
    return None


def _bundled_source() -> _FixtureSource:
    packaged = resources.files("vyral_runtime._conformance").joinpath("runtime").joinpath("v1")
    if packaged.joinpath("manifest.json").is_file():
        return _ResourceFixtureSource(packaged)
    repository = _repository_fixture_root()
    if repository is not None:
        return _PathFixtureSource(repository)
    raise ConformanceError("The bundled Vyral runtime conformance manifest is unavailable.")


def _load_source(root: str | Path | None) -> _FixtureSource:
    if root is None:
        return _bundled_source()
    resolved = Path(root).expanduser().resolve()
    if not resolved.is_dir():
        raise ConformanceError(f"Conformance root {str(resolved)!r} is not a directory.")
    return _PathFixtureSource(resolved)


def load_conformance_manifest(root: str | Path | None = None) -> ConformanceManifest:
    source = _load_source(root)
    document = _parse_object("manifest.json", source.read_bytes("manifest.json"))

    fixture_version = _require_string(document.get("fixtureVersion"), "fixtureVersion")
    contract_version = _require_string(document.get("contractVersion"), "contractVersion")
    runner_version = _require_string(document.get("minimumRunnerVersion"), "minimumRunnerVersion")
    if fixture_version != FIXTURE_VERSION:
        raise ConformanceError(
            f"Fixture version must be {FIXTURE_VERSION!r}, found {fixture_version!r}."
        )
    if contract_version != CONTRACT_VERSION:
        raise ConformanceError(
            f"Fixture contract version must be {CONTRACT_VERSION!r}, found {contract_version!r}."
        )
    if _version_tuple(RUNTIME_VERSION) < _version_tuple(runner_version):
        raise ConformanceError(
            "Fixture minimum runner version "
            f"{runner_version!r} exceeds this runtime's version "
            f"{RUNTIME_VERSION!r}."
        )

    schema_descriptor = document.get("scenarioSchema")
    if not isinstance(schema_descriptor, dict):
        raise ConformanceError("scenarioSchema must be an object.")
    schema_path = _require_string(schema_descriptor.get("path"), "scenarioSchema.path")
    schema_digest = _require_string(schema_descriptor.get("sha256"), "scenarioSchema.sha256")
    schema_raw = source.read_bytes(schema_path)
    _require_digest(schema_path, schema_raw, schema_digest)
    schema = _parse_object(schema_path, schema_raw)
    if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        raise ConformanceError("The scenario schema must use JSON Schema 2020-12.")

    raw_profiles = document.get("profiles")
    if not isinstance(raw_profiles, list) or not raw_profiles:
        raise ConformanceError("profiles must be a non-empty array.")
    profiles = tuple(_require_string(value, "profile") for value in raw_profiles)
    if len(set(profiles)) != len(profiles):
        raise ConformanceError("profiles contains duplicate identifiers.")

    raw_scenarios = document.get("scenarios")
    if not isinstance(raw_scenarios, list) or not raw_scenarios:
        raise ConformanceError("scenarios must be a non-empty array.")
    scenarios: list[ScenarioDescriptor] = []
    for item in raw_scenarios:
        if not isinstance(item, dict):
            raise ConformanceError("Every scenario descriptor must be an object.")
        descriptor = ScenarioDescriptor(
            scenario_id=_require_string(item.get("id"), "scenario.id"),
            path=_require_string(item.get("path"), "scenario.path"),
            sha256=_require_string(item.get("sha256"), "scenario.sha256"),
            profile=_require_string(item.get("profile"), "scenario.profile"),
            kind=_require_string(item.get("kind"), "scenario.kind"),
        )
        _safe_parts(descriptor.path)
        if descriptor.profile not in profiles:
            raise ConformanceError(
                f"Scenario {descriptor.scenario_id!r} uses undeclared profile {descriptor.profile!r}."
            )
        scenarios.append(descriptor)

    ids = [scenario.scenario_id for scenario in scenarios]
    if len(set(ids)) != len(ids):
        raise ConformanceError("The manifest contains duplicate scenario ids.")

    manifest = ConformanceManifest(
        fixture_version=fixture_version,
        contract_version=contract_version,
        runner_version=runner_version,
        profiles=profiles,
        scenarios=tuple(scenarios),
        _source=source,
    )
    for descriptor in manifest.scenarios:
        manifest.scenario(descriptor)
    return manifest


def _version_tuple(value: str) -> tuple[int, int, int]:
    parts = value.split(".")
    if len(parts) != 3 or any(
        not part.isdigit() for part in parts
    ):
        raise ConformanceError(
            f"Conformance version {value!r} is not semantic x.y.z."
        )
    major, minor, patch = parts
    return int(major), int(minor), int(patch)


def _validate_scenario(
    document: Mapping[str, Any],
    descriptor: ScenarioDescriptor,
    manifest: ConformanceManifest,
) -> None:
    expected = {
        "id": descriptor.scenario_id,
        "fixtureVersion": manifest.fixture_version,
        "profile": descriptor.profile,
        "kind": descriptor.kind,
    }
    for field, value in expected.items():
        if document.get(field) != value:
            raise ConformanceError(
                f"Scenario {descriptor.scenario_id!r} field {field!r} must be {value!r}."
            )

    steps = document.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ConformanceError(f"Scenario {descriptor.scenario_id!r} has no steps.")
    step_ids: set[str] = set()
    for step in steps:
        if not isinstance(step, dict):
            raise ConformanceError(
                f"Scenario {descriptor.scenario_id!r} contains a non-object step."
            )
        step_id = _require_string(step.get("id"), "step.id")
        if step_id in step_ids:
            raise ConformanceError(
                f"Scenario {descriptor.scenario_id!r} repeats step id {step_id!r}."
            )
        step_ids.add(step_id)
        _require_string(step.get("operation"), f"step {step_id}.operation")
        if not isinstance(step.get("arguments"), dict):
            raise ConformanceError(f"Step {step_id!r} arguments must be an object.")
        expectation = step.get("expect")
        if not isinstance(expectation, dict) or ("value" in expectation) == ("error" in expectation):
            raise ConformanceError(
                f"Step {step_id!r} must expect exactly one of value or error."
            )


def _operation_sha256(arguments: Mapping[str, Any]) -> JSONValue:
    return sha256_utf8(_require_text(arguments.get("text"), "arguments.text"))


def _operation_transaction_id(arguments: Mapping[str, Any]) -> JSONValue:
    return canonical_transaction_id(
        _require_string(arguments.get("tenantId"), "arguments.tenantId"),
        _require_string(arguments.get("idempotencyKey"), "arguments.idempotencyKey"),
    )


def _operation_lease_hash(arguments: Mapping[str, Any]) -> JSONValue:
    return hash_lease_token(_require_string(arguments.get("token"), "arguments.token"))


def _operation_admission_receipt(
    arguments: Mapping[str, Any],
) -> JSONValue:
    raw_key = arguments.get("idempotencyKey")
    if raw_key is not None and not isinstance(raw_key, str):
        raise ConformanceError(
            "arguments.idempotencyKey must be a string or null."
        )
    replayed = arguments.get("replayed")
    if not isinstance(replayed, bool):
        raise ConformanceError("arguments.replayed must be a boolean.")
    try:
        admitted_at = parse_iso_datetime(
            _require_string(
                arguments.get("admittedAtUtc"),
                "arguments.admittedAtUtc",
            )
        )
    except ValueError as exc:
        raise ConformanceError(
            "arguments.admittedAtUtc must be an ISO timestamp."
        ) from exc
    return create_admission_receipt(
        operation_id=_require_string(
            arguments.get("operationId"), "arguments.operationId"
        ),
        resource_id=_require_string(
            arguments.get("resourceId"), "arguments.resourceId"
        ),
        request_hash=_require_text(
            arguments.get("requestHash"), "arguments.requestHash"
        ),
        idempotency_key=raw_key,
        replayed=replayed,
        admitted_at_utc=admitted_at,
        status_uri=_require_string(
            arguments.get("statusUri"), "arguments.statusUri"
        ),
        status=_require_string(
            arguments.get("status"), "arguments.status"
        ),
        result_uri=(
            _require_string(
                arguments.get("resultUri"), "arguments.resultUri"
            )
            if arguments.get("resultUri") is not None
            else None
        ),
        failure_class=(
            _require_string(
                arguments.get("failureClass"),
                "arguments.failureClass",
            )
            if arguments.get("failureClass") is not None
            else None
        ),
        error=(
            _require_text(arguments.get("error"), "arguments.error")
            if arguments.get("error") is not None
            else None
        ),
    ).to_dict()


def _operation_snapshot_hash(arguments: Mapping[str, Any]) -> JSONValue:
    from .local.snapshots import (
        CollectionExportEnvelope,
        compute_collection_snapshot_hash,
    )

    snapshot = arguments.get("snapshot")
    if not isinstance(snapshot, Mapping):
        raise ConformanceError("arguments.snapshot must be an object.")
    return compute_collection_snapshot_hash(
        CollectionExportEnvelope.from_value(snapshot)
    )


def _operation_embedding(arguments: Mapping[str, Any]) -> JSONValue:
    from .embeddings import EmbeddingProviderRegistry

    provider_id = _require_string(arguments.get("provider"), "arguments.provider")
    dimensions = arguments.get("dimensions")
    if isinstance(dimensions, bool) or not isinstance(dimensions, int):
        raise ConformanceError("arguments.dimensions must be an integer.")
    provider = EmbeddingProviderRegistry().create(
        {"provider": provider_id, "dimensions": dimensions}
    )
    vector = provider.generate_embedding(
        _require_text(arguments.get("text"), "arguments.text")
    )
    return {
        "provider": provider.provider_id,
        "modelId": provider.model_id,
        "dimensions": provider.dimensions,
        "float32LittleEndianHex": struct.pack(
            f"<{len(vector)}f",
            *vector,
        ).hex(),
    }


def _operation_graph_record_map(arguments: Mapping[str, Any]) -> JSONValue:
    from .graph import VyralGraphEnvelope, graph_to_records

    raw_envelope = arguments.get("envelope")
    if not isinstance(raw_envelope, Mapping):
        raise ConformanceError("arguments.envelope must be an object.")
    envelope = VyralGraphEnvelope.from_value(raw_envelope)
    records = graph_to_records(
        envelope,
        clock=lambda: datetime(2000, 1, 1, tzinfo=timezone.utc),
    )
    projected: list[JSONValue] = []
    for record in records:
        content = record.content or {}
        entity_key = record.type.removeprefix("graph.")
        entity = content.get(entity_key)
        entity_id = (
            entity.get("id")
            if isinstance(entity, Mapping)
            and isinstance(entity.get("id"), str)
            else None
        )
        sources: list[JSONValue] = []
        for source in record.sources or ():
            span = source.get("span")
            span_mapping = span if isinstance(span, Mapping) else {}
            sources.append(
                {
                    "id": source.get("id"),
                    "kind": source.get("kind"),
                    "uri": source.get("uri"),
                    "label": source.get("label"),
                    "span": {
                        "charStart": span_mapping.get("charStart"),
                        "charEnd": span_mapping.get("charEnd"),
                        "anchor": span_mapping.get("anchor"),
                        "unit": span_mapping.get("unit"),
                        "textHash": span_mapping.get("textHash"),
                    },
                }
            )
        projected.append(
            {
                "id": record.id,
                "partitionKey": record.partition_key,
                "type": record.type,
                "schemaVersion": record.schema_version,
                "metadata": record.metadata,
                "contentKind": content.get("kind"),
                "contentText": content.get("text"),
                "entityId": entity_id,
                "sources": sources,
            }
        )
    return {
        "partitionKey": envelope.scope.resolved_partition_key(),
        "recordCount": len(records),
        "records": projected,
    }


def _operation_rag_ingestion_plan(
    arguments: Mapping[str, Any],
) -> JSONValue:
    from tempfile import TemporaryDirectory

    from .embeddings import EmbeddingProviderRegistry
    from .local import RecordCollectionPolicy, SQLiteRecordStore
    from .rag import RagIngestTextRequest, RagIngestionService

    collection = _require_string(
        arguments.get("collection"), "arguments.collection"
    )
    raw_policy = arguments.get("policy")
    if not isinstance(raw_policy, Mapping):
        raise ConformanceError("arguments.policy must be an object.")
    raw_provider = arguments.get("provider")
    if not isinstance(raw_provider, Mapping):
        raise ConformanceError("arguments.provider must be an object.")
    raw_request = arguments.get("request")
    if not isinstance(raw_request, Mapping):
        raise ConformanceError("arguments.request must be an object.")

    with TemporaryDirectory(
        prefix="vyral-rag-plan-conformance-"
    ) as temporary:
        store = SQLiteRecordStore(Path(temporary) / "rag.sqlite")
        store.create_collection(
            RecordCollectionPolicy.from_value(raw_policy)
        )
        provider = EmbeddingProviderRegistry().create(
            {
                str(key): value
                for key, value in raw_provider.items()
            }
        )
        service = RagIngestionService(store, provider)
        try:
            result = service.ingest_text(
                collection,
                RagIngestTextRequest.from_value(raw_request),
            )
        finally:
            service.close()
    return {
        "planHash": result.plan_hash,
        "manifestHash": result.manifest_hash,
        "textHash": result.text_hash,
        "chunkCount": result.chunk_count,
        "createdCount": result.created_count,
        "vectorGeneratedCount": result.vector_generated_count,
        "manifestAction": result.manifest_action,
        "chunkIds": [item.id for item in result.chunks],
        "chunkTextHashes": [
            item.text_hash for item in result.chunks
        ],
        "embeddingTextHashes": [
            item.embedding_text_hash for item in result.chunks
        ],
        "spans": [
            [item.char_start, item.char_end]
            for item in result.chunks
        ],
    }


_GOLDEN_OPERATIONS: dict[str, Callable[[Mapping[str, Any]], JSONValue]] = {
    "hash.sha256-utf8": _operation_sha256,
    "canonical.transaction-id": _operation_transaction_id,
    "canonical.lease-token-hash": _operation_lease_hash,
    "admission.receipt": _operation_admission_receipt,
    "records.snapshot-hash": _operation_snapshot_hash,
    "embeddings.generate": _operation_embedding,
    "graph.record-map": _operation_graph_record_map,
    "rag.ingestion-plan": _operation_rag_ingestion_plan,
}


def run_bundled_goldens(root: str | Path | None = None) -> tuple[GoldenResult, ...]:
    manifest = load_conformance_manifest(root)
    results: list[GoldenResult] = []
    for descriptor in manifest.scenarios:
        if descriptor.kind != "golden":
            continue
        scenario = manifest.scenario(descriptor)
        for raw_step in scenario["steps"]:
            step = raw_step
            operation = step["operation"]
            handler = _GOLDEN_OPERATIONS.get(operation)
            if handler is None:
                raise ConformanceError(
                    f"Scenario {descriptor.scenario_id!r} uses unsupported golden operation {operation!r}."
                )
            expectation = step["expect"]
            if "error" in expectation:
                raise ConformanceError(
                    f"Golden error expectations are not implemented for operation {operation!r}."
                )
            try:
                actual = handler(step["arguments"])
            except (TypeError, ValueError) as exc:
                raise ConformanceError(
                    f"Golden operation {operation!r} failed unexpectedly in step {step['id']!r}."
                ) from exc
            if actual != expectation["value"]:
                raise ConformanceError(
                    f"Golden step {step['id']!r} produced {actual!r}, expected {expectation['value']!r}."
                )
            results.append(
                GoldenResult(
                    scenario_id=descriptor.scenario_id,
                    step_id=step["id"],
                    operation=operation,
                    value=actual,
                )
            )
    if not results:
        raise ConformanceError("The conformance bundle contains no executable golden steps.")
    return tuple(results)
