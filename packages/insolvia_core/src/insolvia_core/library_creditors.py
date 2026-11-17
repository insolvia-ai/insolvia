"""A firm's reusable creditor library (issue 13.9 / #350).

Every creditor used to be typed from scratch on every case, and a mis-typed
address is the leading cause of a rejected creditor matrix (#94). A consumer
firm lists the same lenders, servicers, collectors and taxing authorities on
most of its cases, so this module gives a firm ONE deduplicated record per
creditor it reuses — name, notice address, additional notice parties, a
`preferred` flag for the picker to surface first, and free-text notes — kept
in the same table as `firms.py`, under the same firm partition, following the
identical key discipline `firm_user_item` establishes: `PK = FIRM#<firm_id>`,
a namespaced `SK` unique across the partition (`LIBCREDITOR#<id>`, alongside
`META` and `USER#<subject>`).

WHY THIS IS NOT A CASE ENTITY. `core/case_entities.py`'s machinery (a case
partition, per-field provenance, progressive intake) is for records that
belong to ONE case. A library creditor belongs to the FIRM, is reused across
many cases, and carries no provenance of its own — it is a firm's own
authored data, the same standing `Firm.name` and a `FirmUser`'s two name
halves have. What travels to a case is a COPY (`library_creditor_id` on the
provenance of the case record that copied it — see `provenance.py`'s
`library` source), never a live reference: editing the library entry later
must never rewrite a filed schedule.

WHY A WHOLE-RECORD SAVE, NOT A PATCH. `firms.py` gives `FirmChanges` /
`FirmUserChanges` an all-optional "None means unchanged" shape because a PATCH
there changes one axis at a time (a role, a permission). A library creditor
has no such axis — every field is edited together on one form, `notes`
legitimately needs to become empty again, and "None means leave unchanged"
and "None means clear this" cannot both be true of the same field without a
sentinel. So POST and PUT both take a whole `LibraryCreditorDraft`, the same
shape `core/case_entities.py` uses for a case collection's records.

THE WIRE SHAPE IS SNAKE_CASE, THE OPPOSITE OF `firms.py`'s camelCase. Every
other firm-domain JSON body (`firm_json`, `firm_user_json`) is camelCase
because that module invented its own field names. This one does not: the
issue specifies "additional notice parties in the same shape
`claim.notice_parties` uses", and the fastest way to keep that true is to
parse and serialise with `claims.py`'s own `NoticeParty` / `parse_notice_parties`
and `fields.py`'s own `Address` / `parse_address`, unmodified — which means
adopting the case domain's snake_case on the wire rather than inventing a
second, camelCase copy of the same fields to keep in step by hand.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import asdict, dataclass, replace
from typing import Final

from insolvia_core.errors import FieldValidationError, ValidationError

from .claims import NoticeParty, parse_notice_parties
from .fields import (
    Address,
    boolean,
    narrative,
    parse_address,
    prune_body,
    text,
    timestamp,
)
from .firms import partition_key

MAX_NAME: Final = 200
MAX_NOTES: Final = 2000

# The DynamoDB sort-key namespace, alongside firms.py's "META" and "USER#" in
# the same partition — must stay unique across every SK that partition uses.
SK_PREFIX: Final = "LIBCREDITOR"


@dataclass(frozen=True)
class LibraryCreditor:
    """One firm's reusable creditor record — the META item of nothing; it
    lives in its firm's own partition alongside the firm and its users."""

    firm_id: str
    id: str
    name: str
    address: Address
    additional_notice_parties: tuple[NoticeParty, ...]
    preferred: bool
    notes: str | None
    created_at: str
    updated_at: str


@dataclass(frozen=True)
class LibraryCreditorDraft:
    """A validated whole-record save — POST to create, PUT to replace."""

    name: str
    address: Address
    additional_notice_parties: tuple[NoticeParty, ...]
    preferred: bool
    notes: str | None


def _parse_additional_notice_parties(
    value: object, errors: dict[str, str]
) -> tuple[NoticeParty, ...]:
    """`claims.parse_notice_parties` hardcodes `notice_parties` into every
    message it writes (it was built for a field of that name). This library's
    wire field is `additional_notice_parties`, so errors are collected into a
    scratch dict and the prefix is rewritten before they reach the caller —
    otherwise a client would see a message naming a field it never sent."""
    local: dict[str, str] = {}
    parties = parse_notice_parties(value, local)
    for path, message in local.items():
        errors[path.replace("notice_parties", "additional_notice_parties", 1)] = message
    return parties


def parse_library_creditor(payload: Mapping[str, object]) -> LibraryCreditorDraft:
    """Validate POST/PUT `/v1/firm/creditors`. Unknown keys are ignored.

    `name` is the one required field. Unlike a case's progressive intake,
    where every field tolerates absence because a half-finished questionnaire
    must still save, a library entry with no name is not a record — it is the
    one fact the picker shows, and a firm choosing to add something to its
    reusable library has already decided it is worth naming.
    """
    errors: dict[str, str] = {}
    name = text(payload.get("name"), "name", errors, limit=MAX_NAME)
    if name is None and "name" not in errors:
        errors["name"] = "A creditor name is required."
    address = parse_address(payload.get("address"), "address", errors)
    additional = _parse_additional_notice_parties(
        payload.get("additional_notice_parties"), errors
    )
    preferred = boolean(payload.get("preferred"), "preferred", errors)
    notes = narrative(payload.get("notes"), "notes", errors, limit=MAX_NOTES)

    if errors or name is None:
        raise FieldValidationError(errors)

    return LibraryCreditorDraft(
        name=name,
        address=address,
        additional_notice_parties=additional,
        preferred=preferred if preferred is not None else False,
        notes=notes,
    )


