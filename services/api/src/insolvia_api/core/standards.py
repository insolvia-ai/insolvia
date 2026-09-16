"""`GET /v1/cases/<id>/standards` — the IRS National and Local Standards for a
case's jurisdiction and household (issue #348, the income-and-expenses
workbench), read-only and pure.

THIS IS NOT THE MEANS TEST. `core/means_test.py` (issue #101, #349) is the
legally-binding § 707(b) calculation, and it reads `state`/`county`/household
composition from `means_test_input` — figures the debtor confirms. This
module exists so the workbench can show the published allowance next to
Schedule J's health-care and transportation lines the moment an expense is
being typed, before that confirmation happens at all. So the jurisdiction and
household size here are the best facts already on file — Debtor 1's
residence address (B101 line 5 prints a county box for exactly this reason),
plus the filing debtors and any dependents recorded as living with them — not
a legal determination of B122A-2 line 5's "number of people used in
determining deductions". A means-test module must not import this one for
that figure; the two are allowed to (and, once vehicle counts and marital
status are entered, will) disagree.

Every lookup that can fail (an unsupported state, a county the Local
Standards table does not carry) is reported in `problems`, never guessed —
the ust_data module's own no-fallback rule, carried through. The route
answers 200 with partial figures and a non-empty `problems` list rather than
an error status, the same progressive-intake shape `case_summary` and
`packet_assembly` use: a case mid-intake has an incomplete standards lookup
the way it has an incomplete schedule, and that is shown, not refused.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import date
from decimal import ROUND_HALF_UP, Decimal

from insolvia_core.debtors import Debtor
from insolvia_core.expenses import DependentBody

from . import ust_data

_CENT = Decimal("0.01")


def _money(value: Decimal) -> str:
    return f"{value.quantize(_CENT, rounding=ROUND_HALF_UP):f}"


@dataclass(frozen=True)
class HouseholdJurisdiction:
    """Where to look the standards up, and how many people they cover."""

    state: str
    county: str
    household_size: int
    source: str


def household_jurisdiction(
    *, debtors: Sequence[Debtor], dependents: Sequence[DependentBody]
) -> HouseholdJurisdiction | None:
    """Debtor 1's residence state/county, plus a household size counted from
    the filing debtors and their co-resident dependents. `None` when Debtor 1
    or the state/county are not entered yet — a fact for the caller to report
    as a problem, not an exception: a case with no debtor is simply too early
    for this lookup, exactly as it is too early for the completeness gate."""
    debtor1 = next((d for d in debtors if d.filing_role == "debtor_1"), None)
    if debtor1 is None:
        return None
    address = debtor1.residence_address
    if not address.state or not address.county:
        return None
    filers = sum(
        1
        for debtor in debtors
        if debtor.filing_role in ("debtor_1", "debtor_2", "non_filing_spouse")
    )
    living_with = sum(1 for dependent in dependents if dependent.lives_with_debtor)
    household_size = max(filers, 1) + living_with
    return HouseholdJurisdiction(
        state=address.state,
        county=address.county,
        household_size=household_size,
        source=(
            "Debtor 1's residence address, plus "
            f"{living_with} dependent(s) recorded as living with the debtor"
        ),
    )


@dataclass(frozen=True)
class StandardsResult:
    """What the workbench shows beside Schedule J's health-care and
    transportation lines. Every money figure is a string, like every other
    money figure this API serialises. A `None` figure means `problems`
    explains why — never a silent zero standing in for "unavailable"."""

    as_of: date
    state: str | None
    county: str | None
    household_size: int | None
    jurisdiction_source: str | None

    national_release_id: str | None
    national_allowance: str | None  # line 6
    oop_healthcare_under_65: str | None  # line 7a, per person
    oop_healthcare_65_and_older: str | None  # line 7d, per person

    local_release_id: str | None
    housing_non_mortgage: str | None  # line 8
    housing_mortgage_rent: str | None  # line 9a
    transportation_public_national: str | None  # line 14
    transportation_ownership_one_car: str | None  # line 13a
    transportation_ownership_two_cars: str | None  # line 13a + 13d
    transportation_operating_one_car: str | None  # line 12, one vehicle
    transportation_operating_two_cars: str | None  # line 12, two vehicles

    problems: tuple[str, ...]


def resolve_standards(
    *,
    as_of: date,
    debtors: Sequence[Debtor],
    dependents: Sequence[DependentBody],
) -> StandardsResult:
    """The whole read, pure over its inputs — no store, so it is testable with
    plain dataclasses like `cmi.py` and `means_test.py`. Never raises for a
    case that is simply too early to answer (no Debtor 1 yet, no county on
    file, a jurisdiction the Local Standards launch set does not cover); those
    become `problems` entries and every other figure that CAN resolve still
    does. Raises `LookupError` only when the registry itself has no release
    effective on `as_of` — the same "cannot compute on data that does not
    describe this date" refusal every other regulatory-dated module makes,
    which the route lets propagate like any other resolution failure."""
    problems: list[str] = []

    jurisdiction = household_jurisdiction(debtors=debtors, dependents=dependents)
    if jurisdiction is None:
        problems.append(
            "Debtor 1's residence address needs a state and county before "
            "the IRS standards can be looked up (B101 line 5)."
        )

    national_release, national = ust_data.national_standards(as_of)
    local_release, local = ust_data.local_standards(as_of)

    national_allowance: str | None = None
    if jurisdiction is not None:
        national_allowance = _money(national.allowance(jurisdiction.household_size))

    housing_non_mortgage: str | None = None
    housing_mortgage_rent: str | None = None
    operating_one: str | None = None
    operating_two: str | None = None

    if jurisdiction is not None:
        try:
            county_row = local.housing_for(jurisdiction.state, jurisdiction.county)
        except KeyError as error:
            problems.append(str(error))
        else:
            housing_non_mortgage = _money(
                county_row.non_mortgage_for(jurisdiction.household_size)
            )
            housing_mortgage_rent = _money(
                county_row.mortgage_rent_for(jurisdiction.household_size)
            )
        try:
            operating = local.transportation.operating_costs_for(
                jurisdiction.state, jurisdiction.county
            )
        except KeyError as error:
            problems.append(str(error))
        else:
            operating_one = operating.one_car
            operating_two = operating.two_cars

    return StandardsResult(
        as_of=as_of,
        state=jurisdiction.state if jurisdiction else None,
        county=jurisdiction.county if jurisdiction else None,
        household_size=jurisdiction.household_size if jurisdiction else None,
        jurisdiction_source=jurisdiction.source if jurisdiction else None,
        national_release_id=national_release.release_id,
        national_allowance=national_allowance,
        oop_healthcare_under_65=national.oop_healthcare_under_65,
        oop_healthcare_65_and_older=national.oop_healthcare_65_and_older,
        local_release_id=local_release.release_id,
        housing_non_mortgage=housing_non_mortgage,
        housing_mortgage_rent=housing_mortgage_rent,
        transportation_public_national=local.transportation.public_transportation_national,
        transportation_ownership_one_car=local.transportation.ownership_costs.one_car,
        transportation_ownership_two_cars=local.transportation.ownership_costs.two_cars,
        transportation_operating_one_car=operating_one,
        transportation_operating_two_cars=operating_two,
        problems=tuple(problems),
    )
