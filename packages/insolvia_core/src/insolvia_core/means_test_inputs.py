"""The means test's entered figures — B122A-2's questions the case model
does not otherwise store (issue #101).

The § 707(b)(2) calculation draws on three kinds of input: the effective-
dated datasets (the UST registry series), figures DERIVABLE from case
records (CMI from the dated income history, priority and nonpriority debt
from the claims), and figures only the debtor can supply — actual monthly
tax withholding, a term-life premium, the marital adjustment, the vehicles
claimed. This module owns the third kind, one record per case, ENTERED AND
CONFIRMED like the 106I income summary: every one of these lands on a
signed form, so none of them may be a guess this system made.

Field names follow B122A-2's own line subjects (revision 04/25), not line
numbers, so a renumbering does not shift meanings — the income summary's
rule. One-per-case is not key-enforced for the usual progressive-intake
reason; the packet gate owns the cardinality, exactly as it does for the
petition.

The two embedded lists (line 3's marital adjustments, line 33d's other
secured debts) carry client-chosen row ids so provenance can address
`marital_adjustments[<id>].amount` — the notice-party rule.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import boolean, money, text, whole_number
from .provenance import ADDRESSABLE_ID_RE

# Line 11's answer set: the Local Standards publish columns for one and two
# vehicles only, so "2" means "2 or more", as the form itself prints.
MAX_CLAIMED_VEHICLES: Final = 2


@dataclass(frozen=True)
class MaritalAdjustmentItem:
    """One line-3 row: a part of the non-filing spouse's income not paid for
    the household, listed separately as the form requires."""

    id: str
    description: str | None = None
    amount: str | None = None


@dataclass(frozen=True)
class OtherSecuredPayment:
    """One line-33d row: a secured debt beyond the home and the two claimed
    vehicles, as its average monthly payment over the next 60 months."""

    id: str
    creditor_name: str | None = None
    property_description: str | None = None
    monthly_payment: str | None = None


@dataclass(frozen=True)
class MeansTestInputBody:
    """B122A-2's entered figures, named for their subjects. Every money
    member is a monthly amount unless the name says otherwise."""

    # Line 5/7: the household composition the deductions use. Line 5 is the
    # sum; the split feeds line 7's per-person health care bands.
    people_under_65: int | None = None
    people_65_or_older: int | None = None
    # Line 3.
    marital_adjustments: tuple[MaritalAdjustmentItem, ...] = ()
    # Line 9b: average monthly payment for all debts secured by the home.
    home_secured_monthly_total: str | None = None
    # Line 10: the contention that the UST's housing split is wrong.
    housing_adjustment_amount: str | None = None
    housing_adjustment_explanation: str | None = None
    # Lines 11-13.
    vehicle_count: int | None = None
    vehicle_1_loan_monthly: str | None = None
    vehicle_2_loan_monthly: str | None = None
    # Line 15.
    additional_public_transportation: str | None = None
    # Lines 16-23: the Other Necessary Expenses.
    taxes: str | None = None
    involuntary_deductions: str | None = None
    term_life_insurance: str | None = None
    court_ordered_payments: str | None = None
    education_for_employment_or_disability: str | None = None
    childcare: str | None = None
    healthcare_above_allowance: str | None = None
    optional_telecom: str | None = None
    # Lines 25-31: the Additional Expense Deductions.
    health_insurance: str | None = None
    disability_insurance: str | None = None
    health_savings_account: str | None = None
    family_care_contributions: str | None = None
    family_violence_protection: str | None = None
    home_energy_excess: str | None = None
    education_under_18: str | None = None
    additional_food_clothing: str | None = None
    charitable_contributions: str | None = None
    # Line 33d.
    other_secured_payments: tuple[OtherSecuredPayment, ...] = ()
    # Line 34: the total amount past due on line-33 debts that are necessary
    # for support (primary residence, vehicle, other support property).
    priority_cure_total: str | None = None
    # Line 36.
    ch13_eligible: bool | None = None
    ch13_projected_plan_payment: str | None = None


def _parse_marital_adjustments(
    value: object, errors: dict[str, str]
) -> tuple[MaritalAdjustmentItem, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["marital_adjustments"] = "Must be a list."
        return ()
    items: list[MaritalAdjustmentItem] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"marital_adjustments[{index}]"
        if not isinstance(raw, Mapping):
            errors[path] = "Must be an object."
            continue
        given_id = raw.get("id")
        if not isinstance(given_id, str) or not ADDRESSABLE_ID_RE.match(given_id):
            errors[f"{path}.id"] = (
                "Required, and must be letters, digits, hyphen or underscore — "
                "generate one per row so provenance can name it."
            )
            continue
        if given_id in seen:
            errors[f"{path}.id"] = "Duplicate id."
            continue
        seen.add(given_id)
        items.append(
            MaritalAdjustmentItem(
                id=given_id,
                description=text(raw.get("description"), f"{path}.description", errors),
                amount=money(raw.get("amount"), f"{path}.amount", errors),
            )
        )
    return tuple(items)


def _parse_other_secured(
    value: object, errors: dict[str, str]
) -> tuple[OtherSecuredPayment, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["other_secured_payments"] = "Must be a list."
        return ()
    items: list[OtherSecuredPayment] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"other_secured_payments[{index}]"
        if not isinstance(raw, Mapping):
            errors[path] = "Must be an object."
            continue
        given_id = raw.get("id")
        if not isinstance(given_id, str) or not ADDRESSABLE_ID_RE.match(given_id):
            errors[f"{path}.id"] = (
                "Required, and must be letters, digits, hyphen or underscore — "
                "generate one per row so provenance can name it."
            )
            continue
        if given_id in seen:
            errors[f"{path}.id"] = "Duplicate id."
            continue
        seen.add(given_id)
        items.append(
            OtherSecuredPayment(
                id=given_id,
                creditor_name=text(
                    raw.get("creditor_name"), f"{path}.creditor_name", errors
                ),
                property_description=text(
                    raw.get("property_description"),
                    f"{path}.property_description",
                    errors,
                ),
                monthly_payment=money(
                    raw.get("monthly_payment"), f"{path}.monthly_payment", errors
                ),
            )
        )
    return tuple(items)


def parse_means_test_input(payload: Mapping[str, object]) -> MeansTestInputBody:
    errors: dict[str, str] = {}

    def amount(key: str) -> str | None:
        return money(payload.get(key), key, errors)

    vehicle_count = whole_number(payload.get("vehicle_count"), "vehicle_count", errors)
    if vehicle_count is not None and vehicle_count > MAX_CLAIMED_VEHICLES:
        errors["vehicle_count"] = (
            "The Local Standards publish figures for at most two vehicles — "
            'the form\'s own "2 or more".'
        )
    body = MeansTestInputBody(
        people_under_65=whole_number(
            payload.get("people_under_65"), "people_under_65", errors
        ),
        people_65_or_older=whole_number(
            payload.get("people_65_or_older"), "people_65_or_older", errors
        ),
        marital_adjustments=_parse_marital_adjustments(
            payload.get("marital_adjustments"), errors
        ),
        home_secured_monthly_total=amount("home_secured_monthly_total"),
        housing_adjustment_amount=amount("housing_adjustment_amount"),
        housing_adjustment_explanation=text(
            payload.get("housing_adjustment_explanation"),
            "housing_adjustment_explanation",
            errors,
        ),
        vehicle_count=vehicle_count,
        vehicle_1_loan_monthly=amount("vehicle_1_loan_monthly"),
        vehicle_2_loan_monthly=amount("vehicle_2_loan_monthly"),
        additional_public_transportation=amount("additional_public_transportation"),
        taxes=amount("taxes"),
        involuntary_deductions=amount("involuntary_deductions"),
        term_life_insurance=amount("term_life_insurance"),
        court_ordered_payments=amount("court_ordered_payments"),
        education_for_employment_or_disability=amount(
            "education_for_employment_or_disability"
        ),
        childcare=amount("childcare"),
        healthcare_above_allowance=amount("healthcare_above_allowance"),
        optional_telecom=amount("optional_telecom"),
        health_insurance=amount("health_insurance"),
        disability_insurance=amount("disability_insurance"),
        health_savings_account=amount("health_savings_account"),
        family_care_contributions=amount("family_care_contributions"),
        family_violence_protection=amount("family_violence_protection"),
        home_energy_excess=amount("home_energy_excess"),
        education_under_18=amount("education_under_18"),
        additional_food_clothing=amount("additional_food_clothing"),
        charitable_contributions=amount("charitable_contributions"),
        other_secured_payments=_parse_other_secured(
            payload.get("other_secured_payments"), errors
        ),
        priority_cure_total=amount("priority_cure_total"),
        ch13_eligible=boolean(payload.get("ch13_eligible"), "ch13_eligible", errors),
        ch13_projected_plan_payment=amount("ch13_projected_plan_payment"),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


MEANS_TEST_INPUT: EntityKind[MeansTestInputBody] = EntityKind(
    name="means_test_input",
    collection="means_test_inputs",
    sk_prefix="MEANS_TEST_INPUT",
    parse_body=parse_means_test_input,
)
