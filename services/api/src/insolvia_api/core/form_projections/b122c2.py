"""B122C-2 @ 2025-04-01 (revision 04/25) — the Chapter 13 disposable-income
calculation's mapping (issue #365).

The engine (core/means_test.py, its Chapter 13 branch) computes; this
module only lands the result: every amount box takes the engine's line of
the same number on form 122C-2 — lines 5-38 are B122A-2's lines under
different widget names (the form's own "line numbers 1-4 are not used"),
lines 39-45 the § 1325(b)(2) subtractions — the entered rows (line 33d's
other secured debts, line 43's special circumstances) print from the
means_test_input record the engine read, and the checkboxes follow the
answers. The engine's refusals become projection errors verbatim, so packet
assembly reports them in the gate like any other unlandable fact.

This form is only FILED by an above-median debtor (B122C-1 line 17b);
`files_b122c2` makes that call for packet assembly's `packet_form_series`,
by running the engine rather than re-deriving the comparison — the one
place the marital adjustment is subtracted before the median is the
engine, and this module adds no arithmetic. Projecting a below-median case
is therefore an error here, not a blank form.

Deliberate blanks, each until its owner lands: line 25's actual-spend
question, the per-creditor rows of lines 9b/13b/13e and 34 (only the totals
are modelled — with one exception the spec's `vehicle_2_loan_rows` note
explains: the PDF wires line 33c's copy box to 13e's first row, so 13e's
total lands there too), Part 3's change-in-income narrative (an attorney's
statement, not a stored fact), the amended caption, wet signatures, and the
court's case number.
"""

from __future__ import annotations

from datetime import date
from typing import Final

from ..form_fill import Option, Text
from ..form_templates import FormRelease
from ..means_test import (
    FORM_122C2,
    MeansTestCase,
    MeansTestData,
    MeansTestError,
    MeansTestResult,
    resolve_means_test_data,
    run_means_test,
)
from .b122a1 import fill_caption, fill_signature_dates, means_test_as_of
from .b122a2 import build_means_test_case
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    format_money,
    row_fill,
)

# Engine line (form 122C-2) -> the spec field it lands on. Lines whose
# printed box is a shared widget (33a/33b = 9b's and 13b's totals; 42 =
# line 38's box) or a count box landed separately (5, 7b, 7e) are absent;
# 33c is landed by `_vehicle_2_copy` because of the PDF's defect.
_LINE_FIELDS: Final = {
    "6": "food_clothing_allowance",
    "7a": "oop_health_under_65_rate",
    "7c": "oop_health_under_65_subtotal",
    "7d": "oop_health_over_65_rate",
    "7f": "oop_health_over_65_subtotal",
    "7": "oop_health_total",
    "8": "housing_operating",
    "9a": "housing_mortgage_rent_standard",
    "9b": "home_debt_total",
    "9c": "net_mortgage_rent",
    "10": "housing_adjustment",
    "12": "vehicle_operating",
    "13a": "vehicle_1_ownership_standard",
    "13b": "vehicle_1_loan_total",
    "13c": "vehicle_1_net",
    "13d": "vehicle_2_ownership_standard",
    "13e": "vehicle_2_loan_total",
    "13f": "vehicle_2_net",
    "14": "public_transportation",
    "15": "additional_public_transportation",
    "16": "taxes",
    "17": "involuntary_deductions",
    "18": "term_life_insurance",
    "19": "court_ordered_payments",
    "20": "education_for_employment",
    "21": "childcare",
    "22": "additional_health_care",
    "23": "optional_telecom",
    "24": "irs_allowances_total",
    "25": "health_insurance_total",
    "26": "family_care",
    "27": "family_violence",
    "28": "home_energy",
    "29": "education_under_18",
    "30": "additional_food_clothing",
    "31": "charitable_contributions",
    "32": "additional_deductions_total",
    "33e": "secured_debt_total",
    "34": "cure_monthly_total",
    "35": "priority_claims_monthly",
    "36": "ch13_admin_expense",
    "37": "debt_payment_total",
    "38": "total_deductions",
    "39": "current_monthly_income",
    "40": "child_support_income",
    "41": "qualified_retirement_deductions",
    "43": "special_circumstances_total",
    "44": "total_adjustments",
    "45": "monthly_disposable_income",
}

# Engine lines that are count boxes, copies of a shared widget, or sums the
# form prints only as rows.
_UNPRINTED_LINES: Final = frozenset(
    {"5", "7b", "7e", "11", "13", "33a", "33b", "33c", "33d", "42"}
)


def _as_of(case_file: CaseFile) -> date:
    return means_test_as_of(case_file)[0]


def _run(
    case_file: CaseFile,
) -> tuple[MeansTestCase, MeansTestResult, MeansTestData]:
    test = build_means_test_case(case_file)
    if test.cmi.problems:
        raise FormProjectionError(list(test.cmi.problems))
    try:
        data = resolve_means_test_data(_as_of(case_file))
        result = run_means_test(test, data)
    except (MeansTestError, LookupError) as error:
        raised = (
            list(error.problems) if isinstance(error, MeansTestError) else [str(error)]
        )
        raise FormProjectionError(raised) from error
    return test, result, data


def files_b122c2(case_file: CaseFile) -> bool:
    """Whether this Chapter 13 case FILES Form 122C-2 — B122C-1 line 17's
    call, made for packet assembly's `packet_form_series`.

    False only when the engine determinately answers below the median (line
    17a: do not fill out or file 122C-2). Above the median, or not yet
    determinable (household or state missing, no dataset release for the
    date, a CMI record that cannot enter the calculation), the form stays
    in the set so ITS projection can name what is missing through the gate
    instead of the packet silently shipping without the calculation.
    """
    if case_file.case.chapter != 13:
        return False
    try:
        _, result, _ = _run(case_file)
    except FormProjectionError:
        return True
    return result.outcome != "below_median"


