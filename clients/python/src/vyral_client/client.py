from __future__ import annotations

import ipaddress
import json
import socket
import time
import uuid
from dataclasses import dataclass, replace
from email.utils import parsedate_to_datetime
from typing import Any, BinaryIO, Callable, Iterable, Iterator, Mapping, Sequence
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import Request, urlopen


class VyralClientError(RuntimeError):
    def __init__(
        self,
        status: int,
        body: str,
        *,
        problem: dict[str, Any] | None = None,
        failure_class: str | None = None,
        retry_after: str | None = None,
        correlation_id: str | None = None,
    ):
        self.status = status
        self.body = body
        self.problem = problem if problem is not None else _parse_problem_body(body)
        admission = self.problem.get("admission") if isinstance(self.problem, dict) else None
        self.admission = admission if isinstance(admission, dict) else None
        self.failure_class = failure_class or _problem_string(self.admission, "failureClass")
        self.type = _problem_string(self.problem, "type")
        self.title = _problem_string(self.problem, "title")
        self.detail = _problem_string(self.problem, "detail")
        self.instance = _problem_string(self.problem, "instance")
        self.problem_status = _problem_status(self.problem)
        self.retry_after = retry_after
        self.correlation_id = correlation_id
        super().__init__(self._build_message())

    @classmethod
    def timeout(cls, detail: str) -> "VyralClientError":
        return cls(
            0,
            "",
            problem={"title": "Request timeout", "detail": detail, "status": 0},
            failure_class="timeout",
        )

    @classmethod
    def cancelled(cls, detail: str = "Request was cancelled") -> "VyralClientError":
        return cls(
            0,
            "",
            problem={"title": "Request cancelled", "detail": detail, "status": 0},
            failure_class="cancelled",
        )

    def is_missing_collection(self) -> bool:
        text = f"{self.title or ''} {self.detail or self.body}".lower()
        return (
            self.status == 404
            and "collection" in text
            and ("not found" in text or "does not exist" in text)
        )

    def is_auth_error(self) -> bool:
        return self.status in (401, 403)

    def is_validation_error(self) -> bool:
        return self.status in (400, 422)

    def is_timeout(self) -> bool:
        return self.failure_class == "timeout" or self.status in (408, 504)

    def is_cancelled(self) -> bool:
        return self.failure_class == "cancelled"

    def is_transient(self) -> bool:
        return self.failure_class in {"timeout", "transport"} or self.status in (408, 429, 502, 503, 504)

    def _build_message(self) -> str:
        if self.status == 0:
            detail = self.detail or self.body or "request failed before an HTTP response was received"
            title = self.title or "Request failed"
            return f"VYRAL request failed before receiving HTTP response: {title}: {detail}"

        if self.title and self.detail:
            return f"VYRAL request failed with HTTP {self.status}: {self.title}: {self.detail}"
        if self.title:
            return f"VYRAL request failed with HTTP {self.status}: {self.title}"
        return f"VYRAL request failed with HTTP {self.status}: {self.body}"


TERMINAL_PROVIDER_JOB_STATUSES = {
    "Succeeded",
    "Failed",
    "TimedOut",
    "Rejected",
    "Unsupported",
    "NotConfigured",
    "Cancelled",
    "succeeded",
    "failed",
    "timedOut",
    "rejected",
    "unsupported",
    "notConfigured",
    "cancelled",
}

TERMINAL_EXECUTION_RUN_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
    "timed_out",
}


def is_provider_run_succeeded(result: dict[str, Any] | None) -> bool:
    if not isinstance(result, dict):
        return False
    return str(result.get("status") or "").lower() == "succeeded"


def get_provider_run_rejection(result: dict[str, Any] | None) -> dict[str, Any] | None:
    if not isinstance(result, dict):
        return None
    rejection = result.get("rejection")
    if isinstance(rejection, dict):
        return rejection
    output = result.get("output")
    if isinstance(output, dict) and isinstance(output.get("rejection"), dict):
        return output["rejection"]
    return None


def is_provider_run_output_usable(result: dict[str, Any] | None) -> bool:
    if not is_provider_run_succeeded(result):
        return False
    rejection = get_provider_run_rejection(result)
    return rejection is None or bool(rejection.get("contentUsable"))


def is_execution_run_terminal(run_or_status: dict[str, Any] | str | None) -> bool:
    if isinstance(run_or_status, dict):
        status = run_or_status.get("status")
    else:
        status = run_or_status
    return isinstance(status, str) and status in TERMINAL_EXECUTION_RUN_STATUSES


TERMINAL_RETRIEVAL_EVALUATION_JOB_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
}

TERMINAL_EMBEDDING_JOB_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
}

TERMINAL_RECORD_IMPORT_JOB_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
}

TERMINAL_RAG_INGESTION_JOB_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
}

TERMINAL_GRAPH_JOB_STATUSES = {
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
}


DEFAULT_RAG_INDEXED_METADATA = (
    "/metadata/documentId",
    "/metadata/topic",
    "/metadata/status",
    "/type",
)

DEFAULT_VERIFIED_RETRIEVAL_FIELDS = (
    "/content/text",
    "/metadata/referenceId",
    "/metadata/page",
    "/metadata/title",
    "/metadata/source",
    "/id",
)

DEFAULT_VERIFIED_RETRIEVAL_FIELD_BOOSTS = {
    "/metadata/referenceId": 3.0,
    "/id": 1.5,
    "/metadata/page": 1.25,
    "/metadata/title": 1.15,
}

DEFAULT_RAG_RETRIEVAL_FIELDS = (
    "/content/text",
    "/metadata/title",
    "/metadata/source",
    "/id",
)

RETRIEVAL_PROFILES = {
    "evidence": "evidence",
    "rag_baseline": "ragBaseline",
    "rerank_polish": "rerankPolish",
    "deep_quality": "deepQuality",
    "discovery": "discovery",
    "product_optimization": "productOptimization",
}

EVIDENCE_BRIEF_SCHEMA = "vyral.evidence-brief.v1"
EVIDENCE_BRIEF_DOCUMENT_TYPE = "vyral.evidence-brief"
EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC = "vyral.evidence-brief.changed"


def _parse_problem_body(body: str) -> dict[str, Any] | None:
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        return None

    if not isinstance(parsed, dict):
        return None

    problem_keys = {"type", "title", "status", "detail", "instance"}
    has_admission = isinstance(parsed.get("admission"), dict)
    return parsed if problem_keys.intersection(parsed) or has_admission else None


def _problem_string(problem: dict[str, Any] | None, key: str) -> str | None:
    if not problem:
        return None
    value = problem.get(key)
    return value if isinstance(value, str) and value else None


def _problem_status(problem: dict[str, Any] | None) -> int | None:
    if not problem:
        return None
    value = problem.get("status")
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, str) and value.isdigit():
        return int(value)
    return None


def _is_timeout_exception(error: BaseException | object) -> bool:
    if isinstance(error, (TimeoutError, socket.timeout)):
        return True
    name = getattr(error, "name", "")
    return isinstance(name, str) and name.lower() in {"timeouterror", "aborterror"}


def _header_value(headers: object, *names: str) -> str | None:
    getter = getattr(headers, "get", None)
    if not callable(getter):
        return None
    for name in names:
        value = getter(name)
        if value:
            return str(value)
    return None


def _retry_delay_seconds(retry_after: str | None, fallback: float) -> float:
    if retry_after:
        try:
            return max(0.0, float(retry_after))
        except ValueError:
            try:
                delay = (parsedate_to_datetime(retry_after).timestamp() - time.time())
                return max(0.0, delay)
            except (TypeError, ValueError, OverflowError):
                pass
    return fallback


def _with_rag_ingestion_options(request: dict[str, Any], updates: dict[str, Any]) -> dict[str, Any]:
    payload = dict(request)
    options = dict(payload.get("options") or {})
    options.update({key: value for key, value in updates.items() if value is not None})
    payload["options"] = options
    return payload


def _omit_none(values: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in values.items() if value is not None}


def build_evidence_brief_transaction(
    tenant_id: str,
    idempotency_key: str,
    brief: Mapping[str, Any],
    *,
    expected_revision: int | None = None,
    correlation_id: str | None = None,
    actor: str | None = None,
    emit_change_event: bool = True,
    change_event_topic: str = EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC,
) -> dict[str, Any]:
    """Build the canonical transaction for a dated, source-grounded EvidenceBrief.

    The helper intentionally stores evidence and citation presentation only. It does not accept a
    generated answer, persona prompt, or truth-adjudication field. The Vyral server performs the
    full versioned contract validation before it commits this document.
    """
    if not tenant_id or not tenant_id.strip():
        raise ValueError("tenant_id is required")
    if not idempotency_key or not idempotency_key.strip():
        raise ValueError("idempotency_key is required")
    if not isinstance(brief, Mapping):
        raise ValueError("brief is required")

    payload = dict(brief)
    payload.setdefault("schema", EVIDENCE_BRIEF_SCHEMA)
    if payload.get("schema") != EVIDENCE_BRIEF_SCHEMA:
        raise ValueError(f"brief.schema must be {EVIDENCE_BRIEF_SCHEMA!r}")
    for field in ("id", "question", "asOfUtc", "factAnchors", "sourceSnapshots", "citations", "counterEvidence", "uncertainties", "retrievalTraces"):
        if field not in payload:
            raise ValueError(f"brief.{field} is required")
    if not isinstance(payload["id"], str) or not payload["id"].strip():
        raise ValueError("brief.id is required")
    if expected_revision is not None and expected_revision < 0:
        raise ValueError("expected_revision cannot be negative")
    if emit_change_event and not change_event_topic.strip():
        raise ValueError("change_event_topic is required when emit_change_event is true")

    transaction: dict[str, Any] = {
        "tenantId": tenant_id,
        "idempotencyKey": idempotency_key,
        "mutations": [{
            "operation": "upsert",
            "document": {
                "tenantId": tenant_id,
                "documentType": EVIDENCE_BRIEF_DOCUMENT_TYPE,
                "id": payload["id"],
                "schemaVersion": EVIDENCE_BRIEF_SCHEMA,
                "data": payload,
                "indexes": {
                    "schema": EVIDENCE_BRIEF_SCHEMA,
                    "asOfUtc": payload["asOfUtc"],
                },
            },
        }],
    }
    if expected_revision is not None:
        transaction["mutations"][0]["precondition"] = {"expectedRevision": expected_revision}
    if correlation_id is not None:
        transaction["correlationId"] = correlation_id
    if actor is not None:
        transaction["actor"] = actor
    if emit_change_event:
        transaction["outbox"] = [{
            "topic": change_event_topic,
            "key": payload["id"],
            "payload": {
                "briefId": payload["id"],
                "schema": EVIDENCE_BRIEF_SCHEMA,
                "asOfUtc": payload["asOfUtc"],
            },
        }]
    return transaction


def build_rag_collection_policy(
    collection: str,
    *,
    dimensions: int,
    embedding_field: str = "contentEmbedding",
    indexed_metadata: Sequence[str] | None = None,
    partition_key_path: str = "/partitionKey",
    datatype: str = "float32",
    distance_function: str = "cosine",
    index_type: str = "flat",
) -> dict[str, Any]:
    if not collection:
        raise ValueError("collection is required")
    if not embedding_field:
        raise ValueError("embedding_field is required")
    resolved_dimensions = _normalize_positive_integer(dimensions, "dimensions")

    return {
        "name": collection,
        "partitionKeyPath": partition_key_path,
        "indexedMetadata": list(indexed_metadata or DEFAULT_RAG_INDEXED_METADATA),
        "vectorPolicies": [
            {
                "name": embedding_field,
                "path": f"/vectors/{embedding_field}/values",
                "dimensions": resolved_dimensions,
                "datatype": datatype,
                "distanceFunction": distance_function,
                "indexType": index_type,
            }
        ],
    }


