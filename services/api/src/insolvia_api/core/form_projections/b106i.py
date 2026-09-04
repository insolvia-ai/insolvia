"""B106I @ 2015-12-01 (revision 12/15) — Schedule I's mapping.

Two debtor columns (the second may belong to a non-filing spouse), and the
derived arithmetic the model refuses to store: line 4 = 2 + 3, line 6 sums
the deductions, 7 = 4 - 6, 9 sums the other income, 10 = 7 + 9, and line 12
adds the household contributions the debtor-1 summary carries.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from decimal import Decimal
from typing import Final

from insolvia_core.debtors import Debtor
from insolvia_core.income import EmploymentBody, IncomeSummaryBody

from ..form_fill import FieldFill, Option, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    amount,
    format_date,
    format_money,
    full_name,
    yes_no,
)

_EMPLOYMENT_EXPORTS: Final = {"employed": "employed", "not_employed": "unemployed"}

# The 5a-5h deduction lines and 8a-8h other-income lines, in printed order:
# (line key, IncomeSummaryBody attribute).
_DEDUCTION_LINES: Final = (
    ("5a_tax_medicare_ss", "deduction_tax"),
    ("5b_mandatory_retirement", "deduction_mandatory_retirement"),
    ("5c_voluntary_retirement", "deduction_voluntary_retirement"),
    ("5d_retirement_loan_repayment", "deduction_retirement_loan_repayment"),
    ("5e_insurance", "deduction_insurance"),
    ("5f_domestic_support", "deduction_domestic_support"),
    ("5g_union_dues", "deduction_union_dues"),
    ("5h_other_deductions", "deduction_other"),
)

_OTHER_INCOME_LINES: Final = (
    ("8a_rental_business_net", "business_net_income"),
    ("8b_interest_dividends", "interest_and_dividends"),
    ("8c_family_support", "family_support"),
    ("8d_unemployment", "unemployment"),
    ("8e_social_security", "social_security"),
    ("8f_government_assistance", "other_government_assistance"),
    ("8g_pension_retirement", "pension_or_retirement"),
    ("8h_other_income", "other_monthly_income"),
)


@dataclass(frozen=True)
class _Column:
    """One 106I debtor column: the column digit ('1'/'2') and its facts."""

    digit: str
    employment: EmploymentBody | None
    summary: IncomeSummaryBody | None


def _column_widget(release: FormRelease, field_id: str, digit: str) -> str:
    """The PDF field carrying this debtor column — matched by the 'Debtor 1'
    / 'Debtor 2' marker in the name (case varies, and line 4's copy row
    appends an 'a'), never by position."""
    pattern = re.compile(rf"[Dd]ebtor {digit}a?$")
    spec = release.field(field_id)
    matches = [n for n in spec.pdf_names if pattern.search(n)]
    if len(matches) != 1:  # pragma: no cover - the spec's columns are fixed
        raise KeyError(f"{field_id} has no single debtor-{digit} column")
    return matches[0]


def _put_column(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    digit: str,
    fill: FieldFill,
) -> None:
    spec = release.field(field_id)
    if len(spec.pdf_names) == 1:
        values[field_id] = fill
        return
    existing = values.setdefault(field_id, {})
    assert isinstance(existing, dict)  # single-widget path returned above
    existing[_column_widget(release, field_id, digit)] = fill


def monthly_income_line_12(case_file: CaseFile) -> Decimal:
    """106I line 12 — combined monthly income plus household contributions,
    exactly as `project_b106i_1215` derives it. Shared because 106Sum line 4
    and 106J line 23a copy this figure and must not drift from it."""
    total = Decimal("0")
    summary1: IncomeSummaryBody | None = None
    for debtor in case_file.debtors:
        summary = next(
            (s for s in case_file.income_summaries if s.debtor_id == debtor.id), None
        )
        if summary is None:
            continue
        if debtor.filing_role == "debtor_1":
            summary1 = summary
        gross = amount(summary.wages) + amount(summary.overtime)
        deductions = sum(
            (amount(getattr(summary, attr)) for _, attr in _DEDUCTION_LINES),
            Decimal("0"),
        )
        other = sum(
            (amount(getattr(summary, attr)) for _, attr in _OTHER_INCOME_LINES),
            Decimal("0"),
        )
        total += gross - deductions + other
    if summary1 is not None:
        total += amount(summary1.household_contributions)
    return total


def project_b106i_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}
    case = case_file.case
    debtor1 = case_file.debtor("debtor_1")
    # 106I's second column belongs to debtor 2 OR a non-filing spouse.
    debtor2 = case_file.debtor("debtor_2", "non_filing_spouse")

    values["caption.district"] = Text(case.district)
    if debtor1 is not None and (name1 := full_name(debtor1.name)):
        values["caption.debtor1_name"] = Text(name1)
    if debtor2 is not None and (name2 := full_name(debtor2.name)):
        values["caption.debtor2_name"] = Text(name2)

    def column(debtor: Debtor | None, digit: str) -> _Column:
        if debtor is None:
            return _Column(digit=digit, employment=None, summary=None)
        return _Column(
            digit=digit,
            employment=next(
                (e for e in case_file.employments if e.debtor_id == debtor.id), None
            ),
            summary=next(
                (s for s in case_file.income_summaries if s.debtor_id == debtor.id),
                None,
            ),
        )

    columns = (column(debtor1, "1"), column(debtor2, "2"))
    monthly_totals: list[Decimal] = []

    for col in columns:
        employment = col.employment
        if employment is not None:
            if employment.status is not None:
                _put_column(
                    release,
                    values,
                    "employment_status",
                    col.digit,
                    Option(_EMPLOYMENT_EXPORTS[employment.status]),
                )
            for field_id, value in (
                ("occupation", employment.occupation),
                ("employer_name", employment.employer_name),
            ):
                if value:
                    _put_column(release, values, field_id, col.digit, Text(value))
            if employment.employed_since:
                # The form asks "how long employed there?"; the stored fact is
                # the start date, printed as such rather than derived into a
                # duration that would silently age.
                _put_column(
                    release,
                    values,
                    "how_long_employed",
                    col.digit,
                    Text(f"Since {format_date(employment.employed_since)}"),
                )
            address = employment.employer_address
            spec = release.field("employer_address")
            for part, box in (
                (address.line1, f"Employers Street1 Debtor {col.digit}"),
                (address.line2, f"Employers Street2 Debtor {col.digit}"),
                (address.city, f"Employers City Debtor {col.digit}"),
                (address.state, f"Employers State Debtor {col.digit}"),
                # The ZIP boxes alone spell 'debtor' lowercase in the PDF.
                (address.postal_code, f"Employers Zip debtor {col.digit}"),
            ):
                if part and box in spec.pdf_names:
                    entry = values.setdefault("employer_address", {})
                    assert isinstance(entry, dict)
                    entry[box] = Text(part)

        summary = col.summary
        if summary is None:
            continue

        def put_money(field_id: str, value: Decimal, digit: str = col.digit) -> None:
            _put_column(release, values, field_id, digit, Text(format_money(value)))

        # Lines 2-3 as stored; line 4 = 2 + 3, twice (the page-2 copy row).
        if summary.wages is not None:
            put_money("line_2_gross_wages", amount(summary.wages))
        if summary.overtime is not None:
            put_money("line_3_overtime", amount(summary.overtime))
        gross = amount(summary.wages) + amount(summary.overtime)
        put_money("line_4_gross_income", gross)
        put_money("line_4_copy", gross)

        # Line 5's eight deduction lines; line 6 sums them; 7 = 4 - 6.
        deductions = Decimal("0")
        for line, attr in _DEDUCTION_LINES:
            stored = getattr(summary, attr)
            if stored is not None:
                put_money(f"line_{line}", amount(stored))
            deductions += amount(stored)
        put_money("line_6_total_deductions", deductions)
        take_home = gross - deductions
        put_money("line_7_take_home_pay", take_home)

        # Line 8's other income; 9 sums it; 10 = 7 + 9.
        other_income = Decimal("0")
        for line, attr in _OTHER_INCOME_LINES:
            stored = getattr(summary, attr)
            if stored is not None:
                put_money(f"line_{line}", amount(stored))
            other_income += amount(stored)
        put_money("line_9_total_other_income", other_income)
        monthly = take_home + other_income
        put_money("line_10_monthly_income", monthly)
        monthly_totals.append(monthly)

        if summary.deduction_other_specify:
            values["line_5h_other_deductions_specify"] = Text(
                summary.deduction_other_specify
            )
        if summary.other_government_assistance_specify:
            values["line_8f_government_assistance_specify"] = Text(
                summary.other_government_assistance_specify
            )
        if summary.other_monthly_income_specify:
            values["line_8h_other_income_specify"] = Text(
                summary.other_monthly_income_specify
            )

    if monthly_totals:
        combined = sum(monthly_totals, Decimal("0"))
        values["line_10_combined"] = Text(format_money(combined))

        # Line 11 is one value for the household, carried on the debtor-1
        # summary (case-data-model.md); line 12 = 10 + 11.
        summary1 = columns[0].summary
        contributions = amount(summary1.household_contributions if summary1 else None)
        if summary1 is not None and summary1.household_contributions is not None:
            values["line_11_household_contributions"] = Text(
                format_money(contributions)
            )
        values["line_12_combined_monthly_income"] = Text(
            format_money(combined + contributions)
        )

        # Line 13 — the change question, on the debtor-1 summary.
        if summary1 is not None and summary1.change_expected is not None:
            values["line_13_change_expected"] = yes_no(
                release, "line_13_change_expected", summary1.change_expected
            )
            if summary1.change_expected and summary1.change_explanation:
                values["line_13_change_explanation"] = Text(summary1.change_explanation)

    return values
