"""Projection: case data in, fill-engine values out — per form, per revision.

The forms are projections of facts (case-data-model.md): entities hold the
facts, and THIS module owns which fact lands on which line of which printed
revision, including the arithmetic the model refuses to store ("Derived
values are computed, never stored"). The fill engine (core/form_fill.py)
stays verbatim and structural; everything semantic — money and date
formatting, enum-to-export-state mapping, composing a person's name into a
one-line box, summing line 4 from lines 2 and 3 — happens here, where a
reviewer can read the whole mapping for a form in one place.

Projections are REGISTERED PER RELEASE. A form revision bump is a template
change with its own mapping and its own goldens, never an in-place edit
(issue #93): when B101 revises, the new release gets a new projector entry
(usually delegating to the old one plus the delta), and a case pinned to the
old revision keeps projecting through the mapping it was prepared against.
`project` refuses a release nothing was written for — rendering a revision
through another revision's mapping is exactly the silent drift this registry
exists to prevent.

Three rules the mappings follow:

- **Absent facts leave blank boxes.** Intake is progressive; a None simply
  does not emit a fill. Whether a blank is ACCEPTABLE is the pre-filing
  completeness gate's question, not a projection error.
- **Present facts that cannot land are errors.** A third alias when the form
  prints two rows, an explanation longer than the printed lines — dropping
  either silently would put a signed form in front of a debtor with facts
  missing, so `FormProjectionError` names every one instead.
- **Enum values map by MEANING, spelled per revision.** The stored vocabulary
  is stable; each mapping owns the translation to that revision's exact
  export states — including B101's misspelled ones — and to option ORDER
  where the exports themselves are unreliable (lines 18-20's bands, where
  line 19's fourth-bracket export is missing a digit that line 20's has).

What deliberately does NOT project yet, each blank until its owner lands:
tax identifiers (line 3 — encrypted storage with audited reads is its own
work), the amended-filing caption (no `case.is_amended` yet), wet-signature
lines (never machine-filled), the court's case number, and the pro se
acknowledgment boxes (the filer's own act).
"""

from __future__ import annotations

import re
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass
from decimal import Decimal
from typing import Final

from .cases import Case
from .debtors import Debtor
from .form_fill import FieldFill, Option, Text
from .form_templates import FormRelease
from .income import EmploymentBody, IncomeSummaryBody
from .petitions import (
    BUSINESS_TYPES,
    ESTIMATED_CREDITORS_BANDS,
    ESTIMATED_DOLLAR_BANDS,
    SMALL_BUSINESS_STATUSES,
    FilingProfessionalBody,
    PetitionBody,
    PriorCaseBody,
    RelatedCaseBody,
    SoleProprietorshipBody,
)

FieldValues = dict[str, "FieldFill | dict[str, FieldFill]"]


class FormProjectionError(ValueError):
    """A stored fact that cannot land on this revision of the form — an
    overflow the printed rows cannot hold, a narrative longer than its
    printed lines. Carries every problem, like FormFillError does."""

    def __init__(self, problems: list[str]) -> None:
        self.problems = tuple(problems)
        super().__init__("; ".join(problems))


@dataclass(frozen=True)
class CaseFile:
    """Everything a projection reads, in presentation order.

    The caller (packet assembly's worker, or a single-form render) loads the
    case's records and hands over the BODIES, already sorted the way rows
    should print — creation order, the collections' own listing order. The
    projection does not reach into any store (it is a pure function, which
    is what makes the goldens possible)."""

    case: Case
    debtors: tuple[Debtor, ...] = ()
    petition: PetitionBody | None = None
    prior_cases: tuple[PriorCaseBody, ...] = ()
    related_cases: tuple[RelatedCaseBody, ...] = ()
    sole_proprietorships: tuple[SoleProprietorshipBody, ...] = ()
    filing_professionals: tuple[FilingProfessionalBody, ...] = ()
    employments: tuple[EmploymentBody, ...] = ()
    income_summaries: tuple[IncomeSummaryBody, ...] = ()

    def debtor(self, *roles: str) -> Debtor | None:
        return next((d for d in self.debtors if d.filing_role in roles), None)


# --- shared formatting -------------------------------------------------------


