"""The § 707(b) means-test engine — rule-based, effective-dated, pure
(issue #101).

The legally required Chapter 7 eligibility calculation: the median-income
comparison (§ 707(b)(7); B122A-1 lines 12-14), and for above-median debtors
the full B122A-2 calculation (revision 04/25) ending in the presumption-of-
abuse determination (§ 707(b)(2)). Wrong means dismissal or a trustee
challenge, so the numbers stay deterministic — Claude never touches them
(the register's LOGIC rule) — and every figure in the output traces to a
rule, an input, or a dated dataset:

- **Datasets** come resolved as of the filing date through
  `resolve_means_test_data` (core/ust_data.py, core/dollar_amounts.py), and
  the result records the release ids it computed from — the same pin
  discipline packet assembly applies to forms.
- **Derivable figures** arrive as inputs the CALLER derives from case
  records: the CMI result (core/cmi.py), the priority and nonpriority debt
  totals (the claims), the under-18 dependant count. The engine does not
  read stores; purity is what makes the known-answer tests possible.
- **Entered figures** come from the case's `means_test_input`
  (insolvia_core.means_test_inputs) — the actual-expense answers only the
  debtor can supply. An ABSENT entered figure is a zero, exactly as a blank
  box on the form claims nothing; an entered figure that breaks a statutory
  cap is an error naming the cap, never a silent clamp.

Line numbering follows the 04/25 revision so the trace reads against the
printed form; `MeansTestLine.source` says where each amount came from. All
arithmetic is Decimal, quantized to cents per line the way the form's boxes
are, half-up.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from decimal import ROUND_HALF_UP, Decimal
from typing import Final

from insolvia_core.means_test_inputs import MeansTestInputBody

from . import dollar_amounts, ust_data
from .cmi import CmiResult

_CENT: Final = Decimal("0.01")


class MeansTestError(ValueError):
    """The calculation cannot run as asked — a missing required fact, an
    unsupported jurisdiction, an entered figure past a statutory cap.
    Carries every problem found, the fill engine's contract."""

    def __init__(self, problems: list[str]) -> None:
        self.problems = tuple(problems)
        super().__init__("; ".join(problems))


def _money(value: Decimal) -> str:
    return f"{value.quantize(_CENT, rounding=ROUND_HALF_UP):f}"


def _entered(value: str | None) -> Decimal:
    """An entered money figure; absent claims nothing, exactly as a blank
    box on the form does."""
    return Decimal(value) if value is not None else Decimal("0")


@dataclass(frozen=True)
class MeansTestData:
    """The effective-dated datasets, resolved once for one as-of date, with
    the releases kept so the result can record its pins."""

    as_of: date
    medians_release: ust_data.Release
    medians: ust_data.MedianIncomeTable
    national_release: ust_data.Release
    national: ust_data.NationalStandards
    local_release: ust_data.Release
    local: ust_data.LocalStandards
    ch13_release: ust_data.Release
    ch13: ust_data.Ch13AdminMultipliers
    amounts_release: dollar_amounts.Release

    @property
    def release_ids(self) -> dict[str, str]:
        return {
            release.series_id: release.release_id
            for release in (
                self.medians_release,
                self.national_release,
                self.local_release,
                self.ch13_release,
            )
        } | {self.amounts_release.series_id: self.amounts_release.release_id}


def resolve_means_test_data(as_of: date) -> MeansTestData:
    """Every series the test reads, resolved as of one date — the case's
    filing date while it floats, its pinned assembly date afterwards.
    Raises LookupError when any series has no release for the date (the
    no-fallback rule); callers gate on it like any resolution failure."""
    medians_release, medians = ust_data.median_income_table(as_of)
    national_release, national = ust_data.national_standards(as_of)
    local_release, local = ust_data.local_standards(as_of)
    ch13_release, ch13 = ust_data.ch13_admin_multipliers(as_of)
    return MeansTestData(
        as_of=as_of,
        medians_release=medians_release,
        medians=medians,
        national_release=national_release,
        national=national,
        local_release=local_release,
        local=local,
        ch13_release=ch13_release,
        ch13=ch13,
        amounts_release=dollar_amounts.resolve(as_of),
    )


@dataclass(frozen=True)
class MedianComparison:
    """B122A-1 lines 12-14: annualized CMI against the applicable median."""

    state: str
    household_size: int
    monthly_cmi: str
    annualized_cmi: str
    annual_median: str
    above_median: bool
    source: str


