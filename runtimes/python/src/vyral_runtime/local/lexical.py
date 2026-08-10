from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import math
import re
import struct
from typing import Any, Iterable, Mapping, Sequence, cast

from .models import JSONObject, VyralRecord
from .query_engine import QueryValidationError
from .query_models import LexicalSearchOptions, RetrievalDiagnostics


_DEFAULT_FIELDS = ("/content", "/metadata", "/id", "/type", "/sources")
_STOP_WORDS = frozenset(
    {
        "a",
        "an",
        "and",
        "are",
        "as",
        "at",
        "be",
        "by",
        "for",
        "from",
        "in",
        "is",
        "it",
        "of",
        "on",
        "or",
        "that",
        "the",
        "to",
        "with",
    }
)
_WHITESPACE = re.compile(r"\s+")


@dataclass(frozen=True)
class NormalizedLexicalOptions:
    fields: tuple[str, ...]
    field_boosts: Mapping[str, float]
    scoring: str
    match_mode: str
    bm25_k1: float
    bm25_b: float
    phrase_boost: float
    exact_boost: float
    metadata_boost: float
    prefix_matching: bool
    prefix_min_chars: int
    required_phrase_groups: tuple[tuple[str, ...], ...]


@dataclass(frozen=True)
class _QueryParts:
    normalized_text: str
    tokens: tuple[str, ...]
    phrases: tuple[str, ...]


@dataclass(frozen=True)
class _Field:
    path: str
    normalized_text: str
    normalized_phrase_text: str
    frequencies: Mapping[str, int]
    length: int


@dataclass(frozen=True)
class _Document:
    record: VyralRecord
    fields: tuple[_Field, ...]
    length: int


@dataclass(frozen=True)
class LexicalScore:
    record: VyralRecord
    score: float
    base_score: float
    raw_score: float
    term_coverage: float
    frequency_score: float
    phrase_boost: float
    exact_boost: float
    metadata_boost: float
    max_field_boost: float
    document_length: int
    average_document_length: float
    corpus_document_count: int
    matched_fields: tuple[str, ...]
    matched_terms: tuple[str, ...]
    matched_phrases: tuple[str, ...]
    matched_prefix_terms: tuple[str, ...]
    matched_required_phrase_groups: tuple[tuple[str, ...], ...]
    fields: tuple[str, ...]
    term_idf: Mapping[str, float]
    term_scores: Mapping[str, float]
    field_boosts: Mapping[str, float]
    scoring: str
    match_mode: str
    prefix_matching: bool
    prefix_min_chars: int

    def diagnostics(
        self,
        *,
        collection: str,
        candidate_source: str,
        candidate_count: int,
        fts_expression: str | None,
        required_phrase_groups: tuple[tuple[str, ...], ...],
    ) -> RetrievalDiagnostics:
        details: JSONObject = {
            "lexicalFields": list(self.fields),
            "lexicalScoring": self.scoring,
            "lexicalMatchMode": self.match_mode,
            "lexicalPrefixMatching": self.prefix_matching,
            "lexicalPrefixMinChars": self.prefix_min_chars,
            "matchedRequiredPhraseGroups": [
                list(group) for group in self.matched_required_phrase_groups
            ],
            "matchedPhrases": list(self.matched_phrases),
            "matchedPrefixTerms": list(self.matched_prefix_terms),
            "termIdf": dict(self.term_idf),
            "termScores": dict(self.term_scores),
            "fieldBoosts": dict(self.field_boosts),
            "documentLength": self.document_length,
            "averageDocumentLength": self.average_document_length,
            "corpusDocumentCount": self.corpus_document_count,
            "lexicalCandidateSource": candidate_source,
            "lexicalCandidateCount": candidate_count,
        }
        if fts_expression:
            details["lexicalFtsExpression"] = fts_expression
        if required_phrase_groups:
            details["requiredPhraseGroups"] = [
                list(group) for group in required_phrase_groups
            ]
        return RetrievalDiagnostics(
            result_identity={
                "collection": collection,
                "partitionKey": self.record.partition_key,
                "id": self.record.id,
                "type": self.record.type,
                "etag": self.record.etag,
                "revision": self.record.revision,
            },
            score_components={
                "lexical": self.score,
                "lexicalBase": self.base_score,
                "lexicalRaw": self.raw_score,
                "termCoverage": self.term_coverage,
                "termFrequency": self.frequency_score,
                "phraseBoost": self.phrase_boost,
                "exactBoost": self.exact_boost,
                "metadataBoost": self.metadata_boost,
                "maxFieldBoost": self.max_field_boost,
            },
            score_normalization={
                "finalScoreKind": "lexical.score",
                "vectorScoreKind": None,
                "lexicalScoreKind": f"lexical.{self.scoring}",
                "hybridFusion": None,
                "vectorDistanceFunction": None,
                "vectorNormalization": None,
                "weights": {},
                "parameters": {},
            },
            candidate_sources=("lexical",),
            reason_codes=(
                "result.identity.record",
                "mode.lexical",
                "candidate.source.lexical",
                "score.lexical",
            ),
            matched_fields=self.matched_fields,
            matched_terms=self.matched_terms,
            details=details,
        )


