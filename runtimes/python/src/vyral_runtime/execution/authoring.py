"""Small Python authoring conveniences over the portable execution contract."""

from __future__ import annotations

from collections.abc import Callable, Iterable
from functools import update_wrapper
from typing import Mapping

from .models import ExecutionHandlerDescriptor, ExecutionPluginDescriptor
from .worker import (
    DelegateExecutionHandler,
    ExecutionHandler,
    HandlerCallable,
    StaticExecutionPlugin,
)


def execution_handler(
    handler_id: str,
    *,
    plugin_id: str,
    display_name: str | None = None,
    description: str | None = None,
    max_attempts: int = 1,
    concurrency_key: str | None = None,
    tags: Mapping[str, str] | None = None,
) -> Callable[[HandlerCallable], DelegateExecutionHandler]:
    """Decorate an ordinary function as a portable Vyral execution handler.

    This is authoring sugar only. The returned object implements the same
    ``ExecutionHandler`` contract used by local, external-worker, and provider
    adapters; it does not add a scheduler, graph, or alternate retry model.
    """

    def decorate(execute: HandlerCallable) -> DelegateExecutionHandler:
        selected_name = display_name or _function_display_name(execute)
        handler = DelegateExecutionHandler(
            ExecutionHandlerDescriptor(
                handler_id=handler_id,
                plugin_id=plugin_id,
                display_name=selected_name,
                description=description,
                max_attempts=max_attempts,
                concurrency_key=concurrency_key,
                tags=dict(tags or {}),
            ),
            execute,
        )
        update_wrapper(handler, execute)
        return handler

    return decorate


def vyral(
    handler_id: str,
    *,
    plugin: str,
    name: str | None = None,
    description: str | None = None,
    max_attempts: int = 1,
    concurrency_key: str | None = None,
    tags: Mapping[str, str] | None = None,
) -> Callable[[HandlerCallable], DelegateExecutionHandler]:
    """Mark a function as portable Vyral work.

    ``handler_id`` and ``plugin`` stay explicit because they are durable
    identities; Python function names and locations are not contract surfaces.
    Use :func:`execution_handler` when descriptor-oriented naming is clearer.
    """

    return execution_handler(
        handler_id,
        plugin_id=plugin,
        display_name=name,
        description=description,
        max_attempts=max_attempts,
        concurrency_key=concurrency_key,
        tags=tags,
    )


def execution_plugin(
    plugin_id: str,
    *,
    name: str | None = None,
    version: str,
    handlers: Iterable[ExecutionHandler],
) -> StaticExecutionPlugin:
    """Collect decorated handlers into the existing static plugin contract."""

    return StaticExecutionPlugin(
        ExecutionPluginDescriptor(
            plugin_id=plugin_id,
            name=name or _identifier_display_name(plugin_id),
            version=version,
        ),
        handlers,
    )


def _function_display_name(execute: HandlerCallable) -> str:
    name = getattr(execute, "__name__", "handler")
    selected = str(name).strip("_").replace("_", " ").strip()
    return selected[:1].upper() + selected[1:] if selected else "Handler"


def _identifier_display_name(value: str) -> str:
    selected = value.replace(".", " ").replace("_", " ").strip()
    return " ".join(part[:1].upper() + part[1:] for part in selected.split())


__all__ = ["execution_handler", "execution_plugin", "vyral"]
