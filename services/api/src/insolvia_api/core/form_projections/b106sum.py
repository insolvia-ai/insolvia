"""B106Sum @ 2015-12-01 (revision 12/15) — the summary's mapping.

Every line is a copy or a sum of another schedule, so this module stores
nothing and asks the other modules' shared helpers instead — the same
functions their own projections print from, which is what keeps the
summary incapable of disagreeing with the schedules it summarises. The one
exception is line 8's current monthly income, which is a cross-form copy
from the means-test forms (122A-1/122B/122C-1): that milestone owns the
figure, so the box stays blank until it lands.
"""

from __future__ import annotations

from ..form_fill import Text
from ..form_templates import FormRelease
from .b106ab import personal_property_total, real_estate_total
from .b106d import secured_total
from .b106ef import (
    nonpriority_type_total,
    nonpriority_unsecured_total,
    priority_type_total,
    priority_unsecured_total,
)
from .b106i import monthly_income_line_12
from .b106j import monthly_expenses_line_22c
from .shared import CaseFile, FieldValues, format_money, full_name, yes_no


def project_b106sum_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    # Line 1 — Schedule A/B's totals.
    real_estate = real_estate_total(case_file)
    personal = personal_property_total(case_file)
    values["line_1a_total_real_estate"] = Text(format_money(real_estate))
    values["line_1b_total_personal_property"] = Text(format_money(personal))
    values["line_1c_total_property"] = Text(format_money(real_estate + personal))

    # Lines 2-3 — the liabilities side.
    secured = secured_total(case_file)
    priority = priority_unsecured_total(case_file)
    nonpriority = nonpriority_unsecured_total(case_file)
    values["line_2_secured_claims_total"] = Text(format_money(secured))
    values["line_3a_priority_unsecured_total"] = Text(format_money(priority))
    values["line_3b_nonpriority_unsecured_total"] = Text(format_money(nonpriority))
    values["line_3_total_liabilities"] = Text(
        format_money(secured + priority + nonpriority)
    )

    # Lines 4-5 — Schedule I's line 12 and Schedule J's line 22c.
    values["line_4_combined_monthly_income"] = Text(
        format_money(monthly_income_line_12(case_file))
    )
    values["line_5_monthly_expenses"] = Text(
        format_money(monthly_expenses_line_22c(case_file))
    )

    # Part 3 — the administrative and statistical questions.
    values["line_6_filing_under_7_11_13"] = yes_no(
        release, "line_6_filing_under_7_11_13", case_file.case.chapter in (7, 11, 13)
    )
    petition = case_file.petition
    consumer = petition is not None and petition.debt_character == "consumer"
    if petition is not None and petition.debt_character is not None:
        values["line_7_kind_of_debt"] = yes_no(release, "line_7_kind_of_debt", consumer)
    if consumer:
        # Line 8 stays blank — the means-test milestone owns the figure.
        domestic = priority_type_total(case_file, "domestic_support")
        taxes = priority_type_total(case_file, "tax_and_government")
        intoxicated = priority_type_total(
            case_file, "death_or_injury_while_intoxicated"
        )
        student = nonpriority_type_total(case_file, "student_loan")
        divorce = nonpriority_type_total(case_file, "separation_or_divorce")
        pension = nonpriority_type_total(case_file, "pension_or_profit_sharing")
        values["line_9a_domestic_support"] = Text(format_money(domestic))
        values["line_9b_taxes_government"] = Text(format_money(taxes))
        values["line_9c_intoxicated_injury"] = Text(format_money(intoxicated))
        values["line_9d_student_loans"] = Text(format_money(student))
        values["line_9e_separation_divorce"] = Text(format_money(divorce))
        values["line_9f_pension_profit_sharing"] = Text(format_money(pension))
        values["line_9g_total"] = Text(
            format_money(domestic + taxes + intoxicated + student + divorce + pension)
        )

    return values
