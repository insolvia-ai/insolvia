from __future__ import annotations

from insolvia_mailer.core.config import ServiceConfig


class RegisteredServiceAuthorizer:
    """Allows requests only after the API resolves a registered service."""

    def authorize(self, service: ServiceConfig) -> None:
        return None
