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
- **`prospect_stage`.** The funnel (below) has its own route, never the
  whole-record PUT, for `status`'s reason.

## The prospect funnel (issue 14.3 / #355)

"A prospect is a client with no case yet" (ADR 0022) — and the funnel the
issue asks for lives HERE, on the client, by the maintainer's decision of
2026-10-01; a case starts at retained (`cases.STATUSES`).

A client IS A PROSPECT while `first_retained_at` is unset: the firm has not
been retained by them. `prospect_stage` says where in the funnel they are —
`possible`, `consultation_scheduled`, `awaiting_signed_agreement`,
`exhausted` — and is meaningful only then. Opening the client's first case
(or copying one for them) is THE RETAINED TRANSITION: the store's
`mark_client_retained` stamps `first_retained_at` (when unset) and REMOVES
the stage in the same conditional UpdateItem. Cleared rather than kept as
history: the stage is a working position, not a record of how the person
arrived — `lead_source` and the case's own status history are the records —
and a stale "awaiting signed agreement" on a retained client is a field a
reader would have to know to ignore. `firm_client_json` therefore never
serves a stage on a retained client, whatever is stored.

"Has no case" is read as "not retained" — `first_retained_at` — rather than
from the case table's `by-client` index, on purpose. The index would answer
for cases the caller may not see, and refusing a stage because of one would
reveal it (`client_merge`'s argument). `first_retained_at` is on the record
the caller is already reading, and the stamp sets it whenever a case is
opened. Setting a stage is ONE UpdateItem conditioned on
`first_retained_at` still being absent, so a stage write racing a case
opening cannot leave a retained client with a funnel position: either the
stage lands first and the stamp removes it, or the stamp lands first and the
stage write is refused (409).

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
from .debtors import (
    Debtor,
    OtherName,
    create_debtor,
    debtor_body,
    parse_debtor,
    parse_other_names,
    replace_debtor,
)
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
from .provenance import ProvenanceEntry, populated_paths, provenance_json

# The DynamoDB sort-key namespace in the firm partition, alongside "META",
# "USER#", "LIBCREDITOR#" and the portal binding's "CLIENT#". NOT "CLIENT" —
# see the module docstring.
SK_PREFIX: Final = "FIRMCLIENT"

ACTIVE: Final = "active"
ARCHIVED: Final = "archived"
STATUSES: Final = (ACTIVE, ARCHIVED)

# The prospect funnel (#355) — see the module docstring.
PROSPECT_STAGES: Final = (
    "possible",
    "consultation_scheduled",
    "awaiting_signed_agreement",
    "exhausted",
)

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
    # ADR 0022's merge (PR 7). `merged_into` is TERMINAL: set, with status
    # `archived`, when this client was folded into the survivor it names —
    # the record is kept, never deleted, so a debtor's provenance that still
    # says it was copied from this client resolves to someone. The other two
    # are the in-flight merge's claim on both clients (see `client_merge`):
    # `merging_into` on the client being folded away, `merging_from` on the
    # survivor. All three are server-owned and written ONLY by the store's
    # merge methods — never by `update_client`, whose whole-record save would
    # otherwise put back a stale copy of the claim it read.
    merged_into: str | None = None
    merging_into: str | None = None
    merging_from: str | None = None
    # The funnel position (PROSPECT_STAGES) while this client is a prospect.
    # Server-owned like the merge attributes: written only by the store's
    # `set_client_prospect_stage` and removed by `mark_client_retained`,
    # never by `update_client`'s whole-record save.
    prospect_stage: str | None = None

    @property
    def archived(self) -> bool:
        return self.status == ARCHIVED

    @property
    def prospect(self) -> bool:
        """Whether the firm has yet to be retained by this person."""
        return self.first_retained_at is None

    @property
    def merged(self) -> bool:
        return self.merged_into is not None

    def refusal_for_new_case(self) -> str | None:
        """Why this client cannot be opened for a case (or linked to a
        debtor) right now, or None when it can. An archived client — which
        includes every merged one — must be restored first; a client being
        merged away must not gain a case the merge's listing already missed,
        or that case would be left naming a merged client."""
        if self.merged_into is not None:
            return "That client was merged into another client — use that one."
        if self.merging_into is not None:
            return "That client is being merged into another client."
        if self.archived:
            return "That client is archived — restore them first."
        return None


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


def parse_client_merge(payload: Mapping[str, object]) -> str:
    """`POST /v1/firm/clients/<survivor>/merge`'s body:
    `{"merged_client_id": "<id>"}` — the client that is folded INTO the one
    the URL names and archived. Named for what happens to it, so a reader of
    the request cannot get the direction backwards."""
    merged = payload.get("merged_client_id")
    if not isinstance(merged, str) or not merged.strip():
        raise FieldValidationError(
            {"merged_client_id": "Choose the client to merge into this one."}
        )
    return merged.strip()


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


def parse_prospect_stage(payload: Mapping[str, object]) -> str | None:
    """`PUT /v1/firm/clients/<id>/prospect-stage`'s body:
    `{"prospect_stage": "<stage>"}` to place a prospect in the funnel, or
    `null` to take them out of it."""
    if "prospect_stage" not in payload:
        raise FieldValidationError({"prospect_stage": "Choose a stage, or null."})
    stage = payload["prospect_stage"]
    if stage is None:
        return None
    if not isinstance(stage, str) or stage not in PROSPECT_STAGES:
        raise FieldValidationError(
            {"prospect_stage": "Must be one of " + ", ".join(PROSPECT_STAGES) + "."}
        )
    return stage


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
    for attribute, value in _merge_attributes(client).items():
        if value is not None:
            item[attribute] = value
    if client.prospect_stage is not None:
        item[PROSPECT_STAGE] = client.prospect_stage
    return item


# The stored attributes of the merge — `FirmClient.merged_into` and the
# in-flight claim. Named once, because the DynamoDB adapter's whole-record
# update must leave exactly these alone.
MERGED_INTO: Final = "mergedInto"
MERGING_INTO: Final = "mergingInto"
MERGING_FROM: Final = "mergingFrom"
MERGE_ATTRIBUTES: Final = (MERGED_INTO, MERGING_INTO, MERGING_FROM)
# The funnel position (#355), server-owned for the same reason.
PROSPECT_STAGE: Final = "prospectStage"
# Every attribute `update_client`'s whole-record save must leave alone.
SERVER_OWNED_ATTRIBUTES: Final = (*MERGE_ATTRIBUTES, PROSPECT_STAGE)


def _merge_attributes(client: FirmClient) -> dict[str, str | None]:
    return {
        MERGED_INTO: client.merged_into,
        MERGING_INTO: client.merging_into,
        MERGING_FROM: client.merging_from,
    }


def _optional_id(value: object) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise ValueError("a merge pointer is not a string")
    return value


def _optional_stage(value: object) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or value not in PROSPECT_STAGES:
        raise ValueError(f"unknown prospect stage {value!r}")
    return value


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
            merged_into=_optional_id(item.get(MERGED_INTO)),
            merging_into=_optional_id(item.get(MERGING_INTO)),
            merging_from=_optional_id(item.get(MERGING_FROM)),
            prospect_stage=_optional_stage(item.get(PROSPECT_STAGE)),
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
    # The survivor a merged client was folded into, so a screen can say so
    # and link there. The in-flight claim is not on the wire: it is a lock,
    # not a fact about the person.
    if client.merged_into is not None:
        body["merged_into"] = client.merged_into
    # The funnel position, only while a prospect — a stage on a retained
    # client is meaningless, whatever a race left stored.
    if client.prospect and client.prospect_stage is not None:
        body["prospect_stage"] = client.prospect_stage
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


def _copied_root(path: str) -> bool:
    """Whether a provenance path addresses one of COPIED_FIELDS —
    `name.given`, `other_names_used[a1].surname`, `phone`."""
    return path.split(".", 1)[0].split("[", 1)[0] in COPIED_FIELDS


def recopy_from_client(debtor: Debtor, client: FirmClient) -> Debtor:
    """ADR 0022's *re-copy from client*: `debtor` with every copied field
    replaced by `client`'s — a whole-record write, as the questionnaire's is —
    and each of those fields carrying `{source: "client", client_id}` again.

    ONLY the copied fields move. Venue, credit counselling, the signature
    date, employer ids and the tax id are the case's own answers and keep
    their values and their provenance; a copied field the client leaves
    empty becomes empty here too (it is a copy of the record, not a merge
    into it), and its old entry goes with it. The id, `created_at` and the
    link are `replace_debtor`'s to keep.

    Round-tripped through `parse_debtor` with invariant 1 enforced, for
    `debtor_from_client`'s reason. The caller refuses a filed case — this
    function cannot see one."""
    whole = asdict(client)
    body = {**debtor_body(debtor), **{key: whole[key] for key in COPIED_FIELDS}}
    entry = ProvenanceEntry(source="client", client_id=client.id)
    provenance = {
        **{
            path: kept
            for path, kept in debtor.provenance.items()
            if not _copied_root(path)
        },
        **dict.fromkeys(populated_paths(_copied_body(client)), entry),
    }
    draft = parse_debtor(
        {**prune_body(body), "provenance": provenance_json(provenance)}
    )
    return replace_debtor(debtor, draft, tax_id=debtor.tax_id)


def client_updated_from_debtor(client: FirmClient, debtor: Debtor) -> FirmClient:
    """ADR 0022's *update client from this case*: `client` with every copied
    field replaced by what the case now says — the whole-record client write
    `PUT /v1/firm/clients/<id>` makes.

    The client's own fields (date of birth, lead source, referral, retained
    date) and everything server-owned are kept. Re-parsed through
    `parse_firm_client`, so a debtor whose name has been emptied is refused
    with the client's own "a surname or a given name is required" rather than
    leaving a nameless directory entry. The debtor is not written: its values
    are already what the client now holds, and its provenance still says
    truthfully where each one came from."""
    whole = asdict(client)
    ours = asdict(debtor)
    draft_body = {
        key: (ours[key] if key in COPIED_FIELDS else whole[key])
        for key in FirmClientDraft.__dataclass_fields__
    }
    return replace_firm_client(client, parse_firm_client(prune_body(draft_body)))
