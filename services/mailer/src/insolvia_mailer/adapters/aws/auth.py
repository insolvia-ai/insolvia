from __future__ import annotations

import re
from contextvars import ContextVar, Token
from typing import Any

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import AuthorizationError

ASSUMED_ROLE = re.compile(r"^arn:aws:sts::(\d+):assumed-role/([^/]+)/[^/]+$")
_principal: ContextVar[str | None] = ContextVar("mailer_aws_principal", default=None)


def normalize_principal(arn: str) -> str:
    match = ASSUMED_ROLE.fullmatch(arn)
    if match:
        return f"arn:aws:iam::{match.group(1)}:role/{match.group(2)}"
    return arn


def principal_from_event(event: dict[str, Any]) -> str:
    context = event.get("requestContext", {})
    authorizer = context.get("authorizer", {})
    iam = authorizer.get("iam", {}) if isinstance(authorizer, dict) else {}
    identity = context.get("identity", {})
    raw = iam.get("userArn") or identity.get("userArn")
    if not raw:
        raise AuthorizationError("authenticated IAM principal is missing")
    return normalize_principal(raw)


def authorize(service: ServiceConfig, principal: str) -> None:
    if principal not in service.allowed_role_arns:
        raise AuthorizationError("caller is not registered for this service")


class IamAuthorizer:
    """Authorizes Flask requests using identity set by the Lambda entry point."""

    def authorize(self, service: ServiceConfig) -> None:
        principal = _principal.get()
        if not principal:
            raise AuthorizationError("authenticated IAM principal is missing")
        authorize(service, principal)


def set_principal(principal: str) -> Token[str | None]:
    return _principal.set(principal)


def reset_principal(token: Token[str | None]) -> None:
    _principal.reset(token)
