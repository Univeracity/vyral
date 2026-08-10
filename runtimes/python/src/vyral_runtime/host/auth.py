from __future__ import annotations

from hmac import compare_digest
from typing import Any, Mapping


class HostAuthenticationError(PermissionError):
    """Raised when a host request does not carry valid credentials."""


class ApiKeyAuthorizer:
    """API-key policy shared by the REST and MCP host adapters."""

    def __init__(self, api_key: str) -> None:
        if not api_key or api_key.isspace():
            raise ValueError("api_key must be non-empty.")
        self._api_key = api_key

    async def authorize_rest(
        self,
        operation_id: str,
        authorization_class: str,
        headers: Mapping[str, str],
        path_parameters: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> None:
        del operation_id, path_parameters, query, body
        if authorization_class == "anonymous":
            return
        self._require_key(headers)

    async def authorize_mcp(
        self,
        operation_id: str,
        headers: Mapping[str, str],
        arguments: Mapping[str, Any],
    ) -> None:
        del operation_id, arguments
        self._require_key(headers)

    def _require_key(self, headers: Mapping[str, str]) -> None:
        supplied = headers.get("x-vyral-api-key")
        authorization = headers.get("authorization")
        bearer: str | None = None
        if authorization is not None:
            scheme, separator, token = authorization.partition(" ")
            if (
                separator
                and scheme.casefold() == "bearer"
                and token.strip()
            ):
                bearer = token.strip()
        if supplied is not None and authorization is not None:
            if bearer is None or not compare_digest(
                supplied.encode("utf-8"),
                bearer.encode("utf-8"),
            ):
                raise HostAuthenticationError(
                    "Conflicting Vyral authentication headers are not "
                    "allowed."
                )
        elif supplied is None:
            supplied = bearer
        if supplied is None or not compare_digest(
            supplied.encode("utf-8"), self._api_key.encode("utf-8")
        ):
            raise HostAuthenticationError(
                "Valid Vyral API-key authentication is required."
            )


class RestApiKeyAuthorizer:
    """REST authorizer adapter for :class:`ApiKeyAuthorizer`."""

    def __init__(self, policy: ApiKeyAuthorizer) -> None:
        self.policy = policy

    async def authorize(
        self,
        operation_id: str,
        authorization_class: str,
        headers: Mapping[str, str],
        path_parameters: Mapping[str, str],
        query: Mapping[str, str],
        body: object | None,
    ) -> None:
        await self.policy.authorize_rest(
            operation_id,
            authorization_class,
            headers,
            path_parameters,
            query,
            body,
        )


class McpApiKeyAuthorizer:
    """MCP authorizer adapter for :class:`ApiKeyAuthorizer`."""

    def __init__(self, policy: ApiKeyAuthorizer) -> None:
        self.policy = policy

    async def authorize(
        self,
        operation_id: str,
        headers: Mapping[str, str],
        arguments: Mapping[str, Any],
    ) -> None:
        await self.policy.authorize_mcp(
            operation_id, headers, arguments
        )


__all__ = [
    "ApiKeyAuthorizer",
    "HostAuthenticationError",
    "McpApiKeyAuthorizer",
    "RestApiKeyAuthorizer",
]