def format_money(value: str | Decimal) -> str:
    """The printed form of a stored money string: thousands-grouped, two
    decimals, no currency sign (the forms print their own $)."""
    return f"{Decimal(value):,.2f}"


def format_date(value: str) -> str:
    """YYYY-MM-DD (the stored form-date shape) -> MM/DD/YYYY as the printed
    forms' example text spells dates."""
    year, month, day = value.split("-")
    return f"{month}/{day}/{year}"


def full_name(debtor_name: object) -> str | None:
    parts = [
        getattr(debtor_name, part, None)
        for part in ("given", "middle", "surname", "suffix")
    ]
    joined = " ".join(p for p in parts if p)
    return joined or None


def wrap_lines(
    value: str, *, width: int, lines: int, where: str, problems: list[str]
) -> list[str]:
    """Split a narrative across a form's fixed set of printed lines.

    Width is an approximation (the PDF boxes carry no character caps), kept
    conservative; text that cannot fit is an ERROR, not a truncation."""
    words = value.split()
    rows: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if len(candidate) <= width or not current:
            current = candidate
        else:
            rows.append(current)
            current = word
    if current:
        rows.append(current)
    if len(rows) > lines:
        problems.append(
            f"{where}: text needs {len(rows)} lines; the form prints {lines}"
        )
        return rows[:lines]
    return rows


# --- the registry ------------------------------------------------------------

Projector = Callable[[FormRelease, CaseFile], FieldValues]


