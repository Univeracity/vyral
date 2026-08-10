from __future__ import annotations

from dataclasses import dataclass, replace
from collections import Counter
from hashlib import sha256
import json
from time import perf_counter
from typing import Any, Iterable, Mapping, Sequence

from ..async_runtime import RuntimeExecutor
from ..graph import (
    GraphService,
    GraphTraversalRequest,
    GraphTraversalTruncatedError,
    VyralGraphProjection,
)
from ..local.models import JSONObject, VyralRecord
from ..retrieval import (
    RerankOptions,
    RetrievalMatch,
    RetrievalRequest,
    RetrievalService,
)
from .context_models import (
    RagContextAssemblyOptions,
    RagContextChunk,
    RagContextCitation,
    RagContextEnvelope,
    RagContextGroupBudget,
    RagContextGraphExpansionOptions,
    RagContextRequest,
    RagContextRetrievalMatch,
    RagPromptEnvelope,
    RagPromptMessage,
    RagPromptRequest,
)
from .evaluation_models import (
    MAX_EXPECTED_GRAPH_ITEMS,
    MAX_RAG_EVALUATION_CASES,
    RagContextEvaluationCase,
    RagContextEvaluationCaseResult,
    RagContextEvaluationRequest,
    RagContextEvaluationResult,
    RagContextExpectedGraph,
    RagContextGraphEvaluationFailureModes,
    RagContextGraphEvaluationResult,
)


CONTEXT_TEXT_FORMAT = "citation-markdown"
DEFAULT_SYSTEM_INSTRUCTION = (
    "Answer the user's question using only the provided context. "
    "If the context is insufficient, say that the context does not contain "
    "enough information."
)
DEFAULT_CITATION_INSTRUCTION = (
    "Cite supporting claims with the provided citation ids, such as [c1]."
)


@dataclass(frozen=True)
class _Rule:
    key: str
    priority: int
    required: bool
    min_chunks: int | None
    max_chunks: int | None
    min_chars: int | None
    max_chars: int | None


@dataclass(frozen=True)
class _Plan:
    enabled: bool
    authority_ordering: bool
    group_by: str | None
    group_by_path: str | None
    default_max_chunks: int | None
    default_max_chars: int | None
    fail_required: bool
    rules: Mapping[str, _Rule]

    def rule(self, key: str) -> _Rule | None:
        return self.rules.get(key)

    def priority(self, key: str | None) -> int:
        rule = self.rules.get(key or "")
        return rule.priority if rule is not None else 2_147_483_647


@dataclass
class _Stats:
    candidate_count: int = 0
    chunk_count: int = 0
    char_count: int = 0


@dataclass(frozen=True)
class _Candidate:
    match: RetrievalMatch
    text: str
    group_key: str | None


@dataclass(frozen=True)
class _Excerpt:
    text: str
    char_start: int
    char_end: int
    truncated: bool


