"""B106J @ 2015-12-01 (revision 12/15) — Schedule J's mapping.

Expenses are ROWS keyed by category (core/expenses.py); the table below
maps each stored category onto its printed line. A category with one box
sums its rows; the two-row pairs (vehicle installments 17a/17b, other
installments 17c/17d) take one row each in creation order, overflow being
an error. Derived, never stored: 22a (the sum of lines 4-21), 22b (the
106J-2 carry-forward), 22c, and line 23's net-income arithmetic, whose
income side is 106I line 12 through the b106i module's shared helper.

106J-2 prints the identical line set for Debtor 2's separate household, so
the household block here is shared with `b106j2.py` — one mapping, two
schedules, no drift. The amended/supplemental caption stays blank until
`case.is_amended` / `case.ch13_supplement_date` land.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

from insolvia_core.expenses import DependentBody, ExpenseBody, HouseholdBody

from ..form_fill import Option, Text
from ..form_templates import FormRelease
from .b106i import monthly_income_line_12
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)

# The single-box expense lines: stored category -> (amount field, specify
# field or None).
_SINGLE_LINES: Final[dict[str, tuple[str, str | None]]] = {
    "rent_or_home_ownership": ("line_4_rent_or_home_ownership", None),
    "real_estate_taxes": ("line_4a_real_estate_taxes", None),
    "property_insurance": ("line_4b_property_insurance", None),
    "home_maintenance": ("line_4c_home_maintenance", None),
    "homeowners_association_dues": ("line_4d_hoa_dues", None),
    "additional_mortgage_payments": ("line_5_additional_mortgage", None),
    "electricity_heat_gas": ("line_6a_electricity_heat_gas", None),
    "water_sewer_garbage": ("line_6b_water_sewer_garbage", None),
    "telephone_and_internet": ("line_6c_telecom", None),
    "other_utilities": ("line_6d_utilities_other", "line_6d_utilities_other_specify"),
    "food_and_housekeeping": ("line_7_food_housekeeping", None),
    "childcare_and_education": ("line_8_childcare_education", None),
    "clothing_and_laundry": ("line_9_clothing_laundry", None),
    "personal_care": ("line_10_personal_care", None),
    "medical_and_dental": ("line_11_medical_dental", None),
    "transportation": ("line_12_transportation", None),
    "entertainment_and_recreation": ("line_13_entertainment", None),
    "charitable_contributions": ("line_14_charitable", None),
    "life_insurance": ("line_15a_life_insurance", None),
    "health_insurance": ("line_15b_health_insurance", None),
    "vehicle_insurance": ("line_15c_vehicle_insurance", None),
    "other_insurance": ("line_15d_other_insurance", "line_15d_other_insurance_specify"),
    "taxes": ("line_16_taxes", "line_16_taxes_specify"),
    "alimony_and_support_payments": ("line_18_alimony_support", None),
    "support_of_others": ("line_19_support_others", "line_19_support_others_specify"),
    "other_property_mortgages": ("line_20a_other_mortgages", None),
    "other_property_taxes": ("line_20b_other_real_estate_taxes", None),
    "other_property_insurance": ("line_20c_other_property_insurance", None),
    "other_property_maintenance": ("line_20d_other_property_maintenance", None),
    "other_property_association_dues": ("line_20e_other_property_hoa", None),
    "other": ("line_21_other", "line_21_other_specify"),
}

# The two-row pairs: stored category -> ((amount field, specify field)...).
_PAIRED_LINES: Final[dict[str, tuple[tuple[str, str | None], ...]]] = {
    "vehicle_installment_payments": (
        ("line_17a_car_payment_vehicle1", None),
        ("line_17b_car_payment_vehicle2", None),
    ),
    "other_installment_payments": (
        ("line_17c_installment_other_1", "line_17c_installment_other_1_specify"),
        ("line_17d_installment_other_2", "line_17d_installment_other_2_specify"),
    ),
}


def household_row(case_file: CaseFile, which: str) -> tuple[str, HouseholdBody] | None:
    return next(
        (
            (id_, body)
            for id_, body in case_file.households
            if body.which_household == which
        ),
        None,
    )


def _household_expenses(case_file: CaseFile, household_id: str) -> list[ExpenseBody]:
    return [e for e in case_file.expenses if e.household_id == household_id]


def _household_dependents(
    case_file: CaseFile, household_id: str
) -> list[DependentBody]:
    return [d for d in case_file.dependents if d.household_id == household_id]


def household_expense_total(case_file: CaseFile, which: str) -> Decimal:
    """One schedule's expense lines summed — 106J line 22a for the main
    household, 106J-2 line 22 for Debtor 2's."""
    row = household_row(case_file, which)
    if row is None:
        return Decimal("0")
    return sum(
        (amount(e.amount) for e in _household_expenses(case_file, row[0])),
        Decimal("0"),
    )