def _entered_rows(
    release: FormRelease, values: FieldValues, case_file: CaseFile, problems: list[str]
) -> None:
    """The itemized entered lists: line 33d's other secured debts and line
    43's special circumstances, three printed rows each, plus line 10's
    explanation."""
    if not case_file.means_test_inputs:
        return
    inputs = case_file.means_test_inputs[0]
    for index, payment in enumerate(inputs.other_secured_payments):
        row_fill(
            release,
            values,
            "other_secured_creditor",
            index,
            Text(payment.creditor_name) if payment.creditor_name else None,
            problems,
        )
        row_fill(
            release,
            values,
            "other_secured_property",
            index,
            Text(payment.property_description)
            if payment.property_description
            else None,
            problems,
        )
        row_fill(
            release,
            values,
            "other_secured_monthly",
            index,
            Text(format_money(payment.monthly_payment))
            if payment.monthly_payment
            else None,
            problems,
        )
    for index, item in enumerate(inputs.special_circumstances):
        row_fill(
            release,
            values,
            "special_circumstances_description",
            index,
            Text(item.description) if item.description else None,
            problems,
        )
        row_fill(
            release,
            values,
            "special_circumstances_amount",
            index,
            Text(format_money(item.amount)) if item.amount else None,
            problems,
        )
    if inputs.housing_adjustment_explanation:
        row_fill(
            release,
            values,
            "housing_adjustment_explanation",
            0,
            Text(inputs.housing_adjustment_explanation),
            problems,
        )
    # Line 25's three component boxes print the entered figures the engine
    # summed into its line 25; a blank component stays blank.
    for field_id, entered in (
        ("health_insurance", inputs.health_insurance),
        ("disability_insurance", inputs.disability_insurance),
        ("health_savings_account", inputs.health_savings_account),
    ):
        if entered is not None:
            values[field_id] = Text(format_money(entered))


def _checkboxes(values: FieldValues, test: MeansTestCase) -> None:
    inputs = test.inputs
    if inputs.vehicle_count is not None:
        # The export states are the line numbers each answer sends you to.
        values["vehicle_count"] = Option(
            {0: "14", 1: "12", 2: "On"}[inputs.vehicle_count]
        )
    values["cure_claimed"] = Option(
        "yes" if inputs.priority_cure_total is not None else "no"
    )
    values["priority_claims_owed"] = Option(
        "yes" if amount(test.priority_debt_total) > 0 else "no"
    )


def _vehicle_2_copy(
    release: FormRelease,
    values: FieldValues,
    by_line: dict[str, str],
    problems: list[str],
) -> None:
    """Line 33c's "Copy line 13e here" box is, in the official PDF, the
    widget of 13e's FIRST creditor row rather than its total (the spec's
    note). Landing 13e's total on that row is what makes 33c print the
    figure 33e adds up; with one vehicle creditor the row reads right, and
    the rows are not otherwise modelled."""
    total = by_line.get("13e")
    if total is None or amount(total) == 0:
        return
    row_fill(
        release, values, "vehicle_2_loan_rows", 0, Text(format_money(total)), problems
    )


def project_b122c2_0425(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for form/b122c2@2025-04-01, from one case's facts."""
    if case_file.case.chapter != 13:
        raise FormProjectionError(
            [
                f"Form 122C-2 is the Chapter 13 disposable-income calculation "
                f"— this is a Chapter {case_file.case.chapter} case"
            ]
        )
    problems: list[str] = []
    test, result, data = _run(case_file)
    if result.outcome == "below_median":
        raise FormProjectionError(
            [
                "the debtor's current monthly income is at or below the "
                "applicable median — Form 122C-2 is not filed (B122C-1 line 17a)"
            ]
        )

    values: FieldValues = {}
    fill_caption(values, case_file)

    by_line = {
        line.line: line.amount for line in result.lines if line.form == FORM_122C2
    }
    for number, printed in by_line.items():
        field_id = _LINE_FIELDS.get(number)
        if field_id is None:
            if number not in _UNPRINTED_LINES:  # pragma: no cover - map drift
                raise FormProjectionError(
                    [f"engine line {number} has no field mapping"]
                )
            continue
        values[field_id] = Text(format_money(printed))

    # The count boxes print as plain integers, not money.
    values["household_size"] = Text(by_line["5"].split(".")[0])
    values["oop_health_under_65_count"] = Text(by_line["7b"].split(".")[0])
    values["oop_health_over_65_count"] = Text(by_line["7e"].split(".")[0])

    # Line 35's total-claims box is the engine's input, not one of its
    # lines — the engine emits only the divided figure.
    if amount(test.priority_debt_total) > 0:
        values["priority_claims_total"] = Text(format_money(test.priority_debt_total))

    # Line 36's multiplier box: always, on the Chapter 13 form — the engine
    # has already refused a district the table does not carry.
    values["ch13_multiplier"] = Text(str(data.ch13.multiplier_for(test.district)))
    if test.inputs.ch13_projected_plan_payment is not None:
        values["ch13_plan_payment"] = Text(
            format_money(test.inputs.ch13_projected_plan_payment)
        )

    _vehicle_2_copy(release, values, by_line, problems)
    _entered_rows(release, values, case_file, problems)
    _checkboxes(values, test)
    fill_signature_dates(values, case_file)

    if problems:
        raise FormProjectionError(problems)
    return values