def _f32(value: float) -> float:
    try:
        return float(struct.unpack("<f", struct.pack("<f", value))[0])
    except (OverflowError, struct.error) as exc:
        raise QueryValidationError("Lexical scoring produced a non-finite float32 value.") from exc


def tokenize(text: str) -> tuple[str, ...]:
    terms: list[str] = []
    builder: list[str] = []
    for character in text.lower():
        if character.isalnum():
            builder.append(character)
        elif builder:
            terms.append("".join(builder))
            builder.clear()
    if builder:
        terms.append("".join(builder))
    return tuple(terms)


def _normalize_text(text: str) -> str:
    return _WHITESPACE.sub(" ", text.strip().lower())


def _normalize_phrase_text(text: str) -> str:
    return " ".join(tokenize(text))


def _quoted_phrases(query: str) -> tuple[str, ...]:
    phrases: list[str] = []
    start = -1
    for index, character in enumerate(query):
        if character != '"':
            continue
        if start < 0:
            start = index + 1
            continue
        if index > start:
            phrases.append(query[start:index])
        start = -1
    return tuple(phrases)


def _query_parts(query: str) -> _QueryParts:
    phrases = tuple(
        dict.fromkeys(
            phrase
            for phrase in (_normalize_text(value) for value in _quoted_phrases(query))
            if phrase
        )
    )
    return _QueryParts(
        normalized_text=_normalize_text(query.replace('"', " ")),
        tokens=tokenize(query),
        phrases=phrases,
    )


def _query_terms(tokens: Sequence[str]) -> tuple[str, ...]:
    filtered = tuple(
        dict.fromkeys(
            term for term in tokens if len(term) > 1 and term not in _STOP_WORDS
        )
    )
    return filtered or tuple(dict.fromkeys(tokens))


def _normalize_required_phrase(text: str) -> str:
    if not text.strip():
        raise QueryValidationError("Lexical required phrases must be non-empty.")
    normalized = _normalize_phrase_text(text)
    if not normalized:
        raise QueryValidationError(
            "Lexical required phrases must contain at least one letter or digit."
        )
    if len(normalized) > 256:
        raise QueryValidationError(
            "Lexical required phrase cannot exceed 256 normalized characters."
        )
    return normalized


def _required_phrase_groups(
    groups: tuple[tuple[str, ...], ...] | None,
) -> tuple[tuple[str, ...], ...]:
    if not groups:
        return ()
    if len(groups) > 16:
        raise QueryValidationError(
            "Lexical requiredPhraseGroups supports at most 16 groups."
        )
    normalized_groups: list[tuple[str, ...]] = []
    for group in groups:
        if not group:
            raise QueryValidationError(
                "Each lexical requiredPhraseGroups entry must contain at least one phrase."
            )
        if len(group) > 16:
            raise QueryValidationError(
                "Each lexical requiredPhraseGroups entry supports at most 16 phrases."
            )
        normalized = tuple(
            dict.fromkeys(_normalize_required_phrase(phrase) for phrase in group)
        )
        if not normalized:
            raise QueryValidationError(
                "Each lexical requiredPhraseGroups entry must contain a non-empty phrase."
            )
        normalized_groups.append(normalized)
    return tuple(normalized_groups)


