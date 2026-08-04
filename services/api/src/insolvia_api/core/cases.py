"""Case records: validation, identity, and the stored item shape.

The first product entity (issue 8.3). The logical model this implements is
docs/reference/case-data-model.md — but only its `case` root entity. Debtors,
claims, assets and the rest are separate items under the same partition and
arrive with the intake work; nothing here forecloses them.

Everything in this module is pure: no Flask, no boto3, no clock beyond
datetime.now. The item shape lives here rather than in an adapter so the
DynamoDB and in-memory stores cannot drift apart, exactly as core/waitlist.py
does for the waitlist table.
"""

from __future__ import annotations

import base64
import binascii
import json
import re
import uuid
from collections.abc import Mapping
from dataclasses import dataclass, replace
from datetime import UTC, datetime

from insolvia_api.core.errors import FieldValidationError, ValidationError

# The chapters an individual debtor can file under. 9 and 15 are municipal and
# cross-border and will never appear in this product.
CHAPTERS = (7, 11, 12, 13)

# Lifecycle, deliberately small. "filed" is terminal for this issue — what
# happens after filing is the forms milestone's problem.
STATUSES = ("intake", "ready_to_file", "filed")

# A bankruptcy court district identifier. Kept loose on purpose: the
# authoritative list belongs to the forms/e-filing work, which will have the
# CM/ECF court codes, and inventing a half-list here would be a constraint the
# next person has to unpick rather than a validation anyone wanted.
_DISTRICT_RE = re.compile(r"^[A-Za-z0-9 .\-/]{2,64}$")

MAX_LIST_LIMIT = 100
DEFAULT_LIST_LIMIT = 50


@dataclass(frozen=True)
class Case:
    """A case record — the META item of its partition."""

    id: str
    owner_principal: str
    chapter: int
    district: str
    status: str
    created_at: str
    updated_at: str


@dataclass(frozen=True)
class CaseDraft:
    """A validated creation request, before server-generated identity."""

    chapter: int
    district: str


@dataclass(frozen=True)
class CaseChanges:
    """A validated PATCH body. None means "leave unchanged" — which is why
    these are Optional rather than defaulted: a client that omits `district`
    must not silently blank it."""

    chapter: int | None = None
    district: str | None = None
    status: str | None = None


@dataclass(frozen=True)
class CasePage:
    """One page of a principal's cases, newest first."""

    cases: tuple[Case, ...]
    next_cursor: str | None


