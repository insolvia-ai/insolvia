"""The debtor record — B101 Part 1, and the root the rest of a case hangs off.

Shape and reasoning: docs/reference/case-data-model.md, "Identity, and why joint
debtors are two records". The short version, because it is the thing most likely
to be re-litigated:

    A joint filing is TWO debtor records under one case, not one record with
    spouse-suffixed columns.

The IEPD models it that way — `BankruptcyDebtor2` is declared as a substitution
for `BankruptcyDebtor1`, each with its own name, tax identification and
signature — and the forms follow: B101 prints a full second column for credit
counseling and for venue, and 106I's second column may belong to a spouse who
is not filing at all. That last case is why `non_filing_spouse` is a filing
role rather than a flag.

PROGRESSIVE BY CONSTRUCTION. Every field is optional. The data model is explicit
that storage validates shape and type only and accepts absent values everywhere,
because a half-finished questionnaire must persist; completeness against a given
chapter's forms is a pre-filing check the forms engine owns. So there is no
"required" anywhere below — only "if present, it must be well-formed".
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass, field, replace
from typing import Any, Final, Literal

from insolvia_core.errors import FieldValidationError

from .cases import client_key, listing_sort_key, partition_key

# The scalar and structured parsers live in core/fields.py, shared with every
# other case entity (issue #249). `Address` and `PersonName` are re-exported
# here because a debtor's callers have always imported them from this module.
from .fields import Address, PersonName, parse_address, parse_name, prune_body
from .fields import choice as _choice
from .fields import form_date as _form_date
from .fields import mapping as _mapping
from .fields import text as _text
from .fields import timestamp as _timestamp
from .provenance import (
    ADDRESSABLE_ID_RE,
    ProvenanceEntry,
    parse_provenance,
    provenance_json,
    require_provenance,
    require_server_sources_kept,
)
from .tax_ids import TaxIdInput, TaxIdRef, parse_tax_id, tax_id_json

__all__ = [
    "COUNSELING_EXEMPTIONS",
    "COUNSELING_STATUSES",
    "FILING_ROLES",
    "VENUE_BASES",
    "Address",
    "CreditCounseling",
    "Debtor",
    "DebtorDraft",
    "LinkOutcome",
    "OtherName",
    "PersonName",
    "RepointOutcome",
    "Venue",
    "create_debtor",
    "debtor_body",
    "debtor_from_item",
    "debtor_item",
    "debtor_json",
    "link_client",
    "parse_debtor",
    "parse_filing_role",
    "replace_debtor",
    "require_client_provenance_kept",
    "role_order",
    "sort_key",
]

# One record per role per case, which is why the role is the sort key rather
# than a uuid: "the second debtor" is a position on the form, and a case can no
# more have two debtor_2s than form B101 can print two second columns.
FILING_ROLES: Final = ("debtor_1", "debtor_2", "non_filing_spouse")

# What `DebtorStore.link` answers — the conditional write behind the link
# route's "one client, one role per case" (ADR 0022).
LinkOutcome = Literal["written", "role_taken", "client_taken"]

# What `DebtorStore.repoint_client` answers — one case's step of a client
# merge (ADR 0022's PR 7). `absent` is "that role no longer names the merged
# client" (an index entry that lagged behind a write), and nothing was
# written; `client_taken` is "the survivor already holds another role of this
# case", refused for `LinkOutcome`'s reason.
RepointOutcome = Literal["written", "absent", "client_taken"]

# B101 line 6. `other` carries the explanation the form asks for.
VENUE_BASES: Final = ("lived_longest_180_days", "other")

# The four checkboxes of B101 line 15, named rather than numbered so a form
# revision that reorders them does not silently change stored meanings.
COUNSELING_STATUSES: Final = (
    "completed_with_certificate",
    "completed_certificate_pending",
    "exigent_circumstances_waiver_requested",
    "not_required",
)

# Only meaningful with status `not_required` — the form's three grounds.
COUNSELING_EXEMPTIONS: Final = ("incapacity", "disability", "active_duty")


@dataclass(frozen=True)
class OtherName:
    """An 8-year-lookback alias. Carries its own `id` so provenance paths
    address it as `other_names_used[<id>].surname` — see core/provenance.py for
    why position would be wrong."""

    id: str
    given: str | None = None
    middle: str | None = None
    surname: str | None = None
    business_name: str | None = None


@dataclass(frozen=True)
class Venue:
    basis: str | None = None
    explanation: str | None = None


@dataclass(frozen=True)
class CreditCounseling:
    status: str | None = None
    exemption_reason: str | None = None


@dataclass(frozen=True)
class Debtor:
    id: str
    case_id: str
    filing_role: str
    created_at: str
    updated_at: str
    name: PersonName = field(default_factory=PersonName)
    other_names_used: tuple[OtherName, ...] = ()
    employer_ids: tuple[str, ...] = ()
    residence_address: Address = field(default_factory=Address)
    mailing_address: Address = field(default_factory=Address)
    phone: str | None = None
    mobile: str | None = None
    email: str | None = None
    venue: Venue = field(default_factory=Venue)
    credit_counseling: CreditCounseling = field(default_factory=CreditCounseling)
    signed_at: str | None = None
    # The REFERENCE to the sealed identifier, plus the two plain facts every
    # view needs (kind, last four). Never the number: tax_ids.py owns the
    # design, and `debtor_body` keeps this out of the stored body.
    tax_id: TaxIdRef | None = None
    provenance: Mapping[str, ProvenanceEntry] = field(default_factory=dict)
    # The firm client this debtor was copied from (ADR 0022) — SERVER-OWNED:
    # set when the case is opened for the client or by the link route, kept
    # by every questionnaire save, never read from a body. None only for a
    # non-filing spouse who is not the firm's client.
    client_id: str | None = None
    # The CASE's creation time, denormalised for the `by-client` index's sort
    # key — `CaseAssignment.case_created_at`'s reason exactly: a client's
    # cases must list in the order the matters were opened. Set with
    # `client_id`, and only with it.
    case_created_at: str | None = None


@dataclass(frozen=True)
class DebtorDraft:
    """A validated debtor body, before the server stamps identity on it.

    `tax_id` here is the PARSED WRITE (`TaxIdInput`: the full digits being
    entered, or the last four being kept), which is why a draft is not just a
    Debtor without identity: the digits exist only in this object, in memory,
    between the parse and `tax_ids.store_tax_id` sealing them. Nothing
    serialises a draft.
    """

    name: PersonName
    other_names_used: tuple[OtherName, ...]
    employer_ids: tuple[str, ...]
    residence_address: Address
    mailing_address: Address
    phone: str | None
    mobile: str | None
    email: str | None
    venue: Venue
    credit_counseling: CreditCounseling
    signed_at: str | None
    tax_id: TaxIdInput | None
    provenance: Mapping[str, ProvenanceEntry]


def parse_other_names(value: object, errors: dict[str, str]) -> tuple[OtherName, ...]:
    """The 8-year alias list. Public because `firm_clients` parses a client's
    `other_names_used` with it — ADR 0022 copies a client's identity onto a
    debtor, so the two must accept exactly the same rows."""
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["other_names_used"] = "Must be a list."
        return ()

    names: list[OtherName] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"other_names_used[{index}]"
        entry = _mapping(raw, path, errors)
        # REQUIRED, and supplied by the caller. The client creates the row and
        # writes provenance for its fields in the same request, so it has to be
        # able to name the row it is describing.
        #
        # Minting one here instead looks helpful and is a trap: the alias's
        # fields then need provenance at `other_names_used[<fresh-uuid>].…`, a
        # path the caller cannot possibly have sent. It 400s, and because a new
        # uuid is minted on every attempt, echoing back the path the error just
        # named 400s again with a different uuid. There is no escape from that
        # loop, which made the whole 8-year alias list unusable. An earlier
        # version of this function did exactly that.
        #
        # Refused rather than replaced for the same reason a malformed id is:
        # handing back a different id silently leaves the client's provenance
        # keys naming a row that does not exist.
        given_id = entry.get("id")
        if not isinstance(given_id, str) or not ADDRESSABLE_ID_RE.match(given_id):
            errors[f"{path}.id"] = (
                "Required, and must be letters, digits, hyphen or underscore — "
                "generate one per row so provenance can name it."
            )
            continue
        alias_id = given_id
        if alias_id in seen:
            errors[f"{path}.id"] = "Duplicate id."
            continue
        seen.add(alias_id)
        names.append(
            OtherName(
                id=alias_id,
                given=_text(entry.get("given"), f"{path}.given", errors),
                middle=_text(entry.get("middle"), f"{path}.middle", errors),
                surname=_text(entry.get("surname"), f"{path}.surname", errors),
                business_name=_text(
                    entry.get("business_name"), f"{path}.business_name", errors
                ),
            )
        )
    return tuple(names)


def _parse_employer_ids(value: object, errors: dict[str, str]) -> tuple[str, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["employer_ids"] = "Must be a list."
        return ()
    ids: list[str] = []
    for index, raw in enumerate(value):
        text = _text(raw, f"employer_ids[{index}]", errors, limit=20)
        if text is not None:
            ids.append(text)
    return tuple(ids)


def _parse_venue(value: object, errors: dict[str, str]) -> Venue:
    raw = _mapping(value, "venue", errors)
    return Venue(
        basis=_choice(raw.get("basis"), VENUE_BASES, "venue.basis", errors),
        explanation=_text(
            raw.get("explanation"), "venue.explanation", errors, limit=1000
        ),
    )


def _parse_credit_counseling(value: object, errors: dict[str, str]) -> CreditCounseling:
    raw = _mapping(value, "credit_counseling", errors)
    return CreditCounseling(
        status=_choice(
            raw.get("status"), COUNSELING_STATUSES, "credit_counseling.status", errors
        ),
        exemption_reason=_choice(
            raw.get("exemption_reason"),
            COUNSELING_EXEMPTIONS,
            "credit_counseling.exemption_reason",
            errors,
        ),
    )


def parse_filing_role(value: object) -> str:
    errors: dict[str, str] = {}
    role = _choice(value, FILING_ROLES, "filing_role", errors)
    if role is None:
        raise FieldValidationError(
            errors or {"filing_role": "Must be one of " + ", ".join(FILING_ROLES) + "."}
        )
    return role


def parse_debtor(
    payload: Mapping[str, object], *, enforce_provenance: bool = True
) -> DebtorDraft:
    """Validate a whole debtor body. Unknown keys are ignored.

    WHOLE, not partial. The questionnaire PUTs the complete record on every
    autosave rather than PATCHing fields, and that is a consequence of
    invariant 1 rather than a preference: "every populated field carries
    provenance" can only be checked against a complete record, so a partial
    write would have to merge against the stored copy first and then re-derive
    the rule — the same check, done somewhere it is easier to get wrong.

    `tax_id` is parsed for SHAPE here (tax_ids.parse_tax_id — the SSN/ITIN
    rules) and nothing more: the digits ride the draft to the route, which
    seals them through `tax_ids.store_tax_id`. This function used to refuse a
    tax id outright, because the store had no field-level encryption; that
    refusal ended with issue 13.12 / #382.
    """
    errors: dict[str, str] = {}

    tax_id = parse_tax_id(payload.get("tax_id"), "tax_id", errors)
    name = parse_name(payload.get("name"), "name", errors)
    other_names_used = parse_other_names(payload.get("other_names_used"), errors)
    employer_ids = _parse_employer_ids(payload.get("employer_ids"), errors)
    residence_address = parse_address(
        payload.get("residence_address"), "residence_address", errors
    )
    mailing_address = parse_address(
        payload.get("mailing_address"), "mailing_address", errors
    )
    phone = _text(payload.get("phone"), "phone", errors, limit=40)
    mobile = _text(payload.get("mobile"), "mobile", errors, limit=40)
    email = _text(payload.get("email"), "email", errors)
    venue = _parse_venue(payload.get("venue"), errors)
    credit_counseling = _parse_credit_counseling(
        payload.get("credit_counseling"), errors
    )
    signed_at = _form_date(payload.get("signed_at"), "signed_at", errors)

    if errors:
        raise FieldValidationError(errors)

    # Provenance is parsed AFTER the body, so a request with both a malformed
    # field and bad provenance reports the field first — the fixable thing.
    provenance = parse_provenance(payload.get("provenance"))

    draft = DebtorDraft(
        name=name,
        other_names_used=other_names_used,
        employer_ids=employer_ids,
        residence_address=residence_address,
        mailing_address=mailing_address,
        phone=phone,
        mobile=mobile,
        email=email,
        venue=venue,
        credit_counseling=credit_counseling,
        signed_at=signed_at,
        tax_id=tax_id,
        provenance=provenance,
    )
    # INVARIANT 1, against the record as it will be STORED rather than as it
    # arrived: `_text` collapses whitespace-only values to None, so checking the
    # raw payload would demand provenance for fields that are about to vanish.
    #
    # A WRITE rule, which is why reads switch it off. A stored record already
    # passed it once; re-running it on the way out means the day the rule
    # tightens, every record written under the old one becomes unreadable —
    # and because a FieldValidationError is a 400, listing ONE bad debtor
    # would fail the whole case's GET. Shape is still re-parsed on read; only
    # the invariant is skipped.
    #
    # THE TAX ID IS ONE FIELD FOR PROVENANCE: `tax_id`, never `tax_id.kind`
    # and `tax_id.value`. The kind and the digits are one identifier, entered
    # in one act, and the last-four echo a client sends to keep a stored
    # number is not a fact of its own. `debtor_body` deliberately excludes
    # the member (the digits must never enter a body), so it is added back
    # here as a placeholder scalar solely so invariant 1 demands the entry.
    # The api-client's `staffTypedProvenance` mirrors this exception.
    if enforce_provenance:
        checked: dict[str, object] = debtor_body(draft)
        if tax_id is not None:
            checked["tax_id"] = "present"
        require_provenance(checked, provenance)
    return draft


def require_client_provenance_kept(draft: DebtorDraft, stored: Debtor | None) -> None:
    """A questionnaire save may KEEP `client` provenance, never mint it
    (ADR 0022; case-data-model.md: a field keeps `client` provenance until a
    human changes that field).

    `client` provenance says "this value is the firm's client record, copied
    in" — and only the server copies: `POST /v1/cases`, the link route, and
    the re-copy route (`firm_clients.debtor_from_client` /
    `recopy_from_client`). So a `client` entry arriving on the whole-record
    PUT is legitimate in exactly one shape: the entry the stored record
    already carries at that path, on the value that path already holds. That
    is the autosave echoing back a field the preparer did not touch.

    Anything else is refused rather than rewritten: a `client` entry on a
    value that has since changed (the preparer typed over the copy — that
    field is `staff_typed` now), or one the stored record never had (a
    caller claiming a copy the server did not make). Refused, not silently
    downgraded, for the rule every parser here follows — a caller that
    believes it saved one thing must not find another stored. The app's
    `revisedProvenance` is what sends the right map.

    Entries at paths the draft leaves empty describe nothing and are not
    checked. Pure; the route passes the record it just read.

    `client_answered` (ADR 0023 PR 4) follows the same keep-only rule: only
    the review queue's acceptance of a portal answer mints it, so a save may
    echo it back on an untouched value and never claim it. Both live in
    `provenance.require_server_sources_kept`."""
    require_server_sources_kept(
        debtor_body(draft),
        draft.provenance,
        stored_body=debtor_body(stored) if stored is not None else None,
        stored_entries=stored.provenance if stored is not None else None,
    )


def debtor_body(draft: DebtorDraft | Debtor) -> dict[str, object]:
    """The record's case data as plain nested values — what provenance paths
    address, and what gets stored. Excludes identity, provenance itself, AND
    the tax id: on a draft that member holds the digits, on a record it holds
    the sealed item's reference, and neither belongs in a stored body or a
    response. `debtor_json` and `debtor_item` add the view each needs."""
    body = asdict(draft)
    for key in (
        "id",
        "case_id",
        "filing_role",
        "created_at",
        "updated_at",
        "provenance",
        "tax_id",
        "client_id",
        "case_created_at",
    ):
        body.pop(key, None)
    return body


def _record_fields(draft: DebtorDraft) -> dict[str, Any]:
    """The draft's members that a Debtor takes verbatim — everything but the
    parsed tax-id write, which the caller has already turned into a
    reference (or None) through `tax_ids.store_tax_id`. `Any` because that
    is what `vars()` answers and what the `**` spread below needs."""
    fields: dict[str, Any] = vars(draft).copy()
    fields.pop("tax_id")
    return fields


def create_debtor(
    draft: DebtorDraft,
    *,
    case_id: str,
    filing_role: str,
    tax_id: TaxIdRef | None = None,
    client_id: str | None = None,
    case_created_at: str | None = None,
) -> Debtor:
    """A new debtor. `client_id` and `case_created_at` travel together — the
    `by-client` index needs both, and one without the other is refused
    rather than half-indexed."""
    if (client_id is None) != (case_created_at is None):
        raise ValueError("client_id and case_created_at are set together")
    now = _timestamp()
    return Debtor(
        id=str(uuid.uuid4()),
        case_id=case_id,
        filing_role=filing_role,
        created_at=now,
        updated_at=now,
        tax_id=tax_id,
        client_id=client_id,
        case_created_at=case_created_at,
        **_record_fields(draft),
    )


def replace_debtor(
    existing: Debtor, draft: DebtorDraft, *, tax_id: TaxIdRef | None = None
) -> Debtor:
    """A new Debtor with the draft's body, keeping the original id and
    created_at. The id is stable across saves because provenance paths on other
    records may already reference it.

    `tax_id` is what the debtor will carry AFTER this save — the caller
    resolves it from the draft's write and the existing reference through
    `tax_ids.store_tax_id`; a draft that carries no tax id clears it here,
    exactly as PUT semantics clear any other omitted field.

    `client_id` (and the index's `case_created_at`) are KEPT from `existing`
    — server-owned (ADR 0022): a questionnaire save cannot link, re-link or
    unlink a client, whatever its body says. `link_client` is that act."""
    return Debtor(
        id=existing.id,
        case_id=existing.case_id,
        filing_role=existing.filing_role,
        created_at=existing.created_at,
        updated_at=_timestamp(),
        tax_id=tax_id,
        client_id=existing.client_id,
        case_created_at=existing.case_created_at,
        **_record_fields(draft),
    )


def link_client(existing: Debtor, *, client_id: str, case_created_at: str) -> Debtor:
    """`existing` pointed at another firm client, its copied fields untouched.

    Re-linking changes which client the debtor is (and so which `by-client`
    entry the item feeds) — never what the case says about them. The fields
    that now differ from the newly linked client show up in
    `differs_from_client`; copying them over is its own, explicit act."""
    return replace(
        existing,
        client_id=client_id,
        case_created_at=case_created_at,
        updated_at=_timestamp(),
    )


def sort_key(filing_role: str) -> str:
    return f"DEBTOR#{filing_role}"


def role_order(filing_role: str) -> int:
    """Position in FILING_ROLES — the order the forms print debtors in, and
    the ONE definition of it, because both stores have to agree.

    They would otherwise agree only by coincidence: DynamoDB returns a query
    in sort-key order, which is alphabetical by role, and debtor_1 /
    debtor_2 / non_filing_spouse happen to sort the same way. A fourth role
    named anything earlier in the alphabet would silently reorder the AWS
    store's results and not the memory store's — so the tests would keep
    passing while staging printed a second debtor first.

    An unknown role sorts last rather than raising: an item written by a
    later revision should list oddly, not make the whole case unreadable."""
    try:
        return FILING_ROLES.index(filing_role)
    except ValueError:
        return len(FILING_ROLES)


def debtor_json(
    debtor: Debtor, *, differs_from_client: Sequence[str] | None = None
) -> dict[str, object]:
    """The API representation. Absent values are omitted rather than sent as
    nulls: on a progressive intake most of the record is empty most of the
    time, and a body of nulls is mostly noise.

    `client_id` is present whenever the debtor names a firm client (ADR
    0022). `differs_from_client` — the field paths where this copy and the
    client record now disagree (`firm_clients.differs_from_client`) — is
    present only when the caller computed it: it needs the client record,
    which this pure function does not have, and a caller without the
    `clients` feature is not told how a client record reads. An empty list
    is an answer ("identical"), so it is sent, not pruned."""
    body = prune_body(debtor_body(debtor))
    # The LAST-FOUR VIEW, and the only tax-id representation any response
    # carries — the debtor routes, the generic collection routes' summaries,
    # the MCP record tools all serialise through here. The full value has no
    # wire shape at all (tax_ids.read_tax_id is not reachable from a route).
    if debtor.tax_id is not None:
        body["tax_id"] = tax_id_json(debtor.tax_id)
    identity: dict[str, object] = {
        "id": debtor.id,
        "case_id": debtor.case_id,
        "filing_role": debtor.filing_role,
        "created_at": debtor.created_at,
        "updated_at": debtor.updated_at,
    }
    if debtor.client_id is not None:
        identity["client_id"] = debtor.client_id
    if differs_from_client is not None:
        identity["differs_from_client"] = list(differs_from_client)
    return {
        **identity,
        "provenance": provenance_json(debtor.provenance),
        **body,
    }


def debtor_item(debtor: Debtor) -> dict[str, object]:
    """The stored item shape.

    PK  CASE#<case_id>          the same partition as the case root, so a
    SK  DEBTOR#<filing_role>    debtor is read with the case it belongs to and
                                a role can exist at most once per case.

    GSI3PK  CLIENT#<client_id>          the `by-client` index (ADR 0022) —
    GSI3SK  <caseCreatedAt>#<case_id>   only when the debtor names a client

    The debtor item IS the index entry for "this client's cases", exactly as
    an assignment item is the `by-assignee` entry: linking a client and
    making the case appear in their list are one write, and no second row
    can disagree with the debtor's `client_id`. SPARSE: a non-filing spouse
    who is not a client carries neither key and is simply not in it. A joint
    case appears once per client. The sort key is built by
    `cases.listing_sort_key`, the same function both other listings use.

    `taxId` is its OWN attribute beside `body`, never inside it: the kind,
    the last four, and the reference to the sealed item (SK=TAXID#<ref>, in
    this partition today — tax_ids.py). The body is what a client sent and
    what a client gets back; the reference is neither. `clientId` sits
    beside `body` for the same reason: the server set it, nobody sent it.
    """
    item: dict[str, object] = {
        "PK": partition_key(debtor.case_id),
        "SK": sort_key(debtor.filing_role),
        "id": debtor.id,
        "caseId": debtor.case_id,
        "filingRole": debtor.filing_role,
        "createdAt": debtor.created_at,
        "updatedAt": debtor.updated_at,
        "body": prune_body(debtor_body(debtor)),
        "provenance": provenance_json(debtor.provenance),
    }
    if debtor.tax_id is not None:
        item["taxId"] = {
            "kind": debtor.tax_id.kind,
            "lastFour": debtor.tax_id.last_four,
            "ref": debtor.tax_id.ref,
        }
    if debtor.client_id is not None and debtor.case_created_at is not None:
        item["clientId"] = debtor.client_id
        item["caseCreatedAt"] = debtor.case_created_at
        item["GSI3PK"] = client_key(debtor.client_id)
        item["GSI3SK"] = listing_sort_key(debtor.case_created_at, debtor.case_id)
    return item


def _tax_id_from_item(value: object) -> TaxIdRef | None:
    if value is None:
        return None
    if not isinstance(value, Mapping):
        raise ValueError("debtor item's taxId is not a map")
    kind, last_four, ref = value.get("kind"), value.get("lastFour"), value.get("ref")
    if not isinstance(kind, str) or not isinstance(last_four, str):
        raise ValueError("debtor item's taxId is missing kind or lastFour")
    if not isinstance(ref, str):
        raise ValueError("debtor item's taxId is missing its ref")
    return TaxIdRef(kind=kind, last_four=last_four, ref=ref)


def debtor_from_item(item: Mapping[str, object]) -> Debtor:
    """Rebuild from a stored item.

    The body is re-parsed rather than trusted: an item written by an older
    revision is exactly the case where a field has since changed shape, and
    failing loudly here beats a `None` surfacing three layers up.

    Provenance is NOT re-enforced — see parse_debtor. Shape drift should be
    loud; a tightened invariant should not retroactively make saved cases
    unreadable."""
    body = item.get("body")
    draft = parse_debtor(
        {
            **(body if isinstance(body, Mapping) else {}),
            "provenance": item.get("provenance"),
        },
        enforce_provenance=False,
    )
    return Debtor(
        id=str(item.get("id", "")),
        case_id=str(item.get("caseId", "")),
        filing_role=str(item.get("filingRole", "")),
        created_at=str(item.get("createdAt", "")),
        updated_at=str(item.get("updatedAt", "")),
        tax_id=_tax_id_from_item(item.get("taxId")),
        client_id=_optional_str(item.get("clientId")),
        case_created_at=_optional_str(item.get("caseCreatedAt")),
        **_record_fields(draft),
    )


def _optional_str(value: object) -> str | None:
    return str(value) if value is not None else None
