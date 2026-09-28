"""A firm's client — the person, kept at firm scope (ADR 0022 / #353).

A `client` is a firm-scoped person; a `debtor` (`debtors.py`) is that person's
identity as COPIED into one case. One client has many cases over time, a joint
case is two clients on one matter, and a prospect is a client with no case
yet. This module is the client record, its item shape and its wire shape —
and, since ADR 0022's second PR, the COPY: `debtor_from_client` builds a
case's debtor from a client with `client` provenance on every copied field,
and `differs_from_client` is the computed divergence the debtor screen shows.
The debtor's own side of the link (`Debtor.client_id`, the `by-client`
index it feeds) is `debtors.py`'s.

## Why the module is `firm_clients`, not `clients`

`insolvia_core.clients` already exists and means something else: the client
PORTAL's binding (ADR 0023) — which Cognito subject may answer for which
filing roles on which case. That module owns the portal's vocabulary
(`ClientBinding`, `CLIENT_ROLES`, `client.invite`), and both it and the portal
routes import it by name. This record is the firm's directory entry for a
person, whether or not they ever get a login, so it is named for where it
lives — the firm — and its types say `FirmClient`, never a bare `Client`
that a reader would have to disambiguate by import path.

## Where it lives, and why the sort key is not `CLIENT#`

    firm table   PK FIRM#<firm_id>   SK FIRMCLIENT#<id>

The firm table, beside the library creditors (`library_creditors.SK_PREFIX`
is the precedent), no GSI: a client is only ever listed within its own firm.

ADR 0022 wrote `SK CLIENT#<id>`, and that key is TAKEN in this partition: the
portal binding's authoritative row is `SK CLIENT#<subject>`
(`clients.binding_item`), and `ClientBindingStore.find_by_email` queries
`begins_with(SK, "CLIENT#")` over the firm partition. A client record under
the same prefix would be returned by that query and fail to parse as a
binding — a 500 on the invitation route for every firm with a client in it.
`FIRMCLIENT#` does not begin with `CLIENT#`, so the two listings cannot
return each other's rows by construction, the same argument `clients.py`
makes for `CLIENT#` versus `USER#` on the by-subject index.

## What is not on the wire, and why

- **The tax id.** A client holds a POINTER to `tax_ids.py`'s sealed item —
  `tax_id_ref` and `tax_id_last_four` — never the value (ADR 0022: one
  encrypted item, addressed by an opaque ref, so a refiled case reuses it).
  Both are server-owned: the ref is never sent to a caller at all, and a
  request that tries to set either — or sends digits as `tax_id` — is refused
  rather than ignored, because the caller would otherwise believe a number was
  saved. Nothing in this revision sets them; they are carried so that the
  PR which copies a client onto a case has a field to fill and a stored row
  already shaped for it.
- **`status`.** Archiving is its own act (`PUT .../status`), never a side
  effect of an edit: the whole-record PUT keeps the stored status, so a stale
  edit form cannot un-archive a client.
- **`created_by`.** The creating firm user's subject, stamped by the route.

## Whole-record save, snake_case wire

For `library_creditors.py`'s reasons, unchanged: every field is edited on one
form, a field must be able to become empty again, and the shapes are the case
domain's own (`fields.PersonName`, `fields.Address`, `debtors.OtherName`) —
reused unmodified so a client copied onto a debtor passes the debtor's parser
by construction. Unlike a debtor there is no provenance: a client record is the
firm's own authored data, the same standing a library creditor has. What a
case copies from it carries `client` provenance (ADR 0022's PR 2).

Pure: no boto3, no Flask.
"""

from __future__ import annotations

import uuid
from collections.abc import Iterable, Mapping
from dataclasses import asdict, dataclass, field, replace
from datetime import date
from typing import Final

from insolvia_core.errors import FieldValidationError, ValidationError

from .cases import Case
from .debtors import Debtor, OtherName, create_debtor, parse_debtor, parse_other_names
from .fields import (
    Address,
    PersonName,
    form_date,
    parse_address,
    parse_name,
    prune_body,
    text,
    timestamp,
)
from .firms import partition_key
from .provenance import populated_paths

# The DynamoDB sort-key namespace in the firm partition, alongside "META",
# "USER#", "LIBCREDITOR#" and the portal binding's "CLIENT#". NOT "CLIENT" —
# see the module docstring.
SK_PREFIX: Final = "FIRMCLIENT"

ACTIVE: Final = "active"
ARCHIVED: Final = "archived"
STATUSES: Final = (ACTIVE, ARCHIVED)

