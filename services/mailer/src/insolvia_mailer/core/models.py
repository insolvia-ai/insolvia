from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from email.utils import parseaddr
from typing import Any

from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ValidationError

SCHEMA_VERSION = 1
MAX_REQUEST_BYTES = 4 * 1024 * 1024
MAX_SES_MESSAGE_BYTES = 40 * 1024 * 1024
MAX_ATTACHMENT_BYTES = 30 * 1024 * 1024
MAX_ATTACHMENTS = 20
BLOCKED_EXTENSIONS = {
    ".ade",
    ".adp",
    ".app",
    ".asp",
    ".bas",
    ".bat",
    ".cer",
    ".chm",
    ".cmd",
    ".com",
    ".cpl",
    ".crt",
    ".csh",
    ".der",
    ".exe",
    ".fxp",
    ".gadget",
    ".hlp",
    ".hta",
    ".inf",
    ".ins",
    ".isp",
    ".its",
    ".js",
    ".jse",
    ".ksh",
    ".lib",
    ".lnk",
    ".mad",
    ".maf",
    ".mag",
    ".mam",
    ".maq",
    ".mar",
    ".mas",
    ".mat",
    ".mau",
    ".mav",
    ".maw",
    ".mda",
    ".mdb",
    ".mde",
    ".mdt",
    ".mdw",
    ".mdz",
    ".msc",
    ".msh",
    ".msh1",
    ".msh2",
    ".mshxml",
    ".msi",
    ".msp",
    ".mst",
    ".ops",
    ".pcd",
    ".pif",
    ".plg",
    ".prf",
    ".prg",
    ".reg",
    ".scf",
    ".scr",
    ".sct",
    ".shb",
    ".shs",
    ".sys",
    ".ps1",
    ".ps1xml",
    ".ps2",
    ".ps2xml",
    ".psc1",
    ".psc2",
    ".tmp",
    ".url",
    ".vb",
    ".vbe",
    ".vbs",
    ".vps",
    ".vsmacros",
    ".vss",
    ".vst",
    ".vsw",
    ".vxd",
    ".ws",
    ".wsc",
    ".wsf",
    ".wsh",
    ".xnk",
}
ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,199}$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")

MAX_URL_CHARS = 2048

# Why a suppression has a reason at all: the sender Lambda treats every entry
# in the table identically (it refuses to send, full stop), but the reason is
# what makes the table answerable when someone asks "why did this address stop
# receiving mail" — a complaint, a hard bounce, and a person clicking
# unsubscribe are three very different facts about the same address. Kept as a
# closed set so a typo cannot invent a fourth.
SUPPRESSION_REASONS = frozenset({"bounce", "complaint", "unsubscribe"})


def _string(value: Any, name: str, *, maximum: int, required: bool = True) -> str:
    if value is None and not required:
        return ""
    if not isinstance(value, str) or (required and not value.strip()):
        raise ValidationError(f"{name} must be a non-empty string")
    if len(value) > maximum:
        raise ValidationError(f"{name} exceeds {maximum} characters")
    return value


def _identifier(value: Any, name: str) -> str:
    value = _string(value, name, maximum=200)
    if not ID_PATTERN.fullmatch(value):
        raise ValidationError(f"{name} contains unsupported characters")
    return value


def _only_keys(value: dict[str, Any], allowed: set[str]) -> None:
    unexpected = set(value) - allowed
    if unexpected:
        fields = ", ".join(sorted(unexpected))
        raise ValidationError(f"request contains unsupported fields: {fields}")


def _email(value: Any, name: str = "to_address") -> str:
    address = _string(value, name, maximum=320).strip()
    display, parsed = parseaddr(address)
    if display or parsed != address or "@" not in parsed or parsed.count("@") != 1:
        raise ValidationError(f"{name} must contain exactly one bare email address")
    local, domain = parsed.rsplit("@", 1)
    if not local or "." not in domain or any(char.isspace() for char in parsed):
        raise ValidationError(f"{name} is invalid")
    return parsed


