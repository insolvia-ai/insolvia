from __future__ import annotations

from email.message import EmailMessage
from typing import Any, Protocol

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.models import AttachmentUploadRequest, MessageRequest


class RequestAuthorizer(Protocol):
    def authorize(self, service: ServiceConfig) -> None: ...


class MailerStore(Protocol):
    def register_attachment(
        self,
        service: ServiceConfig,
        upload: AttachmentUploadRequest,
        *,
        base_url: str | None = None,
    ) -> dict[str, Any]: ...

    def admit_message(
        self, service: ServiceConfig, message: MessageRequest
    ) -> None: ...


class MailTransport(Protocol):
    def send(self, message: EmailMessage) -> None: ...


class AttachmentReceiver(Protocol):
    def put_upload(
        self, token: str, data: bytes, content_type: str, checksum: str
    ) -> None: ...
