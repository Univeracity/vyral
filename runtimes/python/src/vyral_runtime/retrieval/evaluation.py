from __future__ import annotations

from dataclasses import replace
import math
from time import perf_counter
from typing import Any, Callable, Iterable, Mapping, Sequence

from ..async_runtime import RuntimeExecutor
from ..local.models import JSONObject, VyralRecord
from .evaluation_models import (
    MAX_EVALUATION_CASES,
    MAX_EVALUATION_K,
    MAX_EVALUATION_VARIANTS,
    MAX_EXPECTED_MATCHES,
    MAX_HARD_NEGATIVES,
    RetrievalEvaluationCase,
    RetrievalEvaluationCaseResult,
    RetrievalEvaluationComparisonRequest,
    RetrievalEvaluationComparisonResult,
    RetrievalEvaluationExpectedMatch,
    RetrievalEvaluationExpectedResult,
    RetrievalEvaluationHardNegativeMatch,
    RetrievalEvaluationHardNegativeResult,
    RetrievalEvaluationMetricDeltas,
    RetrievalEvaluationMetrics,
    RetrievalEvaluationRequest,
    RetrievalEvaluationResult,
    RetrievalEvaluationTopResult,
    RetrievalEvaluationVariant,
    RetrievalEvaluationVariantResult,
)
from .models import RetrievalMatch, RetrievalRequest
from .service import RetrievalService


EvaluationProgress = Callable[[RetrievalEvaluationResult], None]
ComparisonProgress = Callable[[RetrievalEvaluationComparisonResult], None]


