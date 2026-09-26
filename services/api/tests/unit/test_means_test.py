"""Known-answer checks for the means-test engine (issues #101, #365).

The register's bar for LOGIC rows: test cases with known answers, sanitized
(this repo is public — every name and figure below is invented, but the
ARITHMETIC is the form's own and can be redone on paper against the 04/25
B122A-2, the 10/19 B122C-1 and the 04/25 B122C-2). The full-trace scenario
walks a Gainesville (Alachua County, FL) household of two through every
line; the outcome scenarios pin all four ways the Chapter 7 determination
can settle — the median, the two § 707(b)(2)(A)(i) thresholds, and the
25%-of-unsecured ratio between them — and, for Chapter 13, both sides of
the § 1325(b)(3) comparison and both commitment periods, including the
case where the marital adjustment makes the two comparisons disagree.
"""

from __future__ import annotations

from dataclasses import replace
from datetime import date
from decimal import Decimal

import pytest
from insolvia_api.core.cmi import CmiResult, cmi_window, current_monthly_income
from insolvia_api.core.means_test import (
    FORM_122C1,
    FORM_122C2,
    MeansTestCase,
    MeansTestError,
    MeansTestResult,
    median_comparison,
    resolve_means_test_data,
    run_means_test,
)
from insolvia_core.debtors import Debtor
from insolvia_core.income import EmploymentBody, PayPeriodRecordBody
from insolvia_core.means_test_inputs import (
    MeansTestInputBody,
    OtherSecuredPayment,
    parse_means_test_input,
)

AS_OF = date(2026, 9, 4)
DATA = resolve_means_test_data(AS_OF)


def flat_cmi(monthly: str) -> CmiResult:
    """A CmiResult carrying just the totals — the engine reads the combined
    figure and the column list; the derivation itself is test_cmi.py's."""
    value = Decimal(monthly)
    return CmiResult(
        window=cmi_window(AS_OF),
        columns=(),
        combined_monthly_total=monthly,
        annualized=f"{(value * 12):f}",
        gaps=(),
        problems=(),
    )


def case_for(
    *,
    monthly_cmi: str,
    inputs: MeansTestInputBody,
    state: str = "FL",
    county: str = "Alachua County",
    district: str = "Middle District of Florida",
    priority_debt: str = "0.00",
    nonpriority_unsecured: str = "0.00",
    children_under_18: int = 0,
    cmi: CmiResult | None = None,
) -> MeansTestCase:
    return MeansTestCase(
        state=state,
        county=county,
        district=district,
        cmi=cmi if cmi is not None else flat_cmi(monthly_cmi),
        inputs=inputs,
        priority_debt_total=priority_debt,
        nonpriority_unsecured_total=nonpriority_unsecured,
        children_under_18=children_under_18,
    )


def household(
    under_65: int = 2, over_65: int = 0, **extra: object
) -> MeansTestInputBody:
    return parse_means_test_input(
        {"people_under_65": under_65, "people_65_or_older": over_65, **extra}
    )


def line(result: MeansTestResult, number: str) -> tuple[str, str]:
    found = next(entry for entry in result.lines if entry.line == number)
    return found.amount, found.source


# ── the median comparison ──────────────────────────────────────


def test_the_comparison_annualizes_and_reads_the_dated_table() -> None:
    comparison = median_comparison(
        monthly_cmi="4000.00", state="fl", household_size=1, data=DATA
    )
    assert comparison.annualized_cmi == "48000.00"
    assert comparison.annual_median == "69876.00"  # the UST table, verbatim
    assert not comparison.above_median
    assert "ust/census-median-family-income@2026-04-01" in comparison.source


def test_income_equal_to_the_median_is_not_above_it() -> None:
    # 86523 / 12 = 7210.25 exactly; the form's box 1 covers "less than or
    # equal to", so equality stays below.
    comparison = median_comparison(
        monthly_cmi="7210.25", state="FL", household_size=2, data=DATA
    )
    assert comparison.annualized_cmi == "86523.00"
    assert not comparison.above_median


def test_a_household_above_four_adds_the_statutory_amount() -> None:
    comparison = median_comparison(
        monthly_cmi="12000.00", state="TX", household_size=6, data=DATA
    )
    # 117962 + 2 x 11100 (12 x the § 707(b)(7)(A)(iii) $925).
    assert comparison.annual_median == "140162.00"


