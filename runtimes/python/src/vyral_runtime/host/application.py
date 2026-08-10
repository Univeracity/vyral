from __future__ import annotations

from collections.abc import Awaitable, Callable, Iterable
from typing import Any, Mapping

from ..execution import ExecutionHandlerDescriptor, StaticExecutionPlugin
from ..runtime import VyralRuntime
from .auth import (
    ApiKeyAuthorizer,
    McpApiKeyAuthorizer,
    RestApiKeyAuthorizer,
)
from .mcp import McpApplicationConfig, McpAuthorizer, StatelessMcpApplication
from .rest import RestApplicationConfig, RestAuthorizer, VyralRestApplication


class VyralHostApplication:
    """Combined REST and stateless MCP ASGI application."""

    def __init__(
        self,
        runtime: VyralRuntime,
        *,
        rest_config: RestApplicationConfig | None = None,
        mcp_config: McpApplicationConfig | None = None,
        rest_authorizer: RestAuthorizer | None = None,
        mcp_authorizer: McpAuthorizer | None = None,
        close_runtime_on_shutdown: bool = False,
    ) -> None:
        self.runtime = runtime
        self.rest = VyralRestApplication(
            runtime, rest_config, authorizer=rest_authorizer
        )
        self.mcp = StatelessMcpApplication(
            runtime, mcp_config, authorizer=mcp_authorizer
        )
        self.close_runtime_on_shutdown = close_runtime_on_shutdown

    async def __call__(
        self,
        scope: Mapping[str, Any],
        receive: Callable[[], Awaitable[Mapping[str, Any]]],
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        if scope.get("type") == "lifespan":
            await self._lifespan(receive, send)
            return
        if (
            scope.get("type") == "http"
            and scope.get("path") == self.mcp.config.endpoint_path
        ):
            await self.mcp(scope, receive, send)
            return
        await self.rest(scope, receive, send)

    async def _lifespan(
        self,
        receive: Callable[[], Awaitable[Mapping[str, Any]]],
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        while True:
            message = await receive()
            message_type = message.get("type")
            if message_type == "lifespan.startup":
                if not await self._startup(send):
                    return
            elif message_type == "lifespan.shutdown":
                await self._shutdown(send)
                return

    async def _startup(
        self,
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> bool:
        rest_attempted = False
        mcp_attempted = False
        try:
            rest_attempted = True
            await self.rest.startup()
            mcp_attempted = True
            await self.mcp.startup()
        except BaseException as startup_error:
            failures: list[BaseException] = [startup_error]
            if mcp_attempted:
                failure = await _capture_failure(self.mcp.shutdown)
                if failure is not None:
                    failures.append(failure)
            if rest_attempted:
                failure = await _capture_failure(self.rest.shutdown)
                if failure is not None:
                    failures.append(failure)
            if self.close_runtime_on_shutdown:
                try:
                    self.runtime.close()
                except BaseException as close_error:
                    failures.append(close_error)
            fatal = _first_fatal(failures)
            if fatal is startup_error:
                raise
            if fatal is not None:
                raise fatal
            await send(
                {
                    "type": "lifespan.startup.failed",
                    "message": "Vyral host startup failed.",
                }
            )
            return False
        await send({"type": "lifespan.startup.complete"})
        return True

    async def _shutdown(
        self,
        send: Callable[[Mapping[str, Any]], Awaitable[None]],
    ) -> None:
        failures = [
            failure
            for operation in (self.mcp.shutdown, self.rest.shutdown)
            if (failure := await _capture_failure(operation)) is not None
        ]
        if self.close_runtime_on_shutdown:
            try:
                self.runtime.close()
            except BaseException as close_error:
                failures.append(close_error)
        fatal = _first_fatal(failures)
        if fatal is not None:
            raise fatal
        failed = bool(failures)
        await send(
            {
                "type": (
                    "lifespan.shutdown.failed"
                    if failed
                    else "lifespan.shutdown.complete"
                ),
                **(
                    {"message": "Vyral host shutdown failed."}
                    if failed
                    else {}
                ),
            }
        )


async def _capture_failure(
    operation: Callable[[], Awaitable[None]],
) -> BaseException | None:
    try:
        await operation()
    except BaseException as error:
        return error
    return None


def _first_fatal(
    failures: Iterable[BaseException],
) -> BaseException | None:
    return next(
        (
            failure
            for failure in failures
            if not isinstance(failure, Exception)
        ),
        None,
    )


def create_host_application(
    root_path: str,
    *,
    rest_config: RestApplicationConfig | None = None,
    mcp_config: McpApplicationConfig | None = None,
    api_key: str | None = None,
    execution_plugins: Iterable[StaticExecutionPlugin] = (),
    external_handlers: Iterable[ExecutionHandlerDescriptor] = (),
) -> VyralHostApplication:
    """Create a host-owned local runtime and combined ASGI application."""
    runtime = VyralRuntime(
        root_path,
        execution_plugins=execution_plugins,
        external_handlers=external_handlers,
        verify_assets=True,
    )
    policy = ApiKeyAuthorizer(api_key) if api_key is not None else None
    return VyralHostApplication(
        runtime,
        rest_config=rest_config,
        mcp_config=mcp_config,
        rest_authorizer=(
            RestApiKeyAuthorizer(policy)
            if policy is not None
            else None
        ),
        mcp_authorizer=(
            McpApiKeyAuthorizer(policy)
            if policy is not None
            else None
        ),
        close_runtime_on_shutdown=True,
    )


__all__ = [
    "VyralHostApplication",
    "create_host_application",
]