def create_library_creditor(
    draft: LibraryCreditorDraft, *, firm_id: str
) -> LibraryCreditor:
    now = timestamp()
    return LibraryCreditor(
        firm_id=firm_id,
        id=str(uuid.uuid4()),
        name=draft.name,
        address=draft.address,
        additional_notice_parties=draft.additional_notice_parties,
        preferred=draft.preferred,
        notes=draft.notes,
        created_at=now,
        updated_at=now,
    )


def replace_library_creditor(
    existing: LibraryCreditor, draft: LibraryCreditorDraft
) -> LibraryCreditor:
    """A new record with the draft applied, keeping `id`/`firm_id`/`created_at`
    — the same shape `case_entities.replace_entity` keeps for a case record,
    and for the same reason: a case's copy of this creditor names this row by
    id in its provenance, so the id must survive every edit."""
    return replace(
        existing,
        name=draft.name,
        address=draft.address,
        additional_notice_parties=draft.additional_notice_parties,
        preferred=draft.preferred,
        notes=draft.notes,
        updated_at=timestamp(),
    )


def sort_key(creditor_id: str) -> str:
    return f"{SK_PREFIX}#{creditor_id}"


def _address_dict(address: Address) -> dict[str, object]:
    return prune_body(asdict(address))


def _notice_party_dict(party: NoticeParty) -> dict[str, object]:
    body = asdict(party)
    address = body.pop("address", {})
    return {
        "id": body.pop("id"),
        **prune_body(body),
        "address": _address_dict_from(address),
    }


def _address_dict_from(raw: object) -> dict[str, object]:
    return prune_body(raw) if isinstance(raw, dict) else {}


def library_creditor_item(creditor: LibraryCreditor) -> dict[str, object]:
    """The stored item shape, in the same table `firms.py` writes to.

    PK  FIRM#<firm_id>          the firm's own partition
    SK  LIBCREDITOR#<id>        one entry per reusable creditor

    No GSI keys: a library creditor is only ever listed within its own firm
    (`PK` alone), never resolved cross-firm the way `firm_user_item`'s
    `GSI1PK`/`GSI1SK` resolve a bare Cognito subject to its firm.
    """
    return {
        "PK": partition_key(creditor.firm_id),
        "SK": sort_key(creditor.id),
        "id": creditor.id,
        "firmId": creditor.firm_id,
        "name": creditor.name,
        "address": _address_dict(creditor.address),
        "additionalNoticeParties": [
            _notice_party_dict(party) for party in creditor.additional_notice_parties
        ],
        "preferred": creditor.preferred,
        "notes": creditor.notes,
        "createdAt": creditor.created_at,
        "updatedAt": creditor.updated_at,
    }


def library_creditor_from_item(item: Mapping[str, object]) -> LibraryCreditor:
    """Inverse of `library_creditor_item`.

    The address and the notice parties are RE-PARSED through `fields.py` and
    `claims.py` rather than trusted, exactly as `case_entities.entity_from_item`
    re-parses a case record's body — an item written by an older revision is
    exactly the case where a nested shape has since changed, and failing loudly
    here beats a `None` surfacing three layers up.
    """
    try:
        address_errors: dict[str, str] = {}
        address = parse_address(item.get("address"), "address", address_errors)
        notice_errors: dict[str, str] = {}
        additional = parse_notice_parties(
            item.get("additionalNoticeParties"), notice_errors
        )
        notes = item.get("notes")
        return LibraryCreditor(
            firm_id=str(item["firmId"]),
            id=str(item["id"]),
            name=str(item["name"]),
            address=address,
            additional_notice_parties=additional,
            preferred=item.get("preferred") is True,
            notes=str(notes) if isinstance(notes, str) else None,
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
        )
    except KeyError as error:
        raise ValidationError(
            f"stored library creditor item is malformed: {error}"
        ) from error


def library_creditor_json(creditor: LibraryCreditor) -> dict[str, object]:
    """The API representation. Snake_case — see the module docstring for why
    this route does not follow `firm_json`'s camelCase.

    `notes` is OMITTED rather than sent as explicit `null` when absent — the
    common convention every optional case-domain field follows (`entity_json`
    via `prune_body`), unlike `firm_json`'s `createdBy`/`createdByEmail`: those
    are explicit-null on purpose, to tell a pre-portal firm apart from a field
    this API version does not carry, and no such ambiguity exists here.
    """
    body: dict[str, object] = {
        "id": creditor.id,
        "name": creditor.name,
        "address": _address_dict(creditor.address),
        "additional_notice_parties": [
            _notice_party_dict(party) for party in creditor.additional_notice_parties
        ],
        "preferred": creditor.preferred,
        "created_at": creditor.created_at,
        "updated_at": creditor.updated_at,
    }
    if creditor.notes is not None:
        body["notes"] = creditor.notes
    return body