def test_an_unknown_jurisdiction_refuses() -> None:
    with pytest.raises(MeansTestError, match="no row"):
        median_comparison(
            monthly_cmi="4000.00", state="ZZ", household_size=2, data=DATA
        )


# ── outcomes ───────────────────────────────────────────────────


def test_a_below_median_debtor_stops_at_the_comparison() -> None:
    result = run_means_test(case_for(monthly_cmi="4000.00", inputs=household()), DATA)
    assert result.outcome == "below_median"
    assert result.determined_by == "median"
    assert result.lines == ()
    assert result.release_ids["ust/census-median-family-income"] == (
        "ust/census-median-family-income@2026-04-01"
    )


def test_high_disposable_income_presumes_abuse_at_the_ceiling() -> None:
    # Household of 1, renting with no home debt, no vehicle: the IRS
    # allowances leave far more than $17,150 over 60 months.
    result = run_means_test(
        case_for(monthly_cmi="9000.00", inputs=household(under_65=1)), DATA
    )
    assert result.outcome == "presumption_of_abuse"
    assert result.determined_by == "threshold_ceiling"
    _, source = line(result, "40")
    assert "17150.00" in source
    assert "§ 707(b)(2)(A)(i)(II)" in source


def test_the_middle_band_compares_against_a_quarter_of_unsecured_debt() -> None:
    # Household of 1 in Alachua: allowances 867 + 90 + 588 + 1225 + 220
    # = 2990; entered taxes 2000 + health insurance 760 make line 38 5750,
    # so 39c = 6000 - 5750 = 250 and 39d = 15000 — between the thresholds.
    inputs = household(under_65=1, taxes="2000.00", health_insurance="760.00")
    presumed = run_means_test(
        case_for(
            monthly_cmi="6000.00", inputs=inputs, nonpriority_unsecured="40000.00"
        ),
        DATA,
    )
    assert line(presumed, "39d")[0] == "15000.00"
    assert line(presumed, "41b")[0] == "10000.00"
    assert presumed.outcome == "presumption_of_abuse"
    assert presumed.determined_by == "unsecured_ratio"

    cleared = run_means_test(
        case_for(
            monthly_cmi="6000.00", inputs=inputs, nonpriority_unsecured="80000.00"
        ),
        DATA,
    )
    assert line(cleared, "41b")[0] == "20000.00"
    assert cleared.outcome == "no_presumption"
    assert cleared.determined_by == "unsecured_ratio"


# ── the full trace, hand-computed ──────────────────────────────


def full_inputs() -> MeansTestInputBody:
    return household(
        under_65=2,
        over_65=0,
        home_secured_monthly_total="1750.00",
        vehicle_count=1,
        vehicle_1_loan_monthly="450.00",
        taxes="1500.00",
        involuntary_deductions="120.00",
        term_life_insurance="40.00",
        healthcare_above_allowance="100.00",
        optional_telecom="60.00",
        health_insurance="400.00",
        health_savings_account="50.00",
        education_under_18="200.00",
        additional_food_clothing="50.00",
        charitable_contributions="25.00",
        priority_cure_total="3000.00",
        ch13_eligible=True,
        ch13_projected_plan_payment="500.00",
    )


def full_case() -> MeansTestCase:
    return case_for(
        monthly_cmi="7500.00",
        inputs=full_inputs(),
        priority_debt="6000.00",
        nonpriority_unsecured="30000.00",
        children_under_18=1,
    )


