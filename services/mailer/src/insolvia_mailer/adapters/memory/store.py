from __future__ import annotations

import hashlib
import queue
import secrets
import threading
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ConflictError, ValidationError
from insolvia_mailer.core.mime import AttachmentContent
from insolvia_mailer.core.models import AttachmentUploadRequest, MessageRequest


@dataclass
class MemoryUpload:
    service_id: str
    attachment_id: str
    token: str
    request: AttachmentUploadRequest
    expires_at: datetime
    data: bytes | None = None


@dataclass(frozen=True)
class MemoryDelivery:
    service: ServiceConfig
    request: MessageRequest
    attachments: list[AttachmentContent]


class MemoryStore:
    """Ephemeral development replacements for S3, DynamoDB, and SQS."""

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.uploads: dict[str, MemoryUpload] = {}
        self.messages: dict[str, str] = {}
        self.deliveries: queue.Queue[MemoryDelivery] = queue.Queue()

    def _register_upload(
        self, service_id: str, upload_request: AttachmentUploadRequest
    ) -> MemoryUpload:
        attachment_id = f"att_{secrets.token_urlsafe(18)}"
        token = secrets.token_urlsafe(32)
        upload = MemoryUpload(
            service_id=service_id,
            attachment_id=attachment_id,
            token=token,
            request=upload_request,
            expires_at=datetime.now(UTC) + timedelta(minutes=15),
        )
        with self.lock:
            self.uploads[token] = upload
        return upload

    def register_attachment(
        self,
        service: ServiceConfig,
        upload: AttachmentUploadRequest,
        *,
        base_url: str | None = None,
    ) -> dict[str, object]:
        if not base_url:
            raise ValidationError(
                "development attachment uploads require an API base URL"
            )
        registered = self._register_upload(service.service_id, upload)
        return {
            "schema_version": 1,
            "attachment_id": registered.attachment_id,
            "upload_url": f"{base_url}/v1/development-uploads/{registered.token}",
            "required_headers": {
                "content-type": registered.request.content_type,
                "x-mailer-content-sha256": registered.request.sha256,
            },
            "expires_at": registered.expires_at.isoformat().replace("+00:00", "Z"),
        }

    def put_upload(
        self, token: str, data: bytes, content_type: str, checksum: str
    ) -> None:
        with self.lock:
            upload = self.uploads.get(token)
            if not upload or upload.expires_at < datetime.now(UTC):
                raise ValidationError("upload URL is invalid or expired")
            if content_type != upload.request.content_type:
                raise ValidationError("content-type does not match upload registration")
            if len(data) != upload.request.size_bytes:
                raise ValidationError(
                    "uploaded size does not match upload registration"
                )
            actual = hashlib.sha256(data).hexdigest()
            if actual != upload.request.sha256 or checksum != actual:
                raise ValidationError(
                    "uploaded checksum does not match upload registration"
                )
            upload.data = data

    def attachments_for(
        self, service_id: str, message: MessageRequest
    ) -> list[AttachmentContent]:
        result: list[AttachmentContent] = []
        with self.lock:
            candidates = list(self.uploads.values())
            for reference in message.attachments:
                upload = next(
                    (
                        item
                        for item in candidates
                        if item.attachment_id == reference.attachment_id
                        and item.service_id == service_id
                        and item.request.application_message_id
                        == message.application_message_id
                    ),
                    None,
                )
                if not upload or upload.data is None:
                    raise ValidationError("attachment upload is missing or incomplete")
                result.append(
                    AttachmentContent(
                        attachment_id=reference.attachment_id,
                        file_name=upload.request.file_name,
                        content_type=upload.request.content_type,
                        disposition=reference.disposition,
                        content_id=reference.content_id,
                        data=upload.data,
                    )
                )
        return result

    def admit_message(self, service: ServiceConfig, message: MessageRequest) -> None:
        message_key = f"{service.service_id}#{message.application_message_id}"
        request_hash = message.canonical_hash()
        attachments = self.attachments_for(service.service_id, message)
        with self.lock:
            existing = self.messages.get(message_key)
            if existing and existing != request_hash:
                raise ConflictError(
                    "application_message_id was reused with different content"
                )
            if existing:
                return
            self.messages[message_key] = request_hash
        self.deliveries.put(MemoryDelivery(service, message, attachments))