class RagContextService:
    def __init__(
        self,
        retrieval_service: RetrievalService,
        *,
        graph_service: GraphService | None = None,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.retrieval_service = retrieval_service
        self.graph_service = graph_service
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def build_context(
        self,
        request: RagContextRequest | Mapping[str, Any],
    ) -> RagContextEnvelope:
        normalized = RagContextRequest.from_value(request)
        _validate_context_request(normalized)
        plan = _build_plan(normalized.context_assembly)
        retrieval_request = _context_retrieval_request(normalized)
        retrieval = self.retrieval_service.search(retrieval_request)
        graph_context, graph_summary = _build_graph_context(
            self.graph_service,
            normalized.graph_expansion,
            retrieval.results,
        )
        if graph_summary is not None:
            graph_summary["graphContextInfluencedContextText"] = bool(
                normalized.include_context_text
                and graph_context is not None
                and graph_context.get("contextText")
            )
        effective_mode = _effective_mode(retrieval.trace, retrieval_request)
        candidates: list[_Candidate] = []
        stats: dict[str, _Stats] = {}
        skipped_empty = 0
        for match in retrieval.results:
            text = _extract_text(match.record, normalized.content_field)
            if not text.strip():
                skipped_empty += 1
                continue
            group_key = _resolve_group_key(match, plan)
            if group_key is not None:
                stats.setdefault(group_key, _Stats()).candidate_count += 1
            candidates.append(_Candidate(match, text, group_key))

        chunks: list[RagContextChunk] = []
        citations: list[RagContextCitation] = []
        total_chars = 0
        remaining = normalized.max_chars
        omitted_citations = 0
        skipped_group = 0
        dropped_budget = 0
        assemble_citations = (
            normalized.include_citations or normalized.include_context_text
        )
        for candidate in _order_candidates(
            candidates,
            plan,
            normalized.max_chars_per_chunk,
        ):
            if remaining <= 0:
                dropped_budget += 1
                break
            group_key = candidate.group_key
            rule = plan.rule(group_key) if group_key is not None else None
            if group_key is not None:
                group_stats = stats.setdefault(group_key, _Stats())
                max_chunks = (
                    rule.max_chunks
                    if rule is not None and rule.max_chunks is not None
                    else plan.default_max_chunks
                )
                if max_chunks is not None and group_stats.chunk_count >= max_chunks:
                    skipped_group += 1
                    continue
                max_chars = (
                    rule.max_chars
                    if rule is not None and rule.max_chars is not None
                    else plan.default_max_chars
                )
                if max_chars is not None and group_stats.char_count >= max_chars:
                    skipped_group += 1
                    continue
            budget = min(normalized.max_chars_per_chunk, remaining)
            if group_key is not None:
                max_chars = (
                    rule.max_chars
                    if rule is not None and rule.max_chars is not None
                    else plan.default_max_chars
                )
                if max_chars is not None:
                    budget = min(budget, max_chars - stats[group_key].char_count)
            if budget <= 0:
                skipped_group += 1
                continue
            excerpt = _trim(candidate.text, budget)
            if not excerpt.text:
                dropped_budget += 1
                break
            match = candidate.match
            chunk = RagContextChunk(
                rank=len(chunks) + 1,
                score=match.score,
                collection=match.collection,
                partition_key=match.record.partition_key,
                id=match.record.id,
                text=excerpt.text,
                content_field=normalized.content_field,
                group_key=group_key,
                char_start=excerpt.char_start,
                char_end=excerpt.char_end,
                original_text_length=len(candidate.text),
                truncated=excerpt.truncated,
                context_excerpt_hash=_hash(excerpt.text),
                retrieval_diagnostics=(
                    match.diagnostics if normalized.include_trace else None
                ),
                retrieval_match=(
                    RagContextRetrievalMatch(
                        rank=match.rank,
                        score=match.score,
                        collection=match.collection,
                        search_mode=effective_mode,
                        snippet=match.snippet,
                    )
                    if normalized.include_trace
                    else None
                ),
                metadata=match.record.metadata,
                sources=match.record.sources,
                record=match.record if normalized.include_records else None,
            )
            if assemble_citations:
                citation_ids, omitted = _add_citations(
                    citations,
                    chunk,
                    match.record,
                    normalized.max_citations_per_chunk,
                )
                omitted_citations += omitted
                chunk = replace(chunk, citation_ids=citation_ids)
            chunks.append(chunk)
            total_chars += len(excerpt.text)
            remaining -= len(excerpt.text)
            if group_key is not None:
                group_stats = stats[group_key]
                group_stats.chunk_count += 1
                group_stats.char_count += len(excerpt.text)

        evaluations = _evaluate_groups(plan, stats)
        unsatisfied = sorted(
            key
            for key, value in evaluations.items()
            if bool(value["required"]) and not bool(value["satisfied"])
        )
        if plan.fail_required and unsatisfied:
            raise ValueError(
                "RAG context required groups were not satisfied: "
                + ", ".join(unsatisfied)
                + "."
            )

        context_text = (
            _render_context_text(chunks, citations, graph_context)
            if normalized.include_context_text
            else None
        )
        context_hash = _hash(context_text) if context_text is not None else None
        trace: JSONObject | None = None
        if normalized.include_trace:
            trace = {
                "retrieval": retrieval.trace or {},
                "chunkCount": len(chunks),
                "citationCount": len(citations) if assemble_citations else 0,
                "omittedCitationCount": omitted_citations,
                "effectiveSearchMode": effective_mode,
                "includeContextText": normalized.include_context_text,
                "contextTextFormat": (
                    CONTEXT_TEXT_FORMAT if context_text is not None else ""
                ),
                "contextTextHash": context_hash or "",
                "totalChars": total_chars,
                "maxChars": normalized.max_chars,
                "maxCharsPerChunk": normalized.max_chars_per_chunk,
                "skippedEmptyText": skipped_empty,
                "skippedForGroupBudget": skipped_group,
                "droppedForBudget": dropped_budget,
                "budgetExhausted": remaining <= 0 and len(candidates) > len(chunks),
                "groupBy": plan.group_by or "",
                "groupByPath": plan.group_by_path or "",
                "maxChunksPerGroup": plan.default_max_chunks or 0,
                "maxCharsPerGroup": plan.default_max_chars or 0,
                "groupCount": len(evaluations),
                "unsatisfiedRequiredGroups": unsatisfied,
                "groupStats": evaluations,
                "graphExpansion": graph_summary
                or {
                    "status": "not_requested",
                    "expansionAttempted": False,
                },
                "contextAssembly": {
                    "enabled": plan.enabled,
                    "authorityOrdering": plan.authority_ordering,
                    "failOnUnsatisfiedRequiredGroups": plan.fail_required,
                    "configuredGroupCount": len(plan.rules),
                },
            }
        return RagContextEnvelope(
            query=normalized.retrieval.query,
            chunks=tuple(chunks),
            citations=(
                tuple(citations) if normalized.include_citations else ()
            ),
            total_chars=total_chars,
            omitted_citation_count=omitted_citations,
            context_text=context_text,
            context_text_format=(
                CONTEXT_TEXT_FORMAT if context_text is not None else None
            ),
            context_text_hash=context_hash,
            graph_context=graph_context,
            graph_expansion=graph_summary,
            trace=trace,
        )

    async def abuild_context(
        self,
        request: RagContextRequest | Mapping[str, Any],
    ) -> RagContextEnvelope:
        return await self.executor.run(lambda: self.build_context(request))

    def evaluate_context(
        self,
        request: RagContextEvaluationRequest | Mapping[str, Any],
    ) -> RagContextEvaluationResult:
        normalized = RagContextEvaluationRequest.from_value(request)
        _validate_evaluation_request(normalized)
        results: list[RagContextEvaluationCaseResult] = []
        stopped = False
        for index, test_case in enumerate(normalized.cases):
            started = perf_counter()
            try:
                context = self.build_context(test_case.request)
                graph = _evaluate_graph_context(
                    context,
                    test_case.expected_graph,
                )
                graph_context = context.graph_context or {}
                projection = graph_context.get("projection")
                nodes = (
                    projection.get("nodes", [])
                    if isinstance(projection, Mapping)
                    else []
                )
                edges = (
                    projection.get("edges", [])
                    if isinstance(projection, Mapping)
                    else []
                )
                mode = (
                    test_case.request.retrieval.search_mode or ""
                ).lower()
                results.append(
                    RagContextEvaluationCaseResult(
                        index=index,
                        name=test_case.name,
                        status="succeeded",
                        passed=graph.passed,
                        duration_ms=(perf_counter() - started) * 1000.0,
                        query_id=_evaluation_query_id(test_case, index),
                        profile_name=_evaluation_profile_name(
                            test_case.request
                        ),
                        expected_anchor_ids=tuple(
                            sorted(
                                set(test_case.expected_graph.node_ids)
                                | set(test_case.expected_graph.edge_ids)
                                | set(
                                    test_case.expected_graph.provenance_entity_ids
                                )
                            )
                        ),
                        retrieved_record_ids=tuple(
                            dict.fromkeys(chunk.id for chunk in context.chunks)
                        ),
                        graph_expanded_node_ids=_entity_ids(nodes),
                        graph_expanded_edge_ids=_entity_ids(edges),
                        lexical_contribution_count=(
                            0 if mode == "vector" else len(context.chunks)
                        ),
                        vector_contribution_count=(
                            len(context.chunks)
                            if mode in {"vector", "hybrid"}
                            else 0
                        ),
                        graph_contribution_count=int(
                            graph_context.get("nodeCount", 0)
                        )
                        + int(graph_context.get("edgeCount", 0)),
                        failure_categories=graph.failure_categories,
                        limit_reasons=_graph_limit_reasons(context),
                        graph_contribution=context.graph_expansion,
                        graph=graph,
                        context=(
                            context if normalized.include_context else None
                        ),
                    )
                )
            except (TypeError, ValueError, LookupError) as exc:
                results.append(
                    RagContextEvaluationCaseResult(
                        index=index,
                        name=test_case.name,
                        status="failed",
                        passed=False,
                        duration_ms=(perf_counter() - started) * 1000.0,
                        query_id=_evaluation_query_id(test_case, index),
                        profile_name=_evaluation_profile_name(
                            test_case.request
                        ),
                        expected_anchor_ids=tuple(
                            sorted(
                                set(test_case.expected_graph.node_ids)
                                | set(test_case.expected_graph.edge_ids)
                                | set(
                                    test_case.expected_graph.provenance_entity_ids
                                )
                            )
                        ),
                        retrieved_record_ids=(),
                        graph_expanded_node_ids=(),
                        graph_expanded_edge_ids=(),
                        lexical_contribution_count=0,
                        vector_contribution_count=0,
                        graph_contribution_count=0,
                        failure_categories=("case_error",),
                        limit_reasons=(),
                        graph_contribution=None,
                        graph=_empty_graph_evaluation(("case_error",)),
                        context=None,
                        error=str(exc),
                    )
                )
                if not normalized.continue_on_error:
                    stopped = index + 1 < len(normalized.cases)
                    break
        return _aggregate_context_evaluation(
            len(normalized.cases),
            results,
            stopped,
        )

    async def aevaluate_context(
        self,
        request: RagContextEvaluationRequest | Mapping[str, Any],
    ) -> RagContextEvaluationResult:
        return await self.executor.run(lambda: self.evaluate_context(request))

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()


class RagPromptService:
    def __init__(
        self,
        context_service: RagContextService,
        *,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.context_service = context_service
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def build_prompt(
        self,
        request: RagPromptRequest | Mapping[str, Any],
    ) -> RagPromptEnvelope:
        normalized = RagPromptRequest.from_value(request)
        template = normalized.template
        format_name = template.format.strip().lower() if template.format.strip() else "chat-markdown"
        if format_name != "chat-markdown":
            raise ValueError(
                f"RAG prompt format {template.format!r} is not supported."
            )
        if template.max_prompt_chars is not None and template.max_prompt_chars <= 0:
            raise ValueError(
                "RAG prompt maxPromptChars must be greater than zero when provided."
            )
        context_request = replace(
            normalized.context,
            include_context_text=True,
            include_citations=True,
        )
        context = self.context_service.build_context(context_request)
        if template.fail_on_empty_context and not context.chunks:
            raise ValueError("RAG prompt assembly produced no context chunks.")
        system = (
            template.system_instruction.strip()
            if template.system_instruction and template.system_instruction.strip()
            else DEFAULT_SYSTEM_INSTRUCTION
        )
        user = (
            template.user_instruction.strip()
            if template.user_instruction and template.user_instruction.strip()
            else context.query
        )
        citation_instruction = (
            (
                template.citation_instruction.strip()
                if template.citation_instruction
                and template.citation_instruction.strip()
                else DEFAULT_CITATION_INSTRUCTION
            )
            if template.include_citation_instruction
            else None
        )
        user_content = _render_user_content(
            user,
            context.context_text or "",
            citation_instruction,
        )
        messages = (
            RagPromptMessage(role="system", content=system),
            RagPromptMessage(role="user", content=user_content),
        )
        prompt = _render_prompt(messages)
        if (
            template.max_prompt_chars is not None
            and len(prompt) > template.max_prompt_chars
        ):
            raise ValueError(
                f"RAG prompt length {len(prompt)} exceeds maxPromptChars "
                f"{template.max_prompt_chars}."
            )
        prompt_hash = _hash(prompt)
        trace = (
            {
                "promptHash": prompt_hash,
                "promptChars": len(prompt),
                "messageCount": len(messages),
                "format": format_name,
                "contextTextHash": context.context_text_hash or "",
                "contextChunkCount": len(context.chunks),
                "citationCount": len(context.citations),
                "failOnEmptyContext": template.fail_on_empty_context,
            }
            if normalized.context.include_trace
            else None
        )
        return RagPromptEnvelope(
            query=context.query,
            format=format_name,
            prompt=prompt,
            prompt_hash=prompt_hash,
            messages=messages,
            context=context,
            trace=trace,
        )

    async def abuild_prompt(
        self,
        request: RagPromptRequest | Mapping[str, Any],
    ) -> RagPromptEnvelope:
        return await self.executor.run(lambda: self.build_prompt(request))

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()


def _context_retrieval_request(request: RagContextRequest) -> RetrievalRequest:
    retrieval = request.retrieval
    rerank = retrieval.rerank
    if rerank is not None and (
        rerank.content_field is None or not rerank.content_field.strip()
    ):
        rerank = replace(rerank, content_field=request.content_field)
    return replace(
        retrieval,
        embedding=(
            replace(
                retrieval.embedding,
                purpose=(
                    retrieval.embedding.purpose
                    if retrieval.embedding.purpose
                    and retrieval.embedding.purpose.strip()
                    else "query"
                ),
            )
            if retrieval.embedding is not None
            else None
        ),
        rerank=rerank,
        include_trace=request.include_trace or retrieval.include_trace,
    )


def _validate_context_request(request: RagContextRequest) -> None:
    if not request.retrieval.collections:
        raise ValueError("RAG context retrieval.collections must not be empty.")
    if request.max_chars <= 0:
        raise ValueError("RAG context maxChars must be greater than zero.")
    if request.max_chars_per_chunk <= 0:
        raise ValueError(
            "RAG context maxCharsPerChunk must be greater than zero."
        )
    if (
        request.max_citations_per_chunk is not None
        and request.max_citations_per_chunk <= 0
    ):
        raise ValueError(
            "RAG context maxCitationsPerChunk must be greater than zero "
            "when provided."
        )
    if not request.content_field.strip():
        raise ValueError("RAG context contentField is required.")
    if any(character in request.content_field for character in "/\\."):
        raise ValueError(
            "RAG context contentField must be a simple content property name."
        )
    if request.context_assembly is not None:
        _validate_assembly(request.context_assembly)
    if request.graph_expansion is not None:
        _validate_graph_expansion(request.graph_expansion)


def _validate_graph_expansion(
    options: RagContextGraphExpansionOptions,
) -> None:
    if not options.enabled:
        return
    if not options.collection.strip():
        raise ValueError(
            "RAG graphExpansion collection is required when enabled."
        )
    if options.max_seed_nodes <= 0:
        raise ValueError("RAG graphExpansion maxSeedNodes must be positive.")
    if options.max_graph_context_chars <= 0:
        raise ValueError(
            "RAG graphExpansion maxGraphContextChars must be positive."
        )
    if options.max_graph_provenance_items < 0:
        raise ValueError(
            "RAG graphExpansion maxGraphProvenanceItems cannot be negative."
        )
    if options.max_records is not None and options.max_records <= 0:
        raise ValueError("RAG graphExpansion maxRecords must be positive.")
    for pointer in options.seed_json_pointers:
        if not pointer.startswith("/"):
            raise ValueError(
                "RAG graphExpansion seedJsonPointers must be JSON pointers."
            )


def _validate_assembly(options: RagContextAssemblyOptions) -> None:
    group_by = _normalize_group_by(options.group_by)
    effective = group_by
    has_budgets = bool(
        options.groups
        or options.default_max_chunks_per_group is not None
        or options.default_max_chars_per_group is not None
    )
    if effective is None and (options.group_by_path or has_budgets):
        effective = "jsonPointer"
    if effective in {"metadata", "jsonPointer"} and not (
        options.group_by_path and options.group_by_path.strip()
    ):
        raise ValueError(
            "RAG contextAssembly groupByPath is required when groupBy is "
            "metadata or jsonPointer."
        )
    if (
        effective == "jsonPointer"
        and options.group_by_path
        and not options.group_by_path.startswith("/")
    ):
        raise ValueError(
            "RAG contextAssembly groupByPath must be a JSON pointer when "
            "groupBy is jsonPointer."
        )
    for name, value in (
        ("defaultMaxChunksPerGroup", options.default_max_chunks_per_group),
        ("defaultMaxCharsPerGroup", options.default_max_chars_per_group),
    ):
        if value is not None and value <= 0:
            raise ValueError(
                f"RAG contextAssembly {name} must be greater than zero."
            )
    keys: set[str] = set()
    for group in options.groups:
        if not group.key.strip():
            raise ValueError("RAG contextAssembly group key is required.")
        if group.key in keys:
            raise ValueError(
                f"RAG contextAssembly contains duplicate group {group.key!r}."
            )
        keys.add(group.key)
        for name, value in (
            ("minChunks", group.min_chunks),
            ("maxChunks", group.max_chunks),
            ("minChars", group.min_chars),
            ("maxChars", group.max_chars),
        ):
            if value is not None and value < 0:
                raise ValueError(
                    f"RAG contextAssembly group {name} cannot be negative."
                )
        if (
            group.min_chunks is not None
            and group.max_chunks is not None
            and group.min_chunks > group.max_chunks
        ):
            raise ValueError(
                "RAG contextAssembly group minChunks cannot exceed maxChunks."
            )
        if (
            group.min_chars is not None
            and group.max_chars is not None
            and group.min_chars > group.max_chars
        ):
            raise ValueError(
                "RAG contextAssembly group minChars cannot exceed maxChars."
            )


def _normalize_group_by(value: str | None) -> str | None:
    if value is None or not value.strip():
        return None
    normalized = value.strip().lower()
    known = {
        "collection": "collection",
        "sourcekind": "sourceKind",
        "metadata": "metadata",
        "jsonpointer": "jsonPointer",
    }
    if normalized not in known:
        raise ValueError(
            f"RAG contextAssembly groupBy {value!r} is not supported."
        )
    return known[normalized]


def _normalize_group_path(group_by: str | None, path: str | None) -> str | None:
    if path is None or not path.strip():
        return None
    normalized = path.strip()
    if group_by == "metadata" and not normalized.startswith("/"):
        return "/metadata/" + normalized.replace("~", "~0").replace("/", "~1")
    return normalized


def _build_plan(options: RagContextAssemblyOptions | None) -> _Plan:
    if options is None:
        return _Plan(False, False, None, None, None, None, False, {})
    group_by = _normalize_group_by(options.group_by)
    if group_by is None and (
        options.group_by_path
        or options.groups
        or options.default_max_chunks_per_group is not None
        or options.default_max_chars_per_group is not None
    ):
        group_by = "jsonPointer"
    rules = {
        group.key: _Rule(
            key=group.key,
            priority=group.priority if group.priority is not None else index,
            required=group.required,
            min_chunks=group.min_chunks,
            max_chunks=group.max_chunks,
            min_chars=group.min_chars,
            max_chars=group.max_chars,
        )
        for index, group in enumerate(options.groups)
    }
    return _Plan(
        enabled=True,
        authority_ordering=bool(rules),
        group_by=group_by,
        group_by_path=_normalize_group_path(group_by, options.group_by_path),
        default_max_chunks=options.default_max_chunks_per_group,
        default_max_chars=options.default_max_chars_per_group,
        fail_required=options.fail_on_unsatisfied_required_groups,
        rules=rules,
    )


def _extract_text(record: VyralRecord, field: str) -> str:
    value = (record.content or {}).get(field)
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=False,
    )


def _resolve_group_key(match: RetrievalMatch, plan: _Plan) -> str | None:
    if plan.group_by is None:
        return None
    if plan.group_by == "collection":
        return match.collection if match.collection.strip() else "(missing)"
    if plan.group_by == "sourceKind":
        for source in match.record.sources or ():
            kind = source.get("kind")
            if isinstance(kind, str) and kind.strip():
                return kind
        return "(missing)"
    value, found = _pointer(match.record.to_dict(), plan.group_by_path or "")
    if not found:
        return "(missing)"
    if value is None:
        return "(null)"
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "True" if value else "False"
    if isinstance(value, (int, float)):
        return str(value)
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _pointer(value: object, path: str) -> tuple[object, bool]:
    if not path.startswith("/"):
        return None, False
    current = value
    for raw in path.split("/")[1:]:
        segment = raw.replace("~1", "/").replace("~0", "~")
        if isinstance(current, Mapping):
            if segment not in current:
                return None, False
            current = current[segment]
        elif isinstance(current, list) and segment.isdigit():
            index = int(segment)
            if index >= len(current):
                return None, False
            current = current[index]
        else:
            return None, False
    return current, True


def _graph_seed_values(value: object) -> Iterable[object]:
    if isinstance(value, list):
        for item in value:
            yield from _graph_seed_values(item)
        return
    # A present object/null value produces a blank diagnostic. This keeps a
    # configured-but-invalid seed distinct from a missing JSON pointer.
    yield value


def _order_candidates(
    candidates: Sequence[_Candidate],
    plan: _Plan,
    max_chars: int,
) -> tuple[_Candidate, ...]:
    if not plan.authority_ordering:
        return tuple(candidates)
    selected: set[tuple[str, str, str]] = set()
    ordered: list[_Candidate] = []
    for rule in sorted(plan.rules.values(), key=lambda item: (item.priority, item.key)):
        if not rule.required and rule.min_chunks is None and rule.min_chars is None:
            continue
        minimum_chunks = (
            rule.min_chunks
            if rule.min_chunks is not None
            else 1
            if rule.required
            else 0
        )
        minimum_chars = rule.min_chars or 0
        count = 0
        chars = 0
        for candidate in candidates:
            if candidate.group_key != rule.key:
                continue
            if count >= minimum_chunks and chars >= minimum_chars:
                break
            key = _match_key(candidate.match)
            if key not in selected:
                selected.add(key)
                ordered.append(candidate)
                count += 1
                chars += min(len(candidate.text), max_chars)
    remaining = sorted(
        (candidate for candidate in candidates if _match_key(candidate.match) not in selected),
        key=lambda item: (
            plan.priority(item.group_key),
            item.match.rank,
            item.match.collection,
            item.match.record.partition_key,
            item.match.record.id,
        ),
    )
    return tuple((*ordered, *remaining))


def _match_key(match: RetrievalMatch) -> tuple[str, str, str]:
    return (match.collection, match.record.partition_key, match.record.id)


def _evaluate_groups(
    plan: _Plan,
    stats: Mapping[str, _Stats],
) -> JSONObject:
    output: JSONObject = {}
    for key in sorted(set(stats) | set(plan.rules)):
        current = stats.get(key, _Stats())
        rule = plan.rule(key)
        required = rule.required if rule is not None else False
        min_chunks = (
            rule.min_chunks
            if rule is not None and rule.min_chunks is not None
            else 1
            if required
            else 0
        )
        min_chars = rule.min_chars if rule is not None and rule.min_chars is not None else 0
        output[key] = {
            "candidateCount": current.candidate_count,
            "chunkCount": current.chunk_count,
            "charCount": current.char_count,
            "priority": rule.priority if rule is not None else plan.priority(key),
            "required": required,
            "satisfied": (
                current.chunk_count >= min_chunks
                and current.char_count >= min_chars
            ),
            "minChunks": min_chunks,
            "maxChunks": (
                rule.max_chunks
                if rule is not None and rule.max_chunks is not None
                else plan.default_max_chunks or 0
            ),
            "minChars": min_chars,
            "maxChars": (
                rule.max_chars
                if rule is not None and rule.max_chars is not None
                else plan.default_max_chars or 0
            ),
        }
    return output


def _trim(text: str, budget: int) -> _Excerpt:
    if len(text) <= budget:
        return _Excerpt(text, 0, len(text), False)
    if budget <= 3:
        return _Excerpt(text[:budget], 0, budget, True)
    end = budget - 3
    return _Excerpt(text[:end] + "...", 0, end, True)


def _add_citations(
    citations: list[RagContextCitation],
    chunk: RagContextChunk,
    record: VyralRecord,
    maximum: int | None,
) -> tuple[tuple[str, ...], int]:
    sources = record.sources or ()
    if not sources:
        identifier = f"c{chunk.rank}"
        citations.append(_citation(identifier, chunk, None))
        return (identifier,), 0
    count = min(len(sources), maximum) if maximum is not None else len(sources)
    identifiers: list[str] = []
    for index, source in enumerate(sources[:count]):
        identifier = (
            f"c{chunk.rank}"
            if len(sources) == 1
            else f"c{chunk.rank}.{index + 1}"
        )
        citations.append(_citation(identifier, chunk, source))
        identifiers.append(identifier)
    return tuple(identifiers), len(sources) - count


def _citation(
    identifier: str,
    chunk: RagContextChunk,
    source: JSONObject | None,
) -> RagContextCitation:
    span_value = source.get("span") if source is not None else None
    span = dict(span_value) if isinstance(span_value, Mapping) else None
    return RagContextCitation(
        id=identifier,
        chunk_rank=chunk.rank,
        collection=chunk.collection,
        partition_key=chunk.partition_key,
        record_id=chunk.id,
        source_id=_source_text(source, "id"),
        source_kind=_source_text(source, "kind"),
        source_uri=_source_text(source, "uri"),
        source_label=_source_text(source, "label"),
        source_span=span,
        included_source_span=_included_span(
            span,
            chunk.char_start,
            chunk.char_end,
        ),
        context_char_start=chunk.char_start,
        context_char_end=chunk.char_end,
        context_excerpt_hash=chunk.context_excerpt_hash,
    )


def _source_text(source: JSONObject | None, key: str) -> str | None:
    value = source.get(key) if source is not None else None
    return value if isinstance(value, str) else None


def _included_span(
    source_span: JSONObject | None,
    start: int,
    end: int,
) -> JSONObject | None:
    if source_span is None:
        return None
    output = dict(source_span)
    source_start = source_span.get("charStart")
    source_end = source_span.get("charEnd")
    if isinstance(source_start, int) and not isinstance(source_start, bool):
        output["charStart"] = source_start + start
        included_end = source_start + end
        if isinstance(source_end, int) and not isinstance(source_end, bool):
            included_end = min(included_end, source_end)
        output["charEnd"] = included_end
    else:
        output["charStart"] = None
        output["charEnd"] = None
    return output


def _build_graph_context(
    graph_service: GraphService | None,
    options: RagContextGraphExpansionOptions | None,
    matches: Sequence[RetrievalMatch],
) -> tuple[JSONObject | None, JSONObject | None]:
    if options is None or not options.enabled:
        return None, None
    seed_ids: list[str] = []
    seen: set[str] = set()
    diagnostics: list[JSONObject] = []
    dropped = 0
    omitted_diagnostics = 0

    def add_diagnostic(diagnostic: JSONObject) -> None:
        nonlocal omitted_diagnostics
        if len(diagnostics) < 200:
            diagnostics.append(diagnostic)
        else:
            omitted_diagnostics += 1

    def add_seed(
        value: object,
        *,
        pointer: str,
        match: RetrievalMatch | None = None,
        found: bool = True,
    ) -> None:
        nonlocal dropped
        normalized = str(value).strip() if isinstance(
            value, (str, int, float, bool)
        ) else ""
        accepted = False
        reason: str | None = None
        if not normalized:
            reason = "blank"
        elif normalized in seen:
            reason = "duplicate"
        elif len(seed_ids) >= options.max_seed_nodes:
            reason = "maxSeedNodes"
            dropped += 1
        else:
            seen.add(normalized)
            seed_ids.append(normalized)
            accepted = True
        add_diagnostic(
            {
                "recordId": match.record.id if match is not None else None,
                "partitionKey": (
                    match.record.partition_key if match is not None else None
                ),
                "pointer": pointer,
                "found": found,
                "rawValue": str(value) if value is not None else None,
                "normalizedValue": normalized or None,
                "skippedReason": reason,
                "accepted": accepted,
            }
        )

    for seed in options.seed_node_ids:
        add_seed(seed, pointer="$.seedNodeIds", found=bool(seed.strip()))
    stop = False
    for match in matches:
        if stop:
            break
        material = match.record.to_dict()
        for pointer in options.seed_json_pointers:
            value, found = _pointer(material, pointer)
            if not found:
                add_diagnostic(
                    {
                        "recordId": match.record.id,
                        "partitionKey": match.record.partition_key,
                        "pointer": pointer,
                        "found": False,
                        "rawValue": None,
                        "normalizedValue": None,
                        "skippedReason": "missing",
                        "accepted": False,
                    }
                )
                continue
            values = tuple(_graph_seed_values(value))
            if not values:
                add_diagnostic(
                    {
                        "recordId": match.record.id,
                        "partitionKey": match.record.partition_key,
                        "pointer": pointer,
                        "found": False,
                        "rawValue": None,
                        "normalizedValue": None,
                        "skippedReason": "missing",
                        "accepted": False,
                    }
                )
                continue
            for selected in values:
                add_seed(
                    selected,
                    pointer=pointer,
                    match=match,
                    found=True,
                )
                if len(seed_ids) >= options.max_seed_nodes:
                    stop = True
                    break
            if stop:
                break

    base: JSONObject = {
        "collection": options.collection,
        "graphId": options.graph_id,
        "seedNodeIds": list(seed_ids),
        "seedCandidateCount": len(matches),
        "seedJsonPointers": list(options.seed_json_pointers),
        "seedDiagnostics": diagnostics,
        "omittedSeedDiagnosticCount": omitted_diagnostics,
        "droppedSeedCount": dropped,
        "maxSeedNodes": options.max_seed_nodes,
        "requestedMaxRecords": options.max_records,
        "exportedRecordCount": 0,
        "estimatedRequiredRecordCount": None,
        "sourceContinuationToken": None,
        "limitsHit": [],
        "projection": None,
        "nodeCount": 0,
        "edgeCount": 0,
        "sourceRecordCount": 0,
        "sourceTruncated": False,
        "contextText": None,
        "contextTextHash": None,
        "contextTextChars": 0,
        "contextTextTruncated": False,
        "provenance": [],
        "omittedProvenanceCount": 0,
        "failureReason": None,
    }
    if not seed_ids:
        base["status"] = "no_seeds"
        return base, _graph_summary(options, matches, base)
    if graph_service is None:
        return _graph_failure(
            options,
            matches,
            base,
            "failed",
            "Graph expansion requires a configured graph service.",
        )
    try:
        traversal = graph_service.traverse(
            options.collection,
            GraphTraversalRequest(
                graph_id=options.graph_id,
                namespace=options.namespace,
                tenant_id=options.tenant_id,
                partition_key=options.partition_key,
                start_node_ids=tuple(seed_ids),
                profile=options.profile,
                max_records=options.max_records,
                allow_partial_graph=options.allow_partial_graph,
            ),
        )
        if traversal is None:
            return _graph_failure(
                options,
                matches,
                base,
                "graph_not_found",
                f"Graph collection {options.collection!r} was not found.",
            )
    except GraphTraversalTruncatedError as exc:
        base.update(
            {
                "status": "budget_truncated",
                "sourceRecordCount": exc.result.record_count,
                "sourceTruncated": True,
                "exportedRecordCount": exc.result.record_count,
                "estimatedRequiredRecordCount": (
                    exc.estimated_required_record_count
                ),
                "sourceContinuationToken": exc.result.continuation_token,
                "limitsHit": ["maxRecords"],
                "failureReason": str(exc),
            }
        )
        if not options.fallback_on_failure:
            raise ValueError(f"RAG graph expansion failed: {exc}") from exc
        return base, _graph_summary(options, matches, base)
    except (TypeError, ValueError, LookupError) as exc:
        return _graph_failure(
            options,
            matches,
            base,
            "failed",
            str(exc),
        )

    projection = traversal.projection
    graph_text: str | None = None
    text_truncated = False
    if options.include_graph_context_text:
        rendered = _render_graph_text(projection)
        if rendered:
            excerpt = _trim(rendered, options.max_graph_context_chars)
            graph_text = excerpt.text
            text_truncated = excerpt.truncated
    all_provenance: list[JSONObject] = []
    for node in projection.nodes:
        all_provenance.append(
            {
                "entityKind": "node",
                "entityId": node.id,
                "label": node.label,
                "nodeType": node.type,
                "predicate": None,
                "sourceId": None,
                "targetId": None,
                "sourceSpans": [
                    span.to_dict() for span in node.source_spans
                ],
                "assertionIds": list(node.assertion_ids),
            }
        )
    for edge in projection.edges:
        all_provenance.append(
            {
                "entityKind": "edge",
                "entityId": edge.id,
                "label": edge.label,
                "nodeType": None,
                "predicate": edge.predicate,
                "sourceId": edge.source_id,
                "targetId": edge.target_id,
                "sourceSpans": [
                    span.to_dict() for span in edge.source_spans
                ],
                "assertionIds": list(edge.assertion_ids),
            }
        )
    provenance = (
        all_provenance[: options.max_graph_provenance_items]
        if options.include_graph_provenance
        else []
    )
    omitted = (
        max(0, len(all_provenance) - len(provenance))
        if options.include_graph_provenance
        else 0
    )
    projection_diagnostics = projection.diagnostics or {}
    limits: list[str] = []
    if traversal.source_truncated:
        limits.append("maxRecords")
    if projection_diagnostics.get("edgeTruncated") is True:
        limits.append("edgeLimit")
    if projection_diagnostics.get("nodeLimitReached") is True:
        limits.append("nodeLimit")
    if text_truncated:
        limits.append("maxGraphContextChars")
    if omitted:
        limits.append("maxGraphProvenanceItems")
    if dropped:
        limits.append("maxSeedNodes")
    base.update(
        {
            "status": "succeeded",
            "collection": traversal.collection,
            "graphId": traversal.graph_id,
            "projection": projection.to_dict(),
            "nodeCount": traversal.node_count,
            "edgeCount": traversal.edge_count,
            "sourceRecordCount": traversal.source_record_count,
            "sourceTruncated": traversal.source_truncated,
            "requestedMaxRecords": traversal.requested_max_records,
            "exportedRecordCount": traversal.exported_record_count,
            "estimatedRequiredRecordCount": (
                traversal.estimated_required_record_count
            ),
            "sourceContinuationToken": traversal.source_continuation_token,
            "limitsHit": limits,
            "contextText": graph_text,
            "contextTextHash": _hash(graph_text) if graph_text else None,
            "contextTextChars": len(graph_text) if graph_text else 0,
            "contextTextTruncated": text_truncated,
            "provenance": provenance,
            "omittedProvenanceCount": omitted,
        }
    )
    return base, _graph_summary(options, matches, base)


def _graph_failure(
    options: RagContextGraphExpansionOptions,
    matches: Sequence[RetrievalMatch],
    base: JSONObject,
    status: str,
    reason: str,
) -> tuple[JSONObject, JSONObject]:
    if not options.fallback_on_failure:
        raise ValueError(f"RAG graph expansion failed: {reason}")
    base.update({"status": status, "failureReason": reason})
    return base, _graph_summary(options, matches, base)


def _graph_summary(
    options: RagContextGraphExpansionOptions,
    matches: Sequence[RetrievalMatch],
    context: JSONObject,
) -> JSONObject:
    provenance = context.get("provenance")
    source_refs: set[str] = set()
    grounded = 0
    if isinstance(provenance, list):
        for item in provenance:
            if not isinstance(item, Mapping):
                continue
            spans = item.get("sourceSpans")
            if isinstance(spans, list):
                grounded += len(spans)
                for span in spans:
                    if isinstance(span, Mapping):
                        source = span.get("sourceRef")
                        if isinstance(source, str) and source.strip():
                            source_refs.add(source)
    return {
        "expansionAttempted": options.enabled,
        "expansionEnabled": options.enabled,
        "collection": options.collection,
        "graphId": options.graph_id,
        "namespace": options.namespace,
        "tenantId": options.tenant_id,
        "partitionKey": options.partition_key,
        "status": context.get("status", "not_requested"),
        "skippedReason": context.get("failureReason"),
        "profileId": options.profile.id,
        "maxDepth": options.profile.max_depth,
        "nodeLimit": options.profile.limit,
        "edgeLimit": options.profile.edge_limit,
        "maxRecords": options.max_records,
        "maxGraphContextChars": options.max_graph_context_chars,
        "maxGraphProvenanceItems": options.max_graph_provenance_items,
        "retrievedRecordIds": [match.record.id for match in matches],
        "sourceRecordIdsTouched": sorted(source_refs),
        "seedJsonPointers": list(options.seed_json_pointers),
        "seedCandidateCount": context.get("seedCandidateCount", 0),
        "seedCount": len(context.get("seedNodeIds", [])),
        "seedNodeIds": context.get("seedNodeIds", []),
        "droppedSeedCount": context.get("droppedSeedCount", 0),
        "nodesAdded": context.get("nodeCount", 0),
        "edgesAdded": context.get("edgeCount", 0),
        "relationshipsAdded": context.get("edgeCount", 0),
        "sourceRecordCount": context.get("sourceRecordCount", 0),
        "sourceTruncated": context.get("sourceTruncated", False),
        "exportedRecordCount": context.get("exportedRecordCount", 0),
        "estimatedRequiredRecordCount": context.get(
            "estimatedRequiredRecordCount"
        ),
        "limitsHit": context.get("limitsHit", []),
        "groundingStatus": (
            "source_grounded"
            if grounded
            else "ungrounded"
            if provenance
            else "none"
        ),
        "graphContextInfluencedContextText": bool(context.get("contextText")),
        "contextTextTruncated": context.get("contextTextTruncated", False),
        "omittedProvenanceCount": context.get(
            "omittedProvenanceCount", 0
        ),
    }


def _validate_evaluation_request(
    request: RagContextEvaluationRequest,
) -> None:
    if not request.cases:
        raise ValueError("RAG context evaluation requires at least one case.")
    if len(request.cases) > MAX_RAG_EVALUATION_CASES:
        raise ValueError(
            "RAG context evaluation supports at most "
            f"{MAX_RAG_EVALUATION_CASES} cases."
        )
    for test_case in request.cases:
        for name, values in (
            ("nodeIds", test_case.expected_graph.node_ids),
            ("edgeIds", test_case.expected_graph.edge_ids),
            (
                "provenanceEntityIds",
                test_case.expected_graph.provenance_entity_ids,
            ),
        ):
            if len(values) > MAX_EXPECTED_GRAPH_ITEMS:
                raise ValueError(
                    f"RAG expectedGraph.{name} supports at most "
                    f"{MAX_EXPECTED_GRAPH_ITEMS} values."
                )


def _evaluate_graph_context(
    context: RagContextEnvelope,
    expected: RagContextExpectedGraph,
) -> RagContextGraphEvaluationResult:
    graph_context = context.graph_context or {}
    projection = graph_context.get("projection")
    nodes = (
        projection.get("nodes", [])
        if isinstance(projection, Mapping)
        else []
    )
    edges = (
        projection.get("edges", [])
        if isinstance(projection, Mapping)
        else []
    )
    node_ids = set(_entity_ids(nodes))
    edge_ids = set(_entity_ids(edges))
    raw_provenance = graph_context.get("provenance")
    provenance = (
        tuple(item for item in raw_provenance if isinstance(item, Mapping))
        if isinstance(raw_provenance, list)
        else ()
    )
    provenance_ids = {
        str(item["entityId"])
        for item in provenance
        if isinstance(item.get("entityId"), str)
    }
    missing_nodes = tuple(
        sorted(set(expected.node_ids).difference(node_ids))
    )
    missing_edges = tuple(
        sorted(set(expected.edge_ids).difference(edge_ids))
    )
    missing_provenance = tuple(
        sorted(
            set(expected.provenance_entity_ids).difference(provenance_ids)
        )
    )
    provenance_scope = (
        tuple(
            item
            for item in provenance
            if item.get("entityId") in expected.provenance_entity_ids
        )
        if expected.provenance_entity_ids
        else provenance
    )
    grounded = sum(
        1
        for item in provenance_scope
        if isinstance(item.get("sourceSpans"), list)
        and bool(item.get("sourceSpans"))
    )
    source_satisfied = (
        not expected.require_source_grounded_provenance
        or bool(provenance_scope)
        and grounded == len(provenance_scope)
    )
    graph_text = graph_context.get("contextText")
    graph_text_present = isinstance(graph_text, str) and bool(
        graph_text.strip()
    )
    text_truncated = graph_context.get("contextTextTruncated") is True
    limits = graph_context.get("limitsHit")
    budget_truncated = bool(
        text_truncated
        or graph_context.get("sourceTruncated") is True
        or _json_int(graph_context.get("omittedProvenanceCount")) > 0
        or isinstance(limits, list)
        and limits
    )
    status_value = graph_context.get("status")
    status = status_value if isinstance(status_value, str) else None
    modes = RagContextGraphEvaluationFailureModes(
        retrieval_miss=not context.chunks,
        seed_miss=status == "no_seeds",
        graph_not_found=status == "graph_not_found",
        traversal_empty=(
            status == "succeeded"
            and bool(expected.node_ids or expected.edge_ids)
            and _json_int(graph_context.get("nodeCount"))
            + _json_int(graph_context.get("edgeCount"))
            == 0
        ),
        expected_node_missing=bool(missing_nodes),
        expected_edge_missing=bool(missing_edges),
        expected_provenance_missing=bool(missing_provenance),
        source_grounding_failed=not source_satisfied,
        graph_context_text_missing=(
            expected.require_graph_context_text and not graph_text_present
        ),
        context_text_truncated=(
            expected.require_context_text_not_truncated and text_truncated
        ),
        budget_truncated=budget_truncated,
    )
    passed = bool(
        not missing_nodes
        and not missing_edges
        and not missing_provenance
        and source_satisfied
        and (
            not expected.require_graph_context_text or graph_text_present
        )
        and (
            not expected.require_context_text_not_truncated
            or not text_truncated
        )
    )
    categories = () if passed else _failure_categories(modes)
    return RagContextGraphEvaluationResult(
        status=status,
        expected_node_count=len(expected.node_ids),
        matched_node_count=len(expected.node_ids) - len(missing_nodes),
        missing_node_ids=missing_nodes,
        expected_edge_count=len(expected.edge_ids),
        matched_edge_count=len(expected.edge_ids) - len(missing_edges),
        missing_edge_ids=missing_edges,
        expected_provenance_count=len(expected.provenance_entity_ids),
        matched_provenance_count=(
            len(expected.provenance_entity_ids) - len(missing_provenance)
        ),
        missing_provenance_entity_ids=missing_provenance,
        source_grounded_provenance_count=grounded,
        source_grounding_satisfied=source_satisfied,
        graph_context_text_present=graph_text_present,
        context_text_truncated=text_truncated,
        budget_truncated=budget_truncated,
        failure_modes=modes,
        failure_categories=categories,
        passed=passed,
    )


def _failure_categories(
    modes: RagContextGraphEvaluationFailureModes,
) -> tuple[str, ...]:
    return tuple(
        name
        for name, enabled in (
            ("retrieval_miss", modes.retrieval_miss),
            ("seed_miss", modes.seed_miss),
            ("graph_not_found", modes.graph_not_found),
            ("traversal_empty", modes.traversal_empty),
            ("expected_node_missing", modes.expected_node_missing),
            ("expected_edge_missing", modes.expected_edge_missing),
            (
                "expected_provenance_missing",
                modes.expected_provenance_missing,
            ),
            ("source_grounding_failed", modes.source_grounding_failed),
            (
                "graph_context_text_missing",
                modes.graph_context_text_missing,
            ),
            ("context_text_truncated", modes.context_text_truncated),
            ("budget_truncated", modes.budget_truncated),
        )
        if enabled
    )


def _empty_graph_evaluation(
    categories: tuple[str, ...],
) -> RagContextGraphEvaluationResult:
    return RagContextGraphEvaluationResult(
        status=None,
        expected_node_count=0,
        matched_node_count=0,
        missing_node_ids=(),
        expected_edge_count=0,
        matched_edge_count=0,
        missing_edge_ids=(),
        expected_provenance_count=0,
        matched_provenance_count=0,
        missing_provenance_entity_ids=(),
        source_grounded_provenance_count=0,
        source_grounding_satisfied=True,
        graph_context_text_present=False,
        context_text_truncated=False,
        budget_truncated=False,
        failure_modes=RagContextGraphEvaluationFailureModes(),
        failure_categories=categories,
        passed=False,
    )


def _aggregate_context_evaluation(
    requested: int,
    cases: Sequence[RagContextEvaluationCaseResult],
    stopped: bool,
) -> RagContextEvaluationResult:
    succeeded = tuple(case for case in cases if case.status == "succeeded")
    expected_nodes = sum(case.graph.expected_node_count for case in succeeded)
    expected_edges = sum(case.graph.expected_edge_count for case in succeeded)
    expected_provenance = sum(
        case.graph.expected_provenance_count for case in succeeded
    )
    failures = Counter(
        category for case in cases for category in case.failure_categories
    )
    limits = Counter(reason for case in cases for reason in case.limit_reasons)
    return RagContextEvaluationResult(
        requested=requested,
        attempted=len(cases),
        succeeded=len(succeeded),
        failed=len(cases) - len(succeeded),
        stopped_on_error=stopped,
        passed_count=sum(case.passed for case in cases),
        pass_rate=sum(case.passed for case in cases) / len(cases)
        if cases
        else 0.0,
        node_hit_rate=(
            sum(case.graph.matched_node_count for case in succeeded)
            / expected_nodes
            if expected_nodes
            else 1.0
        ),
        edge_hit_rate=(
            sum(case.graph.matched_edge_count for case in succeeded)
            / expected_edges
            if expected_edges
            else 1.0
        ),
        provenance_hit_rate=(
            sum(case.graph.matched_provenance_count for case in succeeded)
            / expected_provenance
            if expected_provenance
            else 1.0
        ),
        failure_category_counts=dict(sorted(failures.items())),
        limit_reason_counts=dict(sorted(limits.items())),
        cases=tuple(cases),
    )


def _evaluation_query_id(
    test_case: RagContextEvaluationCase,
    index: int,
) -> str:
    query_id = (test_case.metadata or {}).get("queryId")
    if isinstance(query_id, str) and query_id.strip():
        return query_id
    return test_case.name if test_case.name and test_case.name.strip() else f"case-{index}"


def _evaluation_profile_name(request: RagContextRequest) -> str | None:
    return (
        request.retrieval.profile
        or (
            request.graph_expansion.profile.id
            if request.graph_expansion is not None
            else None
        )
        or request.retrieval.search_mode
    )


def _entity_ids(value: object) -> tuple[str, ...]:
    if not isinstance(value, list):
        return ()
    return tuple(
        dict.fromkeys(
            str(item["id"])
            for item in value
            if isinstance(item, Mapping) and isinstance(item.get("id"), str)
        )
    )


def _graph_limit_reasons(
    context: RagContextEnvelope,
) -> tuple[str, ...]:
    reasons: list[str] = []
    if context.graph_expansion is not None:
        limits = context.graph_expansion.get("limitsHit")
        if isinstance(limits, list):
            reasons.extend(
                item for item in limits if isinstance(item, str)
            )
    graph = context.graph_context or {}
    if graph.get("contextTextTruncated") is True:
        reasons.append("maxGraphContextChars")
    if graph.get("sourceTruncated") is True:
        reasons.append("maxRecords")
    return tuple(dict.fromkeys(reasons))


def _json_int(value: object) -> int:
    return value if isinstance(value, int) and not isinstance(value, bool) else 0


def _render_graph_text(projection: VyralGraphProjection) -> str:
    nodes = projection.nodes
    edges = projection.edges
    if not nodes and not edges:
        return ""
    lines = ["Graph context:"]
    starts = projection.start_node_ids
    if starts:
        lines.append("Seeds: " + ", ".join(starts))
    if nodes:
        lines.append("Nodes:")
        for node in nodes:
            value = f"- {node.id}"
            if node.type:
                value += f" ({node.type})"
            if node.label:
                value += f": {node.label}"
            lines.append(value)
    if edges:
        lines.append("Edges:")
        for edge in edges:
            value = (
                f"- {edge.source_id} --{edge.predicate}--> {edge.target_id}"
            )
            if edge.label:
                value += f" ({edge.label})"
            lines.append(value)
    return "\n".join(lines)


def _render_context_text(
    chunks: Sequence[RagContextChunk],
    citations: Sequence[RagContextCitation],
    graph_context: JSONObject | None = None,
) -> str:
    lines = ["Context:"]
    for chunk in chunks:
        marker = (
            "[" + ", ".join(chunk.citation_ids) + "]"
            if chunk.citation_ids
            else f"[chunk:{chunk.rank}]"
        )
        normalized = "\n".join(
            line.rstrip()
            for line in chunk.text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
        )
        lines.extend((marker + " " + normalized, ""))
    if citations:
        lines.append("Citations:")
        for citation in citations:
            location = citation.source_uri or citation.record_id
            label = (
                " - " + citation.source_label if citation.source_label else ""
            )
            lines.append(
                f"[{citation.id}] {location}{label} "
                f"(record: {citation.record_id})"
            )
    if graph_context is not None:
        graph_text = graph_context.get("contextText")
        if isinstance(graph_text, str) and graph_text.strip():
            lines.extend(("", graph_text))
    return "\n".join(lines).rstrip()


def _effective_mode(
    trace: JSONObject | None,
    request: RetrievalRequest,
) -> str:
    if trace is not None:
        summary = trace.get("resultSummary")
        if isinstance(summary, Mapping):
            mode = summary.get("searchMode")
            if isinstance(mode, str):
                return mode
        mode = trace.get("searchMode")
        if isinstance(mode, str):
            return mode
    if request.search_mode and request.search_mode.strip():
        return request.search_mode.strip().lower()
    return "vector" if request.embedding is not None or request.vector_fields else "lexical"


def _render_user_content(
    instruction: str,
    context: str,
    citation_instruction: str | None,
) -> str:
    lines = ["Question:", instruction, ""]
    if citation_instruction:
        lines.extend(("Citation rule:", citation_instruction, ""))
    lines.append(context)
    return "\n".join(lines).rstrip()


def _render_prompt(messages: Sequence[RagPromptMessage]) -> str:
    return "\n\n".join(
        message.role.upper() + ":\n" + message.content for message in messages
    ).rstrip()


def _hash(value: str) -> str:
    return "sha256:" + sha256(value.encode("utf-8")).hexdigest()