def test_the_full_calculation_line_by_line() -> None:
    result = run_means_test(full_case(), DATA)
    assert result.comparison.above_median  # 90000 > 86523
    expected = {
        "1": "7500.00",
        "3": "0.00",
        "4": "7500.00",
        "5": "2.00",
        "6": "1558.00",  # National Standards, household of 2
        "7": "180.00",  # 2 x 90 out-of-pocket health care
        "8": "690.00",  # Alachua County non-mortgage, household of 2
        "9a": "1440.00",  # Alachua County mortgage/rent standard
        "9b": "1750.00",
        "9c": "0.00",  # the standard is exhausted by the home debt
        "12": "291.00",  # South region operating costs, one car
        "13a": "703.00",  # national ownership costs, one car
        "13b": "450.00",
        "13c": "253.00",
        "13": "253.00",
        "14": "0.00",  # a vehicle was claimed
        "16": "1500.00",
        "24": "4792.00",  # 1558+180+690+0+0+291+253+0+0+1820
        "25": "450.00",
        "29": "200.00",  # under the 214.58 per-child cap
        "30": "50.00",  # under the 53.00 household-of-2 cap
        "32": "725.00",  # 450+200+50+25
        "33e": "2200.00",  # 1750 + 450
        "34": "50.00",  # 3000 / 60
        "35": "100.00",  # 6000 / 60
        "36": "50.00",  # 500 x 0.1 (Middle Florida)
        "37": "2400.00",
        "38": "7917.00",  # 4792 + 725 + 2400
        "39c": "-417.00",
        "39d": "-25020.00",
    }
    for number, amount in expected.items():
        assert line(result, number)[0] == amount, f"line {number}"
    assert result.outcome == "no_presumption"
    assert result.determined_by == "threshold_floor"


def test_every_figure_names_its_rule_input_or_dataset() -> None:
    # The issue's done-when, in assertable form.
    result = run_means_test(full_case(), DATA)
    assert all(entry.source for entry in result.lines)
    assert "ust/irs-national-standards@2026-07-15" in line(result, "6")[1]
    assert "ust/irs-local-standards@2026-07-15" in line(result, "8")[1]
    assert "ust/ch13-admin-multipliers@2026-07-15" in line(result, "36")[1]
    assert "code/dollar-amounts@2025-04-01" in line(result, "29")[1]
    assert "means_test_input.taxes" in line(result, "16")[1]
    assert line(result, "24")[1].startswith("lines 6 + 7 + 8")
    assert set(result.release_ids) == {
        "ust/census-median-family-income",
        "ust/irs-national-standards",
        "ust/irs-local-standards",
        "ust/ch13-admin-multipliers",
        "code/dollar-amounts",
    }


def test_the_cmi_engine_composes_into_the_means_test() -> None:
    # End to end across the two engines: six real monthly paychecks in the
    # window produce the same determination as the flat figure above.
    debtor = Debtor(
        id="d-1",
        case_id="case-0001",
        filing_role="debtor_1",
        created_at="2026-08-01T12:00:00Z",
        updated_at="2026-08-01T12:00:00Z",
    )
    employment = EmploymentBody(
        debtor_id="d-1", status="employed", employer_name="Hillside Clinic"
    )
    cmi = current_monthly_income(
        filing_date=AS_OF,
        debtors=[debtor],
        employments=[("em-1", employment)],
        pay_periods=[
            PayPeriodRecordBody(
                employment_id="em-1",
                pay_date=f"2026-0{month}-25",
                gross="7500.00",
                frequency="monthly",
            )
            for month in range(3, 9)
        ],
        other_income=[],
    )
    assert cmi.combined_monthly_total == "7500.00"
    result = run_means_test(
        case_for(
            monthly_cmi="unused", cmi=cmi, inputs=full_inputs(), children_under_18=1
        ),
        DATA,
    )
    assert result.outcome == "no_presumption"


# ── refusals ───────────────────────────────────────────────────


def test_missing_household_composition_refuses() -> None:
    with pytest.raises(MeansTestError, match="household composition"):
        run_means_test(
            case_for(monthly_cmi="9000.00", inputs=MeansTestInputBody()), DATA
        )


def test_an_unknown_county_refuses_rather_than_guessing() -> None:
    with pytest.raises(MeansTestError, match="no housing standard"):
        run_means_test(
            case_for(
                monthly_cmi="9000.00", inputs=household(), county="Nowhere County"
            ),
            DATA,
        )


def test_an_unknown_district_refuses_when_ch13_applies() -> None:
    inputs = household(
        under_65=1, ch13_eligible=True, ch13_projected_plan_payment="500.00"
    )
    with pytest.raises(MeansTestError, match="no district"):
        run_means_test(
            case_for(monthly_cmi="9000.00", inputs=inputs, district="Outer Nowhere"),
            DATA,
        )


def test_statutory_caps_refuse_rather_than_clamp() -> None:
    over_education = case_for(
        monthly_cmi="9000.00",
        inputs=household(under_65=1, education_under_18="500.00"),
        children_under_18=1,
    )
    with pytest.raises(MeansTestError, match=r"707\(b\)\(2\)\(A\)\(ii\)\(IV\)"):
        run_means_test(over_education, DATA)

    over_food = case_for(
        monthly_cmi="9000.00",
        inputs=household(under_65=1, additional_food_clothing="100.00"),
    )
    with pytest.raises(MeansTestError, match="5% food-and-clothing cap"):
        run_means_test(over_food, DATA)


