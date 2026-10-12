"""The means-test engine — rule-based, effective-dated, pure (issues #101,
#365): § 707(b) for a Chapter 7 case, § 1325(b) for a Chapter 13 case, one
engine selected by the case's chapter.

The legally required calculations: for Chapter 7, the median-income
comparison (§ 707(b)(7); B122A-1 lines 12-14) and, for above-median debtors,
the full B122A-2 calculation (revision 04/25) ending in the presumption-of-
abuse determination (§ 707(b)(2)); for Chapter 13, B122C-1's applicable
commitment period (§ 1325(b)(4): 36 months below the median, 60 at or above
it) and, for above-median debtors, B122C-2's monthly disposable income under
§ 1325(b)(2) — the SAME National and Local Standards and actual-expense
deductions as B122A-2 (lines 5-38 are identical on both forms, by the forms'
own design), followed by the Chapter 13-only subtractions (support income
for dependent children, qualified retirement deductions, special
circumstances). Wrong means dismissal, a trustee challenge, or a plan that
cannot be confirmed, so the numbers stay deterministic — Claude never touches
them (the register's LOGIC rule) — and every figure in the output traces to a
rule, an input, or a dated dataset:

- **Datasets** come resolved as of the filing date through
  `resolve_means_test_data` (core/ust_data.py, core/dollar_amounts.py), and
  the result records the release ids it computed from — the same pin
  discipline packet assembly applies to forms.
- **Derivable figures** arrive as inputs the CALLER derives from case
  records: the CMI result (core/cmi.py), the priority and nonpriority debt
  totals (the claims), the under-18 dependant count, the case's chapter. The
  engine does not read stores; purity is what makes the known-answer tests
  possible.
- **Entered figures** come from the case's `means_test_input`
  (insolvia_core.means_test_inputs) — the actual-expense answers only the
  debtor can supply. An ABSENT entered figure is a zero, exactly as a blank
  box on the form claims nothing; an entered figure that breaks a statutory
  cap is an error naming the cap, never a silent clamp.

Line numbering follows the printed revisions (B122A-2 04/25, B122C-1 10/19,
B122C-2 04/25) so the trace reads against the form; `MeansTestLine.form`
says which form a line belongs to (B122C-1's Part 2-3 lines and B122C-2's
lines share numbers 12-21) and `MeansTestLine.source` where each amount came
from. All arithmetic is Decimal, quantized to cents per line the way the
form's boxes are, half-up.

Issue #349 (the means-test screen) widened the ENTERED inputs without
touching a rule — every new field defaults to what the engine did before:

- **The presumption exemptions** (Form 122A-1Supp; `non_consumer_debts`,
  `disabled_veteran`, `reservist_national_guard`) are checked first. Any
  one ends the test with outcome `exempt`, `determined_by` naming the
  flag, no B122A-2 lines, and the median comparison still reported when
  the household is known — the screen shows it, the form does not need it.
  They are § 707(b) concepts and never consulted for a Chapter 13 case.
- **Three household sizes.** The median comparison takes
  `median_household_size`, line 5's IRS family size takes
  `irs_family_size`, and the housing standard (lines 8 and 9a) takes
  `irs_housing_family_size`; each falls back to `people_under_65 +
  people_65_or_older`, the one figure the engine read before.
- **Per-claim secured payments.** Where the single typed totals
  (`home_secured_monthly_total`, `vehicle_1_loan_monthly`,
  `vehicle_2_loan_monthly`, `priority_cure_total`) are absent, lines 9b,
  13b, 13e and 34 are the sums of the `other_secured_payments` rows in the
  matching bucket, and line 33d takes the rows in no bucket or `other`.
  A typed total wins outright rather than adding, so the panel beside a
  claim and the typed figure can never both count.

The Chapter 13 branch (issue #365) reads the same inputs plus the four the
Chapter 13 forms ask on their own (`commitment_period_marital_adjustment`,
`child_support_for_dependents`, `qualified_retirement_deductions`,
`special_circumstances`), and differs from Chapter 7 in exactly these ways:

- **B122C-1 subtracts the marital adjustment BEFORE the median comparison**
  (lines 12-14; line 15b is line 14 x 12), where B122A-1 compares the raw
  total and B122A-2 line 3 adjusts afterwards. The comparison's
  `monthly_cmi` is therefore line 14 on a Chapter 13 trace.
- **The commitment period is its own comparison** (Part 3, lines 18-21):
  the raw line 11 total, less the marital adjustment only when the debtor
  contends it applies under § 1325(b)(4), annualized against the same
  median — strictly below it is 3 years, at or above it is 5. The two
  comparisons can disagree when the adjustment is claimed on line 13 but
  not on line 19a; both are reported.
- **Line 36 always multiplies** the projected plan payment by the district
  multiplier: a Chapter 13 debtor is filing under Chapter 13, so B122A-2's
  eligibility question does not exist on B122C-2.
- **No presumption.** Above the median, B122C-2 Part 2 subtracts lines
  40-43 from line 39 (B122C-1 line 14) to reach line 45, the monthly
  disposable income the plan must commit; outcome `above_median`, never
  `no_presumption` / `presumption_of_abuse`.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from decimal import ROUND_HALF_UP, Decimal
from typing import Final

from insolvia_core.means_test_inputs import MeansTestInputBody, OtherSecuredPayment

from . import dollar_amounts, ust_data
from .cmi import CmiResult

_CENT: Final = Decimal("0.01")

# The form a trace line belongs to.
FORM_122A2: Final = "122A-2"
FORM_122C1: Final = "122C-1"
FORM_122C2: Final = "122C-2"

# § 1325(b)(4)'s two applicable commitment periods, in months.
COMMITMENT_PERIOD_BELOW_MEDIAN: Final = 36
COMMITMENT_PERIOD_ABOVE_MEDIAN: Final = 60

# The three Form 122A-1Supp exemptions, in the order the form asks them,
# each with the rule that grants it.
PRESUMPTION_EXEMPTIONS: Final = (
    (
        "non_consumer_debts",
        "debts are not primarily consumer debts — 11 U.S.C. § 707(b)(1); "
        "Form 122A-1Supp Part 1",
    ),
    (
        "disabled_veteran",
        "disabled veteran whose debts were incurred primarily during active "
        "duty or homeland-defense activity — 11 U.S.C. § 707(b)(2)(D)(i); "
        "Form 122A-1Supp Part 2",
    ),
    (
        "reservist_national_guard",
        "reservist or National Guard member called to active duty after "
        "September 11, 2001 — 11 U.S.C. § 707(b)(2)(D)(ii); "
        "Form 122A-1Supp Part 2",
    ),
)


def presumption_exemption(inputs: MeansTestInputBody) -> tuple[str, str] | None:
    """The first exemption the entered inputs claim, as (flag, rule), or
    None when the presumption can arise."""
    for flag, rule in PRESUMPTION_EXEMPTIONS:
        if getattr(inputs, flag) is True:
            return flag, rule
    return None


def _entered_household(inputs: MeansTestInputBody) -> int | None:
    if inputs.people_under_65 is None or inputs.people_65_or_older is None:
        return None
    return inputs.people_under_65 + inputs.people_65_or_older


def median_household_size(inputs: MeansTestInputBody) -> tuple[int, str] | None:
    """B122A-1 line 13's (B122C-1 line 16b's) household size and where it
    came from: the entered override, else the sum of the age bands; None
    until either exists."""
    if inputs.median_household_size is not None:
        return (
            inputs.median_household_size,
            "entered (means_test_input.median_household_size)",
        )
    entered = _entered_household(inputs)
    if entered is None:
        return None
    return entered, "entered (means_test_input.people_under_65 + people_65_or_older)"


def irs_family_size(inputs: MeansTestInputBody) -> tuple[int, str] | None:
    """Line 5's "number of people used in determining deductions"."""
    if inputs.irs_family_size is not None:
        return inputs.irs_family_size, "entered (means_test_input.irs_family_size)"
    entered = _entered_household(inputs)
    if entered is None:
        return None
    return entered, "entered (means_test_input.people_under_65 + people_65_or_older)"


