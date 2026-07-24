from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.mime import AttachmentContent, build_message
from insolvia_mailer.core.models import MessageRequest


def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset({"welcome"}),
        allowed_message_classes=frozenset({"transactional"}),
    )


def request(attachments=None):
    return MessageRequest.from_dict(
        {
            "schema_version": 1,
            "application_message_id": "ins_welcome_123",
            "category": "welcome",
            "message_class": "transactional",
            "to_address": "recipient@example.com",
            "subject": "Welcome",
            "html_body": "<p>Hello</p>",
            "text_body": "Hello",
            "attachments": attachments or [],
        },
        service(),
    )


def test_message_contains_text_html_and_privacy_safe_diagnostics():
    value = build_message(service(), request(), [])

    assert value["From"] == "Insolvia <no-reply@insolvia.ai>"
    assert value["X-Mailer-Service-Id"] == "insolvia_api"
    assert (
        value.get_body(preferencelist=("html",)).get_content().strip() == "<p>Hello</p>"
    )
    assert value.get_body(preferencelist=("plain",)).get_content().strip() == "Hello"


def test_message_contains_attachment():
    value = build_message(
        service(),
        request(
            [
                {
                    "attachment_id": "att_123",
                    "disposition": "attachment",
                    "content_id": None,
                }
            ]
        ),
        [
            AttachmentContent(
                attachment_id="att_123",
                file_name="guide.pdf",
                content_type="application/pdf",
                disposition="attachment",
                content_id=None,
                data=b"pdf",
            )
        ],
    )

    attachment = next(value.iter_attachments())
    assert attachment.get_filename() == "guide.pdf"
    assert attachment.get_payload(decode=True) == b"pdf"
