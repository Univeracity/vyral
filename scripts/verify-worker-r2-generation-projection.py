#!/usr/bin/env python3
"""Build and exercise the consumer-neutral Worker/R2 generation projection proof."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
from tempfile import TemporaryDirectory
from typing import Any, Iterable, Mapping

import jsonschema


ROOT = Path(__file__).resolve().parent.parent
PROJECTION = ROOT / "src" / "Vyral.Cloudflare" / "WorkerR2GenerationProjection"
CONTRACT_SCHEMA = (
    ROOT
    / "src"
    / "Vyral.Abstractions"
    / "contracts"
    / "record-search-projection-generation.v1.schema.json"
)
PARTITION = "public"
BUNDLE_SCHEMA = "vyral.worker-r2-proof-bundle.v1"
REPORT_SCHEMA = "vyral.worker-r2-proof-report.v1"
MANIFEST_SCHEMA = "vyral.private.worker-r2-manifest.v1"
SHARD_SCHEMA = "vyral.private.worker-r2-shard.v1"
CATALOG_SCHEMA = "vyral.private.worker-r2-catalog.v1"
ACTIVE_SCHEMA = "vyral.private.worker-r2-active.v1"


class ProofError(RuntimeError):
    """The Worker/R2 proof could not establish a required invariant."""


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def digest_bytes(value: bytes) -> str:
    return "sha256:" + hashlib.sha256(value).hexdigest()


def digest_json(value: Any) -> str:
    return digest_bytes(canonical_bytes(value))


def object_key(digest: str) -> str:
    return "objects/sha256/" + digest.removeprefix("sha256:") + ".json"


def score_hex(value: float) -> str:
    return struct.pack(">d", value).hex()


def route(identifier: str, shard_count: int) -> int:
    prefix = hashlib.sha256(identifier.encode("utf-8")).digest()[:8]
    return int.from_bytes(prefix, "big") % shard_count


def records(count: int) -> list[dict[str, Any]]:
    categories = ("agents", "execution", "records", "retrieval")
    return [
        {
            "partitionKey": PARTITION,
            "id": f"record-{index:03d}",
            "revision": 1 + (index % 3),
            "tokens": ["portable", categories[index % len(categories)], "evidence"],
            "metadata": {
                "category": categories[index % len(categories)],
                "ordinal": index,
                "published": True,
            },
        }
        for index in range(count)
    ]


def request(query: str, *, limit: int, scan_limit: int) -> dict[str, Any]:
    return {
        "schema": "vyral.record-search-projection-request.v1",
        "generationId": None,
        "expectedDescriptorDigest": None,
        "query": {
            "partitionKeys": [PARTITION],
            "filter": None,
            "vector": None,
            "lexical": {
                "query": query,
                "fields": None,
                "top": limit,
                "scanLimit": scan_limit,
                "minScore": None,
                "scoring": "bm25",
                "matchMode": "any",
                "fieldBoosts": None,
                "bm25K1": 1.2,
                "bm25B": 0.75,
                "phraseBoost": 0.15,
                "exactBoost": 0.25,
                "metadataBoost": 0.1,
                "prefixMatching": False,
                "prefixMinChars": 3,
                "requiredPhraseGroups": None,
            },
            "orderBy": None,
            "limit": limit,
            "continuationToken": None,
        },
        "deadlineUtc": None,
    }


def expected_candidates(
    documents: Iterable[Mapping[str, Any]],
    query: str,
    idf: Mapping[str, float],
    *,
    limit: int,
) -> list[dict[str, Any]]:
    normalized = query.lower().strip()
    direct = [document for document in documents if document["id"] == normalized]
    if direct:
        values = [(1000.0, document) for document in direct]
    else:
        terms = sorted(set(normalized.split()))
        values = []
        for document in documents:
            frequencies = Counter(document["tokens"])
            score = 0.0
            for term in terms:
                frequency = float(frequencies.get(term, 0))
                if frequency == 0:
                    continue
                denominator = frequency + 1.2 * (
                    1 - 0.75 + 0.75 * len(document["tokens"]) / 3.0
                )
                score += idf[term] * frequency * (1.2 + 1) / denominator
            if score > 0:
                values.append((score, document))
    values.sort(key=lambda item: (-item[0], item[1]["partitionKey"], item[1]["id"]))
    return [
        {
            "partitionKey": document["partitionKey"],
            "id": document["id"],
            "revision": document["revision"],
            "scoreHex": score_hex(score),
        }
        for score, document in values[:limit]
    ]


def descriptor_material(descriptor: Mapping[str, Any]) -> dict[str, Any]:
    return {
        key: descriptor[key]
        for key in (
            "schema",
            "collection",
            "generationId",
            "providerId",
            "profileId",
            "strategyVersion",
            "sourceManifestDigest",
            "recordRevisionSetDigest",
            "projectionSchemaDigest",
            "analyzerDigest",
            "configurationDigest",
            "expectedItemCount",
            "expectedPartitions",
            "capabilities",
            "artifacts",
            "createdAtUtc",
        )
    }


def build_bundle(record_count: int) -> tuple[dict[str, Any], dict[str, Any]]:
    documents = records(record_count)
    shard_count = 4
    candidate_capacity = 50
    max_work_units = record_count * 16
    source_manifest_digest = digest_json(
        [
            {
                "partitionKey": value["partitionKey"],
                "id": value["id"],
                "revision": value["revision"],
            }
            for value in documents
        ]
    )
    revision_set_digest = digest_json(
        sorted((value["partitionKey"], value["id"], value["revision"]) for value in documents)
    )
    projection_schema_digest = digest_bytes(CONTRACT_SCHEMA.read_bytes())
    analyzer_digest = digest_json(
        {"tokenPattern": "[a-z0-9]+", "stopWords": [], "queryAliases": []}
    )
    generation_material = {
        "provider": "cloudflare-worker-r2",
        "sourceManifestDigest": source_manifest_digest,
        "recordRevisionSetDigest": revision_set_digest,
        "projectionSchemaDigest": projection_schema_digest,
        "analyzerDigest": analyzer_digest,
        "shardCount": shard_count,
        "candidateCapacity": candidate_capacity,
        "maxWorkUnits": max_work_units,
    }
    generation_id = "worker-r2-" + hashlib.sha256(
        canonical_bytes(generation_material)
    ).hexdigest()[:24]

    document_frequency = Counter(
        term for document in documents for term in set(document["tokens"])
    )
    idf = {
        term: math.log(
            1 + (record_count - frequency + 0.5) / (frequency + 0.5)
        )
        for term, frequency in document_frequency.items()
    }
    per_shard: list[list[dict[str, Any]]] = [[] for _ in range(shard_count)]
    for document in documents:
        per_shard[route(document["id"], shard_count)].append(document)

    objects: dict[str, str] = {}
    declarations: list[dict[str, Any]] = []
    shard_keys: list[str] = []
    for shard_index, shard_documents in enumerate(per_shard):
        direct: dict[str, list[int]] = defaultdict(list)
        terms: dict[str, list[Any]] = {}
        postings: dict[str, list[list[Any]]] = defaultdict(list)
        shard_records = []
        for ordinal, document in enumerate(shard_documents):
            shard_records.append(
                {
                    "partitionKey": document["partitionKey"],
                    "id": document["id"],
                    "revision": document["revision"],
                    "length": len(document["tokens"]),
                    "metadata": document["metadata"],
                }
            )
            direct[document["id"]].append(ordinal)
            for term, frequency in Counter(document["tokens"]).items():
                postings[term].append([ordinal, float(frequency)])
        for term, values in postings.items():
            terms[term] = [idf[term], values]
        shard = {
            "schemaVersion": SHARD_SCHEMA,
            "generationId": generation_id,
            "sourceManifestDigest": source_manifest_digest,
            "shardId": f"shard-{shard_index:02d}",
            "partitions": [PARTITION],
            "itemCount": len(shard_records),
            "records": shard_records,
            "directMap": dict(sorted(direct.items())),
            "terms": dict(sorted(terms.items())),
        }
        encoded = canonical_bytes(shard)
        content_hash = digest_bytes(encoded)
        key = object_key(content_hash)
        objects[key] = encoded.decode("utf-8")
        shard_keys.append(key)
        declarations.append(
            {
                "id": shard["shardId"],
                "key": key,
                "contentHash": content_hash,
                "sizeBytes": len(encoded),
                "itemCount": shard["itemCount"],
                "partitions": [PARTITION],
            }
        )

    manifest = {
        "schemaVersion": MANIFEST_SCHEMA,
        "generationId": generation_id,
        "sourceManifestDigest": source_manifest_digest,
        "recordRevisionSetDigest": revision_set_digest,
        "projectionSchemaDigest": projection_schema_digest,
        "analyzerDigest": analyzer_digest,
        "scoringContract": "global-bm25-like-card-v1",
        "tieBreak": "score-desc-partition-id-asc-v1",
        "k1": 1.2,
        "b": 0.75,
        "tokenPattern": "[a-z0-9]+",
        "stopWords": [],
        "queryAliases": [],
        "expectedItemCount": record_count,
        "expectedPartitions": [PARTITION],
        "averageDocumentLength": 3.0,
        "candidateCapacity": candidate_capacity,
        "maxWorkUnits": max_work_units,
        "shards": declarations,
    }
    manifest_bytes = canonical_bytes(manifest)
    manifest_digest = digest_bytes(manifest_bytes)
    manifest_key = object_key(manifest_digest)
    objects[manifest_key] = manifest_bytes.decode("utf-8")

    descriptor = {
        "schema": "vyral.record-search-projection-generation.v1",
        "collection": "portable-sample",
        "generationId": generation_id,
        "providerId": "cloudflare-worker-r2",
        "profileId": "global-bm25-like-card-v1",
        "strategyVersion": "private-json-shards-v1",
        "sourceManifestDigest": source_manifest_digest,
        "recordRevisionSetDigest": revision_set_digest,
        "projectionSchemaDigest": projection_schema_digest,
        "analyzerDigest": analyzer_digest,
        "configurationDigest": digest_json(generation_material),
        "expectedItemCount": record_count,
        "expectedPartitions": [PARTITION],
        "capabilities": ["completeCoverage", "generationPinnedContinuation", "lexical"],
        "artifacts": [
            {
                "id": "worker-r2-manifest",
                "kind": "worker-r2-generation-manifest",
                "contentHash": manifest_digest,
                "sizeBytes": len(manifest_bytes),
                "mediaType": "application/json",
            }
        ],
        "createdAtUtc": "2026-08-27T00:00:00Z",
    }
    descriptor["descriptorDigest"] = digest_json(descriptor_material(descriptor))
    schema = json.loads(CONTRACT_SCHEMA.read_text(encoding="utf-8"))
    jsonschema.validate(descriptor, schema)

    catalog = {
        "schemaVersion": CATALOG_SCHEMA,
        "collection": descriptor["collection"],
        "generationId": generation_id,
        "state": "active",
        "descriptor": descriptor,
        "manifestKey": manifest_key,
        "availablePartitions": [PARTITION],
    }
    active = {
        "schemaVersion": ACTIVE_SCHEMA,
        "collection": descriptor["collection"],
        "generationId": generation_id,
        "descriptorDigest": descriptor["descriptorDigest"],
    }
    catalog_key = f"catalog/{descriptor['collection']}/{generation_id}.json"
    active_key = f"active/{descriptor['collection']}.json"
    objects[catalog_key] = canonical_bytes(catalog).decode("utf-8")
    objects[active_key] = canonical_bytes(active).decode("utf-8")

    query_texts = ("portable", "retrieval", "portable execution", "record-000")
    queries = [
        {
            "id": f"query-{index + 1}",
            "request": request(query, limit=10, scan_limit=max_work_units),
            "expected": expected_candidates(documents, query, idf, limit=10),
        }
        for index, query in enumerate(query_texts)
    ]
    paged_query = {
        "id": "paged-portable",
        "request": request("portable", limit=15, scan_limit=max_work_units),
        "expected": expected_candidates(documents, "portable", idf, limit=15),
    }
    bundle = {
        "schemaVersion": BUNDLE_SCHEMA,
        "collection": descriptor["collection"],
        "generationId": generation_id,
        "descriptor": descriptor,
        "objects": objects,
        "catalogKey": catalog_key,
        "activeKey": active_key,
        "manifestKey": manifest_key,
        "shardKeys": shard_keys,
        "queries": queries,
        "pagedQuery": paged_query,
        "workBoundQuery": queries[0],
    }
    evidence = {
        "recordCount": record_count,
        "queryCount": len(queries),
        "generationId": generation_id,
        "descriptorDigest": descriptor["descriptorDigest"],
        "manifestDigest": manifest_digest,
        "shardDigests": [value["contentHash"] for value in declarations],
        "sourceManifestDigest": source_manifest_digest,
        "recordRevisionSetDigest": revision_set_digest,
        "shardCount": shard_count,
    }
    return bundle, evidence


def validate_report(report: Mapping[str, Any], query_count: int) -> None:
    required_true = (
        "unauthenticatedRequestRejected",
        "wrongAuthenticationRejected",
        "nonJsonContentRejected",
        "oversizedBodyRejected",
        "malformedDescriptorDigestRejected",
        "exactGenerationSelectionPassed",
        "retainedGenerationContinuationPassed",
        "continuationTamperRejected",
        "continuationRequestSubstitutionRejected",
        "retiredGenerationRejected",
        "verifiedContentCacheHit",
        "missingShardFailedClosed",
        "corruptShardFailedClosed",
        "incompleteCoverageFailedClosed",
        "descriptorFenceRejected",
        "expiredDeadlineRejected",
        "workLimitFailedClosed",
        "inspectionVerifiesArtifacts",
        "readerBindingConfigurationFailedClosed",
    )
    if report.get("schemaVersion") != REPORT_SCHEMA or report.get("status") != "complete":
        raise ProofError("Worker/R2 harness did not return a complete public proof report")
    if report.get("queryCount") != query_count:
        raise ProofError("Worker/R2 harness did not run every deterministic query")
    if report.get("exactCandidateAndScoreParityCount") != query_count:
        raise ProofError("Worker/R2 candidates or scores diverged from the exhaustive oracle")
    if any(report.get(field) is not True for field in required_true):
        raise ProofError("Worker/R2 harness omitted a required lifecycle or failure proof")
    if report.get("bindingMode") == "service-reader":
        if report.get("serviceReaderGuardsPassed") is not True:
            raise ProofError("Service-reader authorization or mutation guards did not pass")
    elif report.get("bindingMode") == "direct-r2":
        if report.get("serviceReaderGuardsPassed") is not None:
            raise ProofError("Direct R2 proof unexpectedly reported service-reader evidence")
    else:
        raise ProofError("Worker/R2 harness reported an unsupported binding mode")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--records", type=int, default=100)
    arguments = parser.parse_args()
    if arguments.records < 16:
        parser.error("--records must be at least 16 so pagination and work bounds are exercised")

    bundle, generation = build_bundle(arguments.records)
    with TemporaryDirectory(prefix="vyral-worker-r2-proof-") as temporary:
        bundle_path = Path(temporary) / "bundle.json"
        bundle_path.write_bytes(canonical_bytes(bundle))
        reports: dict[str, Any] = {}
        for mode in ("direct-r2", "service-reader"):
            completed = subprocess.run(
                [
                    "node",
                    str(PROJECTION / "verify.mjs"),
                    "--bundle",
                    str(bundle_path),
                    "--mode",
                    mode,
                ],
                cwd=PROJECTION,
                check=False,
                stdout=subprocess.PIPE,
                text=True,
                timeout=180,
            )
            if completed.returncode != 0:
                raise ProofError(
                    f"Worker/R2 {mode} harness failed with exit code {completed.returncode}"
                )
            report = json.loads(completed.stdout)
            validate_report(report, len(bundle["queries"]))
            reports[mode] = report

    receipt = {
        "schemaVersion": "vyral.worker-r2-local-proof-receipt.v1",
        "status": "complete",
        "generation": generation,
        "implementation": {
            "workerSha256": digest_bytes((PROJECTION / "src" / "worker.mjs").read_bytes()),
            "objectReaderSha256": digest_bytes(
                (PROJECTION / "src" / "object-reader.mjs").read_bytes()
            ),
        },
        "verification": {
            mode: {
                key: value
                for key, value in report.items()
                if key not in {"schemaVersion", "status", "sampleResults"}
            }
            for mode, report in reports.items()
        },
        "boundaries": {
            "candidateOnly": True,
            "consumerAuthorizationMovedIntoVyral": False,
            "providerIndexFormatDeclaredPortable": False,
            "networkHopRequired": False,
            "liveCloudflareQualifiedByThisReceipt": False,
        },
    }
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        json.dumps(
            {
                "output": str(arguments.output),
                "status": receipt["status"],
                "queries": len(bundle["queries"]),
                "records": arguments.records,
                "modes": sorted(reports),
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
