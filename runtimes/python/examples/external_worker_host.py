"""Isolated Python-host fixture for external-worker qualification."""

from __future__ import annotations

import os

from vyral_runtime import (
    ExecutionHandlerDescriptor,
    VyralHostApplication,
    create_host_application,
)

from .external_worker import HANDLER_ID, PLUGIN_ID


def create_application() -> VyralHostApplication:
    root = os.environ.get("VYRAL_RUNTIME_ROOT")
    if not root:
        raise RuntimeError("VYRAL_RUNTIME_ROOT is required.")
    return create_host_application(
        root,
        api_key=os.environ.get("VYRAL_API_KEY"),
        external_handlers=(
            ExecutionHandlerDescriptor(
                HANDLER_ID,
                "Python approval integration",
                plugin_id=PLUGIN_ID,
            ),
        ),
    )


__all__ = ["create_application"]