class RetrievalEvaluationService:
    def __init__(
        self,
        retrieval_service: RetrievalService,
        *,
        executor: RuntimeExecutor | None = None,
    ) -> None:
        self.retrieval_service = retrieval_service
        self.executor = executor or RuntimeExecutor()
        self._owns_executor = executor is None

    def evaluate(
        self,
        request: RetrievalEvaluationRequest | Mapping[str, Any],
        *,
        progress: EvaluationProgress | None = None,
    ) -> RetrievalEvaluationResult:
        normalized = RetrievalEvaluationRequest.from_value(request)
        _validate_evaluation_request(normalized)
        cases: list[RetrievalEvaluationCaseResult] = []
        if progress is not None:
            progress(_aggregate(len(normalized.cases), cases, False))
        stopped = False
        for index, test_case in enumerate(normalized.cases):
            started = perf_counter()
            try:
                result = self._evaluate_case(normalized, test_case, index)
                result = replace(
                    result,
                    duration_ms=(perf_counter() - started) * 1000.0,
                )
            except (TypeError, ValueError, LookupError, NotImplementedError) as exc:
                result = RetrievalEvaluationCaseResult(
                    index=index,
                    name=test_case.name,
                    query=test_case.request.query,
                    status="failed",
                    error=str(exc),
                    duration_ms=(perf_counter() - started) * 1000.0,
                    k=_resolve_k(normalized.default_k, test_case),
                    expected_count=len(test_case.expected),
                    hard_negative_count=len(test_case.hard_negatives),
                )
                cases.append(result)
                if progress is not None:
                    progress(_aggregate(len(normalized.cases), cases, False))
                if not normalized.continue_on_error:
                    stopped = index + 1 < len(normalized.cases)
                    break
                continue
            cases.append(result)
            if progress is not None:
                progress(_aggregate(len(normalized.cases), cases, False))
        evaluation_result = _aggregate(len(normalized.cases), cases, stopped)
        if progress is not None:
            progress(evaluation_result)
        return evaluation_result

    async def aevaluate(
        self,
        request: RetrievalEvaluationRequest | Mapping[str, Any],
        *,
        progress: EvaluationProgress | None = None,
    ) -> RetrievalEvaluationResult:
        return await self.executor.run(
            lambda: self.evaluate(request, progress=progress)
        )

    def compare(
        self,
        request: RetrievalEvaluationComparisonRequest | Mapping[str, Any],
        *,
        progress: ComparisonProgress | None = None,
    ) -> RetrievalEvaluationComparisonResult:
        normalized = RetrievalEvaluationComparisonRequest.from_value(request)
        _validate_comparison_request(normalized)
        variants: list[RetrievalEvaluationVariantResult] = []
        baseline: RetrievalEvaluationMetrics | None = None
        stopped = False
        for index, variant in enumerate(normalized.variants):
            started = perf_counter()
            try:
                applied = tuple(
                    replace(case, request=_apply_variant(case.request, variant))
                    for case in normalized.cases
                )
                evaluation = self.evaluate(
                    RetrievalEvaluationRequest(
                        cases=applied,
                        continue_on_error=normalized.continue_on_error,
                        default_k=normalized.default_k,
                        include_top_results=normalized.include_top_results,
                    )
                )
                metrics = _as_metrics(evaluation)
                if index == 0:
                    baseline = metrics
                variant_result = RetrievalEvaluationVariantResult(
                    id=variant.id,
                    label=variant.label,
                    status="succeeded",
                    error=None,
                    duration_ms=(perf_counter() - started) * 1000.0,
                    metrics=metrics,
                    delta_from_baseline=(
                        None
                        if index == 0 or baseline is None
                        else _delta(metrics, baseline)
                    ),
                    cases=(
                        evaluation.cases
                        if normalized.include_case_results
                        else ()
                    ),
                )
            except (TypeError, ValueError, LookupError, NotImplementedError) as exc:
                variant_result = RetrievalEvaluationVariantResult(
                    id=variant.id,
                    label=variant.label,
                    status="failed",
                    error=str(exc),
                    duration_ms=(perf_counter() - started) * 1000.0,
                    metrics=RetrievalEvaluationMetrics(
                        requested=len(normalized.cases)
                    ),
                )
                variants.append(variant_result)
                current = _comparison_result(normalized, variants, False)
                if progress is not None:
                    progress(current)
                if not normalized.continue_on_error:
                    stopped = index + 1 < len(normalized.variants)
                    break
                continue
            variants.append(variant_result)
            if progress is not None:
                progress(_comparison_result(normalized, variants, False))
        result = _comparison_result(normalized, variants, stopped)
        if progress is not None:
            progress(result)
        return result

    async def acompare(
        self,
        request: RetrievalEvaluationComparisonRequest | Mapping[str, Any],
        *,
        progress: ComparisonProgress | None = None,
    ) -> RetrievalEvaluationComparisonResult:
        return await self.executor.run(
            lambda: self.compare(request, progress=progress)
        )

    def close(self) -> None:
        if self._owns_executor:
            self.executor.close()

    def _evaluate_case(
        self,
        request: RetrievalEvaluationRequest,
        test_case: RetrievalEvaluationCase,
        index: int,
    ) -> RetrievalEvaluationCaseResult:
        _validate_case(test_case)
        k = _resolve_k(request.default_k, test_case)
        retrieval_request = replace(
            test_case.request,
            limit=max(test_case.request.limit, k),
            include_trace=(
                test_case.request.include_trace
                or bool(
                    test_case.request.rerank is not None
                    and test_case.request.rerank.enabled
                )
                or request.include_top_results
            ),
        )
        envelope = self.retrieval_service.search(retrieval_request)
        matches = tuple(sorted(envelope.results, key=lambda item: item.rank)[:k])
        expected_results = [
            RetrievalEvaluationExpectedResult(
                item.id,
                item.partition_key,
                item.collection,
                item.relevance,
            )
            for item in test_case.expected
        ]
        negative_results = [
            RetrievalEvaluationHardNegativeResult(
                item.id,
                item.partition_key,
                item.collection,
                item.reason,
            )
            for item in test_case.hard_negatives
        ]
        expected_indexes: set[int] = set()
        negative_indexes: set[int] = set()
        first_relevant: int | None = None
        first_negative: int | None = None
        dcg = 0.0
        top_results: list[RetrievalEvaluationTopResult] = []
        trace_summary = _trace_summary(envelope.trace)
        rerank_enabled = bool(
            trace_summary.get(
                "rerankEnabled",
                retrieval_request.rerank is not None
                and retrieval_request.rerank.enabled,
            )
        )
        rerank_fallback = bool(trace_summary.get("rerankFallbackApplied", False))
        for match in matches:
            expected_index = _first_match(match, test_case.expected)
            negative_index = _first_match(match, test_case.hard_negatives)
            if expected_index is not None:
                expected_indexes.add(expected_index)
                first_relevant = first_relevant or match.rank
                expected = test_case.expected[expected_index]
                dcg += expected.relevance / math.log2(match.rank + 1)
                current = expected_results[expected_index]
                if current.rank is None:
                    expected_results[expected_index] = replace(
                        current, rank=match.rank, score=match.score
                    )
            if negative_index is not None:
                negative_indexes.add(negative_index)
                first_negative = first_negative or match.rank
                current_negative = negative_results[negative_index]
                if current_negative.rank is None:
                    negative_results[negative_index] = replace(
                        current_negative,
                        rank=match.rank,
                        score=match.score,
                    )
            if request.include_top_results:
                details = (
                    match.diagnostics.details
                    if match.diagnostics is not None
                    else {}
                )
                raw_fields = details.get("vectorIndexFields", ())
                top_results.append(
                    RetrievalEvaluationTopResult(
                        rank=match.rank,
                        score=match.score,
                        collection=match.collection,
                        id=match.record.id,
                        partition_key=match.record.partition_key,
                        type=match.record.type or None,
                        matched_expected=expected_index is not None,
                        matched_hard_negative=negative_index is not None,
                        rerank_fallback_applied=bool(
                            details.get(
                                "rerankFallbackApplied", rerank_fallback
                            )
                        ),
                        rerank_provider_status=_string(
                            details.get("rerankProviderStatus")
                        ),
                        vector_index_used=bool(
                            details.get(
                                "vectorIndexUsed",
                                match.diagnostics is not None
                                and "index.sqlite_vector"
                                in match.diagnostics.reason_codes,
                            )
                        ),
                        vector_index_provider=_string(
                            details.get("vectorIndexProvider")
                        ),
                        vector_index_fields=(
                            tuple(
                                str(field)
                                for field in raw_fields
                                if str(field).strip()
                            )
                            if isinstance(raw_fields, (list, tuple))
                            else ()
                        ),
                        snippet=match.snippet,
                    )
                )
        matched = len(expected_indexes)
        negative_matched = len(negative_indexes)
        ideal_dcg = sum(
            relevance / math.log2(position + 2)
            for position, relevance in enumerate(
                sorted(
                    (item.relevance for item in test_case.expected),
                    reverse=True,
                )[:k]
            )
        )
        return RetrievalEvaluationCaseResult(
            index=index,
            name=test_case.name,
            query=retrieval_request.query,
            status="succeeded",
            error=None,
            duration_ms=0.0,
            k=k,
            expected_count=len(test_case.expected),
            retrieved_count=len(matches),
            matched_count=matched,
            hit=matched > 0,
            first_relevant_rank=first_relevant,
            reciprocal_rank=1.0 / first_relevant if first_relevant else 0.0,
            precision_at_k=matched / k,
            recall_at_k=matched / len(test_case.expected),
            ndcg_at_k=dcg / ideal_dcg if ideal_dcg else 0.0,
            hard_negative_count=len(test_case.hard_negatives),
            hard_negative_matched_count=negative_matched,
            hard_negative_hit=negative_matched > 0,
            first_hard_negative_rank=first_negative,
            hard_negative_rate_at_k=negative_matched / k,
            rerank_enabled=rerank_enabled,
            rerank_provider=_string(trace_summary.get("rerankProvider")),
            rerank_trace_id=_string(trace_summary.get("rerankTraceId")),
            rerank_fallback_applied=rerank_fallback,
            rerank_failure_class=_string(
                trace_summary.get("rerankFailureClass")
            ),
            rerank_provider_status=_string(
                trace_summary.get("rerankProviderStatus")
            ),
            rerank_input_candidate_count=_integer(
                trace_summary.get("rerankInputCandidateCount")
            ),
            rerank_provider_payload_bytes=_integer(
                trace_summary.get("rerankProviderPayloadBytes")
            ),
            rerank_provider_max_input_bytes=_integer(
                trace_summary.get("rerankProviderMaxInputBytes")
            ),
            expected=tuple(expected_results),
            hard_negatives=tuple(negative_results),
            top_results=tuple(top_results),
        )


