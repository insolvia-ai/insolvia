"""What every per-form projection module shares: the case file, the error,
and the formatting/landing helpers.

The rules these helpers encode are the package's contract (see __init__.py):
absent facts leave blank boxes, present facts that cannot land raise
`FormProjectionError`, and enum values map by meaning through each revision's
own export tables. Nothing here knows any particular form — a helper that
does belongs in that form's module.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from decimal import Decimal

from ..assets import AssetBody
from ..cases import Case
from ..claims import ClaimBody
from ..codebtors import CodebtorBody, CommunityHouseholdMemberBody
from ..contract_leases import ContractLeaseBody
from ..creditors import CreditorBody
from ..debtors import Debtor
from ..exemption_claims import ExemptionBody
from ..expenses import DependentBody, ExpenseBody, HouseholdBody
from ..form_fill import FieldFill, Option, Text
from ..form_templates import FormRelease
from ..income import EmploymentBody, IncomeSummaryBody
from ..petitions import (
    FilingProfessionalBody,
    PetitionBody,
    PriorCaseBody,
    RelatedCaseBody,
    SoleProprietorshipBody,
)
from ..sofa import SofaEntryBody

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
    is what makes the goldens possible).

    Collections whose records are referenced BY ID from other records
    (creditors from claims, assets from exemptions, claims and contracts
    from codebtors, households from expenses) carry `(id, body)` pairs so a
    reference resolves without a store; the rest are bare bodies.
    """

    case: Case
    debtors: tuple[Debtor, ...] = ()
    petition: PetitionBody | None = None
    prior_cases: tuple[PriorCaseBody, ...] = ()
    related_cases: tuple[RelatedCaseBody, ...] = ()
    sole_proprietorships: tuple[SoleProprietorshipBody, ...] = ()
    filing_professionals: tuple[FilingProfessionalBody, ...] = ()
    employments: tuple[EmploymentBody, ...] = ()
    income_summaries: tuple[IncomeSummaryBody, ...] = ()
    # 106A/B and 106C.
    assets: tuple[tuple[str, AssetBody], ...] = ()
    exemptions: tuple[ExemptionBody, ...] = ()
    # 106D, 106E/F, 106G, 106H.
    creditors: tuple[tuple[str, CreditorBody], ...] = ()
    claims: tuple[tuple[str, ClaimBody], ...] = ()
    contract_leases: tuple[tuple[str, ContractLeaseBody], ...] = ()
    codebtors: tuple[CodebtorBody, ...] = ()
    community_household_members: tuple[CommunityHouseholdMemberBody, ...] = ()
    # 106J / 106J-2.
    households: tuple[tuple[str, HouseholdBody], ...] = ()
    expenses: tuple[ExpenseBody, ...] = ()
    dependents: tuple[DependentBody, ...] = ()
    # B107.
    sofa_entries: tuple[SofaEntryBody, ...] = ()

    def debtor(self, *roles: str) -> Debtor | None:
        return next((d for d in self.debtors if d.filing_role in roles), None)

    def asset(self, asset_id: str | None) -> AssetBody | None:
        return next((body for id_, body in self.assets if id_ == asset_id), None)

    def creditor(self, creditor_id: str | None) -> CreditorBody | None:
        return next((body for id_, body in self.creditors if id_ == creditor_id), None)


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


def amount(value: str | None) -> Decimal:
    return Decimal(value) if value is not None else Decimal("0")


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
    rows_out: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if len(candidate) <= width or not current:
            current = candidate
        else:
            rows_out.append(current)
            current = word
    if current:
        rows_out.append(current)
    if len(rows_out) > lines:
        problems.append(
            f"{where}: text needs {len(rows_out)} lines; the form prints {lines}"
        )
        return rows_out[:lines]
    return rows_out


# --- shared landing helpers --------------------------------------------------


def by_order(
    release: FormRelease, field_id: str, vocab: tuple[str, ...], value: str
) -> Option:
    """Select a radio's export by the stored vocabulary's POSITION in the
    spec's option list — for groups whose exports are unreliable (B101 line
    19's misprinted band) or spelled per revision."""
    return Option(release.field(field_id).options[vocab.index(value)].value)


def canonical_option(
    release: FormRelease, field_id: str, pdf_name: str, canonical: str
) -> Option:
    """Select the export whose spec `maps_to_value` names the stored enum
    member, narrowed to the states THIS widget declares — the per-row groups
    (106A/B's who-has-interest, 106D/E/F's who-incurred) spell their exports
    differently row by row, including misspellings, and the spec's canonical
    annotations are the reliable identity."""
    spec = release.field(field_id)
    wanted = [o.value for o in spec.options if o.maps_to_value == canonical]
    states = release.widgets[pdf_name].states
    matches = [value for value in wanted if value in states]
    if len(matches) != 1:
        raise KeyError(
            f"{field_id} -> {pdf_name!r}: no single export maps to {canonical!r}"
        )
    return Option(matches[0])


def yes_no(release: FormRelease, field_id: str, answer: bool) -> Option:
    """The No/Yes pairs, tolerant of the PDF's spellings: casing ('no' vs
    'No'), suffixes ('Yes_2'), and the quirky lone 'On' that several gates
    export for their yes box."""
    states = [o.value for o in release.field(field_id).options]

    def classify(state: str) -> str | None:
        lowered = state.lower()
        if lowered.startswith("yes"):
            return "yes"
        if lowered.startswith("no"):
            return "no"
        return None

    wanted = "yes" if answer else "no"
    named = [s for s in states if classify(s) == wanted]
    if len(named) == 1:
        return Option(named[0])
    # The quirky pair: one recognisable state and one opaque export ('On').
    other = "no" if answer else "yes"
    opaque = [s for s in states if classify(s) is None]
    if not named and len(opaque) == 1 and any(classify(s) == other for s in states):
        return Option(opaque[0])
    raise KeyError(f"{field_id} has no {wanted} option")  # pragma: no cover


def address_fills(
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


def text_or_none(value: str | None) -> Text | None:
    """A Text fill for a present value, a blank box for an absent one."""
    return Text(value) if value else None


def rows(
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


def row_fill(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    index: int,
    fill: FieldFill | None,
    problems: list[str],
) -> None:
    """Land one instance of a repeated field on its printed row `index`;
    a row past the printed set is an error, a None fill is a blank box."""
    if fill is None:
        return
    spec = release.field(field_id)
    if index >= len(spec.pdf_names):
        problems.append(
            f"{field_id}: row {index + 1} does not exist — the form "
            f"prints {len(spec.pdf_names)} rows"
        )
        return
    if len(spec.pdf_names) == 1:
        values[field_id] = fill
        return
    entry = values.setdefault(field_id, {})
    assert isinstance(entry, dict)  # single-widget path returned above
    entry[spec.pdf_names[index]] = fill
