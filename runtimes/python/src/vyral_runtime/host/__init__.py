"""Optional dependency-free ASGI hosts for the embedded runtime."""

from .auth import (
    ApiKeyAuthorizer,
    HostAuthenticationError,
    McpApiKeyAuthorizer,
    RestApiKeyAuthorizer,
)
from .application import (
    VyralHostApplication,
    create_host_application,
)
from .mcp import (
    MCP_PROTOCOL_VERSION,
    McpApplicationConfig,
    McpAuthorizer,
    StatelessMcpApplication,
)
from .rest import (
    RestApplicationConfig,
    RestAuthorizer,
    VyralRestApplication,
)

__all__ = [
    "MCP_PROTOCOL_VERSION",
    "ApiKeyAuthorizer",
    "HostAuthenticationError",
    "McpApplicationConfig",
    "McpApiKeyAuthorizer",
    "McpAuthorizer",
    "RestApplicationConfig",
    "RestApiKeyAuthorizer",
    "RestAuthorizer",
    "StatelessMcpApplication",
    "VyralHostApplication",
    "VyralRestApplication",
    "create_host_application",
]