def irs_housing_family_size(inputs: MeansTestInputBody) -> tuple[int, str] | None:
    """The family size the IRS housing and utilities standard is read at
    (lines 8 and 9a)."""
    if inputs.irs_housing_family_size is not None:
        return (
            inputs.irs_housing_family_size,
            "entered (means_test_input.irs_housing_family_size)",
        )
    entered = _entered_household(inputs)
    if entered is None:
        return None
    return entered, "entered (means_test_input.people_under_65 + people_65_or_older)"


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
    """B122A-1 lines 12-14 / B122C-1 lines 15-17: annualized CMI against the
    applicable median. On a Chapter 13 trace `monthly_cmi` is B122C-1 line
    14 (after the marital adjustment), on a Chapter 7 trace line 11."""

    state: str
    household_size: int
    monthly_cmi: str
    annualized_cmi: str
    annual_median: str
    above_median: bool
    source: str


@dataclass(frozen=True)
class MeansTestLine:
    """One line of the trace: the form it prints on, the printed line
    number, its subject, the computed amount, and where the amount came from
    — a dataset release, an entered field, a derived input, or line
    arithmetic."""

    line: str
    label: str
    amount: str
    source: str
    form: str


@dataclass(frozen=True)
class CommitmentPeriod:
    """B122C-1 Part 3 (11 U.S.C. § 1325(b)(4)): the applicable commitment
    period and the comparison that set it — line 19b (the raw total less
    the marital adjustment when contended), annualized on line 20b against
    the median on line 20c."""

    months: int
    marital_adjustment: str
    monthly_income: str
    annualized_income: str
    annual_median: str
    source: str


