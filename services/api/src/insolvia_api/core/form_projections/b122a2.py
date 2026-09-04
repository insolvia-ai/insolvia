"""B122A-2 @ 2025-04-01 (revision 04/25) — the means-test calculation's
mapping.

The engine (core/means_test.py) computes; this module only lands the
result: every amount box takes the engine's line of the same number, the
entered rows (line 3's marital adjustments, line 33d's other secured
debts) print from the means_test_input record the engine read, and the
checkboxes follow the determination. The engine's refusals — a missing
household composition, an unknown county, a figure past a statutory cap —
become projection errors verbatim, so packet assembly reports them in the
gate like any other unlandable fact.

This form is only FILED by an above-median debtor (B122A-1 line 14b);
packet assembly's `packet_form_series` makes that call. Projecting a
below-median case is therefore an error here, not a blank form — an
all-blank 122A-2 in front of a clerk is a question, and a filled one would
contradict the 122A-1 that says it is not required.

Deliberate blanks, each until its owner lands: line 25's actual-spend
question (the entered figures ARE the claimed expenses), lines 9b/13b/13e's
per-creditor itemization rows (only the totals are modelled), line 34's
per-creditor cure rows (ditto), line 43's special circumstances (a legal
argument, not a stored fact), the amended caption, wet signatures, and the
court's case number. Three shared widgets (line 4 = 39a, 9b's total = 33a,
13b/13e's totals = 33b/33c) fill twice from one value by the PDF's own
design.
"""

from __future__ import annotations

from datetime import date
from decimal import Decimal
from typing import Final

from insolvia_core.means_test_inputs import MeansTestInputBody

from ..form_fill import Option, Text
from ..form_templates import FormRelease
from ..means_test import (
    MeansTestCase,
    MeansTestError,
    MeansTestResult,
    resolve_means_test_data,
    run_means_test,
)
from ..ust_data import median_income_table
from .b122a1 import compute_cmi, household_size
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    format_date,
    format_money,
    full_name,
    row_fill,
)

# Engine line number -> the spec field it lands on. Lines the engine emits
# that have no widget (39a reuses line 4's; 39b has none) are absent here.
_LINE_FIELDS: Final = {
    "1": "total_cmi",
    "3": "marital_adjustment_total",
    "4": "adjusted_cmi",
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
    "39c": "monthly_disposable_income",
    "39d": "disposable_income_60_months",
    "41a": "nonpriority_unsecured_total",
    "41b": "nonpriority_unsecured_quarter",
}

# Engine lines that are checkbox counts or copies with no box of their own.
_UNPRINTED_LINES: Final = frozenset(
    {
        "5",
        "7b",
        "7e",
        "11",
        "13",
        "25",
        # 33d's engine line is the rows' sum; the form prints only the rows
        # (33e carries the total) — _entered_rows lands them.
        "33a",
        "33b",
        "33c",
        "33d",
        "39a",
        "39b",
        "40",
        "42",
    }
)


def _as_of(case_file: CaseFile) -> date:
    return date.fromisoformat(case_file.case.created_at[:10])


def files_b122a2(case_file: CaseFile) -> bool:
    """Whether this case FILES Form 122A-2 — B122A-1 line 14's call, made
    for packet assembly's `packet_form_series`.

    False only when the median comparison determinately answers below the
    median (line 14a: do not fill out or file 122A-2). Above the median, or
    not yet determinable (household composition or state missing, no median
    release for the date), the form stays in the set so ITS projection can
    name what is missing through the gate instead of the packet silently
    shipping without the calculation.
    """
    size = household_size(case_file)
    debtor1 = case_file.debtor("debtor_1")
    state = (
        debtor1.residence_address.state
        if debtor1 is not None and debtor1.residence_address.state
        else None
    )
    if size is None or state is None:
        return True
    try:
        _, table = median_income_table(_as_of(case_file))
        median = table.annual_median(state, size)
    except (KeyError, ValueError, LookupError):
        return True
    cmi = compute_cmi(case_file)
    return Decimal(cmi.combined_monthly_total) * 12 > median


def build_means_test_case(case_file: CaseFile) -> MeansTestCase:
    """The engine's inputs, derived from case records (issue #101's split):
    CMI from the dated income history, the debt totals from the claims, the
    under-18 count from the dependents, the rest from means_test_input."""
    debtor1 = case_file.debtor("debtor_1")
    address = debtor1.residence_address if debtor1 is not None else None
    priority = sum(
        (
            amount(body.priority_amount)
            for _, body in case_file.claims
            if body.claim_class == "priority_unsecured"
        ),
        start=amount(None),
    )
    nonpriority = sum(
        (
            amount(body.amount)
            for _, body in case_file.claims
            if body.claim_class == "nonpriority_unsecured"
        ),
        start=amount(None),
    ) + sum(
        (
            amount(body.nonpriority_amount)
            for _, body in case_file.claims
            if body.claim_class == "priority_unsecured"
        ),
        start=amount(None),
    )
    inputs = (
        case_file.means_test_inputs[0]
        if case_file.means_test_inputs
        else MeansTestInputBody()
    )
    return MeansTestCase(
        state=(address.state if address is not None and address.state else ""),
        county=(address.county if address is not None and address.county else ""),
        district=case_file.case.district,
        cmi=compute_cmi(case_file),
        inputs=inputs,
        priority_debt_total=f"{priority:f}",
        nonpriority_unsecured_total=f"{nonpriority:f}",
        children_under_18=sum(
            1
            for dependent in case_file.dependents
            if dependent.age is not None and dependent.age < 18
        ),
    )


