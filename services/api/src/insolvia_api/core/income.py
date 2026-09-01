"""Employment and the 106I income summary.

This is the one place the data model deliberately refuses to mirror the form
(docs/reference/case-data-model.md, "Income: 106I is not the income model"):
the model stores dated pay-period history for the means test and treats 106I
as a projection. The `pay_period_record` half belongs to the means-test
milestone; what THIS module owns is the two record types 106I itself prints —
where the debtor works (Part 1) and the monthly estimate lines (Part 2).

`income_summary` is ENTERED AND CONFIRMED, NOT COMPUTED. Pay-period records
inform it — the UI should offer the arithmetic — but 106I's question is an
estimate of what income *will be*, which a run of past pay stubs cannot answer
on its own. Treating it as derived would put an unreviewed number on a signed
form. The derived lines (gross income, total deductions, take-home pay, total
other income, combined monthly income) are arithmetic and never stored.

`debtor_id` names a debtor record ("one per debtor column"), unchecked here
for the usual progressive-intake reason. One-per-debtor is likewise not a key
constraint: an income summary can be typed before its debtor record is saved,
so the store cannot enforce a uniqueness it cannot resolve — the forms
engine's completeness gate is where a duplicate column becomes an error.

Line 11 (household contributions) is one value for the household rather than
one per debtor column; the model carries it on the debtor-1 summary and the
forms engine renders it in its single box.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import (
    Address,
    boolean,
    choice,
    form_date,
    money,
    narrative,
    parse_address,
    text,
)

# 106I Part 1's employment box.
EMPLOYMENT_STATUSES: Final = ("employed", "not_employed")


@dataclass(frozen=True)
class EmploymentBody:
    debtor_id: str | None = None
    status: str | None = None
    occupation: str | None = None
    employer_name: str | None = None
    employer_address: Address = field(default_factory=Address)
    employed_since: str | None = None


def parse_employment(payload: Mapping[str, object]) -> EmploymentBody:
    errors: dict[str, str] = {}
    body = EmploymentBody(
        debtor_id=text(payload.get("debtor_id"), "debtor_id", errors, limit=64),
        status=choice(payload.get("status"), EMPLOYMENT_STATUSES, "status", errors),
        occupation=text(payload.get("occupation"), "occupation", errors),
        employer_name=text(payload.get("employer_name"), "employer_name", errors),
        employer_address=parse_address(
            payload.get("employer_address"), "employer_address", errors
        ),
        employed_since=form_date(
            payload.get("employed_since"), "employed_since", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


EMPLOYMENT: EntityKind[EmploymentBody] = EntityKind(
    name="employment",
    collection="employments",
    sk_prefix="EMPLOYMENT",
    parse_body=parse_employment,
)


@dataclass(frozen=True)
class IncomeSummaryBody:
    """106I Part 2, one column. Every money member is a monthly estimate as of
    the filing date — the form's own framing — named for the line's subject
    rather than its number so a renumbering does not shift meanings."""

    debtor_id: str | None = None
    # Lines 2-3.
    wages: str | None = None
    overtime: str | None = None
    # Line 5's eight deduction lines, named after 106I's own categories so a
    # pay stub's itemisation maps without a lossy translation.
    deduction_tax: str | None = None
    deduction_mandatory_retirement: str | None = None
    deduction_voluntary_retirement: str | None = None
    deduction_retirement_loan_repayment: str | None = None
    deduction_insurance: str | None = None
    deduction_domestic_support: str | None = None
    deduction_union_dues: str | None = None
    deduction_other: str | None = None
    deduction_other_specify: str | None = None
    # Line 8's other-income lines.
    business_net_income: str | None = None
    interest_and_dividends: str | None = None
    family_support: str | None = None
    unemployment: str | None = None
    social_security: str | None = None
    other_government_assistance: str | None = None
    other_government_assistance_specify: str | None = None
    pension_or_retirement: str | None = None
    other_monthly_income: str | None = None
    other_monthly_income_specify: str | None = None
    # Line 11 — case-level on the form, carried on the debtor-1 summary.
    household_contributions: str | None = None
    household_contributions_specify: str | None = None
    # Line 13.
    change_expected: bool | None = None
    change_explanation: str | None = None


def parse_income_summary(payload: Mapping[str, object]) -> IncomeSummaryBody:
    errors: dict[str, str] = {}

    def amount(key: str) -> str | None:
        return money(payload.get(key), key, errors)

    body = IncomeSummaryBody(
        debtor_id=text(payload.get("debtor_id"), "debtor_id", errors, limit=64),
        wages=amount("wages"),
        overtime=amount("overtime"),
        deduction_tax=amount("deduction_tax"),
        deduction_mandatory_retirement=amount("deduction_mandatory_retirement"),
        deduction_voluntary_retirement=amount("deduction_voluntary_retirement"),
        deduction_retirement_loan_repayment=amount(
            "deduction_retirement_loan_repayment"
        ),
        deduction_insurance=amount("deduction_insurance"),
        deduction_domestic_support=amount("deduction_domestic_support"),
        deduction_union_dues=amount("deduction_union_dues"),
        deduction_other=amount("deduction_other"),
        deduction_other_specify=text(
            payload.get("deduction_other_specify"), "deduction_other_specify", errors
        ),
        business_net_income=amount("business_net_income"),
        interest_and_dividends=amount("interest_and_dividends"),
        family_support=amount("family_support"),
        unemployment=amount("unemployment"),
        social_security=amount("social_security"),
        other_government_assistance=amount("other_government_assistance"),
        other_government_assistance_specify=text(
            payload.get("other_government_assistance_specify"),
            "other_government_assistance_specify",
            errors,
        ),
        pension_or_retirement=amount("pension_or_retirement"),
        other_monthly_income=amount("other_monthly_income"),
        other_monthly_income_specify=text(
            payload.get("other_monthly_income_specify"),
            "other_monthly_income_specify",
            errors,
        ),
        household_contributions=amount("household_contributions"),
        household_contributions_specify=text(
            payload.get("household_contributions_specify"),
            "household_contributions_specify",
            errors,
        ),
        change_expected=boolean(
            payload.get("change_expected"), "change_expected", errors
        ),
        change_explanation=narrative(
            payload.get("change_explanation"), "change_explanation", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


INCOME_SUMMARY: EntityKind[IncomeSummaryBody] = EntityKind(
    name="income_summary",
    collection="income_summaries",
    sk_prefix="INCOME_SUMMARY",
    parse_body=parse_income_summary,
)