@dataclass(frozen=True)
class MeansTestLine:
    """One line of the B122A-2 trace: the printed line number, its subject,
    the computed amount, and where the amount came from — a dataset release,
    an entered field, a derived input, or line arithmetic."""

    line: str
    label: str
    amount: str
    source: str


@dataclass(frozen=True)
class MeansTestCase:
    """Everything one run of the test reads, assembled by the caller from
    case records (the projection layer's job in 10.4).

    `priority_debt_total` is the claims' priority portions summed (line 35);
    `nonpriority_unsecured_total` is Schedule E/F's nonpriority total plus
    priority claims' nonpriority portions (line 41a); `children_under_18`
    counts dependants under 18 for line 29's per-child cap.
    """

    state: str
    county: str
    district: str
    cmi: CmiResult
    inputs: MeansTestInputBody
    priority_debt_total: str
    nonpriority_unsecured_total: str
    children_under_18: int


@dataclass(frozen=True)
class MeansTestResult:
    """The whole determination: which data it used, the median comparison,
    the B122A-2 trace for an above-median debtor (empty below the median),
    and the § 707(b)(2) outcome.

    `outcome` is one of `below_median` / `no_presumption` /
    `presumption_of_abuse`; `determined_by` names the rule that settled it
    (`median`, `threshold_floor`, `threshold_ceiling`, `unsecured_ratio`).
    """

    as_of: date
    release_ids: dict[str, str]
    comparison: MedianComparison
    outcome: str
    determined_by: str
    lines: tuple[MeansTestLine, ...]


def median_comparison(
    *,
    monthly_cmi: str,
    state: str,
    household_size: int,
    data: MeansTestData,
) -> MedianComparison:
    """B122A-1's Part 2: 12x the monthly CMI against the state median for
    the household size (§ 707(b)(7); above 4 the table adds the statutory
    per-person amount). Raises MeansTestError for a jurisdiction the median
    table does not carry."""
    monthly = Decimal(monthly_cmi)
    annualized = monthly * 12
    try:
        median = data.medians.annual_median(state, household_size)
    except (KeyError, ValueError) as error:
        raise MeansTestError([str(error)]) from error
    return MedianComparison(
        state=state.upper(),
        household_size=household_size,
        monthly_cmi=_money(monthly),
        annualized_cmi=_money(annualized),
        annual_median=_money(median),
        above_median=annualized > median,
        source=(
            f"Census median family income, {state.upper()} household of "
            f"{household_size} — {data.medians_release.release_id}"
        ),
    )


def _sixty_month_average(total: Decimal) -> Decimal:
    return (total / 60).quantize(_CENT, rounding=ROUND_HALF_UP)


