import hashlib
import importlib
import sys
import time

import pytest


class FakeSmtp:
    messages = []

    def __init__(self, *_args, **_kwargs):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def send_message(self, message):
        self.messages.append(message)


@pytest.fixture
def gateway(monkeypatch):
    monkeypatch.setenv("MAILER_RUNTIME", "development")
    monkeypatch.setattr("smtplib.SMTP", FakeSmtp)
    sys.modules.pop("insolvia_mailer.entrypoints.development_server", None)
    module = importlib.import_module("insolvia_mailer.entrypoints.development_server")
    FakeSmtp.messages.clear()
    return module


def message(message_id="ins_welcome_123", attachments=None):
    return {
        "schema_version": 1,
        "application_message_id": message_id,
        "category": "welcome",
        "message_class": "transactional",
        "to_address": "recipient@example.com",
        "subject": "Welcome",
        "html_body": "<p>Hello</p>",
        "text_body": "Hello",
        "attachments": attachments or [],
    }


def wait_for_messages(count):
    for _ in range(100):
        if len(FakeSmtp.messages) >= count:
            return
        time.sleep(0.01)
    raise AssertionError("development delivery worker did not capture the message")


def test_development_submission_is_captured_once(gateway):
    client = gateway.app.test_client()

    first = client.post("/v1/services/insolvia_api/messages", json=message())
    retry = client.post("/v1/services/insolvia_api/messages", json=message())

    assert first.status_code == 202
    assert retry.status_code == 202
    wait_for_messages(1)
    assert len(FakeSmtp.messages) == 1
    assert FakeSmtp.messages[0]["To"] == "recipient@example.com"


def test_development_submission_rejects_conflicting_id(gateway):
    client = gateway.app.test_client()
    assert (
        client.post("/v1/services/insolvia_api/messages", json=message()).status_code
        == 202
    )

    response = client.post(
        "/v1/services/insolvia_api/messages", json=message() | {"subject": "Changed"}
    )

    assert response.status_code == 409


def test_development_attachment_upload_and_capture(gateway):
    client = gateway.app.test_client()
    content = b"pdf-content"
    digest = hashlib.sha256(content).hexdigest()
    registration = client.post(
        "/v1/services/insolvia_api/attachment-uploads",
        json={
            "schema_version": 1,
            "application_message_id": "ins_welcome_attachment",
            "file_name": "guide.pdf",
            "content_type": "application/pdf",
            "size_bytes": len(content),
            "sha256": digest,
        },
    )
    assert registration.status_code == 201
    body = registration.get_json()
    upload_path = body["upload_url"].removeprefix("http://localhost")
    uploaded = client.put(
        upload_path,
        data=content,
        headers={"content-type": "application/pdf", "x-mailer-content-sha256": digest},
    )
    assert uploaded.status_code == 204

    response = client.post(
        "/v1/services/insolvia_api/messages",
        json=message(
            "ins_welcome_attachment",
            [
                {
                    "attachment_id": body["attachment_id"],
                    "disposition": "attachment",
                    "content_id": None,
                }
            ],
        ),
    )

    assert response.status_code == 202
    wait_for_messages(1)
    attachment = next(FakeSmtp.messages[0].iter_attachments())
    assert attachment.get_payload(decode=True) == content


def test_unknown_service_is_rejected(gateway):
    response = gateway.app.test_client().post(
        "/v1/services/other_service/messages", json=message()
    )
    assert response.status_code == 403
