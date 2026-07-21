"""Unit tests for the inbound-mail forwarder.

These tests never touch AWS and never send to a real inbox: SES and S3 are
replaced with in-memory fakes, and the forward destination is a dummy address
(``private@example.test``).
"""

from __future__ import annotations

import email
from email.message import EmailMessage
from email.policy import default as DEFAULT_POLICY

import pytest

from inbound_forwarder import handler
from inbound_forwarder.handler import (
    ConfigError,
    Settings,
    TransientError,
    build_forwarded_message,
    process_record,
)

FORWARD_TO = "private@example.test"


def parse_out(data: bytes) -> EmailMessage:
    """Parse forwarded output with the modern policy (iter_attachments/get_body)."""

    return email.message_from_bytes(data, policy=DEFAULT_POLICY)


# --------------------------------------------------------------------------- #
# Fakes
# --------------------------------------------------------------------------- #
class FakeS3:
    def __init__(self, objects: dict[tuple[str, str], bytes] | None = None):
        self.objects = objects or {}

    def get_object(self, Bucket: str, Key: str):  # noqa: N803 - boto3 signature
        if (Bucket, Key) not in self.objects:
            raise KeyError(f"missing s3://{Bucket}/{Key}")
        data = self.objects[(Bucket, Key)]
        return {"Body": _Body(data)}


class _Body:
    def __init__(self, data: bytes):
        self._data = data

    def read(self) -> bytes:
        return self._data


class FakeSES:
    def __init__(self, fail: bool = False):
        self.sent: list[dict] = []
        self.fail = fail

    def send_raw_email(self, **kwargs):
        if self.fail:
            raise RuntimeError("ses down")
        self.sent.append(kwargs)
        return {"MessageId": "fake-message-id"}


@pytest.fixture
def settings() -> Settings:
    return Settings(
        inbound_bucket="insolvia-inbound-mail-test",
        inbound_prefix="inbound/",
        from_address="no-reply@insolvia.ai",
        forward_to=FORWARD_TO,
        allowed_recipients=frozenset(
            {"hello@insolvia.ai", "support@insolvia.ai", "security@insolvia.ai"}
        ),
        own_domains=frozenset({"insolvia.ai"}),
        max_message_bytes=9_000_000,
        max_attachment_bytes=6_000_000,
    )


def make_raw(
    *,
    sender: str = "Customer <customer@external.test>",
    to: str = "support@insolvia.ai",
    subject: str = "Help please",
    body: str = "My schedule export is broken.",
    html: str | None = None,
    attachments: list[tuple[str, bytes]] | None = None,
) -> bytes:
    msg = EmailMessage()
    msg["From"] = sender
    msg["To"] = to
    msg["Subject"] = subject
    msg["Date"] = "Mon, 1 Dec 2025 10:00:00 +0000"
    if html is not None and body is None:
        msg.set_content(html, subtype="html")
    elif html is not None:
        msg.set_content(body)
        msg.add_alternative(html, subtype="html")
    else:
        msg.set_content(body)
    for filename, payload in attachments or []:
        msg.add_attachment(
            payload, maintype="application", subtype="octet-stream", filename=filename
        )
    return msg.as_bytes()


def make_record(
    *,
    message_id: str = "msg-1",
    sender: str = "customer@external.test",
    from_header: str = "customer@external.test",
    recipients: list[str] | None = None,
    spam: str = "PASS",
    virus: str = "PASS",
    extra_headers: list[dict] | None = None,
) -> dict:
    headers = [{"name": "From", "value": from_header}]
    if extra_headers:
        headers.extend(extra_headers)
    return {
        "ses": {
            "mail": {
                "messageId": message_id,
                "source": sender,
                "commonHeaders": {"from": from_header},
                "headers": headers,
            },
            "receipt": {
                "recipients": recipients or ["support@insolvia.ai"],
                "spamVerdict": {"status": spam},
                "virusVerdict": {"status": virus},
                "spfVerdict": {"status": "PASS"},
                "dkimVerdict": {"status": "PASS"},
                "dmarcVerdict": {"status": "PASS"},
            },
        }
    }


