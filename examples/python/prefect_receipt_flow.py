"""Compose a Prefect 3 flow around a receipt-bound Vyral operation.

Prefect owns scheduling, task retries, and operator visibility in this example.
Vyral remains authoritative for admission, lifecycle state, and the result.
"""

from __future__ import annotations

import hashlib
import os
import pathlib
import sys
import uuid
from typing import Any

sys.path.insert(
    0,
    str(pathlib.Path(__file__).resolve().parents[2] / "clients/python/src"),
)

from prefect import flow, task
from prefect.artifacts import create_markdown_artifact
from prefect.logging import get_run_logger
from prefect.runtime import flow_run
from vyral_client import VyralClient


def _client(base_url: str) -> VyralClient:
    # Resolve secrets on the worker. Do not make credentials flow parameters,
    # where an orchestrator may persist or display them.
    return VyralClient(base_url, api_key=os.environ.get("VYRAL_API_KEY"))


def _flow_scoped_key(operation: str) -> str:
    run_id = str(flow_run.id or uuid.uuid4())
    material = f"vyral.prefect.v1:{operation}:{run_id}".encode()
    return hashlib.sha256(material).hexdigest()


@task(retries=2, retry_delay_seconds=2)
def admit_embedding(
    base_url: str,
    texts: list[str],
    idempotency_key: str,
) -> dict[str, Any]:
    """Admit exactly one Vyral job even when Prefect retries this task."""

    job = _client(base_url).start_embedding_job(
        {"texts": texts, "purpose": "document"},
        idempotency_key=idempotency_key,
    )
    receipt = job["admission"]
    get_run_logger().info(
        "Vyral admitted resource %s; lifecycle: %s",
        receipt["resourceId"],
        receipt["statusUri"],
    )
    return job


@task
def await_embedding(base_url: str, job_id: str) -> dict[str, Any]:
    """Poll the Vyral-owned lifecycle rather than rerunning accepted work."""

    job = _client(base_url).wait_embedding_job(job_id)
    if job is None:
        raise RuntimeError(f"Vyral embedding job {job_id} no longer exists")
    if job["status"] != "succeeded":
        raise RuntimeError(
            f"Vyral embedding job {job_id} ended as {job['status']}: "
            f"{job.get('error') or 'no error detail'}"
        )
    return job


@flow(name="Vyral receipt-bound embedding")
def embed_with_vyral(
    texts: list[str],
    base_url: str = "http://localhost:5220",
) -> list[list[float]]:
    # Compute this outside the retried task so every attempt receives the same
    # key, including in an unusual context without a Prefect flow-run ID.
    idempotency_key = _flow_scoped_key("embedding")
    admitted = admit_embedding(base_url, texts, idempotency_key)
    completed = await_embedding(base_url, admitted["id"])
    receipt = admitted["admission"]

    # Project only non-secret receipt fields into Prefect's operator UI.
    create_markdown_artifact(
        key="vyral-receipt",
        description="Durable Vyral admission receipt",
        markdown=(
            "## Vyral admission\n\n"
            f"- Resource: `{receipt['resourceId']}`\n"
            f"- Admission: `{receipt['admissionId']}`\n"
            f"- Replayed: `{str(receipt['replayed']).lower()}`\n"
            f"- Final status: `{completed['status']}`\n"
        ),
    )
    result = completed["result"]
    return [item["values"] for item in result["items"]]


if __name__ == "__main__":
    print(
        embed_with_vyral(
            ["Stable contracts make infrastructure replaceable."],
            os.environ.get("VYRAL_URL", "http://localhost:5220"),
        )
    )