def monthly_expenses_line_22c(case_file: CaseFile) -> Decimal:
    """106J line 22c — both households' expenses. Feeds 106Sum line 5."""
    return household_expense_total(case_file, "main") + household_expense_total(
        case_file, "debtor_2_separate"
    )


def household_block(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    household_id: str,
    household: HouseholdBody,
    problems: list[str],
) -> None:
    """The line set 106J and 106J-2 both print: dependents, line 3, and the
    expense lines 4-21 plus line 24."""
    dependents = _household_dependents(case_file, household_id)
    values["line_2_have_dependents"] = yes_no(
        release, "line_2_have_dependents", bool(dependents)
    )
    for index, dependent in enumerate(dependents):
        row_fill(
            release,
            values,
            "line_2_dependent_relationship",
            index,
            text_or_none(dependent.relationship),
            problems,
        )
        row_fill(
            release,
            values,
            "line_2_dependent_age",
            index,
            Text(str(dependent.age)) if dependent.age is not None else None,
            problems,
        )
        if dependent.lives_with_debtor is not None:
            row_fill(
                release,
                values,
                "line_2_dependent_lives_with_you",
                index,
                Option("yes" if dependent.lives_with_debtor else "no"),
                problems,
            )

    if household.expenses_include_others is not None:
        values["line_3_expenses_include_others"] = yes_no(
            release,
            "line_3_expenses_include_others",
            household.expenses_include_others,
        )

    expenses = _household_expenses(case_file, household_id)
    for category, (amount_field, specify_field) in _SINGLE_LINES.items():
        matching = [e for e in expenses if e.category == category]
        if not matching:
            continue
        values[amount_field] = Text(
            format_money(sum((amount(e.amount) for e in matching), Decimal("0")))
        )
        if specify_field is not None:
            texts = [e.specify_text for e in matching if e.specify_text]
            if texts:
                values[specify_field] = Text("; ".join(texts))
    for category, line_pair in _PAIRED_LINES.items():
        matching = [e for e in expenses if e.category == category]
        for index, expense in enumerate(matching):
            if index >= len(line_pair):
                problems.append(
                    f"{category}: the form prints {len(line_pair)} rows; "
                    f"row {index + 1} does not exist"
                )
                continue
            amount_field, specify_field = line_pair[index]
            if expense.amount is not None:
                values[amount_field] = Text(format_money(expense.amount))
            if specify_field is not None and expense.specify_text:
                values[specify_field] = Text(expense.specify_text)

    if household.change_expected is not None:
        values["line_24_change_expected"] = yes_no(
            release, "line_24_change_expected", household.change_expected
        )
        if household.change_expected and household.change_explanation:
            values["line_24_change_explanation"] = Text(household.change_explanation)


def project_b106j_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    joint = case_file.debtor("debtor_2") is not None
    values["line_1_joint_case"] = yes_no(release, "line_1_joint_case", joint)
    main = household_row(case_file, "main")
    if joint and main is not None and main[1].separate_household is not None:
        values["line_1_debtor2_separate_household"] = yes_no(
            release,
            "line_1_debtor2_separate_household",
            main[1].separate_household,
        )

    if main is not None:
        household_block(release, values, case_file, main[0], main[1], problems)

    # Lines 22-23 — the arithmetic the model refuses to store.
    own = household_expense_total(case_file, "main")
    other = household_expense_total(case_file, "debtor_2_separate")
    values["line_22a_total_expenses"] = Text(format_money(own))
    if household_row(case_file, "debtor_2_separate") is not None:
        values["line_22b_debtor2_expenses"] = Text(format_money(other))
    values["line_22c_monthly_expenses"] = Text(format_money(own + other))
    income = monthly_income_line_12(case_file)
    values["line_23a_combined_monthly_income"] = Text(format_money(income))
    values["line_23b_monthly_expenses"] = Text(format_money(own + other))
    values["line_23c_net_income"] = Text(format_money(income - (own + other)))

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