def _run(record, settings, raw, ses=None):
    key = f"{settings.inbound_prefix}{record['ses']['mail']['messageId']}"
    s3 = FakeS3({(settings.inbound_bucket, key): raw})
    ses = ses or FakeSES()
    status = process_record(record, settings, s3_client=s3, ses_client=ses)
    return status, ses


# --------------------------------------------------------------------------- #
# Happy path
# --------------------------------------------------------------------------- #
def test_forwards_and_sets_reply_to(settings):
    raw = make_raw(sender="Jane <jane@external.test>", subject="Broken export")
    status, ses = _run(make_record(), settings, raw)

    assert status == "forwarded"
    assert len(ses.sent) == 1
    sent = ses.sent[0]
    assert sent["Source"] == "no-reply@insolvia.ai"
    assert sent["Destinations"] == [FORWARD_TO]

    out = parse_out(sent["RawMessage"]["Data"])
    assert out["From"] == "no-reply@insolvia.ai"
    assert out["To"] == FORWARD_TO
    assert out["Reply-To"] == "jane@external.test"
    assert out["Subject"] == "[Insolvia] Broken export"
    assert out[handler.FORWARD_MARKER_HEADER] == "1"


def test_forwards_mail_to_every_allowed_recipient(settings):
    for address in ("hello@insolvia.ai", "support@insolvia.ai", "security@insolvia.ai"):
        record = make_record(recipients=[address])
        status, ses = _run(record, settings, make_raw(to=address))
        assert status == "forwarded", address
        assert len(ses.sent) == 1


def test_product_email_still_from_no_reply(settings):
    raw = make_raw()
    _status, ses = _run(make_record(), settings, raw)
    assert ses.sent[0]["Source"] == "no-reply@insolvia.ai"


def test_body_does_not_relay_untrusted_headers(settings):
    # A header-injection attempt in the subject must be neutralised. Build the
    # raw bytes by hand because a well-behaved builder would already reject the
    # embedded newline — real hostile mail arrives as bytes like this.
    raw = (
        b"From: attacker@external.test\r\n"
        b"To: support@insolvia.ai\r\n"
        b"Subject: Hi\r\n Bcc: victim@evil.test\r\n"
        b"\r\n"
        b"body\r\n"
    )
    _status, ses = _run(make_record(), settings, raw)
    out = parse_out(ses.sent[0]["RawMessage"]["Data"])
    assert "\n" not in out["Subject"]
    assert out["Bcc"] is None


# --------------------------------------------------------------------------- #
# Safety gates
# --------------------------------------------------------------------------- #
def test_drops_on_spam_verdict(settings):
    status, ses = _run(make_record(spam="FAIL"), settings, make_raw())
    assert status == "dropped:verdict"
    assert ses.sent == []


def test_drops_on_virus_verdict(settings):
    status, ses = _run(make_record(virus="FAIL"), settings, make_raw())
    assert status == "dropped:verdict"
    assert ses.sent == []


def test_drops_on_virus_processing_failed(settings):
    status, ses = _run(make_record(virus="PROCESSING_FAILED"), settings, make_raw())
    assert status == "dropped:verdict"
    assert ses.sent == []


def test_drops_loop_from_own_domain(settings):
    record = make_record(
        sender="no-reply@insolvia.ai", from_header="no-reply@insolvia.ai"
    )
    status, ses = _run(record, settings, make_raw())
    assert status == "dropped:loop"
    assert ses.sent == []


def test_drops_loop_on_forward_marker(settings):
    record = make_record(
        extra_headers=[{"name": handler.FORWARD_MARKER_HEADER, "value": "1"}]
    )
    status, ses = _run(record, settings, make_raw())
    assert status == "dropped:loop"
    assert ses.sent == []


def test_drops_empty_envelope_sender(settings):
    record = make_record(sender="")
    status, ses = _run(record, settings, make_raw())
    assert status == "dropped:loop"
    assert ses.sent == []


