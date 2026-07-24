import hashlib

import pytest

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.models import AttachmentUploadRequest, MessageRequest


@pytest.fixture
def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset({"welcome"}),
        allowed_message_classes=frozenset({"transactional"}),
    )


def message(**overrides):
    value = {
        "schema_version": 1,
        "application_message_id": "ins_welcome_123",
        "category": "welcome",
        "message_class": "transactional",
        "to_address": "recipient@example.com",
        "subject": "Welcome",
        "html_body": "<p>Hello</p>",
        "text_body": "Hello",
        "attachments": [],
    }
    value.update(overrides)
    return value


def test_message_parses_and_hash_is_deterministic(service):
    first = MessageRequest.from_dict(message(), service)
    second = MessageRequest.from_dict(message(), service)

    assert first.canonical_hash() == second.canonical_hash()
    assert first.to_address == "recipient@example.com"


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("schema_version", 2),
        ("to_address", "Name <recipient@example.com>"),
        ("to_address", "one@example.com, two@example.com"),
        ("category", "marketing"),
        ("message_class", "arbitrary"),
        ("subject", "Hello\r\nBcc: victim@example.com"),
    ],
)
def test_message_rejects_invalid_contract(service, field, value):
    with pytest.raises(ValidationError):
        MessageRequest.from_dict(message(**{field: value}), service)


def test_message_rejects_caller_controlled_routing(service):
    with pytest.raises(ValidationError, match="unsupported fields"):
        MessageRequest.from_dict(message(service_id="storybook"), service)


def test_inline_attachment_requires_content_id(service):
    with pytest.raises(ValidationError, match="content_id"):
        MessageRequest.from_dict(
            message(
                attachments=[
                    {
                        "attachment_id": "att_123",
                        "disposition": "inline",
                        "content_id": None,
                    }
                ]
            ),
            service,
        )


def test_upload_blocks_executable_extension():
    with pytest.raises(ValidationError, match="blocked"):
        AttachmentUploadRequest.from_dict(
            {
                "schema_version": 1,
                "application_message_id": "ins_welcome_123",
                "file_name": "payload.exe",
                "content_type": "application/octet-stream",
                "size_bytes": 3,
                "sha256": hashlib.sha256(b"bad").hexdigest(),
            }
        )


def test_upload_rejects_path_filename():
    with pytest.raises(ValidationError, match="plain filename"):
        AttachmentUploadRequest.from_dict(
            {
                "schema_version": 1,
                "application_message_id": "ins_welcome_123",
                "file_name": "../guide.pdf",
                "content_type": "application/pdf",
                "size_bytes": 3,
                "sha256": hashlib.sha256(b"pdf").hexdigest(),
            }
        )