def _validate_evaluation_request(request: RetrievalEvaluationRequest) -> None:
    if not request.cases:
        raise ValueError("Retrieval evaluation request must include at least one case.")
    if len(request.cases) > MAX_EVALUATION_CASES:
        raise ValueError(
            f"Retrieval evaluation supports at most {MAX_EVALUATION_CASES} cases."
        )
    _validate_k(request.default_k, "Retrieval evaluation defaultK")


def _validate_comparison_request(
    request: RetrievalEvaluationComparisonRequest,
) -> None:
    if not request.cases:
        raise ValueError("Retrieval evaluation comparison must include cases.")
    if len(request.cases) > MAX_EVALUATION_CASES:
        raise ValueError(
            f"Retrieval evaluation supports at most {MAX_EVALUATION_CASES} cases."
        )
    if not request.variants:
        raise ValueError("Retrieval evaluation comparison must include variants.")
    if len(request.variants) > MAX_EVALUATION_VARIANTS:
        raise ValueError(
            "Retrieval evaluation comparison supports at most "
            f"{MAX_EVALUATION_VARIANTS} variants."
        )
    _validate_k(request.default_k, "Retrieval evaluation comparison defaultK")
    identifiers: set[str] = set()
    for variant in request.variants:
        if not variant.id.strip():
            raise ValueError("Retrieval evaluation comparison variant id is required.")
        if variant.id in identifiers:
            raise ValueError(f"Retrieval evaluation variant {variant.id!r} is duplicated.")
        identifiers.add(variant.id)
        if variant.limit is not None and variant.limit <= 0:
            raise ValueError("Retrieval evaluation variant limit must be positive.")