def normalize_options(options: LexicalSearchOptions) -> NormalizedLexicalOptions:
    scoring = options.scoring.strip().lower() or "bm25"
    if scoring not in {"bm25", "coverage"}:
        raise QueryValidationError(
            f"Lexical scoring {options.scoring!r} is not supported."
        )
    match_mode = options.match_mode.strip().lower() or "any"
    if match_mode not in {"any", "all"}:
        raise QueryValidationError(
            f"Lexical matchMode {options.match_mode!r} is not supported."
        )
    if options.bm25_k1 <= 0:
        raise QueryValidationError("Lexical bm25K1 must be greater than zero.")
    if options.bm25_b < 0 or options.bm25_b > 1:
        raise QueryValidationError("Lexical bm25B must be between 0 and 1.")
    if options.prefix_min_chars <= 0:
        raise QueryValidationError(
            "Lexical prefixMinChars must be greater than zero."
        )
    selected = tuple(
        dict.fromkeys(
            field.strip()
            for field in (options.fields or _DEFAULT_FIELDS)
            if field.strip()
        )
    )
    fields = selected or _DEFAULT_FIELDS
    boosts = {
        path.strip(): max(0.0, float(boost))
        for path, boost in (options.field_boosts or {}).items()
        if path.strip()
    }
    return NormalizedLexicalOptions(
        fields=fields,
        field_boosts=boosts,
        scoring=scoring,
        match_mode=match_mode,
        bm25_k1=options.bm25_k1,
        bm25_b=options.bm25_b,
        phrase_boost=max(0.0, options.phrase_boost),
        exact_boost=max(0.0, options.exact_boost),
        metadata_boost=max(0.0, options.metadata_boost),
        prefix_matching=options.prefix_matching,
        prefix_min_chars=options.prefix_min_chars,
        required_phrase_groups=_required_phrase_groups(
            options.required_phrase_groups
        ),
    )


def _resolve_pointer(root: object, path: str) -> tuple[bool, object]:
    if not path.startswith("/"):
        return False, None
    value = root
    for raw in (segment for segment in path.split("/") if segment):
        segment = raw.replace("~1", "/").replace("~0", "~")
        if isinstance(value, Mapping):
            if segment not in value:
                return False, None
            value = value[segment]
        elif isinstance(value, list) and segment.isdecimal():
            index = int(segment)
            if index >= len(value):
                return False, None
            value = value[index]
        else:
            return False, None
    return True, value


def _flatten(value: object, path: str) -> Iterable[tuple[str, str]]:
    if isinstance(value, str):
        yield path, value
    elif value is True:
        yield path, "True"
    elif value is False:
        yield path, "False"
    elif isinstance(value, (int, float)):
        yield path, str(value)
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from _flatten(item, f"{path}/{index}")
    elif isinstance(value, Mapping):
        for key, item in value.items():
            escaped = str(key).replace("~", "~0").replace("/", "~1")
            yield from _flatten(item, f"{path}/{escaped}")


def _document(record: VyralRecord, fields: Sequence[str]) -> _Document:
    root = record.to_dict()
    extracted: list[_Field] = []
    for path in fields:
        found, value = _resolve_pointer(root, path)
        if not found:
            continue
        for leaf_path, text in _flatten(value, path):
            terms = tokenize(text)
            extracted.append(
                _Field(
                    path=leaf_path,
                    normalized_text=_normalize_text(text),
                    normalized_phrase_text=_normalize_phrase_text(text),
                    frequencies=dict(Counter(terms)),
                    length=len(terms),
                )
            )
    return _Document(
        record=record,
        fields=tuple(extracted),
        length=sum(field.length for field in extracted),
    )


def _matches_required(
    document: _Document,
    groups: Sequence[Sequence[str]],
) -> tuple[bool, tuple[tuple[str, ...], ...]]:
    matched: list[tuple[str, ...]] = []
    for group in groups:
        phrases = tuple(
            phrase
            for phrase in group
            if any(
                phrase in field.normalized_phrase_text for field in document.fields
            )
        )
        if not phrases:
            return False, ()
        matched.append(phrases)
    return True, tuple(matched)


def _term_frequency(
    field: _Field,
    term: str,
    options: NormalizedLexicalOptions,
) -> tuple[int, bool]:
    exact = field.frequencies.get(term)
    if exact is not None:
        return exact, False
    if not options.prefix_matching or len(term) < options.prefix_min_chars:
        return 0, False
    frequency = sum(
        count for candidate, count in field.frequencies.items() if candidate.startswith(term)
    )
    return frequency, frequency > 0


def _field_boost(
    path: str,
    boosts: Mapping[str, float],
) -> float:
    boost = 1.0
    match_length = -1
    for field, candidate in boosts.items():
        if path != field and not path.startswith(field + "/"):
            continue
        if len(field) <= match_length:
            continue
        boost = max(0.0, candidate)
        match_length = len(field)
    return boost


def _idf(document_count: int, document_frequency: int) -> float:
    if document_count <= 0 or document_frequency <= 0:
        return 0.0
    return _f32(
        math.log(
            1.0
            + (
                (document_count - document_frequency + 0.5)
                / (document_frequency + 0.5)
            )
        )
    )


