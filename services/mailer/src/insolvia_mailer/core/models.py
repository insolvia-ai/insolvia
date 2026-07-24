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


def _email(value: Any) -> str:
    address = _string(value, "to_address", maximum=320).strip()
    display, parsed = parseaddr(address)
    if display or parsed != address or "@" not in parsed or parsed.count("@") != 1:
        raise ValidationError("to_address must contain exactly one bare email address")
    local, domain = parsed.rsplit("@", 1)
    if not local or "." not in domain or any(char.isspace() for char in parsed):
        raise ValidationError("to_address is invalid")
    return parsed


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
            "attachments": [
                {
                    "attachment_id": item.attachment_id,
                    "disposition": item.disposition,
                    "content_id": item.content_id,
                }
                for item in self.attachments
            ],
        }


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
