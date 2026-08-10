"""Run a replay-safe Python handler against a Vyral external-worker endpoint.

The same plugin is imported by the live .NET interoperability qualification.
For local development:

    python -m runtimes.python.examples.external_worker \
        --server http://127.0.0.1:8080 --allow-insecure-http
"""

from __future__ import annotations

import argparse
import asyncio
from collections.abc import Sequence
from typing import Any, Mapping

from vyral_runtime import (
    DelegateExecutionHandler,
    ExecutionArtifactWrite,
    ExecutionCheckpointWrite,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionPluginWorker,
    ExecutionPluginWorkerOptions,
    ExecutionRunContext,
    ExecutionRunResult,
    ExecutionRunUpdate,
    HttpExecutionWorkerTransport,
    StaticExecutionPlugin,
    StaticExecutionWorkerTokenSource,
)


PLUGIN_ID = "python.examples.approval"
HANDLER_ID = "python.examples.approval.wait"


async def execute_approval(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    checkpoint = await context.get_checkpoint("before-approval")
    if checkpoint is None:
        await context.put_checkpoint(
            ExecutionCheckpointWrite(
                key="before-approval",
                content={"ready": True},
            )
        )
        await context.report(
            ExecutionRunUpdate(
                progress=0.5,
                current_step="waiting-for-approval",
            )
        )

    outcome = await context.wait_for_external_event("approval")
    payload = (
        outcome.event.payload
        if outcome.event is not None
        and isinstance(outcome.event.payload, Mapping)
        else {}
    )
    approved = payload.get("approved") is True
    await context.record_event(
        "step.completed",
        message="approval received",
        details={"approved": approved},
    )
    await context.put_artifact(
        ExecutionArtifactWrite(
            name="approval-summary",
            content={"approved": approved},
        )
    )
    run_payload = (
        context.run.payload
        if isinstance(context.run.payload, Mapping)
        else {}
    )
    return ExecutionRunResult.succeeded_result(
        {
            "approved": approved,
            "value": run_payload.get("value"),
        }
    )


def create_plugin() -> StaticExecutionPlugin:
    descriptor = ExecutionHandlerDescriptor(
        handler_id=HANDLER_ID,
        plugin_id=PLUGIN_ID,
        display_name="Python approval example",
        description=(
            "Checkpoints once, suspends for an approval event, and resumes "
            "safely after worker or server restart."
        ),
    )
    return StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=PLUGIN_ID,
            name="Python approval example",
            version="1.0.0",
        ),
        (DelegateExecutionHandler(descriptor, execute_approval),),
    )


async def run_worker(arguments: argparse.Namespace) -> None:
    token_source = (
        StaticExecutionWorkerTokenSource(arguments.token)
        if arguments.token
        else None
    )
    async with HttpExecutionWorkerTransport(
        arguments.server,
        arguments.worker_id,
        (HANDLER_ID,),
        token_source=token_source,
        allow_insecure_http=arguments.allow_insecure_http,
    ) as transport:
        worker = ExecutionPluginWorker(
            transport,
            (create_plugin(),),
            ExecutionPluginWorkerOptions(
                lease_ttl_seconds=arguments.lease_ttl_seconds,
                heartbeat_interval_seconds=arguments.heartbeat_interval_seconds,
                idle_delay_seconds=arguments.idle_delay_seconds,
            ),
        )
        await worker.run()


def parse_arguments(
    values: Sequence[str] | None = None,
) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True)
    parser.add_argument("--worker-id", default="python-example-worker")
    parser.add_argument("--token")
    parser.add_argument("--allow-insecure-http", action="store_true")
    parser.add_argument("--lease-ttl-seconds", type=float, default=60.0)
    parser.add_argument(
        "--heartbeat-interval-seconds",
        type=float,
        default=20.0,
    )
    parser.add_argument("--idle-delay-seconds", type=float, default=0.5)
    return parser.parse_args(values)


def main(values: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(values)
    try:
        asyncio.run(run_worker(arguments))
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
