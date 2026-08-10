"""Deterministic local embedding providers and extension protocols."""

from .models import (
    EmbeddingProviderDescriptor,
    EmbeddingProviderOptions,
    EmbeddingRequest,
    EmbeddingResponse,
    EmbeddingResult,
    PreparedEmbeddingText,
)
from .providers import (
    DeterministicHashEmbeddingProvider,
    EmbeddingProvider,
    EmbeddingProviderFactory,
    EmbeddingProviderRegistry,
    LocalTokenHashEmbeddingProvider,
    local_factories,
)
from .service import (
    MAX_EMBEDDING_BATCH_SIZE,
    MAX_EMBEDDING_TEXT_LENGTH,
    EmbeddingService,
    normalize_embedding_purpose,
    prepare_embedding_text,
)

__all__ = [
    "DeterministicHashEmbeddingProvider",
    "EmbeddingProvider",
    "EmbeddingProviderDescriptor",
    "EmbeddingProviderFactory",
    "EmbeddingProviderOptions",
    "EmbeddingProviderRegistry",
    "EmbeddingRequest",
    "EmbeddingResponse",
    "EmbeddingResult",
    "EmbeddingService",
    "LocalTokenHashEmbeddingProvider",
    "MAX_EMBEDDING_BATCH_SIZE",
    "MAX_EMBEDDING_TEXT_LENGTH",
    "PreparedEmbeddingText",
    "local_factories",
    "normalize_embedding_purpose",
    "prepare_embedding_text",
]
