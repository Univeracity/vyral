from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import TypeAlias

from .contracts import JSONValue


class Maturity(str, Enum):
    PROTOTYPE = "prototype"
    PREVIEW = "preview"
    PUBLIC = "public"


class RuntimeProfileId(str, Enum):
    CONTRACTS = "vyral.runtime.contracts.v1"
    DATA_RAG = "vyral.runtime.data-rag.v1"
    EXTERNAL_WORKER = "vyral.runtime.external-worker.v1"
    PROVIDERS = "vyral.runtime.providers.v1"
    CANONICAL = "vyral.runtime.canonical.v1"
    RETRIEVAL_GENERATION = "vyral.runtime.retrieval-generation.v1"
    EXECUTION_LOCAL = "vyral.runtime.execution.local.v1"
    REST = "vyral.runtime.rest.v1"
    MCP_STATELESS = "vyral.runtime.mcp.stateless-2026-07-28.v1"
    FULL_LOCAL = "vyral.runtime.full-local.v1"


ProfileTuple: TypeAlias = tuple["ProfileStatus", ...]


@dataclass(frozen=True)
class ProfileStatus:
    profile_id: RuntimeProfileId
    maturity: Maturity
    available: bool
    required_for_full_local: bool
    summary: str

    def to_dict(self) -> dict[str, JSONValue]:
        return {
            "id": self.profile_id.value,
            "maturity": self.maturity.value,
            "available": self.available,
            "requiredForFullLocal": self.required_for_full_local,
            "summary": self.summary,
        }


_PROFILE_STATUSES: ProfileTuple = (
    ProfileStatus(
        RuntimeProfileId.CONTRACTS,
        Maturity.PROTOTYPE,
        True,
        True,
        "Canonical OpenAPI, JSON Schema, SDK catalog, and initial shared goldens load and verify.",
    ),
    ProfileStatus(
        RuntimeProfileId.DATA_RAG,
        Maturity.PROTOTYPE,
        True,
        True,
        "Embedded records, query/search, objects, traces, embeddings, retrieval, "
        "RAG, evaluation, graph, and GraphRAG are implemented. Promotion still "
        "requires the supported Python/operating-system matrix and published-size "
        "qualification.",
    ),
    ProfileStatus(
        RuntimeProfileId.EXTERNAL_WORKER,
        Maturity.PROTOTYPE,
        True,
        True,
        "Python handler authoring, the worker loop, token-safe HTTP transport, "
        "and replay harness are implemented and pass the shared lifecycle "
        "fixture plus live .NET server restart and completion-replay gates; "
        "supported-platform promotion remains incomplete.",
    ),
    ProfileStatus(
        RuntimeProfileId.PROVIDERS,
        Maturity.PROTOTYPE,
        True,
        True,
        "The provider registry, extension seam, and deterministic local providers "
        "are implemented; broader provider qualification remains future work.",
    ),
    ProfileStatus(
        RuntimeProfileId.CANONICAL,
        Maturity.PROTOTYPE,
        True,
        True,
        "The strong SQLite CanonicalStore profile is implemented with atomic "
        "documents, revisions, fences, idempotency, outbox leasing, migrations, "
        "hash-verified snapshots, and byte-identical .NET archive goldens; "
        "supported-platform and multi-provider promotion remain incomplete.",
    ),
    ProfileStatus(
        RuntimeProfileId.RETRIEVAL_GENERATION,
        Maturity.PROTOTYPE,
        True,
        False,
        "Immutable generation descriptors, complete-coverage outcomes, and "
        "generation-pinned candidate-search fixtures are available as an "
        "optional contract experiment; provider lifecycle semantics remain "
        "intentionally unpromoted.",
    ),
    ProfileStatus(
        RuntimeProfileId.EXECUTION_LOCAL,
        Maturity.PROTOTYPE,
        True,
        True,
        "Native Python handlers and the durable SQLite execution runtime implement "
        "runs, retries, cancellation, waits, events, artifacts, checkpoints, "
        "leases, recovery, maintenance, external workers, and built-in jobs.",
    ),
    ProfileStatus(
        RuntimeProfileId.REST,
        Maturity.PROTOTYPE,
        True,
        True,
        "The optional ASGI host derives all 133 routes from OpenAPI and passes the "
        "maintained Python and JavaScript package-consumer programs.",
    ),
    ProfileStatus(
        RuntimeProfileId.MCP_STATELESS,
        Maturity.PROTOTYPE,
        True,
        True,
        "The stateless MCP 2026-07-28 endpoint implements discovery, bounded "
        "resources/tools, header routing, authorization, and durable Tasks; the "
        "pinned official conformance gate passes for its advertised surface.",
    ),
    ProfileStatus(
        RuntimeProfileId.FULL_LOCAL,
        Maturity.PROTOTYPE,
        True,
        False,
        "Every required full-local subsystem is implemented and locally "
        "qualifiable; aggregate preview promotion remains gated on the supported "
        "platform matrix and combined release qualification.",
    ),
)


def profile_statuses() -> ProfileTuple:
    return _PROFILE_STATUSES


def full_local_ready(profiles: ProfileTuple | None = None) -> bool:
    selected = profiles or _PROFILE_STATUSES
    required = tuple(profile for profile in selected if profile.required_for_full_local)
    return bool(required) and all(
        profile.available and profile.maturity in {Maturity.PREVIEW, Maturity.PUBLIC}
        for profile in required
    )
