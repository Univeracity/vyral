from __future__ import annotations

from vyral_runtime import (
    ContractBundle,
    DelegateExecutionHandler,
    ExecutionHandlerDescriptor,
    ExecutionPluginDescriptor,
    ExecutionRunContext,
    ExecutionRunResult,
    RuntimeReadiness,
    StaticExecutionPlugin,
    VyralRuntime,
    execution_handler,
    execution_plugin,
    vyral,
)
from vyral_runtime.contracts import JSONValue


runtime = VyralRuntime()
bundle: ContractBundle = runtime.contracts
readiness: RuntimeReadiness = runtime.readiness()
full_local_ready: bool = readiness.full_local_ready
readiness_document: dict[str, JSONValue] = readiness.to_dict()


async def execute(context: ExecutionRunContext) -> ExecutionRunResult:
    await context.record_event("log", message="typed worker")
    return ExecutionRunResult.succeeded_result(context.run.payload)


handler = DelegateExecutionHandler(
    ExecutionHandlerDescriptor(
        handler_id="typed.handler",
        plugin_id="typed.plugin",
        display_name="Typed handler",
    ),
    execute,
)
plugin = StaticExecutionPlugin(
    ExecutionPluginDescriptor(
        plugin_id="typed.plugin",
        name="Typed plugin",
        version="1.0.0",
    ),
    (handler,),
)
handler_id: str = plugin.handlers[0].descriptor.handler_id


@execution_handler(
    "typed.decorated",
    plugin_id="typed.decorated.plugin",
)
async def decorated_execute(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    return ExecutionRunResult.succeeded_result(context.run.payload)


decorated_plugin = execution_plugin(
    "typed.decorated.plugin",
    version="1.0.0",
    handlers=(decorated_execute,),
)
decorated_handler_id: str = decorated_plugin.handlers[0].descriptor.handler_id


@vyral(
    "typed.concise",
    plugin="typed.decorated.plugin",
)
async def concise_execute(
    context: ExecutionRunContext,
) -> ExecutionRunResult:
    return ExecutionRunResult.succeeded_result(context.run.payload)


concise_handler_id: str = concise_execute.descriptor.handler_id
