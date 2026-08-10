"""Generate a self-contained user-owned local Vyral application."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


_STARTER_SOURCE = '''\
from __future__ import annotations

import asyncio
from collections.abc import Mapping
import json
from pathlib import Path

from vyral_runtime import (
    ExecutionRunContext,
    ExecutionRunRequest,
    ExecutionRunResult,
    VyralRuntime,
    execution_plugin,
    vyral,
)


STATE_ROOT = Path(__file__).resolve().parent / ".vyral" / "starter"
PLUGIN_ID = "starter"
HANDLER_ID = "starter.hello"
# Rerun unchanged to prove idempotency. Increment only to admit new work.
RUN_VERSION = 1


@vyral(HANDLER_ID, plugin=PLUGIN_ID, max_attempts=2)
async def hello(context: ExecutionRunContext) -> ExecutionRunResult:
    """Edit this handler, increment RUN_VERSION, and rerun it durably."""

    name = "world"
    if isinstance(context.run.payload, Mapping):
        candidate = context.run.payload.get("name")
        if isinstance(candidate, str) and candidate.strip():
            name = candidate.strip()
    await context.record_event(
        "starter.hello",
        message="The user-owned Vyral handler is running.",
    )
    return ExecutionRunResult.succeeded_result(
        {"message": f"Hello, {name}!"}
    )


PLUGIN = execution_plugin(
    PLUGIN_ID,
    name="Starter",
    version="1.0.0",
    handlers=(hello,),
)


async def main() -> None:
    request = ExecutionRunRequest(
        HANDLER_ID,
        plugin_id=PLUGIN_ID,
        payload={"name": "Vyral", "runVersion": RUN_VERSION},
        idempotency_key=f"starter.hello.v{RUN_VERSION}",
    )

    with VyralRuntime.open_local(
        STATE_ROOT,
        execution_plugins=(PLUGIN,),
    ) as runtime:
        admitted = await runtime.execution.start_run(request)
        print(
            "Accepted receipt: "
            f"run={admitted.id} status={admitted.status} "
            f"replayed={str(admitted.admission_replayed).lower()}"
        )

    print("Closed the first runtime instance before dispatch.")
    with VyralRuntime.open_local(
        STATE_ROOT,
        execution_plugins=(PLUGIN,),
    ) as runtime:
        persisted = await runtime.execution.get_run(admitted.id)
        if persisted is None:
            raise RuntimeError("The admitted run was not recovered.")
        print(f"Recovered: run={persisted.id} status={persisted.status}")
        dispatched = await runtime.execution.dispatch_ready_runs(
            recover_interrupted_runs=True
        )
        completed = await runtime.execution.get_run(admitted.id)
        if completed is None or completed.status != "succeeded":
            status = completed.status if completed is not None else "missing"
            raise RuntimeError(f"The starter run did not succeed: {status}")
        print(
            "Completed: "
            f"run={completed.id} status={completed.status} "
            f"dispatched={dispatched} "
            f"result={json.dumps(completed.result, sort_keys=True)}"
        )


if __name__ == "__main__":
    asyncio.run(main())
'''


@dataclass(frozen=True)
class LocalStarterResult:
    created_path: Path
    state_root_path: Path

    def to_dict(self) -> dict[str, object]:
        return {
            "createdPath": str(self.created_path),
            "stateRootPath": str(self.state_root_path),
            "runArguments": ["python", str(self.created_path)],
        }


def create_local_starter(output_path: str | Path) -> LocalStarterResult:
    """Create one editable starter without overwriting an existing path."""

    requested = Path(output_path).expanduser()
    if requested.suffix.casefold() != ".py":
        raise ValueError("The local starter path must end with .py.")
    if requested.exists() or requested.is_symlink():
        raise ValueError(
            f"Refusing to overwrite an existing starter path: {requested}"
        )
    requested.parent.mkdir(parents=True, exist_ok=True)
    target = requested.absolute()
    try:
        with target.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(_STARTER_SOURCE)
    except FileExistsError as error:
        raise ValueError(
            f"Refusing to overwrite an existing starter path: {target}"
        ) from error
    created = target.resolve()
    return LocalStarterResult(
        created_path=created,
        state_root_path=created.parent / ".vyral" / "starter",
    )


__all__ = ["LocalStarterResult", "create_local_starter"]