def test_a_marital_adjustment_without_column_b_is_an_error() -> None:
    inputs = household(
        under_65=1,
        marital_adjustments=[
            {"id": "ma1", "description": "Spouse 401(k)", "amount": "300.00"}
        ],
    )
    with pytest.raises(MeansTestError, match="no Column B"):
        run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)


def test_resolution_before_the_series_begin_refuses() -> None:
    with pytest.raises(LookupError, match="refusing"):
        resolve_means_test_data(date(2026, 3, 31))


def test_marital_adjustment_subtracts_when_column_b_exists() -> None:
    # A column-B CMI with an entered adjustment: line 4 = line 1 - line 3.
    cmi = current_monthly_income(
        filing_date=AS_OF,
        debtors=[
            Debtor(
                id="d-2",
                case_id="case-0001",
                filing_role="debtor_2",
                created_at="2026-08-01T12:00:00Z",
                updated_at="2026-08-01T12:00:00Z",
            )
        ],
        employments=[
            (
                "em-2",
                EmploymentBody(
                    debtor_id="d-2", status="employed", employer_name="Spouse Co"
                ),
            )
        ],
        pay_periods=[
            PayPeriodRecordBody(
                employment_id="em-2",
                pay_date=f"2026-0{month}-25",
                gross="8000.00",
                frequency="monthly",
            )
            for month in range(3, 9)
        ],
        other_income=[],
    )
    inputs = household(
        under_65=2,
        marital_adjustments=[
            {
                "id": "ma1",
                "description": "Spouse's own student loan",
                "amount": "300.00",
            }
        ],
    )
    result = run_means_test(
        case_for(monthly_cmi="unused", cmi=cmi, inputs=inputs), DATA
    )
    assert line(result, "1")[0] == "8000.00"
    assert line(result, "3")[0] == "300.00"
    assert line(result, "4")[0] == "7700.00"
    assert "Spouse's own student loan" in line(result, "3")[1]


def test_two_vehicles_take_the_per_vehicle_ownership_standard() -> None:
    inputs = household(
        under_65=1,
        vehicle_count=2,
        vehicle_1_loan_monthly="800.00",
        vehicle_2_loan_monthly="100.00",
    )
    result = run_means_test(
        case_for(monthly_cmi="9000.00", inputs=inputs, county="Miami-Dade"), DATA
    )
    # Operating: Miami MSA two-car rate; ownership: 703 per vehicle, netted
    # against each vehicle's own loan, floored at zero per vehicle.
    assert line(result, "12")[0] == "846.00"
    assert line(result, "13c")[0] == "0.00"  # 703 - 800, floored
    assert line(result, "13f")[0] == "603.00"  # 703 - 100
    assert line(result, "13")[0] == "603.00"


def test_unused_secured_rows_and_sixty_month_math() -> None:
    inputs = household(
        under_65=1,
        other_secured_payments=[
            {
                "id": "os1",
                "creditor_name": "Marina Finance",
                "property_description": "Boat",
                "monthly_payment": "120.00",
            }
        ],
    )
    assert isinstance(inputs.other_secured_payments[0], OtherSecuredPayment)
    result = run_means_test(
        case_for(monthly_cmi="9000.00", inputs=inputs, priority_debt="9001.00"), DATA
    )
    assert line(result, "33d")[0] == "120.00"
    assert "Marina Finance" in line(result, "33d")[1]
    assert line(result, "35")[0] == "150.02"  # 9001 / 60, half-up


# ── issue #349: the screen's inputs ─────────────────────────────


@pytest.mark.parametrize(
    "flag", ["non_consumer_debts", "disabled_veteran", "reservist_national_guard"]
)
def test_an_exemption_ends_the_test_before_any_line(flag: str) -> None:
    inputs = household(**{flag: True})
    result = run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)
    assert result.outcome == "exempt"
    assert result.determined_by == flag
    assert result.lines == ()
    # The comparison is still reported for the attorney's benefit.
    assert result.comparison is not None
    assert result.comparison.above_median


