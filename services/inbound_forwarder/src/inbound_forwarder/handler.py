"""SES inbound receipt-rule handler that forwards inbound mail.

Flow
----
1. SES accepts mail for the inbound insolvia.ai addresses (``hello@``,
   ``support@``, ``security@``), stores the raw MIME in S3, and invokes this
   Lambda asynchronously with the SES receipt event.
2. The handler applies safety gates (spam/virus verdicts, mail-loop detection,
   allowed-recipient check) before touching the message body.
3. It fetches the raw MIME from S3, parses it with the stdlib ``email`` package,
   and constructs a **brand-new** message. Untrusted headers are never relayed;
   only a sanitised subject, the decoded body, and size-bounded attachments are
   copied across.
4. The forwarded message is sent via SES from ``no-reply@insolvia.ai`` to the
   private destination (read from a secret), with the original sender exposed as
   ``Reply-To`` so the owner can reply directly.

``no-reply@insolvia.ai`` is send-only: it is deliberately absent from the
allowed-recipient list, so mail addressed to it is dropped rather than forwarded.

Failure model
-------------
* Permanent, expected drops (spam, virus, loop, wrong recipient, malformed mail)
  are logged and swallowed so SES/Lambda does not retry them.
* Transient failures (S3 read, SES send, missing secret) raise, so the async
  Lambda invocation retries and — after exhausting retries — lands on the DLQ,
  which is alarmed.
"""

from __future__ import annotations

import email
import email.utils
import logging
import os
from dataclasses import dataclass
from email.message import EmailMessage
from email.policy import default as DEFAULT_POLICY
from typing import Any, Iterable

logger = logging.getLogger()
logger.setLevel(logging.INFO)

# Header stamped on every message we send so a reply/auto-responder loop that
# comes back through an inbound address can be detected and dropped.
FORWARD_MARKER_HEADER = "X-Insolvia-Forwarded"

# Inbound addresses SES accepts mail for. ``no-reply@insolvia.ai`` is a
# send-only transactional sender and is deliberately NOT in this list.
DEFAULT_ALLOWED_RECIPIENTS = (
    "hello@insolvia.ai,support@insolvia.ai,security@insolvia.ai"
)


class TransientError(Exception):
    """Raised for failures that should retry (and eventually hit the DLQ)."""


class ConfigError(TransientError):
    """Raised when required configuration/secret is missing.

    Modelled as transient so the misconfiguration surfaces on the DLQ + alarm
    rather than silently discarding a customer's mail.
    """


@dataclass(frozen=True)
class Settings:
    inbound_bucket: str
    inbound_prefix: str
    from_address: str
    forward_to: str
    allowed_recipients: frozenset[str]
    own_domains: frozenset[str]
    max_message_bytes: int
    max_attachment_bytes: int

    @staticmethod
    def _clean_list(raw: str) -> list[str]:
        return [item.strip().lower() for item in raw.split(",") if item.strip()]

    @classmethod
    def from_env(cls, *, ssm_client=None) -> "Settings":
        bucket = os.environ.get("INBOUND_BUCKET", "").strip()
        if not bucket:
            raise ConfigError("INBOUND_BUCKET is not configured")

        forward_to = _resolve_forward_to(ssm_client=ssm_client)

        allowed = cls._clean_list(
            os.environ.get("ALLOWED_RECIPIENTS", DEFAULT_ALLOWED_RECIPIENTS)
        )
        own = cls._clean_list(os.environ.get("OWN_DOMAINS", "insolvia.ai"))

        return cls(
            inbound_bucket=bucket,
            inbound_prefix=os.environ.get("INBOUND_PREFIX", "inbound/"),
            from_address=os.environ.get("FROM_ADDRESS", "no-reply@insolvia.ai"),
            forward_to=forward_to,
            allowed_recipients=frozenset(allowed),
            own_domains=frozenset(own),
            # SES caps a single message at 10 MB; stay under that after MIME
            # framing overhead.
            max_message_bytes=int(os.environ.get("MAX_MESSAGE_BYTES", "9000000")),
            max_attachment_bytes=int(os.environ.get("MAX_ATTACHMENT_BYTES", "6000000")),
        )