@dataclass(frozen=True)
class MeansTestCase:
    """Everything one run of the test reads, assembled by the caller from
    case records (the projection layer's job in 10.4).

    `priority_debt_total` is the claims' priority portions summed (line 35);
    `nonpriority_unsecured_total` is Schedule E/F's nonpriority total plus
    priority claims' nonpriority portions (line 41a); `children_under_18`
    counts dependants under 18 for line 29's per-child cap; `chapter`
    selects the calculation (7 or 13 — the only chapters with a form).
    """

    state: str
    county: str
    district: str
    cmi: CmiResult
    inputs: MeansTestInputBody
    priority_debt_total: str
    nonpriority_unsecured_total: str
    children_under_18: int
    chapter: int = 7


@dataclass(frozen=True)
class MeansTestResult:
    """The whole determination: which data it used, the median comparison,
    the calculation form's trace (B122A-2 lines for an above-median Chapter
    7 debtor; B122C-1's Part 2-3 lines and, above the median, B122C-2's
    lines for a Chapter 13 debtor — empty below the median on Chapter 7),
    and the outcome.

    `outcome` is one of `below_median` / `no_presumption` /
    `presumption_of_abuse` / `exempt` on Chapter 7 and `below_median` /
    `above_median` on Chapter 13; `determined_by` names the rule that
    settled it (`median`, `threshold_floor`, `threshold_ceiling`,
    `unsecured_ratio`, or the exemption flag). `comparison` is None only
    for an exempt debtor whose household has not been entered — the one
    outcome the median does not decide. `commitment` is the § 1325(b)(4)
    period (Chapter 13 only) and `disposable_income` B122C-2 line 45
    (Chapter 13, above the median only).
    """

    as_of: date
    release_ids: dict[str, str]
    comparison: MedianComparison | None
    outcome: str
    determined_by: str
    lines: tuple[MeansTestLine, ...]
    chapter: int = 7
    commitment: CommitmentPeriod | None = None
    disposable_income: str | None = None


def median_comparison(
    *,
    monthly_cmi: str,
    state: str,
    household_size: int,
    data: MeansTestData,
) -> MedianComparison:
    """B122A-1's Part 2 / B122C-1's line 17: 12x the monthly figure against
    the state median for the household size (§ 707(b)(7); above 4 the table
    adds the statutory per-person amount). Raises MeansTestError for a
    jurisdiction the median table does not carry."""
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