def _entered_rows(
    release: FormRelease, values: FieldValues, case_file: CaseFile, problems: list[str]
) -> None:
    """The itemized entered lists: line 3's marital adjustments and line
    33d's other secured debts, each with three printed rows."""
    if not case_file.means_test_inputs:
        return
    inputs = case_file.means_test_inputs[0]
    for index, item in enumerate(inputs.marital_adjustments):
        row_fill(
            release,
            values,
            "marital_adjustment_purpose",
            index,
            Text(item.description) if item.description else None,
            problems,
        )
        row_fill(
            release,
            values,
            "marital_adjustment_amount",
            index,
            Text(format_money(item.amount)) if item.amount else None,
            problems,
        )
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
    if inputs.housing_adjustment_explanation:
        row_fill(
            release,
            values,
            "housing_adjustment_explanation",
            0,
            Text(inputs.housing_adjustment_explanation),
            problems,
        )


def _checkboxes(
    values: FieldValues,
    case_file: CaseFile,
    test: MeansTestCase,
    result: MeansTestResult,
) -> None:
    inputs = test.inputs
    has_column_b = any(column.column == "B" for column in test.cmi.columns)
    values["column_b_filled"] = Option("Yes" if has_column_b else "No")
    if has_column_b:
        # The nested question: a debtor_2 record is a spouse filing with
        # you; a Column B without one is the non-filing spouse, whose
        # marital-adjustment question then applies.
        spouse_filing = case_file.debtor("debtor_2") is not None
        values["spouse_filing"] = Option("Yes" if spouse_filing else "No")
        if not spouse_filing:
            values["marital_adjustment_claimed"] = Option(
                "On" if inputs.marital_adjustments else "No"
            )
    if inputs.vehicle_count is not None:
        values["vehicle_count"] = Option(
            {0: "0", 1: "1", 2: "On"}[inputs.vehicle_count]
        )
    values["cure_claimed"] = Option(
        "Yes" if inputs.priority_cure_total is not None else "No"
    )
    values["priority_claims_owed"] = Option(
        "Yes" if amount(test.priority_debt_total) > 0 else "No"
    )
    if inputs.ch13_eligible is not None:
        values["ch13_eligible"] = Option("Yes" if inputs.ch13_eligible else "No")

    values["presumption_thresholds"] = Option(
        {
            "threshold_floor": "1",
            "threshold_ceiling": "2",
            "unsecured_ratio": "On",
        }[result.determined_by]
    )
    if result.determined_by == "unsecured_ratio":
        values["unsecured_ratio_determination"] = Option(
            "Yes" if result.outcome == "presumption_of_abuse" else "No"
        )
    values["caption.presumption_box"] = Option(
        "Presumption of abuse applies"
        if result.outcome == "presumption_of_abuse"
        else "No Abuse"
    )


def _caption_and_signatures(values: FieldValues, case_file: CaseFile) -> None:
    debtor1 = case_file.debtor("debtor_1")
    debtor2 = case_file.debtor("debtor_2")
    if debtor1 is not None and (name := full_name(debtor1.name)):
        values["caption.debtor1_name"] = Text(name)
    if debtor2 is not None and (name := full_name(debtor2.name)):
        values["caption.debtor2_name"] = Text(name)
    if case_file.case.district:
        values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "debtor1_signature_date"),
        ("debtor_2", "debtor2_signature_date"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at is not None:
            values[field_id] = Text(format_date(debtor.signed_at[:10]))


def project_b122a2_0425(release: FormRelease, case_file: CaseFile) -> FieldValues:
    """The values for form/b122a2@2025-04-01, from one case's facts."""
    problems: list[str] = []
    test = build_means_test_case(case_file)
    problems.extend(test.cmi.problems)
    if problems:
        raise FormProjectionError(problems)

    try:
        data = resolve_means_test_data(_as_of(case_file))
        result = run_means_test(test, data)
    except (MeansTestError, LookupError) as error:
        raised = (
            list(error.problems) if isinstance(error, MeansTestError) else [str(error)]
        )
        raise FormProjectionError(raised) from error
    if result.outcome == "below_median":
        raise FormProjectionError(
            [
                "the debtor's annualized income is below the applicable "
                "median — Form 122A-2 is not filed (B122A-1 line 14a)"
            ]
        )

    values: FieldValues = {}
    _caption_and_signatures(values, case_file)

    by_line = {line.line: line for line in result.lines}
    for number, line in by_line.items():
        field_id = _LINE_FIELDS.get(number)
        if field_id is None:
            if number not in _UNPRINTED_LINES:  # pragma: no cover - map drift
                raise FormProjectionError(
                    [f"engine line {number} has no field mapping"]
                )
            continue
        values[field_id] = Text(format_money(line.amount))

    # The count boxes print as plain integers, not money.
    values["household_size"] = Text(by_line["5"].amount.split(".")[0])
    values["oop_health_under_65_count"] = Text(by_line["7b"].amount.split(".")[0])
    values["oop_health_over_65_count"] = Text(by_line["7e"].amount.split(".")[0])

    # Line 35's total-claims box is the engine's input, not one of its
    # lines — the engine emits only the divided figure.
    if amount(test.priority_debt_total) > 0:
        values["priority_claims_total"] = Text(format_money(test.priority_debt_total))

    # Line 36's multiplier box: the district's fraction, only when Chapter 13
    # eligibility put a real multiplication on the form.
    if test.inputs.ch13_eligible:
        values["ch13_multiplier"] = Text(str(data.ch13.multiplier_for(test.district)))

    _entered_rows(release, values, case_file, problems)
    _checkboxes(values, case_file, test, result)

    if problems:
        raise FormProjectionError(problems)
    return values
