from __future__ import annotations

import asyncio
from typing import cast
import unittest

from vyral_runtime import (
    ExecutionHandlerHarness,
    ExecutionRunContext,
    ExecutionRunResult,
    execution_handler,
    execution_plugin,
    vyral,
)


class ExecutionAuthoringTests(unittest.TestCase):
    def test_vyral_is_the_concise_handler_surface(self) -> None:
        @vyral(
            "example.concise",
            plugin="example.plugin",
            name="Concise handler",
            max_attempts=2,
        )
        async def concise(
            context: ExecutionRunContext,
        ) -> ExecutionRunResult:
            return ExecutionRunResult.succeeded_result(context.run.payload)

        plugin = execution_plugin(
            "example.plugin",
            version="1.0.0",
            handlers=(concise,),
        )

        self.assertEqual("concise", getattr(concise, "__name__"))
        self.assertEqual("example.concise", concise.descriptor.handler_id)
        self.assertEqual("example.plugin", concise.descriptor.plugin_id)
        self.assertEqual("Concise handler", concise.descriptor.display_name)
        self.assertEqual(2, concise.descriptor.max_attempts)
        self.assertEqual("example.plugin", plugin.descriptor.plugin_id)

    def test_decorators_compile_to_the_existing_plugin_contract(self) -> None:
        @execution_handler(
            "example.echo",
            plugin_id="example.plugin",
            max_attempts=3,
            tags={"surface": "decorator"},
        )
        async def echo(
            context: ExecutionRunContext,
        ) -> ExecutionRunResult:
            await context.record_event("log", message="decorated handler")
            return ExecutionRunResult.succeeded_result(
                {"received": context.run.payload}
            )

        plugin = execution_plugin(
            "example.plugin",
            name="Example",
            version="1.0.0",
            handlers=(echo,),
        )
        completed = asyncio.run(
            ExecutionHandlerHarness(plugin).run(
                "example.echo",
                payload={"value": 42},
            )
        )

        self.assertEqual("echo", getattr(echo, "__name__"))
        self.assertEqual("Echo", echo.descriptor.display_name)
        self.assertEqual(3, echo.descriptor.max_attempts)
        self.assertEqual("decorator", echo.descriptor.tags["surface"])
        self.assertEqual("example.plugin", plugin.descriptor.plugin_id)
        self.assertEqual("succeeded", completed.status)
        self.assertEqual({"received": {"value": 42}}, completed.result)

    def test_decorated_sync_handler_is_directly_awaitable(self) -> None:
        @execution_handler(
            "example.sync",
            plugin_id="example.plugin",
            display_name="Synchronous handler",
        )
        def sync_handler(
            _: ExecutionRunContext,
        ) -> ExecutionRunResult:
            return ExecutionRunResult.succeeded_result({"ok": True})

        result = asyncio.run(
            sync_handler(cast(ExecutionRunContext, _UnusedContext()))
        )

        self.assertEqual("succeeded", result.status)
        self.assertEqual({"ok": True}, result.result)


class _UnusedContext:
    """A deliberately incomplete context for a handler that never reads it."""


if __name__ == "__main__":
    unittest.main()
