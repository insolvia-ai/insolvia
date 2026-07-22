"""Waitlist submission validation and record shape.

The validation rules and the DynamoDB item shape mirror the marketing site's
original SSR implementation (apps/insolvia_marketing waitlist, issue #47) —
that implementation moved behind this API per docs/adr/0001, and the table
schema and its Query pattern (one fixed partition, time-ordered sort key)
must survive the move unchanged.
"""

from __future__ import annotations

import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import UTC, datetime

from insolvia_api.core.errors import FieldValidationError

_EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")

# Per-field caps: keep rows small and reject obvious garbage. name/firm/email/
# currentSoftware/message mirror the marketing form's caps exactly; host is
# capped at the DNS name limit (the marketing SSR action sends its serving
# host, not visitor input).
MAX_LENGTHS = {
    "name": 200,
    "firm": 200,
    "email": 320,
    "currentSoftware": 100,
    "message": 2000,
    "host": 253,
}

_FIELDS = ("name", "firm", "email", "currentSoftware", "message", "host")


@dataclass(frozen=True)
class WaitlistSubmission:
    """A validated, trimmed submission. Optional fields are "" when absent."""

    name: str
    firm: str
    email: str
    current_software: str
    message: str
    host: str


@dataclass(frozen=True)
class WaitlistRecord:
    """A submission plus the server-generated identity fields."""

    id: str
    submitted_at: str
    submission: WaitlistSubmission


def _clean(payload: Mapping[str, object], key: str) -> str:
    value = payload.get(key)
    return value.strip() if isinstance(value, str) else ""


def parse_waitlist_submission(payload: Mapping[str, object]) -> WaitlistSubmission:
    """Validate a request body, raising FieldValidationError with per-field
    messages (keyed by the JSON field names) on failure.

    Unknown keys are ignored — in particular, the marketing form's honeypot
    field is a browser-facing concern its action checks before calling this
    API; it never reaches (and is not re-checked by) this endpoint.
    """
    values = {key: _clean(payload, key) for key in _FIELDS}

    errors: dict[str, str] = {}
    if not values["name"]:
        errors["name"] = "Please tell us your name."
    if not values["firm"]:
        errors["firm"] = "Please tell us your firm's name."
    if not values["email"]:
        errors["email"] = "A work email is required."
    elif not _EMAIL_RE.match(values["email"]):
        errors["email"] = "That doesn't look like a valid email address."
    for key, cap in MAX_LENGTHS.items():
        if key not in errors and len(values[key]) > cap:
            errors[key] = f"Please keep this under {cap} characters."
    if errors:
        raise FieldValidationError(errors)

    return WaitlistSubmission(
        name=values["name"],
        firm=values["firm"],
        email=values["email"],
        current_software=values["currentSoftware"],
        message=values["message"],
        host=values["host"],
    )


def create_waitlist_record(submission: WaitlistSubmission) -> WaitlistRecord:
    """Stamp a submission with a server-generated id and UTC timestamp.

    Millisecond-precision `Z` timestamps match the marketing implementation's
    `new Date().toISOString()`, keeping SK values uniformly sortable.
    """
    submitted_at = (
        datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    )
    return WaitlistRecord(
        id=str(uuid.uuid4()), submitted_at=submitted_at, submission=submission
    )


def record_item(record: WaitlistRecord) -> dict[str, str]:
    """The exact stored item shape (all values strings), shared by every
    WaitlistStore implementation so the two cannot drift.

      PK           constant "WAITLIST" (single partition; volume is tiny)
      SK           "<submittedAt>#<id>" (time-ordered within the partition)
      id, submittedAt, status="new", name, firm, email
      host, currentSoftware, message   omitted when empty, never stored as ""
    """
    submission = record.submission
    item = {
        "PK": "WAITLIST",
        "SK": f"{record.submitted_at}#{record.id}",
        "id": record.id,
        "submittedAt": record.submitted_at,
        "status": "new",
        "name": submission.name,
        "firm": submission.firm,
        "email": submission.email,
    }
    if submission.host:
        item["host"] = submission.host
    if submission.current_software:
        item["currentSoftware"] = submission.current_software
    if submission.message:
        item["message"] = submission.message
    return item