def _resolve_forward_to(*, ssm_client=None) -> str:
    """Read the private destination from env or an SSM SecureString.

    The real address is a human-provided secret and must never be committed.
    ``INBOUND_FORWARD_TO`` (direct) wins for local/test use; production wires
    ``INBOUND_FORWARD_TO_PARAM`` to an SSM SecureString path.
    """

    direct = os.environ.get("INBOUND_FORWARD_TO", "").strip()
    if direct:
        return direct

    param_name = os.environ.get("INBOUND_FORWARD_TO_PARAM", "").strip()
    if not param_name:
        raise ConfigError(
            "No forward destination configured: set INBOUND_FORWARD_TO or "
            "INBOUND_FORWARD_TO_PARAM"
        )

    client = ssm_client if ssm_client is not None else _boto3_client("ssm")
    try:
        resp = client.get_parameter(Name=param_name, WithDecryption=True)
    except Exception as exc:  # noqa: BLE001 - surface as transient for retry
        raise TransientError(f"Unable to read secret {param_name}: {exc}") from exc

    value = (resp.get("Parameter", {}).get("Value") or "").strip()
    if not value:
        raise ConfigError(f"Secret {param_name} is empty")
    return value


def _boto3_client(service: str):
    # Imported lazily so unit tests can run without boto3/network and so import
    # time stays cheap. boto3 uses the Lambda execution role automatically.
    import boto3

    return boto3.client(service, region_name=os.environ.get("AWS_REGION", "us-east-1"))


# --------------------------------------------------------------------------- #
# Safety gates
# --------------------------------------------------------------------------- #
def _verdict(receipt: dict, key: str) -> str:
    return str((receipt.get(key) or {}).get("status", "")).upper()


def _is_blocked_by_verdicts(receipt: dict) -> str | None:
    """Return a reason string if spam/virus scanning says to drop the mail."""

    if _verdict(receipt, "spamVerdict") == "FAIL":
        return "spam verdict FAIL"
    if _verdict(receipt, "virusVerdict") in {"FAIL", "PROCESSING_FAILED"}:
        return "virus verdict not clean"
    return None


def _addresses(value: str | None) -> list[str]:
    if not value:
        return []
    return [addr.lower() for _name, addr in email.utils.getaddresses([value]) if addr]


def _domain_of(address: str) -> str:
    return address.split("@")[-1].lower() if "@" in address else ""


def _is_loop(mail: dict, settings: Settings) -> str | None:
    """Detect mail we sent (or already forwarded) to avoid infinite loops."""

    headers = {
        h.get("name", "").lower(): h.get("value", "") for h in mail.get("headers", [])
    }
    if FORWARD_MARKER_HEADER.lower() in headers:
        return "already carries the forward marker"

    # Envelope MAIL FROM. Empty (<>) means a bounce/auto-reply — never forward.
    source = (mail.get("source") or "").strip().lower()
    if source == "":
        return "empty envelope sender (bounce/auto-reply)"
    if _domain_of(source) in settings.own_domains:
        return f"envelope sender is one of our own domains ({source})"

    from_addrs = _addresses(mail.get("commonHeaders", {}).get("from"))
    if any(_domain_of(a) in settings.own_domains for a in from_addrs):
        return "From header is one of our own domains"

    return None


def _recipients_ok(receipt: dict, settings: Settings) -> bool:
    recipients = {r.lower() for r in receipt.get("recipients", [])}
    return bool(recipients & settings.allowed_recipients)


# --------------------------------------------------------------------------- #
# MIME parsing + safe rebuild
# --------------------------------------------------------------------------- #
def _sanitize_header(value: str, *, max_len: int = 400) -> str:
    """Strip CR/LF (header-injection defence) and clamp length."""

    cleaned = (value or "").replace("\r", " ").replace("\n", " ").strip()
    return cleaned[:max_len]


