"""B101's case-level answers and repeating lists (issue #93).

Five entity types from case-data-model.md that no earlier issue had landed,
now needed because the forms engine projects B101 from stored facts:

- `petition` — the Part 2-6 case-level answers, "churned during intake and
  untouched afterwards". ONE per case by meaning, but stored as a generic
  collection like every other kind: singularity is the completeness gate's
  check (the income_summary one-per-debtor precedent), not a storage key.
- `prior_case` (line 9), `related_case` (line 10), `sole_proprietorship`
  (line 12) — the petition's repeating rows.
- `filing_professional` — the Part 7 signer block: the attorney, or a
  bankruptcy petition preparer (→ Form 119). 0-2 per case. NOT `created_by`
  and NOT the firm: the person who signs, the person who opened the record,
  and the tenant that owns it are three different facts.

Enums are named for MEANINGS, never for the PDF's export values or printed
order — B101's own line 13 exports read "Yes filling under chapter" (sic),
and the 06/24 line-15 exports are '1'/'2'/'On'/'4'. Which export state a
stored value becomes is the projection layer's mapping, revision by revision;
these names must survive a form revision unchanged. The estimate bands on
lines 18-20 are the form's own brackets, which ARE the meaning (the debtor
self-selects a bracket, not a number), so those enum names mirror the
printed brackets.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import (
    Address,
    PersonName,
    boolean,
    choice,
    form_date,
    narrative,
    parse_address,
    parse_name,
    text,
)

# B101 line 8 — how the filing fee will be handled (→ Forms 103A/103B).
FEE_HANDLING: Final = ("full", "installments", "waiver")

# B101 line 12's "check all that apply"-shaped single choice.
BUSINESS_TYPES: Final = (
    "health_care_business",
    "single_asset_real_estate",
    "stockbroker",
    "commodity_broker",
    "none_of_the_above",
)

# B101 line 13, including the Subchapter V election.
SMALL_BUSINESS_STATUSES: Final = (
    "not_filing_under_chapter_11",
    "chapter_11_not_small_business",
    "chapter_11_small_business",
    "chapter_11_subchapter_v",
)

# B101 line 16. `other` carries the explanation in debt_character_other.
DEBT_CHARACTERS: Final = ("consumer", "business", "other")

# B101 line 18's printed brackets, self-selected by the debtor.
ESTIMATED_CREDITORS_BANDS: Final = (
    "1_49",
    "50_99",
    "100_199",
    "200_999",
    "1000_5000",
    "5001_10000",
    "10001_25000",
    "25001_50000",
    "50001_100000",
    "more_than_100000",
)

# B101 lines 19 and 20 share one dollar-bracket scale.
ESTIMATED_DOLLAR_BANDS: Final = (
    "0_50000",
    "50001_100000",
    "100001_500000",
    "500001_1000000",
    "1000001_10000000",
    "10000001_50000000",
    "50000001_100000000",
    "100000001_500000000",
    "500000001_1000000000",
    "1000000001_10000000000",
    "10000000001_50000000000",
    "more_than_50000000000",
)

# Part 7's two signer kinds. An attorney signs the petition; a bankruptcy
# petition preparer (a non-attorney paid to help) triggers Form 119.
FILING_PROFESSIONAL_ROLES: Final = ("attorney", "bankruptcy_petition_preparer")


@dataclass(frozen=True)
class HazardousProperty:
    """B101 line 14: property needing immediate attention."""

    description: str | None = None
    why_immediate: str | None = None
    address: Address = field(default_factory=Address)


@dataclass(frozen=True)
class PetitionBody:
    fee_handling: str | None = None
    rents_residence: bool | None = None  # line 11 → Form 101A
    eviction_judgment_against_you: bool | None = None
    small_business_status: str | None = None
    hazardous_property: HazardousProperty = field(default_factory=HazardousProperty)
    debt_character: str | None = None
    debt_character_other: str | None = None
    ch7_funds_available_for_creditors: bool | None = None  # line 17
    estimated_creditors: str | None = None
    estimated_assets: str | None = None
    estimated_liabilities: str | None = None


def _hazardous_property(
    value: object, path: str, errors: dict[str, str]
) -> HazardousProperty:
    if value is None:
        return HazardousProperty()
    if not isinstance(value, Mapping):
        errors[path] = "must be an object"
        return HazardousProperty()
    return HazardousProperty(
        description=narrative(value.get("description"), f"{path}.description", errors),
        why_immediate=narrative(
            value.get("why_immediate"), f"{path}.why_immediate", errors
        ),
        address=parse_address(value.get("address"), f"{path}.address", errors),
    )


def parse_petition(payload: Mapping[str, object]) -> PetitionBody:
    errors: dict[str, str] = {}
    body = PetitionBody(
        fee_handling=choice(
            payload.get("fee_handling"), FEE_HANDLING, "fee_handling", errors
        ),
        rents_residence=boolean(
            payload.get("rents_residence"), "rents_residence", errors
        ),
        eviction_judgment_against_you=boolean(
            payload.get("eviction_judgment_against_you"),
            "eviction_judgment_against_you",
            errors,
        ),
        small_business_status=choice(
            payload.get("small_business_status"),
            SMALL_BUSINESS_STATUSES,
            "small_business_status",
            errors,
        ),
        hazardous_property=_hazardous_property(
            payload.get("hazardous_property"), "hazardous_property", errors
        ),
        debt_character=choice(
            payload.get("debt_character"), DEBT_CHARACTERS, "debt_character", errors
        ),
        debt_character_other=text(
            payload.get("debt_character_other"), "debt_character_other", errors
        ),
        ch7_funds_available_for_creditors=boolean(
            payload.get("ch7_funds_available_for_creditors"),
            "ch7_funds_available_for_creditors",
            errors,
        ),
        estimated_creditors=choice(
            payload.get("estimated_creditors"),
            ESTIMATED_CREDITORS_BANDS,
            "estimated_creditors",
            errors,
        ),
        estimated_assets=choice(
            payload.get("estimated_assets"),
            ESTIMATED_DOLLAR_BANDS,
            "estimated_assets",
            errors,
        ),
        estimated_liabilities=choice(
            payload.get("estimated_liabilities"),
            ESTIMATED_DOLLAR_BANDS,
            "estimated_liabilities",
            errors,
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


PETITION: EntityKind[PetitionBody] = EntityKind(
    name="petition",
    collection="petitions",
    sk_prefix="PETITION",
    parse_body=parse_petition,
)


@dataclass(frozen=True)
class PriorCaseBody:
    """B101 line 9: a bankruptcy filed within the last 8 years."""

    district: str | None = None
    filed_on: str | None = None
    case_number: str | None = None


def parse_prior_case(payload: Mapping[str, object]) -> PriorCaseBody:
    errors: dict[str, str] = {}
    body = PriorCaseBody(
        district=text(payload.get("district"), "district", errors),
        filed_on=form_date(payload.get("filed_on"), "filed_on", errors),
        case_number=text(payload.get("case_number"), "case_number", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


PRIOR_CASE: EntityKind[PriorCaseBody] = EntityKind(
    name="prior_case",
    collection="prior_cases",
    sk_prefix="PRIOR_CASE",
    parse_body=parse_prior_case,
)


@dataclass(frozen=True)
class RelatedCaseBody:
    """B101 line 10: a pending case by a spouse, partner, or affiliate."""

    debtor_name: str | None = None
    relationship: str | None = None
    district: str | None = None
    filed_on: str | None = None
    case_number: str | None = None


def parse_related_case(payload: Mapping[str, object]) -> RelatedCaseBody:
    errors: dict[str, str] = {}
    body = RelatedCaseBody(
        debtor_name=text(payload.get("debtor_name"), "debtor_name", errors),
        relationship=text(payload.get("relationship"), "relationship", errors),
        district=text(payload.get("district"), "district", errors),
        filed_on=form_date(payload.get("filed_on"), "filed_on", errors),
        case_number=text(payload.get("case_number"), "case_number", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


RELATED_CASE: EntityKind[RelatedCaseBody] = EntityKind(
    name="related_case",
    collection="related_cases",
    sk_prefix="RELATED_CASE",
    parse_body=parse_related_case,
)


@dataclass(frozen=True)
class SoleProprietorshipBody:
    """B101 line 12: a business the debtor operates as a sole proprietor."""

    name: str | None = None
    address: Address = field(default_factory=Address)
    business_type: str | None = None


def parse_sole_proprietorship(payload: Mapping[str, object]) -> SoleProprietorshipBody:
    errors: dict[str, str] = {}
    body = SoleProprietorshipBody(
        name=text(payload.get("name"), "name", errors),
        address=parse_address(payload.get("address"), "address", errors),
        business_type=choice(
            payload.get("business_type"), BUSINESS_TYPES, "business_type", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


SOLE_PROPRIETORSHIP: EntityKind[SoleProprietorshipBody] = EntityKind(
    name="sole_proprietorship",
    collection="sole_proprietorships",
    sk_prefix="SOLE_PROPRIETORSHIP",
    parse_body=parse_sole_proprietorship,
)


@dataclass(frozen=True)
class FilingProfessionalBody:
    """B101 Part 7: the attorney block (printed name, firm, address, phone,
    email, bar number AND bar state, signature date) or a bankruptcy petition
    preparer. The name is four discrete parts like every person name — the
    form prints one line, and composing it is the projection's job."""

    role: str | None = None
    name: PersonName = field(default_factory=PersonName)
    firm_name: str | None = None
    address: Address = field(default_factory=Address)
    phone: str | None = None
    email: str | None = None
    bar_number: str | None = None
    bar_state: str | None = None
    signature_date: str | None = None


def parse_filing_professional(payload: Mapping[str, object]) -> FilingProfessionalBody:
    errors: dict[str, str] = {}
    body = FilingProfessionalBody(
        role=choice(payload.get("role"), FILING_PROFESSIONAL_ROLES, "role", errors),
        name=parse_name(payload.get("name"), "name", errors),
        firm_name=text(payload.get("firm_name"), "firm_name", errors),
        address=parse_address(payload.get("address"), "address", errors),
        phone=text(payload.get("phone"), "phone", errors, limit=32),
        email=text(payload.get("email"), "email", errors),
        bar_number=text(payload.get("bar_number"), "bar_number", errors, limit=32),
        bar_state=text(payload.get("bar_state"), "bar_state", errors, limit=2),
        signature_date=form_date(
            payload.get("signature_date"), "signature_date", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


FILING_PROFESSIONAL: EntityKind[FilingProfessionalBody] = EntityKind(
    name="filing_professional",
    collection="filing_professionals",
    sk_prefix="FILING_PROFESSIONAL",
    parse_body=parse_filing_professional,
)
