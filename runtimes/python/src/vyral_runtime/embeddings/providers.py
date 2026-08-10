from __future__ import annotations

from hashlib import sha256
import math
import struct
from typing import Callable, Iterable, Protocol, runtime_checkable

from .models import EmbeddingProviderDescriptor, EmbeddingProviderOptions


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


def _f32(value: float) -> float:
    return float(struct.unpack("<f", struct.pack("<f", value))[0])


@runtime_checkable
class EmbeddingProvider(Protocol):
    provider_id: str
    dimensions: int
    model_id: str

    def generate_embedding(self, text: str) -> tuple[float, ...]: ...

    async def agenerate_embedding(self, text: str) -> tuple[float, ...]: ...


class DeterministicHashEmbeddingProvider:
    provider_id = "deterministic-hash"

    def __init__(
        self,
        dimensions: int = 64,
        model_id: str | None = None,
    ) -> None:
        if dimensions <= 0:
            raise ValueError("Embedding dimensions must be greater than zero.")
        self.dimensions = dimensions
        self.model_id = (
            model_id.strip()
            if model_id is not None and model_id.strip()
            else "deterministic-hash-embedding-v1"
        )

    def generate_embedding(self, text: str) -> tuple[float, ...]:
        if not isinstance(text, str):
            raise TypeError("Embedding text must be a string.")
        digest = sha256(text.encode("utf-8")).digest()
        vector: list[float] = []
        maximum = _f32(float(2_147_483_647))
        for index in range(self.dimensions):
            offset = (index * 4) % len(digest)
            signed = int.from_bytes(
                digest[offset : offset + 4],
                "little",
                signed=True,
            )
            vector.append(_f32(_f32(float(signed)) / maximum))
        magnitude = math.sqrt(
            sum(_f32(value * value) for value in vector)
        )
        if magnitude > 0:
            vector = [_f32(value / magnitude) for value in vector]
        return tuple(vector)

    async def agenerate_embedding(self, text: str) -> tuple[float, ...]:
        return self.generate_embedding(text)


class LocalTokenHashEmbeddingProvider:
    provider_id = "local-token-hash"

    def __init__(
        self,
        dimensions: int = 384,
        model_id: str | None = None,
    ) -> None:
        if dimensions <= 0:
            raise ValueError("Embedding dimensions must be greater than zero.")
        self.dimensions = dimensions
        self.model_id = (
            model_id.strip()
            if model_id is not None and model_id.strip()
            else "local-token-hash-embedding-v1"
        )

    def _add_feature(
        self,
        vector: list[float],
        feature: str,
        weight: float,
    ) -> None:
        digest = sha256(feature.encode("utf-8")).digest()
        first = int.from_bytes(digest[0:4], "little", signed=False)
        second = int.from_bytes(digest[4:8], "little", signed=False)
        index = first % self.dimensions
        sign = 1.0 if second & 1 == 0 else -1.0
        vector[index] = _f32(vector[index] + _f32(sign * weight))

    def generate_embedding(self, text: str) -> tuple[float, ...]:
        if not isinstance(text, str):
            raise TypeError("Embedding text must be a string.")
        vector = [0.0] * self.dimensions
        tokens = _tokenize(text)
        if not tokens:
            self._add_feature(vector, "empty", 1.0)
            return _normalize(vector)
        for index, token in enumerate(tokens):
            token_weight = 0.15 if token in _STOP_WORDS else 1.0
            self._add_feature(vector, "tok:" + token, token_weight)
            if token not in _STOP_WORDS:
                for gram in _character_ngrams(token, 3):
                    self._add_feature(vector, "tri:" + gram, 0.20)
            if index + 1 < len(tokens):
                following = tokens[index + 1]
                if token not in _STOP_WORDS or following not in _STOP_WORDS:
                    self._add_feature(
                        vector,
                        "bi:" + token + " " + following,
                        1.35,
                    )
        return _normalize(vector)

    async def agenerate_embedding(self, text: str) -> tuple[float, ...]:
        return self.generate_embedding(text)