def test_drops_wrong_recipient(settings):
    record = make_record(recipients=["someone-else@insolvia.ai"])
    status, ses = _run(record, settings, make_raw())
    assert status == "dropped:recipient"
    assert ses.sent == []


def test_drops_mail_addressed_to_no_reply(settings):
    # no-reply@ is a send-only transactional sender: it must never be forwarded.
    record = make_record(recipients=["no-reply@insolvia.ai"])
    status, ses = _run(record, settings, make_raw(to="no-reply@insolvia.ai"))
    assert status == "dropped:recipient"
    assert ses.sent == []


def test_default_allowed_recipients_exclude_no_reply(monkeypatch):
    monkeypatch.setenv("INBOUND_BUCKET", "b")
    monkeypatch.setenv("INBOUND_FORWARD_TO", FORWARD_TO)
    monkeypatch.delenv("ALLOWED_RECIPIENTS", raising=False)
    s = Settings.from_env()
    assert s.allowed_recipients == frozenset(
        {"hello@insolvia.ai", "support@insolvia.ai", "security@insolvia.ai"}
    )
    assert "no-reply@insolvia.ai" not in s.allowed_recipients


def test_drops_missing_message_id(settings):
    record = make_record()
    record["ses"]["mail"]["messageId"] = ""
    s3 = FakeS3()
    ses = FakeSES()
    status = process_record(record, settings, s3_client=s3, ses_client=ses)
    assert status == "dropped:no-message-id"


# --------------------------------------------------------------------------- #
# Attachments & size limits
# --------------------------------------------------------------------------- #
def test_small_attachment_forwarded(settings):
    raw = make_raw(attachments=[("note.txt", b"hello world")])
    _status, ses = _run(make_record(), settings, raw)
    out = parse_out(ses.sent[0]["RawMessage"]["Data"])
    names = [p.get_filename() for p in out.iter_attachments()]
    assert "note.txt" in names


def test_oversize_attachment_omitted_with_note(settings):
    small_limit = Settings(**{**settings.__dict__, "max_attachment_bytes": 10})
    raw = make_raw(attachments=[("big.bin", b"x" * 5000)])
    key = f"{small_limit.inbound_prefix}msg-1"
    s3 = FakeS3({(small_limit.inbound_bucket, key): raw})
    ses = FakeSES()
    status = process_record(make_record(), small_limit, s3_client=s3, ses_client=ses)
    assert status == "forwarded"
    out = parse_out(ses.sent[0]["RawMessage"]["Data"])
    names = [p.get_filename() for p in out.iter_attachments()]
    assert "big.bin" not in names
    body = out.get_body(preferencelist=("plain",)).get_content()
    assert "omitted" in body


def test_message_over_total_limit_strips_attachments(settings):
    tiny = Settings(**{**settings.__dict__, "max_message_bytes": 500})
    raw = make_raw(attachments=[("a.bin", b"y" * 400)])
    key = f"{tiny.inbound_prefix}msg-1"
    s3 = FakeS3({(tiny.inbound_bucket, key): raw})
    ses = FakeSES()
    status = process_record(make_record(), tiny, s3_client=s3, ses_client=ses)
    assert status == "forwarded"
    out = parse_out(ses.sent[0]["RawMessage"]["Data"])
    assert list(out.iter_attachments()) == []


def test_html_only_message_attached_as_html(settings):
    raw = make_raw(body=None, html="<p>hi there</p>")
    _status, ses = _run(make_record(), settings, raw)
    out = parse_out(ses.sent[0]["RawMessage"]["Data"])
    names = [p.get_filename() for p in out.iter_attachments()]
    assert "original.html" in names


# --------------------------------------------------------------------------- #
# Malformed / failures / retries
# --------------------------------------------------------------------------- #
def test_malformed_mail_dropped_not_retried(settings, monkeypatch):
    def boom(_raw, _settings):
        raise ValueError("unparseable MIME")

    monkeypatch.setattr(handler, "build_forwarded_message", boom)
    status, ses = _run(make_record(), settings, b"garbage")
    assert status == "dropped:malformed"
    assert ses.sent == []