# Free text in v1 (ADR 0022); a firm pick-list is 14.8's.
MAX_LEAD_SOURCE: Final = 200
MAX_REFERRED_BY: Final = 200
# The same caps `parse_debtor` applies — a client's contact fields are copied
# onto a debtor, so a value the client accepts must be one the debtor accepts.
MAX_PHONE: Final = 40

# Keys a caller may not send. `tax_id` would be the digits; the other two are
# the server-owned pointer. Refused, not ignored — see the module docstring.
_REFUSED_TAX_ID_KEYS: Final = ("tax_id", "tax_id_ref", "tax_id_last_four")


@dataclass(frozen=True)
class FirmClient:
    """One person in a firm's client directory."""

    id: str
    firm_id: str
    status: str
    created_at: str
    updated_at: str
    created_by: str
    name: PersonName = field(default_factory=PersonName)
    other_names_used: tuple[OtherName, ...] = ()
    date_of_birth: str | None = None
    residence_address: Address = field(default_factory=Address)
    mailing_address: Address = field(default_factory=Address)
    phone: str | None = None
    mobile: str | None = None
    email: str | None = None
    lead_source: str | None = None
    referred_by: str | None = None
    first_retained_at: str | None = None
    # The POINTER to `tax_ids.py`'s sealed item and the one plain fact a
    # screen shows. Never the number. Server-owned; see the module docstring.
    tax_id_ref: str | None = None
    tax_id_last_four: str | None = None

    @property
    def archived(self) -> bool:
        return self.status == ARCHIVED


@dataclass(frozen=True)
class FirmClientDraft:
    """A validated whole-record save — POST to create, PUT to replace. Holds
    the caller-editable fields only: identity, status, the tax-id pointer and
    the stamps are the server's."""

    name: PersonName
    other_names_used: tuple[OtherName, ...]
    date_of_birth: str | None
    residence_address: Address
    mailing_address: Address
    phone: str | None
    mobile: str | None
    email: str | None
    lead_source: str | None
    referred_by: str | None
    first_retained_at: str | None


# ── Parsing ─────────────────────────────────────────────────────────


def _date_of_birth(value: object, errors: dict[str, str]) -> str | None:
    parsed = form_date(value, "date_of_birth", errors)
    if parsed is not None and date.fromisoformat(parsed) > date.today():
        errors["date_of_birth"] = "A date of birth cannot be in the future."
        return None
    return parsed


def parse_firm_client(
    payload: Mapping[str, object], *, require_name: bool = True
) -> FirmClientDraft:
    """Validate POST/PUT `/v1/firm/clients`. Unknown keys are ignored — a
    client echoing back a GET body (with `id`, `status`, `created_at`…) is
    sending server-owned fields it does not control, not making a mistake.
    The tax id is the exception (`_REFUSED_TAX_ID_KEYS`).

    `name.surname` or `name.given` is the one required field (ADR 0022): a
    directory entry with no name is not one anybody can find again. A mononym
    is a given name with no surname, so either half satisfies it.

    `require_name=False` is for reading a stored item back — the same "shape
    is re-checked on read, write rules are not" split `parse_debtor` makes.
    """
    errors: dict[str, str] = {}
    for key in _REFUSED_TAX_ID_KEYS:
        if payload.get(key) is not None:
            errors[key] = (
                "A client's tax ID cannot be set here; it is entered on a case."
            )

    name = parse_name(payload.get("name"), "name", errors)
    if (
        require_name
        and name.surname is None
        and name.given is None
        and not any(path.startswith("name") for path in errors)
    ):
        errors["name"] = "A surname or a given name is required."
    other_names_used = parse_other_names(payload.get("other_names_used"), errors)
    date_of_birth = _date_of_birth(payload.get("date_of_birth"), errors)
    residence_address = parse_address(
        payload.get("residence_address"), "residence_address", errors
    )
    mailing_address = parse_address(
        payload.get("mailing_address"), "mailing_address", errors
    )
    phone = text(payload.get("phone"), "phone", errors, limit=MAX_PHONE)
    mobile = text(payload.get("mobile"), "mobile", errors, limit=MAX_PHONE)
    email = text(payload.get("email"), "email", errors)
    lead_source = text(
        payload.get("lead_source"), "lead_source", errors, limit=MAX_LEAD_SOURCE
    )
    referred_by = text(
        payload.get("referred_by"), "referred_by", errors, limit=MAX_REFERRED_BY
    )
    first_retained_at = form_date(
        payload.get("first_retained_at"), "first_retained_at", errors
    )

    if errors:
        raise FieldValidationError(errors)

    return FirmClientDraft(
        name=name,
        other_names_used=other_names_used,
        date_of_birth=date_of_birth,
        residence_address=residence_address,
        mailing_address=mailing_address,
        phone=phone,
        mobile=mobile,
        email=email,
        lead_source=lead_source,
        referred_by=referred_by,
        first_retained_at=first_retained_at,
    )