def _row_name(payment: OtherSecuredPayment) -> str:
    creditor = payment.creditor_name or payment.id
    return f"{creditor} ({payment.property_description or 'property'})"


def _secured_payment(
    inputs: MeansTestInputBody, *, bucket: str, typed: str | None, field_name: str
) -> tuple[Decimal, str]:
    """One of lines 9b / 13b / 13e: the typed total when entered, else the
    sum of the per-claim rows in this bucket, with the source saying which."""
    if typed is not None:
        return _entered(typed), f"entered (means_test_input.{field_name})"
    rows = [p for p in inputs.other_secured_payments if p.bucket == bucket]
    if not rows:
        return Decimal("0"), f"entered (means_test_input.{field_name})"
    total = sum((_entered(p.monthly_payment) for p in rows), Decimal("0"))
    return total, (
        f"entered per claim (means_test_input.other_secured_payments, "
        f"bucket {bucket}: " + "; ".join(_row_name(p) for p in rows) + ")"
    )


def _cure_total(inputs: MeansTestInputBody) -> tuple[Decimal, str]:
    """Line 34's past-due total before ÷ 60: the typed figure when entered,
    else the per-claim cure amounts summed."""
    if inputs.priority_cure_total is not None:
        return (
            _entered(inputs.priority_cure_total),
            "entered (means_test_input.priority_cure_total)",
        )
    rows = [p for p in inputs.other_secured_payments if p.cure_total is not None]
    if not rows:
        return Decimal("0"), "entered (means_test_input.priority_cure_total)"
    total = sum((_entered(p.cure_total) for p in rows), Decimal("0"))
    return total, (
        "entered per claim (means_test_input.other_secured_payments[].cure_total: "
        + "; ".join(_row_name(p) for p in rows)
        + ")"
    )


def _marital_adjustment(
    case: MeansTestCase, *, form: str, line: str, problems: list[str]
) -> tuple[Decimal, str]:
    """B122A-2 line 3 / B122C-1 line 13: the entered rows summed, with the
    check that a spouse's income is actually included (Column B)."""
    inputs = case.inputs
    has_column_b = any(column.column == "B" for column in case.cmi.columns)
    adjustments = inputs.marital_adjustments
    if adjustments and not has_column_b:
        problems.append(
            f"marital adjustments are entered but Form {form} has no Column B "
            f"— line {line} applies only when a spouse's income is included"
        )
    total = Decimal("0")
    for item in adjustments:
        total += _entered(item.amount)
    source = "entered (means_test_input.marital_adjustments" + (
        ": " + "; ".join(item.description or item.id for item in adjustments)
        if adjustments
        else ""
    )
    return total, source + ")"


class _Trace:
    """The lines one run accumulates, in printed order."""

    def __init__(self) -> None:
        self.lines: list[MeansTestLine] = []

    def put(
        self, form: str, line: str, label: str, amount: Decimal, source: str
    ) -> Decimal:
        self.lines.append(
            MeansTestLine(
                line=line, label=label, amount=_money(amount), source=source, form=form
            )
        )
        return amount


def _household_for_deductions(
    inputs: MeansTestInputBody,
) -> tuple[int, int, tuple[int, str], tuple[int, str]]:
    """Line 5 and line 7's inputs: the age bands (required above the
    median — line 7's per-person allowances need them), the IRS family
    size and the housing family size."""
    under_65 = inputs.people_under_65
    over_65 = inputs.people_65_or_older
    if under_65 is None or over_65 is None:
        raise MeansTestError(
            [
                "the household's age bands (people under 65 / 65 and older) "
                "have not been entered — line 7's health care allowance "
                "needs them"
            ]
        )
    family = irs_family_size(inputs)
    housing_family = irs_housing_family_size(inputs)
    assert family is not None  # both age bands are set above
    assert housing_family is not None
    if family[0] < 1:
        raise MeansTestError(["the household cannot be empty"])
    return under_65, over_65, family, housing_family