def _https_url(value: Any, name: str) -> str:
    """An absolute https URL safe to put in a mail header.

    https-only and control-character-free are both load-bearing: this value
    lands verbatim in a List-Unsubscribe header, so a CR or LF would let a
    caller inject arbitrary headers into the outgoing message, and a
    `javascript:` or `http:` scheme would be handed to a mail client as a
    one-click action.
    """
    url = _string(value, name, maximum=MAX_URL_CHARS).strip()
    if not url.startswith("https://"):
        raise ValidationError(f"{name} must be an absolute https URL")
    if any(char in url for char in "\r\n") or any(ord(char) < 0x20 for char in url):
        raise ValidationError(f"{name} cannot contain control characters")
    if "<" in url or ">" in url:
        raise ValidationError(f"{name} cannot contain angle brackets")
    return url


@dataclass(frozen=True)
class AttachmentUploadRequest:
    application_message_id: str
    file_name: str
    content_type: str
    size_bytes: int
    sha256: str

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> AttachmentUploadRequest:
        _schema(value)
        _only_keys(
            value,
            {
                "schema_version",
                "application_message_id",
                "file_name",
                "content_type",
                "size_bytes",
                "sha256",
            },
        )
        message_id = _identifier(
            value.get("application_message_id"), "application_message_id"
        )
        file_name = _string(value.get("file_name"), "file_name", maximum=255)
        if (
            file_name in {".", ".."}
            or "/" in file_name
            or "\\" in file_name
            or "\x00" in file_name
        ):
            raise ValidationError("file_name must be a plain filename")
        suffix = "." + file_name.rsplit(".", 1)[-1].lower() if "." in file_name else ""
        if suffix in BLOCKED_EXTENSIONS:
            raise ValidationError("file extension is blocked by SES")
        content_type = _string(value.get("content_type"), "content_type", maximum=255)
        if "/" not in content_type or any(char in content_type for char in "\r\n"):
            raise ValidationError("content_type is invalid")
        size = value.get("size_bytes")
        if (
            not isinstance(size, int)
            or isinstance(size, bool)
            or not 1 <= size <= MAX_ATTACHMENT_BYTES
        ):
            raise ValidationError(
                f"size_bytes must be between 1 and {MAX_ATTACHMENT_BYTES}"
            )
        digest = value.get("sha256")
        if not isinstance(digest, str) or not SHA256_PATTERN.fullmatch(digest):
            raise ValidationError("sha256 must be a lowercase hexadecimal SHA-256")
        return cls(message_id, file_name, content_type, size, digest)


@dataclass(frozen=True)
class AttachmentReference:
    attachment_id: str
    disposition: str
    content_id: str | None

    @classmethod
    def from_dict(
        cls, value: dict[str, Any], *, internal: bool = False
    ) -> AttachmentReference:
        allowed = {"attachment_id", "disposition", "content_id"}
        if internal:
            allowed |= {
                "object_key",
                "file_name",
                "content_type",
                "size_bytes",
                "sha256",
            }
        _only_keys(value, allowed)
        attachment_id = _identifier(value.get("attachment_id"), "attachment_id")
        disposition = value.get("disposition", "attachment")
        if disposition not in {"attachment", "inline"}:
            raise ValidationError("attachment disposition must be attachment or inline")
        raw_content_id = value.get("content_id")
        content_id = None
        if raw_content_id is not None:
            content_id = _identifier(raw_content_id, "content_id")
        if disposition == "inline" and not content_id:
            raise ValidationError("inline attachments require content_id")
        if disposition == "attachment" and content_id:
            raise ValidationError("ordinary attachments cannot set content_id")
        return cls(attachment_id, disposition, content_id)