def parse_client_status(payload: Mapping[str, object]) -> str:
    """`PUT /v1/firm/clients/<id>/status`'s body: `{"status": "archived"}` to
    archive, `"active"` to bring a client back. Archive is a status write,
    never a delete (ADR 0022) — a client with cases cannot be deleted at all."""
    status = payload.get("status")
    if not isinstance(status, str) or status not in STATUSES:
        raise FieldValidationError(
            {"status": "Must be one of " + ", ".join(STATUSES) + "."}
        )
    return status


# ── Transitions ─────────────────────────────────────────────────────


def create_firm_client(
    draft: FirmClientDraft, *, firm_id: str, created_by: str
) -> FirmClient:
    now = timestamp()
    return FirmClient(
        id=str(uuid.uuid4()),
        firm_id=firm_id,
        status=ACTIVE,
        created_at=now,
        updated_at=now,
        created_by=created_by,
        **vars(draft),
    )


def replace_firm_client(existing: FirmClient, draft: FirmClientDraft) -> FirmClient:
    """The draft applied over `existing`, keeping everything the caller does
    not own: the id (a case's debtor will name it), the firm, `created_*`,
    the status, and the tax-id pointer."""
    return replace(existing, updated_at=timestamp(), **vars(draft))


def set_firm_client_status(existing: FirmClient, status: str) -> FirmClient:
    return replace(existing, status=status, updated_at=timestamp())


def sorted_firm_clients(clients: Iterable[FirmClient]) -> tuple[FirmClient, ...]:
    """THE directory order — surname, given, middle, then id — case-folded so
    `de la Cruz` does not sort after every capitalised name. Both store
    adapters call this rather than each writing a key: an ordering the memory
    store and DynamoDB disagreed on would make a passing test prove nothing."""

    def key(client: FirmClient) -> tuple[str, str, str, str]:
        return (
            (client.name.surname or "").casefold(),
            (client.name.given or "").casefold(),
            (client.name.middle or "").casefold(),
            client.id,
        )

    return tuple(sorted(clients, key=key))


# ── Item shapes ─────────────────────────────────────────────────────


def sort_key(client_id: str) -> str:
    return f"{SK_PREFIX}#{client_id}"


def _body(client: FirmClient | FirmClientDraft) -> dict[str, object]:
    """The caller-editable fields as plain nested values, absent members
    pruned — what is stored under `body` and what the wire carries."""
    body = {
        key: value
        for key, value in asdict(client).items()
        if key in FirmClientDraft.__dataclass_fields__
    }
    return prune_body(body)


def firm_client_item(client: FirmClient) -> dict[str, object]:
    """The stored item shape, in the same table `firms.py` writes to.

    PK  FIRM#<firm_id>          the firm's own partition
    SK  FIRMCLIENT#<id>         one entry per client

    No GSI keys: a client is listed within its own firm only. The editable
    fields sit under `body` in their wire (snake_case) form and are RE-PARSED
    on read, the way `debtor_item` stores a debtor — so the stored shape and
    the parser a write passed through cannot drift apart. `taxId` is its own
    attribute beside `body`, never inside it, for `debtor_item`'s reason.
    """
    item: dict[str, object] = {
        "PK": partition_key(client.firm_id),
        "SK": sort_key(client.id),
        "id": client.id,
        "firmId": client.firm_id,
        "status": client.status,
        "createdAt": client.created_at,
        "updatedAt": client.updated_at,
        "createdBy": client.created_by,
        "body": _body(client),
    }
    if client.tax_id_ref is not None and client.tax_id_last_four is not None:
        item["taxId"] = {"ref": client.tax_id_ref, "lastFour": client.tax_id_last_four}
    return item


def _tax_id_from_item(value: object) -> tuple[str | None, str | None]:
    if value is None:
        return None, None
    if not isinstance(value, Mapping):
        raise ValueError("taxId is not a map")
    ref, last_four = value.get("ref"), value.get("lastFour")
    if not isinstance(ref, str) or not isinstance(last_four, str):
        raise ValueError("taxId is missing ref or lastFour")
    return ref, last_four


