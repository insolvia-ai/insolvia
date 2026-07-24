from __future__ import annotations

from dataclasses import dataclass

from flask import current_app

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import AuthorizationError
from insolvia_mailer.core.ports import (
    AttachmentReceiver,
    MailerStore,
    RequestAuthorizer,
)


@dataclass(frozen=True)
class ApiDependencies:
    services: dict[str, ServiceConfig]
    store: MailerStore
    authorizer: RequestAuthorizer
    attachment_receiver: AttachmentReceiver | None = None


def dependencies() -> ApiDependencies:
    return current_app.extensions["mailer_dependencies"]


def authorized_service(service_id: str) -> ServiceConfig:
    configured = dependencies()
    service = configured.services.get(service_id)
    if not service:
        raise AuthorizationError("service route is not registered")
    configured.authorizer.authorize(service)
    return service