def run_means_test(case: MeansTestCase, data: MeansTestData) -> MeansTestResult:
    """The whole § 707(b) determination for one case.

    Below-median debtors stop at the comparison (no presumption; B122A-2 is
    not filed). Above-median debtors get the full line-by-line calculation.
    Raises MeansTestError, with every problem named, when required facts
    are missing or an entered figure breaks a statutory cap.
    """
    problems: list[str] = []
    inputs = case.inputs

    under_65 = inputs.people_under_65
    over_65 = inputs.people_65_or_older
    if under_65 is None or over_65 is None:
        raise MeansTestError(
            [
                "the household composition (people under 65 / 65 and older) "
                "has not been entered — line 5's deductions and the median "
                "comparison both need it"
            ]
        )
    household = under_65 + over_65
    if household < 1:
        raise MeansTestError(["the household cannot be empty"])

    comparison = median_comparison(
        monthly_cmi=case.cmi.combined_monthly_total,
        state=case.state,
        household_size=household,
        data=data,
    )
    if not comparison.above_median:
        return MeansTestResult(
            as_of=data.as_of,
            release_ids=data.release_ids,
            comparison=comparison,
            outcome="below_median",
            determined_by="median",
            lines=(),
        )

    lines: list[MeansTestLine] = []

    def put(line: str, label: str, amount: Decimal, source: str) -> Decimal:
        lines.append(
            MeansTestLine(line=line, label=label, amount=_money(amount), source=source)
        )
        return amount

    # ── Part 1: adjusted current monthly income ────────────────────────────
    line_1 = put(
        "1",
        "Total current monthly income",
        Decimal(case.cmi.combined_monthly_total),
        "Form 122A-1 line 11 — the § 101(10A) derivation (core/cmi.py)",
    )
    has_column_b = any(column.column == "B" for column in case.cmi.columns)
    adjustments = inputs.marital_adjustments
    if adjustments and not has_column_b:
        problems.append(
            "marital adjustments are entered but Form 122A-1 has no Column B "
            "— line 3 applies only when a spouse's income is included"
        )
    line_3 = Decimal("0")
    for item in adjustments:
        line_3 += _entered(item.amount)
    put(
        "3",
        "Marital adjustment",
        line_3,
        "entered (means_test_input.marital_adjustments"
        + (
            ": " + "; ".join(item.description or item.id for item in adjustments)
            if adjustments
            else ""
        )
        + ")",
    )
    line_4 = put(
        "4",
        "Adjusted current monthly income",
        line_1 - line_3,
        "line 1 minus line 3",
    )

    # ── Part 2: IRS allowances (lines 5-24) ────────────────────────────────
    put(
        "5",
        "Number of people used in determining deductions",
        Decimal(household),
        "entered (means_test_input.people_under_65 + people_65_or_older)",
    )
    national_source = (
        f"IRS National Standards, household of {household} — "
        f"{data.national_release.release_id}"
    )
    line_6 = put(
        "6",
        "Food, clothing, and other items",
        data.national.allowance(household),
        national_source,
    )
    oop_under = Decimal(data.national.oop_healthcare_under_65)
    oop_over = Decimal(data.national.oop_healthcare_65_and_older)
    put(
        "7a",
        "Out-of-pocket health care allowance per person (under 65)",
        oop_under,
        national_source,
    )
    put("7b", "Number of people who are under 65", Decimal(under_65), "entered")
    line_7c = put(
        "7c", "Subtotal (under 65)", oop_under * under_65, "line 7a x line 7b"
    )
    put(
        "7d",
        "Out-of-pocket health care allowance per person (65 or older)",
        oop_over,
        national_source,
    )
    put("7e", "Number of people who are 65 or older", Decimal(over_65), "entered")
    line_7f = put(
        "7f", "Subtotal (65 or older)", oop_over * over_65, "line 7d x line 7e"
    )
    line_7 = put(
        "7",
        "Out-of-pocket health care allowance",
        line_7c + line_7f,
        "line 7c plus line 7f",
    )

    try:
        county_row = data.local.housing_for(case.state, case.county)
    except KeyError as error:
        raise MeansTestError([*problems, str(error)]) from error
    housing_source = (
        f"IRS Local Standards, {county_row.county}, {case.state.upper()}, "
        f"household of {household} — {data.local_release.release_id}"
    )
    line_8 = put(
        "8",
        "Housing and utilities — insurance and operating expenses",
        county_row.non_mortgage_for(household),
        housing_source,
    )
    line_9a = put(
        "9a",
        "Housing and utilities — mortgage or rent expense (IRS Local Standard)",
        county_row.mortgage_rent_for(household),
        housing_source,
    )
    line_9b = put(
        "9b",
        "Average monthly payment for all debts secured by your home",
        _entered(inputs.home_secured_monthly_total),
        "entered (means_test_input.home_secured_monthly_total)",
    )
    line_9c = put(
        "9c",
        "Net mortgage or rent expense",
        max(Decimal("0"), line_9a - line_9b),
        "line 9a minus line 9b, not below zero",
    )
    line_10 = put(
        "10",
        "Claimed adjustment to the housing standard",
        _entered(inputs.housing_adjustment_amount),
        "entered (means_test_input.housing_adjustment_amount"
        + (
            f": {inputs.housing_adjustment_explanation}"
            if inputs.housing_adjustment_explanation
            else ""
        )
        + ")",
    )
    if inputs.housing_adjustment_amount is not None and not (
        inputs.housing_adjustment_explanation
    ):
        problems.append(
            "line 10's housing adjustment needs its explanation — the form "
            "requires why the UST's division is incorrect"
        )

    vehicles = inputs.vehicle_count or 0
    put(
        "11",
        "Number of vehicles claimed for ownership or operating expenses",
        Decimal(vehicles),
        "entered (means_test_input.vehicle_count)",
    )
    transportation = data.local.transportation
    transport_source = (
        f"IRS Local Standards transportation — {data.local_release.release_id}"
    )
    if vehicles > 0:
        operating = transportation.operating_costs_for(case.state, case.county)
        line_12 = put(
            "12",
            "Vehicle operation expense",
            operating.for_vehicles(vehicles),
            f"{transport_source}, operating costs, {case.county}",
        )
    else:
        line_12 = put(
            "12", "Vehicle operation expense", Decimal("0"), "no vehicles claimed"
        )

    line_13 = Decimal("0")
    line_13b = Decimal("0")
    line_13e = Decimal("0")
    if vehicles >= 1:
        ownership_each = transportation.ownership_costs.for_vehicles(1)
        line_13a = put(
            "13a",
            "Vehicle 1 ownership costs (IRS Local Standard)",
            ownership_each,
            transport_source,
        )
        line_13b = put(
            "13b",
            "Average monthly payment for debts secured by Vehicle 1",
            _entered(inputs.vehicle_1_loan_monthly),
            "entered (means_test_input.vehicle_1_loan_monthly)",
        )
        line_13c = put(
            "13c",
            "Net Vehicle 1 ownership or lease expense",
            max(Decimal("0"), line_13a - line_13b),
            "line 13a minus line 13b, not below zero",
        )
        line_13 += line_13c
    if vehicles >= 2:
        ownership_each = transportation.ownership_costs.for_vehicles(1)
        line_13d = put(
            "13d",
            "Vehicle 2 ownership costs (IRS Local Standard)",
            ownership_each,
            transport_source,
        )
        line_13e = put(
            "13e",
            "Average monthly payment for debts secured by Vehicle 2",
            _entered(inputs.vehicle_2_loan_monthly),
            "entered (means_test_input.vehicle_2_loan_monthly)",
        )
        line_13f = put(
            "13f",
            "Net Vehicle 2 ownership or lease expense",
            max(Decimal("0"), line_13d - line_13e),
            "line 13d minus line 13e, not below zero",
        )
        line_13 += line_13f
    put(
        "13",
        "Vehicle ownership or lease expense",
        line_13,
        "net vehicle expenses added",
    )

    public_transit = Decimal(transportation.public_transportation_national)
    if vehicles == 0:
        line_14 = put(
            "14", "Public transportation expense", public_transit, transport_source
        )
    else:
        line_14 = put(
            "14",
            "Public transportation expense",
            Decimal("0"),
            "vehicles claimed on line 11",
        )
    line_15 = _entered(inputs.additional_public_transportation)
    if vehicles == 0 and line_15 > 0:
        problems.append(
            "line 15's additional public transportation applies only when "
            "one or more vehicles are claimed on line 11"
        )
    if line_15 > public_transit:
        problems.append(
            f"line 15 may not exceed the IRS public transportation standard "
            f"({_money(public_transit)})"
        )
    put(
        "15",
        "Additional public transportation expense",
        line_15,
        "entered (means_test_input.additional_public_transportation), capped "
        "at the IRS public transportation standard",
    )

    other_necessary: list[tuple[str, str, str | None]] = [
        ("16", "Taxes", inputs.taxes),
        ("17", "Involuntary deductions", inputs.involuntary_deductions),
        ("18", "Term life insurance", inputs.term_life_insurance),
        ("19", "Court-ordered payments", inputs.court_ordered_payments),
        (
            "20",
            "Education required for employment or for a disabled child",
            inputs.education_for_employment_or_disability,
        ),
        ("21", "Childcare", inputs.childcare),
        (
            "22",
            "Additional health care expenses, excluding insurance costs",
            inputs.healthcare_above_allowance,
        ),
        ("23", "Optional telephones and telephone services", inputs.optional_telecom),
    ]
    other_necessary_total = Decimal("0")
    for number, label, value in other_necessary:
        field_name = {
            "16": "taxes",
            "17": "involuntary_deductions",
            "18": "term_life_insurance",
            "19": "court_ordered_payments",
            "20": "education_for_employment_or_disability",
            "21": "childcare",
            "22": "healthcare_above_allowance",
            "23": "optional_telecom",
        }[number]
        other_necessary_total += put(
            number, label, _entered(value), f"entered (means_test_input.{field_name})"
        )
    line_24 = put(
        "24",
        "All expenses allowed under the IRS expense allowances",
        line_6
        + line_7
        + line_8
        + line_9c
        + line_10
        + line_12
        + line_13
        + line_14
        + line_15
        + other_necessary_total,
        "lines 6 + 7 + 8 + 9c + 10 + 12 + 13 + 14 + 15 + 16 through 23",
    )

    # ── Part 2: additional expense deductions (lines 25-32) ────────────────
    line_25 = put(
        "25",
        "Health insurance, disability insurance, and HSA expenses",
        _entered(inputs.health_insurance)
        + _entered(inputs.disability_insurance)
        + _entered(inputs.health_savings_account),
        "entered (means_test_input.health_insurance + disability_insurance "
        "+ health_savings_account)",
    )
    line_26 = put(
        "26",
        "Continuing contributions to the care of household or family members",
        _entered(inputs.family_care_contributions),
        "entered (means_test_input.family_care_contributions) — "
        "11 U.S.C. § 707(b)(2)(A)(ii)(II)",
    )
    line_27 = put(
        "27",
        "Protection against family violence",
        _entered(inputs.family_violence_protection),
        "entered (means_test_input.family_violence_protection)",
    )
    line_28 = put(
        "28",
        "Additional home energy costs",
        _entered(inputs.home_energy_excess),
        "entered (means_test_input.home_energy_excess)",
    )
    education_cap_amount = data.amounts_release.amount(
        "means-test-education-annual-cap-per-child"
    )
    monthly_cap_per_child = (education_cap_amount.value / 12).quantize(
        _CENT, rounding=ROUND_HALF_UP
    )
    education_cap = monthly_cap_per_child * case.children_under_18
    line_29 = _entered(inputs.education_under_18)
    if line_29 > education_cap:
        problems.append(
            f"line 29 exceeds the {education_cap_amount.citation} cap of "
            f"{_money(monthly_cap_per_child)} per month per child under 18 "
            f"({case.children_under_18} children — cap {_money(education_cap)})"
        )
    put(
        "29",
        "Education expenses for dependent children younger than 18",
        line_29,
        f"entered (means_test_input.education_under_18), capped per child by "
        f"{education_cap_amount.citation} — {data.amounts_release.release_id}",
    )
    food_clothing_cap = data.national.additional_food_clothing_cap_for(household)
    line_30 = _entered(inputs.additional_food_clothing)
    if line_30 > food_clothing_cap:
        problems.append(
            f"line 30 exceeds the published 5% food-and-clothing cap of "
            f"{_money(food_clothing_cap)} for a household of {household}"
        )
    put(
        "30",
        "Additional food and clothing expense",
        line_30,
        f"entered (means_test_input.additional_food_clothing), capped at 5% "
        f"of the food and clothing allowances — {data.national_release.release_id}",
    )
    line_31 = put(
        "31",
        "Continuing charitable contributions",
        _entered(inputs.charitable_contributions),
        "entered (means_test_input.charitable_contributions)",
    )
    line_32 = put(
        "32",
        "All additional expense deductions",
        line_25 + line_26 + line_27 + line_28 + line_29 + line_30 + line_31,
        "lines 25 through 31",
    )

    # ── Part 2: deductions for debt payment (lines 33-37) ──────────────────
    put("33a", "Copy of line 9b", line_9b, "line 9b")
    put("33b", "Copy of line 13b", line_13b, "line 13b")
    put("33c", "Copy of line 13e", line_13e, "line 13e")
    line_33d = Decimal("0")
    for payment in inputs.other_secured_payments:
        line_33d += _entered(payment.monthly_payment)
    put(
        "33d",
        "Other debts secured by your property",
        line_33d,
        "entered (means_test_input.other_secured_payments"
        + (
            ": "
            + "; ".join(
                f"{p.creditor_name or p.id} ({p.property_description or 'property'})"
                for p in inputs.other_secured_payments
            )
            if inputs.other_secured_payments
            else ""
        )
        + ")",
    )
    line_33e = put(
        "33e",
        "Total average monthly payment on secured debts",
        line_9b + line_13b + line_13e + line_33d,
        "lines 33a through 33d",
    )
    line_34 = put(
        "34",
        "Past-due amounts on secured debts necessary for support (÷ 60)",
        _sixty_month_average(_entered(inputs.priority_cure_total)),
        "entered (means_test_input.priority_cure_total) divided by 60 — "
        "11 U.S.C. § 707(b)(2)(A)(iii)(II)",
    )
    line_35 = put(
        "35",
        "Priority claims (÷ 60)",
        _sixty_month_average(Decimal(case.priority_debt_total)),
        "the claims' priority portions divided by 60 — 11 U.S.C. § 707(b)(2)(A)(iv)",
    )
    if inputs.ch13_eligible:
        plan_payment = _entered(inputs.ch13_projected_plan_payment)
        try:
            multiplier = data.ch13.multiplier_for(case.district)
        except KeyError as error:
            raise MeansTestError([*problems, str(error)]) from error
        line_36 = put(
            "36",
            "Chapter 13 administrative expenses",
            (plan_payment * multiplier).quantize(_CENT, rounding=ROUND_HALF_UP),
            f"projected plan payment {_money(plan_payment)} x the "
            f"{case.district} multiplier {multiplier} — "
            f"{data.ch13_release.release_id}; "
            "11 U.S.C. § 707(b)(2)(A)(ii)(III)",
        )
    else:
        line_36 = put(
            "36",
            "Chapter 13 administrative expenses",
            Decimal("0"),
            "not eligible to file under Chapter 13 (11 U.S.C. § 109(e))",
        )
    line_37 = put(
        "37",
        "All deductions for debt payment",
        line_33e + line_34 + line_35 + line_36,
        "lines 33e + 34 + 35 + 36",
    )

    # ── Part 2 total, Part 3: the determination ────────────────────────────
    line_38 = put(
        "38",
        "Total deductions from income",
        line_24 + line_32 + line_37,
        "lines 24 + 32 + 37",
    )
    put("39a", "Copy of line 4, adjusted current monthly income", line_4, "line 4")
    put("39b", "Copy of line 38, total deductions", line_38, "line 38")
    line_39c = put(
        "39c",
        "Monthly disposable income (11 U.S.C. § 707(b)(2))",
        line_4 - line_38,
        "line 39a minus line 39b",
    )
    line_39d = put("39d", "Total over 60 months", line_39c * 60, "line 39c x 60")

    floor = data.amounts_release.amount("means-test-presumption-floor-60mo")
    ceiling = data.amounts_release.amount("means-test-presumption-ceiling-60mo")
    threshold_source = (
        f"{floor.citation} / {ceiling.citation} — {data.amounts_release.release_id}"
    )
    if line_39d < floor.value:
        outcome, determined_by = "no_presumption", "threshold_floor"
        put(
            "40",
            "Presumption determination",
            line_39d,
            f"line 39d is less than {floor.amount}: no presumption of abuse "
            f"({threshold_source})",
        )
    elif line_39d > ceiling.value:
        outcome, determined_by = "presumption_of_abuse", "threshold_ceiling"
        put(
            "40",
            "Presumption determination",
            line_39d,
            f"line 39d is more than {ceiling.amount}: the presumption of "
            f"abuse arises ({threshold_source})",
        )
    else:
        nonpriority = Decimal(case.nonpriority_unsecured_total)
        put(
            "41a",
            "Total nonpriority unsecured debt",
            nonpriority,
            "the claims' nonpriority unsecured portions",
        )
        line_41b = put(
            "41b",
            "25% of total nonpriority unsecured debt",
            (nonpriority * Decimal("0.25")).quantize(_CENT, rounding=ROUND_HALF_UP),
            f"line 41a x 0.25 — {floor.citation}",
        )
        if line_39d >= line_41b:
            outcome, determined_by = "presumption_of_abuse", "unsecured_ratio"
            put(
                "42",
                "Presumption determination",
                line_39d,
                "line 39d is at least line 41b: the presumption of abuse "
                f"arises ({threshold_source})",
            )
        else:
            outcome, determined_by = "no_presumption", "unsecured_ratio"
            put(
                "42",
                "Presumption determination",
                line_39d,
                "line 39d is less than line 41b: no presumption of abuse "
                f"({threshold_source})",
            )

    if problems:
        raise MeansTestError(problems)

    return MeansTestResult(
        as_of=data.as_of,
        release_ids=data.release_ids,
        comparison=comparison,
        outcome=outcome,
        determined_by=determined_by,
        lines=tuple(lines),
    )
