from __future__ import annotations

from email.message import EmailMessage
from typing import Any, Protocol

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.models import (
    AttachmentUploadRequest,
    MessageRequest,
    SuppressionRequest,
)


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

    def suppress_recipient(
        self, service: ServiceConfig, suppression: SuppressionRequest
    ) -> None:
        """Stop sending to this address. Idempotent — suppressing an already
        suppressed address is a no-op success, because the caller (a person
        clicking unsubscribe twice, a mail client retrying a one-click POST)
        has no way to know the difference and should not be shown an error."""
        ...


class MailTransport(Protocol):
    def send(self, message: EmailMessage) -> None: ...


class AttachmentReceiver(Protocol):
    def put_upload(
        self, token: str, data: bytes, content_type: str, checksum: str
    ) -> None: ...
