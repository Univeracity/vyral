from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from ..local.models import JSONObject


@dataclass(frozen=True)
class EmbeddingProviderOptions:
    provider: str = "local-token-hash"
    model_id: str | None = None
    dimensions: int | None = None
    query_prefix: str | None = None
    passage_prefix: str | None = None
    symmetric_prefix: str | None = None
    additional_properties: JSONObject | None = None

    @classmethod
    def from_value(
        cls,
        value: EmbeddingProviderOptions | Mapping[str, Any] | None,
    ) -> EmbeddingProviderOptions:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("embedding provider options must be an object")
        known = {
            "provider",
            "modelId",
            "dimensions",
            "queryPrefix",
            "passagePrefix",
            "symmetricPrefix",
        }
        provider = value.get("provider", "local-token-hash")
        model_id = value.get("modelId")
        dimensions = value.get("dimensions")
        prefixes = (
            value.get("queryPrefix"),
            value.get("passagePrefix"),
            value.get("symmetricPrefix"),
        )
        if not isinstance(provider, str):
            raise TypeError("embedding provider must be a string")
        if model_id is not None and not isinstance(model_id, str):
            raise TypeError("embedding modelId must be a string")
        if dimensions is not None and (
            isinstance(dimensions, bool) or not isinstance(dimensions, int)
        ):
            raise TypeError("embedding dimensions must be an integer")
        if any(prefix is not None and not isinstance(prefix, str) for prefix in prefixes):
            raise TypeError("embedding prefixes must be strings")
        return cls(
            provider=provider,
            model_id=model_id,
            dimensions=dimensions,
            query_prefix=prefixes[0],
            passage_prefix=prefixes[1],
            symmetric_prefix=prefixes[2],
            additional_properties={
                str(key): item for key, item in value.items() if key not in known
            }
            or None,
        )

    def to_dict(self) -> JSONObject:
        value: JSONObject = {
            "provider": self.provider,
            "modelId": self.model_id,
            "dimensions": self.dimensions,
            "queryPrefix": self.query_prefix,
            "passagePrefix": self.passage_prefix,
            "symmetricPrefix": self.symmetric_prefix,
        }
        value.update(self.additional_properties or {})
        return value


@dataclass(frozen=True)
class EmbeddingProviderDescriptor:
    provider: str
    display_name: str
    description: str
    default_model_id: str
    default_dimensions: int
    local: bool
    cpu_only: bool
    requires_network: bool
    semantic_quality: str

    def to_dict(self) -> JSONObject:
        return {
            "provider": self.provider,
            "displayName": self.display_name,
            "description": self.description,
            "defaultModelId": self.default_model_id,
            "defaultDimensions": self.default_dimensions,
            "local": self.local,
            "cpuOnly": self.cpu_only,
            "requiresNetwork": self.requires_network,
            "semanticQuality": self.semantic_quality,
            "defaultQueryPrefix": None,
            "defaultPassagePrefix": None,
            "defaultSymmetricPrefix": None,
        }


@dataclass(frozen=True)
class EmbeddingRequest:
    text: str | None = None
    texts: tuple[str, ...] | None = None
    purpose: str | None = None
    query_prefix: str | None = None
    passage_prefix: str | None = None
    symmetric_prefix: str | None = None

    @classmethod
    def from_value(
        cls,
        value: EmbeddingRequest | Mapping[str, Any],
    ) -> EmbeddingRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("embedding request must be an object")
        text = value.get("text")
        raw_texts = value.get("texts")
        purpose = value.get("purpose")
        prefixes = (
            value.get("queryPrefix"),
            value.get("passagePrefix"),
            value.get("symmetricPrefix"),
        )
        if text is not None and not isinstance(text, str):
            raise TypeError("embedding text must be a string")
        if raw_texts is not None and (
            not isinstance(raw_texts, (list, tuple))
            or not all(isinstance(item, str) for item in raw_texts)
        ):
            raise TypeError("embedding texts must be an array of strings")
        if purpose is not None and not isinstance(purpose, str):
            raise TypeError("embedding purpose must be a string")
        if any(prefix is not None and not isinstance(prefix, str) for prefix in prefixes):
            raise TypeError("embedding request prefixes must be strings")
        return cls(
            text=text,
            texts=tuple(raw_texts) if raw_texts is not None else None,
            purpose=purpose,
            query_prefix=prefixes[0],
            passage_prefix=prefixes[1],
            symmetric_prefix=prefixes[2],
        )

    def to_dict(self) -> JSONObject:
        return {
            "text": self.text,
            "texts": list(self.texts) if self.texts is not None else None,
            "purpose": self.purpose,
            "queryPrefix": self.query_prefix,
            "passagePrefix": self.passage_prefix,
            "symmetricPrefix": self.symmetric_prefix,
        }


@dataclass(frozen=True)
class PreparedEmbeddingText:
    original_text: str
    prepared_text: str
    purpose: str
    prefix_applied: bool
    prefix_length: int


@dataclass(frozen=True)
class EmbeddingResult:
    index: int
    text_length: int
    prepared_text_length: int
    prefix_applied: bool
    prefix_length: int
    values: tuple[float, ...]

    def to_dict(self) -> JSONObject:
        return {
            "index": self.index,
            "textLength": self.text_length,
            "preparedTextLength": self.prepared_text_length,
            "prefixApplied": self.prefix_applied,
            "prefixLength": self.prefix_length,
            "values": list(self.values),
        }


@dataclass(frozen=True)
class EmbeddingResponse:
    provider: str
    model_id: str
    dimensions: int
    purpose: str
    items: tuple[EmbeddingResult, ...]

    def to_dict(self) -> JSONObject:
        return {
            "provider": self.provider,
            "modelId": self.model_id,
            "dimensions": self.dimensions,
            "purpose": self.purpose,
            "items": [item.to_dict() for item in self.items],
        }