def _is_metadata_field(path: str) -> bool:
    return (
        path.startswith("/metadata")
        or path.startswith("/sources")
        or path == "/id"
        or path.startswith("/id/")
    )


def score_many(
    records: Sequence[VyralRecord],
    query: str,
    options: LexicalSearchOptions,
) -> tuple[LexicalScore, ...]:
    if not query.strip():
        raise QueryValidationError("Lexical search query is required.")
    normalized = normalize_options(options)
    parts = _query_parts(query)
    terms = _query_terms(parts.tokens)
    documents = tuple(_document(record, normalized.fields) for record in records)
    documents = tuple(
        document
        for document in documents
        if _matches_required(document, normalized.required_phrase_groups)[0]
    )
    document_count = len(documents)
    average_length = (
        sum(document.length for document in documents) / document_count
        if document_count
        else 0.0
    )
    frequencies = {
        term: sum(
            1
            for document in documents
            if any(
                _term_frequency(field, term, normalized)[0] > 0
                for field in document.fields
            )
        )
        for term in terms
    }
    term_idf = {term: _idf(document_count, count) for term, count in frequencies.items()}

    raw: list[dict[str, Any]] = []
    for document in documents:
        matched_terms: set[str] = set()
        matched_fields: set[str] = set()
        matched_phrases: set[str] = set()
        matched_prefix_terms: set[str] = set()
        term_scores: dict[str, float] = {}
        total_hits = 0.0
        phrase_matched = False
        exact_matched = False
        metadata_matched = False
        max_field_boost = 1.0

        for field in document.fields:
            field_matched = False
            max_field_boost = max(
                max_field_boost,
                _field_boost(field.path, normalized.field_boosts),
            )
            for term in terms:
                frequency, prefix = _term_frequency(field, term, normalized)
                if frequency <= 0:
                    continue
                matched_terms.add(term)
                if prefix:
                    matched_prefix_terms.add(term)
                total_hits += frequency
                field_matched = True
            for phrase in parts.phrases:
                if phrase in field.normalized_text:
                    phrase_matched = True
                    matched_phrases.add(phrase)
                    field_matched = True
            if parts.normalized_text and parts.normalized_text in field.normalized_text:
                phrase_matched = True
                field_matched = True
            if (
                parts.normalized_text
                and field.normalized_text == parts.normalized_text
            ) or any(field.normalized_text == phrase for phrase in parts.phrases):
                exact_matched = True
                field_matched = True
            if field_matched:
                matched_fields.add(field.path)
                metadata_matched = metadata_matched or _is_metadata_field(field.path)

        raw_score = 0.0
        for term in terms:
            weighted_frequency = sum(
                _term_frequency(field, term, normalized)[0]
                * _field_boost(field.path, normalized.field_boosts)
                for field in document.fields
            )
            if weighted_frequency <= 0:
                continue
            if normalized.scoring == "coverage":
                term_score = weighted_frequency
            else:
                length_ratio = max(1, document.length) / max(
                    1.0,
                    average_length if average_length > 0 else max(1, document.length),
                )
                denominator = weighted_frequency + normalized.bm25_k1 * (
                    1 - normalized.bm25_b + normalized.bm25_b * length_ratio
                )
                term_score = (
                    0.0
                    if denominator <= 0
                    else term_idf[term]
                    * (
                        (weighted_frequency * (normalized.bm25_k1 + 1))
                        / denominator
                    )
                )
            term_score = _f32(term_score)
            term_scores[term] = term_score
            raw_score = _f32(raw_score + term_score)

        coverage = _f32(len(matched_terms) / len(terms)) if terms else 0.0
        frequency_score = (
            _f32(min(1.0, total_hits / len(terms))) if terms else 0.0
        )
        if normalized.scoring == "coverage":
            raw_score = _f32(0.8 * coverage + 0.2 * frequency_score)
        phrase_boost = _f32(normalized.phrase_boost if phrase_matched else 0.0)
        exact_boost = _f32(normalized.exact_boost if exact_matched else 0.0)
        metadata_boost = _f32(
            normalized.metadata_boost
            if metadata_matched
            and (matched_terms or phrase_matched or exact_matched)
            else 0.0
        )
        _, matched_groups = _matches_required(
            document,
            normalized.required_phrase_groups,
        )
        raw.append(
            {
                "document": document,
                "raw_score": raw_score,
                "coverage": coverage,
                "frequency_score": frequency_score,
                "phrase_boost": phrase_boost,
                "exact_boost": exact_boost,
                "metadata_boost": metadata_boost,
                "max_field_boost": _f32(max_field_boost),
                "matched_fields": tuple(sorted(matched_fields)),
                "matched_terms": tuple(sorted(matched_terms)),
                "matched_phrases": tuple(sorted(matched_phrases)),
                "matched_prefix_terms": tuple(sorted(matched_prefix_terms)),
                "matched_groups": matched_groups,
                "term_scores": dict(sorted(term_scores.items())),
            }
        )

    max_raw = max((float(item["raw_score"]) for item in raw), default=0.0)
    results: list[LexicalScore] = []
    for item in raw:
        matched_terms_result = cast(tuple[str, ...], item["matched_terms"])
        if normalized.match_mode == "all" and len(matched_terms_result) < len(terms):
            continue
        raw_score = float(item["raw_score"])
        if raw_score <= 0:
            base_score = 0.0
        elif normalized.scoring == "coverage":
            base_score = _f32(min(1.0, max(0.0, raw_score)))
        else:
            base_score = (
                0.0
                if max_raw <= 0
                else _f32(min(1.0, max(0.0, raw_score / max_raw)))
            )
        final = _f32(
            min(
                1.0,
                base_score
                + float(item["phrase_boost"])
                + float(item["exact_boost"])
                + float(item["metadata_boost"]),
            )
        )
        document = item["document"]
        results.append(
            LexicalScore(
                record=document.record,
                score=final,
                base_score=base_score,
                raw_score=raw_score,
                term_coverage=float(item["coverage"]),
                frequency_score=float(item["frequency_score"]),
                phrase_boost=float(item["phrase_boost"]),
                exact_boost=float(item["exact_boost"]),
                metadata_boost=float(item["metadata_boost"]),
                max_field_boost=float(item["max_field_boost"]),
                document_length=document.length,
                average_document_length=average_length,
                corpus_document_count=document_count,
                matched_fields=item["matched_fields"],
                matched_terms=matched_terms_result,
                matched_phrases=item["matched_phrases"],
                matched_prefix_terms=item["matched_prefix_terms"],
                matched_required_phrase_groups=item["matched_groups"],
                fields=normalized.fields,
                term_idf=dict(sorted(term_idf.items())),
                term_scores=item["term_scores"],
                field_boosts=dict(sorted(normalized.field_boosts.items())),
                scoring=normalized.scoring,
                match_mode=normalized.match_mode,
                prefix_matching=normalized.prefix_matching,
                prefix_min_chars=normalized.prefix_min_chars,
            )
        )
    return tuple(results)


