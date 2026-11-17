"""Known-answer checks for `core/standards.py` (issue #348) — the household
jurisdiction derivation and the standards lookup, pure, no store.

Alachua County, FL mirrors test_means_test.py's fixture — a real row in the
committed registry, so the resolved figures are the UST's own published
numbers and can be checked against the source (test_ust_data.py owns that
verification; this file pins the wiring and the degrade paths instead).
"""

from __future__ import annotations

from datetime import date

from insolvia_api.core.standards import household_jurisdiction, resolve_standards
from insolvia_core.debtors import Address, Debtor
from insolvia_core.expenses import DependentBody

AS_OF = date(2026, 9, 4)


def debtor(role: str, debtor_id: str, **address: str) -> Debtor:
    return Debtor(
        id=debtor_id,
        case_id="case-0001",
        filing_role=role,
        created_at="2026-08-01T12:00:00Z",
        updated_at="2026-08-01T12:00:00Z",
        residence_address=Address(**address) if address else Address(),
    )


DEBTOR_1 = debtor("debtor_1", "d-1", state="FL", county="Alachua County")


def dependent(*, lives_with_debtor: bool) -> DependentBody:
    return DependentBody(
        household_id="h-1",
        relationship="child",
        age=9,
        lives_with_debtor=lives_with_debtor,
    )


# ── household_jurisdiction ───────────────────────────────────────


def test_no_debtors_resolves_to_no_jurisdiction():
    assert household_jurisdiction(debtors=[], dependents=[]) is None


def test_a_debtor_with_no_address_resolves_to_no_jurisdiction():
    bare = debtor("debtor_1", "d-1")
    assert household_jurisdiction(debtors=[bare], dependents=[]) is None


def test_a_state_with_no_county_resolves_to_no_jurisdiction():
    partial = debtor("debtor_1", "d-1", state="FL")
    assert household_jurisdiction(debtors=[partial], dependents=[]) is None


def test_debtor_1_alone_is_a_household_of_one():
    result = household_jurisdiction(debtors=[DEBTOR_1], dependents=[])
    assert result is not None
    assert result.state == "FL"
    assert result.county == "Alachua County"
    assert result.household_size == 1


def test_a_filing_debtor_2_grows_the_household():
    debtor_2 = debtor("debtor_2", "d-2")
    result = household_jurisdiction(debtors=[DEBTOR_1, debtor_2], dependents=[])
    assert result is not None
    assert result.household_size == 2


def test_a_non_filing_spouse_counts_toward_the_household():
    spouse = debtor("non_filing_spouse", "d-2")
    result = household_jurisdiction(debtors=[DEBTOR_1, spouse], dependents=[])
    assert result is not None
    assert result.household_size == 2


def test_only_dependents_living_with_the_debtor_count():
    result = household_jurisdiction(
        debtors=[DEBTOR_1],
        dependents=[
            dependent(lives_with_debtor=True),
            dependent(lives_with_debtor=False),
        ],
    )
    assert result is not None
    assert result.household_size == 2


# ── resolve_standards ─────────────────────────────────────────────


def test_no_jurisdiction_reports_the_problem_and_nothing_resolves():
    result = resolve_standards(as_of=AS_OF, debtors=[], dependents=[])
    assert result.state is None
    assert result.national_allowance is None
    assert result.housing_non_mortgage is None
    assert result.problems


def test_a_supported_county_resolves_every_figure_with_no_problems():
    result = resolve_standards(as_of=AS_OF, debtors=[DEBTOR_1], dependents=[])
    assert result.problems == ()
    assert result.state == "FL"
    assert result.household_size == 1
    assert result.national_release_id
    assert result.national_allowance is not None
    assert result.oop_healthcare_under_65 is not None
    assert result.local_release_id
    assert result.housing_non_mortgage is not None
    assert result.housing_mortgage_rent is not None
    assert result.transportation_public_national is not None
    assert result.transportation_ownership_one_car is not None
    assert result.transportation_ownership_two_cars is not None
    assert result.transportation_operating_one_car is not None
    assert result.transportation_operating_two_cars is not None


def test_an_unsupported_state_still_resolves_the_national_figures():
    # The National Standards apply everywhere; only the Local Standards are
    # scoped to the launch states (ADR 0017).
    unsupported = debtor("debtor_1", "d-1", state="ZZ", county="Nowhere County")
    result = resolve_standards(as_of=AS_OF, debtors=[unsupported], dependents=[])
    assert result.national_allowance is not None
    assert result.housing_non_mortgage is None
    assert result.housing_mortgage_rent is None
    assert result.transportation_operating_one_car is None
    assert result.problems


def test_a_supported_state_with_an_unknown_county_reports_that_alone():
    unknown_county = debtor("debtor_1", "d-1", state="FL", county="Nowhere County")
    result = resolve_standards(as_of=AS_OF, debtors=[unknown_county], dependents=[])
    assert result.national_allowance is not None
    assert result.housing_non_mortgage is None
    assert any("Nowhere County" in problem for problem in result.problems)


def test_national_allowance_grows_with_household_size():
    debtor_2 = debtor("debtor_2", "d-2")
    solo = resolve_standards(as_of=AS_OF, debtors=[DEBTOR_1], dependents=[])
    pair = resolve_standards(as_of=AS_OF, debtors=[DEBTOR_1, debtor_2], dependents=[])
    assert solo.national_allowance is not None
    assert pair.national_allowance is not None
    assert float(pair.national_allowance) > float(solo.national_allowance)


def test_every_resolved_figure_is_a_two_decimal_money_string():
    result = resolve_standards(as_of=AS_OF, debtors=[DEBTOR_1], dependents=[])
    for value in (
        result.national_allowance,
        result.housing_non_mortgage,
        result.housing_mortgage_rent,
    ):
        assert value is not None
        assert "." in value
        assert len(value.split(".")[1]) == 2
