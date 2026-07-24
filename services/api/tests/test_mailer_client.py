from __future__ import annotations

import json

import pytest
from botocore.credentials import Credentials

from insolvia_api.adapters.aws.mailer_client import (
    MailerRequestError,
    SigV4MailerClient,
)
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.core.mail import links_for, welcome_email

LINKS = links_for(
    "https://www.insolvia.ai",
    unsubscribe_url="https://www.insolvia.ai/unsubscribe?token=v1.abc.def",
)
EMAIL = welcome_email(
    "ada@example.com",
    links=LINKS,
    recipient_name="Ada",
    app_url="https://app.insolvia.ai",
)
EMAIL_WITHOUT_UNSUBSCRIBE = welcome_email(
    "ada@example.com",
    links=links_for("https://www.insolvia.ai"),
    recipient_name="Ada",
    app_url="https://app.insolvia.ai",
)

# --- memory adapter ---------------------------------------------------------


def test_in_memory_mailer_client_records_the_send():
    client = InMemoryMailerClient()

    client.send(EMAIL, idempotency_key="key-123")

    assert client.sent == [(EMAIL, "key-123")]


def test_in_memory_mailer_client_records_multiple_sends():
    client = InMemoryMailerClient()

    client.send(EMAIL, idempotency_key="key-1")
    client.send(EMAIL, idempotency_key="key-2")

    assert [key for _, key in client.sent] == ["key-1", "key-2"]


# --- SigV4 adapter -----------------------------------------------------------


class _FakeResponse:
    def __init__(self, status: int = 200, body: bytes = b"{}") -> None:
        self.status = status
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


@pytest.fixture(autouse=True)
def fake_credentials(monkeypatch):
    # Never touch the real default credential chain (no network, no AWS
    # config file needed) — same shape botocore.session.Session().get_credentials()
    # returns in Lambda.
    fake = Credentials("AKIAFAKEEXAMPLE", "secretsecretsecret", "faketoken")
    monkeypatch.setattr(
        "insolvia_api.adapters.aws.mailer_client.Session.get_credentials",
        lambda self: fake,
    )
    monkeypatch.setenv("AWS_REGION", "us-east-1")


def _make_client(monkeypatch, captured: dict):
    def fake_urlopen(request, *args, **kwargs):
        captured["url"] = request.full_url
        captured["method"] = request.get_method()
        captured["headers"] = dict(request.header_items())
        captured["body"] = request.data
        return _FakeResponse(status=200)

    monkeypatch.setattr(
        "insolvia_api.adapters.aws.mailer_client.urllib.request.urlopen",
        fake_urlopen,
    )
    return SigV4MailerClient("https://mailer-staging.insolvia.ai")


def test_sigv4_client_signs_and_posts_to_the_expected_url(monkeypatch):
    captured: dict = {}
    client = _make_client(monkeypatch, captured)

    client.send(EMAIL, idempotency_key="msg-1")

    assert (
        captured["url"]
        == "https://mailer-staging.insolvia.ai/v1/services/insolvia_api/messages"
    )
    assert captured["method"] == "POST"
    auth_header = captured["headers"].get("Authorization", "")
    assert auth_header.startswith("AWS4-HMAC-SHA256 ")
    assert "Credential=AKIAFAKEEXAMPLE" in auth_header


def test_sigv4_client_posts_the_contract_shaped_body(monkeypatch):
    captured: dict = {}
    client = _make_client(monkeypatch, captured)

    client.send(EMAIL, idempotency_key="msg-42")

    body = json.loads(captured["body"])
    assert body == {
        "schema_version": 1,
        "application_message_id": "msg-42",
        "category": "welcome",
        "message_class": "transactional",
        "to_address": "ada@example.com",
        "subject": "Welcome to Insolvia",
        "html_body": EMAIL.html_body,
        "text_body": EMAIL.text_body,
        "attachments": [],
        "list_unsubscribe_url": LINKS.unsubscribe_url,
    }


def test_sigv4_client_raises_on_non_2xx(monkeypatch):
    def fake_urlopen(request, *args, **kwargs):
        import io
        import urllib.error
        from email.message import Message

        raise urllib.error.HTTPError(
            request.full_url,
            403,
            "Forbidden",
            hdrs=Message(),
            fp=io.BytesIO(b'{"error": "Forbidden"}'),
        )

    monkeypatch.setattr(
        "insolvia_api.adapters.aws.mailer_client.urllib.request.urlopen",
        fake_urlopen,
    )
    client = SigV4MailerClient("https://mailer-staging.insolvia.ai")

    with pytest.raises(MailerRequestError) as excinfo:
        client.send(EMAIL, idempotency_key="msg-1")

    assert excinfo.value.status == 403


def test_sigv4_client_omits_the_unsubscribe_key_when_there_is_no_link(monkeypatch):
    captured: dict = {}
    client = _make_client(monkeypatch, captured)

    client.send(EMAIL_WITHOUT_UNSUBSCRIBE, idempotency_key="msg-43")

    # Omitted, not sent as null: the mailer's request model accepts a missing
    # optional key, and omitting keeps the wire body byte-identical to what a
    # send looked like before unsubscribe links existed.
    assert "list_unsubscribe_url" not in json.loads(captured["body"])


# --- suppression (#80) -------------------------------------------------------


def test_in_memory_mailer_client_records_the_suppression():
    client = InMemoryMailerClient()

    client.suppress("ada@example.com", reason="unsubscribe")

    assert client.suppressed == [("ada@example.com", "unsubscribe")]


def test_sigv4_client_posts_suppressions_to_the_expected_url(monkeypatch):
    captured: dict = {}
    client = _make_client(monkeypatch, captured)

    client.suppress("ada@example.com", reason="unsubscribe")

    assert (
        captured["url"]
        == "https://mailer-staging.insolvia.ai/v1/services/insolvia_api/suppressions"
    )
    assert captured["headers"].get("Authorization", "").startswith("AWS4-HMAC-SHA256 ")
    assert json.loads(captured["body"]) == {
        "schema_version": 1,
        "email_address": "ada@example.com",
        "reason": "unsubscribe",
    }
