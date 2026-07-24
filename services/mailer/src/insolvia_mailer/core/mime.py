from __future__ import annotations

from dataclasses import dataclass
from email.message import EmailMessage
from email.policy import SMTP

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.models import MAX_SES_MESSAGE_BYTES, MessageRequest


@dataclass(frozen=True)
class AttachmentContent:
    attachment_id: str
    file_name: str
    content_type: str
    disposition: str
    content_id: str | None
    data: bytes


def build_message(
    service: ServiceConfig,
    request: MessageRequest,
    attachments: list[AttachmentContent],
) -> EmailMessage:
    expected = [item.attachment_id for item in request.attachments]
    actual = [item.attachment_id for item in attachments]
    if expected != actual:
        raise ValidationError("attachment content does not match the request order")

    message = EmailMessage(policy=SMTP)
    message["From"] = service.from_address
    message["To"] = request.to_address
    message["Subject"] = request.subject
    message["X-Mailer-Service-Id"] = service.service_id
    message["X-Mailer-Application-Message-Id"] = request.application_message_id
    message["X-Mailer-Category"] = request.category
    message.set_content(request.text_body)
    message.add_alternative(request.html_body, subtype="html")
    html_part = message.get_payload()[-1]

    for attachment in attachments:
        maintype, subtype = attachment.content_type.split("/", 1)
        if attachment.content_id:
            html_part.add_related(
                attachment.data,
                maintype=maintype,
                subtype=subtype,
                filename=attachment.file_name,
                disposition="inline",
                cid=f"<{attachment.content_id}>",
            )
        else:
            message.add_attachment(
                attachment.data,
                maintype=maintype,
                subtype=subtype,
                filename=attachment.file_name,
                disposition="attachment",
            )

    if len(message.as_bytes()) > MAX_SES_MESSAGE_BYTES:
        raise ValidationError("encoded message exceeds the SES 40 MiB limit")
    return message
