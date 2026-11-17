"""Tool results and the error vocabulary (mcp-surface.md § Error vocabulary).

Every tool returns `structuredContent` conforming to its outputSchema, plus
the serialized JSON as a text block — the spec's backwards-compatibility
SHOULD. Domain failures reuse `insolvia_core.errors`: this module maps
exception classes to machine-readable codes exactly as the API layer maps
them to statuses, so the reasoning each class's docstring carries
(anti-oracle 404, honest 403, retryable 409) governs both surfaces:

    structuredContent on error: { error: { code, message, fields? } }

Unexpected exceptions are a generic `internal` error with no detail — GLBA
logging rules apply to this service exactly as to the API: metadata only,
never payloads, never a token.
"""

from __future__ import annotations

import functools
import json
import logging
from collections.abc import Callable, Mapping
from typing import Any, TypeVar

from insolvia_core.errors import (
    ApiError,
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from mcp.types import CallToolResult, TextContent

logger = logging.getLogger(__name__)

VALIDATION_FAILED = "validation_failed"
NOT_FOUND = "not_found"
PERMISSION_DENIED = "permission_denied"
CONFLICT = "conflict"
INTERNAL = "internal"


def success(payload: Mapping[str, Any]) -> CallToolResult:
    """A successful result: the payload as structuredContent (validated by
    the SDK against the tool's outputSchema) plus its JSON serialization as
    the text block older harnesses read."""
    structured = dict(payload)
    return CallToolResult(
        content=[TextContent(type="text", text=json.dumps(structured))],
        structured_content=structured,
    )


def error(
    code: str, message: str, *, fields: Mapping[str, str] | None = None
) -> CallToolResult:
    body: dict[str, Any] = {"code": code, "message": message}
    if fields is not None:
        body["fields"] = dict(fields)
    structured = {"error": body}
    return CallToolResult(
        content=[TextContent(type="text", text=json.dumps(structured))],
        structured_content=structured,
        is_error=True,
    )


def _map_domain_error(exc: ApiError) -> CallToolResult:
    # Specific before general, exactly as the Flask error handlers register:
    # FieldValidationError IS a ValidationError, ForbiddenError IS an ApiError.
    if isinstance(exc, FieldValidationError):
        return error(VALIDATION_FAILED, str(exc), fields=exc.fields)
    if isinstance(exc, ValidationError):
        return error(VALIDATION_FAILED, str(exc))
    if isinstance(exc, NotFoundError):
        return error(NOT_FOUND, str(exc))
    if isinstance(exc, ForbiddenError):
        return error(PERMISSION_DENIED, str(exc))
    if isinstance(exc, ConflictError):
        return error(CONFLICT, str(exc))
    return error(VALIDATION_FAILED, str(exc))


ToolFn = TypeVar("ToolFn", bound=Callable[..., CallToolResult])


def guarded(tool_name: str) -> Callable[[ToolFn], ToolFn]:
    """Wrap a tool function in the error mapping.

    The equivalent of the API's app-factory error handlers, applied per tool
    because the MCP SDK's own fallback (`ToolError` → bare text) cannot carry
    the structured `error` block the surface promises. An unexpected
    exception logs the traceback (metadata only — the tool name; never the
    arguments) and answers `internal` with no detail.
    """

    def decorate(fn: ToolFn) -> ToolFn:
        @functools.wraps(fn)
        def wrapper(*args: Any, **kwargs: Any) -> CallToolResult:
            try:
                return fn(*args, **kwargs)
            except ApiError as exc:
                logger.info(
                    "tool refused",
                    extra={"tool": tool_name, "kind": type(exc).__name__},
                )
                return _map_domain_error(exc)
            except Exception:
                logger.exception(
                    "unexpected MCP tool failure", extra={"tool": tool_name}
                )
                return error(INTERNAL, "request failed")

        return wrapper  # type: ignore[return-value]

    return decorate