def _extract_body(parsed: EmailMessage) -> tuple[str, str | None]:
    """Return ``(plain_text, html)`` best-effort from the parsed message."""

    plain = ""
    html = None
    try:
        plain_part = parsed.get_body(preferencelist=("plain",))
        if plain_part is not None:
            plain = plain_part.get_content()
    except Exception:  # noqa: BLE001 - malformed encodings must not crash us
        plain = ""

    try:
        html_part = parsed.get_body(preferencelist=("html",))
        if html_part is not None:
            html = html_part.get_content()
    except Exception:  # noqa: BLE001
        html = None

    if not plain and html is None:
        # Fall back to any decodable payload without trusting the structure.
        try:
            payload = parsed.get_payload(decode=True)
            if payload:
                plain = payload.decode("utf-8", errors="replace")
        except Exception:  # noqa: BLE001
            plain = ""
    return plain, html


def _iter_safe_attachments(
    parsed: EmailMessage, settings: Settings
) -> tuple[list[tuple[str, str, str, bytes]], list[str]]:
    """Collect attachments within size budget; return (kept, notes)."""

    kept: list[tuple[str, str, str, bytes]] = []
    notes: list[str] = []
    budget = settings.max_message_bytes
    used = 0

    try:
        attachments = list(parsed.iter_attachments())
    except Exception:  # noqa: BLE001
        return [], ["attachments could not be enumerated (malformed MIME)"]

    for part in attachments:
        filename = part.get_filename() or "attachment"
        try:
            payload = part.get_payload(decode=True) or b""
        except Exception:  # noqa: BLE001
            notes.append(f"{filename}: skipped (could not decode)")
            continue

        size = len(payload)
        if size > settings.max_attachment_bytes:
            notes.append(f"{filename}: omitted ({size} bytes exceeds per-file limit)")
            continue
        if used + size > budget:
            notes.append(f"{filename}: omitted (total size limit reached)")
            continue

        ctype = part.get_content_type()
        maintype, _, subtype = ctype.partition("/")
        kept.append(
            (maintype or "application", subtype or "octet-stream", filename, payload)
        )
        used += size

    return kept, notes


def build_forwarded_message(raw_bytes: bytes, settings: Settings) -> EmailMessage:
    """Parse ``raw_bytes`` and return a fresh, safe forwarded message.

    Raises ``ValueError`` if the raw payload cannot be parsed at all.
    """

    try:
        parsed = email.message_from_bytes(raw_bytes, policy=DEFAULT_POLICY)
    except Exception as exc:  # noqa: BLE001
        raise ValueError(f"unparseable MIME: {exc}") from exc

    orig_from = _sanitize_header(parsed.get("From", ""))
    orig_to = _sanitize_header(parsed.get("To", ""))
    orig_date = _sanitize_header(parsed.get("Date", ""))
    orig_subject = _sanitize_header(parsed.get("Subject", "(no subject)"))

    reply_to = ""
    _name, reply_addr = email.utils.parseaddr(parsed.get("From", ""))
    reply_to = _sanitize_header(reply_addr, max_len=320)

    plain, html = _extract_body(parsed)
    attachments, notes = _iter_safe_attachments(parsed, settings)

    header_block = (
        "Forwarded message received at insolvia.ai\n"
        f"From:    {orig_from}\n"
        f"To:      {orig_to}\n"
        f"Date:    {orig_date}\n"
        f"Subject: {orig_subject}\n"
        "----------------------------------------------------------------\n\n"
    )
    body_text = plain if plain else "(no plain-text body; see attachments/notes)"
    footer = ""
    if html is not None and not plain:
        footer += "\n\n[Original message was HTML-only; attached as original.html]"
    if notes:
        footer += "\n\n[Attachment handling]\n" + "\n".join(f"- {n}" for n in notes)

    message = EmailMessage()
    message["From"] = settings.from_address
    message["To"] = settings.forward_to
    if reply_to:
        message["Reply-To"] = reply_to
    message["Subject"] = _sanitize_header(f"[Insolvia] {orig_subject}")
    message[FORWARD_MARKER_HEADER] = "1"
    message["Auto-Submitted"] = "auto-forwarded"
    message.set_content(header_block + body_text + footer)

    if html is not None and not plain:
        message.add_attachment(
            html.encode("utf-8"),
            maintype="text",
            subtype="html",
            filename="original.html",
        )

    for maintype, subtype, filename, payload in attachments:
        message.add_attachment(
            payload, maintype=maintype, subtype=subtype, filename=filename
        )

    # Final safety net: if the assembled message is still too large, drop
    # attachments entirely and note it, so we never bounce off the SES size cap.
    if len(message.as_bytes()) > settings.max_message_bytes:
        message = _strip_attachments(message, settings, header_block, body_text)

    return message