def project(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for one form release, from one case's facts.

    Raises KeyError when no mapping exists for the release — a new revision
    must bring its own mapping and goldens before anything renders it."""
    projector = PROJECTIONS.get((release.series_id, release.pin))
    if projector is None:
        raise KeyError(
            f"no projection is written for {release.release_id}; a revision "
            "bump is a template change with its own mapping and goldens"
        )
    return projector(release, case_file)


# --- B101 @ 2024-06-22 (revision 06/24) --------------------------------------

_CHAPTER_EXPORTS: Final = {
    7: "Chapter 7",
    11: "Chapter 11",
    12: "Chapter 12",
    13: "Chapter 13",
}

_FEE_HANDLING_EXPORTS: Final = {
    "full": "Pay entirely",
    "installments": "Pay in installments",
    "waiver": "Request fees waived",
}

_VENUE_EXPORTS: Final = {
    "lived_longest_180_days": "Lived in this district",
    "other": "Other",
}

# B101 line 15's exports, verified against the PDF's widget geometry at
# ingestion (the release manifest records the check).
_COUNSELING_EXPORTS: Final = {
    "completed_with_certificate": "1",
    "completed_certificate_pending": "2",
    "exigent_circumstances_waiver_requested": "On",
    "not_required": "4",
}

_COUNSELING_EXEMPTION_EXPORTS: Final = {
    "incapacity": "Incapacity",
    "disability": "Disability",
    "active_duty": "Active duty",
}


def _band_option(
    release: FormRelease, field_id: str, bands: tuple[str, ...], value: str
) -> Option:
    """Bands map by ORDER, each line reading its own spec options — the
    exports are per-line and one of line 19's is misprinted, so the position
    in the printed bracket list is the reliable identity."""
    return Option(release.field(field_id).options[bands.index(value)].value)


def _by_order(
    release: FormRelease, field_id: str, vocab: tuple[str, ...], value: str
) -> Option:
    return Option(release.field(field_id).options[vocab.index(value)].value)


def _yes_no(release: FormRelease, field_id: str, answer: bool) -> Option:
    """The No/Yes pairs, tolerant of the PDF's casing ('no' vs 'No')."""
    wanted = "yes" if answer else "no"
    for option in release.field(field_id).options:
        if option.value.lower() == wanted:
            return Option(option.value)
    raise KeyError(f"{field_id} has no {wanted} option")  # pragma: no cover


def _address_fills(
    values: FieldValues,
    address: object,
    *,
    street: str,
    street2: str | None,
    city: str,
    state: str,
    zip_code: str,
    county: str | None = None,
) -> None:
    """Land an Address on a street/city/state/zip box set, skipping blanks."""

    def put(field_id: str | None, value: str | None) -> None:
        if field_id is not None and value:
            values[field_id] = Text(value)

    put(street, getattr(address, "line1", None))
    put(street2, getattr(address, "line2", None))
    put(city, getattr(address, "city", None))
    put(state, getattr(address, "state", None))
    put(zip_code, getattr(address, "postal_code", None))
    put(county, getattr(address, "county", None))


def _rows(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    row_values: Iterable[str | None],
    problems: list[str],
) -> None:
    """Fill a repeated field's printed rows in order; overflow is an error."""
    spec = release.field(field_id)
    fills: dict[str, FieldFill] = {}
    for index, value in enumerate(row_values):
        if value is None:
            continue
        if index >= len(spec.pdf_names):
            problems.append(
                f"{field_id}: row {index + 1} does not exist — the form "
                f"prints {len(spec.pdf_names)} rows"
            )
            continue
        fills[spec.pdf_names[index]] = Text(value)
    if fills:
        values[field_id] = (
            fills if len(spec.pdf_names) > 1 else fills[spec.pdf_names[0]]
        )


def _project_b101_0624(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}
    case = case_file.case
    debtor1 = case_file.debtor("debtor_1")
    debtor2 = case_file.debtor("debtor_2")  # B101 never prints a non-filing spouse

    values["caption.district"] = Text(case.district)
    values["caption.chapter"] = Option(_CHAPTER_EXPORTS[case.chapter])
    if debtor1 is not None and (header := full_name(debtor1.name)):
        values["caption.header_debtor1_name"] = Text(header)

    # Part 1 — identity, per debtor column.
    for debtor, col in ((debtor1, "debtor1"), (debtor2, "debtor2")):
        if debtor is None:
            continue
        name = debtor.name
        for part, box in (
            ("given", "first_name"),
            ("middle", "middle_name"),
            ("surname", "last_name"),
            ("suffix", "suffix"),
        ):
            if value := getattr(name, part, None):
                values[f"line_1_{col}_{box}"] = Text(value)

        # Line 2 — the 8-year alias rows (the form prints two per debtor).
        aliases = debtor.other_names_used
        for suffix_id, attr in (
            ("other_given", "given"),
            ("other_middle", "middle"),
            ("other_surname", "surname"),
            ("business_name", "business_name"),
        ):
            _rows(
                release,
                values,
                f"line_2_{col}_{suffix_id}",
                [getattr(alias, attr, None) for alias in aliases],
                problems,
            )

        # Line 4 — EINs (two rows per debtor). The boxes are 9-character
        # digit combs, so the customary dash is stripped on the way in.
        _rows(
            release,
            values,
            f"line_4_{col}_ein",
            [re.sub(r"\D", "", ein) or None for ein in debtor.employer_ids],
            problems,
        )

        # Line 5 — where the debtor lives; mailing only when it differs.
        _address_fills(
            values,
            debtor.residence_address,
            street=f"line_5_{col}_street",
            street2=f"line_5_{col}_street2",
            city=f"line_5_{col}_city",
            state=f"line_5_{col}_state",
            zip_code=f"line_5_{col}_zip",
            county=f"line_5_{col}_county",
        )
        if debtor.mailing_address != type(debtor.mailing_address)() and (
            debtor.mailing_address != debtor.residence_address
        ):
            _address_fills(
                values,
                debtor.mailing_address,
                street=f"line_5_{col}_mailing_street",
                # The mailing column prints a P.O. Box line where the
                # residence prints a second street line.
                street2=f"line_5_{col}_mailing_pobox",
                city=f"line_5_{col}_mailing_city",
                state=f"line_5_{col}_mailing_state",
                zip_code=f"line_5_{col}_mailing_zip",
            )

        # Line 6 — venue.
        venue = debtor.venue
        if venue.basis is not None:
            values[f"line_6_{col}_venue_basis"] = Option(_VENUE_EXPORTS[venue.basis])
        if venue.explanation:
            _rows(
                release,
                values,
                f"line_6_{col}_venue_explanation",
                wrap_lines(
                    venue.explanation,
                    width=85,
                    lines=4,
                    where=f"line_6_{col}_venue_explanation",
                    problems=problems,
                ),
                problems,
            )

        # Line 15 — credit counseling, the four-way per debtor.
        counseling = debtor.credit_counseling
        if counseling.status is not None:
            values[f"line_15_{col}_credit_counseling"] = Option(
                _COUNSELING_EXPORTS[counseling.status]
            )
        if counseling.status == "not_required" and counseling.exemption_reason:
            values[f"line_15_{col}_exemption_reason"] = Option(
                _COUNSELING_EXEMPTION_EXPORTS[counseling.exemption_reason]
            )

        # Signature dates (the signature lines themselves stay wet).
        if debtor.signed_at:
            values[f"sign.{col}_executed_on"] = Text(format_date(debtor.signed_at))

    # Line 9 — prior bankruptcies (three printed rows).
    values["line_9_prior_bankruptcy"] = _yes_no(
        release, "line_9_prior_bankruptcy", bool(case_file.prior_cases)
    )
    _rows(
        release,
        values,
        "line_9_prior_district",
        [p.district for p in case_file.prior_cases],
        problems,
    )
    _rows(
        release,
        values,
        "line_9_prior_when",
        [p.filed_on and format_date(p.filed_on) for p in case_file.prior_cases],
        problems,
    )
    _rows(
        release,
        values,
        "line_9_prior_case_number",
        [p.case_number for p in case_file.prior_cases],
        problems,
    )

    # Line 10 — related pending cases (two printed rows).
    values["line_10_related_pending"] = _yes_no(
        release, "line_10_related_pending", bool(case_file.related_cases)
    )
    related = case_file.related_cases
    _rows(
        release,
        values,
        "line_10_related_debtor",
        [r.debtor_name for r in related],
        problems,
    )
    _rows(
        release,
        values,
        "line_10_related_relationship",
        [r.relationship for r in related],
        problems,
    )
    _rows(
        release,
        values,
        "line_10_related_district",
        [r.district for r in related],
        problems,
    )
    _rows(
        release,
        values,
        "line_10_related_when",
        [r.filed_on and format_date(r.filed_on) for r in related],
        problems,
    )
    _rows(
        release,
        values,
        "line_10_related_case_number",
        [r.case_number for r in related],
        problems,
    )

    # Line 12 — sole proprietorships (the form prints ONE business block).
    values["line_12_sole_proprietor"] = _yes_no(
        release, "line_12_sole_proprietor", bool(case_file.sole_proprietorships)
    )
    if len(case_file.sole_proprietorships) > 1:
        problems.append(
            "line_12: the form prints one sole-proprietorship block; "
            f"the case holds {len(case_file.sole_proprietorships)}"
        )
    if case_file.sole_proprietorships:
        business = case_file.sole_proprietorships[0]
        if business.name:
            values["line_12_business_name"] = Text(business.name)
        _address_fills(
            values,
            business.address,
            street="line_12_business_street",
            street2="line_12_business_street2",
            city="line_12_business_city",
            state="line_12_business_state",
            zip_code="line_12_business_zip",
        )
        if business.business_type is not None:
            values["line_12_business_type"] = _by_order(
                release, "line_12_business_type", BUSINESS_TYPES, business.business_type
            )

    # Line 17 — the chapter answers it; only a Chapter 7 filer goes on to
    # the funds question below.
    values["line_17_filing_under_ch7"] = _yes_no(
        release, "line_17_filing_under_ch7", case.chapter == 7
    )

    # Parts 2, 4, 6 — the petition's answers.
    petition = case_file.petition
    if petition is not None:
        if petition.fee_handling is not None:
            values["line_8_fee_handling"] = Option(
                _FEE_HANDLING_EXPORTS[petition.fee_handling]
            )
        if petition.rents_residence is not None:
            values["line_11_rents_residence"] = _yes_no(
                release, "line_11_rents_residence", petition.rents_residence
            )
            if petition.rents_residence and (
                petition.eviction_judgment_against_you is not None
            ):
                values["line_11_eviction_judgment"] = _yes_no(
                    release,
                    "line_11_eviction_judgment",
                    petition.eviction_judgment_against_you,
                )
        if petition.small_business_status is not None:
            values["line_13_small_business_status"] = _by_order(
                release,
                "line_13_small_business_status",
                SMALL_BUSINESS_STATUSES,
                petition.small_business_status,
            )

        # Line 14 — hazardous property.
        hazard = petition.hazardous_property
        has_hazard = bool(
            hazard.description
            or hazard.why_immediate
            or hazard.address != type(hazard.address)()
        )
        values["line_14_hazardous_property"] = _yes_no(
            release, "line_14_hazardous_property", has_hazard
        )
        if hazard.description:
            _rows(
                release,
                values,
                "line_14_hazard_description",
                wrap_lines(
                    hazard.description,
                    width=95,
                    lines=2,
                    where="line_14_hazard_description",
                    problems=problems,
                ),
                problems,
            )
        if hazard.why_immediate:
            _rows(
                release,
                values,
                "line_14_why_immediate",
                wrap_lines(
                    hazard.why_immediate,
                    width=95,
                    lines=2,
                    where="line_14_why_immediate",
                    problems=problems,
                ),
                problems,
            )
        _address_fills(
            values,
            hazard.address,
            street="line_14_property_street",
            street2="line_14_property_street2",
            city="line_14_property_city",
            state="line_14_property_state",
            zip_code="line_14_property_zip",
        )

        # Line 16 — the debt-character three-way, printed as two gates.
        if petition.debt_character is not None:
            consumer = petition.debt_character == "consumer"
            values["line_16a_consumer_debts"] = _yes_no(
                release, "line_16a_consumer_debts", consumer
            )
            if not consumer:
                values["line_16b_business_debts"] = _yes_no(
                    release,
                    "line_16b_business_debts",
                    petition.debt_character == "business",
                )
            if petition.debt_character == "other" and petition.debt_character_other:
                values["line_16c_other_debts"] = Text(petition.debt_character_other)

        if case.chapter == 7 and (
            petition.ch7_funds_available_for_creditors is not None
        ):
            values["line_17_funds_available"] = _yes_no(
                release,
                "line_17_funds_available",
                petition.ch7_funds_available_for_creditors,
            )

        # Lines 18-20 — the self-selected estimate brackets, by order.
        if petition.estimated_creditors is not None:
            values["line_18_estimated_creditors"] = _band_option(
                release,
                "line_18_estimated_creditors",
                ESTIMATED_CREDITORS_BANDS,
                petition.estimated_creditors,
            )
        if petition.estimated_assets is not None:
            values["line_19_estimated_assets"] = _band_option(
                release,
                "line_19_estimated_assets",
                ESTIMATED_DOLLAR_BANDS,
                petition.estimated_assets,
            )
        if petition.estimated_liabilities is not None:
            values["line_20_estimated_liabilities"] = _band_option(
                release,
                "line_20_estimated_liabilities",
                ESTIMATED_DOLLAR_BANDS,
                petition.estimated_liabilities,
            )

    # Part 7 — who signs. An attorney fills the attorney block; without one,
    # the pro se block carries the debtor's own contact details, and the
    # acknowledgment boxes stay the filer's own act.
    attorney = next(
        (p for p in case_file.filing_professionals if p.role == "attorney"), None
    )
    preparer = next(
        (
            p
            for p in case_file.filing_professionals
            if p.role == "bankruptcy_petition_preparer"
        ),
        None,
    )
    if attorney is not None:
        if printed := full_name(attorney.name):
            values["attorney.printed_name"] = Text(printed)
        if attorney.firm_name:
            values["attorney.firm_name"] = Text(attorney.firm_name)
        _address_fills(
            values,
            attorney.address,
            street="attorney.street",
            street2="attorney.street2",
            city="attorney.city",
            state="attorney.state",
            zip_code="attorney.zip",
        )
        for field_id, value in (
            ("attorney.phone", attorney.phone),
            ("attorney.email", attorney.email),
            ("attorney.bar_number", attorney.bar_number),
            ("attorney.bar_state", attorney.bar_state),
        ):
            if value:
                values[field_id] = Text(value)
        if attorney.signature_date:
            values["attorney.date_signed"] = Text(format_date(attorney.signature_date))
    else:
        values["prose.paid_preparer"] = _yes_no(
            release, "prose.paid_preparer", preparer is not None
        )
        if preparer is not None and (preparer_name := full_name(preparer.name)):
            values["prose.preparer_name"] = Text(preparer_name)
        for debtor, col in ((debtor1, "debtor1"), (debtor2, "debtor2")):
            if debtor is None:
                continue
            for field_id, value in (
                (f"prose.{col}_phone", debtor.phone),
                (f"prose.{col}_cell", debtor.mobile),
                (f"prose.{col}_email", debtor.email),
            ):
                if value:
                    values[field_id] = Text(value)
            if debtor.signed_at:
                values[f"prose.{col}_date"] = Text(format_date(debtor.signed_at))

    if problems:
        raise FormProjectionError(sorted(problems))
    return values


# --- B106I @ 2015-12-01 (revision 12/15) -------------------------------------

_EMPLOYMENT_EXPORTS: Final = {"employed": "employed", "not_employed": "unemployed"}

# The 5a-5h deduction lines and 8a-8h other-income lines, in printed order:
# (line key, IncomeSummaryBody attribute).
_DEDUCTION_LINES: Final = (
    ("5a_tax_medicare_ss", "deduction_tax"),
    ("5b_mandatory_retirement", "deduction_mandatory_retirement"),
    ("5c_voluntary_retirement", "deduction_voluntary_retirement"),
    ("5d_retirement_loan_repayment", "deduction_retirement_loan_repayment"),
    ("5e_insurance", "deduction_insurance"),
    ("5f_domestic_support", "deduction_domestic_support"),
    ("5g_union_dues", "deduction_union_dues"),
    ("5h_other_deductions", "deduction_other"),
)

_OTHER_INCOME_LINES: Final = (
    ("8a_rental_business_net", "business_net_income"),
    ("8b_interest_dividends", "interest_and_dividends"),
    ("8c_family_support", "family_support"),
    ("8d_unemployment", "unemployment"),
    ("8e_social_security", "social_security"),
    ("8f_government_assistance", "other_government_assistance"),
    ("8g_pension_retirement", "pension_or_retirement"),
    ("8h_other_income", "other_monthly_income"),
)


def _amount(value: str | None) -> Decimal:
    return Decimal(value) if value is not None else Decimal("0")


@dataclass(frozen=True)
class _Column:
    """One 106I debtor column: the column digit ('1'/'2') and its facts."""

    digit: str
    employment: EmploymentBody | None
    summary: IncomeSummaryBody | None


def _column_widget(release: FormRelease, field_id: str, digit: str) -> str:
    """The PDF field carrying this debtor column — matched by the 'Debtor 1'
    / 'Debtor 2' marker in the name (case varies, and line 4's copy row
    appends an 'a'), never by position."""
    pattern = re.compile(rf"[Dd]ebtor {digit}a?$")
    spec = release.field(field_id)
    matches = [n for n in spec.pdf_names if pattern.search(n)]
    if len(matches) != 1:  # pragma: no cover - the spec's columns are fixed
        raise KeyError(f"{field_id} has no single debtor-{digit} column")
    return matches[0]


def _put_column(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    digit: str,
    fill: FieldFill,
) -> None:
    spec = release.field(field_id)
    if len(spec.pdf_names) == 1:
        values[field_id] = fill
        return
    existing = values.setdefault(field_id, {})
    assert isinstance(existing, dict)  # single-widget path returned above
    existing[_column_widget(release, field_id, digit)] = fill


def _project_b106i_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}
    case = case_file.case
    debtor1 = case_file.debtor("debtor_1")
    # 106I's second column belongs to debtor 2 OR a non-filing spouse.
    debtor2 = case_file.debtor("debtor_2", "non_filing_spouse")

    values["caption.district"] = Text(case.district)
    if debtor1 is not None and (name1 := full_name(debtor1.name)):
        values["caption.debtor1_name"] = Text(name1)
    if debtor2 is not None and (name2 := full_name(debtor2.name)):
        values["caption.debtor2_name"] = Text(name2)

    def column(debtor: Debtor | None, digit: str) -> _Column:
        if debtor is None:
            return _Column(digit=digit, employment=None, summary=None)
        return _Column(
            digit=digit,
            employment=next(
                (e for e in case_file.employments if e.debtor_id == debtor.id), None
            ),
            summary=next(
                (s for s in case_file.income_summaries if s.debtor_id == debtor.id),
                None,
            ),
        )

    columns = (column(debtor1, "1"), column(debtor2, "2"))
    monthly_totals: list[Decimal] = []

    for col in columns:
        employment = col.employment
        if employment is not None:
            if employment.status is not None:
                _put_column(
                    release,
                    values,
                    "employment_status",
                    col.digit,
                    Option(_EMPLOYMENT_EXPORTS[employment.status]),
                )
            for field_id, value in (
                ("occupation", employment.occupation),
                ("employer_name", employment.employer_name),
            ):
                if value:
                    _put_column(release, values, field_id, col.digit, Text(value))
            if employment.employed_since:
                # The form asks "how long employed there?"; the stored fact is
                # the start date, printed as such rather than derived into a
                # duration that would silently age.
                _put_column(
                    release,
                    values,
                    "how_long_employed",
                    col.digit,
                    Text(f"Since {format_date(employment.employed_since)}"),
                )
            address = employment.employer_address
            spec = release.field("employer_address")
            for part, box in (
                (address.line1, f"Employers Street1 Debtor {col.digit}"),
                (address.line2, f"Employers Street2 Debtor {col.digit}"),
                (address.city, f"Employers City Debtor {col.digit}"),
                (address.state, f"Employers State Debtor {col.digit}"),
                # The ZIP boxes alone spell 'debtor' lowercase in the PDF.
                (address.postal_code, f"Employers Zip debtor {col.digit}"),
            ):
                if part and box in spec.pdf_names:
                    entry = values.setdefault("employer_address", {})
                    assert isinstance(entry, dict)
                    entry[box] = Text(part)

        summary = col.summary
        if summary is None:
            continue

        def put_money(field_id: str, amount: Decimal, digit: str = col.digit) -> None:
            _put_column(release, values, field_id, digit, Text(format_money(amount)))

        # Lines 2-3 as stored; line 4 = 2 + 3, twice (the page-2 copy row).
        if summary.wages is not None:
            put_money("line_2_gross_wages", _amount(summary.wages))
        if summary.overtime is not None:
            put_money("line_3_overtime", _amount(summary.overtime))
        gross = _amount(summary.wages) + _amount(summary.overtime)
        put_money("line_4_gross_income", gross)
        put_money("line_4_copy", gross)

        # Line 5's eight deduction lines; line 6 sums them; 7 = 4 - 6.
        deductions = Decimal("0")
        for line, attr in _DEDUCTION_LINES:
            stored = getattr(summary, attr)
            if stored is not None:
                put_money(f"line_{line}", _amount(stored))
            deductions += _amount(stored)
        put_money("line_6_total_deductions", deductions)
        take_home = gross - deductions
        put_money("line_7_take_home_pay", take_home)

        # Line 8's other income; 9 sums it; 10 = 7 + 9.
        other_income = Decimal("0")
        for line, attr in _OTHER_INCOME_LINES:
            stored = getattr(summary, attr)
            if stored is not None:
                put_money(f"line_{line}", _amount(stored))
            other_income += _amount(stored)
        put_money("line_9_total_other_income", other_income)
        monthly = take_home + other_income
        put_money("line_10_monthly_income", monthly)
        monthly_totals.append(monthly)

        if summary.deduction_other_specify:
            values["line_5h_other_deductions_specify"] = Text(
                summary.deduction_other_specify
            )
        if summary.other_government_assistance_specify:
            values["line_8f_government_assistance_specify"] = Text(
                summary.other_government_assistance_specify
            )
        if summary.other_monthly_income_specify:
            values["line_8h_other_income_specify"] = Text(
                summary.other_monthly_income_specify
            )

    if monthly_totals:
        combined = sum(monthly_totals, Decimal("0"))
        values["line_10_combined"] = Text(format_money(combined))

        # Line 11 is one value for the household, carried on the debtor-1
        # summary (case-data-model.md); line 12 = 10 + 11.
        summary1 = columns[0].summary
        contributions = _amount(summary1.household_contributions if summary1 else None)
        if summary1 is not None and summary1.household_contributions is not None:
            values["line_11_household_contributions"] = Text(
                format_money(contributions)
            )
        values["line_12_combined_monthly_income"] = Text(
            format_money(combined + contributions)
        )

        # Line 13 — the change question, on the debtor-1 summary.
        if summary1 is not None and summary1.change_expected is not None:
            values["line_13_change_expected"] = _yes_no(
                release, "line_13_change_expected", summary1.change_expected
            )
            if summary1.change_expected and summary1.change_explanation:
                values["line_13_change_explanation"] = Text(summary1.change_explanation)

    return values


PROJECTIONS: Final[Mapping[tuple[str, str], Projector]] = {
    ("form/b101", "2024-06-22"): _project_b101_0624,
    ("form/b106i", "2015-12-01"): _project_b106i_1215,
}