def _timestamp() -> str:
    """Microsecond-precision UTC with a literal Z.

    Fixed width and Z-suffixed is not cosmetic: GSI1SK sorts
    lexicographically, so a "+00:00" offset is the same instant that sorts
    wrong and silently misorders GET /v1/cases. The infra module's comment
    says the same thing from the other side.

    MICROseconds rather than the milliseconds core/waitlist.py uses, because
    the two tables order differently. GSI1SK is "<createdAt>#<id>" and the id
    is a random uuid4, so two cases sharing a timestamp sort arbitrarily
    rather than by creation order. Milliseconds made that reachable by two
    ordinary back-to-back requests; microseconds make it vanishingly
    unlikely. It remains a tie-break, not a guarantee — if strict total
    ordering is ever needed, the id has to become sortable, and that is a
    schema change rather than a formatting one.
    """
    return datetime.now(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def _parse_chapter(value: object, errors: dict[str, str]) -> int | None:
    # bool is an int in Python, and `True` would otherwise validate as
    # chapter 1 and fail the membership test with a confusing message.
    if isinstance(value, bool) or not isinstance(value, int):
        errors["chapter"] = "Chapter must be a number."
        return None
    if value not in CHAPTERS:
        errors["chapter"] = (
            "Chapter must be one of " + ", ".join(str(c) for c in CHAPTERS) + "."
        )
        return None
    return value


def _parse_district(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or not value.strip():
        errors["district"] = "A filing district is required."
        return None
    district = value.strip()
    if not _DISTRICT_RE.match(district):
        errors["district"] = "That doesn't look like a district identifier."
        return None
    return district


def _parse_status(value: object, errors: dict[str, str]) -> str | None:
    if not isinstance(value, str) or value not in STATUSES:
        errors["status"] = "Status must be one of " + ", ".join(STATUSES) + "."
        return None
    return value


def parse_case_creation(payload: Mapping[str, object]) -> CaseDraft:
    """Validate POST /v1/cases. Unknown keys are ignored.

    Status is deliberately NOT accepted here: every case starts at "intake",
    and letting a client create one already marked "filed" would be a lie the
    server told on its behalf.
    """
    errors: dict[str, str] = {}
    chapter = _parse_chapter(payload.get("chapter"), errors)
    district = _parse_district(payload.get("district"), errors)
    # The None checks are redundant with `errors` but they are what narrows
    # the types, and a redundant guard beats an assert that a future -O strips.
    if errors or chapter is None or district is None:
        raise FieldValidationError(errors)
    return CaseDraft(chapter=chapter, district=district)


def parse_case_update(payload: Mapping[str, object]) -> CaseChanges:
    """Validate PATCH /v1/cases/<id>.

    Only supplied keys are validated, so a caller changing the district alone
    is not forced to resend the chapter. An empty body is rejected rather than
    treated as a no-op — it is far more likely to be a client bug than an
    intent, and a silent 200 would hide it.
    """
    errors: dict[str, str] = {}
    changes: dict[str, object] = {}

    if "chapter" in payload:
        chapter = _parse_chapter(payload["chapter"], errors)
        if chapter is not None:
            changes["chapter"] = chapter
    if "district" in payload:
        district = _parse_district(payload["district"], errors)
        if district is not None:
            changes["district"] = district
    if "status" in payload:
        status = _parse_status(payload["status"], errors)
        if status is not None:
            changes["status"] = status

    if errors:
        raise FieldValidationError(errors)
    if not changes:
        raise ValidationError("no supported fields to update")
    return CaseChanges(**changes)  # type: ignore[arg-type]


def create_case(draft: CaseDraft, *, owner_principal: str) -> Case:
    """Stamp a draft with server-generated identity. The owner comes from the
    verified token, never from the request body — a client cannot create a
    case belonging to someone else because it has no way to say so."""
    now = _timestamp()
    return Case(
        id=str(uuid.uuid4()),
        owner_principal=owner_principal,
        chapter=draft.chapter,
        district=draft.district,
        status="intake",
        created_at=now,
        updated_at=now,
    )


def apply_changes(case: Case, changes: CaseChanges) -> Case:
    """A new Case with the supplied changes applied and updated_at refreshed."""
    updates = {
        field: value
        for field, value in (
            ("chapter", changes.chapter),
            ("district", changes.district),
            ("status", changes.status),
        )
        if value is not None
    }
    return replace(case, updated_at=_timestamp(), **updates)  # type: ignore[arg-type]


def partition_key(case_id: str) -> str:
    return f"CASE#{case_id}"


def owner_key(owner_principal: str) -> str:
    return f"OWNER#{owner_principal}"


def case_item(case: Case) -> dict[str, str | int]:
    """The exact stored item shape, shared by both CaseStore implementations.

    PK      CASE#<id>
    SK      META                    the case root; child entities take
                                    DEBTOR#<id>, CLAIM#<id>, ... later
    GSI1PK  OWNER#<principal>       sparse: only this item carries them, so
    GSI1SK  <createdAt>#<id>        the index holds one entry per case
    """
    return {
        "PK": partition_key(case.id),
        "SK": "META",
        "GSI1PK": owner_key(case.owner_principal),
        "GSI1SK": f"{case.created_at}#{case.id}",
        "id": case.id,
        "ownerPrincipal": case.owner_principal,
        "chapter": case.chapter,
        "district": case.district,
        "status": case.status,
        "createdAt": case.created_at,
        "updatedAt": case.updated_at,
    }


def case_from_item(item: Mapping[str, str | int]) -> Case:
    """Inverse of case_item. Raises ValidationError on an item this service
    did not write — a corrupt row should fail loudly here rather than become
    a half-populated Case that reaches a caller."""
    try:
        return Case(
            id=str(item["id"]),
            owner_principal=str(item["ownerPrincipal"]),
            chapter=int(item["chapter"]),
            district=str(item["district"]),
            status=str(item["status"]),
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored case item is malformed: {error}") from error


def case_json(case: Case) -> dict[str, object]:
    """The API representation. `ownerPrincipal` is deliberately absent: the
    caller is the owner by construction, so returning it would leak a subject
    identifier into a response body for no purpose."""
    return {
        "id": case.id,
        "chapter": case.chapter,
        "district": case.district,
        "status": case.status,
        "createdAt": case.created_at,
        "updatedAt": case.updated_at,
    }


def parse_list_limit(raw: str | None) -> int:
    if raw is None or raw == "":
        return DEFAULT_LIST_LIMIT
    try:
        limit = int(raw)
    except ValueError as error:
        raise ValidationError("limit must be a number") from error
    if limit < 1 or limit > MAX_LIST_LIMIT:
        raise ValidationError(f"limit must be between 1 and {MAX_LIST_LIMIT}")
    return limit


def encode_cursor(key: Mapping[str, str]) -> str:
    """Opaque pagination cursor. Base64 rather than the raw key so the client
    cannot come to depend on the table's attribute names."""
    raw = json.dumps(dict(sorted(key.items())), separators=(",", ":"))
    return base64.urlsafe_b64encode(raw.encode()).decode()


def decode_cursor(cursor: str) -> dict[str, str]:
    """Inverse of encode_cursor, rejecting anything this service did not mint.

    The value is attacker-controlled and goes on to become a DynamoDB
    ExclusiveStartKey, so it is validated to a flat string map here rather
    than passed through — an unchecked dict would let a caller shape the
    query's start key.
    """
    try:
        raw = base64.urlsafe_b64decode(cursor.encode())
        decoded = json.loads(raw)
    except (ValueError, binascii.Error) as error:
        raise ValidationError("cursor is not valid") from error
    if not isinstance(decoded, dict) or not all(
        isinstance(key, str) and isinstance(value, str)
        for key, value in decoded.items()
    ):
        raise ValidationError("cursor is not valid")
    return decoded