def _tokenize(text: str) -> tuple[str, ...]:
    output: list[str] = []
    builder: list[str] = []
    for character in text:
        if character.isalnum():
            builder.append(character.lower())
        elif builder:
            output.append("".join(builder))
            builder.clear()
    if builder:
        output.append("".join(builder))
    return tuple(output)


def _character_ngrams(token: str, length: int) -> Iterable[str]:
    if len(token) < length:
        yield token
        return
    for index in range(len(token) - length + 1):
        yield token[index : index + length]


def _normalize(vector: list[float]) -> tuple[float, ...]:
    magnitude = math.sqrt(sum(_f32(value * value) for value in vector))
    if magnitude <= 0:
        return tuple(vector)
    scale = 1.0 / magnitude
    return tuple(_f32(value * scale) for value in vector)


class EmbeddingProviderFactory(Protocol):
    descriptor: EmbeddingProviderDescriptor

    def create(self, options: EmbeddingProviderOptions) -> EmbeddingProvider: ...


class _Factory:
    def __init__(
        self,
        descriptor: EmbeddingProviderDescriptor,
        constructor: Callable[[int, str | None], EmbeddingProvider],
    ) -> None:
        self.descriptor = descriptor
        self._constructor = constructor

    def create(self, options: EmbeddingProviderOptions) -> EmbeddingProvider:
        dimensions = options.dimensions or self.descriptor.default_dimensions
        if dimensions <= 0:
            raise ValueError("Embedding dimensions must be greater than zero.")
        return self._constructor(dimensions, options.model_id)


class EmbeddingProviderRegistry:
    def __init__(
        self,
        factories: Iterable[EmbeddingProviderFactory] | None = None,
    ) -> None:
        selected = tuple(factories) if factories is not None else local_factories()
        self._factories: dict[str, EmbeddingProviderFactory] = {}
        for factory in selected:
            provider = factory.descriptor.provider
            if not provider.strip():
                raise ValueError(
                    "Embedding provider factory descriptor provider is required."
                )
            self._factories[provider.lower()] = factory

    def get_providers(self) -> tuple[EmbeddingProviderDescriptor, ...]:
        return tuple(
            sorted(
                (factory.descriptor for factory in self._factories.values()),
                key=lambda descriptor: descriptor.provider.lower(),
            )
        )

    def create(
        self,
        options: EmbeddingProviderOptions | dict[str, object] | None = None,
    ) -> EmbeddingProvider:
        normalized = EmbeddingProviderOptions.from_value(options)
        factory = self._factories.get(normalized.provider.lower())
        if factory is None:
            providers = ", ".join(
                sorted(
                    (item.descriptor.provider for item in self._factories.values()),
                    key=str.lower,
                )
            )
            raise ValueError(
                f"Embedding provider {normalized.provider!r} is not registered. "
                f"Registered providers: {providers}."
            )
        return factory.create(normalized)


def local_factories() -> tuple[EmbeddingProviderFactory, ...]:
    return (
        _Factory(
            EmbeddingProviderDescriptor(
                provider="local-token-hash",
                display_name="Local token hash embeddings",
                description=(
                    "CPU-only model-free lexical embeddings for local RAG development. "
                    "Similar token and phrase overlap produces similar vectors without "
                    "network or model files."
                ),
                default_model_id="local-token-hash-embedding-v1",
                default_dimensions=384,
                local=True,
                cpu_only=True,
                requires_network=False,
                semantic_quality="lexical",
            ),
            LocalTokenHashEmbeddingProvider,
        ),
        _Factory(
            EmbeddingProviderDescriptor(
                provider="deterministic-hash",
                display_name="Deterministic hash embeddings",
                description=(
                    "CPU-only deterministic vectors for local storage, policy, query, "
                    "and integration testing. These vectors are not semantic embeddings."
                ),
                default_model_id="deterministic-hash-embedding-v1",
                default_dimensions=64,
                local=True,
                cpu_only=True,
                requires_network=False,
                semantic_quality="mechanical",
            ),
            DeterministicHashEmbeddingProvider,
        ),
    )
