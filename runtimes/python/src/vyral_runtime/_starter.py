"""Generate a self-contained user-owned local Vyral application."""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
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


STATE_ROOT = (
    Path(__file__).resolve().parent
    / ".vyral"
    / __VYRAL_STARTER_STATE_DIRECTORY__
)
PLUGIN_ID = __VYRAL_STARTER_PLUGIN_ID__
HANDLER_ID = __VYRAL_STARTER_HANDLER_ID__
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
        HANDLER_ID,
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
        idempotency_key=f"{HANDLER_ID}.v{RUN_VERSION}",
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
    app_id: str

    def to_dict(self) -> dict[str, object]:
        return {
            "createdPath": str(self.created_path),
            "stateRootPath": str(self.state_root_path),
            "appId": self.app_id,
            "runArguments": ["vyral", "run", str(self.created_path)],
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
    identity = _starter_identity(target.stem)
    state_directory = identity.removeprefix("starter.")
    handler_id = f"{identity}.hello"
    source = (
        _STARTER_SOURCE.replace(
            "__VYRAL_STARTER_STATE_DIRECTORY__",
            repr(state_directory),
        )
        .replace("__VYRAL_STARTER_PLUGIN_ID__", repr(identity))
        .replace("__VYRAL_STARTER_HANDLER_ID__", repr(handler_id))
    )
    try:
        with target.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(source)
    except FileExistsError as error:
        raise ValueError(
            f"Refusing to overwrite an existing starter path: {target}"
        ) from error
    created = target.resolve()
    return LocalStarterResult(
        created_path=created,
        state_root_path=created.parent / ".vyral" / state_directory,
        app_id=identity,
    )


def _starter_identity(stem: str) -> str:
    characters: list[str] = []
    previous_separator = False
    for character in stem.casefold():
        if character.isascii() and (
            character.isalnum() or character in {"_", "-"}
        ):
            characters.append(character)
            previous_separator = False
        elif not previous_separator:
            characters.append("-")
            previous_separator = True
    normalized = "".join(characters).strip("-_") or "app"
    changed = normalized != stem or len(normalized) > 80
    if changed:
        digest = sha256(stem.encode("utf-8")).hexdigest()[:8]
        base = normalized[:70].rstrip("-_") or "app"
        normalized = f"{base}-{digest}"
    return f"starter.{normalized}"


__all__ = ["LocalStarterResult", "create_local_starter"]
