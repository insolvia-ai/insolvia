import pytest

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.mime import AttachmentContent, build_message
from insolvia_mailer.core.models import MessageRequest

UNSUBSCRIBE_URL = "https://www.insolvia.ai/unsubscribe?token=abc.def"


def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset({"welcome"}),
        allowed_message_classes=frozenset({"transactional"}),
    )


def request(attachments=None, **overrides):
    payload = {
        "schema_version": 1,
        "application_message_id": "ins_welcome_123",
        "category": "welcome",
        "message_class": "transactional",
        "to_address": "recipient@example.com",
        "subject": "Welcome",
        "html_body": "<p>Hello</p>",
        "text_body": "Hello",
        "attachments": attachments or [],
    }
    payload.update(overrides)
    return MessageRequest.from_dict(payload, service())


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


# ── List-Unsubscribe (RFC 2369 / RFC 8058), issue #80 ────────────────


def test_omits_unsubscribe_headers_when_no_url_is_given():
    value = build_message(service(), request(), [])

    # Advertising one-click support without a URL would make a mail client
    # render an Unsubscribe button whose click goes nowhere.
    assert "List-Unsubscribe" not in value
    assert "List-Unsubscribe-Post" not in value


def test_emits_both_unsubscribe_headers_when_a_url_is_given():
    value = build_message(service(), request(list_unsubscribe_url=UNSUBSCRIBE_URL), [])

    assert value["List-Unsubscribe"] == f"<{UNSUBSCRIBE_URL}>"
    assert value["List-Unsubscribe-Post"] == "List-Unsubscribe=One-Click"


@pytest.mark.parametrize(
    "url",
    [
        "http://www.insolvia.ai/unsubscribe",
        "javascript:alert(1)",
        "/unsubscribe",
    ],
)
def test_rejects_a_non_https_unsubscribe_url(url):
    with pytest.raises(ValidationError, match="absolute https URL"):
        request(list_unsubscribe_url=url)


def test_rejects_header_injection_through_the_unsubscribe_url():
    # The URL lands verbatim in a header, so a newline would let a caller
    # append headers of its own choosing to the outgoing message.
    with pytest.raises(ValidationError, match="control characters"):
        request(list_unsubscribe_url="https://x.example\r\nBcc: attacker@example.com")


def test_rejects_angle_brackets_in_the_unsubscribe_url():
    # The header wraps the URL in <>; a URL carrying its own would let a
    # caller smuggle a second List-Unsubscribe target past the wrapper.
    with pytest.raises(ValidationError, match="angle brackets"):
        request(list_unsubscribe_url="https://x.example/a>, <mailto:x@example.com")
