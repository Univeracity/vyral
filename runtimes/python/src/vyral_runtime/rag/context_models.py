from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Mapping

from ..local import RetrievalDiagnostics, VyralRecord
from ..local.models import JSONObject
from ..retrieval import RetrievalRequest
from ..graph import VyralGraphTraversalProfile
from .models import _boolean, _integer, _optional_text, _text


def _optional_integer(value: object, name: str) -> int | None:
    if value is None:
        return None
    return _integer(value, name, 0)


@dataclass(frozen=True)
class RagContextGraphExpansionOptions:
    enabled: bool = True
    collection: str = ""
    graph_id: str | None = None
    namespace: str | None = None
    tenant_id: str | None = None
    partition_key: str | None = None
    seed_node_ids: tuple[str, ...] = ()
    seed_json_pointers: tuple[str, ...] = (
        "/metadata/graphNodeId",
        "/metadata/nodeId",
        "/metadata/graphNodeIds",
        "/id",
    )
    max_seed_nodes: int = 16
    profile: VyralGraphTraversalProfile = VyralGraphTraversalProfile()
    max_records: int | None = None
    allow_partial_graph: bool = False
    include_graph_context_text: bool = True
    max_graph_context_chars: int = 1200
    include_graph_provenance: bool = True
    max_graph_provenance_items: int = 64
    fallback_on_failure: bool = True

    @classmethod
    def from_value(
        cls,
        value: RagContextGraphExpansionOptions | Mapping[str, Any] | None,
    ) -> RagContextGraphExpansionOptions | None:
        if value is None or isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG context graphExpansion must be an object")

        def strings(
            name: str, default: tuple[str, ...] = ()
        ) -> tuple[str, ...]:
            raw = value.get(name)
            if raw is None:
                return default
            if not isinstance(raw, (list, tuple)) or not all(
                isinstance(item, str) for item in raw
            ):
                raise TypeError(
                    f"RAG graphExpansion {name} must be an array of strings"
                )
            return tuple(raw)

        return cls(
            enabled=_boolean(
                value.get("enabled"), "RAG graphExpansion enabled", True
            ),
            collection=_text(
                value.get("collection"),
                "RAG graphExpansion collection",
                "",
            ),
            graph_id=_optional_text(
                value.get("graphId"), "RAG graphExpansion graphId"
            ),
            namespace=_optional_text(
                value.get("namespace"), "RAG graphExpansion namespace"
            ),
            tenant_id=_optional_text(
                value.get("tenantId"), "RAG graphExpansion tenantId"
            ),
            partition_key=_optional_text(
                value.get("partitionKey"), "RAG graphExpansion partitionKey"
            ),
            seed_node_ids=strings("seedNodeIds"),
            seed_json_pointers=strings(
                "seedJsonPointers", cls().seed_json_pointers
            ),
            max_seed_nodes=_integer(
                value.get("maxSeedNodes"),
                "RAG graphExpansion maxSeedNodes",
                16,
            ),
            profile=VyralGraphTraversalProfile.from_value(value.get("profile")),
            max_records=_optional_integer(
                value.get("maxRecords"), "RAG graphExpansion maxRecords"
            ),
            allow_partial_graph=_boolean(
                value.get("allowPartialGraph"),
                "RAG graphExpansion allowPartialGraph",
                False,
            ),
            include_graph_context_text=_boolean(
                value.get("includeGraphContextText"),
                "RAG graphExpansion includeGraphContextText",
                True,
            ),
            max_graph_context_chars=_integer(
                value.get("maxGraphContextChars"),
                "RAG graphExpansion maxGraphContextChars",
                1200,
            ),
            include_graph_provenance=_boolean(
                value.get("includeGraphProvenance"),
                "RAG graphExpansion includeGraphProvenance",
                True,
            ),
            max_graph_provenance_items=_integer(
                value.get("maxGraphProvenanceItems"),
                "RAG graphExpansion maxGraphProvenanceItems",
                64,
            ),
            fallback_on_failure=_boolean(
                value.get("fallbackOnFailure"),
                "RAG graphExpansion fallbackOnFailure",
                True,
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "enabled": self.enabled,
            "collection": self.collection,
            "graphId": self.graph_id,
            "namespace": self.namespace,
            "tenantId": self.tenant_id,
            "partitionKey": self.partition_key,
            "seedNodeIds": list(self.seed_node_ids),
            "seedJsonPointers": list(self.seed_json_pointers),
            "maxSeedNodes": self.max_seed_nodes,
            "profile": self.profile.to_dict(),
            "maxRecords": self.max_records,
            "allowPartialGraph": self.allow_partial_graph,
            "includeGraphContextText": self.include_graph_context_text,
            "maxGraphContextChars": self.max_graph_context_chars,
            "includeGraphProvenance": self.include_graph_provenance,
            "maxGraphProvenanceItems": self.max_graph_provenance_items,
            "fallbackOnFailure": self.fallback_on_failure,
        }


@dataclass(frozen=True)
class RagContextGroupBudget:
    key: str
    priority: int | None = None
    required: bool = False
    min_chunks: int | None = None
    max_chunks: int | None = None
    min_chars: int | None = None
    max_chars: int | None = None

    @classmethod
    def from_value(
        cls,
        value: RagContextGroupBudget | Mapping[str, Any],
    ) -> RagContextGroupBudget:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG context group budget must be an object")
        return cls(
            key=_text(value.get("key"), "RAG context group key", ""),
            priority=_optional_integer(
                value.get("priority"),
                "RAG context group priority",
            ),
            required=_boolean(
                value.get("required"),
                "RAG context group required",
                False,
            ),
            min_chunks=_optional_integer(
                value.get("minChunks"),
                "RAG context group minChunks",
            ),
            max_chunks=_optional_integer(
                value.get("maxChunks"),
                "RAG context group maxChunks",
            ),
            min_chars=_optional_integer(
                value.get("minChars"),
                "RAG context group minChars",
            ),
            max_chars=_optional_integer(
                value.get("maxChars"),
                "RAG context group maxChars",
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "key": self.key,
            "priority": self.priority,
            "required": self.required,
            "minChunks": self.min_chunks,
            "maxChunks": self.max_chunks,
            "minChars": self.min_chars,
            "maxChars": self.max_chars,
        }


@dataclass(frozen=True)
class RagContextAssemblyOptions:
    group_by: str | None = None
    group_by_path: str | None = None
    default_max_chunks_per_group: int | None = None
    default_max_chars_per_group: int | None = None
    fail_on_unsatisfied_required_groups: bool = False
    groups: tuple[RagContextGroupBudget, ...] = ()

    @classmethod
    def from_value(
        cls,
        value: RagContextAssemblyOptions | Mapping[str, Any] | None,
    ) -> RagContextAssemblyOptions | None:
        if value is None or isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG context assembly must be an object")
        raw_groups = value.get("groups", [])
        if not isinstance(raw_groups, (list, tuple)):
            raise TypeError("RAG context assembly groups must be an array")
        return cls(
            group_by=_optional_text(
                value.get("groupBy"),
                "RAG context assembly groupBy",
            ),
            group_by_path=_optional_text(
                value.get("groupByPath"),
                "RAG context assembly groupByPath",
            ),
            default_max_chunks_per_group=_optional_integer(
                value.get("defaultMaxChunksPerGroup"),
                "RAG context assembly defaultMaxChunksPerGroup",
            ),
            default_max_chars_per_group=_optional_integer(
                value.get("defaultMaxCharsPerGroup"),
                "RAG context assembly defaultMaxCharsPerGroup",
            ),
            fail_on_unsatisfied_required_groups=_boolean(
                value.get("failOnUnsatisfiedRequiredGroups"),
                "RAG context assembly failOnUnsatisfiedRequiredGroups",
                False,
            ),
            groups=tuple(RagContextGroupBudget.from_value(item) for item in raw_groups),
        )

    def to_dict(self) -> JSONObject:
        return {
            "groupBy": self.group_by,
            "groupByPath": self.group_by_path,
            "defaultMaxChunksPerGroup": self.default_max_chunks_per_group,
            "defaultMaxCharsPerGroup": self.default_max_chars_per_group,
            "failOnUnsatisfiedRequiredGroups": (
                self.fail_on_unsatisfied_required_groups
            ),
            "groups": [group.to_dict() for group in self.groups],
        }


@dataclass(frozen=True)
class RagContextRequest:
    retrieval: RetrievalRequest
    content_field: str = "text"
    max_chars: int = 8000
    max_chars_per_chunk: int = 1200
    max_citations_per_chunk: int | None = None
    context_assembly: RagContextAssemblyOptions | None = None
    graph_expansion: RagContextGraphExpansionOptions | None = None
    include_records: bool = False
    include_citations: bool = True
    include_context_text: bool = False
    include_trace: bool = False

    @classmethod
    def from_value(
        cls,
        value: RagContextRequest | Mapping[str, Any],
    ) -> RagContextRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG context request must be an object")
        retrieval = value.get("retrieval")
        return cls(
            retrieval=RetrievalRequest.from_value(
                retrieval if isinstance(retrieval, Mapping) else {}
            ),
            content_field=_text(
                value.get("contentField"),
                "RAG context contentField",
                "text",
            ),
            max_chars=_integer(value.get("maxChars"), "RAG context maxChars", 8000),
            max_chars_per_chunk=_integer(
                value.get("maxCharsPerChunk"),
                "RAG context maxCharsPerChunk",
                1200,
            ),
            max_citations_per_chunk=_optional_integer(
                value.get("maxCitationsPerChunk"),
                "RAG context maxCitationsPerChunk",
            ),
            context_assembly=RagContextAssemblyOptions.from_value(
                value.get("contextAssembly")
            ),
            graph_expansion=RagContextGraphExpansionOptions.from_value(
                value.get("graphExpansion")
            ),
            include_records=_boolean(
                value.get("includeRecords"),
                "RAG context includeRecords",
                False,
            ),
            include_citations=_boolean(
                value.get("includeCitations"),
                "RAG context includeCitations",
                True,
            ),
            include_context_text=_boolean(
                value.get("includeContextText"),
                "RAG context includeContextText",
                False,
            ),
            include_trace=_boolean(
                value.get("includeTrace"),
                "RAG context includeTrace",
                False,
            ),
        )

    def to_dict(self) -> JSONObject:
        output: JSONObject = {
            "retrieval": self.retrieval.to_dict(),
            "contentField": self.content_field,
            "maxChars": self.max_chars,
            "maxCharsPerChunk": self.max_chars_per_chunk,
            "maxCitationsPerChunk": self.max_citations_per_chunk,
            "includeRecords": self.include_records,
            "includeCitations": self.include_citations,
            "includeContextText": self.include_context_text,
            "includeTrace": self.include_trace,
        }
        if self.context_assembly is not None:
            output["contextAssembly"] = self.context_assembly.to_dict()
        if self.graph_expansion is not None:
            output["graphExpansion"] = self.graph_expansion.to_dict()
        return output


@dataclass(frozen=True)
class RagContextRetrievalMatch:
    rank: int
    score: float
    collection: str
    search_mode: str
    snippet: str | None

    def to_dict(self) -> JSONObject:
        output: JSONObject = {
            "rank": self.rank,
            "score": self.score,
            "collection": self.collection,
            "searchMode": self.search_mode,
            "snippet": self.snippet,
        }
        return output


@dataclass(frozen=True)
class RagContextCitation:
    id: str
    chunk_rank: int
    collection: str
    partition_key: str
    record_id: str
    source_id: str | None
    source_kind: str | None
    source_uri: str | None
    source_label: str | None
    source_span: JSONObject | None
    included_source_span: JSONObject | None
    context_char_start: int
    context_char_end: int
    context_excerpt_hash: str

    def to_dict(self) -> JSONObject:
        output: JSONObject = {
            "id": self.id,
            "chunkRank": self.chunk_rank,
            "collection": self.collection,
            "partitionKey": self.partition_key,
            "recordId": self.record_id,
            "sourceId": self.source_id,
            "sourceKind": self.source_kind,
            "sourceUri": self.source_uri,
            "sourceLabel": self.source_label,
            "sourceSpan": self.source_span,
            "includedSourceSpan": self.included_source_span,
            "contextCharStart": self.context_char_start,
            "contextCharEnd": self.context_char_end,
            "contextExcerptHash": self.context_excerpt_hash,
        }
        return output


@dataclass(frozen=True)
class RagContextChunk:
    rank: int
    score: float
    collection: str
    partition_key: str
    id: str
    text: str
    content_field: str
    group_key: str | None
    char_start: int
    char_end: int
    original_text_length: int
    truncated: bool
    context_excerpt_hash: str
    citation_ids: tuple[str, ...] = ()
    retrieval_diagnostics: RetrievalDiagnostics | None = None
    retrieval_match: RagContextRetrievalMatch | None = None
    metadata: JSONObject | None = None
    sources: tuple[JSONObject, ...] | None = None
    record: VyralRecord | None = None

    def to_dict(self) -> JSONObject:
        output: JSONObject = {
            "rank": self.rank,
            "score": self.score,
            "collection": self.collection,
            "partitionKey": self.partition_key,
            "id": self.id,
            "text": self.text,
            "contentField": self.content_field,
            "groupKey": self.group_key,
            "charStart": self.char_start,
            "charEnd": self.char_end,
            "originalTextLength": self.original_text_length,
            "truncated": self.truncated,
            "contextExcerptHash": self.context_excerpt_hash,
            "citationIds": list(self.citation_ids),
            "metadata": self.metadata,
            "sources": list(self.sources) if self.sources is not None else None,
        }
        if self.retrieval_diagnostics is not None:
            output["retrievalDiagnostics"] = (
                self.retrieval_diagnostics.to_dict()
            )
        if self.retrieval_match is not None:
            output["retrievalMatch"] = self.retrieval_match.to_dict()
        if self.record is not None:
            output["record"] = self.record.to_dict()
        return output


@dataclass(frozen=True)
class RagContextEnvelope:
    query: str
    chunks: tuple[RagContextChunk, ...]
    citations: tuple[RagContextCitation, ...]
    total_chars: int
    omitted_citation_count: int
    context_text: str | None = None
    context_text_format: str | None = None
    context_text_hash: str | None = None
    graph_context: JSONObject | None = None
    graph_expansion: JSONObject | None = None
    trace: JSONObject | None = None

    def to_dict(self) -> JSONObject:
        return {
            "query": self.query,
            "chunks": [chunk.to_dict() for chunk in self.chunks],
            "citations": [citation.to_dict() for citation in self.citations],
            "totalChars": self.total_chars,
            "omittedCitationCount": self.omitted_citation_count,
            "contextText": self.context_text,
            "contextTextFormat": self.context_text_format,
            "contextTextHash": self.context_text_hash,
            "graphContext": self.graph_context,
            "graphExpansion": self.graph_expansion,
            "trace": self.trace,
        }


@dataclass(frozen=True)
class RagPromptTemplateOptions:
    format: str = "chat-markdown"
    system_instruction: str | None = None
    user_instruction: str | None = None
    citation_instruction: str | None = None
    include_citation_instruction: bool = True
    fail_on_empty_context: bool = False
    max_prompt_chars: int | None = None

    @classmethod
    def from_value(
        cls,
        value: RagPromptTemplateOptions | Mapping[str, Any] | None,
    ) -> RagPromptTemplateOptions:
        if value is None:
            return cls()
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG prompt template must be an object")
        return cls(
            format=_text(
                value.get("format"),
                "RAG prompt template format",
                "chat-markdown",
            ),
            system_instruction=_optional_text(
                value.get("systemInstruction"),
                "RAG prompt systemInstruction",
            ),
            user_instruction=_optional_text(
                value.get("userInstruction"),
                "RAG prompt userInstruction",
            ),
            citation_instruction=_optional_text(
                value.get("citationInstruction"),
                "RAG prompt citationInstruction",
            ),
            include_citation_instruction=_boolean(
                value.get("includeCitationInstruction"),
                "RAG prompt includeCitationInstruction",
                True,
            ),
            fail_on_empty_context=_boolean(
                value.get("failOnEmptyContext"),
                "RAG prompt failOnEmptyContext",
                False,
            ),
            max_prompt_chars=_optional_integer(
                value.get("maxPromptChars"),
                "RAG prompt maxPromptChars",
            ),
        )

    def to_dict(self) -> JSONObject:
        return {
            "format": self.format,
            "systemInstruction": self.system_instruction,
            "userInstruction": self.user_instruction,
            "citationInstruction": self.citation_instruction,
            "includeCitationInstruction": self.include_citation_instruction,
            "failOnEmptyContext": self.fail_on_empty_context,
            "maxPromptChars": self.max_prompt_chars,
        }


@dataclass(frozen=True)
class RagPromptRequest:
    context: RagContextRequest
    template: RagPromptTemplateOptions = field(
        default_factory=RagPromptTemplateOptions
    )

    @classmethod
    def from_value(
        cls,
        value: RagPromptRequest | Mapping[str, Any],
    ) -> RagPromptRequest:
        if isinstance(value, cls):
            return value
        if not isinstance(value, Mapping):
            raise TypeError("RAG prompt request must be an object")
        context = value.get("context")
        return cls(
            context=RagContextRequest.from_value(
                context if isinstance(context, Mapping) else {}
            ),
            template=RagPromptTemplateOptions.from_value(value.get("template")),
        )

    def to_dict(self) -> JSONObject:
        return {
            "context": self.context.to_dict(),
            "template": self.template.to_dict(),
        }


@dataclass(frozen=True)
class RagPromptMessage:
    role: str
    content: str

    def to_dict(self) -> JSONObject:
        return {"role": self.role, "content": self.content}


@dataclass(frozen=True)
class RagPromptEnvelope:
    query: str
    format: str
    prompt: str
    prompt_hash: str
    messages: tuple[RagPromptMessage, ...]
    context: RagContextEnvelope
    trace: JSONObject | None = None

    def to_dict(self) -> JSONObject:
        return {
            "query": self.query,
            "format": self.format,
            "prompt": self.prompt,
            "promptHash": self.prompt_hash,
            "messages": [message.to_dict() for message in self.messages],
            "context": self.context.to_dict(),
            "trace": self.trace,
        }
