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
from dataclasses import asdict, dataclass, field
from typing import Final

from insolvia_core.errors import FieldValidationError

from .cases import partition_key

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
)

__all__ = [
    "COUNSELING_EXEMPTIONS",
    "COUNSELING_STATUSES",
    "FILING_ROLES",
    "VENUE_BASES",
    "Address",
    "CreditCounseling",
    "Debtor",
    "DebtorDraft",
    "OtherName",
    "PersonName",
    "Venue",
    "create_debtor",
    "debtor_body",
    "debtor_from_item",
    "debtor_item",
    "debtor_json",
    "parse_debtor",
    "parse_filing_role",
    "replace_debtor",
    "role_order",
    "sort_key",
]

# One record per role per case, which is why the role is the sort key rather
# than a uuid: "the second debtor" is a position on the form, and a case can no
# more have two debtor_2s than form B101 can print two second columns.
FILING_ROLES: Final = ("debtor_1", "debtor_2", "non_filing_spouse")

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
    provenance: Mapping[str, ProvenanceEntry] = field(default_factory=dict)


@dataclass(frozen=True)
class DebtorDraft:
    """A validated debtor body, before the server stamps identity on it."""

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
    provenance: Mapping[str, ProvenanceEntry]


def _parse_other_names(value: object, errors: dict[str, str]) -> tuple[OtherName, ...]:
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
    """Validate a whole debtor body. Unknown keys are ignored; `tax_id` is not.

    WHOLE, not partial. The questionnaire PUTs the complete record on every
    autosave rather than PATCHing fields, and that is a consequence of
    invariant 1 rather than a preference: "every populated field carries
    provenance" can only be checked against a complete record, so a partial
    write would have to merge against the stored copy first and then re-derive
    the rule — the same check, done somewhere it is easier to get wrong.
    """
    errors: dict[str, str] = {}

    # Explicitly rejected rather than ignored. The data model requires the full
    # SSN/ITIN stored ENCRYPTED with only the last four ever served, and that
    # needs field-level encryption this service does not have yet. Silently
    # dropping it would be far worse than refusing it: the client would believe
    # it had stored a tax id and the field would simply not be there.
    if "tax_id" in payload and payload["tax_id"] is not None:
        errors["tax_id"] = (
            "Tax identifiers are not accepted yet — they need field-level "
            "encryption, which is not built. Leave this out."
        )

    name = parse_name(payload.get("name"), "name", errors)
    other_names_used = _parse_other_names(payload.get("other_names_used"), errors)
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
    if enforce_provenance:
        require_provenance(debtor_body(draft), provenance)
    return draft


def debtor_body(draft: DebtorDraft | Debtor) -> dict[str, object]:
    """The record's case data as plain nested values — what provenance paths
    address, and what gets stored. Excludes identity and provenance itself."""
    body = asdict(draft)
    for key in (
        "id",
        "case_id",
        "filing_role",
        "created_at",
        "updated_at",
        "provenance",
    ):
        body.pop(key, None)
    return body


def create_debtor(draft: DebtorDraft, *, case_id: str, filing_role: str) -> Debtor:
    now = _timestamp()
    return Debtor(
        id=str(uuid.uuid4()),
        case_id=case_id,
        filing_role=filing_role,
        created_at=now,
        updated_at=now,
        **vars(draft),
    )


def replace_debtor(existing: Debtor, draft: DebtorDraft) -> Debtor:
    """A new Debtor with the draft's body, keeping the original id and
    created_at. The id is stable across saves because provenance paths on other
    records may already reference it."""
    return Debtor(
        id=existing.id,
        case_id=existing.case_id,
        filing_role=existing.filing_role,
        created_at=existing.created_at,
        updated_at=_timestamp(),
        **vars(draft),
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


def debtor_json(debtor: Debtor) -> dict[str, object]:
    """The API representation. Absent values are omitted rather than sent as
    nulls: on a progressive intake most of the record is empty most of the
    time, and a body of nulls is mostly noise."""
    body = prune_body(debtor_body(debtor))
    return {
        "id": debtor.id,
        "case_id": debtor.case_id,
        "filing_role": debtor.filing_role,
        "created_at": debtor.created_at,
        "updated_at": debtor.updated_at,
        "provenance": provenance_json(debtor.provenance),
        **body,
    }


def debtor_item(debtor: Debtor) -> dict[str, object]:
    """The stored item shape.

    PK  CASE#<case_id>          the same partition as the case root, so a
    SK  DEBTOR#<filing_role>    debtor is read with the case it belongs to and
                                a role can exist at most once per case.

    Carries no GSI keys: debtors are always reached through their case, never
    listed across cases, and the sparse by-owner index stays one entry per case.
    """
    return {
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
        **vars(draft),
    )