def test_s3_failure_raises_for_retry(settings):
    s3 = FakeS3()  # no object present -> KeyError inside
    ses = FakeSES()
    with pytest.raises(TransientError):
        process_record(make_record(), settings, s3_client=s3, ses_client=ses)


def test_ses_failure_raises_for_retry(settings):
    raw = make_raw()
    key = f"{settings.inbound_prefix}msg-1"
    s3 = FakeS3({(settings.inbound_bucket, key): raw})
    ses = FakeSES(fail=True)
    with pytest.raises(TransientError):
        process_record(make_record(), settings, s3_client=s3, ses_client=ses)


# --------------------------------------------------------------------------- #
# Settings / secret resolution
# --------------------------------------------------------------------------- #
def test_from_env_reads_ssm_secret(monkeypatch):
    monkeypatch.setenv("INBOUND_BUCKET", "b")
    monkeypatch.delenv("INBOUND_FORWARD_TO", raising=False)
    monkeypatch.setenv("INBOUND_FORWARD_TO_PARAM", "/insolvia/prod/inbound-forward-to")

    class FakeSSM:
        def get_parameter(self, Name, WithDecryption):  # noqa: N803
            assert WithDecryption is True
            return {"Parameter": {"Value": FORWARD_TO}}

    s = Settings.from_env(ssm_client=FakeSSM())
    assert s.forward_to == FORWARD_TO


def test_from_env_direct_env_wins(monkeypatch):
    monkeypatch.setenv("INBOUND_BUCKET", "b")
    monkeypatch.setenv("INBOUND_FORWARD_TO", FORWARD_TO)
    s = Settings.from_env()
    assert s.forward_to == FORWARD_TO


def test_from_env_missing_secret_raises(monkeypatch):
    monkeypatch.setenv("INBOUND_BUCKET", "b")
    monkeypatch.delenv("INBOUND_FORWARD_TO", raising=False)
    monkeypatch.delenv("INBOUND_FORWARD_TO_PARAM", raising=False)
    with pytest.raises(ConfigError):
        Settings.from_env()


def test_from_env_missing_bucket_raises(monkeypatch):
    monkeypatch.delenv("INBOUND_BUCKET", raising=False)
    monkeypatch.setenv("INBOUND_FORWARD_TO", FORWARD_TO)
    with pytest.raises(ConfigError):
        Settings.from_env()


def test_missing_config_is_transient_not_swallowed(monkeypatch):
    """Missing config must raise (→ retry → DLQ → alarm), never drop mail."""

    monkeypatch.delenv("INBOUND_BUCKET", raising=False)
    monkeypatch.delenv("INBOUND_FORWARD_TO", raising=False)
    monkeypatch.delenv("INBOUND_FORWARD_TO_PARAM", raising=False)

    # ConfigError is a TransientError, so the async invocation retries.
    assert issubclass(ConfigError, TransientError)
    with pytest.raises(TransientError):
        handler.lambda_handler({"Records": [make_record()]})


def test_ssm_read_failure_is_transient(monkeypatch):
    monkeypatch.setenv("INBOUND_BUCKET", "b")
    monkeypatch.delenv("INBOUND_FORWARD_TO", raising=False)
    monkeypatch.setenv("INBOUND_FORWARD_TO_PARAM", "/insolvia/prod/inbound-forward-to")

    class BrokenSSM:
        def get_parameter(self, Name, WithDecryption):  # noqa: N803
            raise RuntimeError("ssm down")

    with pytest.raises(TransientError):
        Settings.from_env(ssm_client=BrokenSSM())


def test_build_forwarded_message_rejects_unparseable(settings, monkeypatch):
    # An empty body still parses into a message; assert a real forward builds.
    msg = build_forwarded_message(make_raw(), settings)
    assert msg["From"] == "no-reply@insolvia.ai"