@dataclass(frozen=True)
class MessageRequest:
    application_message_id: str
    category: str
    message_class: str
    to_address: str
    subject: str
    html_body: str
    text_body: str
    attachments: tuple[AttachmentReference, ...]
    # Optional, and the caller's business: the mailer does not mint unsubscribe
    # links or know how to verify one. The caller composed the body, so the
    # caller owns the URL; all this field buys is turning that link into a
    # List-Unsubscribe header the mail client can surface on its own (see
    # core/mime.py). None means the message ships without those headers.
    list_unsubscribe_url: str | None = None

    @classmethod
    def from_dict(
        cls,
        value: dict[str, Any],
        service: ServiceConfig,
        *,
        internal: bool = False,
    ) -> MessageRequest:
        _schema(value)
        allowed = {
            "schema_version",
            "application_message_id",
            "category",
            "message_class",
            "to_address",
            "subject",
            "html_body",
            "text_body",
            "attachments",
            "list_unsubscribe_url",
        }
        if internal:
            allowed |= {"service_id", "sender_address", "configuration_set"}
        _only_keys(value, allowed)
        attachments_value = value.get("attachments", [])
        if (
            not isinstance(attachments_value, list)
            or len(attachments_value) > MAX_ATTACHMENTS
        ):
            raise ValidationError(
                f"attachments must contain at most {MAX_ATTACHMENTS} items"
            )
        attachments = tuple(
            AttachmentReference.from_dict(item, internal=internal)
            for item in attachments_value
        )
        ids = [item.attachment_id for item in attachments]
        if len(ids) != len(set(ids)):
            raise ValidationError("attachment IDs must be unique")
        category = _identifier(value.get("category"), "category")
        message_class = _identifier(value.get("message_class"), "message_class")
        if category not in service.allowed_categories:
            raise ValidationError("category is not registered for this service")
        if message_class not in service.allowed_message_classes:
            raise ValidationError("message_class is not registered for this service")
        subject = _string(value.get("subject"), "subject", maximum=998)
        if "\r" in subject or "\n" in subject:
            raise ValidationError("subject cannot contain line breaks")
        raw_unsubscribe = value.get("list_unsubscribe_url")
        list_unsubscribe_url = (
            _https_url(raw_unsubscribe, "list_unsubscribe_url")
            if raw_unsubscribe is not None
            else None
        )
        return cls(
            application_message_id=_identifier(
                value.get("application_message_id"), "application_message_id"
            ),
            category=category,
            message_class=message_class,
            to_address=_email(value.get("to_address")),
            subject=subject,
            html_body=_string(value.get("html_body"), "html_body", maximum=4_000_000),
            text_body=_string(value.get("text_body"), "text_body", maximum=1_000_000),
            attachments=attachments,
            list_unsubscribe_url=list_unsubscribe_url,
        )

    def canonical_hash(self) -> str:
        return hashlib.sha256(canonical_json(self.to_public_dict())).hexdigest()

    def to_public_dict(self) -> dict[str, Any]:
        return {
            "schema_version": SCHEMA_VERSION,
            "application_message_id": self.application_message_id,
            "category": self.category,
            "message_class": self.message_class,
            "to_address": self.to_address,
            "subject": self.subject,
            "html_body": self.html_body,
            "text_body": self.text_body,
            "list_unsubscribe_url": self.list_unsubscribe_url,
            "attachments": [
                {
                    "attachment_id": item.attachment_id,
                    "disposition": item.disposition,
                    "content_id": item.content_id,
                }
                for item in self.attachments
            ],
        }


@dataclass(frozen=True)
class SuppressionRequest:
    """A caller asking that an address stop receiving its mail (issue #80).

    The write path the SES feedback Lambda already uses for bounces and
    complaints, exposed to the registered caller so a *user-initiated*
    unsubscribe reaches the same table. It is deliberately the same store: an
    opt-out that lived somewhere else would need the sender Lambda to check
    two places, and the day someone forgets the second check is the day an
    unsubscribed person gets emailed.

    Note what this endpoint does NOT do: it does not verify that the caller
    has any right to suppress this particular address. It cannot — it sees a
    SigV4-signed request from a registered service and nothing else. Proving
    the request came from the address's owner is the caller's job, and for
    insolvia_api that proof is the HMAC unsubscribe token
    (services/api core/unsubscribe.py). The blast radius of that split is
    bounded by the operation itself: the worst a compromised caller achieves
    is stopping its own mail to an address, never reading or sending anything.
    """

    email_address: str
    reason: str

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> SuppressionRequest:
        _schema(value)
        _only_keys(value, {"schema_version", "email_address", "reason"})
        reason = _string(value.get("reason"), "reason", maximum=32)
        if reason not in SUPPRESSION_REASONS:
            allowed = ", ".join(sorted(SUPPRESSION_REASONS))
            raise ValidationError(f"reason must be one of {allowed}")
        return cls(
            email_address=_email(value.get("email_address"), "email_address"),
            reason=reason,
        )


def _schema(value: dict[str, Any]) -> None:
    if not isinstance(value, dict):
        raise ValidationError("request body must be a JSON object")
    if value.get("schema_version") != SCHEMA_VERSION:
        raise ValidationError(f"schema_version must be {SCHEMA_VERSION}")


def canonical_json(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode()


def recipient_hash(address: str) -> str:
    return hashlib.sha256(address.strip().lower().encode()).hexdigest()