def test_an_exempt_debtor_needs_no_household_at_all() -> None:
    inputs = parse_means_test_input({"non_consumer_debts": True})
    result = run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)
    assert result.outcome == "exempt"
    assert result.comparison is None


def test_a_declined_exemption_is_not_an_exemption() -> None:
    result = run_means_test(
        case_for(monthly_cmi="4000.00", inputs=household(disabled_veteran=False)),
        DATA,
    )
    assert result.outcome == "below_median"


def test_the_median_household_size_override_wins_the_comparison() -> None:
    # Two by age band, but the median table is read at four: FL's
    # household-of-4 median is what an 8,000 monthly CMI is now under.
    inputs = household(under_65=2, median_household_size=4)
    result = run_means_test(case_for(monthly_cmi="8000.00", inputs=inputs), DATA)
    assert result.comparison is not None
    assert result.comparison.household_size == 4
    assert result.outcome == "below_median"


def test_the_median_size_alone_decides_a_below_median_case() -> None:
    inputs = parse_means_test_input({"median_household_size": 4})
    result = run_means_test(case_for(monthly_cmi="4000.00", inputs=inputs), DATA)
    assert result.outcome == "below_median"


def test_above_the_median_the_age_bands_are_still_required() -> None:
    inputs = parse_means_test_input({"median_household_size": 1})
    with pytest.raises(MeansTestError, match="age bands"):
        run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)


def test_the_irs_family_size_drives_lines_5_and_6_only() -> None:
    base = run_means_test(
        case_for(monthly_cmi="9000.00", inputs=household(under_65=2)), DATA
    )
    sized = run_means_test(
        case_for(
            monthly_cmi="9000.00", inputs=household(under_65=2, irs_family_size=3)
        ),
        DATA,
    )
    assert line(sized, "5")[0] == "3.00"
    assert "irs_family_size" in line(sized, "5")[1]
    assert line(sized, "6")[0] != line(base, "6")[0]
    assert "household of 3" in line(sized, "6")[1]
    # The median comparison and the housing standard keep the two-person
    # figures.
    assert sized.comparison is not None
    assert sized.comparison.household_size == 2
    assert line(sized, "8")[0] == line(base, "8")[0]


def test_the_housing_family_size_drives_lines_8_and_9a_only() -> None:
    base = run_means_test(
        case_for(monthly_cmi="9000.00", inputs=household(under_65=2)), DATA
    )
    sized = run_means_test(
        case_for(
            monthly_cmi="9000.00",
            inputs=household(under_65=2, irs_housing_family_size=4),
        ),
        DATA,
    )
    assert line(sized, "8")[0] != line(base, "8")[0]
    assert "household of 4" in line(sized, "8")[1]
    assert "irs_housing_family_size" in line(sized, "9a")[1]
    assert line(sized, "6")[0] == line(base, "6")[0]
    assert line(sized, "5")[0] == "2.00"


def secured_row(
    row_id: str, bucket: str | None, monthly: str, cure: str | None = None
) -> dict[str, object]:
    row: dict[str, object] = {
        "id": row_id,
        "creditor_name": f"Lender {row_id}",
        "property_description": "Collateral",
        "monthly_payment": monthly,
    }
    if bucket is not None:
        row["bucket"] = bucket
    if cure is not None:
        row["cure_total"] = cure
    return row


def test_bucketed_claim_rows_feed_the_home_and_vehicle_lines() -> None:
    inputs = household(
        under_65=1,
        vehicle_count=2,
        other_secured_payments=[
            secured_row("h1", "home", "1200.00", "600.00"),
            secured_row("h2", "home", "300.00"),
            secured_row("v1", "vehicle_1", "400.00", "60.00"),
            secured_row("v2", "vehicle_2", "250.00"),
            secured_row("o1", "other", "95.00"),
            secured_row("legacy", None, "50.00"),
        ],
    )
    result = run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)
    assert line(result, "9b")[0] == "1500.00"
    assert "bucket home" in line(result, "9b")[1]
    assert line(result, "13b")[0] == "400.00"
    assert line(result, "13e")[0] == "250.00"
    # Only the rows in no bucket or `other` reach 33d.
    assert line(result, "33d")[0] == "145.00"
    assert line(result, "33e")[0] == "2295.00"
    # Line 34: the per-claim cure amounts summed, then ÷ 60.
    assert line(result, "34")[0] == "11.00"
    assert "cure_total" in line(result, "34")[1]