def build_verified_retrieval_request(
    query: str,
    collections: Sequence[str],
    *,
    partition_keys: Sequence[str] | None = None,
    record_filter: dict[str, Any] | None = None,
    fields: Sequence[str] | None = None,
    field_boosts: dict[str, float] | None = None,
    limit: int = 8,
    include_trace: bool = True,
    scan_limit: int | None = 5000,
    scoring: str = "bm25",
    prefix_matching: bool = True,
    prefix_min_chars: int = 3,
    min_score: float | None = None,
    rerank: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not query or not query.strip():
        raise ValueError("query is required")
    if not collections:
        raise ValueError("collections is required")

    merged_field_boosts = dict(DEFAULT_VERIFIED_RETRIEVAL_FIELD_BOOSTS)
    if field_boosts:
        merged_field_boosts.update(field_boosts)

    request: dict[str, Any] = {
        "query": query,
        "collections": list(collections),
        "searchMode": "lexical",
        "lexical": {
            "fields": list(fields or DEFAULT_VERIFIED_RETRIEVAL_FIELDS),
            "fieldBoosts": merged_field_boosts,
            "scanLimit": scan_limit,
            "scoring": scoring,
            "prefixMatching": prefix_matching,
            "prefixMinChars": prefix_min_chars,
        },
        "limit": _normalize_positive_integer(limit, "limit"),
        "includeTrace": include_trace,
    }
    if partition_keys is not None:
        request["partitionKeys"] = list(partition_keys)
    if record_filter is not None:
        request["filter"] = record_filter
    if min_score is not None:
        request["minScore"] = min_score
    if rerank is not None:
        request["rerank"] = rerank

    return request


def build_retrieval_profile_request(
    profile: str,
    query: str,
    collections: Sequence[str],
    *,
    partition_keys: Sequence[str] | None = None,
    record_filter: dict[str, Any] | None = None,
    embedding: dict[str, Any] | None = None,
    vector_fields: Sequence[dict[str, Any]] | None = None,
    search_mode: str | None = None,
    lexical: dict[str, Any] | None = None,
    hybrid: dict[str, Any] | None = None,
    rerank: dict[str, Any] | None = None,
    limit: int | None = None,
    min_score: float | None = None,
    include_trace: bool | None = None,
) -> dict[str, Any]:
    if not profile or not profile.strip():
        raise ValueError("profile is required")
    if not query or not query.strip():
        raise ValueError("query is required")
    if not collections:
        raise ValueError("collections is required")

    request: dict[str, Any] = {
        "profile": profile,
        "query": query,
        "collections": list(collections),
    }
    if partition_keys is not None:
        request["partitionKeys"] = list(partition_keys)
    if record_filter is not None:
        request["filter"] = record_filter
    if embedding is not None:
        request["embedding"] = embedding
    if vector_fields is not None:
        request["vectorFields"] = list(vector_fields)
    if search_mode is not None:
        request["searchMode"] = search_mode
    if lexical is not None:
        request["lexical"] = lexical
    if hybrid is not None:
        request["hybrid"] = hybrid
    if rerank is not None:
        request["rerank"] = rerank
    if limit is not None:
        request["limit"] = _normalize_positive_integer(limit, "limit")
    if min_score is not None:
        request["minScore"] = min_score
    if include_trace is not None:
        request["includeTrace"] = include_trace

    return request


def build_retrieval_evaluation_expected_match(
    id_or_match: str | dict[str, Any],
    *,
    partition_key: str | None = None,
    collection: str | None = None,
    aliases: Sequence[str] | None = None,
    source_ids: Sequence[str] | None = None,
    sources: Sequence[dict[str, Any]] | None = None,
    relevance: float | None = None,
) -> dict[str, Any]:
    match = _normalize_evaluation_match(id_or_match, "expected match")
    if partition_key is not None:
        match["partitionKey"] = partition_key
    if collection is not None:
        match["collection"] = collection
    if aliases is not None:
        match["aliases"] = list(aliases)
    if source_ids is not None:
        match["sourceIds"] = list(source_ids)
    if sources is not None:
        match["sources"] = list(sources)
    if relevance is not None:
        match["relevance"] = relevance
    return match


def build_retrieval_evaluation_hard_negative(
    id_or_match: str | dict[str, Any],
    *,
    partition_key: str | None = None,
    collection: str | None = None,
    aliases: Sequence[str] | None = None,
    source_ids: Sequence[str] | None = None,
    sources: Sequence[dict[str, Any]] | None = None,
    reason: str | None = None,
) -> dict[str, Any]:
    match = _normalize_evaluation_match(id_or_match, "hard negative")
    if partition_key is not None:
        match["partitionKey"] = partition_key
    if collection is not None:
        match["collection"] = collection
    if aliases is not None:
        match["aliases"] = list(aliases)
    if source_ids is not None:
        match["sourceIds"] = list(source_ids)
    if sources is not None:
        match["sources"] = list(sources)
    if reason is not None:
        match["reason"] = reason
    return match


def build_retrieval_evaluation_case(
    name: str | None,
    request: dict[str, Any],
    expected: Sequence[str | dict[str, Any]] | str | dict[str, Any],
    *,
    hard_negatives: Sequence[str | dict[str, Any]] | str | dict[str, Any] | None = None,
    k: int | None = None,
    metadata: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not isinstance(request, dict):
        raise ValueError("request is required")
    expected_matches = _normalize_evaluation_matches(expected, build_retrieval_evaluation_expected_match)
    if not expected_matches:
        raise ValueError("expected must contain at least one match")

    evaluation_case: dict[str, Any] = {
        "request": request,
        "expected": expected_matches,
        "hardNegatives": _normalize_evaluation_matches(
            hard_negatives or [],
            build_retrieval_evaluation_hard_negative,
        ),
    }
    if name is not None:
        evaluation_case["name"] = name
    if k is not None:
        evaluation_case["k"] = _normalize_positive_integer(k, "k")
    if metadata is not None:
        evaluation_case["metadata"] = metadata
    return evaluation_case


def build_retrieval_evaluation_request(
    cases: Sequence[dict[str, Any]],
    *,
    continue_on_error: bool = True,
    default_k: int | None = None,
    include_top_results: bool = True,
) -> dict[str, Any]:
    if not cases:
        raise ValueError("cases is required")
    request: dict[str, Any] = {
        "cases": list(cases),
        "continueOnError": continue_on_error,
        "includeTopResults": include_top_results,
    }
    if default_k is not None:
        request["defaultK"] = _normalize_positive_integer(default_k, "default_k")
    return request


def build_retrieval_evaluation_variant(
    variant_id: str,
    *,
    label: str | None = None,
    profile: str | None = None,
    collections: Sequence[str] | None = None,
    partition_keys: Sequence[str] | None = None,
    record_filter: dict[str, Any] | None = None,
    embedding: dict[str, Any] | None = None,
    vector_fields: Sequence[dict[str, Any]] | None = None,
    search_mode: str | None = None,
    lexical: dict[str, Any] | None = None,
    hybrid: dict[str, Any] | None = None,
    rerank: dict[str, Any] | None = None,
    limit: int | None = None,
    min_score: float | None = None,
    include_trace: bool | None = None,
) -> dict[str, Any]:
    if not variant_id or not variant_id.strip():
        raise ValueError("variant_id is required")
    variant: dict[str, Any] = {"id": variant_id}
    if label is not None:
        variant["label"] = label
    if profile is not None:
        variant["profile"] = profile
    if collections is not None:
        variant["collections"] = list(collections)
    if partition_keys is not None:
        variant["partitionKeys"] = list(partition_keys)
    if record_filter is not None:
        variant["filter"] = record_filter
    if embedding is not None:
        variant["embedding"] = embedding
    if vector_fields is not None:
        variant["vectorFields"] = list(vector_fields)
    if search_mode is not None:
        variant["searchMode"] = search_mode
    if lexical is not None:
        variant["lexical"] = lexical
    if hybrid is not None:
        variant["hybrid"] = hybrid
    if rerank is not None:
        variant["rerank"] = rerank
    if limit is not None:
        variant["limit"] = _normalize_positive_integer(limit, "limit")
    if min_score is not None:
        variant["minScore"] = min_score
    if include_trace is not None:
        variant["includeTrace"] = include_trace
    return variant


def build_retrieval_evaluation_comparison_request(
    cases: Sequence[dict[str, Any]],
    variants: Sequence[dict[str, Any]],
    *,
    continue_on_error: bool = True,
    default_k: int | None = None,
    include_top_results: bool = False,
    include_case_results: bool = False,
) -> dict[str, Any]:
    if not cases:
        raise ValueError("cases is required")
    if not variants:
        raise ValueError("variants is required")
    request: dict[str, Any] = {
        "cases": list(cases),
        "variants": list(variants),
        "continueOnError": continue_on_error,
        "includeTopResults": include_top_results,
        "includeCaseResults": include_case_results,
    }
    if default_k is not None:
        request["defaultK"] = _normalize_positive_integer(default_k, "default_k")
    return request


def build_rag_text_ingestion_request(
    text: str,
    partition_key: str,
    *,
    document_id: str | None = None,
    id_prefix: str | None = None,
    record_type: str | None = None,
    schema_version: str | None = None,
    content_field: str | None = None,
    embedding: dict[str, Any] | None = None,
    metadata: dict[str, Any] | None = None,
    source_uri: str | None = None,
    source_kind: str | None = None,
    source_id: str | None = None,
    source_label: str | None = None,
    sources: Sequence[dict[str, Any]] | None = None,
    options: dict[str, Any] | None = None,
    chunk_chars: int | None = None,
    chunk_overlap_chars: int | None = None,
    dry_run: bool | None = None,
    replace_document_chunks: bool | None = None,
    skip_unchanged_chunks: bool | None = None,
    reuse_existing_chunk_vectors: bool | None = None,
    deduplicate_existing_chunks: bool | None = None,
    persist_manifest: bool | None = None,
    include_trace: bool | None = None,
) -> dict[str, Any]:
    if not text or not text.strip():
        raise ValueError("text is required")
    if not partition_key or not partition_key.strip():
        raise ValueError("partition_key is required")

    request: dict[str, Any] = {
        "partitionKey": partition_key,
        "text": text,
    }
    optional_fields = {
        "documentId": document_id,
        "idPrefix": id_prefix,
        "type": record_type,
        "schemaVersion": schema_version,
        "contentField": content_field,
        "embedding": embedding,
        "metadata": metadata,
        "sourceUri": source_uri,
        "sourceKind": source_kind,
        "sourceId": source_id,
        "sourceLabel": source_label,
        "sources": list(sources) if sources is not None else None,
    }
    request.update({key: value for key, value in optional_fields.items() if value is not None})

    option_updates = {
        "chunkChars": chunk_chars,
        "chunkOverlapChars": chunk_overlap_chars,
        "dryRun": dry_run,
        "replaceDocumentChunks": replace_document_chunks,
        "skipUnchangedChunks": skip_unchanged_chunks,
        "reuseExistingChunkVectors": reuse_existing_chunk_vectors,
        "deduplicateExistingChunks": deduplicate_existing_chunks,
        "persistManifest": persist_manifest,
        "includeTrace": include_trace,
    }
    merged_options = dict(options or {})
    merged_options.update({key: value for key, value in option_updates.items() if value is not None})
    if merged_options:
        request["options"] = merged_options

    return request


def build_rag_context_request(
    query: str,
    collections: Sequence[str],
    *,
    partition_keys: Sequence[str] | None = None,
    record_filter: dict[str, Any] | None = None,
    profile: str | None = None,
    embedding: dict[str, Any] | None = None,
    vector_fields: Sequence[dict[str, Any]] | None = None,
    fields: Sequence[str] | None = None,
    field_boosts: dict[str, float] | None = None,
    lexical: dict[str, Any] | None = None,
    hybrid: dict[str, Any] | None = None,
    search_mode: str | None = None,
    limit: int = 8,
    min_score: float | None = None,
    rerank: dict[str, Any] | None = None,
    content_field: str = "text",
    max_chars: int = 4000,
    max_chars_per_chunk: int = 1200,
    max_citations_per_chunk: int | None = None,
    context_assembly: dict[str, Any] | None = None,
    graph_expansion: dict[str, Any] | None = None,
    include_records: bool = False,
    include_citations: bool = True,
    include_context_text: bool = True,
    include_trace: bool = True,
) -> dict[str, Any]:
    if not query or not query.strip():
        raise ValueError("query is required")
    if not collections:
        raise ValueError("collections is required")

    lexical_options = dict(lexical or {})
    lexical_options.setdefault("fields", list(fields or DEFAULT_RAG_RETRIEVAL_FIELDS))
    if field_boosts is not None:
        lexical_options["fieldBoosts"] = dict(field_boosts)
    effective_search_mode = search_mode or ("lexical" if profile is None else None)

    retrieval: dict[str, Any] = {
        "query": query,
        "collections": list(collections),
        "limit": _normalize_positive_integer(limit, "limit"),
        "includeTrace": include_trace,
    }
    if profile is not None:
        retrieval["profile"] = profile
    if effective_search_mode is not None:
        retrieval["searchMode"] = effective_search_mode
    if effective_search_mode == "lexical" or lexical is not None or fields is not None or field_boosts is not None:
        retrieval["lexical"] = lexical_options
    if partition_keys is not None:
        retrieval["partitionKeys"] = list(partition_keys)
    if record_filter is not None:
        retrieval["filter"] = record_filter
    if embedding is not None:
        retrieval["embedding"] = embedding
    if vector_fields is not None:
        retrieval["vectorFields"] = list(vector_fields)
    if hybrid is not None:
        retrieval["hybrid"] = hybrid
    if min_score is not None:
        retrieval["minScore"] = min_score
    if rerank is not None:
        retrieval["rerank"] = rerank

    request: dict[str, Any] = {
        "retrieval": retrieval,
        "contentField": content_field,
        "maxChars": _normalize_positive_integer(max_chars, "max_chars"),
        "maxCharsPerChunk": _normalize_positive_integer(max_chars_per_chunk, "max_chars_per_chunk"),
        "includeRecords": include_records,
        "includeCitations": include_citations,
        "includeContextText": include_context_text,
        "includeTrace": include_trace,
    }
    if max_citations_per_chunk is not None:
        request["maxCitationsPerChunk"] = _normalize_positive_integer(
            max_citations_per_chunk,
            "max_citations_per_chunk",
        )
    if context_assembly is not None:
        request["contextAssembly"] = context_assembly
    if graph_expansion is not None:
        request["graphExpansion"] = graph_expansion

    return request


def build_graph_collection_import_request(
    envelope: dict[str, Any],
    *,
    create_collection_if_missing: bool = True,
    replace_existing: bool = False,
    continue_on_error: bool = False,
    allow_non_graph_policy: bool = False,
) -> dict[str, Any]:
    if not isinstance(envelope, dict):
        raise ValueError("envelope is required")
    return {
        "envelope": envelope,
        "createCollectionIfMissing": create_collection_if_missing,
        "replaceExisting": replace_existing,
        "continueOnError": continue_on_error,
        "allowNonGraphPolicy": allow_non_graph_policy,
    }


def build_graph_collection_export_request(
    *,
    graph_id: str | None = None,
    namespace: str | None = None,
    tenant_id: str | None = None,
    partition_key: str | None = None,
    include_projections: bool = True,
    max_records: int | None = None,
    fail_on_limit_exceeded: bool = True,
) -> dict[str, Any]:
    request: dict[str, Any] = {
        "includeProjections": include_projections,
        "failOnLimitExceeded": fail_on_limit_exceeded,
    }
    optional = {
        "graphId": graph_id,
        "namespace": namespace,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
        "maxRecords": _normalize_positive_integer(max_records, "max_records") if max_records is not None else None,
    }
    request.update({key: value for key, value in optional.items() if value is not None})
    return request


def build_graph_traversal_request(
    start_node_ids: Sequence[str],
    *,
    graph_id: str | None = None,
    namespace: str | None = None,
    tenant_id: str | None = None,
    partition_key: str | None = None,
    profile: dict[str, Any] | None = None,
    max_records: int | None = None,
    allow_partial_graph: bool = False,
) -> dict[str, Any]:
    if not start_node_ids:
        raise ValueError("start_node_ids is required")
    request: dict[str, Any] = {
        "startNodeIds": list(start_node_ids),
        "profile": dict(profile or {}),
        "allowPartialGraph": allow_partial_graph,
    }
    optional = {
        "graphId": graph_id,
        "namespace": namespace,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
        "maxRecords": _normalize_positive_integer(max_records, "max_records") if max_records is not None else None,
    }
    request.update({key: value for key, value in optional.items() if value is not None})
    return request


def build_graph_inspection_request(
    *,
    graph_id: str | None = None,
    namespace: str | None = None,
    tenant_id: str | None = None,
    partition_key: str | None = None,
    max_records: int | None = None,
    allow_partial_graph: bool = False,
    include_anomalies: bool = True,
    anomaly_limit: int = 50,
) -> dict[str, Any]:
    if isinstance(anomaly_limit, bool) or not isinstance(anomaly_limit, int) or anomaly_limit < 0:
        raise ValueError("anomaly_limit must be a non-negative integer")
    request: dict[str, Any] = {
        "allowPartialGraph": allow_partial_graph,
        "includeAnomalies": include_anomalies,
        "anomalyLimit": anomaly_limit,
    }
    optional = {
        "graphId": graph_id,
        "namespace": namespace,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
        "maxRecords": _normalize_positive_integer(max_records, "max_records") if max_records is not None else None,
    }
    request.update({key: value for key, value in optional.items() if value is not None})
    return request


def build_graph_doctor_request(
    *,
    graph_id: str | None = None,
    namespace: str | None = None,
    tenant_id: str | None = None,
    partition_key: str | None = None,
    target_collection: str | None = None,
    target_partition_keys: Sequence[str] | None = None,
    seed_json_pointers: Sequence[str] | None = None,
    max_graph_records: int | None = None,
    max_target_records: int = 1000,
    allow_partial_graph: bool = False,
    include_anomalies: bool = True,
    anomaly_limit: int = 50,
) -> dict[str, Any]:
    if isinstance(anomaly_limit, bool) or not isinstance(anomaly_limit, int) or anomaly_limit < 0:
        raise ValueError("anomaly_limit must be a non-negative integer")
    request: dict[str, Any] = {
        "targetPartitionKeys": list(target_partition_keys or []),
        "maxTargetRecords": _normalize_positive_integer(max_target_records, "max_target_records"),
        "allowPartialGraph": allow_partial_graph,
        "includeAnomalies": include_anomalies,
        "anomalyLimit": anomaly_limit,
    }
    optional = {
        "graphId": graph_id,
        "namespace": namespace,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
        "targetCollection": target_collection,
        "seedJsonPointers": list(seed_json_pointers) if seed_json_pointers is not None else None,
        "maxGraphRecords": _normalize_positive_integer(max_graph_records, "max_graph_records") if max_graph_records is not None else None,
    }
    request.update({key: value for key, value in optional.items() if value is not None})
    return request


def build_graph_expansion_options(
    collection: str,
    *,
    enabled: bool = True,
    graph_id: str | None = None,
    namespace: str | None = None,
    tenant_id: str | None = None,
    partition_key: str | None = None,
    seed_node_ids: Sequence[str] | None = None,
    seed_json_pointers: Sequence[str] | None = None,
    max_seed_nodes: int = 16,
    profile: dict[str, Any] | None = None,
    max_records: int | None = None,
    allow_partial_graph: bool = False,
    include_graph_context_text: bool = True,
    max_graph_context_chars: int = 1200,
    include_graph_provenance: bool = True,
    max_graph_provenance_items: int = 64,
    fallback_on_failure: bool = True,
) -> dict[str, Any]:
    if not collection or not collection.strip():
        raise ValueError("collection is required")
    options: dict[str, Any] = {
        "enabled": enabled,
        "collection": collection,
        "seedNodeIds": list(seed_node_ids or []),
        "maxSeedNodes": _normalize_positive_integer(max_seed_nodes, "max_seed_nodes"),
        "profile": dict(profile or {}),
        "allowPartialGraph": allow_partial_graph,
        "includeGraphContextText": include_graph_context_text,
        "maxGraphContextChars": _normalize_positive_integer(max_graph_context_chars, "max_graph_context_chars"),
        "includeGraphProvenance": include_graph_provenance,
        "maxGraphProvenanceItems": _normalize_non_negative_integer(max_graph_provenance_items, "max_graph_provenance_items"),
        "fallbackOnFailure": fallback_on_failure,
    }
    optional = {
        "graphId": graph_id,
        "namespace": namespace,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
        "seedJsonPointers": list(seed_json_pointers) if seed_json_pointers is not None else None,
        "maxRecords": _normalize_positive_integer(max_records, "max_records") if max_records is not None else None,
    }
    options.update({key: value for key, value in optional.items() if value is not None})
    return options


def build_graph_scope(
    graph_id: str,
    *,
    namespace: str = "default",
    collection: str = "default",
    tenant_id: str = "",
    partition_key: str = "",
) -> dict[str, Any]:
    if not graph_id or not graph_id.strip():
        raise ValueError("graph_id is required")
    return {
        "graphId": graph_id,
        "namespace": namespace,
        "collection": collection,
        "tenantId": tenant_id,
        "partitionKey": partition_key,
    }


def build_graph_source_span(
    source_ref: str,
    *,
    char_start: int | None = None,
    char_end: int | None = None,
    unit: str = "utf16",
    locator: str | None = None,
    text_hash: str | None = None,
    metadata: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not source_ref or not source_ref.strip():
        raise ValueError("source_ref is required")
    return _omit_none({
        "sourceRef": source_ref,
        "charStart": char_start,
        "charEnd": char_end,
        "unit": unit,
        "locator": locator,
        "textHash": text_hash,
        "metadata": metadata,
    })


def build_graph_node(
    node_id: str,
    node_type: str,
    *,
    label: str | None = None,
    properties: dict[str, Any] | None = None,
    source_spans: Sequence[dict[str, Any]] | None = None,
    assertion_ids: Sequence[str] | None = None,
) -> dict[str, Any]:
    if not node_id or not node_id.strip():
        raise ValueError("node_id is required")
    if not node_type or not node_type.strip():
        raise ValueError("node_type is required")
    return _omit_none({
        "id": node_id,
        "type": node_type,
        "label": label,
        "properties": dict(properties) if properties is not None else None,
        "sourceSpans": list(source_spans or []),
        "assertionIds": list(assertion_ids or []),
    })


def build_graph_edge(
    edge_id: str,
    source_id: str,
    target_id: str,
    predicate: str,
    *,
    label: str | None = None,
    properties: dict[str, Any] | None = None,
    source_spans: Sequence[dict[str, Any]] | None = None,
    assertion_ids: Sequence[str] | None = None,
) -> dict[str, Any]:
    if not edge_id or not edge_id.strip():
        raise ValueError("edge_id is required")
    if not source_id or not source_id.strip():
        raise ValueError("source_id is required")
    if not target_id or not target_id.strip():
        raise ValueError("target_id is required")
    if not predicate or not predicate.strip():
        raise ValueError("predicate is required")
    return _omit_none({
        "id": edge_id,
        "sourceId": source_id,
        "targetId": target_id,
        "predicate": predicate,
        "label": label,
        "properties": dict(properties) if properties is not None else None,
        "sourceSpans": list(source_spans or []),
        "assertionIds": list(assertion_ids or []),
    })


def build_graph_assertion(
    assertion_id: str,
    subject_id: str,
    *,
    subject_kind: str = "node",
    status: str = "proposed",
    method: str = "unspecified",
    actor: str = "system",
    confidence: float | None = None,
    source_spans: Sequence[dict[str, Any]] | None = None,
    properties: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not assertion_id or not assertion_id.strip():
        raise ValueError("assertion_id is required")
    if not subject_id or not subject_id.strip():
        raise ValueError("subject_id is required")
    return _omit_none({
        "id": assertion_id,
        "subjectId": subject_id,
        "subjectKind": subject_kind,
        "status": status,
        "method": method,
        "actor": actor,
        "confidence": confidence,
        "sourceSpans": list(source_spans or []),
        "properties": dict(properties) if properties is not None else None,
    })


def build_graph_review(
    review_id: str,
    subject_id: str,
    status: str,
    reviewer: str,
    *,
    subject_kind: str = "assertion",
    notes: str | None = None,
    properties: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if not review_id or not review_id.strip():
        raise ValueError("review_id is required")
    if not subject_id or not subject_id.strip():
        raise ValueError("subject_id is required")
    if not status or not status.strip():
        raise ValueError("status is required")
    if not reviewer or not reviewer.strip():
        raise ValueError("reviewer is required")
    return _omit_none({
        "id": review_id,
        "subjectId": subject_id,
        "subjectKind": subject_kind,
        "status": status,
        "reviewer": reviewer,
        "notes": notes,
        "properties": dict(properties) if properties is not None else None,
    })


def build_graph_envelope(
    scope: dict[str, Any],
    *,
    nodes: Sequence[dict[str, Any]] | None = None,
    edges: Sequence[dict[str, Any]] | None = None,
    assertions: Sequence[dict[str, Any]] | None = None,
    reviews: Sequence[dict[str, Any]] | None = None,
    projections: Sequence[dict[str, Any]] | None = None,
    metadata: dict[str, Any] | None = None,
    schema: str = "roman.graph.v1",
) -> dict[str, Any]:
    if not isinstance(scope, dict):
        raise ValueError("scope is required")
    return _omit_none({
        "schema": schema,
        "scope": dict(scope),
        "metadata": dict(metadata) if metadata is not None else None,
        "nodes": list(nodes or []),
        "edges": list(edges or []),
        "assertions": list(assertions or []),
        "reviews": list(reviews or []),
        "projections": list(projections or []),
    })


def stamp_graph_node_metadata(
    record: dict[str, Any],
    graph_node_id: str,
    *,
    graph_node_ids: Sequence[str] | None = None,
) -> dict[str, Any]:
    if not isinstance(record, dict):
        raise ValueError("record is required")
    if not graph_node_id or not graph_node_id.strip():
        raise ValueError("graph_node_id is required")
    stamped = dict(record)
    metadata = dict(stamped.get("metadata") or {})
    metadata["graphNodeId"] = graph_node_id
    if graph_node_ids is not None:
        metadata["graphNodeIds"] = list(graph_node_ids)
    stamped["metadata"] = metadata
    return stamped


def build_rerank_options(
    *,
    provider: str | None = None,
    enabled: bool = True,
    mode: str = "advisory",
    candidate_limit: int | None = 8,
    max_candidate_chars: int = 1000,
    content_field: str = "text",
    rerank_score_weight: float | None = None,
    original_score_weight: float | None = None,
    timeout_seconds: int | None = None,
    max_output_bytes: int | None = None,
    fallback_on_failure: bool = True,
) -> dict[str, Any]:
    options: dict[str, Any] = {
        "enabled": enabled,
        "mode": mode,
        "maxCandidateChars": _normalize_positive_integer(max_candidate_chars, "max_candidate_chars"),
        "contentField": content_field,
        "fallbackOnFailure": fallback_on_failure,
    }
    optional = {
        "provider": provider,
        "candidateLimit": candidate_limit,
        "rerankScoreWeight": rerank_score_weight,
        "originalScoreWeight": original_score_weight,
        "timeoutSeconds": timeout_seconds,
        "maxOutputBytes": max_output_bytes,
    }
    options.update({key: value for key, value in optional.items() if value is not None})
    return options


def build_provider_run_request(
    capability: str,
    payload: dict[str, Any],
    *,
    provider: str | None = None,
    operation: str = "run",
    mode: str = "advisory",
    model_id: str | None = None,
    correlation_id: str | None = None,
    context_refs: Sequence[str] | None = None,
    timeout_seconds: int | None = None,
    max_output_bytes: int | None = None,
    artifact_directory: str | None = None,
) -> dict[str, Any]:
    if not capability or not capability.strip():
        raise ValueError("capability is required")
    if payload is None:
        raise ValueError("payload is required")

    request: dict[str, Any] = {
        "capability": capability,
        "operation": operation,
        "mode": mode,
        "payload": dict(payload),
    }
    optional = {
        "provider": provider,
        "modelId": model_id,
        "correlationId": correlation_id,
        "contextRefs": list(context_refs) if context_refs is not None else None,
        "timeoutSeconds": timeout_seconds,
        "maxOutputBytes": max_output_bytes,
        "artifactDirectory": artifact_directory,
    }
    request.update({key: value for key, value in optional.items() if value is not None})
    return request


def build_provider_chat_request(
    messages: Sequence[dict[str, Any]],
    *,
    system: str | None = None,
    max_output_chars: int | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not messages:
        raise ValueError("messages is required")
    payload: dict[str, Any] = {"messages": list(messages)}
    if system is not None:
        payload["system"] = system
    if max_output_chars is not None:
        payload["maxOutputChars"] = max_output_chars
    return build_provider_run_request("ai.chat", payload, **run_options)


def build_provider_extract_request(
    text: str,
    *,
    schema: dict[str, Any] | None = None,
    instructions: str | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not text or not text.strip():
        raise ValueError("text is required")
    payload: dict[str, Any] = {"text": text}
    if schema is not None:
        payload["schema"] = schema
    if instructions is not None:
        payload["instructions"] = instructions
    return build_provider_run_request("ai.extract", payload, **run_options)


def build_provider_rerank_request(
    query: str,
    candidates: Sequence[dict[str, Any]],
    *,
    limit: int | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not query or not query.strip():
        raise ValueError("query is required")
    if not candidates:
        raise ValueError("candidates is required")
    payload: dict[str, Any] = {"query": query, "candidates": [dict(candidate) for candidate in candidates]}
    if limit is not None:
        payload["limit"] = limit
    return build_provider_run_request("ai.rerank", payload, **run_options)


def build_provider_review_request(
    *,
    prompt: str | None = None,
    subject: str | None = None,
    instructions: str | None = None,
    references: Sequence[dict[str, Any]] | None = None,
    max_findings: int | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not any(value and value.strip() for value in (prompt, subject, instructions)) and not references:
        raise ValueError("prompt, subject, instructions, or references is required")
    payload: dict[str, Any] = {}
    optional = {
        "prompt": prompt,
        "subject": subject,
        "instructions": instructions,
        "references": [dict(reference) for reference in references] if references is not None else None,
        "maxFindings": max_findings,
    }
    payload.update({key: value for key, value in optional.items() if value is not None})
    return build_provider_run_request("ai.review", payload, **run_options)


def build_provider_scaffold_request(
    prompt: str,
    *,
    instructions: str | None = None,
    target: str | None = None,
    references: Sequence[dict[str, Any]] | None = None,
    allowed_paths: Sequence[str] | None = None,
    max_artifacts: int | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not prompt or not prompt.strip():
        raise ValueError("prompt is required")
    payload: dict[str, Any] = {"prompt": prompt}
    optional = {
        "instructions": instructions,
        "target": target,
        "references": [dict(reference) for reference in references] if references is not None else None,
        "allowedPaths": list(allowed_paths) if allowed_paths is not None else None,
        "maxArtifacts": max_artifacts,
    }
    payload.update({key: value for key, value in optional.items() if value is not None})
    return build_provider_run_request("ai.scaffold", payload, **run_options)


def build_provider_tool_plan_request(
    prompt: str,
    tools: Sequence[dict[str, Any]] | None = None,
    **run_options: Any,
) -> dict[str, Any]:
    if not prompt or not prompt.strip():
        raise ValueError("prompt is required")
    payload = {
        "prompt": prompt,
        "tools": [dict(tool) for tool in tools] if tools is not None else [],
    }
    return build_provider_run_request("ai.toolPlan", payload, **run_options)


def summarize_rag_ingest_result(result: dict[str, Any]) -> dict[str, Any]:
    summary = result.get("actionSummary")
    if isinstance(summary, dict):
        return {
            "actionCounts": dict(summary.get("actionCounts") or {}),
            "embeddingActionCounts": dict(summary.get("embeddingActionCounts") or {}),
            "createdIds": list(summary.get("createdIds") or []),
            "updatedIds": list(summary.get("updatedIds") or []),
            "reusedIds": list(summary.get("reusedIds") or []),
            "deduplicatedIds": list(summary.get("deduplicatedIds") or []),
            "staleDeleteIds": list(summary.get("staleDeleteIds") or []),
        }

    chunks = result.get("chunks") if isinstance(result.get("chunks"), list) else []
    stale_deletes = result.get("staleDeletes") if isinstance(result.get("staleDeletes"), list) else []
    action_counts = {"created": 0, "updated": 0, "reused": 0, "deduplicated": 0}
    embedding_action_counts = {"generated": 0, "reused": 0, "unchanged": 0, "deduplicated": 0}
    created_ids: list[str] = []
    updated_ids: list[str] = []
    reused_ids: list[str] = []
    deduplicated_ids: list[str] = []

    for chunk in chunks:
        if not isinstance(chunk, dict):
            continue
        chunk_id = str(chunk.get("id") or "")
        action = str(chunk.get("action") or "")
        embedding_action = str(chunk.get("embeddingAction") or "")
        if action:
            action_counts[action] = action_counts.get(action, 0) + 1
        if embedding_action:
            embedding_action_counts[embedding_action] = embedding_action_counts.get(embedding_action, 0) + 1
        if action == "created":
            created_ids.append(chunk_id)
        elif action == "updated":
            updated_ids.append(chunk_id)
        elif action == "reused":
            reused_ids.append(chunk_id)
        elif action == "deduplicated":
            deduplicated_ids.append(chunk_id)

    return {
        "actionCounts": action_counts,
        "embeddingActionCounts": embedding_action_counts,
        "createdIds": created_ids,
        "updatedIds": updated_ids,
        "reusedIds": reused_ids,
        "deduplicatedIds": deduplicated_ids,
        "staleDeleteIds": sorted(
            str(stale.get("id"))
            for stale in stale_deletes
            if isinstance(stale, dict) and stale.get("id") is not None
        ),
    }


def compare_rag_ingest_results(planned: dict[str, Any], committed: dict[str, Any]) -> dict[str, Any]:
    plan_expected = planned.get("planHash")
    plan_actual = committed.get("planHash")
    manifest_expected = planned.get("manifestHash")
    manifest_actual = committed.get("manifestHash")
    return {
        "planHash": _compare_hash("plan", plan_expected, plan_actual),
        "manifestHash": _compare_hash("manifest", manifest_expected, manifest_actual),
        "plannedSummary": summarize_rag_ingest_result(planned),
        "committedSummary": summarize_rag_ingest_result(committed),
    }


def _compare_hash(kind: str, expected: Any, actual: Any) -> dict[str, Any]:
    expected_hash = expected if isinstance(expected, str) and expected else None
    actual_hash = actual if isinstance(actual, str) and actual else None
    if expected_hash is None:
        return {
            "kind": kind,
            "expectedHash": None,
            "actualHash": actual_hash,
            "compared": False,
            "matches": False,
            "status": "not_provided",
        }
    matches = expected_hash == actual_hash
    return {
        "kind": kind,
        "expectedHash": expected_hash,
        "actualHash": actual_hash,
        "compared": True,
        "matches": matches,
        "status": "actual_missing" if actual_hash is None else "matched" if matches else "drifted",
    }


def _normalize_positive_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise ValueError(f"{name} must be a positive integer")
    return value


def _normalize_non_negative_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{name} must be a non-negative integer")
    return value


def _normalize_evaluation_match(id_or_match: str | dict[str, Any], label: str) -> dict[str, Any]:
    if isinstance(id_or_match, str):
        if not id_or_match.strip():
            raise ValueError(f"{label} id is required")
        return {"id": id_or_match}
    if isinstance(id_or_match, dict):
        match = dict(id_or_match)
        if "aliases" in id_or_match:
            match["aliases"] = list(id_or_match["aliases"])
        if "sourceIds" in id_or_match:
            match["sourceIds"] = list(id_or_match["sourceIds"])
        if "sources" in id_or_match:
            match["sources"] = list(id_or_match["sources"])
        return match
    raise ValueError(f"{label} is required")


def _normalize_evaluation_matches(
    matches: Sequence[str | dict[str, Any]] | str | dict[str, Any] | None,
    builder: Any,
) -> list[dict[str, Any]]:
    if matches is None:
        return []
    if isinstance(matches, (str, dict)):
        return [builder(matches)]
    return [builder(match) for match in matches]


class _MultipartBody:
    def __init__(
        self,
        boundary: str,
        manifest: Mapping[str, Any],
        artifact: BinaryIO | bytes,
        filename: str,
        content_type: str,
        chunk_size: int,
        cancellation_check: Callable[[], bool] | None = None,
    ):
        if chunk_size <= 0:
            raise ValueError("chunk_size must be greater than zero")
        if not filename or "\r" in filename or "\n" in filename:
            raise ValueError("filename must be non-empty and cannot contain line breaks")
        if not content_type or "\r" in content_type or "\n" in content_type:
            raise ValueError("content_type must be non-empty and cannot contain line breaks")

        manifest_json = json.dumps(dict(manifest), ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        ascii_filename = "".join(
            character if 0x20 <= ord(character) < 0x7F and character not in {'"', "\\"} else "_"
            for character in filename
        )
        encoded_filename = quote(filename, safe="")
        self._prefix = (
            f"--{boundary}\r\n"
            'Content-Disposition: form-data; name="manifest"\r\n'
            "Content-Type: application/json; charset=utf-8\r\n\r\n"
        ).encode("ascii") + manifest_json + (
            f"\r\n--{boundary}\r\n"
            f'Content-Disposition: form-data; name="artifact"; filename="{ascii_filename}"; '
            f"filename*=UTF-8''{encoded_filename}\r\n"
            f"Content-Type: {content_type}\r\n\r\n"
        ).encode("ascii")
        self._artifact = artifact
        self._suffix = f"\r\n--{boundary}--\r\n".encode("ascii")
        self._chunk_size = chunk_size
        self._cancellation_check = cancellation_check

    def __iter__(self) -> Iterator[bytes]:
        self._raise_if_cancelled()
        yield self._prefix
        if isinstance(self._artifact, bytes):
            if self._artifact:
                yield self._artifact
        else:
            while True:
                self._raise_if_cancelled()
                chunk = self._artifact.read(self._chunk_size)
                if not chunk:
                    break
                if not isinstance(chunk, bytes):
                    raise TypeError("artifact stream must return bytes")
                yield chunk
        self._raise_if_cancelled()
        yield self._suffix

    def _raise_if_cancelled(self) -> None:
        if self._cancellation_check is not None and self._cancellation_check():
            raise VyralClientError.cancelled()


_CREDENTIAL_HEADERS = {"authorization", "x-vyral-api-key"}


def _normalize_base_url(base_url: str) -> str:
    if not isinstance(base_url, str):
        raise ValueError("base_url must be an absolute HTTP(S) URL without user credentials")
    normalized = base_url.rstrip("/")
    try:
        parsed = urlsplit(normalized)
        hostname = parsed.hostname
    except ValueError as exc:
        raise ValueError("base_url must be an absolute HTTP(S) URL without user credentials") from exc
    if (
        parsed.scheme.lower() not in {"http", "https"}
        or not parsed.netloc
        or not hostname
        or parsed.username is not None
        or parsed.password is not None
        or bool(parsed.query)
        or bool(parsed.fragment)
    ):
        raise ValueError("base_url must be an absolute HTTP(S) URL without user credentials")
    return normalized


def _is_loopback_url(base_url: str) -> bool:
    hostname = urlsplit(base_url).hostname
    if hostname is None:
        return False
    if hostname.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(hostname).is_loopback
    except ValueError:
        return False


def _headers_include_credentials(headers: Mapping[str, str]) -> bool:
    return any(name.lower() in _CREDENTIAL_HEADERS and bool(value) for name, value in headers.items())


def _has_header(headers: Mapping[str, str], *names: str) -> bool:
    expected = {name.lower() for name in names}
    return any(name.lower() in expected for name in headers)


def _require_secure_credential_transport(base_url: str, headers: Mapping[str, str]) -> None:
    if (
        _headers_include_credentials(headers)
        and urlsplit(base_url).scheme.lower() != "https"
        and not _is_loopback_url(base_url)
    ):
        raise ValueError("Vyral credentials require HTTPS except on loopback")


@dataclass
class VyralClient:
    base_url: str = "http://localhost:5220"
    timeout: float = 30.0
    api_key: str | None = None
    bearer_token: str | None = None
    default_headers: Mapping[str, str] | None = None
    correlation_id: str | None = None
    max_retries: int = 0
    retry_backoff_seconds: float = 0.25
    cancellation_check: Callable[[], bool] | None = None

    def __post_init__(self) -> None:
        self.base_url = _normalize_base_url(self.base_url)
        self.default_headers = dict(self.default_headers or {})
        if self.timeout <= 0:
            raise ValueError("timeout must be greater than zero")
        if self.max_retries < 0:
            raise ValueError("max_retries must be non-negative")
        if self.retry_backoff_seconds < 0:
            raise ValueError("retry_backoff_seconds must be non-negative")
        if self.api_key and self.bearer_token:
            raise ValueError("api_key and bearer_token are mutually exclusive")
        configured_headers = dict(self.default_headers)
        if self.bearer_token and not _has_header(configured_headers, "Authorization"):
            configured_headers.setdefault("Authorization", f"Bearer {self.bearer_token}")
        if self.api_key and not _has_header(configured_headers, "Authorization", "X-Vyral-Api-Key"):
            configured_headers.setdefault("X-Vyral-Api-Key", self.api_key)
        _require_secure_credential_transport(self.base_url, configured_headers)

    def with_options(
        self,
        *,
        timeout: float | None = None,
        headers: Mapping[str, str] | None = None,
        correlation_id: str | None = None,
        max_retries: int | None = None,
        retry_backoff_seconds: float | None = None,
        cancellation_check: Callable[[], bool] | None = None,
    ) -> "VyralClient":
        merged_headers = dict(self.default_headers or {})
        merged_headers.update(headers or {})
        return replace(
            self,
            timeout=self.timeout if timeout is None else timeout,
            default_headers=merged_headers,
            correlation_id=self.correlation_id if correlation_id is None else correlation_id,
            max_retries=self.max_retries if max_retries is None else max_retries,
            retry_backoff_seconds=(
                self.retry_backoff_seconds if retry_backoff_seconds is None else retry_backoff_seconds
            ),
            cancellation_check=cancellation_check or self.cancellation_check,
        )

    def health(self) -> dict[str, Any]:
        return self._json("GET", "/health")

    def readiness(self) -> dict[str, Any]:
        return self._json("GET", "/readiness")

    def ingest_record_artifact(
        self,
        manifest: Mapping[str, Any],
        artifact: BinaryIO | bytes,
        *,
        filename: str = "artifact.bin",
        content_type: str = "application/octet-stream",
        chunk_size: int = 64 * 1024,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        boundary = f"vyral-{uuid.uuid4().hex}"
        body = _MultipartBody(
            boundary,
            manifest,
            artifact,
            filename,
            content_type,
            chunk_size,
            self.cancellation_check,
        )
        params = {
            key: value
            for key, value in {"productId": product_id, "tenantId": tenant_id}.items()
            if value is not None
        }
        suffix = f"?{urlencode(params)}" if params else ""
        return self._json(
            "POST",
            f"/ingest/record-artifact{suffix}",
            body=body,
            headers={
                "Content-Type": f"multipart/form-data; boundary={boundary}",
                **(
                    {"Idempotency-Key": idempotency_key}
                    if idempotency_key
                    else {}
                ),
            },
        )

    def openapi_contract(self) -> dict[str, Any]:
        return self._json("GET", "/openapi/vyral.json")

    def get_public_schema_contract(self) -> dict[str, Any]:
        return self._json("GET", "/contracts/schemas/vyral-public.schema.json")

    def list_canonical_migrations(self) -> list[dict[str, Any]]:
        return self._json("GET", "/canonical/migrations")

    def get_canonical_preflight(self) -> dict[str, Any]:
        return self._json("GET", "/canonical/preflight")

    def probe_canonical_data_plane(self) -> dict[str, Any]:
        return self._json("POST", "/canonical/preflight/probe")

    def apply_canonical_migrations(self, migrations: Sequence[Mapping[str, Any]]) -> None:
        self._json("POST", "/canonical/migrations", list(migrations))

    def commit_canonical_transaction(self, tenant_id: str, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/transactions", dict(request))

    def store_evidence_brief(
        self,
        tenant_id: str,
        brief: Mapping[str, Any],
        *,
        idempotency_key: str,
        expected_revision: int | None = None,
        correlation_id: str | None = None,
        actor: str | None = None,
        emit_change_event: bool = True,
        change_event_topic: str = EVIDENCE_BRIEF_CHANGED_EVENT_TOPIC,
    ) -> dict[str, Any]:
        return self.commit_canonical_transaction(
            tenant_id,
            build_evidence_brief_transaction(
                tenant_id,
                idempotency_key,
                brief,
                expected_revision=expected_revision,
                correlation_id=correlation_id,
                actor=actor,
                emit_change_event=emit_change_event,
                change_event_topic=change_event_topic,
            ),
        )

    def get_evidence_brief(
        self,
        tenant_id: str,
        brief_id: str,
        *,
        include_deleted: bool = False,
    ) -> dict[str, Any] | None:
        document = self.get_canonical_document(
            tenant_id,
            EVIDENCE_BRIEF_DOCUMENT_TYPE,
            brief_id,
            include_deleted=include_deleted,
        )
        if document is None:
            return None
        if document.get("documentType") != EVIDENCE_BRIEF_DOCUMENT_TYPE or document.get("schemaVersion") != EVIDENCE_BRIEF_SCHEMA:
            raise ValueError("canonical document is not a supported EvidenceBrief")
        brief = document.get("data")
        if not isinstance(brief, dict) or brief.get("schema") != EVIDENCE_BRIEF_SCHEMA or brief.get("id") != brief_id:
            raise ValueError("canonical EvidenceBrief data is invalid")
        return {"document": document, "brief": brief}

    def get_canonical_document(
        self,
        tenant_id: str,
        document_type: str,
        document_id: str,
        *,
        include_deleted: bool | None = None,
    ) -> dict[str, Any] | None:
        try:
            return self._json(
                "POST",
                f"/canonical/tenants/{quote(tenant_id, safe='')}/documents/read",
                {
                    "tenantId": tenant_id,
                    "documentType": document_type,
                    "id": document_id,
                    "includeDeleted": include_deleted or False,
                },
            )
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def query_canonical_documents(self, tenant_id: str, query: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/documents/query", dict(query))

    def iter_canonical_documents(
        self,
        tenant_id: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> Iterator[dict[str, Any]]:
        if max_pages <= 0:
            raise ValueError("max_pages must be greater than zero")
        if max_items is not None and max_items <= 0:
            raise ValueError("max_items must be greater than zero")
        request = dict(query or {})
        seen_tokens: set[str] = set()
        yielded = 0
        for _page_number in range(max_pages):
            page = self.query_canonical_documents(tenant_id, request)
            for item in page.get("items", []):
                if max_items is not None and yielded >= max_items:
                    return
                yield item
                yielded += 1
            token = page.get("continuationToken")
            if not token:
                return
            if token in seen_tokens:
                raise RuntimeError("Canonical document pagination returned a repeated continuation token")
            seen_tokens.add(token)
            request["continuationToken"] = token
        raise RuntimeError(f"Canonical document pagination exceeded max_pages={max_pages}")

    def query_all_canonical_documents(
        self,
        tenant_id: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> list[dict[str, Any]]:
        return list(self.iter_canonical_documents(
            tenant_id,
            query,
            max_pages=max_pages,
            max_items=max_items,
        ))

    def list_canonical_document_revisions(
        self,
        tenant_id: str,
        document_type: str,
        document_id: str,
        *,
        limit: int | None = None,
    ) -> list[dict[str, Any]]:
        request: dict[str, Any] = {"tenantId": tenant_id, "documentType": document_type, "id": document_id}
        if limit is not None:
            request["limit"] = limit
        return self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/documents/revisions", request)

    def lease_canonical_outbox(self, tenant_id: str, request: Mapping[str, Any]) -> list[dict[str, Any]]:
        return self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/leases", dict(request))

    def query_canonical_outbox(self, tenant_id: str, query: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/query", dict(query))

    def renew_canonical_outbox_lease(self, tenant_id: str, event_id: str, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json(
            "POST",
            f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/{quote(event_id, safe='')}/renew",
            dict(request),
        )

    def acknowledge_canonical_outbox(self, tenant_id: str, event_id: str, lease_token: str) -> None:
        self._json(
            "POST",
            f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/{quote(event_id, safe='')}/ack",
            {"leaseToken": lease_token},
        )

    def release_canonical_outbox(self, tenant_id: str, event_id: str, request: Mapping[str, Any]) -> None:
        self._json(
            "POST",
            f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/{quote(event_id, safe='')}/nack",
            dict(request),
        )

    def replay_canonical_outbox(self, tenant_id: str, event_id: str, request: Mapping[str, Any]) -> None:
        self._json(
            "POST",
            f"/canonical/tenants/{quote(tenant_id, safe='')}/outbox/{quote(event_id, safe='')}/replay",
            dict(request),
        )

    def export_canonical_tenant(self, tenant_id: str) -> dict[str, Any]:
        return self._json("GET", f"/canonical/tenants/{quote(tenant_id, safe='')}/export")

    def restore_canonical_tenant(
        self,
        tenant_id: str,
        snapshot: Mapping[str, Any],
        *,
        expected_content_hash: str | None = None,
    ) -> None:
        request: dict[str, Any] = {"snapshot": dict(snapshot)}
        if expected_content_hash is not None:
            request["expectedContentHash"] = expected_content_hash
        self._json("POST", f"/canonical/tenants/{quote(tenant_id, safe='')}/restore", request)

    def list_graph_provider_shapes(self) -> list[dict[str, Any]]:
        return self._json("GET", "/graph/provider-shapes")

    def get_graph_provider_shape(self, provider_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/graph/provider-shapes/{quote(provider_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_embedding_providers(self) -> list[dict[str, Any]]:
        return self._json("GET", "/embedding-providers")

    def list_embedding_provider_guidance(self) -> list[dict[str, Any]]:
        return self._json("GET", "/embedding-providers/guidance")

    def get_embedding_provider_doctor(self) -> dict[str, Any]:
        return self._json("GET", "/embedding-providers/doctor")

    def get_execution_runtime(self) -> dict[str, Any]:
        return self._json("GET", "/execution/runtime")

    def get_effective_execution_runtime(
        self,
        *,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        query = {
            key: value
            for key, value in {
                "productId": product_id,
                "tenantId": tenant_id,
            }.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/execution/runtime/effective{suffix}")

    def get_execution_runtime_maintenance(self) -> dict[str, Any]:
        return self._json("GET", "/execution/runtime/maintenance")

    def prune_execution_runtime_maintenance(
        self,
        *,
        dry_run: bool = True,
        retain_terminal_runs: int | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {"dryRun": dry_run}
        if retain_terminal_runs is not None:
            request["retainTerminalRuns"] = retain_terminal_runs
        return self._json("POST", "/execution/runtime/maintenance/prune", request)

    def reconcile_execution_runtime_dispatch(
        self,
        *,
        dry_run: bool = False,
        limit: int | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {"dryRun": dry_run}
        if limit is not None:
            request["limit"] = limit
        return self._json("POST", "/execution/runtime/maintenance/reconcile", request)

    def lease_external_execution_run(self, request: Mapping[str, Any]) -> dict[str, Any] | None:
        return self._json("POST", "/execution/workers/leases", dict(request))

    def heartbeat_external_execution_lease(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/heartbeat", dict(request))

    def report_external_execution_lease(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/reports", dict(request))

    def record_external_execution_lease_event(self, request: Mapping[str, Any]) -> None:
        self._json("POST", "/execution/workers/leases/events", dict(request))

    def put_external_execution_lease_artifact(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/artifacts", dict(request))

    def put_external_execution_lease_checkpoint(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/checkpoints", dict(request))

    def get_external_execution_lease_checkpoint(self, request: Mapping[str, Any]) -> dict[str, Any] | None:
        try:
            return self._json("POST", "/execution/workers/leases/checkpoints/read", dict(request))
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_external_execution_lease(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/wait", dict(request))

    def complete_external_execution_lease(self, request: Mapping[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/execution/workers/leases/complete", dict(request))

    def list_execution_runs(
        self,
        *,
        handler_id: str | None = None,
        plugin_id: str | None = None,
        status: str | None = None,
        correlation_id: str | None = None,
        idempotency_key: str | None = None,
        created_after_utc: str | None = None,
        created_before_utc: str | None = None,
        updated_after_utc: str | None = None,
        updated_before_utc: str | None = None,
        tags: Mapping[str, str] | None = None,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {
                "handlerId": handler_id,
                "pluginId": plugin_id,
                "status": status,
                "correlationId": correlation_id,
                "idempotencyKey": idempotency_key,
                "createdAfterUtc": created_after_utc,
                "createdBeforeUtc": created_before_utc,
                "updatedAfterUtc": updated_after_utc,
                "updatedBeforeUtc": updated_before_utc,
                "limit": limit,
            }.items()
            if value is not None
        }
        for key, value in (tags or {}).items():
            query[f"tag.{key}"] = value
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/execution/runs{suffix}")

    def start_execution_run(
        self,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", "/execution/runs", dict(request), headers=headers)

    def raise_execution_event(self, run_id: str, request: Mapping[str, Any]) -> dict[str, Any]:
        payload = dict(request)
        request_run_id = payload.get("runId")
        if request_run_id not in (None, "", run_id):
            raise ValueError("request runId must match run_id")
        return self._json("POST", f"/execution/runs/{quote(run_id, safe='')}/events", payload)

    def get_execution_run(self, run_id: str, *, include_result: bool | None = None) -> dict[str, Any] | None:
        query = {}
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        try:
            return self._json("GET", f"/execution/runs/{quote(run_id, safe='')}{suffix}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_execution_run(self, run_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/execution/runs/{quote(run_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_execution_run(
        self,
        run_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
        *,
        include_result: bool = True,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            run = self.get_execution_run(run_id, include_result=include_result)
            if run is None or is_execution_run_terminal(run):
                return run

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Execution run {run_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def get_execution_run_history(self, run_id: str, *, limit: int | None = None) -> list[dict[str, Any]]:
        query = {"limit": limit} if limit is not None else {}
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/execution/runs/{quote(run_id, safe='')}/history{suffix}")

    def list_execution_run_artifacts(self, run_id: str) -> list[dict[str, Any]]:
        return self._json("GET", f"/execution/runs/{quote(run_id, safe='')}/artifacts")

    def get_execution_run_artifact(self, run_id: str, artifact_ref: str) -> dict[str, Any] | None:
        try:
            return self._json(
                "GET",
                f"/execution/runs/{quote(run_id, safe='')}/artifacts/{quote(artifact_ref, safe='')}",
            )
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def get_execution_run_checkpoint(self, run_id: str, key: str) -> dict[str, Any] | None:
        try:
            return self._json(
                "GET",
                f"/execution/runs/{quote(run_id, safe='')}/checkpoints/{quote(key, safe='')}",
            )
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_providers(self) -> list[dict[str, Any]]:
        return self._json("GET", "/providers")

    def get_provider_capability_matrix(self) -> dict[str, Any]:
        return self._json("GET", "/providers/capabilities")

    def get_provider(self, provider: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/providers/{quote(provider, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_provider_doctor(self) -> list[dict[str, Any]]:
        return self._json("GET", "/providers/doctor")

    def get_provider_doctor(self, provider: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/providers/{quote(provider, safe='')}/doctor")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_provider_readiness(self) -> dict[str, Any]:
        return self._json("GET", "/providers/readiness")

    def get_provider_readiness(self, provider: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/providers/{quote(provider, safe='')}/readiness")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_provider_quotas(self) -> list[dict[str, Any]]:
        return self._json("GET", "/providers/quotas")

    def get_provider_quota(self, provider: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/providers/{quote(provider, safe='')}/quota")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def list_provider_qualifications(self, provider: str) -> list[dict[str, Any]]:
        return self._json("GET", f"/providers/{quote(provider, safe='')}/qualifications")

    def list_provider_models(self, provider: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/providers/{quote(provider, safe='')}/models")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def qualify_provider(self, provider: str, request: dict[str, Any] | None = None) -> list[dict[str, Any]]:
        return self._json("POST", f"/providers/{quote(provider, safe='')}/qualify", request or {})

    def run_provider(
        self,
        provider: str,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/providers/{quote(provider, safe='')}/run",
            request,
            headers=headers,
        )

    def run_provider_extract(
        self,
        provider: str,
        text: str,
        *,
        schema: dict[str, Any] | None = None,
        instructions: str | None = None,
        model_id: str | None = None,
        mode: str = "advisory",
        timeout_seconds: int | None = None,
        max_output_bytes: int | None = None,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        request = build_provider_extract_request(
            text,
            schema=schema,
            instructions=instructions,
            model_id=model_id,
            mode=mode,
            timeout_seconds=timeout_seconds,
            max_output_bytes=max_output_bytes,
        )
        return self.run_provider(
            provider,
            request,
            idempotency_key=idempotency_key,
        )

    def start_provider_job(
        self,
        provider: str,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/providers/{quote(provider, safe='')}/jobs",
            request,
            headers=headers,
        )

    def list_provider_jobs(
        self,
        provider: str | None = None,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {
                "provider": provider,
                "limit": limit,
            }.items()
            if value is not None
        }
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/provider-jobs{suffix}")

    def get_provider_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/provider-jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_provider_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/provider-jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_provider_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_provider_job(job_id)
            if job is None or job.get("status") in TERMINAL_PROVIDER_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Provider job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def embed_text(
        self,
        text: str,
        *,
        purpose: str | None = None,
        query_prefix: str | None = None,
        passage_prefix: str | None = None,
        symmetric_prefix: str | None = None,
    ) -> list[float]:
        response = self.embed_texts(
            [text],
            purpose=purpose,
            query_prefix=query_prefix,
            passage_prefix=passage_prefix,
            symmetric_prefix=symmetric_prefix,
        )
        return response["items"][0]["values"]

    def embed_texts(
        self,
        texts: list[str],
        *,
        purpose: str | None = None,
        query_prefix: str | None = None,
        passage_prefix: str | None = None,
        symmetric_prefix: str | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {"texts": texts}
        if purpose is not None:
            request["purpose"] = purpose
        if query_prefix is not None:
            request["queryPrefix"] = query_prefix
        if passage_prefix is not None:
            request["passagePrefix"] = passage_prefix
        if symmetric_prefix is not None:
            request["symmetricPrefix"] = symmetric_prefix
        return self._json("POST", "/embeddings", request)

    def start_embedding_job(
        self,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", "/embeddings/jobs", request, headers=headers)

    def list_embedding_jobs(
        self,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {
                "limit": limit,
            }.items()
            if value is not None
        }
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/embeddings/jobs{suffix}")

    def get_embedding_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/embeddings/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_embedding_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/embeddings/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_embedding_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_embedding_job(job_id)
            if job is None or job.get("status") in TERMINAL_EMBEDDING_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Embedding job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def list_collections(self) -> list[str]:
        return self._json("GET", "/collections")

    def create_collection(
        self,
        policy: dict[str, Any],
        *,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        params = {
            key: value
            for key, value in {"productId": product_id, "tenantId": tenant_id}.items()
            if value is not None
        }
        suffix = f"?{urlencode(params)}" if params else ""
        return self._json("POST", f"/collections{suffix}", policy, headers=headers)

    def create_rag_collection(
        self,
        collection: str,
        *,
        dimensions: int | None = None,
        embedding_field: str = "contentEmbedding",
        indexed_metadata: Sequence[str] | None = None,
        partition_key_path: str = "/partitionKey",
        datatype: str = "float32",
        distance_function: str = "cosine",
        index_type: str = "flat",
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        resolved_dimensions = dimensions if dimensions is not None else self.health()["embedding"]["dimensions"]
        policy = build_rag_collection_policy(
            collection,
            dimensions=resolved_dimensions,
            embedding_field=embedding_field,
            indexed_metadata=indexed_metadata,
            partition_key_path=partition_key_path,
            datatype=datatype,
            distance_function=distance_function,
            index_type=index_type,
        )
        return self.create_collection(
            policy,
            idempotency_key=idempotency_key,
            product_id=product_id,
            tenant_id=tenant_id,
        )

    def get_collection_policy(self, collection: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/collections/{quote(collection, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def export_collection(
        self,
        collection: str,
        *,
        query: dict[str, Any] | None = None,
        max_records: int | None = None,
        fail_on_limit_exceeded: bool = True,
    ) -> dict[str, Any] | None:
        try:
            if query is not None or max_records is not None or not fail_on_limit_exceeded:
                request: dict[str, Any] = {
                    "failOnLimitExceeded": fail_on_limit_exceeded,
                }
                if query is not None:
                    request["query"] = query
                if max_records is not None:
                    request["maxRecords"] = max_records
                return self._json("POST", f"/collections/{quote(collection, safe='')}/export", request)
            return self._json("GET", f"/collections/{quote(collection, safe='')}/export")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def import_collection(
        self,
        collection: str,
        snapshot: dict[str, Any],
        *,
        expected_content_hash: str | None = None,
        replace_existing: bool = False,
        continue_on_error: bool = False,
        allow_collection_rename: bool = False,
        allow_partial_snapshot: bool = False,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {
            "snapshot": snapshot,
            "replaceExisting": replace_existing,
            "continueOnError": continue_on_error,
            "allowCollectionRename": allow_collection_rename,
            "allowPartialSnapshot": allow_partial_snapshot,
        }
        if expected_content_hash is not None:
            request["expectedContentHash"] = expected_content_hash
        query = {
            key: value
            for key, value in {
                "productId": product_id,
                "tenantId": tenant_id,
            }.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", f"/collections/{quote(collection, safe='')}/import{suffix}", request, headers=headers)

    def start_collection_import_job(
        self,
        collection: str,
        snapshot: dict[str, Any],
        *,
        expected_content_hash: str | None = None,
        replace_existing: bool = False,
        continue_on_error: bool = False,
        allow_collection_rename: bool = False,
        allow_partial_snapshot: bool = False,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {
            "snapshot": snapshot,
            "replaceExisting": replace_existing,
            "continueOnError": continue_on_error,
            "allowCollectionRename": allow_collection_rename,
            "allowPartialSnapshot": allow_partial_snapshot,
        }
        if expected_content_hash is not None:
            request["expectedContentHash"] = expected_content_hash
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", f"/collections/{quote(collection, safe='')}/import/jobs", request, headers=headers)

    def import_graph_envelope(
        self,
        collection: str,
        envelope: dict[str, Any],
        *,
        create_collection_if_missing: bool = True,
        replace_existing: bool = False,
        continue_on_error: bool = False,
        allow_non_graph_policy: bool = False,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        request = build_graph_collection_import_request(
            envelope,
            create_collection_if_missing=create_collection_if_missing,
            replace_existing=replace_existing,
            continue_on_error=continue_on_error,
            allow_non_graph_policy=allow_non_graph_policy,
        )
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/import", request, headers=headers)

    def preflight_graph_import(
        self,
        collection: str,
        envelope: dict[str, Any],
        *,
        create_collection_if_missing: bool = True,
        replace_existing: bool = False,
        continue_on_error: bool = False,
        allow_non_graph_policy: bool = False,
    ) -> dict[str, Any]:
        request = build_graph_collection_import_request(
            envelope,
            create_collection_if_missing=create_collection_if_missing,
            replace_existing=replace_existing,
            continue_on_error=continue_on_error,
            allow_non_graph_policy=allow_non_graph_policy,
        )
        return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/import/preflight", request)

    def export_graph_envelope(
        self,
        collection: str,
        *,
        graph_id: str | None = None,
        namespace: str | None = None,
        tenant_id: str | None = None,
        partition_key: str | None = None,
        include_projections: bool = True,
        max_records: int | None = None,
        fail_on_limit_exceeded: bool = True,
    ) -> dict[str, Any] | None:
        request = build_graph_collection_export_request(
            graph_id=graph_id,
            namespace=namespace,
            tenant_id=tenant_id,
            partition_key=partition_key,
            include_projections=include_projections,
            max_records=max_records,
            fail_on_limit_exceeded=fail_on_limit_exceeded,
        )
        try:
            return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/export", request)
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def traverse_graph(
        self,
        collection: str,
        start_node_ids: Sequence[str],
        *,
        graph_id: str | None = None,
        namespace: str | None = None,
        tenant_id: str | None = None,
        partition_key: str | None = None,
        profile: dict[str, Any] | None = None,
        max_records: int | None = None,
        allow_partial_graph: bool = False,
    ) -> dict[str, Any] | None:
        request = build_graph_traversal_request(
            start_node_ids,
            graph_id=graph_id,
            namespace=namespace,
            tenant_id=tenant_id,
            partition_key=partition_key,
            profile=profile,
            max_records=max_records,
            allow_partial_graph=allow_partial_graph,
        )
        try:
            return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/traverse", request)
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def inspect_graph(
        self,
        collection: str,
        *,
        graph_id: str | None = None,
        namespace: str | None = None,
        tenant_id: str | None = None,
        partition_key: str | None = None,
        max_records: int | None = None,
        allow_partial_graph: bool = False,
        include_anomalies: bool = True,
        anomaly_limit: int = 50,
    ) -> dict[str, Any] | None:
        request = build_graph_inspection_request(
            graph_id=graph_id,
            namespace=namespace,
            tenant_id=tenant_id,
            partition_key=partition_key,
            max_records=max_records,
            allow_partial_graph=allow_partial_graph,
            include_anomalies=include_anomalies,
            anomaly_limit=anomaly_limit,
        )
        try:
            return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/inspect", request)
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def doctor_graph(
        self,
        collection: str,
        *,
        graph_id: str | None = None,
        namespace: str | None = None,
        tenant_id: str | None = None,
        partition_key: str | None = None,
        target_collection: str | None = None,
        target_partition_keys: Sequence[str] | None = None,
        seed_json_pointers: Sequence[str] | None = None,
        max_graph_records: int | None = None,
        max_target_records: int = 1000,
        allow_partial_graph: bool = False,
        include_anomalies: bool = True,
        anomaly_limit: int = 50,
    ) -> dict[str, Any] | None:
        request = build_graph_doctor_request(
            graph_id=graph_id,
            namespace=namespace,
            tenant_id=tenant_id,
            partition_key=partition_key,
            target_collection=target_collection,
            target_partition_keys=target_partition_keys,
            seed_json_pointers=seed_json_pointers,
            max_graph_records=max_graph_records,
            max_target_records=max_target_records,
            allow_partial_graph=allow_partial_graph,
            include_anomalies=include_anomalies,
            anomaly_limit=anomaly_limit,
        )
        try:
            return self._json("POST", f"/collections/{quote(collection, safe='')}/graph/doctor", request)
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def start_graph_import_job(
        self,
        collection: str,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/graph/import/jobs",
            dict(request),
            headers=headers,
        )

    def start_graph_inspection_job(
        self,
        collection: str,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/graph/inspect/jobs",
            dict(request),
            headers=headers,
        )

    def start_graph_doctor_job(
        self,
        collection: str,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/graph/doctor/jobs",
            dict(request),
            headers=headers,
        )

    def list_graph_jobs(
        self,
        *,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {"limit": limit}.items()
            if value is not None
        }
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/graph/jobs{suffix}")

    def get_graph_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/graph/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_graph_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/graph/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_graph_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_graph_job(job_id)
            if job is None or job.get("status") in TERMINAL_GRAPH_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Graph job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def inspect_collection(
        self,
        collection: str,
        *,
        include_anomalies: bool | None = None,
        anomaly_limit: int | None = None,
    ) -> dict[str, Any]:
        params: dict[str, str] = {}
        if include_anomalies is not None:
            params["includeAnomalies"] = str(include_anomalies).lower()
        if anomaly_limit is not None:
            params["anomalyLimit"] = str(anomaly_limit)

        suffix = f"?{urlencode(params)}" if params else ""
        return self._json("GET", f"/collections/{quote(collection, safe='')}/inspect{suffix}")

    def delete_collection(
        self,
        collection: str,
        *,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        params = {
            key: value
            for key, value in {"productId": product_id, "tenantId": tenant_id}.items()
            if value is not None
        }
        suffix = f"?{urlencode(params)}" if params else ""
        return self._json(
            "DELETE",
            f"/collections/{quote(collection, safe='')}{suffix}",
            headers=headers,
        )

    def upsert_record(self, collection: str, record: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/collections/{quote(collection, safe='')}/records", record)

    def upsert_records(
        self,
        collection: str,
        records: list[dict[str, Any]],
        continue_on_error: bool = False,
        *,
        preconditions: list[dict[str, Any] | None] | None = None,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {
            "records": records,
            "continueOnError": continue_on_error,
        }
        if preconditions is not None:
            request["preconditions"] = preconditions
        query = {
            key: value
            for key, value in {
                "productId": product_id,
                "tenantId": tenant_id,
            }.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/records/batch{suffix}",
            request,
            headers=headers,
        )

    def start_record_batch_upsert_job(
        self,
        collection: str,
        records: list[dict[str, Any]],
        *,
        preconditions: list[dict[str, Any] | None] | None = None,
        continue_on_error: bool = False,
        idempotency_key: str | None = None,
        product_id: str | None = None,
        tenant_id: str | None = None,
    ) -> dict[str, Any]:
        request: dict[str, Any] = {"records": records, "continueOnError": continue_on_error}
        if preconditions is not None:
            request["preconditions"] = preconditions
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        query = {
            key: value
            for key, value in {
                "productId": product_id,
                "tenantId": tenant_id,
            }.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/records/batch/jobs{suffix}",
            request,
            headers=headers,
        )

    def list_record_import_jobs(
        self,
        *,
        limit: int | None = None,
        include_result: bool = False,
    ) -> list[dict[str, Any]]:
        params: dict[str, str] = {"includeResult": str(include_result).lower()}
        if limit is not None:
            params["limit"] = str(limit)
        return self._json("GET", f"/record-import/jobs?{urlencode(params)}")

    def get_record_import_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/record-import/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_record_import_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/record-import/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_record_import_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_record_import_job(job_id)
            if job is None or job.get("status") in TERMINAL_RECORD_IMPORT_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Record import job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def get_record(self, collection: str, partition_key: str, record_id: str) -> dict[str, Any] | None:
        path = (
            f"/collections/{quote(collection, safe='')}/records/"
            f"{quote(partition_key, safe='')}/{quote(record_id, safe='')}"
        )
        try:
            return self._json("GET", path)
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def delete_record(self, collection: str, partition_key: str, record_id: str) -> None:
        path = (
            f"/collections/{quote(collection, safe='')}/records/"
            f"{quote(partition_key, safe='')}/{quote(record_id, safe='')}"
        )
        self._request("DELETE", path)

    def query_records(self, collection: str, query: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/collections/{quote(collection, safe='')}/query", query)

    def iter_records(
        self,
        collection: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> Iterator[dict[str, Any]]:
        if max_pages <= 0:
            raise ValueError("max_pages must be greater than zero")
        if max_items is not None and max_items <= 0:
            raise ValueError("max_items must be greater than zero")
        request = dict(query or {})
        seen_tokens: set[str] = set()
        yielded = 0
        for _page_number in range(max_pages):
            page = self.query_records(collection, request)
            for item in page.get("items", []):
                if max_items is not None and yielded >= max_items:
                    return
                yield item
                yielded += 1
            continuation_token = page.get("continuationToken")
            if not continuation_token:
                return
            if continuation_token in seen_tokens:
                raise RuntimeError("Record pagination returned a repeated continuation token")
            seen_tokens.add(continuation_token)
            request["continuationToken"] = continuation_token

        raise RuntimeError(f"Record pagination exceeded max_pages={max_pages}")

    def query_all_records(
        self,
        collection: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> list[dict[str, Any]]:
        return list(self.iter_records(collection, query, max_pages=max_pages, max_items=max_items))

    def search_records(self, collection: str, query: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", f"/collections/{quote(collection, safe='')}/search", query)

    def iter_search_records(
        self,
        collection: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> Iterator[dict[str, Any]]:
        if max_pages <= 0:
            raise ValueError("max_pages must be greater than zero")
        if max_items is not None and max_items <= 0:
            raise ValueError("max_items must be greater than zero")
        request = dict(query or {})
        seen_tokens: set[str] = set()
        yielded = 0
        for _page_number in range(max_pages):
            page = self.search_records(collection, request)
            for item in page.get("items", []):
                if max_items is not None and yielded >= max_items:
                    return
                yield item
                yielded += 1
            continuation_token = page.get("continuationToken")
            if not continuation_token:
                return
            if continuation_token in seen_tokens:
                raise RuntimeError("Search pagination returned a repeated continuation token")
            seen_tokens.add(continuation_token)
            request["continuationToken"] = continuation_token

        raise RuntimeError(f"Search pagination exceeded max_pages={max_pages}")

    def search_all_records(
        self,
        collection: str,
        query: Mapping[str, Any] | None = None,
        *,
        max_pages: int = 1000,
        max_items: int | None = None,
    ) -> list[dict[str, Any]]:
        return list(self.iter_search_records(collection, query, max_pages=max_pages, max_items=max_items))

    def retrieve(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/search", request)

    def list_retrieval_profiles(self) -> list[dict[str, Any]]:
        return self._json("GET", "/retrieval/profiles")

    def evaluate_retrieval(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/retrieval/evaluate", request)

    def compare_retrieval_evaluations(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/retrieval/evaluate/compare", request)

    def start_retrieval_evaluation_job(
        self,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", "/retrieval/evaluate/jobs", request, headers=headers)

    def start_retrieval_evaluation_comparison_job(
        self,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            "/retrieval/evaluate/compare/jobs",
            request,
            headers=headers,
        )

    def list_retrieval_evaluation_jobs(
        self,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {
                "limit": limit,
            }.items()
            if value is not None
        }
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/retrieval/evaluate/jobs{suffix}")

    def get_retrieval_evaluation_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/retrieval/evaluate/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_retrieval_evaluation_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/retrieval/evaluate/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_retrieval_evaluation_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_retrieval_evaluation_job(job_id)
            if job is None or job.get("status") in TERMINAL_RETRIEVAL_EVALUATION_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"Retrieval evaluation job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def build_rag_context(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/rag/context", request)

    def evaluate_rag_context(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/rag/context/evaluate", request)

    def build_rag_prompt(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/rag/prompt", request)

    def ingest_rag_text(
        self,
        collection: str,
        request: dict[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json("POST", f"/collections/{quote(collection, safe='')}/rag/ingest-text", request, headers=headers)

    def plan_rag_text_ingestion(self, collection: str, request: dict[str, Any]) -> dict[str, Any]:
        payload = _with_rag_ingestion_options(request, {"dryRun": True})
        return self.ingest_rag_text(collection, payload)

    def commit_rag_text_ingestion(
        self,
        collection: str,
        request: dict[str, Any],
        planned_result: dict[str, Any] | None = None,
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        payload = _with_rag_ingestion_options(request, {"dryRun": False})
        options = dict(payload.get("options") or {})
        if planned_result:
            options.setdefault("expectedPlanHash", planned_result.get("planHash"))
            if planned_result.get("manifestHash") is not None:
                options.setdefault("expectedManifestHash", planned_result.get("manifestHash"))
        payload["options"] = options
        return self.ingest_rag_text(
            collection, payload, idempotency_key=idempotency_key
        )

    def ingest_rag_texts(
        self,
        collection: str,
        requests: list[dict[str, Any]],
        continue_on_error: bool = False,
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/rag/ingest-text/batch",
            {"items": requests, "continueOnError": continue_on_error},
            headers=headers,
        )

    def start_rag_text_ingestion_job(
        self,
        collection: str,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/rag/ingest-text/jobs",
            dict(request),
            headers=headers,
        )

    def start_rag_text_batch_ingestion_job(
        self,
        collection: str,
        request: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        headers = {"Idempotency-Key": idempotency_key} if idempotency_key else None
        return self._json(
            "POST",
            f"/collections/{quote(collection, safe='')}/rag/ingest-text/batch/jobs",
            dict(request),
            headers=headers,
        )

    def list_rag_ingestion_jobs(
        self,
        *,
        limit: int | None = None,
        include_result: bool | None = None,
    ) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {"limit": limit}.items()
            if value is not None
        }
        if include_result is not None:
            query["includeResult"] = str(include_result).lower()
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/rag/ingestion/jobs{suffix}")

    def get_rag_ingestion_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/rag/ingestion/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def cancel_rag_ingestion_job(self, job_id: str) -> dict[str, Any] | None:
        try:
            return self._json("DELETE", f"/rag/ingestion/jobs/{quote(job_id, safe='')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def wait_rag_ingestion_job(
        self,
        job_id: str,
        timeout_seconds: float = 120.0,
        poll_interval_seconds: float = 1.0,
    ) -> dict[str, Any] | None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")
        if poll_interval_seconds < 0:
            raise ValueError("poll_interval_seconds must be non-negative")

        deadline = time.monotonic() + timeout_seconds
        while True:
            job = self.get_rag_ingestion_job(job_id)
            if job is None or job.get("status") in TERMINAL_RAG_INGESTION_JOB_STATUSES:
                return job

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"RAG ingestion job {job_id} did not complete within {timeout_seconds} seconds")

            time.sleep(min(poll_interval_seconds, remaining))

    def list_objects(
        self,
        container: str,
        prefix: str | None = None,
        limit: int | None = None,
        continuation_token: str | None = None,
    ) -> dict[str, Any]:
        query = {
            key: value
            for key, value in {
                "prefix": prefix,
                "limit": limit,
                "continuationToken": continuation_token,
            }.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/objects/{quote(container)}{suffix}")

    def put_object(
        self,
        container: str,
        key: str,
        content: bytes | BinaryIO,
        content_type: str | None = None,
        metadata: dict[str, str] | None = None,
        if_match: str | None = None,
        if_none_match: str | None = None,
    ) -> dict[str, Any]:
        body = content.read() if hasattr(content, "read") else content
        headers: dict[str, str] = {}
        if content_type:
            headers["Content-Type"] = content_type
        if if_match:
            headers["If-Match"] = if_match
        if if_none_match:
            headers["If-None-Match"] = if_none_match
        for metadata_key, metadata_value in (metadata or {}).items():
            headers[f"X-Vyral-Meta-{metadata_key}"] = metadata_value

        return self._json("PUT", f"/objects/{quote(container)}/{quote(key, safe='/')}", body=body, headers=headers)

    def get_object(self, container: str, key: str) -> bytes | None:
        try:
            return self._request("GET", f"/objects/{quote(container)}/{quote(key, safe='/')}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def delete_object(self, container: str, key: str, if_match: str | None = None) -> None:
        headers = {"If-Match": if_match} if if_match else None
        self._request("DELETE", f"/objects/{quote(container)}/{quote(key, safe='/')}", headers=headers)

    def list_traces(self, operation: str | None = None, limit: int | None = None) -> list[dict[str, Any]]:
        query = {
            key: value
            for key, value in {"operation": operation, "limit": limit}.items()
            if value is not None
        }
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/traces{suffix}")

    def prune_traces(self, request: dict[str, Any]) -> dict[str, Any]:
        return self._json("POST", "/traces/prune", request)

    def summarize_traces(self, operation: str | None = None) -> dict[str, Any]:
        query = {"operation": operation} if operation is not None else {}
        suffix = f"?{urlencode(query)}" if query else ""
        return self._json("GET", f"/traces/summary{suffix}")

    def export_traces(self, request: dict[str, Any] | None = None) -> dict[str, Any]:
        return self._json("POST", "/traces/export", request or {})

    def get_trace(self, trace_id: str) -> dict[str, Any] | None:
        try:
            return self._json("GET", f"/traces/{quote(trace_id)}")
        except VyralClientError as exc:
            if exc.status == 404:
                return None
            raise

    def _json(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        body: bytes | Iterable[bytes] | None = None,
        headers: dict[str, str] | None = None,
    ) -> Any:
        response = self._request(method, path, payload, body, headers)
        return json.loads(response.decode("utf-8")) if response else None

    def _request(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        body: bytes | Iterable[bytes] | None = None,
        headers: dict[str, str] | None = None,
    ) -> bytes:
        request_headers = dict(self.default_headers or {})
        request_headers.update(headers or {})
        if self.bearer_token and not _has_header(request_headers, "Authorization"):
            request_headers["Authorization"] = f"Bearer {self.bearer_token}"
        if self.api_key and not _has_header(request_headers, "Authorization", "X-Vyral-Api-Key"):
            request_headers["X-Vyral-Api-Key"] = self.api_key
        if self.correlation_id and "X-Correlation-ID" not in request_headers:
            request_headers["X-Correlation-ID"] = self.correlation_id
        _require_secure_credential_transport(self.base_url, request_headers)

        data = body
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            request_headers.setdefault("Content-Type", "application/json")

        method_upper = method.upper()
        can_retry = method_upper in {"GET", "HEAD", "OPTIONS"} or any(
            key.lower() in {"idempotency-key", "x-idempotency-key"}
            for key in request_headers
        )
        if not isinstance(data, (bytes, type(None))):
            can_retry = False

        for attempt in range(self.max_retries + 1):
            if self.cancellation_check is not None and self.cancellation_check():
                raise VyralClientError.cancelled()
            credential_headers = {
                name: value
                for name, value in request_headers.items()
                if name.lower() in _CREDENTIAL_HEADERS
            }
            redirectable_headers = {
                name: value
                for name, value in request_headers.items()
                if name.lower() not in _CREDENTIAL_HEADERS
            }
            request = Request(
                f"{self.base_url}{path}",
                data=data,
                headers=redirectable_headers,
                method=method_upper,
            )
            for name, value in credential_headers.items():
                request.add_unredirected_header(name, value)
            try:
                with urlopen(request, timeout=self.timeout) as response:
                    return response.read()
            except HTTPError as exc:
                body_text = exc.read().decode("utf-8", errors="replace")
                response_headers = getattr(exc, "headers", {})
                retry_after = _header_value(response_headers, "Retry-After")
                correlation_id = _header_value(response_headers, "X-Correlation-ID", "X-Request-ID")
                if can_retry and attempt < self.max_retries and exc.code in (408, 429, 502, 503, 504):
                    time.sleep(_retry_delay_seconds(retry_after, self.retry_backoff_seconds * (2 ** attempt)))
                    continue
                raise VyralClientError(
                    exc.code,
                    body_text,
                    problem=_parse_problem_body(body_text),
                    retry_after=retry_after,
                    correlation_id=correlation_id,
                ) from exc
            except (TimeoutError, socket.timeout) as exc:
                if can_retry and attempt < self.max_retries:
                    time.sleep(self.retry_backoff_seconds * (2 ** attempt))
                    continue
                raise VyralClientError.timeout(str(exc) or f"Request timed out after {self.timeout} seconds") from exc
            except URLError as exc:
                reason = getattr(exc, "reason", exc)
                if can_retry and attempt < self.max_retries:
                    time.sleep(self.retry_backoff_seconds * (2 ** attempt))
                    continue
                if _is_timeout_exception(reason):
                    raise VyralClientError.timeout(str(reason) or f"Request timed out after {self.timeout} seconds") from exc
                raise VyralClientError(
                    0,
                    str(reason),
                    problem={"title": "Transport failure", "detail": str(reason), "status": 0},
                    failure_class="transport",
                ) from exc

        raise AssertionError("request retry loop exhausted")