def _strip_attachments(
    original: EmailMessage,
    settings: Settings,
    header_block: str,
    body_text: str,
) -> EmailMessage:
    stripped = EmailMessage()
    for key in (
        "From",
        "To",
        "Reply-To",
        "Subject",
        FORWARD_MARKER_HEADER,
        "Auto-Submitted",
    ):
        if original[key] is not None:
            stripped[key] = original[key]
    stripped.set_content(
        header_block
        + body_text
        + "\n\n[Attachments omitted: forwarded message exceeded the size limit]"
    )
    return stripped


# --------------------------------------------------------------------------- #
# Lambda entrypoint
# --------------------------------------------------------------------------- #
def _s3_key_for(message_id: str, settings: Settings) -> str:
    return f"{settings.inbound_prefix}{message_id}"


def _fetch_raw(message_id: str, settings: Settings, *, s3_client) -> bytes:
    key = _s3_key_for(message_id, settings)
    try:
        obj = s3_client.get_object(Bucket=settings.inbound_bucket, Key=key)
        return obj["Body"].read()
    except Exception as exc:  # noqa: BLE001
        raise TransientError(
            f"Unable to read raw message s3://{settings.inbound_bucket}/{key}: {exc}"
        ) from exc


def _send(message: EmailMessage, settings: Settings, *, ses_client) -> None:
    try:
        ses_client.send_raw_email(
            Source=settings.from_address,
            Destinations=[settings.forward_to],
            RawMessage={"Data": message.as_bytes()},
        )
    except Exception as exc:  # noqa: BLE001
        raise TransientError(f"SES send_raw_email failed: {exc}") from exc


def process_record(
    record: dict,
    settings: Settings,
    *,
    s3_client,
    ses_client,
) -> str:
    """Process one SES record. Returns a short status string.

    Transient failures raise; expected drops return a ``dropped:*`` status.
    """

    ses = record.get("ses", {})
    mail = ses.get("mail", {})
    receipt = ses.get("receipt", {})
    message_id = mail.get("messageId", "")

    if not message_id:
        logger.warning("record missing messageId; dropping")
        return "dropped:no-message-id"

    if not _recipients_ok(receipt, settings):
        logger.info("message %s: no allowed recipient; dropping", message_id)
        return "dropped:recipient"

    blocked = _is_blocked_by_verdicts(receipt)
    if blocked:
        logger.info("message %s: %s; dropping", message_id, blocked)
        return "dropped:verdict"

    loop = _is_loop(mail, settings)
    if loop:
        logger.info("message %s: %s; dropping", message_id, loop)
        return "dropped:loop"

    raw = _fetch_raw(message_id, settings, s3_client=s3_client)

    try:
        forwarded = build_forwarded_message(raw, settings)
    except ValueError as exc:
        # Malformed/unparseable mail is a permanent condition: don't retry.
        logger.warning("message %s: %s; dropping", message_id, exc)
        return "dropped:malformed"

    _send(forwarded, settings, ses_client=ses_client)
    logger.info("message %s: forwarded to private inbox", message_id)
    return "forwarded"


def lambda_handler(event: dict, context: Any = None) -> dict:
    settings = Settings.from_env()
    s3_client = _boto3_client("s3")
    ses_client = _boto3_client("ses")

    results: list[str] = []
    for record in _records(event):
        results.append(
            process_record(record, settings, s3_client=s3_client, ses_client=ses_client)
        )
    return {"processed": len(results), "results": results}


def _records(event: dict) -> Iterable[dict]:
    return event.get("Records", []) or []
