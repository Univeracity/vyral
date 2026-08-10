from __future__ import annotations

from typing import Mapping, Any

from .models import (
    EmbeddingProviderOptions,
    EmbeddingRequest,
    EmbeddingResponse,
    EmbeddingResult,
    PreparedEmbeddingText,
)
from .providers import EmbeddingProvider, EmbeddingProviderRegistry


MAX_EMBEDDING_BATCH_SIZE = 128
MAX_EMBEDDING_TEXT_LENGTH = 100_000
_PURPOSES = frozenset({"query", "passage", "document", "symmetric"})


def normalize_embedding_purpose(purpose: str | None) -> str:
    if purpose is None or not purpose.strip():
        return "symmetric"
    normalized = purpose.strip().lower()
    if normalized not in _PURPOSES:
        raise ValueError(
            "Embedding purpose must be 'query', 'passage', 'document', or 'symmetric'."
        )
    return normalized


def prepare_embedding_text(
    text: str,
    purpose: str | None,
    query_prefix: str | None,
    passage_prefix: str | None,
    symmetric_prefix: str | None,
) -> PreparedEmbeddingText:
    normalized = normalize_embedding_purpose(purpose)
    prefix = {
        "query": query_prefix,
        "passage": passage_prefix,
        "document": passage_prefix,
        "symmetric": symmetric_prefix,
    }[normalized]
    selected = prefix if prefix else None
    applied = selected is not None and not text.startswith(selected)
    return PreparedEmbeddingText(
        original_text=text,
        prepared_text=(selected + text) if applied and selected is not None else text,
        purpose=normalized,
        prefix_applied=applied,
        prefix_length=len(selected) if selected is not None else 0,
    )


class EmbeddingService:
    def __init__(
        self,
        provider: EmbeddingProvider | None = None,
        *,
        provider_options: EmbeddingProviderOptions | Mapping[str, Any] | None = None,
        registry: EmbeddingProviderRegistry | None = None,
    ) -> None:
        self.options = EmbeddingProviderOptions.from_value(provider_options)
        self.provider = provider or (registry or EmbeddingProviderRegistry()).create(
            self.options
        )

    def embed(
        self,
        request: EmbeddingRequest | Mapping[str, Any],
    ) -> EmbeddingResponse:
        normalized = EmbeddingRequest.from_value(request)
        texts: list[str] = []
        if normalized.text is not None:
            texts.append(normalized.text)
        if normalized.texts is not None:
            texts.extend(normalized.texts)
        _validate_texts(texts)
        purpose = normalize_embedding_purpose(normalized.purpose)
        results: list[EmbeddingResult] = []
        for index, text in enumerate(texts):
            prepared = prepare_embedding_text(
                text,
                purpose,
                normalized.query_prefix or self.options.query_prefix,
                normalized.passage_prefix or self.options.passage_prefix,
                normalized.symmetric_prefix or self.options.symmetric_prefix,
            )
            values = self.provider.generate_embedding(prepared.prepared_text)
            if len(values) != self.provider.dimensions:
                raise ValueError(
                    f"Embedding provider returned {len(values)} dimensions, "
                    f"expected {self.provider.dimensions}."
                )
            results.append(
                EmbeddingResult(
                    index=index,
                    text_length=len(text),
                    prepared_text_length=len(prepared.prepared_text),
                    prefix_applied=prepared.prefix_applied,
                    prefix_length=prepared.prefix_length,
                    values=values,
                )
            )
        return EmbeddingResponse(
            provider=self.provider.provider_id,
            model_id=self.provider.model_id,
            dimensions=self.provider.dimensions,
            purpose=purpose,
            items=tuple(results),
        )

    async def aembed(
        self,
        request: EmbeddingRequest | Mapping[str, Any],
    ) -> EmbeddingResponse:
        normalized = EmbeddingRequest.from_value(request)
        texts: list[str] = []
        if normalized.text is not None:
            texts.append(normalized.text)
        if normalized.texts is not None:
            texts.extend(normalized.texts)
        _validate_texts(texts)
        purpose = normalize_embedding_purpose(normalized.purpose)
        results: list[EmbeddingResult] = []
        for index, text in enumerate(texts):
            prepared = prepare_embedding_text(
                text,
                purpose,
                normalized.query_prefix or self.options.query_prefix,
                normalized.passage_prefix or self.options.passage_prefix,
                normalized.symmetric_prefix or self.options.symmetric_prefix,
            )
            values = await self.provider.agenerate_embedding(prepared.prepared_text)
            if len(values) != self.provider.dimensions:
                raise ValueError(
                    f"Embedding provider returned {len(values)} dimensions, "
                    f"expected {self.provider.dimensions}."
                )
            results.append(
                EmbeddingResult(
                    index=index,
                    text_length=len(text),
                    prepared_text_length=len(prepared.prepared_text),
                    prefix_applied=prepared.prefix_applied,
                    prefix_length=prepared.prefix_length,
                    values=values,
                )
            )
        return EmbeddingResponse(
            provider=self.provider.provider_id,
            model_id=self.provider.model_id,
            dimensions=self.provider.dimensions,
            purpose=purpose,
            items=tuple(results),
        )


def _validate_texts(texts: list[str]) -> None:
    if not texts:
        raise ValueError("Embedding request must include text or texts.")
    if len(texts) > MAX_EMBEDDING_BATCH_SIZE:
        raise ValueError(
            f"Embedding request supports at most {MAX_EMBEDDING_BATCH_SIZE} texts."
        )
    for text in texts:
        if not text.strip():
            raise ValueError("Embedding request text values cannot be empty.")
        if len(text) > MAX_EMBEDDING_TEXT_LENGTH:
            raise ValueError(
                "Embedding request text values cannot exceed "
                f"{MAX_EMBEDDING_TEXT_LENGTH} characters."
            )