def build_fts_expression(
    query: str,
    options: LexicalSearchOptions,
) -> tuple[str, tuple[tuple[str, ...], ...]]:
    normalized = normalize_options(options)
    balanced = query.count('"') > 0 and query.count('"') % 2 == 0
    phrases = tuple(
        dict.fromkeys(
            f'"{" ".join(tokens)}"'
            for tokens in (tokenize(phrase) for phrase in (_quoted_phrases(query) if balanced else ()))
            if tokens
        )
    )
    if balanced:
        inside = False
        residual_characters: list[str] = []
        for character in query:
            if character == '"':
                inside = not inside
                residual_characters.append(" ")
            else:
                residual_characters.append(" " if inside else character)
        residual = "".join(residual_characters)
    else:
        residual = query
    residual_tokens = tokenize(residual)
    tokens = (
        tokenize(query)
        if not residual_tokens and not phrases
        else residual_tokens
    )
    terms = _query_terms(tokens)
    term_expressions = tuple(
        (
            term + "*"
            if normalized.prefix_matching and len(term) >= normalized.prefix_min_chars
            else f'"{term}"'
        )
        for term in terms
    )
    expressions = tuple(dict.fromkeys((*phrases, *term_expressions)))
    separator = " AND " if normalized.match_mode == "all" else " OR "
    query_expression = separator.join(expressions)
    required_expressions: list[str] = []
    for group in normalized.required_phrase_groups:
        entries = [f'"{" ".join(tokenize(phrase))}"' for phrase in group]
        required_expressions.append(
            entries[0] if len(entries) == 1 else "(" + " OR ".join(entries) + ")"
        )
    if not query_expression:
        return " AND ".join(required_expressions), normalized.required_phrase_groups
    if not required_expressions:
        return query_expression, normalized.required_phrase_groups
    return (
        "(" + query_expression + ") AND " + " AND ".join(required_expressions),
        normalized.required_phrase_groups,
    )