def test_a_typed_total_wins_over_the_bucketed_rows_rather_than_adding() -> None:
    inputs = household(
        under_65=1,
        home_secured_monthly_total="1000.00",
        priority_cure_total="600.00",
        other_secured_payments=[secured_row("h1", "home", "1200.00", "6000.00")],
    )
    result = run_means_test(case_for(monthly_cmi="9000.00", inputs=inputs), DATA)
    assert line(result, "9b")[0] == "1000.00"
    assert "home_secured_monthly_total" in line(result, "9b")[1]
    assert line(result, "34")[0] == "10.00"
    assert line(result, "33d")[0] == "0.00"


# ── issue #365: Chapter 13 — B122C-1 and B122C-2 ────────────────


def ch13_case(**overrides: object) -> MeansTestCase:
    """`case_for` on a Chapter 13 case — same Gainesville household."""
    return replace(case_for(**overrides), chapter=13)  # type: ignore[arg-type]


def c1_line(result: MeansTestResult, number: str) -> tuple[str, str]:
    found = next(
        entry
        for entry in result.lines
        if entry.line == number and entry.form == FORM_122C1
    )
    return found.amount, found.source


def c2_line(result: MeansTestResult, number: str) -> tuple[str, str]:
    found = next(
        entry
        for entry in result.lines
        if entry.line == number and entry.form == FORM_122C2
    )
    return found.amount, found.source


def test_chapter_7_lines_name_their_form() -> None:
    result = run_means_test(full_case(), DATA)
    assert {entry.form for entry in result.lines} == {"122A-2"}
    assert result.chapter == 7
    assert result.commitment is None
    assert result.disposable_income is None


def test_a_below_median_chapter_13_debtor_gets_a_three_year_period() -> None:
    # 4,000 x 12 = 48,000 against FL's household-of-2 median (86,523): line
    # 17a (no Form 122C-2) and, on the same figure, line 21's 3 years.
    result = run_means_test(ch13_case(monthly_cmi="4000.00", inputs=household()), DATA)
    assert result.chapter == 13
    assert result.outcome == "below_median"
    assert result.determined_by == "median"
    assert result.comparison is not None
    assert result.comparison.monthly_cmi == "4000.00"
    assert not result.comparison.above_median
    assert result.commitment is not None
    assert result.commitment.months == 36
    assert result.commitment.annualized_income == "48000.00"
    assert result.commitment.annual_median == "86523.00"
    assert "§ 1325(b)(4)(A)(i)" in result.commitment.source
    assert result.disposable_income is None
    # Form 122C-1's Part 2-3 lines are the whole trace; no 122C-2 line.
    assert {entry.form for entry in result.lines} == {"122C-1"}
    assert c1_line(result, "17")[0] == "0.00"
    assert "not determined under § 1325(b)(3)" in c1_line(result, "17")[1]
    assert c1_line(result, "21")[0] == "36.00"


def ch13_inputs() -> MeansTestInputBody:
    """The Chapter 7 full-trace inputs plus the four Chapter 13 answers."""
    return parse_means_test_input(
        {
            "people_under_65": 2,
            "people_65_or_older": 0,
            "home_secured_monthly_total": "1750.00",
            "vehicle_count": 1,
            "vehicle_1_loan_monthly": "450.00",
            "taxes": "1500.00",
            "involuntary_deductions": "120.00",
            "term_life_insurance": "40.00",
            "healthcare_above_allowance": "100.00",
            "optional_telecom": "60.00",
            "health_insurance": "400.00",
            "health_savings_account": "50.00",
            "education_under_18": "200.00",
            "additional_food_clothing": "50.00",
            "charitable_contributions": "25.00",
            "priority_cure_total": "3000.00",
            # B122A-2's eligibility question, deliberately unanswered: the
            # Chapter 13 form multiplies regardless.
            "ch13_projected_plan_payment": "500.00",
            "child_support_for_dependents": "250.00",
            "qualified_retirement_deductions": "150.00",
            "special_circumstances": [
                {"id": "sc-1", "description": "Dialysis travel", "amount": "100.00"}
            ],
        }
    )