def firm_client_from_item(item: Mapping[str, object]) -> FirmClient:
    """Inverse of `firm_client_item`. Raises ValidationError on a row this
    module did not write rather than half-populating a client."""
    try:
        status = str(item["status"])
        if status not in STATUSES:
            raise ValueError(f"unknown status {status!r}")
        body = item.get("body")
        draft = parse_firm_client(
            body if isinstance(body, Mapping) else {}, require_name=False
        )
        tax_id_ref, tax_id_last_four = _tax_id_from_item(item.get("taxId"))
        return FirmClient(
            id=str(item["id"]),
            firm_id=str(item["firmId"]),
            status=status,
            created_at=str(item["createdAt"]),
            updated_at=str(item["updatedAt"]),
            created_by=str(item["createdBy"]),
            tax_id_ref=tax_id_ref,
            tax_id_last_four=tax_id_last_four,
            **vars(draft),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(f"stored client item is malformed: {error}") from error


# ── Representation ──────────────────────────────────────────────────


def firm_client_json(client: FirmClient) -> dict[str, object]:
    """The API representation, snake_case. Absent optional fields are omitted,
    as `debtor_json` omits them. `name` is always present (a stored client
    always has one half of it).

    `tax_id_last_four` is the only tax-id member a response carries: the ref
    is an internal address and never leaves the server."""
    body: dict[str, object] = {
        "id": client.id,
        "status": client.status,
        "created_at": client.created_at,
        "updated_at": client.updated_at,
        "created_by": client.created_by,
        "name": {},
        **_body(client),
    }
    if client.tax_id_last_four is not None:
        body["tax_id_last_four"] = client.tax_id_last_four
    return body


# ── The copy onto a case (ADR 0022) ─────────────────────────────────

# The identity a case copies from a client, and the ONLY fields
# `differs_from_client` compares. Everything else on a client is the firm's
# (lead source, referral, retained date) or is never on a debtor (date of
# birth — no form prints it). The tax id is not here yet: a client's
# `tax_id_ref` has no writer until #382 lands the client side, and a debtor's
# sealed identifier is entered on the case.
COPIED_FIELDS: Final = (
    "name",
    "other_names_used",
    "residence_address",
    "mailing_address",
    "phone",
    "mobile",
    "email",
)


def _copied_body(record: FirmClient | Debtor) -> dict[str, object]:
    whole = asdict(record)
    return prune_body({key: whole[key] for key in COPIED_FIELDS})


def debtor_from_client(client: FirmClient, *, case: Case, filing_role: str) -> Debtor:
    """A NEW debtor of `case` carrying `client`'s identity, every copied field
    with provenance `{source: "client", client_id}`.

    Built by round-tripping the copy through `parse_debtor` — with invariant
    1 ENFORCED — rather than by assembling a Debtor directly, so a client
    value the debtor's parser would refuse fails here, at the copy, instead
    of producing a stored debtor that the next questionnaire save cannot
    re-send. The two parsers share every shape (`parse_name`,
    `parse_address`, `parse_other_names`) precisely so this cannot fail on
    a client that parsed.
    """
    body = _copied_body(client)
    entry = {"source": "client", "client_id": client.id}
    # The paths to cover are whatever invariant 1 will demand of this body,
    # so they come from the same walk it uses.
    provenance = dict.fromkeys(populated_paths(body), entry)
    draft = parse_debtor({**body, "provenance": provenance})
    return create_debtor(
        draft,
        case_id=case.id,
        filing_role=filing_role,
        client_id=client.id,
        case_created_at=case.created_at,
    )


def _leaves(record: object, prefix: str = "") -> dict[str, object]:
    """Field path -> value for every populated leaf, addressing list elements
    by their `id` — the provenance grammar, so a path here is one the debtor
    screen can also look up provenance for."""
    out: dict[str, object] = {}
    if isinstance(record, Mapping):
        for key, value in record.items():
            if key == "id" and prefix.endswith("]"):
                continue
            out.update(_leaves(value, f"{prefix}.{key}" if prefix else str(key)))
        return out
    if isinstance(record, (list, tuple)):
        for element in record:
            element_id = element.get("id") if isinstance(element, Mapping) else None
            out.update(_leaves(element, f"{prefix}[{element_id}]"))
        return out
    return {prefix: record} if prefix else {}


def differs_from_client(debtor: Debtor, client: FirmClient) -> list[str]:
    """The copied field paths where `debtor` and `client` now disagree —
    ADR 0022's computed divergence, sorted.

    A path is listed when the two hold different values OR only one of them
    holds one: a phone number the client gained after the case was opened
    differs just as much as one that changed. Nothing is written by this:
    divergence is shown, never silently resolved, and the two acts that
    resolve it (re-copy, update the client) are explicit writes."""
    ours = _leaves(_copied_body(debtor))
    theirs = _leaves(_copied_body(client))
    return sorted(
        path
        for path in ours.keys() | theirs.keys()
        if ours.get(path) != theirs.get(path)
    )