def _deductions(
    case: MeansTestCase,
    data: MeansTestData,
    trace: _Trace,
    *,
    form: str,
    problems: list[str],
) -> Decimal:
    """Lines 5-38 — the IRS allowances, the additional expense deductions
    and the deductions for debt payment — identical on B122A-2 and B122C-2
    except line 36's rule, which `form` selects. Returns line 38."""
    inputs = case.inputs
    under_65, over_65, family, housing_family = _household_for_deductions(inputs)
    household, household_source = family

    def put(line: str, label: str, amount: Decimal, source: str) -> Decimal:
        return trace.put(form, line, label, amount, source)

    # ── IRS allowances (lines 5-24) ────────────────────────────────────────
    put(
        "5",
        "Number of people used in determining deductions",
        Decimal(household),
        household_source,
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
    housing_size, housing_size_source = housing_family
    housing_source = (
        f"IRS Local Standards, {county_row.county}, {case.state.upper()}, "
        f"household of {housing_size} — {data.local_release.release_id}"
    )
    if housing_size != household:
        housing_source += f" ({housing_size_source})"
    line_8 = put(
        "8",
        "Housing and utilities — insurance and operating expenses",
        county_row.non_mortgage_for(housing_size),
        housing_source,
    )
    line_9a = put(
        "9a",
        "Housing and utilities — mortgage or rent expense (IRS Local Standard)",
        county_row.mortgage_rent_for(housing_size),
        housing_source,
    )
    line_9b = put(
        "9b",
        "Average monthly payment for all debts secured by your home",
        *_secured_payment(
            inputs,
            bucket="home",
            typed=inputs.home_secured_monthly_total,
            field_name="home_secured_monthly_total",
        ),
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
            *_secured_payment(
                inputs,
                bucket="vehicle_1",
                typed=inputs.vehicle_1_loan_monthly,
                field_name="vehicle_1_loan_monthly",
            ),
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
            *_secured_payment(
                inputs,
                bucket="vehicle_2",
                typed=inputs.vehicle_2_loan_monthly,
                field_name="vehicle_2_loan_monthly",
            ),
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

    other_necessary: list[tuple[str, str, str, str | None]] = [
        ("16", "Taxes", "taxes", inputs.taxes),
        (
            "17",
            "Involuntary deductions",
            "involuntary_deductions",
            inputs.involuntary_deductions,
        ),
        (
            "18",
            "Term life insurance",
            "term_life_insurance",
            inputs.term_life_insurance,
        ),
        (
            "19",
            "Court-ordered payments",
            "court_ordered_payments",
            inputs.court_ordered_payments,
        ),
        (
            "20",
            "Education required for employment or for a disabled child",
            "education_for_employment_or_disability",
            inputs.education_for_employment_or_disability,
        ),
        ("21", "Childcare", "childcare", inputs.childcare),
        (
            "22",
            "Additional health care expenses, excluding insurance costs",
            "healthcare_above_allowance",
            inputs.healthcare_above_allowance,
        ),
        (
            "23",
            "Optional telephones and telephone services",
            "optional_telecom",
            inputs.optional_telecom,
        ),
    ]
    other_necessary_total = Decimal("0")
    for number, label, field_name, value in other_necessary:
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

    # ── additional expense deductions (lines 25-32) ────────────────────────
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

    # ── deductions for debt payment (lines 33-37) ──────────────────────────
    put("33a", "Copy of line 9b", line_9b, "line 9b")
    put("33b", "Copy of line 13b", line_13b, "line 13b")
    put("33c", "Copy of line 13e", line_13e, "line 13e")
    # Rows in a home or vehicle bucket already landed on 9b/13b/13e (or were
    # displaced by a typed total there); only the rest are "other".
    other_rows = [
        p for p in inputs.other_secured_payments if p.bucket in (None, "other")
    ]
    line_33d = Decimal("0")
    for payment in other_rows:
        line_33d += _entered(payment.monthly_payment)
    put(
        "33d",
        "Other debts secured by your property",
        line_33d,
        "entered (means_test_input.other_secured_payments"
        + (": " + "; ".join(_row_name(p) for p in other_rows) if other_rows else "")
        + ")",
    )
    line_33e = put(
        "33e",
        "Total average monthly payment on secured debts",
        line_9b + line_13b + line_13e + line_33d,
        "lines 33a through 33d",
    )
    cure_total, cure_source = _cure_total(inputs)
    line_34 = put(
        "34",
        "Past-due amounts on secured debts necessary for support (÷ 60)",
        _sixty_month_average(cure_total),
        f"{cure_source} divided by 60 — 11 U.S.C. § 707(b)(2)(A)(iii)(II)",
    )
    line_35 = put(
        "35",
        "Priority claims (÷ 60)",
        _sixty_month_average(Decimal(case.priority_debt_total)),
        "the claims' priority portions divided by 60 — 11 U.S.C. § 707(b)(2)(A)(iv)",
    )
    if form == FORM_122C2 or inputs.ch13_eligible:
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
            "11 U.S.C. § 707(b)(2)(A)(ii)(III)"
            + (" applied by § 1325(b)(3)" if form == FORM_122C2 else ""),
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

    return put(
        "38",
        "Total deductions from income",
        line_24 + line_32 + line_37,
        "lines 24 + 32 + 37",
    )


def _median_size_or_refuse(inputs: MeansTestInputBody, *, needed_by: str) -> int:
    median_size = median_household_size(inputs)
    if median_size is None:
        raise MeansTestError(
            [
                "the household composition (people under 65 / 65 and older) "
                f"has not been entered — {needed_by}"
            ]
        )
    if median_size[0] < 1:
        raise MeansTestError(["the household cannot be empty"])
    return median_size[0]


def _run_chapter_7(case: MeansTestCase, data: MeansTestData) -> MeansTestResult:
    """The whole § 707(b) determination: B122A-1's comparison, then B122A-2
    for an above-median debtor."""
    problems: list[str] = []
    inputs = case.inputs

    median_size = median_household_size(inputs)

    exemption = presumption_exemption(inputs)
    if exemption is not None:
        # Form 122A-1Supp ends the test here. The comparison is still
        # reported when it can be made — an attorney advising a client wants
        # to see it — but nothing depends on it.
        preview: MedianComparison | None = None
        if median_size is not None and median_size[0] >= 1:
            try:
                preview = median_comparison(
                    monthly_cmi=case.cmi.combined_monthly_total,
                    state=case.state,
                    household_size=median_size[0],
                    data=data,
                )
            except MeansTestError:
                preview = None
        return MeansTestResult(
            as_of=data.as_of,
            release_ids=data.release_ids,
            comparison=preview,
            outcome="exempt",
            determined_by=exemption[0],
            lines=(),
            chapter=7,
        )

    household_size = _median_size_or_refuse(
        inputs,
        needed_by="line 5's deductions and the median comparison both need it",
    )
    comparison = median_comparison(
        monthly_cmi=case.cmi.combined_monthly_total,
        state=case.state,
        household_size=household_size,
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
            chapter=7,
        )

    trace = _Trace()

    def put(line: str, label: str, amount: Decimal, source: str) -> Decimal:
        return trace.put(FORM_122A2, line, label, amount, source)

    # ── Part 1: adjusted current monthly income ────────────────────────────
    line_1 = put(
        "1",
        "Total current monthly income",
        Decimal(case.cmi.combined_monthly_total),
        "Form 122A-1 line 11 — the § 101(10A) derivation (core/cmi.py)",
    )
    line_3 = put(
        "3",
        "Marital adjustment",
        *_marital_adjustment(case, form="122A-1", line="3", problems=problems),
    )
    line_4 = put(
        "4",
        "Adjusted current monthly income",
        line_1 - line_3,
        "line 1 minus line 3",
    )

    # ── Part 2: the deductions (lines 5-38) ────────────────────────────────
    line_38 = _deductions(case, data, trace, form=FORM_122A2, problems=problems)

    # ── Part 3: the determination ──────────────────────────────────────────
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
        lines=tuple(trace.lines),
        chapter=7,
    )


def _run_chapter_13(case: MeansTestCase, data: MeansTestData) -> MeansTestResult:
    """B122C-1's two comparisons (§ 1325(b)(3)'s "is disposable income
    determined by the standards" on line 17, § 1325(b)(4)'s commitment
    period on line 21), then B122C-2 for an above-median debtor."""
    problems: list[str] = []
    inputs = case.inputs
    trace = _Trace()

    def put(line: str, label: str, amount: Decimal, source: str) -> Decimal:
        return trace.put(FORM_122C1, line, label, amount, source)

    household_size = _median_size_or_refuse(
        inputs,
        needed_by="line 16b's household size and line 5's deductions both need it",
    )

    # ── B122C-1 Part 2: how to measure the deductions ─────────────────────
    line_11 = put(
        "11",
        "Total average monthly income",
        Decimal(case.cmi.combined_monthly_total),
        "Form 122C-1 line 11 — the § 101(10A) derivation (core/cmi.py)",
    )
    line_12 = put("12", "Copy of line 11", line_11, "line 11")
    line_13 = put(
        "13",
        "Marital adjustment",
        *_marital_adjustment(case, form="122C-1", line="13", problems=problems),
    )
    line_14 = put(
        "14", "Current monthly income", line_12 - line_13, "line 12 minus line 13"
    )
    put("15a", "Copy of line 14", line_14, "line 14")
    comparison = median_comparison(
        monthly_cmi=_money(line_14),
        state=case.state,
        household_size=household_size,
        data=data,
    )
    median = Decimal(comparison.annual_median)
    put(
        "15b",
        "Current monthly income for the year",
        line_14 * 12,
        "line 15a x 12 — 11 U.S.C. § 1325(b)(3)",
    )
    put(
        "16c",
        "Median family income for your state and size of household",
        median,
        comparison.source,
    )
    put(
        "17",
        "Whether disposable income is determined under 11 U.S.C. § 1325(b)(3)",
        Decimal(1 if comparison.above_median else 0),
        (
            "line 15b is more than line 16c: disposable income is determined "
            "under § 1325(b)(3) — fill out Form 122C-2 (line 17b)"
            if comparison.above_median
            else "line 15b is less than or equal to line 16c: disposable "
            "income is not determined under § 1325(b)(3) — Form 122C-2 is "
            "not filed (line 17a)"
        ),
    )

    # ── B122C-1 Part 3: the commitment period ─────────────────────────────
    line_18 = put("18", "Copy of line 11", line_11, "line 11")
    if inputs.commitment_period_marital_adjustment is True:
        line_19a = put(
            "19a",
            "Marital adjustment deducted for the commitment period",
            line_13,
            "line 13, contended to apply under 11 U.S.C. § 1325(b)(4) "
            "(means_test_input.commitment_period_marital_adjustment)",
        )
    else:
        line_19a = put(
            "19a",
            "Marital adjustment deducted for the commitment period",
            Decimal("0"),
            "the marital adjustment is not contended for the commitment "
            "period (means_test_input.commitment_period_marital_adjustment)",
        )
    line_19b = put(
        "19b", "Line 18 minus line 19a", line_18 - line_19a, "line 18 minus line 19a"
    )
    put("20a", "Copy of line 19b", line_19b, "line 19b")
    line_20b = put(
        "20b",
        "Current monthly income for the year (commitment period)",
        line_19b * 12,
        "line 20a x 12 — 11 U.S.C. § 1325(b)(4)",
    )
    put("20c", "Copy of line 16c", median, "line 16c")
    if line_20b < median:
        months = COMMITMENT_PERIOD_BELOW_MEDIAN
        commitment_source = (
            "line 20b is less than line 20c: the commitment period is 3 years "
            "— 11 U.S.C. § 1325(b)(4)(A)(i)"
        )
    else:
        months = COMMITMENT_PERIOD_ABOVE_MEDIAN
        commitment_source = (
            "line 20b is more than or equal to line 20c: the commitment period "
            "is 5 years — 11 U.S.C. § 1325(b)(4)(A)(ii)"
        )
    put(
        "21",
        "Applicable commitment period (months)",
        Decimal(months),
        commitment_source,
    )
    commitment = CommitmentPeriod(
        months=months,
        marital_adjustment=_money(line_19a),
        monthly_income=_money(line_19b),
        annualized_income=_money(line_20b),
        annual_median=_money(median),
        source=commitment_source,
    )

    if not comparison.above_median:
        if problems:
            raise MeansTestError(problems)
        return MeansTestResult(
            as_of=data.as_of,
            release_ids=data.release_ids,
            comparison=comparison,
            outcome="below_median",
            determined_by="median",
            lines=tuple(trace.lines),
            chapter=13,
            commitment=commitment,
        )

    # ── B122C-2 Part 1: the deductions (lines 5-38) ────────────────────────
    line_38 = _deductions(case, data, trace, form=FORM_122C2, problems=problems)

    # ── B122C-2 Part 2: disposable income under § 1325(b)(2) ──────────────
    def put_c2(line: str, label: str, amount: Decimal, source: str) -> Decimal:
        return trace.put(FORM_122C2, line, label, amount, source)

    line_39 = put_c2(
        "39",
        "Current monthly income (Form 122C-1 line 14)",
        line_14,
        "Form 122C-1 line 14",
    )
    line_40 = put_c2(
        "40",
        "Support income for dependent children",
        _entered(inputs.child_support_for_dependents),
        "entered (means_test_input.child_support_for_dependents) — "
        "11 U.S.C. § 1325(b)(2)",
    )
    line_41 = put_c2(
        "41",
        "Qualified retirement deductions",
        _entered(inputs.qualified_retirement_deductions),
        "entered (means_test_input.qualified_retirement_deductions) — "
        "11 U.S.C. §§ 541(b)(7), 362(b)(19)",
    )
    line_42 = put_c2(
        "42",
        "Total of all deductions allowed under 11 U.S.C. § 707(b)(2)(A)",
        line_38,
        "line 38",
    )
    circumstances = inputs.special_circumstances
    line_43 = Decimal("0")
    for item in circumstances:
        line_43 += _entered(item.amount)
    put_c2(
        "43",
        "Deduction for special circumstances",
        line_43,
        "entered (means_test_input.special_circumstances"
        + (
            ": " + "; ".join(item.description or item.id for item in circumstances)
            if circumstances
            else ""
        )
        + ") — 11 U.S.C. § 707(b)(2)(B)",
    )
    line_44 = put_c2(
        "44",
        "Total adjustments",
        line_40 + line_41 + line_42 + line_43,
        "lines 40 + 41 + 42 + 43",
    )
    line_45 = put_c2(
        "45",
        "Monthly disposable income under 11 U.S.C. § 1325(b)(2)",
        line_39 - line_44,
        "line 39 minus line 44",
    )

    if problems:
        raise MeansTestError(problems)

    return MeansTestResult(
        as_of=data.as_of,
        release_ids=data.release_ids,
        comparison=comparison,
        outcome="above_median",
        determined_by="median",
        lines=tuple(trace.lines),
        chapter=13,
        commitment=commitment,
        disposable_income=_money(line_45),
    )


def run_means_test(case: MeansTestCase, data: MeansTestData) -> MeansTestResult:
    """The whole determination for one case, selected by its chapter.

    Chapter 7: below-median debtors stop at the comparison (no presumption;
    B122A-2 is not filed), above-median debtors get the full line-by-line
    calculation. Chapter 13: every debtor gets B122C-1's commitment period;
    above-median debtors get B122C-2's disposable income as well. Raises
    MeansTestError, with every problem named, when required facts are
    missing or an entered figure breaks a statutory cap, and for a chapter
    that has no means-test form.
    """
    if case.chapter == 13:
        return _run_chapter_13(case, data)
    if case.chapter == 7:
        return _run_chapter_7(case, data)
    raise MeansTestError(
        [
            f"Chapter {case.chapter} has no means-test form — Forms 122A and "
            "122C exist for Chapters 7 and 13 only"
        ]
    )
