#!/usr/bin/env python3
"""Regression checks for retrieval-projection qualification policy."""

from __future__ import annotations

from copy import deepcopy
from datetime import datetime, timezone
import importlib.util
from pathlib import Path


VERIFIER = Path(__file__).with_name("verify-retrieval-projection-qualification.py")
SPEC = importlib.util.spec_from_file_location("vyral_retrieval_qualification", VERIFIER)
if SPEC is None or SPEC.loader is None:
    raise SystemExit("Could not load the retrieval qualification verifier.")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


SOURCE = {
    "sourceCommit": "e0b666601b199db4e667857027aa6424c64e52d5",
    "sourceTreeDigest": "sha256:" + "1" * 64,
    "sourceDirty": True,
}


def _artifact() -> dict[str, object]:
    evidence = {
        "kind": "unit_gate",
        "observedAtUtc": "2026-08-27T12:00:00Z",
        "expiresAtUtc": "2026-11-25T12:00:00Z",
        "reference": "tests/Vyral.Tests.Aws/OpenSearchGenerationBoundRecordSearchProjectionTests.cs",
        "disclosure": "public",
        **SOURCE,
        "generationIds": [],
        "descriptorDigests": [],
    }
    return {
        "schemaVersion": "1.0",
        "coreContractVersion": "0.3.0",
        "generatedAtUtc": "2026-08-27T12:05:00Z",
        "qualificationExpiresAfterDays": 90,
        "adapters": [
            {
                "adapterId": "opensearch-generation",
                "displayName": "OpenSearch generation projection",
                "implementation": "OpenSearchGenerationBoundRecordSearchProjection",
                "provider": "opensearch",
                "topology": "single-node-container",
                "profileId": "vector-v1",
                "strategyVersion": "opensearch-3.8",
                "qualification": "local_conformant",
                "status": "current",
                "capabilities": ["complete-coverage", "vector"],
                "generationEvidencePolicy": {
                    "descriptorSchema": "vyral.record-search-projection-generation.v1",
                    "requiresExactGenerationBinding": True,
                    "requiresCompleteRequestedCoverage": True,
                    "mutableHealthEstablishesCompleteness": False,
                },
                "evidence": [
                    evidence,
                    {**evidence, "kind": "local_gate", "reference": "tests/Vyral.Tests.Aws/OpenSearchRecordSearchProjectionLocalTests.cs"},
                ],
            }
        ],
    }


def _rejects(artifact: dict[str, object], expected: str, *, allow_dirty: bool = True) -> None:
    try:
        MODULE.validate(
            artifact,
            as_of=datetime(2026, 8, 27, 12, 10, tzinfo=timezone.utc),
            source=SOURCE,
            allow_dirty=allow_dirty,
            public_disclosure=True,
        )
    except SystemExit as error:
        if expected not in str(error):
            raise SystemExit(f"Unexpected qualification rejection: {error}") from error
        return
    raise SystemExit(f"Qualification verifier accepted invalid evidence: {expected}")


def main() -> int:
    artifact = _artifact()
    MODULE.validate(
        artifact,
        as_of=datetime(2026, 8, 27, 12, 10, tzinfo=timezone.utc),
        source=SOURCE,
        allow_dirty=True,
        public_disclosure=True,
    )

    clean_required = deepcopy(artifact)
    _rejects(clean_required, "dirty source tree", allow_dirty=False)

    missing_local = deepcopy(artifact)
    missing_local["adapters"][0]["evidence"] = missing_local["adapters"][0]["evidence"][:1]
    _rejects(missing_local, "local_gate")

    substituted_source = deepcopy(artifact)
    substituted_source["adapters"][0]["evidence"][0]["sourceTreeDigest"] = "sha256:" + "2" * 64
    _rejects(substituted_source, "does not match")

    unpaired_generation = deepcopy(artifact)
    unpaired_generation["adapters"][0]["evidence"][0]["generationIds"] = ["generation-a"]
    _rejects(unpaired_generation, "must be paired")

    endpoint_reference = deepcopy(artifact)
    endpoint_reference["adapters"][0]["evidence"][0]["reference"] = "https://provider.example.invalid/evidence"
    _rejects(endpoint_reference, "exposes non-portable")

    missing_disclosure = deepcopy(artifact)
    del missing_disclosure["adapters"][0]["evidence"][0]["disclosure"]
    _rejects(missing_disclosure, "declare its disclosure boundary")

    consumer_validated = deepcopy(artifact)
    adapter = consumer_validated["adapters"][0]
    adapter["qualification"] = "consumer_validated"
    base_evidence = adapter["evidence"][0]
    adapter["evidence"].extend(
        [
            {
                **base_evidence,
                "kind": "live_gate",
                "reference": "qualification/evidence/retrieval-live.json",
            },
            {
                **base_evidence,
                "kind": "cleanup",
                "reference": "qualification/evidence/retrieval-cleanup.json",
            },
            {
                **base_evidence,
                "kind": "consumer_validation",
                "reference": "urn:vyral:private-consumer-evidence:sha256:" + "a" * 64,
                "disclosure": "private_opaque",
            },
        ]
    )
    MODULE.validate(
        consumer_validated,
        as_of=datetime(2026, 8, 27, 12, 10, tzinfo=timezone.utc),
        source=SOURCE,
        allow_dirty=True,
        public_disclosure=True,
    )

    named_consumer = deepcopy(consumer_validated)
    named_consumer["adapters"][0]["evidence"][-1].update(
        {
            "reference": "private-consumer/qualification-receipt.json",
            "disclosure": "public",
        }
    )
    _rejects(named_consumer, "must remain private_opaque")

    exposed_generation = deepcopy(consumer_validated)
    private_evidence = exposed_generation["adapters"][0]["evidence"][-1]
    private_evidence["generationIds"] = ["consumer-generation"]
    private_evidence["descriptorDigests"] = ["sha256:" + "b" * 64]
    _rejects(exposed_generation, "cannot expose consumer generation identifiers")

    print("retrieval-projection-qualification-test=ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