def _validate_case(test_case: RetrievalEvaluationCase) -> None:
    if not test_case.expected:
        raise ValueError("Retrieval evaluation case must include expected matches.")
    if len(test_case.expected) > MAX_EXPECTED_MATCHES:
        raise ValueError(
            f"Retrieval evaluation supports at most {MAX_EXPECTED_MATCHES} expected matches."
        )
    if len(test_case.hard_negatives) > MAX_HARD_NEGATIVES:
        raise ValueError(
            f"Retrieval evaluation supports at most {MAX_HARD_NEGATIVES} hard negatives."
        )
    _validate_k(test_case.k, "Retrieval evaluation case k")
    for expected in test_case.expected:
        if not _reference_ids(expected) and not _has_source_identity(expected.sources):
            raise ValueError(
                "Expected match must include id, aliases, sourceIds, or sources."
            )
        if expected.relevance <= 0:
            raise ValueError("Expected match relevance must be greater than zero.")
    for negative in test_case.hard_negatives:
        if not _reference_ids(negative) and not _has_source_identity(negative.sources):
            raise ValueError(
                "Hard negative must include id, aliases, sourceIds, or sources."
            )
        if any(_references_overlap(expected, negative) for expected in test_case.expected):
            raise ValueError(
                f"Hard negative {negative.id!r} overlaps an expected match."
            )


def _validate_k(value: int | None, name: str) -> None:
    if value is not None and not 1 <= value <= MAX_EVALUATION_K:
        raise ValueError(f"{name} must be between 1 and {MAX_EVALUATION_K}.")


def _resolve_k(default_k: int | None, test_case: RetrievalEvaluationCase) -> int:
    return max(
        1,
        min(
            MAX_EVALUATION_K,
            test_case.k or default_k or test_case.request.limit or 10,
        ),
    )


def _aggregate(
    requested: int,
    cases: Sequence[RetrievalEvaluationCaseResult],
    stopped: bool,
) -> RetrievalEvaluationResult:
    succeeded = tuple(case for case in cases if case.status == "succeeded")
    hard_negative_cases = tuple(
        case for case in succeeded if case.hard_negative_count > 0
    )
    rerank_cases = tuple(case for case in succeeded if case.rerank_enabled)
    count = len(succeeded)
    hard_count = len(hard_negative_cases)
    rerank_count = len(rerank_cases)
    return RetrievalEvaluationResult(
        requested=requested,
        attempted=len(cases),
        succeeded=count,
        failed=len(cases) - count,
        stopped_on_error=stopped,
        hit_count=sum(1 for case in succeeded if case.hit),
        hit_rate=_average_flags(succeeded, "hit"),
        mean_reciprocal_rank=_average(succeeded, "reciprocal_rank"),
        mean_precision_at_k=_average(succeeded, "precision_at_k"),
        mean_recall_at_k=_average(succeeded, "recall_at_k"),
        mean_ndcg_at_k=_average(succeeded, "ndcg_at_k"),
        hard_negative_case_count=hard_count,
        hard_negative_hit_count=sum(
            1 for case in hard_negative_cases if case.hard_negative_hit
        ),
        hard_negative_hit_rate=_average_flags(
            hard_negative_cases, "hard_negative_hit"
        ),
        mean_hard_negative_rate_at_k=_average(
            hard_negative_cases, "hard_negative_rate_at_k"
        ),
        rerank_case_count=rerank_count,
        rerank_fallback_case_count=sum(
            1 for case in rerank_cases if case.rerank_fallback_applied
        ),
        rerank_fallback_rate=_average_flags(
            rerank_cases, "rerank_fallback_applied"
        ),
        cases=tuple(cases),
    )


def _average(
    cases: Sequence[RetrievalEvaluationCaseResult],
    field: str,
) -> float:
    return (
        sum(float(getattr(case, field)) for case in cases) / len(cases)
        if cases
        else 0.0
    )