def test_the_chapter_13_calculation_line_by_line() -> None:
    # 9,500 x 12 = 114,000 over the 86,523 median: Form 122C-2 is required
    # (line 17b) and the commitment period is 5 years (line 21). Lines
    # 5-38 are the Chapter 7 full trace's deductions with line 36 always
    # multiplied; lines 39-45 are the § 1325(b)(2) subtractions.
    result = run_means_test(
        ch13_case(
            monthly_cmi="9500.00",
            inputs=ch13_inputs(),
            priority_debt="6000.00",
            children_under_18=1,
        ),
        DATA,
    )
    assert result.outcome == "above_median"
    assert result.determined_by == "median"
    assert result.commitment is not None
    assert result.commitment.months == 60
    assert result.commitment.monthly_income == "9500.00"
    assert result.commitment.annualized_income == "114000.00"
    assert "§ 1325(b)(4)(A)(ii)" in result.commitment.source
    for number, amount in {
        "11": "9500.00",
        "12": "9500.00",
        "13": "0.00",
        "14": "9500.00",
        "15a": "9500.00",
        "15b": "114000.00",
        "16c": "86523.00",
        "17": "1.00",
        "18": "9500.00",
        "19a": "0.00",
        "19b": "9500.00",
        "20a": "9500.00",
        "20b": "114000.00",
        "20c": "86523.00",
        "21": "60.00",
    }.items():
        assert c1_line(result, number)[0] == amount, f"122C-1 line {number}"
    for number, amount in {
        "5": "2.00",
        "6": "1558.00",
        "24": "4792.00",
        "32": "725.00",
        "33e": "2200.00",
        "34": "50.00",
        "35": "100.00",
        "36": "50.00",  # 500 x 0.1 (Middle Florida), no eligibility question
        "37": "2400.00",
        "38": "7917.00",
        "39": "9500.00",
        "40": "250.00",
        "41": "150.00",
        "42": "7917.00",
        "43": "100.00",
        "44": "8417.00",  # 250 + 150 + 7917 + 100
        "45": "1083.00",  # 9500 - 8417
    }.items():
        assert c2_line(result, number)[0] == amount, f"122C-2 line {number}"
    assert result.disposable_income == "1083.00"
    # No Chapter 7 line leaks into a Chapter 13 trace, and every line
    # names its form.
    assert {entry.form for entry in result.lines} == {"122C-1", "122C-2"}
    assert not any(
        entry.line in ("1", "3", "4", "39a", "40") and entry.form == "122A-2"
        for entry in result.lines
    )


def test_every_chapter_13_figure_names_its_rule_input_or_dataset() -> None:
    result = run_means_test(
        ch13_case(monthly_cmi="9500.00", inputs=ch13_inputs(), children_under_18=1),
        DATA,
    )
    assert all(entry.source for entry in result.lines)
    assert "ust/census-median-family-income@2026-04-01" in c1_line(result, "16c")[1]
    assert "§ 1325(b)(3)" in c1_line(result, "15b")[1]
    assert "§ 1325(b)(4)" in c1_line(result, "20b")[1]
    assert "ust/ch13-admin-multipliers@2026-07-15" in c2_line(result, "36")[1]
    assert "§ 1325(b)(3)" in c2_line(result, "36")[1]
    assert "§ 1325(b)(2)" in c2_line(result, "40")[1]
    assert "541(b)(7)" in c2_line(result, "41")[1]
    assert "Dialysis travel" in c2_line(result, "43")[1]
    assert "§ 707(b)(2)(B)" in c2_line(result, "43")[1]
    assert c2_line(result, "44")[1] == "lines 40 + 41 + 42 + 43"
    assert c2_line(result, "45")[1] == "line 39 minus line 44"


def spouse_only_cmi(monthly: str) -> CmiResult:
    """A Column B-only derivation: the non-filing spouse's income."""
    return current_monthly_income(
        filing_date=AS_OF,
        debtors=[
            Debtor(
                id="d-s",
                case_id="case-0001",
                filing_role="non_filing_spouse",
                created_at="2026-08-01T12:00:00Z",
                updated_at="2026-08-01T12:00:00Z",
            )
        ],
        employments=[
            (
                "em-s",
                EmploymentBody(
                    debtor_id="d-s", status="employed", employer_name="Spouse Co"
                ),
            )
        ],
        pay_periods=[
            PayPeriodRecordBody(
                employment_id="em-s",
                pay_date=f"2026-0{month}-25",
                gross=monthly,
                frequency="monthly",
            )
            for month in range(3, 9)
        ],
        other_income=[],
    )