def _average_flags(
    cases: Sequence[RetrievalEvaluationCaseResult],
    field: str,
) -> float:
    return (
        sum(1 for case in cases if bool(getattr(case, field))) / len(cases)
        if cases
        else 0.0
    )


def _as_metrics(result: RetrievalEvaluationResult) -> RetrievalEvaluationMetrics:
    values = {
        field: getattr(result, field)
        for field in RetrievalEvaluationMetrics.__dataclass_fields__
    }
    return RetrievalEvaluationMetrics(**values)


def _delta(
    metrics: RetrievalEvaluationMetrics,
    baseline: RetrievalEvaluationMetrics,
) -> RetrievalEvaluationMetricDeltas:
    return RetrievalEvaluationMetricDeltas(
        hit_rate=metrics.hit_rate - baseline.hit_rate,
        mean_reciprocal_rank=(
            metrics.mean_reciprocal_rank - baseline.mean_reciprocal_rank
        ),
        mean_precision_at_k=metrics.mean_precision_at_k - baseline.mean_precision_at_k,
        mean_recall_at_k=metrics.mean_recall_at_k - baseline.mean_recall_at_k,
        mean_ndcg_at_k=metrics.mean_ndcg_at_k - baseline.mean_ndcg_at_k,
        hard_negative_hit_rate=(
            metrics.hard_negative_hit_rate - baseline.hard_negative_hit_rate
        ),
        mean_hard_negative_rate_at_k=(
            metrics.mean_hard_negative_rate_at_k
            - baseline.mean_hard_negative_rate_at_k
        ),
        rerank_fallback_rate=(
            metrics.rerank_fallback_rate - baseline.rerank_fallback_rate
        ),
    )


def _comparison_result(
    request: RetrievalEvaluationComparisonRequest,
    variants: Sequence[RetrievalEvaluationVariantResult],
    stopped: bool,
) -> RetrievalEvaluationComparisonResult:
    return RetrievalEvaluationComparisonResult(
        requested=len(request.cases),
        variants_requested=len(request.variants),
        variants_attempted=len(variants),
        variants_succeeded=sum(1 for item in variants if item.status == "succeeded"),
        variants_failed=sum(1 for item in variants if item.status == "failed"),
        stopped_on_error=stopped,
        baseline_variant_id=request.variants[0].id,
        variants=tuple(variants),
    )


def _apply_variant(
    request: RetrievalRequest,
    variant: RetrievalEvaluationVariant,
) -> RetrievalRequest:
    changes: dict[str, Any] = {}
    for name in (
        "profile",
        "collections",
        "partition_keys",
        "filter",
        "embedding",
        "vector_fields",
        "search_mode",
        "lexical",
        "hybrid",
        "rerank",
        "limit",
        "min_score",
        "include_trace",
    ):
        value = getattr(variant, name)
        if value is not None:
            changes[name] = value
    return replace(request, **changes)


def _first_match(
    match: RetrievalMatch,
    references: Sequence[
        RetrievalEvaluationExpectedMatch | RetrievalEvaluationHardNegativeMatch
    ],
) -> int | None:
    for index, reference in enumerate(references):
        if _scope_matches(match, reference) and _record_matches(
            match.record, reference
        ):
            return index
    return None


def _scope_matches(
    match: RetrievalMatch,
    reference: RetrievalEvaluationExpectedMatch
    | RetrievalEvaluationHardNegativeMatch,
) -> bool:
    return (
        (not reference.collection or reference.collection == match.collection)
        and (
            not reference.partition_key
            or reference.partition_key == match.record.partition_key
        )
    )


def _record_matches(
    record: VyralRecord,
    reference: RetrievalEvaluationExpectedMatch
    | RetrievalEvaluationHardNegativeMatch,
) -> bool:
    reference_ids = _reference_ids(reference)
    record_ids = _record_reference_ids(record)
    if reference_ids.intersection(record_ids):
        return True
    return any(
        _source_matches(candidate, expected)
        for candidate in record.sources or ()
        for expected in reference.sources
    )


def _record_reference_ids(record: VyralRecord) -> set[str]:
    values = {record.id}
    for source in record.sources or ():
        _add_text(values, source.get("id"))
    for key in (
        "alias",
        "aliases",
        "aliasId",
        "aliasIds",
        "containedId",
        "containedIds",
        "containedRecordId",
        "containedRecordIds",
        "sourceId",
        "sourceIds",
        "verseId",
        "verseIds",
    ):
        _add_values(values, (record.metadata or {}).get(key))
    return values


def _reference_ids(
    reference: RetrievalEvaluationExpectedMatch
    | RetrievalEvaluationHardNegativeMatch,
) -> set[str]:
    values: set[str] = set()
    _add_text(values, reference.id)
    _add_values(values, reference.aliases)
    _add_values(values, reference.source_ids)
    for source in reference.sources:
        _add_text(values, source.get("id"))
    return values


def _add_values(output: set[str], value: object) -> None:
    if isinstance(value, str):
        _add_text(output, value)
    elif isinstance(value, (list, tuple)):
        for item in value:
            _add_values(output, item)


def _add_text(output: set[str], value: object) -> None:
    if isinstance(value, str) and value.strip():
        output.add(value)


def _has_source_identity(sources: Iterable[JSONObject]) -> bool:
    return any(
        any(isinstance(source.get(key), str) and str(source[key]).strip() for key in ("id", "kind", "uri"))
        for source in sources
    )


def _references_overlap(
    expected: RetrievalEvaluationExpectedMatch,
    negative: RetrievalEvaluationHardNegativeMatch,
) -> bool:
    if (
        expected.collection
        and negative.collection
        and expected.collection != negative.collection
    ):
        return False
    if (
        expected.partition_key
        and negative.partition_key
        and expected.partition_key != negative.partition_key
    ):
        return False
    if _reference_ids(expected).intersection(_reference_ids(negative)):
        return True
    return any(
        _source_overlaps(first, second)
        for first in expected.sources
        for second in negative.sources
    )


def _source_matches(candidate: JSONObject, expected: JSONObject) -> bool:
    compared = False
    for key in ("id", "kind", "uri"):
        expected_value = expected.get(key)
        if isinstance(expected_value, str) and expected_value.strip():
            compared = True
            if candidate.get(key) != expected_value:
                return False
    if not compared:
        return False
    return _span_contains(candidate.get("span"), expected.get("span"))


def _source_overlaps(first: JSONObject, second: JSONObject) -> bool:
    compared = False
    for key in ("id", "kind", "uri"):
        first_value = first.get(key)
        second_value = second.get(key)
        if (
            isinstance(first_value, str)
            and first_value.strip()
            and isinstance(second_value, str)
            and second_value.strip()
        ):
            compared = True
            if first_value != second_value:
                return False
    return compared and _spans_overlap(first.get("span"), second.get("span"))


def _span(value: object) -> Mapping[str, Any] | None:
    return value if isinstance(value, Mapping) else None


def _span_contains(candidate_value: object, expected_value: object) -> bool:
    expected = _span(expected_value)
    if expected is None:
        return True
    candidate = _span(candidate_value)
    if candidate is None:
        return False
    start = expected.get("charStart")
    end = expected.get("charEnd")
    if isinstance(start, int):
        candidate_start = candidate.get("charStart")
        if not isinstance(candidate_start, int) or candidate_start > start:
            return False
    if isinstance(end, int):
        candidate_end = candidate.get("charEnd")
        if not isinstance(candidate_end, int) or candidate_end < end:
            return False
    for key in ("line", "column", "anchor"):
        selected = expected.get(key)
        if selected is not None and candidate.get(key) != selected:
            return False
    return True


def _spans_overlap(first_value: object, second_value: object) -> bool:
    first = _span(first_value)
    second = _span(second_value)
    if first is None or second is None:
        return True
    first_start = first.get("charStart")
    first_end = first.get("charEnd")
    second_start = second.get("charStart")
    second_end = second.get("charEnd")
    if (
        isinstance(first_start, int)
        and isinstance(first_end, int)
        and isinstance(second_start, int)
        and isinstance(second_end, int)
    ):
        return first_start < second_end and second_start < first_end
    first_line = first.get("line")
    second_line = second.get("line")
    if isinstance(first_line, int) and isinstance(second_line, int):
        return first_line == second_line
    first_anchor = first.get("anchor")
    second_anchor = second.get("anchor")
    return not first_anchor or not second_anchor or first_anchor == second_anchor


def _trace_summary(trace: JSONObject | None) -> Mapping[str, Any]:
    if trace is None:
        return {}
    summary = trace.get("resultSummary")
    return summary if isinstance(summary, Mapping) else trace


def _string(value: object) -> str | None:
    return value if isinstance(value, str) and value.strip() else None


def _integer(value: object) -> int:
    return value if isinstance(value, int) and not isinstance(value, bool) else 0