def test_the_marital_adjustment_comes_off_before_the_median_on_122c1() -> None:
    # 7,400 x 12 = 88,800 is over the household-of-2 median (86,523), but
    # line 13's 300 brings line 14 to 7,100 and line 15b to 85,200: line
    # 17a, no Form 122C-2. The commitment period reads the RAW line 11
    # unless the adjustment is contended for it too — so 60 months here.
    adjustments = [
        {"id": "ma1", "description": "Spouse's own student loan", "amount": "300.00"}
    ]
    result = run_means_test(
        ch13_case(
            monthly_cmi="unused",
            cmi=spouse_only_cmi("7400.00"),
            inputs=household(under_65=2, marital_adjustments=adjustments),
        ),
        DATA,
    )
    assert result.outcome == "below_median"
    assert result.comparison is not None
    assert result.comparison.monthly_cmi == "7100.00"
    assert result.comparison.annualized_cmi == "85200.00"
    assert c1_line(result, "13")[0] == "300.00"
    assert "Spouse's own student loan" in c1_line(result, "13")[1]
    assert c1_line(result, "19a")[0] == "0.00"
    assert "not contended" in c1_line(result, "19a")[1]
    assert c1_line(result, "20b")[0] == "88800.00"
    assert result.commitment is not None
    assert result.commitment.months == 60
    assert result.commitment.marital_adjustment == "0.00"


def test_contending_the_adjustment_for_the_commitment_period_shortens_it() -> None:
    adjustments = [
        {"id": "ma1", "description": "Spouse's own student loan", "amount": "300.00"}
    ]
    result = run_means_test(
        ch13_case(
            monthly_cmi="unused",
            cmi=spouse_only_cmi("7400.00"),
            inputs=household(
                under_65=2,
                marital_adjustments=adjustments,
                commitment_period_marital_adjustment=True,
            ),
        ),
        DATA,
    )
    assert c1_line(result, "19a")[0] == "300.00"
    assert "§ 1325(b)(4)" in c1_line(result, "19a")[1]
    assert c1_line(result, "19b")[0] == "7100.00"
    assert c1_line(result, "20b")[0] == "85200.00"
    assert result.commitment is not None
    assert result.commitment.months == 36
    assert result.commitment.marital_adjustment == "300.00"


def test_a_chapter_13_marital_adjustment_without_column_b_is_an_error() -> None:
    inputs = household(
        under_65=1,
        marital_adjustments=[{"id": "ma1", "description": "x", "amount": "300.00"}],
    )
    with pytest.raises(MeansTestError, match=r"Form 122C-1 has no Column B"):
        run_means_test(ch13_case(monthly_cmi="4000.00", inputs=inputs), DATA)


def test_the_chapter_13_form_multiplies_line_36_without_the_eligibility_question() -> (
    None
):
    # An absent plan payment is a zero, as a blank box; the district is
    # still looked up, so an unknown one refuses.
    inputs = household(under_65=1)
    result = run_means_test(ch13_case(monthly_cmi="9000.00", inputs=inputs), DATA)
    assert c2_line(result, "36")[0] == "0.00"
    assert "multiplier 0.1" in c2_line(result, "36")[1]
    with pytest.raises(MeansTestError, match="no district"):
        run_means_test(
            ch13_case(monthly_cmi="9000.00", inputs=inputs, district="Outer Nowhere"),
            DATA,
        )


def test_the_presumption_exemptions_never_apply_to_chapter_13() -> None:
    inputs = household(under_65=2, disabled_veteran=True)
    result = run_means_test(ch13_case(monthly_cmi="4000.00", inputs=inputs), DATA)
    assert result.outcome == "below_median"
    assert result.commitment is not None


def test_a_chapter_13_case_needs_the_household_before_either_comparison() -> None:
    with pytest.raises(MeansTestError, match="line 16b"):
        run_means_test(
            ch13_case(monthly_cmi="4000.00", inputs=MeansTestInputBody()), DATA
        )


def test_a_chapter_with_no_means_test_form_refuses() -> None:
    eleven = replace(case_for(monthly_cmi="4000.00", inputs=household()), chapter=11)
    with pytest.raises(MeansTestError, match="Chapter 11 has no means-test form"):
        run_means_test(eleven, DATA)
